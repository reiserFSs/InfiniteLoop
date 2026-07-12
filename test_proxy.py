import os
import sys
import unittest
from types import ModuleType, SimpleNamespace
from unittest.mock import patch

mitmproxy = ModuleType("mitmproxy")
mitmproxy.http = ModuleType("mitmproxy.http")
mitmproxy.http.HTTPFlow = object
mitmproxy.http.Response = SimpleNamespace(
    make=lambda status_code, content, headers: SimpleNamespace(
        status_code=status_code,
        content=content,
        headers=headers,
    )
)
mitmproxy.ctx = SimpleNamespace()
mitmproxy.proxy = ModuleType("mitmproxy.proxy")
mitmproxy.proxy.layer = ModuleType("mitmproxy.proxy.layer")
mitmproxy.proxy.layer.NextLayer = object
sys.modules["mitmproxy"] = mitmproxy
sys.modules["mitmproxy.http"] = mitmproxy.http
sys.modules["mitmproxy.proxy"] = mitmproxy.proxy
sys.modules["mitmproxy.proxy.layer"] = mitmproxy.proxy.layer

import proxy


class ProxyRoutingTests(unittest.TestCase):
    @staticmethod
    def flow(path: str, host: str = "prod-encdn-tx.kurogame.net"):
        request = SimpleNamespace(
            method="GET",
            pretty_url=f"http://{host}{path}",
            pretty_host=host,
            path=path,
            scheme="http",
            host=host,
            port=80,
            headers={},
        )
        return SimpleNamespace(request=request, response=None)

    def test_notice_html_stays_on_upstream_cdn(self):
        flow = self.flow("/prod/client/notice/html/current-notice.html?cache=1")

        with patch.dict(os.environ, {"ASCNET_PROXY_TARGET": "http://127.0.0.1:9"}, clear=False):
            proxy.request(flow)

        self.assertEqual("prod-encdn-tx.kurogame.net", flow.request.host)
        self.assertEqual(80, flow.request.port)
        self.assertNotIn("X-Forwarded-Host", flow.request.headers)

    def test_notice_metadata_still_routes_to_ascnet(self):
        flow = self.flow("/prod/client/notice/config/example/4.5.0/GameNotice.json")

        with patch.dict(os.environ, {"ASCNET_PROXY_TARGET": "http://127.0.0.1:9"}, clear=False):
            proxy.request(flow)

        self.assertEqual("127.0.0.1", flow.request.host)
        self.assertEqual(9, flow.request.port)
        self.assertEqual("prod-encdn-tx.kurogame.net", flow.request.headers["X-Forwarded-Host"])

    def test_tw_config_routes_to_ascnet(self):
        flow = self.flow(
            "/prod/client/config/PQQdKhfClWoBi3Iq/com.kurogame.punishing.grayraven.tw/4.5.0/standalone/config.tab",
            "prod-twcdn-tx.kurogame.net",
        )

        with patch.dict(os.environ, {"ASCNET_PROXY_TARGET": "http://127.0.0.1:9"}, clear=False):
            proxy.request(flow)

        self.assertEqual("127.0.0.1", flow.request.host)
        self.assertEqual(9, flow.request.port)
        self.assertEqual("prod-twcdn-tx.kurogame.net", flow.request.headers["X-Forwarded-Host"])

    def test_tw_feedback_with_query_is_sunk(self):
        flow = self.flow("/feedback?event=login", "prod.twzspnslog.kurogame.com")

        proxy.request(flow)

        self.assertEqual(200, flow.response.status_code)
        self.assertEqual(b"OK", flow.response.content)
        self.assertEqual("prod.twzspnslog.kurogame.com", flow.request.host)


if __name__ == "__main__":
    unittest.main()
