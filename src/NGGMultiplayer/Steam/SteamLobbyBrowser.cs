using System;
using System.Collections.Generic;
using System.Globalization;
using NGGMultiplayer.Core.Diagnostics;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Runtime.Lobbies;
using Steamworks;

namespace NGGMultiplayer.Steam
{
    /// <summary>
    /// Finds games for the multiplayer browser: the lobbies Steam friends are in (their game info and their
    /// rich-presence connect string), Steam's public lobby list (NGG Multiplayer lobbies of this protocol), and the
    /// invites that arrived while the game runs. Lobby data (host name, players, activity, version) is read as it
    /// arrives. Main thread only; nothing here throws.
    /// </summary>
    internal sealed class SteamLobbyBrowser : IDisposable
    {
        private const string Log = "[SteamLobby] ";
        private const int PublicResults = 50;
        private const double RefreshTimeoutSeconds = 8;
        private const int MaxInvites = 10;

        private readonly CallResult<LobbyMatchList_t> _matchList;
        private readonly Callback<LobbyDataUpdate_t> _dataUpdate;
        private readonly Callback<LobbyInvite_t> _invite;
        private readonly Dictionary<ulong, LobbyListing> _found = new Dictionary<ulong, LobbyListing>();
        private readonly List<LobbyListing> _invites = new List<LobbyListing>();
        private List<LobbyListing> _merged = new List<LobbyListing>();
        private double _refreshStartedAt = double.NaN;
        private bool _publicPending;
        private bool _disposed;

        public SteamLobbyBrowser()
        {
            _matchList = CallResult<LobbyMatchList_t>.Create(OnMatchList);
            _dataUpdate = Callback<LobbyDataUpdate_t>.Create(OnDataUpdate);
            _invite = Callback<LobbyInvite_t>.Create(OnInvite);
        }

        /// <summary>The lobby to leave out (the player's own).</summary>
        public ulong OwnLobby { get; set; }

        public bool IsRefreshing => _publicPending;

        public IReadOnlyList<LobbyListing> Lobbies => _merged;

        /// <summary>"Bob invited you to play."</summary>
        public event Action<string> Invited;

        public void Refresh(double now)
        {
            if (_disposed)
                return;
            _found.Clear();
            try
            {
                ScanFriends();
            }
            catch (Exception e)
            {
                NetLog.Warn($"{Log}Reading friends' games failed: {e.Message}");
            }
            try
            {
                SteamMatchmaking.AddRequestLobbyListStringFilter(SteamLobby.ProtocolKey,
                    ProtocolInfo.Version.ToString(CultureInfo.InvariantCulture), ELobbyComparison.k_ELobbyComparisonEqual);
                SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
                SteamMatchmaking.AddRequestLobbyListResultCountFilter(PublicResults);
                _matchList.Set(SteamMatchmaking.RequestLobbyList());
                _publicPending = true;
                _refreshStartedAt = now;
            }
            catch (Exception e)
            {
                _publicPending = false;
                NetLog.Warn($"{Log}Requesting the public lobby list failed: {e.Message}");
            }
            Remerge();
        }

        public void Tick(double now)
        {
            if (_publicPending && now - _refreshStartedAt > RefreshTimeoutSeconds)
            {
                _publicPending = false;
                NetLog.Info($"{Log}The public lobby list did not arrive within {RefreshTimeoutSeconds:0} s.");
            }
        }

        /// <summary>Drops an invite (joined it, or it is gone).</summary>
        public void ForgetInvite(ulong lobby)
        {
            _invites.RemoveAll(i => i.LobbyId == lobby);
            Remerge();
        }

        /// <summary>A name for a lobby seen in the browser or an invite ("" unknown).</summary>
        public string NameOf(ulong lobby)
        {
            foreach (var l in _merged)
            {
                if (l.LobbyId == lobby)
                    return l.HostName.Length > 0 ? l.HostName : l.FriendName;
            }
            return string.Empty;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _matchList.Dispose();
            _dataUpdate.Dispose();
            _invite.Dispose();
        }

        // ---- Sources ------------------------------------------------------------------------------------

        private void ScanFriends()
        {
            var app = SteamUtils.GetAppID();
            int count = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
            for (int i = 0; i < count; i++)
            {
                var friend = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                ulong lobby = 0;
                if (SteamFriends.GetFriendGamePlayed(friend, out FriendGameInfo_t game))
                {
                    if (game.m_gameID.AppID() != app)
                        continue;
                    if (game.m_steamIDLobby.IsValid() && game.m_steamIDLobby.IsLobby())
                        lobby = game.m_steamIDLobby.m_SteamID;
                }
                else
                {
                    continue;
                }
                // Friends-only lobbies show in the game info; the connect string also covers the rest.
                if (lobby == 0 && SteamLobby.TryParseConnect(SteamFriends.GetFriendRichPresence(friend, "connect"), out ulong connect))
                    lobby = connect;
                if (lobby == 0)
                    continue;
                var listing = new LobbyListing
                {
                    LobbyId = lobby,
                    Source = LobbySource.Friend,
                    FriendName = SteamFriends.GetFriendPersonaName(friend) ?? string.Empty
                };
                Add(listing);
            }
        }

