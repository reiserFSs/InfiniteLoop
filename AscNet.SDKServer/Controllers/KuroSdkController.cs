using System.Net;
using AscNet.Common.Database;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AscNet.SDKServer.Controllers
{
    internal class KuroSdkController : IRegisterable
    {
        private const string LocalCuid = "1";
        private const string LocalEmail = "test@ascnet.local";
        private const string LocalUsername = "test";
        private const string LocalToken = "ascnet-local-token";
        private const string LocalOauthCode = "ascnet-local-oauth-code";
        private const string LocalAccessToken = "ascnet-local-access-token";
        private const int SteamLoginType = 23;

        public static void Register(WebApplication app)
        {
            // CN form data is read asynchronously once; existing EN/TW handlers retain their contract.
            app.Use(async (ctx, next) =>
            {
                if (ctx.Request.Path.StartsWithSegments("/sdkcom") && ctx.Request.HasFormContentType)
                    await ctx.Request.ReadFormAsync(ctx.RequestAborted);
                await next(ctx);
            });
            app.MapMethods("/sdkcom/v2/login/accLogin.lg", ["GET", "POST"], HandleAccountLogin);
            app.MapMethods("/sdkcom/v2/login/emailPwd.lg", ["GET", "POST"], HandleLogin);
            app.MapMethods("/sdkcom/v2/login/third/steam.lg", ["GET", "POST"], HandleLogin);
            app.MapMethods("/sdkcom/v2/login/third/pc/mark.lg", ["GET", "POST"], HandleThirdLoginMark);
            app.MapMethods("/sdkcom/v2/login/third/pc/browser.lg", ["GET", "POST"], HandleThirdLoginBrowser);
            app.MapMethods("/sdkcom/v2/login/auto.lg", ["GET", "POST"], HandleAutoLogin);
            app.MapMethods("/sdkcom/v2/login/real-name/login.lg", ["GET", "POST"], HandleLogin);
            app.MapMethods("/sdkcom/v2/login/preambleCode.lg", ["GET", "POST"], HandleLogin);
            app.MapMethods("/sdkcom/v2/auth/getToken.lg", ["GET", "POST"], HandleAccessToken);
            app.MapMethods("/sdkcom/v2/user/oauth/code/generate.lg", ["GET", "POST"], HandleOauthCode);
            app.MapMethods("/sdkcom/v2/sys/europe/config.lg", ["GET", "POST"], HandleSystemConfig);
            app.MapMethods("/sdkcom/v2/sys/conf.lg", ["GET", "POST"], HandleSystemConfig);
            app.MapGet("/sdkcom/v2/sys/player-config.json", HandlePlayerConfig);
            app.MapMethods("/sdkcom/v2/user/game/role.lg", ["GET", "POST"], HandleOk);
            app.MapMethods("/sdkcom/v2/heartbeat/tokenCheck.lg", ["GET", "POST"], HandleOk);
            app.MapMethods("/sdkcom/v2/heartbeat/switchStatus.lg", ["GET", "POST"], HandleOk);
            app.MapMethods("/sdkcom/v2/bind/device/status.lg", ["GET", "POST"], HandleOk);
            app.MapMethods("/sdkcom/v2/bind/device.lg", ["GET", "POST"], HandleOk);
            app.MapMethods("/sdkcom/v2/real-name-info/check.lg", ["GET", "POST"], HandleRealNameCheck);
            app.MapGet("/sdkcom/v2/local/login", HandleLocalLoginPage);
            app.MapMethods("/sdkcom/v2/local/{**path}", ["GET", "POST"], HandleOk);
        }

        private static IResult HandleLogin(HttpContext ctx)
        {
            if (IsCnRequest(ctx))
                return InvalidCredentials();
            return Results.Json(new
            {
                code = 0,
                msg = "success",
                data = LoginData(ctx)
            });
        }

        private static IResult HandleAccessToken(HttpContext ctx)
        {
            if (IsCnRequest(ctx))
            {
                string? code = RequestValue(ctx, "code");
                Account? account = string.IsNullOrEmpty(code) ? null : Account.FromToken(code);
                if (account is null)
                    return InvalidCredentials();
                // AscNet policy: local OAuth codes use the persisted, randomly generated account token.
                return Results.Json(new { code = 0, msg = "success", access_token = account.Token,
                    expires_in = LocalSdkExpiryHintSeconds,
                    data = new { access_token = account.Token, expires_in = LocalSdkExpiryHintSeconds } });
            }
            return Results.Json(new
            {
                code = 0,
                msg = "success",
                access_token = LocalAccessToken,
                expires_in = 2592000,
                data = new
                {
                    access_token = LocalAccessToken,
                    expires_in = 2592000
                }
            });
        }

        private static IResult HandleOauthCode(HttpContext ctx)
        {
            if (IsCnRequest(ctx))
            {
                string? token = RequestValue(ctx, "access_token", "accessToken");
                Account? account = string.IsNullOrEmpty(token) ? null : Account.FromToken(token);
                if (account is null)
                    return InvalidCredentials();
                return Results.Json(new { code = 0, msg = "success", data = new { oauthCode = account.Token, code = account.Token } });
            }
            return Results.Json(new
            {
                code = 0,
                msg = "success",
                data = new
                {
                    oauthCode = LocalOauthCode,
                    code = LocalOauthCode
                }
            });
        }

        private static IResult HandleThirdLoginMark(HttpContext ctx)
        {
            string mark = RequestValue(ctx, "mark") ?? "ascnet-local-third-login";
            return Results.Json(new
            {
                code = 0,
                msg = "success",
                data = new
                {
                    ready = 1,
                    mark
                }
            });
        }

        private static IResult HandleThirdLoginBrowser(HttpContext ctx)
        {
            string origin = LocalSdkOrigin(ctx);
            string mark = RequestValue(ctx, "mark") ?? "ascnet-local-third-login";
            string type = RequestValue(ctx, "type", "loginType") ?? SteamLoginType.ToString();
            string url = $"{origin}/sdkcom/v2/local/login?mark={Uri.EscapeDataString(mark)}&type={Uri.EscapeDataString(type)}";
            return Results.Json(new
            {
                code = 0,
                msg = "success",
                data = url,
                url
            });
        }

        private static IResult HandleSystemConfig(HttpContext ctx)
        {
            return Results.Json(new
            {
                code = 0,
                msg = "success",
                data = PlayerConfigData(ctx)
            });
        }

        private static IResult HandlePlayerConfig(HttpContext ctx)
        {
            return Results.Json(PlayerConfigData(ctx));
        }

        private static IResult HandleLocalLoginPage(HttpContext ctx)
        {
            string origin = LocalSdkOrigin(ctx);
            string gateUrl = $"{origin}/api/Login/Login";
            string html = $$"""
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>AscNet Local Login</title>
  <style>
    :root { color-scheme: dark; font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif; background: #0f1117; color: #f6f7fb; }
    body { margin: 0; min-height: 100vh; display: grid; place-items: center; }
    main { width: min(520px, calc(100vw - 48px)); padding: 32px; border: 1px solid #2d3342; border-radius: 18px; background: #171b25; box-shadow: 0 18px 48px rgba(0,0,0,.35); }
    h1 { margin: 0 0 12px; font-size: 26px; }
    p { line-height: 1.55; color: #cbd2e1; }
    code { color: #9ae6b4; word-break: break-all; }
    .ok { margin-top: 18px; padding: 14px 16px; border-radius: 12px; background: #10291d; color: #a7f3d0; }
  </style>
</head>
<body>
  <main>
    <h1>AscNet local login is active</h1>
    <p>The Steam KRSDK bridge is routed to this AscNet instance.</p>
    <p>Create or select the local AscNet account with <code>run_steam.py --ascnet-username</code>; the bridge maps successful Steam/KRSDK login callbacks to that account at <code>{{WebUtility.HtmlEncode(gateUrl)}}</code>.</p>
    <div class="ok">Close this window and press login again after the Steam/KRSDK account has authenticated.</div>
  </main>
</body>
</html>
""";
            return Results.Content(html, "text/html");
        }

        private static Dictionary<string, object> PlayerConfigData(HttpContext ctx)
        {
            bool cn = IsCnRequest(ctx);
            string origin = LocalSdkOrigin(ctx);
            string playerConfigUrl = $"{origin}/sdkcom/v2/sys/player-config.json" + (cn ? "?region=cn" : "");
            string pcGeetestUrl = $"{origin}/sdkcom/v2/local/geetest";
            string accCenterUrl = $"{origin}/sdkcom/v2/local/account-center";
            string pcThirdLoginUrl = $"{origin}/sdkcom/v2/local/login";
            string customerServiceUrl = $"{origin}/sdkcom/v2/local/customer-service";
            string sobotRedDotUrl = $"{origin}/sdkcom/v2/local/sobot/red-dot";
            string googlePcAuthResultUrl = $"{origin}/sdkcom/v2/local/google-pc-auth-result";
            string emailSystemUrl = $"{origin}/sdkcom/v2/local/email-system";
            string bizsirenUrl = $"{origin}/sdkcom/v2/local/bizsiren";
            string clientUrl = JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                ["pcGeetestUrl"] = pcGeetestUrl,
                ["accCenterUrl"] = accCenterUrl,
                ["pcThirdLoginUrl"] = pcThirdLoginUrl,
                ["kefu"] = customerServiceUrl,
                ["kefuServ"] = customerServiceUrl,
                ["sobot"] = 1,
                ["sobotRedDotUrl"] = sobotRedDotUrl,
                ["googlePcAuthResultUrl"] = googlePcAuthResultUrl,
                ["emailSystemUrl"] = emailSystemUrl,
                ["bizsiren"] = bizsirenUrl
            });

            return new Dictionary<string, object>
            {
                ["link"] = new[] { new { url = playerConfigUrl, weight = 1 } },
                ["clientSwitch"] = 1,
                ["ageCheckBox"] = 0,
                ["regionMinAge"] = 0,
                ["googlePcConsumeIntervalSec"] = 60,
                ["audioSwitch"] = 1,
                ["kefuSessionExpireHour"] = 1,
                ["ignoreCheckWndVisibleForWin"] = 0,
                ["enableTransparentJiYanWebviewForWin"] = 0,
                ["clientFocus"] = 1,
                ["krData"] = 0,
                ["marketAppsFlyer"] = 0,
                ["pcGeetestUrl"] = pcGeetestUrl,
                ["accCenterUrl"] = accCenterUrl,
                ["clientUrl"] = clientUrl,
                ["pcThirdLoginUrl"] = pcThirdLoginUrl,
                ["thirdLogin"] = cn ? CnThirdLoginPolicy() : 1,
                ["heartFreq"] = 60,
                ["kefuInterval"] = 60,
                ["kefu"] = customerServiceUrl,
                ["kefuServ"] = customerServiceUrl,
                ["sobot"] = 1,
                ["sobotRedDotUrl"] = sobotRedDotUrl,
                ["googlePcAuthResultUrl"] = googlePcAuthResultUrl,
                ["emailSystemUrl"] = emailSystemUrl,
                ["bizsiren"] = bizsirenUrl,
                ["webviewIgnoreCertErrorForWin"] = 1,
                ["disableAgreementRemainDlgForWin"] = 1,
                ["PayEventFixedForPCSteam"] = 1,
                ["clientFocusForSteamDeck"] = 1,
                ["useGoogleSDKForWin"] = 1,
                ["disableWebviewVideoSupportedForWin"] = 0,
                ["useNewClientFocus"] = 0,
                ["enableTransparentJiYanWebview"] = 0
            };
        }

        private static string LocalSdkOrigin(HttpContext ctx)
        {
            string? configuredOrigin = Environment.GetEnvironmentVariable("ASCNET_PUBLIC_HTTP_ORIGIN")?.TrimEnd('/');
            if (!string.IsNullOrEmpty(configuredOrigin))
                return configuredOrigin;

            string scheme = ctx.Request.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? ctx.Request.Scheme;
            string host = ctx.Request.Headers["X-Forwarded-Host"].FirstOrDefault() ?? ctx.Request.Host.Value;
            return $"{scheme}://{host}".TrimEnd('/');
        }


        private static IResult HandleRealNameCheck(HttpContext ctx)
        {
            return Results.Json(new
            {
                code = 0,
                msg = "success",
                data = new
                {
                    realNameMethod = 0,
                    realNameKey = string.Empty,
                    realNameUrl = string.Empty
                }
            });
        }

        private static IResult HandleOk(HttpContext ctx)
        {
            return Results.Json(new
            {
                code = 0,
                msg = "success",
                data = new { }
            });
        }

        private static object LoginData(HttpContext ctx)
        {
            string cuid = RequestValue(ctx, "cuid", "uid", "userId", "id") ?? LocalCuid;
            string username = RequestValue(ctx, "username") ?? LocalUsername;
            string email = RequestValue(ctx, "email") ?? LocalEmail;
            string token = RequestValue(ctx, "token", "autoToken", "accessToken") ?? LocalToken;
            int loginType = RequestIntValue(ctx, "loginType") ?? SteamLoginType;
            long id = RequestLongValue(ctx, "cuid", "uid", "userId", "id") ?? 1;

            return new
            {
                id,
                cuid,
                username,
                loginType,
                code = RequestValue(ctx, "code") ?? "0",
                email,
                autoToken = token,
                token,
                bindDevStat = 0,
                idStat = 0,
                firstLgn = 0,
                bindDevMsg = string.Empty,
                realNameMethod = 0,
                thirdNickName = RequestValue(ctx, "thirdNickName", "nickname", "nickName") ?? username,
                bindDevSwitch = 0,
                realNameUrl = string.Empty,
                realNameKey = string.Empty
            };
        }

        private static string? FirstQueryValue(HttpContext ctx, string key)
        {
            return ctx.Request.Query.TryGetValue(key, out var values) ? values.FirstOrDefault() : null;
        }

        // AscNet policy: CN account login creates an account on first use. Existing
        // credentials must match; no separate SDK registration contract is assumed.
        // Compatibility metadata only; local account tokens are not expired or rotated.
        private const int LocalSdkExpiryHintSeconds = 30 * 24 * 60 * 60;
        // Maintainer-authorized AscNet SDK compatibility value, not a verified account age.
        private const int CnSdkCompatibilityAge = 21;

        private static object CnThirdLoginPolicy() => new Dictionary<string, object>
        {
            ["wechat"] = new { enabled = 0 }, ["qq"] = new { enabled = 0 },
            ["phone"] = new { enabled = 1 }, ["tourist"] = new { enabled = 0 },
            ["phoneQk"] = new { enabled = 0 }, ["accReg"] = new { enabled = 0 },
            ["accLogin"] = new { enabled = 1 }, ["qrLogin"] = new { enabled = 0 },
            ["apple"] = new { enabled = 0 }, ["taptap"] = new { enabled = 0 }
        };

        private static bool IsCnRequest(HttpContext ctx)
        {
            string? package = RequestValue(ctx, "pkg");
            if (!string.IsNullOrEmpty(package))
                return package is "com.kurogame.haru.hero" or "com.kurogame.haru.kuro";
            string host = ctx.Request.Headers["X-Forwarded-Host"].FirstOrDefault() ?? ctx.Request.Host.Host;
            return host.Split(':')[0].Equals("sdkapi.kurogame.com", StringComparison.OrdinalIgnoreCase)
                || string.Equals(RequestValue(ctx, "region"), "cn", StringComparison.OrdinalIgnoreCase)
                || (string.Equals(ctx.Request.Headers["Kr-ProjectId"].FirstOrDefault() ?? RequestValue(ctx, "projectId"), "G148", StringComparison.Ordinal)
                    && string.Equals(ctx.Request.Headers["Kr-ProductId"].FirstOrDefault() ?? RequestValue(ctx, "productId"), "A1393", StringComparison.Ordinal));
        }

        private static IResult InvalidCredentials() => Results.Json(new { code = -1, msg = "Invalid credentials", data = new { } });

        private static IResult HandleAccountLogin(HttpContext ctx)
        {
            ctx.Items["AscNet.LoginStage"] = "request-validation";
            if (!IsCnRequest(ctx))
                return InvalidCredentials();
            string? username = RequestValue(ctx, "loginName");
            string? password = RequestValue(ctx, "password");
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
                return InvalidCredentials();
            ctx.Items["AscNet.LoginStage"] = "account-lookup";
            Account? account = Account.FromUsername(username);
            if (account is null)
            {
                try
                {
                    ctx.Items["AscNet.LoginStage"] = "account-create";
                    // Persist the submitted SDK credential without assuming a client password transform.
                    account = Account.Create(username, password);
                }
                catch (ArgumentException)
                {
                    ctx.Items["AscNet.LoginStage"] = "account-create-retry-lookup";
                    // Another registration may have completed after the initial lookup.
                    account = Account.FromUsername(username);
                }
            }
            if (account is not null)
                ctx.Items["AscNet.LoginAccount"] = $"uid={account.Uid}, objectId={account.Id}, username={account.Username}";
            ctx.Items["AscNet.LoginStage"] = "password-validation";
            if (account is null || !string.Equals(account.Password, password, StringComparison.Ordinal))
                return InvalidCredentials();
            return CnLoginResponse(ctx, account);
        }

        private static IResult HandleAutoLogin(HttpContext ctx)
        {
            if (!IsCnRequest(ctx))
                return HandleLogin(ctx);
            string? autoToken = RequestValue(ctx, "autoToken", "token");
            if (string.IsNullOrEmpty(autoToken))
                return InvalidCredentials();
            ctx.Items["AscNet.LoginStage"] = "auto-account-token-lookup";
            string? accountToken = CnAutoTokenAccessToken(autoToken);
            if (accountToken is null)
                return InvalidCredentials();
            Account? account = Account.FromToken(accountToken);
            if (account is null)
                return InvalidCredentials();
            return CnLoginResponse(ctx, account);
        }

        private static string? CnAutoTokenAccessToken(string token)
        {
            if (!token.Contains('.'))
                return token;
            string[] parts = token.Split('.');
            // The SDK wrapper is transport metadata. AscNet does not expire the persisted token.
            return parts.Length == 3 && parts[0] == "0" && !string.IsNullOrEmpty(parts[1])
                && long.TryParse(parts[2], System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out _) ? parts[1] : null;
        }

        private static IResult CnLoginResponse(HttpContext ctx, Account account)
        {
            ctx.Items["AscNet.LoginStage"] = "account-response";
            ctx.Items["AscNet.LoginAccount"] = $"uid={account.Uid}, objectId={account.Id}, username={account.Username}";
            long sdkExpiryHint = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + LocalSdkExpiryHintSeconds;
            return Results.Json(new { code = 0, msg = "登录成功", data = new Dictionary<string, object>
            {
                ["id"] = account.Uid, ["cuid"] = account.Uid.ToString(), ["username"] = account.Username,
                // Dictionary keys preserve both client spellings without case-insensitive CLR property collisions.
                ["sdkuserid"] = account.Uid.ToString(), ["sdkUserId"] = account.Uid.ToString(),
                ["loginType"] = RequestIntValue(ctx, "loginType") ?? 0, ["code"] = account.Token,
                ["age"] = CnSdkCompatibilityAge, ["showPaw"] = false,
                ["email"] = $"{account.Username}@ascnet.local", ["token"] = account.Token, ["accessToken"] = account.Token,
                ["autoToken"] = FormattableString.Invariant($"0.{account.Token}.{sdkExpiryHint}"), ["autoTokenStatus"] = true,
                ["phoneCheck"] = 0, ["phone"] = account.Username,
                ["bindDevStat"] = 0, ["idStat"] = 0, ["firstLgn"] = 0, ["bindDevMsg"] = string.Empty,
                ["realNameMethod"] = 0, ["thirdNickName"] = account.Username, ["bindDevSwitch"] = 0,
                ["realNameUrl"] = string.Empty, ["realNameKey"] = string.Empty
            } });
        }

        private static string? RequestValue(HttpContext ctx, params string[] keys)
        {
            foreach (string key in keys)
            {
                string? queryValue = FirstQueryValue(ctx, key);
                if (!string.IsNullOrEmpty(queryValue))
                    return queryValue;
            }

            if (!ctx.Request.HasFormContentType)
                return null;

            IFormCollection form = ctx.Request.ReadFormAsync().GetAwaiter().GetResult();
            foreach (string key in keys)
            {
                if (form.TryGetValue(key, out var values))
                {
                    string? value = values.FirstOrDefault();
                    if (!string.IsNullOrEmpty(value))
                        return value;
                }
            }

            return null;
        }

        private static int? RequestIntValue(HttpContext ctx, params string[] keys)
        {
            string? value = RequestValue(ctx, keys);
            return int.TryParse(value, out int parsed) ? parsed : null;
        }

        private static long? RequestLongValue(HttpContext ctx, params string[] keys)
        {
            string? value = RequestValue(ctx, keys);
            return long.TryParse(value, out long parsed) ? parsed : null;
        }
    }
}
