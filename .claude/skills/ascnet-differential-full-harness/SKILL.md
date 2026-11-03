---
name: ascnet-differential-full-harness
description: Use after changing shared AscNet code (login, task, inventory, reward, persistence) to prove the full AscNet.Test suite has no regressions versus HEAD; not for debugging one test.
---

# Why
The plain full run stops at the first failure and some failures already exist on HEAD. `--keep-going` runs everything and prints one line per test:
`@@PASS <Name>` / `@@FAIL <Name> <Exception>: <message>`; exit code is non-zero if any failed.

# Steps
1. Working tree run:
   ```
   dotnet build AscNet.Test/AscNet.Test.csproj
   mkdir -p AscNet.Test/bin/Debug/net8.0/Configs
   echo '{"Database":{"Name":"ascnet_manual_full_cur"}}' > AscNet.Test/bin/Debug/net8.0/Configs/config.json
   (cd AscNet.Test/bin/Debug/net8.0 && dotnet AscNet.Test.dll --keep-going) | grep '^@@' | sort > /tmp/cur.txt
   ```
   The database name MUST start with `ascnet_manual_full_` (a guild validator requires it). Needs a local MongoDB. Never point it at a real database.
2. Baseline: `git worktree add --detach /tmp/ascnet-head HEAD`, then repeat step 1 inside it with name `ascnet_manual_full_head` and output `/tmp/head.txt`. Do not run both at once (shared MongoDB load makes timing tests flake; sessions drop after a 10 s idle).
3. `diff /tmp/head.txt /tmp/cur.txt`. Empty diff = no regressions; failures present in both are pre-existing, report them. A test that fails only in `cur` is yours; a flaky one should pass when rerun alone with its `--...-only` selector.
4. Clean up: `git worktree remove --force /tmp/ascnet-head`, `rm /tmp/head.txt /tmp/cur.txt`, drop both `ascnet_manual_full_*` databases (`mongosh --eval 'db.getSiblingDB("ascnet_manual_full_cur").dropDatabase()'`, same for head).

# Notes
- Runs rewrite `Configs/config.json` in the test output dir; that file is not the server's `Resources/Configs/config.json`.
- Untracked new files are not in the HEAD worktree: that is expected, new tests simply do not appear on the baseline side.
