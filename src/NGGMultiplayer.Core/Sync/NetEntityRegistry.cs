using System;
using System.Collections.Generic;
using System.Text;
using NGGMultiplayer.Core.Diagnostics;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Session;

namespace NGGMultiplayer.Core.Sync
{
    /// <summary>
    /// Replicates networked entities across the session.
    /// <list type="bullet">
    /// <item>The host allocates ids and is the only peer that spawns, despawns or reassigns ownership. Clients
    /// mirror what the host announces and ask for ownership with <see cref="RequestOwnership"/>.</item>
    /// <item>Each entity's owner captures its state at <see cref="NetEntityRegistryOptions.SendRate"/> and sends
    /// it unreliably; the host relays client-owned states to the other clients. Everyone else interpolates.</item>
    /// <item>When an owner leaves, the host despawns its entities flagged
    /// <see cref="NetEntityFlags.DespawnOnOwnerLeave"/> and takes ownership of the rest.</item>
    /// </list>
    /// Create one registry per session, after the local role is known. Drive it with <see cref="Update"/>, passing
    /// the session's host-synchronized clock (seconds), so state timestamps share one time base across peers.
    /// Not thread-safe.
    /// </summary>
    public sealed class NetEntityRegistry : IDisposable
    {
        private const string Log = "[EntitySync] ";

        // Host: states stamped further than this ahead of the host clock are rejected.
        private const double MaxFutureStateSeconds = 5.0;

        private readonly ISessionMessaging _messaging;
        private readonly INetEntityFactory _factory;
        private readonly NetEntityRegistryOptions _options;
        private readonly bool _isHost;
        private readonly PeerId _localId;
        private readonly PeerId _hostId;
        private readonly double _sendInterval;

        private readonly Dictionary<NetEntityId, NetEntity> _entities = new Dictionary<NetEntityId, NetEntity>();
        private readonly List<NetEntity> _ordered = new List<NetEntity>();
        private readonly List<NetEntity> _scratch = new List<NetEntity>();
        private readonly StateBatch _sendBatch = new StateBatch();
        private readonly StateBatch _relayBatch = new StateBatch();
        private readonly NetWriter _entryWriter = new NetWriter(TransformCodec.MaxEncodedSize + 16);

        private readonly Action<PeerId, NetReader> _onSpawn;
        private readonly Action<PeerId, NetReader> _onDespawn;
        private readonly Action<PeerId, NetReader> _onState;
        private readonly Action<PeerId, NetReader> _onRelay;
        private readonly Action<PeerId, NetReader> _onOwnershipChange;
        private readonly Action<PeerId, NetReader> _onOwnershipRequest;
        private readonly Action<PeerId, NetReader> _onOwnershipRelease;
        private readonly Action<PeerId, NetReader> _onOwnershipDenied;

        private uint _nextId = 1;
        private double _now;
        private bool _hasTime;
        private double _nextSendAt;
        private bool _closed;
        private bool _disposed;

        /// <param name="factory">Creates game objects for announced entities. May be null on a host that always passes instances.</param>
        public NetEntityRegistry(ISessionMessaging messaging, INetEntityFactory factory = null, NetEntityRegistryOptions options = null)
        {
            _messaging = messaging ?? throw new ArgumentNullException(nameof(messaging));
            _factory = factory;
            _options = (options ?? new NetEntityRegistryOptions()).Validated();
            _isHost = messaging.IsHost;
            _localId = messaging.LocalId;
            _hostId = messaging.HostId;
            if (!_localId.IsValid || !_hostId.IsValid)
                throw new ArgumentException("The session must have valid local and host ids.", nameof(messaging));
            _sendInterval = 1.0 / _options.SendRate;

            _onSpawn = OnSpawn;
            _onDespawn = OnDespawn;
            _onState = OnState;
            _onRelay = OnStateRelay;
            _onOwnershipChange = OnOwnershipChange;
            _onOwnershipRequest = OnOwnershipRequest;
            _onOwnershipRelease = OnOwnershipRelease;
            _onOwnershipDenied = OnOwnershipDenied;
            _messaging.RegisterHandler(MessageType.Spawn, _onSpawn);
            _messaging.RegisterHandler(MessageType.Despawn, _onDespawn);
            _messaging.RegisterHandler(MessageType.State, _onState);
            _messaging.RegisterHandler(MessageType.StateRelayBatch, _onRelay);
            _messaging.RegisterHandler(MessageType.OwnershipChange, _onOwnershipChange);
            _messaging.RegisterHandler(MessageType.OwnershipRequest, _onOwnershipRequest);
            _messaging.RegisterHandler(MessageType.OwnershipRelease, _onOwnershipRelease);
            _messaging.RegisterHandler(MessageType.OwnershipDenied, _onOwnershipDenied);
            _messaging.PlayerJoined += OnPlayerJoined;
            _messaging.PlayerLeft += OnPlayerLeft;
            _messaging.SessionClosed += OnSessionClosed;
        }

