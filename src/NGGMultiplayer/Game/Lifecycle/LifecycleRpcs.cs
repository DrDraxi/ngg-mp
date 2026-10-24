using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Sync;

namespace NGGMultiplayer.Game.Lifecycle
{
    /// <summary>The Lifecycle module's named RPCs (docs/GAME_INTEGRATION.md 2.2).</summary>
    internal static class LifecycleRpcs
    {
        /// <summary>host -> Others, reliable: <c>b scene, vu epoch, b mode</c>. The host started loading a scene.</summary>
        public const string Scene = GameMessages.LifecyclePrefix + "scene";

        public static void WriteScene(NetWriter writer, SceneNotice notice)
        {
            writer.WriteByte((byte)notice.Scene);
            writer.WriteVarUInt(notice.Epoch);
            writer.WriteByte(notice.Mode);
        }

        /// <exception cref="ProtocolException">Malformed payload.</exception>
        public static SceneNotice ReadScene(NetReader reader)
        {
            byte scene = reader.ReadByte();
            if (scene > (byte)GameScene.Other)
                throw new ProtocolException($"Unknown scene {scene}.");
            uint epoch = reader.ReadVarUInt();
            byte mode = reader.ReadByte();
            if (mode > SessionSaveBundle.ModeJustGolf)
                throw new ProtocolException($"Unknown game mode {mode}.");
            return new SceneNotice((GameScene)scene, epoch, mode);
        }

        /// <summary>Validator for host-only RPCs: the host refuses every client-sent call.</summary>
        public static bool HostOnly(PeerId sender, RpcTarget target, PeerId targetPeer, NetReader args) => false;
    }

    internal readonly struct SceneNotice
    {
        public SceneNotice(GameScene scene, uint epoch, byte mode)
        {
            Scene = scene;
            Epoch = epoch;
            Mode = mode;
        }

        public GameScene Scene { get; }

        public uint Epoch { get; }

        public byte Mode { get; }
    }
}
