using System;
using System.Collections.Generic;
using NGGMultiplayer.Core.Net;

namespace NGGMultiplayer.Game.Progression
{
    /// <summary>
    /// Host side of exclusive interactions (docs/GAME_INTEGRATION.md 6.7): who took which pickup, the single
    /// purchase lock, granted claims and the money reports made under them. Pure; the module feeds it the game's
    /// answers (is the object still there, the balance, the price). Main thread only.
    /// </summary>
    public sealed class ClaimArbiter
    {
        public const double PurchaseLockSeconds = 5.0;

        /// <summary>
        /// How long the purchase lock stays after a purchase was paid. What was bought (assist counters, a club
        /// unlock, the stand) reaches the host and the other games only with the buyer's next diff, which waits for
        /// replay quiet; a second purchase before that would start from the old value and buy nothing.
        /// </summary>
        public const double PurchaseSettleSeconds = 3.0;
        public const double GrantLifetimeSeconds = 60.0;
        public const int MaxBankReportsPerClaim = 4;
        public const int MaxPurchase = 1000000;
        public const int MaxMissingBallReward = 10000;

        /// <summary>What FindMissingBall pays in the game (hardcoded there, not m_cashValue).</summary>
        public const int MissingBallReward = 1000;

        private readonly Func<double> _clock;
        private readonly Dictionary<string, Taken> _taken = new Dictionary<string, Taken>(StringComparer.Ordinal);
        private readonly Dictionary<GrantKey, Grant> _grants = new Dictionary<GrantKey, Grant>();
        private readonly List<GrantKey> _expired = new List<GrantKey>();
        private PeerId _lockHolder;
        private uint _lockClaim;
        private string _lockId;
        private double _lockUntil;

        public ClaimArbiter(Func<double> clock)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public bool IsPurchaseLocked => _lockHolder.IsValid;

        public PeerId PurchaseLockHolder => _lockHolder;

        public int GrantCount => _grants.Count;

        /// <summary>Who holds <paramref name="id"/> (<see cref="PeerId.None"/> if nobody).</summary>
        public PeerId TakenBy(string id) =>
            id != null && _taken.TryGetValue(id, out var taken) ? taken.By : PeerId.None;

        /// <summary>When <paramref name="id"/> was taken (for the stale-pickup check), or NaN.</summary>
        public double TakenAt(string id) =>
            id != null && _taken.TryGetValue(id, out var taken) ? taken.At : double.NaN;

        /// <summary>
        /// A client asks for an arbitrated interaction. Returns true (and records the grant) or false with a
        /// player-facing reason.
        /// </summary>
        /// <param name="available">The host still has the interactable, active, of the claimed type.</param>
        /// <param name="price">Purchases: the host's price for it (0 when unknown).</param>
        public bool TryGrant(PeerId peer, uint claimId, string id, ArbitrationKind kind, bool available, int balance,
            int price, Func<PeerId, string> nameOf, out string reason)
        {
            ExpireLock();
            if (!peer.IsValid || string.IsNullOrEmpty(id) || kind == ArbitrationKind.None)
                return Refuse("That cannot be claimed.", out reason);
            if (!available)
                return Refuse("That is not there any more.", out reason);
            var holder = TakenBy(id);
            if (holder.IsValid && holder != peer)
                return Refuse("Already taken by " + Name(nameOf, holder) + ".", out reason);
            if (kind == ArbitrationKind.Purchase)
            {
                if (_lockHolder.IsValid && _lockHolder != peer)
                    return Refuse(Name(nameOf, _lockHolder) + " is buying something; try again in a moment.", out reason);
                if (price > 0 && balance < price)
                    return Refuse("Not enough money.", out reason);
                _lockHolder = peer;
                _lockClaim = claimId;
                _lockId = id;
                _lockUntil = _clock() + PurchaseLockSeconds;
            }
            _taken[id] = new Taken(peer, _clock(), kind == ArbitrationKind.Purchase);
            _grants[new GrantKey(peer, claimId)] = new Grant(id, kind, _clock());
            reason = string.Empty;
            return true;
        }

