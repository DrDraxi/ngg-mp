using System;
using System.Globalization;

namespace NGGMultiplayer.Core.Net
{
    /// <summary>Identifies a remote peer. On Steam this is the SteamID64.</summary>
    public readonly struct PeerId : IEquatable<PeerId>
    {
        public static readonly PeerId None = default;

        public readonly ulong Value;

        public PeerId(ulong value)
        {
            Value = value;
        }

        public bool IsValid => Value != 0;

        public bool Equals(PeerId other) => Value == other.Value;

        public override bool Equals(object obj) => obj is PeerId other && other.Value == Value;

        public override int GetHashCode() => Value.GetHashCode();

        public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);

        public static bool operator ==(PeerId a, PeerId b) => a.Value == b.Value;

        public static bool operator !=(PeerId a, PeerId b) => a.Value != b.Value;
    }
}
