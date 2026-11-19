#!/usr/bin/env bash
# Builds "AscNet Launcher.app" and AscNetLauncher-macOS.zip (ad-hoc signed; the player allows it once in Gatekeeper).
#   packaging/macos/package.sh [--media DIR] [--out DIR] [--skip-build]
# --media DIR (default: $ASCNET_LAUNCHER_MEDIA, else <repo>/.runtime/launcher-media) holds background.bmp, background.mp4
# and background.wav. background.mp4 must pass the launcher's own decoder (no B-frames); otherwise the script fails.
# Run on macOS with the Rust toolchain on PATH.
set -euo pipefail

root="$(cd "$(dirname "$0")/../.." && pwd)"
out="$root/target/macos"
media=""
build=1
while [ $# -gt 0 ]; do
  case "$1" in
    --media) media="$2"; shift 2 ;;
    --out) out="$2"; shift 2 ;;
    --skip-build) build=0; shift ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

version="$(sed -n 's/^version = "\(.*\)"/\1/p' "$root/Cargo.toml" | head -n 1)"
if [ "$build" = 1 ]; then
  # Universal (Apple Silicon + Intel): the app targets Apple Silicon, but an Intel build machine (CI's macos-15-intel)
  # must be able to run the binary for --check-media and the smoke test.
  for target in aarch64-apple-darwin x86_64-apple-darwin; do
    cargo build --manifest-path "$root/Cargo.toml" --release --target "$target"
  done
  mkdir -p "$root/target/universal-apple-darwin/release"
  lipo -create -output "$root/target/universal-apple-darwin/release/ascnet-launcher" \
    "$root/target/aarch64-apple-darwin/release/ascnet-launcher" "$root/target/x86_64-apple-darwin/release/ascnet-launcher"
fi
binary="$root/target/universal-apple-darwin/release/ascnet-launcher"
[ -x "$binary" ] || binary="$root/target/aarch64-apple-darwin/release/ascnet-launcher"
[ -x "$binary" ] || binary="$root/target/release/ascnet-launcher"

[ -n "$media" ] || media="${ASCNET_LAUNCHER_MEDIA:-$root/../.runtime/launcher-media}"
for file in background.bmp background.mp4 background.wav; do
  [ -f "$media/$file" ] || { echo "missing $media/$file (use --media DIR)" >&2; exit 1; }
done
# The launcher decodes the video in pure Rust (OpenH264), which cannot reorder frames: ask that decoder.
"$binary" --check-media "$media/background.mp4" || { echo "background.mp4 is not playable (B-frames? see AGENTS.MD)" >&2; exit 1; }
[ "$("$binary" --self-update-probe)" = "ASCNET_LAUNCHER_VERSION=$version" ] || { echo "launcher binary does not start" >&2; exit 1; }

app="$out/AscNet Launcher.app"
rm -rf "$app" "$out/AscNetLauncher-macOS.zip"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
# Ship arm64 only: launchers up to 1.0.29 accept a single-architecture Mach-O update, not a universal one.
if lipo -archs "$binary" | grep -qw x86_64; then
  lipo -thin arm64 -output "$app/Contents/MacOS/AscNetLauncher" "$binary"
else
  cp "$binary" "$app/Contents/MacOS/AscNetLauncher"
fi
[ "$(lipo -archs "$app/Contents/MacOS/AscNetLauncher")" = arm64 ] || { echo "app binary is not arm64-only" >&2; exit 1; }
sed "s/@VERSION@/$version/g" "$root/packaging/macos/Info.plist" > "$app/Contents/Info.plist"
cp "$root/packaging/macos/AscNet.icns" "$app/Contents/Resources/AscNet.icns"
cp "$root/launcher.json" "$root/supported-client.json" "$root/LICENSE" "$app/Contents/Resources/"
for file in background.bmp background.mp4 background.wav; do cp "$media/$file" "$app/Contents/Resources/$file"; done

codesign --force --deep --sign - "$app"
codesign --verify --deep --strict "$app"
# ditto keeps permissions; no AppleDouble (`._`) members, the updater accepts only the bundle's own files.
ditto -c -k --keepParent --norsrc --noextattr --noqtn --noacl "$app" "$out/AscNetLauncher-macOS.zip"
echo "$out/AscNetLauncher-macOS.zip ($version)"
