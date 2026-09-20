using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.theatre6;

namespace AscNet.GameServer.Handlers;

/// <summary>
/// Theatre6 in-run economy: currency, materials, the battle shop, the skill board (placement, merge,
/// overflow queue, star-up upgrades) and the in-run pool policy used to freeze authored offers.
/// Buff effect/trigger semantics live in <c>Theatre6Module.Effects.cs</c>.
/// </summary>
internal static partial class Theatre6Module
{
    #region Enums, config keys and table index

    private const int SlotTypeSpecial = 1;
    private const int SlotTypeActive = 2;
    private const int SlotTypeInsert = 3;
    private const int SlotTypeBag = 4;

    private const int SkillTypeActive = 1;
    private const int SkillTypeParry = 2;
    private const int SkillTypeOverClock = 3;
    private const int SkillTypeInsert = 4;

    private const int RewardTypeGoods = 1;
    private const int RewardTypeSan = 2;
    private const int RewardTypeCoin = 3;
    private const int RewardTypeBuffPool = 4;
    private const int RewardTypeSkillPool = 5;
    private const int RewardTypeHp = 6;
    private const int RewardTypeFight = 7;
    private const int RewardTypeAvg = 100;

    private const int ShopGoodTypeSkill = 1;
    private const int ShopGoodTypeAttrPack = 2;
    // RoomBattleShop (3) is declared by the room engine in Theatre6Module.Progression.cs; the partial
    // class shares it, so there is exactly one definition of the authored RoomType value.

    private const int ErrorNotOpen = 1;
    private const int ErrorSkillNotExist = 20423070;
    private const int ErrorSkillMove = 20423071;
    private const int ErrorSkillSwap = 20423072;
    private const int ErrorNotInShop = 20423073;
    private const int ErrorShopFreshCount = 20423074;
    private const int ErrorShopGoodSold = 20423079;
    private const int ErrorNoSkillSpace = 20423081;
    private const int ErrorOverflowEmpty = 20423084;
    private const int ErrorOverflowUnresolved = 20423085;
    private const int ErrorSkillMax = 20423089;
    private const int ErrorBuffUpgradeLimit = 20423090;
    private const int ErrorAllGoodsLocked = 20423100;
    private const int ErrorBuySanCount = 20423101;
    private const int ErrorBuySanLimit = 20423102;
    private const int ErrorBuySanFull = 20423103;
    private const int ErrorBuySanConfig = 20423104;
    private const int ErrorSpecialRelicMaxSkill = 20423107;
    private const int ErrorGoldNotEnough = 20423018;

    /// <summary>Table accessors. Missing required rows throw instead of resolving to a silent zero.</summary>
    private static class EcoTables
    {
        internal static readonly Lazy<Dictionary<int, Theatre6StageBuffTable>> Buffs = new(() => Index(TableReaderV2.Parse<Theatre6StageBuffTable>(), row => row.Id));
        internal static readonly Lazy<Dictionary<int, Theatre6SkillTable>> Skills = new(() => Index(TableReaderV2.Parse<Theatre6SkillTable>(), row => row.Id));
        internal static readonly Lazy<Dictionary<int, Theatre6AttrPackTable>> AttrPacks = new(() => Index(TableReaderV2.Parse<Theatre6AttrPackTable>(), row => row.Id));
        internal static readonly Lazy<Dictionary<int, Theatre6AttrTable>> Attrs = new(() => Index(TableReaderV2.Parse<Theatre6AttrTable>(), row => row.Id));
        internal static readonly Lazy<Dictionary<int, Theatre6StageGoodsTable>> Goods = new(() => Index(TableReaderV2.Parse<Theatre6StageGoodsTable>(), row => row.Id));
        internal static readonly Lazy<Dictionary<int, Theatre6StageShopTable>> Shops = new(() => Index(TableReaderV2.Parse<Theatre6StageShopTable>(), row => row.Id));
        internal static readonly Lazy<Dictionary<int, Theatre6StageTable>> Stages = new(() => Index(TableReaderV2.Parse<Theatre6StageTable>(), row => row.Id));
        internal static readonly Lazy<Dictionary<int, Theatre6CharacterTable>> Characters = new(() => Index(TableReaderV2.Parse<Theatre6CharacterTable>(), row => row.Id));
        internal static readonly Lazy<Dictionary<int, Theatre6RandomPoolTable>> RandomPools = new(() => Index(TableReaderV2.Parse<Theatre6RandomPoolTable>(), row => row.Id));
        internal static readonly Lazy<Dictionary<int, Theatre6SkillPoolTable>> SkillPools = new(() => Index(TableReaderV2.Parse<Theatre6SkillPoolTable>(), row => row.Id));
        internal static readonly Lazy<Dictionary<int, Theatre6AttrPackPoolTable>> AttrPackPools = new(() => Index(TableReaderV2.Parse<Theatre6AttrPackPoolTable>(), row => row.Id));
        internal static readonly Lazy<Dictionary<int, Theatre6StageTaskTable>> Tasks = new(() => Index(TableReaderV2.Parse<Theatre6StageTaskTable>(), row => row.Id));
        internal static readonly Lazy<List<Theatre6SkillTable>> SkillCatalog = new(() => TableReaderV2.Parse<Theatre6SkillTable>());
        internal static readonly Lazy<List<Theatre6AttrPackTable>> AttrPackCatalog = new(() => TableReaderV2.Parse<Theatre6AttrPackTable>());
        internal static readonly Lazy<List<Theatre6TalentTable>> Talents = new(() => TableReaderV2.Parse<Theatre6TalentTable>());
        internal static readonly Lazy<Dictionary<string, Theatre6ConfigTable>> Config = new(() =>
            TableReaderV2.Parse<Theatre6ConfigTable>().GroupBy(row => row.Key).ToDictionary(group => group.Key, group => group.First()));

        internal static T Get<T>(Dictionary<int, T> map, int id, string table) where T : class
        {
            if (!map.TryGetValue(id, out T? row) || row is null)
                throw new InvalidDataException($"Missing {table} row {id}.");
            return row;
        }

