using System;
using System.Collections.Generic;

namespace NGGMultiplayer.Game.Progression
{
    /// <summary>Why the local player cannot take another player's story effects right now.</summary>
    public enum BusyReason : byte
    {
        None = 0,

        /// <summary>In a dialogue or cutscene of their own (MoveAndHitController.m_moveLocked).</summary>
        Cutscene = 1,

        /// <summary>A shot of their own is being played (swing to rest).</summary>
        Shot = 2
    }

    /// <summary>
    /// Whether story effects that came from another player (replicated story beats, the shared main track's
    /// challenge completions) wait until the local player is free again (docs/GAME_INTEGRATION.md 6.15). They wait
    /// while the player is in a dialogue / cutscene of their own or playing a shot, and for
    /// <see cref="SettleSeconds"/> after: a dialogue's last line Invokes its invokeEnd methods and saves on the next
    /// frames, a shot is judged when the ball comes to rest. The wait is bounded so a player stuck in a state never
    /// holds the shared progress back for good. Pure logic (unit tested); main thread only.
    /// </summary>
    public sealed class LocalBusyGate
    {
        public const double SettleSeconds = 1.5;
        /// <summary>A dialogue waits for the player's button: an idle player gets the story after this long anyway.</summary>
        public const double MaxCutsceneHoldSeconds = 120.0;
        /// <summary>A ball that never reports its rest (lost, holed and killed) stops holding after this long.</summary>
        public const double MaxShotHoldSeconds = 30.0;

        private double _cutsceneSince = double.NaN;
        private double _shotSince = double.NaN;
        private double _lastHeldAt = double.NegativeInfinity;

        /// <summary>The reason of the last <see cref="Update"/> that held, or None.</summary>
        public BusyReason Reason { get; private set; }

        /// <summary>The last <see cref="Update"/> found a hold that ran out (the player stayed busy too long).</summary>
        public bool Expired { get; private set; }

        /// <summary>
        /// Feeds the local player's state at <paramref name="now"/> (monotonic seconds); true while incoming story
        /// effects should wait. Calling it again with the same state and time gives the same answer.
        /// </summary>
        public bool Update(double now, bool moveLocked, bool midShot)
        {
            _cutsceneSince = Track(_cutsceneSince, moveLocked, now);
            _shotSince = Track(_shotSince, midShot, now);
            bool holdCutscene = moveLocked && now - _cutsceneSince < MaxCutsceneHoldSeconds;
            bool holdShot = midShot && now - _shotSince < MaxShotHoldSeconds;
            Expired = (moveLocked && !holdCutscene) || (midShot && !holdShot);
            if (holdCutscene || holdShot)
            {
                _lastHeldAt = now;
                Reason = holdCutscene ? BusyReason.Cutscene : BusyReason.Shot;
                return true;
            }
            // Still busy past the bound: let it through now, no settle.
            if (moveLocked || midShot)
            {
                Reason = BusyReason.None;
                return false;
            }
            if (now - _lastHeldAt < SettleSeconds)
                return true;
            Reason = BusyReason.None;
            return false;
        }

        public void Clear()
        {
            _cutsceneSince = double.NaN;
            _shotSince = double.NaN;
            _lastHeldAt = double.NegativeInfinity;
            Reason = BusyReason.None;
            Expired = false;
        }

        private static double Track(double since, bool busy, double now)
        {
            if (!busy)
                return double.NaN;
            return double.IsNaN(since) ? now : since;
        }
    }

    /// <summary>
    /// Story beats (<c>ngg.pg.story</c>) that arrived while the local player was busy, each name once, in the order of
    /// its latest arrival: a repeat moves the beat to the end. Replicate-class beats are safe to run again but not all
    /// commutative (OpenGates / CloseGates set the same gate), so the last one sent must also run last. Bounded.
    /// </summary>
    public sealed class StoryBeatQueue
    {
        public const int Capacity = 64;

        private readonly List<KeyValuePair<string, ulong>> _beats = new List<KeyValuePair<string, ulong>>();

        public int Count => _beats.Count;

        /// <summary>
        /// Queues <paramref name="name"/> from <paramref name="from"/> at the end; a beat already queued moves there.
        /// True when the name was not queued before; false when it moved, or the name is empty or the queue full.
        /// </summary>
        public bool Add(string name, ulong from)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            for (int i = 0; i < _beats.Count; i++)
            {
                if (string.Equals(_beats[i].Key, name, StringComparison.Ordinal))
                {
                    _beats.RemoveAt(i);
                    _beats.Add(new KeyValuePair<string, ulong>(name, from));
                    return false;
                }
            }
            if (_beats.Count >= Capacity)
                return false;
            _beats.Add(new KeyValuePair<string, ulong>(name, from));
            return true;
        }

        /// <summary>Everything queued, oldest first; the queue is empty afterwards.</summary>
        public List<KeyValuePair<string, ulong>> TakeAll()
        {
            var all = new List<KeyValuePair<string, ulong>>(_beats);
            _beats.Clear();
            return all;
        }

