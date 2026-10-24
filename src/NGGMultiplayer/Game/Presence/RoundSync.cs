using System;
using System.Collections.Generic;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Sync;
using UnityEngine;
using Random = System.Random;

namespace NGGMultiplayer.Game.Presence
{
    /// <summary>
    /// LMUGC / JustGolf rounds (spec 7.7). Every player plays their own ball through the nine holes; the local
    /// round's start, holes and end go to everyone, the host keeps every player's latest round and hands it to late
    /// joiners (<c>ngg.pr.card</c>), and the HUD shows the shared <see cref="Scorecard"/>.
    /// <para>The game's rules (docs/confirm answers): a round starts with StartChallenge, or resumes through
    /// LoadInToChallenge (SaveManager.LoadInLMUGC, at the first unscored hole, #65); CompleteHole's hole is read from
    /// the save entry it changed, else the FIRST number of its id read with the id base learned at runtime (#45, #52);
    /// the last hole's CompleteHole itself runs FinishChallenge (Story) or CompleteNormalGolfRound (JustGolf, which
    /// resets the scores: that hole is read from the save as the finishing call found it), so the finish goes out after
    /// that hole; the finished total is the game's (#139: unplayed holes count par + 2). Neither finishing call runs ExitChallenge
    /// (#66); the <see cref="AbortGraceSeconds"/> grace before an exit counts as an abort is kept as a safety net.</para>
    /// </summary>
    internal sealed class RoundSync
    {
        public const double AbortGraceSeconds = 0.5;
        private const double RecentHoleSeconds = 3.0;

        private readonly GameContext _context;
        private readonly PresenceAccess _access;
        private readonly Scorecard _card = new Scorecard();
        private readonly Random _random = new Random();
        private uint _roundId;
        private int _lastHole = -1;
        private double _lastHoleAt = double.NegativeInfinity;
        private uint _abortPending;

        // Game-call bookkeeping: StartChallenge's frame (a LoadInToChallenge inside it is not a resume), the save's
        // scores before a CompleteHole, the game's total before a finishing call, and a finish raised inside
        // CompleteHole (Story FinishChallenge / JustGolf CompleteNormalGolfRound run from the last hole's
        // CompleteHole), which is sent after that hole.
        private int _startFrame = -1;
        private int[] _scoresBefore;
        private int[] _scoresAtFinish;
        private int? _finishTotal;
        private int _completeHoleDepth;
        private bool _finishDeferred;
        private bool _holeSourceLogged;
        private bool _idOffsetLogged;
        private int _idOffset;
        private bool _idFallbackLogged;

        public RoundSync(GameContext context, PresenceAccess access)
        {
            _context = context;
            _access = access;
        }

        public Scorecard Card => _card;

        /// <summary>Raised when any player's round changed (the HUD opens the scorecard for a while).</summary>
        public event Action Changed;

        public void Install()
        {
            var patcher = _context.Patcher;
            var type = typeof(RoundSync);
            var lmugc = typeof(LMUGC);
            patcher.Prefix(PresenceFeature.Round, lmugc, "StartChallenge", Type.EmptyTypes, type, nameof(StartPrefix));
            patcher.Postfix(PresenceFeature.Round, lmugc, "StartChallenge", Type.EmptyTypes, type, nameof(StartPostfix));
            patcher.Postfix(PresenceFeature.Round, lmugc, "LoadInToChallenge", Type.EmptyTypes, type, nameof(LoadInPostfix));
            var completeHole = patcher.Method(PresenceFeature.Round, lmugc, "CompleteHole", new[] { typeof(string), typeof(int) });
            patcher.Prefix(PresenceFeature.Round, completeHole, type, nameof(CompleteHolePrefix));
            patcher.Postfix(PresenceFeature.Round, completeHole, type, nameof(CompleteHolePostfix));
            patcher.Finalizer(PresenceFeature.Round, completeHole, type, nameof(CompleteHoleFinalizer));
            foreach (var finish in new[] { "FinishChallenge", "CompleteNormalGolfRound" })
            {
                var method = patcher.Method(PresenceFeature.Round, lmugc, finish, Type.EmptyTypes);
                patcher.Prefix(PresenceFeature.Round, method, type, nameof(FinishPrefix));
                patcher.Postfix(PresenceFeature.Round, method, type, nameof(FinishPostfix));
            }
            patcher.Postfix(PresenceFeature.Round, lmugc, "ExitChallenge", Type.EmptyTypes, type, nameof(ExitPostfix));

            _context.RegisterRpc(PresenceRpc.Round, OnRound, (s, t, p, r) => PresenceWire.IsValid(r, x => RoundMessage.Read(x)));
            _context.RegisterRpc(PresenceRpc.Hole, OnHole, (s, t, p, r) => PresenceWire.IsValid(r, x => HoleMessage.Read(x)));
            _context.RegisterRpc(PresenceRpc.Card, OnCard, (s, t, p, r) => false);
        }

