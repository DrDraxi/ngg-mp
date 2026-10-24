// The command layer over a fake game: what each command answers, the dump's JSON, savecheck baselines.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Session;
using NGGMultiplayer.Game;
using NGGMultiplayer.Game.Lifecycle;
using NGGMultiplayer.Game.Progression;
using NGGMultiplayer.Testing;
using Xunit;

namespace NGGMultiplayer.Tests.Testing
{
    internal sealed class FakeHarnessGame : IHarnessGame
    {
        private readonly HarnessHost _host;

        public FakeHarnessGame(HarnessHost host, string saveDirectory)
        {
            _host = host;
            SaveDirectory = saveDirectory;
        }

        public bool Hosting { get; set; } = true;
        public bool Session { get; set; } = true;
        public string Rebinds { get; set; } = "{}";
        public bool ThrowInWorld { get; set; }
        public int HostCalls { get; private set; }
        public int LeaveCalls { get; private set; }

        public string ServiceState => Hosting ? "Hosting" : Session ? "Client" : "Idle";
        public bool InSession => Session;
        public bool IsHosting => Hosting && Session;
        public PeerId LocalId => HarnessHost.HostId;
        public PeerId HostId => HarnessHost.HostId;
        public ulong LobbyId => 1234;
        public string StatusMessage => "ok";
        public IReadOnlyList<PlayerInfo> Players => _host.Net.Session.Players;
        public bool IsPeerReady(PeerId peer) => _host.Net.IsPeerReady(peer);

        public bool Host(out string message)
        {
            HostCalls++;
            message = "lobby requested";
            return true;
        }

        public void Leave() => LeaveCalls++;

        public FakePeerOptions BotTemplate() => new FakePeerOptions();

        public bool TryGetHostPose(out NetVector3 feet)
        {
            feet = HarnessHost.Anchor;
            return true;
        }

        public Func<float, float, float, float> GroundHeight => (x, z, fallback) => fallback;

        public FactTable SessionFacts => _host.Save.Facts;

        public void DescribeWorld(JsonBuilder json)
        {
            json.Value("scene", "Main");
            if (ThrowInWorld)
            {
                json.BeginObject("half");
                json.BeginArray("written");
                throw new InvalidOperationException("boom");
            }
        }

        public void DescribeSaveGuard(JsonBuilder json) => json.Value("writesBlocked", false);

        public string DescribeWind() => "current (1, 0)";

        public string SaveDirectory { get; }

        public string ReadPlayerPref(string key) => key == HarnessCommands.RebindsPref ? Rebinds : null;

        public bool TryResolvePickup(string interactId, out PickupRequest request, out string error)
        {
            request = null;
            error = null;
            if (interactId != "Cash_7")
            {
                error = "not found";
                return false;
            }
            request = new PickupRequest { InteractId = interactId, Interaction = 1, CashValue = 75, SaveableId = "SO_Cash_7" };
            return true;
        }

        public bool TryGetChallengeSet(string name, out bool isBounty, out int challengeCount)
        {
            isBounty = name.StartsWith("BO_", StringComparison.Ordinal);
            challengeCount = 3;
            return name == "BO_Toilet1" || name == "Main";
        }

        public IReadOnlyList<string> List(string what, out string error)
        {
            error = what == "sets" ? null : "unknown";
            return what == "sets" ? new[] { "Main", "BO_Toilet1" } : null;
        }

        public string RunCheck(string name) => "check " + name + ": ok";

        public IReadOnlyList<string> CheckNames => new[] { "interaction" };
    }

