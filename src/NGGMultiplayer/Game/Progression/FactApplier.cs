using System;
using System.Collections.Generic;
using NGGMultiplayer.Core.Net;
using UnityEngine;
using Object = UnityEngine.Object;

namespace NGGMultiplayer.Game.Progression
{
    internal enum ApplyResult : byte
    {
        /// <summary>The game now holds the value (or already did).</summary>
        Applied,

        /// <summary>Not possible right now (player inside a bounty); applied later by a hook.</summary>
        Deferred,

        /// <summary>Nothing in this game to apply it to (a stored-only fact, an object that does not exist).</summary>
        NotApplicable,

        /// <summary>The game could not be brought in line.</summary>
        Failed
    }

    /// <summary>
    /// Brings the game in line with one confirmed fact (docs/GAME_INTEGRATION.md 6.3 - 6.13). Every game call runs
    /// inside a replay scope, so the mod's own patches neither broadcast it again nor award achievements. Callers
    /// make sure the world is ready. Main thread only.
    /// </summary>
    internal sealed class FactApplier
    {
        private const string Log = "[Progression] ";
        private const double StoryHoldSeconds = 1.0;
        private const double UnlockHoldSeconds = 0.5;
        // The most main-track challenges completed one by one to catch up with the shared track; beyond, it loads.
        private const int MaxCatchUpSteps = 8;

        private readonly ProgressionModule _module;
        private readonly GameContext _ctx;
        private readonly GameAccess _access;
        private readonly LiveFacts _live;

        public FactApplier(ProgressionModule module, GameContext ctx, GameAccess access, LiveFacts live)
        {
            _module = module;
            _ctx = ctx;
            _access = access;
            _live = live;
        }

        /// <param name="claimant">Host applying an accepted claim: who claimed it. Otherwise <see cref="PeerId.None"/>.</param>
        public ApplyResult Apply(string kind, string key, byte[] value, bool onHost, PeerId claimant)
        {
            if (SaveManager.instance == null)
                return ApplyResult.Deferred;
            using (_ctx.Replay.Begin("pg." + kind))
            {
                switch (kind)
                {
                    case FactKinds.Track: return ApplyTrack(value, onHost, claimant, false);
                    case FactKinds.Bounty: return ApplyBounty(key, value, onHost, claimant);
                    case FactKinds.DoNotReload: return AddToList(ListOf(kind), key);
                    case FactKinds.Easy: return AddToList(ListOf(kind), key);
                    case FactKinds.RunBool: return ApplyRunBool(key, value, onHost, claimant);
                    case FactKinds.RunInt:
                    case FactKinds.RunHostInt: return ApplyRunInt(kind, key, value, onHost);
                    case FactKinds.Bank: return onHost ? ApplyResult.NotApplicable : ApplyBank(value);
                    case FactKinds.Obj: return ApplyObj(key, value);
                    case FactKinds.PreHit: return ApplyPreHit(value);
                    case FactKinds.Unlocks: return onHost ? ApplyResult.NotApplicable : ApplyUnlocks(value);
                    case FactKinds.ScoreCard: return onHost ? ApplyResult.NotApplicable : ApplyScoreCard(value);
                    case FactKinds.Auth: return onHost ? ApplyResult.NotApplicable : ApplyAuth(key, value);
                    default: return ApplyResult.NotApplicable;
                }
            }
        }

        /// <summary>
        /// Client reconcile of the shared track: like <see cref="Apply"/>, and a live track ahead of the session's
        /// (progress the host refused; the caller checked no claim of ours is in flight) is rolled back to it.
        /// </summary>
        public ApplyResult ReconcileTrack(byte[] value)
        {
            if (SaveManager.instance == null)
                return ApplyResult.Deferred;
            using (_ctx.Replay.Begin("pg.track.reconcile"))
                return ApplyTrack(value, false, PeerId.None, true);
        }

