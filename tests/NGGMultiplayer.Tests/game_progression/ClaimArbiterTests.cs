using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Game;
using NGGMultiplayer.Game.Progression;
using Xunit;

namespace NGGMultiplayer.Tests.GameProgression
{
    public class ClaimArbiterTests
    {
        private static readonly PeerId Host = new PeerId(1);
        private static readonly PeerId A = new PeerId(2);
        private static readonly PeerId B = new PeerId(3);

        private double _now = 100;

        private ClaimArbiter Arbiter() => new ClaimArbiter(() => _now);

        private static bool Grant(ClaimArbiter arbiter, PeerId peer, uint claim, string id, ArbitrationKind kind,
            int balance, int price) =>
            arbiter.TryGrant(peer, claim, id, kind, true, balance, price, p => "P" + p, out _);

        [Fact]
        public void Pickup_GoesToTheFirstClaimant()
        {
            var arbiter = Arbiter();
            Assert.True(Grant(arbiter, A, 1, "cash", ArbitrationKind.CollectCash, 0, 0));
            Assert.False(arbiter.TryGrant(B, 1, "cash", ArbitrationKind.CollectCash, true, 0, 0, p => "Bob", out string reason));
            Assert.Contains("Bob", reason);
            Assert.Equal(A, arbiter.TakenBy("cash"));
            Assert.True(Grant(arbiter, A, 2, "cash", ArbitrationKind.CollectCash, 0, 0));
            Assert.False(arbiter.TryGrant(A, 3, "gone", ArbitrationKind.Pickup, false, 0, 0, null, out _));
            Assert.False(arbiter.TryBeginHostAction(Host, "cash", ArbitrationKind.CollectCash, null, out _));
        }

        [Fact]
        public void CollectCash_PaysOnce_TheHostsValue()
        {
            var arbiter = Arbiter();
            Grant(arbiter, A, 7, "cash", ArbitrationKind.CollectCash, 0, 0);
            Assert.True(arbiter.TryAcceptBankReport(A, 7, 999, 0, 50, out int apply, out _));
            Assert.Equal(50, apply);
            Assert.False(arbiter.TryAcceptBankReport(A, 7, 50, 0, 50, out _, out _));
            Assert.False(arbiter.TryAcceptBankReport(B, 7, 50, 0, 50, out _, out _));
            Assert.False(arbiter.TryAcceptBankReport(A, 8, 50, 0, 50, out _, out _));
        }

        [Fact]
        public void Purchase_LocksOthers_AndCannotOverdraw()
        {
            var arbiter = Arbiter();
            Assert.False(Grant(arbiter, A, 1, "shop1", ArbitrationKind.Purchase, 10, 20));
            Assert.True(Grant(arbiter, A, 2, "shop1", ArbitrationKind.Purchase, 30, 20));
            Assert.True(arbiter.IsPurchaseLocked);
            Assert.False(Grant(arbiter, B, 1, "shop2", ArbitrationKind.Purchase, 100, 20));
            Assert.False(arbiter.TryBeginHostAction(Host, "shop2", ArbitrationKind.Purchase, null, out _));
            Assert.True(Grant(arbiter, B, 2, "cash", ArbitrationKind.CollectCash, 0, 0));

            Assert.False(arbiter.TryAcceptBankReport(A, 2, 5, 30, 0, out _, out _));
            Assert.False(arbiter.TryAcceptBankReport(A, 2, -40, 30, 0, out _, out _));
            Assert.True(arbiter.TryAcceptBankReport(A, 2, -20, 30, 0, out int apply, out _));
            Assert.Equal(-20, apply);
            // Paid, but what was bought is not on the host yet: the lock settles before anyone else may buy.
            Assert.True(arbiter.IsPurchaseLocked);
            Assert.False(Grant(arbiter, B, 3, "shop2", ArbitrationKind.Purchase, 100, 20));
            _now += ClaimArbiter.PurchaseSettleSeconds + 0.1;
            Assert.True(Grant(arbiter, B, 4, "shop2", ArbitrationKind.Purchase, 100, 20));
            Assert.False(arbiter.TakenBy("shop1").IsValid);
        }

        [Fact]
        public void PurchaseLock_Expires()
        {
            var arbiter = Arbiter();
            Assert.True(Grant(arbiter, A, 1, "shop1", ArbitrationKind.Purchase, 30, 20));
            _now += ClaimArbiter.PurchaseLockSeconds + 0.1;
            Assert.True(Grant(arbiter, B, 1, "shop1", ArbitrationKind.Purchase, 30, 20));
            Assert.Equal(B, arbiter.PurchaseLockHolder);
        }

