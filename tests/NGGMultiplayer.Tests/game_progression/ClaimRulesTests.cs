using System;
using System.Collections.Generic;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Save;
using NGGMultiplayer.Game;
using NGGMultiplayer.Game.Progression;
using Xunit;

namespace NGGMultiplayer.Tests.GameProgression
{
    public class ClaimRulesTests
    {
        private static readonly PeerId Client = new PeerId(2);
        private static readonly PeerId Other = new PeerId(3);

        private sealed class FakeWorld : IClaimWorld
        {
            public bool Ready = true;
            public readonly Dictionary<string, (bool bounty, int count)> Sets = new Dictionary<string, (bool, int)>();
            public readonly Dictionary<string, (bool active, List<string> ids)> Saveables =
                new Dictionary<string, (bool, List<string>)>();

            public bool IsReady => Ready;

            public bool TryGetChallengeSet(string name, out bool isBounty, out int challengeCount)
            {
                if (Sets.TryGetValue(name, out var set))
                {
                    isBounty = set.bounty;
                    challengeCount = set.count;
                    return true;
                }
                isBounty = false;
                challengeCount = 0;
                return false;
            }

            public bool TryGetSaveable(string id, out bool active, out IReadOnlyList<string> arbitratedIds)
            {
                if (Saveables.TryGetValue(id, out var s))
                {
                    active = s.active;
                    arbitratedIds = s.ids;
                    return true;
                }
                active = false;
                arbitratedIds = null;
                return false;
            }
        }

        private static FakeWorld World()
        {
            var world = new FakeWorld();
            world.Sets["Main1"] = (false, 5);
            world.Sets["Main2"] = (false, 4);
            world.Sets["BO_Glass"] = (true, 1);
            world.Saveables["Cash_1"] = (true, new List<string> { "Cash_1/Pickup" });
            world.Saveables["Door_1"] = (true, new List<string>());
            return world;
        }

        private static bool Check(SaveDelta claim, FactTable table, FakeWorld world) =>
            Check(claim, table, world, _ => PeerId.None);

        private static bool Check(SaveDelta claim, FactTable table, FakeWorld world, Func<string, PeerId> takenBy) =>
            ClaimRules.Validate(Client, claim, table, world, takenBy, out _);

        private static SaveDelta Track(string set, int index) =>
            new SaveDelta(FactKinds.Track, FactKinds.TrackKey, FactCodec.EncodeTrack(new TrackValue(set, index)));

        [Fact]
        public void RefusesMalformedUnknownAndHostOnly()
        {
            var table = new FactTable();
            var world = World();
            Assert.False(Check(null, table, world));
            Assert.False(Check(new SaveDelta("nope", "k", new byte[] { 1 }), table, world));
            Assert.False(Check(new SaveDelta(FactKinds.Bank, "", FactTable.EncodeInt(1000)), table, world));
            Assert.False(Check(new SaveDelta(FactKinds.Seed, "", FactTable.EncodeInt(1)), table, world));
            Assert.False(Check(new SaveDelta(FactKinds.RunBool, "m_notAField", FactTable.EncodeBool(true)), table, world));
            Assert.False(Check(new SaveDelta(FactKinds.RunInt, SharedRunFields.BountyAccessLevel, FactTable.EncodeInt(4)), table, world));
            Assert.False(ClaimRules.Validate(Client, new SaveDelta(FactKinds.Track, "main", new byte[] { 9 }), table, world, null, out string reason));
            Assert.NotNull(reason);
        }

        [Fact]
        public void EquivalentValue_IsANoOp_EvenWhenNotReady()
        {
            var table = new FactTable();
            table.Set(FactKinds.Bounty, "BO_Glass", new byte[] { 1 });
            var world = World();
            world.Ready = false;
            Assert.True(Check(new SaveDelta(FactKinds.Bounty, "BO_Glass", new byte[] { 1 }), table, world));
            Assert.False(Check(new SaveDelta(FactKinds.Bounty, "BO_Glass", new byte[] { 3 }), table, world));
        }

