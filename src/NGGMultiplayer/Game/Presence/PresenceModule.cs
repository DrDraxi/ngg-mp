using System;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Session;
using UnityEngine;

namespace NGGMultiplayer.Game.Presence
{
    /// <summary>
    /// Other players in the world and everything transient about playing together: poses and avatars, ghost balls, shot
    /// events, the player list HUD, host wind, the shared LMUGC scorecard. Provides the IPresenceProvider. Spec:
    /// docs/GAME_INTEGRATION.md section 7; pausing without freezing time (7.6) is Lifecycle's PauseTime, the one pause
    /// controller of the plugin (the HUD reads <see cref="GameContext.PauseMenuOpen"/>).
    /// <para>Nothing here is persistent and nothing changes the save. Offline, every patch returns at once (the ball
    /// state hooks only set a mod flag).</para>
    /// </summary>
    public sealed class PresenceModule : IGameModule, IPresenceProvider
    {
        private const string Log = "[Presence] ";

        private GameContext _context;
        private PresenceAccess _access;
        private RemoteVisuals _visuals;
        private Action<PlayerInfo> _onPlayerLeft;
        private Action<PeerId> _onPeerReady;
        private Action<GameScene> _onSceneLoaded;
        private Action _onWorldLoaded;
        private Action _onGui;
        private Action _onRoundChanged;

        public string Name => "Presence";

        /// <summary>The installed module (for the static patch methods); null outside Install..Uninstall.</summary>
        internal static PresenceModule Instance { get; private set; }

        internal LocalPresence Local { get; private set; }

        internal ShotEvents Shots { get; private set; }

        internal WindSync Wind { get; private set; }

        internal RoundSync Rounds { get; private set; }

        internal PresenceRoster Roster { get; } = new PresenceRoster();

        internal PresenceHud Hud { get; private set; }

        internal LookVideo LookVideo { get; private set; }

        public void Install(GameContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _access = new PresenceAccess();
            _access.Resolve(context);
            _visuals = new RemoteVisuals(context, _access);
            Local = new LocalPresence(context);
            Shots = new ShotEvents(context, _access, Roster);
            Wind = new WindSync(context, _access);
            Rounds = new RoundSync(context, _access);
            Hud = new PresenceHud(context, this);
            LookVideo = new LookVideo(context);
            Local.RoundActivity = Rounds.Activity;
            Shots.LmugcHole = Rounds.CurrentHole;
            Instance = this;

            context.RegisterRpc(PresenceRpc.Info, OnInfo, (s, t, p, r) => PresenceWire.IsValid(r, x => InfoMessage.Read(x)));
            Install("ball state", Local.Install);
            Install("shot events", Shots.Install);
            Install("wind", Wind.Install);
            Install("rounds", Rounds.Install);

            _onPlayerLeft = OnPlayerLeft;
            _onPeerReady = OnPeerReady;
            _onSceneLoaded = OnSceneLoaded;
            _onWorldLoaded = OnWorldLoaded;
            _onGui = OnGui;
            _onRoundChanged = OnRoundChanged;
            context.PlayerLeft += _onPlayerLeft;
            context.PeerReady += _onPeerReady;
            context.SceneLoaded += _onSceneLoaded;
            context.WorldLoaded += _onWorldLoaded;
            context.Gui += _onGui;
            Rounds.Changed += _onRoundChanged;
            context.SetPresenceProvider(this);
        }

        public void Uninstall()
        {
            var context = _context;
            if (context != null)
            {
                if (ReferenceEquals(context.Presence, this))
                    context.SetPresenceProvider(null);
                context.PlayerLeft -= _onPlayerLeft;
                context.PeerReady -= _onPeerReady;
                context.SceneLoaded -= _onSceneLoaded;
                context.WorldLoaded -= _onWorldLoaded;
                context.Gui -= _onGui;
            }
            if (Rounds != null)
                Rounds.Changed -= _onRoundChanged;
            if (ReferenceEquals(Instance, this))
                Instance = null;
        }

        public void Tick(float deltaTime)
        {
            if (_context == null)
                return;
            Local.Tick();
            Hud.Tick();
            LookVideo.Tick();
        }

        public void OnSessionReady(bool isHost)
        {
            Roster.Clear();
            Shots.Reset();
            Wind.Reset();
            Rounds.Reset();
            Hud.Reset();
            Local.OnSessionChanged();
            Rounds.ReadPars();
        }

