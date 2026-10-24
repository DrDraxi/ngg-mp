namespace NGGMultiplayer.Game.Lifecycle
{
    internal enum FollowAction
    {
        /// <summary>The client already shows what the host's bundle asks for.</summary>
        Stay,

        /// <summary>The client's scene is loading, or it is in INTRO: decide again once it settles.</summary>
        Wait,

        /// <summary>Load <see cref="FollowDecision.Target"/>.</summary>
        Load
    }

    internal readonly struct FollowDecision
    {
        public FollowDecision(FollowAction action, GameScene target)
        {
            Action = action;
            Target = target;
        }

        public FollowAction Action { get; }

        public GameScene Target { get; }

        public override string ToString() => Action == FollowAction.Load ? "Load " + Target : Action.ToString();
    }

    /// <summary>
    /// Which scene a client shows for the host's session save (docs/GAME_INTEGRATION.md 4.3, 4.4). Pure logic.
    /// </summary>
    internal static class FollowRules
    {
        /// <summary>The scene a client should be in while the host is in <paramref name="hostScene"/>.</summary>
        public static GameScene Target(GameScene hostScene, bool hostHasRun, GameScene clientScene)
        {
            switch (hostScene)
            {
                case GameScene.Main:
                    return hostHasRun ? GameScene.Main : GameScene.Menu;
                case GameScene.Credits:
                    // Only a client that played along watches the ending; anyone else waits in the menu.
                    return clientScene == GameScene.Main || clientScene == GameScene.Credits ? GameScene.Credits : GameScene.Menu;
                default:
                    return GameScene.Menu;
            }
        }

        /// <param name="clientSettled">The client's current scene finished loading (Main: world ready).</param>
        /// <param name="worldMatchesBundle">The client's loaded "Main" was injected from this bundle's epoch and mode.</param>
        public static FollowDecision Decide(GameScene hostScene, bool hostHasRun, GameScene clientScene, bool clientSettled,
            bool worldMatchesBundle)
        {
            // Never interrupt INTRO or a load in progress; the next settle decides again.
            if (clientScene == GameScene.Intro || !clientSettled)
                return new FollowDecision(FollowAction.Wait, GameScene.None);
            var target = Target(hostScene, hostHasRun, clientScene);
            if (target != clientScene)
                return new FollowDecision(FollowAction.Load, target);
            if (target == GameScene.Main && !worldMatchesBundle)
                return new FollowDecision(FollowAction.Load, target);
            return new FollowDecision(FollowAction.Stay, target);
        }
    }
}
