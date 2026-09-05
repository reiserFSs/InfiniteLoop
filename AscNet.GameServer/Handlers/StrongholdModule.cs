using AscNet.Table.V2.share.condition;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.fuben.stronghold;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.reward;

namespace AscNet.GameServer.Handlers;

internal static class StrongholdModule
{
    private const int Invalid = 20113018;
    private static List<T> Rows<T>() where T : class, AscNet.Common.Util.ITable => TableReaderV2.Parse<T>();
    private static StrongholdState State(Session s) => s.player.Stronghold;
    private static void Save(Session s) => s.player.Save();
    private static bool Own(Session s, int id) => id > 0 && s.character.Characters.Any(c => c.Id == (uint)id);
    private static int First(string? value) =>
        int.TryParse((value ?? string.Empty).Trim('[', ']').Split('|')[0], out int result) ? result : 0;
    private static int Config(string key) => Rows<StrongholdCfgTable>().FirstOrDefault(row => row.Key == key)?.Value ?? 0;
    private static int Pick(string candidates, long playerId, int groupId, int index)
    {
        int[] values = candidates.Split('|', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => int.TryParse(value, out int parsed) ? parsed : 0).Where(value => value > 0).ToArray();
        if (values.Length == 0) return 0;
        long seed = unchecked(playerId * 397L + groupId * 31L + index);
        return values[(int)((ulong)seed % (uint)values.Length)];
    }


    internal static NotifyStrongholdLoginData BuildLoginData(Player p)
    {
        StrongholdState state = p.Stronghold;
        Normalize(state);
        return new()
        {
            Id = state.ActivityId,
            BeginTime = state.BeginTime,
            FightBeginTime = state.FightBeginTime,
            CurDay = state.CurDay,
            AssistCharacterId = state.AssistCharacterId,
            SetAssistCharacterTime = state.SetAssistCharacterTime,
            BorrowCount = state.BorrowCount,
            ElectricEnergy = (uint)Math.Max(0, state.ElectricEnergy),
            Endurance = state.Endurance,
            MineralLeft = state.MineralLeft,
            TotalMineral = state.TotalMineral,
            ElectricCharacterIds = state.ElectricCharacterIds.ToList(),
            FinishGroupIds = state.FinishGroupIds.ToList(),
            FinishGroupInfos = state.FinishGroupInfos.ToList(),
            HistoryFinishGroupInfos = state.HistoryFinishGroupInfos.ToList(),
            GroupInfos = state.GroupInfos.ToList(),
            TeamInfos = state.TeamInfos.OrderBy(entry => entry.Key).Select(entry => entry.Value).ToList(),
            GroupStageDatas = state.GroupStageDatas.ToList(),
            RuneList = state.RuneList.ToList(),
            RewardIds = state.RewardIds.ToList(),
            LastResultRecord = state.LastResultRecord,
            MineRecords = state.MineRecords.ToList(),
            LevelId = state.LevelId,
            StayDays = state.StayDays.ToList()
        };
    }

