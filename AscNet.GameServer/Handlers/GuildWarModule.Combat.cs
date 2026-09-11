using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.guildwar;
using AscNet.Table.V2.share.fuben;
using AscNet.Table.V2.share.character;
using MessagePack;

namespace AscNet.GameServer.Handlers;

internal static partial class GuildWarModule
{
    internal static bool IsCombatStage(uint stageId) => TableReaderV2.Parse<GuildWarStageTable>().Any(row => row.StageId == stageId);

    private sealed record Encounter(GuildWarNodeState? Node, GuildWarMonsterState? Monster,
        GuildWarStageTable Stage, int Energy, int BaseDamage, int MaxDamage, int MinDamage,
        int MaxPoint, double A, double B, double C, double D)
    {
        public int Uid => Node?.Uid ?? Monster!.Uid;
        public int Type => Node is null ? 2 : 1;
        public long Hp => Node?.CurHp ?? Monster!.CurHp;
    }

    private static Encounter BattleEncounter(GuildMutation mutation, GuildWarRoundState round, int uid, int stageId, bool sweep, int fightType = 0)
    {
        GuildWarNodeState? node = round.Nodes.SingleOrDefault(row => row.Uid == uid);
        GuildWarMonsterState? monster = round.Monsters.SingleOrDefault(row => row.Uid == uid);
        Require(node is not null || monster is not null, fightType == 2 ? 20164017 : 20164014);
        Require(node is null || monster is null, 20164021);
        Require(fightType == 0 || fightType == (node is null ? 2 : 1), 20164021);
        GuildWarPlayerRound player = PlayerRound(mutation, mutation.ActorSession.player.PlayerData.Id);
        if (node is not null)
        {
            GuildWarNodeTable? config = TableReaderV2.Parse<GuildWarNodeTable>().SingleOrDefault(row => row.Id == node.NodeId);
            Require(config is not null, 20164063);
            Require(config!.Type is not (1 or 18 or 20) && config.GuildWarStageId > 0, 20164057);
            Require(config.DifficultyId == round.DifficultyId, 20164018);
            Require(IsDynamicsNodeActive(round, node), 20164022);
            int location = config.RootId.GetValueOrDefault() > 0 ? config.RootId!.Value : config.Id;
            Require(player.CurNodeId == location || player.CurNodeId == config.Id, 20164025);
            Require(node.CurHp > 0 && node.IsDead == 0, 20164015);
            GuildWarStageTable? stage = TableReaderV2.Parse<GuildWarStageTable>().SingleOrDefault(row => row.Id == config.GuildWarStageId);
            Require(stage is not null && stage.StageId == stageId, 20164018);
            Require(!sweep || config.Type is not (11 or 13 or 14), 20164057);
            return new(node, null, stage!, (sweep ? config.SweepCostEnergy : config.FightCostEnergy) ?? 0,
                config.BaseSubHp ?? 0, config.MaxSubHp ?? 0, config.MinSubDamage ?? 0, config.MaxPoint ?? 0,
                config.FactorA ?? 0, config.FactorB ?? 0, config.FactorC ?? 0, config.FactorD ?? 0);
        }
        GuildWarEliteMonsterTable? elite = TableReaderV2.Parse<GuildWarEliteMonsterTable>().SingleOrDefault(row => row.Id == monster!.MonsterId);
        Require(elite is not null, 20164020);
        GuildWarMonsterPatrolTable? patrol = TableReaderV2.Parse<GuildWarMonsterPatrolTable>().SingleOrDefault(row => row.Id == elite!.PatrolId);
        Require(patrol is not null && monster!.CurNodeIdx >= 0 && monster.CurNodeIdx < patrol.Routes.Count, 20164027);
        Require(player.CurNodeId == patrol!.Routes[monster!.CurNodeIdx], 20164025);
        Require(monster.CurHp > 0, 20164023);
        GuildWarStageTable? monsterStage = TableReaderV2.Parse<GuildWarStageTable>().SingleOrDefault(row => row.Id == elite!.GuildWarStageId);
        Require(monsterStage is not null && monsterStage.StageId == stageId, 20164018);
        return new(null, monster, monsterStage!, sweep ? elite!.SweepCostEnergy : elite!.FightCostEnergy,
            elite.BaseSubHp, elite.MaxSubHp, elite.MinSubDamage, elite.MaxPoint, elite.FactorA, elite.FactorB, elite.FactorC, elite.FactorD);
    }

