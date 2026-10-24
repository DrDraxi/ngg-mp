using NGGMultiplayer.Game.Presence;
using NGGMultiplayer.Game.Progression;
using Xunit;

namespace NGGMultiplayer.Tests.GameProgression
{
    public class CutsceneRulesTests
    {
        // ---- LocalBusyGate --------------------------------------------------------------------------

        [Fact]
        public void Gate_FreePlayer_NeverHolds()
        {
            var gate = new LocalBusyGate();
            Assert.False(gate.Update(10, false, false));
            Assert.False(gate.Update(11, false, false));
            Assert.Equal(BusyReason.None, gate.Reason);
        }

        [Fact]
        public void Gate_HoldsDuringCutscene_ThenSettles()
        {
            var gate = new LocalBusyGate();
            Assert.True(gate.Update(10, true, false));
            Assert.Equal(BusyReason.Cutscene, gate.Reason);
            Assert.True(gate.Update(40, true, false));
            // The dialogue ended: its invokeEnd methods and save run on the next frames.
            Assert.True(gate.Update(40.5, false, false));
            Assert.True(gate.Update(40 + LocalBusyGate.SettleSeconds - 0.01, false, false));
            Assert.False(gate.Update(40 + LocalBusyGate.SettleSeconds + 0.01, false, false));
            Assert.Equal(BusyReason.None, gate.Reason);
        }

        [Fact]
        public void Gate_HoldsDuringShot()
        {
            var gate = new LocalBusyGate();
            Assert.True(gate.Update(5, false, true));
            Assert.Equal(BusyReason.Shot, gate.Reason);
            Assert.True(gate.Update(9, false, true));
            Assert.True(gate.Update(9.2, false, false));
            Assert.False(gate.Update(9 + LocalBusyGate.SettleSeconds + 0.1, false, false));
        }

        [Fact]
        public void Gate_CutsceneWins_OverShot()
        {
            var gate = new LocalBusyGate();
            Assert.True(gate.Update(1, true, true));
            Assert.Equal(BusyReason.Cutscene, gate.Reason);
        }

        [Fact]
        public void Gate_StuckCutscene_StopsHoldingAfterBound()
        {
            var gate = new LocalBusyGate();
            Assert.True(gate.Update(100, true, false));
            Assert.True(gate.Update(100 + LocalBusyGate.MaxCutsceneHoldSeconds - 1, true, false));
            Assert.False(gate.Expired);
            Assert.False(gate.Update(100 + LocalBusyGate.MaxCutsceneHoldSeconds + 1, true, false));
            Assert.True(gate.Expired);
            // Still locked: no settle either.
            Assert.False(gate.Update(100 + LocalBusyGate.MaxCutsceneHoldSeconds + 2, true, false));
        }

        [Fact]
        public void Gate_StuckShot_StopsHoldingAfterBound_ButANewCutsceneHolds()
        {
            var gate = new LocalBusyGate();
            Assert.True(gate.Update(0, false, true));
            Assert.False(gate.Update(LocalBusyGate.MaxShotHoldSeconds + 0.5, false, true));
            Assert.True(gate.Expired);
            Assert.True(gate.Update(LocalBusyGate.MaxShotHoldSeconds + 1, true, true));
            Assert.Equal(BusyReason.Cutscene, gate.Reason);
        }

        [Fact]
        public void Gate_NewBusyPeriod_RestartsTheBound()
        {
            var gate = new LocalBusyGate();
            gate.Update(0, true, false);
            gate.Update(100, true, false);
            gate.Update(101, false, false);
            Assert.False(gate.Update(110, false, false));
            // A second dialogue later gets the full bound again.
            Assert.True(gate.Update(200, true, false));
            Assert.True(gate.Update(200 + LocalBusyGate.MaxCutsceneHoldSeconds - 1, true, false));
        }

        [Fact]
        public void Gate_Clear_DropsSettle()
        {
            var gate = new LocalBusyGate();
            gate.Update(0, true, false);
            gate.Clear();
            Assert.False(gate.Update(0.1, false, false));
        }

        // ---- StoryBeatQueue -------------------------------------------------------------------------

        [Fact]
        public void Beats_KeepOrder_AndDeduplicate_ByLatestArrival()
        {
            var queue = new StoryBeatQueue();
            Assert.True(queue.Add("OpenShop", 2));
            Assert.True(queue.Add("UnlockBounties1", 3));
            // A repeat is not new, but it moves to the end: it was sent last.
            Assert.False(queue.Add("OpenShop", 3));
            Assert.False(queue.Add("", 1));
            Assert.False(queue.Add(null, 1));
            var all = queue.TakeAll();
            Assert.Equal(2, all.Count);
            Assert.Equal("UnlockBounties1", all[0].Key);
            Assert.Equal("OpenShop", all[1].Key);
            Assert.Equal(3ul, all[1].Value);
            Assert.Equal(0, queue.Count);
            Assert.Empty(queue.TakeAll());
        }

