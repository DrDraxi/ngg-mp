using System;

namespace NGGMultiplayer.Game.Lifecycle
{
    internal enum RunGuardAction
    {
        /// <summary>The run's challenge set loads as it is.</summary>
        Keep,

        /// <summary>Replace the run's set and index with <see cref="RunGuardDecision.SetName"/> / <see cref="RunGuardDecision.Index"/>.</summary>
        Replace,

        /// <summary>No safe set: SetUpScene must not run on this run (it would throw or take the broken-save path).</summary>
        Unfixable
    }

    internal readonly struct RunGuardDecision
    {
        public RunGuardDecision(RunGuardAction action, string setName, int index, string reason)
        {
            Action = action;
            SetName = setName;
            Index = index;
            Reason = reason;
        }

        public RunGuardAction Action { get; }

        public string SetName { get; }

        public int Index { get; }

        /// <summary>What was wrong with the run's set (null for <see cref="RunGuardAction.Keep"/>).</summary>
        public string Reason { get; }

        public override string ToString() => Action == RunGuardAction.Replace ? $"Replace '{SetName}' #{Index} ({Reason})" : Action + (Reason != null ? " (" + Reason + ")" : "");
    }

    /// <summary>
    /// Checks the challenge set of a run injected on a client before SaveManager.SetUpScene applies it
    /// (docs/confirm answers #74, #31, #144). In the real game SetUpScene:
    /// <list type="bullet">
    /// <item>treats a Story run with an empty <c>m_challengeSetLoaded</c> and (<c>finishedDemo</c> or any completed
    /// bounty) as broken: it writes run2.txt from the backup (or deletes run2.txt and run2_temp.txt) and reloads
    /// "Main". A client must never get there.</item>
    /// <item>instantiates <c>GreatConnector.GetChallengeSet(name)</c>; an unknown name returns null and
    /// Instantiate(null) throws, which skips the rest of SetUpScene (bank, position, unlocks, pads, LMUGC resume).</item>
    /// </list>
    /// So the injected set must be empty-but-not-broken or a name the game knows. Pure logic.
    /// </summary>
    internal static class RunGuard
    {
        /// <summary>
        /// Whether the game's own run load (LoadStoryRun / LoadNormalGolfRun) ends with SetUpScene: only in "Main". The
        /// client's injection skips those loads, so it must then run SetUpScene itself, or the injected run's unlocked
        /// panels and clubs, bank, bounty tiers and RFID doors never reach the world (the facts already match the run,
        /// so nothing replays them).
        /// </summary>
        public static bool LoadSetsUpScene(string activeSceneName) =>
            string.Equals(activeSceneName, GameScenes.MainName, StringComparison.Ordinal);

        /// <summary>SetUpScene's broken-save rule (Story only).</summary>
        public static bool LooksBroken(bool isStory, string setName, bool finishedDemo, int completedBounties) =>
            isStory && string.IsNullOrEmpty(setName) && (finishedDemo || completedBounties > 0);

        /// <param name="isStory">The run is loaded in Story mode.</param>
        /// <param name="setName">The run's <c>m_challengeSetLoaded</c> as injected (the shared track, or the host's).</param>
        /// <param name="index">The run's <c>m_currentChallenge</c>.</param>
        /// <param name="hostSetName">The host's own <c>m_challengeSetLoaded</c> from the bundle (before the track override).</param>
        /// <param name="hostIndex">The host's own <c>m_currentChallenge</c>.</param>
        /// <param name="isKnown">Whether the game resolves a non-empty set name; null when it cannot be checked (every name counts as known).</param>
        public static RunGuardDecision Decide(bool isStory, string setName, int index, bool finishedDemo, int completedBounties,
            string hostSetName, int hostIndex, Func<string, bool> isKnown)
        {
            bool blankIsBroken = LooksBroken(isStory, null, finishedDemo, completedBounties);
            string reason;
            if (string.IsNullOrEmpty(setName))
            {
                if (!blankIsBroken)
                    return new RunGuardDecision(RunGuardAction.Keep, setName, index, null);
                reason = "empty set name on a run the game treats as broken";
            }
            else
            {
                if (Known(setName, isKnown))
                    return new RunGuardDecision(RunGuardAction.Keep, setName, index, null);
                reason = "unknown challenge set '" + setName + "'";
            }

            // The host's own set: its game loaded it, so it is the next best thing to the shared track.
            if (!string.IsNullOrEmpty(hostSetName) && !string.Equals(hostSetName, setName, StringComparison.Ordinal)
                && Known(hostSetName, isKnown))
                return new RunGuardDecision(RunGuardAction.Replace, hostSetName, Math.Max(0, hostIndex), reason);
            if (!blankIsBroken)
                return new RunGuardDecision(RunGuardAction.Replace, string.Empty, 0, reason);
            return new RunGuardDecision(RunGuardAction.Unfixable, null, 0, reason);
        }

        private static bool Known(string name, Func<string, bool> isKnown)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            if (isKnown == null)
                return true;
            try
            {
                return isKnown(name);
            }
            catch (Exception)
            {
                // The lookup itself is broken: no reason to throw away a name the game may well know.
                return true;
            }
        }
    }
}
