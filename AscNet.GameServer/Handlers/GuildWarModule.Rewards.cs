using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.guildwar;
using AscNet.Table.V2.share.task;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Popup = AscNet.Common.MsgPack.NotifyGuildWarActivityData.NotifyGuildWarActivityDataPopupRecord;
using PopupSettle = AscNet.Common.MsgPack.NotifyGuildWarActivityData.NotifyGuildWarActivityDataPopupRecord.NotifyGuildWarActivityDataPopupRecordSettleData;
using ActivitySettle = AscNet.Common.MsgPack.NotifyGuildWarActivityData.NotifyGuildWarActivityDataActivityData.NotifyGuildWarActivityDataActivityDataSettleData;
using SyncTask = AscNet.Common.MsgPack.NotifyTask.NotifyTaskTasks.NotifyTaskTasksTask;

namespace AscNet.GameServer.Handlers;

internal static partial class GuildWarModule
{
    private static readonly Lazy<Dictionary<int, TaskTable>> RewardTasks = new(() =>
    {
        HashSet<int> ids = TableReaderV2.Parse<GuildWarTaskTable>().Select(row => row.TaskId).ToHashSet();
        return TableReaderV2.Parse<TaskTable>().Where(task => ids.Contains(task.Id)).ToDictionary(task => task.Id);
    });
    private static readonly Lazy<Dictionary<int, ConditionTable>> RewardTaskConditions = new(() =>
    {
        HashSet<int> ids = RewardTasks.Value.Values.Select(task => task.Condition).ToHashSet();
        return TableReaderV2.Parse<ConditionTable>().Where(condition => ids.Contains(condition.Id)).ToDictionary(condition => condition.Id);
    });

    internal static void PrepareRewardLogin(Session session)
    {
        lock (Session.GetPlayerOperationLock(session.player.PlayerData.Id))
        {
            List<int> merged = session.player.GuildWar.PlayedActionIds.Concat(session.player.GuildState.War.PlayedActionIds)
                .Where(id => id > 0).Distinct().ToList();
            if (merged.SequenceEqual(session.player.GuildState.War.PlayedActionIds)) return;
            GuildWarParticipation previous = session.player.GuildState.War;
            GuildWarParticipation staged = BsonSerializer.Deserialize<GuildWarParticipation>(previous.ToBson());
            staged.PlayedActionIds = merged;
            session.player.GuildState.War = staged;
            try { session.player.SaveChecked(); }
            catch { session.player.GuildState.War = previous; throw; }
        }
    }

    [RequestPacketHandler("GuildWarPopupActionRequest")]
    public static void PopupAction(Session session, Packet.Request packet)
    {
        GuildWarPopupActionResponse response = new();
        lock (Session.GetPlayerOperationLock(session.player.PlayerData.Id))
        {
            GuildWarParticipation previous = session.player.GuildState.War;
            List<int> legacy = session.player.GuildWar.PlayedActionIds;
            try
            {
                GuildWarPopupActionRequest request = packet.Deserialize<GuildWarPopupActionRequest>();
                List<int> merged = legacy.Concat(previous.PlayedActionIds).Concat(request.ActionPlayed ?? [])
                    .Where(id => id > 0).Distinct().ToList();
                if (!merged.SequenceEqual(previous.PlayedActionIds))
                {
                    GuildWarParticipation staged = BsonSerializer.Deserialize<GuildWarParticipation>(previous.ToBson());
                    staged.PlayedActionIds = merged;
                    session.player.GuildState.War = staged;
                    session.player.SaveChecked();
                }
            }
            catch
            {
                session.player.GuildState.War = previous;
                response.Code = 20164012;
            }
        }
        session.SendResponse(response, packet.Id);
    }

    [RequestPacketHandler("GuildWarPopupRequest")]
    public static void PopupRequest(Session session, Packet.Request packet)
    {
        GuildWarPopupResponse response = new();
        try { PrepareLogin(session); response.PopupRecord = PopupData(session.player.GuildState.War); }
        catch (ServerCodeException exception) { response.Code = exception.Code; }
        catch { response.Code = 20164012; }
        session.SendResponse(response, packet.Id);
    }

