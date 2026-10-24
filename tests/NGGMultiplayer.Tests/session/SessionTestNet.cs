using System;
using System.Collections.Generic;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Session;

namespace NGGMultiplayer.Tests.SessionTests
{
    /// <summary>A SessionManager on a loopback transport plus a record of every event it raised.</summary>
    internal sealed class SessionNode
    {
        public SessionNode(LoopbackHub hub, ulong id, SessionConfig config)
        {
            Id = new PeerId(id);
            Transport = hub.CreateTransport(Id);
            Session = new SessionManager(Transport, config);
            Session.SessionStarted += () => { StartedCount++; Log.Add("started"); };
            Session.SessionEnded += info => { Ended.Add(info); Log.Add("ended:" + info.Reason); };
            Session.PlayerJoined += p => { Joined.Add(p); Log.Add("joined:" + p.Id); };
            Session.PlayerLeft += (p, r) => { Left.Add((p, r)); Log.Add("left:" + p.Id + ":" + r); };
        }

        public PeerId Id { get; }
        public LoopbackTransport Transport { get; }
        public SessionManager Session { get; }

        /// <summary>Added to the hub clock when updating this node, to give it its own time base.</summary>
        public double ClockOffset { get; set; }

        /// <summary>Paused nodes are not updated (simulates a frozen or crashed process).</summary>
        public bool Paused { get; set; }

        public int StartedCount { get; private set; }
        public List<SessionEndInfo> Ended { get; } = new List<SessionEndInfo>();
        public List<PlayerInfo> Joined { get; } = new List<PlayerInfo>();
        public List<(PlayerInfo Player, PlayerLeaveReason Reason)> Left { get; } = new List<(PlayerInfo, PlayerLeaveReason)>();
        public List<string> Log { get; } = new List<string>();
    }

    /// <summary>A bare transport for crafting raw packets on the wire.</summary>
    internal sealed class RawPeer
    {
        public RawPeer(LoopbackHub hub, ulong id)
        {
            Id = new PeerId(id);
            Transport = hub.CreateTransport(Id);
        }

        public PeerId Id { get; }
        public LoopbackTransport Transport { get; }

        public void Send(PeerId to, byte[] packet, Channel channel = Channel.Control, Reliability reliability = Reliability.Reliable)
            => Transport.Send(to, channel, reliability, new ArraySegment<byte>(packet));

        public void Send(PeerId to, NetWriter packet, Channel channel = Channel.Control, Reliability reliability = Reliability.Reliable)
            => Transport.Send(to, channel, reliability, packet.AsSegment());

        public List<IncomingPacket> Drain()
        {
            var list = new List<IncomingPacket>();
            Transport.Poll(list);
            return list;
        }

        public List<IncomingPacket> Drain(byte messageType)
            => Drain().FindAll(p => p.Data.Length > 0 && p.Data[0] == messageType);
    }

    internal sealed class SessionTestNet
    {
        private readonly List<SessionNode> _nodes = new List<SessionNode>();

        public SessionTestNet(int seed = 12345)
        {
            Hub = new LoopbackHub(seed);
        }

        public LoopbackHub Hub { get; }

        public SessionNode Add(ulong id, Action<SessionConfig> configure = null)
        {
            var config = new SessionConfig { DisplayName = "Peer" + id };
            configure?.Invoke(config);
            var node = new SessionNode(Hub, id, config);
            _nodes.Add(node);
            return node;
        }

        public RawPeer AddRaw(ulong id) => new RawPeer(Hub, id);

        public void UpdateAll()
        {
            foreach (var node in _nodes)
            {
                if (!node.Paused && !node.Transport.IsDisposed)
                    node.Session.Update(Hub.Now + node.ClockOffset);
            }
        }

        public void Step(double seconds, double dt = 0.01)
        {
            int steps = (int)Math.Round(seconds / dt);
            for (int i = 0; i < steps; i++)
            {
                Hub.Advance(dt);
                UpdateAll();
            }
        }

        public bool RunUntil(Func<bool> condition, double maxSeconds, double dt = 0.01)
        {
            int steps = (int)Math.Round(maxSeconds / dt);
            for (int i = 0; i < steps; i++)
            {
                if (condition())
                    return true;
                Hub.Advance(dt);
                UpdateAll();
            }
            return condition();
        }

        /// <summary>Host plus <paramref name="clientCount"/> joined clients (ids 1, 2, 3, ...).</summary>
        public (SessionNode Host, SessionNode[] Clients) StartWithClients(int clientCount, Action<SessionConfig> configure = null)
        {
            var host = Add(1, c => { c.DisplayName = "Host"; configure?.Invoke(c); });
            var clients = new SessionNode[clientCount];
            for (int i = 0; i < clientCount; i++)
                clients[i] = Add((ulong)(i + 2), configure);
            host.Session.StartHost();
            foreach (var c in clients)
                c.Session.Connect(host.Id);
            if (!RunUntil(() => Array.TrueForAll(clients, c => c.Session.IsActive), 5))
                throw new InvalidOperationException("Clients failed to join.");
            Step(0.1);
            return (host, clients);
        }
    }

    internal static class Wire
    {
        public static NetWriter Hello(int protocol = ProtocolInfo.Version, string modVersion = ProtocolInfo.ModVersion,
            string gameBuild = "", string name = "Raw", uint nonce = 1)
        {
            var w = PacketBuilder.Begin(MessageType.Hello);
            w.WriteInt(protocol);
            w.WriteString(modVersion);
            w.WriteString(gameBuild);
            w.WriteString(name);
            w.WriteUInt(nonce);
            return w;
        }

        public static NetWriter Welcome(int localIndex, params (PeerId Id, int Index, string Name)[] roster)
        {
            var w = PacketBuilder.Begin(MessageType.Welcome);
            w.WriteByte((byte)localIndex);
            WriteRoster(w, roster);
            return w;
        }

        public static NetWriter RosterUpdate(params (PeerId Id, int Index, string Name)[] roster)
        {
            var w = PacketBuilder.Begin(MessageType.RosterUpdate);
            WriteRoster(w, roster);
            return w;
        }

        public static NetWriter Reject(RejectReason reason, string hostMod, string message, int hostProtocol = ProtocolInfo.Version)
        {
            var w = PacketBuilder.Begin(MessageType.Reject);
            w.WriteByte((byte)reason);
            w.WriteInt(hostProtocol);
            w.WriteString(hostMod);
            w.WriteString(message);
            return w;
        }

        public static (RejectReason Reason, int HostProtocol, string HostMod, string Message) ReadReject(byte[] data)
        {
            var r = new NetReader(data);
            if (r.ReadByte() != MessageType.Reject)
                throw new InvalidOperationException("Not a Reject.");
            return ((RejectReason)r.ReadByte(), r.ReadInt(), r.ReadString(), r.ReadString());
        }

        public static byte[] Game(byte type, params byte[] payload)
        {
            var data = new byte[payload.Length + 1];
            data[0] = type;
            Buffer.BlockCopy(payload, 0, data, 1, payload.Length);
            return data;
        }

        private static void WriteRoster(NetWriter w, (PeerId Id, int Index, string Name)[] roster)
        {
            w.WriteVarUInt((uint)roster.Length);
            foreach (var e in roster)
            {
                w.WritePeerId(e.Id);
                w.WriteByte((byte)e.Index);
                w.WriteString(e.Name);
                w.WriteUShort(0);
            }
        }
    }
}
