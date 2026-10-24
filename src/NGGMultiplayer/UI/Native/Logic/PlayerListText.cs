using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NGGMultiplayer.UI.Native
{
    /// <summary>
    /// The in-game player list's text (TextMeshPro rich text), one line per player: the name, a HOST / (you) tag, what
    /// they are doing in the game's value colour (the pause screen's controls list uses #B4FFFD), and the ping.
    /// </summary>
    public static class PlayerListText
    {
        public const string ValueColor = "#B4FFFD";
        public const string HostColor = "#FFD27A";

        public static string Build(IReadOnlyList<MenuPlayer> players)
        {
            if (players == null || players.Count == 0)
                return string.Empty;
            var text = new StringBuilder(64 * players.Count);
            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (i > 0)
                    text.Append('\n');
                text.Append(Name(p.Name));
                if (p.IsHost)
                    text.Append(" <size=70%><color=").Append(HostColor).Append(">HOST</color></size>");
                if (p.IsLocal)
                    text.Append(" <size=70%><color=#FFFFFFAA>(you)</color></size>");
                if (!string.IsNullOrEmpty(p.Where))
                    text.Append("  <size=80%><color=").Append(ValueColor).Append('>').Append(Name(Capitalise(p.Where))).Append("</color></size>");
                if (!p.IsLocal && p.PingMs >= 0)
                    text.Append("  <size=65%><color=#FFFFFF99>").Append(p.PingMs.ToString(CultureInfo.InvariantCulture)).Append(" ms</color></size>");
            }
            return text.ToString();
        }

        /// <summary>Steam names (and bounty names) shown as typed: TMP would read "&lt;...&gt;" as tags.</summary>
        public static string Name(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
            if (text.IndexOf('<') < 0)
                return text;
            return "<noparse>" + text.Replace("</noparse>", "</ noparse>") + "</noparse>";
        }

        private static string Capitalise(string text) =>
            text.Length > 0 && char.IsLower(text[0]) ? char.ToUpperInvariant(text[0]) + text.Substring(1) : text;
    }
}
