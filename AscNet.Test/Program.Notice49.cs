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
            AssertEqual(15, games.Count, "4.9 GameNotice count");
            await AssertNoticeHtmlSynced(client, root, "4.9");
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

    // Every page LoginNotice/GameNotice links is Kuro's own HTML, served byte-exact; a missing file would otherwise
    // hide behind the synthesized title-only fallback page (still HTTP 200).
    private static async Task AssertNoticeHtmlSynced(HttpClient client, string setRoot, string label)
    {
        List<string> urls = [JObject.Parse(File.ReadAllText(Path.Combine(setRoot, "LoginNotice.json"))).Value<string>("HtmlUrl")!];
        foreach (JObject notice in JArray.Parse(File.ReadAllText(Path.Combine(setRoot, "GameNotice.json"))).Cast<JObject>())
            foreach (JObject page in notice.Value<JArray>("Content")!.Cast<JObject>())
                urls.Add(RequiredNonEmptyString(page, "Url", $"{label} GameNotice content"));
        foreach (string url in urls)
        {
            string path = Path.Combine(setRoot, url);
            if (!File.Exists(path))
                throw new FileNotFoundException($"{label} notice html missing: {url}");
            byte[] served = await client.GetByteArrayAsync("/prod/" + url);
            if (!served.AsSpan().SequenceEqual(File.ReadAllBytes(path)))
                throw new InvalidDataException($"{label} notice html {url} not served from the synced set.");
        }
    }
}
