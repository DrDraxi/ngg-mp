using System;
using System.Collections.Generic;

namespace NGGMultiplayer.Game
{
    /// <summary>
    /// Marks game calls the mod makes on behalf of the network (replaying a remote change), so the mod's own
    /// patches on those game methods do not broadcast them again, award achievements, or claim them back to the
    /// host. Two parts:
    /// <list type="bullet">
    /// <item><see cref="Begin"/> opens a scope around the replayed calls (nestable).</item>
    /// <item><see cref="HoldFor"/> keeps <see cref="IsReplaying"/> true for a while after the scope closed, for game
    /// effects that run later (MonoBehaviour.Invoke(name, 0f), coroutines started by the replayed call).</item>
    /// <item><see cref="HoldEffectsFor"/> is the narrower window after the replayed calls known to queue such effects
    /// (story methods, challenge completion / loading); <see cref="InReplayEffects"/> covers only those.</item>
    /// </list>
    /// Which one to check: the client's diff waits for <see cref="IsReplaying"/> to go quiet (state is diffed, so waiting
    /// loses nothing). The local player's own actions (interactions, money reports, achievements, sent events) must not
    /// be swallowed by a wall-clock window after some unrelated replay: they check <see cref="IsActive"/>, or
    /// <see cref="InReplayEffects"/> where a deferred effect of a replay would otherwise pass for the player's own.
    /// Main thread only. Contains no Unity types so it can be unit tested.
    /// </summary>
    public sealed class ReplayGuard
    {
        private readonly Func<double> _clock;
        private readonly List<string> _reasons = new List<string>();
        private double _holdUntil = double.NegativeInfinity;
        private double _effectsUntil = double.NegativeInfinity;

        /// <param name="clock">Monotonic seconds (the plugin passes Time.realtimeSinceStartupAsDouble).</param>
        public ReplayGuard(Func<double> clock)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        /// <summary>Inside at least one <see cref="Begin"/> scope.</summary>
        public bool IsActive => _reasons.Count > 0;

        /// <summary>Inside a scope, or within a hold window after one. This is what patches check.</summary>
        public bool IsReplaying => _reasons.Count > 0 || _clock() < _holdUntil;

        /// <summary>Inside a scope, or within an effects hold (<see cref="HoldEffectsFor"/>).</summary>
        public bool InReplayEffects => _reasons.Count > 0 || _clock() < _effectsUntil;

        public int Depth => _reasons.Count;

        /// <summary>The innermost scope's reason, or the empty string.</summary>
        public string Reason => _reasons.Count > 0 ? _reasons[_reasons.Count - 1] : string.Empty;

        /// <summary>Seconds since the hold window (or the last scope) ended; 0 while replaying.</summary>
        public double QuietSeconds
        {
            get
            {
                if (IsReplaying)
                    return 0;
                double since = _clock() - _holdUntil;
                return double.IsInfinity(since) ? double.MaxValue : since;
            }
        }

        /// <summary>Opens a replay scope. Dispose it (use <c>using</c>) to close it.</summary>
        public Scope Begin(string reason)
        {
            _reasons.Add(reason ?? string.Empty);
            return new Scope(this, _reasons.Count);
        }

        /// <summary>Keeps <see cref="IsReplaying"/> true for at least <paramref name="seconds"/> from now.</summary>
        public void HoldFor(double seconds)
        {
            if (!(seconds > 0))
                return;
            double until = _clock() + seconds;
            if (until > _holdUntil)
                _holdUntil = until;
        }

        /// <summary>
        /// After a replayed call that queues game effects for later (Invoke by name, coroutines): keeps
        /// <see cref="InReplayEffects"/> (and <see cref="IsReplaying"/>) true for at least <paramref name="seconds"/>.
        /// </summary>
        public void HoldEffectsFor(double seconds)
        {
            if (!(seconds > 0))
                return;
            HoldFor(seconds);
            double until = _clock() + seconds;
            if (until > _effectsUntil)
                _effectsUntil = until;
        }

        /// <summary>Drops every scope and hold window (session end, scene change).</summary>
        public void Reset()
        {
            _reasons.Clear();
            _holdUntil = double.NegativeInfinity;
            _effectsUntil = double.NegativeInfinity;
        }

        private void Exit(int depth)
        {
            // A scope closed out of order (or twice) must not leave the guard stuck on; trim to below it.
            if (depth <= 0 || depth > _reasons.Count)
                return;
            _reasons.RemoveRange(depth - 1, _reasons.Count - depth + 1);
            // Closing a scope also starts a short implicit hold: effects queued by the replay for the next frame
            // (Invoke(name, 0f)) still count as replay.
            HoldFor(ImplicitHoldSeconds);
        }

        /// <summary>Hold applied whenever a scope closes.</summary>
        public const double ImplicitHoldSeconds = 0.1;

        public struct Scope : IDisposable
        {
            private ReplayGuard _guard;
            private readonly int _depth;

            internal Scope(ReplayGuard guard, int depth)
            {
                _guard = guard;
                _depth = depth;
            }

            public void Dispose()
            {
                var guard = _guard;
                _guard = null;
                guard?.Exit(_depth);
            }
        }
    }
}
