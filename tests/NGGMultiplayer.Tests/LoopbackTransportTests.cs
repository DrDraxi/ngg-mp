using System;
using System.Collections.Generic;
using System.Linq;
using NGGMultiplayer.Core.Net;
using Xunit;

namespace NGGMultiplayer.Tests
{
    public class LoopbackTransportTests
    {
        private static readonly PeerId A = new PeerId(1);
        private static readonly PeerId B = new PeerId(2);
        private static readonly PeerId C = new PeerId(3);

        private static ArraySegment<byte> Seg(params byte[] bytes) => new ArraySegment<byte>(bytes);

        private static List<IncomingPacket> Drain(ITransport t)
        {
            var list = new List<IncomingPacket>();
            t.Poll(list);
            return list;
        }

        [Fact]
        public void ZeroLatency_DeliversOnNextPoll_WithMetadata()
        {
            var hub = new LoopbackHub();
            var a = hub.CreateTransport(A);
            var b = hub.CreateTransport(B);

            a.Send(B, Channel.Events, Reliability.Reliable, Seg(1, 2, 3));
            var got = Drain(b);

            var p = Assert.Single(got);
            Assert.Equal(A, p.From);
            Assert.Equal(Channel.Events, p.Channel);
            Assert.Equal(new byte[] { 1, 2, 3 }, p.Data);
            Assert.Empty(Drain(b));
            Assert.Empty(Drain(a));
        }

        [Fact]
        public void Send_CopiesData_SoCallerCanReuseBuffer()
        {
            var hub = new LoopbackHub();
            var a = hub.CreateTransport(A);
            var b = hub.CreateTransport(B);
            var buffer = new byte[] { 0, 9, 9, 7, 0 };

            a.Send(B, Channel.Control, Reliability.Reliable, new ArraySegment<byte>(buffer, 1, 3));
            buffer[1] = 0;

            Assert.Equal(new byte[] { 9, 9, 7 }, Drain(b)[0].Data);
        }

        [Fact]
        public void Reliable_IsInOrder_UnderLatencyAndJitter()
        {
            var hub = new LoopbackHub(seed: 99) { LatencyMs = 40, JitterMs = 80 };
            var a = hub.CreateTransport(A);
            var b = hub.CreateTransport(B);

            var received = new List<byte>();
            for (int i = 0; i < 200; i++)
            {
                a.Send(B, Channel.Save, Reliability.Reliable, Seg((byte)i));
                hub.Advance(0.002);
                received.AddRange(Drain(b).Select(p => p.Data[0]));
            }
            hub.Advance(1);
            received.AddRange(Drain(b).Select(p => p.Data[0]));

            Assert.Equal(Enumerable.Range(0, 200).Select(i => (byte)i), received);
        }

        [Fact]
        public void Reliable_OrderIsPerChannel_AndPerSender()
        {
            var hub = new LoopbackHub(seed: 5) { JitterMs = 50 };
            var a = hub.CreateTransport(A);
            var c = hub.CreateTransport(C);
            var b = hub.CreateTransport(B);

            for (int i = 0; i < 50; i++)
            {
                a.Send(B, Channel.Control, Reliability.Reliable, Seg(0, (byte)i));
                a.Send(B, Channel.Events, Reliability.Reliable, Seg(1, (byte)i));
                c.Send(B, Channel.Control, Reliability.Reliable, Seg(2, (byte)i));
            }
            hub.Advance(1);
            var got = Drain(b);

            Assert.Equal(150, got.Count);
            foreach (byte stream in new byte[] { 0, 1, 2 })
            {
                var seq = got.Where(p => p.Data[0] == stream).Select(p => (int)p.Data[1]).ToList();
                Assert.Equal(Enumerable.Range(0, 50), seq);
            }
            Assert.All(got.Where(p => p.Data[0] == 2), p => Assert.Equal(C, p.From));
        }

        [Fact]
        public void Latency_HoldsPacketsUntilDue()
        {
            var hub = new LoopbackHub { LatencyMs = 100 };
            var a = hub.CreateTransport(A);
            var b = hub.CreateTransport(B);

            a.Send(B, Channel.State, Reliability.Unreliable, Seg(1));
            Assert.Empty(Drain(b));
            Assert.Equal(1, b.PendingCount);

            hub.Advance(0.099);
            Assert.Empty(Drain(b));

            hub.Advance(0.001);
            Assert.Single(Drain(b));
            Assert.Equal(0, b.PendingCount);
        }

