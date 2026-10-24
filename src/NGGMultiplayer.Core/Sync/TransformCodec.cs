using System;
using NGGMultiplayer.Core.Protocol;

namespace NGGMultiplayer.Core.Sync
{
    public enum TransformEncoding : byte
    {
        /// <summary>Full 32-bit floats everywhere; lossless.</summary>
        Full,

        /// <summary>
        /// Full-precision position and time; rotation as 32-bit smallest-three; velocities as half floats
        /// (saturated to ±65504). Saves 24 to 36 bytes per state.
        /// </summary>
        Compact
    }

    /// <summary>
    /// Serializes <see cref="TransformState"/>. The header byte makes every state self-describing, so readers
    /// need not know which encoding the sender used. Zero velocities, zero flags and empty extras are omitted.
    /// <code>
    /// byte header   bit0 teleport, bit1 velocity, bit2 angularVelocity, bit3 flags, bit4 extra, bit5 compact
    /// double time
    /// vector3 position
    /// rotation      quaternion (16 bytes), or uint smallest-three when compact
    /// [velocity]    vector3, or 3 halfs when compact
    /// [angularVel]  vector3, or 3 halfs when compact
    /// [byte flags]
    /// [bytes extra] varuint length (1..255) + data
    /// </code>
    /// </summary>
    public static class TransformCodec
    {
        private const byte TeleportBit = 1 << 0;
        private const byte VelocityBit = 1 << 1;
        private const byte AngularVelocityBit = 1 << 2;
        private const byte FlagsBit = 1 << 3;
        private const byte ExtraBit = 1 << 4;
        private const byte CompactBit = 1 << 5;
        private const byte KnownBits = TeleportBit | VelocityBit | AngularVelocityBit | FlagsBit | ExtraBit | CompactBit;

        // Unit quaternions read in full precision are renormalized only when this far off, so valid input round-trips exactly.
        private const float NormTolerance = 1e-4f;

        /// <summary>Largest encoded size of one state.</summary>
        public const int MaxEncodedSize = 1 + 8 + 12 + 16 + 12 + 12 + 1 + 2 + TransformState.MaxExtraBytes;

        /// <exception cref="ArgumentException">The state is not <see cref="TransformState.IsValid"/>.</exception>
        public static void Write(NetWriter writer, in TransformState state, TransformEncoding encoding = TransformEncoding.Full)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));
            if (!state.IsValid)
                throw new ArgumentException("TransformState contains non-finite or out-of-range values or an oversized extra payload.", nameof(state));

            bool compact = encoding == TransformEncoding.Compact;
            bool hasVel = !IsZero(state.Velocity);
            bool hasAng = !IsZero(state.AngularVelocity);
            bool hasExtra = state.Extra != null && state.Extra.Length > 0;
            byte header = 0;
            if (state.Teleport) header |= TeleportBit;
            if (hasVel) header |= VelocityBit;
            if (hasAng) header |= AngularVelocityBit;
            if (state.Flags != 0) header |= FlagsBit;
            if (hasExtra) header |= ExtraBit;
            if (compact) header |= CompactBit;

            writer.EnsureCapacity(MaxEncodedSize);
            writer.WriteByte(header);
            writer.WriteDouble(state.Time);
            writer.WriteVector3(state.Position);
            if (compact)
                writer.WriteUInt(QuaternionCompression.Pack32(state.Rotation));
            else
                writer.WriteQuaternion(state.Rotation);
            if (hasVel)
                WriteVector(writer, state.Velocity, compact);
            if (hasAng)
                WriteVector(writer, state.AngularVelocity, compact);
            if (state.Flags != 0)
                writer.WriteByte(state.Flags);
            if (hasExtra)
                writer.WriteBytes(state.Extra);
        }

        /// <exception cref="ProtocolException">Truncated input, unknown header bits, or non-finite or out-of-range values.</exception>
        public static TransformState Read(NetReader reader)
        {
            if (reader == null)
                throw new ArgumentNullException(nameof(reader));
            byte header = reader.ReadByte();
            if ((header & ~KnownBits) != 0)
                throw new ProtocolException($"TransformState header has unknown bits 0x{header:X2}.");
            bool compact = (header & CompactBit) != 0;

            var state = new TransformState();
            state.Teleport = (header & TeleportBit) != 0;
            state.Time = reader.ReadDouble();
            if (double.IsNaN(state.Time) || double.IsInfinity(state.Time))
                throw new ProtocolException("TransformState time is not finite.");
            state.Position = reader.ReadVector3();
            if (!TransformState.IsPositionInRange(state.Position))
                throw new ProtocolException("TransformState position is not finite or out of range.");
            state.Rotation = compact ? QuaternionCompression.Unpack32(reader.ReadUInt()) : ReadRotation(reader);
            if ((header & VelocityBit) != 0)
                state.Velocity = ReadVector(reader, compact, "velocity");
            if ((header & AngularVelocityBit) != 0)
                state.AngularVelocity = ReadVector(reader, compact, "angular velocity");
            if ((header & FlagsBit) != 0)
                state.Flags = reader.ReadByte();
            if ((header & ExtraBit) != 0)
            {
                var extra = reader.ReadBytes(TransformState.MaxExtraBytes);
                if (extra.Length == 0)
                    throw new ProtocolException("TransformState extra flag set with an empty payload.");
                state.Extra = extra;
            }
            return state;
        }

        private static NetQuaternion ReadRotation(NetReader reader)
        {
            var q = reader.ReadQuaternion();
            if (!q.IsFinite)
                throw new ProtocolException("TransformState rotation is not finite.");
            float sq = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
            return Math.Abs(sq - 1f) > NormTolerance ? q.Normalized() : q;
        }

        private static void WriteVector(NetWriter writer, NetVector3 v, bool compact)
        {
            if (!compact)
            {
                writer.WriteVector3(v);
                return;
            }
            writer.WriteUShort(HalfFloat.FromFloatSaturating(v.x));
            writer.WriteUShort(HalfFloat.FromFloatSaturating(v.y));
            writer.WriteUShort(HalfFloat.FromFloatSaturating(v.z));
        }

        private static NetVector3 ReadVector(NetReader reader, bool compact, string what)
        {
            if (!compact)
            {
                var v = reader.ReadVector3();
                if (!TransformState.IsSpeedInRange(v))
                    throw new ProtocolException($"TransformState {what} is not finite or out of range.");
                return v;
            }
            ushort hx = reader.ReadUShort(), hy = reader.ReadUShort(), hz = reader.ReadUShort();
            if (HalfFloat.IsNonFinite(hx) || HalfFloat.IsNonFinite(hy) || HalfFloat.IsNonFinite(hz))
                throw new ProtocolException($"TransformState {what} is not finite.");
            return new NetVector3(HalfFloat.ToFloat(hx), HalfFloat.ToFloat(hy), HalfFloat.ToFloat(hz));
        }

        // Exact zero (including -0) is omitted; anything else is sent.
        private static bool IsZero(NetVector3 v) => v.x == 0f && v.y == 0f && v.z == 0f;
    }
}
