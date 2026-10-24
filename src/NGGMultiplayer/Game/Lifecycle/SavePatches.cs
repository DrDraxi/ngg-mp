using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NGGMultiplayer.Core.Diagnostics;
using UnityEngine.SceneManagement;

namespace NGGMultiplayer.Game.Lifecycle
{
    /// <summary>
    /// SaveManager hooks for the session save (docs/GAME_INTEGRATION.md 5.4, 5.5):
    /// <list type="bullet">
    /// <item><see cref="ProtectFeature"/>: skip every save write while <see cref="SessionSaveStore.WritesBlocked"/>, and
    /// redirect the save paths to the sandbox while the session save is active. These fail <b>closed</b>: a skip
    /// keeps skipping whatever happens inside it.</item>
    /// <item><see cref="InjectFeature"/>: hand the game the host's save instead of reading the player's files.</item>
    /// </list>
    /// Both are all-or-nothing groups; a client refuses to join unless both installed completely. Offline (and on
    /// the host) every hook lets the game run unchanged.
    /// <para>Optional extras (a client still joins without them): <see cref="SetUpFeature"/> keeps an injected run off
    /// SetUpScene's broken-save path and away from Instantiate(null) (#74), and <see cref="ReaderFeature"/> points
    /// SaveReader (CREDITS) at the sandbox (#30).</para>
    /// <para>Writers the game has besides the guarded ones (#31, #71, #144): SetUpScene's broken-save path (writes
    /// run2.txt from the backup or deletes run2.txt + run2_temp.txt, then reloads Main; kept unreachable by
    /// <see cref="SetUpFeature"/>, and its paths are the redirected getters' files), the JustGolf branch of
    /// WriteSave (File.WriteAllText of m_normalGolfSavePath after the m_moveLocked wait; a write still pending when
    /// protection changes is stopped by the store), SaveGameSettings (config.txt: the player's own settings, stays
    /// writable) and RebindSaveLoad (PlayerPrefs "rebinds": the player's own key bindings, stays writable).
    /// AddScoreToCard writes scoreCard.txt itself, bypassing WriteSave (#139): skipped whole. UpgradeDemoSave writes
    /// nothing (it only edits m_run); it is skipped so the host's run in memory stays the host's. The game does not
    /// save on application quit (#49, #108).</para>
    /// </summary>
    internal static class SavePatches
    {
        public const string ProtectFeature = "lc.save.protect";
        public const string InjectFeature = "lc.save.inject";
        public const string SetUpFeature = "lc.save.setup";
        public const string ReaderFeature = "lc.save.reader";

        private const string Log = "[Lifecycle] ";

        public static bool ProtectInstalled { get; private set; }

        public static bool InjectInstalled { get; private set; }

