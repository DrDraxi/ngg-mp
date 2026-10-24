using System;
using System.Collections.Generic;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Save;
using NGGMultiplayer.Game;
using NGGMultiplayer.Game.Progression;
using Xunit;

namespace NGGMultiplayer.Tests.GameProgression
{
    /// <summary>
    /// Two players and bounties: the same bounty at once, different bounties at once, the LMUGC round as a bounty set,
    /// and the locks either player can open (the claim rules and the arbiter behind them).
    /// </summary>
    public class BountyIsolationTests
    {
        private static readonly PeerId Host = new PeerId(1);
        private static readonly PeerId Client = new PeerId(2);

        private static LocalPlay OnMain => new LocalPlay("", "ML_NormalGolf", "ML_NormalGolf", false);
        private static LocalPlay OnPad(string set) => new LocalPlay(set, set, "ML_NormalGolf", false);
        private static LocalPlay FromTee(string set) => new LocalPlay("", set, "ML_NormalGolf", false);
        private static LocalPlay InLmugc => new LocalPlay("", "ML_DoLMUGC_Course", "ML_NormalGolf", true);

        // ---- What this player is playing ------------------------------------------------------------

        [Fact]
        public void LocalPlay_KnowsWhichSetThePlayerIsIn()
        {
            Assert.False(OnMain.InOwnChallenge);
            Assert.False(OnMain.IsPlaying("ML_NormalGolf"));
            Assert.False(OnMain.IsPlaying("BO_Glass"));

            Assert.True(OnPad("BO_Glass").InOwnChallenge);
            Assert.True(OnPad("BO_Glass").IsPlaying("BO_Glass"));
            Assert.False(OnPad("BO_Glass").IsPlaying("BO_Gong"));

            // A bounty set loaded by a tee (no pad) and the LMUGC round (a bounty set, answers 143).
            Assert.True(FromTee("PAR_Hole3").InOwnChallenge);
            Assert.True(FromTee("PAR_Hole3").IsPlaying("PAR_Hole3"));
            Assert.True(InLmugc.InOwnChallenge);
            Assert.True(InLmugc.IsPlaying("ML_DoLMUGC_Course"));
            Assert.False(InLmugc.IsPlaying("BO_Glass"));

            // Between rounds an LMUGC set left as the main one is not "playing" it.
            var lmugcAsMain = new LocalPlay("", "ML_DoLMUGC_Course", "ML_DoLMUGC_Course", false);
            Assert.False(lmugcAsMain.InOwnChallenge);
            Assert.False(lmugcAsMain.IsPlaying("ML_DoLMUGC_Course"));
            Assert.False(new LocalPlay(null, null, null, false).InOwnChallenge);
            Assert.False(OnPad("BO_Glass").IsPlaying(null));
        }

        // ---- The same bounty at once ----------------------------------------------------------------

        [Fact]
        public void SameBounty_ClientFirst_HostPaysOnce_AndItsOwnCompletionIsTakenBack()
        {
            const int reward = 500;
            int bank = 1000;
            bool paid = false;
            var pending = new HostPendingBounties();
            var table = new FactTable();

            // The client's claim reaches the host while the host plays the same pad.
            bool added = true;
            if (BountyIsolation.PayOnHost(added, true, Client.IsValid, paid))
            {
                bank += reward;
                paid = true;
            }
            Assert.Equal(1500, bank);
            Assert.True(BountyIsolation.DeferEntry(added, OnPad("BO_Glass"), "BO_Glass"));
            pending.Add("BO_Glass", FactCodec.BountyDone);
            table.Set(FactKinds.Bounty, "BO_Glass", FactCodec.EncodeFlags(FactCodec.BountyDone));

            // The host finishes it too: its game pays again (HasCompletedThisBounty is still false) ...
            int before = bank;
            bank += reward;
            // ... and the refund takes that back.
            bank -= BountyIsolation.Refund(paid, before, bank);
            Assert.Equal(1500, bank);

            // A second claim for it (the client's diff, a resync) pays nothing.
            Assert.False(BountyIsolation.PayOnHost(true, true, true, paid));
            Assert.False(BountyIsolation.PayOnHost(false, true, true, false));
            Assert.Equal(0, BountyIsolation.Refund(false, 0, 500));
            Assert.Equal(0, BountyIsolation.Refund(true, 500, 500));
        }

