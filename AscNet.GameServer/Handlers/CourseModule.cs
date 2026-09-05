using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.fuben;
using AscNet.Table.V2.share.fuben.course;

namespace AscNet.GameServer.Handlers;

internal static class CourseModule
{
    internal const int InvalidChapter = 20175001, LevelLocked = 20175002, PointLocked = 20175003,
        PreviousChapterLocked = 20175004, InvalidStage = 20175005, PreviousStageLocked = 20175006,
        ResultMissing = 20175007, InvalidReward = 20175008, RewardClaimed = 20175009,
        ChapterMissing = 20175010, RewardPointLocked = 20175011;

    private static readonly Lazy<Dictionary<int, CourseChapterTable>> Chapters = new(() =>
        TableReaderV2.Parse<CourseChapterTable>().ToDictionary(row => row.ChapterId));
    private static readonly Lazy<Dictionary<int, CourseStageTable>> Stages = new(() =>
        TableReaderV2.Parse<CourseStageTable>().ToDictionary(row => row.StageId));
    private static readonly Lazy<Dictionary<int, CourseChapterTable>> StageChapters = new(() =>
        Chapters.Value.Values.SelectMany(chapter => chapter.StageIds.Select(id => (id, chapter)))
            .ToDictionary(pair => pair.id, pair => pair.chapter));
    private static readonly Lazy<Dictionary<int, CourseRewardTable>> Rewards = new(() =>
        TableReaderV2.Parse<CourseRewardTable>().ToDictionary(row => row.Id));

    internal static bool IsStage(uint stageId) => stageId <= int.MaxValue && Stages.Value.ContainsKey((int)stageId);

    private static int StagePoint(CourseStageState stage)
    {
        if (!Stages.Value.TryGetValue(stage.Id, out CourseStageTable? config)) return 0;
        int point = 0;
        for (int index = 0; index < config.StarPoint.Count; index++)
            if ((stage.StarsFlag & (1 << index)) != 0) point += config.StarPoint[index];
        return point;
    }

    private static int ChapterPoint(Player player, CourseChapterTable chapter) =>
        player.Course.Stages.Where(stage => chapter.StageIds.Contains(stage.Id)).Sum(StagePoint);

    private static int TotalLessonPoint(Player player) => Chapters.Value.Values
        .Where(chapter => chapter.StageType == 1).Sum(chapter => ChapterPoint(player, chapter));

    internal static bool TryGetChapterComplete(Player player, int chapterId, out bool complete)
    {
        complete = false;
        if (!Chapters.Value.TryGetValue(chapterId, out CourseChapterTable? chapter)) return true;
        if (chapter.ClearPoint is not > 0) return false;
        complete = chapter.StageIds.Count > 0
            && chapter.StageIds.All(id => player.Course.Stages.Any(stage => stage.Id == id))
            && ChapterPoint(player, chapter) >= chapter.ClearPoint.Value;
        return true;
    }

    internal static bool IsChapterComplete(Player player, int chapterId) =>
        TryGetChapterComplete(player, chapterId, out bool complete) ? complete
            : throw new InvalidOperationException($"Course chapter {chapterId}: authoritative lesson completion rule is unavailable.");

    private static CourseChapterData ChapterData(Player player, CourseChapterTable chapter) => new()
    {
        Id = chapter.ChapterId,
        IsClear = IsChapterComplete(player, chapter.ChapterId),
        TotalPoint = ChapterPoint(player, chapter)
    };

    private static CourseStageData StageData(CourseStageState stage) => new() { Id = stage.Id, StarsFlag = stage.StarsFlag };

    internal static NotifyCourseData BuildLoginData(Player player) => new()
    {
        Data = new()
        {
            TotalLessonPoint = TotalLessonPoint(player),
            MaxTotalLessonPoint = player.Course.MaxTotalLessonPoint,
            ChapterDataList = Chapters.Value.Values
                // Unknown lesson IsClear cannot be represented by the authoritative boolean wire field.
                .Where(chapter => chapter.ClearPoint is > 0
                    && player.Course.Stages.Any(stage => chapter.StageIds.Contains(stage.Id)))
                .Select(chapter => ChapterData(player, chapter)).ToList(),
            StageDataDict = player.Course.Stages.Where(stage => Stages.Value.ContainsKey(stage.Id))
                .ToDictionary(stage => stage.Id, StageData),
            RewardIds = player.Course.RewardIds.ToList()
        }
    };

    internal static int ValidatePreFight(Session session, uint stageId)
    {
        if (!IsStage(stageId)) return 0;
        if (!StageChapters.Value.TryGetValue((int)stageId, out CourseChapterTable? chapter)) return InvalidChapter;
        if (session.player.PlayerData.Level < (chapter.UnlockLv ?? 0)) return LevelLocked;
        if (session.player.Course.MaxTotalLessonPoint < (chapter.UnlockLessonPoint ?? 0)) return PointLocked;
        if (chapter.PrevChapterIds is > 0 && !IsChapterComplete(session.player, chapter.PrevChapterIds.Value))
            return PreviousChapterLocked;
        CourseStageTable stage = Stages.Value[(int)stageId];
        if (stage.PrevStageId is > 0 && !session.player.Course.Stages.Any(saved => saved.Id == stage.PrevStageId.Value))
            return PreviousStageLocked;
        return 0;
    }

