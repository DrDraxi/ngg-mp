using System;
using NGGMultiplayer.Core.Protocol;
using Xunit;

namespace NGGMultiplayer.Tests
{
    public class MalformedInputTests
    {
        [Fact]
        public void EmptyBuffer_EveryReadThrowsProtocolException()
        {
            var empty = Array.Empty<byte>();
            Action<NetReader>[] reads =
            {
                r => r.ReadByte(), r => r.PeekByte(), r => r.ReadSByte(), r => r.ReadBool(),
                r => r.ReadUShort(), r => r.ReadShort(), r => r.ReadInt(), r => r.ReadUInt(),
                r => r.ReadLong(), r => r.ReadULong(), r => r.ReadFloat(), r => r.ReadDouble(),
                r => r.ReadVarUInt(), r => r.ReadVarULong(), r => r.ReadVarInt(), r => r.ReadVarLong(),
                r => r.ReadString(), r => r.ReadBytes(), r => r.ReadBytesSegment(), r => r.ReadRaw(1),
                r => r.ReadVector3(), r => r.ReadQuaternion(), r => r.ReadPeerId(), r => r.Skip(1),
            };
            foreach (var read in reads)
                Assert.Throws<ProtocolException>(() => read(new NetReader(empty)));
        }

        [Fact]
        public void PartialFixedWidth_Throws_AndDoesNotAdvance()
        {
            var r = new NetReader(new byte[] { 1, 2, 3 });
            Assert.Throws<ProtocolException>(() => r.ReadUInt());
            Assert.Equal(0, r.Position);
            Assert.Throws<ProtocolException>(() => r.ReadVector3());
            Assert.Equal(3, r.Remaining);
        }

        [Fact]
        public void TruncatedVarint_Throws()
        {
            Assert.Throws<ProtocolException>(() => new NetReader(new byte[] { 0x80 }).ReadVarUInt());
            Assert.Throws<ProtocolException>(() => new NetReader(new byte[] { 0xFF, 0xFF }).ReadVarULong());
        }

        [Fact]
        public void VarUInt_TooLong_Throws()
        {
            var bytes = new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80, 0x00 };
            Assert.Throws<ProtocolException>(() => new NetReader(bytes).ReadVarUInt());
        }

        [Fact]
        public void VarUInt_Overflow_Throws()
        {
            // uint.MaxValue is FF FF FF FF 0F; 0x10 in the 5th byte would be bit 32.
            Assert.Equal(uint.MaxValue, new NetReader(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x0F }).ReadVarUInt());
            Assert.Throws<ProtocolException>(() => new NetReader(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x10 }).ReadVarUInt());
            Assert.Throws<ProtocolException>(() => new NetReader(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x8F, 0x00 }).ReadVarUInt());
        }

        [Fact]
        public void VarULong_TooLongOrOverflow_Throws()
        {
            var max = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01 };
            Assert.Equal(ulong.MaxValue, new NetReader(max).ReadVarULong());
            var overflow = (byte[])max.Clone();
            overflow[9] = 0x02;
            Assert.Throws<ProtocolException>(() => new NetReader(overflow).ReadVarULong());
            var tooLong = new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x00 };
            Assert.Throws<ProtocolException>(() => new NetReader(tooLong).ReadVarULong());
        }

        [Fact]
        public void InvalidBool_Throws()
        {
            Assert.Throws<ProtocolException>(() => new NetReader(new byte[] { 2 }).ReadBool());
        }

        [Fact]
        public void StringLengthBeyondBuffer_Throws()
        {
            var w = new NetWriter();
            w.WriteVarUInt(10);
            w.WriteByte((byte)'a');
            Assert.Throws<ProtocolException>(() => new NetReader(w.ToArray()).ReadString());
        }

        [Fact]
        public void StringOver64KB_Throws_EvenIfBytesPresent()
        {
            int len = ProtocolInfo.MaxStringBytes + 1;
            var w = new NetWriter();
            w.WriteVarUInt((uint)len);
            w.WriteRaw(new byte[len], 0, len);
            Assert.Throws<ProtocolException>(() => new NetReader(w.ToArray()).ReadString());
        }

        [Fact]
        public void String_CustomMaxBytes_Enforced()
        {
            var w = new NetWriter();
            w.WriteString("abcdef");
            Assert.Throws<ProtocolException>(() => new NetReader(w.ToArray()).ReadString(5));
            Assert.Equal("abcdef", new NetReader(w.ToArray()).ReadString(6));
        }

        [Fact]
        public void InvalidUtf8_Throws()
        {
            var bytes = new byte[] { 3, 0xC3, 0x28, 0xFF };
            Assert.Throws<ProtocolException>(() => new NetReader(bytes).ReadString());
        }

        [Fact]
        public void HugeBytesLengthPrefix_ThrowsWithoutAllocating()
        {
            var w = new NetWriter();
            w.WriteVarUInt(uint.MaxValue);
            Assert.Throws<ProtocolException>(() => new NetReader(w.ToArray()).ReadBytes());
            Assert.Throws<ProtocolException>(() => new NetReader(w.ToArray()).ReadBytesSegment());
        }

        [Fact]
        public void BytesMaxLength_Enforced()
        {
            var w = new NetWriter();
            w.WriteBytes(new byte[] { 1, 2, 3 });
            Assert.Throws<ProtocolException>(() => new NetReader(w.ToArray()).ReadBytes(2));
        }

        [Fact]
        public void RandomGarbage_NeverThrowsAnythingButProtocolException()
        {
            var rng = new Random(1234);
            var reader = new NetReader();
            for (int iter = 0; iter < 5000; iter++)
            {
                var data = new byte[rng.Next(0, 40)];
                rng.NextBytes(data);
                reader.SetSource(data);
                try
                {
                    while (true)
                    {
                        switch (rng.Next(8))
                        {
                            case 0: reader.ReadVarUInt(); break;
                            case 1: reader.ReadVarULong(); break;
                            case 2: reader.ReadString(); break;
                            case 3: reader.ReadBytes(); break;
                            case 4: reader.ReadBool(); break;
                            case 5: reader.ReadVector3(); break;
                            case 6: reader.ReadVarLong(); break;
                            default: reader.ReadUInt(); break;
                        }
                    }
                }
                catch (ProtocolException)
                {
                }
                Assert.InRange(reader.Remaining, 0, data.Length);
            }
        }

        [Fact]
        public void ConstructorRejectsBadRanges()
        {
            Assert.Throws<ArgumentNullException>(() => new NetReader((byte[])null));
            Assert.Throws<ArgumentOutOfRangeException>(() => new NetReader(new byte[4], 3, 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => new NetReader(new byte[4], -1, 2));
            Assert.Throws<ArgumentException>(() => new NetReader(default(ArraySegment<byte>)));
        }
    }
}
