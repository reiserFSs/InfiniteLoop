using MessagePack;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.guildwar;
using RoundWire = AscNet.Common.MsgPack.NotifyGuildWarActivityData.NotifyGuildWarActivityDataActivityData.NotifyGuildWarActivityDataActivityDataRoundData;
using ActivityWire = AscNet.Common.MsgPack.NotifyGuildWarActivityData.NotifyGuildWarActivityDataActivityData;

namespace AscNet.GameServer.Handlers;

internal static partial class GuildWarModule
{
    private static readonly Lazy<GuildWarNodeTable[]> DynamicsNodes = new(() => TableReaderV2.Parse<GuildWarNodeTable>().ToArray());
    private static readonly Lazy<GuildWarPlayThroughTable[]> Cycles = new(() => TableReaderV2.Parse<GuildWarPlayThroughTable>().ToArray());
    private static readonly Lazy<GuildWarDragonRageTable[]> Rages = new(() => TableReaderV2.Parse<GuildWarDragonRageTable>().ToArray());
    private static readonly Lazy<GuildWarDragonRageNodeChangeTable[]> RageChanges = new(() => TableReaderV2.Parse<GuildWarDragonRageNodeChangeTable>().ToArray());
    private static readonly Lazy<GuildWarReinforcementsTable[]> ReinforcementConfigs = new(() => TableReaderV2.Parse<GuildWarReinforcementsTable>().ToArray());
    private static readonly Lazy<GuildWarMonsterPatrolTable[]> Patrols = new(() => TableReaderV2.Parse<GuildWarMonsterPatrolTable>().ToArray());
    private static readonly Lazy<GuildWarEliteMonsterTable[]> Elites = new(() => TableReaderV2.Parse<GuildWarEliteMonsterTable>().ToArray());

    private static GuildWarNodeTable DynamicsNode(int id) => DynamicsNodes.Value.Single(x => x.Id == id);
    private static int RootNode(int id)
    {
        int root = DynamicsNode(id).RootId ?? 0;
        return root > 0 ? root : id;
    }
    private static GuildWarPlayThroughTable Cycle(GuildWarRoundState round) => Cycles.Value.Where(x => x.DifficultyId == round.DifficultyId && x.PlayThrough <= round.GameThrough).OrderByDescending(x => x.PlayThrough).First();
    private static GuildWarDragonRageTable Rage(GuildWarRoundState round) => Rages.Value.Where(x => x.DifficultyId == round.DifficultyId && x.PlayThrough <= round.GameThrough).OrderByDescending(x => x.PlayThrough).First();
    private static GuildWarDragonRageNodeChangeTable RageChange(GuildWarRoundState round) => RageChanges.Value.Where(x => x.DifficultyId == round.DifficultyId && x.PlayThrough <= round.GameThrough).OrderByDescending(x => x.PlayThrough).First();

    internal static void InitializeDynamics(GuildWarRoundState round, long now)
    {
        round.GameThrough = Math.Max(1, round.GameThrough);
        round.Season = Calendar(DateTimeOffset.FromUnixTimeSeconds(now)).Season;
        round.GameThroughCfgId = Cycle(round).Id;
        round.DynamicsAdvancedAt = now;
        round.LastDragonRageReduceTime = now;
        if (round.Nodes.Any(x => DynamicsNode(x.NodeId).Type == 18))
            round.NextAttackTime = now + Setting("ResourceNodeAttackedInterval");
        // Explicit local spawn policy: one of each authored elite whose full patrol belongs
        // to this map, at its first waypoint on round creation; no unconfigured respawns.
        long day = GuildModule.DailyPeriod(DateTimeOffset.FromUnixTimeSeconds(now));
        foreach (var elite in Elites.Value)
        {
            var patrol = Patrols.Value.Single(x => x.Id == elite.PatrolId);
            if (patrol.Routes.Count == 0 || !patrol.Routes.All(id => round.Nodes.Any(x => x.NodeId == id))) continue;
            var monster = new GuildWarMonsterState { Uid = elite.Id, MonsterId = elite.Id, CurNodeIdx = 0, CurHp = elite.HpMax, HpMax = elite.HpMax, LastMoveDayNo = day };
            round.Monsters.Add(monster);
            AddDynamicsAction(round, 2, now, monster: monster);
        }
    }

