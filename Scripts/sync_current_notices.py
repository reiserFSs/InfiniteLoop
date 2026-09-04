#!/usr/bin/env python3
"""Sync official client notice JSON and referenced assets.

The official notice endpoint supplies the JSON documents; their PicAddr,
PicAddrSlave, Url, and HtmlUrl values identify the assets to mirror from the
official CDN asset root. Every managed file is written byte-exact. A referenced
asset is required: HTTP errors (including 404) stop the sync rather than
silently creating a broken local notice.

Example:
    python3 Scripts/sync_current_notices.py \
        --base-url https://prod-encdn-tx.kurogame.net/prod/client/notice/config/9jY3H6OqsppPLu31/com.kurogame.punishing.grayraven.en/4.6.0/ \
        --version 4.6.0
"""
from __future__ import annotations

import argparse
import json
import sys
import urllib.parse
import urllib.request
from pathlib import Path, PurePosixPath
from typing import Any

REPO = Path(__file__).resolve().parents[1]
NOTICE_FILES = (
    "LoginNotice.json",
    "GameNotice.json",
    "ScrollTextNotice.json",
    "ScrollPicNotice.json",
    "SecondMenuNotice.json",
    "PopUpPicNotice.json",
)
ASSET_FIELDS = {
    "PicAddr": PurePosixPath("client/notice/pic"),
    "PicAddrSlave": PurePosixPath("client/notice/pic"),
    "Url": PurePosixPath("client/notice/html"),
    "HtmlUrl": PurePosixPath("client/notice/html"),
}


def fetch(url: str, timeout: float) -> bytes:
    request = urllib.request.Request(url, headers={"User-Agent": "AscNet-notice-sync"})
    with urllib.request.urlopen(request, timeout=timeout) as response:
        return response.read()


def fetch_optional_notice(url: str, timeout: float) -> bytes | None:
    try:
        return fetch(url, timeout)
    except urllib.error.HTTPError as err:
        if err.code == 404:
            return None
        raise


def asset_root_for_notice_base(base_url: str) -> str:
    parsed = urllib.parse.urlsplit(base_url)
    if not parsed.scheme or not parsed.netloc:
        raise ValueError(f"--base-url must be an absolute URL: {base_url}")
    return urllib.parse.urlunsplit((parsed.scheme, parsed.netloc, "/prod/", "", ""))


def normalized_asset_path(value: str, expected_directory: PurePosixPath) -> PurePosixPath:
    parsed = urllib.parse.urlsplit(value)
    if parsed.scheme or parsed.netloc or parsed.query or parsed.fragment or value.startswith("/"):
        raise ValueError(f"notice asset reference must be a relative path: {value!r}")

    decoded = urllib.parse.unquote(parsed.path)
    path = PurePosixPath(decoded)
    if not decoded or path.is_absolute() or ".." in path.parts or "." in path.parts:
        raise ValueError(f"notice asset reference contains traversal: {value!r}")
    if path.parent != expected_directory or not path.name:
        raise ValueError(f"notice asset reference is outside {expected_directory}: {value!r}")
    return path


def collect_asset_paths(value: Any, paths: set[PurePosixPath]) -> None:
    if isinstance(value, dict):
        for key, child in value.items():
            expected_directory = ASSET_FIELDS.get(key)
            if expected_directory is not None:
                if not isinstance(child, str):
                    raise ValueError(f"notice asset field {key} must be a string")
                paths.add(normalized_asset_path(child, expected_directory))
            else:
                collect_asset_paths(child, paths)
    elif isinstance(value, list):
        for child in value:
            collect_asset_paths(child, paths)


def remove_stale_managed_assets(dest: Path, referenced: set[PurePosixPath]) -> int:
    removed = 0
    referenced_paths = {asset.as_posix() for asset in referenced}
    for directory_name in ("pic", "html"):
        directory = dest / "client" / "notice" / directory_name
        if not directory.exists():
            continue
        for path in directory.rglob("*"):
            if path.is_file() and path.relative_to(dest).as_posix() not in referenced_paths:
                path.unlink()
                removed += 1
        if not any(directory.iterdir()):
            directory.rmdir()
    return removed


