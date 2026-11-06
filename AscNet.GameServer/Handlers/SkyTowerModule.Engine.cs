using AscNet.Common.Database;
using AscNet.Common.Util;
using AscNet.Table.V2.share.condition;
using AscNet.Table.V2.share.skytower;

namespace AscNet.GameServer.Handlers;

/// <summary>
/// Dreamhouse Tycoon production engine. The client simulates production locally (XSkyTowerControlBusiness
/// AdvanceLocalProduction, XSkyTowerControlSkill/Staff) and corrects itself from the server's DataUpdate, so the
/// rules here are a line-for-line port of that client algorithm over the authoritative share/skytower tables.
/// Where the client contract leaves a rule to the server (offline limits, speed-up accounting, perform unlocks)
/// the rule is labelled "AscNet policy" at its site.
/// </summary>
internal static partial class SkyTowerModule
{
    internal const int WorkNone = 0, WorkIngredient = 1, WorkFood = 2, WorkSale = 3;
    internal const int StateNormal = 0, StateWaitForStorage = 1, StateWaitForConsume = 2, StateStop = 3;
    internal const int ProductIngredient = 1, ProductFood = 2;
    internal const int PerformNotStart = 0, PerformOnGoing = 1, PerformFinish = 2;
    internal const int PerformEvent = 1, PerformIndent = 2, PerformFloorGroup = 3;
    internal const int Ratio = 10000;
    private const int AdditionReduceTime = 1, AdditionUpProduce = 2, AdditionUpPrice = 3, AdditionExtraProduce = 4, AdditionUpSaleCount = 5;
    private const int CondNone = 0, CondNearby = 1, CondWorkPosition = 2, CondFloorHeight = 3, CondOtherActive = 4;
    private const int WorkPosFactor = 10, StorageGroupFactor = 10000, StorageTypeFactor = 100;

    // ---- authoritative tables ----

    internal static readonly Lazy<SkyTowerActivityTable?> Activity = new(() => TableReaderV2.Parse<SkyTowerActivityTable>().FirstOrDefault());
    private static readonly Lazy<Dictionary<string, List<int>>> Config = new(() =>
        TableReaderV2.Parse<SkyTowerConfigTable>().ToDictionary(row => row.Key, row => row.Values));
    internal static readonly Lazy<Dictionary<int, SkyTowerLvTable>> Levels = new(() => TableReaderV2.Parse<SkyTowerLvTable>().ToDictionary(row => row.Lv));
    private static readonly Lazy<Dictionary<int, SkyTowerFloorTable>> Floors = new(() => TableReaderV2.Parse<SkyTowerFloorTable>().ToDictionary(row => row.Id));
    private static readonly Lazy<Dictionary<int, SkyTowerFloorGroupTable>> Groups = new(() => TableReaderV2.Parse<SkyTowerFloorGroupTable>().ToDictionary(row => row.Id));
    internal static readonly Lazy<Dictionary<int, SkyTowerCharacterTable>> CharacterRows = new(() => TableReaderV2.Parse<SkyTowerCharacterTable>().ToDictionary(row => row.Id));
    private static readonly Lazy<Dictionary<int, SkyTowerCharacterLvTable>> CharacterLvRows = new(() => TableReaderV2.Parse<SkyTowerCharacterLvTable>().ToDictionary(row => row.Id));
    private static readonly Lazy<Dictionary<int, SkyTowerIngredientTable>> Ingredients = new(() => TableReaderV2.Parse<SkyTowerIngredientTable>().ToDictionary(row => row.Id));
    private static readonly Lazy<Dictionary<int, SkyTowerFoodTable>> Foods = new(() => TableReaderV2.Parse<SkyTowerFoodTable>().ToDictionary(row => row.Id));
    internal static readonly Lazy<Dictionary<int, SkyTowerPerformTable>> PerformRows = new(() => TableReaderV2.Parse<SkyTowerPerformTable>().ToDictionary(row => row.Id));
    internal static readonly Lazy<Dictionary<int, SkyTowerStoryTable>> StoryRows = new(() => TableReaderV2.Parse<SkyTowerStoryTable>().ToDictionary(row => row.Id));
    internal static readonly Lazy<Dictionary<int, SkyTowerTalkTable>> TalkRows = new(() => TableReaderV2.Parse<SkyTowerTalkTable>().ToDictionary(row => row.Id));
    private static readonly Lazy<Dictionary<int, ConditionTable>> Conditions = new(() => TableReaderV2.Parse<ConditionTable>()
        .Where(row => row.Type is >= 23501 and <= 23504).ToDictionary(row => row.Id));
    // (ProductType, ProductId) -> storage limits by tower level, ascending.
    private static readonly Lazy<Dictionary<(int, int), List<(int Lv, int Limit)>>> StorageLimits = new(() =>
        TableReaderV2.Parse<SkyTowerStorageTable>().GroupBy(row => (row.ProductType, row.ProductId))
            .ToDictionary(group => group.Key, group => group.OrderBy(row => row.SkyTowerLv).Select(row => (row.SkyTowerLv, row.StorageLimit)).ToList()));
    private static readonly Lazy<Dictionary<int, SkillInfo>> Skills = new(() => TableReaderV2.Parse<SkyTowerCharacterSkillTable>()
        .ToDictionary(row => row.Id, BuildSkill));

