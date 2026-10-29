using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Game;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.reward;
using AscNet.Table.V2.share.wheelchairmanual;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Newtonsoft.Json.Linq;
using System.Reflection;

namespace AscNet.Test;

internal static partial class Program
{
    private static void ValidateWheelchairManualGuideCompatibility()
    {
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out RecordingMongoCollectionProxy<Player> players,
            out RecordingMongoCollectionProxy<Character> characters,
            out RecordingMongoCollectionProxy<Inventory> inventories);
        Player player = CreateDrawCompatibilityPlayer(46_810);
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(player.PlayerData.Id), player,
            CreateDrawCompatibilityInventory(player.PlayerData.Id, []), "manual-guide-reward-receipts");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(player.PlayerData.Id);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        MethodInfo timeControls = RequiredMethod(
            RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule"),
            "BuildTimeLimitControlConfigList", BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(DateTimeOffset), typeof(bool)]);
        var existingWindows = ((List<TimeLimitCtrlConfigList>)timeControls.Invoke(null, [now, false])!)
            .ToDictionary(row => row.Id);
        var manualWindows = ((List<TimeLimitCtrlConfigList>)timeControls.Invoke(null, [now, true])!)
            .ToDictionary(row => row.Id);
        foreach (var window in existingWindows.Values)
        {
            AssertEqual(window.StartTime, manualWindows[window.Id].StartTime, "Manual availability preserves existing opening bounds");
            AssertEqual(window.EndTime, manualWindows[window.Id].EndTime, "Manual availability preserves existing closing bounds");
        }
        foreach (int timeId in TableReaderV2.Parse<WheelchairManualGuideActivityPeriodTable>().Select(row => row.TimeId).Distinct())
        {
            AssertEqual(true, manualWindows.ContainsKey(timeId), "Configured manual period has a client-visible time control");
            AssertEqual(existingWindows.GetValueOrDefault(timeId)?.StartTime ?? 0, manualWindows[timeId].StartTime,
                "Unscheduled manual period has no invented opening date");
            AssertEqual(existingWindows.GetValueOrDefault(timeId)?.EndTime ?? 0, manualWindows[timeId].EndTime,
                "Unscheduled manual period stays available until explicitly scheduled");
        }
        WheelchairManualGuideActivityTable guide = TableReaderV2.Parse<WheelchairManualGuideActivityTable>()
            .First(row => row.EventCause.Count > 0 && row.TaskSourceGroupId is not > 0
                && row.MainTemplateIds.Contains(3));
        RewardGoodsTable goods = TableReaderV2.Parse<RewardGoodsTable>()
            .First(row => row.TemplateId == 3 && row.Count > 0);
        Type grantType = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.RewardGrant");
        Type rewardHandler = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.RewardHandler");
        MethodInfo apply = rewardHandler.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Single(method => method.Name == "ApplyRewardsOnceAndPersist");
        void Grant(string key, RewardGoodsTable? reward = null, int? eventCause = null)
        {
            Array grants = Array.CreateInstance(grantType, 1);
            grants.SetValue(Activator.CreateInstance(grantType, key, new[] { reward ?? goods }, null, eventCause ?? guide.EventCause[0]), 0);
            apply.Invoke(null, [grants, harness.Session]);
        }
        JObject Payload(DateTimeOffset clock)
        {
            NotifyWheelchairManualActivity payload = new();
            WheelchairManualGuideManager.PopulatePayload(harness.Session, clock, payload);
            return JObject.FromObject(payload);
        }
        int Received(JObject payload) => payload["TimeLimitActivityInfos"]!
            .Single(row => row.Value<int>("ActivityId") == guide.Id)["PeriodInfos"]!
            .SelectMany(period => period["GotRewards"]!).Where(row => row.Value<int>("TemplateId") == goods.TemplateId)
            .Sum(row => row.Value<int>("Count"));

        AssertEqual(0, Received(Payload(now)), "fresh guide has no acquired reward");
        Grant("guide-regression:first");
        now = DateTimeOffset.UtcNow;
        AssertEqual(goods.Count, Received(Payload(now)), "committed reward is visible in guide progress");
        Grant("guide-regression:first");
        AssertEqual(goods.Count, Received(Payload(DateTimeOffset.UtcNow)), "reward replay cannot double guide progress");
        AssertEqual(0, Received(Payload(DateTimeOffset.FromUnixTimeSeconds(
            player.WheelchairManualGuideRewardReceipts.Single().GrantedAt - 1))), "future receipt is not visible before grant");

        List<WheelchairManualGuideRewardReceipt> committed = player.WheelchairManualGuideRewardReceipts;
        players.ThrowOnReplaceOne = true;
        bool failed = false;
        try { Grant("guide-regression:retry"); }
        catch (TargetInvocationException) { failed = true; }
        finally { players.ThrowOnReplaceOne = false; }
        AssertEqual(true, failed, "guide reward scenario reaches injected persistence failure");
        AssertEqual(true, ReferenceEquals(committed, player.WheelchairManualGuideRewardReceipts),
            "failed reward restores committed guide receipt list");
        Grant("guide-regression:retry");
        int expected = checked(goods.Count * 2);
        AssertEqual(expected, Received(Payload(DateTimeOffset.UtcNow)), "retry after partial save records reward once");
        harness.Session.player = BsonSerializer.Deserialize<Player>(player.ToBson());
        AssertEqual(expected, Received(Payload(DateTimeOffset.UtcNow)), "relog retains guide receipts");

        WheelchairManualGuideManager.SendUpdate(harness.Session);
        Packet packet = harness.ReadPacket("guide progress update");
        AssertEqual(Packet.ContentType.Push, packet.Type, "guide update is a real socket push");
        Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
        AssertEqual(nameof(NotifyWheelchairManualActivityUpdate), push.Name, "guide update packet name");
        JObject update = JObject.Parse(MessagePackSerializer.ConvertToJson(push.Content));
        JObject normalized = new() { ["TimeLimitActivityInfos"] = update["UpdateTimeLimitActivityInfos"]!.DeepClone() };
        AssertEqual(expected, Received(normalized), "wire update reflects durable acquired rewards");

        WheelchairManualGuideActivityTable unscheduledGuide = TableReaderV2.Parse<WheelchairManualGuideActivityTable>()
            .First(row => !ActivityScheduleService.TryGet(row.TimeId, out _)
                && TableReaderV2.Parse<WheelchairManualGuideActivityPeriodTable>()
                    .Any(period => period.Id == row.PeriodIds && !ActivityScheduleService.TryGet(period.TimeId, out _)));
        WheelchairManualGuideActivityPeriodTable scheduledPeriod = TableReaderV2.Parse<WheelchairManualGuideActivityPeriodTable>()
            .Single(row => row.Id == unscheduledGuide.PeriodIds);
        ActivityScheduleEntry periodWindow = ActivityScheduleService.All
            .First(row => row.StartTime > 0 && row.EndTime > row.StartTime);
        void AssertPeriodAvailability(DateTimeOffset clock, bool available)
        {
            JObject full = Payload(clock);
            AssertEqual(true, full["OpenActivityIds"]!.Values<int>().Contains(unscheduledGuide.Id),
                "parent guide remains open independently of period window");
            JObject delta = JObject.Parse(MessagePackSerializer.ConvertToJson(
                MessagePackSerializer.Serialize(WheelchairManualGuideManager.BuildUpdate(harness.Session, clock))));
            foreach (JToken progress in new[] { full["TimeLimitActivityInfos"]!, delta["UpdateTimeLimitActivityInfos"]! })
            {
                JToken? activityProgress = progress.SingleOrDefault(row => row.Value<int>("ActivityId") == unscheduledGuide.Id);
                AssertEqual(available, activityProgress is not null,
                    "full and update progress honor the period window while parent remains open");
                if (available)
                    AssertEqual(scheduledPeriod.Id, activityProgress!["PeriodInfos"]!.Single().Value<int>("PeriodId"),
                        "available period retains its configured identity");
            }
        }
        int originalPeriodTimeId = scheduledPeriod.TimeId;
        try
        {
            AssertPeriodAvailability(now, true);
            scheduledPeriod.TimeId = checked((int)periodWindow.Id);
            AssertPeriodAvailability(DateTimeOffset.FromUnixTimeSeconds(periodWindow.StartTime - 1), false);
            AssertPeriodAvailability(DateTimeOffset.FromUnixTimeSeconds(periodWindow.StartTime), true);
            AssertPeriodAvailability(DateTimeOffset.FromUnixTimeSeconds(periodWindow.EndTime), false);
        }
        finally
        {
            scheduledPeriod.TimeId = originalPeriodTimeId;
        }
        AssertPeriodAvailability(now, true);

        Player other = CreateDrawCompatibilityPlayer(46_811);
        harness.Session.player = other;
        AssertEqual(0, Received(Payload(DateTimeOffset.UtcNow)), "distinct player does not inherit receipt progress");
        WheelchairManualGuideWeekActivityTable guild = TableReaderV2.Parse<WheelchairManualGuideWeekActivityTable>()
            .Single(row => row.ChapterType is null);
        WheelchairManualGuideWeekRewardTable guildReward = TableReaderV2.Parse<WheelchairManualGuideWeekRewardTable>()
            .Single(row => row.MainId == guild.Id);
        RewardGoodsTable guildGoods = TableReaderV2.Parse<RewardGoodsTable>()
            .First(row => row.TemplateId == guildReward.MainTemplateId[0] && row.Count > 0);
        Grant("guide-regression:weekly", guildGoods, guildReward.EventCause[0]);
        JObject beforeReset = Payload(DateTimeOffset.UtcNow);
        int WeeklyCount(JObject payload) => payload["WeekActivityInfos"]!
            .Single(row => row.Value<int>("MainId") == guild.Id)["GotRewards"]!
            .Where(row => row.Value<int>("TemplateId") == guildGoods.TemplateId).Sum(row => row.Value<int>("Count"));
        AssertEqual(guildGoods.Count, WeeklyCount(beforeReset), "weekly guide reports current acquired reward");
        DateTimeOffset reset = DateTimeOffset.FromUnixTimeSeconds(beforeReset.Value<long>("CurrentGuildBossEndTime"));
        AssertEqual(guildGoods.Count, WeeklyCount(Payload(reset.AddSeconds(-1))), "weekly reward survives until boundary");
        AssertEqual(0, WeeklyCount(Payload(reset)), "weekly reward clears at boundary without mutating historical receipt");
        foreach (var pair in new[] { (Level: 5, Grade: 1), (Level: 6, Grade: 2), (Level: 7, Grade: 3), (Level: 8, Grade: 4) })
        {
            other.SimulatedBattlefield.BossLevelType = pair.Level;
            JObject weekly = Payload(DateTimeOffset.UtcNow);
            AssertEqual(pair.Grade, weekly["WeekActivityInfos"]!.Single(row => row.Value<int>("MainId") == 1003).Value<int>("SubId"),
                "boss guide uses grade rather than raw selected level type");
        }
        other.SimulatedBattlefield.BossOldLevelType = 8;
        other.SimulatedBattlefield.BossLevelType = 0;
        AssertEqual(4, Payload(DateTimeOffset.UtcNow)["WeekActivityInfos"]!
            .Single(row => row.Value<int>("MainId") == 1003).Value<int>("SubId"),
            "unchosen cage season retains previous grade for guide");
        foreach (int subtype in new[] { 1, 2 })
        {
            other.Stronghold.LevelId = subtype;
            other.Transfinite = new() { RegionId = subtype };
            JObject weekly = Payload(DateTimeOffset.UtcNow);
            AssertEqual(subtype, weekly["WeekActivityInfos"]!.Single(row => row.Value<int>("MainId") == 1004).Value<int>("SubId"),
                "Norman guide follows selected battle level");
            AssertEqual(subtype, weekly["WeekActivityInfos"]!.Single(row => row.Value<int>("MainId") == 1005).Value<int>("SubId"),
                "Clash guide follows selected power zone");
        }

        Player modePlayer = CreateDrawCompatibilityPlayer(46_812);
        using LoopbackSessionHarness modeHarness = new(CreateDrawCompatibilityCharacter(modePlayer.PlayerData.Id), modePlayer,
            CreateDrawCompatibilityInventory(modePlayer.PlayerData.Id, []), "manual-guide-mode-handlers");
        modeHarness.Session.stage = CreateLoginAccountCompatibilityStage(modePlayer.PlayerData.Id);
        int requestId = 46_820;
        (JObject Response, JObject? Guide) ModeRequest(string name, object request)
        {
            InvokeRegisteredRequestHandler(name, modeHarness.Session, ++requestId, request);
            JObject? guideUpdate = null;
            for (int i = 0; i < 32; i++)
            {
                Packet reply = modeHarness.ReadPacket("guide mode handler");
                if (reply.Type == Packet.ContentType.Push)
                {
                    Packet.Push notification = MessagePackSerializer.Deserialize<Packet.Push>(reply.Content);
                    if (notification.Name == nameof(NotifyWheelchairManualActivityUpdate))
                        guideUpdate = JObject.Parse(MessagePackSerializer.ConvertToJson(notification.Content));
                    continue;
                }
                Packet.Response response = MessagePackSerializer.Deserialize<Packet.Response>(reply.Content);
                AssertEqual(requestId, response.Id, "guide mode response correlation");
                return (JObject.Parse(MessagePackSerializer.ConvertToJson(response.Content)), guideUpdate);
            }
            throw new InvalidDataException("Guide mode handler did not respond.");
        }
        var levels = TableReaderV2.Parse<AscNet.Table.V2.share.fuben.stronghold.StrongholdLevelTable>();
        var achievement = TableReaderV2.Parse<AscNet.Table.V2.share.fuben.stronghold.StrongholdRewardTable>()
            .Select(row => (Row: row, Condition: TableReaderV2.Parse<AscNet.Table.V2.share.condition.ConditionTable>()
                .Single(condition => condition.Id == row.Condition)))
            .First(pair => pair.Condition.Type == 10131);
        var level = levels.Single(row => row.Id == achievement.Row.LevelId);
        var normanDisplay = TableReaderV2.Parse<WheelchairManualGuideWeekRewardTable>()
            .Single(row => row.MainId == 1004 && row.SubId == level.Id);
        var normanReward = TableReaderV2.Parse<RewardTable>().Single(row => row.Id == achievement.Row.RewardId);
        var normanGoods = TableReaderV2.Parse<RewardGoodsTable>()
            .Where(row => normanReward.SubIds.Contains(row.Id)).ToList();
        AssertEqual(true, normanGoods.Any(row => normanDisplay.MainTemplateId.Contains(row.TemplateId)),
            "chosen Norman achievement includes a configured guide reward");
        string RewardSignature(IEnumerable<(int TemplateId, int Count)> rewards) => string.Join(";",
            rewards.GroupBy(row => row.TemplateId).OrderBy(group => group.Key)
                .Select(group => $"{group.Key}:{group.Sum(row => row.Count)}"));
        modePlayer.PlayerData.Level = checked((uint)level.MinLevel);
        var selected = ModeRequest(nameof(SelectStrongholdLevelRequest), new SelectStrongholdLevelRequest { LevelId = level.Id });
        AssertEqual(0, selected.Response.Value<int>("Code"), "real Norman selection succeeds");
        AssertEqual(level.Id, selected.Guide!["UpdateWeekActivityInfos"]!
            .Single(row => row.Value<int>("MainId") == 1004).Value<int>("SubId"), "Norman selection pushes selected guide subtype");
        modePlayer.Stronghold.FinishGroupIds.Add(achievement.Condition.Params[0]);
        var claimed = ModeRequest(nameof(GetStrongholdRewardRequest), new GetStrongholdRewardRequest { Ids = [achievement.Row.Id] });
        AssertEqual(0, claimed.Response.Value<int>("Code"), "real Norman achievement claim succeeds");
        AssertEqual(RewardSignature(normanGoods.Select(row => (row.TemplateId, row.Count))),
            RewardSignature(claimed.Response["RewardGoodsList"]!
                .Select(row => (row.Value<int>("TemplateId"), row.Value<int>("Count")))),
            "real Norman achievement grants table-defined goods");
        var normanReceipt = modePlayer.WheelchairManualGuideRewardReceipts
            .Single(row => row.ClaimKey == $"stronghold:{modePlayer.PlayerData.Id}:{modePlayer.Stronghold.ActivityId}:{modePlayer.Stronghold.BeginTime}:achievement:{achievement.Row.Id}");
        AssertEqual(true, normanDisplay.EventCause.Contains(normanReceipt.EventCause),
            "real Norman achievement records the configured producer");
        AssertEqual(RewardSignature(normanGoods.Select(row => (row.TemplateId, row.Count))),
            RewardSignature(normanReceipt.Goods.Select(row => (row.TemplateId, row.Count))),
            "real Norman receipt retains all granted goods");
        AssertEqual(RewardSignature(normanGoods.Where(row => normanDisplay.MainTemplateId.Contains(row.TemplateId))
                .Select(row => (row.TemplateId, row.Count))),
            RewardSignature(claimed.Guide!["UpdateWeekActivityInfos"]!
                .Single(row => row.Value<int>("MainId") == 1004)["GotRewards"]!
                .Select(row => (row.Value<int>("TemplateId"), row.Value<int>("Count")))),
            "real achievement updates configured guide rewards without counting other goods");
        var duplicate = ModeRequest(nameof(GetStrongholdRewardRequest), new GetStrongholdRewardRequest { Ids = [achievement.Row.Id] });
        AssertEqual(true, duplicate.Response.Value<int>("Code") != 0, "duplicate Norman claim rejected");
        AssertEqual(null, duplicate.Guide, "rejected achievement emits no acquired-progress update");

        Type transfinite = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.TransfiniteModule");
        RequiredMethod(transfinite, "PrepareLogin", BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(AscNet.GameServer.Session), typeof(long)])
            .Invoke(null, [modeHarness.Session, DateTimeOffset.UtcNow.ToUnixTimeSeconds()]);
        TransfiniteState clash = modePlayer.Transfinite ?? throw new InvalidDataException("Current Clash mode is unavailable.");
        var scoreGroup = TableReaderV2.Parse<AscNet.Table.V2.share.fuben.transfinite.TransfiniteScoreRewardGroupTable>()
            .Single(row => row.RegionId == clash.RegionId && row.ScoreRewardGroupId == clash.ScoreRewardGroupId);
        var display = TableReaderV2.Parse<WheelchairManualGuideWeekRewardTable>()
            .Single(row => row.MainId == 1005 && row.SubId == clash.RegionId);
        var rewards = TableReaderV2.Parse<RewardTable>().ToDictionary(row => row.Id);
        var rewardGoods = TableReaderV2.Parse<RewardGoodsTable>().ToDictionary(row => row.Id);
        var scoreRewards = Enumerable.Range(0, Math.Min(scoreGroup.Score.Count, scoreGroup.RewardId.Count))
            .Where(i => scoreGroup.RewardId[i] > 0)
            .Select(i => (Index: i, Goods: rewards[scoreGroup.RewardId[i]].SubIds
                .Select(id => rewardGoods[id]).ToList())).ToList();
        var featured = scoreRewards.First(row => row.Goods.Any(good => display.MainTemplateId.Contains(good.TemplateId)));
        var nonfeatured = scoreRewards.First(row => row.Goods.All(good => !display.MainTemplateId.Contains(good.TemplateId)));
        int[] rewardIndices = [featured.Index, nonfeatured.Index];
        var clashGoods = featured.Goods.Concat(nonfeatured.Goods).ToList();
        modeHarness.Session.inventory.Do(105, rewardIndices.Max(i => scoreGroup.Score[i]));
        var scoreClaim = ModeRequest(nameof(TransfiniteGetScoreRewardRequest),
            new TransfiniteGetScoreRewardRequest { ScoreRewardIndex = rewardIndices.ToList() });
        AssertEqual(0, scoreClaim.Response.Value<int>("Code"), "real Clash score reward claim succeeds");
        AssertEqual(RewardSignature(clashGoods.Select(row => (row.TemplateId, row.Count))),
            RewardSignature(scoreClaim.Response["RewardGoodsList"]!
                .Select(row => (row.Value<int>("TemplateId"), row.Value<int>("Count")))),
            "real Clash score claim grants all table-defined goods");
        foreach (var scoreReward in new[] { featured, nonfeatured })
        {
            var receipt = modePlayer.WheelchairManualGuideRewardReceipts.Single(row =>
                row.ClaimKey == $"transfinite-score:{clash.ActivityId}:{clash.CircleId}:{scoreReward.Index}");
            AssertEqual(true, display.EventCause.Contains(receipt.EventCause),
                "real Clash score claim records the configured producer");
            AssertEqual(RewardSignature(scoreReward.Goods.Select(row => (row.TemplateId, row.Count))),
                RewardSignature(receipt.Goods.Select(row => (row.TemplateId, row.Count))),
                "real Clash score receipt retains all granted goods");
        }
        AssertEqual(RewardSignature(clashGoods.Where(row => display.MainTemplateId.Contains(row.TemplateId))
                .Select(row => (row.TemplateId, row.Count))),
            RewardSignature(scoreClaim.Guide!["UpdateWeekActivityInfos"]!
                .Single(row => row.Value<int>("MainId") == 1005)["GotRewards"]!
                .Select(row => (row.Value<int>("TemplateId"), row.Value<int>("Count")))),
            "real Clash score claim counts featured guide rewards and excludes other goods");
    }
}
