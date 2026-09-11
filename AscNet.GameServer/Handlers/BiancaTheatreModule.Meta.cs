using System.Globalization;
using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Table.V2.share.biancatheatre;
using AscNet.Table.V2.client.biancatheatre;
using AscNet.Table.V2.share.reward;
using AscNet.Table.V2.share.task;
using MessagePack;

namespace AscNet.GameServer.Handlers;

[MessagePackObject(true)] public sealed class BiancaTheatreSettleAdventureRequest { }
[MessagePackObject(true)] public sealed class BiancaTheatreSettleAdventureResponse : BiancaTheatreResponse { public BiancaTheatreSettleData? SettleData { get; set; } }
[MessagePackObject(true)] public sealed class BiancaTheatreStrengthenRequest { public int Id { get; set; } }
[MessagePackObject(true)] public sealed class BiancaTheatreStrengthenResponse : BiancaTheatreResponse { }
[MessagePackObject(true)] public sealed class BiancaTheatreGetRewardRequest { public int Id { get; set; } }
[MessagePackObject(true)] public sealed class BiancaTheatreGetRewardResponse : BiancaTheatreResponse { public List<RewardGoods> RewardGoodsList { get; set; } = []; }
[MessagePackObject(true)] public sealed class BiancaTheatreGetAllRewardResponse : BiancaTheatreResponse { public List<RewardGoods> RewardGoodsList { get; set; } = []; public List<int> GetRewardIds { get; set; } = []; }
[MessagePackObject(true)] public sealed class BiancaTheatreGetAchievementRewardRequest { public int NeedCountId { get; set; } }
[MessagePackObject(true)] public sealed class BiancaTheatreGetAchievementRewardResponse : BiancaTheatreResponse { public List<RewardGoods> RewardGoodsList { get; set; } = []; }
[MessagePackObject(true)] public sealed class BiancaTheatreNewStageResponse : BiancaTheatreResponse { }
[MessagePackObject(true)] public sealed class NotifyBiancaTheatreTotalExp { public int TotalExp { get; set; } }
[MessagePackObject(true)] public sealed class NotifyBiancaTheatreAdventureSettle { public BiancaTheatreSettleData SettleData { get; set; } = new(); }
[MessagePackObject(true)] public sealed class NotifyBiancaTheatreAchievementCondition { }

internal static partial class BiancaTheatreModule
{
    [RequestPacketHandler("BiancaTheatreSettleAdventureRequest")]
    public static void BiancaTheatreSettleAdventureRequestHandler(Session session, Packet.Request packet) =>
        Handle<BiancaTheatreSettleAdventureRequest, BiancaTheatreSettleAdventureResponse>(session, packet,
            (m, _, response) => response.SettleData = SettleRun(m, failed: true));

    [RequestPacketHandler("BiancaTheatreStrengthenRequest")]
    public static void BiancaTheatreStrengthenRequestHandler(Session session, Packet.Request packet) =>
        Handle<BiancaTheatreStrengthenRequest, BiancaTheatreStrengthenResponse>(session, packet, (m, request, _) =>
        {
            BiancaTheatreStrengthenTable row = Rows<BiancaTheatreStrengthenTable>().SingleOrDefault(row => row.Id == request.Id)
                ?? throw new ServerCodeException("Unknown strengthen.", 20176035);
            if (m.Data.StrengthenDbs.Contains(row.Id)) return;
            int condition = Rows<BiancaTheatreConfigTable>().Single(row => row.Key == "StrongerConditionId").Value;
            if (!Condition(m, condition) || row.PreStrengthenIds.Any(id => id > 0 && !m.Data.StrengthenDbs.Contains(id)))
                throw new ServerCodeException("Strengthen prerequisites not met.", 20176035);
            int currency = MetaCurrency("StrengthenCoinId");
            if (m.Balance(currency) < row.UnlockPrice)
                throw new ServerCodeException("Insufficient Curse-Dispelling Chapters.", 20176035);
            m.AddCost(currency, row.UnlockPrice);
            m.Data.StrengthenDbs.Add(row.Id);
        });

