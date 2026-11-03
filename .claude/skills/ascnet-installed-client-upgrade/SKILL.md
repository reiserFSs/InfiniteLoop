---
name: ascnet-installed-client-upgrade
description: Use when upgrading AscNet tables and runtime support to a newer installed PGR client; verifies bundle provenance and runs isolated verification without touching your main MongoDB.
---

# Authority and imports
- Server support is not complete gameplay parity. Compare each runtime TSV against its recorded baseline in `Resources/table/manifest.json`, not blindly against a newer corpus; keep explicit local-policy projections. An absent historical filename doesn't prove a feature is new.
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
