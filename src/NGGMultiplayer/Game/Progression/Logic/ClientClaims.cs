using System;
using System.Collections.Generic;
using NGGMultiplayer.Core.Net;

namespace NGGMultiplayer.Game.Progression
{
    /// <summary>
    /// Client side of exclusive interactions (6.7): claims waiting for the host, grants waiting to be used, and the
    /// grant scope during which money changes are reported to the host. Pure; main thread only.
    /// </summary>
    public sealed class ClientClaimTracker
    {
        public const double PendingTimeoutSeconds = 5.0;
        public const double GrantUseSeconds = 3.0;
        public const double ScopeTailSeconds = 3.0;

        private readonly Func<double> _clock;
        private readonly Dictionary<uint, Pending> _pending = new Dictionary<uint, Pending>();
        private readonly Dictionary<string, uint> _pendingByTarget = new Dictionary<string, uint>(StringComparer.Ordinal);
        private readonly Dictionary<string, GrantEntry> _grants = new Dictionary<string, GrantEntry>(StringComparer.Ordinal);
        private readonly List<uint> _scratch = new List<uint>();
        private readonly List<string> _scratchIds = new List<string>();
        private uint _nextId;
        private uint _scopeClaim;
        private ArbitrationKind _scopeKind;
        private bool _scopeReported;
        private int _scopeDepth;
        private double _scopeUntil = double.NegativeInfinity;

        public ClientClaimTracker(Func<double> clock)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public int PendingCount => _pending.Count;

        public bool IsPending(string id) => id != null && _pendingByTarget.ContainsKey(id);

        /// <summary>Starts a claim for <paramref name="id"/>; returns its id (never 0).</summary>
        public uint Begin(string id)
        {
            if (string.IsNullOrEmpty(id))
                throw new ArgumentException("A claim needs a target id.", nameof(id));
            if (_pendingByTarget.TryGetValue(id, out uint existing))
                return existing;
            unchecked
            {
                _nextId++;
                if (_nextId == 0)
                    _nextId = 1;
            }
            _pending[_nextId] = new Pending(id, _clock());
            _pendingByTarget[id] = _nextId;
            return _nextId;
        }

        /// <summary>The host answered <paramref name="claimId"/>; false if it is unknown (late or duplicate).</summary>
        public bool TryComplete(uint claimId, out string id)
        {
            if (!_pending.TryGetValue(claimId, out var pending))
            {
                id = null;
                return false;
            }
            _pending.Remove(claimId);
            _pendingByTarget.Remove(pending.Id);
            id = pending.Id;
            return true;
        }

        public void AddGrant(string id, uint claimId)
        {
            if (!string.IsNullOrEmpty(id))
                _grants[id] = new GrantEntry(claimId, _clock() + GrantUseSeconds);
        }

        /// <summary>The prefix found a grant for <paramref name="id"/>: removes and returns it.</summary>
        public bool TryConsumeGrant(string id, out uint claimId)
        {
            claimId = 0;
            if (id == null || !_grants.TryGetValue(id, out var grant))
                return false;
            _grants.Remove(id);
            if (_clock() > grant.Until)
                return false;
            claimId = grant.ClaimId;
            return true;
        }

        /// <summary>Claims the host never answered; they are dropped.</summary>
        public List<string> ExpirePending()
        {
            var timedOut = new List<string>();
            double now = _clock();
            _scratch.Clear();
            foreach (var pair in _pending)
            {
                if (now - pair.Value.At > PendingTimeoutSeconds)
                    _scratch.Add(pair.Key);
            }
            foreach (uint claimId in _scratch)
            {
                if (TryComplete(claimId, out string id))
                    timedOut.Add(id);
            }
            _scratch.Clear();
            _scratchIds.Clear();
            foreach (var pair in _grants)
            {
                if (now > pair.Value.Until)
                    _scratchIds.Add(pair.Key);
            }
            foreach (var id in _scratchIds)
                _grants.Remove(id);
            _scratchIds.Clear();
            return timedOut;
        }

        /// <summary>The granted interaction starts running (every money change in the scope belongs to it).</summary>
        public void OpenScope(uint claimId) => OpenScope(claimId, ArbitrationKind.None);

        /// <summary>
        /// The granted interaction of <paramref name="kind"/> starts running; <see cref="TryTakeReport"/> then only
        /// hands out the changes that interaction makes.
        /// </summary>
        public void OpenScope(uint claimId, ArbitrationKind kind)
        {
            _scopeClaim = claimId;
            _scopeKind = kind;
            _scopeReported = false;
            _scopeDepth++;
        }

        /// <summary>It returned; money changes still count for <see cref="ScopeTailSeconds"/> (animations, coroutines).</summary>
        public void CloseScope()
        {
            if (_scopeDepth > 0)
                _scopeDepth--;
            _scopeUntil = _clock() + ScopeTailSeconds;
        }

        /// <summary>Inside a grant scope: the claim money changes belong to.</summary>
        public bool TryGetScope(out uint claimId)
        {
            claimId = _scopeClaim;
            return _scopeClaim != 0 && (_scopeDepth > 0 || _clock() < _scopeUntil);
        }

