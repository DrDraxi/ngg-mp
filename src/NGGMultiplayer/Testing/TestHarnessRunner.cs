using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using NGGMultiplayer.Core;
using NGGMultiplayer.Core.Diagnostics;
using NGGMultiplayer.Game;
using NGGMultiplayer.Runtime;
using UnityEngine;

namespace NGGMultiplayer.Testing
{
    /// <summary>
    /// Drives the test harness from Unity's frame loop: the bots every frame (on real time, so a paused game at
    /// timeScale 0 keeps its bots talking, as real clients would), and the command file once per second. Commands read
    /// from it go into a <see cref="CommandQueue"/> that runs every frame, so <c>wait</c> and game actions in progress
    /// (a held key, a screenshot being written) hold the commands after them. Only added to the plugin's host object
    /// when the harness is enabled (<see cref="TestHarness.Install"/>).
    /// </summary>
    public sealed class TestHarnessRunner : MonoBehaviour
    {
        private const double PollInterval = 1.0;
        private const double ErrorLogInterval = 5.0;

        private IMultiplayerService _service;
        private BotHub _hub;
        private HarnessCommands _commands;
        private CommandFileReader _reader;
        private ResultWriter _results;
        private HarnessControl _control;
        private readonly CommandQueue _queue = new CommandQueue();
        private readonly Dictionary<HarnessCommand, Action<string>> _callbacks = new Dictionary<HarnessCommand, Action<string>>();
        private NetSession _filteredNet;
        private double _nextPoll;
        private double _nextErrorLog;
        private bool _autoTestPending;
        private double _menuSince = double.NaN;

        internal void Initialize(IMultiplayerService service, IGameAdapter adapter, BotHub hub)
        {
            _service = service;
            _hub = hub;
            string configDir = Paths.ConfigPath;
            _reader = new CommandFileReader(Path.Combine(configDir, TestHarness.CommandFileName));
            _results = new ResultWriter(Path.Combine(configDir, TestHarness.ResultFileName));
            _control = new HarnessControl(this, Write);
            _commands = new HarnessCommands(hub, new HarnessGame(service, adapter), () => Time.realtimeSinceStartupAsDouble,
                null, _control, new HarnessPlay(this));
            _commands.Output += Write;
            _reader.Start(skipExisting: true);
            _commands.Start();
            _autoTestPending = TestHarness.AutoTestRequested;
            Write("test harness started (protocol " + Core.Protocol.ProtocolInfo.Version + ", mod " + Core.Protocol.ProtocolInfo.ModVersion +
                  "). Append commands to " + _reader.Path + "; 'help' lists them.\n" + RuntimeChecks.Run("interaction"));
        }

        private void Update()
        {
            if (_commands == null)
                return;
            double now = Time.realtimeSinceStartupAsDouble;
            try
            {
                ExtendAdmission();
                _commands.Tick();
            }
            catch (Exception e)
            {
                LogThrottled(now, "tick", e);
            }
            StartAutoTest(now);
            if (now >= _nextPoll)
            {
                _nextPoll = now + PollInterval;
                ReadCommands(now);
            }
            RunQueued(now);
        }

        /// <summary>
        /// Queues the built-in run (<see cref="AutoTest"/>) once the main menu has been up for a few seconds, and
        /// switches the auto test and the harness off for the next launch so the run happens once.
        /// </summary>
        private void StartAutoTest(double now)
        {
            if (!_autoTestPending)
                return;
            try
            {
                if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "Menu2")
                {
                    _menuSince = double.NaN;
                    return;
                }
                if (double.IsNaN(_menuSince))
                    _menuSince = now;
                if (now - _menuSince < AutoTest.MenuSettleSeconds)
                    return;
                _autoTestPending = false;
                TestHarness.ConsumeAutoTest();
                var commands = AutoTest.Commands();
                foreach (var command in commands)
                    _queue.Add(command);
                Write("auto test started: " + commands.Count + " commands queued. [" + TestHarness.Section + "] "
                    + TestHarness.AutoTestKey + " and " + TestHarness.EnableKey + " are off again for the next launch.");
            }
            catch (Exception e)
            {
                _autoTestPending = false;
                LogThrottled(now, "auto test", e);
            }
        }

        private void ReadCommands(double now)
        {
            try
            {
                var lines = _reader.ReadNewLines();
                if (_reader.ShrankLastRead)
                {
                    Write("Command file got shorter without being emptied: nothing in it was run again. Only append "
                        + "lines; to start over, save it empty first, then append.");
                }
                foreach (var line in lines)
                {
                    var command = HarnessCommand.Parse(line);
                    if (command != null && !_queue.Add(command))
                        Write("> " + command.Line + "\nnot run: " + CommandQueue.MaxQueued + " commands are already waiting");
                }
            }
            catch (Exception e)
            {
                LogThrottled(now, "command file", e);
            }
        }

        private void RunQueued(double now)
        {
            try
            {
                while (_queue.TryTake(now, _control.Busy, out var command))
                {
                    string result = _commands.Execute(command);
                    Write("> " + command.Line + "\n" + result);
                    _queue.Hold(now, _commands.TakeHold());
                    if (_callbacks.TryGetValue(command, out var callback))
                    {
                        _callbacks.Remove(command);
                        callback(result);
                    }
                }
            }
            catch (Exception e)
            {
                LogThrottled(now, "command queue", e);
            }
        }

        /// <summary>
        /// Queues <paramref name="line"/> behind the commands already waiting (as if appended to the command file) and
        /// calls <paramref name="onResult"/> with its result once it ran. False when it is no command or the queue is
        /// full. Used by <see cref="DevControl"/>.
        /// </summary>
        internal bool Submit(string line, Action<string> onResult)
        {
            var command = HarnessCommand.Parse(line);
            if (command == null || !_queue.Add(command))
                return false;
            if (onResult != null)
                _callbacks[command] = onResult;
            return true;
        }

        /// <summary>Runs <paramref name="line"/> at once, outside the queue (read-only reports such as scene, status).</summary>
        internal string RunNow(string line)
        {
            var command = HarnessCommand.Parse(line);
            return command == null || _commands == null ? string.Empty : _commands.Execute(command);
        }

        /// <summary>
        /// The Steam service admits lobby members only; bots are none. Wraps the session's filter once per session
        /// facade so it also admits this harness's own running bots (and nothing else).
        /// </summary>
        private void ExtendAdmission()
        {
            var net = _service?.Net;
            if (net == null || ReferenceEquals(net, _filteredNet))
                return;
            _filteredNet = net;
            var previous = net.Session.AdmissionFilter;
            if (previous == null)
                return;
            net.Session.AdmissionFilter = peer => TestHarness.AdmitsBot(peer) || previous(peer);
        }

        private void Write(string text)
        {
            try
            {
                _results?.Write(text);
            }
            catch (Exception e)
            {
                // The results file is the harness's output; without it the result goes to the log instead.
                NetLog.Error($"[TestHarness] Writing {_results?.Path} failed: {e.Message}. Result: {text}");
            }
        }

        private void LogThrottled(double now, string what, Exception e)
        {
            if (now < _nextErrorLog)
                return;
            _nextErrorLog = now + ErrorLogInterval;
            NetLog.Error($"[TestHarness] {what} failed: {e}");
        }

        private void OnDestroy()
        {
            _commands = null;
            TestHarness.OnRunnerDestroyed();
        }
    }
}
