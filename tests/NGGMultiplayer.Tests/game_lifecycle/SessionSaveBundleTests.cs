using System;
using System.Text;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Game;
using NGGMultiplayer.Game.Lifecycle;
using Xunit;

namespace NGGMultiplayer.Tests.GameLifecycle
{
    public class SessionSaveBundleTests
    {
        private static SessionSaveBundle Sample()
        {
            var facts = new FactTable();
            facts.Set("bo", "BO_Glass", new byte[] { 1 });
            facts.Set("bank", "", FactTable.EncodeInt(1234));
            return new SessionSaveBundle
            {
                HostScene = GameScene.Main,
                SceneEpoch = 7,
                Mode = SessionSaveBundle.ModeJustGolf,
                HasRun = true,
                RunJson = SessionSaveBundle.Encode("{\"m_bankBalance\":1234,\"name\":\"Übung\"}"),
                UnlocksJson = SessionSaveBundle.Encode("{\"selectedBall\":2}"),
                ScoreCardJson = SessionSaveBundle.Encode("{\"scores\":[]}"),
                Facts = facts.ToArray()
            };
        }

        [Fact]
        public void RoundTrip_KeepsEveryField()
        {
            var original = Sample();
            var parsed = SessionSaveBundle.Parse(original.Serialize());
            Assert.Equal(GameScene.Main, parsed.HostScene);
            Assert.Equal(7u, parsed.SceneEpoch);
            Assert.True(parsed.IsJustGolf);
            Assert.True(parsed.HasRun);
            Assert.Equal("{\"m_bankBalance\":1234,\"name\":\"Übung\"}", parsed.RunJsonText);
            Assert.Equal("{\"selectedBall\":2}", parsed.UnlocksJsonText);
            Assert.Equal("{\"scores\":[]}", parsed.ScoreCardJsonText);
            Assert.Equal(2, parsed.FactTable.Count);
            Assert.Equal(1234, parsed.FactTable.GetInt("bank", ""));
        }

        [Fact]
        public void RoundTrip_WithoutRun()
        {
            var bundle = new SessionSaveBundle
            {
                HostScene = GameScene.Credits,
                SceneEpoch = 1,
                Mode = SessionSaveBundle.ModeStory,
                HasRun = false,
                Facts = new FactTable().ToArray()
            };
            var parsed = SessionSaveBundle.Parse(bundle.Serialize());
            Assert.False(parsed.HasRun);
            Assert.Empty(parsed.RunJson);
            Assert.Equal(string.Empty, parsed.RunJsonText);
            Assert.Equal(0, parsed.FactTable.Count);
        }

        [Fact]
        public void Serialize_DropsARunWhenThereIsNone()
        {
            var bundle = Sample();
            bundle.HasRun = false;
            var parsed = SessionSaveBundle.Parse(bundle.Serialize());
            Assert.Empty(parsed.RunJson);
        }

        [Fact]
        public void Format_IsAsSpecified()
        {
            var bundle = Sample();
            var reader = new NetReader(bundle.Serialize());
            Assert.Equal(1u, reader.ReadVarUInt());
            Assert.Equal((byte)GameScene.Main, reader.ReadByte());
            Assert.Equal(7u, reader.ReadVarUInt());
            Assert.Equal((byte)1, reader.ReadByte());
            Assert.True(reader.ReadBool());
            Assert.Equal(bundle.RunJson, reader.ReadBytes());
            Assert.Equal(bundle.UnlocksJson, reader.ReadBytes());
            Assert.Equal(bundle.ScoreCardJson, reader.ReadBytes());
            Assert.Equal(bundle.Facts, reader.ReadBytes());
            Assert.True(reader.IsAtEnd);
        }

        [Fact]
        public void Parse_RejectsEveryTruncation()
        {
            var data = Sample().Serialize();
            for (int length = 0; length < data.Length; length++)
            {
                var cut = new byte[length];
                Array.Copy(data, cut, length);
                Assert.Throws<ProtocolException>(() => SessionSaveBundle.Parse(cut));
            }
        }

        [Fact]
        public void Parse_RejectsTrailingBytes()
        {
            var data = Sample().Serialize();
            var longer = new byte[data.Length + 1];
            Array.Copy(data, longer, data.Length);
            Assert.Throws<ProtocolException>(() => SessionSaveBundle.Parse(longer));
        }

        [Fact]
        public void Parse_RejectsUnknownFormatSceneAndMode()
        {
            Assert.Throws<ProtocolException>(() => SessionSaveBundle.Parse(Patch(0, 2)));
            Assert.Throws<ProtocolException>(() => SessionSaveBundle.Parse(Patch(1, 99)));
            // Offset of the mode byte: format (1), scene (1), epoch 7 (1).
            Assert.Throws<ProtocolException>(() => SessionSaveBundle.Parse(Patch(3, 2)));
            Assert.Throws<ProtocolException>(() => SessionSaveBundle.Parse(null));
        }

        [Fact]
        public void Parse_RejectsARunWithoutHasRun()
        {
            var writer = new NetWriter();
            writer.WriteVarUInt(1);
            writer.WriteByte((byte)GameScene.Main);
            writer.WriteVarUInt(1);
            writer.WriteByte(0);
            writer.WriteBool(false);
            writer.WriteBytes(Encoding.UTF8.GetBytes("{}"));
            writer.WriteBytes(Array.Empty<byte>());
            writer.WriteBytes(Array.Empty<byte>());
            writer.WriteBytes(new FactTable().ToArray());
            Assert.Throws<ProtocolException>(() => SessionSaveBundle.Parse(writer.ToArray()));
        }

        [Fact]
        public void Parse_RejectsInvalidUtf8AndBadFacts()
        {
            var bundle = Sample();
            bundle.RunJson = new byte[] { 0x7B, 0xC3, 0x28, 0x7D };
            Assert.Throws<ProtocolException>(() => SessionSaveBundle.Parse(bundle.Serialize()));

            bundle = Sample();
            bundle.Facts = new byte[] { 9, 0 };
            Assert.Throws<ProtocolException>(() => SessionSaveBundle.Parse(bundle.Serialize()));
        }

        [Fact]
        public void Serialize_RefusesOversizedFieldsAndUnknownModes()
        {
            var bundle = Sample();
            bundle.UnlocksJson = new byte[SessionSaveBundle.MaxFieldBytes + 1];
            Assert.Throws<InvalidOperationException>(() => bundle.Serialize());

            bundle = Sample();
            bundle.Mode = 5;
            Assert.Throws<InvalidOperationException>(() => bundle.Serialize());
        }

        [Fact]
        public void CloneSave_CopiesTheSaveButNotTheFacts()
        {
            var bundle = Sample();
            var clone = bundle.CloneSave();
            Assert.Equal(bundle.HostScene, clone.HostScene);
            Assert.Equal(bundle.SceneEpoch, clone.SceneEpoch);
            Assert.Equal(bundle.Mode, clone.Mode);
            Assert.Equal(bundle.RunJson, clone.RunJson);
            Assert.Empty(clone.Facts);
        }

        private static byte[] Patch(int offset, byte value)
        {
            var data = Sample().Serialize();
            data[offset] = value;
            return data;
        }
    }
}
