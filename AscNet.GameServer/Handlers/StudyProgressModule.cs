using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.fuben.teaching;
using AscNet.Table.V2.share.reward;
using static AscNet.GameServer.Handlers.RewardHandler;

namespace AscNet.GameServer.Handlers;

internal static class StudyProgressModule
{
    // CodeText.json keys used by xfubennewcharactivitymanager.lua GetStarReward.
    internal const int TreasureAlreadyClaimed = 20003001; // FubenManagerCheckTreasureIsGet
    internal const int TreasureStarsNotEnough = 20003009; // FubenManagerCheckTreasureStarsNotEnough
    internal const int ActivityNotOpen = 20107001; // TeachingActivityIsNotOpen
    internal const int ActivityOver = 20107002; // TeachingActivityIdNotFount ("Event is over")
    internal const int TreasureNotFound = 20107004; // TeachingActivityTreasureRewardNotFound
    internal const int TreasureStageNotPassed = 20107009; // TeachingActivityTreasureRequireStageNotPass
    internal const int TreasureTypeError = 20107010; // TeachingActivityTreasureTypeError

    private static readonly Lazy<Dictionary<int, TeachingTreasureTable>> Treasures = new(() =>
        TableReaderV2.Parse<TeachingTreasureTable>().ToDictionary(row => row.TreasureId));
    private static readonly Lazy<Dictionary<int, TeachingActivityTable>> TreasureOwners = new(() =>
        TableReaderV2.Parse<TeachingActivityTable>()
            .SelectMany(activity => activity.TreasureId.Where(id => id > 0).Select(id => (id, activity)))
            .ToDictionary(pair => pair.id, pair => pair.activity));

    private static string TreasureClaimKey(Session session, int treasureId) =>
        $"teaching-treasure:{session.player.PlayerData.Id}:{treasureId}";

    // Claim state is the durable reward receipt itself (inventory + character), so a partial
    // cross-document write stays claimable and ApplyRewardsOnceAndPersist resumes it without duplicates.
    private static bool IsTreasureClaimed(Session session, int treasureId)
    {
        string key = TreasureClaimKey(session, treasureId);
        return session.inventory.AppliedRewardClaims?.Contains(key, StringComparer.Ordinal) == true
            && session.character.AppliedRewardClaims?.Contains(key, StringComparer.Ordinal) == true;
    }