        /// <summary>
        /// The host itself is about to run an arbitrated interaction. False (with a reason) when a client holds it
        /// or the purchase lock; otherwise purchases take the lock until <see cref="EndHostAction"/>.
        /// </summary>
        public bool TryBeginHostAction(PeerId host, string id, ArbitrationKind kind, Func<PeerId, string> nameOf,
            out string reason)
        {
            ExpireLock();
            var holder = TakenBy(id);
            if (holder.IsValid && holder != host)
                return Refuse("Already taken by " + Name(nameOf, holder) + ".", out reason);
            if (kind == ArbitrationKind.Purchase)
            {
                if (_lockHolder.IsValid && _lockHolder != host)
                    return Refuse(Name(nameOf, _lockHolder) + " is buying something; try again in a moment.", out reason);
                _lockHolder = host;
                _lockClaim = 0;
                _lockId = id;
                _lockUntil = _clock() + PurchaseLockSeconds;
            }
            reason = string.Empty;
            return true;
        }

        /// <summary>The host's own interaction returned. <paramref name="consumed"/>: the pickup is gone now.</summary>
        public void EndHostAction(PeerId host, string id, ArbitrationKind kind, bool consumed)
        {
            if (kind == ArbitrationKind.Purchase)
            {
                if (_lockHolder == host && _lockClaim == 0)
                    Settle();
                return;
            }
            if (consumed && !string.IsNullOrEmpty(id) && !TakenBy(id).IsValid)
                _taken[id] = new Taken(host, _clock(), false);
        }

        /// <summary>
        /// A client reports a money change made under a granted claim. Returns true with the amount the host should
        /// apply (the host's own value where it has one), or false with a reason for the log. Each granted
        /// interaction changes the purse once in the game: cash and the missing ball pay once, a purchase costs once
        /// (an unaffordable one makes a ChangeBalance(0), which clients never report).
        /// </summary>
        /// <param name="hostValue">CollectCash: the host interactable's <c>m_cashValue</c>; Purchase: its price
        /// (<c>m_cashValue</c>); 0 when unknown.</param>
        public bool TryAcceptBankReport(PeerId peer, uint claimId, int change, int balance, int hostValue,
            out int apply, out string reason)
        {
            apply = 0;
            var key = new GrantKey(peer, claimId);
            if (!_grants.TryGetValue(key, out var grant))
                return Refuse("no grant for this claim", out reason);
            if (grant.Reports >= MaxBankReportsPerClaim)
                return Refuse("too many money reports for one claim", out reason);
            grant.Reports++;
            switch (grant.Kind)
            {
                case ArbitrationKind.CollectCash:
                    if (grant.Paid)
                        return Refuse("cash was already paid for this claim", out reason);
                    if (change <= 0 || change > MaxPurchase)
                        return Refuse("cash pickup change out of range", out reason);
                    apply = hostValue > 0 ? hostValue : change;
                    break;
                case ArbitrationKind.FindMissingBall:
                    if (grant.Paid)
                        return Refuse("the missing ball was already paid for this claim", out reason);
                    if (change <= 0 || change > MaxMissingBallReward)
                        return Refuse("missing-ball reward out of range", out reason);
                    // The game's own fixed reward, whatever the client's game added.
                    apply = MissingBallReward;
                    break;
                case ArbitrationKind.Pickup:
                    // Crowbar, card, ticket: picking them up pays nothing. A change in the claimant's report window
                    // is something else (its own bounty reward, which the host pays when it accepts the bounty).
                    return Refuse("pickups pay nothing", out reason);
                case ArbitrationKind.Purchase:
                    if (grant.Paid)
                        return Refuse("this purchase was already paid", out reason);
                    if (change >= 0 || change < -MaxPurchase)
                        return Refuse("purchase change out of range", out reason);
                    // The host's price where it knows one (the same build charges the same).
                    int cost = hostValue > 0 && hostValue <= MaxPurchase ? -hostValue : change;
                    if ((long)balance + cost < 0)
                        return Refuse("purchase would overdraw the purse", out reason);
                    apply = cost;
                    if (_lockHolder == peer && _lockClaim == claimId)
                        Settle();
                    break;
                default:
                    return Refuse("unknown claim kind", out reason);
            }
            grant.Paid = true;
            reason = string.Empty;
            return true;
        }

        /// <summary>
        /// A paid one-off purchase (a club stand, the LMUGC ticket) is gone for good: it stays taken by the buyer like
        /// a pickup, so the buyer's deactivation of its object is accepted after the purchase lock is gone. The stale
        /// check frees it again should it still be there.
        /// </summary>
        public void KeepTaken(string id)
        {
            if (id != null && _taken.TryGetValue(id, out var taken))
                _taken[id] = new Taken(taken.By, _clock(), false);
        }

        /// <summary>The kind of a grant (None if unknown).</summary>
        public ArbitrationKind GrantKind(PeerId peer, uint claimId) =>
            _grants.TryGetValue(new GrantKey(peer, claimId), out var grant) ? grant.Kind : ArbitrationKind.None;

