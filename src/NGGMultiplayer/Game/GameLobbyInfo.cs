using System;
using NGGMultiplayer.Core.Session;
using NGGMultiplayer.Game.Lifecycle;
using NGGMultiplayer.Game.Presence;
using NGGMultiplayer.Runtime;

namespace NGGMultiplayer.Game
{
    /// <summary>
    /// The lobby window's view of the game (integrator glue): the host's scene and mode from Lifecycle, each player's
    /// whereabouts from Presence, and what keeps this game from joining. Every member swallows its own errors: the
    /// window must keep working when a module is off.
    /// </summary>
    internal sealed class GameLobbyInfo : ILobbyGameInfo
    {
        private readonly GameContext _ctx;

        public GameLobbyInfo(GameContext ctx)
        {
            _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        }

        public string SessionStatus
        {
            get
            {
                try
                {
                    return BuildStatus();
                }
                catch (Exception e)
                {
                    _ctx.WarnOnce("lobby.status", $"[Game] Building the lobby status failed: {e.Message}");
                    return string.Empty;
                }
            }
        }

        public string Warning
        {
            get
            {
                try
                {
                    return BuildWarning();
                }
                catch (Exception e)
                {
                    _ctx.WarnOnce("lobby.warning", $"[Game] Building the lobby warning failed: {e.Message}");
                    return string.Empty;
                }
            }
        }

        public string DescribePlayer(PlayerInfo player)
        {
            try
            {
                return _ctx.GetModule<PresenceModule>()?.DescribePlayer(player) ?? string.Empty;
            }
            catch (Exception e)
            {
                _ctx.WarnOnce("lobby.player", $"[Game] Describing a player failed: {e.Message}");
                return string.Empty;
            }
        }

        private string BuildStatus()
        {
            var lifecycle = _ctx.GetModule<LifecycleSaveModule>();
            if (lifecycle == null || !_ctx.InSession)
                return string.Empty;
            if (_ctx.IsHost)
            {
                string mode = ModeName(lifecycle.CurrentModeByte());
                switch (_ctx.Scene)
                {
                    case GameScene.Main:
                        return "You are in the world (" + mode + "). Your save is the session's save.";
                    case GameScene.Credits:
                        return "You are watching the credits. Players wait in the menu.";
                    default:
                        return "You are in the menu. Start a game and everyone follows (" + mode + ").";
                }
            }
            if (!_ctx.IsReady)
                return "Loading the host's save. Your own save is backed up and stays untouched.";
            var bundle = lifecycle.Store?.Bundle;
            string host = _ctx.PlayerName(_ctx.HostId);
            string where;
            switch (lifecycle.HostScene)
            {
                case GameScene.Main:
                    where = host + " is in the world";
                    break;
                case GameScene.Credits:
                    where = host + " is watching the credits";
                    break;
                default:
                    where = host + " is in the menu";
                    break;
            }
            if (bundle != null)
                where += " (" + ModeName(bundle.Mode) + ")";
            return where + ". Playing on the host's save; yours is restored when you leave.";
        }

        private string BuildWarning()
        {
            if (_ctx.Save.SuppressDiskWrites && !_ctx.InSession)
                return "Your own save could not be verified after the last session; saving is off. Restart the game.";
            var lifecycle = _ctx.GetModule<LifecycleSaveModule>();
            if (lifecycle != null && !_ctx.InSession)
            {
                var missing = lifecycle.MissingSafetyFeatures();
                if (missing.Count > 0)
                    return "Save protection is incomplete (" + string.Join(", ", missing.ToArray()) +
                        "): you can host, but not join. This game version may not match the mod.";
            }
            int disabled = _ctx.Patcher.DisabledFeatures.Count;
            if (disabled > 0)
                return disabled + " game feature(s) are off (see the BepInEx log); some things may not sync.";
            return string.Empty;
        }

        private static string ModeName(byte mode) => mode == SessionSaveBundle.ModeJustGolf ? "Just Golf" : "Story";
    }
}
