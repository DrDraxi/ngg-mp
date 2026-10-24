using System;
using System.Globalization;

namespace NGGMultiplayer.Game.Presence
{
    /// <summary>
    /// Which of a clip's redrawn key frames to show for the game video's current frame (<see cref="LookVideo"/>):
    /// stop-motion, each key holds until the next one starts. Before the first key the first one shows. Unity-free
    /// (unit tested).
    /// </summary>
    internal static class LookFrames
    {
        /// <summary>
        /// The index in <paramref name="keys"/> (key frame numbers, ascending) of the drawing that shows at
        /// <paramref name="frame"/>: the last key at or before it. -1 when there are no keys.
        /// </summary>
        public static int Hold(int[] keys, long frame)
        {
            if (keys == null || keys.Length == 0)
                return -1;
            int lo = 0, hi = keys.Length - 1;
            if (frame >= keys[hi])
                return hi;
            if (frame <= keys[0])
                return 0;
            // Binary search: keys[lo] <= frame < keys[hi].
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (keys[mid] <= frame)
                    lo = mid;
                else
                    hi = mid;
            }
            return lo;
        }

        /// <summary>A key frame file's number: "026" (the name without extension) is frame 26; -1 for anything else.</summary>
        public static int ParseFrame(string fileNameWithoutExtension)
        {
            if (string.IsNullOrEmpty(fileNameWithoutExtension) || fileNameWithoutExtension.Length > 6)
                return -1;
            return int.TryParse(fileNameWithoutExtension, NumberStyles.None, CultureInfo.InvariantCulture, out int frame) ? frame : -1;
        }
    }
}
