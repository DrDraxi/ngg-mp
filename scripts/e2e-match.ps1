# Competitive nine holes on the two running test copies (1.1.0, docs/GAME_INTEGRATION.md 7.8). Start them in Just
# Play Nine first: scripts/e2e.ps1 -Nine (or pass -Start). Both copies are played through their dev control API
# (/cmd runs the harness's 'match', 'shoot' and 'mode' commands; docs/TESTING.md 2.8), so no focus, keyboard or mouse.
#   1. The Client presses the round machine: the round must start for both (the client asks the host).
#   2. Out of turn: whoever waits tries to swing; the swing must be refused (the ball does not move).
#   3. Then turn by turn: the player the host names plays. On the first -ShotHoles holes each stroke is a real
#      'shoot', after that the ball is dropped in the cup ('match cup'). The Client picks up on hole -PickUpHole.
#      After holing, the player leaves golf mode; the next tee waits until everyone is done.
#   4. After hole 9: both show the same final card, the winner line, and the results.
# Results land in <TestDir>\match\<yyyyMMdd-HHmmss>\: results.md (every command and answer, verdicts), results.json,
# PNGs of both windows side by side. Your real save folder is hashed before and after (must not change).
param(
    [string]$TestDir = "$env:USERPROFILE\Games\NGG-Test",
    [string]$RealSaveDir = "$env:USERPROFILE\AppData\LocalLow\Luke Muscat\Normal Golf Game",
    [int]$HostPort = 0,
    [int]$ClientPort = 0,
    [int]$ShotHoles = 2,
    [int]$PickUpHole = 3,
    [double]$Power = 0.45,
    [int]$MaxSteps = 160,
    [switch]$Start
)
$ErrorActionPreference = "Stop"

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
$out = Join-Path $TestDir "match\$stamp"
New-Item -ItemType Directory -Force $out | Out-Null
$md = Join-Path $out "results.md"
$verdicts = New-Object System.Collections.ArrayList
$shotNumber = 0
Set-Content $md "# NGG Multiplayer competitive nine holes $stamp`r`n`r`nHost $($api.Host), Client $($api.Client)`r`n" -Encoding UTF8

function Note([string]$text) {
    Write-Host $text
    Add-Content $md "$text`r`n" -Encoding UTF8
}

function Get-Api([string]$url, [int]$timeout = 10) {
    try { return Invoke-RestMethod -Uri $url -TimeoutSec $timeout } catch { return $null }
}

function Send([string]$who, [string]$line) {
    $r = Get-Api "$($api[$who])/cmd?line=$([uri]::EscapeDataString($line))" 75
    $text = if (-not $r) { "(no answer)" } elseif ($r.error) { "ERROR: $($r.error)" } elseif ($r.queued) { "(still queued after 60 s)" } else { [string]$r.result }
    Add-Content $md "**$who** ``> $line```r`n``````r`n$text`r`n``````r`n" -Encoding UTF8
    Write-Host ("[{0}] > {1}`n    {2}" -f $who, $line, (($text -split "`n") -join "`n    "))
    return $text
}

function Verdict([string]$step, [string]$result, [string]$evidence) {
    [void]$verdicts.Add([pscustomobject]@{ scenario = $step; result = $result; evidence = $evidence })
    Note "**Verdict $step`: $result** - $evidence"
}

