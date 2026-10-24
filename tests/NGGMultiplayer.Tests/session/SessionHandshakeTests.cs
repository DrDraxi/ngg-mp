using System;
using System.Linq;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Session;
using Xunit;

namespace NGGMultiplayer.Tests.SessionTests
{
    public class SessionHandshakeTests
    {
        [Fact]
        public void Client_Joins_BothSidesSeeRosterAndEvents()
        {
            var net = new SessionTestNet();
            var host = net.Add(1, c => c.DisplayName = "Host");
            var client = net.Add(2, c => c.DisplayName = "Alice");

            host.Session.StartHost();
            Assert.Equal(1, host.StartedCount);
            Assert.Equal(SessionState.Active, host.Session.State);
            Assert.True(host.Session.IsHost);
            Assert.Equal(host.Id, host.Session.HostId);
            Assert.Equal(0, host.Session.LocalPlayer.PlayerIndex);

            client.Session.Connect(host.Id);
            Assert.Equal(SessionState.Connecting, client.Session.State);
            net.Step(0.05);

            Assert.Equal(SessionState.Active, client.Session.State);
            Assert.True(client.Session.IsClient);
            Assert.Equal(1, client.StartedCount);
            Assert.Equal(host.Id, client.Session.HostId);
            Assert.Equal(1, client.Session.LocalPlayer.PlayerIndex);
            Assert.True(client.Session.LocalPlayer.IsLocal);
            Assert.Equal("Alice", client.Session.LocalPlayer.DisplayName);

            var hostJoined = Assert.Single(host.Joined);
            Assert.Equal(client.Id, hostJoined.Id);
            Assert.Equal("Alice", hostJoined.DisplayName);
            Assert.False(hostJoined.IsLocal);

            var clientJoined = Assert.Single(client.Joined);
            Assert.Equal(host.Id, clientJoined.Id);
            Assert.True(clientJoined.IsHost);
            Assert.Equal("Host", clientJoined.DisplayName);
            Assert.Equal(new[] { "started", "joined:1" }, client.Log);

            foreach (var node in new[] { host, client })
            {
                Assert.Equal(new[] { 0, 1 }, node.Session.Players.Select(p => p.PlayerIndex));
                Assert.Equal(new[] { host.Id, client.Id }, node.Session.Players.Select(p => p.Id));
                Assert.True(node.Session.TryGetPlayer(client.Id, out _));
            }
        }

        [Fact]
        public void SeveralClients_GetDistinctIndices_AndSeeEachOther()
        {
            var net = new SessionTestNet();
            var (host, clients) = net.StartWithClients(3);

            Assert.Equal(4, host.Session.Players.Count);
            Assert.Equal(new[] { 0, 1, 2, 3 }, host.Session.Players.Select(p => p.PlayerIndex));
            foreach (var c in clients)
            {
                Assert.Equal(host.Session.Players.Select(p => p.Id), c.Session.Players.Select(p => p.Id));
                Assert.Equal(3, c.Joined.Count);
                Assert.Equal(1, c.Joined.Count(p => p.IsHost));
                Assert.DoesNotContain(c.Joined, p => p.Id == c.Id);
                Assert.Empty(c.Left);
            }
            Assert.Equal(3, host.Joined.Count);
        }

        [Fact]
        public void Join_WorksUnderLatencyJitterAndLoss()
        {
            var net = new SessionTestNet(seed: 7);
            net.Hub.LatencyMs = 80;
            net.Hub.JitterMs = 40;
            net.Hub.UnreliableLossPercent = 30;
            var (host, clients) = net.StartWithClients(2);

            Assert.Equal(3, host.Session.Players.Count);
            Assert.All(clients, c => Assert.Equal(3, c.Session.Players.Count));
        }

        [Fact]
        public void Session_Full_IsRejected()
        {
            var net = new SessionTestNet();
            var host = net.Add(1, c => c.MaxPlayers = 2);
            var c1 = net.Add(2);
            var c2 = net.Add(3);
            host.Session.StartHost();
            c1.Session.Connect(host.Id);
            net.Step(0.05);
            c2.Session.Connect(host.Id);
            net.Step(0.05);

            Assert.True(c1.Session.IsActive);
            Assert.Equal(SessionState.Idle, c2.Session.State);
            Assert.Equal(SessionRole.None, c2.Session.Role);
            var end = Assert.Single(c2.Ended);
            Assert.Equal(SessionEndReason.Rejected, end.Reason);
            Assert.Equal(RejectReason.SessionFull, end.RejectReason);
            Assert.Equal(0, c2.StartedCount);
            Assert.Equal(2, host.Session.Players.Count);
            Assert.Single(host.Joined);
        }

