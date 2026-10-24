using System;
using NGGMultiplayer.Core.Diagnostics;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Save;

namespace NGGMultiplayer.Game
{
    /// <summary>
    /// The plugin's <see cref="ISaveBackend"/>: joins Core's save sync to the game modules without either knowing
    /// the other.
    /// <list type="bullet">
    /// <item>Snapshot = the session save bundle from <see cref="ISessionSaveStore"/> (Lifecycle), which embeds
    /// <see cref="Table"/>.</item>
    /// <item>Deltas = facts. The Progression module reports local changes with <see cref="ReportLocalFact"/>
    /// (host: broadcast; client: claimed to the host) and applies confirmed ones in
    /// <see cref="IFactHandler.ApplyFact"/>.</item>
    /// <item><see cref="Table"/> holds the host's facts: written by the host's own reports and by every applied
    /// snapshot or delta, never by a client's unconfirmed report.</item>
    /// </list>
    /// Without a store the bundle is just the fact table; without a fact handler facts are only stored, so a session
    /// still runs end to end while a module is missing. No Unity types (unit tested). Main thread only.
    /// </summary>
    public sealed class GameSaveBridge : ISaveBackend
    {
        private const string Log = "[Save] ";

        private readonly Func<bool> _isHosting;
        private readonly Func<bool> _isClient;
        private ISessionSaveStore _store;
        private IFactHandler _facts;
        private FactTable _backup;
        private bool _suppressDiskWrites;

        /// <param name="isHosting">True while this peer hosts a running session.</param>
        /// <param name="isClient">True while this peer is a client of a running (or connecting) session.</param>
        public GameSaveBridge(Func<bool> isHosting, Func<bool> isClient)
        {
            _isHosting = isHosting ?? throw new ArgumentNullException(nameof(isHosting));
            _isClient = isClient ?? throw new ArgumentNullException(nameof(isClient));
        }

        /// <summary>The session's facts (see the class summary for who writes it).</summary>
        public FactTable Table { get; } = new FactTable();

        public ISessionSaveStore Store => _store;

        public IFactHandler FactHandler => _facts;

        public event Action<SaveDelta> LocalProgressChanged;

        /// <summary>Raised after <see cref="SuppressDiskWrites"/> changed, with the new value.</summary>
        public event Action<bool> SuppressDiskWritesChanged;

        /// <summary>Set by Core on clients while the host's save is loaded. The Lifecycle module blocks save writes on it.</summary>
        public bool SuppressDiskWrites
        {
            get => _suppressDiskWrites;
            set
            {
                if (_suppressDiskWrites == value)
                    return;
                _suppressDiskWrites = value;
                // Core reads the flag back to decide whether a restore is pending, so a failing listener must not undo it.
                try
                {
                    _store?.OnSuppressDiskWritesChanged(value);
                }
                catch (Exception e)
                {
                    NetLog.Error($"{Log}Session save store failed to switch disk writes to {(value ? "off" : "on")}: {e}");
                }
                var handler = SuppressDiskWritesChanged;
                if (handler == null)
                    return;
                try
                {
                    handler(value);
                }
                catch (Exception e)
                {
                    NetLog.Error($"{Log}SuppressDiskWritesChanged handler threw: {e}");
                }
            }
        }

        /// <summary>Installs the Lifecycle module's store (null removes it).</summary>
        public void SetStore(ISessionSaveStore store) => _store = store;

        /// <summary>Installs the Progression module's fact handler (null removes it).</summary>
        public void SetFactHandler(IFactHandler handler) => _facts = handler;

        public byte[] CaptureSnapshot()
        {
            var store = _store;
            return store != null ? store.CaptureBundle(Table) : Table.ToArray();
        }

        public void ApplySnapshot(byte[] snapshot)
        {
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));
            var store = _store;
            var facts = store != null ? store.ApplyBundle(snapshot) : FactTable.FromArray(snapshot);
            Table.ReplaceWith(facts);
            _facts?.OnFactsReplaced(Table);
        }

        /// <summary>
        /// Refuse to join a session without a session save store (set by the real game adapter): without one there is
        /// no backup, no injection and no write protection, yet the fact handler would still apply the host's progress
        /// to the running game, from where the next save would write it into the player's own files.
        /// </summary>
        public bool RequireStoreToJoin { get; set; }

        public void BackupLocalSave()
        {
            if (_store == null && RequireStoreToJoin)
                throw new InvalidOperationException("The session save module is not installed; joining is refused to protect the local save.");
            _store?.BackupLocalSave();
            _backup = Table.Clone();
        }

        public void RestoreLocalSave()
        {
            try
            {
                _store?.RestoreLocalSave();
            }
            finally
            {
                // The table only mirrors a session; whatever happened to the files, drop the host's facts.
                Table.ReplaceWith(_backup);
                _backup = null;
            }
        }

        public void ApplyDelta(SaveDelta delta)
        {
            if (delta == null)
                throw new ArgumentNullException(nameof(delta));
            if (!delta.IsValid)
                throw new ArgumentException($"Invalid fact {delta}.", nameof(delta));
            Table.Set(delta.Kind, delta.Key, delta.Value);
            _facts?.ApplyFact(delta);
        }

        /// <summary>Host: Core's claim validator. Without a fact handler every well-formed claim is accepted.</summary>
        public bool ValidateClaim(PeerId from, SaveDelta claim)
        {
            var handler = _facts;
            return handler == null || handler.ValidateClaim(from, claim);
        }

        /// <summary>
        /// The local game recorded a fact. Host (or offline): stored, and broadcast when it changed. Client: claimed
        /// to the host; the table changes only when the host confirms it.
        /// </summary>
        public void ReportLocalFact(string kind, string key, byte[] value)
        {
            bool client = _isClient();
            if (!client)
            {
                bool changed;
                try
                {
                    changed = Table.Set(kind, key, value);
                }
                catch (ArgumentException e)
                {
                    NetLog.Error($"{Log}Dropping fact that does not fit a SaveDelta: {e.Message}");
                    return;
                }
                if (!changed || !_isHosting())
                    return;
            }
            var delta = new SaveDelta(kind, key, value);
            if (!delta.IsValid)
            {
                NetLog.Error($"{Log}Dropping invalid fact {delta}.");
                return;
            }
            var handler = LocalProgressChanged;
            if (handler == null)
                return;
            try
            {
                handler(delta);
            }
            catch (Exception e)
            {
                NetLog.Error($"{Log}Reporting fact {delta} failed: {e}");
            }
        }

        /// <summary>Host: replaces <see cref="Table"/> with a fresh build from the fact handler (before a full resync).</summary>
        public void RebuildFacts()
        {
            var handler = _facts;
            if (handler == null)
                return;
            var fresh = new FactTable();
            handler.BuildFacts(fresh);
            Table.ReplaceWith(fresh);
        }
    }
}
