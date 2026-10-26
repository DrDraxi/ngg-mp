// The harness commands that play the local player (find, goto, interact, bounty, mode, shoot, locks, dialogue,
// remotes, player): their pure parsing, matching and placement, and the command layer over a fake game.
using System;
using System.Collections.Generic;
using System.IO;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Testing;
using Xunit;

namespace NGGMultiplayer.Tests.Testing
{
    public class ObjectMatcherTests
    {
        private static readonly SceneObject PadA = new SceneObject("pad", "BountyPad (3)", "Main:World/Pads/BountyPad (3)", "BO_Chip", new NetVector3(1, 0, 1), "open", 1);
        private static readonly SceneObject PadB = new SceneObject("pad", "BountyPad (4)", "Main:World/Pads/BountyPad (4)", "BO_ChipLong", new NetVector3(2, 0, 2), "open", 2);
        private static readonly SceneObject Padlock = new SceneObject("interactable", "Padlock", "Main:World/Hole1/Gate/Padlock", "so:abc", new NetVector3(3, 0, 3), "Padlock", 3);
        private static readonly SceneObject Padlock2 = new SceneObject("interactable", "Padlock", "Main:World/Shed/Padlock", "so:def", new NetVector3(4, 0, 4), "Padlock", 4);
        private static readonly IReadOnlyList<SceneObject> All = new[] { PadA, PadB, Padlock, Padlock2 };

        [Fact]
        public void ExactIdBeatsPartOfAnId()
        {
            var match = ObjectMatcher.Match(All, "bo_chip");
            Assert.Same(PadA, match.Chosen);
            Assert.Equal("exact id", match.How);
        }

        [Fact]
        public void PartOfAnIdIsAmbiguousUntilPicked()
        {
            var match = ObjectMatcher.Match(All, "Chi");
            Assert.Null(match.Chosen);
            Assert.Equal(2, match.Candidates.Count);
            Assert.Equal("id contains", match.How);
            Assert.Same(PadB, ObjectMatcher.Match(All, "Chi", 2).Chosen);
            Assert.Null(ObjectMatcher.Match(All, "Chi", 3).Chosen);
        }

        [Fact]
        public void ExactNameThenPath()
        {
            Assert.Equal("exact name", ObjectMatcher.Match(All, "padlock").How);
            Assert.Null(ObjectMatcher.Match(All, "padlock").Chosen);
            var shed = ObjectMatcher.Match(All, "Shed/");
            Assert.Same(Padlock2, shed.Chosen);
            Assert.Equal("path contains", shed.How);
            Assert.Empty(ObjectMatcher.Match(All, "nothing").Candidates);
        }

        [Fact]
        public void EmptyQueryChoosesTheOnlyOne()
        {
            Assert.Same(PadA, ObjectMatcher.Match(new[] { PadA }, "").Chosen);
            Assert.Null(ObjectMatcher.Match(All, "").Chosen);
            Assert.Equal(4, ObjectMatcher.Match(All, " ").Candidates.Count);
            Assert.Null(ObjectMatcher.Match(Array.Empty<SceneObject>(), "x").Chosen);
        }

        [Fact]
        public void DescribeAndFilter()
        {
            string text = ObjectMatcher.Describe(All);
            Assert.StartsWith("4 object(s):", text);
            Assert.Contains("1. [pad] 'BountyPad (3)' id BO_Chip open at (1, 0, 1) (Main:World/Pads/BountyPad (3))", text);
            Assert.Contains("... 2 more", ObjectMatcher.Describe(All, 2));
            Assert.Equal(2, ObjectMatcher.Filter(All, "PADLOCK").Count);
            Assert.Equal(4, ObjectMatcher.Filter(All, null).Count);
            Assert.Equal("0 object(s)", ObjectMatcher.Describe(ObjectMatcher.Filter(All, "zzz")));
        }
    }

    public class PlayArgsTests
    {
        private static bool Target(string text, bool allowPosition, string category, out TargetArgs args, out string error) =>
            PlayArgs.TryParseTarget(text.Split(' ', StringSplitOptions.RemoveEmptyEntries), allowPosition, category, out args, out error);

