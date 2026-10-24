using System;
using System.Collections.Generic;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Sync;
using NGGMultiplayer.Game;
using NGGMultiplayer.Game.Presence;

namespace NGGMultiplayer.Testing
{
    /// <summary>What a bot's simulated golfer did that real clients announce with an RPC.</summary>
    public enum BotMotionEventKind : byte
    {
        /// <summary>The ball left the club (a real client's HitBall postfix sends <c>ngg.pr.shot</c>).</summary>
        ShotLaunched,

        /// <summary>The ball came to rest (a real client's CompleteSequence postfix sends <c>ngg.pr.rest</c>).</summary>
        BallRested
    }

    public readonly struct BotMotionEvent
    {
        public BotMotionEvent(BotMotionEventKind kind, NetVector3 position, float power, float distance, uint stroke)
        {
            Kind = kind;
            Position = position;
            Power = power;
            Distance = distance;
            Stroke = stroke;
        }

        public readonly BotMotionEventKind Kind;

        /// <summary>Launch: where the ball was struck. Rest: where it stopped.</summary>
        public readonly NetVector3 Position;

        /// <summary>Fraction of the club's full swing, as <c>ngg.pr.shot</c> carries it (answers #53).</summary>
        public readonly float Power;

        /// <summary>Rest only: XZ distance to the aim point in Unity units, 999 without one (as m_finishedDistance, #54).</summary>
        public readonly float Distance;

        /// <summary>m_currentShot + 1 of the shot (#51: 0-based, so the first shot of a hole is stroke 1).</summary>
        public readonly uint Stroke;
    }

    /// <summary>
    /// A test bot's simulated golfer and ball, as the poses its player-bound entities stream (the same
    /// <see cref="TransformState"/> layout <c>LocalPoseEntity</c> produces: feet position, yaw-only rotation,
    /// <see cref="PoseFlags"/>). The bot stands or walks a circle around an anchor (the host's own player). A shot
    /// works like the game's (answers #55): the ball sits on the walker's spot and the golfer addresses it from the side
    /// (<see cref="GolfStance"/>, the pose the Presence module sends for a real golfer), then a ballistic flight and a
    /// roll toward the aim point (the anchor plays the hole). Deterministic for given inputs; time is passed in. No
    /// Unity types (unit tested).
    /// </summary>
    public sealed class BotAvatar
    {
        public const float WalkRadius = 3f;
        public const float WalkSpeed = 1.4f;
        public const float StandOffset = 2f;
        public const float BallRadius = 0.0214f;
        public const float Gravity = 9.81f;
        public const float LaunchAngleDegrees = 25f;

        /// <summary>Launch speed at a full swing (power fraction 1; a full drive carries about 150 units).</summary>
        public const float FullPowerSpeed = 42f;

        /// <summary>
        /// Shot power is a fraction of the club's full swing (answers #53: ClubSwing.m_power runs 0..powerVisualMult per
        /// club, and Presence sends m_power / powerVisualMult), so a bot never swings harder than 1.
        /// </summary>
        public const float MinPower = 0.05f;
        public const float MaxPower = 1f;

        public const float RollFactor = 0.25f;
        public const float RollDeceleration = 4f;
        public const double StanceSeconds = 1.0;

        /// <summary>Further than this between captures and remote peers snap (as LocalPoseEntity's 20 m).</summary>
        public const float TeleportDistance = 20f;

        /// <summary>m_finishedDistance without a hole in front (#54).</summary>
        public const float NoHoleDistance = 999f;

        /// <summary>Closer than this to the anchor the bot has no aim point and shoots along its facing.</summary>
        public const float MinAimDistance = 1f;

        private readonly List<BotMotionEvent> _events = new List<BotMotionEvent>();
        private readonly float _phaseOffset;

        private NetVector3 _anchor;
        private bool _hasAnchor;
        private NetVector3 _position;
        private float _yaw;
        private NetVector3 _velocity;
        private bool _placed;
        private double _lastUpdate = double.NaN;
        private float _walkAngle;

        private ShotPhase _phase = ShotPhase.None;
        private double _phaseStart;
        private NetVector3 _walkerSpot;
        private float _walkerYaw;
        private NetVector3 _stancePosition;
        private float _stanceYaw;
        private NetVector3 _ballPosition;
        private NetVector3 _ballVelocity;
        private bool _ballPlaced;
        private NetVector3 _launchFrom;
        private NetVector3 _launchVelocity;
        private NetVector3 _rollFrom;
        private NetVector3 _rollDirection;
        private float _rollSpeed;
        private bool _hasAim;
        private NetVector3 _aim;
        private float _power;

