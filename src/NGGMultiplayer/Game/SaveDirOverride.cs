using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using NGGMultiplayer.Core.Diagnostics;
using UnityEngine;

namespace NGGMultiplayer.Game
{
    /// <summary>
    /// <c>[Debug] SaveDirOverride</c>: test copies of the game keep every save file in a folder of their own, so they
    /// never read or write the player's real saves (AppData/LocalLow/Luke Muscat/Normal Golf Game). SaveManager builds
    /// each save path as persistentDataPath + "/name" in a getter (docs/gamemap/05-signatures.md), and SaveReader
    /// (CREDITS) has a getter of its own (#30); postfixes on all of them swap the persistentDataPath prefix for the
    /// override, before any other mod patch sees the path (the session sandbox redirects run after it). The mod's own
    /// readers use <see cref="Effective"/>. Installed once at start, before the game modules.
    /// </summary>
    internal static class SaveDirOverride
    {
        private const string Log = "[SaveDir] ";

        private static readonly string[] SaveManagerGetters =
        {
            "m_runSavePath", "m_runSavePath_writeBackUp", "m_normalGolfSavePath", "m_scorecardSavePath",
            "m_unlocksSavePath", "m_backupSavePath", "m_settingsSavePath"
        };

        private static string _original;

        /// <summary>The override folder (full path), or null when the game's own folder is used.</summary>
        public static string Directory { get; private set; }

        /// <summary>The folder the save files are in: the override, else Application.persistentDataPath.</summary>
        public static string Effective => Directory ?? Application.persistentDataPath;

        /// <summary>
        /// Turns the override on for <paramref name="folder"/>. False (and an error in the log) when a getter could not be
        /// patched: then some save path would still name the real folder, so the caller must not start the game modules.
        /// </summary>
        public static bool Install(Harmony harmony, string folder)
        {
            if (string.IsNullOrWhiteSpace(folder))
                return true;
            string full = Path.GetFullPath(folder.Trim());
            System.IO.Directory.CreateDirectory(full);
            _original = Normalize(Application.persistentDataPath);
            Directory = full;

            var postfix = new HarmonyMethod(AccessTools.Method(typeof(SaveDirOverride), nameof(RewritePostfix)))
            {
                priority = Priority.First
            };
            var missing = new List<string>();
            foreach (var name in SaveManagerGetters)
            {
                var getter = AccessTools.PropertyGetter(typeof(SaveManager), name);
                if (getter == null || !TryPatch(harmony, getter, postfix))
                    missing.Add("SaveManager." + name);
            }
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
                | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            int readers = 0;
            foreach (var property in typeof(SaveReader).GetProperties(all))
            {
                var getter = property.GetGetMethod(true);
                if (getter == null || property.PropertyType != typeof(string) || getter.GetParameters().Length != 0)
                    continue;
                if (TryPatch(harmony, getter, postfix))
                    readers++;
                else
                    missing.Add("SaveReader." + property.Name);
            }
            if (missing.Count > 0)
            {
                NetLog.Error($"{Log}SaveDirOverride is set, but {string.Join(", ", missing.ToArray())} could not be redirected; " +
                    "those paths would still name the real save folder.");
                return false;
            }
            NetLog.Warn($"{Log}Save folder overridden: {full} (instead of {Application.persistentDataPath}); " +
                $"{SaveManagerGetters.Length} SaveManager path(s) and {readers} SaveReader path(s) redirected.");
            return true;
        }

        private static bool TryPatch(Harmony harmony, MethodBase getter, HarmonyMethod postfix)
        {
            try
            {
                harmony.Patch(getter, postfix: postfix);
                return true;
            }
            catch (Exception e)
            {
                NetLog.Error($"{Log}Patching {getter.DeclaringType?.Name}.{getter.Name} failed: {e.Message}");
                return false;
            }
        }

        /// <summary>The getter's path with the game's folder swapped for the override (paths elsewhere stay).</summary>
        public static string Rewrite(string path)
        {
            if (Directory == null || string.IsNullOrEmpty(path))
                return path;
            string normalized = Normalize(path);
            if (!normalized.StartsWith(_original, StringComparison.OrdinalIgnoreCase)
                || (normalized.Length > _original.Length && normalized[_original.Length] != '/'))
                return path;
            string rest = normalized.Substring(_original.Length).TrimStart('/');
            if (rest.Length == 0)
                return path.EndsWith("/", StringComparison.Ordinal) || path.EndsWith("\\", StringComparison.Ordinal)
                    ? Directory + Path.DirectorySeparatorChar
                    : Directory;
            return Path.Combine(Directory, rest.Replace('/', Path.DirectorySeparatorChar));
        }

        private static void RewritePostfix(ref string __result)
        {
            try
            {
                __result = Rewrite(__result);
            }
            catch (Exception e)
            {
                NetLog.Error($"{Log}Rewriting '{__result}' failed: {e.Message}");
            }
        }

        private static string Normalize(string path) => (path ?? string.Empty).Replace('\\', '/').TrimEnd('/');
    }
}
