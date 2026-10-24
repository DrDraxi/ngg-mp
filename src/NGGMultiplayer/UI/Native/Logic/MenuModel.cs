using System.Collections.Generic;
using NGGMultiplayer.Runtime;
using NGGMultiplayer.Runtime.Lobbies;

namespace NGGMultiplayer.UI.Native
{
    /// <summary>Where the multiplayer screens were opened from; Back from the first screen returns there.</summary>
    public enum MenuContext
    {
        /// <summary>The game's main menu (Menu2).</summary>
        MainMenu,
        /// <summary>The pause menu in the world (Main).</summary>
        Pause
    }

    public enum MenuItemKind
    {
        /// <summary>A plain button.</summary>
        Button,
        /// <summary>A button with a value that Left / Right (or a click) cycles: "Who Can Join: Friends Only".</summary>
        Selector,
        /// <summary>A two-line entry of a list (a game, a player): <see cref="MenuItem.Label"/> and <see cref="MenuItem.Detail"/>.</summary>
        Row
    }

    /// <summary>How an entry is coloured: the game's settings blue, green for the step forward, red for leaving,
    /// the darker Back blue, teal for inviting.</summary>
    public enum MenuStyle
    {
        Normal,
        Primary,
        Danger,
        Back,
        Accent
    }

    /// <summary>One entry of a screen. <see cref="Id"/> is what a click reports back.</summary>
    public sealed class MenuItem
    {
        public MenuItem(string id, MenuItemKind kind, string label, string detail = "", bool enabled = true, string value = "",
            MenuStyle style = MenuStyle.Normal)
        {
            Style = id == "back" && style == MenuStyle.Normal ? MenuStyle.Back : style;
            Id = id;
            Kind = kind;
            Label = label ?? string.Empty;
            Detail = detail ?? string.Empty;
            Enabled = enabled;
            Value = value ?? string.Empty;
        }

        public string Id { get; }

        public MenuItemKind Kind { get; }

        public MenuStyle Style { get; }

        public string Label { get; }

        /// <summary>Rows: the second line.</summary>
        public string Detail { get; }

        public bool Enabled { get; }

        /// <summary>Selectors: the value shown after the label.</summary>
        public string Value { get; }

        /// <summary>The text of a one-line button: the label, plus a selector's value.</summary>
        public string Text => Kind == MenuItemKind.Selector ? Label + ": " + Value : Label;

        public override string ToString() => Id + "=" + Text + (Detail.Length > 0 ? " (" + Detail + ")" : string.Empty) + (Enabled ? string.Empty : " [off]");
    }

    /// <summary>What one screen shows: a title, a line under it, the entries, and an empty-list text.</summary>
    public sealed class MenuScreen
    {
        public MenuScreen(string id, string title, string subtitle, IReadOnlyList<MenuItem> items, string empty = "")
        {
            Id = id;
            Title = title ?? string.Empty;
            Subtitle = subtitle ?? string.Empty;
            Items = items ?? new List<MenuItem>();
            Empty = empty ?? string.Empty;
        }

        public string Id { get; }

        public string Title { get; }

        public string Subtitle { get; }

        public IReadOnlyList<MenuItem> Items { get; }

        /// <summary>Shown in place of the rows when a list has none ("No games found...").</summary>
        public string Empty { get; }

        /// <summary>
        /// Changes when the entries' structure changes (ids and kinds), not when only texts change; the view rebuilds
        /// its buttons on a new layout and updates texts in place otherwise.
        /// </summary>
        public string Layout
        {
            get
            {
                var parts = new List<string> { Id };
                foreach (var item in Items)
                    parts.Add(item.Id + ":" + (int)item.Kind);
                return string.Join("|", parts.ToArray());
            }
        }

        public MenuItem Find(string id)
        {
            foreach (var item in Items)
            {
                if (item.Id == id)
                    return item;
            }
            return null;
        }
    }

