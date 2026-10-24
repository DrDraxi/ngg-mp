// Unit tests of the test harness's Unity-free parts: bot ids, the composite transport, the interaction table
// (answers #35, #92, #137, #138), claim bookkeeping, fact text, JSON, the command file and the save-file check
// (answers #31/#71: settings writers).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Save;
using NGGMultiplayer.Game;
using NGGMultiplayer.Game.Progression;
using NGGMultiplayer.Testing;
using Xunit;

namespace NGGMultiplayer.Tests.Testing
{
    public class BotIdsTests
    {
        [Fact]
        public void BotIds_RoundTrip_AndNeverLookLikeSteamIds()
        {
            for (int i = 1; i <= BotIds.MaxBots; i++)
            {
                var id = BotIds.Make(i);
                Assert.True(BotIds.IsBot(id));
                Assert.Equal(i, BotIds.IndexOf(id));
                Assert.Equal(0UL, id.Value >> 56); // universe 0: no Steam user can have it
            }
            Assert.False(BotIds.IsBot(new PeerId(76561198000000000UL)));
            Assert.False(BotIds.IsBot(new PeerId(BotIds.Prefix << 32)));
            Assert.False(BotIds.IsBot(new PeerId((BotIds.Prefix << 32) | (uint)(BotIds.MaxBots + 1))));
            Assert.Equal(0, BotIds.IndexOf(new PeerId(5)));
            Assert.Throws<ArgumentOutOfRangeException>(() => BotIds.Make(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => BotIds.Make(BotIds.MaxBots + 1));
        }
    }

    public class CompositeTransportTests
    {
        private static readonly PeerId Host = new PeerId(76561198000000001UL);
        private static readonly PeerId Steam = new PeerId(76561198000000002UL);

        private static ArraySegment<byte> Seg(params byte[] bytes) => new ArraySegment<byte>(bytes);

        private static List<IncomingPacket> Drain(ITransport t)
        {
            var list = new List<IncomingPacket>();
            t.Poll(list);
            return list;
        }

        [Fact]
        public void RoutesBotsToTheLoopback_AndEveryoneElseToThePrimary()
        {
            var steamHub = new LoopbackHub();
            var primary = steamHub.CreateTransport(Host);
            var remote = steamHub.CreateTransport(Steam);
            var botHub = new LoopbackHub();
            var composite = new CompositeTransport(primary, botHub.CreateTransport(Host)) { LoopbackEnabled = true };
            var bot = botHub.CreateTransport(BotIds.Make(1));

            composite.Send(Steam, Channel.Control, Reliability.Reliable, Seg(1));
            composite.Send(BotIds.Make(1), Channel.Control, Reliability.Reliable, Seg(2));
            Assert.Equal(new byte[] { 1 }, Assert.Single(Drain(remote)).Data);
            Assert.Equal(new byte[] { 2 }, Assert.Single(Drain(bot)).Data);

            remote.Send(Host, Channel.Control, Reliability.Reliable, Seg(3));
            bot.Send(Host, Channel.Control, Reliability.Reliable, Seg(4));
            var got = Drain(composite);
            Assert.Equal(2, got.Count);
            Assert.Contains(got, p => p.From == Steam);
            Assert.Contains(got, p => p.From == BotIds.Make(1));
            Assert.Equal(1, composite.LoopbackSent);
            Assert.Equal(1, composite.LoopbackReceived);
        }

        [Fact]
        public void WhileDisabled_IsExactlyThePrimary()
        {
            var steamHub = new LoopbackHub();
            var primary = steamHub.CreateTransport(Host);
            var botHub = new LoopbackHub();
            var composite = new CompositeTransport(primary, botHub.CreateTransport(Host));
            var bot = botHub.CreateTransport(BotIds.Make(2));

            composite.Send(BotIds.Make(2), Channel.Control, Reliability.Reliable, Seg(1));
            Assert.Empty(Drain(bot));
            bot.Send(Host, Channel.Control, Reliability.Reliable, Seg(2));
            Assert.Empty(Drain(composite));
            Assert.Equal(2, composite.LoopbackDropped);
        }

