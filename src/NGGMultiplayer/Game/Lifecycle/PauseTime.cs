using System;
using System.Collections.Generic;
using System.Reflection;
using ECM2.Examples.FirstPerson;
using NGGMultiplayer.Core.Diagnostics;
using UnityEngine;
using UnityEngine.InputSystem;

namespace NGGMultiplayer.Game.Lifecycle
{
    /// <summary>
    /// Pausing without freezing the shared world (docs/GAME_INTEGRATION.md 7.6). In a session the pause menu keeps
    /// <c>Time.timeScale</c> at 1 and switches the local player's input off instead; it comes back when the menu
    /// closes (PauseMenu.Exit, QuitToMenu, a scene load, or the menu disappearing). Any other source of timeScale 0
    /// is overridden while in session. Offline nothing changes (the menu's open state is still tracked, for
    /// <see cref="GameContext.PauseMenuOpen"/> and the pause menu's multiplayer button).
    /// <para>Confirmed game facts this relies on:</para>
    /// <list type="bullet">
    /// <item>Only PlayerPressedEscape.OpenMenu (and the debug Screenshotter) set timeScale 0 (#46, #82, #113); PauseMenu
    /// Exit / QuitToMenu, SettingsMenu.Close (not from pause), DeleteStorySave / DeleteEverything and the Luke
    /// endings set it back to 1 (#46, #60).</item>
    /// <item>PauseMenu.OpenSettingsMenu deactivates the PauseMenu while the game stays paused, and SettingsMenu.Close
    /// reactivates it (#58, #112): an inactive PauseMenu with an active SettingsMenu is still "paused".</item>
    /// <item>At timeScale 0 vanilla blocks the mode switch (MoveAndHitController checks timeScale != 0) and
    /// HitSequeneceManager's Update / RestartInput bail; InteractChecker has no check (#59). At timeScale 1 those
    /// would run while the menu is open, so the input actions Switch / Reset / Adjust / Practice and the
    /// InteractChecker components are switched off as well, and RestartInput is skipped (MenuPatches).</item>
    /// </list>
    /// <para>The only pause controller of the plugin: Presence reads <see cref="GameContext.PauseMenuOpen"/>.</para>
    /// </summary>
    internal sealed class PauseTime
    {
        private const string Log = "[Lifecycle] ";
        private const string Feature = "lc.pause.input";
        private const double CursorLockedCloseSeconds = 0.5;

        /// <summary>Gameplay actions of InputManager.instance.input that vanilla's frozen time would have stopped.</summary>
        private static readonly string[] HeldActions = { "Switch", "Reset", "Adjust", "Practice" };

        private readonly GameContext _ctx;
        private readonly FieldInfo _fpsInputField;
        private readonly FieldInfo _swingPanelField;
        private readonly MethodInfo _setMovementDirection;
        private readonly List<Behaviour> _disabled = new List<Behaviour>();
        private readonly List<InputAction> _disabledActions = new List<InputAction>();
        private bool _menuOpen;
        private bool _settingsOpen;
        private bool _holding;
        private bool _instancesLogged;
        private double _cursorLockedSince = double.NaN;
        private PauseMenu _menu;
        private SettingsMenu _settings;

        public PauseTime(GameContext ctx)
        {
            _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
            // Separate keys: a missing field only leaves that one input running.
            _fpsInputField = ctx.Patcher.Field(Feature + ".fps", typeof(MoveAndHitController), "m_fpsInput");
            _swingPanelField = ctx.Patcher.Field(Feature + ".swing", typeof(PanelManager), "m_clubSwingPanel");
            // ECM2's own API (confirmed, #78 / #114): stops a walk that was in progress when input went off.
            try
            {
                _setMovementDirection = HarmonyLib.AccessTools.Method(typeof(ECM2.Character), "SetMovementDirection", new[] { typeof(Vector3) });
            }
            catch (Exception)
            {
                _setMovementDirection = null;
            }
        }

        /// <summary>The game's pause menu is open (tracked offline too), including its settings submenu.</summary>
        public bool MenuOpen => _menuOpen;

        /// <summary>The pause menu's settings submenu is up (the PauseMenu itself is inactive then).</summary>
        public bool SettingsOpen => _menuOpen && _settingsOpen;

        /// <summary>Local input is held off for the pause menu (in session only).</summary>
        public bool Holding => _holding;

        public void OnMenuOpened()
        {
            SetOpen(true);
            _cursorLockedSince = double.NaN;
            _menu = null;
            LogInstancesOnce();
            if (_ctx.InSession)
                Hold();
        }

        public void OnMenuClosed(string why)
        {
            SetOpen(false);
            Release(why);
        }

