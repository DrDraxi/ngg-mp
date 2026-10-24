using System;
using System.Collections.Generic;
using System.Globalization;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Sync;
using UnityEngine;

namespace NGGMultiplayer.Game.Progression
{
    /// <summary>
    /// Exclusive pickups and purchases (docs/GAME_INTEGRATION.md 6.7): the FPSInteractable.Interact prefix, the
    /// claim / grant / bank RPCs, and the money reports made during a granted interaction. The host decides with
    /// <see cref="ClaimArbiter"/>; clients track their claims with <see cref="ClientClaimTracker"/>.
    /// </summary>
    internal sealed class Interactions
    {
        public const string Feature = "pg.interact";
        private const string Log = "[Progression] ";
        private const double StalePickupSeconds = 15.0;
        private const double StaleCheckInterval = 5.0;
        private const double ToastInterval = 2.0;

        private readonly ProgressionModule _module;
        private readonly GameContext _ctx;
        private readonly GameAccess _access;
        private readonly ClaimArbiter _arbiter;
        private readonly ClientClaimTracker _client;
        private readonly Dictionary<uint, PendingTarget> _pendingTargets = new Dictionary<uint, PendingTarget>();
        private double _nextStaleCheck;
        private double _lastToast = double.NegativeInfinity;

        public Interactions(ProgressionModule module, GameContext ctx, GameAccess access)
        {
            _module = module;
            _ctx = ctx;
            _access = access;
            _arbiter = new ClaimArbiter(() => ctx.Now);
            _client = new ClientClaimTracker(() => ctx.Now);
        }

        public ClaimArbiter Arbiter => _arbiter;

        public PeerId TakenBy(string id) => _arbiter.TakenBy(id);

        /// <summary>
        /// How an interaction is arbitrated (<see cref="ArbitrationKinds.ForInteraction"/>, by member name): the LMUGC
        /// ticket costs m_cashValue like the assists; GetCard has no handler in Interact (answers 92, 137, 138).
        /// </summary>
        public static ArbitrationKind KindOf(InteractionType type) => ArbitrationKinds.ForInteraction(type.ToString());

        public static bool IsArbitrated(FPSInteractable interactable) =>
            interactable != null && KindOf(interactable.m_interaction) != ArbitrationKind.None;

        public void Install()
        {
            var patcher = _ctx.Patcher;
            var interact = patcher.Method(Feature, typeof(FPSInteractable), "Interact", Type.EmptyTypes);
            if (interact != null)
            {
                patcher.Apply(Feature, interact,
                    prefix: GamePatcher.Hook(typeof(Interactions), nameof(InteractPrefix)),
                    finalizer: GamePatcher.Hook(typeof(Interactions), nameof(InteractFinalizer)));
            }
            _ctx.RegisterRpc(ProgressionRpc.Claim, OnClaim, ValidateClaimRpc);
            _ctx.RegisterRpc(ProgressionRpc.Grant, OnGrant, ProgressionModule.NoClients);
            _ctx.RegisterRpc(ProgressionRpc.Bank, OnBankReport, ValidateBankRpc);
        }

        public void Tick()
        {
            if (_ctx.IsHost)
            {
                _arbiter.Tick();
                if (_ctx.Now >= _nextStaleCheck)
                {
                    _nextStaleCheck = _ctx.Now + StaleCheckInterval;
                    CheckStalePickups();
                }
            }
            else if (_ctx.IsClient)
            {
                foreach (var id in _client.ExpirePending())
                {
                    _ctx.Info($"{Log}The host did not answer the claim for {id}.");
                    Toast("The host did not answer.");
                }
                if (_pendingTargets.Count > 0 && _client.PendingCount == 0)
                    _pendingTargets.Clear();
            }
        }

        public void OnPlayerLeft(PeerId peer) => _arbiter.OnPlayerLeft(peer);

        public void OnSceneLoaded()
        {
            // The world's objects are gone; grants and pending claims refer to them.
            _client.Clear();
            _pendingTargets.Clear();
        }

        public void Clear()
        {
            _arbiter.Clear();
            _client.Clear();
            _pendingTargets.Clear();
        }

        // ---- Bank.ChangeBalance on a client ----------------------------------------------------------

        /// <summary>
        /// Client: the local balance changed. Reported to the host only when it is the granted interaction's own
        /// change (6.7 step 4).
        /// </summary>
        public void OnClientBalanceChanged(int change)
        {
            if (_client.TryTakeReport(change, out uint claimId))
            {
                _ctx.Send(ProgressionRpc.Bank, RpcTarget.Host, w =>
                {
                    w.WriteVarUInt(claimId);
                    w.WriteVarInt(change);
                });
                return;
            }
            // A local-only change (own bounty reward, story money): the host's purse wins shortly.
            _module.ScheduleBankRefresh();
        }

