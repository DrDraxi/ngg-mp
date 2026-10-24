using System;
using NGGMultiplayer.Core.Protocol;

namespace NGGMultiplayer.Core.Sync
{
    /// <summary>
    /// "Smallest three" quaternion compression: the largest component is dropped (it is recovered from the unit
    /// length), its index stored in 2 bits, and the other three quantized over [-1/sqrt2, 1/sqrt2]. q and -q are
    /// the same rotation, so the sign is normalized so the dropped component is positive.
    /// Unpacking never fails: any bit pattern yields a finite unit quaternion.
    /// </summary>
    public static class QuaternionCompression
    {
        public const int MinBitsPerComponent = 4;
        public const int MaxBitsPerComponent = 20;

        /// <summary>Bits per component of the 32-bit form (2 + 3 * 10 = 32); worst-case error is under 0.25 degrees.</summary>
        public const int Bits32 = 10;

        private const double Range = 0.70710678118654752440;

        /// <summary>Total packed size in bits for a component width.</summary>
        public static int PackedBits(int bitsPerComponent) => 2 + 3 * bitsPerComponent;

        public static uint Pack32(NetQuaternion q) => (uint)Pack(q, Bits32);

        public static NetQuaternion Unpack32(uint packed) => Unpack(packed, Bits32);

        public static ulong Pack(NetQuaternion q, int bitsPerComponent)
        {
            CheckBits(bitsPerComponent);
            q = q.Normalized();
            float ax = Math.Abs(q.x), ay = Math.Abs(q.y), az = Math.Abs(q.z), aw = Math.Abs(q.w);
            int largest = 3;
            float max = aw;
            if (ax > max) { largest = 0; max = ax; }
            if (ay > max) { largest = 1; max = ay; }
            if (az > max) { largest = 2; }

            float lv = Component(q, largest);
            float sign = lv < 0f ? -1f : 1f;
            ulong maxQ = (1UL << bitsPerComponent) - 1;
            ulong packed = (ulong)largest;
            for (int i = 0; i < 4; i++)
            {
                if (i == largest)
                    continue;
                double c = Component(q, i) * sign;
                double unit = (c + Range) / (2.0 * Range);
                if (unit < 0.0)
                    unit = 0.0;
                else if (unit > 1.0)
                    unit = 1.0;
                packed = (packed << bitsPerComponent) | (ulong)Math.Round(unit * maxQ);
            }
            return packed;
        }

        /// <summary>Inverse of <see cref="Pack"/>. Bits above <see cref="PackedBits"/> are ignored.</summary>
        public static NetQuaternion Unpack(ulong packed, int bitsPerComponent)
        {
            CheckBits(bitsPerComponent);
            ulong maxQ = (1UL << bitsPerComponent) - 1;
            int largest = (int)((packed >> (3 * bitsPerComponent)) & 3);
            double c2 = Dequantize(packed & maxQ, maxQ);
            double c1 = Dequantize((packed >> bitsPerComponent) & maxQ, maxQ);
            double c0 = Dequantize((packed >> (2 * bitsPerComponent)) & maxQ, maxQ);
            double rest = 1.0 - (c0 * c0 + c1 * c1 + c2 * c2);
            double l = rest > 0.0 ? Math.Sqrt(rest) : 0.0;

            float x, y, z, w;
            switch (largest)
            {
                case 0: x = (float)l; y = (float)c0; z = (float)c1; w = (float)c2; break;
                case 1: x = (float)c0; y = (float)l; z = (float)c1; w = (float)c2; break;
                case 2: x = (float)c0; y = (float)c1; z = (float)l; w = (float)c2; break;
                default: x = (float)c0; y = (float)c1; z = (float)c2; w = (float)l; break;
            }
            return new NetQuaternion(x, y, z, w).Normalized();
        }

        private static double Dequantize(ulong value, ulong maxQ) => value / (double)maxQ * (2.0 * Range) - Range;

        private static float Component(NetQuaternion q, int index)
        {
            switch (index)
            {
                case 0: return q.x;
                case 1: return q.y;
                case 2: return q.z;
                default: return q.w;
            }
        }

        private static void CheckBits(int bitsPerComponent)
        {
            if (bitsPerComponent < MinBitsPerComponent || bitsPerComponent > MaxBitsPerComponent)
                throw new ArgumentOutOfRangeException(nameof(bitsPerComponent), bitsPerComponent,
                    $"Bits per component must be {MinBitsPerComponent}..{MaxBitsPerComponent}.");
        }
    }
}
