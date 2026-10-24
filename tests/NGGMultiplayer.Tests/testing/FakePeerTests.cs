// End to end: a host NetSession on the harness's composite transport (its primary a loopback standing in for Steam),
// with in-process bots joining through the BotHub, as the harness runs them inside the game.
using System;
using System.Collections.Generic;
using System.Linq;
using NGGMultiplayer.Core;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Save;
using NGGMultiplayer.Core.Session;
using NGGMultiplayer.Core.Sync;
using NGGMultiplayer.Game;
using NGGMultiplayer.Game.Lifecycle;
using NGGMultiplayer.Game.Presence;
using NGGMultiplayer.Game.Progression;
using NGGMultiplayer.Testing;
using NGGMultiplayer.Tests.Sync;
using Xunit;

namespace NGGMultiplayer.Tests.Testing
{
    /// <summary>A host save that captures a Lifecycle session bundle around its fact table.</summary>
    internal sealed class BundleHostBackend : ISaveBackend
    {
        public FactTable Facts { get; } = new FactTable();

        public GameScene Scene { get; set; } = GameScene.Main;

        public bool Bare { get; set; }

        public int DiskWrites { get; private set; }

        public event Action<SaveDelta> LocalProgressChanged;

        public bool SuppressDiskWrites { get; set; }

        public byte[] CaptureSnapshot()
        {
            if (Bare)
                return Facts.ToArray();
            return new SessionSaveBundle
            {
                HostScene = Scene,
                SceneEpoch = 3,
                Mode = SessionSaveBundle.ModeStory,
                HasRun = true,
                RunJson = SessionSaveBundle.Encode("{\"run\":1}"),
                Facts = Facts.ToArray()
            }.Serialize();
        }

        public void ApplySnapshot(byte[] snapshot) => throw new InvalidOperationException("The host never loads a snapshot.");

        public void BackupLocalSave()
        {
        }

        public void RestoreLocalSave()
        {
        }

        public void ApplyDelta(SaveDelta delta) => Facts.Set(delta.Kind, delta.Key, delta.Value);

        public void Report(string kind, string key, byte[] value)
        {
            Facts.Set(kind, key, value);
            LocalProgressChanged?.Invoke(new SaveDelta(kind, key, value));
        }
    }

    internal sealed class HarnessHost : IDisposable
    {
        public const double Step = 0.02;
        public static readonly PeerId HostId = new PeerId(76561198000000001UL);
        public static readonly NetVector3 Anchor = new NetVector3(10f, 2f, -4f);

        public HarnessHost(Func<PeerId, SaveDelta, bool> claimValidator = null, bool bare = false)
        {
            Steam = new LoopbackHub();
            Primary = Steam.CreateTransport(HostId);
            Bots = new BotHub();
            Composite = Bots.Wrap(Primary);
            Save = new BundleHostBackend { Bare = bare };
            Net = new NetSession(Composite, Save, new TestFactory(), new NetSessionOptions { ClaimValidator = claimValidator });
            Spawner = new PlayerEntitySpawner(Net, GameEntityKeys.PerPlayer);
            Net.RegisterRpc(PresenceRpc.Shot, (s, r) => Shots.Add((s, ShotMessage.Read(r))), (s, t, p, a) => true);
            Net.RegisterRpc(PresenceRpc.Rest, (s, r) => Rests.Add((s, RestMessage.Read(r))), (s, t, p, a) => true);
            Net.RegisterRpc(PresenceRpc.Info, (s, r) => Infos.Add((s, InfoMessage.Read(r))), (s, t, p, a) => true);
            Net.RegisterRpc(ProgressionRpc.Claim, OnClaim, (s, t, p, a) => t == RpcTarget.Host);
            Net.RegisterRpc(ProgressionRpc.Bank, (s, r) => Banks.Add((s, r.ReadVarUInt(), r.ReadVarInt())), (s, t, p, a) => t == RpcTarget.Host);
            Net.RegisterRpc(ProgressionRpc.World, (s, r) => Worlds.Add((s, r.ReadByte(), r.ReadString(256), r.ReadBool())), (s, t, p, a) => true);
            Net.RegisterRpc(ProgressionRpc.StoryHost, (s, r) => StoryHost.Add(r.ReadString(256)), (s, t, p, a) => t == RpcTarget.Host);
            Net.RegisterRpc(ProgressionRpc.Grant, (s, r) => { });
            Net.StartHost();
            Bots.Message += (bot, text) => Messages.Add(bot.Name + ": " + text);
            Bots.Update(Now, true, Anchor, true);
        }

        public LoopbackHub Steam { get; }
        public LoopbackTransport Primary { get; }
        public BotHub Bots { get; }
        public CompositeTransport Composite { get; }
        public BundleHostBackend Save { get; }
        public NetSession Net { get; }
        public PlayerEntitySpawner Spawner { get; }
        public double Now { get; private set; }
        public bool Hosting { get; set; } = true;

