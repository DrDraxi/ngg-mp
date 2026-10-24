using System;
using System.Collections.Generic;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Save;
using NGGMultiplayer.Core.Session;
using NGGMultiplayer.Core.Sync;

namespace NGGMultiplayer.Game.Progression
{
    /// <summary>
    /// Host-authoritative persistent progress: facts (main track, bounties, unlocks, bank, world objects), claims and
    /// arbitration, world events, story methods, the achievement guard and reconciliation. Provides the IFactHandler.
    /// Spec: docs/GAME_INTEGRATION.md sections 3, 6 and 8.
    /// <para>How it works: every second (and after each SaveRun / balance change) the live game is read into facts
    /// and compared with the last live values (<see cref="FactBaseline"/>); changes are reported to the save bridge
    /// (host: broadcast, client: claimed). Confirmed facts are applied by <see cref="FactApplier"/>; a client also
    /// reconciles its whole world against the session's table after loading, after a resync and every 10 s.</para>
    /// </summary>
    public sealed class ProgressionModule : IGameModule, IFactHandler
    {
        private const string Log = "[Progression] ";
        private const double PollInterval = 1.0;
        private const double ReconcileInterval = 10.0;
        private const double DigestInterval = 10.0;
        private const double LocalChangeWindow = 5.0;
        private const double QuietBeforeDiff = 0.5;
        private const double ObjStableSeconds = 0.8;
        private const double WorldAnimationWindow = 8.0;
        private const double ResyncCooldown = 60.0;
        private const double BankRefreshDelay = 3.0;
        private const double MenuBaselineDelay = 1.5;
        private const int MaxApplyAttempts = 2;
        private const double HostSaveDelay = 1.5;
        private const double HostSaveRetry = 1.0;
        private const double PropsReconcileDelay = 2.0;

        internal static readonly RpcValidator NoClients = (sender, target, peer, args) => false;

        private readonly FactBaseline _baseline = new FactBaseline();
        private readonly StabilitySampler _sampler = new StabilitySampler();
        // Bounties (bo keys) whose entry or pads wait until this player leaves that bounty.
        private readonly HashSet<string> _deferredBounties = new HashSet<string>(StringComparer.Ordinal);
        // Runtime findings logged once per game run (which way the game went where the decompile left it open).
        private readonly HashSet<string> _infoOnce = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _paidRemotely = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, Attempt> _attempts = new Dictionary<string, Attempt>(StringComparer.Ordinal);
        // Objects this player's own bounty / LMUGC round switched on (never reported; see ChallengeProps).
        private readonly HashSet<string> _challengeProps = new HashSet<string>(StringComparer.Ordinal);
        // Objects this player's own bounty / LMUGC round switched off (held back; see ChallengeProps.HoldDeactivation).
        private readonly HashSet<string> _challengePropsOff = new HashSet<string>(StringComparer.Ordinal);
        // Host: client bounties accepted while the host played the same one. The host's own progress, so they outlive
        // scene loads and the session (see HostPendingBounties).
        private readonly HostPendingBounties _hostPendingBounties = new HostPendingBounties();
        private readonly FactTable _diffLive = new FactTable();

        private GameContext _ctx;
        private GameAccess _access;
        private LiveFacts _live;
        private FactApplier _applier;
        private Interactions _interactions;
        private WorldEvents _world;
        private Puzzles _puzzles;
        private Cutscenes _cutscenes;
        private HostClaimWorld _claimWorld;
        private RoundRecords _rounds;
        private PendingClaimQueue _pendingClaims;
        private Action<GameScene> _onSceneLoaded;
        private Action _onWorldLoaded;
        private Action<PlayerInfo> _onPlayerLeft;

        private bool _baselineReady;
        private bool _diffRequested;
        private double _nextPoll;
        private double _nextReconcile;
        private double _nextDigest;
        private double _lastResyncRequest = double.NegativeInfinity;
        private double _bankRefreshAt = double.PositiveInfinity;
        private double _menuBaselineAt = double.PositiveInfinity;
        private bool _menuBaselineDone;
        private int? _seed;
        private byte _moneyState;
        private PeerId _validatedFrom;
        private string _validatedKey;
        private bool _trackDeferred;
        // Host: main-track progress accepted from a client while the host could not apply it (in a bounty). It
        // belongs to the host's own save, so it outlives the session and is applied whenever the host is back on the
        // main track (then saved).
        private byte[] _hostPendingTrack;
        private bool _sessionStarted;
        private bool _hostedSession;
        private bool _preHitDeferred;
        private double _ownSetChangeAt = double.NegativeInfinity;
        private double _nextDeferredCheck;
        private double _hostSaveAt = double.PositiveInfinity;

        public string Name => "Progression";

        internal static ProgressionModule Instance { get; private set; }

        internal GameContext Context => _ctx;

        internal GameAccess Access => _access;

        internal LiveFacts Live => _live;

        internal FactApplier Applier => _applier;

        internal Interactions Interactions => _interactions;

        internal WorldEvents World => _world;

        internal Puzzles Puzzles => _puzzles;

        internal Cutscenes Cutscenes => _cutscenes;

        internal RoundRecords Rounds => _rounds;

        internal bool HasPendingHostTrack => _hostPendingTrack != null;

        /// <summary>Progress of the host's own save that waits for the host to be back on the main track / out of a bounty.</summary>
        internal bool HasPendingHostWork => _hostPendingTrack != null || _hostPendingBounties.Count > 0;

        /// <summary>The game deleted this peer's own run: progress waiting for it goes with it.</summary>
        internal void OnOwnRunDeleted()
        {
            if (_ctx == null)
                return;
            // The other players' bounties the host paid for belonged to the run that is gone: a new run completing
            // them is paid by its own game (no refund).
            _paidRemotely.Clear();
            if (!CanWriteOwnSave || !HasPendingHostWork)
                return;
            _hostPendingTrack = null;
            _trackDeferred = false;
            _hostPendingBounties.Clear();
            _ctx.Info("[Progression] The run was deleted; dropped the track and bounty progress that waited for it.");
        }

        /// <summary>The session's random seed (host: its own; client: the host's fact), or null.</summary>
        internal int? SessionSeed
        {
            get
            {
                if (_ctx == null)
                    return null;
                if (_ctx.IsHost)
                    return _seed;
                return _ctx.Facts.Contains(FactKinds.Seed, string.Empty)
                    ? _ctx.Facts.GetInt(FactKinds.Seed, string.Empty)
                    : (int?)null;
            }
        }

        // ---- IGameModule ----------------------------------------------------------------------------

