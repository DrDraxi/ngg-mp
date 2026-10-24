using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using NGGMultiplayer.Core.Diagnostics;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Session;
using NGGMultiplayer.Game;
using NGGMultiplayer.Game.Lifecycle;
using NGGMultiplayer.Game.Progression;

namespace NGGMultiplayer.Testing
{
    /// <summary>
    /// The test harness's commands (one line of the command file each; <see cref="Help"/> lists them) and the per-frame
    /// work behind them: the bots (<see cref="BotHub"/>) and the save-file baseline of <c>savecheck</c>. Game access
    /// goes through <see cref="IHarnessGame"/>, so everything here is unit tested. Results are returned as text; what
    /// bots report later arrives through <see cref="Output"/>. Commands that drive the game like a player at the PC
    /// (screenshot, key, buttons, click, scene, mpwindow, quit) go through <see cref="IHarnessControl"/>; <c>wait</c>
    /// and <c>quit</c> hold the following commands (<see cref="TakeHold"/>). Main thread only.
    /// </summary>
    public sealed class HarnessCommands
    {
        public const string SettingsFile = "config.txt";
        public const string RebindsPref = "rebinds";
        public const string RebindsName = "PlayerPrefs/" + RebindsPref;

        public static readonly string Help =
            "Commands (one per line; @<bot number or name> picks a bot, default all for walk/shoot, newest for remove/leave, first for claim):\n" +
            "  help | status | dump | host | leave | wind | log <debug|info|warning|error> | latency <ms>\n" +
            "  savecheck [reset]          hash the real save files; report changes since the session started\n" +
            "  check [all|<name>]         runtime checks of the open confirmation answers\n" +
            "  list <sets|bounties|pickups|doors|gates|saveables>\n" +
            "  bot add [name] | bot remove [@bot] (vanish, host times out) | bot leave [@bot] (clean goodbye)\n" +
            "  bot walk <on|off> [@bot] | bot shoot [power 0..1] [@bot]\n" +
            "  bot claim bounty <set> [easy] | challenge [index] | challenge <set> <index>\n" +
            "  bot claim door <id> | gate <id> <open|close> | sliding <id> <open|close> | world <op> [id] [on|off]\n" +
            "  bot claim pickup <interactId> [<type> <cashValue>] | bank <delta> [claimId]\n" +
            "  bot claim rb <field> <true|false> | ri <field> <n> | obj <saveableId> <active|inactive>\n" +
            "  bot claim story <method> | storyhost <method> | fact <kind> <key> <hex>\n" +
            "Driving the game (the following commands wait for each to finish):\n" +
            "  wait <seconds>             hold the following commands (up to " + MaxWaitText + " s)\n" +
            "  screenshot [name]          capture the screen to BepInEx/screenshots/<time>_<name>.png\n" +
            "  key <KeyName> [hold s]     press and release a key through the Input System (default hold 0.1 s)\n" +
            "  buttons                    list active, interactable UI buttons with their path and text\n" +
            "  click <text or path> [@n]  click the button whose text (or path) matches; @n picks one of several\n" +
            "  uitree [filter] [depth]    write the UI hierarchy with components to BepInEx/uitree-<time>.txt\n" +
            "  scene                      scenes, timeScale, cursor, pause/settings menus, game and player mode\n" +
            "  videos                     video players, the quads showing them, and the female look's overlay\n" +
            "  mpwindow <open|close|toggle>  the multiplayer window (F7)\n" +
            "  pause [open|close]         the game's pause menu as Escape opens / Resume closes it (no focus needed)\n" +
            "  quit                       quit the game (MainMenu.Quit in Menu2, else Application.Quit)\n" +
            "Playing in this copy (the local player, through the game's own code; no keyboard or focus needed):\n" +
            "  find <object|interactable|tee|pad|trigger|lock|remote> [text]  list matching objects (name, id, path)\n" +
            "  goto [category] <name, id or path part> [@n] | goto <x> <y> <z> [yaw]  teleport the walking player beside it (or there, facing yaw)\n" +
            "  interact [category] <name, id or path part> [@n]  use it as the game does (Interact, tee, pad, trigger)\n" +
            "  bounty start <set> | bounty state | bounty exit\n" +
            "  mode <golf|walk> | shoot [power 0..1] [angle] [ShotType] | shoot next | shoot again\n" +
            "  locks [text] | dialogue [state|next|triggers [text]] | remotes | player";

        public const double MaxWaitSeconds = 600;
        public const float DefaultKeyHold = 0.1f;
        public const float MaxKeyHold = 30f;
        private const string MaxWaitText = "600";

        private readonly BotHub _hub;
        private readonly IHarnessGame _game;
        private readonly Func<double> _clock;
        private readonly Func<DateTime> _utc;
        private readonly IHarnessControl _control;
        private readonly IHarnessPlay _play;
        private double _hold;
        private SaveFileSnapshot _baseline;
        private bool _baselineAsHost = true;
        private bool _wasInSession;

        public HarnessCommands(BotHub hub, IHarnessGame game, Func<double> clock, Func<DateTime> utcClock = null,
            IHarnessControl control = null, IHarnessPlay play = null)
        {
            _control = control;
            _play = play;
            _hub = hub ?? throw new ArgumentNullException(nameof(hub));
            _game = game ?? throw new ArgumentNullException(nameof(game));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _utc = utcClock ?? (() => DateTime.UtcNow);
            _hub.Message += (bot, text) => Emit(BotLabel(bot) + ": " + text);
        }

        /// <summary>The save files <c>savecheck</c> hashes: the protected ones (5.5) and the player's settings (#31).</summary>
        public static IReadOnlyList<string> SaveFileNames
        {
            get
            {
                var names = new List<string>(LocalSaveFiles.ProtectedNames) { SettingsFile };
                return names;
            }
        }

        public SaveFileSnapshot Baseline => _baseline;

        /// <summary>Text for the results file that is not the answer to a command (bot events, baselines).</summary>
        public event Action<string> Output;

        /// <summary>
        /// Seconds the commands after the last one must wait (<c>wait</c>; infinity after <c>quit</c>), then cleared.
        /// Zero: none.
        /// </summary>
        public double TakeHold()
        {
            double hold = _hold;
            _hold = 0;
            return hold;
        }