        [Fact]
        public void FreedIndex_IsReused()
        {
            var net = new SessionTestNet();
            var (host, clients) = net.StartWithClients(2);
            clients[0].Session.Leave();
            net.Step(0.05);

            var late = net.Add(9);
            late.Session.Connect(host.Id);
            net.Step(0.05);

            Assert.Equal(1, late.Session.LocalPlayer.PlayerIndex);
            Assert.Equal(new[] { 0, 1, 2 }, host.Session.Players.Select(p => p.PlayerIndex));
        }

        [Fact]
        public void ProtocolMismatch_IsRejected_WithBothVersions()
        {
            var net = new SessionTestNet();
            var host = net.Add(1);
            var raw = net.AddRaw(50);
            host.Session.StartHost();

            raw.Send(host.Id, Wire.Hello(protocol: 999, modVersion: "9.9.9"));
            net.Step(0.02);

            var reject = Wire.ReadReject(Assert.Single(raw.Drain(MessageType.Reject)).Data);
            Assert.Equal(RejectReason.ProtocolMismatch, reject.Reason);
            Assert.Equal(ProtocolInfo.Version, reject.HostProtocol);
            Assert.Equal(ProtocolInfo.ModVersion, reject.HostMod);
            Assert.Contains("9.9.9", reject.Message);
            Assert.Contains(ProtocolInfo.ModVersion, reject.Message);
            Assert.Empty(host.Joined);
            Assert.Single(host.Session.Players);
        }

        [Fact]
        public void ProtocolMismatch_WithUnparseableTail_IsStillRejected()
        {
            var net = new SessionTestNet();
            var host = net.Add(1);
            var raw = net.AddRaw(50);
            host.Session.StartHost();

            var w = PacketBuilder.Begin(MessageType.Hello);
            w.WriteInt(ProtocolInfo.Version + 1);
            w.WriteByte(0xFF); // a future format we cannot parse
            raw.Send(host.Id, w);
            net.Step(0.02);

            var reject = Wire.ReadReject(Assert.Single(raw.Drain(MessageType.Reject)).Data);
            Assert.Equal(RejectReason.ProtocolMismatch, reject.Reason);
        }

        [Fact]
        public void ModVersionMismatch_IsRejected()
        {
            var net = new SessionTestNet();
            var host = net.Add(1);
            var raw = net.AddRaw(50);
            host.Session.StartHost();

            raw.Send(host.Id, Wire.Hello(modVersion: "0.0.1-old"));
            net.Step(0.02);

            var reject = Wire.ReadReject(Assert.Single(raw.Drain(MessageType.Reject)).Data);
            Assert.Equal(RejectReason.ModVersionMismatch, reject.Reason);
            Assert.Contains("0.0.1-old", reject.Message);
            Assert.Contains(ProtocolInfo.ModVersion, reject.Message);
            Assert.Empty(host.Joined);
        }

        [Fact]
        public void Client_ReportsRejectFromHost_WithReasonAndMessage()
        {
            var net = new SessionTestNet();
            var rawHost = net.AddRaw(1);
            var client = net.Add(2);
            client.Session.Connect(rawHost.Id);
            net.Step(0.02);

            Assert.Single(rawHost.Drain(MessageType.Hello));
            rawHost.Send(client.Id, Wire.Reject(RejectReason.ModVersionMismatch, "7.7.7", "Mod version mismatch: host has 7.7.7."));
            net.Step(0.02);

            var end = Assert.Single(client.Ended);
            Assert.Equal(SessionEndReason.Rejected, end.Reason);
            Assert.Equal(RejectReason.ModVersionMismatch, end.RejectReason);
            Assert.Contains("7.7.7", end.Message);
            Assert.Equal(SessionState.Idle, client.Session.State);
        }

