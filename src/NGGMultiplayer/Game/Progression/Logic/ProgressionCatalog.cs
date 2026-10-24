using System;
using System.Collections.Generic;

namespace NGGMultiplayer.Game.Progression
{
    /// <summary>Names of the Progression module's RPCs (docs/GAME_INTEGRATION.md 2.2).</summary>
    public static class ProgressionRpc
    {
        /// <summary>Same as GameMessages.ProgressionPrefix (kept literal so this file stays free of plugin types).</summary>
        public const string Prefix = "ngg.pg.";

        public const string World = Prefix + "world";
        public const string Story = Prefix + "story";
        public const string StoryHost = Prefix + "storyHost";
        public const string Claim = Prefix + "claim";
        public const string Grant = Prefix + "grant";
        public const string Bank = Prefix + "bank";
        public const string BallUnlock = Prefix + "ballUnlock";
        public const string Note = Prefix + "note";
        public const string Digest = Prefix + "digest";
        public const string RoundScore = Prefix + "roundScore";
        public const string LmugcBest = Prefix + "lmugcBest";

        public const int MaxWorldIdBytes = 128;
        public const int MaxMethodBytes = 64;
        public const int MaxInteractIdBytes = 256;
        public const int MaxReasonBytes = 128;
        public const int MaxNoteBytes = 128;
    }

    /// <summary>Fact kinds (2.3) and which of them clients may claim.</summary>
    public static class FactKinds
    {
        public const string Track = "track";
        public const string Bounty = "bo";
        public const string DoNotReload = "dnr";
        public const string Easy = "easy";
        public const string RunBool = "rb";
        public const string RunInt = "ri";
        public const string RunHostInt = "rh";
        public const string Bank = "bank";
        public const string Obj = "obj";
        public const string PreHit = "pht";
        public const string Unlocks = "unl";
        public const string ScoreCard = "sc";
        public const string Seed = "seed";
        public const string Auth = "auth";
        public const string Money = "money";

        public const string TrackKey = "main";
        public const string UnlocksKey = "balls";
        public const string MoneyKey = "story";

        private static readonly HashSet<string> HostOnly = new HashSet<string>(StringComparer.Ordinal)
        {
            RunHostInt, Bank, Unlocks, ScoreCard, Seed, Auth, Money
        };

        private static readonly HashSet<string> Known = new HashSet<string>(StringComparer.Ordinal)
        {
            Track, Bounty, DoNotReload, Easy, RunBool, RunInt, RunHostInt, Bank, Obj, PreHit, Unlocks, ScoreCard,
            Seed, Auth, Money
        };

        public static bool IsKnown(string kind) => kind != null && Known.Contains(kind);

        /// <summary>Only the host reports these; a client claim of one is always refused.</summary>
        public static bool IsHostOnly(string kind) => kind != null && HostOnly.Contains(kind);

        /// <summary>Kinds stored for later use only (no game state to bring in line).</summary>
        public static bool IsStoredOnly(string kind) => kind == Seed || kind == Money;
    }

    /// <summary>World ops of <c>ngg.pg.world</c> (2.4).</summary>
    public enum WorldOp : byte
    {
        SetGates = 1,
        OpenDoor = 2,
        SlidingDoor = 3,
        ServerCut = 4,
        ServerRestore = 5,
        BunkerScreen = 6,
        DriverAvailable = 7,
        WedgeAvailable = 8,
        HybridAvailable = 9,
        TicketAvailable = 10,
        LmugcMachine = 11
    }

    public static class WorldOps
    {
        public const byte First = (byte)WorldOp.SetGates;
        public const byte Last = (byte)WorldOp.LmugcMachine;

        public static bool IsKnown(byte op) => op >= First && op <= Last;

        /// <summary>Ops whose id names the door or gate.</summary>
        public static bool HasId(WorldOp op) =>
            op == WorldOp.SetGates || op == WorldOp.OpenDoor || op == WorldOp.SlidingDoor || op == WorldOp.BunkerScreen;
    }

    /// <summary>The RunSave fields shared through <c>rb</c>, <c>ri</c> and <c>rh</c> facts (2.3, 6.5).</summary>
    public static class SharedRunFields
    {
        public const string ServerCut = "m_serverCut";
        public const string BountyAccessLevel = "bountyAccessLevel";

