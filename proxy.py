import os
from urllib.parse import urlparse, urlunparse
from mitmproxy import http
from mitmproxy import ctx
from mitmproxy.proxy import layer

def load(loader):
    # ctx.options.web_open_browser = False
    # We change the connection strategy to lazy so that next_layer happens before we actually connect upstream.
    ctx.options.connection_strategy = "lazy"
    ctx.options.upstream_cert = False
    ctx.options.ssl_insecure = True
    ctx.options.ignore_hosts = [
        r".*sdk-prod-cdn-aws\.kurogame-service\.(com|xyz).*",
        r".*qcloud-sg-datareceiver\.kurogame\.xyz.*",
        r".*mp-gb-sdklog\.kurogames\.net.*",
        r".*events\.appsflyer\.com.*",
        r"pgr\.kurogame\.net:443",
    ]


def _normalise_connect_host(host):
    if host in {None, "", "*", "0.0.0.0", "::", "[::]"}:
        return "127.0.0.1"

    return host

def _is_local_wildcard_host(host):
    return host in {"*", "0.0.0.0", "::", "[::]"}


def _ascnet_target():
    raw_target = os.environ.get("ASCNET_PROXY_TARGET", "http://127.0.0.1:8080").strip()
    if "://" not in raw_target:
        raw_target = f"http://{raw_target}"

    parsed = urlparse(raw_target)
    scheme = parsed.scheme or "http"
    host = _normalise_connect_host(parsed.hostname)
    port = parsed.port or (443 if scheme == "https" else 80)
    return scheme, host, port


def _flow_log_path():
    return os.environ.get("ASCNET_PROXY_LOG")


def _diagnostic_url(url):
    parsed = urlparse(url)
    return urlunparse(parsed._replace(
        netloc=parsed.netloc.rsplit("@", 1)[-1], params="", query="", fragment="",
    ))


def _log_flow(prefix, flow):
    path = _flow_log_path()
    if not path:
        return
    status = getattr(flow.response, "status_code", "-") if getattr(flow, "response", None) else "-"
    line = f"{prefix} {flow.request.method} {_diagnostic_url(flow.request.pretty_url)} -> {status}\n"
    with open(path, "a", encoding="utf-8") as handle:
        handle.write(line)


# Region CDN host prefixes. A new region (e.g. JP) is added here only.
REGION_CDN_PREFIXES = ("prod-encdn-", "prod-twcdn-", "prod-krcdn-", "prod-jpcdn-")
# Regions whose config.tab passes through to the real CDN (local AscNet only reproduces EN's).
AUTHORITATIVE_CONFIG_PREFIXES = ("prod-twcdn-", "prod-krcdn-", "prod-jpcdn-")
# CN (com.kurogame.haru.kuro) uses its own CDN hosts and also passes through; its gates map to the isolated Login-cn route.
CN_CONFIG_HOSTS = {"prod-zspns-txcdn.kurogame.com", "prod-zspnsalicdn.kurogame.com", "prod-zspnstxcdn.kurogame.com"}
CN_GATE_PATH = "/api/Login/Login-cn"
# KRSDKConfig.json KR_Host / KR_HostStandby / KR_SDKAPI_URL of the CN client (same set as lucia's cn_sdk_routing tests).
CN_SDK_HOSTS = {"sdkapi.kurogame.com", "sdkapi2.kurogame.com", "sdkapi3.kurogame.com", "sdkapi.kurogame.xyz"}


def _is_ascnet_host(host):
    return host and (
        host in {"sdkapi.kurogame-service.com", "sdkapi.kurogame-service.xyz", "sdkapi2.kurogame-service.com"}
        or (host.startswith(REGION_CDN_PREFIXES) and host.endswith(".kurogame.net"))
    )


def _is_pgr_game_popup_notice_request(flow):
    host = flow.request.pretty_host
    path = flow.request.path.split("?", 1)[0]
    return (
        host
        and host.startswith(REGION_CDN_PREFIXES)
        and host.endswith(".pgr-game.com")
        and path.startswith("/prod/client/notice/config/")
        and path.endswith("/PopUpPicNotice.json")
    )



def _is_upstream_notice_html_request(flow):
    path = flow.request.path.split("?", 1)[0]
    return (
        _is_ascnet_host(flow.request.pretty_host)
        and path.startswith("/prod/client/notice/html/")
    )


def _is_ascnet_gate_request(flow):
    return flow.request.path.split("?", 1)[0] in {"/api/Login/Login", CN_GATE_PATH}


def _is_feedback_request(flow):
    return "zspnslog." in flow.request.pretty_host and flow.request.path.split("?", 1)[0] == "/feedback"

def _is_wildcard_connect_request(flow):
    return flow.request.method == "CONNECT" and _is_local_wildcard_host(flow.request.pretty_host)


def _is_wildcard_ascnet_request(flow):
    path = flow.request.path.split("?", 1)[0]
    return _is_local_wildcard_host(flow.request.pretty_host) and path.startswith(("/api/", "/prod/", "/sdkcom/"))


def _is_config_tab_request(flow, prefixes):
    # The client bootstraps from hard-coded PrimaryCdns on *.pgr-game.com (prod-<region>cdn-ak) and falls back to
    # *.kurogame.net; both must reach the local rewrite or the real login server wins.
    host = flow.request.pretty_host
    path = flow.request.path.split("?", 1)[0]
    return (
        host.startswith(prefixes)
        and host.endswith((".kurogame.net", ".pgr-game.com"))
        and path.startswith("/prod/client/config/")
        and path.endswith("/standalone/config.tab")
    )