        [Fact]
        public void Track_StepsOneAtATime()
        {
            var table = new FactTable();
            var world = World();
            Assert.True(Check(Track("Main1", 0), table, world));
            table.Set(FactKinds.Track, FactKinds.TrackKey, FactCodec.EncodeTrack(new TrackValue("Main1", 2)));
            Assert.True(Check(Track("Main1", 3), table, world));
            // A client that completed quick challenges before its diff ran claims a few steps at once.
            Assert.True(Check(Track("Main1", 4), table, world));
            Assert.True(Check(Track("Main1", 2 + ClaimRules.MaxTrackSteps), table, world));
            table.Set(FactKinds.Track, FactKinds.TrackKey, FactCodec.EncodeTrack(new TrackValue("Main1", 1)));
            Assert.False(Check(Track("Main1", 1 + ClaimRules.MaxTrackSteps + 1), table, world));
            table.Set(FactKinds.Track, FactKinds.TrackKey, FactCodec.EncodeTrack(new TrackValue("Main1", 2)));
            Assert.False(Check(Track("Main1", 1), table, world));
            Assert.False(Check(Track("Main1", 9), table, world));
            Assert.False(Check(Track("BO_Glass", 0), table, world));
            Assert.False(Check(Track("Unknown", 0), table, world));
        }

        [Fact]
        public void Track_SetChange_OnlyFromTheEnd()
        {
            var table = new FactTable();
            var world = World();
            table.Set(FactKinds.Track, FactKinds.TrackKey, FactCodec.EncodeTrack(new TrackValue("Main1", 2)));
            Assert.False(Check(Track("Main2", 0), table, world));
            table.Set(FactKinds.Track, FactKinds.TrackKey, FactCodec.EncodeTrack(new TrackValue("Main1", 4)));
            Assert.True(Check(Track("Main2", 0), table, world));
            Assert.True(Check(Track("Main2", 1), table, world));
            Assert.False(Check(Track("Main2", 2), table, world));
        }

        [Fact]
        public void Bounty_MustBeABounty_AndFlagsOnlyGrow()
        {
            var table = new FactTable();
            var world = World();
            Assert.False(Check(new SaveDelta(FactKinds.Bounty, "Main1", new byte[] { 1 }), table, world));
            Assert.True(Check(new SaveDelta(FactKinds.Bounty, "BO_Glass", new byte[] { 3 }), table, world));
            table.Set(FactKinds.Bounty, "BO_Glass", new byte[] { 3 });
            Assert.False(Check(new SaveDelta(FactKinds.Bounty, "BO_Glass", new byte[] { 1 }), table, world));
        }

        [Fact]
        public void RunFields_OnlyGrow_ExceptServerCut()
        {
            var table = new FactTable();
            var world = World();
            table.Set(FactKinds.RunBool, "m_spinUnlocked", FactTable.EncodeBool(true));
            table.Set(FactKinds.RunBool, SharedRunFields.ServerCut, FactTable.EncodeBool(true));
            table.Set(FactKinds.RunInt, SharedRunFields.BountyAccessLevel, FactTable.EncodeInt(2));
            Assert.False(Check(new SaveDelta(FactKinds.RunBool, "m_spinUnlocked", FactTable.EncodeBool(false)), table, world));
            Assert.True(Check(new SaveDelta(FactKinds.RunBool, SharedRunFields.ServerCut, FactTable.EncodeBool(false)), table, world));
            Assert.True(Check(new SaveDelta(FactKinds.RunBool, "m_windUnlocked", FactTable.EncodeBool(true)), table, world));
            Assert.False(Check(new SaveDelta(FactKinds.RunInt, SharedRunFields.BountyAccessLevel, FactTable.EncodeInt(1)), table, world));
            Assert.True(Check(new SaveDelta(FactKinds.RunInt, SharedRunFields.BountyAccessLevel, FactTable.EncodeInt(3)), table, world));
        }

