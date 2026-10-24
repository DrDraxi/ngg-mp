// Needs the Presence model sources (src/NGGMultiplayer/Game/Presence/Model/*.cs) linked into the test project and
// PRESENCE_MODEL_LINKED defined (integrator, NGGMultiplayer.Tests.csproj); without them these tests compile to
// nothing so the suite still builds.
#if PRESENCE_MODEL_LINKED
using System;
using System.Collections.Generic;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Game.Presence;
using NGGMultiplayer.Runtime;
using Xunit;

namespace NGGMultiplayer.Tests.GamePresence
{
    public class PresenceWireTests
    {
        private static NetReader RoundTrip(Action<NetWriter> write)
        {
            var writer = new NetWriter();
            write(writer);
            return new NetReader(writer.ToArray());
        }

        [Fact]
        public void Info_RoundTrips_AndTruncatesLongStrings()
        {
            var info = new InfoMessage
            {
                Scene = 3,
                ControlMode = InfoMessage.Golf,
                SelectedBall = 4,
                Club = new string('x', 100),
                Activity = "Bounty BO_Code"
            };
            var read = InfoMessage.Read(RoundTrip(info.Write));
            Assert.Equal(3, read.Scene);
            Assert.Equal(InfoMessage.Golf, read.ControlMode);
            Assert.Equal(4, read.SelectedBall);
            Assert.Equal(PresenceWire.MaxClubBytes, read.Club.Length);
            Assert.Equal("Bounty BO_Code", read.Activity);
        }

        [Fact]
        public void Info_RejectsUnknownSceneAndMode()
        {
            Assert.Throws<ProtocolException>(() => InfoMessage.Read(RoundTrip(w =>
            {
                w.WriteByte(9);
                w.WriteByte(0);
                w.WriteVarInt(0);
                w.WriteString("");
                w.WriteString("");
            })));
            Assert.Throws<ProtocolException>(() => InfoMessage.Read(RoundTrip(w =>
            {
                w.WriteByte(3);
                w.WriteByte(3);
                w.WriteVarInt(0);
                w.WriteString("");
                w.WriteString("");
            })));
        }

        [Fact]
        public void Info_CrouchedWalk_RoundTripsAndReadsCrouching()
        {
            var info = new InfoMessage { Scene = 3, ControlMode = InfoMessage.Crouch, SelectedBall = -1, Club = "", Activity = "" };
            var read = InfoMessage.Read(RoundTrip(info.Write));
            Assert.Equal(InfoMessage.Crouch, read.ControlMode);
            Assert.Equal("Crouching", PresenceText.ControlMode(read.ControlMode));
            Assert.Equal("Walking", PresenceText.ControlMode(InfoMessage.Walk));
            Assert.Equal("Golfing", PresenceText.ControlMode(InfoMessage.Golf));
        }

        [Fact]
        public void Info_Look_RoundTripsAndChangesEquality()
        {
            var male = new InfoMessage { Scene = 3, SelectedBall = -1, Club = "", Activity = "" };
            var female = male;
            female.Look = PlayerLook.Female;
            Assert.Equal(PlayerLook.Female, InfoMessage.Read(RoundTrip(female.Write)).Look);
            Assert.Equal(PlayerLook.Male, InfoMessage.Read(RoundTrip(male.Write)).Look);
            Assert.False(male.Equals(female));
        }

        [Fact]
        public void Info_WithoutLook_IsMale_AndAnUnknownLookReadsAsMale()
        {
            Action<NetWriter> older = w =>
            {
                w.WriteByte(3);
                w.WriteByte(0);
                w.WriteVarInt(2);
                w.WriteString("Iron");
                w.WriteString("");
            };
            var read = InfoMessage.Read(RoundTrip(older));
            Assert.Equal(PlayerLook.Male, read.Look);
            Assert.Equal("Iron", read.Club);
            var newer = InfoMessage.Read(RoundTrip(w =>
            {
                older(w);
                w.WriteByte(7);
            }));
            Assert.Equal(PlayerLook.Male, newer.Look);
            Assert.Equal(2, newer.SelectedBall);
        }

        [Fact]
        public void Info_RejectsOverlongClubFromTheWire()
        {
            var reader = RoundTrip(w =>
            {
                w.WriteByte(3);
                w.WriteByte(0);
                w.WriteVarInt(0);
                w.WriteString(new string('c', 40));
                w.WriteString("");
            });
            Assert.False(PresenceWire.IsValid(reader, r => InfoMessage.Read(r)));
        }

        [Fact]
        public void Truncate_KeepsWholeUtf8Characters()
        {
            string text = new string('a', 31) + "éé";
            string cut = PresenceWire.Truncate(text, 32);
            Assert.Equal(new string('a', 31), cut);
            Assert.Equal("😀", PresenceWire.Truncate("😀😀", 4));
            Assert.Equal(string.Empty, PresenceWire.Truncate(null, 10));
        }

