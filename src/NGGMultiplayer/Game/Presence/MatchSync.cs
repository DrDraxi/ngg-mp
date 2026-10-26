using System;
using System.Collections.Generic;
using HarmonyLib;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Sync;
using UnityEngine;
using Random = System.Random;

namespace NGGMultiplayer.Game.Presence
{
    /// <summary>
    /// The competitive 9-hole round: "Just Play Nine" in a session plays like stroke-play golf instead of everyone on their
    /// own (docs/gamemap/08-justgolf.md, rules in <see cref="MatchState"/>). Story's LMUGC course is untouched.
    /// <para>The host owns the match: it starts with the host's round (the machine button; a client's press asks the
    /// host), takes every player's strokes (<c>ngg.pr.mstroke</c>) and sends the whole state on every change
    /// (<c>ngg.pr.match</c>). Every game plays its own ball and round as usual; the mod only
    /// <list type="bullet">
    /// <item>starts the round on the clients' games with the host's (<c>LMUGC.StartChallenge</c>),</item>
    /// <item>blocks a shot out of turn: the ball's "Next Shot" (<c>FPSInteractable.Interact</c>, ContinueShot), the swing
    /// itself (private <c>ClubSwing.DetectShot</c> / <c>HandleShot</c>: the hole-1 tee shot needs no interaction) and
    /// <c>LMUGC.RetakeShot</c>,</item>
    /// <item>holds the move to the next tee (<c>LMUGC.TeleportBallToHole(n, false)</c>, run when the player leaves golf
    /// mode after holing out) until every player finished the hole,</item>
    /// <item>catches a game up when the match is further on (a late joiner, a picked-up hole): the holes behind it get the
    /// match's score in <c>m_run.lmugcScore</c> and the player goes to the current hole's tee.</item>
    /// </list></para>
    /// </summary>
    internal sealed class MatchSync
    {
        private const string Log = "[Match] ";
        /// <summary>Seconds the finished hole stays on screen before the next one starts.</summary>
        public const double HoleBreakSeconds = 4.0;
        private const double NoticeGapSeconds = 2.5;
        private const double StartRetrySeconds = 3.0;

        private readonly GameContext _context;
        private readonly PresenceAccess _access;
        private readonly Random _random = new Random();
        private AccessTools.FieldRef<BallTarget, bool> _isHole;

        private MatchState _match;
        private double _advanceAt = double.NaN;
        private int _heldHole = -1;
        private bool _bypass;
        private uint _startedFor;
        /// <summary>The match whose round this game already ended and recorded (its own hole 9, or the catch-up).</summary>
        private uint _finishedFor;
        /// <summary>The game's reminder line shows the match's text (to hand back once the match is over).</summary>
        private bool _reminderShown;
        private double _startTriedAt = double.NegativeInfinity;
        private double _lastNotice = double.NegativeInfinity;
        private uint _lastSeenMatch;
        private PeerId _lastTurn = PeerId.None;
        private int _lastHole;
        private readonly Vector3?[] _cups = new Vector3?[MatchState.Holes];
        private int _cupsScene = -1;

        public MatchSync(GameContext context, PresenceAccess access)
        {
            _context = context;
            _access = access;
        }

        internal GameContext Context => _context;

        /// <summary>The match shown to this player (the host's own, or its last snapshot), or null.</summary>
        public MatchState Match => _match;

        /// <summary>The local game finished its hole and waits for the others before going to the next tee.</summary>
        public bool HoldingNextHole => _heldHole > 0;

        /// <summary>Raised when the match changed (new state, turn, hole).</summary>
        public event Action Changed;