    internal static bool IsDynamicsNodeActive(GuildWarRoundState round, GuildWarNodeState node)
    {
        var cfg = DynamicsNode(node.NodeId);
        int root = RootNode(node.NodeId);
        bool relic = Cycle(round).ChangeNodeIds.Contains(root);
        if (relic) return cfg.Type == 20;
        if (cfg.Type == 20) return false;
        bool changed = round.FullDragonRageCfgId != 0 && RageChange(round).ChangeNodeIds.Contains(root);
        var baseNode = round.Nodes.FirstOrDefault(x => x.NodeId == root);
        if (baseNode?.IsDead != 0) changed = false;
        return changed ? cfg.RootId > 0 : !(cfg.RootId > 0);
    }

    internal static int ReduceMoveCost(GuildWarRoundState round, List<int> route, int cost)
    {
        var freeNodes = Cycle(round).ChangeNodeIds;
        return Math.Max(0, cost - route.Count(id => freeNodes.Contains(RootNode(id))) * Setting("MoveCostEnergy"));
    }

    internal static void AdvanceDynamics(GuildMutation mutation, GuildWarRoundState round, long now)
    {
        now = Math.Min(now, round.EndsAt);
        while (round.DynamicsAdvancedAt < now)
        {
            long cursor = round.DynamicsAdvancedAt;
            long next = now;
            foreach (var node in round.Nodes)
            {
                if (node.NextReinforcementsBornTime > cursor) next = Math.Min(next, node.NextReinforcementsBornTime);
                if (node.RebuildAt > cursor) next = Math.Min(next, node.RebuildAt);
                if (node.NextBossTreatMstTime > cursor) next = Math.Min(next, node.NextBossTreatMstTime);
            }
            foreach (var reinforcement in round.Reinforcements)
                if (reinforcement.CurHp > 0 && reinforcement.NextMoveTime > cursor) next = Math.Min(next, reinforcement.NextMoveTime);
            if (round.NextAttackTime > cursor) next = Math.Min(next, round.NextAttackTime);
            if (round.FullDragonRageCfgId > 0 && round.GameThrough < Setting("FullDragonRageGameThrough"))
                next = Math.Min(next, Math.Max(cursor + 1, round.LastDragonRageReduceTime + Rage(round).ReduceIntervalMinute * 60L));
            if (round.Monsters.Any(x => x.CurHp > 0))
            {
                long weekly = GuildModule.NextWeeklyReset(DateTimeOffset.FromUnixTimeSeconds(cursor)).ToUnixTimeSeconds();
                next = Math.Min(next, weekly - (weekly - cursor - 1) / 86400 * 86400);
            }
            AdvanceDynamicsAt(mutation, round, next);
        }
        // Also reconcile departed participants when no clock tick elapsed.
        round.Stations.RemoveAll(x => !mutation.Guild.MemberIds.Contains(x.PlayerId));
        foreach (long uid in round.DefenseDic.Keys.Where(uid => !mutation.Guild.MemberIds.Contains(uid)).ToArray()) round.DefenseDic.Remove(uid);
    }

