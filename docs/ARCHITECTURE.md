# NGG Multiplayer: architecture

BepInEx 5 plugin adding Steam P2P multiplayer to Normal Golf Game (Unity, Steam app 3510740).

## Principles

- **Host-authoritative.** The lobby owner is the host. The host's game save (completed
  challenges, unlocked items, opened locks, any other persistent progress) is the single
  source of truth for the session. Clients load the host's save **in memory only** and
  never write it over their own save file. When a client leaves, their own save is restored.
- Clients send *intents* (shot inputs, interactions, progress claims). The host validates,
  applies to its own game/save, and broadcasts the result.
- Everything game-independent lives in `NGGMultiplayer.Core` (netstandard2.0, no Unity, no
  Steam) and is covered by xunit tests running on a `LoopbackTransport`.
- Everything Unity/Steam/game-specific lives in `NGGMultiplayer` (netstandard2.1 plugin).
  Game-specific hooks go behind `IGameAdapter` / `ISaveBackend` so they can be written once
  the game's assemblies are decompiled.

## Projects

```
src/NGGMultiplayer.Core/       pure C#
  NetSession.cs  facade: SessionManager + save sync + entity sync + RPC, one Update(now)
  Net/        ITransport, PeerId, Channel, Reliability, LoopbackTransport
  Protocol/   NetWriter, NetReader, MessageType, ProtocolInfo, message structs
  Session/    SessionManager (handshake, roster, heartbeats, timeouts), NetClock, ISessionMessaging
  Save/       ISaveBackend, SaveSnapshot, SaveSyncHost, SaveSyncClient, chunking, CRC32
  Sync/       NetEntity registry, TransformState, SnapshotBuffer (interpolation), RpcRouter
  PlayerEntitySpawner.cs  host spawns a player-bound entity per prefab key for every player
src/NGGMultiplayer/            BepInEx plugin
  Plugin.cs   entry point, factories (CreateGameAdapter, CreateService); PluginConfig.cs binds the config
  Steam/      SteamBootstrap, SteamLobby, SteamTransport (SteamNetworkingMessages), SteamMultiplayerService
  Runtime/    IMultiplayerService, MultiplayerSettings, NetRunner (MonoBehaviour tick), PlayerEntityFactory,
              LocalPoseEntity, RemotePlayerView, RemoteBallView
  UI/         LobbyWindow (IMGUI; game lines through Runtime/ILobbyGameInfo)
  Game/       IGameAdapter, NggGameAdapter (real) / NullGameAdapter (fallback), GameContext, IGameModule,
              GamePatcher, ReplayGuard, FactTable, GameSaveBridge, GameIds, Compat, Patches/ (Harmony);
              Lifecycle/, Presence/, Progression/ modules (see docs/GAME_INTEGRATION.md)
scripts/       package.sh / package.ps1 -> dist/NGGMultiplayer-<version>.zip
tests/NGGMultiplayer.Tests/    xunit, net8.0, references Core only
stubs/         compile-time stubs of the game's assemblies (Assembly-CSharp, ECM2, UnityEngine.UI, Unity.TextMeshPro,
               Unity.InputSystem), used with STUB_GAME when GameManagedDir has no game DLLs; never packaged
```

## Contracts

### Net

```csharp
public readonly struct PeerId : IEquatable<PeerId> { public readonly ulong Value; } // SteamID64
public enum Reliability : byte { Unreliable, Reliable }
public enum Channel : byte { Control = 0, Save = 1, State = 2, Events = 3 }
public interface ITransport : IDisposable {
    PeerId LocalId { get; }
    void Send(PeerId to, Channel channel, Reliability reliability, ArraySegment<byte> data);
    // Drains everything received since last call. Ownership of each payload passes to caller.
    void Poll(List<IncomingPacket> into);
    void Disconnect(PeerId peer);
}
public struct IncomingPacket { public PeerId From; public Channel Channel; public byte[] Data; }
```
Connection lifecycle is driven by the session layer (Hello/Welcome/heartbeats), not the
transport, because SteamNetworkingMessages is connectionless.

### Protocol

Every packet = `[byte MessageType][payload]`. `NetWriter`/`NetReader`: little-endian,
`WriteVarUInt`, `WriteString` (UTF-8, varint length), `WriteBytes`, floats, `WriteVector3`
(3 floats), `WriteQuaternion` (4 floats). `NetReader` throws `ProtocolException` on overrun.

MessageType ranges (one module owns each range, so modules never collide):

