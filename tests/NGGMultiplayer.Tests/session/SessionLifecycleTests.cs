using System;
using System.Linq;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Session;
using Xunit;

namespace NGGMultiplayer.Tests.SessionTests
{
    public class SessionLifecycleTests
    {
        [Fact]
        public void Heartbeats_KeepSessionAlive_WithLatencyAndLoss()
        {
            var net = new SessionTestNet(seed: 3);
            net.Hub.LatencyMs = 60;
            net.Hub.JitterMs = 20;
            net.Hub.UnreliableLossPercent = 20;
            var (host, clients) = net.StartWithClients(2);

            net.Step(60, dt: 0.02);

            Assert.True(host.Session.IsActive);
            Assert.All(clients, c => Assert.True(c.Session.IsActive));
            Assert.Empty(host.Left);
            Assert.All(clients, c => Assert.Empty(c.Ended));
        }

        [Fact]
        public void SilentPeers_TimeOut_OnBothSides()
        {
            var net = new SessionTestNet();
            var (host, clients) = net.StartWithClients(2);
            var lost = clients[0];
            var other = clients[1];

            net.Hub.SetLinkDown(host.Id, lost.Id, true);
            net.Step(8.5);
            Assert.True(lost.Session.IsActive);
            Assert.Empty(host.Left);

            net.Step(2.0);
            var left = Assert.Single(host.Left);
            Assert.Equal(lost.Id, left.Player.Id);
            Assert.Equal(PlayerLeaveReason.TimedOut, left.Reason);
            Assert.False(host.Session.TryGetPlayer(lost.Id, out _));

            var end = Assert.Single(lost.Ended);
            Assert.Equal(SessionEndReason.HostTimedOut, end.Reason);
            Assert.Equal(SessionState.Idle, lost.Session.State);
            Assert.Empty(lost.Session.Players);

            net.Step(0.05);
            var otherLeft = Assert.Single(other.Left);
            Assert.Equal(lost.Id, otherLeft.Player.Id);
            Assert.Equal(PlayerLeaveReason.Unknown, otherLeft.Reason);
            Assert.True(other.Session.IsActive);
            Assert.Equal(2, other.Session.Players.Count);
        }

        [Fact]
        public void Timeout_IsConfigurable()
        {
            var net = new SessionTestNet();
            var (host, clients) = net.StartWithClients(1, c => c.TimeoutSeconds = 3);
            clients[0].Paused = true;

            net.Step(2.5);
            Assert.Empty(host.Left);
            net.Step(1.0);
            Assert.Equal(PlayerLeaveReason.TimedOut, Assert.Single(host.Left).Reason);
        }

        [Fact]
        public void HostLeave_EndsSessionForEveryone()
        {
            var net = new SessionTestNet();
            var (host, clients) = net.StartWithClients(2);

            host.Session.Leave();
            Assert.Equal(SessionEndReason.LocalLeft, Assert.Single(host.Ended).Reason);
            Assert.Equal(SessionState.Idle, host.Session.State);
            Assert.Equal(SessionRole.None, host.Session.Role);
            Assert.Empty(host.Session.Players);
            Assert.Null(host.Session.LocalPlayer);

            net.Step(0.05);
            foreach (var c in clients)
            {
                var end = Assert.Single(c.Ended);
                Assert.Equal(SessionEndReason.HostLeft, end.Reason);
                Assert.Equal(SessionState.Idle, c.Session.State);
                Assert.Empty(c.Left);
            }

            host.Session.Leave();
            Assert.Single(host.Ended);
        }

        [Fact]
        public void HostVanishing_EndsClientSessionsByTimeout()
        {
            var net = new SessionTestNet();
            var (host, clients) = net.StartWithClients(2);
            host.Transport.Dispose();

            net.Step(11);
            Assert.All(clients, c => Assert.Equal(SessionEndReason.HostTimedOut, Assert.Single(c.Ended).Reason));
        }

        [Fact]
        public void ClientLeave_SendsGoodbye_AndOthersSeeIt()
        {
            var net = new SessionTestNet();
            var (host, clients) = net.StartWithClients(2);

            clients[0].Session.Leave();
            Assert.Equal(SessionEndReason.LocalLeft, Assert.Single(clients[0].Ended).Reason);
            net.Step(0.05);

            var left = Assert.Single(host.Left);
            Assert.Equal(clients[0].Id, left.Player.Id);
            Assert.Equal(PlayerLeaveReason.Left, left.Reason);
            Assert.Equal(clients[0].Id, Assert.Single(clients[1].Left).Player.Id);
            Assert.Equal(2, clients[1].Session.Players.Count);
        }

