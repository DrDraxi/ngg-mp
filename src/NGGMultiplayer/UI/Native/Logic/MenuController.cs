using System;
using System.Collections.Generic;
using System.Globalization;
using NGGMultiplayer.Runtime;
using NGGMultiplayer.Runtime.Lobbies;

namespace NGGMultiplayer.UI.Native
{
    /// <summary>
    /// The multiplayer screens and the way between them, Unity-free: which screen is open, what it shows for the
    /// current <see cref="MenuSnapshot"/>, and what a click, a Left / Right on a selector or Back does. The view
    /// (<c>NativeMenus</c>) only draws <see cref="Build"/>'s result with the game's own buttons and reports input.
    /// <para>The first screen follows the session: "Multiplayer" offline, the session screen in a session, the busy
    /// screen while hosting / joining. A screen whose premise is gone (a player who left, a confirm for a session
    /// that ended) closes by itself.</para>
    /// </summary>
    public sealed class MenuController
    {
        public const string Main = "mp";
        public const string HostScreen = "host";
        public const string Session = "session";
        public const string Busy = "busy";
        public const string Players = "players";
        public const string Browse = "browse";
        public const string Confirm = "confirm";
        public const string JoinRequest = "joinrequest";

        public const string Back = "back";
        /// <summary>The Character selector (male / female golfer) on the Multiplayer, Host, Join and session screens.</summary>
        public const string Look = "look";
        public const int MinPlayers = 2;
        public const int MaxPlayersShown = 8;
        /// <summary>Rows per page of the game and player lists (the frame grows with its content).</summary>
        public const int PageSize = 5;

        private static readonly LobbyVisibility[] Privacy = { LobbyVisibility.Private, LobbyVisibility.FriendsOnly, LobbyVisibility.Public };

        private readonly IMenuActions _actions;
        private readonly List<string> _stack = new List<string>();
        private LobbyVisibility _hostVisibility = LobbyVisibility.FriendsOnly;
        private int _hostMaxPlayers = 4;
        private ConfirmSpec _confirm;
        private int _page;

        public MenuController(IMenuActions actions)
        {
            _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        }

        public bool IsOpen => _stack.Count > 0;

        /// <summary>The open screen's id, or null.</summary>
        public string Current => _stack.Count > 0 ? _stack[_stack.Count - 1] : null;

        public MenuContext Context { get; private set; }

        public int Depth => _stack.Count;

        /// <summary>Opens the multiplayer screens from the main or pause menu (the first screen follows the session).</summary>
        public void Open(MenuContext context) => Open(context, Main);

        /// <summary>
        /// Opens straight onto <paramref name="screen"/> (the main menu panel's Host Game / Join Game go to the host
        /// and browse screens); Back from it closes the multiplayer screens.
        /// </summary>
        public void Open(MenuContext context, string screen)
        {
            Context = context;
            _stack.Clear();
            _confirm = null;
            _page = 0;
            _stack.Add(string.IsNullOrEmpty(screen) ? Main : screen);
        }

        /// <summary>Opens <paramref name="screen"/> with the host screen's choices taken from <paramref name="s"/>.</summary>
        public void Open(MenuContext context, string screen, MenuSnapshot s)
        {
            Open(context, screen);
            if (screen == HostScreen && s != null)
                ResetHostChoices(s);
            if (screen == Browse)
                _actions.Refresh();
        }

        /// <summary>Opens straight onto the confirm screen of a Steam join request.</summary>
        public void OpenJoinRequest(MenuContext context)
        {
            if (!IsOpen)
                Open(context);
            if (Current != JoinRequest)
                _stack.Add(JoinRequest);
        }

        public void Close()
        {
            _stack.Clear();
            _confirm = null;
        }

        /// <summary>
        /// Back (the Back entry, Escape, the gamepad's B). False when the first screen was open: the multiplayer
        /// screens are closed and the caller shows the game's menu again.
        /// </summary>
        public bool GoBack(MenuSnapshot s)
        {
            if (_stack.Count == 0)
                return false;
            string top = Current;
            if (top == JoinRequest)
                _actions.AnswerPendingJoin(false);
            _stack.RemoveAt(_stack.Count - 1);
            _page = 0;
            if (top == Confirm)
                _confirm = null;
            return _stack.Count > 0;
        }