        /// <summary>Takes the first save baseline ("harness start").</summary>
        public void Start()
        {
            _wasInSession = SafeInSession();
            TakeBaseline(_wasInSession ? "harness start (in a session)" : "harness start", _game.IsHosting || !_wasInSession);
        }

        /// <summary>Every frame: the bots, and a fresh save baseline when a session starts.</summary>
        public void Tick()
        {
            bool inSession = SafeInSession();
            if (inSession && !_wasInSession)
            {
                bool host = _game.IsHosting;
                TakeBaseline(host ? "session start (host)" : "session start (client)", host);
                Emit("savecheck baseline taken at " + (host ? "session start (host)" : "session start (client)"));
            }
            _wasInSession = inSession;

            bool hosting = _game.IsHosting;
            bool hasPose = false;
            NetVector3 anchor = NetVector3.Zero;
            if (_hub.Bots.Count > 0)
            {
                try
                {
                    hasPose = _game.TryGetHostPose(out anchor);
                }
                catch (Exception e)
                {
                    hasPose = false;
                    NetLog.Warn($"[TestHarness] Reading the host pose failed: {e.Message}");
                }
            }
            _hub.Update(_clock(), hosting, anchor, hasPose);
        }

        /// <summary>Runs one command; never throws (errors come back as text).</summary>
        public string Execute(HarnessCommand command)
        {
            if (command == null)
                return string.Empty;
            try
            {
                switch (command.Name)
                {
                    case "help": return Help;
                    case "host": return HostCommand();
                    case "leave": return LeaveCommand();
                    case "status": return Status();
                    case "dump": return Dump();
                    case "bot": return Bot(command);
                    case "wind": return Wind();
                    case "savecheck": return SaveCheck(command.ArgLower(1) == "reset");
                    case "log": return LogLevelCommand(command.ArgLower(1));
                    case "latency": return Latency(command.Arg(1));
                    case "check": return _game.RunCheck(command.ArgLower(1) ?? "all");
                    case "list": return ListCommand(command.ArgLower(1));
                    case "wait": return Wait(command.Arg(1));
                    case "screenshot": return Screenshot(command.Rest(1));
                    case "key": return Key(command);
                    case "buttons": return Buttons();
                    case "click": return Click(command);
                    case "uitree": return UiTree(command);
                    case "scene": return _control != null ? _control.DescribeScene() : NoControl;
                    case "videos": return _control != null ? _control.DescribeVideos() : NoControl;
                    case "mpwindow": return LobbyWindow(command.ArgLower(1));
                    case "pause": return Pause(command.ArgLower(1));
                    case "quit": return Quit();
                    case "find": return Find(command);
                    case "goto": return Goto(command);
                    case "interact": return Interact(command);
                    case "bounty": return Bounty(command);
                    case "mode": return Mode(command.ArgLower(1));
                    case "shoot": return Shoot(command);
                    case "locks": return _play != null ? _play.DescribeLocks(command.Rest(1)) : NoPlay;
                    case "dialogue": return Dialogue(command);
                    case "remotes": return _play != null ? _play.DescribeRemotes() : NoPlay;
                    case "player": return _play != null ? _play.DescribePlayer() : NoPlay;
                    default: return "unknown command '" + command.Words[0] + "'. " + Help;
                }
            }
            catch (Exception e)
            {
                NetLog.Error($"[TestHarness] Command '{command.Line}' failed: {e}");
                return "ERROR: " + e.Message;
            }
        }

        // ---- Session ------------------------------------------------------------------------------------

        private string HostCommand()
        {
            if (_game.IsHosting)
                return "already hosting (lobby " + _game.LobbyId + ")";
            return _game.Host(out string message) ? "hosting requested: " + message : "cannot host: " + message;
        }

        private string LeaveCommand()
        {
            int bots = _hub.Bots.Count;
            _game.Leave();
            return "left the session" + (bots > 0 ? $" ({bots} bot(s) will see the host leave)" : string.Empty);
        }

        private string Status()
        {
            var sb = new StringBuilder();
            sb.Append("service ").Append(_game.ServiceState);
            sb.Append(_game.IsHosting ? ", hosting" : _game.InSession ? ", client" : string.Empty);
            if (_game.LobbyId != 0)
                sb.Append(", lobby ").Append(_game.LobbyId);
            var players = _game.Players;
            sb.Append(", ").Append(players.Count).Append(" player(s)");
            if (!string.IsNullOrEmpty(_game.StatusMessage))
                sb.Append(", \"").Append(_game.StatusMessage).Append('"');
            sb.Append("; bots: ");
            if (!_hub.IsWired)
                sb.Append("NOT WIRED (the Steam service must call TestHarness.WrapTransport) ");
            if (_hub.Bots.Count == 0)
                sb.Append("none");
            foreach (var bot in _hub.Bots)
            {
                sb.Append('\n').Append("  ").Append(BotLabel(bot)).Append(' ').Append(bot.State);
                if (bot.IsReady)
                {
                    sb.Append(", host in ").Append(bot.HostScene).Append(", ").Append(bot.Save.Facts.Count).Append(" facts");
                    sb.Append(bot.Avatar.HasAnchor ? (bot.Avatar.Walking ? ", walking" : ", standing") : ", hidden (no host pose)");
                    if (bot.Avatar.Golfing)
                        sb.Append(", golfing");
                }
            }
            return sb.ToString();
        }

        private string Dump()
        {
            var json = new JsonBuilder();
            json.BeginObject();
            json.Value("time", _utc().ToString("o", CultureInfo.InvariantCulture));

            json.BeginObject("harness");
            json.Value("wired", _hub.IsWired);
            json.Value("latencyMs", _hub.LatencyMs);
            if (_hub.Composite != null)
            {
                json.Value("loopbackEnabled", _hub.Composite.LoopbackEnabled);
                json.Value("loopbackSent", _hub.Composite.LoopbackSent);
                json.Value("loopbackReceived", _hub.Composite.LoopbackReceived);
                json.Value("loopbackDropped", _hub.Composite.LoopbackDropped);
            }
            json.EndObject();

            DumpSession(json);
            DumpBots(json);

            json.BeginObject("world");
            Guarded(json, () => _game.DescribeWorld(json));
            json.EndObject();

            DumpFacts(json, "facts", _game.SessionFacts);

            json.BeginObject("saveGuard");
            Guarded(json, () => _game.DescribeSaveGuard(json));
            json.EndObject();

            var report = CompareSaves(out string label);
            json.BeginObject("saves");
            json.Value("baseline", label);
            if (report != null)
            {
                json.Value("violation", report.HasViolation);
                json.Value("summary", report.Describe());
            }
            json.EndObject();

            json.EndObject();
            return json.ToString();
        }

