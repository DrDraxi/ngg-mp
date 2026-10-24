using System;

namespace NGGMultiplayer.Core.Save
{
    /// <summary>Tuning for <see cref="SaveSyncHost"/> and <see cref="SaveSyncClient"/>. Read when they are constructed.</summary>
    public sealed class SaveSyncOptions
    {
        /// <summary>Client: a snapshot that makes no progress for this long is abandoned and re-requested.</summary>
        public double TransferTimeoutSeconds { get; set; } = 15;

        /// <summary>Host: at most one snapshot per peer per interval; extra resync requests are coalesced.</summary>
        public double MinResyncIntervalSeconds { get; set; } = 1;

        /// <summary>Largest snapshot either side accepts. Capped at <see cref="SaveProtocol.MaxSnapshotBytes"/>.</summary>
        public int MaxSnapshotBytes { get; set; } = SaveProtocol.MaxSnapshotBytes;

        /// <summary>
        /// Host: snapshot bytes sent to one peer per <see cref="SaveSyncHost.Tick"/> (rounded up to whole chunks).
        /// Keeps a large save from overflowing the transport's send buffer in a single frame.
        /// </summary>
        public int SnapshotBytesPerTick { get; set; } = 64 * 1024;

        /// <summary>Client: deltas buffered while a snapshot is in flight. Overflow forces another resync.</summary>
        public int MaxQueuedDeltas { get; set; } = 1024;

        /// <summary>Client: CRC/apply/format failures in a row before giving up (<see cref="SaveSyncState.Failed"/>).</summary>
        public int MaxConsecutiveFailures { get; set; } = 5;

        /// <summary>Client: request a full snapshot when the host rejects a claim, undoing the local change.</summary>
        public bool ResyncOnClaimRejected { get; set; } = true;

        internal SaveSyncOptions Validated()
        {
            if (!(TransferTimeoutSeconds > 0))
                throw new ArgumentOutOfRangeException(nameof(TransferTimeoutSeconds), "Must be positive.");
            if (!(MinResyncIntervalSeconds >= 0))
                throw new ArgumentOutOfRangeException(nameof(MinResyncIntervalSeconds), "Must not be negative.");
            if (MaxSnapshotBytes < 0)
                throw new ArgumentOutOfRangeException(nameof(MaxSnapshotBytes), "Must not be negative.");
            if (SnapshotBytesPerTick < 1)
                throw new ArgumentOutOfRangeException(nameof(SnapshotBytesPerTick), "Must be positive.");
            if (MaxQueuedDeltas < 0)
                throw new ArgumentOutOfRangeException(nameof(MaxQueuedDeltas), "Must not be negative.");
            if (MaxConsecutiveFailures < 1)
                throw new ArgumentOutOfRangeException(nameof(MaxConsecutiveFailures), "Must be at least 1.");

            return new SaveSyncOptions
            {
                TransferTimeoutSeconds = TransferTimeoutSeconds,
                MinResyncIntervalSeconds = MinResyncIntervalSeconds,
                MaxSnapshotBytes = Math.Min(MaxSnapshotBytes, SaveProtocol.MaxSnapshotBytes),
                SnapshotBytesPerTick = SnapshotBytesPerTick,
                MaxQueuedDeltas = MaxQueuedDeltas,
                MaxConsecutiveFailures = MaxConsecutiveFailures,
                ResyncOnClaimRejected = ResyncOnClaimRejected,
            };
        }
    }
}