        /// <summary>The open screen for <paramref name="s"/>; null when closed. Fixes up the screen stack first.</summary>
        public MenuScreen Build(MenuSnapshot s)
        {
            if (!IsOpen || s == null)
                return null;
            Settle(s);
            switch (Current)
            {
                case Main: return BuildMain(s);
                case HostScreen: return BuildHost(s);
                case Session: return BuildSession(s);
                case Busy: return BuildBusy(s);
                case Players: return BuildPlayers(s);
                case Browse: return BuildBrowse(s);
                case Confirm: return BuildConfirm();
                case JoinRequest: return BuildJoinRequest(s);
                default: return BuildMain(s);
            }
        }

        /// <summary>A click on (or Submit of) item <paramref name="id"/> of the open screen.</summary>
        public void Activate(string id, MenuSnapshot s)
        {
            if (!IsOpen || s == null || string.IsNullOrEmpty(id))
                return;
            var screen = Build(s);
            var item = screen?.Find(id);
            if (item == null || !item.Enabled)
                return;
            if (id == Back)
            {
                GoBack(s);
                return;
            }
            if (item.Kind == MenuItemKind.Selector)
            {
                Change(id, 1, s);
                return;
            }
            if (id == "page")
            {
                _page++;
                return;
            }
            switch (Current)
            {
                case Main:
                    if (id == "host")
                    {
                        ResetHostChoices(s);
                        Push(HostScreen);
                    }
                    else if (id == "browse")
                    {
                        Push(Browse);
                        _actions.Refresh();
                    }
                    else if (id == Session)
                    {
                        Push(Session);
                    }
                    break;
                case HostScreen:
                    if (id == "start")
                        _actions.Host(_hostVisibility, _hostMaxPlayers, HostStart.Stay);
                    else if (id == "play")
                        _actions.Host(_hostVisibility, _hostMaxPlayers, HostStart.Play);
                    else if (id == "playnine")
                        _actions.Host(_hostVisibility, _hostMaxPlayers, HostStart.PlayNine);
                    break;
                case Session:
                    if (id == "invite")
                        _actions.Invite();
                    else if (id == Players)
                        Push(Players);
                    else if (id == "leave")
                        AskLeave(s);
                    break;
                case Busy:
                    if (id == "cancel")
                        _actions.Leave();
                    break;
                case Players:
                    if (id.StartsWith("player:", StringComparison.Ordinal) && s.IsHost
                        && ulong.TryParse(id.Substring(7), NumberStyles.None, CultureInfo.InvariantCulture, out ulong player))
                    {
                        var target = FindPlayer(s, player);
                        if (target != null && !target.IsLocal)
                            Ask(new ConfirmSpec("Remove Player", "Remove " + target.Name + " from your game?", "Remove", "Cancel",
                                () => _actions.Kick(player), snap => FindPlayer(snap, player) != null && snap.IsHost, danger: true));
                    }
                    else if (id == "invite")
                        _actions.Invite();
                    break;
                case Browse:
                    if (id == "refresh")
                        _actions.Refresh();
                    else
                    {
                        var listing = FindListing(s, id);
                        if (listing != null && LobbyListings.CanJoin(listing, s.LocalProtocol, out _))
                            JoinListing(s, listing);
                    }
                    break;
                case Confirm:
                    if (id == "yes")
                    {
                        var confirm = _confirm;
                        Pop(Confirm);
                        confirm?.OnYes();
                    }
                    else if (id == "no")
                        Pop(Confirm);
                    break;
                case JoinRequest:
                    if (id == "yes" || id == "no")
                    {
                        _stack.Remove(JoinRequest);
                        _actions.AnswerPendingJoin(id == "yes");
                        if (id == "yes" && _stack.Count == 0)
                            _stack.Add(Main);
                    }
                    break;
            }
        }