    [RequestPacketHandler("BiancaTheatreGetRewardRequest")]
    public static void BiancaTheatreGetRewardRequestHandler(Session session, Packet.Request packet) =>
        Handle<BiancaTheatreGetRewardRequest, BiancaTheatreGetRewardResponse>(session, packet, (m, request, response) =>
        {
            BiancaTheatreLevelRewardTable row = EligibleLevelRewards(m).SingleOrDefault(row => row.Id == request.Id)
                ?? throw new ServerCodeException("Level reward is not unlocked.", 20026007);
            if (m.Data.GetRewardIds.Contains(row.Id)) return;
            response.RewardGoodsList = GrantMetaReward(m, row.RewardId);
            m.Data.GetRewardIds.Add(row.Id);
        });

    [RequestPacketHandler("BiancaTheatreGetAllRewardRequest")]
    public static void BiancaTheatreGetAllRewardRequestHandler(Session session, Packet.Request packet) =>
        Handle<BiancaTheatreEmptyRequest, BiancaTheatreGetAllRewardResponse>(session, packet, (m, _, response) =>
        {
            foreach (BiancaTheatreLevelRewardTable row in EligibleLevelRewards(m))
            {
                if (m.Data.GetRewardIds.Contains(row.Id)) continue;
                response.RewardGoodsList.AddRange(GrantMetaReward(m, row.RewardId));
                m.Data.GetRewardIds.Add(row.Id);
            }
            response.GetRewardIds = [.. m.Data.GetRewardIds];
        });

    [RequestPacketHandler("BiancaTheatreGetAchievementRewardRequest")]
    public static void BiancaTheatreGetAchievementRewardRequestHandler(Session session, Packet.Request packet) =>
        Handle<BiancaTheatreGetAchievementRewardRequest, BiancaTheatreGetAchievementRewardResponse>(session, packet, (m, request, response) =>
        {
            BiancaTheatreActivityTable activity = Rows<BiancaTheatreActivityTable>().Single(row => row.Id == m.Data.CurActivityId);
            int index = request.NeedCountId - 1;
            if (index < 0 || index >= activity.NeedCounts.Count || index >= activity.RewardIds.Count)
                throw new ServerCodeException("Unknown achievement milestone.", 20026007);
            if (m.Data.GetAchievementRecords.Any(record => record.NeedCountId == request.NeedCountId)) return;
            int finished = Rows<BiancaTheatreAchievementTable>().SelectMany(row => row.TaskIds).Distinct().Count(m.IsTaskClaimed);
            if (m.Data.AchievementCondition == 0 || finished < activity.NeedCounts[index])
                throw new ServerCodeException("Achievement milestone is incomplete.", 20026007);
            response.RewardGoodsList = GrantMetaReward(m, activity.RewardIds[index]);
            m.Data.GetAchievementRecords.Add(new() { NeedCountId = request.NeedCountId });
        });

    [RequestPacketHandler("BiancaTheatreNewStageRequest")]
    public static void BiancaTheatreNewStageRequestHandler(Session session, Packet.Request packet) =>
        Handle<BiancaTheatreEmptyRequest, BiancaTheatreNewStageResponse>(session, packet, (m, _, _) => m.Data.NewStage = 0);

    private static int MetaCurrency(string key) => Rows<BiancaTheatreClientConfigTable>().Single(row => row.Key == key).Values[0];

    private static IEnumerable<BiancaTheatreLevelRewardTable> EligibleLevelRewards(Mutation m)
    {
        long required = 0;
        foreach (BiancaTheatreLevelRewardTable row in Rows<BiancaTheatreLevelRewardTable>().OrderBy(row => row.Id))
        {
            required = checked(required + row.UnlockScore);
            if (required > m.Data.TotalExp) yield break;
            yield return row;
        }
    }

