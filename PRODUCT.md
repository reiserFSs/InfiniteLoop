# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Stack

AscNet Launcher (`AscNet.Launcher/`, Rust, Windows x86_64). The UI is Slint (GPL-3.0; software renderer under Wine, GPU renderer otherwise), driven by a toolkit-free controller (`src/controller.rs`); the old Win32/GDI UI and the WebView2 plan are gone. The Rust core (download, install, patch, setup, updater, local services) stays. The front end must still run under Wine/CrossOver on macOS and Linux.

## Users

Punishing: Gray Raven players on the EN (Global), TW, KR, JP and CN clients, mostly non-technical. They use Windows 10/11, or a Mac through CrossOver/Wine. They want to play PGR against a private server running on their own machine.

## Product Purpose

The launcher takes a player from nothing to playing in one place:
- get the official client (download, adopt an existing install, update, verify and repair);
- run Setup, which builds the local AscNet server and the client patch;
- press Play, which starts MongoDB, the server and the game;
- show what is happening, and what to do, when something fails.

Success: the player always knows which state they are in and what the next step is.

## Positioning

Unlike Kuro's official launcher, this one also builds and runs a complete local backend (MongoDB plus the AscNet server) and patches the client. Nothing goes to Kuro except the client downloads. It is a fan project, not an official service.

## Operating Context

- **Packaging:** ships as `AscNetLauncher.zip` containing 7 files: the exe, `launcher.json`, `setup-local.ps1`, `supported-client.json`, `background.bmp`, `background.mp4` and `background.wav`.
- **Updates:** the in-app updater expects a release with exactly one asset named `AscNetLauncher.zip`.
- **Background media:** the background video, still image and music are rebuilt from the client's login screen for every game version (`Scripts/extract_launcher_background.py`). The video must be H.264 without B-frames (the launcher decodes it with OpenH264); the music is a 48 kHz PCM16 stereo WAV with a `smpl` loop.
- **Wine:** the launcher must work under Wine/CrossOver as well as on Windows.
- **Logs:** under `%LOCALAPPDATA%\AscNetLauncher\local\logs`, plus `launcher.log` next to the exe; players send these when reporting bugs.

## Capabilities and Constraints

- **Clients:** five regional clients (EN, TW, KR, JP, CN), pinned per version in `supported-client.json`. The language picker locks to the edition it detects in an existing install.
- **Long-running jobs:** downloads (tens of GB, resumable, pausable), Setup (installs Git, .NET, MongoDB, Rust and MSVC tools when missing; may ask for administrator approval), patch install, and server start and stop.
- **Client mods:** an opt-in game debug mode with per-mod toggles.
- **Required for the redesign:** the UI is translated into the five client languages, and stays sharp at 125–200% display scaling.

## Brand Commitments

- Name: "AscNet Launcher", for "Punishing: Gray Raven".
- Every launcher release carries the current game version's login art: the animated background, the still image and the music.
- Visual reference (user's choice, 2026-11-12): the launcher should look like Kuro's own official PGR / Wuthering Waves PC launchers, with the version art full screen, compact dark controls and small type. Their craft is the bar. Rejected: oversized type, big panels covering the art, and concept-themed layouts.

## Evidence on Hand

- Background media: `.runtime/launcher-package-1.0.23/background.{bmp,mp4,wav}`.
- Earlier screenshots: `.runtime/launcher-package-1.0.21/` and `1.0.22/`.
- Critique of the current UI: `.impeccable/critique/`.

There are no testimonials, user counts or endorsements; never invent them.

## Product Principles

- The next step is always obvious, and the launcher says why when it can't proceed.
- Speak like a player, not a developer: technical detail lives behind "Details" and in the logs.
- The game's art leads; the launcher frames it.
- Never surprise the player's install: confirm destructive or elevated actions in plain words.

## Accessibility & Inclusion

- UI text in the player's client language, with proper CJK fonts.
- Readable at high DPI.
- Full keyboard operation.