    public class HarnessCommandsTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "ngg_cmds_" + Guid.NewGuid().ToString("N"));
        private readonly HarnessHost _host = new HarnessHost();
        private readonly FakeHarnessGame _game;
        private readonly HarnessCommands _commands;
        private readonly List<string> _output = new List<string>();

        public HarnessCommandsTests()
        {
            Directory.CreateDirectory(_dir);
            _game = new FakeHarnessGame(_host, _dir);
            _commands = new HarnessCommands(_host.Bots, _game, () => _host.Now, () => new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc));
            _commands.Output += _output.Add;
            _host.UpdateBots = _commands.Tick;
            _commands.Start();
        }

        public void Dispose()
        {
            _host.Dispose();
            try
            {
                Directory.Delete(_dir, true);
            }
            catch (IOException)
            {
            }
        }

        private string Run(string line) => _commands.Execute(HarnessCommand.Parse(line));

        private FakePeer AddReadyBot(string name = null)
        {
            string reply = Run("bot add" + (name != null ? " " + name : string.Empty));
            Assert.Contains("is joining as", reply);
            var bot = _host.Bots.Newest;
            Assert.True(_host.RunUntil(() => bot.IsReady, 5), string.Join("\n", _output));
            _host.Frame(); // the host's pose reaches the avatar on the next update
            Assert.True(bot.Avatar.HasAnchor);
            return bot;
        }

        [Fact]
        public void UnknownCommand_AndHelp()
        {
            Assert.StartsWith("unknown command 'frobnicate'", Run("frobnicate"));
            Assert.Contains("bot claim bounty", Run("help"));
            Assert.Equal(string.Empty, _commands.Execute(null));
        }

        [Fact]
        public void HostLeaveStatus()
        {
            Assert.StartsWith("already hosting (lobby 1234)", Run("host"));
            _game.Hosting = false;
            Assert.Equal("hosting requested: lobby requested", Run("host"));
            Assert.Equal(1, _game.HostCalls);
            Assert.StartsWith("bots join a hosted session only", Run("bot add"));
            Assert.Equal("left the session", Run("leave"));
            Assert.Equal(1, _game.LeaveCalls);
            Assert.Contains("bots: none", Run("status"));
        }

        [Fact]
        public void BotLifecycle_ThroughCommands()
        {
            var bot = AddReadyBot("Alice");
            Assert.Equal("Alice", bot.Name);
            string status = Run("status");
            Assert.Contains("bot1 (Alice) Ready, host in Main", status);
            Assert.Contains("2 player(s)", status);

            Assert.Equal("bot1 (Alice) walking", Run("bot walk on"));
            Assert.True(bot.Avatar.Walking);
            Assert.StartsWith("usage: bot walk", Run("bot walk sideways"));
            Assert.Equal("no bot 'Bob'", Run("bot walk off @Bob"));
            Assert.Contains("takes its stance (power 1.00)", Run("bot shoot 3 @1"));
            Assert.Contains("cannot shoot now", Run("bot shoot"));
            Assert.True(_host.RunUntil(() => _host.Rests.Count == 1, 30));
            Assert.Equal(1u, _host.Shots[0].Shot.Stroke);

            Assert.Contains("vanished without a goodbye", Run("bot remove"));
            Assert.Equal("no bots", Run("bot remove"));
            Assert.Equal("no bots: 'bot add' first", Run("bot walk on"));
        }

        [Fact]
        public void Claims_ThroughCommands_ReportTheirVerdicts()
        {
            var bot = AddReadyBot();
            Assert.Contains("claims bounty BO_Toilet1; the verdict follows", Run("bot claim bounty BO_Toilet1"));
            Assert.Contains("no such clean set name", Run("bot claim bounty BO_Nope"));
            Assert.Contains("the session has no main track yet", Run("bot claim challenge"));
            Assert.Contains("-> Main #2", Run("bot claim challenge Main 2"));
            Assert.Contains("a bounty set: the host must refuse it as the main track", Run("bot claim challenge BO_Toilet1 1"));
            Assert.Contains("claims rb/m_hitLuke = True", Run("bot claim rb m_hitLuke true"));
            Assert.Contains("not a shared field", Run("bot claim rb m_notAField true"));
            Assert.Contains("claims obj/SO_1 inactive", Run("bot claim obj SO_1 inactive"));
            Assert.Contains("sends world op OpenDoor Door_1", Run("bot claim door Door_1"));
            Assert.Contains("sends world op SetGates Gate_1 open", Run("bot claim gate Gate_1 open"));
            Assert.Contains("sends world op SlidingDoor S_1 close", Run("bot claim sliding S_1 close"));
            Assert.StartsWith("usage: bot claim world", Run("bot claim world 99"));
            Assert.Contains("claims Cash_7 as claim 1 (host world: CollectCash, m_cashValue 75, on SO_Cash_7)", Run("bot claim pickup Cash_7"));
            Assert.Contains("cannot resolve pickup Nope", Run("bot claim pickup Nope"));
            Assert.Contains("not an arbitrated type", Run("bot claim pickup X Teleport 0"));
            Assert.Contains("not host-executed", Run("bot claim storyhost NotAMethod"));
            Assert.Contains("claims seed/ = 1; the verdict follows", Run("bot claim fact seed - 0x01000000"));
            Assert.Equal("the value must be hex (for example 01)", Run("bot claim fact rb x zz"));
            Assert.StartsWith("unknown claim kind", Run("bot claim nonsense"));

            Assert.True(_host.RunUntil(() => _output.Any(o => o.Contains("bo/BO_Toilet1 = done: accepted")), 5), string.Join("\n", _output));
            Assert.True(_host.RunUntil(() => _output.Any(o => o.Contains("GRANTED")), 5), string.Join("\n", _output));
            Assert.True(_host.RunUntil(() => _host.Banks.Count == 1, 5));
            Assert.Equal(75, _host.Banks[0].Change); // CollectCash reports +m_cashValue
            Assert.Contains("reports a money change of -5 under claim 1", Run("bot claim bank -5 1"));
            Assert.True(_host.RunUntil(() => _host.Banks.Count == 2, 5));
            Assert.Equal((bot.Id, 1u, -5), _host.Banks[1]);
            Assert.Equal(3, _host.Worlds.Count(w => w.From == bot.Id));
        }

        [Fact]
        public void Dump_IsOneJsonDocument_EvenWhenAWriterFails()
        {
            AddReadyBot();
            _host.Save.Facts.Set(FactKinds.Bounty, "BO_Toilet1", FactCodec.EncodeFlags(FactCodec.BountyDone));
            string dump = Run("dump");
            using (var doc = System.Text.Json.JsonDocument.Parse(dump))
            {
                var root = doc.RootElement;
                Assert.Equal("host", root.GetProperty("session").GetProperty("role").GetString());
                Assert.Equal(2, root.GetProperty("session").GetProperty("players").GetArrayLength());
                Assert.Equal("Main", root.GetProperty("world").GetProperty("scene").GetString());
                Assert.Equal(1, root.GetProperty("facts").GetProperty("count").GetInt32());
                Assert.False(root.GetProperty("saveGuard").GetProperty("writesBlocked").GetBoolean());
                Assert.False(root.GetProperty("saves").GetProperty("violation").GetBoolean());
                Assert.True(root.GetProperty("harness").GetProperty("wired").GetBoolean());
                Assert.Equal(1, root.GetProperty("bots").GetArrayLength());
            }

            _game.ThrowInWorld = true;
            dump = Run("dump");
            using (var doc = System.Text.Json.JsonDocument.Parse(dump))
                Assert.Contains("boom", doc.RootElement.GetProperty("world").GetProperty("dumpError").GetString());
        }

        [Fact]
        public void SaveCheck_ClientRoleFlagsProtectedChanges_SettingsAndRebindsAreAllowed()
        {
            string protectedName = LocalSaveFiles.ProtectedNames[0];
            File.WriteAllText(Path.Combine(_dir, protectedName), "save");
            File.WriteAllText(Path.Combine(_dir, HarnessCommands.SettingsFile), "settings");

            // Join as a client: the baseline taken at session start forbids protected changes.
            _game.Session = false;
            _game.Hosting = false;
            _host.Frame();
            _game.Session = true;
            _host.Frame();
            Assert.Contains(_output, o => o == "savecheck baseline taken at session start (client)");
            Assert.Contains("nothing changed", Run("savecheck"));

            File.WriteAllText(Path.Combine(_dir, HarnessCommands.SettingsFile), "settings changed");
            _game.Rebinds = "{\"x\":1}";
            string allowed = Run("savecheck");
            Assert.Contains("changes are allowed for this role", allowed);
            Assert.Contains(HarnessCommands.RebindsName + ": CHANGED", allowed);

            File.WriteAllText(Path.Combine(_dir, protectedName), "client wrote its save");
            string violation = Run("savecheck");
            Assert.Contains("VIOLATION: a protected save file changed while this game was a client", violation);
            Assert.Contains(protectedName + ": CHANGED", violation);

            Assert.StartsWith("savecheck baseline reset", Run("savecheck reset"));
            Assert.Contains("nothing changed", Run("savecheck"));
        }

        [Fact]
        public void SaveCheck_HostMayWriteItsOwnSave()
        {
            string protectedName = LocalSaveFiles.ProtectedNames[0];
            _game.Session = false;
            _host.Frame();
            _game.Session = true;
            _host.Frame();
            Assert.Contains(_output, o => o == "savecheck baseline taken at session start (host)");
            File.WriteAllText(Path.Combine(_dir, protectedName), "host saved");
            string result = Run("savecheck");
            Assert.Contains("changes are allowed for this role", result);
            Assert.Contains(protectedName + ": APPEARED [allowed]", result);
        }

        [Fact]
        public void MiscCommands()
        {
            Assert.StartsWith("host: current (1, 0)", Run("wind"));
            Assert.Equal("bot link latency 50 ms one way", Run("latency 50"));
            Assert.Equal(50, _host.Bots.LatencyMs);
            Assert.StartsWith("usage: latency", Run("latency -1"));
            Assert.Equal("check all: ok", Run("check"));
            Assert.Equal("check pause: ok", Run("check pause"));
            Assert.Equal("2 sets:\n  Main\n  BO_Toilet1", Run("list sets"));
            Assert.Equal("cannot list doors: unknown", Run("list doors"));
            Assert.StartsWith("usage: log", Run("log loud"));
            var before = NGGMultiplayer.Core.Diagnostics.NetLog.MinLevel;
            try
            {
                Assert.StartsWith("log level Debug", Run("log debug"));
            }
            finally
            {
                NGGMultiplayer.Core.Diagnostics.NetLog.MinLevel = before;
            }
        }

        [Fact]
        public void Parsers()
        {
            Assert.True(HarnessCommands.TryParseWorldOp("2", out var op) && op == WorldOp.OpenDoor);
            Assert.True(HarnessCommands.TryParseWorldOp("lmugcmachine", out op) && op == WorldOp.LmugcMachine);
            Assert.False(HarnessCommands.TryParseWorldOp("0", out _));
            Assert.False(HarnessCommands.TryParseWorldOp(null, out _));
            Assert.True(HarnessCommands.TryParseHex("0xA0ff", out var bytes));
            Assert.Equal(new byte[] { 0xA0, 0xFF }, bytes);
            Assert.True(HarnessCommands.TryParseHex("", out bytes) && bytes.Length == 0);
            Assert.False(HarnessCommands.TryParseHex("abc", out _));
            Assert.Equal("harness", HarnessCommands.BotLabel(null));
            Assert.Contains(HarnessCommands.SettingsFile, HarnessCommands.SaveFileNames);
            foreach (var name in LocalSaveFiles.ProtectedNames)
                Assert.Contains(name, HarnessCommands.SaveFileNames);
        }
    }
}