    internal static int ConfigInt(string key, int index = 0) => Config.Value.TryGetValue(key, out List<int>? values) && index < values.Count ? values[index] : 0;
    internal static int MaxLevel => Levels.Value.Keys.Max();
    internal static int GoldItemId => ConfigInt("GoldItemId");
    internal static int CharacterLvUpItemId => ConfigInt("CharacterLvUpItemId");
    internal static int SpeedUpItemId => ConfigInt("SpeedUpItemId");

    // ---- keys and geometry ----

    internal static int WorkPosKey(int floorId, int index) => floorId * WorkPosFactor + index - 1;
    internal static (int Floor, int Index) ParseKey(int key) => (key / WorkPosFactor, key % WorkPosFactor + 1);
    internal static int StorageKey(int groupId, int productType, int productId) => groupId * StorageGroupFactor + productType * StorageTypeFactor + productId;
    internal static int ProductTypeOf(int workType) => workType == WorkIngredient ? ProductIngredient : ProductFood;
    internal static int GroupOf(int floorId) => Floors.Value.TryGetValue(floorId, out SkyTowerFloorTable? floor) ? floor.GroupId : 0;
    internal static int MaxFloor(int level) => Levels.Value.TryGetValue(level, out SkyTowerLvTable? row) ? row.FloorId : 0;
    internal static int MaxGroup(int level) => GroupOf(MaxFloor(level));
    private static int Vector(int floorId, int index) =>
        Floors.Value.TryGetValue(floorId, out SkyTowerFloorTable? floor) && index >= 1 && index <= floor.WorkPosVector.Count ? floor.WorkPosVector[index - 1] : 0;
    internal static int AppointWork(int floorId, int index) =>
        Floors.Value.TryGetValue(floorId, out SkyTowerFloorTable? floor) && index >= 1 && index <= floor.AppointWork.Count ? floor.AppointWork[index - 1] : WorkNone;
    internal static bool IsFloorGroupProduct(int groupId, int productType, int productId) =>
        Groups.Value.TryGetValue(groupId, out SkyTowerFloorGroupTable? group)
        && (productType == ProductIngredient ? group.IngredientIds : group.FoodIds).Contains(productId);

    private static readonly Dictionary<int, int[]> PositionCache = new();

    /// <summary>Work positions open at a tower level: SkyTowerLv.UnlockWorkNum is cumulative, filled floor by floor, position by position.</summary>
    internal static int[] PositionsAt(int level)
    {
        lock (PositionCache)
        {
            if (PositionCache.TryGetValue(level, out int[]? cached)) return cached;
            int count = Levels.Value.Where(row => row.Key <= level).Sum(row => row.Value.UnlockWorkNum);
            int[] keys = Floors.Value.OrderBy(row => row.Key).SelectMany(row => Enumerable.Range(1, row.Value.WorkPosVector.Count).Select(index => WorkPosKey(row.Key, index)))
                .Take(count).ToArray();
            return PositionCache[level] = keys;
        }
    }

    internal static HashSet<int> UnlockedIngredients(int level) => Levels.Value.Where(row => row.Key <= level).SelectMany(row => row.Value.UnlockIngredient).ToHashSet();
    internal static HashSet<int> UnlockedFoods(int level) => Levels.Value.Where(row => row.Key <= level).SelectMany(row => row.Value.UnlockFood).ToHashSet();
    internal static bool IsUniversal(int characterId) => CharacterRows.Value.TryGetValue(characterId, out SkyTowerCharacterTable? row) && row.IsUniversal != 0;

    internal static int CharacterMaxLevel(int characterId)
    {
        if (IsUniversal(characterId)) return 1;
        for (int level = 99; level >= 1; level--)
            if (CharacterLvRows.Value.ContainsKey(characterId * 100 + level)) return level;
        return 1;
    }

