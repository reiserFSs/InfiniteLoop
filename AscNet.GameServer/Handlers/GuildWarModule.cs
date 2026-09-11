using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.guildwar;
using System.Globalization;
using Activity = AscNet.Common.MsgPack.NotifyGuildWarActivityData.NotifyGuildWarActivityDataActivityData;
using Round = AscNet.Common.MsgPack.NotifyGuildWarActivityData.NotifyGuildWarActivityDataActivityData.NotifyGuildWarActivityDataActivityDataRoundData;
using Node = AscNet.Common.MsgPack.NotifyGuildWarActivityData.NotifyGuildWarActivityDataActivityData.NotifyGuildWarActivityDataActivityDataRoundData.NotifyGuildWarActivityDataActivityDataRoundDataNodeData;
using Monster = AscNet.Common.MsgPack.NotifyGuildWarActivityData.NotifyGuildWarActivityDataActivityData.NotifyGuildWarActivityDataActivityDataRoundData.NotifyGuildWarActivityDataActivityDataRoundDataMonsterData;

namespace AscNet.GameServer.Handlers;

internal static partial class GuildWarModule
{
    internal static Func<DateTimeOffset> Clock = () => DateTimeOffset.UtcNow;
    internal static long Now => Clock().ToUnixTimeSeconds();
    private const long Week = 7 * 86400;
    private static readonly Lazy<Dictionary<string, string>> Settings = new(() => TableReaderV2.Parse<GuildWarConfigTable>().ToDictionary(x => x.Key, x => x.Value));
    private static readonly Lazy<Dictionary<int, GuildWarNodeTable>> NodeRules = new(() => TableReaderV2.Parse<GuildWarNodeTable>().ToDictionary(x => x.Id));
    internal static int Setting(string key) => int.Parse(Settings.Value[key], CultureInfo.InvariantCulture);
    internal static void Require(bool valid, int code) { if (!valid) throw new ServerCodeException("Guild Expedition request rejected", code); }
    private static GuildWarActivityTable ActivityRule => TableReaderV2.Parse<GuildWarActivityTable>().Where(x => x.TimeId > 0).MaxBy(x => x.Id)!;
    private sealed class ExtraEnergyRule
    {
        public ExtraEnergyRule() { }
        public List<int> DifficultyIds { get; set; } = [];
        public int GiveDays { get; set; }
        public int GameThrough { get; set; }
        public List<ExtraEnergyNode> NodeConditions { get; set; } = [];
    }
    private sealed class ExtraEnergyNode
    {
        public ExtraEnergyNode() { }
        public int Priority { get; set; }
        public int NodeType { get; set; }
        public int EnergyAdd { get; set; }
    }
    private static readonly Lazy<ExtraEnergyRule> ExtraEnergy = new(() => System.Text.Json.JsonSerializer.Deserialize<ExtraEnergyRule>(Settings.Value["ExtraAddEnergyRule"]) ?? throw new InvalidDataException("Missing Guild War catch-up energy rules."));

    // Explicit local calendar: the client ships templates, not deployment dates. Three authored
    // seven-day rounds and a seven-day result period repeat from the reset-aligned Unix epoch.
    internal static (int Season, int RoundId, long Start, long End) Calendar(DateTimeOffset now)
    {
        long epoch = GuildModule.NextWeeklyReset(DateTimeOffset.UnixEpoch).ToUnixTimeSeconds();
        long index = (long)Math.Floor((now.ToUnixTimeSeconds() - epoch) / (double)(4 * Week));
        long start = epoch + index * 4 * Week;
        int offset = (int)((now.ToUnixTimeSeconds() - start) / Week);
        var rounds = TableReaderV2.Parse<GuildWarRoundTable>().Where(x => x.ActivityId == ActivityRule.Id).OrderBy(x => x.Id).ToArray();
        return (checked((int)index + 1), offset < rounds.Length ? rounds[offset].Id : 0, start, start + 4 * Week);
    }

