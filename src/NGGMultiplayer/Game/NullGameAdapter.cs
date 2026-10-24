using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Save;
using NGGMultiplayer.Core.Session;
using UnityEngine;

namespace NGGMultiplayer.Game
{
    /// <summary>
    /// Game-independent fallback, used when <see cref="NggGameAdapter"/> cannot be created (the game's assemblies do
    /// not match this build): the local "player" is the main camera, there is no ball, remote players use
    /// placeholder visuals and progress lives in memory (<see cref="GameSaveBackend"/>).
    /// </summary>
    public sealed class NullGameAdapter : IGameAdapter
    {
        private readonly GameSaveBackend _save = new GameSaveBackend();
        private Camera _camera;

        public NullGameAdapter()
        {
            GameBuild = Application.version ?? string.Empty;
        }

        public string GameBuild { get; }

        public ISaveBackend SaveBackend => _save;

        public bool TryGetLocalPlayerPose(out GamePose pose)
        {
            // Camera.main does a tagged lookup; cache it until it is destroyed (scene change).
            if (_camera == null || !_camera.isActiveAndEnabled)
                _camera = Camera.main;
            if (_camera == null)
            {
                pose = default;
                return false;
            }
            var t = _camera.transform;
            pose = new GamePose(t.position, t.rotation);
            return true;
        }

        public bool TryGetLocalBallPose(out GamePose pose)
        {
            pose = default;
            return false;
        }

        public GameObject CreateRemotePlayerVisual(PlayerInfo player) => null;

        public GameObject CreateRemoteBallVisual(PlayerInfo player) => null;

        public bool ValidateClaim(PeerId from, SaveDelta claim) => true;

        public void OnSessionReady(bool isHost)
        {
        }

        public void OnSessionEnded()
        {
        }

        public void Dispose()
        {
            _camera = null;
        }
    }
}
