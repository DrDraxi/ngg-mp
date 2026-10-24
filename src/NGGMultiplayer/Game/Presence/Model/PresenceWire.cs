using System;
using System.Collections.Generic;
using System.Text;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Runtime;

namespace NGGMultiplayer.Game.Presence
{
    /// <summary>
    /// Payloads of the Presence RPCs (docs/GAME_INTEGRATION.md 2.2) and their bounds. Readers throw
    /// <see cref="ProtocolException"/> on anything malformed or out of range; writers clamp what the game hands them
    /// so a send never fails on a long club name. Contains no Unity types (unit tested).
    /// </summary>
    internal static class PresenceWire
    {
        public const int MaxClubBytes = 32;
        public const int MaxActivityBytes = 64;
        public const int MaxSurfaceBytes = 32;
        public const int MaxStroke = 999;
        public const int MaxPar = 20;
        public const int MaxHoleStrokes = 99;
        public const int MaxTotal = 10000;
        public const int MaxSelectedBall = 1024;
        public const int MaxCardRows = 64;
        public const int Holes = 9;
        public const float MaxWind = 1000f;
        public const float MaxCoordinate = 1e6f;
        public const byte MaxScene = 5;

        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, false);

        /// <summary>Validator body: true when <paramref name="read"/> parses the payload without a protocol error.</summary>
        public static bool IsValid(NetReader reader, Action<NetReader> read)
        {
            try
            {
                read(reader);
                return true;
            }
            catch (ProtocolException)
            {
                return false;
            }
        }

