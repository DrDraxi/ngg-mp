using System;
using System.Collections.Generic;
using NGGMultiplayer.Core.Diagnostics;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Session;

namespace NGGMultiplayer.Core.Sync
{
    /// <summary>
    /// Host-side check of a client's RPC before it is delivered anywhere. <paramref name="args"/> is positioned at the
    /// payload; the handler later gets a fresh reader. Return false to veto.
    /// </summary>
    public delegate bool RpcValidator(PeerId sender, RpcTarget target, PeerId targetPeer, NetReader args);

    /// <summary>
    /// Named remote procedure calls over the session star. Ids are the FNV-1a 32-bit hash of the UTF-8 name.
    /// <list type="bullet">
    /// <item>Clients send every RPC to the host (<see cref="MessageType.Rpc"/>). The host runs the RPC's validator,
    /// then invokes it locally and/or relays it (<see cref="MessageType.RpcRelay"/>) with the sender's id stamped
    /// from the transport, so a client cannot impersonate anyone. Clients accept RPCs from the host only.</item>
    /// <item>On the host, RPCs addressed to itself run synchronously inside <see cref="Send"/>, after remote copies
    /// are sent. A client's own copy of <see cref="RpcTarget.All"/> comes back through the host, so the sender sees
    /// exactly what the host accepted.</item>
    /// </list>
    /// Handlers get a reader over the payload that is valid only during the call. A handler that throws
    /// <see cref="ProtocolException"/> is logged as a malformed RPC; any other exception is logged too.
    /// Not thread-safe.
    /// </summary>
    public sealed class RpcRouter : IDisposable
    {
        private const string Log = "[Rpc] ";
        private const byte TargetMask = 0x03;
        private const byte UnreliableBit = 0x80;
        private const int MaxLocalDepth = 8;

        private static readonly Action<NetWriter, Action<NetWriter>> InvokeWriter = (w, write) => write?.Invoke(w);

        private readonly ISessionMessaging _messaging;
        private readonly bool _isHost;
        private readonly PeerId _localId;
        private readonly PeerId _hostId;
        private readonly Dictionary<uint, Registration> _byId = new Dictionary<uint, Registration>();
        private readonly NetWriter _relayWriter = new NetWriter(ProtocolInfo.MaxUnreliablePacketSize);
        private readonly NetReader[] _readers = new NetReader[MaxLocalDepth];
        private readonly Action<PeerId, NetReader> _onRpc;
        private readonly Action<PeerId, NetReader> _onRelay;
        private int _depth;
        private bool _closed;
        private bool _disposed;

        public RpcRouter(ISessionMessaging messaging)
        {
            _messaging = messaging ?? throw new ArgumentNullException(nameof(messaging));
            _isHost = messaging.IsHost;
            _localId = messaging.LocalId;
            _hostId = messaging.HostId;
            if (!_localId.IsValid || !_hostId.IsValid)
                throw new ArgumentException("The session must have valid local and host ids.", nameof(messaging));
            _onRpc = OnRpc;
            _onRelay = OnRpcRelay;
            _messaging.RegisterHandler(MessageType.Rpc, _onRpc);
            _messaging.RegisterHandler(MessageType.RpcRelay, _onRelay);
            _messaging.SessionClosed += OnSessionClosed;
        }

        public bool IsHost => _isHost;

        /// <summary>
        /// Host: relay client RPCs the host has not registered (they cannot be validated). Off by default; the
        /// host must register every RPC clients exchange, even with a no-op handler.
        /// </summary>
        public bool AllowUnregisteredRelay { get; set; }

        public long Sent { get; private set; }

        /// <summary>Handler invocations, local ones included.</summary>
        public long Invoked { get; private set; }

        public long Relayed { get; private set; }

        public long Rejected { get; private set; }

        /// <summary>Host: a client RPC was dropped (sender, rpc id, reason).</summary>
        public event Action<PeerId, uint, string> RpcRejected;

