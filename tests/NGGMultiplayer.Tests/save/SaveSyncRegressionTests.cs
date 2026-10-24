using System;
using NGGMultiplayer.Core;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Save;
using Xunit;

namespace NGGMultiplayer.Tests.Save
{
    public class SaveSyncRegressionTests
    {
        // ---- Snapshot pacing -------------------------------------------------------------------------

        [Fact]
        public void LargeSnapshot_IsSentInBoundedBatchesPerTick()
        {
            var net = new SaveNet(options: new SaveSyncOptions { SnapshotBytesPerTick = 10_000 });
            net.HostBackend.Set("blob", "course", Filled(300_000, 7));
            var a = net.AddClient(1);
            net.Join(a);

            // SaveBegin plus ten 1000-byte chunks right away; the other ~290 follow from Tick.
            Assert.Equal(11, a.Session.Transport.PendingCount);
            net.Pump();
            Assert.False(a.Sync.IsSynced);

            net.Settle();
            Assert.True(a.Sync.IsSynced);
            SaveAssert.SameProgress(net.HostBackend, a.Backend);
            Assert.Equal(1, a.SyncedEvents);
        }

        [Fact]
        public void ResyncDuringAPacedTransfer_StartsOverAndCompletes()
        {
            var net = new SaveNet(options: new SaveSyncOptions { SnapshotBytesPerTick = 5_000, MinResyncIntervalSeconds = 0 });
            net.HostBackend.Set("blob", "course", Filled(100_000, 3));
            var a = net.Connect(1);
            net.Pump();
            net.Pump();

            net.HostBackend.Set("blob", "course", Filled(120_000, 4));
            net.Settle();

            Assert.True(a.Sync.IsSynced);
            SaveAssert.SameProgress(net.HostBackend, a.Backend);
            Assert.Empty(a.Failures);
        }

        [Fact]
        public void TransferTimeouts_CountAsFailures_AndEventuallyFail()
        {
            var options = new SaveSyncOptions { TransferTimeoutSeconds = 1, MaxConsecutiveFailures = 3 };
            var net = new SaveNet(options: options, createHost: false);
            var a = net.AddClient(1);

            net.Run(10);

            Assert.Equal(SaveSyncState.Failed, a.Sync.State);
            Assert.Single(a.Failures);
            Assert.Contains("timed out", a.Failures[0]);
        }

        // ---- Failed restore is sticky ----------------------------------------------------------------

        [Fact]
        public void FailedRestore_PreventsTheNextSessionFromBackingUpTheHostsSave()
        {
            var backend = new FaultyBackend { FailRestore = true };
            backend.Inner.Set("mine", "x", new byte[] { 9 });
            var ownSaveOnDisk = backend.Inner.PersistedSnapshot;

            var first = new SaveNet();
            first.HostBackend.Set("host1", "progress", new byte[] { 1 });
            var a = JoinWith(first, 1, backend);
            first.Settle();
            Assert.True(a.Sync.IsSynced);
            first.Leave(a);
            Assert.True(backend.SuppressDiskWrites, "a failed restore keeps writes suppressed");

            backend.FailRestore = false;
            var second = new SaveNet();
            second.HostBackend.Set("host2", "progress", new byte[] { 2 });
            var b = JoinWith(second, 1, backend);
            second.Settle();

            Assert.Equal(SaveSyncState.Failed, b.Sync.State);
            Assert.Single(b.Failures);
            Assert.Equal(1, backend.BackupCalls);
            Assert.False(b.Sync.HasLocalBackup);
            Assert.Null(backend.Inner.Get("host2", "progress"));

            second.Leave(b);
            Assert.True(backend.SuppressDiskWrites);
            Assert.Same(ownSaveOnDisk, backend.Inner.PersistedSnapshot);
        }

        [Fact]
        public void NetSession_RefusesToStart_WhileARestoreIsPending()
        {
            var hub = new LoopbackHub();
            var backend = new InMemorySaveBackend { SuppressDiskWrites = true };
            using (var net = new NetSession(hub.CreateTransport(new PeerId(1)), backend))
            {
                Assert.True(net.LocalSaveRestorePending);
                Assert.Throws<InvalidOperationException>(() => net.StartHost());
                Assert.Throws<InvalidOperationException>(() => net.Connect(new PeerId(2)));
                Assert.False(net.Session.IsActive);

                backend.SuppressDiskWrites = false;
                Assert.False(net.LocalSaveRestorePending);
                net.StartHost();
                Assert.True(net.IsActive);
            }
        }

        // ---- Host echo filter ------------------------------------------------------------------------

        [Fact]
        public void HostChangeBackToAValuePublishedBeforeASnapshot_IsBroadcast()
        {
            var net = new SaveNet();
            var a = net.Connect(1);
            net.Settle();

            net.HostBackend.Set("photo", "p", new byte[] { 1 });
            net.Settle();
            net.HostBackend.Set("photo", "p", Filled(SaveDelta.MaxValueBytes + 10, 5));
            net.Settle();
            Assert.Equal(SaveDelta.MaxValueBytes + 10, a.Backend.Get("photo", "p").Length);

            net.HostBackend.Set("photo", "p", new byte[] { 1 });
            net.Settle();
            Assert.Equal(new byte[] { 1 }, a.Backend.Get("photo", "p"));
            SaveAssert.SameProgress(net.HostBackend, a.Backend);
        }

