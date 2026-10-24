using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Game;
using NGGMultiplayer.Game.Progression;
using Xunit;

namespace NGGMultiplayer.Tests.GameProgression
{
    public class ClientClaimsTests
    {
        private double _now = 10;

        [Fact]
        public void Claims_AreDeduplicatedPerTarget()
        {
            var tracker = new ClientClaimTracker(() => _now);
            uint a = tracker.Begin("cash");
            Assert.NotEqual(0u, a);
            Assert.Equal(a, tracker.Begin("cash"));
            uint b = tracker.Begin("shop");
            Assert.NotEqual(a, b);
            Assert.True(tracker.IsPending("cash"));
            Assert.True(tracker.TryComplete(a, out string id));
            Assert.Equal("cash", id);
            Assert.False(tracker.TryComplete(a, out _));
            Assert.False(tracker.IsPending("cash"));
            Assert.Equal(1, tracker.PendingCount);
        }

        [Fact]
        public void PendingClaims_TimeOut()
        {
            var tracker = new ClientClaimTracker(() => _now);
            tracker.Begin("cash");
            Assert.Empty(tracker.ExpirePending());
            _now += ClientClaimTracker.PendingTimeoutSeconds + 0.1;
            Assert.Equal(new[] { "cash" }, tracker.ExpirePending());
            Assert.Equal(0, tracker.PendingCount);
        }

        [Fact]
        public void Grants_AreUsedOnce_AndExpire()
        {
            var tracker = new ClientClaimTracker(() => _now);
            tracker.AddGrant("cash", 5);
            Assert.True(tracker.TryConsumeGrant("cash", out uint claim));
            Assert.Equal(5u, claim);
            Assert.False(tracker.TryConsumeGrant("cash", out _));

            tracker.AddGrant("shop", 6);
            _now += ClientClaimTracker.GrantUseSeconds + 0.1;
            Assert.False(tracker.TryConsumeGrant("shop", out _));
        }

        [Fact]
        public void Scope_CoversTheCallAndItsTail()
        {
            var tracker = new ClientClaimTracker(() => _now);
            Assert.False(tracker.TryGetScope(out _));
            tracker.OpenScope(9);
            Assert.True(tracker.TryGetScope(out uint claim));
            Assert.Equal(9u, claim);
            tracker.CloseScope();
            _now += ClientClaimTracker.ScopeTailSeconds - 0.5;
            Assert.True(tracker.TryGetScope(out _));
            _now += 1;
            Assert.False(tracker.TryGetScope(out _));
            tracker.Clear();
            Assert.False(tracker.TryGetScope(out _));
        }

        [Fact]
        public void Scope_ReportsOnlyTheInteractionsOwnMoney()
        {
            var tracker = new ClientClaimTracker(() => _now);
            tracker.OpenScope(1, ArbitrationKind.CollectCash);
            Assert.True(tracker.TryTakeReport(50, out uint claim));
            Assert.Equal(1u, claim);
            tracker.CloseScope();
            // The player's own bounty reward a moment later is not the pickup's.
            Assert.False(tracker.TryTakeReport(300, out _));

            tracker.OpenScope(2, ArbitrationKind.Pickup);
            Assert.False(tracker.TryTakeReport(300, out _));
            tracker.CloseScope();

            tracker.OpenScope(3, ArbitrationKind.Purchase);
            Assert.False(tracker.TryTakeReport(300, out _));
            Assert.True(tracker.TryTakeReport(-200, out claim));
            Assert.Equal(3u, claim);
            Assert.False(tracker.TryTakeReport(0, out _));
            tracker.CloseScope();
            _now += ClientClaimTracker.ScopeTailSeconds + 0.1;
            Assert.False(tracker.TryTakeReport(-200, out _));
        }

        [Fact]
        public void Baseline_MatchesEquivalentValues_AndTracksLocalChanges()
        {
            var baseline = new FactBaseline();
            var obj = FactCodec.EncodeObj(new ObjState(true, true, new NetQuaternion(0, 0, 0, 1)));
            Assert.True(baseline.Set(FactKinds.Obj, "door", obj));
            Assert.True(baseline.Matches(FactKinds.Obj, "door",
                FactCodec.EncodeObj(new ObjState(true, true, new NetQuaternion(0, 0, 0, -1)))));
            Assert.False(baseline.Matches(FactKinds.Obj, "other", obj));
            Assert.False(baseline.Set("", "k", obj));

            baseline.MarkLocalChange(FactKinds.Obj, "door", 10);
            Assert.True(baseline.ChangedWithin(FactKinds.Obj, "door", 12, 5));
            Assert.False(baseline.ChangedWithin(FactKinds.Obj, "door", 16, 5));

            var live = new FactTable();
            live.Set(FactKinds.Track, FactKinds.TrackKey, new byte[] { 1 });
            baseline.ResetTo(live);
            Assert.Null(baseline.Get(FactKinds.Obj, "door"));
            Assert.Equal(1, baseline.Count);
            Assert.True(baseline.ChangedWithin(FactKinds.Obj, "door", 12, 5));
            baseline.Clear();
            Assert.Equal(0, baseline.Count);
        }

        [Fact]
        public void Sampler_WaitsForStillness()
        {
            var sampler = new StabilitySampler();
            var a = new ObjState(true, true, new NetQuaternion(0, 0, 0, 1));
            var b = new ObjState(true, true, new NetQuaternion(0, 0.3826834f, 0, 0.9238795f));
            Assert.False(sampler.IsStable("door", a, 0, 0.8));
            Assert.False(sampler.IsStable("door", a, 0.5, 0.8));
            Assert.True(sampler.IsStable("door", a, 0.9, 0.8));
            Assert.False(sampler.IsStable("door", b, 1.0, 0.8));
            Assert.True(sampler.IsStable("door", b, 1.9, 0.8));
            sampler.Forget("door");
            Assert.False(sampler.IsStable("door", b, 2.0, 0.8));
        }

        [Fact]
        public void RateLimiter_OnePerIntervalPerPeer()
        {
            var limiter = new PeerRateLimiter(() => _now, 10);
            Assert.True(limiter.TryTake(new PeerId(2)));
            Assert.False(limiter.TryTake(new PeerId(2)));
            Assert.True(limiter.TryTake(new PeerId(3)));
            _now += 10;
            Assert.True(limiter.TryTake(new PeerId(2)));
        }
    }
}