        public bool IsHost => _isHost;

        public PeerId LocalId => _localId;

        public int Count => _entities.Count;

        /// <summary>False once the session closed or the registry was disposed.</summary>
        public bool IsActive => !_closed && !_disposed;

        /// <summary>Entities in spawn order. Do not spawn or despawn while enumerating.</summary>
        public IReadOnlyList<NetEntity> Entities => _ordered;

        /// <summary>
        /// Host: decides client ownership requests (requester, entity). Null applies the default policy: grant when
        /// the host currently owns the entity and it is not <see cref="NetEntityFlags.OwnershipLocked"/>.
        /// </summary>
        public Func<PeerId, NetEntity, bool> OwnershipValidator { get; set; }

        public long StatesSent { get; private set; }

        public long StatesReceived { get; private set; }

        public long StatesRejected { get; private set; }

        public long StatesRelayed { get; private set; }

        public event Action<NetEntity> EntitySpawned;

        public event Action<NetEntity, DespawnReason> EntityDespawned;

        /// <summary>(entity, previous owner).</summary>
        public event Action<NetEntity, PeerId> OwnershipChanged;

        /// <summary>Client: the host refused our ownership request (entity, current owner).</summary>
        public event Action<NetEntity, PeerId> OwnershipDenied;

        public bool TryGet(NetEntityId id, out NetEntity entity) => _entities.TryGetValue(id, out entity);

        public NetEntity Get(NetEntityId id) => _entities.TryGetValue(id, out var e) ? e : null;

        /// <summary>
        /// Host: creates an entity and announces it. <paramref name="initialState"/> is stamped with the registry
        /// clock. When <paramref name="instance"/> is null the factory builds the host's object.
        /// </summary>
        public NetEntity Spawn(string prefabKey, PeerId owner, in TransformState initialState,
            NetEntityFlags flags = NetEntityFlags.None, byte[] spawnData = null, INetEntity instance = null)
        {
            RequireHost(nameof(Spawn));
            if (string.IsNullOrEmpty(prefabKey))
                throw new ArgumentException("Prefab key must not be empty.", nameof(prefabKey));
            if (Encoding.UTF8.GetByteCount(prefabKey) > SyncProtocol.MaxPrefabKeyBytes)
                throw new ArgumentException($"Prefab key exceeds {SyncProtocol.MaxPrefabKeyBytes} UTF-8 bytes.", nameof(prefabKey));
            if ((flags & ~NetEntityFlags.All) != 0)
                throw new ArgumentOutOfRangeException(nameof(flags), flags, "Unknown entity flags.");
            if (spawnData != null && spawnData.Length > SyncProtocol.MaxSpawnDataBytes)
                throw new ArgumentException($"Spawn data exceeds {SyncProtocol.MaxSpawnDataBytes} bytes.", nameof(spawnData));
            if (!initialState.IsValid)
                throw new ArgumentException("Initial state contains non-finite or out-of-range values or an oversized extra payload.", nameof(initialState));
            RequireReachable(owner, nameof(owner));
            if (_entities.Count >= _options.MaxEntities)
                throw new InvalidOperationException($"Entity limit of {_options.MaxEntities} reached.");

            var entity = new NetEntity(AllocateId(), prefabKey, owner, 0, flags,
                spawnData == null || spawnData.Length == 0 ? Array.Empty<byte>() : (byte[])spawnData.Clone(),
                _localId, _options.SnapshotCapacity, _options.MaxExtrapolation);
            var state = initialState;
            state.Time = _now;
            entity.LastState = state;
            if (!entity.IsLocallyOwned)
                entity.Snapshots.Add(state);
            Track(entity);
            entity.Instance = instance ?? CreateInstance(entity);

            using (var packet = PacketBuilder.Rent(MessageType.Spawn))
            {
                WriteSpawn(packet.Writer, entity);
                _messaging.Broadcast(SyncProtocol.EventsChannel, Reliability.Reliable, packet.Segment, PeerId.None);
            }
            NetLog.Info($"{Log}Spawned {entity}.");
            SyncSafeInvoke.Raise(EntitySpawned, entity, nameof(EntitySpawned));
            return entity;
        }

