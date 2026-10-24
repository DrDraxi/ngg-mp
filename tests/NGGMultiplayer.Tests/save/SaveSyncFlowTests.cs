using System;
using System.Collections.Generic;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Save;
using Xunit;

namespace NGGMultiplayer.Tests.Save
{
    public class SaveSyncFlowTests
    {
        private static InMemorySaveBackend LocalSave(params string[] keys)
        {
            var b = new InMemorySaveBackend();
            foreach (var k in keys)
                b.Set("local", k, new byte[] { 42 });
            return b;
        }

        [Fact]
        public void Join_SendsSnapshot_ClientBacksUpSuppressesAppliesAndAcks()
        {
            var net = new SaveNet();
            net.HostBackend.Set("challenge", "c1", new byte[] { 1 });
            net.HostBackend.Set("unlock", "hat", new byte[] { 2 });
            var syncedPeers = new List<(PeerId, uint)>();
            net.Host.PeerSynced += (p, r) => syncedPeers.Add((p, r));

            var client = net.Connect(1, LocalSave("mine"));
            Assert.False(client.Sync.IsSynced);
            net.Settle();

            Assert.True(client.Sync.IsSynced);
            Assert.Equal(SaveSyncState.Synced, client.Sync.State);
            Assert.Equal(1, client.SyncedEvents);
            Assert.Equal(1, client.Backend.BackupCount);
            Assert.True(client.Backend.SuppressDiskWrites);
            Assert.True(client.Sync.HasLocalBackup);
            SaveAssert.SameProgress(net.HostBackend, client.Backend);
            Assert.Null(client.Backend.Get("local", "mine"));
            Assert.Equal(new[] { (client.Id, net.Host.Revision) }, syncedPeers);
            Assert.True(net.Host.IsPeerSynced(client.Id));
            Assert.Equal(1, net.HostSession.CountReceived(MessageType.SaveAck));
            Assert.False(net.HostBackend.SuppressDiskWrites);
        }

        [Fact]
        public void SnapshotProgress_RisesWhileReceiving_AndIsOneWhenSynced()
        {
            // Two chunks per tick, so the transfer spans several frames.
            var net = new SaveNet(options: new SaveSyncOptions { SnapshotBytesPerTick = 2 * SaveProtocol.ChunkSize });
            var rng = new Random(3);
            for (int i = 0; i < 20; i++)
            {
                var value = new byte[1000];
                rng.NextBytes(value);
                net.HostBackend.Set("blob", "b" + i, value);
            }
            var client = net.Connect(1, LocalSave());
            Assert.Equal(0f, client.Sync.SnapshotProgress);

            var seen = new List<float>();
            for (int i = 0; i < 2000 && !client.Sync.IsSynced; i++)
            {
                net.Hub.Advance(0.01);
                net.Pump();
                if (client.Sync.State == SaveSyncState.Receiving)
                    seen.Add(client.Sync.SnapshotProgress);
            }

            Assert.True(client.Sync.IsSynced);
            Assert.Equal(1f, client.Sync.SnapshotProgress);
            Assert.Contains(seen, p => p > 0f && p < 1f);
            for (int i = 1; i < seen.Count; i++)
                Assert.True(seen[i] >= seen[i - 1]);
        }

