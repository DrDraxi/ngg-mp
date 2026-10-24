using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Sync;
using NGGMultiplayer.Game;
using UnityEngine;

namespace NGGMultiplayer.Runtime
{
    internal static class PoseConversion
    {
        public static NetVector3 ToNet(Vector3 v) => new NetVector3(v.x, v.y, v.z);

        public static NetQuaternion ToNet(Quaternion q) => new NetQuaternion(q.x, q.y, q.z, q.w);

        public static Vector3 ToUnity(NetVector3 v) => new Vector3(v.x, v.y, v.z);

        public static Quaternion ToUnity(NetQuaternion q)
        {
            var n = q.Normalized();
            return new Quaternion(n.x, n.y, n.z, n.w);
        }

        public static TransformState ToState(in GamePose pose)
        {
            var state = new TransformState(ToNet(pose.Position), ToNet(pose.Rotation))
            {
                Velocity = ToNet(pose.Velocity),
                AngularVelocity = ToNet(pose.AngularVelocity),
                Teleport = pose.Teleport,
                Flags = (byte)((pose.Flags & ~PoseFlags.Visible) | (pose.Hidden ? 0 : PoseFlags.Visible))
            };
            return state;
        }

        public static bool IsVisible(in TransformState state) => (state.Flags & PoseFlags.Visible) != 0;
    }
}
