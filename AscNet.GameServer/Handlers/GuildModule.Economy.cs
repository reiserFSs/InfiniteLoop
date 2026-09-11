using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.guild;
using AscNet.Table.V2.share.trust;
using AscNet.GameServer.Game;
using System.Globalization;

namespace AscNet.GameServer.Handlers;

internal partial class GuildModule
{
    // Explicit local-server policy: the shipped EN/CN level rows omit all three wish limits.
    internal static class LocalGuildWishPolicy
    {
        public const int DailyPublishLimit = 1;
        public const int WishTarget = 5;
        public const int DailyDonationLimit = 5;
    }

    private static readonly Lazy<Dictionary<int, GuildGoodsTable>> EconomyGoods = new(() => TableReaderV2.Parse<GuildGoodsTable>().ToDictionary(x => x.Id));
    public static int RecentContribution(Guild guild)
    {
        long day = DailyPeriod(DateTimeOffset.UtcNow);
        return guild.ContributionDays.Where(x => x.Key > day - 7 && x.Key <= day).Sum(x => x.Value);
    }
    public static int RecentMemberContribution(Guild guild, long uid)
    {
        long day = DailyPeriod(DateTimeOffset.UtcNow);
        return guild.MemberContributionDays.Where(x => x.PlayerId == uid && x.Day > day - 7 && x.Day <= day).Sum(x => x.Count);
    }

    public static void PrepareEconomy(GuildMutation mutation)
    {
        Guild guild = mutation.Guild;
        long day = DailyPeriod(DateTimeOffset.UtcNow), week = WeeklyPeriod(DateTimeOffset.UtcNow);
        if (guild.EconomyWeek != week)
        {
            bool hadWeek = guild.EconomyWeek != long.MinValue;
            foreach (long uid in guild.MemberIds)
            {
                if (uid != mutation.ActorSession.player.PlayerData.Id && Player.TryFromPlayerId(uid) is null) continue;
                GuildPlayerState state = mutation.Player(uid);
                int contribution = guild.Members[uid].WeekContribute;
                if (hadWeek && guild.EconomyWeek == week - 1 && contribution >= Setting("GuildContributeRewardMin") && Rank(guild, uid) != 5)
                {
                    // Explicit local-server formula; coefficients and eligibility remain authored Config values.
                    state.PendingContributeReward = checked((int)Math.Floor(contribution * double.Parse(Config.Value["GuildContributeRewardCoA"], CultureInfo.InvariantCulture)) + Setting("GuildContributeRewardCoB"));
                    state.HasContributeReward = true;
                }
                else if (hadWeek) { state.PendingContributeReward = 0; state.HasContributeReward = false; }
                if (hadWeek) guild.Members[uid].WeekContribute = 0;
            }
            guild.EconomyWeek = week;
            guild.GiftContribute = hadWeek ? 0 : guild.GiftContribute;
            guild.GiftGuildLevel = guild.Level;
            if (hadWeek) mutation.Broadcast(new NotifyGuildEvent { Type = 8 });
        }
        if (guild.EconomyDay != day)
        {
            guild.Wishes.Clear();
            guild.EconomyDay = day;
            foreach (long expired in guild.ContributionDays.Keys.Where(x => x <= day - 7).ToList()) guild.ContributionDays.Remove(expired);
            guild.MemberContributionDays.RemoveAll(x => x.Day <= day - 7);
        }
        foreach (long uid in guild.MemberIds)
        {
            if (uid != mutation.ActorSession.player.PlayerData.Id && Player.TryFromPlayerId(uid) is null) continue;
            GuildPlayerState state = mutation.Player(uid);
            if (state.EconomyDay != day) { state.WishCount = 0; state.WishContributeCount = 0; state.EconomyDay = day; }
            if (state.EconomyWeek != week) { state.GiftLevelsGot.Clear(); state.GiftGuildGot = 0; state.EconomyWeek = week; }
        }
        // Missing source maintenance dues mean no destructive upkeep or invented debt.
        if (!HasAuthoredMaintenanceDues(guild))
        { guild.MaintainState = 0; guild.EmergenceTime = 0; }
        foreach (GuildHeadPortraitTable icon in TableReaderV2.Parse<GuildHeadPortraitTable>())
            if (OwnsGuildPortrait(guild, icon.Id) && !guild.HeadPortraits.Contains(icon.Id)) guild.HeadPortraits.Add(icon.Id);
    }

