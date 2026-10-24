using NGGMultiplayer.Core;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Session;
using NGGMultiplayer.Core.Sync;
using Xunit;

namespace NGGMultiplayer.Tests.Integration
{
    public class NetSessionReconnectTests
    {
        [Fact]
        public void ClientReconnectingBeforeTheHostTimesItOut_GetsEveryEntityAgain()
        {
            var net = new NetTestNet();
            var host = net.Add(100, options: new NetSessionOptions { Session = new SessionConfig { TimeoutSeconds = 30 } });
            var client = net.Add(200, options: new NetSessionOptions { Session = new SessionConfig { TimeoutSeconds = 3 } });
            host.Save.Set("challenge", "hole-1", new byte[] { 1 });

            host.Net.StartHost();
            net.Frame();
            var ball = host.Net.Entities.Spawn("ball", host.Id, new TransformState(new NetVector3(1, 2, 3), NetQuaternion.Identity));
            client.Net.Connect(host.Id);
            Assert.True(net.RunUntil(() => client.Factory.Has(ball.Id), 5), "first join did not replicate");

            net.Hub.SetLinkDown(host.Id, client.Id, true);
            Assert.True(net.RunUntil(() => client.Ended.Count == 1, 6));
            Assert.Equal(SessionEndReason.HostTimedOut, client.Ended[0].Reason);
            Assert.True(host.Net.Session.IsPeerConnected(client.Id));

            net.Hub.SetLinkDown(host.Id, client.Id, false);
            client.Net.Connect(host.Id);
            Assert.True(net.RunUntil(() => client.Net.IsReady && client.Net.Entities.Get(ball.Id) != null, 5),
                "reconnected client never received the existing entities");
            Assert.True(host.Net.IsPeerReady(client.Id));
            Assert.Equal(2, client.ReadyCount);
            Assert.Equal(new byte[] { 1 }, client.Save.Get("challenge", "hole-1"));
        }
    }
}
