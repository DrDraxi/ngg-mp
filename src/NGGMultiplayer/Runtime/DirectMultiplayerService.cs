using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using NGGMultiplayer.Core;
using NGGMultiplayer.Core.Diagnostics;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Session;
using NGGMultiplayer.Core.Sync;
using NGGMultiplayer.Game;
using NGGMultiplayer.Runtime.Lobbies;

namespace NGGMultiplayer.Runtime
{
    /// <summary>
    /// Multiplayer without Steam (<c>[Network] Transport = Direct</c>): <see cref="DirectTransport"/> over UDP, for two
    /// game copies on one PC (end-to-end tests) or a LAN. The host listens on <see cref="MultiplayerSettings.DirectPort"/>
    /// (on loopback only when <see cref="MultiplayerSettings.HostAddress"/> is a loopback address, else on every
    /// interface); a client joins <c>HostAddress:DirectPort</c>. No lobby: anyone who reaches the port may join, so this is
    /// for trusted networks only. Everything above the transport (session, save sync, entities, the game modules) is the
    /// same as on Steam. The adapter is not owned.
    /// </summary>
    public sealed class DirectMultiplayerService : IMultiplayerService, IMultiplayerLobbies
    {
        private const string Log = "[Direct] ";
        private const double ProbeTimeoutSeconds = 10;
        private const int MaxGameBuildChars = 64;

        private static readonly IReadOnlyList<PlayerInfo> NoPlayers = Array.Empty<PlayerInfo>();

        private readonly IGameAdapter _adapter;
        private readonly SessionConfig _sessionConfig = new SessionConfig();
        private readonly NetEntityRegistryOptions _entityOptions = new NetEntityRegistryOptions();
        private readonly NetSessionOptions _options;
        private readonly List<PeerId> _linkFailures = new List<PeerId>();
        private MultiplayerSettings _settings;

        private DirectTransport _transport;
        private PlayerEntityFactory _factory;
        private NetSession _net;
        private PlayerEntitySpawner _spawner;

        private string _joinTarget = string.Empty;
        private double _probeStartedAt = double.NaN;
        private double _now;
        private MultiplayerState _state = MultiplayerState.Unavailable;
        private string _unavailableReason = "Starting.";
        private string _status = string.Empty;
        private bool _backendTried;
        private bool _disposed;

        public DirectMultiplayerService(IGameAdapter adapter, MultiplayerSettings settings)
        {
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
            _options = new NetSessionOptions
            {
                Session = _sessionConfig,
                Entities = _entityOptions,
                ClaimValidator = (from, claim) => _adapter.ValidateClaim(from, claim)
            };
            ApplySettingsCore((settings ?? new MultiplayerSettings()).Clamped());
            _sessionConfig.GameBuild = ServiceText.Truncate(_adapter.GameBuild, MaxGameBuildChars);
        }

        public MultiplayerState State => _state;

        public bool IsAvailable => _net != null;

        public string UnavailableReason => _net != null ? string.Empty : _unavailableReason;

        public string StatusMessage => _status;

        public bool IsHost => _net != null && _net.IsHost && _net.IsActive;

        public ulong LobbyId => 0;

        public IReadOnlyList<PlayerInfo> Players => _net != null && _net.Session.IsActive ? _net.Session.Players : NoPlayers;

        public NetSession Net => _net;

        /// <summary>Where a client joins and the host listens, as "address:port".</summary>
        public string Endpoint => _settings.HostAddress + ":" + _settings.DirectPort.ToString(CultureInfo.InvariantCulture);

        /// <summary>The address and port this game's socket is bound to now.</summary>
        public string BoundEndpoint => _transport?.LocalEndPoint?.ToString() ?? string.Empty;

        public event Action<MultiplayerState> StateChanged;