        [Fact]
        public void LargeSnapshot_IsChunkedAtMost1000Bytes_AndSurvivesLatencyAndJitter()
        {
            var net = new SaveNet(seed: 77);
            net.Hub.LatencyMs = 60;
            net.Hub.JitterMs = 40;
            var rng = new Random(5);
            for (int i = 0; i < 40; i++)
            {
                var value = new byte[rng.Next(100, 2000)];
                rng.NextBytes(value);
                net.HostBackend.Set("blob", "b" + i, value);
            }
            int snapshotSize = net.HostBackend.CaptureSnapshot().Length;
            Assert.True(snapshotSize > 20 * SaveProtocol.ChunkSize);

            // A bare transport stands in for the client so the raw packets can be inspected.
            var sniffer = net.Hub.CreateTransport(new PeerId(1));
            net.HostSession.AddRemote(sniffer.LocalId);
            net.HostSession.RaiseJoined(sniffer.LocalId);
            net.Run(1);
            var incoming = new List<IncomingPacket>();
            sniffer.Poll(incoming);
            var chunkSizes = new List<int>();
            foreach (var p in incoming)
            {
                Assert.Equal(Channel.Save, p.Channel);
                if (p.Data[0] == MessageType.SaveChunk)
                {
                    Assert.True(p.Data.Length <= SaveProtocol.ChunkSize + 8);
                    var r = new NetReader(p.Data);
                    r.ReadByte();
                    Assert.Equal((uint)chunkSizes.Count, r.ReadVarUInt());
                    chunkSizes.Add(r.ReadBytesSegment().Count);
                }
            }
            Assert.Equal(MessageType.SaveBegin, incoming[0].Data[0]);
            Assert.Equal(SaveProtocol.GetChunkCount(snapshotSize), chunkSizes.Count);
            Assert.All(chunkSizes, s => Assert.InRange(s, 1, SaveProtocol.ChunkSize));
            Assert.Equal(snapshotSize, Sum(chunkSizes));
        }

        [Fact]
        public void LargeSnapshot_FullFlow_WithLatencyAndJitter()
        {
            var net = new SaveNet(seed: 78);
            net.Hub.LatencyMs = 80;
            net.Hub.JitterMs = 60;
            var rng = new Random(6);
            for (int i = 0; i < 60; i++)
            {
                var value = new byte[rng.Next(1, 3000)];
                rng.NextBytes(value);
                net.HostBackend.Set("blob", "b" + i, value);
            }
            var client = net.Connect(1);
            net.Settle();
            Assert.True(client.Sync.IsSynced);
            SaveAssert.SameProgress(net.HostBackend, client.Backend);
            Assert.Equal(0, client.Sync.ResyncRequestsSent);
        }

        [Fact]
        public void ZeroByteSnapshot_CompletesWithoutChunks_AndApplyFailureResyncs()
        {
            var net = new SaveNet(createHost: false);
            var client = net.AddClient(1, LocalSave("mine"));
            var empty = new InMemorySaveBackend().CaptureSnapshot();
            Assert.NotEmpty(empty);

            net.SendFromHost(client.Id, RawPackets.SaveBegin(0, 0, Crc32.Compute(Array.Empty<byte>()), 3));
            net.Settle();

            // The reference backend cannot parse a zero-byte snapshot; the sync layer itself accepts it.
            Assert.Equal(1, client.Backend.BackupCount);
            Assert.Equal(SaveSyncState.AwaitingSnapshot, client.Sync.State);
            Assert.Equal(1, net.HostSession.CountReceived(MessageType.SaveResyncRequest));

            foreach (var packet in RawPackets.Snapshot(empty, 4))
                net.SendFromHost(client.Id, packet);
            net.Settle();
            Assert.True(client.Sync.IsSynced);
            Assert.Equal(0, client.Backend.Count);
            Assert.Equal(4u, client.Sync.Revision);
        }

        [Fact]
        public void HostProgress_IsBroadcastAsDeltas_WithIncreasingRevision()
        {
            var net = new SaveNet();
            var a = net.Connect(1);
            var b = net.Connect(2);
            net.Settle();
            uint start = net.Host.Revision;

            net.HostBackend.Set("challenge", "c1", new byte[] { 1 });
            net.HostBackend.Set("challenge", "c2", new byte[] { 1 });
            net.HostBackend.Set("challenge", "c1", new byte[] { 2 });
            net.Settle();

            Assert.Equal(start + 3, net.Host.Revision);
            foreach (var c in new[] { a, b })
            {
                Assert.Equal(net.Host.Revision, c.Sync.Revision);
                Assert.Equal(3, c.Sync.DeltasApplied);
                Assert.Equal(new byte[] { 2 }, c.Backend.Get("challenge", "c1"));
                SaveAssert.SameProgress(net.HostBackend, c.Backend);
                Assert.Equal(0, c.Sync.ClaimsSent);
                Assert.Equal(0, c.Backend.DiskWriteCount);
            }
            Assert.Equal(0, net.HostSession.CountReceived(MessageType.ProgressClaim));
        }

