"""Checks for pgr_bundles: derivation rule (synthetic) and, when the installed client is present, index-free resolution
against the real index and the verify-live comparison against a fake Proxima. Run: python3 -m unittest Scripts.test_pgr_bundles"""
import os
import re
import struct
import tempfile
import unittest
from pathlib import Path

from Scripts import pgr_bundles as pb


class ClaimsTests(unittest.TestCase):
    def test_single_asset_and_folder_claims(self):
        self.assertEqual(pb.container_claims(["assets/a/b/c.png"]),
                         [(0, "assets/a/b/c.png.ab"), (0, "assets/a/b.ab"), (1, "assets/a.ab")])
        self.assertEqual(pb.container_claims(["assets/a/b/x.lua", "assets/a/b/c/y.lua"]), [(0, "assets/a/b.ab"), (1, "assets/a.ab")])
        self.assertIn((0, "matrixmanifest"), pb.container_claims([pb.MANIFEST_ASSET]))


def synthetic_bundle(data: bytes, prefixed: bool) -> bytes:
    """Minimal uncompressed UnityFS holding one node; 4.9 files carry 32 bytes of HARUINKUROGAME + zeros in front."""
    directory = bytes(16) + struct.pack(">I", 1) + struct.pack(">IIH", len(data), len(data), 0)
    directory += struct.pack(">I", 1) + struct.pack(">QQI", 0, len(data), 4) + b"CAB-x\0"
    head = b"UnityFS\0" + struct.pack(">I", 8) + b"5.x.x\0" + b"2022.3.52f1c1\0"
    size = len(head) + 8 + 12 + len(directory) + len(data)
    body = head + struct.pack(">QIII", size, len(directory), len(directory), 0) + directory + data
    return (pb.PREFIX_MAGIC.ljust(pb.PREFIX_LEN, b"\0") if prefixed else b"") + body


class PrefixTests(unittest.TestCase):
    def test_prefix_and_plain_decode_identically(self):
        plain, prefixed = synthetic_bundle(b"hello", False), synthetic_bundle(b"hello", True)
        self.assertEqual(len(prefixed), len(plain) + pb.PREFIX_LEN)
        self.assertEqual(pb.strip_prefix(prefixed), plain)
        self.assertEqual(pb.strip_prefix(plain), plain)
        for blob in (plain, prefixed):
            self.assertEqual(pb.payload(blob), b"hello")
            with tempfile.NamedTemporaryFile() as f:
                f.write(blob)
                f.flush()
                self.assertEqual(pb.payload(Path(f.name)), b"hello")

    def test_wrong_declared_size_is_rejected(self):
        with self.assertRaises(ValueError):
            pb.payload(synthetic_bundle(b"hello", True) + b"x")

    def test_v49_index_layout(self):
        # [{file: [name, sha1, size, svn, [bases], 0]}, nil, nil, [], [], []] as the 4.9 index TextAsset carries it
        table = b"\x96\xde\x00\x02" + b"\xa5a.uab\x96\xa0\xa3sha\x05\xcd\x07\xd0\x90\x00" \
            + b"\xa5b.acb\x96\xa4A/b.\xa3shb\x01\x01\x90\x00" + b"\xc0\xc0\x90\x90\x90"
        with tempfile.NamedTemporaryFile() as f:
            f.write(synthetic_bundle(b"\0\0\0" + table, False))
            f.flush()
            self.assertEqual(pb.read_file_table(Path(f.name))["files"]["a.uab"], ["", "sha", 5, 2000, [], 0])
            self.assertEqual(pb.read_index_file(Path(f.name)), {"A/b.": ["b.acb", "shb", 1]})


def _installed():
    """(install, cache) from PGR_BUNDLES_INSTALL / PGR_BUNDLES_CACHE, else the native-starter manifest install + default cache."""
    return (Path(os.environ["PGR_BUNDLES_INSTALL"]) if os.environ.get("PGR_BUNDLES_INSTALL") else None,
            Path(os.environ.get("PGR_BUNDLES_CACHE", pb.CACHE)))