    private static void Normalize(StrongholdState state)
    {
        state.ElectricCharacterIds ??= [];
        state.FinishGroupIds ??= [];
        state.FinishGroupInfos ??= [];
        state.HistoryFinishGroupInfos ??= [];
        state.GroupInfos ??= [];
        state.GroupStageDatas ??= [];
        state.TeamInfos ??= [];
        state.FightTeamInfos ??= [];
        state.RuneList ??= [];
        state.RewardIds ??= [];
        state.StayDays ??= [];
        state.MineRecords ??= [];
        state.ClaimedRewardIds ??= [];
        state.RewardIds.AddRange(state.ClaimedRewardIds);
        state.RewardIds = state.RewardIds.Distinct().OrderBy(id => id).ToList();
        state.ClaimedRewardIds.Clear();
        state.ClaimedRewardIds.AddRange(state.RewardIds);
        state.LastResultRecord ??= new();
        foreach (StrongholdGroupInfo group in state.GroupInfos)
            group.FinishStageIds ??= [];
        foreach (StrongholdGroupStageData group in state.GroupStageDatas)
        {
            group.StageIds ??= [];
            group.StageBuffId ??= [];
        }
        foreach (StrongholdTeamInfo team in state.TeamInfos.Values)
        {
            team.CharacterInfos ??= [];
            team.PluginInfos ??= [];
        }
        foreach (List<StrongholdTeamInfo> teams in state.FightTeamInfos.Values)
        foreach (StrongholdTeamInfo team in teams ?? [])
        {
            team.CharacterInfos ??= [];
            team.PluginInfos ??= [];
        }
    }
    private static bool IsRewardEligible(StrongholdState state, StrongholdRewardTable reward)
    {
        if (reward.LevelId != state.LevelId) return false;
        ConditionTable? condition = Rows<ConditionTable>().FirstOrDefault(row => row.Id == reward.Condition);
        if (condition is null) return false;
        return condition.Type switch
        {
            10130 => condition.Params.Count > 0
                && state.FinishGroupIds.Count(id => id > 0) >= condition.Params[0],
            10131 => condition.Params.Count > 0
                && state.FinishGroupIds.Contains(condition.Params[0]),
            10132 => condition.Params.Count > 0
                && state.TotalMineral >= condition.Params[0],
            12103 => condition.Params.Count >= 3
                && (condition.Params.Count > 3 && condition.Params[3] != 0
                    ? state.HistoryFinishGroupInfos.FirstOrDefault(info => info.Id == condition.Params[0]) is { } historyInfo
                        && (condition.Params[2] != 0 ? historyInfo.UsedSystemElectricEnergy : historyInfo.UsedElectricEnergy) <= condition.Params[1]
                    : state.FinishGroupIds.Contains(condition.Params[0])
                        && (condition.Params[2] != 0
                            ? state.FinishGroupInfos.FirstOrDefault(info => info.Id == condition.Params[0])?.UsedSystemElectricEnergy ?? -1
                            : state.FinishGroupInfos.FirstOrDefault(info => info.Id == condition.Params[0])?.UsedElectricEnergy ?? -1) <= condition.Params[1]),
            _ => false
        };
    }




    internal static void PrepareLogin(Player p)
    {
        StrongholdState state = p.Stronghold;
        Normalize(state);
        StrongholdActivityTable? activity = Rows<StrongholdActivityTable>().OrderByDescending(row => row.Id).FirstOrDefault();
        StrongholdLevelTable? level = Rows<StrongholdLevelTable>()
            .Where(row => p.PlayerData.Level >= row.MinLevel && p.PlayerData.Level <= row.MaxLevel)
            .OrderBy(row => row.Id).FirstOrDefault();
        if (state.ActivityId > 0 && Rows<StrongholdLevelTable>().FirstOrDefault(row => row.Id == state.LevelId) is { } selectedLevel)
        {
            if (EnsureGroups(p, selectedLevel)) p.Save();
            return;
        }
        if (activity is null || level is null)
            return;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        state.ActivityId = state.ActivityId > 0 ? state.ActivityId : activity.Id;
        state.BeginTime = state.BeginTime > 0 ? state.BeginTime : checked((uint)now);
        state.FightBeginTime = state.FightBeginTime > 0 ? state.FightBeginTime : checked((int)Math.Min(now, int.MaxValue));
        state.LevelId = level.Id;
        state.ElectricEnergy = level.InitElectricEnergy;
        state.Endurance = level.InitEndurance;
        state.CurDay = 0;
        EnsureGroups(p, level);
        p.Save();
    }

    private static bool EnsureGroups(Player p, StrongholdLevelTable level)
    {
        StrongholdState state = p.Stronghold;
        bool changed = false;
        var chapters = Rows<StrongholdChapterTable>().ToDictionary(row => row.Id);
        var groups = Rows<StrongholdGroupTable>().ToDictionary(row => row.Id);
        foreach (int chapterId in level.Chapter.Where(id => id > 0))
        {
            if (!chapters.TryGetValue(chapterId, out var chapter))
                continue;
            foreach (int groupId in chapter.GroupId.Where(id => id > 0))
            {
                if (!groups.TryGetValue(groupId, out var group))
                    continue;
                if (!state.GroupStageDatas.Any(data => data.Id == groupId))
                {
                    List<uint> stageIds = group.StageIdGroup
                        .Select((candidates, index) => Pick(candidates, p.PlayerData.Id, groupId, index))
                        .Where(id => id > 0).Distinct().Select(id => (uint)id).ToList();
                    if (stageIds.Count == 0) continue;
                    state.GroupStageDatas.Add(new() { Id = groupId, StageIds = stageIds, SupportId = First(group.SupportId) });
                    changed = true;
                }
                if (!state.GroupInfos.Any(info => info.Id == groupId) && !state.FinishGroupIds.Contains(groupId))
                {
                    state.GroupInfos.Add(new() { Id = groupId });
                    changed = true;
                }
            }
        }
        return changed;
    }