        [Fact]
        public void NonBotOnTheHub_IsNotTrusted()
        {
            var botHub = new LoopbackHub();
            var composite = new CompositeTransport(new LoopbackHub().CreateTransport(Host), botHub.CreateTransport(Host))
                { LoopbackEnabled = true };
            var impostor = botHub.CreateTransport(Steam);
            impostor.Send(Host, Channel.Control, Reliability.Reliable, Seg(9));
            Assert.Empty(Drain(composite));
            Assert.Equal(1, composite.LoopbackDropped);
        }

        [Fact]
        public void Dispose_KeepsThePrimaryWorking()
        {
            var steamHub = new LoopbackHub();
            var primary = steamHub.CreateTransport(Host);
            var remote = steamHub.CreateTransport(Steam);
            var composite = new CompositeTransport(primary, new LoopbackHub().CreateTransport(Host)) { LoopbackEnabled = true };
            composite.Dispose();
            Assert.True(composite.IsDisposed);
            Assert.False(composite.LoopbackEnabled);
            composite.Send(Steam, Channel.Control, Reliability.Reliable, Seg(5));
            Assert.Single(Drain(remote));
            composite.Send(BotIds.Make(1), Channel.Control, Reliability.Reliable, Seg(6));
            composite.Disconnect(BotIds.Make(1)); // no throw after dispose
            remote.Send(Host, Channel.Control, Reliability.Reliable, Seg(7));
            Assert.Single(Drain(composite));
        }

        [Fact]
        public void LocalIdsMustMatch()
        {
            Assert.Throws<ArgumentException>(() =>
                new CompositeTransport(new LoopbackHub().CreateTransport(Host), new LoopbackHub().CreateTransport(Steam)));
        }
    }

    public class BotInteractionsTests
    {
        private static byte Of(string name)
        {
            Assert.True(BotInteractions.TryParse(name, out byte value));
            return value;
        }

        [Fact]
        public void InteractionTable_MatchesTheConfirmedEnumOrder()
        {
            // Answers #92: InteractionType 0..21 in this order.
            Assert.Equal(22, BotInteractions.Names.Length);
            Assert.Equal(0, Of("ContinueShot"));
            Assert.Equal(1, Of("CollectCash"));
            Assert.Equal(8, Of("LMUGCTicket"));
            Assert.Equal(19, Of("BuyHoleSkip"));
            Assert.Equal(20, Of("FindMissingBall"));
            Assert.Equal(21, Of("TalkToLuke"));
            Assert.Equal(5, Of("getcrowbar"));
            Assert.Equal(7, Of("7"));
            Assert.False(BotInteractions.TryParse("22", out _));
            Assert.False(BotInteractions.TryParse("Nope", out _));
            Assert.Equal("#40", BotInteractions.NameOf(40));
        }

        [Fact]
        public void BankReports_FollowTheGamesChangeBalanceCalls()
        {
            Assert.Equal(250, BotInteractions.ExpectedBankReport(Of("CollectCash"), 250, 0));
            Assert.Null(BotInteractions.ExpectedBankReport(Of("CollectCash"), 0, 0));
            // #92: FindMissingBall pays a hard-coded 1000 whatever m_cashValue is.
            Assert.Equal(1000, BotInteractions.ExpectedBankReport(Of("FindMissingBall"), 5, 0));
            // #35/#137/#138: LMUGCTicket costs exactly one -m_cashValue (no second charge), only when affordable.
            Assert.Equal(-300, BotInteractions.ExpectedBankReport(Of("LMUGCTicket"), 300, 300));
            Assert.Null(BotInteractions.ExpectedBankReport(Of("LMUGCTicket"), 300, 299));
            foreach (var purchase in new[] { "BuyClub", "BuyMulligan", "BuyNoWind", "BuyHoleSkip" })
            {
                Assert.Equal(-50, BotInteractions.ExpectedBankReport(Of(purchase), 50, 1000));
                Assert.Null(BotInteractions.ExpectedBankReport(Of(purchase), 50, 10));
            }
            Assert.Null(BotInteractions.ExpectedBankReport(Of("GetCrowbar"), 100, 1000));
            Assert.Null(BotInteractions.ExpectedBankReport(Of("GetCard"), 100, 1000));
            Assert.Null(BotInteractions.ExpectedBankReport(Of("Teleport"), 100, 1000));
        }

