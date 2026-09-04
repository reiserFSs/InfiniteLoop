using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using MessagePack;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace AscNet.Test;

internal static partial class Program
{
    private static void ValidatePassportCompatibility()
    {
        using MongoCollectionOverride mongoOverride = MongoCollectionOverride.InstallForShopCompatibility();
        const long playerId = 99_460;
        Player player = CreateDrawCompatibilityPlayer(playerId);
        Inventory inventory = CreateDrawCompatibilityInventory(playerId,
        [
            new Item { Id = 3, Count = 10_000 },
            new Item { Id = 5, Count = 100 }
        ]);
        using LoopbackSessionHarness harness = new(
            CreateDrawCompatibilityCharacter(playerId), player, inventory, "passport-compat-test");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(playerId);

        Type module = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.PassportModule");
        RequiredMethod(module, "ReconcileAndPushLogin", BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(Session)]).Invoke(null, [harness.Session]);
        NotifyPassportBaseInfo loginBase = ReadPushPayload<NotifyPassportBaseInfo>(
            harness, nameof(NotifyPassportBaseInfo), "Passport login base info");
        NotifyPassportData loginData = ReadPushPayload<NotifyPassportData>(
            harness, nameof(NotifyPassportData), "Passport login data");
        AssertEqual(46, loginData.ActivityId, "Passport current activity");
        AssertEqual(1, loginBase.BaseInfo.Level, "Passport initial level");
        AssertEqual(0L, loginBase.BaseInfo.Exp, "Passport initial EXP");
        object taskData = RequiredMethod(
            RequiredAscNetGameServerType("AscNet.GameServer.Handlers.TaskModule"),
            "BuildTaskData",
            BindingFlags.Static | BindingFlags.Public,
            [typeof(Session)]).Invoke(null, [harness.Session])
            ?? throw new InvalidDataException("Passport task data was nil.");
        HashSet<int> passportTaskIds = JArray.FromObject(taskData)
            .Select(task => task.Value<int>("Id"))
            .Where(id => id is >= 80_000 and < 81_000)
            .ToHashSet();
        AssertEqual(true, passportTaskIds.Contains(80_000), "Passport daily mission exposed");
        AssertEqual(true, passportTaskIds.Any(id => id is 80_003 or 80_014 or 80_017),
            "Passport round missions exposed");
        inventory.Do(Inventory.DailyActiveness, 100);
        object updatedTaskData = RequiredMethod(
            RequiredAscNetGameServerType("AscNet.GameServer.Handlers.TaskModule"),
            "BuildTaskData",
            BindingFlags.Static | BindingFlags.Public,
            [typeof(Session)]).Invoke(null, [harness.Session])
            ?? throw new InvalidDataException("Updated Passport task data was nil.");
        JToken totalActivity = JArray.FromObject(updatedTaskData)
            .Single(task => task.Value<int>("Id") == 80_038);
        AssertEqual(100L, totalActivity["Schedule"]![0]!.Value<long>("Value"),
            "Passport Total Activity progress");
        AssertEqual(3, totalActivity.Value<int>("State"), "Passport Total Activity achieved state");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        object controlsResult = RequiredMethod(
            RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule"),
            "BuildTimeLimitControlConfigList",
            BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(DateTimeOffset), typeof(bool)]).Invoke(null, [now, false])
            ?? throw new InvalidDataException("Passport time controls were nil.");
        JArray roundControls = JArray.FromObject(controlsResult);
        AssertEqual(1, roundControls.Count(control =>
                control.Value<long>("Id") is >= 49_905 and <= 49_910
                && control.Value<long>("StartTime") <= now.ToUnixTimeSeconds()
                && now.ToUnixTimeSeconds() < control.Value<long>("EndTime")),
            "Passport active round time control");
        int activeRoundTimeId = roundControls.Single(control =>
            control.Value<long>("Id") is >= 49_905 and <= 49_910
            && control.Value<long>("StartTime") <= now.ToUnixTimeSeconds()
            && now.ToUnixTimeSeconds() < control.Value<long>("EndTime")).Value<int>("Id");
        AscNet.Table.V2.share.passport.PassportTaskGroupTable activeRound =
            AscNet.Common.Util.TableReaderV2.Parse<AscNet.Table.V2.share.passport.PassportTaskGroupTable>()
                .Single(group => group.TimeId == activeRoundTimeId);
        List<AscNet.Table.V2.share.task.TaskTable> roundTasks =
            AscNet.Common.Util.TableReaderV2.Parse<AscNet.Table.V2.share.task.TaskTable>()
                .Where(task => activeRound.TaskId!.Contains(task.Id))
                .ToList();
        Dictionary<int, AscNet.Table.V2.share.task.ConditionTable> roundConditions =
            AscNet.Common.Util.TableReaderV2.Parse<AscNet.Table.V2.share.task.ConditionTable>()
                .Where(condition => condition.Type is 15216 or 25005 or 28005
                    && roundTasks.Any(task => task.Condition == condition.Id))
                .ToDictionary(condition => condition.Type!.Value);
        MethodInfo recordStageClear = RequiredMethod(
            RequiredAscNetGameServerType("AscNet.GameServer.Handlers.TaskModule"),
            "RecordStageClear",
            BindingFlags.Static | BindingFlags.Public,
            [typeof(Session), typeof(int), typeof(int), typeof(int), typeof(bool)]);
        MethodInfo recordArenaResult = RequiredMethod(
            RequiredAscNetGameServerType("AscNet.GameServer.Handlers.TaskModule"),
            "RecordArenaResult",
            BindingFlags.Static | BindingFlags.Public,
            [typeof(Session), typeof(int), typeof(bool)]);
        int painCageStageId = AscNet.Common.Util.TableReaderV2
            .Parse<AscNet.Table.V2.share.fuben.bosssingle.BossSingleStageTable>().First().StageId;
        int siegeStageId = AscNet.Common.Util.TableReaderV2
            .Parse<AscNet.Table.V2.share.guild.boss.GuildBossStageCatalogTable>().First().StageId;
        foreach ((int conditionType, Action<bool> record) in new (int, Action<bool>)[]
        {
            (25005, first => recordStageClear.Invoke(null, [harness.Session, painCageStageId, 1, 0, first])),
            (28005, first => recordArenaResult.Invoke(null, [harness.Session, 0, first])),
            (15216, first => recordStageClear.Invoke(null, [harness.Session, siegeStageId, 1, 0, first]))
        })
        {
            AscNet.Table.V2.share.task.ConditionTable condition = roundConditions[conditionType];
            record(true);
            AssertEqual(1, player.MissionProgress.ConditionCounters.GetValueOrDefault(condition.Id),
                $"Passport first-clear condition {conditionType}");
            record(false);
            AssertEqual(1, player.MissionProgress.ConditionCounters.GetValueOrDefault(condition.Id),
                $"Passport repeated-clear condition {conditionType}");
        }
        while (harness.TryReadAvailablePacket("Passport mission progress push", out _))
        {
        }
        AssertIntegerList([136], loginData.PassportInfos.Select(info => (long)info.Id).ToArray(),
            "Passport initial free tier");

        const int supplyPacketId = 46_001;
        InvokeRegisteredRequestHandler(nameof(PassportGetSupplyRewardRequest), harness.Session, supplyPacketId,
            new PassportGetSupplyRewardRequest());
        NotifyPassportBaseInfo suppliedBase = ReadPushPayload<NotifyPassportBaseInfo>(
            harness, nameof(NotifyPassportBaseInfo), "Passport supply base info");
        _ = ReadPushPayload<NotifyItemDataList>(harness, nameof(NotifyItemDataList), "Passport supply item push");
        PassportGetSupplyRewardResponse supply = ReadResponsePayload<PassportGetSupplyRewardResponse>(
            harness, supplyPacketId, nameof(PassportGetSupplyRewardResponse), "Passport supply response");
        AssertEqual(0, supply.Code, "Passport supply Code");
        AssertEqual(5_600L, suppliedBase.BaseInfo.Exp, "Passport supply EXP");
        AssertEqual(5_600L, inventory.Items.Single(item => item.Id == Inventory.PassportExp).Count,
            "Passport persisted supply EXP");

        const int taskPacketId = 46_002;
        InvokeRegisteredRequestHandler("FinishMultiTaskRequest", harness.Session, taskPacketId,
            new Dictionary<string, object> { ["TaskIds"] = new[] { 80_000 } });
        NotifyPassportBaseInfo taskBase = ReadPushPayload<NotifyPassportBaseInfo>(
            harness, nameof(NotifyPassportBaseInfo), "Passport task base info");
        _ = ReadPushPayload<NotifyTask>(harness, nameof(NotifyTask), "Passport task sync");
        _ = ReadPushPayload<NotifyItemDataList>(harness, nameof(NotifyItemDataList), "Passport task item push");
        JObject task = ReadResponseMapPayload(
            harness, taskPacketId, "FinishMultiTaskResponse", "Passport task response");
        AssertEqual(0, task.Value<int>("Code"), "Passport task Code");
        AssertIntegerList([80_000], task["SuccessTaskIds"]!.Select(value => value.Value<long>()).ToArray(),
            "Passport task success ids");
        AssertEqual(5_700L, taskBase.BaseInfo.Exp, "Passport task EXP");

        int firstRewardId = AscNet.Common.Util.TableReaderV2.Parse<AscNet.Table.V2.share.passport.PassportRewardTable>()
            .Where(row => row.PassportId == 136 && row.Level == 1 && row.RewardId > 0)
            .OrderBy(row => row.Id).First().Id;
        const int singlePacketId = 46_003;
        InvokeRegisteredRequestHandler(nameof(PassportRecvRewardRequest), harness.Session, singlePacketId,
            new PassportRecvRewardRequest { Id = firstRewardId });
        PassportRecvRewardResponse single = (PassportRecvRewardResponse)ReadResponsePayload(
            harness, singlePacketId, nameof(PassportRecvRewardResponse), "Passport single reward response",
            typeof(PassportRecvRewardResponse), maxPacketsToRead: 8);
        AssertEqual(0, single.Code, "Passport single reward Code");
        AssertEqual(true, player.Passport.PassportInfos.Single(info => info.Id == 136).GotRewardList.Contains(firstRewardId),
            "Passport single reward persisted claim");

        const int allPacketId = 46_004;
        InvokeRegisteredRequestHandler(nameof(PassportRecvAllRewardRequest), harness.Session, allPacketId,
            new PassportRecvAllRewardRequest());
        PassportRecvAllRewardResponse all = (PassportRecvAllRewardResponse)ReadResponsePayload(
            harness, allPacketId, nameof(PassportRecvAllRewardResponse), "Passport all rewards response",
            typeof(PassportRecvAllRewardResponse), maxPacketsToRead: 16);
        AssertEqual(0, all.Code, "Passport all rewards Code");
        AssertEqual(true, all.RewardList.Count > 0, "Passport all rewards goods");

        const int tierPacketId = 46_005;
        InvokeRegisteredRequestHandler(nameof(PassportBuyPassportRequest), harness.Session, tierPacketId,
            new PassportBuyPassportRequest { Id = 137 });
        NotifyPassportBaseInfo premiumBase = ReadPushPayload<NotifyPassportBaseInfo>(
            harness, nameof(NotifyPassportBaseInfo), "Passport premium EXP base info");
        PassportBuyPassportResponse tier = (PassportBuyPassportResponse)ReadResponsePayload(
            harness, tierPacketId, nameof(PassportBuyPassportResponse), "Passport tier purchase response",
            typeof(PassportBuyPassportResponse), maxPacketsToRead: 8);
        AssertEqual(0, tier.Code, "Passport tier purchase Code");
        AssertEqual(70L, inventory.Items.Single(item => item.Id == 5).Count, "Passport tier purchase cost");
        AssertEqual(true, player.Passport.PassportInfos.Any(info => info.Id == 137),
            "Passport premium tier ownership");
        AssertEqual(10_700L, premiumBase.BaseInfo.Exp, "Passport premium tier EXP");
        AssertEqual(22, premiumBase.BaseInfo.Level, "Passport premium tier level");

        List<AscNet.Table.V2.share.passport.PassportLevelTable> levels =
            AscNet.Common.Util.TableReaderV2.Parse<AscNet.Table.V2.share.passport.PassportLevelTable>()
                .Where(row => row.ActivityId == 46).OrderBy(row => row.Level).ToList();
        long expBeforePurchase = inventory.Items.Single(item => item.Id == Inventory.PassportExp).Count;
        int currentLevel = levels.Where(row => (row.TotalExp ?? 0) <= expBeforePurchase).Max(row => row.Level);
        AscNet.Table.V2.share.passport.PassportLevelTable destination =
            levels.Single(row => row.Level == currentLevel + 1);
        const int expPacketId = 46_006;
        InvokeRegisteredRequestHandler(nameof(PassportBuyExpRequest), harness.Session, expPacketId,
            new PassportBuyExpRequest { ToLevel = destination.Level });
        PassportBuyExpResponse buyExp = (PassportBuyExpResponse)ReadResponsePayload(
            harness, expPacketId, nameof(PassportBuyExpResponse), "Passport EXP purchase response",
            typeof(PassportBuyExpResponse), maxPacketsToRead: 4);
        AssertEqual(0, buyExp.Code, "Passport EXP purchase Code");
        AssertEqual((long)(destination.TotalExp ?? 0), inventory.Items.Single(item => item.Id == Inventory.PassportExp).Count,
            "Passport purchased EXP");
    }
}
