using System;
using System.Globalization;
using NGGMultiplayer.Core.Diagnostics;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Runtime;
using Steamworks;

namespace NGGMultiplayer.Steam
{
    /// <summary>
    /// The Steam lobby a session lives in: the host creates it, invites go through it, clients find the host as its
    /// owner. Also surfaces join requests from invites, the friends list and the launch command line. One lobby at
    /// a time; main thread only.
    /// </summary>
    internal sealed class SteamLobby : IDisposable
    {
        private const string Log = "[SteamLobby] ";
        public const string ConnectArgument = "+connect_lobby";
        public const string ModVersionKey = "ngg_mp_version";
        public const string ProtocolKey = "ngg_mp_protocol";
        /// <summary>SteamID64 of the player hosting the session; differs from the lobby owner once Steam hands the lobby on.</summary>
        public const string HostKey = "ngg_mp_host";
        /// <summary>The host's display name, for the game browser.</summary>
        public const string NameKey = "ngg_mp_name";
        /// <summary>What the host is doing ("In the menu", "Playing Story"), for the game browser.</summary>
        public const string ActivityKey = "ngg_mp_activity";
        /// <summary>The host's <see cref="LobbyVisibility"/>, so members know whether to offer "Join Game" to their friends.</summary>
        public const string VisibilityKey = "ngg_mp_visibility";

        private const uint MemberGoneFlags = (uint)(EChatMemberStateChange.k_EChatMemberStateChangeLeft
            | EChatMemberStateChange.k_EChatMemberStateChangeDisconnected
            | EChatMemberStateChange.k_EChatMemberStateChangeKicked
            | EChatMemberStateChange.k_EChatMemberStateChangeBanned);

        private readonly CallResult<LobbyCreated_t> _created;
        private readonly CallResult<LobbyEnter_t> _entered;
        private readonly Callback<GameLobbyJoinRequested_t> _joinRequested;
        private readonly Callback<GameRichPresenceJoinRequested_t> _richPresenceJoin;
        private readonly Callback<LobbyChatUpdate_t> _chatUpdate;
        // Tracked here rather than via CallResult.IsActive, which stays true while the result handler runs.
        private bool _creating;
        private bool _joining;
        private bool _abandonPending;
        private bool _disposed;

        public SteamLobby()
        {
            _created = CallResult<LobbyCreated_t>.Create(OnCreated);
            _entered = CallResult<LobbyEnter_t>.Create(OnEntered);
            _joinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnJoinRequested);
            _richPresenceJoin = Callback<GameRichPresenceJoinRequested_t>.Create(OnRichPresenceJoin);
            _chatUpdate = Callback<LobbyChatUpdate_t>.Create(OnChatUpdate);
        }

        /// <summary>The lobby we are in, or Nil.</summary>
        public CSteamID Current { get; private set; } = CSteamID.Nil;

        public bool InLobby => Current.IsValid();

        public bool IsBusy => _creating || _joining;

        /// <summary>Our own lobby was created and entered.</summary>
        public event Action<CSteamID> Created;

        /// <summary>We entered someone else's lobby.</summary>
        public event Action<CSteamID> Entered;

        /// <summary>Creating or entering failed (message for the UI).</summary>
        public event Action<string> Failed;

        /// <summary>The user asked to join a lobby from outside the mod's UI (invite, friends list, command line).</summary>
        public event Action<CSteamID> JoinRequested;

        /// <summary>A member left the current lobby, or Steam saw them disconnect (member id).</summary>
        public event Action<CSteamID> MemberLeft;

        public void Create(LobbyVisibility visibility, int maxMembers)
        {
            ThrowIfDisposed();
            if (IsBusy || InLobby)
                throw new InvalidOperationException("Leave the current lobby first.");
            _created.Set(SteamMatchmaking.CreateLobby(ToSteam(visibility), maxMembers));
            _creating = true;
            NetLog.Info($"{Log}Creating {visibility} lobby for {maxMembers} players.");
        }

        public void Join(CSteamID lobby)
        {
            ThrowIfDisposed();
            if (IsBusy || InLobby)
                throw new InvalidOperationException("Leave the current lobby first.");
            _entered.Set(SteamMatchmaking.JoinLobby(lobby));
            _joining = true;
            NetLog.Info($"{Log}Joining lobby {lobby.m_SteamID}.");
        }