        [Fact]
        public void HostPurchase_HoldsTheLockUntilItEnds()
        {
            var arbiter = Arbiter();
            Assert.True(arbiter.TryBeginHostAction(Host, "shop1", ArbitrationKind.Purchase, null, out _));
            Assert.False(Grant(arbiter, A, 1, "shop2", ArbitrationKind.Purchase, 100, 1));
            arbiter.EndHostAction(Host, "shop1", ArbitrationKind.Purchase, true);
            Assert.False(Grant(arbiter, A, 2, "shop2", ArbitrationKind.Purchase, 100, 1));
            // The host may buy again at once; others after the settle time.
            Assert.True(arbiter.TryBeginHostAction(Host, "shop1", ArbitrationKind.Purchase, null, out _));
            arbiter.EndHostAction(Host, "shop1", ArbitrationKind.Purchase, true);
            _now += ClaimArbiter.PurchaseSettleSeconds + 0.1;
            Assert.True(Grant(arbiter, A, 3, "shop2", ArbitrationKind.Purchase, 100, 1));
        }

        [Fact]
        public void HostPickup_IsRecordedWhenConsumed()
        {
            var arbiter = Arbiter();
            Assert.True(arbiter.TryBeginHostAction(Host, "cash", ArbitrationKind.CollectCash, null, out _));
            arbiter.EndHostAction(Host, "cash", ArbitrationKind.CollectCash, true);
            Assert.Equal(Host, arbiter.TakenBy("cash"));
            Assert.False(Grant(arbiter, A, 1, "cash", ArbitrationKind.CollectCash, 0, 0));
        }

        [Fact]
        public void StalePickups_CanBeReleasedOrConfirmed()
        {
            var arbiter = Arbiter();
            Grant(arbiter, A, 1, "p1", ArbitrationKind.Pickup, 0, 0);
            Grant(arbiter, A, 2, "p2", ArbitrationKind.Pickup, 0, 0);
            Assert.Empty(arbiter.TakenPickupsOlderThan(15));
            _now += 20;
            Assert.Equal(2, arbiter.TakenPickupsOlderThan(15).Count);
            arbiter.Release("p1");
            arbiter.ConfirmTaken("p2");
            Assert.Empty(arbiter.TakenPickupsOlderThan(15));
            Assert.False(arbiter.TakenBy("p1").IsValid);
            Assert.Equal(A, arbiter.TakenBy("p2"));
        }

        [Fact]
        public void PlayerLeaving_DropsLockAndGrants_KeepsPickups()
        {
            var arbiter = Arbiter();
            Grant(arbiter, A, 1, "shop", ArbitrationKind.Purchase, 10, 1);
            Grant(arbiter, A, 2, "cash", ArbitrationKind.CollectCash, 0, 0);
            arbiter.OnPlayerLeft(A);
            Assert.False(arbiter.IsPurchaseLocked);
            Assert.Equal(0, arbiter.GrantCount);
            Assert.Equal(A, arbiter.TakenBy("cash"));
            Assert.False(arbiter.TakenBy("shop").IsValid);
        }

        [Fact]
        public void Grants_ExpireOnTick()
        {
            var arbiter = Arbiter();
            Grant(arbiter, A, 1, "ball", ArbitrationKind.FindMissingBall, 0, 0);
            Assert.Equal(ArbitrationKind.FindMissingBall, arbiter.GrantKind(A, 1));
            Assert.Equal("ball", arbiter.GrantTarget(A, 1));
            Assert.False(arbiter.TryAcceptBankReport(A, 1, ClaimArbiter.MaxMissingBallReward + 1, 0, 0, out _, out _));
            _now += ClaimArbiter.GrantLifetimeSeconds + 1;
            arbiter.Tick();
            Assert.Equal(ArbitrationKind.None, arbiter.GrantKind(A, 1));
            Assert.False(arbiter.TryAcceptBankReport(A, 1, 10, 0, 0, out _, out _));
        }

        [Fact]
        public void BankReports_AreCappedPerClaim()
        {
            var arbiter = Arbiter();
            Grant(arbiter, A, 1, "shop", ArbitrationKind.Purchase, 100, 1);
            for (int i = 0; i < ClaimArbiter.MaxBankReportsPerClaim; i++)
                Assert.False(arbiter.TryAcceptBankReport(A, 1, 5, 100, 0, out _, out _));
            // Even a valid one: the cap is reached.
            Assert.False(arbiter.TryAcceptBankReport(A, 1, -1, 100, 0, out _, out _));
        }

        [Fact]
        public void Purchase_CostsOnce_TheHostsPrice()
        {
            // One ChangeBalance(-m_cashValue) per affordable purchase (answers 138); the host's own price wins.
            var arbiter = Arbiter();
            Assert.True(Grant(arbiter, A, 1, "ticket", ArbitrationKind.Purchase, 500, 200));
            Assert.True(arbiter.TryAcceptBankReport(A, 1, -999, 500, 200, out int apply, out _));
            Assert.Equal(-200, apply);
            Assert.False(arbiter.TryAcceptBankReport(A, 1, -200, 300, 200, out _, out _));
        }

