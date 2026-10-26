using System;
using System.Collections.Generic;
using System.Globalization;
using NGGMultiplayer.Core.Diagnostics;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Runtime;
using NGGMultiplayer.Runtime.Lobbies;
using NGGMultiplayer.UI.Native;
using UnityEngine;

namespace NGGMultiplayer.Game.Menus
{
    /// <summary>
    /// Multiplayer in the game's own menus (docs/gamemap/06-menus.md): a multiplayer panel on the main menu, Invite
    /// Friends and Multiplayer in the pause menu, and the multiplayer screens (host with privacy, join a friend's or a
    /// public game, players, session, confirmations) drawn like the game's settings screens. The screen flow is
    /// <see cref="MenuController"/>; this module feeds it from the service and the game and draws its result.
    /// Steam invites and "Join Game" requests that would replace the player's world or session wait on a
    /// confirmation screen. While a scene's native menus are up, the Lifecycle module's IMGUI buttons stay hidden
    /// (<see cref="GameContext.NativeMenusShown"/>); when the game's pieces are missing they come back.
    /// </summary>
    public sealed class MenusModule : IGameModule, IMenuActions
    {
        public const string BlockEscapeFeature = "menus.escape";
        public const string BlockPauseFeature = "menus.pause";

        private const string Log = "[Menus] ";
        private const double RefreshSeconds = 0.1;
        private const double ActivitySeconds = 2;
        private const float NoticeSeconds = 6f;
        /// <summary>How long Play waits for the hosted session to come up before giving up.</summary>
        private const double StartWaitSeconds = 30;

        private static MenusModule _instance;

        private GameContext _ctx;
        private MenuController _controller;
        private MenuDialog _dialog;
        private MainMenuPanel _panel;
        private PauseButtons _pause;
        private PlayerListHud _playerList;
        private NoticeHud _notices;
        private MatchHud _matchHud;
        private double _nextMatchBuild;
        private object _listMatch;
        private uint _listMatchSequence;
        private bool _keyBroken;
        private double _nextListBuild;
        private double _nextNoticeBuild;
        private MenuSnapshot _listSnapshot;
        private string _listText;
        private Action<GameScene> _onSceneLoaded;
        private Action<string> _onNotice;
        private int _builtLoadId = -1;
        private bool _builtMenu;
        private bool _builtPause;
        private double _nextRefresh;
        private double _nextActivity;
        private int _closedFrame = -10;
        private int _backFrame = -10;
        private Behaviour _restoreGamepad;
        private MenuSnapshot _snapshot = new MenuSnapshot();
        private bool _pauseHidden;
        private IMultiplayerLobbies _lobbies;
        private GameLobbyInfo _info;
        private HostStart _pendingStart;
        private double _pendingStartUntil;

        public string Name => "Menus";

        public void Install(GameContext context)
        {
            _ctx = context ?? throw new ArgumentNullException(nameof(context));
            _instance = this;
            _controller = new MenuController(this);
            _dialog = new MenuDialog(OnActivate, OnChange);
            _panel = new MainMenuPanel(OnPanelClick);
            _pause = new PauseButtons(Invite, OpenFromPause);
            _playerList = new PlayerListHud();
            _notices = new NoticeHud();
            _matchHud = new MatchHud();
            _info = new GameLobbyInfo(context);
            _lobbies = context.Service as IMultiplayerLobbies;
            if (_lobbies != null)
            {
                // In the player's own world a Steam "Join Game" would replace it unasked: confirm first (only where the
                // confirmation screen can be drawn).
                _lobbies.ConfirmJoinRequests = () => _ctx != null && _ctx.Scene == GameScene.Main && !_ctx.InSession && _builtPause;
                _onNotice = text => _ctx?.Notify(text, NoticeSeconds);
                _lobbies.Notice += _onNotice;
            }
            _onSceneLoaded = OnSceneLoaded;
            context.SceneLoaded += _onSceneLoaded;

            var self = typeof(MenusModule);
            var none = Type.EmptyTypes;
            // While the multiplayer screens are open, Escape is their Back: the game's own Escape handling waits.
            context.Patcher.Prefix(BlockEscapeFeature, typeof(PlayerPressedEscape), "Update", none, self, nameof(SkipWhileOpen));
            context.Patcher.Prefix(BlockPauseFeature, typeof(PauseMenu), "Update", none, self, nameof(SkipWhileOpen));
        }