        [Fact]
        public void SameBounty_TheEntryAndPadsWait_OnlyForThePlayerInIt()
        {
            // Completed by the other player: the one still playing it keeps playing (its own completion still exits).
            Assert.True(BountyIsolation.DeferEntry(true, OnPad("BO_Glass"), "BO_Glass"));
            Assert.True(BountyIsolation.DeferPads(OnPad("BO_Glass"), "BO_Glass"));
            Assert.True(BountyIsolation.DeferEntry(true, FromTee("BO_Glass"), "BO_Glass"));
            // Already in the run (this player completed it first): nothing waits.
            Assert.False(BountyIsolation.DeferEntry(false, OnPad("BO_Glass"), "BO_Glass"));
            // Not playing it: applies at once.
            Assert.False(BountyIsolation.DeferEntry(true, OnMain, "BO_Glass"));
            Assert.False(BountyIsolation.DeferPads(OnMain, "BO_Glass"));
        }

        // ---- Different bounties at once ---------------------------------------------------------------

        [Fact]
        public void DifferentBounty_IsAppliedAtOnce_AndNeverTouchesTheOneBeingPlayed()
        {
            var play = OnPad("BO_Gong");
            Assert.False(BountyIsolation.DeferEntry(true, play, "BO_Glass"));
            Assert.False(BountyIsolation.DeferPads(play, "BO_Glass"));
            // The other player's LMUGC round completing does not wait for this player's bounty either.
            Assert.False(BountyIsolation.DeferEntry(true, play, "ML_DoLMUGC_Course"));
            // But one LMUGC round does wait for the other (one ChallengeSystem per game).
            Assert.True(BountyIsolation.DeferEntry(true, InLmugc, "ML_DoLMUGC_Course"));
        }

        [Fact]
        public void DeferredWork_IsResolvedOnlyOutOfTheOwnChallenge()
        {
            Assert.True(BountyIsolation.CanResolve(true, true, false, OnMain));
            Assert.False(BountyIsolation.CanResolve(false, true, false, OnMain));
            Assert.False(BountyIsolation.CanResolve(true, false, false, OnMain));
            Assert.False(BountyIsolation.CanResolve(true, true, true, OnMain));
            Assert.False(BountyIsolation.CanResolve(true, true, false, OnPad("BO_Glass")));
            Assert.False(BountyIsolation.CanResolve(true, true, false, InLmugc));
        }

        [Fact]
        public void PreHitTargets_OfTheOtherPlayer_WaitWhileInAChallenge()
        {
            var run = new List<string> { "glass_1" };
            var live = new List<string> { "glass_2" };
            var missing = BountyIsolation.MissingEntries(new[] { "glass_1", "glass_2", "glass_3", "glass_3", "", null }, run, live);
            Assert.Equal(new[] { "glass_3" }, missing);
            Assert.Empty(BountyIsolation.MissingEntries(null, run, live));
            Assert.Equal(new[] { "a" }, BountyIsolation.MissingEntries(new[] { "a" }, null, null));

            Assert.True(BountyIsolation.DeferPreHit(true, OnPad("BO_Glass")));
            Assert.True(BountyIsolation.DeferPreHit(true, InLmugc));
            Assert.False(BountyIsolation.DeferPreHit(true, OnMain));
            Assert.False(BountyIsolation.DeferPreHit(false, OnPad("BO_Glass")));
        }

        // ---- The host's pending entries -----------------------------------------------------------------

        [Fact]
        public void HostPendingBounties_SurviveARebuild_AndOnlyAddFlags()
        {
            var pending = new HostPendingBounties();
            pending.Add("BO_Glass", FactCodec.BountyDone);
            pending.Add("BO_Glass", (byte)(FactCodec.BountyDone | FactCodec.BountyOnEasy));
            pending.Add("BO_Glass", FactCodec.BountyDone);
            pending.Add("", FactCodec.BountyDone);
            pending.Add("BO_Bad", 0);
            Assert.Equal(1, pending.Count);
            Assert.True(pending.TryGet("BO_Glass", out byte flags));
            Assert.Equal(FactCodec.BountyDone | FactCodec.BountyOnEasy, flags);
            Assert.False(pending.Contains(null));

            // The host rebuilds its facts from a run that does not have the entry yet: it stays the session's.
            var rebuilt = new FactTable();
            Assert.Equal(1, pending.KeepIn(rebuilt));
            Assert.Equal(new byte[] { (byte)(FactCodec.BountyDone | FactCodec.BountyOnEasy) }, rebuilt.Get(FactKinds.Bounty, "BO_Glass"));

            // Already there with the same or more flags: unchanged.
            Assert.Equal(0, pending.KeepIn(rebuilt));
            var weaker = new FactTable();
            weaker.Set(FactKinds.Bounty, "BO_Glass", FactCodec.EncodeFlags(FactCodec.BountyDone));
            Assert.Equal(1, pending.KeepIn(weaker));
            Assert.Equal(new byte[] { 3 }, weaker.Get(FactKinds.Bounty, "BO_Glass"));
            Assert.Equal(0, pending.KeepIn(null));

            Assert.Equal(new[] { "BO_Glass" }, pending.Keys());
            Assert.True(pending.Remove("BO_Glass"));
            Assert.False(pending.Remove("BO_Glass"));
            Assert.Equal(0, pending.KeepIn(new FactTable()));
        }