        public void Reset()
        {
            _card.Clear();
            _roundId = 0;
            _lastHole = -1;
            _lastHoleAt = double.NegativeInfinity;
            _abortPending = 0;
        }

        /// <summary>The local round is gone with the world: tell the others it ended there.</summary>
        public void OnSceneLoaded()
        {
            _abortPending = 0;
            _finishTotal = null;
            _finishDeferred = false;
            _scoresBefore = null;
            _scoresAtFinish = null;
            var record = LocalRecord();
            if (record != null && record.InProgress && _context.IsReady && _context.Patcher.IsEnabled(PresenceFeature.Round))
            {
                try
                {
                    SendRound(RoundEvent.Aborted, record.PlayedTotal);
                }
                catch (Exception e)
                {
                    _context.Patcher.ReportFailure(PresenceFeature.Round, e);
                }
            }
            _roundId = 0;
        }

        /// <summary>"LMUGC hole n" while the local player is in a round.</summary>
        public string Activity()
        {
            var record = LocalRecord();
            return record != null && record.InProgress ? PresenceText.RoundActivity(record.CurrentHole) : string.Empty;
        }

        /// <summary>The hole the local player is on (or just finished), -1 outside a round.</summary>
        public int CurrentHole()
        {
            if (_lastHole > 0 && _context.Now - _lastHoleAt <= RecentHoleSeconds)
                return _lastHole;
            var record = LocalRecord();
            return record != null && record.InProgress ? record.CurrentHole : -1;
        }

        public void OnPlayerLeft(PeerId peer) => _card.MarkLeft(peer);

        /// <summary>Host: a client just became ready; give it everyone's rounds.</summary>
        public void SendCard(PeerId peer)
        {
            if (!_context.IsHost || !_context.Patcher.IsEnabled(PresenceFeature.Round))
                return;
            var rows = _card.BuildCard(p => p != peer && _context.TryGetPlayer(p, out _));
            if (rows.Count == 0)
                return;
            _context.Send(PresenceRpc.Card, RpcTarget.Peer, rows, (w, r) => CardMessage.Write(w, r), Reliability.Reliable, peer);
        }

        /// <summary>Pars of the local course, for the scorecard header.</summary>
        public void ReadPars()
        {
            if (_access.Pars == null || !_context.WorldReady || !_context.Patcher.IsEnabled(PresenceFeature.RoundPars))
                return;
            try
            {
                var lmugc = Compat.FindAny<LMUGC>(true);
                var pars = lmugc != null ? _access.Pars(lmugc) : null;
                if (pars == null)
                    return;
                for (int h = 0; h < pars.Length && h < PresenceWire.Holes; h++)
                    _card.SetPar(h + 1, pars[h]);
            }
            catch (Exception e)
            {
                _context.Patcher.ReportFailure(PresenceFeature.RoundPars, e);
            }
        }

        private RoundRecord LocalRecord()
        {
            if (_roundId == 0)
                return null;
            var record = _card.Get(_context.LocalId);
            return record != null && record.RoundId == _roundId ? record : null;
        }

        private bool CanTrack =>
            _context.Patcher.IsEnabled(PresenceFeature.Round) && _context.IsReady && !_context.Replay.IsActive;

        // ---- Local events ----------------------------------------------------------------------------

        private void OnLocalStart()
        {
            _abortPending = 0;
            _roundId = NewRoundId();
            _lastHole = -1;
            SendRound(RoundEvent.Start, 0);
        }

        /// <summary>
        /// LoadInToChallenge outside StartChallenge: SaveManager.LoadInLMUGC resuming a saved round at its first unscored
        /// hole (answers #65). The round starts here, with the holes the save already scored.
        /// </summary>
        private void OnLocalResume(LMUGC lmugc)
        {
            if (_startFrame == Time.frameCount)
                return;
            OnLocalStart();
            int seeded = Seed(lmugc, SaveScores(), skipHole: -1);
            _context.Info($"[Presence] LMUGC round resumed (LoadInToChallenge) with {seeded} hole(s) already scored.");
        }