        public void Install()
        {
            var patcher = _context.Patcher;
            var type = typeof(MatchSync);
            var lmugc = typeof(LMUGC);
            const string f = PresenceFeature.Match;
            _isHole = patcher.FieldRef<BallTarget, bool>(f, "isHole");
            patcher.Prefix(f, lmugc, "StartChallenge", Type.EmptyTypes, type, nameof(StartPrefix));
            patcher.Postfix(f, lmugc, "StartChallenge", Type.EmptyTypes, type, nameof(StartPostfix));
            patcher.Prefix(f, lmugc, "TeleportBallToHole", new[] { typeof(int), typeof(bool) }, type, nameof(TeleportPrefix));
            patcher.Postfix(f, lmugc, "CompleteHole", new[] { typeof(string), typeof(int) }, type, nameof(CompleteHolePostfix));
            patcher.Prefix(f, lmugc, "CompleteNormalGolfRound", Type.EmptyTypes, type, nameof(RoundEndPrefix));
            patcher.Postfix(f, lmugc, "Update", Type.EmptyTypes, type, nameof(UpdatePostfix));
            patcher.Prefix(f, lmugc, "RetakeShot", new[] { typeof(bool) }, type, nameof(RetakePrefix));
            patcher.Prefix(f, typeof(FPSInteractable), "Interact", Type.EmptyTypes, type, nameof(InteractPrefix));
            patcher.Prefix(f, typeof(ClubSwing), "DetectShot", Type.EmptyTypes, type, nameof(SwingPrefix));
            patcher.Prefix(f, typeof(ClubSwing), "HandleShot", new[] { typeof(ShotType), typeof(float) }, type, nameof(SwingPrefix));
            patcher.Postfix(f, typeof(HitSequeneceManager), "CompleteSequence", new[] { typeof(Ball) }, type, nameof(RestPostfix));

            _context.RegisterRpc(PresenceRpc.Match, OnMatch, (s, t, p, r) => false);
            _context.RegisterRpc(PresenceRpc.MatchStroke, OnStroke,
                (s, t, p, r) => t == RpcTarget.Host && PresenceWire.IsValid(r, x => MatchStrokeMessage.Read(x)));
            _context.RegisterRpc(PresenceRpc.MatchStart, OnStartRequest, (s, t, p, r) => t == RpcTarget.Host);
            _context.RegisterRpc(PresenceRpc.MatchRetire, OnRetire,
                (s, t, p, r) => t == RpcTarget.Host && PresenceWire.IsValid(r, x => x.ReadVarUInt()));
        }

        public void Reset()
        {
            _match = null;
            _advanceAt = double.NaN;
            _heldHole = -1;
            _startedFor = 0;
            _finishedFor = 0;
            _lastSeenMatch = 0;
            _lastTurn = PeerId.None;
            _lastHole = 0;
            Publish();
        }

        /// <summary>The host's [Match] Competitive decides; a client follows whatever match the host sends.</summary>
        private bool Enabled => _context.Patcher.IsEnabled(PresenceFeature.Match) && (!_context.IsHost || _context.MatchSettings.Enabled);

        /// <summary>The session plays JustGolf (the host's mode is the session's, 4.6) and this game is in the world.</summary>
        private bool InNineHoles
        {
            get
            {
                var save = SaveManager.instance;
                return _context.InSession && _context.IsReady && _context.Scene == GameScene.Main && save != null
                       && save.m_gamemodeState != null && save.m_gamemodeState.mode == GameMode.JustGolf;
            }
        }

        private MatchPlayer LocalPlayer => _match?.Get(_context.LocalId);

        /// <summary>The local player is in a running match.</summary>
        public bool LocalInMatch
        {
            get
            {
                var me = LocalPlayer;
                return _match != null && _match.Phase == MatchPhase.Playing && me != null && !me.Left;
            }
        }

        /// <summary>The local player may play a shot now (no match: always).</summary>
        public bool LocalMayPlay
        {
            get
            {
                if (!LocalInMatch)
                    return true;
                var me = LocalPlayer;
                if (me.IsDone(_match.Hole))
                    return false;
                return _match.Mode == MatchMode.Free || _match.Turn == _context.LocalId;
            }
        }

        /// <summary>Why the local player must wait ("Alice's turn"), empty when they may play.</summary>
        public string WaitReason()
        {
            if (LocalMayPlay)
                return string.Empty;
            var me = LocalPlayer;
            if (me.IsDone(_match.Hole))
                return WaitingForOthersText();
            return _match.Turn.IsValid ? MatchText.Possessive(_context.PlayerName(_match.Turn)) + " turn" : "Waiting for the next hole";
        }

        private string WaitingForOthersText()
        {
            var names = new List<string>();
            foreach (var player in _match.Players)
            {
                if (!player.Left && !player.IsDone(_match.Hole))
                    names.Add(_context.PlayerName(player.Id));
            }
            if (names.Count == 0)
                return "Hole " + _match.Hole + " done. Next tee coming up";
            return "Waiting for " + MatchText.JoinNames(names) + " to finish hole " + _match.Hole;
        }

