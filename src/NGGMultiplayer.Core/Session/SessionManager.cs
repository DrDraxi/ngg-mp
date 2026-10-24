using System;
using System.Collections.Generic;
using NGGMultiplayer.Core.Diagnostics;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;

namespace NGGMultiplayer.Core.Session
{
    /// <summary>
    /// Star-topology session over an <see cref="ITransport"/>: handshake, roster, heartbeats, timeouts and
    /// dispatch of non-session messages. Entirely driven by <see cref="Update"/>; no threads or timers, and
    /// every event is raised synchronously from Update or from the public method that caused it.
    /// <para>
    /// Clients only talk to the host and ignore packets from anyone else. The host ignores everything but
    /// Hello from peers that have not completed the handshake. Malformed packets are dropped and logged.
    /// </para>
    /// <para>
    /// <see cref="PlayerJoined"/>/<see cref="PlayerLeft"/> are raised for remote players only. A client that
    /// joins gets <see cref="SessionStarted"/> followed by PlayerJoined for every player already present.
    /// When the session ends, only <see cref="SessionEnded"/> is raised (no PlayerLeft per player).
    /// </para>
    /// <para>
    /// As an <see cref="ISessionMessaging"/> it serves save sync, entity sync and RPC routing: its PeerId-based
    /// events are raised before the public <see cref="PlayerInfo"/>-based ones, and
    /// <see cref="ISessionMessaging.SessionClosed"/> before <see cref="SessionEnded"/>, so those modules settle
    /// (for example, restore the local save) before application code hears about the change.
    /// </para>
    /// Not thread-safe. The transport is not owned and is never disposed by this class.
    /// </summary>
    public sealed class SessionManager : ISessionMessaging, IDisposable
    {
        private const int MaxBufferedPackets = 1024;
        private const int MaxBufferedBytes = 4 * 1024 * 1024;
        private const int MaxLocalQueue = 4096;
        private const double WarmupPingIntervalSeconds = 0.2;

        private readonly ITransport _transport;
        private readonly SessionConfig _config;
        private readonly NetClock _clock;
        private readonly List<IncomingPacket> _incoming = new List<IncomingPacket>(64);
        private readonly NetReader _reader = new NetReader();
        private readonly NetReader _dispatchReader = new NetReader();
        private readonly NetWriter _writer = new NetWriter(512);
        private readonly Action<PeerId, NetReader>[] _handlers = new Action<PeerId, NetReader>[256];

        // Roster sorted by player index; includes the local player.
        private readonly List<PlayerInfo> _players = new List<PlayerInfo>(SessionConfig.AbsoluteMaxPlayers);
        // Host only: handshaken clients.
        private readonly Dictionary<PeerId, RemotePeer> _peers = new Dictionary<PeerId, RemotePeer>();
        private readonly HashSet<PeerId> _banned = new HashSet<PeerId>();
        // Client only: packets from the host that overtook Welcome on another channel.
        private readonly List<IncomingPacket> _buffered = new List<IncomingPacket>();
        private readonly Queue<byte[]> _localQueue = new Queue<byte[]>();

        private readonly List<SessionWire.RosterEntry> _rosterEntries = new List<SessionWire.RosterEntry>(SessionConfig.AbsoluteMaxPlayers);
        private readonly List<PeerId> _scratchPeers = new List<PeerId>();
        private readonly List<PlayerInfo> _joinedScratch = new List<PlayerInfo>();
        private readonly List<PlayerInfo> _leftScratch = new List<PlayerInfo>();

        private SessionRole _role;
        private SessionState _state;
        private PeerId _hostId;
        private PlayerInfo _localPlayer;
        private int _generation;
        private double _now;
        private bool _hasTime;
        private bool _timersPending;
        private bool _updating;
        private bool _disposed;

        // Client timers.
        private double _connectStartedAt;
        private double _nextHelloAt;
        private double _hostLastHeard;
        private double _nextHostPingAt;
        private int _bufferedBytes;
        // Identifies this Connect call to the host, so a reconnect is told apart from a Hello retry.
        private uint _connectNonce;

        // Host timers.
        private bool _rosterDirty;
        private double _nextRosterRefreshAt;

        // ISessionMessaging events.
        private Action<PeerId> _peerJoined;
        private Action<PeerId> _peerLeft;
        private Action _sessionClosed;

        public SessionManager(ITransport transport, SessionConfig config = null)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _config = config ?? new SessionConfig();
            _config.Validate();
            _clock = new NetClock(_config.RttSmoothing, _config.OffsetSmoothing);
        }

        /// <summary>Raised when hosting starts, or when a client receives Welcome.</summary>
        public event Action SessionStarted;

        /// <summary>Raised once per session, after the manager is already back to <see cref="SessionState.Idle"/>.</summary>
        public event Action<SessionEndInfo> SessionEnded;

        public event Action<PlayerInfo> PlayerJoined;

        public event Action<PlayerInfo, PlayerLeaveReason> PlayerLeft;

        public SessionConfig Config => _config;

        public PeerId LocalId => _transport.LocalId;

        public SessionRole Role => _role;

        public SessionState State => _state;

        public bool IsActive => _state == SessionState.Active;

        public bool IsHost => _role == SessionRole.Host;

        public bool IsClient => _role == SessionRole.Client;

        /// <summary>The host's id while a session exists (the local id when hosting); otherwise <see cref="PeerId.None"/>.</summary>
        public PeerId HostId => _hostId;

        /// <summary>Null until the session is active.</summary>
        public PlayerInfo LocalPlayer => _localPlayer;

        /// <summary>Current roster sorted by player index, local player included. Empty when not active.</summary>
        public IReadOnlyList<PlayerInfo> Players => _players;

        /// <summary>
        /// Client: RTT to the host and offset to the host's clock. Host: never sampled (offset 0).
        /// Reset when a new session starts.
        /// </summary>
        public NetClock Clock => _clock;

