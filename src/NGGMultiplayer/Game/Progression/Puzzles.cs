using System;
using System.Reflection;
using System.Text;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Save;
using NGGMultiplayer.Core.Sync;
using UnityEngine;
using Random = UnityEngine.Random;

namespace NGGMultiplayer.Game.Progression
{
    /// <summary>
    /// Achievements guard (6.12) and the seeded / random bits (6.13): the code door's code, the trivia board's
    /// order and new ball skins come from the host so every peer sees the same.
    /// </summary>
    internal sealed class Puzzles
    {
        public const string AchievementsFeature = "pg.achievements";
        public const string RetroFeature = "pg.achievements.retro";
        public const string AuthFeature = "pg.auth";
        public const string TriviaFeature = "pg.trivia";
        public const string BallFeature = "pg.ball";
        private const string Log = "[Progression] ";
        private const double BallRequestInterval = 10.0;

        private readonly ProgressionModule _module;
        private readonly GameContext _ctx;
        private readonly PeerRateLimiter _ballLimiter;
        private MethodInfo _shuffle;
        private double _lastBallRequest = double.NegativeInfinity;

        public Puzzles(ProgressionModule module, GameContext ctx)
        {
            _module = module;
            _ctx = ctx;
            _ballLimiter = new PeerRateLimiter(() => ctx.Now, BallRequestInterval);
        }

        public void Install()
        {
            var patcher = _ctx.Patcher;
            // The single overload UnlockAchievement(AchievementID); AchievementID is top-level (answers 2, 101).
            patcher.Prefix(AchievementsFeature, typeof(SteamManager), "UnlockAchievement", new[] { typeof(AchievementID) },
                typeof(Puzzles), nameof(AchievementPrefix));
            patcher.Prefix(RetroFeature, typeof(SaveManager), "AchievementsRetroUnlock", Type.EmptyTypes,
                typeof(Puzzles), nameof(RetroUnlockPrefix));
            patcher.Postfix(AuthFeature, typeof(Authenticator), "Start", Type.EmptyTypes, typeof(Puzzles), nameof(AuthenticatorStartPostfix));

            _shuffle = patcher.Method(TriviaFeature, typeof(TriviaBoard), "ShuffleButtons", Type.EmptyTypes);
            if (_shuffle != null)
            {
                patcher.Apply(TriviaFeature, _shuffle,
                    prefix: GamePatcher.Hook(typeof(Puzzles), nameof(ShufflePrefix)),
                    finalizer: GamePatcher.Hook(typeof(Puzzles), nameof(ShuffleFinalizer)));
            }

            var unlockBall = patcher.Method(BallFeature, typeof(SaveManager), "UnlockNewBall", Type.EmptyTypes);
            if (unlockBall != null && unlockBall.ReturnType != typeof(bool))
                patcher.Disable(BallFeature, "SaveManager.UnlockNewBall does not return bool");
            else if (unlockBall != null)
                patcher.Prefix(BallFeature, unlockBall, typeof(Puzzles), nameof(UnlockNewBallPrefix));
            _ctx.RegisterRpc(ProgressionRpc.BallUnlock, OnBallUnlock, ValidateBallUnlock);
        }

        public void Clear()
        {
            _ballLimiter.Clear();
            _lastBallRequest = double.NegativeInfinity;
        }

        /// <summary>
        /// Host, session start in "Main": the active trivia boards were shuffled before the session had a seed. The
        /// board builds its buttons in OnEnable and shuffles them there (UnityEngine.Random.Range + SetAsFirstSibling;
        /// OnDisable destroys them; answers 43), so it is re-enabled to rebuild them from the start, seeded like
        /// every client's.
        /// </summary>
        public void ReshuffleBoards()
        {
            if (_shuffle == null || !_ctx.Patcher.IsEnabled(TriviaFeature) || !_module.Live.InWorld)
                return;
            int count = 0;
            foreach (var board in Compat.FindAll<TriviaBoard>(false))
            {
                if (board == null || !board.isActiveAndEnabled)
                    continue;
                try
                {
                    board.enabled = false;
                    board.enabled = true;
                    count++;
                }
                catch (Exception e)
                {
                    _ctx.Patcher.ReportFailure(TriviaFeature, e.GetBaseException());
                }
            }
            if (count > 0)
                _ctx.Info($"{Log}Rebuilt {count} trivia board(s) with the session seed.");
        }

        // ---- Achievements (6.12) --------------------------------------------------------------------

        private static bool AchievementPrefix()
        {
            var ctx = GameContext.Current;
            if (ctx == null || !ctx.InSession || !ctx.Patcher.IsEnabled(AchievementsFeature))
                return true;
            try
            {
                // A replayed remote change (or an effect it queued: story Invokes, challenge completion) is not this
                // player's doing; nor is the host's world loading in. Other hold windows are not checked: the
                // player's own birdie right after some unrelated replay still counts.
                if (ctx.Replay.InReplayEffects || (ctx.IsClient && ctx.IsLoadingWorld))
                    return false;
            }
            catch (Exception e)
            {
                ctx.Patcher.ReportFailure(AchievementsFeature, e);
            }
            return true;
        }

