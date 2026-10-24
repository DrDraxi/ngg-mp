using System;
using System.Collections.Generic;
using NGGMultiplayer.Core.Diagnostics;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Session;

namespace NGGMultiplayer.Core.Save
{
    /// <summary>
    /// Host side of save sync. The host's save is the session's source of truth: every joining peer gets a
    /// full snapshot, every change (local, or a client claim the host accepts) is broadcast as a SaveDelta
    /// with the next revision number. Call <see cref="Tick"/> every frame; main thread only.
    /// <para>
    /// Snapshot chunks are paced: at most <see cref="SaveSyncOptions.SnapshotBytesPerTick"/> per peer go out per call
    /// (the first batch right away, the rest from <see cref="Tick"/>), so a large save never floods the transport's
    /// send buffer in one frame.
    /// </para>
    /// </summary>
    public sealed class SaveSyncHost : IDisposable
    {
        private const string Log = "[SaveSync/Host] ";

        private readonly ISessionMessaging _session;
        private readonly ISaveBackend _backend;
        private readonly Func<PeerId, SaveDelta, bool> _validator;
        private readonly SaveSyncOptions _options;
        private readonly Dictionary<PeerId, PeerState> _peers = new Dictionary<PeerId, PeerState>();
        // Last value broadcast per (Kind, Key); lets the host drop changes that would only echo state back.
        private readonly Dictionary<string, byte[]> _published = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        private readonly NetWriter _chunkWriter = new NetWriter(SaveProtocol.ChunkSize + 16);
        private readonly Action<PeerId, NetReader> _onAck;
        private readonly Action<PeerId, NetReader> _onClaim;
        private readonly Action<PeerId, NetReader> _onResyncRequest;
        private uint _revision;
        private double _now;
        private int _deferredCount;
        private int _transferCount;
        // While a claim is applied: its (Kind, Key), so only that key's echo is dropped, and the game's own value
        // for that key if it recorded a different one while applying.
        private string _applyingClaimKey;
        private SaveDelta _applyingClaimDelta;
        private SaveDelta _claimRecordedValue;
        private bool _disposed;

        /// <param name="validator">Decides whether a client's claim is accepted. Null accepts every well-formed claim.</param>
        public SaveSyncHost(ISessionMessaging session, ISaveBackend backend, Func<PeerId, SaveDelta, bool> validator = null, SaveSyncOptions options = null)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            if (!session.IsHost)
                throw new InvalidOperationException("SaveSyncHost requires a hosting session.");
            _validator = validator;
            _options = (options ?? new SaveSyncOptions()).Validated();

            _onAck = OnSaveAck;
            _onClaim = OnProgressClaim;
            _onResyncRequest = OnResyncRequest;
            _session.RegisterHandler(MessageType.SaveAck, _onAck);
            _session.RegisterHandler(MessageType.ProgressClaim, _onClaim);
            _session.RegisterHandler(MessageType.SaveResyncRequest, _onResyncRequest);
            _session.PlayerJoined += OnPlayerJoined;
            _session.PlayerLeft += OnPlayerLeft;
            _session.SessionClosed += OnSessionClosed;
            _backend.LocalProgressChanged += OnLocalProgressChanged;
        }

        /// <summary>Revision of the latest broadcast change. Snapshots carry the revision they were captured at.</summary>
        public uint Revision => _revision;

        public int PeerCount => _peers.Count;

        public long SnapshotsSent { get; private set; }

        public long DeltasBroadcast { get; private set; }

        /// <summary>A peer acknowledged a snapshot (peer, acknowledged revision).</summary>
        public event Action<PeerId, uint> PeerSynced;

        /// <summary>A client claim was applied and broadcast.</summary>
        public event Action<PeerId, SaveDelta> ClaimAccepted;

        /// <summary>A client claim was refused (peer, claim, reason).</summary>
        public event Action<PeerId, SaveDelta, string> ClaimRejected;

        /// <summary>True once the peer has acknowledged its latest snapshot.</summary>
        public bool IsPeerSynced(PeerId peer) => _peers.TryGetValue(peer, out var state) && state.Synced;

        public bool IsPeerKnown(PeerId peer) => _peers.ContainsKey(peer);

