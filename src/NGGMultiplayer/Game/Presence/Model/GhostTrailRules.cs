using System;

namespace NGGMultiplayer.Game.Presence
{
    /// <summary>
    /// The colour of each player (avatar tint, name tag, ghost ball trail): 8 colours by session player index, wrapping.
    /// Plain floats so it can be unit tested; <see cref="PlayerColors"/> turns them into Unity colours.
    /// </summary>
    internal static class PlayerPalette
    {
        public const int Count = 8;

        private static readonly float[] Rgb =
        {
            0.95f, 0.30f, 0.25f,
            0.25f, 0.55f, 0.95f,
            0.30f, 0.85f, 0.40f,
            0.95f, 0.80f, 0.20f,
            0.75f, 0.40f, 0.90f,
            0.20f, 0.85f, 0.85f,
            0.95f, 0.55f, 0.20f,
            0.90f, 0.90f, 0.90f,
        };

        /// <summary>Palette slot of <paramref name="playerIndex"/>: 0..7 for any index, negative ones included.</summary>
        public static int Slot(int playerIndex) => ((playerIndex % Count) + Count) % Count;

        public static void Get(int playerIndex, out float r, out float g, out float b)
        {
            int i = Slot(playerIndex) * 3;
            r = Rgb[i];
            g = Rgb[i + 1];
            b = Rgb[i + 2];
        }
    }

    /// <summary>What the ghost ball's trail does after one applied pose.</summary>
    internal struct TrailStep
    {
        /// <summary>Drop every trail point now (the ghost appeared, snapped, or a new shot started).</summary>
        public bool Clear;

        /// <summary>Add points while the ghost moves (the ball is in flight or rolling and not holed).</summary>
        public bool Emit;
    }

    /// <summary>
    /// When the trail behind another player's ghost ball is cleared and when it grows (spec 7.3). Fed with every pose
    /// the remote view applies (already interpolated by Core). The trail is cleared:
    /// <list type="bullet">
    /// <item>on the first pose after <see cref="Reset"/> (the ghost was hidden, or is new): never a streak from where
    /// it was last seen;</item>
    /// <item>when the pose becomes a teleport (Core snapped it: reset ball, new hole, mode change);</item>
    /// <item>when the ball starts moving (a new shot) and when it comes back from being holed;</item>
    /// <item>when it moved farther in one step than a ball can fly (<see cref="IsJump"/>; a lost teleport).</item>
    /// </list>
    /// It emits only while the ball moves; at rest no points are added and the existing ones fade out with the
    /// trail's time. Unity-free so it can be unit tested.
    /// </summary>
    internal sealed class GhostTrailRules
    {
        /// <summary>Jumps shorter than this never clear (a slow frame of a fast ball).</summary>
        public const float JumpMinDistance = 5f;

        /// <summary>Faster than any golf shot (a drive leaves the club at about 75 m/s).</summary>
        public const float MaxBallSpeed = 120f;

        /// <summary>Trail length when neither the config nor the game's own ball trail gives one.</summary>
        public const float DefaultSeconds = 1.5f;

        public const float MinSeconds = 0.1f;
        public const float MaxSeconds = 10f;

        /// <summary>The other player's trail is drawn this many times as wide as the game's own ball trail.</summary>
        public const float WidthScale = 0.625f;

        /// <summary>Never thinner than this (metres).</summary>
        public const float MinWidth = 0.015f;

        /// <summary>How far the player colour is lifted toward white, so a blue trail still reads against the sky.</summary>
        public const float Brighten = 0.35f;

        /// <summary>The ghost trail's width multiplier for the game trail's <paramref name="gameWidth"/>.</summary>
        public static float Width(float gameWidth)
        {
            float width = gameWidth > 0f && !float.IsInfinity(gameWidth) ? gameWidth * WidthScale : MinWidth;
            return width < MinWidth ? MinWidth : width;
        }

        /// <summary>A colour channel lifted toward 1 by <see cref="Brighten"/>.</summary>
        public static float Lift(float channel) => channel + (1f - channel) * Brighten;

        private bool _hasLast;
        private float _x, _y, _z;
        private double _lastTime;
        private bool _wasMoving;
        private bool _wasTeleport;
        private bool _wasHoled;

        /// <summary>Forget the last pose: the next one clears the trail (call when the ghost is hidden or disabled).</summary>
        public void Reset() => _hasLast = false;

        /// <summary>
        /// One applied pose at (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>) and time
        /// <paramref name="now"/> (seconds, unscaled). <paramref name="teleport"/> is the pose's raw teleport flag (it
        /// stays set until the next state is reached; only its rising edge counts).
        /// </summary>
        public TrailStep Step(float x, float y, float z, double now, bool moving, bool holed, bool teleport)
        {
            bool clear;
            if (!_hasLast)
            {
                clear = true;
            }
            else
            {
                float dx = x - _x, dy = y - _y, dz = z - _z;
                float distance = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
                clear = (teleport && !_wasTeleport)
                    || (moving && !_wasMoving)
                    || (_wasHoled && !holed)
                    || IsJump(distance, now - _lastTime);
            }
            _hasLast = true;
            _x = x;
            _y = y;
            _z = z;
            _lastTime = now;
            _wasMoving = moving;
            _wasTeleport = teleport;
            _wasHoled = holed;
            return new TrailStep { Clear = clear, Emit = moving && !holed };
        }

        /// <summary>
        /// A step of <paramref name="distance"/> metres in <paramref name="seconds"/> is a jump, not flight: longer than
        /// <see cref="JumpMinDistance"/> and faster than <see cref="MaxBallSpeed"/>. Non-finite input is a jump.
        /// </summary>
        public static bool IsJump(float distance, double seconds)
        {
            if (float.IsNaN(distance) || float.IsInfinity(distance))
                return true;
            if (distance <= JumpMinDistance)
                return false;
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0)
                return true;
            return distance > MaxBallSpeed * seconds;
        }

        /// <summary>
        /// A ghost counts as moving only with the owner's BallMoving flag (its ball body is in play). Not by speed: a
        /// ball placed for golf mode or reset slides there under interpolation, which must not draw a streak.
        /// </summary>
        public static bool IsMoving(byte flags) => (flags & PoseFlags.BallMoving) != 0;

        /// <summary>
        /// The trail's time in seconds: <paramref name="configured"/> ([Presence] TrailSeconds) when above 0, else the
        /// game's own ball trail time when known and positive, else <see cref="DefaultSeconds"/>; clamped to
        /// <see cref="MinSeconds"/>..<see cref="MaxSeconds"/>.
        /// </summary>
        public static float Seconds(float configured, float gameSeconds)
        {
            float seconds = configured > 0f && !float.IsInfinity(configured) ? configured
                : gameSeconds > 0f && !float.IsInfinity(gameSeconds) ? gameSeconds
                : DefaultSeconds;
            if (seconds < MinSeconds)
                return MinSeconds;
            return seconds > MaxSeconds ? MaxSeconds : seconds;
        }
    }
}