    internal static bool TryPreFight(Session session, PreFightRequest request, out PreFightResponse response)
    {
        response = new();
        if (request.PreFightData is null || (!IsCombatStage(request.PreFightData.StageId) && request.PreFightData.GuildWarUid == 0)) return false;
        PreFightResponse result = new();
        try
        {
            GuildModule.Commit(session, GuildModule.RequireMembership(session), mutation =>
            {
                GuildWarRoundState round = CurrentRound(mutation);
                GuildWarPlayerRound player = PlayerRound(mutation, session.player.PlayerData.Id);
                var data = request.PreFightData;
                Require(data.StageId <= int.MaxValue && data.ChallengeCount == 1 && data.SelectAreaId == 0 && data.SpeedrunStageId == 0, 20164018);
                Encounter encounter = BattleEncounter(mutation, round, data.GuildWarUid, checked((int)data.StageId), false);
                GuildWarTeamInfo team = ValidateFightTeam(mutation, encounter.Node?.NodeId ?? 0);
                Require(data.CaptainPos == team.CaptainPos && data.FirstFightPos == team.FirstFightPos, 20164054);
                Require(data.CardIds is { Count: 3 } && data.RobotIds is { Count: 3 }, 20164054);
                for (int pos = 1; pos <= 3; pos++)
                {
                    GuildWarTeamCharacterInfo? member = team.CharacterInfos.SingleOrDefault(row => row.Pos == pos);
                    Require(data.CardIds![pos - 1] == (member?.Id ?? 0)
                        && data.RobotIds![pos - 1] == 0, 20164054);
                }
                Require(mutation.Balance(session.player.PlayerData.Id, Setting("ActivityPointItemId")) >= encounter.Energy, 20012004);
                GuildWarFightCandidate? previous = player.PendingFight;
                if (previous is { Settled: false } && previous.Uid == encounter.Uid && previous.StageId == data.StageId
                    && previous.GameThrough == round.GameThrough && MessagePackSerializer.Serialize(previous.Team).SequenceEqual(MessagePackSerializer.Serialize(team)))
                {
                    result = MessagePackSerializer.Deserialize<PreFightResponse>(previous.PreFightResponse);
                    return;
                }
                GuildWarFightCandidate candidate = new()
                {
                    FightId = Random.Shared.NextInt64(1, int.MaxValue), Seed = Random.Shared.NextInt64(0, (long)uint.MaxValue + 1),
                    StageId = checked((int)data.StageId), Uid = encounter.Uid, Type = encounter.Type,
                    NodeId = encounter.Node?.NodeId ?? 0, MonsterId = encounter.Monster?.MonsterId ?? 0,
                    GameThrough = round.GameThrough, AliveType = encounter.Hp > 0 ? 1 : 2, Energy = encounter.Energy,
                    CreatedAt = Now, Team = MessagePackSerializer.Deserialize<GuildWarTeamInfo>(MessagePackSerializer.Serialize(team))
                };
                result.FightData = BuildWarFightData(mutation, round, encounter, candidate, data);
                candidate.PreFightResponse = MessagePackSerializer.Serialize(result);
                player.PendingFight = candidate;
            });
            if (result.Code == 0) session.fight = new(request, result.FightData.FightId);
        }
        catch (ServerCodeException exception) { result = new() { Code = exception.Code }; }
        response = result;
        return true;
    }

