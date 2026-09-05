using System.Reflection;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.SDKServer.Models;
using Newtonsoft.Json;

namespace AscNet.SDKServer.Controllers
{
    internal class LauncherController : IRegisterable
    {
        private static readonly Dictionary<string, ServerVersionConfig> versions =
            JsonConvert.DeserializeObject<Dictionary<string, ServerVersionConfig>>(
                File.ReadAllText(JsonSnapshot.ResolvePath("Configs/version_config.json")))!;

        public static void Register(WebApplication app)
        {
            app.MapGet("/api/launcher/status", () =>
            {
                var policy = Common.Common.config.Launcher;
                bool maintenance = policy.Maintenance;

                return Results.Json(new
                {
                    schemaVersion = 1,
                    serverVersion = typeof(SDKServer).Assembly
                        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                        ?? typeof(SDKServer).Assembly.GetName().Version?.ToString()
                        ?? "unknown",
                    online = Server.Instance.IsListening && !maintenance,
                    maintenance,
                    message = policy.Message ?? "",
                    minimumPatchVersion = policy.MinimumPatchVersion,
                    minimumLauncherVersion = policy.MinimumLauncherVersion,
                    supportedClients = versions.Select(version => new
                    {
                        applicationVersion = version.Key,
                        documentVersion = version.Value.DocumentVersion,
                        launchModuleVersion = version.Value.LaunchModuleVersion
                    })
                });
            });
        }
    }
}
