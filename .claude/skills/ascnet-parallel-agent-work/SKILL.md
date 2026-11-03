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

# Flakiness
`LoopbackSessionHarness` sessions are dropped after a 10 s idle timeout (`Session.ClientLoop`), so slow assertions between requests flake under full-suite load. Do slow work before opening the session or after reading the last packet; rerun the failing selector alone before blaming code.

# Roles
- Agents build and run their own selector.
- The integrator runs the full `--keep-going` diff versus HEAD before committing (`ascnet-differential-full-harness`). Don't commit unless asked.
- `Resources/Configs/config.json` `Client` block (Debug/ShowInspector) is operator-local: never commit it, never revert it.
