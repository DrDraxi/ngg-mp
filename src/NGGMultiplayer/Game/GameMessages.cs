namespace NGGMultiplayer.Game
{
    /// <summary>
    /// Allocation of the game modules' network traffic (docs/GAME_INTEGRATION.md, "Messages"). Every game message
    /// is a named RPC (RpcRouter): it is gated until the client loaded the host's save, the host stamps the sender
    /// and can veto it, and relaying is built in. A module may only register names with its own prefix; the
    /// names themselves are defined in the module's folder.
    /// <para>The raw message types 80..127 stay reserved (none are registered); they are split the same way should a
    /// module ever need an ungated or higher-rate path, which goes through the integrator.</para>
    /// Persistent progress does not use RPCs: it travels as Core SaveDeltas (facts, see <see cref="FactTable"/>).
    /// </summary>
    public static class GameMessages
    {
        public const string LifecyclePrefix = "ngg.lc.";
        public const string PresencePrefix = "ngg.pr.";
        public const string ProgressionPrefix = "ngg.pg.";

        public const byte LifecycleRawFirst = 80;
        public const byte LifecycleRawLast = 89;
        public const byte PresenceRawFirst = 90;
        public const byte PresenceRawLast = 104;
        public const byte ProgressionRawFirst = 105;
        public const byte ProgressionRawLast = 127;
    }
}