        /// <summary>Replaces the bots' per-frame update (the harness's command layer drives them itself).</summary>
        public Action UpdateBots { get; set; }

        /// <summary>Whether the host grants the next pickup claim, and its balance.</summary>
        public bool Grant { get; set; } = true;
        public int Balance { get; set; } = 500;

        public List<(PeerId From, ShotMessage Shot)> Shots { get; } = new List<(PeerId, ShotMessage)>();
        public List<(PeerId From, RestMessage Msg)> Rests { get; } = new List<(PeerId, RestMessage)>();
        public List<(PeerId From, InfoMessage Info)> Infos { get; } = new List<(PeerId, InfoMessage)>();
        public List<(PeerId From, uint ClaimId, string Id, byte Type)> Claims { get; } = new List<(PeerId, uint, string, byte)>();
        public List<(PeerId From, uint ClaimId, int Change)> Banks { get; } = new List<(PeerId, uint, int)>();
        public List<(PeerId From, byte Op, string Id, bool Flag)> Worlds { get; } = new List<(PeerId, byte, string, bool)>();
        public List<string> StoryHost { get; } = new List<string>();
        public List<string> Messages { get; } = new List<string>();

        private void OnClaim(PeerId sender, NetReader r)
        {
            uint claimId = r.ReadVarUInt();
            string id = r.ReadString(ProgressionRpc.MaxInteractIdBytes);
            byte type = r.ReadByte();
            Claims.Add((sender, claimId, id, type));
            bool granted = Grant;
            Net.Rpc.Send(ProgressionRpc.Grant, RpcTarget.Peer, w => BotWire.WriteGrant(w, new BotWire.Grant(claimId, granted, Balance,
                granted ? string.Empty : "taken")), Reliability.Reliable, sender);
        }

        public FakePeer AddBot(string name = null) =>
            Bots.Add(HostId, new FakePeerOptions { DisplayName = name });

        public void Frame()
        {
            Now += Step;
            Net.Update(Now);
            if (UpdateBots != null)
                UpdateBots();
            else
                Bots.Update(Now, Hosting, Anchor, true);
        }

        public void Run(double seconds)
        {
            int frames = (int)Math.Round(seconds / Step);
            for (int i = 0; i < frames; i++)
                Frame();
        }

        public bool RunUntil(Func<bool> condition, double maxSeconds)
        {
            int frames = (int)Math.Round(maxSeconds / Step);
            for (int i = 0; i < frames; i++)
            {
                if (condition())
                    return true;
                Frame();
            }
            return condition();
        }

        public void Dispose()
        {
            Bots.Dispose();
            Spawner.Dispose();
            Net.Dispose();
        }
    }

    public class FakePeerTests
    {
        private static FakePeer ReadyBot(HarnessHost host, string name = null)
        {
            var bot = host.AddBot(name);
            Assert.True(host.RunUntil(() => bot.IsReady && host.Net.IsPeerReady(bot.Id), 5), string.Join("\n", host.Messages));
            return bot;
        }

        [Fact]
        public void Bot_JoinsSyncsInMemory_AndStreamsItsAvatar()
        {
            using (var host = new HarnessHost())
            {
                host.Save.Facts.Set(FactKinds.Bounty, "BO_Toilet1", FactCodec.EncodeFlags(FactCodec.BountyDone));
                var bot = ReadyBot(host, "Alice");
                Assert.True(BotIds.IsBot(bot.Id));
                Assert.Equal(FakePeerState.Ready, bot.State);
                Assert.True(bot.Save.HasBundle);
                Assert.Equal(GameScene.Main, bot.HostScene);
                Assert.Equal(3u, bot.SceneEpoch);
                Assert.Equal(1, bot.Save.Facts.Count);
                Assert.Equal(0, bot.Save.DiskWrites);
                Assert.Equal(1, bot.Save.Backups);
                Assert.Contains(host.Messages, m => m.Contains("save synced: bundle, host in Main"));
                Assert.Contains(host.Net.Session.Players, p => p.Id == bot.Id && p.DisplayName == "Alice");

                // The host spawned the bot's player and ball; the bot owns them and streams its walker's pose.
                host.Run(1.0);
                var entities = host.Net.Entities.Entities.Where(e => e.Owner == bot.Id).ToList();
                Assert.Equal(2, entities.Count);
                var player = entities.Single(e => e.PrefabKey == GameEntityKeys.Player);
                var view = (TestEntity)player.Instance;
                Assert.NotEmpty(view.Applied);
                var pose = view.LastApplied;
                Assert.NotEqual(0, pose.Flags & PoseFlags.Visible);
                Assert.Equal(HarnessHost.Anchor.y, pose.Position.y, 3);
                Assert.Contains(host.Infos, i => i.From == bot.Id);
            }
        }