        [Fact]
        public void Client_RejectWithoutMessage_GetsVersionText()
        {
            var net = new SessionTestNet();
            var rawHost = net.AddRaw(1);
            var client = net.Add(2);
            client.Session.Connect(rawHost.Id);
            rawHost.Send(client.Id, Wire.Reject(RejectReason.ProtocolMismatch, "3.0.0", "", hostProtocol: 3));
            net.Step(0.02);

            var end = Assert.Single(client.Ended);
            Assert.Equal(RejectReason.ProtocolMismatch, end.RejectReason);
            Assert.Contains("3.0.0", end.Message);
            Assert.Contains(ProtocolInfo.ModVersion, end.Message);
        }

        [Fact]
        public void GameBuildMismatch_IsRejected_UnlessDisabled()
        {
            var net = new SessionTestNet();
            var host = net.Add(1, c => c.GameBuild = "1.0");
            var client = net.Add(2, c => c.GameBuild = "1.1");
            host.Session.StartHost();
            client.Session.Connect(host.Id);
            net.Step(0.05);

            var end = Assert.Single(client.Ended);
            Assert.Equal(RejectReason.GameBuildMismatch, end.RejectReason);
            Assert.Contains("1.0", end.Message);

            host.Session.Config.RequireMatchingGameBuild = false;
            client.Session.Connect(host.Id);
            net.Step(0.05);
            Assert.True(client.Session.IsActive);
        }

        [Fact]
        public void AdmissionFilter_RejectsPeersNotAllowed()
        {
            var net = new SessionTestNet();
            var host = net.Add(1);
            var allowed = net.Add(2);
            var stranger = net.Add(3);
            host.Session.AdmissionFilter = id => id == allowed.Id;
            host.Session.StartHost();
            allowed.Session.Connect(host.Id);
            stranger.Session.Connect(host.Id);
            net.Step(0.05);

            Assert.True(allowed.Session.IsActive);
            var end = Assert.Single(stranger.Ended);
            Assert.Equal(RejectReason.NotAllowed, end.RejectReason);
            Assert.Equal(2, host.Session.Players.Count);
        }

        [Fact]
        public void AdmissionFilter_ThatThrows_Refuses()
        {
            var net = new SessionTestNet();
            var host = net.Add(1);
            var client = net.Add(2);
            host.Session.AdmissionFilter = _ => throw new InvalidOperationException("lobby lookup failed");
            host.Session.StartHost();
            client.Session.Connect(host.Id);
            net.Step(0.05);

            Assert.Equal(RejectReason.NotAllowed, Assert.Single(client.Ended).RejectReason);
            Assert.True(host.Session.IsActive);
        }

        [Fact]
        public void DuplicateHello_DoesNotCreateSecondPlayer_AndResendsWelcome()
        {
            var net = new SessionTestNet();
            var host = net.Add(1);
            var raw = net.AddRaw(50);
            host.Session.StartHost();

            raw.Send(host.Id, Wire.Hello(name: "Dup"));
            raw.Send(host.Id, Wire.Hello(name: "Dup"));
            net.Step(0.02);

            Assert.Single(host.Joined);
            Assert.Equal(2, host.Session.Players.Count);
            var welcomes = raw.Drain(MessageType.Welcome);
            Assert.Equal(2, welcomes.Count);
            Assert.All(welcomes, p => Assert.Equal(1, p.Data[1]));
        }

        [Fact]
        public void HelloIsRetried_WhenTheFirstIsLost()
        {
            var net = new SessionTestNet();
            var host = net.Add(1);
            var client = net.Add(2);
            host.Session.StartHost();
            net.Hub.SetLinkDown(host.Id, client.Id, true);
            client.Session.Connect(host.Id);
            net.Step(2.5);
            Assert.Equal(SessionState.Connecting, client.Session.State);

            net.Hub.SetLinkDown(host.Id, client.Id, false);
            net.Step(1.1);
            Assert.True(client.Session.IsActive);
            Assert.Single(host.Joined);
        }

        [Fact]
        public void ConnectTimesOut_WhenHostNeverAnswers()
        {
            var net = new SessionTestNet();
            var client = net.Add(2, c => c.ConnectTimeoutSeconds = 5);
            client.Session.Connect(new PeerId(999));

            net.Step(4.9);
            Assert.Equal(SessionState.Connecting, client.Session.State);
            Assert.Empty(client.Ended);

            net.Step(0.2);
            var end = Assert.Single(client.Ended);
            Assert.Equal(SessionEndReason.ConnectTimedOut, end.Reason);
            Assert.Equal(SessionState.Idle, client.Session.State);
            Assert.InRange(net.Hub.PacketsDropped, 5, 7); // initial Hello plus one retry per second
        }