        [Fact]
        public void Purchase_TheHostsPriceMayNotOverdraw()
        {
            var arbiter = Arbiter();
            Assert.True(Grant(arbiter, A, 1, "club", ArbitrationKind.Purchase, 500, 0));
            Assert.False(arbiter.TryAcceptBankReport(A, 1, -10, 100, 200, out _, out _));
            // A refused report is not the payment: the right one still goes through.
            Assert.True(arbiter.TryAcceptBankReport(A, 1, -10, 300, 200, out int apply, out _));
            Assert.Equal(-200, apply);
        }

        [Fact]
        public void OneOffPurchase_StaysTakenByTheBuyer()
        {
            var arbiter = Arbiter();
            Assert.True(Grant(arbiter, A, 1, "ticket", ArbitrationKind.Purchase, 500, 200));
            Assert.True(arbiter.TryAcceptBankReport(A, 1, -200, 500, 200, out _, out _));
            arbiter.KeepTaken("ticket");
            _now += ClaimArbiter.PurchaseLockSeconds + ClaimArbiter.PurchaseSettleSeconds + 1;
            arbiter.Tick();
            Assert.False(arbiter.IsPurchaseLocked);
            // Its object's deactivation is still the buyer's to report; others are told it is taken.
            Assert.Equal(A, arbiter.TakenBy("ticket"));
            Assert.False(Grant(arbiter, B, 1, "ticket", ArbitrationKind.Purchase, 500, 200));
            Assert.Contains("ticket", arbiter.TakenPickupsOlderThan(0));
        }

        [Fact]
        public void MissingBall_PaysTheGamesFixedReward()
        {
            // FindMissingBall adds a hardcoded 1000, not m_cashValue (answers 92).
            var arbiter = Arbiter();
            Grant(arbiter, A, 1, "ball", ArbitrationKind.FindMissingBall, 0, 0);
            Assert.True(arbiter.TryAcceptBankReport(A, 1, 1500, 0, 0, out int apply, out _));
            Assert.Equal(ClaimArbiter.MissingBallReward, apply);
        }

        [Theory]
        [InlineData("CollectCash", ArbitrationKind.CollectCash)]
        [InlineData("FindMissingBall", ArbitrationKind.FindMissingBall)]
        [InlineData("GetCrowbar", ArbitrationKind.Pickup)]
        [InlineData("GetCard", ArbitrationKind.None)]
        [InlineData("LMUGCTicket", ArbitrationKind.Purchase)]
        [InlineData("BuyClub", ArbitrationKind.Purchase)]
        [InlineData("BuyMulligan", ArbitrationKind.Purchase)]
        [InlineData("BuyNoWind", ArbitrationKind.Purchase)]
        [InlineData("BuyHoleSkip", ArbitrationKind.Purchase)]
        [InlineData("Padlock", ArbitrationKind.None)]
        [InlineData("TalkToLuke", ArbitrationKind.None)]
        public void Interactions_AreArbitratedAsTheGameHandlesThem(string type, ArbitrationKind kind)
        {
            // Answers 92, 137, 138: the ticket costs money; GetCard has no handler.
            Assert.Equal(kind, ArbitrationKinds.ForInteraction(type));
        }

        [Fact]
        public void OneOffPurchases()
        {
            Assert.True(ArbitrationKinds.IsOneOffPurchase("BuyClub"));
            Assert.True(ArbitrationKinds.IsOneOffPurchase("LMUGCTicket"));
            Assert.False(ArbitrationKinds.IsOneOffPurchase("BuyMulligan"));
            Assert.False(ArbitrationKinds.IsOneOffPurchase("CollectCash"));
        }

        [Fact]
        public void Pickups_PayNothing_AndTheMissingBallPaysOnce()
        {
            // Regression: a client's own bounty reward inside the pickup's report window was paid into the purse
            // under the pickup claim, and again when the host accepted the bounty.
            var arbiter = Arbiter();
            Grant(arbiter, A, 1, "crowbar", ArbitrationKind.Pickup, 0, 0);
            Assert.False(arbiter.TryAcceptBankReport(A, 1, 500, 0, 0, out _, out _));
            Assert.False(arbiter.TryAcceptBankReport(A, 1, -5, 0, 0, out _, out _));

            Grant(arbiter, A, 2, "ball", ArbitrationKind.FindMissingBall, 0, 0);
            Assert.True(arbiter.TryAcceptBankReport(A, 2, 1000, 0, 0, out int apply, out _));
            Assert.Equal(1000, apply);
            Assert.False(arbiter.TryAcceptBankReport(A, 2, 500, 0, 0, out _, out _));
        }

        [Fact]
        public void ArbitrationKinds_Pickups()
        {
            Assert.True(ArbitrationKinds.IsPickup(ArbitrationKind.CollectCash));
            Assert.False(ArbitrationKinds.IsPickup(ArbitrationKind.Purchase));
            Assert.False(ArbitrationKinds.IsPickup(ArbitrationKind.None));
        }
    }
}
