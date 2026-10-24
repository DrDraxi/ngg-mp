// Regression tests for the game facts confirmed against the decompiled game (docs/confirm answers-1/2): power scale
// (#53, #127), rest distance units (#54), golf stance (#55), LMUGC hole ids and scoring (#45, #52, #126, #139).
// Compiled only with the Presence model sources linked (PRESENCE_MODEL_LINKED).
#if PRESENCE_MODEL_LINKED
using System;
using NGGMultiplayer.Game.Presence;
using Xunit;

namespace NGGMultiplayer.Tests.GamePresence
{
    public class PowerAndDistanceTests
    {
        [Theory]
        [InlineData(12f, 15f, 0.8f)]   // a 15-unit club swung to 12: 80 %, not 1200 %
        [InlineData(15f, 15f, 1f)]
        [InlineData(0.9f, 1f, 0.9f)]
        [InlineData(0f, 15f, 0f)]
        public void PowerFraction_DividesByTheClubsVisualMult(float power, float mult, float expected) =>
            Assert.Equal(expected, PresenceText.PowerFraction(power, mult), 4);

        [Theory]
        [InlineData(12f, 0f)]
        [InlineData(12f, -1f)]
        [InlineData(float.NaN, 15f)]
        [InlineData(12f, float.PositiveInfinity)]
        public void PowerFraction_IsUnknownWithoutAScale(float power, float mult) =>
            Assert.Equal(PresenceText.UnknownPower, PresenceText.PowerFraction(power, mult));
    }

    public class GolfStanceTests
    {
        [Fact]
        public void Golfer_StandsLeftOfTheAimLine_FacingTheBall()
        {
            // Aim along +Z: a right-handed golfer stands on -X, faces +X (the ball), the target on their left.
            GolfStance.Place(10f, 20f, 0f, out float x, out float z, out float yaw);
            Assert.Equal(10f - GolfStance.StanceDistance, x, 4);
            Assert.Equal(20f, z, 4);
            Assert.Equal(90f, yaw, 4);
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(37f)]
        [InlineData(180f)]
        [InlineData(-90f)]
        [InlineData(400f)]
        public void Golfer_IsStanceDistanceFromTheBall_AndFacesIt(float aim)
        {
            GolfStance.Place(3f, -4f, aim, out float x, out float z, out float yaw);
            float dx = 3f - x;
            float dz = -4f - z;
            Assert.Equal(GolfStance.StanceDistance, (float)Math.Sqrt(dx * dx + dz * dz), 4);
            // The facing direction (sin yaw, cos yaw) points from the feet to the ball.
            double r = yaw * Math.PI / 180.0;
            Assert.Equal(dx / GolfStance.StanceDistance, (float)Math.Sin(r), 4);
            Assert.Equal(dz / GolfStance.StanceDistance, (float)Math.Cos(r), 4);
            Assert.InRange(yaw, 0f, 359.999f);
            Assert.Equal(GolfStance.NormalizeYaw(aim + 90f), yaw, 3);
        }

        [Fact]
        public void NonFiniteInput_StaysOnTheBall()
        {
            GolfStance.Place(1f, 2f, float.NaN, out float x, out float z, out _);
            Assert.Equal(1f, x);
            Assert.Equal(2f, z);
        }

        [Theory]
        [InlineData(0f, 0f)]
        [InlineData(360f, 0f)]
        [InlineData(-90f, 270f)]
        [InlineData(725f, 5f)]
        public void NormalizeYaw_WrapsInto0To360(float yaw, float expected) =>
            Assert.Equal(expected, GolfStance.NormalizeYaw(yaw), 3);
    }

    public class LmugcRulesTests
    {
        [Theory]
        [InlineData("Hole7", 7)]
        [InlineData("LMUGC_Hole_9", 9)]
        [InlineData("h12b3", 12)]   // the FIRST number, as the game's Regex "\d+" match (answers #45, #52)
        [InlineData("Hole3_Cup2", 3)]
        [InlineData("hole 10", 10)]
        [InlineData("tee", -1)]
        [InlineData("", -1)]
        [InlineData(null, -1)]
        [InlineData("x12345678", -1)]
        public void FirstNumber_IsTheGamesHoleNumber(string id, int expected) =>
            Assert.Equal(expected, LmugcRules.FirstNumber(id));

        [Fact]
        public void ResolveHole_PrefersTheSaveEntryTheCallChanged()
        {
            var before = new[] { 3, 4, 0, 0, 0, 0, 0, 0, 0 };
            var after = new[] { 3, 4, 5, 0, 0, 0, 0, 0, 0 };
            // A 0-based id ("Hole2" for the third hole) is resolved by the save entry, not the number.
            Assert.Equal(3, LmugcRules.ResolveHole(before, after, "Hole2", 9, out var source));
            Assert.Equal(HoleSource.Save, source);
            Assert.Equal(1, LmugcRules.IdOffset("Hole2", 3));
            Assert.Equal(0, LmugcRules.IdOffset("Hole3", 3));
            Assert.Null(LmugcRules.IdOffset("Hole7", 3));
        }

