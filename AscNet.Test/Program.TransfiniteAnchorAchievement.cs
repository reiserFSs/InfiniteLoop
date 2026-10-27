using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.fuben.transfinite;
using AscNet.Table.V2.share.item;
using TaskTable = AscNet.Table.V2.share.task.TaskTable;

namespace AscNet.Test;

internal partial class Program
{
    // Player reports: a Clash Reflection run started at an anchor did not pay the full Creation Operators (the
    // anchor-skipped stages were not counted), and a High Power Zone player could not claim the Low Power Zone
    // achievement row, which the achievement panel says clearing the High Power Zone also unlocks.
    private static void ValidateTransfiniteAnchorAchievementCompatibility()
    {
        PacketFactory.LoadPacketHandlers();
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out _, out _, out _);
        var stages = TableReaderV2.Parse<TransfiniteStageTable>().ToDictionary(row => row.StageId);
        var groups = TableReaderV2.Parse<TransfiniteStageGroupTable>().ToDictionary(row => row.StageGroupId);
        var taskGroups = TableReaderV2.Parse<TransfiniteTaskGroupTable>().ToDictionary(row => row.Id);
        var tasks = TableReaderV2.Parse<TaskTable>().ToDictionary(row => row.Id);
        var conditions = TableReaderV2.Parse<AscNet.Table.V2.share.task.ConditionTable>().ToDictionary(row => row.Id);
        var achievements = TableReaderV2.Parse<TransfiniteAchievementTable>();
        TransfiniteActivityTable activity = TableReaderV2.Parse<TransfiniteActivityTable>().First();
        long maxScore = Inventory.GetMaxCount(TableReaderV2.Parse<ItemTable>().Single(item => item.Id == 105));
        const int fastClear = 1;

        // The achievement task (of the given type) for this group whose condition is met by clearing `stageId`.
        int TaskFor(int groupId, int type, int stageId) => achievements
            .Where(row => row.Type == type && row.StageGroupId.Contains(groupId))
            .SelectMany(row => taskGroups[row.Id].TaskIds)
            .First(id => conditions.TryGetValue(tasks[id].Condition, out var c) && c.Type == 103000 && c.Params.Skip(1).Contains(stageId));

        long uid = 48_990;
        foreach (TransfiniteRegionTable region in TableReaderV2.Parse<TransfiniteRegionTable>().Where(row => row.RegionId is 1 or 2))
        {
            uid++;
            int groupId = TableReaderV2.Parse<TransfiniteRotateGroupTable>().Single(row => row.RotateGroupId == region.RotateGroupId)
                .StageGroupId.First(id => groups[id].StageId.Count == 15
                    && achievements.Any(row => row.Type == 1 && row.StageGroupId.Contains(id))
                    && achievements.Any(row => row.Type == 2 && row.StageGroupId.Contains(id)));
            List<int> stageIds = groups[groupId].StageId;
            // Two distinct anchors (TransfiniteStartStageProgress StartProgress values).
            int anchor = region.RegionId == 2 ? 8 : 5;
            Player player = CreateDrawCompatibilityPlayer(uid);
            player.Transfinite = new TransfiniteState
            {
                ActivityId = activity.Id,
                ActivityAuthorizedUntil = long.MaxValue,
                CircleId = 1,
                RegionId = region.RegionId,
                ScoreRewardGroupId = region.ScoreRewardGroupId,
                StageGroupId = groupId,
                BattleInfo = new TransfiniteBattleState { StageGroupId = groupId, StageProgressIndex = anchor - 1, StartStageProgress = anchor }
            };
            using LoopbackSessionHarness h = new(CreateDrawCompatibilityCharacter(uid), player,
                CreateDrawCompatibilityInventory(uid, []), "transfinite-anchor-achievement");
            int packetId = 48_900;
            TransfiniteConfirmBattleResultResponse? last = null;
            for (int index = anchor - 1; index < stageIds.Count; index++)
            {
                player.Transfinite.BattleInfo!.LastResult = new TransfiniteBattleResultState { LastWinStageId = stageIds[index], StageSpendTime = fastClear };
                InvokeRegisteredRequestHandler(nameof(TransfiniteConfirmBattleResultRequest), h.Session, packetId,
                    new TransfiniteConfirmBattleResultRequest { StageGroupId = groupId });
                last = ReadStrongholdResponse<TransfiniteConfirmBattleResultResponse>(h, packetId++, out _);
                AssertEqual(0, last.Code, $"region {region.RegionId} confirm stage {index + 1}");
            }

            int expected = stageIds.Sum(id => (stages[id].Score ?? 0)
                + (stages[id].ExtraScore is > 0 && stages[id].ExtraTimeLimit > fastClear ? stages[id].ExtraScore!.Value : 0));
            AssertEqual(expected, last!.RewardGoodsList.Single(goods => goods.TemplateId == 105).Count,
                $"anchor {anchor} run pays every stage of the group, skipped ones included");
            AssertEqual(Math.Min(expected, maxScore), h.Session.inventory.Items.Single(item => item.Id == 105).Count,
                "Creation Operators reach the capped total");

            int clearedStage = stageIds[anchor];
            int normalTask = TaskFor(groupId, 1, clearedStage);
            // XViewModelTransfiniteAchievement pairs the rows by position: the Senior task beside this Normal task.
            List<int> TaskIds(int type) => achievements.Where(row => row.Type == type && row.StageGroupId.Contains(groupId))
                .SelectMany(row => taskGroups[row.Id].TaskIds).Order().ToList();
            int seniorTask = TaskIds(2)[TaskIds(1).IndexOf(normalTask)];
            InvokeRegisteredRequestHandler("FinishMultiTaskRequest", h.Session, packetId,
                new FinishMultiTaskRequest { TaskIds = [normalTask] });
            AssertEqual(0, ReadStrongholdResponse<FinishMultiTaskResponse>(h, packetId++, out _).Code,
                $"region {region.RegionId} claims the Low Power Zone achievement");
            InvokeRegisteredRequestHandler("FinishMultiTaskRequest", h.Session, packetId,
                new FinishMultiTaskRequest { TaskIds = [seniorTask] });
            AssertEqual(0, ReadStrongholdResponse<FinishMultiTaskResponse>(h, packetId++, out _).Code, "senior claim request accepted");
            // FinishMultiTask reports unclaimable ids in NotDealTaskIds with Code 0; the claim ledger is the outcome.
            AssertEqual(region.RegionId == 2, player.MissionProgress.ClaimedTaskIds.Contains(seniorTask),
                "only the High Power Zone can claim the High Power Zone row");
            AssertEqual(true, player.MissionProgress.ClaimedTaskIds.Contains(normalTask), "Low Power Zone claim persists");

            // Challenge Reward "Eternal Observation II": a 103002 win-streak task whose target is the full 15 stages.
            int streakTask = taskGroups[region.TaskGroupId].TaskIds
                .First(id => conditions.TryGetValue(tasks[id].Condition, out var c) && c.Type == 103002 && tasks[id].Result == stageIds.Count);
            InvokeRegisteredRequestHandler("FinishMultiTaskRequest", h.Session, packetId,
                new FinishMultiTaskRequest { TaskIds = [streakTask] });
            _ = ReadStrongholdResponse<FinishMultiTaskResponse>(h, packetId++, out _);
            AssertEqual(true, player.MissionProgress.ClaimedTaskIds.Contains(streakTask),
                $"a full {stageIds.Count}-stage run completes the {stageIds.Count}-win streak challenge");
        }
        Console.WriteLine("Transfinite anchor score and achievement rows compatibility passed.");
    }
}
