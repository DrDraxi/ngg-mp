# NGG Multiplayer

Online co-op for **Normal Golf Game**: a [BepInEx 5](https://github.com/BepInEx/BepInEx) mod over Steam
peer-to-peer. One player hosts, friends join, and everyone plays in the host's world with the host's save.

## Features

- Host and join from the game's own menus: Steam lobbies, friend invites and a lobby browser.
- One shared save (the host's): story progress, bounties, unlocks, clubs, opened doors and locks, and one shared bank.
- Other players shown as golfers that face your camera (standing, walking, crouching, swinging), with name tags,
  their ball and its trail.
- Player list in the HUD, hole-out notices and a shared round scorecard (hold **H**).
- Competitive *Just Play Nine*: everyone plays the same hole, the next hole waits for everyone, and a group
  scorecard and leaderboard rank the players by score to par. Real golf turns are an option.
- Male or female golfer, picked with the Character selector.
- Your own save is never touched while you play in someone else's world; it is backed up before you join.
- Direct IP mode (LAN or a VPN like Tailscale) as an alternative to Steam lobbies.

Heads up: the player art (the other players' cutouts and the female golfer's drawings) is AI-generated, and it
looks like it.

## Install

1. Download `NGGMultiplayer-<version>-with-BepInEx.zip` from the [releases](../../releases).
2. In Steam: right-click Normal Golf Game > *Manage* > *Browse local files*, then open the `Normal` folder (the one
   with `Normal Golf Game.exe`).
3. Unzip everything into that folder, so `winhttp.dll` and the `BepInEx` folder sit next to `Normal Golf Game.exe`.
4. Start the game from Steam. The main menu now has a **Multiplayer** panel.

The zip ships UnityDoorstop 4.6.0's `winhttp.dll` because the one in stock BepInEx 5.4.23.2 crashes this game
(Unity 6). If you already have BepInEx, the mod-only zip goes into the same folder. Every player needs the same mod
and game version.

To uninstall, delete `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version` and the `BepInEx` folder.

## Playing

- **Host:** main menu > *Host Game*, pick who can join, then play. Already in your world? Pause menu > *Multiplayer*
  > *Host This Game*. Invite friends from the pause menu or the Steam overlay.
- **Join:** accept a Steam invite, or *Join Game* in the main menu and pick the host's game. You follow the host
  into their world.
- **Leave:** pause menu > *Multiplayer* > *Leave Session* (the host ends the session for everyone).
- **F7** opens the mod's small multiplayer window.

Good to know:

- Everyone plays their own ball; other players' balls are ghosts that don't collide with you.
- *Just Play Nine* is a match: anyone presses the round machine and the round starts for everyone. Everyone plays
  the hole at once (with `TurnOrder = Turns`, when the banner says "Your shot"); holding **H** shows the group
  scorecard. A player who reaches the stroke limit picks up. Players who join mid-round start on the current hole
  (missed holes count par + 2). Story's 9 holes stay co-op.
- Bounties can be played separately or together; a completion counts once and pays once into the shared bank.
- Dialogue and cutscenes play only for the player who triggered them; the results reach everyone.
- Only the host starts a game from the main menu, and only the host's save is written.

## Settings

`BepInEx/config/ngg.multiplayer.cfg` is created on the first start. The useful ones:

| Section | Key | Default | Meaning |
|---------|-----|---------|---------|
| General | `DisplayName` | *(empty)* | Your name for other players; empty uses your Steam name. |
| General | `ScorecardKey` | `H` | Hold for the shared scorecard. |
| Lobby | `LobbyType` | `FriendsOnly` | `Private`, `FriendsOnly`, `Public` or `Invisible`. |
| Lobby | `MaxPlayers` | `4` | Players per session, host included (2-16). |
| Presence | `Character` | `Male` | `Male` or `Female` (also on the multiplayer screens). |
| Match | `Competitive` | `true` | Just Play Nine as a match; `false` plays it side by side as before. Host's setting. |
| Match | `TurnOrder` | `Free` | `Free` (everyone plays the hole at once; the next hole waits for all) or `Turns` (golf order: honours, then farthest from the cup). Host's setting. |
| Match | `MaxStrokes` | `10` | Pick up at this many strokes on a hole; `0` = no limit. Host's setting. |
| Network | `Transport` | `Steam` | `Steam`, or `Direct` for plain UDP to `HostAddress:DirectPort`. Needs a restart. |
| Network | `HostAddress` | `127.0.0.1` | Direct: the host's IP (the host uses `0.0.0.0`). |
| Network | `DirectPort` | `27250` | Direct: the UDP port. |

Direct mode has no lobby or password: anyone who can reach the port can join, so use it on a LAN or a VPN.

Problems? Attach `BepInEx/LogOutput.log` when you report them.

## Building

See [docs/BUILDING.md](docs/BUILDING.md). Testing tools are in [docs/TESTING.md](docs/TESTING.md).