    internal static SkyTowerCharacterLvTable? CharacterLvRow(int characterId, int level) =>
        CharacterLvRows.Value.GetValueOrDefault(characterId * 100 + level);

    internal static bool CharacterInGroup(int characterId, int groupId) =>
        CharacterRows.Value.TryGetValue(characterId, out SkyTowerCharacterTable? row) && (row.FloorGroup == -1 || row.FloorGroup == groupId);

    // ---- storage ----

    internal static long Stock(SkyTowerState s, int groupId, int productType, int productId) =>
        s.Storage.GetValueOrDefault(StorageKey(groupId, productType, productId));

    internal static void SetStock(SkyTowerState s, int groupId, int productType, int productId, long count) =>
        s.Storage[StorageKey(groupId, productType, productId)] = Math.Max(0, count);

    internal static int StorageLimit(int level, int productType, int productId)
    {
        if (!StorageLimits.Value.TryGetValue((productType, productId), out List<(int Lv, int Limit)>? rows)) return 0;
        int limit = 0;
        foreach ((int lv, int rowLimit) in rows)
            if (lv <= level) limit = rowLimit;
        return limit;
    }

    private static long FreeCapacity(SkyTowerState s, int groupId, int productType, int productId) =>
        Math.Max(0, StorageLimit(s.Level, productType, productId) - Stock(s, groupId, productType, productId));

    // ---- products ----

    private static int BaseCycle(int workType, int productId) => workType switch
    {
        WorkIngredient => Ingredients.Value.GetValueOrDefault(productId)?.ProduceNeedTime ?? 0,
        WorkFood => Foods.Value.GetValueOrDefault(productId)?.ProduceNeedTime ?? 0,
        WorkSale => Foods.Value.GetValueOrDefault(productId)?.SaleNeedTime ?? 0,
        _ => 0
    };

    private static int Priority(int workType, int productId) => workType == WorkIngredient
        ? Ingredients.Value.GetValueOrDefault(productId)?.Priority ?? 0
        : Foods.Value.GetValueOrDefault(productId)?.Priority ?? 0;

    internal static bool ProductExists(int workType, int productId) => workType == WorkIngredient ? Ingredients.Value.ContainsKey(productId) : Foods.Value.ContainsKey(productId);

    /// <summary>A dish's recipe: ingredients then handmade dishes, merged by (type, id), zero counts dropped.</summary>
    internal static List<SkyTowerProductAmountState> Recipe(int foodId)
    {
        List<SkyTowerProductAmountState> inputs = [];
        if (!Foods.Value.TryGetValue(foodId, out SkyTowerFoodTable? food)) return inputs;
        void Add(int type, int id, long count)
        {
            if (count <= 0) return;
            SkyTowerProductAmountState? existing = inputs.FirstOrDefault(input => input.ProductType == type && input.ProductId == id);
            if (existing is null) inputs.Add(new() { ProductType = type, ProductId = id, Count = count });
            else existing.Count += count;
        }
        for (int i = 0; i < food.ConsumeIngredientIds.Count; i++)
            Add(ProductIngredient, food.ConsumeIngredientIds[i], i < food.ConsumeIngredientCounts.Count ? food.ConsumeIngredientCounts[i] : 0);
        for (int i = 0; i < food.ConsumeFoodIds.Count; i++)
            Add(ProductFood, food.ConsumeFoodIds[i], i < food.ConsumeFoodCounts.Count ? food.ConsumeFoodCounts[i] : 0);
        return inputs;
    }

    // ---- skills ----

    private sealed record SkillAdditionInfo(int ProductId, int Addition, int Type, List<SkyTowerProductAmountState> ExtraInputs);

    private sealed class SkillInfo
    {
        public int Id, SectionType, ConditionType;
        public List<int> ProductIds = [];
        public List<SkillAdditionInfo> Additions = [];
        public List<(int X, int Y)> ShareOffsets = [];
        public List<string> ConditionParams = [];
        public List<(int X, int Y)> ConditionOffsets = [];

        public int Param(int index) => index < ConditionParams.Count && int.TryParse(ConditionParams[index], out int value) ? value : 0;
    }

    private static bool TryOffset(string text, out (int X, int Y) offset)
    {
        offset = default;
        int separator = text.IndexOf('_');
        if (separator < 0 || !int.TryParse(text[..separator], out int x) || !int.TryParse(text[(separator + 1)..], out int y)) return false;
        offset = (x, y);
        return true;
    }