        private bool _playerTeleport = true;
        private bool _ballTeleport = true;
        private NetVector3 _lastPlayerSent;
        private bool _playerSentOnce;

        /// <param name="phase">Start angle on the walking circle in radians, so several bots spread out.</param>
        public BotAvatar(float phase = 0f)
        {
            _phaseOffset = phase;
            _walkAngle = phase;
        }

        public bool Walking { get; set; }

        /// <summary>
        /// Ground height at (x, z), given a fallback height; null keeps the anchor's height. The harness raycasts the
        /// host's world here so a bot does not float or sink on slopes.
        /// </summary>
        public Func<float, float, float, float> GroundHeight { get; set; }

        public bool HasAnchor => _hasAnchor;

        public NetVector3 Position => _position;

        /// <summary>Radians, turning +Z (forward) toward +X: Unity's yaw.</summary>
        public float Yaw => _yaw;

        public uint Stroke { get; private set; }

        /// <summary>A shot is being played (stance, flight or roll).</summary>
        public bool ShotInProgress => _phase != ShotPhase.None && _phase != ShotPhase.Rest;

        /// <summary>In golf mode (stance through roll): the player pose carries <see cref="PoseFlags.PlayerGolf"/>.</summary>
        public bool Golfing => ShotInProgress;

        public bool BallInFlight => _phase == ShotPhase.Flight || _phase == ShotPhase.Roll;

        public bool InStance => _phase == ShotPhase.Stance;

        public NetVector3 BallPosition => _ballPosition;

        public bool BallVisible => _ballPlaced;

        /// <summary>The point the bot circles and aims at. Invalid hides the bot (the host has no golfer, e.g. in a menu).</summary>
        public void SetAnchor(NetVector3 position, bool valid)
        {
            if (!valid || !position.IsFinite)
            {
                _hasAnchor = false;
                return;
            }
            if (_hasAnchor && Distance(position, _anchor) > TeleportDistance)
            {
                // The host teleported (hole change, elevator): follow without walking across the map.
                _placed = false;
            }
            _anchor = position;
            _hasAnchor = true;
        }

        /// <summary>
        /// Starts a shot with <paramref name="power"/> as a fraction of a full swing: the ball goes down on the bot's
        /// spot, the bot takes its stance beside it, and swings after <see cref="StanceSeconds"/>. False while a shot
        /// is in progress or without an anchor.
        /// </summary>
        public bool Shoot(double now, float power)
        {
            if (ShotInProgress || !_hasAnchor)
                return false;
            if (!_placed)
                Place();
            _power = Clamp(power, MinPower, MaxPower);
            _walkerSpot = _position;
            _walkerYaw = _yaw;
            // Answers #55: SwitchToGolf moves the golfer so the ball lands on the spot the walker stood on.
            _ballPosition = new NetVector3(_position.x, Ground(_position.x, _position.z, _position.y) + BallRadius, _position.z);
            _ballVelocity = NetVector3.Zero;
            _ballPlaced = true;
            _ballTeleport = true;
            _hasAim = Distance(Flat(_anchor), Flat(_ballPosition)) > MinAimDistance;
            _aim = _anchor;
            float aimYaw = _hasAim ? YawOf(Flat(_aim - _ballPosition), _yaw) : _yaw;
            GolfStance.Place(_ballPosition.x, _ballPosition.z, ToDegrees(aimYaw), out float x, out float z, out float yawDegrees);
            _stancePosition = new NetVector3(x, Ground(x, z, _position.y), z);
            _stanceYaw = ToRadians(yawDegrees);
            _phase = ShotPhase.Stance;
            _phaseStart = now;
            return true;
        }

        /// <summary>Hides the ball (the next shot re-tees it) and resets the stroke count (a new hole).</summary>
        public void ResetBall()
        {
            if (ShotInProgress)
            {
                _position = _walkerSpot;
                _yaw = _walkerYaw;
            }
            _phase = ShotPhase.None;
            _ballPlaced = false;
            _ballVelocity = NetVector3.Zero;
            Stroke = 0;
        }

        /// <summary>Events since the last call (launches and rests), oldest first.</summary>
        public void DrainEvents(List<BotMotionEvent> into)
        {
            into.AddRange(_events);
            _events.Clear();
        }

