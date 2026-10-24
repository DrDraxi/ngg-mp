using System.Collections.Generic;
using System.Linq;
using NGGMultiplayer.Runtime;
using NGGMultiplayer.Runtime.Lobbies;
using NGGMultiplayer.UI.Native;
using Xunit;

namespace NGGMultiplayer.Tests.UI
{
    public class MenuControllerTests
    {
        private readonly FakeActions _actions = new FakeActions();
        private readonly MenuController _menu;
        private readonly MenuSnapshot _s = new MenuSnapshot { HasLobbies = true, CanInvite = false, LocalProtocol = "2", MaxPlayers = 4 };

        public MenuControllerTests()
        {
            _menu = new MenuController(_actions);
        }

        private MenuScreen Screen() => _menu.Build(_s);

        private static string[] Ids(MenuScreen screen) => screen.Items.Select(i => i.Id).ToArray();

        private void InSession(bool host, params (ulong Id, string Name, bool Host, bool Local)[] players)
        {
            _s.State = MultiplayerState.InSession;
            _s.IsHost = host;
            _s.CanInvite = true;
            _s.Players = players.Select(p => new MenuPlayer { Id = p.Id, Name = p.Name, IsHost = p.Host, IsLocal = p.Local, PingMs = 30 }).ToList();
            _s.HostName = players.FirstOrDefault(p => p.Host).Name ?? string.Empty;
        }

        [Fact]
        public void Closed_BuildsNothing()
        {
            Assert.False(_menu.IsOpen);
            Assert.Null(Screen());
        }

        [Fact]
        public void Offline_HostAndJoin()
        {
            _menu.Open(MenuContext.MainMenu);
            var screen = Screen();
            Assert.Equal("Multiplayer", screen.Title);
            Assert.Equal(new[] { "host", "browse", "look", "back" }, Ids(screen));
            Assert.Equal("Host Game", screen.Items[0].Text);
            Assert.Equal("Join Game", screen.Items[1].Text);
        }

        [Fact]
        public void Character_ShowsTheLookAndTogglesIt()
        {
            _menu.Open(MenuContext.MainMenu, MenuController.HostScreen, _s);
            Assert.Equal("Character: Male", Screen().Find(MenuController.Look).Text);
            _menu.Change(MenuController.Look, 1, _s);
            _s.Look = PlayerLook.Female;
            Assert.Equal("Character: Female", Screen().Find(MenuController.Look).Text);
            _menu.Activate(MenuController.Look, _s);
            _menu.Change(MenuController.Look, -1, _s);
            Assert.Equal(new[] { "look Female", "look Male", "look Male" }, _actions.Calls);
            // Still on the host screen: changing the character is not a step anywhere.
            Assert.Equal(MenuController.HostScreen, Screen().Id);
        }

        [Fact]
        public void InOwnWorld_HostThisGame()
        {
            _s.InOwnWorld = true;
            _menu.Open(MenuContext.Pause);
            Assert.Equal("Host This Game", Screen().Items[0].Text);
        }

        [Fact]
        public void Unavailable_OnlyBack_WithReason()
        {
            _s.State = MultiplayerState.Unavailable;
            _s.UnavailableReason = "Steam is not running.";
            _menu.Open(MenuContext.MainMenu);
            var screen = Screen();
            Assert.Equal(new[] { "back" }, Ids(screen));
            Assert.Equal("Steam is not running.", screen.Subtitle);
        }

