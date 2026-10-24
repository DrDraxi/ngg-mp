using System;
using System.Collections.Generic;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Save;
using Xunit;

namespace NGGMultiplayer.Tests.Save
{
    /// <summary>Fault handling: gaps, corruption, timeouts, interleaving and hostile input.</summary>
    public class SaveSyncRecoveryTests
    {
        private static byte[] HostSnapshot(params (string Key, byte Value)[] entries)
        {
            var b = new InMemorySaveBackend();
            foreach (var e in entries)
                b.Set("k", e.Key, new byte[] { e.Value });
            return b.CaptureSnapshot();
        }

        private static ClientNode RawClient(SaveNet net)
        {
            var local = new InMemorySaveBackend();
            local.Set("local", "mine", new byte[] { 42 });
            return net.AddClient(1, local);
        }

        private static void SendSnapshot(SaveNet net, ClientNode client, byte[] snapshot, uint revision)
        {
            foreach (var p in RawPackets.Snapshot(snapshot, revision))
                net.SendFromHost(client.Id, p);
        }

        [Fact]
        public void RevisionGap_TriggersResync_AndHostResendsSnapshot()
        {
            var net = new SaveNet();
            var a = net.Connect(1);
            net.Settle();
            net.HostBackend.Set("k", "x", new byte[] { 1 });
            net.Settle();
            Assert.Equal(1u, a.Sync.Revision);

            // A forged delta that skips revision 2.
            net.SendFromHost(a.Id, RawPackets.Delta(3, "k", "y", new byte[] { 1 }));
            net.Settle();
            Assert.False(a.Sync.IsSynced);
            Assert.Equal(1, net.HostSession.CountReceived(MessageType.SaveResyncRequest));
            Assert.Null(a.Backend.Get("k", "y"));

            net.Run(1.5);
            net.Settle();
            Assert.True(a.Sync.IsSynced);
            Assert.Equal(2, net.Host.SnapshotsSent);
            SaveAssert.SameProgress(net.HostBackend, a.Backend);
            Assert.True(net.Host.IsPeerSynced(a.Id));
        }

        [Fact]
        public void StaleAndDuplicateDeltas_AreIgnored()
        {
            var net = new SaveNet(createHost: false);
            var a = RawClient(net);
            SendSnapshot(net, a, HostSnapshot(("x", 1)), 5);
            net.SendFromHost(a.Id, RawPackets.Delta(4, "k", "x", new byte[] { 9 }));
            net.SendFromHost(a.Id, RawPackets.Delta(5, "k", "x", new byte[] { 9 }));
            net.SendFromHost(a.Id, RawPackets.Delta(6, "k", "x", new byte[] { 2 }));
            net.SendFromHost(a.Id, RawPackets.Delta(6, "k", "x", new byte[] { 9 }));
            net.Settle();

            Assert.True(a.Sync.IsSynced);
            Assert.Equal(6u, a.Sync.Revision);
            Assert.Equal(new byte[] { 2 }, a.Backend.Get("k", "x"));
            Assert.Equal(0, net.HostSession.CountReceived(MessageType.SaveResyncRequest));
        }

        [Fact]
        public void DeltasDuringTransfer_AreQueued_AndOnlyNewerOnesApplied()
        {
            var net = new SaveNet(createHost: false);
            var a = RawClient(net);
            var snapshot = new InMemorySaveBackend();
            for (int i = 0; i < 30; i++)
                snapshot.Set("k", "s" + i, new byte[100]);
            snapshot.Set("k", "x", new byte[] { 1 });
            var packets = RawPackets.Snapshot(snapshot.CaptureSnapshot(), 10);
            Assert.True(packets.Count > 3);

            int half = packets.Count / 2;
            for (int i = 0; i < half; i++)
                net.SendFromHost(a.Id, packets[i]);
            net.Settle();
            Assert.Equal(SaveSyncState.Receiving, a.Sync.State);

            net.SendFromHost(a.Id, RawPackets.Delta(9, "k", "x", new byte[] { 0 }));
            net.SendFromHost(a.Id, RawPackets.Delta(10, "k", "x", new byte[] { 1 }));
            net.SendFromHost(a.Id, RawPackets.Delta(11, "k", "x", new byte[] { 2 }));
            net.SendFromHost(a.Id, RawPackets.Delta(12, "k", "y", new byte[] { 3 }));
            net.Settle();
            Assert.Equal(SaveSyncState.Receiving, a.Sync.State);
            Assert.Equal(0, a.Backend.ApplyDeltaCount);

            for (int i = half; i < packets.Count; i++)
                net.SendFromHost(a.Id, packets[i]);
            net.Settle();

            Assert.True(a.Sync.IsSynced);
            Assert.Equal(12u, a.Sync.Revision);
            Assert.Equal(2, a.Backend.ApplyDeltaCount);
            Assert.Equal(new byte[] { 2 }, a.Backend.Get("k", "x"));
            Assert.Equal(new byte[] { 3 }, a.Backend.Get("k", "y"));
            Assert.Equal(1, a.SyncedEvents);
            Assert.Equal(1, net.HostSession.CountReceived(MessageType.SaveAck));
        }

