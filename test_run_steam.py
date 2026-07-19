import errno
import importlib.abc
from http.server import BaseHTTPRequestHandler, HTTPServer
import io
import multiprocessing
import os
from pathlib import Path
import queue
import runpy
import socket
import sys
import threading
import tempfile
from types import ModuleType, SimpleNamespace
import unittest
from unittest.mock import patch

import run_steam


class FakeMsvcrt(ModuleType):
    LK_NBLCK = 1
    LK_UNLCK = 2

    def __init__(self, failures=0):
        super().__init__("msvcrt")
        self.failures = failures
        self.calls = []

    def locking(self, fd, mode, length):
        self.calls.append((fd, mode, length))
        if mode == self.LK_NBLCK and self.failures:
            self.failures -= 1
            raise OSError(errno.EACCES, "lock is held")


class BlockFcntl(importlib.abc.MetaPathFinder):
    def find_spec(self, fullname, path=None, target=None):
        if fullname == "fcntl":
            raise ModuleNotFoundError("No module named 'fcntl'", name="fcntl")
        return None


def hold_build_lock(root, entered, release):
    run_steam.ROOT = Path(root)
    with run_steam.serialized_build_lock():
        entered.put(os.getpid())
        release.get(timeout=5)


class GateFallbackUsernameTests(unittest.TestCase):
    def args(self, fallback, *, no_ensure_account):
        return SimpleNamespace(
            gate_fallback_username=fallback,
            no_ensure_account=no_ensure_account,
            ascnet_username="test",
        )

    def test_normal_run_defaults_to_ascnet_username(self):
        self.assertEqual("test", run_steam.gate_fallback_username(self.args(None, no_ensure_account=False)))

    def test_no_ensure_account_disables_omitted_fallback(self):
        self.assertIsNone(run_steam.gate_fallback_username(self.args(None, no_ensure_account=True)))

    def test_explicit_fallback_wins_under_no_ensure_account(self):
        for fallback in ("fallback", ""):
            with self.subTest(fallback=fallback):
                self.assertEqual(fallback, run_steam.gate_fallback_username(self.args(fallback, no_ensure_account=True)))


class RunnerOwnedHttpTests(unittest.TestCase):
    def test_post_json_ignores_inherited_http_proxy(self):
        class Handler(BaseHTTPRequestHandler):
            def do_POST(self):
                self.rfile.read(int(self.headers["Content-Length"]))
                body = b'{"ok": true}'
                self.send_response(200)
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

            def log_message(self, format, *args):
                pass

        with HTTPServer(("127.0.0.1", 0), Handler) as server, socket.socket() as dead_proxy:
            dead_proxy.bind(("127.0.0.1", 0))
            thread = threading.Thread(target=server.serve_forever)
            thread.start()
            env = os.environ.copy()
            proxy = f"http://127.0.0.1:{dead_proxy.getsockname()[1]}"
            env.update(HTTP_PROXY=proxy, http_proxy=proxy)
            env.pop("NO_PROXY", None)
            env.pop("no_proxy", None)
            try:
                with patch.dict(os.environ, env, clear=True):
                    self.assertEqual(
                        {"ok": True},
                        run_steam.post_json(f"http://127.0.0.1:{server.server_port}/", {}, timeout=1),
                    )
            finally:
                server.shutdown()
                thread.join()


class WindowsBuildLockTests(unittest.TestCase):
    def test_help_imports_when_fcntl_is_unavailable(self):
        fake_msvcrt = FakeMsvcrt()
        blocker = BlockFcntl()
        saved_fcntl = sys.modules.pop("fcntl", None)
        sys.meta_path.insert(0, blocker)
        try:
            with (
                patch.object(sys, "platform", "win32"),
                patch.dict(sys.modules, {"msvcrt": fake_msvcrt}),
                patch.object(sys, "argv", ["run_steam.py", "--help"]),
                patch("sys.stdout", new_callable=io.StringIO) as stdout,
            ):
                with self.assertRaises(SystemExit) as exit_context:
                    runpy.run_path("run_steam.py", run_name="__main__")
            self.assertEqual(0, exit_context.exception.code)
            self.assertIn("--with-mongo", stdout.getvalue())
        finally:
            sys.meta_path.remove(blocker)
            if saved_fcntl is not None:
                sys.modules["fcntl"] = saved_fcntl

    def test_windows_lock_retries_and_unlocks(self):
        fake_msvcrt = FakeMsvcrt(failures=1)
        with tempfile.TemporaryDirectory() as root:
            with (
                patch.object(run_steam.sys, "platform", "win32"),
                patch.object(run_steam, "msvcrt", fake_msvcrt, create=True),
                patch.object(run_steam, "ROOT", Path(root)),
                patch.object(run_steam.time, "sleep") as sleep,
            ):
                with run_steam.serialized_build_lock():
                    lock_path = Path(root) / ".runtime" / "ascnet-build.lock"
                    self.assertEqual(b"\0", lock_path.read_bytes())

        self.assertEqual([fake_msvcrt.LK_NBLCK, fake_msvcrt.LK_NBLCK, fake_msvcrt.LK_UNLCK], [call[1] for call in fake_msvcrt.calls])
        self.assertTrue(all(call[2] == 1 for call in fake_msvcrt.calls))
        sleep.assert_called_once_with(0.05)

    def test_windows_lock_unlocks_after_exception(self):
        fake_msvcrt = FakeMsvcrt()
        with tempfile.TemporaryDirectory() as root:
            with (
                patch.object(run_steam.sys, "platform", "win32"),
                patch.object(run_steam, "msvcrt", fake_msvcrt, create=True),
                patch.object(run_steam, "ROOT", Path(root)),
            ):
                with self.assertRaisesRegex(RuntimeError, "build failed"):
                    with run_steam.serialized_build_lock():
                        raise RuntimeError("build failed")

        self.assertEqual([fake_msvcrt.LK_NBLCK, fake_msvcrt.LK_UNLCK], [call[1] for call in fake_msvcrt.calls])


class UnixBuildLockTests(unittest.TestCase):
    def test_build_lock_excludes_another_process(self):
        context = multiprocessing.get_context("spawn")
        entered = context.Queue()
        release_first = context.Queue()
        release_second = context.Queue()
        with tempfile.TemporaryDirectory() as root:
            first = context.Process(target=hold_build_lock, args=(root, entered, release_first))
            second = context.Process(target=hold_build_lock, args=(root, entered, release_second))
            first.start()
            self.assertEqual(first.pid, entered.get(timeout=5))
            second.start()
            with self.assertRaises(queue.Empty):
                entered.get(timeout=0.3)
            release_first.put(None)
            self.assertEqual(second.pid, entered.get(timeout=5))
            release_second.put(None)
            first.join(timeout=5)
            second.join(timeout=5)
            self.assertEqual(0, first.exitcode)
            self.assertEqual(0, second.exitcode)


if __name__ == "__main__":
    unittest.main()
