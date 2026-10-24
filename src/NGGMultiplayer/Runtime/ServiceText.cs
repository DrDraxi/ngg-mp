using NGGMultiplayer.Core.Session;

namespace NGGMultiplayer.Runtime
{
    /// <summary>Texts the multiplayer services (Steam, Direct) share. Free of platform types.</summary>
    internal static class ServiceText
    {
        public static string Describe(SessionEndInfo info)
        {
            switch (info.Reason)
            {
                case SessionEndReason.LocalLeft: return "Left the session.";
                case SessionEndReason.HostLeft: return "The host ended the session.";
                case SessionEndReason.Kicked: return "You were removed from the session." + Suffix(info.Message);
                case SessionEndReason.HostTimedOut: return "Lost connection to the host.";
                case SessionEndReason.ConnectTimedOut: return "The host did not answer.";
                case SessionEndReason.DroppedByHost: return "The host dropped the connection.";
                case SessionEndReason.Rejected: return "The host refused the connection." + Suffix(info.Message);
                default: return info.ToString();
            }
        }

        public static string Truncate(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;
            value = value.Trim();
            if (value.Length <= maxChars)
                return value;
            // Do not split a surrogate pair.
            int length = char.IsHighSurrogate(value[maxChars - 1]) ? maxChars - 1 : maxChars;
            return value.Substring(0, length);
        }

        private static string Suffix(string message) => string.IsNullOrEmpty(message) ? string.Empty : " " + message;
    }
}
