using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using NGGMultiplayer.Core.Diagnostics;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace NGGMultiplayer.Game.Menus
{
    /// <summary>
    /// Building blocks for menus made of the game's own pieces (docs/gamemap/06-menus.md): cloning a button, panel
    /// or frame and stripping what belongs to the original (its onClick, localisation, helpers), and the game's UI
    /// scripts reached by name (GamepadMenuInput, DVDBouncer, BounceCount, InputManager's input-mode flags), so a
    /// renamed member turns a feature off instead of breaking the build.
    /// </summary>
    internal static class MenuKit
    {
        private const string Log = "[Menus] ";

        public static readonly Type GamepadMenuInputType = AccessTools.TypeByName("GamepadMenuInput");
        public static readonly Type DvdBouncerType = AccessTools.TypeByName("DVDBouncer");
        public static readonly Type BounceCountType = AccessTools.TypeByName("BounceCount");
        public static readonly Type AudioManagerType = AccessTools.TypeByName("AudioManager");

        /// <summary>Components a cloned button keeps: its look, the Button, and the game's punch / sound animator.</summary>
        private static readonly HashSet<string> ButtonParts = new HashSet<string>(StringComparer.Ordinal)
        {
            "RectTransform", "CanvasRenderer", "Image", "Button", "ButtonPunchAnimator", "LayoutElement"
        };

        private static readonly HashSet<string> LabelParts = new HashSet<string>(StringComparer.Ordinal)
        {
            "RectTransform", "CanvasRenderer", "TextMeshProUGUI"
        };

        private static readonly HashSet<string> ImageParts = new HashSet<string>(StringComparer.Ordinal)
        {
            "RectTransform", "CanvasRenderer", "Image"
        };

        // ---- Finding --------------------------------------------------------------------------------------

        /// <summary>The direct or nested child called <paramref name="name"/> (inactive ones too), or null.</summary>
        public static Transform FindDeep(Transform root, string name)
        {
            if (root == null)
                return null;
            if (root.name == name)
                return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var hit = FindDeep(root.GetChild(i), name);
                if (hit != null)
                    return hit;
            }
            return null;
        }

        /// <summary>The first Button below <paramref name="root"/> whose onClick calls <paramref name="method"/>.</summary>
        public static Button FindButtonCalling(Transform root, string method)
        {
            if (root == null)
                return null;
            foreach (var button in root.GetComponentsInChildren<Button>(true))
            {
                var ev = button.onClick;
                for (int i = 0; i < ev.GetPersistentEventCount(); i++)
                {
                    if (ev.GetPersistentMethodName(i) == method)
                        return button;
                }
            }
            return null;
        }

        // ---- Cloning --------------------------------------------------------------------------------------

        /// <summary>
        /// A copy of a game button under <paramref name="parent"/>: same sprite, colour block, punch animation and
        /// sounds; a fresh onClick; only its LABEL child kept (badges such as the new-ball "!" are dropped), without
        /// localisation. Clone into an inactive parent so no Awake of the original's scripts runs before the strip.
        /// </summary>
        public static Button CloneButton(Button template, Transform parent, string name)
        {
            var go = UnityEngine.Object.Instantiate(template.gameObject, parent, false);
            go.name = name;
            go.SetActive(true);
            var label = LabelOf(go.transform);
            for (int i = go.transform.childCount - 1; i >= 0; i--)
            {
                var child = go.transform.GetChild(i);
                if (label == null || child != label.transform)
                    UnityEngine.Object.DestroyImmediate(child.gameObject);
            }
            Strip(go, ButtonParts);
            if (label != null)
                Strip(label.gameObject, LabelParts);
            var button = go.GetComponent<Button>();
            button.onClick = new Button.ButtonClickedEvent();
            button.interactable = true;
            go.transform.localScale = Vector3.one;
            return button;
        }

        /// <summary>A copy of an Image-only object (panel fill, frame, backing, highlight) with no children.</summary>
        public static Image CloneImage(Image template, Transform parent, string name)
        {
            var go = UnityEngine.Object.Instantiate(template.gameObject, parent, false);
            go.name = name;
            for (int i = go.transform.childCount - 1; i >= 0; i--)
                UnityEngine.Object.DestroyImmediate(go.transform.GetChild(i).gameObject);
            Strip(go, ImageParts);
            go.SetActive(true);
            var image = go.GetComponent<Image>();
            image.enabled = true;
            return image;
        }

        /// <summary>A copy of a label (font, material, colour) as a plain text object.</summary>
        public static TextMeshProUGUI CloneLabel(TMP_Text template, Transform parent, string name)
        {
            var go = UnityEngine.Object.Instantiate(template.gameObject, parent, false);
            go.name = name;
            for (int i = go.transform.childCount - 1; i >= 0; i--)
                UnityEngine.Object.DestroyImmediate(go.transform.GetChild(i).gameObject);
            Strip(go, LabelParts);
            go.SetActive(true);
            var text = go.GetComponent<TextMeshProUGUI>();
            text.raycastTarget = false;
            return text;
        }

        /// <summary>The button's label (its LABEL child, else any TMP text below it).</summary>
        public static TextMeshProUGUI LabelOf(Transform button)
        {
            var named = button.Find("LABEL");
            if (named != null && named.GetComponent<TextMeshProUGUI>() != null)
                return named.GetComponent<TextMeshProUGUI>();
            return button.GetComponentInChildren<TextMeshProUGUI>(true);
        }

        /// <summary>Removes every component whose type name is not in <paramref name="keep"/> (dependents first).</summary>
        public static void Strip(GameObject go, HashSet<string> keep)
        {
            // Several passes: a component another one requires can only go once that one is gone.
            for (int pass = 0; pass < 3; pass++)
            {
                bool left = false;
                foreach (var c in go.GetComponents<Component>())
                {
                    if (c == null || c is Transform || keep.Contains(c.GetType().Name))
                        continue;
                    try
                    {
                        UnityEngine.Object.DestroyImmediate(c);
                    }
                    catch (Exception)
                    {
                        left = true;
                    }
                }
                if (!left)
                    return;
            }
        }

        public static void SetColor(Button button, Color color)
        {
            if (button != null && button.image != null)
                button.image.color = color;
        }

        public static void SetText(Button button, string text)
        {
            var label = button != null ? LabelOf(button.transform) : null;
            if (label != null && label.text != text)
                label.text = text;
        }

        // ---- Navigation -----------------------------------------------------------------------------------

        public static void Link(Selectable from, Selectable up, Selectable down, Selectable left, Selectable right)
        {
            if (from == null)
                return;
            var nav = from.navigation;
            nav.mode = Navigation.Mode.Explicit;
            nav.selectOnUp = up;
            nav.selectOnDown = down;
            nav.selectOnLeft = left;
            nav.selectOnRight = right;
            from.navigation = nav;
        }

        public static void SetRight(Selectable from, Selectable right)
        {
            if (from == null)
                return;
            var nav = from.navigation;
            nav.selectOnRight = right;
            from.navigation = nav;
        }

        // ---- The game's UI scripts ------------------------------------------------------------------------

        /// <summary>The GamepadMenuInput on <paramref name="go"/>, or null.</summary>
        public static Behaviour GamepadMenu(GameObject go) =>
            go != null && GamepadMenuInputType != null ? go.GetComponent(GamepadMenuInputType) as Behaviour : null;

        /// <summary>
        /// Adds a GamepadMenuInput (the game's keyboard / gamepad selection, its moving highlight and Cancel = Back)
        /// to an INACTIVE object, so its OnEnable runs only once the fields are set.
        /// </summary>
        public static Behaviour AddGamepadMenu(GameObject go, Image highlight)
        {
            if (GamepadMenuInputType == null)
                return null;
            var menu = go.AddComponent(GamepadMenuInputType) as Behaviour;
            SetField(menu, "m_highlight", highlight);
            SetField(menu, "m_addAllButtons", false);
            SetField(menu, "m_showAlways", false);
            return menu;
        }

        /// <summary>Points a GamepadMenuInput at its default selection and its Back button, and rescans its buttons.</summary>
        public static void ConfigureGamepadMenu(Behaviour menu, GameObject defaultButton, Button back)
        {
            if (menu == null)
                return;
            SetField(menu, "m_defaultButton", defaultButton);
            SetField(menu, "buttonToPressOnBack", back);
            SetField(menu, "m_lastSelected", null);
        }

        /// <summary>GamepadMenuInput.OnEnable: rebuilds its button list from the active buttons and selects when navigating.</summary>
        public static void RescanGamepadMenu(Behaviour menu)
        {
            if (menu == null || !menu.isActiveAndEnabled)
                return;
            Invoke(menu, "OnEnable");
        }

        /// <summary>The player is navigating with keys / a gamepad (the game shows its selection highlight then).</summary>
        public static bool Navigating
        {
            get
            {
                var input = InputManager.instance;
                if (input == null)
                    return false;
                if (input.m_isUsingGamePad)
                    return true;
                return GetField(input, "m_navigatingWithDirections") is bool b && b;
            }
        }

        /// <summary>A menu direction (the game's UI+Move: arrows, WASD, stick, dpad) is held this frame.</summary>
        public static bool NavigationPressed()
        {
            try
            {
                var input = InputManager.instance;
                var actions = input != null && input.input != null ? input.input.actions : null;
                var move = actions?.FindAction("UI+Move", false);
                return move != null && move.ReadValue<Vector2>().sqrMagnitude > 0.25f;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static void Select(GameObject go)
        {
            var system = EventSystem.current;
            if (system != null && go != null && go.activeInHierarchy)
                system.SetSelectedGameObject(go);
        }

        public static GameObject Selected => EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;

        /// <summary>The game's button press sound (AudioManager.Instance.PlayButtonPressedSound), for changes that are not a click.</summary>
        public static void PlayPressedSound()
        {
            if (AudioManagerType == null)
                return;
            try
            {
                object manager = AccessTools.Property(AudioManagerType, "Instance")?.GetValue(null, null)
                                 ?? AccessTools.Field(AudioManagerType, "Instance")?.GetValue(null);
                if (manager is UnityEngine.Object o && o != null)
                    AccessTools.Method(AudioManagerType, "PlayButtonPressedSound")?.Invoke(manager, null);
            }
            catch (Exception e)
            {
                NetLog.Debug($"{Log}Playing the button sound failed: {e.Message}");
            }
        }

        public static object GetField(object target, string name)
        {
            if (target == null)
                return null;
            try
            {
                return AccessTools.Field(target.GetType(), name)?.GetValue(target);
            }
            catch (Exception e)
            {
                NetLog.Debug($"{Log}Reading {target.GetType().Name}.{name} failed: {e.Message}");
                return null;
            }
        }

        public static bool SetField(object target, string name, object value)
        {
            if (target == null)
                return false;
            try
            {
                var field = AccessTools.Field(target.GetType(), name);
                if (field == null)
                {
                    NetLog.Warn($"{Log}{target.GetType().Name}.{name} not found.");
                    return false;
                }
                field.SetValue(target, value);
                return true;
            }
            catch (Exception e)
            {
                NetLog.Warn($"{Log}Setting {target.GetType().Name}.{name} failed: {e.Message}");
                return false;
            }
        }

        public static bool Invoke(object target, string method, params object[] args)
        {
            if (target == null)
                return false;
            try
            {
                var m = AccessTools.Method(target.GetType(), method);
                if (m == null)
                {
                    NetLog.Warn($"{Log}{target.GetType().Name}.{method} not found.");
                    return false;
                }
                m.Invoke(target, args);
                return true;
            }
            catch (TargetInvocationException e)
            {
                NetLog.Warn($"{Log}{target.GetType().Name}.{method} threw: {(e.InnerException ?? e).Message}");
                return false;
            }
            catch (Exception e)
            {
                NetLog.Warn($"{Log}Calling {target.GetType().Name}.{method} failed: {e.Message}");
                return false;
            }
        }
    }
}
