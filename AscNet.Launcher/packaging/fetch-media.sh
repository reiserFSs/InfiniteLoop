#!/usr/bin/env bash
# Downloads the launcher background media pinned in AscNet.Launcher/media.json into DIR and verifies each SHA-256.
#   packaging/fetch-media.sh DIR
# The same manifest feeds flake.nix. Needs curl, jq and sha256sum or shasum.
set -euo pipefail
manifest="$(cd "$(dirname "$0")/.." && pwd)/media.json"
dir="${1:?usage: fetch-media.sh DIR}"
mkdir -p "$dir"
repo="$(jq -r .repository "$manifest")"; tag="$(jq -r .tag "$manifest")"
sha() { if command -v sha256sum >/dev/null; then sha256sum "$1"; else shasum -a 256 "$1"; fi | cut -d' ' -f1; }
jq -r '.files | to_entries[] | "\(.key) \(.value)"' "$manifest" | while read -r name want; do
  curl -fsSL --retry 3 -o "$dir/$name" "https://github.com/$repo/releases/download/$tag/$name"
  got="$(sha "$dir/$name")"
  [ "$got" = "$want" ] || { echo "$name: sha256 $got, manifest says $want" >&2; exit 1; }
done
echo "media $tag verified in $dir"
