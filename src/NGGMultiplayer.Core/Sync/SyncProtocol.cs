using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;

namespace NGGMultiplayer.Core.Sync
{
    /// <summary>
    /// Entity sync and RPC wire format. Reliable entity messages and RPCs share <see cref="EventsChannel"/>, so
    /// an RPC sent after a Spawn never overtakes it. States travel unreliable on <see cref="StateChannel"/>.
    /// <code>
    /// Spawn              varuint id, string prefabKey, peerId owner, byte epoch, byte flags, bytes spawnData, state
    /// Despawn            varuint id, byte reason (DespawnReason.Despawned or OwnerLeft)
    /// OwnershipChange    varuint id, peerId owner, byte epoch
    /// State              byte count, count * (varuint id, byte epoch, state)   owner to host, or host-owned to clients
    /// StateRelayBatch    same as State                                          host relaying client-owned states
    /// OwnershipRequest   varuint id                                             client to host
    /// OwnershipRelease   varuint id                                             client to host
    /// OwnershipDenied    varuint id, peerId currentOwner                        host to the requesting client
    /// Rpc                uint rpcId, byte target|flags, [peerId peer], payload  client to host
    /// RpcRelay           uint rpcId, peerId sender, payload                     host to client; sender stamped by host
    /// </code>
    /// The epoch increments on every ownership change; states carrying a stale epoch are dropped, so states
    /// still in flight from a previous owner never land after a transfer.
    /// </summary>
    public static class SyncProtocol
    {
        public const Channel EventsChannel = Channel.Events;
        public const Channel StateChannel = Channel.State;

        public const int MaxPrefabKeyBytes = 256;
        public const int MaxSpawnDataBytes = 4096;
        public const int MaxStatesPerPacket = 255;

        /// <summary>Header of a State/StateRelayBatch packet: type byte plus count byte.</summary>
        internal const int StateBatchHeaderSize = 2;

        /// <summary>Rpc header: type, rpc id, target byte, optional peer id.</summary>
        internal const int RpcHeaderMaxSize = 1 + 4 + 1 + 8;

        /// <summary>RpcRelay header: type, rpc id, sender.</summary>
        internal const int RpcRelayHeaderSize = 1 + 4 + 8;

        /// <summary>Largest unreliable RPC payload; both its Rpc and RpcRelay forms fit one unreliable packet.</summary>
        public const int MaxUnreliableRpcPayload = ProtocolInfo.MaxUnreliablePacketSize - RpcHeaderMaxSize;

        /// <summary>Largest reliable RPC payload.</summary>
        public const int MaxReliableRpcPayload = ProtocolInfo.MaxPacketSize - RpcHeaderMaxSize;
    }
}
