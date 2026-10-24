using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Sync;
using UnityEngine;

namespace NGGMultiplayer.Game.Progression
{
    /// <summary>
    /// Presentation events and story beats (docs/GAME_INTEGRATION.md 6.10, 6.11, 6.14): world ops (doors, gates,
    /// server, bunker, shop stock) and replicate-class DialogueManager methods are sent to the others and replayed
    /// there; DeductMoney / ReturnMoney run on the host only; endings are the host's. The persistent results travel
    /// as facts, so a missed event never loses state. Calls that are not idempotent (StartWind, the shop stock) run at
    /// most once per world when a replay causes them (<see cref="RepeatRules"/>).
    /// </summary>
    internal sealed class WorldEvents
    {
        public const string MoneyFeature = "pg.money";
        public const string EndingFeature = "pg.ending";
        public const string RepeatFeature = "pg.repeat";
        private const string ModObjectPrefix = "NGGMP ";
        private const string Log = "[Progression] ";
        private const double OpHoldSeconds = 0.5;
        private const double StoryHoldSeconds = 1.0;
        // After a story method was sent, its coroutines' world ops are reproduced by the receivers' replay.
        private const double StoryOpQuietSeconds = 2.0;
        private const double ToastInterval = 5.0;

        private static readonly Dictionary<string, WorldOp> ParameterlessOps = new Dictionary<string, WorldOp>(StringComparer.Ordinal)
        {
            { "ServerCut", WorldOp.ServerCut },
            { "ServerRestore", WorldOp.ServerRestore },
            { "SetDriverAvailable", WorldOp.DriverAvailable },
            { "SetWedgeAvailable", WorldOp.WedgeAvailable },
            { "SetHybridAvailable", WorldOp.HybridAvailable },
            { "SetTicketAvailable", WorldOp.TicketAvailable },
            { "EnableLMUGCMachine", WorldOp.LmugcMachine }
        };

        // Gates of the "skip" prefixes, by method: return true to run the original.
        private static readonly Dictionary<string, SkipGate> SkipGates = new Dictionary<string, SkipGate>(StringComparer.Ordinal);

        private readonly ProgressionModule _module;
        private readonly GameContext _ctx;
        private readonly GameAccess _access;
        private readonly Dictionary<string, MethodInfo> _storyMethods = new Dictionary<string, MethodInfo>(StringComparer.Ordinal);
        private readonly Dictionary<string, MethodInfo> _moneyMethods = new Dictionary<string, MethodInfo>(StringComparer.Ordinal);
        private readonly double[] _opReplayedAt = new double[WorldOps.Last + 1];
        private int _storyDepth;
        private bool _moneyApproved;
        private double _suppressOpsUntil = double.NegativeInfinity;
        private double _lastToast = double.NegativeInfinity;
        // Non-idempotent game calls (RepeatRules) that ran in the loaded world.
        private readonly HashSet<string> _ranInWorld = new HashSet<string>(StringComparer.Ordinal);
        private int _ranScene = -1;

        public WorldEvents(ProgressionModule module, GameContext ctx, GameAccess access)
        {
            _module = module;
            _ctx = ctx;
            _access = access;
            for (int i = 0; i < _opReplayedAt.Length; i++)
                _opReplayedAt[i] = double.NegativeInfinity;
        }

        /// <summary>Last time a door / gate event was sent or replayed here (door animations may still run).</summary>
        public double LastWorldEventAt { get; private set; } = double.NegativeInfinity;

        /// <summary>A world op of this kind was replayed here within <paramref name="seconds"/>.</summary>
        public bool ReplayedRecently(WorldOp op, double seconds) => _ctx.Now - _opReplayedAt[(int)op] < seconds;

        public void Install()
        {
            InstallWorldOps();
            InstallStory();
            InstallMoney();
            InstallEndings();
            InstallRepeatGates();
            _ctx.RegisterRpc(ProgressionRpc.World, OnWorld, ValidateWorld);
            _ctx.RegisterRpc(ProgressionRpc.Story, OnStory, ValidateStory);
            _ctx.RegisterRpc(ProgressionRpc.StoryHost, OnStoryHost, ValidateStoryHost);
        }

