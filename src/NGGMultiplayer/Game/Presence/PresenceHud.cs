using System;
using System.Globalization;
using System.Text;
using BepInEx;
using NGGMultiplayer.Core.Session;
using UnityEngine;

namespace NGGMultiplayer.Game.Presence
{
    /// <summary>
    /// IMGUI overlays in a session (spec 7.4, 7.7): the player list (top right) and the shared scorecard (centre,
    /// while the scorecard key is held and for a few seconds after any round event). Only in a loaded "Main"; hidden while the
    /// local pause menu is open.
    /// </summary>
    internal sealed class PresenceHud
    {
        private const float CardOpenSeconds = 6f;
        private const float ListWidth = 380f;
        private const float RowHeight = 20f;
        private const float Margin = 10f;
        private const float NameColumn = 150f;
        private const float HoleColumn = 30f;
        private const float TotalColumn = 44f;
        private const float ToParColumn = 40f;

        private readonly GameContext _context;
        private readonly PresenceModule _module;
        private readonly StringBuilder _text = new StringBuilder(128);
        private double _cardUntil = double.NegativeInfinity;
        private bool _keyHeld;
        private bool _inputBroken;
        private KeyCode _checkedKey = (KeyCode)(-1);
        private InfoMessage _localInfo;
        private bool _visible;
        private GUIStyle _label;
        private GUIStyle _center;
        private GUIStyle _bold;

        public PresenceHud(GameContext context, PresenceModule module)
        {
            _context = context;
            _module = module;
        }

        public void Tick()
        {
            _visible = _context.InSession && _context.Scene == GameScene.Main && _context.WorldReady
                && _context.Patcher.IsEnabled(PresenceFeature.Hud) && !_context.PauseMenuOpen;
            if (!_visible)
                return;
            var key = _context.ScorecardKey;
            if (key != _checkedKey)
                CheckKeyBinding(key);
            _keyHeld = false;
            if (!_inputBroken && key != KeyCode.None)
            {
                try
                {
                    // HarnessInput: a test-harness `key <name>`; always false outside the harness.
                    _keyHeld = UnityInput.Current.GetKey(key) || Testing.HarnessInput.GetKey(key);
                }
                catch (Exception e)
                {
                    _inputBroken = true;
                    _context.Warn($"[Presence] Reading the scorecard key failed ({e.Message}); the scorecard only opens after round events.");
                }
            }
            try
            {
                _localInfo = _module.Local.CurrentInfo();
            }
            catch (Exception e)
            {
                _context.Patcher.ReportFailure(PresenceFeature.Hud, e);
            }
        }

        /// <summary>
        /// Once per configured key: whether the game's InputSystem asset binds it (answers #67: the game binds Tab, so
        /// the default is a free key; the bindings are asset data, so they are read at runtime). The key still opens the
        /// scorecard either way; this only says so.
        /// </summary>
        private void CheckKeyBinding(KeyCode key)
        {
            _checkedKey = key;
            if (key == KeyCode.None)
            {
                _context.Info("[Presence] No scorecard key ([General] ScorecardKey = None); the scorecard opens after round events only.");
                return;
            }
            try
            {
                var bindings = PresenceAccess.KeyBindings(key);
                if (bindings == null)
                    _context.Info($"[Presence] Could not read the game's input bindings; whether the game uses {key} is unknown.");
                else if (bindings.Count == 0)
                    _context.Info($"[Presence] The game binds nothing to {key}; it is free for the scorecard.");
                else
                    _context.Warn($"[Presence] The game binds {key} ({string.Join(", ", bindings)}); holding it for the scorecard " +
                        "also triggers that. Pick another [General] ScorecardKey.");
            }
            catch (Exception e)
            {
                _context.Info($"[Presence] Reading the game's input bindings failed ({e.Message}); whether the game uses {key} is unknown.");
            }
        }

        /// <summary>A round or hole event: show the scorecard for a while.</summary>
        public void OpenCard() => _cardUntil = _context.Now + CardOpenSeconds;

        public void Reset() => _cardUntil = double.NegativeInfinity;

        public void Draw()
        {
            if (!_visible)
                return;
            try
            {
                EnsureStyles();
                // The Menus module's list in the game's HUD style replaces this one when it could be built.
                if (!_context.NativePlayerListShown)
                    DrawPlayers();
                var card = _module.Rounds.Card;
                if (card.Count > 0 && (_keyHeld || _context.Now < _cardUntil))
                    DrawCard(card);
            }
            catch (Exception e)
            {
                _context.Patcher.ReportFailure(PresenceFeature.Hud, e);
            }
        }

        private void EnsureStyles()
        {
            if (_label != null)
                return;
            _label = new GUIStyle(GUI.skin.label) { fontSize = 13, richText = true, wordWrap = false, clipping = TextClipping.Clip };
            _center = new GUIStyle(_label) { alignment = TextAnchor.MiddleCenter };
            _bold = new GUIStyle(_label) { fontStyle = FontStyle.Bold };
        }

        private void DrawPlayers()
        {
            var players = _context.Players;
            if (players == null || players.Count == 0)
                return;
            float height = RowHeight * players.Count + 8f;
            var area = new Rect(Screen.width - ListWidth - Margin, Margin, ListWidth, height);
            GUI.Box(area, GUIContent.none);
            float y = area.y + 4f;
            foreach (var player in players)
            {
                GUI.Label(new Rect(area.x + 6f, y, ListWidth - 12f, RowHeight), PlayerRow(player), _label);
                y += RowHeight;
            }
        }

