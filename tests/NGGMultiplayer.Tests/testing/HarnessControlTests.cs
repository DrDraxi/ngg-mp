// The harness commands that drive the game like a player (screenshot, key, buttons, click, scene, mpwindow, quit,
// wait): their pure parts (file names, key names, injected key frames, UI matching, the command queue) and the command
// layer over a fake control.
using System;
using System.Collections.Generic;
using System.IO;
using NGGMultiplayer.Testing;
using Xunit;

namespace NGGMultiplayer.Tests.Testing
{
    public class ScreenshotNamesTests
    {
        [Theory]
        [InlineData("main menu", "main_menu")]
        [InlineData("  F7 window!  ", "F7_window")]
        [InlineData("../../evil/../path", "evil_path")]
        [InlineData("C:\\Windows\\system32", "C_Windows_system32")]
        [InlineData("pause.png", "pause")]
        [InlineData("a<b>c:d\"e|f?g*h", "a_b_c_d_e_f_g_h")]
        [InlineData("...", "shot")]
        [InlineData("", "shot")]
        [InlineData(null, "shot")]
        [InlineData("___x___", "x")]
        [InlineData("Menü", "Men")]
        [InlineData("con", "_con")]
        [InlineData("LPT1.txt", "_LPT1.txt")]
        [InlineData("scene-2_v1.0", "scene-2_v1.0")]
        public void Sanitize_KeepsSafeCharactersOnly(string name, string expected)
        {
            Assert.Equal(expected, ScreenshotNames.Sanitize(name));
        }

        [Fact]
        public void Sanitize_CapsTheLength_AndNeverLeavesASeparator()
        {
            string name = ScreenshotNames.Sanitize(new string('a', 63) + " b" + new string('c', 40));
            Assert.Equal(new string('a', 63), name);
            Assert.True(ScreenshotNames.Sanitize(new string('x', 200)).Length == ScreenshotNames.MaxNameChars);
            foreach (char bad in Path.GetInvalidFileNameChars())
                Assert.DoesNotContain(bad, ScreenshotNames.Sanitize("a" + bad + "b"));
        }

        [Fact]
        public void FileName_IsTimestampedPng_AndNumberedOnCollision()
        {
            var at = new DateTime(2026, 10, 20, 15, 38, 11);
            Assert.Equal("20261020-153811_main_menu.png", ScreenshotNames.FileName(at, "main menu"));
            Assert.Equal("20261020-153811_shot.png", ScreenshotNames.FileName(at, null));
            Assert.Equal("x.png", ScreenshotNames.Numbered("x.png", 1));
            Assert.Equal("x-3.png", ScreenshotNames.Numbered("x.png", 3));
        }
    }

    public class KeyNamesTests
    {
        [Theory]
        [InlineData("esc", "Escape")]
        [InlineData("Escape", "Escape")]
        [InlineData("ESCAPE", "ESCAPE")] // parsed case-insensitively against the real enum
        [InlineData("tab", "tab")]
        [InlineData("f7", "F7")]
        [InlineData("F12", "F12")]
        [InlineData("f", "F")]
        [InlineData("fx", "fx")]
        [InlineData("a", "A")]
        [InlineData("7", "Digit7")]
        [InlineData("return", "Enter")]
        [InlineData("up", "UpArrow")]
        [InlineData("ctrl", "LeftCtrl")]
        [InlineData(" space ", "space")]
        [InlineData("alpha3", "Digit3")]
        [InlineData("", null)]
        [InlineData(null, null)]
        public void Normalize_ResolvesAliases(string text, string expected)
        {
            Assert.Equal(expected, KeyNames.Normalize(text));
        }

        [Theory]
        [InlineData("Escape", "Escape")]
        [InlineData("F7", "F7")]
        [InlineData("Tab", "Tab")]
        [InlineData("Enter", "Return")]
        [InlineData("Digit7", "Alpha7")]
        [InlineData("Numpad4", "Keypad4")]
        [InlineData("NumpadEnter", "KeypadEnter")]
        [InlineData("LeftCtrl", "LeftControl")]
        [InlineData("Backquote", "BackQuote")]
        [InlineData("A", "A")]
        public void LegacyName_MapsToKeyCode(string key, string expected)
        {
            Assert.Equal(expected, KeyNames.LegacyName(key));
        }
    }