        /// <summary>
        /// Advances the clock (seconds) used to pace resync snapshots, sends the next chunks of snapshots in flight,
        /// and starts any that were deferred.
        /// </summary>
        public void Tick(double now)
        {
            if (_disposed)
                return;
            _now = now;
            if (_transferCount > 0)
            {
                foreach (var pair in _peers)
                {
                    if (pair.Value.Transfer != null)
                        PumpChunks(pair.Key, pair.Value);
                }
            }
            if (_deferredCount == 0)
                return;

            byte[] snapshot = null;
            uint crc = 0;
            foreach (var pair in _peers)
            {
                var state = pair.Value;
                if (!state.ResyncDeferred || !CanSendSnapshot(state))
                    continue;
                if (snapshot == null && !TryCaptureSnapshot(out snapshot, out crc))
                    return;
                SendSnapshot(pair.Key, state, snapshot, crc);
            }
        }

        /// <summary>
        /// Sends a fresh snapshot to every peer, e.g. after the host's save was reloaded outside of
        /// <see cref="ISaveBackend.LocalProgressChanged"/>. Bumps the revision so clients discard older deltas.
        /// </summary>
        public void ResyncAll()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SaveSyncHost));
            ResyncAllInternal();
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _backend.LocalProgressChanged -= OnLocalProgressChanged;
            _session.PlayerJoined -= OnPlayerJoined;
            _session.PlayerLeft -= OnPlayerLeft;
            _session.SessionClosed -= OnSessionClosed;
            _session.UnregisterHandler(MessageType.SaveAck);
            _session.UnregisterHandler(MessageType.ProgressClaim);
            _session.UnregisterHandler(MessageType.SaveResyncRequest);
            _peers.Clear();
            _published.Clear();
            _deferredCount = 0;
            _transferCount = 0;
        }

        private void OnPlayerJoined(PeerId peer)
        {
            if (_disposed || !peer.IsValid || peer == _session.LocalId)
                return;
            if (_peers.TryGetValue(peer, out var existing))
            {
                NetLog.Warn($"{Log}Peer {peer} joined twice; resending snapshot.");
                ClearDeferred(existing);
                EndTransfer(existing);
            }
            var state = new PeerState();
            _peers[peer] = state;
            if (TryCaptureSnapshot(out var snapshot, out uint crc))
                SendSnapshot(peer, state, snapshot, crc);
        }

        private void OnPlayerLeft(PeerId peer)
        {
            if (_disposed)
                return;
            if (_peers.TryGetValue(peer, out var state))
            {
                ClearDeferred(state);
                EndTransfer(state);
                _peers.Remove(peer);
            }
        }

        private void OnSessionClosed()
        {
            if (_disposed)
                return;
            _peers.Clear();
            _deferredCount = 0;
            _transferCount = 0;
        }

        private void OnLocalProgressChanged(SaveDelta delta)
        {
            if (_disposed)
                return;
            if (delta == null || !delta.IsWellFormed)
            {
                NetLog.Error($"{Log}Ignoring malformed local change: {delta?.ToString() ?? "null"}.");
                return;
            }
            if (_applyingClaimKey != null
                && string.Equals(SaveDelta.CompositeKey(delta.Kind, delta.Key), _applyingClaimKey, StringComparison.Ordinal))
            {
                // The claim being applied, reported back by the game. It is broadcast once the apply returns, with
                // the value the game actually recorded. Other keys (side effects of the claim) go out normally below.
                if (!SaveDelta.ValueEquals(delta.Value, _applyingClaimDelta.Value))
                    _claimRecordedValue = delta.Clone();
                return;
            }
            if (!delta.IsWithinLimits)
            {
                // Too big for a SaveDelta; a chunked snapshot carries it instead.
                NetLog.Warn($"{Log}Local change {delta} exceeds delta limits; resending full snapshots.");
                ResyncAllInternal();
                return;
            }
            if (IsAlreadyPublished(delta))
                return;
            BroadcastDelta(delta);
        }

        private void OnSaveAck(PeerId from, NetReader reader)
        {
            if (_disposed)
                return;
            uint revision;
            try
            {
                revision = reader.ReadVarUInt();
            }
            catch (ProtocolException e)
            {
                NetLog.Warn($"{Log}Malformed SaveAck from {from}: {e.Message}");
                return;
            }
            if (!_peers.TryGetValue(from, out var state))
            {
                NetLog.Warn($"{Log}SaveAck from unknown peer {from}.");
                return;
            }
            if (!state.HasSentSnapshot || revision > _revision || revision < state.LastSnapshotRevision)
            {
                NetLog.Warn($"{Log}Ignoring SaveAck r{revision} from {from} (host r{_revision}, last snapshot r{state.LastSnapshotRevision}).");
                return;
            }
            if (state.Synced)
                return;
            state.Synced = true;
            NetLog.Info($"{Log}Peer {from} synced at r{revision}.");
            SafeInvoke.Raise(PeerSynced, from, revision, nameof(PeerSynced));
        }

        private void OnProgressClaim(PeerId from, NetReader reader)
        {
            if (_disposed)
                return;
            if (!_peers.ContainsKey(from))
            {
                NetLog.Warn($"{Log}ProgressClaim from unknown peer {from}.");
                return;
            }
            SaveDelta delta;
            try
            {
                delta = SaveDelta.Deserialize(reader);
            }
            catch (ProtocolException e)
            {
                NetLog.Warn($"{Log}Malformed ProgressClaim from {from}: {e.Message}");
                return;
            }

            bool accepted;
            string reason = "Rejected by host.";
            try
            {
                accepted = _validator == null || _validator(from, delta);
            }
            catch (Exception e)
            {
                NetLog.Error($"{Log}Claim validator threw for {delta} from {from}: {e}");
                accepted = false;
                reason = "Host validation failed.";
            }
            if (!accepted)
            {
                RejectClaim(from, delta, reason);
                return;
            }
            // Nothing to apply, and every synced peer already has this value.
            if (IsAlreadyPublished(delta))
                return;

            bool applied = false;
            _applyingClaimKey = SaveDelta.CompositeKey(delta.Kind, delta.Key);
            _applyingClaimDelta = delta;
            _claimRecordedValue = null;
            try
            {
                _backend.ApplyDelta(delta);
                applied = true;
            }
            catch (Exception e)
            {
                NetLog.Error($"{Log}Backend failed to apply claim {delta} from {from}: {e}");
            }
            finally
            {
                _applyingClaimKey = null;
                _applyingClaimDelta = null;
            }
            var recorded = _claimRecordedValue;
            _claimRecordedValue = null;
            if (!applied)
            {
                RejectClaim(from, delta, "Host failed to apply the change.");
                // The backend may have changed part of its state before failing; resend the whole truth to everyone.
                if (!_disposed)
                    ResyncAllInternal();
                return;
            }

            if (recorded == null)
                BroadcastDelta(delta);
            else if (recorded.IsWithinLimits)
                BroadcastDelta(recorded);
            else
                ResyncAllInternal();
            SafeInvoke.Raise(ClaimAccepted, from, delta, nameof(ClaimAccepted));
        }

        private void OnResyncRequest(PeerId from, NetReader reader)
        {
            if (_disposed)
                return;
            if (!_peers.TryGetValue(from, out var state))
            {
                NetLog.Warn($"{Log}SaveResyncRequest from unknown peer {from}.");
                return;
            }
            uint clientRevision = 0;
            try
            {
                clientRevision = reader.ReadVarUInt();
            }
            catch (ProtocolException)
            {
                // Diagnostic field only; the request itself is still honoured.
            }
            NetLog.Info($"{Log}Peer {from} requested resync (client r{clientRevision}, host r{_revision}).");
            state.Synced = false;

            if (CanSendSnapshot(state))
            {
                if (TryCaptureSnapshot(out var snapshot, out uint crc))
                    SendSnapshot(from, state, snapshot, crc);
            }
            else if (!state.ResyncDeferred)
            {
                state.ResyncDeferred = true;
                _deferredCount++;
            }
        }

        private void RejectClaim(PeerId peer, SaveDelta delta, string reason)
        {
            NetLog.Info($"{Log}Rejected claim {delta} from {peer}: {reason}");
            using (var packet = PacketBuilder.Rent(MessageType.ProgressClaimRejected))
            {
                delta.Serialize(packet.Writer);
                packet.Writer.WriteString(reason);
                _session.SendTo(peer, SaveProtocol.SyncChannel, Reliability.Reliable, packet.Segment);
            }
            SafeInvoke.Raise(ClaimRejected, peer, delta, reason, nameof(ClaimRejected));
        }

        private void BroadcastDelta(SaveDelta delta)
        {
            _revision++;
            _published[SaveDelta.CompositeKey(delta.Kind, delta.Key)] =
                delta.Value == null ? Array.Empty<byte>() : (byte[])delta.Value.Clone();
            using (var packet = PacketBuilder.Rent(MessageType.SaveDelta))
            {
                packet.Writer.WriteVarUInt(_revision);
                delta.Serialize(packet.Writer);
                _session.Broadcast(SaveProtocol.SyncChannel, Reliability.Reliable, packet.Segment, PeerId.None);
            }
            DeltasBroadcast++;
            if (NetLog.IsEnabled(LogLevel.Debug))
                NetLog.Debug($"{Log}Broadcast r{_revision}: {delta}.");
        }

        private bool IsAlreadyPublished(SaveDelta delta) =>
            _published.TryGetValue(SaveDelta.CompositeKey(delta.Kind, delta.Key), out var value)
            && SaveDelta.ValueEquals(value, delta.Value);

        private void ResyncAllInternal()
        {
            // Older deltas are folded into the snapshot; the new revision makes clients drop any still queued.
            _revision++;
            // Clients now hold whatever the snapshot carries, which need not be the last value broadcast per key.
            _published.Clear();
            if (_peers.Count == 0)
                return;
            if (!TryCaptureSnapshot(out var snapshot, out uint crc))
                return;
            foreach (var pair in _peers)
                SendSnapshot(pair.Key, pair.Value, snapshot, crc);
        }

        private bool CanSendSnapshot(PeerState state) =>
            !state.HasSentSnapshot || _now - state.LastSnapshotAt >= _options.MinResyncIntervalSeconds;

        private bool TryCaptureSnapshot(out byte[] snapshot, out uint crc)
        {
            crc = 0;
            try
            {
                snapshot = _backend.CaptureSnapshot() ?? Array.Empty<byte>();
            }
            catch (Exception e)
            {
                NetLog.Error($"{Log}CaptureSnapshot failed: {e}");
                snapshot = null;
                return false;
            }
            if (snapshot.Length > _options.MaxSnapshotBytes)
            {
                NetLog.Error($"{Log}Snapshot is {snapshot.Length} bytes; the limit is {_options.MaxSnapshotBytes}. Not sending.");
                snapshot = null;
                return false;
            }
            crc = Crc32.Compute(snapshot);
            return true;
        }

        private void SendSnapshot(PeerId peer, PeerState state, byte[] snapshot, uint crc)
        {
            int chunkCount = SaveProtocol.GetChunkCount(snapshot.Length);
            using (var packet = PacketBuilder.Rent(MessageType.SaveBegin))
            {
                var w = packet.Writer;
                w.WriteVarUInt((uint)snapshot.Length);
                w.WriteVarUInt((uint)chunkCount);
                w.WriteUInt(crc);
                w.WriteVarUInt(_revision);
                _session.SendTo(peer, SaveProtocol.SyncChannel, Reliability.Reliable, packet.Segment);
            }

            ClearDeferred(state);
            // A newer snapshot supersedes one still in flight; the client drops a transfer when a new SaveBegin arrives.
            EndTransfer(state);
            state.HasSentSnapshot = true;
            state.LastSnapshotAt = _now;
            state.LastSnapshotRevision = _revision;
            state.Synced = false;
            SnapshotsSent++;
            NetLog.Info($"{Log}Sending snapshot r{_revision} to {peer}: {snapshot.Length} bytes in {chunkCount} chunks.");
            if (chunkCount == 0)
                return;
            state.Transfer = snapshot;
            state.NextChunk = 0;
            _transferCount++;
            PumpChunks(peer, state);
        }

        private void PumpChunks(PeerId peer, PeerState state)
        {
            byte[] snapshot = state.Transfer;
            int chunkCount = SaveProtocol.GetChunkCount(snapshot.Length);
            int budget = _options.SnapshotBytesPerTick;
            while (state.NextChunk < chunkCount && budget > 0)
            {
                int i = state.NextChunk;
                int offset = i * SaveProtocol.ChunkSize;
                int count = Math.Min(SaveProtocol.ChunkSize, snapshot.Length - offset);
                PacketBuilder.Begin(_chunkWriter, MessageType.SaveChunk);
                _chunkWriter.WriteVarUInt((uint)i);
                _chunkWriter.WriteBytes(snapshot, offset, count);
                _session.SendTo(peer, SaveProtocol.SyncChannel, Reliability.Reliable, _chunkWriter.AsSegment());
                state.NextChunk = i + 1;
                budget -= count;
            }
            if (state.NextChunk >= chunkCount)
                EndTransfer(state);
        }

        private void EndTransfer(PeerState state)
        {
            if (state.Transfer == null)
                return;
            state.Transfer = null;
            state.NextChunk = 0;
            _transferCount--;
        }

        private void ClearDeferred(PeerState state)
        {
            if (!state.ResyncDeferred)
                return;
            state.ResyncDeferred = false;
            _deferredCount--;
        }

        private sealed class PeerState
        {
            public bool HasSentSnapshot;
            public double LastSnapshotAt;
            public uint LastSnapshotRevision;
            public bool Synced;
            public bool ResyncDeferred;
            // Snapshot whose chunks are still being sent, and the next chunk index.
            public byte[] Transfer;
            public int NextChunk;
        }
    }
}
