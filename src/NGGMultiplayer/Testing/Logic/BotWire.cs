using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Game.Progression;

namespace NGGMultiplayer.Testing
{
    /// <summary>
    /// Payloads of the Progression RPCs a test bot sends or reads (docs/GAME_INTEGRATION.md 2.2), byte for byte what
    /// the Progression module's client code writes. That code writes them inline (Interactions / WorldEvents keep the
    /// writers private), so these mirror it; the integrator may move both onto one shared writer. Readers throw
    /// <see cref="ProtocolException"/> on malformed input. No Unity types (unit tested).
    /// </summary>
    public static class BotWire
    {
        // ---- client -> host ---------------------------------------------------------------------------

        /// <summary><c>ngg.pg.claim</c>: <c>vu claimId, str interactId, b interactionType</c>.</summary>
        public static void WriteClaim(NetWriter w, uint claimId, string interactId, byte interactionType)
        {
            w.WriteVarUInt(claimId);
            w.WriteString(interactId ?? string.Empty);
            w.WriteByte(interactionType);
        }

        /// <summary><c>ngg.pg.bank</c>: <c>vu claimId, vi change</c>.</summary>
        public static void WriteBank(NetWriter w, uint claimId, int change)
        {
            w.WriteVarUInt(claimId);
            w.WriteVarInt(change);
        }

        /// <summary><c>ngg.pg.world</c>: <c>b op, str id, bool flag</c>.</summary>
        public static void WriteWorld(NetWriter w, WorldOp op, string id, bool flag)
        {
            w.WriteByte((byte)op);
            w.WriteString(id ?? string.Empty);
            w.WriteBool(flag);
        }

        /// <summary><c>ngg.pg.story</c> and <c>ngg.pg.storyHost</c>: <c>str method</c>.</summary>
        public static void WriteMethod(NetWriter w, string method) => w.WriteString(method ?? string.Empty);

        /// <summary><c>ngg.pg.roundScore</c>: <c>vi score</c>.</summary>
        public static void WriteRoundScore(NetWriter w, int score) => w.WriteVarInt(score);

        /// <summary><c>ngg.pg.lmugcBest</c>: <c>vi bestBefore, vi best, vi noAssistBefore, vi noAssist</c>.</summary>
        public static void WriteLmugcBest(NetWriter w, int bestBefore, int best, int noAssistBefore, int noAssist)
        {
            w.WriteVarInt(bestBefore);
            w.WriteVarInt(best);
            w.WriteVarInt(noAssistBefore);
            w.WriteVarInt(noAssist);
        }

        // ---- host -> client ---------------------------------------------------------------------------

        public readonly struct Grant
        {
            public Grant(uint claimId, bool granted, int hostBalance, string reason)
            {
                ClaimId = claimId;
                Granted = granted;
                HostBalance = hostBalance;
                Reason = reason ?? string.Empty;
            }

            public readonly uint ClaimId;
            public readonly bool Granted;
            public readonly int HostBalance;
            public readonly string Reason;
        }

        /// <summary><c>ngg.pg.grant</c>: <c>vu claimId, bool granted, vi hostBalance, str reason</c>.</summary>
        public static Grant ReadGrant(NetReader r)
        {
            uint claimId = r.ReadVarUInt();
            bool granted = r.ReadBool();
            int balance = r.ReadVarInt();
            string reason = r.ReadString(ProgressionRpc.MaxReasonBytes);
            return new Grant(claimId, granted, balance, reason);
        }

        public static void WriteGrant(NetWriter w, Grant grant)
        {
            w.WriteVarUInt(grant.ClaimId);
            w.WriteBool(grant.Granted);
            w.WriteVarInt(grant.HostBalance);
            w.WriteString(grant.Reason);
        }

        public readonly struct Note
        {
            public Note(PeerId by, byte kind, string text)
            {
                By = by;
                Kind = kind;
                Text = text ?? string.Empty;
            }

            public readonly PeerId By;
            public readonly byte Kind;
            public readonly string Text;
        }

        /// <summary><c>ngg.pg.note</c>: <c>u64 by, b kind, str text</c>.</summary>
        public static Note ReadNote(NetReader r)
        {
            ulong by = r.ReadULong();
            byte kind = r.ReadByte();
            string text = r.ReadString(ProgressionRpc.MaxNoteBytes);
            return new Note(new PeerId(by), kind, text);
        }

        public readonly struct Digest
        {
            public Digest(uint revision, uint crc, uint count)
            {
                Revision = revision;
                Crc = crc;
                Count = count;
            }

            public readonly uint Revision;
            public readonly uint Crc;
            public readonly uint Count;
        }

        /// <summary><c>ngg.pg.digest</c>: <c>vu revision, u32 crc, vu factCount</c>.</summary>
        public static Digest ReadDigest(NetReader r)
        {
            uint revision = r.ReadVarUInt();
            uint crc = r.ReadUInt();
            uint count = r.ReadVarUInt();
            return new Digest(revision, crc, count);
        }
    }
}
