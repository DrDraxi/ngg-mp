using System;
using System.Collections.Generic;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Session;
using NGGMultiplayer.Game;

namespace NGGMultiplayer.Testing
{
    /// <summary>
    /// What the test harness's commands need from the running plugin and game. Implemented over the multiplayer service
    /// and the live game by <c>HarnessGame</c>; tests use a fake. Every member must be safe to call in any scene and
    /// state (no session, no world): it reports what it cannot see instead of throwing.
    /// </summary>
    public interface IHarnessGame
    {
        // ---- Multiplayer service ------------------------------------------------------------------------

        /// <summary>The service state (MultiplayerState name).</summary>
        string ServiceState { get; }

        bool InSession { get; }

        bool IsHosting { get; }

        PeerId LocalId { get; }

        PeerId HostId { get; }

        ulong LobbyId { get; }

        string StatusMessage { get; }

        IReadOnlyList<PlayerInfo> Players { get; }

        /// <summary>Host: the client loaded the save and gets gameplay traffic.</summary>
        bool IsPeerReady(PeerId peer);

        /// <summary>Creates a lobby and hosts; false with a reason when that cannot start.</summary>
        bool Host(out string message);

        void Leave();

        /// <summary>The settings a bot copies so the host admits it (game build, timeout, send rate).</summary>
        FakePeerOptions BotTemplate();

        // ---- Game ---------------------------------------------------------------------------------------

        /// <summary>The host's own golfer (feet, answers #55) when it stands in a loaded world.</summary>
        bool TryGetHostPose(out NetVector3 feet);

        /// <summary>Ground height under (x, z) in the host's world (fallback when nothing is hit). Null: none.</summary>
        Func<float, float, float, float> GroundHeight { get; }

        /// <summary>The session's fact table (the host's truth while hosting); null when the game integration is off.</summary>
        FactTable SessionFacts { get; }

        /// <summary>Scene, world readiness, pause and time scale, remote views (written into the open JSON object).</summary>
        void DescribeWorld(JsonBuilder json);

        /// <summary>The Lifecycle save guards (suppression, fence, protect/inject installed) into the open JSON object.</summary>
        void DescribeSaveGuard(JsonBuilder json);

        /// <summary>The host's live wind (WindPanel current and desired, forced wind) as one line.</summary>
        string DescribeWind();

        /// <summary>The real save folder (Application.persistentDataPath; never the redirected getters).</summary>
        string SaveDirectory { get; }

        /// <summary>A PlayerPrefs string (the game's "rebinds"), null when unset or unreadable.</summary>
        string ReadPlayerPref(string key);

        /// <summary>The interactable an arbitration id names in the host's world, as a bot's pickup claim.</summary>
        bool TryResolvePickup(string interactId, out PickupRequest request, out string error);

        /// <summary>A challenge set by clean name: whether it exists and is a bounty (warnings only; the host decides).</summary>
        bool TryGetChallengeSet(string name, out bool isBounty, out int challengeCount);

        /// <summary>Ids of <paramref name="what"/> (sets, bounties, pickups, doors, gates, saveables) in the host's world.</summary>
        IReadOnlyList<string> List(string what, out string error);

        /// <summary>Runs one runtime check (answers marked ?); "all" runs every one. One line per check.</summary>
        string RunCheck(string name);

        /// <summary>The checks <see cref="RunCheck"/> knows.</summary>
        IReadOnlyList<string> CheckNames { get; }
    }
}