        [Fact]
        public void Target_CategoryQueryAndPick()
        {
            Assert.True(Target("pad BO_Chip", true, PlayArgs.AnyObject, out var args, out _));
            Assert.Equal(PlayArgs.Pad, args.Category);
            Assert.Equal("BO_Chip", args.Query);
            Assert.Equal(0, args.Pick);

            Assert.True(Target("Hole1 Gate Padlock @2", false, null, out args, out _));
            Assert.Null(args.Category);
            Assert.Equal("Hole1 Gate Padlock", args.Query);
            Assert.Equal(2, args.Pick);

            Assert.True(Target("door hole5Shed", false, null, out args, out _));
            Assert.Equal(PlayArgs.Lock, args.Category);
            Assert.True(Target("bounty BO_X", false, null, out args, out _));
            Assert.Equal(PlayArgs.Pad, args.Category);
        }

        [Fact]
        public void Target_Coordinates_OnlyWhereAllowed()
        {
            Assert.True(Target("10 2.5 -4", true, PlayArgs.AnyObject, out var args, out _));
            Assert.True(args.HasPosition);
            Assert.Equal(new NetVector3(10f, 2.5f, -4f), args.Position);
            Assert.True(Target("10 2.5 -4", false, null, out args, out _));
            Assert.False(args.HasPosition);
            Assert.Equal("10 2.5 -4", args.Query);
            Assert.True(Target("10 2.5", true, PlayArgs.AnyObject, out args, out _));
            Assert.False(args.HasPosition);
        }

        [Fact]
        public void Target_Errors()
        {
            Assert.False(Target("", true, PlayArgs.AnyObject, out _, out string error));
            Assert.Contains("name an object", error);
            Assert.False(Target("pad", true, PlayArgs.AnyObject, out _, out _));
            Assert.False(Target("x @0", true, PlayArgs.AnyObject, out _, out error));
            Assert.Contains("@n", error);
            Assert.False(Target("x @y", true, PlayArgs.AnyObject, out _, out _));
            Assert.True(Target("remote", true, PlayArgs.AnyObject, out var args, out _));
            Assert.Equal(PlayArgs.Remote, args.Category);
            Assert.Equal(string.Empty, args.Query);
            Assert.False(Target("@1", true, PlayArgs.AnyObject, out _, out _));
        }

        [Fact]
        public void Shot_DefaultsAndArguments()
        {
            Assert.True(PlayArgs.TryParseShot(Array.Empty<string>(), out var shot, out _));
            Assert.Equal(ShotRequest.DefaultPower, shot.Power);
            Assert.Null(shot.Angle);
            Assert.Equal("Good", shot.ShotType);

            Assert.True(PlayArgs.TryParseShot(new[] { "0.5", "-3", "Perfect" }, out shot, out _));
            Assert.Equal(0.5f, shot.Power);
            Assert.Equal(-3f, shot.Angle);
            Assert.Equal("Perfect", shot.ShotType);
            Assert.Equal("power 0.5, angle -3, Perfect", shot.ToString());

            Assert.True(PlayArgs.TryParseShot(new[] { "Thin", "1" }, out shot, out _));
            Assert.Equal(1f, shot.Power);
            Assert.Equal("Thin", shot.ShotType);
        }

        [Theory]
        [InlineData("0")]
        [InlineData("1.5")]
        [InlineData("-0.2")]
        [InlineData("0.5 91")]
        [InlineData("0.5 1 2")]
        [InlineData("0.5 Good!")]
        [InlineData("0.5 9abc")]
        public void Shot_Rejected(string text)
        {
            Assert.False(PlayArgs.TryParseShot(text.Split(' '), out _, out string error));
            Assert.False(string.IsNullOrEmpty(error));
        }

        [Theory]
        [InlineData("golf", true, true)]
        [InlineData("GOLF", true, true)]
        [InlineData("walk", true, false)]
        [InlineData("fps", true, false)]
        [InlineData("run", false, false)]
        [InlineData(null, false, false)]
        public void Mode(string word, bool ok, bool golf)
        {
            Assert.Equal(ok, PlayArgs.TryParseMode(word, out bool parsed));
            Assert.Equal(golf, parsed);
        }

        [Fact]
        public void Categories_AndAliases()
        {
            foreach (var category in PlayArgs.Categories)
                Assert.Equal(category, PlayArgs.Category(category));
            Assert.Equal(PlayArgs.Trigger, PlayArgs.Category("Dialogue"));
            Assert.Equal(PlayArgs.Remote, PlayArgs.Category("player"));
            Assert.Null(PlayArgs.Category("Padlock"));
            Assert.Null(PlayArgs.Category(null));
        }
    }

