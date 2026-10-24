// The ghost ball trail's rules (spec 7.3) and the player palette it is coloured with.
// Compiled only with the Presence model sources linked (PRESENCE_MODEL_LINKED).
#if PRESENCE_MODEL_LINKED
using System.Collections.Generic;
using NGGMultiplayer.Game;
using NGGMultiplayer.Game.Presence;
using Xunit;

namespace NGGMultiplayer.Tests.GamePresence
{
    public class PlayerPaletteTests
    {
        [Fact]
        public void EightDistinctColours_ThatWrapForAnyIndex()
        {
            var seen = new HashSet<(float, float, float)>();
            for (int i = 0; i < PlayerPalette.Count; i++)
            {
                PlayerPalette.Get(i, out float r, out float g, out float b);
                Assert.True(seen.Add((r, g, b)), $"colour {i} repeats");
                PlayerPalette.Get(i + PlayerPalette.Count, out float r2, out float g2, out float b2);
                Assert.Equal((r, g, b), (r2, g2, b2));
                PlayerPalette.Get(i - PlayerPalette.Count, out float r3, out float g3, out float b3);
                Assert.Equal((r, g, b), (r3, g3, b3));
            }
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(7, 7)]
        [InlineData(8, 0)]
        [InlineData(-1, 7)]
        [InlineData(int.MinValue, 0)]
        [InlineData(int.MaxValue, 7)]
        public void Slot_IsAlwaysInRange(int index, int slot) => Assert.Equal(slot, PlayerPalette.Slot(index));

        [Fact]
        public void KeepsTheColoursAvatarsAndNameTagsAlreadyUsed()
        {
            // Host red, first guest blue: the trail must match the avatar and name tag of the same player.
            PlayerPalette.Get(0, out float r0, out float g0, out float b0);
            Assert.Equal((0.95f, 0.30f, 0.25f), (r0, g0, b0));
            PlayerPalette.Get(1, out float r1, out float g1, out float b1);
            Assert.Equal((0.25f, 0.55f, 0.95f), (r1, g1, b1));
        }
    }

    public class GhostTrailRulesTests
    {
        [Theory]
        [InlineData(0.04f, 0.025f)]
        [InlineData(0.01f, 0.015f)]
        [InlineData(0f, 0.015f)]
        public void Width_scales_the_game_trail_and_has_a_floor(float game, float expected) =>
            Assert.Equal(expected, GhostTrailRules.Width(game), 3);

        [Fact]
        public void Lift_moves_a_channel_toward_white()
        {
            Assert.Equal(0.35f, GhostTrailRules.Lift(0f), 3);
            Assert.Equal(1f, GhostTrailRules.Lift(1f), 3);
        }

        private const double Frame = 1.0 / 60.0;

        [Fact]
        public void FirstPose_Clears()
        {
            var rules = new GhostTrailRules();
            Assert.True(rules.Step(0, 0, 0, 1.0, moving: false, holed: false, teleport: false).Clear);
            Assert.False(rules.Step(0, 0, 0, 1.0 + Frame, false, false, false).Clear);
        }

        [Fact]
        public void Flight_EmitsWithoutClearing_ThenStopsEmittingAtRest()
        {
            var rules = new GhostTrailRules();
            rules.Step(0, 0, 0, 0.0, false, false, false);
            Assert.True(rules.Step(0, 0, 0, Frame, true, false, false).Clear); // the shot starts
            double t = Frame;
            for (int i = 1; i <= 120; i++)
            {
                t += Frame;
                // 70 m/s: just over a metre per frame.
                var step = rules.Step(i * 70f / 60f, i * 0.05f, 0f, t, true, false, false);
                Assert.False(step.Clear);
                Assert.True(step.Emit);
            }
            var rest = rules.Step(140f, 6f, 0f, t + Frame, false, false, false);
            Assert.False(rest.Clear); // fades out with the trail's time, not cleared
            Assert.False(rest.Emit);
        }

        [Fact]
        public void NewShot_Clears()
        {
            var rules = new GhostTrailRules();
            rules.Step(0, 0, 0, 0.0, true, false, false);
            rules.Step(1, 0, 0, Frame, true, false, false);
            rules.Step(1, 0, 0, 2 * Frame, false, false, false);
            var shot = rules.Step(1, 0, 0, 3 * Frame, true, false, false);
            Assert.True(shot.Clear);
            Assert.True(shot.Emit);
        }

        [Fact]
        public void Teleport_ClearsOnItsRisingEdgeOnly()
        {
            var rules = new GhostTrailRules();
            rules.Step(0, 0, 0, 0.0, false, false, false);
            Assert.True(rules.Step(3, 0, 0, Frame, false, false, teleport: true).Clear);
            // The sampled teleport flag stays set until the next state is reached.
            Assert.False(rules.Step(3, 0, 0, 2 * Frame, false, false, teleport: true).Clear);
            Assert.False(rules.Step(3, 0, 0, 3 * Frame, false, false, teleport: false).Clear);
            Assert.True(rules.Step(3, 0, 0, 4 * Frame, false, false, teleport: true).Clear);
        }

