using System;
using System.Globalization;

namespace NGGMultiplayer.Core.Protocol
{
    /// <summary>Unity-free mirror of UnityEngine.Vector3.</summary>
    public struct NetVector3 : IEquatable<NetVector3>
    {
        public static readonly NetVector3 Zero = default;

        public float x;
        public float y;
        public float z;

        public NetVector3(float x, float y, float z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public bool IsFinite => FloatUtil.IsFinite(x) && FloatUtil.IsFinite(y) && FloatUtil.IsFinite(z);

        public float SqrMagnitude => x * x + y * y + z * z;

        public static NetVector3 operator +(NetVector3 a, NetVector3 b) => new NetVector3(a.x + b.x, a.y + b.y, a.z + b.z);

        public static NetVector3 operator -(NetVector3 a, NetVector3 b) => new NetVector3(a.x - b.x, a.y - b.y, a.z - b.z);

        public static NetVector3 operator *(NetVector3 a, float s) => new NetVector3(a.x * s, a.y * s, a.z * s);

        public static NetVector3 Lerp(NetVector3 a, NetVector3 b, float t) =>
            new NetVector3(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t);

        public bool Equals(NetVector3 other) => x.Equals(other.x) && y.Equals(other.y) && z.Equals(other.z);

        public override bool Equals(object obj) => obj is NetVector3 other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = x.GetHashCode();
                h = (h * 397) ^ y.GetHashCode();
                return (h * 397) ^ z.GetHashCode();
            }
        }

        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "({0:F3}, {1:F3}, {2:F3})", x, y, z);
    }
}