    private static SkillInfo BuildSkill(SkyTowerCharacterSkillTable row)
    {
        SkillInfo info = new() { Id = row.Id, SectionType = row.SectionType, ConditionType = row.ConditionType,
            ProductIds = row.ProductId, ConditionParams = row.ConditionParams };
        foreach (string text in row.ShareType.Where(text => !string.IsNullOrEmpty(text)))
            if (TryOffset(text, out (int X, int Y) offset)) info.ShareOffsets.Add(offset);
        foreach (string text in row.ConditionParams.Skip(1).Where(text => !string.IsNullOrEmpty(text)))
            if (TryOffset(text, out (int X, int Y) offset)) info.ConditionOffsets.Add(offset);
        int count = Math.Min(row.ProductId.Count, Math.Min(row.SkillAddition.Count, row.SkillAdditionType.Count));
        for (int i = 0; i < count; i++)
        {
            List<SkyTowerProductAmountState> extra = [];
            if (row.SkillAdditionType[i] == AdditionExtraProduce && info.SectionType == WorkFood)
                extra = ParseExtraInputs(i < row.SkillParams.Count ? row.SkillParams[i] : null);
            info.Additions.Add(new(row.ProductId[i], row.SkillAddition[i], row.SkillAdditionType[i], extra));
        }
        return info;
    }

    /// <summary>"type_id_count|type_id_count" extra recipe of an ExtraProduce skill; any invalid item voids the whole entry (as the client does).</summary>
    private static List<SkyTowerProductAmountState> ParseExtraInputs(string? text)
    {
        List<SkyTowerProductAmountState> inputs = [];
        if (string.IsNullOrEmpty(text)) return inputs;
        foreach (string item in text.Split('|'))
        {
            string[] parts = item.Split('_').Select(part => part.Trim()).ToArray();
            if (parts.Length != 3 || !int.TryParse(parts[0], out int type) || !int.TryParse(parts[1], out int id) || !long.TryParse(parts[2], out long count)
                || id <= 0 || id >= 100 || count <= 0 || count > int.MaxValue
                || !(type == ProductIngredient && Ingredients.Value.ContainsKey(id) || type == ProductFood && Foods.Value.ContainsKey(id)))
                return [];
            SkyTowerProductAmountState? existing = inputs.FirstOrDefault(input => input.ProductType == type && input.ProductId == id);
            if (existing is null) inputs.Add(new() { ProductType = type, ProductId = id, Count = count });
            else existing.Count += count;
        }
        return inputs;
    }

    private static bool PosFree(SkyTowerWorkPosState? wp) => wp is null || wp.WorkType == WorkNone || wp.CharacterId <= 0 || wp.ProductId <= 0;

    private static bool TryOffsetTarget(SkyTowerState s, int floorId, int index, int dx, int dy, out int targetFloor, out int targetIndex)
    {
        targetFloor = targetIndex = 0;
        int vector = Vector(floorId, index);
        if (vector == 0) return false;
        targetFloor = floorId + dy;
        if (GroupOf(targetFloor) != GroupOf(floorId) || targetFloor < 1 || targetFloor > MaxFloor(s.Level)) return false;
        int wanted = vector + dx;
        if (wanted == 0 || !Floors.Value.TryGetValue(targetFloor, out SkyTowerFloorTable? floor)) return false;
        for (int i = 1; i <= floor.WorkPosVector.Count; i++)
            if (floor.WorkPosVector[i - 1] == wanted) { targetIndex = i; return true; }
        return false;
    }

    private static List<(int Floor, int Index)> SkillTargets(SkyTowerState s, SkillInfo skill, int floorId, int index)
    {
        // ShareType unset: self only; set: only the listed offsets ("0_0" is self).
        if (skill.ShareOffsets.Count == 0) return [(floorId, index)];
        List<(int, int)> targets = [];
        foreach ((int x, int y) in skill.ShareOffsets)
            if (TryOffsetTarget(s, floorId, index, x, y, out int tf, out int ti) && !targets.Contains((tf, ti)))
                targets.Add((tf, ti));
        return targets;
    }

    private static bool PosMatchesSkill(SkyTowerState s, SkillInfo skill, int floorId, int index)
    {
        if (!s.WorkPos.TryGetValue(WorkPosKey(floorId, index), out SkyTowerWorkPosState? wp) || wp.CharacterId <= 0) return false;
        return wp.WorkType == skill.SectionType && skill.ProductIds.Any(id => id == wp.ProductId || wp.ProductId == -1);
    }

    private static bool HasMatchingTarget(SkyTowerState s, SkillInfo skill, int floorId, int index) =>
        SkillTargets(s, skill, floorId, index).Any(target => PosMatchesSkill(s, skill, target.Floor, target.Index));