        /// <summary>Client reconcile: the host does not have this list entry; take it out of the local run.</summary>
        public void RemoveListEntry(string kind, string key)
        {
            var manager = SaveManager.instance;
            if (manager == null)
                return;
            using (_ctx.Replay.Begin("pg.remove." + kind))
            {
                ref RunSave run = ref manager.m_run;
                switch (kind)
                {
                    case FactKinds.Bounty:
                        run.m_completedBounties?.Remove(key);
                        run.m_completedOnEasy?.Remove(key);
                        bool playing = _live.CurrentPlay().IsPlaying(GameIds.Clean(key));
                        foreach (var pad in FindPads(GameIds.Clean(key)))
                        {
                            if (pad.m_completed && !playing && !IsActivePad(pad))
                                pad.Incomplete();
                        }
                        _access.UpdateBountyBoards();
                        break;
                    case FactKinds.DoNotReload:
                        run.m_doNotReloadChallengeList?.Remove(key);
                        break;
                    case FactKinds.Easy:
                        run.m_completedOnEasy?.Remove(key);
                        break;
                }
            }
        }

        // ---- track (6.3) ----------------------------------------------------------------------------

        private ApplyResult ApplyTrack(byte[] value, bool onHost, PeerId claimant, bool reconcile)
        {
            if (!FactCodec.TryDecodeTrack(value, out var fact))
                return ApplyResult.Failed;
            var system = ChallengeSystem.instance;
            if (system == null || !_access.TrackEnabled || _live.IsJustGolf)
                return ApplyResult.NotApplicable;
            bool hasLocal = _live.TryGetTrack(out var local, out bool onMainTrack);
            // No readable main set (the LMUGC set left as main) while in no round and no bounty: take the shared one.
            if (!hasLocal && !_live.InBounty() && !_live.InLmugc())
                onMainTrack = true;
            var action = reconcile
                ? TrackPlanner.DecideReconcile(fact, hasLocal, local, onMainTrack, false)
                : TrackPlanner.Decide(fact, hasLocal, local, onMainTrack);
            // Completing or loading runs challenge code (functionOnComplete / functionOnStart, Restart challenges):
            // not into the local player's own dialogue, cutscene or shot (6.15). Resolved when they are free.
            if ((action == TrackAction.Complete || action == TrackAction.Load) && _module.Cutscenes.HoldTrack())
            {
                if (onHost && claimant.IsValid)
                    NoteHeldClaim(system, fact, hasLocal, local, claimant);
                _module.OnTrackDeferred(value);
                return ApplyResult.Deferred;
            }
            switch (action)
            {
                case TrackAction.None:
                    return ApplyResult.Applied;
                case TrackAction.Defer:
                    _module.OnTrackDeferred(value);
                    return ApplyResult.Deferred;
                case TrackAction.Complete:
                case TrackAction.Load when hasLocal && CanCatchUp(fact, local):
                {
                    // Each challenge in turn, as the player's own game did: their functionOnComplete story beats run
                    // here too, which a jump with LoadNewChallengeSet would skip.
                    string text = string.Empty;
                    bool reached = CompleteTowards(system, fact, ref local, ref text);
                    _ctx.Replay.HoldEffectsFor(StoryHoldSeconds);
                    if (onHost && claimant.IsValid && text.Length > 0)
                        _module.SendNote(claimant, NoteKind.Challenge, _ctx.PlayerName(claimant) + " completed: " + Shorten(text, fact.Set));
                    if (reached)
                        return ApplyResult.Applied;
                    if (!_live.TryGetTrack(out local, out onMainTrack) || !onMainTrack)
                        return ApplyResult.Failed;
                    // The game moved on by itself (a finished set leading into the next one): never fight it.
                    if (!string.Equals(local.Set, fact.Set, StringComparison.Ordinal) || local.Index >= fact.Index)
                        return ApplyResult.Applied;
                    _ctx.WarnOnce("pg.track.catchup:" + fact.Set, $"{Log}The game did not follow the shared track's challenges one by one; loading {fact} instead.");
                    return LoadTrack(system, fact);
                }
                default:
                    return LoadTrack(system, fact);
            }
        }