    // A receipt on only one document is a validated claim whose cross-document write failed;
    // finish it at login from the authoritative table, regardless of the activity window.
    internal static void ResumePartialTreasureClaims(Session session)
    {
        string prefix = $"teaching-treasure:{session.player.PlayerData.Id}:";
        List<string> inventory = session.inventory.AppliedRewardClaims ?? [];
        List<string> character = session.character.AppliedRewardClaims ?? [];
        List<RewardGrant> grants = inventory.Concat(character)
            .Where(key => key.StartsWith(prefix, StringComparison.Ordinal)
                && !(inventory.Contains(key, StringComparer.Ordinal) && character.Contains(key, StringComparer.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .Select(key => int.TryParse(key.AsSpan(prefix.Length), out int id) && Treasures.Value.TryGetValue(id, out TeachingTreasureTable? treasure)
                ? new RewardGrant(key, GetRewardGoods(treasure.RewardId)) : null)
            .OfType<RewardGrant>()
            .ToList();
        if (grants.Count > 0)
            ApplyRewardsOnceAndPersist(grants, session);
    }

    [RequestPacketHandler("TeachingTreasureRewardRequest")]
    public static void TeachingTreasureRewardRequestHandler(Session session, Packet.Request packet)
    {
        TeachingTreasureRewardRequest request = packet.Deserialize<TeachingTreasureRewardRequest>();
        try
        {
            (int code, RewardApplicationResult? application) = ClaimTreasure(session, request.TreasureId, DateTimeOffset.UtcNow);
            application?.SendPushes(session);
            session.SendResponse(new TeachingTreasureRewardResponse
            {
                Code = code,
                RewardGoodsList = application?.RewardGoods ?? new()
            }, packet.Id);
        }
        catch
        {
            session.DisconnectProtocol(persistState: false);
            throw;
        }
    }

    internal static (int Code, RewardApplicationResult? Application) ClaimTreasure(Session session, int treasureId, DateTimeOffset now)
    {
        if (!Treasures.Value.TryGetValue(treasureId, out TeachingTreasureTable? treasure)
            || !TreasureOwners.Value.TryGetValue(treasureId, out TeachingActivityTable? activity))
            return (TreasureNotFound, null);

        // Window matches XFubenNewCharConfig.GetActivityTime: begin <= now < end, including the
        // calendar-derived TimeIds (905) emitted to the client at login.
        TimeLimitCtrlConfigList? window = activity.TimeId is > 0
            ? AccountModule.BuildTimeLimitControlConfigList(now, WheelchairManualModule.IsActive(now))
                .FirstOrDefault(control => control.Id == activity.TimeId.Value)
            : null;
        long nowSeconds = now.ToUnixTimeSeconds();
        if (window is null || nowSeconds < window.StartTime)
            return (ActivityNotOpen, null);
        if (window.EndTime != 0 && nowSeconds >= window.EndTime)
            return (ActivityOver, null);
        if (IsTreasureClaimed(session, treasureId))
            return (TreasureAlreadyClaimed, null);

        switch (treasure.Type)
        {
            case 1:
                // xfubennewcharactivitymanager.lua CheckTreasureReward: ChallengeStage or StageId, bits 1|2|4.
                IEnumerable<int> stageIds = activity.ChallengeStage.Any(id => id > 0) ? activity.ChallengeStage : activity.StageId;
                int stars = stageIds.Where(id => id > 0).Sum(id => session.stage.Stages.TryGetValue(id, out StageDatum? stage)
                    ? System.Numerics.BitOperations.PopCount((uint)(stage.StarsMark & 7)) : 0);
                if (treasure.RequireStar <= 0 || stars < treasure.RequireStar)
                    return (TreasureStarsNotEnough, null);
                break;
            case 2:
                if (!session.stage.Stages.TryGetValue(treasure.RequireStage, out StageDatum? required) || !required.Passed)
                    return (TreasureStageNotPassed, null);
                break;
            default:
                return (TreasureTypeError, null);
        }

        List<RewardGoodsTable> goods = GetRewardGoods(treasure.RewardId);
        if (goods.Count == 0)
            return (TreasureNotFound, null);
        return (0, ApplyRewardsOnceAndPersist([new RewardGrant(TreasureClaimKey(session, treasureId), goods)], session));
    }

    internal static void SendLoginState(Session session)
    {
        StageDatum[] passedStudyStages = session.stage.Stages.Values
            .Where(stage => stage.Passed && CurrentClientStudyTables.TryGetStage(stage.StageId, out _))
            .ToArray();

        Dictionary<int, List<uint>> completedPracticeStages = new();
        Dictionary<int, List<StageDatum>> completedTeachingStages = new();
        foreach (StageDatum stage in passedStudyStages)
        {
            if (CurrentClientStudyTables.TryGetPracticeChapterId(stage.StageId, out int chapterId))
            {
                if (!completedPracticeStages.TryGetValue(chapterId, out List<uint>? stageIds))
                    completedPracticeStages.Add(chapterId, stageIds = new());
                stageIds.Add((uint)stage.StageId);
            }

            if (CurrentClientStudyTables.TryGetTeachingActivityIds(stage.StageId, out IReadOnlyList<int> activityIds))
            {
                foreach (int activityId in activityIds)
                {
                    if (!completedTeachingStages.TryGetValue(activityId, out List<StageDatum>? stages))
                        completedTeachingStages.Add(activityId, stages = new());
                    stages.Add(stage);
                }
            }
        }

        session.SendPush(new NotifyPracticeData
        {
            ChapterInfos = completedPracticeStages
                .OrderBy(pair => pair.Key)
                .Select(pair => new NotifyPracticeData.NotifyPracticeDataChapterInfo
                {
                    Id = pair.Key,
                    FinishStages = pair.Value.Distinct().Order().ToList()
                })
                .ToList()
        });

        session.SendPush("NotifyTeachingActivityInfo", MessagePackPayloads.Serialize(new Dictionary<string, object?>
        {
            ["ActivityInfo"] = completedTeachingStages
                .OrderBy(pair => pair.Key)
                .Select(pair => new Dictionary<string, object?>
                {
                    ["Id"] = pair.Key,
                    // Every claim requires a passed activity stage, so claimed activities are always listed here.
                    ["TreasureRecord"] = TableReaderV2.Parse<TeachingActivityTable>()
                        .Where(activity => activity.Id == pair.Key)
                        .SelectMany(activity => activity.TreasureId)
                        .Where(id => id > 0 && IsTreasureClaimed(session, id))
                        .ToArray(),
                    ["StarRecords"] = pair.Value
                        .GroupBy(stage => stage.StageId)
                        .OrderBy(group => group.Key)
                        .Select(group => new Dictionary<string, object?>
                        {
                            ["Id"] = group.Key,
                            ["StarsMark"] = group.Last().StarsMark
                        })
                        .ToArray()
                })
                .ToArray()
        }));
    }

    internal static void SendTeachingStageUpdate(Session session, StageDatum stage)
    {
        if (!stage.Passed || !CurrentClientStudyTables.TryGetTeachingActivityIds(stage.StageId, out _))
            return;

        session.SendPush("NotifyTeachingUpdateStageInfo", MessagePackPayloads.Serialize(new Dictionary<string, object?>
        {
            ["Info"] = new Dictionary<string, object?>
            {
                ["Id"] = stage.StageId,
                ["StarsMark"] = stage.StarsMark
            }
        }));
    }
}