        [Fact]
        public void Obj_ArbitratedRemoval_NeedsTheGrant()
        {
            var table = new FactTable();
            var world = World();
            var gone = new SaveDelta(FactKinds.Obj, "Cash_1", FactCodec.EncodeObj(new ObjState(false, false, default)));
            Assert.False(Check(gone, table, world));
            Assert.False(Check(gone, table, world, id => Other));
            Assert.True(Check(gone, table, world, id => id == "Cash_1/Pickup" ? Client : PeerId.None));

            world.Saveables["Cash_1"] = (false, new List<string> { "Cash_1/Pickup" });
            var back = new SaveDelta(FactKinds.Obj, "Cash_1", FactCodec.EncodeObj(new ObjState(true, false, default)));
            Assert.False(Check(back, table, world, id => Client));

            var door = new SaveDelta(FactKinds.Obj, "Door_1", FactCodec.EncodeObj(new ObjState(false, false, default)));
            Assert.True(Check(door, table, world));
            Assert.False(Check(new SaveDelta(FactKinds.Obj, "Nope", door.Value), table, world));
        }

        [Fact]
        public void PreHit_MustAddSomething()
        {
            var table = new FactTable();
            var world = World();
            table.Set(FactKinds.PreHit, "", FactCodec.EncodeStringSet(new[] { "a", "b" }));
            Assert.False(Check(new SaveDelta(FactKinds.PreHit, "", FactCodec.EncodeStringSet(new[] { "a" })), table, world));
            Assert.True(Check(new SaveDelta(FactKinds.PreHit, "", FactCodec.EncodeStringSet(new[] { "a", "c" })), table, world));
        }

        [Fact]
        public void DnrAndEasy_NeedAKnownSet()
        {
            var table = new FactTable();
            var world = World();
            Assert.True(Check(new SaveDelta(FactKinds.DoNotReload, "BO_Glass", new byte[] { 1 }), table, world));
            Assert.False(Check(new SaveDelta(FactKinds.Easy, "Nope", new byte[] { 1 }), table, world));
            Assert.False(Check(new SaveDelta(FactKinds.Easy, "BO_Glass", new byte[] { 2 }), table, world));
        }

        [Fact]
        public void NotReady_RefusesNewProgress_AsRetryable()
        {
            var world = World();
            world.Ready = false;
            Assert.False(ClaimRules.Validate(Client, Track("Main1", 0), new FactTable(), world, null, out string reason));
            Assert.True(ClaimRules.IsRetryable(reason));
            world.Ready = true;
            var table = new FactTable();
            table.Set(FactKinds.Track, FactKinds.TrackKey, FactCodec.EncodeTrack(new TrackValue("Main1", 2)));
            Assert.False(ClaimRules.Validate(Client, Track("Main1", 1), table, world, null, out reason));
            Assert.False(ClaimRules.IsRetryable(reason));
        }

        [Fact]
        public void PendingClaims_KeepTheNewestPerFact_BoundedAndExpiring()
        {
            double now = 0;
            var queue = new PendingClaimQueue(() => now);
            Assert.True(queue.Add(Client, new SaveDelta(FactKinds.Bounty, "BO_Glass", new byte[] { 1 })));
            Assert.True(queue.Add(Client, Track("Main1", 1)));
            Assert.True(queue.Add(Client, Track("Main1", 2)));
            Assert.True(queue.Add(Other, Track("Main1", 1)));
            Assert.Equal(3, queue.Count);
            var all = queue.TakeAll();
            Assert.Equal(0, queue.Count);
            Assert.Equal(3, all.Count);
            Assert.Equal(FactKinds.Bounty, all[0].Value.Kind);
            Assert.Equal(Client, all[1].Key);
            Assert.Equal(Other, all[2].Key);
            FactCodec.TryDecodeTrack(all[1].Value.Value, out var kept);
            Assert.Equal(2, kept.Index);

            for (int i = 0; i < PendingClaimQueue.MaxPerPeer; i++)
                Assert.True(queue.Add(Client, new SaveDelta(FactKinds.DoNotReload, "S" + i, new byte[] { 1 })));
            Assert.False(queue.Add(Client, new SaveDelta(FactKinds.DoNotReload, "more", new byte[] { 1 })));
            Assert.True(queue.Add(Other, new SaveDelta(FactKinds.DoNotReload, "more", new byte[] { 1 })));
            now += PendingClaimQueue.MaxAgeSeconds + 1;
            Assert.Empty(queue.TakeAll());
        }

