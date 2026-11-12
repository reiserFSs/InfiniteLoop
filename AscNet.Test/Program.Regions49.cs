using System.Net;
using System.Reflection;
using AscNet.Common.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json.Linq;

namespace AscNet.Test;

internal partial class Program
{
    // TW/KR/JP/CN 4.9 SDK data. Each region's CDN key is the XBuildConfig.CDNKey in that region's launcher package
    // (PGR_Data/resources.assets, next to the package id and 4.9.0/4.9.1 versions); the rows are Kuro's live config.tab for
    // that key (Fixtures/Region/<region>49.tab, byte for byte except CR) and the notices are Kuro's live notice config.
    private static readonly (string Region, string Package, string Key, string[] Owned)[] Regions49 =
    [
        ("tw", "com.kurogame.punishing.grayraven.tw", "KaWGXt8A8wm5iqm5",
            ["DocumentVersion", "PayCallbackUrl", "AndroidPayCallbackUrl", "IosPayCallbackUrl", "ServerListStr", "LaunchModuleVersion", "KuroSDKPayCallbackUrl", "ChannelServerListStr"]),
        ("kr", "com.kurogame.punishing.grayraven.kr", "vxUrzyBXEyY1L5T8",
            ["DocumentVersion", "PayCallbackUrl", "ServerListStr", "AndroidPayCallbackUrl", "IosPayCallbackUrl", "OneStorePayCallbackUrl", "LaunchModuleVersion", "PcPayCallbackUrl", "ChannelServerListStr"]),
        ("jp", "com.kurogame.punishing.grayraven.jp", "u6ubWH4wy50y8LDz",
            ["DocumentVersion", "PayCallbackUrl", "ServerListStr", "AndroidPayCallbackUrl", "IosPayCallbackUrl", "LaunchModuleVersion", "GooglePointOrderUrl", "PcPayCallbackUrl", "ChannelServerListStr"]),
        ("cn", "com.kurogame.haru.kuro", "tAWC541FSRl5WHwq",
            ["DocumentVersion", "PayCallbackUrl", "ServerListStr", "LaunchModuleVersion", "ChannelServerListStr"]),
    ];

    private static async Task ValidateRegions49()
    {
        Type controller = Type.GetType("AscNet.SDKServer.Controllers.ConfigController, AscNet.SDKServer", throwOnError: true)!;
        MethodInfo handle = RequiredMethod(controller, "HandleConfigRequest", BindingFlags.Static | BindingFlags.NonPublic, [typeof(HttpContext)]);
        string Serve(string package, string version, string? cdnKey)
        {
            DefaultHttpContext context = new();
            context.Request.Scheme = "http";
            context.Request.Host = new HostString("127.0.0.1:8080");
            context.Request.RouteValues["package"] = package;
            context.Request.RouteValues["version"] = version;
            if (cdnKey is not null)
                context.Request.RouteValues["cdnKey"] = cdnKey;
            return (string)handle.Invoke(null, [context])!;
        }

        HashSet<string> skipIds = File.ReadLines(ResourcePath("table", "client", "functional", "SkipFunctional.tsv"))
            .Skip(1).Select(line => line.Split('\t')[0]).ToHashSet();
        string noticeRoot = Path.GetDirectoryName(ResourcePath("Configs", "Notices", "4.9.0", "LoginNotice.json"))!;
        await using WebApplication app = CreateConfigControllerTestApp();
        await app.StartAsync();
        try
        {
            using HttpClient client = new() { BaseAddress = new Uri(BoundAddress(app)) };
            foreach ((string region, string package, string key, string[] owned) in Regions49)
            {
                foreach (string? cdnKey in new[] { null, key })
                {
                    // 4.9.0 is the client's XInfo.Version (skip shortcut: Application == Document == LaunchModule).
                    AssertRegionTabMatchesRetail(Serve(package, "4.9.0", cdnKey), $"{region}49", $"{region} 4.9.0", owned,
                        new() { ["DocumentVersion"] = "4.9.0", ["LaunchModuleVersion"] = "4.9.0" });
                    string served491 = Serve(package, "4.9.1", cdnKey);
                    foreach (string row in new[] { "ApplicationVersion\tstring\t4.9.1", "DocumentVersion\tstring\t4.9.1", "LaunchModuleVersion\tstring\t4.9.1" })
                        if (!served491.Contains(row, StringComparison.Ordinal))
                            throw new InvalidDataException($"{region} 4.9.1 config.tab lost row '{row}'.");
                }

                // The region's own notice set is served, not the EN 4.9.0 one.
                string regionRoot = Path.Combine(noticeRoot, package);
                string prefix = $"/prod/client/notice/config/{key}/{package}/4.9.0/";
                foreach (string name in new[] { "LoginNotice", "GameNotice", "ScrollTextNotice", "ScrollPicNotice", "SecondMenuNotice" })
                {
                    string body = await client.GetStringAsync($"{prefix}{name}.json");
                    AssertEqual(File.ReadAllText(Path.Combine(regionRoot, $"{name}.json")), body, $"{region} 4.9 {name} served from its region set");
                    if (body == File.ReadAllText(Path.Combine(noticeRoot, $"{name}.json")))
                        throw new InvalidDataException($"{region} 4.9 {name} fell back to the EN set.");
                }
                // CN publishes a PopUpPicNotice; the other regions 404 upstream and keep the empty notice.
                string popUp = await client.GetStringAsync($"{prefix}PopUpPicNotice.json");
                AssertEqual(region == "cn" ? File.ReadAllText(Path.Combine(regionRoot, "PopUpPicNotice.json")) : "null", popUp, $"{region} 4.9 PopUpPicNotice");

                JObject scrollPic = JObject.Parse(File.ReadAllText(Path.Combine(regionRoot, "ScrollPicNotice.json")));
                foreach (JObject entry in scrollPic.Value<JArray>("Content")!.Cast<JObject>())
                {
                    string picAddr = RequiredNonEmptyString(entry, "PicAddr", $"{region} 4.9 ScrollPicNotice content");
                    if (!File.Exists(Path.Combine(regionRoot, picAddr)))
                        throw new FileNotFoundException($"{region} 4.9 ScrollPicNotice image missing: {picAddr}");
                    using HttpResponseMessage pic = await client.GetAsync("/prod/" + picAddr);
                    AssertEqual(HttpStatusCode.OK, pic.StatusCode, $"{region} 4.9 picture {picAddr}");
                }
                foreach (string name in new[] { "ScrollPicNotice", "SecondMenuNotice" })
                    foreach (JObject entry in JObject.Parse(File.ReadAllText(Path.Combine(regionRoot, $"{name}.json"))).Value<JArray>("Content")!.Cast<JObject>())
                        if (entry.Value<string>("JumpType") == "2" && !skipIds.Contains(entry.Value<string>("JumpAddr")!))
                            throw new InvalidDataException($"{region} 4.9 {name} jumps to unknown SkipId {entry.Value<string>("JumpAddr")}.");
                await AssertNoticeHtmlSynced(client, regionRoot, $"{region} 4.9");
            }
        }
        finally
        {
            await app.StopAsync();
        }
    }
}