| Range   | Owner           |
|---------|-----------------|
| 1–19    | Session         |
| 20–39   | Save sync       |
| 40–59   | Entity sync     |
| 60–79   | RPC / events    |
| 80–127  | Game-specific (plugin `Game/`) |

`ProtocolInfo.Version` (int) must match exactly; the mod version string is shown on mismatch.

### Session

Host: `SessionManager.StartHost()`. Client: `SessionManager.Connect(PeerId host)` sends `Hello
{protocol, modVersion, gameBuild, displayName, connectNonce}`; host replies `Welcome {playerIndex, roster}`
or `Reject {reason}`. A Hello with the same nonce is a retry; a new nonce from a listed peer is a reconnect
(the old player leaves, the new one joins). Host broadcasts `RosterUpdate` on joins/leaves. `Ping/Pong` every
second gives RTT and feeds `NetClock` (host time offset). A peer silent for `TimeoutSeconds`
(default 10) is dropped, and so is one whose transport link failed (`ReportLinkLost`). Host leaving ends the session for everyone (`SessionEnded` event,
reason). Events: `PlayerJoined`, `PlayerLeft`, `SessionStarted`, `SessionEnded`. Session
dispatches non-session messages to registered handlers by MessageType.

Save sync, entity sync and RPC talk to the session only through one interface, which
`SessionManager` implements (tests implement it over `LoopbackTransport`):

```csharp
public interface ISessionMessaging {               // NGGMultiplayer.Core.Session
    bool IsHost { get; } PeerId LocalId { get; } PeerId HostId { get; }
    bool IsPeerConnected(PeerId peer);             // host: handshaken client; client: the host
    void SendTo(PeerId peer, Channel c, Reliability r, ArraySegment<byte> packet);
    void Broadcast(Channel c, Reliability r, ArraySegment<byte> packet, PeerId except); // client: to host
    void RegisterHandler(byte messageType, Action<PeerId, NetReader> handler);
    void UnregisterHandler(byte messageType);
    event Action<PeerId> PlayerJoined; event Action<PeerId> PlayerLeft; event Action SessionClosed;
}
```
`SessionManager` raises these PeerId events before its public `PlayerInfo` events, and
`SessionClosed` before `SessionEnded`, so the modules settle (save restored, entities
despawned) before application code hears about it.

### NetSession (facade)

```csharp
var net = new NetSession(transport, saveBackend, entityFactory, new NetSessionOptions { ... });
net.RegisterRpc("golf.shot", handler, hostValidator);   // persists across sessions
net.Started += () => { /* net.Entities / net.Rpc / net.SaveHost|SaveClient exist now */ };
net.StartHost();  // or net.Connect(hostId)
// every frame:
net.Update(Time.realtimeSinceStartupAsDouble);
```
Per session it creates `SaveSyncHost` or `SaveSyncClient`, a `NetEntityRegistry` and an
`RpcRouter` when the session starts, and disposes them when it ends (properties are null
otherwise). `Update(now)` = `Session.Update(now)`, save `Tick(now)`, then
`Entities.Update(Session.HostTime)`; a client's registry starts once its clock has a first
sample from the host. Host: with `GateGameplayOnSaveSync` (default on) a client gets entities
and RPCs only after it acked the save snapshot (`PeerReady`); entities are replicated to it
then. Client: `Ready` once the host's save is loaded; if save sync fails it leaves the session.
`Ended(SessionEndInfo)` comes after the local save was restored. `SaveSyncClient.SnapshotProgress` (0..1) feeds the
lobby window's progress bar. `OwnershipValidator` and RPC
registrations carry over to every session.

### Save sync

```csharp
public interface ISaveBackend {
    byte[] CaptureSnapshot();               // host: serialize current progress
    void ApplySnapshot(byte[] snapshot);    // client: load host progress in memory
    void BackupLocalSave();                 // client: before applying host's save
    void RestoreLocalSave();                // client: on leave
    event Action<SaveDelta> LocalProgressChanged; // fired when the local game records progress
    void ApplyDelta(SaveDelta delta);       // apply a single change without full reload
    bool SuppressDiskWrites { get; set; }   // true on clients while in session
}
public sealed class SaveDelta { public string Kind; public string Key; public byte[] Value; }
```
Flow: on Welcome, host sends `SaveBegin {totalBytes, chunkCount, crc32, revision}` +
`SaveChunk {index, bytes}` (≤ 1000 bytes each, Save channel, reliable, paced at `SnapshotBytesPerTick` per peer per
`Tick`), client verifies CRC,
backs up local save, applies, replies `SaveAck {revision}`. Gameplay for that client waits
until ack. Live progress: host `LocalProgressChanged` → `SaveDelta` broadcast with increasing
revision. Client progress (e.g. client finishes a challenge) → `ProgressClaim` to host → host
applies through `ISaveBackend.ApplyDelta`, which then broadcasts. A client that detects a
revision gap requests a full resync (`SaveResyncRequest`). A client never backs up a save while `SuppressDiskWrites` is
already set (an earlier restore failed); `NetSession` then refuses to start any session until the game restarts.

