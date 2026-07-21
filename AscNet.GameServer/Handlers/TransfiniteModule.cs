using System.Globalization;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.character.skill;
using AscNet.Table.V2.share.fuben.transfinite;
using AscNet.Table.V2.share.reward;
using AscNet.Table.V2.share.item;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MessagePack;

namespace AscNet.GameServer.Handlers;

[MessagePackObject(true)] public sealed class TransfiniteGetRotateSettleInfoRequest { }
[MessagePackObject(true)] public sealed class TransfiniteGetRotateSettleInfoResponse { public int Code { get; set; } public int MaxStageProgressIndex { get; set; } public int SettleTransfiniteScore { get; set; } public int UnSettleTransfiniteScore { get; set; } public List<RewardGoods> RewardGoodsList { get; set; } = new(); }
[MessagePackObject(true)] public sealed class NotifyTransfiniteData { public TransfiniteData? TransfiniteData { get; set; } }
[MessagePackObject(true)] public sealed class TransfiniteData { public int ActivityId { get; set; } public int CircleId { get; set; } public long BeginTime { get; set; } public int RegionId { get; set; } public int StageGroupIndex { get; set; } public List<TransfiniteBattleInfo> BattleInfo { get; set; } = new(); public List<TransfiniteBestSpendTime> BestSpendTime { get; set; } = new(); public List<int> GotScoreRewardIndex { get; set; } = new(); public bool SendActivityStartMail { get; set; } public int MaxRotateStageProgressIndex { get; set; } public TransfiniteRotateSettleInfo? RotateSettleInfo { get; set; } public int StageGroupId { get; set; } public long LastModifyTime { get; set; } public int ScoreRewardGroupId { get; set; } }
[MessagePackObject(true)] public sealed class TransfiniteBattleInfo { public int StageGroupId { get; set; } public int StageProgressIndex { get; set; } public int StartStageProgress { get; set; } public TransfiniteTeamInfo? TeamInfo { get; set; } public List<TransfiniteStageInfo> StageInfo { get; set; } = new(); public TransfiniteBattleResult? Result { get; set; } public TransfiniteBattleResult? LastResult { get; set; } public List<TransfiniteBattleResult> HistoryResults { get; set; } = new(); }
[MessagePackObject(true)] public sealed class TransfiniteTeamInfo { public List<long> CharacterIdList { get; set; } = new(); public int CaptainPos { get; set; } public int FirstFightPos { get; set; } public int SelectedGeneralSkill { get; set; } public int EnterCgIndex { get; set; } public int SettleCgIndex { get; set; } }
[MessagePackObject(true)] public sealed class TransfiniteStageInfo { public int StageId { get; set; } public bool IsWin { get; set; } public int SpendTime { get; set; } public int Score { get; set; } }
[MessagePackObject(true)] public sealed class TransfiniteBattleResult { public int LastWinStageId { get; set; } public List<TransfiniteCharacterResult> CharacterResultList { get; set; } = new(); public int StageSpendTime { get; set; } }
[MessagePackObject(true)] public sealed class TransfiniteCharacterResult { public long CharacterId { get; set; } public int HpPercent { get; set; } public int Energy { get; set; } }
[MessagePackObject(true)] public sealed class TransfiniteBestSpendTime { public int StageGroupId { get; set; } public int BestSpendTime { get; set; } }
[MessagePackObject(true)] public sealed class TransfiniteRotateSettleInfo { public int MaxStageProgressIndex { get; set; } public int ScoreRewardGroupId { get; set; } public int SettleTransfiniteScore { get; set; } public int UnSettleTransfiniteScore { get; set; } public List<int> GotScoreRewardIndex { get; set; } = new(); }
[MessagePackObject(true)] public sealed class TransfiniteSetTeamRequest { public int StageGroupId { get; set; } public TransfiniteTeamInfo? TeamInfo { get; set; } public bool ResetStageIndex { get; set; } }
[MessagePackObject(true)] public sealed class TransfiniteSetTeamResponse { public int Code { get; set; } public TransfiniteBattleInfo? BattleInfo { get; set; } }
[MessagePackObject(true)] public sealed class TransfiniteConfirmBattleResultRequest { public int StageGroupId { get; set; } public bool IsGiveUp { get; set; } }
[MessagePackObject(true)] public sealed class TransfiniteConfirmBattleResultResponse { public int Code { get; set; } public List<RewardGoods>? RewardGoodsList { get; set; } public TransfiniteBattleInfo? BattleInfo { get; set; } }
[MessagePackObject(true)] public sealed class TransfiniteResetStageGroupRequest { public int StageGroupId { get; set; } }
[MessagePackObject(true)] public sealed class TransfiniteResetStageGroupResponse { public int Code { get; set; } public TransfiniteBattleInfo? BattleInfo { get; set; } }
[MessagePackObject(true)] public sealed class TransfiniteGetScoreRewardRequest { public List<int> ScoreRewardIndex { get; set; } = new(); }
[MessagePackObject(true)] public sealed class TransfiniteGetScoreRewardResponse { public int Code { get; set; } public List<RewardGoods> RewardGoodsList { get; set; } = new(); public List<int> GotScoreRewardIndex { get; set; } = new(); }

