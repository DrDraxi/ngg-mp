using System;
using System.Collections.Generic;
using NGGMultiplayer.Core.Diagnostics;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Sync;
using NGGMultiplayer.Runtime;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NGGMultiplayer.Game.Lifecycle
{
    /// <summary>
    /// Session lifecycle and scenes, and the host's save as the session save: client save injection, save write
    /// protection, backup and restore of the player's own save. Provides the ISessionSaveStore. Spec:
    /// docs/GAME_INTEGRATION.md sections 4 and 5 (plus the pause-menu time handling of 7.6).
    /// <list type="bullet">
    /// <item>Host: every scene load is announced (<c>ngg.lc.scene</c>); once the scene settles the scene epoch moves
    /// when the scene or game mode changed, and every client gets a fresh session save.</item>
    /// <item>Client: after each session save the client follows the host's scene (<see cref="FollowRules"/>); on
    /// leaving it returns to Menu2, which reads the player's own save from disk again.</item>
    /// </list>
    /// </summary>
    public sealed class LifecycleSaveModule : IGameModule
    {
        private const string Log = "[Lifecycle] ";

        /// <summary>A Menu2 load whose MainMenu.Start was never seen settles after this long.</summary>
        private const double MenuSettleFallbackSeconds = 2.0;

        /// <summary>A scene load the module started that never arrived stops blocking after this long.</summary>
        private const double LoadTimeoutSeconds = 15.0;

        /// <summary>Post-session fence watchdog in Menu2 (5.5.3).</summary>
        private const double FenceWatchdogSeconds = 10.0;

        private const double MainLoadWindowSeconds = 30.0;
        private const int MainLoadsBeforeRawRun = 4;
        private const int MainLoadsBeforeGivingUp = 7;
        private const float BlockedToastSeconds = 3f;

        private readonly List<double> _mainLoads = new List<double>();

        private GameContext _ctx;
        private Action<GameScene> _onSceneLoaded;
        private Action _onWorldLoaded;
        private Action _onLateTick;
        private Action _onGui;
        private Action<MultiplayerState> _onServiceState;
        private LifecycleGui _gui;

        // Scene state (every role).
        private bool _settled = true;
        private int _settledLoadId = -1;
        private GameScene _loadRequested = GameScene.None;
        private double _loadRequestedAt;
        private GameModeState _modeState;

        // Host.
        private bool _hosting;
        private uint _hostEpoch = 1;
        private GameScene _lastScene;
        private byte _lastMode;
        private bool _hostCaptureReady = true;

        // Client.
        private SaveManager _injectedManager;
        private bool _injectedValid;
        private uint _injectedEpoch;
        private byte _injectedMode;
        private int _injectedLoadId = -1;
        private bool _hostLoadingKnown;
        private SceneNotice _hostLoading;
        private bool _endedAsClient;
        private double _lastBlockedToast = double.NegativeInfinity;

        // Leaving.
        private bool _quitToMenuPending;
        private bool _returnToMenuPending;
        private int _returnRequestLoadId;
        private bool _quitting;
        private double _fenceMenuSince = double.NaN;
        private MultiplayerState _lastServiceState = MultiplayerState.Unavailable;

        public string Name => "Lifecycle";

        internal static LifecycleSaveModule Instance { get; private set; }

        internal SessionSaveStore Store { get; private set; }

        internal PauseTime Pause { get; private set; }

        internal GameContext Context => _ctx;

        /// <summary>Host: the scene epoch clients compare against (moves when the host's scene or mode changes).</summary>
        internal uint HostEpoch => _hostEpoch;

        /// <summary>Host: the current scene finished loading and the session save may be read from it.</summary>
        internal bool HostCaptureReady => _hostCaptureReady;

        /// <summary>Client: the host's scene from the latest bundle, or from a newer scene notice.</summary>
        internal GameScene HostScene
        {
            get
            {
                var bundle = Store?.Bundle;
                if (_hostLoadingKnown && (bundle == null || _hostLoading.Epoch >= bundle.SceneEpoch))
                    return _hostLoading.Scene;
                return bundle?.HostScene ?? GameScene.None;
            }
        }

        public void Install(GameContext context)
        {
            _ctx = context ?? throw new ArgumentNullException(nameof(context));
            Instance = this;
            Store = new SessionSaveStore(context, this);
            Pause = new PauseTime(context);
            _gui = new LifecycleGui(this);

            SavePatches.Install(context);
            MenuPatches.Install(context);
            context.Save.SetStore(Store);
            Store.ClearSandboxOnLoad();

            context.RegisterRpc(LifecycleRpcs.Scene, OnSceneRpc, LifecycleRpcs.HostOnly);

            _onSceneLoaded = OnSceneLoaded;
            _onWorldLoaded = OnWorldLoaded;
            _onLateTick = OnLateTick;
            _onGui = OnGui;
            _onServiceState = OnServiceStateChanged;
            context.SceneLoaded += _onSceneLoaded;
            context.WorldLoaded += _onWorldLoaded;
            context.LateTick += _onLateTick;
            context.Gui += _onGui;
            if (context.Service != null)
            {
                _lastServiceState = context.Service.State;
                context.Service.StateChanged += _onServiceState;
            }
            Application.quitting += OnApplicationQuitting;

            // Attached mid-game (plugin reload): the current scene counts as settled.
            _settled = context.Scene != GameScene.Main || context.WorldReady;
            _settledLoadId = context.SceneLoadId;
            _hostCaptureReady = _settled;
            CacheModeState(SaveManager.instance);

            var missing = MissingSafetyFeatures();
            if (missing.Count > 0)
                NetLog.Error($"{Log}Save protection is incomplete ({string.Join(", ", missing.ToArray())}): this game can host, but not join sessions.");
        }

        public void Uninstall()
        {
            var ctx = _ctx;
            if (ctx != null)
            {
                ctx.SceneLoaded -= _onSceneLoaded;
                ctx.WorldLoaded -= _onWorldLoaded;
                ctx.LateTick -= _onLateTick;
                ctx.Gui -= _onGui;
                if (ctx.Service != null)
                    ctx.Service.StateChanged -= _onServiceState;
                if (ReferenceEquals(ctx.Save.Store, Store))
                    ctx.Save.SetStore(null);
            }
            Application.quitting -= OnApplicationQuitting;
            if (ReferenceEquals(Instance, this))
                Instance = null;
        }

        public void Tick(float deltaTime)
        {
            var ctx = _ctx;
            if (ctx == null)
                return;
            if (_loadRequested != GameScene.None && ctx.Now - _loadRequestedAt > LoadTimeoutSeconds)
            {
                NetLog.Warn($"{Log}Loading {_loadRequested} did not finish within {LoadTimeoutSeconds}s.");
                _loadRequested = GameScene.None;
                ctx.SetWorldReplacePending(false);
                _settled = true;
                _settledLoadId = ctx.SceneLoadId;
                if (Store.SessionSaveActive)
                    ctx.Post(FollowHost);
            }
            TickFenceWatchdog(ctx);
        }

        public void OnSessionReady(bool isHost)
        {
            if (isHost)
            {
                _hosting = true;
                _hostEpoch = 1;
                _lastScene = _ctx.Scene;
                _lastMode = CurrentModeByte();
                _hostCaptureReady = _settled && _settledLoadId == _ctx.SceneLoadId;
                Store.ResetHostCache();
                NetLog.Info($"{Log}Hosting from {_lastScene} ({ModeName(_lastMode)}).");
            }
            else
            {
                _hosting = false;
                _ctx.Notify($"Joined {_ctx.PlayerName(_ctx.HostId)}'s session.");
            }
            Pause.OnSessionReady();
        }

        public void OnSessionEnded()
        {
            if (_hosting)
            {
                _hosting = false;
                Store.ResetHostCache();
            }
            _hostLoadingKnown = false;
            Pause.OnSessionEnded();
            if (!_endedAsClient)
                return;
            _endedAsClient = false;
            string status = _ctx.Service?.StatusMessage;
            _ctx.Notify(string.IsNullOrEmpty(status) ? "Left the session." : "Left the session: " + status, 6f);
            if (_ctx.Save.SuppressDiskWrites)
            {
                // Core keeps writes suppressed: the player's files could not be verified, so nothing may touch them.
                _ctx.Notify("Your own save could not be restored safely; saving is off. Restart the game before playing on.", 15f);
            }
        }

        // ---- Store callbacks -------------------------------------------------------------------------

        /// <summary>The safety features a client needs; empty when it may join.</summary>
        internal List<string> MissingSafetyFeatures()
        {
            var missing = new List<string>();
            var p = _ctx.Patcher;
            if (!SavePatches.ProtectInstalled || !p.IsEnabled(SavePatches.ProtectFeature))
                missing.Add(SavePatches.ProtectFeature);
            if (!SavePatches.InjectInstalled || !p.IsEnabled(SavePatches.InjectFeature))
                missing.Add(SavePatches.InjectFeature);
            return missing;
        }

        internal void OnBundleApplied(SessionSaveBundle bundle, SessionSaveBundle previous)
        {
            if (_hostLoadingKnown && _hostLoading.Epoch <= bundle.SceneEpoch)
                _hostLoadingKnown = false;
            if (previous == null)
                _mainLoads.Clear();
            // Progression's OnFactsReplaced runs right after this: tell it now if the running world is about to go.
            if (_loadRequested != GameScene.None || DecideFollow(bundle).Action == FollowAction.Load)
                _ctx.SetWorldReplacePending(true);
            // Never load from inside Core's callback.
            _ctx.Post(FollowHost);
        }

        internal void OnRunInjected(SaveManager manager, SessionSaveBundle bundle)
        {
            _injectedManager = manager;
            _injectedValid = true;
            _injectedEpoch = bundle.SceneEpoch;
            _injectedMode = bundle.Mode;
            _injectedLoadId = _ctx.SceneLoadId;
        }

        internal void OnLocalSaveRestored(bool hadBundle, bool ok)
        {
            NetLog.Info($"{Log}Session save dropped{(hadBundle ? "" : " (none was loaded)")}; own save {(ok ? "in place" : "NOT verified")}.");
            _endedAsClient = true;
            _injectedValid = false;
            _mainLoads.Clear();
            _returnToMenuPending = true;
            _returnRequestLoadId = _ctx.SceneLoadId;
            _ctx.Post(ReturnToMenu);
        }

        // ---- Patch callbacks -------------------------------------------------------------------------

        internal void OnMainMenuStarted()
        {
            var manager = SaveManager.instance;
            CacheModeState(manager);
            var bundle = Store.Bundle;
            if (bundle != null)
            {
                // MainMenu.Start forces Story; the session's mode is the host's.
                ApplyMode(manager, bundle.Mode);
            }
            else
            {
                TryDropFence("Menu2 read the own save");
            }
            Settle(GameScene.Menu);
        }

        /// <summary>True when a client tries to start a game from Menu2 (the host picks it).</summary>
        internal bool BlockMenuPlay()
        {
            if (!_ctx.IsClientConnection && !Store.SessionSaveActive)
                return false;
            double now = _ctx.Now;
            if (now - _lastBlockedToast > BlockedToastSeconds)
            {
                _lastBlockedToast = now;
                _ctx.Notify("The host picks the game.");
            }
            return true;
        }

        /// <summary>
        /// True when the settings menu's DeleteStorySave / DeleteEverything must not run: a client of a session, or
        /// while writes are blocked (the deletes would be skipped anyway, but the button still loads Menu2 / INTRO).
        /// The host deletes its own save as usual.
        /// </summary>
        internal bool BlockSaveDelete()
        {
            if (!_ctx.IsClientConnection && !Store.SessionSaveActive && !SessionSaveStore.WritesBlocked)
                return false;
            double now = _ctx.Now;
            if (now - _lastBlockedToast > BlockedToastSeconds)
            {
                _lastBlockedToast = now;
                _ctx.Notify("Deleting saves is off during a multiplayer session.");
            }
            return true;
        }

        internal void OnQuitToMenu()
        {
            Pause.OnMenuClosed("quit to menu");
            if (!_ctx.IsClientConnection && !Store.SessionSaveActive)
                return;
            // The game sets timeScale 1, calls SaveRun and loads Menu2 itself right after this (#60); that SaveRun
            // writes nothing (the fence is up by then).
            _quitToMenuPending = true;
            NetLog.Info($"{Log}Quit to menu: leaving the session.");
            _ctx.Service?.Leave();
        }

        /// <summary>
        /// Sets the shared GameModeState (a ScriptableObject) to a bundle mode byte. False when no GameModeState was
        /// found (the game keeps whatever mode it has).
        /// </summary>
        internal bool ApplyMode(SaveManager manager, byte mode)
        {
            var state = FindModeState(manager);
            if (state == null)
            {
                _ctx.WarnOnce("mode.state", $"{Log}No GameModeState found; the game mode cannot be set to the host's.");
                return false;
            }
            state.mode = mode == SessionSaveBundle.ModeJustGolf ? GameMode.JustGolf : GameMode.Story;
            return true;
        }

        /// <summary>
        /// Client, SetUpScene starting on the host's run: keeps it off the broken-save path and away from an unknown
        /// challenge set (<see cref="RunGuard"/>, #74). False skips SetUpScene (and leaves the session) when no safe
        /// set exists.
        /// </summary>
        internal bool GuardInjectedRun(SaveManager manager)
        {
            var run = manager.m_run;
            bool isStory = CurrentModeByte() == SessionSaveBundle.ModeStory;
            Func<string, bool> isKnown = null;
            // The list SetUpScene's GetChallengeSet searches, without the Debug.LogError that call writes for the
            // unknown name this check exists for (answers #12, #74, #87).
            if (GreatConnector.instance != null)
                isKnown = name => GameIds.FindConnectorSet(name) != null;
            else
                _ctx.WarnOnce("setup.connector", $"{Log}GreatConnector is not there when SetUpScene starts; the challenge set name is not checked.");
            if (!Store.TryGetHostTrack(out string hostSet, out int hostIndex))
            {
                hostSet = null;
                hostIndex = 0;
            }
            int bounties = run.m_completedBounties != null ? run.m_completedBounties.Count : 0;
            var decision = RunGuard.Decide(isStory, run.m_challengeSetLoaded, run.m_currentChallenge, run.finishedDemo,
                bounties, hostSet, hostIndex, isKnown);
            switch (decision.Action)
            {
                case RunGuardAction.Replace:
                    NetLog.Warn($"{Log}The host's run has {decision.Reason}; loading challenge set " +
                        $"'{decision.SetName}' #{decision.Index} instead (SetUpScene would otherwise break the load).");
                    manager.m_run.m_challengeSetLoaded = decision.SetName;
                    manager.m_run.m_currentChallenge = decision.Index;
                    return true;
                case RunGuardAction.Unfixable:
                    NetLog.Error($"{Log}The host's run has {decision.Reason} and no safe challenge set; not applying it. Leaving the session.");
                    _ctx.Notify("The host's world could not be loaded safely; leaving the session.", 8f);
                    _ctx.Post(() => _ctx?.Service?.Leave());
                    return false;
                default:
                    return true;
            }
        }

        internal byte CurrentModeByte()
        {
            var state = FindModeState(SaveManager.instance);
            if (state == null)
                return _hosting ? _lastMode : SessionSaveBundle.ModeStory;
            return state.mode == GameMode.JustGolf ? SessionSaveBundle.ModeJustGolf : SessionSaveBundle.ModeStory;
        }

        // ---- Scenes ----------------------------------------------------------------------------------

        private void OnSceneLoaded(GameScene scene)
        {
            var ctx = _ctx;
            int loadId = ctx.SceneLoadId;
            _loadRequested = GameScene.None;
            _quitToMenuPending = false;
            _hostCaptureReady = false;
            _fenceMenuSince = double.NaN;
            Pause.OnSceneLoaded();
            if (scene != GameScene.Main)
                _injectedValid = false;

            if (scene == GameScene.Main || scene == GameScene.Menu)
            {
                _settled = false;
                if (scene == GameScene.Menu)
                {
                    ctx.Delay(MenuSettleFallbackSeconds, () =>
                    {
                        if (_ctx != null && _ctx.SceneLoadId == loadId && !_settled)
                        {
                            if (MenuPatches.MenuStartHooked)
                                NetLog.Warn($"{Log}MainMenu.Start was not seen in Menu2; settling anyway.");
                            Settle(GameScene.Menu);
                        }
                    });
                }
            }
            else
            {
                Settle(scene);
            }

            if (scene == GameScene.Main && Store.SessionSaveActive)
                CheckMainLoadLoop();
            if (ctx.IsHost)
                AnnounceHostScene(scene);
        }

        private void OnWorldLoaded()
        {
            CacheModeState(SaveManager.instance);
            if (!Store.SessionSaveActive)
                TryDropFence("Main loaded the own save");
            Settle(GameScene.Main);
        }

        /// <summary>
        /// Drops the post-session fence once a scene loaded after the restore has read the own save from disk (its
        /// SaveManager.Start ran without the injection). Menu2 is the usual place: MainMenu.Start calls LoadRunSave,
        /// which assigns m_run from disk itself (#29, #73; its order against SaveManager.Start does not matter, both
        /// read the own files). CREDITS ends in Menu2 and the game never loads Main after it (#81), but a Main that
        /// loaded the own save counts too.
        /// </summary>
        private void TryDropFence(string why)
        {
            if (!SessionSaveStore.FenceUp || Store.SessionSaveActive || _ctx.SceneLoadId <= Store.FenceLoadId)
                return;
            // SaveManager is per scene (not DontDestroyOnLoad, #72), so the one that held the host's save is gone;
            // should one ever survive the scene change, it still has that save in memory.
            var manager = SaveManager.instance;
            if (manager != null && ReferenceEquals(manager, _injectedManager) && !ReloadOwnSave(manager))
                return;
            _injectedManager = null;
            Store.DropFence(why);
        }

        /// <summary>
        /// Plugin unload (its OnDestroy, while the game may keep running): true when the game's memory may still hold
        /// the host's save, so the save write guards must stay installed. Outside "Main" it first tries to make them
        /// unnecessary by reloading the own save into SaveManager, as the fence drop does. Static: the modules are
        /// already uninstalled when this runs.
        /// </summary>
        internal static bool KeepWriteGuardsOnUnload()
        {
            if (!SessionSaveStore.WritesBlocked)
                return false;
            if (SessionSaveStore.SuppressedBySession)
            {
                NetLog.Warn($"{Log}Unloading while the host's save may be loaded; save writes stay blocked until the game restarts.");
                return true;
            }
            // Fence only: the files are the player's again, but memory may not be.
            if (SceneManager.GetActiveScene().name == GameScenes.MainName)
            {
                NetLog.Warn($"{Log}Unloading in the host's world; save writes stay blocked until the game restarts.");
                return true;
            }
            var manager = SaveManager.instance;
            if (manager != null && !ReloadOwnSave(manager))
                return true;
            SessionSaveStore.DropFenceOnUnload();
            return false;
        }

        /// <summary>Reads the player's own save from disk into <paramref name="manager"/>; false (fence stays) on failure.</summary>
        private static bool ReloadOwnSave(SaveManager manager)
        {
            try
            {
                manager.m_run = manager.LoadRunSave();
                manager.m_unlocks = manager.GetUnlocksSave();
                manager.m_scoreCardSave = manager.GetScoreCardSave();
                return true;
            }
            catch (Exception e)
            {
                NetLog.Error($"{Log}Reloading the own save failed; saving stays off until Menu2 is reloaded: {e}");
                return false;
            }
        }

        private void Settle(GameScene scene)
        {
            var ctx = _ctx;
            if (ctx == null || ctx.Scene != scene)
                return;
            int loadId = ctx.SceneLoadId;
            if (_settled && _settledLoadId == loadId)
                return;
            _settled = true;
            _settledLoadId = loadId;
            // Posted: the other modules rebuild their baselines on the same events first.
            ctx.Post(() => AfterSettled(loadId));
        }

        private void AfterSettled(int loadId)
        {
            var ctx = _ctx;
            if (ctx == null || loadId != ctx.SceneLoadId)
                return;
            _hostCaptureReady = true;
            if (ctx.IsHost)
                HostSettled();
            else if (Store.SessionSaveActive)
                FollowHost();
        }

        // ---- Host ------------------------------------------------------------------------------------

        private void AnnounceHostScene(GameScene scene)
        {
            byte mode = CurrentModeByte();
            uint epoch = scene != _lastScene || mode != _lastMode ? _hostEpoch + 1 : _hostEpoch;
            var notice = new SceneNotice(scene, epoch, mode);
            _ctx.Send(LifecycleRpcs.Scene, RpcTarget.Others, notice, LifecycleRpcs.WriteScene);
        }

        private void HostSettled()
        {
            var ctx = _ctx;
            var scene = ctx.Scene;
            byte mode = CurrentModeByte();
            if (scene != _lastScene || mode != _lastMode)
            {
                _hostEpoch++;
                NetLog.Info($"{Log}Host now in {scene} ({ModeName(mode)}), scene epoch {_hostEpoch}.");
                _lastScene = scene;
                _lastMode = mode;
            }
            try
            {
                ctx.Save.RebuildFacts();
            }
            catch (Exception e)
            {
                NetLog.Error($"{Log}Rebuilding the session's facts failed: {e}");
            }
            try
            {
                // The host's m_run was (re)loaded outside LocalProgressChanged: every client needs the new save.
                var saveHost = ctx.Net?.SaveHost;
                if (saveHost != null && saveHost.PeerCount > 0)
                    saveHost.ResyncAll();
                else
                    Store.RefreshHostCache();
            }
            catch (Exception e)
            {
                NetLog.Error($"{Log}Resyncing the clients failed: {e}");
            }
        }

        // ---- Client ----------------------------------------------------------------------------------

        private void OnSceneRpc(PeerId from, NetReader reader)
        {
            var notice = LifecycleRpcs.ReadScene(reader);
            var ctx = _ctx;
            if (ctx == null || ctx.IsHost || from != ctx.HostId)
                return;
            var bundle = Store.Bundle;
            // Older than the save we already have (late arrival): nothing to show.
            if (bundle != null && notice.Epoch < bundle.SceneEpoch)
                return;
            _hostLoading = notice;
            _hostLoadingKnown = true;
            ctx.Notify($"{ctx.PlayerName(ctx.HostId)} is loading {Describe(notice.Scene)}...", 3f);
        }

        private void FollowHost()
        {
            var ctx = _ctx;
            var bundle = Store?.Bundle;
            if (ctx == null || bundle == null || _quitting)
                return;
            // A load the module started is still on its way; its settle decides again.
            if (_loadRequested != GameScene.None)
                return;
            var decision = DecideFollow(bundle);
            if (decision.Action != FollowAction.Load)
            {
                ctx.SetWorldReplacePending(false);
                return;
            }
            NetLog.Info($"{Log}Following the host ({bundle.HostScene}, epoch {bundle.SceneEpoch}): loading {decision.Target}.");
            string host = ctx.PlayerName(ctx.HostId);
            switch (decision.Target)
            {
                case GameScene.Main:
                    // Loading "Main" directly skips PlaySetUp (its prompts, m_hasCheckedForFirstSave + SaveGameSettings
                    // and the music fade, #76): the mode must already be on the shared GameModeState, so the new
                    // scene's SaveManager starts in the host's mode.
                    if (!ApplyMode(SaveManager.instance, bundle.Mode) && bundle.Mode != SessionSaveBundle.ModeStory)
                    {
                        // MainMenu.Start forces Story; a JustGolf world loaded in Story would be the wrong game.
                        NetLog.Error($"{Log}Cannot switch the game to the host's JustGolf mode; leaving the session.");
                        ctx.Notify("The host's game mode could not be set; leaving the session.", 8f);
                        ctx.SetWorldReplacePending(false);
                        ctx.Post(() => _ctx?.Service?.Leave());
                        return;
                    }
                    ctx.Notify($"Joining {host}'s game...");
                    break;
                case GameScene.Credits:
                    ctx.Notify($"{host} reached the ending.");
                    break;
                default:
                    ctx.Notify(bundle.HostScene == GameScene.Credits
                        ? "The host is watching the credits."
                        : $"Waiting for {host} to start a game.");
                    break;
            }
            LoadScene(decision.Target);
        }

        private FollowDecision DecideFollow(SessionSaveBundle bundle)
        {
            var ctx = _ctx;
            bool settled = _settled && _settledLoadId == ctx.SceneLoadId;
            bool worldMatches = ctx.Scene == GameScene.Main && _injectedValid && _injectedLoadId == ctx.SceneLoadId
                && _injectedEpoch == bundle.SceneEpoch && _injectedMode == bundle.Mode;
            return FollowRules.Decide(bundle.HostScene, bundle.HasRun, ctx.Scene, settled, worldMatches);
        }

        private void LoadScene(GameScene target)
        {
            string name = GameScenes.ToName(target);
            if (name == null)
                return;
            _loadRequested = target;
            _loadRequestedAt = _ctx.Now;
            _settled = false;
            _ctx.SetWorldReplacePending(true);
            SceneManager.LoadScene(name);
        }

        /// <summary>
        /// The game reloading Main over and over must not loop forever. SetUpScene's broken-save reload (#74) is kept
        /// away by <see cref="GuardInjectedRun"/>; this catches it should that check be missing, and anything else.
        /// </summary>
        private void CheckMainLoadLoop()
        {
            double now = _ctx.Now;
            _mainLoads.Add(now);
            _mainLoads.RemoveAll(t => now - t > MainLoadWindowSeconds);
            if (_mainLoads.Count >= MainLoadsBeforeGivingUp)
            {
                _mainLoads.Clear();
                NetLog.Error($"{Log}The host's world reloaded {MainLoadsBeforeGivingUp} times in {MainLoadWindowSeconds}s; leaving the session.");
                _ctx.Notify("The host's world could not be loaded; leaving the session.", 8f);
                _ctx.Post(() => _ctx.Service?.Leave());
            }
            else if (_mainLoads.Count >= MainLoadsBeforeRawRun)
            {
                Store.DisableTrackOverride();
            }
        }

        // ---- Leaving ---------------------------------------------------------------------------------

        private void ReturnToMenu()
        {
            var ctx = _ctx;
            if (ctx == null || !_returnToMenuPending)
                return;
            _returnToMenuPending = false;
            if (_quitting || Store.SessionSaveActive)
                return;
            // QuitToMenu loads Menu2 itself; INTRO and CREDITS go on to Menu2 on their own (nothing saves there).
            if (_quitToMenuPending || ctx.Scene == GameScene.Intro || ctx.Scene == GameScene.Credits)
                return;
            // A Menu2 load finished after the restore: it already read the player's own save.
            if (ctx.Scene == GameScene.Menu && ctx.SceneLoadId != _returnRequestLoadId)
                return;
            NetLog.Info($"{Log}Session over: returning to Menu2 to reload the own save.");
            LoadScene(GameScene.Menu);
        }

        private void TickFenceWatchdog(GameContext ctx)
        {
            if (!SessionSaveStore.FenceUp || Store.SessionSaveActive || ctx.Scene != GameScene.Menu || _loadRequested != GameScene.None)
            {
                _fenceMenuSince = double.NaN;
                return;
            }
            if (double.IsNaN(_fenceMenuSince))
            {
                _fenceMenuSince = ctx.Now;
                return;
            }
            if (ctx.Now - _fenceMenuSince < FenceWatchdogSeconds)
                return;
            _fenceMenuSince = double.NaN;
            NetLog.Warn($"{Log}Menu2 did not re-read the own save within {FenceWatchdogSeconds}s; reloading it from disk.");
            var manager = SaveManager.instance;
            // Keep the fence on failure: memory may still hold the host's save.
            if (manager != null && !ReloadOwnSave(manager))
                return;
            _injectedManager = null;
            Store.DropFence("watchdog reloaded the own save");
        }

        private void OnServiceStateChanged(MultiplayerState state)
        {
            var previous = _lastServiceState;
            _lastServiceState = state;
            if (previous == MultiplayerState.Offline && (state == MultiplayerState.JoiningLobby || state == MultiplayerState.Connecting))
                _ctx.Post(FlushOwnProgress);
        }

        /// <summary>Joining from the own world: put the latest own progress on disk before protection starts (4.1).</summary>
        private void FlushOwnProgress()
        {
            var ctx = _ctx;
            if (ctx == null || ctx.Scene != GameScene.Main || !ctx.WorldReady || SessionSaveStore.WritesBlocked || Store.SessionSaveActive)
                return;
            var controller = MoveAndHitController.instance;
            var manager = SaveManager.instance;
            // Mid-dialogue nothing is flushed: the save on disk stays consistent, just older.
            if (controller == null || controller.m_moveLocked || manager == null)
                return;
            try
            {
                // Not m_moveLocked, so WriteSave writes synchronously inside SaveRun (#135): the file is on disk
                // before the backup, and no pending write can be lost to the host's scene load (#136).
                manager.SaveRun();
                NetLog.Info($"{Log}Saved the own progress before joining.");
            }
            catch (Exception e)
            {
                NetLog.Warn($"{Log}Saving the own progress before joining failed: {e.Message}");
            }
        }

        // The game itself does not save on quit (#49, #108); quitting only stops scene loads of the plugin's own.
        private void OnApplicationQuitting() => _quitting = true;

        // ---- Frame hooks -----------------------------------------------------------------------------

        private void OnLateTick() => Pause.LateTick();

        private void OnGui() => _gui.Draw();

        // ---- Helpers ---------------------------------------------------------------------------------

        private void CacheModeState(SaveManager manager)
        {
            if (manager != null && manager.m_gamemodeState != null)
                _modeState = manager.m_gamemodeState;
        }

        private GameModeState FindModeState(SaveManager manager)
        {
            if (manager != null && manager.m_gamemodeState != null)
                return _modeState = manager.m_gamemodeState;
            if (_modeState != null)
                return _modeState;
            try
            {
                var all = Resources.FindObjectsOfTypeAll<GameModeState>();
                if (all != null && all.Length > 0)
                    _modeState = all[0];
            }
            catch (Exception e)
            {
                _ctx.WarnOnce("mode.find", $"{Log}Looking up the GameModeState failed: {e.Message}");
            }
            return _modeState;
        }

        internal static string Describe(GameScene scene)
        {
            switch (scene)
            {
                case GameScene.Main: return "the world";
                case GameScene.Menu: return "the menu";
                case GameScene.Credits: return "the credits";
                default: return "a scene";
            }
        }

        private static string ModeName(byte mode) => mode == SessionSaveBundle.ModeJustGolf ? "JustGolf" : "Story";
    }
}