        public static void Install(GameContext ctx)
        {
            var p = ctx.Patcher;
            var type = typeof(SaveManager);
            var self = typeof(SavePatches);
            var none = Type.EmptyTypes;

            bool protect =
                p.Prefix(ProtectFeature, type, "WriteSave", new[] { typeof(string) }, self, nameof(WriteSavePrefix))
                && p.Prefix(ProtectFeature, type, "SafeWrite", new[] { typeof(string), typeof(string), typeof(string) }, self, nameof(SkipWhenBlocked))
                && p.Prefix(ProtectFeature, type, "SaveUnlocks", none, self, nameof(SaveUnlocksPrefix))
                && p.Prefix(ProtectFeature, type, "AddScoreToCard", new[] { typeof(int) }, self, nameof(SkipWhenBlocked))
                && p.Prefix(ProtectFeature, type, "DeleteRun", none, self, nameof(SkipWhenBlocked))
                && p.Prefix(ProtectFeature, type, "DeleteEverything", none, self, nameof(SkipWhenBlocked))
                && p.Prefix(ProtectFeature, type, "CreateBackupOfSave", new[] { typeof(string), typeof(string) }, self, nameof(SkipWhenBlocked))
                && p.Prefix(ProtectFeature, type, "UpgradeDemoSave", none, self, nameof(SkipWhenBlockedOrInjected))
                && Redirect(p, type, "m_runSavePath", nameof(RedirectFilePostfix))
                && Redirect(p, type, "m_runSavePath_writeBackUp", nameof(RedirectFilePostfix))
                && Redirect(p, type, "m_normalGolfSavePath", nameof(RedirectFilePostfix))
                && Redirect(p, type, "m_scorecardSavePath", nameof(RedirectFilePostfix))
                && Redirect(p, type, "m_unlocksSavePath", nameof(RedirectFilePostfix))
                && Redirect(p, type, "m_backupSavePath", nameof(RedirectFolderPostfix));
            ProtectInstalled = protect && p.IsEnabled(ProtectFeature);
            if (!ProtectInstalled)
                p.Disable(ProtectFeature, "a save write guard did not install; joining sessions is disabled to protect the save");

            bool inject =
                p.Prefix(InjectFeature, type, "LoadRunSave", none, self, nameof(LoadRunPrefix))
                && p.Prefix(InjectFeature, type, "LoadStoryRun", none, self, nameof(LoadRunPrefix))
                && p.Prefix(InjectFeature, type, "LoadNormalGolfRun", none, self, nameof(LoadRunPrefix))
                && p.Prefix(InjectFeature, type, "GetBackUpStorySave", none, self, nameof(BackupRunPrefix))
                && p.Prefix(InjectFeature, type, "GetUnlocksSave", none, self, nameof(GetUnlocksPrefix))
                && p.Prefix(InjectFeature, type, "GetScoreCardSave", none, self, nameof(GetScoreCardPrefix))
                && p.Prefix(InjectFeature, type, "HasRun", none, self, nameof(HasRunPrefix))
                && p.Prefix(InjectFeature, type, "HasStartedNine", none, self, nameof(HasStartedNinePrefix));
            InjectInstalled = inject && p.IsEnabled(InjectFeature);
            if (!InjectInstalled)
                p.Disable(InjectFeature, "a save injection hook did not install; joining sessions is disabled");

            if (!p.Prefix(SetUpFeature, type, "SetUpScene", none, self, nameof(SetUpScenePrefix)))
                NetLog.Warn($"{Log}The SetUpScene run check did not install: a host run the game considers broken would make it rewrite the (redirected) save and reload Main.");
            InstallReaderRedirect(p);
        }

        /// <summary>
        /// One of the write guards (they read only static flags, so they keep working after the modules are gone).
        /// The plugin leaves these installed on unload while the host's save may still be in memory.
        /// </summary>
        public static bool IsWriteGuard(MethodInfo patch) =>
            patch != null && patch.DeclaringType == typeof(SavePatches)
            && (patch.Name == nameof(WriteSavePrefix) || patch.Name == nameof(SkipWhenBlocked)
                || patch.Name == nameof(SaveUnlocksPrefix) || patch.Name == nameof(SkipWhenBlockedOrInjected));

        private static bool Redirect(GamePatcher p, Type type, string property, string patch) =>
            p.Postfix(ProtectFeature, p.PropertyGetter(ProtectFeature, type, property), typeof(SavePatches), patch);

        /// <summary>
        /// SaveReader (CREDITS) builds persistentDataPath + "/run2.txt" in a property of its own (#30), not through
        /// SaveManager's redirected getters. Its string getters are postfixed so a client in session reads the host's
        /// run from the sandbox; only a result naming a protected save file is changed. Read-only, so optional.
        /// </summary>
        private static void InstallReaderRedirect(GamePatcher p)
        {
            if (!p.IsEnabled(ReaderFeature))
                return;
            var patched = new List<string>();
            try
            {
                const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
                    | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                foreach (var property in typeof(SaveReader).GetProperties(all))
                {
                    var getter = property.GetGetMethod(true);
                    if (getter == null || property.PropertyType != typeof(string) || getter.GetParameters().Length != 0)
                        continue;
                    if (p.Postfix(ReaderFeature, getter, typeof(SavePatches), nameof(RedirectReaderPostfix)))
                        patched.Add(property.Name);
                }
            }
            catch (Exception e)
            {
                p.Disable(ReaderFeature, "looking up SaveReader's path property failed: " + e.Message);
                return;
            }
            if (patched.Count > 0)
                NetLog.Info($"{Log}SaveReader path redirect on: {string.Join(", ", patched.ToArray())}.");
            else
                NetLog.Warn($"{Log}SaveReader has no string property to redirect; a client in CREDITS shows its own save's numbers.");
        }

