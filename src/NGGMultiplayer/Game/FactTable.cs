using System;
using System.Collections.Generic;
using System.Text;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Save;

namespace NGGMultiplayer.Game
{
    /// <summary>
    /// The session's persistent progress as (Kind, Key) -> value facts, the unit Core's save sync moves around
    /// (<see cref="SaveDelta"/>). On the host it mirrors the host's game; on a client it holds what the host
    /// confirmed (snapshot plus deltas). Kinds and encodings are defined in docs/GAME_INTEGRATION.md.
    /// Every entry fits a <see cref="SaveDelta"/>. Deterministic encoding, so <see cref="ComputeCrc"/> works as a
    /// digest across peers. Main thread only; no Unity types (unit tested).
    /// </summary>
    public sealed class FactTable
    {
        public const int FormatVersion = 1;

        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, false);

        // Keyed by kind + "\0" + key; kinds never contain NUL.
        private readonly Dictionary<string, byte[]> _entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        public int Count => _entries.Count;

        public bool Contains(string kind, string key) => kind != null && key != null && _entries.ContainsKey(Composite(kind, key));

        /// <summary>Gets a copy of the value.</summary>
        public bool TryGet(string kind, string key, out byte[] value)
        {
            if (kind != null && key != null && _entries.TryGetValue(Composite(kind, key), out var stored))
            {
                value = (byte[])stored.Clone();
                return true;
            }
            value = null;
            return false;
        }

        /// <summary>A copy of the value, or null when absent.</summary>
        public byte[] Get(string kind, string key) => TryGet(kind, key, out var value) ? value : null;

        /// <summary>Stores a copy of <paramref name="value"/> (null is stored as empty). True if the table changed.</summary>
        /// <exception cref="ArgumentException">The fact would not fit a <see cref="SaveDelta"/>.</exception>
        public bool Set(string kind, string key, byte[] value)
        {
            Validate(kind, key, value);
            string composite = Composite(kind, key);
            byte[] copy = value == null || value.Length == 0 ? Array.Empty<byte>() : (byte[])value.Clone();
            if (_entries.TryGetValue(composite, out var existing) && ValueEquals(existing, copy))
                return false;
            _entries[composite] = copy;
            return true;
        }

        public bool Remove(string kind, string key) => kind != null && key != null && _entries.Remove(Composite(kind, key));

        public void Clear() => _entries.Clear();

        public void ReplaceWith(FactTable other)
        {
            if (ReferenceEquals(other, this))
                return;
            _entries.Clear();
            if (other == null)
                return;
            // Stored arrays are private copies that are never mutated, so they can be shared.
            foreach (var pair in other._entries)
                _entries.Add(pair.Key, pair.Value);
        }

        public FactTable Clone()
        {
            var copy = new FactTable();
            copy.ReplaceWith(this);
            return copy;
        }

        /// <summary>All entries in ordinal (Kind, Key) order, as copies. <paramref name="kind"/> filters when not null.</summary>
        public List<SaveDelta> GetEntries(string kind = null)
        {
            var keys = new List<string>(_entries.Count);
            string prefix = kind == null ? null : kind + "\0";
            foreach (var composite in _entries.Keys)
            {
                if (prefix == null || composite.StartsWith(prefix, StringComparison.Ordinal))
                    keys.Add(composite);
            }
            keys.Sort(StringComparer.Ordinal);
            var result = new List<SaveDelta>(keys.Count);
            foreach (var composite in keys)
            {
                int split = composite.IndexOf('\0');
                result.Add(new SaveDelta(composite.Substring(0, split), composite.Substring(split + 1),
                    (byte[])_entries[composite].Clone()));
            }
            return result;
        }

