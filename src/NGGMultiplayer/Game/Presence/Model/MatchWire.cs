using System;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;

namespace NGGMultiplayer.Game.Presence
{
    /// <summary>
    /// <c>ngg.pr.match</c> (host to all): the whole <see cref="MatchState"/>: <c>vu matchId, vu sequence, b phase,
    /// b mode, b hole, u64 turn, b maxStrokes, 9 x b par, vu count, per player {u64 peer, vu order, b joinedAtHole,
    /// bool left, f distance (-1 unknown), 9 x {b strokes, b state}}</c>. A matchId of 0 means "no match" (the round
    /// ended or the mode is off) and carries nothing else.
    /// </summary>
    internal static class MatchMessage
    {
        public static void Write(NetWriter writer, MatchState match)
        {
            if (match == null)
            {
                writer.WriteVarUInt(0);
                return;
            }
            writer.WriteVarUInt(match.MatchId);
            writer.WriteVarUInt(match.Sequence);
            writer.WriteByte((byte)match.Phase);
            writer.WriteByte((byte)match.Mode);
            writer.WriteByte((byte)match.Hole);
            writer.WritePeerId(match.Turn);
            writer.WriteByte((byte)match.MaxStrokes);
            for (int h = 1; h <= MatchState.Holes; h++)
                writer.WriteByte((byte)HoledMessage.Clamp(match.Par(h), 0, PresenceWire.MaxPar));
            int count = Math.Min(match.Players.Count, MatchState.MaxPlayers);
            writer.WriteVarUInt((uint)count);
            for (int i = 0; i < count; i++)
            {
                var player = match.Players[i];
                writer.WritePeerId(player.Id);
                writer.WriteVarUInt((uint)Math.Max(0, player.Order));
                writer.WriteByte((byte)HoledMessage.Clamp(player.JoinedAtHole, 1, MatchState.Holes));
                writer.WriteBool(player.Left);
                writer.WriteFloat(float.IsNaN(player.Distance) || float.IsInfinity(player.Distance) ? -1f : player.Distance);
                for (int h = 0; h < MatchState.Holes; h++)
                {
                    writer.WriteByte((byte)HoledMessage.Clamp(player.Strokes[h], 0, MatchState.MaxStrokeLimit));
                    writer.WriteByte((byte)player.States[h]);
                }
            }
        }

        /// <summary>The host's match, or null for "no match".</summary>
        public static MatchState Read(NetReader reader)
        {
            uint id = reader.ReadVarUInt();
            if (id == 0)
                return null;
            uint sequence = reader.ReadVarUInt();
            byte phase = reader.ReadByte();
            byte mode = reader.ReadByte();
            int hole = reader.ReadByte();
            var turn = reader.ReadPeerId();
            int maxStrokes = reader.ReadByte();
            if (phase > (byte)MatchPhase.Finished || phase == (byte)MatchPhase.None)
                throw new ProtocolException($"Unknown match phase {phase}.");
            if (mode > (byte)MatchMode.Free)
                throw new ProtocolException($"Unknown match mode {mode}.");
            PresenceWire.RequireRange(hole, 1, MatchState.Holes, "Match hole");
            PresenceWire.RequireRange(maxStrokes, 0, MatchState.MaxStrokeLimit, "Match stroke limit");
            var match = new MatchState(id, (MatchMode)mode, maxStrokes)
            {
                Sequence = sequence,
                Phase = (MatchPhase)phase,
                Hole = hole,
                Turn = turn
            };
            for (int h = 1; h <= MatchState.Holes; h++)
            {
                int par = reader.ReadByte();
                PresenceWire.RequireRange(par, 0, PresenceWire.MaxPar, "Match par");
                match.SetPar(h, par);
            }
            uint count = reader.ReadVarUInt();
            if (count > MatchState.MaxPlayers)
                throw new ProtocolException($"Match with {count} players (limit {MatchState.MaxPlayers}).");
            for (int i = 0; i < count; i++)
            {
                var peer = reader.ReadPeerId();
                uint order = reader.ReadVarUInt();
                int joined = reader.ReadByte();
                bool left = reader.ReadBool();
                float distance = reader.ReadFloat();
                if (!peer.IsValid)
                    throw new ProtocolException("Match player without an id.");
                if (match.Get(peer) != null)
                    throw new ProtocolException("Match player listed twice.");
                if (order > 1000)
                    throw new ProtocolException($"Match player order {order} out of range.");
                PresenceWire.RequireRange(joined, 1, MatchState.Holes, "Match join hole");
                PresenceWire.RequireFinite(distance, "Match distance");
                var player = new MatchPlayer(peer, (int)order, joined)
                {
                    Left = left,
                    Distance = distance < 0f ? float.NaN : Math.Min(distance, PresenceWire.MaxCoordinate)
                };
                for (int h = 0; h < MatchState.Holes; h++)
                {
                    int strokes = reader.ReadByte();
                    byte state = reader.ReadByte();
                    PresenceWire.RequireRange(strokes, 0, MatchState.MaxStrokeLimit, "Match strokes");
                    if (state > (byte)HoleState.Missed)
                        throw new ProtocolException($"Unknown hole state {state}.");
                    player.Strokes[h] = strokes;
                    player.States[h] = (HoleState)state;
                }
                match.AddRaw(player);
            }
            return match;
        }
    }

    /// <summary>
    /// <c>ngg.pr.mstroke</c> (player to host): <c>vu matchId, b hole, b strokes, f distance (-1 unknown), bool holed,
    /// bool pickUp</c>, sent when the player's ball came to rest or dropped, and when they pick up.
    /// </summary>
    internal struct MatchStrokeMessage
    {
        public uint MatchId;
        public int Hole;
        public int Strokes;
        public float Distance;
        public bool Holed;
        public bool PickUp;

        public void Write(NetWriter writer)
        {
            writer.WriteVarUInt(MatchId);
            writer.WriteByte((byte)HoledMessage.Clamp(Hole, 1, MatchState.Holes));
            writer.WriteByte((byte)HoledMessage.Clamp(Strokes, 0, MatchState.MaxStrokeLimit));
            writer.WriteFloat(float.IsNaN(Distance) || float.IsInfinity(Distance) || Distance < 0f
                ? -1f : Math.Min(Distance, PresenceWire.MaxCoordinate));
            writer.WriteBool(Holed);
            writer.WriteBool(PickUp);
        }

        public static MatchStrokeMessage Read(NetReader reader)
        {
            var message = new MatchStrokeMessage
            {
                MatchId = reader.ReadVarUInt(),
                Hole = reader.ReadByte(),
                Strokes = reader.ReadByte(),
                Distance = reader.ReadFloat(),
                Holed = reader.ReadBool(),
                PickUp = reader.ReadBool()
            };
            if (message.MatchId == 0)
                throw new ProtocolException("Match id 0 is reserved.");
            PresenceWire.RequireRange(message.Hole, 1, MatchState.Holes, "Stroke hole");
            PresenceWire.RequireRange(message.Strokes, 0, MatchState.MaxStrokeLimit, "Stroke count");
            PresenceWire.RequireFinite(message.Distance, "Stroke distance");
            if (message.Distance < 0f)
                message.Distance = float.NaN;
            return message;
        }
    }
}