        [Fact]
        public void Arbitration_MatchesTheProgressionModule()
        {
            // Interactions.KindOf (6.7), name by name.
            var expected = new Dictionary<string, ArbitrationKind>
            {
                ["CollectCash"] = ArbitrationKind.CollectCash, ["FindMissingBall"] = ArbitrationKind.FindMissingBall,
                ["GetCrowbar"] = ArbitrationKind.Pickup, ["GetCard"] = ArbitrationKind.Pickup, ["LMUGCTicket"] = ArbitrationKind.Pickup,
                ["BuyClub"] = ArbitrationKind.Purchase, ["BuyMulligan"] = ArbitrationKind.Purchase,
                ["BuyNoWind"] = ArbitrationKind.Purchase, ["BuyHoleSkip"] = ArbitrationKind.Purchase
            };
            for (byte i = 0; i < BotInteractions.Names.Length; i++)
            {
                var want = expected.TryGetValue(BotInteractions.Names[i], out var kind) ? kind : ArbitrationKind.None;
                Assert.Equal(want, BotInteractions.KindOf(i));
            }
            Assert.True(BotInteractions.HidesObject(Of("GetCrowbar")));
            Assert.True(BotInteractions.HidesObject(Of("GetCard")));
            Assert.True(BotInteractions.HidesObject(Of("LMUGCTicket")));
            Assert.False(BotInteractions.HidesObject(Of("BuyClub")));
            Assert.True(BotInteractions.HidesObject(Of("CollectCash")));
            Assert.False(BotInteractions.HidesObject(Of("Teleport")));
        }
    }

    public class BotClaimsTests
    {
        private static SaveDelta Claim(string kind, string key, byte[] value) => new SaveDelta(kind, key, value);

        [Fact]
        public void Claims_SettleOnBroadcastRejectionOrTimeout()
        {
            var claims = new BotClaims();
            claims.Sent(Claim(FactKinds.Bounty, "BO_Toilet1", FactCodec.EncodeFlags(FactCodec.BountyDone)), 0);
            claims.Sent(Claim(FactKinds.RunInt, "x", FactTable.EncodeInt(3)), 0);
            claims.Sent(Claim(FactKinds.RunBool, "y", FactTable.EncodeBool(true)), 0);
            claims.Sent(Claim(FactKinds.Obj, "z", new byte[] { 1 }), 1);
            Assert.Equal(4, claims.PendingCount);

            claims.OnDelta(Claim(FactKinds.Bounty, "BO_Toilet1", FactCodec.EncodeFlags(FactCodec.BountyDone)), 0.5);
            claims.OnDelta(Claim(FactKinds.RunInt, "x", FactTable.EncodeInt(4)), 0.6);
            claims.OnDelta(Claim(FactKinds.RunInt, "unrelated", FactTable.EncodeInt(4)), 0.6);
            claims.OnRejected(Claim(FactKinds.RunBool, "y", FactTable.EncodeBool(true)), "not allowed", 0.7);
            claims.Tick(10.5);
            Assert.Equal(1, claims.PendingCount); // sent at 1: not yet 10 s
            claims.Tick(11.0);
            Assert.Equal(0, claims.PendingCount);

            var results = new List<BotClaimResult>();
            claims.Drain(results);
            Assert.Equal(new[] { BotClaimOutcome.Accepted, BotClaimOutcome.AcceptedOther, BotClaimOutcome.Rejected, BotClaimOutcome.NoAnswer },
                results.Select(r => r.Outcome).ToArray());
            Assert.Contains("host value 4", results[1].ToString());
            Assert.Contains("not allowed", results[2].ToString());
            Assert.Contains("done", results[0].ToString());
            Assert.Equal(2, claims.Accepted);
            Assert.Equal(1, claims.Rejected);
            Assert.Equal(1, claims.Unanswered);
            claims.Drain(results);
            Assert.Equal(4, results.Count);
        }