    internal static IEnumerable<TimeLimitCtrlConfigList> BuildTimeLimits(DateTimeOffset now)
    {
        var calendar = Calendar(now);
        yield return new() { Id = ActivityRule.TimeId ?? 0, StartTime = calendar.Start, EndTime = calendar.End };
        int offset = 0;
        foreach (var round in TableReaderV2.Parse<GuildWarRoundTable>().Where(x => x.ActivityId == ActivityRule.Id).OrderBy(x => x.Id))
        {
            yield return new() { Id = round.TimeId, StartTime = calendar.Start + offset * Week, EndTime = calendar.Start + ++offset * Week };
        }
    }

    internal static void PrepareLogin(Session session)
    {
        PrepareRewardLogin(session);
        Guild? guild = GuildModule.FindMembership(session.player.PlayerData.Id);
        if (guild is null) return;
        GuildModule.Commit(session, guild, Refresh);
    }

    internal static void Refresh(GuildMutation mutation)
    {
        var calendar = Calendar(Clock());
        GuildWarGuildState state = mutation.Guild.War;
        long uid = mutation.ActorSession.player.PlayerData.Id;
        if (state.Season != calendar.Season)
        {
            foreach (var old in state.Rounds.Where(x => !x.Settled)) SettleRound(mutation, old);
            if (state.Season != 0) SettleSeason(mutation);
            state.Season = calendar.Season;
            state.Rounds.Clear();
            state.RoundId = 0;
            state.SeasonSettled = false;
            state.FinalRankPercent = 0;
        }
        foreach (var old in state.Rounds.Where(x => !x.Settled && x.EndsAt <= Now))
        {
            if (mutation.Guild.CreatedAt <= old.StartedAt && old.DifficultyId > 0)
                AdvanceDynamics(mutation, old, old.EndsAt);
            SettleRound(mutation, old);
        }
        state.RoundId = calendar.RoundId;
        if (calendar.RoundId != 0 && state.Rounds.All(x => x.RoundId != calendar.RoundId))
        {
            int offset = Array.FindIndex(TableReaderV2.Parse<GuildWarRoundTable>().Where(x => x.ActivityId == ActivityRule.Id).OrderBy(x => x.Id).ToArray(), x => x.Id == calendar.RoundId);
            var round = new GuildWarRoundState { RoundId = calendar.RoundId, StartedAt = calendar.Start + offset * Week, EndsAt = calendar.Start + (offset + 1) * Week };
            state.Rounds.Add(round);
        }
        GuildWarParticipation player = mutation.Player(uid).War;
        if (player.Season != state.Season || player.GuildId != mutation.Guild.Id)
        {
            player.Season = state.Season;
            player.GuildId = mutation.Guild.Id;
            player.Rounds.Clear();
            player.FinalRankPercent = 0;
            ClearSupport(player);
            ClearCombat(player);
        }
        if (calendar.RoundId != 0)
        {
            var round = state.Rounds.Single(x => x.RoundId == calendar.RoundId);
            if (mutation.Guild.CreatedAt <= round.StartedAt && !round.Settled && round.DifficultyId == 0
                && IsPristine(round) && mutation.Guild.MemberIds.All(member =>
                    mutation.Player(member).War.Season != state.Season
                    || mutation.Player(member).War.Rounds.Where(x => x.GuildId == mutation.Guild.Id && x.RoundId == round.RoundId).All(IsPristine)))
                InitializeMap(round, state.NextDifficultyId > 0 ? state.NextDifficultyId : PreselectedDifficulty(state, round.RoundId));
            if (mutation.Guild.CreatedAt <= round.StartedAt && round.DifficultyId > 0)
            {
                PlayerRound(mutation, uid);
                AdvanceDynamics(mutation, round, Now);
                RefreshEnergy(mutation, player, round);
            }
        }
        else if (!state.SeasonSettled) SettleSeason(mutation);
        UpdateNodeMembers(mutation.Guild);
    }

    private static int PreselectedDifficulty(GuildWarGuildState state, int roundId)
    {
        // XGuildWarManager.GetDifficultyPreSelected / XGWBattleManager.CheckAllInfectIsDead.
        foreach (var prior in state.Rounds.Where(x => x.RoundId < roundId && x.DifficultyId > 0).OrderByDescending(x => x.RoundId))
            if (NodeRules.Value.Values.Where(x => x.DifficultyId == prior.DifficultyId && x.Type is 6 or 7 or 9 or 14 or 19)
                .All(config => prior.Nodes.Any(node => node.NodeId == config.Id && node.IsDead > 0)))
                return prior.DifficultyId;
        return 1;
    }