        [Fact]
        public void BareFactTable_HostWithoutLifecycle_StillSyncs()
        {
            using (var host = new HarnessHost(bare: true))
            {
                host.Save.Facts.Set(FactKinds.RunInt, "m_x", FactTable.EncodeInt(4));
                var bot = ReadyBot(host);
                Assert.False(bot.Save.HasBundle);
                Assert.Equal(4, bot.Save.Facts.GetInt(FactKinds.RunInt, "m_x"));
            }
        }

        [Fact]
        public void Shot_ReachesTheHost_WithStrokeOneAndAPowerFraction()
        {
            using (var host = new HarnessHost())
            {
                var bot = ReadyBot(host);
                Assert.True(bot.Shoot(0.6f));
                Assert.True(host.RunUntil(() => host.Rests.Count > 0, 30));
                var shot = Assert.Single(host.Shots);
                Assert.Equal(bot.Id, shot.From);
                Assert.Equal(1u, shot.Shot.Stroke); // answers #51
                Assert.Equal(0.6f, shot.Shot.Power, 3); // answers #53: a fraction of the full swing
                Assert.Equal("Driver", shot.Shot.Club);
                var rest = Assert.Single(host.Rests);
                Assert.Equal(1u, rest.Msg.Stroke);
                Assert.True(rest.Msg.Distance >= 0 && rest.Msg.Distance < BotAvatar.NoHoleDistance); // answers #54
                Assert.Equal(1, bot.ShotsSent);
                Assert.Equal(1, bot.RestsSent);
            }
        }

        [Fact]
        public void FactClaims_AcceptedRejectedAndAlreadySet()
        {
            using (var host = new HarnessHost(claimValidator: (peer, delta) => delta.Key != "forbidden"))
            {
                var bot = ReadyBot(host);
                Assert.True(bot.ClaimBounty("BO_Toilet1", false, out string error), error);
                Assert.True(bot.ClaimRunBool("forbidden", true, out error), error);
                Assert.True(host.RunUntil(() => bot.Claims.Accepted == 1 && bot.Claims.Rejected == 1, 5), string.Join("\n", host.Messages));
                Assert.True(FactCodec.TryDecodeFlags(host.Save.Facts.Get(FactKinds.Bounty, "BO_Toilet1"), out byte flags));
                Assert.Equal(FactCodec.BountyDone, flags);
                Assert.Null(host.Save.Facts.Get(FactKinds.RunBool, "forbidden"));
                host.Run(0.1);
                Assert.Contains(host.Messages, m => m.Contains("claim bo/BO_Toilet1 = done: accepted"));
                Assert.Contains(host.Messages, m => m.Contains("rb/forbidden") && m.Contains("REJECTED"));

                // A rejected claim makes Core resync the bot (ResyncOnClaimRejected): a second bundle, still in memory only.
                Assert.True(host.RunUntil(() => bot.Save.SnapshotsApplied == 2 && bot.Net.SaveClient.IsSynced, 5));
                Assert.Equal(0, bot.Save.DiskWrites);
                Assert.Equal(GameScene.Main, bot.HostScene);

                // The same value again: Core sends nothing, the harness says so.
                Assert.True(bot.ClaimBounty("BO_Toilet1", false, out error), error);
                host.Run(0.1);
                Assert.Contains(host.Messages, m => m.Contains("already the session's value"));

                // The bounty's easy flag adds to the done flag.
                Assert.True(bot.ClaimBounty("BO_Toilet1", true, out error), error);
                Assert.True(host.RunUntil(() => bot.Claims.Accepted == 2, 5));
                Assert.True(FactCodec.TryDecodeFlags(host.Save.Facts.Get(FactKinds.Bounty, "BO_Toilet1"), out flags));
                Assert.Equal(FactCodec.BountyDone | FactCodec.BountyOnEasy, flags);
            }
        }

        [Fact]
        public void TrackClaim_AdvancesTheSessionsSet()
        {
            using (var host = new HarnessHost())
            {
                host.Save.Facts.Set(FactKinds.Track, FactKinds.TrackKey, FactCodec.EncodeTrack(new TrackValue("Main", 2)));
                var bot = ReadyBot(host);
                Assert.True(bot.ClaimTrack(null, null, out string error), error);
                Assert.True(host.RunUntil(() => bot.Claims.Accepted == 1, 5));
                Assert.True(FactCodec.TryDecodeTrack(host.Save.Facts.Get(FactKinds.Track, FactKinds.TrackKey), out var track));
                Assert.Equal(new TrackValue("Main", 3), track);
                Assert.False(new HarnessHostless().TrackWithoutSession(out error));
            }
        }

