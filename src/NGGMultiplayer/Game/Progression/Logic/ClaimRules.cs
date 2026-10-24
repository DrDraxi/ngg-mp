using System;
using System.Collections.Generic;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Save;

namespace NGGMultiplayer.Game.Progression
{
    /// <summary>The host's world as the claim rules see it. Implemented over the live game by the module.</summary>
    public interface IClaimWorld
    {
        /// <summary>The host's world is loaded and settled; claims are checked against it.</summary>
        bool IsReady { get; }

        /// <summary>A challenge set by clean name.</summary>
        bool TryGetChallengeSet(string name, out bool isBounty, out int challengeCount);

        /// <summary>
        /// The SaveableObject with this m_id: whether it is active on the host, and the ids of the arbitrated
        /// interactables on or under it (empty when none).
        /// </summary>
        bool TryGetSaveable(string id, out bool active, out IReadOnlyList<string> arbitratedIds);
    }

    /// <summary>
    /// Host-side validation of client fact claims (docs/GAME_INTEGRATION.md 6.2). Pure: everything it needs comes in
    /// as arguments. A refused claim makes Core resync that client.
    /// </summary>
    public static class ClaimRules
    {
        /// <summary>The refusal reason for claims that arrive while the host's world is not ready (see <see cref="IsRetryable"/>).</summary>
        public const string NotReadyReason = "host world not ready";

        /// <summary>
        /// The most main-track steps one claim may advance (a client that completed two quick challenges before its
        /// diff ran claims both at once; the host completes each in turn).
        /// </summary>
        public const int MaxTrackSteps = 3;

        /// <summary>
        /// A refusal that says nothing about the claim itself (the host was loading): the host keeps such a claim and
        /// checks it again once its world is ready, instead of losing progress the client really made.
        /// </summary>
        public static bool IsRetryable(string reason) => reason == NotReadyReason;

        /// <param name="takenBy">Who an arbitrated interactable id was granted to (<see cref="PeerId.None"/> if nobody).</param>
        public static bool Validate(PeerId from, SaveDelta claim, FactTable table, IClaimWorld world,
            Func<string, PeerId> takenBy, out string reason)
        {
            reason = null;
            if (claim == null || !claim.IsValid)
                return Refuse("malformed", out reason);
            if (!FactKinds.IsKnown(claim.Kind))
                return Refuse("unknown kind", out reason);
            if (FactKinds.IsHostOnly(claim.Kind))
                return Refuse("host-only kind", out reason);
            if (!IsWellFormedValue(claim.Kind, claim.Key, claim.Value))
                return Refuse("malformed value", out reason);

            // Already the session's value (e.g. the client replayed the same effect): a harmless no-op.
            var current = table?.Get(claim.Kind, claim.Key);
            if (current != null && FactCodec.Equivalent(claim.Kind, current, claim.Value))
                return true;

            if (world == null || !world.IsReady)
                return Refuse(NotReadyReason, out reason);

            switch (claim.Kind)
            {
                case FactKinds.Track:
                    return ValidateTrack(claim, current, world, out reason);
                case FactKinds.Bounty:
                    return ValidateBounty(claim, current, world, out reason);
                case FactKinds.DoNotReload:
                case FactKinds.Easy:
                    if (!world.TryGetChallengeSet(claim.Key, out _, out _))
                        return Refuse("unknown challenge set", out reason);
                    return true;
                case FactKinds.RunBool:
                    return ValidateRunBool(claim, current, out reason);
                case FactKinds.RunInt:
                    return ValidateRunInt(claim, current, out reason);
                case FactKinds.Obj:
                    return ValidateObj(from, claim, world, takenBy, out reason);
                case FactKinds.PreHit:
                    return ValidatePreHit(claim, current, out reason);
                default:
                    return Refuse("unhandled kind", out reason);
            }
        }

        /// <summary>Structural check of a fact value for its kind (independent of any state).</summary>
        public static bool IsWellFormedValue(string kind, string key, byte[] value)
        {
            switch (kind)
            {
                case FactKinds.Track:
                    return key == FactKinds.TrackKey && FactCodec.TryDecodeTrack(value, out _);
                case FactKinds.Bounty:
                    return key.Length > 0 && FactCodec.TryDecodeFlags(value, out byte flags) && FactCodec.IsValidBountyFlags(flags);
                case FactKinds.DoNotReload:
                case FactKinds.Easy:
                    return key.Length > 0 && FactCodec.TryDecodeFlags(value, out byte marker) && marker == 1;
                case FactKinds.RunBool:
                    return SharedRunFields.IsSharedBool(key) && FactCodec.TryDecodeBool(value, out _);
                case FactKinds.RunInt:
                    return SharedRunFields.IsSharedInt(key) && FactCodec.TryDecodeInt(value, out int v)
                        && v >= 0 && v <= SharedRunFields.MaxIntValue(key);
                case FactKinds.RunHostInt:
                    return SharedRunFields.IsHostInt(key) && FactCodec.TryDecodeInt(value, out _);
                case FactKinds.Bank:
                    return key.Length == 0 && FactCodec.TryDecodeInt(value, out _);
                case FactKinds.Obj:
                    return key.Length > 0 && FactCodec.TryDecodeObj(value, out _);
                case FactKinds.PreHit:
                    return key.Length == 0 && FactCodec.TryDecodeStringSet(value, out _);
                case FactKinds.Unlocks:
                    return key == FactKinds.UnlocksKey && FactCodec.TryDecodeBoolList(value, out _);
                case FactKinds.Seed:
                    return key.Length == 0 && FactCodec.TryDecodeInt(value, out _);
                case FactKinds.Money:
                    return key == FactKinds.MoneyKey && FactCodec.TryDecodeFlags(value, out byte state) && state <= MoneyGate.Returned;
                case FactKinds.ScoreCard:
                case FactKinds.Auth:
                    return true;
                default:
                    return false;
            }
        }