        private void DumpSession(JsonBuilder json)
        {
            json.BeginObject("session");
            json.Value("state", _game.ServiceState);
            json.Value("role", _game.IsHosting ? "host" : _game.InSession ? "client" : "none");
            json.Id("localId", _game.LocalId.Value);
            json.Id("hostId", _game.HostId.Value);
            json.Id("lobby", _game.LobbyId);
            json.Value("status", _game.StatusMessage);
            json.BeginArray("players");
            foreach (var player in _game.Players)
            {
                json.BeginObject();
                json.Id("id", player.Id.Value);
                json.Value("name", player.DisplayName);
                json.Value("index", player.PlayerIndex);
                json.Value("pingMs", player.PingMs);
                json.Value("isHost", player.IsHost);
                json.Value("isLocal", player.IsLocal);
                json.Value("isBot", BotIds.IsBot(player.Id));
                json.Value("ready", player.IsLocal || SafeReady(player.Id));
                json.EndObject();
            }
            json.EndArray();
            json.EndObject();
        }

        private void DumpBots(JsonBuilder json)
        {
            json.BeginArray("bots");
            foreach (var bot in _hub.Bots)
            {
                json.BeginObject();
                json.Value("index", bot.Index);
                json.Value("name", bot.Name);
                json.Id("id", bot.Id.Value);
                json.Value("state", bot.State.ToString());
                json.Value("ended", bot.EndInfo.HasValue ? bot.EndInfo.Value.ToString() : null);
                json.Value("hostScene", bot.HostScene.ToString());
                json.Value("sceneEpoch", bot.SceneEpoch);
                json.Value("mode", bot.Mode == SessionSaveBundle.ModeJustGolf ? "JustGolf" : "Story");
                json.Value("sceneNotices", bot.SceneNotices);

                var save = bot.Save;
                json.BeginObject("save");
                json.Value("bundle", save.HasBundle);
                json.Value("snapshots", save.SnapshotsApplied);
                json.Value("deltas", save.DeltasApplied);
                json.Value("backups", save.Backups);
                json.Value("restores", save.Restores);
                json.Value("suppressDiskWrites", save.SuppressDiskWrites);
                json.Value("diskWrites", save.DiskWrites);
                if (save.Bundle != null)
                {
                    json.Value("runJsonBytes", save.Bundle.RunJson.Length);
                    json.Value("unlocksJsonBytes", save.Bundle.UnlocksJson.Length);
                    json.Value("scoreCardJsonBytes", save.Bundle.ScoreCardJson.Length);
                }
                json.Value("factCount", save.Facts.Count);
                json.Value("factCrc", save.Facts.ComputeCrc().ToString("X8", CultureInfo.InvariantCulture));
                json.EndObject();

                json.BeginObject("claims");
                json.Value("pending", bot.Claims.PendingCount);
                json.Value("accepted", bot.Claims.Accepted);
                json.Value("rejected", bot.Claims.Rejected);
                json.Value("unanswered", bot.Claims.Unanswered);
                json.Value("grants", bot.GrantsReceived);
                json.Value("lastGrantedClaim", bot.LastGrantedClaim);
                json.EndObject();

                var avatar = bot.Avatar;
                json.BeginObject("avatar");
                json.Value("visible", avatar.HasAnchor);
                json.Value("walking", avatar.Walking);
                json.Value("golfing", avatar.Golfing);
                json.Value("stroke", avatar.Stroke);
                json.Value("position", avatar.Position.ToString());
                json.Value("ballVisible", avatar.BallVisible);
                json.Value("ballInFlight", avatar.BallInFlight);
                json.Value("ball", avatar.BallPosition.ToString());
                json.EndObject();

                json.BeginObject("traffic");
                json.Value("shotsSent", bot.ShotsSent);
                json.Value("restsSent", bot.RestsSent);
                json.Value("infosSent", bot.InfosSent);
                json.Value("presenceReceived", bot.PresenceEventsReceived);
                json.Value("worldReceived", bot.WorldEventsReceived);
                json.Value("storyReceived", bot.StoryEventsReceived);
                json.Value("windMessages", bot.WindMessages);
                json.Value("digestMatches", bot.DigestMatches);
                json.Value("digestMismatches", bot.DigestMismatches);
                json.EndObject();
                json.EndObject();
            }
            json.EndArray();
        }

        private static void DumpFacts(JsonBuilder json, string name, FactTable facts)
        {
            json.BeginObject(name);
            if (facts == null)
            {
                json.Value("available", false);
                json.EndObject();
                return;
            }
            json.Value("count", facts.Count);
            json.Value("crc", facts.ComputeCrc().ToString("X8", CultureInfo.InvariantCulture));
            json.BeginArray("entries");
            foreach (var entry in facts.GetEntries())
            {
                json.BeginObject();
                json.Value("kind", entry.Kind);
                json.Value("key", entry.Key);
                json.Value("value", FactText.Format(entry.Kind, entry.Value));
                json.EndObject();
            }
            json.EndArray();
            json.EndObject();
        }

        private string Wind()
        {
            var sb = new StringBuilder("host: ");
            try
            {
                sb.Append(_game.DescribeWind());
            }
            catch (Exception e)
            {
                sb.Append("unreadable (").Append(e.Message).Append(')');
            }
            double now = _clock();
            foreach (var bot in _hub.Bots)
            {
                sb.Append("\n  ").Append(BotLabel(bot)).Append(": ");
                if (!bot.HasWind)
                {
                    sb.Append("no ngg.pr.wind received");
                    continue;
                }
                var w = bot.LastWind;
                sb.Append(string.Format(CultureInfo.InvariantCulture,
                    "current ({0:0.###}, {1:0.###}) desired ({2:0.###}, {3:0.###}) hostFree {4}, {5} messages, last {6:0.0} s ago",
                    w.CurrentX, w.CurrentY, w.DesiredX, w.DesiredY, w.HostFree, bot.WindMessages, now - bot.LastWindAt));
            }
            return sb.ToString();
        }