        // ---- FPSInteractable.Interact ---------------------------------------------------------------

        private static bool InteractPrefix(FPSInteractable __instance, out InteractState __state)
        {
            __state = null;
            var ctx = GameContext.Current;
            var module = ProgressionModule.Instance;
            if (ctx == null || module == null || __instance == null || !ctx.Patcher.IsEnabled(Feature))
                return true;
            try
            {
                return module.Interactions.Prefix(__instance, out __state);
            }
            catch (Exception e)
            {
                ctx.Patcher.ReportFailure(Feature, e);
                __state = null;
                return true;
            }
        }

        private static void InteractFinalizer(FPSInteractable __instance, InteractState __state)
        {
            if (__state == null)
                return;
            var ctx = GameContext.Current;
            var module = ProgressionModule.Instance;
            if (ctx == null || module == null)
                return;
            try
            {
                module.Interactions.Finish(__instance, __state);
            }
            catch (Exception e)
            {
                ctx.Patcher.ReportFailure(Feature, e);
            }
        }

        private bool Prefix(FPSInteractable interactable, out InteractState state)
        {
            state = null;
            // Offline, before the host's save is in, and inside a replay scope (the authority already decided):
            // unchanged. The hold window after a replay is not a replay: a press then is the player's own.
            if (!_ctx.InSession || !_ctx.IsReady || _ctx.Replay.IsActive)
                return true;
            var kind = KindOf(interactable.m_interaction);
            if (kind == ArbitrationKind.None)
                return true;
            string id = IdOf(interactable);
            if (_ctx.IsHost)
            {
                if (!_arbiter.TryBeginHostAction(_ctx.LocalId, id, kind, _ctx.PlayerName, out string reason))
                {
                    Toast(reason);
                    return false;
                }
                state = new InteractState(InteractMode.HostAction, id, kind, 0);
                return true;
            }

            if (_client.TryConsumeGrant(id, out uint granted))
            {
                _client.OpenScope(granted, kind);
                state = new InteractState(InteractMode.GrantScope, id, kind, granted);
                return true;
            }
            if (_client.IsPending(id))
                return false;
            if (System.Text.Encoding.UTF8.GetByteCount(id) > ProgressionRpc.MaxInteractIdBytes)
            {
                _ctx.WarnOnce("pg.interact.id:" + id, $"{Log}Interactable id too long to arbitrate; it runs unshared: {id}");
                return true;
            }
            uint claimId = _client.Begin(id);
            _pendingTargets[claimId] = new PendingTarget(interactable, _ctx.SceneLoadId);
            byte type = (byte)interactable.m_interaction;
            bool sent = _ctx.Send(ProgressionRpc.Claim, RpcTarget.Host, w =>
            {
                w.WriteVarUInt(claimId);
                w.WriteString(id);
                w.WriteByte(type);
            });
            if (!sent)
            {
                _client.TryComplete(claimId, out _);
                _pendingTargets.Remove(claimId);
            }
            return false;
        }

        private void Finish(FPSInteractable interactable, InteractState state)
        {
            switch (state.Mode)
            {
                case InteractMode.GrantScope:
                    _client.CloseScope();
                    _module.RequestDiff();
                    break;
                case InteractMode.HostAction:
                    bool consumed = interactable == null || !interactable.isActiveAndEnabled;
                    _arbiter.EndHostAction(_ctx.LocalId, state.Id, state.Kind, consumed);
                    break;
            }
        }

        // ---- RPCs -----------------------------------------------------------------------------------

        private static bool ValidateClaimRpc(PeerId sender, RpcTarget target, PeerId targetPeer, NetReader args)
        {
            if (target != RpcTarget.Host)
                return false;
            args.ReadVarUInt();
            args.ReadString(ProgressionRpc.MaxInteractIdBytes);
            byte type = args.ReadByte();
            return Enum.IsDefined(typeof(InteractionType), (int)type) && KindOf((InteractionType)type) != ArbitrationKind.None;
        }

        private static bool ValidateBankRpc(PeerId sender, RpcTarget target, PeerId targetPeer, NetReader args)
        {
            if (target != RpcTarget.Host)
                return false;
            args.ReadVarUInt();
            args.ReadVarInt();
            return args.IsAtEnd;
        }