    private static PreFightResponse.PreFightResponseFightData BuildWarFightData(GuildMutation mutation,
        GuildWarRoundState round, Encounter encounter, GuildWarFightCandidate candidate, PreFightRequest.PreFightRequestPreFightData request)
    {
        StageTable? stage = TableReaderV2.Parse<StageTable>().SingleOrDefault(row => row.StageId == candidate.StageId);
        Require(stage is not null, 20164018);
        GuildWarNodeTable? node = encounter.Node is null ? null : TableReaderV2.Parse<GuildWarNodeTable>().Single(row => row.Id == encounter.Node.NodeId);
        GuildWarDifficultyTable difficulty = TableReaderV2.Parse<GuildWarDifficultyTable>().Single(row => row.Id == round.DifficultyId);
        var data = new PreFightResponse.PreFightResponseFightData
        {
            FightId = checked((uint)candidate.FightId), Seed = checked((uint)candidate.Seed), StageId = checked((uint)candidate.StageId),
            RebootId = stage!.RebootId ?? 0, PassTimeLimit = stage.PassTimeLimit ?? 0,
            NormalEventIds = stage.NormalEventId.Select(id => (dynamic)id).ToList(),
            EventIds = difficulty.FightEventIds.Append(stage.EventId ?? 0).Concat(node?.FightEventId ?? []).Concat(DynamicsFightEvents(round, encounter.Node))
                .Where(id => id > 0).Distinct().Select(id => (dynamic)id).ToList(),
            Restartable = Convert.ToInt32(stage.Restartable) != 0, CustomData = string.Empty
        };
        HashSet<int> teamCharacters = candidate.Team.CharacterInfos.Where(member => member.Id > 0).Select(member => member.Id).ToHashSet();
        foreach (GuildWarSpecialRoleTeamTable special in TableReaderV2.Parse<GuildWarSpecialRoleTeamTable>())
        {
            int count = special.CharacterIds.Count(teamCharacters.Contains);
            int band = special.EnableNum.FindLastIndex(required => count >= required);
            if (band >= 0 && band < special.FightEventId.Count)
                data.EventIds.Add(special.FightEventId[band]);
        }
        if (encounter.Stage.MonsterLibraryId > 0)
        {
            var groups = TableReaderV2.Parse<GuildWarStageMonsterLibraryTable>().Where(row => row.LibraryId == encounter.Stage.MonsterLibraryId).OrderBy(row => row.Id).ToList();
            Require(groups.Count > 0, 20164018);
            data.NpcGroupList = groups.Select(group => new
            {
                NpcList = group.MonsterIds.SelectMany(id =>
                {
                    GuildWarStageMonsterTable monster = TableReaderV2.Parse<GuildWarStageMonsterTable>().Single(row => row.Id == id);
                    return Enumerable.Range(0, monster.Num).Select(_ => new
                    {
                        NpcId = monster.MonsterId, monster.Level, BufferIds = monster.FightEventIds,
                        AttrTable = new Dictionary<int, int>()
                    });
                }).ToList()
            }).ToList();
        }
        var role = new PreFightResponse.PreFightResponseFightData.PreFightResponseFightDataRoleData
        {
            Id = checked((uint)mutation.ActorSession.player.PlayerData.Id), Camp = 1, Name = mutation.ActorSession.player.PlayerData.Name,
            CaptainIndex = candidate.Team.CaptainPos - 1, FirstFightPos = candidate.Team.FirstFightPos - 1,
            EnterCgIndex = request.EnterCgIndex - 1, SettleCgIndex = request.SettleCgIndex - 1, NpcData = []
        };
        foreach (GuildWarTeamCharacterInfo member in candidate.Team.CharacterInfos)
        {
            if (member.Id == 0) continue;
            GuildWarFightNpcData npc = BuildSupportNpc(mutation, member.PlayerId, member.Id);
            role.NpcData.Add(member.Pos - 1, new
            {
                npc.Character, npc.Equips, npc.Partner, IsRobot = member.RobotId > 0, member.RobotId, IsNpc = false,
                CharacterCareer = TableReaderV2.Parse<CharacterTable>().Single(row => row.Id == npc.Character.Id).Career,
                MagicIds = new Dictionary<int, int>()
            });
        }
        var deployed = role.NpcData.Values.Select(npc => (CharacterData)npc.Character).ToArray();
        foreach (dynamic npc in role.NpcData.Values)
            foreach (var magic in FightModule.BuildObservationMagicIds(deployed, (CharacterData)npc.Character))
                npc.MagicIds.Add(magic.Key, magic.Value);
        data.RoleData.Add(role);
        return data;
    }

