using AscNet.Common.Database;
using Microsoft.AspNetCore.Builder;
using MongoDB.Driver;
using Newtonsoft.Json.Linq;
using System.Net;

namespace AscNet.Test;

internal partial class Program
{
    private static async Task ValidateCnSdkConfiguration()
    {
        ValidateLegacyCnAccountDocument();
        await ValidateCnLoginResponseSerialization();
        await ValidateCnGateUrlBinding();
        var previousVerbosity = AscNet.Common.Common.config.VerboseLevel;
        try
        {
            foreach (var verbosity in new[] { AscNet.Common.VerboseLevel.Normal, AscNet.Common.VerboseLevel.Debug })
            {
                AscNet.Common.Common.config.VerboseLevel = verbosity;
                await ValidateSdkRequestLogging();
            }
        }
        finally
        {
            AscNet.Common.Common.config.VerboseLevel = previousVerbosity;
        }
        string? previous = Environment.GetEnvironmentVariable("ASCNET_PUBLIC_HTTP_ORIGIN");
        Environment.SetEnvironmentVariable("ASCNET_PUBLIC_HTTP_ORIGIN", "http://127.0.0.1:8080");
        await using WebApplication app = CreateKuroSdkTestApp();
        try
        {
            await app.StartAsync();
            using HttpClient client = new() { BaseAddress = new Uri(BoundAddress(app)) };
            // Exercise body-only, header-only and forwarded-host identification separately.
            foreach (string source in new[] { "body", "headers", "host" })
            {
                using HttpRequestMessage request = new(HttpMethod.Post, "/sdkcom/v2/sys/conf.lg");
                request.Content = new FormUrlEncodedContent(source == "body"
                    ? new Dictionary<string, string> { ["pkg"] = "com.kurogame.haru.hero", ["platform"] = "PC" }
                    : new Dictionary<string, string>());
                if (source == "headers")
                {
                    request.Headers.Add("Kr-ProjectId", "G148");
                    request.Headers.Add("Kr-ProductId", "A1393");
                }
                if (source == "host")
                    request.Headers.Add("X-Forwarded-Host", "sdkapi.kurogame.com");
                using HttpResponseMessage response = await client.SendAsync(request);
                response.EnsureSuccessStatusCode();
                JObject data = (JObject)JObject.Parse(await response.Content.ReadAsStringAsync())["data"]!;
                AssertCnThirdLogin(data);
                string link = data["link"]![0]!["url"]!.Value<string>()!;
                AssertEqual("?region=cn", new Uri(link).Query, "CN follow-up config region");
                JObject followUp = JObject.Parse(await client.GetStringAsync(new Uri(link).PathAndQuery));
                AssertCnThirdLogin(followUp);
            }
            JObject legacy = JObject.Parse(await client.GetStringAsync("/sdkcom/v2/sys/player-config.json"));
            AssertEqual(JTokenType.Integer, legacy["thirdLogin"]!.Type, "unmarked config retains EN/TW contract");
            using HttpResponseMessage invalidAuto = await client.PostAsync("/sdkcom/v2/login/auto.lg?region=cn", new FormUrlEncodedContent(new Dictionary<string, string>()));
            AssertEqual(-1, JObject.Parse(await invalidAuto.Content.ReadAsStringAsync()).Value<int>("code"), "CN missing auto token rejected");
        }
        finally
        {
            await app.StopAsync();
            Environment.SetEnvironmentVariable("ASCNET_PUBLIC_HTTP_ORIGIN", previous);
        }
    }

    private static void AssertCnThirdLogin(JObject data)
    {
        JObject third = RequiredObject(data, "thirdLogin", "CN config");
        AssertEqual(1, third["accLogin"]!.Value<int>("enabled"), "CN account login enabled");
        AssertEqual(0, third["accReg"]!.Value<int>("enabled"), "CN separate registration entry disabled");
        AssertEqual(1, third["phone"]!.Value<int>("enabled"), "CN reference phone/account UI switch enabled");
    }

