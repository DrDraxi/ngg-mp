using System;
using System.Collections.Generic;
using NGGMultiplayer.Core.Save;

namespace NGGMultiplayer.Testing
{
    public enum BotClaimOutcome : byte
    {
        /// <summary>The host broadcast the claimed value.</summary>
        Accepted,

        /// <summary>The host broadcast another value for the same fact (it recorded its own, e.g. a merged list).</summary>
        AcceptedOther,

        /// <summary>The host refused the claim (ProgressClaimRejected).</summary>
        Rejected,

        /// <summary>The session already had the value; nothing was sent.</summary>
        AlreadySet,

        /// <summary>No answer in time: the host validated it as a no-op, kept it for later, or it was lost.</summary>
        NoAnswer,

        /// <summary>The session ended before an answer.</summary>
        Abandoned
    }

    /// <summary>One settled claim, for the results file.</summary>
    public readonly struct BotClaimResult
    {
        public BotClaimResult(SaveDelta claim, BotClaimOutcome outcome, string detail, double seconds)
        {
            Claim = claim;
            Outcome = outcome;
            Detail = detail ?? string.Empty;
            Seconds = seconds;
        }

        public readonly SaveDelta Claim;
        public readonly BotClaimOutcome Outcome;
        public readonly string Detail;

        /// <summary>From the claim to its outcome.</summary>
        public readonly double Seconds;

        public override string ToString() =>
            $"claim {Claim.Kind}/{Claim.Key} = {FactText.Format(Claim.Kind, Claim.Value)}: {Describe(Outcome)}" +
            (Detail.Length > 0 ? " (" + Detail + ")" : string.Empty) +
            (Outcome == BotClaimOutcome.AlreadySet ? string.Empty : $" after {Seconds:0.00} s");

        public static string Describe(BotClaimOutcome outcome)
        {
            switch (outcome)
            {
                case BotClaimOutcome.Accepted: return "accepted";
                case BotClaimOutcome.AcceptedOther: return "host recorded another value";
                case BotClaimOutcome.Rejected: return "REJECTED";
                case BotClaimOutcome.AlreadySet: return "already the session's value, not sent";
                case BotClaimOutcome.NoAnswer: return "no broadcast (host treated it as a no-op or kept it for later)";
                default: return "abandoned (session ended)";
            }
        }
    }

    /// <summary>
    /// A test bot's progress claims waiting for the host's verdict: Core sends a claim as ProgressClaim, and the host
    /// either broadcasts the fact (a SaveDelta reaches every client, the claimer included) or rejects it. Pure; the
    /// clock is passed in.
    /// </summary>
    public sealed class BotClaims
    {
        public const double AnswerTimeoutSeconds = 10.0;

        private readonly List<Pending> _pending = new List<Pending>();
        private readonly List<BotClaimResult> _settled = new List<BotClaimResult>();

        public int PendingCount => _pending.Count;

        public int Accepted { get; private set; }

        public int Rejected { get; private set; }

        public int Unanswered { get; private set; }

        /// <summary>A claim was sent at <paramref name="now"/>; one per fact (a newer claim replaces the older one).</summary>
        public void Sent(SaveDelta claim, double now)
        {
            if (claim == null)
                throw new ArgumentNullException(nameof(claim));
            int index = IndexOf(claim.Kind, claim.Key);
            if (index >= 0)
                _pending.RemoveAt(index);
            _pending.Add(new Pending(claim, now));
        }

        /// <summary>The session already had the claimed value, so nothing goes out (Core skips confirmed values).</summary>
        public void AlreadySet(SaveDelta claim) =>
            _settled.Add(new BotClaimResult(claim, BotClaimOutcome.AlreadySet, string.Empty, 0));

        /// <summary>The host broadcast a fact; settles a pending claim of the same fact.</summary>
        public void OnDelta(SaveDelta delta, double now)
        {
            if (delta == null)
                return;
            int index = IndexOf(delta.Kind, delta.Key);
            if (index < 0)
                return;
            var pending = _pending[index];
            _pending.RemoveAt(index);
            bool same = NGGMultiplayer.Game.FactTable.ValueEquals(pending.Claim.Value, delta.Value);
            Accepted++;
            _settled.Add(new BotClaimResult(pending.Claim, same ? BotClaimOutcome.Accepted : BotClaimOutcome.AcceptedOther,
                same ? string.Empty : "host value " + FactText.Format(delta.Kind, delta.Value), now - pending.At));
        }

        /// <summary>The host rejected a claim.</summary>
        public void OnRejected(SaveDelta claim, string reason, double now)
        {
            if (claim == null)
                return;
            int index = IndexOf(claim.Kind, claim.Key);
            double at = now;
            SaveDelta original = claim;
            if (index >= 0)
            {
                at = _pending[index].At;
                original = _pending[index].Claim;
                _pending.RemoveAt(index);
            }
            Rejected++;
            _settled.Add(new BotClaimResult(original, BotClaimOutcome.Rejected, reason, now - at));
        }

        /// <summary>Expires claims older than <see cref="AnswerTimeoutSeconds"/>.</summary>
        public void Tick(double now)
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                if (now - _pending[i].At < AnswerTimeoutSeconds)
                    continue;
                Unanswered++;
                _settled.Add(new BotClaimResult(_pending[i].Claim, BotClaimOutcome.NoAnswer, string.Empty, now - _pending[i].At));
                _pending.RemoveAt(i);
            }
        }

        /// <summary>The session ended: every pending claim is abandoned.</summary>
        public void Abandon(double now)
        {
            foreach (var pending in _pending)
                _settled.Add(new BotClaimResult(pending.Claim, BotClaimOutcome.Abandoned, string.Empty, now - pending.At));
            _pending.Clear();
        }

        /// <summary>Settled claims since the last call, oldest first.</summary>
        public void Drain(List<BotClaimResult> into)
        {
            into.AddRange(_settled);
            _settled.Clear();
        }

        private int IndexOf(string kind, string key)
        {
            for (int i = 0; i < _pending.Count; i++)
            {
                if (string.Equals(_pending[i].Claim.Kind, kind, StringComparison.Ordinal)
                    && string.Equals(_pending[i].Claim.Key, key, StringComparison.Ordinal))
                    return i;
            }
            return -1;
        }

        private readonly struct Pending
        {
            public Pending(SaveDelta claim, double at)
            {
                Claim = claim;
                At = at;
            }

            public readonly SaveDelta Claim;
            public readonly double At;
        }
    }
}
