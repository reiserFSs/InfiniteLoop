"""Checks for restore_document_patch: the CDN URL scheme and fetch() (resume, sha1 gate). Run: python3 -m unittest Scripts.test_restore_document_patch"""
import hashlib
import http.server
import tempfile
import threading
import unittest
from pathlib import Path

from Scripts import restore_document_patch as rd


class Handler(http.server.BaseHTTPRequestHandler):
    body = bytes(range(256)) * 100

    def do_GET(self):
        start = int(self.headers["Range"].split("=")[1].rstrip("-")) if "Range" in self.headers else 0
        self.send_response(206 if start else 200)
        self.end_headers()
        self.wfile.write(self.body[start:])

    def log_message(self, *args):
        pass


class RestoreTests(unittest.TestCase):
    def test_url_scheme_matches_client(self):
        r = rd.Restorer.__new__(rd.Restorer)
        r.version_path, r.client2cdn, r.key, r.cdns = {"1739029": {"version": "4.9.0", "key": "K"}}, {"1739029": {"KuroGameIdentifier": "id"}}, "K", ["http://cdn/prod"]
        self.assertEqual(r.urls("1739029", "matrix", "f.uab"), ["http://cdn/prod/client/patch/K/id/4.9.0/standalone/Release/1739029/matrix/f.uab"])

    def test_fetch_resumes_and_rejects_bad_sha1(self):
        server = http.server.HTTPServer(("127.0.0.1", 0), Handler)
        threading.Thread(target=server.serve_forever, daemon=True).start()
        url = [f"http://127.0.0.1:{server.server_port}/x"]
        body = Handler.body
        with tempfile.TemporaryDirectory() as tmp:
            dest = Path(tmp) / "x"
            (Path(tmp) / "x.part").write_bytes(body[:1000])  # interrupted earlier
            rd.fetch(url, dest, len(body), hashlib.sha1(body).hexdigest())
            self.assertEqual(dest.read_bytes(), body)
            rd.RETRIES = 1
            with self.assertRaises(RuntimeError):
                rd.fetch(url, Path(tmp) / "y", len(body), "0" * 40)
            self.assertFalse((Path(tmp) / "y").exists())
        server.shutdown()


if __name__ == "__main__":
    unittest.main()
