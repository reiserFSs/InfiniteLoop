using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Game;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.skytower;
using AscNet.Table.V2.share.task;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Newtonsoft.Json.Linq;
using SkyTowerConditionRow = AscNet.Table.V2.share.condition.ConditionTable;

namespace AscNet.Test;

[MessagePackObject(true)]
internal sealed class SkyTowerCodeProbe { public int Code { get; set; } }

internal partial class Program
{
    private static readonly string[] SkyTowerRequests =
    [
        nameof(SkyTowerActivityDataRequest), nameof(SkyTowerSettleToNowRequest), nameof(SkyTowerExitRequest), nameof(SkyTowerPerformDataRequest),
        nameof(SkyTowerSavePerformStoryRequest), nameof(SkyTowerFinishPerformRequest), nameof(SkyTowerFloorUpLevelRequest),
        nameof(SkyTowerCharacterUpLevelRequest), nameof(SkyTowerWorkPosAssignRequest), nameof(SkyTowerWorkPosSwapRequest),
        nameof(SkyTowerWorkPosSetProductRequest), nameof(SkyTowerWorkPosStopRequest), nameof(SkyTowerUseSpeedUpItemRequest)
    ];

    /// <summary>Dreamhouse Tycoon: table/schedule coverage, every request and push, the production engine, tasks and persistence.</summary>
    private static void ValidateSkyTowerCompatibility()
    {
        PacketFactory.LoadPacketHandlers();
        foreach (string name in SkyTowerRequests) _ = GetRegisteredRequestHandlerMethod(name);

        // ---- authoritative tables and the missing-calendar window ----
        SkyTowerActivityTable activity = TableReaderV2.Parse<SkyTowerActivityTable>().Single();
        AssertEqual(51011, activity.TimeId, "SkyTowerActivity TimeId");
        AssertEqual(true, activity.TimeLimitTaskIds.SequenceEqual([784, 787]), "SkyTowerActivity task groups");
        AssertEqual(true, ActivityScheduleService.TryGet(51011, out ActivityScheduleEntry window)
            && window.IsOpen(DateTimeOffset.UtcNow) && window.Source.StartsWith("local-policy:missing-calendar:unbounded-calendar:SkyTowerActivity.TimeId", StringComparison.Ordinal),
            "Dreamhouse Tycoon window is a labelled missing-calendar policy entry");
        foreach (TaskTimeLimitTable group in TableReaderV2.Parse<TaskTimeLimitTable>().Where(row => activity.TimeLimitTaskIds.Contains(row.Id)))
            AssertEqual(true, ActivityScheduleService.IsOpen(group.TimeId!.Value, DateTimeOffset.UtcNow), $"TaskTimeLimit {group.Id} window is open");

        Dictionary<int, SkyTowerLvTable> levels = TableReaderV2.Parse<SkyTowerLvTable>().ToDictionary(row => row.Lv);
        Dictionary<int, SkyTowerFoodTable> foods = TableReaderV2.Parse<SkyTowerFoodTable>().ToDictionary(row => row.Id);
        Dictionary<int, SkyTowerFloorTable> floors = TableReaderV2.Parse<SkyTowerFloorTable>().ToDictionary(row => row.Id);
        Dictionary<int, SkyTowerPerformTable> performs = TableReaderV2.Parse<SkyTowerPerformTable>().ToDictionary(row => row.Id);
        Dictionary<int, SkyTowerCharacterLvTable> characterLvs = TableReaderV2.Parse<SkyTowerCharacterLvTable>().ToDictionary(row => row.Id);
        Dictionary<int, SkyTowerCharacterSkillTable> skills = TableReaderV2.Parse<SkyTowerCharacterSkillTable>().ToDictionary(row => row.Id);
        int Config(string key) => TableReaderV2.Parse<SkyTowerConfigTable>().Single(row => row.Key == key).Values[0];
        // Every perform task and activity task resolves to a client task type; every unlock condition is one the engine can evaluate.
        HashSet<int> expectedTasks = performs.Values.SelectMany(row => row.TaskIds)
            .Concat(TableReaderV2.Parse<TaskTimeLimitTable>().Where(row => activity.TimeLimitTaskIds.Contains(row.Id)).SelectMany(row => row.TaskId.Concat(row.DayTaskId))).ToHashSet();
        AssertEqual(true, expectedTasks.SetEquals(SkyTowerModule.Catalog.Value.Keys), "task catalog covers every perform task and TaskTimeLimit task");
        HashSet<int> conditionIds = levels.Values.Select(row => row.ConditionId).Concat(performs.Values.SelectMany(row => row.UnlockConditionIds))
            .Concat(characterLvs.Values.Select(row => row.Condition)).Where(id => id > 0).ToHashSet();
        Dictionary<int, SkyTowerConditionRow> conditionRows = TableReaderV2.Parse<SkyTowerConditionRow>().ToDictionary(row => row.Id);
        AssertEqual(true, conditionIds.All(id => conditionRows.TryGetValue(id, out SkyTowerConditionRow? row) && row.Type is >= 23501 and <= 23504), "every unlock condition is a SkyTower condition type");
        int cumulativeWork = 0;
        foreach (SkyTowerLvTable row in levels.Values.OrderBy(row => row.Lv))
        {
            cumulativeWork += row.UnlockWorkNum;
            int[] positions = SkyTowerModule.PositionsAt(row.Lv);
            AssertEqual(cumulativeWork, positions.Length, $"level {row.Lv} opens cumulative UnlockWorkNum positions");
            AssertEqual(true, positions.All(key => SkyTowerModule.ParseKey(key).Floor <= row.FloorId), $"level {row.Lv} positions stay on built floors");
        }

        const long uid = 98_700;
        DateTimeOffset origin = DateTimeOffset.UtcNow;
        TimeSpan shift = TimeSpan.Zero;
        Func<DateTimeOffset> savedClock = SkyTowerModule.Clock;
        SkyTowerModule.Clock = () => origin + shift;
        try
        {
            using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForBiancaCompatibility(out var players, out _, out _, out _);
            using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(uid), CreateDrawCompatibilityPlayer(uid),
                CreateDrawCompatibilityInventory(uid, []), "sky-tower");
            Session session = harness.Session;
            session.stage = CreateLoginAccountCompatibilityStage(uid);
            int packetId = 98_701;

            (T Response, List<(string Name, byte[] Content)> Pushes) Call<T>(string requestName, object request)
            {
                int id = packetId++;
                InvokeRegisteredRequestHandler(requestName, session, id, request);
                List<(string Name, byte[] Content)> pushes = [];
                for (int index = 0; index < 64; index++)
                {
                    Packet packet = harness.ReadPacket($"SkyTower {requestName} packet {index}");
                    if (packet.Type == Packet.ContentType.Push)
                    {
                        Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                        pushes.Add((push.Name, push.Content));
                        continue;
                    }
                    AssertEqual(id, MessagePackSerializer.Deserialize<Packet.Response>(packet.Content).Id, "SkyTower response correlation");
                    return (ReadResponsePayload<T>(packet, requestName.Replace("Request", "Response")), pushes);
                }
                throw new InvalidDataException($"Missing {requestName} response.");
            }
            SkyTowerState S() => session.player.SkyTower;
            T Push<T>(List<(string Name, byte[] Content)> pushes, string name) =>
                MessagePackSerializer.Deserialize<T>(pushes.Single(push => push.Name == name).Content);
            int Order(List<(string Name, byte[] Content)> pushes, string name) => pushes.FindIndex(push => push.Name == name);
            long At(int seconds) => origin.ToUnixTimeSeconds() + seconds;
            SkyTowerActivityDataResponse Enter() => Call<SkyTowerActivityDataResponse>(nameof(SkyTowerActivityDataRequest), new SkyTowerActivityDataRequest()).Response;
            SkyTowerDataUpdateDto Settle() => Call<SkyTowerSettleToNowResponse>(nameof(SkyTowerSettleToNowRequest), new SkyTowerSettleToNowRequest()).Response.DataUpdate!;
            int Code(string requestName, object request) => Call<SkyTowerCodeProbe>(requestName, request).Response.Code;
            SkyTowerWorkPosSetProductResponse SetProduct(int pos, int workType, int productId) => Call<SkyTowerWorkPosSetProductResponse>(
                nameof(SkyTowerWorkPosSetProductRequest), new SkyTowerWorkPosSetProductRequest
                { WorkPosId = pos, WorkType = workType, ProductType = workType == 1 ? 1 : 2, ProductId = productId }).Response;
            (SkyTowerWorkPosAssignResponse Response, List<(string Name, byte[] Content)> Pushes) Assign(int character, int pos) =>
                Call<SkyTowerWorkPosAssignResponse>(nameof(SkyTowerWorkPosAssignRequest), new SkyTowerWorkPosAssignRequest { CharacterId = character, WorkPosId = pos });
            Dictionary<int, long> Bag() => S().Storage.ToDictionary(entry => entry.Key, entry => entry.Value);
            long Stock(int group, int type, int id) => S().Storage.GetValueOrDefault(group * 10000 + type * 100 + id);

            // ---- closed below the FunctionalOpen 10514 gate (Commandant Lv.40): every request answers NotOpen ----
            session.player.PlayerData.Level = 39;
            AssertEqual(true, SkyTowerModule.Prepare(session) is null, "mode closed below Lv.40");
            foreach ((string name, object request) in new (string, object)[]
            {
                (nameof(SkyTowerActivityDataRequest), new SkyTowerActivityDataRequest()), (nameof(SkyTowerSettleToNowRequest), new SkyTowerSettleToNowRequest()),
                (nameof(SkyTowerExitRequest), new SkyTowerExitRequest()), (nameof(SkyTowerPerformDataRequest), new SkyTowerPerformDataRequest()),
                (nameof(SkyTowerSavePerformStoryRequest), new SkyTowerSavePerformStoryRequest { PerformId = 101 }),
                (nameof(SkyTowerFinishPerformRequest), new SkyTowerFinishPerformRequest { PerformId = 101 }),
                (nameof(SkyTowerFloorUpLevelRequest), new SkyTowerFloorUpLevelRequest { SkyTowerLv = 2 }),
                (nameof(SkyTowerCharacterUpLevelRequest), new SkyTowerCharacterUpLevelRequest { CharacterId = 1, CharacterLv = 2 }),
                (nameof(SkyTowerWorkPosAssignRequest), new SkyTowerWorkPosAssignRequest { CharacterId = 1, WorkPosId = 10 }),
                (nameof(SkyTowerWorkPosSwapRequest), new SkyTowerWorkPosSwapRequest { FromWorkPosId = 10, ToWorkPosId = 11 }),
                (nameof(SkyTowerWorkPosSetProductRequest), new SkyTowerWorkPosSetProductRequest { WorkPosId = 10, WorkType = 1, ProductType = 1, ProductId = 1 }),
                (nameof(SkyTowerWorkPosStopRequest), new SkyTowerWorkPosStopRequest { WorkPosId = 10 }),
                (nameof(SkyTowerUseSpeedUpItemRequest), new SkyTowerUseSpeedUpItemRequest { WorkPosId = 10, Count = 1 })
            })
                AssertEqual(20434001, Code(name, request), $"{name} while closed -> SkyTowerActivityNotOpen");
            AssertEqual(0, S().ActivityId, "closed requests do not create state");
            session.player.PlayerData.Level = 40;

            // ---- login: entrance data and the tasks the client expects before the first request ----
            SkyTowerModule.SendLoginPushes(session);
            NotifySkyTowerLoginData login = MessagePackSerializer.Deserialize<NotifySkyTowerLoginData>(
                MessagePackSerializer.Deserialize<Packet.Push>(harness.ReadPacket("SkyTower login push").Content).Content);
            AssertEqual(activity.Id, login.ActivityId, "login ActivityId");
            AssertEqual(1, login.Level, "login level");
            AssertEqual(true, login.PerformInfos.Keys.SequenceEqual([101]) && login.PerformInfos[101].State == 1 && login.PerformInfos[101].Type == 1, "login lists the level-1 daily event as running");
            List<AscNet.Common.MsgPack.NotifyTaskData.NotifyTaskDataTaskData.NotifyTaskDataTaskDataTask> loginTasks = BuildTaskData(session);
            AssertEqual(true, loginTasks.Any(task => task.Id == 140400 && task.State == 1), "running perform's task is on the login task list");
            AssertEqual(false, loginTasks.Any(task => task.Id == 140401), "not-started perform's task is absent");
            AssertEqual(true, new[] { 140444, 140464, 140465 }.All(id => loginTasks.Any(task => task.Id == id && task.State == 1)), "activity achievement and daily tasks are listed");

            // ---- activity data: initial state comes from SkyTowerConfig and the level-1 row ----
            SkyTowerActivityDataResponse entered;
            List<(string Name, byte[] Content)> enterPushes;
            (entered, enterPushes) = Call<SkyTowerActivityDataResponse>(nameof(SkyTowerActivityDataRequest), new SkyTowerActivityDataRequest());
            AssertEqual(0, entered.Code, "ActivityData code");
            SkyTowerDataDto data = entered.ActivityData!;
            AssertEqual(false, enterPushes.Any(push => push.Name == nameof(NotifyTask)), "tasks already sent at login are not repeated on entry");
            AssertEqual(1, data.SkyTowerLv, "initial level");
            AssertEqual((long)Config("InitItemCount"), data.SpeedUpItemCount, "initial dispatch orders from SkyTowerConfig.InitItemCount");
            AssertEqual(160L, data.Storages[1].StorageInfos[101].Count, "initial ingredient 1 stock (InitIngredientCount[1])");
            AssertEqual(20L, data.Storages[1].StorageInfos[102].Count, "initial ingredient 2 stock (InitIngredientCount[2])");
            AssertEqual(true, data.CharacterInfos.Keys.OrderBy(id => id).SequenceEqual(levels[1].UnlockCharacter.OrderBy(id => id)), "level-1 staff unlocked");
            AssertEqual(true, data.WorkPosInfos.Keys.OrderBy(id => id).SequenceEqual(SkyTowerModule.PositionsAt(1).OrderBy(id => id)) && data.WorkPosInfos.Values.All(pos => pos.State == 3 && pos.WorkType == 0),
                "level-1 work positions exist, empty and stopped");
            AssertEqual(origin.ToUnixTimeSeconds(), data.LastSettleTime, "settle clock starts at entry");
            AssertEqual(0L, entered.OfflineBill, "no offline bill on first entry");
            JObject wire = JObject.Parse(MessagePackSerializer.ConvertToJson(MessagePackSerializer.Serialize(entered)));
            foreach (string key in new[] { "ActivityId", "SkyTowerLv", "GoldItemCount", "CharacterLvUpItemCount", "SpeedUpItemCount", "SpeedUpDailyUsedCount", "SpeedUpGrantedDays",
                "TotalGoldProduced", "TotalTransCharacterLvUpItem", "LastSettleTime", "WorkPosInfos", "Storages", "CharacterInfos", "PerformInfos" })
                AssertEqual(true, wire["ActivityData"]![key] is not null, $"ActivityData wire carries {key}");
            AssertEqual(true, new[] { "WorkType", "ProductType", "ProductId", "CharacterId", "State", "WorkedSeconds", "ProduceRemainder", "ReservedInputs", "ReservedExtraProduceRatio", "HasStartedRound" }
                .All(key => wire["ActivityData"]!["WorkPosInfos"]!["10"]![key] is not null), "work position wire carries every field the client reads");

            // ---- work positions: validation boundaries ----
            AssertEqual(20434015, SetProduct(999, 1, 1).Code, "unknown position -> WorkPosNotUnlock");
            AssertEqual(20434004, Call<SkyTowerWorkPosSetProductResponse>(nameof(SkyTowerWorkPosSetProductRequest), new SkyTowerWorkPosSetProductRequest { WorkPosId = 10, WorkType = 2, ProductType = 1, ProductId = 1 }).Response.Code,
                "product type must match the work type");
            AssertEqual(20434019, SetProduct(10, 2, 6).Code, "locked dish -> SkyTowerConditionNotMet");
            AssertEqual(20434019, SetProduct(10, 2, 3).Code, "dish 3 unlocks at level 6 -> SkyTowerConditionNotMet");
            AssertEqual(20434010, Assign(5, 10).Response.Code, "staff not unlocked yet");
            AssertEqual(20434016, Call<SkyTowerWorkPosStopResponse>(nameof(SkyTowerWorkPosStopRequest), new SkyTowerWorkPosStopRequest { WorkPosId = 11 }).Response.Code, "stopping an empty position -> WorkPosNoCharacter");
            AssertEqual(20434004, Assign(43, 11).Response.Code, "assigning staff to a position without work -> SkyTowerInvalidRequest");
            AssertEqual(20434021, Call<SkyTowerFinishPerformResponse>(nameof(SkyTowerFinishPerformRequest), new SkyTowerFinishPerformRequest { PerformId = 101 }).Response.Code, "perform objective not achieved");
            AssertEqual(20434020, Call<SkyTowerFinishPerformResponse>(nameof(SkyTowerFinishPerformRequest), new SkyTowerFinishPerformRequest { PerformId = 103 }).Response.Code, "perform that never started");

            // ---- scenario A: a universal staff member on a dish position and a sale position (no skill additions) ----
            AssertEqual(0, SetProduct(10, 3, 1).Code, "set sale product");
            AssertEqual(0, SetProduct(11, 2, 1).Code, "set cooking product");
            (SkyTowerWorkPosAssignResponse assignedSale, _) = Assign(43, 10);
            AssertEqual(0, assignedSale.Code, "assign universal staff to the sale position");
            AssertEqual(2, assignedSale.DataUpdate!.ChangedWorkPosInfos[10].ProductType, "sale position works a Food product type");
            (SkyTowerWorkPosAssignResponse assignedCook, List<(string Name, byte[] Content)> assignPushes) = Assign(43, 11);
            AssertEqual(0, assignedCook.Code, "a universal staff member may hold several positions");
            AssertEqual(0, assignedCook.DataUpdate!.ChangedWorkPosInfos[11].State, "cooking position runs");
            AssertEqual(1, assignedCook.DataUpdate.ChangedWorkPosInfos[11].ReservedInputs.Count, "round start reserves the recipe at once");
            AssertEqual(159L, assignedCook.DataUpdate.ChangedStorages[1].StorageInfos[101].Count, "recipe ingredient taken from storage");
            _ = assignPushes;
            // The same layout reproduced from the tables: food 1 cooks in ProduceNeedTime, sells OnceSellNum at a time for Price.
            SkyTowerFoodTable dish = foods[1];
            AssertEqual(true, dish.ProduceNeedTime == 20 && dish.SaleNeedTime == 20 && dish.OnceSellNum == 4 && dish.Price == 3 && dish.ConsumeIngredientIds[0] == 1, "test layout matches the tables");
            shift = TimeSpan.FromSeconds(100);
            SkyTowerDataUpdateDto settled = Settle();
            // Cooks complete at 20..100 (5), sales start at 20..100 and complete at 40..100 (4 x 1 dish x price).
            AssertEqual(4L * dish.Price, settled.GoldItemCount, "gold from four completed sales");
            AssertEqual(4L * dish.Price, settled.TotalGoldProduced, "sales feed TotalGoldProduced");
            AssertEqual(154L, settled.ChangedStorages[1].StorageInfos[101].Count, "six rounds started -> six ingredients spent");
            AssertEqual(0L, settled.ChangedStorages[1].StorageInfos[201].Count, "every cooked dish went to the sale position");
            AssertEqual(true, settled.ChangedWorkPosInfos[10].ReservedInputs.Single() is { ProductType: 2, ProductId: 1, Count: 1 }, "sale position holds its fifth dish");
            AssertEqual(100L, S().LastSettleTime - origin.ToUnixTimeSeconds(), "settle advances the clock");
            AssertEqual((double)dish.Price * dish.OnceSellNum * 3600 / dish.SaleNeedTime, SkyTowerModule.GoldSpeed(S(), 1, new SkyTowerModule.Plan(S())), "gold per hour of the group");

            // ---- perform 101 (Craft food 1 x1): tasks, reward in SkyTower items, claim once ----
            List<(string Name, byte[] Content)> finishPushes;
            SkyTowerFinishPerformResponse finished;
            (finished, finishPushes) = Call<SkyTowerFinishPerformResponse>(nameof(SkyTowerFinishPerformRequest), new SkyTowerFinishPerformRequest { PerformId = 101 });
            AssertEqual(0, finished.Code, "finish perform 101");
            AssertEqual(2, finished.PerformInfo!.State, "perform 101 finished");
            AssertEqual(true, finished.RewardGoodsList.Single() is { TemplateId: 38000001, RewardType: 39 } good && good.Count == 100, "perform reward is the authored SkyTower gold");
            NotifySkyTowerData rewardPush = Push<NotifySkyTowerData>(finishPushes, nameof(NotifySkyTowerData));
            AssertEqual(settled.GoldItemCount + 100, rewardPush.ActivityData.GoldItemCount, "reward credited to the SkyTower balance and pushed");
            AssertEqual(true, Order(finishPushes, nameof(NotifyTask)) is >= 0 && Order(finishPushes, nameof(NotifyTask)) < Order(finishPushes, nameof(NotifySkyTowerData)), "task sync precedes the reward push");
            AssertEqual(true, Order(finishPushes, nameof(NotifySkyTowerData)) < Order(finishPushes, nameof(NotifySkyTowerPerform)), "reward push precedes the perform push");
            AssertEqual(settled.GoldItemCount + 100, S().Gold, "balance persisted");
            AssertEqual(20434022, Call<SkyTowerFinishPerformResponse>(nameof(SkyTowerFinishPerformRequest), new SkyTowerFinishPerformRequest { PerformId = 101 }).Response.Code, "claim once");
            AssertEqual(settled.GoldItemCount + 100, S().Gold, "repeat claim grants nothing");

            // ---- activity task: Obtain 100 Dream Funds (daily) pays dispatch orders through FinishTask, once ----
            AssertEqual(3, BuildTaskData(session).Single(task => task.Id == 140464).State, "daily gold task (100 Dream Funds) is achieved by sales plus the perform reward");
            AssertEqual(100, BuildTaskData(session).Single(task => task.Id == 140464).Schedule.Single().Value, "task value is capped at the target");
            AssertEqual(20026007, Call<FinishTaskResponse>(nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = 140454 }).Response.Code, "unearned achievement task rejected");
            long ordersBefore = S().SpeedUpItem;
            (FinishTaskResponse claimed, List<(string Name, byte[] Content)> claimPushes) = Call<FinishTaskResponse>(nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = 140464 });
            AssertEqual(0, claimed.Code, "claim the daily gold task");
            AssertEqual(true, claimed.RewardGoodsList.Single() is { TemplateId: 38000003, RewardType: 39 } orders && orders.Count == 6, "task reward is six dispatch orders (reward 69809)");
            AssertEqual(ordersBefore + 6, Push<NotifySkyTowerData>(claimPushes, nameof(NotifySkyTowerData)).ActivityData.SpeedUpItemCount, "dispatch orders pushed to the client balance");
            AssertEqual(ordersBefore + 6, S().SpeedUpItem, "dispatch orders persisted");
            AssertEqual(20026006, Call<FinishTaskResponse>(nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = 140464 }).Response.Code, "daily task claim once");
            AssertEqual(ordersBefore + 6, S().SpeedUpItem, "duplicate claim grants nothing");
            session.player = BsonSerializer.Deserialize<Player>(session.player.ToBson());
            AssertEqual(4, BuildTaskData(session).Single(task => task.Id == 140464).State, "claim survives a BSON reload");
            AssertEqual(S().Gold, settled.GoldItemCount + 100, "state survives the reload");

            // ---- speed-up orders: one position advances AccelerateTime seconds per order; unused orders are returned ----
            SkyTowerUseSpeedUpItemResponse SpeedUp(int pos, int count) => Call<SkyTowerUseSpeedUpItemResponse>(
                nameof(SkyTowerUseSpeedUpItemRequest), new SkyTowerUseSpeedUpItemRequest { WorkPosId = pos, Count = count }).Response;
            AssertEqual(20434029, SpeedUp(11, Config("AcceleratePerUseLimit") + 1).Code, "more orders than AcceleratePerUseLimit in one use");
            AssertEqual(20434004, SpeedUp(11, 0).Code, "zero orders");
            AssertEqual(20434015, SpeedUp(999, 1).Code, "unknown position");
            int perOrder = Config("AccelerateTime");
            int foodLimit = TableReaderV2.Parse<SkyTowerStorageTable>().Where(row => row.ProductType == 2 && row.ProductId == 1 && row.SkyTowerLv <= 1).Max(row => row.StorageLimit);
            const int ingredient1 = 10101; // group 1 * 10000 + Ingredient * 100 + id 1
            S().Storage[ingredient1] = 1000;
            long ordersNow = S().SpeedUpItem;
            int usedNow = S().SpeedUpDailyUsedCount;
            long foodNow = Stock(1, 2, 1);
            SkyTowerUseSpeedUpItemResponse fast = SpeedUp(11, 2);
            AssertEqual(0, fast.Code, "speed up the cooking position with two orders");
            long fastRounds = 2L * perOrder / dish.ProduceNeedTime;
            AssertEqual(Math.Min(fastRounds, foodLimit - foodNow), fast.ProduceCount, "orders advance 2 x AccelerateTime seconds of cooking");
            AssertEqual(true, fast.ProductType == 2 && fast.ProductId == 1 && fast.GoldCount == 0, "cooking speed-up reports the dish, no gold");
            AssertEqual(ordersNow - 2, S().SpeedUpItem, "two orders spent");
            AssertEqual(usedNow + 2, fast.SpeedUpDailyUsedCount, "daily used count");
            AssertEqual(1000L - fastRounds, Bag()[ingredient1], "one ingredient per round");
            AssertEqual(foodNow + fastRounds, Stock(1, 2, 1), "dishes banked");
            AssertEqual(2, BuildTaskData(session).Single(task => task.Id == 140465).Schedule.Single().Value, "Use 3 Dispatch Orders counts today's orders");
            // Scarce ingredients: the position stalls inside the first order, so only that order is spent and the rest returned.
            S().Storage[ingredient1] = 30;
            SkyTowerUseSpeedUpItemResponse scarce = SpeedUp(11, 3);
            AssertEqual(0, scarce.Code, "speed up with scarce ingredients");
            AssertEqual(31L, scarce.ProduceCount, "the round in progress plus 30 started rounds complete before the stall");
            AssertEqual(ordersNow - 3, S().SpeedUpItem, "only the order that stalled is spent (two returned)");
            AssertEqual(usedNow + 3, scarce.SpeedUpDailyUsedCount, "daily used counts spent orders only");
            AssertEqual(2, scarce.DataUpdate!.ChangedWorkPosInfos[11].State, "stalled position waits for ingredients");
            AssertEqual(20434027, SpeedUp(11, 1).Code, "a position that is not in production cannot be sped up");
            long keepOrders = S().SpeedUpItem;
            S().SpeedUpItem = 0;
            AssertEqual(20434025, SpeedUp(11, 1).Code, "no dispatch orders");
            S().SpeedUpItem = keepOrders;
            int keepUsed = S().SpeedUpDailyUsedCount;
            S().SpeedUpDailyUsedCount = Config("AccelerateUseLimit");
            AssertEqual(20434026, SpeedUp(11, 1).Code, "daily limit reached");
            S().SpeedUpDailyUsedCount = keepUsed;
            S().Storage[ingredient1] = 500;
            shift = TimeSpan.FromSeconds(120);
            _ = Settle();
            AssertEqual(0, S().WorkPos[11].State, "position resumes once ingredients return");

            // ---- stop returns the reserved round; swap moves the whole slot; both are validated ----
            S().WorkPos[10].ReservedInputs = [new() { ProductType = 2, ProductId = 1, Count = 3 }];
            long foodBeforeStop = Stock(1, 2, 1);
            SkyTowerWorkPosStopResponse stopped = Call<SkyTowerWorkPosStopResponse>(nameof(SkyTowerWorkPosStopRequest), new SkyTowerWorkPosStopRequest { WorkPosId = 10 }).Response;
            AssertEqual(0, stopped.Code, "stop the sale position");
            AssertEqual(3, stopped.DataUpdate!.ChangedWorkPosInfos[10].State, "stopped state");
            AssertEqual(0, stopped.DataUpdate.ChangedWorkPosInfos[10].ReservedInputs.Count, "stop clears the round");
            AssertEqual(foodBeforeStop + 3, stopped.DataUpdate.ChangedStorages[1].StorageInfos[201].Count, "reserved dishes return to storage");
            AssertEqual(0, Assign(43, 10).Response.DataUpdate!.ChangedWorkPosInfos[10].State, "assigning again resumes work");
            AssertEqual(20434015, Call<SkyTowerWorkPosSwapResponse>(nameof(SkyTowerWorkPosSwapRequest), new SkyTowerWorkPosSwapRequest { FromWorkPosId = 10, ToWorkPosId = 999 }).Response.Code, "swap with an unknown position");
            SkyTowerWorkPosSwapResponse swapped = Call<SkyTowerWorkPosSwapResponse>(nameof(SkyTowerWorkPosSwapRequest), new SkyTowerWorkPosSwapRequest { FromWorkPosId = 10, ToWorkPosId = 11 }).Response;
            AssertEqual(0, swapped.Code, "swap two positions");
            AssertEqual(true, swapped.DataUpdate!.ChangedWorkPosInfos[10].WorkType == 2 && swapped.DataUpdate.ChangedWorkPosInfos[11].WorkType == 3, "work content moved with the slot");
            AssertEqual(true, S().WorkPos[10].WorkType == 2 && S().WorkPos[11].WorkType == 3 && S().WorkPos[10].CharacterId == 43, "swap persisted");
            AssertEqual(0, Call<SkyTowerWorkPosSwapResponse>(nameof(SkyTowerWorkPosSwapRequest), new SkyTowerWorkPosSwapRequest { FromWorkPosId = 10, ToWorkPosId = 11 }).Response.Code, "swap back");

            // ---- perform data: the server unlocks performs from level, window and conditions ----
            bool Met(int conditionId, int level, HashSet<int> finishedPerforms)
            {
                SkyTowerConditionRow row = conditionRows[conditionId];
                return row.Type switch { 23501 => levels[level].FloorId >= row.Params[0], 23502 => level >= row.Params[0], 23504 => finishedPerforms.Contains(row.Params[0]), _ => false };
            }
            HashSet<int> StartedAt(int level, HashSet<int> finishedPerforms) => levels.Where(entry => entry.Key <= level).SelectMany(entry => entry.Value.PerformIds)
                .Where(id => performs[id].UnlockConditionIds.All(condition => Met(condition, level, finishedPerforms))).ToHashSet();
            S().Level = 2;
            HashSet<int> startedLv2 = StartedAt(2, [101]);
            AssertEqual(true, startedLv2.Contains(102) && startedLv2.Contains(101), "level 2 with perform 101 finished starts perform 102");
            (SkyTowerPerformDataResponse performData, List<(string Name, byte[] Content)> performPushes) = Call<SkyTowerPerformDataResponse>(nameof(SkyTowerPerformDataRequest), new SkyTowerPerformDataRequest());
            AssertEqual(0, performData.Code, "PerformData code");
            AssertEqual(true, performData.PerformInfos.Select(info => info.PerformId).ToHashSet().SetEquals(startedLv2), "PerformData lists every perform the tables unlock");
            AssertEqual(true, performData.PerformInfos.Single(info => info.PerformId == 101).State == 2 && performData.PerformInfos.Single(info => info.PerformId == 102) is { State: 1, Type: 1 }, "running and finished states");
            AssertEqual(true, Order(performPushes, nameof(NotifyTask)) >= 0 && !performPushes.Any(push => push.Name == nameof(NotifySkyTowerPerform)),
                "new perform tasks are pushed before the response; the response itself carries the performs");
            AssertEqual(true, Push<NotifyTask>(performPushes, nameof(NotifyTask)).Tasks.Tasks.Any(task => task.Id == 140401 && task.State == 1), "perform 102's task (sell food 1 x1) starts at zero");
            S().Level = 1;
            foreach (int id in startedLv2.Where(id => id != 101)) S().Performs.Remove(id);

            // ---- perform story choices ----
            (int storyPerform, int selectTalk) = performs.Values.OrderBy(row => row.Id).Where(row => row.Id != 101)
                .Select(row => (row.Id, Talk: TableReaderV2.Parse<SkyTowerStoryTable>().Single(story => story.Id == row.StoryId).TalkIds
                    .FirstOrDefault(id => TableReaderV2.Parse<SkyTowerTalkTable>().Single(talk => talk.Id == id).Type == 2))).First(entry => entry.Talk != 0);
            SkyTowerTalkTable choice = TableReaderV2.Parse<SkyTowerTalkTable>().Single(talk => talk.Id == selectTalk);
            SkyTowerSavePerformStoryResponse SaveStory(int performId, Dictionary<int, int>? record) => Call<SkyTowerSavePerformStoryResponse>(
                nameof(SkyTowerSavePerformStoryRequest), new SkyTowerSavePerformStoryRequest { PerformId = performId, PerformStoryInfo = record }).Response;
            AssertEqual(20434020, SaveStory(storyPerform, new() { [selectTalk] = 1 }).Code, "story of a perform that never started");
            S().Performs[storyPerform] = new SkyTowerPerformState { State = 1 };
            SkyTowerSavePerformStoryResponse story = SaveStory(storyPerform, new() { [selectTalk] = choice.Answers.Count });
            AssertEqual(0, story.Code, "save a story choice");
            AssertEqual(choice.Answers.Count, story.PerformInfo!.PerformStoryInfo[selectTalk], "choice echoed in PerformInfo");
            AssertEqual(choice.Answers.Count, S().Performs[storyPerform].Story[selectTalk], "choice persisted");
            AssertEqual(20434023, SaveStory(storyPerform, new() { [selectTalk] = choice.Answers.Count + 1 }).Code, "answer beyond the talk's replies");
            AssertEqual(20434023, SaveStory(storyPerform, new() { [selectTalk] = 0 }).Code, "answers are 1-based");
            int foreignTalk = TableReaderV2.Parse<SkyTowerTalkTable>().First(talk => talk.Type == 2 && !TableReaderV2.Parse<SkyTowerStoryTable>().Single(row => row.Id == performs[storyPerform].StoryId).TalkIds.Contains(talk.Id)).Id;
            AssertEqual(20434023, SaveStory(storyPerform, new() { [foreignTalk] = 1 }).Code, "talk that is not part of this story");
            AssertEqual(20434020, SaveStory(101, new() { [203] = 1 }).Code, "finished perform takes no more choices");
            S().Performs.Remove(storyPerform);

            // ---- building level-ups: cost, per-level conditions, unlocks ----
            SkyTowerFloorUpLevelResponse FloorUp(int target, out List<(string Name, byte[] Content)> pushes)
            {
                (SkyTowerFloorUpLevelResponse response, pushes) = Call<SkyTowerFloorUpLevelResponse>(nameof(SkyTowerFloorUpLevelRequest), new SkyTowerFloorUpLevelRequest { SkyTowerLv = target });
                return response;
            }
            S().Gold = 50;
            AssertEqual(20434009, FloorUp(2, out _).Code, "not enough Dream Funds for level 2");
            S().Gold = 5000;
            AssertEqual(20434024, FloorUp(3, out _).Code, "level 3 needs perform 102 finished (condition 1022002)");
            AssertEqual(1, S().Level, "failed upgrade leaves the level");
            AssertEqual(20434004, FloorUp(1, out _).Code, "target not above the current level");
            AssertEqual(20434004, FloorUp(levels.Keys.Max() + 1, out _).Code, "target above the maximum level");
            SkyTowerFloorUpLevelResponse up2 = FloorUp(2, out List<(string Name, byte[] Content)> upPushes);
            AssertEqual(0, up2.Code, "upgrade to level 2");
            AssertEqual(5000L - levels[2].NeedGoldCount, up2.Result!.GoldItemCount, "level 2 cost paid");
            AssertEqual(true, up2.Result.NewWorkPosIds.SequenceEqual(SkyTowerModule.PositionsAt(2).Except(SkyTowerModule.PositionsAt(1))), "new work position from cumulative UnlockWorkNum");
            AssertEqual(true, up2.Result.NewPerformIds.OrderBy(id => id).SequenceEqual(levels[2].PerformIds.OrderBy(id => id)), "NewPerformIds are the level's PerformIds");
            AssertEqual(true, up2.Result.DataUpdate.ChangedWorkPosInfos.ContainsKey(up2.Result.NewWorkPosIds[0]), "new position is in the data update");
            HashSet<int> pushedPerforms = Push<NotifySkyTowerPerform>(upPushes, nameof(NotifySkyTowerPerform)).ChangedPerformInfos.Select(info => info.PerformId).ToHashSet();
            AssertEqual(true, pushedPerforms.SetEquals(StartedAt(2, [101]).Where(id => id != 101)), "newly unlocked performs are pushed");
            AssertEqual(true, Order(upPushes, nameof(NotifyTask)) is >= 0 && Order(upPushes, nameof(NotifyTask)) < Order(upPushes, nameof(NotifySkyTowerPerform)), "perform tasks reach the client before the perform");
            S().Performs[102].State = 2;
            S().Gold = 5000;
            SkyTowerFloorUpLevelResponse up4 = FloorUp(4, out List<(string Name, byte[] Content)> up4Pushes);
            AssertEqual(0, up4.Code, "upgrade 2 -> 4 in one request");
            AssertEqual(5000L - levels[3].NeedGoldCount - levels[4].NeedGoldCount, up4.Result!.GoldItemCount, "both levels paid");
            AssertEqual(true, up4.Result.NewWorkPosIds.SequenceEqual(SkyTowerModule.PositionsAt(4).Except(SkyTowerModule.PositionsAt(2))), "positions between the levels");
            AssertEqual(true, up4.Result.NewCharacterIds.OrderBy(id => id).SequenceEqual(levels[3].UnlockCharacter.Concat(levels[4].UnlockCharacter).OrderBy(id => id)), "staff unlocked by both levels");
            AssertEqual(true, Push<NotifySkyTowerCharacter>(up4Pushes, nameof(NotifySkyTowerCharacter)).ChangedCharacterInfos.Keys.OrderBy(id => id).SequenceEqual(up4.Result.NewCharacterIds.OrderBy(id => id)), "new staff pushed");
            int realLevel = S().Level;
            S().Level = levels.Keys.Max();
            AssertEqual(20434007, FloorUp(levels.Keys.Max(), out _).Code, "already at the highest level");
            S().Level = realLevel;

            // ---- staff: skills activate from the layout; levelling changes them ----
            int farm = SkyTowerModule.PositionsAt(4).First(key => S().WorkPos[key].WorkType == 0);
            AssertEqual(0, SetProduct(farm, 1, 1).Code, "ingredient position");
            (SkyTowerWorkPosAssignResponse ayla, List<(string Name, byte[] Content)> aylaPushes) = Assign(1, farm);
            AssertEqual(0, ayla.Code, "assign Ayla to the ingredient position");
            SkyTowerCharacterInfoDto aylaInfo = Push<NotifySkyTowerCharacter>(aylaPushes, nameof(NotifySkyTowerCharacter)).ChangedCharacterInfos[1];
            AssertEqual(true, aylaInfo.ActiveSkillRatios.Count == 1 && aylaInfo.ActiveSkillRatios[1101] == 10000, "Order skill 1101 is active at full ratio");
            shift = TimeSpan.FromSeconds(180);
            SkyTowerDataUpdateDto farmed = Settle();
            // +50% output on a 20 s cycle: rounds at 20/40/60 give 1, 2, 1 with 5000 carried.
            AssertEqual(5000L, farmed.ChangedWorkPosInfos[farm].ProduceRemainder, "skill output ratio carries its remainder");
            long itemCost = characterLvs[102].NeedItemCount;
            S().CharacterLvUpItem = itemCost + 7;
            SkyTowerCharacterUpLevelResponse levelled;
            List<(string Name, byte[] Content)> levelPushes;
            (levelled, levelPushes) = Call<SkyTowerCharacterUpLevelResponse>(nameof(SkyTowerCharacterUpLevelRequest), new SkyTowerCharacterUpLevelRequest { CharacterId = 1, CharacterLv = 2 });
            AssertEqual(0, levelled.Code, "level Ayla to 2");
            AssertEqual(7L, levelled.DataUpdate!.CharacterLvUpItemCount, "promotion merit cost from SkyTowerCharacterLv");
            AssertEqual(true, Push<NotifySkyTowerCharacter>(levelPushes, nameof(NotifySkyTowerCharacter)).ChangedCharacterInfos[1].ActiveSkillRatios.Keys.SequenceEqual(characterLvs[102].SkillId), "level 2 skill replaces level 1 skill");
            S().CharacterLvUpItem = 0;
            AssertEqual(true, characterLvs[202].NeedItemCount > 0, "staff 2 level 2 costs promotion merit");
            AssertEqual(20434013, Call<SkyTowerCharacterUpLevelResponse>(nameof(SkyTowerCharacterUpLevelRequest), new SkyTowerCharacterUpLevelRequest { CharacterId = 2, CharacterLv = 2 }).Response.Code, "not enough promotion merit");
            AssertEqual(20434010, Call<SkyTowerCharacterUpLevelResponse>(nameof(SkyTowerCharacterUpLevelRequest), new SkyTowerCharacterUpLevelRequest { CharacterId = 20, CharacterLv = 2 }).Response.Code, "staff not unlocked");
            AssertEqual(20434012, Call<SkyTowerCharacterUpLevelResponse>(nameof(SkyTowerCharacterUpLevelRequest), new SkyTowerCharacterUpLevelRequest { CharacterId = 43, CharacterLv = 2 }).Response.Code, "universal staff cannot level");
            AssertEqual(20434004, Call<SkyTowerCharacterUpLevelResponse>(nameof(SkyTowerCharacterUpLevelRequest), new SkyTowerCharacterUpLevelRequest { CharacterId = 1, CharacterLv = 2 }).Response.Code, "target not above the current level");
            S().Characters[2] = SkyTowerModule.CharacterMaxLevel(2);
            AssertEqual(20434011, Call<SkyTowerCharacterUpLevelResponse>(nameof(SkyTowerCharacterUpLevelRequest), new SkyTowerCharacterUpLevelRequest { CharacterId = 2, CharacterLv = 2 }).Response.Code, "already at the staff maximum");
            S().Characters[2] = 1;

            // ---- offline bill: gap >= OfflineBillTime hours, capped by MaxSettleTime and OfflineBillLimit + OfflineBillAdd ----
            foreach (int key in S().WorkPos.Keys.ToList()) S().WorkPos[key] = new() { State = 3 };
            S().WorkPos[10] = new() { WorkType = 3, ProductId = 1, CharacterId = 43, State = 0 };
            S().Storage[10201] = 1_000_000; // group 1, Food, dish 1
            long perRound = (long)dish.OnceSellNum * dish.Price;
            long billCap = Config("OfflineBillLimit") + levels[S().Level].OfflineBillAdd;
            SkyTowerActivityDataResponse Away(long seconds)
            {
                S().LastSettleTime = At((int)shift.TotalSeconds) - seconds;
                return Enter();
            }
            shift += TimeSpan.FromSeconds(1);
            long goldBefore = S().Gold;
            SkyTowerActivityDataResponse shortAway = Away(1800);
            AssertEqual(0L, shortAway.OfflineBill, "under OfflineBillTime hours shows no bill");
            AssertEqual(goldBefore + 1800 / dish.SaleNeedTime * perRound, S().Gold, "short absence still earns");
            long totalBefore = S().TotalGoldProduced, tradedBefore = S().TotalTransCharacterLvUpItem;
            SkyTowerActivityDataResponse twoHours = Away(7200);
            AssertEqual(7200 / dish.SaleNeedTime * perRound, twoHours.OfflineBill, "two hours away under the cap");
            AssertEqual(7200L, twoHours.OfflineTime, "offline time is the gap");
            AssertEqual(S().TotalTransCharacterLvUpItem - tradedBefore, twoHours.OfflineCharacterLvUpItemCount, "bill reports the staff tickets earned");
            _ = totalBefore;
            long maxWindow = Config("MaxSettleTime");
            SkyTowerActivityDataResponse longAway = Away(maxWindow + 36000);
            AssertEqual(true, maxWindow / dish.SaleNeedTime * perRound > billCap, "test gap would exceed the cap");
            AssertEqual(billCap, longAway.OfflineBill, "bill capped at OfflineBillLimit + OfflineBillAdd");
            AssertEqual(maxWindow + 36000, longAway.OfflineTime, "gap beyond MaxSettleTime is reported");

            // ---- persistence and closing the window ----
            string stateJson = S().ToJson();
            session.player = BsonSerializer.Deserialize<Player>(session.player.ToBson());
            AssertEqual(stateJson, S().ToJson(), "SkyTower state survives a BSON round trip");
            ActivityScheduleEntry[] schedules = (ActivityScheduleEntry[])ActivityScheduleService.All;
            int slot = Array.FindIndex(schedules, row => row.Id == 51011);
            ActivityScheduleEntry original = schedules[slot];
            try
            {
                schedules[slot] = new ActivityScheduleEntry(original.Id, 1, 2, "synthetic-test:skytower-closed");
                AssertEqual(20434001, Code(nameof(SkyTowerActivityDataRequest), new SkyTowerActivityDataRequest()), "closed window answers NotOpen");
                AssertEqual(false, BuildTaskData(session).Any(row => row.Id is 140400 or 140444), "closed window hides the tasks");
            }
            finally { schedules[slot] = original; }

            // ---- daily reset: orders granted, used count and daily task reopen ----
            ordersNow = S().SpeedUpItem;
            int grantedDays = S().SpeedUpGrantedDays;
            shift += TimeSpan.FromDays(1);
            List<(string Name, byte[] Content)> resetPushes = Call<SkyTowerSettleToNowResponse>(nameof(SkyTowerSettleToNowRequest), new SkyTowerSettleToNowRequest()).Pushes;
            NotifySkyTowerSpeedUpDailyReset reset = Push<NotifySkyTowerSpeedUpDailyReset>(resetPushes, nameof(NotifySkyTowerSpeedUpDailyReset));
            AssertEqual(0, reset.SpeedUpDailyUsedCount, "used count cleared");
            AssertEqual(ordersNow + Config("AccelerateDailyNum"), reset.SpeedUpItemCount, "AccelerateDailyNum orders granted");
            AssertEqual(grantedDays + 1, S().SpeedUpGrantedDays, "granted days counted");
            AssertEqual(true, BuildTaskData(session).Single(task => task.Id == 140464).State != 4, "claimed daily task reopens");
            AssertEqual(true, BuildTaskData(session).Single(task => task.Id == 140444).State == 1, "achievement task untouched by the reset");

            ValidateSkyTowerEngine();
        }
        finally
        {
            SkyTowerModule.Clock = savedClock;
        }
    }

    /// <summary>The production engine against the authored skill families, on synthetic layouts (every number comes from the tables).</summary>
    private static void ValidateSkyTowerEngine()
    {
        Dictionary<int, SkyTowerCharacterSkillTable> skills = TableReaderV2.Parse<SkyTowerCharacterSkillTable>().ToDictionary(row => row.Id);
        List<SkyTowerCharacterLvTable> lvRows = TableReaderV2.Parse<SkyTowerCharacterLvTable>();
        Dictionary<int, SkyTowerFoodTable> foods = TableReaderV2.Parse<SkyTowerFoodTable>().ToDictionary(row => row.Id);
        Dictionary<int, SkyTowerIngredientTable> ingredients = TableReaderV2.Parse<SkyTowerIngredientTable>().ToDictionary(row => row.Id);
        int minWork = TableReaderV2.Parse<SkyTowerConfigTable>().Single(row => row.Key == "MinWorkTime").Values[0];
        SkyTowerState Fresh() => new() { ActivityId = 1, Level = 55, WorkPos = SkyTowerModule.PositionsAt(55).ToDictionary(key => key, _ => new SkyTowerWorkPosState { State = 3 }) };
        void Place(SkyTowerState s, int floor, int index, int workType, int product, int character, int level = 1)
        {
            s.WorkPos[SkyTowerModule.WorkPosKey(floor, index)] = new() { WorkType = workType, ProductId = product, CharacterId = character, State = 0 };
            s.Characters[character] = level;
        }
        (int Character, int Level) Owner(int skillId) { SkyTowerCharacterLvTable row = lvRows.First(entry => entry.SkillId.Contains(skillId)); return (row.CharacterId, row.Lv); }
        SkyTowerCharacterSkillTable Plain(int type) => skills.Values.First(skill => skill.SkillAdditionType.SequenceEqual([type]) && skill.ConditionType == 0 && skill.ShareType.Count == 0 && skill.ProductId.Count == 1);
        int Key(int floor, int index) => SkyTowerModule.WorkPosKey(floor, index);

        // ReduceTime shortens the cycle by the authored seconds but never below MinWorkTime.
        SkyTowerCharacterSkillTable reduce = Plain(1);
        (int reducer, int reducerLevel) = Owner(reduce.Id);
        SkyTowerState state = Fresh();
        Place(state, 7, 1, reduce.SectionType, reduce.ProductId[0], reducer, reducerLevel);
        int baseCycle = reduce.SectionType == 1 ? ingredients[reduce.ProductId[0]].ProduceNeedTime : foods[reduce.ProductId[0]].ProduceNeedTime;
        AssertEqual((long)Math.Max(baseCycle - reduce.SkillAddition[0], minWork), new SkyTowerModule.Plan(state).Cycle(Key(7, 1), state.WorkPos[Key(7, 1)]), "ReduceTime skill shortens the cycle");
        Place(state, 7, 1, reduce.SectionType, reduce.ProductId[0], 43);
        AssertEqual((long)baseCycle, new SkyTowerModule.Plan(state).Cycle(Key(7, 1), state.WorkPos[Key(7, 1)]), "no skill, base cycle");

        // UpPrice and UpSaleCount raise the sale batch.
        SkyTowerCharacterSkillTable price = Plain(3), count = Plain(5);
        (int priceStaff, int priceLevel) = Owner(price.Id);
        (int countStaff, int countLevel) = Owner(count.Id);
        foreach ((SkyTowerCharacterSkillTable skill, int staff, int staffLevel, long extraPrice, long extraCount) in new[] { (price, priceStaff, priceLevel, (long)price.SkillAddition[0], 0L), (count, countStaff, countLevel, 0L, (long)count.SkillAddition[0]) })
        {
            SkyTowerFoodTable food = foods[skill.ProductId[0]];
            state = Fresh();
            Place(state, 2, 1, 3, food.Id, staff, staffLevel);
            state.Storage[SkyTowerModule.StorageKey(1, 2, food.Id)] = 10_000;
            SkyTowerModule.SimResult sim = SkyTowerModule.Advance(state, 0, food.SaleNeedTime);
            AssertEqual((food.OnceSellNum + extraCount) * (food.Price + extraPrice), sim.Gold, $"sale skill {skill.Id}: batch x price from the table");
        }

        // ExtraProduce: pays the extra recipe and doubles the yield for the authored ratio.
        state = Fresh();
        Place(state, 1, 1, 2, 1, 2);
        state.Storage[SkyTowerModule.StorageKey(1, 1, 1)] = 100;
        SkyTowerModule.Advance(state, 0, foods[1].ProduceNeedTime);
        AssertEqual(96L, state.Storage[SkyTowerModule.StorageKey(1, 1, 1)], "two rounds started, each paying a base and an extra ingredient");
        AssertEqual(2L, state.Storage[SkyTowerModule.StorageKey(1, 2, 1)], "+100% yield");
        state = Fresh();
        Place(state, 1, 1, 2, 1, 43);
        state.Storage[SkyTowerModule.StorageKey(1, 1, 1)] = 100;
        SkyTowerModule.Advance(state, 0, foods[1].ProduceNeedTime);
        AssertEqual(98L, state.Storage[SkyTowerModule.StorageKey(1, 1, 1)], "two rounds started, base recipe only");
        AssertEqual(1L, state.Storage[SkyTowerModule.StorageKey(1, 2, 1)], "base yield");

        // Conditional skills: WorkPosition (12101), Nearby (14101), OtherActive (10101), and an aura (15101).
        state = Fresh();
        Place(state, 7, 2, 2, 6, 12);
        AssertEqual(10000, SkyTowerModule.ComputeRatios(state)[12][12101], "WorkPosition skill active on the authored position");
        Place(state, 7, 1, 2, 6, 12);
        state.WorkPos[Key(7, 2)] = new() { State = 3 };
        AssertEqual(true, !SkyTowerModule.ComputeRatios(state)[12].ContainsKey(12101), "WorkPosition skill inactive elsewhere");
        state = Fresh();
        Place(state, 8, 1, 2, 7, 14);
        Place(state, 8, 2, 2, 7, 13);
        AssertEqual(10000, SkyTowerModule.ComputeRatios(state)[14][14101], "Nearby skill active with the named staff one position right");
        state.WorkPos[Key(8, 2)] = new() { State = 3 };
        Place(state, 8, 3, 2, 7, 13);
        AssertEqual(true, !SkyTowerModule.ComputeRatios(state)[14].ContainsKey(14101), "Nearby skill inactive two positions away");
        state = Fresh();
        Place(state, 7, 1, 1, 5, 10);
        AssertEqual(0, SkyTowerModule.ComputeRatios(state)[10][10101], "OtherActive skill is zero with no active staff in the group");
        Place(state, 7, 2, 2, 6, 12);
        AssertEqual(11107, SkyTowerModule.ComputeRatios(state)[10][10101], "OtherActive skill scales with the active staff count");
        state = Fresh();
        Place(state, 9, 1, 2, 8, 15);
        Place(state, 9, 2, 2, 8, 43);
        Dictionary<int, SkyTowerModule.Additions> adds = SkyTowerModule.ComputeAdditions(state, SkyTowerModule.ComputeRatios(state));
        AssertEqual(skills[15101].SkillAddition[0], adds[Key(9, 2)].ReduceTime, "aura reaches the position one to the right");
        AssertEqual(true, !adds.ContainsKey(Key(9, 1)) || adds[Key(9, 1)].ReduceTime == 0, "aura skips its own position (ShareType lists only 1_0)");
    }
}
