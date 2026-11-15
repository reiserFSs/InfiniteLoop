using System.Net;
using Newtonsoft.Json.Linq;

namespace AscNet.Common.Util;

/// <summary>
/// The only copy of InfiniteLoop's "it is free" text. It is compiled in on purpose: config files are trivially edited by a
/// server operator, so mail, chat marquee and notices all take their wording from here (selected by <see cref="ClientRegions"/>).
/// Text for tw/kr/jp/cn is machine-translated and still needs a native-speaker check.
/// </summary>
public static class FreeNotice
{
    public const string Source = "github.com/reiserFSs/InfiniteLoop";
    public const string ReleasesUrl = "https://github.com/reiserFSs/InfiniteLoop/releases";
    public const string DiscordUrl = "https://discord.gg/UVzx3rRkdK";
    /// <summary>Kept from the upstream disclaimer so players who already have it get the new text in place, not a second mail.</summary>
    public const string MailId = "ascnet-disclaimer";
    public const string HostedMailId = "infiniteloop-hosted-warning";
    public const long NoticeModifyTime = 1795000000000;
    public const long NoticeEndTime = 4102444800;

    /// <summary>
    /// Title and HostedTitle are headings (mail title, notice dialog title). Body/HostedBody go under a heading, so they
    /// don't repeat it. Free/Hosted are standalone one-liners for places with no heading (marquee, scroll text).
    /// Sender follows the coloured "InfiniteLoop" in the mail's From line.
    /// </summary>
    private sealed record Text(string Title, string Body, string Free, string HostedTitle, string HostedBody, string Hosted,
        string Links, string Sender);

    private static readonly Dictionary<string, Text> Texts = new()
    {
        ["en"] = new("InfiniteLoop is free",
            "If you paid anyone to play, you were scammed. InfiniteLoop is a free fan project that runs on your own PC.",
            $"InfiniteLoop is free. If you paid anyone to play, you were scammed. Official: {Source}",
            "You are on someone else's server",
            "This server is run by someone else. InfiniteLoop is free and meant to run on your own PC; if you paid for access, you were scammed.",
            $"You are connected to a server run by someone else. InfiniteLoop is free; if you paid for access, you were scammed. Official: {Source}",
            $"Download: {ReleasesUrl}\nDiscord: {DiscordUrl}", "Developers"),
        ["tw"] = new("InfiniteLoop 是免費的",
            "如果你曾向任何人付費才能遊玩，你就被騙了。InfiniteLoop 是在你自己電腦上執行的免費粉絲專案。",
            $"InfiniteLoop 是免費的。如果你曾向任何人付費才能遊玩，你就被騙了。官方：{Source}",
            "你正連線到他人的伺服器",
            "這個伺服器由他人營運。InfiniteLoop 是免費的，且設計為在你自己的電腦上執行；如果你為了連線而付費，你就被騙了。",
            $"你目前連線的是由他人營運的伺服器。InfiniteLoop 是免費的；如果你為了連線而付費，你就被騙了。官方：{Source}",
            $"下載：{ReleasesUrl}\nDiscord：{DiscordUrl}", "開發團隊"),
        ["kr"] = new("InfiniteLoop는 무료입니다",
            "플레이하기 위해 누군가에게 돈을 냈다면 사기를 당한 것입니다. InfiniteLoop는 본인의 PC에서 실행되는 무료 팬 프로젝트입니다.",
            $"InfiniteLoop는 무료입니다. 플레이하기 위해 누군가에게 돈을 냈다면 사기를 당한 것입니다. 공식: {Source}",
            "다른 사람의 서버에 접속 중입니다",
            "이 서버는 다른 사람이 운영합니다. InfiniteLoop는 무료이며 본인의 PC에서 실행하도록 만들어졌습니다. 접속을 위해 돈을 냈다면 사기를 당한 것입니다.",
            $"다른 사람이 운영하는 서버에 접속 중입니다. InfiniteLoop는 무료입니다. 접속을 위해 돈을 냈다면 사기를 당한 것입니다. 공식: {Source}",
            $"다운로드: {ReleasesUrl}\nDiscord: {DiscordUrl}", "개발진"),
        ["jp"] = new("InfiniteLoopは無料です",
            "プレイするために誰かにお金を払ったなら、あなたは騙されています。InfiniteLoopはご自身のPCで動く無料のファンプロジェクトです。",
            $"InfiniteLoopは無料です。プレイするために誰かにお金を払ったなら、あなたは騙されています。公式：{Source}",
            "他の人のサーバーに接続しています",
            "このサーバーは他の人が運営しています。InfiniteLoopは無料で、ご自身のPCで動かすためのものです。接続のためにお金を払ったなら、騙されています。",
            $"他の人が運営するサーバーに接続しています。InfiniteLoopは無料です。接続のためにお金を払ったなら、騙されています。公式：{Source}",
            $"ダウンロード：{ReleasesUrl}\nDiscord：{DiscordUrl}", "開発チーム"),
        ["cn"] = new("InfiniteLoop 是免费的",
            "如果你为了游玩向任何人付过费，你就被骗了。InfiniteLoop 是在你自己电脑上运行的免费粉丝项目。",
            $"InfiniteLoop 是免费的。如果你为了游玩向任何人付过费，你就被骗了。官方：{Source}",
            "你正连接到他人的服务器",
            "这个服务器由他人运营。InfiniteLoop 是免费的，设计为在你自己的电脑上运行；如果你为了接入而付费，你就被骗了。",
            $"你当前连接的是由他人运营的服务器。InfiniteLoop 是免费的；如果你为了接入而付费，你就被骗了。官方：{Source}",
            $"下载：{ReleasesUrl}\nDiscord：{DiscordUrl}", "开发团队"),
    };

