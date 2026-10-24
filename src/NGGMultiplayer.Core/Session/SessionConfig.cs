using System;
using System.Text;

namespace NGGMultiplayer.Core.Session
{
    /// <summary>
    /// Session tunables. Values are read live, so changes apply from the next <see cref="SessionManager.Update"/>;
    /// they are validated when the manager is created and whenever a session starts.
    /// </summary>
    public sealed class SessionConfig
    {
        /// <summary>Hard cap on roster size, independent of <see cref="MaxPlayers"/>; also bounds roster parsing.</summary>
        public const int AbsoluteMaxPlayers = 64;
        public const int MaxDisplayNameChars = 32;
        public const int MaxGameBuildBytes = 128;

        public string DisplayName { get; set; } = "Player";

        /// <summary>Game version string sent in Hello (e.g. Unity's Application.version).</summary>
        public string GameBuild { get; set; } = string.Empty;

        /// <summary>When true, the host rejects clients whose <see cref="GameBuild"/> differs from its own.</summary>
        public bool RequireMatchingGameBuild { get; set; } = true;

        /// <summary>Including the host. 1..<see cref="AbsoluteMaxPlayers"/>.</summary>
        public int MaxPlayers { get; set; } = 8;

        /// <summary>A peer silent for longer than this is dropped (host side) or ends the session (client side).</summary>
        public double TimeoutSeconds { get; set; } = 10;

        /// <summary>How long a client waits for Welcome/Reject before giving up.</summary>
        public double ConnectTimeoutSeconds { get; set; } = 10;

        public double HelloRetryIntervalSeconds { get; set; } = 1;

        /// <summary>Ping cadence in both directions; pings double as heartbeats.</summary>
        public double PingIntervalSeconds { get; set; } = 1;

        /// <summary>The host re-sends the roster at this interval so clients see fresh pings. Joins and leaves are sent at once.</summary>
        public double RosterRefreshIntervalSeconds { get; set; } = 2;

        /// <summary>EWMA weight of a new RTT sample.</summary>
        public double RttSmoothing { get; set; } = NetClock.DefaultRttSmoothing;

        /// <summary>EWMA weight of a new clock-offset sample.</summary>
        public double OffsetSmoothing { get; set; } = NetClock.DefaultOffsetSmoothing;

        /// <exception cref="ArgumentException">A value is out of range.</exception>
        public void Validate()
        {
            if (MaxPlayers < 1 || MaxPlayers > AbsoluteMaxPlayers)
                throw new ArgumentException($"MaxPlayers must be in [1, {AbsoluteMaxPlayers}].", nameof(MaxPlayers));
            RequirePositive(TimeoutSeconds, nameof(TimeoutSeconds));
            RequirePositive(ConnectTimeoutSeconds, nameof(ConnectTimeoutSeconds));
            RequirePositive(HelloRetryIntervalSeconds, nameof(HelloRetryIntervalSeconds));
            RequirePositive(PingIntervalSeconds, nameof(PingIntervalSeconds));
            RequirePositive(RosterRefreshIntervalSeconds, nameof(RosterRefreshIntervalSeconds));
            RequireUnitInterval(RttSmoothing, nameof(RttSmoothing));
            RequireUnitInterval(OffsetSmoothing, nameof(OffsetSmoothing));
            if (GameBuild != null && Encoding.UTF8.GetByteCount(GameBuild) > MaxGameBuildBytes)
                throw new ArgumentException($"GameBuild must be at most {MaxGameBuildBytes} UTF-8 bytes.", nameof(GameBuild));
        }

        private static void RequirePositive(double value, string name)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
                throw new ArgumentException(name + " must be finite and positive.", name);
        }

        private static void RequireUnitInterval(double value, string name)
        {
            if (double.IsNaN(value) || value <= 0 || value > 1)
                throw new ArgumentException(name + " must be in (0, 1].", name);
        }
    }
}