        public void Uninstall()
        {
            if (_ctx != null)
                _ctx.SceneLoaded -= _onSceneLoaded;
            if (_lobbies != null)
            {
                _lobbies.Notice -= _onNotice;
                _lobbies.ConfirmJoinRequests = null;
            }
            _dialog?.Destroy();
            _playerList?.Destroy();
            _notices?.Destroy();
            _matchHud?.Destroy();
            if (_ctx != null)
                _ctx.NativeNoticesShown = false;
            _ctx = null;
            if (_instance == this)
                _instance = null;
        }

        public void OnSessionReady(bool isHost) => _nextRefresh = 0;

        public void OnSessionEnded() => _nextRefresh = 0;

        // ---- Frame ----------------------------------------------------------------------------------------

        public void Tick(float deltaTime)
        {
            var ctx = _ctx;
            if (ctx == null)
                return;
            EnsureBuilt(ctx);
            RestoreGamepadMenu();
            double now = ctx.Now;
            if (now >= _nextRefresh)
            {
                _nextRefresh = now + RefreshSeconds;
                _snapshot = TakeSnapshot(ctx);
            }
            ReportActivity(ctx, now);
            StartWhenHosting(ctx, now);
            OfferPendingJoin(ctx);

            if (ctx.Scene == GameScene.Menu && _builtMenu)
                _panel.Tick(_snapshot, ctx.IsClientConnection);
            if (ctx.Scene == GameScene.Main && _builtPause)
                _pause.Show(_snapshot.CanInvite && _snapshot.InSession);
            TickPlayerList(ctx, now);
            TickNotices(ctx, now);
            TickMatch(ctx, now);

            if (_controller.IsOpen)
                DriveOpenScreens(ctx);
        }