        public void Clear()
        {
            _storyDepth = 0;
            _moneyApproved = false;
            _suppressOpsUntil = double.NegativeInfinity;
            LastWorldEventAt = double.NegativeInfinity;
            // _ranInWorld stays: it describes the loaded world (keyed by its load), offline calls included.
            for (int i = 0; i < _opReplayedAt.Length; i++)
                _opReplayedAt[i] = double.NegativeInfinity;
        }

        // ---- World ops (6.11) -----------------------------------------------------------------------

        private void InstallWorldOps()
        {
            var patcher = _ctx.Patcher;
            patcher.Postfix("pg.world.gates", typeof(ObjectStates), "SetGates", new[] { typeof(string), typeof(bool) },
                typeof(WorldEvents), nameof(SetGatesPostfix));
            patcher.Postfix("pg.world.door", typeof(ObjectStates), "OpenDoor", new[] { typeof(string) },
                typeof(WorldEvents), nameof(OpenDoorPostfix));
            patcher.Postfix("pg.world.sliding", typeof(ObjectStates), "OpenOrCloseSlidingDoor", new[] { typeof(bool), typeof(string) },
                typeof(WorldEvents), nameof(SlidingDoorPostfix));
            patcher.Postfix("pg.world.bunker", typeof(BunkerTerminal), "SetScreenOpen", Type.EmptyTypes,
                typeof(WorldEvents), nameof(BunkerPostfix));
            foreach (var pair in ParameterlessOps)
            {
                patcher.Postfix("pg.world." + pair.Key, typeof(ObjectStates), pair.Key, Type.EmptyTypes,
                    typeof(WorldEvents), nameof(ParameterlessOpPostfix));
            }
        }

        private static void SetGatesPostfix(string __0, bool __1) => SendOp("pg.world.gates", WorldOp.SetGates, __0, __1);

        private static void OpenDoorPostfix(string __0) => SendOp("pg.world.door", WorldOp.OpenDoor, __0, false);

        private static void SlidingDoorPostfix(bool __0, string __1) => SendOp("pg.world.sliding", WorldOp.SlidingDoor, __1, __0);

        private static void BunkerPostfix(BunkerTerminal __instance) =>
            SendOp("pg.world.bunker", WorldOp.BunkerScreen, __instance != null ? GameIds.ForObject(__instance) : string.Empty, false);

        private static void ParameterlessOpPostfix(MethodBase __originalMethod)
        {
            string name = __originalMethod?.Name;
            if (name != null && ParameterlessOps.TryGetValue(name, out var op))
                SendOp("pg.world." + name, op, string.Empty, false);
        }

        private static void SendOp(string feature, WorldOp op, string id, bool flag)
        {
            var ctx = GameContext.Current;
            var module = ProgressionModule.Instance;
            if (ctx == null || module == null || !ctx.Patcher.IsEnabled(feature))
                return;
            try
            {
                module.World.TrySendOp(op, id, flag);
            }
            catch (Exception e)
            {
                ctx.Patcher.ReportFailure(feature, e);
            }
        }

        private void TrySendOp(WorldOp op, string id, bool flag)
        {
            if (!_ctx.InSession || !_ctx.IsReady || !_module.Live.InWorld || _ctx.IsLoadingWorld || _ctx.Replay.InReplayEffects)
                return;
            // Inside a replicate-class story method the receivers' replay of the method reproduces its world ops.
            if (_storyDepth > 0 || _ctx.Now < _suppressOpsUntil)
                return;
            id = id ?? string.Empty;
            if (System.Text.Encoding.UTF8.GetByteCount(id) > ProgressionRpc.MaxWorldIdBytes)
            {
                _ctx.WarnOnce("pg.world.id:" + id, $"{Log}World op id too long to send: {id}");
                return;
            }
            LastWorldEventAt = _ctx.Now;
            byte code = (byte)op;
            _ctx.Send(ProgressionRpc.World, RpcTarget.Others, w =>
            {
                w.WriteByte(code);
                w.WriteString(id);
                w.WriteBool(flag);
            });
        }