        [Fact]
        public void DisplayName_IsSanitized_AndDefaulted()
        {
            var net = new SessionTestNet();
            var host = net.Add(1);
            var spoofer = net.Add(2, c => c.DisplayName = "  Bob‮\u0007\n  ");
            var longName = net.Add(3, c => c.DisplayName = new string('x', 100));
            var empty = net.Add(4, c => c.DisplayName = "​\t");
            host.Session.StartHost();
            spoofer.Session.Connect(host.Id);
            net.Step(0.03);
            longName.Session.Connect(host.Id);
            net.Step(0.03);
            empty.Session.Connect(host.Id);
            net.Step(0.03);

            Assert.True(host.Session.TryGetPlayer(spoofer.Id, out var a));
            Assert.Equal("Bob", a.DisplayName);
            Assert.True(host.Session.TryGetPlayer(longName.Id, out var b));
            Assert.Equal(SessionConfig.MaxDisplayNameChars, b.DisplayName.Length);
            Assert.True(host.Session.TryGetPlayer(empty.Id, out var c));
            Assert.Equal("Player 4", c.DisplayName);
        }

        [Fact]
        public void EarlyGameplayPacket_BeforeWelcome_IsDeliveredAfterJoin()
        {
            var net = new SessionTestNet();
            var rawHost = net.AddRaw(1);
            var client = net.Add(2);
            client.Session.RegisterHandler(MessageType.SaveBegin, (from, r) => client.Log.Add("save:" + from + ":" + r.ReadByte()));
            client.Session.Connect(rawHost.Id);
            net.Step(0.01);
            Assert.Single(rawHost.Drain(MessageType.Hello));

            // Another channel may overtake the Welcome on the Control channel.
            rawHost.Send(client.Id, Wire.Game(MessageType.SaveBegin, 42), Channel.Save);
            net.Step(0.01);
            Assert.Equal(SessionState.Connecting, client.Session.State);
            Assert.DoesNotContain(client.Log, s => s.StartsWith("save"));

            rawHost.Send(client.Id, Wire.Welcome(1, (rawHost.Id, 0, "H"), (client.Id, 1, "C")));
            net.Step(0.01);

            Assert.Equal(new[] { "started", "joined:1", "save:1:42" }, client.Log);
        }

        [Fact]
        public void Welcome_ThatOmitsUs_IsIgnored()
        {
            var net = new SessionTestNet();
            var rawHost = net.AddRaw(1);
            var client = net.Add(2);
            client.Session.Connect(rawHost.Id);
            rawHost.Send(client.Id, Wire.Welcome(1, (rawHost.Id, 0, "H"), (new PeerId(77), 1, "Other")));
            rawHost.Send(client.Id, Wire.Welcome(2, (rawHost.Id, 0, "H"), (client.Id, 1, "C"))); // wrong index
            net.Step(0.02);

            Assert.Equal(SessionState.Connecting, client.Session.State);
            Assert.Equal(0, client.StartedCount);
        }

        [Fact]
        public void ApiMisuse_Throws()
        {
            var net = new SessionTestNet();
            var host = net.Add(1);
            var client = net.Add(2);

            Assert.Throws<ArgumentException>(() => client.Session.Connect(PeerId.None));
            Assert.Throws<ArgumentException>(() => client.Session.Connect(client.Id));
            host.Session.StartHost();
            Assert.Throws<InvalidOperationException>(() => host.Session.StartHost());
            Assert.Throws<InvalidOperationException>(() => host.Session.Connect(client.Id));
            Assert.Throws<ArgumentOutOfRangeException>(() => host.Session.Update(double.NaN));
            Assert.Throws<ArgumentNullException>(() => new SessionManager(null));
            Assert.Throws<ArgumentException>(() => new SessionManager(net.AddRaw(9).Transport, new SessionConfig { MaxPlayers = 0 }));
            Assert.Throws<ArgumentException>(() => new SessionManager(net.AddRaw(10).Transport, new SessionConfig { TimeoutSeconds = double.NaN }));

            host.Session.Config.PingIntervalSeconds = -1;
            host.Session.Leave();
            Assert.Throws<ArgumentException>(() => host.Session.StartHost());
        }
    }
}