    public class PlacementTests
    {
        [Fact]
        public void StandsOnThePlayersSide_FacingTheObject()
        {
            var stand = Placement.StandBeside(new NetVector3(10, 5, 0), new NetVector3(0, 1, 0), 2f, 1f, out float yaw);
            Assert.Equal(3f, stand.x, 3);
            Assert.Equal(1f, stand.y, 3);
            Assert.Equal(0f, stand.z, 3);
            Assert.Equal(270f, yaw, 2); // looks along -x, back at the object
        }

        [Fact]
        public void OnTheCenter_GoesSouth_AndCapsHugeBounds()
        {
            var stand = Placement.StandBeside(new NetVector3(5, 0, 5), new NetVector3(5, 0, 5), 1000f, 1f, out float yaw);
            Assert.Equal(5f, stand.x, 3);
            Assert.Equal(5f - (Placement.MaxRadius + 1f), stand.z, 3);
            Assert.Equal(0f, yaw, 2);
            stand = Placement.StandBeside(new NetVector3(0, 0, -1), NetVector3.Zero, float.NaN, -1f, out _);
            Assert.Equal(-Placement.DefaultMargin, stand.z, 3);
        }

        [Theory]
        [InlineData(0, 1, 0)]
        [InlineData(1, 0, 90)]
        [InlineData(0, -1, 180)]
        [InlineData(-1, 0, 270)]
        public void YawTowards(float dx, float dz, float expected)
        {
            Assert.Equal(expected, Placement.YawTowards(NetVector3.Zero, new NetVector3(dx, 7, dz)), 2);
        }

        [Fact]
        public void FlatDistance_IgnoresHeight()
        {
            Assert.Equal(5f, Placement.FlatDistance(new NetVector3(0, 100, 0), new NetVector3(3, -4, 4)), 3);
        }
    }

    public class LockTextAndPowerTests
    {
        [Theory]
        [InlineData(true, false, LockState.Locked)]
        [InlineData(true, null, LockState.Locked)]
        [InlineData(false, true, LockState.Open)]
        [InlineData(null, true, LockState.Open)]
        [InlineData(true, true, LockState.Open)]
        [InlineData(false, null, LockState.Open)]
        [InlineData(false, false, LockState.Open)]
        [InlineData(null, false, LockState.Unknown)]
        [InlineData(null, null, LockState.Unknown)]
        public void Classify(bool? padlock, bool? opened, LockState expected)
        {
            Assert.Equal(expected, LockText.Classify(padlock, opened));
        }

        [Fact]
        public void Names()
        {
            Assert.Equal("OPEN", LockText.Name(LockState.Open));
            Assert.Equal("LOCKED", LockText.Name(LockState.Locked));
            Assert.Equal("unknown", LockText.Name(LockState.Unknown));
            Assert.Equal("none", LockText.Active(null));
            Assert.Equal("on", LockText.Active(true));
        }

        [Fact]
        public void PanelPower_FromTheClubsFullSwing()
        {
            Assert.Equal(7.7f, ShotPower.ToPanelPower(0.7f, 11f, out bool fallback), 3);
            Assert.False(fallback);
            Assert.Equal(5f, ShotPower.ToPanelPower(0.5f, null, out fallback), 3);
            Assert.True(fallback);
            Assert.Equal(7f, ShotPower.ToPanelPower(0.7f, 0f, out fallback), 3);
            Assert.True(fallback);
            Assert.Equal(ShotRequest.DefaultPower * 20f, ShotPower.ToPanelPower(3f, 20f, out _), 3);
        }
    }

    internal sealed class FakeHarnessPlay : IHarnessPlay
    {
        public Dictionary<string, List<SceneObject>> Objects { get; } = new Dictionary<string, List<SceneObject>>();
        public List<string> Calls { get; } = new List<string>();
        public bool NoWorld { get; set; }

        public IReadOnlyList<SceneObject> FindObjects(string category, out string error)
        {
            error = NoWorld ? "the world is not loaded" : null;
            if (NoWorld)
                return null;
            return Objects.TryGetValue(category, out var list) ? list : new List<SceneObject>();
        }

        public bool Goto(SceneObject target, out string message)
        {
            Calls.Add("goto " + target.Name);
            message = "moved beside " + target.Name;
            return true;
        }

        public bool GotoPosition(NetVector3 feet, float? yaw, out string message)
        {
            Calls.Add("goto " + PlayText.Vector(feet));
            message = "moved to " + PlayText.Vector(feet) + (yaw.HasValue ? " yaw " + PlayText.Number(yaw.Value) : string.Empty);
            return true;
        }

