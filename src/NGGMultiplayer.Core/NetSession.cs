using System;
using System.Collections.Generic;
using NGGMultiplayer.Core.Diagnostics;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Save;
using NGGMultiplayer.Core.Session;
using NGGMultiplayer.Core.Sync;

namespace NGGMultiplayer.Core
{
    /// <summary>
    /// One object that runs a multiplayer session: <see cref="SessionManager"/> plus save sync, entity sync and RPC
    /// routing, driven by a single <see cref="Update"/> call per frame.
    /// <list type="bullet">
    /// <item>The modules are created per session, when it starts (<see cref="Started"/>), and torn down when it ends
    /// (<see cref="Ended"/>). Their properties are null in between. RPCs registered with <see cref="RegisterRpc"/>
    /// and <see cref="OwnershipValidator"/> carry over to every session.</item>
    /// <item>Host: a client sees entities and RPCs once it has loaded the host's save
    /// (<see cref="NetSessionOptions.GateGameplayOnSaveSync"/>); <see cref="PeerReady"/> marks that moment.</item>
    /// <item>Client: <see cref="Ready"/> is raised once the host's save is loaded. If save sync gives up, the client
    /// leaves at the end of that Update. When the session ends the local save has been restored before
    /// <see cref="Ended"/> is raised.</item>
    /// <item>Entity sync runs on the host's clock (<see cref="HostTime"/>). A client's registry starts updating once
    /// its clock has a first sample from the host, so states are never stamped on an unsynchronized time base.</item>
    /// </list>
    /// Not thread-safe; call everything from the main thread. The transport is not owned.
    /// </summary>
    public sealed class NetSession : IDisposable
    {
        private const string Log = "[NetSession] ";

        private readonly ISaveBackend _saveBackend;
        private readonly INetEntityFactory _entityFactory;
        private readonly NetSessionOptions _options;
        private readonly List<RpcEntry> _rpcs = new List<RpcEntry>();
        private readonly Action _onSessionStarted;
        private readonly Action<SessionEndInfo> _onSessionEnded;
        private readonly Action<PeerId, uint> _onPeerSynced;
        private readonly Action _onClientSynced;
        private readonly Action<string> _onClientSyncFailed;

        private SaveSyncGate _gate;
        private Func<PeerId, NetEntity, bool> _ownershipValidator;
        private bool _ready;
        private bool _leavePending;
        private bool _disposed;

        /// <param name="saveBackend">The game's save system; the host's save is the session's source of truth.</param>
        /// <param name="entityFactory">Creates game objects for entities the host announces. May be null.</param>
        public NetSession(ITransport transport, ISaveBackend saveBackend, INetEntityFactory entityFactory = null,
            NetSessionOptions options = null)
        {
            if (transport == null)
                throw new ArgumentNullException(nameof(transport));
            _saveBackend = saveBackend ?? throw new ArgumentNullException(nameof(saveBackend));
            _entityFactory = entityFactory;
            _options = options ?? new NetSessionOptions();
            _options.Validate();

            Session = new SessionManager(transport, _options.Session);
            _onSessionStarted = OnSessionStarted;
            _onSessionEnded = OnSessionEnded;
            _onPeerSynced = OnPeerSynced;
            _onClientSynced = OnClientSynced;
            _onClientSyncFailed = OnClientSyncFailed;
            Session.SessionStarted += _onSessionStarted;
            Session.SessionEnded += _onSessionEnded;
        }

        public SessionManager Session { get; }

        public ISaveBackend SaveBackend => _saveBackend;

        public NetSessionOptions Options => _options;

        /// <summary>Host only, while a session runs.</summary>
        public SaveSyncHost SaveHost { get; private set; }

        /// <summary>Client only, while a session runs.</summary>
        public SaveSyncClient SaveClient { get; private set; }

        /// <summary>While a session runs.</summary>
        public NetEntityRegistry Entities { get; private set; }

        /// <summary>While a session runs.</summary>
        public RpcRouter Rpc { get; private set; }

        /// <summary>True while a session runs and its modules exist.</summary>
        public bool IsActive => Session.IsActive && Entities != null;

        public bool IsHost => Session.IsHost;

        public PeerId LocalId => Session.LocalId;

        /// <summary>Host: true while active. Client: true once the host's save has been loaded this session.</summary>
        public bool IsReady => IsActive && _ready;

        /// <summary>The host's clock; the time base of entity states.</summary>
        public double HostTime => Session.HostTime;

