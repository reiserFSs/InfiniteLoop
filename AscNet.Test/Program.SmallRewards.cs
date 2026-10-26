using System.Reflection;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.miniactivity.versiongift;
using AscNet.Table.V2.share.task;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace AscNet.Test;

internal static partial class Program
{
    private static void ValidateSmallRewardsCompatibility()
    {
        foreach (string name in new[] { "VersionGiftGetRewardRequest", "AccumulatedConsumeDrawDoDrawRequest", "ClaimSignRewardRequest",
            "EnterAccumulateExpendShopRequest", "GameCollectionEnterGameRequest" })
            AssertEqual("AscNet.GameServer.Handlers.SmallRewardsModule", GetRegisteredRequestHandlerMethod(name).DeclaringType?.FullName, $"{name} registered handler");
        Type module = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.SmallRewardsModule");
        MethodInfo claimVersionGift = module.GetMethod("ClaimVersionGift", BindingFlags.Static | BindingFlags.NonPublic)!;
        MethodInfo claimSign = module.GetMethod("ClaimSign", BindingFlags.Static | BindingFlags.NonPublic)!;
        MethodInfo buildVersionGift = module.GetMethod("BuildVersionGiftNotify", BindingFlags.Static | BindingFlags.NonPublic)!;
        MethodInfo buildShop = module.GetMethod("BuildShopNotify", BindingFlags.Static | BindingFlags.NonPublic)!;
        MethodInfo buildCollection = module.GetMethod("BuildGameCollectionNotify", BindingFlags.Static | BindingFlags.NonPublic)!;

        VersionGiftActivityTable gift = TableReaderV2.Parse<VersionGiftActivityTable>().OrderByDescending(row => row.Id).First();
        VersionGiftProcessTable process = TableReaderV2.Parse<VersionGiftProcessTable>().Single(row => row.ActivityId == gift.Id);
        List<int> finishable = new[] { gift.DailyTaskGroup, gift.NormalTaskGroup, gift.ActivityTaskGroup }
            .Select(id => TableReaderV2.Parse<TaskTimeLimitTable>().Single(row => row.Id == id))
            .SelectMany(group => group.TaskId.Concat(group.DayTaskId).Concat(group.WeekTaskId)).Distinct().ToList();
        AssertEqual(true, finishable.Count >= process.TaskCompleteCount[1], "version gift groups hold enough tasks for two milestones");

        const long playerId = 99_920;
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out RecordingMongoCollectionProxy<Player> playerSaves, out _, out RecordingMongoCollectionProxy<AscNet.Common.Database.Inventory> inventorySaves);
        Player player = CreateDrawCompatibilityPlayer(playerId);
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(playerId), player,
            CreateDrawCompatibilityInventory(playerId, [new Item { Id = 5, Count = 5000 }]), "small-rewards");
        Session session = harness.Session;
        long Count(int id) => session.inventory.Items.FirstOrDefault(item => item.Id == id)?.Count ?? 0;
        int packetId = 900;
        List<string> pushNames = [];
        Dictionary<string, byte[]> lastPush = [];
        T Call<T>(string name, object request)
        {
            int id = ++packetId;
            pushNames.Clear();
            InvokeRegisteredRequestHandler(name, session, id, request);
            while (true)
            {
                Packet packet = harness.ReadPacket(name);
                if (packet.Type == Packet.ContentType.Push)
                {
                    Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                    pushNames.Add(push.Name);
                    lastPush[push.Name] = push.Content;
                    continue;
                }
                Packet.Response wire = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                AssertEqual(id, wire.Id, $"{name} response id");
                T response = MessagePackSerializer.Deserialize<T>(wire.Content);
                return response;
            }
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        void Reload() => session.player = BsonSerializer.Deserialize<Player>(session.player.ToBson());

        // ---- VersionGiftGetRewardRequest
        VersionGiftGetRewardResponse Gift(int type) => Call<VersionGiftGetRewardResponse>("VersionGiftGetRewardRequest", new VersionGiftGetRewardRequest { Type = type });
        NotifyVersionGiftActivity GiftLogin() => (NotifyVersionGiftActivity)buildVersionGift.Invoke(null, [session.player, now])!;
        NotifyVersionGiftActivity login = GiftLogin();
        AssertEqual(gift.Id, login.ActivityId, "login NotifyVersionGiftActivity ActivityId from table");
        AssertEqual(false, login.IsGetDailyGiftReward || login.IsGetVersionGiftReward, "fresh version gift unclaimed");
        AssertEqual(20247003, Gift(99).Code, "unknown reward type");
        AssertEqual(20247008, Gift(3).Code, "progress reward needs finished tasks");
        long daily = Count(90031), version = Count(50005);
        VersionGiftGetRewardResponse dailyResponse = Gift(1);
        AssertEqual(0, dailyResponse.Code, "daily gift claim");
        AssertEqual("90031x2", string.Join(",", dailyResponse.RewardList!.Select(goods => $"{goods.TemplateId}x{goods.Count}")), "daily gift goods from reward table");
        AssertEqual(true, pushNames.Contains("NotifyGiftRewardUpdate"), "daily claim pushes NotifyGiftRewardUpdate");
        AssertEqual(daily + 2, Count(90031), "daily gift granted once");
        AssertEqual(20247004, Gift(1).Code, "daily gift already claimed");
        AssertEqual(daily + 2, Count(90031), "rejected daily claim grants nothing");
        object?[] nextDay = [session, 1, now.AddDays(2), null];
        AssertEqual(0, (int)claimVersionGift.Invoke(null, nextDay)!, "daily gift claimable again after reset");
        AssertEqual(daily + 4, Count(90031), "second day daily gift granted");
        AssertEqual(0, Gift(2).Code, "version gift claim");
        AssertEqual(version + 300, Count(50005), "version gift granted");
        AssertEqual(20247005, Gift(2).Code, "version gift already claimed");
        AssertEqual(version + 300, Count(50005), "version gift granted once");

        session.player.MissionProgress.ClaimedTaskIds.AddRange(finishable.Take(process.TaskCompleteCount[0]));
        long first = Count(97046);
        VersionGiftGetRewardResponse progress = Gift(3);
        AssertEqual(0, progress.Code, "first milestone claim");
        AssertEqual("97046x1", string.Join(",", progress.RewardList!.Select(goods => $"{goods.TemplateId}x{goods.Count}")), "first milestone goods");
        NotifyGiftRewardUpdate update = MessagePackSerializer.Deserialize<NotifyGiftRewardUpdate>(lastPush["NotifyGiftRewardUpdate"]);
        AssertEqual("1", string.Join(",", update.GetProgressRewardIndexSet), "update lists claimed milestone");
        AssertEqual(true, update.IsGetDailyGiftReward && update.IsGetVersionGiftReward, "update carries daily and version flags");
        AssertEqual(20247006, Gift(3).Code, "milestone already claimed");
        AssertEqual(first + 1, Count(97046), "milestone granted once");
        session.player.MissionProgress.ClaimedTaskIds.AddRange(finishable.Skip(process.TaskCompleteCount[0]).Take(process.TaskCompleteCount[1] - process.TaskCompleteCount[0]));
        long second = Count(3005);
        VersionGiftGetRewardResponse progress2 = Gift(3);
        AssertEqual("3005x1", string.Join(",", progress2.RewardList!.Select(goods => $"{goods.TemplateId}x{goods.Count}")), "second milestone grants only the new milestone");
        AssertEqual(second + 1, Count(3005), "second milestone granted");
        Reload();
        login = GiftLogin();
        AssertEqual("1,2", string.Join(",", login.ProgressRewardIndexSet), "relog keeps milestones");
        AssertEqual(true, login.IsGetDailyGiftReward && login.IsGetVersionGiftReward, "relog keeps daily/version flags");
        AssertEqual(20247006, Gift(3).Code, "relogged milestones stay claimed");
        AssertEqual(20247005, Gift(2).Code, "relogged version gift stays claimed");
        AssertEqual(true, playerSaves.ReplaceOneCalls > 0 && inventorySaves.ReplaceOneCalls > 0, "claims persisted");

        // ---- AccumulatedConsumeDrawDoDrawRequest (closed: EN ships no draw activity/odds)
        long hongka = Count(5);
        AssertEqual(20162002, Call<AccumulatedConsumeDrawDoDrawResponse>("AccumulatedConsumeDrawDoDrawRequest", new AccumulatedConsumeDrawDoDrawRequest { Count = 1 }).Code, "draw fails closed");
        AssertEqual(hongka, Count(5), "closed draw changes nothing");

        // ---- Accumulate expend shop
        EnterAccumulateExpendShopResponse Enter() => Call<EnterAccumulateExpendShopResponse>("EnterAccumulateExpendShopRequest", new EnterAccumulateExpendShopRequest());
        ClaimSignRewardResponse Sign() => Call<ClaimSignRewardResponse>("ClaimSignRewardRequest", new ClaimSignRewardRequest());
        NotifyAccumulateExpendShopData Shop() => (NotifyAccumulateExpendShopData)buildShop.Invoke(null, [session, now])!;
        session.player.PlayerData.Level = 10;
        AssertEqual(0, Shop().ActivityId, "shop closed below the authored player-level condition");
        AssertEqual(20322001, Enter().Code, "enter shop below level");
        AssertEqual(20322001, Sign().Code, "sign below level");
        session.player.PlayerData.Level = 60;
        NotifyAccumulateExpendShopData shop = Shop();
        AssertEqual(1, shop.ActivityId, "shop open at level");
        AssertEqual(0, shop.SignRewardData.TodaySignRewardStatus, "sign claimable");
        AssertEqual(0, shop.SignRewardData.Index, "sign index");
        EnterAccumulateExpendShopResponse idle = Enter();
        AssertEqual(true, idle.Code == 0 && idle.RewardGoodsList is null, "enter with no spend grants nothing");
        session.inventory.Do(5, -7);
        shop = Shop();
        AssertEqual(7, shop.TotalConsumeCount, "spend tracked");
        EnterAccumulateExpendShopResponse converted = Enter();
        AssertEqual("97078x70", string.Join(",", converted.RewardGoodsList!.Select(goods => $"{goods.TemplateId}x{goods.Count}")), "7 spent converts to 70 tokens");
        AssertEqual(70, Count(97078), "tokens granted");
        NotifyAccumulateExpendShopData pushed = MessagePackSerializer.Deserialize<NotifyAccumulateExpendShopData>(lastPush["NotifyAccumulateExpendShopData"]);
        AssertEqual(7, pushed.ConvertedCount, "enter pushes converted count");
        AssertEqual(true, Enter().RewardGoodsList is null && Count(97078) == 70, "enter is idempotent");
        session.inventory.Do(5, -3700);
        EnterAccumulateExpendShopResponse capped = Enter();
        AssertEqual(36230, capped.RewardGoodsList!.Single().Count, "conversion stops at ShopItemMaxCount");
        AssertEqual(36300, Count(97078), "token cap reached");
        AssertEqual(3630, Shop().ConvertedCount, "converted count capped");
        long coupons = Count(3);
        ClaimSignRewardResponse signed = Sign();
        AssertEqual("3x10", string.Join(",", signed.RewardGoodsList!.Select(goods => $"{goods.TemplateId}x{goods.Count}")), "sign reward from table");
        AssertEqual(coupons + 10, Count(3), "sign reward granted");
        AssertEqual(1, Shop().SignRewardData.TodaySignRewardStatus, "signed today");
        AssertEqual(20322006, Sign().Code, "sign already claimed today");
        AssertEqual(coupons + 10, Count(3), "second sign grants nothing");
        object?[] signLater = [session, now.AddDays(2), null];
        AssertEqual(20322007, (int)claimSign.Invoke(null, signLater)!, "all sign rewards consumed");
        Reload();
        shop = Shop();
        AssertEqual(3630, shop.ConvertedCount, "relog keeps converted count");
        AssertEqual(3707, shop.TotalConsumeCount, "relog keeps spend");
        AssertEqual(1, shop.SignRewardData.Index, "relog keeps sign index");
        AssertEqual(20322006, Sign().Code, "relogged sign stays claimed");

        // ---- GameCollectionEnterGameRequest
        int Enter2(int type, int stage) => Call<GameCollectionEnterGameResponse>("GameCollectionEnterGameRequest", new GameCollectionEnterGameRequest { GameType = type, StageId = stage }).Code;
        NotifyGameCollectionData collection = (NotifyGameCollectionData)buildCollection.Invoke(null, null)!;
        AssertEqual(1, collection.GameCollectionData.ActivityId, "game collection activity from table");
        AssertEqual(20424003, Enter2(99, 0), "unknown game type");
        AssertEqual(20424004, Enter2(2, 0), "stage id must match the authored game");
        AssertEqual(20424004, Enter2(1, 31), "stage id on a stage-less game");
        AssertEqual(0, Enter2(2, 31), "enter game 2048");
        AssertEqual(0, Enter2(1, 0), "enter golden miner");
        AssertEqual(0, Enter2(3, 4001), "enter fangkuai");
        session.player.FangKuai.StageDataDict[1] = new FangKuaiStageData { StageId = 4001 };
        AssertEqual(20424002, Enter2(1, 0), "another game suspended");
        AssertEqual(0, Enter2(3, 4001), "suspended game itself may re-enter");
    }
}