        /// <summary>Host: the claim is accepted (the others move on now) although the host's own game catches up later.</summary>
        private void NoteHeldClaim(ChallengeSystem system, TrackValue fact, bool hasLocal, TrackValue local, PeerId claimant)
        {
            string text = string.Empty;
            if (hasLocal && string.Equals(fact.Set, local.Set, StringComparison.Ordinal))
            {
                var challenges = _access.Challenges(system.m_mainChallengeSet);
                if (challenges != null && local.Index >= 0 && local.Index < challenges.Length)
                    text = _access.ShortString(challenges[local.Index]);
            }
            _module.SendNote(claimant, NoteKind.Challenge, _ctx.PlayerName(claimant) + " completed: " + Shorten(text, fact.Set));
        }

        private static bool CanCatchUp(TrackValue fact, TrackValue local) =>
            string.Equals(fact.Set, local.Set, StringComparison.Ordinal) && fact.Index > local.Index
            && fact.Index - local.Index <= MaxCatchUpSteps;

        /// <summary>Completes the main set's current challenge until the track reaches the fact; false when it stalls.</summary>
        private bool CompleteTowards(ChallengeSystem system, TrackValue fact, ref TrackValue local, ref string firstText)
        {
            for (int step = 0; step < MaxCatchUpSteps; step++)
            {
                if (!CanCatchUp(fact, local))
                    break;
                var challenges = _access.Challenges(system.m_mainChallengeSet);
                if (challenges == null || local.Index >= challenges.Length || challenges[local.Index] == null)
                    return false;
                var challenge = challenges[local.Index];
                if (step == 0)
                    firstText = _access.ShortString(challenge);
                // The next challenge's functionOnStart runs because of this replay too.
                _module.Cutscenes.MarkStart(system.m_mainChallengeSet, local.Index + 1);
                if (!CompleteStep(system, challenge))
                    return false;
                var before = local;
                if (!_live.TryGetTrack(out local, out bool onMainTrack) || !onMainTrack)
                    return false;
                if (!string.Equals(local.Set, before.Set, StringComparison.Ordinal) || local.Index != before.Index + 1)
                    return false;
            }
            return string.Equals(fact.Set, local.Set, StringComparison.Ordinal) && local.Index == fact.Index;
        }

        /// <summary>
        /// One main-track challenge as the player's own game completes it (answers 86, 141): CompleteChallenge only
        /// marks it (and runs its bounty logic, functionOnComplete and SaveRun); the next challenge is activated by
        /// RefreshChallenges, which the game runs from Restart, its auto-advance or a set load. Without that, the
        /// game's current challenge falls back to the last one. A streak challenge (m_streaksRequired &gt; 1) returns
        /// early while the streak is short, so the streak is lifted to the requirement for the call.
        /// </summary>
        private bool CompleteStep(ChallengeSystem system, Challenge challenge)
        {
            int required = _access.StreaksRequired(challenge);
            int? streak = _access.CurrentStreak;
            bool lifted = false;
            if (TrackPlanner.StreakGateHolds(required, streak ?? 0))
            {
                lifted = _access.SetCurrentStreak(required);
                _ctx.Info($"{Log}Shared track: lifting the streak {(streak.HasValue ? streak.Value.ToString() : "?")} -> {required} to complete a streak challenge{(lifted ? "" : " failed (streak not writable)")}.");
            }
            // Its functionOnComplete is Invoked by name next frame: the replay's, not this player's (6.15).
            _module.Cutscenes.MarkCompletion(challenge);
            system.CompleteChallenge(challenge);
            // Put the player's own streak back unless the game changed it meanwhile.
            if (lifted && streak.HasValue && _access.CurrentStreak == required)
                _access.SetCurrentStreak(streak.Value);
            if (!_access.IsComplete(challenge))
            {
                _ctx.WarnOnce("pg.track.notComplete", $"{Log}ChallengeSystem.CompleteChallenge did not mark a shared-track challenge complete (streak gate: {required} > 1 = {required > 1}).");
                return false;
            }
            var active = _access.HasActiveChallenge(system.m_mainChallengeSet);
            if (active == true)
            {
                // Restart / Aim / m_autoAdvance challenges refresh by themselves.
                _module.InfoOnce("pg.track.selfRefresh", $"{Log}Shared track: the game activated the next challenge itself after CompleteChallenge.");
                return true;
            }
            system.RefreshChallenges(false);
            _module.InfoOnce("pg.track.refresh", $"{Log}Shared track: CompleteChallenge left no challenge active; called RefreshChallenges(false) (active afterwards: {_access.HasActiveChallenge(system.m_mainChallengeSet)?.ToString() ?? "unknown"}).");
            return true;
        }