        // ---- savecheck ----------------------------------------------------------------------------------

        private string SaveCheck(bool reset)
        {
            if (reset)
            {
                TakeBaseline("reset by command", _game.IsHosting || !SafeInSession());
                return "savecheck baseline reset: " + _baseline.Count + " files hashed in " + _game.SaveDirectory;
            }
            var report = CompareSaves(out string label);
            if (report == null)
                return "no baseline: " + label;
            string verdict = report.HasViolation
                ? "VIOLATION: a protected save file changed while this game was a client"
                : report.ChangedCount == 0 ? "nothing changed" : "changes are allowed for this role";
            return $"since {label} ({_baseline.TakenUtc:HH:mm:ss} UTC): {verdict}. {report.Describe()}";
        }

        private SaveFileReport CompareSaves(out string label)
        {
            if (_baseline == null)
            {
                label = "none taken";
                return null;
            }
            label = _baseline.Label;
            var now = TakeSnapshot("now");
            return SaveFileReport.Compare(_baseline, now, new[] { SettingsFile, RebindsName }, _baselineAsHost);
        }

        private void TakeBaseline(string label, bool hostWritesAllowed)
        {
            _baseline = TakeSnapshot(label);
            _baselineAsHost = hostWritesAllowed;
        }

        private SaveFileSnapshot TakeSnapshot(string label)
        {
            string directory = SafeSaveDirectory();
            var snapshot = SaveFileSnapshot.Take(directory, SaveFileNames, _utc(), label);
            string rebinds = null;
            try
            {
                rebinds = _game.ReadPlayerPref(RebindsPref);
            }
            catch (Exception e)
            {
                NetLog.Warn($"[TestHarness] Reading PlayerPrefs '{RebindsPref}' failed: {e.Message}");
            }
            snapshot.Add(SaveFileSnapshot.HashValue(RebindsName, rebinds));
            return snapshot;
        }

        // ---- Settings -----------------------------------------------------------------------------------

        private static string LogLevelCommand(string level)
        {
            switch (level)
            {
                case "debug": NetLog.MinLevel = LogLevel.Debug; break;
                case "info": NetLog.MinLevel = LogLevel.Info; break;
                case "warn":
                case "warning": NetLog.MinLevel = LogLevel.Warning; break;
                case "error": NetLog.MinLevel = LogLevel.Error; break;
                default: return "usage: log <debug|info|warning|error> (now " + NetLog.MinLevel + ")";
            }
            return "log level " + NetLog.MinLevel + " (until the config file changes)";
        }

        private string Latency(string text)
        {
            if (!HarnessCommand.TryParseFloat(text, out float ms) || ms < 0 || ms > 2000)
                return "usage: latency <0..2000 ms> (now " + _hub.LatencyMs.ToString("0", CultureInfo.InvariantCulture) + ")";
            _hub.LatencyMs = ms;
            return "bot link latency " + ms.ToString("0", CultureInfo.InvariantCulture) + " ms one way";
        }

        private string ListCommand(string what)
        {
            if (string.IsNullOrEmpty(what))
                return "usage: list <sets|bounties|pickups|doors|gates|saveables>";
            var items = _game.List(what, out string error);
            if (items == null)
                return "cannot list " + what + ": " + error;
            return items.Count + " " + what + ":" + (items.Count == 0 ? " (none)" : "\n  " + string.Join("\n  ", items));
        }

        // ---- Driving the game ---------------------------------------------------------------------------

        private const string NoControl = "not available: the harness has no game control here";

        private string Wait(string text)
        {
            if (!HarnessCommand.TryParseFloat(text, out float seconds) || seconds <= 0 || seconds > MaxWaitSeconds)
                return "usage: wait <seconds, 0.." + MaxWaitText + ">";
            _hold = seconds;
            return "waiting " + seconds.ToString("0.###", CultureInfo.InvariantCulture) + " s before the next command";
        }

        private string Screenshot(string name)
        {
            if (_control == null)
                return NoControl;
            string fileName = ScreenshotNames.FileName(_utc().ToLocalTime(), name);
            if (!_control.Screenshot(fileName, out string path, out string error))
                return "cannot take a screenshot: " + error;
            return "screenshot " + path + " (the file is written at the end of this frame; a 'screenshot written' line follows)";
        }

        private string Key(HarnessCommand command)
        {
            if (_control == null)
                return NoControl;
            // "LeftCtrl+W": keys held together (crouch-walk); each part is normalized on its own.
            string key = null;
            string arg = command.Arg(1);
            if (!string.IsNullOrEmpty(arg))
            {
                var parts = arg.Split('+');
                for (int i = 0; i < parts.Length; i++)
                {
                    parts[i] = KeyNames.Normalize(parts[i]);
                    if (parts[i] == null)
                    {
                        parts = null;
                        break;
                    }
                }
                if (parts != null)
                    key = string.Join("+", parts);
            }
            float hold = DefaultKeyHold;
            if (key == null || (command.Count > 2 && (!HarnessCommand.TryParseFloat(command.Arg(2), out hold) || hold < 0 || hold > MaxKeyHold)))
                return "usage: key <KeyName[+KeyName...], e.g. Escape, Tab, F7, Enter, Space, A, 1, LeftCtrl+W> [hold seconds 0.." +
                       MaxKeyHold.ToString("0", CultureInfo.InvariantCulture) + ", default " +
                       DefaultKeyHold.ToString("0.0", CultureInfo.InvariantCulture) + "]";
            return _control.PressKey(key, hold, out string message) ? message : "cannot press " + key + ": " + message;
        }

        private string Buttons()
        {
            if (_control == null)
                return NoControl;
            return UiMatcher.Describe(_control.FindClickables());
        }

        private string UiTree(HarnessCommand command)
        {
            if (_control == null)
                return NoControl;
            string filter = command.Arg(1) ?? string.Empty;
            int depth = 64;
            if (command.Count > 2 && (!HarnessCommand.TryParseInt(command.Arg(2), out depth) || depth < 0))
                return "usage: uitree [filter] [depth]";
            if (filter == "*")
                filter = string.Empty;
            return _control.DumpUiTree(filter, depth);
        }

