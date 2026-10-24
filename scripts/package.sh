#!/usr/bin/env bash
# Builds the plugin in Release and packs dist/NGGMultiplayer-<version>.zip:
#   BepInEx/plugins/NGGMultiplayer/{NGGMultiplayer.dll, NGGMultiplayer.Core.dll, avatar/*.png, avatar/female/*.png, video/female/<clip>/*.png}
#   README.md
# Extra arguments are passed to dotnet build (e.g. -p:GameManagedDir=/path/to/Managed).
#
# The build must bind to the game's own Steamworks.NET (found in GameManagedDir); otherwise it fails, because a DLL
# built against the NuGet stand-in references an assembly the game does not load. --allow-standin-steamworks skips
# that check for a local test build and names the zip NGGMultiplayer-<version>-standin.zip so it is not shipped.
# A build against the compile-time stubs in stubs/ (no game assemblies found, STUB_GAME) is always refused, and the
# stub DLLs are never packaged.
set -euo pipefail

allow_standin=0
build_args=()
for arg in "$@"; do
    if [ "$arg" = "--allow-standin-steamworks" ]; then
        allow_standin=1
    else
        build_args+=("$arg")
    fi
done

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$root/Directory.Build.props" | head -n 1)"
if [ -z "$version" ]; then
    echo "Could not read <Version> from Directory.Build.props" >&2
    exit 1
fi

suffix=""
if [ "$allow_standin" -eq 1 ]; then
    suffix="-standin"
    echo "Warning: packaging without the game's Steamworks.NET; this build is for testing only." >&2
else
    build_args+=("-p:RequireGameSteamworks=true")
fi

build_args+=("-p:RequireGameAssemblies=true")

dotnet build "$root/src/NGGMultiplayer/NGGMultiplayer.csproj" -c Release --nologo ${build_args[@]+"${build_args[@]}"}

out="$root/src/NGGMultiplayer/bin/Release/netstandard2.1"
for f in NGGMultiplayer.dll NGGMultiplayer.Core.dll; do
    if [ ! -f "$out/$f" ]; then
        echo "Missing build output: $out/$f" >&2
        exit 1
    fi
done

# Belt and braces: never ship a plugin compiled against the stubs, nor the stubs themselves.
if grep -q -a NGGMP_STUB_GAME_BUILD "$out/NGGMultiplayer.dll"; then
    echo "$out/NGGMultiplayer.dll was built against the game stubs (STUB_GAME); refusing to package it." >&2
    exit 1
fi
for f in Assembly-CSharp.dll ECM2.dll UnityEngine.UI.dll Unity.TextMeshPro.dll Unity.InputSystem.dll; do
    if [ -e "$out/$f" ]; then
        echo "Unexpected $f in $out (game or stub assembly copied to the output); refusing to package." >&2
        exit 1
    fi
done

stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
plugin_dir="$stage/BepInEx/plugins/NGGMultiplayer"
mkdir -p "$plugin_dir"
cp "$out/NGGMultiplayer.dll" "$out/NGGMultiplayer.Core.dll" "$plugin_dir/"
# Remote players' directional cutouts (Game/Presence/DirectionalAvatar.cs).
mkdir -p "$plugin_dir/avatar"
cp "$root"/assets/avatar/*.png "$plugin_dir/avatar/"
# The female golfer's cutouts (the Character choice).
mkdir -p "$plugin_dir/avatar/female"
cp "$root"/assets/avatar/female/*.png "$plugin_dir/avatar/female/"
# The female drawings over the player's own videos (Game/Presence/LookVideo.cs).
if [ -d "$root/assets/video" ]; then cp -r "$root/assets/video" "$plugin_dir/"; fi
cp "$root/README.md" "$stage/README.md"

mkdir -p "$root/dist"
zip_path="$root/dist/NGGMultiplayer-$version$suffix.zip"
rm -f "$zip_path"
if command -v zip >/dev/null 2>&1; then
    (cd "$stage" && zip -q -r -X "$zip_path" BepInEx README.md)
else
    python3 - "$stage" "$zip_path" <<'PY'
import os, sys, zipfile
stage, target = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED) as z:
    for base, _, files in os.walk(stage):
        for name in sorted(files):
            full = os.path.join(base, name)
            z.write(full, os.path.relpath(full, stage).replace(os.sep, "/"))
PY
fi
echo "Packaged $zip_path"
