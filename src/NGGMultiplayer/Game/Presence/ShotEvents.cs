using System;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Sync;

namespace NGGMultiplayer.Game.Presence
{
    /// <summary>
    /// Shot, rest and holed events of the local ball (spec 7.4). Other peers see a shot as the owner's swing and ball
    /// (no notice: the cutout, the ghost ball and its trail show it) and a holed ball as a notice. One rest and one
    /// holed event per shot: CompleteSequence / ShowBallInHoleFeedback can run more than once for a ball.
    /// </summary>
    internal sealed class ShotEvents
    {
        private readonly GameContext _context;
        private readonly PresenceAccess _access;
        private readonly PresenceRoster _roster;
        private int _shotSerial;
        private int _restSerial;
        private int _holedSerial;
        private uint _lastStroke;

        public ShotEvents(GameContext context, PresenceAccess access, PresenceRoster roster)
        {
            _context = context;
            _access = access;
            _roster = roster;
        }

        /// <summary>The local player's stroke count of the current hole (HUD).</summary>
        public uint LocalLastStroke => _lastStroke;

        /// <summary>The LMUGC hole the local player is on, or -1 (set by the round tracker).</summary>
        public Func<int> LmugcHole { get; set; }

        public void Install()
        {
            var patcher = _context.Patcher;
            var type = typeof(ShotEvents);
            patcher.Postfix(PresenceFeature.Shot, typeof(HitManager), "HitBall",
                new[] { typeof(float), typeof(float), typeof(bool), typeof(ShotType) }, type, nameof(HitBallPostfix));
            patcher.Postfix(PresenceFeature.Rest, typeof(HitSequeneceManager), "CompleteSequence", new[] { typeof(Ball) }, type,
                nameof(CompleteSequencePostfix));
            patcher.Postfix(PresenceFeature.Holed, typeof(HitSequeneceManager), "ShowBallInHoleFeedback",
                new[] { typeof(int), typeof(bool) }, type, nameof(BallInHolePostfix));

            _context.RegisterRpc(PresenceRpc.Shot, OnShot, (s, t, p, r) => PresenceWire.IsValid(r, x => ShotMessage.Read(x)));
            _context.RegisterRpc(PresenceRpc.Rest, OnRest, (s, t, p, r) => PresenceWire.IsValid(r, x => RestMessage.Read(x)));
            _context.RegisterRpc(PresenceRpc.Holed, OnHoled, (s, t, p, r) => PresenceWire.IsValid(r, x => HoledMessage.Read(x)));
        }

        public void Reset()
        {
            _restSerial = _shotSerial;
            _holedSerial = _shotSerial;
            _lastStroke = 0;
        }

        private bool CanSend(string feature) =>
            _context.Patcher.IsEnabled(feature) && _context.IsReady && _context.Scene == GameScene.Main
            && !_context.Replay.IsActive;

        // ---- Local events ----------------------------------------------------------------------------

        private void OnLocalShot(float power, bool isMiss, ShotType shot)
        {
            var hit = HitManager.instance;
            var ball = hit != null ? hit.m_ball : null;
            if (ball == null)
                return;
            _shotSerial++;
            _lastStroke = Stroke(ball);
            var controller = MoveAndHitController.instance;
            var club = controller != null ? controller.m_selectedClub : null;
            byte spin = 0;
            try
            {
                spin = (byte)(int)hit.GetSpinType();
            }
            catch (Exception e)
            {
                _context.WarnOnce("pr.shot.spin", $"[Presence] HitManager.GetSpinType failed: {e.Message}");
            }
            var position = ball.transform.position;
            var message = new ShotMessage
            {
                Club = club != null ? club.clubName : string.Empty,
                Power = PresenceText.PowerFraction(SwingPower(power), PowerScale(club)),
                ShotType = (byte)(int)shot,
                SpinType = spin,
                Stroke = _lastStroke,
                IsMiss = isMiss,
                From = new NetVector3(position.x, position.y, position.z)
            };
            if (float.IsNaN(message.Power) || float.IsInfinity(message.Power) || !message.From.IsFinite)
                return;
            _context.Send(PresenceRpc.Shot, RpcTarget.Others, message, (w, m) => m.Write(w));
        }

        /// <summary>
        /// The swing's power on the club's 0..powerVisualMult scale: ClubSwing.m_power (answers #127). HitBall's power
        /// argument is already scaled by the game, so it is only the fallback when the swing panel can't be read.
        /// </summary>
        private static float SwingPower(float hitPower)
        {
            var panels = PanelManager.instance;
            var swing = panels != null ? panels.m_clubSwingPanel : null;
            return swing != null ? swing.m_power : hitPower;
        }