        private static bool ValidateWorld(PeerId sender, RpcTarget target, PeerId targetPeer, NetReader args)
        {
            if (target != RpcTarget.Others)
                return false;
            byte op = args.ReadByte();
            args.ReadString(ProgressionRpc.MaxWorldIdBytes);
            args.ReadBool();
            return args.IsAtEnd && WorldOps.IsKnown(op);
        }

        private void OnWorld(PeerId sender, NetReader reader)
        {
            byte code = reader.ReadByte();
            string id = reader.ReadString(ProgressionRpc.MaxWorldIdBytes);
            bool flag = reader.ReadBool();
            if (sender == _ctx.LocalId || !WorldOps.IsKnown(code))
                return;
            // Not in the world (loading, menus): the facts bring the result once it is.
            if (!_module.Live.InWorld || _ctx.IsLoadingWorld)
                return;
            var states = _access.ObjectStates();
            var op = (WorldOp)code;
            if (states == null && op != WorldOp.BunkerScreen)
                return;
            ref RunSave run = ref SaveManager.instance.m_run;
            using (_ctx.Replay.Begin("pg.world"))
            {
                switch (op)
                {
                    case WorldOp.SetGates:
                        states.SetGates(id, flag);
                        break;
                    case WorldOp.OpenDoor:
                        states.OpenDoor(id);
                        break;
                    case WorldOp.SlidingDoor:
                        if (flag && states.CheckRFIDDoorAlreadyOpen(id))
                            break;
                        states.OpenOrCloseSlidingDoor(flag, id);
                        break;
                    case WorldOp.ServerCut:
                        if (!run.m_serverCut)
                            states.ServerCut();
                        break;
                    case WorldOp.ServerRestore:
                        if (run.m_serverCut)
                            states.ServerRestore();
                        break;
                    case WorldOp.BunkerScreen:
                        if (run.m_bunkerOpen)
                            break;
                        var terminal = GameIds.Resolve<BunkerTerminal>(id);
                        if (terminal == null && states != null)
                            terminal = states.m_bunkerTerminal;
                        if (terminal != null)
                            terminal.SetScreenOpen();
                        break;
                    case WorldOp.DriverAvailable:
                        states.SetDriverAvailable();
                        break;
                    case WorldOp.WedgeAvailable:
                        states.SetWedgeAvailable();
                        break;
                    case WorldOp.HybridAvailable:
                        states.SetHybridAvailable();
                        break;
                    case WorldOp.TicketAvailable:
                        states.SetTicketAvailable();
                        break;
                    case WorldOp.LmugcMachine:
                        states.EnableLMUGCMachine();
                        break;
                }
            }
            _ctx.Replay.HoldFor(OpHoldSeconds);
            _opReplayedAt[code] = _ctx.Now;
            LastWorldEventAt = _ctx.Now;
        }

        // ---- Story methods (6.10) -------------------------------------------------------------------

        private void InstallStory()
        {
            // Only the replicate class is sent by name (answers 38, 96): Luke's clips, LookAt* and the other
            // presentation methods stay with the player in the dialogue; UnlockWind / UnlockTeleporter are not safe
            // to run twice and arrive as facts and world ops; the player's own teleports and tees stay local.
            var names = new List<string>(StoryMethods.Replicate);
            var prefix = GamePatcher.Hook(typeof(WorldEvents), nameof(StoryPrefix));
            var postfix = GamePatcher.Hook(typeof(WorldEvents), nameof(StoryPostfix));
            var finalizer = GamePatcher.Hook(typeof(WorldEvents), nameof(StoryFinalizer));
            foreach (var name in names)
            {
                string feature = "pg.story." + name;
                var method = _ctx.Patcher.Method(feature, typeof(DialogueManager), name, Type.EmptyTypes);
                if (method != null && _ctx.Patcher.Apply(feature, method, prefix, postfix, finalizer))
                    _storyMethods[name] = method;
            }
            _ctx.Info($"{Log}Replicating {_storyMethods.Count} story methods.");
        }