        [Fact]
        public void Host_SelectorsCycle_AndStartHosts()
        {
            _s.Visibility = LobbyVisibility.FriendsOnly;
            _menu.Open(MenuContext.MainMenu);
            _menu.Activate("host", _s);
            var screen = Screen();
            Assert.Equal(MenuController.HostScreen, screen.Id);
            Assert.Equal(new[] { "privacy", "maxplayers", "look", "play", "back" }, Ids(screen));
            Assert.Equal("Who Can Join: Friends", screen.Items[0].Text);

            _menu.Change("privacy", 1, _s);
            Assert.Equal("Who Can Join: Public", Screen().Items[0].Text);
            _menu.Change("privacy", 1, _s);
            Assert.Equal("Who Can Join: Invite Only", Screen().Items[0].Text);
            _menu.Activate("privacy", _s); // a click cycles forward too
            Assert.Equal(LobbyVisibility.FriendsOnly, _menu.HostVisibility);
            _menu.Change("privacy", -1, _s);
            Assert.Equal(LobbyVisibility.Private, _menu.HostVisibility);

            _menu.Change("maxplayers", -1, _s);
            _menu.Change("maxplayers", -1, _s);
            Assert.Equal(2, _menu.HostMaxPlayers);
            _menu.Change("maxplayers", -1, _s);
            Assert.Equal(MenuController.MaxPlayersShown, _menu.HostMaxPlayers);

            _menu.Activate("play", _s);
            Assert.Equal(new[] { "host Private 8 Play" }, _actions.Calls);
        }

        [Fact]
        public void Host_FromMainMenu_GoesIntoTheGame_FromPause_Stays()
        {
            _s.CanPlayNine = true;
            _menu.Open(MenuContext.MainMenu);
            _menu.Activate("host", _s);
            Assert.Equal(new[] { "privacy", "maxplayers", "look", "play", "playnine", "back" }, Ids(Screen()));
            _menu.Activate("playnine", _s);
            Assert.Equal("host FriendsOnly 4 PlayNine", _actions.Calls[0]);

            _menu.Close();
            _s.InOwnWorld = true;
            _menu.Open(MenuContext.Pause);
            _menu.Activate("host", _s);
            Assert.Equal(new[] { "privacy", "maxplayers", "look", "start", "back" }, Ids(Screen()));
            _menu.Activate("start", _s);
            Assert.Equal("host FriendsOnly 4 Stay", _actions.Calls[1]);
        }

        [Fact]
        public void Direct_NoPrivacy_JoinShowsEndpoint()
        {
            _s.HasLobbies = false;
            _s.DirectEndpoint = "127.0.0.1:27250";
            _menu.Open(MenuContext.MainMenu);
            Assert.Equal("Join 127.0.0.1:27250", Screen().Items[1].Text);
            _menu.Activate("host", _s);
            Assert.Equal(new[] { "maxplayers", "look", "play", "back" }, Ids(Screen()));
        }

        [Fact]
        public void HostingFlow_HostToBusyToSession()
        {
            _menu.Open(MenuContext.MainMenu);
            _menu.Activate("host", _s);
            _menu.Activate("play", _s);
            _s.State = MultiplayerState.CreatingLobby;
            Assert.Equal(MenuController.Busy, Screen().Id);
            Assert.Equal("Starting Your Game", Screen().Title);
            InSession(true, (1, "Michal", true, true));
            var screen = Screen();
            Assert.Equal(MenuController.Session, screen.Id);
            Assert.Equal(new[] { "invite", "players", "privacy", "look", "leave", "back" }, Ids(screen));
            Assert.Equal("Hosting", screen.Title);
            Assert.Equal("Players (1/4)", screen.Items[1].Text);
            Assert.Equal("End Session", screen.Items[4].Text);
            // Back from the session screen goes straight out: the host / busy steps are gone.
            Assert.False(_menu.GoBack(_s));
        }

        [Fact]
        public void Session_InviteAndLivePrivacy()
        {
            InSession(true, (1, "Michal", true, true));
            _s.Visibility = LobbyVisibility.FriendsOnly;
            _menu.Open(MenuContext.Pause);
            Assert.Equal(MenuController.Session, Screen().Id);
            _menu.Activate("invite", _s);
            _menu.Change("privacy", 1, _s);
            Assert.Equal(new[] { "invite", "visibility Public" }, _actions.Calls);
        }