        /// <summary>Id of a granted claim, or null.</summary>
        public string GrantTarget(PeerId peer, uint claimId) =>
            _grants.TryGetValue(new GrantKey(peer, claimId), out var grant) ? grant.Id : null;

        /// <summary>Frees a pickup (it was never consumed) so others may take it.</summary>
        public void Release(string id)
        {
            if (id != null)
                _taken.Remove(id);
            if (id != null && id == _lockId)
                ClearLock();
        }

        /// <summary>Pickups taken longer than <paramref name="seconds"/> ago (to check they really are gone).</summary>
        public List<string> TakenPickupsOlderThan(double seconds)
        {
            var result = new List<string>();
            double now = _clock();
            foreach (var pair in _taken)
            {
                if (!pair.Value.Purchase && now - pair.Value.At >= seconds)
                    result.Add(pair.Key);
            }
            return result;
        }

        /// <summary>Re-stamps a taken pickup (it was checked and is gone); it stays taken.</summary>
        public void ConfirmTaken(string id)
        {
            if (id != null && _taken.TryGetValue(id, out var taken))
                _taken[id] = new Taken(taken.By, double.PositiveInfinity, taken.Purchase);
        }

        /// <summary>A player left: its purchase lock goes; its pickups stay taken.</summary>
        public void OnPlayerLeft(PeerId peer)
        {
            if (_lockHolder == peer)
                ClearLock();
            _expired.Clear();
            foreach (var key in _grants.Keys)
            {
                if (key.Peer == peer)
                    _expired.Add(key);
            }
            foreach (var key in _expired)
                _grants.Remove(key);
            _expired.Clear();
        }

        /// <summary>Expires the purchase lock and old grants. Call regularly.</summary>
        public void Tick()
        {
            ExpireLock();
            if (_grants.Count == 0)
                return;
            double now = _clock();
            _expired.Clear();
            foreach (var pair in _grants)
            {
                if (now - pair.Value.At > GrantLifetimeSeconds)
                    _expired.Add(pair.Key);
            }
            foreach (var key in _expired)
                _grants.Remove(key);
            _expired.Clear();
        }

        public void Clear()
        {
            _taken.Clear();
            _grants.Clear();
            ClearLock();
        }

        // Paid: the lock (and the bought item) stay with the buyer a little longer, then free up.
        private void Settle()
        {
            _lockUntil = _clock() + PurchaseSettleSeconds;
        }

        private void ExpireLock()
        {
            if (_lockHolder.IsValid && _clock() >= _lockUntil)
                ClearLock();
        }

        private void ClearLock()
        {
            // A purchase only holds its item while the lock is up.
            if (_lockId != null && _taken.TryGetValue(_lockId, out var taken) && taken.Purchase)
                _taken.Remove(_lockId);
            _lockHolder = PeerId.None;
            _lockClaim = 0;
            _lockId = null;
            _lockUntil = double.NegativeInfinity;
        }

        private static string Name(Func<PeerId, string> nameOf, PeerId peer)
        {
            if (nameOf == null)
                return peer.ToString();
            try
            {
                return nameOf(peer) ?? peer.ToString();
            }
            catch (Exception)
            {
                return peer.ToString();
            }
        }

        private static bool Refuse(string why, out string reason)
        {
            reason = why;
            return false;
        }

        private readonly struct Taken
        {
            public Taken(PeerId by, double at, bool purchase)
            {
                By = by;
                At = at;
                Purchase = purchase;
            }

            public readonly PeerId By;
            public readonly double At;
            public readonly bool Purchase;
        }

        private sealed class Grant
        {
            public Grant(string id, ArbitrationKind kind, double at)
            {
                Id = id;
                Kind = kind;
                At = at;
            }

            public readonly string Id;
            public readonly ArbitrationKind Kind;
            public readonly double At;
            public int Reports;

            /// <summary>The one money change this interaction makes in the game was accepted.</summary>
            public bool Paid;
        }

        private readonly struct GrantKey : IEquatable<GrantKey>
        {
            public GrantKey(PeerId peer, uint claimId)
            {
                Peer = peer;
                ClaimId = claimId;
            }

            public readonly PeerId Peer;
            public readonly uint ClaimId;

            public bool Equals(GrantKey other) => Peer == other.Peer && ClaimId == other.ClaimId;

            public override bool Equals(object obj) => obj is GrantKey other && Equals(other);

            public override int GetHashCode() => Peer.GetHashCode() * 397 ^ (int)ClaimId;
        }
    }
}
