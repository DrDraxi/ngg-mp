using System;
using System.Collections.Generic;

namespace NGGMultiplayer.Game.Progression
{
    /// <summary>
    /// What this player is playing right now, as the bounty isolation rules see it (clean set names, empty for none).
    /// The game has one ChallengeSystem per process: <see cref="CurrentSet"/> is its m_challengeSet,
    /// <see cref="MainSet"/> its m_mainChallengeSet; LoadNewChallengeSet replaces the main set for every non-bounty
    /// set (answers 39), so a current set that is not the main one is a bounty or the LMUGC round (answers 143).
    /// </summary>
    public readonly struct LocalPlay
    {
        public LocalPlay(string activePadSet, string currentSet, string mainSet, bool inLmugc)
        {
            ActivePadSet = activePadSet ?? string.Empty;
            CurrentSet = currentSet ?? string.Empty;
            MainSet = mainSet ?? string.Empty;
            InLmugc = inLmugc;
        }

        /// <summary>The set of MoveAndHitController.m_activeBountyPad, or empty.</summary>
        public readonly string ActivePadSet;

        public readonly string CurrentSet;
        public readonly string MainSet;
        public readonly bool InLmugc;

        /// <summary>
        /// In a challenge of its own (a bounty pad, a bounty or LMUGC set, an LMUGC round): the main track and the
        /// shared lists wait until it is over.
        /// </summary>
        public bool InOwnChallenge =>
            ActivePadSet.Length > 0 || InLmugc
            || (CurrentSet.Length > 0 && !string.Equals(CurrentSet, MainSet, StringComparison.Ordinal));

        /// <summary>This player is playing <paramref name="set"/> right now (from its pad, a tee or the LMUGC machine).</summary>
        public bool IsPlaying(string set)
        {
            if (string.IsNullOrEmpty(set))
                return false;
            if (string.Equals(set, ActivePadSet, StringComparison.Ordinal))
                return true;
            return CurrentSet.Length > 0 && string.Equals(set, CurrentSet, StringComparison.Ordinal)
                && !string.Equals(CurrentSet, MainSet, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Keeps another player's bounty from touching the bounty this player is in (docs/GAME_INTEGRATION.md 6.4). The
    /// game pays, completes the pads and exits a bounty only while HasCompletedThisBounty is false (answers 40), so a
    /// completion of the set this player is playing waits until the player is out of it (or completed it too: the
    /// game then adds it itself). The host pays a client's completion once; when the host then completes the same
    /// bounty, the game's second payment is taken back. Pure.
    /// </summary>
    public static class BountyIsolation
    {
        /// <summary>A newly completed bounty's list entry waits: this player is playing that set.</summary>
        public static bool DeferEntry(bool added, LocalPlay play, string set) => added && play.IsPlaying(set);

        /// <summary>The pads of a bounty everyone finished wait while this player plays that set.</summary>
        public static bool DeferPads(LocalPlay play, string set) => play.IsPlaying(set);

        /// <summary>
        /// The host pays a client's newly accepted bounty, once per bounty and session (the claimant's own payment
        /// was local and is overwritten by the host's bank fact).
        /// </summary>
        public static bool PayOnHost(bool added, bool onHost, bool claimantValid, bool alreadyPaid) =>
            added && onHost && claimantValid && !alreadyPaid;

        /// <summary>
        /// What to take back after this peer's own CompleteChallenge of a bounty the host already paid for another
        /// player: everything that call added to the purse (0 when it paid nothing).
        /// </summary>
        public static int Refund(bool paidRemotely, int balanceBefore, int balanceAfter) =>
            paidRemotely && balanceAfter > balanceBefore ? balanceAfter - balanceBefore : 0;

        /// <summary>Deferred bounty, track or pre-hit work can be done now: in the world and in no challenge of its own.</summary>
        public static bool CanResolve(bool hasDeferredWork, bool inWorld, bool loading, LocalPlay play) =>
            hasDeferredWork && inWorld && !loading && !play.InOwnChallenge;

        /// <summary>
        /// Another player's pre-hit targets (m_preHitTargets / ChallengeSystem.m_preBrokenShots) are recorded at once
        /// but only reach this player's live challenge list once it is out of its own challenge: a target the other
        /// player broke in the same bounty must not satisfy (or hide) one of this player's.
        /// </summary>
        public static bool DeferPreHit(bool addsEntries, LocalPlay play) => addsEntries && play.InOwnChallenge;

        /// <summary>Entries of <paramref name="wanted"/> missing from both local lists.</summary>
        public static List<string> MissingEntries(IEnumerable<string> wanted, ICollection<string> runList, ICollection<string> liveList)
        {
            var missing = new List<string>();
            if (wanted == null)
                return missing;
            foreach (var entry in wanted)
            {
                if (string.IsNullOrEmpty(entry) || missing.Contains(entry))
                    continue;
                if ((runList != null && runList.Contains(entry)) || (liveList != null && liveList.Contains(entry)))
                    continue;
                missing.Add(entry);
            }
            return missing;
        }
    }

    /// <summary>
    /// Host: client bounties accepted while the host was playing that same bounty, so their entries are not yet in
    /// the host's m_run (see <see cref="BountyIsolation"/>). They belong to the host's own save, so they outlive scene
    /// loads and the session (like the pending main track), are kept in the facts the host rebuilds, go into the run
    /// before the game's own save on quitting to the menu, and are applied once the host is out of that bounty.
    /// Pure; main thread only.
    /// </summary>
    public sealed class HostPendingBounties
    {
        private readonly Dictionary<string, byte> _flags = new Dictionary<string, byte>(StringComparer.Ordinal);

        public int Count => _flags.Count;

        public bool Contains(string key) => key != null && _flags.ContainsKey(key);

        /// <summary>Keeps <paramref name="key"/>; flags (done, on easy) only add up.</summary>
        public void Add(string key, byte flags)
        {
            if (string.IsNullOrEmpty(key) || !FactCodec.IsValidBountyFlags(flags))
                return;
            _flags[key] = _flags.TryGetValue(key, out byte old) ? (byte)(old | flags) : flags;
        }

        public bool TryGet(string key, out byte flags)
        {
            flags = 0;
            return key != null && _flags.TryGetValue(key, out flags);
        }

        public bool Remove(string key) => key != null && _flags.Remove(key);

        public List<string> Keys() => new List<string>(_flags.Keys);

        public void Clear() => _flags.Clear();

        /// <summary>Adds the pending entries a rebuilt fact table lacks (flags merged with any it has).</summary>
        public int KeepIn(FactTable table)
        {
            if (table == null)
                return 0;
            int kept = 0;
            foreach (var pair in _flags)
            {
                byte flags = pair.Value;
                var current = table.Get(FactKinds.Bounty, pair.Key);
                if (current != null && FactCodec.TryDecodeFlags(current, out byte existing))
                {
                    if ((existing | flags) == existing)
                        continue;
                    flags = (byte)(existing | flags);
                }
                table.Set(FactKinds.Bounty, pair.Key, FactCodec.EncodeFlags(flags));
                kept++;
            }
            return kept;
        }
    }
}
