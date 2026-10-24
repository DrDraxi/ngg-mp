using System;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Sync;
using Xunit;

namespace NGGMultiplayer.Tests.Sync
{
    public class TransformCodecTests
    {
        private static TransformState Full()
        {
            var s = new TransformState(new NetVector3(1.5f, -2.25f, 1000.125f),
                new NetQuaternion(0.1f, 0.2f, 0.3f, 0.9f).Normalized(), 12345.678901);
            s.Velocity = new NetVector3(10f, -3f, 0.5f);
            s.AngularVelocity = new NetVector3(0f, 6.28f, -1f);
            s.Teleport = true;
            s.Flags = 0xA5;
            s.Extra = new byte[] { 1, 2, 3 };
            return s;
        }

        private static TransformState RoundTrip(in TransformState s, TransformEncoding encoding, out int size)
        {
            var w = new NetWriter();
            TransformCodec.Write(w, s, encoding);
            size = w.Length;
            var r = new NetReader(w.ToArray());
            var result = TransformCodec.Read(r);
            Assert.True(r.IsAtEnd);
            return result;
        }

        [Fact]
        public void Full_RoundTripsExactly()
        {
            var s = Full();
            var back = RoundTrip(s, TransformEncoding.Full, out _);
            Assert.Equal(s.Position, back.Position);
            Assert.Equal(s.Rotation, back.Rotation);
            Assert.Equal(s.Velocity, back.Velocity);
            Assert.Equal(s.AngularVelocity, back.AngularVelocity);
            Assert.Equal(s.Time, back.Time);
            Assert.True(back.Teleport);
            Assert.Equal(0xA5, back.Flags);
            Assert.Equal(s.Extra, back.Extra);
        }

        [Fact]
        public void RestingState_OmitsOptionalFields()
        {
            var s = States.At(1, 2, 3, 4.0);
            var back = RoundTrip(s, TransformEncoding.Full, out int size);
            Assert.Equal(1 + 8 + 12 + 16, size);
            Assert.False(back.Teleport);
            Assert.Equal(0, back.Flags);
            Assert.Null(back.Extra);
            Assert.Equal(NetVector3.Zero, back.Velocity);
        }

        [Fact]
        public void EmptyExtra_IsOmitted()
        {
            var s = States.At(1);
            s.Extra = Array.Empty<byte>();
            Assert.Null(RoundTrip(s, TransformEncoding.Full, out _).Extra);
        }

        [Fact]
        public void Compact_IsSmallerAndWithinTolerance()
        {
            var s = Full();
            RoundTrip(s, TransformEncoding.Full, out int fullSize);
            var back = RoundTrip(s, TransformEncoding.Compact, out int compactSize);
            Assert.Equal(fullSize - 12 - 6 - 6, compactSize);
            Assert.Equal(s.Position, back.Position);
            Assert.Equal(s.Time, back.Time);
            Assert.True(TransformMath.AngleDegrees(s.Rotation, back.Rotation) < 0.25);
            Assert.Equal(s.Velocity.x, back.Velocity.x, 2);
            Assert.Equal(s.Velocity.y, back.Velocity.y, 2);
            Assert.Equal(s.AngularVelocity.y, back.AngularVelocity.y, 2);
            Assert.Equal(s.Extra, back.Extra);
            Assert.True(back.Teleport);
        }

        [Fact]
        public void Compact_SaturatesHugeVelocities()
        {
            var s = States.Moving(0, TransformState.MaxSpeed);
            var back = RoundTrip(s, TransformEncoding.Compact, out _);
            Assert.Equal(HalfFloat.MaxValue, back.Velocity.x);
        }

        [Fact]
        public void Write_RejectsInvalidState()
        {
            var w = new NetWriter();
            var s = States.At(float.NaN);
            Assert.Throws<ArgumentException>(() => TransformCodec.Write(w, s));
            s = States.At(0);
            s.Extra = new byte[TransformState.MaxExtraBytes + 1];
            Assert.Throws<ArgumentException>(() => TransformCodec.Write(w, s));
            s = States.At(0, time: double.PositiveInfinity);
            Assert.Throws<ArgumentException>(() => TransformCodec.Write(w, s));
        }

        [Fact]
        public void MaxExtra_FitsMaxEncodedSize()
        {
            var s = Full();
            s.Extra = new byte[TransformState.MaxExtraBytes];
            RoundTrip(s, TransformEncoding.Full, out int size);
            Assert.Equal(TransformCodec.MaxEncodedSize, size);
        }

