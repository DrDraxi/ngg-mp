using System;
using System.Collections.Generic;
using NGGMultiplayer.Core;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Save;
using NGGMultiplayer.Core.Session;
using NGGMultiplayer.Core.Sync;
using Xunit;

namespace NGGMultiplayer.Tests.Integration
{
    public class NetSessionTests
    {
        private static readonly TransformState Origin = TransformState.Identity;

        private static NetSessionOptions FastResync(bool gate = true) => new NetSessionOptions
        {
            GateGameplayOnSaveSync = gate,
            Save = new SaveSyncOptions { MinResyncIntervalSeconds = 0.2, MaxConsecutiveFailures = 3 },
        };

        [Fact]
        public void SessionManager_ImplementsMessaging_WithModuleEventsFirst()
        {
            var hub = new LoopbackHub();
            var host = new SessionManager(hub.CreateTransport(new PeerId(1)));
            var client = new SessionManager(hub.CreateTransport(new PeerId(2)));
            ISessionMessaging hostView = host;
            ISessionMessaging clientView = client;
            var order = new List<string>();
            hostView.PlayerJoined += p => order.Add("view-joined:" + p.Value);
            host.PlayerJoined += p => order.Add("joined:" + p.Id.Value);
            hostView.PlayerLeft += p => order.Add("view-left:" + p.Value);
            host.PlayerLeft += (p, r) => order.Add("left:" + p.Id.Value);
            clientView.SessionClosed += () => order.Add("client-closed");
            client.SessionEnded += e => order.Add("client-ended");

            Assert.False(host.IsPeerConnected(new PeerId(2)));
            host.StartHost();
            client.Connect(host.LocalId);
            for (int i = 0; i < 5; i++)
            {
                host.Update(i * 0.01);
                client.Update(i * 0.01);
            }
            Assert.True(client.IsActive);
            Assert.True(host.IsPeerConnected(client.LocalId));
            Assert.False(host.IsPeerConnected(host.LocalId));
            Assert.True(client.IsPeerConnected(host.LocalId));
            Assert.False(client.IsPeerConnected(new PeerId(3)));

            // A client's broadcast goes to the host; excluding the host sends nothing.
            int received = 0;
            host.RegisterHandler(MessageType.GameFirst, (from, r) => received++);
            var packet = new ArraySegment<byte>(new[] { MessageType.GameFirst });
            clientView.Broadcast(Channel.Events, Reliability.Reliable, packet, PeerId.None);
            clientView.Broadcast(Channel.Events, Reliability.Reliable, packet, host.LocalId);
            for (int i = 5; i < 8; i++)
                host.Update(i * 0.01);
            Assert.Equal(1, received);

            client.Leave();
            for (int i = 8; i < 11; i++)
                host.Update(i * 0.01);
            Assert.Equal(new[] { "view-joined:2", "joined:2", "client-closed", "client-ended", "view-left:2", "left:2" }, order);
            Assert.False(client.IsPeerConnected(host.LocalId));
        }