        public void Update(double now)
        {
            double dt = double.IsNaN(_lastUpdate) ? 0 : Math.Max(0, Math.Min(0.5, now - _lastUpdate));
            _lastUpdate = now;
            if (!_hasAnchor)
            {
                _velocity = NetVector3.Zero;
                return;
            }
            if (!_placed)
                Place();

            bool wasGolfing = Golfing;
            UpdateShot(now);

            if (Golfing)
            {
                // Golf mode: the golfer stands beside the ball (on the walker's spot), facing across the aim line.
                _position = _stancePosition;
                _yaw = _stanceYaw;
                _velocity = NetVector3.Zero;
                return;
            }
            if (wasGolfing)
            {
                // Back to walking where the walker stood before the shot.
                _position = _walkerSpot;
                _yaw = _walkerYaw;
            }

            if (Walking)
            {
                float omega = WalkSpeed / WalkRadius;
                _walkAngle = (float)((_walkAngle + omega * dt) % (2 * Math.PI));
                float ox = (float)Math.Cos(_walkAngle) * WalkRadius;
                float oz = (float)Math.Sin(_walkAngle) * WalkRadius;
                float nx = _anchor.x + ox;
                float nz = _anchor.z + oz;
                var next = new NetVector3(nx, Ground(nx, nz, _anchor.y), nz);
                // Tangent of the circle (counter-clockwise seen from above).
                var tangent = new NetVector3(-(float)Math.Sin(_walkAngle), 0f, (float)Math.Cos(_walkAngle));
                _velocity = tangent * WalkSpeed;
                _yaw = YawOf(tangent, _yaw);
                if (Distance(next, _position) > 1.5f)
                    _playerTeleport = true;
                _position = next;
                return;
            }

            _velocity = NetVector3.Zero;
            // Standing: keep the feet on the ground (the anchor's height without a probe), so the bot does not float
            // when the host walks up a hill.
            _position = new NetVector3(_position.x, Ground(_position.x, _position.z, _anchor.y), _position.z);
        }

        /// <summary>The player entity's state, as LocalPoseEntity builds it (feet position, answers #55).</summary>
        public TransformState CapturePlayer()
        {
            if (!_hasAnchor)
            {
                _playerTeleport = true;
                return Hidden();
            }
            var state = new TransformState(_position, YawRotation(_yaw))
            {
                Velocity = _velocity,
                Flags = (byte)(PoseFlags.Visible | (Golfing ? PoseFlags.PlayerGolf : 0))
            };
            if (_playerTeleport || (_playerSentOnce && Distance(_position, _lastPlayerSent) > TeleportDistance))
                state.Teleport = true;
            _playerTeleport = false;
            _playerSentOnce = true;
            _lastPlayerSent = _position;
            return state;
        }

        /// <summary>The ball entity's state: hidden until the first shot, <see cref="PoseFlags.BallMoving"/> in flight and roll.</summary>
        public TransformState CaptureBall()
        {
            if (!_hasAnchor || !_ballPlaced)
            {
                _ballTeleport = true;
                return Hidden();
            }
            var state = new TransformState(_ballPosition, NetQuaternion.Identity)
            {
                Velocity = _ballVelocity,
                Flags = (byte)(PoseFlags.Visible | (BallInFlight ? PoseFlags.BallMoving : 0)),
                Teleport = _ballTeleport
            };
            _ballTeleport = false;
            return state;
        }

        // ---- Shot -------------------------------------------------------------------------------------

        private void UpdateShot(double now)
        {
            switch (_phase)
            {
                case ShotPhase.Stance:
                    if (now - _phaseStart >= StanceSeconds)
                        Launch(now);
                    break;
                case ShotPhase.Flight:
                {
                    float t = (float)(now - _phaseStart);
                    float airtime = 2f * _launchVelocity.y / Gravity;
                    if (t >= airtime)
                    {
                        var landing = _launchFrom + new NetVector3(_launchVelocity.x * airtime, 0f, _launchVelocity.z * airtime);
                        landing = OnGround(landing);
                        var horizontal = Flat(_launchVelocity);
                        float speed = Length(horizontal) * RollFactor;
                        _rollFrom = landing;
                        _rollDirection = Normalize(horizontal);
                        _rollSpeed = speed;
                        _phase = ShotPhase.Roll;
                        _phaseStart = _phaseStart + airtime;
                        _ballPosition = landing;
                        _ballVelocity = _rollDirection * speed;
                        UpdateShot(now);
                        break;
                    }
                    _ballPosition = _launchFrom + new NetVector3(_launchVelocity.x * t,
                        _launchVelocity.y * t - 0.5f * Gravity * t * t, _launchVelocity.z * t);
                    _ballVelocity = new NetVector3(_launchVelocity.x, _launchVelocity.y - Gravity * t, _launchVelocity.z);
                    break;
                }
                case ShotPhase.Roll:
                {
                    float t = (float)(now - _phaseStart);
                    float stopAfter = _rollSpeed / RollDeceleration;
                    if (t >= stopAfter)
                    {
                        float travelled = _rollSpeed * stopAfter * 0.5f;
                        _ballPosition = OnGround(_rollFrom + _rollDirection * travelled);
                        _ballVelocity = NetVector3.Zero;
                        _phase = ShotPhase.Rest;
                        float distance = _hasAim ? Length(Flat(_aim - _ballPosition)) : NoHoleDistance;
                        _events.Add(new BotMotionEvent(BotMotionEventKind.BallRested, _ballPosition, _power, distance, Stroke));
                        break;
                    }
                    float s = _rollSpeed * t - 0.5f * RollDeceleration * t * t;
                    _ballPosition = OnGround(_rollFrom + _rollDirection * s);
                    _ballVelocity = _rollDirection * (_rollSpeed - RollDeceleration * t);
                    break;
                }
            }
        }