        [Fact]
        public void DeltasBeforeSaveBegin_AreQueuedToo()
        {
            var net = new SaveNet(createHost: false);
            var a = RawClient(net);
            net.SendFromHost(a.Id, RawPackets.Delta(3, "k", "x", new byte[] { 3 }));
            SendSnapshot(net, a, HostSnapshot(("x", 2)), 2);
            net.Settle();
            Assert.True(a.Sync.IsSynced);
            Assert.Equal(3u, a.Sync.Revision);
            Assert.Equal(new byte[] { 3 }, a.Backend.Get("k", "x"));
        }

        [Fact]
        public void GapInQueuedDeltasAfterSnapshot_TriggersResync_WithoutAck()
        {
            var net = new SaveNet(createHost: false);
            var a = RawClient(net);
            net.SendFromHost(a.Id, RawPackets.SaveBegin(1, 1, 0, 1));
            net.SendFromHost(a.Id, RawPackets.Delta(3, "k", "x", new byte[] { 3 }));
            net.Settle();
            var snapshot = HostSnapshot(("x", 1));
            // Restart the transfer properly; the r3 delta stays queued and leaves a gap after r1.
            SendSnapshot(net, a, snapshot, 1);
            net.Settle();

            Assert.False(a.Sync.IsSynced);
            Assert.Equal(0, net.HostSession.CountReceived(MessageType.SaveAck));
            Assert.Equal(1, net.HostSession.CountReceived(MessageType.SaveResyncRequest));
            Assert.Equal(0, a.SyncedEvents);
        }

        [Fact]
        public void CrcMismatch_DiscardsSnapshot_AndRequestsResync()
        {
            var net = new SaveNet(createHost: false);
            var a = RawClient(net);
            var snapshot = HostSnapshot(("x", 1));
            foreach (var p in RawPackets.Snapshot(snapshot, 1, Crc32.Compute(snapshot) ^ 1))
                net.SendFromHost(a.Id, p);
            net.Settle();

            Assert.Equal(SaveSyncState.AwaitingSnapshot, a.Sync.State);
            Assert.Equal(1, net.HostSession.CountReceived(MessageType.SaveResyncRequest));
            Assert.Equal(0, a.Backend.BackupCount);
            Assert.Equal(0, a.Backend.ApplySnapshotCount);
            Assert.False(a.Backend.SuppressDiskWrites);

            SendSnapshot(net, a, snapshot, 1);
            net.Settle();
            Assert.True(a.Sync.IsSynced);
            Assert.Equal(1, a.Backend.BackupCount);
        }

        [Fact]
        public void CorruptedChunk_IsCaughtByCrc_EndToEnd()
        {
            var net = new SaveNet();
            net.HostBackend.Set("k", "x", new byte[2500]);
            var a = net.AddClient(1);
            // Corrupt the first chunk in flight by replaying a tampered copy ahead of the real transfer.
            var snapshot = net.HostBackend.CaptureSnapshot();
            var tampered = (byte[])snapshot.Clone();
            tampered[10] ^= 0xFF;
            var packets = RawPackets.Snapshot(tampered, 0, Crc32.Compute(snapshot));
            foreach (var p in packets)
                net.SendFromHost(a.Id, p);
            net.Join(a);
            net.Settle();
            net.Run(1.5);
            net.Settle();

            Assert.True(a.Sync.IsSynced);
            SaveAssert.SameProgress(net.HostBackend, a.Backend);
            Assert.True(a.Sync.ResyncRequestsSent >= 1);
        }

