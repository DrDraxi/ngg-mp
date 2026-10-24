using System;

namespace NGGMultiplayer.Game.Presence
{
    /// <summary>
    /// Where a golfing player stands. <c>MoveAndHitController.m_golferHolder</c> is not at the player: SwitchToGolf
    /// moves the holder so that its ball point (<c>m_ballPosition</c>) lands on the spot the walker stood on (answers
    /// #55). So the ball sits on the walker's spot and the golfer addresses it from the side: a right-handed golfer
    /// <see cref="StanceDistance"/> to the left of the aim line, facing the ball, the target on their left. Plain floats
    /// (Unity yaw: degrees clockwise from +Z, seen from above) so it can be unit tested.
    /// </summary>
    internal static class GolfStance
    {
        /// <summary>Metres from the ball to the golfer's feet.</summary>
        public const float StanceDistance = 0.6f;

        /// <summary>The golfer faces the aim direction turned right by this many degrees (towards the ball).</summary>
        public const float FacingOffset = 90f;

        /// <summary>
        /// The golfer's feet (x, z) and yaw for a ball at (<paramref name="ballX"/>, <paramref name="ballZ"/>) aimed
        /// along <paramref name="aimYaw"/>. Non-finite input gives the ball spot and the aim yaw.
        /// </summary>
        public static void Place(float ballX, float ballZ, float aimYaw, out float x, out float z, out float yaw)
        {
            if (!IsFinite(ballX) || !IsFinite(ballZ) || !IsFinite(aimYaw))
            {
                x = ballX;
                z = ballZ;
                yaw = aimYaw;
                return;
            }
            yaw = NormalizeYaw(aimYaw + FacingOffset);
            double radians = yaw * Math.PI / 180.0;
            // Facing direction (sin, cos) in XZ; the feet are that far back from the ball.
            x = (float)(ballX - Math.Sin(radians) * StanceDistance);
            z = (float)(ballZ - Math.Cos(radians) * StanceDistance);
        }

        /// <summary>Yaw in [0, 360).</summary>
        public static float NormalizeYaw(float yaw)
        {
            float result = yaw % 360f;
            if (result < 0f)
                result += 360f;
            return result >= 360f ? 0f : result;
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
