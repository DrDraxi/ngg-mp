using System;
using NGGMultiplayer.Core.Diagnostics;
using NGGMultiplayer.UI.Native;
using UnityEngine;
using UnityEngine.UI;

namespace NGGMultiplayer.Game.Menus
{
    /// <summary>
    /// The multiplayer panel on the main menu (Menu2): a copy of the bottom-left "Watch Making Of / Join the Discord"
    /// panel (glass fill, gold rim, two coloured buttons) mirrored to the bottom right. It rises with the left panel
    /// when the menu appears, joins the keyboard / gamepad navigation (right of Just Play Nine and Quit), and joins
    /// the DVD bounce with the game's own DVDBouncer once the bounce counter reaches <see cref="BounceAt"/>. Its two
    /// buttons follow the session (<see cref="MenuController.PanelButtons"/>).
    /// </summary>
    internal sealed class MainMenuPanel
    {
        private const string Log = "[Menus] ";
        /// <summary>The play panel joins at 10 bounces and the left one at 100; this one in between.</summary>
        public const int BounceAt = 30;
        private const float RestY = 92f;
        private const float IntroTimeoutSeconds = 8f;

        private static readonly Color Teal = new Color(0.16f, 0.70f, 0.64f);
        private static readonly Color Pink = new Color(0.86f, 0.40f, 0.56f);
        private static readonly Color Red = new Color(0.698f, 0.314f, 0.317f);
        private static readonly Color Busy = new Color(0.45f, 0.52f, 0.62f);

        private readonly Action<string> _clicked;
        private RectTransform _panel;
        private RectTransform _extras;
        private Button _top;
        private Button _bottom;
        private string _topId = string.Empty;
        private string _bottomId = string.Empty;
        private Component _bouncer;
        private Component _bounceCount;
        private Button _play;
        private Button _playNine;
        private bool _landed;
        private bool _sawBelow;
        private float _builtAt;
        private bool _bouncing;

        public MainMenuPanel(Action<string> clicked)
        {
            _clicked = clicked;
        }

        public bool Built => _panel != null;

        /// <summary>The main menu canvas's GamepadMenuInput (disabled while the multiplayer screens are open).</summary>
        public Behaviour GamepadMenu { get; private set; }

        /// <summary>Builds the panel from the scene's main menu. False when its pieces are missing.</summary>
        public bool Build()
        {
            if (_panel != null)
                return true;
            var menu = Compat.FindAny<MainMenu>(true);
            if (menu == null)
                return false;
            var root = menu.transform;
            var extras = MenuKit.FindDeep(root, "ExtrasFrame") as RectTransform;
            if (extras == null || extras.GetComponentsInChildren<Button>(true).Length < 2)
            {
                NetLog.Warn($"{Log}The main menu's ExtrasFrame panel was not found; the multiplayer panel is off.");
                return false;
            }
            try
            {
                _extras = extras;
                _builtAt = Time.realtimeSinceStartup;
                var clone = UnityEngine.Object.Instantiate(extras.gameObject, extras.parent, false);
                clone.name = "MultiplayerFrame";
                _panel = clone.GetComponent<RectTransform>();
                _panel.SetSiblingIndex(extras.GetSiblingIndex() + 1);
                _panel.anchorMin = _panel.anchorMax = new Vector2(1f, 0f);
                _panel.anchoredPosition = new Vector2(-extras.anchoredPosition.x, extras.anchoredPosition.y);

                var buttons = clone.GetComponentsInChildren<Button>(true);
                _top = Rebuild(buttons[0], "MP Top");
                _bottom = Rebuild(buttons[1], "MP Bottom");
                _top.onClick.AddListener(() => _clicked(_topId));
                _bottom.onClick.AddListener(() => _clicked(_bottomId));

                LinkNavigation(root);
                SetUpBounce(clone, root);
                _play = MenuKit.FindButtonCalling(root, "PlayGame");
                _playNine = MenuKit.FindButtonCalling(root, "PlayNineButtonPressed");

                GamepadMenu = MenuKit.GamepadMenu(root.gameObject);
                MenuKit.RescanGamepadMenu(GamepadMenu);
                NetLog.Info($"{Log}Multiplayer panel added to the main menu.");
                return true;
            }
            catch (Exception e)
            {
                NetLog.Error($"{Log}Building the main menu panel failed: {e}");
                if (_panel != null)
                    UnityEngine.Object.Destroy(_panel.gameObject);
                _panel = null;
                return false;
            }
        }

        /// <summary>Strips a cloned Discord / Making Of button down to the game's button with our click.</summary>
        private static Button Rebuild(Button cloned, string name)
        {
            cloned.name = name;
            foreach (var c in cloned.GetComponents<MonoBehaviour>())
            {
                if (c != null && c.GetType().Name == "OpenLink")
                    UnityEngine.Object.DestroyImmediate(c);
            }
            cloned.onClick = new Button.ButtonClickedEvent();
            return cloned;
        }

