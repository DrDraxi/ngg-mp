namespace NGGMultiplayer.Core.Protocol
{
    /// <summary>
    /// First byte of every packet. Each module owns one range (see docs/ARCHITECTURE.md).
    /// All core codes are defined here so modules never need to edit this file; the plugin's
    /// game-specific codes live in its own Game/ folder within GameFirst..GameLast.
    /// </summary>
    public static class MessageType
    {
        public const byte SessionFirst = 1;
        public const byte SessionLast = 19;
        public const byte SaveFirst = 20;
        public const byte SaveLast = 39;
        public const byte EntityFirst = 40;
        public const byte EntityLast = 59;
        public const byte RpcFirst = 60;
        public const byte RpcLast = 79;
        public const byte GameFirst = 80;
        public const byte GameLast = 127;

        // Session
        public const byte Hello = 1;
        public const byte Welcome = 2;
        public const byte Reject = 3;
        public const byte RosterUpdate = 4;
        public const byte Ping = 5;
        public const byte Pong = 6;
        public const byte Goodbye = 7;
        public const byte SessionEnded = 8;

        // Save sync
        public const byte SaveBegin = 20;
        public const byte SaveChunk = 21;
        public const byte SaveAck = 22;
        public const byte SaveDelta = 23;
        public const byte ProgressClaim = 24;
        public const byte SaveResyncRequest = 25;
        public const byte ProgressClaimRejected = 26;

        // Entity sync
        public const byte Spawn = 40;
        public const byte Despawn = 41;
        public const byte State = 42;
        public const byte OwnershipChange = 43;
        public const byte StateRelayBatch = 44;
        public const byte OwnershipRequest = 45;
        public const byte OwnershipRelease = 46;
        public const byte OwnershipDenied = 47;

        // RPC / events
        public const byte Rpc = 60;
        public const byte RpcRelay = 61;

        public static bool IsSession(byte type) => type >= SessionFirst && type <= SessionLast;

        public static bool IsSave(byte type) => type >= SaveFirst && type <= SaveLast;

        public static bool IsEntity(byte type) => type >= EntityFirst && type <= EntityLast;

        public static bool IsRpc(byte type) => type >= RpcFirst && type <= RpcLast;

        public static bool IsGame(byte type) => type >= GameFirst && type <= GameLast;

        /// <summary>True for any byte inside one of the owned ranges (1..127).</summary>
        public static bool IsAssignedRange(byte type) => type >= SessionFirst && type <= GameLast;

        /// <summary>Name for logging; unknown codes are rendered with their range.</summary>
        public static string GetName(byte type)
        {
            switch (type)
            {
                case Hello: return nameof(Hello);
                case Welcome: return nameof(Welcome);
                case Reject: return nameof(Reject);
                case RosterUpdate: return nameof(RosterUpdate);
                case Ping: return nameof(Ping);
                case Pong: return nameof(Pong);
                case Goodbye: return nameof(Goodbye);
                case SessionEnded: return nameof(SessionEnded);
                case SaveBegin: return nameof(SaveBegin);
                case SaveChunk: return nameof(SaveChunk);
                case SaveAck: return nameof(SaveAck);
                case SaveDelta: return nameof(SaveDelta);
                case ProgressClaim: return nameof(ProgressClaim);
                case SaveResyncRequest: return nameof(SaveResyncRequest);
                case ProgressClaimRejected: return nameof(ProgressClaimRejected);
                case Spawn: return nameof(Spawn);
                case Despawn: return nameof(Despawn);
                case State: return nameof(State);
                case OwnershipChange: return nameof(OwnershipChange);
                case StateRelayBatch: return nameof(StateRelayBatch);
                case OwnershipRequest: return nameof(OwnershipRequest);
                case OwnershipRelease: return nameof(OwnershipRelease);
                case OwnershipDenied: return nameof(OwnershipDenied);
                case Rpc: return nameof(Rpc);
                case RpcRelay: return nameof(RpcRelay);
            }
            if (IsSession(type)) return "Session#" + type;
            if (IsSave(type)) return "Save#" + type;
            if (IsEntity(type)) return "Entity#" + type;
            if (IsRpc(type)) return "Rpc#" + type;
            if (IsGame(type)) return "Game#" + type;
            return "Unknown#" + type;
        }
    }
}
