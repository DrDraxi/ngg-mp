using System;
using System.Text;
using NGGMultiplayer.Core.Save;
using Xunit;

namespace NGGMultiplayer.Tests.Save
{
    public class Crc32Tests
    {
        [Theory]
        [InlineData("", 0x00000000u)]
        [InlineData("a", 0xE8B7BE43u)]
        [InlineData("123456789", 0xCBF43926u)]
        [InlineData("The quick brown fox jumps over the lazy dog", 0x414FA339u)]
        public void KnownVectors(string text, uint expected)
        {
            Assert.Equal(expected, Crc32.Compute(Encoding.ASCII.GetBytes(text)));
        }

        [Fact]
        public void AllZeroAndAllOnes()
        {
            Assert.Equal(0x190A55ADu, Crc32.Compute(new byte[32]));
            var ones = new byte[32];
            for (int i = 0; i < ones.Length; i++)
                ones[i] = 0xFF;
            Assert.Equal(0xFF6CAB0Bu, Crc32.Compute(ones));
        }

        [Fact]
        public void Append_MatchesOneShot_ForEverySplit()
        {
            var data = new byte[257];
            new Random(7).NextBytes(data);
            uint whole = Crc32.Compute(data);
            for (int split = 0; split <= data.Length; split++)
            {
                uint crc = Crc32.Compute(data, 0, split);
                crc = Crc32.Append(crc, data, split, data.Length - split);
                Assert.Equal(whole, crc);
            }
        }

        [Fact]
        public void SegmentAndRangeOverloads_AgreeWithArray()
        {
            var data = Encoding.ASCII.GetBytes("xx123456789yy");
            Assert.Equal(0xCBF43926u, Crc32.Compute(data, 2, 9));
            Assert.Equal(0xCBF43926u, Crc32.Compute(new ArraySegment<byte>(data, 2, 9)));
        }

        [Fact]
        public void SingleBitFlip_ChangesCrc()
        {
            var data = new byte[1000];
            new Random(3).NextBytes(data);
            uint original = Crc32.Compute(data);
            data[500] ^= 0x10;
            Assert.NotEqual(original, Crc32.Compute(data));
        }

        [Fact]
        public void InvalidArguments_Throw()
        {
            Assert.Throws<ArgumentNullException>(() => Crc32.Compute((byte[])null));
            Assert.Throws<ArgumentException>(() => Crc32.Compute(default(ArraySegment<byte>)));
            Assert.Throws<ArgumentOutOfRangeException>(() => Crc32.Compute(new byte[4], 3, 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => Crc32.Compute(new byte[4], -1, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => Crc32.Append(0, new byte[4], 0, -1));
        }
    }
}