        internal static Theatre6StageBuffTable Buff(int id) => Get(Buffs.Value, id, nameof(Theatre6StageBuffTable));
        internal static Theatre6SkillTable Skill(int id) => Get(Skills.Value, id, nameof(Theatre6SkillTable));
        internal static Theatre6AttrPackTable AttrPack(int id) => Get(AttrPacks.Value, id, nameof(Theatre6AttrPackTable));
        internal static Theatre6AttrTable Attr(int id) => Get(Attrs.Value, id, nameof(Theatre6AttrTable));
        internal static Theatre6StageShopTable Shop(int id) => Get(Shops.Value, id, nameof(Theatre6StageShopTable));
        internal static Theatre6StageTable Stage(int id) => Get(Stages.Value, id, nameof(Theatre6StageTable));
        internal static Theatre6CharacterTable Character(int id) => Get(Characters.Value, id, nameof(Theatre6CharacterTable));
        internal static Theatre6RandomPoolTable RandomPool(int id) => Get(RandomPools.Value, id, nameof(Theatre6RandomPoolTable));

        private static Dictionary<int, T> Index<T>(List<T> rows, Func<T, int> key) =>
            rows.GroupBy(key).ToDictionary(group => group.Key, group => group.First());
    }

    /// <summary>Integer config value of an authored Theatre6 key. A missing key or index is a data error.</summary>
    internal static int EconCfg(string key, int index = 0)
    {
        List<int> values = EconCfgValues(key);
        return index < values.Count ? values[index] : throw new InvalidDataException($"Theatre6Config {key} has no value at index {index}.");
    }

    internal static List<int> EconCfgValues(string key) =>
        EcoTables.Config.Value.TryGetValue(key, out Theatre6ConfigTable? row)
            ? row.Values
            : throw new InvalidDataException($"Missing Theatre6Config key {key}.");

    #endregion

    #region Skill board primitives

    internal static int SkillSlotLimit(int slotType) => slotType switch
    {
        SlotTypeSpecial => 1,
        SlotTypeActive => EconCfg("ActiveSkillSlotLimit"),
        SlotTypeInsert => EconCfg("InsertSkillSlotLimit"),
        SlotTypeBag => EconCfg("SkillBagSlotLimit"),
        _ => throw new InvalidDataException($"Unknown Theatre6 skill slot type {slotType}.")
    };

    internal static int SkillSlotCapacity(int slotType) => slotType switch
    {
        SlotTypeSpecial => 1,
        SlotTypeActive => EconCfg("ActiveSkillSlotInitCount"),
        SlotTypeInsert => EconCfg("InsertSkillSlotInitCount"),
        SlotTypeBag => EconCfg("SkillBagSlotInitCount"),
        _ => throw new InvalidDataException($"Unknown Theatre6 skill slot type {slotType}.")
    };

    /// <summary>Authored placement priority (config "…SkillSlotSort") for a skill type.</summary>
    internal static List<int> SkillSlotSort(int skillType) => EconCfgValues(skillType switch
    {
        SkillTypeActive => "ActiveSkillSlotSort",
        SkillTypeParry => "ClashSkillSlotSort",
        SkillTypeOverClock => "UltraCalcSkillSlotSort",
        SkillTypeInsert => "InsertSkillSlotSort",
        _ => throw new InvalidDataException($"Unknown Theatre6 skill type {skillType}.")
    });

    /// <summary>Slot types a skill may be installed into, mirroring XTheatre6SubSkillModel.</summary>
    internal static List<int> SkillInstallSlots(int skillType) => skillType switch
    {
        SkillTypeActive => new List<int> { SlotTypeActive },
        SkillTypeParry => new List<int> { SlotTypeSpecial },
        SkillTypeOverClock => new List<int> { SlotTypeSpecial },
        SkillTypeInsert => new List<int> { SlotTypeInsert, SlotTypeSpecial },
        _ => throw new InvalidDataException($"Unknown Theatre6 skill type {skillType}.")
    };

    internal static Theatre6SkillState? FindSkill(Theatre6RunState run, int skillId) =>
        skillId > 0 ? run.File.Skills.FirstOrDefault(skill => skill.SkillId == skillId) : null;

    internal static Theatre6SkillState? FindSkillAt(Theatre6RunState run, int slotType, int position) =>
        run.File.Skills.FirstOrDefault(skill => skill.SlotType == slotType && skill.Position == position);

    internal static bool SlotIsFull(Theatre6RunState run, int slotType) =>
        run.File.Skills.Count(skill => skill.SlotType == slotType) >= SkillSlotCapacity(slotType);

    internal static int FirstEmptyPosition(Theatre6RunState run, int slotType)
    {
        int limit = SkillSlotLimit(slotType);
        for (int position = 1; position <= limit; position++)
        {
            if (FindSkillAt(run, slotType, position) is null)
                return position;
        }

        return 0;
    }

    internal static int CountSkillsByKey(Theatre6RunState run, int skillKey) =>
        run.File.Skills.Count(skill => EcoTables.Skill(skill.SkillId).SkillKey == skillKey);

    /// <summary>Next skill id of the same family (SkillKey) at level + 1, or 0 when maxed.</summary>
    internal static int NextLevelSkillId(int skillId)
    {
        Theatre6SkillTable current = EcoTables.Skill(skillId);
        return EcoTables.SkillCatalog.Value
            .FirstOrDefault(candidate => candidate.SkillKey == current.SkillKey && candidate.Level == current.Level + 1)?.Id ?? 0;
    }

    private static Theatre6SkillData CloneSkill(Theatre6SkillState skill) =>
        new() { SkillId = skill.SkillId, SlotType = skill.SlotType, Position = skill.Position };

