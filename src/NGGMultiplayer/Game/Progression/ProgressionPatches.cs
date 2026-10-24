using System;
using UnityEngine;

namespace NGGMultiplayer.Game.Progression
{
    /// <summary>
    /// The module's general game hooks: "something changed" signals for the diff (SaveRun, Bank.ChangeBalance,
    /// SetBountyPadStates), the points where deferred track / pad changes can be applied (leaving a bounty, loading
    /// a set), and the host's guard against paying a bounty twice.
    /// </summary>
    internal static class ProgressionPatches
    {
        public const string DiffFeature = "pg.facts.saveRun";
        public const string BankHookFeature = "pg.bank.hook";
        public const string TiersFeature = "pg.bounty.tiers";
        public const string ExitBountyFeature = "pg.track.exitBounty";
        public const string LoadSetFeature = "pg.track.loadSet";
        public const string RefundFeature = "pg.bounty.refund";
        public const string DeleteRunFeature = "pg.track.deleteRun";
        public const string QuitFeature = "pg.bounty.quit";

        public static void Install(GameContext ctx)
        {
            var patcher = ctx.Patcher;
            var type = typeof(ProgressionPatches);
            patcher.Postfix(DiffFeature, typeof(SaveManager), "SaveRun", Type.EmptyTypes, type, nameof(SaveRunPostfix));
            patcher.Postfix(BankHookFeature, typeof(Bank), "ChangeBalance", new[] { typeof(int), typeof(bool), typeof(bool) },
                type, nameof(ChangeBalancePostfix));
            patcher.Postfix(TiersFeature, typeof(SaveManager), "SetBountyPadStates", new[] { typeof(int) },
                type, nameof(SaveRunPostfix));
            patcher.Postfix(ExitBountyFeature, typeof(ChallengeSystem), "ExitBountyChallenge", new[] { typeof(bool) },
                type, nameof(ChallengeSetChangedPostfix));
            patcher.Postfix(LoadSetFeature, typeof(ChallengeSystem), "LoadNewChallengeSet", new[] { typeof(ChallengeSet), typeof(int) },
                type, nameof(ChallengeSetChangedPostfix));

            // A void prefix (never skips): Lifecycle's QuitToMenu prefix stays the only one that decides (12.3).
            patcher.Prefix(QuitFeature, typeof(PauseMenu), "QuitToMenu", Type.EmptyTypes, type, nameof(QuitToMenuPrefix));

            patcher.Postfix(DeleteRunFeature, typeof(SaveManager), "DeleteRun", Type.EmptyTypes, type, nameof(DeleteRunPostfix));
            patcher.Postfix(DeleteRunFeature, typeof(SaveManager), "DeleteEverything", Type.EmptyTypes, type, nameof(DeleteRunPostfix));

            var complete = patcher.Method(RefundFeature, typeof(ChallengeSystem), "CompleteChallenge", new[] { typeof(Challenge) });
            if (complete != null)
            {
                patcher.Apply(RefundFeature, complete,
                    prefix: GamePatcher.Hook(type, nameof(CompleteChallengePrefix)),
                    postfix: GamePatcher.Hook(type, nameof(CompleteChallengePostfix)));
            }
        }

        private static void SaveRunPostfix()
        {
            var module = ProgressionModule.Instance;
            if (module != null && GameContext.Current != null && GameContext.Current.InSession)
                module.RequestDiff();
        }

        private static void ChangeBalancePostfix(int __0)
        {
            var ctx = GameContext.Current;
            var module = ProgressionModule.Instance;
            if (ctx == null || module == null || !ctx.InSession || !ctx.Patcher.IsEnabled(BankHookFeature))
                return;
            try
            {
                if (ctx.IsHost)
                    module.RequestDiff();
                // Only a change made inside a replay scope is not the player's; a purchase during the hold window
                // after some replay still has to be reported under its grant.
                else if (ctx.IsClient && ctx.IsReady && !ctx.Replay.IsActive)
                    module.Interactions.OnClientBalanceChanged(__0);
            }
            catch (Exception e)
            {
                ctx.Patcher.ReportFailure(BankHookFeature, e);
            }
            // A change applied for the other player (a client's purchase or bounty on the host): inside a replay scope.
            if (BalanceDisplay.NeedsRefresh(ctx.InSession, ctx.Replay.IsActive))
                RefreshBalanceReminder(ctx);
        }

