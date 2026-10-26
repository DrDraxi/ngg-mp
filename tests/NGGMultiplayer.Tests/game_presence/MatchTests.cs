// See PresenceWireTests.cs: compiled only with the Presence model sources linked (PRESENCE_MODEL_LINKED).
#if PRESENCE_MODEL_LINKED
using System;
using System.Linq;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Game.Presence;
using Xunit;

namespace NGGMultiplayer.Tests.GamePresence
{
    public class MatchTests
    {
        private static readonly PeerId Host = new PeerId(1);
        private static readonly PeerId Alice = new PeerId(2);
        private static readonly PeerId Bob = new PeerId(3);
        private static readonly int[] Pars = { 4, 3, 5, 4, 4, 3, 4, 5, 4 };

        private static MatchState NewMatch(MatchMode mode = MatchMode.Turns, int maxStrokes = 10, params PeerId[] players)
        {
            var match = new MatchState(7, mode, maxStrokes);
            for (int h = 1; h <= 9; h++)
                match.SetPar(h, Pars[h - 1]);
            foreach (var p in players.Length > 0 ? players : new[] { Host, Alice, Bob })
                Assert.True(match.AddPlayer(p));
            return match;
        }

        /// <summary>Everyone holes out on the current hole with these strokes, in the given order.</summary>
        private static void FinishHole(MatchState match, params (PeerId who, int strokes)[] scores)
        {
            foreach (var (who, strokes) in scores)
                Assert.True(match.ReportStroke(who, match.Hole, strokes, 0f, holed: true));
        }

        [Fact]
        public void FirstTee_GoesInJoinOrder_ThenFarthestPlays()
        {
            var match = NewMatch();
            Assert.Equal(Host, match.Turn);
            Assert.True(match.ReportStroke(Host, 1, 1, 120f, false));
            Assert.Equal(Alice, match.Turn);
            Assert.True(match.ReportStroke(Alice, 1, 1, 200f, false));
            Assert.Equal(Bob, match.Turn);
            Assert.True(match.ReportStroke(Bob, 1, 1, 50f, false));
            // All teed off: Alice is away.
            Assert.Equal(Alice, match.Turn);
            Assert.True(match.ReportStroke(Alice, 1, 2, 10f, false));
            Assert.Equal(Host, match.Turn);
        }

        [Fact]
        public void HoledPlayersAreSkipped_AndTheHoleCompletes()
        {
            var match = NewMatch(players: new[] { Host, Alice });
            Assert.True(match.ReportStroke(Host, 1, 1, 0f, true));
            Assert.Equal(Alice, match.Turn);
            Assert.False(match.HoleComplete);
            Assert.True(match.ReportStroke(Alice, 1, 1, 30f, false));
            Assert.Equal(Alice, match.Turn);
            Assert.True(match.ReportStroke(Alice, 1, 2, 0f, true));
            Assert.True(match.HoleComplete);
            Assert.Equal(PeerId.None, match.Turn);
            Assert.True(match.AdvanceHole());
            Assert.Equal(2, match.Hole);
        }

        [Fact]
        public void Honours_BestScoreTeesFirst_TiesLookBack()
        {
            var match = NewMatch();
            FinishHole(match, (Host, 5), (Alice, 4), (Bob, 4));
            match.AdvanceHole();
            // Alice and Bob level on hole 1: join order decides; the host is last.
            Assert.Equal(new[] { Alice, Bob, Host }, match.TeeOrder().Select(p => p.Id).ToArray());
            Assert.Equal(Alice, match.Turn);
            FinishHole(match, (Alice, 3), (Bob, 3), (Host, 2));
            match.AdvanceHole();
            // Host best on hole 2; Alice and Bob level on 2 and on 1 -> join order.
            Assert.Equal(new[] { Host, Alice, Bob }, match.TeeOrder().Select(p => p.Id).ToArray());
            FinishHole(match, (Host, 6), (Alice, 5), (Bob, 5));
            match.AdvanceHole();
            Assert.Equal(new[] { Alice, Bob, Host }, match.TeeOrder().Select(p => p.Id).ToArray());
        }

        [Fact]
        public void StrokeLimit_PicksUp()
        {
            var match = NewMatch(maxStrokes: 3, players: new[] { Host, Alice });
            Assert.True(match.ReportStroke(Host, 1, 3, 12f, false));
            Assert.Equal(HoleState.PickedUp, match.Get(Host).States[0]);
            Assert.Equal(3, match.HoleScore(match.Get(Host), 1));
            Assert.Equal(Alice, match.Turn);
            // A report for a hole already finished is ignored.
            Assert.False(match.ReportStroke(Host, 1, 4, 1f, false));
        }

