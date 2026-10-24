using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Sync;
using Xunit;

namespace NGGMultiplayer.Tests.Sync
{
    public class RpcRouterTests
    {
        private static readonly PeerId Host = SyncNet.HostId;

        private static (SyncNet Net, SyncNode C1, SyncNode C2) ThreePeers(string rpc = "ping", RpcValidator validator = null)
        {
            var net = new SyncNet(withRegistry: false);
            var c1 = net.Connect(1);
            var c2 = net.Connect(2);
            net.Host.RegisterRecorder(rpc, validator);
            c1.RegisterRecorder(rpc);
            c2.RegisterRecorder(rpc);
            net.Flush();
            return (net, c1, c2);
        }

        private static Action<NetWriter> Int(int v) => w => w.WriteInt(v);

        private static uint ReferenceFnv(string s)
        {
            uint h = 2166136261;
            foreach (byte b in Encoding.UTF8.GetBytes(s))
                h = (h ^ b) * 16777619;
            return h;
        }

        // ---- Ids and registration --------------------------------------------------------------------

        [Theory]
        [InlineData("", 0x811C9DC5u)]
        [InlineData("a", 0xE40C292Cu)]
        [InlineData("foobar", 0xBF9CF968u)]
        public void GetId_MatchesFnv1aVectors(string name, uint expected)
        {
            Assert.Equal(expected, RpcRouter.GetId(name));
        }

        [Theory]
        [InlineData("SwingShot")]
        [InlineData("héllo wörld")]
        [InlineData("日本語")]
        [InlineData("golf ⛳ 🏌️")]
        [InlineData("lone \ud800 surrogate")]
        [InlineData("trailing \udc00")]
        [InlineData("߿ࠀ￿")]
        public void GetId_HashesUtf8Bytes(string name)
        {
            Assert.Equal(ReferenceFnv(name), RpcRouter.GetId(name));
        }

        [Fact]
        public void Register_RejectsDuplicatesAndCollisions()
        {
            var net = new SyncNet(withRegistry: false);
            var rpc = net.Host.Rpc;
            rpc.Register("a", (s, r) => { });
            Assert.Throws<InvalidOperationException>(() => rpc.Register("a", (s, r) => { }));
            Assert.Throws<ArgumentException>(() => rpc.Register("", (s, r) => { }));
            Assert.Throws<ArgumentNullException>(() => rpc.Register("b", null));

            // Find a genuine 32-bit collision by brute force (birthday bound: ~100k names).
            var seen = new Dictionary<uint, string>();
            string first = null, second = null;
            for (int i = 0; first == null; i++)
            {
                string name = "rpc" + i;
                uint id = RpcRouter.GetId(name);
                if (seen.TryGetValue(id, out var other))
                {
                    first = other;
                    second = name;
                }
                else
                {
                    seen.Add(id, name);
                }
            }
            rpc.Register(first, (s, r) => { });
            var ex = Assert.Throws<InvalidOperationException>(() => rpc.Register(second, (s, r) => { }));
            Assert.Contains("collides", ex.Message);
            Assert.False(rpc.Unregister(second));
            Assert.True(rpc.IsRegistered(first));
            Assert.False(rpc.IsRegistered(second));
            Assert.True(rpc.Unregister(first));
            Assert.False(rpc.IsRegistered(first));
        }

        // ---- Host-originated -------------------------------------------------------------------------

        [Fact]
        public void HostToAll_RunsEverywhereWithHostAsSender()
        {
            var (net, c1, c2) = ThreePeers();
            Assert.True(net.Host.Rpc.Send("ping", RpcTarget.All, Int(5)));
            // The host's own copy runs synchronously.
            Assert.Equal((Host, "ping", 5), net.Host.Calls.Single());
            net.Flush();
            Assert.Equal((Host, "ping", 5), c1.Calls.Single());
            Assert.Equal((Host, "ping", 5), c2.Calls.Single());
        }

        [Fact]
        public void HostToOthers_SkipsHost()
        {
            var (net, c1, c2) = ThreePeers();
            net.Host.Rpc.Send("ping", RpcTarget.Others, Int(1));
            net.Flush();
            Assert.Empty(net.Host.Calls);
            Assert.Single(c1.Calls);
            Assert.Single(c2.Calls);
        }

