using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.config;
using AscNet.Table.V2.share.guild;
using AscNet.Table.V2.share.guildsign;
using AscNet.Table.V2.share.reward;
using MongoDB.Driver;
using Newtonsoft.Json.Linq;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidateGuildEconomyCompatibility()
    {
        using GuildTestScope scope = new();
        var leader = scope.CreatePlayer();
        var member = scope.CreatePlayer();
        var outsider = scope.CreatePlayer();
        var donors = Enumerable.Range(0, 5).Select(_ => scope.CreatePlayer()).ToArray();
        Guild guild = scope.SeedGuild(new[] { leader, member }.Concat(donors).ToArray());
        long leaderId = leader.Session.player.PlayerData.Id;
        long memberId = member.Session.player.PlayerData.Id;
        JObject Call(LoopbackSessionHarness player, string name, params (string Key, object Value)[] fields) =>
            GuildRpc(player, name, fields.ToDictionary(field => field.Key, field => field.Value));
        JObject Ok(LoopbackSessionHarness player, string name, params (string Key, object Value)[] fields)
        {
            JObject response = Call(player, name, fields);
            GuildAssert(response["Code"]?.Type == JTokenType.Integer && response.Value<int>("Code") == 0, name + " must succeed: " + response);
            return response;
        }
        void Reject(LoopbackSessionHarness player, string name, params (string Key, object Value)[] fields)
        {
            var before = Balances(player);
            Guild guildBefore = Guild.FindById(guild.Id)!;
            GuildAssert(Call(player, name, fields).Value<int>("Code") != 0, name + " must reject invalid state");
            GuildAssert(before.OrderBy(pair => pair.Key).SequenceEqual(Balances(player).OrderBy(pair => pair.Key)), name + " failure must not change inventory");
            Guild guildAfter = Guild.FindById(guild.Id)!;
            GuildAssert((guildBefore.ContributeLeft, guildBefore.Build, guildBefore.ShopCoin, guildBefore.TalentPoint)
                == (guildAfter.ContributeLeft, guildAfter.Build, guildAfter.ShopCoin, guildAfter.TalentPoint), name + " failure must not spend or grant guild funds");
        }
        Dictionary<int, long> Balances(LoopbackSessionHarness player) => Inventory.collection
            .Find(row => row.Uid == player.Session.player.PlayerData.Id).Single().Items
            .GroupBy(item => item.Id).ToDictionary(group => group.Key, group => group.Sum(item => item.Count));
        void Reload(LoopbackSessionHarness player)
        {
            long uid = player.Session.player.PlayerData.Id;
            player.Session.player = Player.collection.Find(row => row.PlayerData.Id == uid).Single();
            player.Session.inventory = Inventory.collection.Find(row => row.Uid == uid).Single();
        }

        void ChangeGuild(Action<Guild> change)
        {
            guild = Guild.FindById(guild.Id)!;
            change(guild);
            Guild.collection.ReplaceOne(row => row.Id == guild.Id, guild);
        }
        void SetBalance(LoopbackSessionHarness player, int itemId, long count)
        {
            Reload(player);
            player.Session.inventory.Items.RemoveAll(item => item.Id == itemId);
            player.Session.inventory.Items.Add(new Item { Id = itemId, Count = count });
            player.Session.inventory.Save();
        }
        void AgeEconomyWeek(int count)
        {
            ChangeGuild(value => value.EconomyWeek -= count);
            foreach (long uid in guild.MemberIds)
            {
                var player = Player.collection.Find(row => row.PlayerData.Id == uid).Single();
                player.GuildState.EconomyWeek -= count;
                player.Save();
            }
            Reload(leader);
            Reload(member);
        }
        Reject(outsider, "GuildSignRequest");
        Reject(leader, "GuildSignRewardRequest");
        JObject sign = Ok(leader, "GuildSignRequest");
        var info = sign["GuildSignInfo"]!;
        var outcome = TableReaderV2.Parse<GuildSignTable>().Single(row => row.Id == info.Value<int>("Id"));
        GuildAssert(outcome.RewardId.Any(rewardId =>
        {
            var reward = TableReaderV2.Parse<RewardTable>().Single(row => row.Id == rewardId);
            var expected = TableReaderV2.Parse<RewardGoodsTable>().Where(row => reward.SubIds.Contains(row.Id)).Select(row => (row.TemplateId, Count: (long)row.Count)).OrderBy(row => row.TemplateId);
            var actual = info["RewardGoodsList"]!.Select(row => (TemplateId: row.Value<int>("TemplateId"), Count: row.Value<long>("Count"))).OrderBy(row => row.TemplateId);
            return expected.SequenceEqual(actual);
        }), "Sign preview must match an authored reward outcome, not a captured payload");
        int[] events = info["SignEventIds"]!.Values<int>().ToArray();
        var eventRows = TableReaderV2.Parse<GuildSignEventTable>();
        GuildAssert(events.Distinct().Count() == events.Length, "Sign events must not repeat");
        for (int i = 0; i < outcome.SignType.Count; i++)
            GuildAssert(events.Count(id => eventRows.Single(row => row.Id == id).SignType == outcome.SignType[i]) == outcome.SignNum[i], "Sign event counts derive from outcome table");
        Reject(leader, "GuildSignRequest");
        Reload(leader);
        GuildAssert(JToken.DeepEquals(JToken.FromObject(leader.Session.player.GuildState.SignInfo), info), "Relog must preserve drawn sign, events, and frozen rewards");
        var beforeSignReward = Balances(leader);
        JObject claimedSign = Ok(leader, "GuildSignRewardRequest");
        GuildAssert(JToken.DeepEquals(info["RewardGoodsList"], claimedSign["RewardGoodsList"]), "Sign claim must grant frozen draw rewards");
        var afterSignReward = Balances(leader);
        foreach (var goods in claimedSign["RewardGoodsList"]!.Where(row => row.Value<int>("RewardType") == (int)RewardType.Item).GroupBy(row => row.Value<int>("TemplateId")))
            GuildAssert(afterSignReward.GetValueOrDefault(goods.Key) - beforeSignReward.GetValueOrDefault(goods.Key) == goods.Sum(row => row.Value<long>("Count")), "Sign claim grants exact item quantities");
        Reload(leader);
        Reject(leader, "GuildSignRewardRequest");
        var config = TableReaderV2.Parse<ConfigTable>().ToDictionary(row => row.Key, row => row.Value);
        int minimum = int.Parse(config["GuildSignGuaranteeMin"]);
        int maximum = int.Parse(config["GuildSignGuaranteeMax"]);
        int guaranteed = int.Parse(config["GuildSignGuaranteeId"]);
        DateTime gameDate = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(int.Parse(config["GlobalTimeZone"]))).Date;
        var specialSign = TableReaderV2.Parse<GuildSignSpecialDateSignTable>().FirstOrDefault(row => DateTime.Parse(row.Date, System.Globalization.CultureInfo.InvariantCulture).Date == gameDate);
        foreach (int threshold in new[] { minimum, maximum }.Distinct())
        {
            Reload(leader);
            leader.Session.player.GuildState.SignPeriod = long.MinValue;
            leader.Session.player.PlayerData.Birthday = null;
            leader.Session.player.GuildState.SignGuaranteeThreshold = threshold;
            leader.Session.player.GuildState.SignGuaranteeCount = threshold - 1;
            leader.Session.player.Save();
            var forced = Ok(leader, "GuildSignRequest");
            int expectedSign = specialSign?.SignId ?? guaranteed;
            GuildAssert(forced["GuildSignInfo"]!.Value<int>("Id") == expectedSign, "Sign guarantee respects both threshold boundaries and authored special-date precedence");
            Reload(leader);
            GuildAssert(leader.Session.player.GuildState.SignGuaranteeCount == (expectedSign == guaranteed ? 0 : threshold - 1), "Guaranteed outcome resets count; special dates preserve ordinary progress");
            GuildAssert(leader.Session.player.GuildState.SignGuaranteeThreshold == (expectedSign == guaranteed ? 0 : threshold), "Guaranteed outcome clears threshold; special dates preserve it");
            Ok(leader, "GuildSignRewardRequest");
            Reject(leader, "GuildSignRewardRequest");
        }
        Reload(member);
        member.Session.player.PlayerData.Birthday = new Birthday { Mon = gameDate.Month, Day = gameDate.Day };
        member.Session.player.Save();
        var birthdaySign = Ok(member, "GuildSignRequest");
        GuildAssert(birthdaySign["GuildSignInfo"]!.Value<int>("Id") == (specialSign?.SignId ?? TableReaderV2.Parse<GuildSignTable>().Single(row => row.Type == 2).Id), "Birthday uses authored birthday outcome unless literal holiday takes precedence");

        Reject(outsider, "GuildListRankRequest", ("Order", 1));
        Reject(outsider, "GuildListChatRequest");
        Reject(leader, "GuildListRankRequest", ("Order", 0));
        var rivalLeader = scope.CreatePlayer();
        Guild rival = scope.SeedGuild(rivalLeader);
        ChangeGuild(value =>
        {
            value.ContributionDays = new() { [value.EconomyDay] = 100, [value.EconomyDay - 7] = 1000 };
            value.ContributionRankEnteredAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 2;
        });
        rival.ContributionDays[guild.EconomyDay] = 100;
        rival.ContributionRankEnteredAt = guild.ContributionRankEnteredAt + 1;
        rival.Level = TableReaderV2.Parse<GuildLevelTable>().Max(row => row.Level);
        Guild.collection.ReplaceOne(row => row.Id == rival.Id, rival);
        foreach (int order in new[] { 1, 2, 3 })
        {
            var ranking = Ok(leader, "GuildListRankRequest", ("Order", order));
            if (order == 1)
            {
                var contributions = ranking["RankList"]!.ToArray();
                GuildAssert(contributions.Single(row => row.Value<uint>("GuildId") == guild.Id).Value<int>("Score") == 100, "Contribution leaderboard excludes days outside rolling seven-day window");
                GuildAssert(Array.FindIndex(contributions, row => row.Value<uint>("GuildId") == guild.Id) < Array.FindIndex(contributions, row => row.Value<uint>("GuildId") == rival.Id), "Contribution tie favors first leaderboard-entry timestamp");
            }
            var rows = ranking["RankList"]!.ToArray();
            GuildAssert(rows.Select(row => row.Value<uint>("GuildId")).Distinct().Count() == rows.Length, "Rankings contain no duplicate guilds");
            foreach (var row in rows)
            {
                Guild actual = Guild.FindById(row.Value<uint>("GuildId"))!;
                GuildAssert(actual is { Active: true } && actual.Name == row.Value<string>("GuildName"), "Rank entry identity must name a persisted active guild");
                if (order == 2) GuildAssert(actual.Level == row.Value<int>("Score"), "Level rank score derives from guild level");
            }
            GuildAssert(rows.Select(row => row.Value<int>("Score")).SequenceEqual(rows.Select(row => row.Value<int>("Score")).OrderDescending()), "Guild ranking scores descend");
            if (order == 2)
                GuildAssert(Array.FindIndex(rows, row => row.Value<uint>("GuildId") == rival.Id) < Array.FindIndex(rows, row => row.Value<uint>("GuildId") == guild.Id), "Higher-level real guild ranks before lower-level guild");
        }

        int chatPacket = Interlocked.Increment(ref guildPacketId);
        InvokeRegisteredRequestHandler(nameof(SendChatRequest), outsider.Session, chatPacket,
            new SendChatRequest { ChatData = new ChatData { ChannelType = ChatChannelType.World, MsgType = ChatMsgType.Normal, Content = "guild-identity-" + Guid.NewGuid().ToString("N") } });
        var outsiderChat = ReadPushPayload<NotifyChatMessage>(outsider, nameof(NotifyChatMessage), "No-guild chat identity");
        GuildAssert(outsiderChat.GuildName == string.Empty && outsiderChat.GuildRankLevel == 9, "No-guild world chat must never claim guild affiliation");
        var outsiderResponse = ReadResponsePayload<SendChatResponse>(outsider, chatPacket, nameof(SendChatResponse), "No-guild world chat");
        GuildAssert(outsiderResponse.Code == 0, "No-guild player can use world chat");
        var deniedChat = GuildRpc(outsider, nameof(SendChatRequest),
            new SendChatRequest { ChatData = new ChatData { ChannelType = ChatChannelType.Guild, MsgType = ChatMsgType.Normal, Content = "denied" } });
        GuildAssert(deniedChat.Value<int>("Code") == 20033016, "No-guild guild chat returns authoritative denial");
        string content = "guild-message-" + Guid.NewGuid().ToString("N");
        var guildChat = GuildRpc(member, nameof(SendChatRequest),
            new SendChatRequest { ChatData = new ChatData { ChannelType = ChatChannelType.Guild, MsgType = ChatMsgType.Normal, Content = content } });
        GuildAssert(guildChat.Value<int>("Code") == 0, "Member guild chat succeeds");
        var history = Ok(leader, "GuildListChatRequest")["ChatList"]!.Values<string>().Select(value => JObject.Parse(value!)).ToArray();
        var sent = history.Single(row => row.Value<string>("Content") == content);
        GuildAssert(sent.Value<string>("GuildName") == guild.Name && sent.Value<int>("GuildRankLevel") == 4
            && sent.Value<long>("SenderId") == memberId && sent.Value<uint>("TargetId") == guild.Id, "Guild chat uses sender's actual membership and rank");
        GuildAssert(!Ok(rivalLeader, "GuildListChatRequest")["ChatList"]!.Values<string>().Any(value => JObject.Parse(value!).Value<string>("Content") == content), "Guild chat never leaks into a different guild");
        Reject(leader, "GuildListNewsRequest", ("NewsType", 0), ("PageNo", 0));
        Reject(outsider, "GuildListNewsRequest", ("NewsType", 3), ("PageNo", 0));
        Ok(leader, "GuildListNewsRequest", ("NewsType", 3), ("PageNo", 0));

        Ok(leader, "GuildListWishRequest");
        var gifts = TableReaderV2.Parse<GuildGiftTable>().Where(row => row.GuildLevel == guild.Level).OrderBy(row => row.GiftLevel).ToArray();
        ChangeGuild(value => { value.GiftGuildLevel = value.Level; value.GiftContribute = gifts[0].GiftContribute - 1; });
        Reject(leader, "GuildGetGiftRequest", ("GiftLevel", gifts[0].GiftLevel));
        Reject(outsider, "GuildGetGiftRequest", ("GiftLevel", 0));
        ChangeGuild(value => value.GiftContribute = gifts[0].GiftContribute);
        var beforeWelfare = Balances(leader);
        var firstGift = Ok(leader, "GuildGetGiftRequest", ("GiftLevel", gifts[0].GiftLevel));
        GuildAssert(firstGift["GiftLevels"]!.Values<int>().SequenceEqual(new[] { gifts[0].GiftLevel }), "Exact welfare contribution threshold unlocks only requested gift");
        var welfareReward = TableReaderV2.Parse<RewardTable>().Single(row => row.Id == gifts[0].GiftReward);
        foreach (var goods in TableReaderV2.Parse<RewardGoodsTable>().Where(row => welfareReward.SubIds.Contains(row.Id)).GroupBy(row => row.TemplateId))
            GuildAssert(Balances(leader).GetValueOrDefault(goods.Key) == beforeWelfare.GetValueOrDefault(goods.Key) + goods.Sum(row => (long)row.Count), "Welfare credits authored personal item amounts");
        Reload(leader);
        Reject(leader, "GuildGetGiftRequest", ("GiftLevel", gifts[0].GiftLevel));
        ChangeGuild(value => value.GiftContribute = gifts.Max(row => row.GiftContribute));
        var allGifts = Ok(leader, "GuildGetGiftRequest", ("GiftLevel", 0));
        GuildAssert(allGifts["GiftLevels"]!.Values<int>().Order().SequenceEqual(gifts.Skip(1).Select(row => row.GiftLevel).Order()), "One-click welfare grants every remaining eligible tier once");
        Reject(leader, "GuildGetGiftRequest", ("GiftLevel", 0));
        var memberGifts = Ok(member, "GuildGetGiftRequest", ("GiftLevel", 0));
        GuildAssert(memberGifts["GiftLevels"]!.Values<int>().Order().SequenceEqual(gifts.Select(row => row.GiftLevel).Order()), "Welfare claims are per member, not guild-global");
        Reload(donors[4]);
        donors[4].Session.player.GuildState.GiftGuildGot = checked((int)rival.Id);
        donors[4].Session.player.Save();
        Reject(donors[4], "GuildGetGiftRequest", ("GiftLevel", 0));

        var currentLevel = TableReaderV2.Parse<GuildLevelTable>().Single(row => row.Level == guild.Level);
        ChangeGuild(value => value.Build = currentLevel.Build - 1);
        Reject(leader, "GuildLevelUpRequest");
        ChangeGuild(value => value.Build = currentLevel.Build);
        Reject(member, "GuildLevelUpRequest");
        int initialTalentPoints = guild.TalentPoint;
        Ok(leader, "GuildLevelUpRequest");
        guild = Guild.FindById(guild.Id)!;
        GuildAssert(guild.Level == currentLevel.Level + 1 && guild.Build == 0, "Level-up spends exact current-level build requirement");
        GuildAssert(guild.TalentPoint == initialTalentPoints + TableReaderV2.Parse<GuildLevelTable>().Single(row => row.Level == guild.Level).TalentPoint, "Level-up grants authored talent points");
        var talent = TableReaderV2.Parse<GuildTalentTable>().First(row => row.GuildLevel <= guild.Level && row.Parent.All(id => id == 0));
        Reject(member, "GuildUpgradeTalentRequest", ("Id", talent.Id));
        Reject(leader, "GuildUpgradeTalentRequest", ("Id", int.MaxValue));
        ChangeGuild(value => value.TalentPoint = talent.CostPoint[0] - 1);
        Reject(leader, "GuildUpgradeTalentRequest", ("Id", talent.Id));
        ChangeGuild(value => value.TalentPoint = talent.CostPoint[0]);
        var upgraded = Ok(leader, "GuildUpgradeTalentRequest", ("Id", talent.Id));
        GuildAssert(upgraded.Value<int>("Point") == 0, "Talent upgrade consumes exact authored cost");
        var talentList = Ok(leader, "GuildListTalentRequest");
        GuildAssert(talentList["Talents"]![talent.Id.ToString()]!.Value<int>() == 1, "Talent list reloads committed level");
        var childTalent = TableReaderV2.Parse<GuildTalentTable>().First(row => row.GuildLevel <= guild.Level && row.Parent.Count(id => id > 0) > 1);
        ChangeGuild(value => value.TalentPoint = childTalent.CostPoint[0]);
        Reject(leader, "GuildUpgradeTalentRequest", ("Id", childTalent.Id));
        ChangeGuild(value => { value.Talents[talent.Id] = talent.CostPoint.Count; value.TalentPoint = int.MaxValue; });
        Reject(leader, "GuildUpgradeTalentRequest", ("Id", talent.Id));
        Reject(member, "GuildPayMaintainRequest");
        Reject(leader, "GuildPayMaintainRequest");

        int wishItem = TableReaderV2.Parse<AscNet.Table.V2.share.trust.CharacterTrustItemTable>().First(row => row.FavorCharacterId.Count > 0).Id;
        Reject(outsider, "GuildReleaseWishRequest", ("ItemId", wishItem));
        Reject(leader, "GuildReleaseWishRequest", ("ItemId", int.MaxValue));
        Ok(member, "GuildReleaseWishRequest", ("ItemId", wishItem));
        Reject(member, "GuildReleaseWishRequest", ("ItemId", wishItem));
        var wishList = Ok(leader, "GuildListWishRequest");
        var wish = wishList["WishesData"]!.Single(row => row.Value<long>("Id") == memberId);
        int sequence = wish.Value<int>("Seq");
        GuildAssert(wish.Value<int>("ItemId") == wishItem && wish.Value<int>("GotCount") == 0 && wish.Value<int>("MaxCount") == 5, "Published wish reflects selected trust item and approved local target");
        Reject(member, "GuildWishContributeRequest", ("PlayerId", memberId), ("Seq", sequence), ("ItemId", wishItem));
        Reject(outsider, "GuildWishContributeRequest", ("PlayerId", memberId), ("Seq", sequence), ("ItemId", wishItem));
        Reject(leader, "GuildWishContributeRequest", ("PlayerId", memberId), ("Seq", sequence + 1), ("ItemId", wishItem));
        Reject(leader, "GuildWishContributeRequest", ("PlayerId", memberId), ("Seq", sequence), ("ItemId", int.MaxValue));
        SetBalance(leader, wishItem, 0);
        Reject(leader, "GuildWishContributeRequest", ("PlayerId", memberId), ("Seq", sequence), ("ItemId", wishItem));
        SetBalance(leader, wishItem, 1);
        var recipientBefore = Balances(member);
        var donorBefore = Balances(leader);
        Ok(leader, "GuildWishContributeRequest", ("PlayerId", memberId), ("Seq", sequence), ("ItemId", wishItem));
        GuildAssert(Balances(leader).GetValueOrDefault(wishItem) == 0, "Donation spends one actual donor item");
        GuildAssert(Balances(member).GetValueOrDefault(wishItem) == recipientBefore.GetValueOrDefault(wishItem) + 1, "Donation credits different persisted recipient");
        GuildAssert(Balances(leader).GetValueOrDefault(39) == donorBefore.GetValueOrDefault(39) + int.Parse(config["GuildWishContributeAddCoin"]), "Donation grants configured donor guild coin");
        SetBalance(leader, wishItem, 1);
        Reject(leader, "GuildWishContributeRequest", ("PlayerId", memberId), ("Seq", sequence), ("ItemId", wishItem));
        GuildAssert(Balances(member).GetValueOrDefault(wishItem) == recipientBefore.GetValueOrDefault(wishItem) + 1, "Duplicate donor cannot credit recipient twice");
        Reload(member);
        var persistedWish = Ok(member, "GuildListWishRequest");
        GuildAssert(persistedWish.Value<int>("WishCount") == 1 && persistedWish["WishesData"]!.Single(row => row.Value<long>("Id") == memberId).Value<int>("GotCount") == 1, "Wish progress persists across reload");
        SetBalance(donors[0], wishItem, 1);
        donors[0].Session.player.GuildState.WishContributeCount = 5;
        donors[0].Session.player.Save();
        Reject(donors[0], "GuildWishContributeRequest", ("PlayerId", memberId), ("Seq", sequence), ("ItemId", wishItem));
        Reload(donors[0]);
        donors[0].Session.player.GuildState.WishContributeCount = 0;
        donors[0].Session.player.Save();
        foreach (var donor in donors.Take(4))
        {
            SetBalance(donor, wishItem, 1);
            Ok(donor, "GuildWishContributeRequest", ("PlayerId", memberId), ("Seq", sequence), ("ItemId", wishItem));
        }
        SetBalance(donors[4], wishItem, 1);
        Reject(donors[4], "GuildWishContributeRequest", ("PlayerId", memberId), ("Seq", sequence), ("ItemId", wishItem));
        GuildAssert(Balances(member).GetValueOrDefault(wishItem) == recipientBefore.GetValueOrDefault(wishItem) + wish.Value<int>("MaxCount"), "Distinct donors can exactly fill wish but cannot overfill it");
        ChangeGuild(value => value.EconomyDay--);
        Reload(leader);
        leader.Session.player.GuildState.EconomyDay--;
        leader.Session.player.Save();
        Reload(member);
        member.Session.player.GuildState.EconomyDay--;
        member.Session.player.Save();
        var nextDay = Ok(member, "GuildListWishRequest");
        GuildAssert(nextDay.Value<int>("WishCount") == 0 && !nextDay["WishesData"]!.Any(), "Daily rollover clears old wishes and publication allowance");
        GuildAssert(Ok(leader, "GuildListWishRequest").Value<int>("WishContributeCount") == 0, "Daily rollover clears donor allowance");
        Ok(member, "GuildReleaseWishRequest", ("ItemId", wishItem));
        Reject(leader, "GuildWishContributeRequest", ("PlayerId", memberId), ("Seq", sequence), ("ItemId", wishItem));

        var present = TableReaderV2.Parse<GuildPresentTable>().First();
        SetBalance(leader, present.PresentId, 2);
        var recipientCoins = Balances(member).GetValueOrDefault(39);
        var giverCoins = Balances(leader).GetValueOrDefault(39);
        int popularityBefore = Guild.FindById(guild.Id)!.Members[memberId].Popularity;
        Reject(leader, "GuildGiveLikeRequest", ("OtherId", leaderId), ("ItemId", new[] { present.PresentId }), ("ItemCount", new[] { 1 }));
        Reject(leader, "GuildGiveLikeRequest", ("OtherId", outsider.Session.player.PlayerData.Id), ("ItemId", new[] { present.PresentId }), ("ItemCount", new[] { 1 }));
        Reject(leader, "GuildGiveLikeRequest", ("OtherId", memberId), ("ItemId", new[] { present.PresentId }), ("ItemCount", new[] { 0 }));
        Reject(leader, "GuildGiveLikeRequest", ("OtherId", memberId), ("ItemId", new[] { present.PresentId }), ("ItemCount", new[] { 3 }));
        Reject(leader, "GuildGiveLikeRequest", ("OtherId", memberId), ("ItemId", new[] { present.PresentId }), ("ItemCount", Array.Empty<int>()));
        Reject(leader, "GuildGiveLikeRequest", ("OtherId", memberId), ("ItemId", new[] { present.PresentId, present.PresentId }), ("ItemCount", new[] { 1, 1 }));
        Ok(leader, "GuildGiveLikeRequest", ("OtherId", memberId), ("ItemId", new[] { present.PresentId }), ("ItemCount", new[] { 2 }));
        GuildAssert(Balances(leader).GetValueOrDefault(present.PresentId) == 0, "Cross-player present debits full selected quantity");
        GuildAssert(Guild.FindById(guild.Id)!.Members[memberId].Popularity == popularityBefore + 2 * present.Popularity, "Cross-player present increases recipient popularity from table");
        GuildAssert(Balances(leader).GetValueOrDefault(39) == giverCoins + 2 * present.GuildCoin, "Cross-player present rebates the donor's table-derived guild coin");
        GuildAssert(Balances(member).GetValueOrDefault(39) == recipientCoins, "Gift recipient receives popularity, not the donor's rebate");
        Reject(leader, "GuildGiveLikeRequest", ("OtherId", memberId), ("ItemId", new[] { present.PresentId }), ("ItemCount", new[] { 2 }));

        Reject(leader, "GuildGetContributeRewardRequest");
        int contributionThreshold = int.Parse(config["GuildContributeRewardMin"]);
        ChangeGuild(value =>
        {
            value.EconomyWeek--;
            value.Members[leaderId].WeekContribute = contributionThreshold;
            value.Members[memberId].WeekContribute = contributionThreshold - 1;
        });
        foreach (long uid in guild.MemberIds)
        {
            var player = Player.collection.Find(row => row.PlayerData.Id == uid).Single();
            player.GuildState.EconomyWeek--;
            player.Save();
        }
        Reload(leader);
        Reload(member);
        long coinBeforeWeekly = Balances(leader).GetValueOrDefault(39);
        var weekly = Ok(leader, "GuildGetContributeRewardRequest");
        int expectedWeekly = (int)Math.Floor(contributionThreshold * double.Parse(config["GuildContributeRewardCoA"], System.Globalization.CultureInfo.InvariantCulture)) + int.Parse(config["GuildContributeRewardCoB"]);
        GuildAssert(weekly.Value<int>("AddGuildCoin") == expectedWeekly && Balances(leader).GetValueOrDefault(39) == coinBeforeWeekly + expectedWeekly, "Weekly boundary freezes and pays configured contribution formula");
        Reload(leader);
        Reject(leader, "GuildGetContributeRewardRequest");
        Reject(member, "GuildGetContributeRewardRequest");
        GuildAssert(Guild.FindById(guild.Id)!.Members.Values.All(value => value.WeekContribute == 0), "Weekly rollover resets member contribution counters");
        ChangeGuild(value => value.GiftContribute = gifts[0].GiftContribute);
        Ok(leader, "GuildGetGiftRequest", ("GiftLevel", gifts[0].GiftLevel));
        AgeEconomyWeek(2);
        ChangeGuild(value => value.Members[leaderId].WeekContribute = contributionThreshold);
        Reject(leader, "GuildGetContributeRewardRequest");

        var paidIcon = TableReaderV2.Parse<GuildHeadPortraitTable>().First(row => row.Cost > 0);
        if (paidIcon.Cost is not int iconCost || iconCost <= 0)
            throw new InvalidDataException("Paid guild icon fixture requires an authored positive cost.");
        ChangeGuild(value => value.ShopCoin = iconCost - 1);
        Reject(leader, "GuildBuyIconRequest", ("IconId", paidIcon.Id));
        ChangeGuild(value => value.ShopCoin = iconCost);
        Reject(member, "GuildBuyIconRequest", ("IconId", paidIcon.Id));
        Ok(leader, "GuildBuyIconRequest", ("IconId", paidIcon.Id));
        guild = Guild.FindById(guild.Id)!;
        GuildAssert(guild.ShopCoin == 0 && guild.HeadPortraits.Contains(paidIcon.Id), "Guild icon purchase spends exact shop currency and persists ownership");
        Reject(leader, "GuildBuyIconRequest", ("IconId", paidIcon.Id));
        Ok(leader, "GuildChangeIconRequest", ("IconId", paidIcon.Id));
        GuildAssert(Guild.FindById(guild.Id)!.IconId == paidIcon.Id, "Purchased emblem can become the persisted guild identity");

        long EconomyAmount(int id) => id switch
        {
            38 => Guild.FindById(guild.Id)!.ContributeLeft,
            40 => Guild.FindById(guild.Id)!.Build,
            46 => Guild.FindById(guild.Id)!.TalentPoint,
            62723 => Guild.FindById(guild.Id)!.ShopCoin,
            _ => Balances(leader).GetValueOrDefault(id)
        };
        foreach (int shopId in new[] { 4001, 9998 })
        {
            var catalog = Ok(leader, "GetShopInfoRequest", ("Id", shopId));
            var offers = catalog["ClientShop"]!["GoodsList"]!.ToArray();
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var offer = offers.First(row => row["RewardGoods"]!.Value<int>("RewardType") == (shopId == 4001 ? (int)RewardType.Item : 23)
                && row["ConsumeList"]!.Any()
                && !(row["ConditionIds"]?.Any() ?? false)
                && row.Value<long>("OnSaleTime") <= now
                && (row.Value<long>("SelloutTime") == 0 || row.Value<long>("SelloutTime") > now)
                && (shopId == 4001 || TableReaderV2.Parse<GuildGoodsTable>().Any(good => good.Id == row["RewardGoods"]!.Value<int>("TemplateId"))));
            int offerId = offer.Value<int>("Id");
            var consumes = offer["ConsumeList"]!.GroupBy(row => row.Value<int>("Id")).ToDictionary(group => group.Key, group => group.Sum(row => row.Value<int>("Count")));
            foreach (var cost in consumes)
            {
                if (cost.Key is 38 or 40 or 46 or 62723)
                    ChangeGuild(value =>
                    {
                        if (cost.Key == 38) value.ContributeLeft = cost.Value;
                        if (cost.Key == 40) value.Build = cost.Value;
                        if (cost.Key == 46) value.TalentPoint = cost.Value;
                        if (cost.Key == 62723) value.ShopCoin = cost.Value;
                    });
                else SetBalance(leader, cost.Key, cost.Value);
            }
            Reject(outsider, "BuyRequest", ("ShopId", shopId), ("GoodsId", offerId), ("Count", 1));
            if (shopId == 9998) Reject(member, "BuyRequest", ("ShopId", shopId), ("GoodsId", offerId), ("Count", 1));
            Reject(leader, "BuyRequest", ("ShopId", shopId), ("GoodsId", offerId), ("Count", 0));
            Reject(leader, "BuyRequest", ("ShopId", shopId), ("GoodsId", int.MaxValue), ("Count", 1));
            var amounts = consumes.Keys.ToDictionary(id => id, EconomyAmount);
            int item = offer["RewardGoods"]!.Value<int>("TemplateId");
            long beforeBought = Balances(leader).GetValueOrDefault(item);
            var bought = Ok(leader, "BuyRequest", ("ShopId", shopId), ("GoodsId", offerId), ("Count", 1));
            GuildAssert(bought["GoodList"]!.Single().Value<int>("TemplateId") == item, "Guild shop returns selected catalog reward");
            foreach (var cost in consumes)
                GuildAssert(EconomyAmount(cost.Key) == amounts[cost.Key] - cost.Value, "Guild shop debits correct personal or shared currency");
            if (shopId == 9998)
            {
                var owned = TableReaderV2.Parse<GuildGoodsTable>().Single(row => row.Id == item);
                guild = Guild.FindById(guild.Id)!;
                GuildAssert((owned.Type == 1 ? guild.DormThemes : guild.DormBgms).Contains(owned.TargetId), "Purchase shop resolves guild goods into shared ownership");
                GuildAssert(Balances(leader).GetValueOrDefault(item) == beforeBought, "Shared guild goods never become personal inventory items");
            }
            else GuildAssert(Balances(leader).GetValueOrDefault(item) == beforeBought + offer["RewardGoods"]!.Value<long>("Count"), "Personal guild shop grants selected item quantity");
            Reload(leader);
            Reject(leader, "BuyRequest", ("ShopId", shopId), ("GoodsId", offerId), ("Count", 1));
            if (shopId == 9998)
            {
                ChangeGuild(value => value.ShopCoin = consumes[62723]);
                Reject(leader, "BuyRequest", ("ShopId", shopId), ("GoodsId", offerId), ("Count", 1));
                GuildAssert(Guild.FindById(guild.Id)!.ShopCoin == consumes[62723], "Already-owned guild goods reject even with sufficient shared currency");
            }
        }

        var taskConditions = TableReaderV2.Parse<AscNet.Table.V2.share.task.ConditionTable>();
        var guildTasks = TableReaderV2.Parse<AscNet.Table.V2.share.task.TaskTable>()
            .Where(row => row.Type == 6 && taskConditions.Any(condition => condition.Id == row.Condition && condition.Type is 35000 or 35001 or 35002 or 35003)).ToArray();
        var joinTask = guildTasks.Single(row => taskConditions.Single(condition => condition.Id == row.Condition).Type == 35000);
        Reject(outsider, "FinishTaskRequest", ("TaskId", joinTask.Id));
        var joinReward = TableReaderV2.Parse<RewardTable>().Single(row => row.Id == joinTask.RewardId);
        var joinGoods = TableReaderV2.Parse<RewardGoodsTable>().Where(row => joinReward.SubIds.Contains(row.Id)).ToArray();
        var beforeJoinClaim = Balances(leader);
        Ok(leader, "FinishTaskRequest", ("TaskId", joinTask.Id));
        foreach (var goods in joinGoods.GroupBy(row => row.TemplateId))
            GuildAssert(Balances(leader).GetValueOrDefault(goods.Key) == beforeJoinClaim.GetValueOrDefault(goods.Key) + goods.Sum(row => (long)row.Count), "Guild task claim grants table-derived rewards through generic task RPC");
        Reload(leader);
        Reject(leader, "FinishTaskRequest", ("TaskId", joinTask.Id));
        var levelTask = guildTasks.Where(row => taskConditions.Single(condition => condition.Id == row.Condition).Type == 35002)
            .OrderBy(row => row.Result).First();
        int currentGuildLevel = Guild.FindById(guild.Id)!.Level;
        ChangeGuild(value => value.Level = (levelTask.Result ?? 1) - 1);
        Reject(leader, "FinishTaskRequest", ("TaskId", levelTask.Id));
        ChangeGuild(value => value.Level = levelTask.Result ?? 1);
        Ok(leader, "FinishTaskRequest", ("TaskId", levelTask.Id));
        Reload(leader);
        Reject(leader, "FinishTaskRequest", ("TaskId", levelTask.Id));
        ChangeGuild(value => value.Level = currentGuildLevel);
        var tenureTask = guildTasks.Where(row => taskConditions.Single(condition => condition.Id == row.Condition).Type == 35001)
            .OrderBy(row => row.Result).First();
        long today = (long)Math.Floor((DateTimeOffset.UtcNow.ToUnixTimeSeconds() - int.Parse(config["DailyResetTimestamp"])) / 86400d);
        int requiredDays = tenureTask.Result ?? 1;
        ChangeGuild(value => value.Members[leaderId].JoinedAt = (today - requiredDays + 2) * 86400 + int.Parse(config["DailyResetTimestamp"]));
        Reject(leader, "FinishTaskRequest", ("TaskId", tenureTask.Id));
        ChangeGuild(value => value.Members[leaderId].JoinedAt -= 86400);
        Ok(leader, "FinishTaskRequest", ("TaskId", tenureTask.Id));
        Reload(leader);
        Reject(leader, "FinishTaskRequest", ("TaskId", tenureTask.Id));
        var expiredSiege = TableReaderV2.Parse<AscNet.Table.V2.share.task.TaskTable>().Single(row => row.Id == 35011);
        Reload(leader);
        leader.Session.player.MissionProgress.ConditionCounters[expiredSiege.Condition] = expiredSiege.Result ?? 1;
        leader.Session.player.Save();
        Reject(leader, "FinishTaskRequest", ("TaskId", expiredSiege.Id));
    }
}
