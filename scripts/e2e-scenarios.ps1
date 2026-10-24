# Multiplayer play scenarios on the two running test copies (scripts/e2e.ps1 starts them: Host hosts Story, Client
# joins over localhost). Both copies are played through their dev control API (/cmd runs harness commands such as
# goto, interact, bounty, mode, shoot, locks, dialogue, remotes; docs/TESTING.md 2.8), so no focus, keyboard or mouse
# is needed, and every action goes through the game's own code (real gameplay, not bots).
#   a  locks:     Client unlocks a padlock, Host must see it open; Host unlocks another, Client must see it open.
#   b  same bounty: both start the same bounty at its pad and take shots in it.
#   c  different bounties: each starts another bounty at the same time.
#   d  cutscene:  Host triggers a dialogue / cutscene while Client walks and shoots.
#   e  ball trail: Client shoots next to Host; Host's view of Client's ball must draw a trail (and the other way).
# Results land in <TestDir>\scenarios\<yyyyMMdd-HHmmss>\: results.md (every command and answer, verdicts),
# results.json (verdicts), PNGs of both windows (side by side, plus each window's own). Your real save folder is
# hashed before and after (must not change), the Client copy's own saves too (must not change: it plays on the
# host's save), the Host copy's saves are reported (they hold the session's progress; changes allowed).
# NOTE: these scenarios change the Host copy's save (opened padlocks, a consumed dialogue trigger, bounty
# attempts): that is the test copy's own Saves folder, never your real one. A fresh copy of your real saves:
# delete <TestDir>\Host\Saves and <TestDir>\Client\Saves, then run scripts/e2e.ps1 (it seeds them again).
#   -Only a,c     run only those scenarios.     -Start   run scripts/e2e.ps1 -Reuse first (start / join as needed).
#   -LockA/-LockB <interact id> (from 'locks'), -BountyA/-BountyB <clean set name> (from 'find pad'),
#   -Cutscene <interact query> (default: the first active dialogue trigger, else a padlock).
param(
    [string]$TestDir = "$env:USERPROFILE\Games\NGG-Test",
    [string]$RealSaveDir = "$env:USERPROFILE\AppData\LocalLow\Luke Muscat\Normal Golf Game",
    [int]$HostPort = 0,
    [int]$ClientPort = 0,
    [string[]]$Only = @("a", "b", "c", "d", "e"),
    [string]$LockA,
    [string]$LockB,
    [string]$BountyA,
    [string]$BountyB,
    [string]$Cutscene,
    # e: power of the shot the trail is watched on (lower stays in the watcher's view)
    [double]$TrailPower = 0.8,
    [switch]$Start
)
$ErrorActionPreference = "Stop"
$Only = @($Only | ForEach-Object { $_ -split "," } | ForEach-Object { $_.Trim().ToLowerInvariant() } | Where-Object { $_ })

# ---- Setup ----------------------------------------------------------------------------------------------------

# The dev API port each copy was given by e2e-setup.ps1 ([Debug] DevControlPort), unless set here.
function Get-CopyPort([string]$name, [int]$given, [int]$fallback) {
    if ($given -gt 0) { return $given }
    $cfg = Join-Path $TestDir "$name\BepInEx\config\ngg.multiplayer.cfg"
    if (Test-Path $cfg) {
        $m = Select-String -Path $cfg -Pattern '^\s*DevControlPort\s*=\s*(\d+)' | Select-Object -First 1
        if ($m) { return [int]$m.Matches[0].Groups[1].Value }
    }
    return $fallback
}
$api = [ordered]@{
    Host   = "http://127.0.0.1:$(Get-CopyPort 'Host' $HostPort 27300)"
    Client = "http://127.0.0.1:$(Get-CopyPort 'Client' $ClientPort 27301)"
}

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$out = Join-Path $TestDir "scenarios\$stamp"
New-Item -ItemType Directory -Force $out | Out-Null
$md = Join-Path $out "results.md"
$verdicts = New-Object System.Collections.ArrayList
$shotNumber = 0
Set-Content $md "# NGG Multiplayer play scenarios $stamp`r`n`r`nHost $($api.Host), Client $($api.Client)`r`n" -Encoding UTF8

function Note([string]$text) {
    Write-Host $text
    Add-Content $md "$text`r`n" -Encoding UTF8
}

