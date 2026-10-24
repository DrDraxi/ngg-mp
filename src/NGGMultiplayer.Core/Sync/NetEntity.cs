using NGGMultiplayer.Core.Net;

namespace NGGMultiplayer.Core.Sync
{
    /// <summary>The registry's record of one networked entity. Read-only to callers; the registry mutates it.</summary>
    public sealed class NetEntity
    {
        internal NetEntity(NetEntityId id, string prefabKey, PeerId owner, byte epoch, NetEntityFlags flags, byte[] spawnData,
            PeerId localId, int snapshotCapacity, double maxExtrapolation)
        {
            Id = id;
            PrefabKey = prefabKey;
            Owner = owner;
            OwnerEpoch = epoch;
            Flags = flags;
            SpawnData = spawnData;
            LocalId = localId;
            Snapshots = new SnapshotBuffer(snapshotCapacity) { MaxExtrapolation = maxExtrapolation };
        }

        public NetEntityId Id { get; }

        /// <summary>Game-defined key naming what to instantiate.</summary>
        public string PrefabKey { get; }

        public PeerId Owner { get; internal set; }

        /// <summary>Increments on every ownership change; states tagged with another epoch are ignored.</summary>
        public byte OwnerEpoch { get; internal set; }

        public NetEntityFlags Flags { get; }

        /// <summary>Game-defined spawn parameters (never null; may be empty). Do not modify.</summary>
        public byte[] SpawnData { get; }

        public INetEntity Instance { get; internal set; }

        /// <summary>Received states awaiting interpolation. Only fed while the entity is owned remotely.</summary>
        public SnapshotBuffer Snapshots { get; }

        /// <summary>The newest state captured (owner) or received (others); the spawn pose until then.</summary>
        public TransformState LastState { get; internal set; }

        public bool IsLocallyOwned => Owner == LocalId;

        public bool IsSpawned { get; internal set; } = true;

        /// <summary>A release was sent and the host has not answered yet; the entity stops sending meanwhile.</summary>
        public bool ReleasePending { get; internal set; }

        internal PeerId LocalId { get; }

        internal bool LoggedInvalidState;

        public override string ToString() => $"Entity {Id} '{PrefabKey}' owner {Owner}";
    }
}
