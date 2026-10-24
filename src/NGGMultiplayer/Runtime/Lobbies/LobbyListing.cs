using System;
using System.Collections.Generic;

namespace NGGMultiplayer.Runtime.Lobbies
{
    /// <summary>Where a joinable game was found; lower values are listed first.</summary>
    public enum LobbySource
    {
        /// <summary>Someone sent the player a Steam invite to it.</summary>
        Invite,
        /// <summary>A Steam friend is in it.</summary>
        Friend,
        /// <summary>Steam's public lobby list.</summary>
        Public,
        /// <summary>[Network] Transport = Direct: the configured host address (no lobby).</summary>
        Direct
    }

    /// <summary>One game the multiplayer browser can show and join. Plain data; Unity- and Steam-free.</summary>
    public sealed class LobbyListing
    {
        /// <summary>Steam lobby id; 0 for <see cref="LobbySource.Direct"/>.</summary>
        public ulong LobbyId { get; set; }

        public LobbySource Source { get; set; }

        /// <summary>The hosting player's name ("" while the lobby's data has not arrived).</summary>
        public string HostName { get; set; } = string.Empty;

        /// <summary>The friend who is in it or who sent the invite ("" for public games).</summary>
        public string FriendName { get; set; } = string.Empty;

        /// <summary>Members now; -1 unknown.</summary>
        public int Players { get; set; } = -1;

        /// <summary>Member limit; 0 unknown.</summary>
        public int MaxPlayers { get; set; }

        /// <summary>The host's wire protocol ("" unknown: data not arrived, or not an NGG Multiplayer lobby).</summary>
        public string Protocol { get; set; } = string.Empty;

        /// <summary>The host's mod version, for the "needs version" line.</summary>
        public string ModVersion { get; set; } = string.Empty;

        /// <summary>What the host is doing ("In the menu", "Playing Story"); "" unknown.</summary>
        public string Activity { get; set; } = string.Empty;

        /// <summary>Direct only: "host:port".</summary>
        public string Endpoint { get; set; } = string.Empty;

        /// <summary>The lobby's data has been read (host name, protocol, counts).</summary>
        public bool DataKnown { get; set; }

        /// <summary>A key stable across refreshes, for keeping the selection on the same row.</summary>
        public string Key => Source == LobbySource.Direct ? "direct:" + Endpoint : "lobby:" + LobbyId;

        public LobbyListing Clone() => (LobbyListing)MemberwiseClone();
    }

    /// <summary>Merging, ordering and describing what the browser shows.</summary>
    public static class LobbyListings
    {
        public const int MaxShown = 30;

        /// <summary>
        /// One row per lobby: the same lobby from several sources keeps the best source (invite, then friend, then
        /// public) and the most complete data. The player's own lobby is left out. Invites first, then friends, then
        /// public games with more players first; joinable before not joinable within each group. At most
        /// <see cref="MaxShown"/> rows.
        /// </summary>
        public static List<LobbyListing> Merge(IEnumerable<LobbyListing> found, ulong ownLobby, string localProtocol)
        {
            var byKey = new Dictionary<string, LobbyListing>(StringComparer.Ordinal);
            var order = new List<string>();
            if (found != null)
            {
                foreach (var item in found)
                {
                    if (item == null || (item.Source != LobbySource.Direct && (item.LobbyId == 0 || item.LobbyId == ownLobby)))
                        continue;
                    string key = item.Key;
                    if (!byKey.TryGetValue(key, out var have))
                    {
                        byKey[key] = item.Clone();
                        order.Add(key);
                        continue;
                    }
                    byKey[key] = Combine(have, item);
                }
            }
            var list = new List<LobbyListing>(order.Count);
            foreach (var key in order)
                list.Add(byKey[key]);
            // Stable: equal rows keep the order they were found in.
            var indexed = new List<KeyValuePair<int, LobbyListing>>();
            for (int i = 0; i < list.Count; i++)
                indexed.Add(new KeyValuePair<int, LobbyListing>(i, list[i]));
            indexed.Sort((a, b) =>
            {
                int c = a.Value.Source.CompareTo(b.Value.Source);
                if (c != 0)
                    return c;
                c = CanJoin(b.Value, localProtocol, out _).CompareTo(CanJoin(a.Value, localProtocol, out _));
                if (c != 0)
                    return c;
                c = b.Value.Players.CompareTo(a.Value.Players);
                return c != 0 ? c : a.Key.CompareTo(b.Key);
            });
            list.Clear();
            for (int i = 0; i < indexed.Count && i < MaxShown; i++)
                list.Add(indexed[i].Value);
            return list;
        }