        private static SessionSaveStore Store => LifecycleSaveModule.Instance?.Store;

        // ---- Write protection (fail closed) ----------------------------------------------------------

        // Reads only static flags, so nothing in here can throw: a blocked write is always skipped.
        private static bool SkipWhenBlocked() => !SessionSaveStore.WritesBlocked;

        private static bool SkipWhenBlockedOrInjected()
        {
            if (SessionSaveStore.WritesBlocked)
                return false;
            var store = Store;
            return store == null || !store.SessionSaveActive;
        }

        private static bool WriteSavePrefix(ref IEnumerator __result)
        {
            if (!SessionSaveStore.WritesBlocked)
                return true;
            // SaveRun hands the result to StartCoroutine, which must get a valid enumerator.
            __result = EmptyRoutine();
            return false;
        }

        private static bool SaveUnlocksPrefix(SaveManager __instance)
        {
            if (!SessionSaveStore.WritesBlocked)
                return true;
            try
            {
                // Ball selection saves the unlocks; remember the choice so a later reload of the host's world keeps it.
                if (__instance != null)
                    Store?.RememberSessionBall(__instance.m_unlocks.selectedBall);
            }
            catch (Exception e)
            {
                GameContext.Current?.Patcher.ReportFailure(ProtectFeature + ".ball", e);
            }
            return false;
        }

        private static IEnumerator EmptyRoutine()
        {
            yield break;
        }

        private static void RedirectFilePostfix(ref string __result)
        {
            var store = Store;
            if (store == null || !store.SessionSaveActive || string.IsNullOrEmpty(__result))
                return;
            try
            {
                __result = store.SandboxPath(__result);
            }
            catch (Exception e)
            {
                // The write guards still hold; only reads by path see the player's own files.
                GameContext.Current?.Patcher.ReportFailure(ProtectFeature + ".redirect", e);
            }
        }

        private static void RedirectReaderPostfix(ref string __result)
        {
            var store = Store;
            if (store == null || !store.SessionSaveActive || string.IsNullOrEmpty(__result))
                return;
            try
            {
                if (LocalSaveFiles.IsProtectedName(Path.GetFileName(__result)))
                    __result = store.SandboxPath(__result);
            }
            catch (Exception e)
            {
                GameContext.Current?.Patcher.ReportFailure(ReaderFeature, e);
            }
        }

        private static void RedirectFolderPostfix(ref string __result)
        {
            var store = Store;
            if (store == null || !store.SessionSaveActive)
                return;
            try
            {
                __result = store.SandboxFolder;
            }
            catch (Exception e)
            {
                GameContext.Current?.Patcher.ReportFailure(ProtectFeature + ".redirect", e);
            }
        }

        // ---- Injection (client, while the session save is active) -------------------------------------

        private static bool LoadRunPrefix(SaveManager __instance, ref RunSave __result)
        {
            var store = Store;
            if (store == null || !store.SessionSaveActive || __instance == null)
                return true;
            try
            {
                if (!store.TryCreateRun(out var run))
                    return true; // no run in the bundle: the game reads the (redirected, empty) sandbox and starts fresh
                var bundle = store.Bundle;
                LifecycleSaveModule.Instance?.ApplyMode(__instance, bundle.Mode);
                // The originals assign m_run themselves (#29, #69); with them skipped, the prefix has to.
                __instance.m_run = run;
                __result = run;
                LifecycleSaveModule.Instance?.OnRunInjected(__instance, bundle);
            }
            catch (Exception e)
            {
                // Fail open onto the sandbox, which holds the host's run as written at ApplyBundle. That world still
                // comes from this bundle, so it counts as injected (no follow-up reload).
                GameContext.Current?.Patcher.ReportFailure(InjectFeature, e);
                NetLog.Error($"{Log}Injecting the host's run failed; the game reads the sandbox copy instead.");
                var bundle = store.Bundle;
                if (bundle != null && bundle.HasRun)
                    LifecycleSaveModule.Instance?.OnRunInjected(__instance, bundle);
                return true;
            }
            SetUpInjectedRun(__instance);
            return false;
        }