        /// <summary>Latest time passed to <see cref="Update"/> (non-decreasing).</summary>
        public double Now => _now;

        /// <summary>Estimate of the host's <see cref="Now"/>. Equal to <see cref="Now"/> on the host.</summary>
        public double HostTime => _role == SessionRole.Client ? _clock.ToRemoteTime(_now) : _now;

        /// <summary>
        /// Host only: decides whether a peer may join (e.g. "is a member of the Steam lobby").
        /// Null admits everyone. A throwing filter counts as a refusal.
        /// </summary>
        public Func<PeerId, bool> AdmissionFilter { get; set; }

        event Action<PeerId> ISessionMessaging.PlayerJoined
        {
            add => _peerJoined += value;
            remove => _peerJoined -= value;
        }

        event Action<PeerId> ISessionMessaging.PlayerLeft
        {
            add => _peerLeft += value;
            remove => _peerLeft -= value;
        }

        event Action ISessionMessaging.SessionClosed
        {
            add => _sessionClosed += value;
            remove => _sessionClosed -= value;
        }

        /// <summary>
        /// True if packets can be sent to <paramref name="peer"/> right now: a handshaken client on the host, the
        /// host on a client. Always false for the local id and while the session is not active.
        /// </summary>
        public bool IsPeerConnected(PeerId peer)
        {
            if (_state != SessionState.Active || !peer.IsValid || peer == LocalId)
                return false;
            return _role == SessionRole.Host ? _peers.ContainsKey(peer) : peer == _hostId;
        }

        public bool TryGetPlayer(PeerId id, out PlayerInfo player)
        {
            for (int i = 0; i < _players.Count; i++)
            {
                if (_players[i].Id == id)
                {
                    player = _players[i];
                    return true;
                }
            }
            player = null;
            return false;
        }

        public void StartHost()
        {
            ThrowIfDisposed();
            if (_state != SessionState.Idle)
                throw new InvalidOperationException("A session is already running; call Leave first.");
            _config.Validate();

            ResetSessionState();
            _generation++;
            _role = SessionRole.Host;
            _state = SessionState.Active;
            _hostId = LocalId;
            _localPlayer = new PlayerInfo(LocalId, 0, LocalDisplayName(0), isHost: true, isLocal: true);
            _players.Add(_localPlayer);
            _timersPending = true;

            NetLog.Info($"Session: hosting as {_localPlayer} (max {_config.MaxPlayers} players).");
            RaiseSessionStarted();
        }

        /// <summary>Sends Hello to <paramref name="host"/>; the outcome arrives as SessionStarted or SessionEnded.</summary>
        public void Connect(PeerId host)
        {
            ThrowIfDisposed();
            if (_state != SessionState.Idle)
                throw new InvalidOperationException("A session is already running; call Leave first.");
            if (!host.IsValid)
                throw new ArgumentException("Host id must be valid.", nameof(host));
            if (host == LocalId)
                throw new ArgumentException("Cannot connect to self; use StartHost.", nameof(host));
            _config.Validate();

            ResetSessionState();
            _generation++;
            _role = SessionRole.Client;
            _state = SessionState.Connecting;
            _hostId = host;
            _connectNonce = NewConnectNonce(_connectNonce);
            _timersPending = true;

            NetLog.Info($"Session: connecting to host {host}.");
            SendHello();
        }

        /// <summary>
        /// Ends the session. A host tells every client the session is over; a client says Goodbye.
        /// Raises <see cref="SessionEnded"/> with <see cref="SessionEndReason.LocalLeft"/>. No-op when idle.
        /// </summary>
        public void Leave()
        {
            if (_state == SessionState.Idle)
                return;

            try
            {
                if (_role == SessionRole.Host)
                {
                    BuildSessionEnded(SessionEndReason.HostLeft, "The host left the session.");
                    foreach (var kv in _peers)
                        SendControl(kv.Key, Reliability.Reliable);
                }
                else
                {
                    PacketBuilder.Begin(_writer, MessageType.Goodbye);
                    SendControl(_hostId, Reliability.Reliable);
                }
            }
            catch (Exception ex)
            {
                // A dead transport must not keep the session alive; peers will time out instead.
                NetLog.Warn($"Session: could not send farewell: {ex.Message}");
            }
            EndSession(new SessionEndInfo(SessionEndReason.LocalLeft, RejectReason.Unknown, "Left the session."));
        }

        /// <summary>Host only: removes a client and refuses it for the rest of this session.</summary>
        public bool Kick(PeerId peer, string message = null)
        {
            ThrowIfDisposed();
            if (_role != SessionRole.Host || _state != SessionState.Active || !_peers.TryGetValue(peer, out var remote))
                return false;

            BuildSessionEnded(SessionEndReason.Kicked, string.IsNullOrEmpty(message) ? "Kicked by the host." : message);
            SendControl(peer, Reliability.Reliable);
            _banned.Add(peer);

            int gen = _generation;
            // The kicked peer must still receive SessionEnded, so the transport session is not closed here.
            RemovePeer(remote, PlayerLeaveReason.Kicked, disconnect: false);
            if (gen == _generation)
                FlushRoster();
            return true;
        }