    private static Text For(string? region) => Texts[region is not null && Texts.ContainsKey(region) ? region : ClientRegions.Default];

    /// <summary>The region of a logged-in player: the gate's remembered package, else the persisted one, else EN.</summary>
    public static string RegionOf(Database.Player player) =>
        (player.Token is { } token ? ClientRegions.Recall(token) : null) ?? player.ClientRegion ?? ClientRegions.Default;

    public static string Title(string? region) => For(region).Title;
    public static string FreeText(string? region) => For(region).Free;
    public static string HostedTitle(string? region) => For(region).HostedTitle;
    public static string HostedText(string? region) => For(region).Hosted;
    public static string MailSender(string? region) => $"<color=#8b0000><b>InfiniteLoop</b></color> {For(region).Sender}";
    public static string MailContent(string? region) => $"{For(region).Body}\n\n{For(region).Links}";
    public static string HostedMailContent(string? region) => $"{For(region).HostedBody}\n\n{For(region).Links}";

    /// <summary>Loopback (127.0.0.0/8, ::1, incl. IPv4-mapped) means the player runs the server on their own PC; anything else, including a LAN, is hosted.</summary>
    public static bool IsHosted(IPAddress? remote) =>
        remote is null || !IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote);

    public static string NoticeHtmlName(string? region) => $"infiniteloop-free-{region ?? ClientRegions.Default}.html";

    public static string? RegionFromHtmlName(string fileName) =>
        Texts.Keys.FirstOrDefault(region => NoticeHtmlName(region) == fileName);

    /// <summary>
    /// The client's notice view (XUiPanelWebHtmlView via XHtmlHandler) is not a browser: it turns body paragraphs into
    /// text rows and shows the dialog's own title above them. So, like Kuro's notice pages: one line, no &lt;title&gt;,
    /// no heading repeating the dialog title, no whitespace between tags.
    /// </summary>
    public static string NoticeHtml(string region)
    {
        Text text = For(region);
        // XHtmlHandler decodes only &quot; &amp; &lt; &gt; &nbsp;; WebUtility.HtmlEncode's &#39; etc. would show literally.
        string Encode(string value) => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        // "Label: URL" lines become a client link (XHtmlHandler turns <a href> into Application.OpenURL).
        string Line(string line)
        {
            int at = line.IndexOf("https://", StringComparison.Ordinal);
            if (at < 0) return $"<p>{Encode(line)}</p>";
            string url = Encode(line[at..]);
            return $"<p>{Encode(line[..at])}<a href=\"{url}\">{url}</a></p>";
        }
        string links = string.Concat(text.Links.Split('\n').Select(Line));
        return "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"initial-scale=1, maximum-scale=1.0, user-scalable=no\">"
            + "<style type=\"text/css\">* { margin: 0px; border: 0px; }p {font-size: 28px;color: rgba(0,0,0,0.7);}</style></head>"
            + "<body style=\"width: 1350px; -webkit-text-size-adjust: 100%; text-size-adjust: 100%;padding: 35px;\">"
            + $"<p>{Encode(text.Body)}</p><p><br></p>{links}</body></html>";
    }

    /// <summary>InfiniteLoop's own notices (not Kuro's), in the shapes of Kuro's LoginNotice / ScrollTextNotice / GameNotice entries.</summary>
    public static JObject LoginNotice(string? region) => new()
    {
        ["Id"] = "infiniteloop-free-login",
        ["ModifyTime"] = NoticeModifyTime,
        ["BeginTime"] = 0,
        ["EndTime"] = NoticeEndTime,
        ["Title"] = For(region).Title,
        ["HtmlUrl"] = $"client/notice/html/{NoticeHtmlName(region ?? ClientRegions.Default)}",
        ["LoginPlatformList"] = new JArray(0, 1, 2)
    };

    public static JObject ScrollTextNotice(string? region) => new()
    {
        ["Id"] = "infiniteloop-free-scroll",
        ["ModifyTime"] = NoticeModifyTime,
        ["BeginTime"] = 0,
        ["EndTime"] = NoticeEndTime,
        ["Content"] = For(region).Free,
        ["ScrollInterval"] = 300,
        ["ScrollTimes"] = 100000,
        ["ShowInFight"] = 0,
        ["ShowInPhotograph"] = 0,
        ["LoginPlatformList"] = new JArray(0, 1, 2)
    };

    public static JObject GameNotice(string? region) => new()
    {
        ["Id"] = "infiniteloop-free-game",
        ["Title"] = For(region).Title,
        ["Tag"] = 0,
        ["Type"] = 1,
        ["LoginEject"] = 0,
        ["BluePoint"] = 1,
        ["Preload"] = 0,
        ["Order"] = 0,
        ["ModifyTime"] = NoticeModifyTime / 1000,
        ["BeginTime"] = 0,
        ["EndTime"] = NoticeEndTime,
        ["Content"] = new JArray(new JObject
        {
            ["Id"] = 0,
            ["Title"] = For(region).Title,
            ["Url"] = $"client/notice/html/{NoticeHtmlName(region ?? ClientRegions.Default)}",
            ["Order"] = "1"
        }),
        ["LoginPlatformList"] = new JArray(0, 1, 2)
    };
}