    private static bool IsPristine(GuildWarRoundState round) =>
        round.Nodes.Count == 0 && round.Monsters.Count == 0 && round.AttackPlan.Count == 0
        && round.PlayerNodes.Values.All(x => x == 0) && round.TotalActivation == 0 && round.TotalPoint == 0
        && round.GameThrough == 1 && round.BossDead == 0 && round.DragonRage == 0 && round.FullDragonRageTime == 0
        && round.GameThroughCfgId == 0 && round.DragonRageCfgId == 0 && round.FullDragonRageCfgId == 0
        && round.LastDragonRageReduceTime == 0 && round.DynamicsAdvancedAt == 0 && round.AttackTimes == 0 && round.NextAttackTime == 0
        && round.LastProtectNodeIds.Count == 0 && round.DefenseDic.Count == 0 && round.Stations.Count == 0
        && round.Reinforcements.Count == 0 && round.DynamicsActions.Count == 0 && round.RewardParticipants.Count == 0
        && round.ClearedNodeTypes.Count == 0 && round.FirstPointAt == 0 && round.FirstPassAt == 0 && round.Settlement is null;

    private static bool IsPristine(GuildWarPlayerRound player) =>
        player.DifficultyId == 0 && player.CurNodeId == 0 && player.Activation == 0 && player.Point == 0
        && player.RewardNodeIds.Count == 0 && player.RoundReward == 0 && player.PendingFight is null && player.FightRecords.Count == 0
        && player.FightCount == 0 && player.FirstPointAt == 0 && player.BossRewardIds.Count == 0 && player.Contributions.Count == 0
        && player.SupportSupply == 0 && player.ReceivedAssistSupply == 0 && player.ReceivedTimeSupply == 0
        && player.TeamInfo is null && player.HideAreaTeams.Count == 0 && player.LastHideAreaTeams.Count == 0 && player.HiddenPoint == 0
        && player.BeStationedFightRecord.Count == 0 && player.DefenseCount == 0 && player.ReinforcementSupportCount == 0
        && player.StationCount == 0 && player.StationReceipts.Count == 0;

    private static void InitializeMap(GuildWarRoundState round, int difficulty)
    {
        Require(TableReaderV2.Parse<GuildWarDifficultyTable>().Any(x => x.Id == difficulty), 20164007);
        round.DifficultyId = difficulty;
        round.Nodes = NodeRules.Value.Values.Where(x => x.DifficultyId == difficulty)
            .OrderBy(x => x.Id).Select(x => new GuildWarNodeState { Uid = x.Id, NodeId = x.Id, CurHp = x.HpMax, HpMax = x.HpMax }).ToList();
        InitializeDynamics(round, Now);
    }

    internal static GuildWarRoundState CurrentRound(GuildMutation mutation)
    {
        Refresh(mutation);
        Require(mutation.Guild.War.RoundId > 0, 20164005);
        var round = mutation.Guild.War.Rounds.Single(x => x.RoundId == mutation.Guild.War.RoundId);
        Require(mutation.Guild.CreatedAt <= round.StartedAt, 20164010);
        Require(round.DifficultyId > 0, 20164013);
        Require(!round.Settled && Now < round.EndsAt, 20164064);
        Require(mutation.Guild.Members[mutation.ActorSession.player.PlayerData.Id].JoinedAt <= round.StartedAt, 20164010);
        return round;
    }

