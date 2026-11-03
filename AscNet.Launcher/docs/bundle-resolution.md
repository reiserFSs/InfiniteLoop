# Matrix bundle resolution for PGR 4.9 (design note, not implemented)

Status: `src/bundle.rs` / `src/fps.rs` are unchanged. The research implementation and the 4.8 proof live in
`Scripts/pgr_bundles.py` (see `Docs/development.md`).

## What depends on the index today

- `bundle::resolve(game)` looks up `LOGICAL_ASSET = "assets/temp/lua/matrix.ab"` in
  `PGR_Data/StreamingAssets/{document,resource}/matrix/index` (document first), takes `[file, sha1, size]` -> `file`, and
  returns `Active { path, scope }`.
- `fps.rs` (`inspect`, `remove_legacy_hook`) and `mods.rs` (`load_matrix_payload`) only consume `resolve()`. fps.rs
  additionally refuses to touch `scope != "document"`. So the whole launcher needs exactly one new capability: find the
  matrix Lua bundle when the index no longer maps the name.
- 4.9 reportedly drops the name -> file mapping ("InPkgIndex") for everything except cutscenes/audio. The bundle files stay
  content-addressed `<sha1>.uab`, and the UnityFS payload (same protected LZ4 + AES-CTR seed, same key) still carries the
  bundle's own `AssetBundle` object. [INFERENCE for 4.9: the repack keeps this format; only the index layout is known to
  change. Check on the first 4.9 client that `decode()` still accepts a bundle.]

## Two trees, and the live oracle

`StreamingAssets/{document,resource}/` has two bundle namespaces, each with its own `index`: `matrix/` (everything,
including `assets/temp/lua/matrix.ab`) and `launch/` (`assets/temp/lua/launch.ab`, `launchmanifest`, `assets/launch/*`;
20 index entries, 20 files). The scan covers both (disjoint names, same derivation); `launchmanifest` and `matrixmanifest`
are the only names without `.ab` and are recognised by their single container asset.

Ground truth: the client's `XResourceManager.UrlTable` (private static `Dictionary<string,string>`, 460,827 entries on 4.8,
original-case asset path -> logical bundle; `GetBundleUrl` is case-sensitive). Dumped via the Proxima inspector
(`pgr_bundles.py verify-live --dump-path ...`), it agrees with the scan: 460,827 matches, 0 differences, 0 client-only.
680 scan-only assets (131 bundles) are present on disk and in the index but not in `matrixmanifest`, so the client never
lists them. On 4.9, validate the scan against a fresh UrlTable dump before trusting the container rule.

## Fallback: scan by container

Order: real index (unchanged) -> scan. Use the scan whenever the index is missing, undecodable, lacks the key, or the
named file is absent.

For each `*.uab` (document dir first, then resource):
1. Decode only what is needed: header + protected directory (existing `header`/`directory`), then LZ4 blocks one at a time
   (existing per-block `transform_controls` + `decompress`) until the serialized-file metadata and the `AssetBundle`
   (class id 142) object are covered. This needs a `decode_prefix(data, upto)` variant of `decode()`, which currently
   decompresses every block. Measured on 3,150 4.8 bundles (all 1,650 document + 1,500 random resource): 58% need 1 block
   (128 KiB), 81% need <= 2; the tail is long (the object grows with the container size) up to a few hundred blocks.
   `matrix.ab` itself needs ~10+ blocks (9,923 container paths).
2. Parse the serialized header (v22: metadata at +0x14, `data_offset` at +0x20), the object table, then the AssetBundle
   object: `m_Name` (string), `m_PreloadTable` (PPtr array, 12 bytes each), `m_Dependencies` (string array), `m_Container`
   (array of `(string path, {preloadIndex, preloadSize, PPtr})`).
3. Match: `m_Name == "assets/temp/lua/matrix.ab"`, else the container-derived rule below.

Derivation rule (proven on 4.8, 42,477/42,477 UnityFS index entries): a bundle's logical name is `<asset>.ab` for a
single-asset bundle, or `<dir>.ab` where `dir` is the common directory of all container paths (or one of its ancestors).
For `matrix.ab`: 9,923 container paths, all `assets/temp/lua/matrix/...lua.bytes`, common dir `assets/temp/lua/matrix`.
Pick the file whose container paths all lie under `assets/temp/lua/matrix/`; the `assets/temp/lua/dlcfight/...` bundle
and the `launch` bundle cannot match. Ties (a stale copy beside the updated one): document scope beats resource, then
the newest mtime.

Always keep the existing safety checks: canonical path stays inside the scope's `matrix` dir, regular file, size cap,
and fps.rs's document-only write rule. Do not cache a path across launches without re-validating size + mtime.

## Cost

Measured on the installed 4.8 client with the Python port (14 cores): 43,992 UnityFS bundles (34.6 GB on disk, read only
partially; 4,630 audio/video files skipped by extension) in 80-120 s cold, 0.3 s for an incremental rescan (unchanged
file name + size + mtime), JSON cache 56 MB (a launcher needs only the `matrix.ab` hit, not the cache). Roughly 25 ms CPU
per bundle in Python, dominated by the per-token LZ4 control-byte transform.

Launcher budget [INFERENCE, not measured in Rust]: a native implementation should be around an order of magnitude
cheaper per bundle. To avoid paying even that: scan the document dir first (1,650 bundles on 4.8; `matrix.ab` lives there
once patched or hotfixed), remember `{file name, size, mtime}` of the last hit in launcher state and try it first, and
only fall back to a full resource scan on a miss. Scan in parallel (rayon) and stop at the first valid hit.

## Open items for the first 4.9 client

1. Does the `index` file still decode with the current msgpack search, and which keys remain (cutscenes, audio)?
2. Is `m_Name` still the logical name? If it is hashed, only the container rule applies (the rule already handles it).
3. Is `matrixmanifest` still resolvable? It is the client's own asset -> bundle table (460k assets, 42k bundles, groups of
   asset indices per bundle) and agrees 100% with the container paths on 4.8, so it can arbitrate ambiguous cases.
