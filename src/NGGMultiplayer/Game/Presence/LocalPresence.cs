using System;
using System.Globalization;
using NGGMultiplayer.Core.Sync;
using UnityEngine;

namespace NGGMultiplayer.Game.Presence
{
    /// <summary>
    /// The local player as others see it (spec 7.1): golfer and ball poses for Core's entities, the ball's
    /// killed/reset state (patches on Ball.KillBall, HitManager.ResetBall, HitSequeneceManager.Restart) and the
    /// periodic <c>ngg.pr.info</c>.
    /// </summary>
    internal sealed class LocalPresence
    {
        private const double InfoInterval = 3.0;
        private const double InfoPollInterval = 0.25;

        private readonly GameContext _context;
        private bool _ballKilled;
        private bool _ballTeleport = true;
        private bool _playerTeleport = true;
        private bool _golfAnchorLogged;
        private int _lastMode = -1;
        private InfoMessage _lastInfo;
        private bool _hasSentInfo;
        private double _lastInfoSent = double.NegativeInfinity;
        private double _nextInfoPoll;

        public LocalPresence(GameContext context)
        {
            _context = context;
        }

        /// <summary>Supplies the HUD activity of an active round (set by the round tracker).</summary>
        public Func<string> RoundActivity { get; set; }

        public void Install()
        {
            var patcher = _context.Patcher;
            var type = typeof(LocalPresence);
            patcher.Postfix(PresenceFeature.BallState, typeof(Ball), "KillBall", Type.EmptyTypes, type, nameof(KillBallPostfix));
            patcher.Postfix(PresenceFeature.BallState, typeof(HitManager), "ResetBall", Type.EmptyTypes, type, nameof(BallResetPostfix));
            patcher.Postfix(PresenceFeature.BallState, typeof(HitSequeneceManager), "Restart", new[] { typeof(bool) }, type,
                nameof(BallResetPostfix));
        }

        public void OnSceneLoaded()
        {
            _ballKilled = false;
            _ballTeleport = true;
            _playerTeleport = true;
            _golfAnchorLogged = false;
            _lastMode = -1;
            _nextInfoPoll = 0;
        }

        public void OnSessionChanged()
        {
            _hasSentInfo = false;
            _lastInfoSent = double.NegativeInfinity;
            _nextInfoPoll = 0;
        }

        /// <summary>Send info at the next tick (a peer just became ready).</summary>
        public void InfoSoon() => _lastInfoSent = double.NegativeInfinity;

        // ---- Poses -----------------------------------------------------------------------------------

        public bool TryGetPlayerPose(out GamePose pose)
        {
            pose = default;
            if (!_context.Patcher.IsEnabled(PresenceFeature.Pose) || _context.Scene != GameScene.Main || !_context.WorldReady)
                return false;
            try
            {
                var controller = MoveAndHitController.instance;
                if (controller == null)
                    return false;
                bool golf = IsGolfMode(controller);
                if (golf)
                {
                    if (!TryGetGolfPose(controller, out pose))
                        return false;
                }
                else
                {
                    var character = controller.m_fpsCharacter;
                    if (character == null)
                        return false;
                    pose = new GamePose(character.GetPosition(), YawOnly(character.GetRotation()));
                    if (_context.Patcher.IsEnabled(PresenceFeature.PoseFlags))
                        ReadWalkState(character, ref pose);
                }
                // Walk and golf poses come from different objects: switching is a jump, not a slide.
                int mode = golf ? 1 : 0;
                if (mode != _lastMode || _playerTeleport)
                {
                    pose.Teleport = true;
                    _lastMode = mode;
                    _playerTeleport = false;
                }
                return true;
            }
            catch (Exception e)
            {
                _context.Patcher.ReportFailure(PresenceFeature.Pose, e);
                pose = default;
                return false;
            }
        }

