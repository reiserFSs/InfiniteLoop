using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.theatre3;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Newtonsoft.Json.Linq;
using System.Reflection;
using Character = AscNet.Common.Database.Character;
using Inventory = AscNet.Common.Database.Inventory;

namespace AscNet.Test;

internal partial class Program
{
    private static readonly HashSet<string> Theatre3Calls = [];

    private static void ValidateTheatre3Compatibility()
    {
        // Registration is deliberately checked before referencing the new provider:
        // an old server fails for the missing protocol, not a reflection implementation detail.
        _ = GetRegisteredRequestHandler(nameof(Theatre3SelectDifficultyRequest));
        ValidateTheatre3LifecycleCompatibility();
        ValidateTheatre3NodeCompatibility();
        ValidateTheatre3MixedTaskRecovery();
        ValidateTheatre3CrossModeRecovery();
        ValidateTheatre3EquipmentCompatibility();
        ValidateTheatre3MetaCompatibility();
        ValidateTheatre3CombatCompatibility();
        ValidateTheatre3EffectCompatibility();
        using (Theatre3Case test = new("complete-adventure"))
        {
            test.Start();
            int beforeExp = test.Data.TotalBattlePassExp;
            int limit = TableReaderV2.Parse<Theatre3NodeTable>().Count * 128;
            for (int transition = 0; test.Data.CurChapterId > 0 && transition < limit; transition++)
            {
                AdvanceTheatre3(test);
                if (test.Data.CurChapterId > 0 && transition % 17 == 0) test.Relog("complete-adventure");
            }
            AssertEqual(0, test.Data.CurChapterId, "Theatre3 offered-choice run reaches its ending");
            Theatre3SettleData settle = test.State.LastSettle
                ?? throw new InvalidDataException("Complete Theatre3 run did not persist settlement.");
            AssertEqual(beforeExp + settle.BPExp, test.Data.TotalBattlePassExp, "Complete adventure credits exact settlement experience once");
            AssertEqual(true, settle.ChapterCount > 0 && settle.FightNodeCount > 0 && settle.EndId > 0, "Complete adventure resolves battles and an ending");
            AssertEqual(true, test.State.SettledRunId == test.State.RunId, "Complete adventure settles its own run");
            ValidateTheatre3TerminalReplay(test);
            test.Repeat(nameof(Theatre3SettleAdventureRequest), null, "Complete settlement replay");
            AssertEqual(20203002, JObject.Parse(MessagePackSerializer.ConvertToJson(test.LastResponseContent)).Value<int>("Code"), "New packet cannot re-settle an inactive adventure");
            test.Relog("completed");
        }
        string[] requests = typeof(Theatre3SelectDifficultyRequest).Assembly.GetTypes()
            .Where(type => type.Namespace == typeof(Theatre3SelectDifficultyRequest).Namespace
                && type.Name.StartsWith("Theatre3", StringComparison.Ordinal)
                && type.Name.EndsWith("Request", StringComparison.Ordinal))
            .Select(type => type.Name).Order().ToArray();
        AssertEqual("", string.Join(",", requests.Where(name => !Theatre3Calls.Contains(name))),
            "Every active Theatre3 RPC receives a correlated protocol response");
        Console.WriteLine($"Theatre3 compatibility: {requests.Length} RPCs, complete adventure, durable choices, economy bounds and recovery passed.");
    }

    private static void ValidateTheatre3LifecycleCompatibility()
    {
        using Theatre3Case test = new("lifecycle");
        JObject login = JObject.Parse(MessagePackSerializer.ConvertToJson(MessagePackSerializer.Serialize(test.Login())));
        RequiredValue<bool>(login, "ChapterSwitch", JTokenType.Boolean, "Theatre3 login");
        RequiredValue<int>(login, "FirstPassFlag", JTokenType.Integer, "Theatre3 login");
        test.Reject(nameof(Theatre3SelectDifficultyRequest), new Theatre3SelectDifficultyRequest { Difficulty = int.MaxValue }, "Unknown difficulty");
        test.Reject(nameof(Theatre3EndRecruitRequest), null, "Recruit completion without adventure");
        test.Reject(nameof(Theatre3SetTeamRequest), new Theatre3SetTeamRequest(), "Empty team without adventure");
        test.Start();
        AssertEqual(true, test.Data.EquipPos.All(pos => pos.PosId is >= 1 and <= 3), "Recruitment exposes positional identities");
        test.Relog("offered map");
        test.Reject(nameof(Theatre3SelectDifficultyRequest), new Theatre3SelectDifficultyRequest { Difficulty = test.Data.DifficultyId }, "Cannot reset active adventure");
        test.Reject(nameof(Theatre3SetTeamRequest), new Theatre3SetTeamRequest(), "Empty team cannot erase recruits");
        test.Call(nameof(Theatre3SettleAdventureRequest));
        AssertEqual(0, test.Data.CurChapterId, "Abandon closes adventure");
        test.Repeat(nameof(Theatre3SettleAdventureRequest), null, "Abandon settlement replay");
        test.Relog("abandoned");
    }

    private static Theatre3Step Theatre3FixtureStep(Theatre3Case test, int type)
    {
        // Explicit offered-state branch control; no captured runtime payloads.
        foreach (Theatre3Step old in test.Data.CurChapterDb!.Steps.Where(old => old.StepType != 2)) old.Overdue = 1;
        test.State.Fight = null;
        Theatre3Step step = new() { Uid = ++test.State.NextUid, StepType = type };
        test.Data.CurChapterDb.Steps.Add(step);
        return step;
    }

