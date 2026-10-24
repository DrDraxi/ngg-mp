using System;
using System.Collections.Generic;

namespace NGGMultiplayer.Core.Net
{
    /// <summary>
    /// In-memory network connecting <see cref="LoopbackTransport"/>s by <see cref="PeerId"/>.
    /// Time is manual: nothing is delivered later than "now" until <see cref="Advance"/> is called.
    /// Reliable packets are never lost and stay in order per (from, to, channel); unreliable
    /// packets are subject to <see cref="UnreliableLossPercent"/> and jitter reordering.
    /// Not thread-safe.
    /// </summary>
    public sealed class LoopbackHub
    {
        private readonly Dictionary<PeerId, LoopbackTransport> _transports = new Dictionary<PeerId, LoopbackTransport>();
        private readonly HashSet<LinkKey> _downLinks = new HashSet<LinkKey>();
        private readonly Random _random;
        private long _nowMicros;
        private double _latencyMs;
        private double _jitterMs;
        private double _lossPercent;

        public LoopbackHub(int seed = 12345)
        {
            _random = new Random(seed);
        }

        /// <summary>Current simulated time in seconds.</summary>
        public double Now => _nowMicros / 1_000_000.0;

        /// <summary>One-way delay applied to every packet.</summary>
        public double LatencyMs
        {
            get => _latencyMs;
            set => _latencyMs = RequireNonNegative(value, nameof(LatencyMs));
        }

        /// <summary>Extra uniformly random delay in [0, JitterMs] per packet. Reliable order is preserved.</summary>
        public double JitterMs
        {
            get => _jitterMs;
            set => _jitterMs = RequireNonNegative(value, nameof(JitterMs));
        }

        /// <summary>Chance (0-100) that an unreliable packet is dropped. Reliable packets are never dropped.</summary>
        public double UnreliableLossPercent
        {
            get => _lossPercent;
            set
            {
                if (double.IsNaN(value) || value < 0 || value > 100)
                    throw new ArgumentOutOfRangeException(nameof(UnreliableLossPercent), value, "Must be in [0, 100].");
                _lossPercent = value;
            }
        }

        public long PacketsSent { get; private set; }
        public long PacketsDropped { get; private set; }

        public LoopbackTransport CreateTransport(PeerId id)
        {
            if (!id.IsValid)
                throw new ArgumentException("PeerId must be non-zero.", nameof(id));
            if (_transports.ContainsKey(id))
                throw new InvalidOperationException($"Peer {id} is already attached to this hub.");
            var transport = new LoopbackTransport(this, id);
            _transports.Add(id, transport);
            return transport;
        }

        public bool IsAttached(PeerId id) => _transports.ContainsKey(id);

        public void Advance(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
                throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "Must be finite and non-negative.");
            _nowMicros += (long)Math.Round(seconds * 1_000_000.0);
        }

        /// <summary>Drops all traffic between two peers in both directions while down (simulates a cut link).</summary>
        public void SetLinkDown(PeerId a, PeerId b, bool down)
        {
            if (down)
            {
                _downLinks.Add(new LinkKey(a, b));
                _downLinks.Add(new LinkKey(b, a));
            }
            else
            {
                _downLinks.Remove(new LinkKey(a, b));
                _downLinks.Remove(new LinkKey(b, a));
            }
        }

        internal long NowMicros => _nowMicros;

        internal void Route(PeerId from, PeerId to, Channel channel, Reliability reliability, ArraySegment<byte> data)
        {
            PacketsSent++;

            if (!_transports.TryGetValue(to, out var target)
                || (_downLinks.Count > 0 && _downLinks.Contains(new LinkKey(from, to))))
            {
                PacketsDropped++;
                return;
            }

            if (reliability == Reliability.Unreliable && _lossPercent > 0 && _random.NextDouble() * 100.0 < _lossPercent)
            {
                PacketsDropped++;
                return;
            }

            double delayMs = _latencyMs;
            if (_jitterMs > 0)
                delayMs += _random.NextDouble() * _jitterMs;

            var copy = new byte[data.Count];
            if (data.Count > 0)
                Buffer.BlockCopy(data.Array, data.Offset, copy, 0, data.Count);

            long deliverAt = _nowMicros + (long)Math.Round(delayMs * 1000.0);
            target.Enqueue(from, channel, reliability, copy, deliverAt);
        }

        internal void Detach(PeerId id)
        {
            _transports.Remove(id);
        }

        private static double RequireNonNegative(double value, string name)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
                throw new ArgumentOutOfRangeException(name, value, "Must be finite and non-negative.");
            return value;
        }

        private readonly struct LinkKey : IEquatable<LinkKey>
        {
            private readonly ulong _from;
            private readonly ulong _to;

            public LinkKey(PeerId from, PeerId to)
            {
                _from = from.Value;
                _to = to.Value;
            }

            public bool Equals(LinkKey other) => _from == other._from && _to == other._to;

            public override bool Equals(object obj) => obj is LinkKey other && Equals(other);

            public override int GetHashCode() => (_from.GetHashCode() * 397) ^ _to.GetHashCode();
        }
    }
}