        public bool Interact(SceneObject target, out string message)
        {
            Calls.Add("interact " + target.Name);
            message = "used " + target.Name;
            return true;
        }

        public bool StartBounty(string setName, out string message)
        {
            Calls.Add("bounty start " + setName);
            message = setName == "BO_Missing" ? "no BountyPad has that set" : "started " + setName;
            return setName != "BO_Missing";
        }

        public bool ExitBounty(out string message)
        {
            Calls.Add("bounty exit");
            message = "left";
            return true;
        }

        public string DescribeBounty() => "activeBounty none";

        public bool Shoot(ShotRequest shot, out string message)
        {
            Calls.Add("shoot " + shot);
            message = "shot " + shot;
            return true;
        }

        public bool RestartShot(bool resume, out string message)
        {
            Calls.Add("restart " + resume);
            message = "restarted";
            return true;
        }

        public bool SetMode(bool golf, out string message)
        {
            Calls.Add("mode " + (golf ? "golf" : "walk"));
            message = golf ? "golf" : "walk";
            return true;
        }

        public string DescribeLocks(string filter) => "locks: 1 open, 0 locked" + (string.IsNullOrEmpty(filter) ? string.Empty : " / " + filter);

        public string DescribeDialogue() => "moveLocked False";

        public bool DialogueNext(out string message)
        {
            Calls.Add("dialogue next");
            message = "next";
            return true;
        }

        public string DescribeRemotes() => "remotes: 0";

        public string DescribePlayer() => "feet (0, 0, 0)";

        public string Match(string action)
        {
            Calls.Add("match " + action);
            return "match " + action;
        }
    }

    public class HarnessPlayCommandsTests : IDisposable
    {
        private readonly HarnessHost _host = new HarnessHost();
        private readonly FakeHarnessPlay _play = new FakeHarnessPlay();
        private readonly HarnessCommands _commands;
        private readonly HarnessCommands _withoutPlay;

        public HarnessPlayCommandsTests()
        {
            var game = new FakeHarnessGame(_host, Path.GetTempPath());
            Func<DateTime> utc = () => new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
            _commands = new HarnessCommands(_host.Bots, game, () => _host.Now, utc, new FakeHarnessControl(), _play);
            _withoutPlay = new HarnessCommands(_host.Bots, game, () => _host.Now, utc, new FakeHarnessControl());
            _play.Objects[PlayArgs.Pad] = new List<SceneObject>
            {
                new SceneObject(PlayArgs.Pad, "Pad1", "Main:Pads/Pad1", "BO_Chip", NetVector3.Zero, "open", 1),
                new SceneObject(PlayArgs.Pad, "Pad2", "Main:Pads/Pad2", "BO_Putt", NetVector3.Zero, "open", 2)
            };
            _play.Objects[PlayArgs.Interactable] = new List<SceneObject>
            {
                new SceneObject(PlayArgs.Interactable, "Padlock", "Main:Hole1/Padlock", "so:1", NetVector3.Zero, "Padlock", 3),
                new SceneObject(PlayArgs.Interactable, "Padlock", "Main:Shed/Padlock", "so:2", NetVector3.Zero, "Padlock", 4)
            };
            _play.Objects[PlayArgs.Trigger] = new List<SceneObject>
            {
                new SceneObject(PlayArgs.Trigger, "DEMO", "Main:Triggers/DEMO", "so:9", NetVector3.Zero, string.Empty, 5)
            };
            _play.Objects[PlayArgs.AnyObject] = new List<SceneObject>
            {
                new SceneObject(PlayArgs.AnyObject, "Flag", "Main:Hole1/Flag", string.Empty, NetVector3.Zero, string.Empty, 6)
            };
        }

        public void Dispose() => _host.Dispose();

        private string Run(string line) => _commands.Execute(HarnessCommand.Parse(line));

        [Fact]
        public void Help_ListsThePlayCommands()
        {
            string help = Run("help");
            foreach (var name in new[] { "find <object|", "goto [category]", "interact [category]", "bounty start <set>", "mode <golf|walk>",
                         "shoot [power 0..1]", "locks [text]", "dialogue [state|next|triggers", "remotes", "player" })
                Assert.Contains(name, help);
        }

