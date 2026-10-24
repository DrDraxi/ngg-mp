using NGGMultiplayer.Core.Session;
using UnityEngine;

namespace NGGMultiplayer.Runtime
{
    /// <summary>Another player's ball.</summary>
    public sealed class RemoteBallView : RemoteEntityView
    {
        // Regulation golf ball diameter in metres; the placeholder until the game's ball prefab is known.
        private const float BallDiameter = 0.0427f;

        public RemoteBallView(PlayerInfo player, GameObject visual, Transform parent)
            : base("NGGMP Ball " + (player?.DisplayName ?? "?"), visual ?? CreateDefaultVisual(player), parent)
        {
        }

        private static GameObject CreateDefaultVisual(PlayerInfo player) =>
            CreatePlaceholder(PrimitiveType.Sphere, Vector3.one * BallDiameter, Vector3.zero,
                Color.Lerp(Color.white, PlayerColor(player?.PlayerIndex ?? 0), 0.5f));
    }
}