    private static int? IndependentRatio(SkyTowerState s, SkillInfo skill, (int Floor, int Index) location)
    {
        switch (skill.ConditionType)
        {
            case CondNone: return Ratio;
            case CondNearby:
                int need = skill.Param(0);
                if (need == 0) return null;
                foreach ((int x, int y) in skill.ConditionOffsets)
                    if (TryOffsetTarget(s, location.Floor, location.Index, x, y, out int tf, out int ti)
                        && s.WorkPos.TryGetValue(WorkPosKey(tf, ti), out SkyTowerWorkPosState? wp) && wp.CharacterId == need)
                        return Ratio;
                return null;
            case CondWorkPosition:
                int floorNeed = skill.Param(0), vectorNeed = skill.Param(1);
                if (floorNeed != 0 && floorNeed != location.Floor) return null;
                if (vectorNeed != 0 && vectorNeed != Vector(location.Floor, location.Index)) return null;
                return Ratio;
            case CondFloorHeight:
                int baseFloor = skill.Param(0);
                if (location.Floor < baseFloor) return null;
                return skill.Param(1) + skill.Param(2) * (location.Floor - baseFloor);
            default:
                return null;
        }
    }

    /// <summary>Condition ratios of every owned character under the current layout (client BuildPreviewSkillRatios, which ports the retail server).</summary>
    internal static Dictionary<int, Dictionary<int, int>> ComputeRatios(SkyTowerState s)
    {
        Dictionary<int, (int Floor, int Index)> locations = [];
        foreach ((int key, SkyTowerWorkPosState wp) in s.WorkPos)
            if (wp.CharacterId > 0 && !IsUniversal(wp.CharacterId)) locations[wp.CharacterId] = ParseKey(key);

        Dictionary<int, Dictionary<int, int>> result = [];
        Dictionary<int, List<SkillInfo>> dependent = [];
        List<(int Floor, int Index)> activeLocations = [];
        foreach ((int characterId, int level) in s.Characters.OrderBy(entry => entry.Key))
        {
            Dictionary<int, int> ratios = [];
            result[characterId] = ratios;
            if (!locations.TryGetValue(characterId, out (int Floor, int Index) location)) continue;
            bool hasActiveCondition = false;
            foreach (int skillId in CharacterLvRow(characterId, level)?.SkillId ?? [])
            {
                if (!Skills.Value.TryGetValue(skillId, out SkillInfo? skill) || ratios.ContainsKey(skillId)
                    || !HasMatchingTarget(s, skill, location.Floor, location.Index)) continue;
                if (skill.ConditionType == CondOtherActive)
                {
                    (dependent.TryGetValue(characterId, out List<SkillInfo>? list) ? list : dependent[characterId] = []).Add(skill);
                    continue;
                }
                int? ratio = IndependentRatio(s, skill, location);
                if (ratio is > 0)
                {
                    ratios[skillId] = ratio.Value;
                    hasActiveCondition |= skill.ConditionType != CondNone;
                }
            }
            if (hasActiveCondition) activeLocations.Add(location);
        }
        foreach ((int characterId, List<SkillInfo> skills) in dependent)
        {
            (int Floor, int Index) location = locations[characterId];
            foreach (SkillInfo skill in skills)
            {
                if (result[characterId].ContainsKey(skill.Id)) continue;
                int area = skill.Param(0), group = GroupOf(location.Floor);
                int count = area == 3 ? activeLocations.Count : activeLocations.Count(other =>
                    area == 1 ? GroupOf(other.Floor) == group : area == 2 && other.Floor == location.Floor);
                result[characterId][skill.Id] = skill.Param(1) + skill.Param(2) * count;
            }
        }
        return result;
    }

    internal sealed record Additions(int ReduceTime, int UpProduce, int UpPrice, int ExtraRatio, int UpSaleCount, List<SkyTowerProductAmountState> ExtraInputs);

    private static int AdditionValue(int addition, int conditionRatio) => (int)Math.Max(0, (long)addition * conditionRatio / Ratio);