        public void OnSceneLoaded()
        {
            SetOpen(false);
            Release("scene loaded");
        }

        public void OnSessionReady()
        {
            // Hosting from the pause menu: the world must run from now on.
            if (_menuOpen)
                Hold();
        }

        public void OnSessionEnded()
        {
            Release("session ended");
            // Back to the vanilla pause: the game froze time when the menu opened (and keeps it frozen in settings).
            if (_menuOpen)
                Time.timeScale = 0f;
        }

        public void LateTick()
        {
            if (!_ctx.InSession)
            {
                // Offline only the open state is followed (the game froze time itself).
                if (_menuOpen)
                    PollMenuClosed();
                return;
            }
            // A session that started while the menu was open (hosting or joining from it) takes over the pause.
            if (_menuOpen && !_holding)
                Hold();
            if (_menuOpen)
                PollMenuClosed();
            if (Time.timeScale == 0f)
            {
                if (!_menuOpen)
                {
                    string source = DescribeFreezeSource();
                    _ctx.WarnOnce("timescale:" + source,
                        $"{Log}Time.timeScale was set to 0 by the {source} during a session; keeping the world running.");
                }
                Time.timeScale = 1f;
            }
        }

        private void SetOpen(bool open)
        {
            _menuOpen = open;
            if (!open)
            {
                _menu = null;
                _settings = null;
                _settingsOpen = false;
            }
            _ctx.SetPauseMenuOpen(open);
        }

        private void Hold()
        {
            Time.timeScale = 1f;
            if (_holding || !_ctx.Patcher.IsEnabled(Feature))
                return;
            _holding = true;
            try
            {
                var controller = MoveAndHitController.instance;
                if (controller != null)
                {
                    if (_fpsInputField != null)
                        Disable(_fpsInputField.GetValue(controller) as Behaviour);
                    DisableLookInput(controller.m_FPSController);
                    StopWalking(controller);
                }
                var panels = PanelManager.instance;
                if (panels != null && _swingPanelField != null)
                    Disable(_swingPanelField.GetValue(panels) as Behaviour);
            }
            catch (Exception e)
            {
                _ctx.Patcher.ReportFailure(Feature, e);
            }
            HoldInteract();
            HoldActions();
        }

        private void Release(string why)
        {
            if (!_holding)
                return;
            _holding = false;
            _cursorLockedSince = double.NaN;
            foreach (var behaviour in _disabled)
            {
                try
                {
                    // Destroyed by a scene load: nothing to give back.
                    if (behaviour != null && !behaviour.enabled)
                        behaviour.enabled = true;
                }
                catch (Exception e)
                {
                    _ctx.Patcher.ReportFailure(Feature, e);
                }
            }
            _disabled.Clear();
            foreach (var action in _disabledActions)
            {
                try
                {
                    if (action != null && !action.enabled)
                        action.Enable();
                }
                catch (Exception e)
                {
                    _ctx.Patcher.ReportFailure(Feature + ".actions", e);
                }
            }
            _disabledActions.Clear();
            if (NetLog.IsEnabled(Core.Diagnostics.LogLevel.Debug))
                NetLog.Debug($"{Log}Pause input released ({why}).");
        }

        private void Disable(Behaviour behaviour)
        {
            if (behaviour == null || !behaviour.enabled)
                return;
            behaviour.enabled = false;
            _disabled.Add(behaviour);
        }

        /// <summary>The look input sits on m_FPSController (scene data, #115); a child is accepted too.</summary>
        private void DisableLookInput(GameObject fps)
        {
            if (fps == null)
                return;
            var look = fps.GetComponent(typeof(FirstPersonCharacterLookInput)) as Behaviour;
            if (look == null)
            {
                look = fps.GetComponentInChildren(typeof(FirstPersonCharacterLookInput), true) as Behaviour;
                _ctx.WarnOnce("pause.look", look != null
                    ? $"{Log}FirstPersonCharacterLookInput is on a child of {fps.name}, not on it."
                    : $"{Log}No FirstPersonCharacterLookInput under {fps.name}; looking around stays on in the pause menu.");
            }
            Disable(look);
        }

        /// <summary>InteractChecker has no timeScale check (#59): it would interact through the open menu.</summary>
        private void HoldInteract()
        {
            if (!_ctx.Patcher.IsEnabled(Feature + ".interact"))
                return;
            try
            {
                foreach (var checker in Compat.FindAll<InteractChecker>(false))
                    Disable(checker);
            }
            catch (Exception e)
            {
                _ctx.Patcher.ReportFailure(Feature + ".interact", e);
            }
        }

