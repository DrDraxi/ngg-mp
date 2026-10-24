using System;
using System.Collections.Generic;
using System.Globalization;
using NGGMultiplayer.Core.Diagnostics;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;

namespace NGGMultiplayer.Testing
{
    /// <summary>
    /// The test bots' in-process network: one <see cref="LoopbackHub"/> joining the host's
    /// <see cref="CompositeTransport"/> to every <see cref="FakePeer"/>. <see cref="Wrap"/> puts the composite in front of
    /// the real transport (called once by the Steam service when the harness is enabled); bots can join only while the
    /// local peer hosts (<see cref="CompositeTransport.LoopbackEnabled"/>). The hub's clock follows the time passed to
    /// <see cref="Update"/>, so <see cref="LatencyMs"/> simulates a link delay. Pure; main thread only.
    /// </summary>
    public sealed class BotHub : IDisposable
    {
        private readonly LoopbackHub _hub = new LoopbackHub();
        private readonly List<FakePeer> _bots = new List<FakePeer>();
        private readonly Dictionary<FakePeer, LoopbackTransport> _transports = new Dictionary<FakePeer, LoopbackTransport>();
        private readonly List<FakePeer> _scratch = new List<FakePeer>();
        private CompositeTransport _composite;
        private double _lastNow = double.NaN;
        private int _added;
        private bool _disposed;

        public IReadOnlyList<FakePeer> Bots => _bots;

        /// <summary>Null until <see cref="Wrap"/> ran (the integration hook is missing, or Steam is not up yet).</summary>
        public CompositeTransport Composite => _composite;

        public bool IsWired => _composite != null && !_composite.IsDisposed;

        public LoopbackHub Hub => _hub;

        /// <summary>One-way delay of every packet between the host and its bots.</summary>
        public double LatencyMs
        {
            get => _hub.LatencyMs;
            set => _hub.LatencyMs = value;
        }

        /// <summary>A bot said something (joined, synced, a grant, a claim verdict, ended) or the hub did.</summary>
        public event Action<FakePeer, string> Message;

        /// <summary>
        /// The transport the host's session should use: <paramref name="primary"/> plus the bots' hub. A second call with
        /// another primary (the backend restarted) detaches the old composite first.
        /// </summary>
        public CompositeTransport Wrap(ITransport primary)
        {
            if (primary == null)
                throw new ArgumentNullException(nameof(primary));
            if (_disposed)
                throw new ObjectDisposedException(nameof(BotHub));
            if (_composite != null && !_composite.IsDisposed && ReferenceEquals(_composite.Primary, primary))
                return _composite;
            RemoveAll(false);
            _composite?.Dispose();
            _composite = new CompositeTransport(primary, _hub.CreateTransport(primary.LocalId));
            return _composite;
        }