        [Fact]
        public void HostRepeatingSameValue_IsNotRebroadcast()
        {
            var net = new SaveNet();
            net.Connect(1);
            net.Settle();
            net.HostBackend.Set("k", "x", new byte[] { 1 });
            uint revision = net.Host.Revision;
            net.HostBackend.Set("k", "x", new byte[] { 1 });
            Assert.Equal(revision, net.Host.Revision);
            Assert.Equal(1, net.Host.DeltasBroadcast);
        }

        [Fact]
        public void ClientClaim_IsAppliedByHost_AndBroadcastToEveryoneIncludingClaimant()
        {
            var net = new SaveNet();
            var a = net.Connect(1);
            var b = net.Connect(2);
            net.Settle();
            var accepted = new List<(PeerId, SaveDelta)>();
            net.Host.ClaimAccepted += (p, d) => accepted.Add((p, d));
            uint start = net.Host.Revision;

            // The game on the client records progress (applied locally already).
            a.Backend.Set("challenge", "hole3", new byte[] { 7 });
            net.Settle();

            Assert.Equal(1, a.Sync.ClaimsSent);
            Assert.Equal(new byte[] { 7 }, net.HostBackend.Get("challenge", "hole3"));
            Assert.Equal(start + 1, net.Host.Revision);
            var (peer, delta) = Assert.Single(accepted);
            Assert.Equal(a.Id, peer);
            Assert.Equal("hole3", delta.Key);
            foreach (var c in new[] { a, b })
            {
                Assert.Equal(start + 1, c.Sync.Revision);
                SaveAssert.SameProgress(net.HostBackend, c.Backend);
            }
            // Applying the confirmation did not produce a second claim.
            Assert.Equal(1, net.HostSession.CountReceived(MessageType.ProgressClaim));
            Assert.Equal(0, b.Sync.ClaimsSent);
            // Host progress hits the host's disk, never the clients'.
            Assert.Equal(net.HostBackend.CaptureSnapshot(), net.HostBackend.PersistedSnapshot);
            Assert.Equal(0, a.Backend.DiskWriteCount);
        }

        [Fact]
        public void ClaimsSettle_WithoutEchoLoops()
        {
            var net = new SaveNet();
            var a = net.Connect(1);
            var b = net.Connect(2);
            net.Settle();

            for (int i = 0; i < 10; i++)
            {
                a.Backend.Set("k", "a" + i, new byte[] { (byte)i });
                b.Backend.Set("k", "b" + i, new byte[] { (byte)i });
                net.HostBackend.Set("k", "h" + i, new byte[] { (byte)i });
            }
            net.Settle();
            long sent = net.Hub.PacketsSent;
            net.Run(2);

            Assert.Equal(sent, net.Hub.PacketsSent);
            Assert.Equal(30u, net.Host.Revision);
            Assert.Equal(10, a.Sync.ClaimsSent);
            Assert.Equal(10, b.Sync.ClaimsSent);
            SaveAssert.SameProgress(net.HostBackend, a.Backend);
            SaveAssert.SameProgress(net.HostBackend, b.Backend);
        }

