using System.Linq;
using NGGMultiplayer.Runtime.Lobbies;
using Xunit;

namespace NGGMultiplayer.Tests.UI
{
    public class LobbyListingsTests
    {
        private static LobbyListing L(ulong id, LobbySource source, int players = 1, string protocol = "2", bool known = true, string host = "H", string friend = "") =>
            new LobbyListing { LobbyId = id, Source = source, Players = players, MaxPlayers = 4, Protocol = protocol, DataKnown = known, HostName = host, FriendName = friend };

        [Fact]
        public void Merge_DedupesKeepingBestSourceAndKnownData()
        {
            var merged = LobbyListings.Merge(new[]
            {
                L(5, LobbySource.Public, players: 2, host: "Ann"),
                new LobbyListing { LobbyId = 5, Source = LobbySource.Invite, FriendName = "Bob" },
                L(6, LobbySource.Public, players: 3),
                L(7, LobbySource.Friend, friend: "Cid"),
            }, ownLobby: 0, localProtocol: "2");
            Assert.Equal(new ulong[] { 5, 7, 6 }, merged.Select(m => m.LobbyId).ToArray());
            Assert.Equal(LobbySource.Invite, merged[0].Source);
            Assert.True(merged[0].DataKnown);
            Assert.Equal("Ann", merged[0].HostName);
            Assert.Equal("Bob", merged[0].FriendName);
            Assert.Equal(2, merged[0].Players);
        }

        [Fact]
        public void Merge_LeavesOutOwnLobbyAndZero()
        {
            var merged = LobbyListings.Merge(new[] { L(5, LobbySource.Friend), L(0, LobbySource.Public), L(9, LobbySource.Public) }, 5, "2");
            Assert.Equal(new ulong[] { 9 }, merged.Select(m => m.LobbyId).ToArray());
        }

        [Fact]
        public void Merge_JoinableFirstThenMorePlayers()
        {
            var merged = LobbyListings.Merge(new[]
            {
                L(1, LobbySource.Public, players: 3, protocol: "1"),
                L(2, LobbySource.Public, players: 1),
                L(3, LobbySource.Public, players: 2),
            }, 0, "2");
            Assert.Equal(new ulong[] { 3, 2, 1 }, merged.Select(m => m.LobbyId).ToArray());
        }

        [Fact]
        public void CanJoin_Reasons()
        {
            Assert.True(LobbyListings.CanJoin(L(1, LobbySource.Public), "2", out _));
            Assert.False(LobbyListings.CanJoin(L(1, LobbySource.Public, protocol: ""), "2", out string r1));
            Assert.Equal("Not a multiplayer game", r1);
            var old = L(1, LobbySource.Public, protocol: "1");
            old.ModVersion = "0.0.9";
            Assert.False(LobbyListings.CanJoin(old, "2", out string r2));
            Assert.Equal("Needs mod version 0.0.9", r2);
            Assert.False(LobbyListings.CanJoin(L(1, LobbySource.Public, players: 4), "2", out string r3));
            Assert.Equal("Full", r3);
            Assert.True(LobbyListings.CanJoin(L(1, LobbySource.Invite, known: false, protocol: ""), "2", out _));
        }

        [Fact]
        public void Texts()
        {
            Assert.Equal("Michal's Game", LobbyListings.Title(L(1, LobbySource.Public, host: "Michal")));
            Assert.Equal("Chris' Game", LobbyListings.Title(L(1, LobbySource.Public, host: "Chris")));
            Assert.Equal("Bob's Game", LobbyListings.Title(new LobbyListing { LobbyId = 1, Source = LobbySource.Invite, FriendName = "Bob" }));
            Assert.Equal("Game at 127.0.0.1:27250", LobbyListings.Title(new LobbyListing { Source = LobbySource.Direct, Endpoint = "127.0.0.1:27250" }));
            Assert.Equal("Invited by Bob", LobbyListings.Detail(new LobbyListing { LobbyId = 1, Source = LobbySource.Invite, FriendName = "Bob" }, "2"));
            Assert.Equal("1/4 players · With Ann", LobbyListings.Detail(L(1, LobbySource.Friend, host: "Michal", friend: "Ann"), "2"));
        }
    }
}
