using System;
using System.Collections.Generic;
using NGGMultiplayer.Core.Diagnostics;
using NGGMultiplayer.UI.Native;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NGGMultiplayer.Game.Menus
{
    /// <summary>
    /// Draws a <see cref="MenuScreen"/> the way the game draws its settings screens: the dark striped backing, a
    /// rimmed frame, the settings' blue buttons (with the game's punch animation and click sound), its moving
    /// keyboard / gamepad highlight and Cancel = Back through a GamepadMenuInput of its own. Built from clones of the
    /// scene's SETTINGS MENU pieces into its own overlay canvas above every game menu. Buttons are rebuilt only when
    /// a screen's layout changes; texts update in place.
    /// </summary>
    internal sealed class MenuDialog
    {
        private const string Log = "[Menus] ";
        private const float Width = 640f;
        private const float ButtonHeight = 84f;
        private const float RowHeight = 100f;
        private const float Spacing = 7.18f;

        // The settings screens' blue and Back blue; green as Play, red as Delete, teal for inviting.
        private static readonly Color Normal = new Color(0.275f, 0.365f, 0.549f);
        private static readonly Color BackColor = new Color(0.157f, 0.224f, 0.357f);
        private static readonly Color Primary = new Color(0.185f, 0.702f, 0.42f);
        private static readonly Color Danger = new Color(0.736f, 0.183f, 0.149f);
        private static readonly Color Accent = new Color(0.16f, 0.62f, 0.62f);
        private static readonly Color RowColor = new Color(0.22f, 0.30f, 0.46f);

        private readonly Action<string> _activate;
        private readonly Action<string, int> _change;
        private readonly List<Entry> _entries = new List<Entry>();
        private GameObject _root;
        private RectTransform _frame;
        private TextMeshProUGUI _title;
        private TextMeshProUGUI _subtitle;
        private TextMeshProUGUI _empty;
        private Image _highlight;
        private Behaviour _gamepad;
        private Button _buttonTemplate;
        private string _layout;
        private Button _back;
        private bool _navigationHeld;

        public MenuDialog(Action<string> activate, Action<string, int> change)
        {
            _activate = activate;
            _change = change;
        }

        public bool Built => _root != null;

        public bool Visible => _root != null && _root.activeSelf;

        /// <summary>The screen's GamepadMenuInput presses its Back button on the game's Cancel action.</summary>
        public bool HandlesCancel => _gamepad != null && _gamepad.isActiveAndEnabled && _back != null && _back.isActiveAndEnabled;

        /// <summary>Builds the canvas from the scene's SETTINGS MENU (inactive ones too). False when its pieces are missing.</summary>
        public bool Build()
        {
            if (_root != null)
                return true;
            var settings = Compat.FindAny<SettingsMenu>(true);
            if (settings == null)
            {
                NetLog.Warn($"{Log}No SETTINGS MENU in this scene to build the multiplayer screens from.");
                return false;
            }
            var settingsRoot = settings.transform;
            var backing = MenuKit.FindDeep(settingsRoot, "BACKING")?.GetComponent<Image>();
            var frame = MenuKit.FindDeep(settingsRoot, "SETTINGS FRAME")?.GetComponent<Image>();
            var highlight = frame != null ? frame.transform.Find("highlight")?.GetComponent<Image>() : null;
            var button = MenuKit.FindDeep(settingsRoot, "Sound Settings")?.GetComponent<Button>()
                         ?? (frame != null ? frame.GetComponentInChildren<Button>(true) : null);
            var scaler = settings.GetComponent<CanvasScaler>();
            if (frame == null || button == null || MenuKit.LabelOf(button.transform) == null)
            {
                NetLog.Warn($"{Log}The SETTINGS MENU pieces were not found (frame {frame != null}, button {button != null}).");
                return false;
            }
            try
            {
                BuildCanvas(backing, frame, highlight, button, scaler);
                return true;
            }
            catch (Exception e)
            {
                NetLog.Error($"{Log}Building the multiplayer screens failed: {e}");
                Destroy();
                return false;
            }
        }

        private void BuildCanvas(Image backing, Image frameTemplate, Image highlightTemplate, Button button, CanvasScaler scalerTemplate)
        {
            _root = new GameObject("NGGMP MULTIPLAYER MENU");
            _root.SetActive(false);
            _root.layer = frameTemplate.gameObject.layer;
            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 1000;
            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = scalerTemplate != null ? scalerTemplate.referenceResolution : new Vector2(1920, 1080);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = scalerTemplate != null ? scalerTemplate.matchWidthOrHeight : 1f;
            _root.AddComponent<GraphicRaycaster>();

            if (backing != null)
            {
                var back = MenuKit.CloneImage(backing, _root.transform, "BACKING");
                Stretch(back.rectTransform);
            }

            var frameImage = MenuKit.CloneImage(frameTemplate, _root.transform, "FRAME");
            _frame = frameImage.rectTransform;
            _frame.anchorMin = _frame.anchorMax = new Vector2(0.5f, 0.5f);
            _frame.pivot = new Vector2(0.5f, 0.5f);
            _frame.anchoredPosition = Vector2.zero;
            _frame.sizeDelta = new Vector2(Width, 300f);
            var layout = frameImage.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(30, 30, 26, 30);
            layout.spacing = Spacing;
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            var fitter = frameImage.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var labelTemplate = MenuKit.LabelOf(button.transform);
            _title = Text(labelTemplate, "TITLE", 42f, 64f, Color.white);
            _subtitle = Text(labelTemplate, "SUBTITLE", 24f, -1f, new Color(0.82f, 0.86f, 0.92f));
            _empty = Text(labelTemplate, "EMPTY", 24f, -1f, new Color(0.7f, 0.75f, 0.82f));

            _buttonTemplate = MenuKit.CloneButton(button, _root.transform, "BUTTON TEMPLATE");
            _buttonTemplate.gameObject.SetActive(false);

            if (highlightTemplate != null)
            {
                _highlight = MenuKit.CloneImage(highlightTemplate, _frame, "highlight");
                _highlight.raycastTarget = false;
                _highlight.enabled = false;
                _highlight.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            }
            _gamepad = MenuKit.AddGamepadMenu(frameImage.gameObject, _highlight);
        }

        private TextMeshProUGUI Text(TMP_Text template, string name, float size, float height, Color color)
        {
            var text = MenuKit.CloneLabel(template, _frame, name);
            text.enableAutoSizing = false;
            text.fontSize = size;
            text.color = color;
            text.alignment = TextAlignmentOptions.Center;
            text.richText = true;
            var element = text.gameObject.AddComponent<LayoutElement>();
            if (height > 0)
            {
                element.minHeight = height;
                element.preferredHeight = height;
            }
            return text;
        }

        public void Show(MenuScreen screen)
        {
            if (_root == null || screen == null)
                return;
            bool opening = !_root.activeSelf;
            bool rebuilt = screen.Layout != _layout;
            string selectedId = rebuilt && !opening ? SelectedId() : null;
            if (rebuilt)
                Rebuild(screen);
            Fill(screen);
            if (opening)
            {
                _root.SetActive(true);
                Canvas.ForceUpdateCanvases();
                LayoutRebuilder.ForceRebuildLayoutImmediate(_frame);
            }
            if (rebuilt)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(_frame);
                var first = FirstSelectable();
                MenuKit.ConfigureGamepadMenu(_gamepad, first, _back);
                MenuKit.RescanGamepadMenu(_gamepad);
                // Keep the selection on the same entry across a rebuild (an entry appearing or going).
                if (MenuKit.Navigating)
                {
                    var keep = selectedId != null ? Find(selectedId) : null;
                    MenuKit.Select(keep != null && keep.Button.interactable ? keep.Button.gameObject : first);
                }
            }
            CatchNavigation();
        }

        /// <summary>
        /// GamepadMenuInput only reacts when the selection changes, so with nothing (or something outside this screen)
        /// selected, as after opening it with the mouse, the arrow keys would do nothing. The first direction press then
        /// selects the screen's first button, as the game's own screens do when navigation starts.
        /// </summary>
        private void CatchNavigation()
        {
            bool pressed = MenuKit.NavigationPressed();
            bool started = pressed && !_navigationHeld;
            _navigationHeld = pressed;
            if (!started || Find(SelectedEntryId()) != null)
                return;
            MenuKit.Select(FirstSelectable());
        }

        private string SelectedEntryId() => SelectedId() ?? string.Empty;

        public void Hide()
        {
            if (_root == null || !_root.activeSelf)
                return;
            ParkHighlight();
            _root.SetActive(false);
            _layout = null;
        }

        public void Destroy()
        {
            if (_root != null)
                UnityEngine.Object.Destroy(_root);
            _root = null;
            _entries.Clear();
            _layout = null;
        }

        /// <summary>Presses the screen's Back (or its "no" / "cancel"), as the game's Cancel does.</summary>
        public bool PressBack()
        {
            if (_back == null || !_back.isActiveAndEnabled)
                return false;
            MenuKit.PlayPressedSound();
            _back.onClick.Invoke();
            return true;
        }

        private void Rebuild(MenuScreen screen)
        {
            ParkHighlight();
            foreach (var entry in _entries)
            {
                if (entry.Button != null)
                {
                    entry.Button.gameObject.SetActive(false);
                    UnityEngine.Object.Destroy(entry.Button.gameObject);
                }
            }
            _entries.Clear();
            _back = null;
            _layout = screen.Layout;

            var buttons = new List<Button>();
            foreach (var item in screen.Items)
            {
                var button = MenuKit.CloneButton(_buttonTemplate, _frame, "MP " + item.Id);
                bool row = item.Kind == MenuItemKind.Row;
                var element = button.gameObject.GetComponent<LayoutElement>() ?? button.gameObject.AddComponent<LayoutElement>();
                element.minHeight = element.preferredHeight = row ? RowHeight : ButtonHeight;
                var label = MenuKit.LabelOf(button.transform);
                if (label != null)
                {
                    label.richText = true;
                    if (row)
                    {
                        label.enableAutoSizing = false;
                        label.fontSize = 30f;
                    }
                }
                string id = item.Id;
                button.onClick.AddListener(() => _activate(id));
                if (item.Kind == MenuItemKind.Selector)
                {
                    var selector = button.gameObject.AddComponent<SelectorInput>();
                    selector.Changed = step =>
                    {
                        MenuKit.PlayPressedSound();
                        _change(id, step);
                    };
                }
                var entry = new Entry(item.Id, item.Kind, button, label);
                _entries.Add(entry);
                buttons.Add(button);
                if (item.Style == MenuStyle.Back || item.Id == "no" || item.Id == "cancel")
                    _back = button;
            }
            // Vertical navigation with wrap-around, like the pause menu.
            for (int i = 0; i < buttons.Count; i++)
            {
                var up = buttons[(i - 1 + buttons.Count) % buttons.Count];
                var down = buttons[(i + 1) % buttons.Count];
                MenuKit.Link(buttons[i], up, down, null, null);
            }
        }

        private void Fill(MenuScreen screen)
        {
            Set(_title, screen.Title);
            Set(_subtitle, screen.Subtitle);
            Set(_empty, screen.Empty);
            foreach (var item in screen.Items)
            {
                var entry = Find(item.Id);
                if (entry == null)
                    continue;
                string text = item.Kind == MenuItemKind.Row
                    ? item.Label + (item.Detail.Length > 0 ? "\n<size=72%><alpha=#C8>" + item.Detail + "</size>" : string.Empty)
                    : item.Kind == MenuItemKind.Selector ? item.Label + ":  < " + item.Value + " >" : item.Label;
                if (entry.Label != null && entry.Label.text != text)
                    entry.Label.text = text;
                var color = ColorFor(item);
                if (entry.Button.image != null && entry.Button.image.color != color)
                    entry.Button.image.color = color;
                bool interactable = item.Enabled;
                if (entry.Button.interactable != interactable)
                {
                    entry.Button.interactable = interactable;
                    if (item.Kind == MenuItemKind.Row)
                    {
                        // A row that cannot be picked (another player as a client, a game of another version) stays readable.
                        var colors = entry.Button.colors;
                        colors.disabledColor = new Color(0.86f, 0.86f, 0.86f, 1f);
                        entry.Button.colors = colors;
                    }
                }
            }
        }

        private static Color ColorFor(MenuItem item)
        {
            if (item.Kind == MenuItemKind.Row)
                return RowColor;
            switch (item.Style)
            {
                case MenuStyle.Primary: return Primary;
                case MenuStyle.Danger: return Danger;
                case MenuStyle.Back: return BackColor;
                case MenuStyle.Accent: return Accent;
                default: return Normal;
            }
        }

        private static void Set(TextMeshProUGUI text, string value)
        {
            if (text == null)
                return;
            bool show = !string.IsNullOrEmpty(value);
            if (text.gameObject.activeSelf != show)
                text.gameObject.SetActive(show);
            if (show && text.text != value)
                text.text = value;
        }

        private GameObject FirstSelectable()
        {
            foreach (var entry in _entries)
            {
                if (entry.Button.interactable)
                    return entry.Button.gameObject;
            }
            return _entries.Count > 0 ? _entries[0].Button.gameObject : null;
        }

        private string SelectedId()
        {
            var selected = MenuKit.Selected;
            foreach (var entry in _entries)
            {
                if (entry.Button.gameObject == selected)
                    return entry.Id;
            }
            return null;
        }

        private Entry Find(string id)
        {
            foreach (var entry in _entries)
            {
                if (entry.Id == id)
                    return entry;
            }
            return null;
        }

        /// <summary>The highlight lives under the selected button (GamepadMenuInput moves it); keep it out of buttons being destroyed.</summary>
        private void ParkHighlight()
        {
            if (_highlight == null)
                return;
            _highlight.transform.SetParent(_frame, false);
            _highlight.enabled = false;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private sealed class Entry
        {
            public Entry(string id, MenuItemKind kind, Button button, TextMeshProUGUI label)
            {
                Id = id;
                Kind = kind;
                Button = button;
                Label = label;
            }

            public string Id { get; }
            public MenuItemKind Kind { get; }
            public Button Button { get; }
            public TextMeshProUGUI Label { get; }
        }
    }
}
