using System;
using System.Collections.Generic;
using NGGMultiplayer.Core.Net;

namespace NGGMultiplayer.Testing
{
    /// <summary>
    /// One <see cref="ITransport"/> over two: packets for the harness's bots (<see cref="BotIds"/>) go through an
    /// in-process <see cref="LoopbackTransport"/>, everything else through the real transport (Steam). The session
    /// layer above sees one peer namespace, so a bot is just another client of the host.
    /// <para>
    /// Only the loopback side is owned: the real transport stays its creator's to dispose. While
    /// <see cref="LoopbackEnabled"/> is false (not hosting), packets for bots are dropped and anything the loopback
    /// received is discarded, so the wrapper is exactly the real transport. <see cref="Dispose"/> detaches from the hub
    /// for good but keeps passing the real transport through: the session that owns this wrapper may still say goodbye
    /// after the harness is gone. Not thread-safe, like every transport.
    /// </para>
    /// </summary>
    public sealed class CompositeTransport : ITransport
    {
        private readonly ITransport _primary;
        private readonly LoopbackTransport _loopback;
        private readonly Func<PeerId, bool> _isLoopbackPeer;
        private readonly List<IncomingPacket> _scratch = new List<IncomingPacket>();
        private bool _disposed;

        /// <param name="primary">The real transport (not owned).</param>
        /// <param name="loopback">This peer's attachment to the bots' hub (owned); same local id as <paramref name="primary"/>.</param>
        /// <param name="isLoopbackPeer">Which peers live on the hub; <see cref="BotIds.IsBot"/> when null.</param>
        public CompositeTransport(ITransport primary, LoopbackTransport loopback, Func<PeerId, bool> isLoopbackPeer = null)
        {
            _primary = primary ?? throw new ArgumentNullException(nameof(primary));
            _loopback = loopback ?? throw new ArgumentNullException(nameof(loopback));
            if (primary.LocalId != loopback.LocalId)
                throw new ArgumentException($"The loopback side ({loopback.LocalId}) must use the primary's local id ({primary.LocalId}).");
            _isLoopbackPeer = isLoopbackPeer ?? BotIds.IsBot;
        }

        public PeerId LocalId => _primary.LocalId;

        public ITransport Primary => _primary;

        /// <summary>Bots are reachable. Off by default; the harness turns it on while this peer hosts.</summary>
        public bool LoopbackEnabled { get; set; }

        public long LoopbackSent { get; private set; }

        public long LoopbackReceived { get; private set; }

        /// <summary>Packets for or from bots dropped because <see cref="LoopbackEnabled"/> was off.</summary>
        public long LoopbackDropped { get; private set; }

        public bool IsLoopbackPeer(PeerId peer) => _isLoopbackPeer(peer);

        public bool IsDisposed => _disposed;

        public void Send(PeerId to, Channel channel, Reliability reliability, ArraySegment<byte> data)
        {
            if (!_isLoopbackPeer(to))
            {
                _primary.Send(to, channel, reliability, data);
                return;
            }
            if (!LoopbackEnabled || _disposed)
            {
                LoopbackDropped++;
                return;
            }
            _loopback.Send(to, channel, reliability, data);
            LoopbackSent++;
        }

        public void Poll(List<IncomingPacket> into)
        {
            if (into == null)
                throw new ArgumentNullException(nameof(into));
            _primary.Poll(into);
            if (_disposed)
                return;

            _scratch.Clear();
            _loopback.Poll(_scratch);
            for (int i = 0; i < _scratch.Count; i++)
            {
                var packet = _scratch[i];
                // Only bots live on the hub; anything else claiming to come from there is not trusted.
                if (!LoopbackEnabled || !_isLoopbackPeer(packet.From))
                {
                    LoopbackDropped++;
                    continue;
                }
                into.Add(packet);
                LoopbackReceived++;
            }
            _scratch.Clear();
        }

        public void Disconnect(PeerId peer)
        {
            if (_isLoopbackPeer(peer))
            {
                if (!_disposed)
                    _loopback.Disconnect(peer);
            }
            else
                _primary.Disconnect(peer);
        }

        /// <summary>Detaches from the bots' hub; from now on this is the primary transport alone (left to its owner).</summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            LoopbackEnabled = false;
            _loopback.Dispose();
        }
    }
}
