using System;
using System.Text;
using NGGMultiplayer.Core.Net;

namespace NGGMultiplayer.Core.Protocol
{
    /// <summary>
    /// Little-endian reader over a byte range. Every read is bounds-checked and throws
    /// <see cref="ProtocolException"/> on malformed input; it never reads outside its range.
    /// An instance can be re-pointed with <see cref="SetSource(byte[], int, int)"/> to avoid allocations.
    /// </summary>
    public sealed class NetReader
    {
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        private byte[] _data;
        private int _start;
        private int _end;
        private int _pos;

        public NetReader()
        {
            _data = Array.Empty<byte>();
        }

        public NetReader(byte[] data)
        {
            SetSource(data);
        }

        public NetReader(byte[] data, int offset, int count)
        {
            SetSource(data, offset, count);
        }

        public NetReader(ArraySegment<byte> segment)
        {
            SetSource(segment);
        }

        /// <summary>Bytes consumed so far, relative to the start of the range.</summary>
        public int Position => _pos - _start;

        public int Length => _end - _start;

        public int Remaining => _end - _pos;

        public bool IsAtEnd => _pos >= _end;

        public void SetSource(byte[] data)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            SetSource(data, 0, data.Length);
        }

        public void SetSource(ArraySegment<byte> segment)
        {
            if (segment.Array == null)
                throw new ArgumentException("Segment has no backing array.", nameof(segment));
            SetSource(segment.Array, segment.Offset, segment.Count);
        }

