using System;
using NGGMultiplayer.Core.Diagnostics;
using TMPro;
using UnityEngine;

namespace NGGMultiplayer.Game.Menus
{
    /// <summary>
    /// The mod's short notices (<see cref="GameContext.Notify"/>: another player holed out, the host refused
    /// something, session status) in the game's HUD style (<see cref="HudPanel"/>), centred below the top band (the
    /// objective line and the money panel run across the top and can be long), newest last.
    /// Like the player list it shows with the walking HUD only, so never over a shot.
    /// </summary>
    internal sealed class NoticeHud
    {
        private const string Log = "[Menus] ";
        /// <summary>From the top, in the HUD's reference pixels (1080 high): clear of the objective and money lines.</summary>
        private const float Top = 170f;
        private const float FadePerSecond = 6f;
        /// <summary>With the player list: under the pause canvas (998) and the multiplayer screens (1000).</summary>
        private const int SortingOrder = 21;

        private HudPanel _panel;
        private Canvas _hud;
        private string _text = string.Empty;

        public bool Built => _panel != null && _panel.Root != null;

        /// <summary>Builds the panel from the scene's HUD. False while the HUD is not there (yet).</summary>
        public bool Build()
        {
            if (Built)
                return true;
            if (!HudPanel.FindTemplate(out var template, out var hud))
                return false;
            try
            {
                _panel = HudPanel.Build(hud, template, "NGGMP NOTICES", SortingOrder, new Vector2(0.5f, 1f),
                    new Vector2(0f, -Top), TextAlignmentOptions.Top);
                _hud = hud;
                NetLog.Info($"{Log}Notices added in the HUD's style.");
                return true;
            }
            catch (Exception e)
            {
                NetLog.Error($"{Log}Building the notices failed: {e}");
                Destroy();
                return false;
            }
        }

        /// <summary>Every frame in Main: the current notices, one per line (null or empty: none).</summary>
        public void Tick(string text)
        {
            if (!Built)
                return;
            if (!string.IsNullOrEmpty(text) && text != _text)
            {
                _text = text;
                _panel.SetText(text);
            }
            // Golf mode switches the walking HUD off: the notices go with it, at once (not over the swing).
            bool walking = _hud != null && _hud.isActiveAndEnabled;
            if (!walking)
            {
                _panel.Group.alpha = 0f;
                return;
            }
            float target = string.IsNullOrEmpty(text) ? 0f : 1f;
            _panel.Group.alpha = Mathf.MoveTowards(_panel.Group.alpha, target, FadePerSecond * Time.unscaledDeltaTime);
        }

        public void Destroy()
        {
            if (_panel != null && _panel.Root != null)
                UnityEngine.Object.Destroy(_panel.Root);
            Forget();
        }

        /// <summary>Called when the scene goes away (the panel went with it).</summary>
        public void Forget()
        {
            _panel = null;
            _hud = null;
            _text = string.Empty;
        }
    }
}
