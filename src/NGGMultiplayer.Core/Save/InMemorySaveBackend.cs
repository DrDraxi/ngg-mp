using System;
using System.Collections.Generic;
using NGGMultiplayer.Core.Protocol;

namespace NGGMultiplayer.Core.Save
{
    /// <summary>
    /// Reference <see cref="ISaveBackend"/>: progress is a map of (Kind, Key) to bytes. "Disk" is simulated by
    /// <see cref="PersistedSnapshot"/>, rewritten after every change unless <see cref="SuppressDiskWrites"/> is set,
    /// like a game that autosaves. <see cref="Set"/> plays the part of the game recording progress.
    /// </summary>
    public sealed class InMemorySaveBackend : ISaveBackend
    {
        public const int SnapshotFormatVersion = 1;

        // Keyed by SaveDelta.CompositeKey(kind, key).
        private Dictionary<string, byte[]> _entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        private Dictionary<string, byte[]> _backup;
        private bool _suppressDiskWrites;

        public event Action<SaveDelta> LocalProgressChanged;

        public int Count => _entries.Count;

        public bool HasBackup => _backup != null;

        /// <summary>The simulated save file; null until the first write.</summary>
        public byte[] PersistedSnapshot { get; private set; }

        public int DiskWriteCount { get; private set; }

        public int BackupCount { get; private set; }

        public int RestoreCount { get; private set; }

        public int ApplySnapshotCount { get; private set; }

        public int ApplyDeltaCount { get; private set; }

        public bool SuppressDiskWrites
        {
            get => _suppressDiskWrites;
            set => _suppressDiskWrites = value;
        }

        /// <summary>Records progress as the game would: stores it, autosaves, raises <see cref="LocalProgressChanged"/>.</summary>
        public void Set(string kind, string key, byte[] value)
        {
            var delta = new SaveDelta(kind, key, value);
            if (!delta.IsWellFormed || !delta.IdentifiersWithinLimits)
                throw new ArgumentException($"Invalid kind/key: {delta}.");
            Store(delta);
            Persist();
            LocalProgressChanged?.Invoke(delta.Clone());
        }

        public bool TryGet(string kind, string key, out byte[] value)
        {
            if (kind == null || key == null)
            {
                value = null;
                return false;
            }
            if (_entries.TryGetValue(SaveDelta.CompositeKey(kind, key), out var stored))
            {
                value = (byte[])stored.Clone();
                return true;
            }
            value = null;
            return false;
        }

        /// <summary>The stored value, or null when absent.</summary>
        public byte[] Get(string kind, string key) => TryGet(kind, key, out var value) ? value : null;

        /// <summary>All entries ordered by (Kind, Key), as copies.</summary>
        public List<SaveDelta> GetEntries()
        {
            var keys = new List<string>(_entries.Keys);
            keys.Sort(StringComparer.Ordinal);
            var result = new List<SaveDelta>(keys.Count);
            foreach (var composite in keys)
            {
                int split = composite.IndexOf('\0');
                result.Add(new SaveDelta(composite.Substring(0, split), composite.Substring(split + 1), (byte[])_entries[composite].Clone()));
            }
            return result;
        }

        /// <summary>
        /// Deterministic encoding: varuint format version, varuint count, then per entry in ordinal (Kind, Key)
        /// order: string kind, string key, bytes value. Values are not bound by <see cref="SaveDelta.MaxValueBytes"/>.
        /// </summary>
        public byte[] CaptureSnapshot()
        {
            var entries = GetEntries();
            var writer = new NetWriter(64 + entries.Count * 32);
            writer.WriteVarUInt(SnapshotFormatVersion);
            writer.WriteVarUInt((uint)entries.Count);
            foreach (var entry in entries)
            {
                writer.WriteString(entry.Kind);
                writer.WriteString(entry.Key);
                writer.WriteBytes(entry.Value);
            }
            return writer.ToArray();
        }

        /// <summary>Replaces all progress atomically. Throws <see cref="ProtocolException"/> on a bad snapshot, leaving state untouched.</summary>
        public void ApplySnapshot(byte[] snapshot)
        {
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));
            _entries = Parse(snapshot);
            ApplySnapshotCount++;
            Persist();
        }

        public void BackupLocalSave()
        {
            _backup = new Dictionary<string, byte[]>(_entries, StringComparer.Ordinal);
            BackupCount++;
        }

        public void RestoreLocalSave()
        {
            if (_backup == null)
                throw new InvalidOperationException("No local save was backed up.");
            _entries = _backup;
            _backup = null;
            RestoreCount++;
            Persist();
        }

        public void ApplyDelta(SaveDelta delta)
        {
            if (delta == null)
                throw new ArgumentNullException(nameof(delta));
            if (!delta.IsWellFormed)
                throw new ArgumentException($"Invalid delta: {delta}.", nameof(delta));
            Store(delta);
            ApplyDeltaCount++;
            Persist();
        }

        private void Store(SaveDelta delta)
        {
            // Stored arrays are private copies and never mutated, so backups can share them.
            _entries[SaveDelta.CompositeKey(delta.Kind, delta.Key)] =
                delta.Value == null || delta.Value.Length == 0 ? Array.Empty<byte>() : (byte[])delta.Value.Clone();
        }

        private void Persist()
        {
            if (_suppressDiskWrites)
                return;
            PersistedSnapshot = CaptureSnapshot();
            DiskWriteCount++;
        }

        private static Dictionary<string, byte[]> Parse(byte[] snapshot)
        {
            var reader = new NetReader(snapshot);
            uint version = reader.ReadVarUInt();
            if (version != SnapshotFormatVersion)
                throw new ProtocolException($"Unsupported snapshot format {version}.");
            uint count = reader.ReadVarUInt();
            // Each entry takes at least 4 bytes, which bounds a hostile count before anything is allocated.
            if (count > (uint)reader.Remaining / 4)
                throw new ProtocolException($"Snapshot claims {count} entries in {reader.Remaining} bytes.");

            var entries = new Dictionary<string, byte[]>((int)count, StringComparer.Ordinal);
            for (uint i = 0; i < count; i++)
            {
                string kind = reader.ReadString(SaveDelta.MaxKindBytes);
                if (kind.Length == 0 || kind.IndexOf('\0') >= 0)
                    throw new ProtocolException("Snapshot entry has an invalid kind.");
                string key = reader.ReadString(SaveDelta.MaxKeyBytes);
                byte[] value = reader.ReadBytes();
                string composite = SaveDelta.CompositeKey(kind, key);
                if (entries.ContainsKey(composite))
                    throw new ProtocolException($"Snapshot has duplicate entry {kind}/{key}.");
                entries.Add(composite, value);
            }
            if (!reader.IsAtEnd)
                throw new ProtocolException($"Snapshot has {reader.Remaining} trailing bytes.");
            return entries;
        }
    }
}