        [Fact]
        public void ManualPickUp_CountsOneMore_AtLeastParPlusTwo()
        {
            // Hole 1 is a par 4: picking up on the tee counts 6, not a hole in one.
            var match = NewMatch(players: new[] { Host, Alice });
            Assert.True(match.PickUp(Host));
            Assert.Equal(6, match.HoleScore(match.Get(Host), 1));
            match.ReportStroke(Alice, 1, 6, 3f, false);
            Assert.True(match.PickUp(Alice));
            Assert.Equal(7, match.HoleScore(match.Get(Alice), 1));
            Assert.True(match.HoleComplete);
        }

        [Fact]
        public void ManualPickUp_CappedByTheLimit()
        {
            var match = NewMatch(maxStrokes: 5, players: new[] { Host, Alice });
            match.ReportStroke(Host, 1, 2, 40f, false);
            Assert.True(match.PickUp(Host));
            Assert.Equal(5, match.HoleScore(match.Get(Host), 1));
            match.ReportStroke(Alice, 1, 4, 3f, false);
            Assert.True(match.PickUp(Alice));
            Assert.Equal(5, match.HoleScore(match.Get(Alice), 1));
            Assert.True(match.HoleComplete);
        }

        [Fact]
        public void WrongHoleAndUnknownPlayers_AreIgnored()
        {
            var match = NewMatch(players: new[] { Host, Alice });
            Assert.False(match.ReportStroke(Host, 2, 1, 5f, false));
            Assert.False(match.ReportStroke(Bob, 1, 1, 5f, false));
            Assert.False(match.ReportStroke(Host, 1, 0, 0f, true));
            Assert.False(match.ReportStroke(Host, 1, 100, 5f, false));
        }

        [Fact]
        public void FreeMode_HasNoTurns_ButHolesStillWait()
        {
            var match = NewMatch(MatchMode.Free, players: new[] { Host, Alice });
            Assert.Equal(PeerId.None, match.Turn);
            FinishHole(match, (Host, 4));
            Assert.False(match.HoleComplete);
            Assert.False(match.AdvanceHole());
            FinishHole(match, (Alice, 3));
            Assert.True(match.AdvanceHole());
        }

        [Fact]
        public void LeavingPlayer_PassesTheTurn_AndEndsAnEmptyMatch()
        {
            var match = NewMatch(players: new[] { Host, Alice });
            Assert.Equal(Host, match.Turn);
            Assert.True(match.RemovePlayer(Host));
            Assert.Equal(Alice, match.Turn);
            FinishHole(match, (Alice, 4));
            Assert.True(match.HoleComplete);
            Assert.True(match.RemovePlayer(Alice));
            Assert.Equal(MatchPhase.Finished, match.Phase);
        }

        [Fact]
        public void LateJoiner_StartsOnTheCurrentHole_MissedHolesCountParPlusTwo()
        {
            var match = NewMatch(players: new[] { Host });
            FinishHole(match, (Host, 4));
            match.AdvanceHole();
            Assert.True(match.AddPlayer(Bob));
            var bob = match.Get(Bob);
            Assert.Equal(2, bob.JoinedAtHole);
            Assert.Equal(HoleState.Missed, bob.States[0]);
            Assert.Equal(6, match.HoleScore(bob, 1));
            // A missed hole ranks behind any played one on the tee.
            Assert.Equal(new[] { Host, Bob }, match.TeeOrder().Select(p => p.Id).ToArray());
            var standings = match.Standings();
            Assert.Equal(Host, standings[0].Player.Id);
            Assert.Equal(0, standings[0].ToPar);
            Assert.Equal(2, standings[1].ToPar);
        }

        [Fact]
        public void FinishAfterNine()
        {
            var match = NewMatch(players: new[] { Host, Alice });
            for (int h = 1; h <= 9; h++)
            {
                FinishHole(match, (Host, Pars[h - 1]), (Alice, Pars[h - 1] - 1));
                Assert.True(match.AdvanceHole());
            }
            Assert.Equal(MatchPhase.Finished, match.Phase);
            Assert.Equal(PeerId.None, match.Turn);
            var standings = match.Standings();
            Assert.Equal(Alice, standings[0].Player.Id);
            Assert.Equal(-9, standings[0].ToPar);
            Assert.Equal(1, standings[0].Position);
            Assert.Equal(2, standings[1].Position);
            Assert.Equal(27, standings[0].Total);
        }

