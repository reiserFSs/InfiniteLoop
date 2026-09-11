using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.guild;
using AscNet.Table.V2.share.config;
using AscNet.Table.V2.share.condition;
using AscNet.Table.V2.share.functional;
using MessagePack;
using MongoDB.Driver;
using System.Globalization;

namespace AscNet.GameServer.Handlers;

[MessagePackObject(true)]
public class GuildListRecommendRequest { public int PageNo; }
[MessagePackObject(true)]
public class GuildListRecommendResponse { public int Code; public List<object> Datas = []; public long JoinCdEnd; }
[MessagePackObject(true)]
public class GuildCreateRequest { public string GuildName = string.Empty; public string GuildDeclaration = string.Empty; public int IconId; }
[MessagePackObject(true)]
public class GuildCreateResponse { public int Code; }
[MessagePackObject(true)]
public class GuildFindRequest { public int GuildId; }
[MessagePackObject(true)]
public class GuildFindResponse { public int Code; public List<object> GuildList = []; }
[MessagePackObject(true)]
public class GuildApplyRequest { public int GuildId; }
[MessagePackObject(true)]
public class GuildApplyResponse : GuildResponse { public bool IsPass; }
[MessagePackObject(true)]
public class GuildListApplyRequest { }
[MessagePackObject(true)]
public class GuildAckApplyRequest { public long PlayId; public bool IsAgree; }
[MessagePackObject(true)]
public class GuildAckApplyResponse : GuildResponse { }

