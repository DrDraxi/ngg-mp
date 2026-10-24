# NGG Multiplayer: game integration spec

Binding spec for the three game modules that connect `NGGMultiplayer.Core` to Normal Golf Game. Read
`docs/ARCHITECTURE.md` (Core) and `docs/gamemap/01..05` (the game) first. Every game member named here is from
docs/gamemap; anything the map does not pin down is marked **(confirm)** and listed in section 11. Where this spec
and an implementer's intuition differ, the spec wins; where the spec is impossible against the real game, the
implementer designs around it, keeps the behavior described here, and reports the deviation.

Contents: 0 ownership and rules · 1 shared API · 2 messages and facts · 3 the replication pattern ·
4 lifecycle and scenes · 5 host save = session save · 6 progression · 7 presence · 8 physics objects ·
9 edge cases · 10 test plan · 11 needs confirmation · 12 integration (as built)

---

## 0. Ownership and rules

| Owner | Folder / files | Module class |
|---|---|---|
| Lifecycle implementer | `src/NGGMultiplayer/Game/Lifecycle/` | `LifecycleSaveModule` (sections 4, 5, and 7.6 as built) |
| Presence implementer | `src/NGGMultiplayer/Game/Presence/` | `PresenceModule` (section 7) |
| Progression implementer | `src/NGGMultiplayer/Game/Progression/` | `ProgressionModule` (sections 6, 8) |
| Integrator | `Game/*.cs` (shared), `Runtime/`, `Steam/`, `UI/`, `Plugin.cs`, `PluginConfig.cs`, Core, tests, stubs | `NggGameAdapter`, `GameContext` |

Rules for every implementer:

1. **Only add or change files in your own folder.** Shared files are complete; if you need a change there, describe
   it in your summary as an integrator request and work around it meanwhile. Never reference another module's
   types; talk through `GameContext`, the fact table and the contracts in `Game/`.
2. **Offline is vanilla.** Every patch does nothing unless the relevant session condition holds (`ctx.InSession`,
   `ctx.IsHost`, `ctx.IsClient`, or the Lifecycle save state). Single-player must behave exactly as without the mod.
3. **No `[HarmonyPatch]` attributes.** Patch through `ctx.Patcher` with a feature key `"<module>.<feature>"`
   (`"lc.save.write"`, `"pr.wind"`, `"pg.bounty"`). Look members up with `ctx.Patcher.Method/PropertyGetter/Field/
   FieldRef`; a miss disables that feature with one log line and the rest keeps working.
4. **Access.** Public game members directly; private ones (and anything marked ACCESS-GUESS or SIG-GUESS in the
   stubs) only through AccessTools / `Traverse` / `FieldRef` / reflection by name. The build compiles against
   stubs, the release against the real DLLs; both must work. Never name `ClubPanel` or `Streak` (invented stub
   names); call `global::Utilities.CleanString.RemoveClone` only through `GameIds.Clean`. No optional parameters:
   pass every argument. Unity 6 vs 2021 differences only through `Compat` (`GetVelocity`, `FindAll`, `FindAny`).
5. **Patch parameters by position.** Where the map gives a parameter name only as SIG-GUESS, inject by position
   (`__0`, `__1`, ...) instead of by name. `__instance`, `__result`, `__originalMethod` are fine.