        [Fact]
        public void DeferredEchoFromGame_IsNotReclaimed()
        {
            var net = new SaveNet();
            var a = net.Connect(1);
            net.Settle();
            net.HostBackend.Set("k", "x", new byte[] { 3 });
            net.Settle();

            // Some games re-raise "progress recorded" a frame after the value was loaded.
            a.Backend.Set("k", "x", new byte[] { 3 });
            net.Settle();
            Assert.Equal(0, a.Sync.ClaimsSent);

            a.Backend.Set("k", "x", new byte[] { 4 });
            net.Settle();
            Assert.Equal(1, a.Sync.ClaimsSent);
            Assert.Equal(new byte[] { 4 }, net.HostBackend.Get("k", "x"));
        }

        [Fact]
        public void BackendThatEchoesOnApply_DoesNotLoop()
        {
            var hostBackend = new EchoingBackend();
            var net = new SaveNet(createHost: false);
            var host = new SaveSyncHost(net.HostSession, hostBackend);
            var clientBackend = new EchoingBackend();
            var client = net.AddClient(1);
            client.Sync.Dispose();
            client.Sync = new SaveSyncClient(client.Session, clientBackend);
            net.Join(client);
            net.Settle();
            Assert.True(client.Sync.IsSynced);

            clientBackend.Set("k", "x", new byte[] { 1 });
            net.Settle();
            hostBackend.Set("k", "y", new byte[] { 2 });
            net.Settle();
            long sent = net.Hub.PacketsSent;
            net.Run(1);

            Assert.Equal(sent, net.Hub.PacketsSent);
            Assert.Equal(2u, host.Revision);
            Assert.Equal(1, client.Sync.ClaimsSent);
            Assert.Equal(hostBackend.CaptureSnapshot(), clientBackend.CaptureSnapshot());
            Assert.True(hostBackend.EchoesRaised > 0 && clientBackend.EchoesRaised > 0);
        }

        [Fact]
        public void RejectedClaim_NotifiesClient_AndResyncRestoresHostTruth()
        {
            var net = new SaveNet(validator: (peer, d) => d.Kind != "cheat");
            net.HostBackend.Set("k", "base", new byte[] { 1 });
            var hostRejections = new List<(PeerId, SaveDelta, string)>();
            net.Host.ClaimRejected += (p, d, r) => hostRejections.Add((p, d, r));
            var a = net.Connect(1);
            net.Settle();
            uint revision = net.Host.Revision;

            a.Backend.Set("cheat", "all-unlocks", new byte[] { 1 });
            Assert.NotNull(a.Backend.Get("cheat", "all-unlocks"));
            net.Settle();
            Assert.False(a.Sync.IsSynced);
            // The host paces snapshots per peer (MinResyncIntervalSeconds), so the resync follows shortly.
            net.Run(1.5);
            net.Settle();

            Assert.Null(net.HostBackend.Get("cheat", "all-unlocks"));
            Assert.Single(hostRejections);
            var (delta, reason) = Assert.Single(a.Rejections);
            Assert.Equal("all-unlocks", delta.Key);
            Assert.False(string.IsNullOrEmpty(reason));
            Assert.Equal(1, net.HostSession.CountReceived(MessageType.SaveResyncRequest));
            Assert.True(a.Sync.IsSynced);
            Assert.Null(a.Backend.Get("cheat", "all-unlocks"));
            SaveAssert.SameProgress(net.HostBackend, a.Backend);
            Assert.Equal(1, a.Backend.BackupCount);
            Assert.Equal(2, a.SyncedEvents);
            Assert.Equal(revision, net.Host.Revision);
        }

        [Fact]
        public void ThrowingValidator_RejectsClaim()
        {
            var net = new SaveNet(validator: (p, d) => throw new InvalidOperationException("boom"));
            var a = net.Connect(1);
            net.Settle();
            a.Backend.Set("k", "x", new byte[] { 1 });
            net.Run(1.5);
            net.Settle();
            Assert.Single(a.Rejections);
            Assert.Null(net.HostBackend.Get("k", "x"));
            Assert.True(a.Sync.IsSynced);
        }