        private void OnMatchList(LobbyMatchList_t result, bool ioFailure)
        {
            if (_disposed)
                return;
            _publicPending = false;
            if (ioFailure)
            {
                NetLog.Info($"{Log}The public lobby list failed (I/O).");
                return;
            }
            for (int i = 0; i < result.m_nLobbiesMatching; i++)
            {
                var lobby = SteamMatchmaking.GetLobbyByIndex(i);
                if (lobby.IsValid())
                    Add(new LobbyListing { LobbyId = lobby.m_SteamID, Source = LobbySource.Public });
            }
            Remerge();
        }

        private void OnInvite(LobbyInvite_t invite)
        {
            if (_disposed)
                return;
            try
            {
                if (invite.m_ulGameID != 0 && new CGameID(invite.m_ulGameID).AppID() != SteamUtils.GetAppID())
                    return;
                var from = new CSteamID(invite.m_ulSteamIDUser);
                string name = SteamFriends.GetFriendPersonaName(from) ?? string.Empty;
                _invites.RemoveAll(i => i.LobbyId == invite.m_ulSteamIDLobby);
                if (_invites.Count >= MaxInvites)
                    _invites.RemoveAt(0);
                var listing = new LobbyListing { LobbyId = invite.m_ulSteamIDLobby, Source = LobbySource.Invite, FriendName = name };
                ReadData(listing);
                _invites.Add(listing);
                Remerge();
                NetLog.Info($"{Log}Invite from {name} to lobby {invite.m_ulSteamIDLobby}.");
                var handler = Invited;
                handler?.Invoke((name.Length > 0 ? name : "A friend") + " invited you to play. Open Multiplayer > Join Game, or accept it in Steam.");
            }
            catch (Exception e)
            {
                NetLog.Warn($"{Log}Handling an invite failed: {e.Message}");
            }
        }

        private void OnDataUpdate(LobbyDataUpdate_t update)
        {
            if (_disposed || update.m_ulSteamIDLobby != update.m_ulSteamIDMember)
                return; // a member's data, not the lobby's
            bool changed = false;
            if (_found.TryGetValue(update.m_ulSteamIDLobby, out var listing))
                changed |= ReadData(listing) || update.m_bSuccess == 0;
            foreach (var invite in _invites)
            {
                if (invite.LobbyId == update.m_ulSteamIDLobby)
                    changed |= ReadData(invite);
            }
            if (update.m_bSuccess == 0 && listing != null && !listing.DataKnown && listing.Source == LobbySource.Public)
                _found.Remove(update.m_ulSteamIDLobby); // gone meanwhile
            if (changed)
                Remerge();
        }

        private void Add(LobbyListing listing)
        {
            if (listing.LobbyId == 0 || listing.LobbyId == OwnLobby)
                return;
            if (_found.TryGetValue(listing.LobbyId, out var have) && have.Source <= listing.Source)
            {
                if (have.FriendName.Length == 0)
                    have.FriendName = listing.FriendName;
                return;
            }
            if (have != null && listing.FriendName.Length == 0)
                listing.FriendName = have.FriendName;
            _found[listing.LobbyId] = listing;
            if (!ReadData(listing))
            {
                try
                {
                    SteamMatchmaking.RequestLobbyData(new CSteamID(listing.LobbyId));
                }
                catch (Exception e)
                {
                    NetLog.Debug($"{Log}RequestLobbyData failed: {e.Message}");
                }
            }
        }

        /// <summary>Reads what Steam has of the lobby's data now; true when it has the protocol key (data known).</summary>
        private static bool ReadData(LobbyListing listing)
        {
            var id = new CSteamID(listing.LobbyId);
            string protocol = SteamMatchmaking.GetLobbyData(id, SteamLobby.ProtocolKey) ?? string.Empty;
            if (protocol.Length == 0)
                return false;
            listing.Protocol = protocol;
            listing.ModVersion = SteamMatchmaking.GetLobbyData(id, SteamLobby.ModVersionKey) ?? string.Empty;
            listing.HostName = SteamMatchmaking.GetLobbyData(id, SteamLobby.NameKey) ?? string.Empty;
            listing.Activity = SteamMatchmaking.GetLobbyData(id, SteamLobby.ActivityKey) ?? string.Empty;
            listing.Players = SteamMatchmaking.GetNumLobbyMembers(id);
            listing.MaxPlayers = SteamMatchmaking.GetLobbyMemberLimit(id);
            listing.DataKnown = true;
            return true;
        }

        private void Remerge()
        {
            var all = new List<LobbyListing>(_invites);
            all.AddRange(_found.Values);
            _merged = LobbyListings.Merge(all, OwnLobby, ProtocolInfo.Version.ToString(CultureInfo.InvariantCulture));
        }
    }
}
