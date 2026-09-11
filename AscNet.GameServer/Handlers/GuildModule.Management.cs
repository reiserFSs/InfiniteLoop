using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using MessagePack;
using MongoDB.Driver;

namespace AscNet.GameServer.Handlers;

[MessagePackObject(true)] public class GuildRecruitRequest { public long PlayId { get; set; } }
[MessagePackObject(true)] public class GuildRecruitResponse : GuildResponse { }
[MessagePackObject(true)] public class GuildListRecruitResponse : GuildResponse { public List<GuildRecruitPlayerData> Data { get; set; } = []; }
[MessagePackObject(true)] public class GuildAckRecruitRequest { public int GuildId { get; set; } public bool IsAgree { get; set; } }
[MessagePackObject(true)] public class GuildAckRecruitResponse : GuildResponse { }
[MessagePackObject(true)] public class GuildTouristRequest { public int GuildId { get; set; } }
[MessagePackObject(true)] public class GuildTouristResponse : GuildResponse { }
[MessagePackObject(true)] public class GuildQuitTouristResponse : GuildResponse { }
[MessagePackObject(true)] public class GuildQuitResponse : GuildResponse { }
[MessagePackObject(true)] public class GuildKickMemberRequest { public long OtherId { get; set; } }
[MessagePackObject(true)] public class GuildKickMemberResponse : GuildResponse { }
[MessagePackObject(true)] public class GuildChangeRankRequest { public long PlayerId { get; set; } public int NewRank { get; set; } }
[MessagePackObject(true)] public class GuildChangeRankResponse : GuildResponse { }
[MessagePackObject(true)] public class GuildImpeachResponse : GuildResponse { }
[MessagePackObject(true)] public class NotifyGuildImpeach { public int State { get; set; } }
[MessagePackObject(true)] public class GuildRecruitPlayerData
{
    public long PlayerId { get; set; }
    public string PlayerName { get; set; } = string.Empty;
    public int Level { get; set; }
    public uint HeadPortraitId { get; set; }
    public uint HeadFrameId { get; set; }
    public long GuildCoin { get; set; }
    public long LastLoginTime { get; set; }
    public int OnlineFlag { get; set; }
    public uint GuildId { get; set; }
    public string GuildName { get; set; } = string.Empty;
}

internal partial class GuildModule
{
    internal static void Admit(GuildMutation mutation, long uid, int rank = 4)
    {
        Guild guild = mutation.Guild;
        Guild? existing = Guild.FindByMember(uid);
        Require(existing is null || existing.Id == guild.Id, 20063033);
        Require(Player.TryFromPlayerId(uid) is not null, 20063222);
        Require(rank is 4 or 5, 20063085);
        int oldRank = Rank(guild, uid);
        if (oldRank == rank) return;
        if (rank == 4)
        {
            Require(mutation.Player(uid).JoinCdEnd <= Now, 20063171);
            Require(guild.MemberIds.Count(id => Rank(guild, id) != 5) < Level(guild).Capacity, 20063035);
        }
        else Require(guild.MemberIds.Count(id => Rank(guild, id) == 5) < Level(guild).PositionNum[2], 20063143);
        if (!guild.MemberIds.Contains(uid)) guild.MemberIds.Add(uid);
        guild.Members[uid] = new GuildMemberState { Rank = rank, JoinedAt = Now };
        guild.Applications.RemoveAll(application => application.PlayerId == uid);
        guild.Invites.RemoveAll(invite => invite.PlayerId == uid);
        if (rank == 5) guild.TouristExpiresAt[uid] = checked(Now + Setting("GuildTouristDuration") * 3600L);
        else guild.TouristExpiresAt.Remove(uid);
        AddNews(mutation, 1013, Player.TryFromPlayerId(uid)!.PlayerData.Name);
        mutation.Broadcast(new NotifyGuildEvent { Type = 12, Value = checked((uint)guild.MemberIds.Count(id => Rank(guild, id) != 5)) });
    }

    internal static void RemoveMember(GuildMutation mutation, long uid)
    {
        Guild guild = mutation.Guild;
        if (!guild.MemberIds.Remove(uid)) return;
        guild.Members.Remove(uid);
        guild.TouristExpiresAt.Remove(uid);
        guild.Applications.RemoveAll(application => application.PlayerId == uid);
        guild.Invites.RemoveAll(invite => invite.PlayerId == uid || invite.InviterId == uid);
        guild.ImpeachPetitioners.Remove(uid);
        if (Player.TryFromPlayerId(uid) is { } player) AddNews(mutation, 1014, player.PlayerData.Name);
        mutation.Broadcast(new NotifyGuildEvent { Type = 12, Value = checked((uint)guild.MemberIds.Count(id => Rank(guild, id) != 5)) });
    }