        public void Install(GameContext context)
        {
            _ctx = context ?? throw new ArgumentNullException(nameof(context));
            Instance = this;
            _access = new GameAccess(context);
            _access.Resolve();
            _live = new LiveFacts(context, _access);
            _applier = new FactApplier(this, context, _access, _live);
            _interactions = new Interactions(this, context, _access);
            _world = new WorldEvents(this, context, _access);
            _puzzles = new Puzzles(this, context);
            _cutscenes = new Cutscenes(this, context, _access);
            _claimWorld = new HostClaimWorld(this);
            _rounds = new RoundRecords(this, context);
            _pendingClaims = new PendingClaimQueue(() => context.Now);

            string unmapped = RunFields.FindUnmapped();
            if (unmapped.Length > 0)
                _ctx.Error($"{Log}RunSave field '{unmapped}' is listed as shared but has no accessor; it is not synced.");

            ProgressionPatches.Install(context);
            _interactions.Install();
            _world.Install();
            _puzzles.Install();
            _cutscenes.Install();
            _rounds.Install();
            _ctx.RegisterRpc(ProgressionRpc.Note, OnNote, NoClients);
            _ctx.RegisterRpc(ProgressionRpc.Digest, OnDigest, NoClients);

            _onSceneLoaded = OnSceneLoaded;
            _onWorldLoaded = OnWorldLoaded;
            _onPlayerLeft = OnPlayerLeft;
            _ctx.SceneLoaded += _onSceneLoaded;
            _ctx.WorldLoaded += _onWorldLoaded;
            _ctx.PlayerLeft += _onPlayerLeft;
            _ctx.Save.SetFactHandler(this);
        }

        public void Uninstall()
        {
            if (_ctx != null)
            {
                _ctx.SceneLoaded -= _onSceneLoaded;
                _ctx.WorldLoaded -= _onWorldLoaded;
                _ctx.PlayerLeft -= _onPlayerLeft;
                if (ReferenceEquals(_ctx.Save.FactHandler, this))
                    _ctx.Save.SetFactHandler(null);
            }
            ResetSession();
            if (ReferenceEquals(Instance, this))
                Instance = null;
        }

        public void Tick(float deltaTime)
        {
            if (_ctx == null)
                return;
            double now = _ctx.Now;
            // Also after the session: a save the host still owes its own game.
            if (now >= _hostSaveAt)
                Guard("host save", TickHostSave);
            // Also after the session: the host's own pending progress waits for it to leave its bounty.
            if (now >= _nextDeferredCheck)
            {
                _nextDeferredCheck = now + PollInterval;
                Guard("deferred", TickDeferred);
            }
            if (!_ctx.InSession)
                return;
            Guard("interactions", _interactions.Tick);
            // Story that waited for the local player's own dialogue / cutscene / shot to end.
            Guard("cutscenes", _cutscenes.Tick);
            if (_diffRequested || now >= _nextPoll)
            {
                _diffRequested = false;
                _nextPoll = now + PollInterval;
                Guard("diff", RunDiff);
            }
            if (_ctx.IsClient)
            {
                if (now >= _nextReconcile)
                {
                    _nextReconcile = now + ReconcileInterval;
                    if (CanApplyNow())
                        Guard("reconcile", Reconcile);
                }
                if (now >= _bankRefreshAt)
                {
                    _bankRefreshAt = double.PositiveInfinity;
                    Guard("bank refresh", RefreshBank);
                }
            }
            else if (_ctx.IsHost)
            {
                if (now >= _nextDigest)
                {
                    _nextDigest = now + DigestInterval;
                    Guard("digest", SendDigest);
                }
                if (_pendingClaims.Count > 0 && _claimWorld.IsReady)
                    Guard("kept claims", DrainPendingClaims);
            }
        }

        public void OnSessionReady(bool isHost)
        {
            // The host's seed was made by BuildFacts, which runs just before this.
            _baseline.Clear();
            _sampler.Clear();
            _attempts.Clear();
            _baselineReady = false;
            _sessionStarted = true;
            _hostedSession = isHost;
            _nextPoll = _ctx.Now + PollInterval;
            _nextReconcile = _ctx.Now + ReconcileInterval;
            _nextDigest = _ctx.Now + DigestInterval;
            if (isHost)
            {
                if (_live.InWorld)
                {
                    SetBaselineFromWorld();
                    Guard("trivia", _puzzles.ReshuffleBoards);
                }
                else
                {
                    _menuBaselineDone = false;
                    _menuBaselineAt = _ctx.Now + 0.5;
                }
                return;
            }
            // Lifecycle reloads "Main" for the host's save (WorldLoaded reconciles then). Should the world stay
            // as it is, reconcile it in place.
            if (_live.InWorld)
            {
                int load = _ctx.SceneLoadId;
                _ctx.Delay(3.0, () =>
                {
                    if (load == _ctx.SceneLoadId && _ctx.IsClient && !_baselineReady)
                        Guard("reconcile", ReconcileAndBaseline);
                });
            }
        }

        public void OnSessionEnded()
        {
            // The host's game still owes a save of what clients changed in it: write it before the session is gone.
            if (!double.IsPositiveInfinity(_hostSaveAt))
                Guard("host save", () => SaveOwnRun(true));
            ResetSession();
        }

        private void ResetSession()
        {
            _baseline.Clear();
            _sampler.Clear();
            _deferredBounties.Clear();
            // A bounty still pending for the host's own save was paid already: keep that, so the host completing it
            // after the session is not paid twice.
            _paidRemotely.RemoveWhere(name => !IsHostPendingBounty(name));
            _attempts.Clear();
            _challengeProps.Clear();
            _challengePropsOff.Clear();
            _preHitDeferred = false;
            _ownSetChangeAt = double.NegativeInfinity;
            // A client's deferred pre-hit targets were the host's; its own world comes back without them.
            if (!_hostedSession)
                _live?.DeferredPreHit.Clear();
            _hostedSession = false;
            _pendingClaims?.Clear();
            _interactions?.Clear();
            _world?.Clear();
            _puzzles?.Clear();
            _cutscenes?.Clear();
            _rounds?.Clear();
            _baselineReady = false;
            _diffRequested = false;
            _bankRefreshAt = double.PositiveInfinity;
            _menuBaselineAt = double.PositiveInfinity;
            _menuBaselineDone = false;
            _lastResyncRequest = double.NegativeInfinity;
            _seed = null;
            _moneyState = MoneyGate.None;
            _validatedFrom = PeerId.None;
            _validatedKey = null;
            _trackDeferred = false;
            _sessionStarted = false;
            // _hostPendingTrack and _hostPendingBounties stay: they are the host's own progress (see the fields).
        }

        private bool IsHostPendingBounty(string cleanName)
        {
            foreach (var key in _hostPendingBounties.Keys())
            {
                if (GameIds.Clean(key) == cleanName)
                    return true;
            }
            return false;
        }

        // ---- Context events -------------------------------------------------------------------------

