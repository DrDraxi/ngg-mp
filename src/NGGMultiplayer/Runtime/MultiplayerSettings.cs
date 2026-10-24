using System;

namespace NGGMultiplayer.Runtime
{
    public enum LobbyVisibility
    {
        /// <summary>Joinable only through invites.</summary>
        Private,
        /// <summary>Friends can join from their friends list.</summary>
        FriendsOnly,
        Public,
        /// <summary>Not listed anywhere, but joinable by id.</summary>
        Invisible
    }

    /// <summary>How players reach each other.</summary>
    public enum TransportKind
    {
        /// <summary>Steam lobbies and Steam networking (the normal way to play).</summary>
        Steam,
        /// <summary>Plain UDP to an address and port (tests with two game copies on one PC, or a LAN); no Steam lobby.</summary>
        Direct
    }

    /// <summary>User-tunable values, copied from the plugin config.</summary>
    public sealed class MultiplayerSettings
    {
        public const int MinPlayers = 2;
        public const int MaxPlayersLimit = 16;

        /// <summary>Including the host.</summary>
        public int MaxPlayers { get; set; } = 4;

        /// <summary>Entity state updates per second.</summary>
        public double SendRate { get; set; } = 30;

        /// <summary>How far in the past remote entities are rendered, in seconds.</summary>
        public double InterpolationDelay { get; set; } = 0.1;

        /// <summary>A silent peer is dropped after this many seconds.</summary>
        public double TimeoutSeconds { get; set; } = 10;

        public LobbyVisibility LobbyType { get; set; } = LobbyVisibility.FriendsOnly;

        /// <summary>Overrides the platform name shown to other players when not empty.</summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>Pump Steam callbacks from the plugin, for games whose own Steam wrapper does not.</summary>
        public bool PumpSteamCallbacks { get; set; }

        public const string DefaultHostAddress = "127.0.0.1";
        public const int DefaultDirectPort = 27250;

        /// <summary>Direct transport: the host's address a client joins (a loopback host listens on loopback only).</summary>
        public string HostAddress { get; set; } = DefaultHostAddress;

        /// <summary>Direct transport: the UDP port the host listens on.</summary>
        public int DirectPort { get; set; } = DefaultDirectPort;

        /// <summary>A copy with every value forced into its valid range.</summary>
        public MultiplayerSettings Clamped()
        {
            return new MultiplayerSettings
            {
                MaxPlayers = Math.Max(MinPlayers, Math.Min(MaxPlayersLimit, MaxPlayers)),
                SendRate = Clamp(SendRate, 1, 120, 30),
                InterpolationDelay = Clamp(InterpolationDelay, 0, 1, 0.1),
                TimeoutSeconds = Clamp(TimeoutSeconds, 2, 120, 10),
                LobbyType = Enum.IsDefined(typeof(LobbyVisibility), LobbyType) ? LobbyType : LobbyVisibility.FriendsOnly,
                DisplayName = DisplayName?.Trim() ?? string.Empty,
                PumpSteamCallbacks = PumpSteamCallbacks,
                HostAddress = string.IsNullOrWhiteSpace(HostAddress) ? DefaultHostAddress : HostAddress.Trim(),
                DirectPort = DirectPort >= 1 && DirectPort <= 65535 ? DirectPort : DefaultDirectPort
            };
        }

        private static double Clamp(double value, double min, double max, double fallback)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return fallback;
            return value < min ? min : value > max ? max : value;
        }
    }
}
