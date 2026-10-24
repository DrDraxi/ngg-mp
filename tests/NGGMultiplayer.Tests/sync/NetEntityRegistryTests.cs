using System;
using System.Linq;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Sync;
using Xunit;

namespace NGGMultiplayer.Tests.Sync
{
    public class NetEntityRegistryTests
    {
        private static readonly PeerId Host = SyncNet.HostId;

        private static void RunMoving(SyncNet net, TestEntity owner, double seconds, float speed, double step = 0.01)
        {
            int steps = (int)Math.Round(seconds / step);
            for (int i = 0; i < steps; i++)
            {
                net.Hub.Advance(step);
                owner.Current = States.Moving((float)(speed * net.Now), speed, net.Now);
                net.Frame();
            }
        }

        private static byte[] StatePacket(byte type, NetEntityId id, byte epoch, in TransformState state)
        {
            var w = PacketBuilder.Begin(type);
            w.WriteByte(1);
            NetEntityId.Write(w, id);
            w.WriteByte(epoch);
            TransformCodec.Write(w, state);
            return w.ToArray();
        }

        private static (SyncNet Net, SyncNode C1, SyncNode C2) ThreePeers(NetEntityRegistryOptions options = null)
        {
            var net = new SyncNet(options, withRpc: false);
            var c1 = net.Connect(1);
            var c2 = net.Connect(2);
            net.Flush();
            return (net, c1, c2);
        }

        // ---- Spawn / despawn -------------------------------------------------------------------------

        [Fact]
        public void Spawn_IsMirroredOnClients()
        {
            var (net, c1, c2) = ThreePeers();
            var hostObj = new TestEntity(null);
            var initial = States.At(5, 1, 2);
            initial.Flags = 3;
            var e = net.Host.Registry.Spawn("ball", Host, initial, NetEntityFlags.OwnershipLocked, new byte[] { 9, 8 }, hostObj);
            net.Flush();

            Assert.True(e.Id.IsValid);
            Assert.True(e.IsLocallyOwned);
            Assert.Same(hostObj, e.Instance);
            Assert.Empty(net.Host.Factory.Requests);

            foreach (var c in new[] { c1, c2 })
            {
                var r = c.Registry.Get(e.Id);
                Assert.NotNull(r);
                Assert.Equal("ball", r.PrefabKey);
                Assert.Equal(Host, r.Owner);
                Assert.False(r.IsLocallyOwned);
                Assert.Equal(NetEntityFlags.OwnershipLocked, r.Flags);
                Assert.Equal(new byte[] { 9, 8 }, r.SpawnData);
                Assert.Equal(new NetVector3(5, 1, 2), r.LastState.Position);
                Assert.Equal(3, r.LastState.Flags);
                Assert.Same(c.Replica(e.Id), r.Instance);
                Assert.Contains("spawn:" + e.Id.Value, c.Events);
            }
        }

        [Fact]
        public void Spawn_WithoutInstance_UsesFactoryOnHost()
        {
            var (net, c1, _) = ThreePeers();
            var e = net.Host.Registry.Spawn("avatar", c1.Id, States.At(1));
            Assert.Same(net.Host.Factory[e.Id], e.Instance);
            Assert.False(e.IsLocallyOwned);
            net.Flush();
            var mine = c1.Registry.Get(e.Id);
            Assert.True(mine.IsLocallyOwned);
            Assert.True(c1.Factory.Requests.Single().IsLocallyOwned);
        }

        [Fact]
        public void Ids_AreUniqueAndNonZero()
        {
            var net = new SyncNet(withRpc: false);
            var ids = Enumerable.Range(0, 50).Select(_ => net.Host.Registry.Spawn("x", Host, States.At(0)).Id).ToList();
            Assert.Equal(50, ids.Distinct().Count());
            Assert.DoesNotContain(NetEntityId.None, ids);
        }