        /// <summary>Host: removes an entity everywhere. False if it does not exist.</summary>
        public bool Despawn(NetEntityId id)
        {
            RequireHost(nameof(Despawn));
            return DespawnInternal(id, DespawnReason.Despawned);
        }

        /// <summary>Host: hands an entity to <paramref name="owner"/> (the host or a connected peer). False if it does not exist.</summary>
        public bool SetOwner(NetEntityId id, PeerId owner)
        {
            RequireHost(nameof(SetOwner));
            RequireReachable(owner, nameof(owner));
            if (!_entities.TryGetValue(id, out var entity))
                return false;
            if (entity.Owner != owner)
                ChangeOwner(entity, owner, unchecked((byte)(entity.OwnerEpoch + 1)));
            return true;
        }

        /// <summary>
        /// Asks to own an entity. On the host this takes ownership immediately; on a client it sends a request the
        /// host grants (<see cref="OwnershipChanged"/>) or refuses (<see cref="OwnershipDenied"/>). False if unknown.
        /// </summary>
        public bool RequestOwnership(NetEntityId id)
        {
            RequireActive();
            if (!_entities.TryGetValue(id, out var entity))
                return false;
            if (_isHost)
            {
                if (!entity.IsLocallyOwned)
                    ChangeOwner(entity, _localId, unchecked((byte)(entity.OwnerEpoch + 1)));
                return true;
            }
            if (entity.IsLocallyOwned && !entity.ReleasePending)
                return true;
            SendIdTo(_hostId, MessageType.OwnershipRequest, id);
            return true;
        }

        /// <summary>
        /// Client: gives a locally owned entity back to the host and stops sending its state right away. False if it
        /// is unknown or not ours. The host is the fallback owner, so on the host this always returns false.
        /// </summary>
        public bool ReleaseOwnership(NetEntityId id)
        {
            RequireActive();
            if (_isHost || !_entities.TryGetValue(id, out var entity) || !entity.IsLocallyOwned)
                return false;
            if (!entity.ReleasePending)
            {
                entity.ReleasePending = true;
                SendIdTo(_hostId, MessageType.OwnershipRelease, id);
            }
            return true;
        }

        /// <summary>Host: sends a Spawn for every existing entity to <paramref name="peer"/> (late join).</summary>
        public void ReplicateAllTo(PeerId peer)
        {
            RequireHost(nameof(ReplicateAllTo));
            if (!_messaging.IsPeerConnected(peer))
                throw new ArgumentException($"Peer {peer} is not connected.", nameof(peer));
            for (int i = 0; i < _ordered.Count; i++)
            {
                using (var packet = PacketBuilder.Rent(MessageType.Spawn))
                {
                    WriteSpawn(packet.Writer, _ordered[i]);
                    _messaging.SendTo(peer, SyncProtocol.EventsChannel, Reliability.Reliable, packet.Segment);
                }
            }
            if (_ordered.Count > 0)
                NetLog.Info($"{Log}Replicated {_ordered.Count} entities to {peer}.");
        }

        /// <summary>Sends owned states when due and applies interpolated states to remote-owned entities.</summary>
        public void Update(double now)
        {
            if (_disposed || _closed)
                return;
            if (double.IsNaN(now) || double.IsInfinity(now))
                throw new ArgumentOutOfRangeException(nameof(now), now, "Clock must be finite.");
            _now = now;

            // The host clock estimate can step backwards when it is corrected; do not stall sending until it catches up.
            if (_hasTime && now < _nextSendAt - _sendInterval)
                _nextSendAt = now;
            if (!_hasTime || now >= _nextSendAt)
            {
                _nextSendAt = !_hasTime || now - _nextSendAt >= _sendInterval ? now + _sendInterval : _nextSendAt + _sendInterval;
                _hasTime = true;
                SendOwnedStates();
            }
            if (_options.AutoApply)
                ApplyRemoteStates(now - _options.InterpolationDelay);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _messaging.PlayerJoined -= OnPlayerJoined;
            _messaging.PlayerLeft -= OnPlayerLeft;
            _messaging.SessionClosed -= OnSessionClosed;
            _messaging.UnregisterHandler(MessageType.Spawn);
            _messaging.UnregisterHandler(MessageType.Despawn);
            _messaging.UnregisterHandler(MessageType.State);
            _messaging.UnregisterHandler(MessageType.StateRelayBatch);
            _messaging.UnregisterHandler(MessageType.OwnershipChange);
            _messaging.UnregisterHandler(MessageType.OwnershipRequest);
            _messaging.UnregisterHandler(MessageType.OwnershipRelease);
            _messaging.UnregisterHandler(MessageType.OwnershipDenied);
            DropAll(DespawnReason.Disposed);
            _disposed = true;
        }