    static partial void OnMembershipChangedState(long uid)
    {
        // Atomic pull updates cannot replace another guild's concurrently staged state.
        Guild.collection.UpdateMany(guild => guild.Active,
            Builders<Guild>.Update.PullFilter(guild => guild.Applications, application => application.PlayerId == uid)
                .PullFilter(guild => guild.Invites, invite => invite.PlayerId == uid));
        GuildDormModule.OnMembershipChanged(uid);
        GuildBossModule.OnMembershipChanged(uid);
        GuildWarModule.OnMembershipChanged(uid);
    }

    private static GuildRecruitPlayerData RecruitPlayer(Player player, Guild? guild = null) => new()
    {
        PlayerId = player.PlayerData.Id, PlayerName = player.PlayerData.Name,
        Level = checked((int)player.PlayerData.Level), HeadPortraitId = checked((uint)player.PlayerData.CurrHeadPortraitId),
        HeadFrameId = checked((uint)player.PlayerData.CurrHeadFrameId), LastLoginTime = player.PlayerData.LastLoginTime,
        GuildCoin = Inventory.collection.Find(inventory => inventory.Uid == player.PlayerData.Id).FirstOrDefault()?.Items.FirstOrDefault(item => item.Id == 39)?.Count ?? 0,
        OnlineFlag = IsOnline(player.PlayerData.Id) ? 1 : 0, GuildId = guild?.Id ?? 0, GuildName = guild?.Name ?? string.Empty
    };

    [RequestPacketHandler("GuildRecruitRequest")]
    public static void GuildRecruitRequestHandler(Session session, Packet.Request packet) =>
        Handle<GuildRecruitRequest, GuildRecruitResponse>(session, packet, (mutation, request, response) =>
        {
            Guild guild = mutation.Guild;
            long uid = session.player.PlayerData.Id;
            Require(Rank(guild, uid) is 1 or 2, 20063159);
            Player? target = Player.TryFromPlayerId(request.PlayId);
            Require(target is not null && request.PlayId != uid, 20063161);
            Require(Guild.FindByMember(request.PlayId) is null, 20063160);
            Require(target!.PlayerData.Level >= Math.Max(guild.MinLevel, Setting("GuildPlayerRecommendLevel")), 20063161);
            guild.Invites.RemoveAll(invite => invite.ExpiresAt <= Now);
            Require(!guild.Invites.Any(invite => invite.PlayerId == request.PlayId), 20063162);
            Require(guild.Invites.Count < Setting("GuildRecruitGuildMaxCount"), 20063163);
            long period = DailyPeriod(DateTimeOffset.UtcNow);
            if (guild.RecruitPeriod != period) { guild.RecruitPeriod = period; guild.RecruitCount = 0; }
            Require(guild.RecruitCount < Setting("GuildRecruitGuildDailyCount"), 20063236);
            Require(Guild.AllActive().Sum(value => value.Invites.Count(invite => invite.PlayerId == request.PlayId && invite.ExpiresAt > Now)) < Setting("GuildRecruitPlayerMaxCount"), 20063163);
            guild.Invites.Add(new GuildInvite { PlayerId = request.PlayId, InviterId = uid, CreatedAt = Now, ExpiresAt = checked(Now + Setting("GuildRecruitTimeout") * 3600L) });
            guild.RecruitCount++;
            mutation.Push(request.PlayId, new NotifyGuildEvent { Type = 13, Value = 1 });
        }, membershipCode: 20063155, fallbackCode: 20063156);

    [RequestPacketHandler("GuildListRecruitRequest")]
    public static void GuildListRecruitRequestHandler(Session session, Packet.Request packet)
    {
        GuildListRecruitResponse response = new();
        try
        {
            lock (MembershipLock)
            {
                long uid = session.player.PlayerData.Id;
                Require(Guild.FindByMember(uid) is null, 20063164);
                foreach (Guild guild in Guild.AllActive())
                    foreach (GuildInvite invite in guild.Invites.Where(invite => invite.PlayerId == uid && invite.ExpiresAt > Now))
                        if (Player.TryFromPlayerId(invite.InviterId) is { } inviter) response.Data.Add(RecruitPlayer(inviter, guild));
            }
        }
        catch (Exception exception) { response.Code = Failure(exception, 20063165); }
        session.SendResponse(response, packet.Id);
    }

