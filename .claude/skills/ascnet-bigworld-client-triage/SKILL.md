---
name: ascnet-bigworld-client-triage
description: Use for AscNet Babylonia (BigWorld) symptoms in a real client: infinite load, empty level, missing NPC/object, interaction deadlock, stalled quest, "Request handler not found".
---

# Evidence first
1. Client log: newest `PGR/log/*.log` under persistentDataPath (see `pgr-lua-mods`). Grep `^<(Error|Exception)>`, dedupe by message with digits normalized, read the last exception's stack. Ignore noise: `XConfig TryGet`, `SetResolution`, font-width errors, `NotifyEquipGuideData`, `NotifyDlcQuestActivate`, "referenced script ... missing".
   - `[XLevelActionList]` lines show which quest/interaction action segments ran and finished.
   - `[XLevel][Deserialize] [controllerCount][actorCount]` shows what the server's RepLevel carried.
2. Packet dumps: `.runtime/bigworld-packet-dumps/*.json` when the server runs with dumps on. Decode XRpc args with `unpack_messagepack(base64)` from `Scripts/decode_pgr_pcap.py`. Filter out `XRpcNpcPositionAndRotation`, `RepThreatComponent`, `RepBuff`, `RpcRemoveBuff`, `RpcAddAttribAdditive`. Search by RPC name inside the JSON (`d[0]`): level switches travel as `XRpcCommon` `RpcPlayerSwitchLevelNotify`.
3. Retail oracle: `babylonia.pcap` in the repo root (district 4001 only); summarize with `Scripts/decode_pgr_pcap.py --summary` to see request/response pairs.
4. Native contracts: if you have a il2cpp metadata dump of the client, note it is a specific version; its RVAs don't match another build's `GameAssembly.dll`.

# Known pitfalls (each caused a real bug)
- Level config groups are per sector bundle (`sceneconfig/<sector lowercase>.ab`); group ids repeat across bundles. Key by `(Sector, ConfigGroupId)` via `BigWorldModule.GroupKey`. Place ids start at 1; don't filter below 1000.
- Gameplay actors are per level (`GameplayLevelId`/`GameplayLevelIdsStr`). Replicate only that level's actor (nil content, no components): missing = empty level, foreign = lookups for another level's NPCs.
- Every action type needs an ExecMode row (quest objectives, FixProcessors, `LevelInteractOption.tsv`), else the runner throws mid-interaction. The actions selector enforces it.
- One client segment per ActionListId in flight (dictionary keyed by list id; a second in the same tick throws "same key").
- Ungated quests activate only in their `LevelId`. A Ready quest whose first step has `UnderTakeSelfQuest` opens that step while Ready.
- Client XRpc families `XRpcCommon`, `XRpcComponentAction`, `XRpcActorAction` need a response; retail acks engine-simulated ones with Code 0.
- `RpcPlayerSwitchLevelNotify` carries the world-entry RepLevel with the team at the target pose and empty `NpcTravelInfos` (inferred, confirmed working).

# Fix discipline
- Data comes from importers (`Scripts/import_bigworld_tables_4_7.py`, `Scripts/import_bigworld_scene_objects_4_7.py`, `Scripts/import_scene_colliders_4_8.py`; all take `--version 4.8|4.9`, default 4.9; tables tracked in `manifest.json` are skipped); diff old and new TSVs to prove existing rows are unchanged.
- Add a regression assertion to the matching `--big-world-*-only` selector (`grep -n 'big-world' AscNet.Test/Program.cs`); run every selector the change touches, plus Theatre5/6 when DTOs are shared.
- Account repairs in the `players` / `big_world_state` collections are destructive: preview, get consent, stop the server first.