        // ---- Sending ---------------------------------------------------------------------------------

        private void SendOwnedStates()
        {
            _scratch.Clear();
            for (int i = 0; i < _ordered.Count; i++)
            {
                var e = _ordered[i];
                if (e.IsLocallyOwned && !e.ReleasePending && e.Instance != null)
                    _scratch.Add(e);
            }
            if (_scratch.Count == 0)
                return;

            // Capture first: user code must not run while the batch writer is half-built.
            for (int i = 0; i < _scratch.Count; i++)
            {
                var e = _scratch[i];
                if (!e.IsSpawned || !e.IsLocallyOwned || !TryCapture(e, out var state))
                {
                    _scratch[i] = null;
                    continue;
                }
                e.LastState = state;
            }

            byte type = MessageType.State;
            _sendBatch.Begin(type);
            for (int i = 0; i < _scratch.Count; i++)
            {
                var e = _scratch[i];
                if (e == null || !e.IsSpawned || !e.IsLocallyOwned)
                    continue;
                WriteEntry(e.Id, e.OwnerEpoch, e.LastState);
                if (!_sendBatch.TryAppend(_entryWriter))
                {
                    SendStateBatch(_sendBatch);
                    _sendBatch.Begin(type);
                    _sendBatch.TryAppend(_entryWriter);
                }
            }
            SendStateBatch(_sendBatch);
            _scratch.Clear();
        }

        private bool TryCapture(NetEntity entity, out TransformState state)
        {
            bool captured;
            try
            {
                captured = entity.Instance.CaptureState(out state);
            }
            catch (Exception e)
            {
                SyncSafeInvoke.Fail($"CaptureState of {entity}", e);
                state = default;
                return false;
            }
            if (!captured)
                return false;
            state.Time = _now;
            if (!state.IsValid)
            {
                if (!entity.LoggedInvalidState)
                {
                    entity.LoggedInvalidState = true;
                    NetLog.Warn($"{Log}{entity} captured an invalid state (non-finite or out-of-range values, or oversized extra); not sending it.");
                }
                return false;
            }
            return true;
        }

        private void SendStateBatch(StateBatch batch)
        {
            if (batch.Count == 0)
                return;
            var segment = batch.Finish();
            if (_isHost)
                _messaging.Broadcast(SyncProtocol.StateChannel, Reliability.Unreliable, segment, PeerId.None);
            else
                _messaging.SendTo(_hostId, SyncProtocol.StateChannel, Reliability.Unreliable, segment);
            StatesSent += batch.Count;
        }

        private void WriteEntry(NetEntityId id, byte epoch, in TransformState state)
        {
            _entryWriter.Reset();
            NetEntityId.Write(_entryWriter, id);
            _entryWriter.WriteByte(epoch);
            TransformCodec.Write(_entryWriter, state, _options.Encoding);
        }

        private void ApplyRemoteStates(double renderTime)
        {
            _scratch.Clear();
            for (int i = 0; i < _ordered.Count; i++)
            {
                var e = _ordered[i];
                if (!e.IsLocallyOwned && e.Instance != null)
                    _scratch.Add(e);
            }
            for (int i = 0; i < _scratch.Count; i++)
            {
                var e = _scratch[i];
                if (!e.IsSpawned || e.IsLocallyOwned)
                    continue;
                var kind = e.Snapshots.Sample(renderTime, out var state);
                if (kind == SnapshotSampleKind.None)
                    continue;
                try
                {
                    e.Instance.ApplyState(state, kind);
                }
                catch (Exception ex)
                {
                    SyncSafeInvoke.Fail($"ApplyState of {e}", ex);
                }
            }
            _scratch.Clear();
        }

        // ---- Packet handlers -------------------------------------------------------------------------