    public class InjectedKeyStateTests
    {
        [Fact]
        public void DownInOneFrame_HeldUntilTheRelease()
        {
            var state = new InjectedKeyState();
            Assert.False(state.Any);
            state.Press("F7", 10);
            Assert.False(state.IsDown("F7", 9));
            Assert.True(state.IsDown("f7", 10));
            Assert.False(state.IsDown("F7", 11));
            Assert.True(state.IsHeld("F7", 10));
            Assert.True(state.IsHeld("F7", 50));
            state.Release("F7", 20);
            Assert.True(state.IsHeld("F7", 19));
            Assert.False(state.IsHeld("F7", 20));
            Assert.False(state.IsHeld("Tab", 15));
            state.Prune(20);
            Assert.True(state.Any);
            state.Prune(21);
            Assert.False(state.Any);
        }

        [Fact]
        public void ReleaseInThePressFrame_StillHoldsOneFrame()
        {
            var state = new InjectedKeyState();
            state.Press("Tab", 5);
            state.Release("Tab", 5);
            Assert.True(state.IsDown("Tab", 5));
            Assert.True(state.IsHeld("Tab", 5));
            Assert.False(state.IsHeld("Tab", 6));
            state.Release("Escape", 5); // never pressed: ignored
            Assert.False(state.IsHeld("Escape", 5));
        }
    }

    public class UiMatcherTests
    {
        private static readonly UiElement Play = new UiElement("Button", "Menu2:Canvas/Main/PlayButton", "<b>Play</b>", 1);
        private static readonly UiElement PlayNine = new UiElement("Button", "Menu2:Canvas/Main/PlayNineButton", "Play  9\nholes", 2);
        private static readonly UiElement Settings = new UiElement("Button", "Menu2:Canvas/Main/SettingsButton", "Settings", 3);
        private static readonly UiElement Quit = new UiElement("Button", "Menu2:Canvas/Main/QuitButton", "Quit", 4);
        private static readonly UiElement Icon = new UiElement("Toggle", "Menu2:Canvas/Main/StreamerToggle", null, 5);
        private static readonly IReadOnlyList<UiElement> All = new[] { Play, PlayNine, Settings, Quit, Icon };

        [Theory]
        [InlineData("<b>Play</b>", "Play")]
        [InlineData("  Play  9\n holes ", "Play 9 holes")]
        [InlineData("<color=#fff>Quit</color> <size=10>now</size>", "Quit now")]
        [InlineData("a < b", "a < b")]
        [InlineData(null, "")]
        public void CleanLabel_StripsTagsAndWhitespace(string text, string expected)
        {
            Assert.Equal(expected, UiMatcher.CleanLabel(text));
        }

        [Fact]
        public void ExactLabel_BeatsPartialMatches()
        {
            var match = UiMatcher.Match(All, "play");
            Assert.Same(Play, match.Chosen);
            Assert.Equal("exact label", match.How);
        }

        [Fact]
        public void PartialLabel_ThenPath()
        {
            Assert.Same(PlayNine, UiMatcher.Match(All, "9 HOLES").Chosen);
            var byPath = UiMatcher.Match(All, "streamer");
            Assert.Same(Icon, byPath.Chosen);
            Assert.Equal("path contains", byPath.How);
        }

        [Fact]
        public void Ambiguous_ListsCandidates_AndPickChooses()
        {
            var match = UiMatcher.Match(All, "Button");
            Assert.Null(match.Chosen);
            Assert.Equal(4, match.Candidates.Count);
            Assert.Same(Settings, UiMatcher.Match(All, "Button", 3).Chosen);
            Assert.Null(UiMatcher.Match(All, "Button", 9).Chosen);
        }

