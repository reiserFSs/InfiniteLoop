using AscNet.Common;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.theatre3;
using AscNet.Table.V2.share.reward;
using System.Security.Cryptography;

namespace AscNet.GameServer.Handlers;

internal enum Theatre3EffectTrigger { RunStart, NodeCompleted, FightWon, BossWon, ShopPurchase, Reboot, BoxRefresh, EquipmentChanged, ItemAdded }
internal sealed class Theatre3EffectModifiers
{
    public decimal GoldMultiplier { get; set; } = 1;
    public decimal ShopPriceMultiplier { get; set; } = 1;
    public int? RebootCostOverride { get; set; }
    public int RecruitEnergyBonus { get; set; }
    public int CapacityBonus { get; set; }
    public int ShopDiscountSlots { get; set; }
    public int FreeRefreshCount { get; set; }
    public decimal RefreshPriceMultiplier { get; set; } = 1;
    public Dictionary<int, int> WorkshopExtraUses { get; } = [];
}
internal sealed class Theatre3BattleEffects
{
    public Dictionary<int, int> AttributeDelta { get; } = [];
    public List<Theatre3FightEventLevel> LeveledEvents { get; } = [];
}

internal static partial class Theatre3Module
{
    // Documented local policy, NOT a retail server reconstruction. Numeric operands override prose.
    // Source instances stack additively; price factors multiply; revive overrides choose the minimum.
    // All imported items have no ExpireTime column: they last for the run, not an invented duration.
    private sealed record EffectSource(string Key, int GroupId, int Position = 0, int CharacterId = 0);
    private sealed record ActiveSystemEffect(EffectSource Source, Theatre3SystemEffectTable Row);
    private static List<T> EffectRows<T>() where T : ITable => TableReaderV2.Parse<T>();
    private static int EffectConfig(string key) => Convert.ToInt32(EffectRows<Theatre3ConfigTable>().Single(x => x.Key == key).Value, System.Globalization.CultureInfo.InvariantCulture);
    private static int EffectCount(Mutation m, string key) => m.State.EffectCounters.GetValueOrDefault(key);
    private static void CountEffect(Mutation m, string key, int count = 1) => m.State.EffectCounters[key] = checked(EffectCount(m, key) + count);

    private static IEnumerable<EffectSource> EffectSources(Mutation m)
    {
        foreach (var group in m.State.EffectCounters.Where(x => x.Key.StartsWith("eventGroup:", StringComparison.Ordinal)))
            yield return new(group.Key, group.Value);
        foreach (var item in m.Data.Items)
        {
            var row = EffectRows<Theatre3ItemTable>().Single(x => x.Id == item.ItemId);
            if (row.EffectGroupId is > 0) yield return new($"item:{item.Uid}", row.EffectGroupId.Value);
        }
        foreach (var equip in m.Data.Equips)
        {
            var row = EffectRows<Theatre3EquipTable>().Single(x => x.Id == equip.EquipId);
            yield return new($"equip:{equip.EquipId}", equip.QubitActive ? row.QubitEffectGroupId : row.EffectGroupId, equip.Pos);
        }
        foreach (var suit in m.Data.Equips.GroupBy(x => new { x.SuitId, x.Pos }))
        {
            var row = EffectRows<Theatre3EquipSuitTable>().Single(x => x.Id == suit.Key.SuitId);
            int group = suit.All(x => x.QubitActive) ? row.QubitSuitEffectGroupId : row.SuitEffectGroupId;
            foreach (var effect in EffectRows<Theatre3EquipSuitEffectGroupTable>().Where(x => x.GroupId == group && x.ActivateLevel <= suit.Select(e => e.EquipId).Distinct().Count()))
                if (effect.EffectGroupId is > 0) yield return new($"suit:{suit.Key.SuitId}:{suit.Key.Pos}:{effect.Id}", effect.EffectGroupId.Value, suit.Key.Pos);
        }
        foreach (int id in m.Data.UnlockStrengthTree)
        {
            var row = EffectRows<Theatre3StrengthenTreeTable>().Single(x => x.Id == id);
            // Unlock conditions are acquisition gates, not conditions that revoke a purchased talent.
            if (row.EffectGroupId is > 0) yield return new($"talent:{id}", row.EffectGroupId.Value);
        }
        var quantum = EffectRows<Theatre3QubitEffectTable>().Where(x => (x.QubitA ?? 0) <= m.Data.QubitValueA && (x.QubitB ?? 0) <= m.Data.QubitValueB).ToList();
        var covered = quantum.Where(x => x.CoverId is > 0).Select(x => x.CoverId!.Value).ToHashSet();
        foreach (var row in quantum.Where(x => !covered.Contains(x.Id) && x.EffectGroupId is > 0))
            yield return new($"quantum:{row.Id}", row.EffectGroupId!.Value);
        foreach (var character in m.Data.Characters)
            foreach (int ending in character.EndingIds)
            {
                var row = EffectRows<Theatre3CharacterEndingTable>().Single(x => x.Id == ending);
                if (row.EffectGroupId is > 0) yield return new($"ending:{ending}", row.EffectGroupId.Value, CharacterId: character.CharacterId);
            }
    }

