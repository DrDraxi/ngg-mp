using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using NGGMultiplayer.Core.Diagnostics;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Game;
using NGGMultiplayer.Game.Patches;
using NGGMultiplayer.Runtime;
using NGGMultiplayer.Steam;
using NGGMultiplayer.UI;
using UnityEngine;

namespace NGGMultiplayer
{
    [BepInPlugin(Guid, Name, ProtocolInfo.ModVersion)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "ngg.multiplayer";
        public const string Name = "NGG Multiplayer";

        private static BepInEx.Logging.ManualLogSource _log;

        private PluginConfig _config;
        private IGameAdapter _adapter;
        private IMultiplayerService _service;
        private Harmony _harmony;
        private GameObject _host;

        /// <summary>The running service, for other mods and debugging. Null before Awake.</summary>
        public static IMultiplayerService Service { get; private set; }

        private void Awake()
        {
            _log = Logger;
            NetLog.Sink = WriteLog;
            _config = new PluginConfig(Config);
            NetLog.MinLevel = _config.LogLevel.Value;

            try
            {
                _adapter = CreateGameAdapter();
                _harmony = GamePatches.Apply(Guid);
                InstallSaveDirOverride(_harmony, _config.SaveDirOverride.Value);
                _service = _config.Transport.Value == TransportKind.Direct
                    ? new DirectMultiplayerService(_adapter, _config.ToSettings())
                    : CreateServiceOrFallback(_adapter, _config.ToSettings());
                Service = _service;
                Config.SettingChanged += OnSettingChanged;

                _host = new GameObject("NGGMultiplayer");
                _host.hideFlags = HideFlags.HideAndDontSave;
                DontDestroyOnLoad(_host);
                _host.AddComponent<NetRunner>().Initialize(_service);
                var lobbyWindow = _host.AddComponent<LobbyWindow>();
                lobbyWindow.Initialize(_service, _config.ToggleWindow);
                AttachGameAdapter(_adapter, _service, _harmony, _host, lobbyWindow, _config);
                InstallTestHarness(Config, _service, _adapter, _host);

                Logger.LogInfo($"{Name} {ProtocolInfo.ModVersion} loaded (protocol {ProtocolInfo.Version}). " +
                    $"Press {_config.ToggleWindow.Value} for the multiplayer window.");
#if STUB_GAME
                Logger.LogWarning($"{Name} was compiled against the game stubs (STUB_GAME build); it is for compile checks only.");
#endif
            }
            catch (Exception e)
            {
                Logger.LogError($"{Name} failed to start: {e}");
                Teardown();
            }
        }

        private void OnDestroy() => Teardown();

        /// <summary>
        /// The game integration (<see cref="NggGameAdapter"/>), or the game-independent <see cref="NullGameAdapter"/>
        /// when the game's assemblies do not match this build.
        /// </summary>
        internal static IGameAdapter CreateGameAdapter()
        {
            try
            {
                return CreateNggGameAdapter();
            }
            catch (Exception e) when (IsBindingFailure(e))
            {
                NetLog.Error($"[Game] The game's assemblies do not match this build; the game integration is off and " +
                    $"sessions will not sync progress. {e}");
                return new NullGameAdapter();
            }
        }

        // Out of line for the same reason as CreateService: a game type that does not resolve fails in the caller's try.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static IGameAdapter CreateNggGameAdapter() => new NggGameAdapter();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void AttachGameAdapter(IGameAdapter adapter, IMultiplayerService service, Harmony harmony,
            GameObject host, LobbyWindow lobbyWindow, PluginConfig config)
        {
            if (!(adapter is NggGameAdapter game))
                return;
            try
            {
                game.Attach(service, harmony, host, () => lobbyWindow.Visible = true, () => config.ToggleWindow.Value.ToString(),
                    () => config.ScorecardKey.Value, () => config.RemoteBallTrail.Value, () => config.TrailSeconds.Value);
                lobbyWindow.SetGameInfo(game.LobbyInfo);
                if (game.Context != null)
                {
                    game.Context.SaveLobbyDefaults = (visibility, max) =>
                    {
                        config.LobbyType.Value = visibility;
                        config.MaxPlayers.Value = max;
                    };
                    game.Context.ReadLook = () => config.Character.Value;
                    game.Context.SaveLook = look => config.Character.Value = look;
                }
            }
            catch (Exception e)
            {
                NetLog.Error($"[Game] Attaching the game integration failed; game features are off: {e}");
            }
        }

        /// <summary>
        /// <c>[Debug] SaveDirOverride</c> (test copies): every save path the game builds points into that folder. When
        /// it is set but cannot be applied completely, the plugin does not start (the game would use the real saves).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void InstallSaveDirOverride(Harmony harmony, string folder)
        {
            if (string.IsNullOrWhiteSpace(folder))
                return;
            if (!Game.SaveDirOverride.Install(harmony, folder))
                throw new InvalidOperationException("[Debug] SaveDirOverride could not redirect every save path (see above).");
        }

