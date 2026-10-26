# Testing NGG Multiplayer

Three levels, from cheapest to most complete:

1. **Unit tests** (any OS, no game): `dotnet test`. Core networking over an in-memory network with latency and loss,
   the Unity-free logic of the game modules, and the test harness's bots against a real host `NetSession`.
2. **Single-PC harness** (section 2): the real game on one PC hosts a real Steam lobby, and in-process bots join it
   as clients. Tests everything the **host** does with clients: admission, save sync, presence, claims, money,
   world ops, story, wind, leaves and timeouts.
3. **Two-player play test** (section 3): two PCs, two Steam accounts. The only way to test the **client** side
   (following the host, the client's save protection, applying the host's world).

Bots never run the client's game code: they keep the host's save in memory and write nothing to disk. So section 3
remains required before a release.

## 1. Prerequisites on the test PC

- BepInEx 5.4.23.2 x64 with **UnityDoorstop 4.6.0's `winhttp.dll`** (see README, *Install*). With the Doorstop
  4.3.0 that ships in the BepInEx zip, Unity 6000.3 crashes at startup.
- A Release build of the plugin made against the game's assemblies (`dotnet build -c Release
  -p:GameManagedDir=...`; the build log must say binding `game`), or the packaged zip.
- `BepInEx/LogOutput.log` shows `NGG Multiplayer <version> loaded`.
- Save folder: `%USERPROFILE%\AppData\LocalLow\Luke Muscat\Normal Golf Game\` (`run2.txt`, `run2_temp.txt`,
  `actuallyNormal.txt`, `scoreCard.txt`, `unlocks.txt`, `config.txt`). Key rebinds live in the registry under
  `HKCU\Software\Luke Muscat\Normal Golf Game` (PlayerPrefs `rebinds`).
- Recommended while testing: in Steam, game *Properties > General*, turn **Steam Cloud** off, so a cloud sync can
  not hide or undo a change to the save folder. Turn it back on afterwards.

### 1.1 Save backup and hash (before every launch)

Run this in PowerShell **before every launch** of the game, and again (step 2 only) **after the game has exited**.
It copies the saves into a time-stamped folder and writes their SHA-256 hashes.

```powershell
$save = "$env:USERPROFILE\AppData\LocalLow\Luke Muscat\Normal Golf Game"
$out  = "$env:USERPROFILE\Desktop\ngg-save-backups\$(Get-Date -Format yyyyMMdd-HHmmss)"
New-Item -ItemType Directory -Force $out | Out-Null
# 1. backup (before launch only)
Copy-Item "$save\*" $out -Recurse
reg export "HKCU\Software\Luke Muscat\Normal Golf Game" "$out\prefs.reg" /y | Out-Null
# 2. hashes (before launch and after exit)
Get-ChildItem $save -File | Get-FileHash -Algorithm SHA256 |
  Select-Object @{n='Name';e={Split-Path $_.Path -Leaf}}, Hash | Format-Table -AutoSize | Out-File "$out\hashes.txt"
