// The swing a remote golfer's cutout plays when their shot is heard.
// Compiled only with the Presence model sources linked (PRESENCE_MODEL_LINKED).
#if PRESENCE_MODEL_LINKED
using NGGMultiplayer.Game.Presence;
using Xunit;

namespace NGGMultiplayer.Tests.GamePresence
{
    public class SwingTimelineTests
    {
        [Theory]
        [InlineData(0f, "swing", 0)]      // top of the backswing first
        [InlineData(0.21f, "swing", 0)]
        [InlineData(0.22f, "golf", 0)]    // impact: the address drawing
        [InlineData(0.29f, "golf", 0)]
        [InlineData(0.30f, "swing", 1)]   // finish, held
        [InlineData(1.89f, "swing", 1)]
        public void Sample_PlaysTopImpactFinish(float elapsed, string pose, int frame)
        {
            Assert.True(SwingTimeline.Sample(elapsed, out string p, out int f));
            Assert.Equal(pose, p);
            Assert.Equal(frame, f);
        }

        [Theory]
        [InlineData(-0.01f)]
        [InlineData(1.91f)]
        [InlineData(10f)]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        [InlineData(float.NegativeInfinity)]  // never swung
        public void Sample_IsFalseOutsideTheSwing(float elapsed) =>
            Assert.False(SwingTimeline.Sample(elapsed, out _, out _));

        [Theory]
        [InlineData("Putter", true)]
        [InlineData("putter", true)]
        [InlineData("Iron", false)]
        [InlineData("Driver", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsPutt_ByClubName(string club, bool expected) =>
            Assert.Equal(expected, SwingTimeline.IsPutt(club));

        [Fact]
        public void SwingNames_ParseAsAvatarSprites()
        {
            Assert.True(BillboardAngles.TryParseName("swing_back_left_1", out string pose, out int direction, out int frame));
            Assert.Equal(SwingTimeline.Pose, pose);
            Assert.Equal(5, direction);
            Assert.Equal(SwingTimeline.FinishFrame, frame);
        }
    }
}
#endif
