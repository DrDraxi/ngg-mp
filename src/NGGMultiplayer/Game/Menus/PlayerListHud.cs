using System;
using NGGMultiplayer.Core.Diagnostics;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NGGMultiplayer.Game.Menus
{
    /// <summary>
    /// The session's player list in the game's HUD style (<see cref="HudPanel"/>): the walking HUD's "You have $N"
    /// panel mirrored to the top right, one line per player. It shows with the walking HUD: golf mode (the HUD canvas goes away with the first-person character)
    /// hides it, so it is never over a shot. It lives on its own overlay canvas under the pause screen's backdrop
    /// (dimmed like the HUD) and fades out while the bank or assists panel slides into the top right and while the
    /// cutscene bars are in.
    /// </summary>
    internal sealed class PlayerListHud
    {
        private const string Log = "[Menus] ";
        private const float Edge = 20f;
        private const float FadePerSecond = 6f;
        /// <summary>Under the pause canvas (998) and the multiplayer screens (1000).</summary>
        private const int SortingOrder = 20;

        private GameObject _root;
        private RectTransform _panel;
        private CanvasGroup _group;
        private TextMeshProUGUI _label;
        private Canvas _hud;
        private RectTransform _bank;
        private RectTransform _assists;
        private Transform _cutscene;
        private string _text = string.Empty;

        public bool Built => _root != null;

        /// <summary>Builds the list from the scene's HUD. False while the HUD is not there (yet).</summary>
        public bool Build()
        {
            if (_root != null)
                return true;
            if (!HudPanel.FindTemplate(out var template, out var hud))
            {
                if (template == null && hud == null)
                    return false;
                NetLog.Warn($"{Log}The HUD's money panel was not found; the player list stays the plain one.");
                return false;
            }
            try
            {
                var built = HudPanel.Build(hud, template, "NGGMP PLAYER LIST", SortingOrder, new Vector2(1f, 1f),
                    new Vector2(-Edge, -Edge), TextAlignmentOptions.TopLeft);
                _root = built.Root;
                _panel = built.Panel;
                _group = built.Group;
                _label = built.Label;
                _hud = hud;
                _bank = MenuKit.FindDeep(hud.transform, "BANK_FRAME") as RectTransform;
                _assists = MenuKit.FindDeep(hud.transform, "ASSISTS") as RectTransform;
                _cutscene = MenuKit.FindDeep(hud.transform, "CUTSCENE");
                NetLog.Info($"{Log}Player list added in the HUD's style.");
                return true;
            }
            catch (Exception e)
            {
                NetLog.Error($"{Log}Building the player list failed: {e}");
                Destroy();
                return false;
            }
        }

        /// <summary>Every frame in Main: the lines (null: nothing to show) and whether the list should be up.</summary>
        public void Tick(bool wanted, string text)
        {
            if (_root == null)
                return;
            if (wanted && text != null && text != _text)
            {
                _text = text;
                _label.text = text;
                _label.ForceMeshUpdate();
                LayoutRebuilder.ForceRebuildLayoutImmediate(_panel);
            }
            // Golf mode switches the walking HUD off: the list goes with it, at once (not over the swing).
            bool walking = _hud != null && _hud.isActiveAndEnabled;
            if (!walking)
            {
                _group.alpha = 0f;
                return;
            }
            float target = wanted && !string.IsNullOrEmpty(_text) && !TopRightBusy() ? 1f : 0f;
            _group.alpha = Mathf.MoveTowards(_group.alpha, target, FadePerSecond * Time.unscaledDeltaTime);
        }

        /// <summary>The bank or assists panel is in the top right, or the cutscene bars cover the top.</summary>
        private bool TopRightBusy()
        {
            if (OnScreen(_bank) || OnScreen(_assists))
                return true;
            if (_cutscene != null && _cutscene.gameObject.activeInHierarchy && _cutscene.childCount > 0)
            {
                var bar = _cutscene.GetChild(0);
                if (bar.gameObject.activeInHierarchy && _cutscene.localScale.y * bar.localScale.y > 0.05f)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// The bank and assists panels hang from the top right corner by their right edge and park off-screen to the
        /// right (x 400, past their 331 width); they slide in with an InOutBack tween that overshoots both ways. Busy
        /// while any part of the panel is inside the screen.
        /// </summary>
        private static bool OnScreen(RectTransform panel)
        {
            if (panel == null || !panel.gameObject.activeInHierarchy)
                return false;
            float rightEdge = panel.anchoredPosition.x + (1f - panel.pivot.x) * panel.rect.width;
            return rightEdge - panel.rect.width < 0f;
        }

        public void Destroy()
        {
            if (_root != null)
                UnityEngine.Object.Destroy(_root);
            Forget();
        }

        /// <summary>Called when the scene goes away (the list went with it).</summary>
        public void Forget()
        {
            _root = null;
            _panel = null;
            _group = null;
            _label = null;
            _hud = null;
            _bank = null;
            _assists = null;
            _cutscene = null;
            _text = string.Empty;
        }
    }
}