        // ---- Challenge props --------------------------------------------------------------------------

        [Fact]
        public void OwnBountyStart_SwitchingObjectsOff_IsHeldBack_NotReported()
        {
            double window = ChallengeProps.OwnSetChangeWindowSeconds;
            // In its own bounty, shortly after it started: held.
            Assert.True(ChallengeProps.HoldDeactivation(true, false, 0.5));
            // Long after the start, a new deactivation is real (a consumed object) unless already held.
            Assert.False(ChallengeProps.HoldDeactivation(true, false, window + 1));
            Assert.True(ChallengeProps.HoldDeactivation(true, true, window + 100));
            // On the main track: never held.
            Assert.False(ChallengeProps.HoldDeactivation(false, false, 0.5));
            // Just after the bounty ended, a held object may still be coming back.
            Assert.True(ChallengeProps.HoldDeactivation(false, true, 0.5));
            Assert.False(ChallengeProps.HoldDeactivation(false, true, window + 1));
            // No set change yet in this world.
            Assert.False(ChallengeProps.HoldDeactivation(true, false, double.PositiveInfinity));

            Assert.False(ChallengeProps.ReleaseHeld(true, window + 1));
            Assert.False(ChallengeProps.ReleaseHeld(false, 1));
            Assert.True(ChallengeProps.ReleaseHeld(false, window + 1));

            // The session says "on" while this player's own bounty keeps it off: leave it until the bounty ends.
            Assert.True(ChallengeProps.KeepLocalOff(false, false, true, true));
            Assert.False(ChallengeProps.KeepLocalOff(true, false, true, true));
            Assert.False(ChallengeProps.KeepLocalOff(false, false, true, false));
            Assert.False(ChallengeProps.KeepLocalOff(false, true, true, true));
            Assert.False(ChallengeProps.KeepLocalOff(false, false, false, true));
        }

        // ---- Locks: either player opens them; the host checks the claim -------------------------------

        private sealed class LockWorld : IClaimWorld
        {
            public bool IsReady => true;

            public bool TryGetChallengeSet(string name, out bool isBounty, out int challengeCount)
            {
                isBounty = name.StartsWith("BO_", StringComparison.Ordinal) || name == "ML_DoLMUGC_Course";
                challengeCount = 1;
                return name != "Unknown";
            }

            public bool TryGetSaveable(string id, out bool active, out IReadOnlyList<string> arbitratedIds)
            {
                active = true;
                arbitratedIds = id == "crowbar" ? new List<string> { "crowbar" } : new List<string>();
                return id != "missing";
            }
        }

        private static SaveDelta Obj(string id, bool active, bool rotated = false) =>
            new SaveDelta(FactKinds.Obj, id, FactCodec.EncodeObj(new ObjState(active, rotated,
                rotated ? new NetQuaternion(0f, 0.7071068f, 0f, 0.7071068f) : NetQuaternion.Identity)));

