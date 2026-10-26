using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.boardeffect;
using AscNet.Table.V2.share.miniactivity.stagememory;
using AscNet.Table.V2.share.newbirthdayplot;
using AscNet.Table.V2.share.reward;
using MessagePack;

namespace AscNet.GameServer.Handlers;

[MessagePackObject(true)] public sealed class ViewBirthdaySingleStoryRequest { public int StoryId { get; set; } }
[MessagePackObject(true)] public sealed class ViewBirthdaySingleStoryResponse { public int Code { get; set; } }
[MessagePackObject(true)] public sealed class StageMemoryGetRewardRequest { public int Index { get; set; } }
[MessagePackObject(true)] public sealed class StageMemoryGetRewardResponse { public int Code { get; set; } public List<RewardGoods> RewardList { get; set; } = new(); }
[MessagePackObject(true)] public sealed class NotifyStageMemoryActivity { public int ActivityId { get; set; } public List<int> GotRewardIndexSet { get; set; } = new(); }
[MessagePackObject(true)] public sealed class BoardEffectDataDto { public int ActivityId { get; set; } public long LastTriggerTime { get; set; } }
[MessagePackObject(true)] public sealed class NotifyBoardEffectData { public BoardEffectDataDto BoardEffectData { get; set; } = new(); }
[MessagePackObject(true)] public sealed class BoardEffectTriggerResponse { public int Code { get; set; } public long LastTriggerTime { get; set; } }

/// <summary>
/// Birthday single-story "invite" view, Stage Memory milestone rewards, and main-menu board effect trigger.
/// CharacterUnlockRewardRequest is not implemented: XFavorabilityAgency.OnCollectCharacterReward
/// (XFavorabilityAgency.lua:1567) has no caller in the 4.8 client Lua (line 128/187 bind a nil local).
/// </summary>
internal static class FavorMemoryModule
{
    private const int BirthdayPlotCgNotLock = 20154002, BirthdaySingleStoryCfgNotExist = 20154003;
    private const int BoardEffectActivityNotOpen = 20234001, BoardEffectActivityCfgNotExist = 20234002, BoardEffectTriggerInCd = 20234003;
    private const int StageMemoryActivityNotOpen = 20238001, StageMemoryClientErrorRewardIndex = 20238005,
        StageMemoryRewardAlreadyGot = 20238006, StageMemoryStarNotEnough = 20238007;

    // Client CheckInTimeByTimeId(timeId, defaultOpen): no TimeId or no calendar entry = open.
    // AscNet policy: Stage Memory / Board Effect have no authoritative calendar, so they are open.
    private static bool InTime(int timeId, DateTimeOffset now) =>
        timeId <= 0 || !ActivityScheduleService.TryGet(timeId, out ActivityScheduleEntry entry) || entry.IsOpen(now);

    // AscNet policy: with several open rows the highest Id is the current activity.
    private static StageMemoryActivityTable? StageMemory(DateTimeOffset now) =>
        TableReaderV2.Parse<StageMemoryActivityTable>().Where(row => InTime(row.TimeId, now)).OrderByDescending(row => row.Id).FirstOrDefault();

    private static BoardEffectActivityTable? BoardEffect(DateTimeOffset now) =>
        TableReaderV2.Parse<BoardEffectActivityTable>().Where(row => InTime(row.TimeId, now)).OrderByDescending(row => row.Id).FirstOrDefault();

    internal static List<int> BirthdayStoryIds(Player player) => player.BirthdaySingleStoryIds.ToList();

