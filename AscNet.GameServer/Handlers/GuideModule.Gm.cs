using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.condition;
using AscNet.Table.V2.share.fuben;
using AscNet.Table.V2.share.guide;
using BigWorldConditions = AscNet.GameServer.Handlers.BigWorld.BigWorldConditionService;

namespace AscNet.GameServer.Handlers;

internal enum GuideRewardPolicy
{
    Preserve,
    Grant,
    Skip
}

internal partial class GuideModule
{
    private static readonly Lazy<Dictionary<int, ConditionTable>> GuideConditions = new(() =>
        TableReaderV2.Parse<ConditionTable>().ToDictionary(row => row.Id));
    private static readonly Lazy<Dictionary<int, GuideFightTable>> GuideFights = new(() =>
        TableReaderV2.Parse<GuideFightTable>().ToDictionary(row => row.Id));
    private static readonly Lazy<HashSet<int>> GuideStageIds = new(() =>
        TableReaderV2.Parse<StageTable>().Select(row => row.StageId).ToHashSet());

    private enum GuideStatus
    {
        Ready,
        Completed,
        Preserved,
        Blocked
    }

    private sealed record GuideDecision(GuideStatus Status, string Reason = "", GuideCompletionEntry? Entry = null);

    internal static string ExecuteGmCompletion(Session session, int? guideId, int? groupId,
        GuideRewardPolicy policy, bool dryRun, bool ignoreClientTriggers)
    {
        long uid = session.player.PlayerData.Id;
        if (session.player.PendingGuideCompletion is not null)
            return $"Player {uid} has a pending guide operation. Reconnect to resume it.";

        GuideGroupTable[] candidates = GuideGroups.Value.Values
            .Where(guide => guideId is int id ? guide.Id == id
                : groupId is int group ? guide.GroupId == group : guide.Ignore == 0)
            .OrderBy(guide => guide.Priority).ThenBy(guide => guide.Id).ToArray();
        if (candidates.Length == 0)
            return $"Player {uid} has no matching common guide rows.";

        // Use the same plan for preview and apply. In a batch, only newly planned guide ids
        // can satisfy additional prerequisites. Goods and other progression stay at their starting state.
        Dictionary<int, GuideDecision> decisions = [];
        List<GuideCompletionEntry> entries = [];
        HashSet<long> completed = new(session.player.PlayerData.GuideData ?? []);
        int before;
        do
        {
            before = entries.Count;
            foreach (GuideGroupTable guide in candidates)
            {
                if (decisions.TryGetValue(guide.Id, out GuideDecision? previous) && previous.Status != GuideStatus.Blocked)
                    continue;
                GuideDecision decision = PlanGuide(session, guide, completed, policy, ignoreClientTriggers);
                decisions[guide.Id] = decision;
                if (decision.Entry is { } entry)
                {
                    entries.Add(entry);
                    completed.Add(guide.Id);
                }
            }
        }
        while (entries.Count != before && decisions.Values.Any(decision => decision.Status == GuideStatus.Blocked));

        if (!dryRun && entries.Count > 0)
        {
            RewardApplicationResult? result = CompleteGuides(session, entries, syncOnReconnect: true);
            result?.SendPushes(session);
            NotifyCompletedGuides(session, entries.Select(entry => entry.GuideId));
            BigWorld.BigWorldQuestRuntime.OnConditionsChanged(session);
            BigWorld.BigWorldQuestRuntime.Tick(session);
            session.log.Info($"GM guide completion player={uid} policy={policy} ignoreClientTriggers={ignoreClientTriggers} ids={string.Join(",", entries.Select(entry => entry.GuideId))}");
        }

        string action = dryRun ? "Dry run" : "Guide completion";
        string prefix = $"{action} for player {uid} ({policy.ToString().ToLowerInvariant()})";
        if (guideId is int single)
        {
            GuideDecision decision = decisions[single];
            string status = decision.Status == GuideStatus.Ready ? (dryRun ? "would complete" : "completed")
                : decision.Status.ToString().ToLowerInvariant();
            return $"{prefix}\nGuide {single} {status}. {decision.Reason}";
        }

        string eligible = dryRun ? "would complete" : "completed";
        int granted = entries.Count(entry => entry.Goods is { Count: > 0 });
        int skipped = policy == GuideRewardPolicy.Skip
            ? entries.Count(entry => GuideGroups.Value[entry.GuideId].RewardId > 0) : 0;
        var blocked = decisions.Where(pair => pair.Value.Status == GuideStatus.Blocked).ToArray();
        string details = string.Join("\n", blocked.Take(3).Select(pair => $"Guide {pair.Key}. {pair.Value.Reason}"));
        return $"{prefix}\n{entries.Count} {eligible}, {granted} with grants, {skipped} with rewards skipped, " +
            $"{decisions.Values.Count(decision => decision.Status == GuideStatus.Preserved)} preserved, " +
            $"{decisions.Values.Count(decision => decision.Status == GuideStatus.Completed)} already complete, {blocked.Length} blocked.\n" +
            $"Planned ids {string.Join(", ", entries.Take(8).Select(entry => entry.GuideId))}{(entries.Count > 8 ? ", …" : "")}\n" +
            details;
    }

