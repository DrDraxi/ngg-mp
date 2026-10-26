using System;
using System.Collections.Generic;
using System.Globalization;
using NGGMultiplayer.Core.Net;

namespace NGGMultiplayer.Game.Presence
{
    /// <summary>How the players of a competitive round take their shots.</summary>
    public enum MatchMode : byte
    {
        /// <summary>Real golf: one player at a time; honours on the tee, then whoever is farthest from the cup.</summary>
        Turns = 0,

        /// <summary>Everyone plays the hole at once; the next hole still waits for everyone.</summary>
        Free = 1
    }

    internal enum MatchPhase : byte
    {
        None = 0,
        Playing = 1,

        /// <summary>Every player finished hole 9 (or left): the results are final.</summary>
        Finished = 2
    }

    /// <summary>A player's result on one hole.</summary>
    internal enum HoleState : byte
    {
        /// <summary>Not finished (not started, or still playing it).</summary>
        Open = 0,
        Holed = 1,

        /// <summary>Reached the stroke limit without holing: scored as the limit.</summary>
        PickedUp = 2,

        /// <summary>Joined the match after this hole: scored as par + 2, like the game's unplayed holes.</summary>
        Missed = 3
    }

    /// <summary>One player of a <see cref="MatchState"/>.</summary>
    internal sealed class MatchPlayer
    {
        public MatchPlayer(PeerId id, int order, int joinedAtHole)
        {
            Id = id;
            Order = order;
            JoinedAtHole = joinedAtHole;
            for (int h = 0; h < joinedAtHole - 1 && h < MatchState.Holes; h++)
                States[h] = HoleState.Missed;
        }

        public PeerId Id { get; }

        /// <summary>Join order (0 = first); the tee order of hole 1 and the last tie-break.</summary>
        public int Order { get; }

        /// <summary>The hole this player started on (1 for everyone who was there at the start).</summary>
        public int JoinedAtHole { get; }

        public int[] Strokes { get; } = new int[MatchState.Holes];

        public HoleState[] States { get; } = new HoleState[MatchState.Holes];

        /// <summary>Metres from the ball to the cup on the current hole; NaN while unknown (not teed off, or moving).</summary>
        public float Distance { get; internal set; } = float.NaN;

        public bool Left { get; internal set; }

        public bool IsDone(int hole) => hole >= 1 && hole <= MatchState.Holes && States[hole - 1] != HoleState.Open;
    }

    /// <summary>A player's line on the leaderboard.</summary>
    internal sealed class MatchStanding
    {
        public MatchPlayer Player;

        /// <summary>1-based position; players level share it. 0 for a player who left.</summary>
        public int Position;

        public bool Tied;

        /// <summary>Holes finished (missed holes included).</summary>
        public int Thru;

        /// <summary>Strokes over the finished holes (a missed hole counts par + 2).</summary>
        public int Total;

        /// <summary>Strokes against par over the finished holes; null when a finished hole's par is unknown.</summary>
        public int? ToPar;
    }

    /// <summary>
    /// The competitive 9-hole round ("Just Play Nine" in a session): every player plays their own ball on the same hole,
    /// the host keeps the one true state and sends it to everyone (<c>ngg.pr.match</c>), the players report their
    /// strokes (<c>ngg.pr.mstroke</c>). Rules: stroke play; in <see cref="MatchMode.Turns"/> the tee order is by honours
    /// (best score on the previous hole first, ties going back hole by hole, then join order) and after the tee shots
    /// the player farthest from the cup plays; a player who reaches <see cref="MaxStrokes"/> without holing picks up;
    /// the next hole starts once everyone finished this one. Contains no Unity types (unit tested).
    /// </summary>
    internal sealed class MatchState
    {
        public const int Holes = 9;
        public const int MaxPlayers = 16;
        public const int MissedOverPar = 2;
        public const int MaxStrokeLimit = 99;

        private readonly List<MatchPlayer> _players = new List<MatchPlayer>();
        private readonly int[] _pars = new int[Holes];

