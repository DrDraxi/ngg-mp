using NGGMultiplayer.Core.Net;

namespace NGGMultiplayer.Testing
{
    /// <summary>
    /// Peer ids of the test harness's in-process bots. A real Steam account id carries universe 1 and account type 1 in
    /// its top 12 bits (0x011...), so an id with universe 0 ("invalid") can never belong to a Steam user: Steam never
    /// delivers a packet from one, and the composite transport routes these ids to the loopback hub only.
    /// </summary>
    public static class BotIds
    {
        /// <summary>Top 32 bits of every bot id ("B07B07", universe 0).</summary>
        public const ulong Prefix = 0x00B07B07UL;

        /// <summary>Most bots one harness runs at once (also bounded by the session's MaxPlayers).</summary>
        public const int MaxBots = 15;

        /// <summary>The id of bot number <paramref name="index"/> (1..<see cref="MaxBots"/>).</summary>
        public static PeerId Make(int index)
        {
            if (index < 1 || index > MaxBots)
                throw new System.ArgumentOutOfRangeException(nameof(index), index, $"Bot index must be in 1..{MaxBots}.");
            return new PeerId((Prefix << 32) | (uint)index);
        }

        /// <summary>True for every id <see cref="Make"/> can return.</summary>
        public static bool IsBot(PeerId peer)
        {
            if ((peer.Value >> 32) != Prefix)
                return false;
            uint index = (uint)peer.Value;
            return index >= 1 && index <= MaxBots;
        }

        /// <summary>The bot number of <paramref name="peer"/>, or 0 when it is not a bot id.</summary>
        public static int IndexOf(PeerId peer) => IsBot(peer) ? (int)(uint)peer.Value : 0;
    }
}