        private ApplyResult LoadTrack(ChallengeSystem system, TrackValue fact)
        {
            var asset = _access.FindChallengeSet(fact.Set);
            int count = _access.ChallengeCount(asset);
            if (asset == null || count == 0 || _access.IsBounty(asset))
            {
                _ctx.WarnOnce("pg.track.load:" + fact.Set, $"{Log}Cannot load challenge set '{fact.Set}' for the shared track.");
                return ApplyResult.Failed;
            }
            // Like SaveManager.SetUpScene: play a runtime copy, never the asset. LoadNewChallengeSet marks the
            // challenges before the index complete and clamps the index to the last challenge (answers 39), and it
            // makes a non-bounty set the main one itself.
            var set = Object.Instantiate(asset);
            int loadIndex = TrackPlanner.LoadIndex(fact.Index, count);
            _module.Cutscenes.MarkStart(set, loadIndex);
            system.LoadNewChallengeSet(set, loadIndex);
            if (system.m_challengeSet == set && system.m_mainChallengeSet != set)
            {
                _ctx.WarnOnce("pg.track.mainSet", $"{Log}LoadNewChallengeSet did not make '{fact.Set}' the main set; setting it.");
                system.m_mainChallengeSet = set;
            }
            // A finished set loads at its last challenge, which is then completed like any other step.
            if (TrackPlanner.CompleteAfterLoad(fact.Index, count))
            {
                var challenges = _access.Challenges(system.m_mainChallengeSet);
                var last = challenges != null && challenges.Length == count ? challenges[count - 1] : null;
                if (last != null && !_access.IsComplete(last))
                    CompleteStep(system, last);
            }
            _ctx.Replay.HoldEffectsFor(StoryHoldSeconds);
            _ctx.Info($"{Log}Shared track moved to {fact}.");
            return ApplyResult.Applied;
        }

        private static string Shorten(string text, string fallback) => string.IsNullOrEmpty(text) ? fallback : text;

        // ---- bounties (6.4) -------------------------------------------------------------------------

        private ApplyResult ApplyBounty(string key, byte[] value, bool onHost, PeerId claimant)
        {
            if (!FactCodec.TryDecodeFlags(value, out byte flags))
                return ApplyResult.Failed;
            var manager = SaveManager.instance;
            ref RunSave run = ref manager.m_run;
            if (run.m_completedBounties == null)
                run.m_completedBounties = new List<string>();
            string name = GameIds.Clean(key);
            bool added = !run.m_completedBounties.Contains(key);
            // The claimant's own CompleteChallenge paid it in its game; on the host the purse pays it once.
            if (BountyIsolation.PayOnHost(added, onHost, claimant.IsValid, _module.WasPaidRemotely(name)))
                PayBounty(name);
            if (BountyIsolation.DeferEntry(added, _live.CurrentPlay(), name))
            {
                // This player is in that bounty right now (its pad, or the set from a tee / the LMUGC machine). Its
                // own completion pays, completes the pads and exits the bounty only while HasCompletedThisBounty is
                // false (answers 40), so the entry waits until the player leaves it (or completes it: the game then
                // adds it itself, and the host's refund takes back a second payment).
                _module.DeferBountyEntry(key, flags);
                return ApplyResult.Deferred;
            }
            if (added)
                run.m_completedBounties.Add(key);
            if ((flags & FactCodec.BountyOnEasy) != 0)
            {
                if (run.m_completedOnEasy == null)
                    run.m_completedOnEasy = new List<string>();
                if (!run.m_completedOnEasy.Contains(key))
                    run.m_completedOnEasy.Add(key);
            }
            CompletePads(name);
            if (!added)
                return ApplyResult.Applied;
            _access.UpdateBountyBoards();
            _ctx.Replay.HoldEffectsFor(StoryHoldSeconds);
            if (onHost)
            {
                manager.SaveRun();
                if (claimant.IsValid)
                    _module.SendNote(claimant, NoteKind.Bounty, _ctx.PlayerName(claimant) + " completed bounty " + name);
            }
            return ApplyResult.Applied;
        }