        /// <summary>Host: a client wants to pick up or buy something.</summary>
        private void OnClaim(PeerId sender, NetReader reader)
        {
            uint claimId = reader.ReadVarUInt();
            string id = reader.ReadString(ProgressionRpc.MaxInteractIdBytes);
            var type = (InteractionType)reader.ReadByte();
            if (!_ctx.IsHost || sender == _ctx.LocalId)
                return;
            var kind = KindOf(type);
            var interactable = _module.Live.InWorld && !_ctx.IsLoadingWorld ? FindInteractable(id, type) : null;
            bool available = interactable != null && interactable.isActiveAndEnabled;
            var bank = _access.Bank();
            int balance = bank != null ? bank.m_balance : 0;
            int price = kind == ArbitrationKind.Purchase && interactable != null ? Math.Max(0, interactable.m_cashValue) : 0;
            bool granted = _arbiter.TryGrant(sender, claimId, id, kind, available, balance, price, _ctx.PlayerName, out string reason);
            _ctx.Send(ProgressionRpc.Grant, RpcTarget.Peer, w =>
            {
                w.WriteVarUInt(claimId);
                w.WriteBool(granted);
                w.WriteVarInt(balance);
                w.WriteString(Truncate(reason, ProgressionRpc.MaxReasonBytes));
            }, Reliability.Reliable, sender);
            _ctx.Info($"{Log}{(granted ? "Granted" : "Refused")} {type} {id} to {_ctx.PlayerName(sender)}{(granted ? "" : ": " + reason)}");
        }

        /// <summary>Client: the host answered a claim.</summary>
        private void OnGrant(PeerId sender, NetReader reader)
        {
            uint claimId = reader.ReadVarUInt();
            bool granted = reader.ReadBool();
            int hostBalance = reader.ReadVarInt();
            string reason = reader.ReadString(ProgressionRpc.MaxReasonBytes);
            if (!_ctx.IsClient || sender != _ctx.HostId)
                return;
            if (!_client.TryComplete(claimId, out string id))
                return;
            _pendingTargets.TryGetValue(claimId, out var target);
            _pendingTargets.Remove(claimId);
            // Purchases check the price against the local balance: make it the host's first.
            if (_module.Live.InWorld)
                _module.Applier.ApplyBank(FactTable.EncodeInt(hostBalance));
            if (!granted)
            {
                Toast(string.IsNullOrEmpty(reason) ? "The host refused that." : reason);
                return;
            }
            _client.AddGrant(id, claimId);
            var interactable = target.Interactable;
            int sceneId = target.SceneLoadId;
            _ctx.Post(() =>
            {
                if (interactable == null || sceneId != _ctx.SceneLoadId || !_ctx.IsClient)
                    return;
                try
                {
                    interactable.Interact();
                }
                catch (Exception e)
                {
                    _ctx.Patcher.ReportFailure(Feature, e);
                }
            });
        }

        /// <summary>Host: a client's money change under a granted claim.</summary>
        private void OnBankReport(PeerId sender, NetReader reader)
        {
            uint claimId = reader.ReadVarUInt();
            int change = reader.ReadVarInt();
            if (!_ctx.IsHost || sender == _ctx.LocalId)
                return;
            var bank = _access.Bank();
            if (bank == null)
                return;
            // The host's own value: CollectCash pays its m_cashValue, a purchase costs it (answers 35, 138).
            int hostValue = 0;
            var kind = _arbiter.GrantKind(sender, claimId);
            string targetId = _arbiter.GrantTarget(sender, claimId);
            FPSInteractable target = null;
            if (kind == ArbitrationKind.CollectCash || kind == ArbitrationKind.Purchase)
            {
                target = FindInteractable(targetId, null);
                hostValue = target != null && KindOf(target.m_interaction) == kind ? Math.Max(0, target.m_cashValue) : 0;
            }
            if (!_arbiter.TryAcceptBankReport(sender, claimId, change, bank.m_balance, hostValue, out int apply, out string reason))
            {
                _ctx.Warn($"{Log}Ignored a money report of {change} from {_ctx.PlayerName(sender)}: {reason}");
                return;
            }
            if (kind == ArbitrationKind.Purchase && target != null && ArbitrationKinds.IsOneOffPurchase(target.m_interaction.ToString()))
                _arbiter.KeepTaken(targetId);
            using (_ctx.Replay.Begin("pg.bank.report"))
                bank.ChangeBalance(apply, false, true);
            _module.RequestDiff();
            _module.ScheduleHostSave();
        }

        // ---- Helpers --------------------------------------------------------------------------------

