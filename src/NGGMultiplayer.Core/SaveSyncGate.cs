using System;
using System.Collections.Generic;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Session;

namespace NGGMultiplayer.Core
{
    /// <summary>
    /// Host-side view of the session for gameplay modules (entities, RPCs) in which a client only exists once it
    /// has loaded the host's save. Until then it gets no gameplay traffic, cannot be given entities, and its join
    /// is not announced; <see cref="MarkReady"/> announces it, so the registry replicates entities at that point.
    /// Incoming packets are not filtered: a client may legitimately send right after its ack, on another channel.
    /// </summary>
    internal sealed class SaveSyncGate : ISessionMessaging, IDisposable
    {
        private readonly ISessionMessaging _inner;
        private readonly HashSet<PeerId> _ready = new HashSet<PeerId>();
        private readonly Action<PeerId> _onInnerLeft;
        private readonly Action _onInnerClosed;
        private bool _disposed;

        public SaveSyncGate(ISessionMessaging inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            if (!inner.IsHost)
                throw new InvalidOperationException("The save sync gate is host-only.");
            _onInnerLeft = OnInnerLeft;
            _onInnerClosed = OnInnerClosed;
            _inner.PlayerLeft += _onInnerLeft;
            _inner.SessionClosed += _onInnerClosed;
        }

        public bool IsHost => true;

        public PeerId LocalId => _inner.LocalId;

        public PeerId HostId => _inner.HostId;

        public int ReadyCount => _ready.Count;

        public event Action<PeerId> PlayerJoined;

        public event Action<PeerId> PlayerLeft;

        public event Action SessionClosed;

        public bool IsReady(PeerId peer) => _ready.Contains(peer);

        /// <summary>Admits a connected peer to gameplay traffic. False if it already was, or is not connected.</summary>
        public bool MarkReady(PeerId peer)
        {
            if (_disposed || !_inner.IsPeerConnected(peer) || !_ready.Add(peer))
                return false;
            PlayerJoined?.Invoke(peer);
            return true;
        }

        public bool IsPeerConnected(PeerId peer) => _ready.Contains(peer) && _inner.IsPeerConnected(peer);

        public void SendTo(PeerId peer, Channel channel, Reliability reliability, ArraySegment<byte> data)
        {
            if (_ready.Contains(peer))
                _inner.SendTo(peer, channel, reliability, data);
        }

        public void Broadcast(Channel channel, Reliability reliability, ArraySegment<byte> data, PeerId except)
        {
            foreach (var peer in _ready)
            {
                if (peer != except)
                    _inner.SendTo(peer, channel, reliability, data);
            }
        }

        public void RegisterHandler(byte messageType, Action<PeerId, NetReader> handler) =>
            _inner.RegisterHandler(messageType, handler);

        public void UnregisterHandler(byte messageType) => _inner.UnregisterHandler(messageType);

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _inner.PlayerLeft -= _onInnerLeft;
            _inner.SessionClosed -= _onInnerClosed;
            _ready.Clear();
        }

        private void OnInnerLeft(PeerId peer)
        {
            // Peers that never became ready were never announced, so their departure is not either.
            if (_ready.Remove(peer))
                PlayerLeft?.Invoke(peer);
        }

        private void OnInnerClosed()
        {
            _ready.Clear();
            SessionClosed?.Invoke();
        }
    }
}