        [Fact]
        public void ResolveHole_FallsBackToTheId_ThenToNothing()
        {
            var same = new[] { 3, 0, 0, 0, 0, 0, 0, 0, 0 };
            Assert.Equal(4, LmugcRules.ResolveHole(same, (int[])same.Clone(), "Hole4", 9, out var source));
            Assert.Equal(HoleSource.Id, source);
            Assert.Equal(5, LmugcRules.ResolveHole(null, same, "Hole5_b7", 9, out source));
            Assert.Equal(HoleSource.Id, source);
            Assert.Equal(-1, LmugcRules.ResolveHole(null, null, "Hole0", 9, out source));
            Assert.Equal(HoleSource.None, source);
            Assert.Equal(-1, LmugcRules.ResolveHole(null, null, "Hole12", 9, out source));
            Assert.Equal(-1, LmugcRules.ResolveHole(null, null, "cup", 9, out source));
        }

        [Fact]
        public void ResolveHole_ReadsTheIdWithTheLearnedBase()
        {
            // JustGolf's last hole: the save as the finishing call found it still tells the hole.
            var before = new[] { 3, 4, 5, 3, 4, 5, 3, 4, 0 };
            var atFinish = new[] { 3, 4, 5, 3, 4, 5, 3, 4, 6 };
            Assert.Equal(9, LmugcRules.ResolveHole(before, atFinish, "Hole8", 9, 1, out var source));
            Assert.Equal(HoleSource.Save, source);
            // Without it (scores reset), a 0-based id is read with the offset learned on earlier holes.
            Assert.Equal(9, LmugcRules.ResolveHole(before, new int[9], "Hole8", 9, 1, out source));
            Assert.Equal(HoleSource.Id, source);
            Assert.Equal(1, LmugcRules.ResolveHole(null, null, "Hole0", 9, 1, out source));
            Assert.Equal(-1, LmugcRules.ResolveHole(null, null, "Hole9", 9, 1, out source));
            Assert.Equal(HoleSource.None, source);
            Assert.Equal(8, LmugcRules.ResolveHole(before, new int[9], "Hole8", 9, 0, out source));
        }

        [Fact]
        public void ChangedHole_IsAmbiguousWhenSeveralEntriesChange()
        {
            var before = new int[9];
            var after = new[] { 1, 2, 0, 0, 0, 0, 0, 0, 0 };
            Assert.Equal(-1, LmugcRules.ChangedHole(before, after, 9));
            Assert.Equal(-1, LmugcRules.ChangedHole(before, new int[9], 9));
            Assert.Equal(9, LmugcRules.ChangedHole(before, new[] { 0, 0, 0, 0, 0, 0, 0, 0, 6 }, 9));
            // Shorter arrays are compared as far as both go.
            Assert.Equal(-1, LmugcRules.ChangedHole(new int[2], new[] { 0, 0, 7 }, 9));
        }

        [Fact]
        public void RoundTotal_CountsUnplayedHolesAsParPlusTwo()
        {
            var pars = new[] { 4, 3, 5, 4, 4, 3, 5, 4, 4 };
            var scores = new[] { 4, 3, 6, 0, 0, 0, 0, 0, 0 };
            int total = LmugcRules.RoundTotal(scores, pars, 9, out bool complete);
            // 13 played + pars of holes 4..9 (4+4+3+5+4+4) + 6 * 2 unplayed.
            Assert.Equal(13 + 24 + 12, total);
            Assert.True(complete);

            var all = new[] { 1, 1, 1, 1, 1, 1, 1, 1, 1 };
            Assert.Equal(9, LmugcRules.RoundTotal(all, pars, 9, out complete));
            Assert.True(complete);
        }

        [Fact]
        public void RoundTotal_WithUnknownPars_IsMarkedIncomplete()
        {
            var scores = new[] { 4, 0, 0, 0, 0, 0, 0, 0, 0 };
            Assert.Equal(4 + 5, LmugcRules.RoundTotal(scores, new[] { 0, 3 }, 9, out bool complete));
            Assert.False(complete);
            Assert.Equal(0, LmugcRules.RoundTotal(null, null, 9, out complete));
            Assert.False(complete);
            Assert.False(LmugcRules.IsScored(0));
            Assert.False(LmugcRules.IsScored(-1));
            Assert.True(LmugcRules.IsScored(1));
        }
    }
}
#endif
