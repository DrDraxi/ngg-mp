using System;
using System.Collections;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Sync;
using UnityEngine;

namespace NGGMultiplayer.Game.Presence
{
    /// <summary>
    /// Host-authoritative wind (spec 7.5). The host streams its WindPanel's current and desired wind; a client follows
    /// it while <see cref="HostWindFor"/> holds. A client's own forced wind (challenge m_forceWind, the LMUGC no-wind
    /// assist, m_debugNoWind) and a host whose wind is forced leave the client's game wind alone.
    /// <para>The game's wind (answers #44, #56, #57): WindCycle eases m_currentWind toward m_desiredWind (its lerp
    /// target) and restarts itself; Update never changes the wind; LateUpdate overwrites it in the forced cases.
    /// StartWind is not idempotent and several sites StopAllCoroutines + start a new cycle, so vanilla can already run
    /// two cycles. Every cycle is therefore wrapped (postfix on WindCycle) and paused, not skipped, while the client
    /// follows: no cycle is ever lost or added by the mod, and the game's own count of cycles resumes as it was.
    /// NewWind (a one-shot re-roll) is skipped while following.</para>
    /// </summary>
    internal sealed class WindSync
    {
        private const double SendInterval = 0.1;
        private const double HostWindTimeout = 3.0;

        private readonly GameContext _context;
        private readonly PresenceAccess _access;

        // Client: the host's wind.
        private Vector2 _hostCurrent;
        private Vector2 _hostDesired;
        private bool _hostFree;
        private double _lastHostWindAt = double.NegativeInfinity;
        private bool _following;

        // Host: what went out.
        private WindPanel _panel;
        private double _lastSentAt = double.NegativeInfinity;
        private Vector2 _lastSentDesired;
        private bool _lastSentFree;
        private bool _sentOnce;

        // Per-frame cache of the forced-wind check (GetCurrentActiveChallenge walks the challenge set).
        private int _forcedFrame = -1;
        private WindPanel _forcedPanel;
        private bool _forced;

        // Diagnostics: game cycles stepped in one frame (vanilla double starts, answers #56).
        private int _cycleFrame = -1;
        private int _cyclesThisFrame;
        private bool _cyclesLogged;

        public WindSync(GameContext context, PresenceAccess access)
        {
            _context = context;
            _access = access;
        }

        public void Install()
        {
            // Registered even without the hooks: the host must know every Presence RPC to validate it.
            _context.RegisterRpc(PresenceRpc.Wind, OnWind, (s, t, p, r) => false);
            var patcher = _context.Patcher;
            if (!patcher.IsEnabled(PresenceFeature.Wind))
                return;
            var type = typeof(WindSync);
            var lateUpdate = patcher.Method(PresenceFeature.Wind, typeof(WindPanel), "LateUpdate", Type.EmptyTypes);
            var newWind = patcher.Method(PresenceFeature.Wind, typeof(WindPanel), "NewWind", Type.EmptyTypes);
            var cycle = patcher.Method(PresenceFeature.Wind, typeof(WindPanel), "WindCycle", Type.EmptyTypes);
            if (cycle != null && cycle.ReturnType != typeof(IEnumerator))
                patcher.Disable(PresenceFeature.Wind, "WindPanel.WindCycle does not return IEnumerator");
            patcher.Postfix(PresenceFeature.Wind, lateUpdate, type, nameof(LateUpdatePostfix));
            patcher.Prefix(PresenceFeature.Wind, newWind, type, nameof(NewWindPrefix));
            patcher.Postfix(PresenceFeature.Wind, cycle, type, nameof(WindCyclePostfix));
        }

        /// <summary>A client follows the host's wind right now (on <paramref name="panel"/>, if given).</summary>
        public bool HostWindFor(WindPanel panel)
        {
            if (!_context.Patcher.IsEnabled(PresenceFeature.Wind) || !_context.IsClient || !_context.IsReady
                || !_context.WorldReady)
                return false;
            if (_context.Now - _lastHostWindAt > HostWindTimeout || !_hostFree)
                return false;
            return !LocallyForced(panel);
        }

