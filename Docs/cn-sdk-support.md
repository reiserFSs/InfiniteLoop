# CN SDK and CDN support

CN `config.tab` uses the TW-style upstream path: the proxy preserves official
patch metadata and rewrites every `ServerListStr` / `ChannelServerListStr` gate
URL to `ASCNET_PROXY_TARGET/api/Login/Login-cn` without query parameters so the
client can append `?loginType=...`. Patch files and agreement pages
remain upstream. The local version configuration also contains the CN 4.8.12
SHA1 values under application version 4.8.0's `Packages` entry.

The proxy routes `sdkapi.kurogame.com/sdkcom/...` to AscNet. CN is identified by
the SDK package, original host, project/product headers or form fields; locally
generated player-config links carry `region=cn`. EN/TW retain their existing
configuration and login response contracts.

## Local account login

AscNet policy supports automatic account creation on the first CN
`/sdkcom/v2/login/accLogin.lg` request with `loginName`, `password`, and optional
`loginType`. Existing accounts require matching credentials; failed logins never
overwrite passwords. External `/api/AscNet/register` remains available.
Passwords use the existing AscNet credential comparison; a client's
transformed password value must match the registered value. No password-transform
algorithm is assumed.

CN automatic login accepts `autoToken` or `token`, using the existing persisted
`Account.Token`. Login responses use that same token for `code`, `token`, and
`accessToken`; `autoToken` preserves the successful reference SDK wire format
`0.<Account.Token>.<current UTC + 30 days>`. Automatic login unwraps it without
checking its timestamp and also accepts bare tokens. No additional token is
persisted, expired or rotated. The wrapper timestamp and `expires_in` are SDK
compatibility metadata, not server expiry rules.
The access-token and OAuth endpoints resolve the same account token through Gate
login. Obsolete `cn_auto_token` fields in existing documents are ignored and do
not require manual removal. CN gate URLs disable Steam fallback-account mapping.

`thirdLogin.accLogin` is enabled in both the initial configuration and follow-up
player configuration. The reference `phone` UI switch is enabled alongside
`accLogin`; this does not implement SMS authentication. Separate SDK registration
(`accReg`) remains disabled; new accounts are created through account login.
CN responses include `showPaw`, `phoneCheck`, `phone`, and `age` as in the
successful reference contract. `phone` derives from the local account username;
`age` uses the maintainer-authorized `CnSdkCompatibilityAge = 21` constant,
an AscNet SDK compatibility value rather than verified player age.
`heartbeat/switchStatus.lg` is available with the reference success envelope.

## Diagnostics

Proxy events print as `CONNECT`, `REQ`, `RSP`, `PASS`, `SINK`, and `ERROR` followed
by formatted JSON. Logging retains raw URLs, headers and textual bodies without
redaction. JSON responses use `responseJson`; binary responses use byte counts.
Rewritten requests also include `originalUrl`. Set `ASCNET_PROXY_LOG` to duplicate
the console events into a UTF-8 file. Hosts in mitmproxy's `ignore_hosts` only
expose CONNECT events, and HTTPS must reach the proxy for HTTP bodies to be visible.

At `VerboseLevel` Debug or SuperDebug, SDKServer also prints raw HTTP URL,
headers and body before handler execution, rewinding the body for the handler.
Full exception chains, account details and login stages are DEBUG logs. Normal
verbosity retains only method/path/status and exception types and does not read
bodies for logging. An exception before response start is recorded as HTTP 500
rather than the default 200 at every verbosity. Feedback uploads remain suppressed
in SDKServer. Proxy logging is independent of this setting.

## Focused verification

```text
python -m unittest test_run_steam.py test_proxy.py
dotnet run --project AscNet.Test/AscNet.Test.csproj -- --cn-sdk-config-only
dotnet run --project AscNet.Test/AscNet.Test.csproj -- --cn-sdk-login-only
```

The login check requires MongoDB at the configured database endpoint and cleans
up its temporary accounts.
