using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.fuben.fashionstory;
using AscNet.GameServer.Game;
using AscNet.Table.V2.client.functional;
using AscNet.Table.V2.share.activity;
using AscNet.Table.V2.share.condition;
using AscNet.Table.V2.share.functional;

namespace AscNet.GameServer.Handlers;

internal static class FashionStoryModule
{
    internal const int StageNotFound = 20003002; // FubenManagerCheckPreMapNotFound
    internal const int PreviousStageNotPassed = 20003003; // FubenManagerCheckPreMapNotPass
    internal const int StageLocked = 20003024; // FubenManagerStageLocked
    internal const int PreFightStageNotFound = 20003012; // FubenManagerCheckPreFightStageInfoNotFound

    private static readonly Lazy<IReadOnlyDictionary<int, FashionStoryTable>> Activities = new(() =>
        TableReaderV2.Parse<FashionStoryTable>().ToDictionary(row => row.Id));
    private static readonly Lazy<IReadOnlyDictionary<int, FashionStorySingleLineTable>> Lines = new(() =>
        TableReaderV2.Parse<FashionStorySingleLineTable>().ToDictionary(row => row.Id));
    private static readonly Lazy<IReadOnlyDictionary<int, FashionStoryStageTable>> Stages = new(() =>
        TableReaderV2.Parse<FashionStoryStageTable>().ToDictionary(row => row.StageId));
    private static readonly Lazy<IReadOnlyDictionary<int, ConditionTable>> Conditions = new(() =>
        TableReaderV2.Parse<ConditionTable>().ToDictionary(row => row.Id));
    private static readonly Lazy<IReadOnlyDictionary<int, FunctionalOpenTable>> Functions = new(() =>
        TableReaderV2.Parse<FunctionalOpenTable>().ToDictionary(row => row.Id));
    private static readonly Lazy<IReadOnlyDictionary<int, SkipFunctionalTable>> Skips = new(() =>
        TableReaderV2.Parse<SkipFunctionalTable>().ToDictionary(row => row.SkipId));
    private static readonly Lazy<IReadOnlyDictionary<int, IReadOnlyList<EventCatalogTable>>> Events = new(() =>
        TableReaderV2.Parse<EventCatalogTable>()
            .Where(row => row.TimeId is > 0)
            .GroupBy(row => row.TimeId!.Value)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<EventCatalogTable>)group.ToArray()));

    internal static bool PrepareLogin(Player player, DateTimeOffset now)
    {
        FashionStoryTable? activity = Activities.Value.Values
            .Where(candidate => candidate.TimeId is > 0
                && ActivityScheduleService.IsOpen(candidate.TimeId.Value, now)
                && FunctionGateSatisfied(player, candidate.TimeId.Value))
            .OrderByDescending(candidate => candidate.Id)
            .FirstOrDefault();

        if (activity is null)
        {
            FashionStoryState? existing = player.FashionStory;
            if (existing is null
                || (existing.AuthorizedActivityId == 0 && existing.AuthorizedTimeIds.Count == 0))
                return false;

            existing.AuthorizedActivityId = 0;
            existing.AuthorizedTimeIds.Clear();
            player.Save();
            return true;
        }

        List<int> timeIds = [activity.TimeId!.Value];
        FashionStoryState? state = player.FashionStory;
        if (state is not null
            && state.AuthorizedActivityId == activity.Id
            && state.AuthorizedTimeIds.SequenceEqual(timeIds))
            return false;

        player.FashionStory = new FashionStoryState
        {
            AuthorizedActivityId = activity.Id,
            AuthorizedTimeIds = timeIds
        };
        player.Save();
        return true;
    }

    private static bool FunctionGateSatisfied(Player player, int timeId)
    {
        if (!Events.Value.TryGetValue(timeId, out IReadOnlyList<EventCatalogTable>? entries))
            return false;

        return entries
            .Where(entry => entry.SkipId is int skipId && Skips.Value.TryGetValue(skipId, out _))
            .Select(entry => Skips.Value[entry.SkipId!.Value])
            .Where(skip => skip.FunctionalId is int && Functions.Value.ContainsKey(skip.FunctionalId.Value))
            .Select(skip => Functions.Value[skip.FunctionalId!.Value])
            .Any(function => function.Condition.Count > 0 && function.Condition.All(conditionId =>
                conditionId > 0 && PlayerConditionSatisfied(player, conditionId, new HashSet<int>())));
    }

    private static bool PlayerConditionSatisfied(Player player, int conditionId, HashSet<int> visiting)
    {
        if (!Conditions.Value.TryGetValue(conditionId, out ConditionTable? condition) || !visiting.Add(conditionId))
            return false;
        try
        {
            if (!string.IsNullOrWhiteSpace(condition.Formula))
            {
                bool any = condition.Formula.Contains('|');
                if (any && condition.Formula.Contains('&'))
                    return false;
                string[] terms = condition.Formula.Split(any ? '|' : '&', StringSplitOptions.RemoveEmptyEntries);
                return terms.Length > 0 && (any ? terms.Any(Evaluate) : terms.All(Evaluate));

                bool Evaluate(string term)
                {
                    string value = term.Trim();
                    bool negate = value.StartsWith('!');
                    if (negate)
                        value = value[1..];
                    return int.TryParse(value, out int child)
                        && (PlayerConditionSatisfied(player, child, visiting) != negate);
                }
            }
            return condition.Type == 10101
                && condition.Params.Count > 0
                && condition.Params.All(requiredLevel => player.PlayerData.Level >= requiredLevel);
        }
        finally
        {
            visiting.Remove(conditionId);
        }
    }


    internal static NotifyFashionStoryData BuildNotify(Session session)
    {
        if (!TryAuthorizedActivity(session.player, out FashionStoryTable? activity))
            return new();

        HashSet<int> ownedStages = ResolveStages(activity!);
        return new NotifyFashionStoryData
        {
            ActivityId = activity!.Id,
            FinishStageList = session.stage.Stages.Values
                .Where(stage => stage.Passed && stage.StageId is >= int.MinValue and <= int.MaxValue
                    && ownedStages.Contains((int)stage.StageId))
                .Select(stage => (int)stage.StageId)
                .Distinct()
                .Order()
                .ToList()
        };
    }

    internal static bool TryValidateStage(Session session, int stageId, out int code)
    {
        code = 0;
        if (!Activities.Value.Values.Any(activity => ResolveStages(activity).Contains(stageId)))
            return false;

        if (!TryAuthorizedActivity(session.player, out FashionStoryTable? activity)
            || !ResolveStages(activity!).Contains(stageId))
        {
            code = StageLocked;
            return true;
        }

        if (!Stages.Value.TryGetValue(stageId, out FashionStoryStageTable? stage))
            return true;

        if (stage.TimeId is > 0 && !session.player.FashionStory!.AuthorizedTimeIds.Contains(stage.TimeId.Value))
        {
            code = StageLocked;
            return true;
        }

        if (stage.PreStageId is > 0
            && (!session.stage.Stages.TryGetValue(stage.PreStageId.Value, out StageDatum? prerequisite) || !prerequisite.Passed))
            code = PreviousStageNotPassed;

        return true;
    }

    internal static bool IsTrialStage(Session session, int stageId) =>
        TryAuthorizedActivity(session.player, out FashionStoryTable? activity)
        && activity!.TrialStages.Contains(stageId);

    private static bool TryAuthorizedActivity(Player player, out FashionStoryTable? activity)
    {
        FashionStoryState? state = player.FashionStory;
        if (state is not null && state.AuthorizedActivityId > 0
            && Activities.Value.TryGetValue(state.AuthorizedActivityId, out FashionStoryTable? configured)
            && configured.TimeId is > 0
            && ActivityScheduleService.IsOpen(configured.TimeId.Value, DateTimeOffset.UtcNow)
            && FunctionGateSatisfied(player, configured.TimeId.Value)
            && state.AuthorizedTimeIds.Contains(configured.TimeId.Value))
        {
            activity = configured;
            return true;
        }

        activity = null;
        return false;
    }

    private static HashSet<int> ResolveStages(FashionStoryTable activity)
    {
        HashSet<int> result = activity.TrialStages.Where(id => id > 0).ToHashSet();
        foreach (int lineId in activity.SingleLines.Where(id => id > 0))
            if (Lines.Value.TryGetValue(lineId, out FashionStorySingleLineTable? line))
                result.UnionWith(line.ChapterStages.Where(id => id > 0));
        return result;
    }
}