    internal static GuildWarPlayerRound PlayerRound(GuildMutation mutation, long uid)
    {
        var state = mutation.Player(uid).War;
        var round = mutation.Guild.War.Rounds.Single(x => x.RoundId == mutation.Guild.War.RoundId);
        Require(mutation.Guild.CreatedAt <= round.StartedAt, 20164010);
        if (state.GuildId != mutation.Guild.Id || state.Season != mutation.Guild.War.Season)
        {
            state.GuildId = mutation.Guild.Id;
            state.Season = mutation.Guild.War.Season;
            state.Rounds.Clear();
            ClearSupport(state);
            ClearCombat(state);
        }
        var personal = state.Rounds.SingleOrDefault(x => x.RoundId == round.RoundId && x.GuildId == mutation.Guild.Id);
        if (personal is null)
        {
            personal = new() { GuildId = mutation.Guild.Id, RoundId = round.RoundId, DifficultyId = round.DifficultyId,
                CurNodeId = round.Nodes.FirstOrDefault(x => NodeRules.Value[x.NodeId].Type == 1)?.Uid ?? 0 };
            state.Rounds.Add(personal);
        }
        else if (round.DifficultyId > 0 && IsPristine(personal))
        {
            personal.DifficultyId = round.DifficultyId;
            personal.CurNodeId = round.Nodes.FirstOrDefault(x => NodeRules.Value[x.NodeId].Type == 1)?.Uid ?? 0;
        }
        if (mutation.Guild.CreatedAt <= round.StartedAt && mutation.Guild.Members[uid].JoinedAt <= round.StartedAt)
            round.PlayerNodes[uid] = personal.CurNodeId;
        return personal;
    }

    private static void RefreshEnergy(GuildMutation mutation, GuildWarParticipation player, GuildWarRoundState round)
    {
        // GuildWarConfig explicitly schedules energy at 20:00, in configured game time zone.
        if (mutation.Guild.Members[mutation.ActorSession.player.PlayerData.Id].JoinedAt > round.StartedAt) return;
        long day = (long)Math.Floor((GuildModule.GameDate(Clock()).DateTime - new DateTime(1970, 1, 1, 20, 0, 0)).TotalDays);
        if (player.EnergyDay >= day) return;
        long from = player.EnergyDay == 0 ? day - 1 : player.EnergyDay;
        int item = Setting("ActivityPointItemId");
        long grant = Math.Min(Setting("MaxEnergy") - mutation.Balance(mutation.ActorSession.player.PlayerData.Id, item), (day - from) * Setting("AddEnergy"));
        if (grant > 0) mutation.AddGoods(mutation.ActorSession.player.PlayerData.Id, item, checked((int)grant));
        // Explicit local catch-up policy for the unshipped rule: from GiveDays onward, while
        // below GameThrough, the highest-priority surviving node grants its authored daily bonus.
        var extra = ExtraEnergy.Value;
        if (extra.DifficultyIds.Contains(round.DifficultyId) && round.GameThrough < extra.GameThrough && (Now - round.StartedAt) / 86400 + 1 >= extra.GiveDays)
        {
            var node = extra.NodeConditions.OrderBy(x => x.Priority).FirstOrDefault(rule => round.Nodes.Any(n => IsDynamicsNodeActive(round, n) && n.IsDead == 0 && NodeRules.Value[n.NodeId].Type == rule.NodeType));
            int count = node is null ? 0 : checked((int)Math.Max(0, Math.Min(node.EnergyAdd, Setting("MaxEnergy") - mutation.Balance(mutation.ActorSession.player.PlayerData.Id, item))));
            if (count > 0)
            {
                mutation.AddGoods(mutation.ActorSession.player.PlayerData.Id, item, count);
                mutation.Push(mutation.ActorSession.player.PlayerData.Id, new NotifyAddExtraActionPoint { ItemId = item, Count = count });
            }
        }
        player.EnergyDay = day;
    }

    internal static void SpendEnergy(GuildMutation mutation, int amount)
    {
        Require(amount >= 0, 20164012);
        if (amount > 0) mutation.AddCost(mutation.ActorSession.player.PlayerData.Id, Setting("ActivityPointItemId"), amount);
    }

    internal static NotifyGuildWarActivityData BuildLoginData(Session session)
    {
        Guild? guild = GuildModule.FindMembership(session.player.PlayerData.Id);
        var dto = guild is null ? new NotifyGuildWarActivityData { ActivityNo = ActivityRule.Id, ActivityData = new(), PopupRecord = new() } : LoginData(guild, session.player.GuildState.War, session.player.PlayerData.Id);
        PopulateRewards(session, dto);
        return dto;
    }