        /// <summary>Left (-1) / Right (+1) on a selector.</summary>
        public void Change(string id, int delta, MenuSnapshot s)
        {
            if (!IsOpen || s == null || delta == 0)
                return;
            int step = delta > 0 ? 1 : -1;
            switch (id)
            {
                case "privacy":
                    if (Current == HostScreen)
                        _hostVisibility = Cycle(_hostVisibility, step);
                    else if (Current == Session && s.IsHost && s.HasLobbies)
                        _actions.SetVisibility(Cycle(Supported(s.Visibility), step));
                    break;
                case Look:
                    _actions.SetLook(s.Look == PlayerLook.Female ? PlayerLook.Male : PlayerLook.Female);
                    break;
                case "maxplayers":
                    if (Current == HostScreen)
                    {
                        int next = _hostMaxPlayers + step;
                        _hostMaxPlayers = next > MaxPlayersShown ? MinPlayers : next < MinPlayers ? MaxPlayersShown : next;
                    }
                    break;
            }
        }

        /// <summary>
        /// The two buttons of the multiplayer panel on the main menu, top first. Ids: "host" / "browse" (offline),
        /// "invite" / "session" (hosting), "session" / "leave" (in someone's game), "busy" / "cancel" (on the way).
        /// <see cref="OpenFromPanel"/> carries them out.
        /// </summary>
        public static MenuItem[] PanelButtons(MenuSnapshot s)
        {
            if (s.Busy && !(s.State == MultiplayerState.Syncing && s.IsHost))
            {
                string what = s.State == MultiplayerState.CreatingLobby ? "Starting..." : "Joining...";
                return new[]
                {
                    new MenuItem("busy", MenuItemKind.Button, what),
                    new MenuItem("cancel", MenuItemKind.Button, "Cancel", style: MenuStyle.Danger)
                };
            }
            if (s.InSession && s.IsHost)
            {
                return new[]
                {
                    new MenuItem("invite", MenuItemKind.Button, s.CanInvite ? "Invite Friends" : "Players", style: MenuStyle.Accent),
                    new MenuItem(Session, MenuItemKind.Button, "Hosting (" + PlayerCount(s) + ")")
                };
            }
            if (s.InSession)
            {
                return new[]
                {
                    new MenuItem(Session, MenuItemKind.Button, LobbyListings.Possessive(s.HostName) + " Game", style: MenuStyle.Accent),
                    new MenuItem("leave", MenuItemKind.Button, "Leave Session", style: MenuStyle.Danger)
                };
            }
            return new[]
            {
                new MenuItem("host", MenuItemKind.Button, "Host Game", style: MenuStyle.Accent),
                new MenuItem("browse", MenuItemKind.Button, s.HasLobbies || s.DirectEndpoint.Length == 0 ? "Join Game" : "Join Direct")
            };
        }

        /// <summary>A click on one of <see cref="PanelButtons"/>: opens the screen it leads to (or acts at once).</summary>
        public void OpenFromPanel(string id, MenuContext context, MenuSnapshot s)
        {
            switch (id)
            {
                case "host":
                    Open(context, s.State == MultiplayerState.Unavailable ? Main : HostScreen, s);
                    break;
                case "browse":
                    if (s.State == MultiplayerState.Unavailable)
                        Open(context, Main);
                    else
                        Open(context, Browse, s);
                    break;
                case "invite":
                    if (s.CanInvite)
                        _actions.Invite();
                    else
                        Open(context, Players);
                    break;
                case "leave":
                    Open(context, Session);
                    Activate("leave", s);
                    break;
                case "cancel":
                    _actions.Leave();
                    break;
                default:
                    Open(context, Main);
                    break;
            }
        }

        // ---- Screens ------------------------------------------------------------------------------------

