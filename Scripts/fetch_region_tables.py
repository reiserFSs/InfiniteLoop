#!/usr/bin/env python3
"""Build a tables-only stub install of a region's client from Kuro's CDN (no 60 GB install).

Reads the region's pinned launcher index (AscNet.Launcher/supported-client.json downloads.<region>) and its document patch
(documentPatch pin), resolves the effective matrix file table (package InPkgIndex + document index, sha1-pinned), reads the
client's own matrixmanifest and downloads only the bundles that hold `assets/temp/bytes/**` tables, verifying every file's
sha1/size against the indexes. Output is a directory shaped like an install (PGR_Data/StreamingAssets/{resource,document}/matrix)
that Scripts/scan_client_upgrade.py --extract (see extract_region_tables.py) can read.

  fetch_region_tables.py --region tw --out /Volumes/Miyu/tw49 [--build 1739062]
"""
from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import sys
import urllib.request
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import pgr_bundles as pb  # noqa: E402

CONFIG = Path(__file__).resolve().parent.parent / "AscNet.Launcher/supported-client.json"
TABLE_PREFIX = "assets/temp/bytes/"


def get(urls: "str | list[str]", dest: Path, sha1: str | None = None, size: int | None = None) -> str | None:
    """Download from the first of `urls` that works (a CDN host can be unreachable), verify size/sha1, atomic rename."""
    urls = [urls] if isinstance(urls, str) else urls
    if dest.is_file() and (size is None or dest.stat().st_size == size) and (sha1 is None or hashlib.sha1(dest.read_bytes()).hexdigest() == sha1):
        return
    dest.parent.mkdir(parents=True, exist_ok=True)
    for attempt in range(4):
        url = urls[attempt % len(urls)]
        try:
            with urllib.request.urlopen(url, timeout=30) as r, open(dest.with_name(dest.name + ".part"), "wb") as f:
                shutil.copyfileobj(r, f)
            data = dest.with_name(dest.name + ".part").read_bytes()
            if (size is not None and len(data) != size) or (sha1 and hashlib.sha1(data).hexdigest() != sha1):
                raise OSError(f"size/sha1 mismatch for {url}")
            dest.with_name(dest.name + ".part").replace(dest)
            return url
        except OSError:
            if attempt == 3:
                raise


def manifest_assets(path: Path) -> dict[str, list[str]]:
    """{physical bundle file: [lowercase assets]} from a 4.9 matrixmanifest bundle (see pgr_bundles._manifest)."""
    def v49(value):
        return (isinstance(value, list) and len(value) == 6 and all(isinstance(x, list) for x in (value[0], value[1], value[4], value[5]))
                and len(value[4]) == len(value[1]) and len(value[5]) == len(value[0]))
    value = pb._find_msgpack(pb.payload(path), (b"\x96\xdd",), v49)
    if value is None:
        raise ValueError("matrixmanifest layout not recognised")
    files, assets, _, _, asset_bundle, _ = value
    out = {name: [] for name in files}
    for asset, owners in zip(assets, asset_bundle):
        out[files[owners[0]]].append(asset.lower())
    return out


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--region", required=True)
    ap.add_argument("--out", type=Path, required=True)
    ap.add_argument("--build", help="document build (default: documentPatch.defaultBuild)")
    ap.add_argument("--workers", type=int, default=8)
    args = ap.parse_args()

    dl = json.loads(CONFIG.read_text())["downloads"][args.region]
    dp = dl["documentPatch"]
    build = args.build or dp["defaultBuild"]
    version = dp.get("versions", {}).get(build, dp["defaultVersion"])
    sums = dp["builds"][build]
    streaming = args.out / "PGR_Data/StreamingAssets"
    for cdn in dl["cdnList"]:  # first launcher CDN host that resolves and serves the pinned index
        try:
            launcher = {e["dest"]: e for e in json.loads(urllib.request.urlopen(cdn + dl["indexFile"], timeout=120).read())["resource"]}
            break
        except OSError as e:
            print(f"launcher CDN {cdn}: {e}", flush=True)
    else:
        raise SystemExit("no launcher CDN served the index")
    pkg_url = cdn + dl["baseUrl"] + "PGR_Data/StreamingAssets/resource/matrix/"
    doc_urls = [f"{cdn}client/patch/{dp['key']}/{dp['identifier']}/{version}/standalone/Release/" for cdn in dp["cdns"]]

    pkg_index = streaming / "resource/matrix/InPkgIndex"
    entry = launcher["PGR_Data/StreamingAssets/resource/matrix/InPkgIndex"]
    get(pkg_url + "InPkgIndex", pkg_index, None, entry["size"])
    assert hashlib.md5(pkg_index.read_bytes()).hexdigest() == entry["md5"], "InPkgIndex md5"
    doc_index = streaming / "document/matrix/index"
    served = get([f"{u}{build}/matrix/index" for u in doc_urls], doc_index, sums["matrixSha1"])  # sha1 pinned in supported-client.json
    if served:  # the host that answered goes first for every file
        doc_urls.sort(key=lambda u: not served.startswith(u))
    pkg, doc = (pb.read_file_table(p)["files"] for p in (pkg_index, doc_index))

    def effective(name: str):
        """(scope, [logical, sha1, size, svn, ...]): package copy if the same sha1 ships in the package, else document."""
        p, d = pkg.get(name), doc.get(name)
        if d is None:
            return ("resource", p) if p else None
        return ("resource", p) if p and p[5] != 1 and p[1] == d[1] else ("document", d)

    def fetch(name: str):
        scope, e = effective(name)
        dest = streaming / scope / "matrix" / name
        if scope == "resource":
            get(pkg_url + name, dest, e[1], e[2])
            assert hashlib.md5(dest.read_bytes()).hexdigest() == launcher[f"PGR_Data/StreamingAssets/resource/matrix/{name}"]["md5"], name
        else:
            get([f"{u}{e[3]}/matrix/{name}" for u in doc_urls], dest, e[1], e[2])
        return scope

    extensionless = [n for n in doc if "." not in n]  # the one extensionless file of the matrix tree is matrixmanifest
    assert len(extensionless) == 1, extensionless
    manifest = extensionless[0]
    scope = fetch(manifest)
    tables = [n for n, assets in manifest_assets(streaming / scope / "matrix" / manifest).items() if any(a.startswith(TABLE_PREFIX) for a in assets)]
    print(f"{args.region} build {build}: package {len(pkg)} / document {len(doc)} files; {len(tables)} table bundles", flush=True)
    with ThreadPoolExecutor(args.workers) as pool:
        counts = {}
        for s in pool.map(fetch, tables):
            counts[s] = counts.get(s, 0) + 1
    print("downloaded and verified:", counts)
    return 0


if __name__ == "__main__":
    sys.exit(main())
