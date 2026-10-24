# End-to-end run on one PC with the two test copies from e2e-setup.ps1: quits any running test copy (through its dev
# API, never by killing other processes), installs the current build, starts Host, plays Story (the main menu's Play
# continues the copy's save) and hosts over localhost, then starts Client and joins. Takes a screenshot of each and puts
# them side by side in <TestDir>\shots. Leaves both running for more tests (curl http://127.0.0.1:27300/status, :27301).
#   -Stop      only quit the test copies.
#   -HostOnly  stop after Host is hosting.
#   -Reuse     keep copies that are already running (no reinstall).
# Checks on the way: your real save folder is hashed before and after (it must not change), and so is the Client copy's
# own save folder (a client's own save must not change while it plays on the host's).
# Unity PlayerPrefs: the copies have the real game's company and product name (compiled into the game's data, not
# app.info), so their window size and mode land in your registry key HKCU\Software\Luke Muscat\Normal Golf Game. The
# key is exported to <TestDir>\real-playerprefs.reg before a copy starts and restored once both copies quit (-Stop, or
# the next run). The real game must not run at the same time (refused).
param(
    [string]$TestDir = "$env:USERPROFILE\Games\NGG-Test",
    [string]$RealSaveDir = "$env:USERPROFILE\AppData\LocalLow\Luke Muscat\Normal Golf Game",
    [int]$Width = 1280,
    [int]$Height = 720,
    [switch]$Stop,
    [switch]$HostOnly,
    [switch]$Reuse
)
$ErrorActionPreference = "Stop"
$api = @{ Host = "http://127.0.0.1:27300"; Client = "http://127.0.0.1:27301" }
$exeName = "Normal Golf Game.exe"
$prefsKey = "HKCU\Software\Luke Muscat\Normal Golf Game"
$prefsBackup = Join-Path $TestDir "real-playerprefs.reg"
$prefsAbsent = Join-Path $TestDir "real-playerprefs.absent"

# Your PlayerPrefs before the first copy starts; an export left from a run that was never restored is kept (the key
# may hold the copies' values by now).
function Save-RealPrefs {
    if ((Test-Path $prefsBackup) -or (Test-Path $prefsAbsent)) { return }
    New-Item -ItemType Directory -Force $TestDir | Out-Null
    reg export $prefsKey $prefsBackup /y 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) { Set-Content $prefsAbsent "" }
    $global:LASTEXITCODE = 0
}

function Restore-RealPrefs {
    if (Test-Path $prefsBackup) {
        reg delete $prefsKey /f 2>$null | Out-Null
        reg import $prefsBackup 2>$null | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Restoring your PlayerPrefs from $prefsBackup failed; import it by hand (double click)." }
        Remove-Item $prefsBackup
        Write-Host "Your PlayerPrefs ($prefsKey) are restored."
    } elseif (Test-Path $prefsAbsent) {
        reg delete $prefsKey /f 2>$null | Out-Null
        Remove-Item $prefsAbsent
    }
    $global:LASTEXITCODE = 0
}

function Get-Api([string]$url, [int]$timeout = 10) {
    try { return Invoke-RestMethod -Uri $url -TimeoutSec $timeout } catch { return $null }
}

function Invoke-Cmd([string]$name, [string]$line) {
    $r = Get-Api "$($api[$name])/cmd?line=$([uri]::EscapeDataString($line))" 70
    if (-not $r) { throw "$name did not answer '$line'." }
    if ($r.error) { throw "$name '$line': $($r.error)" }
    return $r.result
}

function Get-TestProcess([string]$name) {
    Get-Process -Name "Normal Golf Game" -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$TestDir\$name\*" }
}

function Stop-Copy([string]$name) {
    if (-not (Get-TestProcess $name)) { return }
    Get-Api "$($api[$name])/quit" | Out-Null
    for ($i = 0; $i -lt 60 -and (Get-TestProcess $name); $i++) { Start-Sleep 1 }
    if (Get-TestProcess $name) { throw "The $name test copy didn't quit; close it by hand." }
    Write-Host "$name quit."
}

function Wait-Until([string]$what, [scriptblock]$cond, [int]$seconds) {
    for ($i = 0; $i -lt $seconds; $i += 2) {
        $r = & $cond
        if ($r) { return $r }
        Start-Sleep 2
    }
    throw "Timed out waiting for: $what"
}

Add-Type -Namespace NggE2e -Name Win -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
'@