        [Fact]
        public void Read_NonUnitRotationIsNormalized()
        {
            var w = new NetWriter();
            w.WriteByte(0);
            w.WriteDouble(1);
            w.WriteVector3(NetVector3.Zero);
            w.WriteQuaternion(new NetQuaternion(0, 0, 0, 5));
            Assert.Equal(NetQuaternion.Identity, TransformCodec.Read(new NetReader(w.ToArray())).Rotation);

            w.Reset();
            w.WriteByte(0);
            w.WriteDouble(1);
            w.WriteVector3(NetVector3.Zero);
            w.WriteQuaternion(new NetQuaternion(0, 0, 0, 0));
            Assert.Equal(NetQuaternion.Identity, TransformCodec.Read(new NetReader(w.ToArray())).Rotation);
        }

        [Theory]
        [InlineData("reserved header bit")]
        [InlineData("nan time")]
        [InlineData("nan position")]
        [InlineData("inf rotation")]
        [InlineData("nan velocity")]
        [InlineData("half infinity")]
        [InlineData("empty extra")]
        [InlineData("oversized extra")]
        [InlineData("truncated")]
        public void Read_RejectsMalformed(string kind)
        {
            var w = new NetWriter();
            byte header = 0;
            switch (kind)
            {
                case "reserved header bit": header = 0x40; break;
                case "nan velocity": header = 0x02; break;
                case "half infinity": header = 0x02 | 0x20; break;
                case "empty extra":
                case "oversized extra": header = 0x10; break;
            }
            w.WriteByte(header);
            w.WriteDouble(kind == "nan time" ? double.NaN : 1.0);
            w.WriteVector3(kind == "nan position" ? new NetVector3(float.NaN, 0, 0) : NetVector3.Zero);
            if ((header & 0x20) != 0)
                w.WriteUInt(0);
            else
                w.WriteQuaternion(kind == "inf rotation" ? new NetQuaternion(float.PositiveInfinity, 0, 0, 1) : NetQuaternion.Identity);
            if (kind == "nan velocity")
                w.WriteVector3(new NetVector3(0, float.NaN, 0));
            if (kind == "half infinity")
            {
                w.WriteUShort(0x7C00);
                w.WriteUShort(0);
                w.WriteUShort(0);
            }
            if (kind == "empty extra")
                w.WriteBytes(Array.Empty<byte>());
            if (kind == "oversized extra")
                w.WriteBytes(new byte[300]);

            var bytes = w.ToArray();
            if (kind == "truncated")
                Array.Resize(ref bytes, bytes.Length - 1);
            Assert.Throws<ProtocolException>(() => TransformCodec.Read(new NetReader(bytes)));
        }

        [Fact]
        public void Read_RandomGarbageOnlyThrowsProtocolException()
        {
            var rng = new Random(7);
            for (int i = 0; i < 20000; i++)
            {
                var bytes = new byte[rng.Next(0, 80)];
                rng.NextBytes(bytes);
                if (bytes.Length > 0)
                    bytes[0] &= 0x3F;
                try
                {
                    var s = TransformCodec.Read(new NetReader(bytes));
                    Assert.True(s.IsValid);
                    Assert.True(Math.Abs(NetQuaternion.Dot(s.Rotation, s.Rotation) - 1f) < 1e-3f);
                }
                catch (ProtocolException)
                {
                }
            }
        }
    }

    public class QuaternionCompressionTests
    {
        private static NetQuaternion RandomRotation(Random rng)
        {
            // Uniform random rotation (Shoemake).
            double u1 = rng.NextDouble(), u2 = rng.NextDouble(), u3 = rng.NextDouble();
            double a = Math.Sqrt(1 - u1), b = Math.Sqrt(u1);
            return new NetQuaternion(
                (float)(a * Math.Sin(2 * Math.PI * u2)),
                (float)(a * Math.Cos(2 * Math.PI * u2)),
                (float)(b * Math.Sin(2 * Math.PI * u3)),
                (float)(b * Math.Cos(2 * Math.PI * u3)));
        }

        [Theory]
        [InlineData(10, 0.25)]
        [InlineData(15, 0.01)]
        [InlineData(20, 0.0005)]
        public void RoundTrip_ErrorIsBounded(int bits, double maxDegrees)
        {
            var rng = new Random(bits);
            double worst = 0;
            for (int i = 0; i < 20000; i++)
            {
                var q = RandomRotation(rng);
                var back = QuaternionCompression.Unpack(QuaternionCompression.Pack(q, bits), bits);
                worst = Math.Max(worst, TransformMath.AngleDegrees(q, back));
            }
            Assert.True(worst < maxDegrees, $"worst error {worst} degrees");
        }

