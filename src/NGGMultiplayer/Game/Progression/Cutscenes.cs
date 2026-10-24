using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using NGGMultiplayer.Core.Net;

namespace NGGMultiplayer.Game.Progression
{
    /// <summary>
    /// Another player's story never runs into the local player's own dialogue, cutscene or shot, and never makes
    /// this player's game play someone else's cutscene (docs/GAME_INTEGRATION.md 6.15):
    /// <list type="bullet">
    /// <item><b>Hold.</b> Replicated story beats (<c>ngg.pg.story</c>) and the shared main track's catch-up
    /// (CompleteChallenge / LoadNewChallengeSet, which run the challenges' functionOnComplete / functionOnStart) wait
    /// while the local player is in a dialogue or cutscene of their own (<c>m_moveLocked</c>) or playing a shot, and
    /// 1.5 s after (<see cref="LocalBusyGate"/>). Door, gate and server world ops and the other facts are not held:
    /// they only change the world.</item>
    /// <item><b>Guard.</b> A dialogue, player lock or no-text cutscene the network causes here (inside a replay
    /// scope, or inside a challenge function a track replay made the game Invoke) is skipped (never an unlock), and so are the
    /// teleports, dialogues and Luke / camera clips of the player who made the progress
    /// (<see cref="CutsceneRules.SkipWhenReplayed"/>).</item>
    /// </list>
    /// </summary>
    internal sealed class Cutscenes
    {
        public const string HoldFeature = "pg.cutscene.hold";
        public const string GuardFeature = "pg.cutscene.guard";
        public const string FunctionFeature = "pg.cutscene.fn";
        private const string FieldsFeature = "pg.cutscene.fields";
        private const string DelayFeature = "pg.cutscene.delay";
        private const string Log = "[Progression] ";
        private const double ToastInterval = 5.0;

        private readonly ProgressionModule _module;
        private readonly GameContext _ctx;
        private readonly GameAccess _access;
        private readonly LocalBusyGate _gate = new LocalBusyGate();
        private readonly StoryBeatQueue _beats = new StoryBeatQueue();
        private readonly ReplayCallMarks _marks = new ReplayCallMarks();
        private readonly HashSet<string> _guarded = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _unguarded = new HashSet<string>(StringComparer.Ordinal);
        private AccessTools.FieldRef<Challenge, string> _onStart;
        private AccessTools.FieldRef<Challenge, string> _onComplete;
        private AccessTools.FieldRef<Challenge, float> _startDelay;
        private bool _trackHeld;
        // How many replay-marked challenge functions are running right now (on the stack). Dialogues and player
        // locks the game starts while it is above 0 come from inside them; anything after they return (the player's
        // own coroutines resuming later in the same frame included) is the player's.
        private int _replayedFunctionDepth;
        private double _lastToast = double.NegativeInfinity;

        public Cutscenes(ProgressionModule module, GameContext ctx, GameAccess access)
        {
            _module = module;
            _ctx = ctx;
            _access = access;
        }

        /// <summary>Another player's story is waiting for the local player to be free.</summary>
        public bool HasHeldWork => _beats.Count > 0 || _trackHeld;

        public void Install()
        {
            var patcher = _ctx.Patcher;
            _onStart = patcher.FieldRef<Challenge, string>(FieldsFeature, "functionOnStart");
            _onComplete = patcher.FieldRef<Challenge, string>(FieldsFeature, "functionOnComplete");
            _startDelay = patcher.FieldRef<Challenge, float>(DelayFeature, "functionOnStartDelay");

            // The game's ways into a dialogue or a player lock; each overload under its own key.
            GuardDialogue("StartDialogue2", "StartDialogue", new[] { typeof(DialogueLine), typeof(float) });
            GuardDialogue("StartDialogue3", "StartDialogue", new[] { typeof(DialogueLine), typeof(float), typeof(string) });
            GuardDialogue("PlayNoTextDialogue", "PlayNoTextDialogue",
                new[] { typeof(UnityEngine.Transform), typeof(float), typeof(string), typeof(string), typeof(DialogueLine) });
            GuardDialogue("LockPlayer", "LockPlayer", new[] { typeof(bool) });
            // The known player-moving story calls are guarded up front; other challenge functions when first marked.
            foreach (var name in CutsceneRules.PlayerMovers)
                EnsureGuarded(name);
        }

        public void Clear()
        {
            _gate.Clear();
            _beats.Clear();
            _marks.Clear();
            _trackHeld = false;
            _replayedFunctionDepth = 0;
        }

        public void Tick()
        {
            if (!_ctx.InSession)
                return;
            if (Holding() || !HasHeldWork)
                return;
            if (!_module.Live.InWorld || _ctx.IsLoadingWorld || _ctx.WorldReplacePending)
                return;
            Flush();
        }

        // ---- Hold -----------------------------------------------------------------------------------

