---
name: ascnet-parallel-agent-work
description: Use when several agents or people edit, build and test this AscNet checkout at once; prevents build collisions, wiped shared files and flaky tests.
---

# Builds and tests share bin/obj: serialize
`lockf /tmp/ascnet-build.lock sh -c 'dotnet build AscNet.Test/AscNet.Test.csproj && dotnet run --no-build --project AscNet.Test/AscNet.Test.csproj -- --<feature>-only'`
(`flock` on Linux.) Never run two builds or two test runs on the same tree at the same time.

# Never revert shared files
Never `git checkout`, `git reset`, `git restore` or `git stash` files other agents may be editing. A checkout of `Resources/table/manifest.json` once wiped other agents' entries. To prove a test fails on old code, temporarily write `git show HEAD:<your file>` content into YOUR file only, then restore your version by editing.

# Shared files: minimal edits
`AscNet.Test/Program.cs`, `AccountModule.cs`, `Player.cs`, `Types.cs`, `Resources/table/manifest.json`, `Docs/*`, `AGENTS.MD`: re-read immediately before editing, change only your lines, never rewrite the file. Prefer new partial files (`Program.<Feature>.cs`, `Player.<Feature>.cs`) and keep shared-file touches to one registration line.

# Test databases
Use a unique `Database.Name` per run in the test output `Configs/config.json`. The full run needs `ascnet_manual_full_<tag>`. Drop your databases afterwards.
The config is read relative to the working directory: run `dotnet AscNet.Test.dll …` from `AscNet.Test/bin/Debug/net8.0` (or `dotnet run --project` from the repo root with that config in place). Running from elsewhere silently falls back to the default `asc_net` — the operator's real database.

# Flakiness
`LoopbackSessionHarness` sessions are dropped after a 10 s idle timeout (`Session.ClientLoop`), so slow assertions between requests flake under full-suite load. Tests that call handlers directly and never write to the socket should start the harness with `startClientLoop: false`. Otherwise do slow work before opening the session or after reading the last packet; rerun the failing selector alone before blaming code.
Pushes go through the async `PacketWriter` queue, and `Session` sends a trailing `NotifyTask` after many requests: wait for an expected push with a blocking read loop (not one non-blocking `TryReadAvailablePacket`), give `ReadResponsePayload` a `maxPacketsToRead` when reward/task pushes precede the response, and let "no more packets" checks tolerate trailing `NotifyTask`.
Tests must not depend on today's date: pin every clock the path reads (`DrawManager.UtcNow`, `TaskModule.ProgressClock`, …) and avoid `First(...)` lookups on per-week generated boards.

# Roles
- Agents build and run their own selector.
- The integrator runs the full `--keep-going` diff versus HEAD before committing (`ascnet-differential-full-harness`). Don't commit unless asked.
- `Resources/Configs/config.json` `Client` block (Debug/ShowInspector) is operator-local: never commit it, never revert it.
