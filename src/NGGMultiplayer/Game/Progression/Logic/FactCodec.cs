using System;
using System.Collections.Generic;
using NGGMultiplayer.Core.Protocol;

namespace NGGMultiplayer.Game.Progression
{
    /// <summary>The shared main challenge track: a set name and the index of its first incomplete challenge.</summary>
    public readonly struct TrackValue : IEquatable<TrackValue>
    {
        public TrackValue(string set, int index)
        {
            Set = set ?? string.Empty;
            Index = index;
        }

        public readonly string Set;
        public readonly int Index;

        public bool Equals(TrackValue other) => string.Equals(Set, other.Set, StringComparison.Ordinal) && Index == other.Index;

        public override bool Equals(object obj) => obj is TrackValue other && Equals(other);

        public override int GetHashCode() => (Set?.GetHashCode() ?? 0) * 31 + Index;

        public override string ToString() => Set + "#" + Index;
    }

    /// <summary>A SaveableObject's shared state (<c>obj</c> facts).</summary>
    public readonly struct ObjState
    {
        public ObjState(bool active, bool hasRotation, NetQuaternion rotation)
        {
            Active = active;
            HasRotation = hasRotation;
            Rotation = hasRotation ? rotation : NetQuaternion.Identity;
        }

        public readonly bool Active;
        public readonly bool HasRotation;
        public readonly NetQuaternion Rotation;
    }

    /// <summary>
    /// Value encodings of the Progression facts (docs/GAME_INTEGRATION.md 2.3). Every decoder is total: it returns
    /// false on anything malformed (claims come from the wire), never throws. Deterministic encodings, so equal
    /// game states produce equal bytes on every peer.
    /// </summary>
    public static class FactCodec
    {
        public const byte BountyDone = 1;
        public const byte BountyOnEasy = 2;
        public const byte ObjActive = 1;
        public const byte ObjHasRotation = 2;

        public const int MaxSetNameBytes = 256;
        public const int MaxListEntries = 4096;
        public const int MaxListEntryBytes = 512;
        public const int MaxBoolListEntries = 1024;

        /// <summary>Rotations closer than this are the same state (float noise, quaternion sign).</summary>
        public const float RotationToleranceDegrees = 0.5f;

        // ---- track ----------------------------------------------------------------------------------

        public static byte[] EncodeTrack(TrackValue value)
        {
            var writer = new NetWriter(32);
            writer.WriteString(value.Set);
            writer.WriteVarInt(value.Index);
            return writer.ToArray();
        }

        public static bool TryDecodeTrack(byte[] value, out TrackValue track)
        {
            track = default;
            if (value == null || value.Length == 0)
                return false;
            try
            {
                var reader = new NetReader(value);
                string set = reader.ReadString(MaxSetNameBytes);
                int index = reader.ReadVarInt();
                if (!reader.IsAtEnd || index < 0 || set.Length == 0)
                    return false;
                track = new TrackValue(set, index);
                return true;
            }
            catch (ProtocolException)
            {
                return false;
            }
        }

        // ---- single-byte flags (bo, dnr, easy) ------------------------------------------------------

        public static byte[] EncodeFlags(byte flags) => new[] { flags };

        public static bool TryDecodeFlags(byte[] value, out byte flags)
        {
            flags = 0;
            if (value == null || value.Length != 1)
                return false;
            flags = value[0];
            return true;
        }

        public static bool IsValidBountyFlags(byte flags) => (flags & BountyDone) != 0 && (flags & ~(BountyDone | BountyOnEasy)) == 0;

        // ---- bool / int (exact length, unlike FactTable.Decode*, which fall back) --------------------

        public static bool TryDecodeBool(byte[] value, out bool result)
        {
            result = false;
            if (value == null || value.Length != 1 || value[0] > 1)
                return false;
            result = value[0] != 0;
            return true;
        }

        public static bool TryDecodeInt(byte[] value, out int result)
        {
            result = 0;
            if (value == null || value.Length != 4)
                return false;
            result = FactTable.DecodeInt(value);
            return true;
        }

        // ---- obj ------------------------------------------------------------------------------------

        public static byte[] EncodeObj(ObjState state)
        {
            var writer = new NetWriter(17);
            byte flags = (byte)((state.Active ? ObjActive : 0) | (state.HasRotation ? ObjHasRotation : 0));
            writer.WriteByte(flags);
            if (state.HasRotation)
                writer.WriteQuaternion(state.Rotation);
            return writer.ToArray();
        }