        public void OnSessionEnded()
        {
            Roster.Clear();
            Shots.Reset();
            Wind.Reset();
            Rounds.Reset();
            Hud.Reset();
            Local.OnSessionChanged();
            _visuals.Clear();
        }

        // ---- IPresenceProvider -----------------------------------------------------------------------

        public bool TryGetLocalPlayerPose(out GamePose pose) => Local.TryGetPlayerPose(out pose);

        public bool TryGetLocalBallPose(out GamePose pose) => Local.TryGetBallPose(out pose);

        public GameObject CreateRemotePlayerVisual(PlayerInfo player)
        {
            if (!_context.Patcher.IsEnabled(PresenceFeature.Avatar))
                return null;
            try
            {
                return _visuals.CreatePlayer(player);
            }
            catch (Exception e)
            {
                // Null makes the remote view use its own placeholder.
                _context.Patcher.ReportFailure(PresenceFeature.Avatar, e);
                return null;
            }
        }

        public GameObject CreateRemoteBallVisual(PlayerInfo player)
        {
            if (!_context.Patcher.IsEnabled(PresenceFeature.Avatar))
                return null;
            try
            {
                return _visuals.CreateBall(player);
            }
            catch (Exception e)
            {
                _context.Patcher.ReportFailure(PresenceFeature.Avatar, e);
                return null;
            }
        }

        /// <summary>
        /// Plain-text "where is this player and what are they doing" for the lobby window ("Walking, Bounty BO_Roof",
        /// "In menu", "joining"). Empty when not in a session.
        /// </summary>
        internal string DescribePlayer(PlayerInfo player)
        {
            if (_context == null || player == null || !_context.InSession)
                return string.Empty;
            InfoMessage info;
            uint stroke;
            if (player.IsLocal)
            {
                info = Local.CurrentInfo();
                stroke = Shots.LocalLastStroke;
            }
            else if (Roster.TryGet(player.Id, out var presence) && presence.HasInfo)
            {
                info = presence.Info;
                stroke = presence.LastStroke;
            }
            else
            {
                return "joining";
            }
            string scene = PresenceText.SceneLabel(info.Scene);
            if (scene != null)
                return scene;
            string text = PresenceText.ControlMode(info.ControlMode);
            if (!string.IsNullOrEmpty(info.Activity))
                text += ", " + info.Activity;
            if (stroke > 0)
                text += ", stroke " + stroke.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return text;
        }

        // ---- Events ----------------------------------------------------------------------------------

        private void OnInfo(PeerId sender, NetReader reader)
        {
            var info = InfoMessage.Read(reader);
            if (sender == _context.LocalId)
                return;
            if (!Roster.ApplyInfo(sender, info, _context.Now))
                return;
            if (_context.Patcher.IsEnabled(PresenceFeature.Skin))
                _visuals.SetSkin(sender, info.SelectedBall);
            _visuals.SetLook(sender, info.Look);
        }

        private void OnPlayerLeft(PlayerInfo player)
        {
            if (player == null)
                return;
            Roster.Remove(player.Id);
            Rounds.OnPlayerLeft(player.Id);
            _visuals.ForgetSkin(player.Id);
            _visuals.ForgetLook(player.Id);
        }

        private void OnPeerReady(PeerId peer)
        {
            // The newcomer hears the host's info now and everyone else's within a few seconds.
            Local.InfoSoon();
            Rounds.SendCard(peer);
        }

        private void OnSceneLoaded(GameScene scene)
        {
            Local.OnSceneLoaded();
            Wind.OnSceneLoaded();
            Rounds.OnSceneLoaded();
        }

        private void OnWorldLoaded()
        {
            _visuals.RefreshMaterials();
            LookVideo.OnWorldLoaded();
            Rounds.ReadPars();
        }

        private void OnGui() => Hud.Draw();

        private void OnRoundChanged()
        {
            if (_context.Scene == GameScene.Main)
                Hud.OpenCard();
        }

        private void Install(string what, Action install)
        {
            try
            {
                install();
            }
            catch (Exception e)
            {
                // A broken part leaves the rest of Presence working.
                _context.Error($"{Log}Installing {what} failed: {e}");
            }
        }
    }
}
