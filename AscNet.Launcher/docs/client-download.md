# Client download

Status: implemented (engine `src/download.rs`, UI `src/ui/client.rs`). The sections below the player guide are the research notes the engine was built from.

## Using it (player guide)
No game selected: press **SELECT GAME** on the home screen (or **Settings → Game client…** at any time). The **Get game** view has:

- **Language**: English (Global), 繁體中文 (Taiwan), 한국어 (Korea), 日本語 (Japan), 简体中文 (China, mainland). Each is a separate Kuro client of the pinned version
  (`supported-client.json` → `applicationVersion`); the game downloads its remaining resources itself on first start. The choice is
  saved in `settings.json` (`region`); with nothing saved it follows the Windows UI language (ko → Korean, ja → Japanese,
  zh-CN/SG → China, other zh (TW/HK/Macau) → Traditional Chinese, everything else → English).
- **Install folder** (Choose…), then the view shows what it found and what the job costs (download size, files, space needed
  and available):
  - *Empty folder* → **DOWNLOAD** (a full install; an interrupted download in the same folder resumes).
  - *Folder with PGR.exe* → the detected edition, version, Steam copy and AscNet-patch state, and **VERIFY & REPAIR**
    (re-check every file, re-download what differs), **UPDATE** (older official version → pinned version via Kuro's patches;
    enabled only when the version is older) and **ADOPT** (bring an official-launcher or Steam install under the launcher by
    verifying it in place; enabled when the folder is not yet the selected game). The recommended action is highlighted.
    A detected edition locks the language picker so a Taiwan client is never "repaired" into English.
  - Any other non-empty folder is refused.
- **PAUSE / CANCEL** while a job runs; **RESUME** continues from the files already verified (also offered after an error or a
  closed launcher). The launcher cannot be closed while a job runs: pause first.
- When a job finishes the folder becomes the selected game and the normal flow continues: press **SETUP** (supported-client
  check, local build, patch install), then **PLAY**. **Settings → Re-run setup** runs that setup again after the home
  button has become **PLAY**.

Verify/repair and update never touch files outside the chosen folder, and refuse to run while PGR.exe is running.

## Engine (`src/download.rs`)
- **Pinned sources**: `supported-client.json` `"downloads"` holds, per region, the 4.8.0 CDN list (priority order), `baseUrl`,
  `indexFile` + `indexFileMd5`, `size`, every `patchConfig` entry (version, baseUrl, indexFile, MD5, size), the
  `resourcesExcludePath`, and the live `discovery` index URLs. File lists are authenticated against the pinned MD5; the live index
  is only fetched (once) to add CDN *hosts* when every pinned host failed. TW index token (from the TW `pclauncher.exe`
  `KRApp.conf`): `G279/50016_i2n5NLmdCAmOGP3J1tJOlWKNSMQuyWL7`. `applicationVersion` must equal each region's pinned version.
- **Full list lists everything**: 46.8k of the 47.2k files are under `PGR_Data/StreamingAssets/resource` (71.5 GB of 72 GB; only 347
  files / 607 MB are the executable client). Install downloads all of it. Repair/Adopt (and the verify pass after an Update) skip
  that prefix because the game updates it itself and drifts from the 4.8.0 index; an Update applies the patch there too.
- **Patches** (all 5 regions, 14/14/15/13/20 versions, every list MD5-checked): only whole-file formats exist. Keys are always
  `resource` (`dest, md5, size, fromFolder` | `chunkInfos` | krzip), `deleteFiles`, `zipInfos`; no HDiff/KrDiff entry anywhere. Unknown
  fields are rejected, so a future diff format fails with "unsupported file list format". `fromFolder+dest` = whole file (the 4.8.0
  `zip/` tree); `.krzip` entries come from the patch `baseUrl` and are plain deflate zips whose members match `zipInfos` exactly
  (names, sizes, MD5 of the decompressed data; checked live against EN/JP 4.7.0).
- **Job routing** (`plan().job` is the effective job, `note` explains): version older + patch available -> Update; older without a
  patch, equal, or unreadable -> verify/repair; newer -> refused; other region (`KR_ProjectId` of KRSDK.bin, or of KRSDKConfig.json for CN) -> refused.
  Install needs an empty folder or one holding its own `.ascnet-download/job.json` (resume).
