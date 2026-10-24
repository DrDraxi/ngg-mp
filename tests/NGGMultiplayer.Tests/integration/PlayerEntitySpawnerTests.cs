using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NGGMultiplayer.Core;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Sync;
using NGGMultiplayer.Tests.Sync;
using Xunit;

namespace NGGMultiplayer.Tests.Integration
{
    public class PlayerEntitySpawnerTests
    {
        private static readonly string[] Keys = { "ngg.player", "ngg.ball" };

        private static PlayerEntitySpawner Attach(NetPeer peer, List<string> log = null)
        {
            var spawner = new PlayerEntitySpawner(peer.Net, Keys);
            if (log != null)
            {
                spawner.PlayerEntitySpawned += (p, e) => log.Add($"+{p.Value}:{e.PrefabKey}");
                spawner.PlayerEntityDespawned += (p, e, r) => log.Add($"-{p.Value}:{e.PrefabKey}:{r}");
            }
            return spawner;
        }

        [Fact]
        public void SpawnData_HasVersionedLittleEndianLayout()
        {
            var data = PlayerEntitySpawner.WriteSpawnData(new PeerId(76561198000000123UL));
            Assert.Equal(9, data.Length);
            Assert.Equal(1, data[0]);
            Assert.Equal(76561198000000123UL, BitConverter.ToUInt64(data, 1));
        }

        [Fact]
        public void Host_SpawnsOwnEntities_OnStart()
        {
            var net = new NetTestNet();
            var host = net.Add(100);
            var spawner = Attach(host);

            host.Net.StartHost();

            Assert.Equal(2, spawner.Count);
            foreach (var key in Keys)
            {
                Assert.True(spawner.TryGetEntity(host.Id, key, out var e));
                Assert.Equal(host.Id, e.Owner);
                Assert.Equal(PlayerEntitySpawner.EntityFlags, e.Flags);
                Assert.True(PlayerEntitySpawner.TryReadPlayer(e, out var player));
                Assert.Equal(host.Id, player);
            }
        }

        [Fact]
        public void Client_GetsOwnAndHostEntities_AfterSaveSync_AndTheyDespawnWhenItLeaves()
        {
            var net = new NetTestNet();
            var host = net.Add(100);
            var c1 = net.Add(201, clockOffset: 50);
            var c2 = net.Add(202, clockOffset: -3);
            var hostLog = new List<string>();
            var c1Log = new List<string>();
            var hostSpawner = Attach(host, hostLog);
            var c1Spawner = Attach(c1, c1Log);
            var c2Spawner = Attach(c2);

            host.Net.StartHost();
            c1.Net.Connect(host.Id);
            c2.Net.Connect(host.Id);
            Assert.True(net.RunUntil(() => hostSpawner.Count == 6 && c1Spawner.Count == 6 && c2Spawner.Count == 6, 10));
            Assert.Equal(0, c1.Factory.CreatedBeforeSync);
            Assert.Equal(0, c2.Factory.CreatedBeforeSync);

            Assert.True(c1Spawner.TryGetEntity(c1.Id, "ngg.player", out var own));
            Assert.True(own.IsLocallyOwned);
            Assert.True(c1Spawner.TryGetEntity(c2.Id, "ngg.ball", out var other));
            Assert.False(other.IsLocallyOwned);
            Assert.Equal(c2.Id, other.Owner);

            // Client 1 moves its avatar; client 2 sees it through the host relay.
            c1.BeforeUpdate = p =>
            {
                if (p.Net.IsActive && c1Spawner.TryGetEntity(c1.Id, "ngg.player", out var e))
                    ((TestEntity)e.Instance).Current = new TransformState(new NetVector3(5, 0, 0), NetQuaternion.Identity);
            };
            Assert.True(net.RunUntil(() =>
                c2Spawner.TryGetEntity(c1.Id, "ngg.player", out var seen)
                && ((TestEntity)seen.Instance).Applied.Count > 0
                && Math.Abs(((TestEntity)seen.Instance).LastApplied.Position.x - 5) < 0.01f, 5));

            c1.Net.Leave();
            Assert.True(net.RunUntil(() => hostSpawner.Count == 4 && c2Spawner.Count == 4, 5));
            Assert.False(c2Spawner.TryGetEntity(c1.Id, "ngg.player", out _));
            Assert.Contains($"-{c1.Id.Value}:ngg.player:{DespawnReason.OwnerLeft}", hostLog);
            Assert.Equal(0, c1Spawner.Count);
            Assert.Contains($"-{c1.Id.Value}:ngg.ball:{DespawnReason.SessionClosed}", c1Log);
        }

