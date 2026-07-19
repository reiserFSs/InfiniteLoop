using AscNet.Common.Util;
using AscNet.SDKServer.Models;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AscNet.SDKServer.Controllers
{
    internal class ConfigController : IRegisterable
    {
        private static readonly Dictionary<string, ServerVersionConfig> versions = new();

        static ConfigController()
        {
            versions = JsonConvert.DeserializeObject<Dictionary<string, ServerVersionConfig>>(File.ReadAllText(JsonSnapshot.ResolvePath("Configs/version_config.json")))!;
        }

        public static void Register(WebApplication app)
        {
            app.MapGet("/prod/client/config/{package}/{version}/standalone/config.tab", HandleConfigRequest);
            app.MapGet("/prod/client/config/{cdnKey}/{package}/{version}/standalone/config.tab", HandleConfigRequest);

            app.MapGet("/prod/client/notice/config/{package}/{version}/LoginNotice.json", HandleLoginNoticeRequest);
            app.MapGet("/prod/client/notice/config/{cdnKey}/{package}/{version}/LoginNotice.json", HandleLoginNoticeRequest);
            app.MapGet("/prod/client/notice/{package}/{version}/standalone/LoginNotice.json", HandleLoginNoticeRequest);
            app.MapGet("/prod/client/notice/{cdnKey}/{package}/{version}/standalone/LoginNotice.json", HandleLoginNoticeRequest);
            app.MapGet("/prod/client/notice/config/{package}/{version}/ScrollTextNotice.json", HandleScrollTextNoticeRequest);
            app.MapGet("/prod/client/notice/config/{cdnKey}/{package}/{version}/ScrollTextNotice.json", HandleScrollTextNoticeRequest);
            app.MapGet("/prod/client/notice/{package}/{version}/standalone/ScrollTextNotice.json", HandleScrollTextNoticeRequest);
            app.MapGet("/prod/client/notice/{cdnKey}/{package}/{version}/standalone/ScrollTextNotice.json", HandleScrollTextNoticeRequest);
            app.MapGet("/prod/client/notice/config/{package}/{version}/ScrollPicNotice.json", HandleScrollPicNoticeRequest);
            app.MapGet("/prod/client/notice/config/{cdnKey}/{package}/{version}/ScrollPicNotice.json", HandleScrollPicNoticeRequest);
            app.MapGet("/prod/client/notice/{package}/{version}/standalone/ScrollPicNotice.json", HandleScrollPicNoticeRequest);
            app.MapGet("/prod/client/notice/{cdnKey}/{package}/{version}/standalone/ScrollPicNotice.json", HandleScrollPicNoticeRequest);
            app.MapGet("/prod/client/notice/config/{package}/{version}/GameNotice.json", HandleGameNoticeRequest);
            app.MapGet("/prod/client/notice/config/{cdnKey}/{package}/{version}/GameNotice.json", HandleGameNoticeRequest);
            app.MapGet("/prod/client/notice/{package}/{version}/standalone/GameNotice.json", HandleGameNoticeRequest);
            app.MapGet("/prod/client/notice/{cdnKey}/{package}/{version}/standalone/GameNotice.json", HandleGameNoticeRequest);
            app.MapGet("/prod/client/notice/config/{package}/{version}/SecondMenuNotice.json", HandleSecondMenuNoticeRequest);
            app.MapGet("/prod/client/notice/config/{cdnKey}/{package}/{version}/SecondMenuNotice.json", HandleSecondMenuNoticeRequest);
            app.MapGet("/prod/client/notice/{package}/{version}/standalone/SecondMenuNotice.json", HandleSecondMenuNoticeRequest);
            app.MapGet("/prod/client/notice/{cdnKey}/{package}/{version}/standalone/SecondMenuNotice.json", HandleSecondMenuNoticeRequest);
            app.MapGet("/prod/client/notice/config/{package}/{version}/PopUpPicNotice.json", HandlePopUpPicNoticeRequest);
            app.MapGet("/prod/client/notice/config/{cdnKey}/{package}/{version}/PopUpPicNotice.json", HandlePopUpPicNoticeRequest);
            app.MapGet("/prod/client/notice/{package}/{version}/standalone/PopUpPicNotice.json", HandlePopUpPicNoticeRequest);
            app.MapGet("/prod/client/notice/{cdnKey}/{package}/{version}/standalone/PopUpPicNotice.json", HandlePopUpPicNoticeRequest);
            app.MapGet("/prod/client/notice/pic/{fileName}", HandleNoticePicRequest);
            app.MapGet("/prod/client/notice/html/{fileName}", HandleNoticeHtmlRequest);


            app.MapPost("/feedback", (HttpContext ctx) =>
            {
                SDKServer.log.Info("1");
                return "1";
            });
        }

        private static string HandleConfigRequest(HttpContext ctx)
        {
            string package = GetRouteValue(ctx, "package");
            string version = GetRouteValue(ctx, "version");
            bool currentClient = IsVersionAtLeast(version, 4, 5, 0);
            string publicHttpOrigin = PublicHttpOrigin(ctx);
            ServerVersionConfig versionConfig = GetVersionConfig(version);

            List<RemoteConfig> remoteConfigs = new();
            if (currentClient)
                AddCurrentClientConfig(remoteConfigs, package, version, versionConfig, publicHttpOrigin);
            else
                AddLegacyClientConfig(remoteConfigs, package, version, versionConfig, publicHttpOrigin);

            string serializedObject = TsvTool.SerializeObject(remoteConfigs);
            SDKServer.log.Info(serializedObject);
            return serializedObject;
        }

        private static string HandleLoginNoticeRequest(HttpContext ctx)
        {
            if (TryReadNoticeFixture(ctx, "LoginNotice.json", out string fixtureJson))
                return fixtureJson;

            return SerializeAndLog(null);
        }

        private static string HandleScrollTextNoticeRequest(HttpContext ctx)
        {
            if (TryReadNoticeFixture(ctx, "ScrollTextNotice.json", out string fixtureJson))
                return fixtureJson;

            return SerializeAndLog(null);
        }

        private static string HandleScrollPicNoticeRequest(HttpContext ctx)
        {
            if (TryReadNoticeFixture(ctx, "ScrollPicNotice.json", out string fixtureJson))
                return fixtureJson;

            return SerializeAndLog(null);
        }

        private static string HandleGameNoticeRequest(HttpContext ctx)
        {
            if (TryReadNoticeFixture(ctx, "GameNotice.json", out string fixtureJson))
                return fixtureJson;

            List<GameNotice> notices = new();
            return SerializeAndLog(notices);
        }

        private static string HandleSecondMenuNoticeRequest(HttpContext ctx)
        {
            if (TryReadNoticeFixture(ctx, "SecondMenuNotice.json", out string fixtureJson))
                return fixtureJson;

            return SerializeAndLog(null);
        }

        private static string HandlePopUpPicNoticeRequest(HttpContext ctx)
        {
            if (TryReadNoticeFixture(ctx, "PopUpPicNotice.json", out string fixtureJson))
                return fixtureJson;

            return SerializeAndLog(null);
        }

        private static IResult HandleNoticePicRequest(HttpContext ctx)
        {
            string fileName = Path.GetFileName(GetRouteValue(ctx, "fileName"));

            foreach (string versionDirectory in NoticeVersionDirectories())
            {
                string path = Path.Combine(versionDirectory, "client", "notice", "pic", fileName);
                if (File.Exists(path))
                {
                    byte[] image = File.ReadAllBytes(path);
                    return Results.File(image, NoticeImageMediaType(image));
                }
            }

            return Results.NotFound();
        }

        private static string NoticeImageMediaType(ReadOnlySpan<byte> image)
        {
            if (image.Length >= 8
                && image[0] == 0x89
                && image[1] == 0x50
                && image[2] == 0x4E
                && image[3] == 0x47
                && image[4] == 0x0D
                && image[5] == 0x0A
                && image[6] == 0x1A
                && image[7] == 0x0A)
            {
                return "image/png";
            }

            if (image.Length >= 12
                && image[..4].SequenceEqual("RIFF"u8)
                && image.Slice(8, 4).SequenceEqual("WEBP"u8))
            {
                return "image/webp";
            }

            return "application/octet-stream";
        }

        private static IResult HandleNoticeHtmlRequest(HttpContext ctx)
        {
            string fileName = Path.GetFileName(GetRouteValue(ctx, "fileName"));
            if (!fileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                return Results.NotFound();

            foreach (string versionDirectory in NoticeVersionDirectories())
            {
                string path = Path.Combine(versionDirectory, "client", "notice", "html", fileName);
                if (File.Exists(path))
                    return Results.File(File.ReadAllBytes(path), "text/html; charset=utf-8");
            }

            if (TryBuildNoticeHtml(fileName, out string html))
                return Results.Text(html, "text/html; charset=utf-8");

            return Results.NotFound();
        }

        private static IEnumerable<string> NoticeVersionDirectories()
        {
            string noticesRoot = JsonSnapshot.ResolveDirectoryPath(Path.Combine("Configs", "Notices"));
            if (!Directory.Exists(noticesRoot))
                return Array.Empty<string>();

            return Directory.GetDirectories(noticesRoot).OrderByDescending(Path.GetFileName);
        }

        private static bool TryGetNoticeVersionDirectory(string version, out string versionDirectory)
        {
            foreach (string candidate in NoticeVersionDirectories())
            {
                if (string.Equals(Path.GetFileName(candidate), version, StringComparison.OrdinalIgnoreCase))
                {
                    versionDirectory = candidate;
                    return true;
                }
            }

            versionDirectory = string.Empty;
            return false;
        }

        private static bool TryBuildNoticeHtml(string fileName, out string html)
        {
            foreach (string versionDirectory in NoticeVersionDirectories())
            {
                if (TryFindLoginNoticeTitle(versionDirectory, fileName, out string title)
                    || TryFindGameNoticeTitle(versionDirectory, fileName, out title))
                {
                    html = BuildNoticeHtml(title);
                    return true;
                }
            }

            html = string.Empty;
            return false;
        }

        private static bool TryFindLoginNoticeTitle(string versionDirectory, string fileName, out string title)
        {
            string path = Path.Combine(versionDirectory, "LoginNotice.json");
            if (File.Exists(path))
            {
                JObject notice = JObject.Parse(File.ReadAllText(path));
                if (string.Equals(Path.GetFileName(notice.Value<string>("HtmlUrl") ?? string.Empty), fileName, StringComparison.OrdinalIgnoreCase))
                {
                    title = notice.Value<string>("Title") ?? "Notice";
                    return true;
                }
            }

            title = string.Empty;
            return false;
        }

        private static bool TryFindGameNoticeTitle(string versionDirectory, string fileName, out string title)
        {
            string path = Path.Combine(versionDirectory, "GameNotice.json");
            if (File.Exists(path))
            {
                foreach (JObject notice in JArray.Parse(File.ReadAllText(path)).OfType<JObject>())
                {
                    foreach (JObject content in (notice.Value<JArray>("Content") ?? new JArray()).OfType<JObject>())
                    {
                        if (string.Equals(Path.GetFileName(content.Value<string>("Url") ?? string.Empty), fileName, StringComparison.OrdinalIgnoreCase))
                        {
                            title = content.Value<string>("Title") ?? notice.Value<string>("Title") ?? "Notice";
                            return true;
                        }
                    }
                }
            }

            title = string.Empty;
            return false;
        }

        private static string BuildNoticeHtml(string title)
        {
            string encodedTitle = System.Net.WebUtility.HtmlEncode(title);
            return $$"""
                <!doctype html>
                <html>
                <head>
                    <meta charset="utf-8">
                    <meta name="viewport" content="width=device-width, initial-scale=1">
                    <title>{{encodedTitle}}</title>
                    <style>
                        body { margin: 0; padding: 24px; background: #10131a; color: #eef3ff; font-family: sans-serif; }
                        main { max-width: 720px; margin: 0 auto; }
                        h1 { font-size: 24px; line-height: 1.25; margin: 0 0 16px; }
                        p { color: #b9c2d5; font-size: 16px; line-height: 1.5; }
                    </style>
                </head>
                <body>
                    <main>
                        <h1>{{encodedTitle}}</h1>
                        <p>Please refer to the in-game notice list for event details.</p>
                    </main>
                </body>
                </html>
                """;
        }

        private static void AddLegacyClientConfig(List<RemoteConfig> remoteConfigs, string package, string version, ServerVersionConfig versionConfig, string publicHttpOrigin)
        {
            (string primaryCdns, string secondaryCdns, int channel) = GetPackageConfig(package, currentClient: false);

            remoteConfigs.AddConfig("DocumentVersion", versionConfig.DocumentVersion);
            remoteConfigs.AddConfig("LaunchModuleVersion", versionConfig.LaunchModuleVersion);
            remoteConfigs.AddConfig("IndexMd5", versionConfig.IndexMd5);
            remoteConfigs.AddConfig("IndexSha1", versionConfig.IndexSha1);
            remoteConfigs.AddConfig("LaunchIndexSha1", versionConfig.LaunchIndexSha1);
            remoteConfigs.AddConfig("ApplicationVersion", version);
            remoteConfigs.AddConfig("Debug", true);
            remoteConfigs.AddConfig("External", true);
            remoteConfigs.AddConfig("PayCallbackUrl", $"{publicHttpOrigin}/api/XPay/KuroPayResult");
            remoteConfigs.AddConfig("PrimaryCdns", primaryCdns);
            remoteConfigs.AddConfig("SecondaryCdns", secondaryCdns);
            remoteConfigs.AddConfig("Channel", channel);
            remoteConfigs.AddConfig("CdnInvalidTime", 60);
            remoteConfigs.AddConfig("MtpEnabled", false);
            remoteConfigs.AddConfig("MemoryLimit", 2048);
            remoteConfigs.AddConfig("CloseMsgEncrypt", false);
            remoteConfigs.AddConfig("ServerListStr", $"{Common.Common.config.GameServer.RegionName}#{publicHttpOrigin}/api/Login/Login");
            remoteConfigs.AddConfig("AndroidPayCallbackUrl", $"{publicHttpOrigin}/api/XPay/HeroHgAndroidPayResult");
            remoteConfigs.AddConfig("IosPayCallbackUrl", $"{publicHttpOrigin}/api/XPay/HeroHgIosPayResult");
            remoteConfigs.AddConfig("WatermarkEnabled", false);
            remoteConfigs.AddConfig("PicComposition", "empty");
            remoteConfigs.AddConfig("DeepLinkEnabled", true);
            remoteConfigs.AddConfig("DownloadMethod", 1);
            remoteConfigs.AddConfig("PcPayCallbackList", $"{publicHttpOrigin}/api/XPay/KuroPayResult");
            remoteConfigs.AddConfig("WatermarkType", 2);
            remoteConfigs.AddConfig("ChannelServerListStr", $"1#{Common.Common.config.GameServer.RegionName}#{publicHttpOrigin}/api/Login/Login");
            remoteConfigs.AddConfig("IsHideFunc", false);
            remoteConfigs.AddConfig("IsHideFuncAndroid", false);
        }

        private static void AddCurrentClientConfig(List<RemoteConfig> remoteConfigs, string package, string version, ServerVersionConfig versionConfig, string publicHttpOrigin)
        {
            (string primaryCdns, string secondaryCdns, int channel) = GetPackageConfig(package, currentClient: true);

            remoteConfigs.AddConfig("ApplicationVersion", version);
            remoteConfigs.AddConfig("DocumentVersion", versionConfig.DocumentVersion);
            remoteConfigs.AddConfig("Debug", false);
            remoteConfigs.AddConfig("External", true);
            remoteConfigs.AddConfig("Channel", channel);
            remoteConfigs.AddConfig("PayCallbackUrl", $"{publicHttpOrigin}/api/XPay/KuroPayResult");
            remoteConfigs.AddConfig("KuroPayCallbackUrl", $"{publicHttpOrigin}/api/XPay/KuroPayResult");
            remoteConfigs.AddConfig("PrimaryCdns", primaryCdns);
            remoteConfigs.AddConfig("SecondaryCdns", secondaryCdns);
            remoteConfigs.AddConfig("CdnInvalidTime", 600);
            remoteConfigs.AddConfig("MtpEnabled", true);
            remoteConfigs.AddConfig("MemoryLimit", 2048);
            remoteConfigs.AddConfig("CloseMsgEncrypt", false);
            remoteConfigs.AddConfig("ServerListStr", CurrentServerListStr(publicHttpOrigin));
            remoteConfigs.AddConfig("IndexMd5", versionConfig.IndexMd5);
            remoteConfigs.AddConfig("AndroidReturnEnabled", false);
            remoteConfigs.AddConfig("AndroidPayCallbackList", $"{publicHttpOrigin}/api/XPay/HeroHgAndroidPayResult");
            remoteConfigs.AddConfig("AndroidPayCallbackUrl", $"{publicHttpOrigin}/api/XPay/HeroHgAndroidPayResult");
            remoteConfigs.AddConfig("IosPayCallbackUrl", $"{publicHttpOrigin}/api/XPay/HeroHgIosPayResult");
            remoteConfigs.AddConfig("DEEnable", true);
            remoteConfigs.AddConfig("DEFilter", "empty");
            remoteConfigs.AddConfig("IndexSha1", versionConfig.IndexSha1);
            remoteConfigs.AddConfig("WatermarkEnabled", false);
            remoteConfigs.AddConfig("PicComposition", "empty");
            remoteConfigs.AddConfig("IosPayCallbackList", $"{publicHttpOrigin}/api/XPay/HeroHgIosPayResult");
            remoteConfigs.AddConfig("LaunchModuleVersion", versionConfig.LaunchModuleVersion);
            remoteConfigs.AddConfig("LaunchIndexSha1", versionConfig.LaunchIndexSha1);
            remoteConfigs.AddConfig("DeepLinkEnabled", true);
            remoteConfigs.AddConfig("AccountCancellationEnable", false);
            remoteConfigs.AddConfig("DownloadMethod", 1);
            remoteConfigs.AddConfig("PcPayCallbackList", $"{publicHttpOrigin}/api/XPay/KuroPayResult");
            remoteConfigs.AddConfig("PcPayCallbackUrl", $"{publicHttpOrigin}/api/XPay/KuroPayResult");
            remoteConfigs.AddConfig("ParallelDownload", 1);
            remoteConfigs.AddConfig("ParallelQueueSize", "3-7");
            remoteConfigs.AddConfig("WatermarkType", 0);
            remoteConfigs.AddConfig("IsPCPayEnable", true);
            remoteConfigs.AddConfig("ChannelServerListStr", CurrentChannelServerListStr(publicHttpOrigin));
            remoteConfigs.AddConfig("IsHeXie", false);
            remoteConfigs.AddConfig("IsHideFunc", false);
            remoteConfigs.AddConfig("IsHideFuncAndroid", false);
            remoteConfigs.AddConfig("UsingXTableBehaviorNodeOptimize", true);
            remoteConfigs.AddConfig("IsUsingCDNAuth", false);
            remoteConfigs.AddConfig("AuthSignName", "sign");
            remoteConfigs.AddConfig("AuthTimeOut", 1800);
            remoteConfigs.AddConfig("AuthIsVolKey", "volcdn");
        }

        private static string CurrentServerListStr(string publicHttpOrigin)
        {
            string loginUrl = $"{publicHttpOrigin}/api/Login/Login";
            return $"NorthAmerica#{loginUrl}|Europe#{loginUrl}|Asia-Pacific#{loginUrl}";
        }

        private static string CurrentChannelServerListStr(string publicHttpOrigin)
        {
            return $"default#Globle#{publicHttpOrigin}/api/Login/Login";
        }

        private static (string PrimaryCdns, string SecondaryCdns, int Channel) GetPackageConfig(string package, bool currentClient)
        {
            return package switch
            {
                "com.kurogame.haru.kuro" => (
                    "http://prod-zspnsalicdn.kurogame.com/prod",
                    "http://prod-zspnstxcdn.kurogame.com/prod",
                    2),
                "com.kurogame.punishing.grayraven.en" or "com.kurogame.gplay.punishing.grayraven.en" when currentClient => (
                    "http://prod-encdn-tx.kurogame.net/prod",
                    "http://prod-encdn-aliyun.kurogame.net/prod",
                    5),
                "com.kurogame.pc.punishing.grayraven.en" when currentClient => (
                    "http://prod-encdn-tx.kurogame.net/prod",
                    "http://prod-encdn-aliyun.kurogame.net/prod",
                    205),
                _ => (
                    "http://prod-encdn-akamai.kurogame.net/prod|http://prod-encdn-aliyun.kurogame.net/prod",
                    "http://prod-encdn-aliyun.kurogame.net/prod",
                    1)
            };
        }

        private static string? FirstForwardedValue(Microsoft.Extensions.Primitives.StringValues values)
        {
            if (values.Count < 1 || string.IsNullOrWhiteSpace(values[0]))
                return null;

            return values[0]!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        }

        private static string PublicHttpOrigin(HttpContext ctx)
        {
            string scheme = FirstForwardedValue(ctx.Request.Headers["X-Forwarded-Proto"]) ?? ctx.Request.Scheme;
            string host = FirstForwardedValue(ctx.Request.Headers["X-Forwarded-Host"]) ?? ctx.Request.Host.Value;
            return $"{scheme}://{host}".TrimEnd('/');
        }


        private static ServerVersionConfig GetVersionConfig(string version)
        {
            if (versions.TryGetValue(version, out ServerVersionConfig? versionConfig))
                return versionConfig;

            ServerVersionConfig? latestConfig = null;
            Version? latestVersion = null;

            foreach (var knownVersion in versions)
            {
                if (!Version.TryParse(knownVersion.Key, out Version? parsedVersion))
                    continue;

                if (latestVersion is null || parsedVersion.CompareTo(latestVersion) > 0)
                {
                    latestVersion = parsedVersion;
                    latestConfig = knownVersion.Value;
                }
            }

            return latestConfig ?? versions.First().Value;
        }

        private static bool IsVersionAtLeast(string version, int major, int minor, int patch)
        {
            return Version.TryParse(version, out Version? parsedVersion)
                && parsedVersion.CompareTo(new Version(major, minor, patch)) >= 0;
        }

        private static string GetRouteValue(HttpContext ctx, string key)
        {
            return (string)ctx.Request.RouteValues[key]!;
        }

        private static bool TryReadNoticeFixture(HttpContext ctx, string fileName, out string fixtureJson)
        {
            string version = GetRouteValue(ctx, "version");
            if (!TryGetNoticeVersionDirectory(version, out string versionDirectory))
            {
                fixtureJson = string.Empty;
                return false;
            }

            string path = Path.Combine(versionDirectory, fileName);
            if (!File.Exists(path))
            {
                fixtureJson = string.Empty;
                return false;
            }

            fixtureJson = File.ReadAllText(path);
            SDKServer.log.Info(fixtureJson);
            return true;
        }


        private static string SerializeAndLog(object? value)
        {
            string serializedObject = JsonConvert.SerializeObject(value);
            SDKServer.log.Info(serializedObject);
            return serializedObject;
        }
    }
}