        /// <summary>The longest prefix of <paramref name="value"/> that fits in <paramref name="maxBytes"/> UTF-8 bytes.</summary>
        public static string Truncate(string value, int maxBytes)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;
            if (Utf8.GetByteCount(value) <= maxBytes)
                return value;
            int bytes = 0;
            int i = 0;
            while (i < value.Length)
            {
                int step = char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]) ? 2 : 1;
                int size = Utf8.GetByteCount(value.ToCharArray(i, step));
                if (bytes + size > maxBytes)
                    break;
                bytes += size;
                i += step;
            }
            return value.Substring(0, i);
        }

        internal static void RequireFinite(float value, string what)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                throw new ProtocolException(what + " is not finite.");
        }

        internal static void RequireFinite(NetVector3 value, string what)
        {
            if (!value.IsFinite || Math.Abs(value.x) > MaxCoordinate || Math.Abs(value.y) > MaxCoordinate
                || Math.Abs(value.z) > MaxCoordinate)
                throw new ProtocolException(what + " is not a finite world position.");
        }

        internal static int RequireRange(int value, int min, int max, string what)
        {
            if (value < min || value > max)
                throw new ProtocolException($"{what} {value} is outside {min}..{max}.");
            return value;
        }

        internal static uint RequireMax(uint value, uint max, string what)
        {
            if (value > max)
                throw new ProtocolException($"{what} {value} is above {max}.");
            return value;
        }
    }

    /// <summary>
    /// <c>ngg.pr.info</c>: <c>b scene, b controlMode (0 walk, 1 golf, 2 crouched walk), vi selectedBall, str club,
    /// str activity[, b look]</c>. The look came later: a payload without it is male, and a look this build does not
    /// know reads as male (so a newer peer's choice never drops its whole info).
    /// </summary>
    internal struct InfoMessage : IEquatable<InfoMessage>
    {
        public const byte Walk = 0;
        public const byte Golf = 1;
        public const byte Crouch = 2;

        public byte Scene;
        public byte ControlMode;
        public int SelectedBall;
        public string Club;
        public string Activity;
        public PlayerLook Look;

        public void Write(NetWriter writer)
        {
            writer.WriteByte(Scene);
            writer.WriteByte(ControlMode);
            writer.WriteVarInt(SelectedBall);
            writer.WriteString(PresenceWire.Truncate(Club, PresenceWire.MaxClubBytes));
            writer.WriteString(PresenceWire.Truncate(Activity, PresenceWire.MaxActivityBytes));
            writer.WriteByte((byte)Look);
        }

        public static InfoMessage Read(NetReader reader)
        {
            var message = new InfoMessage
            {
                Scene = reader.ReadByte(),
                ControlMode = reader.ReadByte(),
                SelectedBall = reader.ReadVarInt(),
                Club = reader.ReadString(PresenceWire.MaxClubBytes),
                Activity = reader.ReadString(PresenceWire.MaxActivityBytes)
            };
            if (!reader.IsAtEnd)
                message.Look = reader.ReadByte() == (byte)PlayerLook.Female ? PlayerLook.Female : PlayerLook.Male;
            if (message.Scene > PresenceWire.MaxScene)
                throw new ProtocolException($"Unknown scene {message.Scene}.");
            if (message.ControlMode > Crouch)
                throw new ProtocolException($"Unknown control mode {message.ControlMode}.");
            PresenceWire.RequireRange(message.SelectedBall, -1, PresenceWire.MaxSelectedBall, "Selected ball");
            return message;
        }

        public bool Equals(InfoMessage other) =>
            Scene == other.Scene && ControlMode == other.ControlMode && SelectedBall == other.SelectedBall && Look == other.Look
            && string.Equals(Club ?? string.Empty, other.Club ?? string.Empty, StringComparison.Ordinal)
            && string.Equals(Activity ?? string.Empty, other.Activity ?? string.Empty, StringComparison.Ordinal);

        public override bool Equals(object obj) => obj is InfoMessage other && Equals(other);

        public override int GetHashCode() => (Scene << 16) ^ (ControlMode << 8) ^ SelectedBall;
    }

    /// <summary><c>ngg.pr.shot</c>: <c>str club, f power, b shotType, b spinType, vu stroke, bool isMiss, v3 from</c>.</summary>
    internal struct ShotMessage
    {
        public string Club;

        /// <summary>Fraction of the club's full swing (PresenceText.PowerFraction); negative when unknown.</summary>
        public float Power;
        public byte ShotType;
        public byte SpinType;
        public uint Stroke;
        public bool IsMiss;
        public NetVector3 From;

        public void Write(NetWriter writer)
        {
            writer.WriteString(PresenceWire.Truncate(Club, PresenceWire.MaxClubBytes));
            writer.WriteFloat(Power);
            writer.WriteByte(ShotType);
            writer.WriteByte(SpinType);
            writer.WriteVarUInt(Math.Min(Stroke, (uint)PresenceWire.MaxStroke));
            writer.WriteBool(IsMiss);
            writer.WriteVector3(From);
        }

        public static ShotMessage Read(NetReader reader)
        {
            var message = new ShotMessage
            {
                Club = reader.ReadString(PresenceWire.MaxClubBytes),
                Power = reader.ReadFloat(),
                ShotType = reader.ReadByte(),
                SpinType = reader.ReadByte(),
                Stroke = reader.ReadVarUInt(),
                IsMiss = reader.ReadBool(),
                From = reader.ReadVector3()
            };
            PresenceWire.RequireFinite(message.Power, "Shot power");
            PresenceWire.RequireMax(message.Stroke, PresenceWire.MaxStroke, "Stroke");
            PresenceWire.RequireFinite(message.From, "Shot origin");
            return message;
        }
    }

    /// <summary><c>ngg.pr.rest</c>: <c>v3 pos, f distance, str surface, vu stroke</c>.</summary>
    internal struct RestMessage
    {
        public NetVector3 Position;

        /// <summary>The game's m_finishedDistance: to the closest hole in front; 999 none, -1 reset.</summary>
        public float Distance;
        public string Surface;
        public uint Stroke;

        public void Write(NetWriter writer)
        {
            writer.WriteVector3(Position);
            writer.WriteFloat(Distance);
            writer.WriteString(PresenceWire.Truncate(Surface, PresenceWire.MaxSurfaceBytes));
            writer.WriteVarUInt(Math.Min(Stroke, (uint)PresenceWire.MaxStroke));
        }

        public static RestMessage Read(NetReader reader)
        {
            var message = new RestMessage
            {
                Position = reader.ReadVector3(),
                Distance = reader.ReadFloat(),
                Surface = reader.ReadString(PresenceWire.MaxSurfaceBytes),
                Stroke = reader.ReadVarUInt()
            };
            PresenceWire.RequireFinite(message.Position, "Rest position");
            PresenceWire.RequireFinite(message.Distance, "Rest distance");
            PresenceWire.RequireMax(message.Stroke, PresenceWire.MaxStroke, "Stroke");
            return message;
        }
    }

    /// <summary><c>ngg.pr.holed</c>: <c>vi par, vu strokes, vi lmugcHole (-1 none)</c>.</summary>
    internal struct HoledMessage
    {
        public int Par;
        public uint Strokes;
        public int LmugcHole;

        public void Write(NetWriter writer)
        {
            writer.WriteVarInt(Clamp(Par, 0, PresenceWire.MaxPar));
            writer.WriteVarUInt(Math.Min(Strokes, (uint)PresenceWire.MaxStroke));
            writer.WriteVarInt(LmugcHole >= 1 && LmugcHole <= PresenceWire.Holes ? LmugcHole : -1);
        }

        public static HoledMessage Read(NetReader reader)
        {
            var message = new HoledMessage
            {
                Par = reader.ReadVarInt(),
                Strokes = reader.ReadVarUInt(),
                LmugcHole = reader.ReadVarInt()
            };
            PresenceWire.RequireRange(message.Par, 0, PresenceWire.MaxPar, "Par");
            PresenceWire.RequireMax(message.Strokes, PresenceWire.MaxStroke, "Strokes");
            if (message.LmugcHole != -1)
                PresenceWire.RequireRange(message.LmugcHole, 1, PresenceWire.Holes, "LMUGC hole");
            return message;
        }

        internal static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;
    }

    /// <summary><c>ngg.pr.wind</c>: <c>v2 current, v2 desired, bool hostFree</c> (x then y, as GameWire).</summary>
    internal struct WindMessage
    {
        public float CurrentX, CurrentY, DesiredX, DesiredY;
        public bool HostFree;

        public void Write(NetWriter writer)
        {
            writer.WriteFloat(CurrentX);
            writer.WriteFloat(CurrentY);
            writer.WriteFloat(DesiredX);
            writer.WriteFloat(DesiredY);
            writer.WriteBool(HostFree);
        }

        public static WindMessage Read(NetReader reader)
        {
            var message = new WindMessage
            {
                CurrentX = reader.ReadFloat(),
                CurrentY = reader.ReadFloat(),
                DesiredX = reader.ReadFloat(),
                DesiredY = reader.ReadFloat(),
                HostFree = reader.ReadBool()
            };
            Check(message.CurrentX);
            Check(message.CurrentY);
            Check(message.DesiredX);
            Check(message.DesiredY);
            return message;
        }

        private static void Check(float value)
        {
            PresenceWire.RequireFinite(value, "Wind");
            if (Math.Abs(value) > PresenceWire.MaxWind)
                throw new ProtocolException($"Wind component {value} is out of range.");
        }
    }

    internal enum RoundEvent : byte
    {
        Start = 0,
        Finished = 1,
        Aborted = 2
    }

    /// <summary><c>ngg.pr.round</c>: <c>vu roundId, b event (0 start, 1 finished, 2 aborted), vi total</c>.</summary>
    internal struct RoundMessage
    {
        public uint RoundId;
        public RoundEvent Event;
        public int Total;

        public void Write(NetWriter writer)
        {
            writer.WriteVarUInt(RoundId);
            writer.WriteByte((byte)Event);
            writer.WriteVarInt(HoledMessage.Clamp(Total, -PresenceWire.MaxTotal, PresenceWire.MaxTotal));
        }

        public static RoundMessage Read(NetReader reader)
        {
            uint id = reader.ReadVarUInt();
            byte ev = reader.ReadByte();
            int total = reader.ReadVarInt();
            if (id == 0)
                throw new ProtocolException("Round id 0 is reserved.");
            if (ev > (byte)RoundEvent.Aborted)
                throw new ProtocolException($"Unknown round event {ev}.");
            PresenceWire.RequireRange(total, -PresenceWire.MaxTotal, PresenceWire.MaxTotal, "Round total");
            return new RoundMessage { RoundId = id, Event = (RoundEvent)ev, Total = total };
        }
    }

    /// <summary><c>ngg.pr.hole</c>: <c>vu roundId, b hole (1..9), vi strokes, vi par</c>.</summary>
    internal struct HoleMessage
    {
        public uint RoundId;
        public int Hole;
        public int Strokes;
        public int Par;

        public void Write(NetWriter writer)
        {
            writer.WriteVarUInt(RoundId);
            writer.WriteByte((byte)HoledMessage.Clamp(Hole, 1, PresenceWire.Holes));
            writer.WriteVarInt(HoledMessage.Clamp(Strokes, 0, PresenceWire.MaxHoleStrokes));
            writer.WriteVarInt(HoledMessage.Clamp(Par, 0, PresenceWire.MaxPar));
        }

        public static HoleMessage Read(NetReader reader)
        {
            var message = new HoleMessage
            {
                RoundId = reader.ReadVarUInt(),
                Hole = reader.ReadByte(),
                Strokes = reader.ReadVarInt(),
                Par = reader.ReadVarInt()
            };
            if (message.RoundId == 0)
                throw new ProtocolException("Round id 0 is reserved.");
            PresenceWire.RequireRange(message.Hole, 1, PresenceWire.Holes, "Hole");
            PresenceWire.RequireRange(message.Strokes, 0, PresenceWire.MaxHoleStrokes, "Hole strokes");
            PresenceWire.RequireRange(message.Par, 0, PresenceWire.MaxPar, "Par");
            return message;
        }
    }

    /// <summary>One row of <c>ngg.pr.card</c>: <c>u64 peer, vu roundId, b finished, 9 * vi strokes (0 = unplayed)</c>.</summary>
    internal struct CardRow
    {
        public PeerId Peer;
        public uint RoundId;
        public RoundEvent State;
        public int[] Strokes;
    }

    /// <summary><c>ngg.pr.card</c>: <c>vu playerCount, n * row</c>. Host to one peer (late join).</summary>
    internal static class CardMessage
    {
        public static void Write(NetWriter writer, IReadOnlyList<CardRow> rows)
        {
            int count = Math.Min(rows?.Count ?? 0, PresenceWire.MaxCardRows);
            writer.WriteVarUInt((uint)count);
            for (int i = 0; i < count; i++)
            {
                var row = rows[i];
                writer.WriteULong(row.Peer.Value);
                writer.WriteVarUInt(row.RoundId);
                writer.WriteByte((byte)row.State);
                for (int h = 0; h < PresenceWire.Holes; h++)
                {
                    int strokes = row.Strokes != null && h < row.Strokes.Length ? row.Strokes[h] : 0;
                    writer.WriteVarInt(HoledMessage.Clamp(strokes, 0, PresenceWire.MaxHoleStrokes));
                }
            }
        }

        public static List<CardRow> Read(NetReader reader)
        {
            uint count = reader.ReadVarUInt();
            if (count > PresenceWire.MaxCardRows)
                throw new ProtocolException($"Scorecard with {count} rows (limit {PresenceWire.MaxCardRows}).");
            var rows = new List<CardRow>((int)count);
            for (int i = 0; i < count; i++)
            {
                var row = new CardRow
                {
                    Peer = new PeerId(reader.ReadULong()),
                    RoundId = reader.ReadVarUInt(),
                    Strokes = new int[PresenceWire.Holes]
                };
                byte state = reader.ReadByte();
                if (state > (byte)RoundEvent.Aborted)
                    throw new ProtocolException($"Unknown round state {state}.");
                row.State = (RoundEvent)state;
                if (!row.Peer.IsValid)
                    throw new ProtocolException("Scorecard row without a player.");
                if (row.RoundId == 0)
                    throw new ProtocolException("Round id 0 is reserved.");
                for (int h = 0; h < PresenceWire.Holes; h++)
                    row.Strokes[h] = PresenceWire.RequireRange(reader.ReadVarInt(), 0, PresenceWire.MaxHoleStrokes, "Hole strokes");
                rows.Add(row);
            }
            return rows;
        }
    }
}