        private void DriveOpenScreens(GameContext ctx)
        {
            // The pause menu closed under the screens (a scene load, the session's own handling): close them too.
            if (_controller.Context == MenuContext.Pause && (ctx.Scene != GameScene.Main || !_builtPause || !ctx.PauseMenuOpen))
            {
                CloseScreens(restoreGameMenu: false);
                return;
            }
            if (_controller.Context == MenuContext.MainMenu && (ctx.Scene != GameScene.Menu || !_builtMenu))
            {
                CloseScreens(restoreGameMenu: false);
                return;
            }
            // Escape is both Pause and the game's Cancel; on Cancel the screen's GamepadMenuInput already presses its
            // Back button (one step), so Pause steps back only when Cancel did not fire (the gamepad's Start).
            if (PausePressed() && !(_dialog.HandlesCancel && ActionPressed("Cancel")) && _backFrame != Time.frameCount)
                Back();
            if (!_controller.IsOpen)
                return;
            var screen = _controller.Build(_snapshot);
            if (screen == null)
            {
                CloseScreens(restoreGameMenu: true);
                return;
            }
            _dialog.Show(screen);
            if (_controller.Context == MenuContext.Pause)
            {
                // PauseMenu.Update is held while the screens are open; it kept the cursor free.
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
        }

        private void EnsureBuilt(GameContext ctx)
        {
            if (_builtLoadId == ctx.SceneLoadId)
                return;
            _builtLoadId = ctx.SceneLoadId;
            _builtMenu = false;
            _builtPause = false;
            _panel.Forget();
            _pause.Forget();
            _playerList.Forget();
            _notices.Forget();
            _matchHud.Forget();
            _nextMatchBuild = 0;
            ctx.NativeMatchHudShown = false;
            _nextListBuild = 0;
            _nextNoticeBuild = 0;
            ctx.NativePlayerListShown = false;
            ctx.NativeNoticesShown = false;
            _dialog.Destroy();
            _controller.Close();
            _pauseHidden = false;
            _restoreGamepad = null;
            try
            {
                if (ctx.Scene == GameScene.Menu)
                    _builtMenu = _panel.Build();
                else if (ctx.Scene == GameScene.Main)
                    _builtPause = _pause.Build();
            }
            catch (Exception e)
            {
                NetLog.Error($"{Log}Adding the multiplayer menus failed: {e}");
            }
            ctx.NativeMenusShown = ctx.Scene == GameScene.Menu ? _builtMenu : ctx.Scene == GameScene.Main && _builtPause;
        }

        /// <summary>The session's player list in the HUD's style (built once the world's HUD exists).</summary>
        private void TickPlayerList(GameContext ctx, double now)
        {
            if (ctx.Scene != GameScene.Main)
                return;
            if (!_playerList.Built)
            {
                if (!ctx.WorldReady || now < _nextListBuild)
                    return;
                _nextListBuild = now + 1.0;
                try
                {
                    ctx.NativePlayerListShown = _playerList.Build();
                }
                catch (Exception e)
                {
                    NetLog.Error($"{Log}Adding the player list failed: {e}");
                }
                if (!_playerList.Built)
                    return;
            }
            bool wanted = ctx.InSession && ctx.WorldReady && !ctx.IsLoadingWorld;
            var match = ctx.Match;
            if (wanted && match != null)
            {
                // A competitive round: the list is its leaderboard.
                if (!ReferenceEquals(_listMatch, match) || _listMatchSequence != match.Sequence || !ReferenceEquals(_listSnapshot, _snapshot))
                {
                    _listMatch = match;
                    _listMatchSequence = match.Sequence;
                    _listSnapshot = _snapshot;
                    _listText = Presence.MatchText.Leaderboard(match, ctx.LocalId, ctx.PlayerName);
                }
            }
            else if (wanted && (!ReferenceEquals(_listSnapshot, _snapshot) || _listMatch != null))
            {
                _listMatch = null;
                _listSnapshot = _snapshot;
                _listText = PlayerListText.Build(_snapshot.Players);
            }
            _playerList.Tick(wanted, wanted ? _listText : null);
        }

        /// <summary>The mod's notices in the HUD's style (built once the world's HUD exists; IMGUI until then).</summary>
        private void TickNotices(GameContext ctx, double now)
        {
            if (ctx.Scene != GameScene.Main)
                return;
            if (!_notices.Built)
            {
                if (!ctx.WorldReady || now < _nextNoticeBuild)
                    return;
                _nextNoticeBuild = now + 1.0;
                try
                {
                    ctx.NativeNoticesShown = _notices.Build();
                }
                catch (Exception e)
                {
                    NetLog.Error($"{Log}Adding the notices failed: {e}");
                }
                if (!_notices.Built)
                    return;
            }
            _notices.Tick(ctx.IsLoadingWorld ? null : ctx.NoticeText());
        }

        /// <summary>The competitive round's turn banner and group scorecard (built once the world's HUD exists).</summary>
        private void TickMatch(GameContext ctx, double now)
        {
            if (ctx.Scene != GameScene.Main)
                return;
            var match = ctx.Match;
            if (!_matchHud.Built)
            {
                if (match == null || !ctx.WorldReady || now < _nextMatchBuild)
                    return;
                _nextMatchBuild = now + 1.0;
                try
                {
                    ctx.NativeMatchHudShown = _matchHud.Build();
                }
                catch (Exception e)
                {
                    NetLog.Error($"{Log}Adding the match screens failed: {e}");
                }
                if (!_matchHud.Built)
                    return;
            }
            bool hidden = !ctx.InSession || !ctx.WorldReady || ctx.IsLoadingWorld || ctx.PauseMenuOpen || _controller.IsOpen;
            _matchHud.Tick(match, ctx.LocalId, ctx.PlayerName, now, ScorecardKeyHeld(ctx), hidden);
        }

        private bool ScorecardKeyHeld(GameContext ctx)
        {
            var key = ctx.ScorecardKey;
            if (_keyBroken || key == KeyCode.None)
                return false;
            try
            {
                // HarnessInput: a test-harness `key <name>`; always false outside the harness.
                return BepInEx.UnityInput.Current.GetKey(key) || Testing.HarnessInput.GetKey(key);
            }
            catch (Exception e)
            {
                _keyBroken = true;
                NetLog.Warn($"{Log}Reading the scorecard key failed ({e.Message}); the group scorecard only opens between holes.");
                return false;
            }
        }

        private void OnSceneLoaded(GameScene scene)
        {
            // Rebuilt on the next tick for the new scene (the old scene's objects are gone).
            _builtLoadId = -1;
        }

        // ---- Opening and closing --------------------------------------------------------------------------

        private void OnPanelClick(string id)
        {
            if (_ctx == null || _controller.IsOpen)
                return;
            _snapshot = TakeSnapshot(_ctx);
            if (id == "invite" && _snapshot.CanInvite)
            {
                Invite();
                return;
            }
            if (id == "cancel")
            {
                Leave();
                return;
            }
            if (!OpenScreens(MenuContext.MainMenu))
                return;
            _controller.OpenFromPanel(id, MenuContext.MainMenu, _snapshot);
            DriveOpenScreens(_ctx);
        }

        private void OpenFromPause()
        {
            if (_ctx == null || _controller.IsOpen)
                return;
            _snapshot = TakeSnapshot(_ctx);
            if (!OpenScreens(MenuContext.Pause))
                return;
            _controller.Open(MenuContext.Pause);
            DriveOpenScreens(_ctx);
        }

        /// <summary>Builds the dialog and steps the game's menu aside. False when it cannot be drawn.</summary>
        private bool OpenScreens(MenuContext context)
        {
            if (!_dialog.Build())
            {
                _ctx.ShowLobbyWindow(); // the game's pieces are missing: the plain window still works
                return false;
            }
            if (context == MenuContext.MainMenu)
            {
                SetEnabled(_panel.GamepadMenu, false);
            }
            else
            {
                SetEnabled(_pause.GamepadMenu, false);
                SetPauseVisible(false);
            }
            return true;
        }

        private void CloseScreens(bool restoreGameMenu)
        {
            var context = _controller.Context;
            _controller.Close();
            _dialog.Hide();
            _closedFrame = Time.frameCount;
            if (context != MenuContext.MainMenu)
                SetPauseVisible(true);
            // Enabled again on the next frame: in this one the Escape / B that closed the screens is still "pressed",
            // and the pause menu's GamepadMenuInput would take it as Cancel and press Resume.
            _restoreGamepad = restoreGameMenu ? (context == MenuContext.MainMenu ? _panel.GamepadMenu : _pause.GamepadMenu) : null;
        }

        private void RestoreGamepadMenu()
        {
            if (_restoreGamepad == null || Time.frameCount <= _closedFrame)
                return;
            if (!_controller.IsOpen)
                SetEnabled(_restoreGamepad, true);
            _restoreGamepad = null;
        }

        private void Back()
        {
            _backFrame = Time.frameCount;
            if (!_controller.GoBack(_snapshot))
                CloseScreens(restoreGameMenu: true);
        }

        /// <summary>Hides the pause screen's frame and controls while the multiplayer screens cover it (the PauseMenu stays active: still paused).</summary>
        private void SetPauseVisible(bool visible)
        {
            var menu = _pause.Menu;
            if (menu == null)
                return;
            var canvas = menu.GetComponent<Canvas>();
            if (canvas != null)
                canvas.enabled = visible;
            _pauseHidden = !visible;
        }

        private static void SetEnabled(Behaviour behaviour, bool enabled)
        {
            if (behaviour != null && behaviour.enabled != enabled)
                behaviour.enabled = enabled;
        }

        private static bool PausePressed() => ActionPressed("Pause");

        private static bool ActionPressed(string name)
        {
            try
            {
                var input = InputManager.instance;
                var actions = input != null && input.input != null ? input.input.actions : null;
                var action = actions?.FindAction(name, false);
                return action != null && action.WasPressedThisFrame();
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Harmony prefix of PlayerPressedEscape.Update and PauseMenu.Update: skipped while the screens are open
        /// and in the frame after they closed (the Escape that closed them must not open or close a game menu).</summary>
        private static bool SkipWhileOpen()
        {
            var self = _instance;
            if (self == null)
                return true;
            return !(self._controller.IsOpen || Time.frameCount - self._closedFrame <= 1);
        }

        /// <summary>
        /// Host from the main menu: once the session is up the screens close and the menu's Play / Just Play Nine is
        /// pressed, so hosting goes straight into the game. Hosting that fails or is cancelled stays in the menu.
        /// </summary>
        private void StartWhenHosting(GameContext ctx, double now)
        {
            if (_pendingStart == HostStart.Stay)
                return;
            var state = ctx.Service?.State ?? MultiplayerState.Unavailable;
            if (ctx.Scene != GameScene.Menu || !_builtMenu || now > _pendingStartUntil
                || state == MultiplayerState.Offline || state == MultiplayerState.Unavailable)
            {
                if (now > _pendingStartUntil)
                    NetLog.Warn($"{Log}The hosted session did not come up in time; staying in the menu.");
                _pendingStart = HostStart.Stay;
                return;
            }
            if (!ctx.IsHost)
                return;
            var start = _pendingStart;
            _pendingStart = HostStart.Stay;
            if (_controller.IsOpen && _controller.Context == MenuContext.MainMenu)
                CloseScreens(restoreGameMenu: true);
            if (!_panel.Press(start))
                NetLog.Warn($"{Log}The menu's {(start == HostStart.PlayNine ? "Just Play Nine" : "Play")} button could not be pressed.");
            else
                NetLog.Info($"{Log}Hosting: into the game ({start}).");
        }

        // ---- Steam join requests --------------------------------------------------------------------------

        private void OfferPendingJoin(GameContext ctx)
        {
            if (_lobbies?.PendingJoin == null || _controller.Current == MenuController.JoinRequest)
                return;
            if (!ctx.NativeMenusShown && !ctx.IsLoadingWorld)
            {
                // No screen to ask on (the game's pieces are missing): join as before the menus existed.
                NetLog.Info($"{Log}No multiplayer screen to confirm the join request on; joining.");
                _lobbies.AnswerPendingJoin(true);
                return;
            }
            if (ctx.Scene == GameScene.Menu && _builtMenu)
            {
                if (_controller.IsOpen || OpenScreens(MenuContext.MainMenu))
                {
                    _controller.OpenJoinRequest(MenuContext.MainMenu);
                    _nextRefresh = 0;
                }
                return;
            }
            if (ctx.Scene == GameScene.Main && _builtPause && ctx.WorldReady)
            {
                if (!ctx.PauseMenuOpen)
                {
                    // Bring up the game's pause menu first, as if the player pressed Escape.
                    var escape = Compat.FindAny<PlayerPressedEscape>(false);
                    if (escape == null || Time.timeScale == 0f)
                        return;
                    escape.OpenMenu();
                    return; // the screens open on a later tick, once the pause is tracked
                }
                if (_controller.IsOpen || OpenScreens(MenuContext.Pause))
                {
                    _controller.OpenJoinRequest(MenuContext.Pause);
                    _nextRefresh = 0;
                }
            }
        }

        private void ReportActivity(GameContext ctx, double now)
        {
            if (_lobbies == null || now < _nextActivity || !ctx.IsHost)
                return;
            _nextActivity = now + ActivitySeconds;
            _lobbies.SetActivity(Activity(ctx));
        }

        private static string Activity(GameContext ctx)
        {
            switch (ctx.Scene)
            {
                case GameScene.Main:
                    var manager = SaveManager.instance;
                    bool golf = manager != null && manager.m_gamemodeState != null && manager.m_gamemodeState.mode == GameMode.JustGolf;
                    return golf ? "Just Play Nine" : "Playing Story";
                case GameScene.Credits:
                    return "Watching the credits";
                default:
                    return "In the menu";
            }
        }

        // ---- The screens' input ---------------------------------------------------------------------------

        private void OnActivate(string id)
        {
            if (_ctx == null || !_controller.IsOpen)
                return;
            if (id == MenuController.Back)
            {
                Back();
                return;
            }
            _controller.Activate(id, _snapshot);
            _nextRefresh = 0;
            if (!_controller.IsOpen)
                CloseScreens(restoreGameMenu: true);
        }

        private void OnChange(string id, int step)
        {
            if (_ctx == null || !_controller.IsOpen)
                return;
            _controller.Change(id, step, _snapshot);
            _nextRefresh = 0;
        }

        // ---- IMenuActions ---------------------------------------------------------------------------------

        public void Host(LobbyVisibility visibility, int maxPlayers, HostStart start)
        {
            var service = _ctx?.Service;
            if (service == null)
                return;
            bool started = _lobbies != null ? _lobbies.Host(visibility, maxPlayers) : service.Host();
            if (started)
            {
                _ctx.SaveLobbyDefaults?.Invoke(visibility, maxPlayers);
                _pendingStart = _ctx.Scene == GameScene.Menu ? start : HostStart.Stay;
                _pendingStartUntil = _ctx.Now + StartWaitSeconds;
            }
            else if (!string.IsNullOrEmpty(service.StatusMessage))
                _ctx.Notify(service.StatusMessage, NoticeSeconds);
        }

        public void Leave() => _ctx?.Service?.Leave();

        public void Invite()
        {
            var service = _ctx?.Service;
            if (service == null)
                return;
            if (!service.OpenInviteDialog() && !string.IsNullOrEmpty(service.StatusMessage))
                _ctx.Notify(service.StatusMessage, NoticeSeconds);
        }

        public void Join(LobbyListing listing)
        {
            if (_lobbies == null || listing == null)
                return;
            if (!_lobbies.Join(listing) && !string.IsNullOrEmpty(_ctx?.Service?.StatusMessage))
                _ctx.Notify(_ctx.Service.StatusMessage, NoticeSeconds);
        }

        public void Kick(ulong playerId) => _lobbies?.Kick(playerId);

        public void SetVisibility(LobbyVisibility visibility)
        {
            if (_lobbies != null && _lobbies.SetVisibility(visibility))
                _ctx?.SaveLobbyDefaults?.Invoke(visibility, _lobbies.MaxPlayers);
        }

        public void Refresh() => _lobbies?.RefreshLobbies();

        public void AnswerPendingJoin(bool accept) => _lobbies?.AnswerPendingJoin(accept);

        public void SetLook(PlayerLook look)
        {
            if (_ctx == null)
                return;
            _ctx.Look = look;
            _snapshot.Look = _ctx.Look;
        }

        // ---- Snapshot -------------------------------------------------------------------------------------

        private MenuSnapshot TakeSnapshot(GameContext ctx)
        {
            var service = ctx.Service;
            var s = new MenuSnapshot
            {
                LocalProtocol = ProtocolInfo.Version.ToString(CultureInfo.InvariantCulture),
                Look = ctx.Look
            };
            if (service == null)
            {
                s.State = MultiplayerState.Unavailable;
                return s;
            }
            s.State = service.State;
            s.IsHost = ctx.IsHost;
            s.CanPlayNine = ctx.Scene == GameScene.Menu && _builtMenu && _panel.CanPlayNine;
            s.UnavailableReason = service.UnavailableReason ?? string.Empty;
            s.StatusMessage = service.StatusMessage ?? string.Empty;
            s.Warning = _info.Warning;
            s.SessionStatus = _info.SessionStatus;
            s.InOwnWorld = ctx.Scene == GameScene.Main && !ctx.InSession && s.State == MultiplayerState.Offline;
            var players = new List<MenuPlayer>();
            foreach (var p in service.Players)
            {
                players.Add(new MenuPlayer
                {
                    Id = p.Id.Value,
                    Name = p.DisplayName,
                    IsHost = p.IsHost,
                    IsLocal = p.IsLocal,
                    PingMs = p.PingMs,
                    Where = _info.DescribePlayer(p)
                });
                if (p.IsHost)
                    s.HostName = p.DisplayName;
            }
            s.Players = players;
            var saveClient = service.Net?.SaveClient;
            s.SyncProgress = saveClient != null ? saveClient.SnapshotProgress : 0f;
            if (service is DirectMultiplayerService direct)
                s.DirectEndpoint = direct.Endpoint;
            if (_lobbies != null)
            {
                s.HasLobbies = _lobbies.HasLobbies;
                s.CanInvite = _lobbies.CanInvite;
                s.Visibility = _lobbies.Visibility;
                // This player's own lobby setting: in someone else's session the host's limit is not known here.
                s.MaxPlayers = ctx.IsClientConnection ? 0 : _lobbies.MaxPlayers;
                s.Lobbies = _lobbies.Lobbies;
                s.Refreshing = _lobbies.IsRefreshing;
                s.PendingJoin = _lobbies.PendingJoin;
            }
            return s;
        }
    }
}
