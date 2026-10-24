# Builds the plugin in Release and packs dist/NGGMultiplayer-<version>.zip:
#   BepInEx/plugins/NGGMultiplayer/{NGGMultiplayer.dll, NGGMultiplayer.Core.dll, avatar/*.png, avatar/female/*.png, video/female/<clip>/*.png}
#   README.md
# Extra arguments are passed to dotnet build, e.g.:
#   ./scripts/package.ps1 "-p:GameManagedDir=C:\...\Normal Golf Game_Data\Managed"
#
# The build must bind to the game's own Steamworks.NET (found in GameManagedDir); otherwise it fails, because a DLL
# built against the NuGet stand-in references an assembly the game does not load. --allow-standin-steamworks skips
# that check for a local test build and names the zip NGGMultiplayer-<version>-standin.zip so it is not shipped.
# A build against the compile-time stubs in stubs/ (no game assemblies found, STUB_GAME) is always refused, and the
# stub DLLs are never packaged.
$ErrorActionPreference = 'Stop'

$allowStandin = $false
$buildArgs = @()
foreach ($arg in $args) {
    if ($arg -eq '--allow-standin-steamworks') { $allowStandin = $true } else { $buildArgs += $arg }
}
$suffix = ''
if ($allowStandin) {
    $suffix = '-standin'
    Write-Warning "Packaging without the game's Steamworks.NET; this build is for testing only."
} else {
    $buildArgs += '-p:RequireGameSteamworks=true'
}

$buildArgs += '-p:RequireGameAssemblies=true'

$root = Split-Path -Parent $PSScriptRoot
[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = ($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { throw 'Could not read <Version> from Directory.Build.props' }

dotnet build (Join-Path $root 'src/NGGMultiplayer/NGGMultiplayer.csproj') -c Release --nologo @buildArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed ($LASTEXITCODE)" }

$out = Join-Path $root 'src/NGGMultiplayer/bin/Release/netstandard2.1'
$dlls = @('NGGMultiplayer.dll', 'NGGMultiplayer.Core.dll') | ForEach-Object { Join-Path $out $_ }
foreach ($dll in $dlls) {
    if (-not (Test-Path $dll)) { throw "Missing build output: $dll" }
}

# Belt and braces: never ship a plugin compiled against the stubs, nor the stubs themselves.
$pluginBytes = [System.IO.File]::ReadAllBytes((Join-Path $out 'NGGMultiplayer.dll'))
if ([System.Text.Encoding]::ASCII.GetString($pluginBytes).Contains('NGGMP_STUB_GAME_BUILD')) {
    throw "$out/NGGMultiplayer.dll was built against the game stubs (STUB_GAME); refusing to package it."
}
foreach ($name in @('Assembly-CSharp.dll', 'ECM2.dll', 'UnityEngine.UI.dll', 'Unity.TextMeshPro.dll', 'Unity.InputSystem.dll')) {
    if (Test-Path (Join-Path $out $name)) {
        throw "Unexpected $name in $out (game or stub assembly copied to the output); refusing to package."
    }
}

$stage = Join-Path ([System.IO.Path]::GetTempPath()) ("nggmp-" + [guid]::NewGuid())
try {
    $pluginDir = Join-Path $stage 'BepInEx/plugins/NGGMultiplayer'
    New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null
    Copy-Item $dlls $pluginDir
    # Remote players' directional cutouts (Game/Presence/DirectionalAvatar.cs).
    $avatarDir = Join-Path $pluginDir 'avatar'
    New-Item -ItemType Directory -Force -Path $avatarDir | Out-Null
    Copy-Item (Join-Path $root 'assets/avatar/*.png') $avatarDir
    # The female golfer's cutouts (the Character choice).
    $femaleDir = Join-Path $avatarDir 'female'
    New-Item -ItemType Directory -Force -Path $femaleDir | Out-Null
    Copy-Item (Join-Path $root 'assets/avatar/female/*.png') $femaleDir
    # The female drawings over the player's own videos (Game/Presence/LookVideo.cs).
    $videoDir = Join-Path $root 'assets/video'
    if (Test-Path $videoDir) { Copy-Item $videoDir $pluginDir -Recurse }
    Copy-Item (Join-Path $root 'README.md') (Join-Path $stage 'README.md')

    $dist = Join-Path $root 'dist'
    New-Item -ItemType Directory -Force -Path $dist | Out-Null
    $zip = Join-Path $dist "NGGMultiplayer-$version$suffix.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    # ZipFile writes '/' separators on every platform (Compress-Archive on Windows PowerShell 5.1 does not).
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    Write-Host "Packaged $zip"
}
finally {
    if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
}