        [Fact]
        public void MalformedTransferMetadata_RequestsResync()
        {
            var net = new SaveNet(createHost: false);
            var a = RawClient(net);
            // Chunk count inconsistent with size.
            net.SendFromHost(a.Id, RawPackets.SaveBegin(2500, 2, 0, 1));
            net.Settle();
            Assert.Equal(1, net.HostSession.CountReceived(MessageType.SaveResyncRequest));

            // Wrong chunk length, then an out-of-range index, each restart the transfer.
            net.SendFromHost(a.Id, RawPackets.SaveBegin(1500, 2, 0, 1));
            net.SendFromHost(a.Id, RawPackets.SaveChunk(0, new byte[999], 0, 999));
            net.Settle();
            Assert.Equal(2, net.HostSession.CountReceived(MessageType.SaveResyncRequest));

            net.SendFromHost(a.Id, RawPackets.SaveBegin(1500, 2, 0, 1));
            net.SendFromHost(a.Id, RawPackets.SaveChunk(2, new byte[500], 0, 500));
            net.Settle();
            Assert.Equal(3, net.HostSession.CountReceived(MessageType.SaveResyncRequest));
            Assert.Equal(0, a.Backend.BackupCount);
            Assert.Empty(a.Failures);
        }

        [Fact]
        public void OversizedSnapshot_IsRefusedBeforeAllocating()
        {
            var net = new SaveNet(createHost: false);
            var a = RawClient(net);
            uint huge = SaveProtocol.MaxSnapshotBytes + 1u;
            net.SendFromHost(a.Id, RawPackets.SaveBegin(huge, (uint)SaveProtocol.GetChunkCount((int)huge), 0, 1));
            net.Settle();
            Assert.Equal(SaveSyncState.AwaitingSnapshot, a.Sync.State);
            Assert.Equal(1, net.HostSession.CountReceived(MessageType.SaveResyncRequest));
        }

        [Fact]
        public void DuplicateChunks_AreIgnored_AndOutOfOrderChunksAssemble()
        {
            var net = new SaveNet(createHost: false);
            var a = RawClient(net);
            var b = new InMemorySaveBackend();
            b.Set("k", "big", new byte[3500]);
            var packets = RawPackets.Snapshot(b.CaptureSnapshot(), 1);
            net.SendFromHost(a.Id, packets[0]);
            for (int i = packets.Count - 1; i >= 1; i--)
            {
                net.SendFromHost(a.Id, packets[i]);
                net.SendFromHost(a.Id, packets[i]);
            }
            net.Settle();
            Assert.True(a.Sync.IsSynced);
            SaveAssert.SameProgress(b, a.Backend);
            Assert.Equal(0, net.HostSession.CountReceived(MessageType.SaveResyncRequest));
        }

        [Fact]
        public void NewSaveBegin_RestartsTransfer()
        {
            var net = new SaveNet(createHost: false);
            var a = RawClient(net);
            var first = RawPackets.Snapshot(new byte[2500], 1);
            net.SendFromHost(a.Id, first[0]);
            net.SendFromHost(a.Id, first[1]);
            SendSnapshot(net, a, HostSnapshot(("x", 7)), 2);
            net.Settle();
            Assert.True(a.Sync.IsSynced);
            Assert.Equal(2u, a.Sync.Revision);
            Assert.Equal(new byte[] { 7 }, a.Backend.Get("k", "x"));
        }

        [Fact]
        public void StalledTransfer_TimesOut_AndIsRequestedAgain()
        {
            var options = new SaveSyncOptions { TransferTimeoutSeconds = 2 };
            var net = new SaveNet(createHost: false, options: options);
            var a = RawClient(net);
            var packets = RawPackets.Snapshot(new byte[2500], 1);
            net.SendFromHost(a.Id, packets[0]);
            net.SendFromHost(a.Id, packets[1]);
            net.Run(1.5);
            Assert.Equal(SaveSyncState.Receiving, a.Sync.State);
            Assert.Equal(0, net.HostSession.CountReceived(MessageType.SaveResyncRequest));

            net.Run(1);
            Assert.Equal(SaveSyncState.AwaitingSnapshot, a.Sync.State);
            Assert.Equal(1, net.HostSession.CountReceived(MessageType.SaveResyncRequest));

            // Keeps asking while the host stays silent, once per timeout.
            net.Run(2.05);
            Assert.Equal(2, net.HostSession.CountReceived(MessageType.SaveResyncRequest));
            Assert.Empty(a.Failures);
        }

