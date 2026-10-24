using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NGGMultiplayer.Testing
{
    /// <summary>File names of the harness's screenshots: "yyyyMMdd-HHmmss_name.png" with a name safe on every OS.</summary>
    public static class ScreenshotNames
    {
        public const string DefaultName = "shot";
        public const int MaxNameChars = 64;

        private static readonly HashSet<string> Reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        /// <summary>
        /// ASCII letters, digits, '-', '_' and '.' are kept (other characters, path separators and spaces become '_'; runs
        /// of them, and "..", one '_'); no leading or trailing '.' or '_'; a ".png" the tester typed is dropped; at most
        /// <see cref="MaxNameChars"/> characters; Windows device names get a '_' prefix; empty becomes "shot".
        /// </summary>
        public static string Sanitize(string name)
        {
            string text = (name ?? string.Empty).Trim();
            if (text.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                text = text.Substring(0, text.Length - 4);
            var sb = new StringBuilder(text.Length);
            bool lastWasFill = false;
            foreach (char c in text)
            {
                bool keep = c < 128 && (char.IsLetterOrDigit(c) || c == '-' || c == '.' || c == '_');
                if (keep && c != '_')
                {
                    sb.Append(c);
                    lastWasFill = false;
                }
                else if (!lastWasFill)
                {
                    sb.Append('_');
                    lastWasFill = true;
                }
            }
            string result = CollapseSeparators(sb.ToString()).Trim('_', '.');
            if (result.Length > MaxNameChars)
                result = result.Substring(0, MaxNameChars).TrimEnd('_', '.');
            if (result.Length == 0)
                return DefaultName;
            int dot = result.IndexOf('.');
            if (Reserved.Contains(dot >= 0 ? result.Substring(0, dot) : result))
                result = "_" + result;
            return result;
        }

        // A run of '.' and '_' longer than one character ("..", "_.._") becomes one '_'; a single '.' stays.
        private static string CollapseSeparators(string text)
        {
            var sb = new StringBuilder(text.Length);
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (c != '.' && c != '_')
                {
                    sb.Append(c);
                    i++;
                    continue;
                }
                int end = i;
                while (end < text.Length && (text[end] == '.' || text[end] == '_'))
                    end++;
                sb.Append(end - i == 1 ? c : '_');
                i = end;
            }
            return sb.ToString();
        }

        /// <summary>"20261020-153811_main_menu.png" (local time of the test PC).</summary>
        public static string FileName(DateTime localTime, string name) =>
            localTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "_" + Sanitize(name) + ".png";

        /// <summary>"name-2.png" etc.: the <paramref name="attempt"/>th alternative when the file already exists.</summary>
        public static string Numbered(string fileName, int attempt)
        {
            if (attempt <= 1)
                return fileName;
            string stem = fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? fileName.Substring(0, fileName.Length - 4) : fileName;
            return stem + "-" + attempt.ToString(CultureInfo.InvariantCulture) + ".png";
        }
    }

    /// <summary>
    /// Key names of the <c>key</c> command: Input System <c>Key</c> member names (case-insensitive) plus a few
    /// everyday aliases, and the matching legacy <c>KeyCode</c> name (what BepInEx's key reading uses when the game
    /// has the legacy input backend).
    /// </summary>
    public static class KeyNames
    {
        private static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "esc", "Escape" }, { "return", "Enter" }, { "spacebar", "Space" }, { "del", "Delete" }, { "ins", "Insert" },
            { "bksp", "Backspace" }, { "back", "Backspace" }, { "pgup", "PageUp" }, { "pgdn", "PageDown" },
            { "up", "UpArrow" }, { "down", "DownArrow" }, { "left", "LeftArrow" }, { "right", "RightArrow" },
            { "ctrl", "LeftCtrl" }, { "control", "LeftCtrl" }, { "leftcontrol", "LeftCtrl" }, { "rightcontrol", "RightCtrl" },
            { "shift", "LeftShift" }, { "alt", "LeftAlt" }, { "win", "LeftMeta" }, { "windows", "LeftMeta" },
            { "menu", "ContextMenu" }, { "print", "PrintScreen" }, { "backquote", "Backquote" }, { "tilde", "Backquote" },
            { "alpha0", "Digit0" }, { "alpha1", "Digit1" }, { "alpha2", "Digit2" }, { "alpha3", "Digit3" }, { "alpha4", "Digit4" },
            { "alpha5", "Digit5" }, { "alpha6", "Digit6" }, { "alpha7", "Digit7" }, { "alpha8", "Digit8" }, { "alpha9", "Digit9" }
        };

        // Input System name -> KeyCode name where they differ.
        private static readonly Dictionary<string, string> Legacy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Enter", "Return" }, { "LeftCtrl", "LeftControl" }, { "RightCtrl", "RightControl" },
            { "LeftMeta", "LeftWindows" }, { "RightMeta", "RightWindows" }, { "ContextMenu", "Menu" },
            { "Backquote", "BackQuote" }, { "PrintScreen", "Print" }, { "NumLock", "Numlock" },
            { "NumpadEnter", "KeypadEnter" }, { "NumpadDivide", "KeypadDivide" }, { "NumpadMultiply", "KeypadMultiply" },
            { "NumpadPlus", "KeypadPlus" }, { "NumpadMinus", "KeypadMinus" }, { "NumpadPeriod", "KeypadPeriod" },
            { "NumpadEquals", "KeypadEquals" }
        };

        /// <summary>
        /// The Input System Key name for <paramref name="text"/>: an alias resolved, "7" as Digit7, a letter upper case,
        /// "f7" as F7; anything else unchanged (the caller parses it against the real enum). Null for blank text.
        /// </summary>
        public static string Normalize(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;
            text = text.Trim();
            if (Aliases.TryGetValue(text, out string alias))
                return alias;
            if (text.Length == 1 && text[0] >= '0' && text[0] <= '9')
                return "Digit" + text;
            if (text.Length == 1 && char.IsLetter(text[0]))
                return text.ToUpperInvariant();
            if ((text[0] == 'f' || text[0] == 'F') && text.Length <= 3
                && int.TryParse(text.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out int f) && f >= 1 && f <= 24)
                return "F" + f.ToString(CultureInfo.InvariantCulture);
            return text;
        }

        /// <summary>The legacy KeyCode name of an Input System Key name ("Digit7" -> "Alpha7", "Enter" -> "Return").</summary>
        public static string LegacyName(string key)
        {
            if (string.IsNullOrEmpty(key))
                return key;
            if (Legacy.TryGetValue(key, out string legacy))
                return legacy;
            if (key.StartsWith("Digit", StringComparison.OrdinalIgnoreCase) && key.Length == 6)
                return "Alpha" + key.Substring(5);
            if (key.StartsWith("Numpad", StringComparison.OrdinalIgnoreCase) && key.Length == 7 && char.IsDigit(key[6]))
                return "Keypad" + key.Substring(6);
            return key;
        }
    }

    /// <summary>
    /// Keys the harness pressed, for the plugin's own key reading when it does not see Input System events (BepInEx's
    /// legacy backend): down in the frame the press takes effect, held until the release frame. Keys are compared
    /// case-insensitively by name. Main thread only.
    /// </summary>
    public sealed class InjectedKeyState
    {
        private readonly Dictionary<string, Entry> _keys = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        private struct Entry
        {
            public int Down;
            public int Up; // -1 while held
        }

        public bool Any => _keys.Count > 0;

        /// <summary>The key goes down in <paramref name="frame"/>.</summary>
        public void Press(string key, int frame)
        {
            if (!string.IsNullOrEmpty(key))
                _keys[key] = new Entry { Down = frame, Up = -1 };
        }

        /// <summary>The key is up again from <paramref name="frame"/> (at least one frame after it went down).</summary>
        public void Release(string key, int frame)
        {
            if (string.IsNullOrEmpty(key) || !_keys.TryGetValue(key, out var entry))
                return;
            entry.Up = Math.Max(frame, entry.Down + 1);
            _keys[key] = entry;
        }

        /// <summary>True only in the frame the key went down.</summary>
        public bool IsDown(string key, int frame) =>
            !string.IsNullOrEmpty(key) && _keys.TryGetValue(key, out var entry) && entry.Down == frame;

        public bool IsHeld(string key, int frame) =>
            !string.IsNullOrEmpty(key) && _keys.TryGetValue(key, out var entry) && frame >= entry.Down && (entry.Up < 0 || frame < entry.Up);

        /// <summary>Forgets keys released before <paramref name="frame"/>.</summary>
        public void Prune(int frame)
        {
            List<string> done = null;
            foreach (var pair in _keys)
            {
                if (pair.Value.Up >= 0 && pair.Value.Up < frame)
                    (done ?? (done = new List<string>())).Add(pair.Key);
            }
            if (done == null)
                return;
            foreach (var key in done)
                _keys.Remove(key);
        }
    }

    /// <summary>The outcome of <see cref="UiMatcher.Match"/>.</summary>
    public sealed class UiMatch
    {
        public UiMatch(UiElement chosen, IReadOnlyList<UiElement> candidates, string how)
        {
            Chosen = chosen;
            Candidates = candidates ?? Array.Empty<UiElement>();
            How = how ?? string.Empty;
        }

        /// <summary>The element to click; null when nothing or more than one matched.</summary>
        public UiElement Chosen { get; }

        /// <summary>Every element of the best tier that matched (the ambiguous ones when <see cref="Chosen"/> is null).</summary>
        public IReadOnlyList<UiElement> Candidates { get; }

        /// <summary>"exact label", "label contains", "path contains", or empty for no match.</summary>
        public string How { get; }
    }

    /// <summary>Labels and matching of the <c>buttons</c> / <c>click</c> commands.</summary>
    public static class UiMatcher
    {
        /// <summary>Rich-text tags removed ("&lt;b&gt;Play&lt;/b&gt;" -> "Play"), whitespace runs as one space, trimmed.</summary>
        public static string CleanLabel(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
            var sb = new StringBuilder(text.Length);
            bool space = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '<')
                {
                    int end = text.IndexOf('>', i + 1);
                    // A tag is "<word...>" without a space right after '<'; "a < b" stays text.
                    if (end > i + 1 && !char.IsWhiteSpace(text[i + 1]))
                    {
                        i = end;
                        continue;
                    }
                }
                if (char.IsWhiteSpace(c))
                {
                    space = sb.Length > 0;
                    continue;
                }
                if (space)
                    sb.Append(' ');
                space = false;
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Finds the element <paramref name="query"/> names, case-insensitively, in tiers: the whole label, then a part
        /// of the label, then a part of the hierarchy path. The first tier with a match decides: one element is chosen;
        /// several are ambiguous unless <paramref name="pick"/> (1-based) picks one of them.
        /// </summary>
        public static UiMatch Match(IReadOnlyList<UiElement> elements, string query, int pick = 0)
        {
            string q = CleanLabel(query);
            if (elements == null || q.Length == 0)
                return new UiMatch(null, null, string.Empty);
            var tiers = new[]
            {
                ("exact label", (Func<UiElement, bool>)(e => string.Equals(e.Label, q, StringComparison.OrdinalIgnoreCase))),
                ("label contains", e => e.Label.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0),
                ("path contains", e => e.Path.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
            };
            foreach (var (how, test) in tiers)
            {
                var found = new List<UiElement>();
                foreach (var element in elements)
                {
                    if (element != null && test(element))
                        found.Add(element);
                }
                if (found.Count == 0)
                    continue;
                if (pick > 0)
                    return new UiMatch(pick <= found.Count ? found[pick - 1] : null, found, how);
                return new UiMatch(found.Count == 1 ? found[0] : null, found, how);
            }
            return new UiMatch(null, null, string.Empty);
        }

        /// <summary>The <c>buttons</c> list: "N clickable element(s):" then one numbered line each.</summary>
        public static string Describe(IReadOnlyList<UiElement> elements)
        {
            int count = elements?.Count ?? 0;
            var sb = new StringBuilder();
            sb.Append(count).Append(" clickable element(s)").Append(count == 0 ? " (none active and interactable)" : ":");
            for (int i = 0; i < count; i++)
                sb.Append("\n  ").Append(i + 1).Append(". ").Append(elements[i]);
            return sb.ToString();
        }
    }
}
