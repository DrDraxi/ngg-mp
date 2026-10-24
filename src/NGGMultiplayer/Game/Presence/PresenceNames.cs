namespace NGGMultiplayer.Game.Presence
{
    /// <summary>Presence RPC names (docs/GAME_INTEGRATION.md 2.2). Every peer registers all of them.</summary>
    internal static class PresenceRpc
    {
        public const string Info = GameMessages.PresencePrefix + "info";
        public const string Shot = GameMessages.PresencePrefix + "shot";
        public const string Rest = GameMessages.PresencePrefix + "rest";
        public const string Holed = GameMessages.PresencePrefix + "holed";
        public const string Wind = GameMessages.PresencePrefix + "wind";
        public const string Round = GameMessages.PresencePrefix + "round";
        public const string Hole = GameMessages.PresencePrefix + "hole";
        public const string Card = GameMessages.PresencePrefix + "card";
    }

    /// <summary>Patcher feature keys of the Presence module; each one fails (and is disabled) on its own.</summary>
    internal static class PresenceFeature
    {
        public const string Pose = "pr.pose";
        public const string PoseFlags = "pr.pose.flags";
        public const string Ball = "pr.ball";
        public const string BallState = "pr.ball.state";
        public const string BallBody = "pr.ball.body";
        public const string Info = "pr.info";
        public const string Avatar = "pr.avatar";
        public const string Skin = "pr.skin";
        public const string Shot = "pr.shot";
        public const string Rest = "pr.rest";
        public const string Holed = "pr.holed";
        public const string Wind = "pr.wind";
        public const string Round = "pr.round";
        public const string RoundPars = "pr.round.pars";
        public const string Hud = "pr.hud";
        public const string Trail = "pr.trail";
        public const string LookVideo = "pr.lookvideo";
    }
}