        [Fact]
        public void NoMatch_AndDescribe()
        {
            var match = UiMatcher.Match(All, "Continue");
            Assert.Null(match.Chosen);
            Assert.Empty(match.Candidates);
            Assert.Null(UiMatcher.Match(All, "   ").Chosen);
            string text = UiMatcher.Describe(All);
            Assert.StartsWith("5 clickable element(s):", text);
            Assert.Contains("1. [Button] 'Play' at Menu2:Canvas/Main/PlayButton", text);
            Assert.Contains("5. [Toggle] (no text) at Menu2:Canvas/Main/StreamerToggle", text);
            Assert.StartsWith("0 clickable element(s) (none", UiMatcher.Describe(Array.Empty<UiElement>()));
        }
    }

    public class CommandQueueTests
    {
        private static HarnessCommand C(string line) => HarnessCommand.Parse(line);

        [Fact]
        public void RunsInOrder_HoldsForWaitAndBusy()
        {
            var queue = new CommandQueue();
            Assert.True(queue.Add(C("key Escape")));
            Assert.True(queue.Add(C("screenshot pause")));
            Assert.False(queue.Add(null));
            Assert.True(queue.TryTake(0, false, out var first));
            Assert.Equal("key", first.Name);
            Assert.False(queue.TryTake(0, true, out _)); // the key is still held
            queue.Hold(0, 2);
            Assert.False(queue.TryTake(1.9, false, out _));
            queue.Hold(1.9, 0.01); // a shorter hold does not cut the longer one
            Assert.False(queue.TryTake(1.95, false, out _));
            Assert.True(queue.TryTake(2, false, out var second));
            Assert.Equal("screenshot", second.Name);
            Assert.False(queue.TryTake(10, false, out _));
            Assert.Equal(0, queue.Count);
        }

        [Fact]
        public void InfiniteHold_StopsForGood_AndClearResets()
        {
            var queue = new CommandQueue();
            queue.Add(C("status"));
            queue.Hold(5, double.PositiveInfinity);
            Assert.False(queue.TryTake(1e9, false, out _));
            queue.Hold(5, double.NaN);
            queue.Hold(5, -1);
            queue.Clear();
            Assert.False(queue.IsHeld(5));
            Assert.Equal(0, queue.Count);
        }

        [Fact]
        public void Bounded()
        {
            var queue = new CommandQueue();
            for (int i = 0; i < CommandQueue.MaxQueued; i++)
                Assert.True(queue.Add(C("status")));
            Assert.False(queue.Add(C("status")));
        }
    }

    internal sealed class FakeHarnessControl : IHarnessControl
    {
        public bool Busy { get; set; }
        public string ScreenshotDirectory => "/shots";
        public List<string> Shots { get; } = new List<string>();
        public List<(string Key, float Hold)> Keys { get; } = new List<(string, float)>();
        public List<UiElement> Elements { get; } = new List<UiElement>();
        public List<UiElement> Clicked { get; } = new List<UiElement>();
        public bool WindowVisible { get; set; }
        public int QuitCalls { get; private set; }
        public bool FailClick { get; set; }

        public bool Screenshot(string fileName, out string path, out string error)
        {
            Shots.Add(fileName);
            path = ScreenshotDirectory + "/" + fileName;
            error = null;
            return true;
        }

        public bool PressKey(string key, float holdSeconds, out string message)
        {
            if (key == "Bogus")
            {
                message = "unknown key name";
                return false;
            }
            Keys.Add((key, holdSeconds));
            message = key + " pressed";
            return true;
        }

        public IReadOnlyList<UiElement> FindClickables() => Elements;

        public bool PauseOpen { get; private set; }

        public bool SetPauseMenu(bool open, out string message)
        {
            PauseOpen = open;
            message = open ? "opened" : "closed";
            return true;
        }

        public bool Click(UiElement element, out string error)
        {
            error = FailClick ? "it is gone" : null;
            if (!FailClick)
                Clicked.Add(element);
            return !FailClick;
        }

