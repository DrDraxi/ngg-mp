using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using NGGMultiplayer.Core.Net;

namespace NGGMultiplayer.Game.Presence
{
    /// <summary>Text of the competitive round's HUD and notices (no Unity types; unit tested).</summary>
    internal static class MatchText
    {
        /// <summary>"Alice", "Alice and Bob", "Alice, Bob and Carol".</summary>
        public static string JoinNames(IReadOnlyList<string> names)
        {
            if (names == null || names.Count == 0)
                return string.Empty;
            if (names.Count == 1)
                return names[0];
            var text = new StringBuilder();
            for (int i = 0; i < names.Count; i++)
            {
                if (i > 0)
                    text.Append(i == names.Count - 1 ? " and " : ", ");
                text.Append(names[i]);
            }
            return text.ToString();
        }

        /// <summary>After a hole: "Hole 3: Alice birdie, Bob par. Alice leads at -1."</summary>
        public static string HoleSummary(MatchState match, int hole, Func<PeerId, string> name)
        {
            var text = new StringBuilder();
            text.Append("Hole ").Append(hole.ToString(CultureInfo.InvariantCulture)).Append(": ");
            int par = match.Par(hole);
            bool first = true;
            foreach (var player in match.Players)
            {
                if (player.Left || player.States[hole - 1] == HoleState.Open || player.States[hole - 1] == HoleState.Missed)
                    continue;
                if (!first)
                    text.Append(", ");
                first = false;
                text.Append(name(player.Id)).Append(' ');
                if (player.States[hole - 1] == HoleState.PickedUp)
                    text.Append("picked up");
                else
                    text.Append(MatchState.ScoreName(player.Strokes[hole - 1], par).ToLowerInvariant());
            }
            text.Append('.');
            var standings = match.Standings();
            if (standings.Count > 1 && standings[0].Position == 1 && !standings[0].Player.Left)
            {
                if (standings[0].Tied)
                    text.Append(" All square at the top, ").Append(MatchState.ToParText(standings[0].ToPar)).Append('.');
                else
                    text.Append(' ').Append(name(standings[0].Player.Id)).Append(" leads at ")
                        .Append(MatchState.ToParText(standings[0].ToPar)).Append('.');
            }
            return text.ToString();
        }

        /// <summary>The winner line of the results: "Alice wins at -2!", "Alice and Bob tie at E!".</summary>
        public static string WinnerLine(MatchState match, Func<PeerId, string> name)
        {
            var standings = match.Standings();
            var winners = new List<string>();
            int? toPar = null;
            int total = 0;
            foreach (var row in standings)
            {
                if (row.Position != 1)
                    break;
                winners.Add(name(row.Player.Id));
                toPar = row.ToPar;
                total = row.Total;
            }
            if (winners.Count == 0)
                return "Round over.";
            string score = toPar != null ? MatchState.ToParText(toPar) : total.ToString(CultureInfo.InvariantCulture);
            return winners.Count == 1
                ? winners[0] + " wins at " + score + "!"
                : JoinNames(winners) + " tie at " + score + "!";
        }

        /// <summary>
        /// The leaderboard lines (rich text, columns by &lt;pos&gt;): a heading "Hole 3 / Par 4", then "1  Alice  -1  thru 2",
        /// the player to play marked with a triangle, the local player's name in the highlight colour.
        /// </summary>
        public static string Leaderboard(MatchState match, PeerId local, Func<PeerId, string> name, int maxNameChars = 14)
        {
            var text = new StringBuilder();
            if (match.Phase == MatchPhase.Finished)
                text.Append("<b>Final</b>");
            else
                text.Append("<b>Hole ").Append(match.Hole.ToString(CultureInfo.InvariantCulture)).Append("</b>  Par ")
                    .Append(match.Par(match.Hole) > 0 ? match.Par(match.Hole).ToString(CultureInfo.InvariantCulture) : "-");
            foreach (var row in match.Standings())
            {
                var player = row.Player;
                text.Append('\n');
                bool turn = match.Turn == player.Id && match.Phase == MatchPhase.Playing;
                text.Append(turn ? "<color=#FFDD79>▶</color>" : " ");
                text.Append("<pos=1.1em>").Append(MatchState.PositionText(row));
                string shown = Clip(name(player.Id), maxNameChars);
                text.Append("<pos=2.9em>");
                if (player.Id == local)
                    text.Append("<color=#B4FFFD>").Append(shown).Append("</color>");
                else
                    text.Append(shown);
                text.Append("<pos=10.5em>");
                if (player.Left)
                {
                    text.Append("<color=#AAAAAA>out</color>");
                    continue;
                }
                text.Append(ScoreColour(row.ToPar)).Append(MatchState.ToParText(row.ToPar)).Append("</color>");
                text.Append("<pos=12.6em><size=75%>").Append(Thru(match, row)).Append("</size>");
            }
            return text.ToString();
        }

