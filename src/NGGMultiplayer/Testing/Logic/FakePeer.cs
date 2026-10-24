using System;
using System.Collections.Generic;
using System.Globalization;
using NGGMultiplayer.Core;
using NGGMultiplayer.Core.Diagnostics;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Save;
using NGGMultiplayer.Core.Session;
using NGGMultiplayer.Core.Sync;
using NGGMultiplayer.Game;
using NGGMultiplayer.Game.Lifecycle;
using NGGMultiplayer.Game.Presence;
using NGGMultiplayer.Game.Progression;

namespace NGGMultiplayer.Testing
{
    public sealed class FakePeerOptions
    {
        public string DisplayName { get; set; } = "Bot";

        /// <summary>Must equal the host's, or the host rejects the Hello (SessionConfig.RequireMatchingGameBuild).</summary>
        public string GameBuild { get; set; } = string.Empty;

        public double TimeoutSeconds { get; set; } = 10;

        public double SendRate { get; set; } = 30;

        /// <summary>Start angle on the walking circle (radians), so bots spread around the host.</summary>
        public float Phase { get; set; }

        /// <summary>The game's ShotType value of the bot's shots (resolved by name at runtime; 0 when unknown).</summary>
        public byte ShotType { get; set; }

        public string Club { get; set; } = "Driver";

        /// <summary>The surface named in the bot's rest messages.</summary>
        public string Surface { get; set; } = "Fairway";
    }

    /// <summary>A pickup or purchase the bot asks the host for (<c>ngg.pg.claim</c>), with what the bot does once granted.</summary>
    public sealed class PickupRequest
    {
        /// <summary>The interactable's arbitration id (Interactions.IdOf on the host).</summary>
        public string InteractId { get; set; }

        /// <summary>The game's InteractionType (answers #92 order).</summary>
        public byte Interaction { get; set; }

        /// <summary>The interactable's m_cashValue: price or cash amount (answers #35).</summary>
        public int CashValue { get; set; }

        /// <summary>
        /// The SaveableObject m_id the interactable sits on, when taking it hides that object: the bot then claims the
        /// <c>obj</c> fact inactive, as a real client's diff would after its Interact ran. Null for none.
        /// </summary>
        public string SaveableId { get; set; }

        /// <summary>Report the money change a real client's granted Interact makes (<see cref="BotInteractions.ExpectedBankReport"/>).</summary>
        public bool ReportMoney { get; set; } = true;
    }

    public enum FakePeerState : byte
    {
        Idle,
        Connecting,
        Syncing,
        Ready,
        Ended
    }

    /// <summary>
    /// An in-process simulated remote client of the host (single-PC testing). It runs its own <see cref="NetSession"/>
    /// over a loopback transport, so it does exactly what a real client's Core does: Hello/Welcome, save sync (the
    /// host's bundle is kept in memory by <see cref="BotSaveBackend"/>, never on disk), its player-bound entities
    /// streamed from <see cref="BotAvatar"/>, progress claims as ProgressClaim, and the game RPCs a real client's modules
    /// send (<c>ngg.pr.info/shot/rest</c>, <c>ngg.pg.claim/bank/world/story</c>) with their exact payloads. It never
    /// touches Unity or the game: the harness feeds it the host's pose and resolves world ids. Main thread only.
    /// </summary>
    public sealed class FakePeer : IDisposable
    {
        public const double InfoInterval = 3.0;
        public const double PickupTimeoutSeconds = 5.0;
        public const double DigestResyncCooldown = 60.0;
        public const int MaxNotes = 8;

        private readonly ITransport _transport;
        private readonly FakePeerOptions _options;
        private readonly BotAvatar _avatar;
        private readonly BotClaims _claims = new BotClaims();
        private readonly Dictionary<uint, PendingPickup> _pickups = new Dictionary<uint, PendingPickup>();
        private readonly List<BotMotionEvent> _motion = new List<BotMotionEvent>();
        private readonly List<BotClaimResult> _settled = new List<BotClaimResult>();
        private readonly List<uint> _scratch = new List<uint>();
        private readonly List<string> _notes = new List<string>();
        private readonly Action<SaveDelta, string> _onClaimRejected;

        private SaveSyncClient _saveClient;
        private double _now;
        private double _lastInfoAt = double.NegativeInfinity;
        private InfoMessage _lastInfo;
        private bool _sentInfo;
        private uint _nextPickupId;
        private int _snapshotsSeen;
        private double _lastDigestResync = double.NegativeInfinity;
        private bool _disposed;

