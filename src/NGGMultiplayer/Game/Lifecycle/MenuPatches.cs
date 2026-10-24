using System;
using NGGMultiplayer.Core.Diagnostics;

namespace NGGMultiplayer.Game.Lifecycle
{
    /// <summary>
    /// Menu2 and pause-menu hooks (docs/GAME_INTEGRATION.md 4.1, 4.3, 4.5, 7.6): the menu settle point, clients
    /// blocked from starting a game, quit-to-menu leaving the session, the pause menu's open/closed state, and the
    /// settings menu's save deletes. Every hook fails open.
    /// <para>Confirmed (#25, #33, #76): only MainMenu.PlaySetUp loads "Main" from Menu2; PlayGame shows the
    /// existing-save / difficulty screens first and PlayNine / PlayNineButtonPressed start JustGolf. All four are
    /// blocked on a client, which loads "Main" itself with the host's mode already on the shared GameModeState.</para>
    /// </summary>
    internal static class MenuPatches
    {
        public const string MenuStartFeature = "lc.menu.start";
        public const string MenuBlockFeature = "lc.menu.block";
        public const string QuitFeature = "lc.pause.quit";
        public const string PauseFeature = "lc.pause.menu";
        public const string RestartInputFeature = "lc.pause.restart";
        public const string DeleteBlockFeature = "lc.menu.delete";

        private const string Log = "[Lifecycle] ";

        private static readonly string[] BlockedMenuMethods = { "PlayGame", "PlaySetUp", "PlayNineButtonPressed", "PlayNine" };

        /// <summary>SettingsMenu buttons that call DeleteRun / DeleteEverything and then load a scene (#144).</summary>
        private static readonly string[] DeleteMethods = { "DeleteStorySave", "DeleteEverything" };

        private static bool _escapeTargetLogged;

        public static bool MenuStartHooked { get; private set; }

        public static void Install(GameContext ctx)
        {
            var p = ctx.Patcher;
            var self = typeof(MenuPatches);
            var none = Type.EmptyTypes;

            MenuStartHooked = p.Postfix(MenuStartFeature, typeof(MainMenu), "Start", none, self, nameof(MainMenuStartPostfix));
            // Each separately: a missing one leaves the others blocking.
            foreach (var method in BlockedMenuMethods)
                p.Prefix(MenuBlockFeature + "." + method, typeof(MainMenu), method, none, self, nameof(BlockPlayPrefix));
            foreach (var method in DeleteMethods)
                p.Prefix(DeleteBlockFeature + "." + method, typeof(SettingsMenu), method, none, self, nameof(BlockDeletePrefix));

            p.Prefix(QuitFeature, typeof(PauseMenu), "QuitToMenu", none, self, nameof(QuitToMenuPrefix));
            p.Postfix(PauseFeature, typeof(PlayerPressedEscape), "OpenMenu", none, self, nameof(OpenMenuPostfix));
            p.Postfix(PauseFeature + ".exit", typeof(PauseMenu), "Exit", none, self, nameof(PauseExitPostfix));

            // Vanilla's RestartInput bails at timeScale 0 (#59); in session the pause keeps time running.
            var restart = p.Method(RestartInputFeature, typeof(HitSequeneceManager), "RestartInput", none);
            if (restart != null && restart.ReturnType != typeof(void))
                p.Disable(RestartInputFeature, "HitSequeneceManager.RestartInput does not return void");
            else
                p.Prefix(RestartInputFeature, restart, self, nameof(RestartInputPrefix));
        }

        private static void MainMenuStartPostfix()
        {
            var module = LifecycleSaveModule.Instance;
            var ctx = GameContext.Current;
            if (module == null || ctx == null)
                return;
            try
            {
                module.OnMainMenuStarted();
            }
            catch (Exception e)
            {
                ctx.Patcher.ReportFailure(MenuStartFeature, e);
            }
        }

        private static bool BlockPlayPrefix()
        {
            var module = LifecycleSaveModule.Instance;
            var ctx = GameContext.Current;
            if (module == null || ctx == null)
                return true;
            try
            {
                return !module.BlockMenuPlay();
            }
            catch (Exception e)
            {
                ctx.Patcher.ReportFailure(MenuBlockFeature, e);
                return true;
            }
        }

        private static bool BlockDeletePrefix()
        {
            var module = LifecycleSaveModule.Instance;
            var ctx = GameContext.Current;
            if (module == null || ctx == null)
                return true;
            try
            {
                return !module.BlockSaveDelete();
            }
            catch (Exception e)
            {
                // The deletes themselves stay guarded (SaveManager.DeleteRun / DeleteEverything skips).
                ctx.Patcher.ReportFailure(DeleteBlockFeature, e);
                return true;
            }
        }

        private static void QuitToMenuPrefix()
        {
            var module = LifecycleSaveModule.Instance;
            var ctx = GameContext.Current;
            if (module == null || ctx == null || !ctx.Patcher.IsEnabled(QuitFeature))
                return;
            try
            {
                module.OnQuitToMenu();
            }
            catch (Exception e)
            {
                ctx.Patcher.ReportFailure(QuitFeature, e);
            }
        }

        private static void OpenMenuPostfix(PlayerPressedEscape __instance)
        {
            var module = LifecycleSaveModule.Instance;
            var ctx = GameContext.Current;
            if (module == null || ctx == null || !ctx.Patcher.IsEnabled(PauseFeature))
                return;
            try
            {
                LogEscapeTargetOnce(__instance);
                module.Pause?.OnMenuOpened();
            }
            catch (Exception e)
            {
                ctx.Patcher.ReportFailure(PauseFeature, e);
            }
        }

        private static void PauseExitPostfix()
        {
            var module = LifecycleSaveModule.Instance;
            var ctx = GameContext.Current;
            if (module == null || ctx == null)
                return;
            try
            {
                module.Pause?.OnMenuClosed("closed");
            }
            catch (Exception e)
            {
                ctx.Patcher.ReportFailure(PauseFeature + ".exit", e);
            }
        }

        private static bool RestartInputPrefix()
        {
            var pause = LifecycleSaveModule.Instance?.Pause;
            return pause == null || !pause.Holding;
        }

        /// <summary>Runtime check (#58, #147): OpenMenu activates m_objectToEnable, presumably the PauseMenu.</summary>
        private static void LogEscapeTargetOnce(PlayerPressedEscape escape)
        {
            if (_escapeTargetLogged || escape == null)
                return;
            _escapeTargetLogged = true;
            try
            {
                var target = HarmonyLib.Traverse.Create(escape).Field("m_objectToEnable").GetValue() as UnityEngine.GameObject;
                if (target == null)
                {
                    NetLog.Info($"{Log}PlayerPressedEscape.m_objectToEnable not readable as a GameObject.");
                    return;
                }
                bool hasMenu = target.GetComponentInChildren(typeof(PauseMenu), true) != null;
                NetLog.Info($"{Log}OpenMenu enabled '{target.name}' (active {target.activeInHierarchy}, " +
                    $"{(hasMenu ? "holds the PauseMenu" : "has NO PauseMenu: the close poll may misread the menu")}).");
            }
            catch (Exception e)
            {
                NetLog.Debug($"{Log}Reading PlayerPressedEscape.m_objectToEnable failed: {e.Message}");
            }
        }
    }
}
