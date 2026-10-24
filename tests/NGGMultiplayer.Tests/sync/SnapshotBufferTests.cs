using System;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Sync;
using Xunit;

namespace NGGMultiplayer.Tests.Sync
{
    public class SnapshotBufferTests
    {
        private static double[] Times(SnapshotBuffer b)
        {
            var t = new double[b.Count];
            for (int i = 0; i < b.Count; i++)
                t[i] = b[i].Time;
            return t;
        }

        [Fact]
        public void Empty_SamplesNone()
        {
            var b = new SnapshotBuffer();
            Assert.Equal(SnapshotSampleKind.None, b.Sample(1.0, out var s));
            Assert.False(b.TryGetNewest(out _));
            Assert.Equal(default(TransformState).Time, s.Time);
        }

        [Fact]
        public void Interpolates_PositionAtMidpoint()
        {
            var b = new SnapshotBuffer();
            b.Add(States.At(0, time: 1.0));
            b.Add(States.At(10, 20, -4, time: 2.0));
            Assert.Equal(SnapshotSampleKind.Interpolated, b.Sample(1.5, out var s));
            Assert.Equal(5f, s.Position.x, 5);
            Assert.Equal(10f, s.Position.y, 5);
            Assert.Equal(-2f, s.Position.z, 5);
            Assert.Equal(1.5, s.Time, 9);

            Assert.Equal(SnapshotSampleKind.Interpolated, b.Sample(1.9, out s));
            Assert.Equal(9f, s.Position.x, 4);
        }

        [Fact]
        public void Interpolates_RotationWithSlerp()
        {
            var b = new SnapshotBuffer();
            var a = States.At(0, time: 0);
            var c = States.At(0, time: 1);
            c.Rotation = TransformMath.AxisAngle(new NetVector3(0, 1, 0), Math.PI);
            b.Add(a);
            b.Add(c);
            b.Sample(0.25, out var s);
            Assert.Equal(45.0, TransformMath.AngleDegrees(NetQuaternion.Identity, s.Rotation), 2);
        }

        [Fact]
        public void PicksTheRightSegmentAmongMany()
        {
            var b = new SnapshotBuffer();
            for (int i = 0; i < 10; i++)
                b.Add(States.At(i * i, time: i * 0.1));
            b.Sample(0.35, out var s);
            // Between x=9 (t=0.3) and x=16 (t=0.4).
            Assert.Equal(12.5f, s.Position.x, 3);
        }

        [Fact]
        public void ExactlyOnAState_ReturnsThatState()
        {
            var b = new SnapshotBuffer();
            b.Add(States.At(0, time: 1));
            b.Add(States.At(4, time: 2));
            b.Add(States.At(8, time: 3));
            Assert.Equal(SnapshotSampleKind.Interpolated, b.Sample(2.0, out var s));
            Assert.Equal(4f, s.Position.x);
            Assert.Equal(SnapshotSampleKind.Interpolated, b.Sample(3.0, out s));
            Assert.Equal(8f, s.Position.x);
        }

        [Fact]
        public void BeforeOldest_IsClamped()
        {
            var b = new SnapshotBuffer();
            b.Add(States.At(3, time: 5));
            b.Add(States.At(6, time: 6));
            Assert.Equal(SnapshotSampleKind.Clamped, b.Sample(1, out var s));
            Assert.Equal(3f, s.Position.x);
            Assert.Equal(2, b.Count);
        }

        [Fact]
        public void OutOfOrder_IsInsertedSorted()
        {
            var b = new SnapshotBuffer();
            foreach (double t in new[] { 0.3, 0.1, 0.5, 0.2, 0.4 })
                Assert.True(b.Add(States.At((float)t * 10, time: t)));
            Assert.Equal(new[] { 0.1, 0.2, 0.3, 0.4, 0.5 }, Times(b));
            b.Sample(0.25, out var s);
            Assert.Equal(2.5f, s.Position.x, 4);
        }

        [Fact]
        public void Duplicates_AreDropped()
        {
            var b = new SnapshotBuffer();
            Assert.True(b.Add(States.At(1, time: 1)));
            Assert.True(b.Add(States.At(2, time: 2)));
            Assert.False(b.Add(States.At(99, time: 1)));
            Assert.False(b.Add(States.At(99, time: 2)));
            Assert.Equal(2, b.Count);
            Assert.Equal(2, b.DroppedCount);
            b.TryGetNewest(out var n);
            Assert.Equal(2f, n.Position.x);
        }

