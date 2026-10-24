// Which cutout of a remote player a viewer sees (directional billboard avatars) and the sprite file names.
// Compiled only with the Presence model sources linked (PRESENCE_MODEL_LINKED).
#if PRESENCE_MODEL_LINKED
using System.Linq;
using NGGMultiplayer.Game.Presence;
using Xunit;

namespace NGGMultiplayer.Tests.GamePresence
{
    public class BillboardAnglesTests
    {
        [Theory]
        [InlineData(0f, 0f, 1f, 0f)]     // player faces +Z, viewer ahead: sees the front
        [InlineData(0f, 1f, 0f, 90f)]    // viewer on the player's right (+X)
        [InlineData(0f, 0f, -1f, 180f)]  // behind
        [InlineData(0f, -1f, 0f, 270f)]  // on the left
        [InlineData(90f, 1f, 0f, 0f)]    // player faces +X, viewer at +X: front again
        [InlineData(90f, 0f, 1f, 270f)]  // player faces +X, viewer at +Z: the player's left
        [InlineData(-90f, 0f, 1f, 90f)]
        public void RelativeYaw_IsTheViewersBearingFromThePlayersFacing(float facing, float viewerX, float viewerZ, float expected) =>
            Assert.Equal(expected, BillboardAngles.RelativeYaw(0f, 0f, facing, viewerX, viewerZ), 3);

        [Fact]
        public void RelativeYaw_UsesThePlayersPosition() =>
            Assert.Equal(180f, BillboardAngles.RelativeYaw(10f, 10f, 0f, 10f, 5f), 3);

        [Theory]
        [InlineData(0f, 0f, 0f)]
        [InlineData(float.NaN, 1f, 0f)]
        [InlineData(0f, float.PositiveInfinity, 0f)]
        public void RelativeYaw_IsNaNWithoutAHorizontalOffsetOrFiniteInput(float facing, float viewerX, float viewerZ) =>
            Assert.True(float.IsNaN(BillboardAngles.RelativeYaw(0f, 0f, facing, viewerX, viewerZ)));

        [Theory]
        [InlineData(0f, 0)]
        [InlineData(22f, 0)]
        [InlineData(23f, 1)]
        [InlineData(90f, 2)]
        [InlineData(180f, 4)]
        [InlineData(200f, 4)]
        [InlineData(270f, 6)]
        [InlineData(338f, 0)]
        [InlineData(-45f, 7)]
        [InlineData(720f + 135f, 3)]
        [InlineData(float.NaN, 0)]
        public void Direction_IsTheNearestSector(float yaw, int expected) =>
            Assert.Equal(expected, BillboardAngles.Direction(yaw));

        [Theory]
        [InlineData(25f, 0, 0)]    // just past the edge: still front
        [InlineData(28f, 0, 0)]
        [InlineData(29f, 0, 1)]    // past edge + hysteresis
        [InlineData(340f, 1, 0)]   // far away: picks fresh
        [InlineData(20f, 1, 1)]    // coming back from front-right, within hysteresis
        [InlineData(15f, 1, 0)]
        [InlineData(100f, -1, 2)]  // none yet
        [InlineData(float.NaN, 5, 5)]
        [InlineData(float.NaN, -1, 0)]
        public void Direction_WithHysteresis_KeepsTheCurrentSectorNearItsEdge(float yaw, int current, int expected) =>
            Assert.Equal(expected, BillboardAngles.Direction(yaw, current));

        [Theory]
        [InlineData(0, 0)]
        [InlineData(1, 7)]
        [InlineData(2, 6)]
        [InlineData(3, 5)]
        [InlineData(4, 4)]
        [InlineData(6, 2)]
        public void Mirror_SwapsRightAndLeft(int direction, int expected) =>
            Assert.Equal(expected, BillboardAngles.Mirror(direction));

        [Fact]
        public void Resolve_PrefersTheExactDrawing()
        {
            Assert.True(BillboardAngles.Resolve(2, d => true, out int source, out bool flip));
            Assert.Equal(2, source);
            Assert.False(flip);
        }

        [Fact]
        public void Resolve_MirrorsTheOtherSide()
        {
            // Only front, right-hand side and back drawn: the left-hand side is the right flipped.
            int[] drawn = { 0, 1, 2, 3, 4 };
            Assert.True(BillboardAngles.Resolve(6, drawn.Contains, out int source, out bool flip));
            Assert.Equal(2, source);
            Assert.True(flip);
            Assert.True(BillboardAngles.Resolve(7, drawn.Contains, out source, out flip));
            Assert.Equal(1, source);
            Assert.True(flip);
        }

        [Fact]
        public void Resolve_FallsBackToTheNearestDrawing()
        {
            // Four views only: three-quarter viewers get the nearer of front/back/side.
            int[] drawn = { 0, 2, 4 };
            Assert.True(BillboardAngles.Resolve(1, drawn.Contains, out int source, out bool flip));
            Assert.Equal(2, source);  // offset +1 is tried first
            Assert.False(flip);
            Assert.True(BillboardAngles.Resolve(5, drawn.Contains, out source, out flip));
            Assert.Equal(2, source);  // back-left: 6 (mirror of 2) comes before 4
            Assert.True(flip);
        }

        [Fact]
        public void Resolve_WithASingleDrawing_AlwaysShowsIt()
        {
            for (int d = 0; d < BillboardAngles.Directions; d++)
            {
                Assert.True(BillboardAngles.Resolve(d, x => x == 0, out int source, out _));
                Assert.Equal(0, source);
            }
        }

        [Fact]
        public void Resolve_IsFalseWithNothingDrawn() =>
            Assert.False(BillboardAngles.Resolve(3, _ => false, out _, out _));

        [Theory]
        [InlineData("stand_front", "stand", 0, 0)]
        [InlineData("golf_back_left", "golf", 5, 0)]
        [InlineData("walk_right_3", "walk", 2, 3)]
        [InlineData("Walk_Front_Right_12", "walk", 1, 12)]
        [InlineData("crouch_left", "crouch", 6, 0)]
        public void TryParseName_ReadsPoseDirectionAndFrame(string name, string pose, int direction, int frame)
        {
            Assert.True(BillboardAngles.TryParseName(name, out string p, out int d, out int f));
            Assert.Equal(pose, p);
            Assert.Equal(direction, d);
            Assert.Equal(frame, f);
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("stand")]
        [InlineData("stand_up")]
        [InlineData("_front")]
        [InlineData("stand_front_x")]
        [InlineData("stand_front_1_2")]
        [InlineData("stand_front_-1")]
        public void TryParseName_RejectsOtherNames(string name) =>
            Assert.False(BillboardAngles.TryParseName(name, out _, out _, out _));

        [Fact]
        public void Name_RoundTripsThroughTryParseName()
        {
            for (int d = 0; d < BillboardAngles.Directions; d++)
            {
                Assert.True(BillboardAngles.TryParseName("stand_" + BillboardAngles.Name(d), out _, out int parsed, out _));
                Assert.Equal(d, parsed);
            }
        }
    }
}
#endif
