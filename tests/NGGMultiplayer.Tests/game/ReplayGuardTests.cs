using NGGMultiplayer.Game;
using Xunit;

namespace NGGMultiplayer.Tests.Game
{
    public class ReplayGuardTests
    {
        private double _now = 100;

        private ReplayGuard Create() => new ReplayGuard(() => _now);

        [Fact]
        public void Scope_MarksReplaying_AndNests()
        {
            var guard = Create();
            Assert.False(guard.IsReplaying);
            using (guard.Begin("outer"))
            {
                Assert.True(guard.IsActive);
                using (guard.Begin("inner"))
                {
                    Assert.Equal(2, guard.Depth);
                    Assert.Equal("inner", guard.Reason);
                }
                Assert.Equal("outer", guard.Reason);
                Assert.True(guard.IsReplaying);
            }
            Assert.False(guard.IsActive);
        }

        [Fact]
        public void ClosingAScope_HoldsBriefly()
        {
            var guard = Create();
            using (guard.Begin("fact"))
            {
            }
            Assert.True(guard.IsReplaying);
            _now += ReplayGuard.ImplicitHoldSeconds + 0.001;
            Assert.False(guard.IsReplaying);
        }

        [Fact]
        public void HoldFor_ExtendsButNeverShortens()
        {
            var guard = Create();
            guard.HoldFor(2);
            guard.HoldFor(0.5);
            _now += 1;
            Assert.True(guard.IsReplaying);
            Assert.False(guard.IsActive);
            Assert.Equal(0, guard.QuietSeconds);
            _now += 1.5;
            Assert.False(guard.IsReplaying);
            Assert.Equal(0.5, guard.QuietSeconds, 6);
        }

        [Fact]
        public void OutOfOrderOrRepeatedDispose_NeverLeavesItStuck()
        {
            var guard = Create();
            var outer = guard.Begin("outer");
            var inner = guard.Begin("inner");
            outer.Dispose();
            Assert.False(guard.IsActive);
            inner.Dispose();
            outer.Dispose();
            Assert.False(guard.IsActive);
            using (guard.Begin("again"))
                Assert.Equal(1, guard.Depth);
        }

        [Fact]
        public void Reset_ClearsScopesAndHolds()
        {
            var guard = Create();
            guard.Begin("leaked");
            guard.HoldFor(30);
            guard.Reset();
            Assert.False(guard.IsReplaying);
            Assert.Equal(string.Empty, guard.Reason);
        }

        [Fact]
        public void QuietSeconds_IsLargeWhenNeverReplayed()
        {
            Assert.True(Create().QuietSeconds > 1e6);
        }
        [Fact]
        public void HoldWindows_DoNotCountAsActive()
        {
            // Local-player gates check IsActive: a plain hold after an unrelated replay must not swallow the
            // player's own interaction, money report or achievement.
            var guard = Create();
            using (guard.Begin("pg.bank.report"))
                Assert.True(guard.IsActive);
            guard.HoldFor(0.5);
            Assert.True(guard.IsReplaying);
            Assert.False(guard.IsActive);
            Assert.False(guard.InReplayEffects);
        }

        [Fact]
        public void EffectsHold_IsSeparateFromThePlainHold()
        {
            var guard = Create();
            using (guard.Begin("pg.track"))
                Assert.True(guard.InReplayEffects);
            Assert.False(guard.InReplayEffects);
            guard.HoldEffectsFor(1.0);
            Assert.True(guard.InReplayEffects);
            Assert.True(guard.IsReplaying);
            _now += 1.01;
            Assert.False(guard.InReplayEffects);
            Assert.False(guard.IsReplaying);
            guard.HoldEffectsFor(2);
            guard.Reset();
            Assert.False(guard.InReplayEffects);
        }
    }
}
