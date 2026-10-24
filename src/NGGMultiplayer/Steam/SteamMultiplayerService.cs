using System;
using System.Collections.Generic;
using System.Globalization;
using NGGMultiplayer.Core;
using NGGMultiplayer.Core.Diagnostics;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Session;
using NGGMultiplayer.Core.Sync;
using NGGMultiplayer.Game;
using NGGMultiplayer.Runtime;
using NGGMultiplayer.Runtime.Lobbies;
using Steamworks;

namespace NGGMultiplayer.Steam
{
    /// <summary>
    /// Multiplayer over Steam: a Steam lobby for discovery and invites, <see cref="SteamTransport"/> for packets and
    /// <see cref="NetSession"/> for everything else. The lobby owner hosts. Every player gets a player and a ball
    /// entity (<see cref="PlayerEntitySpawner"/>) fed from <see cref="IGameAdapter"/>; other players' entities are
    /// shown by <see cref="RemotePlayerView"/> and <see cref="RemoteBallView"/>. The backend starts once the game has
    /// initialized Steam. The adapter is not owned.
    /// </summary>
    public sealed class SteamMultiplayerService : IMultiplayerService, IMultiplayerLobbies
    {
        private const string Log = "[Multiplayer] ";
        private const double ProbeIntervalSeconds = 2;
        private const int MaxGameBuildChars = 64;

        private static readonly IReadOnlyList<PlayerInfo> NoPlayers = Array.Empty<PlayerInfo>();

        private readonly IGameAdapter _adapter;
        private readonly SessionConfig _sessionConfig = new SessionConfig();
        private readonly NetEntityRegistryOptions _entityOptions = new NetEntityRegistryOptions();
        private readonly NetSessionOptions _options;
        private readonly List<PeerId> _linkFailures = new List<PeerId>();
        private MultiplayerSettings _settings;

        private SteamTransport _transport;
        private SteamLobby _lobby;
        private SteamLobbyBrowser _browser;
        private PendingJoin _joinRequest;
        private string _activity = string.Empty;
        private double _now;
        private PlayerEntityFactory _factory;
        private NetSession _net;
        private PlayerEntitySpawner _spawner;

        private LobbyOperation _operation;
        private CSteamID _pendingJoin = CSteamID.Nil;
        private MultiplayerState _state = MultiplayerState.Unavailable;
        private string _unavailableReason = "Waiting for Steam.";
        private string _status = string.Empty;
        private double _nextProbeAt;
        private bool _permanentlyUnavailable;
        private bool _callbackPumpFailed;
        private bool _disposed;

        public SteamMultiplayerService(IGameAdapter adapter, MultiplayerSettings settings)
        {
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
            _options = new NetSessionOptions
            {
                Session = _sessionConfig,
                Entities = _entityOptions,
                ClaimValidator = (from, claim) => _adapter.ValidateClaim(from, claim)
            };
            ApplySettingsCore((settings ?? new MultiplayerSettings()).Clamped());
            _sessionConfig.GameBuild = Truncate(_adapter.GameBuild, MaxGameBuildChars);
        }

        public MultiplayerState State => _state;

        public bool IsAvailable => _net != null;

        public string UnavailableReason => _net != null ? string.Empty : _unavailableReason;

        public string StatusMessage => _status;

        public bool IsHost => _net != null && _net.IsHost && _net.IsActive;

        public ulong LobbyId => _lobby != null && _lobby.InLobby ? _lobby.Current.m_SteamID : 0;

        public IReadOnlyList<PlayerInfo> Players => _net != null && _net.Session.IsActive ? _net.Session.Players : NoPlayers;

        public NetSession Net => _net;

        public event Action<MultiplayerState> StateChanged;

        public bool Host()
        {
            if (!CanStart())
                return false;
            try
            {
                _operation = LobbyOperation.Creating;
                _lobby.Create(_settings.LobbyType, _settings.MaxPlayers);
                SetStatus("Creating lobby...");
                return true;
            }
            catch (Exception e)
            {
                _operation = LobbyOperation.None;
                SetStatus("Could not create a lobby: " + e.Message);
                NetLog.Error($"{Log}Host failed: {e}");
                return false;
            }
            finally
            {
                UpdateState();
            }
        }

