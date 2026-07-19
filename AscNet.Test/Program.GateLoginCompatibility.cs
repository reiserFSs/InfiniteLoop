using AscNet.Common.Database;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Newtonsoft.Json.Linq;
using System.Net;

namespace AscNet.Test
{
    internal partial class Program
    {
        private static async Task ValidateGateLoginCompatibility()
        {
            string? previousFallbackUsername = Environment.GetEnvironmentVariable("ASCNET_GATE_FALLBACK_USERNAME");
            Account? account = null;

            try
            {
                Environment.SetEnvironmentVariable("ASCNET_GATE_FALLBACK_USERNAME", null);
                string suffix = Guid.NewGuid().ToString("N");
                account = Account.Create($"gate-login-compat-{suffix}", suffix);
                Player player = Player.FromPlayerId(account.Uid);

                await using WebApplication app = CreateGateLoginTestApp();
                await app.StartAsync();
                try
                {
                    using HttpClient client = new() { BaseAddress = new Uri(BoundAddress(app)) };
                    await AssertGateLoginCode(client, account.Uid, $"unknown-external-token-{suffix}", 13, "unknown external token");
                    await AssertGateLoginCode(client, account.Uid, account.Token, 0, "local account token", player.Token);
                }
                finally
                {
                    await app.StopAsync();
                }
            }
            finally
            {
                if (account is not null)
                {
                    Player.collection.DeleteOne(player => player.PlayerData.Id == account.Uid);
                    Account.collection.DeleteOne(candidate => candidate.Id == account.Id);
                }

                Environment.SetEnvironmentVariable("ASCNET_GATE_FALLBACK_USERNAME", previousFallbackUsername);
            }
        }

        private static WebApplication CreateGateLoginTestApp()
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = ["--urls", "http://127.0.0.1:0"]
            });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");

            WebApplication app = builder.Build();
            AscNet.SDKServer.Controllers.AccountController.Register(app);
            return app;
        }

        private static async Task AssertGateLoginCode(HttpClient client, long userId, string token, long expectedCode, string name, string? expectedPlayerToken = null)
        {
            string endpoint = $"/api/Login/Login?loginType=0&userId={userId}&token={Uri.EscapeDataString(token)}";
            using HttpResponseMessage response = await client.GetAsync(endpoint);
            string body = await response.Content.ReadAsStringAsync();
            if (response.StatusCode != HttpStatusCode.OK)
                throw new InvalidDataException($"Gate login {name}: expected HTTP 200, got {(int)response.StatusCode} {response.StatusCode}. Body: {body}");

            JObject gate = JObject.Parse(body);
            AssertEqual(expectedCode, gate.Value<long>("code"), $"Gate login {name} code");
            if (expectedPlayerToken is not null)
                AssertEqual(expectedPlayerToken, gate.Value<string>("token"), $"Gate login {name} player token");
        }
    }
}
