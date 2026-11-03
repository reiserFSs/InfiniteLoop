#!/usr/bin/env python3
"""Resolve PGR matrix bundles by logical name, independent of the matrix index's name -> file mapping.

Bundle files under StreamingAssets/{resource,document}/matrix are content-addressed (<sha1>.uab). The matrix index maps
logical names (assets/temp/lua/matrix.ab) to them; 4.9 reportedly drops that mapping for most bundles. Every UnityFS
bundle still carries its own AssetBundle object (m_Name + container asset paths), so we scan the bundles themselves
and cache the result (JSON, keyed by file name + size + mtime). Only the first LZ4 blocks of each bundle are decoded.

  pgr_bundles.py scan [--install DIR]         (re)build the cache incrementally
  pgr_bundles.py resolve assets/temp/lua/matrix.ab
  pgr_bundles.py find assets/temp/lua/matrix/xxx.lua
  pgr_bundles.py validate                      compare the scan against the real index (4.8 proof)
  pgr_bundles.py verify-live [--host H]        cross-check against the running client via Proxima (UNVERIFIED)
"""
from __future__ import annotations

import argparse
import importlib.util
import json
import os
import struct
import sys
import time
from collections import Counter, defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DEFAULT_MANIFEST = ROOT.parent / "manifest/native-starter-manifest.json"
VENDOR = ROOT.parent / "external-tools/python-vendor"
CACHE = ROOT / ".runtime/pgr_bundles_cache.json"
KEY = b"XxecodrPeGaka2e6"  # AES-128-CTR key of the PGR protected-UnityFS seed (bundle.rs KEY)
SCOPES = ("document", "resource")  # priority order: document overrides resource
ASSET_BUNDLE_CLASS = 142
CACHE_VERSION = 2
TREES = ("matrix", "launch")  # StreamingAssets/<scope>/<tree>/: two bundle namespaces, each with its own index
MANIFEST_ASSET = "assets/temp/matrix/matrixmanifest.bytes"
SPECIAL_NAMES = {MANIFEST_ASSET: "matrixmanifest", "assets/temp/launch/launchmanifest.bytes": "launchmanifest"}  # no .ab suffix


if VENDOR.exists() and str(VENDOR) not in sys.path:  # UnityPy, pycryptodome, lz4 live in the shared tools vendor dir
    sys.path.insert(0, str(VENDOR))


def default_install() -> Path:
    return Path(json.loads(DEFAULT_MANIFEST.read_text())["source_install"])


def matrix_dir(install: Path, scope: str, tree: str = "matrix") -> Path:
    return Path(install) / "PGR_Data/StreamingAssets" / scope / tree


def split_key(key: str) -> tuple[str, str, str]:
    """Cache key -> (scope, tree, file name). Keys: "<scope>/<file>" (matrix) or "<scope>/launch/<file>"."""
    scope, rest = key.split("/", 1)
    tree, _, name = rest.rpartition("/")
    return scope, tree or "matrix", name


# --- protected UnityFS (port of AscNet.Launcher/src/bundle.rs) -------------------------------------------------------

def _cstr(data: bytes, offset: int) -> tuple[bytes, int]:
    end = data.index(b"\0", offset)
    return data[offset:end], end + 1


def _align16(n: int) -> int:
    return (n + 15) & ~15


def _lz4(data: bytes, compression: int, size: int) -> bytes:
    if compression == 0:
        if len(data) != size:
            raise ValueError("uncompressed UnityFS size mismatch")
        return data
    if compression in (2, 3):
        import lz4.block
        return lz4.block.decompress(data, size)
    raise ValueError(f"unsupported UnityFS compression {compression}")


