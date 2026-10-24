using System;
using System.Text;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using Xunit;

namespace NGGMultiplayer.Tests
{
    public class NetWriterReaderTests
    {
        private static NetReader RoundTrip(Action<NetWriter> write)
        {
            var w = new NetWriter(1);
            write(w);
            return new NetReader(w.ToArray());
        }

        [Fact]
        public void FixedWidthPrimitives_RoundTrip_EdgeValues()
        {
            var r = RoundTrip(w =>
            {
                w.WriteByte(0); w.WriteByte(255);
                w.WriteSByte(sbyte.MinValue); w.WriteSByte(sbyte.MaxValue);
                w.WriteBool(true); w.WriteBool(false);
                w.WriteUShort(0); w.WriteUShort(ushort.MaxValue);
                w.WriteShort(short.MinValue); w.WriteShort(short.MaxValue);
                w.WriteInt(int.MinValue); w.WriteInt(int.MaxValue); w.WriteInt(-1);
                w.WriteUInt(0); w.WriteUInt(uint.MaxValue);
                w.WriteLong(long.MinValue); w.WriteLong(long.MaxValue);
                w.WriteULong(0); w.WriteULong(ulong.MaxValue);
            });
            Assert.Equal(0, r.ReadByte()); Assert.Equal(255, r.ReadByte());
            Assert.Equal(sbyte.MinValue, r.ReadSByte()); Assert.Equal(sbyte.MaxValue, r.ReadSByte());
            Assert.True(r.ReadBool()); Assert.False(r.ReadBool());
            Assert.Equal(0, r.ReadUShort()); Assert.Equal(ushort.MaxValue, r.ReadUShort());
            Assert.Equal(short.MinValue, r.ReadShort()); Assert.Equal(short.MaxValue, r.ReadShort());
            Assert.Equal(int.MinValue, r.ReadInt()); Assert.Equal(int.MaxValue, r.ReadInt()); Assert.Equal(-1, r.ReadInt());
            Assert.Equal(0u, r.ReadUInt()); Assert.Equal(uint.MaxValue, r.ReadUInt());
            Assert.Equal(long.MinValue, r.ReadLong()); Assert.Equal(long.MaxValue, r.ReadLong());
            Assert.Equal(0ul, r.ReadULong()); Assert.Equal(ulong.MaxValue, r.ReadULong());
            Assert.True(r.IsAtEnd);
            Assert.Equal(0, r.Remaining);
        }

        [Fact]
        public void Encoding_IsLittleEndian()
        {
            var w = new NetWriter();
            w.WriteUInt(0x04030201);
            w.WriteUShort(0x0605);
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, w.ToArray());
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(1.5f)]
        [InlineData(float.MinValue)]
        [InlineData(float.MaxValue)]
        [InlineData(float.Epsilon)]
        [InlineData(float.PositiveInfinity)]
        [InlineData(float.NegativeInfinity)]
        [InlineData(float.NaN)]
        public void Float_RoundTripsBitExact(float value)
        {
            var r = RoundTrip(w => w.WriteFloat(value));
            float back = r.ReadFloat();
            Assert.Equal(BitConverter.SingleToInt32Bits(value), BitConverter.SingleToInt32Bits(back));
        }

        [Theory]
        [InlineData(0d)]
        [InlineData(Math.PI)]
        [InlineData(double.MinValue)]
        [InlineData(double.MaxValue)]
        [InlineData(double.Epsilon)]
        [InlineData(double.NaN)]
        [InlineData(double.NegativeInfinity)]
        public void Double_RoundTripsBitExact(double value)
        {
            var r = RoundTrip(w => w.WriteDouble(value));
            Assert.Equal(BitConverter.DoubleToInt64Bits(value), BitConverter.DoubleToInt64Bits(r.ReadDouble()));
        }

        [Fact]
        public void NegativeZero_KeepsSign()
        {
            var r = RoundTrip(w => { w.WriteFloat(-0f); w.WriteDouble(-0d); });
            Assert.True(float.IsNegative(r.ReadFloat()));
            Assert.True(double.IsNegative(r.ReadDouble()));
        }

        [Theory]
        [InlineData(0u, 1)]
        [InlineData(1u, 1)]
        [InlineData(127u, 1)]
        [InlineData(128u, 2)]
        [InlineData(16383u, 2)]
        [InlineData(16384u, 3)]
        [InlineData(2097151u, 3)]
        [InlineData(2097152u, 4)]
        [InlineData(268435455u, 4)]
        [InlineData(268435456u, 5)]
        [InlineData(uint.MaxValue, 5)]
        public void VarUInt_RoundTrip_AndSize(uint value, int expectedBytes)
        {
            var w = new NetWriter();
            w.WriteVarUInt(value);
            Assert.Equal(expectedBytes, w.Length);
            var r = new NetReader(w.ToArray());
            Assert.Equal(value, r.ReadVarUInt());
            Assert.True(r.IsAtEnd);
        }