        [Fact]
        public void Shot_RoundTrips_AndRejectsNaN()
        {
            var shot = new ShotMessage
            {
                Club = "Driver",
                Power = 0.82f,
                ShotType = 64,
                SpinType = 8,
                Stroke = 2,
                IsMiss = false,
                From = new NetVector3(1, 2, 3)
            };
            var read = ShotMessage.Read(RoundTrip(shot.Write));
            Assert.Equal("Driver", read.Club);
            Assert.Equal(0.82f, read.Power);
            Assert.Equal(64, read.ShotType);
            Assert.Equal(2u, read.Stroke);
            Assert.Equal(new NetVector3(1, 2, 3), read.From);

            shot.Power = float.NaN;
            Assert.False(PresenceWire.IsValid(RoundTrip(shot.Write), r => ShotMessage.Read(r)));
            shot.Power = 1f;
            shot.From = new NetVector3(float.PositiveInfinity, 0, 0);
            Assert.False(PresenceWire.IsValid(RoundTrip(shot.Write), r => ShotMessage.Read(r)));
        }

        [Fact]
        public void Rest_RoundTrips()
        {
            var rest = new RestMessage { Position = new NetVector3(5, 0, -5), Distance = 143.4f, Surface = "Fairway", Stroke = 3 };
            var read = RestMessage.Read(RoundTrip(rest.Write));
            Assert.Equal(143.4f, read.Distance);
            Assert.Equal("Fairway", read.Surface);
            Assert.Equal(3u, read.Stroke);
        }

        [Fact]
        public void Holed_ClampsOnWrite_AndChecksRangesOnRead()
        {
            var holed = new HoledMessage { Par = 4, Strokes = 3, LmugcHole = 12 };
            var read = HoledMessage.Read(RoundTrip(holed.Write));
            Assert.Equal(4, read.Par);
            Assert.Equal(3u, read.Strokes);
            Assert.Equal(-1, read.LmugcHole);

            Assert.False(PresenceWire.IsValid(RoundTrip(w =>
            {
                w.WriteVarInt(4);
                w.WriteVarUInt(3);
                w.WriteVarInt(10);
            }), r => HoledMessage.Read(r)));
        }

        [Fact]
        public void Wind_RejectsHugeValues()
        {
            var wind = new WindMessage { CurrentX = 1, CurrentY = 2, DesiredX = 3, DesiredY = 4, HostFree = true };
            var read = WindMessage.Read(RoundTrip(wind.Write));
            Assert.Equal(4f, read.DesiredY);
            Assert.True(read.HostFree);
            wind.CurrentX = 1e9f;
            Assert.False(PresenceWire.IsValid(RoundTrip(wind.Write), r => WindMessage.Read(r)));
        }

        [Fact]
        public void Round_AndHole_ValidateIdsAndRanges()
        {
            var round = new RoundMessage { RoundId = 7, Event = RoundEvent.Finished, Total = 38 };
            var read = RoundMessage.Read(RoundTrip(round.Write));
            Assert.Equal(RoundEvent.Finished, read.Event);
            Assert.Equal(38, read.Total);
            round.RoundId = 0;
            Assert.False(PresenceWire.IsValid(RoundTrip(round.Write), r => RoundMessage.Read(r)));

            var hole = new HoleMessage { RoundId = 7, Hole = 9, Strokes = 5, Par = 4 };
            var holeRead = HoleMessage.Read(RoundTrip(hole.Write));
            Assert.Equal(9, holeRead.Hole);
            Assert.False(PresenceWire.IsValid(RoundTrip(w =>
            {
                w.WriteVarUInt(7);
                w.WriteByte(10);
                w.WriteVarInt(5);
                w.WriteVarInt(4);
            }), r => HoleMessage.Read(r)));
        }

        [Fact]
        public void Card_RoundTrips_AndBoundsRows()
        {
            var rows = new List<CardRow>
            {
                new CardRow { Peer = new PeerId(11), RoundId = 5, State = RoundEvent.Start, Strokes = new[] { 4, 3, 0, 0, 0, 0, 0, 0, 0 } },
                new CardRow { Peer = new PeerId(12), RoundId = 6, State = RoundEvent.Finished, Strokes = new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 } }
            };
            var read = CardMessage.Read(RoundTrip(w => CardMessage.Write(w, rows)));
            Assert.Equal(2, read.Count);
            Assert.Equal(new PeerId(12), read[1].Peer);
            Assert.Equal(RoundEvent.Finished, read[1].State);
            Assert.Equal(9, read[1].Strokes[8]);

            Assert.False(PresenceWire.IsValid(RoundTrip(w => w.WriteVarUInt(1000)), r => CardMessage.Read(r)));
            Assert.False(PresenceWire.IsValid(RoundTrip(w =>
            {
                w.WriteVarUInt(1);
                w.WriteULong(0);
                w.WriteVarUInt(1);
                w.WriteByte(0);
            }), r => CardMessage.Read(r)));
        }

        [Fact]
        public void TruncatedPayloads_AreInvalid()
        {
            var shot = new ShotMessage { Club = "Iron", Power = 0.5f, Stroke = 1 };
            var writer = new NetWriter();
            shot.Write(writer);
            var bytes = writer.ToArray();
            for (int length = 0; length < bytes.Length; length++)
            {
                var reader = new NetReader(bytes, 0, length);
                Assert.False(PresenceWire.IsValid(reader, r => ShotMessage.Read(r)));
            }
        }
    }
}
#endif