function Get-Api([string]$url, [int]$timeout = 10) {
    try { return Invoke-RestMethod -Uri $url -TimeoutSec $timeout } catch { return $null }
}

# One harness command on one copy; the answer is recorded and returned ("" when the copy did not answer).
function Send([string]$who, [string]$line) {
    $r = Get-Api "$($api[$who])/cmd?line=$([uri]::EscapeDataString($line))" 75
    $text = if (-not $r) { "(no answer)" } elseif ($r.error) { "ERROR: $($r.error)" } elseif ($r.queued) { "(still queued after 60 s)" } else { [string]$r.result }
    Add-Content $md "**$who** ``> $line```r`n``````r`n$text`r`n``````r`n" -Encoding UTF8
    Write-Host ("[{0}] > {1}`n    {2}" -f $who, $line, (($text -split "`n") -join "`n    "))
    return $text
}

function Both([string]$line) {
    return @{ Host = (Send "Host" $line); Client = (Send "Client" $line) }
}

function Verdict([string]$scenario, [string]$result, [string]$evidence) {
    [void]$verdicts.Add([pscustomobject]@{ scenario = $scenario; result = $result; evidence = $evidence })
    Note "**Verdict $scenario`: $result** - $evidence"
}

Add-Type -AssemblyName System.Drawing
# Screenshots of both windows now: each copy's PNG, and both side by side with labels.
function Shot([string]$label) {
    $script:shotNumber++
    $name = "{0:D2}_{1}" -f $script:shotNumber, $label
    $files = @{}
    foreach ($who in @("Host", "Client")) {
        $r = Get-Api "$($api[$who])/screenshot?name=$name" 20
        if ($r -and $r.file -and (Test-Path $r.file)) {
            $dst = Join-Path $out "${name}_$($who.ToLowerInvariant()).png"
            Copy-Item $r.file $dst -Force
            $files[$who] = $dst
        } else {
            Note "(screenshot of $who failed: $(if ($r) { $r.error } else { 'no answer' }))"
        }
    }
    if ($files.Host -and $files.Client) {
        $a = [System.Drawing.Image]::FromFile($files.Host)
        $b = [System.Drawing.Image]::FromFile($files.Client)
        try {
            $both = New-Object System.Drawing.Bitmap ($a.Width + $b.Width), ([Math]::Max($a.Height, $b.Height))
            $g = [System.Drawing.Graphics]::FromImage($both)
            $g.DrawImage($a, 0, 0, $a.Width, $a.Height)
            $g.DrawImage($b, $a.Width, 0, $b.Width, $b.Height)
            $font = New-Object System.Drawing.Font "Arial", 16, ([System.Drawing.FontStyle]::Bold)
            $g.FillRectangle([System.Drawing.Brushes]::Black, 0, 0, 420, 30)
            $g.DrawString("HOST  $label", $font, [System.Drawing.Brushes]::White, 6, 2)
            $g.FillRectangle([System.Drawing.Brushes]::Black, $a.Width, 0, 420, 30)
            $g.DrawString("CLIENT  $label", $font, [System.Drawing.Brushes]::White, $a.Width + 6, 2)
            $g.Dispose()
            $pair = Join-Path $out "$name.png"
            $both.Save($pair, [System.Drawing.Imaging.ImageFormat]::Png)
            $both.Dispose()
            Note "![${label}]($name.png)"
        } finally { $a.Dispose(); $b.Dispose() }
    }
}

function Get-SaveHashes([string]$dir) {
    $h = @{}
    if (Test-Path $dir) {
        Get-ChildItem $dir -File | Where-Object { $_.Name -notlike "Player*.log" } | ForEach-Object { $h[$_.Name] = (Get-FileHash $_.FullName).Hash }
    }
    return $h
}

function Get-Changed($before, $after) {
    $names = @($before.Keys) + @($after.Keys) | Sort-Object -Unique
    return @($names | Where-Object { $before[$_] -ne $after[$_] })
}

function Field([string]$text, [string]$key) {
    $m = [regex]::Match($text, "(?m)^\s*$([regex]::Escape($key)) (.*)$")
    if ($m.Success) { return $m.Groups[1].Value.Trim() }
    return ""
}

function ActiveBounty([string]$text) { return ((Field $text "activeBounty") -split " ")[0] }

function Is-Locked([string]$text) { return (Field $text "moveLocked") -eq "True" }

