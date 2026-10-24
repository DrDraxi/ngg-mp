using System;
using Steamworks;

namespace NGGMultiplayer.Steam
{
    /// <summary>
    /// Detects whether the game's Steam API is up. The game owns SteamAPI.Init / Shutdown; the plugin never calls
    /// them, it only waits until the API answers.
    /// </summary>
    internal static class SteamBootstrap
    {
        /// <summary>True when Steam is initialized and a user is logged on; otherwise a reason for the UI.</summary>
        public static bool TryProbe(out CSteamID localId, out string reason)
        {
            localId = CSteamID.Nil;
            try
            {
                if (!SteamAPI.IsSteamRunning())
                {
                    reason = "Steam is not running.";
                    return false;
                }
                if (!SteamUser.BLoggedOn())
                {
                    reason = "Not logged on to Steam.";
                    return false;
                }
                localId = SteamUser.GetSteamID();
                if (!localId.IsValid())
                {
                    reason = "Steam returned no user id.";
                    return false;
                }
                reason = string.Empty;
                return true;
            }
            catch (InvalidOperationException)
            {
                // Steamworks.NET throws this until the game has called SteamAPI.Init.
                reason = "Waiting for the game to initialize Steam.";
                return false;
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException
                || e is TypeLoadException || e is MissingMethodException || e is BadImageFormatException)
            {
                reason = "The game's Steam API is incompatible with this mod: " + e.Message;
                return false;
            }
        }

        /// <summary>True for failures that retrying will not fix.</summary>
        public static bool IsPermanent(string reason) =>
            reason != null && reason.StartsWith("The game's Steam API is incompatible", StringComparison.Ordinal);
    }
}
