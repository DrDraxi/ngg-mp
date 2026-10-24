namespace NGGMultiplayer.Game.Progression
{
    /// <summary>
    /// The "You have $N" line under the objective (ChallengeReminder.UpdateBalanceReminder). The game refreshes it only
    /// after the local player's own pickups and purchases (FPSInteractable), not when the bank changes, so a change made
    /// for the other player (the host applying a client's purchase or bounty, a client applying the host's bank) left it
    /// showing the old amount while the bank itself was right. Pure.
    /// </summary>
    public static class BalanceDisplay
    {
        /// <summary>
        /// Whether a bank change needs the line refreshed by the mod: in a session, when the change was applied for
        /// another player (inside a replay scope). The player's own changes are refreshed by the game; refreshing those
        /// inside a replay scope would also hold back the player's own events for a moment.
        /// </summary>
        public static bool NeedsRefresh(bool inSession, bool appliedForOtherPlayer) => inSession && appliedForOtherPlayer;
    }
}