        [Fact]
        public void Spawn_ValidatesArguments()
        {
            var net = new SyncNet(new NetEntityRegistryOptions { MaxEntities = 2 }, withRpc: false);
            var reg = net.Host.Registry;
            Assert.Throws<ArgumentException>(() => reg.Spawn("", Host, States.At(0)));
            Assert.Throws<ArgumentException>(() => reg.Spawn(new string('k', SyncProtocol.MaxPrefabKeyBytes + 1), Host, States.At(0)));
            Assert.Throws<ArgumentException>(() => reg.Spawn("x", new PeerId(77), States.At(0)));
            Assert.Throws<ArgumentOutOfRangeException>(() => reg.Spawn("x", Host, States.At(0), (NetEntityFlags)0x80));
            Assert.Throws<ArgumentException>(() => reg.Spawn("x", Host, States.At(float.NaN)));
            Assert.Throws<ArgumentException>(() => reg.Spawn("x", Host, States.At(0), spawnData: new byte[SyncProtocol.MaxSpawnDataBytes + 1]));
            reg.Spawn("x", Host, States.At(0));
            reg.Spawn("x", Host, States.At(0));
            Assert.Throws<InvalidOperationException>(() => reg.Spawn("x", Host, States.At(0)));
        }

        [Fact]
        public void Spawn_CopiesSpawnData()
        {
            var net = new SyncNet(withRpc: false);
            var data = new byte[] { 1 };
            var e = net.Host.Registry.Spawn("x", Host, States.At(0), spawnData: data);
            data[0] = 2;
            Assert.Equal(1, e.SpawnData[0]);
        }

        [Fact]
        public void HostOnlyOperations_ThrowOnClients()
        {
            var (net, c1, _) = ThreePeers();
            var e = net.Host.Registry.Spawn("x", Host, States.At(0));
            net.Flush();
            Assert.Throws<InvalidOperationException>(() => c1.Registry.Spawn("x", c1.Id, States.At(0)));
            Assert.Throws<InvalidOperationException>(() => c1.Registry.Despawn(e.Id));
            Assert.Throws<InvalidOperationException>(() => c1.Registry.SetOwner(e.Id, c1.Id));
            Assert.Throws<InvalidOperationException>(() => c1.Registry.ReplicateAllTo(Host));
        }

        [Fact]
        public void Despawn_RemovesEverywhere()
        {
            var (net, c1, c2) = ThreePeers();
            var hostObj = new TestEntity(null);
            var e = net.Host.Registry.Spawn("x", Host, States.At(0), instance: hostObj);
            net.Flush();
            var replica = c1.Replica(e.Id);

            Assert.True(net.Host.Registry.Despawn(e.Id));
            Assert.False(net.Host.Registry.Despawn(e.Id));
            net.Flush();

            Assert.False(e.IsSpawned);
            Assert.Null(e.Instance);
            Assert.Equal(DespawnReason.Despawned, hostObj.Despawned);
            Assert.Equal(DespawnReason.Despawned, replica.Despawned);
            Assert.Null(c1.Registry.Get(e.Id));
            Assert.Equal(0, c2.Registry.Count);
            Assert.Contains("despawn:" + e.Id.Value + ":Despawned", c2.Events);
        }

        [Fact]
        public void LateJoiner_ReceivesExistingEntitiesWithCurrentOwnerAndPose()
        {
            var net = new SyncNet(withRpc: false);
            var c1 = net.Connect(1);
            net.Flush();
            var hostObj = new TestEntity(null);
            var a = net.Host.Registry.Spawn("a", Host, States.At(0), instance: hostObj);
            var b = net.Host.Registry.Spawn("b", c1.Id, States.At(0), NetEntityFlags.DespawnOnOwnerLeave);
            net.Flush();
            c1.Replica(b.Id).Current = States.At(42);
            hostObj.Current = States.At(7);
            net.Run(0.2);

            var late = net.Connect(3);
            net.Flush();
            Assert.Equal(2, late.Registry.Count);
            var la = late.Registry.Get(a.Id);
            var lb = late.Registry.Get(b.Id);
            Assert.Equal(7f, la.LastState.Position.x);
            Assert.Equal(c1.Id, lb.Owner);
            Assert.Equal(42f, lb.LastState.Position.x);
            Assert.Equal(NetEntityFlags.DespawnOnOwnerLeave, lb.Flags);
            Assert.Equal(new[] { a.Id, b.Id }, late.Registry.Entities.Select(e => e.Id));
        }

        [Fact]
        public void ReplicateOnJoinDisabled_WaitsForExplicitReplication()
        {
            var net = new SyncNet(new NetEntityRegistryOptions { ReplicateOnJoin = false }, withRpc: false);
            net.Host.Registry.Spawn("a", Host, States.At(0));
            var c1 = net.Connect(1);
            net.Flush();
            Assert.Equal(0, c1.Registry.Count);
            net.Host.Registry.ReplicateAllTo(c1.Id);
            net.Flush();
            Assert.Equal(1, c1.Registry.Count);
            Assert.Throws<ArgumentException>(() => net.Host.Registry.ReplicateAllTo(new PeerId(99)));
        }

