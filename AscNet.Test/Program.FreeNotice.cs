using System.Net;
using System.Reflection;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using MessagePack;
using Newtonsoft.Json.Linq;
using Microsoft.AspNetCore.Builder;

namespace AscNet.Test;

internal partial class Program
{
    private const string FreeSource = "github.com/reiserFSs/InfiniteLoop";

    // The free notice is compiled in (FreeNotice): mail, home marquee and notices must carry it without any config file, in each
    // region's language, and a non-loopback session must also get the hosted warning at every login.
    private static void ValidateFreeNotice()
    {
        ValidateFreeNoticeMail();
        ValidateHostedWarningAtLogin();
        ValidateFreeNoticeEndpoints().GetAwaiter().GetResult();
    }

    // What the SDK serves for a notice file: the fixture with InfiniteLoop's own notice replacing/leading it.
    private static string ExpectedNoticeBody(string name, string region, string fixtureText)
    {
        switch (name)
        {
            case "LoginNotice": return FreeNotice.LoginNotice(region).ToString(Newtonsoft.Json.Formatting.None);
            case "ScrollTextNotice": return FreeNotice.ScrollTextNotice(region).ToString(Newtonsoft.Json.Formatting.None);
            case "GameNotice":
                JArray games = JArray.Parse(fixtureText);
                games.Insert(0, FreeNotice.GameNotice(region));
                return games.ToString(Newtonsoft.Json.Formatting.None);
            default: return fixtureText;
        }
    }

    private static void ValidateFreeNoticeMail()
    {
        // Nothing in the shipped config defines the mail: it cannot be deleted or edited there.
        AssertEqual(false, File.ReadAllText(ResourcePath("Configs", "system_mails.json")).Contains("ascnet-disclaimer", StringComparison.Ordinal),
            "system_mails.json no longer carries the free notice");

        HashSet<string> titles = [];
        foreach (string region in new[] { "en", "tw", "kr", "jp", "cn" })
        {
            Player player = CreateDrawCompatibilityPlayer(46_950);
            player.ClientRegion = region == "en" ? null : region;
            AssertEqual(true, AscNet.GameServer.Handlers.MailModule.EnsureSystemMails(player, 1_000), $"{region} receives the free notice mail");
            PlayerMail mail = player.Mails.Single(m => m.Id == "ascnet-disclaimer");
            AssertEqual(true, mail.IsForbidDelete, $"{region} free notice mail is undeletable");
            AssertEqual(true, mail.Content!.Contains(FreeSource, StringComparison.Ordinal), $"{region} free notice mail names the official source");
            AssertEqual(FreeNotice.Title(region), mail.Title, $"{region} free notice mail is localized");
            // The client shows the title above the body and "InfiniteLoop" before the sender: neither may repeat.
            AssertEqual(false, mail.Content.Contains(FreeNotice.Title(region), StringComparison.Ordinal), $"{region} mail body doesn't repeat its title");
            AssertEqual(1, System.Text.RegularExpressions.Regex.Matches(mail.SendName, "InfiniteLoop").Count, $"{region} sender names InfiniteLoop once");
            AssertEqual(true, titles.Add(mail.Title), $"{region} free notice title differs from the other regions");
            AssertEqual(false, AscNet.GameServer.Handlers.MailModule.EnsureSystemMails(player, 1_001), $"{region} free notice mail is not reissued");
            AssertEqual(1, player.Mails.Count, $"{region} free notice mail is not duplicated");
        }

        // A player who already holds the old upstream copy gets the text updated in place, unread, with no second mail.
        Player existing = CreateDrawCompatibilityPlayer(46_951);
        existing.Mails.Add(new PlayerMail { Id = "ascnet-disclaimer", Status = 1, Title = "Welcome to AscNet", Content = "old", IsForbidDelete = false });
        AssertEqual(true, AscNet.GameServer.Handlers.MailModule.EnsureSystemMails(existing, 2_000), "old disclaimer copy is updated");
        AssertEqual(1, existing.Mails.Count, "old disclaimer copy is updated in place");
        AssertEqual("InfiniteLoop is free", Common_Title(existing.Mails[0]), "old disclaimer copy now says InfiniteLoop");
        AssertEqual(0, existing.Mails[0].Status, "updated disclaimer is unread again");
        AssertEqual(true, existing.Mails[0].IsForbidDelete, "updated disclaimer is undeletable");

        // A tampered copy (made deletable, text edited, with a reward) is restored.
        existing.Mails[0].Content = "edited";
        existing.Mails[0].IsForbidDelete = false;
        AssertEqual(true, AscNet.GameServer.Handlers.MailModule.EnsureSystemMails(existing, 2_001), "edited free notice copy is restored");
        AssertEqual(true, existing.Mails[0].Content!.Contains(FreeSource, StringComparison.Ordinal) && existing.Mails[0].IsForbidDelete, "restored copy");

        static string Common_Title(PlayerMail mail) => mail.Title;

        // Loopback forms are not hosted; LAN, public and unknown peers are.
        foreach (string local in new[] { "127.0.0.1", "127.8.9.10", "::1", "::ffff:127.0.0.1" })
            AssertEqual(false, FreeNotice.IsHosted(IPAddress.Parse(local)), $"{local} is loopback");
        foreach (string remote in new[] { "192.168.1.20", "10.0.0.2", "203.0.113.9", "2001:db8::1", "::ffff:192.168.1.20" })
            AssertEqual(true, FreeNotice.IsHosted(IPAddress.Parse(remote)), $"{remote} is hosted");
        AssertEqual(true, FreeNotice.IsHosted(null), "unknown peer is hosted");
    }

