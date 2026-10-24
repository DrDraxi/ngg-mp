using System;
using System.Collections.Generic;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Session;
using Xunit;

namespace NGGMultiplayer.Tests.SessionTests
{
    public class SessionDispatchTests
    {
        private const byte GameMsg = MessageType.GameFirst;

        private static List<(PeerId From, byte[] Payload)> Record(SessionNode node, byte type)
        {
            var list = new List<(PeerId, byte[])>();
            node.Session.RegisterHandler(type, (from, r) => list.Add((from, r.ReadRemainingSegment().ToArray())));
            return list;
        }

        private static ArraySegment<byte> Packet(byte type, params byte[] payload) => new ArraySegment<byte>(Wire.Game(type, payload));

        [Fact]
        public void SendToHost_IsDispatchedOnHost_WithSender()
        {
            var net = new SessionTestNet();
            var (host, clients) = net.StartWithClients(1);
            var got = Record(host, GameMsg);

            Assert.True(clients[0].Session.SendToHost(Channel.Events, Reliability.Reliable, Packet(GameMsg, 1, 2, 3)));
            net.Step(0.02);

            var msg = Assert.Single(got);
            Assert.Equal(clients[0].Id, msg.From);
            Assert.Equal(new byte[] { 1, 2, 3 }, msg.Payload);
        }

        [Fact]
        public void Broadcast_ReachesAllClientsExceptExcluded()
        {
            var net = new SessionTestNet();
            var (host, clients) = net.StartWithClients(3);
            var received = new List<(PeerId, byte[])>[3];
            for (int i = 0; i < 3; i++)
                received[i] = Record(clients[i], MessageType.State);
            var hostGot = Record(host, MessageType.State);

            int sent = host.Session.Broadcast(Channel.State, Reliability.Unreliable, Packet(MessageType.State, 9), except: clients[1].Id);
            net.Step(0.02);

            Assert.Equal(2, sent);
            Assert.Equal(host.Id, Assert.Single(received[0]).Item1);
            Assert.Empty(received[1]);
            Assert.Single(received[2]);
            Assert.Empty(hostGot);

            Assert.Equal(3, host.Session.Broadcast(Channel.State, Reliability.Unreliable, Packet(MessageType.State, 1)));
        }

        [Fact]
        public void SendTo_FromHost_ReachesOneClient()
        {
            var net = new SessionTestNet();
            var (host, clients) = net.StartWithClients(2);
            var a = Record(clients[0], MessageType.Rpc);
            var b = Record(clients[1], MessageType.Rpc);

            var writer = PacketBuilder.Begin(MessageType.Rpc);
            writer.WriteString("hi");
            Assert.True(host.Session.SendTo(clients[1].Id, Channel.Events, Reliability.Reliable, writer));
            Assert.False(host.Session.SendTo(new PeerId(12345), Channel.Events, Reliability.Reliable, writer));
            net.Step(0.02);

            Assert.Empty(a);
            Assert.Equal(host.Id, Assert.Single(b).From);
        }

        [Fact]
        public void Client_CanOnlyTalkToHost()
        {
            var net = new SessionTestNet();
            var (host, clients) = net.StartWithClients(2);
            var got = Record(clients[1], GameMsg);

            Assert.False(clients[0].Session.SendTo(clients[1].Id, Channel.Events, Reliability.Reliable, Packet(GameMsg)));
            Assert.Throws<InvalidOperationException>(() =>
                clients[0].Session.Broadcast(Channel.Events, Reliability.Reliable, Packet(GameMsg)));
            Assert.True(clients[0].Session.SendTo(host.Id, Channel.Events, Reliability.Reliable, Packet(GameMsg)));
            net.Step(0.02);
            Assert.Empty(got);
        }

        [Fact]
        public void SendToSelf_IsDeliveredLocally_OnNextUpdate()
        {
            var net = new SessionTestNet();
            var (host, _) = net.StartWithClients(1);
            var got = Record(host, MessageType.ProgressClaim);

            Assert.True(host.Session.SendToHost(Channel.Save, Reliability.Reliable, Packet(MessageType.ProgressClaim, 5)));
            Assert.Empty(got);
            net.Step(0.01);

            var msg = Assert.Single(got);
            Assert.Equal(host.Id, msg.From);
            Assert.Equal(new byte[] { 5 }, msg.Payload);
        }