Get-Content "$out\hashes.txt"
```

Compare the *after exit* hashes with the *before launch* hashes of the same run:

| Role in that run | Expected |
|---|---|
| Client (joined someone) | `run2.txt`, `run2_temp.txt`, `actuallyNormal.txt`, `scoreCard.txt`, `unlocks.txt` **identical**. `config.txt` may change only if you changed settings yourself. |
| Host | The host's own saves change normally (they hold the session's progress). |
| Harness host with bots | As host. Bots never write: nothing appears or changes because of a bot. |

A client-side change to a protected file is a **failure**: keep the backup folder, `BepInEx/LogOutput.log` and
`BepInEx/cache/NGGMultiplayer/LocalBackup/` (the mod's own backup and `restore-log.txt`) and report them. To roll
back, quit the game and copy the backup files back into the save folder (`prefs.reg` re-imports with a double click).

## 2. Single-PC test harness

### 2.1 Turn it on

In `BepInEx/config/ngg.multiplayer.cfg` (created on first start):

```ini
[Debug]
EnableTestHarness = true
## optional: one-way delay of the bots' link in milliseconds (0..2000)
TestBotLatencyMs = 0
```

Restart the game (the setting is read at start). The log shows `TEST HARNESS ENABLED`, and
`BepInEx/config/ngg_mp_results.txt` gets a `test harness started` line, followed by the `interaction` check.
Set it back to `false` when you are done: while it is `false`, nothing of the harness runs, and the Steam session
uses the plain Steam transport.

### 2.2 How it works

- Append commands, one per line, to `BepInEx/config/ngg_mp_commands.txt` (create it if missing; any editor, or
  `Add-Content "...\BepInEx\config\ngg_mp_commands.txt" "status"`). The file is read once per second. Lines that were
  already there when the game started are skipped; a half-written line waits for its newline; if you empty the file
  (save it with nothing in it, or delete it) it is read from the start again. **Only append lines; never edit or
  delete earlier ones.** A file that got shorter but is not empty (a line deleted, or the whole text replaced in one
  save) is not run again, because many commands change the host's real save (`storyhost DeductMoney`, claims, bank,
  pickups); the reader skips to its new end and writes a note to the results file. To replace the commands, save the
  file empty first, then append the new lines.
- Commands run in order, one after the other in the same frame, except that `wait <seconds>` holds the commands
  after it, and a game action still in progress holds them until it is done: a key until it is released (and one
  more frame), a screenshot until its file is on disk (at most 10 s), a click or `mpwindow` for two frames. So a whole
  sequence can be appended at once (2.6). `quit` stops the queue for good.
- Every result is appended to `ngg_mp_results.txt` as `[yyyy-MM-dd HH:mm:ss.fff] > command` followed by the result,
  indented. Bot events (joined, save synced, claim verdicts, grants, notes) appear there as `botN (Name): ...`.
- Bots are clients in the same process with ids no Steam account can have. They reach the host over an in-process
  hub, only while this game hosts; all other traffic goes to Steam. They keep running while the game is paused
  (timeScale 0), as a real client would.
- `@<number or name>` picks a bot: default all bots for `walk`/`shoot`, the newest for `remove`/`leave`, the first
  for `claim`.

### 2.3 Commands

| Command | What it does | Expected result |
|---|---|---|
| `help` | Lists the commands. | The command list. |
| `status` | Service state, role, lobby, players, each bot's state. | `service ..., hosting, lobby <id>, N player(s); bots: ...`. `NOT WIRED` means the transport hook is missing (a build error). |
| `host` | Hosts a Steam lobby (like *Host game*). | `hosting requested: ...`; `status` then says `hosting`. |
| `leave` | Leaves / ends the session. | `left the session (N bot(s) will see the host leave)`; each bot reports `session ended`. |
| `bot add [name]` | A bot joins the hosted session. | `botN (name) is joining as <id>`, then `connecting`, `joined ... as player #N`, `save synced: ...`. |
| `bot remove [@bot]` | Bot vanishes without goodbye. | The host drops it after `TimeoutSeconds` (default 10 s): the player list loses it. |
| `bot leave [@bot]` | Bot leaves cleanly. | `left the session`; the host's player list loses it at once. |
| `bot walk on\|off [@bot]` | Bot walks a circle around the host's position / stands. | An avatar walks in the host's world; the HUD lists the bot as walking. |
| `bot shoot [power] [@bot]` | Bot takes a golf stance and hits a ghost ball (power 0..1 of a full swing, default 0.7). | `takes its stance`; the avatar golfs and a ghost ball flies (stroke 1 for the first shot in the player list). |
| `wind` | Host's live wind next to each bot's last `ngg.pr.wind`. | Bots' values follow the host's within a second. |
| `dump` | One JSON document: harness, session, bots (save, claims, avatar, traffic), world, facts, save guard, save check. | Valid JSON; `dumpError` only if a game read threw. Per bot `save.diskWrites` must be 0 and `digestMismatches` 0. |
| `savecheck` | Hashes the real save files (+ `config.txt`, PlayerPrefs `rebinds`) against the baseline (harness start or session start). | As host: `changes are allowed for this role`. **`VIOLATION`** only if this game was a client. |
| `savecheck reset` | New baseline now. | `savecheck baseline reset: N files hashed in <save folder>`. |
| `check [all\|name]` | Runtime checks of the open confirmation answers (2.5). | One line per check. |
| `list sets\|bounties\|pickups\|doors\|gates\|saveables` | Ids in the host's world, for claims. | `N <what>:` then one id per line. |
| `log debug\|info\|warning\|error` | Changes the mod's log level until the config changes. | `log level ...`. |
| `latency <ms>` | One-way delay of the bots' link. | `bot link latency N ms one way`. |
| `bot claim bounty <set> [easy]` | Bot completes a bounty. | `accepted`; the host's pads for that set complete, the reward is paid once into the host's bank; the bounty board updates. A main-track set gets a warning. |
| `bot claim challenge [index]` | Bot advances the shared main track (default: the next challenge). | `accepted`; the host's current challenge advances by one, with its story effect. |
| `bot claim challenge <set> <index>` | Bot claims a track position in a given set. | `accepted` for a valid later position. The harness warns when the set is unknown or a bounty set; then the host should refuse it (`REJECTED`). |
| `bot claim door <id>` / `gate <id> open\|close` / `sliding <id> open\|close` | World ops. | The door/gate moves in the host's world. |
| `bot claim world <op> [id] [on\|off]` | Any world op 1..11 by number or name (SetGates, OpenDoor, SlidingDoor, ServerCut, ServerRestore, BunkerScreen, DriverAvailable, WedgeAvailable, HybridAvailable, TicketAvailable, LmugcMachine). | The effect happens once on the host (shop stock appears; replays do not spawn it twice). |
| `bot claim pickup <interactId> [<type> <cash>]` | Bot claims an interactable (id from `list pickups`). | `GRANTED` once; a second claim of the same item is `REFUSED`. CollectCash adds `m_cashValue` to the host bank; a purchase (club, ticket, assist) takes exactly the host's price once, and is refused when the host's balance is too low; crowbar and card change no money. |
| `bot claim bank <delta> [claimId]` | A raw money report. | Without a matching grant the host ignores it (bank unchanged). |
| `bot claim rb <field> true\|false` / `ri <field> <n>` | RunSave bool / int fields. | Shared fields `accepted` and applied (e.g. `m_spinUnlocked true` shows the spin panel); others `REJECTED`. |
| `bot claim obj <saveableId> active\|inactive` | SaveableObject state. | Applied on the host; deactivating an arbitrated pickup the bot was not granted is `REJECTED`. |
| `bot claim story <method>` | Bot sends a replicated DialogueManager method. | Replicate-list methods (e.g. `OpenShop`) run on the host; `Luke*` and others are refused (the harness says so in advance). |
| `bot claim storyhost <method>` | `DeductMoney` / `ReturnMoney` request. | Host runs it once per state (money changes by 100000). |
| `bot claim fact <kind> <key> <hex>` | Any raw fact claim. | Per the host's rules. |

A rejected claim makes the host resync that bot (`save synced` again): expected.

Driving the game without screen, mouse or keyboard (2.6):