        // ---- State streaming -------------------------------------------------------------------------

        [Fact]
        public void HostOwnedState_StreamsAndClientsInterpolateDelayed()
        {
            var (net, c1, c2) = ThreePeers();
            var hostObj = new TestEntity(null);
            var e = net.Host.Registry.Spawn("ball", Host, States.At(0), instance: hostObj);
            RunMoving(net, hostObj, 1.0, 10f);

            foreach (var c in new[] { c1, c2 })
            {
                var replica = c.Replica(e.Id);
                Assert.NotEmpty(replica.Applied);
                Assert.Equal(SnapshotSampleKind.Interpolated, replica.LastKind);
                Assert.Equal(10 * (net.Now - 0.1), replica.LastApplied.Position.x, 2);
            }
            Assert.Empty(hostObj.Applied);
            Assert.True(c1.Session.CountReceived(MessageType.State) > 0);
            Assert.Equal(0, c1.Session.CountReceived(MessageType.StateRelayBatch));
        }

        [Fact]
        public void ClientOwnedState_IsRelayedToOthersButNotEchoed()
        {
            var (net, c1, c2) = ThreePeers();
            var e = net.Host.Registry.Spawn("avatar", c1.Id, States.At(0));
            net.Flush();
            var mine = c1.Replica(e.Id);
            RunMoving(net, mine, 1.0, 5f);

            Assert.Empty(mine.Applied);
            Assert.Equal(0, c1.Session.CountReceived(MessageType.StateRelayBatch));
            Assert.True(c2.Session.CountReceived(MessageType.StateRelayBatch) > 0);
            Assert.True(net.Host.Registry.StatesRelayed > 0);
            Assert.Equal(0, net.Host.Registry.StatesRejected);

            Assert.Equal(5 * (net.Now - 0.1), net.Host.Replica(e.Id).LastApplied.Position.x, 2);
            Assert.Equal(5 * (net.Now - 0.1), c2.Replica(e.Id).LastApplied.Position.x, 2);
            Assert.Equal(c1.Id, c2.Registry.Get(e.Id).Owner);
        }

        [Fact]
        public void Relay_WorksWithLatency()
        {
            var (net, c1, c2) = ThreePeers(new NetEntityRegistryOptions { InterpolationDelay = 0.15 });
            net.Hub.LatencyMs = 30;
            var e = net.Host.Registry.Spawn("avatar", c1.Id, States.At(0));
            net.Run(0.1);
            RunMoving(net, c1.Replica(e.Id), 1.0, 4f);
            // 60 ms of two-hop latency is inside the 150 ms delay, so the sample is still an interpolation.
            Assert.Equal(SnapshotSampleKind.Interpolated, c2.Replica(e.Id).LastKind);
            Assert.Equal(4 * (net.Now - 0.15), c2.Replica(e.Id).LastApplied.Position.x, 2);
        }

        [Fact]
        public void StatesFromNonOwners_AreRejectedAndNotRelayed()
        {
            var (net, c1, c2) = ThreePeers();
            var e = net.Host.Registry.Spawn("avatar", c1.Id, States.At(0));
            net.Flush();
            int relayedBefore = c1.Session.CountReceived(MessageType.StateRelayBatch);

            // c2 forges a state for c1's entity, with the right epoch.
            SyncNet.SendRaw(c2, Host, StatePacket(MessageType.State, e.Id, e.OwnerEpoch, States.At(666, time: net.Now)), Channel.State, Reliability.Unreliable);
            // ...and for an entity that does not exist.
            SyncNet.SendRaw(c2, Host, StatePacket(MessageType.State, new NetEntityId(999), 0, States.At(1, time: net.Now)), Channel.State, Reliability.Unreliable);
            net.Flush();

            Assert.Equal(2, net.Host.Registry.StatesRejected);
            Assert.Equal(relayedBefore, c1.Session.CountReceived(MessageType.StateRelayBatch));
            Assert.NotEqual(666f, e.LastState.Position.x);
        }