        [Fact]
        public void NewerClaimReplacesOlder_AndAbandonSettlesTheRest()
        {
            var claims = new BotClaims();
            claims.Sent(Claim(FactKinds.RunInt, "x", FactTable.EncodeInt(1)), 0);
            claims.Sent(Claim(FactKinds.RunInt, "x", FactTable.EncodeInt(2)), 1);
            Assert.Equal(1, claims.PendingCount);
            claims.AlreadySet(Claim(FactKinds.RunInt, "q", FactTable.EncodeInt(2)));
            claims.Abandon(2);
            var results = new List<BotClaimResult>();
            claims.Drain(results);
            Assert.Equal(BotClaimOutcome.AlreadySet, results[0].Outcome);
            Assert.DoesNotContain(" after ", results[0].ToString());
            Assert.Equal(BotClaimOutcome.Abandoned, results[1].Outcome);
            Assert.Equal(1.0, results[1].Seconds, 3);
        }
    }

    public class BotWireTests
    {
        // Read back in the order the Progression module's host handlers read (Interactions.OnClaim / OnBankReport,
        // WorldEvents.OnWorld / OnStory) and as its client handlers read the host's grant, note and digest.
        [Fact]
        public void ClientPayloads_ReadLikeTheHostReadsThem()
        {
            var w = new NetWriter(64);
            BotWire.WriteClaim(w, 7, "Ticket_1", 8);
            BotWire.WriteBank(w, 7, -300);
            BotWire.WriteWorld(w, WorldOp.SetGates, "Gate_2", true);
            BotWire.WriteMethod(w, "LukeLeaveSequence");
            BotWire.WriteRoundScore(w, -2);
            BotWire.WriteLmugcBest(w, 10, 8, 12, 9);
            var r = new NetReader(w.ToArray());
            Assert.Equal(7u, r.ReadVarUInt());
            Assert.Equal("Ticket_1", r.ReadString(ProgressionRpc.MaxInteractIdBytes));
            Assert.Equal(8, r.ReadByte());
            Assert.Equal(7u, r.ReadVarUInt());
            Assert.Equal(-300, r.ReadVarInt());
            Assert.Equal((byte)WorldOp.SetGates, r.ReadByte());
            Assert.Equal("Gate_2", r.ReadString(ProgressionRpc.MaxWorldIdBytes));
            Assert.True(r.ReadBool());
            Assert.Equal("LukeLeaveSequence", r.ReadString(ProgressionRpc.MaxMethodBytes));
            Assert.Equal(-2, r.ReadVarInt());
            Assert.Equal(new[] { 10, 8, 12, 9 }, new[] { r.ReadVarInt(), r.ReadVarInt(), r.ReadVarInt(), r.ReadVarInt() });
            Assert.True(r.IsAtEnd);
        }

        [Fact]
        public void HostPayloads_Parse()
        {
            var w = new NetWriter(64);
            BotWire.WriteGrant(w, new BotWire.Grant(3, true, 900, "ok"));
            w.WriteULong(42);
            w.WriteByte(2);
            w.WriteString("note text");
            w.WriteVarUInt(17);
            w.WriteUInt(0xDEADBEEF);
            w.WriteVarUInt(5);
            var r = new NetReader(w.ToArray());
            var grant = BotWire.ReadGrant(r);
            Assert.Equal((3u, true, 900, "ok"), (grant.ClaimId, grant.Granted, grant.HostBalance, grant.Reason));
            var note = BotWire.ReadNote(r);
            Assert.Equal((new PeerId(42), (byte)2, "note text"), (note.By, note.Kind, note.Text));
            var digest = BotWire.ReadDigest(r);
            Assert.Equal((17u, 0xDEADBEEFu, 5u), (digest.Revision, digest.Crc, digest.Count));
            Assert.True(r.IsAtEnd);
            Assert.Throws<ProtocolException>(() => BotWire.ReadGrant(new NetReader(new byte[] { 1 })));
        }
    }

