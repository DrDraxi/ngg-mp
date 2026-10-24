using System;
using System.Collections.Generic;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Game;
using NGGMultiplayer.Game.Progression;
using Xunit;

namespace NGGMultiplayer.Tests.GameProgression
{
    public class FactCodecTests
    {
        [Fact]
        public void Track_RoundTrips_AndRejectsGarbage()
        {
            var value = FactCodec.EncodeTrack(new TrackValue("Main_1", 7));
            Assert.True(FactCodec.TryDecodeTrack(value, out var track));
            Assert.Equal(new TrackValue("Main_1", 7), track);

            Assert.False(FactCodec.TryDecodeTrack(null, out _));
            Assert.False(FactCodec.TryDecodeTrack(new byte[0], out _));
            Assert.False(FactCodec.TryDecodeTrack(FactCodec.EncodeTrack(new TrackValue("", 1)), out _));
            Assert.False(FactCodec.TryDecodeTrack(FactCodec.EncodeTrack(new TrackValue("A", -1)), out _));
            var trailing = new byte[value.Length + 1];
            Array.Copy(value, trailing, value.Length);
            Assert.False(FactCodec.TryDecodeTrack(trailing, out _));
            Assert.False(FactCodec.TryDecodeTrack(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, out _));
        }

        [Fact]
        public void BountyFlags_RequireDone_AndOnlyKnownBits()
        {
            Assert.True(FactCodec.IsValidBountyFlags(FactCodec.BountyDone));
            Assert.True(FactCodec.IsValidBountyFlags(FactCodec.BountyDone | FactCodec.BountyOnEasy));
            Assert.False(FactCodec.IsValidBountyFlags(0));
            Assert.False(FactCodec.IsValidBountyFlags(FactCodec.BountyOnEasy));
            Assert.False(FactCodec.IsValidBountyFlags(5));
            Assert.False(FactCodec.TryDecodeFlags(new byte[] { 1, 1 }, out _));
        }

        [Fact]
        public void BoolAndInt_DecodeExactLengthsOnly()
        {
            Assert.True(FactCodec.TryDecodeBool(FactTable.EncodeBool(true), out bool b) && b);
            Assert.False(FactCodec.TryDecodeBool(new byte[] { 2 }, out _));
            Assert.False(FactCodec.TryDecodeBool(new byte[0], out _));
            Assert.True(FactCodec.TryDecodeInt(FactTable.EncodeInt(-42), out int i));
            Assert.Equal(-42, i);
            Assert.False(FactCodec.TryDecodeInt(new byte[3], out _));
            Assert.False(FactCodec.TryDecodeInt(new byte[5], out _));
        }

        [Fact]
        public void Obj_RoundTrips_WithAndWithoutRotation()
        {
            var plain = new ObjState(true, false, default);
            Assert.Single(FactCodec.EncodeObj(plain));
            Assert.True(FactCodec.TryDecodeObj(FactCodec.EncodeObj(plain), out var decoded));
            Assert.True(decoded.Active);
            Assert.False(decoded.HasRotation);

            var rotation = new NetQuaternion(0f, 0.7071068f, 0f, 0.7071068f);
            var rotated = new ObjState(false, true, rotation);
            var bytes = FactCodec.EncodeObj(rotated);
            Assert.Equal(17, bytes.Length);
            Assert.True(FactCodec.TryDecodeObj(bytes, out decoded));
            Assert.False(decoded.Active);
            Assert.True(decoded.HasRotation);
            Assert.True(FactCodec.AngleDegrees(rotation, decoded.Rotation) < 0.01f);
        }

        [Fact]
        public void Obj_RejectsMalformed()
        {
            Assert.False(FactCodec.TryDecodeObj(null, out _));
            Assert.False(FactCodec.TryDecodeObj(new byte[] { 4 }, out _));
            Assert.False(FactCodec.TryDecodeObj(new byte[] { 2 }, out _));
            Assert.False(FactCodec.TryDecodeObj(new byte[] { 1, 0 }, out _));
            var nan = FactCodec.EncodeObj(new ObjState(true, true, new NetQuaternion(float.NaN, 0, 0, 1)));
            Assert.False(FactCodec.TryDecodeObj(nan, out _));
            var zero = FactCodec.EncodeObj(new ObjState(true, true, new NetQuaternion(0, 0, 0, 0)));
            Assert.False(FactCodec.TryDecodeObj(zero, out _));
        }

        [Fact]
        public void ObjEquivalence_ToleratesNoiseAndSign()
        {
            var a = new ObjState(true, true, new NetQuaternion(0, 0, 0, 1));
            var negated = new ObjState(true, true, new NetQuaternion(0, 0, 0, -1));
            float half = (float)(0.2 * Math.PI / 180.0 / 2.0);
            var noisy = new ObjState(true, true, new NetQuaternion(0, (float)Math.Sin(half), 0, (float)Math.Cos(half)));
            float big = (float)(10 * Math.PI / 180.0 / 2.0);
            var turned = new ObjState(true, true, new NetQuaternion(0, (float)Math.Sin(big), 0, (float)Math.Cos(big)));

            Assert.True(FactCodec.ObjEquivalent(a, negated));
            Assert.True(FactCodec.ObjEquivalent(a, noisy));
            Assert.False(FactCodec.ObjEquivalent(a, turned));
            Assert.False(FactCodec.ObjEquivalent(a, new ObjState(false, true, a.Rotation)));
            Assert.False(FactCodec.ObjEquivalent(a, new ObjState(true, false, default)));
            Assert.True(FactCodec.Equivalent(FactKinds.Obj, FactCodec.EncodeObj(a), FactCodec.EncodeObj(noisy)));
            Assert.False(FactCodec.Equivalent(FactKinds.Bounty, new byte[] { 1 }, new byte[] { 3 }));
        }

        [Fact]
        public void StringSet_IsCanonical()
        {
            var a = FactCodec.EncodeStringSet(new[] { "b", "a", "b", "", null, "c" });
            var b = FactCodec.EncodeStringSet(new List<string> { "c", "a", "b" });
            Assert.Equal(a, b);
            Assert.True(FactCodec.TryDecodeStringSet(a, out var values));
            Assert.Equal(new[] { "a", "b", "c" }, values);

            var writer = new NetWriter(16);
            writer.WriteVarUInt(2);
            writer.WriteString("b");
            writer.WriteString("a");
            Assert.False(FactCodec.TryDecodeStringSet(writer.ToArray(), out _));

            var huge = new NetWriter(8);
            huge.WriteVarUInt(100000);
            Assert.False(FactCodec.TryDecodeStringSet(huge.ToArray(), out _));
            Assert.True(FactCodec.TryDecodeStringSet(FactCodec.EncodeStringSet(null), out var empty));
            Assert.Empty(empty);
        }

        [Fact]
        public void BoolList_RoundTrips_AndRejectsBadBytes()
        {
            var bytes = FactCodec.EncodeBoolList(new[] { true, false, true });
            Assert.True(FactCodec.TryDecodeBoolList(bytes, out var values));
            Assert.Equal(new[] { true, false, true }, values);
            Assert.False(FactCodec.TryDecodeBoolList(new byte[] { 2, 1, 2 }, out _));
            Assert.False(FactCodec.TryDecodeBoolList(new byte[] { 3, 1, 0 }, out _));
            Assert.False(FactCodec.TryDecodeBoolList(new byte[] { 1, 1, 0 }, out _));
        }
    }
}