        public FakePeer(int index, ITransport transport, FakePeerOptions options = null)
        {
            if (index < 1)
                throw new ArgumentOutOfRangeException(nameof(index), index, "Bot numbers start at 1.");
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _options = options ?? new FakePeerOptions();
            Index = index;
            Name = string.IsNullOrEmpty(_options.DisplayName) ? "Bot " + index.ToString(CultureInfo.InvariantCulture) : _options.DisplayName;
            _avatar = new BotAvatar(_options.Phase);
            Save = new BotSaveBackend();
            Save.DeltaApplied += OnDeltaApplied;
            _onClaimRejected = OnClaimRejected;

            var netOptions = new NetSessionOptions
            {
                Session = new SessionConfig
                {
                    DisplayName = Truncate(Name, SessionConfig.MaxDisplayNameChars),
                    GameBuild = _options.GameBuild ?? string.Empty,
                    TimeoutSeconds = _options.TimeoutSeconds,
                    ConnectTimeoutSeconds = Math.Max(10, _options.TimeoutSeconds)
                },
                Entities = new NetEntityRegistryOptions { SendRate = _options.SendRate }
            };
            Net = new NetSession(transport, Save, new EntityFactory(this), netOptions);
            Net.Started += OnStarted;
            Net.Ready += OnReady;
            Net.Ended += OnEnded;
            RegisterRpcs();
        }

        public int Index { get; }

        public string Name { get; }

        public PeerId Id => _transport.LocalId;

        public NetSession Net { get; }

        public BotSaveBackend Save { get; }

        public BotAvatar Avatar => _avatar;

        public FakePeerOptions Options => _options;

        public FakePeerState State
        {
            get
            {
                if (EndInfo.HasValue)
                    return FakePeerState.Ended;
                switch (Net.Session.State)
                {
                    case SessionState.Connecting: return FakePeerState.Connecting;
                    case SessionState.Active: return Net.IsReady ? FakePeerState.Ready : FakePeerState.Syncing;
                    default: return FakePeerState.Idle;
                }
            }
        }

        public bool IsReady => Net.IsReady;

        /// <summary>Why the bot's session ended; null while it has not.</summary>
        public SessionEndInfo? EndInfo { get; private set; }

        /// <summary>The host's scene and mode, from the bundle and later <c>ngg.lc.scene</c> notices.</summary>
        public GameScene HostScene { get; private set; } = GameScene.None;

        public uint SceneEpoch { get; private set; }

        public byte Mode { get; private set; }

        public int SceneNotices { get; private set; }

        public bool HasWind { get; private set; }

        internal WindMessage LastWind { get; private set; }

        public double LastWindAt { get; private set; } = double.NegativeInfinity;

        public int WindMessages { get; private set; }

        public int DigestMatches { get; private set; }

        public int DigestMismatches { get; private set; }

        public int GrantsReceived { get; private set; }

        /// <summary>The newest claim the host granted (for a hand-made <c>ngg.pg.bank</c>); 0 before any.</summary>
        public uint LastGrantedClaim { get; private set; }

        public int ShotsSent { get; private set; }

        public int RestsSent { get; private set; }

        public int InfosSent { get; private set; }

        public int WorldEventsReceived { get; private set; }

        public int StoryEventsReceived { get; private set; }

        public int PresenceEventsReceived { get; private set; }

        public BotClaims Claims => _claims;

        public IReadOnlyList<string> Notes => _notes;

        /// <summary>Something the tester should see in the results (joined, synced, grant, claim verdicts, ended).</summary>
        public event Action<FakePeer, string> Message;

        public void Connect(PeerId host)
        {
            EndInfo = null;
            Net.Connect(host);
            Say("connecting to " + host);
        }

        /// <summary>Leaves cleanly (the host hears a goodbye).</summary>
        public void Leave() => Net.Leave();

        /// <param name="anchor">The host's player (feet) position; the bot walks around it.</param>
        public void Update(double now, NetVector3 anchor, bool anchorValid)
        {
            if (_disposed)
                return;
            _now = now;
            // Shown only where the host has a world (no bundle: no Lifecycle on the host, the harness's pose decides).
            _avatar.SetAnchor(anchor, anchorValid && Net.IsReady && (HostScene == GameScene.Main || !Save.HasBundle));
            _avatar.Update(now);
            Net.Update(now);
            if (_disposed)
                return;
            if (Save.SnapshotsApplied != _snapshotsSeen)
            {
                // A resync brings a new bundle: its scene is the host's now.
                _snapshotsSeen = Save.SnapshotsApplied;
                TakeSceneFromBundle();
            }

            _motion.Clear();
            _avatar.DrainEvents(_motion);
            foreach (var e in _motion)
                SendMotion(e);
            SendInfo(now);

            _claims.Tick(now);
            _settled.Clear();
            _claims.Drain(_settled);
            foreach (var result in _settled)
                Say(result.ToString());
            ExpirePickups(now);
        }