        private static LobbyListing Combine(LobbyListing a, LobbyListing b)
        {
            bool bWins = b.Source < a.Source;
            var best = (bWins ? b : a).Clone();
            var other = bWins ? a : b;
            // Data: whichever has it, the later report when both do.
            if (other.DataKnown && (!best.DataKnown || !bWins))
            {
                best.HostName = other.HostName;
                best.Players = other.Players;
                best.MaxPlayers = other.MaxPlayers;
                best.Protocol = other.Protocol;
                best.ModVersion = other.ModVersion;
                best.Activity = other.Activity;
                best.DataKnown = true;
            }
            if (best.FriendName.Length == 0)
                best.FriendName = other.FriendName;
            if (best.HostName.Length == 0)
                best.HostName = other.HostName;
            return best;
        }

        /// <summary>Whether joining makes sense; <paramref name="reason"/> says why not.</summary>
        public static bool CanJoin(LobbyListing listing, string localProtocol, out string reason)
        {
            reason = string.Empty;
            if (listing == null)
            {
                reason = "Gone.";
                return false;
            }
            if (listing.Source == LobbySource.Direct)
                return true;
            if (!listing.DataKnown)
                return true; // invites and friends' lobbies may not show their data before joining; the join checks it
            if (listing.Protocol.Length == 0)
            {
                reason = "Not a multiplayer game";
                return false;
            }
            if (!string.Equals(listing.Protocol, localProtocol, StringComparison.Ordinal))
            {
                reason = listing.ModVersion.Length > 0 ? "Needs mod version " + listing.ModVersion : "Different mod version";
                return false;
            }
            if (listing.MaxPlayers > 0 && listing.Players >= listing.MaxPlayers)
            {
                reason = "Full";
                return false;
            }
            return true;
        }

        /// <summary>The row's main text: "Michal's Game".</summary>
        public static string Title(LobbyListing listing)
        {
            if (listing.Source == LobbySource.Direct)
                return "Game at " + listing.Endpoint;
            string host = listing.HostName.Length > 0 ? listing.HostName : listing.FriendName;
            return host.Length > 0 ? Possessive(host) + " Game" : "Multiplayer Game";
        }

        /// <summary>The row's second line: "2/4 players · In the menu · Invited by Bob".</summary>
        public static string Detail(LobbyListing listing, string localProtocol)
        {
            var parts = new List<string>();
            if (listing.Players >= 0)
                parts.Add(listing.MaxPlayers > 0 ? listing.Players + "/" + listing.MaxPlayers + " players" : listing.Players + " players");
            if (!CanJoin(listing, localProtocol, out string reason))
                parts.Add(reason);
            else if (listing.Activity.Length > 0)
                parts.Add(listing.Activity);
            switch (listing.Source)
            {
                case LobbySource.Invite:
                    parts.Add(listing.FriendName.Length > 0 ? "Invited by " + listing.FriendName : "Invite");
                    break;
                case LobbySource.Friend:
                    if (listing.FriendName.Length > 0 && listing.FriendName != listing.HostName)
                        parts.Add("With " + listing.FriendName);
                    else
                        parts.Add("Friend");
                    break;
                case LobbySource.Public:
                    parts.Add("Public");
                    break;
                case LobbySource.Direct:
                    parts.Add("Direct connection");
                    break;
            }
            return string.Join(" · ", parts.ToArray());
        }

        public static string Possessive(string name)
        {
            if (string.IsNullOrEmpty(name))
                return string.Empty;
            return name.EndsWith("s", StringComparison.OrdinalIgnoreCase) ? name + "'" : name + "'s";
        }
    }
}