        private void PayBounty(string name)
        {
            var bank = _access.Bank();
            int? reward = _access.BountyReward(_access.FindChallengeSet(name));
            if (bank == null || !reward.HasValue)
            {
                _ctx.WarnOnce("pg.bounty.reward", $"{Log}Cannot compute bounty rewards; a client's bounty pays nothing on the host.");
                return;
            }
            if (reward.Value != 0)
                bank.ChangeBalance(reward.Value, false, false);
            _module.MarkPaidRemotely(name);
            // The host's purse changed for a client: saved like the game saves its own (also when the entry waits).
            _module.ScheduleHostSave();
        }

        /// <summary>
        /// Completes every pad of a bounty everyone has finished (the game completes all pads whose set has that
        /// clean name, answers 40), unless this player is playing it now.
        /// </summary>
        public void CompletePads(string name)
        {
            var pads = FindPads(name);
            bool playing = BountyIsolation.DeferPads(_live.CurrentPlay(), name);
            foreach (var pad in pads)
            {
                if (pad.m_completed)
                    continue;
                if (playing || IsActivePad(pad))
                {
                    _module.DeferBounty(name);
                    continue;
                }
                using (_ctx.Replay.Begin("pg.pad"))
                    pad.Complete(false);
            }
        }

        public static bool IsActivePad(BountyPad pad)
        {
            var controller = MoveAndHitController.instance;
            return controller != null && controller.m_activeBountyPad != null && controller.m_activeBountyPad == pad;
        }

        private static List<BountyPad> FindPads(string name)
        {
            var pads = new List<BountyPad>();
            if (string.IsNullOrEmpty(name))
                return pads;
            foreach (var pad in Compat.FindAll<BountyPad>(true))
            {
                if (pad != null && pad.m_bountyChallengeSet != null && GameIds.ChallengeSetName(pad.m_bountyChallengeSet) == name)
                    pads.Add(pad);
            }
            return pads;
        }

        // ---- lists ----------------------------------------------------------------------------------

        private static List<string> ListOf(string kind)
        {
            var manager = SaveManager.instance;
            ref RunSave run = ref manager.m_run;
            if (kind == FactKinds.DoNotReload)
            {
                if (run.m_doNotReloadChallengeList == null)
                    run.m_doNotReloadChallengeList = new List<string>();
                return run.m_doNotReloadChallengeList;
            }
            if (run.m_completedOnEasy == null)
                run.m_completedOnEasy = new List<string>();
            return run.m_completedOnEasy;
        }

        private static ApplyResult AddToList(List<string> list, string key)
        {
            if (!list.Contains(key))
                list.Add(key);
            return ApplyResult.Applied;
        }

        // ---- RunSave fields (6.5) -------------------------------------------------------------------

