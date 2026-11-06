# macOS port (parked design note)

Status: design agreed 2026-09-27; not implemented. Related: `client-download.md`.

## Decisions
- **UI:** cross-platform Rust UI, shared with Windows. No SwiftUI.
- **Runtime:** require the user's own licensed CrossOver at first. A free runtime fallback is out of scope for now.
- **Signing:** no Apple Developer ID will be obtained. The macOS build ships unsigned and unnotarized (ad-hoc
  signature only). Users must allow it once past Gatekeeper (Open from the context menu, or
  `xattr -d com.apple.quarantine`). [INFERENCE] Files written by the launcher's own updater are not quarantined,
  so self-updates should not trigger Gatekeeper again; verify before release.

## Flow
1. Detect CrossOver (`/Applications/CrossOver.app/Contents/SharedSupport/CrossOver`); refuse with guidance if absent.
2. Create an AscNet bottle through CrossOver's documented CLI (`cxbottle --create --template win10_64`) and install
   corefonts, d3dcompiler_47 and vcrun2015–2022. Registry: RetinaMode=N, 1920x1080 window settings.
3. Download the pinned client from Kuro into the bottle (see `client-download.md`). No Steam in the bottle.
4. Apply the Rosetta client patches in Rust, with hash-recorded backups:
   - `PGRBase.dll`: `0F 1F C2` → `90 90 90` at the unique signature
     `21 ca 21 ca 81 f2 b3 a5 d6 7a [0f 1f c2] 41 8b 0a 50 48 8d 05` (stable across builds so far).
   - `GameAssembly.dll`: the single register-form NOP (`[REX] 0F 1F C0-C7` followed by a load, e.g.
     `41 0F 1F C1 | 44 8B 0A`, CN `0F 1F C2 | 41 8B 11`) inside `.tvm0`. The per-build masked patterns of
     `pgr_apply_patches.py` stopped matching TW/KR/JP/CN 4.9.1; the structural signature in
     `Scripts/apply_rosetta_patches.py` is unique in all five 4.9.1 builds (EN reproduces the installed `3e9e3d54…`).
     TW/KR/JP/CN patched files are untested in game; their hashes are in `supported-client.json`.
   - Unity startup stub: already implemented in `src/pgrbase.rs`.
   `supported-client.json` already accepts the Rosetta-patched `GameAssembly.dll` hash.
5. Server: macOS setup installs the .NET 8 SDK and MongoDB from official archives
   (not Homebrew), clones/builds AscNet, and builds or downloads AscNet.Patch (Windows DLLs).
6. Install the AscNet patch (lucia.dll, version.dll, KRSDK.dll) with the existing `install.rs`.
7. Launch `wineloader PGR.exe` from the game directory with the environment from `launch-pgr-ascnet.sh`
   (D3DMetal/DXMT paths, `d3d11,dxgi=n,b`, esync/fsync/msync off) **plus** `version=n,b` and
   `ASCNET_PATCH_ORIGIN`, which that script omits. Run MongoDB and the server natively.

## Code reuse
- Shared as is: `install.rs`, `package.rs`, `pgrbase.rs`, `supported-client.json`, updater verification.
- New: Rosetta patcher, CrossOver bottle manager, Wine launcher, client downloader, macOS setup script,
  cross-platform UI (replaces the Win32 UI). Background video needs a per-platform decoder
  (Media Foundation / AVFoundation) or a bundled decoder; decide when choosing the UI toolkit.
- Updater: releases gain a separate macOS archive.

## Order
1. Shared core: Rosetta patcher with restorable backups, Wine launcher, client downloader.
2. Cross-platform UI on Windows first (parity with the current launcher).
3. macOS: CrossOver detection, bottle setup, macOS setup script, play.
4. macOS release archive and updater support.

## Risks
- New `GameAssembly.dll` Rosetta pattern needed per game update.
- Rosetta is being phased out; [INFERENCE] CrossOver is the only runtime with an announced ARM64 path.
- DXMT requires macOS 14+ on Apple Silicon.
- Unsigned app: Gatekeeper friction on first launch.
- Client/resource version pinning and Kuro CDN changes (see `client-download.md`).
