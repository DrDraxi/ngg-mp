using System;
using System.Collections.Generic;
using NGGMultiplayer.Core.Diagnostics;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using Steamworks;

namespace NGGMultiplayer.Steam
{
    /// <summary>
    /// <see cref="ITransport"/> over ISteamNetworkingMessages (connectionless, relayed through Valve's network).
    /// Each <see cref="Channel"/> maps to the Steam channel with the same number. Incoming session requests are
    /// accepted only from peers <see cref="AcceptFilter"/> allows (the lobby's members). Main thread only.
    /// <para>
    /// A peer whose reliable stream may have a gap (Steam failed the session, or refused a reliable send) is queued
    /// for <see cref="DrainLinkFailures"/>; the owner reports it to the session, which drops that peer, because
    /// nothing above the transport can recover messages Steam discarded.
    /// </para>
    /// </summary>
    internal sealed class SteamTransport : ITransport
    {
        private const string Log = "[SteamTransport] ";
        private const int ReceiveBatch = 64;
        private const int MaxReceivesPerChannelPerPoll = 4096;
        private const double FailureLogInterval = 5;
        // A session request can arrive before Steam has told us the peer joined the lobby; re-check it this long.
        private const double PendingRequestSeconds = 10;
        private const int MaxPendingRequests = 32;
        // Per-connection send buffer: a whole maximum-size save snapshot fits, with room for gameplay traffic.
        private const int SendBufferBytes = 24 * 1024 * 1024;

        private readonly IntPtr[] _messages = new IntPtr[ReceiveBatch];
        private readonly List<PendingRequest> _pending = new List<PendingRequest>();
        private readonly List<PeerId> _linkFailures = new List<PeerId>();
        private readonly Callback<SteamNetworkingMessagesSessionRequest_t> _sessionRequest;
        private readonly Callback<SteamNetworkingMessagesSessionFailed_t> _sessionFailed;
        private double _nextFailureLogAt;
        private int _suppressedFailures;
        private bool _disposed;

        public SteamTransport(CSteamID localId)
        {
            LocalId = new PeerId(localId.m_SteamID);
            _sessionRequest = Callback<SteamNetworkingMessagesSessionRequest_t>.Create(OnSessionRequest);
            _sessionFailed = Callback<SteamNetworkingMessagesSessionFailed_t>.Create(OnSessionFailed);
            RaiseSendBuffer();
            try
            {
                // Starts fetching the relay network config early so the first connection is fast.
                SteamNetworkingUtils.InitRelayNetworkAccess();
            }
            catch (Exception e)
            {
                NetLog.Warn($"{Log}InitRelayNetworkAccess failed: {e.Message}");
            }
        }

        public PeerId LocalId { get; }

        /// <summary>Decides whether an unsolicited session from a peer is accepted. Null refuses all.</summary>
        public Func<PeerId, bool> AcceptFilter { get; set; }

        public long PacketsSent { get; private set; }

        public long PacketsReceived { get; private set; }

        public long PacketsDropped { get; private set; }

        public unsafe void Send(PeerId to, Channel channel, Reliability reliability, ArraySegment<byte> data)
        {
            if (_disposed)
                return;
            if (data.Array == null || data.Count == 0 || !to.IsValid || to == LocalId)
            {
                PacketsDropped++;
                return;
            }
            if (data.Count > ProtocolInfo.MaxPacketSize)
            {
                PacketsDropped++;
                NetLog.Error($"{Log}Dropping {data.Count}-byte packet to {to}; the limit is {ProtocolInfo.MaxPacketSize}.");
                return;
            }

            var identity = new SteamNetworkingIdentity();
            identity.SetSteamID64(to.Value);
            int flags = (reliability == Reliability.Reliable
                ? Constants.k_nSteamNetworkingSend_Reliable
                : Constants.k_nSteamNetworkingSend_Unreliable) | Constants.k_nSteamNetworkingSend_AutoRestartBrokenSession;

            EResult result;
            fixed (byte* p = &data.Array[data.Offset])
                result = SteamNetworkingMessages.SendMessageToUser(ref identity, (IntPtr)p, (uint)data.Count, flags, (int)channel);

            if (result == EResult.k_EResultOK)
            {
                PacketsSent++;
                return;
            }
            PacketsDropped++;
            LogSendFailure(to, result);
            // Steam discarded a reliable message (buffer full, session gone): the stream to this peer has a gap.
            if (reliability == Reliability.Reliable)
                QueueLinkFailure(to);
        }

        /// <summary>Moves the peers whose link failed since the last call into <paramref name="into"/>.</summary>
        public void DrainLinkFailures(List<PeerId> into)
        {
            if (into == null)
                throw new ArgumentNullException(nameof(into));
            into.AddRange(_linkFailures);
            _linkFailures.Clear();
        }

        public void Poll(List<IncomingPacket> into)
        {
            if (into == null)
                throw new ArgumentNullException(nameof(into));
            if (_disposed)
                return;
            if (_pending.Count > 0)
                RetryPendingRequests();
            for (int channel = 0; channel < ChannelInfo.Count; channel++)
            {
                int total = 0;
                while (total < MaxReceivesPerChannelPerPoll)
                {
                    int count = SteamNetworkingMessages.ReceiveMessagesOnChannel(channel, _messages, ReceiveBatch);
                    if (count <= 0)
                        break;
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr ptr = _messages[i];
                        _messages[i] = IntPtr.Zero;
                        try
                        {
                            Receive(ptr, (Channel)channel, into);
                        }
                        finally
                        {
                            SteamNetworkingMessage_t.Release(ptr);
                        }
                    }
                    total += count;
                    if (count < ReceiveBatch)
                        break;
                }
            }
        }