        [Fact]
        public void Beats_LastOfConflictingBeatsRunsLast()
        {
            var queue = new StoryBeatQueue();
            queue.Add("OpenGates", 1);
            queue.Add("CloseGates", 1);
            queue.Add("OpenGates", 1);
            var all = queue.TakeAll();
            Assert.Equal(2, all.Count);
            Assert.Equal("CloseGates", all[0].Key);
            Assert.Equal("OpenGates", all[1].Key);
        }

        [Fact]
        public void Beats_RepeatMovesEvenWhenFull()
        {
            var queue = new StoryBeatQueue();
            for (int i = 0; i < StoryBeatQueue.Capacity; i++)
                queue.Add("M" + i, 1);
            Assert.False(queue.Add("M0", 2));
            var all = queue.TakeAll();
            Assert.Equal(StoryBeatQueue.Capacity, all.Count);
            Assert.Equal("M0", all[all.Count - 1].Key);
            Assert.Equal("M1", all[0].Key);
        }

        [Fact]
        public void Beats_AreBounded()
        {
            var queue = new StoryBeatQueue();
            for (int i = 0; i < StoryBeatQueue.Capacity; i++)
                Assert.True(queue.Add("M" + i, 1));
            Assert.False(queue.Add("Extra", 1));
            Assert.Equal(StoryBeatQueue.Capacity, queue.Count);
            queue.Clear();
            Assert.Equal(0, queue.Count);
        }

        // ---- ReplayCallMarks ------------------------------------------------------------------------

        [Fact]
        public void Marks_AreConsumedOnce_WithinTheirWindow()
        {
            var marks = new ReplayCallMarks();
            marks.Mark("TeleportMD", 10, 1.5);
            Assert.True(marks.IsMarked("TeleportMD", 11));
            Assert.True(marks.TryConsume("TeleportMD", 11));
            Assert.False(marks.TryConsume("TeleportMD", 11));
            Assert.False(marks.IsMarked("TeleportMD", 11));
        }

        [Fact]
        public void Marks_Expire()
        {
            var marks = new ReplayCallMarks();
            marks.Mark("Luke4", 10, 1.5);
            Assert.False(marks.TryConsume("Luke4", 12));
            // The stale mark is gone: the player's own later call is theirs.
            Assert.False(marks.TryConsume("Luke4", 12.1));
        }

        [Fact]
        public void Marks_OfOneMethod_Count_EachCallUsesOne()
        {
            var marks = new ReplayCallMarks();
            // A catch-up of two challenges that both name TeleportMD: both Invokes are the replay's.
            marks.Mark("TeleportMD", 10, 1.5);
            marks.Mark("TeleportMD", 10, 1.5);
            Assert.Equal(2, marks.Count);
            Assert.True(marks.TryConsume("TeleportMD", 10.1));
            Assert.True(marks.IsMarked("TeleportMD", 10.1));
            Assert.True(marks.TryConsume("TeleportMD", 10.1));
            Assert.False(marks.TryConsume("TeleportMD", 10.2));
            Assert.Equal(0, marks.Count);
        }

        [Fact]
        public void Marks_OfOneMethod_UseTheOneRunningOutFirst()
        {
            var marks = new ReplayCallMarks();
            marks.Mark("OpenGates", 0, 20);   // a delayed functionOnStart
            marks.Mark("OpenGates", 0, 1.5);  // a functionOnComplete Invoked next frame
            Assert.True(marks.TryConsume("OpenGates", 0.1));
            // The delayed one is still there for its own call.
            Assert.True(marks.IsMarked("OpenGates", 19));
            Assert.True(marks.TryConsume("OpenGates", 19));
            Assert.False(marks.IsMarked("OpenGates", 19));
        }

        [Fact]
        public void Marks_ExpiredOnesOfAMethodAreDroppedOnConsume()
        {
            var marks = new ReplayCallMarks();
            marks.Mark("Luke4", 0, 1);
            marks.Mark("Luke4", 0, 10);
            Assert.True(marks.TryConsume("Luke4", 5));
            Assert.Equal(0, marks.Count);
            Assert.False(marks.TryConsume("Luke4", 5));
        }

        [Fact]
        public void Marks_IgnoreEmptyNamesAndWindows_AndKeepTheLongerWindow()
        {
            var marks = new ReplayCallMarks();
            marks.Mark(null, 0, 1);
            marks.Mark("", 0, 1);
            marks.Mark("A", 0, 0);
            Assert.Equal(0, marks.Count);
            marks.Mark("A", 0, 5);
            marks.Mark("A", 1, 1);
            Assert.True(marks.IsMarked("A", 4.5));
        }