- **Variants kept, not repaired** (reported in `Outcome.skipped_variants` as `path (reason)`): files whose SHA-256 is in the
  `supported-client.json` `originals` allowlist ("accepted retail variant", e.g. Steam's KRSDK.dll), files AscNet patched
  (`install::managed_files` state: "AscNet patch", e.g. KRSDK.dll/PGRBase.dll; for CN PGRBase.dll only, version.dll/lucia.dll/libraries.txt are
  not in the index and so left alone like any extra), and Steam's `KRSDK.bin` ("Steam build"). Also kept
  in patch archives and `deleteFiles`. Extra files (steam_api, user files) are never touched. A pending launcher transaction
  journal blocks Repair/Update.
- **Integrity**: every file MD5-verified (per 100 MiB chunk for chunked files, resume at chunk granularity; plain files resume by
  length and are discarded on final mismatch), written to `.ascnet-download/parts/` then renamed; krzip members extracted with
  per-entry MD5 and only for names listed in `zipInfos`; every path validated (no absolute, `..`, `\`, `:`, links/reparse points);
  deletes happen after downloads, never for files the same patch writes, only regular files, empty parents pruned.
  Requests are 16 MiB ranges, 8 workers, CDN failover in priority order (hosts failing 3x in a row are tried last), 3 rounds with
  backoff, 4 attempts per segment. Cancel returns `Err` whose root cause is `download::Cancelled`.
- **MD5** is implemented in-tree (no md5 crate offline); `flate2` (already in the lock file) decodes the gzip-encoded `index.json`.
- Tests: local fixture HTTP server (resume after cancel, corrupt chunk/file, failover incl. dead/503/dropped hosts, host discovery,
  tampered list, traversal, Update with whole files + krzip + deletes + launcher-patched file, Steam-like adopt, region refusal);
  `cargo test --offline --lib -- --ignored live` re-checks the real CDN (all lists/patch lists, small files, ranges on every host).

## Official source (global PC, no auth)
- Game index: `https://prod-alicdn-gamestarter.kurogame.com/launcher/game/G143/50015_LWdk9D2Ep9mpJmqBZZkcPBU2YNraEWBQ/index.json`
  (G143 = PGR global, 50015 = global appId; token from public TwintailTeam/game-manifests). Live version 4.8.0.
- TW (Traditional Chinese) PC client: `G279/50016`, same layout; 4.8.0 base
  `launcher/game/G279/50016/4.8.0/RfnyCruypnrxBymaqVVowmvKlxcjZpzc/` (47,116 files, 71.5 GB).
  Package `com.kurogame.punishing.grayraven.tw`; `PGR.exe` and `GameAssembly.dll` differ from EN.
- KR (Korean) PC client: `G286/50011` (launcher index `…/launcher/game/G286/50011_XefwDdpgPxxLABoTOD0yuqTFBC3koJZ0/index.json`),
  4.8.0 base `launcher/game/G286/50011/4.8.0/kHdNSrXondzSdDKXBidpKnhywQlKnflO/` (47,121 files, 72.1 GB;
  file list `resource/50011/4.8.0/indexFile.json`, MD5 `052e1bbd…` = `indexFileMd5`). Package
  `com.kurogame.punishing.grayraven.kr` (resources.assets XBuildConfig; CDN key `jqlCmYRizwT76uvX`; XUrlPerfixConfig
  prod: `krcdn-ak`/`krcdn-aliyun`); `KRSDK.bin`: `KR_ProjectId=G286`, `KR_ProductId=A1794`, `KR_ChannelID=240`,
  `KR_PackageName=com.herogame.pc.punishing.grayraven.kr`. `PGR.exe` and `GameAssembly.dll` differ from EN/TW;
  `UnityPlayer.dll`, `PGRBase.dll`, `PGR_Data/Plugins/KRSDK.dll` are byte-identical to the EN entries.
  Statically checked: lucia resolves via `il2cpp_*` exports (no fixed RVAs/signatures) and all 26 are exported by the KR
  `GameAssembly.dll`. Native hook execution on KR is unverified (game not run).
- JP (Japanese) PC client: `G282/50007` (index `…/launcher/game/G282/50007_NxWGZ0d254oWqZKuuL6szOK7WRLPt668/index.json`),
  4.8.0 base `launcher/game/G282/50007/4.8.0/IuQPbGqaRgXLgaWBKpYwFBuOJqgeiYql/` (file list
  `resource/50007/4.8.0/indexFile.json`). Package `com.kurogame.punishing.grayraven.jp` (XBuildConfig; CDN key
  `xZx901LhZhT6G2HG`; prod URLs `jpcdn-ak`/`jpcdn-aliyun`); `KRSDK.bin`: `KR_ProjectId=G282`, `KR_ProductId=A1778`,
  `KR_ChannelID=240`, `KR_PackageName=com.herogame.pc.punishing.grayraven.jp`. `PGR.exe` and `GameAssembly.dll` differ
  from EN/TW/KR; `UnityPlayer.dll`, `PGRBase.dll`, `KRSDK.dll` are byte-identical to the EN entries. Same static
  `il2cpp_*` export check passed (26/26). Native hook execution on JP is unverified (game not run).
- CDN bases (from index `cdnList`): `zspms-volcdn-gamestarter.kurogame.net`, `zspms-txcdn-gamestarter.kurogame.net`,
  `zspms-akcdn-gamestarter.pgr-game.com`, `zspms-alicdn-gamestarter.kurogame.net`.
- CN (mainland, 战双帕弥什) PC client: `G148/10012`. Discovery (gzip JSON):
  `https://prod-cn-alicdn-gamestarter.kurogame.com/launcher/game/G148/10012_RnIUKs3r59Csliu3N0rl5uRWWBOFDaJL/index.json` with backup
  host `https://prod-volcdn-gamestarter.kurogame.xyz/…` (same path; both returned identical `config`, checked live). CDNs
  (index priority order): `zspms-alicdn-gamestarter.kurogame.com`, `zspms-txcdn-…`, `zspms-volcdn-…`. 4.8.0 base
  `launcher/game/G148/10012/4.8.0/NIqyHuAmTLmulqsTBArguDiUNkahynGD/` (47,052 files, 71.3 GB; file list
  `resource/10012/4.8.0/indexFile.json`, MD5 `1b2e8447…`, fetched live and matching). 20 patch versions (3.2.0 … 4.7.0).
  CN has **no `KRSDK.bin` and no `KRSDK.dll`**: it ships the official `KRSDKEx.dll` + `libkrsdkcurl.dll` and a plain-JSON
  `PGR_Data/Plugins/KRSDKRes/KRSDKConfig.json`. Detection falls back to that file (`KR_ProjectId=G148`, `KR_GameVersion`;
  BOM/CRLF tolerated, `KR_PackageName=com.kurogame.haru.hero`). The SDK files are ordinary index files hash-validated against the
  `originals` allowlist; the launcher patch only adds version.dll/lucia.dll/libraries.txt and patches PGRBase.dll.
- File list: `{cdn}/{config.indexFile}` → `{resource:[{dest, md5, size, chunkInfos?}]}`; 47,190 files, 71.7 GB.
  Files >100 MiB carry per-100 MiB chunk MD5s. File URL: `{cdn}/{config.baseUrl}{dest}`.
- `config.indexFileMd5` authenticates the file list. `resourcesExcludePath` = `PGR_Data/StreamingAssets/resource`.
- `patchConfig` offers KrDiff patches from 14 older versions (4.7.0 → 4.8.0 ≈ 11 GB); KrDiff = HDiff + zstd
  (MIT applier: `hdiffpatch-rs`, used by Vedaru/kuro).
- Older full builds (3.8.0, 4.1.0–4.7.0) were still downloadable per file via versioned paths recorded in
  TwintailTeam history; retention is undocumented.

## Verified against the supported 4.8.0 client
- MD5 match: `PGR.exe`, stock `GameAssembly.dll`, `UnityPlayer.dll`, stock `PGRBase.dll`.
- Official `KRSDK.dll` SHA-256 `59a1d02d…` = second accepted entry in `supported-client.json` (Steam ships `2a1d8f5d…`).

## Intended flow
1. User picks an empty folder (never a Steam install).
2. Pin the version from `supported-client.json` (use versioned URLs, not "latest"); confirm index MD5 first.
3. Resumable download with per-file/per-chunk MD5, disk-space check, repair.
4. Existing SETUP: supported-client check → local source setup → patch install → PLAY.
5. On first start the game downloads `StreamingAssets/resource` itself from Kuro's `prod-encdn-*` CDN
   (AscNet's config points there; lucia.dll only redirects `client/config`, notices and notice HTML).

## Risks
- Undocumented Kuro CDN layout; token/paths can change.
- Pinned versions (client 4.8.0, document/resources 4.8.10) disappear when Kuro stops hosting them.
- ~72 GB pulled from Kuro servers; Kuro terms of service not reviewed.