        [Fact]
        public void NoSnapshotAtAll_TimesOut_AndRequests()
        {
            var options = new SaveSyncOptions { TransferTimeoutSeconds = 3 };
            var net = new SaveNet(createHost: false, options: options);
            net.Hub.Advance(100);
            var a = RawClient(net);
            net.Run(2.9);
            Assert.Equal(0, net.HostSession.CountReceived(MessageType.SaveResyncRequest));
            net.Run(0.2);
            Assert.Equal(1, net.HostSession.CountReceived(MessageType.SaveResyncRequest));
        }

        [Fact]
        public void LostLink_RecoversThroughTimeoutAndHostResend()
        {
            var options = new SaveSyncOptions { TransferTimeoutSeconds = 2 };
            var net = new SaveNet(options: options);
            net.HostBackend.Set("k", "x", new byte[5000]);
            var a = net.AddClient(1);
            net.Hub.SetLinkDown(SaveNet.HostId, a.Id, true);
            net.Join(a);
            net.Run(1);
            Assert.Equal(SaveSyncState.AwaitingSnapshot, a.Sync.State);
            net.Hub.SetLinkDown(SaveNet.HostId, a.Id, false);
            net.Run(3);
            net.Settle();

            Assert.True(a.Sync.IsSynced);
            SaveAssert.SameProgress(net.HostBackend, a.Backend);
            Assert.Equal(1, a.Backend.BackupCount);
        }

        [Fact]
        public void RepeatedFailures_GiveUp_AndStillRestoreOnEnd()
        {
            var options = new SaveSyncOptions { MaxConsecutiveFailures = 3 };
            var net = new SaveNet(createHost: false, options: options);
            var a = RawClient(net);
            var good = HostSnapshot(("x", 1));
            SendSnapshot(net, a, good, 1);
            net.Settle();
            Assert.True(a.Sync.IsSynced);

            for (int i = 0; i < 3; i++)
            {
                foreach (var p in RawPackets.Snapshot(good, 2, 12345))
                    net.SendFromHost(a.Id, p);
                net.Settle();
            }
            Assert.Equal(SaveSyncState.Failed, a.Sync.State);
            Assert.Single(a.Failures);
            Assert.Equal(2, net.HostSession.CountReceived(MessageType.SaveResyncRequest));

            // Failed is terminal for syncing, but leaving still gives the player their save back.
            SendSnapshot(net, a, good, 3);
            net.Settle();
            Assert.Equal(SaveSyncState.Failed, a.Sync.State);
            a.Session.RaiseClosed();
            Assert.Equal(1, a.Backend.RestoreCount);
            Assert.False(a.Backend.SuppressDiskWrites);
            Assert.Equal(new byte[] { 42 }, a.Backend.Get("local", "mine"));
        }

        [Fact]
        public void SuccessResetsFailureCount()
        {
            var options = new SaveSyncOptions { MaxConsecutiveFailures = 2 };
            var net = new SaveNet(createHost: false, options: options);
            var a = RawClient(net);
            var good = HostSnapshot(("x", 1));
            for (int i = 0; i < 4; i++)
            {
                foreach (var p in RawPackets.Snapshot(good, 1, 12345))
                    net.SendFromHost(a.Id, p);
                SendSnapshot(net, a, good, 1);
                net.Settle();
                Assert.True(a.Sync.IsSynced);
            }
            Assert.Empty(a.Failures);
        }

        [Fact]
        public void BackupFailure_NeverAppliesHostSave()
        {
            var net = new SaveNet(createHost: false);
            var backend = new FaultyBackend { FailBackup = true };
            var node = net.AddClient(1);
            node.Sync.Dispose();
            var client = node.Sync = new SaveSyncClient(node.Session, backend);
            var failures = new List<string>();
            client.SyncFailed += failures.Add;

            SendSnapshot(net, node, HostSnapshot(("x", 1)), 1);
            net.Settle();

            Assert.Equal(SaveSyncState.Failed, client.State);
            Assert.Single(failures);
            Assert.Equal(0, backend.ApplySnapshotCalls);
            Assert.False(backend.SuppressDiskWrites);
            client.Shutdown();
            Assert.Equal(0, backend.RestoreCalls);
        }