        [Fact]
        public void Client_Session_NoPrivacy_LeaveConfirms()
        {
            InSession(false, (1, "Michal", true, false), (2, "Bob", false, true));
            _menu.Open(MenuContext.Pause);
            var screen = Screen();
            Assert.Equal("Michal's Game", screen.Title);
            Assert.Equal(new[] { "invite", "players", "look", "leave", "back" }, Ids(screen));
            _menu.Activate("leave", _s);
            screen = Screen();
            Assert.Equal(MenuController.Confirm, screen.Id);
            Assert.Equal("Leave Session?", screen.Title);
            _menu.Activate("no", _s);
            Assert.Equal(MenuController.Session, Screen().Id);
            Assert.Empty(_actions.Calls);
            _menu.Activate("leave", _s);
            _menu.Activate("yes", _s);
            Assert.Equal(new[] { "leave" }, _actions.Calls);
            _s.State = MultiplayerState.Offline;
            _s.Players = new List<MenuPlayer>();
            Assert.Equal(MenuController.Main, Screen().Id);
        }

        [Fact]
        public void Confirm_ClosesWhenSessionEndsMeanwhile()
        {
            InSession(true, (1, "Michal", true, true), (2, "Bob", false, false));
            _menu.Open(MenuContext.Pause);
            _menu.Activate("leave", _s);
            Assert.Equal("End Session?", Screen().Title);
            _s.State = MultiplayerState.Offline;
            Assert.Equal(MenuController.Main, Screen().Id);
            _menu.Activate("yes", _s); // stale click: nothing
            Assert.Empty(_actions.Calls);
        }

        [Fact]
        public void Players_HostKicksWithConfirm()
        {
            InSession(true, (1, "Michal", true, true), (2, "Bob", false, false));
            _menu.Open(MenuContext.Pause);
            _menu.Activate("players", _s);
            var screen = Screen();
            Assert.Equal("Players (2/4)", screen.Title);
            Assert.Equal("Michal", screen.Items[0].Label);
            Assert.Equal("Host · You", screen.Items[0].Detail);
            Assert.False(screen.Items[0].Enabled);
            Assert.Equal("Bob", screen.Items[1].Label);
            Assert.Equal("30 ms", screen.Items[1].Detail);
            Assert.True(screen.Items[1].Enabled);

            _menu.Activate("player:2", _s);
            Assert.Equal("Remove Bob from your game?", Screen().Subtitle);
            _menu.Activate("yes", _s);
            Assert.Equal(new[] { "kick 2" }, _actions.Calls);
            Assert.Equal(MenuController.Players, Screen().Id);
        }

        [Fact]
        public void Players_KickConfirmDropsWhenPlayerLeft()
        {
            InSession(true, (1, "Michal", true, true), (2, "Bob", false, false));
            _menu.Open(MenuContext.Pause);
            _menu.Activate("players", _s);
            _menu.Activate("player:2", _s);
            InSession(true, (1, "Michal", true, true));
            Assert.Equal(MenuController.Players, Screen().Id);
        }

        [Fact]
        public void Players_ClientCannotKick()
        {
            InSession(false, (1, "Michal", true, false), (2, "Bob", false, true));
            _menu.Open(MenuContext.Pause);
            _menu.Activate("players", _s);
            Assert.All(Screen().Items.Where(i => i.Kind == MenuItemKind.Row), i => Assert.False(i.Enabled));
            _menu.Activate("player:1", _s);
            Assert.Empty(_actions.Calls);
        }

        [Fact]
        public void Browse_RefreshesOnOpen_ListsAndJoins()
        {
            _menu.Open(MenuContext.MainMenu);
            _menu.Activate("browse", _s);
            Assert.Equal(new[] { "refresh" }, _actions.Calls);
            _s.Refreshing = true;
            var screen = Screen();
            Assert.Equal("Looking for games...", screen.Empty);
            Assert.False(screen.Find("refresh").Enabled);

            _s.Refreshing = false;
            _s.Lobbies = new List<LobbyListing>
            {
                new LobbyListing { LobbyId = 7, Source = LobbySource.Friend, HostName = "Michal", FriendName = "Michal", Players = 1, MaxPlayers = 4, Protocol = "2", Activity = "In the menu", DataKnown = true },
                new LobbyListing { LobbyId = 8, Source = LobbySource.Public, HostName = "Old", Players = 1, MaxPlayers = 4, Protocol = "1", ModVersion = "0.0.9", DataKnown = true }
            };
            screen = Screen();
            Assert.Equal("", screen.Empty);
            Assert.Equal(new[] { "lobby:7", "lobby:8", "refresh", "look", "back" }, Ids(screen));
            Assert.Equal("Michal's Game", screen.Items[0].Label);
            Assert.Equal("1/4 players · In the menu · Friend", screen.Items[0].Detail);
            Assert.False(screen.Items[1].Enabled);
            Assert.Equal("1/4 players · Needs mod version 0.0.9 · Public", screen.Items[1].Detail);

            _menu.Activate("lobby:8", _s);
            _menu.Activate("lobby:7", _s);
            Assert.Equal(new[] { "refresh", "join 7" }, _actions.Calls);
            _s.State = MultiplayerState.JoiningLobby;
            Assert.Equal(MenuController.Busy, Screen().Id);
            _menu.Activate("cancel", _s);
            Assert.Equal("leave", _actions.Calls.Last());
        }

