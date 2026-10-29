using AscNet.Table.V2.share.condition;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.fuben.stronghold;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.reward;
using AscNet.Table.V2.share.robot;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace AscNet.GameServer.Handlers;

internal static class StrongholdModule
{
    private const int Invalid = 20113018;
    private static List<T> Rows<T>() where T : class, AscNet.Common.Util.ITable => TableReaderV2.Parse<T>();
    private static StrongholdState State(Session s) => s.player.Stronghold;
    private static void Save(Session s) => s.player.Save();
    private static bool Own(Session s, int id) => id > 0 && s.character.Characters.Any(c => c.Id == (uint)id);

    // The client always sends three slots per team (GenerateOriginalTeamInfos): empty slots carry Id 0 and RobotId 0,
    // trial robots carry their RobotId and its CharacterId. Trial robots are the mode-wide StrongholdRobotGroup list.
    private static bool ValidMembers(Session s, List<StrongholdCharacterInfo> members, bool requireMember)
    {
        if (members.Count > 3) return false;
        List<StrongholdCharacterInfo> filled = members.Where(member => member.Id > 0 || member.RobotId > 0).ToList();
        if (requireMember && filled.Count == 0) return false;
        HashSet<int> robots = Rows<StrongholdRobotGroupTable>().SelectMany(row => row.RobotId).Where(id => id > 0).ToHashSet();
        foreach (StrongholdCharacterInfo member in filled)
        {
            if (member.PlayerId != 0 && member.PlayerId != s.player.PlayerData.Id) return false;
            bool valid = member.RobotId > 0
                ? robots.Contains(member.RobotId)
                    && Rows<RobotTable>().FirstOrDefault(row => row.Id == member.RobotId)?.CharacterId == member.Id
                : Own(s, member.Id);
            if (!valid) return false;
        }
        return filled.Select(member => member.Id).Distinct().Count() == filled.Count;
    }
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


    internal static NotifyStrongholdLoginData BuildLoginData(Player p) => BuildLoginData(p, DateTimeOffset.UtcNow);