    private static List<RewardGoods> GrantMetaReward(Mutation m, int rewardId)
    {
        List<RewardGoodsTable> goods = RewardHandler.GetRewardGoods(rewardId);
        if (goods.Count == 0) throw new InvalidOperationException($"Missing Bianca reward {rewardId}.");
        List<RewardGoods> result = goods.Select(row => new RewardGoods
        {
            Id = row.Id, TemplateId = row.TemplateId, Count = row.Count,
            RewardType = (int)(RewardHandler.GetRewardType(row) ?? throw new InvalidOperationException($"Unsupported reward {row.Id}."))
        }).ToList();
        m.Goods.AddRange(goods);
        return result;
    }

    internal static BiancaTheatreSettleData SettleRun(Mutation m, bool failed)
    {
        if (m.Data.CurChapterId == 0)
            return m.State.LastSettleData ?? throw new ServerCodeException("No adventure to settle.", 20026007);
        BiancaTheatreEndingTable ending = Rows<BiancaTheatreEndingTable>()
            .Where(row => row.PassType == (failed ? 1 : 2))
            .OrderByDescending(row => row.Priority).FirstOrDefault(row => EndingMatches(m, row))
            ?? throw new InvalidOperationException("No eligible Bianca ending.");
        BiancaTheatreDifficultyTable difficulty = Rows<BiancaTheatreDifficultyTable>().Single(row => row.Id == m.Data.DifficultyId);
        Dictionary<int, double> factors = Rows<BiancaTheatreSettleFactorTable>().ToDictionary(row => row.Id, row => row.Factor);
        int chapterCount = m.State.RunPassChapterIds.Count;
        BiancaTheatreSettleData result = new()
        {
            EndId = ending.Id, TeamId = m.Data.CurTeamId, NodeCount = m.State.RunNodeCount,
            FightNodeCount = m.State.RunFightNodeCount, TotalCharacterLevel = m.Data.Characters.Sum(character => character.Level),
            TotalItemCount = m.Data.Items.Count, ChapterCount = chapterCount,
            Characters = [.. m.Data.Characters], Items = [.. m.Data.Items],
            EndFactor = ending.Factor.ToString("0.0###############", CultureInfo.InvariantCulture),
            DifficultyFactor = difficulty.ExpFactor.ToString("0.0###############", CultureInfo.InvariantCulture)
        };
        // Source score factors are integral; keep the unrounded sum for the final currency calculation.
        double nodeScore = result.NodeCount * factors[1];
        double fightScore = result.FightNodeCount * factors[2];
        double characterScore = result.TotalCharacterLevel * factors[3];
        double itemScore = result.TotalItemCount * factors[4];
        result.NodeCountScore = checked((int)nodeScore);
        result.FightNodeCountScore = checked((int)fightScore);
        result.TotalCharacterLevelScore = checked((int)characterScore);
        result.TotalItemCountScore = checked((int)itemScore);
        // Approved private-server rule: chapter scores are bands, including beyond the highest configured count.
        result.ChapterCountScore = Rows<BiancaTheatreSettleChapterFactorTable>()
            .Where(row => row.Count <= chapterCount).OrderByDescending(row => row.Count).Select(row => row.Score).FirstOrDefault();
        double settlementScore = nodeScore + fightScore + characterScore + itemScore + result.ChapterCountScore;
        result.TotalScore = checked(result.NodeCountScore + result.FightNodeCountScore + result.TotalCharacterLevelScore + result.TotalItemCountScore + result.ChapterCountScore);
        result.NewRecord = result.TotalScore > m.State.BestScore;
        if (result.NewRecord) m.State.BestScore = result.TotalScore;
        double systemFactor = SystemEffects(m).Where(row => row.Type == 12).Aggregate(1.0, (factor, row) => factor * row.Params[0]);
        // Approved private-server rule, not a reconstructed retail rounding contract:
        // floor each nonnegative currency result once, after multiplying all source factors.
        result.TotalExp = MetaFloor(settlementScore * ending.Factor * difficulty.ExpFactor * systemFactor);
        result.OutItemCount = MetaFloor(settlementScore * ending.Factor * difficulty.OutItemFactor * systemFactor);
        m.Data.TotalExp = checked(m.Data.TotalExp + result.TotalExp);
        if (result.TotalExp > 0) m.AddGoods(MetaCurrency("LevelItemId"), result.TotalExp);
        if (result.OutItemCount > 0) m.AddGoods(MetaCurrency("StrengthenCoinId"), result.OutItemCount);
        foreach (int id in m.State.RunPassChapterIds)
            if (!m.Data.PassChapterIds.Contains(id)) m.Data.PassChapterIds.Add(id);
        if (!failed)
        {
            m.State.CompletionCount = checked(m.State.CompletionCount + 1);
            if (!m.State.SuccessfulDifficultyIds.Contains(m.Data.DifficultyId)) m.State.SuccessfulDifficultyIds.Add(m.Data.DifficultyId);
            result.NewEnding = !m.Data.TeamRecords.Any(record => record.EndRecords.Contains(ending.Id));
            BiancaTheatreTeamRecord? record = m.Data.TeamRecords.SingleOrDefault(record => record.TeamId == m.Data.CurTeamId);
            if (record is null) m.Data.TeamRecords.Add(record = new() { TeamId = m.Data.CurTeamId });
            if (!record.EndRecords.Contains(ending.Id)) record.EndRecords.Add(ending.Id);
            RecordSuccessfulEndingTasks(m);
        }
        RefreshMetaUnlocks(m);
        RecordMetaProgress(m);
        result.PassChapterIds = [.. m.Data.PassChapterIds];
        result.UnlockDifficultyId = [.. m.Data.UnlockDifficultyId];
        result.UnlockItemId = [.. m.Data.UnlockItemId];
        result.UnlockTeamId = [.. m.Data.UnlockTeamId];
        result.TeamRecords = m.Data.TeamRecords.Select(record => new BiancaTheatreTeamRecord { TeamId = record.TeamId, EndRecords = [.. record.EndRecords] }).ToList();
        result.HistoryTotalItemCount = m.Data.HistoryTotalItemCount;
        result.HistoryTotalPassFightNodeCount = m.Data.HistoryTotalPassFightNodeCount;
        result.HistoryItemObtainRecords = new(m.Data.HistoryItemObtainRecords);
        m.State.PreviousRunPassChapterIds = [.. m.State.RunPassChapterIds];
        m.State.LastSettleData = result;
        ResetMetaRun(m);
        m.Push(new NotifyBiancaTheatreTotalExp { TotalExp = m.Data.TotalExp });
        return result;
    }