        [Fact]
        public void InvalidStates_AreDropped()
        {
            var b = new SnapshotBuffer();
            Assert.False(b.Add(States.At(0, time: double.NaN)));
            Assert.False(b.Add(States.At(float.PositiveInfinity, time: 1)));
            Assert.Equal(0, b.Count);
        }

        [Fact]
        public void Sampling_PrunesConsumedStates_AndLateOldStatesAreDropped()
        {
            var b = new SnapshotBuffer();
            for (int i = 0; i <= 5; i++)
                b.Add(States.At(i, time: i));
            b.Sample(3.5, out _);
            // The anchor (t=3) stays, everything older is gone.
            Assert.Equal(new double[] { 3, 4, 5 }, Times(b));

            Assert.False(b.Add(States.At(-1, time: 2.5)));
            Assert.False(b.Add(States.At(-1, time: 0.5)));
            Assert.Equal(3, b.Count);

            // Late but still useful: newer than the anchor.
            Assert.True(b.Add(States.At(3.2f, time: 3.2)));
            Assert.Equal(new[] { 3, 3.2, 4, 5 }, Times(b));
            b.Sample(3.6, out var s);
            Assert.Equal(3.6f, s.Position.x, 4);
        }

        [Fact]
        public void OlderThanOldest_IsInsertedBeforeAnySampling()
        {
            var b = new SnapshotBuffer();
            b.Add(States.At(2, time: 2));
            Assert.True(b.Add(States.At(1, time: 1)));
            Assert.Equal(new double[] { 1, 2 }, Times(b));
        }

        [Fact]
        public void Full_EvictsOldest_AndRejectsOlderThanAll()
        {
            var b = new SnapshotBuffer(4);
            for (int i = 1; i <= 6; i++)
                Assert.True(b.Add(States.At(i, time: i)));
            Assert.Equal(new double[] { 3, 4, 5, 6 }, Times(b));
            Assert.False(b.Add(States.At(0, time: 0.5)));
            Assert.True(b.Add(States.At(0, time: 4.5)));
            Assert.Equal(new[] { 4, 4.5, 5, 6 }, Times(b));
        }

        [Fact]
        public void RingWraparound_KeepsOrder()
        {
            var b = new SnapshotBuffer(5);
            var rng = new Random(9);
            double t = 0;
            for (int i = 0; i < 500; i++)
            {
                t += 0.033;
                double jitter = (rng.NextDouble() - 0.5) * 0.08;
                b.Add(States.At((float)(t + jitter), time: t + jitter));
                if (i % 3 == 0)
                    b.Sample(t - 0.1, out _);
                for (int k = 1; k < b.Count; k++)
                    Assert.True(b[k - 1].Time < b[k].Time);
            }
        }

        [Fact]
        public void Extrapolates_WithinCap()
        {
            var b = new SnapshotBuffer();
            b.Add(States.Moving(0, 10, 1.0));
            b.Add(States.Moving(1, 10, 1.1));
            Assert.Equal(SnapshotSampleKind.Extrapolated, b.Sample(1.2, out var s));
            Assert.Equal(2f, s.Position.x, 4);
            Assert.Equal(1.2, s.Time, 9);
            Assert.Equal(1, b.Count);
        }

        [Fact]
        public void Extrapolation_IsCappedThenHolds()
        {
            var b = new SnapshotBuffer();
            b.Add(States.Moving(0, 10, 1.0));
            Assert.Equal(SnapshotSampleKind.Extrapolated, b.Sample(1.25, out var s));
            Assert.Equal(2.5f, s.Position.x, 4);

            Assert.Equal(SnapshotSampleKind.Held, b.Sample(1.3, out s));
            Assert.Equal(2.5f, s.Position.x, 4);
            Assert.Equal(SnapshotSampleKind.Held, b.Sample(100, out s));
            Assert.Equal(2.5f, s.Position.x, 4);
            Assert.Equal(1.25, s.Time, 9);
        }

        [Fact]
        public void Extrapolation_CapIsConfigurable()
        {
            var b = new SnapshotBuffer { MaxExtrapolation = 0.1 };
            b.Add(States.Moving(0, 10, 0));
            Assert.Equal(SnapshotSampleKind.Held, b.Sample(0.5, out var s));
            Assert.Equal(1f, s.Position.x, 4);

            b = new SnapshotBuffer { MaxExtrapolation = 0 };
            b.Add(States.Moving(0, 10, 0));
            Assert.Equal(SnapshotSampleKind.Held, b.Sample(0.05, out s));
            Assert.Equal(0f, s.Position.x);

            Assert.Throws<ArgumentOutOfRangeException>(() => b.MaxExtrapolation = -1);
            Assert.Throws<ArgumentOutOfRangeException>(() => b.MaxExtrapolation = double.NaN);
        }

