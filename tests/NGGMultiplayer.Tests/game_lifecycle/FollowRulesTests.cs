using NGGMultiplayer.Game;
using NGGMultiplayer.Game.Lifecycle;
using Xunit;

namespace NGGMultiplayer.Tests.GameLifecycle
{
    /// <summary>docs/GAME_INTEGRATION.md 4.3 / 4.4.</summary>
    public class FollowRulesTests
    {
        [Theory]
        // Host in Menu2: everyone waits in Menu2 (own world discarded).
        [InlineData(GameScene.Menu, GameScene.Menu, FollowAction.Stay, GameScene.Menu)]
        [InlineData(GameScene.Menu, GameScene.Main, FollowAction.Load, GameScene.Menu)]
        [InlineData(GameScene.Menu, GameScene.Credits, FollowAction.Load, GameScene.Menu)]
        // Host in Main: load Main with the host's save from anywhere.
        [InlineData(GameScene.Main, GameScene.Menu, FollowAction.Load, GameScene.Main)]
        [InlineData(GameScene.Main, GameScene.Credits, FollowAction.Load, GameScene.Main)]
        // Host watching the credits: only a client that played along follows.
        [InlineData(GameScene.Credits, GameScene.Main, FollowAction.Load, GameScene.Credits)]
        [InlineData(GameScene.Credits, GameScene.Credits, FollowAction.Stay, GameScene.Credits)]
        [InlineData(GameScene.Credits, GameScene.Menu, FollowAction.Stay, GameScene.Menu)]
        // Host in INTRO or anything else: wait in Menu2.
        [InlineData(GameScene.Intro, GameScene.Main, FollowAction.Load, GameScene.Menu)]
        [InlineData(GameScene.None, GameScene.Menu, FollowAction.Stay, GameScene.Menu)]
        [InlineData(GameScene.Other, GameScene.Credits, FollowAction.Load, GameScene.Menu)]
        public void JoinMatrix(GameScene host, GameScene client, object action, GameScene target)
        {
            var decision = FollowRules.Decide(host, true, client, true, false);
            Assert.Equal((FollowAction)action, decision.Action);
            Assert.Equal(target, decision.Target);
        }

        [Fact]
        public void ClientInMain_ReloadsUnlessItsWorldCameFromThisBundle()
        {
            // First bundle, epoch or mode changed: the loaded world is not the host's current one.
            Assert.Equal(FollowAction.Load, FollowRules.Decide(GameScene.Main, true, GameScene.Main, true, false).Action);
            // A resync with the same epoch never reloads.
            Assert.Equal(FollowAction.Stay, FollowRules.Decide(GameScene.Main, true, GameScene.Main, true, true).Action);
        }

        [Fact]
        public void HostWithoutRun_SendsClientsToTheMenu()
        {
            var decision = FollowRules.Decide(GameScene.Main, false, GameScene.Main, true, true);
            Assert.Equal(FollowAction.Load, decision.Action);
            Assert.Equal(GameScene.Menu, decision.Target);
        }

        [Fact]
        public void IntroAndUnsettledScenes_Wait()
        {
            Assert.Equal(FollowAction.Wait, FollowRules.Decide(GameScene.Main, true, GameScene.Intro, true, false).Action);
            Assert.Equal(FollowAction.Wait, FollowRules.Decide(GameScene.Main, true, GameScene.Menu, false, false).Action);
            Assert.Equal(FollowAction.Wait, FollowRules.Decide(GameScene.Menu, true, GameScene.Main, false, false).Action);
        }
    }
}