        private static bool ValidateTrack(SaveDelta claim, byte[] current, IClaimWorld world, out string reason)
        {
            FactCodec.TryDecodeTrack(claim.Value, out var claimed);
            if (!world.TryGetChallengeSet(claimed.Set, out bool claimedIsBounty, out int claimedCount))
                return Refuse("unknown challenge set", out reason);
            if (claimedIsBounty)
                return Refuse("a bounty is not the main track", out reason);
            if (claimed.Index > claimedCount)
                return Refuse("index past the end of the set", out reason);
            if (current == null || !FactCodec.TryDecodeTrack(current, out var host))
            {
                reason = null;
                return true;
            }
            if (string.Equals(claimed.Set, host.Set, StringComparison.Ordinal))
            {
                if (claimed.Index > host.Index && claimed.Index - host.Index <= MaxTrackSteps)
                {
                    reason = null;
                    return true;
                }
                return Refuse($"track step {host.Index} -> {claimed.Index}", out reason);
            }
            // Set change: the host's set is finished (or on its last challenge, which the client just completed
            // before its game moved on to the next set) and the client is at the start of the next one.
            if (!world.TryGetChallengeSet(host.Set, out _, out int hostCount))
                return Refuse("host track set unknown", out reason);
            if (host.Index < hostCount - 1 || claimed.Index > 1)
                return Refuse($"track set change {host} -> {claimed}", out reason);
            reason = null;
            return true;
        }

        private static bool ValidateBounty(SaveDelta claim, byte[] current, IClaimWorld world, out string reason)
        {
            if (!world.TryGetChallengeSet(claim.Key, out bool isBounty, out _) || !isBounty)
                return Refuse("not a bounty", out reason);
            FactCodec.TryDecodeFlags(claim.Value, out byte flags);
            if (current != null && FactCodec.TryDecodeFlags(current, out byte old) && (flags & old) != old)
                return Refuse("bounty flags may only be added", out reason);
            reason = null;
            return true;
        }

        private static bool ValidateRunBool(SaveDelta claim, byte[] current, out string reason)
        {
            FactCodec.TryDecodeBool(claim.Value, out bool claimed);
            bool host = current != null && FactCodec.TryDecodeBool(current, out bool v) && v;
            if (host && !claimed && !SharedRunFields.IsBothWays(claim.Key))
                return Refuse(claim.Key + " cannot be turned off", out reason);
            reason = null;
            return true;
        }

        private static bool ValidateRunInt(SaveDelta claim, byte[] current, out string reason)
        {
            FactCodec.TryDecodeInt(claim.Value, out int claimed);
            if (current != null && FactCodec.TryDecodeInt(current, out int host) && claimed < host)
                return Refuse(claim.Key + " may only increase", out reason);
            reason = null;
            return true;
        }

        private static bool ValidateObj(PeerId from, SaveDelta claim, IClaimWorld world, Func<string, PeerId> takenBy,
            out string reason)
        {
            if (!world.TryGetSaveable(claim.Key, out bool active, out var arbitrated))
                return Refuse("unknown SaveableObject", out reason);
            FactCodec.TryDecodeObj(claim.Value, out var state);
            if (arbitrated != null && arbitrated.Count > 0 && state.Active != active)
            {
                // Picking up / buying is exclusive: only the peer the host granted it to may remove it, and
                // nobody but the host brings it back.
                if (state.Active)
                    return Refuse("only the host restores a pickup", out reason);
                bool granted = false;
                foreach (var id in arbitrated)
                {
                    if (takenBy != null && takenBy(id) == from)
                    {
                        granted = true;
                        break;
                    }
                }
                if (!granted)
                    return Refuse("pickup was not granted to this player", out reason);
            }
            reason = null;
            return true;
        }

        private static bool ValidatePreHit(SaveDelta claim, byte[] current, out string reason)
        {
            FactCodec.TryDecodeStringSet(claim.Value, out var claimed);
            var host = new HashSet<string>(StringComparer.Ordinal);
            if (current != null && FactCodec.TryDecodeStringSet(current, out var hostList))
                host.UnionWith(hostList);
            foreach (var entry in claimed)
            {
                if (!host.Contains(entry))
                {
                    // Adds something; the host stores the union.
                    reason = null;
                    return true;
                }
            }
            return Refuse("pre-hit targets may only be added", out reason);
        }