        /// <summary>
        /// The club's full-swing power: the swing panel's m_currentClub (the club ClubSwing clamps m_power to; answers
        /// #53), else the selected club. 0 when unknown.
        /// </summary>
        private float PowerScale(Club selected)
        {
            var panels = PanelManager.instance;
            var swing = panels != null ? panels.m_clubSwingPanel : null;
            var club = swing != null && swing.m_currentClub != null ? swing.m_currentClub : selected;
            return _access.PowerVisualMult(club);
        }

        private void OnLocalRest(Ball ball)
        {
            // A rest without a shot (resets, resumed shots) or a second call for the same shot says nothing new.
            if (_restSerial == _shotSerial)
                return;
            _restSerial = _shotSerial;
            var data = ball.m_currentShotData;
            var position = ball.transform.position;
            var message = new RestMessage
            {
                Position = new NetVector3(position.x, position.y, position.z),
                // To the closest hole in front, 999 without one, -1 after a reset (answers #54).
                Distance = data != null ? data.m_finishedDistance : -1f,
                Surface = data != null ? data.m_lastTouchedSurface : string.Empty,
                Stroke = _lastStroke
            };
            if (!message.Position.IsFinite || float.IsNaN(message.Distance) || float.IsInfinity(message.Distance))
                return;
            _context.Send(PresenceRpc.Rest, RpcTarget.Others, message, (w, m) => m.Write(w));
        }

        private void OnLocalHoled(int par)
        {
            if (_holedSerial == _shotSerial)
                return;
            _holedSerial = _shotSerial;
            var ball = LocalPresence.LocalBall();
            uint strokes = ball != null ? Stroke(ball) : _lastStroke;
            if (strokes > 0)
                _lastStroke = strokes;
            var message = new HoledMessage { Par = par, Strokes = strokes, LmugcHole = LmugcHole?.Invoke() ?? -1 };
            _context.Send(PresenceRpc.Holed, RpcTarget.Others, message, (w, m) => m.Write(w));
        }

        /// <summary>
        /// m_currentShot + 1: m_currentShot is 0-based and the shot itself does not increment it (Restart(true) does,
        /// before the next shot), so this is right both in the HitBall postfix and when the ball drops (answers #51).
        /// -1 (TeleportBallToHole without the player) gives 0: unknown.
        /// </summary>
        private static uint Stroke(Ball ball)
        {
            if (ball == null)
                return 0;
            int shot = ball.m_currentShot;
            return shot < 0 ? 0u : (uint)Math.Min(shot + 1, PresenceWire.MaxStroke);
        }

        // ---- Remote events ---------------------------------------------------------------------------

        private void OnShot(PeerId sender, NetReader reader)
        {
            var message = ShotMessage.Read(reader);
            if (sender == _context.LocalId)
                return;
            _roster.ApplyStroke(sender, message.Stroke);
            DirectionalAvatar.PlaySwing(sender, message.Club);
        }

        private void OnRest(PeerId sender, NetReader reader)
        {
            var message = RestMessage.Read(reader);
            if (sender == _context.LocalId)
                return;
            if (message.Stroke > 0)
                _roster.ApplyStroke(sender, message.Stroke);
        }

        private void OnHoled(PeerId sender, NetReader reader)
        {
            var message = HoledMessage.Read(reader);
            if (sender == _context.LocalId)
                return;
            if (message.Strokes > 0)
                _roster.ApplyStroke(sender, message.Strokes);
            _context.Notify(PresenceText.HoledToast(_context.PlayerName(sender), message.Strokes, message.Par), 5f);
        }

        // ---- Patches ---------------------------------------------------------------------------------

        // HitBall(float angle, float power, bool isMiss, ShotType shot): parameters by position (spec rule 5).
        private static void HitBallPostfix(float __1, bool __2, ShotType __3)
        {
            var context = GameContext.Current;
            var self = PresenceModule.Instance?.Shots;
            if (context == null || self == null || !self.CanSend(PresenceFeature.Shot))
                return;
            try
            {
                self.OnLocalShot(__1, __2, __3);
            }
            catch (Exception e)
            {
                context.Patcher.ReportFailure(PresenceFeature.Shot, e);
            }
        }

        private static void CompleteSequencePostfix(Ball __0)
        {
            var context = GameContext.Current;
            var self = PresenceModule.Instance?.Shots;
            if (context == null || self == null || !self.CanSend(PresenceFeature.Rest))
                return;
            try
            {
                if (__0 != null && __0 == LocalPresence.LocalBall())
                    self.OnLocalRest(__0);
            }
            catch (Exception e)
            {
                context.Patcher.ReportFailure(PresenceFeature.Rest, e);
            }
        }

        private static void BallInHolePostfix(int __0)
        {
            var context = GameContext.Current;
            var self = PresenceModule.Instance?.Shots;
            if (context == null || self == null || !self.CanSend(PresenceFeature.Holed))
                return;
            try
            {
                self.OnLocalHoled(__0);
            }
            catch (Exception e)
            {
                context.Patcher.ReportFailure(PresenceFeature.Holed, e);
            }
        }
    }
}