Add-Type -AssemblyName System.Drawing
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
            $both.Save((Join-Path $out "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
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

# ---- Reading 'match status' -------------------------------------------------------------------------------------

# "match 7 Turns Playing hole 3/9 (par 3) turn Alice seq 12"
function Parse-Match([string]$text) {
    $m = [regex]::Match($text, "(?m)^match (\d+) (\w+) (\w+) hole (\d)/9 \(par (\d+)\) turn (.+?) seq (\d+)( hole-complete)?")
    if (-not $m.Success) { return $null }
    $me = [regex]::Match($text, "(?m)^\S+ (.+?) \(you\) \S+ .*?\[([^\]]*)\]")
    return [pscustomobject]@{
        Id = [int]$m.Groups[1].Value; Mode = $m.Groups[2].Value; Phase = $m.Groups[3].Value
        Hole = [int]$m.Groups[4].Value; Par = [int]$m.Groups[5].Value; Turn = $m.Groups[6].Value
        Seq = [int]$m.Groups[7].Value; HoleComplete = $m.Groups[8].Success
        Me = if ($me.Success) { $me.Groups[1].Value } else { "" }
        Cards = if ($me.Success) { @($me.Groups[2].Value -split ",") } else { @() }
        MayPlay = $text -match "mayPlay True"; NextShot = $text -match "next-shot ready"
        Golf = $text -match "mode Golf"; Shot = if ($text -match "ball shot (-?\d+)") { [int]$Matches[1] } else { -99 }
        Ball = if ($text -match "ball shot -?\d+ at (\([^)]*\))") { $Matches[1] } else { "" }
        Text = $text
    }
}

function Status([string]$who) { return Parse-Match (Send $who "match status") }

function Wait-For([string]$who, [scriptblock]$cond, [int]$seconds) {
    for ($i = 0; $i -lt $seconds; $i += 2) {
        $s = Status $who
        if ($s -and (& $cond $s)) { return $s }
        Start-Sleep 2
    }
    return $null
}

# ---- Preconditions --------------------------------------------------------------------------------------------

if ($Start) { & "$PSScriptRoot\e2e.ps1" -TestDir $TestDir -RealSaveDir $RealSaveDir -Nine }

$hs = Get-Api "$($api.Host)/status"
$cs = Get-Api "$($api.Client)/status"
if (-not $hs -or -not $cs) { throw "Host or Client does not answer: start them with scripts/e2e.ps1 -Nine (or pass -Start)." }
if (-not $hs.isHost -or $cs.state -ne "InSession" -or $hs.scene -notmatch "world ready" -or $cs.scene -notmatch "world ready") {
    throw "Both copies must be in the world in one session: Host '$($hs.state)', Client '$($cs.state)'. Run scripts/e2e.ps1 -Nine."
}
foreach ($who in @("Host", "Client")) {
    if ((Send $who "help") -notmatch "match ") { throw "$who runs a build without the 'match' command: run scripts/e2e.ps1 -Nine (it installs the current build)." }
}
$realBefore = Get-SaveHashes $RealSaveDir
Send "Client" "savecheck reset" | Out-Null
Shot "before"

# ---- 1. Client starts the round -------------------------------------------------------------------------------

Note "`r`n## 1. Start"
$r = Send "Client" "match start"
if ($r -match "no StartLMUGC") { throw "No round machine: the copies are not in Just Play Nine (scripts/e2e.ps1 -Nine)." }
$hm = Wait-For "Host" { param($s) $s.Phase -eq "Playing" } 30
$cm = Wait-For "Client" { param($s) $s.Phase -eq "Playing" -and $hm -and $s.Id -eq $hm.Id } 30
Verdict "1 client starts the round for both" $(if ($hm -and $cm) { "PASS" } else { "FAIL" }) $(if ($hm -and $cm) { "match $($hm.Id) $($hm.Mode), hole 1, turn $($hm.Turn) on both" } else { "host: $(if ($hm) { 'playing' } else { 'no match' }), client: $(if ($cm) { 'playing' } else { 'no match' })" })
if (-not ($hm -and $cm)) { throw "The round did not start; see $md" }
Start-Sleep 12   # the game's 10 s camera move to the first tee
Shot "hole1-tee"

# ---- 2. Out of turn -------------------------------------------------------------------------------------------

Note "`r`n## 2. Out of turn"
$hm = Status "Host"
if ($hm.Mode -eq "Turns") {
    # Both copies may share a player name (one Steam account): the turn is read from each copy's own mayPlay.
    $waiter = if ((Status "Host").MayPlay) { "Client" } else { "Host" }
    $before = Status $waiter
    Send $waiter "shoot $Power" | Out-Null
    Start-Sleep 4
    $after = Status $waiter
    $moved = $before.Ball -ne $after.Ball
    Verdict "2 $waiter cannot swing out of turn" $(if (-not $moved -and -not $after.MayPlay) { "PASS" } else { "FAIL" }) "ball $($before.Ball) -> $($after.Ball), mayPlay $($after.MayPlay)"
    Shot "out-of-turn"
} else {
    Note "Free order: no turn gate to check."
}

# ---- 3. Turn by turn -------------------------------------------------------------------------------------------

Note "`r`n## 3. Holes"
$seenHoles = @{}
$pickedUp = $false
$heldChecked = $false
for ($step = 0; $step -lt $MaxSteps; $step++) {
    $hm = Status "Host"
    if (-not $hm) { Start-Sleep 2; continue }
    if ($hm.Phase -ne "Playing") { break }
    if (-not $seenHoles.ContainsKey($hm.Hole)) {
        $seenHoles[$hm.Hole] = $true
        if ($hm.Hole -gt 1) { Shot "hole$($hm.Hole)" }
    }
    if ($hm.HoleComplete) { Start-Sleep 2; continue }
    # The copy that may play now (in turn order only one; both copies may share a player name).
    $who = $null
    $me = $null
    foreach ($w in @("Host", "Client")) { $s = Status $w; if ($s -and $s.MayPlay) { $who = $w; $me = $s; break } }
    if (-not $who) { Start-Sleep 2; continue }
    $hole = $me.Hole
    if ($who -eq "Client" -and $hole -eq $PickUpHole -and -not $pickedUp -and $me.Cards[$hole - 1] -match "^~") {
        Send $who "match pickup" | Out-Null
        $pickedUp = $true
        Start-Sleep 2
        Send $who "mode walk" | Out-Null
        Shot "client-picked-up"
        continue
    }
    if ($me.NextShot) {
        Send $who "match resume" | Out-Null
        Start-Sleep 3
        $me = Status $who
    }
    $strokes = if ($me.Cards.Count -ge $hole -and $me.Cards[$hole - 1] -match "^~(\d+)") { [int]$Matches[1] } else { 0 }
    if ($hole -le $ShotHoles -and $strokes -lt 2) {
        $r = Send $who "shoot $Power"
        if ($r -match "played already") { Send $who "match resume" | Out-Null; Start-Sleep 3; $r = Send $who "shoot $Power" }
        Start-Sleep 9   # flight and roll; the rest report passes the turn
        if ($step -lt 6) { Shot "hole$hole-$($who.ToLowerInvariant())-shot" }
    } else {
        Send $who "match cup" | Out-Null
        Start-Sleep 2
        # Leaving golf mode lets the game move the ball to the next tee (the mod holds it until the hole is done).
        Send $who "mode walk" | Out-Null
        Start-Sleep 2
        if (-not $heldChecked -and $hole -lt 9) {
            $other = if ($who -eq "Host") { "Client" } else { "Host" }
            $mine = Status $who
            $theirs = Status $other
            if ($mine.Hole -eq $hole -and $theirs.Cards.Count -ge $hole -and $theirs.Cards[$hole - 1] -notmatch "^\d") {
                $heldChecked = $true
                Verdict "3a next tee waits for everyone" $(if ($mine.Text -match "holding-next-tee|wait 'Waiting for") { "PASS" } else { "FAIL" }) (($mine.Text -split "`n")[0])
                Shot "waiting-for-$($other.ToLowerInvariant())"
            }
        }
    }
}

# ---- 4. Results ------------------------------------------------------------------------------------------------

Note "`r`n## 4. Results"
Start-Sleep 3
$hm = Status "Host"
$cm = Status "Client"
$hostCard = Send "Host" "match card"
$clientCard = Send "Client" "match card"
Shot "results"
$finished = $hm -and $cm -and $hm.Phase -eq "Finished" -and $cm.Phase -eq "Finished"
Verdict "4a round finished on both after 9 holes" $(if ($finished) { "PASS" } else { "FAIL" }) "host $($hm.Phase) hole $($hm.Hole), client $($cm.Phase) hole $($cm.Hole), holes seen $(@($seenHoles.Keys) -join ',')"
$strip = { param($t) (($t -split "`n") | ForEach-Object { $_ -replace " \(you\)", "" }) -join "`n" }
Verdict "4b both show the same final card" $(if ((& $strip $hostCard) -eq (& $strip $clientCard)) { "PASS" } else { "FAIL" }) ((($hostCard -split "`n") | Select-Object -Last 1))
Verdict "4c client's pick-up counted" $(if (-not $pickedUp) { "SKIPPED" } elseif ($hostCard -match "\*") { "PASS" } else { "FAIL" }) "hole $PickUpHole"
Start-Sleep 8
Shot "after-results"

# ---- Saves -----------------------------------------------------------------------------------------------------

Note "`r`n## Saves"
$check = Send "Client" "savecheck"
Verdict "client savecheck" $(if ($check -match "VIOLATION") { "FAIL" } else { "PASS" }) (($check -split "`n")[0])
$after = Get-SaveHashes $RealSaveDir
$changed = @(@($realBefore.Keys) + @($after.Keys) | Sort-Object -Unique | Where-Object { $realBefore[$_] -ne $after[$_] })
Verdict "real save folder unchanged" $(if ($changed.Count -eq 0) { "PASS" } else { "FAIL" }) $(if ($changed.Count -eq 0) { "$($realBefore.Count) files hashed" } else { "CHANGED: $($changed -join ', ')" })

$verdicts | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $out "results.json") -Encoding UTF8
Note "`r`n## Summary`r`n"
Note "| Step | Result | Evidence |`r`n|---|---|---|"
foreach ($v in $verdicts) { Add-Content $md "| $($v.scenario) | $($v.result) | $($v.evidence -replace '\|', '/') |" -Encoding UTF8 }
$verdicts | Format-Table -AutoSize -Wrap | Out-String | Write-Host
Write-Host "Results and screenshots: $out"
if (@($verdicts | Where-Object { $_.result -eq "FAIL" }).Count -gt 0) { exit 1 }