    private static int MetaFloor(double value) => !double.IsFinite(value) || value < 0
        ? throw new InvalidOperationException("Invalid Bianca settlement factor.") : checked((int)Math.Floor(value));

    private static bool EndingMatches(Mutation m, BiancaTheatreEndingTable ending)
    {
        if (ending.Type.Count != ending.Param.Count) throw new InvalidOperationException("Invalid ending requirement arity.");
        for (int index = 0; index < ending.Type.Count; index++)
        {
            bool matches = ending.Type[index] switch
            {
                1 when ending.Param[index] == 0 => true,
                4 => m.State.PassedStageIds.Contains(ending.Param[index]),
                _ => throw new InvalidOperationException($"Unsupported ending requirement {ending.Type[index]}.")
            };
            if (!matches) return false;
        }
        return true;
    }

    private static void RefreshMetaUnlocks(Mutation m)
    {
        RefreshUnlocks(m);
        int achievementCondition = Rows<BiancaTheatreConfigTable>().Single(row => row.Key == "AchievementConditionId").Value;
        if (m.Data.AchievementCondition == 0 && Condition(m, achievementCondition))
        {
            m.Data.AchievementCondition = 1;
            m.Push(new NotifyBiancaTheatreAchievementCondition());
        }
    }

    private static void ResetMetaRun(Mutation m)
    {
        foreach (int itemId in new[] { InnerCoin, ActionPoint, VisionItem })
        {
            int balance = checked((int)m.Balance(itemId));
            if (balance > 0) m.AddCost(itemId, balance);
        }
        m.Data.TeamCountEffect = 0;
        m.Data.CurChapterId = 0; m.Data.CurChapterDb = null; m.Data.DifficultyId = 0; m.Data.CurTeamId = 0; m.Data.CurRoleLv = 0;
        m.Data.Characters = []; m.Data.Items = []; m.Data.SingleTeamData = new(); m.Data.GamePassNodeCount = 0; m.Data.IsOpenVision = 0;
        m.State.Fight = null; m.State.MultiTeams.Clear(); m.State.QueuedSteps.Clear(); m.State.CompletedChapters.Clear(); m.State.AppliedSystemEffectIds.Clear();
        m.State.RunRecruitCount = 0; m.State.RunItemCount = 0; m.State.RunReviveCount = 0; m.State.RunNodeCount = 0; m.State.RunFightNodeCount = 0;
        m.State.RunPassChapterIds.Clear(); m.State.PassedStageIds.Clear(); m.State.LastEventChoiceId = 0; m.State.ConsecutiveEventChoices = 0;
        m.State.ReachedChapterIds.Clear(); m.Data.PassedEventRecord.Clear();
    }