        [Fact]
        public void ApplySnapshotFailure_Resyncs()
        {
            var net = new SaveNet(createHost: false);
            var backend = new FaultyBackend { FailApplySnapshotTimes = 1 };
            var node = net.AddClient(1);
            node.Sync.Dispose();
            node.Sync = new SaveSyncClient(node.Session, backend);

            var snapshot = HostSnapshot(("x", 1));
            SendSnapshot(net, node, snapshot, 1);
            net.Settle();
            Assert.Equal(SaveSyncState.AwaitingSnapshot, node.Sync.State);
            Assert.Equal(1, net.HostSession.CountReceived(MessageType.SaveResyncRequest));

            SendSnapshot(net, node, snapshot, 1);
            net.Settle();
            Assert.True(node.Sync.IsSynced);
            Assert.Equal(1, backend.BackupCalls);
            Assert.True(backend.SuppressDiskWrites);
        }

        [Fact]
        public void RestoreFailure_KeepsDiskWritesSuppressed()
        {
            var net = new SaveNet(createHost: false);
            var backend = new FaultyBackend { FailRestore = true };
            var node = net.AddClient(1);
            node.Sync.Dispose();
            node.Sync = new SaveSyncClient(node.Session, backend);
            SendSnapshot(net, node, HostSnapshot(("x", 1)), 1);
            net.Settle();
            Assert.True(node.Sync.IsSynced);

            node.Session.RaiseClosed();
            Assert.Equal(SaveSyncState.Ended, node.Sync.State);
            Assert.Equal(1, backend.RestoreCalls);
            Assert.True(backend.SuppressDiskWrites);
        }

        [Fact]
        public void PacketsFromNonHost_AreIgnoredByClient()
        {
            var net = new SaveNet();
            var a = net.Connect(1);
            var b = net.Connect(2);
            net.Settle();
            uint revision = a.Sync.Revision;

            var forged = new List<byte[]>(RawPackets.Snapshot(HostSnapshot(("evil", 1)), 50))
            {
                RawPackets.Delta(revision + 1, "k", "evil", new byte[] { 1 }),
                RawPackets.Delta(revision + 5, "k", "evil", new byte[] { 1 }),
            };
            foreach (var p in forged)
                b.Session.SendTo(a.Id, Channel.Save, Reliability.Reliable, new ArraySegment<byte>(p));
            net.Settle();

            Assert.True(a.Sync.IsSynced);
            Assert.Equal(revision, a.Sync.Revision);
            Assert.Null(a.Backend.Get("k", "evil"));
            Assert.Equal(0, a.Sync.ResyncRequestsSent);
        }

        [Fact]
        public void Host_IgnoresUnknownPeersAndBogusAcks()
        {
            var net = new SaveNet();
            var stranger = net.Hub.CreateTransport(new PeerId(66));
            stranger.Send(SaveNet.HostId, Channel.Save, Reliability.Reliable, new ArraySegment<byte>(RawPackets.Claim("k", "x", new byte[] { 1 })));
            stranger.Send(SaveNet.HostId, Channel.Save, Reliability.Reliable, new ArraySegment<byte>(RawPackets.ResyncRequest(0)));
            stranger.Send(SaveNet.HostId, Channel.Save, Reliability.Reliable, new ArraySegment<byte>(RawPackets.Ack(0)));
            net.Settle();
            Assert.Null(net.HostBackend.Get("k", "x"));
            Assert.Equal(0, net.Host.SnapshotsSent);
            Assert.Equal(0, net.Host.PeerCount);

            var a = net.AddClient(1);
            var synced = 0;
            net.Host.PeerSynced += (p, r) => synced++;
            a.Session.SendTo(SaveNet.HostId, Channel.Save, Reliability.Reliable, new ArraySegment<byte>(RawPackets.Ack(0)));
            net.Settle();
            Assert.Equal(0, synced);

            net.Join(a);
            a.Session.SendTo(SaveNet.HostId, Channel.Save, Reliability.Reliable, new ArraySegment<byte>(RawPackets.Ack(999)));
            net.Settle();
            Assert.Equal(1, synced);
        }

