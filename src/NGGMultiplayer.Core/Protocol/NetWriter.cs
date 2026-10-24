using System;
using System.Text;
using NGGMultiplayer.Core.Net;

namespace NGGMultiplayer.Core.Protocol
{
    /// <summary>
    /// Growable little-endian packet writer. Reuse an instance with <see cref="Reset"/> (or rent one
    /// from <see cref="NetWriterPool"/>) to avoid per-packet allocations.
    /// </summary>
    public sealed class NetWriter
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, false);

        private byte[] _buffer;
        private int _length;

        public NetWriter(int initialCapacity = 256)
        {
            if (initialCapacity < 0)
                throw new ArgumentOutOfRangeException(nameof(initialCapacity));
            _buffer = initialCapacity == 0 ? Array.Empty<byte>() : new byte[initialCapacity];
        }

        public int Length => _length;

        public int Capacity => _buffer.Length;

        public void Reset() => _length = 0;

        public byte[] ToArray()
        {
            var result = new byte[_length];
            Buffer.BlockCopy(_buffer, 0, result, 0, _length);
            return result;
        }

        /// <summary>View over the written bytes. Valid only until the writer is next modified or reset.</summary>
        public ArraySegment<byte> AsSegment() => new ArraySegment<byte>(_buffer, 0, _length);

        public void EnsureCapacity(int additional)
        {
            if (additional < 0)
                throw new ArgumentOutOfRangeException(nameof(additional));
            long required = (long)_length + additional;
            if (required <= _buffer.Length)
                return;
            if (required > int.MaxValue)
                throw new InvalidOperationException("NetWriter cannot grow beyond 2 GB.");
            long newSize = Math.Max(Math.Max(_buffer.Length * 2L, 64), required);
            if (newSize > int.MaxValue)
                newSize = int.MaxValue;
            var grown = new byte[newSize];
            Buffer.BlockCopy(_buffer, 0, grown, 0, _length);
            _buffer = grown;
        }

        public void WriteByte(byte value)
        {
            EnsureCapacity(1);
            _buffer[_length++] = value;
        }

        public void WriteSByte(sbyte value) => WriteByte((byte)value);

        public void WriteBool(bool value) => WriteByte(value ? (byte)1 : (byte)0);

        public void WriteUShort(ushort value)
        {
            EnsureCapacity(2);
            _buffer[_length] = (byte)value;
            _buffer[_length + 1] = (byte)(value >> 8);
            _length += 2;
        }

        public void WriteShort(short value) => WriteUShort((ushort)value);

        public void WriteUInt(uint value)
        {
            EnsureCapacity(4);
            PutUInt(_buffer, _length, value);
            _length += 4;
        }

        public void WriteInt(int value) => WriteUInt((uint)value);

        public void WriteULong(ulong value)
        {
            EnsureCapacity(8);
            PutUInt(_buffer, _length, (uint)value);
            PutUInt(_buffer, _length + 4, (uint)(value >> 32));
            _length += 8;
        }

        public void WriteLong(long value) => WriteULong((ulong)value);

        public void WriteFloat(float value) => WriteUInt(FloatUtil.ToBits(value));

        public void WriteDouble(double value) => WriteULong((ulong)BitConverter.DoubleToInt64Bits(value));

        /// <summary>LEB128, 1-5 bytes.</summary>
        public void WriteVarUInt(uint value)
        {
            EnsureCapacity(5);
            while (value >= 0x80)
            {
                _buffer[_length++] = (byte)(value | 0x80);
                value >>= 7;
            }
            _buffer[_length++] = (byte)value;
        }

        /// <summary>LEB128, 1-10 bytes.</summary>
        public void WriteVarULong(ulong value)
        {
            EnsureCapacity(10);
            while (value >= 0x80)
            {
                _buffer[_length++] = (byte)(value | 0x80);
                value >>= 7;
            }
            _buffer[_length++] = (byte)value;
        }

        /// <summary>Zigzag + LEB128, so small negative numbers stay small.</summary>
        public void WriteVarInt(int value) => WriteVarUInt((uint)((value << 1) ^ (value >> 31)));

        public void WriteVarLong(long value) => WriteVarULong((ulong)((value << 1) ^ (value >> 63)));

        /// <summary>UTF-8 with a varint byte-length prefix. Null is written as the empty string.</summary>
        public void WriteString(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                WriteByte(0);
                return;
            }
            int byteCount = Utf8.GetByteCount(value);
            if (byteCount > ProtocolInfo.MaxStringBytes)
                throw new ArgumentException($"String is {byteCount} UTF-8 bytes; the limit is {ProtocolInfo.MaxStringBytes}.", nameof(value));
            WriteVarUInt((uint)byteCount);
            EnsureCapacity(byteCount);
            Utf8.GetBytes(value, 0, value.Length, _buffer, _length);
            _length += byteCount;
        }

        /// <summary>Varint length prefix followed by the bytes. Null is written as empty.</summary>
        public void WriteBytes(byte[] value)
        {
            if (value == null)
            {
                WriteByte(0);
                return;
            }
            WriteBytes(value, 0, value.Length);
        }

        public void WriteBytes(ArraySegment<byte> value)
        {
            if (value.Array == null)
            {
                WriteByte(0);
                return;
            }
            WriteBytes(value.Array, value.Offset, value.Count);
        }

        public void WriteBytes(byte[] source, int offset, int count)
        {
            CheckRange(source, offset, count);
            WriteVarUInt((uint)count);
            WriteRawUnchecked(source, offset, count);
        }

        /// <summary>Bytes without a length prefix.</summary>
        public void WriteRaw(byte[] source, int offset, int count)
        {
            CheckRange(source, offset, count);
            WriteRawUnchecked(source, offset, count);
        }

        public void WriteRaw(ArraySegment<byte> value)
        {
            if (value.Array == null)
                return;
            WriteRawUnchecked(value.Array, value.Offset, value.Count);
        }

        public void WriteVector3(NetVector3 value)
        {
            EnsureCapacity(12);
            PutUInt(_buffer, _length, FloatUtil.ToBits(value.x));
            PutUInt(_buffer, _length + 4, FloatUtil.ToBits(value.y));
            PutUInt(_buffer, _length + 8, FloatUtil.ToBits(value.z));
            _length += 12;
        }

        public void WriteQuaternion(NetQuaternion value)
        {
            EnsureCapacity(16);
            PutUInt(_buffer, _length, FloatUtil.ToBits(value.x));
            PutUInt(_buffer, _length + 4, FloatUtil.ToBits(value.y));
            PutUInt(_buffer, _length + 8, FloatUtil.ToBits(value.z));
            PutUInt(_buffer, _length + 12, FloatUtil.ToBits(value.w));
            _length += 16;
        }

        /// <summary>Fixed 8 bytes.</summary>
        public void WritePeerId(PeerId value) => WriteULong(value.Value);

        /// <summary>Overwrites a byte already written, e.g. to back-patch a count.</summary>
        public void SetByteAt(int position, byte value)
        {
            if ((uint)position >= (uint)_length)
                throw new ArgumentOutOfRangeException(nameof(position));
            _buffer[position] = value;
        }

        /// <summary>Overwrites two bytes already written, e.g. to back-patch a count.</summary>
        public void SetUShortAt(int position, ushort value)
        {
            if (position < 0 || position > _length - 2)
                throw new ArgumentOutOfRangeException(nameof(position));
            _buffer[position] = (byte)value;
            _buffer[position + 1] = (byte)(value >> 8);
        }

        private void WriteRawUnchecked(byte[] source, int offset, int count)
        {
            if (count == 0)
                return;
            EnsureCapacity(count);
            Buffer.BlockCopy(source, offset, _buffer, _length, count);
            _length += count;
        }

        private static void PutUInt(byte[] buffer, int index, uint value)
        {
            buffer[index] = (byte)value;
            buffer[index + 1] = (byte)(value >> 8);
            buffer[index + 2] = (byte)(value >> 16);
            buffer[index + 3] = (byte)(value >> 24);
        }

        private static void CheckRange(byte[] source, int offset, int count)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            if (offset < 0 || count < 0 || offset > source.Length - count)
                throw new ArgumentOutOfRangeException(nameof(offset), "Offset/count outside the source array.");
        }
    }
}
