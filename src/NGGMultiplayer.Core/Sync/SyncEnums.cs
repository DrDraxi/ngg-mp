using System;

namespace NGGMultiplayer.Core.Sync
{
    [Flags]
    public enum NetEntityFlags : byte
    {
        None = 0,

        /// <summary>Player-bound: despawned when its owner leaves instead of being taken over by the host.</summary>
        DespawnOnOwnerLeave = 1,

        /// <summary>The default ownership policy refuses client requests for this entity.</summary>
        OwnershipLocked = 2,

        All = DespawnOnOwnerLeave | OwnershipLocked
    }

    public enum DespawnReason : byte
    {
        /// <summary>The host despawned the entity.</summary>
        Despawned,

        /// <summary>The entity was flagged <see cref="NetEntityFlags.DespawnOnOwnerLeave"/> and its owner left.</summary>
        OwnerLeft,

        /// <summary>The session ended; the registry dropped everything.</summary>
        SessionClosed,

        /// <summary>The registry was disposed.</summary>
        Disposed
    }

    /// <summary>How <see cref="SnapshotBuffer.Sample"/> produced its result.</summary>
    public enum SnapshotSampleKind : byte
    {
        /// <summary>The buffer is empty; no state was produced.</summary>
        None,

        /// <summary>The render time is at or before the oldest state; that state was returned unchanged.</summary>
        Clamped,

        /// <summary>The render time lies between two states (or exactly on one).</summary>
        Interpolated,

        /// <summary>The render time is past the newest state, within the extrapolation limit.</summary>
        Extrapolated,

        /// <summary>The render time is past the extrapolation limit; the pose is frozen at the limit.</summary>
        Held
    }

    public enum RpcTarget : byte
    {
        Host = 0,

        /// <summary>Everyone, including the sender. A client's own copy comes back through the host.</summary>
        All = 1,

        /// <summary>Everyone except the sender.</summary>
        Others = 2,

        /// <summary>One specific peer.</summary>
        Peer = 3
    }
}