        public bool Shoot(float power)
        {
            if (!Net.IsReady)
                return false;
            return _avatar.Shoot(_now, power);
        }

        // ---- Claims (facts) ---------------------------------------------------------------------------

        /// <summary>
        /// Claims a fact as a real client's Progression diff would (Core sends it as ProgressClaim). The verdict comes
        /// later through <see cref="Message"/>. False with <paramref name="error"/> when it cannot be sent.
        /// </summary>
        public bool ClaimFact(string kind, string key, byte[] value, out string error)
        {
            error = null;
            if (!Net.IsReady || _saveClient == null || !_saveClient.IsSynced)
            {
                error = "not synced with the host yet";
                return false;
            }
            var delta = new SaveDelta(kind, key ?? string.Empty, value ?? Array.Empty<byte>());
            if (!delta.IsValid)
            {
                error = "the fact does not fit a SaveDelta";
                return false;
            }
            var current = Save.Facts.Get(delta.Kind, delta.Key);
            if (current != null && FactTable.ValueEquals(current, delta.Value))
            {
                // Core does not send a value the host already confirmed.
                _claims.AlreadySet(delta);
                return true;
            }
            _claims.Sent(delta, _now);
            Save.Claim(delta.Kind, delta.Key, delta.Value);
            return true;
        }

        /// <summary><c>bo</c>: the bounty is done (on easy too when <paramref name="onEasy"/>); flags only add up.</summary>
        public bool ClaimBounty(string setName, bool onEasy, out string error)
        {
            byte flags = FactCodec.BountyDone;
            if (onEasy)
                flags |= FactCodec.BountyOnEasy;
            if (FactCodec.TryDecodeFlags(Save.Facts.Get(FactKinds.Bounty, setName ?? string.Empty), out byte old))
                flags |= old;
            return ClaimFact(FactKinds.Bounty, setName, FactCodec.EncodeFlags(flags), out error);
        }

        /// <summary>
        /// <c>track/main</c>: the main track at <paramref name="index"/> of <paramref name="setName"/> (null: the
        /// session's set), or one challenge further than the session's when <paramref name="index"/> is null (the
        /// first incomplete challenge after completing the current one).
        /// </summary>
        public bool ClaimTrack(string setName, int? index, out string error)
        {
            bool hasTrack = FactCodec.TryDecodeTrack(Save.Facts.Get(FactKinds.Track, FactKinds.TrackKey), out var track);
            string set = string.IsNullOrEmpty(setName) ? (hasTrack ? track.Set : null) : setName;
            if (string.IsNullOrEmpty(set))
            {
                error = "the session has no main track yet; name the set";
                return false;
            }
            int target = index ?? (hasTrack && track.Set == set ? track.Index + 1 : 1);
            if (target < 0)
            {
                error = "the index must not be negative";
                return false;
            }
            return ClaimFact(FactKinds.Track, FactKinds.TrackKey, FactCodec.EncodeTrack(new TrackValue(set, target)), out error);
        }

        public bool ClaimRunBool(string field, bool value, out string error) =>
            ClaimFact(FactKinds.RunBool, field, FactTable.EncodeBool(value), out error);

        public bool ClaimRunInt(string field, int value, out string error) =>
            ClaimFact(FactKinds.RunInt, field, FactTable.EncodeInt(value), out error);

        public bool ClaimObj(string saveableId, bool active, out string error) =>
            ClaimFact(FactKinds.Obj, saveableId, FactCodec.EncodeObj(new ObjState(active, false, NetQuaternion.Identity)), out error);

        // ---- Game RPCs --------------------------------------------------------------------------------

        /// <summary><c>ngg.pg.world</c> to Others (a door, gate or machine as the originator's postfix sends it).</summary>
        public bool SendWorld(WorldOp op, string id, bool flag) =>
            SendRpc(ProgressionRpc.World, RpcTarget.Others, w => BotWire.WriteWorld(w, op, id, flag));