    /// <summary>
    /// Grants a skill to the run. Same-family and same-level copies merge one level up (the client
    /// documents the shop flow as "sell the old skill, then add the new one", i.e. 2048-like merging).
    /// Otherwise the skill is placed by the authored slot priority; a full board pushes it to the
    /// overflow queue that blocks further progression until sold.
    /// </summary>
    internal static Theatre6SkillUpdate GrantSkill(Mutation m, Theatre6RunState run, int skillId, bool pushUpdate)
    {
        Theatre6SkillTable row = EcoTables.Skill(skillId);
        Theatre6SkillUpdate update = new();

        // 1) Merge with an existing copy of the same skill id. Equipped copies are preferred so the
        //    upgrade lands on the board instead of the bag.
        foreach (int slotType in new[] { SlotTypeSpecial, SlotTypeActive, SlotTypeInsert, SlotTypeBag })
        {
            foreach (Theatre6SkillState existing in run.File.Skills
                .Where(skill => skill.SlotType == slotType && skill.SkillId == skillId).ToList())
            {
                int upgraded = NextLevelSkillId(existing.SkillId);
                if (upgraded <= 0)
                    continue;

                existing.SkillId = upgraded;
                update.ReplaceSkills ??= new List<Theatre6SkillData>();
                update.ReplaceSkills.Add(CloneSkill(existing));
                FinishSkillUpdate(m, run, update, pushUpdate);
                return update;
            }
        }

        // 2) Free position by authored priority. The authored sort list already encodes legality
        //    (equip slots first, Bag last), so no install-slot filter is applied here — the client's
        //    own GetEmptyPositionsByCfg iterates the same list including the Bag fallback. Equipping
        //    additionally requires that no other equipped slot already holds this skill family.
        foreach (int slotType in SkillSlotSort(row.Type))
        {
            if (SlotIsFull(run, slotType))
                continue;
            int position = FirstEmptyPosition(run, slotType);
            if (position <= 0)
                continue;
            if (slotType != SlotTypeBag && EquippedFamilyConflicts(run, row.SkillKey, ignoredSkillId: 0, slotType, position))
                continue;

            Theatre6SkillState placed = new() { SkillId = skillId, SlotType = slotType, Position = position };
            run.File.Skills.Add(placed);
            update.AddSkill = CloneSkill(placed);
            FinishSkillUpdate(m, run, update, pushUpdate);
            return update;
        }

        // 3) The board is full: the skill is queued and must be sold before the run continues. The
        //    client's overflow queue keeps one entry per occurrence and its sale pays per entry, so
        //    repeated overflow of the same skill must append rather than deduplicate.
        run.SkillOverQueue.Add(skillId);
        update.FullEnQueueSkill = skillId;
        FinishSkillUpdate(m, run, update, pushUpdate);
        return update;
    }

    private static void FinishSkillUpdate(Mutation m, Theatre6RunState run, Theatre6SkillUpdate update, bool pushSkillUpdate)
    {
        RecalculateAttrs(m, run, pushChanges: false);
        // Equipped skills feed the score, so it is always republished when it changes; only the skill
        // delta push is suppressed for responses that already carry the delta themselves.
        RecalculateScore(m, run);
        if (pushSkillUpdate)
            m.Push(new NotifyTheatre6AddSkill { SkillUpdates = new List<Theatre6SkillUpdate> { update } });
    }

    /// <summary>Skills that are currently upgradable under authored star-up limits.</summary>
    internal static bool HasUpgradableSkill(Theatre6RunState run, int levelLimit, int qualityLimit)
    {
        foreach (Theatre6SkillState skill in run.File.Skills)
        {
            if (NextLevelSkillId(skill.SkillId) <= 0)
                continue;
            Theatre6SkillTable row = EcoTables.Skill(skill.SkillId);
            if (levelLimit > 0 && row.Level >= levelLimit)
                continue;
            if (qualityLimit > 0 && row.Quality > qualityLimit)
                continue;
            return true;
        }

        return false;
    }

    #endregion

    #region Currency and materials

    internal static void AddGold(Mutation m, Theatre6RunState run, int amount)
    {
        if (amount == 0)
            return;
        Require(run.GoldAmount + (long)amount >= 0, ErrorGoldNotEnough);
        run.GoldAmount = checked(run.GoldAmount + amount);
        m.Push(new NotifyTheatre6GoldChange { GoldAmount = run.GoldAmount, GoldChange = amount });
    }

    /// <summary>
    /// Adds materials. Authored "Echo" buffs (stage buff effect 9) scale gains by per-mille for the
    /// matching goods type. Mission surplus conversion into Coins is the task engine's authored rule
    /// (Config TaskOverflowGoodFactor) and is deliberately not duplicated here.
    /// </summary>
    internal static int AddGoods(Mutation m, Theatre6RunState run, int goodsId, int amount)
    {
        Require(goodsId > 0, ErrorNotOpen);
        if (amount == 0)
            return 0;
        EcoTables.Get(EcoTables.Goods.Value, goodsId, nameof(Theatre6StageGoodsTable));
        Theatre6GoodsData? entry = run.Goods.GetValueOrDefault(goodsId);
        int before = entry?.Amount ?? 0;
        int after = amount > 0
            ? checked(before + ApplyGoodsGainModifiers(run, goodsId, amount))
            : Math.Max(0, before + amount);
        Theatre6GoodsData record = entry ?? new Theatre6GoodsData { GoodsId = goodsId };
        record.GoodsId = goodsId;
        record.Amount = after;
        run.Goods[goodsId] = record;
        int delta = after - before;
        m.Push(new NotifyTheatre6GoodsChange
        {
            GoodsList = new List<Theatre6GoodsData> { record }
        });
        if (delta > 0)
            TriggerEffects(m, run, triggerType: 2, amount: delta, parameter: goodsId);
        return delta;
    }

    /// <summary>Per-mille gain modifier from live buffs that carry effect 9 for this goods id.</summary>
    private static int ApplyGoodsGainModifiers(Theatre6RunState run, int goodsId, int amount)
    {
        int perMille = 0;
        foreach (Theatre6LiveBuffState buff in run.Buffs.Values)
        {
            Theatre6StageBuffTable row = EcoTables.Buff(buff.BuffId);
            if (row.BuffEffectType != 9)
                continue;
            int scopedGoods = row.BuffTriggerParams is { Count: >= 2 } triggerParams ? triggerParams[1] : 0;
            if (scopedGoods != 0 && scopedGoods != goodsId)
                continue;
            perMille += row.BuffEffectParams is { Count: > 0 } effectParams ? effectParams[0] : 0;
        }

        if (perMille <= 0)
            return amount;
        return (int)Math.Min(int.MaxValue, (long)amount * (1000 + perMille) / 1000);
    }

    #endregion

    #region In-run pools (AscNet policy tables)