        /// <summary>Gameplay input actions that vanilla's frozen time stops (mode switch, shot reset, adjust, practice).</summary>
        private void HoldActions()
        {
            if (!_ctx.Patcher.IsEnabled(Feature + ".actions"))
                return;
            try
            {
                var input = InputManager.instance != null ? InputManager.instance.input : null;
                var asset = input != null ? input.actions : null;
                if (asset == null)
                    return;
                foreach (var name in HeldActions)
                {
                    var action = asset.FindAction(name, false);
                    if (action == null)
                    {
                        _ctx.WarnOnce("pause.action." + name, $"{Log}Input action '{name}' not found; it stays on in the pause menu.");
                        continue;
                    }
                    if (!action.enabled)
                        continue;
                    action.Disable();
                    _disabledActions.Add(action);
                }
            }
            catch (Exception e)
            {
                _ctx.Patcher.ReportFailure(Feature + ".actions", e);
            }
        }

        private void StopWalking(MoveAndHitController controller)
        {
            if (_setMovementDirection == null)
                return;
            var character = controller.m_fpsCharacter;
            if (character == null)
                return;
            try
            {
                _setMovementDirection.Invoke(character, new object[] { Vector3.zero });
            }
            catch (Exception e)
            {
                _ctx.WarnOnce("pause.stopwalk", $"{Log}Stopping the walk for the pause menu failed: {e.Message}");
            }
        }

        private void PollMenuClosed()
        {
            // Neither the PauseMenu nor its settings submenu is up: it closed without Exit.
            if (!MenuStillActive())
            {
                OnMenuClosed("pause menu gone");
                return;
            }
            // A locked cursor means the player is back in first-person control (Exit locks it in Walk mode).
            // The settings submenu unlocks the cursor itself (SettingsMenu.Update).
            if (!_settingsOpen && Cursor.lockState == CursorLockMode.Locked)
            {
                double now = _ctx.Now;
                if (double.IsNaN(_cursorLockedSince))
                    _cursorLockedSince = now;
                else if (now - _cursorLockedSince >= CursorLockedCloseSeconds)
                    OnMenuClosed("cursor locked again");
            }
            else
            {
                _cursorLockedSince = double.NaN;
            }
        }

        /// <summary>
        /// An active PauseMenu, or the SettingsMenu opened from it, is still there. The object found once is watched
        /// each frame; the scene-wide search runs only to find it and to confirm it is gone.
        /// </summary>
        private bool MenuStillActive()
        {
            if (_menu != null && _menu.isActiveAndEnabled)
            {
                SetSettingsOpen(false);
                return true;
            }
            _menu = Compat.FindAny<PauseMenu>(false);
            if (_menu != null)
            {
                SetSettingsOpen(false);
                return true;
            }
            if (_settings == null || !_settings.isActiveAndEnabled)
                _settings = Compat.FindAny<SettingsMenu>(false);
            SetSettingsOpen(_settings != null);
            return _settingsOpen;
        }

        private void SetSettingsOpen(bool open)
        {
            if (_settingsOpen == open)
                return;
            _settingsOpen = open;
            _cursorLockedSince = double.NaN;
            if (NetLog.IsEnabled(Core.Diagnostics.LogLevel.Debug))
                NetLog.Debug($"{Log}Pause menu {(open ? "switched to its settings submenu (still paused)" : "is back from its settings submenu")}.");
        }

        /// <summary>Runtime check (#147): how many PauseMenu objects the scene has, and whether one is up after OpenMenu.</summary>
        private void LogInstancesOnce()
        {
            if (_instancesLogged)
                return;
            _instancesLogged = true;
            try
            {
                int all = Compat.FindAll<PauseMenu>(true).Length;
                int active = Compat.FindAll<PauseMenu>(false).Length;
                string line = $"{Log}Pause menu opened: {all} PauseMenu object(s) in the scene, {active} active right after OpenMenu.";
                if (all == 1 && active == 1)
                    NetLog.Info(line);
                else
                    NetLog.Warn(line + " The close detection expects exactly one, active while the menu is open.");
            }
            catch (Exception e)
            {
                NetLog.Debug($"{Log}Counting PauseMenu objects failed: {e.Message}");
            }
        }

        private static string DescribeFreezeSource()
        {
            try
            {
                // The only other code that sets timeScale 0 (#46, #113): the debug Screenshotter (P key).
                if (Compat.FindAny<Screenshotter>(false) != null)
                    return "debug screenshotter (or a pause the plugin missed)";
            }
            catch (Exception)
            {
                // Only for the log line.
            }
            return "game (a pause the plugin missed)";
        }
    }
}