# Walking, out of any bounty: the start of every scenario.
function Reset-Player([string]$who) {
    $state = Send $who "bounty state"
    if ((ActiveBounty $state) -and (ActiveBounty $state) -ne "none") { Send $who "bounty exit" | Out-Null; Start-Sleep 3 }
    $p = Send $who "player"
    if ($p -match "(?m)^mode Golf") { Send $who "mode walk" | Out-Null; Start-Sleep 3 }
}

# A shot; a stroke that was played already is restarted first.
function Shoot([string]$who, [string]$shotArgs) {
    $r = Send $who "shoot $shotArgs"
    if ($r -match "played already") {
        Send $who "shoot next" | Out-Null
        Start-Sleep 2
        $r = Send $who "shoot $shotArgs"
    }
    return $r
}

# ---- Preconditions --------------------------------------------------------------------------------------------

if ($Start) { & "$PSScriptRoot\e2e.ps1" -TestDir $TestDir -RealSaveDir $RealSaveDir -Reuse }

$hs = Get-Api "$($api.Host)/status"
$cs = Get-Api "$($api.Client)/status"
if (-not $hs -or -not $cs) { throw "Host ($($api.Host)) or Client ($($api.Client)) does not answer: start them with scripts/e2e.ps1 (or pass -Start)." }
if (-not $hs.isHost -or $cs.state -ne "InSession" -or $hs.scene -notmatch "world ready" -or $cs.scene -notmatch "world ready") {
    throw "Both copies must be in the world in one session (Host hosting, Client joined): Host '$($hs.state)', Client '$($cs.state)'. Run scripts/e2e.ps1 (-Reuse)."
}
foreach ($who in @("Host", "Client")) {
    $help = Send $who "help"
    if ($help -notmatch "goto \[category\]") { throw "$who runs a build without the play commands: run scripts/e2e.ps1 (it installs the current build)." }
}
$realBefore = Get-SaveHashes $RealSaveDir
$clientSaves = Join-Path $TestDir "Client\Saves"
$hostSaves = Join-Path $TestDir "Host\Saves"
$clientBefore = Get-SaveHashes $clientSaves
$hostBefore = Get-SaveHashes $hostSaves
Note "Host: $($hs.state), players $(($hs.players | ForEach-Object { $_.name }) -join ', '); Client: $($cs.state)."
Send "Client" "savecheck reset" | Out-Null
Both "player" | Out-Null
Both "locks" | Out-Null
Both "bounty state" | Out-Null
Shot "start"

# ---- a: locks -------------------------------------------------------------------------------------------------

# Usable padlocks / RFID readers from Host's 'locks', the ones whose gate or door is still locked first.
function Get-LockCandidates {
    $text = Send "Host" "locks"
    $lockedIds = @{}
    foreach ($m in [regex]::Matches($text, "(?m)^\s*(?:gate|door|sliding) (\S+): LOCKED")) { $lockedIds[$m.Groups[1].Value] = $true }
    $list = @()
    foreach ($m in [regex]::Matches($text, "(?m)^\s*(Padlock|RFID) (.+?): active \(usable\)(?:, opens (\S+))? at")) {
        $target = $m.Groups[3].Value
        $list += [pscustomobject]@{ Kind = $m.Groups[1].Value; Id = $m.Groups[2].Value; Opens = $target; Locked = ($target -and $lockedIds[$target]) }
    }
    return @($list | Sort-Object -Property @{ Expression = { -not $_.Locked } }, Kind)
}