        public static readonly string[] Bools =
        {
            "m_stancePanelUnlocked", "m_practiceUnlocked", "m_aimUnlocked", "m_spinUnlocked", "m_windUnlocked",
            "m_ironUnlocked", "m_putterUnlocked", "m_driverUnlocked", "m_wedgeUnlocked", "m_hybridUnlocked",
            "m_cardUnlocked", ServerCut, "m_bunkerOpen", "m_canGolf", "m_golfUnlocked", "m_crowbarUnlocked",
            "m_ticketUnlocked", "m_bountiesRestored", "m_hitLuke", "m_hasReturnedAfterHittingLuke", "m_playTrueEnding",
            "m_seenTrueEnding", "m_ballAwarded", "m_foundFirstCash", "m_playedMulliganVideo", "m_playedNoWindVideo",
            "m_playedHoleSkipVideo", "m_playedCar2", "seenBulletproof", "finishedDemo", "completedBountyOnEasy"
        };

        /// <summary>Client-claimable ints; they only increase.</summary>
        public static readonly string[] Ints =
        {
            BountyAccessLevel, "lmugcStartMulligans", "lmugcStartNoWinds", "lmugcStartHoleSkips"
        };

        /// <summary>Host-only ints.</summary>
        public static readonly string[] HostInts = { "lmugcScoreBest", "lmugcScoreNoAssistBest" };

        public const int MaxBountyAccessLevel = 3;
        public const int MaxAssistCount = 10000;

        private static readonly HashSet<string> BoolSet = new HashSet<string>(Bools, StringComparer.Ordinal);
        private static readonly HashSet<string> IntSet = new HashSet<string>(Ints, StringComparer.Ordinal);
        private static readonly HashSet<string> HostIntSet = new HashSet<string>(HostInts, StringComparer.Ordinal);