        private void OnSpawn(PeerId from, NetReader reader)
        {
            if (!AcceptFromHost(from, MessageType.Spawn))
                return;
            NetEntityId id;
            string prefabKey;
            PeerId owner;
            byte epoch;
            NetEntityFlags flags;
            byte[] spawnData;
            TransformState state;
            try
            {
                id = NetEntityId.Read(reader);
                prefabKey = reader.ReadString(SyncProtocol.MaxPrefabKeyBytes);
                if (prefabKey.Length == 0)
                    throw new ProtocolException("Empty prefab key.");
                owner = reader.ReadPeerId();
                if (!owner.IsValid)
                    throw new ProtocolException("Spawn owner is not a valid peer.");
                epoch = reader.ReadByte();
                byte rawFlags = reader.ReadByte();
                if ((rawFlags & ~(byte)NetEntityFlags.All) != 0)
                    throw new ProtocolException($"Unknown entity flags 0x{rawFlags:X2}.");
                flags = (NetEntityFlags)rawFlags;
                spawnData = reader.ReadBytes(SyncProtocol.MaxSpawnDataBytes);
                state = TransformCodec.Read(reader);
            }
            catch (ProtocolException e)
            {
                NetLog.Warn($"{Log}Malformed Spawn from {from}: {e.Message}");
                return;
            }

            if (_entities.ContainsKey(id))
            {
                NetLog.Warn($"{Log}Ignoring duplicate Spawn of {id}.");
                return;
            }
            if (_entities.Count >= _options.MaxEntities)
            {
                NetLog.Error($"{Log}Ignoring Spawn of {id}: entity limit of {_options.MaxEntities} reached.");
                return;
            }

            var entity = new NetEntity(id, prefabKey, owner, epoch, flags, spawnData, _localId,
                _options.SnapshotCapacity, _options.MaxExtrapolation);
            entity.LastState = state;
            if (!entity.IsLocallyOwned)
                entity.Snapshots.Add(state);
            Track(entity);
            entity.Instance = CreateInstance(entity);
            if (NetLog.IsEnabled(LogLevel.Debug))
                NetLog.Debug($"{Log}Mirrored spawn of {entity}.");
            SyncSafeInvoke.Raise(EntitySpawned, entity, nameof(EntitySpawned));
        }

        private void OnDespawn(PeerId from, NetReader reader)
        {
            if (!AcceptFromHost(from, MessageType.Despawn))
                return;
            NetEntityId id;
            DespawnReason reason;
            try
            {
                id = NetEntityId.Read(reader);
                byte raw = reader.ReadByte();
                if (raw != (byte)DespawnReason.Despawned && raw != (byte)DespawnReason.OwnerLeft)
                    throw new ProtocolException($"Invalid despawn reason {raw}.");
                reason = (DespawnReason)raw;
            }
            catch (ProtocolException e)
            {
                NetLog.Warn($"{Log}Malformed Despawn from {from}: {e.Message}");
                return;
            }
            if (!_entities.TryGetValue(id, out var entity))
                return;
            Untrack(entity, reason);
        }

        private void OnOwnershipChange(PeerId from, NetReader reader)
        {
            if (!AcceptFromHost(from, MessageType.OwnershipChange))
                return;
            NetEntityId id;
            PeerId owner;
            byte epoch;
            try
            {
                id = NetEntityId.Read(reader);
                owner = reader.ReadPeerId();
                if (!owner.IsValid)
                    throw new ProtocolException("Owner is not a valid peer.");
                epoch = reader.ReadByte();
            }
            catch (ProtocolException e)
            {
                NetLog.Warn($"{Log}Malformed OwnershipChange from {from}: {e.Message}");
                return;
            }
            if (_entities.TryGetValue(id, out var entity))
                ChangeOwner(entity, owner, epoch);
        }

        private void OnState(PeerId from, NetReader reader)
        {
            if (_disposed || _closed)
                return;
            if (_isHost)
                ReceiveClientStates(from, reader);
            else if (from == _hostId)
                ReceiveHostStates(reader);
            else
                NetLog.Warn($"{Log}Ignoring State from non-host peer {from}.");
        }

        private void OnStateRelay(PeerId from, NetReader reader)
        {
            if (_disposed || _closed)
                return;
            if (_isHost || from != _hostId)
            {
                NetLog.Warn($"{Log}Ignoring StateRelayBatch from {from}; only the host relays states.");
                return;
            }
            ReceiveHostStates(reader);
        }

        private void ReceiveHostStates(NetReader reader)
        {
            try
            {
                int count = ReadBatchCount(reader);
                for (int i = 0; i < count; i++)
                {
                    var id = NetEntityId.Read(reader);
                    byte epoch = reader.ReadByte();
                    var state = TransformCodec.Read(reader);
                    if (!_entities.TryGetValue(id, out var entity) || entity.IsLocallyOwned || entity.OwnerEpoch != epoch)
                    {
                        StatesRejected++;
                        continue;
                    }
                    StoreRemoteState(entity, state);
                }
            }
            catch (ProtocolException e)
            {
                NetLog.Warn($"{Log}Malformed state batch from host: {e.Message}");
            }
        }