    /// <summary>Per-position skill additions under the current layout, indexed by position key.</summary>
    internal static Dictionary<int, Additions> ComputeAdditions(SkyTowerState s, Dictionary<int, Dictionary<int, int>> ratios)
    {
        Dictionary<int, List<(SkillInfo Skill, int Ratio)>> shared = [];
        foreach ((int key, SkyTowerWorkPosState wp) in s.WorkPos)
        {
            if (wp.CharacterId <= 0 || !ratios.TryGetValue(wp.CharacterId, out Dictionary<int, int>? owned)) continue;
            (int floor, int index) = ParseKey(key);
            foreach ((int skillId, int ratio) in owned)
            {
                if (ratio <= 0 || !Skills.Value.TryGetValue(skillId, out SkillInfo? skill)) continue;
                foreach ((int tf, int ti) in SkillTargets(s, skill, floor, index))
                {
                    int targetKey = WorkPosKey(tf, ti);
                    (shared.TryGetValue(targetKey, out var list) ? list : shared[targetKey] = []).Add((skill, ratio));
                }
            }
        }
        Dictionary<int, Additions> result = [];
        foreach ((int key, SkyTowerWorkPosState wp) in s.WorkPos)
        {
            if (wp.ProductId <= 0 || PosFree(wp) || !shared.TryGetValue(key, out var skills)) continue;
            int reduce = 0, up = 0, price = 0, extraRatio = 0, saleCount = 0;
            List<SkyTowerProductAmountState> extraInputs = [];
            foreach ((SkillInfo skill, int conditionRatio) in skills)
            {
                if (skill.SectionType != wp.WorkType) continue;
                foreach (SkillAdditionInfo item in skill.Additions.Where(item => item.ProductId == wp.ProductId))
                {
                    int value = AdditionValue(item.Addition, conditionRatio);
                    if (value <= 0) continue;
                    switch (item.Type)
                    {
                        case AdditionReduceTime: reduce += value; break;
                        case AdditionUpProduce: up += value; break;
                        case AdditionUpPrice: price += value; break;
                        case AdditionUpSaleCount: saleCount += value; break;
                        case AdditionExtraProduce when wp.WorkType == WorkFood && extraRatio == 0 && item.ExtraInputs.Count > 0:
                            extraRatio = value; extraInputs = item.ExtraInputs; break;
                    }
                }
            }
            result[key] = new(reduce, up, price, extraRatio, saleCount, extraInputs);
        }
        return result;
    }

    private static long ReducedCycle(long baseCycle, int reduceTime) =>
        baseCycle <= 0 ? 0 : reduceTime <= 0 ? baseCycle : Math.Max(baseCycle - reduceTime, ConfigInt("MinWorkTime"));

    private static readonly Additions NoAdditions = new(0, 0, 0, 0, 0, []);

    internal sealed class Plan
    {
        public Dictionary<int, Dictionary<int, int>> Ratios;
        public Dictionary<int, Additions> Additions;
        public SkyTowerState S;

        public Plan(SkyTowerState s)
        {
            S = s;
            Ratios = ComputeRatios(s);
            Additions = ComputeAdditions(s, Ratios);
        }

        public Additions For(int key) => Additions.GetValueOrDefault(key) ?? NoAdditions;

        public long Cycle(int key, SkyTowerWorkPosState wp) => PosFree(wp) ? 0 : ReducedCycle(BaseCycle(wp.WorkType, wp.ProductId), For(key).ReduceTime);

        public long MaxSaleCount(int key, SkyTowerWorkPosState wp)
        {
            int baseCount = Foods.Value.GetValueOrDefault(wp.ProductId)?.OnceSellNum ?? 0;
            return wp.WorkType != WorkSale || PosFree(wp) || baseCount <= 0 ? 0 : baseCount + For(key).UpSaleCount;
        }

        public long SalePrice(int key, SkyTowerWorkPosState wp) => PosFree(wp) ? 0 : (Foods.Value.GetValueOrDefault(wp.ProductId)?.Price ?? 0) + For(key).UpPrice;
    }

    // ---- production simulation (port of XSkyTowerControl:AdvanceLocalProduction) ----

    internal sealed class SimResult
    {
        public long Gold;
        public Dictionary<(int Type, int Id), long> Produced = new();
        public Dictionary<int, long> Sold = new();
    }

    private sealed class Entry
    {
        public int Key, Group;
        public SkyTowerWorkPosState Wp = null!;
        public int Priority;
        public long Cycle, Time, ExtraRatio, UpProduce, Price, MaxSale;
        public List<SkyTowerProductAmountState> Inputs = [];
        public List<SkyTowerProductAmountState> ExtraInputs = [];
        public bool Running;
    }