    private static IEnumerable<ActiveSystemEffect> SystemEffects(Mutation m)
    {
        foreach (var source in EffectSources(m))
            foreach (int id in EffectRows<Theatre3EffectGroupTable>().Single(x => x.Id == source.GroupId).SystemEvents.Where(x => x > 0))
                yield return new(source, EffectRows<Theatre3SystemEffectTable>().Single(x => x.Id == id));
    }

    internal static Theatre3EffectModifiers GetEffects(Mutation m)
    {
        var result = new Theatre3EffectModifiers();
        foreach (var effect in SystemEffects(m))
        {
            var p = effect.Row.Params;
            switch (effect.Row.Type)
            {
                case 3: result.GoldMultiplier += (decimal)p[0] / 100m; break;
                case 5: result.ShopDiscountSlots += (int)p[0]; break;
                case 13: result.ShopPriceMultiplier *= (decimal)p[0]; break;
                case 16: result.CapacityBonus += (int)p[0]; break;
                case 22: result.RecruitEnergyBonus += (int)p[0]; break;
                case 23: result.RebootCostOverride = Math.Min(result.RebootCostOverride ?? int.MaxValue, (int)p[0]); break;
                case 29: result.WorkshopExtraUses[(int)p[0]] = result.WorkshopExtraUses.GetValueOrDefault((int)p[0]) + (int)p[1]; break;
                case 33: result.FreeRefreshCount += (int)p[0]; break;
                case 34: result.RefreshPriceMultiplier *= 1m - (decimal)p[0]; break;
                case 4: case 8: case 12: case 17: case 18: case 19: case 20: case 21: case 25:
                case 26: case 27: case 28: case 30: case 32: case 35: break; // Triggered below, not passive.
                default: throw new InvalidDataException($"Unsupported Theatre3 SystemEffect {effect.Row.Id}/{effect.Row.Type}.");
            }
        }
        return result;
    }

    internal static void RecomputeCapacities(Mutation m)
    {
        var modifiers = GetEffects(m);
        int energy = checked(EffectConfig("InitCharacterEnergy") + modifiers.RecruitEnergyBonus);
        if (energy != m.Data.MaxEnergy)
        {
            m.Data.MaxEnergy = energy;
            m.Push(new NotifyTheatre3MaxEnergyChange { MaxEnergy = energy });
        }
        int capacity = checked(EffectConfig("InitEquipPosCapacity") + modifiers.CapacityBonus);
        if (m.Data.EquipPos.Any(x => x.Capacity != capacity))
        {
            foreach (var position in m.Data.EquipPos) position.Capacity = capacity;
            m.Push(new NotifyTheatre3EquipPosCapacityChange { EquipPos = Clone(m.Data.EquipPos) });
        }
    }

    internal static void ApplyEffectGroup(Mutation m, int groupId, int identity)
    {
        if (groupId <= 0) return;
        m.State.EffectCounters.TryAdd($"eventGroup:{identity}:{groupId}", groupId);
        ApplyEffectTrigger(m, Theatre3EffectTrigger.EquipmentChanged, identity);
    }

    internal static void AddCoin(Mutation m, int delta) => AddEffectCurrency(m, 96189, delta);
    private static void AddEffectCurrency(Mutation m, int itemId, int delta)
    {
        if (delta == 0) return;
        if (delta < 0) { m.Cost(itemId, checked(-delta)); return; }
        string key = $"theatre3:{m.Session.player.PlayerData.Id}:{m.State.RunId}:coin:{AllocateUid(m)}";
        m.Grant(new RewardGrant(key, [new RewardGoodsTable { Id = itemId, TemplateId = itemId, Count = delta, Params = [] }]));
        if (itemId == 96189 && delta > 0) CountEffect(m, "TotalRecvCoin", delta);
    }

