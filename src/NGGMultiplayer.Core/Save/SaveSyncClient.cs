using System;
using System.Collections.Generic;
using NGGMultiplayer.Core.Diagnostics;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Session;

namespace NGGMultiplayer.Core.Save
{
    public enum SaveSyncState : byte
    {
        /// <summary>Waiting for the host to start a snapshot (initially, or after a resync request).</summary>
        AwaitingSnapshot,
        /// <summary>Snapshot chunks are arriving.</summary>
        Receiving,
        /// <summary>The local backend mirrors the host's save.</summary>
        Synced,
        /// <summary>Gave up after repeated failures, or the local save could not be backed up. Terminal.</summary>
        Failed,
        /// <summary>The session ended and the local save was restored. Terminal.</summary>
        Ended
    }

    /// <summary>
    /// Client side of save sync. Loads the host's save in memory only (after backing up the local one and
    /// suppressing disk writes), keeps it current from the host's SaveDeltas, forwards the local game's
    /// progress to the host as claims, and restores the local save when the session ends.
    /// Call <see cref="Tick"/> every frame; main thread only.
    /// </summary>
    public sealed class SaveSyncClient : IDisposable
    {
        private const string Log = "[SaveSync/Client] ";

        private static readonly Comparison<QueuedDelta> ByRevision = (a, b) => a.Revision.CompareTo(b.Revision);

        private readonly ISessionMessaging _session;
        private readonly ISaveBackend _backend;
        private readonly SaveSyncOptions _options;
        private readonly List<QueuedDelta> _queue = new List<QueuedDelta>();
        // Host-confirmed value per (Kind, Key) since the last snapshot. A local change equal to it is an echo, not a claim.
        private readonly Dictionary<string, byte[]> _confirmed = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        private readonly Action<PeerId, NetReader> _onBegin;
        private readonly Action<PeerId, NetReader> _onChunk;
        private readonly Action<PeerId, NetReader> _onDelta;
        private readonly Action<PeerId, NetReader> _onClaimRejected;

        private SaveSyncState _state = SaveSyncState.AwaitingSnapshot;
        private uint _revision;
        private bool _backupTaken;
        private bool _applyingRemote;
        private bool _resyncRequested;
        private bool _queueOverflowed;
        private bool _disposed;
        private int _consecutiveFailures;
        private double _now;
        private double _lastActivity = double.NaN;

        private byte[] _transferBuffer;
        private bool[] _chunkReceived;
        private int _chunksReceived;
        private uint _transferCrc;
        private uint _transferRevision;

        public SaveSyncClient(ISessionMessaging session, ISaveBackend backend, SaveSyncOptions options = null)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            if (session.IsHost)
                throw new InvalidOperationException("SaveSyncClient requires a client session.");
            _options = (options ?? new SaveSyncOptions()).Validated();

            _onBegin = OnSaveBegin;
            _onChunk = OnSaveChunk;
            _onDelta = OnSaveDelta;
            _onClaimRejected = OnClaimRejected;
            _session.RegisterHandler(MessageType.SaveBegin, _onBegin);
            _session.RegisterHandler(MessageType.SaveChunk, _onChunk);
            _session.RegisterHandler(MessageType.SaveDelta, _onDelta);
            _session.RegisterHandler(MessageType.ProgressClaimRejected, _onClaimRejected);
            _session.PlayerLeft += OnPlayerLeft;
            _session.SessionClosed += Shutdown;
            _backend.LocalProgressChanged += OnLocalProgressChanged;
        }

        public SaveSyncState State => _state;

        /// <summary>True while the local backend mirrors the host's save. Gameplay should wait for this.</summary>
        public bool IsSynced => _state == SaveSyncState.Synced;

        /// <summary>Host revision the local backend reflects (meaningful once synced).</summary>
        public uint Revision => _revision;

        /// <summary>True once the local save has been backed up for this session (and will be restored on end).</summary>
        public bool HasLocalBackup => _backupTaken;

        /// <summary>
        /// Share of the snapshot received so far, 0..1, for progress displays: chunks received while
        /// <see cref="SaveSyncState.Receiving"/>, 1 once synced, 0 otherwise.
        /// </summary>
        public float SnapshotProgress
        {
            get
            {
                if (_state == SaveSyncState.Synced)
                    return 1f;
                if (_state != SaveSyncState.Receiving || _chunkReceived == null || _chunkReceived.Length == 0)
                    return 0f;
                return (float)_chunksReceived / _chunkReceived.Length;
            }
        }