    private static void ValidateHostedWarningAtLogin()
    {
        using MongoCollectionOverride mongoOverride = MongoCollectionOverride.InstallForShopCompatibility();
        MethodInfo doLogin = RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule"), "DoLogin",
            BindingFlags.Static | BindingFlags.NonPublic, [typeof(AscNet.GameServer.Session)]);

        // Loopback: only the free notice; no hosted mail, no hosted marquee line.
        Player own = CreateDrawCompatibilityPlayer(46_960);
        (NotifyMails ownMails, AscNet.GameServer.Handlers.NotifyWorldChat ownChat) = LoginPushes(doLogin, own, "127.0.0.1");
        AssertEqual(1, ownMails.NewMailList.Count, "loopback login lists only the free notice mail");
        AssertEqual(1, ownChat.ChatMessages.Count, "loopback login marquee has no hosted warning");
        AssertEqual(true, ownChat.ChatMessages[0].Content!.Contains(FreeSource, StringComparison.Ordinal), "loopback marquee carries the free notice");

        // Hosted (LAN address, Traditional Chinese client): the warning arrives at every login, unread each time, localized.
        Player guest = CreateDrawCompatibilityPlayer(46_961);
        guest.ClientRegion = "tw";
        for (int login = 1; login <= 2; login++)
        {
            (NotifyMails mails, AscNet.GameServer.Handlers.NotifyWorldChat chat) = LoginPushes(doLogin, guest, "192.168.1.20");
            NotifyMails.NotifyMailsNewMailList warning = mails.NewMailList.Single(m => m.Id == "infiniteloop-hosted-warning");
            AssertEqual(0, warning.Status, $"login {login}: hosted warning mail is unread");
            AssertEqual(true, warning.IsForbidDelete, $"login {login}: hosted warning mail is undeletable");
            AssertEqual(FreeNotice.HostedTitle("tw"), warning.Title, $"login {login}: hosted warning is Traditional Chinese");
            AssertEqual(true, warning.Content!.Contains(FreeSource, StringComparison.Ordinal), $"login {login}: hosted warning names the official source");
            AssertEqual(2, mails.NewMailList.Count, $"login {login}: free notice plus one hosted warning, no duplicates");
            AssertEqual(2, chat.ChatMessages.Count, $"login {login}: marquee has free notice and hosted warning");
            AssertEqual(FreeNotice.HostedText("tw"), chat.ChatMessages[1].Content, $"login {login}: hosted marquee line");
            guest.Mails.Single(m => m.Id == "infiniteloop-hosted-warning").Status = 1; // player read it
        }