        // ---- Every frame ----------------------------------------------------------------------------------------

        public void Tick()
        {
            if (_match == null)
                return;
            try
            {
                if (_context.IsHost)
                    HostTick();
                ApplyToLocalGame();
            }
            catch (Exception e)
            {
                _context.Patcher.ReportFailure(PresenceFeature.Match, e);
            }
        }

        private void HostTick()
        {
            if (!InNineHoles)
            {
                // The host left the nine holes (menu, Story): the match is over.
                EndMatch("the host left the nine holes");
                return;
            }
            if (_match.Phase != MatchPhase.Playing)
                return;
            if (!_match.HoleComplete)
            {
                _advanceAt = double.NaN;
                return;
            }
            if (double.IsNaN(_advanceAt))
            {
                _advanceAt = _context.Now + HoleBreakSeconds;
                return;
            }
            if (_context.Now < _advanceAt)
                return;
            _advanceAt = double.NaN;
            if (_match.AdvanceHole())
            {
                ReadPars();
                Broadcast();
            }
        }

        /// <summary>Brings the local game in line with the match: start, catch up, release a held tee, finish.</summary>
        private void ApplyToLocalGame()
        {
            if (!InNineHoles || _context.IsLoadingWorld || _context.Replay.IsActive)
                return;
            var me = LocalPlayer;
            var save = SaveManager.instance;
            var lmugc = PanelManager.instance != null ? PanelManager.instance.m_LMUGCPanel : null;
            if (me == null || me.Left || lmugc == null || save == null)
                return;
            var run = save.m_run;

            // A round of this game's own that is ahead of the match (an old side-by-side round) starts over with it.
            bool ahead = run.isInLMUGC && _startedFor != _match.MatchId && GameHole(run) > _match.Hole;
            // Holed out on 9 first: the game ended and recorded its round at once; the others still play hole 9.
            if (!run.isInLMUGC && _finishedFor == _match.MatchId)
                return;
            if (_match.Phase == MatchPhase.Playing && (!run.isInLMUGC || ahead))
            {
                // Join the round: once per match, retried while the game refuses (dialogue, loading).
                if (_startedFor == _match.MatchId && _context.Now - _startTriedAt < StartRetrySeconds)
                    return;
                if (MoveAndHitController.instance == null || MoveAndHitController.instance.m_moveLocked)
                    return;
                _startedFor = _match.MatchId;
                _startTriedAt = _context.Now;
                _context.Info($"{Log}Starting the round with the match ({_context.PlayerName(_context.HostId)} hosts).");
                Bypass(() => lmugc.StartChallenge());
                return;
            }
            if (!run.isInLMUGC)
                return;
            // A round this game started itself (the player pressed the machine) at or behind the match is the match's
            // round: once adopted, finishing a hole before the others is no longer "ahead".
            if (_match.Phase == MatchPhase.Playing && _startedFor != _match.MatchId)
                _startedFor = _match.MatchId;

            int gameHole = GameHole(run);
            int target = _match.Phase == MatchPhase.Finished ? MatchState.Holes + 1 : _match.Hole;
            if (gameHole < target)
                CatchUp(lmugc, run, gameHole, target);

            if (_heldHole > 0 && (_match.Phase != MatchPhase.Playing || _match.Hole >= _heldHole))
            {
                int hole = _heldHole;
                _heldHole = -1;
                if (hole <= MatchState.Holes)
                    Bypass(() => lmugc.TeleportBallToHole(hole, false));
                Publish();
            }
        }

        /// <summary>
        /// The match is past the holes the local game has scored (a late joiner, a pick-up, a reload): those holes get the
        /// match's score and the player goes to the current hole's tee; after hole 9 the game's own round end runs.
        /// </summary>
        private void CatchUp(LMUGC lmugc, RunSave run, int gameHole, int target)
        {
            var me = LocalPlayer;
            if (run.lmugcScore == null || run.lmugcScore.Length < MatchState.Holes)
                return;
            for (int h = gameHole; h < target && h <= MatchState.Holes; h++)
            {
                int? score = _match.HoleScore(me, h);
                if (score == null)
                    return; // That hole is still open in the match: nothing to catch up yet.
                run.lmugcScore[h - 1] = Math.Max(1, score.Value);
            }
            _heldHole = -1;
            Bypass(() => lmugc.UpdateScoreCard());
            if (target > MatchState.Holes)
            {
                _context.Info($"{Log}Round over; the game records it.");
                _finishedFor = _match.MatchId;
                Bypass(() => lmugc.CompleteNormalGolfRound());
            }
            else
            {
                _context.Info($"{Log}Caught up to hole {target} (holes {gameHole}..{target - 1} scored from the match).");
                Bypass(() => lmugc.TeleportBallToHole(target, true));
            }
            Publish();
        }

