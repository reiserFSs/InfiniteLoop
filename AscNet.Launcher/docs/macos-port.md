# macOS port

Status (2026-11-14): implemented. The crate builds natively (`aarch64-apple-darwin`) and the Windows build is unchanged. Design agreed 2026-09-27, revised 2026-11-13. Related: `client-download.md`.

## Status
- Done and exercised: the `.app` bundle launches, plays the background video and shows Home with the CrossOver and Bottle rows; CrossOver consent prompt; `cargo test --offline`; the Windows exe (`cargo xwin`).
- Slices: `src/mac/crossover.rs` (detect, signed install), `src/mac/bottle.rs` (create/repair the `AscNet` bottle, `ASCNET_BOTTLE=AscNet-test-<n>` for tests), `src/mac/wine.rs` (launch, "PGR.exe running"), `src/mac/setup.rs` (toolchain, server build, patch DLLs, native MongoDB/server), `src/rosetta.rs`, `src/controller/mac.rs` (the CrossOver and bottle steps in the controller).
- Shared-flow hooks: `local::prepare` (macOS setup, then Rosetta, then the AscNet patch install), `local::LocalRuntime`, `install::launch`, `install::game_running`, `local::resource_dir` (`Contents/Resources` in the bundle), `local::data_base`.
- Not verified here: see the integration report; the full Kuro client download and the in-game run depend on the pinned client being fetched.

## Commands
```bash
cargo test --offline
cargo xwin build --release --target x86_64-pc-windows-msvc
packaging/macos/package.sh            # target/macos/{AscNet Launcher.app, AscNetLauncher-macOS.zip}; media from .runtime/launcher-media or ASCNET_LAUNCHER_MEDIA
ASCNET_BOTTLE=AscNet-test-1 ASCNET_UI_START=files "target/macos/AscNet Launcher.app/Contents/MacOS/AscNetLauncher"
```
`ASCNET_UI_START` accepts `settings`, `files`, `setup` and (macOS) `crossover`. `AscNetLauncher --check-media <mp4>` runs the decoder's validation (the packaging gate).

## Home on macOS
Rows: CrossOver, Bottle, Game, AscNet patch, Local server. The big button follows the prerequisites: Install CrossOver (consent dialog: 14-day trial, own licence, official installer from codeweavers.com, signature checked) → Create/Repair bottle → Select game (Game files default to `C:\Games\Punishing Gray Raven` inside the bottle; Steam copies are never adopted automatically) → Set up → Play.

## Updates
Releases carry a separate `AscNetLauncher-macOS.zip` (the whole ad-hoc signed app). The updater verifies GitHub's SHA-256 digest, the bundle's file list, the code signature and that the new executable starts, then swaps the app and keeps the old one until the new launcher's first start. Both archives carry `LICENSE`; see `Docs/development.md` for the release order that keeps Windows 1.0.24 and older able to update.

## Decisions
- **UI:** Slint, shared with Windows (done for the Windows build: `ui/*.slint`, `src/ui.rs`, state in `src/controller.rs`). No SwiftUI.
- **Runtime (revised 2026-11-13):** CrossOver. When it is missing, the launcher downloads CodeWeavers' official installer from codeweavers.com and installs it to `/Applications`. The player runs it on the 14-day trial and must buy their own licence. AscNet never redistributes CrossOver. A free runtime fallback is still out of scope.
- **Signing:** no Apple Developer ID will be obtained. The macOS build ships unsigned and unnotarized (ad-hoc
  signature only). Users must allow it once past Gatekeeper (Open from the context menu, or
  `xattr -d com.apple.quarantine`). [INFERENCE] Files written by the launcher's own updater are not quarantined,
  so self-updates should not trigger Gatekeeper again; verify before release.

## Flow
1. Detect CrossOver (`/Applications/CrossOver.app/Contents/SharedSupport/CrossOver`). If it is absent, explain the trial and licence, then download and install the official CodeWeavers build with the player's consent.
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
- New: Rosetta patcher, CrossOver bottle manager, Wine launcher, client downloader, macOS setup script. The cross-platform UI replaced the Win32 UI. Background media is `src/media/`: pure-Rust
  H.264 (`mp4` + `openh264`, B-frame-free `background.mp4` required) and WAV-loop music (`cpal`), shared by Windows, Wine and macOS.
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

## App icon

Art lives in `packaging/icon/`: `ascnet.svg` (white A with the red slash) and `ascnet-small.svg` (plain A, used below 64 px where the slash reads as a broken leg). `packaging/icon/make-icons.py` renders them with headless Brave/Chrome (rsvg-convert is not installed; qlmanage paints an opaque background) and writes `packaging/macos/AscNet.icns` (iconutil), `packaging/icon/ascnet.ico` (16–256 px, tile edge to edge with a small radius) and `ascnet-256.png` (the Slint window icon). The `.ico` is embedded in the Windows exe by `build.rs` (winresource; needs `llvm-rc` on PATH when cross-compiling). Re-run the script after editing either SVG and commit the outputs.
