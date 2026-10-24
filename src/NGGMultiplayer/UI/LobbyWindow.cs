using System;
using System.Globalization;
using System.Text;
using BepInEx.Configuration;
using NGGMultiplayer.Runtime;
using UnityEngine;

namespace NGGMultiplayer.UI
{
    /// <summary>
    /// IMGUI window for hosting, inviting, joining and leaving. Text that depends on the session (roster, pings, the
    /// game's lines from <see cref="ILobbyGameInfo"/>) is rebuilt at most a few times per second rather than every
    /// OnGUI pass. It opens with the toggle key, from the "Multiplayer" buttons in the main and pause menus, or
    /// from code (<see cref="Visible"/>).
    /// </summary>
    public sealed class LobbyWindow : MonoBehaviour
    {
        private const int WindowId = 0x4E474D50; // "NGMP"
        private const float Width = 380f;
        private const double RosterRefreshSeconds = 0.5;

        private readonly StringBuilder _builder = new StringBuilder(256);
        private IMultiplayerService _service;
        private ILobbyGameInfo _gameInfo;
        private ConfigEntry<KeyboardShortcut> _toggle;
        private Rect _rect = new Rect(20f, 20f, Width, 10f);
        private bool _visible;
        private string _joinId = string.Empty;
        private string _rosterText = string.Empty;
        private string _title = string.Empty;
        private string _statusLine = string.Empty;
        private string _lobbyLine = string.Empty;
        private string _lobbyIdText = string.Empty;
        private string _gameStatus = string.Empty;
        private string _gameWarning = string.Empty;
        private string _syncText = string.Empty;
        private float _syncProgress;
        private MultiplayerState _shownState = (MultiplayerState)(-1);
        private ulong _shownLobby = ulong.MaxValue;
        private double _nextRosterAt;
        private GUI.WindowFunction _draw;

        /// <summary>Shown or hidden; the toggle key flips it.</summary>
        public bool Visible
        {
            get => _visible;
            set => _visible = value;
        }

        public void Initialize(IMultiplayerService service, ConfigEntry<KeyboardShortcut> toggle)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _toggle = toggle;
            _draw = DrawWindow;
            _title = "NGG Multiplayer " + Core.Protocol.ProtocolInfo.ModVersion;
        }

        /// <summary>The game's lines (null: none). Set once the game integration is attached.</summary>
        public void SetGameInfo(ILobbyGameInfo info)
        {
            _gameInfo = info;
            _nextRosterAt = 0;
        }

        private void Update()
        {
            // HarnessInput: a test-harness key press (docs/TESTING.md `key F7`); always false outside the harness.
            if (_toggle != null && (_toggle.Value.IsDown() || Testing.HarnessInput.ShortcutDown(_toggle.Value)))
                _visible = !_visible;
            if (!_visible || _service == null)
                return;
            var state = _service.State;
            if (state != _shownState)
            {
                _shownState = state;
                _statusLine = "Status: " + Describe(state);
            }
            ulong lobby = _service.LobbyId;
            if (lobby != _shownLobby)
            {
                _shownLobby = lobby;
                _lobbyIdText = lobby.ToString(CultureInfo.InvariantCulture);
                _lobbyLine = "Lobby " + _lobbyIdText;
            }
        }

        private void OnGUI()
        {
            if (!_visible || _service == null)
                return;
            _rect = GUILayout.Window(WindowId, _rect, _draw, _title, GUILayout.Width(Width));
        }

        private void DrawWindow(int id)
        {
            var state = _service.State;
            GUILayout.Label(_statusLine);
            if (!string.IsNullOrEmpty(_service.StatusMessage))
                GUILayout.Label(_service.StatusMessage);
            RefreshTexts();
            if (!string.IsNullOrEmpty(_gameWarning))
                GUILayout.Label(_gameWarning);

            switch (state)
            {
                case MultiplayerState.Unavailable:
                    GUILayout.Label(_service.UnavailableReason);
                    break;
                case MultiplayerState.Offline:
                    DrawOffline();
                    break;
                case MultiplayerState.InSession:
                    DrawSession();
                    break;
                case MultiplayerState.Syncing:
                    DrawSyncing();
                    break;
                default:
                    if (GUILayout.Button("Cancel"))
                        _service.Leave();
                    break;
            }

            if (GUILayout.Button("Close"))
                _visible = false;
            GUI.DragWindow();
        }