        /// <summary>Sends every scored hole of <paramref name="scores"/> (except <paramref name="skipHole"/>).</summary>
        private int Seed(LMUGC lmugc, int[] scores, int skipHole)
        {
            if (scores == null)
                return 0;
            int seeded = 0;
            for (int h = 0; h < scores.Length && h < PresenceWire.Holes; h++)
            {
                if (h + 1 == skipHole || !LmugcRules.IsScored(scores[h]))
                    continue;
                SendHole(lmugc, h + 1, scores[h], recent: false);
                seeded++;
            }
            return seeded;
        }

        private void OnLocalHole(LMUGC lmugc, string id, int score)
        {
            int[] before = _scoresBefore;
            _scoresBefore = null;
            // The last hole's CompleteHole runs the finishing call itself, and JustGolf's CompleteNormalGolfRound
            // resets the scores (answers #66, #140): the save as the finishing call found it is that hole's "after".
            int[] after = _scoresAtFinish ?? SaveScores();
            _scoresAtFinish = null;
            int hole = LmugcRules.ResolveHole(before, after, id, PresenceWire.Holes, _idOffset, out var source);
            if (_roundId == 0)
            {
                // Hosting started (or the world loaded) mid-round: the round begins here for the others, with the
                // holes the game had already scored.
                OnLocalStart();
                Seed(lmugc, before, hole);
            }
            LogHoleSource(id, hole, source);
            if (hole < 1)
            {
                var record = LocalRecord();
                _context.WarnOnce("pr.round.id", $"[Presence] LMUGC hole '{id}' could not be told (no single save entry changed and no number 1..9 in the id); using the next unplayed hole.");
                hole = record != null ? record.CurrentHole : 1;
            }
            SendHole(lmugc, hole, score, recent: true);
        }

        private void SendHole(LMUGC lmugc, int hole, int score, bool recent)
        {
            int par = ParOf(lmugc, hole);
            if (recent)
            {
                _lastHole = hole;
                _lastHoleAt = _context.Now;
            }
            var message = new HoleMessage
            {
                RoundId = _roundId,
                Hole = hole,
                Strokes = HoledMessage.Clamp(score, 0, PresenceWire.MaxHoleStrokes),
                Par = HoledMessage.Clamp(par, 0, PresenceWire.MaxPar)
            };
            Apply(_card.ApplyHole(_context.LocalId, message.RoundId, message.Hole, message.Strokes, message.Par));
            _context.Send(PresenceRpc.Hole, RpcTarget.All, message, (w, m) => m.Write(w));
        }

        // The runtime check of answers #45 / #52: where the hole came from, and whether ids count from 0 or 1. The
        // base learned from a save-resolved hole is used when a later hole falls back to its id.
        private void LogHoleSource(string id, int hole, HoleSource source)
        {
            if (!_holeSourceLogged)
            {
                _holeSourceLogged = true;
                string from = source == HoleSource.Save ? "the lmugcScore entry it changed"
                    : source == HoleSource.Id ? $"the first number of its id (ids taken as counting from {(_idOffset == 1 ? 0 : 1)})"
                    : "nothing (fallback)";
                _context.Info($"[Presence] LMUGC.CompleteHole('{id}') -> hole {hole}, from {from}.");
            }
            else if (source == HoleSource.Id && !_idFallbackLogged)
            {
                _idFallbackLogged = true;
                _context.Info($"[Presence] LMUGC.CompleteHole('{id}') -> hole {hole} from its id (no single save entry "
                    + $"changed; ids taken as counting from {(_idOffset == 1 ? 0 : 1)}).");
            }
            if (source != HoleSource.Save || _idOffsetLogged)
                return;
            int? offset = LmugcRules.IdOffset(id, hole);
            if (offset == null)
                return;
            _idOffsetLogged = true;
            _idOffset = offset.Value;
            _context.Info(offset == 0
                ? $"[Presence] LMUGC hole ids count from 1 ('{id}' is hole {hole})."
                : $"[Presence] LMUGC hole ids count from 0 ('{id}' is hole {hole}); the save entry is used, and ids are read that way when it cannot be.");
        }

