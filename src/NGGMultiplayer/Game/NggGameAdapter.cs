using System;
using System.Collections.Generic;
using HarmonyLib;
using NGGMultiplayer.Core.Diagnostics;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Save;
using NGGMultiplayer.Core.Session;
using NGGMultiplayer.Game.Lifecycle;
using NGGMultiplayer.Game.Presence;
using NGGMultiplayer.Game.Progression;
using NGGMultiplayer.Runtime;
using UnityEngine;

namespace NGGMultiplayer.Game
{
    /// <summary>
    /// The Normal Golf Game integration behind <see cref="IGameAdapter"/>. It owns the <see cref="GameContext"/> and
    /// the three modules (Lifecycle, Presence, Progression), forwards the adapter calls to the providers the
    /// modules install, and hands Core the <see cref="GameSaveBridge"/> as its save backend.
    /// <para>Created before the service (the service needs <see cref="SaveBackend"/>); <see cref="Attach"/> runs once
    /// the service, Harmony and the plugin's persistent object exist, and installs the modules.</para>
    /// </summary>
    public sealed class NggGameAdapter : IGameAdapter
    {
        private const string Log = "[Game] ";

        private readonly List<IGameModule> _modules = new List<IGameModule>();
        private readonly List<IGameModule> _installed = new List<IGameModule>();
        private readonly GameContext _context;
        private bool _disposed;

        public NggGameAdapter()
        {
            GameBuild = Application.version ?? string.Empty;
            _modules.Add(new LifecycleSaveModule());
            _modules.Add(new PresenceModule());
            _modules.Add(new ProgressionModule());
            _modules.Add(new Menus.MenusModule());
            _context = new GameContext(_modules);
            // The game: a client must never join without the Lifecycle module protecting its save.
            _context.Save.RequireStoreToJoin = true;
            LobbyInfo = new GameLobbyInfo(_context);
        }

        public string GameBuild { get; }

        public ISaveBackend SaveBackend => _context.Save;

        public GameContext Context => _context;

        /// <summary>The game's lines for the lobby window (host's scene, players' whereabouts, join warnings).</summary>
        public ILobbyGameInfo LobbyInfo { get; }

        public IReadOnlyList<IGameModule> Modules => _installed;

        /// <summary>Wires the context to the running plugin and installs every module. Call once.</summary>
        public void Attach(IMultiplayerService service, Harmony harmony, GameObject host, Action showLobbyWindow,
            Func<string> lobbyKeyText = null, Func<KeyCode> scorecardKey = null, Func<bool> remoteBallTrail = null,
            Func<float> trailSeconds = null)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(NggGameAdapter));
            if (host == null)
                throw new ArgumentNullException(nameof(host));
            var driver = host.AddComponent<GameDriver>();
            driver.Initialize(_context);
            _context.Attach(service, harmony, driver, showLobbyWindow, lobbyKeyText, scorecardKey, remoteBallTrail, trailSeconds);

            foreach (var module in _modules)
            {
                try
                {
                    module.Install(_context);
                    _installed.Add(module);
                    NetLog.Info($"{Log}{module.Name} installed.");
                }
                catch (Exception e)
                {
                    NetLog.Error($"{Log}{module.Name} failed to install and is disabled: {e}");
                    SafeUninstall(module);
                }
            }
            if (_context.Patcher.DisabledFeatures.Count > 0)
            {
                NetLog.Warn($"{Log}{_context.Patcher.DisabledFeatures.Count} game feature(s) are disabled; " +
                    "see the 'Feature ... disabled' lines above.");
            }
        }

        public bool TryGetLocalPlayerPose(out GamePose pose)
        {
            var presence = _context.Presence;
            if (presence != null)
            {
                try
                {
                    return presence.TryGetLocalPlayerPose(out pose);
                }
                catch (Exception e)
                {
                    _context.ErrorOnce("pose.player", $"{Log}Reading the local player's pose failed: {e}");
                }
            }
            pose = default;
            return false;
        }

        public bool TryGetLocalBallPose(out GamePose pose)
        {
            var presence = _context.Presence;
            if (presence != null)
            {
                try
                {
                    return presence.TryGetLocalBallPose(out pose);
                }
                catch (Exception e)
                {
                    _context.ErrorOnce("pose.ball", $"{Log}Reading the local ball's pose failed: {e}");
                }
            }
            pose = default;
            return false;
        }

        public GameObject CreateRemotePlayerVisual(PlayerInfo player) => _context.Presence?.CreateRemotePlayerVisual(player);

        public GameObject CreateRemoteBallVisual(PlayerInfo player) => _context.Presence?.CreateRemoteBallVisual(player);

        public bool ValidateClaim(PeerId from, SaveDelta claim) => _context.Save.ValidateClaim(from, claim);

        public void OnSessionReady(bool isHost) => _context.HandleSessionReady(isHost);

        public void OnSessionEnded() => _context.HandleSessionEnded();

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            for (int i = _installed.Count - 1; i >= 0; i--)
                SafeUninstall(_installed[i]);
            _installed.Clear();
            _context.Dispose();
        }

        private static void SafeUninstall(IGameModule module)
        {
            try
            {
                module.Uninstall();
            }
            catch (Exception e)
            {
                NetLog.Error($"{Log}{module.Name}.Uninstall threw: {e}");
            }
        }
    }
}
