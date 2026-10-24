using System.Globalization;
using NGGMultiplayer.Core.Protocol;

namespace NGGMultiplayer.Core.Sync
{
    /// <summary>
    /// One timestamped pose of a networked entity. <see cref="Time"/> is in seconds on the session's shared
    /// (host) clock. <see cref="Flags"/> and <see cref="Extra"/> carry game-specific state such as
    /// "ball at rest"; Core never interprets them.
    /// </summary>
    public struct TransformState
    {
        public const int MaxExtraBytes = 255;

        /// <summary>
        /// Largest accepted magnitude of a position component, in meters. Far beyond any playable area, and small
        /// enough that interpolating and extrapolating in float can never overflow.
        /// </summary>
        public const float MaxCoordinate = 1e6f;

        /// <summary>Largest accepted magnitude of a velocity or angular velocity component (m/s or rad/s).</summary>
        public const float MaxSpeed = 1e5f;

        public NetVector3 Position;
        public NetQuaternion Rotation;
        public NetVector3 Velocity;

        /// <summary>World-space angular velocity in radians per second (as Unity's Rigidbody.angularVelocity).</summary>
        public NetVector3 AngularVelocity;

        public double Time;

        /// <summary>The entity jumped to this pose; receivers snap instead of interpolating into it.</summary>
        public bool Teleport;

        public byte Flags;

        /// <summary>Optional game-specific payload, at most <see cref="MaxExtraBytes"/>. Null means none.</summary>
        public byte[] Extra;

        public TransformState(NetVector3 position, NetQuaternion rotation, double time = 0)
        {
            Position = position;
            Rotation = rotation;
            Velocity = NetVector3.Zero;
            AngularVelocity = NetVector3.Zero;
            Time = time;
            Teleport = false;
            Flags = 0;
            Extra = null;
        }

        public static TransformState Identity => new TransformState(NetVector3.Zero, NetQuaternion.Identity);

        /// <summary>
        /// True when every number is finite, positions are within <see cref="MaxCoordinate"/>, velocities within
        /// <see cref="MaxSpeed"/>, and <see cref="Extra"/> is within its limit.
        /// </summary>
        public bool IsValid =>
            IsPositionInRange(Position) && Rotation.IsFinite && IsSpeedInRange(Velocity) && IsSpeedInRange(AngularVelocity)
            && !double.IsNaN(Time) && !double.IsInfinity(Time)
            && (Extra == null || Extra.Length <= MaxExtraBytes);

        /// <summary>Finite, and every component within ±<see cref="MaxCoordinate"/>.</summary>
        public static bool IsPositionInRange(NetVector3 v) => WithinMagnitude(v, MaxCoordinate);

        /// <summary>Finite, and every component within ±<see cref="MaxSpeed"/>.</summary>
        public static bool IsSpeedInRange(NetVector3 v) => WithinMagnitude(v, MaxSpeed);

        // NaN fails every comparison, so this also rejects non-finite components.
        private static bool WithinMagnitude(NetVector3 v, float max) =>
            v.x >= -max && v.x <= max && v.y >= -max && v.y <= max && v.z >= -max && v.z <= max;

        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "t={0:F3} pos={1} rot={2} vel={3}{4}",
                Time, Position, Rotation, Velocity, Teleport ? " teleport" : string.Empty);
    }
}