    [RequestPacketHandler("XGuildWarGetBossRewardRequest")]
    public static void GetBossReward(Session session, Packet.Request packet) =>
        GuildModule.Handle<XGuildWarGetBossRewardRequest, XGuildWarGetBossRewardResponse>(session, packet, (mutation, request, response) =>
        {
            GuildWarRoundState round = CurrentRound(mutation);
            GuildWarPlayerRound player = PlayerRound(mutation, session.player.PlayerData.Id);
            Require(player.GuildId == mutation.Guild.Id && player.DifficultyId == round.DifficultyId, 20164072);
            GuildWarNodeState? node = round.Nodes.FirstOrDefault(node => node.Uid == request.NodeUid);
            Require(node is not null, 20164014);
            GuildWarNodeTable? nodeConfig = TableReaderV2.Parse<GuildWarNodeTable>().FirstOrDefault(row => row.Id == node!.NodeId);
            Require(nodeConfig is not null && nodeConfig.Type == ActivityRule.FightNodeType, 20164061);
            GuildWarBossRewardTable? reward = TableReaderV2.Parse<GuildWarBossRewardTable>().FirstOrDefault(row => row.Id == request.Id);
            Require(reward is not null && reward.Difficulty == round.DifficultyId, 20164059);
            Require(!player.BossRewardIds.Contains(request.Id), 20164058);
            Require(round.BossDead >= reward!.LimitLevel, 20164062);
            player.BossRewardIds.Add(request.Id);
            response.RewardGoodsList = mutation.AddReward(session.player.PlayerData.Id, reward.RewardId);
            Publish(mutation);
        });

    internal static void RecordRewardFight(GuildMutation mutation, GuildWarRoundState round, GuildWarPlayerRound player,
        GuildWarNodeState? node, GuildWarMonsterState? monster, long damage, long point, bool killed, int activation)
    {
        long uid = mutation.ActorSession.player.PlayerData.Id;
        if (!round.RewardParticipants.Contains(uid)) round.RewardParticipants.Add(uid);
        player.FightCount = checked(player.FightCount + 1);
        if (player.FirstPointAt == 0) player.FirstPointAt = Now;
        if (round.FirstPointAt == 0) round.FirstPointAt = Now;
        int target = node?.Uid ?? monster!.Uid;
        GuildWarRankContribution? contribution = player.Contributions.FirstOrDefault(entry => entry.Uid == target && entry.Monster == (monster is not null));
        if (contribution is null)
        {
            contribution = new() { Uid = target, Monster = monster is not null, FirstPointAt = Now };
            player.Contributions.Add(contribution);
        }
        contribution.Damage = checked(contribution.Damage + damage);
        contribution.Point = checked(contribution.Point + point);
        contribution.Activation = checked(contribution.Activation + activation);
        if (node is not null && killed)
        {
            GuildWarNodeTable config = TableReaderV2.Parse<GuildWarNodeTable>().First(row => row.Id == node.NodeId);
            round.ClearedNodeTypes.Add(config.Type);
        }
    }

    internal static void SettleRound(GuildMutation mutation, GuildWarRoundState round)
    {
        if (round.Settled) return;
        GuildWarNodeState? home = round.Nodes.FirstOrDefault(node => TableReaderV2.Parse<GuildWarNodeTable>().Any(row => row.Id == node.NodeId && row.Type == 1));
        List<long> participants = round.RewardParticipants.Distinct().ToList();
        GuildWarSettlement summary = new()
        {
            Season = mutation.Guild.War.Season, RoundId = round.RoundId, DifficultyId = round.DifficultyId,
            IsPass = round.BossDead > 0 ? 1 : 0,
            PassUseSecond = Math.Max(0, (round.FirstPassAt > 0 ? round.FirstPassAt : Math.Min(Now, round.EndsAt)) - round.StartedAt),
            TotalActivation = round.TotalActivation, BasePoint = round.TotalPoint,
            BaseHpPercent = home is null || home.HpMax == 0 ? 0 : checked((int)(home.CurHp * 100 / home.HpMax)),
            CurMember = participants.Select(uid => checked((uint)uid)).ToList()
        };
        round.Settlement = summary;
        foreach (long uid in participants)
        {
            GuildWarParticipation participation = mutation.Player(uid).War;
            GuildWarPlayerRound? own = participation.Rounds.FirstOrDefault(entry => entry.RoundId == round.RoundId && entry.GuildId == mutation.Guild.Id);
            if (own is null || participation.Season != mutation.Guild.War.Season) continue;
            if (participation.Settlements.Any(entry => entry.Season == summary.Season && entry.RoundId == round.RoundId)) continue;
            GuildWarSettlement personal = BsonSerializer.Deserialize<GuildWarSettlement>(summary.ToBson());
            personal.PlayerActivation = own.Activation;
            personal.PlayerPoints = own.Point;
            participation.Settlements.Add(personal);
            own.RoundReward = 1;
            mutation.Push(uid, new NotifyGuildWarRoundSettle());
        }
        // Current EN difficulty rows author no PassRewardId/UnPassRewardId. Tasks and boss rows own their rewards.
        round.Settled = true;
    }

