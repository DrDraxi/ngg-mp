using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using NGGMultiplayer.Core;
using NGGMultiplayer.Core.Diagnostics;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Session;
using NGGMultiplayer.Core.Sync;
using NGGMultiplayer.Runtime;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NGGMultiplayer.Game
{
    /// <summary>
    /// Everything a game module needs, in one object handed to <see cref="IGameModule.Install"/>: the session
    /// (role, players, RPCs), the save bridge and fact table, Harmony and the patch helper, the replay guard, scene
    /// and world-readiness tracking, a main-thread dispatcher, coroutines, notifications and logging.
    /// <para>Patch methods are static; they reach the context through <see cref="Current"/> (null before install
    /// and after teardown, so every patch null-checks it).</para>
    /// Main thread only, except <see cref="Post"/>.
    /// </summary>
    public sealed class GameContext : IDisposable
    {
        private const string Log = "[Game] ";

        /// <summary>World readiness if SaveManager.SetUpScene cannot be hooked: this long after "Main" loaded.</summary>
        public const double FallbackWorldReadySeconds = 1.0;

        /// <summary>A "Main" load that never reports SetUpScene is declared ready after this long (with a warning).</summary>
        public const double WorldReadyWatchdogSeconds = 20.0;

        /// <summary><see cref="IsLoadingWorld"/> stays true this long after the world is ready (load-time Invokes).</summary>
        public const double LoadingHoldSeconds = 1.0;

        private const int MaxToasts = 5;
        private const float ToastWidth = 520f;

        private readonly List<IGameModule> _modules;
        private readonly object _postLock = new object();
        private readonly List<Action> _posted = new List<Action>();
        private readonly List<Action> _running = new List<Action>();
        private readonly List<DelayedAction> _delayed = new List<DelayedAction>();
        private readonly List<RpcRegistration> _rpcs = new List<RpcRegistration>();
        private readonly HashSet<string> _onceKeys = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<Toast> _toasts = new List<Toast>();
        private string _noticeText;
        private readonly Action<PlayerInfo> _onPlayerJoined;
        private readonly Action<PlayerInfo, PlayerLeaveReason> _onPlayerLeft;
        private readonly Action<PeerId> _onPeerReady;

        private IMultiplayerService _service;
        private Harmony _harmony;
        private GameDriver _driver;
        private Action _showLobbyWindow;
        private Func<string> _lobbyKeyText;
        private Func<KeyCode> _scorecardKey;
        private Func<bool> _remoteBallTrail;
        private Func<float> _trailSeconds;
        private NetSession _boundNet;
        private IPresenceProvider _presence;
        private int _sceneLoadId;
        private bool _loadingWorld;
        private double _loadingHoldUntil = double.NegativeInfinity;
        private bool _setUpSceneHooked;
        private bool _attached;
        private bool _disposed;
        private GUIStyle _toastStyle;

        internal GameContext(List<IGameModule> modules)
        {
            _modules = modules ?? throw new ArgumentNullException(nameof(modules));
            Replay = new ReplayGuard(() => Time.realtimeSinceStartupAsDouble);
            Patcher = new GamePatcher(() => _harmony ?? throw new InvalidOperationException("Harmony is not attached yet."));
            Save = new GameSaveBridge(() => IsHost, () => IsClientConnection);
            _onPlayerJoined = OnPlayerJoined;
            _onPlayerLeft = OnPlayerLeft;
            _onPeerReady = OnPeerReady;
        }

        /// <summary>The installed context, or null. For static Harmony patch methods.</summary>
        public static GameContext Current { get; private set; }

        // ---- Infrastructure --------------------------------------------------------------------------

        public Harmony Harmony => _harmony;

        public GamePatcher Patcher { get; }

        public ReplayGuard Replay { get; }

        /// <summary>The plugin's save backend (Core's ISaveBackend): session save store + facts.</summary>
        public GameSaveBridge Save { get; }

        /// <summary>The session's facts; see <see cref="GameSaveBridge.Table"/>.</summary>
        public FactTable Facts => Save.Table;

        /// <summary>Monotonic real time in seconds (unaffected by Time.timeScale).</summary>
        public double Now => Time.realtimeSinceStartupAsDouble;

        // ---- Session ---------------------------------------------------------------------------------

        public IMultiplayerService Service => _service;

        /// <summary>The session facade; null until Steam is up.</summary>
        public NetSession Net => _service?.Net;

        /// <summary>A session runs (its modules exist). On a client the host's save may still be loading.</summary>
        public bool InSession
        {
            get
            {
                var net = Net;
                return net != null && net.IsActive;
            }
        }

        /// <summary>Host: in session. Client: the host's save is loaded. Gameplay traffic flows only then.</summary>
        public bool IsReady
        {
            get
            {
                var net = Net;
                return net != null && net.IsReady;
            }
        }

        public bool IsHost
        {
            get
            {
                var net = Net;
                return net != null && net.IsActive && net.IsHost;
            }
        }

        public bool IsClient
        {
            get
            {
                var net = Net;
                return net != null && net.IsActive && !net.IsHost;
            }
        }

        /// <summary>Client of a running or still-connecting session (save writes must already be considered).</summary>
        public bool IsClientConnection
        {
            get
            {
                var net = Net;
                return net != null && net.Session.IsClient;
            }
        }

        public PeerId LocalId => Net?.LocalId ?? PeerId.None;

        public PeerId HostId => Net?.Session.HostId ?? PeerId.None;

        public IReadOnlyList<PlayerInfo> Players => _service != null ? _service.Players : Array.Empty<PlayerInfo>();

        public bool TryGetPlayer(PeerId id, out PlayerInfo player)
        {
            var net = Net;
            if (net != null && net.Session.IsActive)
                return net.Session.TryGetPlayer(id, out player);
            player = null;
            return false;
        }

        /// <summary>Display name for notifications; the Steam id when unknown.</summary>
        public string PlayerName(PeerId id) => TryGetPlayer(id, out var player) ? player.DisplayName : id.ToString();

        /// <summary>A player joined the roster (any role; includes the local player at session start).</summary>
        public event Action<PlayerInfo> PlayerJoined;

        /// <summary>A player left the roster.</summary>
        public event Action<PlayerInfo> PlayerLeft;

        /// <summary>Host: a client loaded the host's save and now receives RPCs. Send it per-session state here.</summary>
        public event Action<PeerId> PeerReady;

        /// <summary>Raised after the modules' <see cref="IGameModule.OnSessionReady"/>.</summary>
        public event Action<bool> SessionReady;

        /// <summary>Raised after the modules' <see cref="IGameModule.OnSessionEnded"/>.</summary>
        public event Action SessionEnded;

        // ---- RPCs ------------------------------------------------------------------------------------

        /// <summary>
        /// Registers a named RPC for every session (call in Install). Every peer must register every RPC it may
        /// receive, the host also those it only relays. Names carry the module's prefix (<see cref="GameMessages"/>).
        /// <paramref name="hostValidator"/> runs on the host for every client-sent call.
        /// </summary>
        public void RegisterRpc(string name, Action<PeerId, NetReader> handler, RpcValidator hostValidator = null)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("RPC name must not be empty.", nameof(name));
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));
            foreach (var existing in _rpcs)
            {
                if (existing.Name == name)
                    throw new InvalidOperationException($"RPC '{name}' is already registered.");
            }
            var registration = new RpcRegistration(name, handler, hostValidator);
            _rpcs.Add(registration);
            if (_boundNet != null)
                RegisterOn(_boundNet, registration);
        }

        /// <summary>Sends an RPC; false when no session runs or it could not be sent.</summary>
        public bool Send(string name, RpcTarget target, Action<NetWriter> write = null,
            Reliability reliability = Reliability.Reliable, PeerId peer = default)
        {
            var rpc = Net?.Rpc;
            if (rpc == null)
                return false;
            try
            {
                return rpc.Send(name, target, write, reliability, peer);
            }
            catch (Exception e)
            {
                ErrorOnce("send:" + name, $"{Log}Sending RPC '{name}' failed: {e}");
                return false;
            }
        }

        /// <summary>Closure-free variant of <see cref="Send(string, RpcTarget, Action{NetWriter}, Reliability, PeerId)"/>.</summary>
        public bool Send<T>(string name, RpcTarget target, T arg, Action<NetWriter, T> write,
            Reliability reliability = Reliability.Reliable, PeerId peer = default)
        {
            var rpc = Net?.Rpc;
            if (rpc == null)
                return false;
            try
            {
                return rpc.Send(name, target, arg, write, reliability, peer);
            }
            catch (Exception e)
            {
                ErrorOnce("send:" + name, $"{Log}Sending RPC '{name}' failed: {e}");
                return false;
            }
        }

        // ---- Scenes and world ------------------------------------------------------------------------

        /// <summary>The scene the local game is in.</summary>
        public GameScene Scene { get; private set; }

        /// <summary>"Main" is loaded and SaveManager.SetUpScene finished: game singletons and the save are in place.</summary>
        public bool WorldReady { get; private set; }

        /// <summary>A "Main" load is in progress, or finished less than <see cref="LoadingHoldSeconds"/> ago.</summary>
        public bool IsLoadingWorld => _loadingWorld || Now < _loadingHoldUntil;

        /// <summary>Counts single-mode scene loads; lets delayed work detect that the world changed under it.</summary>
        public int SceneLoadId => _sceneLoadId;

        /// <summary>A scene finished loading (before its objects' Start ran). <see cref="WorldReady"/> is false here.</summary>
        public event Action<GameScene> SceneLoaded;

        /// <summary>"Main" is set up (<see cref="WorldReady"/> just became true).</summary>
        public event Action WorldLoaded;

        /// <summary>
        /// Client: Lifecycle is about to replace the running scene with the host's (a load is posted or on its way).
        /// Modules leave the doomed world alone (no reconcile, no fact appliers). Cleared by the next scene load.
        /// </summary>
        public bool WorldReplacePending { get; private set; }

        /// <summary>The game's pause menu is open (any role; tracked by Lifecycle). HUDs hide while it is.</summary>
        public bool PauseMenuOpen { get; private set; }

        /// <summary>Lifecycle only.</summary>
        internal void SetWorldReplacePending(bool pending) => WorldReplacePending = pending;

        /// <summary>Lifecycle only.</summary>
        internal void SetPauseMenuOpen(bool open) => PauseMenuOpen = open;

        // ---- Dispatcher ------------------------------------------------------------------------------

        /// <summary>Runs <paramref name="action"/> on the main thread at the next Update. Safe from any thread.</summary>
        public void Post(Action action)
        {
            if (action == null)
                return;
            lock (_postLock)
                _posted.Add(action);
        }

        /// <summary>Runs <paramref name="action"/> after <paramref name="seconds"/> of real time (main thread).</summary>
        public void Delay(double seconds, Action action)
        {
            if (action == null)
                return;
            _delayed.Add(new DelayedAction(Now + Math.Max(0, seconds), action));
        }

        /// <summary>Starts a coroutine on the mod's persistent object (survives scene loads). Null before attach.</summary>
        public Coroutine RunCoroutine(IEnumerator routine)
        {
            if (routine == null || _driver == null)
                return null;
            return _driver.StartCoroutine(routine);
        }

        public void StopRoutine(Coroutine coroutine)
        {
            if (coroutine != null && _driver != null)
                _driver.StopCoroutine(coroutine);
        }

        /// <summary>Raised from LateUpdate.</summary>
        public event Action LateTick;

        /// <summary>Raised from OnGUI (IMGUI); draw HUDs here.</summary>
        public event Action Gui;

        // ---- Providers, UI, logging ------------------------------------------------------------------

        public IPresenceProvider Presence => _presence;

        /// <summary>Installs the Presence module's provider (null removes it).</summary>
        public void SetPresenceProvider(IPresenceProvider provider) => _presence = provider;

        /// <summary>The module of type <typeparamref name="T"/>, or null. For the integrator; modules use contracts instead.</summary>
        public T GetModule<T>() where T : class, IGameModule
        {
            foreach (var module in _modules)
            {
                if (module is T match)
                    return match;
            }
            return null;
        }

        /// <summary>
        /// The native multiplayer menus (main menu panel, pause buttons) are up in this scene, so the IMGUI fallbacks
        /// (Lifecycle's menu button, waiting panel and pause line) stay hidden. Set by the Menus module.
        /// </summary>
        public bool NativeMenusShown { get; set; }

        /// <summary>The native in-game player list (Menus module) is drawn, so Presence's IMGUI list stays hidden.</summary>
        public bool NativePlayerListShown { get; set; }

        /// <summary>The native notices (Menus module) are drawn, so the IMGUI notices at the top stay hidden.</summary>
        public bool NativeNoticesShown { get; set; }

        /// <summary>Remembers the privacy and size chosen when hosting as the next default (the plugin's config).</summary>
        public Action<LobbyVisibility, int> SaveLobbyDefaults { get; set; }

        /// <summary>Reads the local player's look ([Presence] Character); null: always <see cref="PlayerLook.Male"/>.</summary>
        public Func<PlayerLook> ReadLook { get; set; }

        /// <summary>Remembers a new look in the plugin's config.</summary>
        public Action<PlayerLook> SaveLook { get; set; }

        /// <summary>The golfer the local player is drawn as for the others (sent in ngg.pr.info).</summary>
        public PlayerLook Look
        {
            get
            {
                try
                {
                    var look = ReadLook?.Invoke() ?? PlayerLook.Male;
                    return look == PlayerLook.Female ? PlayerLook.Female : PlayerLook.Male;
                }
                catch (Exception)
                {
                    return PlayerLook.Male;
                }
            }
            set => SaveLook?.Invoke(value);
        }

        /// <summary>Opens the multiplayer window (host / invite / join).</summary>
        public void ShowLobbyWindow() => _showLobbyWindow?.Invoke();

        /// <summary>The key that toggles the multiplayer window, for on-screen hints ("F7").</summary>
        public string LobbyWindowKey
        {
            get
            {
                try
                {
                    string text = _lobbyKeyText?.Invoke();
                    return string.IsNullOrEmpty(text) ? "F7" : text;
                }
                catch (Exception)
                {
                    return "F7";
                }
            }
        }

        /// <summary>The key held to show the session scorecard ([General] ScorecardKey); None: no key.</summary>
        public KeyCode ScorecardKey
        {
            get
            {
                try
                {
                    return _scorecardKey?.Invoke() ?? DefaultScorecardKey;
                }
                catch (Exception)
                {
                    return DefaultScorecardKey;
                }
            }
        }

        /// <summary>[Presence] RemoteBallTrail: other players' ghost balls draw a trail. True when unset.</summary>
        public bool RemoteBallTrail
        {
            get
            {
                try
                {
                    return _remoteBallTrail?.Invoke() ?? true;
                }
                catch (Exception)
                {
                    return true;
                }
            }
        }

        /// <summary>[Presence] TrailSeconds: the ghost trail's length in seconds; 0 (or unset) matches the game's ball trail.</summary>
        public float TrailSeconds
        {
            get
            {
                try
                {
                    return _trailSeconds?.Invoke() ?? 0f;
                }
                catch (Exception)
                {
                    return 0f;
                }
            }
        }

        /// <summary>[Match] settings (the host's apply to the session); the defaults when unset.</summary>
        public Func<MatchSettings> ReadMatchSettings { get; set; }

        public MatchSettings MatchSettings
        {
            get
            {
                try
                {
                    return ReadMatchSettings?.Invoke() ?? MatchSettings.Default;
                }
                catch (Exception)
                {
                    return MatchSettings.Default;
                }
            }
        }

        /// <summary>The competitive 9-hole round this player sees (Presence's match), or null. The HUD reads it.</summary>
        internal Presence.MatchState Match { get; set; }

        /// <summary>The native match HUD (leaderboard, scorecard) is up, so the IMGUI scorecard stays hidden.</summary>
        public bool NativeMatchHudShown { get; set; }

        /// <summary>The scorecard key's default: H, which the game binds to nothing (the game binds Tab, #67).</summary>
        public const KeyCode DefaultScorecardKey = KeyCode.H;

        /// <summary>A short on-screen message at the top of the screen.</summary>
        public void Notify(string text, float seconds = 4f)
        {
            if (string.IsNullOrEmpty(text))
                return;
            NetLog.Info($"{Log}{text}");
            if (_toasts.Count >= MaxToasts)
                _toasts.RemoveAt(0);
            _toasts.Add(new Toast(text, Now + Math.Max(0.5f, seconds)));
            _noticeText = null;
        }

        public void Info(string message) => NetLog.Info(message);

        public void Warn(string message) => NetLog.Warn(message);

        public void Error(string message) => NetLog.Error(message);

        /// <summary>Logs <paramref name="message"/> as a warning the first time <paramref name="key"/> is seen.</summary>
        public void WarnOnce(string key, string message)
        {
            if (_onceKeys.Add("w:" + key))
                NetLog.Warn(message);
        }

        /// <summary>Logs <paramref name="message"/> as an error the first time <paramref name="key"/> is seen.</summary>
        public void ErrorOnce(string key, string message)
        {
            if (_onceKeys.Add("e:" + key))
                NetLog.Error(message);
        }

        // ---- Driven by the adapter and GameDriver ----------------------------------------------------

        internal void Attach(IMultiplayerService service, Harmony harmony, GameDriver driver, Action showLobbyWindow,
            Func<string> lobbyKeyText, Func<KeyCode> scorecardKey = null, Func<bool> remoteBallTrail = null,
            Func<float> trailSeconds = null)
        {
            if (_attached)
                throw new InvalidOperationException("The game context is already attached.");
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _harmony = harmony ?? throw new ArgumentNullException(nameof(harmony));
            _driver = driver;
            _showLobbyWindow = showLobbyWindow;
            _lobbyKeyText = lobbyKeyText;
            _scorecardKey = scorecardKey;
            _remoteBallTrail = remoteBallTrail;
            _trailSeconds = trailSeconds;
            _attached = true;
            Current = this;

            SceneManager.sceneLoaded += OnSceneLoaded;
            _setUpSceneHooked = WorldPatches.Install(this);
            var active = SceneManager.GetActiveScene();
            Scene = GameScenes.FromName(active.IsValid() ? active.name : null);
            // Attached while "Main" already runs (plugin reloaded): treat the world as ready.
            WorldReady = Scene == GameScene.Main && SaveManager.instance != null;
            UpdateRemoteVisibility();
            BindNet();
        }

        internal void Tick(float deltaTime)
        {
            if (_disposed)
                return;
            BindNet();
            RunPosted();
            RunDelayed();
            for (int i = 0; i < _modules.Count; i++)
            {
                try
                {
                    _modules[i].Tick(deltaTime);
                }
                catch (Exception e)
                {
                    ErrorOnce("tick:" + _modules[i].Name, $"{Log}{_modules[i].Name}.Tick threw: {e}");
                }
            }
            ExpireToasts();
        }

        internal void RaiseLateTick() => Raise(LateTick, nameof(LateTick));

        internal void DrawGui()
        {
            Raise(Gui, nameof(Gui));
            DrawToasts();
        }

        internal void HandleSessionReady(bool isHost)
        {
            Replay.Reset();
            if (isHost)
            {
                try
                {
                    Save.RebuildFacts();
                }
                catch (Exception e)
                {
                    NetLog.Error($"{Log}Building the session's facts failed: {e}");
                }
            }
            foreach (var module in _modules)
            {
                try
                {
                    module.OnSessionReady(isHost);
                }
                catch (Exception e)
                {
                    NetLog.Error($"{Log}{module.Name}.OnSessionReady threw: {e}");
                }
            }
            Raise(SessionReady, isHost, nameof(SessionReady));
        }

        internal void HandleSessionEnded()
        {
            Replay.Reset();
            WorldReplacePending = false;
            foreach (var module in _modules)
            {
                try
                {
                    module.OnSessionEnded();
                }
                catch (Exception e)
                {
                    NetLog.Error($"{Log}{module.Name}.OnSessionEnded threw: {e}");
                }
            }
            Raise(SessionEnded, nameof(SessionEnded));
        }

        /// <summary>SaveManager.SetUpScene is starting (shared patch).</summary>
        internal void OnSetUpSceneStarting()
        {
            if (Scene != GameScene.Main)
                return;
            _loadingWorld = true;
        }

        /// <summary>SaveManager.SetUpScene returned (shared patch).</summary>
        internal void OnSetUpSceneFinished()
        {
            if (Scene != GameScene.Main)
                return;
            MarkWorldReady(_sceneLoadId, null);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            UnbindNet();
            RemoteEntityView.WorldVisible = true;
            if (ReferenceEquals(Current, this))
                Current = null;
            lock (_postLock)
                _posted.Clear();
            _delayed.Clear();
            _toasts.Clear();
            _noticeText = null;
            LateTick = null;
            Gui = null;
            PlayerJoined = null;
            PlayerLeft = null;
            PeerReady = null;
            SessionReady = null;
            SessionEnded = null;
            WorldLoaded = null;
            SceneLoaded = null;
        }

        // ---- Internals -------------------------------------------------------------------------------

        private void BindNet()
        {
            var net = _service?.Net;
            if (ReferenceEquals(net, _boundNet))
                return;
            UnbindNet();
            if (net == null)
                return;
            _boundNet = net;
            foreach (var registration in _rpcs)
                RegisterOn(net, registration);
            net.Session.PlayerJoined += _onPlayerJoined;
            net.Session.PlayerLeft += _onPlayerLeft;
            net.PeerReady += _onPeerReady;
        }

        private void UnbindNet()
        {
            var net = _boundNet;
            _boundNet = null;
            if (net == null)
                return;
            net.Session.PlayerJoined -= _onPlayerJoined;
            net.Session.PlayerLeft -= _onPlayerLeft;
            net.PeerReady -= _onPeerReady;
            foreach (var registration in _rpcs)
            {
                try
                {
                    net.UnregisterRpc(registration.Name);
                }
                catch (Exception e)
                {
                    NetLog.Warn($"{Log}Unregistering RPC '{registration.Name}' failed: {e.Message}");
                }
            }
        }

        private static void RegisterOn(NetSession net, RpcRegistration registration)
        {
            try
            {
                net.RegisterRpc(registration.Name, registration.Handler, registration.Validator);
            }
            catch (Exception e)
            {
                NetLog.Error($"{Log}Registering RPC '{registration.Name}' failed: {e.Message}");
            }
        }

        private void OnPlayerJoined(PlayerInfo player) => Raise(PlayerJoined, player, nameof(PlayerJoined));

        private void OnPlayerLeft(PlayerInfo player, PlayerLeaveReason reason) => Raise(PlayerLeft, player, nameof(PlayerLeft));

        private void OnPeerReady(PeerId peer) => Raise(PeerReady, peer, nameof(PeerReady));

        private void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, LoadSceneMode mode)
        {
            if (_disposed || mode != LoadSceneMode.Single)
                return;
            int loadId = ++_sceneLoadId;
            Scene = GameScenes.FromName(scene.name);
            WorldReady = false;
            WorldReplacePending = false;
            PauseMenuOpen = false;
            _loadingWorld = Scene == GameScene.Main;
            _loadingHoldUntil = double.NegativeInfinity;
            GameIds.InvalidateCache();
            Replay.Reset();
            UpdateRemoteVisibility();
            if (Scene == GameScene.Main)
            {
                if (!_setUpSceneHooked)
                    Delay(FallbackWorldReadySeconds, () => MarkWorldReady(loadId, null));
                Delay(WorldReadyWatchdogSeconds, () => MarkWorldReady(loadId,
                    "SaveManager.SetUpScene did not finish within " + WorldReadyWatchdogSeconds + "s of loading Main"));
            }
            Raise(SceneLoaded, Scene, nameof(SceneLoaded));
        }

        private void MarkWorldReady(int loadId, string warning)
        {
            if (_disposed || loadId != _sceneLoadId || Scene != GameScene.Main || WorldReady)
                return;
            if (warning != null)
                NetLog.Warn($"{Log}{warning}; treating the world as ready.");
            WorldReady = true;
            _loadingWorld = false;
            _loadingHoldUntil = Now + LoadingHoldSeconds;
            UpdateRemoteVisibility();
            Raise(WorldLoaded, nameof(WorldLoaded));
        }

        private void UpdateRemoteVisibility()
        {
            // Remote avatars live in world coordinates; outside the open world they would float in the menu.
            RemoteEntityView.WorldVisible = Scene == GameScene.Main && WorldReady;
        }

        private void RunPosted()
        {
            lock (_postLock)
            {
                if (_posted.Count == 0)
                    return;
                _running.AddRange(_posted);
                _posted.Clear();
            }
            foreach (var action in _running)
                RunSafe(action, "posted action");
            _running.Clear();
        }

        private void RunDelayed()
        {
            if (_delayed.Count == 0)
                return;
            double now = Now;
            // Collected first: an action may schedule more.
            for (int i = 0; i < _delayed.Count;)
            {
                if (_delayed[i].DueAt <= now)
                {
                    _running.Add(_delayed[i].Action);
                    _delayed.RemoveAt(i);
                }
                else
                {
                    i++;
                }
            }
            foreach (var action in _running)
                RunSafe(action, "delayed action");
            _running.Clear();
        }

        private static void RunSafe(Action action, string what)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                NetLog.Error($"{Log}A {what} threw: {e}");
            }
        }

        private void ExpireToasts()
        {
            double now = Now;
            for (int i = _toasts.Count - 1; i >= 0; i--)
            {
                if (_toasts[i].Until <= now)
                {
                    _toasts.RemoveAt(i);
                    _noticeText = null;
                }
            }
        }

        /// <summary>The current notices (<see cref="Notify"/>), oldest first, one per line; null when there are none.</summary>
        internal string NoticeText()
        {
            if (_toasts.Count == 0)
                return null;
            if (_noticeText != null)
                return _noticeText;
            var text = new System.Text.StringBuilder();
            foreach (var toast in _toasts)
            {
                if (text.Length > 0)
                    text.Append('\n');
                text.Append(toast.Text);
            }
            _noticeText = text.ToString();
            return _noticeText;
        }

        private void DrawToasts()
        {
            if (_toasts.Count == 0 || NativeNoticesShown)
                return;
            if (_toastStyle == null)
            {
                _toastStyle = new GUIStyle(GUI.skin.box)
                {
                    wordWrap = true,
                    fontSize = 16,
                    alignment = TextAnchor.MiddleCenter
                };
            }
            float x = (Screen.width - ToastWidth) * 0.5f;
            float y = 12f;
            foreach (var toast in _toasts)
            {
                float height = _toastStyle.CalcHeight(new GUIContent(toast.Text), ToastWidth) + 8f;
                GUI.Box(new Rect(x, y, ToastWidth, height), toast.Text, _toastStyle);
                y += height + 4f;
            }
        }

        private static void Raise(Action handler, string name)
        {
            if (handler == null)
                return;
            try
            {
                handler();
            }
            catch (Exception e)
            {
                NetLog.Error($"{Log}{name} handler threw: {e}");
            }
        }

        private static void Raise<T>(Action<T> handler, T arg, string name)
        {
            if (handler == null)
                return;
            try
            {
                handler(arg);
            }
            catch (Exception e)
            {
                NetLog.Error($"{Log}{name} handler threw: {e}");
            }
        }

        private readonly struct DelayedAction
        {
            public DelayedAction(double dueAt, Action action)
            {
                DueAt = dueAt;
                Action = action;
            }

            public readonly double DueAt;
            public readonly Action Action;
        }

        private readonly struct Toast
        {
            public Toast(string text, double until)
            {
                Text = text;
                Until = until;
            }

            public readonly string Text;
            public readonly double Until;
        }

        private sealed class RpcRegistration
        {
            public RpcRegistration(string name, Action<PeerId, NetReader> handler, RpcValidator validator)
            {
                Name = name;
                Handler = handler;
                Validator = validator;
            }

            public readonly string Name;
            public readonly Action<PeerId, NetReader> Handler;
            public readonly RpcValidator Validator;
        }
    }
}