    internal static StrongholdFightResult Settle(Player p, bool win, Session session, bool consumeEndurance = true)
    {
        StrongholdState state = p.Stronghold;
        StrongholdFightResult result = new();
        if (state.PendingGroupId <= 0 || state.PendingStageId <= 0) return result;

        int groupId = state.PendingGroupId;
        int stageId = state.PendingStageId;
        StrongholdGroupInfo group = state.GroupInfos.FirstOrDefault(value => value.Id == groupId)
            ?? new StrongholdGroupInfo { Id = groupId };
        if (!state.GroupInfos.Contains(group)) state.GroupInfos.Add(group);
        int enduranceCost = consumeEndurance ? EnduranceCost(state, groupId) : 0;
        state.Endurance = Math.Max(0, state.Endurance - enduranceCost);
        state.PendingStageId = 0;
        if (!win)
        {
            p.Save();
            result.GroupFightResultInfos.Add(new() { GroupId = groupId });
            return result;
        }

        bool newlyCleared = !group.FinishStageIds.Contains(stageId);
        if (newlyCleared)
        {
            group.FinishStageIds.Add(stageId);
            state.LastResultRecord.FinishCount++;
        }
        int next = Next(state, groupId);
        bool groupFinished = next == 0;
        if (!groupFinished)
            state.PendingStageId = next;
        else
        {
            result.AllFinished = true;
            if (!state.FinishGroupIds.Contains(groupId))
            {
                state.FinishGroupIds.Add(groupId);
                state.FinishGroupInfos.Add(new() { Id = groupId });
                state.HistoryFinishGroupInfos.Add(new() { Id = groupId });
            }
        }

        List<RewardGoods> goods = [];
        if (groupFinished)
        {
            StrongholdGroupTable? row = Rows<StrongholdGroupTable>().FirstOrDefault(value => value.Id == groupId);
            int rewardId = state.LevelId > 0 && state.LevelId <= (row?.RewardId.Count ?? 0)
                ? row!.RewardId[state.LevelId - 1] is int id ? id : 0
                : 0;
            if (rewardId > 0)
            {
                List<AscNet.Table.V2.share.reward.RewardGoodsTable> rewardRows = RewardHandler.GetRewardGoods(rewardId);
                RewardApplicationResult grant = RewardHandler.ApplyRewardsOnceAndPersist(
                    [new RewardGrant($"stronghold:{p.PlayerData.Id}:{state.ActivityId}:{groupId}", rewardRows)], session);
                goods = grant.RewardGoods;
                grant.SendPushes(session);
            }
        }
        if (newlyCleared)
        {
            bool firstClear = session.stage is not null
                && !(session.stage.Stages.GetValueOrDefault((uint)stageId)?.Passed ?? false);
            if (session.stage is not null)
            {
                StageDatum stage = session.stage.Stages.GetValueOrDefault((uint)stageId) ?? new StageDatum
                {
                    StageId = (uint)stageId,
                    CreateTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };
                stage.Passed = true;
                stage.StarsMark |= 7;
                session.stage.AddStage(stage);
                session.stage.Save();
            }
            TaskModule.RecordStageClear(session, stageId, 1, 0, firstClear);
        }
        result.GroupFightResultInfos.Add(new() { GroupId = groupId, RewardGoodsList = goods });
        p.Save();
        return result;
    }

