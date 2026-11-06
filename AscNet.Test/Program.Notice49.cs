using System.Net;
using AscNet.Common.Util;
using Microsoft.AspNetCore.Builder;
using Newtonsoft.Json.Linq;

namespace AscNet.Test;

internal partial class Program
{
    // 4.9.0 notice set (EN key clEoudH0zUSDQsQa): Kuro's live notice config, same documented source as the older
    // Configs/Notices/<version> sets. Every picture a ScrollPicNotice references is installed and served, every
    // skip id it jumps to exists in the 4.9 SkipFunctional table, and the 4.9 client's notice URLs resolve to the set.
    private static async Task ValidateEn49NoticeEndpoints()
    {
        string root = Path.GetDirectoryName(ResourcePath("Configs", "Notices", "4.9.0", "LoginNotice.json"))!;
        JObject scrollPic = JObject.Parse(File.ReadAllText(Path.Combine(root, "ScrollPicNotice.json")));
        HashSet<string> skipIds = File.ReadLines(ResourcePath("table", "client", "functional", "SkipFunctional.tsv"))
            .Skip(1).Select(line => line.Split('\t')[0]).ToHashSet();
        JArray content = scrollPic.Value<JArray>("Content") ?? throw new InvalidDataException("4.9 ScrollPicNotice omitted Content.");
        AssertEqual(11, content.Count, "4.9 ScrollPicNotice content count");
        foreach (JObject entry in content.Cast<JObject>())
        {
            string picAddr = RequiredNonEmptyString(entry, "PicAddr", "4.9 ScrollPicNotice content");
            if (!File.Exists(Path.Combine(root, picAddr)))
                throw new FileNotFoundException($"4.9 ScrollPicNotice image missing: {picAddr}");
            if (entry.Value<string>("JumpType") == "2" && !skipIds.Contains(entry.Value<string>("JumpAddr")!))
                throw new InvalidDataException($"4.9 ScrollPicNotice jumps to unknown SkipId {entry.Value<string>("JumpAddr")}.");
        }
        JObject secondMenu = JObject.Parse(File.ReadAllText(Path.Combine(root, "SecondMenuNotice.json")));
        foreach (JObject entry in secondMenu.Value<JArray>("Content")!.Cast<JObject>())
            if (entry.Value<string>("JumpType") == "2" && !skipIds.Contains(entry.Value<string>("JumpAddr")!))
                throw new InvalidDataException($"4.9 SecondMenuNotice jumps to unknown SkipId {entry.Value<string>("JumpAddr")}.");

        await using WebApplication app = CreateConfigControllerTestApp();
        await app.StartAsync();
        try
        {
            using HttpClient client = new() { BaseAddress = new Uri(BoundAddress(app)) };
            const string prefix = "/prod/client/notice/config/clEoudH0zUSDQsQa/com.kurogame.punishing.grayraven.en/4.9.0/";
            foreach (string name in new[] { "LoginNotice", "GameNotice", "ScrollTextNotice", "ScrollPicNotice", "SecondMenuNotice" })
            {
                string body = await client.GetStringAsync($"{prefix}{name}.json");
                AssertEqual(File.ReadAllText(Path.Combine(root, $"{name}.json")), body, $"4.9 {name} served from the 4.9.0 set");
            }
            JArray games = JArray.Parse(await client.GetStringAsync($"{prefix}GameNotice.json"));
            AssertEqual(21, games.Count, "4.9 GameNotice count");
            string loginHtml = JObject.Parse(await client.GetStringAsync($"{prefix}LoginNotice.json")).Value<string>("HtmlUrl")!;
            using HttpResponseMessage html = await client.GetAsync("/prod/" + loginHtml);
            AssertEqual(HttpStatusCode.OK, html.StatusCode, "4.9 LoginNotice html resolves");
            foreach (JObject entry in content.Cast<JObject>())
            {
                using HttpResponseMessage pic = await client.GetAsync("/prod/" + entry.Value<string>("PicAddr")!.Replace("client/notice/pic/", "client/notice/pic/"));
                AssertEqual(HttpStatusCode.OK, pic.StatusCode, $"4.9 picture {entry.Value<string>("PicAddr")}");
            }
            // 4.9.0 has no PopUpPicNotice upstream (404 on Kuro's CDN): served as the empty notice, like 4.8.
            AssertEqual("null", await client.GetStringAsync($"{prefix}PopUpPicNotice.json"), "4.9 PopUpPicNotice empty");
        }
        finally
        {
            await app.StopAsync();
        }
    }
}
