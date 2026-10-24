using System;
using System.Collections.Generic;

namespace NGGMultiplayer.Testing
{
    /// <summary>
    /// The commands read from the command file, run in order: one after another in the same frame, except that a
    /// <c>wait</c> (or <c>quit</c>) holds the rest for a time and a game action still in progress (a held key, a
    /// screenshot not yet written, a click's frame) holds them until it is done. So a tester can append a whole
    /// sequence at once ("key Escape", "wait 1", "screenshot pause"). Pure logic on real time; main thread only.
    /// </summary>
    public sealed class CommandQueue
    {
        public const int MaxQueued = 1000;

        private readonly Queue<HarnessCommand> _queue = new Queue<HarnessCommand>();
        private double _heldUntil = double.NegativeInfinity;

        public int Count => _queue.Count;

        /// <summary>True when a hold is still running at <paramref name="now"/>.</summary>
        public bool IsHeld(double now) => now < _heldUntil;

        /// <summary>When the current hold ends (negative infinity: none).</summary>
        public double HeldUntil => _heldUntil;

        /// <summary>Queues a command; false (dropped) when <see cref="MaxQueued"/> are waiting already.</summary>
        public bool Add(HarnessCommand command)
        {
            if (command == null || _queue.Count >= MaxQueued)
                return false;
            _queue.Enqueue(command);
            return true;
        }

        /// <summary>Holds the following commands for <paramref name="seconds"/> from <paramref name="now"/> (infinity:
        /// for good). A shorter hold never cuts a longer one short.</summary>
        public void Hold(double now, double seconds)
        {
            if (double.IsNaN(seconds) || seconds <= 0)
                return;
            _heldUntil = Math.Max(_heldUntil, now + seconds);
        }

        /// <summary>The next command when nothing holds the queue (<paramref name="busy"/>: a game action in progress).</summary>
        public bool TryTake(double now, bool busy, out HarnessCommand command)
        {
            command = null;
            if (busy || IsHeld(now) || _queue.Count == 0)
                return false;
            command = _queue.Dequeue();
            return true;
        }

        public void Clear()
        {
            _queue.Clear();
            _heldUntil = double.NegativeInfinity;
        }
    }
}
