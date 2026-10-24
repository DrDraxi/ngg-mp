using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using NGGMultiplayer.Core.Net;
using Xunit;

namespace NGGMultiplayer.Tests
{
    /// <summary>The UDP transport over real localhost sockets.</summary>
    public class DirectTransportTests
    {
        private static DirectTransport NewTransport() =>
            new DirectTransport(DirectTransport.NewLocalId(), IPAddress.Loopback, 0);

        /// <summary>Polls both until <paramref name="done"/> holds or 10 s pass; returns everything each received.</summary>
        private static (List<IncomingPacket> A, List<IncomingPacket> B) Pump(DirectTransport a, DirectTransport b, Func<List<IncomingPacket>, List<IncomingPacket>, bool> done)
        {
            var gotA = new List<IncomingPacket>();
            var gotB = new List<IncomingPacket>();
            var watch = Stopwatch.StartNew();
            while (!done(gotA, gotB))
            {
                Assert.True(watch.Elapsed.TotalSeconds < 10, "timed out");
                a.Poll(gotA);
                b.Poll(gotB);
                System.Threading.Thread.Sleep(1);
            }
            return (gotA, gotB);
        }

        private static (DirectTransport Host, DirectTransport Client) Connected()
        {
            var host = NewTransport();
            var client = NewTransport();
            client.Probe(new IPEndPoint(IPAddress.Loopback, host.LocalEndPoint.Port));
            Pump(host, client, (_, __) => client.ProbedHost.IsValid);
            Assert.Equal(host.LocalId, client.ProbedHost);
            return (host, client);
        }

        [Fact]
        public void NewLocalId_IsInItsOwnRange()
        {
            var id = DirectTransport.NewLocalId();
            Assert.True(DirectTransport.IsDirectId(id));
            Assert.False(DirectTransport.IsDirectId(new PeerId(76561198047449076UL)));
            Assert.NotEqual(id, DirectTransport.NewLocalId());
        }

        [Fact]
        public void Rebind_ToTheSamePortAgain_KeepsTheSocket_AndStillDelivers()
        {
            // Hosting, ending the session and hosting again binds the same fixed port twice.
            using (var host = NewTransport())
            using (var client = NewTransport())
            {
                int port = host.LocalEndPoint.Port;
                host.Rebind(IPAddress.Loopback, port);
                host.Rebind(IPAddress.Loopback, port);
                Assert.Equal(port, host.LocalEndPoint.Port);

                client.Probe(new IPEndPoint(IPAddress.Loopback, port));
                Pump(host, client, (_, __) => client.ProbedHost.IsValid);
                Assert.Equal(host.LocalId, client.ProbedHost);
            }
        }

        [Fact]
        public void Probe_FindsHost_AndBothWaysDeliver()
        {
            var (host, client) = Connected();
            using (host)
            using (client)
            {
                client.Send(host.LocalId, Channel.Control, Reliability.Reliable, new ArraySegment<byte>(new byte[] { 1, 2, 3 }));
                var (atHost, _) = Pump(host, client, (h, _) => h.Count == 1);
                Assert.Equal(client.LocalId, atHost[0].From);
                Assert.Equal(Channel.Control, atHost[0].Channel);
                Assert.Equal(new byte[] { 1, 2, 3 }, atHost[0].Data);

                // The host learned the client's address from its datagrams.
                host.Send(client.LocalId, Channel.Events, Reliability.Unreliable, new ArraySegment<byte>(new byte[] { 9 }));
                var (_, atClient) = Pump(host, client, (_, c) => c.Count == 1);
                Assert.Equal(host.LocalId, atClient[0].From);
                Assert.Equal(new byte[] { 9 }, atClient[0].Data);
            }
        }

        [Fact]
        public void LargeReliableMessages_ArriveWholeAndInOrder()
        {
            var (host, client) = Connected();
            using (host)
            using (client)
            {
                var rng = new Random(5);
                var sent = new List<byte[]>();
                foreach (int size in new[] { 0, 1, DirectTransport.FragmentSize, DirectTransport.FragmentSize + 1, 300_000, 17 })
                {
                    var data = new byte[size];
                    rng.NextBytes(data);
                    sent.Add(data);
                    host.Send(client.LocalId, Channel.Save, Reliability.Reliable, new ArraySegment<byte>(data));
                }
                var (_, got) = Pump(host, client, (_, c) => c.Count == sent.Count);
                Assert.Equal(sent.Select(Convert.ToBase64String), got.Select(p => Convert.ToBase64String(p.Data)));
            }
        }