        public bool Join(ulong lobbyId)
        {
            var lobby = new CSteamID(lobbyId);
            if (!lobby.IsValid() || !lobby.IsLobby())
            {
                SetStatus("That is not a valid lobby id.");
                return false;
            }
            if (!CanStart())
                return false;
            try
            {
                _operation = LobbyOperation.Joining;
                _lobby.Join(lobby);
                SetStatus("Joining lobby...");
                return true;
            }
            catch (Exception e)
            {
                _operation = LobbyOperation.None;
                SetStatus("Could not join: " + e.Message);
                NetLog.Error($"{Log}Join failed: {e}");
                return false;
            }
            finally
            {
                UpdateState();
            }
        }

        public void Leave()
        {
            if (_net == null)
                return;
            _pendingJoin = CSteamID.Nil;
            LeaveCore();
        }

        public bool OpenInviteDialog()
        {
            if (_lobby == null || !_net.IsActive)
                return false;
            try
            {
                if (!SteamUtils.IsOverlayEnabled())
                {
                    SetStatus("The Steam overlay is off for this game. Invite friends from your Steam friends list instead.");
                    return false;
                }
                return _lobby.OpenInviteDialog();
            }
            catch (Exception e)
            {
                NetLog.Warn($"{Log}Opening the invite dialog failed: {e.Message}");
                return false;
            }
        }