        [Fact]
        public void Standings_TiesSharePosition()
        {
            var match = NewMatch();
            FinishHole(match, (Host, 4), (Alice, 3), (Bob, 4));
            var rows = match.Standings();
            Assert.Equal(Alice, rows[0].Player.Id);
            Assert.Equal("1", MatchState.PositionText(rows[0]));
            Assert.Equal("T2", MatchState.PositionText(rows[1]));
            Assert.Equal("T2", MatchState.PositionText(rows[2]));
            Assert.Equal("-1", MatchState.ToParText(rows[0].ToPar));
            Assert.Equal("E", MatchState.ToParText(rows[1].ToPar));
        }

        [Fact]
        public void Standings_MidHole_AheadIsWhoPlayedMoreOnTheSameScore()
        {
            var match = NewMatch(players: new[] { Host, Alice });
            FinishHole(match, (Host, 4), (Alice, 4));
            match.AdvanceHole();
            FinishHole(match, (Alice, 3));
            var rows = match.Standings();
            Assert.Equal(Alice, rows[0].Player.Id);
            Assert.Equal(2, rows[0].Thru);
            Assert.False(rows[0].Tied);
        }

        [Fact]
        public void ScoreNames()
        {
            Assert.Equal("Hole in one", MatchState.ScoreName(1, 3));
            Assert.Equal("Eagle", MatchState.ScoreName(3, 5));
            Assert.Equal("Birdie", MatchState.ScoreName(3, 4));
            Assert.Equal("Par", MatchState.ScoreName(4, 4));
            Assert.Equal("Bogey", MatchState.ScoreName(5, 4));
            Assert.Equal("+4", MatchState.ScoreName(8, 4));
        }

        [Fact]
        public void Snapshot_RoundTrips()
        {
            var match = NewMatch(maxStrokes: 8);
            match.ReportStroke(Host, 1, 1, 123.5f, false);
            match.RemovePlayer(Bob);
            var writer = new NetWriter();
            MatchMessage.Write(writer, match);
            var read = MatchMessage.Read(new NetReader(writer.ToArray()));
            Assert.Equal(match.MatchId, read.MatchId);
            Assert.Equal(match.Sequence, read.Sequence);
            Assert.Equal(match.Turn, read.Turn);
            Assert.Equal(8, read.MaxStrokes);
            Assert.Equal(Pars, read.Pars.ToArray());
            Assert.Equal(3, read.Players.Count);
            Assert.Equal(123.5f, read.Get(Host).Distance);
            Assert.Equal(1, read.Get(Host).Strokes[0]);
            Assert.True(float.IsNaN(read.Get(Alice).Distance));
            Assert.True(read.Get(Bob).Left);
        }

        [Fact]
        public void Snapshot_NoMatch_IsNull()
        {
            var writer = new NetWriter();
            MatchMessage.Write(writer, null);
            Assert.Null(MatchMessage.Read(new NetReader(writer.ToArray())));
        }

        [Fact]
        public void Snapshot_RejectsBadData()
        {
            var writer = new NetWriter();
            writer.WriteVarUInt(5);
            writer.WriteVarUInt(1);
            writer.WriteByte(9); // phase
            Assert.Throws<ProtocolException>(() => MatchMessage.Read(new NetReader(writer.ToArray())));
        }

        [Fact]
        public void Stroke_RoundTrips_UnknownDistanceIsNaN()
        {
            var message = new MatchStrokeMessage { MatchId = 4, Hole = 3, Strokes = 2, Distance = float.NaN, Holed = false };
            var writer = new NetWriter();
            message.Write(writer);
            var read = MatchStrokeMessage.Read(new NetReader(writer.ToArray()));
            Assert.Equal(3, read.Hole);
            Assert.Equal(2, read.Strokes);
            Assert.True(float.IsNaN(read.Distance));
            Assert.False(read.Holed);
        }

        [Fact]
        public void PlainText_ShowsHoleTurnAndCards()
        {
            var match = NewMatch();
            FinishHole(match, (Host, 4), (Alice, 3), (Bob, 6));
            match.AdvanceHole();
            Assert.True(match.ReportStroke(Alice, 2, 1, 5.5f, holed: false));
            string text = MatchText.Plain(match, Alice, id => id == Host ? "Host" : id == Alice ? "Alice" : "Bob");
            Assert.StartsWith("match 7 Turns Playing hole 2/9 (par 3)", text);
            Assert.Contains("1 Alice (you) -1 shot 1 total 3 [3,~1,-,-,-,-,-,-,-] 5.5m from the cup", text);
            Assert.Contains("3 Bob +2", text);
        }
    }
}
#endif
