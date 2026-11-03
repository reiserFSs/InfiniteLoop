"""Checks for pgr_bundles: derivation rule (synthetic) and, when the installed client is present, index-free resolution
against the real index and the verify-live comparison against a fake Proxima. Run: python3 -m unittest Scripts.test_pgr_bundles"""
import re
import unittest

from Scripts import pgr_bundles as pb


class ClaimsTests(unittest.TestCase):
    def test_single_asset_and_folder_claims(self):
        self.assertEqual(pb.container_claims(["assets/a/b/c.png"]),
                         [(0, "assets/a/b/c.png.ab"), (0, "assets/a/b.ab"), (1, "assets/a.ab")])
        self.assertEqual(pb.container_claims(["assets/a/b/x.lua", "assets/a/b/c/y.lua"]), [(0, "assets/a/b.ab"), (1, "assets/a.ab")])
        self.assertIn((0, "matrixmanifest"), pb.container_claims([pb.MANIFEST_ASSET]))


class InstalledClientTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        try:
            cls.real = pb.Resolver()
            if not cls.real.indexes or not pb.CACHE.exists():
                raise FileNotFoundError
        except (OSError, KeyError):
            raise unittest.SkipTest("installed 4.8 client or scan cache (pgr_bundles.py scan) not available")
        cls.scanned = pb.Resolver(use_index=False)

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