        private string Click(HarnessCommand command)
        {
            if (_control == null)
                return NoControl;
            int last = command.Count - 1;
            int pick = 0;
            string lastWord = command.Arg(last);
            if (last >= 2 && lastWord.Length > 1 && lastWord[0] == '@')
            {
                if (!HarnessCommand.TryParseInt(lastWord.Substring(1), out pick) || pick < 1)
                    return "usage: click <button text or path part> [@n]";
                last--;
            }
            var words = new List<string>();
            for (int i = 1; i <= last; i++)
                words.Add(command.Arg(i));
            string query = string.Join(" ", words);
            if (query.Length == 0)
                return "usage: click <button text or path part> [@n]";
            var elements = _control.FindClickables();
            var match = UiMatcher.Match(elements, query, pick);
            if (match.Chosen == null)
            {
                if (match.Candidates.Count == 0)
                    return "nothing matches '" + query + "'; nothing clicked. " + UiMatcher.Describe(elements);
                if (pick > 0)
                    return $"only {match.Candidates.Count} element(s) match '{query}' ({match.How}); nothing clicked. " +
                           UiMatcher.Describe(match.Candidates);
                return $"ambiguous: {match.Candidates.Count} elements match '{query}' ({match.How}); nothing clicked. " +
                       "Add @<n> to pick one. " + UiMatcher.Describe(match.Candidates);
            }
            return _control.Click(match.Chosen, out string error)
                ? "clicked " + match.Chosen + " (" + match.How + ")"
                : "could not click " + match.Chosen + ": " + error;
        }

        private string Pause(string what)
        {
            if (_control == null)
                return NoControl;
            bool open;
            switch (what)
            {
                case "open": case "": open = true; break;
                case "close": case "exit": case "resume": open = false; break;
                default: return "usage: pause [open|close]";
            }
            return _control.SetPauseMenu(open, out string message) ? message : "cannot " + (open ? "open" : "close") + " the pause menu: " + message;
        }

        private string LobbyWindow(string what)
        {
            if (_control == null)
                return NoControl;
            bool? visible;
            switch (what)
            {
                case "open": case "show": case "on": visible = true; break;
                case "close": case "hide": case "off": visible = false; break;
                case "toggle": visible = null; break;
                default: return "usage: mpwindow <open|close|toggle>";
            }
            if (!_control.SetLobbyWindow(visible, out bool now, out string error))
                return "cannot change the multiplayer window: " + error;
            return "multiplayer window " + (now ? "open" : "closed");
        }

        private string Quit()
        {
            if (_control == null)
                return NoControl;
            _hold = double.PositiveInfinity;
            return _control.Quit() + "; the following commands are not run";
        }

        // ---- Playing in this copy -----------------------------------------------------------------------

        private const string NoPlay = "not available: the harness cannot play the game here";

        private static List<string> WordsFrom(HarnessCommand command, int index)
        {
            var words = new List<string>();
            for (int i = index; i < command.Count; i++)
                words.Add(command.Arg(i));
            return words;
        }

        private string Find(HarnessCommand command)
        {
            if (_play == null)
                return NoPlay;
            string category = PlayArgs.Category(command.Arg(1));
            if (category == null)
                return "usage: find <" + string.Join("|", PlayArgs.Categories) + "> [text]";
            var objects = _play.FindObjects(category, out string error);
            if (objects == null)
                return "cannot list " + category + ": " + error;
            return ObjectMatcher.Describe(ObjectMatcher.Filter(objects, command.Rest(2)));
        }

        /// <summary>The object a goto / interact names, or null with the answer explaining why.</summary>
        private SceneObject Resolve(TargetArgs args, IReadOnlyList<string> categories, string verb, out string answer)
        {
            answer = null;
            var objects = new List<SceneObject>();
            var errors = new List<string>();
            foreach (var category in categories)
            {
                var found = _play.FindObjects(category, out string error);
                if (found == null)
                    errors.Add(category + ": " + error);
                else
                    objects.AddRange(found);
            }
            if (objects.Count == 0 && errors.Count > 0)
            {
                answer = "cannot " + verb + ": " + string.Join("; ", errors);
                return null;
            }
            var match = ObjectMatcher.Match(objects, args.Query, args.Pick);
            if (match.Chosen != null)
                return match.Chosen;
            string what = args.Query.Length > 0 ? "'" + args.Query + "'" : string.Join("/", categories);
            if (match.Candidates.Count == 0)
                answer = "nothing matches " + what + " (" + string.Join(", ", categories) + "); nothing done. 'find " +
                         categories[0] + " <text>' lists them";
            else if (args.Pick > 0)
                answer = $"only {match.Candidates.Count} object(s) match {what} ({match.How}); nothing done. " +
                         ObjectMatcher.Describe(match.Candidates);
            else
                answer = $"ambiguous: {match.Candidates.Count} objects match {what} ({match.How}); nothing done. Add @<n> to pick one. " +
                         ObjectMatcher.Describe(match.Candidates);
            return null;
        }

        private string Goto(HarnessCommand command)
        {
            if (_play == null)
                return NoPlay;
            if (!PlayArgs.TryParseTarget(WordsFrom(command, 1), true, PlayArgs.AnyObject, out var args, out string error))
                return "usage: goto [" + string.Join("|", PlayArgs.Categories) + "] <name, id or path part> [@n] | goto <x> <y> <z> [yaw] (" + error + ")";
            if (args.HasPosition)
                return _play.GotoPosition(args.Position, args.Yaw, out string moved) ? moved : "cannot go there: " + moved;
            var target = Resolve(args, new[] { args.Category }, "go there", out string answer);
            if (target == null)
                return answer;
            return _play.Goto(target, out string message) ? message : "cannot go to " + target + ": " + message;
        }

        private string Interact(HarnessCommand command)
        {
            if (_play == null)
                return NoPlay;
            if (!PlayArgs.TryParseTarget(WordsFrom(command, 1), false, null, out var args, out string error))
                return "usage: interact [" + string.Join("|", PlayArgs.Categories) + "] <name, id or path part> [@n] (" + error + ")";
            var categories = args.Category != null ? new[] { args.Category } : PlayArgs.Usable;
            var target = Resolve(args, categories, "interact", out string answer);
            if (target == null)
                return answer;
            return _play.Interact(target, out string message) ? message : "could not use " + target + ": " + message;
        }

