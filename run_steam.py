#!/usr/bin/env python3
"""Run AscNet and its Steam/PC mitmproxy bridge together."""

from __future__ import annotations

import argparse
import errno
import json
import contextlib
import os
import shutil
import signal
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path
if sys.platform == "win32":
    import msvcrt
else:
    import fcntl
from typing import BinaryIO, Iterable

ROOT = Path(__file__).resolve().parent
DEFAULT_KRSDK_CACHE_DIR = Path.home() / "Applications/Sikarugir/Steam-AscNet.app/Contents/SharedSupport/prefix/drive_c/users/Sikarugir/AppData/Roaming/KR_G143/A1855"
LOCAL_KRSDK_OAUTH_CODE = "ascnet-local-oauth-code"
CONFIG_SMOKE_TARGETS = [
    (
        "global-client",
        "/prod/client/config/9jY3H6OqsppPLu31/com.kurogame.punishing.grayraven.en/4.6.0/standalone/config.tab",
        "Channel\tint\t5",
    ),
    (
        "steam-pc-package",
        "/prod/client/config/9jY3H6OqsppPLu31/com.kurogame.pc.punishing.grayraven.en/4.6.0/standalone/config.tab",
        "Channel\tint\t205",
    ),
]
CURRENT_DOCUMENT_VERSION = "4.6.7"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Run AscNet on an unprivileged local SDK port and bridge Steam/Kuro HTTP(S) traffic through mitmproxy.",
    )
    parser.add_argument("--sdk-url", default="http://127.0.0.1:8080", help="AscNet SDK server URL. Default: %(default)s")
    parser.add_argument("--proxy-host", default="127.0.0.1", help="mitmproxy listen host. Default: %(default)s")
    parser.add_argument("--proxy-port", type=int, default=8081, help="mitmproxy listen port. Default: %(default)s")
    parser.add_argument("--dotnet", default=os.environ.get("DOTNET"), help="dotnet executable. Defaults to DOTNET, PATH, then /Users/reiserfs/.dotnet/dotnet")
    parser.add_argument("--mitm", default=os.environ.get("MITMPROXY"), help="mitmproxy/mitmdump executable. Defaults to MITMPROXY, mitmdump, then mitmproxy")
    parser.add_argument("--with-mongo", action="store_true", help="Start a local mongod for AscNet login/player data if MongoDB is not already reachable.")
    parser.add_argument("--mongod", default=os.environ.get("MONGOD"), help="mongod executable. Defaults to MONGOD, then PATH.")
    parser.add_argument("--mongo-host", default="127.0.0.1", help="MongoDB bind/check host. Default: %(default)s")
    parser.add_argument("--mongo-port", type=int, default=27017, help="MongoDB bind/check port. Default: %(default)s")
    parser.add_argument("--mongo-dbpath", default=".runtime/mongo", help="Local mongod dbpath when --with-mongo starts MongoDB. Default: %(default)s")
    parser.add_argument(
        "--gate-fallback-username",
        default=os.environ.get("ASCNET_GATE_FALLBACK_USERNAME"),
        help="Local AscNet username to use when Steam/KRSDK reaches /api/Login/Login with an unknown external uid. Pass an empty value to disable. Default: --ascnet-username.",
    )
    parser.add_argument(
        "--ascnet-username",
        default=os.environ.get("ASCNET_USERNAME") or os.environ.get("ASCNET_GATE_FALLBACK_USERNAME") or "test",
        help="Local AscNet account username to create/use for Steam login handoff. Default: %(default)s",
    )
    parser.add_argument(
        "--ascnet-password",
        default=os.environ.get("ASCNET_PASSWORD", "test"),
        help="Local AscNet account password used when --ascnet-username must be created. Default: %(default)s",
    )
    parser.add_argument("--no-ensure-account", action="store_true", help="Do not create/check the local AscNet account before launching.")
    parser.add_argument(
        "--krsdk-cache-dir",
        default=os.environ.get("ASCNET_KRSDK_CACHE_DIR", str(DEFAULT_KRSDK_CACHE_DIR)),
        help="KRSDK cache directory to repair and optionally seed. Empty disables cache maintenance. Default: %(default)s",
    )
    parser.add_argument("--seed-krsdk-cache", action="store_true", help="Opt in to writing a local AscNet account into KRSDKUserCache.json/KRSDKUserLauncherCache.json. Usually not needed for Steam; live KRSDK login plus gate fallback is safer.")
    parser.add_argument("--no-seed-krsdk-cache", action="store_true", help="Legacy guard: do not write KRSDKUserCache.json/KRSDKUserLauncherCache.json.")
    parser.add_argument("--no-repair-krsdk-cache", action="store_true", help="Do not remove stale AscNet-local KRSDK cache entries created by older runner versions.")
    parser.add_argument("--no-proxy", action="store_true", help="Only run AscNet; do not start mitmproxy.")
    parser.add_argument("--no-smoke", action="store_true", help="Skip the Steam config smoke check before starting mitmproxy/launch command.")
    parser.add_argument("--smoke-timeout", type=float, default=30.0, help="Seconds to wait for AscNet config smoke. Default: %(default)s")
    parser.add_argument("--proxy-log", default=".runtime/proxy-flows.log", help="Write redacted HTTP flow diagnostics here. Empty disables. Default: %(default)s")
    parser.add_argument("--proxy-https", action="store_true", help="Also set HTTPS_PROXY for diagnostics. May break pinned KRSDK HTTPS hosts.")
    parser.add_argument("--stop-when-launch-exits", action="store_true", help="Stop AscNet/proxy when --launch-cmd exits. Default keeps the bridge alive for launchers that spawn and detach.")
    parser.add_argument(
        "--launch-cmd",
        nargs=argparse.REMAINDER,
        help="Optional command to start after AscNet/proxy are ready. Use '--launch-cmd <cmd> <args...>'. Proxy env vars are injected.",
    )
    return parser.parse_args()