        /// <summary><c>ngg.pg.story</c> (replicated, to Others) or <c>ngg.pg.storyHost</c> (host-executed, to the host).</summary>
        public bool SendStory(string method, bool hostExecuted) =>
            SendRpc(hostExecuted ? ProgressionRpc.StoryHost : ProgressionRpc.Story, hostExecuted ? RpcTarget.Host : RpcTarget.Others,
                w => BotWire.WriteMethod(w, method));

        /// <summary>Asks the host for a pickup or purchase; the grant comes through <see cref="Message"/>. 0 when not sent.</summary>
        public uint SendPickup(PickupRequest request)
        {
            if (request == null || string.IsNullOrEmpty(request.InteractId))
                return 0;
            unchecked
            {
                _nextPickupId++;
                if (_nextPickupId == 0)
                    _nextPickupId = 1;
            }
            uint claimId = _nextPickupId;
            if (!SendRpc(ProgressionRpc.Claim, RpcTarget.Host, w => BotWire.WriteClaim(w, claimId, request.InteractId, request.Interaction)))
                return 0;
            _pickups[claimId] = new PendingPickup(request, _now);
            return claimId;
        }

        /// <summary><c>ngg.pg.bank</c>: a money change under claim <paramref name="claimId"/> (0: the newest granted one).</summary>
        public bool SendBank(uint claimId, int change)
        {
            uint id = claimId != 0 ? claimId : LastGrantedClaim;
            return SendRpc(ProgressionRpc.Bank, RpcTarget.Host, w => BotWire.WriteBank(w, id, change));
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                Net.Dispose();
            }
            catch (Exception e)
            {
                NetLog.Error($"[TestHarness] Disposing {Name}'s session failed: {e}");
            }
            DetachSaveClient();
            Save.DeltaApplied -= OnDeltaApplied;
        }

        // ---- Session events ---------------------------------------------------------------------------

        private void OnStarted()
        {
            DetachSaveClient();
            _saveClient = Net.SaveClient;
            if (_saveClient != null)
                _saveClient.ClaimRejected += _onClaimRejected;
            Say("joined " + Net.Session.HostId + " as player #" + Net.Session.LocalPlayer?.PlayerIndex);
        }

        private void TakeSceneFromBundle()
        {
            var bundle = Save.Bundle;
            if (bundle == null)
                return;
            HostScene = bundle.HostScene;
            SceneEpoch = bundle.SceneEpoch;
            Mode = bundle.Mode;
        }

        private void OnReady()
        {
            _snapshotsSeen = Save.SnapshotsApplied;
            TakeSceneFromBundle();
            var bundle = Save.Bundle;
            Say("save synced: " + (bundle != null
                ? $"bundle, host in {bundle.HostScene} (epoch {bundle.SceneEpoch}, mode {(bundle.IsJustGolf ? "JustGolf" : "Story")}), " +
                  $"run {bundle.RunJson.Length} bytes"
                : "bare fact table") + $", {Save.Facts.Count} facts (kept in memory only)");
            _sentInfo = false;
        }

        private void OnEnded(SessionEndInfo info)
        {
            EndInfo = info;
            _claims.Abandon(_now);
            _settled.Clear();
            _claims.Drain(_settled);
            foreach (var result in _settled)
                Say(result.ToString());
            _pickups.Clear();
            _avatar.ResetBall();
            HostScene = GameScene.None;
            DetachSaveClient();
            Say("session ended: " + info);
        }

        private void DetachSaveClient()
        {
            if (_saveClient != null)
                _saveClient.ClaimRejected -= _onClaimRejected;
            _saveClient = null;
        }

        private void OnDeltaApplied(SaveDelta delta) => _claims.OnDelta(delta, _now);

        private void OnClaimRejected(SaveDelta claim, string reason) => _claims.OnRejected(claim, reason, _now);

        // ---- RPCs -------------------------------------------------------------------------------------

        private void RegisterRpcs()
        {
            // Every name a client can receive (2.2); the host only relays what the receiver registered handling for.
            Net.RegisterRpc(LifecycleRpcs.Scene, OnScene);
            Net.RegisterRpc(PresenceRpc.Wind, OnWind);
            Net.RegisterRpc(PresenceRpc.Info, OnPresence);
            Net.RegisterRpc(PresenceRpc.Shot, OnPresence);
            Net.RegisterRpc(PresenceRpc.Rest, OnPresence);
            Net.RegisterRpc(PresenceRpc.Holed, OnPresence);
            Net.RegisterRpc(PresenceRpc.Round, OnPresence);
            Net.RegisterRpc(PresenceRpc.Hole, OnPresence);
            Net.RegisterRpc(PresenceRpc.Card, OnPresence);
            Net.RegisterRpc(ProgressionRpc.World, (s, r) => WorldEventsReceived++);
            Net.RegisterRpc(ProgressionRpc.Story, (s, r) => StoryEventsReceived++);
            Net.RegisterRpc(ProgressionRpc.Grant, OnGrant);
            Net.RegisterRpc(ProgressionRpc.Note, OnNote);
            Net.RegisterRpc(ProgressionRpc.Digest, OnDigest);
        }