| Command | What it does | Expected result |
|---|---|---|
| `wait <seconds>` | Holds the following commands (0..600 s, real time, so it also runs while paused). | `waiting N s before the next command`. |
| `screenshot [name]` | `ScreenCapture.CaptureScreenshot` to `BepInEx/screenshots/<yyyyMMdd-HHmmss>_<name>.png` (folder created; the name keeps ASCII letters, digits, `-`, `_`, `.`; default `shot`; `-2`, `-3` appended if the file exists). Includes the IMGUI windows. | `screenshot <full path> (the file is written at the end of this frame ...)`, then a separate `screenshot written: <path> (N KB, WxH)` line once it is on disk. `NOT written` after 10 s means no frame was rendered (minimized window). |
| `key <KeyName> [hold-seconds]` | Presses the key through the Input System (`InputSystem.QueueStateEvent` with a `KeyboardState` on `Keyboard.current`) and releases it after the hold (default 0.1 s, at most 30). The game's actions (Pause = Escape, ...) see it like a real press. Names: Input System `Key` names, any case (`Escape`, `Tab`, `Enter`, `Space`, `F7`, `A`, `Digit1`, `LeftArrow`, `LeftShift`), plus `esc`, `return`, `up`/`down`/`left`/`right`, `ctrl`, `shift`, `alt`, `1`. | `<Key> pressed for 0.1 s through the Input System (device 'Keyboard'); plugin keys (BepInEx LegacyInputSystem) get it as KeyCode.<Key>`. A `DISABLED` device or `does NOT have focus` means the press is likely lost: the game window must be the foreground window. |
| `buttons` | Lists the active, interactable uGUI `Button`s (and other click handlers such as `Toggle`s) of the loaded scenes, sorted by path, with their TextMeshPro / Text label. | `N clickable element(s):` then `n. [Button] 'Play' at Menu2:Canvas/.../PlayButton`. The mod's own IMGUI buttons (the *Multiplayer* buttons, the F7 window) are not uGUI and never listed: use `mpwindow`, `host`, `leave`. |
| `click <label or path part> [@n]` | Clicks the element named, case-insensitively: whole label first, then part of a label, then part of the path. The first of these tiers that matches decides; several matches there are **ambiguous** and nothing is clicked unless `@n` picks the n-th of them. Button: `onClick.Invoke()`; other handlers: `OnPointerClick`. | `clicked [Button] 'Play' at ... (exact label)`; or `ambiguous: ...` / `nothing matches ...` with the candidates, nothing clicked. |
| `scene` | Active scene and build index, all loaded scenes, `Time.timeScale`, `Cursor.lockState`/`visible`, window focus and size, pause menu / settings menu open (the PauseMenu and SettingsMenu objects active, and the integration's own pause flag), game mode (Story / JustGolf), player mode (Walk / Golf, move locked), the multiplayer window, the keyboard device and BepInEx's input backend. | e.g. `active scene 'Main' (build index 2)`, `timeScale 0, ... cursor None visible`, `menus: pause menu OPEN, settings menu closed, ...`, `game: mode Story, integration scene Main (world ready), player Walk`. |
| `mpwindow <open\|close\|toggle>` | Opens / closes the multiplayer window directly (`LobbyWindow.Visible`). | `multiplayer window open`. |
| `quit` | In Menu2: the game's own `MainMenu.Quit()` (the Quit button), with `Application.Quit()` 5 s later if the game still runs; elsewhere `Application.Quit()` at the next frame. In Main use the pause menu's *Save and Quit* first: it saves the run and returns to Menu2 (quitting from Main is like Alt+F4). Nothing after it runs. | `MainMenu.Quit() called ...`; the game exits, the log ends normally. |

**How F7 works here.** BepInEx reads keys through legacy `UnityEngine.Input` in this game (the swing reads the legacy
mouse, so the legacy backend is active; `scene` shows `BepInEx LegacyInputSystem`), and legacy input never sees Input
System events. So while the harness runs, `key` also reports the key to the mod's own key reading (the F7 toggle in
`LobbyWindow`, the HUD's scorecard key), from the frame in which the Input System gets the press: `key F7` toggles the
window exactly like a real F7, and `key H 2` holds the session scorecard open for 2 s (`[General] ScorecardKey`,
default H; the game binds Tab to its club switch, so Tab is no longer the scorecard key). Outside the harness this path is
always off. `mpwindow` sets the window directly, independent of keys.

### 2.4 Harness test run (host side)

Take the save backup (1.1) first. Start the game, load **Story** into the world, then append these lines one block
at a time and check `ngg_mp_results.txt` after each:

1. `status`, `check all` (keep the output for the report), `savecheck reset`.
2. `host`, then `status` -> hosting. `bot add Alice`, `bot add Bob` -> both `save synced`; the in-game player list
   shows three players.
3. `bot walk on` -> two avatars walk; `bot walk off @Bob`; `bot shoot @Alice` -> stance, ghost ball and its
   trail. `check colliders` -> `0 collider(s), 0 rigidbody(ies)` and `OK`.
4. `wind` -> the bots follow the host's wind (once wind is unlocked in the run).
5. Pause the game (Escape): `status` still shows both bots; `check pause` with the menu closed, open, and in its
   settings submenu; unpause.
6. Progress: `list sets`, `bot claim challenge` -> the track advances once on the host; `list bounties`,
   `bot claim bounty <name>` -> pads complete, money is paid once (watch the bank); repeat it -> `already the
   session's value`.
7. Money and pickups: `list pickups`; claim a cash pickup twice (`GRANTED`, then `REFUSED`); claim a shop purchase
   with less money than its price (`REFUSED`), then with enough (`GRANTED`, bank drops by exactly the price once).
8. World: `list doors`, `bot claim door <id>`; `bot claim world DriverAvailable` twice -> the stock appears once,
   with no second effect.
9. Story: `bot claim story OpenShop` (runs), `bot claim story Luke1` (refused); `bot claim storyhost DeductMoney`.
10. `dump` -> valid JSON, every bot `diskWrites` 0, `digestMismatches` 0.
11. Leaves: `bot leave @Alice` (gone at once), `bot remove @Bob` (gone after ~10 s). `latency 300`, `bot add Carol`,
    `bot walk on` -> still smooth. `leave` -> Carol reports `session ended`.
12. `savecheck` -> no `VIOLATION`. Quit the game, run the hash step of 1.1: only the host's own saves changed.

### 2.5 Runtime checks (`check <name>`)

These settle the confirmation answers marked `?` (docs/NEEDS_CONFIRMATION.md `[?]`). Run them in the world (Main)
unless noted, and paste the lines into the report.

| Check | Answer | What to read |
|---|---|---|
| `interaction` | #92 | `#92 holds`. `MISMATCH` means the InteractionType table changed (bots' pickup claims would be wrong). |
| `shottype` | - | ShotType members and the value the bots send. |
| `colliders` | #97 | `OK` with bots present. `DANGER` means a remote view could trigger Luke's ending. |
| `pause` | #58, #112, #147 | PauseMenu count and activity, `m_objectToEnable`, timeScale: run closed, open and in settings. |
| `tab` | #50, #67 | Tab's bindings in the game's input actions (the game binds it: `Switch`), and whether the scorecard key (`[General] ScorecardKey`, default H) is free. |
| `keys` | - | Every keyboard binding of the game's input actions (to pick free keys for the mod). |
| `standees` | #41, #123 | Which shop stock objects are SaveableObjects. |
| `lookinput` | #115 | Where FirstPersonCharacterLookInput sits; the pause-disabled inputs are Behaviours. |
| `materials` | #130, #131 | Shader properties of the ball skins and the ball's material. |
| `lmugc` | #143 | `isBounty` of the LMUGC set; run it again **inside** an LMUGC round: the main set must stay the story set. |
| `manager` | #145 | BepInEx's manager object survives scene loads. |

The mod also logs these outcomes once by itself (`BepInEx/LogOutput.log`): the shop SaveableObjects
(`[Progression] Shop stock objects that are SaveableObjects`), how the game took a shared-track step
(`Shared track: ...`), the golf stance offsets, the ball tint property and whether the game binds the scorecard key
(Presence).

### 2.6 Remote run without screen or input (menus, story, pause, settings)

**Without writing the command file at all:** set both `EnableTestHarness = true` and `AutoTest = true` under
`[Debug]` in the plugin's BepInEx config and launch the game. Once the main menu has been up for 5 s the mod queues
the built-in copy of this run (`Testing/Logic/AutoTest.cs`: the block below plus a bot test), switches both settings
back off for the next launch, and quits the game at the end. Results and screenshots land in the same places.

For a test PC nobody sits at: everything goes through the command file, and the screenshots show what happened.
Keep the game the **foreground window** (not minimized): the Input System drops keyboard events of an unfocused
game and a minimized one renders no frames for `screenshot`. Take the save backup (1.1) first; this run continues the
existing story save (it never picks a new game or deletes anything).

The button texts below were read from the game (2026-10-20): Menu2 has `Play`, `Just Play Nine`, `Settings`,
`Quit To Desktop`; with a story save, `Play` loads straight into the world (there is no Continue screen; with no
story save it shows the difficulty screen, then click a difficulty); the pause menu has `Resume`, `Settings` and
`Save and Quit`; the settings menu has `Back`. A wrong text clicks nothing (`nothing matches` / `ambiguous`, with the
real candidates listed) and the run goes on; the `buttons` line before each click puts the real texts into the
results, so fix the `click` lines from there and run the block again. Never load Main another way than through the game's own Play path
(`MainMenu.PlayGame` -> `PlaySetUp`): loading the scene directly skips the save prompt and the mode setup.

Start the game, wait for the main menu (Menu2), then append the whole block (PowerShell; set `$bep` to the game's
BepInEx folder):

```powershell
$bep = "C:\Program Files (x86)\Steam\steamapps\common\Normal Golf Game\Normal\BepInEx"
Add-Content "$bep\config\ngg_mp_commands.txt" @"
scene
screenshot main_menu
key F7
wait 1
screenshot f7_window
scene
mpwindow close
buttons
click Play
wait 2
buttons
screenshot after_play
wait 8
scene
screenshot main
key Escape
wait 1
screenshot pause
scene
check pause
check tab
buttons
click Settings
wait 1
screenshot settings
scene
check pause
buttons
click Back
wait 1
screenshot settings_closed
scene
buttons
click Save and Quit
wait 8
scene
screenshot menu_again
quit
"@
# about a minute later (or follow it live with -Wait):
Get-Content "$bep\config\ngg_mp_results.txt" -Tail 150
```

The block takes about 40 s. Expected, step by step:

| Step | Expected in the results / screenshot |
|---|---|
| `scene`, `screenshot main_menu` | `active scene 'Menu2'`, `main menu present`, `timeScale 1`; the PNG shows the main menu with the mod's *Multiplayer* button. |
| `key F7`, `screenshot f7_window` | `F7 pressed ... get it as KeyCode.F7`; `scene` says `multiplayer window: open`; the PNG shows the NGG Multiplayer window. `mpwindow close` closes it again. |
| `click Play`, `buttons` | `clicked [Button] 'Play' ...`; the game loads the story save into Main, so the second `buttons` already lists Main's elements (with no story save yet it lists the difficulty screen: click a difficulty there). |
| `wait 8`, `scene`, `screenshot main` | `active scene 'Main' (build index 2)`, `mode Story`, `integration scene Main (world ready)`, `player Walk`; the PNG shows the world. |
| `key Escape`, `screenshot pause`, `scene` | `menus: pause menu OPEN`, `timeScale 0` (stays 1 while hosting a session), `cursor None visible`; the PNG shows the pause menu (with the mod's *Multiplayer* button). `check pause` with the menu open, `check tab` (Tab is bound by the game; the scorecard key H is free). |
| `click Settings`, `scene` | `settings menu OPEN`, `pause menu closed`, still `timeScale 0`, `integration says paused True`; `check pause` in the settings submenu. |
| `click Back`, `scene` | `pause menu OPEN`, `settings menu closed` again. |
| `click Save and Quit`, `wait 8`, `scene` | `active scene 'Menu2'`, `timeScale 1`; the run was saved (`run2.txt` newer). |
| `quit` | `MainMenu.Quit() called ...`; the game exits. Then run the hash step of 1.1. |

Then copy `BepInEx/screenshots/*.png`, `BepInEx/config/ngg_mp_results.txt` and `BepInEx/LogOutput.log` into the
report. Pressing Escape again (`key Escape`) instead of `click Back` / a resume button closes the pause menu the way a
player does; compare `scene` before and after.

### 2.7 Two game copies on one PC (host and client, no Steam lobby)

Tests the **client** side too, on one PC and one Steam account: two copies of the game outside the Steam folder talk
over localhost with the Direct transport, and scripts drive both through the dev control API. Modeled on the
ShadowsOfDoubtMP setup.

```powershell
scripts/e2e.ps1          # build, set up both copies, Host plays Story and hosts, Client joins, screenshots
scripts/e2e.ps1 -Stop    # quit both copies (through the API)
```

`scripts/e2e-setup.ps1` (run by `e2e.ps1`) puts the copies in `%USERPROFILE%\Games\NGG-Test\{Host,Client}` (copied once,
about 740 MB each, without the real install's disabled plugins, logs and caches; no UnityExplorer) and on every run:

| What | Host | Client |
|---|---|---|
| Mod build | current Release build | same |
| `[Network] Transport / HostAddress / DirectPort` | `Direct`, `127.0.0.1`, `27250` (listens on loopback only) | same; joins `127.0.0.1:27250` |
| `[Debug] EnableTestHarness / DevControlPort` | `true`, `27300` | `true`, `27301` |
| `[Debug] SaveDirOverride` | `...\Host\Saves` | `...\Client\Saves` |
| `steam_appid.txt` | `3510740` | same |

The save folders are seeded once from a copy of your real saves, which are only read. `SaveDirOverride` redirects every
save path of SaveManager and SaveReader (the plugin refuses to start if it cannot redirect them all); it is the only
thing that keeps the copies off your saves, because Unity takes `persistentDataPath` and the PlayerPrefs registry key
from the game's compiled data (renaming the copy in `app.info` changes neither). So the copies' `Player.log` lands in
your save folder, and their window size and mode land in your PlayerPrefs (`HKCU\Software\Luke Muscat\Normal Golf
Game`): `e2e.ps1` exports that key to `NGG-Test\real-playerprefs.reg` before the first copy starts and restores it
once both copies quit (`-Stop`, or the next run), and refuses to run while the real game runs. It hashes your real save
folder before and after the run (must be unchanged) and the Client copy's save folder (a client's own save must not
change while it plays on the host's save). Copies run windowed (1280x720) and keep running without focus
(`Application.runInBackground`).

Dev control API (`[Debug] DevControlPort`, only with the harness on, 127.0.0.1 only, GET, JSON answers):

| Path | What |
|---|---|
| `/status` | service, state, role, players, Unity scene, save folder, and the harness's `scene` report. |
| `/cmd?line=<command>` | any harness command (2.3, 2.6), queued like a line of the command file; answered with its result once it ran. |
| `/screenshot?name=` | a PNG in the copy's `BepInEx/screenshots`; answered with the file once it is on disk. |
| `/host`, `/join?host=&port=`, `/leave` | the session (Direct transport; with Steam, `/join?lobby=`). |
| `/quit` | leaves the session, then quits like the harness's `quit`. |

e.g. `curl "http://127.0.0.1:27301/cmd?line=key%20H%202"` holds the Client's scorecard key for 2 s. To play both copies
at once (walk, interact, bounties, shots) use the commands of 2.8, which need no focus.

The Direct transport also works between two PCs on a trusted LAN: set `Transport = Direct` on both, `HostAddress`
to the host's LAN address (the host then listens on every interface; open UDP `DirectPort` in its firewall). It has no
lobby, invites or encryption: anyone who reaches the port can join.

### 2.8 Playing a copy through commands, and the play scenarios

Two copies cannot both have focus, and the Input System drops the keys of an unfocused window, so `key` cannot play
both. These commands play the **local player** of the copy that runs them through the game's own code (no keys,
no mouse, no focus) and work from the command file and `/cmd` alike. They answer `not available ...` outside a copy
with the harness, and `cannot ...: not in the world` in the menus.

| Command | What it does | Expected result |
|---|---|---|
| `find <category> [text]` | Lists objects of one category whose id, name or path contains the text. Categories: `object` (any active transform; id `so:<guid>` when it has its own SaveableObject), `interactable` (FPSInteractable, id as the mod's interaction id), `tee` (StandHere), `pad` (BountyPad, also inactive ones; id = clean set name; detail `open`/`completed`, `locked`, `inactive`), `trigger` (DialogueTrigger), `lock` (gates, doors and sliding doors of ObjectStates, plus Padlock / RFID / CutServer / OpenBunker interactables), `remote` (the mod's remote players and balls). Aliases: `bounty` = pad, `dialogue` = trigger, `door`/`gate` = lock, `player` = remote. | `N object(s):` then `n. [pad] 'BountyPad' id Pond open at (x, y, z) (Main:...)` (at most 40 listed). |
| `goto [category] <text> [@n]` | Teleports the walking player beside the object: a pad onto its `m_playerPosition` facing like the pad; a tee or trigger into its trigger box; anything else 1.2 m outside its bounds, facing it, on the ground below. Through ECM2 `Character.TeleportPosition` + `SetRotation`; without it the FPS controller is switched off for one frame while the transform moves. Matching like `click`: exact id, exact name, part of the id, part of the name, part of the path; the first tier with matches decides and several matches are ambiguous unless `@n` picks one. Category defaults to any usable (interactable, tee, pad, trigger), then `object`. | `moved beside it, facing it: [cat] '...' -> feet (x, y, z) via ... (was ..., N m away)`. Refused in golf mode (`mode walk` first) and while the move is locked (dialogue). |
| `goto <x> <y> <z>` | Teleports the feet to that point (the y is snapped to the ground below). | `moved to feet (...) via ... (was (...))`. |
| `interact [category] <text> [@n]` | Uses it as the player would: `FPSInteractable.Interact()`; a tee `StandHere.Activate()`; a pad its StartBounty interactable (else `BountyPad.StartBounty()`); a trigger `OnTriggerEnter` with the player's collider; a gate/door from `locks` its padlock. Works at any distance (a note says how far it was): `goto` first to test it like a player. | `FPSInteractable.Interact() on <type> <id>; now ...` (mode, move lock), `StandHere.Activate() on tee ...`, `... (StartBounty) on pad ...`. |
| `bounty start <set>` / `bounty state` / `bounty exit` | Starts the bounty of that clean set name at its active pad (`BountyPad.UsePad`); reports `activeBounty`, `loadedSet`, `mainSet`, `currentChallenge`, completed bounties, `bank`, player mode; leaves through `ExitBountyChallenge(false)`. | `bounty state` lines `activeBounty Pond`, ... |
| `mode golf\|walk` | `GolfTransition` / `FPSTransition` like the club key. | `mode Golf`. |
| `shoot [power] [angle] [ShotType]` | One swing of the club in hand: sets the swing panel's power (`m_power` = power x the club's `powerVisualMult`; 0..1, default 0.7) and the spin angle (-90..90, only if given), then calls `ClubSwing.HandleShot` (the end of a real swing; `HitSequence` as fallback). Needs golf mode and an unplayed ball. `shoot next` / `shoot again` = the game's `Restart(true/false)` (next ball / retake). | `shot through ClubSwing.HandleShot(Good, 1): club ..., m_power 7 = 0.7 of powerVisualMult 10, ...`. `this stroke was played already ...` until `shoot next`. |
| `locks [text]` | Every lock and its state: `gate <id>: OPEN/LOCKED`, doors, sliding doors (`CheckRFIDDoorAlreadyOpen`), padlocks (`active (usable)` / `inactive (used)`, which gate they open), bounty pads (locked or not), and the run's crowbar / card / server / bunker flags. | `locks: X open, Y locked ...`. Compare both copies: they must agree. |
| `dialogue [state]` / `dialogue next` / `dialogue triggers [text]` | Dialogue / cutscene state: `moveLocked`, player mode, the DialogueManager's flags, video players playing, active dialogue triggers, `timeScale`. `next` presses the dialogue's next button. | `moveLocked True` while a dialogue or cutscene runs. |
| `remotes` | Each remote player / ball: shown or HIDDEN, position, distance, renderers, trail and line renderers (emitting or not), and `DANGER` if one has a collider or rigidbody. | `remotes: N` ... `trails: T, drawing: D`. |
| `player` | Local feet, facing, mode, club, the ball (hit, stroke, moving, own trail), bank. | `feet (...)`, `ball ...`. |
| `match status\|card\|start\|resume\|cup\|pickup` | The competitive nine holes (GAME_INTEGRATION 7.8). `status`: the local round (`inLMUGC`, the game's scores, the ball's stroke and `next-shot ready`, mode, `inMatch`, `mayPlay`, `holding-next-tee`, the wait reason) and the match (`match <id> Turns Playing hole 3/9 (par 3) turn <name> seq <n>`, then one line per player: position, name, `(you)`, to par, thru, total, `[3,4,~1,-,...]`: `~n` = n strokes on the open hole, `n*` picked up, `m` missed). `card`: the match only. `start`: the round machine's StartLMUGC button (a client asks the host). `resume`: the ball's Next Shot (the turn gate applies). `cup`: drops the ball into the current hole's cup through `BallTarget.OnTriggerEnter` (scores the strokes so far + 1, like the holing swing; refused with `no stroke on hole N yet` while the ball waits on the tee for its Next Shot). `pickup`: gives up the hole. | `match 7 Turns Playing hole 1/9 (par 3) turn ...`; out of turn `resume` says `refused: <name>'s turn`. |

`scripts/e2e-scenarios.ps1` plays scenarios on the two running copies of 2.7 (start them first with `scripts/e2e.ps1`,
or pass `-Start`):

```powershell
scripts/e2e-scenarios.ps1                     # all scenarios a..e
scripts/e2e-scenarios.ps1 -Only a,d           # some of them
scripts/e2e-scenarios.ps1 -LockA <id> -BountyA Pond -BountyB <set> -Cutscene "trigger <name>"
```

It reads each copy's dev API port from its `[Debug] DevControlPort` (`e2e-setup.ps1` writes 27300 / 27301; `-HostPort`,
`-ClientPort` override), checks that the Host hosts, the Client is in the session and both worlds are ready, and writes
`<TestDir>\scenarios\<yyyyMMdd-HHmmss>\`: `results.md` (every command, its answer and the verdicts), `results.json`
and PNGs (both windows side by side, and each one). Exit code 1 if any verdict is FAIL.

| Scenario | What it does | PASS when |
|---|---|---|
| a. locks | Client walks to a usable padlock / RFID reader (from the Host's `locks`, or `-LockA`) and interacts; then the Host does the same with another (`-LockB`). | The other copy's `locks` shows it OPEN within 16 s (and so does the one that opened it). Padlocks need the crowbar (`m_crowbarUnlocked`, shared): without it the game shows its "locked" line and the verdict says so. |
| b. same bounty | Both `goto pad` + `bounty start` of the same set, take two strokes each. | Both report it as `activeBounty` and the shots fire; the Host's main set is the same afterwards. |
| c. different bounties | Host starts `-BountyA`, Client `-BountyB`, one stroke each. | Each keeps its own `activeBounty` before and after the stroke. |
| d. cutscene | Host walks into the first active dialogue trigger (or a padlock; `-Cutscene`), Client walks 4 m and shoots meanwhile; then the Host's dialogue is advanced with `dialogue next`. | The Host is `moveLocked`, the Client is not and walks and shoots, and the Host is free again after. INCONCLUSIVE if no cutscene started. |
| e. remote ball trail | The watcher stands beside the shooter, who shoots; the watcher's `remotes` is read three times during the flight. | The watcher's ghost ball has a trail renderer that draws. |

`scripts/e2e-match.ps1` plays a competitive nine-hole round on both copies (start them in Just Play Nine:
`scripts/e2e.ps1 -Nine`, or pass `-Start`): the Client presses the round machine, the copy out of turn tries to
swing, then the copies play in the order the host gives (real shots on the first `-ShotHoles` holes, then `match
cup`; the Client picks up on `-PickUpHole`), and both must end with the same final card. Results in
`<TestDir>\match\<stamp>\` like the scenarios below.

Then: the Client's `savecheck`, the real save folder (hashed before and after, must not change), the Client copy's
own saves (must not change) and, for the record, which Host copy saves changed.

Notes:
- These scenarios change the **Host copy's** save (opened padlocks, a consumed dialogue trigger, bounty attempts).
  That is the test copy's own folder, never your real one; for fresh saves delete `<TestDir>\Host\Saves` and
  `<TestDir>\Client\Saves` and run `scripts/e2e.ps1` again (it seeds them from your real saves).
- A used padlock stays used, so a later run picks the next one, until none is left (a: SKIPPED).
- e needs `[Presence] RemoteBallTrail = true` (the default) on the watcher. `trails: 0` means the trail is off or
  the patcher disabled feature `pr.trail` after errors (see the watcher's log); `trails: 1, drawing: 0` during the
  flight means the trail exists but never emitted. Check the PNGs for the colour and shape (P5b).
- The scenarios are the scripted part of the play-test rows: a = G20 (locks by either player), b = G17 (same bounty),
  c = G19 (different bounties), d = G15 (cutscene; the hold itself shows in the Host's log as `Holding ...`), e = P5b.
  The rest of those rows (payment once, quit to menu, held track step, colours) still needs a look by hand.

## 3. Two-player play test (both roles)

Two PCs with the same game and mod version, two Steam accounts that are friends. **Host** = PC A, **Client** = PC B.
Before **every** launch on **both** PCs: backup and hash (1.1). After every exit: hash and compare (client
protected files must be identical). Keep `LogOutput.log` from both PCs for every failed step.

For each step: do it, then check the *expected* column on both screens. A step that touches progress is checked on
the host's screen **and** the client's.

### 3.1 Session and lifecycle

| # | Step | Expected |
|---|---|---|
| L1 | Both start the game. F7, Menu2 *Multiplayer* button, pause-menu *Multiplayer* button. | The window opens each way; IMGUI buttons do not overlap the game's menus (#136). |
| L2 | A: *Host game* from Menu2. B: join by invite, then (another run) by *Join Game* in the Steam friends list, then by pasted lobby id. | B shows the progress bar, then the waiting panel ("waiting for the host"). B's log: local backup written to `BepInEx/cache/NGGMultiplayer/LocalBackup/<time>/`. |
| L3 | A: start Story. | B follows into Main at A's position with A's progress (money, unlocks, track). B's *Play* buttons are blocked. |
| L4 | A: pause menu *Save and Quit* (back to Menu2), then start Just Golf. | B follows to Menu2, then into Just Golf. |
| L5 | B: join while A is already in the world; B starts from B's own running game. | B's own progress is saved first, then B follows A. |
| L6 | B: open the pause menu, its settings submenu, close both. | A's game keeps running; B's input stops while paused and comes back; B is not frozen for A. |
| L7 | B: *Leave*. | B returns to Menu2 with B's **own** save (B's own money and track in Story). After exit: B's hashes identical. |
| L8 | A: *Leave* while B is in the world. | The session ends for B; B returns to its own save. |
| L9 | Pull B's network cable (or kill B's game) mid-session. | A drops B after `TimeoutSeconds`. Restart B: B's own save is intact (hashes identical; `restore-log.txt` explains any restore). |
| L10 | A: reach an ending (true ending / credits). | B follows into the credits, then to Menu2 with its own save. B cannot start an ending itself (toast). |
| L11 | Mismatched mod versions (if available). | Refused with a message naming both versions. |

### 3.2 Presence

| # | Step | Expected |
|---|---|---|
| P1 | Walk around, crouch, jump. | Each sees the other's avatar smoothly at the right height; top-right list shows walking. |
| P2 | Take shots with each club, including putts and a miss. | Ghost ball flies on the other screen, as big as the local ball at the same distance; the player list shows the stroke (1 for the first shot). No notice per shot; holing out shows one at the top. The avatar stands beside the ball. |
| P3 | Hole out. | Holed toast for the other player. |
| P4 | Ghost and avatar collisions. | Your ball passes through the other avatar and ghost ball; walking into the avatar does nothing. **Walk B's avatar into Luke (the NPC that ends the game on contact)**: no ending starts on A or B. |
| P5 | Ball skins. | The other player's ghost ball uses their skin; the avatar tint looks right (#130, #131). |
| P5b | Ghost ball trail: A takes a long drive, a putt, then resets the ball / changes hole. | On B, A's ghost draws a smooth trail in A's colour (the colour of A's avatar and name tag) shaped like B's own ball trail; it fades a moment after the ball stops; the next shot starts a fresh trail; no streak across the map on reset, hole change or after A's ball was holed. Log once: `Ghost ball trail: the game's Ball.m_trail look (...)`. `[Presence] RemoteBallTrail = false` removes it at once; `TrailSeconds` changes its length. |
| P6 | Wind (after wind unlock). | B's wind panel shows A's wind; it does not jump when a bounty starts or ends. |
| P7 | LMUGC / Just Golf round, both playing. | Hold H (`[General] ScorecardKey`): the shared round scorecard lists both players' holes and totals (unplayed holes count par + 2). H does nothing else in the game (`check tab`; the game binds Tab, #67). |
| P7b | Just Play Nine, competitive (`[Match] Competitive = true`): B presses the round machine. | The round starts for both on hole 1. The turn banner says whose shot it is (green "Your shot" for you); out of turn the swing, Next Shot and Retake do nothing but a notice. After the tee shots the player farthest from the cup plays. A holed player waits ("Waiting for B to finish hole 1"); the ball goes to the next tee only when both are done (4 s break with the card shown). Honours on the next tee. Hold H: the group card (pars, both rows, colours like the game's card, totals, +/-). The player list ranks by to par. After hole 9 both show the final card and the winner line for 20 s. That is with `[Match] TurnOrder = Turns`; with `Free` (the default) both play the hole at once, no turn banner, holes still wait. Story's LMUGC stays co-op. |
| P8 | A opens the pause menu. | B keeps playing; A's avatar stays visible on B; A's game is not frozen for B. |

### 3.3 Progression (shared host save)

| # | Step | Expected |
|---|---|---|
| G1 | B completes the current main-track challenge. | A and B both advance one step, with the challenge's story effect (dialogue only on B; its results on both). |
| G2 | B completes several track steps quickly, or joins A when A is several steps ahead. | B catches up step by step; no skipped story effects; log shows `Shared track: ...` once. |
| G3 | B completes a bounty (also once on easy). | Pads complete on both; reward paid once into the shared bank (A's balance); bounty boards update. While A is inside that same bounty, A's pad completes when A exits it. |
| G4 | Money: B collects cash. | A's bank rises by exactly the pickup's value; the cash is gone on both. Both try the same pickup at once: one gets it, the other sees "Already taken". |
| G5 | Shop: B buys a club, then the LMUGC ticket, then an assist (mulligan / no wind / hole skip); then tries to buy with too little money. | Each costs exactly the price once from the shared bank; the club stays gone for both; with too little money it is refused ("Not enough money"). Picking up the crowbar or the card changes no money. |
| G6 | Unlocks reached by B: stance, practice, aim, spin, wind, putter (club panel), driver, wedge, hybrid. | Both get the panel/club; no double wind cycles (wind does not re-roll twice); no double particle effects on replays. |
| G7 | World: padlocks, gates, doors, sliding / RFID doors, server cut and restore, bunker screen, elevator. | Same state on both; the elevator moves only the rider; rejoin later: state is kept. |
| G8 | Story beats B triggers (shop opening, bar door, marketing department, teleporter, DeductMoney/ReturnMoney). | The dialogue plays only on B; doors, money and unlocks arrive on A; DeductMoney/ReturnMoney change money once. |
| G9 | Puzzles: code door (BO_Code), trivia board. | Same code and trivia order on both. |
| G10 | Ball unlock (host's true ending path). | A's ball choice is kept; unlocks saved only on A. |
| G11 | LMUGC best score (Story, hole 9). | A's best scores update with the game's rule; B's own `run2.txt` unchanged. |
| G12 | Just Golf round by A. | A's scorecard gets the round (`scoreCard.txt` on A only). |
| G13 | Achievements. | Each unlocks only what they did themselves; B does not get A's progress achievements. |
| G14 | Rejoin: B leaves and joins again later. | B sees all progress made meanwhile. |
| G15 | Cutscenes: A walks into a dialogue (or opens a padlock / cuts the server / rides the elevator) while B plays a shot or walks; then B completes a main-track challenge or triggers a story beat while A is still in that dialogue. | B is never frozen, teleported or shown A's dialogue; B's HUD shows A "In a cutscene"; doors / gates move on B at once. On A the beat and the track step wait (log `Holding ...`) and arrive about 1.5 s after the dialogue ends, without a second dialogue or a teleport on A (log `Skipped ...` if the challenge had one). Same with B mid-shot: the step lands after B's ball has stopped. |
| G16 | Host in a cutscene: A in a dialogue when B finishes the main challenge; B leaves or joins mid-dialogue of A. | B advances at once (toast on A); A's track catches up after the dialogue and A's `run2.txt` has it after A's next save. Joining / leaving never breaks A's dialogue. |
| G17 | Same bounty: A and B start the same pad; B finishes first, then A finishes too. | B's completion pays once (A's bank shows it); A keeps playing (not thrown out); A's own finish exits normally and the log says `already paid for another player's completion; took back <n>`; the bank ends +1 reward. Pads complete on both. Again with A first, then B. Again with B first and A walking off the pad instead: A's pad completes when A leaves (log: `... recorded when the host leaves it`). |
| G18 | Same bounty, B first, then A quits to the menu from inside the bounty and loads Story again. | The bounty is completed on A's board after the reload (log: `Quitting to the menu: wrote 1 bounty entry ...`); no second payment. |
| G19 | Different bounties at once: A plays one pad, B another; both finish (either order); also B in an LMUGC round while A plays a bounty. | Neither bounty is reset or exited by the other's progress or completion; A's main-track targets/decorations stay visible while B is in a bounty (log on B: `Holding back SaveableObject ...` when its bounty start hides some); both rewards paid once; both pads complete on both. |
| G20 | Locks by either player: B opens a padlock (crowbar picked up by A), an RFID door (card found by A), the bunker, the server; A opens the others. | Each opens on both at once (animation on the other), the cutscene plays only for the opener; after A restarts the game the doors are still open; B's own files unchanged (S1). |

### 3.4 Save safety (every run)

| # | Check | Expected |
|---|---|---|
| S1 | Client hashes before launch vs after exit (1.1). | Protected files identical. |
| S2 | Client: open *Settings*, try *Delete story save* / *Delete everything* while in a session. | Blocked on the client; nothing deleted. |
| S3 | Client: change a setting and a key binding in session. | Only `config.txt` / PlayerPrefs change (your own settings). |
| S4 | Client quits the game from the pause menu or Alt+F4 mid-session. | No save on quit; next start the client's own progress is intact. |
| S5 | Client's log after leaving. | `Own save verified unchanged.` and `own save in place`. Otherwise a restore: `.changed-during-session` files next to the backup and an entry in `restore-log.txt` (report it). |
| S6 | Host: backups and hashes. | The host's save holds the session progress; nothing written into other folders except `BepInEx/cache/NGGMultiplayer/`. |

### 3.5 Report

For a failure: step number, role, what happened, both `LogOutput.log` files, the backup folder of that run with its
`hashes.txt` before/after, and (for the harness) `ngg_mp_results.txt`.