    private static void AdvanceDynamicsAt(GuildMutation mutation, GuildWarRoundState round, long now)
    {
        now = Math.Min(now, round.EndsAt);
        round.Stations.RemoveAll(x => !mutation.Guild.MemberIds.Contains(x.PlayerId));
        foreach (long uid in round.DefenseDic.Keys.Where(uid => !mutation.Guild.MemberIds.Contains(uid)).ToArray()) round.DefenseDic.Remove(uid);
        foreach (var reinforcement in round.Reinforcements) reinforcement.SupportedPlayerIds.RemoveAll(uid => !mutation.Guild.MemberIds.Contains(uid));
        if (now <= round.DynamicsAdvancedAt) return;
        if (round.DragonRageCfgId != 0 && round.FullDragonRageCfgId != 0 && round.GameThrough < Setting("FullDragonRageGameThrough"))
        {
            var rage = Rage(round);
            long interval = checked(rage.ReduceIntervalMinute * 60L);
            if (interval <= 0) throw new InvalidOperationException("Guild War rage reduction interval must be positive.");
            long ticks = (now - round.LastDragonRageReduceTime) / interval;
            if (ticks > 0)
            {
                round.LastDragonRageReduceTime += ticks * interval;
                SetRage(round, (int)Math.Max(0, round.DragonRage - ticks * rage.IntervalReduce), round.LastDragonRageReduceTime);
            }
        }
        AdvancePatrols(round, now);
        AdvanceTreatment(round, now);
        foreach (var node in round.Nodes.Where(x => x.RebuildAt > 0 && x.RebuildAt <= now))
        {
            node.CurHp = node.HpMax;
            node.IsDead = 0;
            node.DeadTime = 0;
            node.RebuildAt = 0;
        }
        AdvanceReinforcements(mutation, round, now);
        if (round.NextAttackTime > 0)
        {
            int interval = Setting("ResourceNodeAttackedInterval");
            if (interval <= 0) throw new InvalidOperationException("Guild War resource attack interval must be positive.");
            while (round.NextAttackTime <= now)
            {
                var counts = round.DefenseDic.Values.GroupBy(x => x).ToArray();
                int maximum = counts.Length == 0 ? 0 : counts.Max(x => x.Count());
                round.LastProtectNodeIds = counts.Where(x => x.Count() == maximum).Select(x => x.Key).ToList();
                // Local policy for the retired resource-node mechanic: all tied largest real
                // garrisons protect their node; other resources rebuild at the next attack tick.
                foreach (var resource in round.Nodes.Where(x => DynamicsNode(x.NodeId).Type == 18 && !round.LastProtectNodeIds.Contains(x.NodeId)))
                {
                    resource.CurHp = 0;
                    resource.IsDead = 1;
                    resource.DeadTime = round.NextAttackTime;
                    resource.RebuildAt = round.NextAttackTime + interval;
                }
                round.AttackTimes++;
                round.NextAttackTime += interval;
            }
        }
        round.DynamicsAdvancedAt = now;
    }

    private static void AdvancePatrols(GuildWarRoundState round, long now)
    {
        long day = GuildModule.DailyPeriod(DateTimeOffset.FromUnixTimeSeconds(now));
        foreach (var monster in round.Monsters.Where(x => x.CurHp > 0))
        {
            var elite = Elites.Value.Single(x => x.Id == monster.MonsterId);
            var route = Patrols.Value.Single(x => x.Id == elite.PatrolId).Routes;
            // Explicit local clock: one route position per global daily reset.
            // CurNodeIdx is zero-based (XGWEliteMonster indexes Routes[CurNodeIdx + 1]).
            while (monster.LastMoveDayNo < day && monster.CurHp > 0)
            {
                int previous = monster.CurNodeIdx;
                int next = Math.Min(route.Count - 1, previous + 1);
                monster.CurNodeIdx = next;
                monster.LastMoveDayNo++;
                var action = AddDynamicsAction(round, 3, now, monster: monster);
                action.PreNodeIdx = previous;
                action.NextNodeIdx = next;
                if (next == route.Count - 1)
                {
                    var home = round.Nodes.FirstOrDefault(x => x.NodeId == route[^1]);
                    if (home != null)
                    {
                        long damage = Math.Min(home.CurHp, home.HpMax * elite.DamagePercent / 100);
                        home.CurHp -= damage;
                        action = AddDynamicsAction(round, 4, now, node: home, monster: monster);
                        action.Damage = damage;
                    }
                    monster.CurHp = 0;
                    monster.DeadTime = now;
                }
            }
        }
    }

    internal static void ApplyDefeat(GuildMutation mutation, GuildWarRoundState round, GuildWarNodeState? node, GuildWarMonsterState? monster) => ApplyDefeatAt(mutation, round, node, monster, Now);