        [Fact]
        public void HostRejectsStatesFarInTheFuture()
        {
            var (net, c1, _) = ThreePeers();
            var e = net.Host.Registry.Spawn("avatar", c1.Id, States.At(0));
            net.Flush();
            SyncNet.SendRaw(c1, Host, StatePacket(MessageType.State, e.Id, e.OwnerEpoch, States.At(1, time: net.Now + 100)), Channel.State, Reliability.Unreliable);
            net.Flush();
            Assert.Equal(1, net.Host.Registry.StatesRejected);
        }

        [Fact]
        public void ClientsRejectStatesWithStaleEpoch()
        {
            var (net, c1, c2) = ThreePeers();
            var e = net.Host.Registry.Spawn("ball", Host, States.At(0));
            net.Flush();
            net.Host.Registry.SetOwner(e.Id, c1.Id);
            net.Flush();
            Assert.Equal(1, c2.Registry.Get(e.Id).OwnerEpoch);

            // A host-era state (epoch 0) arriving after the transfer.
            long before = c2.Registry.StatesRejected;
            SyncNet.SendRaw(net.Host, c2.Id, StatePacket(MessageType.State, e.Id, 0, States.At(50, time: net.Now)), Channel.State, Reliability.Unreliable);
            net.Flush();
            Assert.Equal(before + 1, c2.Registry.StatesRejected);
            Assert.NotEqual(50f, c2.Registry.Get(e.Id).LastState.Position.x);
        }

        [Fact]
        public void ClientsIgnoreEntityMessagesFromNonHosts()
        {
            var (net, c1, c2) = ThreePeers();
            var w = PacketBuilder.Begin(MessageType.Spawn);
            NetEntityId.Write(w, new NetEntityId(5));
            w.WriteString("evil");
            w.WritePeerId(c2.Id);
            w.WriteByte(0);
            w.WriteByte(0);
            w.WriteBytes(Array.Empty<byte>());
            TransformCodec.Write(w, States.At(0));
            SyncNet.SendRaw(c2, c1.Id, w.ToArray());
            net.Flush();
            Assert.Equal(0, c1.Registry.Count);

            // The same bytes from the host are accepted, proving the packet itself was valid.
            SyncNet.SendRaw(net.Host, c1.Id, w.ToArray());
            net.Flush();
            Assert.Equal(1, c1.Registry.Count);
        }

        [Fact]
        public void Host_IgnoresRelayBatchesFromClients()
        {
            var (net, c1, _) = ThreePeers();
            var e = net.Host.Registry.Spawn("avatar", c1.Id, States.At(0));
            net.Flush();
            SyncNet.SendRaw(c1, Host, StatePacket(MessageType.StateRelayBatch, e.Id, e.OwnerEpoch, States.At(9, time: net.Now + 0.5)), Channel.State, Reliability.Unreliable);
            net.Flush();
            Assert.NotEqual(9f, e.LastState.Position.x);
            Assert.Equal(0, net.Host.Registry.StatesRejected);
        }

        [Fact]
        public void SendRate_IsRespected()
        {
            var (net, c1, _) = ThreePeers(new NetEntityRegistryOptions { SendRate = 10 });
            net.Host.Registry.Spawn("ball", Host, States.At(0), instance: new TestEntity(null));
            net.Flush();
            long before = net.Host.Registry.StatesSent;
            net.Run(2.0, 1.0 / 120);
            long sent = net.Host.Registry.StatesSent - before;
            Assert.InRange(sent, 19, 21);
            Assert.InRange(c1.Session.CountReceived(MessageType.State), 19, 23);
        }

        [Fact]
        public void ManyEntities_AreSplitIntoPacketsUnderTheUnreliableLimit()
        {
            var (net, c1, _) = ThreePeers();
            var ids = Enumerable.Range(0, 60)
                .Select(i => net.Host.Registry.Spawn("e", Host, States.At(i), instance: new TestEntity(null) { Current = States.Moving(i, 1) }).Id)
                .ToList();
            net.Flush();
            var received = new System.Collections.Generic.List<IncomingPacket>();
            net.Hub.Advance(0.1);
            net.Host.Registry.Update(net.Now);
            c1.Session.Transport.Poll(received);

            Assert.True(received.Count >= 2);
            Assert.All(received, p => Assert.True(p.Data.Length <= ProtocolInfo.MaxUnreliablePacketSize));
            int entries = received.Sum(p => (int)p.Data[1]);
            Assert.Equal(60, entries);
        }