        /// <summary>
        /// Starts a bot that joins <paramref name="host"/> (the local peer, which must be hosting through the composite).
        /// </summary>
        /// <exception cref="InvalidOperationException">Not wired, not hosting, or <see cref="BotIds.MaxBots"/> bots run.</exception>
        public FakePeer Add(PeerId host, FakePeerOptions options)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(BotHub));
            if (!IsWired)
                throw new InvalidOperationException("The bots' transport is not wired into the multiplayer service (SteamMultiplayerService must pass its transport through TestHarness.WrapTransport).");
            if (!_composite.LoopbackEnabled)
                throw new InvalidOperationException("Bots can only join while this game hosts a session.");
            if (host != _composite.LocalId)
                throw new InvalidOperationException("Bots join the local host only.");
            int index = FreeIndex();
            if (index == 0)
                throw new InvalidOperationException($"At most {BotIds.MaxBots} bots.");
            options = options ?? new FakePeerOptions();
            if (string.IsNullOrEmpty(options.DisplayName))
                options.DisplayName = "Bot " + index.ToString(CultureInfo.InvariantCulture);
            // Spread bots around the host (golden angle), whatever order they come and go in.
            options.Phase = (float)((_added++ * 2.39996322972865332) % (2 * Math.PI));
            var id = BotIds.Make(index);
            _hub.SetLinkDown(id, host, false);
            var transport = _hub.CreateTransport(id);
            FakePeer bot;
            try
            {
                bot = new FakePeer(index, transport, options);
            }
            catch
            {
                transport.Dispose();
                throw;
            }
            bot.Message += OnBotMessage;
            _bots.Add(bot);
            _transports[bot] = transport;
            try
            {
                bot.Connect(host);
            }
            catch
            {
                Remove(bot, false);
                throw;
            }
            return bot;
        }

        /// <summary>
        /// Removes a bot. <paramref name="graceful"/>: it leaves the session (the host hears a goodbye at once);
        /// otherwise its link is cut first, so it vanishes like a crashed game and the host times it out.
        /// </summary>
        public void Remove(FakePeer bot, bool graceful)
        {
            if (bot == null || !_transports.TryGetValue(bot, out var transport))
                return;
            if (!graceful && _composite != null)
                _hub.SetLinkDown(bot.Id, _composite.LocalId, true);
            try
            {
                if (graceful)
                    bot.Leave();
            }
            catch (Exception e)
            {
                NetLog.Error($"[TestHarness] {bot.Name} could not leave cleanly: {e}");
            }
            bot.Dispose();
            transport.Dispose();
            bot.Message -= OnBotMessage;
            _transports.Remove(bot);
            _bots.Remove(bot);
        }

        public void RemoveAll(bool graceful)
        {
            _scratch.Clear();
            _scratch.AddRange(_bots);
            foreach (var bot in _scratch)
                Remove(bot, graceful);
            _scratch.Clear();
        }

        /// <summary>A bot by number ("2") or name (case-insensitive); null when none matches.</summary>
        public FakePeer Find(string selector)
        {
            if (string.IsNullOrEmpty(selector))
                return null;
            if (int.TryParse(selector, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
            {
                foreach (var bot in _bots)
                {
                    if (bot.Index == index)
                        return bot;
                }
            }
            foreach (var bot in _bots)
            {
                if (string.Equals(bot.Name, selector, StringComparison.OrdinalIgnoreCase))
                    return bot;
            }
            return null;
        }

        /// <summary>The newest bot, or null.</summary>
        public FakePeer Newest => _bots.Count > 0 ? _bots[_bots.Count - 1] : null;

        /// <summary>
        /// Runs one frame: advances the hub's clock, then every bot (around <paramref name="anchor"/>, the host's pose).
        /// Bots whose session ended are removed. <paramref name="hosting"/> opens the loopback while the local peer hosts.
        /// </summary>
        public void Update(double now, bool hosting, NetVector3 anchor, bool anchorValid)
        {
            if (_disposed)
                return;
            if (!double.IsNaN(_lastNow) && now > _lastNow)
                _hub.Advance(Math.Min(1.0, now - _lastNow));
            _lastNow = now;
            if (_composite != null && !_composite.IsDisposed)
                _composite.LoopbackEnabled = hosting;

            _scratch.Clear();
            _scratch.AddRange(_bots);
            foreach (var bot in _scratch)
            {
                try
                {
                    bot.Update(now, anchor, anchorValid);
                }
                catch (Exception e)
                {
                    NetLog.Error($"[TestHarness] {bot.Name} failed: {e}");
                    Say(bot, "failed and was removed: " + e.Message);
                    Remove(bot, false);
                    continue;
                }
                if (bot.State == FakePeerState.Ended)
                {
                    Say(bot, "removed (its session ended)");
                    Remove(bot, false);
                }
            }
            _scratch.Clear();
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            RemoveAll(true);
            // The host session may still use the composite as its transport: it keeps passing the real one through.
            _composite?.Dispose();
            _disposed = true;
        }

        private int FreeIndex()
        {
            for (int i = 1; i <= BotIds.MaxBots; i++)
            {
                bool used = false;
                foreach (var bot in _bots)
                {
                    if (bot.Index == i)
                    {
                        used = true;
                        break;
                    }
                }
                if (!used)
                    return i;
            }
            return 0;
        }

        private void OnBotMessage(FakePeer bot, string text) => Say(bot, text);

        private void Say(FakePeer bot, string text)
        {
            var handler = Message;
            if (handler == null)
                return;
            try
            {
                handler(bot, text);
            }
            catch (Exception e)
            {
                NetLog.Error($"[TestHarness] Message handler threw: {e}");
            }
        }
    }
}
