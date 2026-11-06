# Matrix bundle resolution for PGR 4.9 (implemented in launcher 1.0.20)

Status: `src/bundle.rs` / `src/fps.rs` implement the "Required launcher changes" below (prefix strip/rewrite with the
`file_size` check, unprotected index bundles through `decode_index`, `m_Name` scan with document-first order, snapshot
filtering of obsolete resource files, sha1/size tie-break, `fps.rs` staging a resource-only matrix bundle into
`document/matrix`). 4.8 installs still resolve through the logical index. Tests: synthetic prefixed/plain/protected
bundles in `bundle.rs`, plus ignored read-only runs against real installs (`real_49_*`, `real_48_*`).
The research implementation, proven on a
4.8.0 install and the 4.9.1 Steam install, is `Scripts/pgr_bundles.py` (see `Docs/development.md`). Everything below
marked "observed" was decoded from `PGR_Data/StreamingAssets` of the EN Steam 4.9.1 client (package 4.9.1,
`document/ver` LeastVersionFile 1739029).

## What 4.9 changed (observed)

1. **32-byte file prefix.** Every `*.uab` and the extensionless manifests start with `"HARUINKUROGAME"` + 18 zero bytes
   (32 bytes in total), then the normal `UnityFS` header (`5.x.x`, Unity `2022.3.52f1c1`). The UnityFS header's
   `file_size` field **excludes** the prefix (file 21,759 bytes -> `file_size` 21,727) and every header/directory/block
   offset is relative to the `UnityFS` signature, so a decoder just strips the 32 bytes first. Detection is by magic
   (`HARUINKUROGAME`), so un-prefixed 4.8 files still decode. The three index files below are **not** prefixed.
2. **Protection unchanged.** Bundle flags are `0x243` (LZ4HC, block+directory info together, 16-byte alignment) or
   `0x1243` (the same with the `0x1000` PGR-protected bit: same AES-CTR seed block, same key `XxecodrPeGaka2e6`, same
   per-token LZ4 control transform). In 4.9 `matrix.ab`, `dlcfight.ab`, `launch.ab` are `0x1243`; sampled resource files
   are 2% protected, document files 6%. **The index bundles changed**: 4.8 `index` was `0x1243` (protected), the 4.9
   `index` / `InPkgIndex` are plain `0x243` and unprefixed.
3. **Physical names are 16-hex ids, not sha1.** `<16hex>.uab`, `.acb`, `.awb`, `.usm`, `.acf`, `.bytes`. The id is stable per
   logical bundle across patches: 1,733 file names appear in both resource and document with different content (the
   document copy is the patched one). Hash function of the id is unknown (not md5/sha1/sha256/blake2b/fnv of the
   logical name; xxhash not tried).
4. **`resource/matrix/index` is gone**, replaced by `resource/matrix/InPkgIndex` (84,873 entries, the shipped package);
   `document/matrix/index` (85,421 entries) is a **full snapshot of the effective set**, not a delta (its manifest lists
   exactly its files; the 121 resource-only files are obsolete). `resource|document/launch/index` still exist (19 entries).
5. **The index no longer maps logical name -> file.** Entries are keyed by physical file; the logical name is only present
   for non-bundle files (4,962 `acb/awb/usm/acf/.patch.bytes`, e.g. `External/Criware/amb_building_in.acb`). All 80,459
   bundle entries have an empty name. Logical bundle names exist only inside each bundle (`AssetBundle.m_Name`) and
   `matrixmanifest` maps asset -> physical file.
6. **Bundle names are no longer derivable from container paths.** `m_Name` is still the logical name
   (`assets/product/.../x.prefab.ab`, `launch_assets/launch/config/launchconfig.asset.ab` for launch,
   `xtreecalcnew_<kind>_<n>.ab` for packed dependency bundles), but only 41,986 of 82,303 bundles satisfy the 4.8 rule
   `<asset>.ab` / `<common dir>.ab`; the rest pack unrelated assets. **Use `m_Name`; do not use the container rule for
   4.9.** (`assets/temp/lua/matrix.ab`, `dlcfight.ab`, `launch.ab` and the `assets/temp/bytes/*` table bundles still have
   exactly those `m_Name`s.)
7. New: `document/origin_index`, `document/ver`, `document/client2cdn`, `download/info/matrix/<from>_<to>.json`.