    internal static void SettleSeason(GuildMutation mutation)
    {
        if (mutation.Guild.War.SeasonSettled) return;
        foreach (GuildWarRoundState round in mutation.Guild.War.Rounds) SettleRound(mutation, round);
        GuildWarSeasonStanding? standing = SeasonStanding(mutation.Guild, mutation.Guild.War.Season);
        if (standing is not null && !mutation.Guild.War.RankSeasons.Any(entry => entry.Season == standing.Season))
            mutation.Guild.War.RankSeasons.Add(standing);
        List<GuildWarRankInfo> ranks = GuildRanks(mutation.Guild);
        GuildWarRankInfo? rank = ranks.FirstOrDefault(entry => entry.Uid == mutation.Guild.Id);
        int percent = rank is null ? 0 : Math.Max(1, checked((int)Math.Ceiling(rank.Rank * 100.0 / ranks.Count)));
        mutation.Guild.War.FinalRankPercent = percent;
        mutation.Guild.War.SeasonSettled = true;
        foreach (long uid in mutation.Guild.War.Rounds.SelectMany(round => round.RewardParticipants).Distinct())
        {
            GuildWarParticipation participation = mutation.Player(uid).War;
            if (participation.Season != mutation.Guild.War.Season) continue;
            participation.FinalRankPercent = percent;
            foreach (GuildWarSettlement summary in participation.Settlements.Where(entry => entry.Season == participation.Season)) summary.FinalRankPercent = percent;
        }
    }

    internal static void PopulateRewards(Session session, NotifyGuildWarActivityData dto)
    {
        dto.ActionPlayed = session.player.GuildWar.PlayedActionIds.Concat(session.player.GuildState.War.PlayedActionIds)
            .Where(id => id > 0).Distinct().Select(id => (dynamic)id).ToList();
        dto.PopupRecord = PopupData(session.player.GuildState.War);
    }

    internal static void PopulateRewards(NotifyGuildWarActivityData dto, Guild guild, GuildWarParticipation player)
    {
        dto.ActionPlayed = player.PlayedActionIds.Select(id => (dynamic)id).ToList();
        dto.PopupRecord = PopupData(player);
        PopulateRewardActivity(dto.ActivityData, guild);
        foreach (var own in dto.MyRoundData)
            own.GetBossRewards = player.Rounds.FirstOrDefault(round => round.RoundId == own.RoundId && round.GuildId == own.GuildId)?.BossRewardIds.ToList() ?? [];
    }

    internal static void PopulateRewardActivity(NotifyGuildWarActivityData.NotifyGuildWarActivityDataActivityData dto, Guild guild)
    {
        dto.SettleDatas = guild.War.Rounds.Where(round => round.Settlement is not null).Select(round =>
        {
            GuildWarSettlement value = round.Settlement!;
            return new ActivitySettle { RoundId = value.RoundId, DifficultyId = value.DifficultyId, IsPass = value.IsPass,
                PassUseSecond = checked((uint)value.PassUseSecond), TotalActivation = value.TotalActivation,
                BaseHpPercent = value.BaseHpPercent, BasePoint = checked((uint)value.BasePoint),
                FinalRankPercent = guild.War.FinalRankPercent, CurMember = value.CurMember.ToList() };
        }).ToList();
    }