        [Theory]
        // A bounty prop the client switched on itself: kept while off the main track.
        [InlineData(false, true, false, false, false, true)]
        [InlineData(false, true, false, true, true, true)]
        // On the main track, or the host wants it on, or it is not on: the session's value applies.
        [InlineData(true, true, false, true, false, false)]
        [InlineData(false, true, true, true, false, false)]
        [InlineData(false, false, false, true, false, false)]
        // Active in the baseline (reported or loaded that way) and not seen switched on in a challenge.
        [InlineData(false, true, false, false, true, false)]
        public void ChallengeProps_AreNotSwitchedOffMidChallenge(bool onMain, bool live, bool wanted, bool known,
            bool baselineActive, bool keep)
        {
            Assert.Equal(keep, ChallengeProps.KeepLocal(onMain, live, wanted, known, baselineActive));
        }

        [Fact]
        public void ChallengeProps_WithoutBaseline_FollowTheSession()
        {
            Assert.False(ChallengeProps.KeepLocal(false, true, false, false, null));
        }

        [Theory]
        // Live ahead of the session with no claim in flight: the host refused it; roll back.
        [InlineData(true, "Main1", 2, "Main1", 4, false, TrackAction.Load)]
        [InlineData(true, "Main1", 2, "Main1", 4, true, TrackAction.None)]
        [InlineData(false, "Main1", 2, "Main1", 4, false, TrackAction.None)]
        [InlineData(true, "Main1", 2, "Main1", 2, false, TrackAction.None)]
        [InlineData(true, "Main1", 3, "Main1", 2, false, TrackAction.Complete)]
        [InlineData(false, "Main1", 3, "Main1", 2, false, TrackAction.Defer)]
        public void TrackPlanner_ReconcileRollsBackRefusedProgress(bool onMain, string factSet, int factIndex,
            string localSet, int localIndex, bool inFlight, TrackAction expected)
        {
            var action = TrackPlanner.DecideReconcile(new TrackValue(factSet, factIndex), true,
                new TrackValue(localSet, localIndex), onMain, inFlight);
            Assert.Equal(expected, action);
        }

        [Fact]
        public void TrackPlanner_PendingHostTrack_NeverUndoesLaterProgress()
        {
            Assert.True(TrackPlanner.PendingStillAhead(new TrackValue("Main1", 4), true, new TrackValue("Main1", 2), 5));
            Assert.False(TrackPlanner.PendingStillAhead(new TrackValue("Main1", 4), true, new TrackValue("Main1", 4), 5));
            // The clients moved on to the next set while the host waited at the end of the old one.
            Assert.True(TrackPlanner.PendingStillAhead(new TrackValue("Main2", 0), true, new TrackValue("Main1", 4), 5));
            // The host went on into the next set by itself: an older pending value from the first set is stale.
            Assert.False(TrackPlanner.PendingStillAhead(new TrackValue("Main1", 4), true, new TrackValue("Main2", 1), 4));
            Assert.True(TrackPlanner.PendingStillAhead(new TrackValue("Main1", 4), false, default, 0));
        }