        /// <summary>The hole the local game is on: 1 + the scored holes (the game's own rule).</summary>
        private static int GameHole(RunSave run)
        {
            if (run.lmugcScore == null)
                return 1;
            int n = 1;
            for (int i = 0; i < run.lmugcScore.Length && i < MatchState.Holes; i++)
            {
                if (run.lmugcScore[i] > 0)
                    n++;
            }
            return n;
        }

        private void Bypass(Action action)
        {
            bool was = _bypass;
            _bypass = true;
            try
            {
                action();
            }
            finally
            {
                _bypass = was;
            }
        }

        // ---- Host ---------------------------------------------------------------------------------------------

        private void HostStartMatch()
        {
            var settings = _context.MatchSettings;
            uint id;
            do
            {
                id = (uint)_random.Next(1, int.MaxValue);
            } while (_match != null && id == _match.MatchId);
            _match = new MatchState(id, settings.Turns, settings.MaxStrokes);
            ReadPars();
            _match.AddPlayer(_context.LocalId);
            foreach (var player in _context.Players)
            {
                if (player != null && !player.IsLocal && _context.TryGetPlayer(player.Id, out _))
                    _match.AddPlayer(player.Id);
            }
            _advanceAt = double.NaN;
            _heldHole = -1;
            _startedFor = id;
            _context.Info($"{Log}Match {id} started: {_match.ActiveCount} player(s), {settings.Turns}, " +
                          $"stroke limit {(settings.MaxStrokes > 0 ? settings.MaxStrokes.ToString() : "none")}.");
            Broadcast();
        }

        private void EndMatch(string why)
        {
            if (_match == null)
                return;
            _context.Info($"{Log}Match over: {why}.");
            _match = null;
            _heldHole = -1;
            if (_context.IsHost && _context.IsReady)
                _context.Send(PresenceRpc.Match, RpcTarget.All, (MatchState)null, (w, m) => MatchMessage.Write(w, m));
            Publish();
        }

        private void ReadPars()
        {
            if (_match == null || _access.Pars == null)
                return;
            try
            {
                var lmugc = PanelManager.instance != null ? PanelManager.instance.m_LMUGCPanel : Compat.FindAny<LMUGC>(true);
                var pars = lmugc != null ? _access.Pars(lmugc) : null;
                if (pars == null)
                    return;
                for (int h = 0; h < pars.Length && h < MatchState.Holes; h++)
                    _match.SetPar(h + 1, pars[h]);
            }
            catch (Exception e)
            {
                _context.Patcher.ReportFailure(PresenceFeature.RoundPars, e);
            }
        }

        private void Broadcast()
        {
            if (_match == null || !_context.IsHost)
                return;
            _context.Send(PresenceRpc.Match, RpcTarget.All, _match, (w, m) => MatchMessage.Write(w, m));
            Publish();
        }

        /// <summary>Host: a client finished loading the host's save; it joins a running match.</summary>
        public void OnPeerReady(PeerId peer)
        {
            if (!_context.IsHost || _match == null)
                return;
            if (_match.Phase == MatchPhase.Playing && _match.AddPlayer(peer))
            {
                var player = _match.Get(peer);
                if (player != null && player.JoinedAtHole > 1)
                    _context.Notify($"{_context.PlayerName(peer)} joins on hole {player.JoinedAtHole}; " +
                                    "the holes before count double bogey.", 6f);
            }
            Broadcast();
        }

        public void OnPlayerLeft(PeerId peer)
        {
            if (!_context.IsHost || _match == null)
                return;
            if (_match.RemovePlayer(peer))
                Broadcast();
        }

        /// <summary>The world went away (scene load): a held tee is gone with it.</summary>
        public void OnSceneLoaded()
        {
            _heldHole = -1;
            _cupsScene = -1;
            _startTriedAt = double.NegativeInfinity;
            _startedFor = 0;
        }