        /// <summary>
        /// Tells the session that the transport lost its link to <paramref name="peer"/> (for example Steam failed the
        /// connection), so reliable messages may have been lost. The host drops that client as timed out; a client
        /// whose host link failed ends the session with <see cref="SessionEndReason.HostTimedOut"/>. A client still
        /// connecting keeps retrying until its connect timeout. Unknown peers are ignored. Returns true if acted on.
        /// </summary>
        public bool ReportLinkLost(PeerId peer)
        {
            ThrowIfDisposed();
            if (!peer.IsValid || peer == LocalId)
                return false;
            if (_role == SessionRole.Host)
            {
                if (_state != SessionState.Active || !_peers.TryGetValue(peer, out var remote))
                    return false;
                NetLog.Info($"Session: lost the link to {remote.Player}; dropping it.");
                int gen = _generation;
                RemovePeer(remote, PlayerLeaveReason.TimedOut, disconnect: true);
                if (gen == _generation && _state == SessionState.Active)
                    FlushRoster();
                return true;
            }
            if (_role != SessionRole.Client || _state != SessionState.Active || peer != _hostId)
                return false;
            NetLog.Info($"Session: lost the link to host {_hostId}.");
            _transport.Disconnect(_hostId);
            EndSession(new SessionEndInfo(SessionEndReason.HostTimedOut, RejectReason.Unknown, "Lost connection to the host."));
            return true;
        }

        /// <summary>Polls the transport, dispatches messages, and runs heartbeats and timeouts.</summary>
        /// <param name="nowSeconds">Monotonic local time; earlier values than a previous call are ignored.</param>
        public void Update(double nowSeconds)
        {
            ThrowIfDisposed();
            if (double.IsNaN(nowSeconds) || double.IsInfinity(nowSeconds))
                throw new ArgumentOutOfRangeException(nameof(nowSeconds), nowSeconds, "Must be finite.");
            if (_updating)
                throw new InvalidOperationException("Update is not re-entrant.");

            _updating = true;
            try
            {
                if (!_hasTime || nowSeconds > _now)
                    _now = nowSeconds;
                _hasTime = true;
                if (_timersPending)
                    InitTimers();

                int gen = _generation;
                // Pings go out before dispatch: a ping is stamped with _now, which a slow handler later in this
                // Update would make stale, and a stale stamp inflates the RTT and skews the clock offset.
                if (_state == SessionState.Active)
                    SendDuePings();

                _incoming.Clear();
                _transport.Poll(_incoming);
                for (int i = 0; i < _incoming.Count; i++)
                {
                    if (_state == SessionState.Idle || gen != _generation)
                        break;
                    HandlePacket(_incoming[i]);
                }

                if (_state == SessionState.Active && gen == _generation)
                    ProcessLocalQueue(gen);

                if (_state != SessionState.Idle && gen == _generation)
                {
                    if (_role == SessionRole.Host)
                        TickHost(gen);
                    else
                        TickClient();
                }
            }
            finally
            {
                _incoming.Clear();
                _updating = false;
            }
        }