    private static int OptionalLevelInt(GuildLevelTable row, string name) => Convert.ToInt32(row.GetType().GetProperty(name)?.GetValue(row) ?? 0, CultureInfo.InvariantCulture);
    private static bool HasAuthoredMaintenanceDues(Guild guild)
    {
        GuildLevelTable level = Level(guild);
        return OptionalLevelInt(level, "Contribution") > 0 || OptionalLevelInt(level, "EmergencyMaintenance") > 0;
    }
    public static bool OwnsGuildPortrait(Guild guild, int id)
    {
        if (guild.HeadPortraits.Contains(id)) return true;
        GuildHeadPortraitTable? icon = TableReaderV2.Parse<GuildHeadPortraitTable>().FirstOrDefault(x => x.Id == id);
        if (icon is null || icon.Cost > 0) return false;
        if (icon.ConditionId is not int conditionId || conditionId <= 0) return true;
        if (!Conditions.Value.TryGetValue(conditionId, out var condition) || condition.Params.Count == 0) return false;
        return condition.Type switch { 16100 => guild.Level >= condition.Params[0], 22005 => guild.DormThemes.Contains(condition.Params[0]), _ => false };
    }
    private static void ConvertMaximumLevelBuild(Guild guild)
    {
        if (TableReaderV2.Parse<GuildLevelTable>().Any(x => x.Level > guild.Level)) return;
        int interval = Setting("GuildBuildIntervalWhenMaxLevel");
        int points = checked(guild.Build / interval * Setting("GuildTalentPointCountWhenMaxLevel"));
        guild.Build %= interval;
        guild.TalentPoint = checked(guild.TalentPoint + points);
        guild.TalentPointFromBuild = checked(guild.TalentPointFromBuild + points);
    }


