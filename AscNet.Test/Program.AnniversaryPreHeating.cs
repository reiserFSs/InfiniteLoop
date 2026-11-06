using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Game;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.miniactivity.anniversarypreheating;
using AscNet.Table.V2.share.reward;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace AscNet.Test;

internal partial class Program
{
    /// <summary>4.9 Time Trace: closed in the real schedule; Read/Reward requests, reward-once claim, login push and BSON reload under an injected test window.</summary>
    private static void ValidateAnniversaryPreHeatingCompatibility()
    {
        const long playerId = 49_610;
        AssertMailNamedMapKeys(new AnniversaryPreHeatingReadRequest(), ["GroupId", "ContentId"], "AnniversaryPreHeatingReadRequest");
        AssertMailNamedMapKeys(new AnniversaryPreHeatingReadResponse(), ["Code"], "AnniversaryPreHeatingReadResponse");
        AssertMailNamedMapKeys(new AnniversaryPreHeatingRewardRequest(), ["GroupId"], "AnniversaryPreHeatingRewardRequest");
        AssertMailNamedMapKeys(new AnniversaryPreHeatingRewardResponse(), ["Code", "RewardGoodsList"], "AnniversaryPreHeatingRewardResponse");
        AssertMailNamedMapKeys(new NotifyAnniversaryPreHeatingData(), ["Data"], "NotifyAnniversaryPreHeatingData");
        AssertMailNamedMapKeys(new AnniversaryPreHeatingDataDb(), ["ActivityId", "AnniversaryPreHeatingGroupDatas"], "AnniversaryPreHeatingDataDb");
        AssertMailNamedMapKeys(new AnniversaryPreHeatingGroupDataDb(), ["GroupId", "ReadContentIds", "RewardReceived"], "AnniversaryPreHeatingGroupDataDb");

        AnniversaryPreHeatingActivityTable activity = TableReaderV2.Parse<AnniversaryPreHeatingActivityTable>().Single();
        AnniversaryPreHeatingGroupTable[] groups = TableReaderV2.Parse<AnniversaryPreHeatingGroupTable>()
            .Where(row => activity.GroupIds.Contains(row.Id)).ToArray();
        AssertEqual(7, groups.Length, "authored Time Trace groups");
        // Closed by user decision (4.9.1 tables are placeholder text): the real schedule has no window for 51503-51510.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        for (int timeId = 51503; timeId <= 51510; timeId++)
            AssertEqual(false, ActivityScheduleService.IsOpen(timeId, now), "Time Trace TimeId stays closed " + timeId);
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out _, out _, out _);
        Player player = CreateDrawCompatibilityPlayer(playerId);
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(playerId), player,
            CreateDrawCompatibilityInventory(playerId, []), "anniversary-preheating");
        int id = 49_610_000;
        T Call<T>(string name, object request) where T : class
        {
            InvokeRegisteredRequestHandler(name + "Request", harness.Session, ++id, request);
            return ReadResponsePayload<T>(harness, id, name + "Response", name, maxPacketsToRead: 16);
        }
        // Closed by default: requests answer NotOpen, nothing is pushed or persisted.
        AssertEqual(null, AnniversaryPreHeatingModule.BuildNotify(harness.Session.player, now), "closed activity sends no login push");
        AssertEqual(20436001, Call<AnniversaryPreHeatingReadResponse>("AnniversaryPreHeatingRead",
            new AnniversaryPreHeatingReadRequest { GroupId = groups[0].Id, ContentId = groups[0].ContentIds[0] }).Code, "closed read");
        AssertEqual(20436001, Call<AnniversaryPreHeatingRewardResponse>("AnniversaryPreHeatingReward",
            new AnniversaryPreHeatingRewardRequest { GroupId = groups[0].Id }).Code, "closed reward");
        AssertEqual(0, harness.Session.player.AnniversaryPreHeating.Groups.Count, "closed requests persist nothing");
        int Read(int groupId, int contentId) => Call<AnniversaryPreHeatingReadResponse>("AnniversaryPreHeatingRead",
            new AnniversaryPreHeatingReadRequest { GroupId = groupId, ContentId = contentId }).Code;
        AnniversaryPreHeatingRewardResponse Claim(int groupId) => Call<AnniversaryPreHeatingRewardResponse>(
            "AnniversaryPreHeatingReward", new AnniversaryPreHeatingRewardRequest { GroupId = groupId });
        long Owned(int itemId) => harness.Session.inventory.Items.Where(item => item.Id == itemId).Sum(item => (long)item.Count);

        // Handlers need an open window: inject one for the test only (the real schedule keeps Time Trace closed, asserted above).
        ActivityScheduleService.TestEntries = groups.Select(group => group.TimeId).Append(activity.TimeId)
            .Select(timeId => new ActivityScheduleEntry(timeId, 0, ActivityScheduleService.UnboundedEnd, "test-only:AnniversaryPreHeating")).ToArray();
        try
        {
        // Login push: fresh state carries the activity id and no groups.
        NotifyAnniversaryPreHeatingData fresh = AnniversaryPreHeatingModule.BuildNotify(harness.Session.player, now)!;
        AssertEqual(activity.Id, fresh.Data.ActivityId, "login push activity id");
        AssertEqual(0, fresh.Data.AnniversaryPreHeatingGroupDatas.Count, "fresh login push has no groups");

        AnniversaryPreHeatingGroupTable a = groups[0], b = groups[1];
        // Failure boundaries.
        AssertEqual(20436004, Read(int.MaxValue, a.ContentIds[0]), "unknown group");
        AssertEqual(20436007, Read(a.Id, b.ContentIds[0]), "content of another group");
        AssertEqual(20436004, Claim(int.MaxValue).Code, "reward for unknown group");
        AssertEqual(20436008, Claim(a.Id).Code, "reward before reading");
        AssertEqual(0, harness.Session.player.AnniversaryPreHeating.Groups.Count, "rejections persist nothing");

        // Reading is persisted and idempotent; the reward needs every content of the group.
        AssertEqual(0, Read(a.Id, a.ContentIds[0]), "first read");
        AssertEqual(0, Read(a.Id, a.ContentIds[0]), "repeat read");
        AssertEqual(1, harness.Session.player.AnniversaryPreHeating.Groups.Single().ReadContentIds.Count, "repeat read stores once");
        AssertEqual(20436008, Claim(a.Id).Code, "reward with partial reading");
        foreach (int contentId in a.ContentIds.Skip(1)) AssertEqual(0, Read(a.Id, contentId), "read content " + contentId);

        // Reward: authored goods once; the repeat grants nothing.
        List<RewardGoodsTable> goodsA = RewardHandler.GetRewardGoods(a.RewardId);
        int itemA = (int)goodsA[0].TemplateId;
        long beforeA = Owned(itemA);
        AnniversaryPreHeatingRewardResponse claimed = Claim(a.Id);
        AssertEqual(0, claimed.Code, "reward claimed");
        AssertEqual(true, claimed.RewardGoodsList.Select(goods => goods.TemplateId).OrderBy(x => x)
            .SequenceEqual(goodsA.Select(goods => (int)goods.TemplateId).OrderBy(x => x)), "response lists the authored goods");
        AssertEqual(beforeA + goodsA.Where(goods => goods.TemplateId == itemA).Sum(goods => (long)goods.Count), Owned(itemA), "inventory grew by authored reward");
        long afterA = Owned(itemA);
        AssertEqual(20436009, Claim(a.Id).Code, "repeat reward rejected");
        AssertEqual(afterA, Owned(itemA), "repeat grants nothing");

        // A second group is independent and pays its own reward.
        foreach (int contentId in b.ContentIds) AssertEqual(0, Read(b.Id, contentId), "read group B content");
        List<RewardGoodsTable> goodsB = RewardHandler.GetRewardGoods(b.RewardId);
        AssertEqual(true, a.RewardId != b.RewardId, "distinct group rewards");
        int itemB = (int)goodsB.First(goods => goods.TemplateId != 50005).TemplateId;
        long beforeB = Owned(itemB);
        AssertEqual(0, Claim(b.Id).Code, "second group reward");
        AssertEqual(beforeB + goodsB.Where(goods => goods.TemplateId == itemB).Sum(goods => (long)goods.Count), Owned(itemB), "second group paid its own reward");

        // Persistence: BSON reload keeps reads and the claim flag, and the login push mirrors them.
        player = harness.Session.player = BsonSerializer.Deserialize<Player>(harness.Session.player.ToBson());
        NotifyAnniversaryPreHeatingData reloaded = AnniversaryPreHeatingModule.BuildNotify(player, now)!;
        AssertEqual(2, reloaded.Data.AnniversaryPreHeatingGroupDatas.Count, "reloaded groups");
        AnniversaryPreHeatingGroupDataDb pushedA = reloaded.Data.AnniversaryPreHeatingGroupDatas.Single(group => group.GroupId == a.Id);
        AssertEqual(true, pushedA.RewardReceived, "reward flag survives reload");
        AssertEqual(true, pushedA.ReadContentIds.SequenceEqual(a.ContentIds.Order()), "read contents survive reload");
        AssertEqual(20436009, Claim(a.Id).Code, "repeat reward rejected after reload");

        // State is per player.
        AssertEqual(false, AnniversaryPreHeatingModule.BuildNotify(CreateDrawCompatibilityPlayer(playerId + 1), now)!
            .Data.AnniversaryPreHeatingGroupDatas.Any(), "other player starts empty");
        }
        finally { ActivityScheduleService.TestEntries = []; }
    }
}