    private static void ValidateTheatre3EquipmentCompatibility()
    {
        using Theatre3Case test = new("equipment");
        test.Start();
        int itemId = TableReaderV2.Parse<Theatre3ItemTable>()
            .First(row => !(row.UnlockConditionId > 0) && test.Data.Items.All(item => item.ItemId != row.Id)).Id;
        Theatre3Step itemStep = Theatre3FixtureStep(test, 4);
        itemStep.ItemIds = [itemId];
        test.SaveFixture();
        test.Relog("item offer");
        test.Reject(nameof(Theatre3SelectItemRewardRequest), new Theatre3SelectItemRewardRequest { InnerItemId = int.MaxValue }, "Unoffered item");
        test.Call(nameof(Theatre3SelectItemRewardRequest), new Theatre3SelectItemRewardRequest { InnerItemId = itemId });
        AssertEqual(true, test.Data.Items.Any(item => item.ItemId == itemId), "Selected offered item enters inventory");
        test.Repeat(nameof(Theatre3SelectItemRewardRequest), new Theatre3SelectItemRewardRequest { InnerItemId = itemId }, "Item cannot be granted twice");

        var box = TableReaderV2.Parse<Theatre3EquipBoxTable>().First(row => row.Type != 2 && row.RefreshCostNum > 0);
        var equipRows = TableReaderV2.Parse<Theatre3EquipTable>();
        var first = equipRows.First();
        Theatre3Step step = Theatre3FixtureStep(test, 6);
        step.EquipBoxId = box.Id;
        step.EquipIds = [first.Id];
        step.FreeRefreshLimit = 1;
        step.RefreshDiscount = 0.5;
        test.Fund(96189, 0);
        test.SaveFixture();
        test.Reject(nameof(Theatre3SelectEquipRequest), new Theatre3SelectEquipRequest { SelectId = int.MaxValue, Pos = 1 }, "Unoffered equipment");
        test.Call(nameof(Theatre3RefreshEquipBoxRequest));
        AssertEqual(0L, test.Balance(96189), "Free refresh does not spend");
        test.Relog("refreshed equipment");
        test.Reject(nameof(Theatre3RefreshEquipBoxRequest), null, "Insufficient refresh currency preserves offers");
        int refreshCost = (int)Math.Floor(box.RefreshCost[0] * 0.5);
        test.Fund(96189, refreshCost);
        test.SaveFixture();
        test.Call(nameof(Theatre3RefreshEquipBoxRequest));
        AssertEqual(0L, test.Balance(96189), "Discounted refresh spends exact table price");
        byte[] refreshed = test.Step.ToBson();
        test.Call(nameof(Theatre3RefreshEquipBoxRequest), reusePacketId: test.LastPacketId);
        AssertEqual(true, refreshed.SequenceEqual(test.Step.ToBson()), "Refresh retry preserves offered identities");
        int offered = test.Step.EquipIds.First();
        test.Call(nameof(Theatre3SelectEquipRequest), new Theatre3SelectEquipRequest { SelectId = offered, Pos = 1 });
        AssertEqual(true, test.Data.Equips.Any(equip => equip.EquipId == offered && equip.Pos == 1), "Equipment selection binds to requested valid position");

        var a = equipRows.First();
        var b = equipRows.First(row => row.SuitId != a.SuitId && equipRows.Count(other => other.SuitId == row.SuitId) > 1);
        void Workshop(int type)
        {
            var row = TableReaderV2.Parse<Theatre3WorkShopTable>().First(value => value.Type == type);
            Theatre3Step work = Theatre3FixtureStep(test, 5);
            work.WorkShopId = row.Id;
            work.WorkShopType = type;
            work.WorkShopTotalCount = 1;
            test.Data.Equips = [new() { EquipId = a.Id, SuitId = a.SuitId, Pos = 1 }, new() { EquipId = b.Id, SuitId = b.SuitId, Pos = 2 }];
            test.SaveFixture();
        }
        Workshop(1);
        test.Reject(nameof(Theatre3RecastEquipRequest), new Theatre3RecastEquipRequest { SrcEquipId = int.MaxValue, DstSuitId = b.SuitId }, "Recast missing source");
        JObject recast = test.Call(nameof(Theatre3RecastEquipRequest), new Theatre3RecastEquipRequest { SrcEquipId = a.Id, DstSuitId = b.SuitId });
        int destination = recast.Value<int>("DstEquipId");
        AssertEqual(true, equipRows.Any(row => row.Id == destination && row.SuitId == b.SuitId) && test.Data.Equips.All(equip => equip.EquipId != a.Id), "Recast removes source and creates a real destination piece");
        test.Reject(nameof(Theatre3RecastEquipRequest), new Theatre3RecastEquipRequest { SrcEquipId = destination, DstSuitId = a.SuitId }, "Exhausted workshop");
        test.Call(nameof(Theatre3EndWorkShopRequest));
        Workshop(2);
        test.Call(nameof(Theatre3ChangeEquipPosRequest), new Theatre3ChangeEquipPosRequest { SrcSuitId = a.SuitId, SrcPos = 1, DstSuitId = b.SuitId, DstPos = 2 });
        AssertEqual(2, test.Data.Equips.Single(equip => equip.EquipId == a.Id).Pos, "Exchange moves source suit");
        AssertEqual(1, test.Data.Equips.Single(equip => equip.EquipId == b.Id).Pos, "Exchange moves destination suit atomically");
        test.Reject(nameof(Theatre3ChangeEquipPosRequest), new Theatre3ChangeEquipPosRequest { SrcSuitId = a.SuitId, SrcPos = 2, DstSuitId = b.SuitId, DstPos = 1 }, "Exchange usage exhausted");
        Workshop(3);
        test.Call(nameof(Theatre3QubitEquipRequest), new Theatre3QubitEquipRequest { EquipId = a.SuitId });
        AssertEqual(true, test.Data.Equips.Where(equip => equip.SuitId == a.SuitId).All(equip => equip.QubitActive), "Quantum activation updates owned suit");
        JObject quantum = JObject.Parse(MessagePackSerializer.ConvertToJson(MessagePackSerializer.Serialize(test.Data.Equips.First())));
        RequiredValue<bool>(quantum, "QubitActive", JTokenType.Boolean, "Quantum equipment");
        test.Reject(nameof(Theatre3QubitEquipRequest), new Theatre3QubitEquipRequest { EquipId = a.SuitId }, "Quantum cannot consume twice");
        test.State.EffectCounters["CurBuyCount"] = 3;
        test.State.EffectCounters["TotalRecvCoin"] = 275;
        test.State.EffectCounters[$"suit:{a.SuitId}:fight"] = 7;
        test.State.EffectCounters[$"suit:{a.SuitId}:boss"] = 2;
        test.Data.Equips.First(equip => equip.EquipId == a.Id).PassFightCount = 4;
        test.SaveFixture();
        byte[] beforeLook = test.State.ToBson();
        int savesBeforeLook = test.Players.ReplaceOneCalls;
        JObject genericLook = test.Call(nameof(Theatre3LookEquipAttributeRequest), new Theatre3LookEquipAttributeRequest());
        AssertEqual(3, genericLook.Value<int>("CurBuyCount"), "Generic attribute lookup exposes purchase count");
        AssertEqual(275, genericLook.Value<int>("TotalRecvCoin"), "Generic attribute lookup exposes received currency");
        JObject ownedLook = test.Call(nameof(Theatre3LookEquipAttributeRequest), new Theatre3LookEquipAttributeRequest { EquipId = a.Id, SuitId = a.SuitId });
        AssertEqual(4, ownedLook["Equip"]!.Value<int>("PassFightCount"), "Owned equipment reports actual clears");
        AssertEqual(7, ownedLook["EquipSuit"]!.Value<int>("PassFightCount"), "Owned suit reports cumulative clears");
        AssertEqual(2, ownedLook["EquipSuit"]!.Value<int>("PassBossFightCount"), "Owned suit reports boss clears");
        test.Reject(nameof(Theatre3LookEquipAttributeRequest), new Theatre3LookEquipAttributeRequest { EquipId = int.MaxValue }, "Unowned equipment attributes");
        test.Reject(nameof(Theatre3LookEquipAttributeRequest), new Theatre3LookEquipAttributeRequest { SuitId = -1 }, "Negative suit attributes");
        AssertEqual(true, beforeLook.SequenceEqual(test.State.ToBson()), "Attribute lookups never mutate persisted counters or receipts");
        AssertEqual(savesBeforeLook, test.Players.ReplaceOneCalls, "Read-only attribute lookups do not persist");
        Theatre3FixtureStep(test, 7).EquipIds = [first.Id];
        test.SaveFixture();
        test.Call(nameof(Theatre3EndEquipBoxRequest));
        test.Relog("equipment workshop state");
        int nestedChoice = equipRows.First(row => row.SuitId == a.SuitId && row.Id != a.Id).Id;
        test.Data.Equips = [new() { EquipId = a.Id, SuitId = a.SuitId, Pos = 1 }];
        Theatre3Step older = Theatre3FixtureStep(test, 6);
        older.EquipBoxId = box.Id;
        older.EquipIds = [nestedChoice, b.Id];
        var newer = new Theatre3Step { Uid = ++test.State.NextUid, StepType = 6, EquipBoxId = box.Id, EquipIds = [nestedChoice] };
        test.Data.CurChapterDb!.Steps.Add(newer);
        test.SaveFixture();
        long coinsBeforeNested = test.Balance(96189);
        test.Call(nameof(Theatre3SelectEquipRequest), new Theatre3SelectEquipRequest { SelectId = nestedChoice, Pos = 1 });
        AssertEqual(older.Uid, test.Step.Uid, "Nested equipment choice resumes the original offered step identity");
        AssertEqual(true, test.Step.EquipIds.Contains(b.Id) && !test.Step.EquipIds.Contains(nestedChoice), "Resumed box preserves valid offer and removes newly owned choice");
        AssertEqual(0, test.Step.RefreshTimes, "Automatic stale-choice reconciliation is not a paid refresh");
        AssertEqual(coinsBeforeNested, test.Balance(96189), "Nested offer reconciliation does not spend");
        test.Relog("reconciled nested equipment offers");
    }