    private static int EnduranceCost(StrongholdState state, int groupId)
    {
        if (state.FinishGroupIds.Contains(groupId)
            || state.GroupInfos.Any(group => group.Id == groupId && group.FinishStageIds.Count > 0)) return 0;
        var groups = Rows<StrongholdGroupTable>().ToDictionary(group => group.Id);
        HashSet<int> related = [];
        void Visit(int id)
        {
            if (!related.Add(id) || !groups.TryGetValue(id, out var group)) return;
            foreach (int child in group.FinishRelatedId.Where(value => value > 0)) Visit(child);
        }
        Visit(groupId);
        return related.Where(id => !state.FinishGroupIds.Contains(id)).Sum(id => groups.GetValueOrDefault(id)?.Endurance ?? 0);
    }

    private static int Next(StrongholdState x,int id) { var stages=x.GroupStageDatas.FirstOrDefault(g=>g.Id==id)?.StageIds; var done=x.GroupInfos.FirstOrDefault(g=>g.Id==id)?.FinishStageIds??[]; return stages?.Select(v=>(int)v).FirstOrDefault(v=>!done.Contains(v))??0; }
    private static bool IsGroupUnlocked(StrongholdState state, int groupId)
    {
        int? predecessor = Rows<StrongholdGroupTable>().FirstOrDefault(row => row.Id == groupId)?.PreId;
        return predecessor is not > 0 || state.FinishGroupIds.Contains(predecessor.Value);
    }
    private static bool CanSweep(StrongholdState state, StrongholdGroupTable group)
    {
        int index = group.SweepLevelId.FindIndex(level => level == state.LevelId);
        if (index < 0 || index >= group.SweepCondition.Count) return false;
        int conditionId = group.SweepCondition[index];
        ConditionTable? condition = Rows<ConditionTable>().FirstOrDefault(row => row.Id == conditionId);
        if (condition?.Type != 12103 || condition.Params.Count < 4) return false;
        int referencedGroup = condition.Params[0];
        int threshold = condition.Params[1];
        bool system = condition.Params[2] != 0;
        bool history = condition.Params[3] != 0;
        StrongholdFinishGroupInfo? record = (history ? state.HistoryFinishGroupInfos : state.FinishGroupInfos)
            .LastOrDefault(info => info.Id == referencedGroup);
        if (record is null || (!history && !state.FinishGroupIds.Contains(referencedGroup))) return false;
        return (system ? record.UsedSystemElectricEnergy : record.UsedElectricEnergy) <= threshold;
    }

    internal static bool TryAuthorizePreFight(Player p, uint stageId, out int code)
    {
        code = 0;
        StrongholdState state = p.Stronghold;
        StrongholdGroupStageData? stageData = state.GroupStageDatas.FirstOrDefault(group => group.StageIds.Contains(stageId));
        if (stageData is null) return false;
        if (!IsGroupUnlocked(state, stageData.Id)
            || state.PendingGroupId != stageData.Id
            || state.PendingStageId != (int)stageId)
        {
            code = Invalid;
            return true;
        }
        if (state.Endurance < EnduranceCost(state, stageData.Id)) code = 20113020;
        return true;
    }


    [RequestPacketHandler("GetStrongholdMineralRequest")]
    public static void GetMineral(Session s, Packet.Request p)
    {
        StrongholdState state = State(s);
        int mineral = state.MineralLeft;
        state.MineralLeft = 0;
        Save(s);
        s.SendResponse(new GetStrongholdMineralResponse { Code = mineral > 0 ? 0 : Invalid, MineralCount = mineral }, p.Id);
    }

    [RequestPacketHandler("SetStrongholdElectricTeamRequest")]
    public static void SetElectric(Session s, Packet.Request p)
    {
        SetStrongholdElectricTeamRequest request = p.Deserialize<SetStrongholdElectricTeamRequest>();
        if (request.CharacterIds.Count > Config("MaxElectricTeamMemberCount")
            || request.CharacterIds.Distinct().Count() != request.CharacterIds.Count
            || request.CharacterIds.Any(id => !Own(s, id)))
        {
            s.SendResponse(new SetStrongholdElectricTeamResponse { Code = 20113014 }, p.Id);
            return;
        }
        State(s).ElectricCharacterIds = request.CharacterIds.ToList();
        Save(s);
        s.SendResponse(new SetStrongholdElectricTeamResponse(), p.Id);
    }

