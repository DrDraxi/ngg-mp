namespace NGGMultiplayer.Core.Session
{
    /// <summary>Payload of <see cref="SessionManager.SessionEnded"/>.</summary>
    public readonly struct SessionEndInfo
    {
        public SessionEndInfo(SessionEndReason reason, RejectReason rejectReason, string message)
        {
            Reason = reason;
            RejectReason = rejectReason;
            Message = message ?? string.Empty;
        }

        public SessionEndReason Reason { get; }

        /// <summary>Meaningful only when <see cref="Reason"/> is <see cref="SessionEndReason.Rejected"/>.</summary>
        public RejectReason RejectReason { get; }

        /// <summary>Human-readable explanation for UI (includes both mod versions on a version mismatch).</summary>
        public string Message { get; }

        public override string ToString()
        {
            var head = Reason == SessionEndReason.Rejected ? "Rejected (" + RejectReason + ")" : Reason.ToString();
            return string.IsNullOrEmpty(Message) ? head : head + ": " + Message;
        }
    }
}