        private void OnSceneLoaded(GameScene scene)
        {
            // The world (and every object the baseline described) is gone.
            _baselineReady = false;
            _baseline.Clear();
            _sampler.Clear();
            _attempts.Clear();
            _deferredBounties.Clear();
            _challengeProps.Clear();
            _challengePropsOff.Clear();
            _trackDeferred = false;
            // Held story beats belonged to that world; the facts bring their state to the next one.
            _cutscenes.Clear();
            _preHitDeferred = false;
            // Kept by the game's lists only from here on (the host flushed them into its run before quitting).
            _live.DeferredPreHit.Clear();
            _interactions.OnSceneLoaded();
            _menuBaselineDone = false;
            _menuBaselineAt = _ctx.Now + MenuBaselineDelay;
        }

        private void OnWorldLoaded()
        {
            if (_ctx.InSession)
                Guard("shop objects", _world.NoteShopObjects);
            if (!_ctx.InSession)
            {
                // A client's track progress the host accepted in a bounty, and the session ended first: this is the
                // host's own save again, so it gets that progress now. (Pending bounties: the deferred poll, once the
                // load has settled.)
                if (_hostPendingTrack != null && CanWriteOwnSave)
                    Guard("pending track", ApplyTrackFromTable);
                return;
            }
            if (_ctx.IsHost)
            {
                // A client's track progress accepted while the host was in a bounty, then reloaded: catch up now,
                // before the scene settles and the facts are rebuilt from this world.
                if (_hostPendingTrack != null)
                    Guard("pending track", ApplyTrackFromTable);
                // Pending bounties stay in the facts (BuildFacts) and are applied by the deferred poll once settled.
                SetBaselineFromWorld();
                return;
            }
            int load = _ctx.SceneLoadId;
            // After the load-time Invokes (LoadInLMUGC, ...) have run.
            _ctx.Delay(GameContext.LoadingHoldSeconds + 0.25, () =>
            {
                if (load == _ctx.SceneLoadId && _ctx.IsClient)
                    Guard("reconcile", ReconcileAndBaseline);
            });
        }

        private void OnPlayerLeft(PlayerInfo player)
        {
            if (player != null && _ctx.IsHost)
                _interactions.OnPlayerLeft(player.Id);
        }

        // ---- IFactHandler ---------------------------------------------------------------------------

        public void BuildFacts(FactTable into)
        {
            if (into == null || _ctx == null)
                return;
            if (!_seed.HasValue)
                _seed = Guid.NewGuid().GetHashCode();
            if (_live.InWorld)
            {
                _live.CollectWorld(into, true, _seed, _moneyState);
                if (!into.Contains(FactKinds.Track, FactKinds.TrackKey))
                    KeepKnownTrack(into);
                KeepPendingTrack(into);
            }
            else
            {
                _live.CollectRun(into, _ctx.Facts, _seed, _moneyState);
            }
            // Client bounties the host accepted while it played the same one: not in its run yet, still the session's.
            _hostPendingBounties.KeepIn(into);
        }

        public bool ValidateClaim(PeerId from, SaveDelta claim)
        {
            if (_ctx == null || claim == null)
                return false;
            try
            {
                bool accepted = ClaimRules.Validate(from, claim, _ctx.Facts, _claimWorld, _interactions.TakenBy, out string reason);
                if (accepted)
                {
                    _validatedFrom = from;
                    _validatedKey = Composite(claim.Kind, claim.Key);
                }
                else if (ClaimRules.IsRetryable(reason) && _ctx.IsHost && _pendingClaims.Add(from, claim))
                {
                    // Not the claim's fault: kept and checked again once the world is ready (DrainPendingClaims).
                    _ctx.Info($"{Log}Kept {claim} from {_ctx.PlayerName(from)} until the host's world is ready.");
                }
                else
                {
                    _ctx.Info($"{Log}Refused {claim} from {_ctx.PlayerName(from)}: {reason}");
                }
                return accepted;
            }
            catch (Exception e)
            {
                _ctx.ErrorOnce("pg.validate:" + claim.Kind, $"{Log}Validating {claim} failed: {e}");
                return false;
            }
        }

        public void ApplyFact(SaveDelta fact)
        {
            if (_ctx == null || fact == null)
                return;
            try
            {
                if (_ctx.IsHost)
                    ApplyClaimOnHost(fact);
                else if (_ctx.IsClient)
                    ApplyHostFact(fact);
            }
            catch (Exception e)
            {
                _ctx.ErrorOnce("pg.apply:" + fact.Kind, $"{Log}Applying {fact} failed: {e}");
            }
        }

        public void OnFactsReplaced(FactTable facts)
        {
            if (_ctx == null || !_ctx.IsClientConnection)
                return;
            // Mid-session resync (same scene epoch): bring the running world in line. During a load, the reconcile
            // after WorldLoaded does it.
            // When Lifecycle is about to reload the scene for this save, the running world is not worth fixing.
            if (_baselineReady && _live.InWorld && !_ctx.IsLoadingWorld && !_ctx.WorldReplacePending)
                _ctx.Post(() => Guard("reconcile", ReconcileAndBaseline));
        }

        private void ApplyClaimOnHost(SaveDelta fact)
        {
            string composite = Composite(fact.Kind, fact.Key);
            var claimant = _validatedKey == composite ? _validatedFrom : PeerId.None;
            _validatedKey = null;
            if (!_live.InWorld)
                return;
            bool hadLive = _live.TryRead(fact.Kind, fact.Key, out var before);
            var result = _applier.Apply(fact.Kind, fact.Key, fact.Value, true, claimant);
            bool hasLive = _live.TryRead(fact.Kind, fact.Key, out var live);
            if (hasLive)
                _baseline.Set(fact.Kind, fact.Key, live);
            if (result == ApplyResult.Deferred || result == ApplyResult.NotApplicable || !hasLive)
                return;
            // The game saves its own changes as it makes them; a client's change made here is saved by us.
            if (!hadLive || !FactCodec.Equivalent(fact.Kind, before, live))
                ScheduleHostSave();
            // The host's game recorded something else (the union of pre-hit targets, an unlock it refused): that
            // is the session's value; Core broadcasts it instead of the claim.
            if (!FactCodec.Equivalent(fact.Kind, live, fact.Value))
                _ctx.Save.ReportLocalFact(fact.Kind, fact.Key, live);
        }

        private void ApplyHostFact(SaveDelta fact)
        {
            if (FactKinds.IsStoredOnly(fact.Kind) || !CanApplyNow())
                return;
            // Our own claim for this key may be in flight; if the host disagrees the reconcile fixes it.
            if (fact.Kind != FactKinds.Track && _baseline.ChangedWithin(fact.Kind, fact.Key, _ctx.Now, LocalChangeWindow))
                return;
            if (fact.Kind == FactKinds.Obj && KeepChallengeProp(fact.Key, fact.Value, OnMainTrackNow()))
                return;
            ApplyAndRebaseline(fact.Kind, fact.Key, fact.Value);
        }

        private bool CanApplyNow() =>
            _ctx.IsClient && _ctx.IsReady && _baselineReady && _live.InWorld && !_ctx.IsLoadingWorld && !_ctx.WorldReplacePending;

