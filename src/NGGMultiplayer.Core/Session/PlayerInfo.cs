using NGGMultiplayer.Core.Net;

namespace NGGMultiplayer.Core.Session
{
    /// <summary>
    /// One roster entry. An instance stays the same object for as long as the player is in the session,
    /// so references may be kept; <see cref="DisplayName"/> and <see cref="PingMs"/> update in place.
    /// </summary>
    public sealed class PlayerInfo
    {
        internal PlayerInfo(PeerId id, int playerIndex, string displayName, bool isHost, bool isLocal)
        {
            Id = id;
            PlayerIndex = playerIndex;
            DisplayName = displayName ?? string.Empty;
            IsHost = isHost;
            IsLocal = isLocal;
        }

        public PeerId Id { get; }

        /// <summary>Host is 0; clients get the lowest free index, which is reused after they leave.</summary>
        public int PlayerIndex { get; internal set; }

        public string DisplayName { get; internal set; }

        /// <summary>
        /// Round-trip time in milliseconds. On the host: measured to that client (0 for the host itself).
        /// On a client: its own RTT to the host for the local and host entries, the host's measurement for others.
        /// </summary>
        public int PingMs { get; internal set; }

        public bool IsHost { get; }

        public bool IsLocal { get; }

        public override string ToString() => DisplayName + " (#" + PlayerIndex + ", " + Id + ")";
    }
}
