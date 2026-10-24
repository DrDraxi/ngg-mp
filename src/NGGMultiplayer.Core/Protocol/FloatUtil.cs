using System.Runtime.InteropServices;

namespace NGGMultiplayer.Core.Protocol
{
    internal static class FloatUtil
    {
        public static bool IsFinite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);

        // netstandard2.0 lacks BitConverter.SingleToInt32Bits.
        public static uint ToBits(float value) => new FloatBits { Float = value }.UInt;

        public static float FromBits(uint bits) => new FloatBits { UInt = bits }.Float;

        [StructLayout(LayoutKind.Explicit)]
        private struct FloatBits
        {
            [FieldOffset(0)] public float Float;
            [FieldOffset(0)] public uint UInt;
        }
    }
}