    private static NotifyGuildWarActivityData LoginData(Guild guild, GuildWarParticipation player, long uid)
    {
        var dto = new NotifyGuildWarActivityData { ActivityNo = ActivityRule.Id, ActivityData = ActivityData(guild, uid), PopupRecord = new(),
            ActionPlayed = player.PlayedActionIds.Select(x => (dynamic)x).ToList(),
            MyRoundData = player.Rounds.Where(x => x.GuildId == guild.Id).Select(x => new NotifyGuildWarActivityData.NotifyGuildWarActivityDataMyRoundData {
                GuildId = guild.Id, RoundId = x.RoundId, DifficultyId = x.DifficultyId, CurNodeId = x.CurNodeId, Activation = x.Activation, SkipRound = Math.Max(guild.CreatedAt, guild.Members[uid].JoinedAt) > guild.War.Rounds.Single(r => r.RoundId == x.RoundId).StartedAt ? 1 : 0,
                Point = checked((int)Math.Min(int.MaxValue, x.Point)), RewardNodeIds = x.RewardNodeIds.Select(x => (dynamic)x).ToList(), RoundReward = x.RoundReward }).ToList() };
        PopulateRewards(dto, guild, player);
        PopulateSupportLogin(dto, player.Rounds.SingleOrDefault(x => x.RoundId == guild.War.RoundId));
        PopulateCombatLogin(player.Rounds.SingleOrDefault(x => x.RoundId == guild.War.RoundId), dto);
        dto.BeStationedFightRecord = player.Rounds.SingleOrDefault(x => x.RoundId == guild.War.RoundId)?.BeStationedFightRecord.ToList() ?? [];
        return dto;
    }

    internal static Activity ActivityData(Guild guild, long uid = 0)
    {
        var data = new Activity { CurRoundId = guild.War.RoundId, RestRoundId = guild.War.RoundId == 0 ? guild.War.Rounds.LastOrDefault()?.RoundId ?? 0 : guild.War.RoundId,
            RoundData = guild.War.Rounds.Select(round => RoundData(guild, round)).ToList(), NextDifficultyId = guild.War.NextDifficultyId, LastMaxDifficultyId = guild.War.LastMaxDifficultyId };
        foreach (var round in guild.War.Rounds)
        {
            PopulateDynamicsActions(round, data);
            PopulateDynamicsPlayer(guild, round, uid, data.RoundData.Single(x => x.RoundId == round.RoundId));
        }
        PopulateRewardActivity(data, guild);
        return data;
    }

    internal static Round RoundData(Guild guild, GuildWarRoundState round)
    {
        var data = new Round { RoundId = round.RoundId, DifficultyId = round.DifficultyId, SkipRound = guild.CreatedAt > round.StartedAt ? 1 : 0, NodeData = round.Nodes.Select(NodeData).ToList(), MonsterData = round.Monsters.Select(MonsterData).ToList(),
            AttackPlan = round.AttackPlan.Select(x => (dynamic)x).ToList(), TotalActivation = round.TotalActivation, TotalPoint = checked((uint)Math.Min(uint.MaxValue, round.TotalPoint)) };
        PopulateDynamics(round, data);
        return data;
    }
    internal static Node NodeData(GuildWarNodeState node) => new() { Uid = node.Uid, NodeId = node.NodeId, CurHp = checked((uint)node.CurHp), HpMax = checked((uint)node.HpMax), DeadTime = checked((int)node.DeadTime), FightCount = node.FightCount, IsDead = node.IsDead, CurMember = node.CurMember };
    internal static Monster MonsterData(GuildWarMonsterState monster) => new() { Uid = monster.Uid, MonsterId = monster.MonsterId, CurNodeIdx = monster.CurNodeIdx, CurHp = checked((int)monster.CurHp), HpMax = checked((uint)monster.HpMax), DeadTime = checked((uint)monster.DeadTime), FightCount = monster.FightCount, LastMoveDayNo = checked((uint)monster.LastMoveDayNo) };

