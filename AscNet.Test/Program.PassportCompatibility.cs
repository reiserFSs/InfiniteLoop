using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.GameServer;
using AscNet.GameServer.Game;
using AscNet.GameServer.Handlers;
using MessagePack;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace AscNet.Test;

internal static partial class Program
{
    private static void ValidatePassportCompatibility()
    {
        // The newest authored season is exercised inside a TEST-ONLY in-memory copy of its own
        // authored window shifted to contain now (Theatre6/FangKuai pattern), restored in finally;
        // Resources keep the official dated calendar and no date is authored here.
        AscNet.Table.V2.share.passport.PassportActivityTable newest = ScheduledPassportSeasons().Last();
        ActivityScheduleEntry[] schedules = (ActivityScheduleEntry[])ActivityScheduleService.All;
        int seasonIndex = Array.FindIndex(schedules, entry => entry.Id == newest.TimeId);
        if (seasonIndex < 0)
            throw new InvalidDataException($"Passport season {newest.Id} time {newest.TimeId} has no authored schedule.");
        ActivityScheduleEntry authoredWindow = schedules[seasonIndex];
        if (authoredWindow.EndTime <= authoredWindow.StartTime)
            throw new InvalidDataException($"Passport season {newest.Id} schedule is not a dated window.");
        long syntheticStart = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 3_600;
        schedules[seasonIndex] = authoredWindow with
        {
            StartTime = syntheticStart,
            EndTime = syntheticStart + (authoredWindow.EndTime - authoredWindow.StartTime),
            Source = "synthetic-test:Passport"
        };
        try
        {
            ValidatePassportSeasonFlow();
        }
        finally
        {
            schedules[seasonIndex] = authoredWindow;
        }
    }

