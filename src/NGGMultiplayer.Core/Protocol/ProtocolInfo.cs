namespace NGGMultiplayer.Core.Protocol
{
    public static class ProtocolInfo
    {
        /// <summary>Wire protocol version; peers must match exactly.</summary>
        public const int Version = 2;

        /// <summary>Human-readable mod version, shown to the user on a version mismatch.</summary>
        public const string ModVersion = "1.1.0";

        /// <summary>Largest packet any transport must accept (Steam's reliable send limit).</summary>
        public const int MaxPacketSize = 512 * 1024;

        /// <summary>Keep unreliable packets at or below this to avoid fragmentation.</summary>
        public const int MaxUnreliablePacketSize = 1200;

        /// <summary>Maximum UTF-8 byte length of a string on the wire.</summary>
        public const int MaxStringBytes = 64 * 1024;
    }
}
