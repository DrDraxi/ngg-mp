#if PRESENCE_MODEL_LINKED
using NGGMultiplayer.Game.Presence;
using Xunit;

namespace NGGMultiplayer.Tests.GamePresence
{
    public class LookFramesTests
    {
        private static readonly int[] Keys = { 0, 10, 26, 40 };

        [Theory]
        [InlineData(-5, 0)]
        [InlineData(0, 0)]
        [InlineData(9, 0)]
        [InlineData(10, 1)]
        [InlineData(25, 1)]
        [InlineData(26, 2)]
        [InlineData(39, 2)]
        [InlineData(40, 3)]
        [InlineData(400, 3)]
        public void Hold_ShowsTheLastKeyAtOrBeforeTheFrame(long frame, int index) =>
            Assert.Equal(index, LookFrames.Hold(Keys, frame));

        [Fact]
        public void Hold_NoKeys_IsMinusOne()
        {
            Assert.Equal(-1, LookFrames.Hold(new int[0], 3));
            Assert.Equal(-1, LookFrames.Hold(null, 3));
            Assert.Equal(0, LookFrames.Hold(new[] { 5 }, 3));
        }

        [Theory]
        [InlineData("026", 26)]
        [InlineData("0", 0)]
        [InlineData("alpha_000", -1)]
        [InlineData("-3", -1)]
        [InlineData("", -1)]
        public void ParseFrame(string name, int frame) => Assert.Equal(frame, LookFrames.ParseFrame(name));
    }
}
#endif