        [Fact]
        public void HostToPeer_And_HostToHost()
        {
            var (net, c1, c2) = ThreePeers();
            Assert.True(net.Host.Rpc.Send("ping", RpcTarget.Peer, Int(2), peer: c2.Id));
            Assert.True(net.Host.Rpc.Send("ping", RpcTarget.Host, Int(3)));
            Assert.True(net.Host.Rpc.Send("ping", RpcTarget.Peer, Int(4), peer: Host));
            Assert.False(net.Host.Rpc.Send("ping", RpcTarget.Peer, Int(5), peer: new PeerId(999)));
            net.Flush();
            Assert.Empty(c1.Calls);
            Assert.Equal((Host, "ping", 2), c2.Calls.Single());
            Assert.Equal(new[] { 3, 4 }, net.Host.Calls.Select(c => c.Value));
        }

        [Fact]
        public void HostValidator_DoesNotApplyToHostOwnCalls()
        {
            var (net, c1, _) = ThreePeers(validator: (s, t, p, r) => false);
            net.Host.Rpc.Send("ping", RpcTarget.All, Int(1));
            net.Flush();
            Assert.Single(c1.Calls);
            Assert.Equal(0, net.Host.Rpc.Rejected);
        }

        // ---- Client-originated -----------------------------------------------------------------------

        [Fact]
        public void ClientToHost_OnlyHostRuns()
        {
            var (net, c1, c2) = ThreePeers();
            c1.Rpc.Send("ping", RpcTarget.Host, Int(7));
            net.Flush();
            Assert.Equal((c1.Id, "ping", 7), net.Host.Calls.Single());
            Assert.Empty(c1.Calls);
            Assert.Empty(c2.Calls);
        }

        [Fact]
        public void ClientToAll_IsRelayedToEveryoneIncludingSenderWithSenderPreserved()
        {
            var (net, c1, c2) = ThreePeers();
            c1.Rpc.Send("ping", RpcTarget.All, Int(8));
            // The sender's own copy waits for the host.
            Assert.Empty(c1.Calls);
            net.Flush();
            Assert.Equal((c1.Id, "ping", 8), net.Host.Calls.Single());
            Assert.Equal((c1.Id, "ping", 8), c1.Calls.Single());
            Assert.Equal((c1.Id, "ping", 8), c2.Calls.Single());
            Assert.Equal(1, net.Host.Rpc.Relayed);
        }

        [Fact]
        public void ClientToOthers_SkipsSender()
        {
            var (net, c1, c2) = ThreePeers();
            c1.Rpc.Send("ping", RpcTarget.Others, Int(9));
            net.Flush();
            Assert.Equal((c1.Id, "ping", 9), net.Host.Calls.Single());
            Assert.Empty(c1.Calls);
            Assert.Equal((c1.Id, "ping", 9), c2.Calls.Single());
        }

        [Fact]
        public void ClientToPeer_GoesThroughHost()
        {
            var (net, c1, c2) = ThreePeers();
            c1.Rpc.Send("ping", RpcTarget.Peer, Int(10), peer: c2.Id);
            c1.Rpc.Send("ping", RpcTarget.Peer, Int(11), peer: Host);
            Assert.True(c1.Rpc.Send("ping", RpcTarget.Peer, Int(12), peer: c1.Id));
            Assert.Equal((c1.Id, "ping", 12), c1.Calls.Single());
            net.Flush();
            Assert.Equal((c1.Id, "ping", 10), c2.Calls.Single());
            Assert.Equal((c1.Id, "ping", 11), net.Host.Calls.Single());
            Assert.Single(c1.Calls);
            // Every client packet went to the host; none went straight to c2.
            Assert.DoesNotContain(c2.Session.Received, r => r.From == c1.Id);
        }

        [Fact]
        public void ClientToDisconnectedPeer_IsDroppedByHost()
        {
            var (net, c1, _) = ThreePeers();
            c1.Rpc.Send("ping", RpcTarget.Peer, Int(1), peer: new PeerId(55));
            net.Flush();
            Assert.Empty(net.Host.Calls);
        }