        private string Bounty(HarnessCommand command)
        {
            if (_play == null)
                return NoPlay;
            switch (command.ArgLower(1))
            {
                case null:
                case "state":
                case "status":
                    return _play.DescribeBounty();
                case "start":
                {
                    string set = command.Rest(2);
                    if (set.Length == 0)
                        return "usage: bounty start <clean set name> ('list bounties' or 'find pad' lists them)";
                    return _play.StartBounty(set, out string message) ? message : "cannot start bounty " + set + ": " + message;
                }
                case "exit":
                case "quit":
                case "leave":
                    return _play.ExitBounty(out string exited) ? exited : "cannot leave the bounty: " + exited;
                default:
                    return "usage: bounty start <set> | bounty state | bounty exit";
            }
        }

        private string Mode(string word)
        {
            if (_play == null)
                return NoPlay;
            if (!PlayArgs.TryParseMode(word, out bool golf))
                return "usage: mode <golf|walk>";
            return _play.SetMode(golf, out string message) ? message : "cannot switch to " + (golf ? "golf" : "walking") + ": " + message;
        }

        private string Shoot(HarnessCommand command)
        {
            if (_play == null)
                return NoPlay;
            string sub = command.ArgLower(1);
            if (sub == "next" || sub == "again")
            {
                bool resume = sub == "next";
                return _play.RestartShot(resume, out string restarted) ? restarted : "cannot restart the shot: " + restarted;
            }
            if (!PlayArgs.TryParseShot(WordsFrom(command, 1), out var shot, out string error))
                return "usage: shoot [power 0..1, default " + ShotRequest.DefaultPower.ToString("0.0", CultureInfo.InvariantCulture) +
                       "] [angle] [ShotType, default Good] | shoot next | shoot again (" + error + ")";
            return _play.Shoot(shot, out string message) ? message : "cannot shoot: " + message;
        }

        private string Dialogue(HarnessCommand command)
        {
            if (_play == null)
                return NoPlay;
            switch (command.ArgLower(1))
            {
                case null:
                case "state":
                case "status":
                    return _play.DescribeDialogue();
                case "next":
                    return _play.DialogueNext(out string message) ? message : "cannot advance the dialogue: " + message;
                case "triggers":
                {
                    var triggers = _play.FindObjects(PlayArgs.Trigger, out string error);
                    if (triggers == null)
                        return "cannot list dialogue triggers: " + error;
                    return ObjectMatcher.Describe(ObjectMatcher.Filter(triggers, command.Rest(2)));
                }
                default:
                    return "usage: dialogue [state|next|triggers [text]]";
            }
        }

        // ---- Bots ---------------------------------------------------------------------------------------

        private string Bot(HarnessCommand command)
        {
            string sub = command.ArgLower(1);
            var args = new List<string>();
            string selector = null;
            for (int i = 2; i < command.Count; i++)
            {
                string word = command.Arg(i);
                if (word.Length > 1 && word[0] == '@')
                    selector = word.Substring(1);
                else
                    args.Add(word);
            }
            switch (sub)
            {
                case "add": return BotAdd(args.Count > 0 ? string.Join(" ", args) : null);
                case "remove": return BotRemove(selector, false);
                case "leave": return BotRemove(selector, true);
                case "walk": return BotWalk(args, selector);
                case "shoot": return BotShoot(args, selector);
                case "claim": return BotClaim(args, selector);
                default: return "usage: bot <add|remove|leave|walk|shoot|claim> ...";
            }
        }

        private string BotAdd(string name)
        {
            if (!_game.IsHosting)
                return "bots join a hosted session only: run 'host' first";
            if (!_hub.IsWired)
                return "the bots' transport is not wired: SteamMultiplayerService must pass its transport through TestHarness.WrapTransport";
            var options = _game.BotTemplate() ?? new FakePeerOptions();
            options.DisplayName = name;
            var bot = _hub.Add(_game.LocalId, options);
            bot.Avatar.GroundHeight = _game.GroundHeight;
            return BotLabel(bot) + " is joining as " + bot.Id;
        }

        private string BotRemove(string selector, bool graceful)
        {
            var bot = selector != null ? _hub.Find(selector) : _hub.Newest;
            if (bot == null)
                return selector != null ? "no bot '" + selector + "'" : "no bots";
            string label = BotLabel(bot);
            _hub.Remove(bot, graceful);
            return graceful
                ? label + " left the session"
                : label + " vanished without a goodbye; the host should drop it after its timeout";
        }

        private string BotWalk(List<string> args, string selector)
        {
            if (args.Count < 1 || !HarnessCommand.TryParseSwitch(args[0], out bool on))
                return "usage: bot walk <on|off> [@bot]";
            var bots = Select(selector, true, out string error);
            if (bots == null)
                return error;
            foreach (var bot in bots)
                bot.Avatar.Walking = on;
            return Names(bots) + (on ? " walking" : " standing");
        }

        private string BotShoot(List<string> args, string selector)
        {
            float power = 0.7f;
            if (args.Count > 0 && (!HarnessCommand.TryParseFloat(args[0], out power) || power <= 0f))
                return "usage: bot shoot [power 0..1, a fraction of the club's full swing] [@bot]";
            var bots = Select(selector, true, out string error);
            if (bots == null)
                return error;
            var sb = new StringBuilder();
            foreach (var bot in bots)
            {
                if (sb.Length > 0)
                    sb.Append("; ");
                bool ok = bot.Shoot(power);
                sb.Append(BotLabel(bot)).Append(ok
                    ? " takes its stance (power " + Math.Min(power, BotAvatar.MaxPower).ToString("0.00", CultureInfo.InvariantCulture) + ")"
                    : bot.IsReady ? " cannot shoot now (shot in progress or no host pose)" : " is not synced yet");
            }
            return sb.ToString();
        }