        /// <summary>
        /// Leaves the lobby. A pending create or join is abandoned: the lobby it produces is left as soon as Steam
        /// reports it, so no orphaned lobby stays behind. <see cref="IsBusy"/> stays true until then.
        /// </summary>
        public void Leave()
        {
            if (_disposed)
                return;
            if (IsBusy)
                _abandonPending = true;
            if (InLobby)
            {
                SteamMatchmaking.LeaveLobby(Current);
                NetLog.Info($"{Log}Left lobby {Current.m_SteamID}.");
                Current = CSteamID.Nil;
            }
            ClearRichPresence();
        }

        public CSteamID Owner => InLobby ? SteamMatchmaking.GetLobbyOwner(Current) : CSteamID.Nil;

        public bool IsMember(PeerId peer)
        {
            if (!InLobby || !peer.IsValid)
                return false;
            int count = SteamMatchmaking.GetNumLobbyMembers(Current);
            for (int i = 0; i < count; i++)
            {
                if (SteamMatchmaking.GetLobbyMemberByIndex(Current, i).m_SteamID == peer.Value)
                    return true;
            }
            return false;
        }

        public string GetData(string key) => InLobby ? SteamMatchmaking.GetLobbyData(Current, key) ?? string.Empty : string.Empty;

        public void SetData(string key, string value)
        {
            if (InLobby)
                SteamMatchmaking.SetLobbyData(Current, key, value ?? string.Empty);
        }

        /// <summary>Owner only: applies visibility and size to the live lobby.</summary>
        public void Configure(LobbyVisibility visibility, int maxMembers)
        {
            if (!InLobby)
                return;
            SteamMatchmaking.SetLobbyType(Current, ToSteam(visibility));
            SteamMatchmaking.SetLobbyMemberLimit(Current, maxMembers);
        }

        public bool OpenInviteDialog()
        {
            if (!InLobby)
                return false;
            SteamFriends.ActivateGameOverlayInviteDialog(Current);
            return true;
        }