    private static void ApplyDefeatAt(GuildMutation mutation, GuildWarRoundState round, GuildWarNodeState? node, GuildWarMonsterState? monster, long now)
    {
        AddDynamicsAction(round, node != null ? 5 : 1, now, node: node, monster: monster);
        if (node == null) return;
        var cfg = DynamicsNode(node.NodeId);
        int root = RootNode(node.NodeId);
        foreach (var samePosition in round.Nodes.Where(x => RootNode(x.NodeId) == root && x != node && DynamicsNode(x.NodeId).Type != 20))
        {
            samePosition.CurHp = 0;
            samePosition.IsDead = 1;
            samePosition.DeadTime = now;
        }
        if (cfg.Type == 19)
        {
            round.BossDead++;
            if (round.FirstPassAt == 0) round.FirstPassAt = now;
            round.GameThrough++;
            round.GameThroughCfgId = Cycle(round).Id;
            round.DragonRage = 0;
            round.FullDragonRageCfgId = 0;
            round.DragonRageCfgId = Rage(round).Id;
            round.LastDragonRageReduceTime = now;
            foreach (var current in round.Nodes)
            {
                var currentCfg = DynamicsNode(current.NodeId);
                current.HpMax = (long)currentCfg.HpMax * Cycle(round).AddNodeHpPercent / 10000;
                current.CurHp = current.HpMax;
                current.IsDead = 0;
                current.DeadTime = 0;
            }
            // Real station assignments persist across cycles, as the client station record does.
            foreach (var reinforcement in round.Reinforcements.Where(x => x.CurHp > 0)) { reinforcement.CurHp = 0; reinforcement.DeadTime = now; }
            foreach (var current in round.Nodes) current.NextReinforcementsBornTime = 0;
            AddDynamicsAction(round, 21, now);
            if (Cycle(round).ChangeNodeIds.Count > 0) AddDynamicsAction(round, 20, now);
            if (round.GameThrough >= Setting("FullDragonRageGameThrough")) SetRage(round, Rage(round).UpLimit, now);
            return;
        }
        if (round.DragonRageCfgId == 0 && cfg.Type == Setting("DragonRageOpenNodeType")) round.DragonRageCfgId = Rage(round).Id;
        if (ReinforcementConfigs.Value.Any(x => x.DifficultyId == round.DifficultyId && x.BornNodeId == root))
            SpawnReinforcement(round, round.Nodes.Single(x => x.NodeId == root), now);
    }

    internal static long DynamicsPoint(GuildWarRoundState round, long point) => checked(point * Cycle(round).AddPointPercent) / 10000;

    internal static void ApplyDynamicsFight(GuildWarRoundState round, GuildWarNodeState? node)
    {
        if (round.DragonRageCfgId == 0) return;
        var rage = Rage(round);
        SetRage(round, round.DragonRage + (round.FullDragonRageCfgId == 0 ? rage.PassAdd : -rage.PassReduce), Now);
    }

    private static void SetRage(GuildWarRoundState round, int value, long now)
    {
        var cfg = Rage(round);
        round.DragonRage = round.GameThrough >= Setting("FullDragonRageGameThrough") ? cfg.UpLimit : Math.Clamp(value, 0, cfg.UpLimit);
        if (round.DragonRage == cfg.UpLimit && round.FullDragonRageCfgId == 0)
        {
            round.FullDragonRageCfgId = RageChange(round).Id;
            round.FullDragonRageTime++;
            round.LastDragonRageReduceTime = now;
            SynchronizeVariantHp(round);
            ArmTreatment(round, now);
            AddDynamicsAction(round, 18, now);
        }
        else if (round.DragonRage == 0 && round.FullDragonRageCfgId != 0)
        {
            SynchronizeVariantHp(round);
            round.FullDragonRageCfgId = 0;
            AddDynamicsAction(round, 19, now);
            ArmTreatment(round, now);
        }
    }

    private static void ArmTreatment(GuildWarRoundState round, long now)
    {
        foreach (var node in round.Nodes)
        {
            int interval = DynamicsNode(node.NodeId).TreatMstInterval ?? 0;
            if (interval <= 0 || node.CurHp <= 0 || !IsDynamicsNodeActive(round, node)) node.NextBossTreatMstTime = 0;
            else if (node.NextBossTreatMstTime == 0) node.NextBossTreatMstTime = now + interval * 3600L;
        }
    }

    private static void AdvanceTreatment(GuildWarRoundState round, long now)
    {
        ArmTreatment(round, now);
        foreach (var node in round.Nodes.Where(x => x.NextBossTreatMstTime > 0 && x.NextBossTreatMstTime <= now))
        {
            var cfg = DynamicsNode(node.NodeId);
            // Explicit local unit policy for the inherited TreatMstInterval column: hours.
            // TreatMstPercent is percent of each living elite's maximum HP, capped at max;
            // no healing tick revives dead monsters or fabricates new encounters.
            long interval = (cfg.TreatMstInterval ?? 0) * 3600L;
            while (node.NextBossTreatMstTime <= now)
            {
                long tick = node.NextBossTreatMstTime;
                node.NextBossTreatMstTime += interval;
                foreach (var monster in round.Monsters.Where(x => x.CurHp > 0 && x.CurHp < x.HpMax))
                {
                    monster.CurHp = Math.Min(monster.HpMax, monster.CurHp + monster.HpMax * (cfg.TreatMstPercent ?? 0) / 100);
                    AddDynamicsAction(round, 12, tick, node: node, monster: monster);
                }
            }
        }
    }