    public class FactTextTests
    {
        [Fact]
        public void FormatsEveryKindReadably_AndFallsBackToHex()
        {
            Assert.Equal("Main#3", FactText.Format(FactKinds.Track, FactCodec.EncodeTrack(new TrackValue("Main", 3))));
            Assert.Equal("done+easy", FactText.Format(FactKinds.Bounty, FactCodec.EncodeFlags(FactCodec.BountyDone | FactCodec.BountyOnEasy)));
            Assert.Equal("true", FactText.Format(FactKinds.RunBool, FactTable.EncodeBool(true)));
            Assert.Equal("-42", FactText.Format(FactKinds.Bank, FactTable.EncodeInt(-42)));
            Assert.Equal("inactive", FactText.Format(FactKinds.Obj, FactCodec.EncodeObj(new ObjState(false, false, default))));
            Assert.Equal("[a, b]", FactText.Format(FactKinds.PreHit, FactCodec.EncodeStringSet(new[] { "a", "b" })));
            Assert.Equal("[101]", FactText.Format(FactKinds.Unlocks, FactCodec.EncodeBoolList(new[] { true, false, true })));
            Assert.Equal("deducted", FactText.Format(FactKinds.Money, new byte[] { 1 }));
            Assert.Equal("0x0102", FactText.Format(FactKinds.RunBool, new byte[] { 1, 2 }));
            Assert.Equal("0x07", FactText.Format("unknown", new byte[] { 7 }));
            Assert.Equal("null", FactText.Format(FactKinds.Bank, null));
            Assert.Equal("0x", FactText.Hex(new byte[0]));
            string longHex = FactText.Hex(new byte[500]);
            Assert.EndsWith("(500 bytes)", longHex);
            Assert.True(FactText.Format(FactKinds.ScoreCard, Encoding.UTF8.GetBytes(new string('x', 400))).Length <= FactText.MaxTextChars + 3);
        }
    }

    public class JsonBuilderTests
    {
        [Fact]
        public void BuildsNestedJson_WithEscapes()
        {
            string json = new JsonBuilder()
                .BeginObject()
                .Value("s", "a\"b\\c\n\u0001")
                .Value("b", true)
                .Value("n", -5L)
                .Value("d", 1.23456)
                .Value("nan", double.NaN)
                .Id("id", ulong.MaxValue)
                .Value("nil", (string)null)
                .BeginArray("list").Item("x").Item("y").EndArray()
                .BeginObject("o").EndObject()
                .EndObject()
                .ToString();
            Assert.Equal("{\"s\":\"a\\\"b\\\\c\\n\\u0001\",\"b\":true,\"n\":-5,\"d\":1.235,\"nan\":null,\"id\":\"18446744073709551615\"," +
                         "\"nil\":null,\"list\":[\"x\",\"y\"],\"o\":{}}", json);
        }

        [Fact]
        public void CloseTo_ClosesWhatAWriterLeftOpen()
        {
            var json = new JsonBuilder().BeginObject();
            int depth = json.Depth;
            json.BeginObject("half").BeginArray("list").Item("x");
            json.CloseTo(depth).Value("error", "boom").EndObject();
            Assert.Equal("{\"half\":{\"list\":[\"x\"]},\"error\":\"boom\"}", json.ToString());
            Assert.Equal(0, json.Depth);
        }

        [Fact]
        public void Misuse_Throws()
        {
            Assert.Throws<InvalidOperationException>(() => new JsonBuilder().BeginObject().Item("x"));
            Assert.Throws<InvalidOperationException>(() => new JsonBuilder().BeginArray().Value("n", 1L));
            Assert.Throws<InvalidOperationException>(() => new JsonBuilder().BeginObject().EndArray());
            Assert.Throws<InvalidOperationException>(() => new JsonBuilder().BeginObject().ToString());
            Assert.Throws<InvalidOperationException>(() => new JsonBuilder().BeginObject().EndObject().BeginObject());
            Assert.Throws<InvalidOperationException>(() => new JsonBuilder().BeginObject("root"));
        }
    }

    public class CommandFileTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "ngg_harness_" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try
            {
                Directory.Delete(_dir, true);
            }
            catch (IOException)
            {
            }
        }