        [Fact]
        public void ValidatorSeesClaimant()
        {
            var seen = new List<PeerId>();
            var net = new SaveNet(validator: (p, d) => { seen.Add(p); return true; });
            var a = net.Connect(1);
            var b = net.Connect(2);
            net.Settle();
            b.Backend.Set("k", "x", new byte[] { 1 });
            a.Backend.Set("k", "y", new byte[] { 1 });
            net.Settle();
            Assert.Equal(new[] { b.Id, a.Id }, seen);
        }

        [Fact]
        public void ClientProgressBeforeFirstSync_IsNotClaimed()
        {
            var net = new SaveNet();
            var a = net.AddClient(1);
            a.Backend.Set("k", "offline", new byte[] { 1 });
            net.Join(a);
            net.Settle();
            Assert.Equal(0, a.Sync.ClaimsSent);
            Assert.Null(net.HostBackend.Get("k", "offline"));
            Assert.True(a.Sync.IsSynced);
        }

        [Fact]
        public void SessionEnd_RestoresLocalSaveExactlyOnce_AndDiskIsUntouched()
        {
            var net = new SaveNet();
            net.HostBackend.Set("host", "x", new byte[] { 9 });
            var local = LocalSave("mine", "also-mine");
            var diskBefore = local.PersistedSnapshot;
            int writesBefore = local.DiskWriteCount;
            var a = net.Connect(1, local);
            net.Settle();
            a.Backend.Set("k", "claimed", new byte[] { 1 });
            net.HostBackend.Set("host", "y", new byte[] { 2 });
            net.Settle();
            Assert.Equal(writesBefore, local.DiskWriteCount);

            net.Leave(a);
            a.Sync.Shutdown();
            a.Session.RaiseClosed();
            a.Sync.Dispose();

            Assert.Equal(SaveSyncState.Ended, a.Sync.State);
            Assert.Equal(1, local.RestoreCount);
            Assert.Equal(1, a.EndedEvents);
            Assert.False(local.SuppressDiskWrites);
            Assert.Equal(new byte[] { 42 }, local.Get("local", "mine"));
            Assert.Null(local.Get("host", "x"));
            Assert.Null(local.Get("k", "claimed"));
            Assert.Equal(diskBefore, local.PersistedSnapshot);

            // Progress after leaving is the player's own again and is neither claimed nor suppressed.
            long claims = a.Sync.ClaimsSent;
            local.Set("local", "after", new byte[] { 1 });
            Assert.Equal(claims, a.Sync.ClaimsSent);
            Assert.Equal(local.CaptureSnapshot(), local.PersistedSnapshot);
        }

        [Fact]
        public void HostLeaving_EndsClientSync()
        {
            var net = new SaveNet();
            var a = net.Connect(1, LocalSave("mine"));
            net.Settle();
            a.Session.RaiseLeft(SaveNet.HostId);
            Assert.Equal(SaveSyncState.Ended, a.Sync.State);
            Assert.Equal(1, a.Backend.RestoreCount);
            a.Session.RaiseClosed();
            Assert.Equal(1, a.Backend.RestoreCount);
        }

        [Fact]
        public void SessionEndBeforeSync_DoesNotTouchLocalSave()
        {
            var net = new SaveNet(createHost: false);
            var local = LocalSave("mine");
            var a = net.AddClient(1, local);
            a.Session.RaiseClosed();
            Assert.Equal(SaveSyncState.Ended, a.Sync.State);
            Assert.Equal(0, local.BackupCount);
            Assert.Equal(0, local.RestoreCount);
            Assert.False(local.SuppressDiskWrites);
            Assert.Equal(1, a.EndedEvents);
        }

