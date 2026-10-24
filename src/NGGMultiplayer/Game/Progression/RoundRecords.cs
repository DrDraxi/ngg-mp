using System;
using System.Collections.Generic;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Sync;

namespace NGGMultiplayer.Game.Progression
{
    /// <summary>
    /// A client's round records into the host's save. A client never writes its save, and the scorecard and the LMUGC
    /// best scores are host-only facts, so a JustGolf round a client finished (SaveManager.AddScoreToCard, skipped on
    /// clients) and an LMUGC best it set are sent to the host, which records them itself. The host's own diff then
    /// shares the new scorecard / best with everyone.
    /// <para>From the game (answers 139, 140): a JustGolf round's score is the sum of its 9 lmugcScore entries
    /// (unplayed holes count par + 2), and AddScoreToCard appends it and writes scoreCard.txt at once. The bests are
    /// written by LMUGC.CompleteHole (Story only, once hole 9 is scored, before UpdateBestScores and FinishChallenge),
    /// not by FinishChallenge; JustGolf never changes them.</para>
    /// </summary>
    internal sealed class RoundRecords
    {
        public const string CardFeature = "pg.rounds.card";
        public const string LmugcFeature = "pg.rounds.lmugc";
        private const string Log = "[Progression] ";
        private const string BestField = "lmugcScoreBest";
        private const string NoAssistField = "lmugcScoreNoAssistBest";

        private readonly ProgressionModule _module;
        private readonly GameContext _ctx;
        private readonly PeerRateLimiter _cardLimiter;
        private readonly PeerRateLimiter _bestLimiter;

        public RoundRecords(ProgressionModule module, GameContext ctx)
        {
            _module = module;
            _ctx = ctx;
            _cardLimiter = new PeerRateLimiter(() => ctx.Now, RoundRecordRules.RoundScoreIntervalSeconds);
            _bestLimiter = new PeerRateLimiter(() => ctx.Now, RoundRecordRules.RoundScoreIntervalSeconds);
        }

        public void Install()
        {
            var patcher = _ctx.Patcher;
            patcher.Postfix(CardFeature, typeof(SaveManager), "AddScoreToCard", new[] { typeof(int) },
                typeof(RoundRecords), nameof(AddScoreToCardPostfix));
            var completeHole = patcher.Method(LmugcFeature, typeof(LMUGC), "CompleteHole", new[] { typeof(string), typeof(int) });
            if (completeHole != null)
            {
                patcher.Apply(LmugcFeature, completeHole,
                    prefix: GamePatcher.Hook(typeof(RoundRecords), nameof(BestsPrefix)),
                    postfix: GamePatcher.Hook(typeof(RoundRecords), nameof(BestsPostfix)));
            }
            _ctx.RegisterRpc(ProgressionRpc.RoundScore, OnRoundScore, ValidateRoundScore);
            _ctx.RegisterRpc(ProgressionRpc.LmugcBest, OnLmugcBest, ValidateLmugcBest);
        }

        public void Clear()
        {
            _cardLimiter.Clear();
            _bestLimiter.Clear();
        }

        private bool IsClientSending => _ctx.InSession && _ctx.IsClient && _ctx.IsReady && !_ctx.Replay.IsActive;

        // ---- JustGolf scorecard ------------------------------------------------------------------------

        private static void AddScoreToCardPostfix(int __0)
        {
            var ctx = GameContext.Current;
            var module = ProgressionModule.Instance;
            if (ctx == null || module == null || !ctx.Patcher.IsEnabled(CardFeature))
                return;
            try
            {
                module.Live.InvalidateScoreCard();
                if (!module.Rounds.IsClientSending)
                    return;
                // Skipped here by the save guards; the host records it in the session's (its own) scorecard.
                int score = __0;
                if (RoundRecordRules.IsPlausibleRoundScore(score))
                    ctx.Send(ProgressionRpc.RoundScore, RpcTarget.Host, w => w.WriteVarInt(score));
            }
            catch (Exception e)
            {
                ctx.Patcher.ReportFailure(CardFeature, e);
            }
        }

        private static bool ValidateRoundScore(PeerId sender, RpcTarget target, PeerId targetPeer, NetReader args)
        {
            if (target != RpcTarget.Host)
                return false;
            args.ReadVarInt();
            return args.IsAtEnd;
        }