        private MenuScreen BuildMain(MenuSnapshot s)
        {
            var items = new List<MenuItem>();
            string subtitle;
            if (s.State == MultiplayerState.Unavailable)
            {
                subtitle = s.UnavailableReason.Length > 0 ? s.UnavailableReason : "Multiplayer is not available.";
            }
            else
            {
                subtitle = FirstOf(s.Warning, s.StatusMessage,
                    s.HasLobbies ? "Play together on one save: the host's. Joining never changes your own save." :
                    "Direct connection to " + s.DirectEndpoint + ".");
                items.Add(new MenuItem("host", MenuItemKind.Button, s.InOwnWorld ? "Host This Game" : "Host Game"));
                items.Add(new MenuItem("browse", MenuItemKind.Button, s.HasLobbies ? "Join Game" : "Join " + s.DirectEndpoint));
                items.Add(LookItem(s));
            }
            items.Add(new MenuItem(Back, MenuItemKind.Button, "Back"));
            return new MenuScreen(Main, "Multiplayer", subtitle, items);
        }

        private MenuScreen BuildHost(MenuSnapshot s)
        {
            var items = new List<MenuItem>();
            if (s.HasLobbies)
                items.Add(new MenuItem("privacy", MenuItemKind.Selector, "Who Can Join", value: PrivacyName(_hostVisibility)));
            items.Add(new MenuItem("maxplayers", MenuItemKind.Selector, "Players", value: _hostMaxPlayers.ToString(CultureInfo.InvariantCulture)));
            items.Add(LookItem(s));
            // From the main menu hosting goes straight into the game, like the menu's own Play / Just Play Nine;
            // from the pause menu the player is already in it.
            bool fromMenu = Context == MenuContext.MainMenu;
            if (fromMenu)
            {
                items.Add(new MenuItem("play", MenuItemKind.Button, "Play", style: MenuStyle.Primary));
                if (s.CanPlayNine)
                    items.Add(new MenuItem("playnine", MenuItemKind.Button, "Just Play Nine", style: MenuStyle.Accent));
            }
            else
            {
                items.Add(new MenuItem("start", MenuItemKind.Button, "Start Hosting", style: MenuStyle.Primary));
            }
            items.Add(new MenuItem(Back, MenuItemKind.Button, "Back"));
            string subtitle = s.HasLobbies ? PrivacyHelp(_hostVisibility) : "Players join at " + s.DirectEndpoint + ".";
            subtitle += s.InOwnWorld ? " Friends join you right here, in your save."
                : fromMenu ? " Friends join you in your game, on your save." : " Your save is the one everyone plays on.";
            return new MenuScreen(HostScreen, "Host Game", FirstOf(s.Warning, subtitle), items);
        }

        private MenuScreen BuildSession(MenuSnapshot s)
        {
            var items = new List<MenuItem>();
            if (s.CanInvite)
                items.Add(new MenuItem("invite", MenuItemKind.Button, "Invite Friends", style: MenuStyle.Accent));
            items.Add(new MenuItem(Players, MenuItemKind.Button, "Players (" + PlayerCount(s) + ")"));
            if (s.IsHost && s.HasLobbies)
                items.Add(new MenuItem("privacy", MenuItemKind.Selector, "Who Can Join", value: PrivacyName(Supported(s.Visibility))));
            items.Add(LookItem(s));
            items.Add(new MenuItem("leave", MenuItemKind.Button, s.IsHost ? "End Session" : "Leave Session", style: MenuStyle.Danger));
            items.Add(new MenuItem(Back, MenuItemKind.Button, "Back"));
            string title = s.IsHost ? "Hosting" : LobbyListings.Possessive(s.HostName) + " Game";
            string subtitle = FirstOf(s.SessionStatus, s.StatusMessage);
            return new MenuScreen(Session, title, subtitle, items);
        }

        private MenuScreen BuildBusy(MenuSnapshot s)
        {
            string title;
            string subtitle = s.StatusMessage;
            switch (s.State)
            {
                case MultiplayerState.CreatingLobby:
                    title = "Starting Your Game";
                    break;
                case MultiplayerState.Syncing:
                    title = s.HostName.Length > 0 ? "Joining " + LobbyListings.Possessive(s.HostName) + " Game" : "Joining";
                    subtitle = s.SyncProgress > 0f
                        ? "Loading the host's save: " + Math.Round(s.SyncProgress * 100f).ToString(CultureInfo.InvariantCulture) + "%"
                        : "Waiting for the host's save...";
                    break;
                default:
                    title = "Joining";
                    break;
            }
            var items = new List<MenuItem> { new MenuItem("cancel", MenuItemKind.Button, "Cancel", style: MenuStyle.Back) };
            return new MenuScreen(Busy, title, subtitle, items);
        }