        // Host or client: the stored value (the host's claimant was paid when it was accepted).
        private void ApplyAndRebaselineAny(string kind, string key, byte[] value)
        {
            var result = _applier.Apply(kind, key, value, _ctx.IsHost, PeerId.None);
            if (_live.TryRead(kind, key, out var live))
                _baseline.Set(kind, key, live);
            if (_ctx.IsHost && result == ApplyResult.Applied)
                ScheduleHostSave();
        }

        private ApplyResult ApplyAndRebaseline(string kind, string key, byte[] value)
        {
            var result = _applier.Apply(kind, key, value, false, PeerId.None);
            if (_live.TryRead(kind, key, out var live))
                _baseline.Set(kind, key, live);
            return result;
        }

        // ---- Diff (3.1, 6.1) ------------------------------------------------------------------------

        internal void RequestDiff() => _diffRequested = true;

        private void RunDiff()
        {
            if (_ctx.IsHost)
            {
                if (_live.InWorld)
                {
                    if (_baselineReady && !_ctx.IsLoadingWorld)
                        DiffWorld(true);
                }
                else if (_ctx.Scene == GameScene.Menu || _ctx.Scene == GameScene.Credits)
                {
                    DiffMenu();
                }
                return;
            }
            if (!_ctx.IsClient || !_ctx.IsReady || !_baselineReady || !_live.InWorld || _ctx.IsLoadingWorld)
                return;
            // Effects of a replay are not this player's changes; state is diffed, so nothing is lost by waiting.
            if (_ctx.Replay.IsReplaying || _ctx.Replay.QuietSeconds < QuietBeforeDiff)
            {
                _nextPoll = Math.Min(_nextPoll, _ctx.Now + QuietBeforeDiff);
                return;
            }
            DiffWorld(false);
        }

        private void DiffWorld(bool host)
        {
            var live = _diffLive;
            live.Clear();
            _live.CollectWorld(live, host, _seed, _moneyState);
            _live.TryGetTrack(out _, out bool onMainTrack);
            bool inOwnChallenge = _live.InOwnChallenge();
            double now = _ctx.Now;
            foreach (var entry in live.GetEntries())
            {
                string kind = entry.Kind;
                string key = entry.Key;
                var value = entry.Value;
                if (FactKinds.IsStoredOnly(kind) || (!host && FactKinds.IsHostOnly(kind)))
                    continue;
                if (_baseline.Matches(kind, key, value))
                    continue;
                // Inside a bounty / LMUGC round the main track is not this player's to change.
                if (kind == FactKinds.Track && !onMainTrack)
                    continue;
                var previous = _baseline.Get(kind, key);
                if (kind == FactKinds.Obj && !ObjChangeReportable(key, previous, value, now, !onMainTrack, inOwnChallenge))
                    continue;
                _baseline.Set(kind, key, value);
                if (host)
                {
                    NoteHostChange(kind, key, previous, value);
                    _ctx.Save.ReportLocalFact(kind, key, value);
                    continue;
                }
                // Already the session's value (a replay's effect, or our confirmed claim): nothing to claim.
                var confirmed = _ctx.Facts.Get(kind, key);
                if (confirmed != null && FactCodec.Equivalent(kind, confirmed, value))
                    continue;
                _baseline.MarkLocalChange(kind, key, now);
                _ctx.Save.ReportLocalFact(kind, key, value);
            }
        }

        private bool ObjChangeReportable(string key, byte[] previous, byte[] value, double now, bool inOtherSet, bool inOwnChallenge)
        {
            if (!FactCodec.TryDecodeObj(value, out var state))
                return false;
            if (previous == null || !FactCodec.TryDecodeObj(previous, out var old))
            {
                _sampler.Forget(key);
                return true;
            }
            if (old.Active != state.Active)
            {
                // Objects a bounty switches on are that player's challenge props; consumed objects still count.
                if (state.Active && inOtherSet)
                {
                    _challengeProps.Add(key);
                    return false;
                }
                // And the main challenge's objects its start switched off (they come back when it ends). A pickup or
                // purchase is never a prop: the host's grant waits for exactly this report.
                if (!state.Active && ChallengeProps.HoldDeactivation(inOwnChallenge, _challengePropsOff.Contains(key), now - _ownSetChangeAt)
                    && !IsArbitratedObject(key))
                {
                    if (_challengePropsOff.Add(key))
                        _ctx.Info($"{Log}Holding back SaveableObject {key}: switched off by this player's own challenge.");
                    return false;
                }
                _sampler.Forget(key);
                return true;
            }
            // Rotation only: a swinging door is reported once it holds still.
            if (!_sampler.IsStable(key, state, now, ObjStableSeconds))
            {
                _nextPoll = Math.Min(_nextPoll, now + 0.5);
                return false;
            }
            _sampler.Forget(key);
            return true;
        }

        private void DiffMenu()
        {
            if (SaveManager.instance == null)
                return;
            var live = new FactTable();
            _live.CollectRun(live, _ctx.Facts, _seed, _moneyState);
            if (!_menuBaselineDone)
            {
                // The menu loads the host's save in its Start; take the baseline after that.
                if (_ctx.Now < _menuBaselineAt)
                    return;
                _baseline.ResetTo(live);
                _menuBaselineDone = true;
                return;
            }
            foreach (var entry in live.GetEntries())
            {
                if (FactKinds.IsStoredOnly(entry.Kind) || _baseline.Matches(entry.Kind, entry.Key, entry.Value))
                    continue;
                _baseline.Set(entry.Kind, entry.Key, entry.Value);
                _ctx.Save.ReportLocalFact(entry.Kind, entry.Key, entry.Value);
            }
        }

        private void SetBaselineFromWorld()
        {
            var live = new FactTable();
            _live.CollectWorld(live, _ctx.IsHost, _seed, _moneyState);
            _baseline.ResetTo(live);
            _sampler.Clear();
            _baselineReady = true;
        }

        // ---- Reconcile (6.1) ------------------------------------------------------------------------

        private void ReconcileAndBaseline()
        {
            if (!_ctx.IsClient || !_ctx.IsReady || !_live.InWorld || _ctx.WorldReplacePending)
                return;
            Reconcile();
            SetBaselineFromWorld();
            _nextReconcile = _ctx.Now + ReconcileInterval;
        }

