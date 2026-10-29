# InfiniteLoop

Play **Punishing: Gray Raven** on your own PC, against a private server that runs on your machine. InfiniteLoop is a branch of [AscNet](https://github.com/rafi1212122/AscNet).

This is a fan project, not an official service. Everything runs locally: your progress is stored on your computer, and there are no other players unless you share your server.

## What you need

- A Windows 10/11 PC (64-bit). macOS and Linux work through CrossOver or Wine.
- About 72 GB of free space if the launcher downloads the game for you.
- An internet connection for the first setup and the game download.

Game version **4.8.0** is supported, in these editions: English (Global), 繁體中文 (Taiwan), 한국어 (Korea), 日本語 (Japan) and 简体中文 (China).

## Getting started

1. Download `AscNetLauncher.zip` from the [latest release](https://github.com/reiserFSs/InfiniteLoop/releases/latest) and extract it to its own folder.
2. Run `AscNetLauncher.exe`.
3. Press **SELECT GAME** and choose a language and an install folder:
   - An empty folder: press **DOWNLOAD** to install the game.
   - A folder that already has the game (official launcher or Steam): press **ADOPT** to use it, or **UPDATE** if it's an older version.
4. Press **SETUP**. The first setup downloads the tools it needs and builds your local server, so it takes a while.
5. Press **PLAY**.
6. In the game's login screen, enter any email and password. The account is created the first time you use it; use the same details next time to get the same save.

After the first time, just open the launcher and press **PLAY**.

## Tips

- **Chat commands:** type `/help` in the in-game chat for commands that give currency, items, characters, levels and stage clears.
- **GM menu and client mods:** in the launcher, open **SETTINGS → CLIENT MODS**, switch on **Client mods (game debug mode)**, and enable the mods you want. Restart the game afterwards. The included GM menu opens with **Ctrl+Shift+D**. More in [Mods](Mods/README.md).
- **Repairing the game:** **SETTINGS → Game client…** has **VERIFY & REPAIR**, which re-checks every game file and re-downloads anything damaged.

## What works

Most of the game is playable: story, characters and gear, gacha, shops, tasks, events, guilds, dorms, friends and the regular PvE modes.

Some things are missing or limited:

- **Multiplayer and co-op rooms** don't work yet.
- **Some older or minor events** have no server support.
- **A few rewards, shops and banners stay closed** because the game files don't contain the data needed to run them; [Docs/closed.md](Docs/closed.md) lists each one.
- **Event calendars:** the game files don't include the official event calendar, so some events are simply always open.

## Having a problem?

When you report a bug, include what you did, what happened, your game language, and these log files (`%LOCALAPPDATA%` is `C:\Users\<you>\AppData\Local`):

| Log | Where |
| --- | --- |
| Server | `%LOCALAPPDATA%\AscNetLauncher\local\logs\server.log` (the previous run is `server.previous.log`) |
| Launcher | `launcher.log`, next to `AscNetLauncher.exe` |
| Game patch | `ascnet-patch.log`, next to `PGR.exe` in the game folder |
| Game | the newest file in `%USERPROFILE%\AppData\LocalLow\kurogame\PGR\log\` |

If the game doesn't reach the login screen, the [player diagnostics guide](AscNet.Launcher/docs/player-diagnostics.md) explains what the patch log lines mean.

## For developers

Building the server, running it without the launcher, the protocol and feature notes, and the test suite are in the [developer guide](Docs/development.md). Repository rules for contributors and agents are in [AGENTS.md](AGENTS.md).