    /// <summary>A player as the Players screen lists them.</summary>
    public sealed class MenuPlayer
    {
        public ulong Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public bool IsHost { get; set; }

        public bool IsLocal { get; set; }

        public int PingMs { get; set; }

        /// <summary>Where they are ("Golfing", "In the menu"); "" unknown.</summary>
        public string Where { get; set; } = string.Empty;
    }

    /// <summary>Everything the screens are built from, read once per refresh from the service and the game.</summary>
    public sealed class MenuSnapshot
    {
        public MultiplayerState State { get; set; } = MultiplayerState.Offline;

        public bool IsHost { get; set; }

        /// <summary>The main menu's Just Play Nine can be pressed (it is unlocked and shown).</summary>
        public bool CanPlayNine { get; set; }

        public string UnavailableReason { get; set; } = string.Empty;

        /// <summary>The service's latest event ("Bob joined.", "Could not join: it is full.").</summary>
        public string StatusMessage { get; set; } = string.Empty;

        /// <summary>Something that limits hosting or joining (from the game integration).</summary>
        public string Warning { get; set; } = string.Empty;

        /// <summary>The session from the game's point of view ("You are in the menu. Start a game and everyone follows.").</summary>
        public string SessionStatus { get; set; } = string.Empty;

        public string HostName { get; set; } = string.Empty;

        public IReadOnlyList<MenuPlayer> Players { get; set; } = new List<MenuPlayer>();

        /// <summary>Steam lobbies (privacy, invites, browser). False: Direct transport or none.</summary>
        public bool HasLobbies { get; set; }

        public bool CanInvite { get; set; }

        public LobbyVisibility Visibility { get; set; } = LobbyVisibility.FriendsOnly;

        public int MaxPlayers { get; set; } = 4;

        /// <summary>Direct transport: the address joined ("127.0.0.1:27250"); "" on Steam.</summary>
        public string DirectEndpoint { get; set; } = string.Empty;

        /// <summary>0..1 while loading the host's save.</summary>
        public float SyncProgress { get; set; }

        public IReadOnlyList<LobbyListing> Lobbies { get; set; } = new List<LobbyListing>();

        public bool Refreshing { get; set; }

        public string LocalProtocol { get; set; } = string.Empty;

        /// <summary>The player is in their own world (Main, offline), so joining replaces it.</summary>
        public bool InOwnWorld { get; set; }

        public PendingJoin PendingJoin { get; set; }

        /// <summary>The golfer this player is drawn as for the others ([Presence] Character).</summary>
        public PlayerLook Look { get; set; } = PlayerLook.Male;

        public bool InSession => State == MultiplayerState.InSession || State == MultiplayerState.Syncing;

        public bool Busy => State == MultiplayerState.CreatingLobby || State == MultiplayerState.JoiningLobby
            || State == MultiplayerState.Connecting || State == MultiplayerState.Syncing;
    }

    /// <summary>What hosting from a menu does once the session is up.</summary>
    public enum HostStart
    {
        /// <summary>Stay where the player is (hosting from the pause menu: friends join this game).</summary>
        Stay,
        /// <summary>Press the main menu's Play (the game's own continue / new game screens follow).</summary>
        Play,
        /// <summary>Press the main menu's Just Play Nine.</summary>
        PlayNine
    }

    /// <summary>The menus' commands, carried out by the service (and the game) behind them.</summary>
    public interface IMenuActions
    {
        void Host(LobbyVisibility visibility, int maxPlayers, HostStart start);

        void Leave();

        void Invite();

        void Join(LobbyListing listing);

        void Kick(ulong playerId);

        void SetVisibility(LobbyVisibility visibility);

        void Refresh();

        void AnswerPendingJoin(bool accept);

        /// <summary>Draws this player as <paramref name="look"/> for the others from now on (and remembers it).</summary>
        void SetLook(PlayerLook look);
    }
}