    /// <summary>
    /// Rolls a concrete skill for a Theatre6RandomPool entry. Quality/level candidates come from the
    /// authored <c>Theatre6Skill</c> catalog; the filter/weight row comes from the AscNet policy
    /// <c>Theatre6SkillPool.tsv</c> (the shipped client does not contain the retail composition).
    /// <paramref name="qualityHint"/> is the caller's authored quality restriction (0 = pool default).
    /// </summary>
    internal static int RollSkillPool(Mutation m, Theatre6RunState run, int poolId, int qualityHint = 0)
    {
        Theatre6RandomPoolTable pool = EcoTables.RandomPool(poolId);
        Require(pool.Type == 1, ErrorNotOpen);
        Theatre6SkillPoolTable policy = EcoTables.Get(EcoTables.SkillPools.Value, poolId, nameof(Theatre6SkillPoolTable));
        List<int> levels = Positive(policy.NeedLevels);
        List<int> qualities = Positive(policy.NeedQualitys);
        if (qualityHint > 0)
        {
            List<int> intersected = qualities.Where(quality => quality == qualityHint).ToList();
            qualities = intersected.Count > 0 ? intersected : new List<int> { qualityHint };
        }
        List<int> appointed = Positive(policy.AppointSkillId);

        List<(int Id, long Weight)> candidates = new();
        foreach (Theatre6SkillTable skill in EcoTables.SkillCatalog.Value)
        {
            if (skill.IsOutPool == 1)
                continue;
            if (skill.Character is > 0 && skill.Character != run.File.CharacterId)
                continue;
            if (appointed.Count > 0)
            {
                if (!appointed.Contains(skill.Id))
                    continue;
            }
            else
            {
                if (levels.Count > 0 && !levels.Contains(skill.Level))
                    continue;
                if (qualities.Count > 0 && !qualities.Contains(skill.Quality))
                    continue;
            }

            long weight = WeightFor(qualities, policy.QualityWeights, skill.Quality)
                * WeightFor(levels, policy.LevelWeights, skill.Level)
                * TagBonus(run, policy.NeedTags, policy.TagWeights, policy.SameTagWeight, policy.MaxSameTagWeight, skill.BuildTags);
            if (weight > 0)
                candidates.Add((skill.Id, weight));
        }

        Require(candidates.Count > 0, ErrorNotOpen);
        return candidates[RollWeighted(m, run, candidates)].Id;
    }

    /// <summary>Rolls a concrete relic (attr pack) for a Theatre6RandomPool entry.</summary>
    internal static int RollAttrPackPool(Mutation m, Theatre6RunState run, int poolId, int qualityHint = 0)
    {
        Theatre6RandomPoolTable pool = EcoTables.RandomPool(poolId);
        Require(pool.Type == 2, ErrorNotOpen);
        Theatre6AttrPackPoolTable policy = EcoTables.Get(EcoTables.AttrPackPools.Value, poolId, nameof(Theatre6AttrPackPoolTable));
        List<int> qualities = Positive(policy.NeedQualitys);
        if (qualityHint > 0)
        {
            List<int> intersected = qualities.Where(quality => quality == qualityHint).ToList();
            qualities = intersected.Count > 0 ? intersected : new List<int> { qualityHint };
        }
        List<int> appointed = Positive(policy.AppointAttrPackId > 0 ? new List<int> { policy.AppointAttrPackId } : new List<int>());

        List<(int Id, long Weight)> candidates = new();
        foreach (Theatre6AttrPackTable pack in EcoTables.AttrPackCatalog.Value)
        {
            if (pack.IsOutPool == 1)
                continue;
            if (pack.Character is > 0 && pack.Character != run.File.CharacterId)
                continue;
            // The authored LimitCount is the acquisition cap: offering a relic the run already holds
            // at its cap produces an unusable offer (the acquisition guard refuses it), so capped
            // candidates leave the pool. Weights and every other filter are untouched.
            if (pack.LimitCount is int limit and > 0
                && run.File.AttrPacks.FirstOrDefault(owned => owned.PackId == pack.Id)?.Num >= limit)
                continue;
            if (appointed.Count > 0)
            {
                if (!appointed.Contains(pack.Id))
                    continue;
            }
            else if (qualities.Count > 0 && !qualities.Contains(pack.Quality))
            {
                continue;
            }

            long weight = WeightFor(qualities, policy.QualityWeights, pack.Quality)
                * TagBonus(run, policy.NeedTags, policy.TagWeights, policy.SameTagWeight, policy.MaxSameTagWeight, pack.BuildTags);
            if (policy.AppointAttrPackWeight > 0 && appointed.Contains(pack.Id))
                weight *= policy.AppointAttrPackWeight;
            if (weight > 0)
                candidates.Add((pack.Id, weight));
        }

        Require(candidates.Count > 0, ErrorNotOpen);
        return candidates[RollWeighted(m, run, candidates)].Id;
    }

    private static List<int> Positive(List<int> values) =>
        values.Where(value => value > 0).Distinct().ToList();

    /// <summary>Authored weight of a quality/level entry; unlisted values are ineligible when a filter exists.</summary>
    private static long WeightFor(List<int> filter, List<int> weights, int value)
    {
        if (filter.Count == 0)
            return 1;
        int index = filter.IndexOf(value);
        if (index < 0)
            return 0;
        long weight = index < weights.Count ? weights[index] : 1;
        return Math.Max(0, weight);
    }

    /// <summary>Build-tag affinity: authored tag weights plus a capped "same tag as current build" bonus.</summary>
    private static long TagBonus(Theatre6RunState run, List<int> needTags, List<int> tagWeights, int sameTagWeight, int maxSameTagWeight, List<int> candidateTags)
    {
        long bonus = 1;
        if (needTags.Count > 0 && candidateTags.Count > 0)
        {
            for (int index = 0; index < needTags.Count; index++)
            {
                if (needTags[index] > 0 && candidateTags.Contains(needTags[index]))
                    bonus += Math.Max(0, index < tagWeights.Count ? tagWeights[index] : 0);
            }
        }

        if (sameTagWeight > 0 && run.File.BuildTags.Count > 0)
        {
            int shared = candidateTags.Count(tag => run.File.BuildTags.Contains(tag));
            if (shared > 0)
            {
                long extra = (long)shared * sameTagWeight;
                if (maxSameTagWeight > 0)
                    extra = Math.Min(extra, maxSameTagWeight);
                bonus += extra;
            }
        }

        return bonus;
    }

    /// <summary>
    /// Resolves an authored StageFight/StageChoose/StageShop RandomPool id into a frozen reward record
    /// (event reward type 5). The rolled identity is stored in the record so later application never
    /// re-rolls the pool. Safe to call while generating offers inside the same Mutation.
    /// </summary>
    internal static Theatre6RewardData RollPoolReward(Mutation m, Theatre6RunState run, int poolId, int amount = 1)
    {
        Theatre6RandomPoolTable pool = EcoTables.RandomPool(poolId);
        Theatre6RewardData reward = new() { RewardType = RewardTypeSkillPool, TemplateId = poolId, Amount = Math.Max(1, amount) };
        if (pool.Type == ShopGoodTypeAttrPack)
            reward.AttrPack = RollAttrPackPool(m, run, poolId);
        else
            reward.SkillId = RollSkillPool(m, run, poolId);
        return reward;
    }