        private static void StoryPrefix()
        {
            var module = ProgressionModule.Instance;
            if (module != null)
                module.World._storyDepth++;
        }

        private static void StoryPostfix(MethodBase __originalMethod)
        {
            var ctx = GameContext.Current;
            var module = ProgressionModule.Instance;
            if (ctx == null || module == null || __originalMethod == null)
                return;
            string feature = "pg.story." + __originalMethod.Name;
            if (!ctx.Patcher.IsEnabled(feature))
                return;
            try
            {
                module.World.TrySendStory(__originalMethod.Name);
            }
            catch (Exception e)
            {
                ctx.Patcher.ReportFailure(feature, e);
            }
        }

        private static void StoryFinalizer()
        {
            var module = ProgressionModule.Instance;
            if (module != null && module.World._storyDepth > 0)
                module.World._storyDepth--;
        }

        private void TrySendStory(string name)
        {
            // Only the outermost story method is sent; its replay reproduces the nested ones.
            if (_storyDepth != 1)
                return;
            if (!_ctx.InSession || !_ctx.IsReady || !_module.Live.InWorld || _ctx.IsLoadingWorld || _ctx.Replay.InReplayEffects)
                return;
            if (IsChallengeDriven(name))
                return;
            _suppressOpsUntil = _ctx.Now + StoryOpQuietSeconds;
            _ctx.Send(ProgressionRpc.Story, RpcTarget.Others, w => w.WriteString(name));
        }

        /// <summary>
        /// The method is a functionOnStart / functionOnComplete of the shared main track, which every peer replays
        /// itself (6.3), so it is not sent by name.
        /// </summary>
        private bool IsChallengeDriven(string name)
        {
            var system = ChallengeSystem.instance;
            if (system == null || system.m_mainChallengeSet == null || system.m_challengeSet != system.m_mainChallengeSet)
                return false;
            var challenges = _access.Challenges(system.m_mainChallengeSet);
            if (challenges == null)
                return false;
            foreach (var challenge in challenges)
            {
                if (_access.UsesFunction(challenge, name))
                    return true;
            }
            return false;
        }

        private bool ValidateStory(PeerId sender, RpcTarget target, PeerId targetPeer, NetReader args)
        {
            if (target != RpcTarget.Others)
                return false;
            string name = args.ReadString(ProgressionRpc.MaxMethodBytes);
            return args.IsAtEnd && StoryMethods.Classify(name) == StoryClass.Replicate && _storyMethods.ContainsKey(name);
        }

        private void OnStory(PeerId sender, NetReader reader)
        {
            string name = reader.ReadString(ProgressionRpc.MaxMethodBytes);
            if (sender == _ctx.LocalId || StoryMethods.Classify(name) != StoryClass.Replicate)
                return;
            if (!_module.Live.InWorld || _ctx.IsLoadingWorld || !_storyMethods.ContainsKey(name))
                return;
            // Not into the local player's own dialogue, cutscene or shot: it waits until they are free (6.15).
            if (_module.Cutscenes.HoldStory(name, sender))
                return;
            ReplayStory(name, sender);
        }

        /// <summary>Runs a replicate-class story beat another player sent (now, or after the local player was busy).</summary>
        public void ReplayStory(string name, PeerId sender)
        {
            if (!_module.Live.InWorld || _ctx.IsLoadingWorld || !_storyMethods.TryGetValue(name, out var method))
                return;
            var dialogue = DialogueManager.instance;
            if (dialogue == null)
                return;
            using (_ctx.Replay.Begin("pg.story " + name))
                Invoke(dialogue, method);
            // Its coroutines open doors and unlock things later; those are the replay's, not this player's.
            _ctx.Replay.HoldEffectsFor(StoryHoldSeconds);
            LastWorldEventAt = _ctx.Now;
            _ctx.Info($"{Log}Replayed story beat {name} from {_ctx.PlayerName(sender)}.");
        }

