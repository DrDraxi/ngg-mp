using System;
using System.Collections.Generic;
using NGGMultiplayer.Core.Diagnostics;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Session;
using NGGMultiplayer.Core.Sync;

namespace NGGMultiplayer.Core
{
    /// <summary>
    /// Gives every player in a <see cref="NetSession"/> one entity per configured prefab key (for example an avatar
    /// and a ball), owned by that player and flagged <see cref="NetEntityFlags.DespawnOnOwnerLeave"/> and
    /// <see cref="NetEntityFlags.OwnershipLocked"/>.
    /// <list type="bullet">
    /// <item>Host: spawns its own set when the session starts and a client's set once that client has loaded the
    /// host's save (<see cref="NetSession.PeerReady"/>).</item>
    /// <item>Every peer: tracks which player each entity belongs to. The player's id travels in the entity's spawn
    /// data, so the mapping survives ownership changes; <see cref="TryReadPlayer"/> decodes it.</item>
    /// </list>
    /// Main thread only, like <see cref="NetSession"/>.
    /// </summary>
    public sealed class PlayerEntitySpawner : IDisposable
    {
        public const NetEntityFlags EntityFlags = NetEntityFlags.DespawnOnOwnerLeave | NetEntityFlags.OwnershipLocked;

        private const string Log = "[PlayerEntities] ";
        private const int SpawnDataLength = 9;
        private const byte SpawnDataVersion = 1;

        private readonly NetSession _net;
        private readonly string[] _prefabKeys;
        private readonly Dictionary<Key, NetEntity> _byPlayer = new Dictionary<Key, NetEntity>();
        private readonly Action _onStarted;
        private readonly Action<PeerId> _onPeerReady;
        private readonly Action<SessionEndInfo> _onEnded;
        private readonly Action<NetEntity> _onSpawned;
        private readonly Action<NetEntity, DespawnReason> _onDespawned;

        private NetEntityRegistry _registry;
        private bool _disposed;

        /// <param name="prefabKeys">One entity per key and player, spawned in this order.</param>
        public PlayerEntitySpawner(NetSession net, IReadOnlyList<string> prefabKeys)
        {
            _net = net ?? throw new ArgumentNullException(nameof(net));
            if (prefabKeys == null || prefabKeys.Count == 0)
                throw new ArgumentException("At least one prefab key is required.", nameof(prefabKeys));
            _prefabKeys = new string[prefabKeys.Count];
            for (int i = 0; i < prefabKeys.Count; i++)
            {
                var key = prefabKeys[i];
                if (string.IsNullOrEmpty(key))
                    throw new ArgumentException("Prefab keys must not be empty.", nameof(prefabKeys));
                if (Array.IndexOf(_prefabKeys, key, 0, i) >= 0)
                    throw new ArgumentException($"Duplicate prefab key '{key}'.", nameof(prefabKeys));
                _prefabKeys[i] = key;
            }

            _onStarted = OnStarted;
            _onPeerReady = OnPeerReady;
            _onEnded = OnEnded;
            _onSpawned = OnEntitySpawned;
            _onDespawned = OnEntityDespawned;
            _net.Started += _onStarted;
            _net.PeerReady += _onPeerReady;
            _net.Ended += _onEnded;
            if (_net.IsActive)
                OnStarted();
        }

        /// <summary>Host: the spawn pose for a player's entity (player, prefab key). Null spawns at the identity pose.</summary>
        public Func<PeerId, string, TransformState> InitialState { get; set; }

        public IReadOnlyList<string> PrefabKeys => _prefabKeys;

        /// <summary>Player-bound entities currently known, on any peer.</summary>
        public int Count => _byPlayer.Count;

        /// <summary>A player's entity appeared (player, entity); its <see cref="NetEntity.Instance"/> already exists.</summary>
        public event Action<PeerId, NetEntity> PlayerEntitySpawned;

        /// <summary>A player's entity went away (player, entity, reason).</summary>
        public event Action<PeerId, NetEntity, DespawnReason> PlayerEntityDespawned;

        public bool TryGetEntity(PeerId player, string prefabKey, out NetEntity entity) =>
            _byPlayer.TryGetValue(new Key(player, prefabKey), out entity);

        /// <summary>Decodes the player an entity spawned by this class belongs to. False for any other entity.</summary>
        public static bool TryReadPlayer(NetEntity entity, out PeerId player)
        {
            player = PeerId.None;
            var data = entity?.SpawnData;
            if (data == null || data.Length != SpawnDataLength || data[0] != SpawnDataVersion)
                return false;
            ulong value = 0;
            for (int i = 8; i >= 1; i--)
                value = (value << 8) | data[i];
            if (value == 0)
                return false;
            player = new PeerId(value);
            return true;
        }

