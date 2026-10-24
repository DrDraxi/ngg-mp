using System.Collections.Generic;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Game.Lifecycle;
using Xunit;

namespace NGGMultiplayer.Tests.GameLifecycle
{
    public class PersonalizationTests
    {
        [Fact]
        public void TrackFact_RoundTrips()
        {
            var value = Personalization.EncodeTrack("ML_DoLMUGC", 4);
            Assert.True(Personalization.TryDecodeTrack(value, out var set, out var index));
            Assert.Equal("ML_DoLMUGC", set);
            Assert.Equal(4, index);
        }

        [Fact]
        public void TrackFact_MatchesTheSpecifiedEncoding()
        {
            // str setName, vi index (NetWriter), as Progression writes it.
            var writer = new NetWriter();
            writer.WriteString("CH_Putt3");
            writer.WriteVarInt(12);
            Assert.True(Personalization.TryDecodeTrack(writer.ToArray(), out var set, out var index));
            Assert.Equal("CH_Putt3", set);
            Assert.Equal(12, index);
        }

        [Fact]
        public void TrackFact_RejectsAbsentOrMalformedValues()
        {
            Assert.False(Personalization.TryDecodeTrack(null, out _, out _));
            Assert.False(Personalization.TryDecodeTrack(new byte[0], out _, out _));
            Assert.False(Personalization.TryDecodeTrack(new byte[] { 5, 65 }, out _, out _));
            Assert.False(Personalization.TryDecodeTrack(Personalization.EncodeTrack("", 1), out _, out _));
            Assert.False(Personalization.TryDecodeTrack(Personalization.EncodeTrack("ML_A", -1), out _, out _));
        }

        [Fact]
        public void PickBall_KeepsTheOwnChoiceWhenUnlockedSomewhere()
        {
            var host = new List<bool> { true, false, true };
            var own = new List<bool> { true, true, false };
            Assert.Equal(2, Personalization.PickBall(2, true, own, host, 0));
            Assert.Equal(1, Personalization.PickBall(1, true, own, host, 0));
        }

        [Fact]
        public void PickBall_FallsBackToTheHost()
        {
            var host = new List<bool> { true, false, false, false };
            var own = new List<bool> { true, false };
            Assert.Equal(0, Personalization.PickBall(3, true, own, host, 0));
            Assert.Equal(0, Personalization.PickBall(9, true, own, host, 0));
            Assert.Equal(0, Personalization.PickBall(-1, true, own, host, 0));
            Assert.Equal(3, Personalization.PickBall(1, false, own, new List<bool> { true, true, true, true }, 3));
            Assert.Equal(2, Personalization.PickBall(1, true, null, null, 2));
        }
    }
}