        [Fact]
        public void PacketsAfterEnd_AreIgnored()
        {
            var net = new SaveNet(createHost: false);
            var a = net.AddClient(1, LocalSave("mine"));
            a.Sync.Shutdown();
            foreach (var p in RawPackets.Snapshot(new InMemorySaveBackend().CaptureSnapshot(), 1))
                net.SendFromHost(a.Id, p);
            net.SendFromHost(a.Id, RawPackets.Delta(2, "k", "x", new byte[] { 1 }));
            net.Settle();
            Assert.Equal(0, a.Backend.BackupCount);
            Assert.Equal(0, a.Backend.ApplySnapshotCount);
            Assert.Equal(new byte[] { 42 }, a.Backend.Get("local", "mine"));
        }

        [Fact]
        public void Resync_DoesNotBackUpAgain()
        {
            var net = new SaveNet();
            var a = net.Connect(1, LocalSave("mine"));
            net.Settle();
            a.Sync.RequestResync();
            net.Run(2);
            net.Settle();
            Assert.Equal(2, a.SyncedEvents);
            Assert.Equal(1, a.Backend.BackupCount);
            Assert.Equal(2, a.Backend.ApplySnapshotCount);

            net.Leave(a);
            Assert.Equal(new byte[] { 42 }, a.Backend.Get("local", "mine"));
        }

        [Fact]
        public void PlayerLeft_ForgetsPeer_AndLaterClaimsAreIgnored()
        {
            var net = new SaveNet();
            var a = net.Connect(1);
            net.Settle();
            net.HostSession.RemoveRemote(a.Id);
            net.HostSession.RaiseLeft(a.Id);
            Assert.False(net.Host.IsPeerKnown(a.Id));

            a.Session.SendTo(SaveNet.HostId, Channel.Save, Reliability.Reliable, new ArraySegment<byte>(RawPackets.Claim("k", "x", new byte[] { 1 })));
            net.Settle();
            Assert.Null(net.HostBackend.Get("k", "x"));
            Assert.Equal(0, net.Host.PeerCount);
        }

        [Fact]
        public void Dispose_UnregistersHandlersAndEvents()
        {
            var net = new SaveNet();
            var a = net.Connect(1);
            net.Settle();
            Assert.Equal(4, a.Session.HandlerCount);
            Assert.Equal(3, net.HostSession.HandlerCount);

            a.Sync.Dispose();
            net.Host.Dispose();
            Assert.Equal(0, a.Session.HandlerCount);
            Assert.Equal(0, net.HostSession.HandlerCount);

            net.HostBackend.Set("k", "x", new byte[] { 1 });
            net.Settle();
            Assert.Equal(0, net.Host.DeltasBroadcast);
            Assert.Throws<ObjectDisposedException>(() => net.Host.ResyncAll());
            Assert.Throws<ObjectDisposedException>(() => a.Sync.RequestResync());

            // A fresh pair can take over the same session.
            var host2 = new SaveSyncHost(net.HostSession, net.HostBackend);
            Assert.Equal(3, net.HostSession.HandlerCount);
            host2.Dispose();
        }

        [Fact]
        public void Constructors_CheckRoleAndArguments()
        {
            var net = new SaveNet(createHost: false);
            var clientSession = new FakeSession(net.Hub.CreateTransport(new PeerId(5)), SaveNet.HostId);
            Assert.Throws<InvalidOperationException>(() => new SaveSyncHost(clientSession, new InMemorySaveBackend()));
            Assert.Throws<InvalidOperationException>(() => new SaveSyncClient(net.HostSession, new InMemorySaveBackend()));
            Assert.Throws<ArgumentNullException>(() => new SaveSyncClient(null, new InMemorySaveBackend()));
            Assert.Throws<ArgumentNullException>(() => new SaveSyncClient(clientSession, null));
            Assert.Throws<ArgumentOutOfRangeException>(() => new SaveSyncClient(clientSession, new InMemorySaveBackend(), new SaveSyncOptions { TransferTimeoutSeconds = 0 }));
        }

