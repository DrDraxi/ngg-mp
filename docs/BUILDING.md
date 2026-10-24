# Building NGG Multiplayer

Requirements: the .NET 8 SDK (any OS).

```sh
dotnet build -c Release     # build everything
dotnet test                 # run the test suite
./scripts/package.sh -p:GameManagedDir=/path/to/Managed   # Linux/macOS: write dist/NGGMultiplayer-<version>.zip
./scripts/package.ps1 "-p:GameManagedDir=C:\...\Managed"  # Windows PowerShell equivalent
```

Without the game installed (cloud / CI builds) the plugin compiles against stand-ins: compile-time stubs of the
game's assemblies in `stubs/` (Assembly-CSharp, ECM2, UnityEngine.UI, Unity.TextMeshPro, Unity.InputSystem; see
[stubs/README.md](../stubs/README.md)), the `UnityEngine.Modules` 2021.3.33 package and `Steamworks.NET` 2024.8.0, all
compile-only. Such a build defines `STUB_GAME` and is for compile checks only. BepInEx and Harmony come from `lib/`.

To compile against the game's real assemblies, point `GameManagedDir` at the game's `Managed` folder. It defaults
to `lib/game/`, which is git-ignored, so you can also copy the DLLs there:

```sh
dotnet build -c Release -p:GameManagedDir="C:\Program Files (x86)\Steam\steamapps\common\Normal Golf Game\Normal\Normal Golf Game_Data\Managed"
./scripts/package.sh -p:GameManagedDir=/path/to/Managed
```

When `Assembly-CSharp.dll` is found there, the plugin references the game's own `Assembly-CSharp`, `ECM2`,
`UnityEngine.dll` with every `UnityEngine.*Module.dll` (Unity 6; the 2021 package is dropped), `UnityEngine.UI`,
`Unity.TextMeshPro` and `Unity.InputSystem`, none of them copied to the output. `GAME_ASSEMBLIES` is defined in both
modes. When the game's Steamworks.NET build (`com.rlabrecque.steamworks.net.dll` or `Steamworks.NET.dll`) is
found, it replaces the NuGet package so the plugin binds to exactly the assembly the game loads.
When the game has no separate Steamworks DLL but compiled Steamworks.NET into `Assembly-CSharp-firstpass.dll`,
that assembly is used instead. The build log prints the game references and the chosen binding (`game`,
`firstpass` or `standin`).

The package scripts always refuse a `STUB_GAME` build, and never ship the stub DLLs. They also refuse the `standin`
Steamworks binding: the NuGet assembly is named `Steamworks.NET`, which a Unity game almost never loads under that
name, so the plugin would fail to start. For a local test build with the game's assemblies but without its
Steamworks DLL, pass `--allow-standin-steamworks`; the zip is then named `NGGMultiplayer-<version>-standin.zip`. Even
then the plugin reports multiplayer as unavailable instead of failing to load when the binding does not resolve.

## Releasing