        [Fact]
        public void Teleport_SnapsOnReceivers()
        {
            var (net, c1, _) = ThreePeers();
            var hostObj = new TestEntity(null) { Current = States.At(0) };
            var e = net.Host.Registry.Spawn("ball", Host, States.At(0), instance: hostObj);
            net.Run(0.5);
            var jump = States.At(100);
            jump.Teleport = true;
            hostObj.Current = jump;
            net.Run(0.04);
            hostObj.Current = States.At(100);
            net.Run(0.5);

            var xs = c1.Replica(e.Id).Applied.Select(a => a.State.Position.x).ToList();
            Assert.Contains(0f, xs);
            Assert.Equal(100f, xs.Last());
            Assert.DoesNotContain(xs, x => x > 0.001f && x < 99.999f);
        }

        [Fact]
        public void CompactEncoding_WorksEndToEnd()
        {
            var (net, c1, _) = ThreePeers(new NetEntityRegistryOptions { Encoding = TransformEncoding.Compact });
            var hostObj = new TestEntity(null);
            var e = net.Host.Registry.Spawn("ball", Host, States.At(0), instance: hostObj);
            RunMoving(net, hostObj, 0.5, 3f);
            Assert.Equal(3 * (net.Now - 0.1), c1.Replica(e.Id).LastApplied.Position.x, 2);
        }

        [Fact]
        public void AutoApplyDisabled_OnlyBuffers()
        {
            var (net, c1, _) = ThreePeers(new NetEntityRegistryOptions { AutoApply = false });
            var hostObj = new TestEntity(null);
            var e = net.Host.Registry.Spawn("ball", Host, States.At(0), instance: hostObj);
            RunMoving(net, hostObj, 0.3, 1f);
            var replica = c1.Registry.Get(e.Id);
            Assert.Empty(c1.Replica(e.Id).Applied);
            Assert.True(replica.Snapshots.Count > 3);
            Assert.Equal(SnapshotSampleKind.Interpolated, replica.Snapshots.Sample(net.Now - 0.1, out _));
        }

        [Fact]
        public void FaultyEntities_DoNotStopOthers()
        {
            var (net, c1, _) = ThreePeers();
            var bad = new TestEntity(null) { ThrowOnCapture = true };
            var nan = new TestEntity(null) { Current = States.At(float.NaN) };
            var skip = new TestEntity(null) { CaptureEnabled = false };
            var good = new TestEntity(null) { Current = States.At(3) };
            net.Host.Registry.Spawn("bad", Host, States.At(0), instance: bad);
            net.Host.Registry.Spawn("nan", Host, States.At(0), instance: nan);
            var s = net.Host.Registry.Spawn("skip", Host, States.At(0), instance: skip);
            var g = net.Host.Registry.Spawn("good", Host, States.At(0), instance: good);
            net.Run(0.5);

            Assert.True(bad.Captures > 0);
            Assert.True(nan.Captures > 0);
            Assert.Equal(3f, c1.Registry.Get(g.Id).LastState.Position.x);
            Assert.Equal(0f, c1.Registry.Get(s.Id).LastState.Position.x);
        }

        [Fact]
        public void FactoryFailures_KeepEntityTrackedWithoutInstance()
        {
            var (net, c1, c2) = ThreePeers();
            c1.Factory.Throw = true;
            c2.Factory.ReturnNull = true;
            var hostObj = new TestEntity(null);
            var e = net.Host.Registry.Spawn("x", Host, States.At(0), instance: hostObj);
            RunMoving(net, hostObj, 0.3, 1f);
            Assert.Null(c1.Registry.Get(e.Id).Instance);
            Assert.Null(c2.Registry.Get(e.Id).Instance);
            Assert.True(c1.Registry.Get(e.Id).LastState.Position.x > 0);
            net.Host.Registry.Despawn(e.Id);
            net.Flush();
            Assert.Equal(0, c1.Registry.Count);
        }

        // ---- Ownership -------------------------------------------------------------------------------

