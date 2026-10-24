using UnityEngine;

namespace NGGMultiplayer.Game
{
    /// <summary>A pose read from (or shown in) the game world, in Unity world space.</summary>
    public struct GamePose
    {
        public Vector3 Position;
        public Quaternion Rotation;

        /// <summary>Metres per second. Only meaningful when <see cref="HasVelocity"/> is set; otherwise it is estimated.</summary>
        public Vector3 Velocity;

        /// <summary>Radians per second, world space.</summary>
        public Vector3 AngularVelocity;

        public bool HasVelocity;

        /// <summary>Game-specific pose bits (<see cref="PoseFlags"/> other than <see cref="PoseFlags.Visible"/>).</summary>
        public byte Flags;

        /// <summary>The object jumped (respawn, hole change); remote peers snap instead of interpolating.</summary>
        public bool Teleport;

        /// <summary>Hide the remote representation (for example a ball that is not in play).</summary>
        public bool Hidden;

        public GamePose(Vector3 position, Quaternion rotation)
        {
            Position = position;
            Rotation = rotation;
            Velocity = Vector3.zero;
            AngularVelocity = Vector3.zero;
            HasVelocity = false;
            Flags = 0;
            Teleport = false;
            Hidden = false;
        }
    }
}
