using System;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Save;
using NGGMultiplayer.Game;
using NGGMultiplayer.Game.Lifecycle;

namespace NGGMultiplayer.Testing
{
    /// <summary>
    /// A test bot's save: the host's session save bundle, kept in memory only. It never reads or writes a file (the
    /// bot has no save of its own to protect, and the real files on this PC belong to the host's game). Snapshots are
    /// parsed with the Lifecycle module's own <see cref="SessionSaveBundle"/> parser, so a bundle the bot accepts is one
    /// a real client would accept; deltas update <see cref="Facts"/> like a real client's fact table. The bot's
    /// claims are raised through <see cref="LocalProgressChanged"/>, which Core's save sync turns into the same
    /// ProgressClaim packets a real client sends. No Unity types (unit tested).
    /// </summary>
    public sealed class BotSaveBackend : ISaveBackend
    {
        private bool _suppressDiskWrites;

        /// <summary>The facts the host confirmed (snapshot plus deltas), as a real client's GameSaveBridge holds them.</summary>
        public FactTable Facts { get; } = new FactTable();

        /// <summary>The last bundle applied; null before the first snapshot and after the session.</summary>
        internal SessionSaveBundle Bundle { get; private set; }

        /// <summary>The raw bytes of the last snapshot (bundle or bare fact table).</summary>
        public byte[] SnapshotBytes { get; private set; }

        /// <summary>The snapshot was a full session save bundle (Lifecycle installed on the host), not a bare table.</summary>
        public bool HasBundle => Bundle != null;

        public int SnapshotsApplied { get; private set; }

        public int DeltasApplied { get; private set; }

        public int Backups { get; private set; }

        public int Restores { get; private set; }

        public int ClaimsRaised { get; private set; }

        /// <summary>Always zero: kept so a dump can show it.</summary>
        public int DiskWrites => 0;

        public event Action<SaveDelta> LocalProgressChanged;

        /// <summary>Raised after a confirmed delta was stored (kind, key).</summary>
        public event Action<SaveDelta> DeltaApplied;

        /// <summary>Tracked for the dump only; the bot never writes anything either way.</summary>
        public bool SuppressDiskWrites
        {
            get => _suppressDiskWrites;
            set => _suppressDiskWrites = value;
        }

        public byte[] CaptureSnapshot() =>
            throw new InvalidOperationException("A test bot never hosts a session.");

        /// <summary>
        /// Parses the host's snapshot before touching any state: a session save bundle when the host runs the Lifecycle
        /// module, otherwise the bare fact table <see cref="GameSaveBridge"/> sends without a store.
        /// </summary>
        public void ApplySnapshot(byte[] snapshot)
        {
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));
            SessionSaveBundle bundle = null;
            FactTable facts;
            try
            {
                bundle = SessionSaveBundle.Parse(snapshot);
                facts = bundle.FactTable;
            }
            catch (ProtocolException bundleError)
            {
                try
                {
                    facts = FactTable.FromArray(snapshot);
                }
                catch (ProtocolException)
                {
                    throw bundleError;
                }
            }
            Bundle = bundle;
            SnapshotBytes = (byte[])snapshot.Clone();
            Facts.ReplaceWith(facts);
            SnapshotsApplied++;
        }

        public void BackupLocalSave() => Backups++;

        public void RestoreLocalSave()
        {
            Restores++;
            Bundle = null;
            SnapshotBytes = null;
            Facts.Clear();
        }

        public void ApplyDelta(SaveDelta delta)
        {
            if (delta == null)
                throw new ArgumentNullException(nameof(delta));
            if (!delta.IsValid)
                throw new ArgumentException($"Invalid fact {delta}.", nameof(delta));
            Facts.Set(delta.Kind, delta.Key, delta.Value);
            DeltasApplied++;
            var handler = DeltaApplied;
            if (handler == null)
                return;
            try
            {
                handler(delta);
            }
            catch (Exception e)
            {
                Core.Diagnostics.NetLog.Error($"[TestHarness] DeltaApplied handler threw: {e}");
            }
        }

        /// <summary>
        /// The bot "recorded" progress: Core claims it to the host (only once the host's save is loaded, and only when
        /// it differs from what the host confirmed). False when the fact would not fit a SaveDelta.
        /// </summary>
        public bool Claim(string kind, string key, byte[] value)
        {
            var delta = new SaveDelta(kind, key ?? string.Empty, value ?? Array.Empty<byte>());
            if (!delta.IsValid)
                return false;
            ClaimsRaised++;
            LocalProgressChanged?.Invoke(delta);
            return true;
        }
    }
}
