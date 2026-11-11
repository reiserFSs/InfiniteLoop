# CN SDK and CDN support

CN (`com.kurogame.haru.kuro`, 4.8.0) is a package entry in `Resources/Configs/version_config.json`
with the region's authoritative `ConfigRows`, like KR/JP. Only AscNet-owned rows are templated
(`PayCallbackUrl`, `ServerListStr`, `ChannelServerListStr` use `{origin}`); the rest, including the
CN CDN list, document/launch hashes and `Channel`, are served verbatim. `ChannelServerListStr` keeps
the retail per-login-channel groups (`18|19|46|56|167`), each pointing at `{origin}/api/Login/Login-cn`.
`AscNet.Test/Fixtures/Region/cn.tab` is the retail table the regression compares against.

Through the proxy, CN `config.tab` (`prod-zspns-txcdn` / `prod-zspnsalicdn` `.kurogame.com`) passes through
to the real CDN and `response()` rewrites every `;`-separated gate URL in `ServerListStr` /
`ChannelServerListStr` to `ASCNET_PROXY_TARGET/api/Login/Login-cn` without a query (the client appends
`?loginType=...`). Patch files and agreement pages stay upstream.

The proxy routes `/sdkcom/...` on every KRSDKConfig host (`sdkapi`/`sdkapi2`/`sdkapi3.kurogame.com`, `sdkapi.kurogame.xyz`) to AscNet and sinks `/ad-service/v1/sendEvent`.
CN is identified by the SDK package, original host, project/product headers or form fields; locally
generated player-config links carry `region=cn`. EN/TW/KR/JP keep their configuration and login contracts.

## Local account login

AscNet policy: the first CN `/sdkcom/v2/login/accLogin.lg` request with `loginName`, `password` and
optional `loginType` creates the account (same plaintext store as `/api/AscNet/register`). Existing
accounts require a matching password (fixed-time compare); failed logins never overwrite it.

CN automatic login accepts `autoToken` or `token` and resolves the persisted `Account.Token`. Responses use
that token for `code`, `token` and `accessToken`; `autoToken` is `0.<Account.Token>.<UTC + 30 days>`, which
auto login unwraps without checking the timestamp (bare tokens are also accepted). Nothing is rotated or
expired; the timestamp and `expires_in` are SDK compatibility metadata. Obsolete `cn_auto_token` fields in
existing account documents are ignored.

`/api/Login/Login-cn` (and any gate request with `region=cn`) never maps an unknown token to the Steam
fallback account. `thirdLogin.accLogin` and the `phone` UI switch are enabled, `accReg` stays off (accounts
are created by account login; no SMS). `age` is the AscNet compatibility constant `CnSdkCompatibilityAge`.

## Verification

```text
python3 -m unittest test_run_steam.py test_proxy.py
dotnet run --project AscNet.Test/AscNet.Test.csproj -- --cn-sdk-config-only
dotnet run --project AscNet.Test/AscNet.Test.csproj -- --cn-sdk-login-only   # needs MongoDB; cleans up its accounts
```

## Native launcher and patch (no proxy)

The CN client keeps its official SDK (`PGR_Data/Plugins/KRSDKEx.dll`, `libkrsdkcurl.dll`, plain-JSON
`KRSDKRes/KRSDKConfig.json`: G148/A1393, channel 19; there is no `KRSDK.dll`/`KRSDK.bin`) and its own login UI.
The launcher therefore installs only `version.dll`, `lucia.dll`, `libraries.txt` and the PGRBase startup stub; the
SDK files are hash-checked against `supported-client.json`, never replaced. Inside the game, `lucia.dll` preloads
`libkrsdkcurl.dll`, hooks its `kr_sdk_curl_easy_setopt` (the only curl import KRSDKEx uses to set a URL) and rewrites
`CURLOPT_URL` values whose path is `/sdkcom/*` or `/ad-service/v1/sendEvent` (any `sdkapi*.kurogame.com|xyz` host) to
`ASCNET_PATCH_ORIGIN`. Agreement pages, payment, analytics and OAuth hosts are untouched. Unity requests (CN
`config.tab`, notices) are routed by the existing path rules, so `ServerListStr` leads the client to `/api/Login/Login-cn`.
Log lines: `[lucia] OK KRSDKConfig.json identity`, `[lucia] OK CN SDK hook install`, `[lucia] routed <url> -> <url>`.

Per-player SDK data lives in `%APPDATA%\KR_G148\A1393\` (agreement/cache); the SDK shows its privacy agreement first.
The helper process `KRSDKExternal.exe` uses its own libcurl and still reports analytics to Kuro (`KR_DATA_HOST`,
`KR_ShushuAddress`); that is retail SDK behaviour and not routed.

Not covered: `/sdkcom/v2/heartbeat/statusCheck.lg`, `sys/conf/cps.lg`, SMS/QR/idcard/third-party login and
`/sdk/trade/*` payment endpoints are not implemented by the server.
