using System;
using BepInEx.Configuration;
using NGGMultiplayer.Core.Diagnostics;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Game;
using NGGMultiplayer.Runtime;
using UnityEngine;

namespace NGGMultiplayer.Testing
{
    /// <summary>
    /// Entry points of the single-PC test harness (bots that join this game's hosted session in-process, driven by a
    /// command file). Off unless <c>[Debug] EnableTestHarness = true</c> in the plugin's config; then read once at
    /// start (a change needs a game restart, since the transport is wrapped when Steam comes up). While off, every hook
    /// is a pass-through and nothing of the harness runs.
    /// <para>Wired in at two places (both no-ops while the harness is off):</para>
    /// <list type="number">
    /// <item><c>Plugin.Awake</c>, after <c>AttachGameAdapter(...)</c>: <c>InstallTestHarness</c> calls <see cref="Install"/>.</item>
    /// <item><c>SteamMultiplayerService.TryStartBackend</c>: <c>_net = new NetSession(Testing.TestHarness.WrapTransport(_transport), ...)</c>
    /// (the Steam transport keeps being created, owned and disposed by the service).</item>
    /// </list>
    /// The host admits bots although they are not Steam lobby members: the harness extends the session's admission
    /// filter at run time with its own bots' ids only.
    /// </summary>
    public static class TestHarness
    {
        public const string Section = "Debug";
        public const string EnableKey = "EnableTestHarness";
        public const string AutoTestKey = "AutoTest";
        public const string CommandFileName = "ngg_mp_commands.txt";
        public const string ResultFileName = "ngg_mp_results.txt";

        private static BotHub _hub;
        private static bool _enabled;
        private static ConfigEntry<bool> _enable;
        private static ConfigEntry<int> _latency;
        private static ConfigEntry<bool> _autoTest;
        private static ConfigEntry<int> _devPort;

        /// <summary>True when the harness was enabled at start.</summary>
        public static bool Enabled => _enabled;

        /// <summary>The bots' network while enabled; null otherwise.</summary>
        public static BotHub Hub => _hub;

        /// <summary>
        /// Binds the harness's settings and, when enabled, starts it on <paramref name="host"/>. Never throws; any
        /// failure leaves the harness off.
        /// </summary>
        public static void Install(ConfigFile config, IMultiplayerService service, IGameAdapter adapter, GameObject host)
        {
            try
            {
                if (config == null)
                    return;
                _enable = config.Bind(Section, EnableKey, false,
                    "Single-PC test harness: bots that join your hosted session in-process, driven by BepInEx/config/" +
                    CommandFileName + " (results in " + ResultFileName + "). For testing only; needs a game restart.");
                _latency = config.Bind(Section, "TestBotLatencyMs", 0, new ConfigDescription(
                    "One-way delay of the harness bots' link in milliseconds.", new AcceptableValueRange<int>(0, 2000)));
                _autoTest = config.Bind(Section, AutoTestKey, false,
                    "Runs the built-in remote test (docs/TESTING.md 2.6) by itself once the main menu is up, then quits " +
                    "the game. Needs " + EnableKey + ". One-shot: starting the run switches this and " + EnableKey +
                    " back off for the next launch.");
                _devPort = config.Bind(Section, "DevControlPort", 0, new ConfigDescription(
                    "Test harness only: a local HTTP API on 127.0.0.1:<port> for scripts (scripts/e2e.ps1): /status, " +
                    "/cmd?line=<harness command>, /screenshot, /host, /join, /leave, /quit. 0 is off.",
                    new AcceptableValueRange<int>(0, 65535)));
                if (!_enable.Value)
                    return;
                if (service == null || host == null)
                {
                    NetLog.Warn("[TestHarness] Enabled, but there is no multiplayer service; the harness stays off.");
                    return;
                }
                _hub = _hub ?? new BotHub();
                _hub.LatencyMs = _latency.Value;
                _enabled = true;
                var runner = host.AddComponent<TestHarnessRunner>();
                runner.Initialize(service, adapter, _hub);
                NetLog.Warn($"[TestHarness] TEST HARNESS ENABLED ([{Section}] {EnableKey}). Commands: BepInEx/config/{CommandFileName}.");
                if (_devPort.Value > 0)
                    host.AddComponent<DevControl>().Initialize(_devPort.Value, service, runner);
            }
            catch (Exception e)
            {
                NetLog.Error($"[TestHarness] Starting the test harness failed; it stays off: {e}");
                _enabled = false;
            }
        }

        /// <summary>
        /// The transport the Steam service's session should use: <paramref name="primary"/> itself while the harness is
        /// off, else a composite that also reaches the bots (they are reachable only while this game hosts).
        /// </summary>
        public static ITransport WrapTransport(ITransport primary)
        {
            if (!_enabled || _hub == null || primary == null)
                return primary;
            try
            {
                var composite = _hub.Wrap(primary);
                NetLog.Info($"[TestHarness] Session transport wrapped for bots (local id {primary.LocalId}).");
                return composite;
            }
            catch (Exception e)
            {
                NetLog.Error($"[TestHarness] Wrapping the transport failed; bots are unavailable: {e}");
                return primary;
            }
        }

        /// <summary>True for the id of a bot this harness runs (the session admits it although it is no lobby member).</summary>
        public static bool AdmitsBot(PeerId peer)
        {
            var hub = _hub;
            if (!_enabled || hub == null || !BotIds.IsBot(peer))
                return false;
            foreach (var bot in hub.Bots)
            {
                if (bot.Id == peer)
                    return true;
            }
            return false;
        }

        /// <summary>True when the built-in run should start this launch.</summary>
        internal static bool AutoTestRequested => _enabled && _autoTest != null && _autoTest.Value;

        /// <summary>Switches the auto test and the harness off for the next launch (BepInEx saves the file).</summary>
        internal static void ConsumeAutoTest()
        {
            if (_autoTest != null)
                _autoTest.Value = false;
            if (_enable != null)
                _enable.Value = false;
        }

        internal static void OnRunnerDestroyed()
        {
            var hub = _hub;
            _hub = null;
            _enabled = false;
            try
            {
                hub?.Dispose();
            }
            catch (Exception e)
            {
                NetLog.Error($"[TestHarness] Shutting the bots down failed: {e}");
            }
        }
    }
}