        [Fact]
        public void Kick_RemovesClient_AndBansRejoin()
        {
            var net = new SessionTestNet();
            var (host, clients) = net.StartWithClients(2);

            Assert.True(host.Session.Kick(clients[0].Id, "Be nice."));
            Assert.False(host.Session.Kick(clients[0].Id));
            Assert.False(clients[1].Session.Kick(host.Id));
            Assert.Equal(PlayerLeaveReason.Kicked, Assert.Single(host.Left).Reason);
            net.Step(0.05);

            var end = Assert.Single(clients[0].Ended);
            Assert.Equal(SessionEndReason.Kicked, end.Reason);
            Assert.Equal("Be nice.", end.Message);
            Assert.Single(clients[1].Left);

            clients[0].Session.Connect(host.Id);
            net.Step(0.05);
            Assert.Equal(RejectReason.NotAllowed, clients[0].Ended[1].RejectReason);
        }

        [Fact]
        public void RosterUpdate_WithoutUs_EndsSession()
        {
            var net = new SessionTestNet();
            var rawHost = net.AddRaw(1);
            var client = net.Add(2);
            client.Session.Connect(rawHost.Id);
            rawHost.Send(client.Id, Wire.Welcome(1, (rawHost.Id, 0, "H"), (client.Id, 1, "C")));
            net.Step(0.01);
            Assert.True(client.Session.IsActive);

            rawHost.Send(client.Id, Wire.RosterUpdate((rawHost.Id, 0, "H")));
            net.Step(0.01);
            Assert.Equal(SessionEndReason.DroppedByHost, Assert.Single(client.Ended).Reason);
        }

        [Fact]
        public void RosterUpdate_RenamesInPlace_AndKeepsInstances()
        {
            var net = new SessionTestNet();
            var rawHost = net.AddRaw(1);
            var client = net.Add(2);
            client.Session.Connect(rawHost.Id);
            rawHost.Send(client.Id, Wire.Welcome(1, (rawHost.Id, 0, "H"), (client.Id, 1, "C")));
            net.Step(0.01);
            var hostInfo = client.Session.Players[0];

            rawHost.Send(client.Id, Wire.RosterUpdate((rawHost.Id, 0, "Renamed"), (client.Id, 1, "C"), (new PeerId(7), 2, "New")));
            net.Step(0.01);

            Assert.Same(hostInfo, client.Session.Players[0]);
            Assert.Equal("Renamed", hostInfo.DisplayName);
            Assert.Equal(new PeerId(7), client.Joined.Last().Id);
            Assert.Equal(3, client.Session.Players.Count);
        }

        [Fact]
        public void SessionCanBeRestarted_AfterEnding()
        {
            var net = new SessionTestNet();
            var (host, clients) = net.StartWithClients(1);
            host.Session.Leave();
            net.Step(0.05);

            host.Session.StartHost();
            clients[0].Session.Connect(host.Id);
            net.Step(0.05);

            Assert.True(clients[0].Session.IsActive);
            Assert.Equal(2, host.StartedCount);
            Assert.Equal(2, clients[0].StartedCount);
            Assert.Equal(2, host.Session.Players.Count);
        }

        [Fact]
        public void Reentrancy_LeaveInsideEventHandlers_IsSafe()
        {
            var net = new SessionTestNet();
            var host = net.Add(1);
            var client = net.Add(2);
            client.Session.PlayerJoined += _ => client.Session.Leave();
            host.Session.SessionEnded += info =>
            {
                if (info.Reason == SessionEndReason.LocalLeft && host.Ended.Count == 1)
                    host.Session.StartHost();
            };
            host.Session.StartHost();
            client.Session.Connect(host.Id);
            net.Step(0.05);

            Assert.Equal(SessionEndReason.LocalLeft, Assert.Single(client.Ended).Reason);
            Assert.Equal(PlayerLeaveReason.Left, Assert.Single(host.Left).Reason);

            host.Session.Leave();
            Assert.True(host.Session.IsActive); // restarted from its own SessionEnded handler
            Assert.Equal(2, host.StartedCount);
        }

        [Fact]
        public void Update_IsNotReentrant_ButHandlerExceptionsAreContained()
        {
            var net = new SessionTestNet();
            var host = net.Add(1);
            var client = net.Add(2);
            Exception caught = null;
            host.Session.PlayerJoined += _ =>
            {
                try { host.Session.Update(net.Hub.Now); }
                catch (Exception ex) { caught = ex; }
                throw new InvalidOperationException("subscriber bug");
            };
            host.Session.StartHost();
            client.Session.Connect(host.Id);
            net.Step(0.05);

            Assert.IsType<InvalidOperationException>(caught);
            Assert.True(client.Session.IsActive);
            Assert.Equal(2, host.Session.Players.Count);
        }