        private void Launch(double now)
        {
            var direction = _hasAim ? Normalize(Flat(_aim - _ballPosition)) : Forward(_walkerYaw);
            float speed = FullPowerSpeed * _power;
            double angle = LaunchAngleDegrees * Math.PI / 180.0;
            float horizontal = (float)(speed * Math.Cos(angle));
            float vertical = (float)(speed * Math.Sin(angle));
            _launchFrom = _ballPosition;
            _launchVelocity = new NetVector3(direction.x * horizontal, vertical, direction.z * horizontal);
            _ballVelocity = _launchVelocity;
            _phase = ShotPhase.Flight;
            _phaseStart = now;
            // Answers #51: m_currentShot is 0-based and the shot does not increment it; the stroke sent is
            // m_currentShot + 1, so a hole's first shot is stroke 1 (the game counts it up before the next shot).
            Stroke++;
            _events.Add(new BotMotionEvent(BotMotionEventKind.ShotLaunched, _launchFrom, _power, 0f, Stroke));
            UpdateShot(now);
        }

        // ---- Helpers ----------------------------------------------------------------------------------

        private void Place()
        {
            var offset = new NetVector3((float)Math.Cos(_phaseOffset) * StandOffset, 0f, (float)Math.Sin(_phaseOffset) * StandOffset);
            float x = _anchor.x + offset.x;
            float z = _anchor.z + offset.z;
            _position = new NetVector3(x, Ground(x, z, _anchor.y), z);
            _yaw = YawOf(Flat(_anchor - _position), 0f);
            _walkAngle = (float)Math.Atan2(offset.z, offset.x);
            _placed = true;
            _playerTeleport = true;
        }

        private float Ground(float x, float z, float fallback)
        {
            var probe = GroundHeight;
            if (probe == null)
                return fallback;
            float y = probe(x, z, fallback);
            return float.IsNaN(y) || float.IsInfinity(y) ? fallback : y;
        }

        private NetVector3 OnGround(NetVector3 ball) =>
            GroundHeight == null ? ball : new NetVector3(ball.x, Ground(ball.x, ball.z, ball.y - BallRadius) + BallRadius, ball.z);

        private static TransformState Hidden() => new TransformState(NetVector3.Zero, NetQuaternion.Identity);

        /// <summary>Unity's rotation about +Y that turns +Z (forward) by <paramref name="yaw"/> radians toward +X.</summary>
        public static NetQuaternion YawRotation(float yaw) =>
            new NetQuaternion(0f, (float)Math.Sin(yaw * 0.5), 0f, (float)Math.Cos(yaw * 0.5));

        public static NetVector3 Forward(float yaw) => new NetVector3((float)Math.Sin(yaw), 0f, (float)Math.Cos(yaw));

        private static float YawOf(NetVector3 direction, float fallback) =>
            direction.x * direction.x + direction.z * direction.z < 1e-8f ? fallback : (float)Math.Atan2(direction.x, direction.z);

        private static float ToDegrees(float radians) => (float)(radians * 180.0 / Math.PI);

        private static float ToRadians(float degrees) => (float)(degrees * Math.PI / 180.0);

        private static NetVector3 Flat(NetVector3 v) => new NetVector3(v.x, 0f, v.z);

        private static float Length(NetVector3 v) => (float)Math.Sqrt(v.SqrMagnitude);

        private static NetVector3 Normalize(NetVector3 v)
        {
            float length = Length(v);
            return length < 1e-6f ? new NetVector3(0f, 0f, 1f) : v * (1f / length);
        }

        private static float Distance(NetVector3 a, NetVector3 b) => Length(a - b);

        private static float Clamp(float value, float min, float max)
        {
            if (float.IsNaN(value))
                return min;
            return value < min ? min : value > max ? max : value;
        }

        private enum ShotPhase : byte
        {
            None,
            Stance,
            Flight,
            Roll,
            Rest
        }
    }
}
