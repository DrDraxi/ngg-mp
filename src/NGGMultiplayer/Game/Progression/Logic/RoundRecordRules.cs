namespace NGGMultiplayer.Game.Progression
{
    /// <summary>
    /// Host checks for a client's round records (a JustGolf round for the scorecard, an LMUGC best score), which the
    /// client's game computes but may not save. Pure.
    /// <para>From the game (docs/confirm answers 139, 140): a round total is the sum of the 9 lmugcScore entries, an
    /// unplayed hole counting par + 2, so it is at least 9 strokes. LMUGC.CompleteHole (Story only, once hole 9 is
    /// scored, before FinishChallenge) keeps a total as the best when <c>total &lt; best || best &lt; 9</c> (a best
    /// below 9 is "none yet"); the no-assist best the same way when no assist was used.</para>
    /// </summary>
    public static class RoundRecordRules
    {
        public const int Holes = 9;

        /// <summary>Nine holes in one stroke each.</summary>
        public const int MinRoundScore = Holes;

        /// <summary>Bound only against garbage.</summary>
        public const int MaxRoundScore = 999;

        public const double RoundScoreIntervalSeconds = 30.0;

        public static bool IsPlausibleRoundScore(int score) => score >= MinRoundScore && score <= MaxRoundScore;

        /// <summary>A best of <paramref name="best"/> is a real one (the game treats anything below 9 as none yet).</summary>
        public static bool HasBest(int best) => best >= MinRoundScore;

        /// <summary>The game's own rule: <paramref name="total"/> replaces <paramref name="best"/>.</summary>
        public static bool GameKeepsAsBest(int best, int total) => total < best || !HasBest(best);

        /// <summary>
        /// The client's game changed an LMUGC best from <paramref name="before"/> to <paramref name="after"/>. The
        /// host takes it only while its own value is still the one the client compared against (so a better score
        /// the host made meanwhile is never replaced), and only when the game's own rule would have kept it.
        /// </summary>
        public static bool AcceptBest(int hostValue, int before, int after) =>
            before != after && hostValue == before && IsPlausibleRoundScore(after) && GameKeepsAsBest(before, after);
    }
}