    /// <summary>Resolves an authored buff pool id into a frozen reward record (event reward type 4).</summary>
    internal static Theatre6RewardData RollBuffPoolReward(Mutation m, Theatre6RunState run, int poolId, int amount = 1) => new()
    {
        RewardType = RewardTypeBuffPool,
        TemplateId = poolId,
        Amount = Math.Max(1, amount),
        BuffList = new List<Theatre6BuffState> { new() { BuffId = RollFloorBuff(m, run, poolId) } }
    };

    /// <summary>The reward DTO carries buff records (the client indexes BuffId), not bare ids.</summary>
    private static Theatre6BuffState DuplicateBuff(Theatre6BuffState buff) => new()
    {
        BuffId = buff.BuffId,
        TriggerCount = buff.TriggerCount,
        AddMagic = buff.AddMagic
    };

    private static int RollWeighted(Mutation m, Theatre6RunState run, List<(int Id, long Weight)> candidates)
    {
        long total = candidates.Sum(candidate => candidate.Weight);
        Require(total > 0, ErrorNotOpen);
        long roll = Roll(run, (int)Math.Min(int.MaxValue, total));
        for (int index = 0; index < candidates.Count; index++)
        {
            roll -= candidates[index].Weight;
            if (roll < 0)
                return index;
        }

        return candidates.Count - 1;
    }

    /// <summary>
    /// Weighted pick of one buff from an authored <c>Theatre6StageBuffPool</c> PoolId (floor start buffs).
    /// Returns the chosen buff id without granting it; callers apply it with AddBuff.
    /// </summary>
    internal static int RollFloorBuff(Mutation m, Theatre6RunState run, int poolId)
    {
        List<(int Id, long Weight)> candidates = TableReaderV2.Parse<Theatre6StageBuffPoolTable>()
            .Where(entry => entry.PoolId == poolId && entry.BuffId > 0 && entry.Weight > 0)
            .OrderBy(entry => entry.Id)
            .Select(entry => (entry.BuffId, (long)entry.Weight))
            .ToList();
        Require(candidates.Count > 0, ErrorNotOpen);
        return candidates[RollWeighted(m, run, candidates)].Id;
    }

    /// <summary>
    /// Grants every buff of an authored floor start pool (each pool grants one weighted row).
    /// </summary>
    internal static List<Theatre6LiveBuffState> GrantFloorBuffPools(Mutation m, Theatre6RunState run, IEnumerable<int> poolIds)
    {
        List<Theatre6LiveBuffState> granted = new();
        foreach (int poolId in poolIds ?? Array.Empty<int>())
        {
            if (poolId <= 0)
                continue;
            granted.Add(AddBuff(m, run, RollFloorBuff(m, run, poolId)));
        }

        return granted;
    }

    #endregion

    #region Shop

    /// <summary>
    /// Fills the run's current shop room with goods frozen from the authored pools. The room engine
    /// installs the room it is materialising as <c>run.CurrentRoomDataDb</c> before building its authored
    /// content, so the current room is always the room being published; the room-type requirement keeps a
    /// mis-targeted call loud (20423073) instead of silently publishing an empty shelf.
    /// </summary>
    internal static void BuildShop(Mutation m, Theatre6RunState run, int shopId)
    {
        Theatre6RoomDataDb room = run.CurrentRoomDataDb ?? throw new InvalidDataException("Theatre6 shop room is missing.");
        Require(room.RoomType == RoomBattleShop, ErrorNotInShop);
        Theatre6StageShopTable shop = EcoTables.Shop(shopId);
        room.ShopId = shopId;
        room.ShopFreshCount = 0;
        room.BuySanTimes = 0;
        room.ShopGoods = RollShopGoods(m, run, shop);
    }

    /// <summary>
    /// Rolls shop goods position by position. Locked entries survive a refresh untouched (the client's
    /// refresh pre-check requires at least one unlocked position, i.e. only unlocked goods are re-rolled);
    /// every other position receives a freshly rolled good in the unsold, unlocked state.
    /// </summary>
    private static List<Theatre6ShopGoodData> RollShopGoods(Mutation m, Theatre6RunState run, Theatre6StageShopTable shop, List<Theatre6ShopGoodData>? existing = null)
    {
        List<Theatre6ShopGoodData> goods = new();
        List<int> pools = shop.RewardPools;
        for (int position = 1; position <= pools.Count; position++)
        {
            int poolId = pools[position - 1];
            if (poolId <= 0)
                continue;
            Theatre6ShopGoodData? locked = existing?.FirstOrDefault(good => good.Position == position && good.IsLock);
            if (locked is not null)
            {
                goods.Add(locked);
                continue;
            }

            int type = EcoTables.RandomPool(poolId).Type;
            int goodId = type == ShopGoodTypeSkill
                ? RollSkillPool(m, run, poolId)
                : RollAttrPackPool(m, run, poolId);
            goods.Add(new Theatre6ShopGoodData { Position = position, GoodId = goodId, Type = type });
        }

        Require(goods.Count > 0, ErrorNotOpen);
        return goods;
    }

    private static Theatre6ShopGoodData RequireShopGood(Theatre6RunState run, int position)
    {
        Theatre6RoomDataDb room = run.CurrentRoomDataDb ?? throw new InvalidDataException("Theatre6 shop room is missing.");
        Require(room.RoomType == RoomBattleShop && room.ShopId > 0, ErrorNotInShop);
        Theatre6ShopGoodData? good = room.ShopGoods.FirstOrDefault(entry => entry.Position == position);
        Require(good is not null, ErrorNotInShop);
        return good!;
    }

    private static Theatre6ShopGoodData RequireUnsoldShopGood(Theatre6RunState run, int position)
    {
        Theatre6ShopGoodData good = RequireShopGood(run, position);
        Require(!good.IsSell, ErrorShopGoodSold);
        return good;
    }

    private static int SkillBuyPrice(int skillId) => EcoTables.Skill(skillId).BuyPrice;
    private static int AttrPackBuyPrice(int packId) => EcoTables.AttrPack(packId).BuyPrice;