function Start-Copy([string]$name, [int]$x) {
    $dir = Join-Path $TestDir $name
    if (-not (Get-TestProcess $name)) {
        Start-Process -FilePath (Join-Path $dir $exeName) -WorkingDirectory $dir `
            -ArgumentList "-screen-fullscreen", "0", "-screen-width", $Width, "-screen-height", $Height | Out-Null
    }
    Wait-Until "$name main menu" {
        $s = Get-Api "$($api[$name])/status" 5
        if ($s -and $s.unityScene -eq "Menu2") { return $s }
    } 180 | Out-Null
    # Side by side: Host on the left, Client to its right (SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE).
    $p = Get-TestProcess $name | Select-Object -First 1
    if ($p -and $p.MainWindowHandle -ne 0) { [NggE2e.Win]::SetWindowPos($p.MainWindowHandle, [IntPtr]::Zero, $x, 30, 0, 0, 0x15) | Out-Null }
    Write-Host "$name at the main menu."
}

function Get-SaveHashes([string]$dir) {
    if (-not (Test-Path $dir)) { return @{} }
    $h = @{}
    Get-ChildItem $dir -File | Where-Object { $_.Name -notlike "Player*.log" } | ForEach-Object { $h[$_.Name] = (Get-FileHash $_.FullName).Hash }
    return $h
}

function Compare-Hashes([string]$what, $before, $after) {
    $names = @($before.Keys) + @($after.Keys) | Sort-Object -Unique
    $changed = @($names | Where-Object { $before[$_] -ne $after[$_] })
    if ($changed.Count -eq 0) { Write-Host "$what unchanged ($($names.Count) files)." }
    else { Write-Warning "$what CHANGED: $($changed -join ', ')" }
    return $changed
}

if (-not $Reuse -or $Stop) {
    Stop-Copy "Client"
    Stop-Copy "Host"
    Restore-RealPrefs
    if ($Stop) { return }
    & "$PSScriptRoot\e2e-setup.ps1" -TestDir $TestDir -RealSaveDir $RealSaveDir
}

$real = Get-Process -Name "Normal Golf Game" -ErrorAction SilentlyContinue | Where-Object { $_.Path -notlike "$TestDir\*" }
if ($real) { throw "The real game is running ($($real[0].Path)); quit it first (it shares PlayerPrefs and Player.log with the copies)." }
Save-RealPrefs
$realBefore = Get-SaveHashes $RealSaveDir
$clientSaves = Join-Path $TestDir "Client\Saves"
$clientBefore = Get-SaveHashes $clientSaves

Start-Copy "Host" 0
Invoke-Cmd "Host" "click Play" | Out-Null
Wait-Until "Host in the world" {
    $s = Get-Api "$($api.Host)/status" 5
    if ($s -and $s.scene -match "world ready") { return $s }
} 120 | Out-Null
Start-Sleep 3
$h = Get-Api "$($api.Host)/host"
if (-not $h.ok) { throw "Host didn't start hosting: $($h.status)" }
Write-Host "Hosting: $($h.status)"
if ($HostOnly) { return }

Start-Copy "Client" ($Width + 16)
# The port e2e-setup.ps1 gave the pair (-DirectPort), from the Client copy's config.
$directPort = 27250
$clientCfg = Join-Path $TestDir "Client\BepInEx\config\ngg.multiplayer.cfg"
$m = if (Test-Path $clientCfg) { Select-String -Path $clientCfg -Pattern '^\s*DirectPort\s*=\s*(\d+)' | Select-Object -First 1 }
if ($m) { $directPort = [int]$m.Matches[0].Groups[1].Value }
$j = Get-Api "$($api.Client)/join?host=127.0.0.1&port=$directPort"
if (-not $j.ok) { throw "Client couldn't join: $($j.status)" }
Wait-Until "Client in the host's world" {
    $s = Get-Api "$($api.Client)/status" 5
    if ($s -and $s.state -eq "InSession" -and $s.scene -match "world ready") { return $s }
} 180 | Out-Null
Write-Host "Client joined and is in the world."
Start-Sleep 5

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$hostShot = (Get-Api "$($api.Host)/screenshot?name=e2e_host" 20).file
$clientShot = (Get-Api "$($api.Client)/screenshot?name=e2e_client" 20).file
if (-not $hostShot -or -not $clientShot) { throw "A screenshot failed (host '$hostShot', client '$clientShot')." }
$shots = Join-Path $TestDir "shots"
New-Item -ItemType Directory -Force $shots | Out-Null
Add-Type -AssemblyName System.Drawing
$a = [System.Drawing.Image]::FromFile($hostShot)
$b = [System.Drawing.Image]::FromFile($clientShot)
try {
    $both = New-Object System.Drawing.Bitmap ($a.Width + $b.Width), ([Math]::Max($a.Height, $b.Height))
    $g = [System.Drawing.Graphics]::FromImage($both)
    $g.DrawImage($a, 0, 0, $a.Width, $a.Height)
    $g.DrawImage($b, $a.Width, 0, $b.Width, $b.Height)
    $font = New-Object System.Drawing.Font "Arial", 18, ([System.Drawing.FontStyle]::Bold)
    $g.FillRectangle([System.Drawing.Brushes]::Black, 0, 0, 90, 32)
    $g.DrawString("HOST", $font, [System.Drawing.Brushes]::White, 6, 2)
    $g.FillRectangle([System.Drawing.Brushes]::Black, $a.Width, 0, 110, 32)
    $g.DrawString("CLIENT", $font, [System.Drawing.Brushes]::White, $a.Width + 6, 2)
    $g.Dispose()
    $out = Join-Path $shots "${stamp}_host_and_client.png"
    $both.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
    $both.Dispose()
} finally { $a.Dispose(); $b.Dispose() }
Copy-Item $hostShot, $clientShot $shots
Write-Host "Screenshots: $out"

$hs = Get-Api "$($api.Host)/status"
$cs = Get-Api "$($api.Client)/status"
Write-Host "Host:   $($hs.state), players: $(($hs.players | ForEach-Object { $_.name }) -join ', ')"
Write-Host "Client: $($cs.state), players: $(($cs.players | ForEach-Object { $_.name }) -join ', ')"
Compare-Hashes "Real save folder ($RealSaveDir)" $realBefore (Get-SaveHashes $RealSaveDir) | Out-Null
Compare-Hashes "Client's own saves ($clientSaves)" $clientBefore (Get-SaveHashes $clientSaves) | Out-Null
Write-Host "Both running: $($api.Host)/status and $($api.Client)/status (scripts/e2e.ps1 -Stop quits them)."
