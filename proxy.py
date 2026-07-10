import os
from urllib.parse import parse_qsl, urlencode, urlparse, urlunparse
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


def _redact_url(url):
    parsed = urlparse(url)
    query = []
    for key, value in parse_qsl(parsed.query, keep_blank_values=True):
        if key.lower() in {"token", "access_token", "accesstoken", "refresh_token", "code", "password", "pwd"}:
            value = "<redacted>"
        query.append((key, value))
    return urlunparse(parsed._replace(query=urlencode(query)))


def _log_flow(prefix, flow):
    path = _flow_log_path()
    if not path:
        return
    status = getattr(flow.response, "status_code", "-") if getattr(flow, "response", None) else "-"
    line = f"{prefix} {flow.request.method} {_redact_url(flow.request.pretty_url)} -> {status}\n"
    with open(path, "a", encoding="utf-8") as handle:
        handle.write(line)


def _is_ascnet_host(host):
    return host and (
        host in {"sdkapi.kurogame-service.com", "sdkapi.kurogame-service.xyz"}
        or (host.startswith("prod-encdn-") and host.endswith(".kurogame.net"))
    )

def _is_upstream_notice_html_request(flow):
    path = flow.request.path.split("?", 1)[0]
    return (
        _is_ascnet_host(flow.request.pretty_host)
        and path.startswith("/prod/client/notice/html/")
    )


def _is_ascnet_gate_request(flow):
    return flow.request.path.split("?", 1)[0] == "/api/Login/Login"


def _is_feedback_request(flow):
    return flow.request.pretty_host == "prod.enzspnslog.kurogame.com" and flow.request.path.split("?", 1)[0] == "/feedback"

def _is_wildcard_connect_request(flow):
    return flow.request.method == "CONNECT" and _is_local_wildcard_host(flow.request.pretty_host)


def _is_wildcard_ascnet_request(flow):
    path = flow.request.path.split("?", 1)[0]
    return _is_local_wildcard_host(flow.request.pretty_host) and path.startswith(("/api/", "/prod/", "/sdkcom/"))






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

    if _is_feedback_request(flow):
        flow.response = http.Response.make(200, b"OK", {"Content-Type": "text/plain"})
        _log_flow("SINK", flow)
        return

    # Notice metadata points at version-specific CDN HTML files. Keep those
    # requests on the original CDN so new notices work without local fixtures.
    if _is_upstream_notice_html_request(flow):
        _log_flow("PASS", flow)
        return

    if not (_is_ascnet_host(flow.request.pretty_host) or _is_ascnet_gate_request(flow) or _is_wildcard_ascnet_request(flow)):
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