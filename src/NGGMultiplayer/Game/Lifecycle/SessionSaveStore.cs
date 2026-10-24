using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NGGMultiplayer.Core.Diagnostics;
using UnityEngine;

namespace NGGMultiplayer.Game.Lifecycle
{
    /// <summary>
    /// The session save (docs/GAME_INTEGRATION.md section 5), behind Core's save sync through
    /// <see cref="GameSaveBridge"/>:
    /// <list type="bullet">
    /// <item>Host: <see cref="CaptureBundle"/> packs the host's save and the facts. Nothing in the game changes.</item>
    /// <item>Client: <see cref="BackupLocalSave"/> copies the player's own files aside, <see cref="ApplyBundle"/>
    /// keeps the host's bundle in memory (the game reads it through the injection patches), and
    /// <see cref="RestoreLocalSave"/> verifies the player's files and raises the post-session fence.</item>
    /// </list>
    /// The write-protection state is static (<see cref="WritesBlocked"/>) so the save-write guards can read it
    /// without touching anything that could throw. Main thread only.
    /// </summary>
    internal sealed class SessionSaveStore : ISessionSaveStore
    {
        private const string Log = "[Lifecycle] ";
        private const string BankFeature = "lc.capture.bank";
        private const string PendingWriteFeature = "lc.save.pending";
        private const int LmugcHoles = 9;

        public const string RunFile = "run2.txt";
        public const string NormalGolfFile = "actuallyNormal.txt";
        public const string UnlocksFile = "unlocks.txt";
        public const string ScoreCardFile = "scoreCard.txt";

        private static volatile bool _suppress;
        private static volatile bool _fence;

        private readonly GameContext _ctx;
        private readonly LifecycleSaveModule _module;
        private readonly LocalSaveFiles _files;
        private readonly string _filesError;
        private readonly FieldInfo _bankField;
        private readonly FieldInfo _pendingWriteField;

        private SaveBackup _backup;
        private SessionSaveBundle _bundle;
        private SessionSaveBundle _hostCache;
        private bool _haveOwnBall;
        private int _ownSelectedBall;
        private List<bool> _ownUnlockedBalls;
        private int _sessionSelectedBall = -1;
        private bool _useTrackOverride = true;
        private int _fenceLoadId = -1;
        private bool _hostRunKnown;
        private string _hostRunSet;
        private int _hostRunIndex;
        private bool _creditsLogged;

        public SessionSaveStore(GameContext ctx, LifecycleSaveModule module)
        {
            _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
            _module = module ?? throw new ArgumentNullException(nameof(module));
            try
            {
                string root = Path.Combine(BepInEx.Paths.CachePath, "NGGMultiplayer");
                _files = new LocalSaveFiles(SaveDirOverride.Effective, Path.Combine(root, "LocalBackup"),
                    Path.Combine(root, "SessionSave"));
            }
            catch (Exception e)
            {
                _filesError = e.Message;
                NetLog.Error($"{Log}The save backup folders are unusable, so joining sessions is disabled: {e}");
            }
            _bankField = ctx.Patcher.Field(BankFeature, typeof(ProgressManager), "m_bank");
            // SaveRun keeps its WriteSave coroutine here (it stops the previous one first, #135).
            _pendingWriteField = ctx.Patcher.Field(PendingWriteFeature, typeof(SaveManager), "_writeSave");
        }

        /// <summary>Save writes must be skipped: the host's save is (or may still be) loaded in memory.</summary>
        public static bool WritesBlocked
        {
            get
            {
                if (_suppress || _fence)
                    return true;
                var ctx = GameContext.Current;
                return ctx != null && ctx.Save.SuppressDiskWrites;
            }
        }

        /// <summary>The post-session fence (5.5.3) is up.</summary>
        public static bool FenceUp => _fence;

        /// <summary>Core still suppresses writes (a session is running, or the own save could not be put back).</summary>
        public static bool SuppressedBySession => _suppress;

        /// <summary>Plugin unload, after the own save was reloaded into memory: nothing of the host's is left to guard.</summary>
        public static void DropFenceOnUnload()
        {
            if (!_fence)
                return;
            _fence = false;
            NetLog.Info($"{Log}Post-session fence dropped (plugin unloading; the own save is back in memory).");
        }

