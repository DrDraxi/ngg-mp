using System;

namespace NGGMultiplayer.Core.Save
{
    /// <summary>
    /// Game-side persistence seam. The plugin implements it over the game's save system;
    /// <see cref="InMemorySaveBackend"/> is the reference implementation. Called on the main thread only.
    /// </summary>
    public interface ISaveBackend
    {
        /// <summary>Host: serializes current progress. The format is opaque to the sync layer.</summary>
        byte[] CaptureSnapshot();

        /// <summary>
        /// Client: replaces in-memory progress with the host's. Must not write to disk while
        /// <see cref="SuppressDiskWrites"/> is set, and must not raise <see cref="LocalProgressChanged"/>.
        /// </summary>
        void ApplySnapshot(byte[] snapshot);

        /// <summary>Client: remembers the local save before the host's is applied.</summary>
        void BackupLocalSave();

        /// <summary>Client: reloads the save remembered by <see cref="BackupLocalSave"/>.</summary>
        void RestoreLocalSave();

        /// <summary>Raised when the local game records progress.</summary>
        event Action<SaveDelta> LocalProgressChanged;

        /// <summary>Applies a single change without a full reload. Must not raise <see cref="LocalProgressChanged"/>.</summary>
        void ApplyDelta(SaveDelta delta);

        /// <summary>True on clients while in a session, so the host's progress never overwrites the local save file.</summary>
        bool SuppressDiskWrites { get; set; }
    }
}