        [Fact]
        public void VarUInt_KnownEncoding()
        {
            var w = new NetWriter();
            w.WriteVarUInt(300);
            Assert.Equal(new byte[] { 0xAC, 0x02 }, w.ToArray());
        }

        [Theory]
        [InlineData(0ul, 1)]
        [InlineData(127ul, 1)]
        [InlineData(128ul, 2)]
        [InlineData(0xFFFFFFFFul, 5)]
        [InlineData(0x7FFFFFFFFFFFFFFFul, 9)]
        [InlineData(ulong.MaxValue, 10)]
        public void VarULong_RoundTrip_AndSize(ulong value, int expectedBytes)
        {
            var w = new NetWriter();
            w.WriteVarULong(value);
            Assert.Equal(expectedBytes, w.Length);
            Assert.Equal(value, new NetReader(w.ToArray()).ReadVarULong());
        }

        [Theory]
        [InlineData(0, 1)]
        [InlineData(-1, 1)]
        [InlineData(1, 1)]
        [InlineData(-64, 1)]
        [InlineData(63, 1)]
        [InlineData(64, 2)]
        [InlineData(-65, 2)]
        [InlineData(int.MaxValue, 5)]
        [InlineData(int.MinValue, 5)]
        public void VarInt_ZigZag_RoundTrip(int value, int expectedBytes)
        {
            var w = new NetWriter();
            w.WriteVarInt(value);
            Assert.Equal(expectedBytes, w.Length);
            Assert.Equal(value, new NetReader(w.ToArray()).ReadVarInt());
        }

        [Theory]
        [InlineData(0L)]
        [InlineData(-1L)]
        [InlineData(long.MaxValue)]
        [InlineData(long.MinValue)]
        [InlineData(-1234567890123L)]
        public void VarLong_ZigZag_RoundTrip(long value)
        {
            var w = new NetWriter();
            w.WriteVarLong(value);
            Assert.Equal(value, new NetReader(w.ToArray()).ReadVarLong());
        }

