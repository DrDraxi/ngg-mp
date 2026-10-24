using System;
using NGGMultiplayer.Core.Protocol;

namespace NGGMultiplayer.Core.Sync
{
    /// <summary>Quaternion and pose helpers used for interpolation and extrapolation.</summary>
    public static class TransformMath
    {
        /// <summary>Hamilton product; like Unity, <c>Multiply(a, b)</c> applies b first, then a.</summary>
        public static NetQuaternion Multiply(NetQuaternion a, NetQuaternion b) =>
            new NetQuaternion(
                a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
                a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
                a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w,
                a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z);

        /// <summary>Rotation of <paramref name="radians"/> about <paramref name="axis"/> (normalized here).</summary>
        public static NetQuaternion AxisAngle(NetVector3 axis, double radians)
        {
            double len = Math.Sqrt(axis.SqrMagnitude);
            if (len < 1e-12 || double.IsNaN(len) || double.IsInfinity(len))
                return NetQuaternion.Identity;
            double s = Math.Sin(radians * 0.5) / len;
            return new NetQuaternion((float)(axis.x * s), (float)(axis.y * s), (float)(axis.z * s), (float)Math.Cos(radians * 0.5));
        }

        /// <summary>Shortest-path spherical interpolation; t is clamped to [0, 1]. Falls back to nlerp for tiny angles.</summary>
        public static NetQuaternion Slerp(NetQuaternion a, NetQuaternion b, float t)
        {
            if (t <= 0f)
                return a;
            if (t >= 1f)
                return b;
            double ax = a.x, ay = a.y, az = a.z, aw = a.w;
            double bx = b.x, by = b.y, bz = b.z, bw = b.w;
            double cos = ax * bx + ay * by + az * bz + aw * bw;
            if (cos < 0)
            {
                cos = -cos;
                bx = -bx;
                by = -by;
                bz = -bz;
                bw = -bw;
            }

            double wa, wb;
            if (cos > 0.9995)
            {
                wa = 1.0 - t;
                wb = t;
            }
            else
            {
                double theta = Math.Acos(cos);
                double sin = Math.Sin(theta);
                wa = Math.Sin((1.0 - t) * theta) / sin;
                wb = Math.Sin(t * theta) / sin;
            }
            return new NetQuaternion(
                (float)(ax * wa + bx * wb),
                (float)(ay * wa + by * wb),
                (float)(az * wa + bz * wb),
                (float)(aw * wa + bw * wb)).Normalized();
        }

        /// <summary>Rotates <paramref name="rotation"/> by a world-space angular velocity for <paramref name="seconds"/>.</summary>
        public static NetQuaternion Integrate(NetQuaternion rotation, NetVector3 angularVelocity, double seconds)
        {
            double speed = Math.Sqrt(angularVelocity.SqrMagnitude);
            double angle = speed * seconds;
            if (angle < 1e-9 || double.IsNaN(angle) || double.IsInfinity(angle))
                return rotation;
            return Multiply(AxisAngle(angularVelocity, angle), rotation).Normalized();
        }

        /// <summary>Angle in degrees between two rotations (0..180).</summary>
        public static double AngleDegrees(NetQuaternion a, NetQuaternion b)
        {
            a = a.Normalized();
            b = b.Normalized();
            double sign = NetQuaternion.Dot(a, b) < 0f ? -1.0 : 1.0;
            double dx = a.x - sign * b.x, dy = a.y - sign * b.y, dz = a.z - sign * b.z, dw = a.w - sign * b.w;
            double sx = a.x + sign * b.x, sy = a.y + sign * b.y, sz = a.z + sign * b.z, sw = a.w + sign * b.w;
            // 2*atan2(|a-b|, |a+b|) stays accurate for tiny angles, where acos(dot) loses all precision.
            double diff = Math.Sqrt(dx * dx + dy * dy + dz * dz + dw * dw);
            double sum = Math.Sqrt(sx * sx + sy * sy + sz * sz + sw * sw);
            return 4.0 * Math.Atan2(diff, sum) * (180.0 / Math.PI);
        }

        /// <summary>
        /// Blends two states: position, velocities and time are lerped, rotation is slerped. Discrete fields
        /// (Teleport, Flags, Extra) come from <paramref name="a"/>, the state already reached.
        /// </summary>
        public static TransformState Interpolate(in TransformState a, in TransformState b, float t)
        {
            var result = a;
            result.Position = NetVector3.Lerp(a.Position, b.Position, t);
            result.Rotation = Slerp(a.Rotation, b.Rotation, t);
            result.Velocity = NetVector3.Lerp(a.Velocity, b.Velocity, t);
            result.AngularVelocity = NetVector3.Lerp(a.AngularVelocity, b.AngularVelocity, t);
            result.Time = a.Time + (b.Time - a.Time) * t;
            return result;
        }

        /// <summary>Projects a state forward by <paramref name="seconds"/> using its linear and angular velocity.</summary>
        public static TransformState Extrapolate(in TransformState state, double seconds)
        {
            var result = state;
            if (seconds <= 0)
                return result;
            result.Position = state.Position + state.Velocity * (float)seconds;
            result.Rotation = Integrate(state.Rotation, state.AngularVelocity, seconds);
            result.Time = state.Time + seconds;
            return result;
        }
    }
}