        /// <summary>
        /// Host: decides client ownership requests; applied to every session's registry. Null uses the registry's
        /// default policy.
        /// </summary>
        public Func<PeerId, NetEntity, bool> OwnershipValidator
        {
            get => _ownershipValidator;
            set
            {
                _ownershipValidator = value;
                if (Entities != null)
                    Entities.OwnershipValidator = value;
            }
        }

        /// <summary>Modules exist; subscribe to their events and send initial state here.</summary>
        public event Action Started;

        /// <summary>Host: right after <see cref="Started"/>. Client: the host's save was loaded (once per session).</summary>
        public event Action Ready;

        /// <summary>Host: a client loaded the host's save and now receives entities and RPCs (once per client).</summary>
        public event Action<PeerId> PeerReady;

        /// <summary>The session ended and its modules are gone; on a client the local save was restored.</summary>
        public event Action<SessionEndInfo> Ended;

        /// <summary>Host: true once <paramref name="peer"/> has loaded the save and receives gameplay traffic.</summary>
        public bool IsPeerReady(PeerId peer)
        {
            return IsActive && _gate != null && _gate.IsReady(peer);
        }

        /// <summary>
        /// True while no session runs but the save backend still suppresses disk writes: an earlier client session
        /// could not restore the player's own save, so the host's save is still loaded in memory. Hosting would
        /// share it as the player's progress and joining would back it up as theirs, so both are refused until the
        /// game is restarted.
        /// </summary>
        public bool LocalSaveRestorePending
        {
            get
            {
                if (Session.State != SessionState.Idle)
                    return false;
                try
                {
                    return _saveBackend.SuppressDiskWrites;
                }
                catch (Exception e)
                {
                    NetLog.Error($"{Log}Reading SuppressDiskWrites failed: {e}");
                    return true;
                }
            }
        }

        /// <exception cref="InvalidOperationException">A session runs, or <see cref="LocalSaveRestorePending"/>.</exception>
        public void StartHost()
        {
            ThrowIfDisposed();
            ThrowIfRestorePending();
            Session.StartHost();
        }

        /// <exception cref="InvalidOperationException">A session runs, or <see cref="LocalSaveRestorePending"/>.</exception>
        public void Connect(PeerId host)
        {
            ThrowIfDisposed();
            ThrowIfRestorePending();
            Session.Connect(host);
        }

        /// <summary>Ends the session; a no-op when idle. Raises <see cref="Ended"/>.</summary>
        public void Leave() => Session.Leave();