        /// <summary>
        /// Registers the handler for a non-session message type (20..127). The reader is positioned after the
        /// type byte and is only valid during the call. A <see cref="ProtocolException"/> thrown by the handler
        /// drops the packet; any other exception is logged and swallowed.
        /// </summary>
        public void RegisterHandler(byte messageType, Action<PeerId, NetReader> handler)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));
            RequireDispatchable(messageType, nameof(messageType));
            if (_handlers[messageType] != null)
                throw new InvalidOperationException($"A handler for {MessageType.GetName(messageType)} is already registered.");
            _handlers[messageType] = handler;
        }

        public bool UnregisterHandler(byte messageType)
        {
            bool had = _handlers[messageType] != null;
            _handlers[messageType] = null;
            return had;
        }

        /// <summary>Sends to the host (delivered locally on the next Update when we are the host). False if not active.</summary>
        public bool SendToHost(Channel channel, Reliability reliability, ArraySegment<byte> packet)
        {
            ValidateOutgoing(channel, reliability, packet);
            return _state == SessionState.Active && SendValidated(_hostId, channel, reliability, packet);
        }

        public bool SendToHost(Channel channel, Reliability reliability, NetWriter packet)
            => SendToHost(channel, reliability, SegmentOf(packet));

        /// <summary>
        /// Sends a packet ([type][payload], type outside the session range) to one peer. On the host the target
        /// must be a handshaken client; a client may only target the host. Sending to the local id delivers locally
        /// on the next Update. Returns false if the session is not active or the target is not reachable.
        /// </summary>
        public bool SendTo(PeerId peer, Channel channel, Reliability reliability, ArraySegment<byte> packet)
        {
            ValidateOutgoing(channel, reliability, packet);
            return _state == SessionState.Active && SendValidated(peer, channel, reliability, packet);
        }

        public bool SendTo(PeerId peer, Channel channel, Reliability reliability, NetWriter packet)
            => SendTo(peer, channel, reliability, SegmentOf(packet));

        /// <summary>
        /// Host only: sends to every handshaken client except <paramref name="except"/> (never to the host itself).
        /// Returns the number of recipients; 0 when not active.
        /// </summary>
        /// <exception cref="InvalidOperationException">Called on a client; clients route through the host.</exception>
        public int Broadcast(Channel channel, Reliability reliability, ArraySegment<byte> packet, PeerId except = default)
        {
            ValidateOutgoing(channel, reliability, packet);
            if (_state != SessionState.Active)
                return 0;
            if (_role != SessionRole.Host)
                throw new InvalidOperationException("Only the host can broadcast; clients send to the host, which relays.");

            int sent = 0;
            foreach (var kv in _peers)
            {
                if (kv.Key == except)
                    continue;
                _transport.Send(kv.Key, channel, reliability, packet);
                sent++;
            }
            return sent;
        }

        public int Broadcast(Channel channel, Reliability reliability, NetWriter packet, PeerId except = default)
            => Broadcast(channel, reliability, SegmentOf(packet), except);

        void ISessionMessaging.SendTo(PeerId peer, Channel channel, Reliability reliability, ArraySegment<byte> data)
            => SendTo(peer, channel, reliability, data);

        void ISessionMessaging.Broadcast(Channel channel, Reliability reliability, ArraySegment<byte> data, PeerId except)
        {
            if (_role != SessionRole.Client)
                Broadcast(channel, reliability, data, except);
            else if (except != _hostId)
                SendToHost(channel, reliability, data);
        }

        void ISessionMessaging.UnregisterHandler(byte messageType) => UnregisterHandler(messageType);

        public void Dispose()
        {
            if (_disposed)
                return;
            Leave();
            _disposed = true;
        }

        // ---- Packet handling -------------------------------------------------------------------------

        private void HandlePacket(IncomingPacket packet)
        {
            var data = packet.Data;
            if (data == null || data.Length == 0 || data.Length > ProtocolInfo.MaxPacketSize
                || !packet.From.IsValid || packet.From == LocalId)
            {
                if (NetLog.IsEnabled(LogLevel.Debug))
                    NetLog.Debug($"Session: dropped empty, oversized or self-addressed packet from {packet.From}.");
                return;
            }

            byte type = data[0];
            try
            {
                if (_role == SessionRole.Host)
                    HandleHostPacket(type, packet);
                else
                    HandleClientPacket(type, packet);
            }
            catch (ProtocolException ex)
            {
                NetLog.Warn($"Session: dropped malformed {MessageType.GetName(type)} from {packet.From}: {ex.Message}");
            }
        }

        private void HandleHostPacket(byte type, IncomingPacket packet)
        {
            var from = packet.From;
            _peers.TryGetValue(from, out var remote);

            if (type == MessageType.Hello)
            {
                _reader.SetSource(packet.Data, 1, packet.Data.Length - 1);
                OnHello(from, remote, _reader);
                return;
            }

            if (remote == null)
            {
                if (NetLog.IsEnabled(LogLevel.Debug))
                    NetLog.Debug($"Session: ignored {MessageType.GetName(type)} from {from}, which has not completed the handshake.");
                return;
            }

            remote.LastHeard = _now;
            if (!MessageType.IsSession(type))
            {
                Dispatch(from, packet.Data);
                return;
            }

            _reader.SetSource(packet.Data, 1, packet.Data.Length - 1);
            switch (type)
            {
                case MessageType.Ping:
                    ReplyPong(from, _reader);
                    break;
                case MessageType.Pong:
                {
                    double sent = _reader.ReadDouble();
                    double remoteTime = _reader.ReadDouble();
                    if (remote.Clock.AddSample(sent, remoteTime, _now))
                        remote.Player.PingMs = remote.Clock.RttMs;
                    break;
                }
                case MessageType.Goodbye:
                    NetLog.Info($"Session: {remote.Player} left.");
                    RemovePeer(remote, PlayerLeaveReason.Left, disconnect: true);
                    break;
                default:
                    if (NetLog.IsEnabled(LogLevel.Debug))
                        NetLog.Debug($"Session: host ignored {MessageType.GetName(type)} from {from}.");
                    break;
            }
        }

        private void HandleClientPacket(byte type, IncomingPacket packet)
        {
            if (packet.From != _hostId)
            {
                if (NetLog.IsEnabled(LogLevel.Debug))
                    NetLog.Debug($"Session: ignored {MessageType.GetName(type)} from non-host {packet.From}.");
                return;
            }
            _hostLastHeard = _now;

            if (!MessageType.IsSession(type))
            {
                if (_state == SessionState.Active)
                    Dispatch(packet.From, packet.Data);
                else
                    BufferEarlyPacket(packet);
                return;
            }

            _reader.SetSource(packet.Data, 1, packet.Data.Length - 1);
            switch (type)
            {
                case MessageType.Welcome:
                    if (_state == SessionState.Connecting)
                        OnWelcome(_reader);
                    break;
                case MessageType.Reject:
                    if (_state == SessionState.Connecting)
                        OnReject(_reader);
                    break;
                case MessageType.SessionEnded:
                    OnSessionEndedMessage(_reader);
                    break;
                case MessageType.Goodbye:
                    EndSession(new SessionEndInfo(SessionEndReason.HostLeft, RejectReason.Unknown, "The host left the session."));
                    break;
                case MessageType.RosterUpdate:
                    if (_state == SessionState.Active)
                        OnRosterUpdate(_reader);
                    break;
                case MessageType.Ping:
                    if (_state == SessionState.Active)
                        ReplyPong(packet.From, _reader);
                    break;
                case MessageType.Pong:
                    if (_state == SessionState.Active)
                        OnClientPong(_reader);
                    break;
                default:
                    if (NetLog.IsEnabled(LogLevel.Debug))
                        NetLog.Debug($"Session: client ignored {MessageType.GetName(type)} from host.");
                    break;
            }
        }

        private void Dispatch(PeerId from, byte[] data)
        {
            byte type = data[0];
            var handler = _handlers[type];
            if (handler == null || !MessageType.IsAssignedRange(type))
            {
                if (NetLog.IsEnabled(LogLevel.Debug))
                    NetLog.Debug($"Session: no handler for {MessageType.GetName(type)} from {from}; dropped.");
                return;
            }

            _dispatchReader.SetSource(data, 1, data.Length - 1);
            try
            {
                handler(from, _dispatchReader);
            }
            catch (ProtocolException ex)
            {
                NetLog.Warn($"Session: dropped malformed {MessageType.GetName(type)} from {from}: {ex.Message}");
            }
            catch (Exception ex)
            {
                NetLog.Error($"Session: handler for {MessageType.GetName(type)} from {from} threw: {ex}");
            }
        }

        // ---- Host side -------------------------------------------------------------------------------

        private void OnHello(PeerId from, RemotePeer existing, NetReader r)
        {
            int protocol = r.ReadInt();
            if (protocol != ProtocolInfo.Version)
            {
                string theirMod = SessionWire.TryReadString(r, SessionWire.MaxVersionBytes);
                SendReject(from, RejectReason.ProtocolMismatch,
                    $"Protocol mismatch: host runs protocol {ProtocolInfo.Version} (mod {ProtocolInfo.ModVersion}), "
                    + $"you run protocol {protocol} (mod {theirMod}). Install the same mod version as the host.");
                return;
            }

            string modVersion = r.ReadString(SessionWire.MaxVersionBytes);
            if (modVersion != ProtocolInfo.ModVersion)
            {
                SendReject(from, RejectReason.ModVersionMismatch,
                    $"Mod version mismatch: host has {ProtocolInfo.ModVersion}, you have {modVersion}.");
                return;
            }

            string gameBuild = r.ReadString(SessionConfig.MaxGameBuildBytes);
            string displayName = SessionWire.SanitizeDisplayName(r.ReadString(SessionWire.MaxNameBytes));
            uint nonce = r.ReadUInt();

            if (existing != null)
            {
                if (existing.ConnectNonce == nonce)
                {
                    // Hello retry that crossed our Welcome.
                    existing.LastHeard = _now;
                    SendWelcome(existing.Player);
                    return;
                }
                // A new Connect from a peer we still list (it restarted, or gave up on us before we timed it out).
                // Its new session knows nothing of the old one, so the old player leaves and this one joins afresh.
                NetLog.Info($"Session: {existing.Player} reconnected; replacing its previous session.");
                int gen = _generation;
                RemovePeer(existing, PlayerLeaveReason.Left, disconnect: false);
                if (gen != _generation || _state != SessionState.Active)
                    return;
            }

            string hostBuild = _config.GameBuild ?? string.Empty;
            if (_config.RequireMatchingGameBuild && gameBuild != hostBuild)
            {
                SendReject(from, RejectReason.GameBuildMismatch,
                    $"Game version mismatch: host has '{hostBuild}', you have '{gameBuild}'.");
                return;
            }
            if (_banned.Contains(from) || !Admit(from))
            {
                SendReject(from, RejectReason.NotAllowed, "You are not allowed to join this session.");
                return;
            }
            int index = _players.Count < _config.MaxPlayers ? FindFreeIndex() : -1;
            if (index < 0)
            {
                SendReject(from, RejectReason.SessionFull, $"The session is full ({_config.MaxPlayers} players).");
                return;
            }

            var player = new PlayerInfo(from, index, string.IsNullOrEmpty(displayName) ? DefaultName(index) : displayName,
                isHost: false, isLocal: false);
            var remote = new RemotePeer(player, new NetClock(_config.RttSmoothing, _config.OffsetSmoothing))
            {
                ConnectNonce = nonce,
                LastHeard = _now,
                NextPingAt = _now
            };
            _peers.Add(from, remote);
            InsertSorted(player);
            _rosterDirty = true;

            SendWelcome(player);
            NetLog.Info($"Session: {player} joined.");
            RaiseJoined(player);
        }

        private bool Admit(PeerId peer)
        {
            var filter = AdmissionFilter;
            if (filter == null)
                return true;
            try
            {
                return filter(peer);
            }
            catch (Exception ex)
            {
                NetLog.Error($"Session: admission filter threw for {peer}; refusing. {ex}");
                return false;
            }
        }

        private int FindFreeIndex()
        {
            int max = Math.Min(_config.MaxPlayers, SessionConfig.AbsoluteMaxPlayers);
            // _players is sorted by index, so the first gap is the lowest free index.
            int expected = 0;
            for (int i = 0; i < _players.Count; i++)
            {
                if (_players[i].PlayerIndex != expected)
                    break;
                expected++;
            }
            return expected < max ? expected : -1;
        }

        private void RemovePeer(RemotePeer remote, PlayerLeaveReason reason, bool disconnect)
        {
            var id = remote.Player.Id;
            if (!_peers.Remove(id))
                return;
            _players.Remove(remote.Player);
            _rosterDirty = true;
            if (disconnect)
                _transport.Disconnect(id);
            RaiseLeft(remote.Player, reason);
        }

        private void TickHost(int gen)
        {
            double timeout = _config.TimeoutSeconds;
            _scratchPeers.Clear();
            foreach (var kv in _peers)
            {
                if (_now - kv.Value.LastHeard > timeout)
                    _scratchPeers.Add(kv.Key);
            }
            for (int i = 0; i < _scratchPeers.Count; i++)
            {
                if (gen != _generation)
                    return;
                if (_peers.TryGetValue(_scratchPeers[i], out var remote))
                {
                    NetLog.Info($"Session: {remote.Player} timed out.");
                    RemovePeer(remote, PlayerLeaveReason.TimedOut, disconnect: true);
                }
            }
            if (gen != _generation)
                return;

            if (_rosterDirty || (_peers.Count > 0 && _now >= _nextRosterRefreshAt))
                FlushRoster();
        }

        private void FlushRoster()
        {
            _rosterDirty = false;
            _nextRosterRefreshAt = _now + _config.RosterRefreshIntervalSeconds;
            if (_peers.Count == 0)
                return;
            PacketBuilder.Begin(_writer, MessageType.RosterUpdate);
            SessionWire.WriteRoster(_writer, _players);
            foreach (var kv in _peers)
                SendControl(kv.Key, Reliability.Reliable);
        }

        private void SendWelcome(PlayerInfo player)
        {
            PacketBuilder.Begin(_writer, MessageType.Welcome);
            _writer.WriteByte((byte)player.PlayerIndex);
            SessionWire.WriteRoster(_writer, _players);
            SendControl(player.Id, Reliability.Reliable);
        }

        private void SendReject(PeerId to, RejectReason reason, string message)
        {
            NetLog.Info($"Session: rejected {to} ({reason}): {message}");
            PacketBuilder.Begin(_writer, MessageType.Reject);
            _writer.WriteByte((byte)reason);
            _writer.WriteInt(ProtocolInfo.Version);
            _writer.WriteString(ProtocolInfo.ModVersion);
            _writer.WriteString(Truncate(message, SessionWire.MaxMessageChars));
            SendControl(to, Reliability.Reliable);
        }

        // ---- Client side -----------------------------------------------------------------------------

        private void SendHello()
        {
            PacketBuilder.Begin(_writer, MessageType.Hello);
            _writer.WriteInt(ProtocolInfo.Version);
            _writer.WriteString(ProtocolInfo.ModVersion);
            _writer.WriteString(_config.GameBuild ?? string.Empty);
            _writer.WriteString(SessionWire.SanitizeDisplayName(_config.DisplayName));
            _writer.WriteUInt(_connectNonce);
            SendControl(_hostId, Reliability.Reliable);
        }

        private void OnWelcome(NetReader r)
        {
            int localIndex = r.ReadByte();
            SessionWire.ReadRoster(r, _rosterEntries);

            bool hasHost = false;
            bool hasLocal = false;
            for (int i = 0; i < _rosterEntries.Count; i++)
            {
                var e = _rosterEntries[i];
                if (e.Id == _hostId)
                    hasHost = true;
                else if (e.Id == LocalId)
                    hasLocal = e.Index == localIndex;
            }
            if (!hasHost || !hasLocal)
                throw new ProtocolException("Welcome roster does not list both the host and us at the assigned index.");

            for (int i = 0; i < _rosterEntries.Count; i++)
            {
                var e = _rosterEntries[i];
                var player = new PlayerInfo(e.Id, e.Index, NameOrDefault(e.Name, e.Index), e.Id == _hostId, e.Id == LocalId);
                player.PingMs = e.PingMs;
                if (player.IsLocal)
                    _localPlayer = player;
                InsertSorted(player);
            }

            _state = SessionState.Active;
            _hostLastHeard = _now;
            _nextHostPingAt = _now;
            NetLog.Info($"Session: joined host {_hostId} as {_localPlayer} ({_players.Count} players).");

            int gen = _generation;
            RaiseSessionStarted();
            _joinedScratch.Clear();
            for (int i = 0; i < _players.Count; i++)
            {
                if (!_players[i].IsLocal)
                    _joinedScratch.Add(_players[i]);
            }
            for (int i = 0; i < _joinedScratch.Count && gen == _generation; i++)
                RaiseJoined(_joinedScratch[i]);
            _joinedScratch.Clear();

            int count = _buffered.Count;
            for (int i = 0; i < count && i < _buffered.Count && gen == _generation; i++)
                Dispatch(_buffered[i].From, _buffered[i].Data);
            if (gen == _generation)
            {
                _buffered.Clear();
                _bufferedBytes = 0;
            }
        }

        private void OnReject(NetReader r)
        {
            byte rawReason = r.ReadByte();
            var reason = Enum.IsDefined(typeof(RejectReason), rawReason) ? (RejectReason)rawReason : RejectReason.Unknown;
            int hostProtocol = r.ReadInt();
            string hostMod = r.ReadString(SessionWire.MaxVersionBytes);
            string message = SessionWire.TryReadString(r, SessionWire.MaxMessageBytes);

            if (string.IsNullOrEmpty(message) || message == "?")
            {
                switch (reason)
                {
                    case RejectReason.ProtocolMismatch:
                    case RejectReason.ModVersionMismatch:
                        message = $"Version mismatch: host has mod {hostMod} (protocol {hostProtocol}), "
                            + $"you have mod {ProtocolInfo.ModVersion} (protocol {ProtocolInfo.Version}).";
                        break;
                    case RejectReason.SessionFull:
                        message = "The session is full.";
                        break;
                    case RejectReason.NotAllowed:
                        message = "You are not allowed to join this session.";
                        break;
                    default:
                        message = "The host refused the connection.";
                        break;
                }
            }

            _transport.Disconnect(_hostId);
            EndSession(new SessionEndInfo(SessionEndReason.Rejected, reason, message));
        }

        private void OnSessionEndedMessage(NetReader r)
        {
            byte rawReason = r.ReadByte();
            string message = SessionWire.TryReadString(r, SessionWire.MaxMessageBytes);
            var reason = rawReason == (byte)SessionEndReason.Kicked ? SessionEndReason.Kicked : SessionEndReason.HostLeft;
            if (string.IsNullOrEmpty(message) || message == "?")
                message = reason == SessionEndReason.Kicked ? "Kicked by the host." : "The host ended the session.";
            _transport.Disconnect(_hostId);
            EndSession(new SessionEndInfo(reason, RejectReason.Unknown, message));
        }

        private void OnRosterUpdate(NetReader r)
        {
            SessionWire.ReadRoster(r, _rosterEntries);

            bool hasHost = false;
            bool hasLocal = false;
            for (int i = 0; i < _rosterEntries.Count; i++)
            {
                if (_rosterEntries[i].Id == _hostId) hasHost = true;
                else if (_rosterEntries[i].Id == LocalId) hasLocal = true;
            }
            if (!hasHost)
                throw new ProtocolException("Roster update does not list the host.");
            if (!hasLocal)
            {
                _transport.Disconnect(_hostId);
                EndSession(new SessionEndInfo(SessionEndReason.DroppedByHost, RejectReason.Unknown,
                    "The host dropped this player from the session."));
                return;
            }

            _leftScratch.Clear();
            for (int i = _players.Count - 1; i >= 0; i--)
            {
                var p = _players[i];
                if (IndexOfEntry(p.Id) < 0)
                {
                    _players.RemoveAt(i);
                    _leftScratch.Add(p);
                }
            }

            _joinedScratch.Clear();
            for (int i = 0; i < _rosterEntries.Count; i++)
            {
                var e = _rosterEntries[i];
                string name = NameOrDefault(e.Name, e.Index);
                if (TryGetPlayer(e.Id, out var existing))
                {
                    existing.DisplayName = name;
                    existing.PingMs = e.PingMs;
                    if (existing.PlayerIndex != e.Index)
                    {
                        _players.Remove(existing);
                        existing.PlayerIndex = e.Index;
                        InsertSorted(existing);
                    }
                }
                else
                {
                    var player = new PlayerInfo(e.Id, e.Index, name, isHost: false, isLocal: false);
                    player.PingMs = e.PingMs;
                    InsertSorted(player);
                    _joinedScratch.Add(player);
                }
            }
            ApplyOwnRtt();

            int gen = _generation;
            for (int i = 0; i < _leftScratch.Count && gen == _generation; i++)
                RaiseLeft(_leftScratch[i], PlayerLeaveReason.Unknown);
            for (int i = 0; i < _joinedScratch.Count && gen == _generation; i++)
                RaiseJoined(_joinedScratch[i]);
            _leftScratch.Clear();
            _joinedScratch.Clear();
        }

        private void OnClientPong(NetReader r)
        {
            double sent = r.ReadDouble();
            double hostTime = r.ReadDouble();
            if (_clock.AddSample(sent, hostTime, _now))
                ApplyOwnRtt();
        }

        private void ApplyOwnRtt()
        {
            if (!_clock.HasSamples)
                return;
            int ms = _clock.RttMs;
            if (_localPlayer != null)
                _localPlayer.PingMs = ms;
            if (TryGetPlayer(_hostId, out var host))
                host.PingMs = ms;
        }

        private void BufferEarlyPacket(IncomingPacket packet)
        {
            if (_buffered.Count >= MaxBufferedPackets || _bufferedBytes + packet.Data.Length > MaxBufferedBytes)
            {
                NetLog.Warn($"Session: pre-Welcome buffer full; dropped {MessageType.GetName(packet.Data[0])} from host.");
                return;
            }
            _buffered.Add(packet);
            _bufferedBytes += packet.Data.Length;
        }

        private void TickClient()
        {
            if (_state == SessionState.Connecting)
            {
                if (_now - _connectStartedAt > _config.ConnectTimeoutSeconds)
                {
                    _transport.Disconnect(_hostId);
                    EndSession(new SessionEndInfo(SessionEndReason.ConnectTimedOut, RejectReason.Unknown,
                        "The host did not answer."));
                    return;
                }
                if (_now >= _nextHelloAt)
                {
                    SendHello();
                    _nextHelloAt = _now + _config.HelloRetryIntervalSeconds;
                }
                return;
            }

            if (_now - _hostLastHeard > _config.TimeoutSeconds)
            {
                NetLog.Info($"Session: host {_hostId} timed out.");
                _transport.Disconnect(_hostId);
                EndSession(new SessionEndInfo(SessionEndReason.HostTimedOut, RejectReason.Unknown, "Lost connection to the host."));
            }
        }

        // ---- Shared ----------------------------------------------------------------------------------

        private void SendDuePings()
        {
            double interval = _config.PingIntervalSeconds;
            if (_role == SessionRole.Client)
            {
                if (_now >= _nextHostPingAt)
                {
                    SendPing(_hostId);
                    // Entity sync waits for the first clock sample, so a lost early ping is retried quickly.
                    if (_clock.SampleCount < NetClock.WarmupSamples)
                        interval = Math.Min(interval, WarmupPingIntervalSeconds);
                    _nextHostPingAt = _now + interval;
                }
                return;
            }
            foreach (var kv in _peers)
            {
                var remote = kv.Value;
                if (_now < remote.NextPingAt)
                    continue;
                SendPing(kv.Key);
                remote.NextPingAt = _now + interval;
            }
        }

        private void SendPing(PeerId to)
        {
            PacketBuilder.Begin(_writer, MessageType.Ping);
            _writer.WriteDouble(_now);
            SendControl(to, Reliability.Unreliable);
        }

        private void ReplyPong(PeerId to, NetReader r)
        {
            double sent = r.ReadDouble();
            PacketBuilder.Begin(_writer, MessageType.Pong);
            _writer.WriteDouble(sent);
            _writer.WriteDouble(_now);
            SendControl(to, Reliability.Unreliable);
        }

        private void BuildSessionEnded(SessionEndReason reason, string message)
        {
            PacketBuilder.Begin(_writer, MessageType.SessionEnded);
            _writer.WriteByte((byte)reason);
            _writer.WriteString(Truncate(message, SessionWire.MaxMessageChars));
        }

        private void SendControl(PeerId to, Reliability reliability)
        {
            _transport.Send(to, Channel.Control, reliability, _writer.AsSegment());
        }

        private bool SendValidated(PeerId peer, Channel channel, Reliability reliability, ArraySegment<byte> packet)
        {
            if (peer == LocalId)
                return EnqueueLocal(packet);
            if (_role == SessionRole.Host)
            {
                if (!_peers.ContainsKey(peer))
                    return false;
            }
            else if (peer != _hostId)
            {
                NetLog.Warn($"Session: clients can only send to the host; dropped {MessageType.GetName(packet.Array[packet.Offset])} for {peer}.");
                return false;
            }
            _transport.Send(peer, channel, reliability, packet);
            return true;
        }

        private bool EnqueueLocal(ArraySegment<byte> packet)
        {
            if (_localQueue.Count >= MaxLocalQueue)
            {
                NetLog.Warn($"Session: local delivery queue full; dropped {MessageType.GetName(packet.Array[packet.Offset])}.");
                return false;
            }
            var copy = new byte[packet.Count];
            Buffer.BlockCopy(packet.Array, packet.Offset, copy, 0, packet.Count);
            _localQueue.Enqueue(copy);
            return true;
        }

        private void ProcessLocalQueue(int gen)
        {
            // Only what was queued before this Update; handlers that send to self are served next time.
            int count = _localQueue.Count;
            for (int i = 0; i < count && _localQueue.Count > 0 && gen == _generation; i++)
                Dispatch(LocalId, _localQueue.Dequeue());
        }

        private void InitTimers()
        {
            _timersPending = false;
            if (_role == SessionRole.Client && _state == SessionState.Connecting)
            {
                _connectStartedAt = _now;
                _nextHelloAt = _now + _config.HelloRetryIntervalSeconds;
            }
            else if (_role == SessionRole.Host)
            {
                _nextRosterRefreshAt = _now + _config.RosterRefreshIntervalSeconds;
            }
        }

        private void EndSession(SessionEndInfo info)
        {
            if (_state == SessionState.Idle)
                return;
            var role = _role;
            ResetSessionState();
            _generation++;
            NetLog.Info($"Session: {(role == SessionRole.Host ? "hosted session" : "session")} ended: {info}");
            int gen = _generation;
            RaiseClosed();
            if (gen == _generation)
                Raise(SessionEnded, info);
        }

        private void ResetSessionState()
        {
            _role = SessionRole.None;
            _state = SessionState.Idle;
            _hostId = PeerId.None;
            _localPlayer = null;
            _players.Clear();
            _peers.Clear();
            _banned.Clear();
            _buffered.Clear();
            _bufferedBytes = 0;
            _localQueue.Clear();
            _rosterDirty = false;
            _timersPending = false;
            _clock.Reset();
        }

        private void InsertSorted(PlayerInfo player)
        {
            int i = _players.Count;
            while (i > 0 && _players[i - 1].PlayerIndex > player.PlayerIndex)
                i--;
            _players.Insert(i, player);
        }

        private int IndexOfEntry(PeerId id)
        {
            for (int i = 0; i < _rosterEntries.Count; i++)
            {
                if (_rosterEntries[i].Id == id)
                    return i;
            }
            return -1;
        }

        private string LocalDisplayName(int index) => NameOrDefault(SessionWire.SanitizeDisplayName(_config.DisplayName), index);

        private static string NameOrDefault(string name, int index) => string.IsNullOrEmpty(name) ? DefaultName(index) : name;

        private static string DefaultName(int index) => "Player " + (index + 1);

        private static uint NewConnectNonce(uint previous)
        {
            // Random across process restarts too: a restarted game must not repeat the nonce of its last run.
            uint nonce;
            do
            {
                nonce = BitConverter.ToUInt32(Guid.NewGuid().ToByteArray(), 0);
            }
            while (nonce == previous);
            return nonce;
        }

        private static string Truncate(string s, int maxChars)
            => s == null || s.Length <= maxChars ? s : s.Substring(0, char.IsHighSurrogate(s[maxChars - 1]) ? maxChars - 1 : maxChars);

        private static ArraySegment<byte> SegmentOf(NetWriter writer)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));
            return writer.AsSegment();
        }

        private static void RequireDispatchable(byte messageType, string paramName)
        {
            if (!MessageType.IsAssignedRange(messageType) || MessageType.IsSession(messageType))
                throw new ArgumentOutOfRangeException(paramName, messageType,
                    $"Message type must be in {MessageType.SaveFirst}..{MessageType.GameLast}; the session range is reserved.");
        }

        private static void ValidateOutgoing(Channel channel, Reliability reliability, ArraySegment<byte> packet)
        {
            if (packet.Array == null || packet.Count == 0)
                throw new ArgumentException("Packet must contain at least the message type byte.", nameof(packet));
            RequireDispatchable(packet.Array[packet.Offset], nameof(packet));
            if (!ChannelInfo.IsDefined(channel))
                throw new ArgumentOutOfRangeException(nameof(channel), channel, "Undefined channel.");
            if (reliability != Reliability.Reliable && reliability != Reliability.Unreliable)
                throw new ArgumentOutOfRangeException(nameof(reliability), reliability, "Undefined reliability.");
            if (packet.Count > ProtocolInfo.MaxPacketSize)
                throw new ArgumentException($"Packet of {packet.Count} bytes exceeds {ProtocolInfo.MaxPacketSize}.", nameof(packet));
            if (reliability == Reliability.Unreliable && packet.Count > ProtocolInfo.MaxUnreliablePacketSize)
                throw new ArgumentException(
                    $"Unreliable packet of {packet.Count} bytes exceeds {ProtocolInfo.MaxUnreliablePacketSize}; send it reliably.", nameof(packet));
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SessionManager));
        }

        private void RaiseSessionStarted()
        {
            var handler = SessionStarted;
            if (handler == null)
                return;
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                NetLog.Error($"Session: SessionStarted handler threw: {ex}");
            }
        }

        private void RaiseJoined(PlayerInfo player)
        {
            int gen = _generation;
            Raise(_peerJoined, player.Id);
            if (gen == _generation)
                Raise(PlayerJoined, player);
        }

        private void RaiseLeft(PlayerInfo player, PlayerLeaveReason reason)
        {
            int gen = _generation;
            Raise(_peerLeft, player.Id);
            if (gen == _generation)
                Raise(PlayerLeft, player, reason);
        }

        private void RaiseClosed()
        {
            var handler = _sessionClosed;
            if (handler == null)
                return;
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                NetLog.Error($"Session: SessionClosed handler threw: {ex}");
            }
        }

        private static void Raise<T>(Action<T> handler, T arg)
        {
            if (handler == null)
                return;
            try
            {
                handler(arg);
            }
            catch (Exception ex)
            {
                NetLog.Error($"Session: event handler threw: {ex}");
            }
        }

        private static void Raise<T1, T2>(Action<T1, T2> handler, T1 arg1, T2 arg2)
        {
            if (handler == null)
                return;
            try
            {
                handler(arg1, arg2);
            }
            catch (Exception ex)
            {
                NetLog.Error($"Session: event handler threw: {ex}");
            }
        }

        private sealed class RemotePeer
        {
            public RemotePeer(PlayerInfo player, NetClock clock)
            {
                Player = player;
                Clock = clock;
            }

            public readonly PlayerInfo Player;
            public readonly NetClock Clock;
            public uint ConnectNonce;
            public double LastHeard;
            public double NextPingAt;
        }
    }
}
