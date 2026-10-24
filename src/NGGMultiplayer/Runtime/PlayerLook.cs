namespace NGGMultiplayer.Runtime
{
    /// <summary>
    /// Which golfer a player is drawn as: other players see their cutout in this look ([Presence] Character, the
    /// Character selector of the multiplayer screens). Sent in <c>ngg.pr.info</c> as one byte.
    /// </summary>
    public enum PlayerLook : byte
    {
        Male = 0,
        Female = 1
    }
}
