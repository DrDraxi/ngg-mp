using System;

namespace NGGMultiplayer.Game.Presence
{
    /// <summary>
    /// How big another player's ball is drawn: as big as the world's own ball (its renderer's size), which the game
    /// draws well above a real golf ball's 4.27 cm.
    /// </summary>
    internal static class GhostBallRules
    {
        /// <summary>A real golf ball; used until the world's ball can be measured.</summary>
        public const float FallbackDiameter = 0.0427f;

        /// <summary>Measurements outside this range (metres) are not a ball (hidden, not set up yet): ignored.</summary>
        public const float MinDiameter = 0.02f;
        public const float MaxDiameter = 1.5f;

        /// <summary>The diameter from an axis-aligned size of the ball; 0 when that is not a usable measurement.</summary>
        public static float Diameter(float sizeX, float sizeY, float sizeZ)
        {
            float size = Math.Max(sizeX, Math.Max(sizeY, sizeZ));
            if (float.IsNaN(size) || float.IsInfinity(size) || size < MinDiameter || size > MaxDiameter)
                return 0f;
            return size;
        }

        /// <summary>
        /// The diameter from the ball mesh's own bounds times its transform's scale. Not the renderer's world bounds: the
        /// ball rolls, and the world box around a rotated sphere mesh grows up to sqrt(3) times its width (measured
        /// 2026-10-22: 0.107 m world bounds, the ghost then drawn about 1.4x the world's ball).
        /// </summary>
        public static float DiameterFromMesh(float meshX, float meshY, float meshZ, float scaleX, float scaleY, float scaleZ) =>
            Diameter(Math.Abs(meshX * scaleX), Math.Abs(meshY * scaleY), Math.Abs(meshZ * scaleZ));
    }
}