        [Fact]
        public void VarInt_RandomValues_RoundTrip()
        {
            var rng = new Random(7);
            var w = new NetWriter();
            var values = new int[1000];
            var lvalues = new long[1000];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = rng.Next(int.MinValue, int.MaxValue);
                lvalues[i] = rng.NextInt64(long.MinValue, long.MaxValue);
                w.WriteVarInt(values[i]);
                w.WriteVarLong(lvalues[i]);
            }
            var r = new NetReader(w.AsSegment());
            for (int i = 0; i < values.Length; i++)
            {
                Assert.Equal(values[i], r.ReadVarInt());
                Assert.Equal(lvalues[i], r.ReadVarLong());
            }
            Assert.True(r.IsAtEnd);
        }

        [Theory]
        [InlineData("")]
        [InlineData("hello")]
        [InlineData("Golfer éè 中文 🏌")]
        public void String_RoundTrip(string value)
        {
            var r = RoundTrip(w => w.WriteString(value));
            Assert.Equal(value, r.ReadString());
            Assert.True(r.IsAtEnd);
        }

        [Fact]
        public void NullString_IsWrittenAsEmpty()
        {
            var r = RoundTrip(w => w.WriteString(null));
            Assert.Equal(string.Empty, r.ReadString());
        }

        [Fact]
        public void String_AtExactLimit_RoundTrips()
        {
            var s = new string('a', ProtocolInfo.MaxStringBytes);
            var r = RoundTrip(w => w.WriteString(s));
            Assert.Equal(s, r.ReadString());
        }

        [Fact]
        public void String_OverLimit_WriterThrows()
        {
            var w = new NetWriter();
            Assert.Throws<ArgumentException>(() => w.WriteString(new string('a', ProtocolInfo.MaxStringBytes + 1)));
        }

        [Fact]
        public void Bytes_RoundTrip_AllOverloads()
        {
            var data = new byte[] { 9, 8, 7, 6, 5 };
            var r = RoundTrip(w =>
            {
                w.WriteBytes(data);
                w.WriteBytes(new ArraySegment<byte>(data, 1, 3));
                w.WriteBytes(data, 4, 1);
                w.WriteBytes((byte[])null);
                w.WriteBytes(Array.Empty<byte>());
                w.WriteRaw(data, 0, 2);
            });
            Assert.Equal(data, r.ReadBytes());
            Assert.Equal(new byte[] { 8, 7, 6 }, r.ReadBytes());
            var seg = r.ReadBytesSegment();
            Assert.Equal(new byte[] { 5 }, seg.ToArray());
            Assert.Empty(r.ReadBytes());
            Assert.Empty(r.ReadBytes());
            Assert.Equal(new byte[] { 9, 8 }, r.ReadRaw(2));
            Assert.True(r.IsAtEnd);
        }

        [Fact]
        public void VectorQuaternionPeerId_RoundTrip()
        {
            var v = new NetVector3(1.25f, -2.5f, 1e6f);
            var q = new NetQuaternion(0.1f, 0.2f, 0.3f, 0.9f);
            var id = new PeerId(76561198000000001UL);
            var r = RoundTrip(w => { w.WriteVector3(v); w.WriteQuaternion(q); w.WritePeerId(id); });
            Assert.Equal(v, r.ReadVector3());
            Assert.Equal(q, r.ReadQuaternion());
            Assert.Equal(id, r.ReadPeerId());
            Assert.True(r.IsAtEnd);
        }

        [Fact]
        public void Writer_GrowsFromZeroCapacity_AndResetReuses()
        {
            var w = new NetWriter(0);
            for (int i = 0; i < 10000; i++)
                w.WriteInt(i);
            Assert.Equal(40000, w.Length);
            int capacity = w.Capacity;
            w.Reset();
            Assert.Equal(0, w.Length);
            Assert.Equal(capacity, w.Capacity);
            w.WriteByte(42);
            Assert.Equal(new byte[] { 42 }, w.ToArray());
        }

        [Fact]
        public void AsSegment_ViewsWrittenBytesOnly()
        {
            var w = new NetWriter(64);
            w.WriteByte(1);
            w.WriteByte(2);
            var seg = w.AsSegment();
            Assert.Equal(0, seg.Offset);
            Assert.Equal(2, seg.Count);
            Assert.Equal(new byte[] { 1, 2 }, seg.ToArray());
        }

        [Fact]
        public void BackPatching_OverwritesInPlace()
        {
            var w = new NetWriter();
            w.WriteByte(0);
            w.WriteUShort(0);
            w.WriteByte(0xFF);
            w.SetByteAt(0, 7);
            w.SetUShortAt(1, 0x1234);
            Assert.Equal(new byte[] { 7, 0x34, 0x12, 0xFF }, w.ToArray());
            Assert.Throws<ArgumentOutOfRangeException>(() => w.SetUShortAt(3, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => w.SetByteAt(4, 1));
        }

        [Fact]
        public void Reader_OverSubrange_RespectsBounds()
        {
            var data = new byte[] { 0xEE, 1, 2, 3, 4, 0xEE };
            var r = new NetReader(data, 1, 4);
            Assert.Equal(4, r.Length);
            Assert.Equal(0x04030201u, r.ReadUInt());
            Assert.Equal(4, r.Position);
            Assert.Throws<ProtocolException>(() => r.ReadByte());
        }

        [Fact]
        public void Reader_SetSource_Reuses()
        {
            var r = new NetReader();
            Assert.True(r.IsAtEnd);
            r.SetSource(new byte[] { 5 });
            Assert.Equal(5, r.PeekByte());
            Assert.Equal(5, r.ReadByte());
            r.SetSource(new ArraySegment<byte>(new byte[] { 1, 6 }, 1, 1));
            Assert.Equal(0, r.Position);
            Assert.Equal(6, r.ReadByte());
        }

        [Fact]
        public void Reader_SkipAndRemainingSegment()
        {
            var r = new NetReader(new byte[] { 1, 2, 3, 4, 5 });
            r.Skip(2);
            var rest = r.ReadRemainingSegment();
            Assert.Equal(new byte[] { 3, 4, 5 }, rest.ToArray());
            Assert.True(r.IsAtEnd);
            Assert.Throws<ProtocolException>(() => r.Skip(1));
        }

        [Fact]
        public void ToArray_IsIndependentCopy()
        {
            var w = new NetWriter();
            w.WriteByte(1);
            var a = w.ToArray();
            w.SetByteAt(0, 2);
            Assert.Equal(1, a[0]);
        }

        [Fact]
        public void Utf8Bytes_MatchStandardEncoding()
        {
            var w = new NetWriter();
            w.WriteString("hé");
            var expected = Encoding.UTF8.GetBytes("hé");
            var bytes = w.ToArray();
            Assert.Equal(expected.Length, bytes[0]);
            Assert.Equal(expected, bytes.AsSpan(1).ToArray());
        }
    }
}
