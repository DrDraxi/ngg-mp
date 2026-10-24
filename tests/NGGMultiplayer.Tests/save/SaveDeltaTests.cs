using System;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Save;
using Xunit;

namespace NGGMultiplayer.Tests.Save
{
    public class SaveDeltaTests
    {
        private static SaveDelta RoundTrip(SaveDelta delta)
        {
            var w = new NetWriter();
            delta.Serialize(w);
            var r = new NetReader(w.ToArray());
            var result = SaveDelta.Deserialize(r);
            Assert.True(r.IsAtEnd);
            return result;
        }

        [Fact]
        public void RoundTrip_PreservesContent()
        {
            var delta = new SaveDelta("challenge", "hole-7/par", new byte[] { 1, 2, 3, 255 });
            var back = RoundTrip(delta);
            Assert.Equal("challenge", back.Kind);
            Assert.Equal("hole-7/par", back.Key);
            Assert.Equal(new byte[] { 1, 2, 3, 255 }, back.Value);
            Assert.True(delta.ContentEquals(back));
        }

        [Fact]
        public void RoundTrip_UnicodeEmptyKeyAndNullValue()
        {
            var back = RoundTrip(new SaveDelta("ключ✓", string.Empty, null));
            Assert.Equal("ключ✓", back.Kind);
            Assert.Equal(string.Empty, back.Key);
            Assert.Empty(back.Value);
        }

        [Fact]
        public void RoundTrip_AtExactLimits()
        {
            var delta = new SaveDelta(new string('k', SaveDelta.MaxKindBytes), new string('y', SaveDelta.MaxKeyBytes), new byte[SaveDelta.MaxValueBytes]);
            Assert.True(delta.IsValid);
            Assert.True(delta.ContentEquals(RoundTrip(delta)));
        }

        [Fact]
        public void Validity_Rules()
        {
            Assert.False(new SaveDelta(null, "k", null).IsWellFormed);
            Assert.False(new SaveDelta("", "k", null).IsWellFormed);
            Assert.False(new SaveDelta("a\0b", "k", null).IsWellFormed);
            Assert.False(new SaveDelta("kind", null, null).IsWellFormed);
            Assert.True(new SaveDelta("kind", "k\0with nul is fine", null).IsWellFormed);
            Assert.False(new SaveDelta(new string('k', SaveDelta.MaxKindBytes + 1), "k", null).IsWithinLimits);
            Assert.False(new SaveDelta("kind", new string('k', SaveDelta.MaxKeyBytes + 1), null).IsWithinLimits);
            Assert.False(new SaveDelta("kind", "k", new byte[SaveDelta.MaxValueBytes + 1]).IsWithinLimits);
            // Limits are in UTF-8 bytes, not chars: each 'é' is 2 bytes.
            Assert.False(new SaveDelta(new string('é', SaveDelta.MaxKindBytes / 2 + 1), "k", null).IsWithinLimits);
        }

        [Fact]
        public void Serialize_RejectsInvalid()
        {
            var w = new NetWriter();
            Assert.Throws<ArgumentException>(() => new SaveDelta("", "k", null).Serialize(w));
            Assert.Throws<ArgumentException>(() => new SaveDelta("kind", "k", new byte[SaveDelta.MaxValueBytes + 1]).Serialize(w));
            Assert.Throws<ArgumentNullException>(() => new SaveDelta("kind", "k", null).Serialize(null));
        }

        [Fact]
        public void Deserialize_RejectsEmptyKindNulKindAndOversizedFields()
        {
            Assert.Throws<ProtocolException>(() => SaveDelta.Deserialize(Reader(w => { w.WriteString(""); w.WriteString("k"); w.WriteBytes(null); })));
            Assert.Throws<ProtocolException>(() => SaveDelta.Deserialize(Reader(w => { w.WriteString("a\0"); w.WriteString("k"); w.WriteBytes(null); })));
            Assert.Throws<ProtocolException>(() => SaveDelta.Deserialize(Reader(w => { w.WriteString(new string('k', SaveDelta.MaxKindBytes + 1)); w.WriteString("k"); w.WriteBytes(null); })));
            Assert.Throws<ProtocolException>(() => SaveDelta.Deserialize(Reader(w => { w.WriteString("kind"); w.WriteString(new string('k', SaveDelta.MaxKeyBytes + 1)); w.WriteBytes(null); })));
            Assert.Throws<ProtocolException>(() => SaveDelta.Deserialize(Reader(w => { w.WriteString("kind"); w.WriteString("k"); w.WriteBytes(new byte[SaveDelta.MaxValueBytes + 1]); })));
        }

        [Fact]
        public void Deserialize_EveryTruncation_ThrowsProtocolException()
        {
            var w = new NetWriter();
            new SaveDelta("kind", "key", new byte[] { 9, 8, 7 }).Serialize(w);
            var full = w.ToArray();
            for (int length = 0; length < full.Length; length++)
                Assert.Throws<ProtocolException>(() => SaveDelta.Deserialize(new NetReader(full, 0, length)));
        }

        [Fact]
        public void Deserialize_RandomGarbage_OnlyThrowsProtocolException()
        {
            var rng = new Random(1234);
            for (int i = 0; i < 5000; i++)
            {
                var data = new byte[rng.Next(0, 40)];
                rng.NextBytes(data);
                try
                {
                    var d = SaveDelta.Deserialize(new NetReader(data));
                    Assert.True(d.IsValid);
                }
                catch (ProtocolException)
                {
                }
            }
        }

        [Fact]
        public void ContentEquals_TreatsNullAndEmptyValueAsEqual_AndIsOrdinal()
        {
            Assert.True(new SaveDelta("a", "b", null).ContentEquals(new SaveDelta("a", "b", Array.Empty<byte>())));
            Assert.False(new SaveDelta("a", "b", null).ContentEquals(new SaveDelta("A", "b", null)));
            Assert.False(new SaveDelta("a", "b", new byte[] { 1 }).ContentEquals(new SaveDelta("a", "b", new byte[] { 2 })));
            Assert.False(new SaveDelta("a", "b", null).ContentEquals(null));
        }

        [Fact]
        public void Clone_CopiesValue()
        {
            var original = new SaveDelta("a", "b", new byte[] { 1 });
            var clone = original.Clone();
            clone.Value[0] = 2;
            Assert.Equal(1, original.Value[0]);
        }

        private static NetReader Reader(Action<NetWriter> write)
        {
            var w = new NetWriter();
            write(w);
            return new NetReader(w.ToArray());
        }
    }
}
