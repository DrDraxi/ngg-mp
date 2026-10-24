using System;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Sync;
using Xunit;

namespace NGGMultiplayer.Tests.Sync
{
    public class StateBoundsAndClockTests
    {
        private static readonly PeerId Host = SyncNet.HostId;

        // Full-precision encoding written by hand, so values TransformCodec.Write refuses can still be put on the wire.
        private static byte[] RawStatePacket(NetEntityId id, byte epoch, double time, NetVector3 position, NetVector3 velocity)
        {
            var w = PacketBuilder.Begin(MessageType.State);
            w.WriteByte(1);
            NetEntityId.Write(w, id);
            w.WriteByte(epoch);
            w.WriteByte(1 << 1); // velocity present
            w.WriteDouble(time);
            w.WriteVector3(position);
            w.WriteQuaternion(NetQuaternion.Identity);
            w.WriteVector3(velocity);
            return w.ToArray();
        }

        [Theory]
        [InlineData(3e38f, 0f)]
        [InlineData(-2e6f, 0f)]
        [InlineData(0f, 3e38f)]
        [InlineData(0f, -2e5f)]
        public void ExtremeStatesFromAClient_AreDroppedAndNotRelayed(float x, float vx)
        {
            var net = new SyncNet(withRpc: false);
            var c1 = net.Connect(1);
            var c2 = net.Connect(2);
            net.Flush();
            var e = net.Host.Registry.Spawn("ball", c1.Id, States.At(0));
            net.Flush();
            int relayedBefore = c2.Session.CountReceived(MessageType.StateRelayBatch);
            long receivedBefore = net.Host.Registry.StatesReceived;

            SyncNet.SendRaw(c1, Host, RawStatePacket(e.Id, e.OwnerEpoch, net.Now, new NetVector3(x, 0, 0), new NetVector3(vx, 0, 0)),
                Channel.State, Reliability.Unreliable);
            net.Flush();

            Assert.Equal(receivedBefore, net.Host.Registry.StatesReceived);
            Assert.Equal(relayedBefore, c2.Session.CountReceived(MessageType.StateRelayBatch));
            Assert.Equal(0f, e.LastState.Position.x);
        }

        [Fact]
        public void StatesAtTheLimits_AreAccepted()
        {
            var net = new SyncNet(withRpc: false);
            var c1 = net.Connect(1);
            net.Flush();
            var e = net.Host.Registry.Spawn("ball", c1.Id, States.At(0));
            net.Flush();

            net.Run(0.05);
            var position = new NetVector3(TransformState.MaxCoordinate, -TransformState.MaxCoordinate, 0);
            SyncNet.SendRaw(c1, Host, RawStatePacket(e.Id, e.OwnerEpoch, net.Now, position, new NetVector3(TransformState.MaxSpeed, 0, 0)),
                Channel.State, Reliability.Unreliable);
            net.Flush();

            Assert.Equal(position, e.LastState.Position);
        }

        [Fact]
        public void Codec_RejectsOutOfRangeValues_OnBothSides()
        {
            var tooFar = States.At(TransformState.MaxCoordinate * 2);
            Assert.False(tooFar.IsValid);
            Assert.Throws<ArgumentException>(() => TransformCodec.Write(new NetWriter(), tooFar));

            var tooFast = States.Moving(0, TransformState.MaxSpeed * 2);
            Assert.False(tooFast.IsValid);
            Assert.Throws<ArgumentException>(() => TransformCodec.Write(new NetWriter(), tooFast));

            var spinning = States.At(0);
            spinning.AngularVelocity = new NetVector3(0, 0, -TransformState.MaxSpeed * 2);
            Assert.False(spinning.IsValid);

            var w = new NetWriter();
            w.WriteByte(1 << 1);
            w.WriteDouble(0);
            w.WriteVector3(NetVector3.Zero);
            w.WriteQuaternion(NetQuaternion.Identity);
            w.WriteVector3(new NetVector3(float.MaxValue, 0, 0));
            Assert.Throws<ProtocolException>(() => TransformCodec.Read(new NetReader(w.ToArray())));
        }

        [Fact]
        public void Buffer_NeverProducesNonFiniteSamples_FromExtremeButValidStates()
        {
            var buffer = new SnapshotBuffer(8) { MaxExtrapolation = 0.25 };
            var a = States.Moving(-TransformState.MaxCoordinate, TransformState.MaxSpeed, 0);
            var b = States.Moving(TransformState.MaxCoordinate, TransformState.MaxSpeed, 1);
            Assert.True(buffer.Add(a));
            Assert.True(buffer.Add(b));

            buffer.Sample(0.5, out var mid);
            Assert.True(mid.Position.IsFinite);
            buffer.Sample(1.2, out var ahead);
            Assert.True(ahead.Position.IsFinite);
        }

        [Fact]
        public void BackwardClockStep_DoesNotStallOwnedStates()
        {
            var net = new SyncNet(withRpc: false);
            var c1 = net.Connect(1);
            net.Flush();
            var mine = new TestEntity(null) { Current = States.At(1) };
            net.Host.Registry.Spawn("ball", Host, States.At(1), instance: mine);

            net.Host.Registry.Update(100.0);
            net.Host.Registry.Update(100.05);
            long before = net.Host.Registry.StatesSent;
            Assert.True(before > 0);

            // The host time estimate is corrected 1.35 s backwards.
            net.Host.Registry.Update(98.7);
            net.Host.Registry.Update(98.75);
            Assert.True(net.Host.Registry.StatesSent > before, "sending resumed right after the clock stepped back");
        }
    }
}
