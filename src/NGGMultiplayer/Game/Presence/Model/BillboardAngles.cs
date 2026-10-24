using System;

namespace NGGMultiplayer.Game.Presence
{
    /// <summary>
    /// Which of eight cutouts a viewer sees of a remote player, and the sprite file names that hold them. Directions
    /// are where the viewer stands around the player, measured from the player's facing, clockwise seen from above
    /// (Unity yaw): 0 front (the viewer sees the face), 2 right (the player's right side, so the cutout faces
    /// screen-right), 4 back, 6 left. Plain floats so it can be unit tested.
    /// </summary>
    internal static class BillboardAngles
    {
        public const int Directions = 8;
        public const float Step = 360f / Directions;

        /// <summary>Degrees past a sector's edge the viewer must move before the cutout changes (no flicker on the edge).</summary>
        public const float Hysteresis = 6f;

        private static readonly string[] Names =
            { "front", "front_right", "right", "back_right", "back", "back_left", "left", "front_left" };

        public static string Name(int direction) => Names[Wrap(direction)];

        /// <summary>
        /// The viewer's yaw around the player in [0, 360), relative to the player's facing. NaN when the viewer is
        /// straight above or below the player (no horizontal offset) or any input is not finite.
        /// </summary>
        public static float RelativeYaw(float playerX, float playerZ, float facingYaw, float viewerX, float viewerZ)
        {
            float dx = viewerX - playerX;
            float dz = viewerZ - playerZ;
            if (!IsFinite(dx) || !IsFinite(dz) || !IsFinite(facingYaw) || dx * dx + dz * dz < 1e-8f)
                return float.NaN;
            float viewer = (float)(Math.Atan2(dx, dz) * 180.0 / Math.PI);
            return GolfStance.NormalizeYaw(viewer - facingYaw);
        }

        /// <summary>The nearest direction to <paramref name="relativeYaw"/>; 0 for NaN.</summary>
        public static int Direction(float relativeYaw)
        {
            if (!IsFinite(relativeYaw))
                return 0;
            return Wrap((int)Math.Round(GolfStance.NormalizeYaw(relativeYaw) / Step, MidpointRounding.AwayFromZero));
        }

        /// <summary>
        /// Like <see cref="Direction(float)"/>, but keeps <paramref name="current"/> until the viewer is
        /// <see cref="Hysteresis"/> degrees past its sector. A negative current (none yet) or NaN yaw picks fresh.
        /// </summary>
        public static int Direction(float relativeYaw, int current)
        {
            if (!IsFinite(relativeYaw))
                return current >= 0 ? Wrap(current) : 0;
            if (current >= 0 && Distance(relativeYaw, Wrap(current) * Step) <= Step * 0.5f + Hysteresis)
                return Wrap(current);
            return Direction(relativeYaw);
        }

        /// <summary>The direction a horizontal mirror of <paramref name="direction"/> shows (right and left swap).</summary>
        public static int Mirror(int direction) => Wrap(Directions - Wrap(direction));

        /// <summary>
        /// Which drawn direction to show for <paramref name="direction"/>: itself, else its mirror image flipped, else
        /// the nearest drawn one (itself before its mirror at the same distance). False when none is drawn.
        /// </summary>
        public static bool Resolve(int direction, Func<int, bool> drawn, out int source, out bool flip)
        {
            direction = Wrap(direction);
            for (int offset = 0; offset <= Directions / 2; offset++)
            {
                foreach (int candidate in offset == 0 ? new[] { direction } : new[] { direction + offset, direction - offset })
                {
                    int d = Wrap(candidate);
                    if (drawn(d))
                    {
                        source = d;
                        flip = false;
                        return true;
                    }
                    int mirrored = Mirror(d);
                    if (drawn(mirrored))
                    {
                        source = mirrored;
                        flip = true;
                        return true;
                    }
                }
            }
            source = 0;
            flip = false;
            return false;
        }

        /// <summary>
        /// Parses a sprite file name (no extension) "&lt;pose&gt;_&lt;direction&gt;[_&lt;frame&gt;]", e.g. "stand_front",
        /// "golf_back_left", "walk_right_3". Case-insensitive; frame defaults to 0.
        /// </summary>
        public static bool TryParseName(string name, out string pose, out int direction, out int frame)
        {
            pose = null;
            direction = -1;
            frame = 0;
            if (string.IsNullOrEmpty(name))
                return false;
            var parts = name.Trim().ToLowerInvariant().Split('_');
            if (parts.Length < 2 || parts[0].Length == 0)
                return false;
            int next;
            if (parts.Length >= 3 && TryDirection(parts[1] + "_" + parts[2], out direction))
                next = 3;
            else if (TryDirection(parts[1], out direction))
                next = 2;
            else
                return false;
            if (next < parts.Length)
            {
                if (next != parts.Length - 1 || !int.TryParse(parts[next], out frame) || frame < 0)
                    return false;
            }
            pose = parts[0];
            return true;
        }

        private static bool TryDirection(string text, out int direction)
        {
            direction = Array.IndexOf(Names, text);
            return direction >= 0;
        }

        private static float Distance(float a, float b)
        {
            float d = Math.Abs(GolfStance.NormalizeYaw(a) - GolfStance.NormalizeYaw(b));
            return d > 180f ? 360f - d : d;
        }

        private static int Wrap(int direction) => ((direction % Directions) + Directions) % Directions;

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
