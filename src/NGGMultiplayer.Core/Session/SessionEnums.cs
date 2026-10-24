namespace NGGMultiplayer.Core.Session
{
    public enum SessionRole : byte
    {
        None,
        Host,
        Client
    }

    public enum SessionState : byte
    {
        /// <summary>No session; incoming packets are discarded.</summary>
        Idle,
        /// <summary>Client sent Hello and is waiting for Welcome or Reject.</summary>
        Connecting,
        /// <summary>Hosting, or joined and welcomed by the host.</summary>
        Active
    }

    /// <summary>Why the host refused a Hello. Values are part of the wire format.</summary>
    public enum RejectReason : byte
    {
        Unknown = 0,
        ProtocolMismatch = 1,
        ModVersionMismatch = 2,
        GameBuildMismatch = 3,
        SessionFull = 4,
        NotAllowed = 5
    }

    /// <summary>
    /// Why the local session ended. <see cref="HostLeft"/> and <see cref="Kicked"/> also travel on the
    /// wire in the SessionEnded message, so existing values must not be renumbered.
    /// </summary>
    public enum SessionEndReason : byte
    {
        Unknown = 0,
        HostLeft = 1,
        Kicked = 2,
        /// <summary>The local player called Leave (or disposed the session).</summary>
        LocalLeft = 3,
        HostTimedOut = 4,
        ConnectTimedOut = 5,
        Rejected = 6,
        /// <summary>The host's roster no longer lists the local player (e.g. the host timed us out).</summary>
        DroppedByHost = 7
    }

    public enum PlayerLeaveReason : byte
    {
        /// <summary>Clients only learn about departures from roster updates, so they cannot tell why.</summary>
        Unknown,
        Left,
        TimedOut,
        Kicked
    }
}
