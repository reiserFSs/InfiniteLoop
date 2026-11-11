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

    def test_kr_jp_config_pass_through_and_rewrite_every_server_entry(self):
        for host, key, pkg, label in [
            ("prod-krcdn-volcdn.kurogame.net", "jqlCmYRizwT76uvX", "kr", "한국정식서버"),
            ("prod-jpcdn-ak.kurogame.net", "xZx901LhZhT6G2HG", "jp", "日本サーバー"),
            # Hard-coded PrimaryCdns in the regional clients are *.pgr-game.com.
            ("prod-krcdn-ak.pgr-game.com", "jqlCmYRizwT76uvX", "kr", "한국정식서버"),
            ("prod-jpcdn-ak.pgr-game.com", "xZx901LhZhT6G2HG", "jp", "日本サーバー"),
        ]:
            flow = self.flow(
                f"/prod/client/config/{key}/com.kurogame.punishing.grayraven.{pkg}/4.8.0/standalone/config.tab", host)
            with patch.dict(os.environ, {"ASCNET_PROXY_TARGET": "http://127.0.0.1:8080"}, clear=False):
                proxy.request(flow)
                self.assertEqual(host, flow.request.host)
                self.assertNotIn("X-Forwarded-Host", flow.request.headers)
                flow.response = SimpleNamespace(status_code=200, content=(
                    "DocumentVersion\tstring\t4.8.12\n"
                    f"ServerListStr\tstring\t{label}#http://1.2.3.4:1/api/Login/Login|b#http://5.6.7.8:2/api/Login/Login\n"
                    f"ChannelServerListStr\tstring\tdefault#{label}#http://1.2.3.4:1/api/Login/Login\n").encode("utf-8"))
                proxy.response(flow)
            text = flow.response.content.decode("utf-8")
            self.assertIn(f"ServerListStr\tstring\t{label}#http://127.0.0.1:8080/api/Login/Login|b#http://127.0.0.1:8080/api/Login/Login\n", text)
            self.assertIn(f"ChannelServerListStr\tstring\tdefault#{label}#http://127.0.0.1:8080/api/Login/Login\n", text)
            self.assertIn("DocumentVersion\tstring\t4.8.12\n", text)

    def test_en_config_on_pgr_game_primary_cdn_routes_to_ascnet(self):
        flow = self.flow(
            "/prod/client/config/YHcyljDAVMYA6tK8/com.kurogame.punishing.grayraven.en/4.8.0/standalone/config.tab",
            "prod-encdn-ak.pgr-game.com")
        with patch.dict(os.environ, {"ASCNET_PROXY_TARGET": "http://127.0.0.1:9"}, clear=False):
            proxy.request(flow)
        self.assertEqual(9, flow.request.port)

    def test_every_krsdk_bin_api_host_routes_to_ascnet(self):
        # KR_SDKAPI_URL / KR_PlayerAddress(Second) are identical in the EN, KR and JP KRSDK.bin.
        for host in ("sdkapi.kurogame-service.com", "sdkapi2.kurogame-service.com", "sdkapi.kurogame-service.xyz"):
            flow = self.flow("/sdkcom/v2/sys/conf.lg", host)
            with patch.dict(os.environ, {"ASCNET_PROXY_TARGET": "http://127.0.0.1:9"}, clear=False):
                proxy.request(flow)
            self.assertEqual(9, flow.request.port, host)

    def test_kr_jp_notice_metadata_routes_to_ascnet(self):
        for host, key, pkg in [("prod-krcdn-volcdn.kurogame.net", "jqlCmYRizwT76uvX", "kr"), ("prod-jpcdn-aliyun.kurogame.net", "xZx901LhZhT6G2HG", "jp")]:
            flow = self.flow(
                f"/prod/client/notice/{key}/com.kurogame.punishing.grayraven.{pkg}/4.8.0/standalone/LoginNotice.json", host)
            with patch.dict(os.environ, {"ASCNET_PROXY_TARGET": "http://127.0.0.1:9"}, clear=False):
                proxy.request(flow)
            self.assertEqual(9, flow.request.port)
            self.assertEqual(host, flow.request.headers["X-Forwarded-Host"])

    def test_tw_feedback_with_query_is_sunk(self):
        flow = self.flow("/feedback?event=login", "prod.twzspnslog.kurogame.com")

        proxy.request(flow)

        self.assertEqual(200, flow.response.status_code)
        self.assertEqual(b"OK", flow.response.content)
        self.assertEqual("prod.twzspnslog.kurogame.com", flow.request.host)

    def test_pgr_game_feedback_host_is_sunk(self):
        flow = self.flow("/feedback", "prod.twzspnslog.pgr-game.com")

        proxy.request(flow)

        self.assertEqual(200, flow.response.status_code)

    def test_cn_config_preserves_metadata_and_rewrites_every_gate(self):
        for host in ("prod-zspns-txcdn.kurogame.com", "prod-zspnsalicdn.kurogame.com"):
            with self.subTest(host=host):
                flow = self.flow("/prod/client/config/key/com.kurogame.haru.kuro/4.8.0/standalone/config.tab", host)
                body = ("DocumentVersion\tstring\t4.8.12\r\n"
                        "ServerListStr\tstring\t星火服#https://gate.example/api/Login/Login\r\n"
                        "ChannelServerListStr\tstring\t18#星火服#https://gate.example/api/Login/Login;http://backup.example/api/Login/Login|19#星火服#http://another.example/api/Login/Login?x=1\r\n")
                with patch.dict(os.environ, {"ASCNET_PROXY_TARGET": "http://127.0.0.1:8080"}):
                    proxy.request(flow)
                    self.assertEqual(host, flow.request.host)
                    flow.response = SimpleNamespace(status_code=200, content=body.encode(), headers={})
                    proxy.response(flow)
                result = flow.response.content.decode()
                self.assertIn("DocumentVersion\tstring\t4.8.12\r\n", result)
                self.assertEqual(4, result.count("http://127.0.0.1:8080/api/Login/Login-cn"))
                self.assertNotIn("?", result)
                gate_url = result.split("ServerListStr\tstring\t", 1)[1].split("\r\n", 1)[0].split("#")[-1]
                self.assertEqual("http://127.0.0.1:8080/api/Login/Login-cn?loginType=5&userId=1&token=test", gate_url + "?loginType=5&userId=1&token=test")
                self.assertNotIn("gate.example", result)

    def test_cn_patch_and_agreement_stay_upstream(self):
        for host, path in (("prod-zspns-txcdn.kurogame.com", "/prod/client/patch/key/com.kurogame.haru.kuro/4.8.0/standalone/4.8.12/launch/index"),
                           ("pro-cdn-sdk.kurogame.com", "/pro/G148/19/agreement.json?pkgid=A1393")):
            flow = self.flow(path, host)
            proxy.request(flow)
            self.assertEqual(host, flow.request.host)
            self.assertIsNone(flow.response)

    def test_cn_sdk_routes_and_telemetry_is_sunk(self):
        gate = self.flow("/api/Login/Login-cn?loginType=5&userId=1&token=test", "gate.example")
        with patch.dict(os.environ, {"ASCNET_PROXY_TARGET": "http://127.0.0.1:8080"}):
            proxy.request(gate)
        self.assertEqual("127.0.0.1", gate.request.host)
        self.assertEqual("/api/Login/Login-cn?loginType=5&userId=1&token=test", gate.request.path)
        flow = self.flow("/sdkcom/v2/sys/conf.lg", "sdkapi.kurogame.com")
        proxy.request(flow)
        self.assertEqual("sdkapi.kurogame.com", flow.request.headers["X-Forwarded-Host"])
        for host, path in (("prod-zspnslog.zspms-game.com", "/feedback"), ("sdkapi.kurogame.com", "/ad-service/v1/sendEvent")):
            flow = self.flow(path, host)
            proxy.request(flow)
            self.assertEqual(200, flow.response.status_code)

    def test_every_cn_sdk_host_routes_to_ascnet_and_sinks_telemetry(self):
        for host in ("sdkapi.kurogame.com", "sdkapi2.kurogame.com", "sdkapi3.kurogame.com", "sdkapi.kurogame.xyz"):
            with self.subTest(host=host):
                flow = self.flow("/sdkcom/v2/login/accLogin.lg", host)
                with patch.dict(os.environ, {"ASCNET_PROXY_TARGET": "http://127.0.0.1:8080"}):
                    proxy.request(flow)
                self.assertEqual("127.0.0.1", flow.request.host)
                self.assertEqual(host, flow.request.headers["X-Forwarded-Host"])
                flow = self.flow("/ad-service/v1/sendEvent", host)
                proxy.request(flow)
                self.assertEqual(200, flow.response.status_code)
        flow = self.flow("/sdk/trade/v1/order/query", "sdkapi2.kurogame.com")
        proxy.request(flow)
        self.assertEqual("sdkapi2.kurogame.com", flow.request.host)


if __name__ == "__main__":
    unittest.main()
