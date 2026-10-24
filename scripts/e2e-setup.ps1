# Sets up two test copies of the game for end-to-end tests on one PC: Host and Client, outside the Steam folder, so the
# real install, its saves and its settings are never touched. Each copy gets:
#   - the current Release build of the mod (built against the real install's Managed folder),
#   - [Network] Transport = Direct over localhost (HostAddress 127.0.0.1, DirectPort 27250 or -DirectPort),
#   - the test harness with the dev control API (Host http://127.0.0.1:27300/, Client :27301, or -HostPort/-ClientPort; src/NGGMultiplayer/Testing/DevControl.cs),
#   - [Debug] SaveDirOverride = <copy>\Saves, seeded once from a copy of your real save folder (read only); this is
#     what keeps the copies off your saves (Unity's persistentDataPath of a copy is still your real save folder; only
#     its Player.log lands there),
#   - steam_appid.txt (3510740): started outside Steam, the game initializes Steam itself instead of relaunching
#     through Steam (which would start the real install).
# No UnityExplorer and no other plugins of the real install go into the copies. Re-run after every mod change: copying
# the game (about 740 MB each) only happens the first time.
param(
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Normal Golf Game\Normal",
    [string]$TestDir = "$env:USERPROFILE\Games\NGG-Test",
    [string]$RealSaveDir = "$env:USERPROFILE\AppData\LocalLow\Luke Muscat\Normal Golf Game",
    # Dev control API ports of the Host and Client copies, and the Direct transport port; change them to run a second
    # pair of copies next to the first.
    [int]$HostPort = 27300,
    [int]$ClientPort = 27301,
    [int]$DirectPort = 27250,
    [switch]$NoBuild
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$exeName = "Normal Golf Game.exe"
$managed = Join-Path $GameDir "Normal Golf Game_Data\Managed"

if (-not $NoBuild) {
    dotnet build "$root\src\NGGMultiplayer\NGGMultiplayer.csproj" -c Release --nologo -v q "-p:GameManagedDir=$managed" `
        -p:RequireGameSteamworks=true -p:RequireGameAssemblies=true
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed." }
}
$out = "$root\src\NGGMultiplayer\bin\Release\netstandard2.1"

$copies = [ordered]@{ Host = @{ Port = $HostPort }; Client = @{ Port = $ClientPort } }
foreach ($name in $copies.Keys) {
    $dst = Join-Path $TestDir $name
    if (Get-Process -Name "Normal Golf Game" -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$dst\*" }) {
        throw "The $name test copy is running; quit it first (scripts/e2e.ps1 -Stop, or http://127.0.0.1:$($copies[$name].Port)/quit)."
    }
    if (-not (Test-Path (Join-Path $dst $exeName))) {
        Write-Host "Copying the game to $dst (once)..."
        # The real install's own test files, logs, caches and disabled plugins stay behind.
        robocopy $GameDir $dst /E /NFL /NDL /NJH /NJS /NP /R:1 /W:1 `
            /XD (Join-Path $GameDir "BepInEx\plugins-disabled") (Join-Path $GameDir "BepInEx\screenshots") (Join-Path $GameDir "BepInEx\cache") `
            /XF ngg_mp_commands.txt ngg_mp_results.txt LogOutput.log com.sinai.unityexplorer.cfg | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "Copying the game failed (robocopy $LASTEXITCODE)." }
        $global:LASTEXITCODE = 0 # robocopy's 1..7 mean success
    }
    Set-Content (Join-Path $dst "steam_appid.txt") "3510740" -NoNewline
    # Undo an earlier version of this script that renamed the copy in app.info (Unity does not take the save folder or
    # the PlayerPrefs key from it).
    Copy-Item (Join-Path $GameDir "Normal Golf Game_Data\app.info") (Join-Path $dst "Normal Golf Game_Data\app.info") -Force

    # Only our plugin.
    $plugins = Join-Path $dst "BepInEx\plugins"
    Get-ChildItem $plugins -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -ne "NGGMultiplayer" } |
        ForEach-Object { Remove-Item $_.FullName -Recurse -Force }
    $plugin = Join-Path $plugins "NGGMultiplayer"
    New-Item -ItemType Directory -Force $plugin | Out-Null
    Copy-Item "$out\NGGMultiplayer.dll", "$out\NGGMultiplayer.Core.dll", "$out\NGGMultiplayer.pdb", "$out\NGGMultiplayer.Core.pdb" $plugin -Force
    # Remote players' directional cutouts.
    $avatar = Join-Path $plugin "avatar"
    New-Item -ItemType Directory -Force $avatar | Out-Null
    Copy-Item "$root\assets\avatar\*.png" $avatar -Force
    $female = Join-Path $avatar "female"
    New-Item -ItemType Directory -Force $female | Out-Null
    Copy-Item "$root\assets\avatar\female\*.png" $female -Force -ErrorAction SilentlyContinue
    # The female drawings over the player's own videos (Game/Presence/LookVideo.cs).
    if (Test-Path "$root\assets\video") { Copy-Item "$root\assets\video" $plugin -Recurse -Force }

    # The copy's own save folder, seeded once from the real one (copied from, never written to).
    $saves = Join-Path $dst "Saves"
    if (-not (Test-Path $saves)) {
        New-Item -ItemType Directory -Force $saves | Out-Null
        if (Test-Path $RealSaveDir) {
            Get-ChildItem $RealSaveDir -File | Where-Object { $_.Name -notlike "Player*.log" -and $_.Name -ne "steam_autocloud.vdf" } |
                Copy-Item -Destination $saves
        }
    }

    $cfg = Join-Path $dst "BepInEx\config\ngg.multiplayer.cfg"
    $text = if (Test-Path $cfg) { Get-Content $cfg -Raw } else { "" }
    function Set-Key([string]$t, [string]$section, [string]$key, [string]$value) {
        if ($t -match "(?m)^$key\s*=") { return ($t -replace "(?m)^$key\s*=.*$", "$key = $value") }
        if ($t -notmatch "(?m)^\[$section\]") { $t = $t.TrimEnd() + "`r`n`r`n[$section]`r`n" }
        return ($t -replace "(?m)^\[$section\]\s*$", "[$section]`r`n`r`n$key = $value`r`n")
    }
    $text = Set-Key $text "Network" "Transport" "Direct"
    $text = Set-Key $text "Network" "HostAddress" "127.0.0.1"
    $text = Set-Key $text "Network" "DirectPort" $DirectPort
    $text = Set-Key $text "General" "DisplayName" $name
    $text = Set-Key $text "Debug" "EnableTestHarness" "true"
    $text = Set-Key $text "Debug" "DevControlPort" $copies[$name].Port
    $text = Set-Key $text "Debug" "SaveDirOverride" $saves
    Set-Content $cfg $text -NoNewline
    Write-Host "$name ready: $dst (dev API http://127.0.0.1:$($copies[$name].Port)/, saves $saves)"
}
