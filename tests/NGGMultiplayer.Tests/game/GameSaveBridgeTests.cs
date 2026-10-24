using System;
using System.Collections.Generic;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Save;
using NGGMultiplayer.Game;
using NGGMultiplayer.Tests.Save;
using Xunit;

namespace NGGMultiplayer.Tests.Game
{
    /// <summary>The bridge driven by Core's real save sync over the loopback harness.</summary>
    public class GameSaveBridgeTests
    {
        private static readonly PeerId ClientId = new PeerId(200);

        private sealed class FakeFacts : IFactHandler
        {
            private readonly GameSaveBridge _bridge;

            public FakeFacts(GameSaveBridge bridge)
            {
                _bridge = bridge;
            }

            public readonly List<SaveDelta> Applied = new List<SaveDelta>();
            public readonly FactTable Built = new FactTable();
            public int Replaced;

            public void BuildFacts(FactTable into) => into.ReplaceWith(Built);

            // Money is host-derived: clients may never claim it.
            public bool ValidateClaim(PeerId from, SaveDelta claim) => claim.Kind != "bank";

            public void ApplyFact(SaveDelta fact)
            {
                Applied.Add(fact.Clone());
                // A completed bounty pays out on the host, as the game would.
                if (fact.Kind == "bo" && _bridge.Table.GetInt("bank", "", 0) == 100)
                    _bridge.ReportLocalFact("bank", "", FactTable.EncodeInt(600));
            }

            public void OnFactsReplaced(FactTable facts) => Replaced++;
        }

        private sealed class FakeStore : ISessionSaveStore
        {
            public int Captured, Applied, Backups, Restores;
            public readonly List<bool> Suppress = new List<bool>();

            public byte[] CaptureBundle(FactTable facts)
            {
                Captured++;
                var w = new NetWriter();
                w.WriteString("host-run-json");
                facts.Serialize(w);
                return w.ToArray();
            }

            public FactTable ApplyBundle(byte[] bundle)
            {
                Applied++;
                var r = new NetReader(bundle);
                Assert.Equal("host-run-json", r.ReadString());
                return FactTable.Deserialize(r);
            }

            public void BackupLocalSave() => Backups++;

            public void RestoreLocalSave() => Restores++;

            public void OnSuppressDiskWritesChanged(bool suppress) => Suppress.Add(suppress);
        }

        private sealed class Rig
        {
            public readonly SaveNet Net = new SaveNet(createHost: false);
            public readonly GameSaveBridge HostBridge = new GameSaveBridge(() => true, () => false);
            public readonly GameSaveBridge ClientBridge = new GameSaveBridge(() => false, () => true);
            public readonly FakeFacts HostFacts;
            public readonly FakeFacts ClientFacts;
            public readonly SaveSyncHost Host;
            public readonly FakeSession ClientSession;
            public readonly SaveSyncClient Client;

            public Rig(bool withStores = false)
            {
                HostFacts = new FakeFacts(HostBridge);
                ClientFacts = new FakeFacts(ClientBridge);
                HostBridge.SetFactHandler(HostFacts);
                ClientBridge.SetFactHandler(ClientFacts);
                if (withStores)
                {
                    HostBridge.SetStore(new FakeStore());
                    ClientBridge.SetStore(new FakeStore());
                }
                Host = new SaveSyncHost(Net.HostSession, HostBridge, HostBridge.ValidateClaim);
                ClientSession = new FakeSession(Net.Hub.CreateTransport(ClientId), SaveNet.HostId);
                Client = new SaveSyncClient(ClientSession, ClientBridge);
            }

            public void Join()
            {
                Net.HostSession.AddRemote(ClientId);
                Net.HostSession.RaiseJoined(ClientId);
                Settle();
            }

            /// <summary>Lets time pass (the host paces resync snapshots), then settles.</summary>
            public void Run(double seconds)
            {
                for (double t = 0; t < seconds; t += 0.05)
                {
                    Net.Hub.Advance(0.05);
                    Settle();
                }
            }

            public void Settle()
            {
                for (int i = 0; i < 10000; i++)
                {
                    double now = Net.Hub.Now;
                    Host.Tick(now);
                    Client.Tick(now);
                    int delivered = Net.HostSession.Pump() + ClientSession.Pump();
                    if (delivered == 0 && Net.HostSession.Transport.PendingCount == 0 && ClientSession.Transport.PendingCount == 0)
                        return;
                    Net.Hub.Advance(0.01);
                }
                throw new TimeoutException("Network did not settle.");
            }
        }

        [Fact]
        public void Join_IsRefused_WithoutAStore_WhenOneIsRequired()
        {
            var rig = new Rig();
            rig.ClientBridge.RequireStoreToJoin = true;
            rig.HostBridge.ReportLocalFact("bank", "", FactTable.EncodeInt(100));
            Assert.Throws<InvalidOperationException>(() => rig.ClientBridge.BackupLocalSave());
            rig.Join();

            // No backup, so the host's save never reached the client's game.
            Assert.False(rig.Client.IsSynced);
            Assert.False(rig.ClientBridge.SuppressDiskWrites);
            Assert.Equal(0, rig.ClientFacts.Replaced);

            var withStore = new Rig(withStores: true);
            withStore.ClientBridge.RequireStoreToJoin = true;
            withStore.Join();
            Assert.True(withStore.Client.IsSynced);
        }

