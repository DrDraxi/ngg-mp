using System;

namespace NGGMultiplayer.Game.Presence
{
    /// <summary>
    /// The swing a remote golfer's cutout plays when their shot is heard (ShotEvents): the top of the backswing, the
    /// impact (the "golf" address drawing, club at the ball), then the finish held while the ball flies. The shot
    /// message is sent at impact, so the backswing is kept short to stay close to the ghost ball's launch.
    /// Plain floats so it can be unit tested.
    /// </summary>
    internal static class SwingTimeline
    {
        /// <summary>Cutout pose with the swing drawings: frame 0 top of the backswing, frame 1 the finish.</summary>
        public const string Pose = "swing";

        public const string ImpactPose = "golf";
        public const int TopFrame = 0;
        public const int FinishFrame = 1;

        public const float TopSeconds = 0.22f;
        public const float ImpactSeconds = 0.08f;
        public const float FinishSeconds = 1.6f;
        public const float Duration = TopSeconds + ImpactSeconds + FinishSeconds;

        /// <summary>
        /// What to show <paramref name="elapsed"/> seconds after the shot was heard. False before it starts, once it
        /// is over, and for a non-finite time (no swing playing).
        /// </summary>
        public static bool Sample(float elapsed, out string pose, out int frame)
        {
            pose = null;
            frame = 0;
            if (float.IsNaN(elapsed) || float.IsInfinity(elapsed) || elapsed < 0f || elapsed >= Duration)
                return false;
            if (elapsed < TopSeconds)
            {
                pose = Pose;
                frame = TopFrame;
            }
            else if (elapsed < TopSeconds + ImpactSeconds)
            {
                pose = ImpactPose;
            }
            else
            {
                pose = Pose;
                frame = FinishFrame;
            }
            return true;
        }

        /// <summary>Putts get no full swing (there are no putting drawings): the golfer keeps the address stance.</summary>
        public static bool IsPutt(string club) =>
            club != null && club.IndexOf("putt", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