        [Fact]
        public void Pack32_UsesAllBitsAndRoundTrips()
        {
            Assert.Equal(32, QuaternionCompression.PackedBits(QuaternionCompression.Bits32));
            var q = new NetQuaternion(0.5f, -0.5f, 0.5f, 0.5f);
            Assert.True(TransformMath.AngleDegrees(q, QuaternionCompression.Unpack32(QuaternionCompression.Pack32(q))) < 0.25);
        }

        [Fact]
        public void NegatedQuaternion_PacksIdentically()
        {
            var rng = new Random(3);
            for (int i = 0; i < 1000; i++)
            {
                var q = RandomRotation(rng);
                var neg = new NetQuaternion(-q.x, -q.y, -q.z, -q.w);
                Assert.Equal(QuaternionCompression.Pack32(q), QuaternionCompression.Pack32(neg));
            }
        }

        [Theory]
        [InlineData(1f, 0f, 0f, 0f)]
        [InlineData(0f, 1f, 0f, 0f)]
        [InlineData(0f, 0f, 1f, 0f)]
        [InlineData(0f, 0f, 0f, 1f)]
        [InlineData(0f, 0f, 0f, -1f)]
        [InlineData(0.7071068f, 0f, 0f, 0.7071068f)]
        public void AxisAlignedAndEdgeCases_RoundTrip(float x, float y, float z, float w)
        {
            var q = new NetQuaternion(x, y, z, w);
            var back = QuaternionCompression.Unpack(QuaternionCompression.Pack(q, 12), 12);
            Assert.True(TransformMath.AngleDegrees(q, back) < 0.05);
        }

        [Fact]
        public void Pack_DegenerateInputBecomesIdentity()
        {
            var back = QuaternionCompression.Unpack32(QuaternionCompression.Pack32(new NetQuaternion(float.NaN, 0, 0, 0)));
            Assert.True(TransformMath.AngleDegrees(NetQuaternion.Identity, back) < 0.25);
        }

        [Fact]
        public void Unpack_AnyBitsYieldUnitQuaternion()
        {
            var rng = new Random(11);
            var buf = new byte[8];
            for (int i = 0; i < 20000; i++)
            {
                rng.NextBytes(buf);
                ulong bits = BitConverter.ToUInt64(buf, 0);
                var q = QuaternionCompression.Unpack(bits, 4 + i % 17);
                Assert.True(q.IsFinite);
                Assert.True(Math.Abs(NetQuaternion.Dot(q, q) - 1f) < 1e-4f);
            }
            Assert.True(QuaternionCompression.Unpack32(uint.MaxValue).IsFinite);
        }

