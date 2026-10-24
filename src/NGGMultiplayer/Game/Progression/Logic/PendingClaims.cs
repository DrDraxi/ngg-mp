using System;
using System.Collections.Generic;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Save;

namespace NGGMultiplayer.Game.Progression
{
    /// <summary>
    /// Host: client claims that arrived while the host's world was not ready (loading, the hold after a load). They
    /// were refused for that reason only, so they are kept and checked again once the world is ready, rather than lost
    /// (<see cref="ClaimRules.IsRetryable"/>). Bounded per peer; the newest value per fact wins; old ones expire.
    /// Pure; main thread only.
    /// </summary>
    public sealed class PendingClaimQueue
    {
        public const int MaxPerPeer = 64;
        public const double MaxAgeSeconds = 120.0;

        private readonly Func<double> _clock;
        private readonly List<Entry> _entries = new List<Entry>();

        public PendingClaimQueue(Func<double> clock)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public int Count => _entries.Count;

        /// <summary>Keeps <paramref name="claim"/> from <paramref name="peer"/>; false when that peer's queue is full.</summary>
        public bool Add(PeerId peer, SaveDelta claim)
        {
            if (!peer.IsValid || claim == null || !claim.IsValid)
                return false;
            int count = 0;
            for (int i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                if (entry.Peer != peer)
                    continue;
                if (entry.Claim.Kind == claim.Kind && entry.Claim.Key == claim.Key)
                {
                    // The newer value of the same fact replaces the older one, at the back of the queue.
                    _entries.RemoveAt(i);
                    _entries.Add(new Entry(peer, claim.Clone(), _clock()));
                    return true;
                }
                count++;
            }
            if (count >= MaxPerPeer)
                return false;
            _entries.Add(new Entry(peer, claim.Clone(), _clock()));
            return true;
        }

        /// <summary>Removes and returns every kept claim, oldest first, dropping those older than <see cref="MaxAgeSeconds"/>.</summary>
        public List<KeyValuePair<PeerId, SaveDelta>> TakeAll()
        {
            var result = new List<KeyValuePair<PeerId, SaveDelta>>(_entries.Count);
            double now = _clock();
            foreach (var entry in _entries)
            {
                if (now - entry.At <= MaxAgeSeconds)
                    result.Add(new KeyValuePair<PeerId, SaveDelta>(entry.Peer, entry.Claim));
            }
            _entries.Clear();
            return result;
        }

        public void Clear() => _entries.Clear();

        private readonly struct Entry
        {
            public Entry(PeerId peer, SaveDelta claim, double at)
            {
                Peer = peer;
                Claim = claim;
                At = at;
            }

            public readonly PeerId Peer;
            public readonly SaveDelta Claim;
            public readonly double At;
        }
    }

    /// <summary>
    /// Objects a player's own bounty / LMUGC round switched on are that player's challenge props: the diff does not
    /// report them (they are not progress), so the session's table still says "inactive". The client's reconcile and
    /// the host's object facts must not switch them off in the middle of that challenge. Pure.
    /// </summary>
    public static class ChallengeProps
    {
        /// <param name="onMainTrack">The player is on the main track (no bounty, no LMUGC round, no other set).</param>
        /// <param name="liveActive">The object is active in the player's world.</param>
        /// <param name="wantedActive">The session's value.</param>
        /// <param name="knownProp">The object was seen switched on while off the main track.</param>
        /// <param name="baselineActive">The object's state in the player's baseline (null: not in it).</param>
        public static bool KeepLocal(bool onMainTrack, bool liveActive, bool wantedActive, bool knownProp, bool? baselineActive)
        {
            if (onMainTrack || !liveActive || wantedActive)
                return false;
            // Switched on locally and never reported: a challenge prop, not a disagreement with the host. Without a
            // baseline (just loaded from the host's save) there is no local change to protect.
            return knownProp || baselineActive == false;
        }

        /// <summary>
        /// Seconds after this player's own challenge-set change (a bounty start or exit, an LMUGC round, a set load)
        /// within which objects it switches off while off the main track count as that challenge's props.
        /// </summary>
        public const double OwnSetChangeWindowSeconds = 5.0;

        /// <summary>
        /// The other half: starting a bounty switches the main challenge's own objects off in this player's world
        /// (ToggleOffPreviousChallengeObjects), and the game switches them back on when the bounty ends. Reporting
        /// that would take them out of every other player's world (the host's main challenge included), so a
        /// deactivation seen in its own challenge shortly after this player's own set change is held back, and stays
        /// held while the player is in that challenge and for the same window after it ended (the set's objects may
        /// appear with a delay). Nothing is lost: should the object still be off after that, the diff reports it then
        /// (its baseline never changed).
        /// </summary>
        /// <param name="inOwnChallenge">The player is in a bounty / LMUGC round / set of its own.</param>
        /// <param name="knownHeld">The object is already held back this way.</param>
        /// <param name="sinceOwnSetChange">Seconds since this player's last own set change.</param>
        public static bool HoldDeactivation(bool inOwnChallenge, bool knownHeld, double sinceOwnSetChange)
        {
            bool recent = sinceOwnSetChange >= 0 && sinceOwnSetChange < OwnSetChangeWindowSeconds;
            return (inOwnChallenge && (knownHeld || recent)) || (knownHeld && recent);
        }

        /// <summary>The objects held by <see cref="HoldDeactivation"/> are released (and diffed normally) now.</summary>
        public static bool ReleaseHeld(bool inOwnChallenge, double sinceOwnSetChange) =>
            !inOwnChallenge && !(sinceOwnSetChange >= 0 && sinceOwnSetChange < OwnSetChangeWindowSeconds);

        /// <summary>
        /// The session says an object held back by <see cref="HoldDeactivation"/> is on: leave it off until the player
        /// is back on the main track (the game turns it on itself then), rather than put it into the bounty.
        /// </summary>
        public static bool KeepLocalOff(bool onMainTrack, bool liveActive, bool wantedActive, bool knownHeld) =>
            !onMainTrack && !liveActive && wantedActive && knownHeld;
    }
}