    private static void SynchronizeVariantHp(GuildWarRoundState round)
    {
        foreach (int root in RageChange(round).ChangeNodeIds)
        {
            var variants = round.Nodes.Where(x => RootNode(x.NodeId) == root && DynamicsNode(x.NodeId).Type != 20).ToArray();
            if (variants.Length < 2) continue;
            long hp = variants.Min(x => x.CurHp);
            foreach (var variant in variants) variant.CurHp = Math.Min(variant.HpMax, hp);
        }
    }

    private static void SpawnReinforcement(GuildWarRoundState round, GuildWarNodeState born, long now)
    {
        var configs = ReinforcementConfigs.Value.Where(x => x.DifficultyId == round.DifficultyId && x.BornNodeId == born.NodeId).OrderBy(x => x.Id).ToArray();
        if (configs.Length == 0) return;
        var config = configs[(Array.FindIndex(configs, x => x.Id == born.LastReinforcementId) + 1) % configs.Length];
        if (config.AttackIntervalSecond <= 0) throw new InvalidOperationException("Guild War reinforcement interval must be positive.");
        // Explicit local schedule: cycle the authored variants, one reinforcement per interval
        // after this spawn point is cleared. No captured waves, members, or damage are used.
        var reinforcement = new GuildWarReinforcementState { Uid = round.Reinforcements.Count == 0 ? 1 : round.Reinforcements.Max(x => x.Uid) + 1, ReinforcementId = config.Id,
            CurHp = config.HpMax, HpMax = config.HpMax, CurNodeId = config.BornNodeId,
            ReadyDoneTime = now + config.AttackIntervalSecond, NextMoveTime = now + config.AttackIntervalSecond };
        round.Reinforcements.Add(reinforcement);
        born.LastReinforcementId = config.Id;
        born.NextReinforcementsBornTime = now + config.AttackIntervalSecond;
        AddDynamicsAction(round, 14, now, reinforcement: reinforcement);
    }

    private static void AdvanceReinforcements(GuildMutation mutation, GuildWarRoundState round, long now)
    {
        // ponytail: process at most the finite round's authored ticks; use an event heap only
        // if future maps introduce enough simultaneous actors to make this scan material.
        while (true)
        {
            var born = round.Nodes.Where(x => x.NextReinforcementsBornTime > 0 && x.NextReinforcementsBornTime <= now).OrderBy(x => x.NextReinforcementsBornTime).FirstOrDefault();
            var actor = round.Reinforcements.Where(x => x.CurHp > 0 && x.NextMoveTime > 0 && x.NextMoveTime <= now).OrderBy(x => x.NextMoveTime).FirstOrDefault();
            if (born == null && actor == null) break;
            if (born != null && (actor == null || born.NextReinforcementsBornTime < actor.NextMoveTime)) { SpawnReinforcement(round, born, born.NextReinforcementsBornTime); continue; }
            long tick = actor!.NextMoveTime;
            var config = ReinforcementConfigs.Value.Single(x => x.Id == actor.ReinforcementId);
            int index = config.MoveNextNodeIds.IndexOf(actor.CurNodeId) + 1;
            if (index >= config.MoveNextNodeIds.Count) { actor.NextMoveTime = 0; continue; }
            int targetId = config.MoveNextNodeIds[index];
            var target = round.Nodes.FirstOrDefault(x => RootNode(x.NodeId) == targetId && IsDynamicsNodeActive(round, x));
            int previous = actor.CurNodeId;
            actor.CurNodeId = targetId;
            actor.NextMoveTime += config.AttackIntervalSecond;
            var action = AddDynamicsAction(round, 15, tick, reinforcement: actor);
            action.PreNodeIdx = previous;
            action.NextNodeIdx = targetId;
            if (target == null || target.CurHp <= 0 || DynamicsNode(target.NodeId).Type == 20) continue;
            // Local combat policy: this one-use reinforcement deals its remaining HP in map
            // damage; support adds the authored HP-per-resource amount before its first attack.
            long damage = Math.Min(target.CurHp, actor.CurHp);
            target.CurHp -= damage;
            actor.Attacked = true;
            actor.CurHp = 0;
            actor.DeadTime = tick;
            actor.NextMoveTime = 0;
            target.LastAttackedReinforcementId = config.Id;
            target.LastReinforcementAttackedTime = tick;
            action = AddDynamicsAction(round, 16, tick, node: target, reinforcement: actor);
            action.Damage = damage;
            AddDynamicsAction(round, 17, tick, reinforcement: actor);
            if (target.CurHp == 0) { target.IsDead = 1; target.DeadTime = tick; ApplyDefeatAt(mutation, round, target, null, tick); }
        }
    }

