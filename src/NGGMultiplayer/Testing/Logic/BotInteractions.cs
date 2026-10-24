using System;
using System.Collections.Generic;
using NGGMultiplayer.Game.Progression;

namespace NGGMultiplayer.Testing
{
    /// <summary>
    /// The game's <c>InteractionType</c> as numbers (confirmed 0..21 in this order, answer #92), so the pure bot code
    /// can put the byte on the wire without the game assembly. The harness checks this table against the real enum
    /// at start and logs any mismatch. Also what money a real client reports after a granted interaction.
    /// </summary>
    public static class BotInteractions
    {
        public static readonly string[] Names =
        {
            "ContinueShot", "CollectCash", "StartBounty", "BuyClub", "Teleport", "GetCrowbar", "GetCard", "Padlock",
            "LMUGCTicket", "ElevatorDoorUpper", "ElevatorDoorLower", "ElevatorDown", "ElevatorUp", "RFID", "StartLMUGC",
            "BuyMulligan", "BuyNoWind", "CutServer", "OpenBunker", "BuyHoleSkip", "FindMissingBall", "TalkToLuke"
        };

        /// <summary>FindMissingBall pays a hard-coded 1000, not m_cashValue (answer #92).</summary>
        public const int MissingBallReward = 1000;

        private static readonly Dictionary<string, byte> ByName = BuildIndex();

        public static bool TryParse(string name, out byte value)
        {
            value = 0;
            if (string.IsNullOrEmpty(name))
                return false;
            if (byte.TryParse(name, out byte number))
            {
                if (number >= Names.Length)
                    return false;
                value = number;
                return true;
            }
            return ByName.TryGetValue(name, out value);
        }

        public static string NameOf(byte value) => value < Names.Length ? Names[value] : "#" + value;

        /// <summary>
        /// The bank change a real client's granted <c>Interact</c> makes, which it then reports with <c>ngg.pg.bank</c>
        /// (answers #35, #92, #137, #138): CollectCash +m_cashValue (the host substitutes its own value anyway);
        /// FindMissingBall +1000; BuyClub, LMUGCTicket and the three assists exactly one -m_cashValue when affordable
        /// (an unaffordable press makes ChangeBalance(0), which is not reported); GetCrowbar and GetCard change no money.
        /// Null when nothing is reported.
        /// </summary>
        public static int? ExpectedBankReport(byte interaction, int cashValue, int balance)
        {
            switch (NameOf(interaction))
            {
                case "CollectCash":
                    return cashValue > 0 ? cashValue : (int?)null;
                case "FindMissingBall":
                    return MissingBallReward;
                case "BuyClub":
                case "LMUGCTicket":
                case "BuyMulligan":
                case "BuyNoWind":
                case "BuyHoleSkip":
                    if (cashValue <= 0 || balance < cashValue)
                        return null;
                    return -cashValue;
                default:
                    return null;
            }
        }

        /// <summary>
        /// How the host arbitrates an interaction, as the Progression module's Interactions.KindOf does (6.7):
        /// CollectCash, FindMissingBall, the GetCrowbar / GetCard / LMUGCTicket pickups and the four purchases; everything
        /// else runs unshared. A claim of an unarbitrated type is refused by the host's RPC validator.
        /// </summary>
        public static ArbitrationKind KindOf(byte interaction)
        {
            switch (NameOf(interaction))
            {
                case "CollectCash": return ArbitrationKind.CollectCash;
                case "FindMissingBall": return ArbitrationKind.FindMissingBall;
                case "GetCrowbar":
                case "GetCard":
                case "LMUGCTicket": return ArbitrationKind.Pickup;
                case "BuyClub":
                case "BuyMulligan":
                case "BuyNoWind":
                case "BuyHoleSkip": return ArbitrationKind.Purchase;
                default: return ArbitrationKind.None;
            }
        }

        /// <summary>
        /// Taking it removes the object from the world, so a real client's diff then claims its SaveableObject inactive.
        /// Purchases leave the stand (whether the shop standees are SaveableObjects is a runtime check, answers #41).
        /// </summary>
        public static bool HidesObject(byte interaction) => ArbitrationKinds.IsPickup(KindOf(interaction));

        private static Dictionary<string, byte> BuildIndex()
        {
            var index = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < Names.Length; i++)
                index[Names[i]] = (byte)i;
            return index;
        }
    }
}