    internal static void ApplyEffectTrigger(Mutation m, Theatre3EffectTrigger trigger, int referenceId = 0, int count = 1)
    {
        if (count <= 0) return;
        // Mutation receipts deduplicate requests; these semantic keys also deduplicate distinct retry packets.
        bool discovery = trigger is Theatre3EffectTrigger.ItemAdded or Theatre3EffectTrigger.EquipmentChanged;
        string triggerKey = $"{m.State.RunId}:{trigger}:{referenceId}:{(trigger == Theatre3EffectTrigger.BoxRefresh ? count : 0)}";
        if (!discovery && !m.State.AppliedEffectTriggers.Add(triggerKey)) return;
        CountEffect(m, $"trigger:{trigger}", count);
        foreach (var suit in m.Data.Equips.GroupBy(x => new { x.SuitId, x.Pos }))
        {
            var row = EffectRows<Theatre3EquipSuitTable>().Single(x => x.Id == suit.Key.SuitId);
            int pieces = suit.Select(x => x.EquipId).Distinct().Count();
            bool complete = EffectRows<Theatre3EquipSuitEffectGroupTable>().Any(x => x.GroupId == row.SuitEffectGroupId && x.ActivateLevel <= pieces);
            if (complete && m.State.AppliedEffectTriggers.Add($"{m.State.RunId}:completeSuit:{suit.Key.SuitId}"))
            {
                m.State.TotalActivatedSuitCount++;
                RecordProgress(m, 104007);
            }
        }
        if (trigger == Theatre3EffectTrigger.ShopPurchase) { m.State.LastLotteryRewards.Clear(); CountEffect(m, "CurBuyCount"); }
        if (trigger is Theatre3EffectTrigger.FightWon or Theatre3EffectTrigger.BossWon)
        {
            string counter = trigger == Theatre3EffectTrigger.BossWon ? "boss" : "fight";
            foreach (int itemId in m.Data.Items.Select(x => x.ItemId).Distinct()) CountEffect(m, $"item:{itemId}:{counter}", count);
            foreach (var equip in m.Data.Equips)
            {
                if (counter == "boss") equip.PassBossFightCount += count; else equip.PassFightCount += count;
            }
            foreach (int suit in m.Data.Equips.Select(x => x.SuitId).Distinct())
                if (m.State.AppliedEffectTriggers.Contains($"{m.State.RunId}:completeSuit:{suit}")) CountEffect(m, $"suit:{suit}:{counter}", count);
            m.Push(new NotifyTheatre3EquipDatas { Equips = Clone(m.Data.Equips) });
        }
        // Snapshot active effects: newly granted items are discovered by their own hook. Once keys are
        // recorded BEFORE side effects, so cycles in item/equip/quantum source graphs terminate.
        foreach (var effect in SystemEffects(m).ToList())
        {
            var row = effect.Row;
            var p = row.Params;
            string sourceKey = $"{m.State.RunId}:{effect.Source.Key}:{row.Id}";
            bool first = m.State.AppliedEffectTriggers.Add($"activate:{sourceKey}");
            if (first) CountEffect(m, $"effect:{row.Id}:activations");
            bool fight = trigger is Theatre3EffectTrigger.FightWon or Theatre3EffectTrigger.BossWon;
            switch (row.Type)
            {
                case 4:
                    if (trigger == Theatre3EffectTrigger.NodeCompleted) AddEffectCurrency(m, (int)p[0], checked((int)p[1] * count));
                    break;
                case 8:
                    if (!m.State.AppliedEffectTriggers.Contains($"grant:{sourceKey}") && m.Data.CurChapterId == (int)p[0])
                    { m.State.AppliedEffectTriggers.Add($"grant:{sourceKey}"); OpenItemBox(m, (int)p[1]); }
                    break;
                case 12: if (first) AddEffectCurrency(m, (int)p[0], (int)p[1]); break;
                case 17:
                    if (fight) CountEffect(m, $"growth:{(int)p[0]}:{(int)p[1]}", count);
                    break;
                case 18: case 19:
                    // Operand1 is suit identity (15), not a cap. Counters begin at source activation.
                    if ((row.Type == 18 && trigger == Theatre3EffectTrigger.FightWon) || (row.Type == 19 && trigger == Theatre3EffectTrigger.BossWon))
                        CountEffect(m, $"record:{(int)p[0]}:{row.Type}", count);
                    break;
                case 20:
                    if (fight)
                    {
                        string key = $"goldGrowth:{(int)p[0]}";
                        CountEffect(m, key, count);
                        int amount = EffectRows<Theatre3GoldTable>().Single(x => x.Id == (int)p[1]).Count;
                        AddCoin(m, checked(amount * EffectCount(m, key)));
                    }
                    break;
                case 21: if (fight) OpenItemBox(m, (int)p[0], count); break; // Operand1=ItemBoxId, one per clear.
                case 25: if (first) OpenEquipBox(m, (int)p[0], (int)p[1]); break;
                case 26:
                    if (first) { m.Data.ChapterSwitch = p[0] != 0; m.Push(new NotifyTheatre3ChapterSwitch { ChapterSwitch = m.Data.ChapterSwitch }); }
                    break;
                case 27: case 35: if (first) m.State.EffectCounters["pendingSwitch"] = 1; break; // Same local action; distinct source IDs.
                case 28: if (first) AddQuantumValue(m, (int)p[0], RandomNumberGenerator.GetInt32((int)p[1], checked((int)p[2] + 1))); break;
                case 30: if (trigger == Theatre3EffectTrigger.ShopPurchase) ApplyShopLottery(m, row); break;
                case 32: if (fight) AddQuantumValue(m, (int)p[0], checked((int)p[1] * count)); break;
                case 3: case 5: case 13: case 16: case 22: case 23: case 29: case 33: case 34: break; // GetEffects owns these.
                default: throw new InvalidDataException($"Unsupported Theatre3 SystemEffect {row.Id}/{row.Type}.");
            }
        }
        TryEffectLineSwitch(m);
        RecomputeCapacities(m);
    }