    internal static IEnumerable<int> DynamicsFightEvents(GuildWarRoundState round, GuildWarNodeState? node)
    {
        foreach (int id in Cycle(round).FightEventIds) if (id > 0) yield return id;
        if (node == null || node.LastAttackedReinforcementId == 0 || node.LastReinforcementAttackedTime + Setting("ReinforcementBuffEffectInterval") <= Now) yield break;
        foreach (int id in ReinforcementConfigs.Value.Single(x => x.Id == node.LastAttackedReinforcementId).FightEventIds) if (id > 0) yield return id;
    }

    internal static int StationDamage(GuildWarRoundState round, GuildWarNodeState node)
    {
        var cfg = DynamicsNode(node.NodeId);
        int count = round.Stations.Count(x => x.NodeId == RootNode(node.NodeId));
        int result = 0;
        for (int i = 0; i < Math.Min(cfg.DeployCharacterNum.Count, cfg.DeployBuff.Count); i++)
            if (count >= cfg.DeployCharacterNum[i]) result = cfg.DeployBuff[i];
        return result;
    }

    internal static bool IsCharacterStationed(GuildMutation mutation, long uid, int characterId) => CurrentRound(mutation).Stations.Any(x => x.PlayerId == uid && x.CharacterId == characterId);

    [RequestPacketHandler("GuildWarSupportReinforcementRequest")]
    public static void GuildWarSupportReinforcementRequestHandler(Session session, Packet.Request packet) =>
        GuildModule.Handle<GuildWarSupportReinforcementRequest, GuildWarSupportReinforcementResponse>(session, packet, (mutation, request, response) =>
        {
            var round = CurrentRound(mutation);
            var actor = round.Reinforcements.FirstOrDefault(x => x.Uid == request.ReinforcementUid);
            Require(actor != null, 20164065);
            Require(actor!.CurHp > 0, 20164069);
            Require(!actor.Attacked, 20164071);
            long uid = session.player.PlayerData.Id;
            Require(!actor.SupportedPlayerIds.Contains(uid), 20164066);
            var config = ReinforcementConfigs.Value.Single(x => x.Id == actor.ReinforcementId);
            SpendEnergy(mutation, config.SupportCost);
            actor.SupportedPlayerIds.Add(uid);
            long hp = checked(config.SupportCost * (long)Setting("ReinforcementSupportHp"));
            actor.HpMax += hp;
            actor.CurHp += hp;
            PlayerRound(mutation, uid).ReinforcementSupportCount++;
            response.ReinforcementData = ReinforcementData(actor);
            Publish(mutation);
        });

    [RequestPacketHandler("GuildWarCancelSupportReinforcementRequest")]
    public static void GuildWarCancelSupportReinforcementRequestHandler(Session session, Packet.Request packet) =>
        GuildModule.Handle<GuildWarSupportReinforcementRequest, GuildWarCancelSupportReinforcementResponse>(session, packet, (mutation, request, response) =>
        {
            var round = CurrentRound(mutation);
            var actor = round.Reinforcements.FirstOrDefault(x => x.Uid == request.ReinforcementUid);
            Require(actor != null, 20164065);
            Require(!actor!.Attacked, 20164070);
            Require(actor.CurHp > 0, 20164069);
            long uid = session.player.PlayerData.Id;
            Require(actor.SupportedPlayerIds.Remove(uid), 20164068);
            var config = ReinforcementConfigs.Value.Single(x => x.Id == actor.ReinforcementId);
            mutation.AddGoods(uid, Setting("ActivityPointItemId"), config.SupportCost);
            long hp = checked(config.SupportCost * (long)Setting("ReinforcementSupportHp"));
            actor.HpMax -= hp;
            actor.CurHp = Math.Max(0, actor.CurHp - hp);
            response.ReinforcementData = ReinforcementData(actor);
            Publish(mutation);
        });