        private ApplyResult ApplyRunBool(string key, byte[] value, bool onHost, PeerId claimant)
        {
            if (!FactCodec.TryDecodeBool(value, out bool wanted))
                return ApplyResult.Failed;
            ref RunSave run = ref SaveManager.instance.m_run;
            if (!RunFields.TryGetBool(ref run, key, out bool current))
                return ApplyResult.NotApplicable;
            if (current == wanted)
                return ApplyResult.Applied;
            // Field first, then the game's own unlock (which SetUpScene also calls with the field already set).
            RunFields.TrySetBool(ref run, key, wanted);
            if (wanted)
                CallUnlock(key);
            else if (key == SharedRunFields.ServerCut)
                Call("ServerRestore", () => _access.ObjectStates()?.ServerRestore());
            _ctx.Replay.HoldFor(UnlockHoldSeconds);
            string feature = SharedRunFields.FeatureName(key);
            if (wanted && feature != null && onHost && claimant.IsValid)
                _module.SendNote(claimant, NoteKind.Unlock, _ctx.PlayerName(claimant) + " unlocked: " + feature);
            return ApplyResult.Applied;
        }

        private void CallUnlock(string key)
        {
            var progress = ProgressManager.instance;
            switch (key)
            {
                case "m_stancePanelUnlocked":
                    if (progress != null)
                        Call(key, () => progress.UnlockStance(false));
                    break;
                case "m_practiceUnlocked":
                    if (progress != null)
                        Call(key, () => progress.UnlockPractice(false));
                    break;
                case "m_aimUnlocked":
                    if (progress != null)
                        Call(key, () => progress.UnlockAdjust(false));
                    break;
                case "m_spinUnlocked":
                    Call(key, () => _access.UnlockSpin(false));
                    break;
                case "m_windUnlocked":
                    // Not idempotent (another WindCycle per call, answers 95): the field went false -> true here, and
                    // WorldEvents' StartWind gate keeps a replay from starting a second cycle in this world.
                    var panels = PanelManager.instance;
                    if (panels != null && panels.m_windPanel != null)
                        Call(key, () => panels.m_windPanel.StartWind(false));
                    break;
                case "m_putterUnlocked":
                    // The club panel goes with the putter, not the iron (the iron is always unlocked; answers 37, 94).
                    UnlockClub(progress, "Putter");
                    Call("club panel", () => _access.UnlockClubPanel(false));
                    break;
                case "m_driverUnlocked":
                    UnlockClub(progress, "Driver");
                    break;
                case "m_wedgeUnlocked":
                    UnlockClub(progress, "Wedge");
                    break;
                case "m_hybridUnlocked":
                    UnlockClub(progress, "Hybrid");
                    break;
                case "m_cardUnlocked":
                    Call(key, () => _access.ObjectStates()?.CheckRFIDDoors());
                    break;
                case SharedRunFields.ServerCut:
                    Call(key, () => _access.ObjectStates()?.ServerCut());
                    break;
                case "m_bunkerOpen":
                    var states = _access.ObjectStates();
                    if (states != null && states.m_bunkerTerminal != null)
                        Call(key, () => states.m_bunkerTerminal.SetScreenOpen());
                    break;
            }
        }

        private void UnlockClub(ProgressManager progress, string clubName)
        {
            if (progress == null)
                return;
            var club = _access.FindClub(clubName);
            if (club == null)
            {
                _ctx.WarnOnce("pg.club:" + clubName, $"{Log}Club '{clubName}' not found; its unlock is only recorded.");
                return;
            }
            Call("club " + clubName, () => progress.UnlockClub(club));
        }

        private ApplyResult ApplyRunInt(string kind, string key, byte[] value, bool onHost)
        {
            if (!FactCodec.TryDecodeInt(value, out int wanted))
                return ApplyResult.Failed;
            var manager = SaveManager.instance;
            ref RunSave run = ref manager.m_run;
            if (!RunFields.TryGetInt(ref run, key, out int current))
                return ApplyResult.NotApplicable;
            if (current == wanted)
                return ApplyResult.Applied;
            RunFields.TrySetInt(ref run, key, wanted);
            if (key == SharedRunFields.BountyAccessLevel)
            {
                Call(key, () => manager.SetBountyPadStates(wanted));
                _ctx.Replay.HoldFor(UnlockHoldSeconds);
            }
            else if (kind == FactKinds.RunHostInt)
            {
                var panels = PanelManager.instance;
                if (panels != null && panels.m_LMUGCPanel != null)
                    Call(key, () => panels.m_LMUGCPanel.UpdateBestScores());
            }
            return ApplyResult.Applied;
        }