        [Fact]
        public void Snapshot_CarriesTheHostTable()
        {
            var rig = new Rig();
            rig.HostBridge.ReportLocalFact("bo", "BO_Glass", new byte[] { 1 });
            rig.HostBridge.ReportLocalFact("bank", "", FactTable.EncodeInt(100));
            rig.ClientBridge.Table.Set("bo", "BO_OwnSave", new byte[] { 1 });
            rig.Join();

            Assert.True(rig.Client.IsSynced);
            Assert.Equal(rig.HostBridge.Table.ComputeCrc(), rig.ClientBridge.Table.ComputeCrc());
            Assert.False(rig.ClientBridge.Table.Contains("bo", "BO_OwnSave"));
            Assert.Equal(1, rig.ClientFacts.Replaced);
            Assert.True(rig.ClientBridge.SuppressDiskWrites);
        }

        [Fact]
        public void HostReport_ReachesTheClient_AndUnchangedReportsAreNotResent()
        {
            var rig = new Rig();
            rig.Join();
            rig.HostBridge.ReportLocalFact("rb", "m_driverUnlocked", FactTable.EncodeBool(true));
            rig.Settle();
            rig.HostBridge.ReportLocalFact("rb", "m_driverUnlocked", FactTable.EncodeBool(true));
            rig.Settle();

            Assert.True(rig.ClientBridge.Table.GetBool("rb", "m_driverUnlocked"));
            Assert.Single(rig.ClientFacts.Applied);
            Assert.Equal(1, rig.Host.DeltasBroadcast);
        }

        [Fact]
        public void ClientClaim_IsConfirmedByTheHost_WithItsSideEffects()
        {
            var rig = new Rig();
            rig.HostBridge.ReportLocalFact("bank", "", FactTable.EncodeInt(100));
            rig.Join();

            rig.ClientBridge.ReportLocalFact("bo", "BO_Glass", new byte[] { 1 });
            // Unconfirmed: the client's table only holds what the host said.
            Assert.False(rig.ClientBridge.Table.Contains("bo", "BO_Glass"));
            rig.Settle();

            Assert.True(rig.HostBridge.Table.Contains("bo", "BO_Glass"));
            Assert.True(rig.ClientBridge.Table.Contains("bo", "BO_Glass"));
            Assert.Equal(600, rig.HostBridge.Table.GetInt("bank", ""));
            Assert.Equal(600, rig.ClientBridge.Table.GetInt("bank", ""));
            Assert.Contains(rig.HostFacts.Applied, d => d.Kind == "bo");
            Assert.Contains(rig.ClientFacts.Applied, d => d.Kind == "bank");
        }

        [Fact]
        public void RejectedClaim_LeavesBothTablesOnTheHostsTruth()
        {
            var rig = new Rig();
            rig.HostBridge.ReportLocalFact("bank", "", FactTable.EncodeInt(100));
            rig.Join();

            rig.ClientBridge.ReportLocalFact("bank", "", FactTable.EncodeInt(999999));
            rig.Run(2);

            Assert.Equal(100, rig.HostBridge.Table.GetInt("bank", ""));
            Assert.Equal(100, rig.ClientBridge.Table.GetInt("bank", ""));
            Assert.True(rig.Client.IsSynced);
            Assert.Equal(2, rig.ClientFacts.Replaced);
        }

        [Fact]
        public void SessionEnd_RestoresTheClientsOwnTable()
        {
            var rig = new Rig();
            rig.ClientBridge.Table.Set("bo", "BO_OwnSave", new byte[] { 1 });
            rig.HostBridge.ReportLocalFact("bo", "BO_HostSave", new byte[] { 1 });
            rig.Join();
            Assert.False(rig.ClientBridge.Table.Contains("bo", "BO_OwnSave"));

            rig.ClientSession.RaiseClosed();

            Assert.True(rig.ClientBridge.Table.Contains("bo", "BO_OwnSave"));
            Assert.False(rig.ClientBridge.Table.Contains("bo", "BO_HostSave"));
            Assert.False(rig.ClientBridge.SuppressDiskWrites);
        }

        [Fact]
        public void Store_WrapsTheTable_AndHearsEveryStep()
        {
            var rig = new Rig(withStores: true);
            rig.HostBridge.ReportLocalFact("seed", "", FactTable.EncodeInt(77));
            rig.Join();
            rig.ClientSession.RaiseClosed();

            var hostStore = (FakeStore)rig.HostBridge.Store;
            var clientStore = (FakeStore)rig.ClientBridge.Store;
            Assert.Equal(1, hostStore.Captured);
            Assert.Equal(1, clientStore.Applied);
            Assert.Equal(1, clientStore.Backups);
            Assert.Equal(1, clientStore.Restores);
            Assert.Equal(new[] { true, false }, clientStore.Suppress);
        }

        [Fact]
        public void RebuildFacts_ReplacesTheHostTable()
        {
            var rig = new Rig();
            rig.HostBridge.ReportLocalFact("bo", "BO_Stale", new byte[] { 1 });
            rig.HostFacts.Built.Set("bo", "BO_Fresh", new byte[] { 1 });
            rig.HostBridge.RebuildFacts();
            Assert.False(rig.HostBridge.Table.Contains("bo", "BO_Stale"));
            Assert.True(rig.HostBridge.Table.Contains("bo", "BO_Fresh"));
        }

        [Fact]
        public void OfflineReports_OnlyUpdateTheTable()
        {
            var bridge = new GameSaveBridge(() => false, () => false);
            int raised = 0;
            bridge.LocalProgressChanged += _ => raised++;
            bridge.ReportLocalFact("bo", "BO_1", new byte[] { 1 });
            Assert.True(bridge.Table.Contains("bo", "BO_1"));
            Assert.Equal(0, raised);
        }
    }
}
