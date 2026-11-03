---
name: pgr-packet-contract-reconstruction
description: Use when implementing or fixing an AscNet gameplay flow from captures, client Lua and client tables; captures are test oracles, never runtime payload sources.
---

# Goal
Bring one flow to retail-compatible behavior end to end: client gate, wire exchange, validation, mutation, persistence, visible result. Rules come from `AGENTS.MD` (hardcoding rules).

# Where the evidence lives
- Client dump: `../PGR_DATA/<region>/` (a sibling of the repo, produced by your own client extraction, not in the repo). Use `en` (4.8); `cn` only when EN lacks the source.
- Captures: `*.pcap` in the repo root or your own. Decode only what you need:
  `python3 Scripts/decode_pgr_pcap.py <capture> --summary .runtime/<name>-summary.jsonl`, add `--dump-dir .runtime/<name>-dump --dump-name <Request> --dump-name <Response>` for exact payloads. `.runtime/` is never committed.

# Steps
1. Name the exact user action and the observed failure. Don't start from a guessed handler.
2. Search existing captures, decoded summaries, client logs, handlers and compatibility tests before asking a human for anything. If the contract is still absent, request the smallest controlled capture (start state, action, one failure case, relog or not, end state).
3. Trace the client: Lua request issuance, response consumers, caches, UI gates, follow-up requests, table schemas.
4. Rebuild the chronological exchange: request ids, pushes before and after the response, MessagePack keys/types, wrapper objects, nested list shapes, error codes.
5. Write down six contracts before editing: client gating, wire schema, validation/error boundaries, durable mutation, response/push order, relog/retry/idempotence.
6. Classify every runtime output as: authoritative table, persisted state, validated request input, authoritative clock, deterministic computation, or an explicit AGENTS.MD curated exception. Anything else is hardcoding.

# Never hardcode
- Never embed captured payloads, standings, ids, balances, timestamps, NPC waves or other retail values in server code or runtime config. A JSON/resource file copied from a capture is still hardcoding.
- If no authoritative source exists, report the missing source (and record it in `Docs/closed.md`). No stage-specific snapshot, guessed mapping or silent fallback.
- Before delivery grep the changed runtime files for capture-specific constants; prove two distinct inputs give independently derived outputs.

# Data procurement order
existing `Resources/table/**/*.tsv` -> EN client dump -> CN client dump -> explicit curated exception -> targeted capture (behavior only) -> report missing authority.
Import rows with the existing `Scripts/import_*.py` pattern or extend the table generator (`AscNet.Table`); never hand-edit generated output. Read rows with `TableReaderV2.Parse<T>()`. Confirm semantics from Lua consumers, not column names.

# Implement
Find the first divergent boundary, fix the shared root cause minimally, reuse existing DTOs/readers/`RewardHandler`/persistence. Migrate every caller; no aliases or shims. See `ascnet-handler-checklist`.

# Verify
Add coverage in `AscNet.Test` (see `ascnet-handler-checklist`): dispatch, exact response schema and packet order, success and failure boundaries, mutation, save/reload, retry/idempotence, two distinct inputs. Compare structure, not copied scalars. Run the narrow selector, then the full suite once if shared code changed (`ascnet-differential-full-harness`). Server tests do not prove client presentation; check in a real client when one is available.

# Native/schema pitfalls
- A corpus can contain the right producer Lua but a stale generated enum; verify enums and table headers against the installed client bundles before weakening equality.
- Check whether a saved response overwrites request fields (Theatre5 settlement replaces `ResultData.WorldData`); it can't reveal the incoming value.
- Run original Lua producers, not transcriptions. Don't build a regression's client report from the server projection under test: both sides share the bug. Run the new test against the old code and require a failure.
- xLua (Lua 5.3) integer conversion maps fractional doubles to 0 and narrows int64 to int32 low bits; Python `int()` / CLR `(int)` don't. Probe fractional, integral, range edges.
- Collection setters can clear whole maps (Theatre4 `UpdateColors`): partial snapshots must carry all entries; replay the real notification order.
- Units differ per consumer (Theatre4 map HP basis 10000 vs whole-percent frozen encounters); convert at the boundary and repair legacy values idempotently only with unambiguous provenance.
- When removing a persisted field keep old BSON loadable (`BsonIgnoreExtraElements` where appropriate).
- A grep window can miss records deep in large JSON: parse the full file and select by id before declaring a record absent.
