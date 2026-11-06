#!/usr/bin/env python3
"""Export installed EN Lua TextAssets (matrix, dlcfight, launch) from the effective bundles of an install.

Bundles are located by logical name through pgr_bundles (index or scan, 4.8 and 4.9 layouts) and verified against the
effective index entry (sha1 + size) when the index lists them. Pass --output outside the corpus you want to keep."""
from __future__ import annotations

import argparse
from collections import Counter
import hashlib
import importlib.util
from io import BytesIO
import json
from pathlib import Path

import pgr_bundles

FOCUS = Path(__file__).resolve().parents[2] / "tools/build_re_focus_index.py"
GROUPS = ("matrix", "dlcfight", "launch")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--install", type=Path, help="game dir (default: source_install of the native-starter manifest)")
    parser.add_argument("--output", type=Path, default=Path(".runtime/upgrade-4.8/lua"))
    parser.add_argument("--allow-modified", action="store_true",
                        help="export a bundle whose bytes differ from its index entry (locally modified install); recorded in provenance")
    args = parser.parse_args()
    spec = importlib.util.spec_from_file_location("focus_index", FOCUS)
    focus = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(focus)
    assets = focus.load_module(focus.ASSET_VIEWER, "pgr_assets")  # independent TextAsset iterator for the cross-check
    import UnityPy
    UnityPy.set_assetbundle_decrypt_key(pgr_bundles.KEY.decode())
    install = args.install or pgr_bundles.default_install()
    resolver = pgr_bundles.Resolver(install)
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    bundle_report, module_report = [], []
    for group in GROUPS:
        logical = f"assets/temp/lua/{group}.ab"
        path = resolver.resolve(logical)
        data = path.read_bytes()
        entry = resolver.entry(logical)  # 4.8: [file, sha1, size] by logical name
        if entry is not None and entry[0] != path.name:
            raise ValueError(f"{logical}: index maps {entry[0]} but resolved {path.name}")
        entry = entry or resolver.file_entry(path)  # 4.9: physical-file table
        verified = entry is not None
        if verified and (len(data) != entry[2] or hashlib.sha1(data).hexdigest() != entry[1]):
            if not args.allow_modified:
                raise ValueError(f"{logical}: {path} does not match its effective index entry {entry}")
            verified = False
            print(f"WARNING {logical}: {path} differs from its index entry (modified install)", flush=True)
        payload = pgr_bundles.payload(data)
        expected = Counter((item["name"], item["sha1"]) for item in focus.iter_lua_text_assets(assets, payload))
        exported = Counter()
        prefix = f"assets/temp/lua/{group}/"
        for source_path, pointer in UnityPy.load(BytesIO(pgr_bundles.strip_prefix(data))).container.items():
            if not source_path.startswith(prefix) or not source_path.endswith(".lua.bytes"):
                continue
            text_asset = pointer.read()
            name = text_asset.m_Name
            raw = text_asset.m_Script
            if isinstance(raw, str):
                raw = raw.encode("utf-8", "surrogateescape")
            sha1 = hashlib.sha1(raw).hexdigest()
            exported[name, sha1] += 1
            rel = source_path[len("assets/temp/lua/"):].removesuffix(".bytes").lower()
            target = output / rel
            if not target.resolve().is_relative_to(output):
                raise ValueError(f"Unsafe module path: {source_path}")
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(raw)
            module_report.append({"path": rel, "source_path": source_path, "source_name": name,
                                  "bundle": logical, "size": len(raw), "sha1": sha1})
        if not exported:
            raise ValueError(f"{logical} contains no Lua TextAssets")
        if expected - exported:
            raise ValueError(f"TextAsset iterator/container mismatch: {logical}, missing {sum((expected - exported).values())}, "
                             f"extra {sum((exported - expected).values())}")
        bundle_report.append({"asset": logical, "origin": str(path), "scope": path.parent.parent.name,
                              "index_entry": entry, "index_verified": verified,
                              "sha1": hashlib.sha1(data).hexdigest(), "modules": sum(exported.values())})
        print(f"{group}: {sum(exported.values())} modules, {path}{'' if verified else ' (not in index, unverified)'}", flush=True)
    index_sha1 = {f"{scope}/{tree}/{p.name}": hashlib.sha1(p.read_bytes()).hexdigest()
                  for tree in pgr_bundles.TREES for scope in pgr_bundles.SCOPES
                  if (p := pgr_bundles._index_path(Path(install), scope, tree))}
    (output / "provenance.json").write_text(json.dumps({"install": str(install), "index_sha1": index_sha1,
                                                        "bundles": bundle_report, "modules": module_report}, indent=2) + "\n")


if __name__ == "__main__":
    main()