internal static class TransfiniteModule
{
    internal const int ActivityNotOpen = 20008001, ConfigNotFound = 20008002, NoRotateSettlement = 20008003;
    // XItemManager.ItemId.TransfiniteScore = 105 in the authoritative client enum.
    private const int TransfiniteScoreItemId = 105;
    private static readonly Lazy<Dictionary<int, TransfiniteActivityTable>> Activities = new(() => TableReaderV2.Parse<TransfiniteActivityTable>().ToDictionary(x => x.Id));
    private static readonly Lazy<List<TransfiniteRegionTable>> Regions = new(() => TableReaderV2.Parse<TransfiniteRegionTable>());
    private static readonly Lazy<Dictionary<int, TransfiniteRotateGroupTable>> Rotates = new(() => TableReaderV2.Parse<TransfiniteRotateGroupTable>().ToDictionary(x => x.RotateGroupId));
    private static readonly Lazy<Dictionary<int, TransfiniteStageGroupTable>> Groups = new(() => TableReaderV2.Parse<TransfiniteStageGroupTable>().ToDictionary(x => x.StageGroupId));
    private static readonly Lazy<Dictionary<int, TransfiniteStageTable>> Stages = new(() => TableReaderV2.Parse<TransfiniteStageTable>().ToDictionary(x => x.StageId));
    private static readonly Lazy<List<TransfiniteIslandTable>> Islands = new(() => TableReaderV2.Parse<TransfiniteIslandTable>());
    private static readonly Lazy<List<TransfiniteScoreRewardGroupTable>> Rewards = new(() => TableReaderV2.Parse<TransfiniteScoreRewardGroupTable>());
    private static readonly Lazy<List<TransfiniteStartStageProgressTable>> StartProgress = new(() => TableReaderV2.Parse<TransfiniteStartStageProgressTable>());
    private static readonly Lazy<HashSet<int>> GeneralSkills = new(() => TableReaderV2.Parse<CharacterGeneralSkillTable>().Select(x => x.Id).ToHashSet());
    private static readonly Lazy<Dictionary<int, ItemTable>> Items = new(() => TableReaderV2.Parse<ItemTable>().ToDictionary(x => x.Id));
    internal static bool IsStage(uint id) => id <= int.MaxValue && Stages.Value.ContainsKey((int)id);
    private static bool Authorized(TransfiniteState? x) => x is not null && x.ActivityAuthorizedUntil >= DateTimeOffset.UtcNow.ToUnixTimeSeconds() && Activities.Value.ContainsKey(x.ActivityId);

    internal static void PrepareLogin(Player player, long now)
    {
        DateTimeOffset time = DateTimeOffset.FromUnixTimeSeconds(now);
        TransfiniteActivityTable? activity = Activities.Value.Values.Where(x => ActivityScheduleService.TryGet(x.TimeId, out ActivityScheduleEntry s) && s.IsOpen(time)).OrderByDescending(x => x.Id).FirstOrDefault();
        if (activity is null || !ActivityScheduleService.TryGet(activity.TimeId, out ActivityScheduleEntry schedule) || !Select(activity, (int)player.PlayerData.Level, now, out TransfiniteState? next))
        {
            if (player.Transfinite is { ActivityAuthorizedUntil: not 0 } inactive)
            {
                inactive.ActivityAuthorizedUntil = 0;
                player.Save();
            }
            return;
        }
        next!.ActivityAuthorizedUntil = schedule.EndTime == 0 ? long.MaxValue : schedule.EndTime;
        if (player.Transfinite is { } old)
        {
            if (old.ActivityId == next.ActivityId && old.CircleId == next.CircleId)
            {
                if (old.ActivityAuthorizedUntil == next.ActivityAuthorizedUntil
                    && old.BeginTime == next.BeginTime
                    && old.RegionId == next.RegionId
                    && old.StageGroupIndex == next.StageGroupIndex
                    && old.StageGroupId == next.StageGroupId
                    && old.ScoreRewardGroupId == next.ScoreRewardGroupId)
                    return;
                old.ActivityAuthorizedUntil = next.ActivityAuthorizedUntil;
                old.BeginTime = next.BeginTime;
                old.RegionId = next.RegionId;
                old.StageGroupIndex = next.StageGroupIndex;
                old.StageGroupId = next.StageGroupId;
                old.ScoreRewardGroupId = next.ScoreRewardGroupId;
                if (old.BattleInfo is { } battle && !IsAllowedGroup(old, battle.StageGroupId))
                    old.BattleInfo = null;
                player.Save();
                return;
            }
            int outgoing = old.MaxRotateStageProgressIndex;
            if (old.BattleInfo?.StageGroupId == old.StageGroupId)
                outgoing = Math.Max(outgoing, old.BattleInfo.StageProgressIndex);
            if (old.ActivityId == next.ActivityId)
            {
                int anchor = StartProgress.Value.Where(x => x.LastProgress <= outgoing).OrderByDescending(x => x.LastProgress).Select(x => x.StartProgress).FirstOrDefault();
                if (anchor > 0)
                    next.BattleInfo = new() { StageGroupId = next.StageGroupId, StageProgressIndex = anchor - 1, StartStageProgress = anchor };
                next.MaxRotateStageProgressIndex = outgoing;
            }
            next.RotateSettleInfo = new() { RotationId = old.CircleId, RegionId = old.RegionId, MaxStageProgressIndex = outgoing, ScoreRewardGroupId = 0 };
        }
        player.Transfinite = next; player.Save();
    }
    private static bool Select(TransfiniteActivityTable activity, int level, long now, out TransfiniteState? state)
    { state = null; if (activity.CycleSeconds <= 0) return false; TransfiniteRegionTable? region = Regions.Value.SingleOrDefault(x => level >= x.MinLv && level <= x.MaxLv); if (region is null || !Rotates.Value.TryGetValue(region.RotateGroupId, out var rotate) || rotate.StageGroupId.Count == 0) return false; long begin = now - now % activity.CycleSeconds, circle = begin / activity.CycleSeconds + 1; if (circle > int.MaxValue) return false; int index = (int)((circle - 1) % rotate.StageGroupId.Count), group = rotate.StageGroupId[index]; if (!Groups.Value.ContainsKey(group) || !Rewards.Value.Any(x => x.RegionId == region.RegionId && x.ScoreRewardGroupId == region.ScoreRewardGroupId)) return false; state = new() { ActivityId = activity.Id, CircleId = (int)circle, BeginTime = begin, RegionId = region.RegionId, StageGroupIndex = index, StageGroupId = group, ScoreRewardGroupId = region.ScoreRewardGroupId }; return true; }
    internal static NotifyTransfiniteData BuildNotify(Player player) => Authorized(player.Transfinite) ? new() { TransfiniteData = ToWire(player.Transfinite!) } : new();