        private static void Invoke(DialogueManager dialogue, MethodInfo method)
        {
            object result;
            try
            {
                result = method.Invoke(dialogue, null);
            }
            catch (TargetInvocationException e)
            {
                throw e.InnerException ?? e;
            }
            if (result is IEnumerator routine)
                dialogue.StartCoroutine(routine);
        }

        // ---- Host-executed money (6.10) -------------------------------------------------------------

        private void InstallMoney()
        {
            foreach (var name in new[] { StoryMethods.DeductMoney, StoryMethods.ReturnMoney })
            {
                var method = _ctx.Patcher.Method(MoneyFeature, typeof(DialogueManager), name, Type.EmptyTypes);
                if (method != null && ApplySkip(MoneyFeature, method, MoneyGateRun))
                    _moneyMethods[name] = method;
            }
        }

        private bool MoneyGateRun(MethodBase method)
        {
            if (!_ctx.InSession)
                return true;
            string name = method.Name;
            if (_ctx.IsClient)
            {
                // The purse is the host's: ask it, never change money here.
                if (_ctx.IsReady)
                    _ctx.Send(ProgressionRpc.StoryHost, RpcTarget.Host, w => w.WriteString(name));
                return false;
            }
            if (_moneyApproved)
                return true;
            bool run = _module.TryAdvanceMoney(name);
            if (!run)
                _ctx.Info($"{Log}Skipped {name}: the session already did it.");
            return run;
        }

        private static bool ValidateStoryHost(PeerId sender, RpcTarget target, PeerId targetPeer, NetReader args)
        {
            if (target != RpcTarget.Host)
                return false;
            string name = args.ReadString(ProgressionRpc.MaxMethodBytes);
            return args.IsAtEnd && StoryMethods.Classify(name) == StoryClass.HostExecuted;
        }

        private void OnStoryHost(PeerId sender, NetReader reader)
        {
            string name = reader.ReadString(ProgressionRpc.MaxMethodBytes);
            if (!_ctx.IsHost || sender == _ctx.LocalId || !_moneyMethods.TryGetValue(name, out var method))
                return;
            var dialogue = DialogueManager.instance;
            if (dialogue == null || !_module.Live.InWorld)
                return;
            if (!_module.TryAdvanceMoney(name))
            {
                _ctx.Info($"{Log}Ignored {name} from {_ctx.PlayerName(sender)}: the session already did it.");
                return;
            }
            _moneyApproved = true;
            try
            {
                using (_ctx.Replay.Begin("pg.money " + name))
                    Invoke(dialogue, method);
            }
            finally
            {
                _moneyApproved = false;
            }
            _module.RequestDiff();
            // The client's story money is the host's save's now; saved like the game saves its own.
            _module.ScheduleHostSave();
        }

        // ---- Endings (6.14) -------------------------------------------------------------------------

