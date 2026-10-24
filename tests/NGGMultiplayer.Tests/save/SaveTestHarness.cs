using System;
using System.Collections.Generic;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Save;
using NGGMultiplayer.Core.Session;

namespace NGGMultiplayer.Tests.Save
{
    /// <summary>Minimal <see cref="ISessionMessaging"/> over a <see cref="LoopbackTransport"/>; membership is driven by the test.</summary>
    internal sealed class FakeSession : ISessionMessaging
    {
        private readonly Dictionary<byte, Action<PeerId, NetReader>> _handlers = new Dictionary<byte, Action<PeerId, NetReader>>();
        private readonly HashSet<PeerId> _remotes = new HashSet<PeerId>();
        private readonly List<IncomingPacket> _incoming = new List<IncomingPacket>();
        private readonly NetReader _reader = new NetReader();

        public FakeSession(LoopbackTransport transport, PeerId hostId)
        {
            Transport = transport;
            HostId = hostId;
            if (!IsHost)
                _remotes.Add(hostId);
        }

        public LoopbackTransport Transport { get; }

        public bool IsHost => Transport.LocalId == HostId;

        public PeerId LocalId => Transport.LocalId;

        public PeerId HostId { get; }

        /// <summary>Every packet received, as (sender, message type), in arrival order.</summary>
        public List<(PeerId From, byte Type)> Received { get; } = new List<(PeerId, byte)>();

        public int HandlerCount => _handlers.Count;

        public event Action<PeerId> PlayerJoined;

        public event Action<PeerId> PlayerLeft;

        public event Action SessionClosed;

        public bool HasHandler(byte type) => _handlers.ContainsKey(type);

        public int CountReceived(byte type)
        {
            int n = 0;
            foreach (var r in Received)
            {
                if (r.Type == type)
                    n++;
            }
            return n;
        }

        public void SendTo(PeerId peer, Channel channel, Reliability reliability, ArraySegment<byte> data) =>
            Transport.Send(peer, channel, reliability, data);

        public bool IsPeerConnected(PeerId peer) => _remotes.Contains(peer);

        public void Broadcast(Channel channel, Reliability reliability, ArraySegment<byte> data, PeerId except)
        {
            foreach (var peer in _remotes)
            {
                if (peer != except)
                    Transport.Send(peer, channel, reliability, data);
            }
        }