    [RequestPacketHandler("TransfiniteSetTeamRequest")] public static void SetTeam(Session s, Packet.Request p) { var r = p.Deserialize<TransfiniteSetTeamRequest>(); var state = s.player.Transfinite; if (!Authorized(state)) { s.SendResponse(new TransfiniteSetTeamResponse { Code = ActivityNotOpen }, p.Id); return; } if (r.TeamInfo is null || !IsAllowedGroup(state!, r.StageGroupId) || !ValidTeam(s, r.TeamInfo)) { s.SendResponse(new TransfiniteSetTeamResponse { Code = ConfigNotFound }, p.Id); return; } if (s.fight is not null || state!.BattleInfo?.LastResult is not null) { s.SendResponse(new TransfiniteSetTeamResponse { Code = ConfigNotFound }, p.Id); return; } var battle = state.BattleInfo; if (battle is null) battle = new() { StageGroupId = r.StageGroupId, StartStageProgress = 1, TeamInfo = ToState(r.TeamInfo), Result = new() }; else if (battle.StageGroupId != r.StageGroupId) { s.SendResponse(new TransfiniteSetTeamResponse { Code = ConfigNotFound }, p.Id); return; } else { battle.TeamInfo = ToState(r.TeamInfo); battle.Result ??= new(); } state.BattleInfo = battle; state.LastModifyTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(); s.player.Save(); s.SendResponse(new TransfiniteSetTeamResponse { BattleInfo = ToWire(battle) }, p.Id); }
    [RequestPacketHandler("TransfiniteConfirmBattleResultRequest")]
    public static void Confirm(Session s, Packet.Request p)
    {
        var r = p.Deserialize<TransfiniteConfirmBattleResultRequest>();
        var state = s.player.Transfinite;
        var battle = state?.BattleInfo;
        if (!Authorized(state)) { s.SendResponse(new TransfiniteConfirmBattleResultResponse { Code = ActivityNotOpen }, p.Id); return; }
        if (!IsAllowedGroup(state!, r.StageGroupId) || battle?.LastResult is null || battle.StageGroupId != r.StageGroupId) { s.SendResponse(new TransfiniteConfirmBattleResultResponse { Code = ConfigNotFound }, p.Id); return; }

        TransfiniteState stateSnapshot = BsonSerializer.Deserialize<TransfiniteState>(state.ToBson());
        MissionProgressState progressSnapshot = BsonSerializer.Deserialize<MissionProgressState>(s.player.MissionProgress.ToBson());
        if (r.IsGiveUp)
        {
            battle.LastResult = null;
            try { s.player.Save(); }
            catch { s.player.Transfinite = stateSnapshot; s.SendResponse(new TransfiniteConfirmBattleResultResponse { Code = ConfigNotFound }, p.Id); return; }
            s.SendResponse(new TransfiniteConfirmBattleResultResponse { BattleInfo = ToWire(battle) }, p.Id);
            return;
        }

        int stageId = battle.LastResult.LastWinStageId;
        if (!Expected(battle, stageId) || !Stages.Value.TryGetValue(stageId, out var row)) { s.SendResponse(new TransfiniteConfirmBattleResultResponse { Code = ConfigNotFound }, p.Id); return; }
        int score = row.Score ?? 0;
        int extra = row.ExtraScore ?? 0;
        if (score < 0 || extra < 0 || (extra > 0 && (row.ExtraTimeLimit is null || row.ExtraTimeLimit <= 0))) { s.SendResponse(new TransfiniteConfirmBattleResultResponse { Code = ConfigNotFound }, p.Id); return; }
        if (extra > 0 && battle.LastResult.StageSpendTime < row.ExtraTimeLimit) score = checked(score + extra);

        battle.StageInfo.Add(new() { StageId = stageId, IsWin = true, SpendTime = battle.LastResult.StageSpendTime, Score = score });
        battle.HistoryResults.Add(battle.LastResult);
        battle.Result = battle.LastResult;
        battle.LastResult = null;
        battle.StageProgressIndex++;
        if (battle.StageGroupId == state.StageGroupId) state.MaxRotateStageProgressIndex = Math.Max(state.MaxRotateStageProgressIndex, battle.StageProgressIndex);
        bool terminal = battle.StageProgressIndex >= Groups.Value[battle.StageGroupId].StageId.Count;
        if (terminal)
        {
            int total = battle.StageInfo.Sum(x => x.Score);
            int spend = battle.StageInfo.Sum(x => x.SpendTime);
            if (!state.BestSpendTime.TryGetValue(battle.StageGroupId, out int best) || spend < best) state.BestSpendTime[battle.StageGroupId] = spend;
            state.BattleInfo = null;
        }

        NotifyTask? taskUpdate = TaskModule.RecordTransfiniteConfirmedProgress(s, battle.StageGroupId, stageId, battle.Result!.StageSpendTime, row.ExtraTimeLimit, battle.StageProgressIndex);
        try
        {
            RewardApplicationResult? applied = null;
            if (terminal)
            {
                int total = battle.StageInfo.Sum(x => x.Score);
                applied = RewardHandler.ApplyRewardsOnceAndPersist(
                    [new RewardGrant($"transfinite-terminal:{state.ActivityId}:{state.CircleId}:{battle.StageGroupId}", [new RewardGoodsTable { Id = TransfiniteScoreItemId, TemplateId = TransfiniteScoreItemId, Count = total }])], s);
            }
            s.player.Save();
            if (taskUpdate is not null) s.SendPush(taskUpdate);
            if (applied is not null) applied.SendPushes(s);
            s.SendResponse(terminal
                ? new TransfiniteConfirmBattleResultResponse { RewardGoodsList = applied!.RewardGoods }
                : new TransfiniteConfirmBattleResultResponse { BattleInfo = ToWire(battle) }, p.Id);
        }
        catch
        {
            s.player.Transfinite = stateSnapshot;
            s.player.MissionProgress = progressSnapshot;
            s.SendResponse(new TransfiniteConfirmBattleResultResponse { Code = ConfigNotFound }, p.Id);
        }
    }
    [RequestPacketHandler("TransfiniteResetStageGroupRequest")] public static void Reset(Session s, Packet.Request p) { _ = p.Deserialize<TransfiniteResetStageGroupRequest>(); s.SendResponse(new TransfiniteResetStageGroupResponse { Code = ConfigNotFound }, p.Id); }
    [RequestPacketHandler("TransfiniteGetScoreRewardRequest")] public static void GetScoreReward(Session s, Packet.Request p) { var r = p.Deserialize<TransfiniteGetScoreRewardRequest>(); var state = s.player.Transfinite; if (!Authorized(state)) { s.SendResponse(new TransfiniteGetScoreRewardResponse { Code = ActivityNotOpen }, p.Id); return; } var group = Rewards.Value.SingleOrDefault(x => x.RegionId == state!.RegionId && x.ScoreRewardGroupId == state.ScoreRewardGroupId); if (group is null || r.ScoreRewardIndex.Count == 0 || r.ScoreRewardIndex.Distinct().Count() != r.ScoreRewardIndex.Count || r.ScoreRewardIndex.Any(i => i < 0 || i >= group.Score.Count || i >= group.RewardId.Count || state.GotScoreRewardIndex.Contains(i) || group.Score[i] > Score(s) || group.RewardId[i] <= 0)) { s.SendResponse(new TransfiniteGetScoreRewardResponse { Code = ConfigNotFound }, p.Id); return; } try { var applied = RewardHandler.ApplyRewardsOnceAndPersist(r.ScoreRewardIndex.Select(i => new RewardGrant($"transfinite-score:{state.ActivityId}:{state.CircleId}:{i}", RewardHandler.GetRewardGoods(group.RewardId[i]))).ToList(), s); int before = state.GotScoreRewardIndex.Count; state.GotScoreRewardIndex.AddRange(r.ScoreRewardIndex); try { s.player.Save(); } catch { state.GotScoreRewardIndex.RemoveRange(before, r.ScoreRewardIndex.Count); throw; } applied.SendPushes(s); s.SendResponse(new TransfiniteGetScoreRewardResponse { RewardGoodsList = applied.RewardGoods, GotScoreRewardIndex = state.GotScoreRewardIndex.ToList() }, p.Id); } catch { s.SendResponse(new TransfiniteGetScoreRewardResponse { Code = ConfigNotFound }, p.Id); } }
    [RequestPacketHandler("TransfiniteGetRotateSettleInfoRequest")]
    public static void GetRotateSettleInfo(Session s, Packet.Request p)
    {
        _ = p.Deserialize<TransfiniteGetRotateSettleInfoRequest>(); var state = s.player.Transfinite;
        if (!Authorized(state)) { s.SendResponse(new TransfiniteGetRotateSettleInfoResponse { Code = ActivityNotOpen }, p.Id); return; }
        if (state!.RotateSettleInfo is not { } pending) { if (state.LastRotateReceipt is { } completed) { s.SendResponse(ToResponse(completed), p.Id); return; } s.SendResponse(new TransfiniteGetRotateSettleInfoResponse { Code = NoRotateSettlement }, p.Id); return; }
        if (pending.ScoreRewardGroupId == 0) { var sentinelReceipt = new TransfiniteRotateSettleReceipt { RotationId = pending.RotationId, MaxStageProgressIndex = pending.MaxStageProgressIndex, SettleTransfiniteScore = 0, UnSettleTransfiniteScore = 0 }; if (!CommitRotateReceipt(s, state, sentinelReceipt)) { s.SendResponse(new TransfiniteGetRotateSettleInfoResponse { Code = ConfigNotFound }, p.Id); return; } s.SendResponse(ToResponse(sentinelReceipt), p.Id); return; }
        TransfiniteScoreRewardGroupTable? group = Rewards.Value.SingleOrDefault(x => x.RegionId == pending.RegionId && x.ScoreRewardGroupId == pending.ScoreRewardGroupId);
        if (group is null || !TryResolveRotateRewards(pending, group, out List<RewardGoodsTable> rows)) { s.SendResponse(new TransfiniteGetRotateSettleInfoResponse { Code = ConfigNotFound }, p.Id); return; }
        List<TransfiniteInventoryReceipt> matches = s.inventory.TransfiniteReceipts.Where(x => x.ActivityId == state.ActivityId && x.RotationId == pending.RotationId).Take(2).ToList();
        if (matches.Count > 1) { s.SendResponse(new TransfiniteGetRotateSettleInfoResponse { Code = ConfigNotFound }, p.Id); return; }
        if (matches.SingleOrDefault() is { } saved) { if (!ReceiptMatches(saved, pending, rows) || !CommitRotateReceipt(s, state, ToPlayerReceipt(saved))) { s.SendResponse(new TransfiniteGetRotateSettleInfoResponse { Code = ConfigNotFound }, p.Id); return; } SendReceiptPush(s, saved); s.SendResponse(ToResponse(saved), p.Id); return; }
        if (!CanApplyItems(rows, s.inventory)) { s.SendResponse(new TransfiniteGetRotateSettleInfoResponse { Code = ConfigNotFound }, p.Id); return; }
        List<RewardGoods> goods = rows.Select(x => new RewardGoods { Id = x.Id, TemplateId = x.TemplateId, Count = x.Count, RewardType = (int)RewardType.Item }).ToList();
        TransfiniteInventoryReceipt receipt = new() { ActivityId = state.ActivityId, RotationId = pending.RotationId, RegionId = pending.RegionId, ScoreRewardGroupId = pending.ScoreRewardGroupId, MaxStageProgressIndex = pending.MaxStageProgressIndex, SettleTransfiniteScore = pending.SettleTransfiniteScore, UnSettleTransfiniteScore = pending.UnSettleTransfiniteScore, RewardGoods = goods.Select(ToReceipt).ToList() };
        Inventory snapshot = BsonSerializer.Deserialize<Inventory>(s.inventory.ToBson()); List<Item> changed = rows.GroupBy(x => x.TemplateId).Select(x => s.inventory.Do(x.Key, checked(x.Sum(y => y.Count)))).ToList(); s.inventory.TransfiniteReceipts.Add(receipt);
        try { s.inventory.Save(); } catch { s.inventory.Items = snapshot.Items; s.inventory.TransfiniteReceipts = snapshot.TransfiniteReceipts; s.SendResponse(new TransfiniteGetRotateSettleInfoResponse { Code = ConfigNotFound }, p.Id); return; }
        if (!CommitRotateReceipt(s, state, ToPlayerReceipt(receipt))) { s.SendResponse(new TransfiniteGetRotateSettleInfoResponse { Code = ConfigNotFound }, p.Id); return; }
        if (changed.Count > 0) s.SendPush(new NotifyItemDataList { ItemDataList = changed }); s.SendResponse(ToResponse(receipt), p.Id);
    }
    private static bool IsAllowedGroup(TransfiniteState state, int groupId) => groupId == state.StageGroupId || Regions.Value.SingleOrDefault(x => x.RegionId == state.RegionId) is { } region && Islands.Value.Any(x => x.Id == region.IslandId && x.StageGroupId.Contains(groupId));
    internal static bool ApplyPreFight(Session s, PreFightRequest.PreFightRequestPreFightData r, out int code)
    {
        if (!IsStage(r.StageId)) { code = 0; return false; }
        TransfiniteState? state = s.player.Transfinite;
        TransfiniteBattleState? battle = state?.BattleInfo;
        bool valid = Authorized(state)
            && s.fight is null
            && battle is not null
            && IsAllowedGroup(state!, battle.StageGroupId)
            && Expected(battle, (int)r.StageId)
            && (battle.LastResult is null || battle.LastResult.LastWinStageId == (int)r.StageId);
        code = valid ? 0 : (Authorized(state) ? ConfigNotFound : ActivityNotOpen);
        return !valid;
    }
    internal static bool TryCommitPreFight(Session s, uint stageId, out int code)
    {
        code = 0;
        if (!IsStage(stageId)) return true;
        TransfiniteState? state = s.player.Transfinite;
        TransfiniteBattleState? battle = state?.BattleInfo;
        if (!Authorized(state)
            || s.fight is not null
            || battle is null
            || !IsAllowedGroup(state!, battle.StageGroupId)
            || !Expected(battle, (int)stageId)
            || battle.LastResult is { } pending && pending.LastWinStageId != (int)stageId)
        {
            code = Authorized(state) ? ConfigNotFound : ActivityNotOpen;
            return false;
        }
        if (battle.LastResult is not { } retry) return true;
        battle.LastResult = null;
        try { s.player.Save(); }
        catch { battle.LastResult = retry; code = ConfigNotFound; return false; }
        return true;
    }
    internal static bool TrySettle(Session s, FightSettleResult r, out FightSettleResponse response) { if (!IsStage(r.StageId)) { response = null!; return false; } var b = s.player.Transfinite?.BattleInfo; if (!Authorized(s.player.Transfinite) || b is null || b.LastResult is not null || !IsAllowedGroup(s.player.Transfinite!, b.StageGroupId) || !Expected(b, (int)r.StageId) || !r.IsWin || r.IsForceExit) { response = new() { Code = ConfigNotFound }; return true; } b.LastResult = new() { LastWinStageId = (int)r.StageId, StageSpendTime = SafeTime(r.LeftTime), CharacterResultList = Results(s, r) }; s.player.Save(); response = new() { Code = 0, Settle = new() { IsWin = true, StageId = r.StageId, LeftTime = (int)Math.Clamp(r.LeftTime, int.MinValue, int.MaxValue), NpcHpInfo = r.NpcHpInfo, TransfiniteBattleResult = Wire(b.LastResult) } }; return true; }
    private static int SafeTime(long left) => left == long.MinValue ? int.MaxValue : (int)Math.Min(int.MaxValue, Math.Abs(left));
    private static bool Expected(TransfiniteBattleState b, int stage) => Groups.Value.TryGetValue(b.StageGroupId, out var g) && b.StageProgressIndex >= 0 && b.StageProgressIndex < g.StageId.Count && g.StageId[b.StageProgressIndex] == stage;
    private static int Score(Session s) => checked((int)Math.Min(int.MaxValue, s.inventory.Items.Where(x => x.Id == TransfiniteScoreItemId).Sum(x => (long)x.Count)));
    private static bool ValidTeam(Session s, TransfiniteTeamInfo t) { if (t.CharacterIdList.Count != 3 || t.CaptainPos < 1 || t.CaptainPos > 3 || t.FirstFightPos < 1 || t.FirstFightPos > 3 || t.EnterCgIndex < 0 || t.SettleCgIndex < 0 || t.SelectedGeneralSkill < 0 || (t.SelectedGeneralSkill > 0 && !GeneralSkills.Value.Contains(t.SelectedGeneralSkill)) || t.CharacterIdList[t.CaptainPos - 1] <= 0 || t.CharacterIdList[t.FirstFightPos - 1] <= 0) return false; var ids = t.CharacterIdList.Where(x => x > 0).ToList(); return ids.Count is >= 1 and <= 3 && ids.All(id => id <= uint.MaxValue) && ids.Distinct().Count() == ids.Count && ids.All(id => s.character.Characters.Any(c => c.Id == (uint)id)); }
    private static List<TransfiniteCharacterResultState> Results(Session s, FightSettleResult r)
    {
        TransfiniteBattleState battle = s.player.Transfinite!.BattleInfo!;
        Dictionary<long, TransfiniteCharacterResultState> previous = battle.Result?.CharacterResultList.ToDictionary(x => x.CharacterId) ?? new();
        return battle.TeamInfo!.CharacterIdList.Where(x => x > 0).Select(id =>
        {
            List<NpcHp> matches = r.NpcHpInfo?.Values.Where(x => x.CharacterId == id).Take(2).ToList() ?? [];
            NpcHp? npc = matches.Count == 1 ? matches[0] : null;
            TransfiniteCharacterResultState fallback = previous.GetValueOrDefault(id) ?? new() { CharacterId = id, HpPercent = 100, Energy = 0 };
            double max = Attribute(npc, 1, "MaxValue"), value = Attribute(npc, 1, "Value");
            return new TransfiniteCharacterResultState { CharacterId = id, HpPercent = max > 0 ? (int)Math.Clamp(Math.Floor(value * 100 / max), 0, 100) : fallback.HpPercent, Energy = npc is null ? fallback.Energy : (int)Math.Clamp(Math.Floor(Attribute(npc, 2, "Value")), 0, int.MaxValue) };
        }).ToList();
    }
    private static double Attribute(NpcHp? npc, int index, string member)
    {
        if (npc?.AttrTable is null || !npc.AttrTable.TryGetValue(index, out dynamic? value)) return 0;
        if (value is IDictionary<object, object> objects)
        {
            object? entry = objects.FirstOrDefault(x => string.Equals(Convert.ToString(x.Key), member, StringComparison.Ordinal)).Value;
            return double.TryParse(Convert.ToString(entry, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : 0;
        }
        if (value is IDictionary<string, object> strings && strings.TryGetValue(member, out object? stringRaw))
            return double.TryParse(Convert.ToString(stringRaw, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed2) ? parsed2 : 0;
        return 0;
    }
    private static TransfiniteTeamState ToState(TransfiniteTeamInfo x) => new() { CharacterIdList = x.CharacterIdList.ToList(), CaptainPos = x.CaptainPos, FirstFightPos = x.FirstFightPos, SelectedGeneralSkill = x.SelectedGeneralSkill, EnterCgIndex = x.EnterCgIndex, SettleCgIndex = x.SettleCgIndex };
    private static TransfiniteBattleResult? Wire(TransfiniteBattleResultState? x) => x is null ? null : new() { LastWinStageId = x.LastWinStageId, StageSpendTime = x.StageSpendTime, CharacterResultList = x.CharacterResultList.Select(y => new TransfiniteCharacterResult { CharacterId = y.CharacterId, HpPercent = y.HpPercent, Energy = y.Energy }).ToList() };
    private static TransfiniteBattleInfo ToWire(TransfiniteBattleState x) => new() { StageGroupId = x.StageGroupId, StageProgressIndex = x.StageProgressIndex, StartStageProgress = x.StartStageProgress, TeamInfo = x.TeamInfo is null ? null : new() { CharacterIdList = x.TeamInfo.CharacterIdList.ToList(), CaptainPos = x.TeamInfo.CaptainPos, FirstFightPos = x.TeamInfo.FirstFightPos, SelectedGeneralSkill = x.TeamInfo.SelectedGeneralSkill, EnterCgIndex = x.TeamInfo.EnterCgIndex, SettleCgIndex = x.TeamInfo.SettleCgIndex }, StageInfo = x.StageInfo.Select(y => new TransfiniteStageInfo { StageId = y.StageId, IsWin = y.IsWin, SpendTime = y.SpendTime, Score = y.Score }).ToList(), Result = Wire(x.Result) ?? (x.TeamInfo is null ? null : new()), LastResult = Wire(x.LastResult), HistoryResults = x.HistoryResults.Select(Wire).Where(y => y is not null).Cast<TransfiniteBattleResult>().ToList() };
    private static TransfiniteData ToWire(TransfiniteState x) => new()
    {
        ActivityId = x.ActivityId,
        CircleId = x.CircleId,
        BeginTime = x.BeginTime,
        RegionId = x.RegionId,
        StageGroupIndex = x.StageGroupIndex,
        StageGroupId = x.StageGroupId,
        ScoreRewardGroupId = x.ScoreRewardGroupId,
        LastModifyTime = x.LastModifyTime,
        GotScoreRewardIndex = x.GotScoreRewardIndex.ToList(),
        MaxRotateStageProgressIndex = x.MaxRotateStageProgressIndex,
        BestSpendTime = x.BestSpendTime.OrderBy(y => y.Key).Select(y => new TransfiniteBestSpendTime { StageGroupId = y.Key, BestSpendTime = y.Value }).ToList(),
        BattleInfo = x.BattleInfo is null ? [] : [ToWire(x.BattleInfo)],
        RotateSettleInfo = x.RotateSettleInfo is null ? null : new()
        {
            MaxStageProgressIndex = x.RotateSettleInfo.MaxStageProgressIndex,
            ScoreRewardGroupId = x.RotateSettleInfo.ScoreRewardGroupId,
            SettleTransfiniteScore = x.RotateSettleInfo.SettleTransfiniteScore,
            UnSettleTransfiniteScore = x.RotateSettleInfo.UnSettleTransfiniteScore,
            GotScoreRewardIndex = x.RotateSettleInfo.GotScoreRewardIndex.ToList()
        }
    };
    private static bool TryResolveRotateRewards(TransfiniteRotateSettleState p, TransfiniteScoreRewardGroupTable g, out List<RewardGoodsTable> rows)
    {
        rows = []; if (p.MaxStageProgressIndex < 0 || p.SettleTransfiniteScore < 0 || p.UnSettleTransfiniteScore < 0 || p.GotScoreRewardIndex.Distinct().Count() != p.GotScoreRewardIndex.Count) return false;
        foreach (int i in p.GotScoreRewardIndex) { int index = i - 1; if (index < 0 || index >= g.Score.Count || index >= g.RewardId.Count || p.SettleTransfiniteScore < g.Score[index] || g.RewardId[index] <= 0) return false; List<RewardGoodsTable> r = RewardHandler.GetRewardGoods(g.RewardId[index]); if (r.Count == 0) return false; rows.AddRange(r); }
        return true;
    }
    private static bool CanApplyItems(IReadOnlyList<RewardGoodsTable> rows, Inventory inventory)
    {
        try { foreach (var g in rows.GroupBy(x => x.TemplateId)) { if (g.Any(x => x.Count <= 0 || RewardHandler.GetRewardType(x) != RewardType.Item) || !Items.Value.TryGetValue(g.Key, out ItemTable? item)) return false; List<Item> have = inventory.Items.Where(x => x.Id == g.Key).ToList(); if (have.Count > 1) return false; long now = have.SingleOrDefault()?.Count ?? 0, add = g.Sum(x => (long)x.Count), max = Inventory.GetMaxCount(item); if (now < 0 || add > max - now) return false; } return true; } catch (OverflowException) { return false; }
    }
    private static bool ReceiptMatches(TransfiniteInventoryReceipt r, TransfiniteRotateSettleState p, IReadOnlyList<RewardGoodsTable> rows) => r.RegionId == p.RegionId && r.ScoreRewardGroupId == p.ScoreRewardGroupId && r.MaxStageProgressIndex == p.MaxStageProgressIndex && r.SettleTransfiniteScore == p.SettleTransfiniteScore && r.UnSettleTransfiniteScore == p.UnSettleTransfiniteScore && r.RewardGoods.Count == rows.Count && r.RewardGoods.Zip(rows).All(x => x.First.Id == x.Second.Id && x.First.TemplateId == x.Second.TemplateId && x.First.Count == x.Second.Count && x.First.RewardType == (int)RewardType.Item);
    private static bool CommitRotateReceipt(Session s, TransfiniteState state, TransfiniteRotateSettleReceipt receipt) { var pending = state.RotateSettleInfo; var old = state.LastRotateReceipt; long time = state.LastModifyTime; state.LastRotateReceipt = receipt; state.RotateSettleInfo = null; state.LastModifyTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(); try { s.player.Save(); return true; } catch { state.RotateSettleInfo = pending; state.LastRotateReceipt = old; state.LastModifyTime = time; return false; } }
    private static TransfiniteRotateSettleReceipt ToPlayerReceipt(TransfiniteInventoryReceipt x) => new() { RotationId = x.RotationId, MaxStageProgressIndex = x.MaxStageProgressIndex, SettleTransfiniteScore = x.SettleTransfiniteScore, UnSettleTransfiniteScore = x.UnSettleTransfiniteScore, RewardGoods = x.RewardGoods.ToList() };
    private static TransfiniteGetRotateSettleInfoResponse ToResponse(TransfiniteRotateSettleReceipt x) => new() { MaxStageProgressIndex = x.MaxStageProgressIndex, SettleTransfiniteScore = x.SettleTransfiniteScore, UnSettleTransfiniteScore = x.UnSettleTransfiniteScore, RewardGoodsList = x.RewardGoods.Select(ToWireReward).ToList() };
    private static TransfiniteGetRotateSettleInfoResponse ToResponse(TransfiniteInventoryReceipt x) => ToResponse(ToPlayerReceipt(x));
    private static void SendReceiptPush(Session s, TransfiniteInventoryReceipt r) { HashSet<int> ids = r.RewardGoods.Select(x => x.TemplateId).ToHashSet(); List<Item> items = s.inventory.Items.Where(x => ids.Contains(x.Id)).ToList(); if (items.Count > 0) s.SendPush(new NotifyItemDataList { ItemDataList = items }); }
    private static TransfiniteRewardReceipt ToReceipt(RewardGoods x) => new() { RewardType = x.RewardType, TemplateId = x.TemplateId, Count = x.Count, Level = x.Level, Quality = x.Quality, Grade = x.Grade, Breakthrough = x.Breakthrough, ConvertFrom = x.ConvertFrom, ShowQuality = x.ShowQuality, Id = x.Id, IsGift = x.IsGift, RewardMulti = x.RewardMulti };
    private static RewardGoods ToWireReward(TransfiniteRewardReceipt x) => new() { RewardType = x.RewardType, TemplateId = x.TemplateId, Count = x.Count, Level = x.Level, Quality = x.Quality, Grade = x.Grade, Breakthrough = x.Breakthrough, ConvertFrom = x.ConvertFrom, ShowQuality = x.ShowQuality, Id = x.Id, IsGift = x.IsGift, RewardMulti = x.RewardMulti };
}