        /// <summary>The keys stored under <paramref name="kind"/>, in ordinal order.</summary>
        public List<string> KeysOf(string kind)
        {
            var result = new List<string>();
            if (kind == null)
                return result;
            string prefix = kind + "\0";
            foreach (var composite in _entries.Keys)
            {
                if (composite.StartsWith(prefix, StringComparison.Ordinal))
                    result.Add(composite.Substring(prefix.Length));
            }
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        /// <summary>varuint format, varuint count, then per entry in ordinal order: string kind, string key, bytes value.</summary>
        public void Serialize(NetWriter writer)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));
            var entries = GetEntries();
            writer.WriteVarUInt(FormatVersion);
            writer.WriteVarUInt((uint)entries.Count);
            foreach (var entry in entries)
            {
                writer.WriteString(entry.Kind);
                writer.WriteString(entry.Key);
                writer.WriteBytes(entry.Value);
            }
        }

        public byte[] ToArray()
        {
            var writer = new NetWriter(64 + _entries.Count * 48);
            Serialize(writer);
            return writer.ToArray();
        }

        /// <summary>Reads a table written by <see cref="Serialize"/>. Throws <see cref="ProtocolException"/> on bad input.</summary>
        public static FactTable Deserialize(NetReader reader)
        {
            if (reader == null)
                throw new ArgumentNullException(nameof(reader));
            uint version = reader.ReadVarUInt();
            if (version != FormatVersion)
                throw new ProtocolException($"Unsupported fact table format {version}.");
            uint count = reader.ReadVarUInt();
            // Each entry takes at least 3 bytes, which bounds a hostile count before anything is allocated.
            if (count > (uint)reader.Remaining / 3)
                throw new ProtocolException($"Fact table claims {count} entries in {reader.Remaining} bytes.");
            var table = new FactTable();
            for (uint i = 0; i < count; i++)
            {
                string kind = reader.ReadString(SaveDelta.MaxKindBytes);
                if (kind.Length == 0 || kind.IndexOf('\0') >= 0)
                    throw new ProtocolException("Fact has an invalid kind.");
                string key = reader.ReadString(SaveDelta.MaxKeyBytes);
                byte[] value = reader.ReadBytes(SaveDelta.MaxValueBytes);
                string composite = Composite(kind, key);
                if (table._entries.ContainsKey(composite))
                    throw new ProtocolException($"Duplicate fact {kind}/{key}.");
                table._entries.Add(composite, value);
            }
            return table;
        }

        /// <summary>Reads a whole buffer written by <see cref="ToArray"/>; trailing bytes are an error.</summary>
        public static FactTable FromArray(byte[] data)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            var reader = new NetReader(data);
            var table = Deserialize(reader);
            if (!reader.IsAtEnd)
                throw new ProtocolException($"Fact table has {reader.Remaining} trailing bytes.");
            return table;
        }

        /// <summary>CRC-32 of the deterministic encoding; equal tables give equal digests on every peer.</summary>
        public uint ComputeCrc() => Crc32.Compute(ToArray());

        // ---- Value encodings shared by every module (little-endian, as NetWriter) ----------------------

        public static byte[] EncodeBool(bool value) => new[] { value ? (byte)1 : (byte)0 };

        public static bool DecodeBool(byte[] value, bool fallback = false) =>
            value == null || value.Length == 0 ? fallback : value[0] != 0;

        public static byte[] EncodeInt(int value) =>
            new[] { (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24) };

        public static int DecodeInt(byte[] value, int fallback = 0) =>
            value == null || value.Length < 4 ? fallback : value[0] | (value[1] << 8) | (value[2] << 16) | (value[3] << 24);

        public static byte[] EncodeString(string value) => Utf8.GetBytes(value ?? string.Empty);

        public static string DecodeString(byte[] value) => value == null || value.Length == 0 ? string.Empty : Utf8.GetString(value);

        public bool GetBool(string kind, string key, bool fallback = false) =>
            _entries.TryGetValue(Composite(kind, key), out var v) ? DecodeBool(v, fallback) : fallback;

        public int GetInt(string kind, string key, int fallback = 0) =>
            _entries.TryGetValue(Composite(kind, key), out var v) ? DecodeInt(v, fallback) : fallback;

        public string GetString(string kind, string key, string fallback = "") =>
            _entries.TryGetValue(Composite(kind, key), out var v) ? DecodeString(v) : fallback;

        public static bool ValueEquals(byte[] a, byte[] b)
        {
            int lengthA = a?.Length ?? 0;
            int lengthB = b?.Length ?? 0;
            if (lengthA != lengthB)
                return false;
            for (int i = 0; i < lengthA; i++)
            {
                if (a[i] != b[i])
                    return false;
            }
            return true;
        }

        private static void Validate(string kind, string key, byte[] value)
        {
            var delta = new SaveDelta(kind, key, value);
            if (!delta.IsWellFormed)
                throw new ArgumentException($"Invalid fact {kind ?? "null"}/{key ?? "null"}.");
            if (!delta.IsWithinLimits)
                throw new ArgumentException($"Fact {delta} exceeds the SaveDelta limits.");
        }

        private static string Composite(string kind, string key) => kind + "\0" + key;
    }
}