        /// <summary>
        /// Lets friends join from the friends list ("Join Game") unless the lobby is invite only, and groups the
        /// players of one lobby together in their friends lists.
        /// </summary>
        public void PublishRichPresence(bool joinable)
        {
            if (!InLobby)
                return;
            string id = Current.m_SteamID.ToString(CultureInfo.InvariantCulture);
            SteamFriends.SetRichPresence("connect", joinable ? ConnectArgument + " " + id : null);
            SteamFriends.SetRichPresence("steam_player_group", id);
            SteamFriends.SetRichPresence("steam_player_group_size",
                SteamMatchmaking.GetNumLobbyMembers(Current).ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>Opens the Steam overlay's friends list.</summary>
        public static void OpenFriendsOverlay() => SteamFriends.ActivateGameOverlay("friends");

        public void Dispose()
        {
            if (_disposed)
                return;
            Leave();
            // Disposing the call results drops their handlers, so a lobby still being created cannot be left later.
            _created.Dispose();
            _entered.Dispose();
            _joinRequested.Dispose();
            _richPresenceJoin.Dispose();
            _chatUpdate.Dispose();
            _disposed = true;
        }

        /// <summary>Finds "+connect_lobby &lt;id&gt;" in a command line or rich presence connect string.</summary>
        public static bool TryParseConnect(string text, out ulong lobbyId)
        {
            lobbyId = 0;
            if (string.IsNullOrEmpty(text))
                return false;
            int at = text.IndexOf(ConnectArgument, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
                return false;
            int start = at + ConnectArgument.Length;
            while (start < text.Length && (text[start] == ' ' || text[start] == '"' || text[start] == '='))
                start++;
            int end = start;
            while (end < text.Length && text[end] >= '0' && text[end] <= '9')
                end++;
            return end > start
                && ulong.TryParse(text.Substring(start, end - start), NumberStyles.None, CultureInfo.InvariantCulture, out lobbyId)
                && lobbyId != 0;
        }

        /// <summary>A lobby id from the process command line, if Steam launched us to join one.</summary>
        public static bool TryGetLaunchLobby(out CSteamID lobby)
        {
            lobby = CSteamID.Nil;
            string line = null;
            try
            {
                SteamApps.GetLaunchCommandLine(out line, 1024);
            }
            catch (Exception e)
            {
                NetLog.Debug($"{Log}GetLaunchCommandLine failed: {e.Message}");
            }
            if (!TryParseConnect(line, out ulong id) && !TryParseConnect(Environment.CommandLine, out id))
                return false;
            lobby = new CSteamID(id);
            return lobby.IsLobby();
        }

        private void OnCreated(LobbyCreated_t result, bool ioFailure)
        {
            if (_disposed)
                return;
            _creating = false;
            if (_abandonPending)
            {
                _abandonPending = false;
                if (!ioFailure && result.m_eResult == EResult.k_EResultOK)
                    SteamMatchmaking.LeaveLobby(new CSteamID(result.m_ulSteamIDLobby));
                NetLog.Info($"{Log}Abandoned lobby creation.");
                return;
            }
            if (ioFailure || result.m_eResult != EResult.k_EResultOK)
            {
                string reason = ioFailure ? "I/O failure" : result.m_eResult.ToString();
                NetLog.Warn($"{Log}Lobby creation failed: {reason}.");
                Raise(Failed, "Could not create a Steam lobby (" + reason + ").");
                return;
            }
            Current = new CSteamID(result.m_ulSteamIDLobby);
            NetLog.Info($"{Log}Created lobby {Current.m_SteamID}.");
            Raise(Created, Current);
        }

        private void OnEntered(LobbyEnter_t result, bool ioFailure)
        {
            if (_disposed)
                return;
            _joining = false;
            var response = (EChatRoomEnterResponse)result.m_EChatRoomEnterResponse;
            if (_abandonPending)
            {
                _abandonPending = false;
                if (!ioFailure && response == EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
                    SteamMatchmaking.LeaveLobby(new CSteamID(result.m_ulSteamIDLobby));
                NetLog.Info($"{Log}Abandoned lobby join.");
                return;
            }
            if (ioFailure || response != EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            {
                string reason = ioFailure ? "I/O failure" : Describe(response);
                NetLog.Warn($"{Log}Joining lobby {result.m_ulSteamIDLobby} failed: {reason}.");
                Raise(Failed, "Could not join the lobby: " + reason + ".");
                return;
            }
            Current = new CSteamID(result.m_ulSteamIDLobby);
            NetLog.Info($"{Log}Entered lobby {Current.m_SteamID}.");
            Raise(Entered, Current);
        }

        private void OnJoinRequested(GameLobbyJoinRequested_t request)
        {
            if (!_disposed && request.m_steamIDLobby.IsValid())
                Raise(JoinRequested, request.m_steamIDLobby);
        }

        private void OnChatUpdate(LobbyChatUpdate_t update)
        {
            if (_disposed || !InLobby || update.m_ulSteamIDLobby != Current.m_SteamID)
                return;
            if ((update.m_rgfChatMemberStateChange & MemberGoneFlags) == 0)
                return;
            var member = new CSteamID(update.m_ulSteamIDUserChanged);
            NetLog.Info($"{Log}{member.m_SteamID} left lobby {Current.m_SteamID}.");
            Raise(MemberLeft, member);
        }

        private void OnRichPresenceJoin(GameRichPresenceJoinRequested_t request)
        {
            if (!_disposed && TryParseConnect(request.m_rgchConnect, out ulong id))
                Raise(JoinRequested, new CSteamID(id));
        }

        private static void ClearRichPresence()
        {
            try
            {
                SteamFriends.SetRichPresence("connect", null);
                SteamFriends.SetRichPresence("steam_player_group", null);
                SteamFriends.SetRichPresence("steam_player_group_size", null);
            }
            catch (Exception e)
            {
                NetLog.Debug($"{Log}Clearing rich presence failed: {e.Message}");
            }
        }

        private static ELobbyType ToSteam(LobbyVisibility visibility)
        {
            switch (visibility)
            {
                case LobbyVisibility.Private: return ELobbyType.k_ELobbyTypePrivate;
                case LobbyVisibility.Public: return ELobbyType.k_ELobbyTypePublic;
                case LobbyVisibility.Invisible: return ELobbyType.k_ELobbyTypeInvisible;
                default: return ELobbyType.k_ELobbyTypeFriendsOnly;
            }
        }

        private static string Describe(EChatRoomEnterResponse response)
        {
            switch (response)
            {
                case EChatRoomEnterResponse.k_EChatRoomEnterResponseDoesntExist: return "it no longer exists";
                case EChatRoomEnterResponse.k_EChatRoomEnterResponseNotAllowed: return "not allowed";
                case EChatRoomEnterResponse.k_EChatRoomEnterResponseFull: return "it is full";
                case EChatRoomEnterResponse.k_EChatRoomEnterResponseBanned: return "banned";
                case EChatRoomEnterResponse.k_EChatRoomEnterResponseLimited: return "limited Steam accounts cannot join";
                default: return response.ToString();
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SteamLobby));
        }

        private static void Raise<T>(Action<T> handler, T arg)
        {
            if (handler == null)
                return;
            try
            {
                handler(arg);
            }
            catch (Exception e)
            {
                NetLog.Error($"{Log}Event handler threw: {e}");
            }
        }
    }
}