        [Fact]
        public void Host_PacesResyncRequests_AndCoalescesSpam()
        {
            var net = new SaveNet(options: new SaveSyncOptions { MinResyncIntervalSeconds = 1 });
            var a = net.Connect(1);
            net.Settle();
            Assert.Equal(1, net.Host.SnapshotsSent);

            var request = new ArraySegment<byte>(RawPackets.ResyncRequest(0));
            for (int i = 0; i < 50; i++)
                a.Session.SendTo(SaveNet.HostId, Channel.Save, Reliability.Reliable, request);
            net.Settle();
            Assert.Equal(1, net.Host.SnapshotsSent);

            net.Run(1.1);
            Assert.Equal(2, net.Host.SnapshotsSent);
            net.Run(3);
            Assert.Equal(2, net.Host.SnapshotsSent);
            net.Settle();
            Assert.True(a.Sync.IsSynced);
        }

        [Fact]
        public void Host_OversizedLocalChange_IsDeliveredBySnapshot()
        {
            var net = new SaveNet();
            var a = net.Connect(1);
            var b = net.Connect(2);
            net.Settle();
            uint before = net.Host.Revision;

            var huge = new byte[SaveDelta.MaxValueBytes + 10];
            new Random(1).NextBytes(huge);
            net.HostBackend.Set("photo", "hole1", huge);
            net.HostBackend.Set("k", "after", new byte[] { 1 });
            net.Settle();

            Assert.Equal(before + 2, net.Host.Revision);
            Assert.Equal(4, net.Host.SnapshotsSent);
            foreach (var c in new[] { a, b })
            {
                Assert.True(c.Sync.IsSynced);
                Assert.Equal(net.Host.Revision, c.Sync.Revision);
                SaveAssert.SameProgress(net.HostBackend, c.Backend);
                Assert.Equal(1, c.Backend.BackupCount);
            }
        }

        [Fact]
        public void Host_ResyncAll_ResendsToEveryone()
        {
            var net = new SaveNet();
            var a = net.Connect(1);
            net.Settle();
            // The host's save changed behind the sync layer's back, e.g. reloaded from disk.
            net.HostBackend.ApplyDelta(new SaveDelta("k", "silent", new byte[] { 5 }));
            net.Host.ResyncAll();
            net.Settle();
            Assert.Equal(new byte[] { 5 }, a.Backend.Get("k", "silent"));
            Assert.Equal(2, a.SyncedEvents);
        }

        [Fact]
        public void Host_FailingCapture_IsLogged_AndClientRetries()
        {
            var options = new SaveSyncOptions { TransferTimeoutSeconds = 2 };
            var net = new SaveNet(createHost: false, options: options);
            var backend = new FaultyBackend { FailCaptureTimes = 1 };
            backend.Inner.Set("k", "x", new byte[] { 1 });
            var host = new SaveSyncHost(net.HostSession, backend, null, options);
            var a = net.Connect(1);
            net.Settle();
            Assert.Equal(0, host.SnapshotsSent);

            net.Run(2.5);
            net.Settle();
            Assert.True(a.Sync.IsSynced);
            Assert.Equal(new byte[] { 1 }, a.Backend.Get("k", "x"));
        }

        [Fact]
        public void Host_FailingApplyDelta_RejectsClaim_AndResyncsEveryone()
        {
            var net = new SaveNet(createHost: false);
            var backend = new FaultyBackend { FailApplyDelta = true };
            var host = new SaveSyncHost(net.HostSession, backend);
            var rejected = 0;
            host.ClaimRejected += (p, d, r) => rejected++;
            var a = net.Connect(1);
            var b = net.Connect(2);
            net.Settle();
            long snapshotsBefore = host.SnapshotsSent;
            a.Backend.Set("k", "x", new byte[] { 1 });
            net.Settle();
            Assert.Equal(1, rejected);
            Assert.Single(a.Rejections);
            Assert.Equal(0L, host.DeltasBroadcast);
            // The failed apply may have left partial changes, so every peer gets a fresh snapshot.
            Assert.True(host.SnapshotsSent >= snapshotsBefore + 2);
            Assert.True(a.Sync.IsSynced);
            Assert.True(b.Sync.IsSynced);
            Assert.Equal(host.Revision, b.Sync.Revision);
        }