def resolve_dotnet(value: str | None) -> str:
    candidates = [value, shutil.which("dotnet"), "/Users/reiserfs/.dotnet/dotnet"]
    for candidate in candidates:
        if candidate and Path(candidate).exists():
            return candidate
        if candidate and shutil.which(candidate):
            return candidate
    raise SystemExit("dotnet not found. Pass --dotnet /path/to/dotnet or set DOTNET.")


def resolve_mitm(value: str | None) -> str:
    candidates = [value, shutil.which("mitmdump"), shutil.which("mitmproxy")]
    for candidate in candidates:
        if candidate and Path(candidate).exists():
            return candidate
        if candidate and shutil.which(candidate):
            return candidate
    raise SystemExit("mitmproxy not found. Install mitmproxy or pass --mitm /path/to/mitmdump.")

def resolve_mongod(value: str | None) -> str:
    candidates = [value, shutil.which("mongod")]
    for candidate in candidates:
        if candidate and Path(candidate).exists():
            return candidate
        if candidate and shutil.which(candidate):
            return candidate
    raise SystemExit("mongod not found. Install MongoDB, pass --mongod /path/to/mongod, or start MongoDB yourself on 127.0.0.1:27017.")


def can_connect(host: str, port: int, timeout: float = 0.5) -> bool:
    try:
        with socket.create_connection((host, port), timeout=timeout):
            return True
    except OSError:
        return False


def normalise_local_sdk_url(value: str) -> str:
    raw = value.strip()
    if "://" not in raw:
        raw = f"http://{raw}"

    parsed = urllib.parse.urlparse(raw)
    if parsed.scheme not in {"http", "https"}:
        raise SystemExit(f"--sdk-url must be http:// or https://, got {value!r}.")

    host = parsed.hostname or "127.0.0.1"
    if host in {"*", "0.0.0.0", "::", "[::]"}:
        host = "127.0.0.1"

    try:
        port = parsed.port
    except ValueError as exc:
        raise SystemExit(f"--sdk-url has an invalid port: {value!r}.") from exc

    if parsed.path not in {"", "/"}:
        raise SystemExit(f"--sdk-url must be a base URL without a path, got {value!r}.")

    host_part = f"[{host}]" if ":" in host and not host.startswith("[") else host
    netloc = host_part if port is None else f"{host_part}:{port}"
    return urllib.parse.urlunparse((parsed.scheme, netloc, "", "", "", ""))


def wait_for_tcp(host: str, port: int, timeout: float, label: str) -> None:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if can_connect(host, port):
            print(f"{label} OK: {host}:{port}", flush=True)
            return
        time.sleep(0.25)
    raise SystemExit(f"{label} did not become reachable on {host}:{port} within {timeout:g}s.")