    /// <summary>
    /// Relic acquisition. Stack count is capped by the authored LimitCount (0/absent = unlimited,
    /// which is how the repeatable skill star-up relic works). The relic's authored BuffIds are
    /// granted as live buffs, which is how native magic / star-up chances reach the client.
    /// </summary>
    internal static Theatre6AttrPackState AddAttrPack(Mutation m, Theatre6RunState run, int packId)
    {
        Theatre6AttrPackTable row = EcoTables.AttrPack(packId);
        Require(row.Character is not > 0 || row.Character == run.File.CharacterId, ErrorNotOpen);
        Theatre6AttrPackState? existing = run.File.AttrPacks.FirstOrDefault(pack => pack.PackId == packId);
        Require(existing is null || row.LimitCount is not > 0 || existing.Num < row.LimitCount, ErrorNotOpen);
        if (existing is null)
        {
            existing = new Theatre6AttrPackState { PackId = packId, Num = 1 };
            run.File.AttrPacks.Add(existing);
        }
        else
        {
            existing.Num = checked(existing.Num + 1);
        }

        m.Push(new NotifyTheatre6AttrPackChange { AttrPack = new Theatre6AttrPackData { PackId = packId, Num = existing.Num } });
        if (row.BuffIds is int buffId && buffId > 0)
            AddBuff(m, run, buffId);
        RecalculateAttrs(m, run);
        RecalculateScore(m, run);
        // Every acquisition route (reward list, shop purchase) runs through here, so the authored relic
        // trigger family (e.g. Quenched Edge: ATK for each relic obtained) fires exactly once.
        TriggerEffects(m, run, triggerType: 10, amount: 1, parameter: 0);
        return existing;
    }

    #endregion

    #region Reward application

    /// <summary>
    /// Applies an ordered authored reward list. The identity inside each record was frozen when the
    /// offer was generated, so nothing here re-rolls a pool. Returns the applied copies in order.
    /// </summary>
    internal static List<Theatre6RewardData> ApplyRewards(Mutation m, Theatre6RunState run, IEnumerable<Theatre6RewardData> rewards)
    {
        List<Theatre6RewardData> applied = new();
        foreach (Theatre6RewardData reward in rewards)
        {
            Theatre6RewardData result = new()
            {
                RewardType = reward.RewardType,
                TemplateId = reward.TemplateId,
                Amount = reward.Amount,
                SkillId = reward.SkillId,
                AttrPack = reward.AttrPack,
                BuffList = reward.BuffList.Select(DuplicateBuff).ToList(),
                FightId = reward.FightId,
                MonsterId = reward.MonsterId,
                FightRewards = reward.FightRewards?.Select(DuplicateReward).ToList() ?? new List<Theatre6RewardData>()
            };
            switch (reward.RewardType)
            {
                case RewardTypeGoods:
                    AddGoods(m, run, reward.TemplateId, reward.Amount);
                    break;
                case RewardTypeSan:
                    result.AmountChange = AddSan(m, run, reward.Amount);
                    break;
                case RewardTypeCoin:
                    AddGold(m, run, reward.Amount);
                    result.AmountChange = reward.Amount;
                    break;
                case RewardTypeHp:
                    result.AmountChange = AddHealth(m, run, reward.Amount);
                    break;
                case RewardTypeBuffPool:
                    foreach (Theatre6BuffState granted in reward.BuffList)
                    {
                        if (granted.BuffId > 0)
                            AddBuff(m, run, granted.BuffId);
                    }

                    break;
                case RewardTypeSkillPool:
                    // Event reward type 5 resolves to either identity: a concrete skill id or, through
                    // RandomPool.Type 2, a concrete relic. Both were frozen when the offer was generated.
                    if (reward.AttrPack > 0)
                        AddAttrPack(m, run, reward.AttrPack);
                    else
                    {
                        Require(reward.SkillId > 0, ErrorSkillNotExist);
                        GrantSkill(m, run, reward.SkillId, pushUpdate: true);
                    }

                    break;
                case RewardTypeFight:
                case RewardTypeAvg:
                    // Fight identity is room state (Run) and AVG kind 100 is a local movie keyed by the
                    // story detail id; neither changes the in-run economy.
                    break;
                default:
                    throw new InvalidDataException($"Unsupported Theatre6 reward type {reward.RewardType}.");
            }

            applied.Add(result);
        }

        // Relic acquisition fire the authored relic trigger inside AddAttrPack (single acquisition
        // point shared with the shop); mission completion is signalled by the room/task engine through
        // TriggerEffects(..., triggerType: 11, ...).
        return applied;
    }

    private static Theatre6RewardData DuplicateReward(Theatre6RewardData reward) => new()
    {
        RewardType = reward.RewardType,
        TemplateId = reward.TemplateId,
        Amount = reward.Amount,
        AmountChange = reward.AmountChange,
        SkillId = reward.SkillId,
        AttrPack = reward.AttrPack,
        BuffList = reward.BuffList.Select(DuplicateBuff).ToList(),
        FightId = reward.FightId,
        MonsterId = reward.MonsterId,
        FightRewards = reward.FightRewards?.Select(DuplicateReward).ToList() ?? new List<Theatre6RewardData>()
    };

    /// <summary>Called by Run once per new run: base attributes, starting buff and starting sanity state.</summary>
    internal static void InitializeCharacter(Mutation m, Theatre6RunState run, int initBuffId)
    {
        Theatre6CharacterTable character = EcoTables.Character(run.File.CharacterId);
        if (initBuffId > 0)
        {
            int index = character.TagBuffIds.IndexOf(initBuffId);
            Require(index >= 0, ErrorNotOpen);
            int conditionId = index < character.BuffConditionIds.Count ? character.BuffConditionIds[index] : 0;
            Require(conditionId <= 0 || HasCondition(m.Player, conditionId), ErrorNotOpen);
            AddBuff(m, run, initBuffId);
        }

        RecalculateAttrs(m, run);
        RecalculateScore(m, run);
        ApplySanState(m, run);
    }

    private static Theatre6ShopGoodData CloneGood(Theatre6ShopGoodData good) => new()
    {
        Position = good.Position,
        GoodId = good.GoodId,
        IsSell = good.IsSell,
        IsLock = good.IsLock,
        Type = good.Type
    };

    #endregion

    #region In-run shop requests

    [RequestPacketHandler("Theatre6ShopFreshRequest")]
    public static void ShopFresh(Session session, Packet.Request packet) =>
        Handle<Theatre6ShopFreshRequest, Theatre6ShopFreshResponse>(session, packet, static (m, request, response) =>
        {
            Theatre6RunState run = CurrentRun(m);
            Theatre6RoomDataDb room = RequireShopRoom(run);
            Theatre6StageShopTable shop = EcoTables.Shop(room.ShopId);
            Require(request.ShopFreshCount == room.ShopFreshCount, ErrorShopFreshCount);
            Require(room.ShopFreshCount < shop.RefreshTime, ErrorShopFreshCount);
            Require(room.ShopGoods.Any(good => !good.IsLock), ErrorAllGoodsLocked);
            int price = shop.BaseRereshPrice + room.ShopFreshCount * shop.RefreshAddPrice;
            Require(run.GoldAmount >= price, ErrorGoldNotEnough);
            AddGold(m, run, -price);
            room.ShopFreshCount++;
            room.ShopGoods = RollShopGoods(m, run, shop, room.ShopGoods);
            response.ShopGoods = room.ShopGoods.Select(CloneGood).ToList();
            response.ShopFreshCount = room.ShopFreshCount;
        }, static (response, code) => response.Code = code);

