using System;
using System.Collections.Generic;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Save;
using Xunit;

namespace NGGMultiplayer.Tests.Save
{
    public class InMemorySaveBackendTests
    {
        [Fact]
        public void Set_StoresPersistsAndRaisesEvent()
        {
            var b = new InMemorySaveBackend();
            var events = new List<SaveDelta>();
            b.LocalProgressChanged += events.Add;

            b.Set("challenge", "c1", new byte[] { 1 });

            Assert.Equal(new byte[] { 1 }, b.Get("challenge", "c1"));
            var e = Assert.Single(events);
            Assert.True(e.ContentEquals(new SaveDelta("challenge", "c1", new byte[] { 1 })));
            Assert.Equal(1, b.DiskWriteCount);
            Assert.Equal(b.CaptureSnapshot(), b.PersistedSnapshot);
        }

        [Fact]
        public void KindAndKeyDoNotCollide()
        {
            var b = new InMemorySaveBackend();
            b.Set("ab", "c", new byte[] { 1 });
            b.Set("a", "bc", new byte[] { 2 });
            Assert.Equal(2, b.Count);
            Assert.Equal(new byte[] { 1 }, b.Get("ab", "c"));
            Assert.Equal(new byte[] { 2 }, b.Get("a", "bc"));
            Assert.Null(b.Get("abc", ""));
        }

        [Fact]
        public void ApplyDelta_DoesNotRaiseEvent()
        {
            var b = new InMemorySaveBackend();
            int events = 0;
            b.LocalProgressChanged += _ => events++;
            b.ApplyDelta(new SaveDelta("k", "x", new byte[] { 5 }));
            Assert.Equal(0, events);
            Assert.Equal(new byte[] { 5 }, b.Get("k", "x"));
            Assert.Equal(1, b.ApplyDeltaCount);
        }

        [Fact]
        public void Snapshot_RoundTrips_AndIsDeterministic()
        {
            var a = new InMemorySaveBackend();
            a.Set("unlock", "hat", new byte[] { 1 });
            a.Set("challenge", "z", Array.Empty<byte>());
            a.Set("challenge", "a", new byte[] { 2, 3 });

            var b = new InMemorySaveBackend();
            b.Set("challenge", "a", new byte[] { 2, 3 });
            b.Set("unlock", "hat", new byte[] { 1 });
            b.Set("challenge", "z", null);

            Assert.Equal(a.CaptureSnapshot(), b.CaptureSnapshot());

            var c = new InMemorySaveBackend();
            c.Set("stale", "x", new byte[] { 9 });
            c.ApplySnapshot(a.CaptureSnapshot());
            Assert.Null(c.Get("stale", "x"));
            SaveAssert.SameProgress(a, c);
            Assert.Equal(3, c.GetEntries().Count);
        }

        [Fact]
        public void ApplySnapshot_Garbage_ThrowsProtocolException_AndKeepsState()
        {
            var b = new InMemorySaveBackend();
            b.Set("k", "x", new byte[] { 1 });
            var before = b.CaptureSnapshot();
            var rng = new Random(42);
            for (int i = 0; i < 2000; i++)
            {
                var data = new byte[rng.Next(0, 64)];
                rng.NextBytes(data);
                if (data.Length > 0)
                    data[0] = (byte)(i % 3 == 0 ? 1 : data[0]);
                try
                {
                    b.ApplySnapshot(data);
                    b.ApplySnapshot(before);
                }
                catch (ProtocolException)
                {
                }
            }
            Assert.Equal(before, b.CaptureSnapshot());
        }

        [Fact]
        public void ApplySnapshot_RejectsDuplicatesTrailingBytesWrongVersionAndHugeCount()
        {
            var b = new InMemorySaveBackend();
            Assert.Throws<ProtocolException>(() => b.ApplySnapshot(Build(w =>
            {
                w.WriteVarUInt(1);
                w.WriteVarUInt(2);
                new SaveDelta("k", "x", null).Serialize(w);
                new SaveDelta("k", "x", null).Serialize(w);
            })));
            Assert.Throws<ProtocolException>(() => b.ApplySnapshot(Build(w =>
            {
                w.WriteVarUInt(1);
                w.WriteVarUInt(0);
                w.WriteByte(0);
            })));
            Assert.Throws<ProtocolException>(() => b.ApplySnapshot(Build(w => { w.WriteVarUInt(2); w.WriteVarUInt(0); })));
            Assert.Throws<ProtocolException>(() => b.ApplySnapshot(Build(w => { w.WriteVarUInt(1); w.WriteVarUInt(uint.MaxValue); })));
            Assert.Throws<ArgumentNullException>(() => b.ApplySnapshot(null));
        }

        [Fact]
        public void BackupRestore_AndSuppressDiskWrites()
        {
            var b = new InMemorySaveBackend();
            b.Set("k", "mine", new byte[] { 1 });
            var disk = b.PersistedSnapshot;
            int writes = b.DiskWriteCount;

            b.BackupLocalSave();
            b.SuppressDiskWrites = true;
            var host = new InMemorySaveBackend();
            host.Set("k", "host", new byte[] { 2 });
            b.ApplySnapshot(host.CaptureSnapshot());
            b.ApplyDelta(new SaveDelta("k", "host2", new byte[] { 3 }));
            Assert.Equal(writes, b.DiskWriteCount);
            Assert.Same(disk, b.PersistedSnapshot);
            Assert.Null(b.Get("k", "mine"));

            b.RestoreLocalSave();
            b.SuppressDiskWrites = false;
            Assert.Equal(new byte[] { 1 }, b.Get("k", "mine"));
            Assert.Null(b.Get("k", "host"));
            Assert.Equal(disk, b.PersistedSnapshot);
            Assert.False(b.HasBackup);
            Assert.Throws<InvalidOperationException>(() => b.RestoreLocalSave());
        }

        [Fact]
        public void Get_ReturnsCopies()
        {
            var b = new InMemorySaveBackend();
            var value = new byte[] { 1 };
            b.Set("k", "x", value);
            value[0] = 9;
            b.Get("k", "x")[0] = 7;
            Assert.Equal(new byte[] { 1 }, b.Get("k", "x"));
        }

        [Fact]
        public void Set_InvalidKind_Throws()
        {
            var b = new InMemorySaveBackend();
            Assert.Throws<ArgumentException>(() => b.Set("", "x", null));
            Assert.Throws<ArgumentException>(() => b.Set("a\0", "x", null));
            Assert.Throws<ArgumentException>(() => b.Set("a", null, null));
            Assert.Throws<ArgumentException>(() => b.Set(new string('k', SaveDelta.MaxKindBytes + 1), "x", null));
        }

        [Fact]
        public void Snapshot_CarriesValuesLargerThanADelta()
        {
            var a = new InMemorySaveBackend();
            var big = new byte[SaveDelta.MaxValueBytes * 2];
            new Random(2).NextBytes(big);
            a.Set("photo", "p", big);
            var b = new InMemorySaveBackend();
            b.ApplySnapshot(a.CaptureSnapshot());
            Assert.Equal(big, b.Get("photo", "p"));
        }

        private static byte[] Build(Action<NetWriter> write)
        {
            var w = new NetWriter();
            write(w);
            return w.ToArray();
        }
    }
}
