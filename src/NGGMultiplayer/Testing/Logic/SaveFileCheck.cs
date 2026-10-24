using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace NGGMultiplayer.Testing
{
    /// <summary>One file (or pseudo-file such as a PlayerPrefs key) as it was when hashed.</summary>
    public readonly struct SaveFileState
    {
        public SaveFileState(string name, bool exists, long length, string sha256, DateTime lastWriteUtc)
        {
            Name = name;
            Exists = exists;
            Length = length;
            Sha256 = sha256 ?? string.Empty;
            LastWriteUtc = lastWriteUtc;
        }

        public readonly string Name;
        public readonly bool Exists;
        public readonly long Length;

        /// <summary>Lower-case hex; empty when the file does not exist or could not be read.</summary>
        public readonly string Sha256;

        public readonly DateTime LastWriteUtc;

        public bool SameContent(SaveFileState other) =>
            Exists == other.Exists && Length == other.Length && string.Equals(Sha256, other.Sha256, StringComparison.Ordinal);
    }

    public enum SaveFileChange : byte
    {
        Unchanged,
        Changed,
        Appeared,
        Vanished
    }

    /// <summary>The hashes of a set of save files at one moment.</summary>
    public sealed class SaveFileSnapshot
    {
        private readonly Dictionary<string, SaveFileState> _files = new Dictionary<string, SaveFileState>(StringComparer.OrdinalIgnoreCase);

        public SaveFileSnapshot(DateTime takenUtc, string label)
        {
            TakenUtc = takenUtc;
            Label = label ?? string.Empty;
        }

        public DateTime TakenUtc { get; }

        /// <summary>Why it was taken ("session start", "harness start").</summary>
        public string Label { get; }

        public IEnumerable<SaveFileState> Files => _files.Values;

        public int Count => _files.Count;

        public void Add(SaveFileState state) => _files[state.Name] = state;

        public bool TryGet(string name, out SaveFileState state) => _files.TryGetValue(name, out state);

        /// <summary>Hashes <paramref name="names"/> in <paramref name="directory"/> (missing files are recorded as missing).</summary>
        public static SaveFileSnapshot Take(string directory, IEnumerable<string> names, DateTime nowUtc, string label)
        {
            var snapshot = new SaveFileSnapshot(nowUtc, label);
            foreach (var name in names)
                snapshot.Add(HashFile(directory, name));
            return snapshot;
        }

        public static SaveFileState HashFile(string directory, string name)
        {
            string path = Path.Combine(directory ?? string.Empty, name);
            try
            {
                if (!File.Exists(path))
                    return new SaveFileState(name, false, 0, string.Empty, DateTime.MinValue);
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sha = SHA256.Create())
                {
                    long length = stream.Length;
                    string hash = ToHex(sha.ComputeHash(stream));
                    return new SaveFileState(name, true, length, hash, File.GetLastWriteTimeUtc(path));
                }
            }
            catch (IOException)
            {
                return new SaveFileState(name, true, -1, string.Empty, DateTime.MinValue);
            }
            catch (UnauthorizedAccessException)
            {
                return new SaveFileState(name, true, -1, string.Empty, DateTime.MinValue);
            }
        }

        /// <summary>A value that is not a file (a PlayerPrefs string, say) hashed the same way; null means absent.</summary>
        public static SaveFileState HashValue(string name, string value)
        {
            if (value == null)
                return new SaveFileState(name, false, 0, string.Empty, DateTime.MinValue);
            var bytes = Encoding.UTF8.GetBytes(value);
            using (var sha = SHA256.Create())
                return new SaveFileState(name, true, bytes.Length, ToHex(sha.ComputeHash(bytes)), DateTime.MinValue);
        }

        private static string ToHex(byte[] hash)
        {
            var sb = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash)
                sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }

    /// <summary>One file's verdict in a <see cref="SaveFileReport"/>.</summary>
    public readonly struct SaveFileVerdict
    {
        public SaveFileVerdict(string name, SaveFileChange change, bool expected, SaveFileState before, SaveFileState after)
        {
            Name = name;
            Change = change;
            Expected = expected;
            Before = before;
            After = after;
        }

        public readonly string Name;
        public readonly SaveFileChange Change;

        /// <summary>A change this peer's role allows (the host saving its own game; the player's own settings).</summary>
        public readonly bool Expected;

        public readonly SaveFileState Before;
        public readonly SaveFileState After;
    }

    /// <summary>
    /// What changed between two <see cref="SaveFileSnapshot"/>s. While this PC is a client nothing of the protected save
    /// may change (docs/GAME_INTEGRATION.md 5.5); while it hosts, the host's own saves change normally (SaveRun,
    /// door methods, AddScoreToCard), and the bots never write anything. Settings (config.txt, written by
    /// SaveGameSettings, and the PlayerPrefs "rebinds" key of RebindSaveLoad, answers #31/#71) are the player's own
    /// and may change in either role.
    /// </summary>
    public sealed class SaveFileReport
    {
        private readonly List<SaveFileVerdict> _verdicts = new List<SaveFileVerdict>();

        public IReadOnlyList<SaveFileVerdict> Verdicts => _verdicts;

        /// <summary>A file changed that must not (a protected file while a client).</summary>
        public bool HasViolation
        {
            get
            {
                foreach (var v in _verdicts)
                {
                    if (v.Change != SaveFileChange.Unchanged && !v.Expected)
                        return true;
                }
                return false;
            }
        }

        public int ChangedCount
        {
            get
            {
                int n = 0;
                foreach (var v in _verdicts)
                {
                    if (v.Change != SaveFileChange.Unchanged)
                        n++;
                }
                return n;
            }
        }

        /// <param name="settingsNames">Names that are the player's own settings (always expected to be writable).</param>
        /// <param name="hostWritesAllowed">This PC hosted: protected files may change from the host's own saves.</param>
        public static SaveFileReport Compare(SaveFileSnapshot before, SaveFileSnapshot after, ICollection<string> settingsNames,
            bool hostWritesAllowed)
        {
            if (before == null)
                throw new ArgumentNullException(nameof(before));
            if (after == null)
                throw new ArgumentNullException(nameof(after));
            var report = new SaveFileReport();
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in before.Files)
                names.Add(f.Name);
            foreach (var f in after.Files)
                names.Add(f.Name);
            foreach (var name in names)
            {
                bool hadBefore = before.TryGet(name, out var b);
                bool hasAfter = after.TryGet(name, out var a);
                if (!hadBefore)
                    b = new SaveFileState(name, false, 0, string.Empty, DateTime.MinValue);
                if (!hasAfter)
                    a = new SaveFileState(name, false, 0, string.Empty, DateTime.MinValue);
                SaveFileChange change;
                if (b.SameContent(a))
                    change = SaveFileChange.Unchanged;
                else if (!b.Exists)
                    change = SaveFileChange.Appeared;
                else if (!a.Exists)
                    change = SaveFileChange.Vanished;
                else
                    change = SaveFileChange.Changed;
                bool settings = settingsNames != null && Contains(settingsNames, name);
                report._verdicts.Add(new SaveFileVerdict(name, change, settings || hostWritesAllowed, b, a));
            }
            return report;
        }

        public string Describe()
        {
            var sb = new StringBuilder();
            foreach (var v in _verdicts)
            {
                if (sb.Length > 0)
                    sb.Append("; ");
                sb.Append(v.Name).Append(": ");
                switch (v.Change)
                {
                    case SaveFileChange.Unchanged:
                        sb.Append(v.After.Exists ? "unchanged" : "absent");
                        break;
                    case SaveFileChange.Changed:
                        sb.Append("CHANGED (").Append(v.Before.Length.ToString(CultureInfo.InvariantCulture)).Append(" -> ")
                            .Append(v.After.Length.ToString(CultureInfo.InvariantCulture)).Append(" bytes)");
                        break;
                    case SaveFileChange.Appeared:
                        sb.Append("APPEARED");
                        break;
                    default:
                        sb.Append("VANISHED");
                        break;
                }
                if (v.Change != SaveFileChange.Unchanged)
                    sb.Append(v.Expected ? " [allowed]" : " [VIOLATION]");
            }
            return sb.ToString();
        }

        private static bool Contains(ICollection<string> names, string name)
        {
            foreach (var n in names)
            {
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