        [Fact]
        public void Browse_FromOwnWorld_ConfirmsFirst()
        {
            _s.InOwnWorld = true;
            _s.Lobbies = new List<LobbyListing> { new LobbyListing { LobbyId = 7, Source = LobbySource.Invite, FriendName = "Bob" } };
            _menu.Open(MenuContext.Pause);
            _menu.Activate("browse", _s);
            _menu.Activate("lobby:7", _s);
            var screen = Screen();
            Assert.Equal("Join Bob's Game?", screen.Title);
            _menu.Activate("yes", _s);
            Assert.Equal(new[] { "refresh", "join 7" }, _actions.Calls);
        }

        [Fact]
        public void Syncing_ShowsProgress_ThenSession()
        {
            _menu.Open(MenuContext.MainMenu);
            _s.State = MultiplayerState.Syncing;
            _s.HostName = "Michal";
            _s.SyncProgress = 0.456f;
            var screen = Screen();
            Assert.Equal("Joining Michal's Game", screen.Title);
            Assert.Equal("Loading the host's save: 46%", screen.Subtitle);
            InSession(false, (1, "Michal", true, false), (2, "Bob", false, true));
            Assert.Equal(MenuController.Session, Screen().Id);
        }

        [Fact]
        public void JoinRequest_AcceptAndDecline()
        {
            _s.PendingJoin = new PendingJoin(9, "Alice");
            _menu.OpenJoinRequest(MenuContext.Pause);
            var screen = Screen();
            Assert.Equal("Join Alice's Game?", screen.Title);
            _menu.Activate("no", _s);
            Assert.Equal(new[] { "answer False" }, _actions.Calls);
            Assert.Equal(MenuController.Main, _menu.Current);

            _menu.OpenJoinRequest(MenuContext.Pause);
            _menu.Activate("yes", _s);
            Assert.Equal("answer True", _actions.Calls.Last());

            _menu.OpenJoinRequest(MenuContext.Pause);
            Assert.True(_menu.GoBack(_s)); // Back declines
            Assert.Equal("answer False", _actions.Calls.Last());
        }

        [Fact]
        public void JoinRequest_AnsweredElsewhere_Closes()
        {
            _s.PendingJoin = new PendingJoin(9, "Alice");
            _menu.OpenJoinRequest(MenuContext.MainMenu);
            _s.PendingJoin = null;
            Assert.Equal(MenuController.Main, Screen().Id);
        }

        [Fact]
        public void JoinRequest_InSessionWarns()
        {
            InSession(true, (1, "Michal", true, true));
            _s.PendingJoin = new PendingJoin(9, "Alice");
            _menu.OpenJoinRequest(MenuContext.Pause);
            Assert.Equal("This ends the session you are hosting.", Screen().Subtitle);
        }

        [Fact]
        public void Layout_ChangesOnlyWithStructure()
        {
            InSession(true, (1, "Michal", true, true));
            _menu.Open(MenuContext.Pause);
            string a = Screen().Layout;
            _s.Players = new List<MenuPlayer> { new MenuPlayer { Id = 1, Name = "Michal", IsHost = true, IsLocal = true }, new MenuPlayer { Id = 2, Name = "Bob" } };
            Assert.Equal(a, Screen().Layout); // count in a label only
            _s.CanInvite = false;
            Assert.NotEqual(a, Screen().Layout);
        }

