using System;
using NGGMultiplayer.Core;
using NGGMultiplayer.Core.Diagnostics;
using NGGMultiplayer.Core.Session;
using NGGMultiplayer.Core.Sync;
using NGGMultiplayer.Game;
using UnityEngine;

namespace NGGMultiplayer.Runtime
{
    /// <summary>
    /// Builds the game-side objects for the plugin's entities: the local player's own entities read their pose
    /// from <see cref="IGameAdapter"/>; everyone else's become <see cref="RemotePlayerView"/> /
    /// <see cref="RemoteBallView"/>.
    /// </summary>
    public sealed class PlayerEntityFactory : INetEntityFactory
    {
        private readonly IGameAdapter _adapter;
        private readonly Func<SessionManager> _session;
        private Transform _remoteRoot;

        public PlayerEntityFactory(IGameAdapter adapter, Func<SessionManager> session)
        {
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
            _session = session ?? throw new ArgumentNullException(nameof(session));
        }

        public INetEntity CreateEntity(NetEntity entity)
        {
            bool isPlayer = entity.PrefabKey == GameEntityKeys.Player;
            if (!isPlayer && entity.PrefabKey != GameEntityKeys.Ball)
            {
                NetLog.Warn($"[Entities] No game object for unknown prefab '{entity.PrefabKey}' ({entity.Id}).");
                return null;
            }
            if (entity.IsLocallyOwned)
                return new LocalPoseEntity(_adapter, isPlayer ? PoseSource.Player : PoseSource.Ball);

            PlayerInfo player = null;
            var session = _session();
            if (PlayerEntitySpawner.TryReadPlayer(entity, out var playerId) && session != null)
                session.TryGetPlayer(playerId, out player);

            GameObject visual = null;
            try
            {
                visual = isPlayer ? _adapter.CreateRemotePlayerVisual(player) : _adapter.CreateRemoteBallVisual(player);
            }
            catch (Exception e)
            {
                NetLog.Error($"[Entities] Game adapter failed to create a visual for {entity}: {e}");
            }
            var root = RemoteRoot();
            return isPlayer ? (INetEntity)new RemotePlayerView(player, visual, root) : new RemoteBallView(player, visual, root);
        }

        /// <summary>Destroys the container of remote views (they are normally destroyed one by one on despawn).</summary>
        public void DestroyViews()
        {
            if (_remoteRoot != null)
                UnityEngine.Object.Destroy(_remoteRoot.gameObject);
            _remoteRoot = null;
        }

        private Transform RemoteRoot()
        {
            if (_remoteRoot == null)
            {
                var go = new GameObject("NGGMultiplayer Remotes");
                UnityEngine.Object.DontDestroyOnLoad(go);
                _remoteRoot = go.transform;
            }
            return _remoteRoot;
        }
    }
}