        /// <summary>Stable 32-bit FNV-1a hash of the UTF-8 bytes of <paramref name="name"/>. Allocation-free.</summary>
        public static uint GetId(string name)
        {
            if (name == null)
                throw new ArgumentNullException(nameof(name));
            uint h = 2166136261;
            for (int i = 0; i < name.Length; i++)
            {
                int c = name[i];
                if (c < 0x80)
                {
                    h = Step(h, c);
                }
                else if (c < 0x800)
                {
                    h = Step(h, 0xC0 | (c >> 6));
                    h = Step(h, 0x80 | (c & 0x3F));
                }
                else if (char.IsHighSurrogate((char)c) && i + 1 < name.Length && char.IsLowSurrogate(name[i + 1]))
                {
                    int cp = char.ConvertToUtf32((char)c, name[++i]);
                    h = Step(h, 0xF0 | (cp >> 18));
                    h = Step(h, 0x80 | ((cp >> 12) & 0x3F));
                    h = Step(h, 0x80 | ((cp >> 6) & 0x3F));
                    h = Step(h, 0x80 | (cp & 0x3F));
                }
                else
                {
                    // Lone surrogates encode as U+FFFD, matching Encoding.UTF8.
                    if (char.IsSurrogate((char)c))
                        c = 0xFFFD;
                    h = Step(h, 0xE0 | (c >> 12));
                    h = Step(h, 0x80 | ((c >> 6) & 0x3F));
                    h = Step(h, 0x80 | (c & 0x3F));
                }
            }
            return h;
        }