    internal static List<TaskTable> MetaTasks()
    {
        HashSet<int> ids = Rows<BiancaTheatreTaskTable>().SelectMany(row => row.TaskId)
            .Concat(Rows<BiancaTheatreAchievementTable>().SelectMany(row => row.TaskIds)).ToHashSet();
        return Rows<TaskTable>().Where(row => ids.Contains(row.Id)).ToList();
    }

    internal static void RecordEventChoice(Mutation m, int eventId, int stepId, int optionId = 0)
    {
        if (!m.Data.PassedEventRecord.TryGetValue(eventId, out List<int>? steps)) m.Data.PassedEventRecord.Add(eventId, steps = []);
        if (!steps.Contains(stepId)) steps.Add(stepId);
        if (!m.State.HistoryPassedEventRecord.TryGetValue(eventId, out List<int>? history)) m.State.HistoryPassedEventRecord.Add(eventId, history = []);
        if (!history.Contains(stepId)) history.Add(stepId);
        BiancaTheatreEventTable row = Rows<BiancaTheatreEventTable>().Single(row => row.EventId == eventId && row.StepId == stepId);
        if (m.State.LastEventChoiceId != eventId) m.State.ConsecutiveEventChoices = 0;
        m.State.LastEventChoiceId = eventId;
        if (row.Type != 2) return;
        if (optionId < 1 || optionId > row.OptionType.Count) throw new InvalidOperationException("Missing accepted event option.");
        if (row.OptionType[optionId - 1] != 1) { m.State.ConsecutiveEventChoices = 0; return; }
        m.State.ConsecutiveEventChoices = checked(m.State.ConsecutiveEventChoices + 1);
        foreach (ConditionTable condition in MetaTaskConditions().Where(row => row.Type == 91009))
        {
            if (condition.Params.Count != condition.Params[0] + 2) throw new InvalidOperationException("Invalid Bianca consecutive event task.");
            if (condition.Params.Skip(1).Take(condition.Params[0]).Contains(eventId)
                && m.State.ConsecutiveEventChoices >= condition.Params[^1] && m.State.TaskProgress.GetValueOrDefault(condition.Id) == 0)
            {
                m.State.TaskProgress[condition.Id] = 1;
                m.Push(TaskModule.BuildBiancaTaskSync(m.State, m.IsTaskClaimed));
            }
        }
    }

    private static IEnumerable<ConditionTable> MetaTaskConditions()
    {
        HashSet<int> ids = MetaTasks().Select(row => row.Condition).ToHashSet();
        return Rows<ConditionTable>().Where(row => ids.Contains(row.Id));
    }