        [Fact]
        public void Parse_SplitsWordsQuotesAndComments()
        {
            var c = HarnessCommand.Parse("  BOT Claim bounty \"BO Toilet 1\" # comment");
            Assert.Equal("bot", c.Name);
            Assert.Equal(4, c.Count);
            Assert.Equal("claim", c.ArgLower(1));
            Assert.Equal("BO Toilet 1", c.Arg(3));
            Assert.Null(c.Arg(4));
            Assert.Equal("bounty BO Toilet 1", c.Rest(2));
            Assert.Equal(string.Empty, c.Rest(9));
            Assert.Null(HarnessCommand.Parse("   "));
            Assert.Null(HarnessCommand.Parse("# only a comment"));
            Assert.Null(HarnessCommand.Parse(null));
            Assert.Equal("a#b", HarnessCommand.Parse("x a#b").Arg(1)); // # only starts a comment at a word start
            Assert.Equal(string.Empty, HarnessCommand.Parse("x \"\"").Arg(1));
            Assert.True(HarnessCommand.TryParseSwitch("ON", out bool on) && on);
            Assert.True(HarnessCommand.TryParseSwitch("0", out bool off) && !off);
            Assert.False(HarnessCommand.TryParseSwitch("maybe", out _));
            Assert.True(HarnessCommand.TryParseFloat("0.5", out float f) && f == 0.5f);
            Assert.False(HarnessCommand.TryParseFloat("NaN", out _));
            Assert.True(HarnessCommand.TryParseInt("-3", out int i) && i == -3);
        }

        [Fact]
        public void Reader_SkipsOldLines_WaitsForPartialLines_AndRestartsOnlyWhenEmptied()
        {
            string path = Path.Combine(_dir, "sub", "commands.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "old command\n");
            var reader = new CommandFileReader(path);
            reader.Start();
            Assert.Empty(reader.ReadNewLines());

            File.AppendAllText(path, "status\r\nbot add");
            Assert.Equal(new[] { "status" }, reader.ReadNewLines());
            File.AppendAllText(path, " Alice\n\n");
            Assert.Equal(new[] { "bot add Alice", "" }, reader.ReadNewLines());
            Assert.Empty(reader.ReadNewLines());

            // Shorter but not empty (a line deleted, or replaced in one save): nothing is run again.
            File.WriteAllText(path, "old command\nstatus\r\n");
            Assert.Empty(reader.ReadNewLines());
            Assert.True(reader.ShrankLastRead);
            File.AppendAllText(path, "next\n");
            Assert.Equal(new[] { "next" }, reader.ReadNewLines());
            Assert.False(reader.ShrankLastRead);

            // Emptied: read from the start again.
            File.WriteAllText(path, string.Empty);
            Assert.Empty(reader.ReadNewLines());
            Assert.False(reader.ShrankLastRead);
            File.WriteAllBytes(path, new byte[] { 0xEF, 0xBB, 0xBF, (byte)'h', (byte)'i', (byte)'\n' });
            Assert.Equal(new[] { "hi" }, reader.ReadNewLines());

            File.Delete(path);
            Assert.Empty(reader.ReadNewLines());
            File.WriteAllText(path, "again\n");
            Assert.Equal(new[] { "again" }, reader.ReadNewLines());
        }

        [Fact]
        public void Reader_CreatesAMissingFile_AndCanReadExistingLines()
        {
            string path = Path.Combine(_dir, "new", "commands.txt");
            var reader = new CommandFileReader(path);
            reader.Start();
            Assert.True(File.Exists(path));
            File.WriteAllText(path, "one\ntwo\n");
            var replay = new CommandFileReader(path);
            replay.Start(skipExisting: false);
            Assert.Equal(new[] { "one", "two" }, replay.ReadNewLines());
        }

        [Fact]
        public void Reader_ClipsVeryLongLines()
        {
            string path = Path.Combine(_dir, "long.txt");
            var reader = new CommandFileReader(path);
            reader.Start();
            File.AppendAllText(path, new string('a', CommandFileReader.MaxLineChars * 2) + "\n");
            Assert.Equal(CommandFileReader.MaxLineChars, Assert.Single(reader.ReadNewLines()).Length);
        }

        [Fact]
        public void Results_AreTimestamped_AndMultiLineIsIndented()
        {
            var at = new DateTime(2026, 10, 20, 15, 38, 11, 250);
            Assert.Equal("[2026-10-20 15:38:11.250] ok", ResultWriter.Format(at, "ok"));
            Assert.Equal("[2026-10-20 15:38:11.250] > status\n    host: yes", ResultWriter.Format(at, "> status\r\nhost: yes\n"));

            string path = Path.Combine(_dir, "results.txt");
            var writer = new ResultWriter(path, () => at);
            writer.Write("first");
            writer.Write("second");
            var lines = File.ReadAllLines(path);
            Assert.Equal(new[] { "[2026-10-20 15:38:11.250] first", "[2026-10-20 15:38:11.250] second" }, lines);
            Assert.Equal(2, writer.LinesWritten);
        }
    }

