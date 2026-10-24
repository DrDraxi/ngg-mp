using System;
using System.Collections.Generic;
using NGGMultiplayer.Core;
using NGGMultiplayer.Core.Session;

namespace NGGMultiplayer.Runtime
{
    /// <summary>
    /// Stands in when the real service cannot even be constructed (for example the game's Steamworks.NET does not
    /// resolve), so the window still opens and explains why multiplayer is off instead of the plugin dying.
    /// </summary>
    public sealed class UnavailableMultiplayerService : IMultiplayerService
    {
        public UnavailableMultiplayerService(string reason)
        {
            UnavailableReason = string.IsNullOrEmpty(reason) ? "Multiplayer could not start." : reason;
        }

        public MultiplayerState State => MultiplayerState.Unavailable;

        public bool IsAvailable => false;

        public string UnavailableReason { get; }

        public string StatusMessage => string.Empty;

        public bool IsHost => false;

        public ulong LobbyId => 0;

        public IReadOnlyList<PlayerInfo> Players => Array.Empty<PlayerInfo>();

        public NetSession Net => null;

        public event Action<MultiplayerState> StateChanged
        {
            add { }
            remove { }
        }

        public bool Host() => false;

        public bool Join(ulong lobbyId) => false;

        public void Leave()
        {
        }

        public bool OpenInviteDialog() => false;

        public void ApplySettings(MultiplayerSettings settings)
        {
        }

        public void Tick(double now)
        {
        }

        public void Dispose()
        {
        }
    }
}
