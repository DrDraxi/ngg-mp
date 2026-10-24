using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace NGGMultiplayer.Testing
{
    /// <summary>One line of the command file, split into words (double quotes group words, # starts a comment).</summary>
    public sealed class HarnessCommand
    {
        private HarnessCommand(string line, string[] words)
        {
            Line = line;
            Words = words;
        }

        public string Line { get; }

        /// <summary>Never empty.</summary>
        public IReadOnlyList<string> Words { get; }

        /// <summary>The first word, lower case.</summary>
        public string Name => Words[0].ToLowerInvariant();

        public int Count => Words.Count;

        /// <summary>Word <paramref name="index"/>, or null past the end.</summary>
        public string Arg(int index) => index >= 0 && index < Words.Count ? Words[index] : null;

        /// <summary>Word <paramref name="index"/> lower case, or null past the end.</summary>
        public string ArgLower(int index) => Arg(index)?.ToLowerInvariant();

        /// <summary>The words from <paramref name="index"/> on, joined by single spaces (empty past the end).</summary>
        public string Rest(int index)
        {
            if (index >= Words.Count)
                return string.Empty;
            var sb = new StringBuilder();
            for (int i = index; i < Words.Count; i++)
            {
                if (sb.Length > 0)
                    sb.Append(' ');
                sb.Append(Words[i]);
            }
            return sb.ToString();
        }

        /// <summary>Null for a blank or comment-only line.</summary>
        public static HarnessCommand Parse(string line)
        {
            if (line == null)
                return null;
            var words = new List<string>();
            var word = new StringBuilder();
            bool quoted = false;
            bool hasWord = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (quoted)
                {
                    if (c == '"')
                        quoted = false;
                    else
                        word.Append(c);
                    continue;
                }
                if (c == '"')
                {
                    quoted = true;
                    hasWord = true;
                    continue;
                }
                if (c == '#' && !hasWord)
                    break;
                if (char.IsWhiteSpace(c))
                {
                    if (hasWord)
                        words.Add(word.ToString());
                    word.Clear();
                    hasWord = false;
                    continue;
                }
                word.Append(c);
                hasWord = true;
            }
            if (hasWord)
                words.Add(word.ToString());
            return words.Count == 0 ? null : new HarnessCommand(line.Trim(), words.ToArray());
        }

        public static bool TryParseInt(string text, out int value) =>
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

        public static bool TryParseFloat(string text, out float value) =>
            float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && !float.IsNaN(value) && !float.IsInfinity(value);

        /// <summary>on/off, true/false, yes/no, 1/0.</summary>
        public static bool TryParseSwitch(string text, out bool value)
        {
            switch (text?.ToLowerInvariant())
            {
                case "on": case "true": case "yes": case "1": value = true; return true;
                case "off": case "false": case "no": case "0": value = false; return true;
                default: value = false; return false;
            }
        }
    }

    /// <summary>
    /// Follows the command file: every complete line appended since the last read is a new command. Lines already in the
    /// file when the reader starts are old (from an earlier run) and skipped; a line without its newline yet waits for
    /// it; a file emptied (or deleted) by the tester is read again from its start. A file that got shorter but is not
    /// empty (an earlier line edited or deleted) is NOT run again: many commands change the host's real save, so the
    /// reader skips to its new end and reports it (<see cref="ShrankLastRead"/>). Reads with sharing, so an editor can
    /// keep the file open. Pure System.IO (unit tested); main thread only.
    /// </summary>
    public sealed class CommandFileReader
    {
        public const int MaxLineChars = 4096;
        public const int MaxBytesPerRead = 64 * 1024;

        private readonly string _path;
        private long _offset;
        private readonly List<byte> _partial = new List<byte>();
        private bool _started;

        public CommandFileReader(string path)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("A path is required.", nameof(path));
            _path = path;
        }

        public string Path => _path;

        public long Offset => _offset;

        /// <summary>
        /// True when the last <see cref="ReadNewLines"/> found the file shorter than what was read but not empty: its
        /// content was skipped, not run again.
        /// </summary>
        public bool ShrankLastRead { get; private set; }

        /// <summary>
        /// Starts following: creates the file when missing and skips what it holds now. With
        /// <paramref name="skipExisting"/> false the existing lines are read as commands too.
        /// </summary>
        public void Start(bool skipExisting = true)
        {
            string directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            if (!File.Exists(_path))
                File.WriteAllText(_path, string.Empty);
            _offset = skipExisting ? new FileInfo(_path).Length : 0;
            _partial.Clear();
            _started = true;
        }

        /// <summary>The complete lines appended since the last call (without line ends, blank ones included).</summary>
        public List<string> ReadNewLines()
        {
            var lines = new List<string>();
            ShrankLastRead = false;
            if (!_started)
                Start();
            if (!File.Exists(_path))
            {
                _offset = 0;
                _partial.Clear();
                return lines;
            }
            using (var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long length = stream.Length;
                if (length < _offset)
                {
                    _partial.Clear();
                    if (length > 0)
                    {
                        // Edited (a line deleted) or replaced: running it again would repeat commands that already
                        // changed the host's save. Follow from the new end.
                        _offset = length;
                        ShrankLastRead = true;
                        return lines;
                    }
                    // Emptied: whatever is appended next is new.
                    _offset = 0;
                }
                if (length == _offset)
                    return lines;
                stream.Seek(_offset, SeekOrigin.Begin);
                int toRead = (int)Math.Min(MaxBytesPerRead, length - _offset);
                var buffer = new byte[toRead];
                int read = 0;
                while (read < toRead)
                {
                    int n = stream.Read(buffer, read, toRead - read);
                    if (n <= 0)
                        break;
                    read += n;
                }
                _offset += read;
                for (int i = 0; i < read; i++)
                {
                    byte b = buffer[i];
                    if (b == (byte)'\n')
                    {
                        lines.Add(Decode(_partial));
                        _partial.Clear();
                    }
                    else if (_partial.Count < MaxLineChars * 4)
                    {
                        _partial.Add(b);
                    }
                }
            }
            return lines;
        }

        private static string Decode(List<byte> bytes)
        {
            int start = 0;
            // A UTF-8 byte order mark (Notepad) at the start of a line.
            if (bytes.Count >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                start = 3;
            string text = Encoding.UTF8.GetString(bytes.ToArray(), start, bytes.Count - start).TrimEnd('\r');
            return text.Length <= MaxLineChars ? text : text.Substring(0, MaxLineChars);
        }
    }

    /// <summary>Appends timestamped result lines to the results file (created on demand).</summary>
    public sealed class ResultWriter
    {
        private readonly string _path;
        private readonly Func<DateTime> _clock;

        public ResultWriter(string path, Func<DateTime> clock = null)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("A path is required.", nameof(path));
            _path = path;
            _clock = clock ?? (() => DateTime.Now);
        }

        public string Path => _path;

        public int LinesWritten { get; private set; }

        /// <summary>"[2026-10-20 15:38:11.250] text"; multi-line text is indented under its stamp.</summary>
        public static string Format(DateTime at, string text)
        {
            string stamp = "[" + at.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "] ";
            string body = (text ?? string.Empty).Replace("\r\n", "\n").TrimEnd('\n');
            return stamp + body.Replace("\n", "\n    ");
        }

        public void Write(string text)
        {
            string line = Format(_clock(), text) + Environment.NewLine;
            string directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            using (var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
                var bytes = new UTF8Encoding(false).GetBytes(line);
                stream.Write(bytes, 0, bytes.Length);
            }
            LinesWritten++;
        }
    }
}