        private static readonly MethodInfo SetUpSceneMethod = typeof(SaveManager).GetMethod("SetUpScene",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);

        /// <summary>
        /// The skipped originals end with SetUpScene() in Main, which applies the run to the world: the unlocked panels
        /// and clubs, bank, player position, bounty tiers, RFID doors. Without it a client's UI stays locked, because the
        /// facts already match the injected run and nothing replays the unlocks. Kept apart from the injection's
        /// fail-open: a throw in SetUpScene must not make the game load the run a second time.
        /// </summary>
        private static void SetUpInjectedRun(SaveManager manager)
        {
            if (!RunGuard.LoadSetsUpScene(SceneManager.GetActiveScene().name))
                return;
            if (SetUpSceneMethod == null)
            {
                NetLog.Error($"{Log}SaveManager.SetUpScene not found; the host's unlocks and bank are not applied to this world.");
                return;
            }
            try
            {
                SetUpSceneMethod.Invoke(manager, null);
            }
            catch (TargetInvocationException e)
            {
                NetLog.Error($"{Log}SetUpScene on the host's run threw: {e.InnerException ?? e}");
            }
        }

        /// <summary>
        /// GetBackUpStorySave returns a run without assigning m_run (#69); its only caller is SetUpScene's
        /// broken-save path, which <see cref="SetUpScenePrefix"/> keeps an injected run away from.
        /// </summary>
        private static bool BackupRunPrefix(SaveManager __instance, ref RunSave __result)
        {
            var store = Store;
            if (store == null || !store.SessionSaveActive || __instance == null)
                return true;
            try
            {
                if (!store.TryCreateRun(out var run))
                    return true;
                GameContext.Current?.WarnOnce("save.backuprun",
                    $"{Log}The game asked for its backup save while the host's save is loaded (broken-save path); it gets the host's run.");
                __result = run;
                return false;
            }
            catch (Exception e)
            {
                GameContext.Current?.Patcher.ReportFailure(InjectFeature, e);
                return true;
            }
        }

        /// <summary>
        /// Client with the host's save: the run SetUpScene is about to apply must not send it down its broken-save
        /// path (file writes or deletes, then a reload of Main) or into Instantiate(null) for an unknown set (#74).
        /// </summary>
        private static bool SetUpScenePrefix(SaveManager __instance)
        {
            var store = Store;
            if (store == null || !store.SessionSaveActive || __instance == null)
                return true;
            try
            {
                return LifecycleSaveModule.Instance?.GuardInjectedRun(__instance) ?? true;
            }
            catch (Exception e)
            {
                GameContext.Current?.Patcher.ReportFailure(SetUpFeature, e);
                return true;
            }
        }

        private static bool GetUnlocksPrefix(SaveManager __instance, ref UnlocksSave __result)
        {
            var store = Store;
            if (store == null || !store.SessionSaveActive || __instance == null)
                return true;
            try
            {
                if (!store.TryCreateUnlocks(out var unlocks))
                    return true;
                __instance.m_unlocks = unlocks;
                __result = unlocks;
                return false;
            }
            catch (Exception e)
            {
                GameContext.Current?.Patcher.ReportFailure(InjectFeature, e);
                return true;
            }
        }

        private static bool GetScoreCardPrefix(SaveManager __instance, ref ScoreCardSave __result)
        {
            var store = Store;
            if (store == null || !store.SessionSaveActive || __instance == null)
                return true;
            try
            {
                if (!store.TryCreateScoreCard(out var scoreCard))
                    return true;
                __instance.m_scoreCardSave = scoreCard;
                __result = scoreCard;
                return false;
            }
            catch (Exception e)
            {
                GameContext.Current?.Patcher.ReportFailure(InjectFeature, e);
                return true;
            }
        }

        private static bool HasRunPrefix(ref bool __result)
        {
            var bundle = Store?.Bundle;
            if (bundle == null)
                return true;
            __result = bundle.HasRun && !bundle.IsJustGolf;
            return false;
        }

        private static bool HasStartedNinePrefix(ref bool __result)
        {
            var bundle = Store?.Bundle;
            if (bundle == null)
                return true;
            __result = bundle.HasRun && bundle.IsJustGolf;
            return false;
        }
    }
}