    [RequestPacketHandler("ResetStrongholdGroupRequest")]
    public static void ResetGroup(Session s, Packet.Request p)
    {
        ResetStrongholdGroupRequest request = p.Deserialize<ResetStrongholdGroupRequest>();
        StrongholdGroupInfo? group = State(s).GroupInfos.FirstOrDefault(value => value.Id == request.Id);
        if (group is null)
        {
            s.SendResponse(new ResetStrongholdGroupResponse { Code = Invalid }, p.Id);
            return;
        }
        group.FinishStageIds.Clear();
        Save(s);
        s.SendResponse(new ResetStrongholdGroupResponse(), p.Id);
    }

    [RequestPacketHandler("ResetStrongholdStageRequest")]
    public static void ResetStage(Session s, Packet.Request p)
    {
        ResetStrongholdStageRequest request = p.Deserialize<ResetStrongholdStageRequest>();
        StrongholdGroupInfo? group = State(s).GroupInfos.FirstOrDefault(value => value.Id == request.GroupId);
        int code = group is not null && group.FinishStageIds.Remove(request.StageId) ? 0 : 20113021;
        if (code == 0) Save(s);
        s.SendResponse(new ResetStrongholdStageResponse { Code = code }, p.Id);
    }
    [RequestPacketHandler("SetStrongholdFightTeamRequest")]
    public static void SetFightTeam(Session s, Packet.Request p)
    {
        SetStrongholdFightTeamRequest request = p.Deserialize<SetStrongholdFightTeamRequest>();
        StrongholdState state = State(s);
        bool valid = request.Id > 0
            && Next(state, request.Id) > 0
            && request.TeamInfos.Count > 0
            && request.TeamInfos.Count <= Config("MaxPreTeamCount")
            && request.TeamInfos.Select(team => team.Id).Distinct().Count() == request.TeamInfos.Count
            && request.TeamInfos.All(team => team.CharacterInfos.Count > 0
                && team.CharacterInfos.Count <= 3
                && team.CharacterInfos.Select(character => character.Id).Distinct().Count() == team.CharacterInfos.Count
                && team.CharacterInfos.All(character => Own(s, character.Id)));
        if (valid)
        {
            state.FightTeamInfos[request.Id] = request.TeamInfos;
            state.PendingGroupId = request.Id;
            state.PendingStageId = Next(state, request.Id);
            Save(s);
        }
        s.SendResponse(new SetStrongholdFightTeamResponse { Code = valid ? 0 : 20113030 }, p.Id);
    }
    [RequestPacketHandler("SetStrongholdTeamRequest")]
    public static void SetTeam(Session s, Packet.Request p)
    {
        SetStrongholdTeamRequest request = p.Deserialize<SetStrongholdTeamRequest>();
        bool valid = request.TeamInfos.Count > 0
            && request.TeamInfos.Count <= Config("MaxPreTeamCount")
            && request.TeamInfos.Select(team => team.Id).Distinct().Count() == request.TeamInfos.Count
            && request.TeamInfos.All(team => team.CharacterInfos.Count <= 3
                && team.CharacterInfos.Select(character => character.Id).Distinct().Count() == team.CharacterInfos.Count
                && team.CharacterInfos.All(character => Own(s, character.Id)));
        if (valid)
        {
            State(s).TeamInfos = request.TeamInfos.ToDictionary(team => team.Id);
            Save(s);
        }
        s.SendResponse(new SetStrongholdTeamResponse { Code = valid ? 0 : 20113023 }, p.Id);
    }
    [RequestPacketHandler("GetStrongholdAssistCharacterListRequest")]
    public static void AssistList(Session s,Packet.Request p){s.SendResponse(new GetStrongholdAssistCharacterListResponse{Code=0,CharacterDetails=[]},p.Id);}
    [RequestPacketHandler("SetStrongholdAssistCharacterRequest")]
    public static void Assist(Session s,Packet.Request p){var r=p.Deserialize<SetStrongholdAssistCharacterRequest>();bool ok=r.CharacterId==0||Own(s,r.CharacterId);if(ok){var x=State(s);x.AssistCharacterId=r.CharacterId;x.SetAssistCharacterTime=(int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();Save(s);}s.SendResponse(new SetStrongholdAssistCharacterResponse{Code=ok?0:20113003},p.Id);}
    [RequestPacketHandler("GetStrongholdLendDetailRequest")]
    public static void Lend(Session s,Packet.Request p)=>s.SendResponse(new GetStrongholdLendDetailResponse{Code=0,LendDayInfos=[]},p.Id);
    [RequestPacketHandler("GetStrongholdRewardRequest")]
    public static void Reward(Session s, Packet.Request p)
    {
        GetStrongholdRewardRequest request = p.Deserialize<GetStrongholdRewardRequest>();
        StrongholdState state = State(s);
        List<int> ids = request.Ids.Distinct().ToList();
        List<StrongholdRewardTable> rows = Rows<StrongholdRewardTable>()
            .Where(row => ids.Contains(row.Id)
                && !state.RewardIds.Contains(row.Id)
                && !state.ClaimedRewardIds.Contains(row.Id)
                && IsRewardEligible(state, row))
            .ToList();
        if (ids.Count == 0 || rows.Count != ids.Count)
        {
            s.SendResponse(new GetStrongholdRewardResponse { Code = Invalid }, p.Id);
            return;
        }
        List<RewardGoodsTable> goods = rows.SelectMany(row => RewardHandler.GetRewardGoods(row.RewardId)).ToList();
        if (goods.Count == 0)
        {
            s.SendResponse(new GetStrongholdRewardResponse { Code = Invalid }, p.Id);
            return;
        }
        RewardApplicationResult grant = RewardHandler.ApplyRewardsOnceAndPersist(
            rows.Select(row => new RewardGrant($"stronghold:{s.player.PlayerData.Id}:achievement:{row.Id}", RewardHandler.GetRewardGoods(row.RewardId))).ToList(), s);
        state.RewardIds.AddRange(ids);
        state.RewardIds = state.RewardIds.Distinct().OrderBy(id => id).ToList();
        state.ClaimedRewardIds.AddRange(ids);
        state.ClaimedRewardIds = state.ClaimedRewardIds.Distinct().OrderBy(id => id).ToList();
        Save(s);
        grant.SendPushes(s);
        s.SendResponse(new GetStrongholdRewardResponse
        {
            Code = 0,
            SuccessIds = ids,
            RewardGoodsList = grant.RewardGoods
        }, p.Id);
    }
    [RequestPacketHandler("SweepStrongholdStageRequest")]
    public static void Sweep(Session s, Packet.Request p)
    {
        SweepStrongholdStageRequest request = p.Deserialize<SweepStrongholdStageRequest>();
        StrongholdState state = State(s);
        var groups = Rows<StrongholdGroupTable>().ToDictionary(row => row.Id);
        StrongholdLevelTable? level = Rows<StrongholdLevelTable>().FirstOrDefault(row => row.Id == state.LevelId);
        StrongholdActivityTable? activity = Rows<StrongholdActivityTable>().FirstOrDefault(row => row.Id == state.ActivityId);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        int code = activity is null || state.BeginTime == 0 || now < state.BeginTime
            || now >= (long)state.BeginTime + activity.OneCycleSeconds ? 20113001 : 0;
        if (code == 0 && (!groups.ContainsKey(request.GroupId) || level is null
            || !Rows<StrongholdChapterTable>().Any(chapter => level.Chapter.Contains(chapter.Id)
                && chapter.GroupId.Contains(request.GroupId)))) code = Invalid;
        if (code == 0 && state.FinishGroupIds.Contains(request.GroupId)) code = 20113070;
        if (code == 0 && !IsGroupUnlocked(state, request.GroupId)) code = 20113019;
        if (code == 0 && !CanSweep(state, groups[request.GroupId])) code = 20113071;

        List<int> sweepGroups = [];
        HashSet<int> visited = [];
        bool AddGroup(int groupId)
        {
            if (!visited.Add(groupId) || state.FinishGroupIds.Contains(groupId)) return true;
            if (!groups.TryGetValue(groupId, out var row)) return false;
            foreach (int relatedId in row.FinishRelatedId.Where(id => id > 0))
                if (!AddGroup(relatedId)) return false;
            StrongholdGroupStageData? stages = state.GroupStageDatas.FirstOrDefault(data => data.Id == groupId);
            if (stages is null || stages.StageIds.Count == 0 || Next(state, groupId) == 0
                || stages.StageIds.Any(id => !row.StageIdGroup.Any(candidates =>
                    candidates.Split('|').Contains(id.ToString(), StringComparer.Ordinal)))) return false;
            sweepGroups.Add(groupId);
            return true;
        }
        if (code == 0 && !AddGroup(request.GroupId)) code = Invalid;
        int enduranceCost = code == 0 ? EnduranceCost(state, request.GroupId) : 0;
        if (code == 0 && state.Endurance < enduranceCost) code = 20113020;
        if (code != 0)
        {
            s.SendResponse(new SweepStrongholdStageResponse { Code = code }, p.Id);
            return;
        }
        state.Endurance -= enduranceCost;
        StrongholdFightResult result = new() { AllFinished = true };
        foreach (int groupId in sweepGroups)
        {
            StrongholdFightResult groupResult;
            do
            {
                state.PendingGroupId = groupId;
                state.PendingStageId = Next(state, groupId);
                groupResult = Settle(s.player, true, s, consumeEndurance: false);
                StrongholdGroupInfo? progress = state.GroupInfos.FirstOrDefault(value => value.Id == groupId);
                if (progress is not null)
                    s.SendPush(new NotifyUpdateStrongholdGroupData { GroupInfo = progress });
                s.SendPush(new NotifyStrongholdTotalMineral { TotalMineral = state.TotalMineral });
            }
            while (Next(state, groupId) > 0);
            result.GroupFightResultInfos.AddRange(groupResult.GroupFightResultInfos);
            s.SendPush(new NotifyStrongholdFinishGroupId
            {
                FinishGroupIds = state.FinishGroupIds.ToList(),
                ElectricEnergy = state.ElectricEnergy,
                FinishGroupInfos = state.FinishGroupInfos.ToList(),
                HistoryFinishGroupInfos = state.HistoryFinishGroupInfos.ToList()
            });
            s.SendPush(new NotifyDeleteStrongholdGroupData { Id = groupId });
        }
        state.PendingGroupId = 0;
        state.PendingStageId = 0;
        Save(s);
        s.SendPush(new NotifyStrongholdEnduranceData { Endurance = state.Endurance });
        s.SendResponse(new SweepStrongholdStageResponse { Code = 0, StrongholdFightResult = result }, p.Id);
    }
    [RequestPacketHandler("SelectStrongholdLevelRequest")]
    public static void SelectLevel(Session s, Packet.Request p)
    {
        SelectStrongholdLevelRequest request = p.Deserialize<SelectStrongholdLevelRequest>();
        StrongholdState state = State(s);
        StrongholdLevelTable? level = Rows<StrongholdLevelTable>().FirstOrDefault(row => row.Id == request.LevelId
            && s.player.PlayerData.Level >= row.MinLevel && s.player.PlayerData.Level <= row.MaxLevel);
        if (state.LevelId != 0 || level is null)
        {
            s.SendResponse(new SelectStrongholdLevelResponse { Code = 20113056 }, p.Id);
            return;
        }
        state.LevelId = level.Id;
        state.Endurance = level.InitEndurance;
        state.ElectricEnergy = level.InitElectricEnergy;
        state.GroupInfos.Clear();
        state.GroupStageDatas.Clear();
        EnsureGroups(s.player, level);
        Save(s);
        s.SendResponse(new SelectStrongholdLevelResponse
        {
            ElectricEnergy = state.ElectricEnergy,
            Endurance = state.Endurance,
            GroupStageDatas = state.GroupStageDatas
        }, p.Id);
    }
    // ponytail: server-only distribution rules are unavailable; use the typed eligible stage groups, deterministic per player, and persist the result.
    [RequestPacketHandler("SetStrongholdStayRequest")]
    public static void Stay(Session s,Packet.Request p){var x=State(s);int d=++x.CurDay;if(!x.StayDays.Contains(d))x.StayDays.Add(d);Save(s);s.SendResponse(new SetStrongholdStayResponse{Code=0,StayDays=x.StayDays},p.Id);}
}