    internal static bool TrySettleFight(Session session, FightSettleResult request, out FightSettleResponse response)
    {
        response = new();
        if (!IsCombatStage(request.StageId)) return false;
        FightSettleResponse result = new();
        try
        {
            GuildModule.Commit(session, GuildModule.RequireMembership(session), mutation =>
            {
                GuildWarRoundState round = CurrentRound(mutation);
                GuildWarPlayerRound player = PlayerRound(mutation, session.player.PlayerData.Id);
                GuildWarFightCandidate? candidate = player.PendingFight;
                Require(candidate is not null && candidate.FightId == request.FightId, 20164028);
                Require(candidate!.StageId == request.StageId, 20164018);
                if (!candidate.Settled)
                {
                    Require(candidate.GameThrough == round.GameThrough, 20164022);
                    Encounter encounter = BattleEncounter(mutation, round, candidate.Uid, candidate.StageId, false, candidate.Type);
                    Require(encounter.Node?.NodeId == (candidate.NodeId == 0 ? null : candidate.NodeId), 20164022);
                    StageTable stage = TableReaderV2.Parse<StageTable>().Single(row => row.StageId == candidate.StageId);
                    Require(request.LeftTime is >= 0 and <= int.MaxValue
                        && (stage.PassTimeLimit is not > 0 || request.LeftTime <= stage.PassTimeLimit), 20164018);
                    Require(request.StartFrame >= 0 && request.SettleFrame >= request.StartFrame
                        && request.PauseFrame >= 0 && request.ExSkillPauseFrame >= 0
                        && request.PauseFrame <= request.SettleFrame - request.StartFrame
                        && request.TotalDamage >= 0 && request.DeathTotalEnemy >= 0, 20164018);
                    long activeFrames = request.SettleFrame - request.StartFrame - request.PauseFrame;
                    Require(activeFrames / 60 <= Math.Max(0, Now - candidate.CreatedAt) + 1, 20164018);
                    Require(request.ExSkillPauseFrame <= activeFrames, 20164018);
                    if (stage.PassTimeLimit is > 0)
                        Require(request.LeftTime <= Math.Max(0, stage.PassTimeLimit.Value - (activeFrames - request.ExSkillPauseFrame) / 60) + 1, 20164018);
                    HashSet<int> characters = candidate.Team.CharacterInfos.Where(member => member.Id > 0).Select(member => member.Id).ToHashSet();
                    foreach (NpcDpsTable npc in request.NpcDpsTable?.Values ?? Enumerable.Empty<NpcDpsTable>())
                        Require(npc.Value >= 0 && npc.MaxValue >= npc.Value && npc.DamageTotal >= 0
                            && (npc.CharacterId == 0 || characters.Contains(npc.CharacterId)), 20164054);
                    candidate.IsWin = request.IsWin && !request.IsForceExit;
                    candidate.LeftTime = checked((int)request.LeftTime);
                    candidate.Result = ScoreBattle(round, player, encounter, candidate.LeftTime, candidate.IsWin);
                    candidate.BasePoint = candidate.Result.Point;
                    candidate.Result.Point = DynamicsPoint(round, candidate.BasePoint);
                    candidate.Settled = true;
                    if (candidate.IsWin && encounter.Node is not null && TableReaderV2.Parse<GuildWarNodeTable>().Single(row => row.Id == encounter.Node.NodeId).Type == 13)
                    {
                        SpendEnergy(mutation, candidate.Energy);
                        Require(MessagePackSerializer.Serialize(candidate.Team).SequenceEqual(
                            MessagePackSerializer.Serialize(ValidateFightTeam(mutation, candidate.NodeId))), 20164053);
                        SetHiddenCandidate(mutation, candidate.NodeId, candidate.Result.Point);
                    }
                }
                result.Settle = new()
                {
                    StageId = candidate.StageId, IsWin = candidate.IsWin, LeftTime = candidate.LeftTime,
                    ChallengeCount = 1, GuildWarFightResult = candidate.Result
                };
            });
        }
        catch (ServerCodeException exception) { result = new() { Code = exception.Code }; }
        response = result;
        return true;
    }