    public static bool IsEconomyGoods(int itemId) => itemId is 38 or 40 or 46 or 62723 || EconomyGoods.Value.ContainsKey(itemId);
    public static long EconomyBalance(Guild guild, int itemId) => itemId switch { 38 => guild.ContributeLeft, 40 => guild.Build, 46 => guild.TalentPoint, 62723 => guild.ShopCoin, _ => 0 };
    public static void AddEconomyCost(GuildMutation mutation, long uid, int itemId, int count)
    {
        Require(count > 0, 20063357);
        if (!IsEconomyGoods(itemId)) { mutation.AddCost(uid, itemId, count); return; }
        Require(itemId is 38 or 40 or 46 or 62723, 20063357);
        Require(EconomyBalance(mutation.Guild, itemId) >= count, 20012004);
        switch (itemId)
        {
            case 38: mutation.Guild.ContributeLeft -= count; EconomyEvent(mutation, 1, mutation.Guild.ContributeLeft); break;
            case 40: mutation.Guild.Build -= count; EconomyEvent(mutation, 2, mutation.Guild.Build); break;
            case 46: mutation.Guild.TalentPoint -= count; EconomyEvent(mutation, 11, mutation.Guild.TalentPoint); break;
            case 62723: mutation.Guild.ShopCoin -= count; EconomyEvent(mutation, 15, mutation.Guild.ShopCoin); break;
        }
    }
    public static void AddEconomyGoods(GuildMutation mutation, long uid, int itemId, int count)
    {
        Require(count > 0, 20063355);
        Guild guild = mutation.Guild;
        switch (itemId)
        {
            case 38:
                Require(guild.MemberIds.Contains(uid) && Rank(guild, uid) != 5, 20063206);
                GuildMemberState member = guild.Members[uid];
                member.WeekContribute = checked(member.WeekContribute + count);
                member.TotalContribute = checked(member.TotalContribute + count);
                member.ActiveContribute = checked(member.ActiveContribute + count);
                guild.ContributeLeft = checked(guild.ContributeLeft + count);
                guild.GiftContribute = checked(guild.GiftContribute + count);
                long day = DailyPeriod(DateTimeOffset.UtcNow);
                guild.ContributionDays[day] = checked(guild.ContributionDays.GetValueOrDefault(day) + count);
                GuildContributionDay? memberDay = guild.MemberContributionDays.FirstOrDefault(x => x.PlayerId == uid && x.Day == day);
                if (memberDay is null) guild.MemberContributionDays.Add(new() { PlayerId = uid, Day = day, Count = count });
                else memberDay.Count = checked(memberDay.Count + count);
                RecordContributionRankEntry(guild);
                EconomyEvent(mutation, 1, guild.ContributeLeft); EconomyEvent(mutation, 3, guild.GiftContribute);
                return;
            case 40:
                guild.Build = checked(guild.Build + count);
                ConvertMaximumLevelBuild(guild);
                EconomyEvent(mutation, 11, guild.TalentPoint);
                EconomyEvent(mutation, 2, guild.Build); return;
            case 46: guild.TalentPoint = checked(guild.TalentPoint + count); EconomyEvent(mutation, 11, guild.TalentPoint); return;
            case 62723:
                int cap = Setting("GuildShopCoinLimit");
                long added = (long)guild.ShopCoin + count;
                guild.ShopCoin = (int)Math.Min(cap, added);
                if (added >= cap) mutation.Push(uid, new NotifyGuildShopCoinReachLimit());
                EconomyEvent(mutation, 15, guild.ShopCoin); return;
        }
        if (!EconomyGoods.Value.TryGetValue(itemId, out GuildGoodsTable? good)) { mutation.AddGoods(uid, itemId, count); return; }
        List<int> owned = good.Type switch { 1 => guild.DormThemes, 2 => guild.DormBgms, _ => throw new ServerCodeException("Unknown guild goods type", 20063356) };
        Require(count == 1 && !owned.Contains(good.TargetId), 20063355);
        owned.Add(good.TargetId);
        foreach (GuildHeadPortraitTable icon in TableReaderV2.Parse<GuildHeadPortraitTable>())
            if (OwnsGuildPortrait(guild, icon.Id) && !guild.HeadPortraits.Contains(icon.Id)) guild.HeadPortraits.Add(icon.Id);
        mutation.Broadcast(GoodsProjection(guild));
    }
    private static void EconomyEvent(GuildMutation mutation, int type, int value) => mutation.Broadcast(new NotifyGuildEvent { Type = type, Value = checked((uint)value) });
    private static NotifyGuildGoodsChange GoodsProjection(Guild guild) => new() { ShopCoin = guild.ShopCoin, HeadPortraits = [.. guild.HeadPortraits], DormThemes = [.. guild.DormThemes], DormBgms = [.. guild.DormBgms] };
    public static void ProjectEconomyLogin(Guild guild, GuildPlayerState state, NotifyGuildData result)
    { result.HasContributeReward = state.HasContributeReward ? 1 : 0; result.ShopCoin = guild.ShopCoin; result.HeadPortraits = [.. guild.HeadPortraits]; result.DormThemes = [.. guild.DormThemes]; result.DormBgms = [.. guild.DormBgms]; }
    public static void ProjectEconomyDetail(Guild guild, GuildPlayerState state, GuildListDetailResponse result)
    {
        result.GuildContributeLeft = guild.ContributeLeft; result.GuildContributeIn7Days = RecentContribution(guild);
        result.Build = guild.Build; result.GiftContribute = guild.GiftContribute; result.GiftGuildLevel = guild.GiftGuildLevel;
        result.GiftLevel = TableReaderV2.Parse<GuildGiftTable>().Where(x => x.GuildLevel == guild.GiftGuildLevel && x.GiftContribute <= guild.GiftContribute).Select(x => x.GiftLevel).DefaultIfEmpty().Max();
        result.GiftLevelGot = state.GiftLevelsGot.Cast<dynamic>().ToList(); result.GiftGuildGot = state.GiftGuildGot;
        result.TalentPointFromBuild = guild.TalentPointFromBuild; result.TalentSumLevel = guild.Talents.Values.Sum();
        bool hasDues = HasAuthoredMaintenanceDues(guild);
        result.MaintainState = hasDues ? guild.MaintainState : 0;
        result.EmergenceTime = hasDues ? checked((int)guild.EmergenceTime) : 0;
    }

    [RequestPacketHandler("GuildReleaseWishRequest")]
    public static void GuildReleaseWishRequestHandler(Session session, Packet.Request packet) => Handle<GuildReleaseWishRequest, GuildReleaseWishResponse>(session, packet, (m, r, response) =>
    {
        Require(TableReaderV2.Parse<CharacterTrustItemTable>().Any(x => x.Id == r.ItemId && x.FavorCharacterId.Count > 0), 20063066);
        GuildPlayerState state = m.Player(session.player.PlayerData.Id);
        Require(state.WishCount < LocalGuildWishPolicy.DailyPublishLimit, 20063067);
        m.Guild.Wishes.Add(new() { PlayerId = session.player.PlayerData.Id, ItemId = r.ItemId, Seq = checked(++m.Guild.NextWishSequence), MaxCount = LocalGuildWishPolicy.WishTarget });
        state.WishCount++;
    }, membershipCode: 20063065, fallbackCode: 20063070);

