using NGGMultiplayer.Core.Protocol;
using UnityEngine;

namespace NGGMultiplayer.Game
{
    /// <summary>Unity value types on the wire (little-endian floats, as NetWriter).</summary>
    public static class GameWire
    {
        public static void WriteVector2(this NetWriter writer, Vector2 value)
        {
            writer.WriteFloat(value.x);
            writer.WriteFloat(value.y);
        }

        public static Vector2 ReadVector2(this NetReader reader) => new Vector2(reader.ReadFloat(), reader.ReadFloat());

        public static void WriteVector3(this NetWriter writer, Vector3 value) =>
            writer.WriteVector3(new NetVector3(value.x, value.y, value.z));

        public static Vector3 ReadUnityVector3(this NetReader reader)
        {
            var v = reader.ReadVector3();
            return new Vector3(v.x, v.y, v.z);
        }

        public static void WriteQuaternion(this NetWriter writer, Quaternion value) =>
            writer.WriteQuaternion(new NetQuaternion(value.x, value.y, value.z, value.w));

        /// <summary>Normalized; a degenerate quaternion reads as identity.</summary>
        public static Quaternion ReadUnityQuaternion(this NetReader reader)
        {
            var q = reader.ReadQuaternion().Normalized();
            return new Quaternion(q.x, q.y, q.z, q.w);
        }

        /// <summary>False for NaN or infinity in any component.</summary>
        public static bool IsFinite(Vector3 v) =>
            !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z)
            && !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);

        public static bool IsFinite(Vector2 v) =>
            !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsInfinity(v.x) && !float.IsInfinity(v.y);
    }
}