        /// <summary>
        /// The "You have $N" line (<see cref="BalanceDisplay"/>). Call inside a replay scope only: with every bounty
        /// restored the game's method may unlock the COMPLETE achievement, which the replay guard keeps from a replay.
        /// </summary>
        internal static void RefreshBalanceReminder(GameContext ctx)
        {
            try
            {
                var system = ChallengeSystem.instance;
                var reminder = system != null ? system.m_challengeReminder : null;
                if (reminder != null)
                    reminder.UpdateBalanceReminder();
            }
            catch (Exception e)
            {
                ctx.WarnOnce("pg.bank.reminder", $"[Progression] Refreshing the balance line failed: {e.Message}");
            }
        }

        private static void ChallengeSetChangedPostfix()
        {
            var ctx = GameContext.Current;
            var module = ProgressionModule.Instance;
            // Out of a session too: a pending host track / bounty (see ProgressionModule) waits for the main track.
            if (ctx == null || module == null || !(ctx.InSession || module.HasPendingHostWork))
                return;
            module.OnOwnSetChanged();
            // Next frame: the game may still be switching sets inside this call.
            ctx.Post(module.ResolveDeferred);
        }

        // Before the game's own SaveRun in QuitToMenu: the host's pending bounty entries go into the run it saves.
        private static void QuitToMenuPrefix()
        {
            var ctx = GameContext.Current;
            var module = ProgressionModule.Instance;
            if (ctx == null || module == null || !ctx.Patcher.IsEnabled(QuitFeature))
                return;
            try
            {
                module.FlushOwnPendingIntoRun();
            }
            catch (Exception e)
            {
                ctx.Patcher.ReportFailure(QuitFeature, e);
            }
        }

        // The player deleted the run (or everything): a pending host track belonged to the run that is gone.
        private static void DeleteRunPostfix()
        {
            var ctx = GameContext.Current;
            var module = ProgressionModule.Instance;
            if (ctx == null || module == null)
                return;
            try
            {
                // Skipped by the save guards when blocked; then nothing was deleted.
                if (!Lifecycle.SessionSaveStore.WritesBlocked)
                    module.OnOwnRunDeleted();
            }
            catch (Exception e)
            {
                ctx.Patcher.ReportFailure(DeleteRunFeature, e);
            }
        }

        // A bounty the host already paid for a client's claim pays nothing when the host completes it too (also
        // after the session, while that client's entry still waits for the host to leave the bounty).
        private static void CompleteChallengePrefix(out int __state)
        {
            __state = int.MinValue;
            var ctx = GameContext.Current;
            var module = ProgressionModule.Instance;
            if (ctx == null || module == null || ctx.IsClientConnection || ctx.Replay.IsActive || !ctx.Patcher.IsEnabled(RefundFeature))
                return;
            try
            {
                var system = ChallengeSystem.instance;
                var set = system != null ? system.m_challengeSet : null;
                if (set == null || !module.Access.IsBounty(set) || !module.WasPaidRemotely(GameIds.ChallengeSetName(set)))
                    return;
                var bank = module.Access.Bank();
                if (bank != null)
                    __state = bank.m_balance;
            }
            catch (Exception e)
            {
                ctx.Patcher.ReportFailure(RefundFeature, e);
            }
        }

        private static void CompleteChallengePostfix(int __state)
        {
            if (__state == int.MinValue)
                return;
            var ctx = GameContext.Current;
            var module = ProgressionModule.Instance;
            if (ctx == null || module == null)
                return;
            try
            {
                var bank = module.Access.Bank();
                if (bank == null)
                    return;
                int paid = BountyIsolation.Refund(true, __state, bank.m_balance);
                if (paid <= 0)
                    return;
                using (ctx.Replay.Begin("pg.bounty.refund"))
                {
                    bank.ChangeBalance(-paid, false, true);
                    // CompleteChallenge already ran SaveRun, which captured the overpaid balance; save the right one.
                    SaveManager.instance?.SaveRun();
                }
                ctx.Info($"[Progression] This bounty was already paid for another player's completion; took back {paid}.");
            }
            catch (Exception e)
            {
                ctx.Patcher.ReportFailure(RefundFeature, e);
            }
        }
    }
}