        /// <summary>
        /// The single-PC test harness (docs/TESTING.md). Binds <c>[Debug] EnableTestHarness</c> (default false) and,
        /// only when it is on, starts the bots and the command-file poller; off, nothing of the harness runs.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void InstallTestHarness(ConfigFile config, IMultiplayerService service, IGameAdapter adapter, GameObject host)
        {
            try
            {
                Testing.TestHarness.Install(config, service, adapter, host);
            }
            catch (Exception e)
            {
                NetLog.Error($"[TestHarness] The test harness could not be loaded; it stays off: {e}");
            }
        }

        private static bool IsBindingFailure(Exception e) =>
            e is TypeLoadException || e is FileNotFoundException || e is FileLoadException || e is BadImageFormatException
            || e is MissingMemberException || e is TypeInitializationException;

        // Kept out of line: compiling it loads SteamMultiplayerService and with it Steamworks.NET, which must fail
        // inside the caller's try block when the game's Steamworks assembly does not resolve.
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static IMultiplayerService CreateService(IGameAdapter adapter, MultiplayerSettings settings) =>
            new SteamMultiplayerService(adapter, settings);

        private static IMultiplayerService CreateServiceOrFallback(IGameAdapter adapter, MultiplayerSettings settings)
        {
            try
            {
                return CreateService(adapter, settings);
            }
            catch (Exception e) when (IsBindingFailure(e))
            {
                NetLog.Error($"[Multiplayer] Steamworks.NET could not be loaded; multiplayer is disabled. {e}");
                return new UnavailableMultiplayerService(
                    "This build of the mod does not match the game's Steamworks.NET (" + e.GetType().Name + "). " +
                    "Install a build made for this game.");
            }
        }

        private void OnSettingChanged(object sender, SettingChangedEventArgs e)
        {
            NetLog.MinLevel = _config.LogLevel.Value;
            try
            {
                _service?.ApplySettings(_config.ToSettings());
            }
            catch (Exception ex)
            {
                Logger.LogError($"Applying settings failed: {ex}");
            }
        }

        private void Teardown()
        {
            Config.SettingChanged -= OnSettingChanged;
            if (_host != null)
            {
                Destroy(_host);
                _host = null;
            }
            try
            {
                _service?.Dispose();
            }
            catch (Exception e)
            {
                Logger.LogError($"Shutting down multiplayer failed: {e}");
            }
            try
            {
                _adapter?.Dispose();
            }
            catch (Exception e)
            {
                Logger.LogError($"Shutting down the game adapter failed: {e}");
            }
            bool keepGuards = KeepSaveWriteGuards();
            try
            {
                if (_harmony != null && keepGuards)
                    UnpatchAllButSaveGuards(_harmony);
                else
                    _harmony?.UnpatchSelf();
            }
            catch (Exception e)
            {
                Logger.LogError($"Removing the game patches failed: {e}");
            }
            _service = null;
            _adapter = null;
            _harmony = null;
            Service = null;
        }

        /// <summary>
        /// After the service and the game modules are gone: the game's memory may still hold the host's save (a client
        /// unloaded mid-session or behind the post-session fence). Then the save write guards stay installed, or the
        /// game's next SaveRun would write the host's progress over the player's own files.
        /// </summary>
        private bool KeepSaveWriteGuards()
        {
            try
            {
                return KeepWriteGuardsOnUnload();
            }
            catch (Exception e) when (IsBindingFailure(e))
            {
                return false; // the game integration never loaded: nothing of the game is patched
            }
            catch (Exception e)
            {
                Logger.LogError($"Checking the save state on unload failed; save writes stay blocked: {e}");
                return true;
            }
        }

        // Out of line: it loads the game integration's types, which must fail inside the caller's try block.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool KeepWriteGuardsOnUnload() => Game.Lifecycle.LifecycleSaveModule.KeepWriteGuardsOnUnload();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool IsSaveWriteGuard(MethodInfo patch) => Game.Lifecycle.SavePatches.IsWriteGuard(patch);

        private static void UnpatchAllButSaveGuards(Harmony harmony)
        {
            var originals = new List<MethodBase>(harmony.GetPatchedMethods());
            foreach (var original in originals)
            {
                var info = Harmony.GetPatchInfo(original);
                if (info == null)
                    continue;
                var patches = new List<Patch>();
                patches.AddRange(info.Prefixes);
                patches.AddRange(info.Postfixes);
                patches.AddRange(info.Transpilers);
                patches.AddRange(info.Finalizers);
                foreach (var patch in patches)
                {
                    if (patch.owner != harmony.Id || IsSaveWriteGuard(patch.PatchMethod))
                        continue;
                    harmony.Unpatch(original, patch.PatchMethod);
                }
            }
            NetLog.Warn("[Lifecycle] The plugin unloaded with the host's save possibly in memory; its save write guards " +
                "stay in place until the game restarts.");
        }

        private static void WriteLog(LogLevel level, string message)
        {
            var log = _log;
            if (log == null)
                return;
            switch (level)
            {
                case LogLevel.Debug:
                    log.LogDebug(message);
                    break;
                case LogLevel.Warning:
                    log.LogWarning(message);
                    break;
                case LogLevel.Error:
                    log.LogError(message);
                    break;
                default:
                    log.LogInfo(message);
                    break;
            }
        }
    }
}