        public MatchState(uint matchId, MatchMode mode, int maxStrokes)
        {
            MatchId = matchId;
            Mode = mode;
            MaxStrokes = Math.Max(0, Math.Min(MaxStrokeLimit, maxStrokes));
        }

        public uint MatchId { get; }

        /// <summary>Bumped by the host on every change; a client keeps the newest.</summary>
        public uint Sequence { get; internal set; }

        public MatchMode Mode { get; }

        /// <summary>Strokes after which a player picks up (0 = no limit).</summary>
        public int MaxStrokes { get; }

        public MatchPhase Phase { get; internal set; } = MatchPhase.Playing;

        /// <summary>The hole being played, 1..9.</summary>
        public int Hole { get; internal set; } = 1;

        /// <summary>Whose shot it is (<see cref="MatchMode.Turns"/>); None in free play, between holes and at the end.</summary>
        public PeerId Turn { get; internal set; } = PeerId.None;

        public IReadOnlyList<MatchPlayer> Players => _players;

        public IReadOnlyList<int> Pars => _pars;

        public int Par(int hole) => hole >= 1 && hole <= Holes ? _pars[hole - 1] : 0;

        public void SetPar(int hole, int par)
        {
            if (hole >= 1 && hole <= Holes && par >= 0 && par <= PresenceWire.MaxPar)
                _pars[hole - 1] = par;
        }

        public MatchPlayer Get(PeerId id)
        {
            foreach (var player in _players)
            {
                if (player.Id == id)
                    return player;
            }
            return null;
        }

        /// <summary>Every player still in the match finished the current hole (and someone is still in).</summary>
        public bool HoleComplete
        {
            get
            {
                if (Phase != MatchPhase.Playing)
                    return false;
                bool any = false;
                foreach (var player in _players)
                {
                    if (player.Left)
                        continue;
                    any = true;
                    if (!player.IsDone(Hole))
                        return false;
                }
                return any;
            }
        }

        public int ActiveCount
        {
            get
            {
                int n = 0;
                foreach (var player in _players)
                {
                    if (!player.Left)
                        n++;
                }
                return n;
            }
        }

        // ---- Host rules --------------------------------------------------------------------------------------

        /// <summary>Host: a player joins (or comes back). A newcomer starts on the current hole.</summary>
        public bool AddPlayer(PeerId id)
        {
            if (!id.IsValid || Phase != MatchPhase.Playing)
                return false;
            var existing = Get(id);
            if (existing != null)
            {
                if (!existing.Left)
                    return false;
                existing.Left = false;
                RecomputeTurn();
                return Touch();
            }
            if (_players.Count >= MaxPlayers)
                return false;
            int order = 0;
            foreach (var player in _players)
                order = Math.Max(order, player.Order + 1);
            _players.Add(new MatchPlayer(id, order, Hole));
            RecomputeTurn();
            return Touch();
        }

        /// <summary>Host: a player left the session. Their row stays (marked) and the turn moves on.</summary>
        public bool RemovePlayer(PeerId id)
        {
            var player = Get(id);
            if (player == null || player.Left)
                return false;
            player.Left = true;
            RecomputeTurn();
            if (Phase == MatchPhase.Playing && ActiveCount == 0)
                Phase = MatchPhase.Finished;
            return Touch();
        }

        /// <summary>
        /// Host: a player's stroke count on <paramref name="hole"/> after a shot came to rest (or holed out).
        /// <paramref name="distance"/> is the ball's distance to the cup (negative or NaN: unknown). Reports for another
        /// hole, of a finished hole or of an unknown player are ignored (false).
        /// </summary>
        public bool ReportStroke(PeerId id, int hole, int strokes, float distance, bool holed)
        {
            var player = Get(id);
            if (Phase != MatchPhase.Playing || player == null || player.Left || hole != Hole || player.IsDone(hole))
                return false;
            if (strokes < 0 || strokes > MaxStrokeLimit || (holed && strokes < 1))
                return false;
            int i = hole - 1;
            player.Strokes[i] = strokes;
            player.Distance = holed ? 0f : distance >= 0f ? distance : float.NaN;
            if (holed)
                player.States[i] = HoleState.Holed;
            else if (MaxStrokes > 0 && strokes >= MaxStrokes)
            {
                player.Strokes[i] = MaxStrokes;
                player.States[i] = HoleState.PickedUp;
            }
            RecomputeTurn();
            return Touch();
        }

