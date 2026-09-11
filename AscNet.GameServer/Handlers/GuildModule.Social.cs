using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.guild;
using MongoDB.Driver;
using System.Globalization;
using System.Text.Json;

namespace AscNet.GameServer.Handlers;

internal partial class GuildModule
{
    private static readonly Lazy<Dictionary<int, GuildNewsTable>> NewsTemplates = new(() => TableReaderV2.Parse<GuildNewsTable>().ToDictionary(row => row.Id));

    internal static void RecordContributionRankEntry(Guild guild)
    {
        if (guild.ContributionRankEnteredAt == 0) guild.ContributionRankEnteredAt = Now;
    }

    internal static void AddNews(GuildMutation mutation, int templateId, params object[] arguments)
    {
        GuildNewsTable template = NewsTemplates.Value[templateId];
        Guild guild = mutation.Guild;
        List<string> values = arguments.Select(value => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty).ToList();
        string content = string.Format(CultureInfo.InvariantCulture, template.Content, values.Cast<object>().ToArray());
        guild.News.Add(new GuildNewsEntry { Id = ++guild.NextSocialSequence, MsgId = templateId, Time = Now, Params = values });
        int limit = Setting(template.Group == 2 ? "GuildPlayerNewsMaxCount" : "GuildNewsMaxCount");
        int excess = guild.News.Count(entry => NewsTemplates.Value[entry.MsgId].Group == template.Group) - limit;
        if (excess > 0)
        {
            HashSet<long> remove = guild.News.Where(entry => NewsTemplates.Value[entry.MsgId].Group == template.Group).OrderBy(entry => entry.Id).Take(excess).Select(entry => entry.Id).ToHashSet();
            guild.News.RemoveAll(entry => remove.Contains(entry.Id));
        }
        if (template.ShowChat == 1)
        {
            NotifyChatMessage message = ChatModule.BuildNotifyChatMessage(mutation.ActorSession, new ChatData { ChannelType = ChatChannelType.Guild, MsgType = ChatMsgType.System, Content = content });
            message.GuildName = guild.Name;
            message.GuildRankLevel = Rank(guild, mutation.ActorSession.player.PlayerData.Id);
            AppendGuildChat(mutation, message);
        }
    }

    private static void AppendGuildChat(GuildMutation mutation, NotifyChatMessage message)
    {
        Guild guild = mutation.Guild;
        message.MessageId = checked((int)++guild.NextSocialSequence);
        message.ChannelType = ChatChannelType.Guild;
        message.TargetId = guild.Id;
        // XChatData consumes the NotifyChatMessage shape, including HeadFrameId and BabelTowerTitleInfo.
        guild.ChatHistory.Add(JsonSerializer.Serialize(message));
        int excess = guild.ChatHistory.Count - Setting("GuildChatMaxCount");
        if (excess > 0) guild.ChatHistory.RemoveRange(0, excess);
        mutation.Broadcast(message);
    }

    internal static void SendGuildChat(Session session, NotifyChatMessage message)
    {
        Guild guild = RequireMembership(session, allowTourist: true);
        CommitTarget(session, guild, mutation =>
        {
            Require(mutation.Guild.MemberIds.Contains(session.player.PlayerData.Id), 20033016);
            message.GuildName = mutation.Guild.Name;
            message.GuildRankLevel = Rank(mutation.Guild, session.player.PlayerData.Id);
            AppendGuildChat(mutation, message);
        }, requireMembership: true, allowTourist: true);
    }

    [RequestPacketHandler("GuildListChatRequest")]
    public static void GuildListChatRequestHandler(Session session, Packet.Request packet)
    {
        GuildListChatResponse response = new();
        try
        {
            lock (MembershipLock)
            {
                Guild? guild = FindMembership(session.player.PlayerData.Id);
                Require(guild is not null, 20063211);
                response.ChatList = guild!.ChatHistory.TakeLast(Setting("GuildChatMaxCount")).ToList();
            }
        }
        catch (Exception exception) { response.Code = Failure(exception, 20063212); }
        session.SendResponse(response, packet.Id);
    }

    [RequestPacketHandler("GuildListNewsRequest")]
    public static void GuildListNewsRequestHandler(Session session, Packet.Request packet)
    {
        GuildListNewsResponse response = new();
        try
        {
            GuildListNewsRequest request = packet.Deserialize<GuildListNewsRequest>();
            lock (MembershipLock)
            {
                Guild? guild = FindMembership(session.player.PlayerData.Id);
                Require(guild is not null, 20063108);
                Require(request.NewsType is >= 1 and <= 3 && request.PageNo >= 0, 5);
                List<GuildNewsEntry> news = guild!.News.Where(entry => request.NewsType == 3 || NewsTemplates.Value[entry.MsgId].Group == request.NewsType).OrderBy(entry => entry.Id).ToList();
                int pageSize = Setting("GuildNewsCountPerPage");
                response.MaxPageNo = (news.Count + pageSize - 1) / pageSize;
                int page = request.PageNo == 0 ? response.MaxPageNo : request.PageNo;
                Require(page <= response.MaxPageNo, 5);
                if (page > 0) response.News = news.Skip((page - 1) * pageSize).Take(pageSize).ToList();
            }
        }
        catch (Exception exception) { response.Code = Failure(exception, 20063110); }
        session.SendResponse(response, packet.Id);
    }