    [RequestPacketHandler("GuildListWishRequest")]
    public static void GuildListWishRequestHandler(Session session, Packet.Request packet) => Handle<GuildListWishRequest, GuildListWishResponse>(session, packet, (m, r, response) =>
    {
        GuildPlayerState state = m.Player(session.player.PlayerData.Id);
        response.WishCount = state.WishCount; response.WishContributeCount = state.WishContributeCount;
        foreach (GuildWishState wish in m.Guild.Wishes.Where(x => m.Guild.MemberIds.Contains(x.PlayerId)))
        {
            Player? owner = Player.TryFromPlayerId(wish.PlayerId);
            if (owner is null) continue;
            response.WishesData.Add(new() { Id = wish.PlayerId, Name = owner.PlayerData.Name, RankLevel = Rank(m.Guild, wish.PlayerId), ItemId = wish.ItemId, Seq = wish.Seq, GotCount = wish.GotCount, MaxCount = wish.MaxCount });
        }
    }, membershipCode: 20063072, fallbackCode: 20063073);

    [RequestPacketHandler("GuildWishContributeRequest")]
    public static void GuildWishContributeRequestHandler(Session session, Packet.Request packet) => Handle<GuildWishContributeRequest, GuildWishContributeResponse>(session, packet, (m, r, response) =>
    {
        long uid = session.player.PlayerData.Id;
        Require(r.PlayerId != uid, 20063077);
        Require(m.Guild.MemberIds.Contains(r.PlayerId) && Rank(m.Guild, r.PlayerId) != 5, 20063078);
        GuildPlayerState state = m.Player(uid);
        Require(state.WishContributeCount < LocalGuildWishPolicy.DailyDonationLimit, 20063079);
        GuildWishState? wish = m.Guild.Wishes.FirstOrDefault(x => x.PlayerId == r.PlayerId && x.Seq == r.Seq && x.ItemId == r.ItemId);
        Require(wish is not null && wish.GotCount < wish.MaxCount, 20063080);
        Require(!wish!.Donors.Contains(uid), 20063081);
        Require(m.Balance(uid, r.ItemId) >= 1, 20063076);
        m.AddCost(uid, r.ItemId, 1); m.AddGoods(r.PlayerId, r.ItemId, 1); m.AddGoods(uid, 39, Setting("GuildWishContributeAddCoin"));
        wish.GotCount++; wish.Donors.Add(uid); state.WishContributeCount++;
    }, membershipCode: 20063075, fallbackCode: 20063082);

    [RequestPacketHandler("GuildGiveLikeRequest")]
    public static void GuildGiveLikeRequestHandler(Session session, Packet.Request packet) => Handle<GuildGiveLikeRequest, GuildGiveLikeResponse>(session, packet, (m, r, response) =>
    {
        long uid = session.player.PlayerData.Id;
        Require(r.OtherId != uid, 20063112);
        Require(m.Guild.MemberIds.Contains(r.OtherId), 20063115);
        if (r.ItemId is not { Count: > 0 } || r.ItemCount is null || r.ItemId.Count != r.ItemCount.Count || r.ItemId.Distinct().Count() != r.ItemId.Count)
            throw new ServerCodeException("Invalid guild gift list", 20063237);
        int popularity = 0, coin = 0;
        for (int i = 0; i < r.ItemId.Count; i++)
        {
            GuildPresentTable? gift = TableReaderV2.Parse<GuildPresentTable>().FirstOrDefault(x => x.PresentId == r.ItemId[i]);
            if (r.ItemCount[i] <= 0 || gift is null) throw new ServerCodeException("Invalid guild gift item", 20063237);
            Require(m.Balance(uid, r.ItemId[i]) >= r.ItemCount[i], 20063116);
            popularity = checked(popularity + r.ItemCount[i] * gift.Popularity);
            coin = checked(coin + r.ItemCount[i] * gift.GuildCoin);
        }
        for (int i = 0; i < r.ItemId.Count; i++) m.AddCost(uid, r.ItemId[i], r.ItemCount[i]);
        m.Guild.Members[r.OtherId].Popularity = checked(m.Guild.Members[r.OtherId].Popularity + popularity);
        // Explicit local-server policy: the sender receives the authored gift rebate.
        if (coin > 0) m.AddGoods(uid, 39, coin);
    }, membershipCode: 20063111, fallbackCode: 20063117);