    private static async Task ValidateCnSdkLogin()
    {
        List<Account> accounts = [];
        string? previousFallback = Environment.GetEnvironmentVariable("ASCNET_GATE_FALLBACK_USERNAME");
        await using WebApplication app = CreateKuroSdkTestApp();
        AscNet.SDKServer.Controllers.AccountController.Register(app);
        try
        {
            await app.StartAsync();
            using HttpClient client = new() { BaseAddress = new Uri(BoundAddress(app)) };
            async Task<JObject> Post(string endpoint, Dictionary<string, string> fields)
            {
                fields["pkg"] = "com.kurogame.haru.hero";
                using HttpResponseMessage response = await client.PostAsync(endpoint, new FormUrlEncodedContent(fields));
                response.EnsureSuccessStatusCode();
                return JObject.Parse(await response.Content.ReadAsStringAsync());
            }
            for (int i = 0; i < 2; i++)
            {
                string suffix = Guid.NewGuid().ToString("N");
                string username = $"cn-sdk-test-{suffix}";
                AssertEqual<Account?>(null, Account.FromUsername(username), "first login starts without an account");
                JObject login = await Post("/sdkcom/v2/login/accLogin.lg", new() { ["loginName"] = username, ["password"] = suffix, ["loginType"] = "7" });
                Account account = Account.FromUsername(username)
                    ?? throw new InvalidDataException("CN first login did not persist an account.");
                accounts.Add(account);
                AssertEqual(0, login.Value<int>("code"), "CN login success");
                AssertEqual(suffix, account.Password, "CN submitted credential persisted");
                JObject repeated = await Post("/sdkcom/v2/login/accLogin.lg", new() { ["loginName"] = username, ["password"] = suffix, ["loginType"] = "7" });
                AssertEqual(0, repeated.Value<int>("code"), "CN existing account login success");
                AssertEqual(account.Uid, repeated["data"]!.Value<long>("id"), "repeated login retains account identity");
                AssertEqual(1L, Account.collection.CountDocuments(x => x.Username == username), "repeated login does not duplicate account");
                JObject data = (JObject)login["data"]!;
                AssertEqual(account.Uid, data.Value<long>("id"), "CN persisted identity");
                AssertEqual(account.Token, data.Value<string>("code"), "CN OAuth identity");
                foreach (string field in new[] { "token", "accessToken" })
                    AssertEqual(account.Token, data.Value<string>(field), $"CN {field} shares persistent token");
                AssertEqual(7, data.Value<int>("loginType"), "CN requested login type");
                string auto = repeated["data"]!.Value<string>("autoToken")!;
                JObject resumed = await Post("/sdkcom/v2/login/auto.lg", new() { ["autoToken"] = auto });
                AssertEqual(account.Uid, resumed["data"]!.Value<long>("id"), "CN auto login identity");
                AssertEqual(-1, (await Post("/sdkcom/v2/login/accLogin.lg", new() { ["loginName"] = account.Username, ["password"] = "wrong" })).Value<int>("code"), "wrong password rejected");
                AssertEqual(suffix, Account.FromUsername(username)!.Password, "failed login does not overwrite password");
                JObject access = await Post("/sdkcom/v2/auth/getToken.lg", new() { ["code"] = account.Token });
                AssertEqual(account.Token, access.Value<string>("access_token"), "CN access token identity");
                JObject oauth = await Post("/sdkcom/v2/user/oauth/code/generate.lg", new() { ["access_token"] = account.Token });
                AssertEqual(account.Token, oauth["data"]!.Value<string>("oauthCode"), "CN OAuth code identity");
                Player player = Player.FromPlayerId(account.Uid);
                JObject validGate = JObject.Parse(await client.GetStringAsync($"/api/Login/Login-cn?loginType=7&userId={account.Uid}&token={account.Token}"));
                AssertEqual(0, validGate.Value<int>("code"), "CN valid gate login");
                AssertEqual(player.Token, validGate.Value<string>("token"), "CN gate player identity");
                AssertEqual(account.Token, resumed["data"]!.Value<string>("token"), "auto login does not rotate account token");
                JObject resumedAgain = await Post("/sdkcom/v2/login/auto.lg", new() { ["token"] = account.Token });
                AssertEqual(account.Uid, resumedAgain["data"]!.Value<long>("id"), "original token remains reusable");
                JObject oldWrapper = await Post("/sdkcom/v2/login/auto.lg", new() { ["token"] = $"0.{account.Token}.1" });
                AssertEqual(account.Uid, oldWrapper["data"]!.Value<long>("id"), "SDK wrapper timestamp does not expire account token");
            }
            AssertEqual(-1, (await Post("/sdkcom/v2/login/accLogin.lg", new() { ["loginName"] = "", ["password"] = "" })).Value<int>("code"), "empty credentials rejected");
            AssertEqual(-1, (await Post("/sdkcom/v2/login/auto.lg", new() { ["token"] = "unknown" })).Value<int>("code"), "unknown token rejected");
            Environment.SetEnvironmentVariable("ASCNET_GATE_FALLBACK_USERNAME", accounts[0].Username);
            JObject gate = JObject.Parse(await client.GetStringAsync("/api/Login/Login-cn?loginType=0&userId=0&token=unknown"));
            AssertEqual(13, gate.Value<int>("code"), "CN never maps unknown token to Steam fallback");
        }
        finally
        {
            await app.StopAsync();
            foreach (Account account in accounts)
            {
                Player.collection.DeleteOne(x => x.PlayerData.Id == account.Uid);
                Account.collection.DeleteOne(x => x.Id == account.Id);
            }
            Environment.SetEnvironmentVariable("ASCNET_GATE_FALLBACK_USERNAME", previousFallback);
        }
    }