        /// <summary>Client: applies every session fact the live world disagrees with (unless a claim of ours is in flight).</summary>
        private void Reconcile()
        {
            var live = new FactTable();
            _live.CollectWorld(live, true, null, 0);
            bool onMainTrack = OnMainTrackNow();
            double now = _ctx.Now;
            int applied = 0;
            foreach (var fact in _ctx.Facts.GetEntries())
            {
                if (FactKinds.IsStoredOnly(fact.Kind))
                    continue;
                string composite = Composite(fact.Kind, fact.Key);
                var current = live.Get(fact.Kind, fact.Key);
                if (current != null && FactCodec.Equivalent(fact.Kind, current, fact.Value))
                {
                    _attempts.Remove(composite);
                    continue;
                }
                // Objects and code doors this world does not have (or does not share) have nothing to apply to.
                if (current == null && (fact.Kind == FactKinds.Obj || fact.Kind == FactKinds.Auth))
                    continue;
                if (_baseline.ChangedWithin(fact.Kind, fact.Key, now, LocalChangeWindow))
                    continue;
                if (fact.Kind == FactKinds.Obj && RotationOnly(current, fact.Value) && now - _world.LastWorldEventAt < WorldAnimationWindow)
                    continue;
                if (fact.Kind == FactKinds.Obj && KeepChallengeProp(fact.Key, fact.Value, onMainTrack, current))
                    continue;
                if (!TryAttempt(composite, fact.Value))
                    continue;
                // The track rolls back too: a live track ahead of the session's with no claim of ours in flight
                // (checked above) was refused by the host.
                var result = fact.Kind == FactKinds.Track
                    ? _applier.ReconcileTrack(fact.Value)
                    : _applier.Apply(fact.Kind, fact.Key, fact.Value, false, PeerId.None);
                if (result == ApplyResult.Deferred)
                    _attempts.Remove(composite);
                applied++;
            }
            // List entries the host does not have (a refused claim): take them back out.
            foreach (var entry in live.GetEntries())
            {
                if (entry.Kind != FactKinds.Bounty && entry.Kind != FactKinds.DoNotReload && entry.Kind != FactKinds.Easy)
                    continue;
                if (_ctx.Facts.Contains(entry.Kind, entry.Key) || _baseline.ChangedWithin(entry.Kind, entry.Key, now, LocalChangeWindow))
                    continue;
                _applier.RemoveListEntry(entry.Kind, entry.Key);
                applied++;
            }
            if (applied > 0)
                _ctx.Info($"{Log}Reconciled {applied} fact(s) with the host's session.");
        }

        private bool OnMainTrackNow() => _live.TryGetTrack(out _, out bool onMainTrack) && onMainTrack;

        /// <summary>
        /// Client: the session says this object is off, but this player's own bounty / LMUGC round switched it on
        /// (see <see cref="ChallengeProps"/>): leave it until the player is back on the main track.
        /// </summary>
        private bool KeepChallengeProp(string key, byte[] wanted, bool onMainTrack, byte[] current = null)
        {
            bool heldOff = _challengePropsOff.Contains(key);
            if ((onMainTrack && !heldOff) || !FactCodec.TryDecodeObj(wanted, out var want))
                return false;
            if (current == null && !_live.TryRead(FactKinds.Obj, key, out current))
                return false;
            if (!FactCodec.TryDecodeObj(current, out var now))
                return false;
            // Switched off by this player's own bounty start: the game brings it back when the bounty ends.
            if (heldOff && ChallengeProps.KeepLocalOff(!_live.InOwnChallenge(), now.Active, want.Active, true))
                return true;
            if (onMainTrack)
                return false;
            var previous = _baseline.Get(FactKinds.Obj, key);
            bool? baselineActive = previous != null && FactCodec.TryDecodeObj(previous, out var old) ? old.Active : (bool?)null;
            if (!ChallengeProps.KeepLocal(false, now.Active, want.Active, _challengeProps.Contains(key), baselineActive))
                return false;
            _challengeProps.Add(key);
            return true;
        }

        private static bool IsArbitratedObject(string key)
        {
            var saveable = GameIds.ResolveSaveable(key);
            return saveable != null && Interactions.ArbitratedIds(saveable).Count > 0;
        }

        private static bool RotationOnly(byte[] current, byte[] wanted) =>
            FactCodec.TryDecodeObj(current, out var a) && FactCodec.TryDecodeObj(wanted, out var b) && a.Active == b.Active;

        /// <summary>The same value is applied at most <see cref="MaxApplyAttempts"/> times without converging.</summary>
        private bool TryAttempt(string composite, byte[] value)
        {
            if (_attempts.TryGetValue(composite, out var attempt) && FactTable.ValueEquals(attempt.Value, value))
            {
                if (attempt.Count >= MaxApplyAttempts)
                {
                    _ctx.WarnOnce("pg.converge:" + composite, $"{Log}The game does not take fact {composite.Replace('\0', '/')}; giving up on it.");
                    return false;
                }
                attempt.Count++;
                return true;
            }
            _attempts[composite] = new Attempt(value);
            return true;
        }

        private void RefreshBank()
        {
            if (!CanApplyNow())
                return;
            var value = _ctx.Facts.Get(FactKinds.Bank, string.Empty);
            if (value != null)
                ApplyAndRebaseline(FactKinds.Bank, string.Empty, value);
        }

        internal void ScheduleBankRefresh() => _bankRefreshAt = _ctx.Now + BankRefreshDelay;

        // ---- Deferred track / pads ------------------------------------------------------------------

        internal void OnTrackDeferred(byte[] value)
        {
            _trackDeferred = true;
            if (_ctx.IsHost && value != null)
                _hostPendingTrack = (byte[])value.Clone();
        }

        /// <summary>A bounty's pads (or its entry, see <see cref="DeferBountyEntry"/>) wait until this player leaves that bounty.</summary>
        internal void DeferBounty(string bounty)
        {
            if (!string.IsNullOrEmpty(bounty))
                _deferredBounties.Add(bounty);
        }

        /// <summary>
        /// A newly completed bounty's list entry waits: this player is playing it. On the host the entry belongs to
        /// its own save, so it is also kept where scene loads and the session end do not drop it.
        /// </summary>
        internal void DeferBountyEntry(string key, byte flags)
        {
            DeferBounty(key);
            // Clients: the host keeps it. Host (or offline, re-applying its own pending entry): its own save.
            if (_ctx == null || !CanWriteOwnSave || !(_ctx.IsHost || !_ctx.InSession))
                return;
            if (!_hostPendingBounties.Contains(key))
                _ctx.Info($"{Log}Bounty {GameIds.Clean(key)} was completed by another player while the host plays it; it is recorded when the host leaves it.");
            _hostPendingBounties.Add(key, flags);
        }

        /// <summary>Other players' pre-hit targets wait until this player is out of its own challenge.</summary>
        internal void DeferPreHit() => _preHitDeferred = true;

        /// <summary>This player's own challenge set changed (a bounty or LMUGC round started or ended, a set loaded).</summary>
        internal void OnOwnSetChanged()
        {
            if (_ctx != null)
                _ownSetChangeAt = _ctx.Now;
        }

        /// <summary>
        /// Every second: deferred bounty / track / pre-hit work is done once the player is out of its own challenge,
        /// whichever way it got out (the ExitBountyChallenge / LoadNewChallengeSet hooks are the quick path; the game
        /// may clear its active pad later than those, or end a challenge another way).
        /// </summary>
        private void TickDeferred()
        {
            if (!_live.InWorld || _ctx.IsLoadingWorld || _ctx.WorldReplacePending)
                return;
            bool work;
            if (_ctx.InSession)
            {
                bool props = (_challengeProps.Count > 0 && OnMainTrackNow()) || _challengePropsOff.Count > 0;
                work = _deferredBounties.Count > 0 || _trackDeferred || _preHitDeferred || props
                    || (_ctx.IsHost && HasPendingHostWork);
            }
            else
            {
                work = CanWriteOwnSave && (HasPendingHostWork || _live.DeferredPreHit.Count > 0);
            }
            if (BountyIsolation.CanResolve(work, true, false, _live.CurrentPlay()))
                ResolveDeferred();
        }

