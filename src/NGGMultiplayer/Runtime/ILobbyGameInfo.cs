using NGGMultiplayer.Core.Session;

namespace NGGMultiplayer.Runtime
{
    /// <summary>
    /// Game-specific lines for the lobby window (what the host is doing, where each player is, why joining is not
    /// possible). Supplied by the game adapter; the window works without it. Main thread; members never throw.
    /// </summary>
    public interface ILobbyGameInfo
    {
        /// <summary>A line about the session from this game's point of view; empty for none.</summary>
        string SessionStatus { get; }

        /// <summary>Something that limits hosting or joining (missing save protection, disabled features); empty for none.</summary>
        string Warning { get; }

        /// <summary>Where <paramref name="player"/> is and what they are doing; empty when unknown.</summary>
        string DescribePlayer(PlayerInfo player);
    }
}