        private string BotClaim(List<string> args, string selector)
        {
            if (args.Count < 1)
                return "usage: bot claim <bounty|challenge|door|gate|sliding|world|pickup|bank|rb|ri|obj|story|storyhost|fact> ...";
            var bots = Select(selector, false, out string error);
            if (bots == null)
                return error;
            var bot = bots[0];
            if (!bot.IsReady)
                return BotLabel(bot) + " is not synced with the host yet";
            string kind = args[0].ToLowerInvariant();
            string a1 = args.Count > 1 ? args[1] : null;
            string a2 = args.Count > 2 ? args[2] : null;
            string label = BotLabel(bot);
            string claimError;
            switch (kind)
            {
                case "bounty":
                {
                    if (string.IsNullOrEmpty(a1))
                        return "usage: bot claim bounty <clean set name> [easy]";
                    string warning = SetWarning(a1, true);
                    bool easy = string.Equals(a2, "easy", StringComparison.OrdinalIgnoreCase);
                    return bot.ClaimBounty(a1, easy, out claimError)
                        ? $"{label} claims bounty {a1}{(easy ? " on easy" : string.Empty)}{warning}; the verdict follows"
                        : $"{label} cannot claim: {claimError}";
                }
                case "challenge":
                {
                    string set = null;
                    int? index = null;
                    if (a1 != null && HarnessCommand.TryParseInt(a1, out int only))
                        index = only;
                    else if (a1 != null)
                    {
                        set = a1;
                        if (a2 == null || !HarnessCommand.TryParseInt(a2, out int at))
                            return "usage: bot claim challenge [index] | challenge <set> <index>";
                        index = at;
                    }
                    string warning = set != null ? SetWarning(set, false) : string.Empty;
                    return bot.ClaimTrack(set, index, out claimError)
                        ? $"{label} claims the main track {Describe(bot, FactKinds.Track, FactKinds.TrackKey)} -> " +
                          $"{(set ?? "same set")} #{(index.HasValue ? index.Value.ToString(CultureInfo.InvariantCulture) : "+1")}{warning}; the verdict follows"
                        : $"{label} cannot claim: {claimError}";
                }
                case "door":
                    if (string.IsNullOrEmpty(a1))
                        return "usage: bot claim door <id>";
                    return SentWorld(bot, WorldOp.OpenDoor, a1, false);
                case "gate":
                case "sliding":
                {
                    if (string.IsNullOrEmpty(a1) || a2 == null || !TryOpenClose(a2, out bool open))
                        return "usage: bot claim " + kind + " <id> <open|close>";
                    return SentWorld(bot, kind == "gate" ? WorldOp.SetGates : WorldOp.SlidingDoor, a1, open);
                }
                case "world":
                {
                    if (!TryParseWorldOp(a1, out var op))
                        return "usage: bot claim world <op 1..11 or name> [id] [on|off]";
                    string id = WorldOps.HasId(op) ? a2 : string.Empty;
                    if (WorldOps.HasId(op) && string.IsNullOrEmpty(id))
                        return "world op " + op + " needs an id";
                    string flagText = WorldOps.HasId(op) ? (args.Count > 3 ? args[3] : null) : a2;
                    bool flag = flagText != null && HarnessCommand.TryParseSwitch(flagText, out bool f) && f;
                    return SentWorld(bot, op, id, flag);
                }
                case "pickup":
                    return ClaimPickup(bot, args);
                case "bank":
                {
                    if (!HarnessCommand.TryParseInt(a1, out int delta))
                        return "usage: bot claim bank <delta> [claimId]";
                    uint claimId = 0;
                    if (a2 != null && (!uint.TryParse(a2, NumberStyles.None, CultureInfo.InvariantCulture, out claimId) || claimId == 0))
                        return "usage: bot claim bank <delta> [claimId]";
                    uint used = claimId != 0 ? claimId : bot.LastGrantedClaim;
                    return bot.SendBank(claimId, delta)
                        ? $"{label} reports a money change of {delta} under claim {used}" +
                          (used == 0 ? " (no grant yet: the host must ignore it)" : string.Empty) + "; check the host's bank and log"
                        : $"{label} could not send the bank report";
                }
                case "rb":
                {
                    if (string.IsNullOrEmpty(a1) || !HarnessCommand.TryParseSwitch(a2, out bool value))
                        return "usage: bot claim rb <RunSave bool field> <true|false>";
                    string warning = SharedRunFields.IsSharedBool(a1) ? string.Empty : " (not a shared field: the host must refuse it)";
                    return bot.ClaimRunBool(a1, value, out claimError)
                        ? $"{label} claims rb/{a1} = {value}{warning}; the verdict follows"
                        : $"{label} cannot claim: {claimError}";
                }
                case "ri":
                {
                    if (string.IsNullOrEmpty(a1) || !HarnessCommand.TryParseInt(a2, out int value))
                        return "usage: bot claim ri <RunSave int field> <value>";
                    string warning = SharedRunFields.IsSharedInt(a1) ? string.Empty : " (not a shared field: the host must refuse it)";
                    return bot.ClaimRunInt(a1, value, out claimError)
                        ? $"{label} claims ri/{a1} = {value}{warning}; the verdict follows"
                        : $"{label} cannot claim: {claimError}";
                }
                case "obj":
                {
                    if (string.IsNullOrEmpty(a1) || a2 == null)
                        return "usage: bot claim obj <SaveableObject m_id> <active|inactive>";
                    bool active = !string.Equals(a2, "inactive", StringComparison.OrdinalIgnoreCase)
                        && (string.Equals(a2, "active", StringComparison.OrdinalIgnoreCase) || (HarnessCommand.TryParseSwitch(a2, out bool on) && on));
                    return bot.ClaimObj(a1, active, out claimError)
                        ? $"{label} claims obj/{a1} {(active ? "active" : "inactive")}; the verdict follows"
                        : $"{label} cannot claim: {claimError}";
                }
                case "story":
                case "storyhost":
                {
                    if (string.IsNullOrEmpty(a1))
                        return "usage: bot claim " + kind + " <DialogueManager method>";
                    bool hostExecuted = kind == "storyhost";
                    var cls = StoryMethods.Classify(a1);
                    string expected = hostExecuted
                        ? (cls == StoryClass.HostExecuted ? string.Empty : " (not host-executed: the host must refuse it)")
                        : (cls == StoryClass.Replicate ? string.Empty : " (not in the replicate list: the host must refuse it)");
                    return bot.SendStory(a1, hostExecuted)
                        ? $"{label} sends {(hostExecuted ? ProgressionRpc.StoryHost : ProgressionRpc.Story)} {a1}{expected}"
                        : $"{label} could not send";
                }
                case "fact":
                {
                    if (string.IsNullOrEmpty(a1) || a2 == null)
                        return "usage: bot claim fact <kind> <key> <hex value>";
                    string key = a2 == "-" ? string.Empty : a2;
                    if (!TryParseHex(args.Count > 3 ? args[3] : string.Empty, out byte[] value))
                        return "the value must be hex (for example 01)";
                    return bot.ClaimFact(a1, key, value, out claimError)
                        ? $"{label} claims {a1}/{key} = {FactText.Format(a1, value)}; the verdict follows"
                        : $"{label} cannot claim: {claimError}";
                }
                default:
                    return "unknown claim kind '" + args[0] + "'";
            }
        }

