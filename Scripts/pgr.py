#!/usr/bin/env python3
"""Drive a running PGR client through its built-in Proxima ("Haru") inspector.

Needs remote-config Debug + IsShowInspector (AscNet `Client.Debug` / `Client.ShowInspector`) and the inspector open
(tap the like count on the player info page 5 times). Protocol, from the inspector's own web client:
  ws://<host>:7378/api; JSON requests {Id, Type, Cmd, Args} (Type 0 Command, 1 StartStream, 2 StopStream, 3 List,
  4 Select); replies {Id, Data, Error}. List returns instances; Select sends Base64(SHA256(password + ConnectionId)).

Usage:
  pgr.py lua 'return 1 + 1'          run Lua on the game's main thread (Proxima `lua` command) and print the result
  pgr.py lua -f script.lua           same, from a file
  pgr.py reload XUi/XUiLogin/XUiLogin [...]   XHotReload.Reload each module
  pgr.py watch                       on every change under Mods/: rebuild/install (needs PGR_LUA_MODS_TOOL), then hot-reload
  pgr.py cmd ls 'Canvas*'            any Proxima console command (`cmd ?` lists them)
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import json
import os
import socket
import struct
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
MODS = ROOT / "Mods"
# The mod builder that diffs against the installed client's shipped Lua lives outside this repository.
LUA_MODS_TOOL = os.environ.get("PGR_LUA_MODS_TOOL")
DEFAULT_PASSWORD = "HaruGames"  # XProximaInspector.Password constant in the 4.8 client


class WebSocket:
    """Minimal RFC 6455 text client (stdlib only): enough for Proxima's JSON messages."""

    def __init__(self, host: str, port: int, path: str, timeout: float):
        self.sock = socket.create_connection((host, port), timeout=timeout)
        key = base64.b64encode(os.urandom(16)).decode()
        request = (f"GET {path} HTTP/1.1\r\nHost: {host}:{port}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
                   f"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n")
        self.sock.sendall(request.encode())
        response = b""
        while b"\r\n\r\n" not in response:
            chunk = self.sock.recv(4096)
            if not chunk:
                raise ConnectionError("inspector closed the connection during the handshake")
            response += chunk
        head, self.buffer = response.split(b"\r\n\r\n", 1)
        if b" 101 " not in head.split(b"\r\n", 1)[0]:
            raise ConnectionError(f"WebSocket upgrade refused: {head.split(b'\r\n', 1)[0].decode(errors='replace')}")

    def send(self, text: str) -> None:
        payload = text.encode()
        header = bytearray([0x81])
        if len(payload) < 126:
            header.append(0x80 | len(payload))
        elif len(payload) < 1 << 16:
            header += bytes([0x80 | 126]) + struct.pack(">H", len(payload))
        else:
            header += bytes([0x80 | 127]) + struct.pack(">Q", len(payload))
        mask = os.urandom(4)
        self.sock.sendall(bytes(header) + mask + bytes(b ^ mask[i % 4] for i, b in enumerate(payload)))

    def _read(self, size: int) -> bytes:
        while len(self.buffer) < size:
            chunk = self.sock.recv(65536)
            if not chunk:
                raise ConnectionError("inspector closed the connection")
            self.buffer += chunk
        data, self.buffer = self.buffer[:size], self.buffer[size:]
        return data

    def receive(self) -> str:
        message = b""
        while True:
            first, second = self._read(2)
            size = second & 0x7F
            if size == 126:
                size = struct.unpack(">H", self._read(2))[0]
            elif size == 127:
                size = struct.unpack(">Q", self._read(8))[0]
            mask = self._read(4) if second & 0x80 else None
            data = self._read(size)
            if mask:
                data = bytes(b ^ mask[i % 4] for i, b in enumerate(data))
            opcode = first & 0x0F
            if opcode == 0x8:
                raise ConnectionError("inspector closed the connection")
            if opcode == 0x9:  # ping: answer with a masked pong echoing its payload (control payloads are <126 bytes)
                mask = os.urandom(4)
                self.sock.sendall(bytes([0x8A, 0x80 | len(data)]) + mask
                                  + bytes(b ^ mask[i % 4] for i, b in enumerate(data)))
                continue
            if opcode in (0x1, 0x2, 0x0):
                message += data
                if first & 0x80:
                    return message.decode("utf-8", errors="replace")