        [Fact]
        public void Find_ListsAndFilters()
        {
            Assert.StartsWith("2 object(s):", Run("find pad"));
            Assert.StartsWith("1 object(s):", Run("find pads putt"));
            Assert.StartsWith("usage: find <object|interactable|tee|pad|trigger|lock|remote>", Run("find"));
            Assert.StartsWith("usage: find", Run("find widgets"));
            _play.NoWorld = true;
            Assert.Equal("cannot list pad: the world is not loaded", Run("find pad"));
        }

        [Fact]
        public void Goto_ObjectsPadsAndCoordinates()
        {
            Assert.Equal("moved beside Flag", Run("goto Flag"));
            Assert.Equal("moved beside Pad2", Run("goto pad BO_Putt"));
            Assert.Equal("moved to (1.5, 2, -3)", Run("goto 1.5 2 -3"));
            Assert.Equal("moved to (1.5, 2, -3) yaw 90", Run("goto 1.5 2 -3 90"));
            Assert.StartsWith("usage: goto", Run("goto 1.5 2 -3 east"));
            Assert.StartsWith("nothing matches 'Tree' (object); nothing done.", Run("goto Tree"));
            Assert.StartsWith("ambiguous: 2 objects match 'BO_' (id contains); nothing done.", Run("goto pad BO_"));
            Assert.Equal("moved beside Pad1", Run("goto pad BO_ @1"));
            Assert.StartsWith("only 2 object(s) match", Run("goto pad BO_ @3"));
            Assert.StartsWith("usage: goto", Run("goto"));
            Assert.Equal(new[] { "goto Flag", "goto Pad2", "goto (1.5, 2, -3)", "goto (1.5, 2, -3)", "goto Pad1" }, _play.Calls);
            _play.NoWorld = true;
            Assert.StartsWith("cannot go there: pad: the world is not loaded", Run("goto pad BO_Chip"));
        }

        [Fact]
        public void Interact_SearchesEveryUsableKind()
        {
            Assert.StartsWith("ambiguous: 2 objects match 'Padlock' (exact name)", Run("interact Padlock"));
            Assert.Equal("used Padlock", Run("interact Shed/Padlock"));
            Assert.Equal("used DEMO", Run("interact DEMO"));
            Assert.Equal("used Pad1", Run("interact pad BO_Chip"));
            Assert.StartsWith("nothing matches 'Flag' (interactable, tee, pad, trigger)", Run("interact Flag"));
            Assert.Equal("used Flag", Run("interact object Flag"));
            Assert.StartsWith("usage: interact", Run("interact"));
        }

        [Fact]
        public void Bounty_ModeShootAndReports()
        {
            Assert.Equal("activeBounty none", Run("bounty"));
            Assert.Equal("activeBounty none", Run("bounty state"));
            Assert.Equal("started BO_Chip", Run("bounty start BO_Chip"));
            Assert.Equal("cannot start bounty BO_Missing: no BountyPad has that set", Run("bounty start BO_Missing"));
            Assert.StartsWith("usage: bounty start", Run("bounty start"));
            Assert.Equal("left", Run("bounty exit"));
            Assert.StartsWith("usage: bounty", Run("bounty dance"));

            Assert.Equal("golf", Run("mode golf"));
            Assert.StartsWith("usage: mode", Run("mode fly"));
            Assert.Equal("shot power 0.7, Good", Run("shoot"));
            Assert.Equal("shot power 0.4, angle 2, Perfect", Run("shoot 0.4 2 Perfect"));
            Assert.StartsWith("usage: shoot", Run("shoot 4"));
            Assert.Equal("restarted", Run("shoot next"));
            Assert.Equal("restarted", Run("shoot again"));
            Assert.Contains("restart True", _play.Calls);
            Assert.Contains("restart False", _play.Calls);

            Assert.Equal("locks: 1 open, 0 locked / hole1", Run("locks hole1"));
            Assert.Equal("moveLocked False", Run("dialogue"));
            Assert.Equal("next", Run("dialogue next"));
            Assert.StartsWith("1 object(s):", Run("dialogue triggers DEMO"));
            Assert.StartsWith("usage: dialogue", Run("dialogue skip"));
            Assert.Equal("remotes: 0", Run("remotes"));
            Assert.Equal("feet (0, 0, 0)", Run("player"));
        }

        [Fact]
        public void WithoutPlay_SaysSo()
        {
            foreach (var line in new[] { "find pad", "goto Flag", "interact DEMO", "bounty state", "mode golf", "shoot", "locks", "dialogue", "remotes", "player" })
                Assert.StartsWith("not available", _withoutPlay.Execute(HarnessCommand.Parse(line)));
        }
    }
}