        private string PlayerRow(PlayerInfo player)
        {
            _text.Length = 0;
            if (player.IsHost)
                _text.Append("<b>*</b> ");
            _text.Append(Escape(player.DisplayName));
            if (player.IsHost)
                _text.Append(" (host)");
            if (player.IsLocal)
                _text.Append(" (you)");

            InfoMessage info;
            uint stroke;
            bool known;
            if (player.IsLocal)
            {
                info = _localInfo;
                stroke = _module.Shots.LocalLastStroke;
                known = true;
            }
            else if (_module.Roster.TryGet(player.Id, out var presence))
            {
                info = presence.Info;
                stroke = presence.LastStroke;
                known = presence.HasInfo;
            }
            else
            {
                info = default;
                stroke = 0;
                known = false;
            }

            _text.Append("  <color=#bbbbbb>");
            if (!known)
            {
                _text.Append("joining");
            }
            else
            {
                string scene = PresenceText.SceneLabel(info.Scene);
                _text.Append(scene ?? PresenceText.ControlMode(info.ControlMode));
                if (scene == null && !string.IsNullOrEmpty(info.Activity))
                    _text.Append(", ").Append(Escape(info.Activity));
                if (scene == null && stroke > 0)
                    _text.Append(", stroke ").Append(stroke.ToString(CultureInfo.InvariantCulture));
            }
            _text.Append("</color>");
            if (!player.IsLocal)
                _text.Append("  ").Append(player.PingMs.ToString(CultureInfo.InvariantCulture)).Append(" ms");
            return _text.ToString();
        }

        private void DrawCard(Scorecard card)
        {
            int rows = card.Count + 2;
            float width = NameColumn + HoleColumn * PresenceWire.Holes + TotalColumn + ToParColumn + 16f;
            float height = RowHeight * rows + 30f;
            var area = new Rect((Screen.width - width) * 0.5f, Screen.height * 0.22f, width, height);
            // Twice: the default skin's box is too transparent to read a grid of numbers over the course.
            GUI.Box(area, GUIContent.none);
            GUI.Box(area, GUIContent.none);
            float x0 = area.x + 8f;
            float y = area.y + 6f;
            GUI.Label(new Rect(x0, y, width - 16f, RowHeight), "Scorecard  <color=#bbbbbb>(hold " + _context.ScorecardKey + ")</color>", _bold);
            y += RowHeight + 4f;

            var pars = card.Pars;
            Cell(x0, y, NameColumn, "Hole", _bold);
            float x = x0 + NameColumn;
            for (int h = 0; h < PresenceWire.Holes; h++, x += HoleColumn)
                Cell(x, y, HoleColumn, (h + 1).ToString(CultureInfo.InvariantCulture), _center);
            Cell(x, y, TotalColumn, "Tot", _center);
            Cell(x + TotalColumn, y, ToParColumn, "+/-", _center);
            y += RowHeight;

            Cell(x0, y, NameColumn, "<color=#bbbbbb>Par</color>", _label);
            x = x0 + NameColumn;
            int parTotal = 0;
            bool allPars = true;
            for (int h = 0; h < PresenceWire.Holes; h++, x += HoleColumn)
            {
                int par = pars[h];
                parTotal += par;
                allPars &= par > 0;
                Cell(x, y, HoleColumn, par > 0 ? "<color=#bbbbbb>" + par.ToString(CultureInfo.InvariantCulture) + "</color>" : "-", _center);
            }
            Cell(x, y, TotalColumn, allPars ? "<color=#bbbbbb>" + parTotal.ToString(CultureInfo.InvariantCulture) + "</color>" : "", _center);
            y += RowHeight;

            foreach (var record in card.Rows)
            {
                string name = Escape(_context.PlayerName(record.Player));
                if (record.Left)
                    name += " <color=#999999>(left)</color>";
                else if (record.State == RoundEvent.Finished)
                    name += " <color=#88dd88>done</color>";
                else if (record.State == RoundEvent.Aborted)
                    name += " <color=#999999>quit</color>";
                Cell(x0, y, NameColumn, name, _label);
                x = x0 + NameColumn;
                for (int h = 0; h < PresenceWire.Holes; h++, x += HoleColumn)
                    Cell(x, y, HoleColumn, StrokeCell(record.Strokes[h], pars[h]), _center);
                Cell(x, y, TotalColumn, record.HolesPlayed > 0 ? record.Total.ToString(CultureInfo.InvariantCulture) : "-", _center);
                int? par = card.ParForPlayed(record);
                Cell(x + TotalColumn, y, ToParColumn,
                    record.HolesPlayed > 0 && par.HasValue ? PresenceText.ToPar(record.PlayedTotal, par.Value) : "", _center);
                y += RowHeight;
            }
        }

        private static string StrokeCell(int strokes, int par)
        {
            if (strokes <= 0)
                return "-";
            string text = strokes.ToString(CultureInfo.InvariantCulture);
            if (par <= 0 || strokes == par)
                return text;
            return strokes < par ? "<color=#ff8866>" + text + "</color>" : "<color=#88aaff>" + text + "</color>";
        }

        private void Cell(float x, float y, float width, string text, GUIStyle style) =>
            GUI.Label(new Rect(x, y, width, RowHeight), text, style);

        // Steam names may contain rich-text tags; IMGUI would interpret them.
        private static string Escape(string text) =>
            string.IsNullOrEmpty(text) ? string.Empty : text.Replace("<", "<​");
    }
}