    private static Popup PopupData(GuildWarParticipation player) => new()
    {
        SettleDatas = player.Settlements.Where(entry => entry.Season == player.Season).Select(value => new PopupSettle
        {
            RoundId = value.RoundId, DifficultyId = value.DifficultyId, IsPass = value.IsPass,
            PassUseSecond = checked((uint)value.PassUseSecond), TotalActivation = value.TotalActivation,
            PlayerActivation = value.PlayerActivation, PlayerPoints = checked((int)value.PlayerPoints),
            BaseHpPercent = value.BaseHpPercent, BasePoint = checked((uint)value.BasePoint),
            FinalRankPercent = value.FinalRankPercent, CurMember = value.CurMember.ToList()
        }).ToList()
    };

    internal static bool TryGetTaskProgress(Session session, int taskId, out int progress, out bool claimed)
    {
        progress = 0; claimed = false;
        if (!RewardTasks.Value.ContainsKey(taskId)) return false;
        Guild? guild = GuildModule.FindMembership(session.player.PlayerData.Id);
        GuildWarParticipation participation = session.player.GuildState.War;
        claimed = participation.TaskReceipts.Any(receipt => receipt.Season == participation.Season && receipt.TaskId == taskId);
        if (guild is not null && participation.Season == guild.War.Season)
            progress = TaskProgress(guild, participation, taskId, session.player.PlayerData.Id);
        return true;
    }

    private static int TaskProgress(Guild guild, GuildWarParticipation participation, int taskId, long uid)
    {
        TaskTable task = RewardTasks.Value[taskId];
        ConditionTable condition = RewardTaskConditions.Value[task.Condition];
        List<int> p = condition.Params;
        GuildWarPlayerRound? playerRound = p.Count >= 2 ? participation.Rounds.FirstOrDefault(round => round.GuildId == guild.Id && round.RoundId == p[0] && round.DifficultyId == p[1]) : null;
        GuildWarRoundState? round = playerRound is null ? null : guild.War.Rounds.FirstOrDefault(round => round.RoundId == playerRound.RoundId);
        long value = condition.Type switch
        {
            81000 => playerRound?.FightCount ?? 0,
            81001 => playerRound?.Point ?? 0,
            81002 => round?.ClearedNodeTypes.Count(type => type == p[3]) ?? 0,
            81013 => round is not null && round.BossDead >= p[2] ? 1 : 0,
            81014 => participation.Rounds.Where(own => own.GuildId == guild.Id).Sum(own => own.StationCount),
            81004 => participation.Rounds.Any(own => own.GuildId == guild.Id && own.DifficultyId >= p[0]
                && guild.War.Rounds.Any(round => round.RoundId == own.RoundId && round.BossDead > 0)) ? 1 : 0,
            81005 => participation.FinalRankPercent > 0 && participation.FinalRankPercent <= p[0] ? 1 : 0,
            _ => throw new InvalidOperationException($"Unsupported authored Guild War task condition {condition.Type}.")
        };
        return checked((int)Math.Min(value, task.Result ?? 1));
    }

    internal static List<RewardGoods> ClaimTask(GuildMutation mutation, int taskId)
    {
        Require(RewardTasks.Value.ContainsKey(taskId), 20026005);
        Refresh(mutation);
        long uid = mutation.ActorSession.player.PlayerData.Id;
        GuildWarParticipation player = mutation.Player(uid).War;
        Require(!player.TaskReceipts.Any(receipt => receipt.Season == player.Season && receipt.TaskId == taskId), 20026006);
        TaskTable task = RewardTasks.Value[taskId];
        Require(TaskProgress(mutation.Guild, player, taskId, uid) >= (task.Result ?? 1), 20026007);
        player.TaskReceipts.Add(new() { Season = player.Season, TaskId = taskId });
        mutation.MarkTaskClaimed(uid, taskId);
        return task.RewardId is > 0 ? mutation.AddReward(uid, task.RewardId.Value) : [];
    }

    internal static List<SyncTask> BuildTaskData(Session session) =>
        BuildTaskData(GuildModule.FindMembership(session.player.PlayerData.Id), session.player.GuildState.War, session.player.PlayerData.Id);