        public void ApplySettings(MultiplayerSettings settings)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));
            ApplySettingsCore(settings.Clamped());
            if (IsHost && _lobby != null && _lobby.InLobby)
            {
                try
                {
                    _lobby.Configure(_settings.LobbyType, _settings.MaxPlayers);
                    _lobby.SetData(SteamLobby.VisibilityKey, _settings.LobbyType.ToString());
                    _lobby.PublishRichPresence(Joinable(_settings.LobbyType));
                }
                catch (Exception e)
                {
                    NetLog.Warn($"{Log}Updating the lobby failed: {e.Message}");
                }
            }
        }

        public void Tick(double now)
        {
            if (_disposed)
                return;
            if (_net == null)
            {
                if (!_permanentlyUnavailable && now >= _nextProbeAt)
                {
                    _nextProbeAt = now + ProbeIntervalSeconds;
                    TryStartBackend();
                }
                UpdateState();
                return;
            }

            _now = now;
            if (_settings.PumpSteamCallbacks)
                PumpCallbacks();
            _browser?.Tick(now);
            ReportLinkFailures();

            if (_pendingJoin.IsValid() && _state == MultiplayerState.Offline && !_lobby.IsBusy)
            {
                var lobby = _pendingJoin;
                _pendingJoin = CSteamID.Nil;
                Join(lobby.m_SteamID);
            }

            _net.Update(now);
            UpdateState();
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            ShutdownBackend();
            StateChanged = null;
        }

        // ---- IMultiplayerLobbies ---------------------------------------------------------------------

        public bool HasLobbies => true;

        public bool CanInvite => _lobby != null && _lobby.InLobby && _net != null && _net.IsActive;

        public LobbyVisibility Visibility => _settings.LobbyType;

        public int MaxPlayers => _settings.MaxPlayers;

        public bool Host(LobbyVisibility visibility, int maxPlayers)
        {
            var settings = _settings.Clamped();
            settings.LobbyType = visibility;
            settings.MaxPlayers = maxPlayers;
            ApplySettingsCore(settings.Clamped());
            return Host();
        }

        public bool SetVisibility(LobbyVisibility visibility)
        {
            var settings = _settings.Clamped();
            settings.LobbyType = visibility;
            ApplySettings(settings);
            return IsHost;
        }

        public void RefreshLobbies()
        {
            if (_browser == null)
                return;
            _browser.OwnLobby = LobbyId;
            _browser.Refresh(_now);
        }

        public bool IsRefreshing => _browser != null && _browser.IsRefreshing;

        public IReadOnlyList<LobbyListing> Lobbies => _browser != null ? _browser.Lobbies : (IReadOnlyList<LobbyListing>)Array.Empty<LobbyListing>();

        public bool Join(LobbyListing listing)
        {
            if (listing == null || listing.LobbyId == 0)
                return false;
            _browser?.ForgetInvite(listing.LobbyId);
            return Join(listing.LobbyId);
        }

        public bool Kick(ulong playerId)
        {
            if (!IsHost)
                return false;
            try
            {
                return _net.Session.Kick(new PeerId(playerId));
            }
            catch (Exception e)
            {
                NetLog.Warn($"{Log}Kicking {playerId} failed: {e.Message}");
                return false;
            }
        }

        public void SetActivity(string activity)
        {
            activity = activity ?? string.Empty;
            if (activity == _activity)
                return;
            _activity = activity;
            if (IsHost && _lobby != null && _lobby.InLobby)
            {
                try
                {
                    _lobby.SetData(SteamLobby.ActivityKey, activity);
                }
                catch (Exception e)
                {
                    NetLog.Debug($"{Log}Setting the lobby activity failed: {e.Message}");
                }
            }
        }

        public Func<bool> ConfirmJoinRequests { get; set; }

        public PendingJoin PendingJoin => _joinRequest;

        public void AnswerPendingJoin(bool accept)
        {
            var request = _joinRequest;
            _joinRequest = null;
            if (request == null || !accept || _lobby == null)
                return;
            NetLog.Info($"{Log}The player accepted the join request for lobby {request.LobbyId}.");
            JoinRequested(new CSteamID(request.LobbyId));
        }

        public event Action<string> Notice;

        private void RaiseNotice(string text)
        {
            var handler = Notice;
            if (handler == null)
                return;
            try
            {
                handler(text);
            }
            catch (Exception e)
            {
                NetLog.Error($"{Log}Notice handler threw: {e}");
            }
        }

        private static bool Joinable(LobbyVisibility visibility) =>
            visibility != LobbyVisibility.Private && visibility != LobbyVisibility.Invisible;

        // ---- Backend lifecycle -----------------------------------------------------------------------

        private void TryStartBackend()
        {
            if (!SteamBootstrap.TryProbe(out var localId, out var reason))
            {
                if (_unavailableReason != reason)
                    NetLog.Info($"{Log}{reason}");
                _unavailableReason = reason;
                _permanentlyUnavailable = SteamBootstrap.IsPermanent(reason);
                return;
            }

            try
            {
                if (string.IsNullOrEmpty(_settings.DisplayName))
                    _sessionConfig.DisplayName = Truncate(SteamFriends.GetPersonaName(), SessionConfig.MaxDisplayNameChars);

                _transport = new SteamTransport(localId) { AcceptFilter = AcceptsSessionFrom };
                _lobby = new SteamLobby();
                _lobby.Created += OnLobbyCreated;
                _lobby.Entered += OnLobbyEntered;
                _lobby.Failed += OnLobbyFailed;
                _lobby.JoinRequested += OnJoinRequested;
                _lobby.MemberLeft += OnLobbyMemberLeft;
                _browser = new SteamLobbyBrowser();
                _browser.Invited += RaiseNotice;

                _factory = new PlayerEntityFactory(_adapter, () => _net?.Session);
                // The test harness's bots share the session only when [Debug] EnableTestHarness is on; otherwise this is _transport.
                _net = new NetSession(Testing.TestHarness.WrapTransport(_transport), _adapter.SaveBackend, _factory, _options);
                _net.Session.AdmissionFilter = peer => _lobby != null && _lobby.IsMember(peer);
                _net.Session.PlayerJoined += OnPlayerJoined;
                _net.Session.PlayerLeft += OnPlayerLeft;
                _net.Ready += OnReady;
                _net.Ended += OnEnded;

                _spawner = new PlayerEntitySpawner(_net, GameEntityKeys.PerPlayer) { InitialState = InitialStateFor };

                NetLog.Info($"{Log}Steam ready as {_sessionConfig.DisplayName} ({localId.m_SteamID}).");
                if (SteamLobby.TryGetLaunchLobby(out var launchLobby))
                {
                    NetLog.Info($"{Log}Launched to join lobby {launchLobby.m_SteamID}.");
                    _pendingJoin = launchLobby;
                }
            }
            catch (Exception e)
            {
                NetLog.Error($"{Log}Starting the Steam backend failed: {e}");
                ShutdownBackend();
                _unavailableReason = "Steam networking failed to start: " + e.Message;
                _permanentlyUnavailable = true;
            }
        }

        private void ShutdownBackend()
        {
            // Leaving first lets clients restore their own save and the host say goodbye.
            SafeRun(() => _spawner?.Dispose(), "spawner");
            SafeRun(() => _net?.Dispose(), "session");
            SafeRun(() => _browser?.Dispose(), "lobby browser");
            SafeRun(() => _lobby?.Dispose(), "lobby");
            SafeRun(() => _transport?.Dispose(), "transport");
            SafeRun(() => _factory?.DestroyViews(), "views");
            _spawner = null;
            _net = null;
            _lobby = null;
            _browser = null;
            _joinRequest = null;
            _transport = null;
            _factory = null;
            _operation = LobbyOperation.None;
        }

        private void LeaveCore()
        {
            _operation = LobbyOperation.None;
            try
            {
                _net.Leave();
            }
            finally
            {
                _lobby.Leave();
                UpdateState();
            }
        }

        // ---- Lobby events ----------------------------------------------------------------------------

        private void OnLobbyCreated(CSteamID lobby)
        {
            _operation = LobbyOperation.None;
            try
            {
                _lobby.SetData(SteamLobby.ModVersionKey, ProtocolInfo.ModVersion);
                _lobby.SetData(SteamLobby.ProtocolKey, ProtocolInfo.Version.ToString(CultureInfo.InvariantCulture));
                _lobby.SetData(SteamLobby.HostKey, _transport.LocalId.Value.ToString(CultureInfo.InvariantCulture));
                _lobby.SetData(SteamLobby.NameKey, _sessionConfig.DisplayName);
                _lobby.SetData(SteamLobby.ActivityKey, _activity);
                _lobby.SetData(SteamLobby.VisibilityKey, _settings.LobbyType.ToString());
                _net.StartHost();
                _lobby.PublishRichPresence(Joinable(_settings.LobbyType));
                SetStatus("Hosting. Invite friends to play.");
            }
            catch (Exception e)
            {
                NetLog.Error($"{Log}Starting the host session failed: {e}");
                SetStatus("Could not start hosting: " + e.Message);
                LeaveCore();
            }
        }

        private void OnLobbyEntered(CSteamID lobby)
        {
            _operation = LobbyOperation.None;
            string protocol = _lobby.GetData(SteamLobby.ProtocolKey);
            if (protocol.Length == 0)
            {
                SetStatus("That lobby is not an NGG Multiplayer session.");
                LeaveCore();
                return;
            }
            if (protocol != ProtocolInfo.Version.ToString(CultureInfo.InvariantCulture))
            {
                string hostVersion = _lobby.GetData(SteamLobby.ModVersionKey);
                SetStatus($"The host runs NGG Multiplayer {(hostVersion.Length > 0 ? hostVersion : "(unknown)")}; " +
                    $"you have {ProtocolInfo.ModVersion}. Both players need the same version.");
                LeaveCore();
                return;
            }

            var owner = _lobby.Owner;
            if (!owner.IsValid() || owner.m_SteamID == _transport.LocalId.Value)
            {
                SetStatus("The lobby has no host.");
                LeaveCore();
                return;
            }
            // When the host drops off Steam, the lobby passes to another member, who is not running a host session.
            if (_lobby.GetData(SteamLobby.HostKey) != owner.m_SteamID.ToString(CultureInfo.InvariantCulture))
            {
                SetStatus("The host has left this lobby; the session is over.");
                LeaveCore();
                return;
            }
            try
            {
                _net.Connect(new PeerId(owner.m_SteamID));
                _browser?.ForgetInvite(lobby.m_SteamID);
                Enum.TryParse(_lobby.GetData(SteamLobby.VisibilityKey), out LobbyVisibility hostVisibility);
                _lobby.PublishRichPresence(Joinable(hostVisibility));
                SetStatus("Connecting to host...");
            }
            catch (Exception e)
            {
                NetLog.Error($"{Log}Connecting to the host failed: {e}");
                SetStatus("Could not connect: " + e.Message);
                LeaveCore();
            }
        }

        private void OnLobbyFailed(string message)
        {
            _operation = LobbyOperation.None;
            SetStatus(message);
            UpdateState();
        }

        private void OnJoinRequested(CSteamID lobby)
        {
            if (LobbyId == lobby.m_SteamID)
                return;
            if (NeedsConfirmation())
            {
                string name = _browser?.NameOf(lobby.m_SteamID) ?? string.Empty;
                if (name.Length == 0)
                    name = SafeLobbyName(lobby);
                _joinRequest = new PendingJoin(lobby.m_SteamID, name);
                NetLog.Info($"{Log}Join request for lobby {lobby.m_SteamID} waits for the player.");
                return;
            }
            JoinRequested(lobby);
        }

        private void JoinRequested(CSteamID lobby)
        {
            if (LobbyId == lobby.m_SteamID)
                return;
            if (_state == MultiplayerState.Offline && !_lobby.IsBusy)
            {
                Join(lobby.m_SteamID);
                return;
            }
            // Busy or in another session: leave first, join on a later tick.
            NetLog.Info($"{Log}Leaving the current session to join lobby {lobby.m_SteamID}.");
            LeaveCore();
            _pendingJoin = lobby;
            SetStatus("Switching to the invited lobby...");
        }

        private bool NeedsConfirmation()
        {
            if (_state != MultiplayerState.Offline)
                return true;
            try
            {
                return ConfirmJoinRequests != null && ConfirmJoinRequests();
            }
            catch (Exception e)
            {
                NetLog.Warn($"{Log}ConfirmJoinRequests threw: {e.Message}");
                return false;
            }
        }

        private static string SafeLobbyName(CSteamID lobby)
        {
            try
            {
                SteamMatchmaking.RequestLobbyData(lobby);
                return SteamMatchmaking.GetLobbyData(lobby, SteamLobby.NameKey) ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private void OnLobbyMemberLeft(CSteamID member)
        {
            if (_net == null)
                return;
            var peer = new PeerId(member.m_SteamID);
            var session = _net.Session;
            if (session.IsHost)
            {
                session.ReportLinkLost(peer);
                return;
            }
            if (!session.IsClient || peer != session.HostId)
                return;
            NetLog.Info($"{Log}The host left the lobby.");
            if (!session.ReportLinkLost(peer))
                LeaveCore();
            SetStatus("The host left the session.");
        }

        private void ReportLinkFailures()
        {
            _linkFailures.Clear();
            _transport.DrainLinkFailures(_linkFailures);
            for (int i = 0; i < _linkFailures.Count && _net != null; i++)
            {
                try
                {
                    _net.Session.ReportLinkLost(_linkFailures[i]);
                }
                catch (Exception e)
                {
                    NetLog.Error($"{Log}Handling the lost link to {_linkFailures[i]} failed: {e}");
                }
            }
            _linkFailures.Clear();
        }

        private bool AcceptsSessionFrom(PeerId peer) =>
            _lobby != null && (_lobby.IsMember(peer) || (_net != null && _net.Session.IsPeerConnected(peer)));

        // ---- Session events --------------------------------------------------------------------------

        private void OnReady()
        {
            if (!_net.IsHost)
                SetStatus("Connected.");
            try
            {
                _adapter.OnSessionReady(_net.IsHost);
            }
            catch (Exception e)
            {
                NetLog.Error($"{Log}Game adapter OnSessionReady threw: {e}");
            }
        }

        private void OnEnded(SessionEndInfo info)
        {
            _operation = LobbyOperation.None;
            _lobby?.Leave();
            SetStatus(Describe(info));
            NetLog.Info($"{Log}Session ended: {info}");
            try
            {
                _adapter.OnSessionEnded();
            }
            catch (Exception e)
            {
                NetLog.Error($"{Log}Game adapter OnSessionEnded threw: {e}");
            }
        }

        private void OnPlayerJoined(PlayerInfo player)
        {
            if (!player.IsLocal)
                SetStatus(player.DisplayName + " joined.");
        }

        private void OnPlayerLeft(PlayerInfo player, PlayerLeaveReason reason)
        {
            if (player.IsLocal)
                return;
            SetStatus(reason == PlayerLeaveReason.TimedOut
                ? player.DisplayName + " lost connection."
                : player.DisplayName + " left.");
        }

        private TransformState InitialStateFor(PeerId player, string prefabKey)
        {
            // Only the host's own pose is known when spawning; clients start hidden until their first state.
            if (player != _net.LocalId)
                return TransformState.Identity;
            GamePose pose;
            bool has = prefabKey == GameEntityKeys.Player
                ? _adapter.TryGetLocalPlayerPose(out pose)
                : _adapter.TryGetLocalBallPose(out pose);
            return has ? PoseConversion.ToState(pose) : TransformState.Identity;
        }

        // ---- Helpers ---------------------------------------------------------------------------------

        private bool CanStart()
        {
            if (_disposed || _net == null)
                return false;
            if (_state != MultiplayerState.Offline)
            {
                SetStatus("Leave the current session first.");
                return false;
            }
            if (_lobby.IsBusy)
            {
                SetStatus("Still leaving the previous lobby; try again in a moment.");
                return false;
            }
            if (_net.LocalSaveRestorePending)
            {
                SetStatus("Your own save could not be restored after the last session. Restart the game before playing online again.");
                return false;
            }
            return true;
        }

        private void ApplySettingsCore(MultiplayerSettings settings)
        {
            _settings = settings;
            _sessionConfig.MaxPlayers = settings.MaxPlayers;
            _sessionConfig.TimeoutSeconds = settings.TimeoutSeconds;
            _sessionConfig.ConnectTimeoutSeconds = Math.Max(10, settings.TimeoutSeconds);
            if (!string.IsNullOrEmpty(settings.DisplayName))
                _sessionConfig.DisplayName = Truncate(settings.DisplayName, SessionConfig.MaxDisplayNameChars);
            // Read when the next session starts.
            _entityOptions.SendRate = settings.SendRate;
            _entityOptions.InterpolationDelay = settings.InterpolationDelay;
        }

        private void PumpCallbacks()
        {
            if (_callbackPumpFailed)
                return;
            try
            {
                SteamAPI.RunCallbacks();
            }
            catch (Exception e)
            {
                _callbackPumpFailed = true;
                NetLog.Error($"{Log}SteamAPI.RunCallbacks failed; no longer pumping callbacks: {e.Message}");
            }
        }

        private void UpdateState()
        {
            var state = ComputeState();
            if (state == _state)
                return;
            _state = state;
            NetLog.Debug($"{Log}State: {state}");
            var handler = StateChanged;
            if (handler == null)
                return;
            try
            {
                handler(state);
            }
            catch (Exception e)
            {
                NetLog.Error($"{Log}StateChanged handler threw: {e}");
            }
        }

        private MultiplayerState ComputeState()
        {
            if (_net == null)
                return MultiplayerState.Unavailable;
            if (_operation == LobbyOperation.Creating)
                return MultiplayerState.CreatingLobby;
            if (_operation == LobbyOperation.Joining)
                return MultiplayerState.JoiningLobby;
            switch (_net.Session.State)
            {
                case SessionState.Connecting:
                    return MultiplayerState.Connecting;
                case SessionState.Active:
                    return _net.IsReady ? MultiplayerState.InSession : MultiplayerState.Syncing;
                default:
                    return MultiplayerState.Offline;
            }
        }

        private void SetStatus(string message) => _status = message ?? string.Empty;

        private static string Describe(SessionEndInfo info) => ServiceText.Describe(info);

        private static string Truncate(string value, int maxChars) => ServiceText.Truncate(value, maxChars);

        private static void SafeRun(Action action, string what)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                NetLog.Error($"{Log}Shutting down the {what} failed: {e}");
            }
        }

        private enum LobbyOperation
        {
            None,
            Creating,
            Joining
        }
    }
}