        [Theory]
        [InlineData(3)]
        [InlineData(21)]
        public void InvalidBitWidth_Throws(int bits)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => QuaternionCompression.Pack(NetQuaternion.Identity, bits));
            Assert.Throws<ArgumentOutOfRangeException>(() => QuaternionCompression.Unpack(0, bits));
        }
    }

    public class HalfFloatTests
    {
        [Theory]
        [InlineData(0f, 0x0000)]
        [InlineData(1f, 0x3C00)]
        [InlineData(-2f, 0xC000)]
        [InlineData(65504f, 0x7BFF)]
        [InlineData(0.5f, 0x3800)]
        [InlineData(5.9604645e-8f, 0x0001)]
        [InlineData(6.1035156e-5f, 0x0400)]
        public void KnownValues(float value, int bits)
        {
            Assert.Equal((ushort)bits, HalfFloat.FromFloat(value));
            Assert.Equal(value, HalfFloat.ToFloat((ushort)bits));
        }

        [Fact]
        public void EveryFiniteHalf_RoundTripsThroughFloat()
        {
            for (int h = 0; h < 0x10000; h++)
            {
                var half = (ushort)h;
                if (HalfFloat.IsNonFinite(half))
                    continue;
                ushort back = HalfFloat.FromFloat(HalfFloat.ToFloat(half));
                // -0 and +0 are distinct encodings and both survive.
                Assert.Equal(half, back);
            }
        }

        [Fact]
        public void RoundsToNearestEven()
        {
            // 1 + 2^-11 is exactly between 1 and the next half (1 + 2^-10): ties go to even (1).
            Assert.Equal(0x3C00, HalfFloat.FromFloat(1f + 1f / 2048f));
            // 1 + 3 * 2^-11 ties between 1 + 2^-10 (odd) and 1 + 2^-9 (even).
            Assert.Equal(0x3C02, HalfFloat.FromFloat(1f + 3f / 2048f));
            Assert.Equal(0x3C01, HalfFloat.FromFloat(1f + 1.1f / 1024f));
        }

        [Fact]
        public void SpecialValues()
        {
            Assert.Equal(0x7C00, HalfFloat.FromFloat(float.PositiveInfinity));
            Assert.Equal(0xFC00, HalfFloat.FromFloat(float.NegativeInfinity));
            Assert.Equal(0x7C00, HalfFloat.FromFloat(70000f));
            Assert.True(HalfFloat.IsNonFinite(HalfFloat.FromFloat(float.NaN)));
            Assert.Equal(0, HalfFloat.FromFloat(1e-10f));
            Assert.Equal(0x7BFF, HalfFloat.FromFloatSaturating(1e30f));
            Assert.Equal(0xFBFF, HalfFloat.FromFloatSaturating(float.NegativeInfinity));
            Assert.Equal(0, HalfFloat.FromFloatSaturating(float.NaN));
            Assert.True(float.IsPositiveInfinity(HalfFloat.ToFloat(0x7C00)));
            Assert.True(float.IsNaN(HalfFloat.ToFloat(0x7E00)));
        }

        [Fact]
        public void RelativeErrorIsBounded()
        {
            var rng = new Random(5);
            for (int i = 0; i < 10000; i++)
            {
                float v = (float)((rng.NextDouble() * 2 - 1) * 1000);
                if (Math.Abs(v) < 1e-3f)
                    continue;
                float back = HalfFloat.ToFloat(HalfFloat.FromFloat(v));
                Assert.True(Math.Abs(back - v) <= Math.Abs(v) / 2048f + 1e-7f, $"{v} -> {back}");
            }
        }
    }

    public class TransformMathTests
    {
        private static readonly NetVector3 Up = new NetVector3(0, 1, 0);

        [Fact]
        public void Slerp_MidpointOfQuarterTurn_IsEighthTurn()
        {
            var a = NetQuaternion.Identity;
            var b = TransformMath.AxisAngle(Up, Math.PI / 2);
            var mid = TransformMath.Slerp(a, b, 0.5f);
            Assert.Equal(45.0, TransformMath.AngleDegrees(a, mid), 3);
            Assert.Equal(45.0, TransformMath.AngleDegrees(mid, b), 3);
            var expected = TransformMath.AxisAngle(Up, Math.PI / 4);
            Assert.True(TransformMath.AngleDegrees(expected, mid) < 1e-3);
        }

        [Fact]
        public void Slerp_IsConstantSpeed()
        {
            var a = TransformMath.AxisAngle(new NetVector3(1, 0, 0), 0.3);
            var b = TransformMath.AxisAngle(new NetVector3(0, 0, 1), 2.5);
            double total = TransformMath.AngleDegrees(a, b);
            for (int i = 1; i < 10; i++)
            {
                float t = i / 10f;
                Assert.Equal(total * t, TransformMath.AngleDegrees(a, TransformMath.Slerp(a, b, t)), 2);
            }
        }

        [Fact]
        public void Slerp_TakesShortestPath()
        {
            var a = TransformMath.AxisAngle(Up, 0.1);
            var b = TransformMath.AxisAngle(Up, 0.3);
            var bNeg = new NetQuaternion(-b.x, -b.y, -b.z, -b.w);
            var mid = TransformMath.Slerp(a, bNeg, 0.5f);
            Assert.True(TransformMath.AngleDegrees(TransformMath.AxisAngle(Up, 0.2), mid) < 1e-3);
        }

        [Fact]
        public void Slerp_ClampsAndHandlesNearlyEqualInputs()
        {
            var a = TransformMath.AxisAngle(Up, 1.0);
            var b = TransformMath.AxisAngle(Up, 1.00001);
            Assert.Equal(a, TransformMath.Slerp(a, b, -1f));
            Assert.Equal(b, TransformMath.Slerp(a, b, 2f));
            var mid = TransformMath.Slerp(a, b, 0.5f);
            Assert.True(mid.IsFinite);
            Assert.True(Math.Abs(NetQuaternion.Dot(mid, mid) - 1f) < 1e-5f);
        }

        [Fact]
        public void Integrate_AngularVelocityRotatesAboutAxis()
        {
            var spin = new NetVector3(0, (float)Math.PI, 0); // half a turn per second
            var q = TransformMath.Integrate(NetQuaternion.Identity, spin, 0.5);
            Assert.True(TransformMath.AngleDegrees(TransformMath.AxisAngle(Up, Math.PI / 2), q) < 1e-3);
            Assert.Equal(NetQuaternion.Identity, TransformMath.Integrate(NetQuaternion.Identity, NetVector3.Zero, 1));
        }

        [Fact]
        public void Integrate_IsWorldSpace()
        {
            // Start rotated 90 degrees about X, then spin about world Y: equals AxisAngle(Y) * start.
            var start = TransformMath.AxisAngle(new NetVector3(1, 0, 0), Math.PI / 2);
            var q = TransformMath.Integrate(start, new NetVector3(0, 1, 0), 0.7);
            var expected = TransformMath.Multiply(TransformMath.AxisAngle(Up, 0.7), start);
            Assert.True(TransformMath.AngleDegrees(expected, q) < 1e-3);
        }

        [Fact]
        public void Multiply_IdentityIsNeutral()
        {
            var q = TransformMath.AxisAngle(new NetVector3(1, 2, 3), 1.2);
            Assert.True(TransformMath.AngleDegrees(q, TransformMath.Multiply(NetQuaternion.Identity, q)) < 1e-4);
            Assert.True(TransformMath.AngleDegrees(q, TransformMath.Multiply(q, NetQuaternion.Identity)) < 1e-4);
        }

        [Fact]
        public void AxisAngle_DegenerateAxisIsIdentity()
        {
            Assert.Equal(NetQuaternion.Identity, TransformMath.AxisAngle(NetVector3.Zero, 1));
            Assert.Equal(NetQuaternion.Identity, TransformMath.AxisAngle(new NetVector3(float.NaN, 0, 0), 1));
        }

        [Fact]
        public void Interpolate_LerpsContinuousFieldsAndKeepsDiscreteFromA()
        {
            var a = States.Moving(0, 2, 1.0);
            a.Flags = 1;
            a.Extra = new byte[] { 9 };
            var b = States.Moving(10, 4, 2.0);
            b.Flags = 2;
            b.Rotation = TransformMath.AxisAngle(Up, Math.PI / 2);
            var r = TransformMath.Interpolate(a, b, 0.25f);
            Assert.Equal(2.5f, r.Position.x, 5);
            Assert.Equal(2.5f, r.Velocity.x, 5);
            Assert.Equal(1.25, r.Time, 9);
            Assert.Equal(22.5, TransformMath.AngleDegrees(NetQuaternion.Identity, r.Rotation), 3);
            Assert.Equal(1, r.Flags);
            Assert.Same(a.Extra, r.Extra);
        }

        [Fact]
        public void Extrapolate_UsesVelocities()
        {
            var s = States.Moving(1, 3, 5.0);
            s.AngularVelocity = new NetVector3(0, 2, 0);
            var r = TransformMath.Extrapolate(s, 0.5);
            Assert.Equal(2.5f, r.Position.x, 5);
            Assert.Equal(5.5, r.Time, 9);
            Assert.Equal(TransformMath.AngleDegrees(NetQuaternion.Identity, TransformMath.AxisAngle(Up, 1.0)),
                TransformMath.AngleDegrees(NetQuaternion.Identity, r.Rotation), 3);
            Assert.Equal(s.Position, TransformMath.Extrapolate(s, -1).Position);
        }

        [Fact]
        public void NetEntityId_Basics()
        {
            Assert.False(NetEntityId.None.IsValid);
            Assert.True(new NetEntityId(3).IsValid);
            Assert.Equal(new NetEntityId(3), new NetEntityId(3));
            Assert.True(new NetEntityId(3) != new NetEntityId(4));
            Assert.Equal("#3", new NetEntityId(3).ToString());

            var w = new NetWriter();
            NetEntityId.Write(w, new NetEntityId(300));
            Assert.Equal(new NetEntityId(300), NetEntityId.Read(new NetReader(w.ToArray())));
            Assert.Throws<ProtocolException>(() => NetEntityId.Read(new NetReader(new byte[] { 0 })));
        }
    }
}