        [Fact]
        public void RequestOwnership_IsGrantedForHostOwnedEntity()
        {
            var (net, c1, c2) = ThreePeers();
            var hostObj = new TestEntity(null);
            var e = net.Host.Registry.Spawn("ball", Host, States.At(0), instance: hostObj);
            net.Run(0.2);

            Assert.True(c1.Registry.RequestOwnership(e.Id));
            net.Flush();

            Assert.Equal(c1.Id, e.Owner);
            Assert.Equal(1, e.OwnerEpoch);
            Assert.True(c1.Registry.Get(e.Id).IsLocallyOwned);
            Assert.Equal(c1.Id, c2.Registry.Get(e.Id).Owner);
            Assert.Equal((c1.Id, true), c1.Replica(e.Id).OwnershipChanges.Single());
            Assert.Equal((c1.Id, false), hostObj.OwnershipChanges.Single());
            Assert.Contains($"owner:{e.Id.Value}:100->1", c2.Events);

            // The new owner streams; the host and c2 follow.
            RunMoving(net, c1.Replica(e.Id), 0.5, 2f);
            Assert.Equal(2 * (net.Now - 0.1), hostObj.LastApplied.Position.x, 2);
            Assert.Equal(2 * (net.Now - 0.1), c2.Replica(e.Id).LastApplied.Position.x, 2);
        }

        [Fact]
        public void OwnershipTransfer_ContinuesFromLastPose()
        {
            var (net, c1, _) = ThreePeers();
            var hostObj = new TestEntity(null) { Current = States.At(10) };
            var e = net.Host.Registry.Spawn("ball", Host, States.At(0), instance: hostObj);
            net.Run(0.3);
            c1.Registry.RequestOwnership(e.Id);
            net.Flush();
            // The host's buffer was seeded with its own last pose, so it holds there rather than jumping.
            net.Run(0.02);
            Assert.Equal(10f, hostObj.LastApplied.Position.x);
        }

        [Fact]
        public void RequestOwnership_DefaultPolicyRefusesStealingAndLocked()
        {
            var (net, c1, c2) = ThreePeers();
            var owned = net.Host.Registry.Spawn("a", c1.Id, States.At(0));
            var locked = net.Host.Registry.Spawn("b", Host, States.At(0), NetEntityFlags.OwnershipLocked);
            net.Flush();

            c2.Registry.RequestOwnership(owned.Id);
            c2.Registry.RequestOwnership(locked.Id);
            net.Flush();

            Assert.Equal(c1.Id, owned.Owner);
            Assert.Equal(Host, locked.Owner);
            Assert.Contains($"denied:{owned.Id.Value}:1", c2.Events);
            Assert.Contains($"denied:{locked.Id.Value}:100", c2.Events);
        }

        [Fact]
        public void OwnershipValidator_DecidesAndFailsClosed()
        {
            var (net, c1, c2) = ThreePeers();
            var e = net.Host.Registry.Spawn("a", c1.Id, States.At(0));
            net.Flush();
            var asked = new System.Collections.Generic.List<PeerId>();
            net.Host.Registry.OwnershipValidator = (peer, entity) =>
            {
                asked.Add(peer);
                if (peer == c2.Id)
                    return true;
                throw new InvalidOperationException("boom");
            };

            c2.Registry.RequestOwnership(e.Id);
            net.Flush();
            Assert.Equal(c2.Id, e.Owner);
            Assert.Equal(c2.Id, c1.Registry.Get(e.Id).Owner);
            Assert.False(c1.Registry.Get(e.Id).IsLocallyOwned);

            c1.Registry.RequestOwnership(e.Id);
            net.Flush();
            Assert.Equal(c2.Id, e.Owner);
            Assert.Contains($"denied:{e.Id.Value}:2", c1.Events);
            Assert.Equal(new[] { c2.Id, c1.Id }, asked);
        }

        [Fact]
        public void RequestOwnership_OnHostTakesItImmediately()
        {
            var (net, c1, _) = ThreePeers();
            var e = net.Host.Registry.Spawn("a", c1.Id, States.At(0));
            net.Flush();
            Assert.True(net.Host.Registry.RequestOwnership(e.Id));
            Assert.True(e.IsLocallyOwned);
            net.Flush();
            Assert.False(c1.Registry.Get(e.Id).IsLocallyOwned);
            Assert.False(net.Host.Registry.RequestOwnership(new NetEntityId(1234)));
        }

