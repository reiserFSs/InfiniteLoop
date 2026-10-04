import os
import json
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


def _log_flow(prefix, flow):
    path = _flow_log_path()
    status = getattr(flow.response, "status_code", "-") if getattr(flow, "response", None) else "-"
    url = (f"{flow.request.pretty_host}:{flow.request.port}"
           if prefix.startswith("CONNECT") else flow.request.pretty_url)
    entry = {"method": flow.request.method, "url": url, "status": status}
    original = getattr(flow, "metadata", {}).get("ascnet_original_url")
    if original and original != url:
        entry["originalUrl"] = original
    message = flow.request if prefix == "REQ" else flow.response if prefix == "RSP" else None
    if message is not None:
        label = "request" if prefix == "REQ" else "response"
        entry[label + "Headers"] = dict(message.headers)
        if hasattr(message.headers, "items"):
            try:
                pairs = list(message.headers.items(multi=True))
                if len(pairs) != len(entry[label + "Headers"]):
                    entry[label + "HeaderPairs"] = pairs
            except TypeError:
                pass
        content = getattr(message, "content", None) or b""
        content_type = message.headers.get("Content-Type", "").lower()
        if not content or any(kind in content_type for kind in ("json", "text", "xml", "javascript", "x-www-form-urlencoded")) or (not content_type and b"\x00" not in content):
            text = content.decode("utf-8", errors="replace")
            if label == "response" and "json" in content_type:
                try:
                    entry["responseJson"] = json.loads(text)
                except ValueError:
                    entry["responseBody"] = text
            else:
                entry[label + "Body"] = text
        else:
            entry[label + "Bytes"] = len(content)
    if prefix == "ERROR":
        entry["error"] = str(getattr(flow, "error", ""))
    formatted = prefix + " " + json.dumps(entry, ensure_ascii=False, indent=2)
    logger = getattr(ctx, "log", None)
    if logger is not None:
        logger.info(formatted)
    if not path:
        return
    try:
        with open(path, "a", encoding="utf-8") as handle:
            handle.write(formatted + "\n")
    except OSError:
        # Diagnostics must not interrupt routing.
        pass


def _is_ascnet_host(host):
    return host and (
        host in {"sdkapi.kurogame-service.com", "sdkapi.kurogame-service.xyz"}
        or (host.startswith(("prod-encdn-", "prod-twcdn-")) and host.endswith(".kurogame.net"))
    )
