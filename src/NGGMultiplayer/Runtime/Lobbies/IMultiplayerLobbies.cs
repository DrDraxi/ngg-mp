using System;
using System.Collections.Generic;

namespace NGGMultiplayer.Runtime.Lobbies
{
    /// <summary>A join the player asked for outside the game (a Steam invite they accepted, "Join Game" in the
    /// friends list) that waits for them to confirm, because it would leave the current session or world.</summary>
    public sealed class PendingJoin
    {
        public PendingJoin(ulong lobbyId, string hostName)
        {
            LobbyId = lobbyId;
            HostName = hostName ?? string.Empty;
        }

        public ulong LobbyId { get; }

        /// <summary>The lobby's host or the friend in it; "" unknown.</summary>
        public string HostName { get; }
    }

    /// <summary>
    /// What the multiplayer menus need beyond <see cref="IMultiplayerService"/>: lobby privacy, invites, the game
    /// browser, kicking, and Steam join requests that wait for the player. Implemented by the Steam and Direct
    /// services. Main thread only; members never throw.
    /// </summary>
    public interface IMultiplayerLobbies
    {
        /// <summary>Steam lobbies: privacy, invites and friends' / public games exist.</summary>
        bool HasLobbies { get; }

        /// <summary>The invite overlay can be opened now (in a lobby, Steam overlay available).</summary>
        bool CanInvite { get; }

        /// <summary>Who can join the lobby being hosted, or the next one.</summary>
        LobbyVisibility Visibility { get; }

        /// <summary>Players per session (including the host) of the hosted lobby, or the next one.</summary>
        int MaxPlayers { get; }

        /// <summary>Hosts with these lobby settings (also kept as the defaults for the next time).</summary>
        bool Host(LobbyVisibility visibility, int maxPlayers);

        /// <summary>Changes who can join while hosting (and the default). False when not possible.</summary>
        bool SetVisibility(LobbyVisibility visibility);

        /// <summary>Starts looking for games (friends' lobbies, public lobbies, invites); results arrive over time.</summary>
        void RefreshLobbies();

        bool IsRefreshing { get; }

        /// <summary>The games found by the last refresh plus invites, merged and ordered (<see cref="LobbyListings.Merge"/>).</summary>
        IReadOnlyList<LobbyListing> Lobbies { get; }

        /// <summary>Joins a listed game. False with <see cref="IMultiplayerService.StatusMessage"/> saying why.</summary>
        bool Join(LobbyListing listing);

        /// <summary>Host only: removes a player from the session.</summary>
        bool Kick(ulong playerId);

        /// <summary>Host only: a short line about what the host is doing, shown to others in the browser.</summary>
        void SetActivity(string activity);

        /// <summary>
        /// Decides whether a join request from Steam must wait for the player (it does whenever this returns true,
        /// and always while in a session). Null: join at once when offline.
        /// </summary>
        Func<bool> ConfirmJoinRequests { get; set; }

        /// <summary>The join request waiting for the player's answer, or null.</summary>
        PendingJoin PendingJoin { get; }

        /// <summary>Joins (leaving the current session first) or drops <see cref="PendingJoin"/>.</summary>
        void AnswerPendingJoin(bool accept);

        /// <summary>Something to tell the player (an invite arrived).</summary>
        event Action<string> Notice;
    }
}
