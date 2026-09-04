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
            app.MapPost("/api/AscNet/register", async (HttpContext ctx) =>
            {
                AuthRequest? req = await ReadAuthRequest(ctx);

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

                    return AccountResponse(account);
                }
                catch (ArgumentException)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        code = -1,
                        msg = "Username is already registered!"
                    });
                }
            });

            app.MapPost("/api/AscNet/login", async (HttpContext ctx) =>
            {
                AuthRequest? req = await ReadAuthRequest(ctx);

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

                return AccountResponse(account);
            });

            app.MapPost("/api/AscNet/verify", async (HttpContext ctx) =>
            {
                AuthRequest? req = await ReadAuthRequest(ctx, verify: true);

                if (req is null)
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

                return AccountResponse(account);
            });

            app.MapGet("/api/Login/Login", ([FromQuery] int loginType, [FromQuery] int userId, [FromQuery] string token, [FromQuery] string? clientIp) =>
            {
                try
                {
                    Account? account = Account.FromToken(token);

                    if (account is null)
                        account = GateFallbackAccount();

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
                    SDKServer.log.Error($"Gate login lookup failed: {ex.GetType().Name}");
                    return InvalidLoginToken();
                }
            });
        }

        // Engineering limit, not a retail protocol constant: the runner sends only username/password,
        // and AuthRequest adds only a token (generated as a 36-character GUID). 16 KiB leaves ample
        // room for escaped/unicode credentials while bounding both known-length and chunked input.
        private const int MaxAuthBodyBytes = 16 * 1024;

        private static async Task<AuthRequest?> ReadAuthRequest(HttpContext context, bool verify = false)
        {
            context.RequestAborted.ThrowIfCancellationRequested();
            if (context.Request.ContentLength > MaxAuthBodyBytes)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return null;
            }

            byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(MaxAuthBodyBytes + 1);
            try
            {
                int length = 0;
                int read;
                while ((read = await context.Request.Body.ReadAsync(
                    buffer.AsMemory(length, MaxAuthBodyBytes + 1 - length), context.RequestAborted)) != 0)
                {
                    length += read;
                    if (length > MaxAuthBodyBytes)
                    {
                        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                        return null;
                    }
                }

                context.RequestAborted.ThrowIfCancellationRequested();
                AuthRequest? request = JsonConvert.DeserializeObject<AuthRequest>(Encoding.UTF8.GetString(buffer, 0, length));
                if (request is not null && (verify
                    ? !string.IsNullOrEmpty(request.Token)
                    : !string.IsNullOrEmpty(request.Username) && !string.IsNullOrEmpty(request.Password)))
                    return request;
            }
            catch (JsonException)
            {
                // Do not expose parser messages: they can contain credential-bearing input.
            }
            catch (BadHttpRequestException ex) when (ex.StatusCode is 400 or 413)
            {
                context.Response.StatusCode = ex.StatusCode;
                return null;
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }

            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return null;
        }

        private static string AccountResponse(Account account) => JsonConvert.SerializeObject(new
        {
            code = 0,
            msg = "OK",
            account = new { account.Id, account.Uid, account.Username, account.Token }
        });

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

        private static Account? GateFallbackAccount()
        {
            string? fallbackUsername = Environment.GetEnvironmentVariable(GateFallbackUsernameEnv);
            if (string.IsNullOrWhiteSpace(fallbackUsername))
                return null;

            Account? account = Account.FromUsername(fallbackUsername);
            if (account is not null)
                SDKServer.log.Warn("Gate login fallback mapped to the configured local account.");

            return account;
        }
    }
}