        /// <summary>The scene load the fence went up in; a scene loaded after it read the own save from disk.</summary>
        public int FenceLoadId => _fenceLoadId;

        /// <summary>The client holds the host's bundle; the injection and redirect patches are live.</summary>
        public bool SessionSaveActive => _bundle != null;

        public SessionSaveBundle Bundle => _bundle;

        public bool BackupTaken => _backup != null;

        public LocalSaveFiles Files => _files;

        // ---- ISessionSaveStore -----------------------------------------------------------------------

        public byte[] CaptureBundle(FactTable facts)
        {
            var bundle = BuildHostSave();
            bundle.Facts = facts != null ? facts.ToArray() : new FactTable().ToArray();
            return bundle.Serialize();
        }

        public FactTable ApplyBundle(byte[] data)
        {
            // Parsed (and validated) completely before anything changes; a bad bundle leaves the previous one.
            var bundle = SessionSaveBundle.Parse(data);
            if (_backup == null)
                throw new InvalidOperationException("The host's save arrived before the player's own save was backed up.");
            var previous = _bundle;
            _bundle = bundle;
            if (previous == null)
                NetLog.Info($"{Log}Session save loaded: host in {bundle.HostScene}, epoch {bundle.SceneEpoch}, " +
                    $"{(bundle.IsJustGolf ? "JustGolf" : "Story")}{(bundle.HasRun ? "" : ", no run")}.");
            WriteSandbox(bundle);
            _module.OnBundleApplied(bundle, previous);
            return bundle.FactTable;
        }

        public void BackupLocalSave()
        {
            var missing = _module.MissingSafetyFeatures();
            if (missing.Count > 0)
            {
                string message = "Cannot load the host's save safely: " + string.Join(", ", missing.ToArray()) +
                    " did not install (game update?).";
                _ctx.Notify("Joining refused: your own save could not be protected. See the log.", 8f);
                throw new InvalidOperationException(message);
            }
            if (_files == null)
                throw new InvalidOperationException("Cannot back up the local save: " + _filesError);

            try
            {
                _backup = _files.Backup(DateTime.Now);
                RememberOwnSave();
                _files.ClearSandbox();
            }
            catch (Exception e)
            {
                _backup = null;
                _ctx.Notify("Joining refused: your own save could not be backed up. See the log.", 8f);
                throw new IOException("Backing up the local save failed: " + e.Message, e);
            }
            _bundle = null;
            _hostRunKnown = false;
            _sessionSelectedBall = -1;
            _useTrackOverride = true;
            NetLog.Info($"{Log}Own save backed up to {_backup.Folder}.");
        }

        public void RestoreLocalSave()
        {
            // The fence first: from here on the in-memory save may still be the host's until Menu2 re-reads the disk.
            RaiseFence();
            var hadBundle = _bundle != null;
            _bundle = null;
            _hostRunKnown = false;
            _sessionSelectedBall = -1;
            Exception failure = null;
            if (_backup != null && _files != null)
            {
                try
                {
                    var report = _files.Restore(_backup);
                    if (report.Clean)
                    {
                        NetLog.Info($"{Log}Own save verified unchanged.");
                    }
                    else
                    {
                        // Steam Auto-Cloud syncs only at launch and exit (#84), so a change here means a game
                        // writer got past the guards.
                        NetLog.Error($"{Log}Save files changed during the session and were put back from {_backup.Folder} " +
                            $"(changed: {Join(report.Changed)}; appeared: {Join(report.Appeared)}; vanished: {Join(report.Vanished)}). " +
                            $"The changed files are kept there as *{LocalSaveFiles.ChangedSuffix}. Please report this with the log.");
                    }
                }
                catch (Exception e)
                {
                    failure = e;
                    NetLog.Error($"{Log}Restoring the own save from {_backup.Folder} failed: {e}");
                }
            }
            try
            {
                _files?.ClearSandbox();
            }
            catch (Exception e)
            {
                NetLog.Warn($"{Log}Emptying the session sandbox failed: {e.Message}");
            }
            if (failure == null)
                _backup = null;
            _module.OnLocalSaveRestored(hadBundle, failure == null);
            if (failure != null)
                throw new IOException("The player's save files could not be put right: " + failure.Message, failure);
        }

