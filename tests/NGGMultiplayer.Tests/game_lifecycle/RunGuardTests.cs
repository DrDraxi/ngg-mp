using System;
using NGGMultiplayer.Game.Lifecycle;
using Xunit;

namespace NGGMultiplayer.Tests.GameLifecycle
{
    /// <summary>docs/confirm #74: the injected run must not take SetUpScene's broken-save path or Instantiate(null).</summary>
    public class RunGuardTests
    {
        private static readonly Func<string, bool> Known = name => name == "Main1" || name == "Main2" || name == "ML_NormalGolf";

        [Theory]
        [InlineData("Main", true)]
        [InlineData("Menu2", false)]
        [InlineData("CREDITS", false)]
        [InlineData("main", false)]
        [InlineData(null, false)]
        public void InjectedLoad_SetsUpTheSceneOnlyInMain(string scene, bool setsUp)
        {
            Assert.Equal(setsUp, RunGuard.LoadSetsUpScene(scene));
        }

        [Theory]
        [InlineData(true, "", false, 0, false)]
        [InlineData(true, null, false, 0, false)]
        [InlineData(true, "", true, 0, true)]
        [InlineData(true, "", false, 2, true)]
        [InlineData(true, "Main1", true, 3, false)]
        // JustGolf never counts as broken.
        [InlineData(false, "", true, 3, false)]
        public void BrokenRuleMatchesTheGame(bool story, string set, bool finishedDemo, int bounties, bool broken)
        {
            Assert.Equal(broken, RunGuard.LooksBroken(story, set, finishedDemo, bounties));
        }

        [Fact]
        public void KnownSetIsKept()
        {
            var d = RunGuard.Decide(true, "Main2", 4, true, 1, "Main1", 0, Known);
            Assert.Equal(RunGuardAction.Keep, d.Action);
        }

        [Fact]
        public void EmptySetOnAFreshRunIsKept()
        {
            var d = RunGuard.Decide(true, "", 0, false, 0, "", 0, Known);
            Assert.Equal(RunGuardAction.Keep, d.Action);
        }

        [Fact]
        public void UnknownTrackFallsBackToTheHostsSet()
        {
            var d = RunGuard.Decide(true, "Hacked", 7, true, 0, "Main1", 3, Known);
            Assert.Equal(RunGuardAction.Replace, d.Action);
            Assert.Equal("Main1", d.SetName);
            Assert.Equal(3, d.Index);
            Assert.Contains("Hacked", d.Reason);
        }

        [Fact]
        public void BrokenEmptySetFallsBackToTheHostsSet()
        {
            var d = RunGuard.Decide(true, "", 0, true, 2, "Main2", 5, Known);
            Assert.Equal(RunGuardAction.Replace, d.Action);
            Assert.Equal("Main2", d.SetName);
            Assert.Equal(5, d.Index);
        }

        [Fact]
        public void UnknownSetOnAFreshRunIsBlanked()
        {
            var d = RunGuard.Decide(true, "Nope", 2, false, 0, "AlsoNope", 1, Known);
            Assert.Equal(RunGuardAction.Replace, d.Action);
            Assert.Equal(string.Empty, d.SetName);
            Assert.Equal(0, d.Index);
        }

        [Fact]
        public void UnknownSetInJustGolfIsBlanked()
        {
            var d = RunGuard.Decide(false, "Nope", 2, true, 4, null, 0, Known);
            Assert.Equal(RunGuardAction.Replace, d.Action);
            Assert.Equal(string.Empty, d.SetName);
        }

        [Fact]
        public void NoSafeSetIsUnfixable()
        {
            Assert.Equal(RunGuardAction.Unfixable, RunGuard.Decide(true, "Nope", 2, true, 0, "AlsoNope", 1, Known).Action);
            Assert.Equal(RunGuardAction.Unfixable, RunGuard.Decide(true, "", 0, false, 1, "", 0, Known).Action);
            Assert.Equal(RunGuardAction.Unfixable, RunGuard.Decide(true, "", 0, true, 0, null, 0, Known).Action);
        }

        [Fact]
        public void HostIndexIsNeverNegative()
        {
            var d = RunGuard.Decide(true, "Nope", 0, false, 0, "Main1", -3, Known);
            Assert.Equal("Main1", d.SetName);
            Assert.Equal(0, d.Index);
        }

        [Fact]
        public void WithoutALookupEveryNameCountsAsKnown()
        {
            Assert.Equal(RunGuardAction.Keep, RunGuard.Decide(true, "Anything", 1, true, 1, null, 0, null).Action);
            // The broken rule still applies.
            var d = RunGuard.Decide(true, "", 0, true, 0, "HostSet", 2, null);
            Assert.Equal(RunGuardAction.Replace, d.Action);
            Assert.Equal("HostSet", d.SetName);
        }

        [Fact]
        public void AThrowingLookupDoesNotDiscardTheName()
        {
            Func<string, bool> broken = _ => throw new InvalidOperationException("not ready");
            Assert.Equal(RunGuardAction.Keep, RunGuard.Decide(true, "Main1", 1, false, 0, null, 0, broken).Action);
        }
    }
}
