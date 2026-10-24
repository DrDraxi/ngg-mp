using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Save;

namespace NGGMultiplayer.Game
{
    /// <summary>
    /// The session save's container and the player's own save files. Implemented by the Lifecycle module and
    /// installed with <see cref="GameSaveBridge.SetStore"/>. Called by Core's save sync through the bridge, on the
    /// main thread. See docs/GAME_INTEGRATION.md, "Host save = session save".
    /// </summary>
    public interface ISessionSaveStore
    {
        /// <summary>Host: the session save bundle (host save plus <paramref name="facts"/>). Must not change game state.</summary>
        byte[] CaptureBundle(FactTable facts);

        /// <summary>
        /// Client: parses and keeps the host's bundle in memory and returns its fact table. Throws on a bad bundle,
        /// leaving the previous session save in place. Never writes the player's save files.
        /// </summary>
        FactTable ApplyBundle(byte[] bundle);

        /// <summary>Client: protect the player's own save before the host's is applied. Throw to refuse the session.</summary>
        void BackupLocalSave();

        /// <summary>Client: the session ended; bring the player's own save back. Throw if that failed.</summary>
        void RestoreLocalSave();

        /// <summary>Core switched disk-write suppression (true on clients while the host's save is loaded).</summary>
        void OnSuppressDiskWritesChanged(bool suppress);
    }

    /// <summary>
    /// Turns the game's progress into facts and back. Implemented by the Progression module and installed with
    /// <see cref="GameSaveBridge.SetFactHandler"/>. Main thread only.
    /// </summary>
    public interface IFactHandler
    {
        /// <summary>Host: fills <paramref name="into"/> with every fact, from the live world when it is loaded, else from the save.</summary>
        void BuildFacts(FactTable into);

        /// <summary>Host: accept or refuse a client's claim. Refusing makes that client reload the host's save.</summary>
        bool ValidateClaim(PeerId from, SaveDelta claim);

        /// <summary>
        /// Applies one confirmed fact to the game: on the host an accepted claim, on a client a host change. The fact
        /// table already holds the new value. Throw only when the game could not be brought in line (Core resyncs).
        /// </summary>
        void ApplyFact(SaveDelta fact);

        /// <summary>Client: a snapshot replaced the whole table; re-apply when the world is ready.</summary>
        void OnFactsReplaced(FactTable facts);
    }
}
