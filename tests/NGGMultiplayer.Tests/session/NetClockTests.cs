using System;
using NGGMultiplayer.Core.Session;
using Xunit;

namespace NGGMultiplayer.Tests.SessionTests
{
    public class NetClockTests
    {
        // Simulates one exchange: local clock reads `local`, remote = local + offset, symmetric one-way delay.
        private static bool Exchange(NetClock clock, double local, double offset, double oneWayOut, double oneWayBack)
            => clock.AddSample(local, local + oneWayOut + offset, local + oneWayOut + oneWayBack);

        [Fact]
        public void FirstSample_SetsRttAndOffsetExactly()
        {
            var clock = new NetClock();
            Assert.False(clock.HasSamples);
            Assert.Equal(0, clock.Offset);

            Assert.True(Exchange(clock, 10, offset: 5, oneWayOut: 0.04, oneWayBack: 0.04));
            Assert.Equal(1, clock.SampleCount);
            Assert.Equal(0.08, clock.Rtt, 9);
            Assert.Equal(0.08, clock.LastRtt, 9);
            Assert.Equal(0.08, clock.MinRtt, 9);
            Assert.Equal(5, clock.Offset, 9);
            Assert.Equal(80, clock.RttMs);
            Assert.Equal(15.5, clock.ToRemoteTime(10.5), 9);
            Assert.Equal(10.5, clock.ToLocalTime(clock.ToRemoteTime(10.5)), 9);
        }

        [Fact]
        public void Rtt_IsExponentiallySmoothed()
        {
            var clock = new NetClock(rttSmoothing: 0.5, offsetSmoothing: 0.1);
            Exchange(clock, 0, 0, 0.05, 0.05);
            Exchange(clock, 1, 0, 0.15, 0.15);
            Assert.Equal(0.2, clock.Rtt, 9); // 0.1 + 0.5 * (0.3 - 0.1)
            Assert.Equal(0.3, clock.LastRtt, 9);
            Assert.Equal(0.1, clock.MinRtt, 9);
            Assert.True(clock.RttVariance > 0);

            for (int i = 0; i < 60; i++)
                Exchange(clock, 2 + i, 0, 0.15, 0.15);
            Assert.Equal(0.3, clock.Rtt, 6);
        }

        [Fact]
        public void ImpossibleSamples_AreRejected()
        {
            var clock = new NetClock();
            Assert.False(clock.AddSample(10, 0, 9)); // negative RTT
            Assert.False(clock.AddSample(double.NaN, 0, 1));
            Assert.False(clock.AddSample(0, double.PositiveInfinity, 1));
            Assert.False(clock.AddSample(0, 0, double.NegativeInfinity));
            Assert.False(clock.AddSample(0, 0, NetClock.DefaultMaxRttSeconds + 1));
            Assert.False(clock.HasSamples);
            Assert.True(clock.AddSample(0, 0, 0)); // zero RTT is fine
        }

        [Fact]
        public void Offset_ConvergesUnderAsymmetricNoise()
        {
            var clock = new NetClock();
            var random = new Random(1);
            for (int i = 0; i < 400; i++)
            {
                double outbound = 0.03 + random.NextDouble() * 0.02;
                double back = 0.03 + random.NextDouble() * 0.02;
                Exchange(clock, i, offset: -42.5, outbound, back);
            }
            Assert.InRange(clock.Offset, -42.5 - 0.004, -42.5 + 0.004);
            Assert.InRange(clock.Rtt, 0.07, 0.09);
        }

        [Fact]
        public void SlowSample_HasReducedInfluenceOnOffset()
        {
            var clock = new NetClock();
            for (int i = 0; i < 10; i++)
                Exchange(clock, i, 0, 0.05, 0.05);

            // 0.9 s queued on the way out: its offset estimate is 0.4 s off but within ±rtt/2, so no snap.
            Exchange(clock, 20, 0, 0.9, 0.1);
            Assert.InRange(clock.Offset, 0, 0.005);
        }

        [Fact]
        public void HitchDelayedFirstSample_IsReplacedByTheNextFastExchange()
        {
            var clock = new NetClock();
            // The first ping waited 1.5 s locally before it left: RTT inflated, offset off by about +0.75 s.
            Assert.True(clock.AddSample(0, 1.5 + 0.02 + 10, 1.5 + 0.04));
            Assert.InRange(clock.Offset, 10.7, 10.8);

            Exchange(clock, 2, offset: 10, oneWayOut: 0.02, oneWayBack: 0.02);
            Assert.Equal(10, clock.Offset, 9);
            Assert.Equal(0.04, clock.Rtt, 9);
            Assert.Equal(0.04, clock.MinRtt, 9);
        }

        [Fact]
        public void FasterSample_AfterWarmup_IsBlendedNotReplaced()
        {
            var clock = new NetClock();
            for (int i = 0; i < NetClock.WarmupSamples; i++)
                Exchange(clock, i, 0, 0.05, 0.05);
            Exchange(clock, 20, offset: 0.2, oneWayOut: 0.01, oneWayBack: 0.01);
            Assert.InRange(clock.Offset, 0.001, 0.1);
        }

        [Fact]
        public void StallDelayedSample_DoesNotMoveOffset()
        {
            var clock = new NetClock();
            for (int i = 0; i < 10; i++)
                Exchange(clock, i, 0, 0.02, 0.02);
            // Queued 3 s behind a frame hitch on the way out; its offset estimate is 1.5 s off, but within ±rtt/2.
            Exchange(clock, 20, 0, 3.0, 0.02);
            Assert.Equal(0, clock.Offset, 9);
        }

        [Fact]
        public void LargeClockJump_Snaps()
        {
            var clock = new NetClock();
            for (int i = 0; i < 10; i++)
                Exchange(clock, i, 0, 0.05, 0.05);
            Exchange(clock, 20, offset: 30, 0.05, 0.05);
            Assert.Equal(30, clock.Offset, 9);
        }

        [Fact]
        public void Reset_ClearsEverything()
        {
            var clock = new NetClock();
            Exchange(clock, 0, 3, 0.1, 0.1);
            clock.Reset();
            Assert.False(clock.HasSamples);
            Assert.Equal(0, clock.Rtt);
            Assert.Equal(0, clock.Offset);
            Assert.Equal(0, clock.MinRtt);
        }

        [Fact]
        public void RttMs_IsClampedForDisplay()
        {
            var clock = new NetClock(0.5, 0.5, maxRttSeconds: 100);
            clock.AddSample(0, 0, 99);
            Assert.Equal(ushort.MaxValue, clock.RttMs);
        }

        [Fact]
        public void Constructor_ValidatesArguments()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new NetClock(0, 0.1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new NetClock(1.5, 0.1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new NetClock(0.1, double.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => new NetClock(0.1, 0.1, maxRttSeconds: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new NetClock(0.1, 0.1, snapThresholdSeconds: double.PositiveInfinity));
        }
    }
}
