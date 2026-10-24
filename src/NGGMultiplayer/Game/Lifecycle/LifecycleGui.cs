using System.Globalization;
using NGGMultiplayer.Runtime;
using UnityEngine;

namespace NGGMultiplayer.Game.Lifecycle
{
    /// <summary>
    /// The Lifecycle module's IMGUI (docs/GAME_INTEGRATION.md 4.1, 4.3): the "Multiplayer" button in Menu2, the
    /// waiting panel a client sees there, and the multiplayer button in the pause menu (in a session it shows the
    /// player count; offline it opens the window to host or join from the running game). Texts are rebuilt a few
    /// times per second, not every OnGUI pass.
    /// </summary>
    internal sealed class LifecycleGui
    {
        private const double RefreshSeconds = 0.5;
        private const float Margin = 16f;
        private const float PanelWidth = 460f;

        private readonly LifecycleSaveModule _module;
        private GUIStyle _button;
        private GUIStyle _panel;
        private GUIStyle _label;
        private double _nextRefresh;
        private string _menuButton = "Multiplayer";
        private string _waitTitle = string.Empty;
        private string _waitLine = string.Empty;
        private string _pauseLine = string.Empty;

        public LifecycleGui(LifecycleSaveModule module)
        {
            _module = module;
        }

        public void Draw()
        {
            var ctx = _module.Context;
            // The native menus (Menus module) replace all of this; it is only the fallback when they could not be built.
            if (ctx == null || ctx.NativeMenusShown)
                return;
            bool menu = ctx.Scene == GameScene.Menu;
            // Not over the settings submenu (still paused, but its own screen, #58).
            bool pause = ctx.Scene == GameScene.Main && _module.Pause != null && _module.Pause.MenuOpen && !_module.Pause.SettingsOpen;
            bool waiting = menu && (ctx.IsClientConnection || (_module.Store != null && _module.Store.SessionSaveActive));
            if (!menu && !pause)
                return;
            EnsureStyles();
            if (ctx.Now >= _nextRefresh)
            {
                _nextRefresh = ctx.Now + RefreshSeconds;
                Refresh(ctx);
            }

            if (menu)
            {
                if (GUI.Button(new Rect(Margin, Screen.height - Margin - 44f, 320f, 44f), _menuButton, _button))
                    ctx.ShowLobbyWindow();
            }
            if (waiting)
                DrawWaitingPanel(ctx);
            if (pause)
            {
                if (GUI.Button(new Rect(Margin, Screen.height - Margin - 36f, 320f, 36f), _pauseLine, _button))
                    ctx.ShowLobbyWindow();
            }
        }

        private void DrawWaitingPanel(GameContext ctx)
        {
            var rect = new Rect((Screen.width - PanelWidth) * 0.5f, Screen.height * 0.5f - 80f, PanelWidth, 160f);
            GUI.Box(rect, GUIContent.none, _panel);
            GUI.Label(new Rect(rect.x + 16f, rect.y + 16f, rect.width - 32f, 30f), _waitTitle, _label);
            GUI.Label(new Rect(rect.x + 16f, rect.y + 50f, rect.width - 32f, 50f), _waitLine, _label);
            if (GUI.Button(new Rect(rect.x + (rect.width - 180f) * 0.5f, rect.y + rect.height - 52f, 180f, 36f), "Leave session", _button))
                ctx.Service?.Leave();
        }

        private void Refresh(GameContext ctx)
        {
            int count = ctx.Players.Count;
            string host = ctx.PlayerName(ctx.HostId);
            var state = ctx.Service?.State ?? MultiplayerState.Unavailable;
            if (ctx.IsHost)
                _menuButton = "Multiplayer - hosting " + Players(count);
            else if (ctx.IsClient)
                _menuButton = "Multiplayer - in " + host + "'s session";
            else if (state == MultiplayerState.Unavailable)
                _menuButton = "Multiplayer (unavailable)";
            else
                _menuButton = "Multiplayer";

            string key = ctx.LobbyWindowKey;
            if (ctx.InSession)
                _pauseLine = "Multiplayer: " + key + " - " + Players(count);
            else if (state == MultiplayerState.Unavailable)
                _pauseLine = "Multiplayer (unavailable)";
            else if (state == MultiplayerState.Offline)
                _pauseLine = "Multiplayer: " + key + " - host or join";
            else
                _pauseLine = "Multiplayer: " + key + " - connecting...";

            if (!ctx.IsReady)
            {
                _waitTitle = "Joining a session...";
                _waitLine = "Loading the host's save.";
                return;
            }
            _waitTitle = "In " + host + "'s session.";
            switch (_module.HostScene)
            {
                case GameScene.Main:
                    _waitLine = "Joining " + host + "'s game...";
                    break;
                case GameScene.Credits:
                    _waitLine = "The host is watching the credits.";
                    break;
                default:
                    _waitLine = "Waiting for the host to start a game.";
                    break;
            }
        }

        private static string Players(int count) =>
            count == 1 ? "1 player" : count.ToString(CultureInfo.InvariantCulture) + " players";

        private void EnsureStyles()
        {
            if (_button != null)
                return;
            _button = new GUIStyle(GUI.skin.button) { fontSize = 16 };
            _panel = new GUIStyle(GUI.skin.box);
            _label = new GUIStyle(GUI.skin.label)
            {
                fontSize = 18,
                wordWrap = true,
                alignment = TextAnchor.MiddleCenter
            };
        }
    }
}