        public void RegisterHandler(byte messageType, Action<PeerId, NetReader> handler)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));
            if (_handlers.ContainsKey(messageType))
                throw new InvalidOperationException($"Handler for {MessageType.GetName(messageType)} already registered.");
            _handlers.Add(messageType, handler);
        }

        public void UnregisterHandler(byte messageType) => _handlers.Remove(messageType);

        public void AddRemote(PeerId peer) => _remotes.Add(peer);

        public void RemoveRemote(PeerId peer) => _remotes.Remove(peer);

        public void RaiseJoined(PeerId peer) => PlayerJoined?.Invoke(peer);

        public void RaiseLeft(PeerId peer) => PlayerLeft?.Invoke(peer);

        public void RaiseClosed() => SessionClosed?.Invoke();

        /// <summary>Dispatches everything delivered so far. Handler exceptions propagate so tests see them.</summary>
        public int Pump()
        {
            if (Transport.IsDisposed)
                return 0;
            _incoming.Clear();
            Transport.Poll(_incoming);
            foreach (var packet in _incoming)
            {
                if (packet.Data.Length == 0)
                    continue;
                _reader.SetSource(packet.Data);
                byte type = _reader.ReadByte();
                Received.Add((packet.From, type));
                if (_handlers.TryGetValue(type, out var handler))
                    handler(packet.From, _reader);
            }
            return _incoming.Count;
        }
    }

    internal sealed class ClientNode
    {
        public PeerId Id;
        public FakeSession Session;
        public InMemorySaveBackend Backend;
        public SaveSyncClient Sync;
        public int SyncedEvents;
        public int EndedEvents;
        public readonly List<(SaveDelta Delta, string Reason)> Rejections = new List<(SaveDelta, string)>();
        public readonly List<string> Failures = new List<string>();
    }

    /// <summary>A host plus clients wired over one <see cref="LoopbackHub"/>, with a manual clock.</summary>
    internal sealed class SaveNet
    {
        public static readonly PeerId HostId = new PeerId(100);

        private readonly SaveSyncOptions _options;

        public SaveNet(Func<PeerId, SaveDelta, bool> validator = null, SaveSyncOptions options = null, bool createHost = true, int seed = 1)
        {
            _options = options;
            Hub = new LoopbackHub(seed);
            HostSession = new FakeSession(Hub.CreateTransport(HostId), HostId);
            HostBackend = new InMemorySaveBackend();
            if (createHost)
                Host = new SaveSyncHost(HostSession, HostBackend, validator, options);
        }

        public LoopbackHub Hub { get; }

        public FakeSession HostSession { get; }

        public InMemorySaveBackend HostBackend { get; }

        public SaveSyncHost Host { get; }

        public List<ClientNode> Clients { get; } = new List<ClientNode>();

        public ClientNode AddClient(ulong id, InMemorySaveBackend backend = null)
        {
            var node = new ClientNode
            {
                Id = new PeerId(id),
                Backend = backend ?? new InMemorySaveBackend(),
            };
            node.Session = new FakeSession(Hub.CreateTransport(node.Id), HostId);
            node.Sync = new SaveSyncClient(node.Session, node.Backend, _options);
            node.Sync.Synced += () => node.SyncedEvents++;
            node.Sync.Ended += () => node.EndedEvents++;
            node.Sync.ClaimRejected += (d, r) => node.Rejections.Add((d, r));
            node.Sync.SyncFailed += r => node.Failures.Add(r);
            node.Sync.Tick(Hub.Now);
            Clients.Add(node);
            return node;
        }

        public void Join(ClientNode node)
        {
            HostSession.AddRemote(node.Id);
            HostSession.RaiseJoined(node.Id);
        }

        public ClientNode Connect(ulong id, InMemorySaveBackend backend = null)
        {
            var node = AddClient(id, backend);
            Join(node);
            return node;
        }

        public void Leave(ClientNode node)
        {
            HostSession.RemoveRemote(node.Id);
            HostSession.RaiseLeft(node.Id);
            node.Session.RaiseClosed();
        }

        /// <summary>One frame: tick everyone at the current time, then dispatch delivered packets.</summary>
        public int Pump()
        {
            double now = Hub.Now;
            Host?.Tick(now);
            foreach (var c in Clients)
                c.Sync.Tick(now);
            int n = HostSession.Pump();
            foreach (var c in Clients)
                n += c.Session.Pump();
            return n;
        }

        public void Run(double seconds, double step = 0.01)
        {
            int steps = (int)Math.Round(seconds / step);
            for (int i = 0; i < steps; i++)
            {
                Hub.Advance(step);
                Pump();
            }
        }

        /// <summary>Runs frames until no packet is in flight and a frame delivers nothing.</summary>
        public void Settle(double step = 0.01, int maxFrames = 20000)
        {
            for (int i = 0; i < maxFrames; i++)
            {
                int delivered = Pump();
                if (delivered == 0 && InFlight() == 0)
                    return;
                Hub.Advance(step);
            }
            throw new TimeoutException("Network did not settle.");
        }

        public int InFlight()
        {
            int n = HostSession.Transport.PendingCount;
            foreach (var c in Clients)
                n += c.Session.Transport.IsDisposed ? 0 : c.Session.Transport.PendingCount;
            return n;
        }

        /// <summary>Sends a raw packet as if from the host.</summary>
        public void SendFromHost(PeerId to, byte[] packet) =>
            HostSession.Transport.Send(to, Channel.Save, Reliability.Reliable, new ArraySegment<byte>(packet));
    }

    /// <summary>Hand-built save packets, to exercise the wire format and inject faults.</summary>
    internal static class RawPackets
    {
        public static byte[] SaveBegin(uint totalBytes, uint chunkCount, uint crc, uint revision)
        {
            var w = PacketBuilder.Begin(MessageType.SaveBegin);
            w.WriteVarUInt(totalBytes);
            w.WriteVarUInt(chunkCount);
            w.WriteUInt(crc);
            w.WriteVarUInt(revision);
            return w.ToArray();
        }

        public static byte[] SaveChunk(uint index, byte[] data, int offset, int count)
        {
            var w = PacketBuilder.Begin(MessageType.SaveChunk);
            w.WriteVarUInt(index);
            w.WriteBytes(data, offset, count);
            return w.ToArray();
        }

        /// <summary>SaveBegin followed by every chunk of <paramref name="snapshot"/>.</summary>
        public static List<byte[]> Snapshot(byte[] snapshot, uint revision, uint? crcOverride = null)
        {
            int chunks = SaveProtocol.GetChunkCount(snapshot.Length);
            var packets = new List<byte[]> { SaveBegin((uint)snapshot.Length, (uint)chunks, crcOverride ?? Crc32.Compute(snapshot), revision) };
            for (int i = 0; i < chunks; i++)
            {
                int offset = i * SaveProtocol.ChunkSize;
                packets.Add(SaveChunk((uint)i, snapshot, offset, Math.Min(SaveProtocol.ChunkSize, snapshot.Length - offset)));
            }
            return packets;
        }

        public static byte[] Delta(uint revision, string kind, string key, byte[] value)
        {
            var w = PacketBuilder.Begin(MessageType.SaveDelta);
            w.WriteVarUInt(revision);
            new SaveDelta(kind, key, value).Serialize(w);
            return w.ToArray();
        }

        public static byte[] Claim(string kind, string key, byte[] value)
        {
            var w = PacketBuilder.Begin(MessageType.ProgressClaim);
            new SaveDelta(kind, key, value).Serialize(w);
            return w.ToArray();
        }

        public static byte[] Ack(uint revision)
        {
            var w = PacketBuilder.Begin(MessageType.SaveAck);
            w.WriteVarUInt(revision);
            return w.ToArray();
        }

        public static byte[] ResyncRequest(uint revision)
        {
            var w = PacketBuilder.Begin(MessageType.SaveResyncRequest);
            w.WriteVarUInt(revision);
            return w.ToArray();
        }

        public static byte[] Bytes(params byte[] b) => b;
    }

    internal static class SaveAssert
    {
        public static void SameProgress(InMemorySaveBackend expected, InMemorySaveBackend actual) =>
            Xunit.Assert.Equal(expected.CaptureSnapshot(), actual.CaptureSnapshot());
    }
}