        [Fact]
        public void Reset_ClearsAtTheNextPose()
        {
            // The ghost was hidden (holed, owner in a menu): it must not streak from its last spot when it reappears.
            var rules = new GhostTrailRules();
            rules.Step(0, 0, 0, 0.0, true, false, false);
            rules.Reset();
            Assert.True(rules.Step(1, 0, 0, Frame, true, false, false).Clear);
        }

        [Fact]
        public void BackFromHoled_Clears_AndHoledNeverEmits()
        {
            var rules = new GhostTrailRules();
            rules.Step(0, 0, 0, 0.0, true, false, false);
            var holed = rules.Step(0, 0, 0, Frame, true, holed: true, teleport: false);
            Assert.False(holed.Emit);
            Assert.True(rules.Step(0, 0, 0, 2 * Frame, false, false, false).Clear);
        }

        [Fact]
        public void JumpAcrossTheMap_ClearsEvenWithoutATeleportFlag()
        {
            var rules = new GhostTrailRules();
            rules.Step(0, 0, 0, 0.0, true, false, false);
            Assert.True(rules.Step(300, 0, 0, Frame, true, false, false).Clear);
        }

        [Theory]
        [InlineData(1f, 1.0 / 60, false)]    // a ball in flight
        [InlineData(5f, 0.001, false)]       // short steps never count
        [InlineData(6f, 0.1, false)]         // 60 m/s through a 100 ms hitch
        [InlineData(10f, 0.05, true)]        // 200 m/s
        [InlineData(50f, 0.0, true)]         // no time passed
        [InlineData(float.NaN, 0.1, true)]
        [InlineData(10f, double.NaN, true)]
        public void IsJump(float distance, double seconds, bool jump) => Assert.Equal(jump, GhostTrailRules.IsJump(distance, seconds));

        [Theory]
        [InlineData(PoseFlags.BallMoving, true)]
        [InlineData((byte)(PoseFlags.BallMoving | PoseFlags.Visible), true)]
        [InlineData(0, false)]                       // placed or reset: it may slide, but it is not in play
        [InlineData(PoseFlags.BallHoled, false)]
        public void IsMoving(byte flags, bool moving) => Assert.Equal(moving, GhostTrailRules.IsMoving(flags));

        [Theory]
        [InlineData(2f, 0.7f, 2f)]                 // configured wins
        [InlineData(0f, 0.7f, 0.7f)]               // 0: the game ball trail's time
        [InlineData(0f, 0f, GhostTrailRules.DefaultSeconds)]
        [InlineData(0f, float.PositiveInfinity, GhostTrailRules.DefaultSeconds)]
        [InlineData(float.NaN, 0.7f, 0.7f)]
        [InlineData(0f, 0.01f, GhostTrailRules.MinSeconds)]
        [InlineData(50f, 0f, GhostTrailRules.MaxSeconds)]
        public void Seconds(float configured, float game, float expected) => Assert.Equal(expected, GhostTrailRules.Seconds(configured, game));
    }

    public class GhostBallRulesTests
    {
        [Theory]
        [InlineData(0.12f, 0.12f, 0.12f, 0.12f)]
        [InlineData(0.10f, 0.11f, 0.09f, 0.11f)]   // the largest side
        [InlineData(0.0427f, 0.0427f, 0.0427f, 0.0427f)]
        public void Diameter_is_the_game_balls_size(float x, float y, float z, float expected) =>
            Assert.Equal(expected, GhostBallRules.Diameter(x, y, z), 4);

        [Theory]
        [InlineData(0f, 0f, 0f)]                    // hidden or not set up: empty bounds
        [InlineData(0.01f, 0.01f, 0.01f)]
        [InlineData(3f, 3f, 3f)]
        [InlineData(float.NaN, 0.1f, 0.1f)]
        [InlineData(float.PositiveInfinity, 0.1f, 0.1f)]
        public void Diameter_ignores_what_is_not_a_ball(float x, float y, float z) =>
            Assert.Equal(0f, GhostBallRules.Diameter(x, y, z));

        [Theory]
        [InlineData(1f, 1f, 1f, 0.062f, 0.062f, 0.062f, 0.062f)]   // a unit sphere mesh scaled to the ball
        [InlineData(2f, 2f, 2f, 0.03f, 0.03f, 0.03f, 0.06f)]
        [InlineData(1f, 1f, 1f, -0.05f, 0.05f, 0.05f, 0.05f)]    // a mirrored scale is still a size
        public void DiameterFromMesh_is_mesh_size_times_scale(float mx, float my, float mz, float sx, float sy, float sz, float expected) =>
            Assert.Equal(expected, GhostBallRules.DiameterFromMesh(mx, my, mz, sx, sy, sz), 4);

        [Fact]
        public void DiameterFromMesh_does_not_grow_with_rotation()
        {
            // The world bounds of a 0.062 m sphere mesh rotated 45 degrees about two axes are about 0.107 m across;
            // the mesh measurement ignores rotation.
            float worldBounds = 0.062f * (float)System.Math.Sqrt(3);
            Assert.Equal(0.107f, GhostBallRules.Diameter(worldBounds, worldBounds, worldBounds), 3);
            Assert.Equal(0.062f, GhostBallRules.DiameterFromMesh(1f, 1f, 1f, 0.062f, 0.062f, 0.062f), 4);
        }
    }
}
#endif