        /// <summary>
        /// A local money change: true (with the claim) when it is the granted interaction's to report. A pickup pays
        /// nothing; cash and the missing ball pay once; a purchase costs once (the game's ChangeBalance(0) for an
        /// unaffordable one is never reported). Anything else in the window (the player's own bounty reward) stays
        /// local, and the host's purse wins it back.
        /// </summary>
        public bool TryTakeReport(int change, out uint claimId)
        {
            if (change == 0 || !TryGetScope(out claimId))
            {
                claimId = 0;
                return false;
            }
            bool take;
            switch (_scopeKind)
            {
                case ArbitrationKind.Pickup:
                    take = false;
                    break;
                case ArbitrationKind.CollectCash:
                case ArbitrationKind.FindMissingBall:
                    take = change > 0 && !_scopeReported;
                    break;
                case ArbitrationKind.Purchase:
                    take = change < 0 && !_scopeReported;
                    break;
                default:
                    take = true;
                    break;
            }
            if (!take)
            {
                claimId = 0;
                return false;
            }
            _scopeReported = true;
            return true;
        }

        public void Clear()
        {
            _pending.Clear();
            _pendingByTarget.Clear();
            _grants.Clear();
            _scopeClaim = 0;
            _scopeKind = ArbitrationKind.None;
            _scopeReported = false;
            _scopeDepth = 0;
            _scopeUntil = double.NegativeInfinity;
        }

        private readonly struct Pending
        {
            public Pending(string id, double at)
            {
                Id = id;
                At = at;
            }

            public readonly string Id;
            public readonly double At;
        }

        private readonly struct GrantEntry
        {
            public GrantEntry(uint claimId, double until)
            {
                ClaimId = claimId;
                Until = until;
            }

            public readonly uint ClaimId;
            public readonly double Until;
        }
    }

    /// <summary>
    /// The last live value of every fact this peer saw (<c>lastLive</c>, 3.1) and when each last changed locally.
    /// Change detection is against this, never against the session table, so a deferred application is never
    /// claimed back. Pure; main thread only.
    /// </summary>
    public sealed class FactBaseline
    {
        private readonly FactTable _values = new FactTable();
        private readonly Dictionary<string, double> _changedAt = new Dictionary<string, double>(StringComparer.Ordinal);

        public int Count => _values.Count;

        public byte[] Get(string kind, string key) => _values.Get(kind, key);

        /// <summary>False when the value does not fit a fact (it is then simply not tracked).</summary>
        public bool Set(string kind, string key, byte[] value)
        {
            try
            {
                _values.Set(kind, key, value);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        /// <summary>The baseline holds an equivalent value (see <see cref="FactCodec.Equivalent"/>).</summary>
        public bool Matches(string kind, string key, byte[] value)
        {
            var last = _values.Get(kind, key);
            return last != null && FactCodec.Equivalent(kind, last, value);
        }

        public void MarkLocalChange(string kind, string key, double now) => _changedAt[kind + "\0" + key] = now;

        public bool ChangedWithin(string kind, string key, double now, double seconds) =>
            _changedAt.TryGetValue(kind + "\0" + key, out double at) && now - at < seconds;

        /// <summary>Replaces every value with <paramref name="live"/>; local-change times are kept.</summary>
        public void ResetTo(FactTable live) => _values.ReplaceWith(live);

        public void Clear()
        {
            _values.Clear();
            _changedAt.Clear();
        }
    }

    /// <summary>
    /// Reports an object's rotation only once it held still for a while, so a door that is still swinging is not
    /// sent frame by frame. Pure; main thread only.
    /// </summary>
    public sealed class StabilitySampler
    {
        private readonly Dictionary<string, Sample> _samples = new Dictionary<string, Sample>(StringComparer.Ordinal);

        /// <summary>
        /// True when <paramref name="state"/> has stayed within <paramref name="toleranceDegrees"/> of the first
        /// sample for at least <paramref name="seconds"/>.
        /// </summary>
        public bool IsStable(string id, ObjState state, double now, double seconds) =>
            IsStable(id, state, now, seconds, FactCodec.RotationToleranceDegrees * 0.5f);

        public bool IsStable(string id, ObjState state, double now, double seconds, float toleranceDegrees)
        {
            if (_samples.TryGetValue(id, out var sample) && FactCodec.ObjEquivalent(sample.State, state, toleranceDegrees))
                return now - sample.Since >= seconds;
            _samples[id] = new Sample(state, now);
            return seconds <= 0;
        }

        public void Forget(string id) => _samples.Remove(id);

        public void Clear() => _samples.Clear();

        private readonly struct Sample
        {
            public Sample(ObjState state, double since)
            {
                State = state;
                Since = since;
            }

            public readonly ObjState State;
            public readonly double Since;
        }
    }

    /// <summary>At most one action per peer per interval.</summary>
    public sealed class PeerRateLimiter
    {
        private readonly Func<double> _clock;
        private readonly double _interval;
        private readonly Dictionary<PeerId, double> _last = new Dictionary<PeerId, double>();

        public PeerRateLimiter(Func<double> clock, double intervalSeconds)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _interval = intervalSeconds;
        }

        public bool TryTake(PeerId peer)
        {
            double now = _clock();
            if (_last.TryGetValue(peer, out double last) && now - last < _interval)
                return false;
            _last[peer] = now;
            return true;
        }

        public void Clear() => _last.Clear();
    }
}
