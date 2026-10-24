using NGGMultiplayer.Core.Protocol;

namespace NGGMultiplayer.Core.Sync
{
    /// <summary>IEEE 754 binary16 conversion (netstandard2.0 has no System.Half). Rounds to nearest even.</summary>
    public static class HalfFloat
    {
        public const float MaxValue = 65504f;

        private const ushort PositiveInfinity = 0x7C00;
        private const ushort NaN = 0x7E00;

        public static ushort FromFloat(float value)
        {
            uint bits = FloatUtil.ToBits(value);
            uint sign = (bits >> 16) & 0x8000u;
            int rawExp = (int)((bits >> 23) & 0xFF);
            uint mant = bits & 0x7FFFFFu;

            if (rawExp == 0xFF)
                return (ushort)(mant != 0 ? NaN : sign | PositiveInfinity);

            int exp = rawExp - 127 + 15;
            if (exp >= 31)
                return (ushort)(sign | PositiveInfinity);

            if (exp <= 0)
            {
                if (exp < -10)
                    return (ushort)sign;
                uint m = mant | 0x800000u;
                int shift = 14 - exp;
                uint h = m >> shift;
                uint rem = m & ((1u << shift) - 1);
                uint half = 1u << (shift - 1);
                if (rem > half || (rem == half && (h & 1) != 0))
                    h++;
                return (ushort)(sign | h);
            }

            uint result = sign | ((uint)exp << 10) | (mant >> 13);
            uint remainder = mant & 0x1FFFu;
            if (remainder > 0x1000u || (remainder == 0x1000u && (result & 1) != 0))
                result++;
            return (ushort)result;
        }

        public static float ToFloat(ushort half)
        {
            uint sign = (uint)(half & 0x8000) << 16;
            int exp = (half >> 10) & 0x1F;
            uint mant = (uint)(half & 0x3FF);

            if (exp == 0)
            {
                if (mant == 0)
                    return FloatUtil.FromBits(sign);
                int e = -14;
                while ((mant & 0x400u) == 0)
                {
                    mant <<= 1;
                    e--;
                }
                mant &= 0x3FFu;
                return FloatUtil.FromBits(sign | ((uint)(e + 127) << 23) | (mant << 13));
            }
            if (exp == 31)
                return FloatUtil.FromBits(sign | 0x7F800000u | (mant << 13));
            return FloatUtil.FromBits(sign | ((uint)(exp - 15 + 127) << 23) | (mant << 13));
        }

        /// <summary>True if the encoded value is infinity or NaN.</summary>
        public static bool IsNonFinite(ushort half) => (half & 0x7C00) == 0x7C00;

        /// <summary>Converts with saturation to ±<see cref="MaxValue"/>; NaN becomes 0. Never yields a non-finite half.</summary>
        public static ushort FromFloatSaturating(float value)
        {
            if (float.IsNaN(value))
                return 0;
            if (value > MaxValue)
                value = MaxValue;
            else if (value < -MaxValue)
                value = -MaxValue;
            return FromFloat(value);
        }
    }
}