        [Fact]
        public void NextSession_StartsClean()
        {
            var net = new NetTestNet();
            var host = net.Add(100);
            var client = net.Add(201);
            var hostSpawner = Attach(host);
            var clientSpawner = Attach(client);

            host.Net.StartHost();
            client.Net.Connect(host.Id);
            Assert.True(net.RunUntil(() => clientSpawner.Count == 4, 10));

            host.Net.Leave();
            Assert.True(net.RunUntil(() => !client.Net.IsActive, 5));
            Assert.Equal(0, hostSpawner.Count);
            Assert.Equal(0, clientSpawner.Count);

            host.Net.StartHost();
            client.Net.Connect(host.Id);
            Assert.True(net.RunUntil(() => clientSpawner.Count == 4 && hostSpawner.Count == 4, 10));
        }

        [Fact]
        public void InitialState_IsUsed_AndInvalidOrThrowingProvidersAreSurvived()
        {
            var net = new NetTestNet();
            var host = net.Add(100);
            var spawner = Attach(host);
            spawner.InitialState = (player, key) =>
            {
                if (key == "ngg.ball")
                    throw new InvalidOperationException("no ball yet");
                return new TransformState(new NetVector3(1, 2, 3), NetQuaternion.Identity);
            };

            host.Net.StartHost();

            Assert.True(spawner.TryGetEntity(host.Id, "ngg.player", out var player));
            Assert.Equal(2f, player.LastState.Position.y);
            Assert.False(spawner.TryGetEntity(host.Id, "ngg.ball", out _));

            host.Net.Leave();
            spawner.InitialState = (p, k) => new TransformState(new NetVector3(float.NaN, 0, 0), NetQuaternion.Identity);
            host.Net.StartHost();
            Assert.True(spawner.TryGetEntity(host.Id, "ngg.ball", out var ball));
            Assert.True(ball.LastState.IsValid);
        }

        [Fact]
        public void ForeignEntities_AreIgnored()
        {
            var net = new NetTestNet();
            var host = net.Add(100);
            var spawner = Attach(host);
            host.Net.StartHost();

            host.Net.Entities.Spawn("ngg.player", host.Id, TransformState.Identity);
            host.Net.Entities.Spawn("other", host.Id, TransformState.Identity, spawnData: PlayerEntitySpawner.WriteSpawnData(host.Id));

            Assert.Equal(2, spawner.Count);
            Assert.False(PlayerEntitySpawner.TryReadPlayer(null, out _));
        }

        [Fact]
        public void Constructor_RejectsBadKeys()
        {
            var net = new NetTestNet();
            var host = net.Add(100);
            Assert.Throws<ArgumentException>(() => new PlayerEntitySpawner(host.Net, Array.Empty<string>()));
            Assert.Throws<ArgumentException>(() => new PlayerEntitySpawner(host.Net, new[] { "a", "a" }));
            Assert.Throws<ArgumentException>(() => new PlayerEntitySpawner(host.Net, new[] { "a", "" }));
        }

        [Fact]
        public void Dispose_StopsSpawning()
        {
            var net = new NetTestNet();
            var host = net.Add(100);
            var spawner = Attach(host);
            spawner.Dispose();
            host.Net.StartHost();
            Assert.Equal(0, spawner.Count);
            Assert.Equal(0, host.Net.Entities.Count);
        }

        [Fact]
        public void ModVersion_MatchesAssemblyVersion()
        {
            var version = typeof(ProtocolInfo).Assembly.GetName().Version;
            Assert.Equal(ProtocolInfo.ModVersion, version.ToString(3));
            var informational = typeof(ProtocolInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            Assert.StartsWith(ProtocolInfo.ModVersion, informational.InformationalVersion);
        }
    }
}