    private static void AddQuantumValue(Mutation m, int type, int value)
    {
        if (type == 1) m.Data.QubitValueA = Math.Clamp(checked(m.Data.QubitValueA + value), 0, EffectConfig("MaxQubitValueA"));
        else if (type == 2) m.Data.QubitValueB = Math.Clamp(checked(m.Data.QubitValueB + value), 0, EffectConfig("MaxQubitValueB"));
        else throw new InvalidDataException($"Unsupported quantum channel {type}.");
        int level = EffectRows<Theatre3QubitLevelTable>().Where(x => x.Exp <= m.Data.QubitValueA + m.Data.QubitValueB).Select(x => x.Level).DefaultIfEmpty().Max();
        if (level > 0) RecordProgress(m, 104015, level);
        if (level > 0) m.State.QuantumTipUnlocked = true;
        m.Push(new NotifyTheatre3QubitValues { QubitValueA = m.Data.QubitValueA, QubitValueB = m.Data.QubitValueB });
        ApplyEffectTrigger(m, Theatre3EffectTrigger.EquipmentChanged);
    }

    internal static void ActivateQuantumSuit(Mutation m, int suitId)
    {
        var equips = m.Data.Equips.Where(x => x.SuitId == suitId).ToList();
        var suit = EffectRows<Theatre3EquipSuitTable>().Single(x => x.Id == suitId);
        Require(equips.Count > 0 && suit.QubitSuitEffectGroupId > 0, 20203070);
        if (equips.All(x => x.QubitActive)) return;
        foreach (var equip in equips) equip.QubitActive = true;
        RecordProgress(m, 104017);
        m.Push(new NotifyTheatre3EquipDatas { Equips = Clone(equips) });
        ApplyEffectTrigger(m, Theatre3EffectTrigger.EquipmentChanged, suitId);
    }

    private static void TryEffectLineSwitch(Mutation m)
    {
        var chapter = m.Data.CurChapterDb;
        if (EffectCount(m, "pendingSwitch") == 0 || chapter == null || chapter.ConnectChapterId <= 0) return;
        var step = chapter.Steps.LastOrDefault(x => x.Overdue == 0 && x.StepType == 2);
        if (step == null || step.NodeData?.Selected > 0 || step.ConnectNodeData?.Selected > 0) return;
        ApplyPendingLineSwitchAtNodeBoundary(m);
    }

    // Nodes calls this after completing the selected node, before resolving chapter successors.
    // The outgoing step may already be overdue; no old node/progress state is reset.
    internal static void ApplyPendingLineSwitchAtNodeBoundary(Mutation m)
    {
        var chapter = m.Data.CurChapterDb;
        if (EffectCount(m, "pendingSwitch") == 0 || chapter == null || chapter.ConnectChapterId <= 0) return;
        m.Data.CurChapterId = m.Data.CurChapterId == chapter.ChapterId ? chapter.ConnectChapterId : chapter.ChapterId;
        m.State.EffectCounters.Remove("pendingSwitch");
        m.Push(new NotifyTheatre3SwitchLine { ChapterId = m.Data.CurChapterId });
    }