        /// <summary>
        /// Registers an RPC for this and every later session. Must happen before peers send it; registering in
        /// <see cref="Started"/> or before connecting is always early enough.
        /// </summary>
        /// <exception cref="InvalidOperationException">The name, or its id, is already registered.</exception>
        public void RegisterRpc(string name, Action<PeerId, NetReader> handler, RpcValidator hostValidator = null)
        {
            ThrowIfDisposed();
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("RPC name must not be empty.", nameof(name));
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));
            uint id = RpcRouter.GetId(name);
            int index = IndexOfRpc(id);
            if (index >= 0)
            {
                throw new InvalidOperationException(_rpcs[index].Name == name
                    ? $"RPC '{name}' is already registered."
                    : $"RPC '{name}' collides with '{_rpcs[index].Name}' (id 0x{id:X8}); rename one of them.");
            }
            // The live router throws on a clash with an RPC registered on it directly; keep both views in step.
            Rpc?.Register(name, handler, hostValidator);
            _rpcs.Add(new RpcEntry(id, name, handler, hostValidator));
        }

        public bool UnregisterRpc(string name)
        {
            if (name == null)
                throw new ArgumentNullException(nameof(name));
            int index = IndexOfRpc(RpcRouter.GetId(name));
            if (index < 0 || _rpcs[index].Name != name)
                return false;
            _rpcs.RemoveAt(index);
            Rpc?.Unregister(name);
            return true;
        }

        /// <summary>
        /// Runs one frame: receives and dispatches packets, then ticks save sync and entity sync.
        /// </summary>
        /// <param name="now">Monotonic local time in seconds (for example the game's unscaled real time).</param>
        public void Update(double now)
        {
            ThrowIfDisposed();
            Session.Update(now);
            // A callback may end the session and null the modules at any step, so each is re-read.
            SaveHost?.Tick(now);
            SaveClient?.Tick(now);
            var entities = Entities;
            if (entities != null && Session.IsActive && (Session.IsHost || Session.Clock.HasSamples))
                entities.Update(Session.HostTime);
            if (_leavePending)
            {
                _leavePending = false;
                Session.Leave();
            }
        }

        /// <summary>Leaves any running session (restoring the local save on a client) and detaches from it.</summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            Session.Dispose();
            TearDownModules();
            Session.SessionStarted -= _onSessionStarted;
            Session.SessionEnded -= _onSessionEnded;
            _disposed = true;
        }

        private void OnSessionStarted()
        {
            TearDownModules();
            try
            {
                CreateModules();
            }
            catch (Exception e)
            {
                NetLog.Error($"{Log}Could not set up the session's modules; leaving. {e}");
                TearDownModules();
                Session.Leave();
                return;
            }

            NetLog.Info($"{Log}Session started as {(Session.IsHost ? "host" : "client")}.");
            Raise(Started, nameof(Started));
            if (Session.IsHost && IsActive)
                MarkReady();
        }

        private void CreateModules()
        {
            ISessionMessaging gameplay = Session;
            if (Session.IsHost)
            {
                SaveHost = new SaveSyncHost(Session, _saveBackend, _options.ClaimValidator, _options.Save);
                SaveHost.PeerSynced += _onPeerSynced;
                // The gate tracks readiness either way; gameplay only goes through it when gating is on.
                _gate = new SaveSyncGate(Session);
                if (_options.GateGameplayOnSaveSync)
                    gameplay = _gate;
            }
            else
            {
                SaveClient = new SaveSyncClient(Session, _saveBackend, _options.Save);
                SaveClient.Synced += _onClientSynced;
                SaveClient.SyncFailed += _onClientSyncFailed;
            }

            Entities = new NetEntityRegistry(gameplay, _entityFactory, _options.Entities)
            {
                OwnershipValidator = _ownershipValidator
            };
            Rpc = new RpcRouter(gameplay);
            foreach (var rpc in _rpcs)
                Rpc.Register(rpc.Name, rpc.Handler, rpc.Validator);
        }

        private void OnSessionEnded(SessionEndInfo info)
        {
            // The modules already reacted to SessionClosed (save restored, entities despawned); now release them.
            bool hadModules = Entities != null || SaveHost != null || SaveClient != null;
            TearDownModules();
            if (hadModules)
                Raise(Ended, info, nameof(Ended));
        }

        private void OnPeerSynced(PeerId peer, uint revision)
        {
            if (_gate != null && _gate.MarkReady(peer))
                Raise(PeerReady, peer, nameof(PeerReady));
        }

        private void OnClientSynced()
        {
            if (!_ready)
                MarkReady();
        }

        private void OnClientSyncFailed(string reason)
        {
            // Leaving from inside the save client's own callback would dispose it mid-call; defer to the end of Update.
            NetLog.Error($"{Log}Save sync failed ({reason}); leaving the session.");
            _leavePending = true;
        }

        private void MarkReady()
        {
            _ready = true;
            Raise(Ready, nameof(Ready));
        }

        private void TearDownModules()
        {
            _ready = false;
            _leavePending = false;
            var rpc = Rpc;
            var entities = Entities;
            var saveHost = SaveHost;
            var saveClient = SaveClient;
            var gate = _gate;
            Rpc = null;
            Entities = null;
            SaveHost = null;
            SaveClient = null;
            _gate = null;

            rpc?.Dispose();
            entities?.Dispose();
            gate?.Dispose();
            if (saveHost != null)
            {
                saveHost.PeerSynced -= _onPeerSynced;
                saveHost.Dispose();
            }
            if (saveClient != null)
            {
                saveClient.Synced -= _onClientSynced;
                saveClient.SyncFailed -= _onClientSyncFailed;
                // Restores the local save if the session did not already do so.
                saveClient.Dispose();
            }
        }

        private int IndexOfRpc(uint id)
        {
            for (int i = 0; i < _rpcs.Count; i++)
            {
                if (_rpcs[i].Id == id)
                    return i;
            }
            return -1;
        }

        private void ThrowIfRestorePending()
        {
            if (LocalSaveRestorePending)
                throw new InvalidOperationException(
                    "The local save from the last session could not be restored; restart the game before playing online again.");
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(NetSession));
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

        private readonly struct RpcEntry
        {
            public RpcEntry(uint id, string name, Action<PeerId, NetReader> handler, RpcValidator validator)
            {
                Id = id;
                Name = name;
                Handler = handler;
                Validator = validator;
            }

            public readonly uint Id;
            public readonly string Name;
            public readonly Action<PeerId, NetReader> Handler;
            public readonly RpcValidator Validator;
        }
    }
}
