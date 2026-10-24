using System;
using System.Globalization;

namespace NGGMultiplayer.Core.Protocol
{
    /// <summary>Unity-free mirror of UnityEngine.Quaternion.</summary>
    public struct NetQuaternion : IEquatable<NetQuaternion>
    {
        public static readonly NetQuaternion Identity = new NetQuaternion(0f, 0f, 0f, 1f);

        public float x;
        public float y;
        public float z;
        public float w;

        public NetQuaternion(float x, float y, float z, float w)
        {
            this.x = x;
            this.y = y;
            this.z = z;
            this.w = w;
        }

        public bool IsFinite => FloatUtil.IsFinite(x) && FloatUtil.IsFinite(y) && FloatUtil.IsFinite(z) && FloatUtil.IsFinite(w);

        public static float Dot(NetQuaternion a, NetQuaternion b) => a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;

        /// <summary>Returns a unit-length copy, or <see cref="Identity"/> if the input is degenerate or non-finite.</summary>
        public NetQuaternion Normalized()
        {
            float sq = x * x + y * y + z * z + w * w;
            if (!FloatUtil.IsFinite(sq) || sq < 1e-12f)
                return Identity;
            float inv = 1f / (float)Math.Sqrt(sq);
            return new NetQuaternion(x * inv, y * inv, z * inv, w * inv);
        }

        public bool Equals(NetQuaternion other) =>
            x.Equals(other.x) && y.Equals(other.y) && z.Equals(other.z) && w.Equals(other.w);

        public override bool Equals(object obj) => obj is NetQuaternion other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = x.GetHashCode();
                h = (h * 397) ^ y.GetHashCode();
                h = (h * 397) ^ z.GetHashCode();
                return (h * 397) ^ w.GetHashCode();
            }
        }

        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "({0:F4}, {1:F4}, {2:F4}, {3:F4})", x, y, z, w);
    }
}