        private void OnRoundScore(PeerId sender, NetReader reader)
        {
            int score = reader.ReadVarInt();
            if (!_ctx.IsHost || sender == _ctx.LocalId)
                return;
            var manager = SaveManager.instance;
            // Only a JustGolf session has a scorecard round; one per player per round at most.
            if (manager == null || !_module.Live.IsJustGolf || !RoundRecordRules.IsPlausibleRoundScore(score)
                || !_cardLimiter.TryTake(sender))
            {
                _ctx.Info($"{Log}Ignored a round score of {score} from {_ctx.PlayerName(sender)}.");
                return;
            }
            using (_ctx.Replay.Begin("pg.rounds.card"))
            {
                // AddScoreToCard appends to m_scoreCardSave.scores, which LoadInToChallenge loads in JustGolf: a host
                // that has not played a round here yet has none.
                if (manager.m_scoreCardSave.scores == null)
                    manager.m_scoreCardSave = manager.GetScoreCardSave();
                if (manager.m_scoreCardSave.scores == null)
                    manager.m_scoreCardSave.scores = new List<ScoreCardEntry>();
                manager.AddScoreToCard(score);
            }
            _module.Live.InvalidateScoreCard();
            _module.RequestDiff();
            _ctx.Info($"{Log}Recorded {_ctx.PlayerName(sender)}'s round of {score} on the scorecard.");
        }

        // ---- LMUGC best scores --------------------------------------------------------------------------

        private static void BestsPrefix(out long __state)
        {
            __state = 0;
            var manager = SaveManager.instance;
            if (manager != null)
                __state = Pack(manager.m_run.lmugcScoreBest, manager.m_run.lmugcScoreNoAssistBest);
        }

        private static void BestsPostfix(long __state)
        {
            var ctx = GameContext.Current;
            var module = ProgressionModule.Instance;
            var manager = SaveManager.instance;
            if (ctx == null || module == null || manager == null || !ctx.Patcher.IsEnabled(LmugcFeature))
                return;
            try
            {
                if (!module.Rounds.IsClientSending)
                    return;
                int bestBefore = (int)(__state >> 32);
                int noAssistBefore = (int)(uint)__state;
                int best = manager.m_run.lmugcScoreBest;
                int noAssist = manager.m_run.lmugcScoreNoAssistBest;
                if (best == bestBefore && noAssist == noAssistBefore)
                    return;
                ctx.Send(ProgressionRpc.LmugcBest, RpcTarget.Host, w =>
                {
                    w.WriteVarInt(bestBefore);
                    w.WriteVarInt(best);
                    w.WriteVarInt(noAssistBefore);
                    w.WriteVarInt(noAssist);
                });
            }
            catch (Exception e)
            {
                ctx.Patcher.ReportFailure(LmugcFeature, e);
            }
        }

        private static bool ValidateLmugcBest(PeerId sender, RpcTarget target, PeerId targetPeer, NetReader args)
        {
            if (target != RpcTarget.Host)
                return false;
            for (int i = 0; i < 4; i++)
                args.ReadVarInt();
            return args.IsAtEnd;
        }

        private void OnLmugcBest(PeerId sender, NetReader reader)
        {
            int bestBefore = reader.ReadVarInt();
            int best = reader.ReadVarInt();
            int noAssistBefore = reader.ReadVarInt();
            int noAssist = reader.ReadVarInt();
            if (!_ctx.IsHost || sender == _ctx.LocalId || SaveManager.instance == null || !_bestLimiter.TryTake(sender))
                return;
            ref RunSave run = ref SaveManager.instance.m_run;
            bool changed = false;
            if (RoundRecordRules.AcceptBest(run.lmugcScoreBest, bestBefore, best))
                changed |= Apply(BestField, best, sender);
            if (RoundRecordRules.AcceptBest(run.lmugcScoreNoAssistBest, noAssistBefore, noAssist))
                changed |= Apply(NoAssistField, noAssist, sender);
            if (!changed)
                return;
            _module.RequestDiff();
            _module.ScheduleHostSave();
            _module.SendNote(sender, NoteKind.Unlock, _ctx.PlayerName(sender) + " set a new LMUGC best");
        }

        private bool Apply(string field, int value, PeerId sender)
        {
            var result = _module.Applier.Apply(FactKinds.RunHostInt, field, FactTable.EncodeInt(value), true, sender);
            _ctx.Info($"{Log}{_ctx.PlayerName(sender)}'s LMUGC {field} {value}: {result}.");
            return result == ApplyResult.Applied;
        }

        private static long Pack(int high, int low) => ((long)high << 32) | (uint)low;
    }
}
