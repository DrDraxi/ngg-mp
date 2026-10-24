using System.Linq;
using System.Reflection;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using Xunit;

namespace NGGMultiplayer.Tests
{
    public class ProtocolDefinitionTests
    {
        private static readonly string[] RangeNames =
        {
            "SessionFirst", "SessionLast", "SaveFirst", "SaveLast", "EntityFirst", "EntityLast",
            "RpcFirst", "RpcLast", "GameFirst", "GameLast",
        };

        private static (string Name, byte Value)[] MessageCodes() =>
            typeof(MessageType).GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.IsLiteral && f.FieldType == typeof(byte) && !RangeNames.Contains(f.Name))
                .Select(f => (f.Name, (byte)f.GetRawConstantValue()))
                .ToArray();

        [Fact]
        public void MessageCodes_AreUnique()
        {
            var codes = MessageCodes();
            Assert.Equal(codes.Length, codes.Select(c => c.Value).Distinct().Count());
            Assert.Equal(25, codes.Length);
        }

        [Fact]
        public void MessageCodes_LiveInTheirOwnersRange_AndHaveNames()
        {
            foreach (var (name, value) in MessageCodes())
            {
                Assert.True(MessageType.IsAssignedRange(value), name);
                Assert.False(MessageType.IsGame(value), name);
                Assert.Equal(name, MessageType.GetName(value));
            }
            Assert.True(MessageType.IsSession(MessageType.SessionEnded));
            Assert.True(MessageType.IsSave(MessageType.ProgressClaimRejected));
            Assert.True(MessageType.IsEntity(MessageType.StateRelayBatch));
            Assert.True(MessageType.IsRpc(MessageType.RpcRelay));
        }

        [Fact]
        public void Ranges_AreContiguousAndNonOverlapping()
        {
            Assert.Equal(MessageType.SessionLast + 1, MessageType.SaveFirst);
            Assert.Equal(MessageType.SaveLast + 1, MessageType.EntityFirst);
            Assert.Equal(MessageType.EntityLast + 1, MessageType.RpcFirst);
            Assert.Equal(MessageType.RpcLast + 1, MessageType.GameFirst);
            Assert.Equal(127, MessageType.GameLast);
            Assert.Equal("Game#90", MessageType.GetName(90));
            Assert.Equal("Unknown#0", MessageType.GetName(0));
            Assert.Equal("Unknown#200", MessageType.GetName(200));
        }

        [Fact]
        public void ProtocolInfo_Values()
        {
            Assert.Equal(2, ProtocolInfo.Version);
            Assert.Equal("1.0.0", ProtocolInfo.ModVersion);
        }

        [Fact]
        public void PacketBuilder_Begin_WritesTypeByte()
        {
            var w = PacketBuilder.Begin(MessageType.Ping);
            w.WriteVarUInt(5);
            var r = new NetReader(w.AsSegment());
            Assert.Equal(MessageType.Ping, r.ReadByte());
            Assert.Equal(5u, r.ReadVarUInt());
        }

        [Fact]
        public void PacketBuilder_BeginReusable_ResetsWriter()
        {
            var w = new NetWriter();
            w.WriteInt(123);
            PacketBuilder.Begin(w, MessageType.Hello);
            Assert.Equal(new[] { MessageType.Hello }, w.ToArray());
        }

        [Fact]
        public void PacketBuilder_Rent_ReturnsWriterToPool()
        {
            NetWriter first;
            using (var p = PacketBuilder.Rent(MessageType.State))
            {
                first = p.Writer;
                p.Writer.WriteByte(9);
                Assert.Equal(new byte[] { MessageType.State, 9 }, p.Segment.ToArray());
            }
            Assert.Equal(0, first.Length);
            using (var p = PacketBuilder.Rent(MessageType.Pong))
            {
                Assert.Equal(new[] { MessageType.Pong }, p.Segment.ToArray());
            }
        }

        [Fact]
        public void Pool_DoesNotRetainOversizedWriters()
        {
            var big = new NetWriter(NetWriterPool.MaxRetainedCapacity + 1);
            NetWriterPool.Return(big);
            Assert.NotSame(big, NetWriterPool.Rent());
        }

        [Fact]
        public void PooledPacket_EndToEndOverLoopback()
        {
            var hub = new LoopbackHub();
            var a = hub.CreateTransport(new PeerId(1));
            var b = hub.CreateTransport(new PeerId(2));
            using (var p = PacketBuilder.Rent(MessageType.Hello))
            {
                p.Writer.WriteInt(ProtocolInfo.Version);
                p.Writer.WriteString(ProtocolInfo.ModVersion);
                a.Send(b.LocalId, Channel.Control, Reliability.Reliable, p.Segment);
            }
            var list = new System.Collections.Generic.List<IncomingPacket>();
            b.Poll(list);
            var r = new NetReader(Assert.Single(list).Data);
            Assert.Equal(MessageType.Hello, r.ReadByte());
            Assert.Equal(ProtocolInfo.Version, r.ReadInt());
            Assert.Equal("1.0.0", r.ReadString());
        }

        [Fact]
        public void MathStructs_Basics()
        {
            Assert.True(new NetVector3(1, 2, 3).IsFinite);
            Assert.False(new NetVector3(float.NaN, 0, 0).IsFinite);
            Assert.False(new NetQuaternion(0, float.PositiveInfinity, 0, 1).IsFinite);
            Assert.Equal(NetQuaternion.Identity, new NetQuaternion(0, 0, 0, 0).Normalized());
            var n = new NetQuaternion(0, 0, 0, 2).Normalized();
            Assert.Equal(1f, n.w, 5);
            Assert.Equal(new NetVector3(5, 5, 5), NetVector3.Lerp(NetVector3.Zero, new NetVector3(10, 10, 10), 0.5f));
        }
    }
}