        private static bool RetroUnlockPrefix()
        {
            var ctx = GameContext.Current;
            if (ctx == null || !ctx.Patcher.IsEnabled(RetroFeature))
                return true;
            // It would grant the client the host's progress.
            return !(ctx.IsClientConnection || ctx.Save.SuppressDiskWrites);
        }

        // ---- Authenticator (BO_Code) ----------------------------------------------------------------

        private static void AuthenticatorStartPostfix(Authenticator __instance)
        {
            var ctx = GameContext.Current;
            var module = ProgressionModule.Instance;
            if (ctx == null || module == null || __instance == null || !ctx.InSession || !ctx.Patcher.IsEnabled(AuthFeature))
                return;
            try
            {
                string id = GameIds.ForObject(__instance);
                if (ctx.IsHost)
                {
                    string code = LiveFacts.ReadCode(__instance);
                    if (code != null && Encoding.UTF8.GetByteCount(id) <= SaveDelta.MaxKeyBytes)
                        ctx.Save.ReportLocalFact(FactKinds.Auth, id, FactTable.EncodeString(code));
                    return;
                }
                var host = ctx.Facts.Get(FactKinds.Auth, id);
                if (host != null)
                {
                    using (ctx.Replay.Begin("pg.auth"))
                        module.Applier.SetCode(__instance, FactTable.DecodeString(host));
                }
            }
            catch (Exception e)
            {
                ctx.Patcher.ReportFailure(AuthFeature, e);
            }
        }

        // ---- TriviaBoard ----------------------------------------------------------------------------

        private static bool ShufflePrefix(TriviaBoard __instance, out object __state)
        {
            __state = null;
            var ctx = GameContext.Current;
            var module = ProgressionModule.Instance;
            if (ctx == null || module == null || __instance == null || !ctx.InSession || !ctx.Patcher.IsEnabled(TriviaFeature))
                return true;
            try
            {
                int? seed = module.SessionSeed;
                if (!seed.HasValue)
                    return true;
                uint salt = Crc32.Compute(Encoding.UTF8.GetBytes(GameIds.ForObject(__instance)));
                __state = Random.state;
                Random.InitState(seed.Value ^ (int)salt);
            }
            catch (Exception e)
            {
                ctx.Patcher.ReportFailure(TriviaFeature, e);
            }
            return true;
        }

        private static void ShuffleFinalizer(object __state)
        {
            // Put the game's own random sequence back, so nothing else becomes predictable.
            if (__state is Random.State state)
                Random.state = state;
        }

        // ---- New ball skins -------------------------------------------------------------------------
        // SaveManager.UnlockNewBall (answers 47, 100): only TryTrueEnding calls it, and that is the host's (clients
        // skip it). True: a random locked ball (UnityEngine.Random; of 5, index 0 forced) is unlocked, selected,
        // showNewBallAlert set and the LOCAL unlocks.txt written. False: every ball was unlocked already. A client
        // therefore never runs it: it asks the host, whose unl/balls fact follows.

        private static bool UnlockNewBallPrefix(ref bool __result)
        {
            var ctx = GameContext.Current;
            var module = ProgressionModule.Instance;
            if (ctx == null || module == null || !ctx.IsClient || !ctx.Patcher.IsEnabled(BallFeature))
                return true;
            try
            {
                module.Puzzles.RequestBall();
            }
            catch (Exception e)
            {
                ctx.Patcher.ReportFailure(BallFeature, e);
            }
            // Skins are the host's (unlocks.txt); the new one arrives as the unl/balls fact.
            __result = false;
            return false;
        }

        private void RequestBall()
        {
            if (!_ctx.IsReady || _ctx.Now - _lastBallRequest < BallRequestInterval)
                return;
            _lastBallRequest = _ctx.Now;
            _ctx.Send(ProgressionRpc.BallUnlock, RpcTarget.Host);
        }

        private bool ValidateBallUnlock(PeerId sender, RpcTarget target, PeerId targetPeer, NetReader args) =>
            target == RpcTarget.Host && args.IsAtEnd && _ballLimiter.TryTake(sender);

        private void OnBallUnlock(PeerId sender, NetReader reader)
        {
            if (!_ctx.IsHost || sender == _ctx.LocalId)
                return;
            var manager = SaveManager.instance;
            if (manager == null)
                return;
            // The new ball is shared (unl/balls); the host's own ball choice stays its own.
            int selected = manager.m_unlocks.selectedBall;
            bool unlocked;
            using (_ctx.Replay.Begin("pg.ball"))
            {
                unlocked = manager.UnlockNewBall();
                if (unlocked && manager.m_unlocks.selectedBall != selected)
                {
                    manager.m_unlocks.selectedBall = selected;
                    if (Cosmetics.instance != null)
                        Cosmetics.instance.SetBall(selected);
                    manager.SaveUnlocks();
                }
            }
            _ctx.Info($"{Log}{_ctx.PlayerName(sender)} earned a ball skin; {(unlocked ? "unlocked one" : "none left to unlock")}.");
            _module.RequestDiff();
        }
    }
}