    private static void ValidatePassportSeasonFlow()
    {
        using MongoCollectionOverride mongoOverride = MongoCollectionOverride.InstallForShopCompatibility();
        List<AscNet.Table.V2.share.passport.PassportActivityTable> seasons = ScheduledPassportSeasons();
        AscNet.Table.V2.share.passport.PassportActivityTable season = seasons[^1];
        int previousSeasonId = seasons[^2].Id;
        List<AscNet.Table.V2.share.passport.PassportTypeInfoTable> types =
            AscNet.Common.Util.TableReaderV2.Parse<AscNet.Table.V2.share.passport.PassportTypeInfoTable>()
                .Where(row => row.ActivityId == season.Id).OrderBy(row => row.Id).ToList();
        int freeTypeId = types.Single(row => row.IsFree == 1).Id;
        AscNet.Table.V2.share.passport.PassportTypeInfoTable premiumType = types.First(row => row.IsFree != 1);
        List<AscNet.Table.V2.share.passport.PassportLevelTable> levels =
            AscNet.Common.Util.TableReaderV2.Parse<AscNet.Table.V2.share.passport.PassportLevelTable>()
                .Where(row => row.ActivityId == season.Id).OrderBy(row => row.Level).ToList();
        int LevelFor(long exp) => levels.Where(row => (row.TotalExp ?? 0) <= exp).Max(row => row.Level);
        long Exp(Inventory owner) => owner.Items.FirstOrDefault(item => item.Id == Inventory.PassportExp)?.Count ?? 0;

        const long playerId = 99_460;
        Player player = CreateDrawCompatibilityPlayer(playerId);
        player.Passport.ActivityId = previousSeasonId;
        Inventory inventory = CreateDrawCompatibilityInventory(playerId,
        [
            new Item { Id = 3, Count = 10_000 },
            new Item { Id = 5, Count = 100 },
            new Item { Id = Inventory.PassportExp, Count = 2_000 }
        ]);
        using LoopbackSessionHarness harness = new(
            CreateDrawCompatibilityCharacter(playerId), player, inventory, "passport-compat-test");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(playerId);

        Type module = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.PassportModule");
        MethodInfo login = RequiredMethod(module, "ReconcileAndPushLogin", BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(Session)]);
        login.Invoke(null, [harness.Session]);
        NotifyPassportBaseInfo loginBase = ReadPushPayload<NotifyPassportBaseInfo>(
            harness, nameof(NotifyPassportBaseInfo), "Passport login base info");
        NotifyPassportData loginData = ReadPushPayload<NotifyPassportData>(
            harness, nameof(NotifyPassportData), "Passport login data");
        AssertEqual(season.Id, loginData.ActivityId, "Passport newest authored season is live");
        AssertEqual(1, loginBase.BaseInfo.Level, "Passport rollover initial level");
        AssertEqual(0L, loginBase.BaseInfo.Exp, "Passport rollover resets EXP");
        AssertEqual(2_000L, player.Passport.LastTimeBaseInfo.Exp, "Passport rollover keeps previous season EXP");
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
        HashSet<long> roundTimeIds = AscNet.Common.Util.TableReaderV2
            .Parse<AscNet.Table.V2.share.passport.PassportTaskGroupTable>()
            .Where(group => group.Group == season.WeekTaskGroup && group.Type == 2 && group.TimeId is > 0)
            .Select(group => (long)group.TimeId!.Value)
            .ToHashSet();
        object controlsResult = RequiredMethod(
            RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule"),
            "BuildTimeLimitControlConfigList",
            BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(DateTimeOffset), typeof(bool)]).Invoke(null, [now, false])
            ?? throw new InvalidDataException("Passport time controls were nil.");
        JToken[] activeRounds = JArray.FromObject(controlsResult).Where(control =>
                roundTimeIds.Contains(control.Value<long>("Id"))
                && control.Value<long>("StartTime") <= now.ToUnixTimeSeconds()
                && now.ToUnixTimeSeconds() < control.Value<long>("EndTime"))
            .ToArray();
        AssertEqual(1, activeRounds.Length, "Passport active round time control");
        int activeRoundTimeId = activeRounds[0].Value<int>("Id");
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
            if (!roundConditions.TryGetValue(conditionType, out AscNet.Table.V2.share.task.ConditionTable? condition))
                continue;
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
        AssertIntegerList([freeTypeId], loginData.PassportInfos.Select(info => (long)info.Id).ToArray(),
            "Passport initial free tier");

        const int supplyPacketId = 46_001;
        InvokeRegisteredRequestHandler(nameof(PassportGetSupplyRewardRequest), harness.Session, supplyPacketId,
            new PassportGetSupplyRewardRequest());
        if (season.SupplyReward is > 0)
        {
            NotifyPassportBaseInfo suppliedBase = ReadPushPayload<NotifyPassportBaseInfo>(
                harness, nameof(NotifyPassportBaseInfo), "Passport supply base info");
            _ = ReadPushPayload<NotifyItemDataList>(harness, nameof(NotifyItemDataList), "Passport supply item push");
            PassportGetSupplyRewardResponse supply = ReadResponsePayload<PassportGetSupplyRewardResponse>(
                harness, supplyPacketId, nameof(PassportGetSupplyRewardResponse), "Passport supply response");
            AssertEqual(0, supply.Code, "Passport supply Code");
            AssertEqual(Exp(inventory), suppliedBase.BaseInfo.Exp, "Passport persisted supply EXP");
        }
        else
        {
            PassportGetSupplyRewardResponse supply = ReadResponsePayload<PassportGetSupplyRewardResponse>(
                harness, supplyPacketId, nameof(PassportGetSupplyRewardResponse), "Passport unauthored supply");
            AssertEqual(20137017, supply.Code, "Passport unauthored supply Code");
            AssertEqual(false, player.Passport.IsGetSupplyReward, "Passport unauthored supply not claimed");
        }

        long expBeforeTask = Exp(inventory);
        const int taskPacketId = 46_002;
        InvokeRegisteredRequestHandler("FinishMultiTaskRequest", harness.Session, taskPacketId,
            new Dictionary<string, object> { ["TaskIds"] = new[] { 80_000 } });
        NotifyPassportBaseInfo taskBase = ReadPushPayload<NotifyPassportBaseInfo>(
            harness, nameof(NotifyPassportBaseInfo), "Passport task base info");
        HashSet<string> taskPushes = new();
        Packet taskPacket = harness.ReadPacket("Passport task packet");
        for (int index = 1; taskPacket.Type == Packet.ContentType.Push && index < 8; index++)
        {
            taskPushes.Add(MessagePackSerializer.Deserialize<Packet.Push>(taskPacket.Content).Name);
            taskPacket = harness.ReadPacket($"Passport task packet {index + 1}");
        }
        AssertEqual(true, taskPushes.IsSupersetOf([nameof(NotifyTask), nameof(NotifyItemDataList)]),
            "Passport task sync and item pushes");
        AssertEqual(Packet.ContentType.Response, taskPacket.Type, "Passport task response packet type");
        Packet.Response taskResponse = MessagePackSerializer.Deserialize<Packet.Response>(taskPacket.Content);
        AssertEqual(taskPacketId, taskResponse.Id, "Passport task response id");
        AssertEqual("FinishMultiTaskResponse", taskResponse.Name, "Passport task response name");
        JObject task = JObject.Parse(MessagePackSerializer.ConvertToJson(taskResponse.Content));
        AssertEqual(0, task.Value<int>("Code"), "Passport task Code");
        AssertIntegerList([80_000], task["SuccessTaskIds"]!.Select(value => value.Value<long>()).ToArray(),
            "Passport task success ids");
        AssertEqual(true, taskBase.BaseInfo.Exp > expBeforeTask, "Passport task EXP credited");
        AssertEqual(Exp(inventory), taskBase.BaseInfo.Exp, "Passport task EXP persisted");

        int firstRewardId = AscNet.Common.Util.TableReaderV2.Parse<AscNet.Table.V2.share.passport.PassportRewardTable>()
            .Where(row => row.PassportId == freeTypeId && row.Level == 1 && row.RewardId > 0)
            .OrderBy(row => row.Id).First().Id;
        const int singlePacketId = 46_003;
        InvokeRegisteredRequestHandler(nameof(PassportRecvRewardRequest), harness.Session, singlePacketId,
            new PassportRecvRewardRequest { Id = firstRewardId });
        PassportRecvRewardResponse single = (PassportRecvRewardResponse)ReadResponsePayload(
            harness, singlePacketId, nameof(PassportRecvRewardResponse), "Passport single reward response",
            typeof(PassportRecvRewardResponse), maxPacketsToRead: 8);
        AssertEqual(0, single.Code, "Passport single reward Code");
        AssertEqual(true, player.Passport.PassportInfos.Single(info => info.Id == freeTypeId).GotRewardList.Contains(firstRewardId),
            "Passport single reward persisted claim");

        long expAfterClaim = Exp(inventory);
        login.Invoke(null, [harness.Session]);
        NotifyPassportBaseInfo reloadBase = ReadPushPayload<NotifyPassportBaseInfo>(
            harness, nameof(NotifyPassportBaseInfo), "Passport reload base info");
        NotifyPassportData reloadData = ReadPushPayload<NotifyPassportData>(
            harness, nameof(NotifyPassportData), "Passport reload data");
        AssertEqual(expAfterClaim, reloadBase.BaseInfo.Exp, "Passport reload keeps season EXP");
        AssertEqual(true, reloadData.PassportInfos.Single(info => info.Id == freeTypeId).GotRewardList.Contains(firstRewardId),
            "Passport reload keeps claimed reward");

        const int replayPacketId = 46_007;
        InvokeRegisteredRequestHandler(nameof(PassportRecvRewardRequest), harness.Session, replayPacketId,
            new PassportRecvRewardRequest { Id = firstRewardId });
        PassportRecvRewardResponse replay = (PassportRecvRewardResponse)ReadResponsePayload(
            harness, replayPacketId, nameof(PassportRecvRewardResponse), "Passport replayed reward response",
            typeof(PassportRecvRewardResponse), maxPacketsToRead: 8);
        AssertEqual(true, replay.Code != 0, "Passport replayed reward rejected");
        AssertEqual(1, player.Passport.PassportInfos.Single(info => info.Id == freeTypeId).GotRewardList
            .Count(id => id == firstRewardId), "Passport replayed reward not duplicated");

        const int tierPacketId = 46_005;
        long currencyBeforeTier = inventory.Items.Single(item => item.Id == 5).Count;
        InvokeRegisteredRequestHandler(nameof(PassportBuyPassportRequest), harness.Session, tierPacketId,
            new PassportBuyPassportRequest { Id = premiumType.Id });
        NotifyPassportBaseInfo premiumBase = ReadPushPayload<NotifyPassportBaseInfo>(
            harness, nameof(NotifyPassportBaseInfo), "Passport premium EXP base info");
        PassportBuyPassportResponse tier = (PassportBuyPassportResponse)ReadResponsePayload(
            harness, tierPacketId, nameof(PassportBuyPassportResponse), "Passport tier purchase response",
            typeof(PassportBuyPassportResponse), maxPacketsToRead: 8);
        AssertEqual(0, tier.Code, "Passport tier purchase Code");
        AssertEqual(currencyBeforeTier - (premiumType.CostItemCount ?? 0), inventory.Items.Single(item => item.Id == 5).Count,
            "Passport tier purchase cost");
        AssertEqual(true, player.Passport.PassportInfos.Any(info => info.Id == premiumType.Id),
            "Passport premium tier ownership");
        AssertEqual(Exp(inventory), premiumBase.BaseInfo.Exp, "Passport premium tier EXP");
        AssertEqual(LevelFor(premiumBase.BaseInfo.Exp), premiumBase.BaseInfo.Level, "Passport premium tier level");

        const int allPacketId = 46_004;
        InvokeRegisteredRequestHandler(nameof(PassportRecvAllRewardRequest), harness.Session, allPacketId,
            new PassportRecvAllRewardRequest());
        PassportRecvAllRewardResponse all = (PassportRecvAllRewardResponse)ReadResponsePayload(
            harness, allPacketId, nameof(PassportRecvAllRewardResponse), "Passport all rewards response",
            typeof(PassportRecvAllRewardResponse), maxPacketsToRead: 32);
        AssertEqual(0, all.Code, "Passport all rewards Code");
        AssertEqual(true, all.RewardList.Count > 0, "Passport all rewards goods");

        long expBeforePurchase = Exp(inventory);
        AscNet.Table.V2.share.passport.PassportLevelTable destination =
            levels.Single(row => row.Level == LevelFor(expBeforePurchase) + 1);
        const int expPacketId = 46_006;
        InvokeRegisteredRequestHandler(nameof(PassportBuyExpRequest), harness.Session, expPacketId,
            new PassportBuyExpRequest { ToLevel = destination.Level });
        PassportBuyExpResponse buyExp = (PassportBuyExpResponse)ReadResponsePayload(
            harness, expPacketId, nameof(PassportBuyExpResponse), "Passport EXP purchase response",
            typeof(PassportBuyExpResponse), maxPacketsToRead: 4);
        AssertEqual(0, buyExp.Code, "Passport EXP purchase Code");
        AssertEqual((long)(destination.TotalExp ?? 0), Exp(inventory), "Passport purchased EXP");
    }
}
