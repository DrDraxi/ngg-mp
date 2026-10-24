using NGGMultiplayer.Game.Progression;
using Xunit;

namespace NGGMultiplayer.Tests.GameProgression
{
    /// <summary>The "You have $N" line must follow bank changes made for the other player (2026-10-21: host showed $3,040, bank $2,640).</summary>
    public class BalanceDisplayTests
    {
        [Theory]
        [InlineData(true, true, true)]
        // The player's own pickups and purchases: the game refreshes the line itself.
        [InlineData(true, false, false)]
        // Offline nothing is applied for anyone else.
        [InlineData(false, true, false)]
        [InlineData(false, false, false)]
        public void RefreshesOnlyForChangesMadeForTheOtherPlayer(bool inSession, bool forOther, bool refresh)
        {
            Assert.Equal(refresh, BalanceDisplay.NeedsRefresh(inSession, forOther));
        }
    }
}