def _lock_build_file(lock_file: BinaryIO) -> None:
    if sys.platform != "win32":
        fcntl.flock(lock_file.fileno(), fcntl.LOCK_EX)
        return

    lock_file.seek(0, os.SEEK_END)
    if lock_file.tell() == 0:
        lock_file.write(b"\0")
        lock_file.flush()

    while True:
        lock_file.seek(0)
        try:
            msvcrt.locking(lock_file.fileno(), msvcrt.LK_NBLCK, 1)
            return
        except OSError as exc:
            if exc.errno not in {errno.EACCES, errno.EAGAIN, errno.EDEADLK}:
                raise
            time.sleep(0.05)

def _unlock_build_file(lock_file: BinaryIO) -> None:
    if sys.platform != "win32":
        fcntl.flock(lock_file.fileno(), fcntl.LOCK_UN)
        return

    lock_file.seek(0)
    msvcrt.locking(lock_file.fileno(), msvcrt.LK_UNLCK, 1)



@contextlib.contextmanager
def serialized_build_lock() -> Iterable[None]:
    """Prevent concurrent runner instances from writing the shared MSBuild obj tree."""
    lock_path = ROOT / ".runtime" / "ascnet-build.lock"
    lock_path.parent.mkdir(parents=True, exist_ok=True)
    with lock_path.open("a+b") as lock_file:
        print(f"Waiting for AscNet build lock: {lock_path}", flush=True)
        _lock_build_file(lock_file)
        try:
            yield
        finally:
            _unlock_build_file(lock_file)


def build_ascnet(dotnet: str, env: dict[str, str]) -> None:
    """Build once under a cross-process lock; dotnet run then only executes outputs."""
    project = "AscNet/AscNet.csproj"
    with serialized_build_lock():
        cmd = [dotnet, "build", project]
        print("+ " + " ".join(cmd), flush=True)
        completed = subprocess.run(cmd, cwd=ROOT, env=env)
        if completed.returncode:
            raise SystemExit(completed.returncode)


def ascnet_run_command(dotnet: str, sdk_url: str) -> list[str]:
    return [dotnet, "run", "--no-build", "--project", "AscNet/AscNet.csproj", "--", "--urls", sdk_url]


def popen(cmd: list[str], *, env: dict[str, str] | None = None) -> subprocess.Popen[bytes]:
    print("+ " + " ".join(cmd), flush=True)
    return subprocess.Popen(cmd, cwd=ROOT, env=env)


def smoke_check(sdk_url: str, timeout: float) -> None:
    for label, path, channel_assertion in CONFIG_SMOKE_TARGETS:
        smoke_config_target(sdk_url, timeout, label, path, channel_assertion)


def smoke_config_target(sdk_url: str, timeout: float, label: str, path: str, channel_assertion: str) -> None:
    url = sdk_url.rstrip("/") + path
    deadline = time.monotonic() + timeout
    last_error: Exception | None = None

    while time.monotonic() < deadline:
        try:
            with urllib.request.urlopen(url, timeout=2.0) as response:
                body = response.read().decode("utf-8", errors="replace")
            required = [
                "ApplicationVersion\tstring\t4.6.0",
                f"DocumentVersion\tstring\t{CURRENT_DOCUMENT_VERSION}",
                f"LaunchModuleVersion\tstring\t{CURRENT_DOCUMENT_VERSION}",
                channel_assertion,
                "KuroPayCallbackUrl\tstring\t",
                "PcPayCallbackUrl\tstring\t",
                "IsPCPayEnable\tbool\t1",
            ]
            missing = [needle for needle in required if needle not in body]
            if missing:
                raise RuntimeError(f"{label} smoke response is missing: " + ", ".join(missing))
            print(f"Smoke OK [{label}]: {url}", flush=True)
            return
        except (urllib.error.URLError, TimeoutError, RuntimeError) as exc:
            last_error = exc
            time.sleep(0.5)

    raise SystemExit(f"AscNet did not pass {label} config smoke within {timeout:g}s: {last_error}")

def post_json(url: str, payload: dict[str, str], timeout: float) -> dict[str, object]:
    data = json.dumps(payload).encode("utf-8")
    request = urllib.request.Request(url, data=data, method="POST")
    request.add_header("Content-Type", "application/json")
    with urllib.request.urlopen(request, timeout=timeout) as response:
        body = response.read().decode("utf-8", errors="replace")
    parsed = json.loads(body)
    if not isinstance(parsed, dict):
        raise RuntimeError(f"{url} returned non-object JSON: {body[:200]}")
    return parsed


def response_account(payload: dict[str, object]) -> dict[str, object] | None:
    account = payload.get("account")
    return account if isinstance(account, dict) else None


