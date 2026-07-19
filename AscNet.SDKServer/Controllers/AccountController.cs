using System.Text;
using AscNet.Common.Database;
using AscNet.SDKServer.Models;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;

namespace AscNet.SDKServer.Controllers
{
    public class AccountController : IRegisterable
    {
        private const string GateFallbackUsernameEnv = "ASCNET_GATE_FALLBACK_USERNAME";
        public static void Register(WebApplication app)
        {
            app.MapPost("/api/AscNet/register", (HttpContext ctx) =>
            {
                AuthRequest? req = JsonConvert.DeserializeObject<AuthRequest>(Encoding.UTF8.GetString(ctx.Request.BodyReader.ReadAsync().Result.Buffer));

                if (req is null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        code = -1,
                        msg = "Invalid request"
                    });
                }

                try
                {
                    Account account = Account.Create(req.Username, req.Password);

                    return JsonConvert.SerializeObject(new
                    {
                        code = 0,
                        msg = "OK",
                        account
                    });
                }
                catch (Exception ex)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        code = -1,
                        msg = ex.Message
                    });
                }
            });

            app.MapPost("/api/AscNet/login", (HttpContext ctx) =>
            {
                AuthRequest? req = JsonConvert.DeserializeObject<AuthRequest>(Encoding.UTF8.GetString(ctx.Request.BodyReader.ReadAsync().Result.Buffer));

                if (req is null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        code = -1,
                        msg = "Invalid request"
                    });
                }

                Account? account = Account.FromUsername(req.Username, req.Password);

                if (account == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        code = -1,
                        msg = "Invalid credentials!"
                    });
                }

                return JsonConvert.SerializeObject(new
                {
                    code = 0,
                    msg = "OK",
                    account
                });
            });

            app.MapPost("/api/AscNet/verify", (HttpContext ctx) =>
            {
                AuthRequest? req = JsonConvert.DeserializeObject<AuthRequest>(Encoding.UTF8.GetString(ctx.Request.BodyReader.ReadAsync().Result.Buffer));

                if (req is null || req.Token == string.Empty)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        code = -1,
                        msg = "Invalid request"
                    });
                }

                Account? account = Account.FromToken(req.Token);

                if (account == null)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        code = -1,
                        msg = "Invalid credentials!"
                    });
                }

                return JsonConvert.SerializeObject(new
                {
                    code = 0,
                    msg = "OK",
                    account
                });
            });

            app.MapGet("/api/Login/Login", ([FromQuery] int loginType, [FromQuery] int userId, [FromQuery] string token, [FromQuery] string? clientIp) =>
            {
                try
                {
                    Account? account = Account.FromToken(token);

                    if (account is null)
                        account = GateFallbackAccount(loginType, userId);

                    if (account is null)
                        return InvalidLoginToken();

                    Player player = Player.FromPlayerId(account.Uid);

                    LoginGate gate = new()
                    {
                        Code = 0,
                        Ip = GameServerTcpHost(),
                        Port = Common.Common.config.GameServer.Port,
                        Token = player.Token
                    };

                    return JsonConvert.SerializeObject(gate);
                }
                catch (Exception ex)
                {
                    SDKServer.log.Error($"Gate login lookup failed: {ex.Message}");
                    return InvalidLoginToken();
                }
            });
        }

        private static string InvalidLoginToken()
        {
            return JsonConvert.SerializeObject(new LoginGate
            {
                Code = 13
            });
        }

        private static string GameServerTcpHost()
        {
            string host = Common.Common.config.GameServer.Host.TrimEnd('/');
            if (Uri.TryCreate(host, UriKind.Absolute, out Uri? uri) && !string.IsNullOrEmpty(uri.Host))
                return uri.Host;

            return host;
        }

        private static Account? GateFallbackAccount(int loginType, int userId)
        {
            string? fallbackUsername = Environment.GetEnvironmentVariable(GateFallbackUsernameEnv);
            if (string.IsNullOrWhiteSpace(fallbackUsername))
                return null;

            Account? account = Account.FromUsername(fallbackUsername);
            if (account is not null)
                SDKServer.log.Warn($"Gate login fallback mapped loginType={loginType} userId={userId} to local account '{fallbackUsername}'.");

            return account;
        }
    }
}