        [Fact]
        public void Payload_SurvivesRelay()
        {
            var net = new SyncNet(withRegistry: false);
            var c1 = net.Connect(1);
            var c2 = net.Connect(2);
            string got = null;
            byte[] blob = null;
            foreach (var n in new[] { net.Host, c1, c2 })
                n.Rpc.Register("chat", (s, r) => { got = r.ReadString(); blob = r.ReadBytes(); });
            c1.Rpc.Send("chat", RpcTarget.Others, ("hello ⛳", new byte[] { 1, 2, 3 }), (w, a) => { w.WriteString(a.Item1); w.WriteBytes(a.Item2); });
            net.Flush();
            Assert.Equal("hello ⛳", got);
            Assert.Equal(new byte[] { 1, 2, 3 }, blob);
        }

        // ---- Sender identity -------------------------------------------------------------------------

        [Fact]
        public void ClientsCannotSpoofSender()
        {
            var (net, c1, c2) = ThreePeers();
            uint id = RpcRouter.GetId("ping");

            // A forged relay claiming to come from c2, sent to the host...
            var relay = PacketBuilder.Begin(MessageType.RpcRelay);
            relay.WriteUInt(id);
            relay.WritePeerId(c2.Id);
            relay.WriteInt(66);
            SyncNet.SendRaw(c1, Host, relay.ToArray());
            // ...and straight to another client, claiming to be the host.
            var relay2 = PacketBuilder.Begin(MessageType.RpcRelay);
            relay2.WriteUInt(id);
            relay2.WritePeerId(Host);
            relay2.WriteInt(67);
            SyncNet.SendRaw(c1, c2.Id, relay2.ToArray());
            // A plain Rpc sent directly to a client.
            var direct = PacketBuilder.Begin(MessageType.Rpc);
            direct.WriteUInt(id);
            direct.WriteByte((byte)RpcTarget.All);
            direct.WriteInt(68);
            SyncNet.SendRaw(c1, c2.Id, direct.ToArray());
            net.Flush();

            Assert.Empty(net.Host.Calls);
            Assert.Empty(c2.Calls);

            // Whatever a client puts in the payload, the host stamps the real sender.
            c1.Rpc.Send("ping", RpcTarget.Others, Int(69));
            net.Flush();
            Assert.Equal(c1.Id, c2.Calls.Single().Sender);
        }

        // ---- Validation ------------------------------------------------------------------------------

        [Fact]
        public void HostValidator_CanVetoAndSeesContext()
        {
            var seen = new List<(PeerId Sender, RpcTarget Target, PeerId Peer, int Value)>();
            RpcValidator validator = (sender, target, peer, args) =>
            {
                int v = args.ReadInt();
                seen.Add((sender, target, peer, v));
                return v != 13;
            };
            var (net, c1, c2) = ThreePeers(validator: validator);
            var rejections = new List<(PeerId, uint, string)>();
            net.Host.Rpc.RpcRejected += (p, id, reason) => rejections.Add((p, id, reason));

            c1.Rpc.Send("ping", RpcTarget.All, Int(13));
            c1.Rpc.Send("ping", RpcTarget.Peer, Int(7), peer: c2.Id);
            net.Flush();

            Assert.Empty(c1.Calls);
            Assert.Equal((c1.Id, "ping", 7), c2.Calls.Single());
            Assert.Empty(net.Host.Calls);
            Assert.Equal(new[] { (c1.Id, RpcTarget.All, PeerId.None, 13), (c1.Id, RpcTarget.Peer, c2.Id, 7) }, seen);
            Assert.Equal(1, net.Host.Rpc.Rejected);
            Assert.Equal(c1.Id, rejections.Single().Item1);
            Assert.Equal(RpcRouter.GetId("ping"), rejections.Single().Item2);
        }

        [Fact]
        public void ThrowingValidator_Vetoes()
        {
            var (net, c1, c2) = ThreePeers(validator: (s, t, p, r) => throw new InvalidOperationException("boom"));
            c1.Rpc.Send("ping", RpcTarget.All, Int(1));
            net.Flush();
            Assert.Empty(c2.Calls);
            Assert.Empty(net.Host.Calls);
            Assert.Equal(1, net.Host.Rpc.Rejected);
        }

        [Fact]
        public void ValidatorReadingPastPayload_Vetoes()
        {
            var (net, c1, c2) = ThreePeers(validator: (s, t, p, r) => { r.ReadLong(); return true; });
            c1.Rpc.Send("ping", RpcTarget.All, Int(1));
            net.Flush();
            Assert.Empty(c2.Calls);
        }