        [Fact]
        public void Fuzz_RandomSavePackets_NeverThrow_AndSyncStillWorks()
        {
            var net = new SaveNet(options: new SaveSyncOptions { MaxConsecutiveFailures = int.MaxValue });
            var a = net.Connect(1, new InMemorySaveBackend());
            net.Settle();
            var rng = new Random(31337);
            byte[] types =
            {
                MessageType.SaveBegin, MessageType.SaveChunk, MessageType.SaveAck, MessageType.SaveDelta,
                MessageType.ProgressClaim, MessageType.SaveResyncRequest, MessageType.ProgressClaimRejected,
            };

            for (int i = 0; i < 3000; i++)
            {
                var packet = new byte[rng.Next(1, 24)];
                rng.NextBytes(packet);
                packet[0] = types[rng.Next(types.Length)];
                // Keep any accidental SaveBegin small so the fuzz does not allocate huge buffers.
                if (packet[0] == MessageType.SaveBegin && packet.Length > 1)
                    packet[1] &= 0x7F;
                if (rng.Next(2) == 0)
                    net.SendFromHost(a.Id, packet);
                else
                    a.Session.SendTo(SaveNet.HostId, Channel.Save, Reliability.Reliable, new ArraySegment<byte>(packet));
                if (i % 50 == 0)
                    net.Run(0.3);
            }
            net.Settle();

            // Whatever state the fuzz left behind, an explicit resync brings the client back unless it gave up.
            if (a.Sync.State != SaveSyncState.Failed)
            {
                a.Sync.RequestResync();
                net.Run(20);
                net.Settle();
                Assert.True(a.Sync.IsSynced);
                net.HostBackend.Set("k", "post-fuzz", new byte[] { 1 });
                net.Settle();
                Assert.Equal(new byte[] { 1 }, a.Backend.Get("k", "post-fuzz"));
            }
            a.Session.RaiseClosed();
            Assert.Equal(SaveSyncState.Ended, a.Sync.State);
            Assert.False(a.Backend.SuppressDiskWrites);
        }

        [Fact]
        public void QueueOverflow_ForcesAnotherResync()
        {
            var options = new SaveSyncOptions { MaxQueuedDeltas = 2 };
            var net = new SaveNet(createHost: false, options: options);
            var a = RawClient(net);
            for (uint r = 2; r <= 5; r++)
                net.SendFromHost(a.Id, RawPackets.Delta(r, "k", "x", new byte[] { (byte)r }));
            SendSnapshot(net, a, HostSnapshot(("x", 1)), 1);
            net.Settle();
            Assert.False(a.Sync.IsSynced);
            Assert.Equal(1, net.HostSession.CountReceived(MessageType.SaveResyncRequest));
        }
    }

    /// <summary>Backend wrapper with switchable faults.</summary>
    internal sealed class FaultyBackend : ISaveBackend
    {
        public readonly InMemorySaveBackend Inner = new InMemorySaveBackend();

        public FaultyBackend()
        {
            Inner.LocalProgressChanged += d => LocalProgressChanged?.Invoke(d);
        }

        public bool FailBackup;
        public bool FailRestore;
        public bool FailApplyDelta;
        public int FailApplySnapshotTimes;
        public int FailCaptureTimes;
        public int ApplySnapshotCalls;
        public int BackupCalls;
        public int RestoreCalls;

        public event Action<SaveDelta> LocalProgressChanged;

        public bool SuppressDiskWrites
        {
            get => Inner.SuppressDiskWrites;
            set => Inner.SuppressDiskWrites = value;
        }

        public byte[] CaptureSnapshot()
        {
            if (FailCaptureTimes > 0)
            {
                FailCaptureTimes--;
                throw new InvalidOperationException("capture failed");
            }
            return Inner.CaptureSnapshot();
        }

        public void ApplySnapshot(byte[] snapshot)
        {
            ApplySnapshotCalls++;
            if (FailApplySnapshotTimes > 0)
            {
                FailApplySnapshotTimes--;
                throw new InvalidOperationException("apply failed");
            }
            Inner.ApplySnapshot(snapshot);
        }

        public void BackupLocalSave()
        {
            BackupCalls++;
            if (FailBackup)
                throw new InvalidOperationException("backup failed");
            Inner.BackupLocalSave();
        }

        public void RestoreLocalSave()
        {
            RestoreCalls++;
            if (FailRestore)
                throw new InvalidOperationException("restore failed");
            Inner.RestoreLocalSave();
        }

        public void ApplyDelta(SaveDelta delta)
        {
            if (FailApplyDelta)
                throw new InvalidOperationException("apply delta failed");
            Inner.ApplyDelta(delta);
        }
    }
}