        [Fact]
        public void Sends_ReturnFalse_WhenNotActive()
        {
            var net = new SessionTestNet();
            var node = net.Add(1);
            Assert.False(node.Session.SendToHost(Channel.Events, Reliability.Reliable, Packet(GameMsg)));
            Assert.False(node.Session.SendTo(new PeerId(2), Channel.Events, Reliability.Reliable, Packet(GameMsg)));
            Assert.Equal(0, node.Session.Broadcast(Channel.Events, Reliability.Reliable, Packet(GameMsg)));

            node.Session.Connect(new PeerId(2));
            Assert.False(node.Session.SendToHost(Channel.Events, Reliability.Reliable, Packet(GameMsg)));
        }

        [Fact]
        public void Sends_RejectSessionTypesAndBadSizes()
        {
            var net = new SessionTestNet();
            var (host, clients) = net.StartWithClients(1);
            var s = clients[0].Session;

            Assert.ThrowsAny<ArgumentException>(() => s.SendToHost(Channel.Control, Reliability.Reliable, Packet(MessageType.Goodbye)));
            Assert.ThrowsAny<ArgumentException>(() => s.SendToHost(Channel.Control, Reliability.Reliable, Packet(0)));
            Assert.ThrowsAny<ArgumentException>(() => s.SendToHost(Channel.Control, Reliability.Reliable, Packet(200)));
            Assert.ThrowsAny<ArgumentException>(() => s.SendToHost(Channel.Control, Reliability.Reliable, new ArraySegment<byte>(new byte[0])));
            Assert.ThrowsAny<ArgumentException>(() => s.SendToHost((Channel)9, Reliability.Reliable, Packet(GameMsg)));
            Assert.ThrowsAny<ArgumentException>(() => s.SendToHost(Channel.State, Reliability.Unreliable,
                Packet(GameMsg, new byte[ProtocolInfo.MaxUnreliablePacketSize])));
            Assert.ThrowsAny<ArgumentException>(() => host.Session.Broadcast(Channel.Save, Reliability.Reliable,
                Packet(GameMsg, new byte[ProtocolInfo.MaxPacketSize])));
            Assert.Throws<ArgumentNullException>(() => s.SendToHost(Channel.Events, Reliability.Reliable, (NetWriter)null));

            Assert.True(s.SendToHost(Channel.Save, Reliability.Reliable, Packet(GameMsg, new byte[ProtocolInfo.MaxPacketSize - 1])));
        }

        [Fact]
        public void RegisterHandler_ValidatesTypes()
        {
            var net = new SessionTestNet();
            var s = net.Add(1).Session;
            Action<PeerId, NetReader> h = (_, __) => { };

            Assert.ThrowsAny<ArgumentException>(() => s.RegisterHandler(MessageType.Ping, h));
            Assert.ThrowsAny<ArgumentException>(() => s.RegisterHandler(0, h));
            Assert.ThrowsAny<ArgumentException>(() => s.RegisterHandler(128, h));
            Assert.Throws<ArgumentNullException>(() => s.RegisterHandler(GameMsg, null));

            s.RegisterHandler(GameMsg, h);
            Assert.Throws<InvalidOperationException>(() => s.RegisterHandler(GameMsg, h));
            Assert.True(s.UnregisterHandler(GameMsg));
            Assert.False(s.UnregisterHandler(GameMsg));
            s.RegisterHandler(GameMsg, h);
        }

