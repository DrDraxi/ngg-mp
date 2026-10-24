// Regression tests of the harness bots against the confirmed game facts (docs/confirm answers-1/2): golf stance (#55),
// stroke numbering (#51), power as a fraction of the club's full swing (#53), rest distance (#54).
using System;
using System.Collections.Generic;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Game;
using NGGMultiplayer.Game.Presence;
using NGGMultiplayer.Testing;
using Xunit;

namespace NGGMultiplayer.Tests.Testing
{
    public class BotAvatarTests
    {
        private static readonly NetVector3 Host = new NetVector3(100f, 5f, 50f);

        private static BotAvatar Placed(double now = 0)
        {
            var avatar = new BotAvatar();
            avatar.SetAnchor(Host, true);
            avatar.Update(now);
            return avatar;
        }

        private static float FlatDistance(NetVector3 a, NetVector3 b) =>
            (float)Math.Sqrt((a.x - b.x) * (a.x - b.x) + (a.z - b.z) * (a.z - b.z));

        [Fact]
        public void HiddenWithoutAnAnchor()
        {
            var avatar = new BotAvatar();
            avatar.Update(0);
            var player = avatar.CapturePlayer();
            Assert.Equal(0, player.Flags & PoseFlags.Visible);
            Assert.Equal(0, avatar.CaptureBall().Flags & PoseFlags.Visible);
        }

        [Fact]
        public void StandsNearTheHost_AtFeetHeight_AndSnapsOnFirstCapture()
        {
            var avatar = Placed();
            var state = avatar.CapturePlayer();
            Assert.NotEqual(0, state.Flags & PoseFlags.Visible);
            Assert.Equal(0, state.Flags & PoseFlags.PlayerGolf);
            Assert.True(state.Teleport);
            Assert.Equal(Host.y, state.Position.y);
            Assert.Equal(BotAvatar.StandOffset, FlatDistance(state.Position, Host), 3);
            Assert.False(avatar.CapturePlayer().Teleport);
        }

        [Fact]
        public void GroundProbe_SetsTheFeetHeight()
        {
            var avatar = new BotAvatar { GroundHeight = (x, z, fallback) => 7.5f };
            avatar.SetAnchor(Host, true);
            avatar.Update(0);
            Assert.Equal(7.5f, avatar.CapturePlayer().Position.y);
        }

        [Fact]
        public void Walking_CirclesTheHost()
        {
            var avatar = Placed();
            avatar.Walking = true;
            for (int i = 1; i <= 50; i++)
                avatar.Update(i * 0.1);
            var state = avatar.CapturePlayer();
            Assert.Equal(BotAvatar.WalkRadius, FlatDistance(state.Position, Host), 2);
            Assert.Equal(BotAvatar.WalkSpeed, (float)Math.Sqrt(state.Velocity.SqrMagnitude), 2);
        }

        [Fact]
        public void Shot_BallOnTheWalkersSpot_GolferBesideIt()
        {
            // Answers #55: SwitchToGolf puts the ball on the walker's spot; the golfer addresses it from the side.
            var avatar = Placed();
            var walker = avatar.Position;
            Assert.True(avatar.Shoot(1.0, 0.5f));
            avatar.Update(1.1);
            Assert.True(avatar.InStance);
            Assert.Equal(walker.x, avatar.BallPosition.x, 4);
            Assert.Equal(walker.z, avatar.BallPosition.z, 4);

            var player = avatar.CapturePlayer();
            Assert.NotEqual(0, player.Flags & PoseFlags.PlayerGolf);
            Assert.Equal(GolfStance.StanceDistance, FlatDistance(player.Position, avatar.BallPosition), 3);

            // Same placement as the Presence module's real golfer pose.
            float aimYaw = (float)(Math.Atan2(Host.x - walker.x, Host.z - walker.z) * 180.0 / Math.PI);
            GolfStance.Place(walker.x, walker.z, aimYaw, out float x, out float z, out float yaw);
            Assert.Equal(x, player.Position.x, 3);
            Assert.Equal(z, player.Position.z, 3);
            Assert.Equal(yaw, (float)(avatar.Yaw * 180.0 / Math.PI + 360.0) % 360f, 1);
        }