        public static bool TryDecodeObj(byte[] value, out ObjState state)
        {
            state = default;
            if (value == null || value.Length == 0)
                return false;
            byte flags = value[0];
            if ((flags & ~(ObjActive | ObjHasRotation)) != 0)
                return false;
            bool hasRotation = (flags & ObjHasRotation) != 0;
            if (value.Length != (hasRotation ? 17 : 1))
                return false;
            var rotation = NetQuaternion.Identity;
            if (hasRotation)
            {
                var reader = new NetReader(value, 1, 16);
                rotation = reader.ReadQuaternion();
                if (!rotation.IsFinite)
                    return false;
                float lengthSq = NetQuaternion.Dot(rotation, rotation);
                if (lengthSq < 0.25f || lengthSq > 4f)
                    return false;
            }
            state = new ObjState((flags & ObjActive) != 0, hasRotation, rotation);
            return true;
        }

        /// <summary>Angle in degrees between two rotations (sign-insensitive); 180 for non-finite input.</summary>
        public static float AngleDegrees(NetQuaternion a, NetQuaternion b)
        {
            var na = a.Normalized();
            var nb = b.Normalized();
            float dot = Math.Abs(NetQuaternion.Dot(na, nb));
            if (float.IsNaN(dot))
                return 180f;
            if (dot >= 1f)
                return 0f;
            return (float)(2.0 * Math.Acos(dot) * (180.0 / Math.PI));
        }

        public static bool ObjEquivalent(ObjState a, ObjState b) => ObjEquivalent(a, b, RotationToleranceDegrees);

        public static bool ObjEquivalent(ObjState a, ObjState b, float toleranceDegrees)
        {
            if (a.Active != b.Active || a.HasRotation != b.HasRotation)
                return false;
            return !a.HasRotation || AngleDegrees(a.Rotation, b.Rotation) <= toleranceDegrees;
        }

        // ---- string sets (pht) ----------------------------------------------------------------------

        /// <summary>Sorted (ordinal), de-duplicated, empty entries dropped.</summary>
        public static byte[] EncodeStringSet(IEnumerable<string> values)
        {
            var sorted = NormalizeSet(values);
            var writer = new NetWriter(16 + sorted.Count * 24);
            writer.WriteVarUInt((uint)sorted.Count);
            foreach (var value in sorted)
                writer.WriteString(value);
            return writer.ToArray();
        }

        public static bool TryDecodeStringSet(byte[] value, out List<string> values)
        {
            values = null;
            if (value == null || value.Length == 0)
                return false;
            try
            {
                var reader = new NetReader(value);
                uint count = reader.ReadVarUInt();
                if (count > MaxListEntries || count > (uint)reader.Remaining)
                    return false;
                var result = new List<string>((int)count);
                string previous = null;
                for (uint i = 0; i < count; i++)
                {
                    string entry = reader.ReadString(MaxListEntryBytes);
                    // The encoding is canonical: strictly increasing, no empty entries.
                    if (entry.Length == 0 || (previous != null && string.CompareOrdinal(previous, entry) >= 0))
                        return false;
                    result.Add(entry);
                    previous = entry;
                }
                if (!reader.IsAtEnd)
                    return false;
                values = result;
                return true;
            }
            catch (ProtocolException)
            {
                return false;
            }
        }

        public static List<string> NormalizeSet(IEnumerable<string> values)
        {
            var set = new SortedSet<string>(StringComparer.Ordinal);
            if (values != null)
            {
                foreach (var value in values)
                {
                    if (!string.IsNullOrEmpty(value))
                        set.Add(value);
                }
            }
            return new List<string>(set);
        }

        // ---- bool lists (unl) -----------------------------------------------------------------------

        public static byte[] EncodeBoolList(IList<bool> values)
        {
            int count = values?.Count ?? 0;
            var writer = new NetWriter(8 + count);
            writer.WriteVarUInt((uint)count);
            for (int i = 0; i < count; i++)
                writer.WriteBool(values[i]);
            return writer.ToArray();
        }

        public static bool TryDecodeBoolList(byte[] value, out List<bool> values)
        {
            values = null;
            if (value == null || value.Length == 0)
                return false;
            try
            {
                var reader = new NetReader(value);
                uint count = reader.ReadVarUInt();
                if (count > MaxBoolListEntries || count != (uint)reader.Remaining)
                    return false;
                var result = new List<bool>((int)count);
                for (uint i = 0; i < count; i++)
                {
                    byte b = reader.ReadByte();
                    if (b > 1)
                        return false;
                    result.Add(b != 0);
                }
                values = result;
                return true;
            }
            catch (ProtocolException)
            {
                return false;
            }
        }

        // ---- equivalence ----------------------------------------------------------------------------

        /// <summary>Same game state: bytes equal, or (obj) equal up to rotation noise.</summary>
        public static bool Equivalent(string kind, byte[] a, byte[] b)
        {
            if (FactTable.ValueEquals(a, b))
                return true;
            if (kind == FactKinds.Obj && TryDecodeObj(a, out var objA) && TryDecodeObj(b, out var objB))
                return ObjEquivalent(objA, objB);
            return false;
        }
    }
}
