using System;

namespace NGGMultiplayer.Core.Save
{
    /// <summary>Standard CRC-32 (IEEE 802.3, reflected polynomial 0xEDB88320), as used by zip and PNG.</summary>
    public static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        public static uint Compute(byte[] data)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            return Append(0, data, 0, data.Length);
        }

        public static uint Compute(ArraySegment<byte> data)
        {
            if (data.Array == null)
                throw new ArgumentException("Segment has no backing array.", nameof(data));
            return Append(0, data.Array, data.Offset, data.Count);
        }

        public static uint Compute(byte[] data, int offset, int count) => Append(0, data, offset, count);

        /// <summary>
        /// Continues a CRC over more data: <c>Append(Compute(a), b)</c> equals the CRC of a followed by b.
        /// Start from 0 for an empty prefix.
        /// </summary>
        public static uint Append(uint crc, byte[] data, int offset, int count)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            if (offset < 0 || count < 0 || offset > data.Length - count)
                throw new ArgumentOutOfRangeException(nameof(offset), "Offset/count outside the array.");

            uint c = ~crc;
            int end = offset + count;
            for (int i = offset; i < end; i++)
                c = Table[(c ^ data[i]) & 0xFF] ^ (c >> 8);
            return ~c;
        }

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }
    }
}
