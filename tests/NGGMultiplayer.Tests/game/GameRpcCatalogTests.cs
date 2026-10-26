using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NGGMultiplayer.Core.Sync;
using NGGMultiplayer.Game;
using NGGMultiplayer.Game.Lifecycle;
using NGGMultiplayer.Game.Presence;
using NGGMultiplayer.Game.Progression;
using Xunit;

namespace NGGMultiplayer.Tests.Game
{
    /// <summary>The three modules' RPC names together: the catalog of docs/GAME_INTEGRATION.md 2.2, no id clashes.</summary>
    public class GameRpcCatalogTests
    {
        private static List<string> Names(Type type) =>
            type.GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.IsLiteral && f.FieldType == typeof(string))
                .Select(f => (string)f.GetRawConstantValue())
                .Where(v => v.StartsWith("ngg.", StringComparison.Ordinal) && !v.EndsWith(".", StringComparison.Ordinal))
                .ToList();

        [Fact]
        public void EachModuleUsesOnlyItsOwnPrefix()
        {
            Assert.All(Names(typeof(LifecycleRpcs)), n => Assert.StartsWith(GameMessages.LifecyclePrefix, n));
            Assert.All(Names(typeof(PresenceRpc)), n => Assert.StartsWith(GameMessages.PresencePrefix, n));
            Assert.All(Names(typeof(ProgressionRpc)), n => Assert.StartsWith(GameMessages.ProgressionPrefix, n));
            Assert.Equal(GameMessages.ProgressionPrefix, ProgressionRpc.Prefix);
        }

        [Fact]
        public void CatalogMatchesTheSpec_AndIdsAreUnique()
        {
            var all = Names(typeof(LifecycleRpcs)).Concat(Names(typeof(PresenceRpc))).Concat(Names(typeof(ProgressionRpc))).ToList();
            var expected = new[]
            {
                "ngg.lc.scene",
                "ngg.pr.info", "ngg.pr.shot", "ngg.pr.rest", "ngg.pr.holed", "ngg.pr.wind", "ngg.pr.round", "ngg.pr.hole", "ngg.pr.card",
                "ngg.pr.match", "ngg.pr.mstroke", "ngg.pr.mstart", "ngg.pr.mretire",
                "ngg.pg.world", "ngg.pg.story", "ngg.pg.storyHost", "ngg.pg.claim", "ngg.pg.grant", "ngg.pg.bank",
                "ngg.pg.ballUnlock", "ngg.pg.note", "ngg.pg.digest", "ngg.pg.roundScore", "ngg.pg.lmugcBest"
            };
            Assert.Equal(expected.OrderBy(n => n, StringComparer.Ordinal), all.OrderBy(n => n, StringComparer.Ordinal));
            var ids = all.Select(RpcRouter.GetId).ToList();
            Assert.Equal(ids.Count, ids.Distinct().Count());
        }
    }
}