        /// <summary>
        /// Host: a player picks up on the current hole (they gave up on it, or their game cannot play it on): scored as
        /// their strokes so far + 1, at most the stroke limit.
        /// </summary>
        public bool PickUp(PeerId id)
        {
            var player = Get(id);
            if (Phase != MatchPhase.Playing || player == null || player.Left || player.IsDone(Hole))
                return false;
            int i = Hole - 1;
            int strokes = Math.Max(1, player.Strokes[i] + 1);
            if (MaxStrokes > 0)
                strokes = Math.Min(strokes, MaxStrokes);
            player.Strokes[i] = strokes;
            player.States[i] = HoleState.PickedUp;
            RecomputeTurn();
            return Touch();
        }

        /// <summary>Host: after <see cref="HoleComplete"/>, on to the next hole (or the end after hole 9).</summary>
        public bool AdvanceHole()
        {
            if (!HoleComplete)
                return false;
            if (Hole >= Holes)
            {
                Phase = MatchPhase.Finished;
                Turn = PeerId.None;
                return Touch();
            }
            Hole++;
            foreach (var player in _players)
                player.Distance = float.NaN;
            RecomputeTurn();
            return Touch();
        }

        private bool Touch()
        {
            Sequence++;
            return true;
        }

        private void RecomputeTurn()
        {
            if (Phase != MatchPhase.Playing || Mode == MatchMode.Free || HoleComplete)
            {
                Turn = PeerId.None;
                return;
            }
            Turn = NextToPlay();
        }

        /// <summary>
        /// Whose shot it is: first anyone who has not teed off, in <see cref="TeeOrder"/>; then the player farthest
        /// from the cup (unknown distance counts as farthest), ties in tee order.
        /// </summary>
        public PeerId NextToPlay()
        {
            var order = TeeOrder();
            MatchPlayer far = null;
            foreach (var player in order)
            {
                if (player.IsDone(Hole))
                    continue;
                if (player.Strokes[Hole - 1] == 0)
                    return player.Id;
                if (far == null || Farther(player.Distance, far.Distance))
                    far = player;
            }
            return far?.Id ?? PeerId.None;
        }

        private static bool Farther(float a, float b)
        {
            if (float.IsNaN(b))
                return false;
            if (float.IsNaN(a))
                return true;
            return a > b;
        }

        /// <summary>
        /// The tee order of the current hole for the players still in: honours (lower score on the previous hole
        /// first; level scores look at the hole before, and so on; a hole a player missed counts as worse than any
        /// score), then join order.
        /// </summary>
        public List<MatchPlayer> TeeOrder()
        {
            var list = new List<MatchPlayer>();
            foreach (var player in _players)
            {
                if (!player.Left)
                    list.Add(player);
            }
            int hole = Hole;
            list.Sort((a, b) => CompareHonours(a, b, hole));
            return list;
        }

        private int CompareHonours(MatchPlayer a, MatchPlayer b, int hole)
        {
            for (int h = hole - 1; h >= 1; h--)
            {
                int sa = HonourScore(a, h);
                int sb = HonourScore(b, h);
                if (sa != sb)
                    return sa.CompareTo(sb);
            }
            return a.Order.CompareTo(b.Order);
        }

        private static int HonourScore(MatchPlayer player, int hole)
        {
            var state = player.States[hole - 1];
            return state == HoleState.Holed || state == HoleState.PickedUp ? player.Strokes[hole - 1] : int.MaxValue;
        }

        // ---- Scores ------------------------------------------------------------------------------------------

        /// <summary>The strokes a finished hole counts (a missed hole par + 2); null while it is open.</summary>
        public int? HoleScore(MatchPlayer player, int hole)
        {
            if (player == null || hole < 1 || hole > Holes)
                return null;
            switch (player.States[hole - 1])
            {
                case HoleState.Holed:
                case HoleState.PickedUp:
                    return player.Strokes[hole - 1];
                case HoleState.Missed:
                    int par = _pars[hole - 1];
                    return par > 0 ? par + MissedOverPar : (int?)null;
                default:
                    return null;
            }
        }