        private void OnScene(PeerId sender, NetReader reader)
        {
            var notice = LifecycleRpcs.ReadScene(reader);
            if (sender != Net.Session.HostId)
                return;
            HostScene = notice.Scene;
            SceneEpoch = notice.Epoch;
            Mode = notice.Mode;
            SceneNotices++;
            if (HostScene != GameScene.Main)
                _avatar.ResetBall();
            Say($"host is loading {notice.Scene} (epoch {notice.Epoch})");
        }

        private void OnWind(PeerId sender, NetReader reader)
        {
            var wind = WindMessage.Read(reader);
            if (sender != Net.Session.HostId)
                return;
            LastWind = wind;
            HasWind = true;
            LastWindAt = _now;
            WindMessages++;
        }

        private void OnPresence(PeerId sender, NetReader reader) => PresenceEventsReceived++;

        private void OnGrant(PeerId sender, NetReader reader)
        {
            var grant = BotWire.ReadGrant(reader);
            if (sender != Net.Session.HostId)
                return;
            GrantsReceived++;
            if (!_pickups.TryGetValue(grant.ClaimId, out var pending))
            {
                Say($"grant for unknown claim {grant.ClaimId} ({(grant.Granted ? "granted" : "refused")})");
                return;
            }
            _pickups.Remove(grant.ClaimId);
            var request = pending.Request;
            string what = $"{BotInteractions.NameOf(request.Interaction)} {request.InteractId}";
            if (!grant.Granted)
            {
                Say($"pickup claim {grant.ClaimId} for {what} REFUSED: {grant.Reason} (host balance {grant.HostBalance})");
                return;
            }
            LastGrantedClaim = grant.ClaimId;
            Say($"pickup claim {grant.ClaimId} for {what} GRANTED (host balance {grant.HostBalance})");
            // What a real client's granted Interact does next: one money change (reported), and the object it took
            // disappears from its world (its diff claims the SaveableObject inactive).
            if (request.ReportMoney)
            {
                int? change = BotInteractions.ExpectedBankReport(request.Interaction, request.CashValue, grant.HostBalance);
                if (change.HasValue)
                {
                    bool sent = SendBank(grant.ClaimId, change.Value);
                    Say($"bank report {change.Value:+#;-#;0} under claim {grant.ClaimId}{(sent ? " sent" : " NOT sent")}");
                }
            }
            if (!string.IsNullOrEmpty(request.SaveableId) && BotInteractions.HidesObject(request.Interaction))
            {
                if (!ClaimObj(request.SaveableId, false, out string error))
                    Say($"could not claim obj {request.SaveableId} inactive: {error}");
            }
        }

        private void OnNote(PeerId sender, NetReader reader)
        {
            var note = BotWire.ReadNote(reader);
            if (sender != Net.Session.HostId)
                return;
            if (_notes.Count >= MaxNotes)
                _notes.RemoveAt(0);
            _notes.Add(note.Text);
            Say($"note (kind {note.Kind}, by {note.By}): {note.Text}");
        }

        private void OnDigest(PeerId sender, NetReader reader)
        {
            var digest = BotWire.ReadDigest(reader);
            if (sender != Net.Session.HostId)
                return;
            var client = _saveClient;
            // Only comparable at the same revision, as the Progression module's own check (6.13).
            if (client == null || !client.IsSynced || client.Revision != digest.Revision)
                return;
            if ((uint)Save.Facts.Count == digest.Count && Save.Facts.ComputeCrc() == digest.Crc)
            {
                DigestMatches++;
                return;
            }
            DigestMismatches++;
            Say($"DIGEST MISMATCH at r{digest.Revision}: host {digest.Count} facts crc {digest.Crc:X8}, " +
                $"bot {Save.Facts.Count} facts crc {Save.Facts.ComputeCrc():X8}");
            if (_now - _lastDigestResync >= DigestResyncCooldown)
            {
                _lastDigestResync = _now;
                client.RequestResync();
            }
        }

        // ---- Sending ----------------------------------------------------------------------------------