        public void Disconnect(PeerId peer)
        {
            if (_disposed || !peer.IsValid)
                return;
            var identity = new SteamNetworkingIdentity();
            identity.SetSteamID64(peer.Value);
            SteamNetworkingMessages.CloseSessionWithUser(ref identity);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _pending.Clear();
            _linkFailures.Clear();
            _sessionRequest.Dispose();
            _sessionFailed.Dispose();
        }

        private unsafe void Receive(IntPtr ptr, Channel channel, List<IncomingPacket> into)
        {
            if (ptr == IntPtr.Zero)
                return;
            // Read the native message in place: FromIntPtr would marshal (and box) a copy of the whole struct.
            var message = (SteamNetworkingMessage_t*)ptr;
            ulong sender = message->m_identityPeer.GetSteamID64();
            int size = message->m_cbSize;
            IntPtr payload = message->m_pData;
            if (sender == 0 || size <= 0 || size > ProtocolInfo.MaxPacketSize || payload == IntPtr.Zero)
            {
                PacketsDropped++;
                return;
            }
            var data = new byte[size];
            System.Runtime.InteropServices.Marshal.Copy(payload, data, 0, size);
            PacketsReceived++;
            into.Add(new IncomingPacket(new PeerId(sender), channel, data));
        }

        private void OnSessionRequest(SteamNetworkingMessagesSessionRequest_t request)
        {
            if (_disposed)
                return;
            var identity = request.m_identityRemote;
            var peer = new PeerId(identity.GetSteamID64());
            if (TryAccept(peer, ref identity))
                return;
            for (int i = 0; i < _pending.Count; i++)
            {
                if (_pending[i].Peer == peer)
                    return;
            }
            if (_pending.Count >= MaxPendingRequests)
            {
                NetLog.Warn($"{Log}Ignored session request from {peer} (not in the lobby).");
                return;
            }
            NetLog.Debug($"{Log}Holding session request from {peer} until it shows up in the lobby.");
            _pending.Add(new PendingRequest(peer, UnityEngine.Time.realtimeSinceStartupAsDouble + PendingRequestSeconds));
        }

        private void RetryPendingRequests()
        {
            double now = UnityEngine.Time.realtimeSinceStartupAsDouble;
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                var pending = _pending[i];
                var identity = new SteamNetworkingIdentity();
                identity.SetSteamID64(pending.Peer.Value);
                if (TryAccept(pending.Peer, ref identity))
                {
                    _pending.RemoveAt(i);
                }
                else if (now >= pending.ExpiresAt)
                {
                    _pending.RemoveAt(i);
                    NetLog.Warn($"{Log}Ignored session request from {pending.Peer} (not in the lobby).");
                }
            }
        }

        private bool TryAccept(PeerId peer, ref SteamNetworkingIdentity identity)
        {
            bool accept;
            try
            {
                accept = peer.IsValid && AcceptFilter != null && AcceptFilter(peer);
            }
            catch (Exception e)
            {
                NetLog.Error($"{Log}Accept filter threw for {peer}: {e.Message}");
                accept = false;
            }
            if (!accept)
                return false;
            SteamNetworkingMessages.AcceptSessionWithUser(ref identity);
            NetLog.Info($"{Log}Accepted session from {peer}.");
            return true;
        }

        private void OnSessionFailed(SteamNetworkingMessagesSessionFailed_t failed)
        {
            if (_disposed)
                return;
            var info = failed.m_info;
            ulong remote = info.m_identityRemote.GetSteamID64();
            NetLog.Warn($"{Log}Session with {remote} failed: {(ESteamNetConnectionEnd)info.m_eEndReason} {info.m_szEndDebug}");
            // Anything queued or in flight on that session is lost; a later send would quietly open a fresh one.
            QueueLinkFailure(new PeerId(remote));
        }

        private void QueueLinkFailure(PeerId peer)
        {
            if (!peer.IsValid || _linkFailures.Contains(peer))
                return;
            _linkFailures.Add(peer);
        }

        private static unsafe void RaiseSendBuffer()
        {
            try
            {
                int value = SendBufferBytes;
                bool ok = SteamNetworkingUtils.SetConfigValue(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendBufferSize,
                    ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero,
                    ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, (IntPtr)(&value));
                if (!ok)
                    NetLog.Warn($"{Log}Steam refused the larger send buffer; large saves may need several attempts.");
            }
            catch (Exception e)
            {
                NetLog.Warn($"{Log}Setting the send buffer size failed: {e.Message}");
            }
        }

        private void LogSendFailure(PeerId to, EResult result)
        {
            double now = UnityEngine.Time.realtimeSinceStartupAsDouble;
            if (now < _nextFailureLogAt)
            {
                _suppressedFailures++;
                return;
            }
            string suppressed = _suppressedFailures > 0 ? $" ({_suppressedFailures} more suppressed)" : string.Empty;
            NetLog.Warn($"{Log}Send to {to} failed: {result}{suppressed}.");
            _nextFailureLogAt = now + FailureLogInterval;
            _suppressedFailures = 0;
        }

        private readonly struct PendingRequest
        {
            public PendingRequest(PeerId peer, double expiresAt)
            {
                Peer = peer;
                ExpiresAt = expiresAt;
            }

            public readonly PeerId Peer;
            public readonly double ExpiresAt;
        }
    }
}