        /// <summary>The leaderboard: players still in ranked by score against par (else strokes), then those who left.</summary>
        public List<MatchStanding> Standings()
        {
            var rows = new List<MatchStanding>();
            bool parsKnown = true;
            foreach (var player in _players)
            {
                var row = new MatchStanding { Player = player };
                int toPar = 0;
                bool known = true;
                for (int h = 1; h <= Holes; h++)
                {
                    if (!player.IsDone(h))
                        continue;
                    row.Thru++;
                    int par = _pars[h - 1];
                    int? score = HoleScore(player, h);
                    if (score == null)
                    {
                        // A missed hole with an unknown par: counts as nothing yet.
                        known = false;
                        continue;
                    }
                    row.Total += score.Value;
                    if (par > 0)
                        toPar += score.Value - par;
                    else
                        known = false;
                }
                row.ToPar = known ? toPar : (int?)null;
                if (!player.Left && !known && row.Thru > 0)
                    parsKnown = false;
                rows.Add(row);
            }
            bool byPar = parsKnown;
            rows.Sort((a, b) =>
            {
                if (a.Player.Left != b.Player.Left)
                    return a.Player.Left ? 1 : -1;
                int c = byPar ? (a.ToPar ?? 0).CompareTo(b.ToPar ?? 0) : a.Total.CompareTo(b.Total);
                if (c != 0)
                    return c;
                // Level: whoever has played more holes is ahead on the same score.
                c = b.Thru.CompareTo(a.Thru);
                return c != 0 ? c : a.Player.Order.CompareTo(b.Player.Order);
            });
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row.Player.Left)
                    continue;
                if (i > 0 && !rows[i - 1].Player.Left && Level(rows[i - 1], row, byPar))
                {
                    row.Position = rows[i - 1].Position;
                    row.Tied = true;
                    rows[i - 1].Tied = true;
                }
                else
                {
                    row.Position = i + 1;
                }
            }
            return rows;
        }

        private static bool Level(MatchStanding a, MatchStanding b, bool byPar) =>
            a.Thru == b.Thru && (byPar ? a.ToPar == b.ToPar : a.Total == b.Total);

        // ---- Text --------------------------------------------------------------------------------------------

        /// <summary>"E", "+3", "-2", or "-" when unknown.</summary>
        public static string ToParText(int? toPar)
        {
            if (toPar == null)
                return "-";
            if (toPar.Value == 0)
                return "E";
            return toPar.Value > 0
                ? "+" + toPar.Value.ToString(CultureInfo.InvariantCulture)
                : toPar.Value.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>"1", "T2", or "-" for a player who left.</summary>
        public static string PositionText(MatchStanding row)
        {
            if (row.Position <= 0)
                return "-";
            string n = row.Position.ToString(CultureInfo.InvariantCulture);
            return row.Tied ? "T" + n : n;
        }

        /// <summary>The golf name of a score on a hole: "Hole in one", "Eagle", "Birdie", "Par", "Bogey", "+3".</summary>
        public static string ScoreName(int strokes, int par)
        {
            if (strokes == 1)
                return "Hole in one";
            if (par <= 0)
                return strokes.ToString(CultureInfo.InvariantCulture);
            switch (strokes - par)
            {
                case -3: return "Albatross";
                case -2: return "Eagle";
                case -1: return "Birdie";
                case 0: return "Par";
                case 1: return "Bogey";
                case 2: return "Double bogey";
                case 3: return "Triple bogey";
            }
            int d = strokes - par;
            return d < 0 ? d.ToString(CultureInfo.InvariantCulture) : "+" + d.ToString(CultureInfo.InvariantCulture);
        }

        // ---- Copy (client mirror) ----------------------------------------------------------------------------

        internal void AddRaw(MatchPlayer player) => _players.Add(player);
    }
}