    internal static void Publish(GuildMutation mutation)
    {
        UpdateNodeMembers(mutation.Guild);
        foreach (long uid in mutation.Guild.MemberIds.Where(uid => Server.Instance.SessionFromUID(uid) is not null))
            mutation.Push(uid, new NotifyGuildWarActivityDataChange { ActivityData = ActivityData(mutation.Guild, uid) });
        mutation.Push(mutation.ActorSession.player.PlayerData.Id, LoginData(mutation.Guild, mutation.Player(mutation.ActorSession.player.PlayerData.Id).War, mutation.ActorSession.player.PlayerData.Id));
    }

    private static void UpdateNodeMembers(Guild guild)
    {
        foreach (var round in guild.War.Rounds)
        {
            foreach (long uid in round.PlayerNodes.Keys.Where(uid => !guild.MemberIds.Contains(uid)).ToArray()) round.PlayerNodes.Remove(uid);
            var counts = round.PlayerNodes.Values.GroupBy(id => id).ToDictionary(group => group.Key, group => group.Count());
            foreach (var node in round.Nodes)
            {
                int root = NodeRules.Value[node.NodeId].RootId.GetValueOrDefault();
                node.CurMember = counts.GetValueOrDefault(root > 0 ? root : node.NodeId);
            }
        }
    }

    private static void ClearCombat(GuildWarParticipation player)
    {
        foreach (var round in player.Rounds) round.PendingFight = null;
    }

    internal static void OnMembershipChanged(long uid)
    {
        Session? session = Server.Instance.SessionFromUID(uid);
        if (session is null) return;
        Guild? guild = GuildModule.FindMembership(uid);
        if (guild is not null && session.player.GuildState.War.GuildId == guild.Id) return;
        ClearSupport(session.player.GuildState.War);
        ClearCombat(session.player.GuildState.War);
        // A new membership is fenced and durably normalized by the first war mutation.
        session.player.SaveChecked();
    }

    [RequestPacketHandler("GuildWarGetActivityDataRequest")]
    public static void GetActivityData(Session session, Packet.Request request) => GuildModule.Handle<GuildWarGetActivityDataRequest, GuildWarGetActivityDataResponse>(session, request, (m, _, r) =>
    {
        long uid = session.player.PlayerData.Id;
        GuildWarParticipation player = m.Player(uid).War;
        var personal = player.Rounds.SingleOrDefault(x => x.GuildId == m.Guild.Id && x.RoundId == m.Guild.War.RoundId);
        var before = (player.Season, player.GuildId, personal?.RoundId, personal?.DifficultyId, personal?.CurNodeId);
        Refresh(m);
        personal = player.Rounds.SingleOrDefault(x => x.GuildId == m.Guild.Id && x.RoundId == m.Guild.War.RoundId);
        if (before != (player.Season, player.GuildId, personal?.RoundId, personal?.DifficultyId, personal?.CurNodeId))
            m.Push(uid, LoginData(m.Guild, player, uid));
        r.ActivityData = ActivityData(m.Guild, uid);
    });

    [RequestPacketHandler("GuildWarSelectDifficultyRequest")]
    public static void SelectDifficulty(Session session, Packet.Request request) => GuildModule.Handle<GuildWarSelectDifficultyRequest, GuildWarSelectDifficultyResponse>(session, request, (m, q, _) =>
    {
        Refresh(m);
        GuildModule.RequirePermission(m.Guild, session.player.PlayerData.Id, GuildPermission.ManageWar);
        var rule = TableReaderV2.Parse<GuildWarDifficultyTable>().SingleOrDefault(x => x.Id == q.DifficultyId);
        Require(rule is not null, 20164007);
        Require(rule!.PreId.GetValueOrDefault() == 0 || m.Guild.War.LastMaxDifficultyId >= rule.PreId, 20164008);
        Require(m.Guild.War.NextDifficultyId != q.DifficultyId, 20164009);
        m.Guild.War.NextDifficultyId = q.DifficultyId;
        Publish(m);
    });

    [RequestPacketHandler("GuildWarEditLineRequest")]
    public static void EditLine(Session session, Packet.Request request) => GuildModule.Handle<GuildWarEditLineRequest, GuildWarEditLineResponse>(session, request, (m, q, _) =>
    {
        var round = CurrentRound(m);
        GuildModule.RequirePermission(m.Guild, session.player.PlayerData.Id, GuildPermission.ManageWar);
        Require(q.AttackPlan is not null && q.AttackPlan.Count <= round.Nodes.Count && q.AttackPlan.Distinct().Count() == q.AttackPlan.Count && q.AttackPlan.All(id => round.Nodes.Any(n => n.Uid == id)), 20164014);
        round.AttackPlan = q.AttackPlan!;
        Publish(m);
    });