Bump the version in `Directory.Build.props`, `ProtocolInfo.ModVersion` and `ProtocolDefinitionTests`, then run the
package script with PowerShell 7 (`pwsh`; Windows PowerShell 5.1 writes `\` paths into the zip). A fresh clone has
no `lib/BepInEx.dll` or `lib/0Harmony.dll` (git-ignored): copy them from `BepInEx/core` of a BepInEx 5.4.23.2
install. The all-in-one release zip is the package plus BepInEx 5.4.23.2 (`BepInEx/core`, `doorstop_config.ini`,
`changelog.txt`), UnityDoorstop 4.6.0's x64 `winhttp.dll` and `.doorstop_version`, and
[`packaging/INSTALL.txt`](../packaging/INSTALL.txt), with the README renamed to `NGGMultiplayer-README.md`.

## Layout

| Path | Contents |
|------|----------|
| `src/NGGMultiplayer.Core` | Game- and engine-independent networking (netstandard2.0): protocol, session, save sync, entity sync, RPC. |
| `src/NGGMultiplayer` | The BepInEx plugin (netstandard2.1): Steam transport and lobbies, runtime glue, IMGUI window, game adapter seam. |
| `src/NGGMultiplayer/Game` | The game integration: shared context and Lifecycle (scenes, session save, save protection), Presence (avatars, ghosts, HUD, wind, rounds) and Progression (host-authoritative progress) modules. |
| `tests/NGGMultiplayer.Tests` | xunit tests for Core over an in-memory loopback network with latency and loss, the Unity-free logic of the game modules, and the test harness's bots. |
| `assets-src/` | The lossless source images (the raw base to edit or regenerate from): `avatar/`, the remote players' cutouts (generated with Tripo), and `video/female/`, the female look's key drawings for the player's own videos. Not shipped. |
| `assets/` | The shipped, compressed copies of `assets-src/` (256-colour PNGs, about a third of the size), made by `assets/tools/pack_assets.py` after any change there; the package scripts copy these. `assets/tools/make_cutouts.py` scales and places a cut-out figure on the avatar canvas. |
| `stubs/` | Compile-time stubs of the game's assemblies for builds without the game (never shipped). |
| `docs/ARCHITECTURE.md` | Design and module contracts. |
| `docs/GAME_INTEGRATION.md` | The game integration spec (scenes, session save, progression, presence) and its feature keys. |
| `docs/gamemap/` | The map of the game's classes and patch points the integration is built on. |
| `docs/TESTING.md` | How to run the single-PC test harness and the two-copy end-to-end setup (`scripts/e2e.ps1`), and the in-game test plan (including save-safety checks). |
| `docs/NEEDS_CONFIRMATION.md`, `docs/confirm/` | The game facts the code relies on, checked against the decompiled game. |
| `src/NGGMultiplayer/Testing` | The single-PC test harness (off unless `EnableTestHarness`). |

## All settings

Settings live in `BepInEx/config/ngg.multiplayer.cfg` (created on first run). Changes made while the game runs
apply at once unless noted.

| Section | Key | Default | Meaning |
|---------|-----|---------|---------|
| General | `ToggleWindowKey` | `F7` | Shows or hides the multiplayer window. |
| General | `DisplayName` | *(empty)* | Name shown to other players; empty uses your Steam name. |
| General | `ScorecardKey` | `H` | Hold for the session scorecard. The game uses Tab itself (club switch); `None` turns the key off. |
| Lobby | `LobbyType` | `FriendsOnly` | `Private` (invite only), `FriendsOnly`, `Public`, or `Invisible` (joinable by id only). |
| Lobby | `MaxPlayers` | `4` | Players per session including the host (2-16). |
| Network | `SendRate` | `30` | Position updates per second for your golfer and ball (5-60). Next session. |
| Network | `InterpolationDelayMs` | `100` | How far in the past other players are drawn; raise it on poor connections (0-500). Next session. |
| Network | `TimeoutSeconds` | `10` | Seconds of silence before a player counts as disconnected (3-60). |
| Network | `Transport` | `Steam` | `Steam` (lobbies, invites) or `Direct` (plain UDP to `HostAddress:DirectPort`, no Steam lobby; two copies on one PC or a trusted LAN, anyone who reaches the port can join). Needs a restart. |
| Network | `HostAddress` | `127.0.0.1` | Direct: the host a client joins. A loopback host listens on this PC only; otherwise on every interface. |
| Network | `DirectPort` | `27250` | Direct: the UDP port the host listens on. |
| Presence | `RemoteBallTrail` | `true` | Draws a trail in the player's colour behind other players' balls while they fly and roll. |
| Presence | `Character` | `Male` | `Male` or `Female`: your golfer for the others and your own hands and swings. |
| Presence | `TrailSeconds` | `0` | How long that trail lasts in seconds (0-10); `0` matches the game's own ball trail. |
| Steam | `PumpCallbacks` | `false` | Runs Steam callbacks from the mod. Only enable if creating or joining a lobby never finishes. |
| Debug | `LogLevel` | `Info` | Minimum level of networking messages written to the BepInEx log (`Debug` for troubleshooting). |
| Debug | `EnableTestHarness` | `false` | Single-PC test harness with in-process bots, driven by `BepInEx/config/ngg_mp_commands.txt` ([TESTING.md](TESTING.md)). Testing only; needs a restart. |
| Debug | `TestBotLatencyMs` | `0` | One-way delay of the harness bots' link in milliseconds (0-2000). |
| Debug | `DevControlPort` | `0` | With the harness on: an HTTP API on 127.0.0.1 for scripts (`scripts/e2e.ps1`, `scripts/e2e-scenarios.ps1`, [TESTING.md](TESTING.md) 2.7, 2.8). 0 is off. |
| Debug | `SaveDirOverride` | *(empty)* | Test copies: a folder that replaces the game's save folder for every save file. Needs a restart. |