function Unlock-And-Check([string]$actor, [string]$observer, $lock) {
    $filter = if ($lock.Opens) { $lock.Opens } else { $lock.Id }
    $before = Send $observer "locks $filter"
    Send $actor "goto lock `"$($lock.Id)`"" | Out-Null
    Start-Sleep 1
    $used = Send $actor "interact lock `"$($lock.Id)`""
    Start-Sleep 1
    Send $actor "dialogue state" | Out-Null
    Shot "a_${actor}_unlocks"
    $seenOpen = $false; $actorOpen = $false
    for ($i = 0; $i -lt 8 -and -not $seenOpen; $i++) {
        Start-Sleep 2
        $obs = Send $observer "locks $filter"
        $act = Send $actor "locks $filter"
        if ($lock.Opens) {
            $pattern = "(?m)^\s*(?:gate|door|sliding) $([regex]::Escape($lock.Opens)): OPEN"
            $seenOpen = $obs -match $pattern
            $actorOpen = $act -match $pattern
        } else {
            $pattern = "(?m)^\s*$($lock.Kind) $([regex]::Escape($lock.Id)): inactive"
            $seenOpen = $obs -match $pattern
            $actorOpen = $act -match $pattern
        }
    }
    Shot "a_${observer}_sees_${actor}s_lock"
    $run = Field (Send $actor "locks run:") "run:"
    if ($seenOpen) { return @("PASS", "$actor used $($lock.Kind) $($lock.Id) ($filter); $observer sees it open") }
    if ($actorOpen) { return @("FAIL", "$actor opened $filter but $observer still sees it locked (world op / obj fact not applied)") }
    return @("INCONCLUSIVE", "$actor's game did not open $filter itself (answer: $used; run: $run) - a padlock needs the crowbar")
}

if ($Only -contains "a") {
    Note "`r`n## a. Locks: either player unlocks, the other sees it"
    Reset-Player "Host"; Reset-Player "Client"
    $candidates = @(Get-LockCandidates)
    $first = if ($LockA) { [pscustomobject]@{ Kind = "Padlock"; Id = $LockA; Opens = "" } } else { $candidates | Select-Object -First 1 }
    $second = if ($LockB) { [pscustomobject]@{ Kind = "Padlock"; Id = $LockB; Opens = "" } } else { $candidates | Where-Object { $_.Id -ne $first.Id } | Select-Object -First 1 }
    if (-not $first) { Verdict "a1 client unlocks" "SKIPPED" "no usable padlock or RFID reader in Host's world (all used?): pass -LockA" }
    else { $r = Unlock-And-Check "Client" "Host" $first; Verdict "a1 client unlocks, host sees" $r[0] $r[1] }
    if (-not $second) { Verdict "a2 host unlocks" "SKIPPED" "no second usable lock: pass -LockB" }
    else { Start-Sleep 3; Reset-Player "Host"; $r = Unlock-And-Check "Host" "Client" $second; Verdict "a2 host unlocks, client sees" $r[0] $r[1] }
}

# ---- b / c: bounties ------------------------------------------------------------------------------------------

function Get-Bounties {
    $text = Send "Host" "find pad"
    $list = @()
    foreach ($m in [regex]::Matches($text, "(?m)^\s*\d+\. \[pad\] '(.+?)' id (\S+) (open|completed)(, locked)?(, inactive)? at")) {
        if ($m.Groups[4].Success -or $m.Groups[5].Success) { continue }
        $list += [pscustomobject]@{ Pad = $m.Groups[1].Value; Set = $m.Groups[2].Value; Open = ($m.Groups[3].Value -eq "open") }
    }
    # Unplayed ones first (a completed bounty can be replayed, but its reward is not paid again).
    return @($list | Sort-Object -Property @{ Expression = { -not $_.Open } }, Set -Unique)
}

function Start-At([string]$who, [string]$set) {
    Send $who "goto pad $set" | Out-Null
    Start-Sleep 1
    Send $who "bounty start $set" | Out-Null
}

$bounties = @()
if ($Only -contains "b" -or $Only -contains "c") {
    $bounties = @(Get-Bounties)
    if (-not $BountyA -and $bounties.Count -gt 0) { $BountyA = $bounties[0].Set }
    if (-not $BountyB) { $BountyB = ($bounties | Where-Object { $_.Set -ne $BountyA } | Select-Object -First 1).Set }
}