        public void OnSuppressDiskWritesChanged(bool suppress)
        {
            _suppress = suppress;
            NetLog.Info($"{Log}Save writes {(suppress ? "suppressed (host's save loaded)" : "no longer suppressed by the session")}.");
            // A save of the own world still waiting on m_moveLocked would land in the middle of the session.
            if (suppress)
                StopPendingWrite("the session started");
        }

        // ---- Fence -----------------------------------------------------------------------------------

        public void RaiseFence()
        {
            if (!_fence)
                NetLog.Info($"{Log}Post-session fence up: no save writes until the menu re-reads the own save.");
            _fence = true;
            _fenceLoadId = _ctx.SceneLoadId;
            // A WriteSave started in the host's world may still be waiting on m_moveLocked with the host's JSON; its
            // JustGolf branch writes by path after the wait, when the redirect is already off (#71).
            StopPendingWrite("the session ended");
        }

        /// <summary>
        /// Stops SaveManager's pending WriteSave coroutine. The JSON is taken synchronously in SaveRun; WriteSave only
        /// waits while m_moveLocked (and otherwise writes at once, #135), so a pending one exists only mid-dialogue.
        /// A scene change ends it too (SaveManager is per scene, #72, #136).
        /// </summary>
        private void StopPendingWrite(string why)
        {
            if (_pendingWriteField == null)
                return;
            try
            {
                var manager = SaveManager.instance;
                if (manager == null || !(_pendingWriteField.GetValue(manager) is Coroutine pending) || pending == null)
                    return;
                manager.StopCoroutine(pending);
                NetLog.Info($"{Log}Stopped a pending save write ({why}).");
            }
            catch (Exception e)
            {
                _ctx.Patcher.ReportFailure(PendingWriteFeature, e);
            }
        }

        public void DropFence(string why)
        {
            if (!_fence)
                return;
            _fence = false;
            NetLog.Info($"{Log}Post-session fence dropped ({why}); saving works normally again.");
        }

        /// <summary>Plugin load: a crash may have left a sandbox behind.</summary>
        public void ClearSandboxOnLoad()
        {
            try
            {
                _files?.ClearSandbox();
            }
            catch (Exception e)
            {
                NetLog.Warn($"{Log}Emptying the session sandbox failed: {e.Message}");
            }
        }

        // ---- Injection (client) ----------------------------------------------------------------------

        /// <summary>The host's run fitted to this player (5.3); false without a run in the bundle.</summary>
        public bool TryCreateRun(out RunSave run)
        {
            run = default;
            var bundle = _bundle;
            if (bundle == null || !bundle.HasRun || bundle.RunJson.Length == 0)
                return false;
            // Parsed fresh every time: RunSave holds lists and arrays, which must not be shared with the bundle.
            run = JsonUtility.FromJson<RunSave>(bundle.RunJsonText);
            _hostRunKnown = true;
            _hostRunSet = run.m_challengeSetLoaded;
            _hostRunIndex = run.m_currentChallenge;
            if (_useTrackOverride
                && Personalization.TryDecodeTrack(_ctx.Facts.Get(Personalization.TrackKind, Personalization.TrackKey),
                    out string setName, out int index))
            {
                // A host inside a bounty must not drop the client into it: start on the shared main track.
                run.m_challengeSetLoaded = setName;
                run.m_currentChallenge = index;
            }
            if (!bundle.IsJustGolf)
                run.isInLMUGC = false;
            // JustGolf keeps isInLMUGC: SetUpScene then Invokes LoadInLMUGC, which resumes at the first hole whose
            // lmugcScore is below 1 (#65), so all-zero scores start the client's round at hole 1.
            run.lmugcScore = new int[LmugcHoles];
            run.lmugcCurrentMulligans = run.lmugcStartMulligans;
            run.lmugcCurrentNoWinds = run.lmugcStartNoWinds;
            run.lmugcCurrentHoleSkips = run.lmugcStartHoleSkips;
            run.lmugcNoWindTime = 0f;
            run.lmugcUsedAssist = false;
            run.lmugcSaveScumCount = 0;
            return true;
        }

        public bool TryCreateUnlocks(out UnlocksSave unlocks)
        {
            unlocks = default;
            var bundle = _bundle;
            if (bundle == null || bundle.UnlocksJson.Length == 0)
                return false;
            unlocks = JsonUtility.FromJson<UnlocksSave>(bundle.UnlocksJsonText);
            bool haveSession = _sessionSelectedBall >= 0;
            unlocks.selectedBall = Personalization.PickBall(haveSession ? _sessionSelectedBall : _ownSelectedBall,
                haveSession || _haveOwnBall, _ownUnlockedBalls, unlocks.unlockedBalls, unlocks.selectedBall);
            return true;
        }

        public bool TryCreateScoreCard(out ScoreCardSave scoreCard)
        {
            scoreCard = default;
            var bundle = _bundle;
            if (bundle == null || bundle.ScoreCardJson.Length == 0)
                return false;
            scoreCard = JsonUtility.FromJson<ScoreCardSave>(bundle.ScoreCardJsonText);
            return true;
        }

        /// <summary>The host's own challenge set and index from the last injected run (before the track override).</summary>
        public bool TryGetHostTrack(out string setName, out int index)
        {
            setName = _hostRunSet;
            index = _hostRunIndex;
            return _hostRunKnown && _bundle != null;
        }

        /// <summary>The player picked a ball during the session (seen at the skipped SaveUnlocks).</summary>
        public void RememberSessionBall(int selected)
        {
            if (_bundle != null && selected >= 0)
                _sessionSelectedBall = selected;
        }

        /// <summary>The track override keeps breaking the load (the game reloads Main): use the host's run as is.</summary>
        public void DisableTrackOverride()
        {
            if (!_useTrackOverride)
                return;
            _useTrackOverride = false;
            NetLog.Warn($"{Log}Loading the host's world keeps failing; injecting the host's run without the shared-track override.");
        }

        public string SandboxPath(string realPath) => _files.SandboxFile(Path.GetFileName(realPath));

        public string SandboxFolder => _files.SandboxDirectory + "/";

        // ---- Host capture ----------------------------------------------------------------------------

        public void ResetHostCache() => _hostCache = null;

        /// <summary>Host: captures the settled save now, so a capture during the next load has something to stand in.</summary>
        public void RefreshHostCache() => BuildHostSave();

        private SessionSaveBundle BuildHostSave()
        {
            var scene = _ctx.Scene;
            uint epoch = _module.HostEpoch;
            byte mode = _module.CurrentModeByte();
            // A player who just left someone else's session may still hold that host's save in memory.
            if (_fence || _bundle != null)
                return EmptySave(GameScene.None, epoch, mode);
            // Mid-load the new scene's SaveManager has not read the save yet; the last settled save stands in.
            if (!_module.HostCaptureReady)
                return _hostCache != null ? _hostCache.CloneSave() : EmptySave(GameScene.None, epoch, mode);

            var manager = SaveManager.instance;
            if (scene == GameScene.Credits && !_creditsLogged)
            {
                // Runtime check (#81): CREDITS reads the save through SaveReader; whether it has a SaveManager is scene data.
                _creditsLogged = true;
                NetLog.Info($"{Log}CREDITS {(manager != null ? "has a SaveManager: its run is captured" : "has no SaveManager: the last world save stands in")}.");
            }
            if (manager == null)
            {
                // CREDITS (no SaveManager): the save is what it was when the host left the world.
                if (_hostCache == null)
                    return EmptySave(scene, epoch, mode);
                var cached = _hostCache.CloneSave();
                cached.HostScene = scene;
                cached.SceneEpoch = epoch;
                return cached;
            }

            var bundle = new SessionSaveBundle
            {
                HostScene = scene,
                SceneEpoch = epoch,
                Mode = mode,
                HasRun = true,
                RunJson = SessionSaveBundle.Encode(JsonUtility.ToJson(CopyHostRun(manager))),
                UnlocksJson = SessionSaveBundle.Encode(JsonUtility.ToJson(manager.m_unlocks)),
                ScoreCardJson = SessionSaveBundle.Encode(JsonUtility.ToJson(manager.m_scoreCardSave))
            };
            _hostCache = bundle.CloneSave();
            return bundle;
        }

        private static SessionSaveBundle EmptySave(GameScene scene, uint epoch, byte mode) => new SessionSaveBundle
        {
            HostScene = scene,
            SceneEpoch = epoch,
            Mode = mode,
            HasRun = false
        };

        /// <summary>The host's run with the live world folded in (5.1). A struct copy; m_run is not changed.</summary>
        private RunSave CopyHostRun(SaveManager manager)
        {
            RunSave run = manager.m_run;
            if (!_ctx.WorldReady || _ctx.Scene != GameScene.Main)
                return run;

            var saved = LiveSavedObjects();
            if (saved != null)
                run.m_savedObjects = saved;
            try
            {
                var progress = ProgressManager.instance;
                if (_bankField != null && progress != null && _bankField.GetValue(progress) is Bank bank && bank != null)
                    run.m_bankBalance = bank.m_balance;
            }
            catch (Exception e)
            {
                _ctx.Patcher.ReportFailure(BankFeature, e);
            }
            var controller = MoveAndHitController.instance;
            var character = controller != null ? controller.m_fpsCharacter : null;
            if (character != null)
            {
                var position = character.GetPosition();
                var rotation = character.GetRotation();
                if (GameWire.IsFinite(position))
                {
                    run.m_playerPosition = position;
                    run.m_playerRotation = rotation;
                }
            }
            return run;
        }

        private SavedObjectData[] LiveSavedObjects()
        {
            var all = GameIds.Saveables;
            if (all.Count == 0)
                return null;
            var ids = new List<string>(all.Count);
            foreach (var pair in all)
            {
                if (pair.Value != null)
                    ids.Add(pair.Key);
            }
            ids.Sort(StringComparer.Ordinal);
            var result = new List<SavedObjectData>(ids.Count);
            int failures = 0;
            foreach (var id in ids)
            {
                try
                {
                    var data = all[id].GetSaveData();
                    if (data != null)
                        result.Add(data);
                }
                catch (Exception e)
                {
                    if (failures++ == 0)
                        _ctx.WarnOnce("capture.saveable", $"{Log}SaveableObject {id}.GetSaveData failed: {e.Message}");
                }
            }
            return result.ToArray();
        }

        // ---- Client helpers --------------------------------------------------------------------------

        private void RememberOwnSave()
        {
            _haveOwnBall = false;
            _ownSelectedBall = -1;
            _ownUnlockedBalls = null;
            var manager = SaveManager.instance;
            // Right after another session the memory may still hold that host's unlocks; the disk copy is the player's.
            if (manager != null && !_fence)
            {
                var unlocks = manager.m_unlocks;
                _ownSelectedBall = unlocks.selectedBall;
                _ownUnlockedBalls = unlocks.unlockedBalls != null ? new List<bool>(unlocks.unlockedBalls) : null;
                _haveOwnBall = true;
                return;
            }
            try
            {
                string path = Path.Combine(_backup.Folder, UnlocksFile);
                if (!File.Exists(path))
                    return;
                var unlocks = JsonUtility.FromJson<UnlocksSave>(File.ReadAllText(path));
                _ownSelectedBall = unlocks.selectedBall;
                _ownUnlockedBalls = unlocks.unlockedBalls != null ? new List<bool>(unlocks.unlockedBalls) : null;
                _haveOwnBall = true;
            }
            catch (Exception e)
            {
                NetLog.Warn($"{Log}Reading the own ball choice failed ({e.Message}); the host's ball is used.");
            }
        }

        private void WriteSandbox(SessionSaveBundle bundle)
        {
            if (_files == null)
                return;
            try
            {
                _files.ClearSandbox();
                // Game code that reads the save by path (SaveReader in CREDITS) sees the host's files.
                if (bundle.HasRun)
                    _files.WriteSandbox(bundle.IsJustGolf ? NormalGolfFile : RunFile, bundle.RunJson);
                if (bundle.UnlocksJson.Length > 0)
                    _files.WriteSandbox(UnlocksFile, bundle.UnlocksJson);
                if (bundle.ScoreCardJson.Length > 0)
                    _files.WriteSandbox(ScoreCardFile, bundle.ScoreCardJson);
            }
            catch (Exception e)
            {
                _ctx.WarnOnce("sandbox.write", $"{Log}Writing the session sandbox failed: {e.Message}");
            }
        }

        private static string Join(List<string> names) => names.Count == 0 ? "none" : string.Join(", ", names.ToArray());
    }
}
