using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.reward;
using AscNet.Table.V2.share.task;
using AscNet.Table.V2.share.theatre5;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Newtonsoft.Json.Linq;
using System.Reflection;
using Character = AscNet.Common.Database.Character;
using Inventory = AscNet.Common.Database.Inventory;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidateGodfallMetaChecks()
    {
        Type module = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.Theatre5Module");
        var taskShops = TableReaderV2.Parse<AscNet.Table.V2.client.theatre5.Theatre5TaskShopTable>();
        var limits = taskShops.Where(row => row.Type == 2).Select(row => row.TaskTimeLimitId).ToHashSet();
        var taskLimits = TableReaderV2.Parse<TaskTimeLimitTable>().Where(row => limits.Contains(row.Id)).ToList();
        var ids = taskLimits.SelectMany(row => row.TaskId).ToHashSet();
        var tasks = TableReaderV2.Parse<TaskTable>().Where(row => row.Type == 11 && ids.Contains(row.Id)).OrderBy(row => row.Id).ToList();
        var conditions = TableReaderV2.Parse<AscNet.Table.V2.share.task.ConditionTable>().ToDictionary(row => row.Id);
        var goods = TableReaderV2.Parse<RewardGoodsTable>();
        AssertEqual(97, tasks.Count, "Imported Godfall shared task coverage (not a shop catalog)");
        DateTimeOffset calendarNow = DateTimeOffset.UtcNow;
        var activeIds = taskLimits.Where(row => row.TimeId is > 0
            && AscNet.GameServer.Game.ActivityScheduleService.IsOpen(row.TimeId.Value, calendarNow))
            .SelectMany(row => row.TaskId).ToHashSet();
        var activeTasks = tasks.Where(row => activeIds.Contains(row.Id)).ToList();
        var inactiveTasks = tasks.Where(row => !activeIds.Contains(row.Id)).ToList();
        var activeConditions = activeTasks.Select(row => row.Condition).ToHashSet();
        AssertEqual(true, activeTasks.Count > 0 && inactiveTasks.Count > 0, "Approved Godfall calendars leave historical task periods closed");
        Type mutationType = module.GetNestedType("Mutation", BindingFlags.NonPublic)!;
        MethodInfo record = module.GetMethod("RecordMetaProgress", BindingFlags.Static | BindingFlags.NonPublic)!;
        MethodInfo evaluate = module.GetMethod("EvaluateMetaCondition", BindingFlags.Static | BindingFlags.NonPublic)!;

        // Explicit producer/claim contract fixtures, not natural battle progression.
        using (GodfallCase test = new("all-shared-tasks"))
        {
            test.State.TaskProgress.Clear();
            test.State.ClaimedTaskIds.Clear();
            test.Session.player.MissionProgress.ClaimedTaskIds.RemoveAll(ids.Contains);
            test.SaveFixture();
            var unearned = activeTasks.First(row => row.ShowAfterTaskId is not > 0 && conditions[row.Condition].Type == 131008);
            AssertEqual(20026007, test.Call(nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = unearned.Id }, success: false).Value<int>("Code"),
                "Unearned shared task cannot mint rewards");
            foreach (var condition in tasks.Select(row => conditions[row.Condition]).DistinctBy(row => row.Id))
            {
                object mutation = Activator.CreateInstance(mutationType, BindingFlags.Instance | BindingFlags.NonPublic,
                    null, [test.Session, false], null)!;
                var state = (PlayerTheatre5State)mutationType.GetProperty("State")!.GetValue(mutation)!;
                state.TaskProgress.Clear();
                state.Data.Characters.Clear();
                var p = condition.Params;
                if (!activeConditions.Contains(condition.Id))
                {
                    string trigger;
                    int character = p[0] > 0 ? p[0] : 1;
                    if (condition.Type == 131008)
                    {
                        trigger = p[2] == 1 ? "BattleWin" : "BattleFinish";
                    }
                    else
                    {
                        AssertEqual(131010, condition.Type, "Closed shared task retains supported authored operand family");
                        int rating = TableReaderV2.Parse<Theatre5RankTable>().Single(row => row.Id == p[1]).Rating
                            ?? throw new InvalidDataException($"Historical Godfall rank {p[1]} lacks its rating threshold.");
                        state.Data.Characters[character] = new Theatre5PvpCharacter { Id = character, Rating = rating };
                        trigger = "PvpRankChanged";
                    }
                    record.Invoke(null, [mutation, trigger, character, p[1]]);
                    AssertEqual(0, state.TaskProgress.GetValueOrDefault(condition.Id),
                        $"Closed task condition {condition.Id} cannot record current-period producer progress");
                    // Historical earned progress must still be rejected by the expired
                    // claim calendar, rather than being mistaken for a live entitlement.
                    test.State.TaskProgress[condition.Id] = tasks.Where(row => row.Condition == condition.Id).Max(row => Math.Max(1, row.Result ?? 1));
                    continue;
                }
                if (condition.Type == 131008)
                {
                    string trigger = p[2] == 1 ? "BattleWin" : "BattleFinish";
                    int character = p[0] > 0 ? p[0] : 1;
                    record.Invoke(null, [mutation, p[2] == 1 ? "BattleFinish" : "BattleWin", character, p[1]]);
                    AssertEqual(0, (int)evaluate.Invoke(null, [state, condition])!, $"Task {condition.Id} distinguishes wins from finishes");
                    if (p[0] > 0)
                    {
                        record.Invoke(null, [mutation, trigger, character + 100, p[1]]);
                        AssertEqual(0, (int)evaluate.Invoke(null, [state, condition])!, $"Task {condition.Id} rejects another character");
                    }
                    record.Invoke(null, [mutation, trigger, character, p[1] - 1]);
                    AssertEqual(p[1] - 1, (int)evaluate.Invoke(null, [state, condition])!, $"Task {condition.Id} below authored threshold");
                    record.Invoke(null, [mutation, trigger, character, 2]);
                    AssertEqual(p[1], (int)evaluate.Invoke(null, [state, condition])!, $"Task {condition.Id} saturates at authored target");
                }
                else
                {
                    AssertEqual(131010, condition.Type, "Shared task has a supported authored operand family");
                    int rating = TableReaderV2.Parse<Theatre5RankTable>().Single(row => row.Id == p[1]).Rating
                        ?? throw new InvalidDataException($"Authored Godfall rank {p[1]} lacks its rating threshold.");
                    int character = p[0] > 0 ? p[0] : 1;
                    state.Data.Characters[character] = new Theatre5PvpCharacter { Id = character, Rating = rating - 1 };
                    AssertEqual(0, (int)evaluate.Invoke(null, [state, condition])!, $"Rank task {condition.Id} below rating boundary");
                    state.Data.Characters[character].Rating = rating;
                    record.Invoke(null, [mutation, "PvpRankChanged", character, 1]);
                    AssertEqual(1, (int)evaluate.Invoke(null, [state, condition])!, $"Rank task {condition.Id} reaches authored rank rating");
                    state.Data.Characters[character].Rating = 0;
                    AssertEqual(1, (int)evaluate.Invoke(null, [state, condition])!, $"Rank task {condition.Id} retains reached rank after demotion");
                }
                test.State.TaskProgress[condition.Id] = (int)evaluate.Invoke(null, [state, condition])!;
            }
            test.SaveFixture();
            var before = test.Session.inventory.Items.ToDictionary(item => item.Id, item => (long)item.Count);
            byte[] unclaimedInventory = test.Inventories.LastSuccessfulReplacementBson!.ToArray();
            byte[] unclaimedCharacter = test.Characters.LastSuccessfulReplacementBson!.ToArray();
            foreach (var task in inactiveTasks)
            {
                AssertEqual(20026007, test.Call(nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = task.Id }, success: false).Value<int>("Code"),
                    $"Historical-earned closed task {task.Id} cannot claim outside its authored calendar");
                AssertEqual(false, test.State.ClaimedTaskIds.Contains(task.Id), $"Closed task {task.Id} grants no claim entitlement");
                AssertEqual(true, unclaimedInventory.SequenceEqual(test.Inventories.LastSuccessfulReplacementBson!)
                    && unclaimedCharacter.SequenceEqual(test.Characters.LastSuccessfulReplacementBson!),
                    $"Closed task {task.Id} cannot persist reward items or nameplates");
            }
            var remaining = activeTasks.ToList();
            while (remaining.Count > 0)
            {
                var task = remaining.FirstOrDefault(row => row.ShowAfterTaskId is not > 0 || test.State.ClaimedTaskIds.Contains(row.ShowAfterTaskId.Value))
                    ?? throw new InvalidDataException("Godfall shared task prerequisites contain a missing predecessor or cycle.");
                JObject response = test.Call(nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = task.Id });
                var expected = ResolveRewardGoods(task.RewardId ?? 0, goods, "Godfall shared task");
                var actual = response["RewardGoodsList"]!.ToObject<List<RewardGoods>>()!;
                AssertEqual(true, expected.OrderBy(row => row.Id).Select(row => (row.TemplateId, row.Count)).SequenceEqual(
                    actual.OrderBy(row => row.Id).Select(row => (row.TemplateId, row.Count))), $"Task {task.Id} returns authored goods");
                AssertEqual(true, BsonSerializer.Deserialize<Player>(test.Players.LastSuccessfulReplacementBson!).Theatre5.ClaimedTaskIds.Contains(task.Id),
                    $"Task {task.Id} claim survives the actual player BSON write");
                remaining.Remove(task);
            }
            GodfallAssertMetaItems(test, before, activeTasks.Select(row => row.RewardId ?? 0));
            test.Relog("active-and-closed-task-contracts");
            AssertEqual(true, activeTasks.All(row => test.State.ClaimedTaskIds.Contains(row.Id)), "All active task claims survive BSON relog");
            AssertEqual(true, inactiveTasks.All(row => !test.State.ClaimedTaskIds.Contains(row.Id)), "All historical closed tasks remain unclaimed after BSON relog");
            JObject duplicate = test.Call(nameof(FinishMultiTaskRequest), new FinishMultiTaskRequest { TaskIds = tasks.Select(row => row.Id).ToList() });
            AssertEqual(0, ((JArray)duplicate["SuccessTaskIds"]!).Count, "Already claimed task batch grants nothing");
            AssertEqual(true, tasks.Select(row => row.Id).Order().SequenceEqual(duplicate["NotDealTaskIds"]!.Values<int>().Order()), "Batch reports all 97 already-claimed or calendar-closed tasks without payment");
            GodfallAssertMetaItems(test, before, activeTasks.Select(row => row.RewardId ?? 0));
            Console.WriteLine($"Godfall shared tasks: {activeTasks.Count} active claims and {inactiveTasks.Count} calendar-closed rejections; {tasks.Count} imported rows covered.");
        }

        var recoveryTask = activeTasks.First(row => row.ShowAfterTaskId is not > 0 && conditions[row.Condition].Type == 131008 && row.RewardId > 0);
        using (GodfallCase test = new("task-inventory-failure"))
        {
            test.State.TaskProgress[recoveryTask.Condition] = conditions[recoveryTask.Condition].Params[1];
            test.SaveFixture();
            var before = test.Session.inventory.Items.ToDictionary(item => item.Id, item => (long)item.Count);
            byte[] inventoryBefore = test.Inventories.LastSuccessfulReplacementBson!.ToArray();
            var request = new FinishTaskRequest { TaskId = recoveryTask.Id };
            test.Inventories.ThrowOnReplaceOne = true;
            try { test.Call(nameof(FinishTaskRequest), request, success: false); }
            finally { test.Inventories.ThrowOnReplaceOne = false; }
            AssertEqual(0, test.Pushes.Count, "Failed inventory grant emits no success pushes");
            AssertEqual(true, inventoryBefore.SequenceEqual(test.Inventories.LastSuccessfulReplacementBson!), "Failed grant preserves durable inventory");
            AssertEqual(true, BsonSerializer.Deserialize<Player>(test.Players.LastSuccessfulReplacementBson!).Theatre5.PendingMutation != null, "Failed grant retains durable journal");
            test.ReloadDurable();
            AssertEqual(true, test.State.PendingMutation != null, "Durable reload preserves pending task before semantic retry");
            JObject recovered = test.Call(nameof(FinishTaskRequest), request);
            AssertEqual(true, test.State.PendingMutation == null && test.State.ClaimedTaskIds.Contains(recoveryTask.Id), "New packet semantic retry commits pending claim");
            JObject replay = test.Call(nameof(FinishTaskRequest), request, reusePacketId: test.LastPacketId);
            AssertEqual(true, JToken.DeepEquals(recovered, replay), "Committed task replay returns frozen response");
            AssertEqual(0, test.Pushes.Count, "Committed duplicate is response-only");
            GodfallAssertMetaItems(test, before, [recoveryTask.RewardId!.Value]);
        }
        ValidateGodfallMetaConditions(module);
        ValidateGodfallNameplates(tasks.SelectMany(row => ResolveRewardGoods(row.RewardId ?? 0, goods, "Godfall nameplate task source"))
            .Select(row => row.TemplateId).ToHashSet());
        ValidateGodfallBfrtMixedNameplateReward();
        ValidateGodfallForeignTaskPendingGuard();
        ValidateGodfallForeignMonthlyShopPendingGuard();
        ValidateGodfallLocalNameplatePolicy();

        using (GodfallCase test = new("source-absent-common-shops"))
        {
            var shops = taskShops.Where(row => row.Type == 1).Select(row => checked((uint)(row.ShopId
                ?? throw new InvalidDataException($"Godfall shop tab {row.Id} lacks its authored shop ID.")))).Distinct().ToList();
            byte[] before = test.Session.inventory.ToBson();
            foreach (uint shop in shops)
            {
                AssertEqual(1, test.Call(nameof(GetShopInfoRequest), new GetShopInfoRequest { Id = shop }, success: false).Value<int>("Code"), $"Source-absent shop {shop} is explicitly unavailable");
                AssertEqual(1, test.Call(nameof(BuyRequest), new BuyRequest { ShopId = shop, GoodsId = 1, Count = 1 }, success: false).Value<int>("Code"), $"Shop {shop} cannot fabricate goods");
                AssertEqual(true, before.SequenceEqual(test.Session.inventory.ToBson()), "Unavailable shop does not debit or reward inventory");
            }
            JObject list = test.Call(nameof(GetFixedShopListRequest), new GetFixedShopListRequest { IdList = shops }, success: false);
            AssertEqual(0, ((JArray)list["ClientShopList"]!).Count, "Missing common shop catalogs are not reported as complete empty catalogs");
        }
    }

    private static void GodfallAssertMetaItems(GodfallCase test, IReadOnlyDictionary<int, long> before, IEnumerable<int> rewards)
    {
        var rows = TableReaderV2.Parse<RewardGoodsTable>();
        var goods = rewards.SelectMany(id => ResolveRewardGoods(id, rows, "Godfall persisted meta rewards")).ToList();
        var items = TableReaderV2.Parse<AscNet.Table.V2.share.item.ItemTable>().Select(row => row.Id).ToHashSet();
        var nameplates = TableReaderV2.Parse<AscNet.Table.V2.share.nameplate.NameplateTable>().ToDictionary(row => row.Id);
        AssertEqual(true, goods.All(row => items.Contains(row.TemplateId) || nameplates.ContainsKey(row.TemplateId)),
            "Shared task reward rows resolve to imported items or nameplate entitlements");
        var expected = goods.Where(row => items.Contains(row.TemplateId)).GroupBy(row => row.TemplateId)
            .ToDictionary(group => group.Key, group => group.Sum(row => (long)row.Count));
        var character = BsonSerializer.Deserialize<Character>(test.Characters.LastSuccessfulReplacementBson!);
        foreach (var plate in goods.Where(row => nameplates.ContainsKey(row.TemplateId)).DistinctBy(row => row.TemplateId))
        {
            AssertEqual(1, character.Nameplates.Count(value => value.Id == plate.TemplateId),
                $"Shared task nameplate {plate.TemplateId} persists exactly one actual entitlement");
            var owned = character.Nameplates.Single(value => value.Id == plate.TemplateId);
            AssertEqual(0L, owned.EndTime, $"Shared task nameplate {plate.TemplateId} is permanent");
            AssertEqual(true, owned.GetTime > 0, $"Shared task nameplate {plate.TemplateId} persists acquisition time");
            AssertEqual(1, test.Session.character.Nameplates.Count(value => value.Id == plate.TemplateId),
                $"Shared task nameplate {plate.TemplateId} live ownership agrees with BSON");
        }
        var saved = BsonSerializer.Deserialize<Inventory>(test.Inventories.LastSuccessfulReplacementBson!).Items.ToDictionary(item => item.Id, item => (long)item.Count);
        foreach (int id in before.Keys.Concat(expected.Keys).Concat(saved.Keys).Distinct())
        {
            AssertEqual(before.GetValueOrDefault(id) + expected.GetValueOrDefault(id), saved.GetValueOrDefault(id), $"Persisted Godfall reward {id} exactly once without incidental goods");
            AssertEqual(saved.GetValueOrDefault(id), (long)test.Balance(id), $"Live Godfall reward {id} agrees with BSON");
        }
    }

    private static void ValidateGodfallMetaConditions(Type module)
    {
        using GodfallCase test = new("conditions-and-archive");
        MethodInfo condition = module.GetMethod("IsConditionMet", BindingFlags.Static | BindingFlags.NonPublic, null,
            [typeof(Session), typeof(int), typeof(PlayerTheatre5State)], null)!;
        bool Met(int id) => (bool)condition.Invoke(null, [test.Session, id, test.State])!;
        var rows = TableReaderV2.Parse<AscNet.Table.V2.share.condition.ConditionTable>().ToDictionary(row => row.Id);
        test.Data.PveStoryLines.Clear();
        AssertEqual(true, Met(0), "Absent Godfall gate passes");
        AssertEqual(false, Met(int.MaxValue), "Missing Godfall gate fails closed");
        AssertEqual(false, Met(1), "Unsupported condition fails closed");
        test.Data.PvpType = 1;
        AssertEqual(false, Met(863034), "Authored PVE mode operand does not match wire PVP");
        AssertEqual(true, Met(863035), "Authored PVP mode operand matches wire PVP");
        test.Data.PvpType = 2;
        AssertEqual(true, Met(863034), "Authored PVE mode operand matches wire PVE");
        AssertEqual(false, Met(863035), "Authored PVP operand does not match wire PVE");
        foreach (var row in rows.Values.Where(row => row.Type == 17826))
        {
            int content = Convert.ToInt32(row.Params[0]);
            bool expected = Convert.ToInt32(row.Params[1]) == 1;
            test.Data.PveStoryLines.Clear();
            test.Data.PveStoryLines[1] = new Theatre5StoryLine { StoryLineId = 1, CurContentId = content };
            AssertEqual(!expected, Met(row.Id), $"Story gate {row.Id} ignores merely selected content");
            test.Data.PveStoryLines[1].FinishContents.Add(content);
            AssertEqual(expected, Met(row.Id), $"Story gate {row.Id} reads exact completed-content operand");
        }
        test.Data.CommonFightCnt.Clear();
        test.State.CharacterWinCounts[1] = 1000;
        AssertEqual(false, Met(1050172), "Shared win condition ignores private counters");
        test.Data.CommonFightCnt[1] = 29;
        AssertEqual(false, Met(1050172), "Shared win condition rejects one below authored target");
        test.Data.CommonFightCnt[2] = 1;
        AssertEqual(true, Met(1050172), "Shared win condition sums authoritative CommonFightCnt");
        test.Data.PveStoryLines.Clear();
        test.Data.PveStoryLines[1] = new Theatre5StoryLine { StoryLineId = 1, FinishContents = [721] };
        AssertEqual(true, Met(1020916), "Authored AND accepts completed 721 and absent 713");
        test.Data.PveStoryLines[1].FinishContents.Add(713);
        AssertEqual(false, Met(1020916), "Authored negative leaf invalidates AND");
        test.Data.PveStoryLines[1].FinishContents = [720];
        AssertEqual(true, Met(1020912) && Met(1020913), "Archive story OR gates accept alternate final ending");
        var storyGates = TableReaderV2.Parse<AscNet.Table.V2.client.theatre5.Theatre5StoryTable>();
        AssertEqual(41, storyGates.Count, "Imported Godfall story archive gate coverage");
        test.Data.PveStoryLines[1].FinishContents = rows.Values.Where(row => row.Type == 17826 && Convert.ToInt32(row.Params[1]) == 1).Select(row => Convert.ToInt32(row.Params[0])).Distinct().ToList();
        AssertEqual(true, storyGates.All(row => Met(row.Condition)), "All 41 imported story archive gates accept their completed history");
        var cgs = TableReaderV2.Parse<AscNet.Table.V2.share.archive.CGDetailTable>().Where(row => row.Condition is > 0 && rows.GetValueOrDefault(row.Condition.Value)?.Type == 17826).ToList();
        AssertEqual(true, cgs.Count > 0, "Imported archive CGs exercise Godfall story conditions");
        var completed = test.Data.PveStoryLines[1].FinishContents;
        test.Data.PveStoryLines[1].FinishContents = [];
        test.Session.player.ArchiveUnlockedCgs.ExceptWith(cgs.Select(row => row.Id));
        var locked = ArchiveCgModule.GetUnlockedCgs(test.Session, DateTimeOffset.UtcNow);
        AssertEqual(true, cgs.Where(row => Convert.ToInt32(rows[row.Condition!.Value].Params[1]) == 1
            && (string.IsNullOrWhiteSpace(row.UnLockTime) || row.UnLockTime == "0")).All(row => !locked.Contains(row.Id)),
            "CG catalog membership without finished story grants no entitlement");
        test.Data.PveStoryLines[1].FinishContents = completed;
        // Direct completed-history fixture must first perform the same authored
        // storyline/character/knowledge unlock reconciliation as a real login.
        // The subsequent relog still requires byte-for-byte durable mode stability.
        RequiredMethod(module, "PrepareLogin", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Session)])
            .Invoke(null, [test.Session]);
        AssertEqual(true, storyGates.All(row => Met(row.Condition)), "Authored login reconciliation preserves completed archive gates");
        ArchiveCgModule.Reconcile(test.Session, notify: false);
        var saved = BsonSerializer.Deserialize<Player>(test.Players.LastSuccessfulReplacementBson!);
        AssertEqual(true, cgs.Where(row => Convert.ToInt32(rows[row.Condition!.Value].Params[1]) == 1).All(row => saved.ArchiveUnlockedCgs.Contains(row.Id)), "Story-earned CG entitlements persist to player BSON");
        test.Relog("story-archive-entitlements");
        AssertEqual(true, storyGates.All(row => Met(row.Condition)), "Story archive gates survive BSON reload");
    }

    private static void ValidateGodfallNameplates(HashSet<int> taskRewardIds)
    {
        Type rewardHandler = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.RewardHandler");
        Type grantType = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.RewardGrant");
        MethodInfo apply = rewardHandler.GetMethod("ApplyRewardsOnceAndPersist", BindingFlags.Static | BindingFlags.Public)!;
        var catalog = TableReaderV2.Parse<AscNet.Table.V2.share.nameplate.NameplateTable>().Where(row => taskRewardIds.Contains(row.Id)).ToList();
        AssertEqual(9, catalog.Count, "Godfall's nine task nameplates remain distinct from the global nameplate catalog");
        var goods = TableReaderV2.Parse<RewardGoodsTable>();
        foreach (var plate in catalog)
        {
            using GodfallCase test = new($"nameplate-{plate.Id}");
            var reward = goods.First(row => row.TemplateId == plate.Id && row.Count == 1);
            string key = $"godfall-nameplate:{plate.Id}";
            void Grant()
            {
                Array grants = Array.CreateInstance(grantType, 1);
                grants.SetValue(Activator.CreateInstance(grantType, key, new[] { reward }, null, null), 0);
                _ = apply.Invoke(null, [grants, test.Session]);
            }
            test.Session.character.Nameplates.Clear();
            test.Session.character.CurrentWearNameplate = 0;
            test.SaveFixture();
            AssertEqual(20120002, test.Call(nameof(WearNameplateRequest), new WearNameplateRequest { NameplateId = plate.Id }, success: false).Value<int>("Code"), "Catalog presence is not ownership");
            byte[] characterBefore = test.Characters.LastSuccessfulReplacementBson!.ToArray();
            test.Characters.ThrowOnReplaceOne = true;
            bool failed = false;
            try { Grant(); }
            catch (TargetInvocationException exception) when (exception.InnerException is MongoDB.Driver.MongoException) { failed = true; }
            finally { test.Characters.ThrowOnReplaceOne = false; }
            AssertEqual(true, failed, "Nameplate character-save failure surfaces");
            AssertEqual(true, characterBefore.SequenceEqual(test.Characters.LastSuccessfulReplacementBson!), "Failed nameplate save grants no durable ownership");
            AssertEqual(0, test.Session.character.Nameplates.Count, "Failed nameplate save restores live ownership");
            Grant();
            Character owned = BsonSerializer.Deserialize<Character>(test.Characters.LastSuccessfulReplacementBson!);
            AssertEqual(plate.Id, owned.Nameplates.Single().Id, "Nameplate grant persists actual entitlement, not just receipt");
            AssertEqual(0L, owned.Nameplates.Single().EndTime, "Authored permanent nameplate does not expire");
            AssertEqual(true, owned.Nameplates.Single().GetTime > 0, "Nameplate persists acquisition time");
            Grant();
            AssertEqual(1, test.Session.character.Nameplates.Count, "Repeated nameplate grant does not duplicate entitlement");
            test.Call(nameof(WearNameplateRequest), new WearNameplateRequest { NameplateId = plate.Id });
            test.Relog("equipped-nameplate");
            AssertEqual(plate.Id, test.Session.character.BuildNameplateLoginData().CurrentWearNameplate, "Equipped owned nameplate survives BSON/login projection");
            test.Call(nameof(WearNameplateRequest), new WearNameplateRequest { NameplateId = 0 });
            AssertEqual(0, BsonSerializer.Deserialize<Character>(test.Characters.LastSuccessfulReplacementBson!).CurrentWearNameplate, "Unequip persists while retaining ownership");
            AssertEqual(1, test.Session.character.Nameplates.Count, "Unequip retains entitlement");
            test.Session.character.Nameplates.Clear();
            test.SaveFixture();
            AssertEqual(true, test.Session.character.AppliedRewardClaims.Contains(key) && test.Session.inventory.AppliedRewardClaims.Contains(key), "Legacy fixture retains both old no-op grant receipts");
            Grant();
            AssertEqual(plate.Id, BsonSerializer.Deserialize<Character>(test.Characters.LastSuccessfulReplacementBson!).Nameplates.Single().Id, "Legacy receipts without ownership repair real entitlement");
        }
    }

    private static void ValidateGodfallBfrtMixedNameplateReward()
    {
        using GodfallCase test = new("bfrt-mixed-nameplate-reward");
        var course = TableReaderV2.Parse<AscNet.Table.V2.share.fuben.bfrt.BfrtCourseRewardTable>().Single(row => row.RewardIds == 62328);
        var source = ResolveRewardGoods(course.RewardIds, TableReaderV2.Parse<RewardGoodsTable>(), "Bfrt mixed nameplate reward");
        AssertEqual(true, source.Select(row => (row.TemplateId, row.Count)).OrderBy(row => row.TemplateId)
            .SequenceEqual(new[] { (TemplateId: 102, Count: 50), (TemplateId: 17063001, Count: 1) }),
            "Bfrt course reward62328 contains both authored currency and nameplate");
        int previous = TableReaderV2.Parse<AscNet.Table.V2.share.fuben.bfrt.BfrtCourseRewardTable>()
            .Where(row => row.CourseStars < course.CourseStars).Max(row => row.CourseStars);
        int group = TableReaderV2.Parse<AscNet.Table.V2.share.fuben.bfrt.BfrtGroupTable>().First().GroupId;
        // Historical completed-course fixture: exercise the actual existing Bfrt claim handler.
        test.Session.player.Bfrt.CourseRewardStar = previous;
        test.Session.player.Bfrt.Groups = [new BfrtGroupState { Id = group, Count = course.CourseStars }];
        test.Session.character.Nameplates.Clear();
        test.SaveFixture();
        var before = test.Session.inventory.Items.ToDictionary(item => item.Id, item => (long)item.Count);
        byte[] unclaimedPlayer = test.Players.LastSuccessfulReplacementBson!.ToArray();
        JObject claimed = test.Call(nameof(BfrtReceiveCourseRewardRequest), new BfrtReceiveCourseRewardRequest());
        AssertEqual(course.CourseStars, claimed.Value<int>("CourseRewardStar"), "Actual Bfrt claim advances course cursor");
        var responseGoods = claimed["RewardGoodsList"]!.ToObject<List<RewardGoods>>()!;
        AssertEqual(true, source.Select(row => (row.TemplateId, row.Count)).OrderBy(row => row.TemplateId)
            .SequenceEqual(responseGoods.Select(row => (row.TemplateId, row.Count)).OrderBy(row => row.TemplateId)),
            "Actual Bfrt response returns both parts of the mixed reward");
        GodfallAssertMetaItems(test, before, [course.RewardIds]);
        long acquired = test.Session.character.Nameplates.Single(value => value.Id == 17063001).GetTime;
        AssertEqual(course.CourseStars, BsonSerializer.Deserialize<Player>(test.Players.LastSuccessfulReplacementBson!).Bfrt.CourseRewardStar,
            "Bfrt mixed reward persists the course claim cursor");
        test.Relog("bfrt-mixed-nameplate");
        AssertEqual(17063001, test.Session.character.Nameplates.Single().Id, "Bfrt global nameplate ownership survives BSON reload");
        AssertEqual(20113018, test.Call(nameof(BfrtReceiveCourseRewardRequest), new BfrtReceiveCourseRewardRequest(), success: false).Value<int>("Code"),
            "Already claimed Bfrt mixed reward cannot grant again");
        GodfallAssertMetaItems(test, before, [course.RewardIds]);

        // Durable reward receipts with a stale player cursor model interruption between
        // the inventory/character commit and Bfrt's subsequent player acknowledgement.
        test.Session.player.Bfrt = BsonSerializer.Deserialize<Player>(unclaimedPlayer).Bfrt;
        test.SaveFixture();
        test.ReloadDurable();
        test.Call(nameof(BfrtReceiveCourseRewardRequest), new BfrtReceiveCourseRewardRequest());
        GodfallAssertMetaItems(test, before, [course.RewardIds]);
        AssertEqual(acquired, test.Session.character.Nameplates.Single(value => value.Id == 17063001).GetTime,
            "Bfrt interrupted-ack retry retains acquisition time and exactly one entitlement");
        AssertEqual(course.CourseStars, BsonSerializer.Deserialize<Player>(test.Players.LastSuccessfulReplacementBson!).Bfrt.CourseRewardStar,
            "Bfrt interrupted-ack retry durably completes the cursor without paying twice");
    }

    private static void ValidateGodfallForeignTaskPendingGuard()
    {
        using GodfallCase test = new("foreign-task-next-day-pending");
        test.Start();
        var freeze = new XTheatre5ShopFreezeRequest { InstanceId = -1, IsFreeze = true };
        int saves = 0;
        test.Players.BeforeReplaceOne = _ =>
        {
            if (++saves == 2) throw new MongoDB.Driver.MongoException("Injected non-task Godfall acknowledgement failure.");
        };
        try { test.Call(nameof(XTheatre5ShopFreezeRequest), freeze, success: false); }
        finally { test.Players.BeforeReplaceOne = null; }
        AssertEqual(true, test.State.PendingMutation != null, "Foreign task fixture retains a real non-task owner journal");

        long today = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 86_400;
        test.Session.player.MissionProgress.DailyResetDay = today - 1;
        test.Session.player.MissionProgress.ConditionCounters[2003] = 4;
        int story = TableReaderV2.Parse<AscNet.Table.V2.share.fuben.StageTable>()
            .First(row => row.Type == 11 && row.StageType == 2).StageId;
        test.Session.stage.Stages.Remove(story);
        test.Session.player.MissionProgress.ClaimedTaskIds.Remove(3001699);
        test.SaveFixture();
        byte[] playerBefore = test.Session.player.ToBson();
        byte[] stageBefore = test.Session.stage.ToBson();
        byte[] inventoryBefore = test.Session.inventory.ToBson();
        byte[] characterBefore = test.Session.character.ToBson();
        byte[] savedPlayerBefore = test.Players.LastSuccessfulReplacementBson!.ToArray();

        DispatchStory(1);
        AssertUnchanged();
        AssertEqual(1, test.Call(nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = 3001699 }, success: false).Value<int>("Code"),
            "Foreign single task emits explicit busy response instead of replaying a non-task journal");
        AssertEqual(0, test.Pushes.Count, "Busy single task emits no pending owner pushes");
        AssertUnchanged();
        AssertEqual(1, test.Call(nameof(FinishMultiTaskRequest), new FinishMultiTaskRequest { TaskIds = [3001699] }, success: false).Value<int>("Code"),
            "Foreign multi task emits explicit busy response instead of ReplayPending exception");
        AssertEqual(0, test.Pushes.Count, "Busy multi task emits no pending owner pushes");
        AssertUnchanged();

        test.Call(nameof(XTheatre5ShopFreezeRequest), freeze);
        AssertEqual(true, test.State.PendingMutation == null, "Only matching non-task owner recovers pending intent");
        DispatchStory(0);
        AssertEqual(today, test.Session.player.MissionProgress.DailyResetDay, "Unblocked real dispatcher resets daily epoch before FightModule progress");
        AssertEqual(1, test.Session.player.MissionProgress.ConditionCounters.GetValueOrDefault(2003),
            "Today's accepted story clear records one fresh daily progress, not yesterday's four plus one");
        AssertEqual(true, test.Session.stage.Stages[story].Passed, "Unblocked actual FightModule story handler accepts stage clear");
        var saved = BsonSerializer.Deserialize<Player>(test.Players.LastSuccessfulReplacementBson!);
        AssertEqual(today, saved.MissionProgress.DailyResetDay, "Accepted progress persists today's epoch");
        AssertEqual(1, saved.MissionProgress.ConditionCounters.GetValueOrDefault(2003), "Accepted daily progress survives player BSON");

        void AssertUnchanged()
        {
            AssertEqual(true, playerBefore.SequenceEqual(test.Session.player.ToBson()), "Foreign pending request cannot mutate old-epoch tasks or owner journal");
            AssertEqual(true, stageBefore.SequenceEqual(test.Session.stage.ToBson()), "Foreign pending Fight request cannot persist a story clear");
            AssertEqual(true, inventoryBefore.SequenceEqual(test.Session.inventory.ToBson()), "Foreign pending request cannot debit or reward inventory");
            AssertEqual(true, characterBefore.SequenceEqual(test.Session.character.ToBson()), "Foreign pending request cannot mutate character entitlements");
            AssertEqual(true, savedPlayerBefore.SequenceEqual(test.Players.LastSuccessfulReplacementBson!), "Foreign pending request leaves durable player unchanged");
        }

        void DispatchStory(int code)
        {
            const int packetId = 990001;
            MethodInfo dispatch = RequiredMethod(typeof(Session), "InvokeRequestHandler", BindingFlags.Instance | BindingFlags.NonPublic,
                [typeof(RequestPacketHandlerDelegate), typeof(Packet.Request)]);
            dispatch.Invoke(test.Session, [GetRegisteredRequestHandler(nameof(EnterStoryRequest)), new Packet.Request
            {
                Id = packetId, Name = nameof(EnterStoryRequest),
                Content = MessagePack.MessagePackSerializer.Serialize(new EnterStoryRequest { StageId = story })
            }]);
            while (true)
            {
                Packet packet = test.Harness.ReadPacket("Godfall foreign story dispatch");
                if (packet.Type == Packet.ContentType.Push)
                {
                    AssertEqual(0, code, "Busy foreign Fight request sends no progress or owner pushes");
                    continue;
                }
                AssertEqual(Packet.ContentType.Response, packet.Type, "Foreign Fight request returns a response");
                var response = MessagePack.MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                AssertEqual(packetId, response.Id, "Foreign Fight response preserves correlation");
                AssertEqual(nameof(EnterStoryResponse), response.Name, "Foreign Fight busy response uses the expected protocol name");
                AssertEqual(code, MessagePack.MessagePackSerializer.Deserialize<EnterStoryResponse>(response.Content).Code, "Foreign Fight dispatcher result");
                break;
            }
            while (test.Harness.TryReadAvailablePacket("Godfall foreign story post-hooks", out _))
                AssertEqual(0, code, "Busy foreign Fight request skips all post-hooks");
        }
    }

    private static void ValidateGodfallForeignMonthlyShopPendingGuard()
    {
        using GodfallCase test = new("foreign-monthly-shop-pending");
        // Authored Shop950/95001: 4500x103 buys one45, monthly limit10, clock10004.
        // Fund this isolated account before starting the run, enough for two batches
        // so the second denial must come from the purchase limit rather than balance.
        test.Session.inventory.Do(103, 90000);
        test.SaveFixture();
        test.Start();
        var freeze = new XTheatre5ShopFreezeRequest { InstanceId = -1, IsFreeze = true };
        int saves = 0;
        test.Players.BeforeReplaceOne = _ =>
        {
            if (++saves == 2) throw new MongoDB.Driver.MongoException("Injected monthly-shop pending owner acknowledgement failure.");
        };
        try { test.Call(nameof(XTheatre5ShopFreezeRequest), freeze, success: false); }
        finally { test.Players.BeforeReplaceOne = null; }
        AssertEqual(true, test.State.PendingMutation != null, "Monthly shop fixture retains non-task Godfall pending intent");
        var shopType = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.ShopModule");
        object?[] window = [10004, DateTimeOffset.UtcNow, 0L, 0L];
        AssertEqual(true, (bool)shopType.GetMethod("TryGetAlarmWindow", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, window)!,
            "Authored monthly shop clock resolves an active epoch");
        long period = (long)window[2]!;
        test.Session.player.ShopResetPeriods[10004] = DateTimeOffset.FromUnixTimeSeconds(period).AddMonths(-1).ToUnixTimeSeconds();
        test.Session.player.ShopBuyTimes[95001] = 0;
        test.SaveFixture();
        byte[] pending = test.State.PendingMutation!.ToBson();
        byte[] inventoryBefore = test.Session.inventory.ToBson();
        byte[] playerBefore = test.Players.LastSuccessfulReplacementBson!.ToArray();
        long currency = test.Balance(103);
        long reward = test.Balance(45);
        var buy = new BuyRequest { ShopId = 950, GoodsId = 95001, Count = 10 };
        AssertEqual(1, test.Call(nameof(BuyRequest), buy, success: false).Value<int>("Code"), "Foreign monthly purchase is busy while non-task Godfall owns the journal");
        AssertEqual(true, inventoryBefore.SequenceEqual(test.Session.inventory.ToBson()), "Busy monthly purchase neither spends45000 nor grants10 goods");
        AssertEqual(true, playerBefore.SequenceEqual(test.Players.LastSuccessfulReplacementBson!), "Busy monthly purchase cannot record purchases against stale epoch");
        AssertEqual(0, test.Session.player.ShopBuyTimes.GetValueOrDefault(95001u), "Busy monthly purchase leaves old-period count zero");

        AssertEqual(1, test.Call(nameof(GetShopInfoRequest), new GetShopInfoRequest { Id = 950 }, success: false).Value<int>("Code"),
            "Real Session rejects foreign monthly catalog request until its pending owner recovers");
        AssertEqual(true, playerBefore.SequenceEqual(test.Players.LastSuccessfulReplacementBson!), "Busy catalog request cannot reconcile or mutate the old monthly epoch");
        AssertEqual(true, pending.SequenceEqual(test.State.PendingMutation!.ToBson()), "Busy catalog request preserves pending Godfall intent");
        AssertEqual(true, inventoryBefore.SequenceEqual(test.Session.inventory.ToBson()), "Busy catalog request cannot mutate inventory");
        test.Call(nameof(XTheatre5ShopFreezeRequest), freeze);
        AssertEqual(true, test.State.PendingMutation == null, "Matching owner recovers before foreign catalog access");
        JObject info = test.Call(nameof(GetShopInfoRequest), new GetShopInfoRequest { Id = 950 });
        JObject goods = ((JArray)info["ClientShop"]!["GoodsList"]!).Children<JObject>().Single(row => row.Value<int>("Id") == 95001);
        AssertEqual(10, goods.Value<int>("BuyTimesLimit"), "Actual monthly goods preserve authored limit10");
        AssertEqual(10004, goods.Value<int>("AutoResetClockId"), "Actual monthly goods use authored clock10004");
        AssertEqual(period, test.Session.player.ShopResetPeriods[10004], "Unblocked real shop info reconciles its monthly epoch after owner recovery");
        AssertEqual(period, BsonSerializer.Deserialize<Player>(test.Players.LastSuccessfulReplacementBson!).ShopResetPeriods[10004], "Monthly epoch reconciliation persists");
        AssertEqual(0, goods.Value<int>("TotalBuyTimes"), "Reconciled monthly catalog reports zero purchases");
        AssertEqual(true, inventoryBefore.SequenceEqual(test.Session.inventory.ToBson()), "Catalog reconciliation grants no goods and charges nothing");

        test.Call(nameof(BuyRequest), buy);
        AssertEqual(currency - 45000, test.Balance(103), "First accepted monthly batch pays authored45000 currency");
        AssertEqual(reward + 10, test.Balance(45), "First accepted monthly batch grants exactly10 goods");
        AssertEqual(10, BsonSerializer.Deserialize<Player>(test.Players.LastSuccessfulReplacementBson!).ShopBuyTimes.GetValueOrDefault(95001u),
            "Accepted monthly limit count persists in current epoch");
        JObject afterPurchase = test.Call(nameof(GetShopInfoRequest), new GetShopInfoRequest { Id = 950 });
        AssertEqual(10, ((JArray)afterPurchase["ClientShop"]!["GoodsList"]!).Children<JObject>().Single(row => row.Value<int>("Id") == 95001).Value<int>("TotalBuyTimes"),
            "Repeated current-period catalog lookup retains consumed monthly limit instead of resetting purchases");
        test.Relog("foreign-monthly-limit");
        AssertEqual(1, test.Call(nameof(BuyRequest), buy, success: false).Value<int>("Code"), "Another10 monthly goods cannot be bought after pending recovery and relog");
        AssertEqual(currency - 45000, test.Balance(103), "Over-limit retry preserves remaining currency despite sufficient balance");
        AssertEqual(reward + 10, test.Balance(45), "Old-epoch pending sequence never permits a second monthly batch");
        AssertEqual(10, test.Session.player.ShopBuyTimes.GetValueOrDefault(95001u), "Monthly purchase limit remains consumed");
    }

    private static void ValidateGodfallLocalNameplatePolicy()
    {
        using GodfallCase test = new("approved-local-nameplate-policy");
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Character character = test.Session.character;
        character.Nameplates.Clear();
        character.CurrentWearNameplate = 0;
        Check(character.GrantNameplate(17050101, 1, now), 17050101, now, now + 1296000, "Type3 fresh tier");
        character.CurrentWearNameplate = 17050101;
        Check(character.GrantNameplate(17050101, 1, now + 10), 17050102, now, now + 10 + 2592000, "Type3 second copy consumes authored EXP threshold");
        AssertEqual(17050102, character.CurrentWearNameplate, "Equipped active nameplate follows promoted tier");
        Check(character.GrantNameplate(17050101, 1, now + 20), 17050103, now, now + 20 + 3888000, "Type3 third copy reaches terminal tier");
        Check(character.GrantNameplate(17050101, 1, now + 30), 17050103, now, now + 30 + 3888000, "Type3 terminal copy retains no excess EXP");
        character.Nameplates.Clear();
        character.CurrentWearNameplate = 0;
        Check(character.GrantNameplate(17050101, 3, now), 17050103, now, now + 3888000, "Type3 three-copy batch matches sequential tier advancement");
        long expiredAt = character.Nameplates.Single().EndTime;
        character.CurrentWearNameplate = 17050103;
        Check(character.GrantNameplate(17050101, 1, expiredAt), 17050101, expiredAt, expiredAt + 1296000, "Expired tier resets at exact expiry boundary");
        AssertEqual(0, character.CurrentWearNameplate, "Expired equipped tier is cleared rather than transferred to renewed tier");
        test.SaveFixture();
        Check(BsonSerializer.Deserialize<Character>(test.Characters.LastSuccessfulReplacementBson!).Nameplates.Single(),
            17050101, expiredAt, expiredAt + 1296000, "Expired tier reset survives actual BSON persistence");

        character.Nameplates.Clear();
        Check(character.GrantNameplate(17050113, 1, now), 17050113, now, now + 604800, "Type1 timed fresh grant");
        Check(character.GrantNameplate(17050113, 1, now + 10), 17050113, now, now + 10 + 604800, "Type1 timed renewal refreshes from grant time");
        Check(character.GrantNameplate(17050113, 1, now + 5), 17050113, now, now + 10 + 604800, "Older Type1 grant clock never shortens active expiry");
        test.SaveFixture();
        Check(BsonSerializer.Deserialize<Character>(test.Characters.LastSuccessfulReplacementBson!).Nameplates.Single(),
            17050113, now, now + 10 + 604800, "Type1 renewal survives actual BSON persistence");

        character.Nameplates.Clear();
        long grantClock = now - 3600;
        character.GrantNameplate(17050101, 1, grantClock - 10);
        const string key = "godfall-local-nameplate:durable-clock";
        // Historical persisted grant-clock fixture: wall time is deliberately later,
        // so replay using UtcNow would observably extend the frozen expiry.
        test.Session.inventory.RewardClaimTimes[key] = grantClock;
        test.SaveFixture();
        Type handler = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.RewardHandler");
        Type grantType = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.RewardGrant");
        var reward = TableReaderV2.Parse<RewardGoodsTable>().First(row => row.TemplateId == 17050101 && row.Count == 1);
        Array grants = Array.CreateInstance(grantType, 1);
        grants.SetValue(Activator.CreateInstance(grantType, key, new[] { reward }, null, null), 0);
        MethodInfo apply = handler.GetMethod("ApplyRewardsOnceAndPersist", BindingFlags.Static | BindingFlags.Public)!;
        test.Characters.ThrowOnReplaceOne = true;
        bool failed = false;
        try { _ = apply.Invoke(null, [grants, test.Session]); }
        catch (TargetInvocationException exception) when (exception.InnerException is MongoDB.Driver.MongoException) { failed = true; }
        finally { test.Characters.ThrowOnReplaceOne = false; }
        AssertEqual(true, failed, "Type3 grant reaches failed character persistence boundary");
        Inventory savedInventory = BsonSerializer.Deserialize<Inventory>(test.Inventories.LastSuccessfulReplacementBson!);
        AssertEqual(grantClock, savedInventory.RewardClaimTimes[key], "Inventory grant receipt retains historical grant clock");
        AssertEqual(true, savedInventory.AppliedRewardClaims.Contains(key), "Type3 partial commit persists inventory receipt before character retry");
        test.ReloadDurable();
        _ = apply.Invoke(null, [grants, test.Session]);
        Check(BsonSerializer.Deserialize<Character>(test.Characters.LastSuccessfulReplacementBson!).Nameplates.Single(),
            17050102, grantClock - 10, grantClock + 2592000, "Retried Type3 grant consumes one copy using frozen clock");
        byte[] committed = test.Characters.LastSuccessfulReplacementBson!.ToArray();
        _ = apply.Invoke(null, [grants, test.Session]);
        AssertEqual(true, committed.SequenceEqual(test.Characters.LastSuccessfulReplacementBson!), "Committed ApplyOnce replay neither adds EXP nor refreshes expiry");
        Check(test.Session.character.Nameplates.Single(), 17050102, grantClock - 10, grantClock + 2592000, "Committed Type3 replay retains exact tier and clock");

        static void Check(NameplateData plate, int id, long acquired, long expires, string label)
        {
            AssertEqual(id, plate.Id, label + ": tier");
            AssertEqual(0, plate.Exp, label + ": consumed or terminal EXP");
            AssertEqual(acquired, plate.GetTime, label + ": acquisition time");
            AssertEqual(expires, plate.EndTime, label + ": expiry");
        }
    }
}
