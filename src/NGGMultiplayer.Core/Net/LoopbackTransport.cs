using System;
using System.Collections.Generic;

namespace NGGMultiplayer.Core.Net
{
    /// <summary>In-memory <see cref="ITransport"/> for tests. Create through <see cref="LoopbackHub.CreateTransport"/>.</summary>
    public sealed class LoopbackTransport : ITransport
    {
        private readonly LoopbackHub _hub;
        // Sorted by deliverAt (ties in send order) so the ready packets are always a prefix.
        private readonly List<Pending> _inbox = new List<Pending>();
        private readonly Dictionary<StreamKey, long> _lastReliableDeliverAt = new Dictionary<StreamKey, long>();
        private bool _disposed;

        internal LoopbackTransport(LoopbackHub hub, PeerId localId)
        {
            _hub = hub;
            LocalId = localId;
        }

        public PeerId LocalId { get; }

        public bool IsDisposed => _disposed;

        /// <summary>Packets queued for this transport, including ones not yet due.</summary>
        public int PendingCount => _inbox.Count;

        public void Send(PeerId to, Channel channel, Reliability reliability, ArraySegment<byte> data)
        {
            ThrowIfDisposed();
            if (data.Array == null)
                throw new ArgumentException("Segment has no backing array.", nameof(data));
            if (!ChannelInfo.IsDefined(channel))
                throw new ArgumentOutOfRangeException(nameof(channel), channel, "Undefined channel.");
            if (reliability != Reliability.Reliable && reliability != Reliability.Unreliable)
                throw new ArgumentOutOfRangeException(nameof(reliability), reliability, "Undefined reliability.");
            if (!to.IsValid)
                throw new ArgumentException("Destination must be a valid peer.", nameof(to));

            _hub.Route(LocalId, to, channel, reliability, data);
        }

        public void Poll(List<IncomingPacket> into)
        {
            if (into == null)
                throw new ArgumentNullException(nameof(into));
            ThrowIfDisposed();

            long now = _hub.NowMicros;
            int ready = 0;
            while (ready < _inbox.Count && _inbox[ready].DeliverAt <= now)
            {
                var p = _inbox[ready];
                into.Add(new IncomingPacket(p.From, p.Channel, p.Data));
                ready++;
            }
            if (ready > 0)
                _inbox.RemoveRange(0, ready);
        }

        public void Disconnect(PeerId peer)
        {
            if (_disposed)
                return;
            _inbox.RemoveAll(p => p.From == peer);
            for (byte c = 0; c < ChannelInfo.Count; c++)
                _lastReliableDeliverAt.Remove(new StreamKey(peer, (Channel)c));
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _inbox.Clear();
            _lastReliableDeliverAt.Clear();
            _hub.Detach(LocalId);
        }

        internal void Enqueue(PeerId from, Channel channel, Reliability reliability, byte[] data, long deliverAt)
        {
            if (reliability == Reliability.Reliable)
            {
                // Jitter must not reorder a reliable stream: never deliver before the previous packet.
                var key = new StreamKey(from, channel);
                if (_lastReliableDeliverAt.TryGetValue(key, out long last) && deliverAt < last)
                    deliverAt = last;
                _lastReliableDeliverAt[key] = deliverAt;
            }

            var pending = new Pending(deliverAt, from, channel, data);
            // Inserting after every entry with deliverAt <= ours keeps equal-time packets in send order.
            int index = _inbox.Count;
            while (index > 0 && _inbox[index - 1].DeliverAt > deliverAt)
                index--;
            _inbox.Insert(index, pending);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(LoopbackTransport));
        }

        private readonly struct Pending
        {
            public readonly long DeliverAt;
            public readonly PeerId From;
            public readonly Channel Channel;
            public readonly byte[] Data;

            public Pending(long deliverAt, PeerId from, Channel channel, byte[] data)
            {
                DeliverAt = deliverAt;
                From = from;
                Channel = channel;
                Data = data;
            }
        }

        private readonly struct StreamKey : IEquatable<StreamKey>
        {
            private readonly ulong _from;
            private readonly Channel _channel;

            public StreamKey(PeerId from, Channel channel)
            {
                _from = from.Value;
                _channel = channel;
            }

            public bool Equals(StreamKey other) => _from == other._from && _channel == other._channel;

            public override bool Equals(object obj) => obj is StreamKey other && Equals(other);

            public override int GetHashCode() => (_from.GetHashCode() * 397) ^ (int)_channel;
        }
    }
}