        [Fact]
        public void Gating_HoldsEntitiesAndRpcs_UntilTheClientLoadedTheSave()
        {
            var net = new NetTestNet();
            net.Hub.LatencyMs = 20;
            var host = net.Add(1, options: FastResync());
            var flaky = new FlakySaveBackend(new InMemorySaveBackend()) { FailApply = true };
            var client = net.Add(2, flaky, FastResync());
            host.Net.StartHost();
            client.Net.Connect(host.Id);

            Assert.True(net.RunUntil(() => flaky.FailedApplies > 0, 2));
            Assert.True(client.Net.IsActive);
            Assert.False(client.Net.IsReady);
            Assert.False(host.Net.IsPeerReady(client.Id));
            Assert.True(host.Net.Session.IsPeerConnected(client.Id));

            var ball = host.Net.Entities.Spawn("ball", host.Id, Origin);
            Assert.Throws<ArgumentException>(() => host.Net.Entities.Spawn("cart", client.Id, Origin));
            Assert.True(host.SendChat("too early"));
            net.Run(0.1);
            Assert.False(client.Factory.Has(ball.Id));
            Assert.Empty(client.Chat);
            Assert.Equal(0, client.Net.Entities.Count);

            flaky.FailApply = false;
            Assert.True(net.RunUntil(() => client.Net.IsReady && client.Factory.Has(ball.Id), 3));
            Assert.True(host.Net.IsPeerReady(client.Id));
            Assert.Equal(new[] { client.Id }, host.ReadyPeers);
            Assert.Equal(0, client.Factory.CreatedBeforeSync);
            Assert.Empty(client.Chat);

            // Now the client is a full participant.
            var cart = host.Net.Entities.Spawn("cart", client.Id, Origin);
            Assert.True(host.SendChat("welcome"));
            net.Run(0.2);
            Assert.True(client.Net.Entities.Get(cart.Id).IsLocallyOwned);
            Assert.Equal(new[] { (host.Id, "welcome") }, client.Chat);
        }

        [Fact]
        public void WithoutGating_EntitiesArriveBeforeTheSave()
        {
            var net = new NetTestNet();
            var host = net.Add(1, options: FastResync(gate: false));
            var flaky = new FlakySaveBackend(new InMemorySaveBackend()) { FailApply = true };
            var client = net.Add(2, flaky, FastResync(gate: false));
            host.Net.StartHost();
            var ball = host.Net.Entities.Spawn("ball", host.Id, Origin);
            client.Net.Connect(host.Id);

            Assert.True(net.RunUntil(() => client.Factory.Has(ball.Id), 2));
            Assert.False(client.Net.IsReady);
            Assert.Equal(1, client.Factory.CreatedBeforeSync);

            flaky.FailApply = false;
            Assert.True(net.RunUntil(() => host.Net.IsPeerReady(client.Id) && client.Net.IsReady, 3));
            Assert.Equal(new[] { client.Id }, host.ReadyPeers);
        }

        [Fact]
        public void SaveSyncFailure_LeavesTheSession_AndRestoresTheLocalSave()
        {
            var net = new NetTestNet();
            var host = net.Add(1, options: FastResync());
            host.Save.Set("challenge", "a", new byte[] { 1 });
            var inner = new InMemorySaveBackend();
            inner.Set("challenge", "mine", new byte[] { 9 });
            var original = inner.CaptureSnapshot();
            var flaky = new FlakySaveBackend(inner) { FailApply = true };
            var client = net.Add(2, flaky, FastResync());
            host.Net.StartHost();
            client.Net.Connect(host.Id);

            Assert.True(net.RunUntil(() => client.Ended.Count == 1, 10));
            Assert.Equal(SessionEndReason.LocalLeft, client.Ended[0].Reason);
            Assert.Equal(3, flaky.FailedApplies);
            Assert.Equal(1, inner.RestoreCount);
            Assert.False(inner.SuppressDiskWrites);
            Assert.Equal(original, inner.CaptureSnapshot());
            Assert.Equal(new[] { "started", "ended:LocalLeft" }, client.Log);
            Assert.True(net.RunUntil(() => !host.Net.Session.IsPeerConnected(client.Id), 1));
        }