        [Fact]
        public void RequestOwnership_ForUnknownEntityIsDenied()
        {
            var (net, c1, _) = ThreePeers();
            var e = net.Host.Registry.Spawn("a", Host, States.At(0));
            net.Flush();
            // The host despawns while the request is in flight.
            c1.Registry.RequestOwnership(e.Id);
            net.Host.Registry.Despawn(e.Id);
            net.Flush();
            Assert.Equal(0, c1.Registry.Count);
            Assert.Equal(0, net.Host.Registry.Count);
        }

        [Fact]
        public void ReleaseOwnership_ReturnsEntityToHostAndStopsSendingAtOnce()
        {
            var (net, c1, c2) = ThreePeers();
            var e = net.Host.Registry.Spawn("a", c1.Id, States.At(0));
            net.Flush();
            var mine = c1.Replica(e.Id);
            RunMoving(net, mine, 0.3, 1f);

            Assert.True(c1.Registry.ReleaseOwnership(e.Id));
            Assert.True(c1.Registry.Get(e.Id).ReleasePending);
            int captures = mine.Captures;
            c1.Registry.Update(net.Now + 1);
            Assert.Equal(captures, mine.Captures);
            net.Flush();

            Assert.Equal(Host, e.Owner);
            Assert.False(c1.Registry.Get(e.Id).ReleasePending);
            Assert.Equal(Host, c2.Registry.Get(e.Id).Owner);
            Assert.Equal((Host, false), mine.OwnershipChanges.Last());
            Assert.Equal((Host, true), net.Host.Replica(e.Id).OwnershipChanges.Last());

            Assert.False(c1.Registry.ReleaseOwnership(e.Id));
            Assert.False(net.Host.Registry.ReleaseOwnership(e.Id));
        }

        [Fact]
        public void ReleaseFromNonOwner_IsIgnoredByHost()
        {
            var (net, c1, c2) = ThreePeers();
            var e = net.Host.Registry.Spawn("a", c1.Id, States.At(0));
            net.Flush();
            var w = PacketBuilder.Begin(MessageType.OwnershipRelease);
            NetEntityId.Write(w, e.Id);
            SyncNet.SendRaw(c2, Host, w.ToArray());
            net.Flush();
            Assert.Equal(c1.Id, e.Owner);
        }

        [Fact]
        public void OwnerLeaving_DespawnsPlayerBoundAndHostTakesTheRest()
        {
            var (net, c1, c2) = ThreePeers();
            var avatar = net.Host.Registry.Spawn("avatar", c1.Id, States.At(0), NetEntityFlags.DespawnOnOwnerLeave);
            var ball = net.Host.Registry.Spawn("ball", c1.Id, States.At(0));
            var other = net.Host.Registry.Spawn("other", c2.Id, States.At(0), NetEntityFlags.DespawnOnOwnerLeave);
            net.Flush();
            c1.Replica(ball.Id).Current = States.At(8);
            net.Run(0.3);
            var hostBall = net.Host.Replica(ball.Id);
            var hostAvatar = net.Host.Replica(avatar.Id);

            net.Leave(c1);
            net.Flush();

            Assert.Null(net.Host.Registry.Get(avatar.Id));
            Assert.Equal(DespawnReason.OwnerLeft, hostAvatar.Despawned);
            Assert.Null(c2.Registry.Get(avatar.Id));
            Assert.Equal(DespawnReason.OwnerLeft, c2.Replica(avatar.Id).Despawned);

            Assert.Equal(Host, ball.Owner);
            Assert.Equal(Host, c2.Registry.Get(ball.Id).Owner);
            Assert.Equal((Host, true), hostBall.OwnershipChanges.Last());
            Assert.Equal(c2.Id, other.Owner);

            // The host now simulates the ball from where it was and streams it.
            int captures = hostBall.Captures;
            net.Run(0.3);
            Assert.True(hostBall.Captures > captures);
            Assert.Equal(8f, c2.Registry.Get(ball.Id).LastState.Position.x);
        }

        [Fact]
        public void SessionClosed_DropsAllEntitiesAndDeactivates()
        {
            var (net, c1, _) = ThreePeers();
            var e = net.Host.Registry.Spawn("a", Host, States.At(0));
            net.Flush();
            var replica = c1.Replica(e.Id);
            c1.Session.RaiseClosed();

            Assert.Equal(DespawnReason.SessionClosed, replica.Despawned);
            Assert.Equal(0, c1.Registry.Count);
            Assert.False(c1.Registry.IsActive);
            Assert.Throws<InvalidOperationException>(() => c1.Registry.RequestOwnership(e.Id));
            c1.Registry.Update(net.Now + 1);
        }