        /// <summary>
        /// A story beat from <paramref name="from"/> arrived: true when it waits (it is queued then). It also waits,
        /// behind them, while older beats are still queued, so the beats run in the order they were sent.
        /// </summary>
        public bool HoldStory(string name, PeerId from)
        {
            bool holding = Holding();
            if (!holding && _beats.Count == 0)
                return false;
            if (_beats.Add(name, from.Value))
            {
                _ctx.Info(holding
                    ? $"{Log}Holding story beat {name} from {_ctx.PlayerName(from)} until {Describe(_gate.Reason)}."
                    : $"{Log}Queued story beat {name} from {_ctx.PlayerName(from)} behind the held ones.");
            }
            return true;
        }

        /// <summary>The shared track is about to run challenge code here: true when it waits (the caller defers it).</summary>
        public bool HoldTrack()
        {
            if (!Holding())
                return false;
            if (!_trackHeld)
                _ctx.Info($"{Log}Holding the shared track's catch-up until {Describe(_gate.Reason)}.");
            _trackHeld = true;
            return true;
        }

        private bool Holding()
        {
            if (!_ctx.InSession || !_ctx.Patcher.IsEnabled(HoldFeature))
                return false;
            bool moveLocked;
            bool midShot;
            try
            {
                ReadBusy(out moveLocked, out midShot);
            }
            catch (Exception e)
            {
                _ctx.Patcher.ReportFailure(HoldFeature, e);
                return false;
            }
            bool hold = _gate.Update(_ctx.Now, moveLocked, midShot);
            if (_gate.Expired && HasHeldWork)
            {
                _module.InfoOnce("pg.cutscene.expired." + (moveLocked ? "cutscene" : "shot"),
                    $"{Log}The local player stayed {(moveLocked ? "in a dialogue / cutscene" : "mid-shot")} too long; applying the other players' story anyway.");
            }
            return hold;
        }

        private static void ReadBusy(out bool moveLocked, out bool midShot)
        {
            var controller = MoveAndHitController.instance;
            moveLocked = controller != null && controller.m_moveLocked;
            var hits = HitManager.instance;
            var ball = hits != null ? hits.m_ball : null;
            // From the swing (HitSequence sets m_ballHit) until CompleteSequence judges the rested ball.
            midShot = ball != null && ball.gameObject.activeInHierarchy && ball.m_ballHit
                && ball.m_currentShotData != null && !ball.m_currentShotData.m_shotComplete;
        }

        private void Flush()
        {
            var beats = _beats.TakeAll();
            bool track = _trackHeld;
            _trackHeld = false;
            if (beats.Count > 0)
                _ctx.Info($"{Log}The local player is free again; replaying {beats.Count} held story beat(s).");
            foreach (var beat in beats)
            {
                try
                {
                    _module.World.ReplayStory(beat.Key, new PeerId(beat.Value));
                }
                catch (Exception e)
                {
                    _ctx.ErrorOnce("pg.cutscene.flush:" + beat.Key, $"{Log}Replaying held story beat {beat.Key} failed: {e}");
                }
            }
            if (track)
                _module.ResolveDeferred();
        }

        private static string Describe(BusyReason reason) =>
            reason == BusyReason.Shot ? "the local shot is over" : "the local dialogue / cutscene is over";

        // ---- Marks: what a track replay makes the game Invoke ----------------------------------------

        /// <summary>A replay is about to complete <paramref name="challenge"/>: its functionOnComplete is the replay's.</summary>
        public void MarkCompletion(Challenge challenge)
        {
            if (challenge == null || _onComplete == null)
                return;
            Mark(_onComplete(challenge), CutsceneRules.InvokeWindowSeconds);
        }

        /// <summary>A replay is about to activate <paramref name="challenge"/>: its functionOnStart is the replay's.</summary>
        public void MarkStart(Challenge challenge)
        {
            if (challenge == null || _onStart == null)
                return;
            float delay = _startDelay != null ? _startDelay(challenge) : 0f;
            Mark(_onStart(challenge), CutsceneRules.StartWindow(delay));
        }

        /// <summary>The challenge of the main set at <paramref name="index"/> (activated next by a replay), if any.</summary>
        public void MarkStart(ChallengeSet set, int index)
        {
            var challenges = _access.Challenges(set);
            if (challenges != null && index >= 0 && index < challenges.Length)
                MarkStart(challenges[index]);
        }

        private void Mark(string method, double seconds)
        {
            if (string.IsNullOrEmpty(method) || !_ctx.Patcher.IsEnabled(FunctionFeature))
                return;
            if (!EnsureGuarded(method))
                return;
            _marks.Mark(method, _ctx.Now, seconds);
        }