        [Fact]
        public void Dispose_LeavesAndBlocksFurtherUse()
        {
            var net = new SessionTestNet();
            var (host, clients) = net.StartWithClients(1);
            clients[0].Session.Dispose();
            clients[0].Session.Dispose();

            Assert.Equal(SessionEndReason.LocalLeft, Assert.Single(clients[0].Ended).Reason);
            Assert.Throws<ObjectDisposedException>(() => clients[0].Session.Update(1));
            clients[0].Paused = true;
            net.Step(0.05);
            Assert.Equal(PlayerLeaveReason.Left, Assert.Single(host.Left).Reason);
        }

        [Fact]
        public void Leave_WithDisposedTransport_StillEnds()
        {
            var net = new SessionTestNet();
            var (host, _) = net.StartWithClients(1);
            host.Transport.Dispose();
            host.Session.Leave();
            Assert.Equal(SessionState.Idle, host.Session.State);
            Assert.Single(host.Ended);
        }

        [Fact]
        public void Time_GoingBackwards_IsIgnored()
        {
            var net = new SessionTestNet();
            var host = net.Add(1);
            host.Session.Update(10);
            host.Session.Update(5);
            Assert.Equal(10, host.Session.Now);
        }

        [Fact]
        public void RttAndHostClock_AreEstimated_UnderLatency()
        {
            var net = new SessionTestNet();
            net.Hub.LatencyMs = 50;
            var host = net.Add(1);
            var client = net.Add(2);
            var other = net.Add(3);
            client.ClockOffset = 1000; // the client's clock reads 1000 s ahead of the host's
            host.Session.StartHost();
            client.Session.Connect(host.Id);
            other.Session.Connect(host.Id);

            net.Step(6, dt: 0.001);

            var clock = client.Session.Clock;
            Assert.True(clock.SampleCount >= 4);
            Assert.InRange(clock.Rtt, 0.099, 0.101);
            Assert.InRange(clock.Offset, -1000.001, -999.999);
            Assert.InRange(client.Session.HostTime - host.Session.Now, -0.001, 0.001);
            Assert.InRange(client.Session.LocalPlayer.PingMs, 99, 101);

            Assert.True(host.Session.TryGetPlayer(client.Id, out var onHost));
            Assert.InRange(onHost.PingMs, 99, 101);
            Assert.Equal(0, host.Session.LocalPlayer.PingMs);
            Assert.Equal(host.Session.Now, host.Session.HostTime);

            // Roster refreshes carry the host's measurements to the other clients.
            Assert.True(other.Session.TryGetPlayer(client.Id, out var seenByOther));
            Assert.InRange(seenByOther.PingMs, 99, 101);
        }

        [Fact]
        public void RttEstimate_IsStable_UnderJitter()
        {
            var net = new SessionTestNet(seed: 11);
            net.Hub.LatencyMs = 40;
            net.Hub.JitterMs = 20; // mean one-way 50 ms
            var host = net.Add(1);
            var client = net.Add(2);
            client.ClockOffset = -250;
            host.Session.StartHost();
            client.Session.Connect(host.Id);

            net.Step(30, dt: 0.002);

            Assert.InRange(client.Session.Clock.Rtt, 0.085, 0.115);
            Assert.InRange(client.Session.Clock.Offset, 250 - 0.012, 250 + 0.012);
        }

        [Fact]
        public void PlayerIndices_StayWithinMaxPlayers()
        {
            var net = new SessionTestNet();
            var (host, clients) = net.StartWithClients(7);
            Assert.Equal(Enumerable.Range(0, 8), host.Session.Players.Select(p => p.PlayerIndex));
            Assert.Equal(8, clients.Last().Session.Players.Count);

            var ninth = net.Add(20);
            ninth.Session.Connect(host.Id);
            net.Step(0.05);
            Assert.Equal(RejectReason.SessionFull, Assert.Single(ninth.Ended).RejectReason);
            Assert.Contains("8", ninth.Ended[0].Message);
        }

        [Fact]
        public void RosterAndSessionMessages_UseControlChannel()
        {
            var net = new SessionTestNet();
            var host = net.Add(1);
            var raw = net.AddRaw(50);
            host.Session.StartHost();
            raw.Send(host.Id, Wire.Hello());
            net.Step(0.02);

            var got = raw.Drain();
            Assert.Contains(got, p => p.Data[0] == MessageType.Welcome);
            Assert.All(got, p => Assert.Equal(Channel.Control, p.Channel));
        }
    }
}