    [RequestPacketHandler("GuildAckRecruitRequest")]
    public static void GuildAckRecruitRequestHandler(Session session, Packet.Request packet)
    {
        GuildAckRecruitRequest request = packet.Deserialize<GuildAckRecruitRequest>();
        if (request.GuildId <= 0) { session.SendResponse(new GuildAckRecruitResponse { Code = 20063168 }, packet.Id); return; }
        HandleTarget<GuildAckRecruitRequest, GuildAckRecruitResponse>(session, packet, request.GuildId > 0 ? (uint)request.GuildId : 0, (mutation, value, response) =>
        {
            long uid = session.player.PlayerData.Id;
            Guild? membership = Guild.FindByMember(uid);
            if (value.IsAgree && membership?.Id == mutation.Guild.Id && Rank(mutation.Guild, uid) != 5) return;
            Require(membership is null, 20063166);
            Require(mutation.Guild.Invites.Any(invite => invite.PlayerId == uid && invite.ExpiresAt > Now), 20063169);
            if (value.IsAgree)
            {
                Require(mutation.Player(uid).JoinCdEnd <= Now, 20063171);
                Require(session.player.PlayerData.Level >= mutation.Guild.MinLevel, 20063038);
                Require(mutation.Guild.MemberIds.Count(id => Rank(mutation.Guild, id) != 5) < Level(mutation.Guild).Capacity, 20063170);
                Admit(mutation, uid);
            }
            else mutation.Guild.Invites.RemoveAll(invite => invite.PlayerId == uid);
        }, membershipCode: 20063168, fallbackCode: 20063167);
    }

    [RequestPacketHandler("GuildTouristRequest")]
    public static void GuildTouristRequestHandler(Session session, Packet.Request packet)
    {
        GuildTouristRequest request = packet.Deserialize<GuildTouristRequest>();
        if (request.GuildId <= 0) { session.SendResponse(new GuildTouristResponse { Code = 20063141 }, packet.Id); return; }
        HandleTarget<GuildTouristRequest, GuildTouristResponse>(session, packet, request.GuildId > 0 ? (uint)request.GuildId : 0, (mutation, value, response) =>
        {
            long uid = session.player.PlayerData.Id;
            Guild? membership = Guild.FindByMember(uid);
            if (membership?.Id == mutation.Guild.Id && Rank(mutation.Guild, uid) == 5) return;
            Require(membership is null, 20063139);
            Require(GuildOpen(session), 20063038);
            Admit(mutation, uid, 5);
        }, membershipCode: 20063141, fallbackCode: 20063142);
    }

    [RequestPacketHandler("GuildQuitTouristRequest")]
    public static void GuildQuitTouristRequestHandler(Session session, Packet.Request packet) =>
        Handle<GuildEmptyRequest, GuildQuitTouristResponse>(session, packet, (mutation, request, response) =>
        {
            long uid = session.player.PlayerData.Id;
            Require(Rank(mutation.Guild, uid) == 5, 20063145);
            RemoveMember(mutation, uid);
        }, allowTourist: true, membershipCode: 20063144, fallbackCode: 20063145);

    [RequestPacketHandler("GuildQuitRequest")]
    public static void GuildQuitRequestHandler(Session session, Packet.Request packet) =>
        Handle<GuildEmptyRequest, GuildQuitResponse>(session, packet, (mutation, request, response) =>
        {
            long uid = session.player.PlayerData.Id;
            Guild guild = mutation.Guild;
            Require(uid != guild.LeaderId || guild.MemberIds.Count(id => Rank(guild, id) != 5) == 1, 20063097);
            mutation.Player(uid).JoinCdEnd = checked(Now + Setting("GuildEnterCd") * 3600L);
            if (guild.LeaderId == uid)
            {
                foreach (long member in guild.MemberIds.ToArray()) RemoveMember(mutation, member);
                guild.Active = false;
                guild.CreationReserved = false;
                guild.LeaderId = 0;
                guild.Applications.Clear();
                guild.Invites.Clear();
                ClearImpeachment(guild);
            }
            else RemoveMember(mutation, uid);
        }, membershipCode: 20063094, fallbackCode: 20063098);

    [RequestPacketHandler("GuildKickMemberRequest")]
    public static void GuildKickMemberRequestHandler(Session session, Packet.Request packet) =>
        Handle<GuildKickMemberRequest, GuildKickMemberResponse>(session, packet, (mutation, request, response) =>
        {
            long uid = session.player.PlayerData.Id;
            Guild guild = mutation.Guild;
            Require(uid != request.OtherId, 20063101);
            Require(Rank(guild, uid) is 1 or 2, 20063103);
            Require(guild.MemberIds.Contains(request.OtherId), 20063104);
            Require(Rank(guild, uid) < Rank(guild, request.OtherId), 20063105);
            long period = DailyPeriod(DateTimeOffset.UtcNow);
            if (guild.KickPeriod != period) { guild.KickPeriod = period; guild.KickCount = 0; }
            Require(guild.KickCount < Setting("GuildKickCountDailyMax"), 20063106);
            guild.KickCount++;
            mutation.Broadcast(new NotifyGuildEvent { Type = 6, Value = checked((uint)request.OtherId) });
            RemoveMember(mutation, request.OtherId);
        }, membershipCode: 20063099, fallbackCode: 20063107);