        /// <param name="hostValidator">Runs on the host for every client-originated call of this RPC, whatever its target.</param>
        /// <exception cref="InvalidOperationException">The name is registered, or its id collides with another name.</exception>
        public void Register(string name, Action<PeerId, NetReader> handler, RpcValidator hostValidator = null)
        {
            RequireNotDisposed();
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("RPC name must not be empty.", nameof(name));
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));
            uint id = GetId(name);
            if (_byId.TryGetValue(id, out var existing))
            {
                throw new InvalidOperationException(existing.Name == name
                    ? $"RPC '{name}' is already registered."
                    : $"RPC '{name}' collides with '{existing.Name}' (id 0x{id:X8}); rename one of them.");
            }
            _byId.Add(id, new Registration(name, handler, hostValidator));
        }

        public bool Unregister(string name)
        {
            if (name == null)
                throw new ArgumentNullException(nameof(name));
            uint id = GetId(name);
            if (!_byId.TryGetValue(id, out var reg) || reg.Name != name)
                return false;
            return _byId.Remove(id);
        }

        public bool IsRegistered(string name) =>
            name != null && _byId.TryGetValue(GetId(name), out var reg) && reg.Name == name;

        /// <summary>
        /// Sends an RPC. <paramref name="peer"/> is required for <see cref="RpcTarget.Peer"/> and ignored otherwise.
        /// Returns false if the session closed, the payload is too large, or the target peer is not connected (host).
        /// </summary>
        public bool Send(string name, RpcTarget target, Action<NetWriter> write = null,
            Reliability reliability = Reliability.Reliable, PeerId peer = default) =>
            SendCore(name, target, peer, reliability, write, InvokeWriter);

        /// <summary>Closure-free variant: <paramref name="write"/> receives <paramref name="arg"/>.</summary>
        public bool Send<T>(string name, RpcTarget target, T arg, Action<NetWriter, T> write,
            Reliability reliability = Reliability.Reliable, PeerId peer = default) =>
            SendCore(name, target, peer, reliability, arg, write ?? throw new ArgumentNullException(nameof(write)));

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _messaging.SessionClosed -= OnSessionClosed;
            _messaging.UnregisterHandler(MessageType.Rpc);
            _messaging.UnregisterHandler(MessageType.RpcRelay);
            _byId.Clear();
        }

        private bool SendCore<T>(string name, RpcTarget target, PeerId peer, Reliability reliability, T arg, Action<NetWriter, T> write)
        {
            RequireNotDisposed();
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("RPC name must not be empty.", nameof(name));
            if (target > RpcTarget.Peer)
                throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown RPC target.");
            if (reliability != Reliability.Reliable && reliability != Reliability.Unreliable)
                throw new ArgumentOutOfRangeException(nameof(reliability), reliability, "Unknown reliability.");
            if (target == RpcTarget.Peer && !peer.IsValid)
                throw new ArgumentException("RpcTarget.Peer needs a valid peer.", nameof(peer));
            if (target != RpcTarget.Peer)
                peer = PeerId.None;
            if (_closed)
                return false;

            uint id = GetId(name);
            using (var packet = PacketBuilder.Rent(_isHost ? MessageType.RpcRelay : MessageType.Rpc))
            {
                var w = packet.Writer;
                w.WriteUInt(id);
                if (_isHost)
                {
                    w.WritePeerId(_localId);
                }
                else
                {
                    w.WriteByte(EncodeTarget(target, reliability));
                    if (target == RpcTarget.Peer)
                        w.WritePeerId(peer);
                }
                int payloadStart = w.Length;
                write(w, arg);

                var whole = packet.Segment;
                var payload = new ArraySegment<byte>(whole.Array, whole.Offset + payloadStart, whole.Count - payloadStart);
                int max = MaxPayload(reliability);
                if (payload.Count > max)
                {
                    NetLog.Error($"{Log}RPC '{name}' payload is {payload.Count} bytes; the {reliability} limit is {max}. Not sent.");
                    return false;
                }

                if (_isHost)
                    return Deliver(id, _localId, target, peer, reliability, whole, payload);

                if (target == RpcTarget.Peer && peer == _localId)
                {
                    InvokeLocal(id, _localId, payload);
                    return true;
                }
                _messaging.SendTo(_hostId, SyncProtocol.EventsChannel, reliability, whole);
                Sent++;
                return true;
            }
        }

        /// <summary>
        /// Host: delivers an RPC from <paramref name="sender"/>. <paramref name="relayPacket"/> is the complete RpcRelay
        /// packet for remote recipients; remote copies go out before the local handler runs.
        /// </summary>
        private bool Deliver(uint id, PeerId sender, RpcTarget target, PeerId peer, Reliability reliability,
            ArraySegment<byte> relayPacket, ArraySegment<byte> payload)
        {
            bool fromRemote = sender != _localId;
            bool invokeLocal;
            switch (target)
            {
                case RpcTarget.Host:
                    invokeLocal = true;
                    break;
                case RpcTarget.All:
                    _messaging.Broadcast(SyncProtocol.EventsChannel, reliability, relayPacket, PeerId.None);
                    CountRemote(fromRemote);
                    invokeLocal = true;
                    break;
                case RpcTarget.Others:
                    _messaging.Broadcast(SyncProtocol.EventsChannel, reliability, relayPacket, fromRemote ? sender : PeerId.None);
                    CountRemote(fromRemote);
                    invokeLocal = fromRemote;
                    break;
                default:
                    if (peer == _localId)
                    {
                        invokeLocal = true;
                        break;
                    }
                    if (!_messaging.IsPeerConnected(peer))
                    {
                        NetLog.Warn($"{Log}Dropped RPC 0x{id:X8} from {sender}: target peer {peer} is not connected.");
                        return false;
                    }
                    _messaging.SendTo(peer, SyncProtocol.EventsChannel, reliability, relayPacket);
                    CountRemote(fromRemote);
                    invokeLocal = false;
                    break;
            }
            if (invokeLocal)
                InvokeLocal(id, sender, payload);
            return true;
        }

        private void CountRemote(bool relayed)
        {
            if (relayed)
                Relayed++;
            else
                Sent++;
        }

        private void OnRpc(PeerId from, NetReader reader)
        {
            if (_disposed || _closed)
                return;
            if (!_isHost)
            {
                NetLog.Warn($"{Log}Ignoring Rpc from {from}; clients only accept RpcRelay from the host.");
                return;
            }

            uint id;
            RpcTarget target;
            Reliability reliability;
            PeerId peer = PeerId.None;
            ArraySegment<byte> payload;
            try
            {
                id = reader.ReadUInt();
                byte raw = reader.ReadByte();
                if ((raw & ~(TargetMask | UnreliableBit)) != 0)
                    throw new ProtocolException($"Unknown RPC target bits 0x{raw:X2}.");
                target = (RpcTarget)(raw & TargetMask);
                reliability = (raw & UnreliableBit) != 0 ? Reliability.Unreliable : Reliability.Reliable;
                if (target == RpcTarget.Peer)
                {
                    peer = reader.ReadPeerId();
                    if (!peer.IsValid)
                        throw new ProtocolException("RPC target peer is not valid.");
                }
                payload = reader.ReadRemainingSegment();
                if (payload.Count > MaxPayload(reliability))
                    throw new ProtocolException($"RPC payload of {payload.Count} bytes exceeds the {reliability} limit.");
            }
            catch (ProtocolException e)
            {
                Reject(from, 0, "Malformed RPC: " + e.Message);
                return;
            }

            _byId.TryGetValue(id, out var reg);
            if (reg == null)
            {
                bool remoteOnly = target == RpcTarget.Others || (target == RpcTarget.Peer && peer != _localId);
                if (!AllowUnregisteredRelay || !remoteOnly)
                {
                    Reject(from, id, "Unregistered RPC.");
                    return;
                }
            }
            else if (reg.Validator != null && !RunValidator(reg, from, target, peer, payload))
            {
                Reject(from, id, $"Vetoed by the validator of '{reg.Name}'.");
                return;
            }

            // The host stamps the transport-level sender; nothing the client wrote can change it.
            PacketBuilder.Begin(_relayWriter, MessageType.RpcRelay);
            _relayWriter.WriteUInt(id);
            _relayWriter.WritePeerId(from);
            _relayWriter.WriteRaw(payload);
            Deliver(id, from, target, peer, reliability, _relayWriter.AsSegment(), payload);
        }

        private void OnRpcRelay(PeerId from, NetReader reader)
        {
            if (_disposed || _closed)
                return;
            if (_isHost || from != _hostId)
            {
                NetLog.Warn($"{Log}Ignoring RpcRelay from {from}; only the host relays RPCs.");
                return;
            }
            uint id;
            PeerId sender;
            ArraySegment<byte> payload;
            try
            {
                id = reader.ReadUInt();
                sender = reader.ReadPeerId();
                if (!sender.IsValid)
                    throw new ProtocolException("RPC sender is not valid.");
                payload = reader.ReadRemainingSegment();
            }
            catch (ProtocolException e)
            {
                NetLog.Warn($"{Log}Malformed RpcRelay from host: {e.Message}");
                return;
            }
            InvokeLocal(id, sender, payload);
        }

        private bool RunValidator(Registration reg, PeerId sender, RpcTarget target, PeerId peer, ArraySegment<byte> payload)
        {
            if (_depth >= MaxLocalDepth)
                return false;
            var reader = RentReader(payload);
            _depth++;
            try
            {
                return reg.Validator(sender, target, peer, reader);
            }
            catch (ProtocolException e)
            {
                NetLog.Warn($"{Log}Validator of '{reg.Name}' found a malformed payload from {sender}: {e.Message}");
                return false;
            }
            catch (Exception e)
            {
                SyncSafeInvoke.Fail($"Validator of RPC '{reg.Name}'", e);
                return false;
            }
            finally
            {
                _depth--;
            }
        }

        private void InvokeLocal(uint id, PeerId sender, ArraySegment<byte> payload)
        {
            if (!_byId.TryGetValue(id, out var reg))
            {
                if (NetLog.IsEnabled(LogLevel.Debug))
                    NetLog.Debug($"{Log}No handler for RPC 0x{id:X8} from {sender}.");
                return;
            }
            if (_depth >= MaxLocalDepth)
            {
                NetLog.Error($"{Log}RPC '{reg.Name}' dropped: local RPCs nested deeper than {MaxLocalDepth}.");
                return;
            }
            var reader = RentReader(payload);
            _depth++;
            try
            {
                Invoked++;
                reg.Handler(sender, reader);
            }
            catch (ProtocolException e)
            {
                NetLog.Warn($"{Log}Malformed '{reg.Name}' from {sender}: {e.Message}");
            }
            catch (Exception e)
            {
                SyncSafeInvoke.Fail($"Handler of RPC '{reg.Name}'", e);
            }
            finally
            {
                _depth--;
            }
        }

        private NetReader RentReader(ArraySegment<byte> payload)
        {
            var reader = _readers[_depth] ?? (_readers[_depth] = new NetReader());
            reader.SetSource(payload);
            return reader;
        }

        private void Reject(PeerId from, uint id, string reason)
        {
            Rejected++;
            NetLog.Warn($"{Log}Rejected RPC 0x{id:X8} from {from}: {reason}");
            SyncSafeInvoke.Raise(RpcRejected, from, id, reason, nameof(RpcRejected));
        }

        private void OnSessionClosed() => _closed = true;

        private void RequireNotDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(RpcRouter));
        }

        private static byte EncodeTarget(RpcTarget target, Reliability reliability) =>
            (byte)((byte)target | (reliability == Reliability.Unreliable ? UnreliableBit : 0));

        private static int MaxPayload(Reliability reliability) =>
            reliability == Reliability.Unreliable ? SyncProtocol.MaxUnreliableRpcPayload : SyncProtocol.MaxReliableRpcPayload;

        private static uint Step(uint h, int b) => (h ^ (uint)b) * 16777619;

        private sealed class Registration
        {
            public Registration(string name, Action<PeerId, NetReader> handler, RpcValidator validator)
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