    public class SaveFileCheckTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "ngg_savecheck_" + Guid.NewGuid().ToString("N"));

        public SaveFileCheckTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try
            {
                Directory.Delete(_dir, true);
            }
            catch (IOException)
            {
            }
        }

        private static readonly string[] Names = { "save.txt", "gone.txt", "new.txt", "config.txt" };

        [Fact]
        public void Client_AnyProtectedChangeIsAViolation_SettingsAreNot()
        {
            File.WriteAllText(Path.Combine(_dir, "save.txt"), "a");
            File.WriteAllText(Path.Combine(_dir, "gone.txt"), "b");
            File.WriteAllText(Path.Combine(_dir, "config.txt"), "c");
            var before = SaveFileSnapshot.Take(_dir, Names, DateTime.UtcNow, "session start");
            Assert.Equal(4, before.Count);
            Assert.True(before.TryGet("new.txt", out var missing) && !missing.Exists);

            var unchanged = SaveFileReport.Compare(before, SaveFileSnapshot.Take(_dir, Names, DateTime.UtcNow, "now"), new[] { "config.txt" }, false);
            Assert.False(unchanged.HasViolation);
            Assert.Equal(0, unchanged.ChangedCount);

            // answers #31/#71: SaveGameSettings writes config.txt at any time; the player's own, so allowed.
            File.WriteAllText(Path.Combine(_dir, "config.txt"), "c2");
            var settingsOnly = SaveFileReport.Compare(before, SaveFileSnapshot.Take(_dir, Names, DateTime.UtcNow, "now"), new[] { "CONFIG.TXT" }, false);
            Assert.False(settingsOnly.HasViolation);
            Assert.Equal(1, settingsOnly.ChangedCount);
            Assert.Contains("config.txt: CHANGED (1 -> 2 bytes) [allowed]", settingsOnly.Describe());

            File.WriteAllText(Path.Combine(_dir, "save.txt"), "z");
            File.Delete(Path.Combine(_dir, "gone.txt"));
            File.WriteAllText(Path.Combine(_dir, "new.txt"), "n");
            var after = SaveFileSnapshot.Take(_dir, Names, DateTime.UtcNow, "now");
            var client = SaveFileReport.Compare(before, after, new[] { "config.txt" }, false);
            Assert.True(client.HasViolation);
            Assert.Equal(4, client.ChangedCount);
            string text = client.Describe();
            Assert.Contains("save.txt: CHANGED (1 -> 1 bytes) [VIOLATION]", text);
            Assert.Contains("gone.txt: VANISHED [VIOLATION]", text);
            Assert.Contains("new.txt: APPEARED [VIOLATION]", text);

            var host = SaveFileReport.Compare(before, after, new[] { "config.txt" }, true);
            Assert.False(host.HasViolation);
        }

        [Fact]
        public void PseudoFiles_HashLikeFiles()
        {
            var a = SaveFileSnapshot.HashValue("PlayerPrefs/rebinds", "{}");
            var b = SaveFileSnapshot.HashValue("PlayerPrefs/rebinds", "{}");
            var c = SaveFileSnapshot.HashValue("PlayerPrefs/rebinds", "{ }");
            Assert.True(a.SameContent(b));
            Assert.False(a.SameContent(c));
            Assert.False(SaveFileSnapshot.HashValue("x", null).Exists);
            File.WriteAllText(Path.Combine(_dir, "f.txt"), "{}");
            Assert.Equal(a.Sha256, SaveFileSnapshot.HashFile(_dir, "f.txt").Sha256);
            Assert.Equal(64, a.Sha256.Length);
        }
    }
}