        /// <summary>
        /// Host (or offline, its own save): applies the client bounties that waited for it to leave the same bounty.
        /// They were paid when accepted; the entry, the pads, the boards and the save follow now.
        /// </summary>
        private void ApplyHostPendingBounties()
        {
            if (!CanWriteOwnSave || _hostPendingBounties.Count == 0 || !_live.InWorld)
                return;
            foreach (var key in _hostPendingBounties.Keys())
            {
                byte[] value = _ctx.InSession ? _ctx.Facts.Get(FactKinds.Bounty, key) : null;
                if (value == null)
                {
                    _hostPendingBounties.TryGet(key, out byte flags);
                    value = FactCodec.EncodeFlags(flags);
                }
                var result = _applier.Apply(FactKinds.Bounty, key, value, true, PeerId.None);
                if (_live.TryRead(FactKinds.Bounty, key, out var live))
                {
                    _hostPendingBounties.Remove(key);
                    if (_ctx.InSession)
                        _baseline.Set(FactKinds.Bounty, key, live);
                    else
                        ForgetPaidRemotely(key);
                    _ctx.Info($"{Log}Recorded bounty {GameIds.Clean(key)} in the host's run.");
                }
                if (result == ApplyResult.Applied)
                    ScheduleHostSave();
            }
        }

        /// <summary>
        /// Host, PauseMenu.QuitToMenu (before the game's own SaveRun there): what waits for the host to leave its
        /// bounty goes into the run now, so that save keeps it.
        /// </summary>
        internal void FlushOwnPendingIntoRun()
        {
            if (_ctx == null || !CanWriteOwnSave || !_live.InWorld)
                return;
            if (_hostPendingBounties.Count == 0 && _live.DeferredPreHit.Count == 0)
                return;
            var flushed = _hostPendingBounties.Keys();
            int added;
            using (_ctx.Replay.Begin("pg.flush"))
                added = _applier.FlushIntoRun(_hostPendingBounties);
            _preHitDeferred = false;
            if (!_ctx.InSession)
            {
                foreach (var key in flushed)
                {
                    if (!_hostPendingBounties.Contains(key))
                        ForgetPaidRemotely(key);
                }
            }
            _ctx.Info($"{Log}Quitting to the menu: wrote {added} bounty entr{(added == 1 ? "y" : "ies")} other players completed into the run before it is saved.");
        }

        /// <summary>Logs <paramref name="message"/> the first time <paramref name="key"/> is seen.</summary>
        internal void InfoOnce(string key, string message)
        {
            if (_ctx != null && _infoOnce.Add(key))
                _ctx.Info(message);
        }

        internal void MarkPaidRemotely(string bounty)
        {
            if (!string.IsNullOrEmpty(bounty))
                _paidRemotely.Add(bounty);
        }

        internal bool WasPaidRemotely(string bounty) => bounty != null && _paidRemotely.Contains(bounty);

        /// <summary>
        /// After the session: the host's waiting entry <paramref name="key"/> is in its run now, so the run's own
        /// HasCompletedThisBounty keeps the game from paying it again; the refund mark has done its job.
        /// </summary>
        private void ForgetPaidRemotely(string key)
        {
            string name = GameIds.Clean(key);
            if (_paidRemotely.Remove(name))
                _ctx.Info($"{Log}Bounty {name} is in the host's run now; a later completion of it is the run's own.");
        }

        /// <summary>The player left a bounty or loaded a set: apply what waited for that.</summary>
        internal void ResolveDeferred()
        {
            if (_ctx == null || !_live.InWorld)
                return;
            if (!_ctx.InSession)
            {
                // After the session (see _hostPendingTrack): leaving the bounty brings the host back to the main track.
                if (_hostPendingTrack != null && CanWriteOwnSave)
                    Guard("pending track", ApplyTrackFromTable);
                if (_hostPendingBounties.Count > 0 && CanWriteOwnSave)
                    Guard("pending bounties", ApplyHostPendingBounties);
                if (_live.DeferredPreHit.Count > 0 && CanWriteOwnSave && !_live.InOwnChallenge())
                {
                    Guard("pending pre-hit", _applier.ResolvePreHit);
                    ScheduleHostSave();
                }
                return;
            }
            Guard("deferred bounties", () =>
            {
                var keys = new List<string>(_deferredBounties);
                // The host's own pending entries too (a scene load forgot the deferral, not the entry).
                if (_ctx.IsHost)
                {
                    foreach (var key in _hostPendingBounties.Keys())
                    {
                        if (!_deferredBounties.Contains(key))
                            keys.Add(key);
                    }
                }
                if (keys.Count == 0)
                    return;
                _deferredBounties.Clear();
                foreach (var key in keys)
                {
                    // The session's entry (the player did not complete it meanwhile, or did: then it is there).
                    var value = _ctx.Facts.Get(FactKinds.Bounty, key);
                    if (value == null && _ctx.IsHost && _hostPendingBounties.TryGet(key, out byte flags))
                        value = FactCodec.EncodeFlags(flags);
                    if (value != null)
                        ApplyAndRebaselineAny(FactKinds.Bounty, key, value);
                    else
                        _applier.CompletePads(GameIds.Clean(key));
                    if (_ctx.IsHost && _live.TryRead(FactKinds.Bounty, key, out _))
                        _hostPendingBounties.Remove(key);
                }
            });
            if (_preHitDeferred && !_live.InOwnChallenge())
                Guard("deferred pre-hit", ResolvePreHitInSession);
            if (_trackDeferred || (_ctx.IsHost && _hostPendingTrack != null))
                Guard("deferred track", ApplyTrackFromTable);
            // Back on the main track: the challenge's props are the game's to clear; reconcile what is left soon.
            if (_ctx.IsClient && _challengeProps.Count > 0 && OnMainTrackNow())
            {
                _challengeProps.Clear();
                _nextReconcile = Math.Min(_nextReconcile, _ctx.Now + PropsReconcileDelay);
            }
            // Out of its own challenge: what its start switched off is back (or, if not, reported by the diff now).
            if (_challengePropsOff.Count > 0 && ChallengeProps.ReleaseHeld(_live.InOwnChallenge(), _ctx.Now - _ownSetChangeAt))
            {
                _challengePropsOff.Clear();
                RequestDiff();
                if (_ctx.IsClient)
                    _nextReconcile = Math.Min(_nextReconcile, _ctx.Now + PropsReconcileDelay);
            }
        }