        private string ClaimPickup(FakePeer bot, List<string> args)
        {
            if (args.Count < 2)
                return "usage: bot claim pickup <interactId> [<InteractionType> <cashValue>]";
            string id = args[1];
            PickupRequest request;
            string note;
            if (args.Count >= 3)
            {
                if (!BotInteractions.TryParse(args[2], out byte type))
                    return "unknown InteractionType '" + args[2] + "'";
                int cash = 0;
                if (args.Count >= 4 && !HarnessCommand.TryParseInt(args[3], out cash))
                    return "the cash value must be a number";
                request = new PickupRequest { InteractId = id, Interaction = type, CashValue = cash };
                note = " (type given by hand)";
            }
            else
            {
                if (!_game.TryResolvePickup(id, out request, out string error))
                    return "cannot resolve pickup " + id + " in the host's world: " + error + " (give <type> <cashValue> to send it anyway)";
                note = $" (host world: {BotInteractions.NameOf(request.Interaction)}, m_cashValue {request.CashValue}" +
                       (request.SaveableId != null ? ", on " + request.SaveableId : string.Empty) + ")";
            }
            if (BotInteractions.KindOf(request.Interaction) == ArbitrationKind.None)
                note += " - not an arbitrated type: the host must refuse the RPC";
            uint claimId = bot.SendPickup(request);
            return claimId == 0
                ? BotLabel(bot) + " could not send the claim"
                : $"{BotLabel(bot)} claims {id} as claim {claimId}{note}; the grant follows";
        }

        private string SentWorld(FakePeer bot, WorldOp op, string id, bool flag) =>
            bot.SendWorld(op, id, flag)
                ? $"{BotLabel(bot)} sends world op {op}{(string.IsNullOrEmpty(id) ? string.Empty : " " + id)}" +
                  $"{(op == WorldOp.SetGates || op == WorldOp.SlidingDoor ? (flag ? " open" : " close") : string.Empty)}; the host replays it"
                : BotLabel(bot) + " could not send the world op";

        private string SetWarning(string name, bool wantBounty)
        {
            try
            {
                if (!_game.TryGetChallengeSet(name, out bool isBounty, out _))
                    return " (no such clean set name on the host: it must refuse the claim)";
                if (wantBounty && !isBounty)
                    return " (not a bounty set: the host must refuse the claim)";
                if (!wantBounty && isBounty)
                    return " (a bounty set: the host must refuse it as the main track)";
            }
            catch (Exception e)
            {
                return " (could not look the set up: " + e.Message + ")";
            }
            return string.Empty;
        }

        private static string Describe(FakePeer bot, string kind, string key)
        {
            var value = bot.Save.Facts.Get(kind, key);
            return value == null ? "(none)" : FactText.Format(kind, value);
        }

        private List<FakePeer> Select(string selector, bool allByDefault, out string error)
        {
            error = null;
            if (selector != null)
            {
                var bot = _hub.Find(selector);
                if (bot == null)
                {
                    error = "no bot '" + selector + "'";
                    return null;
                }
                return new List<FakePeer> { bot };
            }
            if (_hub.Bots.Count == 0)
            {
                error = "no bots: 'bot add' first";
                return null;
            }
            return allByDefault ? new List<FakePeer>(_hub.Bots) : new List<FakePeer> { _hub.Bots[0] };
        }

        private static string Names(List<FakePeer> bots)
        {
            var names = new List<string>(bots.Count);
            foreach (var bot in bots)
                names.Add(BotLabel(bot));
            return string.Join(", ", names);
        }

        public static string BotLabel(FakePeer bot) =>
            bot == null ? "harness" : "bot" + bot.Index.ToString(CultureInfo.InvariantCulture) + " (" + bot.Name + ")";

        private static bool TryOpenClose(string text, out bool open)
        {
            switch (text.ToLowerInvariant())
            {
                case "open": open = true; return true;
                case "close":
                case "closed": open = false; return true;
                default: return HarnessCommand.TryParseSwitch(text, out open);
            }
        }

        public static bool TryParseWorldOp(string text, out WorldOp op)
        {
            op = default;
            if (string.IsNullOrEmpty(text))
                return false;
            if (byte.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out byte number))
            {
                if (!WorldOps.IsKnown(number))
                    return false;
                op = (WorldOp)number;
                return true;
            }
            return Enum.TryParse(text, true, out op) && WorldOps.IsKnown((byte)op);
        }

        public static bool TryParseHex(string text, out byte[] value)
        {
            value = Array.Empty<byte>();
            if (text == null)
                return false;
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                text = text.Substring(2);
            if (text.Length % 2 != 0)
                return false;
            var bytes = new byte[text.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                if (!byte.TryParse(text.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
                    return false;
            }
            value = bytes;
            return true;
        }

        private bool SafeInSession()
        {
            try
            {
                return _game.InSession;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private bool SafeReady(PeerId peer)
        {
            try
            {
                return _game.IsPeerReady(peer);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private string SafeSaveDirectory()
        {
            try
            {
                return _game.SaveDirectory ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static void Guarded(JsonBuilder json, Action write)
        {
            int depth = json.Depth;
            try
            {
                write();
                if (json.Depth != depth)
                    throw new InvalidOperationException("the writer left " + (json.Depth - depth) + " level(s) open");
            }
            catch (Exception e)
            {
                // Close what the writer left open; a half-written object may already hold names, this one cannot clash.
                json.CloseTo(depth);
                json.Value("dumpError", e.GetType().Name + ": " + e.Message);
            }
        }

        private void Emit(string text)
        {
            var handler = Output;
            if (handler == null)
                return;
            try
            {
                handler(text);
            }
            catch (Exception e)
            {
                NetLog.Error($"[TestHarness] Output handler threw: {e}");
            }
        }
    }
}
