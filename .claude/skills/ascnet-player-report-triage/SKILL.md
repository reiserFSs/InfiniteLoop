---
name: ascnet-player-report-triage
description: Use when a player reports an AscNet bug (error popup, stuck screen, missing reward) and you must go from logs to a root cause and a pinned regression test.
---

# 1. Collect logs (ask for all that apply)
- Server: launcher install `%LOCALAPPDATA%\AscNetLauncher\local\logs\server.log` plus `server.previous.log` (and `mongod.log`); or the console output of `dotnet run`. Verbosity `Debug` (default) includes full response content; see `AscNet.Launcher/docs/player-diagnostics.md`.
- `launcher.log` beside `AscNetLauncher.exe`; `ascnet-patch.log` beside `PGR.exe` (last launch only).
- Game log: newest `PGR/log/*.log` under the client's persistentDataPath (Windows: `%USERPROFILE%\AppData\LocalLow\kurogame\PGR`; elsewhere inside the Wine prefix). Grep `<Error>` and Lua tracebacks.
- Region and exact steps (which screen, which button).

# 2. Read the server side
- Request lines: `Request received: nameLength=...`; responses/pushes: `<Name>Response, {json}`. Find the request around the reported time and read its response `Code`.
- Map an error code with `Resources/table/share/text/CodeText.tsv` (the popup text the player saw). Code 0 with a broken screen usually means a missing response field the Lua consumer reads.
- "Request handler not found" = unimplemented request: first check the client really issues it (`ascnet-handler-checklist`).

# 3. Check the player's state
`mongosh asc_net` (use the player's configured database name), collections `players`, `characters`, `inventory`, `stages`. Query by Uid; read-only. Repairs are destructive: preview, get consent, stop the server first.

# 4. Probe a live client (optional, your own client)
`Scripts/pgr.py` drives the Proxima inspector; setup in `pgr-lua-mods`. Typical probes:
- Hook `XNetwork.Call` to log request bodies the client really sends; hooks vanish on game restart.
- Inspect client models (`XDataCenter.*`) to see what the client believes the state is.

# 5. Confirm the hypothesis from tables
Compute from `Resources/table/**/*.tsv` before changing code. Example: an anchor score of 1530 against a 315 threshold leaves 1530-315 = 1215; if the server returns something else, the formula is wrong, not the player's data.

# 6. Pin it
Reproduce in a focused `Validate<Feature>Compatibility` test that FAILS before the fix (`ascnet-handler-checklist`), fix the shared root cause, rerun the selector, then the full diff if shared code changed.
