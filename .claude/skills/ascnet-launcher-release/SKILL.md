---
name: ascnet-launcher-release
description: "Release an AscNet launcher via the GitHub Actions workflow: bump, push, dispatch, verify; screenshot only for game-version updates"
---

# AscNet launcher release

Repo reiserFSs/InfiniteLoop (git remote `infinite`). Tags are `1.0.N`, titles `AscNet Launcher v1.0.N`. CI builds, packages, smoke-tests and publishes; nothing is built or uploaded by hand. Details: `Docs/development.md` ("Launcher releases").

1. Local master == `infinite/master`; check `gh release list` (skipped numbers are fine).
2. Bump in one commit (each must match once): `AscNet.Launcher/Cargo.toml` `version`, the `name = "ascnet-launcher"` entry in `AscNet.Launcher/Cargo.lock`, the `launcher 1.0.N, patch 0.X.0` line in `Docs/development.md`. Run `cargo test --offline` in `AscNet.Launcher` (rustup toolchain first on PATH). Push: `git push infinite master:master` (if the user edited README on GitHub, `git merge infinite/master`; rebase is blocked by the operator's uncommitted `Resources/Configs/config.json`; never stash it).
3. Notes: player-facing bullets for `git log <prev>..HEAD` only; claim only committed, confirmed fixes; credit non-owner authors; server/patch changes say "applied by running Setup". One line, `\n` for breaks.
4. Dispatch: `gh workflow run launcher-release.yml --repo reiserFSs/InfiniteLoop -f version=1.0.N -f windows_license=false -f notes='...'`. `windows_license=true` only one release after the first LICENSE-aware launcher (older launchers reject an 8th member). Screenshot (`-f screenshot=<https url or committed png path>`) ONLY for game-version releases (new client / new launcher background); it must not show the whole desktop and must be looked at first.
5. Watch: `gh run watch`. The version job fails early on any mismatch; `release` verifies `releases/latest`, assets and digests itself. Afterwards `gh release view 1.0.N` for a final glance.
6. Media: `background.*` come from the pinned media release in `AscNet.Launcher/media.json`. New game visuals: regenerate with `Scripts/extract_launcher_background.py` (no B-frames; `AscNetLauncher --check-media`), `gh release create launcher-media-N --prerelease --latest=false <3 files>`, update `tag` and hashes in `media.json`, commit before releasing.
7. Optional wine smoke of the Windows zip is no longer part of the release; run it only when changing Windows UI/runtime code (CrossOver wine, WINEPREFIX under $HOME).