    private static void ApplyShopLottery(Mutation m, Theatre3SystemEffectTable row)
    {
        var p = row.Params;
        // Local 7-operand policy: independent basis-point chances, weighted ItemGroup FK,
        // inclusive gold-loss bounds [p4,p5]; duplicate12345 treated LOCALLY as reserved operands.
        if (p.Count != 7 || p[5] != 12345 || p[6] != 12345) throw new InvalidDataException("Unknown Theatre3 lottery operands.");
        if (RandomNumberGenerator.GetInt32(10000) < p[0])
        {
            var candidates = EffectRows<Theatre3ItemGroupTable>().Where(x => x.GroupId == (int)p[2] && x.Weight > 0
                && (!(x.InitialCondition is > 0) || IsConditionSatisfied(m, x.InitialCondition.Value)) && CanAddItem(m, x.ItemId)).ToList();
            if (candidates.Count > 0)
            {
                int draw = RandomNumberGenerator.GetInt32(candidates.Sum(x => x.Weight));
                var chosen = candidates.First(x => (draw -= x.Weight) < 0);
                AddItem(m, chosen.ItemId);
                m.State.LastLotteryRewards[1] = chosen.ItemId;
                CountEffect(m, "lottery:items");
            }
            else CountEffect(m, "lottery:exhausted");
        }
        if (RandomNumberGenerator.GetInt32(10000) < p[1])
        {
            int amount = (int)Math.Min(m.Balance(96189), RandomNumberGenerator.GetInt32((int)p[3], checked((int)p[4] + 1)));
            AddCoin(m, -amount);
            m.State.LastLotteryRewards[2] = -amount;
            CountEffect(m, "lottery:loss", amount);
        }
    }

    internal static List<int> GetFightEvents(Mutation m, int characterId, int position)
    {
        var events = new HashSet<int>();
        foreach (var source in EffectSources(m).Where(x => (x.Position == 0 || x.Position == position) && (x.CharacterId == 0 || x.CharacterId == characterId)))
            events.UnionWith(EffectRows<Theatre3EffectGroupTable>().Single(x => x.Id == source.GroupId).FightEvents);
        foreach (var equip in m.Data.Equips)
        {
            var row = EffectRows<Theatre3EquipTable>().Single(x => x.Id == equip.EquipId);
            events.UnionWith(equip.QubitActive ? row.QubitGlobalFightEventIds : row.GlobalFightEventIds);
            if (equip.Pos == position) events.UnionWith(row.BaseAttFightEventIds);
        }
        foreach (var suit in m.Data.Equips.GroupBy(x => new { x.SuitId, x.Pos }))
        {
            var row = EffectRows<Theatre3EquipSuitTable>().Single(x => x.Id == suit.Key.SuitId);
            int group = suit.All(x => x.QubitActive) ? row.QubitSuitEffectGroupId : row.SuitEffectGroupId;
            foreach (var effect in EffectRows<Theatre3EquipSuitEffectGroupTable>().Where(x => x.GroupId == group && x.ActivateLevel <= suit.Select(e => e.EquipId).Distinct().Count()))
                events.UnionWith(effect.GlobalFightEventIds);
        }
        events.Remove(0);
        events.ExceptWith(GetBattleEffects(m, characterId, position).LeveledEvents.Select(x => x.FightEventId));
        return events.Order().ToList();
    }

    internal static Theatre3BattleEffects GetBattleEffects(Mutation m, int characterId, int position)
    {
        var result = new Theatre3BattleEffects();
        foreach (var effect in SystemEffects(m).Where(x => x.Row.Type == 17 && (x.Source.Position == 0 || x.Source.Position == position)))
        {
            var p = effect.Row.Params;
            // Channels1/2/3 identify HP/ATK/pet growth, not native attribute IDs. The referenced
            // equipment's existing event is leveled once per recorded clear; no fabricated stats.
            int level = EffectCount(m, $"growth:{(int)p[0]}:{(int)p[1]}");
            if (level == 0) continue;
            var equip = m.Data.Equips.FirstOrDefault(x => x.EquipId == (int)p[0]);
            if (equip == null) continue;
            var row = EffectRows<Theatre3EquipTable>().Single(x => x.Id == equip.EquipId);
            int group = equip.QubitActive ? row.QubitEffectGroupId : row.EffectGroupId;
            foreach (int id in EffectRows<Theatre3EffectGroupTable>().Single(x => x.Id == group).FightEvents.Where(x => x > 0))
                result.LeveledEvents.Add(new() { FightEventId = id, FightEventLevel = level });
        }
        return result;
    }
}
