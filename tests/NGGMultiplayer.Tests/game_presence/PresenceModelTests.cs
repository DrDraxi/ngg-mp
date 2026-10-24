// See PresenceWireTests.cs: compiled only with the Presence model sources linked (PRESENCE_MODEL_LINKED).
#if PRESENCE_MODEL_LINKED
using System.Linq;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Game.Presence;
using Xunit;

namespace NGGMultiplayer.Tests.GamePresence
{
    public class ScorecardTests
    {
        private static readonly PeerId Alice = new PeerId(1);
        private static readonly PeerId Bob = new PeerId(2);

        [Fact]
        public void StartHoleFinish_BuildsARound()
        {
            var card = new Scorecard();
            Assert.True(card.ApplyRound(Alice, 10, RoundEvent.Start, 0));
            Assert.True(card.ApplyHole(Alice, 10, 1, 4, 4));
            Assert.True(card.ApplyHole(Alice, 10, 2, 2, 3));
            var record = card.Get(Alice);
            Assert.True(record.InProgress);
            Assert.Equal(3, record.CurrentHole);
            Assert.Equal(6, record.PlayedTotal);
            Assert.Equal(7, card.ParForPlayed(record));
            Assert.True(card.ApplyRound(Alice, 10, RoundEvent.Finished, 6));
            Assert.Equal(RoundEvent.Finished, record.State);
            Assert.Equal(6, record.Total);
        }

        [Fact]
        public void DuplicatedEvents_ChangeNothing()
        {
            var card = new Scorecard();
            card.ApplyRound(Alice, 10, RoundEvent.Start, 0);
            card.ApplyHole(Alice, 10, 1, 4, 4);
            Assert.False(card.ApplyRound(Alice, 10, RoundEvent.Start, 0));
            Assert.False(card.ApplyHole(Alice, 10, 1, 4, 4));
            card.ApplyRound(Alice, 10, RoundEvent.Finished, 4);
            Assert.False(card.ApplyRound(Alice, 10, RoundEvent.Finished, 4));
            Assert.False(card.ApplyRound(Alice, 10, RoundEvent.Aborted, 4));
            card.ApplyHole(Alice, 10, 2, 3, 3);
            Assert.Equal(0, card.Get(Alice).Strokes[1]);
            Assert.Equal(RoundEvent.Finished, card.Get(Alice).State);
        }

        [Fact]
        public void NewRoundId_ReplacesTheOldRound()
        {
            var card = new Scorecard();
            card.ApplyRound(Alice, 10, RoundEvent.Start, 0);
            card.ApplyHole(Alice, 10, 1, 7, 4);
            Assert.True(card.ApplyRound(Alice, 11, RoundEvent.Start, 0));
            Assert.Equal(11u, card.Get(Alice).RoundId);
            Assert.Equal(0, card.Get(Alice).HolesPlayed);
        }

        [Fact]
        public void HoleWithoutStart_CreatesTheRound()
        {
            var card = new Scorecard();
            Assert.True(card.ApplyHole(Bob, 5, 3, 4, 3));
            var record = card.Get(Bob);
            Assert.True(record.InProgress);
            Assert.Equal(4, record.Strokes[2]);
            Assert.Equal(1, record.CurrentHole);
        }

        [Fact]
        public void CardRow_MergesTheSameRound_AndReplacesAnother()
        {
            var card = new Scorecard();
            card.ApplyRound(Alice, 10, RoundEvent.Start, 0);
            card.ApplyHole(Alice, 10, 2, 3, 0);
            var same = new CardRow { Peer = Alice, RoundId = 10, State = RoundEvent.Start, Strokes = new[] { 4, 9, 0, 0, 0, 0, 0, 0, 0 } };
            Assert.True(card.ApplyCardRow(same));
            Assert.Equal(4, card.Get(Alice).Strokes[0]);
            Assert.Equal(3, card.Get(Alice).Strokes[1]);

            var other = new CardRow { Peer = Alice, RoundId = 12, State = RoundEvent.Finished, Strokes = new[] { 1, 1, 1, 1, 1, 1, 1, 1, 1 } };
            Assert.True(card.ApplyCardRow(other));
            Assert.Equal(12u, card.Get(Alice).RoundId);
            Assert.Equal(RoundEvent.Finished, card.Get(Alice).State);
            Assert.Equal(9, card.Get(Alice).Total);
        }

        [Fact]
        public void LeftPlayers_StayButAreNotSentToLateJoiners()
        {
            var card = new Scorecard();
            card.ApplyRound(Alice, 1, RoundEvent.Start, 0);
            card.ApplyRound(Bob, 2, RoundEvent.Start, 0);
            card.MarkLeft(Bob);
            Assert.Equal(2, card.Count);
            Assert.True(card.Get(Bob).Left);
            var rows = card.BuildCard(null);
            Assert.Single(rows);
            Assert.Equal(Alice, rows[0].Peer);
            Assert.Empty(card.BuildCard(p => p != Alice));
        }

