using System;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;

namespace NGGMultiplayer.Core.Session
{
    /// <summary>
    /// The slice of the session layer that save sync, entity sync and RPC routing use. <see cref="SessionManager"/>
    /// implements it; tests implement it over <see cref="LoopbackTransport"/>. The session is a star: clients only
    /// exchange packets with the host.
    /// </summary>
    public interface ISessionMessaging
    {
        bool IsHost { get; }

        PeerId LocalId { get; }

        /// <summary>The host's id; equals <see cref="LocalId"/> on the host.</summary>
        PeerId HostId { get; }

        /// <summary>True if <paramref name="peer"/> is a remote peer of the session that packets can be sent to.</summary>
        bool IsPeerConnected(PeerId peer);

        /// <summary>Sends one packet ([type][payload]). Packets for unreachable peers are dropped.</summary>
        void SendTo(PeerId peer, Channel channel, Reliability reliability, ArraySegment<byte> data);

        /// <summary>
        /// Host: sends to every remote peer except <paramref name="except"/> (pass <see cref="PeerId.None"/> to
        /// exclude nobody). Client: sends to the host unless it is <paramref name="except"/>.
        /// </summary>
        void Broadcast(Channel channel, Reliability reliability, ArraySegment<byte> data, PeerId except);

        /// <summary>
        /// Routes incoming packets of <paramref name="messageType"/> to <paramref name="handler"/>. The reader is
        /// positioned just after the type byte and is only valid during the call.
        /// </summary>
        void RegisterHandler(byte messageType, Action<PeerId, NetReader> handler);

        void UnregisterHandler(byte messageType);

        /// <summary>
        /// Host: a peer finished the handshake (raised after its Welcome was sent). Client: another player
        /// appeared in the roster; right after joining it is raised for every player already present.
        /// </summary>
        event Action<PeerId> PlayerJoined;

        /// <summary>A remote player left. Not raised per player when the whole session ends.</summary>
        event Action<PeerId> PlayerLeft;

        /// <summary>Raised once when the local peer leaves or the session ends for any reason.</summary>
        event Action SessionClosed;
    }
}