        [Fact]
        public void HandlerFailures_DoNotStopLaterMessages()
        {
            var net = new SessionTestNet();
            var (host, clients) = net.StartWithClients(1);
            var seen = new List<byte>();
            host.Session.RegisterHandler(GameMsg, (from, r) =>
            {
                byte b = r.ReadByte();
                seen.Add(b);
                if (b == 1) r.ReadLong(); // overruns: ProtocolException
                if (b == 2) throw new InvalidOperationException("handler bug");
            });

            foreach (byte b in new byte[] { 1, 2, 3 })
                clients[0].Session.SendToHost(Channel.Events, Reliability.Reliable, Packet(GameMsg, b));
            net.Step(0.02);

            Assert.Equal(new byte[] { 1, 2, 3 }, seen);
            Assert.True(host.Session.IsActive);
        }

        [Fact]
        public void UnhandledTypes_AreDroppedQuietly()
        {
            var net = new SessionTestNet();
            var (host, clients) = net.StartWithClients(1);
            var got = Record(host, GameMsg);
            clients[0].Session.SendToHost(Channel.Events, Reliability.Reliable, Packet(GameMsg + 1));
            clients[0].Transport.Send(host.Id, Channel.Events, Reliability.Reliable, Packet(250));
            clients[0].Session.SendToHost(Channel.Events, Reliability.Reliable, Packet(GameMsg));
            net.Step(0.02);
            Assert.Single(got);
            Assert.True(host.Session.TryGetPlayer(clients[0].Id, out _));
        }

        [Fact]
        public void Host_IgnoresMessagesFromPeersWithoutHandshake()
        {
            var net = new SessionTestNet();
            var host = net.Add(1);
            var raw = net.AddRaw(50);
            host.Session.StartHost();
            var got = Record(host, GameMsg);

            raw.Send(host.Id, Wire.Game(GameMsg, 1));
            var ping = PacketBuilder.Begin(MessageType.Ping);
            ping.WriteDouble(0);
            raw.Send(host.Id, ping);
            raw.Send(host.Id, Wire.Game(MessageType.Goodbye));
            net.Step(0.02);

            Assert.Empty(got);
            Assert.Empty(raw.Drain());

            // A rejected peer stays outside the session.
            raw.Send(host.Id, Wire.Hello(modVersion: "bad"));
            raw.Send(host.Id, Wire.Game(GameMsg, 2));
            net.Step(0.02);
            Assert.Empty(got);
            Assert.Single(raw.Drain(MessageType.Reject));
            Assert.Empty(host.Joined);
        }

        [Fact]
        public void Client_IgnoresEverythingFromNonHostPeers()
        {
            var net = new SessionTestNet();
            var (host, clients) = net.StartWithClients(1);
            var intruder = net.AddRaw(66);
            var got = Record(clients[0], GameMsg);

            intruder.Send(clients[0].Id, Wire.Game(GameMsg, 1));
            var ended = PacketBuilder.Begin(MessageType.SessionEnded);
            ended.WriteByte((byte)SessionEndReason.Kicked);
            ended.WriteString("fake");
            intruder.Send(clients[0].Id, ended);
            intruder.Send(clients[0].Id, Wire.RosterUpdate((intruder.Id, 0, "Fake host")));
            intruder.Send(clients[0].Id, Wire.Game(MessageType.Goodbye));
            net.Step(0.02);

            Assert.Empty(got);
            Assert.True(clients[0].Session.IsActive);
            Assert.Empty(clients[0].Ended);
            Assert.Equal(2, clients[0].Session.Players.Count);
        }

        [Fact]
        public void Client_DoesNotDispatchWhileConnecting_FromOtherPeers()
        {
            var net = new SessionTestNet();
            var rawHost = net.AddRaw(1);
            var intruder = net.AddRaw(66);
            var client = net.Add(2);
            var got = Record(client, GameMsg);
            client.Session.Connect(rawHost.Id);
            intruder.Send(client.Id, Wire.Game(GameMsg, 1));
            intruder.Send(client.Id, Wire.Welcome(1, (intruder.Id, 0, "X"), (client.Id, 1, "C")));
            net.Step(0.02);
            rawHost.Send(client.Id, Wire.Welcome(1, (rawHost.Id, 0, "H"), (client.Id, 1, "C")));
            net.Step(0.02);

            Assert.True(client.Session.IsActive);
            Assert.Equal(rawHost.Id, client.Session.HostId);
            Assert.Empty(got);
        }
    }
}
