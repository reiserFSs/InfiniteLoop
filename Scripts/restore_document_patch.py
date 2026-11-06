#!/usr/bin/env python3
"""Restore a 4.9 client's in-game patch files (StreamingAssets/document/{matrix,launch}) from Kuro's CDN.

The 4.9 launch Lua (launch/xlaunchupdate/*) with DocumentVersion == LaunchModuleVersion == app version (what AscNet
serves) never downloads anything, but XModuleUpdateStateInitLocalIndex still loads document/<tree>/index when its sha1
equals the served IndexSha1/LaunchIndexSha1, and XModuleUpdateStateInitResData then maps every file that the package
index (resource/matrix/InPkgIndex, resource/launch/index) does not already hold with the same sha1 to document/<tree>/<file>.
So a complete, hash-exact document tree + the CDN's own index = the newest audio/video/InjectFix patch, no download at runtime.

This script ports the client's URL scheme (xmoduleupdateinfo.lua GetUrlByVersion/_GetAbDownloadUrl):
  <cdn>/client/patch/<key>/<KuroGameIdentifier>/<version>/standalone/Release/<svn>/<tree>/<file>
Index files are written last, so an interrupted run never leaves an index that points at missing files.
Only files under <install>/PGR_Data/StreamingAssets/document/{matrix,launch} are written; overwritten files are moved to --backup.

  restore_document_patch.py --install "<game dir>" [--build 1739029] [--dry-run] [--backup DIR]
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import sys
import tempfile
import urllib.request
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import pgr_bundles as pb  # noqa: E402

CDNS = ["http://prod-encdn-ak.pgr-game.com/prod", "http://prod-encdn-aliyun.kurogame.net/prod"]  # en49.tab Primary/SecondaryCdns
KEY = "clEoudH0zUSDQsQa"  # EN
IDENTIFIER, GAME_VERSION = "com.kurogame.punishing.grayraven.en", "4.9.0"  # fallbacks when document/{ver,client2cdn} are absent
# build -> sha1 of the CDN's matrix/launch index == IndexSha1/LaunchIndexSha1 the server must serve (en49.tab)
KNOWN = {"1739029": {"matrix": "7be18b2e546dbf95442e098be024a89c22041a52", "launch": "b069d45a6b7d7679e6ab66c82c661ddd85b92ef3"}}
PKG_INDEX = {"matrix": "InPkgIndex", "launch": "index"}  # package-side index per tree (XModuleUpdateInfo.ApplicationIndex)
TIMEOUT, RETRIES, WORKERS = 60, 4, 8


def sha1_file(path: Path) -> str:
    h = hashlib.sha1()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def fetch(url_for: "list[str]", dest: Path, size: int | None, sha1: str | None) -> None:
    """Download to dest (.part + Range resume), verify size/sha1, atomic rename. url_for: candidate URLs in CDN order."""
    part = dest.with_name(dest.name + ".part")
    last = None
    for attempt in range(RETRIES):
        for url in url_for:
            try:
                have = part.stat().st_size if part.exists() else 0
                req = urllib.request.Request(url, headers={"Range": f"bytes={have}-"} if have else {})
                with urllib.request.urlopen(req, timeout=TIMEOUT) as r:
                    if have and r.status != 206:
                        have = 0  # server ignored Range: restart
                    with open(part, "ab" if have else "wb") as f:
                        shutil.copyfileobj(r, f, 1 << 20)
                if size is not None and part.stat().st_size != size:
                    raise OSError(f"size {part.stat().st_size} != {size}")
                if sha1 and sha1_file(part) != sha1:
                    part.unlink()
                    raise OSError("sha1 mismatch")
                os.replace(part, dest)
                return
            except (OSError, ValueError) as e:  # URLError/HTTPError are OSError
                last = f"{url}: {e}"
    raise RuntimeError(f"download failed after {RETRIES} attempts: {last}")


class Restorer:
    def __init__(self, args):
        self.install = Path(args.install) if args.install else pb.default_install()
        sa = self.install / "PGR_Data" / "StreamingAssets"
        self.doc, self.res = sa / "document", sa / "resource"
        self.dry, self.backup, self.cdns, self.key = args.dry_run, args.backup, args.cdn or CDNS, args.key
        ver = json.loads((self.doc / "ver").read_text()) if (self.doc / "ver").is_file() else {}
        self.build = str(args.build or ver.get("LeastVersionFile") or "")
        if not self.build:
            sys.exit("no --build and document/ver has no LeastVersionFile")
        self.version_path = ver.get("VersionPath", {})
        c2c = self.doc / "client2cdn"
        self.client2cdn = json.loads(c2c.read_text()) if c2c.is_file() else {}
        self.sha = {"matrix": args.index_sha1 or KNOWN.get(self.build, {}).get("matrix"),
                    "launch": args.launch_index_sha1 or KNOWN.get(self.build, {}).get("launch")}
        if not all(self.sha.values()):
            sys.exit(f"build {self.build} has no known index sha1; pass --index-sha1 and --launch-index-sha1 (the served IndexSha1/LaunchIndexSha1)")

    def base(self, svn: str) -> str:
        """xmoduleupdateinfo.lua GetUrlByVersion: per-file-version path, from document/ver + client2cdn."""
        info, c2c = self.version_path.get(svn, {}), self.client2cdn.get(svn, {})
        return (f"client/patch/{info.get('key') or self.key}/{c2c.get('KuroGameIdentifier', IDENTIFIER)}/"
                f"{info.get('version', GAME_VERSION)}/standalone/Release")

    def urls(self, svn: str, tree: str, name: str) -> list[str]:
        return [f"{cdn}/{self.base(svn)}/{svn}/{tree}/{name}" for cdn in self.cdns]

    def safe(self, tree: str, name: str) -> Path:
        path = (self.doc / tree / name).resolve()
        if path.parent != (self.doc / tree).resolve() or "/" in name or name in ("", ".", ".."):
            raise ValueError(f"unsafe file name {name!r}")  # index content is untrusted input
        return path

    def move_to_backup(self, path: Path, tree: str) -> None:
        if self.backup and path.exists():
            target = Path(self.backup) / tree / path.name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.move(path, target)

    def tree(self, tree: str) -> bool:
        tmp = Path(tempfile.mkdtemp(prefix=f"restore-{tree}-"))
        try:
            index = tmp / "index"
            fetch(self.urls(self.build, tree, "index"), index, None, self.sha[tree])
            files = pb.read_file_table(index)["files"]
            pkg_path = self.res / tree / PKG_INDEX[tree]
            pkg = pb.read_file_table(pkg_path)["files"]
            # XModuleUpdateManager:IsInnerResource: package holds it iff same name, flag != 1 and same sha1; the rest lives in document/
            need = {n: e for n, e in files.items() if not ((p := pkg.get(n)) and p[5] != 1 and p[1] == e[1])}
            total = sum(e[2] for e in need.values())
            if not self.dry:
                (self.doc / tree).mkdir(parents=True, exist_ok=True)

            def one(item):
                name, e = item
                dest = self.safe(tree, name)
                if dest.is_file() and dest.stat().st_size == e[2] and sha1_file(dest) == e[1]:
                    return "ok", e[2]
                if self.dry:
                    return "todo", e[2]
                part = dest.with_name(dest.name + ".part")
                if dest.exists():
                    self.move_to_backup(dest, tree)  # wrong content: keep a copy before replacing
                    if dest.exists():
                        dest.unlink()
                if part.exists() and part.stat().st_size > e[2]:
                    part.unlink()
                fetch(self.urls(str(e[3]), tree, name), dest, e[2], e[1])
                return "got", e[2]

            counts, errors = {"ok": [0, 0], "got": [0, 0], "todo": [0, 0]}, []
            with ThreadPoolExecutor(WORKERS) as pool:
                for done, (item, fut) in enumerate(((i, pool.submit(one, i)) for i in need.items()), 1):
                    try:
                        status, size = fut.result()
                        counts[status][0] += 1
                        counts[status][1] += size
                    except Exception as e:  # keep going: resumable, report at the end
                        errors.append(f"{item[0]}: {e}")
                    if done % 100 == 0:
                        print(f"  [{tree}] {done}/{len(need)}", flush=True)
            print(f"[{tree}] index {len(files)} entries, package holds {len(files) - len(need)}, document needs {len(need)} files "
                  f"({total / 1e9:.3f} GB): already valid {counts['ok'][0]} ({counts['ok'][1] / 1e9:.3f} GB), "
                  f"downloaded {counts['got'][0]} ({counts['got'][1] / 1e9:.3f} GB), still to download {counts['todo'][0]} ({counts['todo'][1] / 1e9:.3f} GB), "
                  f"errors {len(errors)}")
            for line in errors[:10]:
                print("  ERROR", line)
            if errors or self.dry:
                return not errors
            dest = self.doc / tree / "index"
            if not (dest.is_file() and sha1_file(dest) == self.sha[tree]):
                self.move_to_backup(dest, tree)
                shutil.copyfile(index, dest.with_name("index.new"))
                os.replace(dest.with_name("index.new"), dest)
            extra = {p.name for p in (self.doc / tree).iterdir() if p.is_file()} - files.keys() - {"index"}
            print(f"[{tree}] index written (sha1 {self.sha[tree]}); {len(extra)} extra files not in the index (left alone)")
            return True
        finally:
            shutil.rmtree(tmp, ignore_errors=True)

    def run(self) -> int:
        print(f"install {self.install}\nbuild {self.build}, key {self.key}, cdns {self.cdns}, {'DRY RUN' if self.dry else 'writing document/'}")
        ok = [self.tree(t) for t in ("launch", "matrix")]  # launch first: small
        return 0 if all(ok) else 1


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--install", help="game dir (default: pgr_bundles default install)")
    ap.add_argument("--build", help="SVN build id (default: document/ver LeastVersionFile)")
    ap.add_argument("--key", default=KEY, help="CDN key (default: EN %(default)s)")
    ap.add_argument("--cdn", action="append", help="CDN base incl. /prod, repeatable (default: EN primary + secondary)")
    ap.add_argument("--index-sha1", help="expected sha1 of matrix/index (= served IndexSha1)")
    ap.add_argument("--launch-index-sha1", help="expected sha1 of launch/index (= served LaunchIndexSha1)")
    ap.add_argument("--backup", help="move files this run overwrites here instead of losing them")
    ap.add_argument("--dry-run", action="store_true", help="fetch the indexes, report what is missing, write nothing")
    return Restorer(ap.parse_args()).run()


if __name__ == "__main__":
    sys.exit(main())