        private void HostReport(PeerId who, MatchStrokeMessage message)
        {
            if (_match == null || message.MatchId != _match.MatchId)
                return;
            bool changed = message.PickUp
                ? message.Hole == _match.Hole && _match.PickUp(who)
                : _match.ReportStroke(who, message.Hole, message.Strokes, message.Distance, message.Holed);
            if (changed)
                Broadcast();
        }

        // ---- Local reports ------------------------------------------------------------------------------------

        private void SendStroke(int hole, int strokes, float distance, bool holed, bool pickUp = false)
        {
            if (_match == null)
                return;
            var message = new MatchStrokeMessage
            {
                MatchId = _match.MatchId, Hole = hole, Strokes = strokes, Distance = distance, Holed = holed, PickUp = pickUp
            };
            if (_context.IsHost)
                HostReport(_context.LocalId, message);
            else
                _context.Send(PresenceRpc.MatchStroke, RpcTarget.Host, message, (w, m) => m.Write(w));
        }

        /// <summary>The local player gives up the current hole (the pick-up command).</summary>
        public bool PickUpLocal()
        {
            if (!LocalInMatch || LocalPlayer.IsDone(_match.Hole))
                return false;
            SendStroke(_match.Hole, LocalPlayer.Strokes[_match.Hole - 1], float.NaN, false, pickUp: true);
            return true;
        }

        private void OnLocalRest(Ball ball)
        {
            var save = SaveManager.instance;
            if (save == null || !save.m_run.isInLMUGC)
                return;
            var run = save.m_run;
            int hole = _match.Hole;
            // A ball that just dropped (scored by CompleteHole) also comes to rest: nothing more to say.
            if (run.lmugcScore != null && hole - 1 < run.lmugcScore.Length && run.lmugcScore[hole - 1] > 0)
                return;
            int strokes = Math.Max(0, ball.m_currentShot + 1);
            if (strokes == 0)
                return;
            // A killed ball (water, a wrong cup) is replayed from where it was hit.
            Vector3 from = ball.m_meshRenderer != null && !ball.m_meshRenderer.enabled
                ? PanelManager.instance.m_LMUGCPanel.m_shotFromPosition
                : ball.transform.position;
            SendStroke(hole, strokes, DistanceToCup(hole, from), false);
        }

        private void OnLocalHoled(string id, int score)
        {
            int hole = LmugcRules.FirstNumber(id);
            if (hole < 1 || hole > MatchState.Holes)
                hole = _match.Hole;
            SendStroke(hole, score, 0f, true);
        }