        private MenuScreen BuildPlayers(MenuSnapshot s)
        {
            var items = new List<MenuItem>();
            int pages;
            foreach (var p in Page(s.Players, out pages))
            {
                var detail = new List<string>();
                if (p.IsHost)
                    detail.Add("Host");
                if (p.IsLocal)
                    detail.Add("You");
                else
                    detail.Add(p.PingMs.ToString(CultureInfo.InvariantCulture) + " ms");
                if (p.Where.Length > 0)
                    detail.Add(p.Where);
                bool kickable = s.IsHost && !p.IsLocal;
                items.Add(new MenuItem("player:" + p.Id.ToString(CultureInfo.InvariantCulture), MenuItemKind.Row, p.Name,
                    string.Join(" · ", detail.ToArray()), kickable));
            }
            AddPager(items, pages);
            if (s.CanInvite)
                items.Add(new MenuItem("invite", MenuItemKind.Button, "Invite Friends", style: MenuStyle.Accent));
            items.Add(new MenuItem(Back, MenuItemKind.Button, "Back"));
            string subtitle = s.IsHost && s.Players.Count > 1 ? "Select a player to remove them." : string.Empty;
            return new MenuScreen(Players, "Players (" + PlayerCount(s) + ")", subtitle, items);
        }

        private MenuScreen BuildBrowse(MenuSnapshot s)
        {
            var items = new List<MenuItem>();
            int pages;
            foreach (var listing in Page(s.Lobbies, out pages))
            {
                bool joinable = LobbyListings.CanJoin(listing, s.LocalProtocol, out _);
                items.Add(new MenuItem(listing.Key, MenuItemKind.Row, LobbyListings.Title(listing),
                    LobbyListings.Detail(listing, s.LocalProtocol), joinable));
            }
            AddPager(items, pages);
            if (s.HasLobbies)
                items.Add(new MenuItem("refresh", MenuItemKind.Button, s.Refreshing ? "Searching..." : "Refresh", enabled: !s.Refreshing));
            items.Add(LookItem(s));
            items.Add(new MenuItem(Back, MenuItemKind.Button, "Back"));
            string empty = s.Refreshing ? "Looking for games..." :
                "No games found. Games your Steam friends host show up here, and so do public games. A friend can also invite you from their pause menu.";
            string subtitle = FirstOf(s.Warning, s.StatusMessage);
            return new MenuScreen(Browse, "Join Game", subtitle, items, s.Lobbies.Count == 0 ? empty : string.Empty);
        }

        private MenuScreen BuildConfirm()
        {
            var c = _confirm;
            var items = new List<MenuItem>
            {
                new MenuItem("yes", MenuItemKind.Button, c?.Yes ?? "Yes", style: c != null && c.Danger ? MenuStyle.Danger : MenuStyle.Primary),
                new MenuItem("no", MenuItemKind.Button, c?.No ?? "No", style: MenuStyle.Back)
            };
            return new MenuScreen(Confirm, c?.Title ?? string.Empty, c?.Message ?? string.Empty, items);
        }

        private MenuScreen BuildJoinRequest(MenuSnapshot s)
        {
            string host = s.PendingJoin?.HostName ?? string.Empty;
            string title = host.Length > 0 ? "Join " + LobbyListings.Possessive(host) + " Game?" : "Join Game?";
            string message;
            if (s.InSession)
                message = s.IsHost ? "This ends the session you are hosting." : "This leaves the game you are in.";
            else if (s.InOwnWorld)
                message = "Your game is saved first. You play on the host's save; yours is untouched.";
            else
                message = "You play on the host's save; yours is untouched.";
            var items = new List<MenuItem>
            {
                new MenuItem("yes", MenuItemKind.Button, "Join", style: MenuStyle.Primary),
                new MenuItem("no", MenuItemKind.Button, "Not Now", style: MenuStyle.Back)
            };
            return new MenuScreen(JoinRequest, title, message, items);
        }

