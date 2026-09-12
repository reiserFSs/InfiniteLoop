using AscNet.Common;
using AscNet.Common.MsgPack;
using AscNet.Table.V2.share.theatre5;
using AscNet.Table.V2.share.theatre5.theatremission;

namespace AscNet.GameServer.Handlers;

internal static partial class Theatre5Module
{
    internal static void InitializeMissions(Mutation m)
    {
        var adventure = Adventure(m);
        adventure.Missioning = null;
        adventure.ChooseMissions.Clear();
        adventure.FreshMissionCounts.Clear();
        adventure.FreshMissionBounty.Clear();
        adventure.FreshMissionCondition.Clear();
        adventure.MissionBattleConditions.Clear();
        adventure.MissionBattleAttributes.Clear();
        adventure.MissionBattleCountedRound = -1;
        adventure.MissionChoiceRound = -1;
        adventure.MissionLevelUpForRound = 0;
    }

    internal static void GenerateMissionChoices(Mutation m)
    {
        var adventure = Adventure(m);
        if (adventure.Missioning != null || adventure.ChooseMissions.Count != 0) return;
        // LOCAL commission policy: one contract per adventure, three distinct offers.
        // The extracted mission tables contain no weight/random-group definitions. Each
        // visible mission and each condition kind therefore has equal weight; unrelated
        // ItemRandomGroup IDs must not be joined to MissionConditionGroup.
        for (int position = 1; position <= 3; position++)
            adventure.ChooseMissions.Add(position, DrawMission(m));
        adventure.MissionChoiceRound = adventure.RoundNum;
    }

    private static Theatre5Mission DrawMission(Mutation m)
    {
        var adventure = Adventure(m);
        var missions = Rows<Theatre5MissionTable>().Where(row => !string.Equals(row.HideInHandBook, "true", StringComparison.OrdinalIgnoreCase)
            && !adventure.FreshMissionBounty.Contains(row.Bounty)).ToList();
        Require(missions.Count > 0, 20285003);
        var source = missions[Random.Shared.Next(missions.Count)];
        Require(source.MissionConditionGroup is >= 1001 and <= 1004, 20285001);
        var bounty = Rows<Theatre5MissionBountyTable>().Where(row => row.Bounty == source.Bounty).OrderBy(row => row.Level).FirstOrDefault();
        Require(bounty != null, 20285008);
        // LOCAL missing group composition: groups 1001/1002 use the current round's
        // five-tier difficulty; groups 1003/1004 start two tiers higher. Preserve all
        // fourteen authored kinds and their exact targets/operands, never a text parser.
        int tier = Math.Clamp(1 + Math.Max(0, adventure.RoundNum - 1) / 3
            + (source.MissionConditionGroup >= 1003 ? 2 : 0), 1, 5);
        var conditions = Rows<Theatre5MissionConditionTable>().GroupBy(row => row.MissionCondition)
            .Select(group => group.OrderBy(row => row.Id).ElementAt(Math.Min(tier, group.Count()) - 1))
            .Where(row => !adventure.FreshMissionCondition.Contains(row.Id)).ToList();
        Require(conditions.Count > 0, 20285015);
        var condition = conditions[Random.Shared.Next(conditions.Count)];
        adventure.FreshMissionBounty.Add(source.Bounty);
        adventure.FreshMissionCondition.Add(condition.Id);
        return new Theatre5Mission
        {
            MissionId = source.Id,
            MissionBounty = new() { Bounty = source.Bounty, BountyLevel = bounty!.Level },
            MissionCondition = new() { ConditionId = condition.Id },
            MissionState = 1
        };
    }

    private static Theatre5AdventureData MissionShop(Mutation m)
    {
        EnsureAvailable(m.Session);
        var adventure = Adventure(m);
        Require(adventure.Status == 4, 20281001);
        return adventure;
    }

