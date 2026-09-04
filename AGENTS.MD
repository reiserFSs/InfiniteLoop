# AscNet Agent Guide

AscNet is a research/private-server emulator for Punishing: Gray Raven. The runtime projects target .NET 8 and expect a local MongoDB instance; the legacy `AscNet.PcapParser` utility targets .NET Framework 4.8.1 and uses `packages.config`. Keep changes narrow and preserve existing behavior unless the task requires otherwise.

## Non-negotiable protocol rule

**Nothing should be hardcoded into the server.** This is normative: server implementations **MUST NOT** hardcode protocol data or behavior. Never embed captured request or response payloads, IDs, balances, standings, timestamps, stage data, NPC waves, or other retail values in server code or runtime resources.

**Maintainer-authorized shop exception:** Curated retail `GetShopInfoResponse` catalogs MAY be stored in `Resources/Configs/client_shops.json` when the corresponding server-owned Shop/ShopGoods source is unavailable. This exception applies only to static shop catalog fields. Player purchase counts, refresh timestamps, reset periods, availability, and all other dynamic state MUST still be derived at runtime from persisted state and authoritative clock/table data; production MUST NOT read captures or decoded dump files.

**Maintainer-authorized Simulated Battlefield exception:** Curated retail `PreFightResponse.FightData.NpcGroupList` compositions and per-clear `FightSettleResponse.Settle.MultiRewardGoodsList` goods for Simulated Battlefield MAY be stored in `Resources/Configs/simulated_battlefield.json` when no server-owned encounter or drop source exists. This exception applies only to ordered static wave fields `NpcId` and `BufferIds`, plus static per-clear settlement fields `RewardType`, `TemplateId`, and `Count`. NPC levels, attributes, magic data, challenge counts, progression effects, timestamps, reward multiplicity, and all other dynamic state MUST still be derived at runtime from player/request state and authoritative table data; production MUST NOT read captures or decoded dump files.

For all game-protocol work:

- Treat packet captures and decoded traffic as behavioral test oracles only, except for the narrow curated shop-catalog and Simulated Battlefield encounter exceptions above. Use them to learn field semantics, ordering, invariants, state transitions, and validation rules; never use other captured values as runtime response sources.
- Derive runtime responses from authoritative game tables, persisted player/session state, deterministic algorithms, validated client-selected inputs, or the narrowly authorized curated data above, as appropriate to the field.
- Moving captured constants into JSON, configuration, fixtures, generated source, or another resource is still hardcoding unless covered by an explicit curated exception above. A value is otherwise data-driven only when it comes from an authoritative source and is selected or computed for the current request/state.
- If required authoritative source data is absent, report the missing source explicitly. Do not ship captured snapshots, speculative substitutes, stage-specific special cases, silent defaults, or fallback payloads outside the curated exceptions above.
- Before delivery, search every changed runtime file for capture-specific constants outside the curated exceptions above. Verify dynamic outputs with at least two distinct inputs or states.
- Tests may preserve captured observations as expected behavior. Production code and runtime configuration must not depend on captured observations except under the explicit curated exceptions above.

## Repository map

- `AscNet/`: host process; starts the TCP game server and ASP.NET SDK server.
- `AscNet.GameServer/`: TCP protocol, request handlers, commands, combat, and settlement logic.
- `AscNet.SDKServer/`: HTTP SDK, config, login, and KRSDK endpoints.
- `AscNet.Common/`: shared database models, MessagePack schemas, configuration, and utilities.
- `AscNet.Table/`: source generator for typed rows from authoritative `Resources/table/**/*.tsv` data.
- `AscNet.Test/`: focused compatibility and regression harness.
- `AscNet.Logging/`: shared logging support.
- `AscNet.PcapParser/`: legacy .NET Framework 4.8.1 packet-capture parsing utility.
- `Resources/`: runtime configuration, authoritative client table TSVs, data fixtures, and notices.
- `run_steam.py` and `proxy.py`: local Steam/PC bridge and mitmproxy routing.

## Tables and protocol mechanics

- Treat `Resources/table/**/*.tsv` as authoritative table input. `AscNet.Table` generates typed row classes from those files; consume them at runtime with `TableReaderV2.Parse<T>()`. Never hand-edit source-generator output or other generated table rows—change the authoritative TSV or generator instead.
- Define protocol DTOs using the existing named-key `[MessagePackObject(true)]` pattern in `AscNet.Common/MsgPack`.
- Register request handlers with `[RequestPacketHandler("...Request")]`, deserialize the request DTO from the packet, and send the matching response with the request's `packet.Id`.
- Use `session.SendPush(...)` for server-initiated notifications. After state mutation, call the relevant persisted model's `Save()` method (for example, player, inventory, character, or stage as applicable).

## Build, run, and test

Prerequisites documented by the repository are .NET SDK 8, MongoDB at `127.0.0.1:27017` (or `mongod` for the runner), Python 3.10+, and mitmproxy/mitmdump for bridge mode.

```bash
# Run the server after MongoDB is available
dotnet run --project AscNet/AscNet.csproj -- --urls http://127.0.0.1:8080

# Build the main projects
dotnet build AscNet.Common/AscNet.Common.csproj
dotnet build AscNet.GameServer/AscNet.GameServer.csproj
dotnet build AscNet.SDKServer/AscNet.SDKServer.csproj
dotnet build AscNet.Test/AscNet.Test.csproj
dotnet build AscNet/AscNet.csproj

# Run the compatibility harness
dotnet run --project AscNet.Test/AscNet.Test.csproj

# List bridge options or run with a managed local MongoDB
python3 run_steam.py --help
python3 run_steam.py --with-mongo

# Run Python bridge/proxy tests after Python changes
python3 -m unittest test_run_steam.py test_proxy.py
```

Focused harness switches are defined in `AscNet.Test/Program.cs`; `README.md` contains examples only. Prefer the narrow `--...-only` switch covering the changed behavior, then run the full harness when warranted.

## Change discipline

- Put game TCP behavior in `AscNet.GameServer/`, HTTP-facing behavior in `AscNet.SDKServer/`, and shared schemas/state/database concerns in `AscNet.Common/`; follow the existing module near the behavior being changed.
- Keep nullable-reference handling compatible with the .NET 8 runtime projects, which enable nullable references and implicit usings; do not assume the legacy parser shares their framework or project conventions.
- For protocol changes, test observable request/state-to-response behavior rather than implementation details. Include distinct-input coverage for any derived fields.
- Do not commit packet captures of any kind. Also do not commit `.runtime/`, MongoDB files, proxy flow logs, `bin/`, `obj/`, client binaries, runtime logs, or captured credentials.
- `launch-pgr-ascnet.sh` contains workstation-specific macOS/CrossOver paths; treat it as an example, not a portable default.

## Client dump and native research
An authoritative client source of the 4.7.0 update is available at /PGR-native-research/PGR_DATA/en/. You can use /cn/ if it has the required data if /en/ does not. Additional tooling is available in the root directory /PGR-native-research.

# Agent responses
Keep your responses brief, terse  