        private void ReceiveClientStates(PeerId from, NetReader reader)
        {
            _relayBatch.Begin(MessageType.StateRelayBatch);
            try
            {
                int count = ReadBatchCount(reader);
                for (int i = 0; i < count; i++)
                {
                    var id = NetEntityId.Read(reader);
                    byte epoch = reader.ReadByte();
                    var state = TransformCodec.Read(reader);
                    if (!_entities.TryGetValue(id, out var entity) || entity.Owner != from || entity.OwnerEpoch != epoch
                        || (_hasTime && state.Time > _now + MaxFutureStateSeconds))
                    {
                        StatesRejected++;
                        continue;
                    }
                    StoreRemoteState(entity, state);

                    // Re-encode rather than forward the client's bytes: only validated data is relayed.
                    WriteEntry(id, epoch, state);
                    if (!_relayBatch.TryAppend(_entryWriter))
                    {
                        Relay(from);
                        _relayBatch.Begin(MessageType.StateRelayBatch);
                        _relayBatch.TryAppend(_entryWriter);
                    }
                }
            }
            catch (ProtocolException e)
            {
                NetLog.Warn($"{Log}Malformed State from {from}: {e.Message}");
            }
            finally
            {
                Relay(from);
            }
        }

        private void Relay(PeerId except)
        {
            if (_relayBatch.Count == 0)
                return;
            int n = _relayBatch.Count;
            _messaging.Broadcast(SyncProtocol.StateChannel, Reliability.Unreliable, _relayBatch.Finish(), except);
            _relayBatch.Begin(MessageType.StateRelayBatch);
            StatesRelayed += n;
        }

        private void StoreRemoteState(NetEntity entity, in TransformState state)
        {
            StatesReceived++;
            if (!entity.Snapshots.Add(state))
                return;
            if (state.Time >= entity.LastState.Time)
                entity.LastState = state;
        }

        private static int ReadBatchCount(NetReader reader)
        {
            int count = reader.ReadByte();
            if (count == 0)
                throw new ProtocolException("Empty state batch.");
            return count;
        }

        private void OnOwnershipRequest(PeerId from, NetReader reader)
        {
            if (!AcceptFromClient(from, MessageType.OwnershipRequest, reader, out var id))
                return;
            if (!_entities.TryGetValue(id, out var entity))
            {
                SendDenied(from, id, PeerId.None);
                return;
            }
            if (entity.Owner == from)
                return;

            bool grant;
            try
            {
                var validator = OwnershipValidator;
                grant = validator != null
                    ? validator(from, entity)
                    : entity.Owner == _localId && (entity.Flags & NetEntityFlags.OwnershipLocked) == 0;
            }
            catch (Exception e)
            {
                SyncSafeInvoke.Fail(nameof(OwnershipValidator), e);
                grant = false;
            }
            // The validator may have despawned it, or the requester may have left meanwhile.
            if (grant && entity.IsSpawned && _messaging.IsPeerConnected(from))
            {
                if (entity.Owner != from)
                    ChangeOwner(entity, from, unchecked((byte)(entity.OwnerEpoch + 1)));
                return;
            }
            NetLog.Info($"{Log}Denied ownership of {id} to {from}.");
            SendDenied(from, id, entity.IsSpawned ? entity.Owner : PeerId.None);
        }

        private void OnOwnershipRelease(PeerId from, NetReader reader)
        {
            if (!AcceptFromClient(from, MessageType.OwnershipRelease, reader, out var id))
                return;
            if (_entities.TryGetValue(id, out var entity) && entity.Owner == from)
                ChangeOwner(entity, _localId, unchecked((byte)(entity.OwnerEpoch + 1)));
        }

        private void OnOwnershipDenied(PeerId from, NetReader reader)
        {
            if (!AcceptFromHost(from, MessageType.OwnershipDenied))
                return;
            NetEntityId id;
            PeerId owner;
            try
            {
                id = NetEntityId.Read(reader);
                owner = reader.ReadPeerId();
            }
            catch (ProtocolException e)
            {
                NetLog.Warn($"{Log}Malformed OwnershipDenied from {from}: {e.Message}");
                return;
            }
            if (_entities.TryGetValue(id, out var entity))
                SyncSafeInvoke.Raise(OwnershipDenied, entity, owner, nameof(OwnershipDenied));
        }

        private bool AcceptFromHost(PeerId from, byte type)
        {
            if (_disposed || _closed)
                return false;
            if (_isHost || from != _hostId)
            {
                NetLog.Warn($"{Log}Ignoring {MessageType.GetName(type)} from {from}; only the host sends it.");
                return false;
            }
            return true;
        }

