using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NGGMultiplayer.Core.Diagnostics;

namespace NGGMultiplayer.Core.Net
{
    /// <summary>
    /// <see cref="ITransport"/> over plain UDP, for play and tests without Steam (two game copies on one PC, or a LAN).
    /// Reliable messages are split into fragments of at most <see cref="FragmentSize"/> bytes, each with its own sequence
    /// number per (peer, channel), acknowledged one by one and resent until acknowledged; the receiver delivers them in
    /// order and joins the fragments again. Unreliable messages go as one datagram (larger ones go reliably).
    /// <para>Peers are addressed by <see cref="PeerId"/>; every datagram carries its sender's id, so a host learns a
    /// client's endpoint from its first datagram. A client finds the host's id with <see cref="Probe"/>.</para>
    /// <para>No encryption or authentication: for localhost and trusted LANs only. Not thread-safe: call from a single
    /// thread (the socket is non-blocking and read in <see cref="Poll"/>).</para>
    /// </summary>
    public sealed class DirectTransport : ITransport
    {
        /// <summary>Top 32 bits of every id <see cref="NewLocalId"/> makes (universe 0: never a Steam account).</summary>
        public const ulong IdPrefix = 0x00D1EC70UL;

        /// <summary>Largest payload of one reliable fragment (fits an Ethernet MTU with the headers).</summary>
        public const int FragmentSize = 1200;

        /// <summary>Unreliable payloads above this go reliably (one UDP datagram must hold them).</summary>
        public const int MaxUnreliablePayload = 60000;

        /// <summary>Unacknowledged fragments in flight per (peer, channel).</summary>
        public const int Window = 256;

        /// <summary>Seconds before an unacknowledged fragment is sent again.</summary>
        public const double ResendSeconds = 0.2;

        /// <summary>Seconds a fragment may stay unacknowledged before the link counts as lost.</summary>
        public const double LinkLostSeconds = 15;

        private const byte Magic = 0x4E;
        private const byte Version = 1;
        private const int HeaderSize = 11;
        private const int DataHeaderSize = HeaderSize + 14;
        private const int MaxAcksPerDatagram = 300;

        private Socket _socket;
        private readonly byte[] _receiveBuffer = new byte[65536];
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Random _random = new Random();
        private readonly Dictionary<PeerId, Peer> _peers = new Dictionary<PeerId, Peer>();
        private readonly List<PeerId> _linkFailures = new List<PeerId>();
        private readonly List<IncomingPacket> _incoming = new List<IncomingPacket>();
        private readonly List<PeerId> _scratchPeers = new List<PeerId>();
        private IPEndPoint _probeTarget;
        private double _nextProbeAt;
        private bool _disposed;

        /// <summary>Binds a UDP socket on <paramref name="bindAddress"/>:<paramref name="port"/> (0: any free port).</summary>
        public DirectTransport(PeerId localId, IPAddress bindAddress, int port)
        {
            if (!localId.IsValid)
                throw new ArgumentException("The local id must be valid.", nameof(localId));
            LocalId = localId;
            _socket = Open(bindAddress, port);
            LocalEndPoint = (IPEndPoint)_socket.LocalEndPoint;
        }

        public PeerId LocalId { get; }

        /// <summary>The bound address and port.</summary>
        public IPEndPoint LocalEndPoint { get; private set; }

        /// <summary>
        /// Moves to a new socket on <paramref name="bindAddress"/>:<paramref name="port"/> (0: any free port) and forgets
        /// every peer and the probe. The old socket stays when the new one cannot be bound (the exception is thrown).
        /// Already bound there (hosting again on the same port after a session ended): keeps the socket, since a second
        /// socket on its port could not be bound while it is open.
        /// </summary>
        public void Rebind(IPAddress bindAddress, int port)
        {
            ThrowIfDisposed();
            var address = bindAddress ?? IPAddress.Any;
            if (port != 0 && LocalEndPoint != null && LocalEndPoint.Port == port && LocalEndPoint.Address.Equals(address))
            {
                ForgetPeers();
                return;
            }
            var socket = Open(bindAddress, port);
            try
            {
                _socket.Close();
            }
            catch (Exception)
            {
                // The old socket is gone either way.
            }
            _socket = socket;
            LocalEndPoint = (IPEndPoint)_socket.LocalEndPoint;
            ForgetPeers();
        }