### Entity sync

`NetEntityId` (uint) assigned by host. `NetEntityRegistry` maps id → owner + `INetEntity`.
`TransformState {pos, rot, vel, angVel, tick/time}`. Owner sends states at `SendRate` Hz
(default 30) unreliable on State channel; host relays client-owned states to other clients.
`SnapshotBuffer` keeps states sorted by time and samples at `now - InterpolationDelay`
(default 100 ms) with linear/slerp interpolation, bounded extrapolation (≤ 250 ms) and
snapping on teleport flag. Spawn/despawn and ownership changes are reliable. Wire codes: Spawn 40, Despawn 41, State 42,
OwnershipChange 43, StateRelayBatch 44, OwnershipRequest 45, OwnershipRelease 46,
OwnershipDenied 47. Reliable entity messages and RPCs share the Events channel, so an RPC
sent after a Spawn never overtakes it.

### RPC / events

`RpcRouter.Register(string name, Action<PeerId, NetReader> handler)`; ids are a stable
32-bit FNV-1a hash of the name. Targets: `Host`, `All`, `Others`, specific peer. Clients
sending to `All`/`Others` route via host, which may veto (validation callback).

### Player entities

`PlayerEntitySpawner(net, prefabKeys)`: the host spawns one entity per key for itself when the session starts
and for each client on `PeerReady`, owned by that player, flagged `DespawnOnOwnerLeave | OwnershipLocked`, with
the player's id in the spawn data (`[1][u64 LE]`, decoded by `TryReadPlayer`). Every peer tracks
(player, key) -> entity. The plugin uses keys `ngg.player` and `ngg.ball`; `TransformState.Flags` bit 0 (`PoseFlags.Visible`)
says the owner has a pose, otherwise the remote view is hidden.

## Plugin

`Plugin` creates the game adapter (`NggGameAdapter`, falling back to `NullGameAdapter` if the game's assembly
does not bind; the game-facing design is specified in [GAME_INTEGRATION.md](GAME_INTEGRATION.md)) and the service
(`SteamMultiplayerService : IMultiplayerService`), then a hidden `DontDestroyOnLoad` object carrying `NetRunner`
(ticks the service with `Time.realtimeSinceStartupAsDouble`) and `LobbyWindow`. Config changes are pushed through
`IMultiplayerService.ApplySettings`: `MaxPlayers` / `TimeoutSeconds` go to the live `SessionConfig` (and the
hosted lobby's member limit / type), `SendRate` / `InterpolationDelay` to `NetEntityRegistryOptions` for the next
session.

`NggGameAdapter.LobbyInfo` (`ILobbyGameInfo`) gives the window the game's lines: the host's scene and mode, each
player's whereabouts, and why this game cannot join (GAME_INTEGRATION.md 12.1).

`SteamMultiplayerService` waits until the game has initialized Steam (it never calls `SteamAPI.Init`), then builds
`SteamTransport`, `SteamLobby`, `NetSession(transport, adapter.SaveBackend, PlayerEntityFactory, options)` and a
`PlayerEntitySpawner`. Hosting = create lobby, tag it with `ngg_mp_version` / `ngg_mp_protocol`, `StartHost`.
Joining = enter lobby, check the protocol tag, `Connect(lobby owner)`. The host only admits lobby members
(`AdmissionFilter`), and the transport only accepts Steam sessions from lobby members. A Steam session failure or a refused
reliable send is reported to `SessionManager.ReportLinkLost`, so a peer whose reliable stream may have gaps is dropped
rather than kept on a silently restarted session; a host leaving the lobby ends client sessions at once. `PlayerEntityFactory` gives
the local player's entities a `LocalPoseEntity` (pose from `IGameAdapter`, velocity estimated when not supplied,
large jumps flagged as teleports) and everyone else's a `RemotePlayerView` / `RemoteBallView`.
