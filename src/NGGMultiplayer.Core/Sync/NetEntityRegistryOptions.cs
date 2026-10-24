using System;

namespace NGGMultiplayer.Core.Sync
{
    public sealed class NetEntityRegistryOptions
    {
        /// <summary>States sent per second for each locally owned entity.</summary>
        public double SendRate { get; set; } = 30;

        /// <summary>Remote entities are rendered this many seconds in the past so there are states to interpolate between.</summary>
        public double InterpolationDelay { get; set; } = 0.1;

        public double MaxExtrapolation { get; set; } = SnapshotBuffer.DefaultMaxExtrapolation;

        public int SnapshotCapacity { get; set; } = SnapshotBuffer.DefaultCapacity;

        public TransformEncoding Encoding { get; set; } = TransformEncoding.Full;

        /// <summary>Upper bound on tracked entities; spawns beyond it are refused (host) or ignored (client).</summary>
        public int MaxEntities { get; set; } = 4096;

        /// <summary>
        /// Host: send every existing entity to a peer when it joins. Disable to call
        /// <see cref="NetEntityRegistry.ReplicateAllTo"/> yourself, e.g. once the peer has loaded the host's save.
        /// </summary>
        public bool ReplicateOnJoin { get; set; } = true;

        /// <summary>Feed sampled states to <see cref="INetEntity.ApplyState"/> during Update.</summary>
        public bool AutoApply { get; set; } = true;

        internal NetEntityRegistryOptions Validated()
        {
            if (double.IsNaN(SendRate) || SendRate <= 0 || SendRate > 1000)
                throw new ArgumentOutOfRangeException(nameof(SendRate), SendRate, "Must be in (0, 1000].");
            if (double.IsNaN(InterpolationDelay) || InterpolationDelay < 0 || InterpolationDelay > 5)
                throw new ArgumentOutOfRangeException(nameof(InterpolationDelay), InterpolationDelay, "Must be in [0, 5].");
            if (double.IsNaN(MaxExtrapolation) || MaxExtrapolation < 0 || MaxExtrapolation > 5)
                throw new ArgumentOutOfRangeException(nameof(MaxExtrapolation), MaxExtrapolation, "Must be in [0, 5].");
            if (SnapshotCapacity < 2 || SnapshotCapacity > 1024)
                throw new ArgumentOutOfRangeException(nameof(SnapshotCapacity), SnapshotCapacity, "Must be in [2, 1024].");
            if (MaxEntities < 1)
                throw new ArgumentOutOfRangeException(nameof(MaxEntities), MaxEntities, "Must be positive.");
            if (!Enum.IsDefined(typeof(TransformEncoding), Encoding))
                throw new ArgumentOutOfRangeException(nameof(Encoding), Encoding, "Unknown encoding.");
            return new NetEntityRegistryOptions
            {
                SendRate = SendRate,
                InterpolationDelay = InterpolationDelay,
                MaxExtrapolation = MaxExtrapolation,
                SnapshotCapacity = SnapshotCapacity,
                Encoding = Encoding,
                MaxEntities = MaxEntities,
                ReplicateOnJoin = ReplicateOnJoin,
                AutoApply = AutoApply,
            };
        }
    }
}