    internal static NotifyStrongholdLoginData BuildLoginData(Player p, DateTimeOffset now)
    {
        StrongholdState state = p.Stronghold;
        Normalize(state);
        StrongholdActivityTable? activity = Activity(state.ActivityId);
        bool visible = state.BeginTime <= now.ToUnixTimeSeconds()
            && activity is not null && InWindow(activity, now);
        return new()
        {
            Id = state.ActivityId,
            BeginTime = visible ? state.BeginTime : 0,
            FightBeginTime = visible ? state.FightBeginTime : 0,
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
        state.CurrentResultRecord ??= state.LastResultRecord.Id == 0 || state.LastResultRecord.Id == state.ActivityId
            ? BsonSerializer.Deserialize<StrongholdResultRecord>(state.LastResultRecord.ToBson())
            : new();
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
                    ? state.HistoryFinishGroupInfos.Any(info => info.Id == condition.Params[0]
                        && (condition.Params[2] != 0 ? info.UsedSystemElectricEnergy : info.UsedElectricEnergy) <= condition.Params[1])
                    : state.FinishGroupIds.Contains(condition.Params[0])
                        && (condition.Params[2] != 0
                            ? state.FinishGroupInfos.FirstOrDefault(info => info.Id == condition.Params[0])?.UsedSystemElectricEnergy ?? -1
                            : state.FinishGroupInfos.FirstOrDefault(info => info.Id == condition.Params[0])?.UsedElectricEnergy ?? -1) <= condition.Params[1]),
            _ => false
        };
    }




    internal static void PrepareLogin(Player p) => PrepareLogin(p, DateTimeOffset.UtcNow);

    private static StrongholdActivityTable? Activity(int id) =>
        Rows<StrongholdActivityTable>().FirstOrDefault(row => row.Id == id)
        ?? Rows<StrongholdActivityTable>().FirstOrDefault(row => row.Id == 1);

    private static bool InWindow(StrongholdActivityTable activity, DateTimeOffset now) =>
        activity.OpenTimeId is not > 0
        || !ActivityScheduleService.TryGet(activity.OpenTimeId.Value, out ActivityScheduleEntry schedule)
        || schedule.IsOpen(now);

    internal static void PrepareLogin(Player p, DateTimeOffset now)
    {
        StrongholdState state = p.Stronghold;
        StrongholdActivityTable? activity = Activity(state.ActivityId);
        StrongholdLevelTable? level = Rows<StrongholdLevelTable>()
            .Where(row => p.PlayerData.Level >= row.MinLevel && p.PlayerData.Level <= row.MaxLevel)
            .OrderBy(row => row.Id).FirstOrDefault();
        long timestamp = now.ToUnixTimeSeconds();
        if (state.ActivityId > 0 && state.BeginTime > 0 && activity is not null
            && activity.OneCycleSeconds > 0 && timestamp >= (long)state.BeginTime + activity.OneCycleSeconds)
        {
            StrongholdActivityTable? nextActivity = Activity(1);
            if (nextActivity is null || nextActivity.OneCycleSeconds <= 0 || level is null
                || !InWindow(activity, now) || !InWindow(nextActivity, now))
                return;
            long nextBegin = (long)state.BeginTime + activity.OneCycleSeconds;
            if (nextActivity.OpenTimeId is > 0
                && ActivityScheduleService.TryGet(nextActivity.OpenTimeId.Value, out ActivityScheduleEntry schedule))
                nextBegin = Math.Max(nextBegin, schedule.StartTime);
            long skippedCycles = (timestamp - nextBegin) / nextActivity.OneCycleSeconds;
            nextBegin = checked(nextBegin + skippedCycles * nextActivity.OneCycleSeconds);
            int nextId = checked((int)(Math.Max((long)state.ActivityId + 1,
                (long)Rows<StrongholdActivityTable>().Max(row => row.Id) + 1) + skippedCycles));

            // Stage settlement, mail, and the new cycle together; a failed save leaves the session unchanged.
            Player staged = BsonSerializer.Deserialize<Player>(p.ToBson());
            Normalize(staged.Stronghold);
            SettleExpiredCycle(staged, timestamp);
            StartCycle(staged.Stronghold, nextId, nextBegin, level, nextActivity);
            staged.Stronghold.RewardIds.Sort();
            staged.Stronghold.ClaimedRewardIds.Sort();
            EnsureGroups(staged, level);
            staged.SaveChecked();
            p.Stronghold = staged.Stronghold;
            p.Mails = staged.Mails;
            p.MailExpireIds = staged.MailExpireIds;
            return;
        }

        Normalize(state);
        if (state.ActivityId > 0 && Rows<StrongholdLevelTable>().FirstOrDefault(row => row.Id == state.LevelId) is { } selectedLevel)
        {
            if (EnsureGroups(p, selectedLevel)) p.SaveChecked();
            return;
        }
        activity = state.ActivityId > 0 ? activity
            : Rows<StrongholdActivityTable>().OrderByDescending(row => row.Id).FirstOrDefault();
        if (activity is null || level is null || !InWindow(activity, now))
            return;
        state.ActivityId = state.ActivityId > 0 ? state.ActivityId : activity.Id;
        state.BeginTime = state.BeginTime > 0 ? state.BeginTime : checked((uint)timestamp);
        state.FightBeginTime = state.FightBeginTime > 0 ? state.FightBeginTime : checked((int)timestamp);
        state.LevelId = level.Id;
        state.ElectricEnergy = level.InitElectricEnergy;
        state.Endurance = level.InitEndurance;
        state.CurDay = 1;
        state.SettledPeriod = GuildModule.DailyPeriod(DateTimeOffset.FromUnixTimeSeconds(state.FightBeginTime));
        EnsureGroups(p, level);
        p.SaveChecked();
    }

    // Achievement claims and settlement mails are per cycle: the same reward ID is earned again every cycle.
    private static string RewardKey(Player player, int rewardId) =>
        $"stronghold:{player.PlayerData.Id}:{player.Stronghold.ActivityId}:{player.Stronghold.BeginTime}:achievement:{rewardId}";

    internal static void Reconcile(Session s) => Reconcile(s, DateTimeOffset.UtcNow, push: true);

    /// <summary>
    /// Settles every missed daily boundary (authored DailyResetTimestamp, GuildModule.DailyPeriod) of the active cycle once,
    /// in one state mutation. Day c -> c+1 adds StrongholdEndurance/StrongholdElectric row c (XStrongholdManager.GetMaxEndurance,
    /// XStrongholdConfigs.GetElectricAdd); a day in StayDays holds the boundary and consumes the pause; each settled day mines
    /// ceil(miners * MinerEfficiency) and miners then grow by MinerGrowRate percent (XStrongholdManager.GetPredictMinerCount).
    /// </summary>
    internal static bool Reconcile(Session s, DateTimeOffset now, bool push)
    {
        Player p = s.player;
        Normalize(p.Stronghold);
        StrongholdActivityTable? activity = Activity(p.Stronghold.ActivityId);
        if (p.Stronghold.ActivityId <= 0 || p.Stronghold.BeginTime == 0 || p.Stronghold.LevelId <= 0
            || activity is null || activity.OneCycleSeconds <= 0)
            return false;
        long begin = p.Stronghold.BeginTime;
        long clock = Math.Min(now.ToUnixTimeSeconds(), begin + activity.OneCycleSeconds);
        if (clock < begin) return false;
        long period = GuildModule.DailyPeriod(DateTimeOffset.FromUnixTimeSeconds(clock));
        int totalDay = (int)Math.Ceiling(activity.FightContinueSeconds / 86400d);

        StrongholdState x = BsonSerializer.Deserialize<StrongholdState>(p.Stronghold.ToBson());
        Normalize(x);
        bool changed = false;
        if (x.SettledPeriod == 0)
        {
            // One-time cutover for documents saved before daily settlement: an untouched state counts from its fight
            // start; one the old stay handler already moved (CurDay/StayDays) or that holds records is anchored to now
            // so its hand-advanced days are never settled a second time.
            bool pristine = x.CurDay == 0 && x.StayDays.Count == 0 && x.MineRecords.Count == 0;
            x.SettledPeriod = pristine
                ? GuildModule.DailyPeriod(DateTimeOffset.FromUnixTimeSeconds(x.FightBeginTime > 0 ? x.FightBeginTime : begin))
                : period;
            changed = !pristine; // a pristine anchor is derived from FightBeginTime, so it need not be written until a day settles
        }
        x.CurDay = Math.Max(1, x.CurDay);

        StrongholdEnduranceTable[] endurance = Rows<StrongholdEnduranceTable>().ToArray();
        StrongholdElectricTable[] electric = Rows<StrongholdElectricTable>().ToArray();
        int levelIndex = x.LevelId - 1;
        int Add(IEnumerable<int> perLevel) => perLevel.ElementAtOrDefault(levelIndex);
        int minerItem = Config("MinerId");
        long miners = minerItem > 0 ? s.inventory.SpendableCount(minerItem) : 0;
        long startMiners = miners;
        int efficiency = Config("MinerEfficiency"), growRate = Config("MinerGrowRate");
        int lastMinedDay = 0;
        bool anyBoundary = false;
        for (; x.SettledPeriod < period; x.SettledPeriod++)
        {
            changed = anyBoundary = true;
            x.BorrowCount = 0;
            int day = x.CurDay;
            if (day > totalDay) continue;
            if (x.StayDays.Remove(day)) continue;
            if (x.MineRecords.Any(record => record.Day == day)) continue;
            int mined = checked((int)(miners * efficiency));
            x.MineRecords.Add(new() { Day = day, MinerCount = checked((int)miners), MineralCount = mined });
            x.MineralLeft = checked(x.MineralLeft + mined);
            x.TotalMineral = checked(x.TotalMineral + mined);
            miners = (miners * (100L + growRate) + 99) / 100;
            lastMinedDay = day;
            if (day < totalDay)
            {
                x.CurDay = day + 1;
                x.Endurance += Add(endurance.FirstOrDefault(row => row.Id == day)?.Endurance ?? []);
                x.ElectricEnergy += Add(electric.FirstOrDefault(row => row.Id == day)?.ElectricEnerge ?? []);
            }
        }
        if (!changed) return false;

        RewardApplicationResult? grown = null;
        if (miners > startMiners)
            grown = RewardHandler.ApplyRewardsOnceAndPersist(
                [new RewardGrant($"stronghold:{p.PlayerData.Id}:{x.ActivityId}:{x.BeginTime}:miners:{lastMinedDay}",
                    [new RewardGoodsTable { Id = minerItem, TemplateId = minerItem, Count = checked((int)(miners - startMiners)), Params = [] }])], s);
        p.Stronghold = x;
        p.SaveChecked();
        grown?.SendPushes(s);
        if (push && anyBoundary)
            s.SendPush(new NotifyStrongholdChangeDay
            {
                CurDay = x.CurDay,
                FightBeginTime = x.FightBeginTime,
                MineralLeft = x.MineralLeft,
                Endurance = x.Endurance,
                ElectricEnergy = x.ElectricEnergy,
                BorrowCount = x.BorrowCount,
                StayDays = x.StayDays.ToList(),
                MineRecords = x.MineRecords.ToList()
            });
        return true;
    }

    private static void SettleExpiredCycle(Player player, long now)
    {
        StrongholdState state = player.Stronghold;
        foreach (StrongholdRewardTable reward in Rows<StrongholdRewardTable>()
            .Where(reward => !state.RewardIds.Contains(reward.Id) && IsRewardEligible(state, reward)))
        {
            List<PlayerMailRewardGoods> goods = RewardHandler.GetRewardGoods(reward.RewardId)
                .Select(row => new PlayerMailRewardGoods
                {
                    Id = row.Id,
                    RewardType = (int)(RewardHandler.GetRewardType(row)
                        ?? throw new InvalidDataException($"Unknown Stronghold settlement reward {row.Id}.")),
                    TemplateId = checked((uint)row.TemplateId),
                    Count = row.Count
                }).ToList();
            if (goods.Count == 0)
                throw new InvalidDataException($"Empty Stronghold settlement reward {reward.RewardId}.");
            AddSettlementMail(player, Config("UnGetRewardMail"), reward.Id, now, goods,
                RewardKey(player, reward.Id),
                $"Unclaimed reward: {reward.Desc}");
            state.RewardIds.Add(reward.Id);
            state.ClaimedRewardIds.Add(reward.Id);
        }
        foreach (StrongholdFinishGroupInfo info in state.FinishGroupInfos)
            if (!state.HistoryFinishGroupInfos.Any(history => history.Id == info.Id))
                state.HistoryFinishGroupInfos.Add(info);
        StrongholdResultRecord result = state.CurrentResultRecord!;
        result.Id = state.ActivityId;
        result.MineralCount = state.TotalMineral;
        if (state.MineRecords.Count > 0)
            result.MinerCount = state.MineRecords[^1].MinerCount;
        state.LastResultRecord = result;
        AddSettlementMail(player, Config("ResultMail"), 0, now, null, null,
            $"Cycle {result.Id} complete. Stages cleared: {result.FinishCount}. Ore earned: {result.MineralCount}.");
    }

    private static void AddSettlementMail(Player player, int templateId, int rewardId, long now,
        List<PlayerMailRewardGoods>? goods, string? claimKey, string content)
    {
        if (templateId <= 0)
            throw new InvalidDataException("Missing Stronghold settlement mail configuration.");
        string id = $"stronghold:{player.PlayerData.Id}:{player.Stronghold.ActivityId}:{player.Stronghold.BeginTime}:{templateId}:{rewardId}";
        if (player.Mails.Any(mail => mail.Id == id) || player.MailExpireIds.Contains(id))
            return;
        player.Mails.Add(new PlayerMail
        {
            Id = id,
            GroupId = templateId,
            SendName = "Norman Revival Plan",
            Title = goods is null ? "Norman Revival Plan results" : "Norman Revival Plan unclaimed rewards",
            Content = content,
            CreateTime = now,
            SendTime = now,
            RewardGoodsList = goods,
            RewardClaimKey = claimKey
        });
    }

    private static void StartCycle(StrongholdState state, int id, long begin,
        StrongholdLevelTable level, StrongholdActivityTable activity)
    {
        state.ActivityId = id;
        state.BeginTime = checked((uint)begin);
        state.FightBeginTime = checked((int)begin);
        state.CurDay = 1;
        state.SettledPeriod = GuildModule.DailyPeriod(DateTimeOffset.FromUnixTimeSeconds(begin));
        state.RewardIds.Clear();
        state.ClaimedRewardIds.Clear();
        state.LevelId = level.Id;
        state.AssistCharacterId = 0;
        state.SetAssistCharacterTime = 0;
        state.BorrowCount = 0;
        state.ElectricEnergy = level.InitElectricEnergy;
        state.Endurance = level.InitEndurance;
        state.MineralLeft = 0;
        state.TotalMineral = 0;
        state.ElectricCharacterIds.Clear();
        state.FinishGroupIds.Clear();
        state.FinishGroupInfos.Clear();
        state.GroupInfos.Clear();
        state.GroupStageDatas.Clear();
        if (activity.IsClearTeam != 0)
            state.TeamInfos.Clear();
        state.FightTeamInfos.Clear();
        state.RuneList.Clear();
        state.StayDays.Clear();
        state.MineRecords.Clear();
        state.CurrentResultRecord = new();
        state.PendingGroupId = 0;
        state.PendingStageId = 0;
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
            if (state.CurrentResultRecord is null) Normalize(state);
            state.CurrentResultRecord!.FinishCount++;
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
                    [new RewardGrant($"stronghold:{p.PlayerData.Id}:{state.ActivityId}:{groupId}", rewardRows,
                        EventCause: WheelchairManualGuideManager.GetUniqueWeeklyEventCause(44))], session);
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
        // XStrongholdManager.GetGroupCostEndurance: a chapter's last group already charges the endurance of every
        // unfinished FinishRelatedId group, so winning it finishes them too. XUiStrongholdFightSettleWin shows one
        // reward line per finished group and treats the last line as the fought group. Sweep finishes them itself.
        if (groupFinished && consumeEndurance)
            result.GroupFightResultInfos.InsertRange(0, CompleteRelatedGroups(p, session, groupId));
        p.Save();
        return result;
    }

    private static List<StrongholdFightResultInfo> CompleteRelatedGroups(Player p, Session session, int groupId)
    {
        StrongholdState state = p.Stronghold;
        var groups = Rows<StrongholdGroupTable>().ToDictionary(row => row.Id);
        List<StrongholdFightResultInfo> lines = [];
        HashSet<int> visited = [groupId];
        void Visit(int id)
        {
            if (!groups.TryGetValue(id, out var row)) return;
            foreach (int relatedId in row.FinishRelatedId.Where(value => value > 0))
            {
                if (!visited.Add(relatedId)) continue;
                Visit(relatedId);
                if (state.FinishGroupIds.Contains(relatedId)) continue;
                StrongholdFightResult last = new();
                while (Next(state, relatedId) is int stageId and > 0)
                {
                    state.PendingGroupId = relatedId;
                    state.PendingStageId = stageId;
                    last = Settle(p, true, session, consumeEndurance: false);
                }
                lines.AddRange(last.GroupFightResultInfos);
            }
        }
        Visit(groupId);
        state.PendingGroupId = groupId;
        state.PendingStageId = 0;
        return lines;
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
        StrongholdActivityTable? activity = Activity(state.ActivityId);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (activity is null || state.BeginTime == 0 || now.ToUnixTimeSeconds() < state.BeginTime
            || now.ToUnixTimeSeconds() >= (long)state.BeginTime + activity.OneCycleSeconds
            || !InWindow(activity, now))
        {
            code = 20113001;
            return true;
        }
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
        Reconcile(s);
        StrongholdState state = State(s);
        int mineral = state.MineralLeft;
        int itemId = Config("MineralId");
        if (mineral <= 0 || itemId <= 0)
        {
            s.SendResponse(new GetStrongholdMineralResponse { Code = Invalid }, p.Id);
            return;
        }
        // TotalMineral only grows when MineralLeft does, so it names this claim; a retried claim re-finds the same key.
        RewardApplicationResult grant = RewardHandler.ApplyRewardsOnceAndPersist(
            [new RewardGrant($"stronghold:{s.player.PlayerData.Id}:{state.ActivityId}:{state.BeginTime}:mineral:{state.TotalMineral}",
                [new RewardGoodsTable { Id = itemId, TemplateId = itemId, Count = mineral, Params = [] }])], s);
        state.MineralLeft = 0;
        Save(s);
        grant.SendPushes(s);
        s.SendResponse(new GetStrongholdMineralResponse { Code = 0, MineralCount = mineral }, p.Id);
    }

    [RequestPacketHandler("SetStrongholdElectricTeamRequest")]
    public static void SetElectric(Session s, Packet.Request p)
    {
        Reconcile(s);
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
        Reconcile(s);
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
        Reconcile(s);
        ResetStrongholdStageRequest request = p.Deserialize<ResetStrongholdStageRequest>();
        StrongholdGroupInfo? group = State(s).GroupInfos.FirstOrDefault(value => value.Id == request.GroupId);
        int code = group is not null && group.FinishStageIds.Remove(request.StageId) ? 0 : 20113021;
        if (code == 0) Save(s);
        s.SendResponse(new ResetStrongholdStageResponse { Code = code }, p.Id);
    }
    [RequestPacketHandler("SetStrongholdFightTeamRequest")]
    public static void SetFightTeam(Session s, Packet.Request p)
    {
        Reconcile(s);
        SetStrongholdFightTeamRequest request = p.Deserialize<SetStrongholdFightTeamRequest>();
        StrongholdState state = State(s);
        bool valid = request.Id > 0
            && Next(state, request.Id) > 0
            && request.TeamInfos.Count > 0
            && request.TeamInfos.Count <= Config("MaxPreTeamCount")
            && request.TeamInfos.Select(team => team.Id).Distinct().Count() == request.TeamInfos.Count
            && request.TeamInfos.All(team => ValidMembers(s, team.CharacterInfos, requireMember: true));
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
        Reconcile(s);
        SetStrongholdTeamRequest request = p.Deserialize<SetStrongholdTeamRequest>();
        bool valid = request.TeamInfos.Count > 0
            && request.TeamInfos.Count <= Config("MaxPreTeamCount")
            && request.TeamInfos.Select(team => team.Id).Distinct().Count() == request.TeamInfos.Count
            && request.TeamInfos.All(team => ValidMembers(s, team.CharacterInfos, requireMember: false));
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
    public static void Assist(Session s,Packet.Request p){Reconcile(s);var r=p.Deserialize<SetStrongholdAssistCharacterRequest>();bool ok=r.CharacterId==0||Own(s,r.CharacterId);if(ok){var x=State(s);x.AssistCharacterId=r.CharacterId;x.SetAssistCharacterTime=(int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();Save(s);}s.SendResponse(new SetStrongholdAssistCharacterResponse{Code=ok?0:20113003},p.Id);}
    [RequestPacketHandler("GetStrongholdLendDetailRequest")]
    public static void Lend(Session s,Packet.Request p)=>s.SendResponse(new GetStrongholdLendDetailResponse{Code=0,LendDayInfos=[]},p.Id);
    [RequestPacketHandler("GetStrongholdRewardRequest")]
    public static void Reward(Session s, Packet.Request p)
    {
        Reconcile(s);
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
            rows.Select(row => new RewardGrant(RewardKey(s.player, row.Id),
                RewardHandler.GetRewardGoods(row.RewardId),
                EventCause: WheelchairManualGuideManager.GetUniqueWeeklyEventCause(44))).ToList(), s);
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
        Reconcile(s);
        SweepStrongholdStageRequest request = p.Deserialize<SweepStrongholdStageRequest>();
        StrongholdState state = State(s);
        var groups = Rows<StrongholdGroupTable>().ToDictionary(row => row.Id);
        StrongholdLevelTable? level = Rows<StrongholdLevelTable>().FirstOrDefault(row => row.Id == state.LevelId);
        StrongholdActivityTable? activity = Activity(state.ActivityId);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        int code = activity is null || state.BeginTime == 0 || now < state.BeginTime
            || now >= (long)state.BeginTime + activity.OneCycleSeconds
            || !InWindow(activity, DateTimeOffset.FromUnixTimeSeconds(now)) ? 20113001 : 0;
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
        Reconcile(s);
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
        WheelchairManualGuideManager.SendUpdate(s);
        s.SendResponse(new SelectStrongholdLevelResponse
        {
            ElectricEnergy = state.ElectricEnergy,
            Endurance = state.Endurance,
            GroupStageDatas = state.GroupStageDatas
        }, p.Id);
    }
    // ponytail: server-only distribution rules are unavailable; use the typed eligible stage groups, deterministic per player, and persist the result.
    [RequestPacketHandler("SetStrongholdStayRequest")]
    public static void Stay(Session s, Packet.Request p)
    {
        Reconcile(s);
        StrongholdState x = State(s);
        // The client only sends this while a day is live; recording the reconciled day twice is a no-op.
        if (x.CurDay > 0 && !x.StayDays.Contains(x.CurDay))
        {
            x.StayDays.Add(x.CurDay);
            Save(s);
        }
        s.SendResponse(new SetStrongholdStayResponse { Code = 0, StayDays = x.StayDays }, p.Id);
    }
}