        /// <summary>
        /// Golf mode: the avatar stands beside the ball, not at <c>m_golferHolder</c> (answers #55: SwitchToGolf moves
        /// the holder so that <c>m_ballPosition</c> lands on the walker's spot; the holder itself is offset). Anchor: the
        /// ball point, else the (inactive) walker's spot, else the holder; aim: the holder's yaw.
        /// </summary>
        private bool TryGetGolfPose(MoveAndHitController controller, out GamePose pose)
        {
            pose = default;
            var holder = controller.m_golferHolder;
            var ballPoint = controller.m_ballPosition;
            var character = controller.m_fpsCharacter;
            Vector3 anchor;
            string source;
            if (ballPoint != null)
            {
                anchor = ballPoint.position;
                source = "ball point";
            }
            else if (character != null)
            {
                anchor = character.GetPosition();
                source = "walker spot";
            }
            else if (holder != null)
            {
                anchor = holder.position;
                source = "golfer holder";
            }
            else
            {
                return false;
            }
            float aimYaw = (holder != null ? holder.rotation : ballPoint != null ? ballPoint.rotation : Quaternion.identity).eulerAngles.y;
            // The feet are on the ground the walker stood on (GetPosition is at the feet); the ball point is a ball's
            // radius above it.
            float feetY = character != null ? character.GetPosition().y : anchor.y;
            GolfStance.Place(anchor.x, anchor.z, aimYaw, out float x, out float z, out float yaw);
            pose = new GamePose(new Vector3(x, feetY, z), Quaternion.Euler(0f, yaw, 0f)) { Flags = PoseFlags.PlayerGolf };
            if (!_golfAnchorLogged)
            {
                _golfAnchorLogged = true;
                LogGolfAnchor(source, anchor, holder, character);
            }
            return true;
        }

        // Once per scene: the runtime check of answers #55 (the ball point should sit on the walker's spot).
        private void LogGolfAnchor(string source, Vector3 anchor, Transform holder, ECM2.Character character)
        {
            string walker = character != null ? Flat(anchor - character.GetPosition()).ToString("0.00", CultureInfo.InvariantCulture) + " m" : "n/a";
            string held = holder != null ? Flat(anchor - holder.position).ToString("0.00", CultureInfo.InvariantCulture) + " m" : "n/a";
            _context.Info($"[Presence] Golf pose anchored on the {source}; it is {walker} from the walker's spot and {held} from m_golferHolder (horizontal).");
        }

        private static float Flat(Vector3 v) => new Vector2(v.x, v.z).magnitude;

        private void ReadWalkState(ECM2.Character character, ref GamePose pose)
        {
            try
            {
                pose.Velocity = character.GetVelocity();
                pose.HasVelocity = true;
                if (character.IsCrouched())
                    pose.Flags |= PoseFlags.PlayerCrouched;
                if (!character.IsOnGround())
                    pose.Flags |= PoseFlags.PlayerAirborne;
            }
            catch (Exception e)
            {
                _context.Patcher.ReportFailure(PresenceFeature.PoseFlags, e);
            }
        }

        public bool TryGetBallPose(out GamePose pose)
        {
            pose = default;
            if (!_context.Patcher.IsEnabled(PresenceFeature.Ball) || _context.Scene != GameScene.Main || !_context.WorldReady)
                return false;
            try
            {
                var ball = LocalBall();
                if (ball == null || !ball.gameObject.activeInHierarchy)
                {
                    _ballTeleport = true;
                    return false;
                }
                var transform = ball.transform;
                pose = new GamePose(transform.position, transform.rotation);
                if (_ballKilled)
                {
                    pose.Hidden = true;
                    pose.Flags |= PoseFlags.BallHoled;
                }
                else if (_context.Patcher.IsEnabled(PresenceFeature.BallBody))
                {
                    var body = ball.m_rb;
                    if (body != null && !body.isKinematic)
                    {
                        pose.Velocity = Compat.GetVelocity(body);
                        pose.AngularVelocity = Compat.GetAngularVelocity(body);
                        pose.HasVelocity = true;
                        pose.Flags |= PoseFlags.BallMoving;
                    }
                }
                if (_ballTeleport && !pose.Hidden)
                {
                    pose.Teleport = true;
                    _ballTeleport = false;
                }
                return true;
            }
            catch (Exception e)
            {
                _context.Patcher.ReportFailure(PresenceFeature.Ball, e);
                pose = default;
                return false;
            }
        }