        [Fact]
        public void Latency_FloatingPointAccumulation_StillDeliversOnTime()
        {
            var hub = new LoopbackHub { LatencyMs = 30 };
            var a = hub.CreateTransport(A);
            var b = hub.CreateTransport(B);

            a.Send(B, Channel.State, Reliability.Reliable, Seg(1));
            hub.Advance(0.01);
            hub.Advance(0.01);
            hub.Advance(0.01);

            Assert.Single(Drain(b));
        }

        [Fact]
        public void LatencyChange_AffectsOnlyNewPackets()
        {
            var hub = new LoopbackHub { LatencyMs = 100 };
            var a = hub.CreateTransport(A);
            var b = hub.CreateTransport(B);

            a.Send(B, Channel.State, Reliability.Unreliable, Seg(1));
            hub.LatencyMs = 0;
            a.Send(B, Channel.State, Reliability.Unreliable, Seg(2));

            Assert.Equal(new byte[] { 2 }, Assert.Single(Drain(b)).Data);
            hub.Advance(0.1);
            Assert.Equal(new byte[] { 1 }, Assert.Single(Drain(b)).Data);
        }

        [Fact]
        public void Loss_AppliesToUnreliableOnly()
        {
            var hub = new LoopbackHub(seed: 42) { UnreliableLossPercent = 30 };
            var a = hub.CreateTransport(A);
            var b = hub.CreateTransport(B);

            const int n = 2000;
            for (int i = 0; i < n; i++)
            {
                a.Send(B, Channel.State, Reliability.Unreliable, Seg(0));
                a.Send(B, Channel.Control, Reliability.Reliable, Seg(1));
            }
            var got = Drain(b);

            int reliable = got.Count(p => p.Data[0] == 1);
            int unreliable = got.Count(p => p.Data[0] == 0);
            Assert.Equal(n, reliable);
            Assert.InRange(unreliable, (int)(n * 0.62), (int)(n * 0.78));
            Assert.Equal(n - unreliable, hub.PacketsDropped);
            Assert.Equal(2 * n, hub.PacketsSent);
        }

        [Fact]
        public void Loss_IsDeterministicForSeed()
        {
            int Run()
            {
                var hub = new LoopbackHub(seed: 7) { UnreliableLossPercent = 50 };
                var a = hub.CreateTransport(A);
                var b = hub.CreateTransport(B);
                for (int i = 0; i < 500; i++)
                    a.Send(B, Channel.State, Reliability.Unreliable, Seg((byte)i));
                return Drain(b).Aggregate(17, (h, p) => h * 31 + p.Data[0]);
            }

            Assert.Equal(Run(), Run());
        }

        [Fact]
        public void FullLoss_DropsAllUnreliable_NoDuplicates()
        {
            var hub = new LoopbackHub { UnreliableLossPercent = 100 };
            var a = hub.CreateTransport(A);
            var b = hub.CreateTransport(B);
            for (int i = 0; i < 100; i++)
                a.Send(B, Channel.State, Reliability.Unreliable, Seg(1));
            a.Send(B, Channel.State, Reliability.Reliable, Seg(2));

            Assert.Equal(new byte[] { 2 }, Assert.Single(Drain(b)).Data);
        }

        [Fact]
        public void Send_ToUnknownPeer_IsDroppedSilently()
        {
            var hub = new LoopbackHub();
            var a = hub.CreateTransport(A);
            a.Send(new PeerId(999), Channel.Control, Reliability.Reliable, Seg(1));
            Assert.Equal(1, hub.PacketsDropped);
        }

        [Fact]
        public void SendToSelf_IsDelivered()
        {
            var hub = new LoopbackHub();
            var a = hub.CreateTransport(A);
            a.Send(A, Channel.Control, Reliability.Reliable, Seg(5));
            Assert.Equal(A, Assert.Single(Drain(a)).From);
        }