def regenerate_schedule_if_available(game_notice_path: Path) -> bool:
    catalog_path = REPO / "Resources" / "table" / "share" / "activity" / "EventCatalog.tsv"
    if not catalog_path.is_file():
        return False

    scripts_directory = str(REPO / "Scripts")
    if scripts_directory not in sys.path:
        sys.path.insert(0, scripts_directory)
    from activity_schedule import regenerate_from_catalog

    regenerate_from_catalog(
        catalog_path,
        game_notice_path,
        REPO / "Resources" / "table" / "share" / "activity" / "ActivitySchedule.tsv",
    )
    return True


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--base-url", required=True,
                        help="official CDN directory URL containing the notice JSON files")
    parser.add_argument("--asset-base-url", default=None,
                        help="official CDN asset root (defaults to the /prod/ root of --base-url)")
    parser.add_argument("--version", required=True,
                        help="client version directory name under Resources/Configs/Notices/")
    parser.add_argument("--dest", type=Path, default=None,
                        help="override destination directory (default: Resources/Configs/Notices/<version>)")
    parser.add_argument("--timeout", type=float, default=30.0, help="per-request timeout in seconds")
    args = parser.parse_args()

    base_url = args.base_url if args.base_url.endswith("/") else args.base_url + "/"
    asset_base_url = args.asset_base_url or asset_root_for_notice_base(base_url)
    if not asset_base_url.endswith("/"):
        asset_base_url += "/"
    dest = args.dest if args.dest is not None else REPO / "Resources" / "Configs" / "Notices" / args.version
    dest.mkdir(parents=True, exist_ok=True)

    installed: list[str] = []
    absent: list[str] = []
    notice_documents: list[Any] = []
    try:
        for name in NOTICE_FILES:
            payload = fetch_optional_notice(urllib.parse.urljoin(base_url, name), args.timeout)
            target = dest / name
            if payload is None:
                if target.exists():
                    target.unlink()
                    print(f"404  {name}: official endpoint absent; removed stale local copy")
                else:
                    print(f"404  {name}: official endpoint absent; kept absent")
                absent.append(name)
                continue

            try:
                document = json.loads(payload.decode("utf-8"))
            except (UnicodeDecodeError, json.JSONDecodeError) as err:
                raise ValueError(f"{name}: official payload is not valid JSON ({err})") from err

            target.write_bytes(payload)
            if target.read_bytes() != payload:
                raise OSError(f"{name}: written bytes do not match fetched payload")
            print(f"OK   {name}: {len(payload)} bytes -> {target.relative_to(REPO)}")
            installed.append(name)
            notice_documents.append(document)

        referenced_assets: set[PurePosixPath] = set()
        for document in notice_documents:
            collect_asset_paths(document, referenced_assets)

        asset_bytes = 0
        for asset_path in sorted(referenced_assets):
            url = urllib.parse.urljoin(asset_base_url, urllib.parse.quote(asset_path.as_posix(), safe="/"))
            payload = fetch(url, args.timeout)
            target = dest / asset_path
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(payload)
            if target.read_bytes() != payload:
                raise OSError(f"{asset_path}: written bytes do not match fetched payload")
            asset_bytes += len(payload)
            print(f"OK   {asset_path}: {len(payload)} bytes <- {url}")

        stale_assets = remove_stale_managed_assets(dest, referenced_assets)
        schedule_regenerated = regenerate_schedule_if_available(dest / "GameNotice.json")
    except Exception as err:
        print(f"FAIL {err}", file=sys.stderr)
        return 1

    print(
        f"Installed {len(installed)} JSON file(s), {len(absent)} official JSON 404(s), "
        f"fetched {len(referenced_assets)} asset(s) / {asset_bytes} bytes, "
        f"removed {stale_assets} stale asset(s), schedule {'regenerated' if schedule_regenerated else 'not generated (EventCatalog.tsv absent)'}, in {dest}"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