        private bool AcceptFromClient(PeerId from, byte type, NetReader reader, out NetEntityId id)
        {
            id = default;
            if (_disposed || _closed)
                return false;
            if (!_isHost)
            {
                NetLog.Warn($"{Log}Ignoring {MessageType.GetName(type)} from {from}; only the host handles it.");
                return false;
            }
            try
            {
                id = NetEntityId.Read(reader);
                return true;
            }
            catch (ProtocolException e)
            {
                NetLog.Warn($"{Log}Malformed {MessageType.GetName(type)} from {from}: {e.Message}");
                return false;
            }
        }

        // ---- Session events --------------------------------------------------------------------------

        private void OnPlayerJoined(PeerId peer)
        {
            if (_disposed || _closed || !_isHost || !_options.ReplicateOnJoin || peer == _localId)
                return;
            if (_messaging.IsPeerConnected(peer))
                ReplicateAllTo(peer);
        }

        private void OnPlayerLeft(PeerId peer)
        {
            if (_disposed || _closed || !_isHost || peer == _localId)
                return;
            _scratch.Clear();
            for (int i = 0; i < _ordered.Count; i++)
            {
                if (_ordered[i].Owner == peer)
                    _scratch.Add(_ordered[i]);
            }
            if (_scratch.Count == 0)
                return;
            // Copy out: the callbacks below may re-enter the registry, which reuses _scratch.
            var orphaned = _scratch.ToArray();
            _scratch.Clear();
            int despawned = 0;
            foreach (var e in orphaned)
            {
                if (!e.IsSpawned || e.Owner != peer)
                    continue;
                if ((e.Flags & NetEntityFlags.DespawnOnOwnerLeave) != 0)
                {
                    DespawnInternal(e.Id, DespawnReason.OwnerLeft);
                    despawned++;
                }
                else
                {
                    ChangeOwner(e, _localId, unchecked((byte)(e.OwnerEpoch + 1)));
                }
            }
            NetLog.Info($"{Log}Owner {peer} left: despawned {despawned}, took over {orphaned.Length - despawned} entities.");
        }

        private void OnSessionClosed()
        {
            if (_disposed || _closed)
                return;
            DropAll(DespawnReason.SessionClosed);
            _closed = true;
        }

        // ---- Bookkeeping -----------------------------------------------------------------------------

        private bool DespawnInternal(NetEntityId id, DespawnReason reason)
        {
            if (!_entities.TryGetValue(id, out var entity))
                return false;
            using (var packet = PacketBuilder.Rent(MessageType.Despawn))
            {
                NetEntityId.Write(packet.Writer, id);
                packet.Writer.WriteByte((byte)reason);
                _messaging.Broadcast(SyncProtocol.EventsChannel, Reliability.Reliable, packet.Segment, PeerId.None);
            }
            NetLog.Info($"{Log}Despawned {entity} ({reason}).");
            Untrack(entity, reason);
            return true;
        }

        private void ChangeOwner(NetEntity entity, PeerId owner, byte epoch)
        {
            var previous = entity.Owner;
            bool wasLocal = entity.IsLocallyOwned;
            entity.Owner = owner;
            entity.OwnerEpoch = epoch;
            entity.ReleasePending = false;
            bool isLocal = entity.IsLocallyOwned;
            if (wasLocal != isLocal)
            {
                entity.Snapshots.Clear();
                // Seed with the last known pose so interpolation continues from where the entity was.
                if (!isLocal)
                    entity.Snapshots.Add(entity.LastState);
            }

            if (_isHost)
            {
                using (var packet = PacketBuilder.Rent(MessageType.OwnershipChange))
                {
                    NetEntityId.Write(packet.Writer, entity.Id);
                    packet.Writer.WritePeerId(owner);
                    packet.Writer.WriteByte(epoch);
                    _messaging.Broadcast(SyncProtocol.EventsChannel, Reliability.Reliable, packet.Segment, PeerId.None);
                }
            }
            if (previous == owner)
                return;
            if (NetLog.IsEnabled(LogLevel.Debug))
                NetLog.Debug($"{Log}{entity.Id} ownership {previous} -> {owner} (epoch {epoch}).");
            if (entity.Instance != null)
            {
                try
                {
                    entity.Instance.OnOwnershipChanged(owner, isLocal);
                }
                catch (Exception e)
                {
                    SyncSafeInvoke.Fail($"OnOwnershipChanged of {entity}", e);
                }
            }
            SyncSafeInvoke.Raise(OwnershipChanged, entity, previous, nameof(OwnershipChanged));
        }

        private void Track(NetEntity entity)
        {
            _entities.Add(entity.Id, entity);
            _ordered.Add(entity);
        }