        private void SendMotion(BotMotionEvent e)
        {
            if (e.Kind == BotMotionEventKind.ShotLaunched)
            {
                var shot = new ShotMessage
                {
                    Club = _options.Club,
                    Power = e.Power,
                    ShotType = _options.ShotType,
                    SpinType = 0,
                    Stroke = e.Stroke,
                    IsMiss = false,
                    From = e.Position
                };
                if (SendRpc(PresenceRpc.Shot, RpcTarget.Others, w => shot.Write(w)))
                    ShotsSent++;
                _sentInfo = false;
                return;
            }
            var rest = new RestMessage
            {
                Position = e.Position,
                Distance = e.Distance,
                Surface = _options.Surface,
                Stroke = e.Stroke
            };
            if (SendRpc(PresenceRpc.Rest, RpcTarget.Others, w => rest.Write(w)))
                RestsSent++;
            _sentInfo = false;
        }

        private void SendInfo(double now)
        {
            if (!Net.IsReady)
                return;
            var info = new InfoMessage
            {
                Scene = (byte)HostScene,
                ControlMode = _avatar.Golfing ? InfoMessage.Golf : InfoMessage.Walk,
                SelectedBall = 0,
                Club = _avatar.Golfing ? _options.Club : string.Empty,
                Activity = "Test bot"
            };
            if (_sentInfo && info.Equals(_lastInfo) && now - _lastInfoAt < InfoInterval)
                return;
            if (SendRpc(PresenceRpc.Info, RpcTarget.Others, w => info.Write(w)))
            {
                InfosSent++;
                _lastInfo = info;
                _sentInfo = true;
                _lastInfoAt = now;
            }
        }

        private bool SendRpc(string name, RpcTarget target, Action<NetWriter> write)
        {
            var rpc = Net.Rpc;
            if (rpc == null || !Net.IsReady)
                return false;
            try
            {
                return rpc.Send(name, target, write);
            }
            catch (Exception e)
            {
                NetLog.Error($"[TestHarness] {Name} could not send {name}: {e}");
                return false;
            }
        }

        private void ExpirePickups(double now)
        {
            if (_pickups.Count == 0)
                return;
            _scratch.Clear();
            foreach (var pair in _pickups)
            {
                if (now - pair.Value.At >= PickupTimeoutSeconds)
                    _scratch.Add(pair.Key);
            }
            foreach (uint id in _scratch)
            {
                Say($"pickup claim {id} for {_pickups[id].Request.InteractId}: no answer from the host");
                _pickups.Remove(id);
            }
        }

        private void Say(string text)
        {
            var handler = Message;
            if (handler == null)
                return;
            try
            {
                handler(this, text);
            }
            catch (Exception e)
            {
                NetLog.Error($"[TestHarness] Message handler threw: {e}");
            }
        }

        private static string Truncate(string value, int maxChars) =>
            value.Length <= maxChars ? value : value.Substring(0, maxChars);

        private readonly struct PendingPickup
        {
            public PendingPickup(PickupRequest request, double at)
            {
                Request = request;
                At = at;
            }

            public readonly PickupRequest Request;
            public readonly double At;
        }

        /// <summary>Gives the bot's own player and ball entities their poses; everyone else's are tracked without objects.</summary>
        private sealed class EntityFactory : INetEntityFactory
        {
            private readonly FakePeer _peer;

            public EntityFactory(FakePeer peer)
            {
                _peer = peer;
            }

            public INetEntity CreateEntity(NetEntity entity)
            {
                if (!entity.IsLocallyOwned)
                    return null;
                if (entity.PrefabKey == GameEntityKeys.Player)
                    return new PoseEntity(_peer._avatar, true);
                if (entity.PrefabKey == GameEntityKeys.Ball)
                    return new PoseEntity(_peer._avatar, false);
                return null;
            }
        }

        private sealed class PoseEntity : INetEntity
        {
            private readonly BotAvatar _avatar;
            private readonly bool _player;

            public PoseEntity(BotAvatar avatar, bool player)
            {
                _avatar = avatar;
                _player = player;
            }

            public bool CaptureState(out TransformState state)
            {
                state = _player ? _avatar.CapturePlayer() : _avatar.CaptureBall();
                return true;
            }

            public void ApplyState(in TransformState state, SnapshotSampleKind kind)
            {
            }

            public void OnOwnershipChanged(PeerId owner, bool isLocal)
            {
            }

            public void OnDespawned(DespawnReason reason)
            {
            }
        }
    }
}