        [Fact]
        public void UnregisteredOnHost_IsDroppedUnlessAllowed()
        {
            var net = new SyncNet(withRegistry: false);
            var c1 = net.Connect(1);
            var c2 = net.Connect(2);
            c2.RegisterRecorder("clientOnly");
            c1.Rpc.Send("clientOnly", RpcTarget.Others, Int(1));
            net.Flush();
            Assert.Empty(c2.Calls);
            Assert.Equal(1, net.Host.Rpc.Rejected);

            net.Host.Rpc.AllowUnregisteredRelay = true;
            c1.Rpc.Send("clientOnly", RpcTarget.Others, Int(2));
            c1.Rpc.Send("clientOnly", RpcTarget.Host, Int(3));
            c1.Rpc.Send("clientOnly", RpcTarget.All, Int(4));
            net.Flush();
            Assert.Equal(2, c2.Calls.Single().Value);
            Assert.Equal(3, net.Host.Rpc.Rejected);
        }

        // ---- Robustness ------------------------------------------------------------------------------

        [Fact]
        public void HandlerFailures_AreContained()
        {
            var net = new SyncNet(withRegistry: false);
            var c1 = net.Connect(1);
            c1.Rpc.Register("throws", (s, r) => throw new InvalidOperationException("boom"));
            c1.Rpc.Register("overreads", (s, r) => r.ReadLong());
            c1.RegisterRecorder("ok");
            net.Host.Rpc.Send("throws", RpcTarget.All);
            net.Host.Rpc.Send("overreads", RpcTarget.All, Int(1));
            net.Host.Rpc.Send("ok", RpcTarget.All, Int(2));
            net.Flush();
            Assert.Equal(2, c1.Calls.Single().Value);
        }

        [Fact]
        public void UnreliablePayloadLimit_IsEnforcedAndRelayFits()
        {
            var net = new SyncNet(withRegistry: false);
            var c1 = net.Connect(1);
            var c2 = net.Connect(2);
            int got = -1;
            foreach (var n in new[] { net.Host, c1, c2 })
                n.Rpc.Register("blob", (s, r) => got = r.Remaining);

            Assert.False(c1.Rpc.Send("blob", RpcTarget.Others, w => w.WriteRaw(new byte[SyncProtocol.MaxUnreliableRpcPayload + 1], 0, SyncProtocol.MaxUnreliableRpcPayload + 1), Reliability.Unreliable));
            Assert.True(c1.Rpc.Send("blob", RpcTarget.Peer, w => w.WriteRaw(new byte[SyncProtocol.MaxUnreliableRpcPayload], 0, SyncProtocol.MaxUnreliableRpcPayload), Reliability.Unreliable, c2.Id));
            Assert.True(c1.Rpc.Send("blob", RpcTarget.Others, w => w.WriteRaw(new byte[5000], 0, 5000)));

            var packets = new List<IncomingPacket>();
            net.Host.Session.Pump();
            c2.Session.Transport.Poll(packets);
            Assert.Equal(2, packets.Count);
            // The largest unreliable payload, once relayed with the stamped sender, still fits one packet.
            int relayedSize = SyncProtocol.MaxUnreliableRpcPayload + 1 + 4 + 8;
            Assert.True(relayedSize <= ProtocolInfo.MaxUnreliablePacketSize);
            Assert.Contains(packets, p => p.Data.Length == relayedSize);
            Assert.All(packets, p => Assert.Equal(Channel.Events, p.Channel));
        }

        [Fact]
        public void OversizedUnreliableFromClient_IsRejected()
        {
            var (net, c1, c2) = ThreePeers();
            var w = PacketBuilder.Begin(MessageType.Rpc);
            w.WriteUInt(RpcRouter.GetId("ping"));
            w.WriteByte((byte)RpcTarget.Others | 0x80);
            w.WriteRaw(new byte[SyncProtocol.MaxUnreliableRpcPayload + 1], 0, SyncProtocol.MaxUnreliableRpcPayload + 1);
            SyncNet.SendRaw(c1, Host, w.ToArray());
            net.Flush();
            Assert.Empty(c2.Calls);
            Assert.Equal(1, net.Host.Rpc.Rejected);
        }