        private int ParOf(LMUGC lmugc, int hole)
        {
            if (_access.Pars == null || lmugc == null || !_context.Patcher.IsEnabled(PresenceFeature.RoundPars))
                return 0;
            try
            {
                var pars = _access.Pars(lmugc);
                return pars != null && hole >= 1 && hole - 1 < pars.Length ? pars[hole - 1] : 0;
            }
            catch (Exception e)
            {
                _context.Patcher.ReportFailure(PresenceFeature.RoundPars, e);
                return 0;
            }
        }

        /// <summary>The live <c>m_run.lmugcScore</c> (not a copy), or null.</summary>
        private static int[] SaveScores()
        {
            var save = SaveManager.instance;
            return save != null ? save.m_run.lmugcScore : null;
        }

        /// <summary>
        /// The game's round score from the save (answers #139: the nine lmugcScore entries, an unplayed hole counting
        /// par + 2), or null when the scores cannot be read. Taken before the finishing call runs:
        /// CompleteNormalGolfRound resets the scores.
        /// </summary>
        private int? GameTotal(LMUGC lmugc)
        {
            var scores = SaveScores();
            if (scores == null)
                return null;
            int[] pars = null;
            if (_access.Pars != null && lmugc != null && _context.Patcher.IsEnabled(PresenceFeature.RoundPars))
            {
                try
                {
                    pars = _access.Pars(lmugc);
                }
                catch (Exception e)
                {
                    _context.Patcher.ReportFailure(PresenceFeature.RoundPars, e);
                }
            }
            IReadOnlyList<int> parList = pars ?? (IReadOnlyList<int>)_card.Pars;
            int total = LmugcRules.RoundTotal(scores, parList, PresenceWire.Holes, out bool complete);
            if (!complete)
                _context.WarnOnce("pr.round.total", "[Presence] LMUGC round total without some unplayed holes' par + 2 (pars unknown).");
            return total;
        }

        private void OnLocalFinish()
        {
            _abortPending = 0;
            int? gameTotal = _finishTotal;
            _finishTotal = null;
            var record = LocalRecord();
            if (record == null || !record.InProgress)
                return;
            int total = gameTotal ?? LmugcRules.RoundTotal(record.Strokes, _card.Pars, PresenceWire.Holes, out _);
            SendRound(RoundEvent.Finished, total);
            _roundId = 0;
        }

        private void OnLocalExit()
        {
            var record = LocalRecord();
            if (record == null || !record.InProgress)
                return;
            uint round = _roundId;
            _abortPending = round;
            _context.Delay(AbortGraceSeconds, () =>
            {
                if (_abortPending != round || _roundId != round)
                    return;
                _abortPending = 0;
                var current = LocalRecord();
                if (current == null || !current.InProgress || !_context.IsReady)
                    return;
                SendRound(RoundEvent.Aborted, current.PlayedTotal);
                _roundId = 0;
            });
        }

        private void SendRound(RoundEvent ev, int total)
        {
            var message = new RoundMessage { RoundId = _roundId, Event = ev, Total = total };
            Apply(_card.ApplyRound(_context.LocalId, message.RoundId, message.Event, message.Total));
            _context.Send(PresenceRpc.Round, RpcTarget.All, message, (w, m) => m.Write(w));
        }

        private uint NewRoundId()
        {
            uint id;
            do
            {
                // System.Random: UnityEngine.Random would shift the game's own random sequence.
                id = (uint)_random.Next(1, int.MaxValue);
            } while (id == _roundId);
            return id;
        }

        private void Apply(bool changed)
        {
            if (!changed)
                return;
            try
            {
                Changed?.Invoke();
            }
            catch (Exception e)
            {
                _context.ErrorOnce("pr.round.changed", $"[Presence] Scorecard change handler threw: {e}");
            }
        }

        // ---- Remote events ---------------------------------------------------------------------------

        private void OnRound(PeerId sender, NetReader reader)
        {
            var message = RoundMessage.Read(reader);
            Apply(_card.ApplyRound(sender, message.RoundId, message.Event, message.Total));
        }

        private void OnHole(PeerId sender, NetReader reader)
        {
            var message = HoleMessage.Read(reader);
            Apply(_card.ApplyHole(sender, message.RoundId, message.Hole, message.Strokes, message.Par));
        }