        [Fact]
        public void Shot_FirstStrokeIsOne_PowerIsAFraction_RestDistanceToTheAim()
        {
            var avatar = Placed();
            Assert.True(avatar.Shoot(0, 5f)); // way past a full swing: clamped to 1 (answers #53)
            var events = new List<BotMotionEvent>();
            for (int i = 1; i <= 400; i++)
            {
                avatar.Update(i * 0.05);
                avatar.CaptureBall();
                avatar.DrainEvents(events);
            }
            Assert.Equal(2, events.Count);
            Assert.Equal(BotMotionEventKind.ShotLaunched, events[0].Kind);
            Assert.Equal(1u, events[0].Stroke); // answers #51: m_currentShot is 0-based, stroke = m_currentShot + 1
            Assert.Equal(BotAvatar.MaxPower, events[0].Power);
            Assert.Equal(BotMotionEventKind.BallRested, events[1].Kind);
            Assert.Equal(1u, events[1].Stroke);
            Assert.Equal(FlatDistance(events[1].Position, Host), events[1].Distance, 2);
            Assert.False(avatar.Golfing);
            Assert.Equal(0, avatar.CapturePlayer().Flags & PoseFlags.PlayerGolf);

            Assert.True(avatar.Shoot(30, 0.3f));
            for (int i = 1; i <= 400; i++)
            {
                avatar.Update(30 + i * 0.05);
                avatar.DrainEvents(events);
            }
            Assert.Equal(2u, events[2].Stroke);
        }

        [Fact]
        public void BallFlagsMovingInFlight_AndRestsOnTheGround()
        {
            var avatar = Placed();
            avatar.Shoot(0, 0.5f);
            avatar.Update(BotAvatar.StanceSeconds + 0.5);
            var flying = avatar.CaptureBall();
            Assert.NotEqual(0, flying.Flags & PoseFlags.BallMoving);
            Assert.True(flying.Position.y > Host.y);
            for (int i = 1; i <= 400; i++)
                avatar.Update(BotAvatar.StanceSeconds + 0.5 + i * 0.05);
            var rest = avatar.CaptureBall();
            Assert.Equal(0, rest.Flags & PoseFlags.BallMoving);
            Assert.NotEqual(0, rest.Flags & PoseFlags.Visible);
            Assert.Equal(Host.y + BotAvatar.BallRadius, rest.Position.y, 3);
        }

        [Fact]
        public void NoAim_RestDistanceIsTheGamesNoHoleValue()
        {
            // The bot right on the host: no aim point, so m_finishedDistance's 999 (answers #54).
            var avatar = new BotAvatar(0f);
            avatar.SetAnchor(Host, true);
            avatar.Update(0);
            avatar.SetAnchor(avatar.Position, true);
            avatar.Shoot(0, 0.2f);
            var events = new List<BotMotionEvent>();
            for (int i = 1; i <= 400; i++)
            {
                avatar.Update(i * 0.05);
                avatar.DrainEvents(events);
            }
            Assert.Equal(BotAvatar.NoHoleDistance, events[events.Count - 1].Distance);
        }

        [Fact]
        public void NoShotWithoutAnAnchor_OrWhileOneIsInProgress()
        {
            Assert.False(new BotAvatar().Shoot(0, 0.5f));
            var avatar = Placed();
            Assert.True(avatar.Shoot(0, 0.5f));
            Assert.False(avatar.Shoot(0.1, 0.5f));
            avatar.ResetBall();
            Assert.False(avatar.BallVisible);
            Assert.Equal(0u, avatar.Stroke);
            Assert.True(avatar.Shoot(0.2, 0.5f));
        }

        [Fact]
        public void HostTeleport_MovesTheBotWithoutWalking()
        {
            var avatar = Placed();
            avatar.CapturePlayer();
            avatar.SetAnchor(new NetVector3(500f, 0f, 500f), true);
            avatar.Update(0.1);
            var state = avatar.CapturePlayer();
            Assert.True(state.Teleport);
            Assert.Equal(BotAvatar.StandOffset, FlatDistance(state.Position, new NetVector3(500f, 0f, 500f)), 3);
        }
    }
}
