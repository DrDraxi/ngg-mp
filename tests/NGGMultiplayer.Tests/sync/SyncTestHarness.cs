using System;
using System.Collections.Generic;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Session;
using NGGMultiplayer.Core.Sync;

namespace NGGMultiplayer.Tests.Sync
{
    /// <summary>Minimal <see cref="ISessionMessaging"/> over a <see cref="LoopbackTransport"/>; membership is driven by the test.</summary>
    internal sealed class FakeSyncSession : ISessionMessaging
    {
        private readonly Dictionary<byte, Action<PeerId, NetReader>> _handlers = new Dictionary<byte, Action<PeerId, NetReader>>();
        private readonly HashSet<PeerId> _remotes = new HashSet<PeerId>();
        private readonly List<IncomingPacket> _incoming = new List<IncomingPacket>();
        private readonly NetReader _reader = new NetReader();

        public FakeSyncSession(LoopbackTransport transport, PeerId hostId)
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

        public List<(PeerId From, byte Type, Channel Channel)> Received { get; } = new List<(PeerId, byte, Channel)>();

        public int HandlerCount => _handlers.Count;

        public event Action<PeerId> PlayerJoined;

        public event Action<PeerId> PlayerLeft;

        public event Action SessionClosed;

        public bool IsPeerConnected(PeerId peer) => _remotes.Contains(peer);

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
                Received.Add((packet.From, type, packet.Channel));
                if (_handlers.TryGetValue(type, out var handler))
                    handler(packet.From, _reader);
            }
            return _incoming.Count;
        }
    }

    internal sealed class TestEntity : INetEntity
    {
        public TestEntity(NetEntity entity)
        {
            Entity = entity;
            Current = entity?.LastState ?? TransformState.Identity;
        }

        public NetEntity Entity { get; set; }

        public TransformState Current;

        public bool CaptureEnabled = true;

        public bool ThrowOnCapture;

        public int Captures;

        public readonly List<(TransformState State, SnapshotSampleKind Kind)> Applied = new List<(TransformState, SnapshotSampleKind)>();

        public readonly List<(PeerId Owner, bool IsLocal)> OwnershipChanges = new List<(PeerId, bool)>();

        public DespawnReason? Despawned;

        public TransformState LastApplied => Applied[Applied.Count - 1].State;

        public SnapshotSampleKind LastKind => Applied[Applied.Count - 1].Kind;

        public bool CaptureState(out TransformState state)
        {
            Captures++;
            if (ThrowOnCapture)
                throw new InvalidOperationException("capture failed");
            state = Current;
            return CaptureEnabled;
        }

        public void ApplyState(in TransformState state, SnapshotSampleKind kind)
        {
            Applied.Add((state, kind));
            Current = state;
        }

        public void OnOwnershipChanged(PeerId owner, bool isLocal) => OwnershipChanges.Add((owner, isLocal));

        public void OnDespawned(DespawnReason reason) => Despawned = reason;
    }

    internal sealed class TestFactory : INetEntityFactory
    {
        public readonly Dictionary<NetEntityId, TestEntity> ById = new Dictionary<NetEntityId, TestEntity>();
        public readonly List<NetEntity> Requests = new List<NetEntity>();
        public bool ReturnNull;
        public bool Throw;

        public INetEntity CreateEntity(NetEntity entity)
        {
            Requests.Add(entity);
            if (Throw)
                throw new InvalidOperationException("factory failed");
            if (ReturnNull)
                return null;
            var e = new TestEntity(entity);
            ById[entity.Id] = e;
            return e;
        }

        public TestEntity this[NetEntityId id] => ById[id];
    }

    internal sealed class SyncNode
    {
        public PeerId Id;
        public FakeSyncSession Session;
        public TestFactory Factory;
        public NetEntityRegistry Registry;
        public RpcRouter Rpc;
        public readonly List<string> Events = new List<string>();
        public readonly List<(PeerId Sender, string Rpc, int Value)> Calls = new List<(PeerId, string, int)>();

        public TestEntity Replica(NetEntityId id) => Factory.ById[id];

        /// <summary>Registers an RPC that records (sender, name, int payload).</summary>
        public void RegisterRecorder(string name, RpcValidator validator = null) =>
            Rpc.Register(name, (sender, r) => Calls.Add((sender, name, r.IsAtEnd ? -1 : r.ReadInt())), validator);
    }

    /// <summary>Host plus clients over one <see cref="LoopbackHub"/>; the hub's clock is everyone's host time.</summary>
    internal sealed class SyncNet
    {
        public static readonly PeerId HostId = new PeerId(100);

        private readonly NetEntityRegistryOptions _options;

        public SyncNet(NetEntityRegistryOptions options = null, int seed = 1, bool withRegistry = true, bool withRpc = true)
        {
            _options = options;
            WithRegistry = withRegistry;
            WithRpc = withRpc;
            Hub = new LoopbackHub(seed);
            Host = CreateNode(HostId);
        }

        public bool WithRegistry { get; }

        public bool WithRpc { get; }

        public LoopbackHub Hub { get; }

        public SyncNode Host { get; }

        public List<SyncNode> Clients { get; } = new List<SyncNode>();

        public double Now => Hub.Now;

        public SyncNode Connect(ulong id)
        {
            var node = CreateNode(new PeerId(id));
            Clients.Add(node);
            Host.Session.AddRemote(node.Id);
            Host.Session.RaiseJoined(node.Id);
            return node;
        }

        public void Leave(SyncNode node)
        {
            Host.Session.RemoveRemote(node.Id);
            Host.Session.RaiseLeft(node.Id);
            node.Session.RaiseClosed();
            Clients.Remove(node);
            node.Session.Transport.Dispose();
        }

        /// <summary>One frame at the current time: dispatch delivered packets, then update.</summary>
        public void Frame()
        {
            double now = Hub.Now;
            Step(Host, now);
            foreach (var c in Clients)
                Step(c, now);
        }

        public void Run(double seconds, double step = 0.01)
        {
            int steps = (int)Math.Round(seconds / step);
            for (int i = 0; i < steps; i++)
            {
                Hub.Advance(step);
                Frame();
            }
        }

        /// <summary>Lets reliable traffic arrive without advancing much time (zero latency: two frames suffice).</summary>
        public void Flush()
        {
            for (int i = 0; i < 4; i++)
                Frame();
        }

        private static void Step(SyncNode node, double now)
        {
            node.Session.Pump();
            node.Registry?.Update(now);
        }

        private SyncNode CreateNode(PeerId id)
        {
            var node = new SyncNode { Id = id, Factory = new TestFactory() };
            node.Session = new FakeSyncSession(Hub.CreateTransport(id), HostId);
            if (WithRegistry)
            {
                node.Registry = new NetEntityRegistry(node.Session, node.Factory, _options);
                node.Registry.EntitySpawned += e => node.Events.Add("spawn:" + e.Id.Value);
                node.Registry.EntityDespawned += (e, r) => node.Events.Add("despawn:" + e.Id.Value + ":" + r);
                node.Registry.OwnershipChanged += (e, prev) => node.Events.Add("owner:" + e.Id.Value + ":" + prev.Value + "->" + e.Owner.Value);
                node.Registry.OwnershipDenied += (e, owner) => node.Events.Add("denied:" + e.Id.Value + ":" + owner.Value);
            }
            if (WithRpc)
                node.Rpc = new RpcRouter(node.Session);
            return node;
        }

        /// <summary>Raw packet from <paramref name="from"/>'s transport.</summary>
        public static void SendRaw(SyncNode from, PeerId to, byte[] packet, Channel channel = Channel.Events, Reliability reliability = Reliability.Reliable) =>
            from.Session.Transport.Send(to, channel, reliability, new ArraySegment<byte>(packet));
    }

    internal static class States
    {
        public static TransformState At(float x, float y = 0, float z = 0, double time = 0) =>
            new TransformState(new NetVector3(x, y, z), NetQuaternion.Identity, time);

        public static TransformState Moving(float x, float vx, double time = 0)
        {
            var s = At(x, 0, 0, time);
            s.Velocity = new NetVector3(vx, 0, 0);
            return s;
        }
    }
}