        /// <summary>Patches DialogueManager.<paramref name="name"/>() once so a replay's call of it can be told apart.</summary>
        private bool EnsureGuarded(string name)
        {
            if (_guarded.Contains(name))
                return true;
            if (_unguarded.Contains(name) || !_ctx.Patcher.IsEnabled(FunctionFeature))
                return false;
            MethodInfo method = null;
            try
            {
                method = AccessTools.Method(typeof(DialogueManager), name, Type.EmptyTypes);
            }
            catch (Exception)
            {
                method = null;
            }
            if (method == null || method.ReturnType != typeof(void))
            {
                // Challenge data names it, but there is no parameterless void DialogueManager method to wrap.
                _unguarded.Add(name);
                _module.InfoOnce("pg.cutscene.fn:" + name, $"{Log}Challenge function '{name}' is {(method == null ? "not a DialogueManager method" : "not a void method")}; a replay of it is not guarded.");
                return false;
            }
            bool ok = _ctx.Patcher.Apply(FunctionFeature + "." + name, method,
                prefix: GamePatcher.Hook(typeof(Cutscenes), nameof(FunctionPrefix)),
                finalizer: GamePatcher.Hook(typeof(Cutscenes), nameof(FunctionFinalizer)));
            (ok ? _guarded : _unguarded).Add(name);
            return ok;
        }

        // ---- Patches --------------------------------------------------------------------------------

        private void GuardDialogue(string key, string name, Type[] parameters)
        {
            string feature = GuardFeature + "." + key;
            var method = _ctx.Patcher.Method(feature, typeof(DialogueManager), name, parameters);
            if (method == null)
                return;
            if (method.ReturnType != typeof(void))
            {
                _ctx.Patcher.Disable(feature, $"DialogueManager.{name} returns {method.ReturnType.Name}; cannot skip it safely");
                return;
            }
            _ctx.Patcher.Prefix(feature, method, typeof(Cutscenes), nameof(DialoguePrefix));
        }

        private static string GuardKey(MethodBase method)
        {
            if (method == null)
                return GuardFeature;
            switch (method.Name)
            {
                case "StartDialogue": return GuardFeature + ".StartDialogue" + method.GetParameters().Length;
                default: return GuardFeature + "." + method.Name;
            }
        }

        /// <summary>A dialogue, no-text cutscene or player lock the network caused here is not this player's: skip it.</summary>
        private static bool DialoguePrefix(MethodBase __originalMethod, object[] __args)
        {
            var ctx = GameContext.Current;
            var self = ProgressionModule.Instance?.Cutscenes;
            if (ctx == null || self == null)
                return true;
            string feature = GuardKey(__originalMethod);
            if (!ctx.Patcher.IsEnabled(feature))
                return true;
            try
            {
                if (CutsceneRules.NeverSkipped(__originalMethod?.Name, __args))
                    return true;
                bool inFunction = self._replayedFunctionDepth > 0;
                if (!CutsceneRules.IsReplayCaused(ctx.InSession, ctx.Replay.IsActive, inFunction))
                    return true;
                ctx.Info($"{Log}Skipped DialogueManager.{__originalMethod?.Name} caused by another player's progress ({(ctx.Replay.IsActive ? ctx.Replay.Reason : "replayed challenge function")}); the dialogue is theirs.");
                return false;
            }
            catch (Exception e)
            {
                ctx.Patcher.ReportFailure(feature, e);
                return true;
            }
        }

        /// <summary>
        /// A challenge function the game runs because of a track replay: the player-moving and presentation ones are
        /// skipped; the others run, with dialogues and player locks inside them skipped (<see cref="DialoguePrefix"/>)
        /// until <see cref="FunctionFinalizer"/> sees the call return.
        /// </summary>
        private static bool FunctionPrefix(MethodBase __originalMethod, out bool __state)
        {
            __state = false;
            var ctx = GameContext.Current;
            var self = ProgressionModule.Instance?.Cutscenes;
            string name = __originalMethod?.Name;
            if (ctx == null || self == null || name == null)
                return true;
            string feature = FunctionFeature + "." + name;
            if (!ctx.Patcher.IsEnabled(FunctionFeature) || !ctx.Patcher.IsEnabled(feature))
                return true;
            try
            {
                if (!self._marks.TryConsume(name, ctx.Now))
                    return true;
                if (CutsceneRules.SkipWhenReplayed(name))
                {
                    self.NoteSkipped(name);
                    return false;
                }
                self._replayedFunctionDepth++;
                __state = true;
            }
            catch (Exception e)
            {
                ctx.Patcher.ReportFailure(feature, e);
            }
            return true;
        }

        /// <summary>The replayed function's call is over (returned or threw): what follows is not inside it.</summary>
        private static void FunctionFinalizer(bool __state)
        {
            if (!__state)
                return;
            var self = ProgressionModule.Instance?.Cutscenes;
            if (self != null && self._replayedFunctionDepth > 0)
                self._replayedFunctionDepth--;
        }

        private void NoteSkipped(string name)
        {
            _ctx.Info($"{Log}Skipped {name}: it belongs to the player whose progress was replayed here.");
            bool mover = Array.IndexOf(CutsceneRules.PlayerMovers, name) >= 0;
            if (!mover || _ctx.Now - _lastToast < ToastInterval)
                return;
            _lastToast = _ctx.Now;
            _ctx.Notify(name.StartsWith("Teleport", StringComparison.Ordinal)
                ? "The story moved the other player on; you stay where you are."
                : "The other player got a story dialogue; it is not shown here.");
        }
    }
}
