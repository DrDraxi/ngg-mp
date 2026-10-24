using System;
using HarmonyLib;
using NGGMultiplayer.Core.Diagnostics;

namespace NGGMultiplayer.Game.Patches
{
    /// <summary>
    /// Applies every Harmony patch class in this folder (classes marked [HarmonyPatch]). Game hooks for shots,
    /// progress and the save system go here once the game's assemblies are available.
    /// </summary>
    internal static class GamePatches
    {
        public static Harmony Apply(string harmonyId)
        {
            var harmony = new Harmony(harmonyId);
            try
            {
                harmony.PatchAll(typeof(GamePatches).Assembly);
            }
            catch (Exception e)
            {
                // A broken patch (game update) must not take the rest of the mod down with it.
                NetLog.Error($"[Patches] Applying Harmony patches failed: {e}");
            }
            return harmony;
        }
    }
}