    [RequestPacketHandler("GuildLevelUpRequest")]
    public static void GuildLevelUpRequestHandler(Session session, Packet.Request packet) => Handle<GuildEmptyRequest, GuildLevelUpResponse>(session, packet, (m, r, response) =>
    {
        Require(Rank(m.Guild, session.player.PlayerData.Id) <= 2, 20063176);
        GuildLevelTable current = Level(m.Guild);
        GuildLevelTable? next = TableReaderV2.Parse<GuildLevelTable>().FirstOrDefault(x => x.Level == m.Guild.Level + 1);
        Require(next is not null && current.Build > 0, 20063178);
        Require(m.Guild.Build >= current.Build, 20063177);
        m.Guild.Build -= current.Build; m.Guild.Level = next!.Level;
        m.Guild.TalentPoint = checked(m.Guild.TalentPoint + next.TalentPoint);
        m.Guild.MaxMembers = next.Capacity;
        ConvertMaximumLevelBuild(m.Guild);
        foreach (GuildHeadPortraitTable icon in TableReaderV2.Parse<GuildHeadPortraitTable>())
            if (OwnsGuildPortrait(m.Guild, icon.Id) && !m.Guild.HeadPortraits.Contains(icon.Id)) m.Guild.HeadPortraits.Add(icon.Id);
        m.Broadcast(GoodsProjection(m.Guild));
        EconomyEvent(m, 4, m.Guild.Level); EconomyEvent(m, 2, m.Guild.Build); EconomyEvent(m, 11, m.Guild.TalentPoint);
        AddNews(m, 1002, m.Guild.Level);
    }, membershipCode: 20063172, fallbackCode: 20063173);

    [RequestPacketHandler("GuildPayMaintainRequest")]
    public static void GuildPayMaintainRequestHandler(Session session, Packet.Request packet) => Handle<GuildEmptyRequest, GuildPayMaintainResponse>(session, packet, (m, r, response) =>
    {
        Require(Rank(m.Guild, session.player.PlayerData.Id) <= 2, 20063183);
        Require(m.Guild.MaintainState == 1, 20063184);
        int due = OptionalLevelInt(Level(m.Guild), "EmergencyMaintenance");
        Require(due > 0, 20063184); Require(m.Guild.ContributeLeft >= due, 20063185);
        m.Guild.ContributeLeft -= due; m.Guild.MaintainState = 0; m.Guild.EmergenceTime = 0;
        EconomyEvent(m, 1, m.Guild.ContributeLeft); m.Broadcast(new NotifyGuildMaintain());
    }, membershipCode: 20063179, fallbackCode: 20063180);

    [RequestPacketHandler("GuildListTalentRequest")]
    public static void GuildListTalentRequestHandler(Session session, Packet.Request packet) => Handle<GuildEmptyRequest, GuildListTalentResponse>(session, packet, (m, r, response) =>
    { response.Point = m.Guild.TalentPoint; response.Talents = new(m.Guild.Talents); }, membershipCode: 20063249, fallbackCode: 20063248);

    [RequestPacketHandler("GuildUpgradeTalentRequest")]
    public static void GuildUpgradeTalentRequestHandler(Session session, Packet.Request packet) => Handle<GuildUpgradeTalentRequest, GuildUpgradeTalentResponse>(session, packet, (m, r, response) =>
    {
        Require(Rank(m.Guild, session.player.PlayerData.Id) <= 2, 20063253);
        GuildTalentTable? talent = TableReaderV2.Parse<GuildTalentTable>().FirstOrDefault(x => x.Id == r.Id);
        Require(talent is not null, 20063254);
        Require(m.Guild.Level >= talent!.GuildLevel, 20063255);
        int level = m.Guild.Talents.GetValueOrDefault(r.Id);
        Require(level < talent.CostPoint.Count, 20063258);
        foreach (int parent in talent.Parent.Where(x => x > 0))
        { Require(m.Guild.Talents.ContainsKey(parent), 20063256); Require(m.Guild.Talents[parent] >= level + 1, 20063257); }
        Require(m.Guild.TalentPoint >= talent.CostPoint[level], 20063259);
        m.Guild.TalentPoint -= talent.CostPoint[level]; m.Guild.Talents[r.Id] = level + 1; response.Point = m.Guild.TalentPoint;
        m.Broadcast(new NotifyGuildEvent { Type = 10, Value = checked((uint)r.Id), Value2 = level + 1 }); EconomyEvent(m, 11, m.Guild.TalentPoint);
    }, membershipCode: 20063251, fallbackCode: 20063248);