        public long SnapshotsApplied { get; private set; }

        public long DeltasApplied { get; private set; }

        public long ClaimsSent { get; private set; }

        public long ResyncRequestsSent { get; private set; }

        /// <summary>The host's save was applied (raised after every successful snapshot, including resyncs).</summary>
        public event Action Synced;

        /// <summary>A live change from the host was applied (revision, change).</summary>
        public event Action<uint, SaveDelta> DeltaApplied;

        /// <summary>The host refused a claim (claim, reason). A resync follows when <see cref="SaveSyncOptions.ResyncOnClaimRejected"/>.</summary>
        public event Action<SaveDelta, string> ClaimRejected;

        /// <summary>Sync gave up (reason). The session should be left; the local save is still restored on end.</summary>
        public event Action<string> SyncFailed;

        /// <summary>The session ended; the local save has been restored if it was replaced.</summary>
        public event Action Ended;

        /// <summary>Advances the clock (seconds) used for transfer timeouts.</summary>
        public void Tick(double now)
        {
            _now = now;
            if (double.IsNaN(_lastActivity))
                _lastActivity = now;
            if (_state != SaveSyncState.AwaitingSnapshot && _state != SaveSyncState.Receiving)
                return;
            if (now - _lastActivity < _options.TransferTimeoutSeconds)
                return;

            NetLog.Warn($"{Log}No snapshot progress for {now - _lastActivity:0.0}s ({_state}).");
            DiscardTransfer();
            // Counted like any other failure, so a host that never completes a snapshot eventually ends the sync.
            FailAndResync("snapshot transfer timed out");
        }

