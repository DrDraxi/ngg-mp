using System;
using NGGMultiplayer.Core.Diagnostics;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Sync;
using NGGMultiplayer.Game;
using UnityEngine;
using Object = UnityEngine.Object;

namespace NGGMultiplayer.Runtime
{
    /// <summary>
    /// Shows a remotely owned entity: a root object (kept across scene loads) moved to the interpolated pose,
    /// holding the game's visual or a placeholder. Hidden while the owner reports no pose or while
    /// <see cref="WorldVisible"/> is off. A visual with an <see cref="IRemoteVisual"/> component hears every pose.
    /// </summary>
    public abstract class RemoteEntityView : INetEntity
    {
        private readonly GameObject _root;
        private readonly Transform _transform;
        private IRemoteVisual _remoteVisual;
        private bool _visible;
        private bool _destroyed;

        protected RemoteEntityView(string name, GameObject visual, Transform parent)
        {
            _root = new GameObject(name);
            _transform = _root.transform;
            if (parent != null)
                _transform.SetParent(parent, false);
            else
                Object.DontDestroyOnLoad(_root);
            if (visual != null)
            {
                visual.transform.SetParent(_transform, false);
                _remoteVisual = visual.GetComponent<IRemoteVisual>();
            }
            _root.SetActive(false);
        }

        /// <summary>
        /// Off while the local game is not in a world remote players can be in (menus, loading); every remote view
        /// then hides on its next update. Set by the game context.
        /// </summary>
        public static bool WorldVisible { get; set; } = true;

        public GameObject Root => _root;

        protected Transform Transform => _transform;

        public bool CaptureState(out TransformState state)
        {
            // Only reached if ownership ever moved to us; player-bound entities never do.
            state = default;
            return false;
        }

        public void ApplyState(in TransformState state, SnapshotSampleKind kind)
        {
            if (_destroyed || _root == null || kind == SnapshotSampleKind.None)
                return;
            // Core bounds every state it accepts, but never hand Unity a non-finite pose.
            if (!state.Position.IsFinite || !state.Rotation.IsFinite)
                return;
            bool visible = WorldVisible && PoseConversion.IsVisible(state);
            if (visible != _visible)
            {
                _visible = visible;
                _root.SetActive(visible);
            }
            if (!visible)
                return;
            _transform.SetPositionAndRotation(PoseConversion.ToUnity(state.Position), PoseConversion.ToUnity(state.Rotation));
            OnPoseFlags(state.Flags);
            if (_remoteVisual == null)
                return;
            try
            {
                _remoteVisual.OnRemotePose(state.Flags, PoseConversion.ToUnity(state.Velocity), state.Teleport);
            }
            catch (Exception e)
            {
                // A broken visual must not take pose updates down with it; it stops hearing poses.
                NetLog.Error($"[Entities] Remote visual of {_root.name} threw; it no longer gets poses: {e}");
                _remoteVisual = null;
            }
        }

        /// <summary>The flags of each pose shown (after the root has moved there).</summary>
        protected virtual void OnPoseFlags(byte flags)
        {
        }

        public virtual void OnOwnershipChanged(PeerId owner, bool isLocal)
        {
            if (isLocal)
                SetHidden();
        }

        public void OnDespawned(DespawnReason reason)
        {
            if (_destroyed)
                return;
            _destroyed = true;
            if (_root != null)
                Object.Destroy(_root);
        }

        private void SetHidden()
        {
            _visible = false;
            if (_root != null)
                _root.SetActive(false);
        }

        /// <summary>A collider-free primitive tinted with <paramref name="color"/>.</summary>
        protected static GameObject CreatePlaceholder(PrimitiveType type, Vector3 scale, Vector3 offset, Color color)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = "Placeholder";
            var collider = go.GetComponent<Collider>();
            if (collider != null)
                Object.Destroy(collider);
            go.transform.localScale = scale;
            go.transform.localPosition = offset;
            var renderer = go.GetComponent<Renderer>();
            if (renderer != null && renderer.material != null)
                renderer.material.color = color;
            return go;
        }

        /// <summary>The player's colour (avatar tint, name tag, ghost trail).</summary>
        protected static Color PlayerColor(int playerIndex) => Game.Presence.PlayerColors.For(playerIndex);
    }
}
