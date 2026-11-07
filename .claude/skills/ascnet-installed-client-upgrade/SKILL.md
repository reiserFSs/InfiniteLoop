---
name: ascnet-installed-client-upgrade
description: Use when a new PGR client version ships (e.g. 4.8 → 4.9) or when upgrading AscNet tables/runtime to a newer installed client; runbook from bundle extraction to launcher release, plus isolated verification.
---

# New game version runbook (order matters)
Proven on 4.8 → 4.9. Each step lists the trap that cost time.
1. **Keep the old client.** Back up the previous full install before updating; Kuro's CDN may drop old builds. Keep its extracted corpus at `PGR_DATA/<region>`; write the new one to a sibling (`PGR_DATA/en-<ver>`), never over it.
2. **Extract.** `Scripts/pgr_bundles.py scan` then `validate --sha1` (must match 100% of index entries), `Scripts/export_installed_lua.py --install … --output …`, `Scripts/scan_client_upgrade.py --install … --output … --extract`. If everything scans as "raw", the bundle container changed (4.9 added a 32-byte `HARUINKUROGAME` prefix): fix `pgr_bundles.py` with magic detection so old versions still decode. Resolve bundles by their own AssetBundle `m_Name`; the index may not carry logical names.
3. **Native patch.** Compare KRSDK export tables and the `kurosdk_*` P/Invoke strings in GameAssembly old vs new; check KRSDK.bin. lucia hooks resolve il2cpp names at runtime and log `[lucia] OK/FAILED` — `global-metadata.dat` is a stub, so a runtime launch is the only full check.
4. **Launcher allowlist** (`AscNet.Launcher/supported-client.json`): add hashes for Steam and every region's CDN build (download the few binaries from each region's index, MD5-verified). A Steam hash that matches no CDN build may just be the Rosetta-patched CDN build — reproduce it with `Scripts/apply_rosetta_patches.py` before calling it a variant. Keep old-version hashes. `downloads.<region>` from the live discovery index; `documentPatch` pins for the patch files.
5. **SDK version data** (`Resources/Configs/version_config.json`): the client skips its whole update pipeline only when DocumentVersion == LaunchModuleVersion == ApplicationVersion == the version in its config.tab URL (`XInfo.Version`, e.g. `4.9.0` while version.json says `4.9.1` — add both keys). Kuro's live rows serve a higher DocumentVersion, which makes the client download from Kuro. IndexSha1/LaunchIndexSha1 must equal the build's `document/{matrix,launch}/index` sha1. Region config keys: read `XBuildConfig.CDNKey` from each region's `PGR_Data/resources.assets` on its launcher CDN. Notices: `Resources/Configs/Notices/<version>/[<package>/]` — no folder means empty notices.
6. **Document patch files.** If the client ran against stale server data it may delete `StreamingAssets/document/{matrix,launch}` (`MajorVersionCheck`); symptoms are old login music/video and a missing InjectFix patch. Restore with `Scripts/restore_document_patch.py` (players: launcher Verify & Repair).
7. **Tables.** Import only the retail delta (`Scripts/import_tables_<ver>.py` pattern: rows retail added/changed, keep local cells and retail-removed rows), record manifest provenance, re-derive derived tables. Gift box 40693 pool and `Docs/items.md` per AGENTS.MD. Schema changes in TSVs need a server rebuild (generated row types).
8. **Availability.** Retail zeroing a TimeId does not mean "permanent": mirror each client call site (`CheckInTimeByTimeId(id)` → closed; `(id, true)` or `not IsNumberValid(id) or …` → open). New TimeIds without a calendar get labelled missing-calendar windows; derive them generically from client tables (`FubenClientConfig.MainPanelTimeId`, `FubenActivityTimeTips`, `FubenActivity`, `ActivityBrief` groups) — the Battle Screen "Limited Event" goes empty every update otherwise. Unbounded windows go to the client as a dated range (`ClientWindow`), or it shows `01.01-01.01` / huge remaining days. Seasonal modes counted from a window start (Passport weeks) need a dated start, not 0.
9. **Draws.** A banner's tab comes from `DrawTabs` (its `TabBg` art shows which tab the new season uses), not from the frame type. Only open reruns retail actually runs. Any "no activity" id the client tests with `if not x` must be sent as nil, not 0 (CanLiver crash).
10. **New modes.** List new client table groups and the requests their Lua issues; open an entry only once its handlers exist (entering an unserved mode can hang the client).
11. **Launcher media.** `Scripts/extract_launcher_background.py` renders the login Spine prefab (`UiLoginSpineAnimPath`) and decodes the UiLogin `XPlayMusic` cue; take the cue from the document-scope `uilogin` bundle (resource scope may still hold the old cue). Not `MovieLogin.usm`.
12. **Release.** Full `--keep-going` suite, then the launcher release procedure; the user confirms the launcher visually before publishing.