    [RequestPacketHandler("GuildChangeRankRequest")]
    public static void GuildChangeRankRequestHandler(Session session, Packet.Request packet) =>
        Handle<GuildChangeRankRequest, GuildChangeRankResponse>(session, packet, (mutation, request, response) =>
        {
            Guild guild = mutation.Guild;
            long uid = session.player.PlayerData.Id;
            Require(uid != request.PlayerId, 20063084);
            Require(request.NewRank is >= 1 and <= 4, 20063085);
            Require(guild.MemberIds.Contains(request.PlayerId) && Rank(guild, request.PlayerId) != 5, 20063088);
            int actorRank = Rank(guild, uid), targetRank = Rank(guild, request.PlayerId);
            Require(actorRank is 1 or 2, 20063090);
            Require(actorRank != targetRank, 20063089);
            Require(actorRank < targetRank, 20063090);
            if (request.NewRank == 1)
            {
                Require(actorRank == 1 && guild.LeaderId == uid, 20063091);
                TransferLeader(mutation, request.PlayerId);
                return;
            }
            Require(actorRank < request.NewRank, 20063091);
            if (targetRank == request.NewRank) return;
            if (request.NewRank is 2 or 3)
                Require(guild.MemberIds.Count(id => Rank(guild, id) == request.NewRank) < Level(guild).PositionNum[request.NewRank - 2], 20063092);
            guild.Members[request.PlayerId].Rank = request.NewRank;
            mutation.Push(request.PlayerId, new NotifyGuildEvent { Type = 9, Value = checked((uint)request.NewRank) });
            string name = Player.TryFromPlayerId(request.PlayerId)!.PlayerData.Name;
            if (request.NewRank == 2) AddNews(mutation, 1012, name);
            else AddNews(mutation, 1015, name,
                AscNet.Common.Util.TableReaderV2.Parse<AscNet.Table.V2.share.guild.GuildPositionTable>().Single(row => row.Id == request.NewRank).Name);
        }, membershipCode: 20063083, fallbackCode: 20063093);

    private static void TransferLeader(GuildMutation mutation, long newLeader, bool voluntary = true)
    {
        Guild guild = mutation.Guild;
        long oldLeader = guild.LeaderId;
        guild.Members[oldLeader].Rank = 4;
        guild.Members[newLeader].Rank = 1;
        guild.LeaderId = newLeader;
        ClearImpeachment(guild);
        mutation.Push(oldLeader, new NotifyGuildEvent { Type = 9, Value = 4 });
        mutation.Push(newLeader, new NotifyGuildEvent { Type = 9, Value = 1 });
        mutation.Broadcast(new NotifyGuildImpeach { State = 0 });
        if (voluntary) AddNews(mutation, 1011, Player.TryFromPlayerId(oldLeader)?.PlayerData.Name ?? string.Empty, Player.TryFromPlayerId(newLeader)!.PlayerData.Name);
    }

    internal static bool CanImpeach(Guild guild, long uid)
    {
        if (!guild.MemberIds.Contains(uid) || uid == guild.LeaderId || Rank(guild, uid) == 5 || IsOnline(guild.LeaderId)) return false;
        Player? leader = Player.TryFromPlayerId(guild.LeaderId);
        return leader is not null && leader.PlayerData.LastLoginTime <= Now - Setting("GuildImpeachDays") * 86400L;
    }

    internal static bool HasImpeached(Guild guild, long uid) => guild.ImpeachLeaderId == guild.LeaderId && guild.ImpeachPetitioners.Contains(uid);