        /// <summary>
        /// The match as plain text for the test harness and the log: "match 7 Turns hole 3/9 (par 3) turn Alice",
        /// then one line per player "1 Alice -1 thru 2 [3,4,-,...]".
        /// </summary>
        public static string Plain(MatchState match, PeerId local, Func<PeerId, string> name)
        {
            if (match == null)
                return "no match";
            var text = new StringBuilder();
            text.Append("match ").Append(match.MatchId.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(match.Mode)
                .Append(' ').Append(match.Phase).Append(" hole ").Append(match.Hole.ToString(CultureInfo.InvariantCulture))
                .Append("/9 (par ").Append(match.Par(match.Hole).ToString(CultureInfo.InvariantCulture)).Append(") turn ")
                .Append(match.Turn.IsValid ? name(match.Turn) : "-").Append(" seq ")
                .Append(match.Sequence.ToString(CultureInfo.InvariantCulture));
            if (match.HoleComplete)
                text.Append(" hole-complete");
            foreach (var row in match.Standings())
            {
                var player = row.Player;
                text.Append('\n').Append(MatchState.PositionText(row)).Append(' ').Append(name(player.Id));
                if (player.Id == local)
                    text.Append(" (you)");
                text.Append(' ').Append(player.Left ? "out" : MatchState.ToParText(row.ToPar)).Append(' ').Append(Thru(match, row))
                    .Append(" total ").Append(row.Total.ToString(CultureInfo.InvariantCulture)).Append(" [");
                for (int h = 0; h < MatchState.Holes; h++)
                {
                    if (h > 0)
                        text.Append(',');
                    switch (player.States[h])
                    {
                        case HoleState.Open:
                            text.Append(player.Strokes[h] > 0 ? "~" + player.Strokes[h].ToString(CultureInfo.InvariantCulture) : "-");
                            break;
                        case HoleState.PickedUp:
                            text.Append(player.Strokes[h].ToString(CultureInfo.InvariantCulture)).Append('*');
                            break;
                        case HoleState.Missed:
                            text.Append('m');
                            break;
                        default:
                            text.Append(player.Strokes[h].ToString(CultureInfo.InvariantCulture));
                            break;
                    }
                }
                text.Append(']');
                if (!float.IsNaN(player.Distance))
                    text.Append(" ").Append(player.Distance.ToString("0.0", CultureInfo.InvariantCulture)).Append("m from the cup");
            }
            if (match.Phase == MatchPhase.Finished)
                text.Append('\n').Append(WinnerLine(match, name));
            return text.ToString();
        }

        /// <summary>"F" once every hole is done, "thru 3", or "-" before the first hole is done; "on 4: 2" mid hole.</summary>
        public static string Thru(MatchState match, MatchStanding row)
        {
            if (row.Thru >= MatchState.Holes)
                return "F";
            int strokes = match.Phase == MatchPhase.Playing ? row.Player.Strokes[match.Hole - 1] : 0;
            if (match.Phase == MatchPhase.Playing && !row.Player.IsDone(match.Hole) && strokes > 0)
                return "shot " + strokes.ToString(CultureInfo.InvariantCulture);
            return row.Thru == 0 ? "-" : "thru " + row.Thru.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Under par red like a real leaderboard, over par grey-blue, level white.</summary>
        public static string ScoreColour(int? toPar)
        {
            if (toPar == null || toPar.Value == 0)
                return "<color=#FFFFFF>";
            return toPar.Value < 0 ? "<color=#FF6B6B>" : "<color=#9EC9FF>";
        }

        /// <summary>
        /// The turn banner for <paramref name="local"/>: "Alice's turn", "Waiting for Bob to finish hole 3", "Your
        /// shot" (<paramref name="mine"/> set), or null when there is nothing to say (no match, not in it, free play
        /// with the hole still open, the round over).
        /// </summary>
        public static string Banner(MatchState match, PeerId local, Func<PeerId, string> name, out bool mine)
        {
            mine = false;
            var me = match?.Get(local);
            if (match == null || me == null || me.Left || match.Phase != MatchPhase.Playing)
                return null;
            if (me.IsDone(match.Hole))
            {
                var waiting = new List<string>();
                foreach (var player in match.Players)
                {
                    if (!player.Left && !player.IsDone(match.Hole))
                        waiting.Add(name(player.Id));
                }
                return waiting.Count == 0
                    ? "Hole " + match.Hole.ToString(CultureInfo.InvariantCulture) + " done. Next tee coming up"
                    : "Waiting for " + JoinNames(waiting) + " to finish hole " + match.Hole.ToString(CultureInfo.InvariantCulture);
            }
            if (match.Mode == MatchMode.Free)
                return null;
            if (match.Turn == local)
            {
                mine = true;
                return me.Strokes[match.Hole - 1] == 0 ? "Your shot: tee off on hole " + match.Hole.ToString(CultureInfo.InvariantCulture) : "Your shot";
            }
            return match.Turn.IsValid ? Possessive(name(match.Turn)) + " turn" : null;
        }

        /// <summary>"Alice's", "James'".</summary>
        public static string Possessive(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "Their";
            return name.EndsWith("s", StringComparison.OrdinalIgnoreCase) ? name + "'" : name + "'s";
        }

        public static string Clip(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value))
                return "?";
            // No rich text from player names.
            value = value.Replace("<", "˂").Replace(">", "˃");
            return value.Length <= maxChars ? value : value.Substring(0, Math.Max(1, maxChars - 1)) + "…";
        }
    }
}
