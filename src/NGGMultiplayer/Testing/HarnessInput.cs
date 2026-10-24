using System;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace NGGMultiplayer.Testing
{
    /// <summary>
    /// The harness's key presses for the plugin's own key reading (the multiplayer window's toggle key, the HUD's
    /// Tab). The harness presses keys through the Input System, which the game reads; BepInEx's
    /// <see cref="UnityInput"/> reads legacy <c>UnityEngine.Input</c> instead whenever the game has the legacy backend
    /// (it does: the swing reads the legacy mouse), and legacy input never sees Input System events. So while the
    /// harness runs and BepInEx uses that legacy backend, the pressed keys are also reported here, from the frame the
    /// Input System takes the press. Off (always false) when the harness is off, so normal play is unaffected.
    /// </summary>
    internal static class HarnessInput
    {
        private static readonly InjectedKeyState State = new InjectedKeyState();
        private static int _overlay; // 0 unknown, 1 needed, -1 not needed (BepInEx reads the Input System itself)

        /// <summary>The name of BepInEx's input backend ("LegacyInputSystem", "NewInputSystem"), for the results.</summary>
        public static string Backend
        {
            get
            {
                try
                {
                    return UnityInput.Current != null ? UnityInput.Current.GetType().Name : "none";
                }
                catch (Exception e)
                {
                    return "unreadable (" + e.GetType().Name + ")";
                }
            }
        }

        /// <summary>True when the plugin's key reading needs the harness's presses reported here.</summary>
        public static bool OverlayNeeded
        {
            get
            {
                if (_overlay == 0)
                    _overlay = Backend.IndexOf("NewInputSystem", StringComparison.Ordinal) >= 0 ? -1 : 1;
                return _overlay > 0;
            }
        }

        private static bool Active => TestHarness.Enabled && State.Any;

        /// <summary>The harness pressed <paramref name="key"/>; it is down from the next frame (when the Input System
        /// processes the queued event too).</summary>
        public static void Press(KeyCode key)
        {
            if (key != KeyCode.None && OverlayNeeded)
                State.Press(key.ToString(), Time.frameCount + 1);
        }

        public static void Release(KeyCode key)
        {
            if (key != KeyCode.None && OverlayNeeded)
                State.Release(key.ToString(), Time.frameCount + 1);
        }

        public static bool GetKeyDown(KeyCode key)
        {
            if (!Active)
                return false;
            int frame = Time.frameCount;
            State.Prune(frame - 1);
            return State.IsDown(key.ToString(), frame);
        }

        public static bool GetKey(KeyCode key) => Active && State.IsHeld(key.ToString(), Time.frameCount);

        /// <summary>Like <see cref="KeyboardShortcut.IsDown"/> for a harness press: the main key went down this frame
        /// and every modifier is held (by the harness or for real).</summary>
        public static bool ShortcutDown(KeyboardShortcut shortcut)
        {
            if (!Active || !GetKeyDown(shortcut.MainKey))
                return false;
            foreach (var modifier in shortcut.Modifiers)
            {
                if (!GetKey(modifier) && !UnityInput.Current.GetKey(modifier))
                    return false;
            }
            return true;
        }
    }
}