    [RequestPacketHandler("XGuildWarSelectDefenseNodeRequest")]
    public static void XGuildWarSelectDefenseNodeRequestHandler(Session session, Packet.Request packet) =>
        GuildModule.Handle<XGuildWarSelectDefenseNodeRequest, XGuildWarSelectDefenseNodeResponse>(session, packet, (mutation, request, _) =>
        {
            var round = CurrentRound(mutation);
            Require(round.Nodes.Any(x => x.NodeId == request.NodeId && DynamicsNode(x.NodeId).Type == 18), 20164014);
            long uid = session.player.PlayerData.Id;
            if (!round.DefenseDic.ContainsKey(uid)) PlayerRound(mutation, uid).DefenseCount++;
            round.DefenseDic[uid] = request.NodeId;
            Publish(mutation);
        });

    [RequestPacketHandler("XGuildWarBeStationedRequest")]
    public static void XGuildWarBeStationedRequestHandler(Session session, Packet.Request packet) =>
        GuildModule.Handle<XGuildWarBeStationedRequest, XGuildWarBeStationedResponse>(session, packet, (mutation, request, response) =>
        {
            var round = CurrentRound(mutation);
            var node = round.Nodes.FirstOrDefault(x => x.Uid == request.NodeUid);
            Require(node != null, 20164014);
            var cfg = DynamicsNode(node!.NodeId);
            Require(cfg.Type is not (1 or 14 or 15 or 19 or 20) && IsDynamicsNodeActive(round, node), 20164074);
            long uid = session.player.PlayerData.Id;
            int root = RootNode(node.NodeId);
            var player = PlayerRound(mutation, uid);
            Require(player.BeStationedFightRecord.Any(id => RootNode(id) == root), 20164075);
            var existing = round.Stations.FirstOrDefault(x => x.PlayerId == uid && x.NodeId == root);
            if (request.CharacterId == 0)
            {
                Require(existing != null, 20164079);
                round.Stations.Remove(existing!);
            }
            else
            {
                Require(session.character.Characters.Any(x => x.Id == request.CharacterId), 20164079);
                Require(existing == null, 20164076);
                Require(!round.Stations.Any(x => x.PlayerId == uid && x.CharacterId == request.CharacterId), 20164078);
                Require(round.Stations.Count(x => x.NodeId == root) < (cfg.DeployCharacterMax ?? 0), 20164077);
                round.Stations.Add(new GuildWarStationState { PlayerId = uid, NodeId = root, CharacterId = request.CharacterId });
                string receipt = $"{round.GameThrough}:{root}";
                if (!player.StationReceipts.Contains(receipt))
                {
                    player.StationReceipts.Add(receipt);
                    player.StationCount++;
                }
            }
            response.MyStationedData = MyStations(round, uid);
            response.GuildStationedData = GuildStations(round);
            Publish(mutation);
        });

    private static GuildWarDynamicAction AddDynamicsAction(GuildWarRoundState round, int type, long now, GuildWarNodeState? node = null, GuildWarMonsterState? monster = null, GuildWarReinforcementState? reinforcement = null)
    {
        int sequence = checked(round.DynamicsActions.Count + 1);
        if (sequence >= 100000) throw new InvalidOperationException("Guild War round action sequence exhausted.");
        var action = new GuildWarDynamicAction { ActionId = checked((round.Season * 4 + round.RoundId) * 100000 + sequence), CreateTime = now, ActionType = type, GameThrough = round.GameThrough, GameThroughId = round.GameThroughCfgId, DragonRageValue = round.DragonRage,
            NodeUid = node?.Uid ?? 0, MonsterUid = monster?.Uid ?? 0, ReinforcementUid = reinforcement?.Uid ?? 0,
            NodeSnapshot = node == null ? null : MessagePackSerializer.Serialize(DynamicsNodeData(node)),
            MonsterSnapshot = monster == null ? null : MessagePackSerializer.Serialize(MonsterData(monster)),
            ReinforcementSnapshot = reinforcement == null ? null : MessagePackSerializer.Serialize(ReinforcementData(reinforcement)) };
        round.DynamicsActions.Add(action);
        return action;
    }
    private static GuildWarReinforcementData ReinforcementData(GuildWarReinforcementState x) => new() { Uid = x.Uid, ReinforcementId = x.ReinforcementId, CurHp = x.CurHp, HpMax = x.HpMax, CurNodeId = x.CurNodeId, DeadTime = x.DeadTime, NextMoveTime = x.NextMoveTime, ReadyDoneTime = x.ReadyDoneTime, SupportedPlayerIds = x.SupportedPlayerIds.ToList() };
    private static List<GuildWarMyStationedData> MyStations(GuildWarRoundState round, long uid) => round.Stations.Where(x => x.PlayerId == uid).Select(x => new GuildWarMyStationedData { NodeId = x.NodeId, CharacterId = x.CharacterId }).ToList();
    private static List<GuildWarGuildStationedData> GuildStations(GuildWarRoundState round) => round.Stations.GroupBy(x => x.NodeId).Select(x => new GuildWarGuildStationedData { NodeId = x.Key, Count = x.Count() }).ToList();

