using System;
using System.Globalization;
using NGGMultiplayer.Core.Protocol;

namespace NGGMultiplayer.Core.Sync
{
    /// <summary>Session-wide entity id, allocated by the host. Zero is never a valid id.</summary>
    public readonly struct NetEntityId : IEquatable<NetEntityId>
    {
        public static readonly NetEntityId None = default;

        public readonly uint Value;

        public NetEntityId(uint value)
        {
            Value = value;
        }

        public bool IsValid => Value != 0;

        public static void Write(NetWriter writer, NetEntityId id) => writer.WriteVarUInt(id.Value);

        /// <summary>Reads an id and rejects zero.</summary>
        public static NetEntityId Read(NetReader reader)
        {
            uint value = reader.ReadVarUInt();
            if (value == 0)
                throw new ProtocolException("Entity id 0 is not valid.");
            return new NetEntityId(value);
        }

        public bool Equals(NetEntityId other) => Value == other.Value;

        public override bool Equals(object obj) => obj is NetEntityId other && Value == other.Value;

        public override int GetHashCode() => (int)Value;

        public static bool operator ==(NetEntityId a, NetEntityId b) => a.Value == b.Value;

        public static bool operator !=(NetEntityId a, NetEntityId b) => a.Value != b.Value;

        public override string ToString() => "#" + Value.ToString(CultureInfo.InvariantCulture);
    }
}