        public static byte[] WriteSpawnData(PeerId player)
        {
            var data = new byte[SpawnDataLength];
            data[0] = SpawnDataVersion;
            ulong value = player.Value;
            for (int i = 1; i <= 8; i++)
            {
                data[i] = (byte)value;
                value >>= 8;
            }
            return data;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _net.Started -= _onStarted;
            _net.PeerReady -= _onPeerReady;
            _net.Ended -= _onEnded;
            Detach();
        }

        private void OnStarted()
        {
            Detach();
            _registry = _net.Entities;
            if (_registry == null)
                return;
            _registry.EntitySpawned += _onSpawned;
            _registry.EntityDespawned += _onDespawned;
            // Entities announced before we attached (none in practice, but cheap to be sure).
            var existing = _registry.Entities;
            for (int i = 0; i < existing.Count; i++)
                OnEntitySpawned(existing[i]);
            if (_registry.IsHost)
                SpawnFor(_net.LocalId);
        }

        private void OnPeerReady(PeerId peer)
        {
            if (_registry != null && _registry.IsHost && _registry.IsActive)
                SpawnFor(peer);
        }

        private void OnEnded(SessionEndInfo info) => Detach();

        private void SpawnFor(PeerId player)
        {
            var spawnData = WriteSpawnData(player);
            for (int i = 0; i < _prefabKeys.Length; i++)
            {
                var key = _prefabKeys[i];
                if (_byPlayer.ContainsKey(new Key(player, key)))
                    continue;
                try
                {
                    var state = InitialState != null ? InitialState(player, key) : TransformState.Identity;
                    if (!state.IsValid)
                        state = TransformState.Identity;
                    _registry.Spawn(key, player, state, EntityFlags, spawnData);
                }
                catch (Exception e)
                {
                    NetLog.Error($"{Log}Could not spawn '{key}' for {player}: {e.Message}");
                }
                if (_registry == null || !_registry.IsActive)
                    return;
            }
        }

        private void OnEntitySpawned(NetEntity entity)
        {
            if (!TryReadPlayer(entity, out var player) || Array.IndexOf(_prefabKeys, entity.PrefabKey) < 0)
                return;
            var key = new Key(player, entity.PrefabKey);
            if (_byPlayer.TryGetValue(key, out var previous) && previous != entity)
                NetLog.Warn($"{Log}{player} already had '{entity.PrefabKey}' ({previous.Id}); tracking {entity.Id} instead.");
            _byPlayer[key] = entity;
            Raise(PlayerEntitySpawned, player, entity);
        }

        private void OnEntityDespawned(NetEntity entity, DespawnReason reason)
        {
            if (!TryReadPlayer(entity, out var player))
                return;
            var key = new Key(player, entity.PrefabKey);
            if (_byPlayer.TryGetValue(key, out var tracked) && tracked == entity)
            {
                _byPlayer.Remove(key);
                if (PlayerEntityDespawned != null)
                {
                    try
                    {
                        PlayerEntityDespawned(player, entity, reason);
                    }
                    catch (Exception e)
                    {
                        NetLog.Error($"{Log}{nameof(PlayerEntityDespawned)} handler threw: {e}");
                    }
                }
            }
        }

        private void Detach()
        {
            if (_registry != null)
            {
                _registry.EntitySpawned -= _onSpawned;
                _registry.EntityDespawned -= _onDespawned;
                _registry = null;
            }
            _byPlayer.Clear();
        }

        private static void Raise(Action<PeerId, NetEntity> handler, PeerId player, NetEntity entity)
        {
            if (handler == null)
                return;
            try
            {
                handler(player, entity);
            }
            catch (Exception e)
            {
                NetLog.Error($"{Log}{nameof(PlayerEntitySpawned)} handler threw: {e}");
            }
        }

        private readonly struct Key : IEquatable<Key>
        {
            private readonly PeerId _player;
            private readonly string _prefabKey;

            public Key(PeerId player, string prefabKey)
            {
                _player = player;
                _prefabKey = prefabKey ?? string.Empty;
            }

            public bool Equals(Key other) => _player == other._player && string.Equals(_prefabKey, other._prefabKey, StringComparison.Ordinal);

            public override bool Equals(object obj) => obj is Key other && Equals(other);

            public override int GetHashCode() => (_player.GetHashCode() * 397) ^ StringComparer.Ordinal.GetHashCode(_prefabKey);
        }
    }
}