        [Fact]
        public void ThrowingEventSubscriber_DoesNotBreakSync()
        {
            var net = new SaveNet();
            var a = net.AddClient(1);
            a.Sync.Synced += () => throw new InvalidOperationException("subscriber bug");
            a.Sync.DeltaApplied += (r, d) => throw new InvalidOperationException("subscriber bug");
            net.Join(a);
            net.Settle();
            net.HostBackend.Set("k", "x", new byte[] { 1 });
            net.Settle();
            Assert.True(a.Sync.IsSynced);
            SaveAssert.SameProgress(net.HostBackend, a.Backend);
        }

        [Fact]
        public void Soak_ManyClientsRandomProgress_ConvergesToHost()
        {
            var net = new SaveNet(seed: 2024);
            net.Hub.LatencyMs = 30;
            net.Hub.JitterMs = 50;
            var rng = new Random(99);
            var clients = new List<ClientNode>();
            for (ulong i = 1; i <= 4; i++)
            {
                net.HostBackend.Set("pre", "p" + i, new byte[] { (byte)i });
                clients.Add(net.Connect(i, LocalSave("own" + i)));
                net.Run(0.05);
            }

            for (int step = 0; step < 400; step++)
            {
                int who = rng.Next(0, 5);
                var value = new byte[rng.Next(0, 8)];
                rng.NextBytes(value);
                string key = "k" + rng.Next(0, 25);
                if (who == 4)
                    net.HostBackend.Set("prog", key, value);
                else if (clients[who].Sync.HasLocalBackup)
                    clients[who].Backend.Set("prog", key, value);
                net.Run(0.01);
            }
            net.Settle();

            foreach (var c in clients)
            {
                Assert.True(c.Sync.IsSynced);
                Assert.Equal(net.Host.Revision, c.Sync.Revision);
                SaveAssert.SameProgress(net.HostBackend, c.Backend);
                Assert.Equal(1, c.Backend.BackupCount);
                Assert.Equal(0, c.Sync.ResyncRequestsSent);
            }
            foreach (var c in clients)
                net.Leave(c);
            for (int i = 0; i < clients.Count; i++)
            {
                Assert.Equal(1, clients[i].Backend.Count);
                Assert.Equal(new byte[] { 42 }, clients[i].Backend.Get("local", "own" + (i + 1)));
            }
        }

        private static int Sum(List<int> values)
        {
            int total = 0;
            foreach (var v in values)
                total += v;
            return total;
        }
    }

    /// <summary>A misbehaving backend that raises LocalProgressChanged even for remote applies.</summary>
    internal sealed class EchoingBackend : ISaveBackend
    {
        private readonly InMemorySaveBackend _inner = new InMemorySaveBackend();

        public EchoingBackend()
        {
            _inner.LocalProgressChanged += d => LocalProgressChanged?.Invoke(d);
        }

        public event Action<SaveDelta> LocalProgressChanged;

        public int EchoesRaised { get; private set; }

        public bool SuppressDiskWrites
        {
            get => _inner.SuppressDiskWrites;
            set => _inner.SuppressDiskWrites = value;
        }

        public void Set(string kind, string key, byte[] value) => _inner.Set(kind, key, value);

        public byte[] CaptureSnapshot() => _inner.CaptureSnapshot();

        public void ApplySnapshot(byte[] snapshot)
        {
            _inner.ApplySnapshot(snapshot);
            foreach (var e in _inner.GetEntries())
                Echo(e);
        }

        public void BackupLocalSave() => _inner.BackupLocalSave();

        public void RestoreLocalSave() => _inner.RestoreLocalSave();

        public void ApplyDelta(SaveDelta delta)
        {
            _inner.ApplyDelta(delta);
            Echo(delta.Clone());
        }

        private void Echo(SaveDelta delta)
        {
            EchoesRaised++;
            LocalProgressChanged?.Invoke(delta);
        }
    }
}