        private static bool Refuse(string why, out string reason)
        {
            reason = why;
            return false;
        }
    }

    /// <summary>What a peer does with a confirmed <c>track</c> fact (6.3).</summary>
    public enum TrackAction : byte
    {
        /// <summary>Already there or ahead (a claim of its own in flight).</summary>
        None = 0,

        /// <summary>Complete the current main challenge (the fact is exactly one step ahead).</summary>
        Complete = 1,

        /// <summary>Load the fact's set at its index (jump or different set).</summary>
        Load = 2,

        /// <summary>Inside a bounty or another set: apply when back on the main track.</summary>
        Defer = 3
    }

    public static class TrackPlanner
    {
        /// <summary>
        /// Like <see cref="Decide"/>, for the client's reconcile against the session's table: a live track ahead of
        /// the fact in the same set, with no claim of ours in flight, was refused by the host and is rolled back
        /// (<see cref="TrackAction.Load"/> of the fact), so the client claims its next completion from the host's step.
        /// </summary>
        public static TrackAction DecideReconcile(TrackValue fact, bool hasLocal, TrackValue local, bool onMainTrack,
            bool claimInFlight)
        {
            bool sameSet = hasLocal && string.Equals(fact.Set, local.Set, StringComparison.Ordinal);
            if (sameSet && fact.Index < local.Index)
                return onMainTrack && !claimInFlight ? TrackAction.Load : TrackAction.None;
            return Decide(fact, hasLocal, local, onMainTrack);
        }

        /// <summary>
        /// A main-track value the host accepted from a client while it could not apply it (in a bounty) and applies
        /// later, possibly after the session: is it still ahead of the host's own track? A value in another set
        /// counts only while the host's set is at its last challenge or done (as a set change claim requires), so
        /// progress the host made itself in a later set is never undone.
        /// </summary>
        public static bool PendingStillAhead(TrackValue pending, bool hasLocal, TrackValue local, int localSetCount)
        {
            if (!hasLocal)
                return true;
            if (string.Equals(pending.Set, local.Set, StringComparison.Ordinal))
                return pending.Index > local.Index;
            return local.Index >= localSetCount - 1;
        }

        /// <summary>
        /// Where LoadNewChallengeSet(set, skip) can start a set for a fact index: the game clamps skip to Length - 1
        /// (and marks the challenges before it complete), so a finished set (index == count) is loaded at its last
        /// challenge, which is then completed like any other step.
        /// </summary>
        public static int LoadIndex(int factIndex, int count) => count <= 0 ? 0 : Math.Max(0, Math.Min(factIndex, count - 1));

        /// <summary>A set loaded at <see cref="LoadIndex"/> still needs its last challenge completed to reach the fact.</summary>
        public static bool CompleteAfterLoad(int factIndex, int count) => count > 0 && factIndex >= count;

        /// <summary>
        /// ChallengeSystem.CompleteChallenge returns early, marking nothing, for a streak challenge
        /// (m_streaksRequired &gt; 1) while the streak is short; a replay of that completion must lift the streak to
        /// the requirement for the call.
        /// </summary>
        public static bool StreakGateHolds(int streaksRequired, int currentStreak) =>
            streaksRequired > 1 && currentStreak < streaksRequired;

        /// <param name="hasLocal">The peer has a main set (<paramref name="local"/> is its live value).</param>
        /// <param name="onMainTrack">The main set is the loaded set and no bounty is active.</param>
        public static TrackAction Decide(TrackValue fact, bool hasLocal, TrackValue local, bool onMainTrack)
        {
            bool sameSet = hasLocal && string.Equals(fact.Set, local.Set, StringComparison.Ordinal);
            if (sameSet && fact.Index <= local.Index)
                return TrackAction.None;
            if (!onMainTrack)
                return TrackAction.Defer;
            if (sameSet && fact.Index == local.Index + 1)
                return TrackAction.Complete;
            return TrackAction.Load;
        }
    }

    /// <summary>The host-executed money story beats (6.10): DeductMoney once, then ReturnMoney once.</summary>
    public static class MoneyGate
    {
        public const byte None = 0;
        public const byte Deducted = 1;
        public const byte Returned = 2;

        public static bool CanRun(string method, byte state)
        {
            if (method == StoryMethods.DeductMoney)
                return state != Deducted;
            // From None too: the deduction may have happened before this session (the gate lives only as long as
            // the session), and blocking the return would keep the money from the host's save for good.
            if (method == StoryMethods.ReturnMoney)
                return state != Returned;
            return false;
        }

        public static byte After(string method, byte state)
        {
            if (method == StoryMethods.DeductMoney)
                return Deducted;
            if (method == StoryMethods.ReturnMoney)
                return Returned;
            return state;
        }
    }
}
