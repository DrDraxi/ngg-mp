using System;
using NGGMultiplayer.Core.Diagnostics;
using NGGMultiplayer.Core.Save;

namespace NGGMultiplayer.Game
{
    /// <summary>
    /// Save backend of <see cref="NullGameAdapter"/>: progress in memory only (it never touches the player's save
    /// file), so sessions run end to end without syncing real progress. The game's backend is
    /// <see cref="GameSaveBridge"/>.
    /// </summary>
    public sealed class GameSaveBackend : ISaveBackend
    {
        private readonly InMemorySaveBackend _store = new InMemorySaveBackend();
        private bool _loggedSeam;

        public event Action<SaveDelta> LocalProgressChanged
        {
            add => _store.LocalProgressChanged += value;
            remove => _store.LocalProgressChanged -= value;
        }

        public bool SuppressDiskWrites
        {
            get => _store.SuppressDiskWrites;
            set => _store.SuppressDiskWrites = value;
        }

        public byte[] CaptureSnapshot()
        {
            LogSeamOnce();
            return _store.CaptureSnapshot();
        }

        public void ApplySnapshot(byte[] snapshot)
        {
            LogSeamOnce();
            _store.ApplySnapshot(snapshot);
        }

        public void BackupLocalSave() => _store.BackupLocalSave();

        public void RestoreLocalSave() => _store.RestoreLocalSave();

        public void ApplyDelta(SaveDelta delta) => _store.ApplyDelta(delta);

        private void LogSeamOnce()
        {
            if (_loggedSeam)
                return;
            _loggedSeam = true;
            NetLog.Warn("[Save] Game save hooks are not implemented yet; progress is synced in memory only.");
        }
    }
}