        public void Reset()
        {
            _lastHostWindAt = double.NegativeInfinity;
            _hostFree = false;
            _sentOnce = false;
            _lastSentAt = double.NegativeInfinity;
        }

        public void OnSceneLoaded()
        {
            _panel = null;
            _following = false;
            _sentOnce = false;
            _forcedFrame = -1;
            _forcedPanel = null;
        }

        /// <summary>
        /// The local game sets the wind itself this frame: a challenge's m_forceWind, the LMUGC no-wind timer or
        /// m_debugNoWind (the cases WindPanel.LateUpdate overwrites, answers #57).
        /// </summary>
        private bool LocallyForced(WindPanel panel)
        {
            int frame = Time.frameCount;
            if (frame == _forcedFrame && panel == _forcedPanel)
                return _forced;
            _forcedFrame = frame;
            _forcedPanel = panel;
            _forced = false;
            var challenges = ChallengeSystem.instance;
            if (challenges != null && PresenceAccess.IsWindForced(challenges.GetCurrentActiveChallenge()))
                _forced = true;
            var save = SaveManager.instance;
            if (save != null && save.m_run.lmugcNoWindTime > 0f)
                _forced = true;
            if (panel != null && panel.m_debugNoWind)
                _forced = true;
            return _forced;
        }

        // ---- Host ------------------------------------------------------------------------------------

        private void HostLateUpdate(WindPanel panel)
        {
            if (!_context.IsHost || !_context.WorldReady)
                return;
            // One panel per world; a second instance (if any) must not interleave its values.
            if (_panel == null)
                _panel = panel;
            else if (_panel != panel)
                return;
            double now = _context.Now;
            if (now - _lastSentAt < SendInterval)
                return;
            var current = panel.m_currentWind;
            var desired = _access.DesiredWind(panel);
            // The host's wind is its own while it is forced: clients then keep their own rather than take a calm one.
            bool free = !LocallyForced(panel);
            bool reliable = !_sentOnce || free != _lastSentFree
                || WindMath.ChangedBeyond(desired.x, desired.y, _lastSentDesired.x, _lastSentDesired.y, WindMath.ReliableDelta);
            var message = new WindMessage
            {
                CurrentX = current.x,
                CurrentY = current.y,
                DesiredX = desired.x,
                DesiredY = desired.y,
                HostFree = free
            };
            if (!Finite(message))
                return;
            _lastSentAt = now;
            if (_context.Send(PresenceRpc.Wind, RpcTarget.Others, message, (w, m) => m.Write(w),
                    reliable ? Reliability.Reliable : Reliability.Unreliable))
            {
                _sentOnce = true;
                _lastSentDesired = desired;
                _lastSentFree = free;
            }
        }

        private static bool Finite(WindMessage m) =>
            GameWire.IsFinite(new Vector2(m.CurrentX, m.CurrentY)) && GameWire.IsFinite(new Vector2(m.DesiredX, m.DesiredY))
            && Mathf.Abs(m.CurrentX) <= PresenceWire.MaxWind && Mathf.Abs(m.CurrentY) <= PresenceWire.MaxWind
            && Mathf.Abs(m.DesiredX) <= PresenceWire.MaxWind && Mathf.Abs(m.DesiredY) <= PresenceWire.MaxWind;

        // ---- Client ----------------------------------------------------------------------------------

        private void OnWind(PeerId sender, NetReader reader)
        {
            var message = WindMessage.Read(reader);
            if (_context.IsHost || sender != _context.HostId)
                return;
            _hostCurrent = new Vector2(message.CurrentX, message.CurrentY);
            _hostDesired = new Vector2(message.DesiredX, message.DesiredY);
            _hostFree = message.HostFree;
            _lastHostWindAt = _context.Now;
        }