    private static void ValidateLegacyCnAccountDocument()
    {
        var document = new MongoDB.Bson.BsonDocument
        {
            ["_id"] = MongoDB.Bson.ObjectId.GenerateNewId(), ["uid"] = 42L,
            ["username"] = "legacy-cn", ["password"] = "submitted-sdk-password",
            ["token"] = "persistent-test-token", ["cn_auto_token"] = "obsolete-token",
            ["cn_auto_token_expires_at"] = 1L
        };
        Account account = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<Account>(document);
        AssertEqual("persistent-test-token", account.Token, "legacy document ignores obsolete auto token");
        AssertEqual(42L, account.Uid, "legacy document retains identity");
        document.Remove("cn_auto_token");
        document.Remove("cn_auto_token_expires_at");
        Account withoutExtraFields = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<Account>(document);
        AssertEqual(account.Token, withoutExtraFields.Token, "account without obsolete fields uses same token");
    }

    private static async Task ValidateCnLoginResponseSerialization()
    {
        await using WebApplication app = CreateKuroSdkTestApp();
        Type controller = Type.GetType("AscNet.SDKServer.Controllers.KuroSdkController, AscNet.SDKServer", throwOnError: true)!;
        var method = controller.GetMethod("CnLoginResponse", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var unwrap = controller.GetMethod("CnAutoTokenAccessToken", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        foreach (string token in new[] { "first-token", "second-token" })
        {
            AssertEqual(token, (string?)unwrap.Invoke(null, [token]), "bare account token accepted");
            AssertEqual(token, (string?)unwrap.Invoke(null, [$"0.{token}.1"]), "wrapped token accepted without expiry check");
        }
        foreach (string malformed in new[] { "0..1", "1.token.1", "0.token.invalid", "0.token.1.extra" })
            AssertEqual<string?>(null, (string?)unwrap.Invoke(null, [malformed]), "malformed token wrapper rejected");
        app.MapGet("/test/cn-login-response", (Microsoft.AspNetCore.Http.HttpContext ctx) =>
        {
            long uid = long.Parse(ctx.Request.Query["uid"].ToString());
            Account account = new()
            {
                Uid = uid, Username = $"serialization-{uid}", Password = "unused",
                Token = $"synthetic-token-{uid}"
            };
            return (Microsoft.AspNetCore.Http.IResult)method.Invoke(null, [ctx, account])!;
        });
        try
        {
            await app.StartAsync();
            using HttpClient client = new() { BaseAddress = new Uri(BoundAddress(app)) };
            foreach (long uid in new[] { 17L, 29L })
            {
                long before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                string loginTypeQuery = uid == 17 ? "&loginType=7" : "";
                using HttpResponseMessage response = await client.GetAsync($"/test/cn-login-response?uid={uid}{loginTypeQuery}");
                AssertEqual(HttpStatusCode.OK, response.StatusCode, "CN login response serializes over HTTP");
                JObject payload = JObject.Parse(await response.Content.ReadAsStringAsync());
                AssertEqual(0, payload.Value<int>("code"), "CN serialized success code");
                JObject data = (JObject)payload["data"]!;
                AssertEqual(uid, data.Value<long>("id"), "CN serialized UID");
                foreach (string name in new[] { "sdkuserid", "sdkUserId" })
                    AssertEqual(uid.ToString(), data.Value<string>(name), $"CN exact-case {name} retained");
                foreach (string name in new[] { "code", "token", "accessToken" })
                    AssertEqual($"synthetic-token-{uid}", data.Value<string>(name), $"CN serialized {name}");
                string[] autoParts = data.Value<string>("autoToken")!.Split('.');
                AssertEqual(3, autoParts.Length, "CN SDK auto token wrapper shape");
                AssertEqual("0", autoParts[0], "CN SDK wrapper prefix");
                AssertEqual($"synthetic-token-{uid}", autoParts[1], "CN SDK wrapper account identity");
                long expiryHint = long.Parse(autoParts[2]);
                if (expiryHint < before + 30 * 24 * 60 * 60 || expiryHint > DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 30 * 24 * 60 * 60)
                    throw new InvalidDataException("CN wrapper timestamp was not derived from the current clock.");
                AssertEqual(21, data.Value<int>("age"), "CN SDK compatibility age contract");
                AssertEqual(false, data.Value<bool>("showPaw"), "CN showPaw contract");
                AssertEqual(0, data.Value<int>("phoneCheck"), "CN phoneCheck contract");
                AssertEqual($"serialization-{uid}", data.Value<string>("phone"), "CN phone identity follows account");
                AssertEqual(uid == 17 ? 7 : 0, data.Value<int>("loginType"), "CN requested or reference default login type");
            }
            using HttpResponseMessage heartbeat = await client.PostAsync("/sdkcom/v2/heartbeat/switchStatus.lg?region=cn", new FormUrlEncodedContent(new Dictionary<string, string>()));
            AssertEqual(HttpStatusCode.OK, heartbeat.StatusCode, "CN switchStatus route");
            AssertEqual(0, JObject.Parse(await heartbeat.Content.ReadAsStringAsync()).Value<int>("code"), "CN switchStatus success envelope");
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static async Task ValidateSdkRequestLogging()
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Scheme = "http";
        context.Request.Host = new Microsoft.AspNetCore.Http.HostString("localhost");
        context.Request.Path = "/sdkcom/v2/login/accLogin.lg";
        context.Request.ContentType = "application/x-www-form-urlencoded";
        string body = "loginName=diagnostic-user&password=raw-password";
        context.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body));
        var middleware = new AscNet.SDKServer.SDKServer.RequestLoggingMiddleware(async ctx =>
        {
            var form = await ctx.Request.ReadFormAsync();
            AssertEqual("raw-password", form["password"].ToString(), "logging preserves handler body");
            ctx.Items["AscNet.LoginStage"] = "synthetic-handler";
            throw new InvalidOperationException("synthetic diagnostic failure", new ArgumentException("synthetic inner failure"));
        });
        try
        {
            await middleware.Invoke(context);
            throw new InvalidDataException("SDK middleware swallowed handler exception.");
        }
        catch (InvalidOperationException ex) when (ex.Message == "synthetic diagnostic failure")
        {
            AssertEqual(500, context.Response.StatusCode, "exception is not reported as HTTP 200");
        }
    }

    private static async Task ValidateCnGateUrlBinding()
    {
        await using WebApplication app = CreateGateLoginTestApp();
        try
        {
            await app.StartAsync();
            using HttpClient client = new() { BaseAddress = new Uri(BoundAddress(app)) };
            foreach (string path in new[] { "/api/Login/Login-cn", "/api/Login/Login" })
            {
                // Whitespace credentials avoid a database dependency while exercising real route binding.
                using HttpResponseMessage response = await client.GetAsync(path + "?loginType=5&userId=1&token=%20&pkgId=test");
                AssertEqual(HttpStatusCode.OK, response.StatusCode, "client-appended gate query binds correctly");
                AssertEqual(13, JObject.Parse(await response.Content.ReadAsStringAsync()).Value<int>("code"), "blank gate credential rejected by handler");
                using HttpResponseMessage missingType = await client.GetAsync(path + "?userId=1&token=%20");
                AssertEqual(HttpStatusCode.BadRequest, missingType.StatusCode, "gate still requires loginType");
            }
        }
        finally
        {
            await app.StopAsync();
        }
    }
}