def _is_pgr_game_popup_notice_request(flow):
    host = flow.request.pretty_host
    path = flow.request.path.split("?", 1)[0]
    return (
        host
        and host.startswith(("prod-encdn-", "prod-twcdn-"))
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
    return flow.request.path.split("?", 1)[0] in {"/api/Login/Login", "/api/Login/Login-cn"}


def _is_feedback_request(flow):
    return "zspnslog." in flow.request.pretty_host and flow.request.path.split("?", 1)[0] == "/feedback"


def _is_cn_config_request(flow):
    path = flow.request.path.split("?", 1)[0]
    return (flow.request.pretty_host in {"prod-zspns-txcdn.kurogame.com", "prod-zspnsalicdn.kurogame.com", "prod-zspnstxcdn.kurogame.com"}
            and path.startswith("/prod/client/config/")
            and "/com.kurogame.haru.kuro/" in path
            and path.endswith("/standalone/config.tab"))


def _is_cn_sdk_request(flow):
    return (flow.request.pretty_host == "sdkapi.kurogame.com"
            and flow.request.path.split("?", 1)[0].startswith("/sdkcom/"))


def _rewrite_cn_config_body(body, target_origin):
    def rewrite_url(url):
        parsed = urlparse(url)
        if not (parsed.scheme and parsed.hostname):
            return url
        target = urlparse(target_origin)
        # The client appends '?' unconditionally. Gate bases must not contain a query.
        return urlunparse(parsed._replace(scheme=target.scheme, netloc=target.netloc,
                                         path="/api/Login/Login-cn", query="", fragment=""))

    def rewrite_group(group):
        head, sep, urls = group.rpartition("#")
        if not sep:
            return group
        return head + sep + ";".join(rewrite_url(url) for url in urls.split(";"))

    out = []
    for line in body.split("\n"):
        cols = line.split("\t")
        if len(cols) >= 3 and cols[0] in {"ServerListStr", "ChannelServerListStr"}:
            ending = "\r" if cols[2].endswith("\r") else ""
            cols[2] = "|".join(rewrite_group(group) for group in cols[2].rstrip("\r").split("|")) + ending
        out.append("\t".join(cols))
    return "\n".join(out)

def _is_wildcard_connect_request(flow):
    return flow.request.method == "CONNECT" and _is_local_wildcard_host(flow.request.pretty_host)


def _is_wildcard_ascnet_request(flow):
    path = flow.request.path.split("?", 1)[0]
    return _is_local_wildcard_host(flow.request.pretty_host) and path.startswith(("/api/", "/prod/", "/sdkcom/"))


def _is_tw_config_request(flow):
    host = flow.request.pretty_host
    path = flow.request.path.split("?", 1)[0]
    return (
        host.startswith("prod-twcdn-")
        and host.endswith(".kurogame.net")
        and path.startswith("/prod/client/config/")
        and path.endswith("/standalone/config.tab")
    )


def _ascnet_origin():
    scheme, host, port = _ascnet_target()
    if port in (80, 443):
        return f"{scheme}://{host}"
    return f"{scheme}://{host}:{port}"


def _rewrite_login_url(value, target_origin):
    # ServerListStr/ChannelServerListStr are `label#url` / `default#label#url`.
    # Keep labels and metadata, replace only the final URL's origin with the
    # local AscNet target while preserving its path.
    head, sep, url = value.rpartition("#")
    parsed = urlparse(url)
    if not (sep and parsed.scheme and parsed.hostname):
        return value
    suffix = parsed.path + (f"?{parsed.query}" if parsed.query else "")
    return head + sep + target_origin + suffix


def _rewrite_tw_config_body(body, target_origin):
    lines = body.split("\n")
    out = []
    for line in lines:
        cols = line.split("\t")
        if len(cols) >= 3 and cols[0] in {"ServerListStr", "ChannelServerListStr"}:
            cols[2] = _rewrite_login_url(cols[2], target_origin)
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

    if _is_feedback_request(flow) or (flow.request.pretty_host == "sdkapi.kurogame.com" and flow.request.path.split("?", 1)[0] == "/ad-service/v1/sendEvent"):
        flow.response = http.Response.make(200, b"OK", {"Content-Type": "text/plain"})
        _log_flow("SINK", flow)
        return

    # Notice metadata points at version-specific CDN HTML files. Keep those
    # requests on the original CDN so new notices work without local fixtures.
    if _is_upstream_notice_html_request(flow):
        _log_flow("PASS", flow)
        return

    # TW config carries authoritative upstream metadata (doc/launch version,
    # channel, CDN list) that local AscNet does not reproduce. Let it pass
    # through to the real CDN unchanged; response() rewrites only the login
    # endpoints to the local target.
    if _is_tw_config_request(flow) or _is_cn_config_request(flow):
        _log_flow("PASS", flow)
        return

    if not (_is_ascnet_host(flow.request.pretty_host) or _is_cn_sdk_request(flow) or _is_pgr_game_popup_notice_request(flow)
            or _is_ascnet_gate_request(flow) or _is_wildcard_ascnet_request(flow)):
        return

    scheme, host, port = _ascnet_target()
    original_host = flow.request.host
    original_scheme = flow.request.scheme
    if not hasattr(flow, "metadata"):
        flow.metadata = {}
    flow.metadata["ascnet_original_url"] = flow.request.pretty_url

    flow.request.scheme = scheme
    flow.request.host = host
    flow.request.port = port
    flow.request.headers["Host"] = host if port in (80, 443) else f"{host}:{port}"
    flow.request.headers["X-Forwarded-Host"] = original_host
    flow.request.headers["X-Forwarded-Proto"] = original_scheme


def response(flow: http.HTTPFlow) -> None:

    # TW config was passed through upstream unchanged. Rewrite only the login
    # endpoint URLs to the local target so the client reaches local AscNet,
    # keeping all authoritative metadata (version, channel, CDNs, labels).
    if not (_is_tw_config_request(flow) or _is_cn_config_request(flow)) or flow.response is None or flow.response.status_code != 200:
        _log_flow("RSP", flow)
        return

    body = flow.response.content
    if not body:
        _log_flow("RSP", flow)
        return

    text = body.decode("utf-8", errors="replace")
    rewritten = (_rewrite_cn_config_body if _is_cn_config_request(flow) else _rewrite_tw_config_body)(text, _ascnet_origin())
    if rewritten != text:
        flow.response.content = rewritten.encode("utf-8")
        _log_flow("CN-CONFIG-REWRITE" if _is_cn_config_request(flow) else "TW-CONFIG-REWRITE", flow)
    _log_flow("RSP", flow)


def error(flow: http.HTTPFlow) -> None:
    _log_flow("ERROR", flow)