        private void OnCard(PeerId sender, NetReader reader)
        {
            List<CardRow> rows = CardMessage.Read(reader);
            if (_context.IsHost || sender != _context.HostId)
                return;
            bool changed = false;
            foreach (var row in rows)
            {
                // The local player's own round is known best here.
                if (row.Peer == _context.LocalId)
                    continue;
                changed |= _card.ApplyCardRow(row);
            }
            Apply(changed);
        }

        // ---- Patches ---------------------------------------------------------------------------------

        private static RoundSync Tracking(out GameContext context)
        {
            context = GameContext.Current;
            var self = PresenceModule.Instance?.Rounds;
            return context != null && self != null && self.CanTrack ? self : null;
        }

        private static void StartPrefix()
        {
            var self = PresenceModule.Instance?.Rounds;
            if (self != null)
                self._startFrame = Time.frameCount;
        }

        private static void StartPostfix()
        {
            var self = Tracking(out var context);
            if (self == null)
                return;
            try
            {
                self.OnLocalStart();
            }
            catch (Exception e)
            {
                context.Patcher.ReportFailure(PresenceFeature.Round, e);
            }
        }

        private static void LoadInPostfix(LMUGC __instance)
        {
            var self = Tracking(out var context);
            if (self == null)
                return;
            try
            {
                self.OnLocalResume(__instance);
            }
            catch (Exception e)
            {
                context.Patcher.ReportFailure(PresenceFeature.Round, e);
            }
        }

        private static void CompleteHolePrefix()
        {
            var self = PresenceModule.Instance?.Rounds;
            if (self == null)
                return;
            self._completeHoleDepth++;
            self._scoresBefore = null;
            self._scoresAtFinish = null;
            if (Tracking(out var context) == null)
                return;
            try
            {
                var scores = SaveScores();
                self._scoresBefore = scores != null ? (int[])scores.Clone() : null;
            }
            catch (Exception e)
            {
                context.Patcher.ReportFailure(PresenceFeature.Round, e);
            }
        }

        private static void CompleteHolePostfix(LMUGC __instance, string __0, int __1)
        {
            var self = Tracking(out var context);
            if (self == null)
                return;
            try
            {
                self.OnLocalHole(__instance, __0, __1);
            }
            catch (Exception e)
            {
                context.Patcher.ReportFailure(PresenceFeature.Round, e);
            }
        }

        // Runs after the postfix, and also when CompleteHole threw (that exception is rethrown unchanged): a finish
        // raised inside CompleteHole goes out after its hole.
        private static void CompleteHoleFinalizer(Exception __exception)
        {
            var self = PresenceModule.Instance?.Rounds;
            if (self == null)
                return;
            if (self._completeHoleDepth > 0)
                self._completeHoleDepth--;
            if (self._completeHoleDepth > 0 || !self._finishDeferred)
                return;
            self._finishDeferred = false;
            var tracking = Tracking(out var context);
            if (tracking == null)
                return;
            try
            {
                tracking.OnLocalFinish();
            }
            catch (Exception e)
            {
                context.Patcher.ReportFailure(PresenceFeature.Round, e);
            }
        }

        private static void FinishPrefix(LMUGC __instance)
        {
            var self = Tracking(out var context);
            if (self == null)
                return;
            try
            {
                self._finishTotal = self.GameTotal(__instance);
                if (self._completeHoleDepth > 0)
                {
                    // Inside the last hole's CompleteHole: that hole is scored, and CompleteNormalGolfRound is about
                    // to reset the scores, so keep them for the hole's postfix.
                    var scores = SaveScores();
                    self._scoresAtFinish = scores != null ? (int[])scores.Clone() : null;
                }
            }
            catch (Exception e)
            {
                self._finishTotal = null;
                self._scoresAtFinish = null;
                context.Patcher.ReportFailure(PresenceFeature.Round, e);
            }
        }

        private static void FinishPostfix()
        {
            var self = Tracking(out var context);
            if (self == null)
                return;
            if (self._completeHoleDepth > 0)
            {
                // Inside the last hole's CompleteHole: its hole is not sent yet.
                self._finishDeferred = true;
                return;
            }
            try
            {
                self.OnLocalFinish();
            }
            catch (Exception e)
            {
                context.Patcher.ReportFailure(PresenceFeature.Round, e);
            }
        }

        private static void ExitPostfix()
        {
            var self = Tracking(out var context);
            if (self == null)
                return;
            try
            {
                self.OnLocalExit();
            }
            catch (Exception e)
            {
                context.Patcher.ReportFailure(PresenceFeature.Round, e);
            }
        }
    }
}