        public void SetSource(byte[] data, int offset, int count)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            if (offset < 0 || count < 0 || offset > data.Length - count)
                throw new ArgumentOutOfRangeException(nameof(offset), "Offset/count outside the array.");
            _data = data;
            _start = offset;
            _pos = offset;
            _end = offset + count;
        }

        public void Skip(int count)
        {
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));
            Require(count);
            _pos += count;
        }

        public byte PeekByte()
        {
            Require(1);
            return _data[_pos];
        }

        public byte ReadByte()
        {
            Require(1);
            return _data[_pos++];
        }

        public sbyte ReadSByte() => (sbyte)ReadByte();

        /// <summary>Strict: only 0 and 1 are valid.</summary>
        public bool ReadBool()
        {
            byte b = ReadByte();
            if (b > 1)
                throw new ProtocolException($"Invalid bool byte 0x{b:X2}.");
            return b == 1;
        }

        public ushort ReadUShort()
        {
            Require(2);
            ushort value = (ushort)(_data[_pos] | (_data[_pos + 1] << 8));
            _pos += 2;
            return value;
        }

        public short ReadShort() => (short)ReadUShort();

        public uint ReadUInt()
        {
            Require(4);
            uint value = GetUInt(_pos);
            _pos += 4;
            return value;
        }

        public int ReadInt() => (int)ReadUInt();

        public ulong ReadULong()
        {
            Require(8);
            ulong value = GetUInt(_pos) | ((ulong)GetUInt(_pos + 4) << 32);
            _pos += 8;
            return value;
        }

        public long ReadLong() => (long)ReadULong();

        public float ReadFloat() => FloatUtil.FromBits(ReadUInt());

        public double ReadDouble() => BitConverter.Int64BitsToDouble((long)ReadULong());

        public uint ReadVarUInt()
        {
            uint result = 0;
            int shift = 0;
            for (int i = 0; i < 5; i++)
            {
                byte b = ReadByte();
                // The fifth byte may only carry the top 4 bits and must terminate.
                if (i == 4 && b > 0x0F)
                    throw new ProtocolException("VarUInt overflows 32 bits.");
                result |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                    return result;
                shift += 7;
            }
            throw new ProtocolException("VarUInt longer than 5 bytes.");
        }

        public ulong ReadVarULong()
        {
            ulong result = 0;
            int shift = 0;
            for (int i = 0; i < 10; i++)
            {
                byte b = ReadByte();
                if (i == 9 && b > 0x01)
                    throw new ProtocolException("VarULong overflows 64 bits.");
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                    return result;
                shift += 7;
            }
            throw new ProtocolException("VarULong longer than 10 bytes.");
        }

        public int ReadVarInt()
        {
            uint raw = ReadVarUInt();
            return (int)(raw >> 1) ^ -(int)(raw & 1);
        }

        public long ReadVarLong()
        {
            ulong raw = ReadVarULong();
            return (long)(raw >> 1) ^ -(long)(raw & 1);
        }

        /// <summary>Reads a varint-prefixed UTF-8 string, rejecting invalid UTF-8 and lengths over <paramref name="maxBytes"/>.</summary>
        public string ReadString(int maxBytes = ProtocolInfo.MaxStringBytes)
        {
            int count = ReadLength(Math.Min(maxBytes, ProtocolInfo.MaxStringBytes), "String");
            if (count == 0)
                return string.Empty;
            string value;
            try
            {
                value = StrictUtf8.GetString(_data, _pos, count);
            }
            catch (DecoderFallbackException e)
            {
                throw new ProtocolException("String is not valid UTF-8.", e);
            }
            _pos += count;
            return value;
        }

        /// <summary>Reads a varint-prefixed byte array into a new array.</summary>
        public byte[] ReadBytes(int maxLength = int.MaxValue)
        {
            int count = ReadLength(maxLength, "Byte array");
            var result = count == 0 ? Array.Empty<byte>() : new byte[count];
            if (count > 0)
            {
                Buffer.BlockCopy(_data, _pos, result, 0, count);
                _pos += count;
            }
            return result;
        }

        /// <summary>Zero-copy variant of <see cref="ReadBytes"/>; the segment aliases the packet buffer.</summary>
        public ArraySegment<byte> ReadBytesSegment(int maxLength = int.MaxValue)
        {
            int count = ReadLength(maxLength, "Byte array");
            var segment = new ArraySegment<byte>(_data, _pos, count);
            _pos += count;
            return segment;
        }

        /// <summary>Reads exactly <paramref name="count"/> bytes with no length prefix.</summary>
        public byte[] ReadRaw(int count)
        {
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));
            Require(count);
            var result = count == 0 ? Array.Empty<byte>() : new byte[count];
            if (count > 0)
            {
                Buffer.BlockCopy(_data, _pos, result, 0, count);
                _pos += count;
            }
            return result;
        }

        /// <summary>The unread remainder as a zero-copy segment; consumes it.</summary>
        public ArraySegment<byte> ReadRemainingSegment()
        {
            var segment = new ArraySegment<byte>(_data, _pos, _end - _pos);
            _pos = _end;
            return segment;
        }

        public NetVector3 ReadVector3()
        {
            Require(12);
            var v = new NetVector3(
                FloatUtil.FromBits(GetUInt(_pos)),
                FloatUtil.FromBits(GetUInt(_pos + 4)),
                FloatUtil.FromBits(GetUInt(_pos + 8)));
            _pos += 12;
            return v;
        }

        public NetQuaternion ReadQuaternion()
        {
            Require(16);
            var q = new NetQuaternion(
                FloatUtil.FromBits(GetUInt(_pos)),
                FloatUtil.FromBits(GetUInt(_pos + 4)),
                FloatUtil.FromBits(GetUInt(_pos + 8)),
                FloatUtil.FromBits(GetUInt(_pos + 12)));
            _pos += 16;
            return q;
        }

        public PeerId ReadPeerId() => new PeerId(ReadULong());

        private int ReadLength(int max, string what)
        {
            uint count = ReadVarUInt();
            if (count > (uint)Math.Max(max, 0))
                throw new ProtocolException($"{what} length {count} exceeds limit {max}.");
            if (count > (uint)Remaining)
                throw new ProtocolException($"{what} length {count} exceeds remaining {Remaining} bytes.");
            return (int)count;
        }

        private void Require(int count)
        {
            if (count > _end - _pos)
                throw new ProtocolException($"Read past end of packet: need {count} bytes at offset {Position}, {Remaining} remaining.");
        }

        private uint GetUInt(int index) =>
            (uint)(_data[index] | (_data[index + 1] << 8) | (_data[index + 2] << 16) | (_data[index + 3] << 24));
    }
}