    private static GuildWarFightResult ScoreBattle(GuildWarRoundState round, GuildWarPlayerRound player, Encounter encounter, int leftTime, bool win)
    {
        // Local-server policy: authored A-D define the bounded remaining-time performance curve;
        // authored BaseSubHp/MinSubDamage/MaxSubHp define map damage. No unshipped server scoring code exists in EN.
        double performance = encounter.B > 0 ? Math.Clamp((leftTime - encounter.C) / encounter.B, 0, 1) : 0;
        long point = win ? (long)Math.Clamp(Math.Floor(encounter.D * (encounter.A + (1 - encounter.A) * performance)), 0, encounter.MaxPoint) : 0;
        long damage = win ? (long)Math.Clamp(Math.Floor(encounter.BaseDamage + encounter.MinDamage * performance)
            + (encounter.Node is null ? 0 : StationDamage(round, encounter.Node)), 0, encounter.MaxDamage) : 0;
        GuildWarFightRecord? record = player.FightRecords.FirstOrDefault(row => row.Uid == encounter.Uid && row.GameThrough == round.GameThrough && row.AliveType == 1);
        return new()
        {
            Type = encounter.Type, NodeId = encounter.Node?.NodeId ?? 0, MonsterId = encounter.Monster?.MonsterId ?? 0,
            CurHp = encounter.Hp, Damage = damage, Point = point, MaxDamage = Math.Max(record?.MaxDamage ?? 0, damage),
            IsNewRecord = damage > (record?.MaxDamage ?? 0) ? 1 : 0, costCount = encounter.Energy
        };
    }

    [RequestPacketHandler("GuildWarConfirmFightResultRequest")]
    public static void ConfirmFightResult(Session session, Packet.Request packet) =>
        GuildModule.Handle<GuildWarConfirmFightResultRequest, GuildWarConfirmFightResultResponse>(session, packet, (mutation, request, response) =>
        {
            GuildWarRoundState round = CurrentRound(mutation);
            GuildWarPlayerRound player = PlayerRound(mutation, session.player.PlayerData.Id);
            GuildWarFightCandidate? candidate = player.PendingFight;
            Require(candidate is not null && candidate.Settled && candidate.IsWin, 20164028);
            Require(candidate!.StageId == request.StageId, 20164018);
            Require(candidate.NodeId == 0 || TableReaderV2.Parse<GuildWarNodeTable>().Single(row => row.Id == candidate.NodeId).Type != 13, 20164047);
            if (candidate.Confirmed) return;
            Require(candidate.GameThrough == round.GameThrough, 20164022);
            Encounter encounter = BattleEncounter(mutation, round, candidate.Uid, candidate.StageId, false, candidate.Type);
            Require(encounter.Node?.NodeId == (candidate.NodeId == 0 ? null : candidate.NodeId), 20164022);
            SpendEnergy(mutation, candidate.Energy);
            ConsumeTeamSupport(mutation, candidate.Team);
            candidate.Confirmed = true;
            ApplyBattle(mutation, round, player, encounter, candidate.Result.Damage, candidate.Result.Point, candidate.Energy, candidate.BasePoint);
            Publish(mutation);
        });