    private static RoundWire.NotifyGuildWarActivityDataActivityDataRoundDataNodeData DynamicsNodeData(GuildWarNodeState node)
    {
        var wire = NodeData(node);
        PopulateDynamicsNode(node, wire);
        return wire;
    }

    private static void PopulateDynamicsNode(GuildWarNodeState node, RoundWire.NotifyGuildWarActivityDataActivityDataRoundDataNodeData wire)
    {
        wire.NodeType = DynamicsNode(node.NodeId).Type;
        wire.NextReinforcementsBornTime = node.NextReinforcementsBornTime;
        wire.LastReinforcementId = node.LastReinforcementId;
        wire.LastAttackedReinforcementId = node.LastAttackedReinforcementId;
        wire.LastReinforcementAttackedTime = node.LastReinforcementAttackedTime;
        wire.NextBossTreatMstTime = node.NextBossTreatMstTime;
    }

    internal static void PopulateDynamics(GuildWarRoundState round, RoundWire data)
    {
        data.GameThrough = round.GameThrough;
        data.BossDead = round.BossDead;
        data.DragonRage = round.DragonRage;
        data.FullDragonRageTime = round.FullDragonRageTime;
        data.GameThroughCfgId = round.GameThroughCfgId;
        data.DragonRageCfgId = round.DragonRageCfgId;
        data.FullDragonRageCfgId = round.FullDragonRageCfgId;
        data.LastDragonRageReduceTime = round.LastDragonRageReduceTime;
        data.AttackTimes = round.AttackTimes;
        data.NextAttackTime = round.NextAttackTime;
        data.DefenseDic = new(round.DefenseDic);
        data.LastProtectNodeIds = round.LastProtectNodeIds.ToList();
        data.ReinforcementData = round.Reinforcements.Select(ReinforcementData).ToList();
        data.GuildStationedData = GuildStations(round);
        foreach (var wire in data.NodeData)
        {
            var node = round.Nodes.Single(x => x.Uid == wire.Uid);
            PopulateDynamicsNode(node, wire);
        }
    }
    internal static void PopulateDynamicsPlayer(Guild guild, GuildWarRoundState round, long uid, RoundWire data) => data.MyStationedData = MyStations(round, uid);
    internal static void PopulateDynamicsActions(GuildWarRoundState round, ActivityWire data)
    {
        foreach (var action in round.DynamicsActions)
        {
            var node = action.NodeSnapshot == null ? null : MessagePackSerializer.Deserialize<RoundWire.NotifyGuildWarActivityDataActivityDataRoundDataNodeData>(action.NodeSnapshot);
            var monster = action.MonsterSnapshot == null ? null : MessagePackSerializer.Deserialize<RoundWire.NotifyGuildWarActivityDataActivityDataRoundDataMonsterData>(action.MonsterSnapshot);
            var reinforcement = action.ReinforcementSnapshot == null ? null : MessagePackSerializer.Deserialize<GuildWarReinforcementData>(action.ReinforcementSnapshot);
            data.ActionList.Add(new ActivityWire.NotifyGuildWarActivityDataActivityDataAction { ActionId = action.ActionId, ActionType = action.ActionType, CreateTime = checked((uint)action.CreateTime), RoundId = round.RoundId,
                NodeUid = action.NodeUid, NodeId = node?.NodeId ?? reinforcement?.CurNodeId ?? 0, NodeData = node, MonsterUid = action.MonsterUid, MonsterData = monster,
                ReinforcementUid = action.ReinforcementUid, ReinforcementData = reinforcement, PreNodeIdx = action.PreNodeIdx, NextNodeIdx = action.NextNodeIdx,
                PreNodeId = action.PreNodeIdx, NextNodeId = action.NextNodeIdx, Damage = checked((int)action.Damage), DragonRageValue = action.DragonRageValue, GameThroughId = action.GameThroughId });
        }
    }
}
