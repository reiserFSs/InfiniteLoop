---
name: pgr-headless-lua-wire-replay
description: Advanced. Use to replay real AscNet server packets through the original EN client Lua of a game mode headlessly; proves Lua consumers, not native combat or rendering.
---

# Goal
Show that real server packets drive the actual PGR Lua mode entities, requests and callbacks. Not proof of native combat, attributes, movie playback or rendered UI. Needs the EN client dump at `../PGR_DATA/en/` (your own extraction, not in the repo).

# Transcript
- Isolated working dir, own MongoDB and ports, synthetic accounts. Reuse the production encrypted `PacketCodec` and real Login handler.
- Allocate every request (including Handshake fences) from ONE transport id counter; reuse an id only for intentional retransmission.
- Emit the client request constructor's actual keys, not every property of a server DTO.
- Keep pushes before and after responses in order; never rewrite recorded packets to match the client.

# Loading the client
- Resolve EN Lua paths case-insensitively and load original bytes. Load real `XClass`, `XQueue`, `XTable`, `XNpcAttribType` and the FULL `XEnumConst` before mode modules (partial enums miss dependencies like `XTeam.Career`).
- If your Lua version rejects unrelated old syntax, extract only the needed original function bodies; never alter logic or replace the mode manager.
- Normalize JSON rows with the actual `XTable` field descriptors (absent strings, sparse arrays, numeric dictionary keys). Missing required rows stay errors.
- Lua `assert` returns all its arguments: `return (assert(path, msg))` in path helpers.

# Engine boundaries
- Real mode managers, request constructors and response handlers. Engine-only UI/event sinks may record calls but must not replace mode transitions.
- Derive character ids from the login CharacterList and real Robot/Character rows; don't invent BP or empty skill lists.
- Movie nodes: record the story token, call the real completion callback, report `rendered=false`.
- A retained finished run needs the real source presentation and final UI Close before the next Start; never clear the manager directly to hide a stale-run bug.

# Callback semantics
- Correlate by transport id; drop duplicate responses before callbacks (and skip their replay actions). Compare exact request bodies and array order.
- Honor callback-generated follow-up requests in order (e.g. Theatre shop entry requests the skill preview first).
- One ownership chain for fight entry, settlement and scene finish: a successful `FightSettleResponse` doesn't finish the scene; drive `XMVCA.XFuben:CallFinishFight` before the next locate. Track each FightId through exactly one settlement/finish.
- Popups: answer from the exact popup the client opened; a callback-free tip isn't a confirmation.
- Keep authoritative lookups strict: keep the exception stack and key, no guessed missing-row fallbacks.
- Negative probes the UI can't reach are excluded only when their correlated response has a known nonzero code; never reorder or drop the queued source request.
- Relog is a fresh client context: restore server snapshots and verify chapter-local counters, frozen offers, teams and settlement presentation. Server cumulative counters aren't client chapter counters.
- Recovered ending caches and terminal-result fields (Theatre4 `NotifyTheatre4AdventureSettle.AdventureData` vs `SettleData`) must stay distinct from the cleared active run; replay on login without regranting rewards.
- Theatre6 settlement: `FinishTime` is simulation seconds (`delta * TimeScale`, truncated to int32), not wall clock; don't invent speed caps from Theatre5.
- Progress observers must include pending transactions, selection counters and tickets; consecutive choice confirmations can legitimately leave map/phase unchanged.

# Report and clean up
Report exercised requests, callbacks, pushes, intentional duplicate drops and exclusions; keep server economy/persistence checks separate from client-consumer proof. Stop owned services; remove probes, synthetic data and transcripts; never commit captures.