        [Fact]
        public void Marks_AreBounded_AndPruneStaleOnes()
        {
            var marks = new ReplayCallMarks();
            for (int i = 0; i < ReplayCallMarks.Capacity; i++)
                marks.Mark("M" + i, 0, 1);
            Assert.Equal(ReplayCallMarks.Capacity, marks.Count);
            marks.Mark("Fresh", 0.5, 1);
            Assert.False(marks.IsMarked("Fresh", 0.5));
            // Later the stale ones make room.
            marks.Mark("Fresh", 5, 1);
            Assert.True(marks.IsMarked("Fresh", 5.5));
            Assert.Equal(1, marks.Count);
        }

        // ---- CutsceneRules --------------------------------------------------------------------------

        [Theory]
        [InlineData("TeleportMD")]
        [InlineData("TeleportCarpark")]
        [InlineData("TeleportMDTrap")]
        [InlineData("StartTeleporterDialogue")]
        [InlineData("PlayCarDialogue2")]
        [InlineData("Luke1")]
        [InlineData("Luke16")]
        [InlineData("EndLuke0")]
        [InlineData("ReturnLuke1a")]
        [InlineData("LookAtTee")]
        public void SkipWhenReplayed_PlayerMoversAndClips(string method)
        {
            Assert.True(CutsceneRules.SkipWhenReplayed(method));
        }

        [Theory]
        [InlineData("OpenShop")]
        [InlineData("UnlockBounties2")]
        [InlineData("EnableGreenTeeShort")]
        [InlineData("ShowPowerRequirement")]
        [InlineData("HidePowerRequirement")]
        [InlineData("ShowNullRef")]
        [InlineData("EndLukeFadeDown")]
        [InlineData("DeductMoney")]
        [InlineData("UnlockWind")]
        [InlineData("LukeSaysHi")]
        [InlineData("")]
        [InlineData(null)]
        public void SkipWhenReplayed_WorldStateRuns(string method)
        {
            Assert.False(CutsceneRules.SkipWhenReplayed(method));
        }

        [Fact]
        public void PlayerMovers_AreNotSentByName()
        {
            // The player's own calls never travel as story beats, so only a track replay can make another peer run them.
            foreach (var name in CutsceneRules.PlayerMovers)
                Assert.NotEqual(StoryClass.Replicate, StoryMethods.Classify(name));
        }

        [Fact]
        public void StartWindow_CoversTheDelay_Bounded()
        {
            Assert.Equal(CutsceneRules.InvokeWindowSeconds, CutsceneRules.StartWindow(0f), 3);
            Assert.Equal(CutsceneRules.InvokeWindowSeconds + 2, CutsceneRules.StartWindow(2f), 3);
            Assert.Equal(CutsceneRules.InvokeWindowSeconds, CutsceneRules.StartWindow(-1f), 3);
            Assert.Equal(CutsceneRules.InvokeWindowSeconds, CutsceneRules.StartWindow(float.NaN), 3);
            Assert.Equal(CutsceneRules.InvokeWindowSeconds + CutsceneRules.MaxStartDelaySeconds, CutsceneRules.StartWindow(1000f), 3);
        }

        [Theory]
        [InlineData(true, true, false, true)]
        [InlineData(true, false, false, false)]
        [InlineData(false, true, false, false)]
        [InlineData(false, false, true, true)]
        [InlineData(true, false, true, true)]
        public void IsReplayCaused(bool inSession, bool scope, bool inReplayedFunction, bool expected)
        {
            Assert.Equal(expected, CutsceneRules.IsReplayCaused(inSession, scope, inReplayedFunction));
        }

        [Fact]
        public void NeverSkipped_OnlyAnUnlock()
        {
            Assert.True(CutsceneRules.NeverSkipped("LockPlayer", new object[] { false }));
            Assert.False(CutsceneRules.NeverSkipped("LockPlayer", new object[] { true }));
            Assert.False(CutsceneRules.NeverSkipped("LockPlayer", null));
            Assert.False(CutsceneRules.NeverSkipped("LockPlayer", new object[0]));
            Assert.False(CutsceneRules.NeverSkipped("StartDialogue", new object[] { false }));
            Assert.False(CutsceneRules.NeverSkipped(null, new object[] { false }));
        }

        [Fact]
        public void CutsceneActivity_FitsTheInfoMessage()
        {
            Assert.False(string.IsNullOrEmpty(PresenceText.CutsceneActivity));
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(PresenceText.CutsceneActivity) <= PresenceWire.MaxActivityBytes);
        }
    }
}