        [Fact]
        public void Locks_AClientsPadlockGateAndDoor_AreAccepted()
        {
            var world = new LockWorld();
            var table = new FactTable();
            // The padlock disappears, the gate and the door swing open (rotation only): anyone may do that.
            Assert.True(ClaimRules.Validate(Client, Obj("padlock_hole1", false), table, world, _ => PeerId.None, out _));
            Assert.True(ClaimRules.Validate(Client, Obj("gate_hole1", true, true), table, world, _ => PeerId.None, out _));
            Assert.True(ClaimRules.Validate(Client, Obj("door_bar", true, true), table, world, _ => PeerId.None, out _));
            Assert.False(ClaimRules.Validate(Client, Obj("missing", false), table, world, _ => PeerId.None, out _));
            // The persistent flags behind the doors (server cut, bunker, key card, crowbar) only open up.
            foreach (var field in new[] { "m_bunkerOpen", "m_cardUnlocked", "m_crowbarUnlocked", "m_bountiesRestored", SharedRunFields.ServerCut })
            {
                Assert.True(ClaimRules.Validate(Client, new SaveDelta(FactKinds.RunBool, field, FactTable.EncodeBool(true)), table, world, null, out _));
                table.Set(FactKinds.RunBool, field, FactTable.EncodeBool(true));
                bool canClose = field == SharedRunFields.ServerCut;
                Assert.Equal(canClose, ClaimRules.Validate(Client, new SaveDelta(FactKinds.RunBool, field, FactTable.EncodeBool(false)), table, world, null, out _));
            }
        }

        [Fact]
        public void Locks_TheCrowbar_IsSharedProgress_TakenByTheFirstClaimant()
        {
            double now = 10;
            var arbiter = new ClaimArbiter(() => now);
            Assert.Equal(ArbitrationKind.Pickup, ArbitrationKinds.ForInteraction("GetCrowbar"));
            // Padlocks and RFID readers are not arbitrated: either player opens them, with the shared crowbar / card.
            Assert.Equal(ArbitrationKind.None, ArbitrationKinds.ForInteraction("Padlock"));
            Assert.Equal(ArbitrationKind.None, ArbitrationKinds.ForInteraction("RFID"));
            Assert.Equal(ArbitrationKind.None, ArbitrationKinds.ForInteraction("OpenBunker"));
            Assert.Equal(ArbitrationKind.None, ArbitrationKinds.ForInteraction("CutServer"));

            Assert.True(arbiter.TryGrant(Client, 1, "crowbar", ArbitrationKind.Pickup, true, 0, 0, p => "Client", out _));
            Assert.False(arbiter.TryBeginHostAction(Host, "crowbar", ArbitrationKind.Pickup, p => "Client", out string reason));
            Assert.Contains("Client", reason);
            // Only the claimant may report it gone; nobody but the host brings it back.
            var world = new LockWorld();
            var table = new FactTable();
            Assert.True(ClaimRules.Validate(Client, Obj("crowbar", false), table, world, arbiter.TakenBy, out _));
            Assert.False(ClaimRules.Validate(new PeerId(3), Obj("crowbar", false), table, world, arbiter.TakenBy, out _));
        }

        [Fact]
        public void Bounties_TheLmugcSetCountsAsABounty_AndFlagsOnlyGrow()
        {
            var world = new LockWorld();
            var table = new FactTable();
            Assert.True(ClaimRules.Validate(Client, new SaveDelta(FactKinds.Bounty, "ML_DoLMUGC_Course", new byte[] { 1 }), table, world, null, out _));
            Assert.False(ClaimRules.Validate(Client, new SaveDelta(FactKinds.Bounty, "ML_NormalGolf", new byte[] { 1 }), table, world, null, out _));
            // The same bounty claimed again (both players finished it): a harmless no-op.
            table.Set(FactKinds.Bounty, "BO_Glass", new byte[] { 1 });
            Assert.True(ClaimRules.Validate(Client, new SaveDelta(FactKinds.Bounty, "BO_Glass", new byte[] { 1 }), table, world, null, out _));
            Assert.True(ClaimRules.Validate(Client, new SaveDelta(FactKinds.Bounty, "BO_Glass", new byte[] { 3 }), table, world, null, out _));
        }

        [Fact]
        public void Bounties_ARemoteBountyNeverMovesTheMainTrack()
        {
            // A client in a bounty never claims the track (its diff skips it); a claim naming a bounty set as the
            // main track is refused, and a peer in a bounty defers the track it is given.
            var world = new LockWorld();
            var table = new FactTable();
            table.Set(FactKinds.Track, FactKinds.TrackKey, FactCodec.EncodeTrack(new TrackValue("ML_NormalGolf", 2)));
            Assert.False(ClaimRules.Validate(Client,
                new SaveDelta(FactKinds.Track, FactKinds.TrackKey, FactCodec.EncodeTrack(new TrackValue("BO_Glass", 0))),
                table, world, null, out _));
            Assert.Equal(TrackAction.Defer, TrackPlanner.Decide(new TrackValue("ML_NormalGolf", 3), true,
                new TrackValue("ML_NormalGolf", 2), false));
        }
    }
}
