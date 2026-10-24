using System;
using NGGMultiplayer.Core.Diagnostics;
using UnityEngine;
using UnityEngine.UI;

namespace NGGMultiplayer.Game.Menus
{
    /// <summary>
    /// "Invite Friends" and "Multiplayer" in the pause menu, between Resume and Settings: copies of RESUME in the
    /// frame's VerticalLayoutGroup, in blues between Resume's and Settings', with the frame grown by one row per
    /// shown button (the layout group does not size the frame). Invite shows only while it can be used.
    /// </summary>
    internal sealed class PauseButtons
    {
        private const string Log = "[Menus] ";
        /// <summary>A pause button row: 100 high plus the layout's 7.18 spacing.</summary>
        private const float RowHeight = 107.18f;

        private static readonly Color InviteColor = new Color(0.16f, 0.58f, 0.58f);
        private static readonly Color MultiplayerColor = new Color(0.31f, 0.405f, 0.59f);

        private readonly Action _invite;
        private readonly Action _multiplayer;
        private PauseMenu _menu;
        private RectTransform _frame;
        private float _baseHeight;
        private Button _inviteButton;
        private Button _multiplayerButton;
        private int _shown = -1;

        public PauseButtons(Action invite, Action multiplayer)
        {
            _invite = invite;
            _multiplayer = multiplayer;
        }

        public bool Built => _menu != null;

        public PauseMenu Menu => _menu;

        /// <summary>The pause screen's GamepadMenuInput (disabled while the multiplayer screens are open).</summary>
        public Behaviour GamepadMenu => _menu != null ? MenuKit.GamepadMenu(_menu.gameObject) : null;

        public bool Build()
        {
            if (_menu != null)
                return true;
            var menu = Compat.FindAny<PauseMenu>(true);
            if (menu == null)
                return false;
            var resume = MenuKit.FindButtonCalling(menu.transform, "Exit") ?? MenuKit.FindDeep(menu.transform, "RESUME")?.GetComponent<Button>();
            var frame = MenuKit.FindDeep(menu.transform, "FRAME") as RectTransform;
            if (resume == null || frame == null || resume.transform.parent == null)
            {
                NetLog.Warn($"{Log}The pause menu's RESUME button or FRAME was not found; its multiplayer buttons are off.");
                return false;
            }
            try
            {
                _menu = menu;
                _frame = frame;
                _baseHeight = frame.sizeDelta.y;
                var list = resume.transform.parent;
                int at = resume.transform.GetSiblingIndex();
                _inviteButton = MenuKit.CloneButton(resume, list, "MP INVITE");
                _inviteButton.transform.SetSiblingIndex(at + 1);
                MenuKit.SetText(_inviteButton, "Invite Friends");
                MenuKit.SetColor(_inviteButton, InviteColor);
                _inviteButton.onClick.AddListener(() => _invite());

                _multiplayerButton = MenuKit.CloneButton(resume, list, "MP MULTIPLAYER");
                _multiplayerButton.transform.SetSiblingIndex(at + 2);
                MenuKit.SetText(_multiplayerButton, "Multiplayer");
                MenuKit.SetColor(_multiplayerButton, MultiplayerColor);
                _multiplayerButton.onClick.AddListener(() => _multiplayer());
                Show(false);
                NetLog.Info($"{Log}Multiplayer buttons added to the pause menu.");
                return true;
            }
            catch (Exception e)
            {
                NetLog.Error($"{Log}Adding the pause menu buttons failed: {e}");
                Forget();
                return false;
            }
        }

        /// <summary>Shows Invite Friends when it can be used; Multiplayer always.</summary>
        public void Show(bool invite)
        {
            if (_menu == null)
                return;
            int shown = invite ? 2 : 1;
            if (shown == _shown)
                return;
            _shown = shown;
            _inviteButton.gameObject.SetActive(invite);
            _frame.sizeDelta = new Vector2(_frame.sizeDelta.x, _baseHeight + RowHeight * shown);
            // The pause screen rebuilds its button list when it opens; while open, rescan now.
            MenuKit.RescanGamepadMenu(GamepadMenu);
        }

        public void Forget()
        {
            _menu = null;
            _frame = null;
            _inviteButton = null;
            _multiplayerButton = null;
            _shown = -1;
        }
    }
}