if ($Only -contains "b") {
    Note "`r`n## b. Both players in the same bounty ($BountyA)"
    if (-not $BountyA) { Verdict "b same bounty" "SKIPPED" "no active, unlocked bounty pad in Host's world: pass -BountyA" }
    else {
        Reset-Player "Host"; Reset-Player "Client"
        $mainBefore = Field (Send "Host" "bounty state") "mainSet"
        Start-At "Host" $BountyA
        Start-At "Client" $BountyA
        Start-Sleep 4
        $st = Both "bounty state"
        Shot "b_both_in_$BountyA"
        $h1 = Shoot "Host" "0.6"
        $c1 = Shoot "Client" "0.6"
        Start-Sleep 2
        Both "remotes" | Out-Null
        Shot "b_shots_in_flight"
        Start-Sleep 8
        $st2 = Both "bounty state"
        Both "player" | Out-Null
        Shot "b_after_first_stroke"
        $h2 = Shoot "Host" "0.5"
        $c2 = Shoot "Client" "0.5"
        Start-Sleep 9
        $st3 = Both "bounty state"
        Shot "b_after_second_stroke"
        $inBoth = (ActiveBounty $st.Host) -eq $BountyA -and (ActiveBounty $st.Client) -eq $BountyA
        $shots = @($h1, $c1, $h2, $c2 | Where-Object { $_ -match "^shot through" }).Count
        $evidence = "Host active '$(ActiveBounty $st.Host)' -> '$(ActiveBounty $st3.Host)', Client active '$(ActiveBounty $st.Client)' -> '$(ActiveBounty $st3.Client)'; $shots/4 shots fired; " +
                    "Host challenge $(Field $st3.Host 'currentChallenge'), Client challenge $(Field $st3.Client 'currentChallenge')"
        Verdict "b same bounty" $(if ($inBoth -and $shots -ge 2) { "PASS" } elseif ($inBoth) { "INCONCLUSIVE" } else { "FAIL" }) $evidence
        Reset-Player "Host"; Reset-Player "Client"
        $mainAfter = Field (Send "Host" "bounty state") "mainSet"
        Verdict "b main track kept" $(if ($mainBefore -eq $mainAfter) { "PASS" } else { "CHECK" }) "Host main set '$mainBefore' before, '$mainAfter' after leaving the bounty"
    }
}