    internal static List<SyncTask> BuildTaskData(GuildMutation mutation) =>
        BuildTaskData(mutation.Guild, mutation.Player(mutation.ActorSession.player.PlayerData.Id).War, mutation.ActorSession.player.PlayerData.Id);

    private static List<SyncTask> BuildTaskData(Guild? guild, GuildWarParticipation participation, long uid)
    {
        return RewardTasks.Value.Values.Select(task =>
        {
            int progress = guild is not null && guild.War.Season == participation.Season
                ? TaskProgress(guild, participation, task.Id, uid) : 0;
            bool claimed = participation.TaskReceipts.Any(receipt => receipt.Season == participation.Season && receipt.TaskId == task.Id);
            return new SyncTask { Id = checked((uint)task.Id), State = claimed ? 4 : progress >= (task.Result ?? 1) ? 3 : 1,
                RecordTime = checked((uint)Now), Schedule = [new() { Id = checked((uint)task.Condition), Value = progress }] };
        }).ToList();
    }

    private static List<GuildWarRankInfo> GuildRanks(Guild current)
    {
        List<Guild> guilds = Guild.AllActive();
        guilds.RemoveAll(guild => guild.Id == current.Id);
        guilds.Add(current);
        List<GuildWarRankInfo> ranks = guilds.Select(guild => (Guild: guild, Standing: SeasonStanding(guild, current.War.Season)))
            .Where(entry => entry.Standing is not null)
            .Select(entry => new GuildWarRankInfo { Uid = entry.Guild.Id, Name = entry.Guild.Name, IconId = entry.Guild.IconId,
                Point = entry.Standing!.Point, Activation = entry.Standing.Activation, FirstPointAt = entry.Standing.FirstPointAt }).ToList();
        OrderRanks(ranks);
        return ranks;
    }

    private static GuildWarSeasonStanding? SeasonStanding(Guild guild, int season)
    {
        GuildWarSeasonStanding? archived = guild.War.RankSeasons.FirstOrDefault(entry => entry.Season == season);
        if (archived is not null) return archived;
        if (guild.War.Season != season || !guild.War.Rounds.Any(round => round.RewardParticipants.Count > 0)) return null;
        return new() { Season = season, Point = guild.War.Rounds.Sum(round => round.TotalPoint),
            Activation = guild.War.Rounds.Sum(round => round.TotalActivation),
            FirstPointAt = guild.War.Rounds.Where(round => round.FirstPointAt > 0).Select(round => round.FirstPointAt).DefaultIfEmpty(long.MaxValue).Min() };
    }

    private static void OrderRanks(List<GuildWarRankInfo> ranks)
    {
        // Local standings contain only real participants; equal totals retain first scoring time, then UID.
        ranks.Sort((left, right) =>
        {
            int result = right.Point.CompareTo(left.Point);
            if (result == 0) result = right.Activation.CompareTo(left.Activation);
            if (result == 0) result = left.FirstPointAt.CompareTo(right.FirstPointAt);
            return result == 0 ? left.Uid.CompareTo(right.Uid) : result;
        });
        for (int i = 0; i < ranks.Count; i++) { ranks[i].Rank = i + 1; ranks[i].MemberCount = ranks.Count; }
    }

