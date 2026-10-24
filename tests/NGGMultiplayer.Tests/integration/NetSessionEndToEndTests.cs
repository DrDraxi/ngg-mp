using System;
using System.Linq;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Save;
using NGGMultiplayer.Core.Session;
using NGGMultiplayer.Core.Sync;
using Xunit;

namespace NGGMultiplayer.Tests.Integration
{
    public class NetSessionEndToEndTests
    {
        private static byte[] Bytes(int length, byte seed)
        {
            var b = new byte[length];
            for (int i = 0; i < b.Length; i++)
                b[i] = (byte)(seed + i * 31);
            return b;
        }

        private static TransformState Pose(float x, double time, float vx)
        {
            var s = new TransformState(new NetVector3(x, 1, 0), NetQuaternion.Identity, time);
            s.Velocity = new NetVector3(vx, 0, 0);
            return s;
        }

        [Theory]
        [InlineData(7)]
        [InlineData(42)]
        [InlineData(20261019)]
        public void HostAndThreeClients_FullSessionLifecycle_OverLossyNetwork(int seed)
        {
            var net = new NetTestNet(seed);
            net.Hub.LatencyMs = 40;
            net.Hub.JitterMs = 15;
            net.Hub.UnreliableLossPercent = 10;

            // Each peer runs on its own clock; entity sync must still agree on the host's time base.
            var host = net.Add(100, clockOffset: 0);
            var c1 = net.Add(201, clockOffset: 1234.5);
            var c2 = net.Add(202, clockOffset: -77.25);
            var c3 = net.Add(203, clockOffset: 9000);
            var clients = new[] { c1, c2, c3 };

            // Host progress, large enough to need several snapshot chunks; clients have their own saves on disk.
            host.Save.Set("challenge", "hole-1", new byte[] { 1 });
            host.Save.Set("unlock", "putter-gold", new byte[] { 1 });
            host.Save.Set("blob", "course-state", Bytes(3500, 3));
            for (int i = 0; i < clients.Length; i++)
            {
                clients[i].Save.Set("challenge", "own-" + i, new byte[] { (byte)(10 + i) });
                clients[i].Save.Set("unlock", "hat-" + i, new byte[] { 2 });
            }
            var hostSnapshot = host.Save.CaptureSnapshot();
            var originalSaves = clients.Select(c => c.Save.CaptureSnapshot()).ToArray();
            var originalDisk = clients.Select(c => c.Save.PersistedSnapshot).ToArray();

            // --- Host starts and spawns a host-owned entity before anyone joins. ---
            host.Net.StartHost();
            Assert.True(host.Net.IsActive);
            Assert.True(host.Net.IsReady);
            net.Frame();
            var ball = host.Net.Entities.Spawn("ball", host.Id, Pose(0, 0, 2));
            host.BeforeUpdate = p =>
            {
                if (p.Net.IsActive && p.Factory.Has(ball.Id))
                {
                    double t = p.Net.HostTime;
                    p.Factory[ball.Id].Current = Pose((float)(2 * t), t, 2);
                }
            };

            // --- Join and save snapshot sync. ---
            foreach (var c in clients)
                c.Net.Connect(host.Id);
            Assert.True(net.RunUntil(() => clients.All(c => c.Net.IsReady)
                && clients.All(c => host.Net.IsPeerReady(c.Id)), 10), "clients did not sync");

            Assert.Equal(3, host.Net.Session.Players.Count - 1);
            Assert.Equal(clients.Select(c => c.Id).OrderBy(i => i.Value), host.ReadyPeers.OrderBy(i => i.Value));
            foreach (var c in clients)
            {
                Assert.Equal(1, c.ReadyCount);
                Assert.Equal(hostSnapshot, c.Save.CaptureSnapshot());
                Assert.Equal(1, c.Save.BackupCount);
                Assert.True(c.Save.SuppressDiskWrites);
                Assert.Equal(SaveSyncState.Synced, c.Net.SaveClient.State);
            }

            // --- The pre-existing entity reaches every client, only after its save loaded, and streams. ---
            Assert.True(net.RunUntil(() => clients.All(c => c.Factory.Has(ball.Id)), 2), "ball was not replicated");
            net.Run(1.0);
            foreach (var c in clients)
            {
                Assert.Equal(0, c.Factory.CreatedBeforeSync);
                var replica = c.Factory[ball.Id];
                Assert.NotEmpty(replica.Applied);
                // Rendered InterpolationDelay (0.1 s) behind the host clock; tolerate clock estimation error.
                double expected = 2 * (host.Net.HostTime - 0.1);
                Assert.InRange(replica.Current.Position.x, expected - 0.15, expected + 0.15);
            }

            // --- A client's progress claim reaches the host's save and every client. ---
            int hostDiskWrites = host.Save.DiskWriteCount;
            c1.Save.Set("challenge", "hole-7", new byte[] { 1 });
            Assert.True(net.RunUntil(() => new[] { host, c2, c3 }.All(p => p.Save.Get("challenge", "hole-7") != null), 2),
                "claim did not propagate");
            Assert.Equal(new byte[] { 1 }, host.Save.Get("challenge", "hole-7"));
            Assert.True(host.Save.DiskWriteCount > hostDiskWrites, "the host persists accepted claims");
            net.Run(0.3);
            var withClaim = host.Save.CaptureSnapshot();
            foreach (var c in clients)
            {
                Assert.Equal(withClaim, c.Save.CaptureSnapshot());
                Assert.Same(originalDisk[Array.IndexOf(clients, c)], c.Save.PersistedSnapshot);
            }

            // --- A client-owned entity: client 2 drives it, the host relays it to the others. ---
            var cart = host.Net.Entities.Spawn("cart", c2.Id, Pose(100, 0, 3), NetEntityFlags.DespawnOnOwnerLeave);
            Assert.True(net.RunUntil(() => c2.Factory.Has(cart.Id), 2));
            c2.BeforeUpdate = p =>
            {
                if (p.Net.IsActive && p.Factory.Has(cart.Id))
                {
                    double t = p.Net.HostTime;
                    p.Factory[cart.Id].Current = Pose((float)(100 + 3 * t), t, 3);
                }
            };
            Assert.True(c2.Net.Entities.Get(cart.Id).IsLocallyOwned);
            net.Run(1.5);
            Assert.True(host.Net.Entities.StatesRelayed > 0);
            foreach (var p in new[] { host, c1, c3 })
            {
                var replica = p.Factory[cart.Id];
                Assert.NotEmpty(replica.Applied);
                double expected = 100 + 3 * (host.Net.HostTime - 0.1);
                Assert.InRange(replica.Current.Position.x, expected - 0.25, expected + 0.25);
            }

            // --- RPC to All from a client: everyone, the sender included, sees it with the true sender. ---
            Assert.True(c3.SendChat("forbidden"));
            Assert.True(c3.SendChat("hello all"));
            net.Run(0.5);
            foreach (var p in net.Peers)
                Assert.Equal(new[] { (c3.Id, "hello all") }, p.Chat);
            Assert.Equal(1, host.Net.Rpc.Rejected);

            // --- Client 2 leaves: its own save comes back; the host drops the entity it owned. ---
            c2.Net.Leave();
            Assert.Equal(SessionEndReason.LocalLeft, Assert.Single(c2.Ended).Reason);
            Assert.False(c2.Net.IsActive);
            Assert.Null(c2.Net.Entities);
            Assert.Equal(1, c2.Save.RestoreCount);
            Assert.False(c2.Save.SuppressDiskWrites);
            Assert.Equal(originalSaves[1], c2.Save.CaptureSnapshot());
            Assert.Equal(originalSaves[1], c2.Save.PersistedSnapshot);
            Assert.Equal(DespawnReason.SessionClosed, c2.Factory[ball.Id].Despawned);
            Assert.Equal(DespawnReason.SessionClosed, c2.Factory[cart.Id].Despawned);

            Assert.True(net.RunUntil(() => !host.Net.Session.IsPeerConnected(c2.Id)
                && c1.Factory[cart.Id].Despawned != null && c3.Factory[cart.Id].Despawned != null, 2));
            Assert.False(host.Net.IsPeerReady(c2.Id));
            Assert.Null(host.Net.Entities.Get(cart.Id));
            Assert.Equal(DespawnReason.OwnerLeft, host.Factory[cart.Id].Despawned);
            Assert.Equal(DespawnReason.OwnerLeft, c1.Factory[cart.Id].Despawned);
            Assert.Equal(DespawnReason.OwnerLeft, c3.Factory[cart.Id].Despawned);
            Assert.Null(c1.Factory[ball.Id].Despawned);
            Assert.Equal(1, host.Net.Entities.Count);

            // The remaining clients still play: RPCs and saves keep flowing.
            Assert.True(host.SendChat("still here"));
            host.Save.Set("challenge", "hole-8", new byte[] { 1 });
            net.Run(0.5);
            Assert.Equal((host.Id, "still here"), c1.Chat.Last());
            Assert.Equal((host.Id, "still here"), c3.Chat.Last());
            Assert.Single(c2.Chat);
            Assert.NotNull(c1.Save.Get("challenge", "hole-8"));
            Assert.Null(c2.Save.Get("challenge", "hole-8"));

            // --- Host leaves: every session ends and every client gets its own save back. ---
            host.Net.Leave();
            Assert.Equal(SessionEndReason.LocalLeft, Assert.Single(host.Ended).Reason);
            Assert.Equal(DespawnReason.SessionClosed, host.Factory[ball.Id].Despawned);
            Assert.True(net.RunUntil(() => c1.Ended.Count == 1 && c3.Ended.Count == 1, 2), "clients did not see the host leave");
            foreach (var c in new[] { c1, c3 })
            {
                int i = Array.IndexOf(clients, c);
                Assert.Equal(SessionEndReason.HostLeft, Assert.Single(c.Ended).Reason);
                Assert.False(c.Net.IsActive);
                Assert.Null(c.Net.SaveClient);
                Assert.Equal(1, c.Save.RestoreCount);
                Assert.False(c.Save.SuppressDiskWrites);
                Assert.Equal(originalSaves[i], c.Save.CaptureSnapshot());
                Assert.Equal(originalSaves[i], c.Save.PersistedSnapshot);
                Assert.Equal(DespawnReason.SessionClosed, c.Factory[ball.Id].Despawned);
                Assert.Equal(new[] { "started", "ready", "ended:HostLeft" }, c.Log);
            }

            // The host keeps everything the session produced, including the client's accepted claim.
            Assert.Equal(0, host.Save.RestoreCount);
            Assert.NotNull(host.Save.Get("challenge", "hole-7"));
            Assert.Single(c2.Ended);
            Assert.Equal(1, c2.Save.RestoreCount);
        }
    }
}
