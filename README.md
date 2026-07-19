# InfiniteLoop

InfiniteLoop is a working branch of [AscNet](https://github.com/rafi1212122/AscNet), a private-server emulator for **Punishing: Gray Raven**. This branch focuses on bringing AscNet forward to the current global PC/Steam client path and documenting the compatibility work needed for the 4.5-era client.

This is research/dev infrastructure, not an official service. It expects a local client, a local AscNet process, and a local MongoDB database.

## Current target

The current compatibility target in this tree is:

| Area | Value |
| --- | --- |
| Client package | `com.kurogame.pc.punishing.grayraven.en` |
| Application version | `4.5.0` |
| Document version | `4.5.12` |
| Launch module version | `4.5.12` |
| Steam/PC channel | `205` |
| Game server TCP port | `2335` by default |
| SDK/HTTP URL used by the runner | `http://127.0.0.1:8080` by default |

`Resources/Configs/version_config.json` carries the current `4.5.0 -> 4.5.12` version/hash tuple used by the current-client config endpoint.

## What changed in this branch

### Current-client config and SDK routing

- Added current-client config generation for the PC package `com.kurogame.pc.punishing.grayraven.en`.
- Added Steam/PC channel handling with channel `205`.
- Added current-client CDN and server-list payloads for the config endpoints.
- Added Kuro SDK compatibility endpoints under `/sdkcom/v2/...`, including:
  - email/password login
  - Steam third-party login
  - auto login
  - real-name login/check
  - access token
  - OAuth code generation
  - Steam/PC third-login mark/browser helpers
  - player config/system config payloads
- Added gate-login fallback via `ASCNET_GATE_FALLBACK_USERNAME`, so Steam/KRSDK handoff can map an unknown external user id to a local AscNet account.
- Added current notice fixtures and endpoint compatibility checks for the current client.

### Steam/PC bridge

- Added `run_steam.py`, a local runner that starts AscNet, optionally starts MongoDB, starts mitmproxy, performs SDK/config smoke checks, creates or verifies a local AscNet account, and launches the client command with proxy environment variables.
- Added `proxy.py` routing for current PC/Steam HTTP traffic:
  - `sdkapi.kurogame-service.com`
  - `sdkapi.kurogame-service.xyz`
  - `prod-encdn-*.kurogame.net`
  - local wildcard `/api/`, `/prod/`, and `/sdkcom/` requests
- Added redacted proxy flow logging to `.runtime/proxy-flows.log`.
- Added KRSDK cache repair/seeding helpers for local Steam bridge experiments.
- Added `launch-pgr-ascnet.sh` as a macOS/CrossOver launch example. Its paths are workstation-specific; adjust them before reuse.

### Game protocol and data compatibility

This branch adds or fixes current-client server behavior for:

- `NotifyLogin` shape and current-client login data.
- Current-client notice payloads.
- Stage bookmark compatibility.
- Board mutual push compatibility.
- Mainline 2 exhibition chapter compatibility.
- Mainline treasure rewards using current `Treasure.tsv` and `Chapter.tsv` contracts.
- Story course rewards.
- Boss single login payload shape.
- Guide table compatibility for current guide TSVs.
- Player cost-time upload.
- Player point upload.
- PR2 quality compatibility.
- Character progression persistence.
- Character/frame experience rollover and commandant-level caps.
- Fight settlement commandant EXP and character/card EXP.
- Current-client first-clear fields such as `FirstTeamExp` and `FirstCardExp`.

### Gender setup fix

The current client needs gender selection to update both persisted player state and the live in-session player cache.

This branch adds:

- `PlayerData.Gender`
- `PlayerData.ChangeGenderTime`
- `ChangePlayerGenderRequest`
- `ChangePlayerGenderResponse`
- `NotifyPlayerGender`
- `ChangePlayerGenderRequestHandler`

Behavior:

- accepts current-client gender values `1..3`
- rejects invalid values with `20002020` / `PlayerGenderCfgNotExist`
- returns `20002021` / `PlayerGenderIsSame` only after setup has completed
- treats `Gender <= 0` or `ChangeGenderTime <= 0` as incomplete first setup
- grants first-setup `Inventory.FreeGem x50`
- sends `NotifyItemDataList` for the 50 Black Card reward
- includes `RewardGoodsList` in the response
- sends `NotifyPlayerGender` before the success response so the profile can refresh without a client restart
- persists inventory and player state

This also covers the earlier broken state where `Gender` may have been written but no reward or `ChangeGenderTime` was recorded; that account can still receive the first-setup reward once.

## Repository layout

| Path | Purpose |
| --- | --- |
| `AscNet/` | Main host process. Starts the TCP game server and ASP.NET SDK server. |
| `AscNet.GameServer/` | TCP game protocol, request handlers, commands, combat/settlement logic. |
| `AscNet.SDKServer/` | HTTP SDK/config/login/KRSDK endpoints. |
| `AscNet.Common/` | Shared database models, MessagePack schemas, config, utility code. |
| `AscNet.Table/` | Table generator/parser support. |
| `AscNet.Test/` | Focused compatibility harness and regression checks. |
| `Resources/` | Runtime configs, current client tables, data fixtures, notices. |
| `proxy.py` | mitmproxy script that routes client HTTP traffic back to local AscNet. |
| `run_steam.py` | Steam/PC bridge runner for AscNet + proxy + optional MongoDB + launch command. |
| `launch-pgr-ascnet.sh` | macOS/CrossOver launch example for the Steam client. |

## Requirements

Minimum local tooling:

- .NET SDK 8
- MongoDB reachable at `127.0.0.1:27017`, or `mongod` available for `run_steam.py --with-mongo`
- Python 3.10 or newer for `run_steam.py`
- mitmproxy/mitmdump for Steam/PC bridge mode
- A local Punishing: Gray Raven PC/Steam installation for client testing

Optional/macOS-specific:

- CrossOver or another Wine launcher if you use `launch-pgr-ascnet.sh`

## Running AscNet directly

Start MongoDB first, then run:

```bash
dotnet run --project AscNet/AscNet.csproj -- --urls http://127.0.0.1:8080
```

Default config values come from `AscNet.Common/Config.cs`:

| Setting | Default |
| --- | --- |
| Game server host | `127.0.0.1` |
| Game server port | `2335` |
| MongoDB host | `127.0.0.1` |
| MongoDB port | `27017` |
| MongoDB database | `asc_net` |

`Resources/Configs/config.json` may be left empty to use those defaults, or populated with overrides.

## Running with the Steam/PC bridge

Basic local bridge with MongoDB managed by the runner:

```bash
python3 run_steam.py --with-mongo
```

Run AscNet, proxy traffic, and launch the client command:

```bash
python3 run_steam.py --with-mongo --launch-cmd ./launch-pgr-ascnet.sh
```

Useful options:

```bash
python3 run_steam.py --help
```

Common options:

| Option | Purpose |
| --- | --- |
| `--sdk-url http://127.0.0.1:8080` | Local SDK/config URL exposed by AscNet. |
| `--proxy-host 127.0.0.1` | mitmproxy bind host. |
| `--proxy-port 8081` | mitmproxy bind port. |
| `--with-mongo` | Start local MongoDB if it is not already reachable. |
| `--ascnet-username test` | Local AscNet account used for Steam login handoff. |
| `--ascnet-password test` | Password used when creating that local account. |
| `--gate-fallback-username <name>` | Map unknown Steam/KRSDK gate logins to an existing local account. |
| `--no-ensure-account` | Skip local account creation/checking and disable implicit unknown-user fallback. |
| `--seed-krsdk-cache` | Opt in to writing local AscNet account data into KRSDK cache files. |
| `--krsdk-cache-dir <path>` | Override the KRSDK login-cache directory used for repair/seeding. |
| `--no-proxy` | Run only AscNet; skip mitmproxy. |
| `--no-smoke` | Skip config smoke checks before launching. |
| `--proxy-log <path>` | Write redacted request/response diagnostics. |
| `--launch-cmd ...` | Command to start after AscNet/proxy are ready. |

On native Windows, pass the client's actual `%APPDATA%\KR_G143\A1855` directory with `--krsdk-cache-dir` when using KRSDK cache repair or `--seed-krsdk-cache`; the default path targets the macOS/CrossOver launch example.

The runner sets:

- `ASCNET_PUBLIC_HTTP_ORIGIN`
- `ASCNET_GATE_FALLBACK_USERNAME`
- proxy variables for the launch command
- `ASCNET_PROXY_TARGET`
- `ASCNET_PROXY_LOG`

## Local account flow

The runner can create or verify a local account before the client launches:

```bash
python3 run_steam.py --with-mongo --ascnet-username test --ascnet-password test
```

The underlying account endpoints are:

- `POST /api/AscNet/register`
- `POST /api/AscNet/login`
- `POST /api/AscNet/verify`
- `GET /api/Login/Login`

Steam/KRSDK login callbacks can be mapped to the local account through `ASCNET_GATE_FALLBACK_USERNAME`.

## Testing

Run the focused compatibility harness:

```bash
dotnet run --project AscNet.Test/AscNet.Test.csproj
```

Run one focused check:

```bash
dotnet run --project AscNet.Test/AscNet.Test.csproj -- --player-gender-compat-only
```

Available focused switches:

```text
--notify-login-compat-only
--stage-bookmark-compat-only
--mainline2-exhibition-compat-only
--mainline-treasure-reward-compat-only
--boss-single-login-compat-only
--guide-table-compat-only
--player-cost-time-upload-compat-only
--record-player-point-compat-only
--player-gender-compat-only
--board-mutual-push-compat-only
--character-progression-persistence-compat-only
--exp-level-compat-only
--story-course-reward-compat-only
--pr2-quality-compat-only
--current-client-notice-endpoints-only
```

Build the main projects:

```bash
dotnet build AscNet.Common/AscNet.Common.csproj
dotnet build AscNet.GameServer/AscNet.GameServer.csproj
dotnet build AscNet.SDKServer/AscNet.SDKServer.csproj
dotnet build AscNet.Test/AscNet.Test.csproj
dotnet build AscNet/AscNet.csproj
```

## Runtime data

Local runtime state should stay out of commits:

- `.runtime/`
- `.runtime/mongo`
- `.runtime/proxy-flows.log`
- build outputs under `bin/` and `obj/`

Do not commit runtime logs, MongoDB files, client binaries, or captured credentials.

## Current caveats

- This remains a compatibility/research server, not a complete production backend.
- Some modules are still skeletal or best-effort.
- Steam support is local-bridge based: it relies on local SDK/config responses plus mitmproxy routing.
- HTTPS proxying for pinned KRSDK hosts may break; the runner keeps HTTPS proxying opt-in through `--proxy-https`.
- `launch-pgr-ascnet.sh` is an example for one macOS/CrossOver setup and should be edited for other machines.

## Upstream

Original AscNet project:

```text
https://github.com/rafi1212122/AscNet
```

This branch is intended to document and preserve the current-client compatibility work separately from upstream.