        [Fact]
        public void LinkDown_DropsBothDirections_UntilRestored()
        {
            var hub = new LoopbackHub();
            var a = hub.CreateTransport(A);
            var b = hub.CreateTransport(B);
            var c = hub.CreateTransport(C);

            hub.SetLinkDown(A, B, true);
            a.Send(B, Channel.Control, Reliability.Reliable, Seg(1));
            b.Send(A, Channel.Control, Reliability.Reliable, Seg(1));
            a.Send(C, Channel.Control, Reliability.Reliable, Seg(1));
            Assert.Empty(Drain(b));
            Assert.Empty(Drain(a));
            Assert.Single(Drain(c));

            hub.SetLinkDown(A, B, false);
            a.Send(B, Channel.Control, Reliability.Reliable, Seg(2));
            Assert.Single(Drain(b));
        }

        [Fact]
        public void Disconnect_DiscardsQueuedPacketsFromThatPeerOnly()
        {
            var hub = new LoopbackHub { LatencyMs = 50 };
            var a = hub.CreateTransport(A);
            var b = hub.CreateTransport(B);
            var c = hub.CreateTransport(C);

            a.Send(B, Channel.Control, Reliability.Reliable, Seg(1));
            c.Send(B, Channel.Control, Reliability.Reliable, Seg(3));
            b.Disconnect(A);
            hub.Advance(0.05);

            Assert.Equal(C, Assert.Single(Drain(b)).From);
        }

        [Fact]
        public void Dispose_DetachesFromHub_AndFurtherUseThrows()
        {
            var hub = new LoopbackHub();
            var a = hub.CreateTransport(A);
            var b = hub.CreateTransport(B);

            b.Dispose();
            b.Dispose();
            Assert.False(hub.IsAttached(B));
            a.Send(B, Channel.Control, Reliability.Reliable, Seg(1));
            Assert.Equal(1, hub.PacketsDropped);
            Assert.Throws<ObjectDisposedException>(() => b.Send(A, Channel.Control, Reliability.Reliable, Seg(1)));
            Assert.Throws<ObjectDisposedException>(() => Drain(b));

            var b2 = hub.CreateTransport(B);
            a.Send(B, Channel.Control, Reliability.Reliable, Seg(1));
            Assert.Single(Drain(b2));
        }

        [Fact]
        public void InvalidArguments_Throw()
        {
            var hub = new LoopbackHub();
            var a = hub.CreateTransport(A);

            Assert.Throws<ArgumentException>(() => hub.CreateTransport(PeerId.None));
            Assert.Throws<InvalidOperationException>(() => hub.CreateTransport(A));
            Assert.Throws<ArgumentException>(() => a.Send(B, Channel.Control, Reliability.Reliable, default));
            Assert.Throws<ArgumentException>(() => a.Send(PeerId.None, Channel.Control, Reliability.Reliable, Seg(1)));
            Assert.Throws<ArgumentOutOfRangeException>(() => a.Send(B, (Channel)9, Reliability.Reliable, Seg(1)));
            Assert.Throws<ArgumentNullException>(() => a.Poll(null));
            Assert.Throws<ArgumentOutOfRangeException>(() => hub.Advance(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => hub.Advance(double.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => hub.UnreliableLossPercent = 101);
            Assert.Throws<ArgumentOutOfRangeException>(() => hub.LatencyMs = -5);
        }

        [Fact]
        public void EmptyPayload_IsDelivered()
        {
            var hub = new LoopbackHub();
            var a = hub.CreateTransport(A);
            var b = hub.CreateTransport(B);
            a.Send(B, Channel.Control, Reliability.Reliable, new ArraySegment<byte>(Array.Empty<byte>()));
            Assert.Empty(Assert.Single(Drain(b)).Data);
        }

        [Fact]
        public void Advance_UpdatesNow()
        {
            var hub = new LoopbackHub();
            hub.Advance(1.5);
            hub.Advance(0.25);
            Assert.Equal(1.75, hub.Now, 9);
        }

        [Fact]
        public void PeerId_EqualityAndToString()
        {
            var x = new PeerId(76561198012345678UL);
            var y = new PeerId(76561198012345678UL);
            Assert.True(x == y);
            Assert.False(x != y);
            Assert.True(x.Equals((object)y));
            Assert.Equal(x.GetHashCode(), y.GetHashCode());
            Assert.Equal("76561198012345678", x.ToString());
            Assert.False(PeerId.None.IsValid);
            Assert.NotEqual(A, B);
        }
    }
}