# Authority and imports
- Server support is not complete gameplay parity. Compare each runtime TSV against its recorded baseline in `Resources/table/manifest.json`, not blindly against a newer corpus; keep explicit local-policy projections. An absent historical filename doesn't prove a feature is new.
- **Region tables.** The server tables are EN-derived; a TW/KR/JP/CN client resolves ids against its own. Per update, `Scripts/fetch_region_tables.py --region <r> --out <dir>` builds a tables-only stub install from that region's CDN (sha1-verified, ~240 MB), `scan_client_upgrade.py --extract --install <dir>/install --output ../PGR_DATA/<r>-4.9/bytes` decodes it (use a private bundle-scan cache, never the shared `.runtime` one), `Scripts/import_region_tables.py --region <r>` refreshes `Resources/regions/<r>` (Task ids, MailCollectionBox*). A region needs a `Login-<r>` gate (`ClientRegions.GateRegions`) so the TCP login knows it. See `Docs/development.md` "Region tables".
- Verify the effective document/resource indexes and physical bundle SHA/size; distinguish raw assets, typed tables, deltas and unresolved assets.
- Reuse `Scripts/scan_client_upgrade.py` and `Scripts/import_client_tables.py` (and the per-feature `Scripts/import_*_4_8.py` pattern). Extraction needs an explicit mode; `--help` must not mutate staging. An interrupted extraction can truncate metadata: recover from verified indexes and raw files, then audit strictly.
- Use function-local variables in importers. Assert exact source/destination identity, row shape and hashes before writing; row counts alone can't catch writing the wrong table.

# Runtime integration
- One owner controls shared login/fight hooks, test selectors and the manifest.
- Trace original Lua producers, UI init, callbacks and follow-up calls. Existing request names may gain nested fields or activate new reward categories.
- Close reward dependencies across Item, Character/Equip/Partner, portraits, emoji, titles, nameplates, scenes and DrawTicket. Reward rows alone don't prove the target metadata and ownership grant work; an empty reward switch branch is a defect even if the response succeeds.
- Free-ticket eligibility doesn't resolve missing paid-currency metadata; don't invent a currency. Keep earned tickets when banners are unavailable.
- Distinguish a ticket's serial Id from its CfgId; validate ownership, expiry, group, single/ten-pull. Freeze outcome/pity and persist the claim before cross-model writes; test a reward-written/completion-failed retry and BSON reload.
- Missing pools, ranking rules, calendars: report in `Docs/closed.md`; display text doesn't authorize embedded id mappings.
- Update the Gifts/items docs per `AGENTS.MD` when item ids change.

# Verification isolation
Runtime configuration is `Configs/config.json` (under the working directory), not a root `Config.json`.
1. Use a private working directory with its own real `Configs/` directory (copy it; symlink only read-only children). The harness rewrites `Configs/config.json` and may create/delete `Configs/SecondMenuNotice.json`; a directory-level `Configs` symlink is unsafe.
2. Start your own `mongod` on an unused loopback port with a private dbpath. Full-suite DB name must begin `ascnet_manual_full_`.
3. Before any handler runs, assert both `Common.config.Database` endpoint and `Common.db.DatabaseNamespace.DatabaseName`; file existence isn't enough. Login triggers guild migration/index initialization.
4. Run the focused selector, changed modes, login/config/notice checks, then the full diff (`ascnet-differential-full-harness`). Delete incidental version-string assertions instead of re-pinning them; keep observable persistence, schedule, rejection and retry contracts.
5. Fault-injection reloads use `RecordingMongoCollectionProxy.LastReplacement` (attempted write) vs `LastSuccessfulReplacementBson` (committed). Keep failure-before-write distinct from lost acknowledgement.
6. Label injected-date successes separately from live-calendar rejection; restore in-memory calendar overrides in `finally`.
7. To replay original Lua against loopback packets see `pgr-headless-lua-wire-replay`.
8. Afterwards stop only your mongod and delete probe sources, packet transcripts and private data. Never commit databases or captures.

# If isolation was wrong
Stop, fix the config, audit the executed call paths. Without a baseline don't claim the database was untouched; report the uncertainty.