    internal static int ValidateBattleResult(FightSettleResult result)
    {
        if (!IsStage(result.StageId)) return 0;
        CourseStageTable stage = Stages.Value[(int)result.StageId];
        int mask = (1 << stage.StarPoint.Count) - 1;
        StageTable? battle = TableReaderV2.Parse<StageTable>().FirstOrDefault(row => row.StageId == result.StageId);
        return result.AddStars < 0 || (result.AddStars & ~mask) != 0
            || result.LeftTime < 0 || battle is null || result.LeftTime > battle.PassTimeLimit
            ? InvalidStage : 0;
    }

    // Called only after FightModule authenticates and accepts the current fight result.
    internal static bool RecordBattleResult(Session session, FightSettleResult result)
    {
        if (!IsStage(result.StageId) || !result.IsWin || result.IsForceExit || ValidateBattleResult(result) != 0)
            return false;
        session.player.Course.PendingResult = new() { Id = (int)result.StageId, StarsFlag = result.AddStars };
        session.player.SaveChecked();
        return true;
    }

    internal static void CancelPendingResult(Session session)
    {
        if (session.player.Course.PendingResult is null) return;
        session.player.Course.PendingResult = null;
        session.player.SaveChecked();
    }

    [RequestPacketHandler("CourseSaveResultRequest")]
    public static void SaveResult(Session session, Packet.Request packet)
    {
        CourseStageState? pending = session.player.Course.PendingResult;
        if (pending is null || !StageChapters.Value.TryGetValue(pending.Id, out CourseChapterTable? chapter))
        {
            session.SendResponse(new CourseSaveResultResponse { Code = ResultMissing }, packet.Id);
            return;
        }
        if (!TryGetChapterComplete(session.player, chapter.ChapterId, out _))
        {
            session.log.Error($"CourseSaveResultRequest blocked for chapter {chapter.ChapterId}: authoritative lesson completion rule is unavailable; pending result retained.");
            session.SendResponse(new CourseSaveResultResponse { Code = 1 }, packet.Id);
            return;
        }
        CourseStageState? saved = session.player.Course.Stages.FirstOrDefault(stage => stage.Id == pending.Id);
        bool fullChapter = chapter.StageIds.All(id => session.player.Course.Stages.Any(stage =>
            stage.Id == id && stage.StarsFlag == (1 << Stages.Value[id].StarPoint.Count) - 1));
        if (saved is null)
        {
            saved = new() { Id = pending.Id, StarsFlag = pending.StarsFlag };
            session.player.Course.Stages.Add(saved);
        }
        else if (!fullChapter) saved.StarsFlag = pending.StarsFlag;
        int total = TotalLessonPoint(session.player);
        session.player.Course.MaxTotalLessonPoint = Math.Max(session.player.Course.MaxTotalLessonPoint, total);
        session.player.Course.PendingResult = null;
        session.player.SaveChecked();
        // The client compares its old chapter state before consuming this response; do not push it ahead of the response.
        session.SendResponse(new CourseSaveResultResponse
        {
            ChapterData = ChapterData(session.player, chapter),
            StageData = StageData(saved),
            TotalLessonPoint = total,
            MaxTotalLessonPoint = session.player.Course.MaxTotalLessonPoint
        }, packet.Id);
    }

    [RequestPacketHandler("CourseGetRewardRequest")]
    public static void GetReward(Session session, Packet.Request packet)
    {
        CourseGetRewardRequest? request = packet.Deserialize<CourseGetRewardRequest>();
        List<int>? ids = request?.RewardIds;
        int code = ids is null || ids.Count == 0 || ids.Distinct().Count() != ids.Count ? InvalidReward : 0;
        if (code == 0 && ids is not null)
        {
            foreach (int id in ids)
            {
                if (!Rewards.Value.TryGetValue(id, out CourseRewardTable? reward)) { code = InvalidReward; break; }
                if (session.player.Course.RewardIds.Contains(id)) { code = RewardClaimed; break; }
                CourseChapterTable chapter = Chapters.Value[reward.ChapterId];
                if (!session.player.Course.Stages.Any(stage => chapter.StageIds.Contains(stage.Id))) { code = ChapterMissing; break; }
                if (ChapterPoint(session.player, chapter) < reward.Point) { code = RewardPointLocked; break; }
            }
        }
        if (code != 0 || ids is null)
        {
            session.SendResponse(new CourseGetRewardResponse { Code = code }, packet.Id);
            return;
        }
        RewardApplicationResult application = RewardHandler.ApplyRewardsOnceAndPersist(ids.Select(id =>
            new RewardGrant($"course:{session.player.PlayerData.Id}:{id}", RewardHandler.GetRewardGoods(Rewards.Value[id].RewardId))).ToList(), session);
        session.player.Course.RewardIds.AddRange(ids);
        session.player.SaveChecked();
        application.SendPushes(session);
        session.SendResponse(new CourseGetRewardResponse { RewardGoodsList = application.RewardGoods, SuccessRewardIds = ids }, packet.Id);
    }
}