    [RequestPacketHandler("GuildWarOpenRankRequest")]
    public static void OpenRank(Session session, Packet.Request packet) =>
        GuildModule.Handle<GuildWarOpenRankRequest, GuildWarOpenRankResponse>(session, packet, (mutation, request, response) =>
        {
            Refresh(mutation);
            Require(request.RankType is >= 1 and <= 8, 5);
            if (request.RankType == 1)
            {
                Require(request.Uid == 0, 5);
                response.RankList = GuildRanks(mutation.Guild);
                response.MyRankInfo = response.RankList.FirstOrDefault(row => row.Uid == mutation.Guild.Id)
                    ?? new() { Uid = mutation.Guild.Id, Name = mutation.Guild.Name, IconId = mutation.Guild.IconId, MemberCount = response.RankList.Count };
                return;
            }
            GuildWarRoundState? round = request.RankType is 2 or 6
                ? mutation.Guild.War.Rounds.FirstOrDefault(round => round.RoundId == request.Uid)
                : mutation.Guild.War.Rounds.FirstOrDefault(round => round.RoundId == mutation.Guild.War.RoundId);
            Require(request.RankType == 2 && request.Uid == 0 || round is not null, 20164006);
            if (request.RankType is 3 or 5 or 7) Require(round!.Nodes.Any(node => node.Uid == request.Uid), 20164014);
            if (request.RankType == 4) Require(round!.Monsters.Any(monster => monster.Uid == request.Uid), 20164017);
            if (request.RankType == 8) Require(round!.Reinforcements.Any(reinforcement => reinforcement.Uid == request.Uid), 20164065);
            foreach (long uid in mutation.Guild.MemberIds)
            {
                Player? identity = uid == session.player.PlayerData.Id ? session.player : Server.Instance.SessionFromUID(uid)?.player ?? Player.TryFromPlayerId(uid);
                if (identity is null) continue;
                GuildWarParticipation participation = mutation.Player(uid).War;
                if (participation.Season != mutation.Guild.War.Season) continue;
                List<GuildWarPlayerRound> rounds = participation.Rounds.Where(own => own.GuildId == mutation.Guild.Id
                    && (request.RankType == 2 && request.Uid == 0 || own.RoundId == round!.RoundId)).ToList();
                if (rounds.Count == 0) continue;
                GuildWarPlayerRound own = rounds[^1];
                GuildWarRankInfo row = new() { Uid = uid, Name = identity.PlayerData.Name,
                    HeadPortraitId = identity.PlayerData.CurrHeadPortraitId, HeadFrameId = identity.PlayerData.CurrHeadFrameId,
                    Point = rounds.Sum(own => own.Point), Activation = rounds.Sum(own => own.Activation),
                    FirstPointAt = rounds.Where(own => own.FirstPointAt > 0).Select(own => own.FirstPointAt).DefaultIfEmpty(long.MaxValue).Min() };
                switch (request.RankType)
                {
                    case 3:
                    case 4:
                        GuildWarRankContribution? contribution = own.Contributions.FirstOrDefault(entry => entry.Uid == request.Uid && entry.Monster == (request.RankType == 4));
                        if (contribution is null) continue;
                        row.Point = contribution.Point; row.Activation = contribution.Activation; row.FirstPointAt = contribution.FirstPointAt;
                        break;
                    case 5:
                        if (own.CurNodeId != RootNode(round!.Nodes.First(node => node.Uid == request.Uid).NodeId)) continue;
                        break;
                    case 6:
                        if (own.LastHideAreaTeams.Count == 0) continue;
                        row.Point = own.HiddenPoint;
                        row.HideAreaMetas = own.LastHideAreaTeams.Select(team => new GuildWarHideAreaRankMeta { NodeId = team.NodeId, Point = team.LastPoint,
                            Characters = team.CharacterInfos.OrderBy(character => character.Pos).Select(character => new GuildWarHideAreaCharacterMeta
                            { PlayerId = character.Id, IsAssist = character.PlayerId != uid ? 1 : 0 }).ToList() }).ToList();
                        row.TeamLeaders = own.LastHideAreaTeams.Select(team => team.CharacterInfos.FirstOrDefault(character => character.Pos == team.CaptainPos)?.Id ?? 0).ToList();
                        break;
                    case 7:
                        int nodeId = round!.Nodes.First(node => node.Uid == request.Uid).NodeId;
                        if (round.DefenseDic.GetValueOrDefault(uid) != nodeId) continue;
                        break;
                    case 8:
                        if (!round!.Reinforcements.First(reinforcement => reinforcement.Uid == request.Uid).SupportedPlayerIds.Contains(uid)) continue;
                        break;
                }
                response.RankList.Add(row);
            }
            OrderRanks(response.RankList);
            response.MyRankInfo = response.RankList.FirstOrDefault(row => row.Uid == session.player.PlayerData.Id)
                ?? new() { Uid = session.player.PlayerData.Id, Name = session.player.PlayerData.Name,
                    HeadPortraitId = session.player.PlayerData.CurrHeadPortraitId, HeadFrameId = session.player.PlayerData.CurrHeadFrameId,
                    MemberCount = response.RankList.Count };
        });
}