def _is_cn_config_request(flow):
    path = flow.request.path.split("?", 1)[0]
    return (
        flow.request.pretty_host in CN_CONFIG_HOSTS
        and path.startswith("/prod/client/config/")
        and "/com.kurogame.haru.kuro/" in path
        and path.endswith("/standalone/config.tab")
    )


def _is_authoritative_config_request(flow):
    return _is_config_tab_request(flow, AUTHORITATIVE_CONFIG_PREFIXES) or _is_cn_config_request(flow)


def _is_cn_sdk_request(flow):
    return flow.request.pretty_host in CN_SDK_HOSTS and flow.request.path.split("?", 1)[0].startswith("/sdkcom/")


def _is_cn_telemetry_request(flow):
    return flow.request.pretty_host in CN_SDK_HOSTS and flow.request.path.split("?", 1)[0] == "/ad-service/v1/sendEvent"


def _ascnet_origin():
    scheme, host, port = _ascnet_target()
    if port in (80, 443):
        return f"{scheme}://{host}"
    return f"{scheme}://{host}:{port}"


def _rewrite_login_url(value, target_origin, login_path=None):
    # ServerListStr/ChannelServerListStr are `|`-separated entries of `label#url` / `default#label#url`
    # (CN: `channel#label#url;url;...`). Keep labels and metadata, replace every URL's origin with the local
    # AscNet target. The path is preserved unless login_path pins it (CN gates carry no query: the client appends one).
    entries = []
    for entry in value.split("|"):
        head, sep, urls = entry.rpartition("#")
        rewritten = []
        for url in urls.split(";"):
            parsed = urlparse(url)
            if sep and parsed.scheme and parsed.hostname:
                url = target_origin + (login_path or parsed.path + (f"?{parsed.query}" if parsed.query else ""))
            rewritten.append(url)
        entries.append(head + sep + ";".join(rewritten))
    return "|".join(entries)


def _rewrite_config_body(body, target_origin, login_path=None):
    lines = body.split("\n")
    out = []
    for line in lines:
        cols = line.split("\t")
        if len(cols) >= 3 and cols[0] in {"ServerListStr", "ChannelServerListStr"}:
            # A CRLF-terminated final column keeps its terminator outside the rewritten value.
            ending = "\r" if cols[2].endswith("\r") else ""
            cols[2] = _rewrite_login_url(cols[2].rstrip("\r"), target_origin, login_path) + ending
        out.append("\t".join(cols))
    return "\n".join(out)


def next_layer(nextlayer: layer.NextLayer):
    # Only mark hosts we intend to rewrite. HTTPS proxying is intentionally
    # avoided for pinned KRSDK/service hosts by the runner/environment.
    sni = nextlayer.context.client.sni
    if _is_ascnet_host(sni):
        ctx.log.info("ascnet candidate sni:" + sni)


def http_connect(flow: http.HTTPFlow) -> None:
    _log_flow("CONNECT", flow)

    if not _is_wildcard_connect_request(flow):
        return

    flow.response = http.Response.make(
        502,
        b"AscNet blocked invalid CONNECT target 0.0.0.0/::; restart with run_steam.py so local SDK URLs use 127.0.0.1.\n",
        {"Content-Type": "text/plain"},
    )
    _log_flow("CONNECT-BLOCK", flow)


def request(flow: http.HTTPFlow) -> None:
    _log_flow("REQ", flow)

    if _is_feedback_request(flow) or _is_cn_telemetry_request(flow):
        flow.response = http.Response.make(200, b"OK", {"Content-Type": "text/plain"})
        _log_flow("SINK", flow)
        return

    # Notice metadata points at version-specific CDN HTML files. Keep those
    # requests on the original CDN so new notices work without local fixtures.
    if _is_upstream_notice_html_request(flow):
        _log_flow("PASS", flow)
        return

    # TW/KR/JP/CN config carries authoritative upstream metadata (doc/launch version,
    # channel, CDN list) that local AscNet does not reproduce. Let it pass
    # through to the real CDN unchanged; response() rewrites only the login
    # endpoints to the local target.
    if _is_authoritative_config_request(flow):
        _log_flow("PASS", flow)
        return

    if not (_is_ascnet_host(flow.request.pretty_host) or _is_cn_sdk_request(flow) or _is_pgr_game_popup_notice_request(flow)
            or _is_config_tab_request(flow, REGION_CDN_PREFIXES)
            or _is_ascnet_gate_request(flow) or _is_wildcard_ascnet_request(flow)):
        return

    scheme, host, port = _ascnet_target()
    original_host = flow.request.host
    original_scheme = flow.request.scheme

    flow.request.scheme = scheme
    flow.request.host = host
    flow.request.port = port
    flow.request.headers["Host"] = host if port in (80, 443) else f"{host}:{port}"
    flow.request.headers["X-Forwarded-Host"] = original_host
    flow.request.headers["X-Forwarded-Proto"] = original_scheme


def response(flow: http.HTTPFlow) -> None:
    _log_flow("RSP", flow)

    # Authoritative config was passed through upstream unchanged. Rewrite only the login
    # endpoint URLs to the local target so the client reaches local AscNet,
    # keeping all authoritative metadata (version, channel, CDNs, labels).
    if not _is_authoritative_config_request(flow) or flow.response is None:
        return

    body = flow.response.content
    if not body:
        return

    text = body.decode("utf-8", errors="replace")
    rewritten = _rewrite_config_body(text, _ascnet_origin(), CN_GATE_PATH if _is_cn_config_request(flow) else None)
    if rewritten != text:
        flow.response.content = rewritten.encode("utf-8")
        _log_flow("CONFIG-REWRITE", flow)
