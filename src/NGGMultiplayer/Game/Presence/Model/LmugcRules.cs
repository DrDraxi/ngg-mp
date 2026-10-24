using System;
using System.Collections.Generic;
using System.Globalization;

namespace NGGMultiplayer.Game.Presence
{
    /// <summary>Where the hole number of an <c>LMUGC.CompleteHole</c> came from.</summary>
    internal enum HoleSource
    {
        /// <summary>No hole could be told; the caller uses the next unplayed hole.</summary>
        None,

        /// <summary>The one <c>m_run.lmugcScore</c> entry the call changed (the game's own index).</summary>
        Save,

        /// <summary>The first number in the hole id, read as a hole 1..9.</summary>
        Id
    }

    /// <summary>
    /// The game's LMUGC / JustGolf round rules (answers #45, #52, #65, #139), free of Unity and game types (unit
    /// tested): per-hole scores live in <c>m_run.lmugcScore[9]</c>, a score below 1 means unplayed; the round score is
    /// the sum of the nine entries with an unplayed hole counted as par + 2; <c>CompleteHole</c> takes the hole from the
    /// FIRST number in its id.
    /// </summary>
    internal static class LmugcRules
    {
        /// <summary>Strokes over par an unplayed hole counts for in the round score.</summary>
        public const int UnplayedOverPar = 2;

        /// <summary>A per-hole save score that counts as played.</summary>
        public static bool IsScored(int score) => score >= 1;

        /// <summary>The first run of digits in <paramref name="id"/> ("Hole7" -> 7, "H3_12" -> 3); -1 if none.</summary>
        public static int FirstNumber(string id)
        {
            if (string.IsNullOrEmpty(id))
                return -1;
            int start = -1;
            for (int i = 0; i < id.Length; i++)
            {
                if (id[i] >= '0' && id[i] <= '9')
                {
                    start = i;
                    break;
                }
            }
            if (start < 0)
                return -1;
            int end = start;
            while (end + 1 < id.Length && id[end + 1] >= '0' && id[end + 1] <= '9')
                end++;
            int length = end - start + 1;
            if (length > 6)
                return -1;
            return int.Parse(id.Substring(start, length), NumberStyles.None, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The hole (1..<paramref name="holes"/>) whose save entry is the only one that differs between
        /// <paramref name="before"/> and <paramref name="after"/>, or -1 (no snapshot, no change, or several changes).
        /// </summary>
        public static int ChangedHole(int[] before, int[] after, int holes)
        {
            if (before == null || after == null)
                return -1;
            int n = Math.Min(holes, Math.Min(before.Length, after.Length));
            int found = -1;
            for (int i = 0; i < n; i++)
            {
                if (before[i] == after[i])
                    continue;
                if (found >= 0)
                    return -1;
                found = i;
            }
            return found < 0 ? -1 : found + 1;
        }

        /// <summary>
        /// The hole a <c>CompleteHole(id, score)</c> scored: the save entry it changed if exactly one did, else the
        /// first number of the id when it is 1..<paramref name="holes"/>, else -1.
        /// </summary>
        public static int ResolveHole(int[] before, int[] after, string id, int holes, out HoleSource source) =>
            ResolveHole(before, after, id, holes, 0, out source);

        /// <summary>
        /// As <see cref="ResolveHole(int[], int[], string, int, out HoleSource)"/>, the id fallback reading the number
        /// plus <paramref name="idOffset"/> (an <see cref="IdOffset"/> learned earlier: 1 when ids count from 0).
        /// </summary>
        public static int ResolveHole(int[] before, int[] after, string id, int holes, int idOffset, out HoleSource source)
        {
            int changed = ChangedHole(before, after, holes);
            if (changed > 0)
            {
                source = HoleSource.Save;
                return changed;
            }
            int number = FirstNumber(id);
            if (number >= 0)
                number += idOffset;
            if (number >= 1 && number <= holes)
            {
                source = HoleSource.Id;
                return number;
            }
            source = HoleSource.None;
            return -1;
        }

        /// <summary>
        /// How the id's first number relates to the game's 1-based hole: 0 when equal (1-based ids), 1 when it is one
        /// less (0-based ids), null when neither or unknown. Diagnostic only.
        /// </summary>
        public static int? IdOffset(string id, int hole)
        {
            int number = FirstNumber(id);
            if (number < 0 || hole < 1)
                return null;
            if (number == hole)
                return 0;
            if (number == hole - 1)
                return 1;
            return null;
        }

        /// <summary>
        /// The game's round score: every scored hole's strokes plus par + 2 for each unplayed hole.
        /// <paramref name="complete"/> is false when an unplayed hole's par is unknown (it then adds nothing).
        /// </summary>
        public static int RoundTotal(IReadOnlyList<int> scores, IReadOnlyList<int> pars, int holes, out bool complete)
        {
            complete = true;
            int total = 0;
            for (int h = 0; h < holes; h++)
            {
                int score = scores != null && h < scores.Count ? scores[h] : 0;
                if (IsScored(score))
                {
                    total += score;
                    continue;
                }
                int par = pars != null && h < pars.Count ? pars[h] : 0;
                if (par > 0)
                    total += par + UnplayedOverPar;
                else
                    complete = false;
            }
            return total;
        }
    }
}