def account_value(account: dict[str, object], *names: str) -> object:
    lowered = {key.lower(): value for key, value in account.items()}
    for name in names:
        if name in account:
            return account[name]
        lowered_value = lowered.get(name.lower())
        if lowered_value is not None:
            return lowered_value
    raise RuntimeError(f"AscNet account response is missing {names[0]}.")


def ensure_ascnet_account(sdk_url: str, username: str, password: str, timeout: float) -> dict[str, object]:
    base = sdk_url.rstrip("/")
    credentials = {"username": username, "password": password}
    try:
        login_payload = post_json(f"{base}/api/AscNet/login", credentials, timeout)
        if login_payload.get("code") == 0 and (account := response_account(login_payload)) is not None:
            print(f"AscNet account OK: {username}", flush=True)
            return account

        register_payload = post_json(f"{base}/api/AscNet/register", credentials, timeout)
        if register_payload.get("code") == 0 and (account := response_account(register_payload)) is not None:
            print(f"AscNet account created: {username}", flush=True)
            return account

        raise RuntimeError(str(register_payload.get("msg") or login_payload.get("msg") or "unknown account error"))
    except (urllib.error.URLError, TimeoutError, json.JSONDecodeError, RuntimeError) as exc:
        raise SystemExit(
            f"Could not ensure AscNet account '{username}'. Start MongoDB with --with-mongo, "
            f"or pass --no-ensure-account for config-only testing. Details: {exc}"
        )


def read_json_file(path: Path, fallback: object) -> object:
    if not path.exists() or path.stat().st_size == 0:
        return fallback
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError:
        return fallback


def backup_once(path: Path) -> None:
    if not path.exists():
        return
    backup = path.with_suffix(path.suffix + ".bak")
    if not backup.exists():
        shutil.copy2(path, backup)



def is_local_krsdk_account(item: object) -> bool:
    if not isinstance(item, dict):
        return False
    email = str(item.get("email") or "")
    oauth_code = str(item.get("oauthCode") or "")
    return email.endswith("@ascnet.local") or oauth_code == LOCAL_KRSDK_OAUTH_CODE


def repair_krsdk_login_cache(cache_dir: Path) -> None:
    user_cache_path = cache_dir / "KRSDKUserCache.json"
    launcher_cache_path = cache_dir / "KRSDKUserLauncherCache.json"
    removed_user_entries = 0
    removed_launcher_entries = 0

    user_cache = read_json_file(user_cache_path, {})
    if isinstance(user_cache, dict):
        account_list = user_cache.get("account_list")
        if isinstance(account_list, list):
            kept_accounts = [item for item in account_list if not is_local_krsdk_account(item)]
            removed_user_entries = len(account_list) - len(kept_accounts)
            if removed_user_entries:
                removed_cuids = {
                    str(item.get("cuid"))
                    for item in account_list
                    if is_local_krsdk_account(item) and isinstance(item, dict) and item.get("cuid") is not None
                }
                last_login_cuid = str(user_cache.get("last_login_cuid") or "")
                user_cache["account_list"] = kept_accounts
                if not last_login_cuid or last_login_cuid in removed_cuids:
                    user_cache["last_login_cuid"] = next(
                        (
                            str(item.get("cuid"))
                            for item in kept_accounts
                            if isinstance(item, dict) and item.get("cuid") is not None
                        ),
                        "",
                    )
                backup_once(user_cache_path)
                user_cache_path.write_text(json.dumps(user_cache, ensure_ascii=False, separators=(",", ":")) + "\n", encoding="utf-8")

    launcher_cache = read_json_file(launcher_cache_path, [])
    if isinstance(launcher_cache, list):
        kept_launcher_accounts = [item for item in launcher_cache if not is_local_krsdk_account(item)]
        removed_launcher_entries = len(launcher_cache) - len(kept_launcher_accounts)
        if removed_launcher_entries:
            backup_once(launcher_cache_path)
            launcher_cache_path.write_text(json.dumps(kept_launcher_accounts, ensure_ascii=False, separators=(",", ":")) + "\n", encoding="utf-8")

    if removed_user_entries or removed_launcher_entries:
        print(
            "Removed stale AscNet-local KRSDK cache entries: "
            f"{removed_user_entries} user cache, {removed_launcher_entries} launcher cache.",
            flush=True,
        )

