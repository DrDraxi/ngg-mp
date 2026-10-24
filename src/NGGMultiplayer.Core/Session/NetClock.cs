using System;

namespace NGGMultiplayer.Core.Session
{
    /// <summary>
    /// Round-trip time and remote-clock offset estimated from ping/pong timestamps.
    /// A sample is (local send time, remote time when answering, local receive time); all in seconds.
    /// RTT is an EWMA with RFC 6298-style mean deviation. The offset (remote minus local) assumes symmetric
    /// paths, so each sample is accurate to ±RTT/2; samples slower than the smoothed RTT get less weight,
    /// and a sample proving the estimate wrong by more than the snap threshold replaces it outright.
    /// <para>
    /// The first sample can be distorted by a frame hitch on either side, so during the first
    /// <see cref="WarmupSamples"/> samples any faster exchange replaces the estimate instead of being blended into
    /// it. Later samples far slower than the smoothed RTT (queued behind a hitch) do not move the offset at all.
    /// </para>
    /// </summary>
    public sealed class NetClock
    {
        public const double DefaultRttSmoothing = 0.125;
        public const double DefaultOffsetSmoothing = 0.1;
        public const double DefaultMaxRttSeconds = 10.0;
        public const double DefaultSnapThresholdSeconds = 1.0;

        /// <summary>Samples during which a faster exchange replaces the estimate outright.</summary>
        public const int WarmupSamples = 5;

        private const double RttVarianceSmoothing = 0.25;
        // A sample slower than 2 x smoothed RTT + this is treated as delayed by a stall, not by the network.
        private const double OutlierMarginSeconds = 0.05;
        private const double MinOffsetWeightFactor = 0.1;
        // Keeps the weight ratio sane when RTTs are ~0 (loopback).
        private const double RttFloorSeconds = 0.001;

        private readonly double _rttAlpha;
        private readonly double _offsetAlpha;
        private readonly double _maxRtt;
        private readonly double _snapThreshold;

        public NetClock()
            : this(DefaultRttSmoothing, DefaultOffsetSmoothing)
        {
        }

        public NetClock(double rttSmoothing, double offsetSmoothing,
            double maxRttSeconds = DefaultMaxRttSeconds, double snapThresholdSeconds = DefaultSnapThresholdSeconds)
        {
            if (double.IsNaN(rttSmoothing) || rttSmoothing <= 0 || rttSmoothing > 1)
                throw new ArgumentOutOfRangeException(nameof(rttSmoothing), rttSmoothing, "Must be in (0, 1].");
            if (double.IsNaN(offsetSmoothing) || offsetSmoothing <= 0 || offsetSmoothing > 1)
                throw new ArgumentOutOfRangeException(nameof(offsetSmoothing), offsetSmoothing, "Must be in (0, 1].");
            if (!IsFinite(maxRttSeconds) || maxRttSeconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxRttSeconds), maxRttSeconds, "Must be finite and positive.");
            if (!IsFinite(snapThresholdSeconds) || snapThresholdSeconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(snapThresholdSeconds), snapThresholdSeconds, "Must be finite and positive.");
            _rttAlpha = rttSmoothing;
            _offsetAlpha = offsetSmoothing;
            _maxRtt = maxRttSeconds;
            _snapThreshold = snapThresholdSeconds;
        }

        public int SampleCount { get; private set; }

        public bool HasSamples => SampleCount > 0;

        /// <summary>Smoothed round-trip time in seconds (0 before the first sample).</summary>
        public double Rtt { get; private set; }

        /// <summary>Smoothed mean deviation of the RTT in seconds.</summary>
        public double RttVariance { get; private set; }

        public double LastRtt { get; private set; }

        public double MinRtt { get; private set; }

        /// <summary>Estimated remote time minus local time, in seconds (0 before the first sample).</summary>
        public double Offset { get; private set; }

        public int RttMs => ToMilliseconds(Rtt);

        public double ToRemoteTime(double localTime) => localTime + Offset;

        public double ToLocalTime(double remoteTime) => remoteTime - Offset;

        /// <summary>Feeds one ping/pong exchange. Returns false (and changes nothing) for an impossible sample.</summary>
        public bool AddSample(double localSendTime, double remoteTime, double localReceiveTime)
        {
            if (!IsFinite(localSendTime) || !IsFinite(remoteTime) || !IsFinite(localReceiveTime))
                return false;
            double rtt = localReceiveTime - localSendTime;
            if (rtt < 0 || rtt > _maxRtt)
                return false;

            double sampleOffset = remoteTime + rtt * 0.5 - localReceiveTime;
            if (!IsFinite(sampleOffset))
                return false;

            if (SampleCount == 0 || (SampleCount < WarmupSamples && rtt < MinRtt))
            {
                // A tighter exchange bounds the offset more closely than everything before it.
                Rtt = rtt;
                RttVariance = rtt * 0.5;
                MinRtt = rtt;
                Offset = sampleOffset;
            }
            else
            {
                double diff = sampleOffset - Offset;
                double absDiff = Math.Abs(diff);
                if (absDiff > _snapThreshold && absDiff > rtt)
                {
                    // The true offset lies within ±rtt/2 of this sample, so the old estimate is definitely stale.
                    Offset = sampleOffset;
                }
                else if (rtt <= 2 * Rtt + OutlierMarginSeconds)
                {
                    double factor = (Rtt + RttFloorSeconds) / (rtt + RttFloorSeconds);
                    if (factor > 1) factor = 1;
                    else if (factor < MinOffsetWeightFactor) factor = MinOffsetWeightFactor;
                    Offset += _offsetAlpha * factor * diff;
                }

                RttVariance += RttVarianceSmoothing * (Math.Abs(Rtt - rtt) - RttVariance);
                Rtt += _rttAlpha * (rtt - Rtt);
                if (rtt < MinRtt)
                    MinRtt = rtt;
            }

            LastRtt = rtt;
            if (SampleCount < int.MaxValue)
                SampleCount++;
            return true;
        }

        public void Reset()
        {
            SampleCount = 0;
            Rtt = 0;
            RttVariance = 0;
            LastRtt = 0;
            MinRtt = 0;
            Offset = 0;
        }

        internal static int ToMilliseconds(double seconds)
        {
            double ms = Math.Round(seconds * 1000.0);
            if (!(ms > 0)) return 0;
            return ms >= ushort.MaxValue ? ushort.MaxValue : (int)ms;
        }

        private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
    }
}
