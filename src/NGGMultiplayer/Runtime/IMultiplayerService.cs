using System;
using System.Collections.Generic;
using NGGMultiplayer.Core;
using NGGMultiplayer.Core.Session;

namespace NGGMultiplayer.Runtime
{
    public enum MultiplayerState
    {
        /// <summary>The platform backend (Steam) is not usable; see <see cref="IMultiplayerService.UnavailableReason"/>.</summary>
        Unavailable,
        Offline,
        CreatingLobby,
        JoiningLobby,
        /// <summary>In the lobby, waiting for the host to accept the handshake.</summary>
        Connecting,
        /// <summary>Accepted by the host; loading the host's save.</summary>
        Syncing,
        InSession
    }

    /// <summary>
    /// What the UI and the frame driver see of multiplayer: hosting, joining and leaving, independent of the
    /// platform behind it. Main thread only.
    /// </summary>
    public interface IMultiplayerService : IDisposable
    {
        MultiplayerState State { get; }

        bool IsAvailable { get; }

        /// <summary>Why <see cref="State"/> is <see cref="MultiplayerState.Unavailable"/>; empty otherwise.</summary>
        string UnavailableReason { get; }

        /// <summary>The latest notable event (session ended, join failed) for display.</summary>
        string StatusMessage { get; }

        bool IsHost { get; }

        /// <summary>The platform lobby id while in one, else 0.</summary>
        ulong LobbyId { get; }

        /// <summary>The roster of the running session; empty when offline.</summary>
        IReadOnlyList<PlayerInfo> Players { get; }

        /// <summary>The session facade, once the backend is up. Null while unavailable.</summary>
        NetSession Net { get; }

        event Action<MultiplayerState> StateChanged;

        /// <summary>Creates a lobby and hosts a session in it. False if not offline.</summary>
        bool Host();

        /// <summary>Joins a lobby and connects to its owner. False if not offline or the id is invalid.</summary>
        bool Join(ulong lobbyId);

        /// <summary>Leaves the session and lobby, or cancels a pending host/join. No-op when offline.</summary>
        void Leave();

        /// <summary>Opens the platform's invite dialog for the current lobby. False if there is none.</summary>
        bool OpenInviteDialog();

        /// <summary>Applies changed settings: session-level values at once, entity sync values from the next session.</summary>
        void ApplySettings(MultiplayerSettings settings);

        /// <summary>Runs one frame. <paramref name="now"/> is monotonic real time in seconds.</summary>
        void Tick(double now);
    }
}