def _protected_context(seed: bytes) -> list[int]:
    from Crypto.Cipher import AES
    from Crypto.Util import Counter as AesCounter
    ctr = AesCounter.new(128, initial_value=int.from_bytes(seed[16:], "big"))
    decrypted = AES.new(KEY, AES.MODE_CTR, counter=ctr).encrypt(seed[:16])
    context = [0] * 32
    for i in range(16):
        context[(decrypted[i // 2] >> 4) if i % 2 == 0 else (decrypted[i // 2] & 15)] = i
    for i, position in enumerate((16, 20, 24, 28, 17, 21, 25, 29, 18, 22, 26, 30, 19, 23, 27, 31)):
        byte = decrypted[8 + i // 2]
        context[position] = (byte >> 4) if i % 2 == 0 else (byte & 15)
    return context


def _transform_tables(context: list[int]):
    """Byte transform of bundle.rs, tabulated: shift depends on counter mod 256, value is one byte."""
    shifts = [(context[28 + ((c >> 6) & 3)] + context[24 + ((c >> 4) & 3)]
               + context[20 + ((c >> 2) & 3)] + context[16 + (c & 3)]) & 15 for c in range(256)]
    tables = [bytes((((context[v >> 4] - shift) & 15) << 4) | ((context[v & 15] - shift) & 15) for v in range(256))
              for shift in range(16)]
    return [tables[s] for s in shifts]  # indexed by counter & 255


def _untransform_lz4(data: bytes, context: list[int], initial: int) -> bytes:
    """Undo the per-token obfuscation of control bytes (token, length extensions, match offsets) of an LZ4 block."""
    tab = _transform_tables(context)
    out = bytearray(data)
    n = len(out)
    offset, sequence = 0, initial
    while offset < n:
        token = out[offset] = tab[sequence & 255][out[offset]]
        offset += 1
        sequence += 1
        control = sequence
        literals = token >> 4
        if literals == 15:
            while True:
                if offset >= n:
                    raise ValueError("truncated protected LZ4 literal length")
                value = out[offset] = tab[control & 255][out[offset]]
                offset += 1
                control += 1
                literals += value
                if value != 255:
                    break
        offset += literals
        if offset > n:
            raise ValueError("protected LZ4 literal overruns block")
        if offset == n:
            break
        if offset + 2 > n:
            raise ValueError("truncated protected LZ4 match offset")
        for _ in range(2):
            out[offset] = tab[control & 255][out[offset]]
            offset += 1
            control += 1
        if token & 15 == 15:
            while True:
                if offset >= n:
                    raise ValueError("truncated protected LZ4 match length")
                value = out[offset] = tab[control & 255][out[offset]]
                offset += 1
                control += 1
                if value != 255:
                    break
    return bytes(out)


class Bundle:
    """Lazy decoder: `prefix(n)` decompresses only as many blocks as needed to cover the first n payload bytes."""

    def __init__(self, path: Path):
        self.f = open(path, "rb")
        size = os.fstat(self.f.fileno()).st_size
        head = self.f.read(256)
        sig, off = _cstr(head, 0)
        if sig != b"UnityFS":
            raise ValueError("not UnityFS")
        off += 4
        for _ in range(2):
            _, off = _cstr(head, off)
        file_size, info_comp, info_plain, flags = struct.unpack_from(">QIII", head, off)
        header_size = off + 20
        if file_size != size or not info_comp or flags & 0x80:
            raise ValueError("unsupported UnityFS layout")
        self.context = None
        if flags & 0x1000:
            start = _align16(header_size) if flags & 0x400 else header_size
            self.f.seek(start)
            meta = self.f.read(4 + 33 * 2 + 1 + 32)
            seed, o = _cstr(meta, 4)
            _, o = _cstr(meta, o)
            if len(seed) != 32:
                raise ValueError("invalid protected seed")
            self.context = _protected_context(seed)
            start += o
        else:
            start = _align16(header_size) if flags & 0x200 else header_size
        self.f.seek(start)
        raw = self.f.read(info_comp + 16)
        for skip in range(16 if self.context else 1):
            try:
                plain = _lz4(raw[skip:skip + info_comp], flags & 0x3F, info_plain)
                self.blocks, self.nodes = self._directory(plain)
            except Exception:  # noqa: BLE001 - try next alignment skip
                continue
            self.cursor = _align16(start + skip + info_comp) if self.context or flags & 0x200 else start + skip + info_comp
            break
        else:
            raise ValueError("cannot locate UnityFS directory")
        self.payload = bytearray()
        self.next_block = 0

    @staticmethod
    def _directory(data: bytes):
        count, = struct.unpack_from(">I", data, 16)
        off = 20
        blocks = []
        for _ in range(count):
            blocks.append(struct.unpack_from(">IIH", data, off))
            off += 10
        n, = struct.unpack_from(">I", data, off)
        off += 4
        nodes = []
        for _ in range(n):
            o, s, fl = struct.unpack_from(">QQI", data, off)
            name, off = _cstr(data, off + 20)
            nodes.append((o, s, fl, name.decode("utf-8", "replace")))
        if off != len(data):
            raise ValueError("invalid UnityFS directory length")
        return blocks, nodes

    def prefix(self, n: int) -> bytes:
        while len(self.payload) < n and self.next_block < len(self.blocks):
            plain, comp, bflags = self.blocks[self.next_block]
            self.f.seek(self.cursor)
            data = self.f.read(comp)
            if self.context and bflags & 0x3F in (2, 3):
                data = _untransform_lz4(data, self.context, self.next_block)
            self.payload += _lz4(data, bflags & 0x3F, plain)
            self.cursor += comp
            self.next_block += 1
        return bytes(self.payload[:n])

    def close(self):
        self.f.close()


def read_container(path: Path) -> dict:
    """Decode just enough of one bundle to read its AssetBundle object: {name, assets[]} (or kind != 'unity')."""
    try:
        bundle = Bundle(path)
    except ValueError as error:
        return {"kind": "raw" if str(error) == "not UnityFS" else "error", "error": str(error)}
    except Exception as error:  # noqa: BLE001 - record per-file failure, keep scanning
        return {"kind": "error", "error": f"{type(error).__name__}: {error}"}
    try:
        from UnityPy.files import SerializedFile
        from UnityPy.files.ObjectReader import ObjectReader
        from UnityPy.streams import EndianBinaryReader
        node = next((n for n in bundle.nodes if n[2] & 4), None)
        if node is None:
            return {"kind": "error", "error": "no serialized node"}
        base = node[0]
        head = bundle.prefix(base + 48)[base:]
        if len(head) < 40:
            return {"kind": "error", "error": "short serialized file"}
        version, data_offset = struct.unpack_from(">II", head, 8)
        if version >= 22:
            data_offset, = struct.unpack_from(">Q", head, 32)
        # The constructor also parses the AssetBundle object, which a metadata-only prefix cannot satisfy: skip that
        # step once to learn where the object lives, then reparse with the blocks covering it.
        parse = ObjectReader.parse_as_object
        ObjectReader.parse_as_object = lambda self, *a, **k: []
        try:
            sf = SerializedFile(EndianBinaryReader(bundle.prefix(base + data_offset)[base:]), name="x")
        finally:
            ObjectReader.parse_as_object = parse
        obj = next((o for o in sf.objects.values() if o.type.value == ASSET_BUNDLE_CLASS), None)
        if obj is None:
            return {"kind": "error", "error": "no AssetBundle object"}
        sf = SerializedFile(EndianBinaryReader(bundle.prefix(base + data_offset + obj.byte_start + obj.byte_size)[base:]), name="x")
        data = sf.assetbundle
        return {"kind": "unity", "name": data.m_Name, "assets": sorted(k for k, _ in data.m_Container)}
    except Exception as error:  # noqa: BLE001
        return {"kind": "error", "error": f"{type(error).__name__}: {error}"}
    finally:
        bundle.close()


# --- scan + cache ----------------------------------------------------------------------------------------------------

def _scan_one(job):
    key, path = job
    return key, read_container(Path(path))


def scan(install: Path | None = None, cache_path: Path = CACHE, workers: int | None = None, progress: bool = False) -> dict:
    """Walk resource+document matrix dirs, decode new/changed *.uab files in parallel, save + return the cache.

    cache = {"version", "install", "files": {"<scope>/<file name>": {scope, size, mtime_ns, kind, name?, assets?, error?}}}.
    An entry is reused while file name, size and mtime are unchanged. Non-.uab files (audio/video) are kind "raw".
    """
    install = Path(install) if install else default_install()
    old = {}
    if cache_path.exists():
        loaded = json.loads(cache_path.read_text())
        if loaded.get("version") == CACHE_VERSION:
            old = loaded["files"]
    files, jobs = {}, []
    for scope, tree in ((s, t) for t in TREES for s in SCOPES):
        if not matrix_dir(install, scope, tree).is_dir():
            continue
        for entry in os.scandir(matrix_dir(install, scope, tree)):
            if entry.name == "index" or not entry.is_file():
                continue
            st = entry.stat()
            key = f"{scope}/{entry.name}" if tree == "matrix" else f"{scope}/{tree}/{entry.name}"
            rec = {"scope": scope, "tree": tree, "size": st.st_size, "mtime_ns": st.st_mtime_ns}
            prev = old.get(key)
            if prev and prev["size"] == rec["size"] and prev["mtime_ns"] == rec["mtime_ns"]:
                rec.update({k: v for k, v in prev.items() if k not in rec})
            elif entry.name.endswith(".uab"):
                jobs.append((key, entry.path))
            else:
                rec["kind"] = "raw"
            files[key] = rec
    if jobs:
        from multiprocessing import get_context
        with get_context("fork").Pool(workers) as pool:  # fork: safe for callers without a __main__ guard
            for i, (key, result) in enumerate(pool.imap_unordered(_scan_one, jobs, chunksize=16), 1):
                if "assets" in result:
                    result["assets"] = sorted(set(result["assets"]))
                files[key].update(result)
                if progress and i % 5000 == 0:
                    print(f"scanned {i}/{len(jobs)}", file=sys.stderr)
    cache = {"version": CACHE_VERSION, "install": str(install), "files": files}
    saved = {**cache}
    if jobs or files.keys() != old.keys():
        cache_path.parent.mkdir(parents=True, exist_ok=True)
        tmp = cache_path.with_suffix(".tmp")
        tmp.write_text(json.dumps(saved, separators=(",", ":")))
        tmp.replace(cache_path)
    cache["scanned"] = len(jobs)  # decoded this call (not persisted)
    return cache


def container_claims(assets: list[str]) -> list[tuple[int, str]]:
    """Logical names a bundle may carry, derived from container asset paths alone: (tier, name), tier 0 best.

    Proven on 4.8: the name is `<asset>.ab` (single-asset bundles) or `<dir>.ab` where dir is the common directory of all
    assets or one of its ancestors. Tier 0 = asset.ab / common dir, tier 1 = a strict ancestor of the common dir.
    """
    parts = [a.split("/")[:-1] for a in assets]
    common = []
    for column in zip(*parts):
        if len(set(column)) != 1:
            break
        common.append(column[0])
    out = [(0, assets[0] + ".ab")] if len(assets) == 1 else []
    if len(assets) == 1 and assets[0] in SPECIAL_NAMES:
        out.append((0, SPECIAL_NAMES[assets[0]]))  # names without .ab, hard-coded by the client (ResolveBundleManifest)
    out += [(0 if i == len(common) else 1, "/".join(common[:i]) + ".ab")
            for i in range(len(common), 0, -1) if i == len(common) or i >= 2]  # never the bare root ("assets.ab")
    return out


# --- real matrix index (name -> [file, sha1, size]) -------------------------------------------------------------------

def _msgpack(data: bytes, off: int = 0, depth: int = 0):
    """Minimal MessagePack reader (the subset the matrix index uses); returns (value, next offset)."""
    if depth > 64:
        raise ValueError("msgpack too deep")
    m = data[off]
    off += 1
    if m <= 0x7F:
        return m, off
    if m >= 0xE0:
        return m - 256, off
    if 0x80 <= m <= 0x8F or m in (0xDE, 0xDF):
        n = m & 15 if m < 0x90 else int.from_bytes(data[off:off + (2 if m == 0xDE else 4)], "big")
        off += 0 if m < 0x90 else (2 if m == 0xDE else 4)
        out = {}
        for _ in range(n):
            k, off = _msgpack(data, off, depth + 1)
            out[k], off = _msgpack(data, off, depth + 1)
        return out, off
    if 0x90 <= m <= 0x9F or m in (0xDC, 0xDD):
        n = m & 15 if m < 0xA0 else int.from_bytes(data[off:off + (2 if m == 0xDC else 4)], "big")
        off += 0 if m < 0xA0 else (2 if m == 0xDC else 4)
        out = []
        for _ in range(n):
            v, off = _msgpack(data, off, depth + 1)
            out.append(v)
        return out, off
    if 0xA0 <= m <= 0xBF or m in (0xD9, 0xDA, 0xDB):
        if m <= 0xBF:
            n = m & 31
        else:
            w = {0xD9: 1, 0xDA: 2, 0xDB: 4}[m]
            n, off = int.from_bytes(data[off:off + w], "big"), off + w
        return data[off:off + n].decode("utf-8", "replace"), off + n
    fixed = {0xC0: None, 0xC2: False, 0xC3: True}
    if m in fixed:
        return fixed[m], off
    for marker, width, signed in ((0xCC, 1, 0), (0xCD, 2, 0), (0xCE, 4, 0), (0xCF, 8, 0),
                                  (0xD0, 1, 1), (0xD1, 2, 1), (0xD2, 4, 1), (0xD3, 8, 1)):
        if m == marker:
            return int.from_bytes(data[off:off + width], "big", signed=bool(signed)), off + width
    raise ValueError(f"unsupported msgpack marker 0x{m:02x}")


def read_index_file(path: Path) -> dict[str, list]:
    """Decode a matrix `index` file -> {logical name: [file, sha1, size]}. Raises if the 4.8 layout is not found."""
    bundle = Bundle(path)
    try:
        payload = bundle.prefix(sum(b[0] for b in bundle.blocks))
    finally:
        bundle.close()
    start = payload.find(b"\x93")
    while start >= 0:
        try:
            value, _ = _msgpack(payload, start)
            table = value[0] if isinstance(value, list) and len(value) == 3 else None
            if isinstance(table, dict) and all(isinstance(k, str) and isinstance(v, list) and len(v) == 3
                                               for k, v in list(table.items())[:50]):
                return table
        except (ValueError, IndexError, KeyError):
            pass
        start = payload.find(b"\x93", start + 1)
    raise ValueError(f"matrix index table not found in {path}")


def read_indexes(install: Path, tree: str = "matrix") -> dict[str, dict[str, list]]:
    """Per-scope index tables; a scope whose index is missing/undecodable (4.9?) is omitted."""
    out = {}
    for scope in SCOPES:
        path = matrix_dir(install, scope, tree) / "index"
        if path.is_file():
            try:
                out[scope] = read_index_file(path)
            except (ValueError, OSError, struct.error):
                pass
    return out


# --- resolver --------------------------------------------------------------------------------------------------------

def _valid_name(name) -> bool:
    return isinstance(name, str) and bool(name)  # 4.8: always the logical name (incl. packages/... and "matrixmanifest")


class Resolver:
    """logical bundle name / asset path -> bundle file. Order: real index (if it maps the name) -> scan cache.

    Scan cache lookup prefers the bundle's own AssetBundle name, then names derived from container paths (so a repack
    that renames or hashes m_Name still resolves); among several files claiming a name, document beats resource, then
    the newest file wins (4.8: stale resource copies of document-updated bundles).
    """

    def __init__(self, install: Path | None = None, cache_path: Path = CACHE, use_index: bool = True):
        self.install = Path(install) if install else default_install()
        self.cache_path = cache_path
        self.indexes = read_indexes(self.install) if use_index else {}
        self.launch_indexes = read_indexes(self.install, "launch") if use_index else {}
        self._cache = None
        self._claims_map = None
        self._asset_map = None

    # real index
    def entry(self, logical: str, scope: str | None = None) -> list | None:
        """The real index's [file, sha1, size] for a name (document overrides resource), or None."""
        for sc in (scope,) if scope else SCOPES:
            if logical in self.indexes.get(sc, {}):
                return self.indexes[sc][logical]
        return None

    # scan cache
    @property
    def files(self) -> dict:
        if self._cache is None:
            self._cache = scan(self.install, self.cache_path)["files"]
        return self._cache

    def _path(self, key: str) -> Path:
        scope, tree, name = split_key(key)
        return matrix_dir(self.install, scope, tree) / name

    def _claims(self) -> dict[str, list[tuple[int, str]]]:
        if self._claims_map is None:
            claims = defaultdict(list)
            for key, rec in self.files.items():
                if rec["kind"] != "unity":
                    continue
                if _valid_name(rec["name"]):
                    claims[rec["name"]].append((-1, key))
                for tier, name in container_claims(rec["assets"]) if rec["assets"] else ():
                    claims[name].append((tier, key))
            self._claims_map = claims
        return self._claims_map

    def candidates(self, logical: str, scope: str | None = None, names: bool = True, containers: bool = True) -> list[str]:
        """Scan-cache keys ("scope/file") claiming `logical`, best first (best claim tier, document first, newest)."""
        scopes = (scope,) if scope else SCOPES
        found = [(t, k) for t, k in self._claims().get(logical, ())
                 if (t < 0 and names or t >= 0 and containers) and self.files[k]["scope"] in scopes]
        if not found:
            return []
        best = min(t for t, _ in found)
        keys = [k for t, k in found if t == best]
        return sorted(keys, key=lambda k: (scopes.index(self.files[k]["scope"]), -self.files[k]["mtime_ns"]))

    def resolve(self, logical: str, scope: str | None = None) -> Path:
        scopes = (scope,) if scope else SCOPES
        for tree, indexes in (("matrix", self.indexes), ("launch", self.launch_indexes)):
            for sc in scopes:
                ent = indexes.get(sc, {}).get(logical)
                for s in scopes if ent else ():
                    path = matrix_dir(self.install, s, tree) / str(ent[0])
                    if path.is_file():
                        return path
        keys = self.candidates(logical, scope)
        if keys:
            return self._path(keys[0])
        raise FileNotFoundError(f"bundle not found by index or scan: {logical} ({scope or 'any scope'})")

    def names(self, prefix: str = "", suffix: str = "", scope: str | None = None) -> list[str]:
        """Logical names known to the index or the scan (own AssetBundle name, else the best derived claim)."""
        scopes = (scope,) if scope else SCOPES
        out = {n for idx in (self.indexes, self.launch_indexes) for sc in scopes for n in idx.get(sc, {})}
        for rec in self.files.values():
            if rec["kind"] != "unity" or rec["scope"] not in scopes:
                continue
            if _valid_name(rec["name"]):
                out.add(rec["name"])
            elif rec["assets"]:
                out.update(n for t, n in container_claims(rec["assets"]) if t == 0)
        return sorted(n for n in out if n.startswith(prefix) and n.endswith(suffix))

    def find_asset(self, asset_path: str, scope: str | None = None) -> tuple[Path, str] | None:
        """(bundle file, logical name) of the bundle whose container holds asset_path (case-insensitive)."""
        if self._asset_map is None:
            amap = defaultdict(list)
            for key, rec in self.files.items():
                for asset in rec.get("assets", ()):
                    amap[asset].append(key)
            self._asset_map = amap
        scopes = (scope,) if scope else SCOPES
        keys = sorted((k for k in self._asset_map.get(asset_path.lower(), ()) if self.files[k]["scope"] in scopes),
                      key=lambda k: (scopes.index(self.files[k]["scope"]), -self.files[k]["mtime_ns"]))
        if not keys:
            return None
        rec = self.files[keys[0]]
        logical = rec["name"] if _valid_name(rec["name"]) else (container_claims(rec["assets"]) or [(0, rec["name"])])[0][1]
        return self._path(keys[0]), logical

    def asset_bundles(self) -> dict[str, str]:
        """{lowercase asset path: logical bundle name} over both trees: the effective file per logical name (document beats
        resource, then newest; stale copies are ignored)."""
        out, seen = {}, set()
        for key in sorted(self.files, key=lambda k: (SCOPES.index(self.files[k]["scope"]), -self.files[k]["mtime_ns"])):
            rec = self.files[key]
            if rec["kind"] != "unity" or not rec["assets"]:
                continue
            logical = rec["name"] if _valid_name(rec["name"]) else container_claims(rec["assets"])[0][1]
            if logical in seen:
                continue
            seen.add(logical)
            for asset in rec["assets"]:
                out.setdefault(asset, logical)
        return out


    def raw_index_names(self) -> set[str]:
        """Lowercase names of real-index entries that are not bundles (audio/video/acf); only present while the index maps them."""
        return {n.lower() for idx in (self.indexes, self.launch_indexes) for table in idx.values() for n in table
                if not n.endswith(".ab") and n.lower() not in SPECIAL_NAMES.values()}


_default: dict[str, Resolver] = {}


def _resolver(install) -> Resolver:
    key = str(install or "")
    if key not in _default:
        _default[key] = Resolver(install)
    return _default[key]


def resolve(logical_bundle: str, install: Path | None = None, scope: str | None = None) -> Path:
    """Bundle file for a logical name such as assets/temp/lua/matrix.ab (real index first, then the scan cache)."""
    return _resolver(install).resolve(logical_bundle, scope)


def find_asset(asset_path: str, install: Path | None = None) -> tuple[Path, str] | None:
    """(bundle file, logical name) containing asset_path, via the scan cache."""
    return _resolver(install).find_asset(asset_path)


# --- validation against the real index (4.8 proof) -------------------------------------------------------------------

def _manifest(resolver: Resolver):
    """The client's own asset -> bundle table (`matrixmanifest`: [assets, bundles, groups, deps, ?]) as {bundle: [assets]}."""
    path = resolver.resolve("matrixmanifest")
    bundle = Bundle(path)
    try:
        payload = bundle.prefix(sum(b[0] for b in bundle.blocks))
    finally:
        bundle.close()
    at = payload.find(b"\x95")
    while at >= 0:
        try:
            value, _ = _msgpack(payload, at)
            if (isinstance(value, list) and len(value) == 5 and value[0] and isinstance(value[0][0], str)
                    and isinstance(value[1], list) and len(value[1]) == len(value[2])):
                assets = value[0]
                return {name: [assets[i].lower() for i in group] for name, group in zip(value[1], value[2])}
        except (ValueError, IndexError, TypeError):
            pass
        at = payload.find(b"\x95", at + 1)
    raise ValueError("matrixmanifest table not found")


def validate(install: Path | None = None, cache_path: Path = CACHE, examples: int = 5) -> dict:
    """Compare scan-derived logical name -> file with the real effective index, entry by entry."""
    t0 = time.time()
    cache = scan(install, cache_path)
    scan_seconds = time.time() - t0
    r = Resolver(install, cache_path)
    r._cache = cache["files"]
    effective = {}  # name -> [file, sha1, size, tree]; document overrides resource; matrix and launch are disjoint
    for tree, indexes in (("matrix", r.indexes), ("launch", r.launch_indexes)):
        for scope in reversed(SCOPES):
            effective.update({n: [*e, tree] for n, e in indexes.get(scope, {}).items()})
    stats, samples = Counter(), defaultdict(list)

    def record(category, item):
        stats[category] += 1
        if item is not None and len(samples[category]) < examples:
            samples[category].append(item)

    for name, (file, _sha, _size, tree) in effective.items():
        prefix = "" if tree == "matrix" else tree + "/"
        rec = r.files.get(f"document/{prefix}{file}") or r.files.get(f"resource/{prefix}{file}")
        if rec is None:
            record("index entry whose file is not on disk", (name, file))
        elif rec["kind"] == "raw":
            record("raw non-UnityFS file (audio/video): index-only", (name, Path(file).suffix))
        elif rec["kind"] != "unity":
            record("unreadable bundle", (name, file, rec.get("error")))
        else:
            for label, kwargs in (("own AssetBundle name", {"containers": False}), ("container-derived", {"names": False})):
                keys = r.candidates(name, **kwargs)
                got = [split_key(k)[2] for k in keys]
                if got[:1] == [file]:
                    record(f"{label}: match" + (" (ties resolved by doc>newest)" if len(got) > 1 else ""), None)
                elif file in got:
                    record(f"{label}: ambiguous, wrong pick", (name, file, got[:3]))
                else:
                    record(f"{label}: no match", (name, file, rec["name"], rec["assets"][:2]))
    unity_entries = sum(v for k, v in stats.items() if k.startswith("own AssetBundle name"))
    indexed_files = {(e[3], e[0]) for e in effective.values()}
    stale = orphan = 0
    for key, rec in r.files.items():
        if rec["kind"] == "unity" and (rec["tree"], split_key(key)[2]) not in indexed_files:
            if rec["name"] in effective:
                stale += 1  # an older copy whose logical name the index now points elsewhere (document override)
            else:
                orphan += 1
    out = {"index_entries": len(effective), "unity_entries": unity_entries, "stats": dict(stats),
           "samples": dict(samples),
           "unity_files_not_in_effective_index": {"stale copy of an overridden name": stale, "name unknown to index": orphan},
           "scan_seconds": round(scan_seconds, 1), "decoded_in_this_scan": cache["scanned"],
           "cache_bytes": cache_path.stat().st_size if cache_path.exists() else None,
           "bundles_on_disk": sum(1 for v in r.files.values() if v["kind"] == "unity")}
    try:  # extra cross-check against the client's own manifest (asset -> bundle names)
        manifest = _manifest(r)
        agree = disagree = 0
        for bundle, assets in manifest.items():
            keys = r.candidates(bundle, containers=False)
            if keys and set(assets) == set(r.files[keys[0]]["assets"]):
                agree += 1
            else:
                disagree += 1
                if len(samples["manifest group != container assets"]) < examples:
                    samples["manifest group != container assets"].append(bundle)
        out["manifest"] = {"bundles": len(manifest), "assets_equal_to_container": agree, "differ": disagree}
    except (FileNotFoundError, ValueError) as error:
        out["manifest"] = {"error": str(error)}
    return out


# --- live cross-check against the running client's own asset -> bundle table --------------------------------------------

# XResourceManager.UrlTable (private static IDictionary<string,string>, 4.8: 460,827 entries, original-case asset path ->
# logical bundle name; ~1 s to enumerate). Reflection flags 40 = NonPublic|Static. GetBundleUrl itself is case-sensitive.
LIVE_TABLE = "local urls = typeof(CS.XResourceManager):GetField('UrlTable', 40):GetValue(nil)\n"
LIVE_DUMP_LUA = LIVE_TABLE + """local path = CS.UnityEngine.Application.persistentDataPath .. '/ascnet-urltable.tsv'
local f = assert(io.open(path, 'wb'))
local e, n = urls:GetEnumerator(), 0
while e:MoveNext() do local kv = e.Current; f:write(kv.Key, '\\t', kv.Value, '\\n'); n = n + 1 end
f:close()
return path .. '|' .. n"""
LIVE_CHUNK_LUA = LIVE_TABLE + """local skip, take = %d, %d
local e, i, out = urls:GetEnumerator(), 0, {}
while e:MoveNext() do
  if i >= skip + take then break end
  if i >= skip then local kv = e.Current; out[#out + 1] = kv.Key .. '\\t' .. kv.Value end
  i = i + 1
end
return table.concat(out, '\\n')"""
LIVE_DELETE_LUA = """local path = CS.UnityEngine.Application.persistentDataPath .. '/ascnet-urltable.tsv'
return tostring(os.remove(path))"""


def _prefix(asset: str, depth: int = 3) -> str:
    return "/".join(asset.split("/")[:depth])


def dump_url_table(proxima, dump_path: Path | None = None, chunk: int = 2000, log=lambda m: print(m, file=sys.stderr)) -> dict[str, str]:
    """The client's UrlTable as {asset path (original case): bundle}. With dump_path (where the client's
    persistentDataPath/ascnet-urltable.tsv is visible locally) the client writes it with io.open; otherwise it is fetched
    over Proxima `chunk` entries per call (each call re-enumerates from the start). The client-side file is deleted."""
    table = {}
    t0 = time.time()
    if dump_path:
        reply = proxima.lua(LIVE_DUMP_LUA)
        client_path, _, count = reply.rpartition("|")
        try:
            for line in Path(dump_path).read_text(encoding="utf-8", errors="replace").splitlines():
                asset, _, bundle = line.partition("\t")
                table[asset] = bundle
        finally:
            for target in (Path(dump_path),):
                target.unlink(missing_ok=True)
            try:
                proxima.lua(LIVE_DELETE_LUA)  # best effort: normally the unlink above already removed the same file
            except (RuntimeError, ConnectionError, OSError):
                pass
        log(f"dumped {count} entries via file ({client_path}) in {time.time() - t0:.1f}s")
    else:
        skip = 0
        while True:
            rows = proxima.lua(LIVE_CHUNK_LUA % (skip, chunk))
            lines = rows.splitlines()
            for line in lines:
                asset, _, bundle = line.partition("\t")
                table[asset] = bundle
            skip += chunk
            if len(lines) < chunk:
                break
        log(f"fetched {len(table)} entries over Proxima in {time.time() - t0:.1f}s ({chunk}/call)")
    return table


def verify_live(proxima, resolver: Resolver, dump_path: Path | None = None, chunk: int = 2000, examples: int = 10,
                log=lambda m: print(m, file=sys.stderr)) -> dict:
    """Compare the client's UrlTable with the scan (case-insensitive): match / bundle differs / client-only / scan-only."""
    live = {a.lower(): b.lower() for a, b in dump_url_table(proxima, dump_path, chunk, log).items()}
    scan_map = {a: b.lower() for a, b in resolver.asset_bundles().items()}
    stats = {"client entries": len(live), "scan assets": len(scan_map),
             "match": 0, "bundle differs": 0, "client-only": 0, "scan-only": 0}
    samples = defaultdict(list)
    prefixes = {"client-only": Counter(), "scan-only": Counter()}
    raw = resolver.raw_index_names()
    for asset, bundle in live.items():
        mine = scan_map.get(asset)
        if mine is None and asset in raw and bundle == asset:
            stats["match"] += 1  # raw (non-bundle) file the client lists under its own index name
        elif mine is None:
            stats["client-only"] += 1
            prefixes["client-only"][_prefix(asset)] += 1
            samples["client-only"].append((asset, bundle))
        elif mine == bundle:
            stats["match"] += 1
        else:
            stats["bundle differs"] += 1
            samples["bundle differs"].append((asset, bundle, mine))
    for asset in scan_map.keys() - live.keys():
        stats["scan-only"] += 1
        prefixes["scan-only"][_prefix(asset)] += 1
        samples["scan-only"].append((asset, scan_map[asset]))
    return {"stats": stats, "prefixes": {k: v.most_common(15) for k, v in prefixes.items()},
            "samples": {k: v[:examples] for k, v in samples.items()}}


# --- CLI -------------------------------------------------------------------------------------------------------------

def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--install", type=Path, help="game dir (default: source_install of the native-starter manifest)")
    parser.add_argument("--cache", type=Path, default=CACHE)
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("scan").add_argument("--quiet", action="store_true")
    for name in ("resolve", "find"):
        p = sub.add_parser(name)
        p.add_argument("target")
        p.add_argument("--scope", choices=SCOPES)
        p.add_argument("--no-index", action="store_true", help="ignore the real index (simulate 4.9)")
    sub.add_parser("validate")
    live = sub.add_parser("verify-live", help="compare with CS.XResourceManager.GetBundleUrl/XBundleManager.GetFile (UrlTable dump)")
    live.add_argument("--host", default="127.0.0.1")
    live.add_argument("--port", type=int, default=7378)
    live.add_argument("--password", default=os.environ.get("PROXIMA_PASSWORD"))
    live.add_argument("--timeout", type=float, default=30.0)
    live.add_argument("--dump-path", type=Path, help="local path of the client's <persistentDataPath>/ascnet-urltable.tsv "
                      "(client writes it with io.open, we read + delete it); default: fetch over Proxima in chunks")
    live.add_argument("--chunk", type=int, default=2000, help="entries per Proxima call without --dump-path (slow: ~28 s per 2000; use --dump-path)")
    args = parser.parse_args()

    if args.command == "scan":
        t0 = time.time()
        cache = scan(args.install, args.cache, progress=not args.quiet)
        kinds = Counter(rec["kind"] for rec in cache["files"].values())
        print(f"{len(cache['files'])} files {dict(kinds)}; decoded {cache['scanned']} in {time.time() - t0:.1f}s; "
              f"cache {args.cache} ({args.cache.stat().st_size} bytes)")
    elif args.command in ("resolve", "find"):
        resolver = Resolver(args.install, args.cache, use_index=not args.no_index)
        if args.command == "resolve":
            print(resolver.resolve(args.target, args.scope))
        else:
            found = resolver.find_asset(args.target, args.scope)
            if not found:
                raise SystemExit(f"asset not in any scanned bundle: {args.target}")
            print(f"{found[0]}\t{found[1]}")
    elif args.command == "validate":
        print(json.dumps(validate(args.install, args.cache), indent=1, ensure_ascii=False))
    else:
        import pgr
        proxima = pgr.Proxima(args.host, args.port, args.password or pgr.DEFAULT_PASSWORD, args.timeout)
        print(json.dumps(verify_live(proxima, Resolver(args.install, args.cache), args.dump_path, args.chunk), indent=1))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (RuntimeError, ConnectionError, OSError, FileNotFoundError) as error:  # Proxima/Lua failures, missing bundle
        print(f"error: {error}", file=sys.stderr)
        raise SystemExit(1)