        // ---- bank (6.6) -----------------------------------------------------------------------------

        public ApplyResult ApplyBank(byte[] value)
        {
            if (!FactCodec.TryDecodeInt(value, out int balance))
                return ApplyResult.Failed;
            var bank = _access.Bank();
            if (bank == null)
                return ApplyResult.Deferred;
            SaveManager.instance.m_run.m_bankBalance = balance;
            if (bank.m_balance != balance)
            {
                bank.m_balance = balance;
                bank.UpdateVisuals();
            }
            // Bank set directly (not through ChangeBalance): the "You have $N" line follows here (inside Apply's replay scope).
            ProgressionPatches.RefreshBalanceReminder(_ctx);
            return ApplyResult.Applied;
        }

        // ---- SaveableObjects (6.9) ------------------------------------------------------------------

        private ApplyResult ApplyObj(string key, byte[] value)
        {
            if (!FactCodec.TryDecodeObj(value, out var state))
                return ApplyResult.Failed;
            if (_live.IsExcluded(key))
                return ApplyResult.NotApplicable;
            var saveable = GameIds.ResolveSaveable(key);
            if (saveable == null)
                return ApplyResult.NotApplicable;
            var go = saveable.gameObject;
            if (go.activeSelf != state.Active)
                go.SetActive(state.Active);
            if (state.HasRotation && saveable.m_saveRotation)
                ApplyRotation(saveable, LiveFacts.ToUnity(state.Rotation.Normalized()));
            return ApplyResult.Applied;
        }

        // SetUpScene applies m_savedObjects with gameObject.SetActive and, for m_saveRotation, transform.rotation:
        // world space, as GetSaveData reads it (answers 32, 90).
        private static void ApplyRotation(SaveableObject saveable, Quaternion rotation) => saveable.transform.rotation = rotation;

        // ---- pre-hit targets ------------------------------------------------------------------------

        private ApplyResult ApplyPreHit(byte[] value)
        {
            if (!FactCodec.TryDecodeStringSet(value, out var wanted))
                return ApplyResult.Failed;
            ref RunSave run = ref SaveManager.instance.m_run;
            var system = ChallengeSystem.instance;
            var missing = BountyIsolation.MissingEntries(wanted, run.m_preHitTargets, system != null ? system.m_preBrokenShots : null);
            if (BountyIsolation.DeferPreHit(missing.Count > 0, _live.CurrentPlay()))
            {
                // In a bounty / LMUGC round of its own: the targets wait outside the game's lists (the live pht value
                // still includes them) until the player is out of it.
                bool added = false;
                foreach (var entry in missing)
                    added |= _live.DeferredPreHit.Add(entry);
                if (added)
                    _module.DeferPreHit();
                return ApplyResult.Deferred;
            }
            AddPreHit(ref run, system, wanted);
            return ApplyResult.Applied;
        }

        /// <summary>The player is out of its own challenge: hands the deferred pre-hit targets to the game.</summary>
        public void ResolvePreHit()
        {
            var manager = SaveManager.instance;
            if (manager == null || _live.DeferredPreHit.Count == 0)
                return;
            var entries = new List<string>(_live.DeferredPreHit);
            _live.DeferredPreHit.Clear();
            using (_ctx.Replay.Begin("pg.pht.deferred"))
                AddPreHit(ref manager.m_run, ChallengeSystem.instance, entries);
        }