        [Fact]
        public void RpcRegistrations_AndOwnershipValidator_CarryOverAcrossSessions()
        {
            var net = new NetTestNet();
            var host = net.Add(1);
            Func<PeerId, NetEntity, bool> validator = (p, e) => false;
            host.Net.OwnershipValidator = validator;
            Assert.Null(host.Net.Rpc);

            host.Net.StartHost();
            Assert.True(host.Net.Rpc.IsRegistered(NetPeer.ChatRpc));
            Assert.Same(validator, host.Net.Entities.OwnershipValidator);

            // Registering while active reaches the live router too.
            host.Net.RegisterRpc("test.live", (s, r) => { });
            Assert.True(host.Net.Rpc.IsRegistered("test.live"));
            Assert.Throws<InvalidOperationException>(() => host.Net.RegisterRpc(NetPeer.ChatRpc, (s, r) => { }));
            Assert.Throws<InvalidOperationException>(() => host.Net.RegisterRpc("test.live", (s, r) => { }));

            var firstRouter = host.Net.Rpc;
            host.Net.Leave();
            Assert.Null(host.Net.Rpc);
            Assert.Null(host.Net.Entities);
            Assert.Null(host.Net.SaveHost);

            host.Net.StartHost();
            Assert.NotSame(firstRouter, host.Net.Rpc);
            Assert.True(host.Net.Rpc.IsRegistered(NetPeer.ChatRpc));
            Assert.True(host.Net.Rpc.IsRegistered("test.live"));
            Assert.Same(validator, host.Net.Entities.OwnershipValidator);

            Assert.True(host.Net.UnregisterRpc("test.live"));
            Assert.False(host.Net.Rpc.IsRegistered("test.live"));
            Assert.False(host.Net.UnregisterRpc("test.live"));
            Assert.Equal(new[] { "started", "ready", "ended:LocalLeft", "started", "ready" }, host.Log);
        }

        [Fact]
        public void FailedConnect_RaisesNeitherStartedNorEnded()
        {
            var net = new NetTestNet();
            var client = net.Add(2, options: new NetSessionOptions { Session = new SessionConfig { ConnectTimeoutSeconds = 0.5 } });
            int sessionEnded = 0;
            client.Net.Session.SessionEnded += e => sessionEnded++;
            client.Net.Connect(new PeerId(99));
            net.Run(1);
            Assert.Equal(1, sessionEnded);
            Assert.Empty(client.Log);
            Assert.False(client.Net.IsActive);
            Assert.Equal(0, client.Save.BackupCount);
        }

        [Fact]
        public void Dispose_EndsTheSession_AndRestoresTheClientSave()
        {
            var net = new NetTestNet();
            var host = net.Add(1);
            host.Save.Set("challenge", "a", new byte[] { 1 });
            var client = net.Add(2);
            client.Save.Set("challenge", "mine", new byte[] { 2 });
            var original = client.Save.CaptureSnapshot();
            host.Net.StartHost();
            client.Net.Connect(host.Id);
            Assert.True(net.RunUntil(() => client.Net.IsReady, 2));

            client.Net.Dispose();
            Assert.Equal(1, client.Save.RestoreCount);
            Assert.Equal(original, client.Save.CaptureSnapshot());
            Assert.Equal(SessionEndReason.LocalLeft, Assert.Single(client.Ended).Reason);
            Assert.Throws<ObjectDisposedException>(() => client.Net.Update(10));
            client.Net.Dispose();

            net.Peers.Remove(client);
            Assert.True(net.RunUntil(() => !host.Net.Session.IsPeerConnected(client.Id), 1));
        }

        [Fact]
        public void Constructor_ValidatesOptions()
        {
            var hub = new LoopbackHub();
            var backend = new InMemorySaveBackend();
            Assert.Throws<ArgumentNullException>(() => new NetSession(null, backend));
            Assert.Throws<ArgumentNullException>(() => new NetSession(hub.CreateTransport(new PeerId(1)), null));
            Assert.Throws<ArgumentOutOfRangeException>(() => new NetSession(hub.CreateTransport(new PeerId(2)), backend,
                options: new NetSessionOptions { Entities = new NetEntityRegistryOptions { SendRate = 0 } }));
            Assert.Throws<ArgumentOutOfRangeException>(() => new NetSession(hub.CreateTransport(new PeerId(3)), backend,
                options: new NetSessionOptions { Save = new SaveSyncOptions { MaxConsecutiveFailures = 0 } }));
        }
    }
}