    [RequestPacketHandler("GuildListRankRequest")]
    public static void GuildListRankRequestHandler(Session session, Packet.Request packet)
    {
        GuildListRankResponse response = new();
        try
        {
            GuildListRankRequest request = packet.Deserialize<GuildListRankRequest>();
            lock (MembershipLock)
            {
                Guild? own = FindMembership(session.player.PlayerData.Id);
                Require(own is not null, 20063232);
                Require(request.Order is >= 1 and <= 3, 5);
                int Score(Guild guild) => request.Order switch
                {
                    1 => RecentContribution(guild),
                    2 => guild.Level,
                    _ => Math.Min(Setting("GuildGloryMaxLevel"), guild.TalentPointFromBuild / Setting("GuildGloryPointsPerLevel"))
                };
                // Local-server tie policy: first contribution leaderboard entry, then stable guild ID.
                List<Guild> ranked = Guild.AllActive().OrderByDescending(Score)
                    .ThenBy(guild => request.Order == 1 && guild.ContributionRankEnteredAt > 0 ? guild.ContributionRankEnteredAt : long.MaxValue)
                    .ThenBy(guild => guild.Id).ToList();
                int rank = ranked.FindIndex(guild => guild.Id == own!.Id) + 1;
                int cap = Setting("GuildRankListCount");
                response.MyRankNum = rank <= cap ? rank : (double)rank / ranked.Count;
                response.RankList = ranked.Take(cap).Select(guild => new GuildRankEntry
                {
                    GuildId = guild.Id, GuildName = guild.Name, IconId = guild.IconId,
                    MemberCount = guild.MemberIds.Count(uid => Rank(guild, uid) != 5), Score = Score(guild)
                }).ToList();
            }
        }
        catch (Exception exception) { response.Code = Failure(exception, 20063233); }
        session.SendResponse(response, packet.Id);
    }

    internal static GuildRecruitPlayer SocialPlayer(Player player)
    {
        long uid = player.PlayerData.Id;
        return new GuildRecruitPlayer
        {
            PlayerId = uid, PlayerName = player.PlayerData.Name, Level = checked((int)player.PlayerData.Level),
            HeadPortraitId = checked((int)player.PlayerData.CurrHeadPortraitId), HeadFrameId = checked((int)player.PlayerData.CurrHeadFrameId),
            GuildCoin = Inventory.collection.Find(inventory => inventory.Uid == uid).FirstOrDefault()?.Items.FirstOrDefault(item => item.Id == 39)?.Count ?? 0,
            LastLoginTime = player.PlayerData.LastLoginTime, OnlineFlag = IsOnline(uid) ? 1 : 0
        };
    }

    [RequestPacketHandler("GuildRecruitRecommendRequest")]
    public static void GuildRecruitRecommendRequestHandler(Session session, Packet.Request packet)
    {
        GuildRecruitRecommendResponse response = new();
        try
        {
            GuildRecruitRecommendRequest request = packet.Deserialize<GuildRecruitRecommendRequest>();
            lock (MembershipLock)
            {
                Guild? guild = FindMembership(session.player.PlayerData.Id);
                Require(guild is not null, 20063146);
                Require(Rank(guild!, session.player.PlayerData.Id) is 1 or 2, 20063150);
                Require(request.PageNo > 0, 5);
                int minLevel = Math.Max(guild!.MinLevel, Setting("GuildPlayerRecommendLevel"));
                int pageSize = Setting("GuildPlayerRecommendCountPage");
                HashSet<long> members = Guild.AllActive().SelectMany(value => value.MemberIds).ToHashSet();
                response.RecommendData = Player.collection.Find(player => player.PlayerData.Level >= minLevel).ToEnumerable()
                    .Where(player => !members.Contains(player.PlayerData.Id) && player.GuildState.JoinCdEnd <= Now)
                    .OrderByDescending(player => IsOnline(player.PlayerData.Id)).ThenByDescending(player => player.PlayerData.LastLoginTime).ThenBy(player => player.PlayerData.Id)
                    .Take(Setting("GuildPlayerRecommendCount")).Skip((int)Math.Min(int.MaxValue, (long)(request.PageNo - 1) * pageSize)).Take(pageSize)
                    .Select(SocialPlayer).ToList();
            }
        }
        catch (Exception exception) { response.Code = Failure(exception, 20063147); }
        session.SendResponse(response, packet.Id);
    }
}
