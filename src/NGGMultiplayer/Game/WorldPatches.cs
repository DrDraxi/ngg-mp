using System;

namespace NGGMultiplayer.Game
{
    /// <summary>
    /// The one game hook shared by every module: SaveManager.SetUpScene (private), which applies the loaded save to
    /// "Main". Its end is <see cref="GameContext.WorldReady"/>. Without it the context falls back to a timer.
    /// </summary>
    internal static class WorldPatches
    {
        public const string Feature = "world.ready";

        /// <summary>True if SetUpScene was hooked.</summary>
        public static bool Install(GameContext context)
        {
            var target = context.Patcher.Method(Feature, typeof(SaveManager), "SetUpScene", Type.EmptyTypes);
            if (target == null)
                return false;
            bool prefix = context.Patcher.Prefix(Feature, target, typeof(WorldPatches), nameof(SetUpScenePrefix));
            bool postfix = context.Patcher.Postfix(Feature, target, typeof(WorldPatches), nameof(SetUpScenePostfix));
            return prefix && postfix;
        }

        private static void SetUpScenePrefix()
        {
            var context = GameContext.Current;
            if (context == null)
                return;
            try
            {
                context.OnSetUpSceneStarting();
            }
            catch (Exception e)
            {
                context.Patcher.ReportFailure(Feature, e);
            }
        }

        private static void SetUpScenePostfix()
        {
            var context = GameContext.Current;
            if (context == null)
                return;
            try
            {
                context.OnSetUpSceneFinished();
            }
            catch (Exception e)
            {
                context.Patcher.ReportFailure(Feature, e);
            }
        }
    }
}
