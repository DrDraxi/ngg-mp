using NGGMultiplayer.Core.Diagnostics;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Sync;
using NGGMultiplayer.Game;
using UnityEngine;

namespace NGGMultiplayer.Runtime
{
    public enum PoseSource
    {
        Player,
        Ball
    }

    /// <summary>
    /// A locally owned entity whose state is read from the game through <see cref="IGameAdapter"/>. Velocity is
    /// estimated from successive captures when the adapter does not supply it, and large jumps are flagged as
    /// teleports so remote peers snap instead of sliding across the map.
    /// </summary>
    public sealed class LocalPoseEntity : INetEntity
    {
        private const float TeleportDistance = 20f;
        private const double MaxVelocityEstimateGap = 0.5;

        private readonly IGameAdapter _adapter;
        private readonly PoseSource _source;
        private Vector3 _lastPosition;
        private double _lastTime;
        private bool _hasLast;
        private bool _wasHidden = true;

        public LocalPoseEntity(IGameAdapter adapter, PoseSource source)
        {
            _adapter = adapter;
            _source = source;
        }

        public PoseSource Source => _source;

        public bool CaptureState(out TransformState state)
        {
            GamePose pose;
            bool has = _source == PoseSource.Player
                ? _adapter.TryGetLocalPlayerPose(out pose)
                : _adapter.TryGetLocalBallPose(out pose);
            if (!has)
                pose = new GamePose(Vector3.zero, Quaternion.identity) { Hidden = true };

            if (!IsFinite(pose.Position) || !IsFinite(pose.Rotation))
            {
                state = default;
                return false;
            }

            double now = Time.realtimeSinceStartupAsDouble;
            bool jumped = _hasLast && (pose.Position - _lastPosition).sqrMagnitude > TeleportDistance * TeleportDistance;
            // Appearing after being hidden is a jump too: there is nothing sensible to interpolate from.
            if (jumped || (_wasHidden && !pose.Hidden))
                pose.Teleport = true;

            if (!pose.HasVelocity)
            {
                double dt = now - _lastTime;
                pose.Velocity = _hasLast && !pose.Teleport && dt > 1e-4 && dt < MaxVelocityEstimateGap
                    ? (pose.Position - _lastPosition) / (float)dt
                    : Vector3.zero;
            }

            _lastPosition = pose.Position;
            _lastTime = now;
            _hasLast = !pose.Hidden;
            _wasHidden = pose.Hidden;
            state = PoseConversion.ToState(pose);
            return true;
        }

        public void ApplyState(in TransformState state, SnapshotSampleKind kind)
        {
            // Owned locally: the game simulates it.
        }

        public void OnOwnershipChanged(PeerId owner, bool isLocal)
        {
            if (!isLocal)
                NetLog.Warn($"[Entities] Local {_source} entity was handed to {owner}; it is player-bound and should stay ours.");
        }

        public void OnDespawned(DespawnReason reason)
        {
        }

        private static bool IsFinite(Vector3 v) => !float.IsNaN(v.x + v.y + v.z) && !float.IsInfinity(v.x + v.y + v.z);

        private static bool IsFinite(Quaternion q) =>
            !float.IsNaN(q.x + q.y + q.z + q.w) && !float.IsInfinity(q.x + q.y + q.z + q.w);
    }
}