        [Fact]
        public void MalformedRpcPackets_AreDropped()
        {
            var (net, c1, c2) = ThreePeers();
            uint id = RpcRouter.GetId("ping");
            var badTarget = PacketBuilder.Begin(MessageType.Rpc);
            badTarget.WriteUInt(id);
            badTarget.WriteByte(0x14);
            var zeroPeer = PacketBuilder.Begin(MessageType.Rpc);
            zeroPeer.WriteUInt(id);
            zeroPeer.WriteByte((byte)RpcTarget.Peer);
            zeroPeer.WritePeerId(PeerId.None);
            var truncated = new byte[] { MessageType.Rpc, 1, 2 };
            SyncNet.SendRaw(c1, Host, badTarget.ToArray());
            SyncNet.SendRaw(c1, Host, zeroPeer.ToArray());
            SyncNet.SendRaw(c1, Host, truncated);
            var zeroSender = PacketBuilder.Begin(MessageType.RpcRelay);
            zeroSender.WriteUInt(id);
            zeroSender.WritePeerId(PeerId.None);
            SyncNet.SendRaw(net.Host, c2.Id, zeroSender.ToArray());
            net.Flush();
            Assert.Equal(3, net.Host.Rpc.Rejected);
            Assert.Empty(net.Host.Calls);
            Assert.Empty(c2.Calls);

            var rng = new Random(23);
            for (int i = 0; i < 4000; i++)
            {
                var bytes = new byte[1 + rng.Next(0, 30)];
                rng.NextBytes(bytes);
                bytes[0] = i % 2 == 0 ? MessageType.Rpc : MessageType.RpcRelay;
                SyncNet.SendRaw(c1, Host, bytes);
                SyncNet.SendRaw(net.Host, c2.Id, bytes);
                if (i % 100 == 0)
                    net.Frame();
            }
            net.Flush();
        }

        [Fact]
        public void NestedLocalCalls_AreBounded()
        {
            var net = new SyncNet(withRegistry: false);
            int depth = 0;
            net.Host.Rpc.Register("recurse", (s, r) =>
            {
                depth++;
                net.Host.Rpc.Send("recurse", RpcTarget.Host);
            });
            net.Host.Rpc.Send("recurse", RpcTarget.Host);
            Assert.Equal(8, depth);
        }

        [Fact]
        public void Send_ValidatesArguments()
        {
            var net = new SyncNet(withRegistry: false);
            var rpc = net.Host.Rpc;
            Assert.Throws<ArgumentException>(() => rpc.Send("x", RpcTarget.Peer));
            Assert.Throws<ArgumentException>(() => rpc.Send("", RpcTarget.All));
            Assert.Throws<ArgumentOutOfRangeException>(() => rpc.Send("x", (RpcTarget)7));
            Assert.Throws<ArgumentOutOfRangeException>(() => rpc.Send("x", RpcTarget.All, null, (Reliability)5));
            Assert.Throws<ArgumentNullException>(() => rpc.Send<int>("x", RpcTarget.All, 1, null));
        }

        [Fact]
        public void SessionClosed_StopsSending_DisposeUnregisters()
        {
            var (net, c1, _) = ThreePeers();
            c1.Session.RaiseClosed();
            Assert.False(c1.Rpc.Send("ping", RpcTarget.All, Int(1)));

            var bare = new SyncNet(withRegistry: false);
            Assert.Equal(2, bare.Host.Session.HandlerCount);
            bare.Host.Rpc.Dispose();
            bare.Host.Rpc.Dispose();
            Assert.Equal(0, bare.Host.Session.HandlerCount);
            Assert.Throws<ObjectDisposedException>(() => bare.Host.Rpc.Send("x", RpcTarget.All));
        }

        [Fact]
        public void RpcAfterSpawn_NeverOvertakesIt()
        {
            var net = new SyncNet(withRpc: true);
            var c1 = net.Connect(1);
            net.Flush();
            net.Hub.LatencyMs = 40;
            net.Hub.JitterMs = 30;
            var found = new List<bool>();
            c1.Rpc.Register("hit", (s, r) => found.Add(c1.Registry.Get(new NetEntityId(r.ReadVarUInt())) != null));
            net.Host.Rpc.Register("hit", (s, r) => { });
            for (int i = 0; i < 20; i++)
            {
                var e = net.Host.Registry.Spawn("ball", Host, States.At(i));
                net.Host.Rpc.Send("hit", RpcTarget.Others, e.Id.Value, (w, v) => w.WriteVarUInt(v));
            }
            net.Run(1.0);
            Assert.Equal(20, found.Count);
            Assert.All(found, Assert.True);
        }
    }
}
