using System;

namespace NGGMultiplayer.Game.Presence
{
    /// <summary>How a client's wind follows the host's (spec 7.5). Plain floats so it can be unit tested.</summary>
    internal static class WindMath
    {
        /// <summary>Units per second the shown wind moves toward the host's.</summary>
        public const float EaseRate = 10f;

        /// <summary>A gap above this fraction of the host's wind speed snaps instead of easing.</summary>
        public const float SnapFraction = 0.5f;

        /// <summary>A desired-wind change above this goes out reliably.</summary>
        public const float ReliableDelta = 0.01f;

        /// <summary>Moves (x, y) toward (targetX, targetY): snaps on a large gap, else at most <see cref="EaseRate"/> * dt.</summary>
        public static void Follow(ref float x, ref float y, float targetX, float targetY, float deltaTime)
        {
            float dx = targetX - x;
            float dy = targetY - y;
            float gap = (float)Math.Sqrt(dx * dx + dy * dy);
            float speed = (float)Math.Sqrt(targetX * targetX + targetY * targetY);
            float step = EaseRate * Math.Max(0f, deltaTime);
            if (gap > SnapFraction * speed || gap <= step || gap < 1e-6f)
            {
                x = targetX;
                y = targetY;
                return;
            }
            x += dx / gap * step;
            y += dy / gap * step;
        }

        public static bool ChangedBeyond(float ax, float ay, float bx, float by, float threshold)
        {
            float dx = ax - bx;
            float dy = ay - by;
            return dx * dx + dy * dy > threshold * threshold;
        }
    }
}