internal partial class GuildModule
{
    // EN XGuildConfig.ApplySetting/GuildRankLevel and XGuildData initial compatibility defaults.
    private const int DirectAdmission = 1, NeedsApproval = 2, ForbiddenAdmission = 3;
    private const int LeaderRank = 1, MemberRank = 4, NoGuildRank = 9;
    // ponytail: authenticated operations share this monitor; use ordered per-guild/multi-account
    // dispatch if measured throughput requires concurrency.
    internal static readonly object MembershipLock = new();
    private static readonly Lazy<Dictionary<string, string>> Config = new(() => TableReaderV2.Parse<ConfigTable>().ToDictionary(row => row.Key, row => row.Value));
    private static readonly Lazy<Dictionary<int, ConditionTable>> Conditions = new(() => TableReaderV2.Parse<ConditionTable>().ToDictionary(row => row.Id));
    private static int Setting(string name) => int.Parse(Config.Value[name], CultureInfo.InvariantCulture);
    private static GuildLevelTable Level(Guild guild) => TableReaderV2.Parse<GuildLevelTable>().Single(row => row.Level == guild.Level);
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private static Guild? Resolve(Session session, int id) => id == 0 ? Guild.FindByMember(session.player.PlayerData.Id) : id > 0 && Guild.FindById((uint)id) is { Active: true } guild ? guild : null;
    private static bool IsOnline(long id) => Server.Instance.SessionFromUID(id) is not null;
    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool valid, int code) { if (!valid) throw new ServerCodeException("Guild request rejected", code); }
    private static int Failure(Exception exception, int fallback) => exception is ServerCodeException code ? code.Code : fallback;

    internal static bool ConditionSatisfied(Session session, int id, HashSet<int> visiting)
    {
        if (!Conditions.Value.TryGetValue(id, out ConditionTable? condition) || !visiting.Add(id)) return false;
        try
        {
            if (!string.IsNullOrWhiteSpace(condition.Formula))
            {
                bool any = condition.Formula.Contains('|');
                if (any && condition.Formula.Contains('&')) return false;
                string[] terms = condition.Formula.Split(any ? '|' : '&', StringSplitOptions.RemoveEmptyEntries);
                bool Evaluate(string term) => int.TryParse(term.Trim(), out int child) && ConditionSatisfied(session, child, visiting);
                return terms.Length > 0 && (any ? terms.Any(Evaluate) : terms.All(Evaluate));
            }
            return condition.Params.Count > 0 && (condition.Type switch
            {
                10101 => session.player.PlayerData.Level >= condition.Params[0],
                10105 => session.stage.Stages.TryGetValue(condition.Params[0], out StageDatum? stage) && stage.Passed,
                _ => false
            });
        }
        finally { visiting.Remove(id); }
    }

    private static bool GuildOpen(Session session) => TableReaderV2.Parse<FunctionalOpenTable>()
        .Single(row => row.Id == 1901).Condition.All(id => ConditionSatisfied(session, id, []));

    private static Dictionary<int, int> CreationCosts()
    {
        GuildCreateTable rule = TableReaderV2.Parse<GuildCreateTable>().Single();
        if (rule.ItemId.Count != rule.ItemNum.Count) throw new InvalidDataException("Guild creation cost table mismatch.");
        return rule.ItemId.Select((id, index) => (Id: id, Count: rule.ItemNum[index]))
            .GroupBy(cost => cost.Id).ToDictionary(group => group.Key, group => group.Sum(cost => cost.Count));
    }

    private static void RecoverMembershipProgress(Session session, bool sendNotification = true)
    {
        Guild? guild = Guild.FindByMember(session.player.PlayerData.Id);
        if (guild is null || session.player.GuildProgressRecordedId == guild.Id) return;
        TaskModule.EnsureMissionResets(session);
        session.player.GuildProgressRecordedId = guild.Id;
        try
        {
            TaskModule.RecordTableDrivenProgress(session, [(35002, null, 1)], sendNotification);
        }
        catch
        {
            // Resolve an uncertain save from durable state before an in-session retry.
            Player persisted = Player.collection.Find(player => player.PlayerData.Id == session.player.PlayerData.Id).Single();
            session.player.GuildProgressRecordedId = persisted.GuildProgressRecordedId;
            session.player.MissionProgress = persisted.MissionProgress;
            throw;
        }
    }

    internal static void PrepareLogin(Session session)
    {
        lock (MembershipLock)
        {
            RecoverPending(session, false);
            Guild? pending = Guild.FindPendingByFounder(session.player.PlayerData.Id);
            if (pending is not null && session.inventory.AppliedRewardClaims.Contains($"guild-create:{pending.Id}"))
                CompleteCreation(session, pending, false);
            PrepareManagement(session);
            Guild? guild = FindMembership(session.player.PlayerData.Id);
            if (guild is not null)
            {
                GuildMutation mutation = new(session, guild);
                PrepareEconomy(mutation);
                PrepareSign(mutation, session.player.PlayerData.Id);
                Persist(mutation, guild, 0, string.Empty, string.Empty, [], false);
            }
            RecoverMembershipProgress(session, sendNotification: false);
        }
    }

    internal static NotifyGuildData BuildLoginData(Session session)
    {
        Guild? guild = FindMembership(session.player.PlayerData.Id);
        return guild is null ? new NotifyGuildData { GuildName = string.Empty, GuildRankLevel = NoGuildRank,
            HasRecruit = Guild.HasRecruit(session.player.PlayerData.Id, Now) } :
            BuildLoginData(guild, session.player.PlayerData.Id, session.player.GuildState);
    }

    internal static NotifyGuildData BuildLoginData(Guild guild, long uid, GuildPlayerState? state = null)
    {
        state ??= Server.Instance.SessionFromUID(uid)?.player.GuildState ?? Player.TryFromPlayerId(uid)?.GuildState ?? new();
        NotifyGuildData response = new()
        {
            GuildId = guild.Id, GuildName = guild.Name, GuildLevel = guild.Level, IconId = guild.IconId,
            GuildRankLevel = Rank(guild, uid), FreeChangeGuildNameCount = guild.FreeChangeGuildNameCount,
            HasRecruit = Guild.HasRecruit(uid, Now), BossEndTime = checked((uint)GuildBossModule.NextEndTime(DateTimeOffset.UtcNow))
        };
        ProjectEconomyLogin(guild, state, response);
        return response;
    }

    private static void CompleteCreation(Session session, Guild guild, bool notify = true, Packet.Request? packet = null)
    {
        GuildCreateTable rule = TableReaderV2.Parse<GuildCreateTable>().Single();
        try { Guild.EnsureCreationQuota(guild, DailyPeriod(DateTimeOffset.UtcNow), rule.DailyLimit); }
        catch (InvalidOperationException exception) when (exception.Message == "GuildCreateReachDailyLimit")
        {
            throw new ServerCodeException(exception.Message, 20063325);
        }
        guild = Guild.FindById(guild.Id) ?? throw new InvalidOperationException("Guild reservation disappeared");
        if (guild.PendingOperation is not null) { CompletePending(session, guild, notify); return; }
        if (guild.Active) return;
        session.inventory = Inventory.FromUid(session.player.PlayerData.Id);
        string claimKey = $"guild-create:{guild.Id}";
        if (guild.CreationCosts.Count == 0)
        {
            guild.CreationCosts = CreationCosts();
            guild.SaveChecked();
        }
        Require(session.inventory.AppliedRewardClaims.Contains(claimKey)
            || guild.CreationCosts.All(cost => (session.inventory.Items.FirstOrDefault(item => item.Id == cost.Key)?.Count ?? 0) >= cost.Value), 20063018);
        GuildMutation mutation = new(session, guild);
        mutation.Guild.Active = true;
        mutation.Guild.CreationReserved = false;
        mutation.AddCostGrant(session.player.PlayerData.Id, claimKey, guild.CreationCosts);
        PrepareEconomy(mutation);
        AddNews(mutation, 1001, session.player.PlayerData.Name);
        byte[] responseBody = packet is null ? [] : MessagePackSerializer.Serialize(new GuildCreateResponse());
        string requestKey = packet is null ? string.Empty : RequestKey(session, packet);
        if (packet is not null)
            mutation.Player(session.player.PlayerData.Id).RequestReceipts.Add(new() { RequestId = packet.Id,
                RequestKey = requestKey, ResponseName = nameof(GuildCreateResponse), ResponseBody = responseBody });
        Persist(mutation, guild, packet?.Id ?? 0, requestKey, packet is null ? string.Empty : nameof(GuildCreateResponse), responseBody, notify);
    }

    [RequestPacketHandler("GuildCreateRequest")]
    public static void GuildCreateRequestHandler(Session session, Packet.Request packet)
    {
        GuildCreateRequest request = packet.Deserialize<GuildCreateRequest>();
        GuildCreateResponse response = new();
        try
        {
            lock (MembershipLock)
            {
                RecoverPending(session, false);
                GuildRequestReceipt? receipt = session.player.GuildState.RequestReceipts.LastOrDefault(value =>
                    value.RequestId == packet.Id && value.RequestKey == RequestKey(session, packet)
                    && value.ResponseName == nameof(GuildCreateResponse));
                if (receipt is not null)
                {
                    session.SendPush(BuildLoginData(session));
                    session.SendResponse(receipt.ResponseName, receipt.ResponseBody, packet.Id);
                    return;
                }
                Guild? existing = Guild.FindByMember(session.player.PlayerData.Id);
                if (existing is not null)
                {
                    Require(existing.LeaderId == session.player.PlayerData.Id && existing.Name == request.GuildName
                        && existing.Declaration == request.GuildDeclaration && existing.IconId == request.IconId, 20063010);
                }
                else
                {
                    Guild? guild = Guild.FindPendingByFounder(session.player.PlayerData.Id);
                    Require(guild is null || (guild.Name == request.GuildName
                        && guild.Declaration == request.GuildDeclaration && guild.IconId == request.IconId), 20063009);
                    if (guild is null)
                    {
                        Require(GuildOpen(session), 20063324);
                        Require(session.player.GuildState.JoinCdEnd <= Now, 20063016);
                        Require(!string.IsNullOrWhiteSpace(request.GuildName), 20063002);
                        int length = new StringInfo(request.GuildName).LengthInTextElements;
                        Require(length >= Setting("GuildNameMinLen") && length <= Setting("GuildNameMaxLen"), 20063003);
                        Require(!request.GuildName.Any(char.IsWhiteSpace) && !request.GuildName.Any(char.IsControl), 20063004);
                        Require(!string.IsNullOrWhiteSpace(request.GuildDeclaration), 20063005);
                        Require(new StringInfo(request.GuildDeclaration).LengthInTextElements <= Setting("GuildDeclarationMaxLen"), 20063006);
                        Require(!request.GuildDeclaration.Any(char.IsControl), 20063007);
                        GuildHeadPortraitTable? icon = TableReaderV2.Parse<GuildHeadPortraitTable>().SingleOrDefault(row => row.Id == request.IconId);
                        Require(icon is not null && !(icon.ConditionId > 0) && !(icon.Cost > 0), 20063017);
                        GuildCreateTable rule = TableReaderV2.Parse<GuildCreateTable>().Single();
                        Require(ConditionSatisfied(session, rule.ConditionIds, []), 20063324);
                        Dictionary<int, int> costs = CreationCosts();
                        Require(costs.All(cost => (session.inventory.Items.FirstOrDefault(item => item.Id == cost.Key)?.Count ?? 0) >= cost.Value), 20063018);
                        GuildLevelTable initial = TableReaderV2.Parse<GuildLevelTable>().MinBy(row => row.Level)!;
                        guild = Guild.ReserveCreation(new Guild
                        {
                            Name = request.GuildName, Declaration = request.GuildDeclaration, IconId = request.IconId,
                            LeaderId = session.player.PlayerData.Id, CreatedAt = Now, Level = initial.Level,
                            Option = NeedsApproval, MinLevel = 1, MaxMembers = initial.Capacity,
                            MaxTourists = initial.PositionNum[2], CreationCosts = costs
                        });
                    }
                    if (!guild.Active) CompleteCreation(session, guild, packet: packet);
                }
                RecoverMembershipProgress(session);
                session.SendPush(BuildLoginData(session));
            }
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey) { response.Code = 20063012; }
        catch (Exception exception) { response.Code = Failure(exception, 20063019); }
        session.SendResponse(response, packet.Id);
    }

    internal static object Summary(Guild guild) => new
    {
        Id = guild.Id, Name = guild.Name, IconId = guild.IconId, Level = guild.Level,
        MemberCount = guild.MemberIds.Count(uid => Rank(guild, uid) != 5),
        ContributeIn7Days = RecentContribution(guild)
    };

    [RequestPacketHandler("GuildListRecommendRequest")]
    public static void GuildListRecommendRequestHandler(Session session, Packet.Request packet)
    {
        GuildListRecommendRequest request = packet.Deserialize<GuildListRecommendRequest>();
        Guild? membership = FindMembership(session.player.PlayerData.Id);
        if (membership is not null && Rank(membership, session.player.PlayerData.Id) != 5)
        {
            session.SendResponse(new GuildListRecommendResponse { Code = 20063040, JoinCdEnd = session.player.GuildState.JoinCdEnd }, packet.Id);
            return;
        }
        int pageSize = Setting("GuildRecommendCountPage");
        session.SendResponse(new GuildListRecommendResponse
        {
            JoinCdEnd = session.player.GuildState.JoinCdEnd,
            Datas = request.PageNo <= 0 ? [] : Guild.AllActive().Where(guild => guild.Option != ForbiddenAdmission && guild.MemberIds.Count(uid => Rank(guild, uid) != 5) < Level(guild).Capacity)
                .OrderByDescending(RecentContribution).ThenBy(guild => guild.Id)
                .Skip((int)Math.Min(int.MaxValue, (long)(request.PageNo - 1) * pageSize)).Take(pageSize).Select(Summary).ToList()
        }, packet.Id);
    }

    [RequestPacketHandler("GuildFindRequest")]
    public static void GuildFindRequestHandler(Session session, Packet.Request packet)
    {
        GuildFindRequest request = packet.Deserialize<GuildFindRequest>();
        Guild? guild = request.GuildId > 0 ? Resolve(session, request.GuildId) : null;
        session.SendResponse(new GuildFindResponse { GuildList = guild is null ? [] : [Summary(guild)] }, packet.Id);
    }

    [RequestPacketHandler("GuildApplyRequest")]
    public static void GuildApplyRequestHandler(Session session, Packet.Request packet)
    {
        GuildApplyRequest request = packet.Deserialize<GuildApplyRequest>();
        if (request.GuildId <= 0) { session.SendResponse(new GuildApplyResponse { Code = 20063026 }, packet.Id); return; }
        HandleTarget<GuildApplyRequest, GuildApplyResponse>(session, packet, (uint)request.GuildId, (mutation, input, response) =>
        {
            Guild guild = mutation.Guild;
            long uid = session.player.PlayerData.Id;
            Guild? membership = FindMembership(uid);
            Require(membership is null || membership.Id == guild.Id, 20063022);
            if (membership is not null && Rank(guild, uid) != 5) { response.IsPass = true; return; }
            Require(GuildOpen(session) && session.player.PlayerData.Level >= guild.MinLevel, 20063038);
            Require(mutation.Player(uid).JoinCdEnd <= Now, 20063021);
            Require(guild.Option != ForbiddenAdmission, 20063037);
            Require(guild.MemberIds.Count(id => Rank(guild, id) != 5) < Level(guild).Capacity, 20063028);
            if (guild.Option == DirectAdmission)
            {
                Admit(mutation, uid);
                response.IsPass = true;
            }
            else
            {
                Require(guild.Option == NeedsApproval, 20063037);
                long cutoff = Now - Setting("GuildApplyTimeoutSec");
                guild.Applications.RemoveAll(application => application.CreatedAt <= cutoff);
                bool alreadyPending = guild.Applications.Any(application => application.PlayerId == uid);
                Require(alreadyPending || Guild.collection.CountDocuments(value => value.Active
                    && value.Applications.Any(application => application.PlayerId == uid && application.CreatedAt > cutoff))
                    < Setting("GuildApplyPlayerMaxCount"), 20063023);
                Require(alreadyPending || guild.Applications.Count < Setting("GuildApplyGuildMaxCount"), 20063027);
                if (!alreadyPending) guild.Applications.Add(new() { PlayerId = uid, CreatedAt = Now });
                mutation.Broadcast(new NotifyGuildEvent { Type = 5, Value = checked((uint)uid) });
            }
        }, allowTourist: true, membershipCode: 20063026, fallbackCode: 20063025);
    }

    [RequestPacketHandler("GuildAckApplyRequest")]
    public static void GuildAckApplyRequestHandler(Session session, Packet.Request packet) =>
        Handle<GuildAckApplyRequest, GuildAckApplyResponse>(session, packet, (mutation, request, response) =>
        {
            Guild guild = mutation.Guild;
            RequirePermission(guild, session.player.PlayerData.Id, GuildPermission.ManageApplications);
            bool admitted = guild.MemberIds.Contains(request.PlayId) && Rank(guild, request.PlayId) != 5;
            Require(admitted || guild.Applications.Any(application => application.PlayerId == request.PlayId
                && application.CreatedAt > Now - Setting("GuildApplyTimeoutSec")), 20063034);
            if (request.IsAgree)
            {
                Require(FindMembership(request.PlayId) is not { } other || other.Id == guild.Id, 20063033);
                if (!admitted) Admit(mutation, request.PlayId);
            }
            else guild.Applications.RemoveAll(application => application.PlayerId == request.PlayId);
            mutation.Broadcast(new NotifyGuildEvent { Type = 5, Value = checked((uint)request.PlayId) });
        }, fallbackCode: 20063036);

    [RequestPacketHandler("GuildListApplyRequest")]
    public static void GuildListApplyRequestHandler(Session session, Packet.Request packet)
    {
        GuildListApplyResponse response = new();
        Guild? guild = Guild.FindByMember(session.player.PlayerData.Id);
        if (guild is null) response.Code = 20063189;
        else if (Rank(guild, session.player.PlayerData.Id) is not (1 or 2)) response.Code = 20063193;
        else foreach (GuildApplication application in guild.Applications.Where(application => application.CreatedAt > Now - Setting("GuildApplyTimeoutSec")))
        {
            Player? player = Player.TryFromPlayerId(application.PlayerId);
            if (player is null) continue;
            response.Data.Add(new
            {
                PlayerId = player.PlayerData.Id, PlayerName = player.PlayerData.Name, Level = player.PlayerData.Level,
                HeadPortraitId = player.PlayerData.CurrHeadPortraitId, HeadFrameId = player.PlayerData.CurrHeadFrameId,
                // EN XGuildConfig.GuildCoin is item 39 (distinct from contribution item 38).
                GuildCoin = Inventory.collection.Find(inventory => inventory.Uid == player.PlayerData.Id).FirstOrDefault()?.Items.FirstOrDefault(item => item.Id == 39)?.Count ?? 0,
                LastLoginTime = player.PlayerData.LastLoginTime, OnlineFlag = IsOnline(player.PlayerData.Id) ? 1 : 0
            });
        }
        session.SendResponse(response, packet.Id);
    }

    [RequestPacketHandler("GuildListDetailRequest")]
    public static void GuildListDetailRequestHandler(Session session, Packet.Request packet)
    {
        PrepareManagement(session);
        int requestedId = packet.Deserialize<GuildListDetailRequest>().GuildId;
        Guild? guild = Resolve(session, requestedId);
        GuildListDetailResponse response = new() { Code = guild is null ? requestedId == 0 ? 20063043 : 20063042 : 0,
            GuildName = string.Empty, GuildLeaderName = string.Empty, GuildDeclaration = string.Empty, RankNames = string.Empty };
        if (guild is not null)
        {
            guild.Normalize();
            GuildLevelTable level = Level(guild);
            response.GuildId = guild.Id; response.GuildName = guild.Name; response.GuildIconId = guild.IconId; response.GuildLevel = guild.Level;
            response.GuildMemberCount = guild.MemberIds.Count(uid => Rank(guild, uid) != 5); response.GuildMemberMaxCount = level.Capacity;
            response.GuildTouristCount = guild.MemberIds.Count(uid => Rank(guild, uid) == 5); response.GuildTouristMaxCount = level.PositionNum[2];
            response.GuildLeaderName = Player.TryFromPlayerId(guild.LeaderId)?.PlayerData.Name ?? string.Empty;
            response.GuildDeclaration = guild.Declaration; response.Option = guild.Option; response.MinLevel = guild.MinLevel;
            response.RankNames = guild.RankNames; response.Notice = guild.Notice;
            ProjectEconomyDetail(guild, guild.MemberIds.Contains(session.player.PlayerData.Id) ? session.player.GuildState : new(), response);
        }
        session.SendResponse(response, packet.Id);
    }

    [RequestPacketHandler("GuildMemberDetailRequest")]
    public static void GuildMemberDetailRequestHandler(Session session, Packet.Request packet)
    {
        PrepareManagement(session);
        Guild? guild = Resolve(session, packet.Deserialize<GuildMemberDetailRequest>().GuildId);
        GuildMemberDetailResponse response = new() { Code = guild is null ? 20063026 : 0, GuildId = guild?.Id ?? 0 };
        if (guild is not null)
        {
            guild.Normalize();
            response.CanImpeach = CanImpeach(guild, session.player.PlayerData.Id);
            response.HasImpeach = HasImpeached(guild, session.player.PlayerData.Id);
        }
        if (guild is not null) foreach (long id in guild.MemberIds)
        {
            Player? player = id == session.player.PlayerData.Id ? session.player : Player.TryFromPlayerId(id);
            if (player is null) continue;
            response.MembersData.Add(new()
            {
                Id = checked((uint)id), Name = player.PlayerData.Name, HeadPortraitId = checked((uint)player.PlayerData.CurrHeadPortraitId),
                HeadFrameId = (int)player.PlayerData.CurrHeadFrameId, Level = (int)player.PlayerData.Level,
                RankLevel = Rank(guild, id), ContributeIn7Days = RecentMemberContribution(guild, id),
                ContributeAct = guild.Members[id].ActiveContribute, ContributeHistory = guild.Members[id].TotalContribute,
                Popularity = guild.Members[id].Popularity,
                LastLoginTime = (uint)player.PlayerData.LastLoginTime, OnlineFlag = IsOnline(id) ? 1 : 0
            });
        }
        session.SendResponse(response, packet.Id);
    }

}
