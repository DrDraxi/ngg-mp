using System;
using System.Text;
using NGGMultiplayer.Core.Protocol;

namespace NGGMultiplayer.Core.Save
{
    /// <summary>
    /// One unit of persistent progress, e.g. Kind "challenge", Key "hole7-par", Value the game's encoding of it.
    /// Applying a delta sets the value for (Kind, Key). Kind must be non-empty and contain no NUL character;
    /// Key may be empty. A null Value is sent as empty.
    /// </summary>
    public sealed class SaveDelta
    {
        public const int MaxKindBytes = 128;
        public const int MaxKeyBytes = 1024;
        public const int MaxValueBytes = 65536;

        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, false);

        public string Kind;
        public string Key;
        public byte[] Value;

        public SaveDelta()
        {
        }

        public SaveDelta(string kind, string key, byte[] value)
        {
            Kind = kind;
            Key = key;
            Value = value;
        }

        /// <summary>Structure only: Kind non-empty without NUL, Key non-null.</summary>
        public bool IsWellFormed => !string.IsNullOrEmpty(Kind) && Kind.IndexOf('\0') < 0 && Key != null;

        /// <summary>Encoded sizes fit the wire limits. Assumes <see cref="IsWellFormed"/>.</summary>
        public bool IsWithinLimits => IdentifiersWithinLimits && (Value == null || Value.Length <= MaxValueBytes);

        internal bool IdentifiersWithinLimits =>
            Utf8.GetByteCount(Kind ?? string.Empty) <= MaxKindBytes
            && Utf8.GetByteCount(Key ?? string.Empty) <= MaxKeyBytes;

        public bool IsValid => IsWellFormed && IsWithinLimits;

        /// <summary>Throws <see cref="ArgumentException"/> unless <see cref="IsValid"/>.</summary>
        public void Serialize(NetWriter writer)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));
            if (!IsWellFormed)
                throw new ArgumentException($"SaveDelta is malformed: {this}.");
            if (!IsWithinLimits)
                throw new ArgumentException($"SaveDelta exceeds wire limits: {this}.");
            writer.WriteString(Kind);
            writer.WriteString(Key);
            writer.WriteBytes(Value);
        }

        /// <summary>Reads a delta written by <see cref="Serialize"/>. Throws <see cref="ProtocolException"/> on bad input.</summary>
        public static SaveDelta Deserialize(NetReader reader)
        {
            if (reader == null)
                throw new ArgumentNullException(nameof(reader));
            string kind = reader.ReadString(MaxKindBytes);
            if (kind.Length == 0)
                throw new ProtocolException("SaveDelta kind is empty.");
            if (kind.IndexOf('\0') >= 0)
                throw new ProtocolException("SaveDelta kind contains NUL.");
            string key = reader.ReadString(MaxKeyBytes);
            byte[] value = reader.ReadBytes(MaxValueBytes);
            return new SaveDelta(kind, key, value);
        }

        public SaveDelta Clone() => new SaveDelta(Kind, Key, Value == null ? null : (byte[])Value.Clone());

        /// <summary>Same Kind and Key (ordinal) and same Value bytes; null and empty values are equal.</summary>
        public bool ContentEquals(SaveDelta other)
        {
            if (other == null)
                return false;
            return string.Equals(Kind, other.Kind, StringComparison.Ordinal)
                && string.Equals(Key, other.Key, StringComparison.Ordinal)
                && ValueEquals(Value, other.Value);
        }

        public override string ToString() => $"{Kind}/{Key} ({Value?.Length ?? 0} bytes)";

        /// <summary>Unambiguous dictionary key for (kind, key); relies on Kind never containing NUL.</summary>
        internal static string CompositeKey(string kind, string key) => kind + "\0" + key;

        internal static bool ValueEquals(byte[] a, byte[] b)
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
    }
}
