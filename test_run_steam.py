import errno
import importlib.abc
import io
import multiprocessing
import os
from pathlib import Path
import queue
import runpy
import sys
import tempfile
from types import ModuleType
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