        /// <summary>
        /// The ending entry points (answers 23, 38, 97, 120): DialogueManager.TryTrueEnding, the true ending's
        /// EndLukeFadeDown -> LukeVideo.LukeLeaveSequence (TRUEENDING, timeScale 1, FadeDownEnding), hitting Luke
        /// (LukeVideo.OnTriggerEnter -> LukeHitSequence, SaveRun with m_hitLuke, CREDITS) and
        /// TeleportManager.FadeDownEnding (no static instance; patched by type). Clients skip them all.
        /// </summary>
        private void InstallEndings()
        {
            ApplySkip(EndingFeature + ".trueEnding",
                _ctx.Patcher.Method(EndingFeature + ".trueEnding", typeof(DialogueManager), StoryMethods.TryTrueEnding, Type.EmptyTypes),
                HostOnlyEnding);
            ApplySkip(EndingFeature + ".lukeFadeDown",
                _ctx.Patcher.Method(EndingFeature + ".lukeFadeDown", typeof(DialogueManager), StoryMethods.EndLukeFadeDown, Type.EmptyTypes),
                HostOnlyEnding);
            ApplySkip(EndingFeature + ".lukeLeave",
                _ctx.Patcher.Method(EndingFeature + ".lukeLeave", typeof(LukeVideo), "LukeLeaveSequence", Type.EmptyTypes),
                HostOnlyEnding);
            _ctx.Patcher.Prefix(EndingFeature + ".luke", typeof(LukeVideo), "OnTriggerEnter", new[] { typeof(Collider) },
                typeof(WorldEvents), nameof(LukeTriggerPrefix));
            ApplySkip(EndingFeature + ".fade",
                _ctx.Patcher.Method(EndingFeature + ".fade", typeof(TeleportManager), "FadeDownEnding", Type.EmptyTypes),
                HostOnlyEnding);
        }

        // LukeVideo.OnTriggerEnter takes any collider (answers 97): on the host, never one of the mod's own objects.
        private static bool LukeTriggerPrefix(Collider __0)
        {
            var ctx = GameContext.Current;
            var module = ProgressionModule.Instance;
            const string feature = EndingFeature + ".luke";
            if (ctx == null || module == null || !ctx.Patcher.IsEnabled(feature))
                return true;
            try
            {
                if (!ctx.InSession)
                    return true;
                if (ctx.IsClient)
                    return module.World.HostOnlyEnding(null);
                if (__0 != null && IsModObject(__0.transform))
                {
                    ctx.WarnOnce("pg.luke.modCollider", $"{Log}A mod object ({__0.name}) entered Luke's trigger; ignored.");
                    return false;
                }
            }
            catch (Exception e)
            {
                ctx.Patcher.ReportFailure(feature, e);
            }
            return true;
        }