        /// <summary>
        /// The arbitration id of one interactable: the id of its object (<see cref="GameIds.ForObject"/>, usually the
        /// nearest SaveableObject), plus "|n" when several arbitrated interactables share that id (a shop's stands under
        /// one saveable root), n being its place among them in hierarchy order. Identical on every peer of one build.
        /// </summary>
        public static string IdOf(FPSInteractable interactable)
        {
            if (interactable == null)
                return string.Empty;
            string baseId = GameIds.ForObject(interactable);
            var group = Group(OwnerOf(interactable.transform), baseId);
            int index = group.IndexOf(interactable);
            return group.Count <= 1 || index < 0 ? baseId : baseId + IndexSeparator + index.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>The interactable an id (from <see cref="IdOf"/>) names, if it is of <paramref name="type"/> (when given).</summary>
        private static FPSInteractable FindInteractable(string id, InteractionType? type)
        {
            if (string.IsNullOrEmpty(id))
                return null;
            string baseId = id;
            int index = 0;
            bool indexed = false;
            int separator = id.LastIndexOf(IndexSeparator);
            if (separator > 0 && int.TryParse(id.Substring(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int parsed))
            {
                baseId = id.Substring(0, separator);
                index = parsed;
                indexed = true;
            }
            var go = GameIds.Resolve(baseId);
            if (go == null)
                return null;
            var group = Group(go.transform, baseId);
            // An id without an index names a lone interactable; with one, one of several.
            if (indexed != group.Count > 1 || index >= group.Count)
                return null;
            var found = group[index];
            return !type.HasValue || found.m_interaction == type.Value ? found : null;
        }

        /// <summary>The ids of arbitrated interactables on or under <paramref name="saveable"/>.</summary>
        public static List<string> ArbitratedIds(SaveableObject saveable)
        {
            var ids = new List<string>();
            if (saveable == null)
                return ids;
            foreach (var interactable in saveable.GetComponentsInChildren<FPSInteractable>(true))
            {
                if (IsArbitrated(interactable))
                {
                    string id = IdOf(interactable);
                    if (!ids.Contains(id))
                        ids.Add(id);
                }
            }
            return ids;
        }

        private const char IndexSeparator = '|';

        // The object a base id names: the nearest SaveableObject with an id (as GameIds.ForObject climbs), else itself.
        private static Transform OwnerOf(Transform transform)
        {
            for (var t = transform; t != null; t = t.parent)
            {
                var saveable = t.GetComponent<SaveableObject>();
                if (saveable != null && !string.IsNullOrEmpty(saveable.m_id))
                    return t;
            }
            return transform;
        }

        // Every arbitrated interactable under owner whose object id is baseId, in hierarchy order.
        private static List<FPSInteractable> Group(Transform owner, string baseId)
        {
            var group = new List<FPSInteractable>();
            if (owner == null)
                return group;
            foreach (var candidate in owner.GetComponentsInChildren<FPSInteractable>(true))
            {
                if (IsArbitrated(candidate) && GameIds.ForObject(candidate) == baseId)
                    group.Add(candidate);
            }
            return group;
        }

        private void CheckStalePickups()
        {
            foreach (var id in _arbiter.TakenPickupsOlderThan(StalePickupSeconds))
            {
                var interactable = _module.Live.InWorld ? FindInteractable(id, null) : null;
                bool stillThere = interactable != null && interactable.isActiveAndEnabled;
                if (stillThere)
                {
                    // Granted but never picked up (the claimant left the spot or its game refused): free it.
                    _arbiter.Release(id);
                    _ctx.Info($"{Log}Released unused pickup {id}.");
                }
                else
                {
                    _arbiter.ConfirmTaken(id);
                }
            }
        }

        private void Toast(string text)
        {
            if (string.IsNullOrEmpty(text) || _ctx.Now - _lastToast < ToastInterval)
                return;
            _lastToast = _ctx.Now;
            _ctx.Notify(text);
        }

        private static string Truncate(string text, int maxBytes)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
            // Reasons are short ASCII-ish texts; cut by characters until they fit.
            while (System.Text.Encoding.UTF8.GetByteCount(text) > maxBytes)
                text = text.Substring(0, text.Length - 1);
            return text;
        }

        private enum InteractMode : byte
        {
            HostAction,
            GrantScope
        }

        private sealed class InteractState
        {
            public InteractState(InteractMode mode, string id, ArbitrationKind kind, uint claimId)
            {
                Mode = mode;
                Id = id;
                Kind = kind;
                ClaimId = claimId;
            }

            public readonly InteractMode Mode;
            public readonly string Id;
            public readonly ArbitrationKind Kind;
            public readonly uint ClaimId;
        }

        private readonly struct PendingTarget
        {
            public PendingTarget(FPSInteractable interactable, int sceneLoadId)
            {
                Interactable = interactable;
                SceneLoadId = sceneLoadId;
            }

            public readonly FPSInteractable Interactable;
            public readonly int SceneLoadId;
        }
    }
}