        [Theory]
        [InlineData(true, "Main1", 2, true, "Main1", 2, TrackAction.None)]
        [InlineData(true, "Main1", 3, true, "Main1", 2, TrackAction.Complete)]
        [InlineData(true, "Main1", 4, true, "Main1", 2, TrackAction.Load)]
        [InlineData(true, "Main2", 0, true, "Main1", 4, TrackAction.Load)]
        [InlineData(false, "Main1", 3, true, "Main1", 2, TrackAction.Defer)]
        [InlineData(false, "Main1", 1, true, "Main1", 2, TrackAction.None)]
        [InlineData(true, "Main1", 0, false, "", 0, TrackAction.Load)]
        public void TrackPlanner_Decides(bool onMain, string factSet, int factIndex, bool hasLocal, string localSet,
            int localIndex, TrackAction expected)
        {
            var action = TrackPlanner.Decide(new TrackValue(factSet, factIndex), hasLocal, new TrackValue(localSet, localIndex), onMain);
            Assert.Equal(expected, action);
        }

        [Fact]
        public void MoneyGate_DeductOnceThenReturnOnce()
        {
            byte state = MoneyGate.None;
            Assert.True(MoneyGate.CanRun(StoryMethods.DeductMoney, state));
            state = MoneyGate.After(StoryMethods.DeductMoney, state);
            Assert.False(MoneyGate.CanRun(StoryMethods.DeductMoney, state));
            Assert.True(MoneyGate.CanRun(StoryMethods.ReturnMoney, state));
            state = MoneyGate.After(StoryMethods.ReturnMoney, state);
            Assert.Equal(MoneyGate.Returned, state);
            Assert.False(MoneyGate.CanRun(StoryMethods.ReturnMoney, state));
            Assert.False(MoneyGate.CanRun("OpenShop", state));
        }

        [Fact]
        public void MoneyGate_ReturnsADeductionMadeBeforeTheSession()
        {
            // Regression: the gate starts at None every session, so a deduction from solo play was never returned.
            byte state = MoneyGate.None;
            Assert.True(MoneyGate.CanRun(StoryMethods.ReturnMoney, state));
            state = MoneyGate.After(StoryMethods.ReturnMoney, state);
            Assert.False(MoneyGate.CanRun(StoryMethods.ReturnMoney, state));
        }

        [Fact]
        public void StoryMethods_Classify()
        {
            Assert.Equal(StoryClass.Replicate, StoryMethods.Classify("OpenShop"));
            // Luke's clips are presentation only (answers 38, 96).
            Assert.Equal(StoryClass.Presentation, StoryMethods.Classify("Luke16"));
            Assert.Equal(StoryClass.Presentation, StoryMethods.Classify("ReturnLuke3"));
            Assert.Equal(StoryClass.Presentation, StoryMethods.Classify("ReturnLuke1a"));
            Assert.Equal(StoryClass.Presentation, StoryMethods.Classify("EndLuke0"));
            Assert.Equal(StoryClass.Local, StoryMethods.Classify("ReturnLuke"));
            Assert.Equal(StoryClass.HostExecuted, StoryMethods.Classify(StoryMethods.DeductMoney));
            Assert.Equal(StoryClass.HostExecuted, StoryMethods.Classify(StoryMethods.ReturnMoney));
            Assert.Equal(StoryClass.HostOnlyEnding, StoryMethods.Classify(StoryMethods.TryTrueEnding));
            Assert.Equal(StoryClass.Local, StoryMethods.Classify("SomethingElse"));
            Assert.Equal(StoryClass.Local, StoryMethods.Classify(null));
        }