        private void ClientLateUpdate(WindPanel panel)
        {
            bool follow = HostWindFor(panel);
            if (follow)
            {
                // The paused cycles and Update leave m_currentWind alone, so easing from it is easing from our own
                // last value.
                _access.DesiredWind(panel) = _hostDesired;
                float x = panel.m_currentWind.x;
                float y = panel.m_currentWind.y;
                WindMath.Follow(ref x, ref y, _hostCurrent.x, _hostCurrent.y, Time.unscaledDeltaTime);
                panel.m_currentWind = new Vector2(x, y);
            }
            if (follow != _following)
                _context.Info(follow ? "[Presence] Following the host's wind." : "[Presence] The game's own wind resumes.");
            _following = follow;
        }

        /// <summary>
        /// A running game cycle (past its first step, so a cycle restarting itself is not counted twice) was stepped
        /// this frame; logs once when two are.
        /// </summary>
        private void CountCycle()
        {
            int frame = Time.frameCount;
            if (frame != _cycleFrame)
            {
                _cycleFrame = frame;
                _cyclesThisFrame = 0;
            }
            if (++_cyclesThisFrame == 2 && !_cyclesLogged)
            {
                _cyclesLogged = true;
                _context.Info("[Presence] WindPanel runs two or more WindCycle coroutines at once (the game's own double start).");
            }
        }

        // ---- Patches ---------------------------------------------------------------------------------

        private static void LateUpdatePostfix(WindPanel __instance)
        {
            var context = GameContext.Current;
            var self = PresenceModule.Instance?.Wind;
            if (context == null || self == null || __instance == null || !context.Patcher.IsEnabled(PresenceFeature.Wind))
                return;
            if (!context.InSession && !self._following)
                return;
            try
            {
                if (context.IsHost)
                    self.HostLateUpdate(__instance);
                else
                    self.ClientLateUpdate(__instance);
            }
            catch (Exception e)
            {
                context.Patcher.ReportFailure(PresenceFeature.Wind, e);
            }
        }

        private static bool NewWindPrefix(WindPanel __instance)
        {
            var context = GameContext.Current;
            var self = PresenceModule.Instance?.Wind;
            if (context == null || self == null || !context.Patcher.IsEnabled(PresenceFeature.Wind))
                return true;
            try
            {
                return !self.HostWindFor(__instance);
            }
            catch (Exception e)
            {
                context.Patcher.ReportFailure(PresenceFeature.Wind, e);
                return true;
            }
        }

        private static void WindCyclePostfix(WindPanel __instance, ref IEnumerator __result)
        {
            if (__result == null || __result is GatedCycle)
                return;
            __result = new GatedCycle(__result, __instance);
        }

        /// <summary>
        /// A game WindCycle that waits (yields a frame without stepping) while the client follows the host's wind.
        /// Stopping it (StopAllCoroutines) stops it as it would the game's own; the game's exceptions pass through
        /// unchanged, the mod's never do.
        /// </summary>
        private sealed class GatedCycle : IEnumerator
        {
            private readonly IEnumerator _inner;
            private readonly WindPanel _panel;
            private object _current;
            private bool _started;

            public GatedCycle(IEnumerator inner, WindPanel panel)
            {
                _inner = inner;
                _panel = panel;
            }

            public object Current => _current;

            public bool MoveNext()
            {
                bool first = !_started;
                _started = true;
                if (Paused(first))
                {
                    _current = null;
                    return true;
                }
                bool more = _inner.MoveNext();
                _current = more ? _inner.Current : null;
                return more;
            }

            public void Reset() => _inner.Reset();

            private bool Paused(bool first)
            {
                var context = GameContext.Current;
                var self = PresenceModule.Instance?.Wind;
                if (context == null || self == null || !context.Patcher.IsEnabled(PresenceFeature.Wind))
                    return false;
                try
                {
                    if (!first)
                        self.CountCycle();
                    return self.HostWindFor(_panel);
                }
                catch (Exception e)
                {
                    context.Patcher.ReportFailure(PresenceFeature.Wind, e);
                    return false;
                }
            }
        }
    }
}