        public bool Host()
        {
            if (!CanStart())
                return false;
            var listen = IsLoopback(_settings.HostAddress) ? IPAddress.Loopback : IPAddress.Any;
            try
            {
                _transport.Rebind(listen, _settings.DirectPort);
            }
            catch (SocketException e)
            {
                SetStatus($"Could not listen on port {_settings.DirectPort} ({e.SocketErrorCode}); is another game hosting there?");
                NetLog.Error($"{Log}Binding {listen}:{_settings.DirectPort} failed: {e.Message}");
                return false;
            }
            try
            {
                _net.StartHost();
                SetStatus($"Hosting on {_transport.LocalEndPoint}.");
                NetLog.Info($"{Log}Hosting on {_transport.LocalEndPoint} as {_transport.LocalId}.");
                return true;
            }
            catch (Exception e)
            {
                NetLog.Error($"{Log}Starting the host session failed: {e}");
                SetStatus("Could not start hosting: " + e.Message);
                return false;
            }
            finally
            {
                UpdateState();
            }
        }

        /// <summary>Joins the configured host (<see cref="Endpoint"/>); <paramref name="lobbyId"/> is not used.</summary>
        public bool Join(ulong lobbyId) => Join(_settings.HostAddress, _settings.DirectPort);

        /// <summary>Joins the host at <paramref name="address"/>:<paramref name="port"/>.</summary>
        public bool Join(string address, int port)
        {
            if (!CanStart())
                return false;
            if (!TryResolve(address, out var ip) || port < 1 || port > 65535)
            {
                SetStatus($"'{address}:{port}' is not a valid host address.");
                return false;
            }
            try
            {
                _transport.Rebind(IsLoopback(address) ? IPAddress.Loopback : IPAddress.Any, 0);
                var target = new IPEndPoint(ip, port);
                _transport.Probe(target);
                _joinTarget = target.ToString();
                _probeStartedAt = _now;
                SetStatus($"Looking for a host at {_joinTarget}...");
                NetLog.Info($"{Log}Joining {_joinTarget} from {_transport.LocalEndPoint}.");
                return true;
            }
            catch (Exception e)
            {
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
            StopProbe();
            try
            {
                _net.Leave();
            }
            finally
            {
                UpdateState();
            }
        }

        public bool OpenInviteDialog() => false;

        public void ApplySettings(MultiplayerSettings settings)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));
            ApplySettingsCore(settings.Clamped());
        }

        public void Tick(double now)
        {
            _now = now;
            if (_disposed)
                return;
            if (_net == null)
            {
                // On the first frame, like the Steam service: by then the test harness (if on) can wrap the transport.
                if (!_backendTried)
                {
                    _backendTried = true;
                    StartBackend();
                }
                return;
            }
            ReportLinkFailures();
            if (!double.IsNaN(_probeStartedAt))
            {
                var host = _transport.ProbedHost;
                if (host.IsValid)
                {
                    StopProbe();
                    try
                    {
                        _net.Connect(host);
                        SetStatus("Connecting to host...");
                    }
                    catch (Exception e)
                    {
                        NetLog.Error($"{Log}Connecting to the host failed: {e}");
                        SetStatus("Could not connect: " + e.Message);
                    }
                }
                else if (now - _probeStartedAt > ProbeTimeoutSeconds)
                {
                    StopProbe();
                    SetStatus($"No host answered at {_joinTarget}.");
                    NetLog.Info($"{Log}No host answered at {_joinTarget}.");
                }
            }
            _net.Update(now);
            UpdateState();
        }

        // ---- IMultiplayerLobbies: no lobbies; the browser lists the configured host --------------------

        public bool HasLobbies => false;

        public bool CanInvite => false;

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

        public bool SetVisibility(LobbyVisibility visibility) => false;

        public void RefreshLobbies()
        {
        }

        public bool IsRefreshing => false;

        public IReadOnlyList<LobbyListing> Lobbies => new[]
        {
            new LobbyListing { Source = LobbySource.Direct, Endpoint = Endpoint }
        };

        public bool Join(LobbyListing listing) => listing != null && Join(0);

        public bool Kick(ulong playerId) => IsHost && _net.Session.Kick(new PeerId(playerId));

        public void SetActivity(string activity)
        {
        }

        public Func<bool> ConfirmJoinRequests { get; set; }

        public PendingJoin PendingJoin => null;

        public void AnswerPendingJoin(bool accept)
        {
        }

        public event Action<string> Notice
        {
            add { }
            remove { }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            ShutdownBackend();
            StateChanged = null;
        }

        // ---- Backend lifecycle -----------------------------------------------------------------------

        private void StartBackend()
        {
            try
            {
                var id = DirectTransport.NewLocalId();
                if (string.IsNullOrEmpty(_settings.DisplayName))
                    _sessionConfig.DisplayName = "Player " + ((uint)id.Value % 10000).ToString("D4", CultureInfo.InvariantCulture);
                // A free loopback port until Host or Join binds the right one.
                _transport = new DirectTransport(id, IPAddress.Loopback, 0);
                _factory = new PlayerEntityFactory(_adapter, () => _net?.Session);
                // The test harness's bots share the session only when [Debug] EnableTestHarness is on; otherwise this is _transport.
                _net = new NetSession(Testing.TestHarness.WrapTransport(_transport), _adapter.SaveBackend, _factory, _options);
                _net.Session.PlayerJoined += OnPlayerJoined;
                _net.Session.PlayerLeft += OnPlayerLeft;
                _net.Ready += OnReady;
                _net.Ended += OnEnded;
                _spawner = new PlayerEntitySpawner(_net, GameEntityKeys.PerPlayer) { InitialState = InitialStateFor };
                NetLog.Warn($"{Log}Direct transport (no Steam) as {_sessionConfig.DisplayName} ({id}); hosts listen on / " +
                    $"clients join {Endpoint}. For localhost and trusted networks only.");
            }
            catch (Exception e)
            {
                NetLog.Error($"{Log}Starting the direct backend failed: {e}");
                ShutdownBackend();
                _unavailableReason = "Direct networking failed to start: " + e.Message;
            }
            UpdateState();
        }

        private void ShutdownBackend()
        {
            SafeRun(() => _spawner?.Dispose(), "spawner");
            SafeRun(() => _net?.Dispose(), "session");
            SafeRun(() => _transport?.Dispose(), "transport");
            SafeRun(() => _factory?.DestroyViews(), "views");
            _spawner = null;
            _net = null;
            _transport = null;
            _factory = null;
            _probeStartedAt = double.NaN;
        }

        private void StopProbe()
        {
            _probeStartedAt = double.NaN;
            _transport?.CancelProbe();
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
            StopProbe();
            SetStatus(ServiceText.Describe(info));
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
                _sessionConfig.DisplayName = ServiceText.Truncate(settings.DisplayName, SessionConfig.MaxDisplayNameChars);
            // Read when the next session starts.
            _entityOptions.SendRate = settings.SendRate;
            _entityOptions.InterpolationDelay = settings.InterpolationDelay;
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
            if (!double.IsNaN(_probeStartedAt))
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

        private static bool IsLoopback(string address) =>
            string.Equals(address, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(address, out var ip) && IPAddress.IsLoopback(ip));

        private static bool TryResolve(string address, out IPAddress ip)
        {
            ip = null;
            if (string.IsNullOrWhiteSpace(address))
                return false;
            if (string.Equals(address, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                ip = IPAddress.Loopback;
                return true;
            }
            if (IPAddress.TryParse(address, out ip))
                return ip.AddressFamily == AddressFamily.InterNetwork;
            try
            {
                foreach (var candidate in Dns.GetHostAddresses(address))
                {
                    if (candidate.AddressFamily == AddressFamily.InterNetwork)
                    {
                        ip = candidate;
                        return true;
                    }
                }
            }
            catch (Exception e)
            {
                NetLog.Info($"{Log}Resolving '{address}' failed: {e.Message}");
            }
            return false;
        }

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
    }
}