    internal static void SendLoginPushes(Session session)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (BoardEffect(now) is { } board)
            session.SendPush(new NotifyBoardEffectData
            {
                BoardEffectData = new() { ActivityId = board.Id, LastTriggerTime = session.player.BoardEffectLastTriggerTime }
            });
        if (StageMemory(now) is { } memory)
            session.SendPush(new NotifyStageMemoryActivity
            {
                ActivityId = memory.Id,
                GotRewardIndexSet = session.player.StageMemoryClaims.FirstOrDefault(c => c.ActivityId == memory.Id)?.Indices.ToList() ?? new()
            });
    }

    [RequestPacketHandler("ViewBirthdaySingleStoryRequest")]
    public static void ViewBirthdaySingleStory(Session session, Packet.Request packet)
    {
        var request = packet.Deserialize<ViewBirthdaySingleStoryRequest>();
        var response = new ViewBirthdaySingleStoryResponse();
        lock (session.player)
        {
            if (!TableReaderV2.Parse<SingleStoryTable>().Any(row => row.Id == request.StoryId))
                response.Code = BirthdaySingleStoryCfgNotExist;
            // The client only offers the story once a birthday is set (XBirthdayPlotAgency:IsShowBirthdayBtn).
            else if (session.player.PlayerData.Birthday is null)
                response.Code = BirthdayPlotCgNotLock;
            else if (!session.player.BirthdaySingleStoryIds.Contains(request.StoryId))
            {
                session.player.BirthdaySingleStoryIds.Add(request.StoryId);
                try { session.player.SaveChecked(); }
                catch { session.player.BirthdaySingleStoryIds.Remove(request.StoryId); throw; }
            }
        }
        session.SendResponse(response, packet.Id);
    }

    [RequestPacketHandler("StageMemoryGetRewardRequest")]
    public static void StageMemoryGetReward(Session session, Packet.Request packet)
    {
        var request = packet.Deserialize<StageMemoryGetRewardRequest>();
        var response = new StageMemoryGetRewardResponse();
        lock (session.player)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            StageMemoryActivityTable? activity = StageMemory(now);
            int slot = request.Index - 1;
            StageMemoryClaimState? claim = activity is null ? null : session.player.StageMemoryClaims.FirstOrDefault(c => c.ActivityId == activity.Id);
            if (activity is null) response.Code = StageMemoryActivityNotOpen;
            else if (slot < 0 || slot >= activity.RewardIds.Count || slot >= activity.RequireStages.Count)
                response.Code = StageMemoryClientErrorRewardIndex;
            else if (claim?.Indices.Contains(request.Index) == true) response.Code = StageMemoryRewardAlreadyGot;
            else
            {
                // Client XStageMemoryControl:UpdateUiData counts stages that are passed and inside their StageTimeIds window.
                int passed = activity.StageIds.Where((id, i) => i < activity.StageTimeIds.Count && InTime(activity.StageTimeIds[i], now)
                    && session.stage.Stages.TryGetValue(id, out StageDatum? stage) && stage.Passed).Count();
                List<RewardGoodsTable> goods = RewardHandler.GetRewardGoods(activity.RewardIds[slot]);
                if (passed < activity.RequireStages[slot]) response.Code = StageMemoryStarNotEnough;
                else if (goods.Count == 0) response.Code = StageMemoryClientErrorRewardIndex;
                else
                {
                    RewardApplicationResult result = RewardHandler.ApplyRewardsOnceAndPersist(
                        [new RewardGrant($"stage-memory:{session.player.PlayerData.Id}:{activity.Id}:{request.Index}", goods)], session);
                    bool created = claim is null;
                    if (created) session.player.StageMemoryClaims.Add(claim = new() { ActivityId = activity.Id });
                    claim!.Indices.Add(request.Index);
                    try { session.player.SaveChecked(); }
                    catch
                    {
                        claim.Indices.Remove(request.Index);
                        if (created) session.player.StageMemoryClaims.Remove(claim);
                        throw;
                    }
                    response.RewardList = result.RewardGoods;
                    session.SendResponse(response, packet.Id);
                    result.SendPushes(session);
                    return;
                }
            }
        }
        session.SendResponse(response, packet.Id);
    }

    [RequestPacketHandler("BoardEffectTriggerRequest")]
    public static void BoardEffectTrigger(Session session, Packet.Request packet)
    {
        var response = new BoardEffectTriggerResponse();
        lock (session.player)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            BoardEffectActivityTable? activity = BoardEffect(now);
            long last = session.player.BoardEffectLastTriggerTime;
            if (activity is null) response.Code = TableReaderV2.Parse<BoardEffectActivityTable>().Any() ? BoardEffectActivityNotOpen : BoardEffectActivityCfgNotExist;
            else if (last > 0 && now.ToUnixTimeSeconds() - last < activity.TriggerCd) response.Code = BoardEffectTriggerInCd;
            else
            {
                session.player.BoardEffectLastTriggerTime = now.ToUnixTimeSeconds();
                try { session.player.SaveChecked(); }
                catch { session.player.BoardEffectLastTriggerTime = last; throw; }
            }
            response.LastTriggerTime = session.player.BoardEffectLastTriggerTime;
        }
        session.SendResponse(response, packet.Id);
    }
}