        [Fact]
        public void ClaimOfAValuePublishedBeforeResyncAll_IsApplied()
        {
            var net = new SaveNet();
            var a = net.Connect(1);
            var b = net.Connect(2);
            net.Settle();

            net.HostBackend.Set("ch", "h7", new byte[] { 1 });
            net.Settle();

            // The host reloads an older save outside of LocalProgressChanged and pushes it to everyone.
            var older = new InMemorySaveBackend();
            older.Set("ch", "h7", new byte[] { 0 });
            net.HostBackend.ApplySnapshot(older.CaptureSnapshot());
            net.Host.ResyncAll();
            net.Settle();
            Assert.Equal(new byte[] { 0 }, a.Backend.Get("ch", "h7"));

            a.Backend.Set("ch", "h7", new byte[] { 1 });
            net.Settle();

            Assert.Equal(new byte[] { 1 }, net.HostBackend.Get("ch", "h7"));
            Assert.Equal(new byte[] { 1 }, b.Backend.Get("ch", "h7"));
            Assert.Empty(a.Rejections);
        }

        // ---- Claims and side effects -----------------------------------------------------------------

        [Fact]
        public void SideEffectsOfAnAppliedClaim_AreBroadcast_AndTheClaimIsNotEchoed()
        {
            var net = new SaveNet(createHost: false);
            var backend = new GameLikeBackend();
            var host = new SaveSyncHost(net.HostSession, backend);
            var a = net.Connect(1);
            var b = net.Connect(2);
            net.Settle();

            a.Backend.Set("challenge", "hole7", new byte[] { 1 });
            net.Settle();

            Assert.Equal(new byte[] { 1 }, backend.Inner.Get("item", "hat"));
            Assert.Equal(2L, host.DeltasBroadcast);
            foreach (var c in new[] { a, b })
            {
                Assert.Equal(new byte[] { 1 }, c.Backend.Get("challenge", "hole7"));
                Assert.Equal(new byte[] { 1 }, c.Backend.Get("item", "hat"));
                SaveAssert.SameProgress(backend.Inner, c.Backend);
            }
        }

        [Fact]
        public void ClaimNormalizedByTheGame_BroadcastsTheRecordedValue()
        {
            var net = new SaveNet(createHost: false);
            var backend = new GameLikeBackend { Normalize = v => new byte[] { (byte)Math.Min(v[0], (byte)3) } };
            var host = new SaveSyncHost(net.HostSession, backend);
            var a = net.Connect(1);
            var b = net.Connect(2);
            net.Settle();

            a.Backend.Set("score", "hole2", new byte[] { 9 });
            net.Settle();

            Assert.Equal(new byte[] { 3 }, backend.Inner.Get("score", "hole2"));
            Assert.Equal(new byte[] { 3 }, a.Backend.Get("score", "hole2"));
            Assert.Equal(new byte[] { 3 }, b.Backend.Get("score", "hole2"));
            Assert.Equal(1L, host.DeltasBroadcast);
        }

        [Fact]
        public void ClientChangeTooLargeToClaim_IsRejectedLocally_AndUndoneByResync()
        {
            var net = new SaveNet();
            net.HostBackend.Set("challenge", "hole1", new byte[] { 1 });
            var a = net.Connect(1);
            net.Settle();
            long resyncs = a.Sync.ResyncRequestsSent;

            a.Backend.Set("photo", "big", Filled(SaveDelta.MaxValueBytes + 1, 2));

            var rejection = Assert.Single(a.Rejections);
            Assert.Equal("photo", rejection.Delta.Kind);
            Assert.Equal(resyncs + 1, a.Sync.ResyncRequestsSent);
            // The host paces resyncs (MinResyncIntervalSeconds), so give it time to answer.
            net.Run(1.5);
            net.Settle();
            Assert.True(a.Sync.IsSynced);
            Assert.Null(a.Backend.Get("photo", "big"));
            Assert.Null(net.HostBackend.Get("photo", "big"));
            SaveAssert.SameProgress(net.HostBackend, a.Backend);
        }

        private static ClientNode JoinWith(SaveNet net, ulong id, ISaveBackend backend)
        {
            var node = net.AddClient(id);
            node.Sync.Dispose();
            node.Sync = new SaveSyncClient(node.Session, backend);
            node.Sync.SyncFailed += r => node.Failures.Add(r);
            net.Join(node);
            return node;
        }

        private static byte[] Filled(int length, byte value)
        {
            var bytes = new byte[length];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = (byte)(value + i);
            return bytes;
        }

        /// <summary>
        /// Behaves like a game applying progress through its own logic: the claimed value is recorded through the
        /// game (so LocalProgressChanged fires for it, possibly normalized), and completing hole7 unlocks a hat.
        /// </summary>
        private sealed class GameLikeBackend : ISaveBackend
        {
            public readonly InMemorySaveBackend Inner = new InMemorySaveBackend();

            public Func<byte[], byte[]> Normalize;

            public GameLikeBackend()
            {
                Inner.LocalProgressChanged += d => LocalProgressChanged?.Invoke(d);
            }

            public event Action<SaveDelta> LocalProgressChanged;

            public bool SuppressDiskWrites
            {
                get => Inner.SuppressDiskWrites;
                set => Inner.SuppressDiskWrites = value;
            }

            public byte[] CaptureSnapshot() => Inner.CaptureSnapshot();

            public void ApplySnapshot(byte[] snapshot) => Inner.ApplySnapshot(snapshot);

            public void BackupLocalSave() => Inner.BackupLocalSave();

            public void RestoreLocalSave() => Inner.RestoreLocalSave();

            public void ApplyDelta(SaveDelta delta)
            {
                Inner.Set(delta.Kind, delta.Key, Normalize != null ? Normalize(delta.Value) : delta.Value);
                if (delta.Kind == "challenge" && delta.Key == "hole7")
                    Inner.Set("item", "hat", new byte[] { 1 });
            }
        }
    }
}