        /// <summary>Asks the host for a full snapshot, e.g. when the game detects its state diverged.</summary>
        public void RequestResync()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SaveSyncClient));
            RequestResyncInternal("requested locally");
        }

        /// <summary>
        /// Ends save sync for this session: restores the local save (if it was replaced) and re-enables disk
        /// writes. Idempotent; also runs on <see cref="ISessionMessaging.SessionClosed"/> and when the host leaves.
        /// </summary>
        public void Shutdown()
        {
            if (_state == SaveSyncState.Ended)
                return;
            _state = SaveSyncState.Ended;
            _backend.LocalProgressChanged -= OnLocalProgressChanged;
            DiscardTransfer();
            _queue.Clear();
            _confirmed.Clear();

            if (_backupTaken)
            {
                bool restored = false;
                _applyingRemote = true;
                try
                {
                    _backend.RestoreLocalSave();
                    restored = true;
                }
                catch (Exception e)
                {
                    // Disk writes stay suppressed: the file on disk is still the player's own save, and
                    // letting the game write now would overwrite it with the host's progress.
                    NetLog.Error($"{Log}RestoreLocalSave failed; keeping disk writes suppressed to protect the local save file. Restart the game to reload it. {e}");
                }
                finally
                {
                    _applyingRemote = false;
                }
                if (restored)
                {
                    try
                    {
                        _backend.SuppressDiskWrites = false;
                    }
                    catch (Exception e)
                    {
                        NetLog.Error($"{Log}Failed to re-enable disk writes: {e}");
                    }
                    NetLog.Info($"{Log}Local save restored.");
                }
            }
            SafeInvoke.Raise(Ended, nameof(Ended));
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            Shutdown();
            _disposed = true;
            _session.PlayerLeft -= OnPlayerLeft;
            _session.SessionClosed -= Shutdown;
            _session.UnregisterHandler(MessageType.SaveBegin);
            _session.UnregisterHandler(MessageType.SaveChunk);
            _session.UnregisterHandler(MessageType.SaveDelta);
            _session.UnregisterHandler(MessageType.ProgressClaimRejected);
        }

        private void OnPlayerLeft(PeerId peer)
        {
            if (peer == _session.HostId)
                Shutdown();
        }

        private void OnSaveBegin(PeerId from, NetReader reader)
        {
            if (!AcceptFromHost(from, "SaveBegin"))
                return;
            uint totalBytes, chunkCount, crc, revision;
            try
            {
                totalBytes = reader.ReadVarUInt();
                chunkCount = reader.ReadVarUInt();
                crc = reader.ReadUInt();
                revision = reader.ReadVarUInt();
            }
            catch (ProtocolException e)
            {
                FailTransfer("malformed SaveBegin: " + e.Message);
                return;
            }
            if (totalBytes > (uint)_options.MaxSnapshotBytes)
            {
                FailTransfer($"snapshot of {totalBytes} bytes exceeds limit {_options.MaxSnapshotBytes}");
                return;
            }
            if (chunkCount != (uint)SaveProtocol.GetChunkCount((int)totalBytes))
            {
                FailTransfer($"SaveBegin chunk count {chunkCount} does not match {totalBytes} bytes");
                return;
            }

            DiscardTransfer();
            _transferBuffer = totalBytes == 0 ? Array.Empty<byte>() : new byte[totalBytes];
            _chunkReceived = chunkCount == 0 ? Array.Empty<bool>() : new bool[chunkCount];
            _chunksReceived = 0;
            _transferCrc = crc;
            _transferRevision = revision;
            _state = SaveSyncState.Receiving;
            _resyncRequested = false;
            MarkActivity();
            NetLog.Info($"{Log}Receiving snapshot r{revision}: {totalBytes} bytes in {chunkCount} chunks.");

            if (chunkCount == 0)
                CompleteTransfer();
        }

        private void OnSaveChunk(PeerId from, NetReader reader)
        {
            if (!AcceptFromHost(from, "SaveChunk"))
                return;
            if (_state != SaveSyncState.Receiving)
            {
                if (NetLog.IsEnabled(LogLevel.Debug))
                    NetLog.Debug($"{Log}Dropping SaveChunk outside a transfer ({_state}).");
                return;
            }

            uint index;
            ArraySegment<byte> data;
            try
            {
                index = reader.ReadVarUInt();
                data = reader.ReadBytesSegment(SaveProtocol.ChunkSize);
            }
            catch (ProtocolException e)
            {
                FailTransfer("malformed SaveChunk: " + e.Message);
                return;
            }
            if (index >= (uint)_chunkReceived.Length)
            {
                FailTransfer($"chunk index {index} out of range ({_chunkReceived.Length} chunks)");
                return;
            }
            int offset = (int)index * SaveProtocol.ChunkSize;
            int expected = Math.Min(SaveProtocol.ChunkSize, _transferBuffer.Length - offset);
            if (data.Count != expected)
            {
                FailTransfer($"chunk {index} has {data.Count} bytes, expected {expected}");
                return;
            }
            if (_chunkReceived[index])
                return;

            Buffer.BlockCopy(data.Array, data.Offset, _transferBuffer, offset, data.Count);
            _chunkReceived[index] = true;
            _chunksReceived++;
            MarkActivity();
            if (_chunksReceived == _chunkReceived.Length)
                CompleteTransfer();
        }

        private void OnSaveDelta(PeerId from, NetReader reader)
        {
            if (!AcceptFromHost(from, "SaveDelta"))
                return;
            uint revision;
            SaveDelta delta;
            try
            {
                revision = reader.ReadVarUInt();
                delta = SaveDelta.Deserialize(reader);
            }
            catch (ProtocolException e)
            {
                NetLog.Warn($"{Log}Malformed SaveDelta from host: {e.Message}");
                // A lost revision while synced is a gap; while a snapshot is pending, the queue's gap check covers it.
                if (_state == SaveSyncState.Synced)
                    FailAndResync("malformed SaveDelta");
                return;
            }

            if (_state != SaveSyncState.Synced)
            {
                if (_queue.Count >= _options.MaxQueuedDeltas)
                {
                    _queueOverflowed = true;
                    return;
                }
                _queue.Add(new QueuedDelta(revision, delta));
                return;
            }

            if (revision <= _revision)
                return;
            if (revision != _revision + 1)
            {
                NetLog.Warn($"{Log}Revision gap: have r{_revision}, got r{revision}.");
                RequestResyncInternal("revision gap");
                return;
            }
            ApplyRemoteDelta(revision, delta);
        }

        private void OnClaimRejected(PeerId from, NetReader reader)
        {
            if (!AcceptFromHost(from, "ProgressClaimRejected"))
                return;
            SaveDelta delta;
            string reason;
            try
            {
                delta = SaveDelta.Deserialize(reader);
                reason = reader.ReadString(SaveProtocol.MaxReasonBytes);
            }
            catch (ProtocolException e)
            {
                NetLog.Warn($"{Log}Malformed ProgressClaimRejected: {e.Message}");
                return;
            }
            NetLog.Info($"{Log}Host rejected claim {delta}: {reason}");
            SafeInvoke.Raise(ClaimRejected, delta, reason, nameof(ClaimRejected));
            // The game already applied the change locally; reload the host's truth to undo it.
            if (_options.ResyncOnClaimRejected && _backupTaken)
                RequestResyncInternal("claim rejected");
        }

        private void OnLocalProgressChanged(SaveDelta delta)
        {
            if (_applyingRemote || _disposed || _state == SaveSyncState.Ended || _state == SaveSyncState.Failed)
                return;
            // Before the first snapshot the game is still running the player's own save.
            if (!_backupTaken)
                return;
            if (delta == null)
            {
                NetLog.Warn($"{Log}Ignoring a null local change.");
                return;
            }
            if (!delta.IsValid)
            {
                // The game applied it in memory, but the host will never record it: treat it as rejected and
                // reload the host's truth so the local world does not show progress the session does not have.
                const string reason = "The change is malformed or too large to send to the host.";
                NetLog.Warn($"{Log}Cannot claim local change {delta}: {reason}");
                SafeInvoke.Raise(ClaimRejected, delta, reason, nameof(ClaimRejected));
                if (_options.ResyncOnClaimRejected)
                    RequestResyncInternal("local change could not be claimed");
                return;
            }
            string composite = SaveDelta.CompositeKey(delta.Kind, delta.Key);
            if (_confirmed.TryGetValue(composite, out var known))
            {
                if (SaveDelta.ValueEquals(known, delta.Value))
                    return;
                // The local value now differs from what the host confirmed, so a later change back to the
                // confirmed value is a real change and must be claimed too.
                _confirmed.Remove(composite);
            }

            using (var packet = PacketBuilder.Rent(MessageType.ProgressClaim))
            {
                delta.Serialize(packet.Writer);
                _session.SendTo(_session.HostId, SaveProtocol.SyncChannel, Reliability.Reliable, packet.Segment);
            }
            ClaimsSent++;
            if (NetLog.IsEnabled(LogLevel.Debug))
                NetLog.Debug($"{Log}Claimed {delta}.");
        }

        private void CompleteTransfer()
        {
            byte[] snapshot = _transferBuffer;
            uint revision = _transferRevision;
            if (Crc32.Compute(snapshot) != _transferCrc)
            {
                FailTransfer($"CRC mismatch on snapshot r{revision}");
                return;
            }
            DiscardTransfer();

            if (!_backupTaken)
            {
                bool alreadySuppressed;
                try
                {
                    alreadySuppressed = _backend.SuppressDiskWrites;
                }
                catch (Exception e)
                {
                    Fail("could not read the save backend's state: " + e.Message);
                    return;
                }
                if (alreadySuppressed)
                {
                    // An earlier session's host save is still loaded (its restore failed). Backing that up would
                    // later restore it, and save it, as if it were the player's own progress.
                    Fail("the local save from an earlier session was never restored; restart the game before joining");
                    return;
                }
                try
                {
                    _backend.BackupLocalSave();
                }
                catch (Exception e)
                {
                    // Never replace a save we could not back up.
                    Fail("BackupLocalSave failed: " + e.Message);
                    return;
                }
                _backupTaken = true;
                try
                {
                    _backend.SuppressDiskWrites = true;
                }
                catch (Exception e)
                {
                    Fail("could not suppress disk writes: " + e.Message);
                    return;
                }
            }

            bool applied = false;
            _applyingRemote = true;
            try
            {
                _backend.ApplySnapshot(snapshot);
                applied = true;
            }
            catch (Exception e)
            {
                NetLog.Error($"{Log}ApplySnapshot r{revision} failed: {e}");
            }
            finally
            {
                _applyingRemote = false;
            }
            if (!applied)
            {
                FailAndResync("snapshot could not be applied");
                return;
            }

            _revision = revision;
            _confirmed.Clear();
            _consecutiveFailures = 0;
            SnapshotsApplied++;
            _state = SaveSyncState.Synced;
            NetLog.Info($"{Log}Applied snapshot r{revision} ({snapshot.Length} bytes).");

            DrainQueue();
            if (_state != SaveSyncState.Synced)
                return;

            using (var packet = PacketBuilder.Rent(MessageType.SaveAck))
            {
                packet.Writer.WriteVarUInt(_revision);
                _session.SendTo(_session.HostId, SaveProtocol.SyncChannel, Reliability.Reliable, packet.Segment);
            }
            SafeInvoke.Raise(Synced, nameof(Synced));
        }

        private void DrainQueue()
        {
            if (_queueOverflowed)
            {
                _queueOverflowed = false;
                _queue.Clear();
                RequestResyncInternal("delta queue overflowed during transfer");
                return;
            }
            if (_queue.Count == 0)
                return;

            _queue.Sort(ByRevision);
            for (int i = 0; i < _queue.Count; i++)
            {
                var queued = _queue[i];
                if (queued.Revision <= _revision)
                    continue;
                if (queued.Revision != _revision + 1)
                {
                    NetLog.Warn($"{Log}Revision gap after snapshot: have r{_revision}, queued r{queued.Revision}.");
                    _queue.Clear();
                    RequestResyncInternal("revision gap");
                    return;
                }
                if (!ApplyRemoteDelta(queued.Revision, queued.Delta))
                {
                    _queue.Clear();
                    return;
                }
            }
            _queue.Clear();
        }

        private bool ApplyRemoteDelta(uint revision, SaveDelta delta)
        {
            bool applied = false;
            _applyingRemote = true;
            try
            {
                _backend.ApplyDelta(delta);
                applied = true;
            }
            catch (Exception e)
            {
                NetLog.Error($"{Log}ApplyDelta r{revision} {delta} failed: {e}");
            }
            finally
            {
                _applyingRemote = false;
            }
            if (!applied)
            {
                FailAndResync("delta could not be applied");
                return false;
            }

            _revision = revision;
            _confirmed[SaveDelta.CompositeKey(delta.Kind, delta.Key)] = delta.Value;
            DeltasApplied++;
            SafeInvoke.Raise(DeltaApplied, revision, delta, nameof(DeltaApplied));
            return true;
        }

        private bool AcceptFromHost(PeerId from, string what)
        {
            if (_disposed || _state == SaveSyncState.Ended || _state == SaveSyncState.Failed)
                return false;
            if (from != _session.HostId)
            {
                NetLog.Warn($"{Log}Ignoring {what} from non-host peer {from}.");
                return false;
            }
            return true;
        }

        private void FailTransfer(string reason)
        {
            DiscardTransfer();
            FailAndResync(reason);
        }

        private void FailAndResync(string reason)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= _options.MaxConsecutiveFailures)
            {
                Fail($"{reason} ({_consecutiveFailures} failures in a row)");
                return;
            }
            NetLog.Warn($"{Log}{reason}; requesting resync.");
            // A failure can be the answer to an earlier request, so always ask again.
            _resyncRequested = false;
            RequestResyncInternal(reason);
        }

        private void Fail(string reason)
        {
            NetLog.Error($"{Log}Save sync failed: {reason}");
            DiscardTransfer();
            _queue.Clear();
            _state = SaveSyncState.Failed;
            SafeInvoke.Raise(SyncFailed, reason, nameof(SyncFailed));
        }

        private void RequestResyncInternal(string reason)
        {
            if (_state == SaveSyncState.Ended || _state == SaveSyncState.Failed)
                return;
            // One request in flight; the transfer timeout re-sends it if the host never answers.
            if (_state == SaveSyncState.AwaitingSnapshot && _resyncRequested)
                return;

            DiscardTransfer();
            // Everything queued so far predates the request, so the answering snapshot already covers it.
            _queue.Clear();
            _queueOverflowed = false;
            _state = SaveSyncState.AwaitingSnapshot;
            _resyncRequested = true;
            MarkActivity();

            using (var packet = PacketBuilder.Rent(MessageType.SaveResyncRequest))
            {
                packet.Writer.WriteVarUInt(_revision);
                _session.SendTo(_session.HostId, SaveProtocol.SyncChannel, Reliability.Reliable, packet.Segment);
            }
            ResyncRequestsSent++;
            NetLog.Info($"{Log}Requested resync ({reason}).");
        }

        private void DiscardTransfer()
        {
            _transferBuffer = null;
            _chunkReceived = null;
            _chunksReceived = 0;
            if (_state == SaveSyncState.Receiving)
                _state = SaveSyncState.AwaitingSnapshot;
        }

        // The timestamp is taken on the next Tick, so activity between ticks never reads a stale clock.
        private void MarkActivity() => _lastActivity = double.NaN;

        private readonly struct QueuedDelta
        {
            public readonly uint Revision;
            public readonly SaveDelta Delta;

            public QueuedDelta(uint revision, SaveDelta delta)
            {
                Revision = revision;
                Delta = delta;
            }
        }
    }
}