if ($Only -contains "c") {
    Note "`r`n## c. Different bounties at the same time (Host $BountyA, Client $BountyB)"
    if (-not $BountyA -or -not $BountyB) { Verdict "c different bounties" "SKIPPED" "need two active, unlocked bounty pads: pass -BountyA and -BountyB" }
    else {
        Reset-Player "Host"; Reset-Player "Client"
        Start-At "Host" $BountyA
        Start-At "Client" $BountyB
        Start-Sleep 4
        $st = Both "bounty state"
        Both "wind" | Out-Null
        Shot "c_host_${BountyA}_client_$BountyB"
        $h1 = Shoot "Host" "0.6"
        $c1 = Shoot "Client" "0.6"
        Start-Sleep 2
        Shot "c_shots_in_flight"
        Start-Sleep 8
        $st2 = Both "bounty state"
        Both "wind" | Out-Null
        Shot "c_after_stroke"
        $ok = (ActiveBounty $st.Host) -eq $BountyA -and (ActiveBounty $st.Client) -eq $BountyB -and
              (ActiveBounty $st2.Host) -eq $BountyA -and (ActiveBounty $st2.Client) -eq $BountyB
        $shots = @($h1, $c1 | Where-Object { $_ -match "^shot through" }).Count
        Verdict "c different bounties" $(if ($ok -and $shots -eq 2) { "PASS" } elseif ($ok) { "INCONCLUSIVE" } else { "FAIL" }) `
            "Host '$(ActiveBounty $st.Host)'/'$(ActiveBounty $st2.Host)', Client '$(ActiveBounty $st.Client)'/'$(ActiveBounty $st2.Client)' (each must keep its own); $shots/2 shots"
        Reset-Player "Host"; Reset-Player "Client"
    }
}

# ---- d: cutscene ----------------------------------------------------------------------------------------------

if ($Only -contains "d") {
    Note "`r`n## d. Host in a dialogue / cutscene while Client walks and shoots"
    Reset-Player "Host"; Reset-Player "Client"
    $target = $null
    if ($Cutscene) { $target = $Cutscene }
    else {
        $triggers = Send "Host" "dialogue triggers"
        $m = [regex]::Match($triggers, "(?m)^\s*1\. \[trigger\] '(.+?)'(?: id (\S+))? ")
        if ($m.Success) { $target = if ($m.Groups[2].Success) { "trigger `"$($m.Groups[2].Value)`"" } else { "trigger `"$($m.Groups[1].Value)`"" } }
        else {
            $lock = Get-LockCandidates | Select-Object -First 1
            if ($lock) { $target = "lock `"$($lock.Id)`"" }
        }
    }
    if (-not $target) { Verdict "d cutscene" "SKIPPED" "no active dialogue trigger or usable padlock on Host: pass -Cutscene" }
    else {
        Send "Host" "goto $target" | Out-Null
        Start-Sleep 2
        $hd = Send "Host" "dialogue state"
        if (-not (Is-Locked $hd)) { Send "Host" "interact $target" | Out-Null; Start-Sleep 2; $hd = Send "Host" "dialogue state" }
        $cd = Send "Client" "dialogue state"
        Shot "d_host_in_cutscene"
        $p0 = Field (Send "Client" "player") "feet"
        $xyz = [regex]::Match($p0, "\(([-\d.]+), ([-\d.]+), ([-\d.]+)\)")
        $walked = ""
        if ($xyz.Success) {
            $x = [double]::Parse($xyz.Groups[1].Value, [cultureinfo]::InvariantCulture) + 4
            $z = [double]::Parse($xyz.Groups[3].Value, [cultureinfo]::InvariantCulture) + 4
            $walked = Send "Client" ("goto {0} {1} {2}" -f $x.ToString([cultureinfo]::InvariantCulture), $xyz.Groups[2].Value, $z.ToString([cultureinfo]::InvariantCulture))
        }
        Start-Sleep 1
        Send "Client" "mode golf" | Out-Null
        Start-Sleep 3
        $shot = Shoot "Client" "0.6"
        Start-Sleep 2
        Send "Host" "remotes" | Out-Null
        $hd2 = Send "Host" "dialogue state"
        $cd2 = Send "Client" "dialogue state"
        Shot "d_client_shoots_during_cutscene"
        Start-Sleep 6
        Send "Client" "player" | Out-Null
        $hostWasLocked = (Is-Locked $hd) -or (Is-Locked $hd2)
        $clientFree = -not (Is-Locked $cd) -and -not (Is-Locked $cd2)
        for ($i = 0; $i -lt 20 -and (Is-Locked (Send "Host" "dialogue state")); $i++) { Send "Host" "dialogue next" | Out-Null; Start-Sleep 2 }
        $hd3 = Send "Host" "dialogue state"
        Shot "d_after_cutscene"
        $evidence = "Host moveLocked $(Field $hd 'moveLocked')/$(Field $hd2 'moveLocked') (videos $(Field $hd2 'videosPlaying')), Client moveLocked $(Field $cd 'moveLocked')/$(Field $cd2 'moveLocked'); " +
                    "Client moved: $(if ($walked -match '^moved') { 'yes' } else { 'no' }), shot: $(if ($shot -match '^shot through') { 'yes' } else { 'no' }); Host after: moveLocked $(Field $hd3 'moveLocked')"
        $result = if (-not $hostWasLocked) { "INCONCLUSIVE" } elseif ($clientFree -and $walked -match "^moved" -and $shot -match "^shot through" -and -not (Is-Locked $hd3)) { "PASS" } elseif (-not $clientFree) { "FAIL" } else { "CHECK" }
        Verdict "d cutscene" $result $evidence
        Reset-Player "Client"
    }
}

# ---- e: remote ball trail -------------------------------------------------------------------------------------

function Trail-Check([string]$shooter, [string]$watcher) {
    $shooterName = if ($shooter -eq "Host") { $hs.players | Where-Object { $_.host } | Select-Object -First 1 } else { $hs.players | Where-Object { -not $_.host } | Select-Object -First 1 }
    $name = if ($shooterName) { $shooterName.name } else { $shooter }
    Reset-Player $shooter; Reset-Player $watcher
    Send $watcher "goto remote `"Player $name`"" | Out-Null
    Start-Sleep 1
    Send $shooter "mode golf" | Out-Null
    Start-Sleep 3
    $shot = Shoot $shooter $TrailPower.ToString([cultureinfo]::InvariantCulture)
    # Once the ball is in the air: the watcher steps 7 m behind the shooter and looks along the ball's flight, so
    # the trail is in the picture (beside the avatar it filled the view and the ball left the frame).
    Start-Sleep -Milliseconds 500
    $seen = Send $watcher "remotes"
    $pm = [regex]::Match($seen, "NGGMP Player $([regex]::Escape($name)) shown at \(([-\d.]+), ([-\d.]+), ([-\d.]+)\)")
    $bm = [regex]::Match($seen, "NGGMP Ball $([regex]::Escape($name)) shown at \(([-\d.]+), ([-\d.]+), ([-\d.]+)\)")
    if ($pm.Success -and $bm.Success) {
        $inv = [cultureinfo]::InvariantCulture
        $px = [double]::Parse($pm.Groups[1].Value, $inv); $pz = [double]::Parse($pm.Groups[3].Value, $inv)
        $dx = [double]::Parse($bm.Groups[1].Value, $inv) - $px; $dz = [double]::Parse($bm.Groups[3].Value, $inv) - $pz
        $len = [Math]::Sqrt($dx * $dx + $dz * $dz)
        if ($len -gt 0.5) {
            $nx = $dx / $len; $nz = $dz / $len
            $yaw = [Math]::Atan2($nx, $nz) * 180 / [Math]::PI
            Send $watcher ("goto {0} {1} {2} {3}" -f ($px - 7 * $nx).ToString("0.##", $inv), $pm.Groups[2].Value,
                ($pz - 7 * $nz).ToString("0.##", $inv), $yaw.ToString("0.#", $inv)) | Out-Null
        }
    }
    $best = 0; $trails = 0
    foreach ($delay in @(1.4, 0.7, 0.7)) {
        Start-Sleep -Milliseconds ([int]($delay * 1000))
        $r = Send $watcher "remotes"
        $m = [regex]::Match($r, "trails: (\d+), drawing: (\d+)")
        if ($m.Success) { $trails = [Math]::Max($trails, [int]$m.Groups[1].Value); $best = [Math]::Max($best, [int]$m.Groups[2].Value) }
        Shot "e_${watcher}_watches_${shooter}s_ball"
    }
    Start-Sleep 6
    Send $watcher "remotes" | Out-Null
    Reset-Player $shooter
    if ($shot -notmatch "^shot through") { return @("INCONCLUSIVE", "$shooter could not shoot: $shot") }
    if ($best -ge 1) { return @("PASS", "$watcher's view of ${name}'s ball drew a trail ($best drawing of $trails trail renderers); check the PNGs") }
    if ($trails -ge 1) { return @("FAIL", "$watcher's ghost ball has $trails trail renderer(s) but none drew during the flight") }
    return @("FAIL", "$watcher's ghost ball has no trail renderer at all (remotes: trails 0)")
}

if ($Only -contains "e") {
    Note "`r`n## e. Remote ball trail"
    $r = Trail-Check "Client" "Host"; Verdict "e1 host sees client's trail" $r[0] $r[1]
    $r = Trail-Check "Host" "Client"; Verdict "e2 client sees host's trail" $r[0] $r[1]
}

# ---- Save safety ----------------------------------------------------------------------------------------------

Note "`r`n## Saves"
$check = Send "Client" "savecheck"
Verdict "client savecheck" $(if ($check -match "VIOLATION") { "FAIL" } else { "PASS" }) (($check -split "`n")[0])
$realChanged = Get-Changed $realBefore (Get-SaveHashes $RealSaveDir)
Verdict "real save folder unchanged" $(if ($realChanged.Count -eq 0) { "PASS" } else { "FAIL" }) $(if ($realChanged.Count -eq 0) { "$($realBefore.Count) files hashed in $RealSaveDir" } else { "CHANGED: $($realChanged -join ', ')" })
$clientChanged = Get-Changed $clientBefore (Get-SaveHashes $clientSaves)
Verdict "client's own saves unchanged" $(if ($clientChanged.Count -eq 0) { "PASS" } else { "FAIL" }) $(if ($clientChanged.Count -eq 0) { "$($clientBefore.Count) files in $clientSaves" } else { "CHANGED: $($clientChanged -join ', ')" })
$hostChanged = Get-Changed $hostBefore (Get-SaveHashes $hostSaves)
Note "Host copy's saves changed (allowed, the session's progress): $(if ($hostChanged.Count) { $hostChanged -join ', ' } else { 'none' })"

$verdicts | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $out "results.json") -Encoding UTF8
Note "`r`n## Summary`r`n"
Note "| Scenario | Result | Evidence |`r`n|---|---|---|"
foreach ($v in $verdicts) { Add-Content $md "| $($v.scenario) | $($v.result) | $($v.evidence -replace '\|', '/') |" -Encoding UTF8 }
$verdicts | Format-Table -AutoSize -Wrap | Out-String | Write-Host
Write-Host "Results and screenshots: $out"
if (@($verdicts | Where-Object { $_.result -eq "FAIL" }).Count -gt 0) { exit 1 }