    internal static void RecordMetaProgress(Mutation m)
    {
        if (m.Data.CurChapterId > 0 && !m.State.ReachedChapterIds.Contains(m.Data.CurChapterId)) m.State.ReachedChapterIds.Add(m.Data.CurChapterId);
        bool changed = false;
        foreach (ConditionTable condition in MetaTaskConditions())
        {
            List<int> p = condition.Params;
            int value = condition.Type switch
            {
                91002 or 91012 => m.Data.HistoryTotalItemCount,
                91003 or 91013 => m.State.TotalRecruitCount,
                91004 => p.Skip(1).Take(p[0]).Any(m.State.ReachedChapterIds.Contains) ? 1 : 0,
                91005 => m.Data.TeamRecords.Any(record => record.EndRecords.Contains(p[0])) ? 1 : 0,
                91008 => m.State.HistoryPassedEventRecord.Values.Any(steps => steps.Contains(p[0])) ? 1 : 0,
                91009 or 91014 or 91022 => m.State.TaskProgress.GetValueOrDefault(condition.Id),
                91010 => checked((int)m.State.RunId),
                91011 => m.Data.HistoryTotalPassFightNodeCount,
                91015 => m.State.RunRecruitCount,
                91016 => m.State.RunItemCount,
                91017 => m.Data.TeamRecords.Select(record => record.EndRecords.Distinct().Count()).DefaultIfEmpty().Max(),
                91018 => m.State.ComboPhaseHistory.Values.Any(level => level >= p[0]) ? 1 : 0,
                91019 => m.Data.Characters.Any(character => character.Level >= p[0]) ? 1 : 0,
                91020 => Rows<BiancaTheatreTeamTable>().Count(row => row.ConditionId > 0 && m.Data.UnlockTeamId.Contains(row.Id)),
                91021 => m.Data.StrengthenDbs.Contains(p[0]) ? 1 : 0,
                _ => throw new InvalidOperationException($"Unsupported Bianca task condition {condition.Type}.")
            };
            if (value <= m.State.TaskProgress.GetValueOrDefault(condition.Id)) continue;
            m.State.TaskProgress[condition.Id] = value;
            changed = true;
        }
        if (changed) m.Push(TaskModule.BuildBiancaTaskSync(m.State, m.IsTaskClaimed));
    }

    private static void RecordSuccessfulEndingTasks(Mutation m)
    {
        bool changed = false;
        foreach (ConditionTable condition in MetaTaskConditions())
        {
            bool achieved = condition.Type switch
            {
                91014 => m.Balance(VisionItem) >= condition.Params[0],
                91022 => m.Data.DifficultyId == condition.Params[0] && m.State.RunReviveCount == 0,
                _ => false
            };
            if (achieved && m.State.TaskProgress.GetValueOrDefault(condition.Id) == 0)
            {
                m.State.TaskProgress[condition.Id] = 1;
                changed = true;
            }
        }
        if (changed) m.Push(TaskModule.BuildBiancaTaskSync(m.State, m.IsTaskClaimed));
    }

    internal static FinishTaskResponse ClaimMetaTask(Mutation m, int taskId)
    {
        TaskTable task = MetaTasks().Single(row => row.Id == taskId);
        if (m.IsTaskClaimed(task.Id)) return new() { Code = 20026006 };
        RecordMetaProgress(m);
        if (m.State.TaskProgress.GetValueOrDefault(task.Condition) < (task.Result ?? 1))
            return new() { Code = 20026007 };
        FinishTaskResponse response = new();
        if (task.RewardId is > 0) response.RewardGoodsList = GrantMetaReward(m, task.RewardId.Value);
        m.MarkTaskClaimed(task.Id);
        m.Push(TaskModule.BuildBiancaTaskSync(m.State, m.IsTaskClaimed));
        return response;
    }
}
