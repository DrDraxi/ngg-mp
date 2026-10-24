using NGGMultiplayer.Game.Progression;
using Xunit;

namespace NGGMultiplayer.Tests.GameProgression
{
    public class RoundRecordRulesTests
    {
        [Theory]
        // A round is the sum of 9 hole scores (an unplayed hole counts par + 2): at least 9 (answers 139).
        [InlineData(0, false)]
        [InlineData(-5, false)]
        [InlineData(9, true)]
        [InlineData(72, true)]
        [InlineData(RoundRecordRules.MaxRoundScore, true)]
        [InlineData(RoundRecordRules.MaxRoundScore + 1, false)]
        [InlineData(RoundRecordRules.MinRoundScore - 1, false)]
        [InlineData(int.MaxValue, false)]
        public void RoundScoresOutsideTheBoundsAreRefused(int score, bool plausible)
        {
            Assert.Equal(plausible, RoundRecordRules.IsPlausibleRoundScore(score));
        }

        [Fact]
        public void BestIsTakenOnlyWhileTheHostStillHasTheValueTheClientComparedAgainst()
        {
            Assert.True(RoundRecordRules.AcceptBest(hostValue: 40, before: 40, after: 35));
            // The host set a better one meanwhile: kept.
            Assert.False(RoundRecordRules.AcceptBest(hostValue: 30, before: 40, after: 35));
            // Nothing changed.
            Assert.False(RoundRecordRules.AcceptBest(hostValue: 40, before: 40, after: 40));
            // Garbage.
            Assert.False(RoundRecordRules.AcceptBest(hostValue: 40, before: 40, after: 100000));
        }

        [Fact]
        public void BestFollowsTheGamesOwnRule()
        {
            // LMUGC.CompleteHole: total < best || best < 9 (a best below 9 is "none yet"; answers 140).
            Assert.False(RoundRecordRules.HasBest(0));
            Assert.True(RoundRecordRules.HasBest(9));
            Assert.True(RoundRecordRules.GameKeepsAsBest(best: 0, total: 50));
            Assert.True(RoundRecordRules.GameKeepsAsBest(best: 40, total: 39));
            Assert.False(RoundRecordRules.GameKeepsAsBest(best: 40, total: 41));
            Assert.True(RoundRecordRules.AcceptBest(hostValue: 0, before: 0, after: 50));
            // A worse total never replaces a real best, whatever the client says.
            Assert.False(RoundRecordRules.AcceptBest(hostValue: 40, before: 40, after: 45));
            Assert.False(RoundRecordRules.AcceptBest(hostValue: 0, before: 0, after: 5));
        }
    }
}
