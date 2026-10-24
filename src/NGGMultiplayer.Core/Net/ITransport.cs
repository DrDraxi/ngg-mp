using System;
using System.Collections.Generic;

namespace NGGMultiplayer.Core.Net
{
    /// <summary>
    /// Connectionless message transport. Connection lifecycle (handshake, heartbeats, timeouts)
    /// is the session layer's job. Not thread-safe: call from a single thread.
    /// </summary>
    public interface ITransport : IDisposable
    {
        PeerId LocalId { get; }

        /// <summary>
        /// Queues <paramref name="data"/> for delivery. Implementations copy the bytes before
        /// returning, so callers may reuse the buffer immediately.
        /// </summary>
        void Send(PeerId to, Channel channel, Reliability reliability, ArraySegment<byte> data);

        /// <summary>Drains everything received since the last call. Ownership of each payload passes to caller.</summary>
        void Poll(List<IncomingPacket> into);

        /// <summary>Forgets any per-peer state and discards anything still queued from that peer.</summary>
        void Disconnect(PeerId peer);
    }
}