        private static bool IsModObject(Transform t)
        {
            for (; t != null; t = t.parent)
            {
                if (t.name.StartsWith(ModObjectPrefix, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        // ---- Shop stock persistence (docs/confirm answers 41, 123: runtime check) ----------------------

        private static readonly string[] ShopObjects =
        {
            "m_driver", "m_driverStandee", "m_wedge", "m_wedgeStandee", "m_hybrid", "m_hybridStandee", "m_ticket",
            "m_lmugcMachine", "m_assistStandees", "m_assists"
        };

        /// <summary>
        /// Logs once which of ObjectStates' shop objects persist as SaveableObjects (then their <c>obj</c> facts bring
        /// the stock to late joiners); SetUpScene has no shop logic, so one that is not saveable only follows the
        /// world ops 7..11 while both players are in the world.
        /// </summary>
        public void NoteShopObjects()
        {
            var states = _access.ObjectStates();
            if (states == null)
                return;
            var saveable = new List<string>();
            var notSaveable = new List<string>();
            foreach (var field in ShopObjects)
            {
                var value = Traverse.Create(states).Field(field);
                if (!value.FieldExists())
                    continue;
                var go = value.GetValue() as GameObject;
                if (go == null)
                    continue;
                var data = go.GetComponentInParent<SaveableObject>(true);
                (data != null && !string.IsNullOrEmpty(data.m_id) ? saveable : notSaveable).Add(field);
            }
            _module.InfoOnce("pg.shop.saveable", $"{Log}Shop stock objects that are SaveableObjects: [{string.Join(", ", saveable)}]; not: [{string.Join(", ", notSaveable)}].");
        }

        // ---- Repeat gates (docs/confirm answers 44, 95) -----------------------------------------------

        private void InstallRepeatGates()
        {
            ApplySkip(RepeatFeature + "." + RepeatRules.StartWind,
                _ctx.Patcher.Method(RepeatFeature + "." + RepeatRules.StartWind, typeof(WindPanel), RepeatRules.StartWind, new[] { typeof(bool) }),
                RepeatGate);
            foreach (var name in RepeatRules.ShopStock)
            {
                string feature = RepeatFeature + "." + name;
                ApplySkip(feature, _ctx.Patcher.Method(feature, typeof(ObjectStates), name, Type.EmptyTypes), RepeatGate);
            }
        }

        /// <summary>
        /// StartWind starts one more WindCycle (and re-rolls the wind) on every call; the shop stock methods spawn
        /// PFX and a sound on every call. One the network caused runs only if it did not run in this world yet.
        /// </summary>
        private bool RepeatGate(MethodBase method)
        {
            if (_ranScene != _ctx.SceneLoadId)
            {
                _ranInWorld.Clear();
                _ranScene = _ctx.SceneLoadId;
            }
            string name = method.Name;
            bool ran = _ranInWorld.Contains(name);
            if (RepeatRules.SkipRepeat(_ctx.InSession, _ctx.Replay.InReplayEffects, ran))
            {
                _ctx.Info($"{Log}Skipped a replayed {method.DeclaringType?.Name}.{name}: it already ran in this world.");
                return false;
            }
            _ranInWorld.Add(name);
            return true;
        }

        private bool HostOnlyEnding(MethodBase method)
        {
            if (!_ctx.IsClient)
                return true;
            if (_ctx.Now - _lastToast >= ToastInterval)
            {
                _lastToast = _ctx.Now;
                _ctx.Notify("Only the host can start the ending.");
            }
            return false;
        }

        // ---- Skip prefixes for any return type ------------------------------------------------------

        private bool ApplySkip(string feature, MethodInfo method, Func<MethodBase, bool> runOriginal)
        {
            if (method == null)
                return false;
            string hook;
            if (method.ReturnType == typeof(void))
                hook = nameof(SkipVoidPrefix);
            else if (method.ReturnType == typeof(bool))
                hook = nameof(SkipBoolPrefix);
            else if (method.ReturnType == typeof(IEnumerator))
                hook = nameof(SkipEnumeratorPrefix);
            else
            {
                _ctx.Patcher.Disable(feature, $"{method.DeclaringType?.Name}.{method.Name} returns {method.ReturnType.Name}; cannot skip it safely");
                return false;
            }
            SkipGates[KeyOf(method)] = new SkipGate(feature, runOriginal);
            return _ctx.Patcher.Prefix(feature, method, typeof(WorldEvents), hook);
        }

        private static string KeyOf(MethodBase method) => method.DeclaringType?.FullName + "::" + method.Name;

        private static bool RunOriginal(MethodBase method)
        {
            var ctx = GameContext.Current;
            if (ctx == null || method == null || ProgressionModule.Instance == null)
                return true;
            if (!SkipGates.TryGetValue(KeyOf(method), out var gate) || !ctx.Patcher.IsEnabled(gate.Feature))
                return true;
            try
            {
                return gate.Run(method);
            }
            catch (Exception e)
            {
                ctx.Patcher.ReportFailure(gate.Feature, e);
                return true;
            }
        }

        private static bool SkipVoidPrefix(MethodBase __originalMethod) => RunOriginal(__originalMethod);

        private static bool SkipBoolPrefix(MethodBase __originalMethod, ref bool __result)
        {
            if (RunOriginal(__originalMethod))
                return true;
            __result = false;
            return false;
        }

        private static bool SkipEnumeratorPrefix(MethodBase __originalMethod, ref IEnumerator __result)
        {
            if (RunOriginal(__originalMethod))
                return true;
            __result = Empty();
            return false;
        }

        private static IEnumerator Empty()
        {
            yield break;
        }

        private readonly struct SkipGate
        {
            public SkipGate(string feature, Func<MethodBase, bool> run)
            {
                Feature = feature;
                Run = run;
            }

            public readonly string Feature;
            public readonly Func<MethodBase, bool> Run;
        }
    }
}