    [RequestPacketHandler("GuildWarSweepRequest")]
    public static void Sweep(Session session, Packet.Request packet) =>
        GuildModule.Handle<GuildWarSweepRequest, GuildWarSweepResponse>(session, packet, (mutation, request, response) =>
        {
            Require(request.SweepType is 1 or 2, 20164021);
            GuildWarRoundState round = CurrentRound(mutation);
            GuildWarPlayerRound player = PlayerRound(mutation, session.player.PlayerData.Id);
            Encounter encounter = BattleEncounter(mutation, round, request.Uid, request.StageId, true, request.SweepType);
            GuildWarFightRecord? record = player.FightRecords.Where(row => row.Uid == encounter.Uid && row.Type == encounter.Type
                && row.AliveType == 1 && row.GameThrough <= round.GameThrough).OrderByDescending(row => row.GameThrough).FirstOrDefault();
            Require(record is not null && record.MaxDamage > 0, 20164024);
            GuildWarDifficultyTable difficulty = TableReaderV2.Parse<GuildWarDifficultyTable>().Single(row => row.Id == round.DifficultyId);
            long damage = (long)Math.Floor(record!.MaxDamage * difficulty.SweepHpFactor);
            long point = DynamicsPoint(round, (long)Math.Floor(record.MaxPoint * difficulty.SweepPointFactor));
            SpendEnergy(mutation, encounter.Energy);
            ApplyBattle(mutation, round, player, encounter, damage, point, encounter.Energy, null);
            Publish(mutation);
        });

    private static void ApplyBattle(GuildMutation mutation, GuildWarRoundState round, GuildWarPlayerRound player,
        Encounter encounter, long damage, long point, int energy, long? basePoint)
    {
        int cycle = round.GameThrough;
        bool killed = encounter.Hp > 0 && damage >= encounter.Hp;
        if (basePoint.HasValue)
        {
            GuildWarFightRecord? record = player.FightRecords.FirstOrDefault(row => row.Uid == encounter.Uid && row.GameThrough == cycle && row.AliveType == 1);
            if (record is null)
            {
                record = new() { Uid = encounter.Uid, Type = encounter.Type, GameThrough = cycle, AliveType = 1 };
                player.FightRecords.Add(record);
            }
            record.MaxDamage = Math.Max(record.MaxDamage, damage);
            record.MaxPoint = Math.Max(record.MaxPoint, basePoint.Value);
        }
        if (encounter.Node is not null)
        {
            encounter.Node.CurHp = Math.Max(0, encounter.Node.CurHp - damage);
            encounter.Node.FightCount++;
            if (killed) { encounter.Node.IsDead = 1; encounter.Node.DeadTime = Now; }
            GuildWarNodeTable config = TableReaderV2.Parse<GuildWarNodeTable>().Single(row => row.Id == encounter.Node.NodeId);
            int root = config.RootId.GetValueOrDefault() > 0 ? config.RootId!.Value : config.Id;
            if (!player.BeStationedFightRecord.Contains(root)) player.BeStationedFightRecord.Add(root);
        }
        else
        {
            encounter.Monster!.CurHp = Math.Max(0, encounter.Monster.CurHp - damage);
            encounter.Monster.FightCount++;
            if (killed) encounter.Monster.DeadTime = Now;
        }
        player.Point = checked(player.Point + point);
        round.TotalPoint = checked(round.TotalPoint + point);
        // Local-server activation policy: accepted combat expenditure is its activity contribution.
        player.Activation = checked(player.Activation + energy);
        round.TotalActivation = checked(round.TotalActivation + energy);
        RecordRewardFight(mutation, round, player, encounter.Node, encounter.Monster, damage, point, killed, energy);
        ApplyDynamicsFight(round, encounter.Node);
        if (killed) ApplyDefeat(mutation, round, encounter.Node, encounter.Monster);
    }

    internal static void PopulateCombatLogin(GuildWarPlayerRound? player, NotifyGuildWarActivityData data)
    {
        if (player is not null)
            data.FightRecords = player.FightRecords.GroupBy(record => record.Uid)
                .Select(group => group.OrderByDescending(record => record.GameThrough).First()).Select(record => (dynamic)record).ToList();
    }
}
