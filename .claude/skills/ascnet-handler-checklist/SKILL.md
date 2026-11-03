---
name: ascnet-handler-checklist
description: Use when implementing or fixing an AscNet request handler (client request -> response/pushes -> persisted state) and its AscNet.Test compatibility test.
---

# Before coding
1. Does the client actually issue the request? Find the caller in the client Lua (`../PGR_DATA/<region>/`, not in the repo). No caller, or commented out = do NOT implement; report it with file:line.
2. The client Lua is the wire contract: read the request constructor and every response/push consumer. Every field the consumer reads must be present: a missing key is nil and can abort a whole screen (e.g. `QueryPlayerDetail` without `CurrentWearNameplate`).
3. Bounds and limits come from the client (tables, Lua, UI), never invented (the collection wall was 12x8 invented vs 16x9 in the client).
4. Compare like with like in enum/type columns (a Transfinite achievement `Type` was compared to a stage-group `Type`).
5. Regions number some tables differently (TW favourite mails); server tables are EN.
6. Missing authoritative source (drop table, pity law, catalog): don't invent; list in `Docs/closed.md`. Item semantics changes: update `Docs/items.md`. Hardcoding rules: `AGENTS.MD`.

# Wire
- Handler: `[RequestPacketHandler("<Name>Request")]`, deserialize the request DTO, `SendResponse(new <Name>Response{...}, p.Id)`; pushes via `session.SendPush`.
- DTOs use `[MessagePackObject(true)]` (named keys). NEVER put Database model types on the wire: unregistered types crash serialization; map to DTOs.
- XMessagePack encodes an empty Lua table as nil: list/map request fields must accept null.
- Reward pushes are sent before the response; tests read with `ReadResponsePayload(..., maxPacketsToRead: N)`.

# State
- One-time claims: `RewardHandler.ApplyRewardsOnceAndPersist` with a stable claim key scoped to the period/cycle.
- Resets: `TaskModule.CurrentDailyResetPeriod` / `CurrentWeeklyResetPeriod` (from Config.tsv; authored 07:00 UTC, Monday). Don't hand-roll day math.
- `Save()` every touched model (player, character, inventory, stage). Old BSON documents must still load (new fields defaulted, removed fields `BsonIgnoreExtraElements`).

# Test
- Put `Validate<Feature>Compatibility()` in `AscNet.Test/Program.<Feature>.cs` (a new partial file).
- Wire it in `AscNet.Test/Program.cs` in two places: an `if (args.Contains("--<feature>-only")) { Validate...(); return; }` selector block, and a `Step("Validate<Feature>Compatibility", () => ...)` in the full sequence.
- It must fail on the old code: prove it by temporarily restoring `git show HEAD:<file>` of the handler (do not use `git checkout`; see `ascnet-parallel-agent-work`), seeing red, then restoring your version.
- Cover: response schema and packet order, a failure boundary, mutation, two distinct inputs (derived outputs), repeat/claim-once, and a save + BSON reload.
- Run `dotnet run --project AscNet.Test/AscNet.Test.csproj -- --<feature>-only`; list selectors with `grep -n 'args.Contains("--' AscNet.Test/Program.cs`. Shared-code change: full `--keep-going` diff (`ascnet-differential-full-harness`).