        [Fact]
        public void Rows_KeepFirstAppearanceOrder()
        {
            var card = new Scorecard();
            card.ApplyRound(Bob, 2, RoundEvent.Start, 0);
            card.ApplyRound(Alice, 1, RoundEvent.Start, 0);
            card.ApplyRound(Bob, 3, RoundEvent.Start, 0);
            Assert.Equal(new[] { Bob, Alice }, card.Rows.Select(r => r.Player).ToArray());
        }

        [Fact]
        public void InvalidInput_IsIgnored()
        {
            var card = new Scorecard();
            Assert.False(card.ApplyRound(PeerId.None, 1, RoundEvent.Start, 0));
            Assert.False(card.ApplyRound(Alice, 0, RoundEvent.Start, 0));
            Assert.False(card.ApplyHole(Alice, 1, 0, 3, 3));
            Assert.False(card.ApplyHole(Alice, 1, 10, 3, 3));
            Assert.Equal(0, card.Count);
        }

        [Fact]
        public void UnknownPar_GivesNoToPar()
        {
            var card = new Scorecard();
            card.ApplyHole(Alice, 1, 1, 4, 0);
            Assert.Null(card.ParForPlayed(card.Get(Alice)));
            card.SetPar(1, 5);
            Assert.Equal(5, card.ParForPlayed(card.Get(Alice)));
        }
    }

    public class PresenceTextTests
    {
        [Fact]
        public void HoledNotice_ReadsAsSpecified()
        {
            Assert.Equal("Alice holed out in 3 (par 4)", PresenceText.HoledToast("Alice", 3, 4));
            Assert.Equal("Alice holed out in 1", PresenceText.HoledToast("Alice", 1, 0));
            Assert.Equal("Someone holed out in 2", PresenceText.HoledToast(null, 2, 0));
        }

        [Fact]
        public void ToPar_FormatsSigns()
        {
            Assert.Equal("E", PresenceText.ToPar(36, 36));
            Assert.Equal("+2", PresenceText.ToPar(38, 36));
            Assert.Equal("-1", PresenceText.ToPar(35, 36));
        }

        [Fact]
        public void SceneLabel_IsNullOnlyInTheWorld()
        {
            Assert.Null(PresenceText.SceneLabel(3));
            Assert.Equal("In menu", PresenceText.SceneLabel(2));
            Assert.Equal("Credits", PresenceText.SceneLabel(4));
            Assert.Equal("Loading", PresenceText.SceneLabel(0));
        }
    }

    public class PresenceStateTests
    {
        [Fact]
        public void Roster_ReportsInfoChanges()
        {
            var roster = new PresenceRoster();
            var peer = new PeerId(5);
            var info = new InfoMessage { Scene = 3, SelectedBall = 2, Club = "Putter" };
            Assert.True(roster.ApplyInfo(peer, info, 1.0));
            Assert.False(roster.ApplyInfo(peer, info, 4.0));
            info.SelectedBall = 3;
            Assert.True(roster.ApplyInfo(peer, info, 5.0));
            roster.ApplyStroke(peer, 4);
            Assert.True(roster.TryGet(peer, out var presence));
            Assert.Equal(4u, presence.LastStroke);
            roster.Remove(peer);
            Assert.False(roster.TryGet(peer, out _));
        }

        [Fact]
        public void InfoEquality_TreatsNullAndEmptyAlike()
        {
            var a = new InfoMessage { Club = null, Activity = "" };
            var b = new InfoMessage { Club = "", Activity = null };
            Assert.True(a.Equals(b));
        }

        [Fact]
        public void Wind_SnapsOnLargeGaps_AndEasesSmallOnes()
        {
            float x = 0f, y = 0f;
            WindMath.Follow(ref x, ref y, 4f, 0f, 0.016f);
            Assert.Equal(4f, x);

            x = 3.5f;
            y = 0f;
            WindMath.Follow(ref x, ref y, 4f, 0f, 0.016f);
            Assert.Equal(3.66f, x, 3);

            x = 3.95f;
            WindMath.Follow(ref x, ref y, 4f, 0f, 0.016f);
            Assert.Equal(4f, x);
        }

        [Fact]
        public void Wind_ReliableThreshold()
        {
            Assert.False(WindMath.ChangedBeyond(1f, 1f, 1.005f, 1f, WindMath.ReliableDelta));
            Assert.True(WindMath.ChangedBeyond(1f, 1f, 1.02f, 1f, WindMath.ReliableDelta));
        }
    }
}
#endif