    [RequestPacketHandler("Theatre6EndShopRequest")]
    public static void EndShop(Session session, Packet.Request packet) =>
        Handle<Theatre6EndShopRequest, Theatre6EndShopResponse>(session, packet, static (m, request, response) =>
        {
            Theatre6RunState run = CurrentRun(m);
            RequireShopRoom(run);
            RequireOverflowResolved(run);
            // The shop room ends here; advancing the chain is the room engine's contract.
            AdvanceRoom(m, run, afterResponse: true);
        }, static (response, code) => response.Code = code);

    /// <summary>
    /// The authored overflow barrier: while the skill overflow queue is unresolved the client refuses to
    /// leave the shop (BattleShop:OnBtnExitClick → CheckForceSellSkillBlock) and blocks the other
    /// progression entries (task confirm, task settlement, choice, fight reward), so the server enforces
    /// the same rule and a hostile client cannot advance past unsold skills. Selling the queue is always
    /// allowed, and a run whose settlement is already frozen is never blocked — CheckForceSellSkillBlock
    /// returns false once the mode is settled.
    /// </summary>
    internal static void RequireOverflowResolved(Theatre6RunState run) =>
        Require(run.SkillOverQueue.Count == 0 || run.Settled, ErrorOverflowUnresolved);

    [RequestPacketHandler("Theatre6ShopGoodLockRequest")]
    public static void ShopGoodLock(Session session, Packet.Request packet) =>
        Handle<Theatre6ShopGoodLockRequest, Theatre6ShopGoodLockResponse>(session, packet, static (m, request, response) =>
        {
            Theatre6RunState run = CurrentRun(m);
            Theatre6ShopGoodData good = RequireShopGood(run, request.Pos);
            // The lock button sends the state it currently displays and the client applies the returned
            // value, i.e. this request toggles. A stale view (its value no longer matches ours) is
            // rejected so a racing or duplicated request cannot flip the lock twice.
            Require(request.IsLock == good.IsLock, ErrorNotOpen);
            good.IsLock = !good.IsLock;
            response.IsLock = good.IsLock;
        }, static (response, code) => response.Code = code);

    [RequestPacketHandler("Theatre6ShopGoodBuyRequest")]
    public static void ShopGoodBuy(Session session, Packet.Request packet) =>
        Handle<Theatre6ShopGoodBuyRequest, Theatre6ShopGoodBuyResponse>(session, packet, static (m, request, response) =>
        {
            Theatre6RunState run = CurrentRun(m);
            Theatre6ShopGoodData good = RequireUnsoldShopGood(run, request.Pos);
            int cost = good.Type == ShopGoodTypeSkill ? SkillBuyPrice(good.GoodId) : AttrPackBuyPrice(good.GoodId);
            Require(run.GoldAmount >= cost, ErrorGoldNotEnough);
            if (good.Type == ShopGoodTypeAttrPack)
                RequireBuyableRelic(run, good.GoodId);
            AddGold(m, run, -cost);
            good.IsSell = true;
            good.IsLock = false;
            if (good.Type == ShopGoodTypeSkill)
            {
                Theatre6SkillUpdate update = GrantSkill(m, run, good.GoodId, pushUpdate: false);
                response.SkillUpdates = new List<Theatre6SkillUpdate> { update };
            }
            else
            {
                AddAttrPack(m, run, good.GoodId);
                response.AttrPackId = good.GoodId;
            }

            response.SellShopGood = CloneGood(good);
        }, static (response, code) => response.Code = code);

    /// <summary>
    /// The authored upgrade relic must not be sold when no skill can still be star-upgraded; the client
    /// performs the same pre-check (Theatre6BattleShopBuyLvUpRelicTip).
    /// </summary>
    private static void RequireBuyableRelic(Theatre6RunState run, int packId)
    {
        int specialPackId = EconCfg("SkillUpAttrPackId");
        if (packId != specialPackId)
            return;
        int buffId = EcoTables.AttrPack(packId).BuffIds ?? 0;
        if (buffId <= 0)
            return;
        List<int> parameters = EcoTables.Buff(buffId).BuffEffectParams;
        int levelLimit = parameters.Count > 2 ? parameters[2] : 0;
        int qualityLimit = parameters.Count > 3 ? parameters[3] : 0;
        Require(HasUpgradableSkill(run, levelLimit, qualityLimit), ErrorSpecialRelicMaxSkill);
    }

    [RequestPacketHandler("Theatre6ShopGoodSellRequest")]
    public static void ShopGoodSell(Session session, Packet.Request packet) =>
        Handle<Theatre6ShopGoodSellRequest, Theatre6ShopGoodSellResponse>(session, packet, static (m, request, response) =>
        {
            Theatre6RunState run = CurrentRun(m);
            RequireShopRoom(run);
            Theatre6SkillState? skill = FindSkill(run, request.SkillId);
            Require(skill is not null, ErrorSkillNotExist);
            AddGold(m, run, EcoTables.Skill(skill!.SkillId).SellPrice);
            run.File.Skills.Remove(skill);
            response.SkillUpdates = new List<Theatre6SkillUpdate>
            {
                new() { RemovesSkills = new List<Theatre6SkillData> { CloneSkill(skill) } }
            };
            RecalculateAttrs(m, run, pushChanges: false);
            RecalculateScore(m, run);
        }, static (response, code) => response.Code = code);