        // ---- Navigation ---------------------------------------------------------------------------------

        /// <summary>Keeps the stack in line with the session: the first screen per state, finished steps closed.</summary>
        private void Settle(MenuSnapshot s)
        {
            if (_stack.Count == 0)
                return;
            // A join request that was answered elsewhere (or expired).
            if (Current == JoinRequest && s.PendingJoin == null)
                _stack.RemoveAt(_stack.Count - 1);
            if (Current == Confirm && (_confirm == null || (_confirm.StillValid != null && !_confirm.StillValid(s))))
                Pop(Confirm);
            if (_stack.Count == 0)
            {
                _stack.Add(Main);
                return;
            }

            string root = RootFor(s);
            // The flow screens that belong to one state collapse onto the root for the current state.
            string top = Current;
            bool topIsFlow = top == Main || top == Session || top == Busy || top == HostScreen || top == Browse || top == Players;
            if (!topIsFlow)
                return;
            if (root == Busy && (top == HostScreen || top == Browse || top == Main || top == Session || top == Players))
            {
                Replace(Busy);
                return;
            }
            if (root == Session && (top == Busy || top == Main || top == HostScreen || top == Browse))
            {
                // Arrived in a session: the session screen replaces the steps that led there.
                while (_stack.Count > 1 && (Current == Busy || Current == HostScreen || Current == Browse))
                    _stack.RemoveAt(_stack.Count - 1);
                Replace(Session);
                return;
            }
            if (root == Main && (top == Busy || top == Session || top == Players))
            {
                while (_stack.Count > 1 && (Current == Busy || Current == Session || Current == Players))
                    _stack.RemoveAt(_stack.Count - 1);
                Replace(Main);
            }
        }

        private static string RootFor(MenuSnapshot s)
        {
            if (s.Busy && !(s.State == MultiplayerState.Syncing && s.IsHost))
                return Busy;
            return s.InSession ? Session : Main;
        }

        private void Push(string screen)
        {
            if (Current != screen)
            {
                _stack.Add(screen);
                _page = 0;
            }
        }

        private void ResetHostChoices(MenuSnapshot s)
        {
            _hostVisibility = s.HasLobbies ? Supported(s.Visibility) : LobbyVisibility.FriendsOnly;
            _hostMaxPlayers = ClampPlayers(s.MaxPlayers);
        }

        private List<T> Page<T>(IReadOnlyList<T> all, out int pages)
        {
            pages = Math.Max(1, (all.Count + PageSize - 1) / PageSize);
            int page = _page % pages;
            var list = new List<T>();
            for (int i = page * PageSize; i < all.Count && i < (page + 1) * PageSize; i++)
                list.Add(all[i]);
            return list;
        }

        private void AddPager(List<MenuItem> items, int pages)
        {
            if (pages > 1)
                items.Add(new MenuItem("page", MenuItemKind.Button, "More (" + (_page % pages + 1) + "/" + pages + ")"));
        }

        private void Replace(string screen)
        {
            if (_stack.Count == 0)
                _stack.Add(screen);
            else
                _stack[_stack.Count - 1] = screen;
        }

        private void Pop(string screen)
        {
            if (Current == screen)
                _stack.RemoveAt(_stack.Count - 1);
            if (screen == Confirm)
                _confirm = null;
        }

        private void Ask(ConfirmSpec spec)
        {
            _confirm = spec;
            Push(Confirm);
        }

        private void AskLeave(MenuSnapshot s)
        {
            if (s.IsHost)
            {
                int others = Math.Max(0, PlayerCountValue(s) - 1);
                string message = others > 0
                    ? "Everyone else goes back to the menu, with their own saves. Your progress stays in your save."
                    : "Nobody else is in your game. Your progress stays in your save.";
                Ask(new ConfirmSpec("End Session?", message, "End Session", "Cancel", _actions.Leave, snap => snap.InSession && snap.IsHost, danger: true));
            }
            else
            {
                Ask(new ConfirmSpec("Leave Session?", "You go back to the menu and your own save.", "Leave", "Cancel",
                    _actions.Leave, snap => snap.InSession && !snap.IsHost, danger: true));
            }
        }