        private sealed class HarnessHostless
        {
            public bool TrackWithoutSession(out string error)
            {
                var bot = new FakePeer(1, new LoopbackHub().CreateTransport(BotIds.Make(1)));
                return bot.ClaimTrack("Main", 1, out error);
            }
        }

        [Fact]
        public void Pickup_GrantedLikeARealClient_ReportsMoneyAndHidesTheObject()
        {
            using (var host = new HarnessHost())
            {
                var bot = ReadyBot(host);
                byte ticket = (byte)Array.IndexOf(BotInteractions.Names, "LMUGCTicket");
                uint claimId = bot.SendPickup(new PickupRequest
                {
                    InteractId = "Ticket_1", Interaction = ticket, CashValue = 300, SaveableId = "SO_Ticket_1"
                });
                Assert.NotEqual(0u, claimId);
                Assert.True(host.RunUntil(() => host.Banks.Count == 1 && bot.Claims.Accepted == 1, 5), string.Join("\n", host.Messages));
                var claim = Assert.Single(host.Claims);
                Assert.Equal(("Ticket_1", ticket, claimId), (claim.Id, claim.Type, claim.ClaimId));
                // answers #35/#137/#138: one -m_cashValue under the granted claim.
                Assert.Equal((bot.Id, claimId, -300), Assert.Single(host.Banks));
                Assert.True(FactCodec.TryDecodeObj(host.Save.Facts.Get(FactKinds.Obj, "SO_Ticket_1"), out var obj));
                Assert.False(obj.Active);
                Assert.Equal(claimId, bot.LastGrantedClaim);

                host.Grant = false;
                byte cash = (byte)Array.IndexOf(BotInteractions.Names, "CollectCash");
                bot.SendPickup(new PickupRequest { InteractId = "Cash_1", Interaction = cash, CashValue = 50 });
                Assert.True(host.RunUntil(() => bot.GrantsReceived == 2, 5));
                host.Run(0.1);
                Assert.Single(host.Banks);
                Assert.Contains(host.Messages, m => m.Contains("REFUSED: taken"));
            }
        }

        [Fact]
        public void WorldAndStoryRpcs_UseTheProgressionPayloads()
        {
            using (var host = new HarnessHost())
            {
                var bot = ReadyBot(host);
                Assert.True(bot.SendWorld(WorldOp.OpenDoor, "Door_3", true));
                Assert.True(bot.SendStory("LukeLeaveSequence", true));
                Assert.True(host.RunUntil(() => host.Worlds.Count == 1 && host.StoryHost.Count == 1, 5));
                Assert.Equal((bot.Id, (byte)WorldOp.OpenDoor, "Door_3", true), host.Worlds[0]);
                Assert.Equal("LukeLeaveSequence", host.StoryHost[0]);
            }
        }

        [Fact]
        public void GracefulLeave_EndsAtOnce_DropTimesOut()
        {
            using (var host = new HarnessHost())
            {
                var a = ReadyBot(host, "A");
                var b = ReadyBot(host, "B");
                Assert.Equal(3, host.Net.Session.Players.Count);

                host.Bots.Remove(a, graceful: true);
                Assert.True(host.RunUntil(() => host.Net.Session.Players.Count == 2, 1));

                host.Bots.Remove(b, graceful: false);
                host.Run(1);
                Assert.Equal(2, host.Net.Session.Players.Count); // a dropped link is only noticed by the timeout
                Assert.True(host.RunUntil(() => host.Net.Session.Players.Count == 1, 15));
                Assert.Empty(host.Bots.Bots);
            }
        }

        [Fact]
        public void NotHosting_BotsCannotJoin_AndAreCutOff()
        {
            using (var host = new HarnessHost())
            {
                var bot = ReadyBot(host);
                host.Hosting = false;
                host.Run(0.1);
                Assert.False(host.Composite.LoopbackEnabled);
                Assert.Throws<InvalidOperationException>(() => host.AddBot());
                Assert.True(host.RunUntil(() => host.Bots.Bots.Count == 0, 30));
                Assert.True(host.Composite.LoopbackDropped > 0);
            }
        }

        [Fact]
        public void BotsJoinOnlyTheLocalHost_AndAtMostMaxBots()
        {
            using (var host = new HarnessHost())
            {
                Assert.Throws<InvalidOperationException>(() => host.Bots.Add(new PeerId(42), null));
                var unwired = new BotHub();
                Assert.Throws<InvalidOperationException>(() => unwired.Add(HarnessHost.HostId, null));
                var bot = host.AddBot();
                Assert.Equal("Bot 1", bot.Name);
                Assert.Same(bot, host.Bots.Find("1"));
                Assert.Same(bot, host.Bots.Find("bot 1"));
                Assert.Null(host.Bots.Find("2"));
                Assert.Same(bot, host.Bots.Newest);
                Assert.Same(host.Composite, host.Bots.Wrap(host.Primary));
            }
        }
    }
}