        public List<(string Filter, int Depth)> UiTrees { get; } = new List<(string, int)>();

        public string DumpUiTree(string filter, int maxDepth)
        {
            UiTrees.Add((filter, maxDepth));
            return "uitree: written";
        }

        public string DescribeScene() => "active scene 'Menu2'";
        public string DescribeVideos() => "1 video player(s)";

        public bool SetLobbyWindow(bool? visible, out bool nowVisible, out string error)
        {
            WindowVisible = visible ?? !WindowVisible;
            nowVisible = WindowVisible;
            error = null;
            return true;
        }

        public string Quit()
        {
            QuitCalls++;
            return "MainMenu.Quit() called";
        }
    }

    public class HarnessControlCommandsTests : IDisposable
    {
        private readonly HarnessHost _host = new HarnessHost();
        private readonly FakeHarnessControl _control = new FakeHarnessControl();
        private readonly HarnessCommands _commands;
        private readonly HarnessCommands _withoutControl;

        public HarnessControlCommandsTests()
        {
            var game = new FakeHarnessGame(_host, Path.GetTempPath());
            Func<DateTime> utc = () => new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
            _commands = new HarnessCommands(_host.Bots, game, () => _host.Now, utc, _control);
            _withoutControl = new HarnessCommands(_host.Bots, game, () => _host.Now, utc);
        }

        public void Dispose() => _host.Dispose();

        private string Run(string line) => _commands.Execute(HarnessCommand.Parse(line));

        [Fact]
        public void Help_ListsTheDrivingCommands()
        {
            string help = Run("help");
            foreach (var name in new[] { "wait <seconds>", "screenshot [name]", "key <KeyName>", "buttons", "click <text", "scene", "mpwindow", "quit" })
                Assert.Contains(name, help);
        }

        [Fact]
        public void Screenshot_SanitizedTimestampedName()
        {
            string reply = Run("screenshot main menu/../x");
            Assert.Single(_control.Shots);
            string file = _control.Shots[0];
            Assert.Matches(@"^\d{8}-\d{6}_main_menu_x\.png$", file);
            Assert.StartsWith("screenshot /shots/" + file, reply);
            Assert.Contains("written at the end of this frame", reply);
            Run("screenshot");
            Assert.EndsWith("_shot.png", _control.Shots[1]);
        }

        [Fact]
        public void Key_DefaultHold_AliasesAndUsage()
        {
            Assert.Equal("Escape pressed", Run("key esc"));
            Assert.Equal(("Escape", HarnessCommands.DefaultKeyHold), _control.Keys[0]);
            Run("key F7 0.5");
            Assert.Equal(("F7", 0.5f), _control.Keys[1]);
            Assert.StartsWith("usage: key", Run("key"));
            Assert.StartsWith("usage: key", Run("key Tab forever"));
            Assert.StartsWith("usage: key", Run("key Tab -1"));
            Assert.StartsWith("usage: key", Run("key Tab 31"));
            Assert.Equal("cannot press Bogus: unknown key name", Run("key Bogus"));
            Assert.Equal(2, _control.Keys.Count);
            Assert.Equal(0, _commands.TakeHold());
        }

        [Fact]
        public void Key_Combination_NormalizesEveryPart()
        {
            Run("key ctrl+w 3");
            Assert.Equal(("LeftCtrl+W", 3f), _control.Keys[0]);
            Assert.StartsWith("usage: key", Run("key ctrl+ 1"));
            Assert.Single(_control.Keys);
        }

        [Fact]
        public void UiTree_PassesFilterAndDepth()
        {
            Assert.Equal("uitree: written", Run("uitree"));
            Assert.Equal("uitree: written", Run("uitree PauseMenu 3"));
            Assert.Equal("uitree: written", Run("uitree * 2"));
            Assert.StartsWith("usage: uitree", Run("uitree Canvas x"));
            Assert.Equal(new[] { ("", 64), ("PauseMenu", 3), ("", 2) }, _control.UiTrees);
        }