    private static List<int>? Path(GuildWarRoundState round, int start, int target)
    {
        var nodes = round.Nodes.Where(n => IsDynamicsNodeActive(round, n)).ToDictionary(n => NodeRules.Value[n.NodeId].RootId.GetValueOrDefault() is > 0 ? NodeRules.Value[n.NodeId].RootId!.Value : n.NodeId);
        if (!nodes.ContainsKey(start) || !nodes.ContainsKey(target)) return null;
        var queue = new Queue<int>(); queue.Enqueue(start);
        var previous = new Dictionary<int, int> { [start] = 0 };
        while (queue.TryDequeue(out int current))
        {
            if (current == target)
            {
                var path = new List<int>();
                while (current != start) { path.Add(current); current = previous[current]; }
                path.Reverse(); return path;
            }
            foreach (int next in NodeRules.Value.Values.Where(n => n.Id == current || n.LinkIds.Contains(current)).SelectMany(n => n.Id == current ? n.LinkIds : [n.Id]).Distinct())
            {
                if (!nodes.ContainsKey(next) || previous.ContainsKey(next)) continue;
                var node = nodes[next];
                int type = NodeRules.Value[node.NodeId].Type;
                if (next != target && node.IsDead == 0 && type != 1 && type != 20) continue;
                if (NodeRules.Value[nodes[current].NodeId].Type is not (1 or 20) && nodes[current].IsDead == 0 && current != start) continue;
                previous[next] = current; queue.Enqueue(next);
            }
        }
        return null;
    }

    private static (GuildWarRoundState Round, GuildWarPlayerRound Player, int Cost) CheckMove(GuildMutation m, int current, int next)
    {
        var round = CurrentRound(m);
        var player = PlayerRound(m, m.ActorSession.player.PlayerData.Id);
        Require(player.CurNodeId == current && current != next, 20164025);
        var target = round.Nodes.SingleOrDefault(x => IsDynamicsNodeActive(round, x) && (NodeRules.Value[x.NodeId].RootId.GetValueOrDefault() is > 0 ? NodeRules.Value[x.NodeId].RootId : x.NodeId) == next);
        Require(target is not null, 20164014);
        int home = round.Nodes.Single(x => NodeRules.Value[x.NodeId].Type == 1).Uid;
        Require(Path(round, home, next) is not null, 20164026);
        int type = NodeRules.Value[target!.NodeId].Type;
        Require(type is not (3 or 6 or 7 or 10 or 11 or 14 or 19) || round.Nodes.Where(x => IsDynamicsNodeActive(round, x) && NodeRules.Value[x.NodeId].Type == 5).All(x => x.IsDead != 0), 20164026);
        var path = Path(round, current, next);
        Require(path is not null, 20164026);
        return (round, player, ReduceMoveCost(round, path!, path!.Count * Setting("MoveCostEnergy")));
    }

    [RequestPacketHandler("GuildWarMoveRequest")]
    public static void Move(Session session, Packet.Request request) => GuildModule.Handle<GuildWarMoveRequest, GuildWarMoveResponse>(session, request, (m, q, r) =>
    {
        var move = CheckMove(m, q.CurNodeId, q.NextNodeId);
        SpendEnergy(m, move.Cost);
        move.Player.CurNodeId = q.NextNodeId;
        move.Round.PlayerNodes[session.player.PlayerData.Id] = q.NextNodeId;
        UpdateNodeMembers(m.Guild);
        r.NodeDatas = move.Round.Nodes.Select(NodeData).ToList();
        Publish(m);
    });

    [RequestPacketHandler("GuildWarCanMoveRequest")]
    public static void CanMove(Session session, Packet.Request request) => GuildModule.Handle<GuildWarCanMoveRequest, GuildWarCanMoveResponse>(session, request, (m, q, _) => { CheckMove(m, q.CurNodeId, q.NextNodeId); });
}
