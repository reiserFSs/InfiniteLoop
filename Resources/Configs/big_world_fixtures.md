# BigWorld fixture provenance

This directory contains BigWorld payload fixtures used by `AscNet.GameServer.Handlers.DlcModule`.

## Extraction source

- Capture/session: `20260706_1451.pcap` in the repository root.
- Decoder: `Scripts/decode_pgr_pcap.py`.
- Extraction method: decode the game TCP stream with the repository Haru key, dump the matching response/push payload bytes, and store raw MessagePack payload bytes as base64 sidecars.
- The adjacent `.json` files are decoded inspection copies only; the server reads the `.msgpack.b64` sidecars for binary fidelity.

## Fixture files

- `big_world_enter_world_response.msgpack.b64` / `big_world_enter_world_response.json`
  - Pcap-verified source: `BigWorldEnterWorldResponse` payload from `20260706_1451.pcap`.
  - Runtime note: player identity and commander-fashion data are patched from the active session before sending; do not bake account-specific values into new fixtures.
- `big_world_save_data_response.msgpack.b64` / `big_world_save_data_response.json`
  - Pcap-verified source: `DlcWorldSaveDataResponse` payload from `20260706_1451.pcap`.
- `big_world_album_update.msgpack.b64`
  - Pcap-verified source: `NotifyBigWorldAlbumUpdate` startup push from the same BigWorld capture.
- `big_world_map_data.msgpack.b64`
  - Pcap-verified source: `NotifyBigWorldMapData` startup push from the same BigWorld capture.
  - Runtime note: `BoxRewardedCntData` supplies the initial per-level box count; session collections add to this per level.
- `big_world_sg_dorm_data.msgpack.b64` / `big_world_sg_dorm_data.json`
  - Pcap-verified source: `NotifySgDormData` startup push from the same BigWorld capture.
- `big_world_load_complete_xrpc_pushes.json`
  - Pcap-verified source: the XRpcCommon pushes observed after `LoadCompleteRequest` in the same BigWorld session.

## Interaction packet facts from the capture

The known-good scene-object collect sequence came from `20260706_1451.pcap`, request `XRpcCommon / RpcPlayerInteractRequest` with target uuid `95`, place id `100016`, target type `2`, level id `4001`, option id `1`.

Pcap-verified server sidecars for that collect:

1. `NotifyBigWorldBoxData`: MessagePack map `{ LevelId: 4001, BoxRewardedCnt: 4 }`.
2. `NotifyTask`: task progress updates for `990007` through `990010` with value `4`.
3. `NotifyBigWorldCourseExploreProgress`: MessagePack map `{ VersionId: 1, ExploreId: 1, PoiId: 101, Count: 4 }`.
4. `XRpcComponentAction / RpcSceneObjectCollectNotify`: envelope shape `[rpcName, argsBytes, 0, 15, levelId, targetUuid]`; args shape is one array containing reward-goods maps, not positional ints.
5. `XRpcCommon / RpcNpcInteractStartNotify` and `RpcNpcInteractFinishNotify`: exact payload bytes are asserted in `AscNet.Test` for the `95 / 100016` retail path.
6. `NotifyItemDataList`: item inventory sync before `XRpcCommonResponse`.

## Inference boundaries

- Reward lookup is table-backed at runtime through `TableReaderV2`, which resolves `table/share/reward/Reward.tsv` and `table/share/reward/RewardGoods.tsv` relative to the server working directory. The repository copy is under `Resources/table/share/reward/`.
- Applying the captured `NotifyBigWorldCourseExploreProgress` constants (`VersionId = 1`, `ExploreId = 1`, `PoiId = 101`) to every scene-object collect is an implementation inference. The pcap verifies those values for the captured `4001 / 100016` collect only.
- `EnterInstLevelRequest` / `EnterInstLevelResponse` and `NotifyNewEnteredBigWorldLevelId` are inferred from client Lua (`client_lua_dump/XBigWorldGamePlayAgency.lua` and `client_lua_dump/XBigWorldMapAgency.lua`). No available pcap currently contains an Orbital Corridor enter-level request/response pair.
- No `*_5002*` BigWorld fixture exists in `Resources/Configs`. Do not add guessed level fixtures; capture and decode the retail packet first.
- External client data at `https://github.com/myssal/PGR_Data/tree/master/en/bytes/client/bigworld` is useful for level names and map metadata, but it did not identify the local interaction place ids `100021` or `100123` during this investigation.