    [RequestPacketHandler("Theatre6ShopBuySanRequest")]
    public static void ShopBuySan(Session session, Packet.Request packet) =>
        Handle<Theatre6ShopBuySanRequest, Theatre6ShopBuySanResponse>(session, packet, static (m, request, response) =>
        {
            Theatre6RunState run = CurrentRun(m);
            Theatre6RoomDataDb room = RequireShopRoom(run);
            Theatre6StageShopTable shop = EcoTables.Shop(room.ShopId);
            Require(request.BuySanTimes == room.BuySanTimes, ErrorBuySanCount);
            Require(shop.BuySanNum > 0 && shop.BuySanMaxTimes > 0, ErrorBuySanConfig);
            Require(room.BuySanTimes < shop.BuySanMaxTimes, ErrorBuySanLimit);
            Require(run.CurSan < run.MaxSan, ErrorBuySanFull);
            int price = shop.BuySanBasePrice + room.BuySanTimes * shop.BuySanAddPrice;
            Require(run.GoldAmount >= price, ErrorGoldNotEnough);
            AddGold(m, run, -price);
            room.BuySanTimes++;
            AddSan(m, run, shop.BuySanNum);
            response.BuySanTimes = room.BuySanTimes;
        }, static (response, code) => response.Code = code);

    private static Theatre6RoomDataDb RequireShopRoom(Theatre6RunState run)
    {
        Theatre6RoomDataDb? room = run.CurrentRoomDataDb;
        Require(room is not null && room.RoomType == RoomBattleShop && room.ShopId > 0, ErrorNotInShop);
        Require(EcoTables.Shops.Value.ContainsKey(room!.ShopId), ErrorNotInShop);
        return room;
    }

    #endregion

    #region Skill board requests

    [RequestPacketHandler("Theatre6SkillMoveOrSwapRequest")]
    public static void SkillMoveOrSwap(Session session, Packet.Request packet) =>
        Handle<Theatre6SkillMoveOrSwapRequest, Theatre6SkillMoveOrSwapResponse>(session, packet, static (m, request, response) =>
        {
            Theatre6RunState run = CurrentRun(m);
            Theatre6SkillState src = FindSkill(run, request.SrcSkillId) ?? throw new ServerCodeException("Theatre6 skill is not owned.", ErrorSkillNotExist);
            Theatre6SkillTable srcRow = EcoTables.Skill(src.SkillId);
            Require(request.DstPosition >= 1 && request.DstPosition <= SkillSlotLimit(request.DstSlotType)
                && request.DstSlotType is SlotTypeSpecial or SlotTypeActive or SlotTypeInsert or SlotTypeBag, ErrorSkillMove);
            Theatre6SkillState? dst = FindSkillAt(run, request.DstSlotType, request.DstPosition);
            if (dst is not null && dst.SkillId == src.SkillId)
            {
                response.SkillUpdates = new List<Theatre6SkillUpdate>();
                return;
            }

            int sourceSlot = src.SlotType;
            int sourcePosition = src.Position;
            if (request.DstSlotType != SlotTypeBag)
            {
                Require(SkillInstallSlots(srcRow.Type).Contains(request.DstSlotType), ErrorSkillMove);
                // The client validates this even when the destination is empty, so an equipping move
                // can never leave two equip slots holding the same family.
                Require(!EquippedFamilyConflicts(run, srcRow.SkillKey, src.SkillId, request.DstSlotType, request.DstPosition), ErrorSkillSwap);
            }

            if (dst is not null)
            {
                // Swapping must keep the displaced skill legal in the source slot.
                Theatre6SkillTable dstRow = EcoTables.Skill(dst.SkillId);
                if (sourceSlot != SlotTypeBag)
                    Require(SkillInstallSlots(dstRow.Type).Contains(sourceSlot)
                        || (sourceSlot == SlotTypeSpecial && SkillInstallSlots(dstRow.Type).Contains(SlotTypeSpecial)), ErrorSkillSwap);
                Require(dstRow.Level <= srcRow.Level || dstRow.SkillKey != srcRow.SkillKey, ErrorSkillSwap);
            }

            Theatre6SkillUpdate update = new();
            if (dst is null)
            {
                run.File.Skills.Remove(src);
                src.SlotType = request.DstSlotType;
                src.Position = request.DstPosition;
                run.File.Skills.Add(src);
                update.RemovesSkills = new List<Theatre6SkillData>
                {
                    new() { SkillId = src.SkillId, SlotType = sourceSlot, Position = sourcePosition }
                };
                update.AddSkill = CloneSkill(src);
            }
            else
            {
                dst.SlotType = sourceSlot;
                dst.Position = sourcePosition;
                src.SlotType = request.DstSlotType;
                src.Position = request.DstPosition;
                update.ReplaceSkills = new List<Theatre6SkillData> { CloneSkill(src), CloneSkill(dst) };
            }

            response.SkillUpdates = new List<Theatre6SkillUpdate> { update };
            RecalculateAttrs(m, run, pushChanges: false);
            RecalculateScore(m, run);
        }, static (response, code) => response.Code = code);

    /// <summary>
    /// The client forbids two equipped slots (never the Bag) from holding the same skill family: the
    /// moving/arriving skill itself and the destination position are excluded from the scan.
    /// </summary>
    private static bool EquippedFamilyConflicts(Theatre6RunState run, int skillKey, int ignoredSkillId, int slotType, int position) =>
        run.File.Skills.Any(skill => skill.SlotType != SlotTypeBag
            && skill.SkillId != ignoredSkillId
            && !(skill.SlotType == slotType && skill.Position == position)
            && EcoTables.Skill(skill.SkillId).SkillKey == skillKey);

    [RequestPacketHandler("Theatre6SkillOverQueueSellRequest")]
    public static void SkillOverQueueSell(Session session, Packet.Request packet) =>
        Handle<Theatre6SkillOverQueueSellRequest, Theatre6SkillOverQueueSellResponse>(session, packet, static (m, request, response) =>
        {
            Theatre6RunState run = CurrentRun(m);
            Require(run.SkillOverQueue.Count > 0, ErrorOverflowEmpty);
            int gold = run.SkillOverQueue.Sum(skillId => EcoTables.Skill(skillId).SellPrice);
            run.SkillOverQueue.Clear();
            AddGold(m, run, gold);
        }, static (response, code) => response.Code = code);

    [RequestPacketHandler("Theatre6BuffLevelUpSkillRequest")]
    public static void BuffLevelUpSkill(Session session, Packet.Request packet) =>
        Handle<Theatre6BuffLevelUpSkillRequest, Theatre6BuffLevelUpSkillResponse>(session, packet, static (m, request, response) =>
        {
            Theatre6RunState run = CurrentRun(m);
            Require(run.Buffs.TryGetValue(request.BuffId, out Theatre6LiveBuffState? buff), ErrorBuffUpgradeLimit);
            response.SkillUpdates = new List<Theatre6SkillUpdate> { UpgradeSkillByBuff(m, run, buff!, request.SkillId) };
        }, static (response, code) => response.Code = code);

    #endregion
}