        private void LinkNavigation(Transform root)
        {
            var playNine = MenuKit.FindButtonCalling(root, "PlayNineButtonPressed");
            var quit = MenuKit.FindButtonCalling(root, "Quit");
            MenuKit.Link(_top, null, _bottom, playNine, null);
            MenuKit.Link(_bottom, _top, null, quit, null);
            MenuKit.SetRight(playNine, _top);
            MenuKit.SetRight(quit, _bottom);
        }

        /// <summary>The cloned DVDBouncer already points at this copy's rim and buttons; it bounces the other way.</summary>
        private void SetUpBounce(GameObject clone, Transform root)
        {
            if (MenuKit.DvdBouncerType == null)
                return;
            _bouncer = clone.GetComponent(MenuKit.DvdBouncerType);
            if (_bouncer is Behaviour b)
                b.enabled = false;
            if (MenuKit.GetField(_bouncer, "m_dir") is Vector2 dir)
                MenuKit.SetField(_bouncer, "m_dir", new Vector2(-dir.x, dir.y));
            if (MenuKit.BounceCountType != null)
                _bounceCount = root.GetComponentInChildren(MenuKit.BounceCountType, true);
        }

        /// <summary>Every frame in Menu2: the intro rise, the bounce start, and the buttons for the session.</summary>
        public void Tick(MenuSnapshot s, bool client)
        {
            if (_panel == null)
                return;
            FollowIntro();
            StartBounceWhenDue();
            var items = MenuController.PanelButtons(s);
            Apply(_top, items[0], ref _topId);
            Apply(_bottom, items[1], ref _bottomId);
            // A player in someone's session waits for the host to start; the play buttons are the host's.
            if (_play != null && _play.interactable == client)
                _play.interactable = !client;
            if (_playNine != null && _playNine.interactable == client)
                _playNine.interactable = !client;
        }

        /// <summary>The left panel starts below the screen and rises 1.5 s after the play panel; this one rises with it.</summary>
        private void FollowIntro()
        {
            if (_landed || _extras == null)
                return;
            float y = _extras.anchoredPosition.y;
            if (y < RestY - 1f)
                _sawBelow = true;
            // Built after the menu already appeared (or the intro never moved it): rest where the left panel rests.
            if (!_sawBelow && Time.realtimeSinceStartup - _builtAt > IntroTimeoutSeconds)
                y = RestY;
            _panel.anchoredPosition = new Vector2(_panel.anchoredPosition.x, y);
            if (Mathf.Abs(y - RestY) < 0.5f && (_sawBelow || Time.realtimeSinceStartup - _builtAt > IntroTimeoutSeconds))
                _landed = true;
        }

        private void StartBounceWhenDue()
        {
            if (_bouncing || !_landed || _bouncer == null || _bounceCount == null)
                return;
            if (!(MenuKit.GetField(_bounceCount, "bounceCounter") is int count) || count < BounceAt)
                return;
            _bouncing = true;
            if (_bouncer is Behaviour b)
                b.enabled = true;
            MenuKit.Invoke(_bouncer, "StartBounce");
            NetLog.Info($"{Log}The multiplayer panel joins the bounce at {count} bounces.");
        }

        private void Apply(Button button, MenuItem item, ref string id)
        {
            bool changed = id != item.Id;
            id = item.Id;
            MenuKit.SetText(button, item.Label);
            // While bouncing the game recolours the buttons at every wall hit: ours only when the button's role changes.
            if (!_bouncing || changed)
                MenuKit.SetColor(button, ColorFor(item));
            if (button.interactable != item.Enabled)
                button.interactable = item.Enabled;
        }

        private static Color ColorFor(MenuItem item)
        {
            switch (item.Id)
            {
                case "host":
                case "invite":
                    return Teal;
                case "leave":
                case "cancel":
                    return Red;
                case "busy":
                    return Busy;
                default:
                    return item.Style == MenuStyle.Accent ? Teal : Pink;
            }
        }

        /// <summary>The menu's Just Play Nine is there to press (shown, and unlocked when the game locks it).</summary>
        public bool CanPlayNine => _playNine != null && _playNine.gameObject.activeInHierarchy && _playNine.interactable;

        /// <summary>Presses the menu's Play or Just Play Nine, as the player would (the game's own screens follow).</summary>
        public bool Press(HostStart start)
        {
            var button = start == HostStart.PlayNine ? _playNine : start == HostStart.Play ? _play : null;
            if (button == null || !button.gameObject.activeInHierarchy || !button.interactable)
                return false;
            MenuKit.Select(button.gameObject);
            button.onClick.Invoke();
            return true;
        }

        /// <summary>Called when the scene goes away.</summary>
        public void Forget()
        {
            _panel = null;
            _extras = null;
            _bouncer = null;
            _bounceCount = null;
            _play = null;
            _playNine = null;
            GamepadMenu = null;
            _topId = _bottomId = string.Empty;
            _landed = false;
            _sawBelow = false;
            _bouncing = false;
        }
    }
}