        [Fact]
        public void Buttons_AndClick()
        {
            _control.Elements.Add(new UiElement("Button", "Menu2:Canvas/PlayButton", "Play", null));
            _control.Elements.Add(new UiElement("Button", "Menu2:Canvas/PlayNineButton", "Play 9 holes", null));
            _control.Elements.Add(new UiElement("Button", "Menu2:Canvas/QuitButton", "Quit to Menu", null));
            Assert.StartsWith("3 clickable element(s):", Run("buttons"));

            Assert.Equal("clicked [Button] 'Play' at Menu2:Canvas/PlayButton (exact label)", Run("click PLAY"));
            Assert.Equal("clicked [Button] 'Quit to Menu' at Menu2:Canvas/QuitButton (exact label)", Run("click Quit to Menu"));
            Assert.Equal("clicked [Button] 'Quit to Menu' at Menu2:Canvas/QuitButton (exact label)", Run("click \"quit to menu\""));

            string ambiguous = Run("click Canvas");
            Assert.StartsWith("ambiguous: 3 elements match 'Canvas' (path contains); nothing clicked.", ambiguous);
            Assert.Contains("2. [Button] 'Play 9 holes'", ambiguous);
            Assert.Equal("clicked [Button] 'Play 9 holes' at Menu2:Canvas/PlayNineButton (path contains)", Run("click Canvas @2"));
            Assert.StartsWith("only 3 element(s) match", Run("click Canvas @7"));
            Assert.StartsWith("nothing matches 'Continue'; nothing clicked. 3 clickable", Run("click Continue"));
            Assert.StartsWith("usage: click", Run("click"));
            Assert.StartsWith("usage: click", Run("click Canvas @x"));
            Assert.Equal(4, _control.Clicked.Count);

            _control.FailClick = true;
            Assert.Equal("could not click [Button] 'Play' at Menu2:Canvas/PlayButton: it is gone", Run("click play"));
        }

        [Fact]
        public void Click_AnAtWordAloneIsAQuery()
        {
            _control.Elements.Add(new UiElement("Button", "Menu2:Canvas/MailButton", "@1", null));
            Assert.StartsWith("clicked [Button] '@1'", Run("click @1"));
        }

        [Fact]
        public void Wait_HoldsTheNextCommands()
        {
            Assert.Equal("waiting 1.5 s before the next command", Run("wait 1.5"));
            Assert.Equal(1.5, _commands.TakeHold(), 3);
            Assert.Equal(0, _commands.TakeHold());
            Assert.StartsWith("usage: wait", Run("wait"));
            Assert.StartsWith("usage: wait", Run("wait 0"));
            Assert.StartsWith("usage: wait", Run("wait 601"));
            Assert.Equal(0, _commands.TakeHold());
        }

        [Fact]
        public void SceneWindowQuit()
        {
            Assert.Equal("active scene 'Menu2'", Run("scene"));
            Assert.Equal("multiplayer window open", Run("mpwindow open"));
            Assert.Equal("multiplayer window closed", Run("mpwindow toggle"));
            Assert.Equal("multiplayer window closed", Run("mpwindow close"));
            Assert.StartsWith("usage: mpwindow", Run("mpwindow"));
            Assert.Equal("MainMenu.Quit() called; the following commands are not run", Run("quit"));
            Assert.Equal(1, _control.QuitCalls);
            Assert.True(double.IsPositiveInfinity(_commands.TakeHold()));
        }

        [Fact]
        public void WithoutControl_SaysSo()
        {
            foreach (var line in new[] { "screenshot", "key Escape", "buttons", "click Play", "scene", "mpwindow open", "quit" })
                Assert.StartsWith("not available", _withoutControl.Execute(HarnessCommand.Parse(line)));
            Assert.Equal(0, _withoutControl.TakeHold());
            Assert.StartsWith("waiting", _withoutControl.Execute(HarnessCommand.Parse("wait 1")));
        }
    }
}