    [RequestPacketHandler("Theatre5MissionFreshRequest")]
    public static void Theatre5MissionFreshRequestHandler(Session session, Packet.Request packet) =>
        Handle<Theatre5MissionFreshRequest, Theatre5MissionFreshResponse>(session, packet, (m, request, response) =>
        {
            var adventure = MissionShop(m);
            Require(adventure.Missioning == null, 20285003);
            Require(adventure.ChooseMissions.ContainsKey(request.PositionId), 20285005);
            int count = adventure.FreshMissionCounts.GetValueOrDefault(request.PositionId);
            Require(count < ConfigInt("MissionFreshCnt"), 20285004);
            var mission = DrawMission(m);
            adventure.ChooseMissions[request.PositionId] = mission;
            adventure.FreshMissionCounts[request.PositionId] = checked(count + 1);
            response.FreshMissionCnt = count + 1;
            response.FreshMission = Clone(mission);
        });

    [RequestPacketHandler("Theatre5MissionChooseRequest")]
    public static void Theatre5MissionChooseRequestHandler(Session session, Packet.Request packet) =>
        Handle<Theatre5MissionChooseRequest, Theatre5MissionChooseResponse>(session, packet, (m, request, response) =>
        {
            var adventure = MissionShop(m);
            Require(adventure.Missioning == null, 20285006);
            Require(adventure.ChooseMissions.TryGetValue(request.PositionId, out var mission), 20285006);
            adventure.Missioning = mission;
            adventure.ChooseMissions.Clear();
            adventure.FreshMissionCounts.Clear();
            adventure.FreshMissionBounty.Clear();
            adventure.FreshMissionCondition.Clear();
            var history = m.Data.PvpType == 1 ? m.Data.PvpChooseMissionBounty : m.Data.PveChooseMissionBounty;
            if (!history.Contains(mission!.MissionBounty.Bounty)) history.Add(mission.MissionBounty.Bounty);
            UpdateMissionProgress(m, "ChooseMission");
            response.ChooseMission = Clone(mission!);
        });

    private static Theatre5MissionBountyTable MissionBountyRow(Theatre5Mission mission)
    {
        var row = Rows<Theatre5MissionBountyTable>().SingleOrDefault(row => row.Bounty == mission.MissionBounty.Bounty
            && row.Level == mission.MissionBounty.BountyLevel);
        Require(row != null, 20285008);
        return row!;
    }

    [RequestPacketHandler("Theatre5MissionLevelUpRequest")]
    public static void Theatre5MissionLevelUpRequestHandler(Session session, Packet.Request packet) =>
        Handle<Theatre5MissionLevelUpRequest, Theatre5MissionLevelUpResponse>(session, packet, (m, request, response) =>
        {
            var adventure = MissionShop(m);
            var mission = adventure.Missioning;
            Require(mission != null, 20285007);
            Require(mission!.MissionState != 3, 20285011);
            Require(request.CurLevel == mission.MissionBounty.BountyLevel, 20285008);
            Require(adventure.MissionLevelUpForRound < ConfigInt("MissionLevelUpForRound"), 20285014);
            var current = MissionBountyRow(mission);
            var next = Rows<Theatre5MissionBountyTable>().SingleOrDefault(row => row.Bounty == current.Bounty && row.Level == current.Level + 1);
            Require(next != null && current.Cost.HasValue, 20285009);
            int cost = current.Cost!.Value;
            Require(cost >= 0 && adventure.GoldNum >= cost, 20285010);
            // Bounty upgrades explicitly do not advance the spend-gold commission.
            if (cost > 0) SpendItems(m, ConfigInt("ChapterCoin"), cost);
            mission.MissionBounty.BountyLevel = next!.Level;
            adventure.MissionLevelUpForRound++;
            response.CurLevel = next.Level;
            response.CostGoldNum = cost;
        });