        /// <summary>Hands other players' pre-hit targets to the game now that this player is out of its own challenge.</summary>
        private void ResolvePreHitInSession()
        {
            _preHitDeferred = false;
            _applier.ResolvePreHit();
            if (!_live.TryRead(FactKinds.PreHit, string.Empty, out var live))
                return;
            _baseline.Set(FactKinds.PreHit, string.Empty, live);
            if (!_ctx.IsHost)
                return;
            ScheduleHostSave();
            // The session's value is the union; the host's game holds it now.
            var table = _ctx.Facts.Get(FactKinds.PreHit, string.Empty);
            if (table == null || !FactCodec.Equivalent(FactKinds.PreHit, table, live))
                _ctx.Save.ReportLocalFact(FactKinds.PreHit, string.Empty, live);
        }

        private void ApplyTrackFromTable()
        {
            // The host's own save: in its session, or after it (the pending value is then all there is).
            bool own = _ctx.IsHost || !_ctx.InSession;
            var wanted = _ctx.InSession ? _ctx.Facts.Get(FactKinds.Track, FactKinds.TrackKey) : null;
            if (own && _hostPendingTrack != null && wanted == null)
                wanted = _hostPendingTrack;
            if (wanted == null)
            {
                // Nothing to catch up with (the session has no track).
                _trackDeferred = false;
                return;
            }
            if (!CanTakeTrack(out bool hasLocal, out var local))
                return;
            if (own && _hostPendingTrack != null && FactCodec.TryDecodeTrack(wanted, out var target)
                && !TrackPlanner.PendingStillAhead(target, hasLocal, local, SetCount(local.Set)))
            {
                // The host got there (or further) by itself.
                _hostPendingTrack = null;
                _trackDeferred = false;
                return;
            }
            var result = _applier.Apply(FactKinds.Track, FactKinds.TrackKey, wanted, own, PeerId.None);
            if (result == ApplyResult.Deferred)
                return;
            _trackDeferred = false;
            if (own)
            {
                _hostPendingTrack = null;
                if (result == ApplyResult.Applied)
                    ScheduleHostSave();
            }
            if (_live.TryRead(FactKinds.Track, FactKinds.TrackKey, out var live))
                _baseline.Set(FactKinds.Track, FactKinds.TrackKey, live);
        }

        /// <summary>
        /// The player can take the shared track now: on the main track, or with no readable main set (an LMUGC round
        /// left its set as the main one) while in no round and no bounty.
        /// </summary>
        private bool CanTakeTrack(out bool hasLocal, out TrackValue local)
        {
            hasLocal = _live.TryGetTrack(out local, out bool onMainTrack);
            if (hasLocal)
                return onMainTrack;
            return _access.TrackEnabled && !_live.IsJustGolf && ChallengeSystem.instance != null && !_live.InBounty() && !_live.InLmugc();
        }

        private int SetCount(string set) => _access.ChallengeCount(_access.FindChallengeSet(set));

        /// <summary>Host rebuilding its facts while a client's track progress waits: keep that progress.</summary>
        private void KeepPendingTrack(FactTable into)
        {
            if (_hostPendingTrack == null || !FactCodec.TryDecodeTrack(_hostPendingTrack, out var wanted))
                return;
            var live = into.Get(FactKinds.Track, FactKinds.TrackKey);
            bool hasLive = live != null && FactCodec.TryDecodeTrack(live, out _);
            TrackValue current = default;
            if (hasLive)
                FactCodec.TryDecodeTrack(live, out current);
            if (!TrackPlanner.PendingStillAhead(wanted, hasLive, current, hasLive ? SetCount(current.Set) : 0))
                return;
            into.Set(FactKinds.Track, FactKinds.TrackKey, _hostPendingTrack);
        }

        /// <summary>
        /// Host rebuilding its facts while the main track cannot be read (in an LMUGC round): keep the shared track
        /// (the last one read here, the session's, or the run's saved one) rather than drop it from the session.
        /// </summary>
        private void KeepKnownTrack(FactTable into)
        {
            var known = _live.FallbackTrack();
            if (known == null && _sessionStarted)
                known = _ctx.Facts.Get(FactKinds.Track, FactKinds.TrackKey);
            if (known != null && FactCodec.TryDecodeTrack(known, out _))
                into.Set(FactKinds.Track, FactKinds.TrackKey, known);
        }

        // ---- Host saves -----------------------------------------------------------------------------

        /// <summary>This peer's save is its own (hosting or offline), so the mod may save it.</summary>
        private bool CanWriteOwnSave => !_ctx.IsClientConnection && !_ctx.Save.SuppressDiskWrites;

        /// <summary>
        /// Host (or offline): a change the mod made to the host's game on a client's behalf (an accepted claim, a
        /// money report, a pending track) is saved soon, as the game saves its own changes when it makes them.
        /// </summary>
        internal void ScheduleHostSave()
        {
            if (_ctx == null || !CanWriteOwnSave)
                return;
            double at = _ctx.Now + HostSaveDelay;
            if (at < _hostSaveAt)
                _hostSaveAt = at;
        }

        private void TickHostSave()
        {
            if (!SaveOwnRun(false))
                _hostSaveAt = _ctx.Now + HostSaveRetry;
        }

        /// <summary>SaveRun now when the world allows it; false to try again later.</summary>
        private bool SaveOwnRun(bool force)
        {
            var manager = SaveManager.instance;
            if (!CanWriteOwnSave || manager == null || !_live.InWorld)
            {
                // Nothing of ours loaded (menu, or a client's world): the next world load starts from disk anyway.
                _hostSaveAt = double.PositiveInfinity;
                return true;
            }
            if (_ctx.IsLoadingWorld && !force)
                return false;
            var controller = MoveAndHitController.instance;
            if (!force && controller != null && controller.m_moveLocked)
                return false;
            _hostSaveAt = double.PositiveInfinity;
            manager.SaveRun();
            return true;
        }

        // ---- Kept claims ----------------------------------------------------------------------------

        /// <summary>Host, world ready again: checks and applies the claims that arrived while it was not.</summary>
        private void DrainPendingClaims()
        {
            foreach (var pair in _pendingClaims.TakeAll())
            {
                var from = pair.Key;
                var claim = pair.Value;
                if (!ClaimRules.Validate(from, claim, _ctx.Facts, _claimWorld, _interactions.TakenBy, out string reason))
                {
                    _ctx.Info($"{Log}Refused kept {claim} from {_ctx.PlayerName(from)}: {reason}");
                    continue;
                }
                var current = _ctx.Facts.Get(claim.Kind, claim.Key);
                if (current != null && FactCodec.Equivalent(claim.Kind, current, claim.Value))
                    continue;
                _ctx.Info($"{Log}Accepted kept {claim} from {_ctx.PlayerName(from)}.");
                // As Core does for a live claim: the session's value first (broadcast), then the game; a different
                // value the host's game recorded replaces it (ApplyClaimOnHost reports that).
                _ctx.Save.ReportLocalFact(claim.Kind, claim.Key, claim.Value);
                _validatedFrom = from;
                _validatedKey = Composite(claim.Kind, claim.Key);
                ApplyClaimOnHost(claim);
            }
        }

