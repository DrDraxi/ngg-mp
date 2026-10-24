using System;
using System.Collections.Generic;
using NGGMultiplayer.Core.Net;

namespace NGGMultiplayer.Game.Presence
{
    /// <summary>One player's latest LMUGC / JustGolf round.</summary>
    internal sealed class RoundRecord
    {
        public RoundRecord(PeerId player, uint roundId)
        {
            Player = player;
            RoundId = roundId;
        }

        public PeerId Player { get; }

        public uint RoundId { get; }

        public RoundEvent State { get; internal set; }

        /// <summary>Strokes per hole; 0 = not played.</summary>
        public int[] Strokes { get; } = new int[PresenceWire.Holes];

        /// <summary>The total the player reported when the round ended; null while it runs.</summary>
        public int? ReportedTotal { get; internal set; }

        /// <summary>The player left the session; the row stays until the session ends.</summary>
        public bool Left { get; internal set; }

        public bool InProgress => State == RoundEvent.Start;

        public int HolesPlayed
        {
            get
            {
                int n = 0;
                foreach (int s in Strokes)
                {
                    if (s > 0)
                        n++;
                }
                return n;
            }
        }

        /// <summary>Sum of the played holes' strokes.</summary>
        public int PlayedTotal
        {
            get
            {
                int sum = 0;
                foreach (int s in Strokes)
                    sum += s;
                return sum;
            }
        }

        public int Total => ReportedTotal ?? PlayedTotal;

        /// <summary>The first unplayed hole (1..9), or 9 when every hole has a score.</summary>
        public int CurrentHole
        {
            get
            {
                for (int i = 0; i < Strokes.Length; i++)
                {
                    if (Strokes[i] <= 0)
                        return i + 1;
                }
                return PresenceWire.Holes;
            }
        }

        public CardRow ToCardRow() => new CardRow
        {
            Peer = Player,
            RoundId = RoundId,
            State = State,
            Strokes = (int[])Strokes.Clone()
        };
    }

    /// <summary>
    /// The session's shared scorecard: every player's latest round, fed by <c>ngg.pr.round</c> / <c>ngg.pr.hole</c>
    /// and, on a late joiner, by the host's <c>ngg.pr.card</c>. Every apply is idempotent and reports whether
    /// anything changed, so duplicated events (a client's own copy of an RPC to All) are harmless. Events of one
    /// sender arrive in order (reliable, one channel), so a different round id always means a newer round.
    /// Contains no Unity types (unit tested).
    /// </summary>
    internal sealed class Scorecard
    {
        private readonly Dictionary<PeerId, RoundRecord> _rounds = new Dictionary<PeerId, RoundRecord>();
        private readonly List<PeerId> _order = new List<PeerId>();
        private readonly int[] _pars = new int[PresenceWire.Holes];

        public int Count => _rounds.Count;

        /// <summary>Par per hole (0 = unknown), learned from hole events or set from the local course.</summary>
        public IReadOnlyList<int> Pars => _pars;

        /// <summary>Rows in the order players first appeared.</summary>
        public IEnumerable<RoundRecord> Rows
        {
            get
            {
                foreach (var peer in _order)
                {
                    if (_rounds.TryGetValue(peer, out var record))
                        yield return record;
                }
            }
        }

        public bool TryGet(PeerId player, out RoundRecord record) => _rounds.TryGetValue(player, out record);

        public RoundRecord Get(PeerId player) => _rounds.TryGetValue(player, out var record) ? record : null;

        public bool ApplyRound(PeerId player, uint roundId, RoundEvent ev, int total)
        {
            if (!player.IsValid || roundId == 0)
                return false;
            var record = Get(player);
            if (ev == RoundEvent.Start)
            {
                if (record != null && record.RoundId == roundId)
                    return false;
                Put(new RoundRecord(player, roundId) { Left = record?.Left ?? false });
                return true;
            }
            if (record == null || record.RoundId != roundId)
            {
                // The start was missed (joined mid-round): the end still tells the result.
                record = new RoundRecord(player, roundId) { Left = record?.Left ?? false };
                Put(record);
            }
            else if (!record.InProgress)
            {
                return false;
            }
            record.State = ev;
            record.ReportedTotal = total;
            return true;
        }