## Index formats (observed, decoded in `read_file_table` / `_manifest`)

All three are Unity bundles holding one `TextAsset` whose script is MessagePack; the decoder scans the payload for the
top-level array.

`index` / `InPkgIndex` (`assets/buildtemp/index.bytes`, `inpkgindex.bytes`; launch `assets/temp/index.bytes`) -
`fixarray(6)`:

| # | content |
|---|---------|
| 0 | `map<physical file, [name-or-"", sha1 hex(40), size, svn version, [base versions], 0]>`; sha1/size are over the whole file **including** the 32-byte prefix (verified for all 85,421 + 19 entries on disk) |
| 1 | document matrix index only: `map<int group id, [files]>` (416 groups, 107,298 refs; download groups) |
| 2 | nil |
| 3, 4 | document matrix index only: two sorted file lists (900 and 48,616 files; purpose unproven) |
| 5 | document matrix index only: `[7000, 7001, 7002, 7003, 7512]` |

`svn version` is the revision that introduced the file (1693800 base, 1723213/1731820/1735136/1736632/1739029 patches);
the list is the base versions the patch applies to.

`matrixmanifest` (`document/matrix/37ec7ceb97560217`, `m_Name == "matrixmanifest"`, 52 MB payload) - `fixarray(6)`:
`[bundle files (85,421), asset paths (474,509, original case), per-uab hash (80,459 uint32; not crc32 of the file),
asset-dependency lists (209,704), asset -> [bundle index] (474,509, exactly one each), bundle -> [dep indexes or nil]]`.
Every file in the effective index appears; each bundle's asset list equals the container of that file for **all 85,421**
files (raw files list their own logical name).

`document/origin_index`: msgpack `["XPKGSET1", "1792678635" (unix 2026-10-22), 84873, [file names]]` where the list is
exactly the key set of `resource/matrix/InPkgIndex` - the manifest of the original package, used by the client to tell
shipped files from downloaded ones. Not needed for resolution.

## What depends on the index today (`bundle.rs`)

- `resolve(game)` looks up `LOGICAL_ASSET = "assets/temp/lua/matrix.ab"` in `{document,resource}/matrix/index` (document
  first; `matrix_name` scans the decoded payload for a 3-array whose first element is a `{name: [file, ...]}` map).
- `decode()` / `directory()` bail with "matrix bundle is not PGR-protected UnityFS" when flag `0x1000` is clear, and
  `header()` requires `file_size == data.len()`.
- `fps.rs` and `mods.rs` only consume `resolve()`/`decode()`/`rewrite()`; fps.rs refuses to touch `scope != "document"`.

## Required launcher changes

1. **Prefix.** In `decode()` (and `header()`): if `data.starts_with(b"HARUINKUROGAME")` and `data.len() >= 32`, remember
   `prefix = 32`, decode `&data[32..]`, and require `file_size == data.len() - 32`. `rewrite()` must emit the same 32
   bytes in front of the re-encoded bundle (header `file_size` again excludes them). Add a `Decoded.prefix: Vec<u8>` and
   write it back first.
2. **Unprotected index.** `decode()` must accept `flags & 0x1000 == 0` for the index (plain LZ4HC directory at the normal
   offset, no seed, no block transform) - or give `decode_plain()` a separate entry used only for index bundles. The
   matrix bundle must still be protected: keep rejecting an unprotected `matrix.ab`.