        [Fact]
        public void DisabledItems_IgnoreClicks()
        {
            _menu.Open(MenuContext.MainMenu);
            _menu.Activate("browse", _s);
            _s.Refreshing = true;
            _actions.Calls.Clear();
            _menu.Activate("refresh", _s);
            _menu.Activate("nothing", _s);
            Assert.Empty(_actions.Calls);
        }

        [Fact]
        public void Browse_PagesLongLists()
        {
            _s.Lobbies = Enumerable.Range(1, 12).Select(i => new LobbyListing { LobbyId = (ulong)i, Source = LobbySource.Public, HostName = "H" + i }).ToList();
            _menu.Open(MenuContext.MainMenu, MenuController.Browse, _s);
            var screen = Screen();
            Assert.Equal(MenuController.PageSize, screen.Items.Count(i => i.Kind == MenuItemKind.Row));
            Assert.Equal("More (1/3)", screen.Find("page").Text);
            _menu.Activate("page", _s);
            _menu.Activate("page", _s);
            screen = Screen();
            Assert.Equal(2, screen.Items.Count(i => i.Kind == MenuItemKind.Row));
            Assert.Equal("lobby:11", screen.Items[0].Id);
            _menu.Activate("page", _s);
            Assert.Equal("lobby:1", Screen().Items[0].Id);
        }

        [Fact]
        public void PanelButtons_FollowTheSession()
        {
            Assert.Equal(new[] { "host", "browse" }, MenuController.PanelButtons(_s).Select(i => i.Id));
            _s.State = MultiplayerState.JoiningLobby;
            Assert.Equal(new[] { "busy", "cancel" }, MenuController.PanelButtons(_s).Select(i => i.Id));
            InSession(true, (1, "Michal", true, true), (2, "Bob", false, false));
            var host = MenuController.PanelButtons(_s);
            Assert.Equal(new[] { "invite", "session" }, host.Select(i => i.Id));
            Assert.Equal("Hosting (2/4)", host[1].Text);
            InSession(false, (1, "Michal", true, false), (2, "Bob", false, true));
            var client = MenuController.PanelButtons(_s);
            Assert.Equal("Michal's Game", client[0].Text);
            Assert.Equal("leave", client[1].Id);
        }

        [Fact]
        public void Panel_OpensScreens()
        {
            _menu.OpenFromPanel("host", MenuContext.MainMenu, _s);
            Assert.Equal(MenuController.HostScreen, _menu.Current);
            Assert.False(_menu.GoBack(_s));
            _menu.OpenFromPanel("browse", MenuContext.MainMenu, _s);
            Assert.Equal(MenuController.Browse, _menu.Current);
            Assert.Equal(new[] { "refresh" }, _actions.Calls);
            InSession(false, (1, "Michal", true, false), (2, "Bob", false, true));
            _menu.OpenFromPanel("leave", MenuContext.MainMenu, _s);
            Assert.Equal("Leave Session?", Screen().Title);
            _menu.Activate("no", _s);
            Assert.Equal(MenuController.Session, _menu.Current);
        }

        private sealed class FakeActions : IMenuActions
        {
            public List<string> Calls { get; } = new List<string>();

            public void Host(LobbyVisibility visibility, int maxPlayers, HostStart start) => Calls.Add("host " + visibility + " " + maxPlayers + " " + start);
            public void Leave() => Calls.Add("leave");
            public void Invite() => Calls.Add("invite");
            public void Join(LobbyListing listing) => Calls.Add("join " + listing.LobbyId);
            public void Kick(ulong playerId) => Calls.Add("kick " + playerId);
            public void SetVisibility(LobbyVisibility visibility) => Calls.Add("visibility " + visibility);
            public void Refresh() => Calls.Add("refresh");
            public void AnswerPendingJoin(bool accept) => Calls.Add("answer " + accept);
            public void SetLook(PlayerLook look) => Calls.Add("look " + look);
        }
    }
}