        private void JoinListing(MenuSnapshot s, LobbyListing listing)
        {
            if (s.InOwnWorld)
            {
                Ask(new ConfirmSpec("Join " + LobbyListings.Title(listing) + "?",
                    "Your game is saved first. You play on the host's save; yours is untouched.", "Join", "Cancel",
                    () => _actions.Join(listing), snap => !snap.InSession && !snap.Busy));
                return;
            }
            _actions.Join(listing);
        }

        // ---- Helpers ------------------------------------------------------------------------------------

        public static string PrivacyName(LobbyVisibility visibility)
        {
            switch (visibility)
            {
                case LobbyVisibility.Private: return "Invite Only";
                case LobbyVisibility.Public: return "Public";
                default: return "Friends";
            }
        }

        public static string PrivacyHelp(LobbyVisibility visibility)
        {
            switch (visibility)
            {
                case LobbyVisibility.Private: return "Only players you invite can join.";
                case LobbyVisibility.Public: return "Anyone can find and join your game.";
                default: return "Your Steam friends can join from the Join Game list or their friends list.";
            }
        }

        private static MenuItem LookItem(MenuSnapshot s) =>
            new MenuItem(Look, MenuItemKind.Selector, "Character", value: LookName(s.Look));

        public static string LookName(PlayerLook look) => look == PlayerLook.Female ? "Female" : "Male";

        private static LobbyVisibility Supported(LobbyVisibility v) =>
            v == LobbyVisibility.Invisible ? LobbyVisibility.Private : Array.IndexOf(Privacy, v) >= 0 ? v : LobbyVisibility.FriendsOnly;

        private static LobbyVisibility Cycle(LobbyVisibility v, int step)
        {
            int i = Array.IndexOf(Privacy, Supported(v));
            i = (i + step + Privacy.Length) % Privacy.Length;
            return Privacy[i];
        }

        private static int ClampPlayers(int n) => n < MinPlayers ? MinPlayers : n > MaxPlayersShown ? MaxPlayersShown : n;

        private static int PlayerCountValue(MenuSnapshot s) => s.Players.Count;

        private static string PlayerCount(MenuSnapshot s)
        {
            int n = PlayerCountValue(s);
            return s.MaxPlayers > 0 ? n + "/" + s.MaxPlayers : n.ToString(CultureInfo.InvariantCulture);
        }

        private static MenuPlayer FindPlayer(MenuSnapshot s, ulong id)
        {
            foreach (var p in s.Players)
            {
                if (p.Id == id)
                    return p;
            }
            return null;
        }

        private static LobbyListing FindListing(MenuSnapshot s, string key)
        {
            foreach (var l in s.Lobbies)
            {
                if (l.Key == key)
                    return l;
            }
            return null;
        }

        private static string FirstOf(params string[] texts)
        {
            foreach (var t in texts)
            {
                if (!string.IsNullOrEmpty(t))
                    return t;
            }
            return string.Empty;
        }

        /// <summary>For tests and the view: the host screen's current choices.</summary>
        public LobbyVisibility HostVisibility => _hostVisibility;

        public int HostMaxPlayers => _hostMaxPlayers;

        private sealed class ConfirmSpec
        {
            public ConfirmSpec(string title, string message, string yes, string no, Action onYes, Func<MenuSnapshot, bool> stillValid,
                bool danger = false)
            {
                Danger = danger;
                Title = title;
                Message = message;
                Yes = yes;
                No = no;
                OnYes = onYes;
                StillValid = stillValid;
            }

            public string Title { get; }
            public string Message { get; }
            public string Yes { get; }
            public string No { get; }
            public Action OnYes { get; }
            public Func<MenuSnapshot, bool> StillValid { get; }
            public bool Danger { get; }
        }
    }
}