        private void DrawOffline()
        {
            if (GUILayout.Button("Host game"))
                _service.Host();

            GUILayout.Label("Your save becomes the session's save. Players who join play on it; their own saves are untouched.");

            GUILayout.Space(6f);
            if (_service is DirectMultiplayerService direct)
            {
                // Direct transport ([Network] Transport = Direct): no lobby ids; HostAddress / DirectPort in the config.
                if (GUILayout.Button("Join " + direct.Endpoint))
                    direct.Join(0);
                return;
            }
            GUILayout.Label("Join by lobby id (or accept a Steam invite):");
            GUILayout.BeginHorizontal();
            _joinId = GUILayout.TextField(_joinId ?? string.Empty, 24);
            if (GUILayout.Button("Join", GUILayout.Width(60f)))
            {
                if (ulong.TryParse(_joinId.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out ulong lobby))
                    _service.Join(lobby);
            }
            GUILayout.EndHorizontal();
        }

        private void DrawSyncing()
        {
            GUILayout.Label(_syncText);
            var bar = GUILayoutUtility.GetRect(Width - 24f, 14f);
            GUI.Box(bar, GUIContent.none);
            if (_syncProgress > 0f)
                GUI.Box(new Rect(bar.x, bar.y, bar.width * Mathf.Clamp01(_syncProgress), bar.height), GUIContent.none);
            if (!string.IsNullOrEmpty(_gameStatus))
                GUILayout.Label(_gameStatus);
            if (GUILayout.Button("Cancel"))
                _service.Leave();
        }

        private void DrawSession()
        {
            GUILayout.Label(_service.IsHost ? "You are hosting." : "Connected to host.");
            if (!string.IsNullOrEmpty(_gameStatus))
                GUILayout.Label(_gameStatus);
            if (_shownLobby != 0)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(_lobbyLine);
                if (GUILayout.Button("Copy id", GUILayout.Width(70f)))
                    GUIUtility.systemCopyBuffer = _lobbyIdText;
                GUILayout.EndHorizontal();
            }
            if (!(_service is DirectMultiplayerService) && GUILayout.Button("Invite friends"))
                _service.OpenInviteDialog();

            GUILayout.Space(6f);
            GUILayout.Label(_rosterText);
            GUILayout.Space(6f);
            if (GUILayout.Button("Leave"))
                _service.Leave();
        }

        private void RefreshTexts()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (now < _nextRosterAt)
                return;
            _nextRosterAt = now + RosterRefreshSeconds;
            var info = _gameInfo;
            _gameStatus = info?.SessionStatus ?? string.Empty;
            _gameWarning = info?.Warning ?? string.Empty;
            var saveClient = _service.Net?.SaveClient;
            _syncProgress = saveClient != null ? saveClient.SnapshotProgress : 0f;
            _syncText = _syncProgress > 0f
                ? "Loading the host's save: " + Mathf.RoundToInt(_syncProgress * 100f).ToString(CultureInfo.InvariantCulture) + "%"
                : "Waiting for the host's save...";
            RefreshRoster(info);
        }

        private void RefreshRoster(ILobbyGameInfo info)
        {
            var players = _service.Players;
            _builder.Clear();
            _builder.Append("Players (").Append(players.Count).Append("):");
            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                _builder.Append('\n').Append(p.DisplayName);
                if (p.IsHost)
                    _builder.Append(" [host]");
                if (p.IsLocal)
                    _builder.Append(" (you)");
                else
                    _builder.Append("  ").Append(p.PingMs).Append(" ms");
                string where = info?.DescribePlayer(p);
                if (!string.IsNullOrEmpty(where))
                    _builder.Append("\n    ").Append(where);
            }
            _rosterText = _builder.ToString();
        }

        private static string Describe(MultiplayerState state)
        {
            switch (state)
            {
                case MultiplayerState.Unavailable: return "Unavailable";
                case MultiplayerState.Offline: return "Offline";
                case MultiplayerState.CreatingLobby: return "Creating lobby...";
                case MultiplayerState.JoiningLobby: return "Joining lobby...";
                case MultiplayerState.Connecting: return "Connecting...";
                case MultiplayerState.Syncing: return "Loading host's progress...";
                case MultiplayerState.InSession: return "In session";
                default: return state.ToString();
            }
        }
    }
}
