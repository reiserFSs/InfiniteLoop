using System.Reflection;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Game;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.client.wheelchairmanual;
using AscNet.Table.V2.share.fuben;
using AscNet.Table.V2.share.equip;
using AscNet.Table.V2.share.item;
using AscNet.Table.V2.share.reward;
using AscNet.Table.V2.share.task;
using AscNet.Table.V2.share.wheelchairmanual;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using ItemUseRequest = AscNet.GameServer.Handlers.ItemUseRequest;
using ItemUseResponse = AscNet.GameServer.Handlers.ItemUseResponse;

namespace AscNet.Test;

internal static partial class Program
{
    private static void ValidateWheelchairManualBattlePassCompatibility()
    {
        PacketFactory.LoadPacketHandlers();
        using MongoCollectionOverride storage = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out RecordingMongoCollectionProxy<Player> players,
            out RecordingMongoCollectionProxy<Character> characters,
            out RecordingMongoCollectionProxy<Inventory> inventories);
        var activity = TableReaderV2.Parse<WheelchairManualActivityTable>().Single();
        var manuals = TableReaderV2.Parse<WheelchairManualBattlePassManualTable>().ToDictionary(row => row.Id);
        var senior = manuals[activity.SeniorBattlePassManualId];
        var levels = TableReaderV2.Parse<WheelchairManualBattlePassLevelTable>()
            .Where(row => row.Id >= activity.BpLevelConfig[0] && row.Id <= activity.BpLevelConfig[1])
            .OrderBy(row => row.Id).ToArray();
        var rewardRows = TableReaderV2.Parse<WheelchairManualBattlePassRewardTable>();
        var rewards = TableReaderV2.Parse<RewardTable>().ToDictionary(row => row.Id);
        var goods = TableReaderV2.Parse<RewardGoodsTable>();
        Type module = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.WheelchairManualModule");
        MethodInfo builder = RequiredMethod(module, "BuildPayload", BindingFlags.Static | BindingFlags.Public,
            [typeof(Session), typeof(DateTimeOffset)]);
        MethodInfo refresh = RequiredMethod(module, "RefreshProgress", BindingFlags.Static | BindingFlags.Public, [typeof(Session)]);
        int expId = (int)module.GetProperty("ExperienceItemId", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        int costId = Convert.ToInt32(senior.ConsumeItemId);
        int cost = Convert.ToInt32(senior.ConsumeItemCount);
        const long uid = 49_900;
        Player player = CreateDrawCompatibilityPlayer(uid);
        player.MissionProgress = new();
        player.PlayerData.Level = 7;
        Inventory inventory = CreateDrawCompatibilityInventory(uid, [new Item { Id = costId, Count = cost - 1 }]);
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(uid), player, inventory, "manual-bp");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(uid);
        RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.TaskModule"),
            "EnsureMissionResets", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Session)])
            .Invoke(null, [harness.Session]);
        harness.Session.stage.Stages.Clear();
        // An authenticated session has already persisted its initial default/time-earned CGs.
        ArchiveCgModule.Reconcile(harness.Session, notify: false);
        using LoopbackSessionHarness other = new(CreateDrawCompatibilityCharacter(uid + 1),
            CreateDrawCompatibilityPlayer(uid + 1), CreateDrawCompatibilityInventory(uid + 1, []), "manual-bp-other");
        other.Session.stage = CreateLoginAccountCompatibilityStage(uid + 1);
        int packetId = 49_900;
        bool injectPersistenceFailure = false;
        NotifyWheelchairManualActivity Payload(LoopbackSessionHarness target) =>
            (NotifyWheelchairManualActivity)builder.Invoke(null, [target.Session, DateTimeOffset.UtcNow])!;
        long Count(int itemId) => harness.Session.inventory.Items.FirstOrDefault(item => item.Id == itemId)?.Count ?? 0;
        void SetCount(int itemId, long count)
        {
            Item? item = harness.Session.inventory.Items.FirstOrDefault(row => row.Id == itemId);
            if (item is null) harness.Session.inventory.Items.Add(new Item { Id = itemId, Count = count });
            else item.Count = count;
        }
        T Request<T>(string name, object? request, out NotifyWheelchairManualActivity? manualPush)
        {
            int sequence = packetId++;
            manualPush = null;
            if (injectPersistenceFailure)
                InvokeRegisteredRequestHandler(name, harness.Session, sequence, request);
            else
                harness.WriteClientBytes(LoopbackSessionHarness.SerializeClientRequestFrame(name, sequence, request));
            for (int index = 0; index < 64; index++)
            {
                Packet packet = harness.ReadPacket(name);
                if (packet.Type == Packet.ContentType.Push)
                {
                    Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                    if (push.Name == nameof(NotifyWheelchairManualActivity))
                        manualPush = MessagePackSerializer.Deserialize<NotifyWheelchairManualActivity>(push.Content);
                    continue;
                }
                AssertEqual(Packet.ContentType.Response, packet.Type, "Manual response packet type");
                Packet.Response response = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                AssertEqual(sequence, response.Id, "Manual response correlation");
                AssertEqual(typeof(T).Name, response.Name, "Manual response wire name");
                AssertNoAvailablePacket(harness, "Manual state updates precede response callback");
                return MessagePackSerializer.Deserialize<T>(response.Content);
            }
            throw new InvalidDataException($"Missing {name} response.");
        }
        WheelchairManualPurchaseResponse Buy(int? id) => Request<WheelchairManualPurchaseResponse>(
            "WheelchairManualPurchaseRequest", new WheelchairManualPurchaseRequest { ManualId = id }, out _);
        WheelchairManualGetManualRewardResponse Claim(int? id, out NotifyWheelchairManualActivity? push) =>
            Request<WheelchairManualGetManualRewardResponse>("WheelchairManualGetManualRewardRequest",
                new WheelchairManualGetManualRewardRequest { ManualId = id }, out push);
        int Blue(int? type) => Request<WheelchairManualClickBluePointResponse>("WheelchairManualClickBluePointRequest",
            new WheelchairManualClickBluePointRequest { Type = type }, out _).Code;
        int Red(long? id) => Request<WheelchairManualClickRedPointResponse>("WheelchairManualClickRedPointRequest",
            new WheelchairManualClickRedPointRequest { Id = id }, out _).Code;
        string Signature(IEnumerable<RewardGoods> entries) => string.Join(";", entries.GroupBy(row => row.TemplateId)
            .OrderBy(group => group.Key).Select(group => $"{group.Key}:{group.Sum(row => row.Count)}"));
        int[] Eligible(params int[] tiers) => tiers.SelectMany(id => rewardRows.Where(row =>
            row.Id >= manuals[id].BpRewardConfig[0] && row.Id <= manuals[id].BpRewardConfig[1]
            && row.Level <= Payload(harness).BpLevel)).Select(row => row.Id)
            .Except(Payload(harness).GetRewardManualRewardIds).OrderBy(id => id).ToArray();
        List<RewardGoods> AssertClaim(int tier, int[] expected)
        {
            var expectedGoods = expected.SelectMany(id => goods.Where(row => rewards[rewardRows.Single(reward => reward.Id == id).RewardId].SubIds.Contains(row.Id)))
                .Select(row => new RewardGoods { TemplateId = row.TemplateId, Count = row.Count });
            var result = Claim(tier, out var push);
            AssertEqual(0, result.Code, "Manual tier claim succeeds");
            AssertEqual(Signature(expectedGoods), Signature(result.RewardList), "Manual claim grants every eligible table reward");
            AssertEqual(true, push is not null && expected.All(push.GetRewardManualRewardIds.Contains), "Claim ids pushed before callback");
            AssertEqual(0, Request<GetPurchaseListResponse>(nameof(GetPurchaseListRequest),
                new GetPurchaseListRequest { UiTypeList = [activity.PurchaseType] }, out _).Code,
                "Full manual push purchase refresh does not report failure after a successful claim");
            return result.RewardList;
        }
        void FailsPersistence(Action action)
        {
            bool failed = false;
            injectPersistenceFailure = true;
            try { action(); }
            catch (InvalidDataException error) when (error.InnerException is MongoDB.Driver.MongoException) { failed = true; }
            finally { injectPersistenceFailure = false; }
            AssertEqual(true, failed, "Injected persistence failure propagates");
            AssertNoAvailablePacket(harness, "Failed persistence emits no success packets");
        }
        void Reload()
        {
            harness.Session.player = BsonSerializer.Deserialize<Player>(harness.Session.player.ToBson());
            harness.Session.inventory = BsonSerializer.Deserialize<Inventory>(inventories.LastReplacement!.ToBson());
            harness.Session.character = BsonSerializer.Deserialize<Character>((characters.LastReplacement ?? harness.Session.character).ToBson());
        }

        // Reuse an authoritative bounded schedule; never change its clock bounds.
        ActivityScheduleEntry window = ActivityScheduleService.All.First(row =>
            row.StartTime > 0 && row.EndTime > row.StartTime + 1
            && row.EndTime <= DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var originalTimeId = activity.TimeId;
        string windowInventoryBefore = harness.Session.inventory.ToJson();
        string windowPlayerBefore = harness.Session.player.ToJson();
        string windowCharacterBefore = harness.Session.character.ToJson();
        MethodInfo availability = module.GetMethod("Activity", BindingFlags.NonPublic | BindingFlags.Static)!;
        try
        {
            activity.TimeId = checked((int)window.Id);
            SetCount(expId, 250);
            string fundedInventory = harness.Session.inventory.ToJson();
            foreach (var (timestamp, code) in new[]
            {
                (window.StartTime - 1, 20236001),
                (window.StartTime, 0),
                (window.StartTime + 1, 0),
                (window.EndTime, 20236002)
            })
            {
                DateTimeOffset clock = DateTimeOffset.FromUnixTimeSeconds(timestamp);
                var payload = (NotifyWheelchairManualActivity)builder.Invoke(null, [harness.Session, clock])!;
                AssertEqual(code, (int)availability.Invoke(null, [null, clock])!,
                    "Manual request gate honors supplied window boundary");
                AssertEqual(code == 0 ? activity.Id : 0, payload.ActivityId,
                    "Manual login payload agrees with request window");
                if (code != 0)
                {
                    var inactive = MessagePackSerializer.Deserialize<NotifyWheelchairManualActivity>(
                        MessagePackSerializer.Serialize(payload));
                    AssertEqual(true, inactive.OpenActivityIds is { Count: 0 }
                        && inactive.GetRewardManualRewardIds is { Count: 0 }
                        && inactive.GetRewardPlanIds is { Count: 0 }
                        && inactive.FinishStageIds is { Count: 0 }
                        && inactive.TimeLimitActivityInfos is { Count: 0 }
                        && inactive.WeekActivityInfos is { Count: 0 }
                        && inactive.BluePointSet is { Count: 0 }
                        && inactive.RedPointSet is { Count: 0 },
                        "Inactive manual retains initialized full-schema lists");
                    AssertEqual(0, inactive.PlanId, "Inactive manual exposes no reward plan");
                    AssertEqual(0, inactive.BpLevel, "Inactive manual exposes no battle pass");
                    AssertEqual(false, inactive.IsSeniorManualUnlock, "Inactive manual exposes no paid unlock");
                }
            }
            AssertEqual(20236002, Buy(senior.Id).Code, "Closed manual cannot debit a purchase");
            var closedClaim = Claim(0, out var closedPush);
            AssertEqual(20236002, closedClaim.Code, "Closed manual cannot grant battle pass rewards");
            AssertEqual(0, closedClaim.RewardList.Count, "Closed manual grants no rewards");
            AssertEqual(true, closedPush is null, "Closed manual sends no active reward push");
            AssertEqual(20236002, Request<WheelchairManualGetPlanRewardResponse>(
                "WheelchairManualGetPlanRewardRequest", new { }, out _).Code,
                "Closed manual cannot grant plan rewards");
            AssertEqual(20236002, Blue(1), "Closed manual cannot acknowledge blue markers");
            AssertEqual(20236002, Red(1), "Closed manual cannot acknowledge red markers");
            AssertEqual(false, (bool)refresh.Invoke(null, [harness.Session])!,
                "Closed manual cannot consume pending EXP");
            AssertEqual(fundedInventory, harness.Session.inventory.ToJson(), "Window checks leave rewards and EXP untouched");
            AssertEqual(windowPlayerBefore, harness.Session.player.ToJson(), "Window checks leave manual state untouched");
            AssertEqual(windowCharacterBefore, harness.Session.character.ToJson(), "Window checks leave roster untouched");
        }
        finally
        {
            activity.TimeId = originalTimeId;
            harness.Session.inventory = BsonSerializer.Deserialize<Inventory>(windowInventoryBefore);
        }
        AssertEqual(activity.Id, Payload(harness).ActivityId, "Unscheduled configured manual remains available");

        AssertEqual(1, Payload(harness).BpLevel, "Zero-cost level zero starts fresh BP at one");
        AssertEqual(1, Payload(other).BpLevel, "Distinct account starts at one");
        foreach (int? invalid in new int?[] { null, -1, int.MaxValue })
        {
            AssertEqual(true, Buy(invalid).Code != 0, "Invalid purchase cannot debit");
            AssertEqual(true, Claim(invalid, out _).Code != 0, "Invalid tier cannot grant");
            AssertEqual(true, Blue(invalid) != 0, "Invalid blue marker rejected");
            AssertEqual(true, Red(invalid) != 0, "Invalid red marker rejected");
        }
        AssertEqual(true, Request<WheelchairManualPurchaseResponse>("WheelchairManualPurchaseRequest", null, out _).Code != 0, "Nil purchase rejected");
        AssertEqual(true, Request<WheelchairManualGetManualRewardResponse>("WheelchairManualGetManualRewardRequest", null, out _).Code != 0, "Nil reward request is not claim-all");
        AssertEqual(true, Request<WheelchairManualClickBluePointResponse>("WheelchairManualClickBluePointRequest", null, out _).Code != 0, "Nil blue rejected");
        AssertEqual(true, Request<WheelchairManualClickRedPointResponse>("WheelchairManualClickRedPointRequest", null, out _).Code != 0, "Nil red rejected");
        AssertEqual(20236012, Claim(senior.Id, out _).Code, "Senior rewards locked before purchase");
        AssertEqual(20012004, Buy(senior.Id).Code, "One below table cost rejects purchase");
        AssertEqual((long)cost - 1, Count(costId), "Insufficient funds unchanged");
        AssertClaim(0, Eligible(activity.CommonBattlePassManualId));
        AssertEqual(false, Payload(harness).IsSeniorManualUnlock, "Claim-all does not unlock the paid tier");

        // Claim a currently achieved task derived from actual level/login state, never a seeded condition counter.
        var taskRows = TableReaderV2.Parse<CurrentTaskTable>().ToDictionary(row => row.Id);
        var task = BuildTaskData(harness.Session).Where(row => row.State == 3).Select(row => taskRows[(int)row.Id])
            .First(row => goods.Any(good => rewards[row.RewardId].SubIds.Contains(good.Id) && good.TemplateId == expId));
        int earned = goods.Where(row => rewards[task.RewardId].SubIds.Contains(row.Id) && row.TemplateId == expId).Sum(row => row.Count);
        var taskResult = Request<FinishTaskResponse>(nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = task.Id }, out var earnedPush);
        AssertEqual(0, taskResult.Code, "Natural task claim succeeds");
        AssertEqual(1 + earned / 100, earnedPush?.BpLevel, "Task reward promotes BP before task response");
        AssertEqual((long)(earned % 100), Count(expId), "Task EXP inventory is remainder, not lifetime earned");
        AssertEqual(1, Payload(other).BpLevel, "Task reward cannot advance another player");
        int achievedLevel = Payload(harness).BpLevel;
        AssertEqual(true, Request<FinishTaskResponse>(nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = task.Id }, out _).Code != 0, "Duplicate natural task rejected");
        AssertEqual(achievedLevel, Payload(harness).BpLevel, "Duplicate task cannot promote again");

        SetCount(expId, 250);
        AssertEqual(true, (bool)refresh.Invoke(null, [harness.Session])!, "Multiple EXP thresholds consumed");
        AssertEqual(achievedLevel + 2, Payload(harness).BpLevel, "Each hundred earns a level");
        AssertEqual(50L, Count(expId), "Partial next level retained");
        Reload();
        AssertEqual(achievedLevel + 2, Payload(harness).BpLevel, "Durable inventory receipts reconstruct level after relog");
        AssertEqual(false, (bool)refresh.Invoke(null, [harness.Session])!, "Reconciliation cannot consume the same EXP twice");
        AssertClaim(activity.CommonBattlePassManualId, Eligible(activity.CommonBattlePassManualId));

        SetCount(costId, cost);
        string inventoryBefore = harness.Session.inventory.ToJson();
        inventories.ThrowOnReplaceOne = true;
        try { FailsPersistence(() => Buy(senior.Id)); }
        finally { inventories.ThrowOnReplaceOne = false; }
        AssertEqual(inventoryBefore, harness.Session.inventory.ToJson(), "Failed inventory save rolls back debit and receipt");
        AssertEqual(false, Payload(harness).IsSeniorManualUnlock, "Failed debit cannot unlock senior");
        string playerBefore = harness.Session.player.ToJson();
        players.ThrowOnReplaceOne = true;
        try { FailsPersistence(() => Buy(senior.Id)); }
        finally { players.ThrowOnReplaceOne = false; }
        AssertEqual(playerBefore, harness.Session.player.ToJson(), "Failed player save rolls back staged unlock");
        AssertEqual(0L, Count(costId), "Purchase receipt durably debits exact table cost");
        Reload();
        AssertEqual(0, Buy(senior.Id).Code, "Receipt retry succeeds even with already-debited balance");
        AssertEqual(0L, Count(costId), "Purchase retry does not debit twice");
        AssertEqual(true, Payload(harness).IsSeniorManualUnlock, "Purchased senior unlock persists");
        AssertEqual(20236011, Buy(senior.Id).Code, "Duplicate purchase rejected");
        AssertEqual(0L, Count(costId), "Duplicate purchase balance unchanged");

        int[] seniorExpected = Eligible(senior.Id);
        inventoryBefore = harness.Session.inventory.ToJson();
        inventories.ThrowOnReplaceOne = true;
        try { FailsPersistence(() => Claim(senior.Id, out _)); }
        finally { inventories.ThrowOnReplaceOne = false; }
        AssertEqual(inventoryBefore, harness.Session.inventory.ToJson(), "Failed reward inventory save grants nothing");
        playerBefore = harness.Session.player.ToJson();
        players.ThrowOnReplaceOne = true;
        try { FailsPersistence(() => Claim(senior.Id, out _)); }
        finally { players.ThrowOnReplaceOne = false; }
        AssertEqual(playerBefore, harness.Session.player.ToJson(), "Failed reward player save rolls back claimed ids");
        Reload();
        inventoryBefore = harness.Session.inventory.ToJson();
        string characterBefore = harness.Session.character.ToJson();
        AssertClaim(senior.Id, seniorExpected);
        AssertEqual(inventoryBefore, harness.Session.inventory.ToJson(), "Durable reward retry never grants items twice");
        AssertEqual(characterBefore, harness.Session.character.ToJson(), "Durable reward retry never grants characters or equipment twice");
        AssertEqual(true, Claim(senior.Id, out _).Code != 0, "Claimed tier replay rejected");
        AssertEqual(inventoryBefore, harness.Session.inventory.ToJson(), "Rejected reward replay leaves inventory unchanged");

        int currentLevel = Payload(harness).BpLevel;
        long toMaximum = levels.Where(row => row.Level >= currentLevel && row.Level < levels.Last().Level)
            .Sum(row => (long)Convert.ToInt32(row.NeedExp));
        SetCount(expId, toMaximum + 37);
        refresh.Invoke(null, [harness.Session]);
        AssertEqual(Convert.ToInt32(levels.Last().Level), Payload(harness).BpLevel, "BP stops at configured maximum");
        AssertEqual(37L, Count(expId), "Max progression does not consume surplus EXP");
        AssertEqual(false, (bool)refresh.Invoke(null, [harness.Session])!, "Maximum level reconciliation is idempotent");
        var claimAllGoods = AssertClaim(0, Eligible(activity.CommonBattlePassManualId, senior.Id));
        AssertEqual(true, AssertParentAwardAutoUse(harness, claimAllGoods, ref packetId).Count > 0,
            "Claim-all exercises awarded auto boxes");
        inventoryBefore = harness.Session.inventory.ToJson();
        characterBefore = harness.Session.character.ToJson();
        AssertEqual(true, Claim(0, out _).Code != 0, "Fully claimed all-tier replay rejected");
        AssertEqual(inventoryBefore, harness.Session.inventory.ToJson(), "Claim-all replay cannot duplicate items");
        AssertEqual(characterBefore, harness.Session.character.ToJson(), "Claim-all replay cannot duplicate roster rewards");

        // The high 32 bits distinguish tab namespaces; neither packet nor BSON may narrow marker IDs.
        harness.Session.player.PlayerData.Level = 1;
        harness.Session.stage.Stages.Clear();
        var locked = Payload(harness);
        var lottoTab = TableReaderV2.Parse<WheelchairManualTabsTable>().Single(row => row.Condition > 0);
        AssertEqual(false, locked.BluePointSet.Contains(lottoTab.Type), "Story-locked lotto tab not exposed");
        AssertEqual(true, Blue(lottoTab.Type) != 0, "Locked tab cannot be pre-acknowledged");
        long giftMarker = activity.ShowPackageIds.Select(id => ((long)5 << 32) | (uint)id)
            .First(id => !locked.RedPointSet.Contains(id));
        AssertEqual(true, Red(giftMarker) != 0, "Locked gift cannot be pre-acknowledged");
        int blue = locked.BluePointSet.First();
        AssertEqual(0, Blue(blue), "First exposed blue marker acknowledged");
        AssertEqual(false, Payload(harness).BluePointSet.Contains(blue), "Acknowledged blue disappears");
        var stages = TableReaderV2.Parse<StageTable>().ToDictionary(row => row.StageId);
        var teaching = stages[activity.TeachConnectivityStageId];
        long teachingMarker = ((long)6 << 32) | (uint)teaching.StageId;
        AssertEqual(false, locked.RedPointSet.Contains(teachingMarker), "Teaching prerequisites hide its marker");
        AssertEqual(true, Red(teachingMarker) != 0, "Locked teaching marker cannot be acknowledged");
        harness.Session.player.PlayerData.Level = 80;
        foreach (int prerequisite in teaching.PreStageId.Where(id => id > 0))
            harness.Session.stage.Stages[prerequisite] = new StageDatum { StageId = prerequisite, Passed = true };
        // All gates are derived from the configured condition, rather than captured availability.
        var condition = TableReaderV2.Parse<AscNet.Table.V2.share.condition.ConditionTable>()
            .Single(row => row.Id == lottoTab.Condition);
        foreach (int stageId in condition.Params.Where(stages.ContainsKey))
            harness.Session.stage.Stages[stageId] = new StageDatum { StageId = stageId, Passed = true };
        // Gift condition 770303 requires Normal Story 1-4 in addition to its level gate.
        harness.Session.stage.Stages[10010104] = new StageDatum { StageId = 10010104, Passed = true };
        AssertEqual(true, Payload(harness).BluePointSet.Contains(lottoTab.Type), "Unlocked lotto first exposure appears");
        AssertEqual(true, Payload(harness).RedPointSet.Contains(teachingMarker), "Unlocked teaching marker retains long namespace");
        AssertEqual(0, Blue(lottoTab.Type), "Newly unlocked tab acknowledges");
        AssertEqual(0, Red(teachingMarker), "Long teaching marker acknowledges");
        AssertEqual(true, Payload(harness).RedPointSet.Contains(giftMarker), "Newly unlocked gift marker first exposure appears");
        AssertEqual(0, Red(giftMarker), "Unlocked gift marker acknowledges independently");
        harness.Session.player = BsonSerializer.Deserialize<Player>(players.LastReplacement!.ToBson());
        var relogged = Payload(harness);
        AssertEqual(false, relogged.BluePointSet.Contains(blue) || relogged.BluePointSet.Contains(lottoTab.Type), "Blue acknowledgements survive relog");
        AssertEqual(false, relogged.RedPointSet.Contains(teachingMarker) || relogged.RedPointSet.Contains(giftMarker), "Long red acknowledgements survive relog");
        AssertEqual(0, Red(teachingMarker), "Duplicate eligible acknowledgement is idempotent");
        AssertEqual(true, Payload(other).BluePointSet.Contains(blue), "Marker acknowledgements isolated by player");
    }

    private static List<RewardGoods> AssertParentAwardAutoUse(LoopbackSessionHarness harness,
        IEnumerable<RewardGoods> parentRewards, ref int packetId)
    {
        var items = TableReaderV2.Parse<ItemTable>().ToDictionary(row => row.Id);
        var policies = TableReaderV2.Parse<EquipmentOverclockDropPolicyTable>().ToDictionary(row => row.Id);
        var materials = TableReaderV2.Parse<EquipBreakThroughTable>()
            .SelectMany(row => row.ItemId.Zip(row.ItemCount)
                .Where(pair => pair.First > 0 && pair.Second > 0).Select(pair => pair.First)).ToHashSet();
        List<RewardGoods> opened = [];
        foreach (var award in parentRewards.GroupBy(row => row.TemplateId))
        {
            if (!items.TryGetValue(award.Key, out var box) || box.ItemType != (int)AscNet.Common.ItemType.Gift
                || box.SubTypeParams.Count < 2 || box.SubTypeParams[0] != 6)
                continue;
            var policy = policies[box.SubTypeParams[1]];
            var pool = materials.Where(id => items[id].Quality == policy.Quality).ToHashSet();
            var before = harness.Session.inventory.Items.ToDictionary(row => row.Id, row => row.Count);
            ItemUseRequest request = new() { Id = box.Id, Count = award.Sum(row => row.Count) };
            AssertEqual(true, before.GetValueOrDefault(box.Id) >= request.Count, "Parent award stocks auto box before callback");
            int sequence = ++packetId;
            harness.WriteClientBytes(LoopbackSessionHarness.SerializeClientRequestFrame(nameof(ItemUseRequest), sequence, request));
            var notification = ReadPushPayload<NotifyItemDataList>(harness, nameof(NotifyItemDataList), "Parent award auto-use inventory");
            var response = ReadResponsePayload<ItemUseResponse>(harness, sequence, nameof(ItemUseResponse), "Parent award auto-use response");
            AssertEqual(0, response.Code, "Parent award auto-use succeeds");
            AssertEqual(request.Count * policy.Count, response.RewardGoodsList.Sum(row => row.Count),
                "Parent auto boxes grant table-policy quantity exactly once");
            foreach (var good in response.RewardGoodsList)
            {
                AssertEqual(true, pool.Contains(good.TemplateId) && good.Count > 0, "Parent auto box grants authoritative material");
                AssertEqual((int)RewardType.Item, good.RewardType, "Parent auto box grants item reward");
            }
            before[box.Id] -= request.Count;
            foreach (var good in response.RewardGoodsList)
                before[good.TemplateId] = before.GetValueOrDefault(good.TemplateId) + good.Count;
            foreach (int id in before.Keys.Union(harness.Session.inventory.Items.Select(row => row.Id)))
                AssertEqual(before.GetValueOrDefault(id), harness.Session.inventory.Items.SingleOrDefault(row => row.Id == id)?.Count ?? 0,
                    "Parent auto-use consumes and grants exactly once");
            foreach (var item in notification.ItemDataList)
                AssertEqual(before.GetValueOrDefault(item.Id), item.Count, "Parent auto-use pushes final balance");
            AssertNoAvailablePacket(harness, "Parent auto-use completes before client callback");
            opened.AddRange(response.RewardGoodsList);
        }
        return opened;
    }
}