class Installed49Tests(unittest.TestCase):
    """4.9: physical-file index + manifest; needs an installed 4.9 client and its scan cache."""

    @classmethod
    def setUpClass(cls):
        install, cache = _installed()
        try:
            cls.r = pb.Resolver(install, cache)
            if not cls.r.file_tables() or not cache.exists():
                raise FileNotFoundError
        except (OSError, KeyError):
            raise unittest.SkipTest("installed 4.9 client or scan cache (pgr_bundles.py scan) not available")

    def test_lua_bundle_verified_and_in_manifest(self):
        path = self.r.resolve("assets/temp/lua/matrix.ab")
        entry = self.r.file_entry(path)
        self.assertIsNotNone(entry)
        self.assertEqual(path.stat().st_size, entry[2])
        layout, manifest = pb._manifest(self.r)
        self.assertEqual(layout, "4.9")
        self.assertIn("assets/temp/lua/matrix/xmanager/xluavideomanager.lua.bytes", manifest[path.name])
        self.assertEqual(self.r.find_asset("assets/temp/lua/matrix/xmanager/xluavideomanager.lua.bytes")[0], path)


class InstalledClientTests(unittest.TestCase):
    """4.8: index-free resolution against the real name -> file index."""

    @classmethod
    def setUpClass(cls):
        install, cache = _installed()
        try:
            cls.real = pb.Resolver(install, cache)
            if cls.real.entry("assets/temp/lua/matrix.ab") is None or not cache.exists():
                raise FileNotFoundError
        except (OSError, KeyError):
            raise unittest.SkipTest("installed 4.8 client (set PGR_BUNDLES_INSTALL/PGR_BUNDLES_CACHE) not available")
        cls.scanned = pb.Resolver(install, cache, use_index=False)

    def test_scan_matches_index(self):
        for name in ("assets/temp/lua/matrix.ab", "assets/temp/bytes/share/statussyncfight/quest.ab",
                     "assets/product/ui/prefab/uitip.prefab.ab", "matrixmanifest"):
            self.assertEqual(self.scanned.resolve(name), self.real.resolve(name), name)
            self.assertEqual(self.scanned.resolve(name, scope="resource"), self.real.resolve(name, scope="resource"), name)

    def test_container_only_matches_index(self):
        for name in ("assets/temp/lua/matrix.ab", "assets/temp/bytes/share/statussyncfight/quest.ab"):
            keys = self.scanned.candidates(name, names=False)
            self.assertEqual(self.scanned._path(keys[0]), self.real.resolve(name))

    def test_launch_tree(self):
        for name in ("assets/temp/lua/launch.ab", "assets/launch/config.ab", "launchmanifest"):
            self.assertEqual(self.scanned.resolve(name), self.real.resolve(name), name)
            self.assertIn("/launch/", str(self.scanned.resolve(name)))

    def test_find_asset_and_fake_live(self):
        path, logical = self.scanned.find_asset("assets/temp/lua/matrix/xmanager/xluavideomanager.lua.bytes")
        self.assertEqual((path, logical), (self.real.resolve("assets/temp/lua/matrix.ab"), "assets/temp/lua/matrix.ab"))

        class FakeProxima:  # the client's UrlTable (original-case asset -> bundle), served in chunks
            def lua(self_, code):
                skip, take = map(int, re.search(r"local skip, take = (\d+), (\d+)", code).groups())
                rows = [f"{a.upper()}\t{b}" for a, b in sorted(self.scanned.asset_bundles().items())][skip:skip + take]
                return "\n".join(rows)
        result = pb.verify_live(FakeProxima(), self.scanned, chunk=100000, log=lambda m: None)["stats"]
        self.assertEqual((result["bundle differs"], result["client-only"], result["scan-only"]), (0, 0, 0))

if __name__ == "__main__":
    unittest.main()
