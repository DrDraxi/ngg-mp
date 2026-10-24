using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using HarmonyLib;
using NGGMultiplayer.Game;
using NGGMultiplayer.UI;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace NGGMultiplayer.Testing
{
    /// <summary>
    /// <see cref="IHarnessControl"/> over Unity: screenshots (ScreenCapture), key presses queued as Keyboard state
    /// events (the game's Input System actions see them like a real press; the plugin's legacy key reading through
    /// <see cref="HarnessInput"/>), clicks on uGUI elements, the scene report, the multiplayer window and quitting.
    /// Coroutines run on the harness runner (real time, so they work while the game is paused at timeScale 0). Only
    /// created when the harness is enabled.
    /// </summary>
    internal sealed class HarnessControl : IHarnessControl
    {
        private const float ShotTimeoutSeconds = 10f;
        private const float QuitFallbackSeconds = 5f;
        private const int MaxNumbered = 100;

        private static readonly Type ClickHandlerType = AccessTools.TypeByName("UnityEngine.EventSystems.IPointerClickHandler");
        private static readonly Type PointerDataType = AccessTools.TypeByName("UnityEngine.EventSystems.PointerEventData");
        private static readonly Type EventSystemType = AccessTools.TypeByName("UnityEngine.EventSystems.EventSystem");

        private readonly MonoBehaviour _runner;
        private readonly Action<string> _report;
        private readonly HashSet<Key> _held = new HashSet<Key>();
        private int _keysInFlight;
        private int _shotsPending;
        private int _busyUntilFrame;

        public HarnessControl(MonoBehaviour runner, Action<string> report)
        {
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
            _report = report ?? (_ => { });
        }

        public bool Busy => _keysInFlight > 0 || _shotsPending > 0 || Time.frameCount < _busyUntilFrame;

        public string ScreenshotDirectory => Path.Combine(Paths.BepInExRootPath, "screenshots");

        // ---- Screenshot ---------------------------------------------------------------------------------

        public bool Screenshot(string fileName, out string path, out string error)
        {
            path = null;
            error = null;
            try
            {
                string directory = ScreenshotDirectory;
                Directory.CreateDirectory(directory);
                for (int attempt = 1; attempt <= MaxNumbered; attempt++)
                {
                    path = Path.Combine(directory, ScreenshotNames.Numbered(fileName, attempt));
                    if (!File.Exists(path))
                        break;
                }
                ScreenCapture.CaptureScreenshot(path);
                _shotsPending++;
                _runner.StartCoroutine(AwaitShot(path));
                return true;
            }
            catch (Exception e)
            {
                error = e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        private IEnumerator AwaitShot(string path)
        {
            float deadline = Time.realtimeSinceStartup + ShotTimeoutSeconds;
            long lastLength = -1;
            try
            {
                while (Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                    long length = File.Exists(path) ? new FileInfo(path).Length : -1;
                    // Written when it exists and its size held still for a frame.
                    if (length > 0 && length == lastLength)
                    {
                        _report($"screenshot written: {path} ({length / 1024} KB, {Screen.width}x{Screen.height})");
                        yield break;
                    }
                    lastLength = length;
                }
                _report($"screenshot NOT written within {ShotTimeoutSeconds:0} s: {path} (minimized window or no graphics device?)");
            }
            finally
            {
                _shotsPending--;
            }
        }

        // ---- Keys ---------------------------------------------------------------------------------------

        public bool PressKey(string keyName, float holdSeconds, out string message)
        {
            // "LeftCtrl+W": every key is held together for the same time.
            var names = (keyName ?? string.Empty).Split('+');
            var keys = new Key[names.Length];
            var legacy = new KeyCode[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                if (!Enum.TryParse(names[i], true, out keys[i]) || keys[i] == Key.None || !Enum.IsDefined(typeof(Key), keys[i]))
                {
                    message = "unknown key name '" + names[i] + "' (Input System Key names: Escape, Tab, Enter, Space, F1..F12, " +
                              "A..Z, Digit0..Digit9, LeftArrow, LeftShift, LeftCtrl, Backspace, ...)";
                    return false;
                }
                Enum.TryParse(KeyNames.LegacyName(keys[i].ToString()), true, out legacy[i]);
            }
            var keyboard = Keyboard.current;
            if (keyboard == null)
            {
                message = "the Input System has no keyboard device (Keyboard.current is null)";
                return false;
            }
            _keysInFlight++;
            _runner.StartCoroutine(HoldKeys(keyboard, keys, legacy, holdSeconds));

            var sb = new StringBuilder();
            sb.Append(string.Join("+", keys)).Append(" pressed for ").Append(holdSeconds.ToString("0.###", CultureInfo.InvariantCulture))
                .Append(" s through the Input System (device '").Append(keyboard.name).Append("'")
                .Append(keyboard.enabled ? string.Empty : ", DISABLED: the press is ignored").Append(')');
            if (!Application.isFocused)
                sb.Append("; the game window does NOT have focus: the Input System may ignore keyboard events in the background");
            sb.Append("; plugin keys (BepInEx ").Append(HarnessInput.Backend).Append(")");
            var known = Array.FindAll(legacy, k => k != KeyCode.None);
            sb.Append(HarnessInput.OverlayNeeded
                ? known.Length > 0 ? " get it as KeyCode." + string.Join("+", known) : " do not get it (no matching KeyCode)"
                : " read the Input System directly");
            message = sb.ToString();
            return true;
        }

        private IEnumerator HoldKeys(Keyboard keyboard, Key[] keys, KeyCode[] legacy, float holdSeconds)
        {
            try
            {
                foreach (var key in keys)
                    _held.Add(key);
                QueueKeyboard(keyboard);
                foreach (var code in legacy)
                    HarnessInput.Press(code);
                float releaseAt = Time.realtimeSinceStartup + holdSeconds;
                // The press is processed at the start of the next frame; keep it down for at least one full frame.
                yield return null;
                yield return null;
                while (Time.realtimeSinceStartup < releaseAt)
                    yield return null;
                foreach (var key in keys)
                    _held.Remove(key);
                QueueKeyboard(keyboard);
                foreach (var code in legacy)
                    HarnessInput.Release(code);
                // Let the release land, and the game react to it, before the next command.
                yield return null;
                yield return null;
            }
            finally
            {
                foreach (var key in keys)
                    _held.Remove(key);
                _keysInFlight--;
            }
        }

        private void QueueKeyboard(Keyboard keyboard)
        {
            var pressed = new Key[_held.Count];
            _held.CopyTo(pressed);
            // The whole keyboard's state: every key the harness holds is down, every other key up.
            UnityEngine.InputSystem.InputSystem.QueueStateEvent(keyboard, new KeyboardState(pressed), -1.0);
        }

        // ---- UI -----------------------------------------------------------------------------------------

        public IReadOnlyList<UiElement> FindClickables()
        {
            var elements = new List<UiElement>();
            foreach (var button in Compat.FindAll<Button>(false))
            {
                if (button != null && button.isActiveAndEnabled && button.IsInteractable())
                    elements.Add(new UiElement("Button", PathOf(button.transform), LabelOf(button.gameObject), button));
            }
            if (ClickHandlerType != null)
            {
                foreach (var behaviour in Compat.FindAll<MonoBehaviour>(false))
                {
                    if (behaviour == null || behaviour is Button || !behaviour.isActiveAndEnabled || !ClickHandlerType.IsInstanceOfType(behaviour))
                        continue;
                    // A Button's helpers (the game's ButtonPunchAnimator) share its label; the Button alone stands for the object.
                    if (behaviour.GetComponent<Button>() != null)
                        continue;
                    if (behaviour is Selectable selectable && !selectable.IsInteractable())
                        continue;
                    elements.Add(new UiElement(behaviour.GetType().Name, PathOf(behaviour.transform), LabelOf(behaviour.gameObject), behaviour));
                }
            }
            elements.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            return elements;
        }

        public bool Click(UiElement element, out string error)
        {
            error = null;
            var behaviour = element?.Handle as MonoBehaviour;
            if (behaviour == null || !behaviour.isActiveAndEnabled)
            {
                error = "it is gone or inactive now";
                return false;
            }
            try
            {
                _busyUntilFrame = Time.frameCount + 2;
                if (behaviour is Button button)
                {
                    if (!button.IsInteractable())
                    {
                        error = "it is not interactable now";
                        return false;
                    }
                    button.onClick.Invoke();
                    return true;
                }
                var click = ClickHandlerType?.GetMethod("OnPointerClick");
                var eventSystem = EventSystemType != null ? AccessTools.Property(EventSystemType, "current")?.GetValue(null, null) : null;
                if (click == null || PointerDataType == null)
                {
                    error = "IPointerClickHandler / PointerEventData not found";
                    return false;
                }
                // PointerEventData(EventSystem); its button defaults to the left one.
                object data = Activator.CreateInstance(PointerDataType, eventSystem);
                click.Invoke(behaviour, new[] { data });
                return true;
            }
            catch (TargetInvocationException e)
            {
                error = "the click handler threw " + (e.InnerException ?? e).GetType().Name + ": " + (e.InnerException ?? e).Message;
                return false;
            }
            catch (Exception e)
            {
                error = "the click handler threw " + e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        private static string PathOf(Transform transform)
        {
            var names = new List<string>();
            for (var t = transform; t != null; t = t.parent)
                names.Add(t.name);
            names.Reverse();
            return transform.gameObject.scene.name + ":" + string.Join("/", names);
        }

        private static string LabelOf(GameObject go)
        {
            var parts = new List<string>();
            foreach (var text in go.GetComponentsInChildren<TMPro.TMP_Text>(false))
            {
                if (text != null && text.isActiveAndEnabled && !string.IsNullOrWhiteSpace(text.text))
                    parts.Add(text.text);
            }
            foreach (var text in go.GetComponentsInChildren<Text>(false))
            {
                if (text != null && text.isActiveAndEnabled && !string.IsNullOrWhiteSpace(text.text))
                    parts.Add(text.text);
            }
            return string.Join(" / ", parts);
        }

        public string DumpUiTree(string filter, int maxDepth)
        {
            try
            {
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                string path = Path.Combine(Paths.BepInExRootPath, "uitree-" + stamp + ".txt");
                return UiTreeDump.Write(filter, maxDepth, path);
            }
            catch (Exception e)
            {
                return "uitree failed: " + e.GetType().Name + ": " + e.Message;
            }
        }

        // ---- Scene --------------------------------------------------------------------------------------

        public string DescribeScene()
        {
            var sb = new StringBuilder();
            var active = SceneManager.GetActiveScene();
            sb.Append("active scene '").Append(active.name).Append("' (build index ").Append(active.buildIndex).Append(')');
            sb.Append("\nloaded scenes:");
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                sb.Append(i == 0 ? " " : ", ").Append('\'').Append(scene.name).Append('\'').Append(scene.isLoaded ? string.Empty : " (loading)");
            }
            sb.Append("\ntimeScale ").Append(Time.timeScale.ToString("0.###", CultureInfo.InvariantCulture))
                .Append(", frame ").Append(Time.frameCount)
                .Append(", cursor ").Append(Cursor.lockState).Append(Cursor.visible ? " visible" : " hidden")
                .Append(", window ").Append(Application.isFocused ? "focused" : "NOT focused")
                .Append(' ').Append(Screen.width).Append('x').Append(Screen.height);
            Guard(sb, "\nmenus: ", DescribeMenus);
            Guard(sb, "\ngame: ", DescribeGame);
            Guard(sb, "\nmultiplayer window: ", () => LobbyWindowOrNull() is LobbyWindow w ? (w.Visible ? "open" : "closed") : "not found");
            Guard(sb, "\ninput: ", () =>
            {
                var keyboard = Keyboard.current;
                return "keyboard " + (keyboard == null ? "none" : "'" + keyboard.name + "'" + (keyboard.enabled ? " enabled" : " DISABLED"))
                       + ", BepInEx " + HarnessInput.Backend;
            });
            return sb.ToString();
        }

        public string DescribeVideos()
        {
            var sb = new StringBuilder();
            var players = Compat.FindAll<UnityEngine.Video.VideoPlayer>(true);
            sb.Append(players.Length).Append(" video player(s):");
            foreach (var vp in players)
            {
                if (vp == null)
                    continue;
                sb.Append("\n  ").Append(PathOf(vp.transform))
                    .Append(vp.gameObject.activeInHierarchy ? " active" : " INACTIVE").Append(vp.enabled ? string.Empty : " disabled")
                    .Append(vp.isPlaying ? " playing" : " stopped").Append(vp.isPrepared ? string.Empty : " unprepared")
                    .Append(" clip ").Append(vp.clip != null ? vp.clip.name : "-").Append(" frame ").Append(vp.frame)
                    .Append(" mode ").Append(vp.renderMode)
                    .Append(" target ").Append(vp.targetTexture != null ? vp.targetTexture.name + "#" + vp.targetTexture.GetInstanceID() : "-");
            }
            sb.Append("\nquads with a video or drawing:");
            foreach (var renderer in Compat.FindAll<Renderer>(true))
            {
                if (renderer == null)
                    continue;
                foreach (var material in renderer.sharedMaterials)
                {
                    if (material == null || !material.HasProperty("_BaseMap"))
                        continue;
                    var texture = material.GetTexture("_BaseMap");
                    if (!(texture is RenderTexture) && (texture == null || !texture.name.StartsWith("NGGMP", StringComparison.Ordinal)))
                        continue;
                    var t = renderer.transform;
                    sb.Append("\n  ").Append(PathOf(t))
                        .Append(renderer.gameObject.activeInHierarchy ? " active" : " INACTIVE").Append(renderer.enabled ? string.Empty : " disabled")
                        .Append(" material ").Append(material.name).Append('#').Append(material.GetInstanceID())
                        .Append(" shows ").Append(texture.name).Append('#').Append(texture.GetInstanceID())
                        .Append(" pos ").Append(t.localPosition.ToString("0.###")).Append(" scale ").Append(t.localScale.ToString("0.###"));
                }
            }
            sb.Append("\nfemale overlay: ").Append(NGGMultiplayer.Game.Presence.LookVideo.DescribeCurrent());
            return sb.ToString();
        }

        private static string DescribeMenus()
        {
            // Answers #58/#112: the PauseMenu object is inactive while closed; the settings submenu deactivates it
            // but the game stays paused. The integration tracks the same as GameContext.PauseMenuOpen.
            bool pauseOpen = AnyActive(Compat.FindAll<PauseMenu>(true));
            bool settingsOpen = AnyActive(Compat.FindAll<SettingsMenu>(true));
            var ctx = GameContext.Current;
            return "pause menu " + (pauseOpen ? "OPEN" : "closed") + ", settings menu " + (settingsOpen ? "OPEN" : "closed")
                   + (ctx != null ? ", integration says paused " + ctx.PauseMenuOpen : string.Empty)
                   + ", main menu " + (Compat.FindAny<MainMenu>(false) != null ? "present" : "absent");
        }

        private static string DescribeGame()
        {
            var sb = new StringBuilder();
            var manager = SaveManager.instance;
            sb.Append("mode ").Append(manager != null && manager.m_gamemodeState != null ? manager.m_gamemodeState.mode.ToString() : "unknown");
            var ctx = GameContext.Current;
            if (ctx != null)
                sb.Append(", integration scene ").Append(ctx.Scene).Append(ctx.WorldReady ? " (world ready)" : string.Empty);
            var player = MoveAndHitController.instance;
            if (player != null)
                sb.Append(", player ").Append(player.m_mode).Append(player.m_moveLocked ? " (move locked: dialogue or cutscene)" : string.Empty);
            else
                sb.Append(", no player (not in Main)");
            return sb.ToString();
        }

        private static bool AnyActive<T>(T[] components) where T : Component
        {
            foreach (var component in components)
            {
                if (component != null && component.gameObject.activeInHierarchy)
                    return true;
            }
            return false;
        }

        private static void Guard(StringBuilder sb, string label, Func<string> read)
        {
            sb.Append(label);
            try
            {
                sb.Append(read());
            }
            catch (Exception e)
            {
                sb.Append("unreadable (").Append(e.GetType().Name).Append(": ").Append(e.Message).Append(')');
            }
        }

        // ---- Window and quit ----------------------------------------------------------------------------

        private LobbyWindow LobbyWindowOrNull() => _runner != null ? _runner.GetComponent<LobbyWindow>() : null;

        public bool SetLobbyWindow(bool? visible, out bool nowVisible, out string error)
        {
            nowVisible = false;
            error = null;
            var window = LobbyWindowOrNull();
            if (window == null)
            {
                error = "no LobbyWindow on the plugin's object";
                return false;
            }
            window.Visible = visible ?? !window.Visible;
            nowVisible = window.Visible;
            _busyUntilFrame = Time.frameCount + 2;
            return true;
        }

        public bool SetPauseMenu(bool open, out string message)
        {
            var pause = Compat.FindAny<PauseMenu>(false);
            if (!open)
            {
                if (pause == null)
                {
                    message = "the pause menu is not open";
                    return false;
                }
                pause.Exit();
                _busyUntilFrame = Time.frameCount + 2;
                message = "PauseMenu.Exit() called (the game's Resume)";
                return true;
            }
            if (pause != null)
            {
                message = "the pause menu is already open";
                return true;
            }
            var escape = Compat.FindAny<PlayerPressedEscape>(false);
            if (escape == null)
            {
                message = "no PlayerPressedEscape in this scene";
                return false;
            }
            escape.OpenMenu();
            _busyUntilFrame = Time.frameCount + 2;
            message = "PlayerPressedEscape.OpenMenu() called (what Escape does)";
            return true;
        }

        public string Quit()
        {
            var menu = Compat.FindAny<MainMenu>(false);
            if (menu != null)
            {
                _runner.StartCoroutine(QuitAfter(QuitFallbackSeconds));
                menu.Quit();
                return $"MainMenu.Quit() called (the game's own Quit button); Application.Quit() follows in {QuitFallbackSeconds:0} s " +
                       "if the game still runs";
            }
            _runner.StartCoroutine(QuitAfter(0f));
            return "Application.Quit() at the next frame (no MainMenu loaded; in Main the game's own way out is the pause " +
                   "menu's Save and Quit, which saves the run and returns to the main menu)";
        }

        private static IEnumerator QuitAfter(float seconds)
        {
            float at = Time.realtimeSinceStartup + seconds;
            yield return null;
            while (Time.realtimeSinceStartup < at)
                yield return null;
            Application.Quit();
        }
    }
}
