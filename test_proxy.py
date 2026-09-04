import os
import sys
from pathlib import Path
import tempfile
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

    def test_flow_logs_omit_credentials_without_changing_routing(self):
        path = "/prod/client/notice/html/current-notice.html"
        query = (
            "autoToken=synthetic-auto&oauthCode=synthetic-oauth"
            "&%74oKeN=synthetic-encoded&PaSsWoRd=synthetic-password"
            "&token=synthetic-first&token=synthetic-second"
            "&futureCredential=synthetic-unknown&cache=synthetic-cache"
        )
        flow = self.flow(f"{path}?{query}")
        flow.request.pretty_url = (
            f"http://synthetic-user:synthetic-userinfo@{flow.request.host}"
            f"{flow.request.path}#synthetic-fragment"
        )
        original = vars(flow.request).copy()
        original["headers"] = flow.request.headers.copy()
        with tempfile.TemporaryDirectory() as root:
            log_path = Path(root) / "flows.log"
            with patch.dict(os.environ, {"ASCNET_PROXY_LOG": str(log_path)}):
                proxy.request(flow)
                flow.response = SimpleNamespace(status_code=204)
                proxy.response(flow)
            logged = log_path.read_text(encoding="utf-8")
        self.assertNotIn("synthetic-", logged)
        self.assertNotIn("?", logged)
        self.assertNotIn("@", logged)
        self.assertIn(f"REQ GET http://{flow.request.host}{path} -> -", logged)
        self.assertIn(f"RSP GET http://{flow.request.host}{path} -> 204", logged)
        self.assertEqual(original, vars(flow.request))


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

    def test_pgr_game_popup_notice_routes_to_ascnet(self):
        flow = self.flow(
            "/prod/client/notice/config/jmpyKTGE5zwaZ0O4/com.kurogame.punishing.grayraven.en/4.7.0/PopUpPicNotice.json",
            "prod-encdn-ak.pgr-game.com",
        )

        with patch.dict(os.environ, {"ASCNET_PROXY_TARGET": "http://127.0.0.1:9"}, clear=False):
            proxy.request(flow)

        self.assertEqual("127.0.0.1", flow.request.host)
        self.assertEqual(9, flow.request.port)
        self.assertEqual("prod-encdn-ak.pgr-game.com", flow.request.headers["X-Forwarded-Host"])

    def test_pgr_game_banner_asset_stays_upstream(self):
        flow = self.flow(
            "/prod/client/notice/pic/home-lobby-banner.png",
            "prod-encdn-ak.pgr-game.com",
        )

        with patch.dict(os.environ, {"ASCNET_PROXY_TARGET": "http://127.0.0.1:9"}, clear=False):
            proxy.request(flow)

        self.assertEqual("prod-encdn-ak.pgr-game.com", flow.request.host)
        self.assertEqual(80, flow.request.port)
        self.assertNotIn("X-Forwarded-Host", flow.request.headers)

    def test_pgr_game_scroll_banner_metadata_stays_upstream(self):
        flow = self.flow(
            "/prod/client/notice/config/jmpyKTGE5zwaZ0O4/com.kurogame.punishing.grayraven.en/4.7.0/ScrollPicNotice.json",
            "prod-encdn-ak.pgr-game.com",
        )

        with patch.dict(os.environ, {"ASCNET_PROXY_TARGET": "http://127.0.0.1:9"}, clear=False):
            proxy.request(flow)

        self.assertEqual("prod-encdn-ak.pgr-game.com", flow.request.host)
        self.assertEqual(80, flow.request.port)
        self.assertNotIn("X-Forwarded-Host", flow.request.headers)




    def test_tw_config_passes_through_upstream(self):
        flow = self.flow(
            "/prod/client/config/PQQdKhfClWoBi3Iq/com.kurogame.punishing.grayraven.tw/4.5.0/standalone/config.tab",
            "prod-twcdn-tx.kurogame.net",
        )

        with patch.dict(os.environ, {"ASCNET_PROXY_TARGET": "http://127.0.0.1:9"}, clear=False):
            proxy.request(flow)

        self.assertEqual("prod-twcdn-tx.kurogame.net", flow.request.host)
        self.assertEqual(80, flow.request.port)
        self.assertNotIn("X-Forwarded-Host", flow.request.headers)

    def test_tw_config_response_rewrites_login_endpoints_only(self):
        flow = self.flow(
            "/prod/client/config/Pxk4VQxGusWDqGN5/com.kurogame.punishing.grayraven.tw/4.7.0/standalone/config.tab",
            "prod-twcdn-tx.kurogame.net",
        )
        flow.response = SimpleNamespace(
            status_code=200,
            content=(
                "Key\tType\tValue\n"
                "ApplicationVersion\tstring\t4.7.0\n"
                "DocumentVersion\tstring\t4.7.12\n"
                "Channel\tint\t5\n"
                "PrimaryCdns\tstring\thttp://prod-twcdn-ak.pgr-game.com/prod\n"
                "ServerListStr\tstring\t繁體中文服#http://175.97.184.50:55556/api/Login/Login\n"
                "ChannelServerListStr\tstring\tdefault#繁體中文服#http://175.97.184.50:55556/api/Login/Login\n"
            ).encode("utf-8"),
        )

        with patch.dict(os.environ, {"ASCNET_PROXY_TARGET": "http://127.0.0.1:8080"}, clear=False):
            proxy.response(flow)

        text = flow.response.content.decode("utf-8")
        self.assertIn("ServerListStr\tstring\t繁體中文服#http://127.0.0.1:8080/api/Login/Login\n", text)
        self.assertIn("ChannelServerListStr\tstring\tdefault#繁體中文服#http://127.0.0.1:8080/api/Login/Login\n", text)
        self.assertIn("DocumentVersion\tstring\t4.7.12\n", text)
        self.assertIn("Channel\tint\t5\n", text)
        self.assertIn("PrimaryCdns\tstring\thttp://prod-twcdn-ak.pgr-game.com/prod\n", text)

    def test_tw_feedback_with_query_is_sunk(self):
        flow = self.flow("/feedback?event=login", "prod.twzspnslog.kurogame.com")

        proxy.request(flow)

        self.assertEqual(200, flow.response.status_code)
        self.assertEqual(b"OK", flow.response.content)
        self.assertEqual("prod.twzspnslog.kurogame.com", flow.request.host)


if __name__ == "__main__":
    unittest.main()
