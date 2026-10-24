using System;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Save;
using NGGMultiplayer.Core.Session;
using NGGMultiplayer.Core.Sync;

namespace NGGMultiplayer.Core
{
    /// <summary>Configuration for <see cref="NetSession"/>. Module options are read each time a session starts.</summary>
    public sealed class NetSessionOptions
    {
        /// <summary>Handshake, timeouts and display name. Owned by the <see cref="SessionManager"/>; null uses defaults.</summary>
        public SessionConfig Session { get; set; }

        public SaveSyncOptions Save { get; set; }

        public NetEntityRegistryOptions Entities { get; set; }

        /// <summary>Host: decides whether a client's progress claim is applied. Null accepts every well-formed claim.</summary>
        public Func<PeerId, SaveDelta, bool> ClaimValidator { get; set; }

        /// <summary>
        /// Host: a client receives entities and RPCs only after it has loaded the host's save, which keeps the game
        /// from spawning objects into a world that still shows the client's own progress. On by default.
        /// </summary>
        public bool GateGameplayOnSaveSync { get; set; } = true;

        internal void Validate()
        {
            Session?.Validate();
            Save?.Validated();
            Entities?.Validated();
        }
    }
}