        public void Clear() => _beats.Clear();
    }

    /// <summary>
    /// DialogueManager methods a network replay is about to make the game run (the shared track's
    /// functionOnComplete / functionOnStart, which the game Invokes by name a frame or a delay later), each mark with
    /// the time until which a call of it counts as the replay's. A method can carry several marks (a catch-up of
    /// several challenges that name the same function); each call uses up one, the one that runs out first.
    /// </summary>
    public sealed class ReplayCallMarks
    {
        /// <summary>The most marks kept, over all methods.</summary>
        public const int Capacity = 32;

        private readonly Dictionary<string, List<double>> _until = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        private int _count;

        /// <summary>Marks held (each counts, also several of one method).</summary>
        public int Count => _count;

        public void Mark(string method, double now, double seconds)
        {
            if (string.IsNullOrEmpty(method) || !(seconds > 0))
                return;
            if (_count >= Capacity)
                Prune(now);
            if (_count >= Capacity)
                return;
            if (!_until.TryGetValue(method, out var list))
            {
                list = new List<double>(1);
                _until[method] = list;
            }
            list.Add(now + seconds);
            _count++;
        }

        /// <summary>
        /// A call of <paramref name="method"/> now is the replay's: true when a mark of it is still running, and that
        /// mark is used up. Marks that ran out are dropped.
        /// </summary>
        public bool TryConsume(string method, double now)
        {
            if (string.IsNullOrEmpty(method) || !_until.TryGetValue(method, out var list))
                return false;
            _count -= list.RemoveAll(until => until < now);
            int first = -1;
            for (int i = 0; i < list.Count; i++)
            {
                if (first < 0 || list[i] < list[first])
                    first = i;
            }
            if (first >= 0)
            {
                list.RemoveAt(first);
                _count--;
            }
            if (list.Count == 0)
                _until.Remove(method);
            return first >= 0;
        }

        public bool IsMarked(string method, double now)
        {
            if (string.IsNullOrEmpty(method) || !_until.TryGetValue(method, out var list))
                return false;
            foreach (double until in list)
            {
                if (now <= until)
                    return true;
            }
            return false;
        }

        public void Clear()
        {
            _until.Clear();
            _count = 0;
        }

        private void Prune(double now)
        {
            var empty = new List<string>();
            foreach (var pair in _until)
            {
                _count -= pair.Value.RemoveAll(until => until < now);
                if (pair.Value.Count == 0)
                    empty.Add(pair.Key);
            }
            foreach (var key in empty)
                _until.Remove(key);
        }
    }

    /// <summary>Which story calls a network replay must not make on this peer (6.15).</summary>
    public static class CutsceneRules
    {
        /// <summary>How long after a replayed challenge completion its functionOnComplete counts as the replay's.</summary>
        public const double InvokeWindowSeconds = 1.5;

        /// <summary>The longest functionOnStartDelay that is still waited for.</summary>
        public const double MaxStartDelaySeconds = 30.0;

        /// <summary>
        /// The player's own story calls: they move the local player (teleports) or start a dialogue for them. The
        /// player who triggered them gets them; another player's replay of the same challenge does not.
        /// </summary>
        public static readonly string[] PlayerMovers =
        {
            "TeleportCarpark", "TeleportMD", "TeleportMDTrap", "StartTeleporterDialogue", "PlayCarDialogue2"
        };

        private static readonly HashSet<string> MoverSet = new HashSet<string>(PlayerMovers, StringComparer.Ordinal);

        /// <summary>
        /// A DialogueManager method that a replay of someone else's progress must not run here: a teleport or
        /// dialogue of the player who made the progress, or a presentation clip of their dialogue (Luke's videos,
        /// LookAt* camera moves). World-state methods (gates, unlocks, tees, the power requirement UI) still run.
        /// </summary>
        public static bool SkipWhenReplayed(string method)
        {
            if (string.IsNullOrEmpty(method))
                return false;
            if (MoverSet.Contains(method))
                return true;
            if (StoryMethods.Classify(method) != StoryClass.Presentation)
                return false;
            // ShowPowerRequirement / HidePowerRequirement belong to the challenge everyone is on now; the NullRef
            // pair is UI of unknown use, left alone.
            return method.StartsWith("LookAt", StringComparison.Ordinal) || method.StartsWith("Luke", StringComparison.Ordinal)
                || method.StartsWith("EndLuke", StringComparison.Ordinal) || StoryMethods.IsReturnLuke(method);
        }

        /// <summary>How long a functionOnStart Invoked with <paramref name="delay"/> counts as the replay's.</summary>
        public static double StartWindow(float delay)
        {
            double d = float.IsNaN(delay) || delay < 0 ? 0 : Math.Min(delay, MaxStartDelaySeconds);
            return d + InvokeWindowSeconds;
        }

        /// <summary>
        /// A dialogue / player lock call is the network's, not this player's: made synchronously inside a replay
        /// scope in a session, or synchronously inside a replayed challenge function (its call is on the stack).
        /// </summary>
        public static bool IsReplayCaused(bool inSession, bool replayScopeActive, bool insideReplayedFunction) =>
            (inSession && replayScopeActive) || insideReplayedFunction;

        /// <summary>
        /// A guarded call that is never skipped: <c>LockPlayer(false)</c>. Skipping an unlock can only leave a player
        /// frozen (and the game's save, which waits on <c>m_moveLocked</c>, unwritten); a replay's unlock of a
        /// player who is not locked does nothing.
        /// </summary>
        public static bool NeverSkipped(string method, object[] args) =>
            string.Equals(method, "LockPlayer", StringComparison.Ordinal)
            && args != null && args.Length == 1 && args[0] is bool locked && !locked;
    }
}
