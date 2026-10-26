using System;
using BepInEx.Configuration;
using NGGMultiplayer.Core.Diagnostics;
using NGGMultiplayer.Runtime;
using UnityEngine;

namespace NGGMultiplayer
{
    /// <summary>The plugin's BepInEx config (BepInEx/config/ngg.multiplayer.cfg).</summary>
    internal sealed class PluginConfig
    {
        public PluginConfig(ConfigFile file)
        {
            File = file ?? throw new ArgumentNullException(nameof(file));
            ToggleWindow = file.Bind("General", "ToggleWindowKey", new KeyboardShortcut(KeyCode.F7),
                "Shows or hides the multiplayer window.");
            DisplayName = file.Bind("General", "DisplayName", string.Empty,
                "Name shown to other players. Empty uses your Steam name.");
            LobbyType = file.Bind("Lobby", "LobbyType", LobbyVisibility.FriendsOnly,
                "Who can join a lobby you host: Private (invite only), FriendsOnly, Public, Invisible (by id only).");
            MaxPlayers = file.Bind("Lobby", "MaxPlayers", 4, new ConfigDescription("Players per session, including the host.",
                new AcceptableValueRange<int>(MultiplayerSettings.MinPlayers, MultiplayerSettings.MaxPlayersLimit)));
            SendRate = file.Bind("Network", "SendRate", 30, new ConfigDescription(
                "Position updates per second sent for your golfer and ball. Applies from the next session.",
                new AcceptableValueRange<int>(5, 60)));
            InterpolationDelayMs = file.Bind("Network", "InterpolationDelayMs", 100, new ConfigDescription(
                "How far in the past other players are shown, in milliseconds. Higher is smoother on bad connections. " +
                "Applies from the next session.", new AcceptableValueRange<int>(0, 500)));
            TimeoutSeconds = file.Bind("Network", "TimeoutSeconds", 10, new ConfigDescription(
                "Seconds without traffic before a player is considered disconnected.",
                new AcceptableValueRange<int>(3, 60)));
            ScorecardKey = file.Bind("General", "ScorecardKey", Game.GameContext.DefaultScorecardKey,
                "Hold to show the session scorecard. The game itself uses Tab (club switch), so the default is H. " +
                "None turns the key off (the scorecard still opens after round events).");
            RemoteBallTrail = file.Bind("Presence", "RemoteBallTrail", true,
                "Draw a trail in the player's colour behind other players' balls while they fly and roll.");
            TrailSeconds = file.Bind("Presence", "TrailSeconds", 0f, new ConfigDescription(
                "How long the trail behind other players' balls lasts, in seconds. 0 matches the game's own ball trail.",
                new AcceptableValueRange<float>(0f, 10f)));
            Competitive = file.Bind("Match", "Competitive", true,
                "Just Play Nine in a session is a competitive round: everyone plays the same hole, strokes count per " +
                "player, with a leaderboard and a scorecard. Off: everyone plays the nine holes on their own. The host's " +
                "setting applies. Story mode is always played together.");
            TurnOrder = file.Bind("Match", "TurnOrder", Game.Presence.MatchMode.Free,
                "Turns: real golf, one shot at a time (best score on the last hole tees off first, then whoever is " +
                "farthest from the cup). Free: everyone plays the hole at once; the next hole still waits for all.");
            MaxStrokes = file.Bind("Match", "MaxStrokes", Game.MatchSettings.DefaultMaxStrokes, new ConfigDescription(
                "Strokes on one hole after which a player picks up and scores that many. 0 = no limit.",
                new AcceptableValueRange<int>(0, 30)));
            Character = file.Bind("Presence", "Character", PlayerLook.Male,
                "Male or Female: the golfer other players see you as. Also the Character selector of the multiplayer screens.");
            Transport = file.Bind("Network", "Transport", TransportKind.Steam,
                "Steam: lobbies and invites (normal play). Direct: plain UDP to HostAddress:DirectPort without Steam, for " +
                "two game copies on one PC or a trusted LAN (anyone who reaches the port can join). Needs a game restart.");
            HostAddress = file.Bind("Network", "HostAddress", MultiplayerSettings.DefaultHostAddress,
                "Direct transport: the host's address that Join connects to. On the host, a loopback address (127.0.0.1) " +
                "listens on this PC only; any other address listens on every network interface.");
            DirectPort = file.Bind("Network", "DirectPort", MultiplayerSettings.DefaultDirectPort, new ConfigDescription(
                "Direct transport: the UDP port the host listens on and clients join.", new AcceptableValueRange<int>(1, 65535)));
            SaveDirOverride = file.Bind("Debug", "SaveDirOverride", string.Empty,
                "Test copies only: a folder that replaces the game's save folder (AppData/LocalLow/Luke Muscat/Normal Golf " +
                "Game) for every save file the game and the mod read or write. Empty uses the normal folder. Needs a game restart.");
            PumpSteamCallbacks = file.Bind("Steam", "PumpCallbacks", false,
                "Run Steam callbacks from the mod. Only enable if lobbies never finish creating or joining " +
                "(the game's own Steam wrapper normally does this).");
            LogLevel = file.Bind("Debug", "LogLevel", Core.Diagnostics.LogLevel.Info,
                "Minimum level of networking messages written to the BepInEx log.");
        }

        public ConfigFile File { get; }

        public ConfigEntry<KeyboardShortcut> ToggleWindow { get; }

        public ConfigEntry<string> DisplayName { get; }

        public ConfigEntry<LobbyVisibility> LobbyType { get; }

        public ConfigEntry<int> MaxPlayers { get; }

        public ConfigEntry<int> SendRate { get; }

        public ConfigEntry<int> InterpolationDelayMs { get; }

        public ConfigEntry<int> TimeoutSeconds { get; }

        public ConfigEntry<bool> PumpSteamCallbacks { get; }

        public ConfigEntry<KeyCode> ScorecardKey { get; }

        public ConfigEntry<bool> RemoteBallTrail { get; }

        public ConfigEntry<float> TrailSeconds { get; }

        public ConfigEntry<PlayerLook> Character { get; }

        public ConfigEntry<bool> Competitive { get; }

        public ConfigEntry<Game.Presence.MatchMode> TurnOrder { get; }

        public ConfigEntry<int> MaxStrokes { get; }

        public ConfigEntry<TransportKind> Transport { get; }

        public ConfigEntry<string> HostAddress { get; }

        public ConfigEntry<int> DirectPort { get; }

        public ConfigEntry<string> SaveDirOverride { get; }

        public ConfigEntry<LogLevel> LogLevel { get; }

        public MultiplayerSettings ToSettings()
        {
            return new MultiplayerSettings
            {
                MaxPlayers = MaxPlayers.Value,
                SendRate = SendRate.Value,
                InterpolationDelay = InterpolationDelayMs.Value / 1000.0,
                TimeoutSeconds = TimeoutSeconds.Value,
                LobbyType = LobbyType.Value,
                DisplayName = DisplayName.Value,
                PumpSteamCallbacks = PumpSteamCallbacks.Value,
                HostAddress = HostAddress.Value,
                DirectPort = DirectPort.Value
            }.Clamped();
        }
    }
}