    [RequestPacketHandler("GuildImpeachRequest")]
    public static void GuildImpeachRequestHandler(Session session, Packet.Request packet) =>
        Handle<GuildEmptyRequest, GuildImpeachResponse>(session, packet, (mutation, request, response) =>
        {
            Guild guild = mutation.Guild;
            long uid = session.player.PlayerData.Id;
            Require(uid != guild.LeaderId, 20063120);
            Require(CanImpeach(guild, uid), 20063121);
            Require(!HasImpeached(guild, uid), 20063122);
            int[] items = Config.Value["GuildImpeachCostItem"].Split('|').Select(int.Parse).ToArray();
            int[] counts = Config.Value["GuildImpeachCostCount"].Split('|').Select(int.Parse).ToArray();
            if (items.Length != counts.Length) throw new InvalidDataException("Guild impeachment cost table mismatch.");
            for (int index = 0; index < items.Length; index++) mutation.AddCost(uid, items[index], counts[index]);
            if (guild.ImpeachLeaderId != guild.LeaderId || guild.ImpeachEndAt == 0)
            {
                ClearImpeachment(guild);
                guild.ImpeachLeaderId = guild.LeaderId;
                guild.ImpeachStartedAt = Now;
                // Explicit local-server policy: configured petition/extension durations are days.
                guild.ImpeachEndAt = checked(Now + Setting("GuildImpeachDuration") * 86400L);
            }
            guild.ImpeachPetitioners.Add(uid);
            mutation.Broadcast(new NotifyGuildImpeach { State = 1 });
            AddNews(mutation, 1009, session.player.PlayerData.Name);
        }, membershipCode: 20063118, fallbackCode: 20063123);

    private static void ClearImpeachment(Guild guild)
    {
        guild.ImpeachLeaderId = 0;
        guild.ImpeachStartedAt = 0;
        guild.ImpeachEndAt = 0;
        guild.ImpeachPetitioners.Clear();
    }

    internal static void PrepareManagement(Session session)
    {
        Guild? guild = Guild.FindByMember(session.player.PlayerData.Id);
        if (guild is not null) PrepareManagementTarget(session, guild);
    }

    internal static void PrepareManagementTarget(Session session, Guild guild)
    {
        if (!guild.TouristExpiresAt.Any(pair => pair.Value <= Now) && guild.ImpeachEndAt == 0) return;
        CommitTarget(session, guild, mutation =>
        {
            Guild current = mutation.Guild;
            foreach (long uid in current.TouristExpiresAt.Where(pair => pair.Value <= Now).Select(pair => pair.Key).ToArray())
                if (Rank(current, uid) == 5) RemoveMember(mutation, uid);
                else current.TouristExpiresAt.Remove(uid);
            if (current.ImpeachEndAt == 0) return;
            Player? leader = Player.TryFromPlayerId(current.LeaderId);
            if (current.ImpeachLeaderId != current.LeaderId || IsOnline(current.LeaderId) || leader is null || leader.PlayerData.LastLoginTime > current.ImpeachStartedAt)
            {
                ClearImpeachment(current);
                mutation.Broadcast(new NotifyGuildImpeach { State = 0 });
                return;
            }
            if (Now < current.ImpeachEndAt) return;
            // User-approved deterministic election: eligible petitioners, then rank/contribution/join/UID.
            long winner = current.ImpeachPetitioners.Where(uid => current.MemberIds.Contains(uid) && Rank(current, uid) is >= 2 and <= 4 && Player.TryFromPlayerId(uid) is not null)
                .OrderBy(uid => Rank(current, uid)).ThenByDescending(uid => RecentMemberContribution(current, uid))
                .ThenBy(uid => current.Members[uid].JoinedAt).ThenBy(uid => uid).FirstOrDefault();
            if (winner == 0) current.ImpeachEndAt = checked(Now + Setting("GuildImpeachExtend") * 86400L);
            else
            {
                long startedAt = current.ImpeachStartedAt;
                TransferLeader(mutation, winner, voluntary: false);
                string winnerName = Player.TryFromPlayerId(winner)!.PlayerData.Name;
                AddNews(mutation, 1010, winnerName);
                string content = string.Format(System.Globalization.CultureInfo.InvariantCulture, NewsTemplates.Value[1010].Content, winnerName);
                foreach (long uid in current.MemberIds.Where(uid => Rank(current, uid) != 5))
                {
                    if (Player.TryFromPlayerId(uid) is null) continue;
                    // Explicit local-server result-mail policy: authored event text, no attachments or expiry.
                    PlayerMail mail = new()
                    {
                        Id = $"guild-impeach:{Setting("GuildImpeachMailId")}:{current.Id}:{startedAt}:{uid}",
                        SendName = current.Name, Title = current.Name, Content = content, CreateTime = Now, SendTime = Now,
                        RewardGoodsList = MailModule.ResolveMailRewards(Setting("GuildImpeachMailId"))
                    };
                    mutation.AddMail(uid, mail);
                    mutation.Push(uid, new NotifyMails { NewMailList = [MailModule.ToNotify(mail)] });
                }
            }
        });
    }
}