    [RequestPacketHandler("Theatre5MissionRewardRequest")]
    public static void Theatre5MissionRewardRequestHandler(Session session, Packet.Request packet) =>
        Handle<Theatre5MissionRewardRequest, Theatre5MissionRewardResponse>(session, packet, (m, request, response) =>
        {
            var adventure = MissionShop(m);
            var mission = adventure.Missioning;
            Require(mission != null, 20285007);
            Require(mission!.MissionState != 3, 20285011);
            Require(mission.MissionState == 2, 20285012);
            var bounty = MissionBountyRow(mission);
            Require(request.ChooseItemId > 0 && bounty.BountyItem.Contains(request.ChooseItemId), 20285016);
            AddItems(m, request.ChooseItemId);
            mission.MissionRelicId = request.ChooseItemId;
            mission.MissionState = 3;
            if (ItemConfig(request.ChooseItemId).Type == 7)
                TriggerEffects(m, "ChooseRelic", request.ChooseItemId);
            response.MissionState = 3;
            m.Push(new NotifyTheatre5BagDataUpdate { Status = adventure.Status, GoldNum = adventure.GoldNum, BagData = Clone(adventure.BagData) });
            m.Push(new NotifyTheatre5Mission { Mission = Clone(mission) });
        });

    // LOCAL trigger decoding: 1=accept, 2=buy, 3=refresh, 4=slot unlock,
    // 5=battle; subtype 1=equip, 2=rune triggers, 3=attributes, 4=damage,
    // 5=healing/shield, 6=duration, 7=win, 8=winning duration. AddType
    // 1 counts qualifying events, 2 sums event value, 3 snapshots live state.
    // BattleRune/BattleAttribute are accepted, checked report operands, not
    // independent client RPCs. Rune records use item IDs; selectors 6..12 are
    // authored Theatre5Item.Tags (see Theatre5ItemTag), not record keys.
    // LOCAL aggregation sums recorded activations across runes sharing that tag.
    // RoundEnd evaluates the resulting predicates once per settled round.
    internal static void UpdateMissionProgress(Mutation m, string trigger, int value = 1, int itemId = 0)
    {
        var adventure = Adventure(m);
        var mission = adventure.Missioning;
        if (mission == null || mission.MissionState != 1) return;
        // Native SetMaxRate preserves signed debuffed attributes; only counters are nonnegative.
        Require(trigger == "BattleAttribute" || value >= 0, 20285015);
        var condition = Rows<Theatre5MissionConditionTable>().SingleOrDefault(row => row.Id == mission.MissionCondition.ConditionId);
        Require(condition != null, 20285015);
        var definition = Rows<Theatre5MissionTriggerConditionTable>().SingleOrDefault(row => row.Id == condition!.TriggerCondition);
        Require(definition != null, 20285015);
        if (trigger == "BeforeBattle")
        {
            adventure.MissionBattleConditions.Clear();
            adventure.MissionBattleAttributes.Clear();
        }
        if (trigger == "BattleRune")
        {
            Require(ItemConfig(itemId).Type == 2, 20285016);
            adventure.MissionBattleConditions[itemId] = value;
            return;
        }
        if (trigger == "BattleAttribute") { adventure.MissionBattleAttributes[itemId] = value; return; }
        int kind = condition!.MissionCondition;
        int triggerType = 0, subtype = 0, amount = value;
        bool qualifies = true;
        switch (kind)
        {
            case 10001:
                if (trigger == "SpendGold") { triggerType = 2; subtype = 1; }
                break;
            case 10002:
                if (trigger == "BeforeBattle")
                {
                    triggerType = 5; subtype = 1;
                    qualifies = MissionCompare(condition, key => adventure.BagData.RuneDict.Values.Count(item =>
                        Rows<Theatre5ItemTable>().Any(row => row.Id == item.ItemId && row.Quality == key + 1)));
                }
                break;
            case 10003:
            case 10004:
                if (trigger == "BuyItem")
                {
                    triggerType = 2; subtype = 2;
                    var item = Rows<Theatre5ItemTable>().SingleOrDefault(row => row.Id == itemId);
                    Require(item != null, 20285016);
                    qualifies = MissionCompare(condition, _ => item!.Type);
                }
                break;
            case 10005:
                if (trigger == "ChooseMission") { triggerType = 1; subtype = 1; }
                if (trigger == "UnlockGrid" && value == 2) { triggerType = 4; subtype = 2; }
                amount = adventure.BagData.RuneGridsNum;
                break;
            case 10006:
                if (trigger == "BattleWin") { triggerType = 5; subtype = 7; amount = 1; }
                break;
            case 10007:
            case 10008:
                if (trigger == "RoundEnd")
                {
                    triggerType = 5; subtype = 3;
                    qualifies = MissionCompare(condition, key => adventure.MissionBattleAttributes.GetValueOrDefault(key));
                }
                break;
            case 10009:
                if (trigger == "BattleDamage") { triggerType = 5; subtype = 4; }
                break;
            case 10010:
                if (trigger == "BattleHealShield") { triggerType = 5; subtype = 5; }
                break;
            case 10011:
            case 10012:
            case 10013:
                if (trigger == "RoundEnd")
                {
                    triggerType = 5; subtype = 2;
                    qualifies = MissionCompare(condition, key => (int)Math.Min(int.MaxValue,
                        adventure.MissionBattleConditions.Where(pair => ItemConfig(pair.Key).Tags.Contains(key))
                            .Sum(pair => (long)pair.Value)));
                }
                break;
            case 10014:
                if (trigger == "BattleWinTime")
                {
                    triggerType = 5; subtype = 8;
                    qualifies = MissionCompare(condition, _ => value);
                }
                break;
            default: throw new InvalidDataException($"Unknown Theatre5 mission kind {kind}.");
        }
        if (triggerType == 0) return;
        if (trigger == "RoundEnd")
        {
            if (adventure.MissionBattleCountedRound == adventure.RoundNum) return;
            adventure.MissionBattleCountedRound = adventure.RoundNum;
        }
        if (!qualifies) return;
        int index = definition!.TriggerType.FindIndex(type => type == triggerType);
        Require(index >= 0 && index < definition.SubTriggerType.Count && definition.SubTriggerType[index] == subtype
            && index < definition.AddType.Count, 20285015);
        int previous = mission.MissionCondition.ConditionCounter;
        long counter = definition.AddType[index] switch
        {
            1 => (long)previous + 1,
            2 => (long)previous + amount,
            3 => amount,
            _ => throw new InvalidDataException("Unknown Theatre5 mission accumulation type.")
        };
        mission.MissionCondition.ConditionCounter = (int)Math.Min(condition.ConditionTarget, counter);
        if (counter >= condition.ConditionTarget) mission.MissionState = 2;
        if (previous != mission.MissionCondition.ConditionCounter)
            m.Push(new NotifyTheatre5Mission { Mission = Clone(mission) });
    }

    private static bool MissionCompare(Theatre5MissionConditionTable condition, Func<int, int> operand)
    {
        bool and = string.Equals(condition.IsConditionAnd, "true", StringComparison.OrdinalIgnoreCase);
        bool result = and;
        Require(condition.ConditionParams.Count == condition.ConditionCompares.Count
            && condition.ConditionParams.Count == condition.ConditionCompareValues.Count, 20285015);
        for (int i = 0; i < condition.ConditionParams.Count; i++)
        {
            int actual = operand(condition.ConditionParams[i]);
            int target = condition.ConditionCompareValues[i];
            bool match = condition.ConditionCompares[i] switch
            {
                1 => actual == target,
                2 => actual >= target,
                3 => actual <= target,
                _ => throw new InvalidDataException("Unknown Theatre5 mission comparison.")
            };
            result = and ? result && match : result || match;
        }
        return result;
    }
}