        /// <summary>
        /// Host, before the game's own save when the world is about to go (quit to the menu): what still waits for
        /// the host to leave its bounty goes into the run, so the save keeps it. The bounties were paid when they
        /// were accepted; the pads follow from the run when the world loads again. Returns how many entries were added.
        /// </summary>
        public int FlushIntoRun(HostPendingBounties pending)
        {
            var manager = SaveManager.instance;
            if (manager == null)
                return 0;
            ref RunSave run = ref manager.m_run;
            if (_live.DeferredPreHit.Count > 0)
            {
                var entries = new List<string>(_live.DeferredPreHit);
                _live.DeferredPreHit.Clear();
                AddPreHit(ref run, ChallengeSystem.instance, entries);
            }
            if (pending == null || pending.Count == 0)
                return 0;
            if (run.m_completedBounties == null)
                run.m_completedBounties = new List<string>();
            int added = 0;
            foreach (var key in pending.Keys())
            {
                pending.TryGet(key, out byte flags);
                pending.Remove(key);
                if (!run.m_completedBounties.Contains(key))
                {
                    run.m_completedBounties.Add(key);
                    added++;
                }
                if ((flags & FactCodec.BountyOnEasy) != 0)
                {
                    if (run.m_completedOnEasy == null)
                        run.m_completedOnEasy = new List<string>();
                    if (!run.m_completedOnEasy.Contains(key))
                        run.m_completedOnEasy.Add(key);
                }
            }
            return added;
        }

        private static void AddPreHit(ref RunSave run, ChallengeSystem system, List<string> wanted)
        {
            var union = new HashSet<string>(StringComparer.Ordinal);
            if (run.m_preHitTargets != null)
                union.UnionWith(run.m_preHitTargets);
            int before = union.Count;
            union.UnionWith(wanted);
            if (union.Count != before || run.m_preHitTargets == null)
                run.m_preHitTargets = FactCodec.NormalizeSet(union).ToArray();
            if (system != null)
            {
                if (system.m_preBrokenShots == null)
                    system.m_preBrokenShots = new List<string>();
                foreach (var entry in wanted)
                {
                    if (!system.m_preBrokenShots.Contains(entry))
                        system.m_preBrokenShots.Add(entry);
                }
            }
        }

        // ---- host-only facts on clients -------------------------------------------------------------

        private ApplyResult ApplyUnlocks(byte[] value)
        {
            if (!FactCodec.TryDecodeBoolList(value, out var wanted))
                return ApplyResult.Failed;
            ref UnlocksSave unlocks = ref SaveManager.instance.m_unlocks;
            var old = unlocks.unlockedBalls;
            bool added = false;
            for (int i = 0; i < wanted.Count; i++)
            {
                if (wanted[i] && (old == null || i >= old.Count || !old[i]))
                    added = true;
            }
            unlocks.unlockedBalls = wanted;
            if (added)
                unlocks.showNewBallAlert = true;
            return ApplyResult.Applied;
        }

        private ApplyResult ApplyScoreCard(byte[] value)
        {
            string json = FactTable.DecodeString(value);
            if (string.IsNullOrEmpty(json))
                return ApplyResult.Failed;
            SaveManager.instance.m_scoreCardSave = JsonUtility.FromJson<ScoreCardSave>(json);
            return ApplyResult.Applied;
        }

        private ApplyResult ApplyAuth(string key, byte[] value)
        {
            var authenticator = GameIds.Resolve<Authenticator>(key);
            if (authenticator == null)
                return ApplyResult.NotApplicable;
            return SetCode(authenticator, FactTable.DecodeString(value)) ? ApplyResult.Applied : ApplyResult.Failed;
        }

        /// <summary>
        /// Sets the Authenticator's code, and the text showing it, to the host's. Both are public; the game's NewCode
        /// sets m_text.text = code, and InputBoard compares its input with auth.code (answers 21, 42).
        /// </summary>
        public bool SetCode(Authenticator authenticator, string code)
        {
            if (authenticator == null || string.IsNullOrEmpty(code))
                return false;
            if (authenticator.code != code)
                authenticator.code = code;
            if (authenticator.m_text != null && authenticator.m_text.text != code)
                authenticator.m_text.text = code;
            return true;
        }

        private void Call(string what, Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                _ctx.WarnOnce("pg.apply:" + what, $"{Log}Applying '{what}' to the game failed: {e.GetBaseException().Message}");
            }
        }
    }
}