        public bool ApplyHole(PeerId player, uint roundId, int hole, int strokes, int par)
        {
            if (!player.IsValid || roundId == 0 || hole < 1 || hole > PresenceWire.Holes)
                return false;
            bool changed = false;
            if (par > 0 && par <= PresenceWire.MaxPar && _pars[hole - 1] != par)
            {
                _pars[hole - 1] = par;
                changed = true;
            }
            var record = Get(player);
            if (record == null || record.RoundId != roundId)
            {
                record = new RoundRecord(player, roundId) { Left = record?.Left ?? false };
                Put(record);
                changed = true;
            }
            else if (!record.InProgress)
            {
                // A late duplicate of a hole of a round that already ended.
                return changed;
            }
            if (record.Strokes[hole - 1] != strokes)
            {
                record.Strokes[hole - 1] = strokes;
                changed = true;
            }
            return changed;
        }

        /// <summary>
        /// A row from the host's card. Same round as the local row: fills holes the local row lacks and takes the
        /// further state. Another round (or none locally): the card's row replaces it.
        /// </summary>
        public bool ApplyCardRow(CardRow row)
        {
            if (!row.Peer.IsValid || row.RoundId == 0)
                return false;
            var record = Get(row.Peer);
            bool changed = false;
            if (record == null || record.RoundId != row.RoundId)
            {
                record = new RoundRecord(row.Peer, row.RoundId) { Left = record?.Left ?? false };
                Put(record);
                changed = true;
            }
            for (int h = 0; h < PresenceWire.Holes; h++)
            {
                int strokes = row.Strokes != null && h < row.Strokes.Length ? row.Strokes[h] : 0;
                if (strokes > 0 && record.Strokes[h] <= 0)
                {
                    record.Strokes[h] = strokes;
                    changed = true;
                }
            }
            if (record.InProgress && row.State != RoundEvent.Start)
            {
                record.State = row.State;
                changed = true;
            }
            return changed;
        }

        public void SetPar(int hole, int par)
        {
            if (hole >= 1 && hole <= PresenceWire.Holes && par > 0 && par <= PresenceWire.MaxPar)
                _pars[hole - 1] = par;
        }

        /// <summary>Sum of par over the holes <paramref name="record"/> played; null if a played hole's par is unknown.</summary>
        public int? ParForPlayed(RoundRecord record)
        {
            if (record == null)
                return null;
            int sum = 0;
            for (int h = 0; h < PresenceWire.Holes; h++)
            {
                if (record.Strokes[h] <= 0)
                    continue;
                if (_pars[h] <= 0)
                    return null;
                sum += _pars[h];
            }
            return sum;
        }

        public void MarkLeft(PeerId player)
        {
            if (_rounds.TryGetValue(player, out var record))
                record.Left = true;
        }

        /// <summary>Rows for a late joiner: every player still in the session.</summary>
        public List<CardRow> BuildCard(Func<PeerId, bool> include)
        {
            var rows = new List<CardRow>();
            foreach (var record in Rows)
            {
                if (record.Left || (include != null && !include(record.Player)))
                    continue;
                if (rows.Count >= PresenceWire.MaxCardRows)
                    break;
                rows.Add(record.ToCardRow());
            }
            return rows;
        }

        public void Clear()
        {
            _rounds.Clear();
            _order.Clear();
            Array.Clear(_pars, 0, _pars.Length);
        }

        private void Put(RoundRecord record)
        {
            if (!_rounds.ContainsKey(record.Player))
                _order.Add(record.Player);
            _rounds[record.Player] = record;
        }
    }
}