        private void ForgetPeers()
        {
            _peers.Clear();
            _incoming.Clear();
            _probeTarget = null;
            ProbedHost = PeerId.None;
        }

        /// <summary>Stops asking a host for its id (the session gave up or left).</summary>
        public void CancelProbe() => _probeTarget = null;

        private static Socket Open(IPAddress bindAddress, int port)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                // Windows reports an ICMP "port unreachable" as an error on the next receive; ignore it (SIO_UDP_CONNRESET).
                try
                {
                    socket.IOControl(-1744830452, new byte[] { 0 }, null);
                }
                catch (Exception)
                {
                    // Not Windows, or not supported by this runtime: a reset then surfaces as an ignored receive error.
                }
                socket.Blocking = false;
                socket.Bind(new IPEndPoint(bindAddress ?? IPAddress.Any, port));
                return socket;
            }
            catch
            {
                socket.Close();
                throw;
            }
        }

        /// <summary>The id of the host <see cref="Probe"/> reached; None until it answered.</summary>
        public PeerId ProbedHost { get; private set; }

        /// <summary>Test hook: datagrams for which this returns true are not sent (simulated loss).</summary>
        public Func<bool> DropOutgoing { get; set; }

        /// <summary>A random id in this transport's range (top 32 bits <see cref="IdPrefix"/>, low 32 bits nonzero).</summary>
        public static PeerId NewLocalId()
        {
            var bytes = new byte[4];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
            {
                uint low;
                do
                {
                    rng.GetBytes(bytes);
                    low = BitConverter.ToUInt32(bytes, 0);
                }
                while (low == 0);
                return new PeerId((IdPrefix << 32) | low);
            }
        }

        /// <summary>True for an id <see cref="NewLocalId"/> can return.</summary>
        public static bool IsDirectId(PeerId peer) => (peer.Value >> 32) == IdPrefix && (uint)peer.Value != 0;

        /// <summary>Asks <paramref name="host"/> for its id (sent again on every <see cref="Poll"/> until it answers).</summary>
        public void Probe(IPEndPoint host)
        {
            ThrowIfDisposed();
            _probeTarget = host ?? throw new ArgumentNullException(nameof(host));
            ProbedHost = PeerId.None;
            SendControl(Kind.Hello, host);
        }

        /// <summary>Peers whose oldest fragment went unacknowledged for <see cref="LinkLostSeconds"/>; their state is gone.</summary>
        public void DrainLinkFailures(List<PeerId> into)
        {
            into.AddRange(_linkFailures);
            _linkFailures.Clear();
        }

        public void Send(PeerId to, Channel channel, Reliability reliability, ArraySegment<byte> data)
        {
            ThrowIfDisposed();
            if (!ChannelInfo.IsDefined(channel))
                throw new ArgumentOutOfRangeException(nameof(channel));
            if (data.Array == null)
                throw new ArgumentNullException(nameof(data));
            if (!_peers.TryGetValue(to, out var peer) || peer.EndPoint == null)
            {
                NetLog.Debug($"[Direct] No address for {to}; {data.Count} byte(s) dropped.");
                return;
            }
            if (reliability == Reliability.Unreliable && data.Count <= MaxUnreliablePayload)
            {
                var datagram = DataDatagram(peer, channel, false, 0, 0, 1, data.Array, data.Offset, data.Count);
                Transmit(datagram, peer.EndPoint);
                return;
            }
            var stream = peer.Out[(int)channel];
            int count = Math.Max(1, (data.Count + FragmentSize - 1) / FragmentSize);
            if (count > ushort.MaxValue)
                throw new ArgumentException($"{data.Count} bytes is too large for one message.", nameof(data));
            for (int i = 0; i < count; i++)
            {
                int offset = i * FragmentSize;
                int length = Math.Min(FragmentSize, data.Count - offset);
                uint seq = stream.NextSeq++;
                var datagram = DataDatagram(peer, channel, true, seq, (ushort)i, (ushort)count, data.Array, data.Offset + offset, length);
                stream.Pending.Add(seq, new Outgoing { Datagram = datagram, FirstSent = -1 });
                stream.Order.Enqueue(seq);
            }
            Flush(peer, stream, Now);
        }

        public void Poll(List<IncomingPacket> into)
        {
            ThrowIfDisposed();
            double now = Now;
            Receive(now);
            into.AddRange(_incoming);
            _incoming.Clear();

            if (_probeTarget != null && !ProbedHost.IsValid && now >= _nextProbeAt)
            {
                _nextProbeAt = now + ResendSeconds;
                SendControl(Kind.Hello, _probeTarget);
            }

            _scratchPeers.Clear();
            _scratchPeers.AddRange(_peers.Keys);
            foreach (var id in _scratchPeers)
            {
                if (!_peers.TryGetValue(id, out var peer))
                    continue;
                SendAcks(peer);
                for (int c = 0; c < ChannelInfo.Count; c++)
                {
                    if (!Flush(peer, peer.Out[c], now))
                    {
                        NetLog.Warn($"[Direct] {id} acknowledged nothing for {LinkLostSeconds:0} s; link lost.");
                        _peers.Remove(id);
                        _linkFailures.Add(id);
                        break;
                    }
                }
            }
        }

        public void Disconnect(PeerId peer)
        {
            _peers.Remove(peer);
            _incoming.RemoveAll(p => p.From == peer);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _peers.Clear();
            try
            {
                _socket.Close();
            }
            catch (Exception)
            {
                // Closing a socket that failed is not worth reporting.
            }
        }

        // ---- Sending -------------------------------------------------------------------------------------

        /// <summary>Sends new fragments up to the window and resends overdue ones. False when the link is lost.</summary>
        private bool Flush(Peer peer, OutStream stream, double now)
        {
            if (stream.Pending.Count == 0)
            {
                stream.Order.Clear();
                return true;
            }
            // Order holds every assigned seq in send order; acknowledged ones are skipped and dropped from its front.
            while (stream.Order.Count > 0 && !stream.Pending.ContainsKey(stream.Order.Peek()))
                stream.Order.Dequeue();
            int inFlight = 0;
            foreach (uint seq in stream.Order)
            {
                if (inFlight >= Window)
                    break;
                if (!stream.Pending.TryGetValue(seq, out var item))
                    continue;
                inFlight++;
                if (item.FirstSent >= 0 && now - item.FirstSent > LinkLostSeconds)
                    return false;
                if (item.FirstSent < 0 || now - item.LastSent >= ResendSeconds)
                {
                    if (item.FirstSent < 0)
                        item.FirstSent = now;
                    item.LastSent = now;
                    Transmit(item.Datagram, peer.EndPoint);
                }
            }
            return true;
        }

        private void SendAcks(Peer peer)
        {
            for (int c = 0; c < ChannelInfo.Count; c++)
            {
                var acks = peer.In[c].Acks;
                while (acks.Count > 0)
                {
                    int count = Math.Min(MaxAcksPerDatagram, acks.Count);
                    var datagram = new byte[HeaderSize + 7 + count * 4];
                    int at = WriteHeader(datagram, Kind.Ack);
                    WriteUInt(datagram, ref at, peer.In[c].Stream);
                    datagram[at++] = (byte)c;
                    datagram[at++] = (byte)count;
                    datagram[at++] = (byte)(count >> 8);
                    for (int i = 0; i < count; i++)
                        WriteUInt(datagram, ref at, acks[i]);
                    acks.RemoveRange(0, count);
                    Transmit(datagram, peer.EndPoint);
                }
            }
        }

        private byte[] DataDatagram(Peer peer, Channel channel, bool reliable, uint seq, ushort index, ushort count,
            byte[] source, int offset, int length)
        {
            var datagram = new byte[DataHeaderSize + length];
            int at = WriteHeader(datagram, Kind.Data);
            WriteUInt(datagram, ref at, peer.OutStreamId);
            datagram[at++] = (byte)channel;
            datagram[at++] = reliable ? (byte)1 : (byte)0;
            WriteUInt(datagram, ref at, seq);
            datagram[at++] = (byte)index;
            datagram[at++] = (byte)(index >> 8);
            datagram[at++] = (byte)count;
            datagram[at++] = (byte)(count >> 8);
            Buffer.BlockCopy(source, offset, datagram, at, length);
            return datagram;
        }

        private void SendControl(Kind kind, IPEndPoint to)
        {
            var datagram = new byte[HeaderSize];
            WriteHeader(datagram, kind);
            Transmit(datagram, to);
        }

        private int WriteHeader(byte[] datagram, Kind kind)
        {
            datagram[0] = Magic;
            datagram[1] = Version;
            datagram[2] = (byte)kind;
            ulong id = LocalId.Value;
            for (int i = 0; i < 8; i++)
                datagram[3 + i] = (byte)(id >> (8 * i));
            return HeaderSize;
        }

        private void Transmit(byte[] datagram, IPEndPoint to)
        {
            if (to == null || (DropOutgoing != null && DropOutgoing()))
                return;
            try
            {
                _socket.SendTo(datagram, to);
            }
            catch (SocketException e)
            {
                // A full send buffer (WouldBlock) or an unreachable peer: reliable data is resent, the rest may drop.
                NetLog.Debug($"[Direct] Sending {datagram.Length} byte(s) to {to} failed: {e.SocketErrorCode}");
            }
        }

        // ---- Receiving -----------------------------------------------------------------------------------

        private void Receive(double now)
        {
            while (true)
            {
                int length;
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                try
                {
                    if (_socket.Available <= 0)
                        return;
                    length = _socket.ReceiveFrom(_receiveBuffer, ref from);
                }
                catch (SocketException e) when (e.SocketErrorCode == SocketError.WouldBlock)
                {
                    return;
                }
                catch (SocketException e)
                {
                    // ConnectionReset and the like refer to an earlier send; the next datagram may be fine.
                    NetLog.Debug($"[Direct] Receive failed: {e.SocketErrorCode}");
                    continue;
                }
                try
                {
                    Handle(_receiveBuffer, length, (IPEndPoint)from, now);
                }
                catch (Exception e)
                {
                    NetLog.Debug($"[Direct] Dropped a malformed datagram from {from}: {e.Message}");
                }
            }
        }

        private void Handle(byte[] d, int length, IPEndPoint from, double now)
        {
            if (length < HeaderSize || d[0] != Magic || d[1] != Version)
                return;
            var kind = (Kind)d[2];
            ulong id = 0;
            for (int i = 0; i < 8; i++)
                id |= (ulong)d[3 + i] << (8 * i);
            var sender = new PeerId(id);
            if (!sender.IsValid || sender == LocalId)
                return;
            int at = HeaderSize;
            switch (kind)
            {
                case Kind.Hello:
                    Learn(sender, from);
                    SendControl(Kind.Welcome, from);
                    return;
                case Kind.Welcome:
                    if (_probeTarget != null && _probeTarget.Equals(from))
                    {
                        Learn(sender, from);
                        ProbedHost = sender;
                    }
                    return;
                case Kind.Ack:
                {
                    if (!_peers.TryGetValue(sender, out var peer) || length < at + 7)
                        return;
                    uint stream = ReadUInt(d, ref at);
                    int channel = d[at++];
                    int count = d[at] | (d[at + 1] << 8);
                    at += 2;
                    if (stream != peer.OutStreamId || channel >= ChannelInfo.Count || length < at + count * 4)
                        return;
                    var pending = peer.Out[channel].Pending;
                    for (int i = 0; i < count; i++)
                        pending.Remove(ReadUInt(d, ref at));
                    return;
                }
                case Kind.Data:
                {
                    if (length < DataHeaderSize)
                        return;
                    var peer = Learn(sender, from);
                    uint stream = ReadUInt(d, ref at);
                    int channel = d[at++];
                    bool reliable = d[at++] != 0;
                    uint seq = ReadUInt(d, ref at);
                    int index = d[at] | (d[at + 1] << 8);
                    int count = d[at + 2] | (d[at + 3] << 8);
                    at += 4;
                    if (channel >= ChannelInfo.Count || count == 0 || index >= count)
                        return;
                    var payload = new byte[length - at];
                    Buffer.BlockCopy(d, at, payload, 0, payload.Length);
                    if (!reliable)
                    {
                        _incoming.Add(new IncomingPacket(sender, (Channel)channel, payload));
                        return;
                    }
                    ReceiveReliable(peer, (Channel)channel, stream, seq, index, count, payload);
                    return;
                }
            }
        }

        private void ReceiveReliable(Peer peer, Channel channel, uint stream, uint seq, int index, int count, byte[] payload)
        {
            var input = peer.In[(int)channel];
            if (input.Stream != stream)
            {
                // The sender started a new stream (it forgot us, or restarted): start over from its first fragment.
                input.Reset(stream);
            }
            input.Acks.Add(seq);
            // Already delivered (a resend whose ack was lost), or too far ahead to buffer.
            if (unchecked((int)(seq - input.NextExpected)) < 0 || seq - input.NextExpected > Window * 4)
                return;
            if (!input.Buffered.ContainsKey(seq))
                input.Buffered.Add(seq, new Fragment { Index = index, Count = count, Payload = payload });
            while (input.Buffered.TryGetValue(input.NextExpected, out var next))
            {
                input.Buffered.Remove(input.NextExpected);
                input.NextExpected++;
                if (next.Index == 0)
                    input.Assembly.Clear();
                input.Assembly.Add(next.Payload);
                if (next.Index != next.Count - 1)
                    continue;
                int total = 0;
                foreach (var part in input.Assembly)
                    total += part.Length;
                var message = new byte[total];
                int at = 0;
                foreach (var part in input.Assembly)
                {
                    Buffer.BlockCopy(part, 0, message, at, part.Length);
                    at += part.Length;
                }
                input.Assembly.Clear();
                _incoming.Add(new IncomingPacket(peer.Id, channel, message));
            }
        }

        private Peer Learn(PeerId id, IPEndPoint from)
        {
            if (!_peers.TryGetValue(id, out var peer))
            {
                peer = new Peer(id, NewStreamId());
                _peers.Add(id, peer);
            }
            if (peer.EndPoint == null || !peer.EndPoint.Equals(from))
                peer.EndPoint = new IPEndPoint(from.Address, from.Port);
            return peer;
        }

        /// <summary>Remembers <paramref name="endPoint"/> as <paramref name="peer"/>'s address.</summary>
        public void AddPeer(PeerId peer, IPEndPoint endPoint)
        {
            ThrowIfDisposed();
            if (endPoint == null)
                throw new ArgumentNullException(nameof(endPoint));
            Learn(peer, endPoint);
        }

        private uint NewStreamId()
        {
            uint value;
            do
            {
                value = (uint)_random.Next() ^ ((uint)_random.Next() << 16);
            }
            while (value == 0);
            return value;
        }

        private double Now => _clock.Elapsed.TotalSeconds;

        private static void WriteUInt(byte[] d, ref int at, uint value)
        {
            d[at++] = (byte)value;
            d[at++] = (byte)(value >> 8);
            d[at++] = (byte)(value >> 16);
            d[at++] = (byte)(value >> 24);
        }

        private static uint ReadUInt(byte[] d, ref int at)
        {
            uint value = d[at] | ((uint)d[at + 1] << 8) | ((uint)d[at + 2] << 16) | ((uint)d[at + 3] << 24);
            at += 4;
            return value;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(DirectTransport));
        }

        private enum Kind : byte
        {
            Hello = 1,
            Welcome = 2,
            Data = 3,
            Ack = 4
        }

        private sealed class Peer
        {
            public Peer(PeerId id, uint outStreamId)
            {
                Id = id;
                OutStreamId = outStreamId;
                for (int i = 0; i < ChannelInfo.Count; i++)
                {
                    Out[i] = new OutStream();
                    In[i] = new InStream();
                }
            }

            public readonly PeerId Id;
            public readonly uint OutStreamId;
            public readonly OutStream[] Out = new OutStream[ChannelInfo.Count];
            public readonly InStream[] In = new InStream[ChannelInfo.Count];
            public IPEndPoint EndPoint;
        }

        private sealed class OutStream
        {
            public uint NextSeq;
            public readonly Dictionary<uint, Outgoing> Pending = new Dictionary<uint, Outgoing>();
            public readonly Queue<uint> Order = new Queue<uint>();
        }

        private sealed class Outgoing
        {
            public byte[] Datagram;
            public double FirstSent;
            public double LastSent;
        }

        private sealed class InStream
        {
            public uint Stream;
            public uint NextExpected;
            public readonly Dictionary<uint, Fragment> Buffered = new Dictionary<uint, Fragment>();
            public readonly List<byte[]> Assembly = new List<byte[]>();
            public readonly List<uint> Acks = new List<uint>();

            public void Reset(uint stream)
            {
                Stream = stream;
                NextExpected = 0;
                Buffered.Clear();
                Assembly.Clear();
            }
        }

        private struct Fragment
        {
            public int Index;
            public int Count;
            public byte[] Payload;
        }
    }
}
