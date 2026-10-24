using System.Collections.Generic;
using NGGMultiplayer.Core.Protocol;

namespace NGGMultiplayer.Game.Lifecycle
{
    /// <summary>
    /// The pure parts of fitting the host's run to a joining client (docs/GAME_INTEGRATION.md 5.3): the shared main
    /// track from the facts and the client's ball skin. The RunSave edits themselves live in the store.
    /// </summary>
    internal static class Personalization
    {
        public const string TrackKind = "track";
        public const string TrackKey = "main";

        /// <summary>Longest challenge set name read from the track fact.</summary>
        public const int MaxSetNameBytes = 128;

        /// <summary>Decodes the <c>track/main</c> fact (<c>str setName, vi index</c>); false when absent or malformed.</summary>
        public static bool TryDecodeTrack(byte[] value, out string setName, out int index)
        {
            setName = null;
            index = 0;
            if (value == null || value.Length == 0)
                return false;
            try
            {
                var reader = new NetReader(value);
                string name = reader.ReadString(MaxSetNameBytes);
                int at = reader.ReadVarInt();
                if (name.Length == 0 || at < 0)
                    return false;
                setName = name;
                index = at;
                return true;
            }
            catch (ProtocolException)
            {
                return false;
            }
        }

        public static byte[] EncodeTrack(string setName, int index)
        {
            var writer = new NetWriter(32);
            writer.WriteString(setName ?? string.Empty);
            writer.WriteVarInt(index);
            return writer.ToArray();
        }

        /// <summary>
        /// The ball skin a client uses in the host's session: its own choice when that ball exists and is unlocked
        /// in the host's or its own list, else the host's.
        /// </summary>
        public static int PickBall(int ownSelected, bool haveOwn, IList<bool> ownUnlocked, IList<bool> hostUnlocked, int hostSelected)
        {
            if (haveOwn && ownSelected >= 0 && (IsUnlocked(hostUnlocked, ownSelected) || IsUnlocked(ownUnlocked, ownSelected)))
                return ownSelected;
            return hostSelected;
        }

        private static bool IsUnlocked(IList<bool> list, int index) => list != null && index < list.Count && list[index];
    }
}
