#!/usr/bin/env python3
"""Export installed EN Lua TextAssets from verified effective matrix bundles."""
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
BUNDLES = ("matrix", "dlcfight")
UNITY_INSPECT = Path(__file__).resolve().parents[2] / "tools/unitypy_asset_inspect.py"
OFFICIAL_MATRIX_CDN = ("http://prod-encdn-ak.pgr-game.com/prod/client/patch/"
                       "YHcyljDAVMYA6tK8/com.kurogame.punishing.grayraven.en/4.8.0/standalone/4.8.10/matrix")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=Path(".runtime/upgrade-4.8/lua"))
    parser.add_argument("--cdn-root", default=OFFICIAL_MATRIX_CDN,
                        help="Official 4.8.10 CDN matrix root for locally modified/missing bundles")
    args = parser.parse_args()
    spec = importlib.util.spec_from_file_location("focus_index", FOCUS)
    focus = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(focus)
    assets = focus.load_module(focus.ASSET_VIEWER, "pgr_assets")
    inspector_spec = importlib.util.spec_from_file_location("unity_inspect", UNITY_INSPECT)
    inspector = importlib.util.module_from_spec(inspector_spec)
    inspector_spec.loader.exec_module(inspector)
    UnityPy = inspector.load_unitypy()
    UnityPy.set_assetbundle_decrypt_key(inspector.DEFAULT_KEY)
    install = Path(json.loads(focus.DEFAULT_MANIFEST.read_text())["source_install"])
    root = install / "PGR_Data/StreamingAssets"
    resolver = pgr_bundles.Resolver(install)
    indexes = {scope: {"sha1": hashlib.sha1((root / scope / "matrix/index").read_bytes()).hexdigest(), "entries": table}
               for scope, table in resolver.indexes.items()}
    effective = {**resolver.indexes.get("resource", {}), **resolver.indexes.get("document", {})}
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    bundle_report = []
    module_report = []
    for group in BUNDLES:
        logical = f"assets/temp/lua/{group}.ab"
        entry = effective.get(logical)
        data = None
        origin = None
        if entry is None:  # index no longer maps the name (4.9+): locate by bundle contents; sha1 cannot be checked
            path = resolver.resolve(logical)
            data = path.read_bytes()
            origin = f"{path} (scan, unverified)"
            entry = [path.name, hashlib.sha1(data).hexdigest(), len(data)]
        else:
            for scope in ("document", "resource"):
                scoped = indexes.get(scope, {}).get("entries", {}).get(logical)
                if scoped is None or scoped[1:] != entry[1:]:
                    continue
                path = root / scope / "matrix" / str(scoped[0])
                if path.is_file():
                    candidate = path.read_bytes()
                    if len(candidate) == entry[2] and hashlib.sha1(candidate).hexdigest() == entry[1]:
                        data, origin = candidate, str(path)
                        break
            if data is None:
                if not args.cdn_root:
                    raise ValueError(f"Missing unmodified indexed bundle {logical}: {entry}; supply --cdn-root")
                from urllib.request import urlopen
                url = f"{args.cdn_root.rstrip('/')}/{entry[0]}"
                with urlopen(url, timeout=120) as response:
                    candidate = response.read()
                if len(candidate) != entry[2] or hashlib.sha1(candidate).hexdigest() != entry[1]:
                    raise ValueError(f"Official bundle identity mismatch: {url}")
                data, origin = candidate, url
                raw = output / "bundles" / str(entry[0])
                raw.parent.mkdir(parents=True, exist_ok=True)
                raw.write_bytes(data)
        _, _, payload = assets.decode_bundle_payload(data)
        expected = Counter((item["name"], item["sha1"]) for item in focus.iter_lua_text_assets(assets, payload))
        exported = Counter()
        prefix = f"assets/temp/lua/{group}/"
        for source_path, pointer in UnityPy.load(BytesIO(data)).container.items():
            if not source_path.startswith(prefix) or not source_path.endswith(".lua.bytes"):
                continue
            text_asset = pointer.read()
            name = text_asset.m_Name
            raw = text_asset.m_Script
            if isinstance(raw, str):
                raw = raw.encode("utf-8", "surrogateescape")
            sha1 = hashlib.sha1(raw).hexdigest()
            exported[name, sha1] += 1
            rel = source_path[len("assets/temp/lua/") :].removesuffix(".bytes").lower()
            path = output / rel
            if not path.resolve().is_relative_to(output):
                raise ValueError(f"Unsafe module path: {source_path}")
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(raw)
            module_report.append({"path": rel, "source_path": source_path, "source_name": name,
                                  "bundle": logical, "size": len(raw), "sha1": sha1})
        if expected-exported:
            raise ValueError(f"TextAsset iterator/container mismatch: {logical}, missing {sum((expected-exported).values())}, extra {sum((exported-expected).values())}")
        bundle_report.append({"asset": logical, "scope": "document" if logical in indexes.get("document", {}).get("entries", {}) else "resource",
                              "entry": entry, "origin": origin, "sha1": hashlib.sha1(data).hexdigest(),
                              "modules": sum(exported.values())})
        print(f"{group}: {sum(exported.values())} modules, {origin}", flush=True)
    launch_indexes = assets.load_launch_indexes(install)

    def launch_source(logical: str) -> tuple[bytes, Path | str]:
        entry = {**launch_indexes["resource"], **launch_indexes.get("document", {})}[logical]
        for candidate_scope in ("document", "resource"):
            candidate_entry = launch_indexes.get(candidate_scope, {}).get(logical)
            if candidate_entry is None or candidate_entry[1:] != entry[1:]:
                continue
            candidate_path = root / candidate_scope / "launch" / str(candidate_entry[0])
            if candidate_path.is_file():
                content = candidate_path.read_bytes()
                if len(content) == entry[2] and hashlib.sha1(content).hexdigest() == entry[1]:
                    return content, candidate_path
        if not args.cdn_root:
            raise ValueError(f"Missing index-matching launch bundle: {logical} {entry}")
        from urllib.request import urlopen
        url = f"{args.cdn_root.rstrip('/').removesuffix('/matrix')}/launch/{entry[0]}"
        with urlopen(url, timeout=120) as response:
            content = response.read()
        if len(content) != entry[2] or hashlib.sha1(content).hexdigest() != entry[1]:
            raise ValueError(f"Official launch bundle identity mismatch: {url}")
        path = output / "bundles" / str(entry[0])
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(content)
        return content, url
    index_hashes = {scope: hashlib.sha1((root / scope / "launch/index").read_bytes()).hexdigest()
                    for scope in launch_indexes}
    manifest_entry = {**launch_indexes["resource"], **launch_indexes.get("document", {})}["launchmanifest"]
    manifest_bytes, manifest_path = launch_source("launchmanifest")
    manifest_payload = assets.decode_bundle_payload(manifest_bytes)[2]
    launch_assets, launch_bundles, groups, _, _ = assets.find_launch_manifest_msgpack(manifest_payload)[2]
    logical = "assets/temp/lua/launch.ab"
    launch_entry = {**launch_indexes["resource"], **launch_indexes.get("document", {})}[logical]
    launch_bytes, launch_path = launch_source(logical)
    launch_payload = assets.decode_bundle_payload(launch_bytes)[2]
    count = 0
    for asset_index, source_path in enumerate(launch_assets):
        if not isinstance(source_path, str) or not source_path.lower().startswith("assets/temp/lua/launch/") or not source_path.lower().endswith(".lua.bytes"):
            continue
        if not any(isinstance(indices, list) and asset_index in indices and launch_bundles[i] == logical
                   for i, indices in enumerate(groups)):
            continue
        raw_asset = assets.find_embedded_text_asset(launch_payload, assets.logical_asset_serialized_name(source_path))
        if raw_asset is None:
            raise ValueError(f"Missing launch TextAsset {source_path}")
        raw = raw_asset["content"]
        rel = source_path[len("assets/temp/lua/") :].removesuffix(".bytes").lower()
        path = output / rel
        if not path.resolve().is_relative_to(output):
            raise ValueError(f"Unsafe launch path: {source_path}")
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(raw)
        module_report.append({"path": rel, "source_path": source_path, "source_name": path.name,
                              "bundle": logical, "size": len(raw), "sha1": hashlib.sha1(raw).hexdigest()})
        count += 1
    if not count:
        raise ValueError("Launch manifest contains no Lua TextAssets")
    bundle_report.append({"asset": logical, "scope": "document" if logical in launch_indexes.get("document", {}) else "resource",
                          "entry": launch_entry, "origin": str(launch_path),
                          "sha1": hashlib.sha1(launch_bytes).hexdigest(), "modules": count})
    print(f"launch: {count} modules, {launch_path}", flush=True)
    (output / "provenance.json").write_text(json.dumps({"matrix_index_sha1": {k: v["sha1"] for k,v in indexes.items()},
        "launch_index_sha1": index_hashes, "launch_manifest": {"entry": manifest_entry, "sha1": hashlib.sha1(manifest_bytes).hexdigest()},
        "bundles": bundle_report, "modules": module_report}, indent=2) + "\n")


if __name__ == "__main__":
    main()
