// How a cutout without walk drawings moves while walking or crouch-walking (directional billboard avatars).
// Compiled only with the Presence model sources linked (PRESENCE_MODEL_LINKED).
#if PRESENCE_MODEL_LINKED
using NGGMultiplayer.Game.Presence;
using Xunit;

namespace NGGMultiplayer.Tests.GamePresence
{
    public class StepMotionTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Sample_StartsOnAPlantedFoot(bool crouched)
        {
            StepMotion.Sample(0f, crouched, out float lift, out float roll);
            Assert.Equal(0f, lift);
            Assert.Equal(0f, roll);
        }

        [Fact]
        public void Sample_WalkingHopsWithoutTilting()
        {
            float midStep = 0.5f / StepMotion.StepRate;
            StepMotion.Sample(midStep, false, out float lift, out float roll);
            Assert.Equal(StepMotion.StepLift, lift, 4);
            Assert.Equal(0f, roll);
        }

        [Fact]
        public void Sample_CrouchWalkingRocksToTheOtherSideEachStep()
        {
            float step = 1f / StepMotion.CrouchStepRate;
            StepMotion.Sample(step * 0.5f, true, out float lift, out float first);
            StepMotion.Sample(step * 1.5f, true, out float lift2, out float second);
            Assert.Equal(StepMotion.CrouchStepLift, lift, 4);
            Assert.Equal(StepMotion.CrouchStepLift, lift2, 4);
            Assert.Equal(StepMotion.CrouchRollDegrees, first, 3);
            Assert.Equal(-StepMotion.CrouchRollDegrees, second, 3);
        }

        [Theory]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        [InlineData(-1f)]
        public void Sample_IsStillForABadTime(float elapsed)
        {
            StepMotion.Sample(elapsed, true, out float lift, out float roll);
            Assert.Equal(0f, lift);
            Assert.Equal(0f, roll);
        }
    }
}
#endif