6. **Patches never throw into the game.** Template:
   ```csharp
   private static bool WriteSavePrefix(ref IEnumerator __result)
   {
       var ctx = GameContext.Current;
       if (ctx == null || !ctx.Patcher.IsEnabled(Feature)) return true;
       try { /* ... */ }
       catch (Exception e) { ctx.Patcher.ReportFailure(Feature, e); /* fail open or closed, see below */ }
       return true;
   }
   ```
   Patches fail **open** (run the game's code unchanged) except the client save-write guards of section 5.5, which
   fail **closed** (skip the write).
7. **Main thread.** Core calls (RPC handlers, `IFactHandler`, `ISessionSaveStore`) arrive inside `NetRunner.Update`.
   Never call `SceneManager.LoadScene`, `StartDialogue` or anything that tears the world down from inside a patch
   or a network callback: `ctx.Post(...)` it to the next frame. Delays use `ctx.Delay` (real time) or
   `ctx.RunCoroutine` (the coroutine host survives scene loads).
8. **Static access from patches.** Each module keeps `internal static <Module> Instance` set in `Install`, cleared in
   `Uninstall`; patch bodies use `GameContext.Current` plus that instance and null-check both. Plugin teardown
   removes all Harmony patches (`UnpatchSelf`), so `Uninstall` does not unpatch.
9. **RunSave and UnlocksSave are structs.** Mutate in place: `ref RunSave run = ref SaveManager.instance.m_run;`.
   A copy (`var run = SaveManager.instance.m_run`) changes nothing in the game.
10. **Never trust the wire.** RPC handlers bound every count and length, throw `ProtocolException` on malformed
    input, and the host re-checks every client request (validators, section 2).
11. **Logs.** Prefix `[Lifecycle]`, `[Presence]`, `[Progression]`. Use `ctx.WarnOnce/ErrorOnce` for anything that
    can repeat per frame. User-facing messages go through `ctx.Notify` (top-of-screen toast).
12. **Style.** As the existing plugin: `namespace X { }` blocks, 4 spaces, `_camelCase` private fields, sparse
    comments that say why. Build at 0 warnings / 0 errors; `dotnet test` green.

---

## 1. Shared API (integrator-owned, complete)

`NggGameAdapter` (`IGameAdapter`) is created by `Plugin` before the service (the service needs its
`SaveBackend`), then `Attach(service, harmony, hostObject, showLobbyWindow)` installs the modules in the order
Lifecycle, Presence, Progression. It forwards pose/visual calls to the presence provider, claim validation and the
save backend to `GameSaveBridge`, and session ready/ended to the context and modules. If the game assemblies do not
bind, `Plugin` falls back to `NullGameAdapter` (game features off, sessions still run).

`IGameModule`: `Name`, `Install(GameContext)`, `Uninstall()`, `Tick(float unscaledDt)`, `OnSessionReady(bool isHost)`
(host: session started; client: first host snapshot applied), `OnSessionEnded()` (client: own save already restored).

`GameContext` (one instance, `GameContext.Current` for patches):

| Member | Meaning |
|---|---|
| `Harmony`, `Patcher` | Harmony instance; patch helper with per-feature disable (`IsEnabled`, `Disable`, `ReportFailure`, `Method`, `PropertyGetter`, `Field`, `FieldRef<T,F>`, `Prefix/Postfix/Finalizer/Apply`) |
| `Replay` | `ReplayGuard` (section 3): `Begin(reason)` scope, `HoldFor(s)`, `IsReplaying`, `QuietSeconds` |
| `Save`, `Facts` | `GameSaveBridge` (Core's `ISaveBackend`); `FactTable` of the session |
| `Service`, `Net` | `IMultiplayerService`; `NetSession` (null until Steam is up) |
| `InSession`, `IsReady`, `IsHost`, `IsClient`, `IsClientConnection` | role and state; `IsReady` = gameplay traffic flows (client: host save loaded) |
| `LocalId`, `HostId`, `Players`, `TryGetPlayer`, `PlayerName` | roster |
| `PlayerJoined`, `PlayerLeft`, `PeerReady` (host: client loaded the save), `SessionReady`, `SessionEnded` | events |
| `RegisterRpc(name, handler, hostValidator)`, `Send(...)`, `Send<T>(...)` | named RPCs (section 2); register in `Install`; registrations carry over to every session |
| `Scene`, `SceneLoadId`, `SceneLoaded` | current `GameScene` (Intro, Menu, Main, Credits), raised on every single-mode load |
| `WorldReady`, `WorldLoaded`, `IsLoadingWorld` | "Main" is set up (postfix of private `SaveManager.SetUpScene`; timer fallback); `IsLoadingWorld` stays true 1 s after |
| `Post`, `Delay`, `RunCoroutine`, `StopRoutine` | dispatcher (Post is thread-safe) |
| `LateTick`, `Gui` | LateUpdate and OnGUI events |
| `SetPresenceProvider`, `Presence`, `ShowLobbyWindow`, `Notify`, `Info/Warn/Error/WarnOnce/ErrorOnce` | providers, UI, logs |

Contracts each module installs in `Install`:

| Contract | Installed by | With |
|---|---|---|
| `ISessionSaveStore` (`CaptureBundle`, `ApplyBundle`, `BackupLocalSave`, `RestoreLocalSave`, `OnSuppressDiskWritesChanged`) | Lifecycle | `ctx.Save.SetStore(this or helper)` |
| `IFactHandler` (`BuildFacts`, `ValidateClaim`, `ApplyFact`, `OnFactsReplaced`) | Progression | `ctx.Save.SetFactHandler(...)` |
| `IPresenceProvider` (local poses, remote visuals) | Presence | `ctx.SetPresenceProvider(...)` |
| `IRemoteVisual` (component on a remote visual root: `OnRemotePose(flags, velocity, teleport)`) | Presence | on the GameObjects it creates |

Other shared helpers: `GameIds` (stable ids: `so:<SaveableObject.m_id>` or `path:<A/B#1/C>`, `Clean`,
`ChallengeSetName`, `Resolve`, `ResolveSaveable`, `Saveables`), `GameWire` (Vector2/3, Quaternion on the wire),
`Compat`, `GameScenes`, `PoseFlags`, `GameMessages` (prefixes), `RemoteEntityView.WorldVisible` (the context hides
every remote view outside a ready "Main").

`GameSaveBridge` semantics (unit tested): `Table` holds the host's facts. Host (or offline): `ReportLocalFact`
stores the value and, when it changed during a hosted session, raises `LocalProgressChanged` (Core broadcasts a
SaveDelta). Client: `ReportLocalFact` raises only (Core sends a ProgressClaim); the table changes when the host
confirms. `ApplyDelta` stores the value, then calls `IFactHandler.ApplyFact`. `CaptureSnapshot` /
`ApplySnapshot` go through the store (bundle) or, without a store, are the bare table. `RebuildFacts()` replaces
the host table with `IFactHandler.BuildFacts`. On host session start the context calls `RebuildFacts()` before the
modules' `OnSessionReady(true)`.

---

## 2. Messages and facts

### 2.1 Decision: named RPCs, raw types reserved

All game traffic is named RPCs (`ctx.RegisterRpc` / `ctx.Send`): gated until the client loaded the host save
(`GateGameplayOnSaveSync`), sender stamped by the host, host validator per RPC, relay built in, ordered with entity
spawns on the Events channel. Persistent progress is **not** RPC: it is facts carried by Core's SaveDelta /
ProgressClaim / snapshot. Poses are Core entities (`ngg.player`, `ngg.ball`). The raw message types 80..127 stay
unregistered and reserved: Lifecycle 80..89, Presence 90..104, Progression 105..127 (`GameMessages`); using them
needs the integrator. Prefixes: `ngg.lc.` Lifecycle, `ngg.pr.` Presence, `ngg.pg.` Progression. Each module
defines its names as constants in its own folder, exactly as below. Every peer registers every name it can
receive; the host registers all of them (it validates and relays).

Encoding: NetWriter little-endian; `str` = WriteString (UTF-8, varuint length; readers pass a max), `vu` varuint,
`vi` zigzag varint, `f` float, `v2`/`v3`/`q` via GameWire, `b` byte, `bool`.

### 2.2 RPC catalog

| Name | From -> target | Rel. | Payload | Host validator |
|---|---|---|---|---|
| `ngg.lc.scene` | host -> Others | R | `b scene, vu epoch, b mode` | clients may not send |
| `ngg.pr.info` | any -> Others | R | `b scene, b controlMode (0 walk, 1 golf), vi selectedBall, str club (<=32), str activity (<=64)` | sender's own; lengths |
| `ngg.pr.shot` | any -> Others | R | `str club, f power, b shotType, b spinType, vu stroke, bool isMiss, v3 from` | finite, lengths |
| `ngg.pr.rest` | any -> Others | R | `v3 pos, f distance, str surface (<=32), vu stroke` | finite |
| `ngg.pr.holed` | any -> Others | R | `vi par, vu strokes, vi lmugcHole (-1 none)` | ranges |
| `ngg.pr.wind` | host -> Others | U (10 Hz) / R on change | `v2 current, v2 desired, bool hostFree` | clients may not send |
| `ngg.pr.round` | any -> All | R | `vu roundId, b event (0 start, 1 finished, 2 aborted), vi total` | sender's own |
| `ngg.pr.hole` | any -> All | R | `vu roundId, b hole (1..9), vi strokes, vi par` | ranges |
| `ngg.pr.card` | host -> Peer | R | `vu playerCount, n * (u64 peer, vu roundId, b finished, 9 * vi strokes (0 = unplayed))` | clients may not send |
| `ngg.pg.world` | any -> Others | R | `b op, str id (<=128), bool flag` (2.4) | op known, id length, world ready |
| `ngg.pg.story` | any -> Others | R | `str method` | method in the replicate list (6.10) |
| `ngg.pg.storyHost` | client -> Host | R | `str method` | method in the host-executed list |
| `ngg.pg.claim` | client -> Host | R | `vu claimId, str interactId (<=256), b interactionType` | type arbitrated |
| `ngg.pg.grant` | host -> Peer | R | `vu claimId, bool granted, vi hostBalance, str reason (<=128)` | clients may not send |
| `ngg.pg.bank` | client -> Host | R | `vu claimId, vi change` | claim granted to sender, sign/limits (6.8) |
| `ngg.pg.ballUnlock` | client -> Host | R | (empty) | rate limit 1 / 10 s per peer |
| `ngg.pg.note` | host -> Others | R | `u64 by, b kind, str text (<=128)` | clients may not send |
| `ngg.pg.digest` | host -> Others | R (every 10 s) | `vu revision, u32 crc, vu factCount` | clients may not send |
| `ngg.pg.roundScore` | client -> Host | R | `vi score` | JustGolf only, -200..999, 1 / 30 s per peer; host calls `AddScoreToCard` |
| `ngg.pg.lmugcBest` | client -> Host | R | `vi bestBefore, vi best, vi noAssistBefore, vi noAssist` | host value still equals `before`, -200..999, 1 / 30 s per peer |

"Clients may not send" = the validator returns false for any client sender. Targets: `Others` from a client goes
to the host, which runs it locally and relays to the other clients.

### 2.3 Fact catalog (owner: Progression; carried by Core)

A fact is `(kind, key) -> bytes`, set semantics, idempotent to apply. Encodings use `FactTable.EncodeBool/Int/
String` unless stated. "Client claims" = a client may raise it (validated per 6.x); "host" = host-only, clients
never raise it and the validator rejects it.

| Kind | Key | Value | Source (live) | Client claims |
|---|---|---|---|---|
| `track` | `main` | `str setName, vi index` (NetWriter) | `ChallengeSystem.m_mainChallengeSet` clean name; index of its first challenge with `!m_complete` | +1 step only (6.3) |
| `bo` | bounty set clean name | `b flags` (1 done, 2 on easy) | `m_run.m_completedBounties` / `m_completedOnEasy` | additions |
| `dnr` | set name | `[1]` | `m_run.m_doNotReloadChallengeList` entries | additions |
| `easy` | set name | `[1]` | `m_run.m_completedOnEasy` entries of non-bounty sets | additions |
| `rb` | RunSave bool field name | bool | the shared bools of 6.5 | false -> true (except `m_serverCut` both ways) |
| `ri` | RunSave int field name | int | `bountyAccessLevel`, `lmugcStartMulligans`, `lmugcStartNoWinds`, `lmugcStartHoleSkips` | increases |
| `rh` | RunSave int field name | int | `lmugcScoreBest`, `lmugcScoreNoAssistBest` | host |
| `bank` | `` | int | `ProgressManager.instance.m_bank.m_balance` | host |
| `obj` | SaveableObject `m_id` | `b flags` (1 active, 2 has rotation) + `q` if 2 | `SaveableObject.GetSaveData()` | yes (6.9) |
| `pht` | `` | `vu n, n * str` sorted | `m_run.m_preHitTargets` (and `ChallengeSystem.m_preBrokenShots`) | union |
| `unl` | `balls` | `vu n, n * bool` | `SaveManager.m_unlocks.unlockedBalls` | host |
| `sc` | `` | UTF-8 `JsonUtility.ToJson(m_scoreCardSave)` | JustGolf scorecard | host |
| `seed` | `` | int | host random at session start | host |
| `auth` | `GameIds.ForObject(authenticator)` | str code | `Authenticator.code` (private) | host |
| `money` | `story` | `b state` (0 none, 1 deducted, 2 returned) | story money effects (6.10) | host |

Per-player state is never a fact: positions (`m_playerPosition`, `m_playerRotation`, `lastShotPos`), `m_shots`,
`m_time`, `isInLMUGC`, `lmugcScore`, `lmugcCurrent*`, `lmugcNoWindTime`, `lmugcUsedAssist`, `lmugcSaveScumCount`,
current tee (`m_currentTee`), current non-main challenge set, the selected ball.

### 2.4 World ops (`ngg.pg.world`)

| op | Game call (originator postfix, receiver replay) | id | flag |
|---|---|---|---|
| 1 | `ObjectStates.SetGates(string id, bool open)` | gate id | open |
| 2 | `ObjectStates.OpenDoor(string id)` | door id | - |
| 3 | `ObjectStates.OpenOrCloseSlidingDoor(bool open, string id)` | door id | open |
| 4 | `ObjectStates.ServerCut()` | - | - |
| 5 | `ObjectStates.ServerRestore()` | - | - |
| 6 | `BunkerTerminal.SetScreenOpen()` | `GameIds.ForObject(terminal)` | - |
| 7..11 | `ObjectStates.SetDriverAvailable / SetWedgeAvailable / SetHybridAvailable / SetTicketAvailable / EnableLMUGCMachine` | - | - |

Elevator (`OpenTopDoors`, `OpenBottomDoors`, `RideElevator`) and `Teleporter.Teleport` are **not** world ops: they
move the local player and toggle the elevator's private `m_bunker` object for that player's view only.

---

## 3. The replication pattern (all modules)

Three mechanisms, chosen per mechanic:

1. **Facts (state).** Persistent progress is diffed, not evented. Progression computes each fact's live value
   from the game and compares it with the last live value it saw (`lastLive[key]`, per peer); a change is
   reported with `ctx.Save.ReportLocalFact`. Host: stored and broadcast. Client: claimed; the host validates
   (`IFactHandler.ValidateClaim`), applies to its own game (`ApplyFact`), and Core broadcasts the value the host
   recorded. Everyone applies confirmed values idempotently. A refused claim makes Core resync that client (full
   snapshot), which undoes the local change. Change detection is against `lastLive`, never against the table, so
   a fact whose application is deferred (world not ready, player inside a bounty) is never claimed back.
2. **Events (presentation).** Things whose *animation or timing* matters (doors opening, story beats) are also
   sent as RPCs (`ngg.pg.world`, `ngg.pg.story`) to `Others` and replayed when the receiver's world is ready. The
   persistent result still comes from facts, so a missed event never loses state.
3. **Arbitration (exclusive actions).** Pickups and purchases (6.7) ask the host first (`claim` -> `grant`) and only
   then run locally. First claim wins.

**ReplayGuard.** Every game call the mod makes because of the network runs inside
`using (ctx.Replay.Begin("<what>")) { ... }`, followed by `ctx.Replay.HoldFor(seconds)` when the call schedules work
for later (`Invoke(name, 0f)`: 0.5 s; story methods and challenge completion: 1.0 s; coroutine sequences: their
length, at most 5 s). Closing a scope always holds 0.1 s. While `ctx.Replay.IsReplaying`:
- event patches (world ops, story methods, shot events caused by the replay) do not send;
- claim and arbitration prefixes let the game call through unchanged (the replay *is* the authority's decision);
- `SteamManager.UnlockAchievement` is skipped (6.12);
- client fact diffing pauses (it resumes when `QuietSeconds >= 0.5`; state is diffed, so nothing is lost).

The host's own diff runs during replays (the host is the authority; Core merges what a claim apply records).

---

## 4. Session lifecycle and scenes (Lifecycle)

### 4.1 Hosting and joining

The lobby window (`ToggleWindowKey`, default F7, opened from code with `ctx.ShowLobbyWindow()`) hosts, invites,
joins and leaves anywhere: Menu2, Main (also with the pause menu open), CREDITS. Lifecycle adds:
- **Menu2:** an IMGUI button "Multiplayer" (bottom-left, also showing "Hosting N players" / "In <host>'s session")
  that calls `ctx.ShowLobbyWindow()`.
- **Pause menu open in a session** (between `PlayerPressedEscape.OpenMenu` and `PauseMenu.Exit`): a line "Multiplayer:
  F7" plus the player count.
- Steam invites and `+connect_lobby` launches are handled by the service; nothing extra.

**Joining from "Main" (own world running).** On the service state change Offline -> JoiningLobby/Connecting while
`ctx.Scene == Main`, `ctx.WorldReady`, `!MoveAndHitController.instance.m_moveLocked` and
`!ctx.Save.SuppressDiskWrites`: call `SaveManager.instance.SaveRun()` so the player's own latest progress is on disk
before protection starts (its `WriteSave` coroutine runs while the handshake is in flight). During dialogue
(`m_moveLocked`) nothing is flushed: progress since the player's last save is not written, but the save stays
consistent. A client's own world is never kept: the host's world replaces it (4.3).

**Hosting from anywhere.** Nothing changes for the host's game. Lifecycle's host state: `sceneEpoch = 1`,
`lastScene = ctx.Scene`, `lastMode = mode` at `OnSessionReady(true)`.

### 4.2 Host scene tracking

On the host in session:
- `ctx.SceneLoaded(scene)`: send `ngg.lc.scene {scene, epoch + 1 if scene/mode differ, mode}` (clients show
  "Host is loading ..."; nothing follows from it).
- The host scene is **settled** at: `ctx.WorldLoaded` (Main), postfix `MainMenu.Start` (Menu2), `SceneLoaded`
  (CREDITS). Then, via `ctx.Post` (so Progression rebuilt its baselines first): if scene or
  `SaveManager.instance.m_gamemodeState.mode` differs from `lastScene/lastMode`, `sceneEpoch++`; always
  `ctx.Save.RebuildFacts()` and `ctx.Net.SaveHost.ResyncAll()` (the host's `m_run` was reloaded outside
  `LocalProgressChanged`, which is exactly what `ResyncAll` is for). A Main -> Main reload (broken-save reload,
  `RestartFromHole1` if it reloads) resyncs without an epoch change, so clients are not reloaded.

### 4.3 Client follow rules

The bundle (5.1) carries `hostScene`, `sceneEpoch`, `mode`. After `ApplyBundle`, Lifecycle `ctx.Post`s
`FollowHost()`:

| Host scene in bundle | Client target |
|---|---|
| Main (with a run) | Main |
| Menu | Menu |
| Credits | Credits if the client is in Main or Credits now (it played along), else Menu |
| Intro / None / Other | Menu |

- Never interrupt INTRO: if `ctx.Scene == Intro`, wait for the next `SceneLoaded` and decide again.
- Load when `target != ctx.Scene`, or when target is Main and (first bundle of this session, epoch changed, or
  mode changed). A mid-session resync with the same epoch never reloads; Progression reconciles in place.
- Before `SceneManager.LoadScene("Main")`: set `SaveManager.instance.m_gamemodeState.mode = bundle.mode` (the
  GameModeState is a shared ScriptableObject) and toast "Joining <host>'s game ...". Loading Main runs
  `SaveManager.Start -> LoadRunSave -> SetUpScene`, which the injection (5.4) feeds with the host's save.
- In Menu (waiting): show a centered panel "In <host>'s session. Waiting for the host to start a game." with a
  "Leave session" button (`ctx.Service.Leave()`). Prefix-skip on clients in session: `MainMenu.PlayGame`,
  `MainMenu.PlaySetUp`, `MainMenu.PlayNineButtonPressed`, `MainMenu.PlayNine` (toast "The host picks the game").
  Settings and quit stay available.
- Postfix `MainMenu.Start` on a client with an active session save: re-set `m_gamemodeState.mode = bundle.mode`
  (MainMenu.Start forces Story).

### 4.4 Joining by host scene

| Host is in | Client is in | Result |
|---|---|---|
| Menu2 | Menu2 | stays, waiting panel; follows when the host starts |
| Menu2 | Main / CREDITS | goes to Menu2 (own world discarded, nothing written), waits |
| Main | Menu2 / Main / CREDITS | loads Main with the host save, spawns at the host's position |
| CREDITS | anywhere | Menu2, waits ("The host is watching the credits") |
| INTRO | anywhere | Menu2, waits |

### 4.5 Leaving

- **Client leaves** (lobby window, "Leave session", `PauseMenu.QuitToMenu`, kick, timeout, host gone): Core ends the
  session -> `RestoreLocalSave` (5.6) -> `OnSessionEnded`. Lifecycle then `ctx.Post`s `LoadScene("Menu2")` unless
  already loading Menu2 (QuitToMenu does it itself) or the application is quitting (`Application.quitting`). Menu2's
  `MainMenu.Start` reads the player's own save from disk. Toast with the reason (`IMultiplayerService.StatusMessage`).
- `PauseMenu.QuitToMenu` prefix on a client in session: set `_quitToMenu = true`, call `ctx.Service.Leave()`, let
  the original run (its `SaveRun` writes nothing: the post-session fence of 5.5 is up).
- **Host leaves:** clients get `HostLeft` and do the above. The host's game continues untouched.
- **Host returns to Menu2** (`QuitToMenu` on the host): the session stays open; clients follow to Menu2 and wait
  (4.3). `PauseMenu.QuitToMenu` on the host does not leave.
- **Quit to desktop:** `NetRunner.OnApplicationQuit` leaves; the client's restore runs with the fence up, no scene
  load; nothing is written.

### 4.6 Story vs JustGolf

The host's `m_gamemodeState.mode` is the session's. The host switches modes only from Menu2 (`MainMenu.PlayNine` ->
JustGolf, `PlayGame` -> Story); each switch is a scene/mode change (epoch++), and clients follow into Main in that
mode. Clients cannot start either mode themselves.

---

## 5. Host save = session save (Lifecycle)

### 5.1 Bundle (`ISessionSaveStore.CaptureBundle`)

```
vu    format = 1
b     hostScene       GameScene
vu    sceneEpoch
b     mode            (byte)GameMode: 0 Story, 1 JustGolf
bool  hasRun
bytes runJson         UTF-8 JsonUtility.ToJson(run copy)            (empty when !hasRun)
bytes unlocksJson     UTF-8 JsonUtility.ToJson(SaveManager.instance.m_unlocks)
bytes scoreCardJson   UTF-8 JsonUtility.ToJson(SaveManager.instance.m_scoreCardSave)
bytes facts           FactTable.ToArray() of the table passed in
```
Use `WriteBytes` (no 64 KB string limit); readers cap each at 4 MB. Capture never calls `SaveRun` and never changes
game state.

**Run copy (host).** `var run = SaveManager.instance.m_run;` (struct copy). When `ctx.WorldReady`: replace
`m_savedObjects` with the live `GetSaveData()` of every SaveableObject (`GameIds.Saveables`, inactive included, in
`m_id` order), `m_bankBalance` with `ProgressManager.instance.m_bank.m_balance`, and `m_playerPosition/
m_playerRotation` with the host's live `MoveAndHitController.instance.m_fpsCharacter.GetPosition()/GetRotation()`.
In Menu2 / CREDITS the copy is the `m_run` the game loaded from the host's disk. No SaveManager -> `hasRun = false`.

### 5.2 ApplyBundle (client)

Parse fully (throw on any error before touching state). Keep the bundle as the **session save**
(`SessionSaveActive = true`). Write the sandbox files (5.5) from it. Return its fact table. Then `ctx.Post(FollowHost)`.
Raise nothing (Core forbids `LocalProgressChanged` from `ApplySnapshot`).

### 5.3 Personalizing the host run for this client

Done on the client each time the run is injected (5.4), from the bundle's run and the current fact table:
- `m_challengeSetLoaded`, `m_currentChallenge` = the `track/main` fact when present (so a host inside a bounty does
  not drop the client into it).
- Story: `isInLMUGC = false`. Both modes: fresh LMUGC round state: `lmugcScore = new int[9]`,
  `lmugcCurrentMulligans = lmugcStartMulligans`, `lmugcCurrentNoWinds = lmugcStartNoWinds`,
  `lmugcCurrentHoleSkips = lmugcStartHoleSkips`, `lmugcNoWindTime = 0`, `lmugcUsedAssist = false`,
  `lmugcSaveScumCount = 0` (JustGolf keeps `isInLMUGC`, so `SetUpScene`'s `Invoke("LoadInLMUGC", 0.1f)` starts the
  client at hole 1).
- Position and rotation: the host's (from the run copy). Remote avatars have no colliders, so spawning on the host
  cannot get stuck.
- Unlocks: the host's `unlockedBalls`; `selectedBall` = the player's own (remembered at backup) if that index exists
  and is unlocked in the host's or the player's own list, else the host's.

### 5.4 Injection (client, while `SessionSaveActive`)

Prefixes, each `return false` with `__result` set:

| Method | Does |
|---|---|
| `SaveManager.LoadRunSave()`, `LoadStoryRun()`, `LoadNormalGolfRun()`, `GetBackUpStorySave()` | `m_gamemodeState.mode = bundle.mode`; `m_run = personalized run`; `__result = m_run` (both, since it is not known whether the caller assigns) **(answered: 12.6)** |
| `SaveManager.GetUnlocksSave()` | `m_unlocks = personalized unlocks`; `__result = m_unlocks` |
| `SaveManager.GetScoreCardSave()` | `m_scoreCardSave = bundle scorecard`; `__result` |
| `SaveManager.HasRun()` / `HasStartedNine()` | `__result = hasRun && mode == Story` / `hasRun && mode == JustGolf` |
| `SaveManager.UpgradeDemoSave()` | skip |

`LoadGameSettings` / `SaveGameSettings` (config.txt) stay local and writable. If any injection feature failed to
install, `BackupLocalSave` throws ("cannot load the host's save safely: <features>") and the client does not join.

### 5.5 Write protection (client)

`WritesBlocked = ctx.Save.SuppressDiskWrites || _postSessionFence`.

1. **Skips** (prefix, `return false`, fail closed): `SaveManager.WriteSave(string)` (`__result` = an empty
   `IEnumerator`), `SafeWrite(string,string,string)`, `SaveUnlocks()`, `AddScoreToCard(int)`, `DeleteRun()`,
   `DeleteEverything()`, `CreateBackupOfSave(string,string)`, `UpgradeDemoSave()`.
   `SaveRun()` itself runs (it only updates `m_run` in memory and starts `WriteSave`).
2. **Redirect** (postfix on the getters, while `SessionSaveActive`): `m_runSavePath`, `m_runSavePath_writeBackUp`,
   `m_normalGolfSavePath`, `m_scorecardSavePath`, `m_unlocksSavePath` -> same file name in the sandbox,
   `m_backupSavePath` -> sandbox folder + "/". `m_settingsSavePath` (config.txt) is never redirected. The sandbox is
   `Path.Combine(BepInEx.Paths.CachePath, "NGGMultiplayer", "SessionSave")`, outside `persistentDataPath`, which
   Steam Cloud syncs. It is emptied at backup and at restore and on plugin load. At `ApplyBundle` it receives
   `run2.txt` (Story) or `actuallyNormal.txt` (JustGolf) with `runJson`, `unlocks.txt`, `scoreCard.txt`, so game
   code that reads files by path (`SaveReader` in CREDITS) sees the host's save **(answered: 12.6)**.
3. **Post-session fence.** `RestoreLocalSave` raises `_postSessionFence`; it drops at the postfix of `MainMenu.Start`
   in Menu2 once `SessionSaveActive` is false (Menu2 re-read the player's own save from disk). Watchdog: 10 s in
   Menu2 without that postfix -> `SaveManager.instance.m_run = SaveManager.instance.LoadRunSave();
   m_unlocks = GetUnlocksSave();` then drop the fence. Until it drops, the in-memory save may still hold host data
   or a host world snapshot, so nothing may be written.
4. **Required features.** Skips 1 and redirect 2 are one feature group `"lc.save.protect"`; if any part is missing,
   the client refuses to join (5.4). The host is never blocked.

### 5.6 Backup and restore (client)

`BackupLocalSave` (Core calls it right before the first `ApplySnapshot`; throw = do not join):
1. Require `"lc.save.protect"` and the injection features.
2. Copy the real files (paths built from `Application.persistentDataPath` + `run2.txt`, `run2_temp.txt`,
   `actuallyNormal.txt`, `scoreCard.txt`, `unlocks.txt`; not via the getters) that exist into
   `<BepInEx cache>/NGGMultiplayer/LocalBackup/<yyyyMMdd-HHmmss>/` with a manifest of name, length, CRC32. Keep the
   5 newest. Any IO error throws.
3. Remember the player's own `selectedBall` and mode (in-memory `SaveManager.instance` values when present).
4. Empty the sandbox.

`RestoreLocalSave` (session end; throw only if the player's files could not be put right):
1. `_postSessionFence = true`; `SessionSaveActive = false`; drop the bundle.
2. Verify each real file against the manifest. A file that changed (nothing in the mod writes it, so this means a
   guard failed or Steam Cloud replaced it) is moved into the backup folder as `<name>.changed-during-session` and
   the backup copy is put back; log an error with both paths. A file that appeared is moved there too; one that
   vanished is restored.
3. Empty the sandbox. Return; `OnSessionEnded` does the scene part (4.5).

Core keeps `SuppressDiskWrites` set if restore throws, and then refuses new sessions until the game restarts.

### 5.7 Mapping onto Core

| Core | Game |
|---|---|
| `CaptureSnapshot` | `CaptureBundle(table)` (5.1) |
| `ApplySnapshot` | `ApplyBundle` (5.2) + table replaced + `IFactHandler.OnFactsReplaced` |
| `BackupLocalSave` / `RestoreLocalSave` | 5.6 |
| `SuppressDiskWrites` | 5.5 (`OnSuppressDiskWritesChanged` turns redirect/skips on) |
| `LocalProgressChanged` / `ApplyDelta` | facts (section 6) |
| `SaveSyncHost.ResyncAll` | host scene settles (4.2), or Progression after a bulk change |
| `SaveSyncClient.RequestResync` | digest mismatch (6.13) |

---

## 6. Progression (host-authoritative)

General: install `IFactHandler`; register all `ngg.pg.*` RPCs; patch everything below with feature keys
`pg.<area>`. All appliers run only when `ctx.WorldReady` (otherwise the table already holds the value and the next
reconcile applies it). Every applier runs inside `ctx.Replay.Begin(...)` and updates `lastLive[key]`.

### 6.1 Diff loop and reconciliation

- **When:** postfix `SaveManager.SaveRun()` (the game calls it after nearly every change), postfix
  `Bank.ChangeBalance` (host), and a 1 s poll, while `ctx.WorldReady && !ctx.IsLoadingWorld`; on the host in
  Menu2/CREDITS from `m_run` only. Client additionally needs `ctx.IsReady`, `!ctx.Replay.IsReplaying` and
  `ctx.Replay.QuietSeconds >= 0.5`.
- **What:** for every fact source (2.3) compute the live value; if it differs from `lastLive[key]`, set
  `lastLive[key]` and `ctx.Save.ReportLocalFact(kind, key, value)`. Clients skip host-only kinds.
- **BuildFacts (host):** all facts from the live world (Main ready) or from `m_run` (Menu2/CREDITS), plus `seed`
  (kept for the session), `auth`, `money`, `unl`, `sc`, `rh`.
- **Baselines:** at `WorldLoaded` and in `OnFactsReplaced`, set `lastLive` from the live world *after* reconciling.
- **Reconcile (client):** after `WorldLoaded` (the injected run was applied by `SetUpScene`), after
  `OnFactsReplaced` when the world is ready, and every 10 s: for each fact whose live value differs and whose key
  had no local change in the last 5 s (a claim may be in flight), run its applier.
- **Host after SaveRun** is the "compact world state broadcast": the diff sends exactly the facts that changed.

### 6.2 Validation (host, `ValidateClaim`)

Reject (client resyncs) when: kind is host-only; the value is malformed; or the kind rule fails:
`track` 6.3, `bo` set exists via `GreatConnector.instance.GetChallengeSet(name)` with `isBounty` and flags only add,
`dnr`/`easy` set exists and only add, `rb` field in the shared list and false->true (`m_serverCut` both ways), `ri`
field in the list and value >= current, `obj` 6.9, `pht` value is a superset (the host stores the union). Accepted
claims also produce `ngg.pg.note` for toasts where 6.x says so.

### 6.3 Challenges: the shared main track

Only `ChallengeSystem.m_mainChallengeSet` is shared. Bounty sets and other sets loaded by tees
(`StandHere.m_loadChallengeSetOnEnter`) or dialogue are played locally; their persistent results come through `bo`,
`dnr`, `easy`, `rb` and world facts.

- **Live value:** set = `GameIds.ChallengeSetName(cs.m_mainChallengeSet)`, index = first `m_challenges[i]` with
  `!m_complete` (or the length). No main set -> no fact.
- **Claim:** the client finished the main challenge locally (its own ball, its own `CompleteChallenge`); the diff
  claims index + 1. Valid when the set equals the host's and the index is the host's + 1, or (set change) the host's
  index equals its set length and the claimed set exists and is not a bounty.
- **Apply (any peer):** let `local` be the peer's live index for the fact's set.
  - Same set, `fact == local + 1`, and the main set is the loaded one (`cs.m_challengeSet == cs.m_mainChallengeSet`):
    `cs.CompleteChallenge(cs.m_mainChallengeSet.m_challenges[local])` (one-arg, public; patch target
    `AccessTools.Method(typeof(ChallengeSystem), "CompleteChallenge", new[] { typeof(Challenge) })`), hold 1.0 s.
  - Same set, `fact > local + 1`, or a different set: `cs.LoadNewChallengeSet(GetChallengeSet(name), index)`.
  - `fact <= local`: nothing (this peer is ahead with a claim in flight).
  - Peer inside a bounty or another set (`m_challengeSet != m_mainChallengeSet` or `m_activeBountyPad != null`):
    defer; apply on postfix `ChallengeSystem.ExitBountyChallenge(bool)` or the next `LoadNewChallengeSet` that makes
    the main set current.
  - Toast on others: "<name> completed: <challenge m_shortString>" (from `ngg.pg.note`, kind 1).
- The replay runs the challenge's `functionOnComplete` / next `functionOnStart` on every peer, so those story
  methods are **not** also sent by name (6.10).

### 6.4 Bounties

Any player may run any bounty locally (`BountyPad.StartBounty` unchanged). Several players may run the same bounty;
the first completion counts.
- **Live:** `bo/<name>` for each `m_completedBounties` entry, flag 2 if also in `m_completedOnEasy`.
- **Apply on the host (accepted claim)** when newly added: add to `m_completedBounties` (and `m_completedOnEasy`
  for flag 2); pay once: `ProgressManager.instance.m_bank.ChangeBalance(BountyBalancer.instance.GetDifficultyValue(
  set.difficulty), false, false)` **(answered: 12.6)**; complete the pad (below);
  `BountyBalancer.instance.UpdateBountyBoards()`; `SaveManager.instance.SaveRun()`; note "<name> completed bounty
  <name>". The bank fact follows from the diff.
- **Apply on clients:** the same without money (the bank arrives as its own fact).
- **Pad:** the `BountyPad` whose `m_bountyChallengeSet` clean name matches -> `Complete(false)`, unless it is this
  peer's `MoveAndHitController.instance.m_activeBountyPad` (the player is in that bounty right now): then defer
  until postfix `ExitBountyChallenge`.
- **Tiers:** `ri/bountyAccessLevel` -> `SaveManager.instance.SetBountyPadStates(level)` (postfix of
  `SetBountyPadStates` feeds the diff).
- The claimant's own `CompleteChallenge` already paid locally; that local balance is overwritten by the host's bank
  fact, so the reward is counted once. `BountyPad.CheckAutoComplete` chains (private) complete further bounties
  locally and become further `bo` claims.
- *As built:* "playing it" also covers a bounty set loaded without a pad and the LMUGC round, the host keeps such a
  waiting entry across scene loads and the session end, and other players' pre-hit targets and this player's own
  bounty-start object changes wait the same way (12.7).

### 6.5 Unlocks and features (`rb`, `ri`)

Shared bools and their appliers (set the RunSave field in place first, then the applier):

| Field | Applier |
|---|---|
| `m_stancePanelUnlocked` | `ProgressManager.instance.UnlockStance(false)` |
| `m_practiceUnlocked` | `ProgressManager.instance.UnlockPractice(false)` |
| `m_aimUnlocked` | `ProgressManager.instance.UnlockAdjust(false)` **(answered: 12.6)** |
| `m_spinUnlocked` | `PanelManager.instance.m_spinPanel.Unlock(false)` |
| `m_windUnlocked` | `PanelManager.instance.m_windPanel.StartWind(false)` |
| `m_ironUnlocked` | `m_clubPanel.Unlock(false)` through the field (never name its type) **(answered: 12.6)** |
| `m_putterUnlocked`, `m_driverUnlocked`, `m_wedgeUnlocked`, `m_hybridUnlocked` | `ProgressManager.instance.UnlockClub(club)`, club from `Resources.FindObjectsOfTypeAll<Club>()` by `clubName` ("Putter", "Driver", "Wedge", "Hybrid") |
| `m_cardUnlocked` | `ProgressManager.instance.m_objectStates.CheckRFIDDoors()` |
| `m_serverCut` | true: `ObjectStates.ServerCut()`, false: `ServerRestore()` |
| `m_bunkerOpen` | `ObjectStates.m_bunkerTerminal.SetScreenOpen()` |
| `m_canGolf`, `m_golfUnlocked`, `m_crowbarUnlocked`, `m_ticketUnlocked`, `m_bountiesRestored`, `m_hitLuke`, `m_hasReturnedAfterHittingLuke`, `m_playTrueEnding`, `m_seenTrueEnding`, `m_ballAwarded`, `m_foundFirstCash`, `m_playedMulliganVideo`, `m_playedNoWindVideo`, `m_playedHoleSkipVideo`, `m_playedCar2`, `seenBulletproof`, `finishedDemo`, `completedBountyOnEasy` | field only |

Appliers call the game's unlock with `showPFX = false`; if that call itself re-sets the field, fine. Ints: `ri/
bountyAccessLevel` (6.4), `ri/lmugcStart*` (field only), `rh/lmugcScoreBest`, `rh/lmugcScoreNoAssistBest` (field,
then `PanelManager.instance.m_LMUGCPanel.UpdateBestScores()`). Toast "Unlocked: <feature>" on others.

### 6.6 Bank: one shared purse

The purse is the host's `m_bank.m_balance`, synced absolutely as `bank`. Clients never report it and never forward
`ChangeBalance` calls, with one exception: money changes during a granted purchase/pickup (6.7). Every other
balance change on a client (its local bounty reward, a local story effect) is local only and is overwritten by the
next host value. Applier (clients): `bank.m_balance = v; m_run.m_bankBalance = v; bank.UpdateVisuals();`.
`ProgressManager.Start` setting 99990 before the load is harmless (the injected run's `m_bankBalance` wins in
`SetUpScene`, then the fact).

### 6.7 Interactions: `FPSInteractable.Interact()` prefix

Identify the interactable with `GameIds.ForObject(__instance)`; switch on `__instance.m_interaction`:

| InteractionType | Handling |
|---|---|
| ContinueShot, StartBounty, Teleport, ElevatorDoorUpper, ElevatorDoorLower, ElevatorDown, ElevatorUp, StartLMUGC, TalkToLuke | local, unchanged |
| Padlock, RFID, CutServer, OpenBunker | local, unchanged; their world effects replicate as world ops (6.11) and facts |
| CollectCash, FindMissingBall, GetCrowbar, GetCard, LMUGCTicket | **arbitrated pickup** |
| BuyClub, BuyMulligan, BuyNoWind, BuyHoleSkip | **arbitrated purchase** (also takes the host's purchase lock) |

Arbitration (only in session; replays pass through):
1. **Client prefix**, no grant for this id: send `ngg.pg.claim {claimId, id, type}` once (pending), skip the
   original. Pending for this id: skip. Grant present: consume it, open the grant scope for `claimId`, run the
   original.
2. **Host handler:** resolve with `GameIds.Resolve<FPSInteractable>(id)`; refuse when missing or inactive, when the
   id is in `taken`, or (purchase) when the purchase lock is held by someone else or, if the host's own
   `m_cashValue > 0`, when `balance < m_cashValue` **(answered: 12.6)**. Otherwise add to `taken[id] = peer`
   (pickups keep it; purchases free it when the bank report arrives or after 5 s), take the purchase lock for
   purchases, reply `ngg.pg.grant {claimId, true, balance, ""}`. Refusal: `{claimId, false, balance, reason}` ->
   toast "Already taken by <name>" / "Not enough money".
3. **Client on grant:** set the local bank to `hostBalance` (6.6 applier), then `ctx.Post(() => interactable.Interact())`.
   The prefix sees the grant and runs the game's own interaction (its affordability check now uses the host's
   balance).
4. **Grant scope (client):** from the granted `Interact` until 3 s after it returns, postfix `Bank.ChangeBalance`
   sends `ngg.pg.bank {claimId, change}`; balance changes are not otherwise forwarded.
5. **Host bank handler:** claim granted to that peer, at most 4 reports per claim; pickups: positive only, and for
   CollectCash the host uses its own interactable's `m_cashValue` instead of the client's number; FindMissingBall
   0 < change <= 10000; purchases: negative only, |change| <= 1,000,000 and the result may not go below 0.
   Apply `ChangeBalance(change, false, true)` inside a replay scope; release the purchase lock.
6. **Host's own interactions** go through the same `taken` / purchase-lock checks in its prefix (skip + toast when
   a client got there first).
7. The object's disappearance, the unlock flags and the shop state arrive as the claimant's facts (`obj`, `rb`,
   `ri`); `obj` deactivation of an arbitrated object is only accepted from the peer it was granted to.
8. Pending claims time out after 5 s (toast "The host did not answer"). `PlayerLeft` frees that peer's purchase
   lock; its pickups stay taken.

### 6.8 Shop stock

`DialogueManager` `DriverAvailable/WedgeAvailable/HybridAvailable/TicketAvailable` call
`ObjectStates.SetDriverAvailable()` etc.; those `ObjectStates` methods and `EnableLMUGCMachine` are world ops 7..11
(replayed by name on others), and the resulting standee objects are SaveableObjects (`obj` facts) **(answered: 12.6)**.

### 6.9 SaveableObjects (`obj`)

Cash pickups, padlocks, dialogue triggers, shop items, doors with `m_saveRotation`: about 209 objects, keyed by
`m_id`.
- **Live:** `GetSaveData()` -> `m_active`, and `m_rotation` when `m_saveRotation`.
- **Apply:** `gameObject.SetActive(active)`; rotation: set `transform.rotation`, read `GetSaveData()` back, and if it
  differs by more than 1 degree switch this object (and remember the choice for all) to `localRotation`
  **(answered: 12.6)**.
- **Validate:** deactivating an arbitrated interactable's object (it or a child has an `FPSInteractable` of an
  arbitrated type) only from the peer `taken` names; reactivating one only from the host; everything else accepted.
- A dialogue trigger consumed by one player is consumed for all (story is shared); that player's dialogue effects
  reach the others as story methods and facts.

### 6.10 Dialogue and story methods

`DialogueLine.invokeStart/invokeEnd` and `Challenge.functionOnStart/functionOnComplete` call private parameterless
`DialogueManager` methods by name through `Invoke`. Patch each listed method individually (missing name -> only that
method's feature is off); get the name from `__originalMethod.Name`.

| Class | Methods | Handling |
|---|---|---|
| Replicate | `UnlockBounties1-3`, `OpenShop`, `OpenHole1Gates`, `OpenHole2Gates`, `DriverAvailable`, `WedgeAvailable`, `HybridAvailable`, `TicketAvailable`, `UnlockLMUGC`, `OpenBarDoor`, `OpenMarketingDept`, `RestoreBounties`, `CompletedDemo`, `CloseGates`, `OpenGates`, `UnlockStance`, `UnlockPractice`, `UnlockWind`, `UnlockSpin`, `UnlockAdjust`, `Luke1-16`, `EndLuke0-13`, and every `DialogueManager` method whose name starts with `ReturnLuke` (found by reflection) **(answered: 12.6)** | originator postfix sends `ngg.pg.story {name}` to Others unless replaying or challenge-driven (below); receivers call the method (AccessTools, synchronous) in a replay scope, hold 1.0 s |
| Host-executed | `DeductMoney`, `ReturnMoney` | client prefix: skip, send `ngg.pg.storyHost {name}`; host (own call or request): run only if `money/story` allows (Deduct when state != 1, Return when state == 1), then set the state fact; the bank fact carries the money |
| Local | `TeleportMD`, `TeleportCarpark`, `LockPlayer`, `PlayNoTextDialogue`, `PadlockSequence`, `OpenSequence`, `ServerSequence`, `BunkerSequence`, `FinalDoorSequence`, `StartDialogue`, `NextButtonPressed`, any other name | unchanged on the peer that has the dialogue; world sub-effects replicate as world ops and facts |
| Host-only ending | `TryTrueEnding` | clients: skip + toast "Only the host can start the ending" (6.14) |

- **Challenge-driven calls are not sent:** if, when a replicate-class method runs, `cs.m_challengeSet ==
  cs.m_mainChallengeSet` and its name equals the `functionOnStart` or `functionOnComplete` of any challenge in the
  main set, it came from the shared track, which every peer replays itself (6.3).
- **No double world ops:** while a replicate-class method runs on the originator (prefix..postfix), world-op sends
  are suppressed: the receivers' replay of the method reproduces them.
- Dialogue itself (lines, video, `m_moveLocked`) stays with the player who triggered it. A remote dialogue never
  locks anyone (`WriteSave` waits on `m_moveLocked`, so never hold it for others).

### 6.11 Doors, gates, server, bunker, elevator

World ops (2.4): originator postfix on the listed `ObjectStates`/`BunkerTerminal` methods sends when in session,
world ready, not replaying and not inside a replicate-class story method; receivers replay the same call in a
replay scope (hold 0.5 s). These methods call `SaveRun`: on the host that persists the change, on clients it is a
no-op write. Persistence and late joiners come from `obj` facts (door rotations, padlocks) and `rb` (`m_serverCut`,
`m_bunkerOpen`, `m_bountiesRestored`). The final door (`FinalDoorSequence`, `m_bountiesRestored`) and the RFID
doors (`CheckRFIDDoors` after `m_cardUnlocked`) are covered the same way. Elevator rides, elevator doors and
teleporters stay local (they move only the local player; `m_bunker` is per-view).

### 6.12 Achievements

Prefix `SteamManager.UnlockAchievement(AchievementID)`: skip when `ctx.Replay.IsReplaying`, or on a client while
`ctx.IsLoadingWorld`. Prefix-skip `SaveManager.AchievementsRetroUnlock()` on clients while the session save is
active (it would grant the client the host's progress). Everything a player does themselves unlocks normally
on their own Steam account. Remote avatars and ghost balls never touch `AchievementTrigger` (no colliders, other
names).

### 6.13 Seeded puzzles, digest

- **Authenticator** (BO_Code): host postfix `Authenticator.Start` and `BuildFacts` report `auth/<id>` = private
  `code`. Clients: postfix `Authenticator.Start` and the applier set `code` and `m_text.text` (Traverse) to the host's
  code **(answered: 12.6)**. `InputBoard.AddInput` stays local (completes the client's own challenge).
- **TriviaBoard:** prefix private `ShuffleButtons` in session: save `UnityEngine.Random.state`,
  `Random.InitState(seed ^ (int)Crc32(id))`; postfix restores the state. Every peer gets the same order. On host
  session start, call `ShuffleButtons` once on active boards so the host matches **(answered: 12.6)**.
- **Cosmetic unlock:** `SaveManager.UnlockNewBall()` prefix on clients: skip (`__result = false`), send
  `ngg.pg.ballUnlock`; the host calls `UnlockNewBall()` (random on the host, saved to the host's unlocks.txt) and the
  `unl/balls` fact follows (client applier: `m_unlocks.unlockedBalls`, `showNewBallAlert = true` when a ball was
  added).
- **Digest:** host every 10 s `ngg.pg.digest {SaveHost.Revision, Facts.ComputeCrc(), Facts.Count}`. A client whose
  `SaveClient.Revision` equals it and whose table CRC differs calls `ctx.Net.SaveClient.RequestResync()` (at most
  once per minute).

### 6.14 Endings and CREDITS

Endings change the scene for everyone, so only the host triggers them. Clients: prefix-skip private
`LukeVideo.OnTriggerEnter(Collider)`, `TeleportManager.FadeDownEnding()` and `DialogueManager.TryTrueEnding()`, with
a toast. When the host's game loads CREDITS, clients that are in Main follow (4.3). Ending flags (`m_hitLuke`,
`m_playTrueEnding`, `m_seenTrueEnding`) are facts.

### 6.15 Cutscenes with several players (as built: `Progression/Cutscenes.cs`, `Logic/CutsceneRules.cs`)

A dialogue or cutscene (`StartDialogue`, `PlayNoTextDialogue` with `PadlockSequence`, `ServerSequence`,
`BunkerSequence`, `FinalDoorSequence`, Luke's clips, `LookAt*`, `TeleportMD` / `TeleportCarpark`, the elevator ride)
plays only for the player who triggered it; `m_moveLocked` is only theirs. The other players get the world changes
(world ops, facts) at once, see the avatar stand still with the HUD activity "In a cutscene", and are never frozen,
teleported or shown that cutscene. A cutscene never pauses the session: the mod ticks on real time, the pause
controller (7.6) keeps `timeScale` at 1, and the host's save write that waits on `m_moveLocked` is the host's own.

- **Hold (`pg.cutscene.hold`).** While the local player is in a dialogue / cutscene of their own (`m_moveLocked`) or
  playing a shot (`Ball.m_ballHit` and not `m_currentShotData.m_shotComplete`), and for 1.5 s after
  (`LocalBusyGate`), incoming replicated story beats (`ngg.pg.story`) are queued (each name once in the order of its latest arrival, since
  OpenGates / CloseGates do not commute; at most 64; a beat arriving while older ones still wait queues behind them) and the
  shared track's catch-up (`CompleteChallenge` / `LoadNewChallengeSet`, Complete or Load in 6.3) is deferred like the
  bounty case; both run when the player is free (scene loads drop the queue; the facts carry the state). A held
  client completion still gets its toast on the host at once. Bounded: 120 s for a dialogue (it waits for the
  player's button), 30 s for a ball that never reports its rest. World ops, bounties, unlocks and objects are not
  held: they only change the world.
- **Guard (`pg.cutscene.guard.*`).** `DialogueManager.StartDialogue` (both overloads), `PlayNoTextDialogue` and
  `LockPlayer(true)` are skipped when the network causes them here: inside a replay scope in a session, or inside the
  call of a challenge function marked by a track replay (below; a depth counter, prefix + finalizer, so the player's
  own coroutines resuming later in that frame are not caught). `LockPlayer(false)` is never skipped: a lost unlock
  freezes the player and the game's save that waits on `m_moveLocked`.
- **Replayed challenge functions (`pg.cutscene.fn.*`).** Before a track replay completes a challenge or activates the
  next one, its `functionOnComplete` / `functionOnStart` is marked for 1.5 s (plus `functionOnStartDelay`, at most
  30 s; one mark per Invoke, so a catch-up of several challenges naming the same method covers each call) and `DialogueManager.<name>()` is wrapped once. A marked call of a player mover (`TeleportCarpark`,
  `TeleportMD`, `TeleportMDTrap`, `StartTeleporterDialogue`, `PlayCarDialogue2`) or a presentation clip (`Luke*`,
  `EndLuke*`, `ReturnLuke*`, `LookAt*`) is skipped (toast for the movers); any other runs, with the dialogues and
  player locks it starts skipped. Only parameterless void methods are wrapped (others are logged once). The player who made the progress gets all of them from their own game.
- Endings stay the host's (6.14): clients in Main follow into CREDITS; a client hitting Luke gets the toast only.

---

## 7. Presence

Install `IPresenceProvider` (`ctx.SetPresenceProvider`). Everything here is transient.

### 7.1 Local poses

- **Player** (`TryGetLocalPlayerPose`): false unless `ctx.Scene == Main && ctx.WorldReady` and
  `MoveAndHitController.instance` exists. `m_mode == Walk`: position `m_fpsCharacter.GetPosition()`, yaw-only rotation
  from `GetRotation()`, velocity `GetVelocity()` (`HasVelocity = true`), flags `PlayerCrouched` (`IsCrouched()`),
  `PlayerAirborne` (`!IsOnGround()`). `Golf`: `m_golferHolder.position/rotation`, flag `PlayerGolf`.
- **Ball** (`TryGetLocalBallPose`): `HitManager.instance.m_ball`; false when null or its GameObject is inactive;
  `Hidden` from postfix `Ball.KillBall()` until postfix `HitManager.ResetBall()` / `HitSequeneceManager.Restart(bool)`;
  velocity `Compat.GetVelocity(m_rb)` with `HasVelocity` when `!m_rb.isKinematic`, flag `BallMoving` then.
- **Info:** send `ngg.pr.info` on change of scene, mode, club (`m_selectedClub.clubName`) or ball skin
  (`SaveManager.instance.m_unlocks.selectedBall`), and every 3 s. `activity`: "In a cutscene" while
  `m_moveLocked`, "Bounty <name>" when `m_activeBountyPad`, "LMUGC hole n" in a round, else "".

### 7.2 Avatars (`CreateRemotePlayerVisual`)

Mod-made from primitives: body capsule (1.8 m), head sphere, club cylinder. Remove every collider with
`Object.DestroyImmediate` right after `GameObject.CreatePrimitive`; layer 2 ("Ignore Raycast") on every object;
child names "Body", "Head", "Club" (never "Ball" or "First Person Character"); no Rigidbody. Material: copy of
`HitManager.instance.m_ball.m_meshRenderer.sharedMaterial` (a pipeline-correct lit material) tinted with the
player color (set both `color` and `_BaseColor` if present). Add an `IRemoteVisual` component: walk pose upright
with the club hidden; `PlayerGolf`: club shown, body leaned into the stance; `PlayerCrouched`: body scaled to
0.6 height. The name tag comes from `RemotePlayerView`.

### 7.3 Ghost balls (`CreateRemoteBallVisual`)

Sphere (0.0427 m), collider removed with `DestroyImmediate`, layer 2, child name "GhostBall", no `Ball`
component, no Rigidbody. Material = `Cosmetics.instance.m_balls[i].mat` with `i` = the owner's
`selectedBall` from `ngg.pr.info` (default material until the first info; swap when it changes; out-of-range ->
default). `IRemoteVisual`: hide the mesh while `BallHoled`. Interpolation and teleport snapping come from Core.
A ghost can never enter `ChallengeSystem.m_ballsInPlay` (it has no `Ball`) and never touches a trigger.

*Trail* (`[Presence] RemoteBallTrail`, default on; feature `pr.trail`): a `TrailRenderer` on a child "GhostTrail" of the
ghost (layer 2, no collider, no Rigidbody), so it follows the interpolated pose. Look: a copy of the game ball's own
public `Ball.m_trail` (else `m_chasingTrail`, answers #6): material, width curve and multiplier, alpha keys, cap/corner
vertices, alignment, texture mode, time. Until a world ball is readable, the mod's look: the first supported of
`Universal Render Pipeline/Particles/Unlit`, `Sprites/Default`, `Universal Render Pipeline/Unlit`,
`Legacy Shaders/Particles/Alpha Blended`, `Unlit/Color` (URP ones set to alpha blending), else a copy of the ball's
lit material; never a null material (the trail is not drawn until it has one). Colour = the owner's player colour
(the avatar's and name tag's), through the material's `_BaseColor` / `_TintColor` / `_Color` when it has one, else
through the vertex colours. Time = `[Presence] TrailSeconds` when > 0, else the game trail's time, else 1.5 s
(clamped 0.1..10). `IRemoteVisual.OnRemotePose` gets the pose's teleport flag; the trail is **cleared** on the first
pose after the ghost was hidden or created, on the teleport flag's rising edge, when the ball starts moving
(`BallMoving` or > 0.5 m/s: a new shot), when it comes back from `BallHoled`, and on a step longer than 5 m and
faster than 120 m/s (a lost teleport). It **emits** only while moving and not holed; at rest it fades out over its
time. A trail failure is reported on `pr.trail` and never stops the ghost's poses.

### 7.4 Shot events, notifications, HUD

- Postfix `HitManager.HitBall(float angle, float power, bool isMiss, ShotType shot)` (`__1` power, `__2` isMiss,
  `__3` shot), local ball only, not replaying: `ngg.pr.shot {club, power, shot, HitManager.instance.GetSpinType(),
  stroke = m_ball.m_currentShot + 1, isMiss, from}` **(answered: 12.6)**.
- Postfix `HitSequeneceManager.CompleteSequence(Ball)` when `__0 == HitManager.instance.m_ball`: `ngg.pr.rest {pos,
  m_currentShotData.m_finishedDistance, m_lastTouchedSurface, stroke}`.
- Postfix `HitSequeneceManager.ShowBallInHoleFeedback(int par, bool showAnyway)`: `ngg.pr.holed {par, strokes,
  lmugcHole}`.
- Receivers: toast (at most one per player per 2 s, holes always): "Alice: Driver, Perfect (82%)", "Alice's ball
  stopped after 143 m", "Alice holed out in 3 (par 4)".
- HUD (IMGUI, top-right, in Main while in session): one row per player: name (host marked), Walking/Golfing,
  activity, last stroke count, ping. Hidden while the pause menu is open.

### 7.5 Wind (host-authoritative)

- Host: postfix `WindPanel.LateUpdate`; every 0.1 s `ngg.pr.wind` unreliable `{m_currentWind, m_desiredWind (private,
  FieldRef), hostFree}`, reliable when `m_desiredWind` changed by more than 0.01. `hostFree` = the host's own wind is
  not forced (`ChallengeSystem.instance.GetCurrentActiveChallenge()?.m_forceWind != true`).
- Client: `HostWind` = in session, host wind received in the last 3 s, `hostFree`, and the local wind not forced
  (`GetCurrentActiveChallenge()?.m_forceWind != true`, LMUGC no-wind assist not running:
  `m_run.lmugcNoWindTime <= 0`). While `HostWind`: prefix-skip `WindPanel.NewWind()` and `WindPanel.WindCycle()`
  (`__result` = empty `IEnumerator`); postfix `LateUpdate`: `m_desiredWind = host desired`, `m_currentWind` eased
  toward host current (rate 10/s; snap when the gap exceeds 50% of the speed). Otherwise the game's own wind runs.

### 7.6 Pause without freezing time

*As built:* one controller only, Lifecycle's `PauseTime` (`lc.pause.*`); it also tracks the menu offline and
publishes `GameContext.PauseMenuOpen`, which the Presence HUD reads. Two controllers that each remember and restore
`Behaviour.enabled` could leave input disabled, so Presence has none (see 12.3).

- Postfix `PlayerPressedEscape.OpenMenu()` in session: `Time.timeScale = 1`; disable local input while the menu is
  open: `MoveAndHitController.instance` private `m_fpsInput` (`FirstPersonCharacterInput`), the
  `FirstPersonCharacterLookInput` on `m_FPSController`, and `PanelManager.instance.m_clubSwingPanel` (`ClubSwing`)
  `.enabled = false`, remembering their previous state.
- Restore on postfix `PauseMenu.Exit()`, on `PauseMenu.QuitToMenu()`, on any scene load, or when no active
  `PauseMenu` is found (polled per frame while paused).
- `ctx.LateTick` while in session: if `Time.timeScale == 0` set it to 1 (warn once per source: settings menu,
  screenshotter). Private `HitSequeneceManager.RestartInput` bails on timeScale 0, so this also keeps shots going.

### 7.7 LMUGC and JustGolf rounds

Each player plays their own ball through the holes at their own pace (the game is single-ball); the host's own
round is what the host save records (the game's own code); clients' rounds are session-only.
- Postfix `LMUGC.StartChallenge()` -> new random `roundId`, `ngg.pr.round {roundId, 0, 0}`.
- Postfix `LMUGC.CompleteHole(string id, int score)` -> `ngg.pr.hole {roundId, hole, score, par}`: hole = last number
  in `id` **(answered: 12.6)**, par from private `LMUGC.m_pars[hole - 1]`.
- Postfix `FinishChallenge()` / `CompleteNormalGolfRound()` -> `{roundId, 1, total}`; `ExitChallenge()` -> `{roundId, 2,
  total}`.
- The host keeps every player's latest round and sends `ngg.pr.card` on `PeerReady`; everyone shows a shared
  scorecard (IMGUI): rows players, columns holes 1..9 with par, total and +/-; open while Tab is held, and for 6 s
  after any hole or round event.
- Assists: buying is a purchase (6.7, `ri/lmugcStart*`); using a mulligan/no-wind/skip is local to the round.
- JustGolf: `AddScoreToCard` runs for the host only (Lifecycle skips it on clients); the host scorecard reaches
  clients as `sc`.

---

## 8. Moving and physics world objects (Progression owns the decision, Presence the ghosts)

| Object | Decision | Why |
|---|---|---|
| `Breakable` | local only | PhysX is not reproducible and shards are random; breaking is evaluated by the shooter's own shot. Persistent "already hit" targets are the `pht` fact. |
| `PhysicsChallengeObject` | local only | enabled per active challenge (bounties are per player); main-track ones are enabled on every peer by the track replay and simulated locally |
| `TargetAnimator` (DOTween, random delays) | local only | cosmetic; target hits are judged by the local shooter |
| `BallGlass`, `BallHitButton`, gongs | local only | react to the local ball; persistent consequences (doors, flags) replicate as world ops / facts |
| Doors, gates, sliding doors, server, bunker screen | synced | world ops for the animation, facts for persistence |
| Cash, crowbar, card, ticket, missing ball, shop items | synced, host-arbitrated | exclusive; first claim wins |
| Elevator, teleporters | local only | move only the local player |
| Ghost balls, avatars | display only | kinematic, no collider, never part of the game's simulation |

---

## 9. Edge cases

| Situation | Behavior |
|---|---|
| Client joins mid-dialogue (`m_moveLocked`) | no flush; host save loads; the dialogue is abandoned with the client's own world |
| Two clients collect the same cash | first claim granted, second refused with a toast |
| Two purchases at once | the purchase lock serializes them; the second sees the updated balance |
| Client completes the main challenge while the host is in a bounty | claim accepted, host defers its replay until it leaves the bounty; other idle clients replay at once |
| Host and client finish the same bounty | first `bo` wins, the money is paid once by the host |
| Fact arrives while loading | stored in the table; applied by the reconcile after `WorldLoaded` |
| Client's claim refused | Core resyncs; same epoch, so no reload; reconcile undoes the local change |
| Host reloads Main (broken save) | resync without epoch change; clients reconcile in place |
| Host quits to Menu2 | clients go to Menu2 and wait; the session stays open |
| Host switches to JustGolf | epoch and mode change; clients load Main in JustGolf, fresh rounds |
| Host triggers an ending | clients in Main follow into CREDITS; later joiners wait in Menu2 |
| Client tries to start a game in Menu2 | blocked with a toast |
| Client quits to desktop | leave, restore with the fence up, nothing written |
| Client crashes | its files were never written; the sandbox is wiped on next start |
| Restore finds a changed save file | changed file kept in the backup folder, own file put back, error logged |
| Save protection patch missing (game update) | that client refuses to join with a message; hosting still works |
| `SetUpScene` hook missing | world ready 1 s after Main loads (fallback) |
| Host leaves while a client is in CREDITS | client restores and goes to Menu2 after |
| Player leaves | avatar/ghost despawned by Core; its purchase lock released; scorecard row kept until session end |
| Remote dialogue trigger already consumed | the `obj` fact deactivates it for everyone; nobody else gets that dialogue |
| Story beat or track progress arrives while the local player is in a dialogue / cutscene or mid-shot | held; applied 1.5 s after it ends (6.15) |
| Track replay would teleport this player or start a dialogue for them | skipped; only the player who made the progress gets it (6.15) |
| Host in a cutscene when a client completes the main challenge | claim accepted and broadcast at once; the host's own game catches up after the cutscene |

---

## 10. Test plan (on the PC with the real game)

1. Offline: the game plays and saves exactly as without the mod (diff the save files after a short session).
2. Host in Menu2, client joins from Menu2: waits; host plays Story: client loads Main at the host, sees the
   host's unlocks, bank, doors.
3. Client joins while the host is in Main mid-world; then joins from its own Main (own progress flushed first).
4. Client completes the current main challenge: everyone advances; story effects appear on all.
5. Client completes a bounty: pad completes everywhere, money paid once, board updated.
6. Cash pickup race; simultaneous club purchases; LMUGC assist purchase.
7. Padlock/gate/door/RFID/server/bunker opened by a client: animates on others; host save has it after restart.
8. Leave mid-game: client back in Menu2 with its own save; its files byte-identical to before (compare CRCs).
9. Host quits to Menu2 and switches to JustGolf; clients follow; LMUGC rounds and shared scorecard.
10. Pause in session: time keeps running for everyone; no input leaks into the game.
11. Wind identical on host and clients (watch both panels); forced-wind bounty keeps its own wind.
12. Host ending -> CREDITS for clients in Main.
13. Kill Steam on the host: clients restore and return to Menu2.

---

## 11. Needs confirmation (check against the decompiled game)

1. `LoadRunSave` / `LoadStoryRun` / `LoadNormalGolfRun`: do they assign `m_run` themselves or does the caller?
2. File format of run2.txt / actuallyNormal.txt (plain JsonUtility JSON, or `CompressSave` / `EncodeDecode`)?
   Matters for the sandbox copy that `SaveReader` reads in CREDITS.
3. Does the game write save files anywhere besides `WriteSave`, `SafeWrite`, `SaveUnlocks`, `AddScoreToCard`,
   `Delete*`, `CreateBackupOfSave`, `UpgradeDemoSave` (other classes, quit handlers, `SaveReader`)?
4. `SetUpScene`: only in "Main"? Which rotation space does it apply to `m_savedObjects`, and does it use
   `gameObject.SetActive`?
5. `MainMenu.Start` calls `LoadRunSave` (and forces Story); what `PlayGame` does for a new game (`DeleteRun`,
   `DifficultySelect`).
6. `PauseMenu.RestartFromHole1`: does it reload "Main"?
7. Price of `BuyClub` / `Buy*` interactions: in `FPSInteractable.m_cashValue`?
8. Bounty reward on easy mode (`completedOnEasy`): the same `GetDifficultyValue(difficulty)`?
9. `rb` applier mapping: `m_aimUnlocked` <-> `UnlockAdjust`; the club panel (`m_clubPanel.Unlock`) <-> `m_ironUnlocked`.
10. `Luke1..16`, `EndLuke0..13`, `ReturnLuke*`: world state (NPC placement) or presentation only?
11. `ChallengeSystem.m_mainChallengeSet` keeps its challenges' `m_complete` while a bounty set is loaded;
    `LoadNewChallengeSet(set, skipToChallenge)` marks earlier challenges complete.
12. `CompleteChallenge` on a bounty's final challenge calls `BountyPad.Complete(true)` on `m_activeBountyPad`.
13. Shop standees (`m_driver`, `m_driverStandee`, ...) are SaveableObjects.
14. `Authenticator.m_text` shows the code; `InputBoard.AddInput` compares against `Authenticator.code`.
15. `TriviaBoard.ShuffleButtons` uses only `UnityEngine.Random`.
16. `WindPanel.WindCycle` start sites and what `m_desiredWind` drives.
17. `LMUGC.CompleteHole` id format; `Ball.m_currentShot` base (0 or 1).
18. Anything else that sets `Time.timeScale = 0`.
19. `UnlockNewBall` callers and the meaning of its return value.
20. `GameMode` order (Story = 0, JustGolf = 1) and `ControlMode` order (Golf, Walk).
21. Whether the game saves on application quit.

---

## 12. Integration (as built)

### 12.1 Wiring

`Plugin.Awake`: `CreateGameAdapter` (`NggGameAdapter`, or `NullGameAdapter` when the game's assemblies do not bind)
-> `CreateServiceOrFallback` -> persistent object with `NetRunner` and `LobbyWindow` -> `AttachGameAdapter`, which
calls `NggGameAdapter.Attach(service, harmony, host, showLobbyWindow, lobbyKeyText)` and hands the window
`NggGameAdapter.LobbyInfo` (`ILobbyGameInfo`). `Attach` installs the shared `world.ready` hook
(`SaveManager.SetUpScene`) and then Lifecycle, Presence, Progression in that order. With `NullGameAdapter` the
window shows Core state only.

Lobby window (UI/LobbyWindow): host / invite / join / leave; while syncing a progress bar
(`SaveSyncClient.SnapshotProgress`); in session the game's status line (host's scene and mode, from Lifecycle),
each player's whereabouts (from Presence: walking / golfing, activity, stroke, or "In menu" / "Credits" /
"joining"), and a warning when this game cannot join (save protection incomplete, own save unverified after a
failed restore) or game features are disabled. It opens with `ToggleWindowKey`, the Menu2 "Multiplayer" button and
the pause-menu "Multiplayer" button (offline too, so a running game can host or join).

Test harness (docs/TESTING.md): after `AttachGameAdapter`, `Plugin.InstallTestHarness` calls
`Testing.TestHarness.Install`, which binds `[Debug] EnableTestHarness` (default false) and `TestBotLatencyMs`; only when
enabled it adds `TestHarnessRunner` (command-file poller and bot ticks). `SteamMultiplayerService.TryStartBackend`
builds its `NetSession` on `TestHarness.WrapTransport(_transport)`: the Steam transport itself while the harness is
off, a `CompositeTransport` (bots on an in-process loopback hub, everyone else on Steam) when it is on.

### 12.2 Shared context additions

| Member | Set by | Read by |
|---|---|---|
| `GameContext.WorldReplacePending` | Lifecycle: at `ApplyBundle` when `FollowHost` will load, and on every load it starts; cleared on scene load, session end, a follow decision without a load, or a load timeout | Progression: no reconcile and no fact appliers on a world about to be replaced |
| `GameContext.PauseMenuOpen` | Lifecycle `PauseTime` (any role) | Presence HUD (hidden while open) |
| `GameContext.LobbyWindowKey` | Plugin (`ToggleWindowKey` text) | Lifecycle pause-menu button |

### 12.3 Cross-module rules checked at integration

- **One patch owner per behavior.** `PlayerPressedEscape.OpenMenu`, `PauseMenu.Exit` are patched by Lifecycle
  only; `PauseMenu.QuitToMenu` has one prefix (Lifecycle: leave). Other shared targets carry postfixes only
  (`SaveManager.SaveRun`: Progression diff; `SaveManager.SetUpScene`: the shared world hook); no method has two
  prefixes that can skip it.
- **RPC names** are exactly the 2.2 catalog, each with its module's prefix and a distinct FNV-1a id
  (`GameRpcCatalogTests`). No raw message types 80..127 are registered.
- **ReplayGuard.** Every network-caused game call runs in `ctx.Replay.Begin` (Progression appliers, world ops, story
  methods, grants, ball unlocks); Presence suppresses shot/rest/holed sends while `IsReplaying` and round/hole sends
  inside an open scope; Lifecycle makes no replayed game calls (scene loads reset the guard).
- **Ordering.** Host session start: `RebuildFacts` -> modules' `OnSessionReady(true)`. Host settle: Progression
  rebaselines on `WorldLoaded`; Lifecycle's settle is posted, then `RebuildFacts` + `ResyncAll`. Client: the bundle
  is stored before `FollowHost` is posted, so the injection prefixes see it when Main's `SaveManager.Start` runs;
  Progression reconciles 1.25 s after `WorldLoaded`; Core spawns player entities on `PeerReady` and remote views
  are visible only in a ready Main.

### 12.4 Feature keys

A missing member disables only its key (one log line); `lc.save.protect` / `lc.save.inject` missing makes the
client refuse to join.

- Shared: `world.ready`.
- Lifecycle: `lc.save.protect`, `lc.save.inject`, `lc.capture.bank`, `lc.menu.start`, `lc.menu.block.<Method>`
  (PlayGame, PlaySetUp, PlayNineButtonPressed, PlayNine), `lc.pause.quit`, `lc.pause.menu`, `lc.pause.menu.exit`,
  `lc.pause.input`, `lc.pause.input.fps`, `lc.pause.input.swing`.
- Presence: `pr.pose`, `pr.pose.flags`, `pr.ball`, `pr.ball.state`, `pr.ball.body`, `pr.info`, `pr.avatar`,
  `pr.skin`, `pr.trail`, `pr.shot`, `pr.rest`, `pr.holed`, `pr.wind`, `pr.round`, `pr.round.pars`, `pr.hud`.
- Progression: `pg.track`, `pg.track.exitBounty`, `pg.track.loadSet`, `pg.track.streak`, `pg.track.active`,
  `pg.track.deleteRun`, `pg.bounty`, `pg.bounty.pay`, `pg.bounty.boards`, `pg.bounty.tiers`, `pg.bounty.refund`,
  `pg.bounty.quit`, `pg.bank`, `pg.bank.hook`, `pg.objectStates`, `pg.challenge.text`, `pg.unlock.clubPanel`, `pg.unlock.spin`,
  `pg.elevator`, `pg.lmugc.set`, `pg.facts.saveRun`, `pg.interact`, `pg.world.gates`, `pg.world.door`,
  `pg.world.sliding`, `pg.world.bunker`, `pg.world.<method>` (each parameterless `ObjectStates` op),
  `pg.repeat.<method>` (`WindPanel.StartWind` and the five `ObjectStates` shop-stock methods, at most once per world
  when a replay causes them), `pg.story.<method>`, `pg.money`, `pg.ending.trueEnding`, `pg.ending.lukeFadeDown`,
  `pg.ending.lukeLeave`, `pg.ending.luke`, `pg.ending.fade`, `pg.achievements`, `pg.achievements.retro`, `pg.auth`,
  `pg.trivia`, `pg.ball`, `pg.cutscene.hold`, `pg.cutscene.guard.<StartDialogue2|StartDialogue3|PlayNoTextDialogue|LockPlayer>`,
  `pg.cutscene.fn`, `pg.cutscene.fn.<method>`, `pg.cutscene.fields`, `pg.cutscene.delay`.

### 12.5 Tests

`tests/NGGMultiplayer.Tests` links the Unity-free sources: `FactTable`, `ReplayGuard`, `GameSaveBridge`,
`GameSaveContracts`, `GameScene`, `GameMessages`, `Lifecycle/Logic/*` and `LifecycleRpcs`, `Presence/Model/*` and
`PresenceNames` (with `PRESENCE_MODEL_LINKED`), `Progression/Logic/*`.

### 12.6 Confirmed against the decompiled game

docs/confirm/answers-1.md and answers-2.md answer docs/NEEDS_CONFIRMATION.md item by item. Where they differ from the
text above, the code follows the answers:

- **6.3 Track catch-up** (#86, #141): `CompleteChallenge` does not advance. Catch-up completes a step, then calls
  `RefreshChallenges(false)` unless the game already activated the next challenge; a short streak
  (`m_streaksRequired`) is lifted for the call and restored. A finished set is loaded at its last challenge and
  completed. `LoadNewChallengeSet` keeps `m_mainChallengeSet` for bounty sets (#39).
- **6.4 Bounties** (#36, #40, #122): the easy reward is the same `GetDifficultyValue`. The game's own completion does
  not go through `m_activeBountyPad`: it completes every pad whose clean set name matches. The applier completes all
  of them too; while this player is inside that bounty the whole entry waits until `ExitBountyChallenge`. The host
  pays a client's bounty once.
- **6.5 Unlocks** (#37, #94, #119): `m_aimUnlocked` <-> `UnlockAdjust` is right; the club panel goes with
  `m_putterUnlocked` (`UnlockClub(Putter)` plus `m_clubPanel.Unlock(false)`), `m_ironUnlocked` is field only.
  `StartWind` and the `ObjectStates` shop-stock methods are not idempotent (#95): replays run them at most once per
  world (`pg.repeat.*`).
- **6.7 Interactions** (#35, #92, #137, #138): `m_cashValue` is the price. `LMUGCTicket` is a purchase, `GetCrowbar` a
  free pickup, `GetCard` has no handler (not arbitrated), `FindMissingBall` pays a hard-coded 1000. A purchase is
  charged once at the host's own `m_cashValue`; a paid one-off purchase (club, ticket) stays taken by its buyer.
- **6.8 / 6.9 SaveableObjects** (#41, #90): rotations are world space (`transform.rotation`); whether the shop
  standees are SaveableObjects is logged at run time.
- **6.10 Story** (#38, #96, #97, #120): the Replicate list is the decompile's list in `StoryMethods`; `Luke*`,
  `EndLuke*`, `ReturnLuke*` and `LookAt*` are presentation and are not sent; `UnlockWind` and `UnlockTeleporter` are
  driven by facts. `EndLukeFadeDown` and `LukeVideo.LukeLeaveSequence` are host-only endings. `LukeVideo.OnTriggerEnter`
  has no filter: remote visuals carry no colliders, and on the host colliders under `NGGMP ` objects are ignored.
- **6.13 Puzzles** (#42, #43, #47, #100): `Authenticator.code` / `m_text` are public and set directly; trivia boards
  only use `UnityEngine.Random` in `OnEnable`, so the host disables and re-enables active boards at session start.
  `UnlockNewBall` is called only by `TryTrueEnding`; a client-requested unlock keeps the host's own ball choice.
- **7 Presence** (#45, #51-#55, #126, #127): LMUGC hole = the FIRST number of the id; `m_currentShot` is 0-based
  (stroke = +1); power = `m_power / powerVisualMult`; distance in Unity units (UI says yd), 999 = no hole; the golf
  pose stands at `m_ballPosition`.
- **LMUGC scores** (#139, #140, #143): round totals are >= 9; bests are written in `CompleteHole` with the game's rule
  `num < best || best < 9`; the host loads `GetScoreCardSave` when its scores list is null. The LMUGC set is
  probably a bounty set; the code works either way and logs which.
- **11 Save safety** (#31, #49, #71, #74, #144): no save on quit. Extra writers: the `SetUpScene` broken-save path
  (guarded by `RunGuard`), `SaveGameSettings` (`config.txt`, the player's own), PlayerPrefs `rebinds`, and the
  `SettingsMenu` delete buttons (blocked on clients).

### 12.7 Two players: bounties and locks (as built)

The game has one `ChallengeSystem` per process; each player runs bounties in their own game (`Progression/Logic/
BountyIsolation.cs`, unit tested). "Playing a set" = this player's `m_activeBountyPad` has it, or it is
`m_challengeSet` while that is not `m_mainChallengeSet` (a bounty from a tee, the LMUGC round, #39, #143).

- **Same bounty at once.** The first completion counts. A client's completion is paid once by the host (in its accept);
  a peer still playing that set keeps playing: the entry and the pads wait (the game pays, completes the pads and exits
  only while `HasCompletedThisBounty` is false, #40). If that peer finishes too, its game adds the entry itself; on the
  host the second payment is taken back (`pg.bounty.refund`, also after the session, until that entry is in the
  host's run or the run is deleted; then the run's own `HasCompletedThisBounty` decides). If it walks off instead, the entry
  and pads are applied when it is out (postfix `ExitBountyChallenge` / `LoadNewChallengeSet`, and a 1 s poll, since the
  game may clear its active pad later than those or end a challenge another way).
- **The host's waiting entries** are its own save: kept across scene loads and the session end, kept in every fact
  rebuild, written into the run before the game's own save in `PauseMenu.QuitToMenu` (void prefix, `pg.bounty.quit`),
  and applied (pads, boards, save) as soon as the host is out of that bounty.
- **Different bounties at once.** Another player's completion of a set this player is not playing is applied at once
  (entry, pads, boards); this player's bounty, its `m_challengeSet` and the main track are never touched (the main
  track waits as in 6.3). Other players' pre-hit targets (`pht`) are recorded at once but reach `m_preBrokenShots` only
  once this player is out of its own challenge. Objects this player's own bounty start switches off (the main
  challenge's objects, which come back when it ends) are held back, like the props it switches on: a deactivation in
  its own challenge within 5 s of its own set change is not reported, stays held while in it and 5 s after, and is
  reported then only if the object is still off; pickups and purchases are never held.
- **Locks.** Padlocks (crowbar), RFID doors (card), gates, doors, the bunker and the server are opened by whoever is
  there, in their own game, cutscene included; the requirement is shared progress (`rb/m_crowbarUnlocked`,
  `rb/m_cardUnlocked`, arbitrated pickups). The opening reaches the others as a world op (animation) and as `obj` / `rb`
  facts (state, late joiners); a client's are claims the host applies and saves (`ScheduleHostSave`), the client's own
  `SaveRun` calls never write (Lifecycle). Story-driven doors (`OpenShop`, `OpenBarDoor`, `OpenMarketingDept`,
  `Open*Gates`) replicate by name (6.10), main-track ones through the track replay. Runtime checks: whether sliding
  (RFID) door positions persist in the game at all (`SaveableObject` saves only active and rotation), whether
  `BountyPad.Complete(false)` unlocks `m_padThatIsUnlocked`, and whether a bounty start switches SaveableObjects off.