3. **New index layout.** `matrix_name` cannot work: 4.9 has no logical key for bundles. Replace the lookup by:
   - read `document/matrix/index` (fallback `resource/matrix/InPkgIndex`; `index` first on 4.8), find the 6-array whose
     first element is a map of `[name, sha1, size, ver, bases, 0]` (or keep the 4.8 3-array parse as the first try);
   - **scan by `m_Name`** for `assets/temp/lua/matrix.ab` over the files of that table (document copy first, then
     resource): decode header + directory + only as many LZ4 blocks as needed to cover the serialized metadata and the
     `AssetBundle` object (class 142; `m_Name`, `m_Container`), exactly as `pgr_bundles.read_container`. Match on
     `m_Name`, not on the container-derived name (item 6 above). Documented cost on 4.8: 80-120 s cold for 44k bundles in
     Python; in 4.9 the matrix bundle is in the **document** directory once patched (2,357 files, 2.7% of all), so scan
     `document/matrix` first and fall back to `resource/matrix` [INFERENCE: 2,357 bundles, seconds in Rust];
   - verify the hit: size and sha1 against the index entry of that physical file (`file_size` of the file on disk must
     equal the entry; all 85,421 matched on the 4.9.1 install) and treat a mismatch the way 4.8 treated a modified
     install (the launcher's own patched `matrix.ab` legitimately differs from its index entry);
   - alternative that avoids the scan: `matrixmanifest` (`m_Name == "matrixmanifest"`, found by the same scan restricted
     to the one extensionless file in `document/matrix/` and `resource/matrix/`) gives
     `assets/temp/lua/matrix/<any module>.lua.bytes -> physical file` directly (474k assets, 52 MB; ~2.5 s in Python), so
     one asset lookup yields the file. The scan is still needed to locate the manifest unless its file name
     (`37ec7ceb97560217` on 4.9.1, identical in both scopes) is hard-coded - do not hard-code it.
4. **Scopes.** `resolve()` returns `scope`; with a full-snapshot document index the first scope that contains the
   `m_Name` wins, and a document file whose name also exists in resource is the patched one. Resource-only index entries
   that the document index omits (121 on 4.9.1) are obsolete and must not be picked.
5. **After patching.** `rewrite()` changes the file's bytes, so its sha1/size no longer match the index (also true on
   4.8; the 4.8 modified install in this repo's tests ran with a mismatching document `matrix.ab`). Record that the
   launcher modified the file; do not "repair" the index. `fps.rs` keeps its document-only write rule: when the
   matrix bundle only exists in resource, copy it to `document/matrix/<same physical name>` first (the physical name
   is the id, so the document copy overrides it by name) [INFERENCE: the 4.9 client prefers a document file of the same
   name; not tested in a running client].
6. **Limits.** `MAX_BUNDLE_SIZE` (512 MiB) and the 65,536 block/node limits still hold (largest 4.9 files are far
   below). The scan should stream (`read` only the needed blocks) instead of `read_bounded` for every file.

## Fallback scan details (unchanged from 4.8)

For each candidate (`*.uab` or extensionless, document dir first, then resource):
1. Decode header + protected directory, then LZ4 blocks one at a time (per-block `transform_controls` + `decompress`)
   until the serialized-file metadata and the `AssetBundle` (class id 142) object are covered (needs a `decode_prefix`
   variant of `decode()`). Measured on 4.8: 58% of bundles need 1 block (128 KiB), 81% <= 2; `matrix.ab` needs ~10+.
2. Parse the serialized header (v22: metadata at +0x14, `data_offset` at +0x20), the object table, then the AssetBundle
   object: `m_Name`, `m_PreloadTable`, `m_Dependencies`, `m_Container` (`(string path, {preloadIndex, preloadSize, PPtr})`).
3. 4.9: match `m_Name == "assets/temp/lua/matrix.ab"`. Ties (a stale resource copy beside the updated document copy):
   document beats resource, then the newest mtime. On 4.9.1 no logical name is claimed by more than one effective file
   (0 of 80,459), so ties only occur between stale copies the document index no longer lists.

Always keep the existing safety checks: canonical path stays inside the scope's `matrix` dir, regular file, size cap, and
fps.rs's document-only write rule. Do not cache a path across launches without re-validating size + mtime.

## Cost (4.9.1, Python port, 14 cores)

Cold scan of all 87,297 files (82,303 UnityFS, 64 GB resource + 3.6 GB document, only the first blocks read): 130 s,
62 MB cache; incremental rescan 0.5 s. `validate --sha1` (hash all 85,421 effective files against the index): ~190 s.

## Verification evidence

- 4.8.0 copy (`/Volumes/Miyu/Punishing Gray Raven 4.8`): `validate` matches 42,496 UnityFS entries (47,456 index entries,
  4,960 raw) and 42,346/42,346 manifest bundles; the un-prefixed path is unchanged.
- 4.9.1: `validate --sha1`: 85,421/85,421 + 19/19 sha1+size vs index, manifest 85,421/85,421 bundles' assets == scanned
  container assets, 0 duplicate logical names.
- Not done: the client's live `UrlTable` on 4.9 (needs a running client via Proxima).