    private static GuideDecision PlanGuide(Session session, GuideGroupTable guide, HashSet<long> completed,
        GuideRewardPolicy policy, bool ignoreClientTriggers)
    {
        if (completed.Contains(guide.Id))
            return new(GuideStatus.Completed, "Already recorded.");
        if (guide.Ignore != 0)
            return new(GuideStatus.Blocked, "The guide is disabled by its table row.");
        if (!GuideCompletions.Value.TryGetValue(guide.CompleteId, out GuideCompleteTable? completion))
            return new(GuideStatus.Blocked, $"Missing completion rule {guide.CompleteId}.");

        string? requirement = CompletionBlock(session, guide, completion);
        if (requirement is not null)
            return new(GuideStatus.Blocked, requirement);
        try
        {
            foreach (int conditionId in guide.ConditionId.Where(id => id != 0))
            {
                if (!CheckGuideCondition(session, conditionId, completed, ignoreClientTriggers, []))
                    return new(GuideStatus.Blocked, $"Condition {conditionId} is unmet.");
            }

            if (guide.RewardId > 0)
            {
                if (policy == GuideRewardPolicy.Preserve)
                    return new(GuideStatus.Preserved, $"Reward {guide.RewardId} remains available through native completion.");
                if (policy == GuideRewardPolicy.Skip && HasGuideRewardReceipt(session, guide.Id))
                    return new(GuideStatus.Blocked, $"Reward {guide.RewardId} has a receipt. Use grant to finish settlement.");
                if (policy == GuideRewardPolicy.Grant)
                {
                    GuideCompletionEntry entry = NaturalCompletion(guide);
                    string goods = string.Join(", ", entry.Goods!.Select(row => $"{row.TemplateId} × {row.Count}"));
                    string receipt = HasGuideRewardReceipt(session, guide.Id) ? " Existing receipts will be reused." : "";
                    return new(GuideStatus.Ready, $"Reward {guide.RewardId} ({goods}).{receipt}", entry);
                }
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or FormatException)
        {
            return new(GuideStatus.Blocked, exception.Message);
        }

        return new(GuideStatus.Ready, guide.RewardId > 0 ? $"Skip reward {guide.RewardId}." : "Rewardless.",
            new GuideCompletionEntry { GuideId = guide.Id });
    }

    private static string? CompletionBlock(Session session, GuideGroupTable guide, GuideCompleteTable completion)
    {
        List<int> parameters = completion.Param;
        if (parameters.Count == 0 && GuideFights.Value.TryGetValue(guide.Id, out GuideFightTable? fight)
            && GuideStageIds.Value.Contains(fight.StageId))
            return StagePassed(session, fight.StageId) ? null : $"Requires prologue stage {fight.StageId} to be passed.";

        return (CompletionMode)parameters.FirstOrDefault() switch
        {
            CompletionMode.Step => null,
            CompletionMode.Stage when parameters.Count >= 3 && parameters[2] == 1 && GuideStageIds.Value.Contains(parameters[1])
                => StagePassed(session, parameters[1]) ? null : $"Requires stage {parameters[1]} to be passed.",
            CompletionMode.CourseReward when parameters.Count >= 2 && GuideStageIds.Value.Contains(parameters[1])
                => session.stage.Course.Contains((uint)parameters[1]) ? null : $"Requires the course reward for stage {parameters[1]}.",
            _ => $"Unsupported completion rule {completion.Id} (mode {parameters.FirstOrDefault()})."
        };
    }

    private static bool StagePassed(Session session, int stageId) =>
        session.stage.Stages.TryGetValue(stageId, out StageDatum? stage) && stage.Passed;

    private static bool CheckGuideCondition(Session session, int id, HashSet<long> completed,
        bool ignoreClientTriggers, HashSet<int> evaluating)
    {
        if (!GuideConditions.Value.TryGetValue(id, out ConditionTable? row) || !evaluating.Add(id))
            throw new InvalidDataException($"Missing or cyclic guide condition {id}.");

        try
        {
            if (!string.IsNullOrWhiteSpace(row.Formula))
                return BigWorldConditions.EvaluateFormula(row.Formula.Replace(" ", ""),
                    conditionId => CheckGuideCondition(session, conditionId, completed, ignoreClientTriggers, evaluating));

            List<int> p = row.Params;
            var player = session.player.PlayerData;
            return row.Type switch
            {
                10101 when p.Count >= 1 => player.Level >= p[0],
                10102 when p.Count >= 1 => session.character.Characters.Any(character => character.Id == p[0]),
                10105 when p.Count >= 1 && GuideStageIds.Value.Contains(p[0]) => StagePassed(session, p[0]),
                10107 when p.Count >= 1 => player.Level <= p[0],
                10108 when p.Count >= 1 && GuideStageIds.Value.Contains(p[0]) => !StagePassed(session, p[0]),
                10153 when p.Count >= 2 && p[1] is 0 or 1 => (p[0] >= player.CreateTime) == (p[1] == 1),
                10187 when p.Count >= 2 && p[0] is 0 or 1 => completed.Contains(p[1]) == (p[0] == 1),
                // Client presentation predicates in XConditionManager. The opt-in applies to
                // the guide's activation check only. It changes no download, room, or queue state.
                10189 or 15206 or 20112 or 20114 => ignoreClientTriggers
                    ? true : throw new InvalidDataException($"Condition {id} is a client trigger. Use --ignore-client-triggers to waive it."),
                _ => throw new InvalidDataException($"Unsupported guide condition {id} (type {row.Type}).")
            };
        }
        finally
        {
            evaluating.Remove(id);
        }
    }
}