        internal static Ball LocalBall()
        {
            var hit = HitManager.instance;
            return hit != null ? hit.m_ball : null;
        }

        private static Quaternion YawOnly(Quaternion rotation) => Quaternion.Euler(0f, rotation.eulerAngles.y, 0f);

        // ControlMode is a top-level enum {Golf = 0, Walk = 1} and m_mode is public (answers #5, #48, #134).
        internal static bool IsGolfMode(MoveAndHitController controller) => controller.m_mode == ControlMode.Golf;

        private static bool IsCrouched(MoveAndHitController controller)
        {
            try
            {
                var character = controller.m_fpsCharacter;
                return character != null && character.IsCrouched();
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ---- Info ------------------------------------------------------------------------------------

        public void Tick()
        {
            if (!_context.IsReady || !_context.Patcher.IsEnabled(PresenceFeature.Info))
                return;
            double now = _context.Now;
            if (now < _nextInfoPoll)
                return;
            _nextInfoPoll = now + InfoPollInterval;
            InfoMessage info;
            try
            {
                info = CurrentInfo();
            }
            catch (Exception e)
            {
                _context.Patcher.ReportFailure(PresenceFeature.Info, e);
                return;
            }
            if (_hasSentInfo && info.Equals(_lastInfo) && now - _lastInfoSent < InfoInterval)
                return;
            if (_context.Send(PresenceRpc.Info, RpcTarget.Others, info, (w, m) => m.Write(w)))
            {
                _lastInfo = info;
                _hasSentInfo = true;
                _lastInfoSent = now;
            }
        }

        /// <summary>What the local player is doing now (also the HUD's own row).</summary>
        public InfoMessage CurrentInfo()
        {
            var info = new InfoMessage
            {
                Scene = (byte)_context.Scene,
                ControlMode = InfoMessage.Walk,
                SelectedBall = -1,
                Look = _context.Look
            };
            var save = SaveManager.instance;
            if (save != null)
                info.SelectedBall = HoledMessage.Clamp(save.m_unlocks.selectedBall, -1, PresenceWire.MaxSelectedBall);
            if (_context.Scene != GameScene.Main || !_context.WorldReady)
                return info;
            var controller = MoveAndHitController.instance;
            if (controller == null)
                return info;
            info.ControlMode = IsGolfMode(controller) ? InfoMessage.Golf : IsCrouched(controller) ? InfoMessage.Crouch : InfoMessage.Walk;
            var club = controller.m_selectedClub;
            info.Club = club != null ? PresenceWire.Truncate(club.clubName, PresenceWire.MaxClubBytes) : string.Empty;
            var pad = controller.m_activeBountyPad;
            // The others see why this player stands still (their own dialogue / cutscene locks them).
            if (controller.m_moveLocked)
                info.Activity = PresenceText.CutsceneActivity;
            else if (pad != null)
                info.Activity = PresenceWire.Truncate(PresenceText.BountyActivity(GameIds.ChallengeSetName(pad.m_bountyChallengeSet)),
                    PresenceWire.MaxActivityBytes);
            else
                info.Activity = RoundActivity?.Invoke() ?? string.Empty;
            return info;
        }

        // ---- Patches ---------------------------------------------------------------------------------

        private static void KillBallPostfix(Ball __instance)
        {
            var context = GameContext.Current;
            var self = PresenceModule.Instance?.Local;
            // Tracked offline too (it only sets a mod flag), so hosting mid-hole starts with the right state.
            if (context == null || self == null || !context.Patcher.IsEnabled(PresenceFeature.BallState))
                return;
            try
            {
                if (__instance != null && __instance == LocalBall())
                    self._ballKilled = true;
            }
            catch (Exception e)
            {
                context.Patcher.ReportFailure(PresenceFeature.BallState, e);
            }
        }

        private static void BallResetPostfix()
        {
            var context = GameContext.Current;
            var self = PresenceModule.Instance?.Local;
            if (context == null || self == null || !context.Patcher.IsEnabled(PresenceFeature.BallState))
                return;
            self._ballKilled = false;
            self._ballTeleport = true;
        }
    }
}