    /// <summary>
    /// Advances the given positions (all when <paramref name="only"/> is null) over [from, to]. Gold is returned, not credited.
    /// A zero-length window still runs the start round, so a layout change takes effect at once like the client's next tick.
    /// </summary>
    internal static SimResult Advance(SkyTowerState s, long from, long to, int? only = null)
    {
        SimResult result = new();
        Plan plan = new(s);
        List<Entry> entries = [];
        foreach ((int key, SkyTowerWorkPosState wp) in s.WorkPos)
        {
            if (only is int single && single != key) continue;
            if (PosFree(wp) || wp.State == StateStop) continue;
            long cycle = plan.Cycle(key, wp);
            if (cycle <= 0) continue;
            Additions add = plan.For(key);
            List<SkyTowerProductAmountState> inputs = wp.WorkType switch
            {
                WorkSale => [new() { ProductType = ProductFood, ProductId = wp.ProductId, Count = plan.MaxSaleCount(key, wp) }],
                WorkFood => Recipe(wp.ProductId),
                _ => []
            };
            bool noInputRoundStarted = inputs.Count == 0 && (wp.HasStartedRound || wp.WorkedSeconds > 0 || wp.WorkType != WorkFood || add.ExtraRatio <= 0);
            entries.Add(new Entry
            {
                Key = key, Group = GroupOf(ParseKey(key).Floor), Wp = wp, Priority = Priority(wp.WorkType, wp.ProductId), Cycle = cycle, Time = from,
                Inputs = inputs, ExtraRatio = add.ExtraRatio, ExtraInputs = add.ExtraInputs, UpProduce = add.UpProduce,
                Price = plan.SalePrice(key, wp), MaxSale = plan.MaxSaleCount(key, wp),
                Running = wp.State == StateNormal && (wp.ReservedInputs.Count > 0 || noInputRoundStarted)
            });
        }
        if (entries.Count == 0) return result;
        entries.Sort((a, b) => a.Key.CompareTo(b.Key));
        List<Entry> starts = entries.OrderBy(entry => entry.Priority).ThenBy(entry => entry.Key).ToList();

        StartRound(s, starts, from);
        long current = from;
        for (int guard = 0; guard < 1_000_000; guard++)
        {
            long next = to;
            foreach (Entry entry in entries)
            {
                if (entry.Time > current) next = Math.Min(next, entry.Time);
                else if (entry.Running) next = Math.Min(next, entry.Time + Math.Max(0, entry.Cycle - entry.Wp.WorkedSeconds));
            }
            foreach (Entry entry in entries.Where(entry => entry.Time <= next))
            {
                if (entry.Running)
                {
                    entry.Wp.WorkedSeconds += next - entry.Time;
                    if (entry.Wp.WorkedSeconds >= entry.Cycle) Complete(s, entry, result);
                }
                entry.Time = next;
            }
            StartRound(s, starts, next);
            current = next;
            if (current >= to) break;
        }
        return result;
    }

    private static void StartRound(SkyTowerState s, List<Entry> starts, long time)
    {
        for (int pass = 0; pass < starts.Count; pass++)
        {
            bool started = false;
            foreach (Entry entry in starts)
                started |= TryStart(s, entry, time);
            if (!started) return;
        }
    }

    private static bool TryStart(SkyTowerState s, Entry entry, long time)
    {
        if (entry.Running || entry.Time > time) return false;
        SkyTowerWorkPosState wp = entry.Wp;
        bool sale = wp.WorkType == WorkSale;
        int state = StateNormal;
        foreach (SkyTowerProductAmountState input in entry.Inputs)
        {
            long stock = Stock(s, entry.Group, input.ProductType, input.ProductId);
            if (sale ? input.Count <= 0 || stock <= 0 : stock < input.Count) { state = StateWaitForConsume; break; }
        }
        if (state == StateNormal && !sale && FreeCapacity(s, entry.Group, ProductTypeOf(wp.WorkType), wp.ProductId) <= 0) state = StateWaitForStorage;
        wp.State = state;
        if (state != StateNormal) return false;
        List<SkyTowerProductAmountState> reserved = [];
        foreach (SkyTowerProductAmountState input in entry.Inputs)
        {
            long count = Stock(s, entry.Group, input.ProductType, input.ProductId);
            long take = sale ? Math.Min(count, input.Count) : input.Count;
            SetStock(s, entry.Group, input.ProductType, input.ProductId, count - take);
            if (sale) reserved.Add(new() { ProductType = input.ProductType, ProductId = input.ProductId, Count = take });
            else reserved.Add(new() { ProductType = input.ProductType, ProductId = input.ProductId, Count = input.Count });
        }
        wp.ReservedInputs = reserved;
        wp.ReservedExtraProduceRatio = 0;
        // Extra recipe: paid whole or not at all, and only once the base recipe is paid.
        if (wp.WorkType == WorkFood && entry.ExtraRatio > 0 && entry.ExtraInputs.Count > 0
            && entry.ExtraInputs.All(input => Stock(s, entry.Group, input.ProductType, input.ProductId) >= input.Count))
        {
            foreach (SkyTowerProductAmountState input in entry.ExtraInputs)
            {
                SetStock(s, entry.Group, input.ProductType, input.ProductId, Stock(s, entry.Group, input.ProductType, input.ProductId) - input.Count);
                reserved.Add(new() { ProductType = input.ProductType, ProductId = input.ProductId, Count = input.Count });
            }
            wp.ReservedExtraProduceRatio = entry.ExtraRatio;
        }
        wp.HasStartedRound = true;
        entry.Time = time;
        entry.Running = true;
        return true;
    }

