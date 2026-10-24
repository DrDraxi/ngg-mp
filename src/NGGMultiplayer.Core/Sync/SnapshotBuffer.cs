using System;

namespace NGGMultiplayer.Core.Sync
{
    /// <summary>
    /// Time-sorted ring of received states for one remote entity, sampled at a delayed render time.
    /// <list type="bullet">
    /// <item>Out-of-order states are inserted in place; a state with a time already held is a duplicate and dropped.</item>
    /// <item>Sampling discards every state older than the one just before the render time, and states that
    /// arrive older than both that state and the render time are dropped as too old.</item>
    /// <item>A full buffer evicts its oldest state to make room for a newer one.</item>
    /// <item>Into a state flagged <see cref="TransformState.Teleport"/> there is no interpolation: the previous
    /// pose holds until the teleport's time, then the result snaps.</item>
    /// <item>Past the newest state the pose is extrapolated from its velocities for up to
    /// <see cref="MaxExtrapolation"/> seconds, then held at that extrapolated pose.</item>
    /// </list>
    /// Allocation-free after construction. Not thread-safe.
    /// </summary>
    public sealed class SnapshotBuffer
    {
        public const int DefaultCapacity = 32;
        public const double DefaultMaxExtrapolation = 0.25;

        private readonly TransformState[] _items;
        private int _head;
        private int _count;
        private double _lastRenderTime = double.NegativeInfinity;
        private double _maxExtrapolation = DefaultMaxExtrapolation;

        public SnapshotBuffer(int capacity = DefaultCapacity)
        {
            if (capacity < 2)
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be at least 2.");
            _items = new TransformState[capacity];
        }

        public int Capacity => _items.Length;

        public int Count => _count;

        /// <summary>Seconds past the newest state that may be extrapolated. 0 disables extrapolation.</summary>
        public double MaxExtrapolation
        {
            get => _maxExtrapolation;
            set
            {
                if (double.IsNaN(value) || value < 0)
                    throw new ArgumentOutOfRangeException(nameof(value), value, "Must be a non-negative number.");
                _maxExtrapolation = value;
            }
        }

        /// <summary>States rejected by <see cref="Add"/> (invalid, duplicate or too old).</summary>
        public long DroppedCount { get; private set; }

        /// <summary>The i-th held state, oldest first.</summary>
        public TransformState this[int index]
        {
            get
            {
                if ((uint)index >= (uint)_count)
                    throw new ArgumentOutOfRangeException(nameof(index));
                return _items[Physical(index)];
            }
        }

        public bool TryGetNewest(out TransformState state)
        {
            if (_count == 0)
            {
                state = default;
                return false;
            }
            state = _items[Physical(_count - 1)];
            return true;
        }

        public void Clear()
        {
            Array.Clear(_items, 0, _items.Length);
            _head = 0;
            _count = 0;
            _lastRenderTime = double.NegativeInfinity;
        }

        /// <summary>Inserts a state in time order. Returns false if it was dropped.</summary>
        public bool Add(in TransformState state)
        {
            if (!state.IsValid)
                return Drop();
            double time = state.Time;
            if (_count == 0)
            {
                _items[Physical(0)] = state;
                _count = 1;
                return true;
            }

            // Scan from the newest end: in-order arrivals are the common case.
            int index = _count;
            while (index > 0)
            {
                double t = _items[Physical(index - 1)].Time;
                if (t == time)
                    return Drop();
                if (t < time)
                    break;
                index--;
            }

            if (index == 0 && (_count == _items.Length || time <= _lastRenderTime))
                return Drop();

            if (_count == _items.Length)
            {
                _items[_head] = default;
                _head = (_head + 1) % _items.Length;
                _count--;
                index--;
            }
            for (int i = _count; i > index; i--)
                _items[Physical(i)] = _items[Physical(i - 1)];
            _items[Physical(index)] = state;
            _count++;
            return true;
        }

        /// <summary>
        /// Produces the pose at <paramref name="renderTime"/> (seconds on the same clock as the states' times).
        /// Discrete fields (Teleport, Flags, Extra) come from the latest state at or before the render time.
        /// </summary>
        public SnapshotSampleKind Sample(double renderTime, out TransformState result)
        {
            if (_count == 0)
            {
                result = default;
                return SnapshotSampleKind.None;
            }

            ref TransformState newest = ref _items[Physical(_count - 1)];
            if (double.IsNaN(renderTime) || double.IsInfinity(renderTime))
            {
                result = newest;
                return SnapshotSampleKind.Held;
            }
            if (renderTime > _lastRenderTime)
                _lastRenderTime = renderTime;

            ref TransformState oldest = ref _items[_head];
            if (renderTime <= oldest.Time)
            {
                result = oldest;
                return SnapshotSampleKind.Clamped;
            }

            if (renderTime >= newest.Time)
            {
                double ahead = renderTime - newest.Time;
                result = ahead > 0 && _maxExtrapolation > 0
                    ? TransformMath.Extrapolate(newest, Math.Min(ahead, _maxExtrapolation))
                    : newest;
                var kind = ahead == 0 ? SnapshotSampleKind.Interpolated
                    : ahead <= _maxExtrapolation ? SnapshotSampleKind.Extrapolated
                    : SnapshotSampleKind.Held;
                DropOldest(_count - 1);
                return kind;
            }

            // oldest.Time < renderTime < newest.Time: find a with a.Time <= renderTime < b.Time.
            int i = 0;
            while (_items[Physical(i + 1)].Time <= renderTime)
                i++;
            ref TransformState a = ref _items[Physical(i)];
            ref TransformState b = ref _items[Physical(i + 1)];
            if (b.Teleport)
            {
                result = a;
            }
            else
            {
                float t = (float)((renderTime - a.Time) / (b.Time - a.Time));
                result = TransformMath.Interpolate(a, b, t);
                result.Time = renderTime;
            }
            DropOldest(i);
            return SnapshotSampleKind.Interpolated;
        }

        private void DropOldest(int n)
        {
            for (int k = 0; k < n; k++)
            {
                _items[_head] = default;
                _head = (_head + 1) % _items.Length;
            }
            _count -= n;
        }

        private bool Drop()
        {
            DroppedCount++;
            return false;
        }

        private int Physical(int logical) => (_head + logical) % _items.Length;
    }
}
