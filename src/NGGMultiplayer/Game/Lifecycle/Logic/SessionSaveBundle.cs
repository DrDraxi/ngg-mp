using System;
using System.Text;
using NGGMultiplayer.Core.Protocol;

namespace NGGMultiplayer.Game.Lifecycle
{
    /// <summary>
    /// The session save: the host's save files plus the session's facts, as Core's save sync carries it
    /// (docs/GAME_INTEGRATION.md 5.1). Layout:
    /// <code>
    /// vu format = 1, b hostScene, vu sceneEpoch, b mode, bool hasRun,
    /// bytes runJson (empty when !hasRun), bytes unlocksJson, bytes scoreCardJson, bytes facts
    /// </code>
    /// <see cref="Parse"/> validates everything (UTF-8, the fact table, trailing bytes) before returning, so a bad
    /// bundle is rejected before any game state is touched. No Unity types (unit tested).
    /// </summary>
    internal sealed class SessionSaveBundle
    {
        public const uint Format = 1;

        /// <summary>Cap for each embedded field.</summary>
        public const int MaxFieldBytes = 4 * 1024 * 1024;

        public const byte ModeStory = 0;
        public const byte ModeJustGolf = 1;

        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public GameScene HostScene { get; set; }

        public uint SceneEpoch { get; set; }

        /// <summary>(byte)GameMode: 0 Story, 1 JustGolf.</summary>
        public byte Mode { get; set; }

        public bool HasRun { get; set; }

        /// <summary>UTF-8 JsonUtility JSON of the host's RunSave; empty without a run.</summary>
        public byte[] RunJson { get; set; } = Array.Empty<byte>();

        public byte[] UnlocksJson { get; set; } = Array.Empty<byte>();

        public byte[] ScoreCardJson { get; set; } = Array.Empty<byte>();

        /// <summary><see cref="FactTable.ToArray"/> of the session's facts.</summary>
        public byte[] Facts { get; set; } = Array.Empty<byte>();

        /// <summary>The decoded <see cref="Facts"/>; set by <see cref="Parse"/>.</summary>
        public FactTable FactTable { get; private set; }

        public bool IsJustGolf => Mode == ModeJustGolf;

        public string RunJsonText => Decode(RunJson);

        public string UnlocksJsonText => Decode(UnlocksJson);

        public string ScoreCardJsonText => Decode(ScoreCardJson);

        public static byte[] Encode(string json) => string.IsNullOrEmpty(json) ? Array.Empty<byte>() : StrictUtf8.GetBytes(json);

        public byte[] Serialize()
        {
            var run = HasRun ? RunJson ?? Array.Empty<byte>() : Array.Empty<byte>();
            var unlocks = UnlocksJson ?? Array.Empty<byte>();
            var scoreCard = ScoreCardJson ?? Array.Empty<byte>();
            var facts = Facts ?? Array.Empty<byte>();
            CheckField(run, "runJson");
            CheckField(unlocks, "unlocksJson");
            CheckField(scoreCard, "scoreCardJson");
            CheckField(facts, "facts");
            if (Mode > ModeJustGolf)
                throw new InvalidOperationException($"Unknown game mode {Mode}.");

            var writer = new NetWriter(64 + run.Length + unlocks.Length + scoreCard.Length + facts.Length);
            writer.WriteVarUInt(Format);
            writer.WriteByte((byte)HostScene);
            writer.WriteVarUInt(SceneEpoch);
            writer.WriteByte(Mode);
            writer.WriteBool(HasRun);
            writer.WriteBytes(run);
            writer.WriteBytes(unlocks);
            writer.WriteBytes(scoreCard);
            writer.WriteBytes(facts);
            return writer.ToArray();
        }

        /// <exception cref="ProtocolException">The bundle is malformed in any way.</exception>
        public static SessionSaveBundle Parse(byte[] data)
        {
            if (data == null)
                throw new ProtocolException("Session save bundle is missing.");
            var reader = new NetReader(data);
            uint format = reader.ReadVarUInt();
            if (format != Format)
                throw new ProtocolException($"Unsupported session save format {format} (expected {Format}).");
            byte scene = reader.ReadByte();
            if (scene > (byte)GameScene.Other)
                throw new ProtocolException($"Unknown host scene {scene}.");
            uint epoch = reader.ReadVarUInt();
            byte mode = reader.ReadByte();
            if (mode > ModeJustGolf)
                throw new ProtocolException($"Unknown game mode {mode}.");
            bool hasRun = reader.ReadBool();
            var bundle = new SessionSaveBundle
            {
                HostScene = (GameScene)scene,
                SceneEpoch = epoch,
                Mode = mode,
                HasRun = hasRun,
                RunJson = reader.ReadBytes(MaxFieldBytes),
                UnlocksJson = reader.ReadBytes(MaxFieldBytes),
                ScoreCardJson = reader.ReadBytes(MaxFieldBytes),
                Facts = reader.ReadBytes(MaxFieldBytes)
            };
            if (!reader.IsAtEnd)
                throw new ProtocolException($"Session save bundle has {reader.Remaining} trailing bytes.");
            if (!hasRun && bundle.RunJson.Length > 0)
                throw new ProtocolException("Session save bundle carries a run but says it has none.");
            Validate(bundle.RunJson, "runJson");
            Validate(bundle.UnlocksJson, "unlocksJson");
            Validate(bundle.ScoreCardJson, "scoreCardJson");
            bundle.FactTable = FactTable.FromArray(bundle.Facts);
            return bundle;
        }

        /// <summary>A copy of everything but the facts (the host's cached last settled save).</summary>
        public SessionSaveBundle CloneSave() => new SessionSaveBundle
        {
            HostScene = HostScene,
            SceneEpoch = SceneEpoch,
            Mode = Mode,
            HasRun = HasRun,
            RunJson = RunJson,
            UnlocksJson = UnlocksJson,
            ScoreCardJson = ScoreCardJson
        };

        private static void CheckField(byte[] value, string name)
        {
            if (value.Length > MaxFieldBytes)
                throw new InvalidOperationException($"Session save field {name} is {value.Length} bytes (limit {MaxFieldBytes}).");
        }

        private static void Validate(byte[] utf8, string name)
        {
            if (utf8.Length == 0)
                return;
            try
            {
                StrictUtf8.GetCharCount(utf8);
            }
            catch (DecoderFallbackException e)
            {
                throw new ProtocolException($"Session save field {name} is not valid UTF-8.", e);
            }
        }

        private static string Decode(byte[] utf8) => utf8 == null || utf8.Length == 0 ? string.Empty : StrictUtf8.GetString(utf8);
    }
}