        [Fact]
        public void ReliableMessages_SurviveLoss_ExactlyOnceInOrder()
        {
            var (host, client) = Connected();
            using (host)
            using (client)
            {
                var rng = new Random(11);
                host.DropOutgoing = () => rng.NextDouble() < 0.3;
                client.DropOutgoing = () => rng.NextDouble() < 0.3;
                for (int i = 0; i < 200; i++)
                {
                    var data = new byte[1 + (i % 7) * 500];
                    data[0] = (byte)i;
                    client.Send(host.LocalId, Channel.State, Reliability.Reliable, new ArraySegment<byte>(data));
                }
                var (got, _) = Pump(host, client, (h, _) => h.Count >= 200);
                Assert.Equal(200, got.Count);
                for (int i = 0; i < 200; i++)
                {
                    Assert.Equal((byte)i, got[i].Data[0]);
                    Assert.Equal(1 + (i % 7) * 500, got[i].Data.Length);
                }
            }
        }

        [Fact]
        public void Disconnect_ForgetsPeer_UntilItSendsAgain()
        {
            var (host, client) = Connected();
            using (host)
            using (client)
            {
                client.Send(host.LocalId, Channel.Control, Reliability.Reliable, new ArraySegment<byte>(new byte[] { 1 }));
                Pump(host, client, (h, _) => h.Count == 1);
                host.Disconnect(client.LocalId);
                // No address any more: dropped silently.
                host.Send(client.LocalId, Channel.Control, Reliability.Reliable, new ArraySegment<byte>(new byte[] { 2 }));
                client.Send(host.LocalId, Channel.Control, Reliability.Unreliable, new ArraySegment<byte>(new byte[] { 3 }));
                var (atHost, atClient) = Pump(host, client, (h, _) => h.Count == 1);
                Assert.Equal(new byte[] { 3 }, atHost[0].Data);
                Assert.Empty(atClient);
            }
        }

        [Fact]
        public void NetSession_HostAndClient_ConnectAndSyncTheSave()
        {
            var (hostTransport, clientTransport) = Connected();
            var hostSave = new NGGMultiplayer.Core.Save.InMemorySaveBackend();
            var big = new byte[50_000];
            new Random(3).NextBytes(big);
            hostSave.Set("run", "json", big);
            var clientSave = new NGGMultiplayer.Core.Save.InMemorySaveBackend();
            using (var host = new NGGMultiplayer.Core.NetSession(hostTransport, hostSave))
            using (var client = new NGGMultiplayer.Core.NetSession(clientTransport, clientSave))
            {
                host.StartHost();
                client.Connect(hostTransport.LocalId);
                var watch = Stopwatch.StartNew();
                while (!(client.IsReady && host.IsPeerReady(clientTransport.LocalId)))
                {
                    Assert.True(watch.Elapsed.TotalSeconds < 15, "the client never got ready");
                    double now = watch.Elapsed.TotalSeconds;
                    host.Update(now);
                    client.Update(now);
                    System.Threading.Thread.Sleep(2);
                }
                Assert.Equal(big, clientSave.Get("run", "json"));
                Assert.Equal(2, host.Session.Players.Count);
            }
        }

        [Fact]
        public void ForeignDatagrams_AreIgnored()
        {
            using (var host = NewTransport())
            using (var udp = new System.Net.Sockets.UdpClient(0, System.Net.Sockets.AddressFamily.InterNetwork))
            {
                var target = new IPEndPoint(IPAddress.Loopback, host.LocalEndPoint.Port);
                udp.Send(new byte[] { 1, 2, 3 }, 3, target);
                udp.Send(new byte[40], 40, target);
                var got = new List<IncomingPacket>();
                System.Threading.Thread.Sleep(50);
                host.Poll(got);
                Assert.Empty(got);
            }
        }
    }
}