        private void Untrack(NetEntity entity, DespawnReason reason)
        {
            _entities.Remove(entity.Id);
            _ordered.Remove(entity);
            entity.IsSpawned = false;
            entity.Snapshots.Clear();
            var instance = entity.Instance;
            entity.Instance = null;
            if (instance != null)
            {
                try
                {
                    instance.OnDespawned(reason);
                }
                catch (Exception e)
                {
                    SyncSafeInvoke.Fail($"OnDespawned of {entity}", e);
                }
            }
            SyncSafeInvoke.Raise(EntityDespawned, entity, reason, nameof(EntityDespawned));
        }

        private void DropAll(DespawnReason reason)
        {
            if (_ordered.Count == 0)
                return;
            var all = _ordered.ToArray();
            foreach (var e in all)
            {
                if (e.IsSpawned)
                    Untrack(e, reason);
            }
            _entities.Clear();
            _ordered.Clear();
        }

        private INetEntity CreateInstance(NetEntity entity)
        {
            if (_factory == null)
            {
                NetLog.Warn($"{Log}No entity factory; {entity} has no game object.");
                return null;
            }
            try
            {
                var instance = _factory.CreateEntity(entity);
                // Null is a valid answer: tracked without a game object (the harness bots do it for others' entities).
                if (instance == null)
                    NetLog.Debug($"{Log}Factory returned no object for {entity}.");
                return instance;
            }
            catch (Exception e)
            {
                SyncSafeInvoke.Fail($"Factory for {entity}", e);
                return null;
            }
        }

        private NetEntityId AllocateId()
        {
            while (true)
            {
                uint value = _nextId;
                _nextId = _nextId == uint.MaxValue ? 1 : _nextId + 1;
                var id = new NetEntityId(value);
                if (!_entities.ContainsKey(id))
                    return id;
            }
        }

        private static void WriteSpawn(NetWriter w, NetEntity entity)
        {
            NetEntityId.Write(w, entity.Id);
            w.WriteString(entity.PrefabKey);
            w.WritePeerId(entity.Owner);
            w.WriteByte(entity.OwnerEpoch);
            w.WriteByte((byte)entity.Flags);
            w.WriteBytes(entity.SpawnData);
            // Spawns are reliable, so they always carry full precision.
            TransformCodec.Write(w, entity.LastState, TransformEncoding.Full);
        }

        private void SendIdTo(PeerId peer, byte type, NetEntityId id)
        {
            using (var packet = PacketBuilder.Rent(type))
            {
                NetEntityId.Write(packet.Writer, id);
                _messaging.SendTo(peer, SyncProtocol.EventsChannel, Reliability.Reliable, packet.Segment);
            }
        }

        private void SendDenied(PeerId peer, NetEntityId id, PeerId owner)
        {
            using (var packet = PacketBuilder.Rent(MessageType.OwnershipDenied))
            {
                NetEntityId.Write(packet.Writer, id);
                packet.Writer.WritePeerId(owner);
                _messaging.SendTo(peer, SyncProtocol.EventsChannel, Reliability.Reliable, packet.Segment);
            }
        }

        private void RequireActive()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(NetEntityRegistry));
            if (_closed)
                throw new InvalidOperationException("The session has closed.");
        }

        private void RequireHost(string operation)
        {
            RequireActive();
            if (!_isHost)
                throw new InvalidOperationException($"{operation} is host-only; clients ask the host.");
        }

        private void RequireReachable(PeerId owner, string paramName)
        {
            if (owner != _localId && !_messaging.IsPeerConnected(owner))
                throw new ArgumentException($"Owner {owner} is neither the host nor a connected peer.", paramName);
        }

        /// <summary>Builds [type][count][entries...] packets that never exceed the unreliable packet size.</summary>
        private sealed class StateBatch
        {
            private readonly NetWriter _writer = new NetWriter(ProtocolInfo.MaxUnreliablePacketSize);

            public int Count { get; private set; }

            public void Begin(byte type)
            {
                PacketBuilder.Begin(_writer, type);
                _writer.WriteByte(0);
                Count = 0;
            }

            /// <summary>Appends the entry; false (nothing written) if the batch is full.</summary>
            public bool TryAppend(NetWriter entry)
            {
                if (Count >= SyncProtocol.MaxStatesPerPacket
                    || _writer.Length + entry.Length > ProtocolInfo.MaxUnreliablePacketSize)
                    return false;
                _writer.WriteRaw(entry.AsSegment());
                Count++;
                return true;
            }

            public ArraySegment<byte> Finish()
            {
                _writer.SetByteAt(1, (byte)Count);
                return _writer.AsSegment();
            }
        }
    }
}