    private static void Complete(SkyTowerState s, Entry entry, SimResult result)
    {
        SkyTowerWorkPosState wp = entry.Wp;
        if (wp.WorkType == WorkSale)
        {
            long sold = wp.ReservedInputs.Sum(input => input.Count);
            result.Gold += sold * entry.Price;
            result.Sold[wp.ProductId] = result.Sold.GetValueOrDefault(wp.ProductId) + sold;
        }
        else
        {
            int productType = ProductTypeOf(wp.WorkType);
            long capacity = FreeCapacity(s, entry.Group, productType, wp.ProductId);
            if (capacity > 0)
            {
                long outputRatio = Math.Max(Ratio + entry.UpProduce + wp.ReservedExtraProduceRatio, Ratio);
                long total = outputRatio + wp.ProduceRemainder;
                long output = Math.Min(total / Ratio, capacity);
                wp.ProduceRemainder = total % Ratio;
                if (output > 0)
                {
                    SetStock(s, entry.Group, productType, wp.ProductId, Stock(s, entry.Group, productType, wp.ProductId) + output);
                    result.Produced[(productType, wp.ProductId)] = result.Produced.GetValueOrDefault((productType, wp.ProductId)) + output;
                }
            }
        }
        wp.WorkedSeconds = 0;
        wp.ReservedInputs = [];
        wp.ReservedExtraProduceRatio = 0;
        wp.HasStartedRound = false;
        entry.Running = false;
    }

    /// <summary>Returns a position's reserved inputs to its group storage (stop, product change, swap across groups).</summary>
    internal static void Refund(SkyTowerState s, int key, SkyTowerWorkPosState wp)
    {
        int group = GroupOf(ParseKey(key).Floor);
        foreach (SkyTowerProductAmountState input in wp.ReservedInputs)
            SetStock(s, group, input.ProductType, input.ProductId, Stock(s, group, input.ProductType, input.ProductId) + input.Count);
        wp.ReservedInputs = [];
        wp.ReservedExtraProduceRatio = 0;
        wp.WorkedSeconds = 0;
        wp.HasStartedRound = false;
    }

    // ---- gold income speed (143008) ----

    /// <summary>Gold per hour of a floor group: working sale positions at their full batch (client _AccumulateWorkPosProduction).</summary>
    internal static double GoldSpeed(SkyTowerState s, int groupId, Plan plan)
    {
        double speed = 0;
        foreach ((int key, SkyTowerWorkPosState wp) in s.WorkPos)
        {
            (int floor, _) = ParseKey(key);
            if (floor > MaxFloor(s.Level) || GroupOf(floor) != groupId || wp.WorkType != WorkSale || PosFree(wp) || wp.State != StateNormal) continue;
            long cycle = plan.Cycle(key, wp);
            if (cycle > 0) speed += 3600.0 / cycle * plan.MaxSaleCount(key, wp) * plan.SalePrice(key, wp);
        }
        return speed;
    }

    // ---- conditions (client XConditionManager 23501-23504) ----

    internal static bool ConditionMet(SkyTowerState s, int conditionId)
    {
        if (conditionId <= 0) return true;
        if (!Conditions.Value.TryGetValue(conditionId, out ConditionTable? row) || row.Params.Count == 0) return false;
        int p0 = row.Params[0], p1 = row.Params.Count > 1 ? row.Params[1] : 0;
        return row.Type switch
        {
            23501 => MaxFloor(s.Level) >= p0,
            23502 => s.Level >= p0,
            23503 => p0 == 0 ? s.Characters.Values.Any(level => level >= p1) : s.Characters.TryGetValue(p0, out int level) && level >= p1,
            23504 => s.Performs.TryGetValue(p0, out SkyTowerPerformState? perform) && perform.State == PerformFinish,
            _ => false
        };
    }
}