def seed_krsdk_login_cache(cache_dir: Path, account: dict[str, object]) -> None:
    cache_dir.mkdir(parents=True, exist_ok=True)
    uid = int(account_value(account, "Uid", "uid"))
    cuid = str(uid)
    username = str(account_value(account, "Username", "username"))
    token = str(account_value(account, "Token", "token"))
    email = f"{username}@ascnet.local"

    user_item = {
        "id": uid,
        "cuid": cuid,
        "username": username,
        "loginType": 23,
        "code": "0",
        "email": email,
        "autoToken": token,
        "token": token,
        "bindDevStat": 0,
        "idStat": 0,
        "firstLgn": 0,
        "bindDevMsg": "",
        "realNameMethod": 0,
        "thirdNickName": username,
        "bindDevSwitch": 0,
        "realNameUrl": "",
        "realNameKey": "",
    }
    user_cache_path = cache_dir / "KRSDKUserCache.json"
    user_cache = read_json_file(user_cache_path, {})
    if not isinstance(user_cache, dict):
        user_cache = {}
    account_list = user_cache.get("account_list")
    if not isinstance(account_list, list):
        account_list = []
    account_list = [item for item in account_list if not isinstance(item, dict) or str(item.get("cuid")) != cuid]
    account_list.insert(0, user_item)
    user_cache["account_list"] = account_list
    user_cache["last_login_cuid"] = cuid
    backup_once(user_cache_path)
    user_cache_path.write_text(json.dumps(user_cache, ensure_ascii=False, separators=(",", ":")) + "\n", encoding="utf-8")

    launcher_item = {
        "cuid": cuid,
        "email": email,
        "id": uid,
        "loginType": 23,
        "oauthCode": LOCAL_KRSDK_OAUTH_CODE,
        "thirdNickName": username,
        "username": username,
    }
    launcher_cache_path = cache_dir / "KRSDKUserLauncherCache.json"
    launcher_cache = read_json_file(launcher_cache_path, [])
    if not isinstance(launcher_cache, list):
        launcher_cache = []
    launcher_cache = [item for item in launcher_cache if not isinstance(item, dict) or str(item.get("cuid")) != cuid]
    launcher_cache.insert(0, launcher_item)
    backup_once(launcher_cache_path)
    launcher_cache_path.write_text(json.dumps(launcher_cache, ensure_ascii=False, separators=(",", ":")) + "\n", encoding="utf-8")
    print(f"KRSDK local login cache seeded: {cache_dir}", flush=True)


def proxy_env(base: dict[str, str], proxy_host: str, proxy_port: int, sdk_url: str, proxy_log: str, proxy_https: bool) -> dict[str, str]:
    env = dict(base)
    proxy_url = f"http://{proxy_host}:{proxy_port}"
    env["ASCNET_PROXY_TARGET"] = sdk_url
    env["http_proxy"] = proxy_url
    env["HTTP_PROXY"] = proxy_url
    for inherited_proxy_name in ("https_proxy", "HTTPS_PROXY", "all_proxy", "ALL_PROXY"):
        env.pop(inherited_proxy_name, None)

    if proxy_log:
        log_path = (ROOT / proxy_log).resolve()
        log_path.parent.mkdir(parents=True, exist_ok=True)
        log_path.write_text("", encoding="utf-8")
        env["ASCNET_PROXY_LOG"] = str(log_path)
    else:
        env.pop("ASCNET_PROXY_LOG", None)
    if proxy_https:
        env["https_proxy"] = proxy_url
        env["HTTPS_PROXY"] = proxy_url
    return env


def normalize_launch_cmd(raw: list[str] | None) -> list[str] | None:
    if not raw:
        return None
    if raw and raw[0] == "--":
        raw = raw[1:]
    if not raw:
        raise SystemExit("--launch-cmd was provided without a command.")
    return raw



def terminate(processes: Iterable[subprocess.Popen[bytes]]) -> None:
    alive = [process for process in processes if process.poll() is None]
    for process in reversed(alive):
        process.terminate()
    deadline = time.monotonic() + 5.0
    for process in reversed(alive):
        remaining = max(0.0, deadline - time.monotonic())
        try:
            process.wait(timeout=remaining)
        except subprocess.TimeoutExpired:
            process.kill()