        [Fact]
        public void StoryMethods_FollowTheDecompiledCatalog()
        {
            // Answers 96: shared state that is safe to run again.
            foreach (var name in new[]
            {
                "HideCar", "CloseGates", "OpenGates", "UnlockStance", "UnlockPractice", "UnlockSpin", "UnlockAdjust",
                "CompletedDemo", "OpenShop", "OpenHole1Gates", "OpenHole2Gates", "UnlockBounties1", "UnlockBounties2",
                "UnlockBounties3", "DriverAvailable", "WedgeAvailable", "HybridAvailable", "TicketAvailable",
                "UnlockLMUGC", "TicketReady", "EnableTrap", "OpenBarDoor", "EnableTopOfMountain", "OpenMarketingDept",
                "RestoreBounties"
            })
                Assert.Equal(StoryClass.Replicate, StoryMethods.Classify(name));
            // Not idempotent: StartWind starts another wind cycle; UnlockTeleporter loads a set and adds a bounty.
            Assert.Equal(StoryClass.FactDriven, StoryMethods.Classify(StoryMethods.UnlockWind));
            Assert.Equal(StoryClass.FactDriven, StoryMethods.Classify(StoryMethods.UnlockTeleporter));
            // The true ending, before the EndLuke* prefix.
            Assert.Equal(StoryClass.HostOnlyEnding, StoryMethods.Classify(StoryMethods.EndLukeFadeDown));
            foreach (var name in new[] { "LookAtLuke", "ShowNullRef", "ShowConfettiNullRef", "ShowPowerRequirement", "HidePowerRequirement" })
                Assert.Equal(StoryClass.Presentation, StoryMethods.Classify(name));
            // The player's own: teleports, tees, achievements.
            foreach (var name in StoryMethods.PlayerLocal)
                Assert.Equal(StoryClass.Local, StoryMethods.Classify(name));
            Assert.Equal(StoryClass.Local, StoryMethods.Classify("AchievementPadlock"));
            Assert.Equal(StoryClass.Local, StoryMethods.Classify("LukeX"));
        }

        [Fact]
        public void RepeatRules_OnlyANetworkReplayOfSomethingThatRanIsSkipped()
        {
            // Answers 95: StartWind and the shop stock are not idempotent.
            Assert.True(RepeatRules.SkipRepeat(inSession: true, replaying: true, ranInThisWorld: true));
            Assert.False(RepeatRules.SkipRepeat(inSession: true, replaying: true, ranInThisWorld: false));
            Assert.False(RepeatRules.SkipRepeat(inSession: true, replaying: false, ranInThisWorld: true));
            Assert.False(RepeatRules.SkipRepeat(inSession: false, replaying: true, ranInThisWorld: true));
            Assert.Contains("SetDriverAvailable", RepeatRules.ShopStock);
            Assert.Contains("EnableLMUGCMachine", RepeatRules.ShopStock);
        }

        [Fact]
        public void TrackCatchUp_Rules()
        {
            // LoadNewChallengeSet clamps the skip to the last challenge (answers 39).
            Assert.Equal(0, TrackPlanner.LoadIndex(0, 5));
            Assert.Equal(3, TrackPlanner.LoadIndex(3, 5));
            Assert.Equal(4, TrackPlanner.LoadIndex(5, 5));
            Assert.Equal(0, TrackPlanner.LoadIndex(-1, 5));
            Assert.Equal(0, TrackPlanner.LoadIndex(2, 0));
            // A finished set then completes its last challenge.
            Assert.True(TrackPlanner.CompleteAfterLoad(5, 5));
            Assert.False(TrackPlanner.CompleteAfterLoad(4, 5));
            Assert.False(TrackPlanner.CompleteAfterLoad(0, 0));
            // CompleteChallenge returns early for a short streak (answers 86).
            Assert.True(TrackPlanner.StreakGateHolds(3, 1));
            Assert.False(TrackPlanner.StreakGateHolds(3, 3));
            Assert.False(TrackPlanner.StreakGateHolds(1, 0));
            Assert.False(TrackPlanner.StreakGateHolds(0, 0));
        }

        [Fact]
        public void Catalog_IsConsistent()
        {
            foreach (var field in SharedRunFields.Bools)
                Assert.False(SharedRunFields.IsSharedInt(field));
            Assert.True(FactKinds.IsHostOnly(FactKinds.Money));
            Assert.False(FactKinds.IsHostOnly(FactKinds.Track));
            Assert.True(WorldOps.IsKnown((byte)WorldOp.LmugcMachine));
            Assert.False(WorldOps.IsKnown(0));
            Assert.False(WorldOps.IsKnown(12));
            Assert.StartsWith(ProgressionRpc.Prefix, ProgressionRpc.Digest);
        }
    }
}