    [RequestPacketHandler("GuildGetGiftRequest")]
    public static void GuildGetGiftRequestHandler(Session session, Packet.Request packet) => Handle<GuildGetGiftRequest, GuildGetGiftResponse>(session, packet, (m, r, response) =>
    {
        long uid = session.player.PlayerData.Id; GuildPlayerState state = m.Player(uid);
        Require(state.GiftGuildGot == 0 || state.GiftGuildGot == m.Guild.Id, 20063197);
        var candidates = TableReaderV2.Parse<GuildGiftTable>().Where(x => x.GuildLevel == m.Guild.GiftGuildLevel && (r.GiftLevel == 0 || x.GiftLevel == r.GiftLevel)).OrderBy(x => x.GiftLevel).ToList();
        Require(candidates.Count > 0, 20063200);
        var eligible = candidates.Where(x => x.GiftContribute <= m.Guild.GiftContribute && !state.GiftLevelsGot.Contains(x.GiftLevel)).ToList();
        Require(eligible.Count > 0, candidates.All(x => state.GiftLevelsGot.Contains(x.GiftLevel)) ? 20063202 : 20063201);
        // Explicit local-server calendar for the unshipped Time810: use the authored guild reward revision date.
        bool newRewardWindow = Now >= Setting("GuildBossNewRewardDate");
        foreach (GuildGiftTable gift in eligible)
        { m.AddReward(uid, gift.NewGiftReward > 0 && (ActivityScheduleService.TryGet(gift.TimeId, out var schedule) ? schedule.IsOpen(DateTimeOffset.UtcNow) : newRewardWindow) ? gift.NewGiftReward : gift.GiftReward); state.GiftLevelsGot.Add(gift.GiftLevel); response.GiftLevels.Add(gift.GiftLevel); }
        state.GiftGuildGot = checked((int)m.Guild.Id);
    }, membershipCode: 20063195, fallbackCode: 20063196);

    [RequestPacketHandler("GuildGetContributeRewardRequest")]
    public static void GuildGetContributeRewardRequestHandler(Session session, Packet.Request packet) => Handle<GuildEmptyRequest, GuildGetContributeRewardResponse>(session, packet, (m, r, response) =>
    {
        GuildPlayerState state = m.Player(session.player.PlayerData.Id);
        Require(state.HasContributeReward, 20063204); Require(state.PendingContributeReward > 0, 20063207);
        response.AddGuildCoin = state.PendingContributeReward; m.AddGoods(session.player.PlayerData.Id, 39, response.AddGuildCoin);
        state.HasContributeReward = false; state.PendingContributeReward = 0;
        m.Push(session.player.PlayerData.Id, new NotifyGuildEvent { Type = 7, Value = 0 });
    }, membershipCode: 20063203, fallbackCode: 20063205);

    [RequestPacketHandler("GuildBuyIconRequest")]
    public static void GuildBuyIconRequestHandler(Session session, Packet.Request packet) => Handle<GuildBuyIconRequest, GuildBuyIconResponse>(session, packet, (m, r, response) =>
    {
        Require(Rank(m.Guild, session.player.PlayerData.Id) <= 2, 20063224);
        GuildHeadPortraitTable? icon = TableReaderV2.Parse<GuildHeadPortraitTable>().FirstOrDefault(x => x.Id == r.IconId);
        if (icon is null) throw new ServerCodeException("Unknown guild portrait", 20063355);
        if (icon.Cost is not int cost || cost <= 0) throw new ServerCodeException("Guild portrait does not require purchase", 20063359);
        Require(!m.Guild.HeadPortraits.Contains(r.IconId), 20063360);
        Require(m.Guild.ShopCoin >= cost, 20012004);
        m.Guild.ShopCoin -= cost; m.Guild.HeadPortraits.Add(r.IconId);
        EconomyEvent(m, 15, m.Guild.ShopCoin); m.Broadcast(GoodsProjection(m.Guild));
    }, membershipCode: 20063228, fallbackCode: 20063229);
}