        // ---- Money gate -----------------------------------------------------------------------------

        /// <summary>Host: may DeductMoney / ReturnMoney run now? If so, records it in the money fact.</summary>
        internal bool TryAdvanceMoney(string method)
        {
            if (!MoneyGate.CanRun(method, _moneyState))
                return false;
            _moneyState = MoneyGate.After(method, _moneyState);
            _ctx.Save.ReportLocalFact(FactKinds.Money, FactKinds.MoneyKey, FactCodec.EncodeFlags(_moneyState));
            return true;
        }

        // ---- Notes ----------------------------------------------------------------------------------

        /// <summary>Host: a toast for the others ("Alice completed bounty BO_Gong").</summary>
        internal void SendNote(PeerId by, NoteKind kind, string text)
        {
            if (!_ctx.IsHost || string.IsNullOrEmpty(text))
                return;
            text = Truncate(text, ProgressionRpc.MaxNoteBytes);
            if (by != _ctx.LocalId)
                _ctx.Notify(text);
            ulong author = by.Value;
            byte code = (byte)kind;
            _ctx.Send(ProgressionRpc.Note, RpcTarget.Others, w =>
            {
                w.WriteULong(author);
                w.WriteByte(code);
                w.WriteString(text);
            });
        }

        private void OnNote(PeerId sender, NetReader reader)
        {
            var by = new PeerId(reader.ReadULong());
            reader.ReadByte();
            string text = reader.ReadString(ProgressionRpc.MaxNoteBytes);
            if (!_ctx.IsClient || sender != _ctx.HostId || by == _ctx.LocalId)
                return;
            _ctx.Notify(text);
        }

        /// <summary>Host: toasts for the host's own progress.</summary>
        private void NoteHostChange(string kind, string key, byte[] previous, byte[] value)
        {
            string me = _ctx.PlayerName(_ctx.LocalId);
            switch (kind)
            {
                case FactKinds.Track:
                    if (previous != null && FactCodec.TryDecodeTrack(previous, out var old) && FactCodec.TryDecodeTrack(value, out var now)
                        && old.Set == now.Set && now.Index == old.Index + 1)
                    {
                        var system = ChallengeSystem.instance;
                        var challenges = system != null ? _access.Challenges(system.m_mainChallengeSet) : null;
                        string text = challenges != null && old.Index < challenges.Length ? _access.ShortString(challenges[old.Index]) : string.Empty;
                        SendNote(_ctx.LocalId, NoteKind.Challenge, me + " completed: " + (text.Length > 0 ? text : now.Set));
                    }
                    break;
                case FactKinds.Bounty:
                    if (previous == null)
                        SendNote(_ctx.LocalId, NoteKind.Bounty, me + " completed bounty " + GameIds.Clean(key));
                    break;
                case FactKinds.RunBool:
                    string feature = SharedRunFields.FeatureName(key);
                    if (feature != null && FactTable.DecodeBool(value) && !FactTable.DecodeBool(previous))
                        SendNote(_ctx.LocalId, NoteKind.Unlock, me + " unlocked: " + feature);
                    break;
            }
        }

        // ---- Digest (6.13) --------------------------------------------------------------------------

        private void SendDigest()
        {
            var host = _ctx.Net?.SaveHost;
            if (host == null || host.PeerCount == 0)
                return;
            uint revision = host.Revision;
            uint crc = _ctx.Facts.ComputeCrc();
            uint count = (uint)_ctx.Facts.Count;
            _ctx.Send(ProgressionRpc.Digest, RpcTarget.Others, w =>
            {
                w.WriteVarUInt(revision);
                w.WriteUInt(crc);
                w.WriteVarUInt(count);
            });
        }

        private void OnDigest(PeerId sender, NetReader reader)
        {
            uint revision = reader.ReadVarUInt();
            uint crc = reader.ReadUInt();
            uint count = reader.ReadVarUInt();
            if (!_ctx.IsClient || sender != _ctx.HostId)
                return;
            var client = _ctx.Net?.SaveClient;
            if (client == null || !client.IsSynced || client.Revision != revision)
                return;
            if ((uint)_ctx.Facts.Count == count && _ctx.Facts.ComputeCrc() == crc)
                return;
            if (_ctx.Now - _lastResyncRequest < ResyncCooldown)
                return;
            _lastResyncRequest = _ctx.Now;
            _ctx.Warn($"{Log}Session facts differ from the host's at r{revision}; asking for a resync.");
            client.RequestResync();
        }

        // ---- Helpers --------------------------------------------------------------------------------

        private void Guard(string what, Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                _ctx.ErrorOnce("pg.tick:" + what, $"{Log}{what} failed: {e}");
            }
        }

        private static string Composite(string kind, string key) => kind + "\0" + key;

        private static string Truncate(string text, int maxBytes)
        {
            while (text.Length > 0 && System.Text.Encoding.UTF8.GetByteCount(text) > maxBytes)
                text = text.Substring(0, text.Length - 1);
            return text;
        }

        private sealed class Attempt
        {
            public Attempt(byte[] value)
            {
                Value = value;
                Count = 1;
            }

            public readonly byte[] Value;
            public int Count;
        }

        /// <summary>The host's world for <see cref="ClaimRules"/>.</summary>
        private sealed class HostClaimWorld : IClaimWorld
        {
            private readonly ProgressionModule _module;

            public HostClaimWorld(ProgressionModule module)
            {
                _module = module;
            }

            public bool IsReady => _module._live.InWorld && !_module._ctx.IsLoadingWorld;

            public bool TryGetChallengeSet(string name, out bool isBounty, out int challengeCount)
            {
                var set = _module._access.FindChallengeSet(GameIds.Clean(name));
                if (set == null)
                {
                    isBounty = false;
                    challengeCount = 0;
                    return false;
                }
                challengeCount = _module._access.ChallengeCount(set);
                // Without the isBounty field, fall back to the game's naming of bounty sets.
                isBounty = _module._access.BountyEnabled ? _module._access.IsBounty(set) : LooksLikeBounty(GameIds.Clean(name));
                return true;
            }

            public bool TryGetSaveable(string id, out bool active, out IReadOnlyList<string> arbitratedIds)
            {
                var saveable = GameIds.ResolveSaveable(id);
                if (saveable == null)
                {
                    active = false;
                    arbitratedIds = null;
                    return false;
                }
                active = saveable.gameObject.activeSelf;
                arbitratedIds = Interactions.ArbitratedIds(saveable);
                return true;
            }

            private static bool LooksLikeBounty(string name) =>
                name.StartsWith("BO_", StringComparison.Ordinal) || name.StartsWith("PAR", StringComparison.Ordinal)
                || name.StartsWith("CC_", StringComparison.Ordinal);
        }
    }
}