class Proxima:
    def __init__(self, host: str, port: int, password: str, timeout: float):
        self.ws = WebSocket(host, port, "/api", timeout)
        self.next_id = 0
        instances = self.request(3, "", [])
        if not instances:
            raise SystemExit("inspector has no instances (is it open in the game?)")
        instance = instances[0]
        digest = hashlib.sha256((password + instance["ConnectionId"]).encode()).digest()
        self.request(4, instance["InstanceId"], [base64.b64encode(digest).decode()])
        self.instance = instance

    def request(self, kind: int, cmd: str, args: list[str]):
        request_id = str(self.next_id)
        self.next_id += 1
        self.ws.send(json.dumps({"Id": request_id, "Type": kind, "Cmd": cmd, "Args": args}))
        while True:
            reply = json.loads(self.ws.receive())
            if reply.get("Id") != request_id:
                continue  # stream data or replies to earlier requests
            if reply.get("Error"):
                raise RuntimeError(reply["Error"])
            return reply.get("Data")

    def command(self, cmd: str, *args: str):
        return self.request(0, cmd, list(args))

    def lua(self, code: str) -> str:
        # Proxima's `lua` command only reports that the chunk ran, so the chunk logs its result with a unique marker
        # and the reply is read back from the inspector's LogStream (Unity log entries: LogString per entry).
        marker = f"PGR_RESULT_{os.getpid()}_{time.monotonic_ns()}:"
        wrapped = (f"local ok, result = xpcall(function() {code}\nend, debug.traceback) "
                   f"CS.UnityEngine.Debug.Log({lua_quote(marker)} .. (ok and 'ok:' or 'error:') .. tostring(result))")

        def match(entries):
            for entry in entries or []:
                text = entry.get("LogString", "") if isinstance(entry, dict) else ""
                if text.startswith(marker):
                    return text[len(marker):]
            return None

        stream_id = str(self.next_id)
        self.next_id += 1
        self.ws.send(json.dumps({"Id": stream_id, "Type": 1, "Cmd": "LogStream", "Args": []}))
        self.command("lua", wrapped)
        while True:
            reply = json.loads(self.ws.receive())
            if reply.get("Id") != stream_id:
                continue
            found = match(reply.get("Data"))
            if found is not None:
                self.ws.send(json.dumps({"Id": stream_id, "Type": 2, "Cmd": "LogStream", "Args": []}))
                status, _, value = found.partition(":")
                if status == "error":
                    raise RuntimeError(value)
                return value


def lua_quote(text: str) -> str:
    return "'" + text.replace("\\", "\\\\").replace("'", "\\'").replace("\n", "\\n").replace("\r", "\\r") + "'"


def reload_modules(proxima: Proxima, modules: list[str]) -> None:
    for module in modules:
        # XHotReload logs a load error and keeps the old module; a module never required yet has nothing to reload.
        print(proxima.lua(f"if not package.loaded[{lua_quote(module)}] then return 'not loaded yet (no-op): ' .. "
                          f"{lua_quote(module)} end XHotReload.Reload({lua_quote(module)}) "
                          f"return 'reloaded ' .. {lua_quote(module)}"))


def changed_modules(before: dict[str, float]) -> tuple[dict[str, float], list[str]]:
    """Snapshot mod sources; return the new snapshot and the Lua modules whose sources changed since `before`."""
    now = {}
    for path in MODS.rglob("*"):
        rel = path.relative_to(MODS)
        if path.is_file() and not rel.parts[0].startswith("_"):
            now[path] = path.stat().st_mtime
    modules = []
    for path, mtime in now.items():
        if before.get(path) == mtime:
            continue
        parts = path.relative_to(MODS).parts[1:]  # drop the mod directory
        if path.suffix in (".lua", ".diff") and parts and parts[0] != "Table":
            modules.append(Path(*parts).with_suffix("").as_posix())
    return now, modules


def watch(proxima: Proxima) -> None:
    state, _ = changed_modules({})
    print(f"watching {MODS} (Ctrl+C to stop)")
    while True:
        time.sleep(0.5)
        state, modules = changed_modules(state)
        if not modules:
            continue
        if not LUA_MODS_TOOL:
            raise SystemExit("watch needs PGR_LUA_MODS_TOOL=<path to pgr_lua_mods.py>; use `reload` after the launcher applies mods")
        build = subprocess.run([sys.executable, LUA_MODS_TOOL, "build"],
                               capture_output=True, text=True)
        if build.returncode != 0:
            print((build.stdout + build.stderr).strip().splitlines()[-1])
            continue
        reload_modules(proxima, sorted(set(modules)))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=7378)
    parser.add_argument("--password", default=os.environ.get("PROXIMA_PASSWORD", DEFAULT_PASSWORD))
    parser.add_argument("--timeout", type=float, default=30.0)
    sub = parser.add_subparsers(dest="command", required=True)
    lua = sub.add_parser("lua")
    lua.add_argument("code", nargs="?")
    lua.add_argument("-f", "--file", type=Path)
    sub.add_parser("reload").add_argument("modules", nargs="+")
    sub.add_parser("watch")
    raw = sub.add_parser("cmd")
    raw.add_argument("name")
    raw.add_argument("args", nargs="*")
    args = parser.parse_args()

    proxima = Proxima(args.host, args.port, args.password, args.timeout)
    if args.command == "lua":
        if bool(args.code) == bool(args.file):
            raise SystemExit("give Lua code or -f FILE")
        print(proxima.lua(args.file.read_text() if args.file else args.code))
    elif args.command == "reload":
        reload_modules(proxima, args.modules)
    elif args.command == "watch":
        watch(proxima)
    else:
        result = proxima.command(args.name, *args.args)
        print(result if isinstance(result, str) else json.dumps(result, indent=1, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (RuntimeError, ConnectionError, OSError) as error:  # game-side Lua errors and connection failures
        print(f"error: {error}", file=sys.stderr)
        raise SystemExit(1)
    except KeyboardInterrupt:
        raise SystemExit(130)