        // The same account on its own PC afterwards: the hosted warning is gone.
        (NotifyMails backMails, AscNet.GameServer.Handlers.NotifyWorldChat backChat) = LoginPushes(doLogin, guest, "127.0.0.1");
        AssertEqual(false, backMails.NewMailList.Any(m => m.Id == "infiniteloop-hosted-warning"), "loopback login drops the hosted warning mail");
        AssertEqual(1, backChat.ChatMessages.Count, "loopback login marquee has no hosted warning again");
    }

    private static (NotifyMails Mails, AscNet.GameServer.Handlers.NotifyWorldChat Chat) LoginPushes(MethodInfo doLogin, Player player, string remote)
    {
        long id = player.PlayerData.Id;
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(id), player,
            CreateDrawCompatibilityInventory(id, []), $"free-notice-{remote}");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(id);
        harness.Session.RemoteAddress = IPAddress.Parse(remote);
        doLogin.Invoke(null, [harness.Session]);
        NotifyMails? mails = null;
        AscNet.GameServer.Handlers.NotifyWorldChat? chat = null;
        for (int index = 0; index < 192 && chat is null; index++)
        {
            Packet.Push push = ReadStartupPush(harness, $"free notice login push {index + 1}");
            if (push.Name == nameof(NotifyMails))
                mails = MessagePackSerializer.Deserialize<NotifyMails>(push.Content);
            else if (push.Name == "NotifyWorldChat")
                chat = MessagePackSerializer.Deserialize<AscNet.GameServer.Handlers.NotifyWorldChat>(push.Content);
        }
        return (mails ?? throw new InvalidDataException("login sent no NotifyMails"), chat ?? throw new InvalidDataException("login sent no NotifyWorldChat"));
    }

    private static async Task ValidateFreeNoticeEndpoints()
    {
        await using WebApplication app = CreateConfigControllerTestApp();
        await app.StartAsync();
        try
        {
            using HttpClient client = new() { BaseAddress = new Uri(BoundAddress(app)) };
            foreach ((string region, string package, string key) in new[]
            {
                ("en", "com.kurogame.punishing.grayraven.en", "clEoudH0zUSDQsQa"),
                ("tw", "com.kurogame.punishing.grayraven.tw", "KaWGXt8A8wm5iqm5")
            })
            {
                string prefix = $"/prod/client/notice/config/{key}/{package}/4.9.0/";
                JObject login = JObject.Parse(await client.GetStringAsync($"{prefix}LoginNotice.json"));
                AssertEqual(FreeNotice.Title(region), login.Value<string>("Title"), $"{region} LoginNotice title");
                string html = await client.GetStringAsync("/prod/" + login.Value<string>("HtmlUrl"));
                AssertEqual(true, html.Contains(FreeSource, StringComparison.Ordinal), $"{region} login notice page names the official source");
                // The notice view is the client's text renderer, not a browser: no <title>, no heading repeating the dialog title, no indentation.
                AssertEqual(false, html.Contains("<title", StringComparison.OrdinalIgnoreCase) || html.Contains(System.Net.WebUtility.HtmlEncode(FreeNotice.Title(region)), StringComparison.Ordinal) || html.Contains('\n'), $"{region} notice page has no duplicated title or whitespace");
                AssertEqual(true, html.Contains(System.Net.WebUtility.HtmlEncode(FreeNotice.MailContent(region).Split("\n")[0]), StringComparison.Ordinal), $"{region} login notice page text");
                JObject scroll = JObject.Parse(await client.GetStringAsync($"{prefix}ScrollTextNotice.json"));
                AssertEqual(FreeNotice.FreeText(region), scroll.Value<string>("Content"), $"{region} ScrollTextNotice text");
                JArray games = JArray.Parse(await client.GetStringAsync($"{prefix}GameNotice.json"));
                AssertEqual("infiniteloop-free-game", games[0].Value<string>("Id"), $"{region} GameNotice leads with the InfiniteLoop notice");
                AssertEqual(true, games.Count > 1, $"{region} Kuro's own game notices are still served");
                string gamePage = await client.GetStringAsync("/prod/" + games[0]["Content"]![0]!.Value<string>("Url"));
                AssertEqual(true, gamePage.Contains(FreeSource, StringComparison.Ordinal), $"{region} game notice page names the official source");
            }
        }
        finally
        {
            await app.StopAsync();
        }
    }
}