        [Fact]
        public void Extrapolation_IntegratesAngularVelocity()
        {
            var b = new SnapshotBuffer();
            var s0 = States.At(0, time: 0);
            s0.AngularVelocity = new NetVector3(0, (float)Math.PI, 0);
            b.Add(s0);
            b.Sample(0.2, out var s);
            Assert.Equal(36.0, TransformMath.AngleDegrees(NetQuaternion.Identity, s.Rotation), 2);
        }

        [Fact]
        public void ExtrapolationResumesSmoothly_WhenNewStateArrives()
        {
            var b = new SnapshotBuffer();
            b.Add(States.Moving(0, 10, 0.0));
            b.Sample(0.1, out var s);
            Assert.Equal(1f, s.Position.x, 4);
            b.Add(States.Moving(1.5f, 10, 0.15));
            Assert.Equal(SnapshotSampleKind.Interpolated, b.Sample(0.12, out s));
            Assert.Equal(1.2f, s.Position.x, 4);
        }

        [Fact]
        public void Teleport_SnapsInsteadOfInterpolating()
        {
            var b = new SnapshotBuffer();
            b.Add(States.At(0, time: 1.0));
            var jump = States.At(100, time: 1.1);
            jump.Teleport = true;
            b.Add(jump);
            b.Add(States.At(101, time: 1.2));

            Assert.Equal(SnapshotSampleKind.Interpolated, b.Sample(1.05, out var s));
            Assert.Equal(0f, s.Position.x);
            Assert.Equal(SnapshotSampleKind.Interpolated, b.Sample(1.0999, out s));
            Assert.Equal(0f, s.Position.x);

            b.Sample(1.1, out s);
            Assert.Equal(100f, s.Position.x);
            Assert.True(s.Teleport);

            b.Sample(1.15, out s);
            Assert.Equal(100.5f, s.Position.x, 3);
        }

        [Fact]
        public void DiscreteFields_ComeFromTheStateAlreadyReached()
        {
            var b = new SnapshotBuffer();
            var a = States.At(0, time: 0);
            a.Flags = 1;
            var c = States.At(1, time: 1);
            c.Flags = 2;
            c.Extra = new byte[] { 7 };
            b.Add(a);
            b.Add(c);
            b.Sample(0.9, out var s);
            Assert.Equal(1, s.Flags);
            Assert.Null(s.Extra);
            b.Sample(1.0, out s);
            Assert.Equal(2, s.Flags);
            Assert.Equal(new byte[] { 7 }, s.Extra);
        }

        [Fact]
        public void NonFiniteRenderTime_HoldsNewest()
        {
            var b = new SnapshotBuffer();
            b.Add(States.At(1, time: 1));
            b.Add(States.At(2, time: 2));
            Assert.Equal(SnapshotSampleKind.Held, b.Sample(double.NaN, out var s));
            Assert.Equal(2f, s.Position.x);
            Assert.Equal(2, b.Count);
        }

        [Fact]
        public void Clear_ResetsEverything()
        {
            var b = new SnapshotBuffer();
            b.Add(States.At(1, time: 5));
            b.Sample(10, out _);
            b.Clear();
            Assert.Equal(0, b.Count);
            // After a clear, older times are accepted again.
            Assert.True(b.Add(States.At(1, time: 1)));
        }

        [Fact]
        public void Constructor_RejectsTinyCapacity()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new SnapshotBuffer(1));
        }

        [Fact]
        public void SteadyStream_WithJitterAndLoss_StaysCloseToTruth()
        {
            // Truth: x = 3t. States every 1/30 s arrive with jitter, some lost, some reordered.
            var b = new SnapshotBuffer();
            var rng = new Random(21);
            const double delay = 0.1;
            var pending = new System.Collections.Generic.List<(double Arrive, TransformState State)>();
            for (int frame = 0; frame < 600; frame++)
            {
                double now = frame / 60.0;
                if (frame % 2 == 0 && rng.NextDouble() > 0.1)
                {
                    var st = States.Moving((float)(3 * now), 3, now);
                    pending.Add((now + 0.02 + rng.NextDouble() * 0.04, st));
                }
                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    if (pending[i].Arrive <= now)
                    {
                        b.Add(pending[i].State);
                        pending.RemoveAt(i);
                    }
                }
                if (frame > 30 && b.Sample(now - delay, out var s) != SnapshotSampleKind.None)
                    Assert.True(Math.Abs(s.Position.x - 3 * (now - delay)) < 1e-3, $"frame {frame}: {s.Position.x} vs {3 * (now - delay)}");
            }
        }
    }
}