        private static readonly Dictionary<string, string> Features = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "m_stancePanelUnlocked", "Stance" },
            { "m_practiceUnlocked", "Practice swings" },
            { "m_aimUnlocked", "Aim adjust" },
            { "m_spinUnlocked", "Spin" },
            { "m_windUnlocked", "Wind" },
            { "m_ironUnlocked", "Iron" },
            { "m_putterUnlocked", "Putter" },
            { "m_driverUnlocked", "Driver" },
            { "m_wedgeUnlocked", "Wedge" },
            { "m_hybridUnlocked", "Hybrid" },
            { "m_crowbarUnlocked", "Crowbar" },
            { "m_cardUnlocked", "Key card" },
            { "m_ticketUnlocked", "LMUGC ticket" },
            { "m_golfUnlocked", "Golf" }
        };

        public static bool IsSharedBool(string field) => field != null && BoolSet.Contains(field);

        public static bool IsSharedInt(string field) => field != null && IntSet.Contains(field);

        public static bool IsHostInt(string field) => field != null && HostIntSet.Contains(field);

        /// <summary>Bools a client may also turn back off (the server cut can be restored).</summary>
        public static bool IsBothWays(string field) => field == ServerCut;

        /// <summary>Player-facing name of an unlock worth a toast, or null.</summary>
        public static string FeatureName(string field) =>
            field != null && Features.TryGetValue(field, out var name) ? name : null;

        public static int MaxIntValue(string field) =>
            field == BountyAccessLevel ? MaxBountyAccessLevel : MaxAssistCount;
    }

    /// <summary>How each DialogueManager story method is replicated (6.10).</summary>
    public enum StoryClass : byte
    {
        /// <summary>The player's own (moves or locks the local player, its tees, its dialogue, its achievements).</summary>
        Local = 0,

        /// <summary>Shared world state, safe to run again: sent by name and replayed on the others.</summary>
        Replicate = 1,

        /// <summary>Story money (DeductMoney / ReturnMoney): only the host runs it, once per session state.</summary>
        HostExecuted = 2,

        /// <summary>Starts an ending (loads CREDITS for everyone): the host only.</summary>
        HostOnlyEnding = 3,

        /// <summary>Video, camera and UI only (Luke's clips, LookAt*): stays with the player in the dialogue.</summary>
        Presentation = 4,

        /// <summary>
        /// Shared state, but running it again would repeat work that is not idempotent (a second wind cycle, a set
        /// load, a bounty): never sent by name; its results arrive as facts and world ops.
        /// </summary>
        FactDriven = 5
    }

    /// <summary>
    /// The DialogueManager methods Invoked by name (DialogueLine.invokeStart/invokeEnd, Challenge.functionOnStart /
    /// functionOnComplete), classified from the decompiled game (docs/confirm answers 38, 95, 96).
    /// </summary>
    public static class StoryMethods
    {
        public const string DeductMoney = "DeductMoney";
        public const string ReturnMoney = "ReturnMoney";
        public const string TryTrueEnding = "TryTrueEnding";
        public const string EndLukeFadeDown = "EndLukeFadeDown";
        public const string UnlockWind = "UnlockWind";
        public const string UnlockTeleporter = "UnlockTeleporter";
        public const string ReturnLukePrefix = "ReturnLuke";

        /// <summary>
        /// Shared world state that is safe to run again on a peer that already has it: gates, doors and triggers
        /// (SetActive / SetGates), feature unlocks (idempotent apart from their PFX, which replays never show),
        /// bounty tiers and the shop stock (whose non-idempotent PFX the repeat guard keeps to one per world).
        /// </summary>
        public static readonly string[] Replicate =
        {
            "HideCar", "CloseGates", "OpenGates", "UnlockStance", "UnlockPractice", "UnlockSpin", "UnlockAdjust",
            "CompletedDemo", "OpenShop", "OpenHole1Gates", "OpenHole2Gates", "UnlockBounties1", "UnlockBounties2",
            "UnlockBounties3", "DriverAvailable", "WedgeAvailable", "HybridAvailable", "TicketAvailable", "UnlockLMUGC",
            "TicketReady", "EnableTrap", "OpenBarDoor", "EnableTopOfMountain", "OpenMarketingDept", "RestoreBounties"
        };

        /// <summary>
        /// UnlockWind: StartWind starts one more WindCycle on every call; the <c>rb/m_windUnlocked</c> applier starts
        /// it once. UnlockTeleporter: loads a challenge set, adds a completed bounty, completes the cross-country pad
        /// and opens two doors; those arrive as the track / bo facts and world ops.
        /// </summary>
        public static readonly string[] FactDriven = { UnlockWind, UnlockTeleporter };

        /// <summary>The player's own: teleports, its tees, its dialogue and its achievements.</summary>
        public static readonly string[] PlayerLocal =
        {
            "TeleportCarpark", "TeleportMD", "TeleportMDTrap", "StartTeleporterDialogue", "EnableGreenTeeShort",
            "EnableGreenTeeLong", "EnableRedTeeForMultiShot", "EnableWedgeTutorial2Tee", "EnableWedgeTutorial3Tee",
            "EnableHybridTutorial2Tee", "PlayCarDialogue2", "ResetShot", "UnlockDemoPads"
        };

        /// <summary>Endings: the host's alone (clients follow it into CREDITS).</summary>
        public static readonly string[] HostOnlyEndings = { TryTrueEnding, EndLukeFadeDown };

        private static readonly string[] PresentationNames =
        {
            "ShowNullRef", "ShowConfettiNullRef", "ShowPowerRequirement", "HidePowerRequirement"
        };

        private static readonly string[] PresentationPrefixes = { "LookAt", "Luke", "EndLuke", ReturnLukePrefix };

        private static readonly HashSet<string> ReplicateSet = new HashSet<string>(Replicate, StringComparer.Ordinal);
        private static readonly HashSet<string> FactDrivenSet = new HashSet<string>(FactDriven, StringComparer.Ordinal);
        private static readonly HashSet<string> EndingSet = new HashSet<string>(HostOnlyEndings, StringComparer.Ordinal);
        private static readonly HashSet<string> PresentationSet = new HashSet<string>(PresentationNames, StringComparer.Ordinal);

        /// <summary>The class of a method name. Unknown names are <see cref="StoryClass.Local"/>.</summary>
        public static StoryClass Classify(string method)
        {
            if (string.IsNullOrEmpty(method))
                return StoryClass.Local;
            if (ReplicateSet.Contains(method))
                return StoryClass.Replicate;
            if (method == DeductMoney || method == ReturnMoney)
                return StoryClass.HostExecuted;
            // Before the Luke prefixes: EndLukeFadeDown is the true ending, not one of Luke's clips.
            if (EndingSet.Contains(method))
                return StoryClass.HostOnlyEnding;
            if (FactDrivenSet.Contains(method))
                return StoryClass.FactDriven;
            if (PresentationSet.Contains(method))
                return StoryClass.Presentation;
            foreach (var prefix in PresentationPrefixes)
            {
                if (method.Length > prefix.Length && method.StartsWith(prefix, StringComparison.Ordinal)
                    && IsClipSuffix(method, prefix.Length, prefix == "LookAt"))
                    return StoryClass.Presentation;
            }
            return StoryClass.Local;
        }

        public static bool IsReturnLuke(string method) =>
            method != null && method.Length > ReturnLukePrefix.Length
            && method.StartsWith(ReturnLukePrefix, StringComparison.Ordinal);

        // "Luke12", "EndLuke0", "ReturnLuke1a": a number, optionally followed by letters. LookAt* takes any name.
        private static bool IsClipSuffix(string method, int start, bool anyName)
        {
            if (anyName)
                return true;
            if (!char.IsDigit(method[start]))
                return false;
            for (int i = start; i < method.Length; i++)
            {
                if (!char.IsLetterOrDigit(method[i]))
                    return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Game calls that are not idempotent (docs/confirm answer 95): WindPanel.StartWind starts another WindCycle and
    /// re-rolls the wind on every call; ObjectStates' Set*Available / EnableLMUGCMachine spawn PFX and a sound each
    /// time. A network replay runs each of them at most once per loaded world.
    /// </summary>
    public static class RepeatRules
    {
        public const string StartWind = "StartWind";

        /// <summary>The ObjectStates shop-stock methods (world ops 7..11).</summary>
        public static readonly string[] ShopStock =
        {
            "SetDriverAvailable", "SetWedgeAvailable", "SetHybridAvailable", "SetTicketAvailable", "EnableLMUGCMachine"
        };

        /// <summary>
        /// Skip this call: in a session, caused by a network replay (or its hold window), and the method already ran
        /// in this world. The player's own first call, and anything offline, always runs.
        /// </summary>
        public static bool SkipRepeat(bool inSession, bool replaying, bool ranInThisWorld) =>
            inSession && replaying && ranInThisWorld;
    }

    /// <summary>Kinds of <c>ngg.pg.note</c>.</summary>
    public enum NoteKind : byte
    {
        Info = 0,
        Challenge = 1,
        Bounty = 2,
        Unlock = 3
    }

    /// <summary>What an arbitrated interaction is (6.7). Values are not on the wire.</summary>
    public enum ArbitrationKind : byte
    {
        None = 0,
        CollectCash,
        FindMissingBall,
        Pickup,
        Purchase
    }

    public static class ArbitrationKinds
    {
        public static bool IsPickup(ArbitrationKind kind) =>
            kind == ArbitrationKind.CollectCash || kind == ArbitrationKind.FindMissingBall || kind == ArbitrationKind.Pickup;

        /// <summary>
        /// How an <c>InteractionType</c> (by member name) is arbitrated (6.7, docs/confirm answers 92, 137, 138):
        /// CollectCash pays m_cashValue, FindMissingBall a fixed reward; GetCrowbar is a free pickup; BuyClub,
        /// LMUGCTicket and the three LMUGC assists cost m_cashValue. GetCard has no handler in Interact, so it is not
        /// arbitrated. Everything else stays local.
        /// </summary>
        public static ArbitrationKind ForInteraction(string interactionType)
        {
            switch (interactionType)
            {
                case "CollectCash": return ArbitrationKind.CollectCash;
                case "FindMissingBall": return ArbitrationKind.FindMissingBall;
                case "GetCrowbar": return ArbitrationKind.Pickup;
                case "BuyClub":
                case "LMUGCTicket":
                case "BuyMulligan":
                case "BuyNoWind":
                case "BuyHoleSkip": return ArbitrationKind.Purchase;
                default: return ArbitrationKind.None;
            }
        }

        /// <summary>
        /// Purchases that are gone once bought (a club, the LMUGC ticket), unlike the LMUGC assists, which can be
        /// bought again.
        /// </summary>
        public static bool IsOneOffPurchase(string interactionType) =>
            interactionType == "BuyClub" || interactionType == "LMUGCTicket";
    }
}
