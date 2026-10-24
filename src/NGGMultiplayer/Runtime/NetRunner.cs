using System;
using NGGMultiplayer.Core.Diagnostics;
using UnityEngine;

namespace NGGMultiplayer.Runtime
{
    /// <summary>
    /// Drives an <see cref="IMultiplayerService"/> from Unity's frame loop. While a session is in progress it keeps
    /// the player loop running when the window loses focus (heartbeats would stop otherwise and every peer would
    /// time out), and restores the game's own setting afterwards.
    /// </summary>
    public sealed class NetRunner : MonoBehaviour
    {
        private const double ErrorLogInterval = 5;

        private IMultiplayerService _service;
        private double _nextErrorLogAt;
        private int _suppressedErrors;
        private bool _forcingRunInBackground;
        private bool _savedRunInBackground;

        public IMultiplayerService Service => _service;

        public void Initialize(IMultiplayerService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
        }

        private void Update()
        {
            if (_service == null)
                return;
            double now = Time.realtimeSinceStartupAsDouble;
            try
            {
                _service.Tick(now);
                UpdateRunInBackground(_service.State);
            }
            catch (Exception e)
            {
                // Throttled: a persistent fault would otherwise log every frame.
                if (now >= _nextErrorLogAt)
                {
                    string suppressed = _suppressedErrors > 0 ? $" ({_suppressedErrors} similar errors suppressed)" : string.Empty;
                    NetLog.Error($"[NetRunner] Tick failed{suppressed}: {e}");
                    _nextErrorLogAt = now + ErrorLogInterval;
                    _suppressedErrors = 0;
                }
                else
                {
                    _suppressedErrors++;
                }
            }
        }

        private void OnDestroy() => RestoreRunInBackground();

        private void UpdateRunInBackground(MultiplayerState state)
        {
            bool inSession = state != MultiplayerState.Unavailable && state != MultiplayerState.Offline;
            if (inSession && !_forcingRunInBackground)
            {
                _savedRunInBackground = Application.runInBackground;
                _forcingRunInBackground = true;
                if (!_savedRunInBackground)
                {
                    Application.runInBackground = true;
                    NetLog.Info("[NetRunner] Keeping the game running in the background for the session.");
                }
            }
            else if (!inSession)
            {
                RestoreRunInBackground();
            }
        }

        private void RestoreRunInBackground()
        {
            if (!_forcingRunInBackground)
                return;
            _forcingRunInBackground = false;
            Application.runInBackground = _savedRunInBackground;
        }

        private void OnApplicationQuit()
        {
            // Say goodbye and restore the local save before the process goes away.
            try
            {
                _service?.Leave();
            }
            catch (Exception e)
            {
                NetLog.Error($"[NetRunner] Leave on quit failed: {e}");
            }
        }
    }
}