    private static void ValidateTheatre3MetaCompatibility()
    {
        using Theatre3Case test = new("meta");
        var tree = TableReaderV2.Parse<Theatre3StrengthenTreeTable>().First(row => row.PreId.All(id => id == 0) && !(row.Condition > 0));
        test.Fund(96187, tree.NeedStrengthenPoint - 1);
        test.SaveFixture();
        test.Reject(nameof(Theatre3ActivationStrengthenTreeRequest), new Theatre3ActivationStrengthenTreeRequest { Id = tree.Id }, "Talent cannot overdraft");
        test.Fund(96187, tree.NeedStrengthenPoint);
        test.SaveFixture();
        test.Call(nameof(Theatre3ActivationStrengthenTreeRequest), new Theatre3ActivationStrengthenTreeRequest { Id = tree.Id });
        AssertEqual(0L, test.Balance(96187), "Talent uses exact authoritative price");
        AssertEqual(true, test.Data.UnlockStrengthTree.Contains(tree.Id), "Talent unlock is persisted");
        test.Repeat(nameof(Theatre3ActivationStrengthenTreeRequest), new Theatre3ActivationStrengthenTreeRequest { Id = tree.Id }, "Talent repeated purchase");
        var levels = TableReaderV2.Parse<Theatre3BattlePassTable>().OrderBy(row => row.Level).Take(3).ToList();
        test.Data.TotalBattlePassExp = levels[0].NeedExp - 1;
        test.SaveFixture();
        test.Reject(nameof(Theatre3GetBattlePassRewardRequest), new Theatre3GetBattlePassRewardRequest { GetRewardType = 1, Id = levels[0].Level }, "Battle pass threshold");
        test.Data.TotalBattlePassExp++;
        test.SaveFixture();
        Theatre3AssertReward(test, nameof(Theatre3GetBattlePassRewardRequest),
            new Theatre3GetBattlePassRewardRequest { GetRewardType = 1, Id = levels[0].Level }, [levels[0].RewardId]);
        test.Repeat(nameof(Theatre3GetBattlePassRewardRequest), new Theatre3GetBattlePassRewardRequest { GetRewardType = 1, Id = levels[0].Level }, "Battle pass single claim replay");
        test.Reject(nameof(Theatre3GetBattlePassRewardRequest), new Theatre3GetBattlePassRewardRequest { GetRewardType = 9, Id = levels[0].Level }, "Invalid claim selector");

        // Fail each durable boundary, then submit a different request: recovery must
        // finish the frozen grant AND execute that new request, not silently consume it.
        foreach (int boundary in new[] { 0, 1, 2 })
        {
            using Theatre3Case recovery = new($"reward-recovery-{boundary}");
            recovery.Data.TotalBattlePassExp = levels.Sum(row => row.NeedExp);
            recovery.Fund(96187, tree.NeedStrengthenPoint);
            recovery.SaveFixture();
            var before = recovery.Session.inventory.Items.ToDictionary(item => item.Id, item => item.Count);
            if (boundary == 0) recovery.Inventories.ThrowOnReplaceOne = true;
            if (boundary == 1) recovery.Characters.ThrowOnReplaceOne = true;
            if (boundary == 2) recovery.Players.BeforeReplaceOne = player =>
            {
                if (player.Theatre3.PendingMutation is null) throw new MongoDB.Driver.MongoException("Injected final Theatre3 acknowledgement failure.");
            };
            try
            {
                recovery.Call(nameof(Theatre3GetBattlePassRewardRequest), new Theatre3GetBattlePassRewardRequest { GetRewardType = 2 }, success: false);
                AssertEqual(0, recovery.Pushes.Count, "Failed reward transaction sends no success payload");
            }
            finally
            {
                recovery.Inventories.ThrowOnReplaceOne = false;
                recovery.Characters.ThrowOnReplaceOne = false;
                recovery.Players.BeforeReplaceOne = null;
            }
            AssertEqual(true, recovery.State.PendingMutation != null, "Failed commit leaves durable recovery intent");
            recovery.Call(nameof(Theatre3ActivationStrengthenTreeRequest), new Theatre3ActivationStrengthenTreeRequest { Id = tree.Id });
            AssertEqual(true, recovery.Data.UnlockStrengthTree.Contains(tree.Id), "Recovery executes next actual request");
            Theatre3AssertInventoryRewards(recovery, before, levels.Select(row => row.RewardId), 96187);
            AssertEqual(true, levels.All(row => recovery.Data.GetRewardIds.Contains(row.Level)), "Recovery finalizes original claim-all");
            recovery.Relog("reward recovery");
            recovery.Repeat(nameof(Theatre3GetBattlePassRewardRequest), new Theatre3GetBattlePassRewardRequest { GetRewardType = 2 }, "Recovered grant cannot duplicate");
        }
        var activity = TableReaderV2.Parse<Theatre3ActivityTable>().Single(row => row.Id == test.Data.CurActivityId);
        test.Reject(nameof(Theatre3GetAchievementRewardRequest), new Theatre3GetAchievementRewardRequest { NeedCountId = 1 }, "Achievement count gate");
        test.Session.player.MissionProgress.ClaimedTaskIds.AddRange(TableReaderV2.Parse<Theatre3AchievementTable>()
            .SelectMany(row => row.TaskIds).Distinct().Take(activity.NeedCounts[0]));
        test.SaveFixture();
        Theatre3AssertReward(test, nameof(Theatre3GetAchievementRewardRequest),
            new Theatre3GetAchievementRewardRequest { NeedCountId = 1 }, [activity.RewardIds[0]]);
        test.Repeat(nameof(Theatre3GetAchievementRewardRequest), new Theatre3GetAchievementRewardRequest { NeedCountId = 1 }, "Achievement replay");
        var task = TableReaderV2.Parse<AscNet.Table.V2.share.task.TaskTable>().Single(row => row.Id == 91301);
        test.Reject(nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = task.Id }, "Incomplete Theatre3 task");
        test.State.TaskProgress[task.Condition] = task.Result ?? 1;
        test.SaveFixture();
        Theatre3AssertReward(test, nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = task.Id }, [task.RewardId ?? 0]);
        test.Repeat(nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = task.Id }, "Claimed Theatre3 task");
        test.Relog("meta and task claims");
    }

    private static void Theatre3AssertReward(Theatre3Case test, string request, object body, IEnumerable<int> rewards)
    {
        var before = test.Session.inventory.Items.ToDictionary(item => item.Id, item => item.Count);
        test.Call(request, body);
        Theatre3AssertInventoryRewards(test, before, rewards);
    }

    private static void Theatre3AssertInventoryRewards(Theatre3Case test, IReadOnlyDictionary<int, long> before, IEnumerable<int> rewards, int excludedItem = 0, IEnumerable<int>? currentRewards = null)
    {
        var items = TableReaderV2.Parse<AscNet.Table.V2.share.item.ItemTable>().Select(row => row.Id).ToHashSet();
        var goods = rewards.Where(id => id > 0).SelectMany(id => ResolveRewardGoods(id,
            TableReaderV2.Parse<AscNet.Table.V2.share.reward.RewardGoodsTable>(), "Theatre3 rewards")).ToList();
        foreach (int id in currentRewards ?? [])
        {
            var reward = TableReaderV2.Parse<AscNet.Table.V2.share.task.CurrentRewardTable>().Single(row => row.Id == id);
            goods.AddRange(TableReaderV2.Parse<AscNet.Table.V2.share.task.CurrentRewardGoodsTable>()
                .Where(row => reward.SubIds.Contains(row.Id)).Select(row => new AscNet.Table.V2.share.reward.RewardGoodsTable
                { Id = row.Id, TemplateId = row.TemplateId, Count = row.Count }));
        }
        foreach (var group in goods.Where(row => items.Contains(row.TemplateId) && row.TemplateId != excludedItem).GroupBy(row => row.TemplateId))
            AssertEqual(before.GetValueOrDefault(group.Key) + group.Sum(row => (long)row.Count), test.Balance(group.Key),
                $"Reward item {group.Key} is additive exactly once");
    }
    private static void ValidateTheatre3MixedTaskRecovery()
    {
        using Theatre3Case test = new("mixed-task-recovery");
        // Existing achievement fixture: current task3660 is type4, condition10101
        // requires account level10; this case's level70 actually earns it.
        var ordinary = TableReaderV2.Parse<AscNet.Table.V2.share.task.CurrentTaskTable>().Single(row => row.Id == 3660);
        test.Session.player.MissionProgress.ClaimedTaskIds.Remove(ordinary.Id);
        AssertEqual(true, test.Session.player.PlayerData.Level >= ordinary.Result, "Ordinary mixed-task level objective is earned");
        var biancaIds = TableReaderV2.Parse<AscNet.Table.V2.client.biancatheatre.BiancaTheatreTaskTable>().SelectMany(row => row.TaskId).ToHashSet();
        var tasks = TableReaderV2.Parse<AscNet.Table.V2.share.task.TaskTable>();
        var bianca = tasks.First(row => biancaIds.Contains(row.Id) && row.RewardId > 0);
        var theatre = tasks.Single(row => row.Id == 91301);
        test.Session.player.BiancaTheatre.TaskProgress[bianca.Condition] = bianca.Result ?? 1;
        test.State.TaskProgress[theatre.Condition] = theatre.Result ?? 1;
        int[] ids = [ordinary.Id, theatre.Id, bianca.Id];
        test.Session.player.MissionProgress.ClaimedTaskIds.RemoveAll(ids.Contains);
        test.SaveFixture();
        var before = test.Session.inventory.Items.ToDictionary(item => item.Id, item => item.Count);
        FinishMultiTaskRequest request = new() { TaskIds = ids.ToList() };
        test.Players.BeforeReplaceOne = player =>
        {
            if (player.Theatre3.PendingMutation == null) throw new MongoDB.Driver.MongoException("Injected mixed task acknowledgement failure.");
        };
        try { test.Call(nameof(FinishMultiTaskRequest), request, success: false); }
        finally { test.Players.BeforeReplaceOne = null; }
        AssertEqual(true, test.State.PendingMutation != null, "Mixed batch retains one recovery journal");
        JObject recovered = test.Call(nameof(FinishMultiTaskRequest), request, reusePacketId: test.LastPacketId);
        JObject replay = test.Call(nameof(FinishMultiTaskRequest), request, reusePacketId: test.LastPacketId);
        AssertEqual(true, JToken.DeepEquals(recovered, replay), "Mixed batch exact retry returns frozen response");
        Theatre3AssertInventoryRewards(test, before, [theatre.RewardId ?? 0, bianca.RewardId ?? 0], currentRewards: [ordinary.RewardId]);
        AssertEqual(true, ids.All(test.Session.player.MissionProgress.ClaimedTaskIds.Contains), "All three task domains commit once");
        test.Relog("mixed task claims");
        test.Repeat(nameof(FinishMultiTaskRequest), request, "Mixed batch new packet cannot grant again");
    }


    private static void ValidateTheatre3CrossModeRecovery()
    {
        var tasks = TableReaderV2.Parse<AscNet.Table.V2.share.task.TaskTable>();
        var biancaIds = TableReaderV2.Parse<AscNet.Table.V2.client.biancatheatre.BiancaTheatreTaskTable>().SelectMany(row => row.TaskId).ToHashSet();
        var bianca = tasks.First(row => biancaIds.Contains(row.Id) && row.RewardId > 0);
        var theatre = tasks.Single(row => row.Id == 91301);
        foreach (bool biancaFirst in new[] { true, false })
        {
            using Theatre3Case test = new($"cross-journal-{biancaFirst}");
            test.Session.player.BiancaTheatre.TaskProgress[bianca.Condition] = bianca.Result ?? 1;
            test.State.TaskProgress[theatre.Condition] = theatre.Result ?? 1;
            test.SaveFixture();
            var before = test.Session.inventory.Items.ToDictionary(item => item.Id, item => item.Count);
            test.Players.BeforeReplaceOne = player =>
            {
                if (biancaFirst ? player.BiancaTheatre.PendingMutation == null : player.Theatre3.PendingMutation == null)
                    throw new MongoDB.Driver.MongoException("Injected cross-mode final player failure.");
            };
            try
            {
                if (biancaFirst) test.Call(nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = bianca.Id }, success: false);
                else test.Call(nameof(FinishMultiTaskRequest), new FinishMultiTaskRequest { TaskIds = [theatre.Id, bianca.Id] }, success: false);
            }
            finally { test.Players.BeforeReplaceOne = null; }
            AssertEqual(true, biancaFirst ? test.Session.player.BiancaTheatre.PendingMutation != null : test.State.PendingMutation != null,
                "Foreign claim is durably pending before domain handoff");
            if (biancaFirst)
            {
                JObject mixed = test.Call(nameof(FinishMultiTaskRequest), new FinishMultiTaskRequest { TaskIds = [theatre.Id, bianca.Id] });
                AssertIntegerList([theatre.Id], RequiredValue<JArray>(mixed, "SuccessTaskIds", JTokenType.Array, "Mixed task recovery")
                    .Select(value => value.Value<long>()).ToArray(), "Mixed recovery claims still-unclaimed Theatre3 task");
                AssertIntegerList([bianca.Id], RequiredValue<JArray>(mixed, "NotDealTaskIds", JTokenType.Array, "Mixed task recovery")
                    .Select(value => value.Value<long>()).ToArray(), "Mixed recovery reports already-recovered Bianca task without repaying");
                AssertEqual(true, test.Session.player.MissionProgress.ClaimedTaskIds.Contains(theatre.Id), "New mixed intent executes after foreign recovery");
            }
            else
            {
                byte[] pendingResponse = test.State.PendingMutation?.Response
                    ?? throw new InvalidDataException("Reciprocal task recovery requires its frozen mixed result.");
                FinishMultiTaskResponse planned = MessagePackSerializer.Deserialize<FinishMultiTaskResponse>(pendingResponse);
                AssertEqual(true, planned.SuccessTaskIds.Contains(theatre.Id), "Frozen mixed intent includes earned Theatre3 claim");
                JObject next = test.Call(nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = bianca.Id }, success: null);
                AssertEqual(planned.SuccessTaskIds.Contains(bianca.Id) ? 20026006 : 0, next.Value<int>("Code"),
                    "Bianca request observes recovered mixed claim before planning new reward");
            }
            AssertEqual(true, test.Session.player.BiancaTheatre.PendingMutation == null && test.State.PendingMutation == null, "Foreign pending claim finishes before planning another domain");
            Theatre3AssertInventoryRewards(test, before, [bianca.RewardId ?? 0, theatre.RewardId ?? 0]);
            AssertEqual(true, test.Session.player.MissionProgress.ClaimedTaskIds.Contains(bianca.Id)
                && test.Session.player.MissionProgress.ClaimedTaskIds.Contains(theatre.Id), "Cross-domain recovery commits both intended claims without duplicates");
            test.Relog("cross journal");
        }
        using Theatre3Case rollover = new("pending-daily-reset");
        // Current daily login task, authored from 2025-11-13 onward, condition2000
        // type10102 evaluates a real logged-in session to1 without forged counters.
        var daily = TableReaderV2.Parse<AscNet.Table.V2.share.task.CurrentTaskTable>().Single(row => row.Id == 3001699);
        _ = BuildTaskData(rollover.Session);
        rollover.Session.player.MissionProgress.ClaimedTaskIds.Remove(daily.Id);
        rollover.State.TaskProgress[theatre.Condition] = theatre.Result ?? 1;
        rollover.SaveFixture();
        rollover.Inventories.ThrowOnReplaceOne = true;
        try { rollover.Call(nameof(FinishMultiTaskRequest), new FinishMultiTaskRequest { TaskIds = [daily.Id, theatre.Id] }, success: false); }
        finally { rollover.Inventories.ThrowOnReplaceOne = false; }
        Theatre3PendingMutation pending = rollover.State.PendingMutation
            ?? throw new InvalidDataException("Daily rollover fixture requires persisted pending task intent.");
        // Move only the saved calendar-bound intent to yesterday; this is a BSON
        // historical-state fixture, not a runtime clock override or forged reward.
        long today = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 86_400;
        rollover.Session.player.MissionProgress.DailyResetDay = today - 1;
        foreach (var grant in pending.Grants.Where(grant => grant.ClaimKey.StartsWith($"current-task:{daily.Id}:", StringComparison.Ordinal)))
            grant.ClaimKey = $"current-task:{daily.Id}:{today - 1}";
        rollover.SaveFixture();
        var dailyBefore = rollover.Session.inventory.Items.ToDictionary(item => item.Id, item => item.Count);
        _ = BuildTaskData(rollover.Session);
        while (rollover.Harness.TryReadAvailablePacket("rollover recovered owed push", out _)) { }
        AssertEqual(today, rollover.Session.player.MissionProgress.DailyResetDay, "Pending old journal is reconciled into today's calendar");
        AssertEqual(false, rollover.Session.player.MissionProgress.ClaimedTaskIds.Contains(daily.Id), "Yesterday's restored claim cannot consume today's daily claim");
        Theatre3AssertInventoryRewards(rollover, dailyBefore, [theatre.RewardId ?? 0],
            excludedItem: Inventory.DailyActiveness, currentRewards: [daily.RewardId]);
        AssertEqual(0L, rollover.Balance(Inventory.DailyActiveness), "Yesterday's recovered daily activeness is cleared by today's reset");
        rollover.SaveFixture();
        var todayBefore = rollover.Session.inventory.Items.ToDictionary(item => item.Id, item => item.Count);
        rollover.Call(nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = daily.Id });
        Theatre3AssertInventoryRewards(rollover, todayBefore, [], currentRewards: [daily.RewardId]);
        AssertEqual(5L, rollover.Balance(Inventory.DailyActiveness), "Today's login claim earns fresh daily activeness");
        rollover.Repeat(nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = daily.Id }, "Today's daily reward cannot be credited twice");
    }
    private static Theatre3Step Theatre3FixtureNode(Theatre3Case test, Theatre3NodeSlot slot)
    {
        foreach (var old in test.Data.CurChapterDb!.Steps) old.Overdue = 1;
        Theatre3Step step = Theatre3FixtureStep(test, 2);
        var node = TableReaderV2.Parse<Theatre3NodeTable>().First(row => row.ChapterId == test.Data.CurChapterId);
        slot.SlotId = ++test.State.NextUid;
        step.NodeData = new() { NodeId = node.Id, ChapterId = node.ChapterId, Selected = slot.Selected, Slots = [slot] };
        return step;
    }

    private static void ValidateTheatre3NodeCompatibility()
    {
        using Theatre3Case test = new("node-branches");
        test.Start();
        test.Reject(nameof(Theatre3SelectNodeRequest), new Theatre3SelectNodeRequest { NodeId = int.MaxValue, SlotId = int.MaxValue }, "Forged map identity");
        test.Reject(nameof(Theatre3RecvFightRewardRequest), new Theatre3RecvFightRewardRequest { Uid = int.MaxValue }, "Reward without fight");
        test.Reject(nameof(Theatre3EndRecvFightRewardRequest), null, "Reward completion without fight");
        test.Reject(nameof(Theatre3EndNodeRequest), null, "End node requires selected shop");
        test.Reject(nameof(Theatre3SwitchParallelChapterRequest), new Theatre3SwitchParallelChapterRequest { ChapterId = int.MaxValue }, "Unoffered parallel chapter");
        var initial = TableReaderV2.Parse<Theatre3ItemGroupTable>().First(row => row.Id == 322);
        Theatre3Step prop = Theatre3FixtureStep(test, 9);
        prop.ItemIds = [0, initial.Id];
        test.SaveFixture();
        test.Reject(nameof(Theatre3SelectInitialItemRequest), new Theatre3SelectInitialItemRequest { ItemId = initial.ItemId }, "Initial item requires offered group row identity");
        test.Call(nameof(Theatre3SelectInitialItemRequest), new Theatre3SelectInitialItemRequest { ItemId = initial.Id });
        AssertEqual(true, test.Data.Items.Any(item => item.ItemId == initial.ItemId), "Initial choice grants group's underlying item");
        prop = Theatre3FixtureStep(test, 9);
        prop.ItemIds = [0, initial.Id];
        test.SaveFixture();
        byte[] items = MessagePackSerializer.Serialize(test.Data.Items);
        test.Call(nameof(Theatre3SelectInitialItemRequest), new Theatre3SelectInitialItemRequest());
        AssertEqual(true, items.SequenceEqual(MessagePackSerializer.Serialize(test.Data.Items)), "Explicit zero initial choice skips reward");
        Theatre3Step lucky = Theatre3FixtureStep(test, 8);
        lucky.DestinyCharacterIds = [TableReaderV2.Parse<Theatre3CharacterRecruitTable>().First().CharacterId];
        test.SaveFixture();
        test.Reject(nameof(Theatre3DestinySelectRequest), new Theatre3DestinySelectRequest { CharacterId = int.MaxValue }, "Unoffered destiny character");
        int destiny = lucky.DestinyCharacterIds[0];
        test.Call(nameof(Theatre3DestinySelectRequest), new Theatre3DestinySelectRequest { CharacterId = destiny });
        AssertEqual(destiny, test.Data.DestinyCharacterId, "Destiny selection preserves offered identity");

        var eventRow = TableReaderV2.Parse<Theatre3EventTable>().First(row => row.Type == 2
            && TableReaderV2.Parse<Theatre3EventOptionGroupTable>().Any(option => option.GroupId == row.OptionGroupId
                && option.Id > 3 && option.OptionType == 3 && !(option.OptionShowCondition > 0)));
        var eventOptions = TableReaderV2.Parse<Theatre3EventOptionGroupTable>().Where(row => row.GroupId == eventRow.OptionGroupId).OrderBy(row => row.Id).ToList();
        int optionIndex = eventOptions.FindIndex(row => row.OptionType == 3 && !(row.OptionShowCondition > 0)) + 1;
        var eventSlot = new Theatre3NodeSlot { SlotType = 2, EventId = eventRow.EventId, CurStepId = eventRow.StepId, Selected = 1 };
        Theatre3FixtureNode(test, eventSlot);
        test.SaveFixture();
        test.Relog("event branch");
        test.Reject(nameof(Theatre3EventNodeNextStepRequest), new Theatre3EventNodeNextStepRequest { CurEventStepId = eventRow.StepId, OptionId = eventOptions[optionIndex - 1].Id }, "Event option is index not table row id");
        test.Call(nameof(Theatre3EventNodeNextStepRequest), new Theatre3EventNodeNextStepRequest { CurEventStepId = eventRow.StepId, OptionId = optionIndex });
        AssertEqual(true, test.State.RunPassedEventStepIds.Contains(eventRow.StepId), "Event acknowledges source step transition");

        var shop = TableReaderV2.Parse<Theatre3NodeShopTable>().First(row => row.ShopType == 1);
        int shopItem = TableReaderV2.Parse<Theatre3ItemTable>().First(row => !(row.UnlockConditionId > 0)
            && (row.ObtainMaxCount <= 0 || test.Data.Items.Count(item => item.ItemId == row.Id) < row.ObtainMaxCount)).Id;
        var offer = new Theatre3ShopItem { Uid = ++test.State.NextUid, ItemType = 1, ItemId = shopItem, Price = 100, DiscountPrice = 75, DiscountPercent = 0.75 };
        Theatre3FixtureNode(test, new Theatre3NodeSlot { SlotType = 3, ShopId = shop.Id, Selected = 1, ShopItems = [offer] });
        test.Fund(96189, 74);
        test.SaveFixture();
        test.Relog("stable shop offer");
        test.Reject(nameof(Theatre3NodeShopBuyItemRequest), new Theatre3NodeShopBuyItemRequest { ShopItemUid = int.MaxValue }, "Shop UID forgery");
        test.Reject(nameof(Theatre3NodeShopBuyItemRequest), new Theatre3NodeShopBuyItemRequest { ShopItemUid = offer.Uid }, "Shop cannot partially spend");
        test.Fund(96189, 75);
        test.SaveFixture();
        test.Call(nameof(Theatre3NodeShopBuyItemRequest), new Theatre3NodeShopBuyItemRequest { ShopItemUid = offer.Uid });
        AssertEqual(0L, test.Balance(96189), "Shop charges persisted discounted offer");
        AssertEqual(true, test.Data.Items.Any(item => item.ItemId == shopItem), "Shop delivers offered item");
        test.Repeat(nameof(Theatre3NodeShopBuyItemRequest), new Theatre3NodeShopBuyItemRequest { ShopItemUid = offer.Uid }, "Shop repeated UID");
        test.Call(nameof(Theatre3EndNodeRequest));

        var chapter = TableReaderV2.Parse<Theatre3ChapterTable>().First(row => row.ConnectChapter > 0);
        var nodes = TableReaderV2.Parse<Theatre3NodeTable>();
        var sourceNode = nodes.First(row => row.ChapterId == chapter.Id && row.ConnectNode > 0);
        var destinationNode = nodes.Single(row => row.Id == sourceNode.ConnectNode);
        test.Data.CurChapterId = chapter.Id;
        test.Data.CurChapterDb!.ChapterId = chapter.Id;
        test.Data.CurChapterDb.ConnectChapterId = chapter.ConnectChapter ?? 0;
        Theatre3Step parallel = Theatre3FixtureNode(test, new Theatre3NodeSlot { SlotType = 3, ShopId = shop.Id });
        parallel.NodeData!.NodeId = sourceNode.Id;
        parallel.ConnectNodeData = new()
        {
            ChapterId = destinationNode.ChapterId,
            NodeId = destinationNode.Id,
            Slots = [new() { SlotId = ++test.State.NextUid, SlotType = 3, ShopId = shop.Id }]
        };
        test.Data.ChapterSwitch = true;
        test.SaveFixture();
        test.Call(nameof(Theatre3SwitchParallelChapterRequest), new Theatre3SwitchParallelChapterRequest { ChapterId = destinationNode.ChapterId });
        AssertEqual(destinationNode.ChapterId, test.Data.CurChapterId, "Quantum branch switches to offered parallel chapter");
        test.Relog("parallel chapter");
        test.Data.EndingRecord.Remove(5);
        test.SaveFixture();
        test.Reject(nameof(Theatre3SwitchParallelChapterRequest), new Theatre3SwitchParallelChapterRequest { ChapterId = chapter.Id }, "B-to-A requires ending five");
        AssertEqual(20203073, JObject.Parse(MessagePackSerializer.ConvertToJson(test.LastResponseContent)).Value<int>("Code"), "B-to-A positive unlock gate error");
        test.Data.EndingRecord.Add(5);
        test.SaveFixture();
        test.Call(nameof(Theatre3SwitchParallelChapterRequest), new Theatre3SwitchParallelChapterRequest { ChapterId = chapter.Id });
        AssertEqual(chapter.Id, test.Data.CurChapterId, "Ending five permits B-to-A return");
        test.Call(nameof(Theatre3SwitchParallelChapterRequest), new Theatre3SwitchParallelChapterRequest { ChapterId = destinationNode.ChapterId });
        test.Call(nameof(Theatre3SelectNodeRequest), new Theatre3SelectNodeRequest { NodeId = destinationNode.Id, SlotId = parallel.ConnectNodeData.Slots[0].SlotId });
        test.Reject(nameof(Theatre3SwitchParallelChapterRequest), new Theatre3SwitchParallelChapterRequest { ChapterId = chapter.Id }, "Cannot switch after choosing a branch node");
        // Source-defined singleton event pool must still fill its authored two slots.
        test.Data.DifficultyId = 2;
        test.Data.CurChapterId = 101;
        test.Data.EndingRecord.Remove(2);
        test.State.EncounterUseCounts.Clear();
        test.Data.CurChapterDb = new()
        {
            ChapterId = 101,
            PassNodeIds = nodes.Where(row => row.ChapterId == 101 && row.Id < 21).Select(row => row.Id).ToList()
        };
        Theatre3GenerateFixture(test);
        AssertEqual(21, test.Step.NodeData!.NodeId, "Normal difficulty reaches source singleton-pool node");
        AssertEqual(2, test.Step.NodeData.Slots.Count, "Authored two-slot node is not collapsed by singleton pool");
        AssertEqual(true, test.Step.NodeData.Slots.All(slot => slot.EventId == 2), "Both slots use the only eligible event");
        AssertEqual(2, test.Step.NodeData.Slots.Select(slot => slot.SlotId).Distinct().Count(), "Repeated event offers retain distinct selectable identities");
        test.Relog("singleton event pool");

        test.Data.CurChapterId = 9102;
        test.Data.CurChapterDb = new() { ChapterId = 102, ConnectChapterId = 9102, PassNodeIds = [22, 134] };
        Theatre3GenerateFixture(test);
        AssertEqual(135, test.Step.NodeData!.NodeId, "B path advances from node 134 to 135");
        AssertEqual(23, test.Step.ConnectNodeData!.NodeId, "B path retains reverse A partner");
        AssertEqual(102, test.Step.ConnectNodeData.ChapterId, "Reverse partner remains in A chapter");
        test.Relog("reverse B pair");
        test.Step.NodeData!.Selected = 1;
        test.Step.NodeData.Slots = [new() { SlotId = ++test.State.NextUid, SlotType = 3, ShopId = shop.Id, Selected = 1 }];
        test.State.EffectCounters["pendingSwitch"] = 1;
        test.SaveFixture();
        test.Call(nameof(Theatre3EndNodeRequest));
        AssertEqual(102, test.Data.CurChapterId, "Selected-node pending switch applies at completion before successor generation");
        AssertEqual(false, test.State.EffectCounters.ContainsKey("pendingSwitch"), "Boundary switch is consumed exactly once");
        test.Relog("pending boundary switch");
        AssertEqual(102, test.Data.CurChapterId, "Login cannot apply consumed boundary switch again");
        Theatre3GenerateFixture(test, 9103);
        AssertEqual(103, test.Data.CurChapterDb!.ConnectChapterId, "New B chapter discovers its authored reverse A chapter");
        AssertEqual(true, test.Step.ConnectNodeData?.ChapterId == 103, "New B chapter publishes the reverse-paired map");
        test.Relog("reverse B chapter transition");
    }

    private static void Theatre3GenerateFixture(Theatre3Case test, int chapterId = 0)
    {
        Type module = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.Theatre3Module");
        Type mutation = module.GetNestedType("Mutation", BindingFlags.NonPublic)!;
        object state = Activator.CreateInstance(mutation, BindingFlags.Instance | BindingFlags.NonPublic, null, [test.Session], null)!;
        if (chapterId == 0)
            RequiredMethod(module, "GenerateNextNode", BindingFlags.Static | BindingFlags.NonPublic, [mutation]).Invoke(null, [state]);
        else
        {
            var chapter = TableReaderV2.Parse<Theatre3ChapterTable>().Single(row => row.Id == chapterId);
            RequiredMethod(module, "StartChapter", BindingFlags.Static | BindingFlags.NonPublic,
                [mutation, typeof(Theatre3ChapterTable)]).Invoke(null, [state, chapter]);
        }
        test.Session.player.Theatre3 = (PlayerTheatre3State)mutation.GetProperty("State")!.GetValue(state)!;
        test.SaveFixture();
    }

    private static bool Theatre3Condition(Theatre3Case test, int condition)
    {
        Type module = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.Theatre3Module");
        Type mutation = module.GetNestedType("Mutation", BindingFlags.NonPublic)!;
        object state = Activator.CreateInstance(mutation, BindingFlags.Instance | BindingFlags.NonPublic, null, [test.Session], null)!;
        return (bool)RequiredMethod(module, "IsConditionSatisfied", BindingFlags.Static | BindingFlags.NonPublic,
            [mutation, typeof(int)]).Invoke(null, [state, condition])!;
    }

    private static void ValidateTheatre3TerminalReplay(Theatre3Case test)
    {
        string terminalName = test.LastRequestName;
        object? terminalRequest = test.LastRequest;
        int terminalId = test.LastPacketId;
        int serverDestiny = test.Data.DestinyValue;
        int serverPasses = test.Data.TotalAllPassCount;
        byte[] serverCharacters = MessagePackSerializer.Serialize(test.Data.Characters);
        int clientDestiny = serverDestiny, clientPasses = serverPasses;
        List<Theatre3Character> clientCharacters = test.Data.Characters;
        void Apply(Theatre3SettleData settle)
        {
            clientDestiny += settle.DestinyValue;
            if (TableReaderV2.Parse<Theatre3EndingTable>().Single(row => row.Id == settle.EndId).PassType == 2) clientPasses++;
            clientCharacters = settle.Characters;
            AssertEqual(true, clientCharacters.All(character => character.ExpTemp == 0), "Replayed mastery is final, without a second animation delta");
        }
        for (int retry = 0; retry < 2; retry++)
        {
            JObject response = test.Call(terminalName, terminalRequest, reusePacketId: terminalId);
            foreach (var push in test.Pushes)
            {
                if (push.Name == nameof(NotifyTheatre3ActivityData))
                {
                    var snapshot = MessagePackSerializer.Deserialize<Theatre3ActivityData>(push.Content);
                    clientDestiny = snapshot.DestinyValue;
                    clientPasses = snapshot.TotalAllPassCount;
                    clientCharacters = snapshot.Characters;
                }
                else if (push.Name == nameof(NotifyTheatre3AdventureSettle))
                    Apply(MessagePackSerializer.Deserialize<NotifyTheatre3AdventureSettle>(push.Content).SettleData);
            }
            if (response["SettleData"] is JObject settled) Apply(settled.ToObject<Theatre3SettleData>()!);
            AssertEqual(serverDestiny, clientDestiny, "Terminal replay additive destiny remains synchronized");
            AssertEqual(serverPasses, clientPasses, "Terminal replay additive pass count remains synchronized");
            AssertEqual(true, serverCharacters.SequenceEqual(MessagePackSerializer.Serialize(clientCharacters)), "Terminal replay preserves final mastery roster");
        }
        test.Relog("terminal recovery", pending: true);
        Theatre3ActivityData login = test.Login();
        clientDestiny = login.DestinyValue;
        clientPasses = login.TotalAllPassCount;
        Type module = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.Theatre3Module");
        RequiredMethod(module, "SendRecoveredSettlement", BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(Session)]).Invoke(null, [test.Session]);
        var recovery = ReadPushPayload<NotifyTheatre3AdventureSettle>(test.Harness, nameof(NotifyTheatre3AdventureSettle), "Theatre3 login settlement recovery");
        Apply(recovery.SettleData);
        AssertEqual(serverDestiny, clientDestiny, "Login settlement adds destiny only once");
        AssertEqual(serverPasses, clientPasses, "Login settlement adds pass count only once");
        AssertEqual(true, serverCharacters.SequenceEqual(MessagePackSerializer.Serialize(clientCharacters)), "Login settlement preserves final mastery");
    }

    private static void AdvanceTheatre3(Theatre3Case test)
    {
        Theatre3Step step = test.Step;
        switch (step.StepType)
        {
            case 8:
                test.Call(nameof(Theatre3DestinySelectRequest), new Theatre3DestinySelectRequest { CharacterId = step.DestinyCharacterIds.First() });
                break;
            case 9:
                test.Call(nameof(Theatre3SelectInitialItemRequest), new Theatre3SelectInitialItemRequest { ItemId = step.ItemIds.First() });
                break;
            case 4:
                test.Call(nameof(Theatre3SelectItemRewardRequest), new Theatre3SelectItemRewardRequest { InnerItemId = step.ItemIds.First() });
                break;
            case 5:
                test.Call(nameof(Theatre3EndWorkShopRequest));
                break;
            case 6:
            case 7:
                var equipment = TableReaderV2.Parse<Theatre3EquipTable>();
                var choice = (from id in step.EquipIds
                              from pos in test.Data.EquipPos
                              let row = equipment.FirstOrDefault(row => row.Id == id)
                              where step.EquipBoxType == 2 || row != null
                                  && (test.Data.Equips.Any(equip => equip.SuitId == row.SuitId && equip.Pos == pos.PosId)
                                      || test.Data.Equips.All(equip => equip.SuitId != row.SuitId)
                                          && test.Data.Equips.Where(equip => equip.Pos == pos.PosId).Select(equip => equip.SuitId).Distinct().Count() < pos.Capacity)
                              select (Id: id, Pos: pos.PosId)).FirstOrDefault();
                if (choice.Id == 0) test.Call(nameof(Theatre3EndEquipBoxRequest));
                else test.Call(nameof(Theatre3SelectEquipRequest), new Theatre3SelectEquipRequest { SelectId = choice.Id, Pos = choice.Pos });
                break;
            case 3:
                Theatre3NodeReward? reward = step.FightRewards.FirstOrDefault(reward => reward.Received == 0);
                if (reward == null) test.Call(nameof(Theatre3EndRecvFightRewardRequest));
                else test.Call(nameof(Theatre3RecvFightRewardRequest), new Theatre3RecvFightRewardRequest { Uid = reward.Uid });
                break;
            case 2:
                Theatre3NodeData node = new[] { step.NodeData, step.ConnectNodeData }.First(value => value?.ChapterId == test.Data.CurChapterId)!;
                Theatre3NodeSlot? slot = node.Slots.SingleOrDefault(value => value.Selected != 0);
                if (slot == null)
                {
                    // Prefer combat for a finite full-adventure route; other branch types
                    // have separate controlled fixtures rather than random coverage claims.
                    slot = node.Slots.OrderBy(value => value.SlotType == 1 ? 0 : value.SlotType).First();
                    test.Call(nameof(Theatre3SelectNodeRequest), new Theatre3SelectNodeRequest { NodeId = node.NodeId, SlotId = slot.SlotId });
                }
                else if (slot.SlotType == 1) FightTheatre3(test);
                else if (slot.SlotType == 3) test.Call(nameof(Theatre3EndNodeRequest));
                else
                {
                    var row = TableReaderV2.Parse<Theatre3EventTable>().Single(row => row.EventId == slot.EventId && row.StepId == slot.CurStepId);
                    if (row.Type == 4) { FightTheatre3(test); break; }
                    int optionIndex = 0;
                    if (row.Type == 2)
                    {
                        var options = TableReaderV2.Parse<Theatre3EventOptionGroupTable>().Where(option => option.GroupId == row.OptionGroupId).OrderBy(option => option.Id).ToList();
                        optionIndex = options.FindIndex(option => Theatre3Condition(test, option.OptionShowCondition ?? 0)
                            && (option.OptionType == 3 || option.OptionItemId.Select((id, index) =>
                                option.OptionItemType == 1 ? test.Balance(id) >= option.OptionItemCount.ElementAtOrDefault(index)
                                : test.Data.Items.Count(item => item.ItemId == id) >= option.OptionItemCount.ElementAtOrDefault(index)).All(ok => ok))) + 1;
                        AssertEqual(true, optionIndex > 0, $"Event {row.EventId}/{row.StepId} has an affordable offered option");
                    }
                    test.Call(nameof(Theatre3EventNodeNextStepRequest), new Theatre3EventNodeNextStepRequest { CurEventStepId = slot.CurStepId, OptionId = optionIndex });
                }
                break;
            default:
                throw new InvalidDataException($"Unexpected Theatre3 step {step.StepType}/{step.Uid} in complete adventure.");
        }
    }


    private sealed class Theatre3Case : IDisposable
    {
        private static long nextPlayerId = 99_100;
        private int packetId;
        private readonly MongoCollectionOverride collections;
        public readonly RecordingMongoCollectionProxy<Player> Players;
        public readonly RecordingMongoCollectionProxy<Character> Characters;
        public readonly RecordingMongoCollectionProxy<Inventory> Inventories;
        public readonly RecordingMongoCollectionProxy<Stage> Stages;
        public LoopbackSessionHarness Harness { get; private set; }
        public Session Session => Harness.Session;
        public PlayerTheatre3State State => Session.player.Theatre3;
        public Theatre3ActivityData Data => State.Data;
        public List<Packet.Push> Pushes { get; } = [];
        public byte[] LastResponseContent { get; private set; } = [];
        public Theatre3Step Step => Data.CurChapterDb?.Steps.LastOrDefault(step => step.Overdue == 0 && step.StepType is 6 or 7)
            ?? Data.CurChapterDb?.Steps.LastOrDefault(step => step.Overdue == 0)
            ?? throw new InvalidDataException("Adventure has no actionable step.");
        public int LastPacketId => packetId;
        public string LastRequestName { get; private set; } = "";
        public object? LastRequest { get; private set; }

        public Theatre3Case(string name)
        {
            collections = MongoCollectionOverride.InstallForBiancaCompatibility(out Players, out Characters, out Inventories, out Stages);
            long id = ++nextPlayerId;
            Harness = new(CreateDrawCompatibilityCharacter(id), CreateDrawCompatibilityPlayer(id),
                CreateDrawCompatibilityInventory(id, []), $"theatre3-{name}");
            Session.stage = CreateLoginAccountCompatibilityStage(id);
            Session.player.PlayerData.Level = 70;
            RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.Theatre3Module"), "PrepareLogin",
                BindingFlags.Static | BindingFlags.NonPublic, [typeof(Session)]).Invoke(null, [Session]);
            Login();
            _ = BuildTaskData(Session);
            SaveFixture();
        }

        public Theatre3ActivityData Login() => (Theatre3ActivityData)RequiredMethod(
            RequiredAscNetGameServerType("AscNet.GameServer.Handlers.Theatre3Module"), "BuildLoginData",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, [typeof(Session)])
            .Invoke(null, [Session])!;

        public void SaveFixture()
        {
            Session.player.SaveChecked();
            Session.character.SaveChecked();
            Session.inventory.SaveChecked();
        }

        public void Start()
        {
            int difficulty = TableReaderV2.Parse<Theatre3DifficultyTable>().OrderBy(row => row.Id).First().Id;
            Call(nameof(Theatre3SelectDifficultyRequest), new Theatre3SelectDifficultyRequest { Difficulty = difficulty });
            while (Step.StepType is 8 or 9) AdvanceTheatre3(this);
            var recruits = TableReaderV2.Parse<Theatre3CharacterRecruitTable>().OrderBy(row => row.GroupId).ThenBy(row => row.Id).Take(3).ToList();
            Call(nameof(Theatre3SetTeamRequest), new Theatre3SetTeamRequest
            {
                TeamData = new() { CaptainPos = 1, FirstFightPos = 1, CardIds = [0, 0, 0], RobotIds = recruits.Select(row => row.RobotId).ToList() },
                EquipPosInfos = recruits.Select((row, index) => new Theatre3EquipPosInfo { Pos = index + 1, ColorId = index + 1, RobotId = row.RobotId }).ToList()
            });
            Call(nameof(Theatre3EndRecruitRequest));
        }

        public JObject Call(string requestName, object? request = null, bool? success = true, int? reusePacketId = null)
        {
            Pushes.Clear();
            int id = reusePacketId ?? ++packetId;
            Theatre3Calls.Add(requestName);
            LastRequestName = requestName;
            LastRequest = request;
            InvokeRegisteredRequestHandler(requestName, Session, id, request);
            for (int index = 0; index < 256; index++)
            {
                Packet packet = Harness.ReadPacket($"{requestName} result {index}");
                if (packet.Type == Packet.ContentType.Push)
                {
                    Pushes.Add(MessagePackSerializer.Deserialize<Packet.Push>(packet.Content));
                    continue;
                }
                AssertEqual(Packet.ContentType.Response, packet.Type, $"{requestName} response packet type");
                Packet.Response response = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                AssertEqual(id, response.Id, $"{requestName} correlation");
                AssertEqual(requestName.Replace("Request", "Response", StringComparison.Ordinal), response.Name, $"{requestName} response name");
                LastResponseContent = response.Content;
                JObject body = JObject.Parse(MessagePackSerializer.ConvertToJson(response.Content));
                int code = RequiredValue<int>(body, "Code", JTokenType.Integer, requestName);
                if (success.HasValue) AssertEqual(success.Value, code == 0, $"{requestName} accepted={success}, actual code={code}");
                if (Harness.TryReadAvailablePacket($"{requestName} unexpected trailing packet", out Packet extra))
                    throw new InvalidDataException($"{requestName}: mutation push/duplicate response after response ({extra.Type}).");
                if (code == 0 && requestName.StartsWith("Theatre3", StringComparison.Ordinal))
                {
                    Player saved = BsonSerializer.Deserialize<Player>(Players.LastSuccessfulReplacementBson
                        ?? throw new InvalidDataException($"{requestName}: success without a durable player record."));
                    AssertEqual(true, State.ToBson().SequenceEqual(saved.Theatre3.ToBson()), $"{requestName} persists before acknowledgement");
                }
                return body;
            }
            throw new InvalidDataException($"{requestName}: no response within 256 packets.");
        }

        public void Reject(string requestName, object? request, string name)
        {
            byte[] before = State.ToBson();
            byte[] inventory = Session.inventory.ToBson();
            byte[] character = Session.character.ToBson();
            Call(requestName, request, success: false);
            AssertEqual(false, Pushes.Any(push =>
                !(requestName == nameof(FinishTaskRequest) && push.Name == nameof(NotifyTask))
                && !(requestName == nameof(Theatre3SelectDifficultyRequest) && push.Name == nameof(NotifyTheatre3ActivityData))),
                $"{name}: rejection emits no additive success pushes");
            if (requestName == nameof(Theatre3SelectDifficultyRequest))
            {
                var snapshot = Pushes.Single(push => push.Name == nameof(NotifyTheatre3ActivityData));
                Theatre3ActivityData restored = MessagePackSerializer.Deserialize<Theatre3ActivityData>(snapshot.Content);
                AssertEqual(true, Data.ToBson().SequenceEqual(restored.ToBson()), $"{name}: pre-response snapshot restores stable adventure");
            }
            AssertEqual(true, before.SequenceEqual(State.ToBson()), $"{name}: rejection preserves adventure");
            AssertEqual(true, inventory.SequenceEqual(Session.inventory.ToBson()), $"{name}: rejection preserves inventory");
            AssertEqual(true, character.SequenceEqual(Session.character.ToBson()), $"{name}: rejection preserves roster");
        }

        public void Repeat(string requestName, object? request, string name)
        {
            byte[] state = Data.ToBson();
            int nextUid = State.NextUid;
            byte[] inventory = Session.inventory.ToBson();
            byte[] roster = Session.character.ToBson();
            Call(requestName, request, success: null);
            AssertEqual(true, state.SequenceEqual(Data.ToBson()), $"{name}: repeat cannot advance client state");
            AssertEqual(nextUid, State.NextUid, $"{name}: repeat cannot allocate identities");
            AssertEqual(true, inventory.SequenceEqual(Session.inventory.ToBson()), $"{name}: repeat cannot change balances");
            AssertEqual(true, roster.SequenceEqual(Session.character.ToBson()), $"{name}: repeat cannot grant an account character");
        }

        public void Relog(string name, bool pending = false)
        {
            byte[] chapter = MessagePackSerializer.Serialize(Data.CurChapterDb);
            byte[] localCharacters = MessagePackSerializer.Serialize(Data.Characters);
            byte[] items = MessagePackSerializer.Serialize(Data.Items);
            int nextUid = State.NextUid;
            Player player = BsonSerializer.Deserialize<Player>(Players.LastSuccessfulReplacementBson!);
            Character character = BsonSerializer.Deserialize<Character>(Characters.LastSuccessfulReplacementBson ?? Session.character.ToBson());
            Inventory inventory = BsonSerializer.Deserialize<Inventory>(Inventories.LastSuccessfulReplacementBson ?? Session.inventory.ToBson());
            Stage stage = BsonSerializer.Deserialize<Stage>(Session.stage.ToBson());
            Harness.Dispose();
            Harness = new(character, player, inventory, $"theatre3-relog-{name}");
            Session.stage = stage;
            RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.Theatre3Module"), "PrepareLogin",
                BindingFlags.Static | BindingFlags.NonPublic, [typeof(Session)]).Invoke(null, [Session]);
            Theatre3ActivityData login = Login();
            if (!pending)
            {
                AssertEqual(true, chapter.SequenceEqual(MessagePackSerializer.Serialize(login.CurChapterDb)), $"{name}: login preserves choices/node/reward UIDs");
                AssertEqual(true, localCharacters.SequenceEqual(MessagePackSerializer.Serialize(login.Characters)), $"{name}: login preserves recruits and upgrades");
                AssertEqual(true, items.SequenceEqual(MessagePackSerializer.Serialize(login.Items)), $"{name}: login preserves item occurrences");
                AssertEqual(nextUid, State.NextUid, $"{name}: login does not reroll or allocate identities");
            }
            AssertEqual(Data.CurChapterId > 0, login.CurChapterDb is not null, $"{name}: chapter gate matches resumable snapshot");
        }

        public long Balance(int itemId) => Session.inventory.Items.SingleOrDefault(item => item.Id == itemId)?.Count ?? 0;
        public void Fund(int itemId, int count)
        {
            Item? item = Session.inventory.Items.SingleOrDefault(item => item.Id == itemId);
            if (item is null) Session.inventory.Items.Add(new Item { Id = itemId, Count = count });
            else item.Count = count;
        }
        public void Dispose() { Harness.Dispose(); collections.Dispose(); }
    }

}