def main() -> int:
    args = parse_args()
    args.sdk_url = normalise_local_sdk_url(args.sdk_url)
    dotnet = resolve_dotnet(args.dotnet)
    mitm = None if args.no_proxy else resolve_mitm(args.mitm)
    launch_cmd = normalize_launch_cmd(args.launch_cmd)

    env = os.environ.copy()
    env["ASCNET_PUBLIC_HTTP_ORIGIN"] = args.sdk_url.rstrip("/")
    gate_fallback_username = args.gate_fallback_username
    if gate_fallback_username is None:
        gate_fallback_username = args.ascnet_username
    if gate_fallback_username:
        env["ASCNET_GATE_FALLBACK_USERNAME"] = gate_fallback_username
    else:
        env.pop("ASCNET_GATE_FALLBACK_USERNAME", None)
    child_env = proxy_env(env, args.proxy_host, args.proxy_port, args.sdk_url, args.proxy_log, args.proxy_https)
    print(f"AscNet SDK URL: {args.sdk_url}", flush=True)
    processes: list[subprocess.Popen[bytes]] = []
    ignored_exit_processes: set[int] = set()

    def handle_signal(signum: int, _frame: object) -> None:
        print(f"Received signal {signum}; stopping children...", flush=True)
        terminate(processes)
        raise SystemExit(128 + signum)

    signal.signal(signal.SIGINT, handle_signal)
    signal.signal(signal.SIGTERM, handle_signal)

    if args.with_mongo:
        if can_connect(args.mongo_host, args.mongo_port):
            print(f"MongoDB already reachable: {args.mongo_host}:{args.mongo_port}", flush=True)
        else:
            mongod = resolve_mongod(args.mongod)
            dbpath = (ROOT / args.mongo_dbpath).resolve()
            dbpath.mkdir(parents=True, exist_ok=True)
            logpath = dbpath.parent / "mongod.log"
            mongo = popen([
                mongod,
                "--dbpath",
                str(dbpath),
                "--bind_ip",
                args.mongo_host,
                "--port",
                str(args.mongo_port),
                "--logpath",
                str(logpath),
                "--logappend",
                "--quiet",
            ], env=env)
            processes.append(mongo)
            wait_for_tcp(args.mongo_host, args.mongo_port, 20.0, "MongoDB")
    elif not can_connect(args.mongo_host, args.mongo_port):
        print(f"MongoDB not reachable on {args.mongo_host}:{args.mongo_port}; config endpoints work, but login/player APIs will fail until MongoDB is running.", flush=True)

    cache_dir = Path(args.krsdk_cache_dir).expanduser() if args.krsdk_cache_dir else None
    if cache_dir and not args.no_repair_krsdk_cache and not args.seed_krsdk_cache:
        repair_krsdk_login_cache(cache_dir)

    try:
        build_ascnet(dotnet, env)
        ascnet = popen(ascnet_run_command(dotnet, args.sdk_url), env=env)
        processes.append(ascnet)

        if not args.no_smoke:
            smoke_check(args.sdk_url, args.smoke_timeout)

        account = None
        if not args.no_ensure_account:
            account = ensure_ascnet_account(args.sdk_url, args.ascnet_username, args.ascnet_password, args.smoke_timeout)

        if args.seed_krsdk_cache and not args.no_seed_krsdk_cache and cache_dir:
            if account is None:
                print("Skipping KRSDK cache seeding because --no-ensure-account was used.", flush=True)
            else:
                seed_krsdk_login_cache(cache_dir, account)

        if mitm:
            proxy = popen([mitm, "--listen-host", args.proxy_host, "--listen-port", str(args.proxy_port), "-s", "proxy.py"], env=child_env)
            processes.append(proxy)
            print(f"Proxy env: http_proxy=http://{args.proxy_host}:{args.proxy_port}; ASCNET_PROXY_TARGET={child_env['ASCNET_PROXY_TARGET']}; flow log={args.proxy_log or '<disabled>'}", flush=True)

        if launch_cmd:
            launcher = popen(launch_cmd, env=child_env)
            processes.append(launcher)
            if args.stop_when_launch_exits:
                return launcher.wait()
            ignored_exit_processes.add(id(launcher))
            print("Launch command started; keeping AscNet Steam bridge running. Press Ctrl-C to stop.", flush=True)

        if not launch_cmd:
            print("AscNet Steam bridge is running. Press Ctrl-C to stop.", flush=True)
        while True:
            for process in processes:
                if id(process) in ignored_exit_processes:
                    continue
                code = process.poll()
                if code is not None:
                    print(f"Bridge child exited with code {code}; stopping remaining children.", flush=True)
                    return code
            time.sleep(1.0)
    finally:
        terminate(processes)


if __name__ == "__main__":
    raise SystemExit(main())