        [Fact]
        public void Dispose_UnregistersAndDespawns()
        {
            var net = new SyncNet(withRpc: false);
            var obj = new TestEntity(null);
            net.Host.Registry.Spawn("a", Host, States.At(0), instance: obj);
            Assert.Equal(8, net.Host.Session.HandlerCount);
            net.Host.Registry.Dispose();
            net.Host.Registry.Dispose();
            Assert.Equal(0, net.Host.Session.HandlerCount);
            Assert.Equal(DespawnReason.Disposed, obj.Despawned);
            Assert.Throws<ObjectDisposedException>(() => net.Host.Registry.Spawn("a", Host, States.At(0)));
        }

        [Fact]
        public void Options_AreValidated()
        {
            var session = new SyncNet(withRegistry: false, withRpc: false).Host.Session;
            Assert.Throws<ArgumentOutOfRangeException>(() => new NetEntityRegistry(session, null, new NetEntityRegistryOptions { SendRate = 0 }));
            Assert.Throws<ArgumentOutOfRangeException>(() => new NetEntityRegistry(session, null, new NetEntityRegistryOptions { InterpolationDelay = -1 }));
            Assert.Throws<ArgumentOutOfRangeException>(() => new NetEntityRegistry(session, null, new NetEntityRegistryOptions { MaxExtrapolation = double.NaN }));
            Assert.Throws<ArgumentOutOfRangeException>(() => new NetEntityRegistry(session, null, new NetEntityRegistryOptions { SnapshotCapacity = 1 }));
            Assert.Throws<ArgumentOutOfRangeException>(() => new NetEntityRegistry(session, null, new NetEntityRegistryOptions { Encoding = (TransformEncoding)9 }));
            Assert.Throws<ArgumentNullException>(() => new NetEntityRegistry(null));
        }

        // ---- Malformed input -------------------------------------------------------------------------

        [Fact]
        public void MalformedEntityPackets_AreDroppedWithoutThrowing()
        {
            var (net, c1, c2) = ThreePeers();
            var e = net.Host.Registry.Spawn("a", c1.Id, States.At(1));
            net.Flush();
            var rng = new Random(17);
            byte[] types =
            {
                MessageType.Spawn, MessageType.Despawn, MessageType.State, MessageType.OwnershipChange,
                MessageType.StateRelayBatch, MessageType.OwnershipRequest, MessageType.OwnershipRelease, MessageType.OwnershipDenied
            };
            for (int i = 0; i < 3000; i++)
            {
                var bytes = new byte[1 + rng.Next(0, 40)];
                rng.NextBytes(bytes);
                bytes[0] = types[i % types.Length];
                SyncNet.SendRaw(c2, Host, bytes);
                SyncNet.SendRaw(c2, c1.Id, bytes);
                if (i % 50 == 0)
                    net.Frame();
            }
            net.Flush();

            Assert.Equal(c1.Id, e.Owner);
            Assert.True(e.IsSpawned);
            Assert.Equal(1, c1.Registry.Count);
            Assert.True(c1.Registry.Get(e.Id).IsLocallyOwned);
        }

        [Fact]
        public void TruncatedBatch_KeepsEntriesBeforeTheDamage()
        {
            var (net, c1, c2) = ThreePeers();
            var a = net.Host.Registry.Spawn("a", c1.Id, States.At(0));
            var b = net.Host.Registry.Spawn("b", c1.Id, States.At(0));
            net.Flush();

            var w = PacketBuilder.Begin(MessageType.State);
            w.WriteByte(2);
            NetEntityId.Write(w, a.Id);
            w.WriteByte(a.OwnerEpoch);
            TransformCodec.Write(w, States.At(5, time: net.Now + 0.5));
            NetEntityId.Write(w, b.Id);
            w.WriteByte(b.OwnerEpoch);
            var bytes = w.ToArray();
            SyncNet.SendRaw(c1, Host, bytes, Channel.State, Reliability.Unreliable);
            net.Flush();

            Assert.Equal(5f, a.LastState.Position.x);
            Assert.Equal(5f, c2.Registry.Get(a.Id).LastState.Position.x);
            Assert.Equal(0f, b.LastState.Position.x);
        }
    }
}
