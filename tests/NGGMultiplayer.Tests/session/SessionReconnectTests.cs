using System;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Session;
using Xunit;

namespace NGGMultiplayer.Tests.SessionTests
{
    public class SessionReconnectTests
    {
        [Fact]
        public void HelloWithNewNonce_FromListedPeer_ReplacesItsSession()
        {
            var net = new SessionTestNet();
            var host = net.Add(1);
            var raw = net.AddRaw(50);
            host.Session.StartHost();

            raw.Send(host.Id, Wire.Hello(name: "First", nonce: 7));
            net.Step(0.02);
            Assert.Single(host.Joined);

            raw.Send(host.Id, Wire.Hello(name: "Again", nonce: 8));
            net.Step(0.02);

            var left = Assert.Single(host.Left);
            Assert.Equal(raw.Id, left.Player.Id);
            Assert.Equal(PlayerLeaveReason.Left, left.Reason);
            Assert.Equal(2, host.Joined.Count);
            Assert.Equal("Again", host.Joined[1].DisplayName);
            Assert.Equal(2, host.Session.Players.Count);
            Assert.True(host.Session.IsPeerConnected(raw.Id));
            Assert.Equal(new[] { "joined:" + raw.Id, "left:" + raw.Id + ":Left", "joined:" + raw.Id },
                host.Log.FindAll(l => l.StartsWith("joined") || l.StartsWith("left")));
        }

        [Fact]
        public void ClientThatGaveUp_ReconnectsBeforeHostTimesItOut_AndIsJoinedAfresh()
        {
            var net = new SessionTestNet();
            var host = net.Add(1, c => c.TimeoutSeconds = 30);
            var client = net.Add(2, c => c.TimeoutSeconds = 3);
            host.Session.StartHost();
            client.Session.Connect(host.Id);
            Assert.True(net.RunUntil(() => client.Session.IsActive, 2));
            Assert.Single(host.Joined);

            net.Hub.SetLinkDown(host.Id, client.Id, true);
            Assert.True(net.RunUntil(() => client.Ended.Count == 1, 5));
            Assert.Equal(SessionEndReason.HostTimedOut, client.Ended[0].Reason);
            Assert.True(host.Session.IsPeerConnected(client.Id), "the host has not timed the client out yet");

            net.Hub.SetLinkDown(host.Id, client.Id, false);
            client.Session.Connect(host.Id);
            Assert.True(net.RunUntil(() => client.Session.IsActive, 2));

            Assert.Single(host.Left);
            Assert.Equal(2, host.Joined.Count);
            Assert.Equal(2, host.Session.Players.Count);
            net.Step(1);
            Assert.True(client.Session.IsActive);
            Assert.Single(host.Left);
        }

        [Fact]
        public void HelloRetries_KeepTheirNonce()
        {
            var net = new SessionTestNet();
            var rawHost = net.AddRaw(1);
            var client = net.Add(2);
            client.Session.Connect(rawHost.Id);
            net.Step(2.5);

            var hellos = rawHost.Drain(MessageType.Hello);
            Assert.True(hellos.Count >= 2);
            uint first = ReadNonce(hellos[0].Data);
            Assert.All(hellos, h => Assert.Equal(first, ReadNonce(h.Data)));

            client.Session.Leave();
            client.Session.Connect(rawHost.Id);
            net.Step(0.02);
            Assert.NotEqual(first, ReadNonce(Assert.Single(rawHost.Drain(MessageType.Hello)).Data));
        }

        [Fact]
        public void HelloWithoutNonce_IsDropped()
        {
            var net = new SessionTestNet();
            var host = net.Add(1);
            var raw = net.AddRaw(50);
            host.Session.StartHost();

            var w = PacketBuilder.Begin(MessageType.Hello);
            w.WriteInt(ProtocolInfo.Version);
            w.WriteString(ProtocolInfo.ModVersion);
            w.WriteString("");
            w.WriteString("NoNonce");
            raw.Send(host.Id, w);
            net.Step(0.02);

            Assert.Empty(host.Joined);
            Assert.Empty(raw.Drain());
        }

        [Fact]
        public void ReportLinkLost_OnHost_DropsThatClientAsTimedOut()
        {
            var net = new SessionTestNet();
            var (host, clients) = net.StartWithClients(2);

            Assert.False(host.Session.ReportLinkLost(new PeerId(999)));
            Assert.True(host.Session.ReportLinkLost(clients[0].Id));

            var left = Assert.Single(host.Left);
            Assert.Equal(clients[0].Id, left.Player.Id);
            Assert.Equal(PlayerLeaveReason.TimedOut, left.Reason);
            Assert.False(host.Session.IsPeerConnected(clients[0].Id));
            Assert.True(host.Session.IsPeerConnected(clients[1].Id));
            Assert.False(host.Session.ReportLinkLost(clients[0].Id));

            net.Step(0.1);
            Assert.True(clients[1].Session.IsActive);
            Assert.Equal(2, clients[1].Session.Players.Count);
        }

        [Fact]
        public void ReportLinkLost_OnClient_EndsTheSessionWhenItIsTheHost()
        {
            var net = new SessionTestNet();
            var (host, clients) = net.StartWithClients(2);
            var client = clients[0];

            Assert.False(client.Session.ReportLinkLost(clients[1].Id));
            Assert.True(client.Session.IsActive);

            Assert.True(client.Session.ReportLinkLost(host.Id));
            var end = Assert.Single(client.Ended);
            Assert.Equal(SessionEndReason.HostTimedOut, end.Reason);
            Assert.Equal(SessionState.Idle, client.Session.State);
        }

        [Fact]
        public void ReportLinkLost_WhileConnecting_KeepsRetrying()
        {
            var net = new SessionTestNet();
            var host = net.Add(1);
            var client = net.Add(2);
            host.Session.StartHost();
            client.Session.Connect(host.Id);

            Assert.False(client.Session.ReportLinkLost(host.Id));
            Assert.True(net.RunUntil(() => client.Session.IsActive, 2));
        }

        [Fact]
        public void FirstPing_IsNotStampedBeforeAHitchInTheWelcomeUpdate()
        {
            var net = new SessionTestNet();
            net.Hub.LatencyMs = 20;
            var host = net.Add(1);
            var client = net.Add(2);
            client.ClockOffset = 500;
            host.Session.StartHost();
            // The game takes 1.5 s to react to the session starting (loading the host's save, spawning).
            client.Session.SessionStarted += () => net.Hub.Advance(1.5);
            client.Session.Connect(host.Id);

            Assert.True(net.RunUntil(() => client.Session.Clock.HasSamples, 5));
            Assert.InRange(client.Session.Clock.LastRtt, 0, 0.1);
            Assert.InRange(client.Session.Clock.Offset, -500 - 0.03, -500 + 0.03);
        }

        private static uint ReadNonce(byte[] hello)
        {
            var r = new NetReader(hello);
            r.ReadByte();
            r.ReadInt();
            r.ReadString();
            r.ReadString();
            r.ReadString();
            return r.ReadUInt();
        }
    }
}
