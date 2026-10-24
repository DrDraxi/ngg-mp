using System;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Save;
using NGGMultiplayer.Core.Session;
using UnityEngine;

namespace NGGMultiplayer.Game
{
    /// <summary>
    /// Everything the plugin needs from Normal Golf Game itself. The networking layers only talk to the game
    /// through this seam; <see cref="NggGameAdapter"/> is the game's implementation.
    /// All members are called on Unity's main thread.
    /// </summary>
    public interface IGameAdapter : IDisposable
    {
        /// <summary>Game version sent in the handshake; hosts reject clients on a different build.</summary>
        string GameBuild { get; }

        /// <summary>The game's progress store. The host's is the session's source of truth.</summary>
        ISaveBackend SaveBackend { get; }

        /// <summary>The local golfer. False while there is none (menus, loading).</summary>
        bool TryGetLocalPlayerPose(out GamePose pose);

        /// <summary>The local player's ball. False while there is none.</summary>
        bool TryGetLocalBallPose(out GamePose pose);

        /// <summary>A visual for another player's golfer, parented by the caller. Null uses a placeholder.</summary>
        GameObject CreateRemotePlayerVisual(PlayerInfo player);

        /// <summary>A visual for another player's ball, parented by the caller. Null uses a placeholder.</summary>
        GameObject CreateRemoteBallVisual(PlayerInfo player);

        /// <summary>Host: accept or refuse a client's progress claim. Refusing makes that client reload the host's save.</summary>
        bool ValidateClaim(PeerId from, SaveDelta claim);

        /// <summary>The session is running and, on a client, the host's save is loaded.</summary>
        void OnSessionReady(bool isHost);

        /// <summary>The session ended; on a client the local save has been restored.</summary>
        void OnSessionEnded();
    }
}
