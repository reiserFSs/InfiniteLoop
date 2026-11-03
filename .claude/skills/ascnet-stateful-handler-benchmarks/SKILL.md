---
name: ascnet-stateful-handler-benchmarks
description: Use to benchmark an AscNet login or gameplay handler before/after a change with frozen BSON fixtures, an isolated MongoDB and real socket responses.
---

# Goal
Time a real handler without contaminating the baseline, touching live accounts, or timing fixture setup.

# Isolate
- Before editing runtime code, copy the built test assemblies/deps to a temp baseline dir (no tables, Configs, Data, .runtime).
- Temp resource cwd with symlinks to authoritative resources and its OWN `Configs/config.json`; private `mongod` on an unused port, unique database name. Never the live database.
- A throwaway net8 probe references the preserved DLLs directly (plain assembly references + Microsoft.AspNetCore.App), not ProjectReferences, which rebuild and contaminate the baseline. Swap rebuilt DLLs in only for the "after" run.
- Set the cwd before `Common` initializes; assert `Common.config`, the database namespace and the Mongo endpoint before any fixture write. Same build configuration and logging for before/after.

# Fixtures
- `AscNet.Test.Program` has `CreateDrawCompatibilityCharacter/Player/Inventory`, `CreateLoginAccountCompatibilityStage` and `LoopbackSessionHarness`; re-read their signatures, they change.
- Pick ownable characters from tables (`Character.IsOwnableCharacter`), create via `AddCharacter`; don't fabricate maxima.
- Freeze Player/Character/Inventory/Stage as BSON ONCE with the baseline binary; assign non-empty synthetic ObjectIds before serializing. Restore from BSON outside each timed sample (normalizers mutate).
- Real isolated Mongo collections for `FromUid`/full login; recording proxies can't run the aggregate/LINQ queries.

# Measure
- Time the exact handler boundary (cached delegate), or label the narrower `Character.FromUid` + `DoLogin` boundary. Not fixture construction.
- Full login: restore the four models, pass a synthetic persisted login session to `LoginRequestHandler`; include model loads, response production, serialization/encryption/socket writes and the final save.
- Start the loopback packet reader before invoking the producer; wait for BOTH reader completion and producer return (find the current final startup push in `DoLogin`).
- Stopwatch plus `GC.GetAllocatedBytesForCurrentThread` on the producer thread (excludes reader/other threads). Dispose the harness and await `Session.Completion` between cases; don't register the synthetic session in the real registry.
- Run before/after sequentially, same warm-up; report medians, raw samples and counts. Server latency is not client load time.

# Compare behavior
- Compare persisted Character BSON, repeat-load stability, ordered packet names, and `NotifyLogin` character/equipment/fashion/partner fields exactly. `MessagePackSerializer.ConvertToJson` and BSON extended JSON are enough to decode.
- Never blanket-strip time/id fields: list the differences and verify against real clock deltas (`NotifyChatLoginData.RefreshTime` comes from process start).
- Generic pre-fight fixture: only `FightData.FightId` and `Seed` are random; assert Code 0, positions and the active session FightId. Recheck for other modes.

# Finish
Run the focused character/login selectors, and the full diff if shared login code changed (`ascnet-differential-full-harness`). Stop owned processes; remove temp probes, databases and generated artifacts.
