using System;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Session;
using Xunit;

namespace NGGMultiplayer.Tests.SessionTests
{
    public class SessionMalformedTests
    {
        [Fact]
        public void TruncatedAndOversizedHandshakeFields_AreDropped()
        {
            var net = new SessionTestNet();
            var host = net.Add(1);
            var raw = net.AddRaw(50);
            host.Session.StartHost();

            raw.Send(host.Id, new byte[] { MessageType.Hello });
            raw.Send(host.Id, new byte[] { MessageType.Hello, 1, 0 });
            var hugeName = PacketBuilder.Begin(MessageType.Hello);
            hugeName.WriteInt(ProtocolInfo.Version);
            hugeName.WriteString(ProtocolInfo.ModVersion);
            hugeName.WriteString("");
            hugeName.WriteString(new string('n', 5000));
            raw.Send(host.Id, hugeName);
            var lyingLength = PacketBuilder.Begin(MessageType.Hello);
            lyingLength.WriteInt(ProtocolInfo.Version);
            lyingLength.WriteVarUInt(60);
            raw.Send(host.Id, lyingLength);
            net.Step(0.02);

            Assert.Empty(raw.Drain());
            Assert.Empty(host.Joined);
            Assert.True(host.Session.IsActive);

            raw.Send(host.Id, Wire.Hello());
            net.Step(0.02);
            Assert.Single(host.Joined);
        }

        [Fact]
        public void MalformedRosters_AreRejected_ByClient()
        {
            var net = new SessionTestNet();
            var rawHost = net.AddRaw(1);
            var client = net.Add(2);
            client.Session.Connect(rawHost.Id);

            var tooMany = PacketBuilder.Begin(MessageType.Welcome);
            tooMany.WriteByte(1);
            tooMany.WriteVarUInt(1000);
            rawHost.Send(client.Id, tooMany);
            rawHost.Send(client.Id, Wire.Welcome(1, (rawHost.Id, 0, "H"), (client.Id, 1, "C"), (client.Id, 2, "Dup")));
            rawHost.Send(client.Id, Wire.Welcome(1, (rawHost.Id, 0, "H"), (client.Id, 1, "C"), (new PeerId(5), 1, "SameIndex")));
            rawHost.Send(client.Id, Wire.Welcome(1, (rawHost.Id, 0, "H"), (client.Id, 1, "C"), (PeerId.None, 2, "Zero")));
            rawHost.Send(client.Id, Wire.Welcome(1, (rawHost.Id, 0, "H"), (client.Id, 1, "C"), (new PeerId(5), 200, "Range")));
            var truncated = Wire.Welcome(1, (rawHost.Id, 0, "H"), (client.Id, 1, "C")).ToArray();
            rawHost.Send(client.Id, truncated.AsSpan(0, truncated.Length - 1).ToArray());
            net.Step(0.02);

            Assert.Equal(SessionState.Connecting, client.Session.State);

            rawHost.Send(client.Id, Wire.Welcome(1, (rawHost.Id, 0, "H"), (client.Id, 1, "C")));
            net.Step(0.02);
            Assert.True(client.Session.IsActive);

            var badUpdate = PacketBuilder.Begin(MessageType.RosterUpdate);
            badUpdate.WriteVarUInt(0);
            rawHost.Send(client.Id, badUpdate);
            rawHost.Send(client.Id, Wire.RosterUpdate((client.Id, 1, "C"))); // no host listed
            net.Step(0.02);
            Assert.True(client.Session.IsActive);
            Assert.Equal(2, client.Session.Players.Count);
        }

        [Fact]
        public void PingPongGarbage_DoesNotCorruptClock()
        {
            var net = new SessionTestNet();
            var rawHost = net.AddRaw(1);
            var client = net.Add(2);
            client.Session.Connect(rawHost.Id);
            rawHost.Send(client.Id, Wire.Welcome(1, (rawHost.Id, 0, "H"), (client.Id, 1, "C")));
            net.Step(0.02);

            foreach (var (sent, remote) in new[] { (double.NaN, 1.0), (1e9, 1.0), (-1e9, 1.0), (0.0, double.PositiveInfinity) })
            {
                var pong = PacketBuilder.Begin(MessageType.Pong);
                pong.WriteDouble(sent);
                pong.WriteDouble(remote);
                rawHost.Send(client.Id, pong);
            }
            rawHost.Send(client.Id, new byte[] { MessageType.Pong, 1, 2 });
            net.Step(0.02);

            Assert.False(client.Session.Clock.HasSamples);
            Assert.True(client.Session.IsActive);
        }

        [Fact]
        public void RandomGarbage_NeverEscapesUpdate()
        {
            var net = new SessionTestNet(seed: 4);
            var (host, clients) = net.StartWithClients(2);
            var stranger = net.AddRaw(77);
            host.Session.RegisterHandler(MessageType.GameFirst, (_, r) => r.ReadString());
            clients[1].Session.RegisterHandler(MessageType.GameFirst, (_, r) => r.ReadVector3());
            var random = new Random(2024);

            for (int i = 0; i < 4000; i++)
            {
                var data = new byte[random.Next(0, 40)];
                random.NextBytes(data);
                if (data.Length > 0 && random.Next(3) == 0)
                    data[0] = (byte)random.Next(1, 9); // bias towards session messages
                var seg = new ArraySegment<byte>(data);
                switch (i % 4)
                {
                    case 0: clients[0].Transport.Send(host.Id, Channel.Control, Reliability.Reliable, seg); break;
                    case 1: stranger.Transport.Send(host.Id, Channel.Events, Reliability.Reliable, seg); break;
                    case 2: stranger.Transport.Send(clients[1].Id, Channel.Control, Reliability.Reliable, seg); break;
                    case 3: host.Transport.Send(clients[1].Id, Channel.Control, Reliability.Reliable, seg); break;
                }
                if (i % 50 == 0)
                    net.Step(0.01);
            }
            net.Step(0.1);

            // Garbage from a member may legitimately parse as Goodbye/SessionEnded; the host itself must survive.
            Assert.True(host.Session.IsActive);
            Assert.Equal(SessionRole.Host, host.Session.Role);
        }

        [Fact]
        public void EmptyPacket_FromMember_IsIgnored()
        {
            var net = new SessionTestNet();
            var (host, clients) = net.StartWithClients(1);
            clients[0].Transport.Send(host.Id, Channel.Control, Reliability.Reliable, new ArraySegment<byte>(new byte[0]));
            host.Transport.Send(clients[0].Id, Channel.Control, Reliability.Reliable, new ArraySegment<byte>(new byte[0]));
            net.Step(0.02);
            Assert.True(host.Session.IsActive);
            Assert.True(clients[0].Session.IsActive);
            Assert.Equal(2, host.Session.Players.Count);
        }
    }
}
