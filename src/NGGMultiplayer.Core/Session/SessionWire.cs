using System.Collections.Generic;
using System.Globalization;
using System.Text;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;

namespace NGGMultiplayer.Core.Session
{
    /// <summary>
    /// Payload layouts of the session messages (after the type byte). All on <see cref="Channel.Control"/>.
    /// <code>
    /// Hello         int protocol, string modVersion, string gameBuild, string displayName, uint connectNonce
    /// Welcome       byte playerIndex, Roster
    /// Reject        byte RejectReason, int hostProtocol, string hostModVersion, string message
    /// RosterUpdate  Roster
    /// Ping          double senderTime
    /// Pong          double echoedSenderTime, double responderTime
    /// Goodbye       (empty)
    /// SessionEnded  byte SessionEndReason, string message
    /// Roster        varuint count, count x (PeerId id, byte index, string name, ushort pingMs)
    /// </code>
    /// The leading (protocol, modVersion) of Hello and (reason, hostProtocol, hostModVersion) of Reject
    /// must never change, so mismatched versions can still explain themselves to each other.
    /// </summary>
    internal static class SessionWire
    {
        public const int MaxVersionBytes = 64;
        public const int MaxNameBytes = 128;
        public const int MaxMessageBytes = 512;
        // A UTF-16 char encodes to at most 3 UTF-8 bytes (a surrogate pair to 4), so this always fits MaxMessageBytes.
        public const int MaxMessageChars = MaxMessageBytes / 3;

        public struct RosterEntry
        {
            public PeerId Id;
            public int Index;
            public string Name;
            public int PingMs;
        }

        public static void WriteRoster(NetWriter w, List<PlayerInfo> players)
        {
            w.WriteVarUInt((uint)players.Count);
            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                w.WritePeerId(p.Id);
                w.WriteByte((byte)p.PlayerIndex);
                w.WriteString(p.DisplayName);
                w.WriteUShort((ushort)(p.PingMs < 0 ? 0 : p.PingMs > ushort.MaxValue ? ushort.MaxValue : p.PingMs));
            }
        }

        /// <summary>Reads and validates a roster: bounded count, valid unique ids, unique in-range indices.</summary>
        public static void ReadRoster(NetReader r, List<RosterEntry> into)
        {
            into.Clear();
            uint count = r.ReadVarUInt();
            if (count == 0 || count > SessionConfig.AbsoluteMaxPlayers)
                throw new ProtocolException("Roster count " + count + " out of range.");
            for (int i = 0; i < (int)count; i++)
            {
                var entry = new RosterEntry
                {
                    Id = r.ReadPeerId(),
                    Index = r.ReadByte(),
                    Name = SanitizeDisplayName(r.ReadString(MaxNameBytes)),
                    PingMs = r.ReadUShort()
                };
                if (!entry.Id.IsValid)
                    throw new ProtocolException("Roster contains an invalid peer id.");
                if (entry.Index >= SessionConfig.AbsoluteMaxPlayers)
                    throw new ProtocolException("Roster index " + entry.Index + " out of range.");
                for (int j = 0; j < into.Count; j++)
                {
                    if (into[j].Id == entry.Id || into[j].Index == entry.Index)
                        throw new ProtocolException("Roster contains a duplicate peer or index.");
                }
                into.Add(entry);
            }
        }

        /// <summary>
        /// Strips control and format characters (no bidi-override spoofing), trims, and caps the length.
        /// Returns empty when nothing printable is left.
        /// </summary>
        public static string SanitizeDisplayName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return string.Empty;

            var sb = new StringBuilder(name.Length);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (char.IsSurrogate(c))
                {
                    // Keep only well-formed pairs.
                    if (char.IsHighSurrogate(c) && i + 1 < name.Length && char.IsLowSurrogate(name[i + 1]))
                    {
                        sb.Append(c).Append(name[i + 1]);
                        i++;
                    }
                    continue;
                }
                var category = char.GetUnicodeCategory(c);
                if (category == UnicodeCategory.Control
                    || category == UnicodeCategory.Format
                    || category == UnicodeCategory.LineSeparator
                    || category == UnicodeCategory.ParagraphSeparator
                    || category == UnicodeCategory.OtherNotAssigned)
                    continue;
                sb.Append(c);
            }

            string clean = sb.ToString().Trim();
            if (clean.Length > SessionConfig.MaxDisplayNameChars)
            {
                int cut = SessionConfig.MaxDisplayNameChars;
                if (char.IsHighSurrogate(clean[cut - 1]))
                    cut--;
                clean = clean.Substring(0, cut).TrimEnd();
            }
            return clean;
        }

        /// <summary>Reads a string without letting a malformed tail abort the caller (used only for diagnostics).</summary>
        public static string TryReadString(NetReader r, int maxBytes)
        {
            try
            {
                return r.IsAtEnd ? string.Empty : r.ReadString(maxBytes);
            }
            catch (ProtocolException)
            {
                return "?";
            }
        }
    }
}