        /// <summary>Horizontal distance to hole <paramref name="hole"/>'s cup; NaN when the cup is unknown.</summary>
        private float DistanceToCup(int hole, Vector3 from)
        {
            if (_cupsScene != _context.SceneLoadId)
                FindCups();
            var cup = hole >= 1 && hole <= MatchState.Holes ? _cups[hole - 1] : null;
            if (cup == null)
                return float.NaN;
            float dx = from.x - cup.Value.x;
            float dz = from.z - cup.Value.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private void FindCups()
        {
            _cupsScene = _context.SceneLoadId;
            Array.Clear(_cups, 0, _cups.Length);
            if (_isHole == null)
                return;
            foreach (var target in Compat.FindAll<BallTarget>(true))
            {
                if (target == null || !_isHole(target) || target.holeNumber < 1 || target.holeNumber > MatchState.Holes)
                    continue;
                // The main cup of each hole is the one under 3D Course/HOLES (other cups share a number rarely; keep the
                // first found and prefer HOLE_HoleN by name).
                int i = target.holeNumber - 1;
                bool named = target.name.IndexOf("HOLE_Hole" + target.holeNumber, StringComparison.OrdinalIgnoreCase) >= 0;
                if (_cups[i] == null || named)
                    _cups[i] = target.transform.position;
            }
        }

        // ---- Remote -------------------------------------------------------------------------------------------

        private void OnMatch(PeerId sender, NetReader reader)
        {
            if (_context.IsHost || sender != _context.HostId)
                return;
            var match = MatchMessage.Read(reader);
            if (match == null)
            {
                if (_match != null)
                    EndMatch("the host ended it");
                return;
            }
            if (_match != null && _match.MatchId == match.MatchId && match.Sequence <= _match.Sequence)
                return;
            _match = match;
            Publish();
        }

        private void OnStroke(PeerId sender, NetReader reader)
        {
            var message = MatchStrokeMessage.Read(reader);
            if (_context.IsHost && sender != _context.LocalId)
                HostReport(sender, message);
        }

        private void OnStartRequest(PeerId sender, NetReader reader)
        {
            if (!_context.IsHost || !Enabled || !InNineHoles)
                return;
            if (_match != null && _match.Phase == MatchPhase.Playing)
            {
                Broadcast();
                return;
            }
            var lmugc = PanelManager.instance != null ? PanelManager.instance.m_LMUGCPanel : null;
            if (lmugc == null || (MoveAndHitController.instance != null && MoveAndHitController.instance.m_moveLocked))
                return;
            _context.Notify($"{_context.PlayerName(sender)} started the round for everyone.", 4f);
            // The host's own StartChallenge (its postfix starts the match) puts the host on the first tee too.
            _context.Post(() =>
            {
                try
                {
                    lmugc.StartChallenge();
                }
                catch (Exception e)
                {
                    _context.Patcher.ReportFailure(PresenceFeature.Match, e);
                }
            });
        }

        private void OnRetire(PeerId sender, NetReader reader)
        {
            uint id = reader.ReadVarUInt();
            if (!_context.IsHost || _match == null || id != _match.MatchId)
                return;
            if (_match.RemovePlayer(sender))
            {
                _context.Notify($"{_context.PlayerName(sender)} retired from the round.", 5f);
                Broadcast();
            }
        }

        // ---- Notices ------------------------------------------------------------------------------------------

        /// <summary>Raises <see cref="Changed"/> and says what changed for this player (turn, next hole, the end).</summary>
        private void Publish()
        {
            _context.Match = _match;
            if (_match != null)
            {
                bool fresh = _match.MatchId != _lastSeenMatch;
                if (fresh)
                {
                    _lastSeenMatch = _match.MatchId;
                    _lastHole = 0;
                    _lastTurn = PeerId.None;
                    _context.Notify(_match.Mode == MatchMode.Turns
                        ? "Competitive round: honours on the tee, then farthest from the cup plays."
                        : "Competitive round: everyone plays each hole, the next hole waits for all.", 6f);
                }
                if (_match.Phase == MatchPhase.Playing && _match.Hole != _lastHole)
                {
                    if (_lastHole > 0)
                        _context.Notify(MatchText.HoleSummary(_match, _lastHole, _context.PlayerName), 6f);
                    _lastHole = _match.Hole;
                }
                if (_match.Turn != _lastTurn)
                {
                    _lastTurn = _match.Turn;
                    if (_match.Turn == _context.LocalId)
                        _context.Notify("Your shot.", 3f);
                }
            }
            try
            {
                Changed?.Invoke();
            }
            catch (Exception e)
            {
                _context.ErrorOnce("pr.match.changed", $"{Log}Match change handler threw: {e}");
            }
        }

        private void BlockedNotice()
        {
            if (_context.Now - _lastNotice < NoticeGapSeconds)
                return;
            _lastNotice = _context.Now;
            _context.Notify(WaitReason() + ".", (float)NoticeGapSeconds);
        }

        // ---- Patches ------------------------------------------------------------------------------------------

        private static MatchSync Active(out GameContext context)
        {
            context = GameContext.Current;
            var self = PresenceModule.Instance?.Match;
            return context != null && self != null && !self._bypass && self.Enabled ? self : null;
        }

        private static bool StartPrefix()
        {
            var self = Active(out var context);
            if (self == null || !self.InNineHoles || context.IsHost)
                return true;
            try
            {
                // A client's machine press also asks the host, whose round (and match, when the host plays
                // competitive) starts for everyone. The client's own round starts as usual: without a match it is the
                // side-by-side round of 1.0, with one the match's hole 1 is where it is anyway.
                context.Send(PresenceRpc.MatchStart, RpcTarget.Host);
                return true;
            }
            catch (Exception e)
            {
                context.Patcher.ReportFailure(PresenceFeature.Match, e);
                return true;
            }
        }

        private static void StartPostfix()
        {
            var self = Active(out var context);
            if (self == null || !context.IsHost || !self.InNineHoles)
                return;
            try
            {
                self.HostStartMatch();
            }
            catch (Exception e)
            {
                context.Patcher.ReportFailure(PresenceFeature.Match, e);
            }
        }

        private static bool TeleportPrefix(int hole, bool teleportPlayer)
        {
            var self = Active(out var context);
            if (self == null || teleportPlayer || !self.LocalInMatch || hole <= self._match.Hole)
                return true;
            // The ball would go to the next tee: wait until everyone finished this hole.
            self._heldHole = hole;
            self.Publish();
            return false;
        }

        private static void CompleteHolePostfix(string id, int score)
        {
            var self = Active(out var context);
            if (self == null || !self.LocalInMatch)
                return;
            try
            {
                self.OnLocalHoled(id, score);
            }
            catch (Exception e)
            {
                context.Patcher.ReportFailure(PresenceFeature.Match, e);
            }
        }

        private static void RoundEndPrefix()
        {
            var self = Active(out var context);
            if (self == null || !self.LocalInMatch)
                return;
            try
            {
                var save = SaveManager.instance;
                var scores = save != null ? save.m_run.lmugcScore : null;
                if (scores == null || scores.Length < MatchState.Holes)
                    return;
                if (scores[MatchState.Holes - 1] > 0)
                {
                    // The game's own end of hole 9: this round is done and recorded, whatever the others still play.
                    self._finishedFor = self._match.MatchId;
                    return;
                }
                // Ended before hole 9 (pause menu "Restart From Hole 1"): this player retires from the match.
                if (context.IsHost)
                {
                    if (self._match.RemovePlayer(context.LocalId))
                        self.Broadcast();
                }
                else
                {
                    context.Send(PresenceRpc.MatchRetire, RpcTarget.Host, self._match.MatchId, (w, id) => w.WriteVarUInt(id));
                }
            }
            catch (Exception e)
            {
                context.Patcher.ReportFailure(PresenceFeature.Match, e);
            }
        }

        private static void UpdatePostfix()
        {
            var self = Active(out var context);
            if (self == null)
                return;
            try
            {
                var challenges = ChallengeSystem.instance;
                var reminder = challenges != null ? challenges.m_challengeReminder : null;
                if (reminder == null)
                    return;
                if (!self.LocalInMatch)
                {
                    // Over (or this player is out): ChallengeSystem.Update writes the reminder only when its last line
                    // changes, so forget that line and the game's own ("Press button to start round") comes back.
                    if (self._reminderShown)
                    {
                        self._reminderShown = false;
                        challenges.m_previousString = null;
                    }
                    return;
                }
                // The ball sits in the cup (hidden), so the game would say "Ball in water". The banner says who is still
                // playing; the reminder shows the player's own result instead of the same line twice.
                if (self._heldHole > 0 || self.LocalPlayer.IsDone(self._match.Hole))
                    reminder.SetString(MatchText.HoleDone(self._match, self._context.LocalId) ?? self.WaitingForOthersText());
                else if (!self.LocalMayPlay)
                    reminder.SetString(self.WaitReason());
                else
                    return;
                self._reminderShown = true;
            }
            catch (Exception e)
            {
                context.Patcher.ReportFailure(PresenceFeature.Match, e);
            }
        }

        private static bool RetakePrefix() => Gate(notice: true);

        private static bool InteractPrefix(FPSInteractable __instance)
        {
            if (__instance == null || __instance.m_interaction != InteractionType.ContinueShot)
                return true;
            return Gate(notice: true);
        }

        private static bool SwingPrefix(ClubSwing __instance)
        {
            if (__instance != null && __instance.m_isPracticeMode)
                return true;
            // Runs every physics frame: no notice (the turn banner says it).
            return Gate(notice: false);
        }

        private static bool Gate(bool notice)
        {
            var self = Active(out var context);
            if (self == null)
                return true;
            try
            {
                if (self.LocalMayPlay)
                    return true;
                var save = SaveManager.instance;
                if (save == null || !save.m_run.isInLMUGC)
                    return true;
                if (notice)
                    self.BlockedNotice();
                return false;
            }
            catch (Exception e)
            {
                context.Patcher.ReportFailure(PresenceFeature.Match, e);
                return true;
            }
        }

        private static void RestPostfix(Ball __0)
        {
            var self = Active(out var context);
            if (self == null || !self.LocalInMatch || context.Replay.IsActive)
                return;
            try
            {
                if (__0 != null && __0 == LocalPresence.LocalBall())
                    self.OnLocalRest(__0);
            }
            catch (Exception e)
            {
                context.Patcher.ReportFailure(PresenceFeature.Match, e);
            }
        }
    }
}
