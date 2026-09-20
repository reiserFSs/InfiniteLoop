using AscNet.Common;
using AscNet.Common.MsgPack;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.theatre4;
using AscNet.Table.V2.share.theatre4.theatre4map;
using GateConditionTable = AscNet.Table.V2.share.condition.ConditionTable;

namespace AscNet.GameServer.Handlers;

// Awakening Tundra (Theatre4) map slice: chapter/map lifecycle, locally generated grids,
// fog, exploration, content placement (box/shop/monster/event/boss) and traceback restore.
//
// EN client authority: XTheatre4Grid (Type/State/Color enums, GridId = 10000 + x*100 + y),
// XTheatre4MapSubControl (adjacency/range shapes), XEnumConst.Theatre4.
// The authored Block/BlockGrid/BlockGroup/BlockRelation/MapGrid tables are absent from the
// shipped data; every placement/distribution rule below is an AscNet Theatre4 local policy
// (deterministic from the persisted per-run RNG), never presented as retail behaviour.
internal static partial class Theatre4Module
{
    // EN XEnumConst.Theatre4.GridType / GridExploreState / ColorType / AssetType.
    internal static class T4Grid
    {
        public const int Nothing = 1, Empty = 2, Hurdle = 3, Shop = 4, Box = 5, Monster = 6,
            Boss = 7, Event = 8, Start = 9, Blank = 10, Building = 11;
    }

    internal static class T4State
    {
        public const int Unknown = 0, Visible = 1, Discover = 2, Explored = 3, Processed = 4;
    }

    internal static class T4Color { public const int Red = 1, Yellow = 2, Blue = 3; }

    internal static class T4Asset
    {
        public const int ItemBox = 1, Item = 2, Recruit = 3, Gold = 4, Hp = 5, Prosperity = 6,
            ColorLevel = 7, ColorResource = 8, ColorPoint = 9, BuildPoint = 10, ActionPoint = 11,
            ColorDailyResource = 12, ItemLimit = 13, SettleBpExp = 14, AwakeningPoint = 15,
            ColorCostPoint = 16, TimeBack = 17;
    }

    // EN XEnumConst.Theatre4.MapExploredCost.
    internal const int MapExploredCost = 1;

    // AscNet Theatre4 local policy: days a chapter boss waits before its punish strike.
    private const int BossPunishCountdownDays = 5;

    // AscNet Theatre4 local policy: share of a map's rectangle that becomes traversable.
    private const int MapFloorPercent = 62;

    // Wire GridId matches client XTheatre4Grid:GetGridId (10000 + x*100 + y). Internal cell keys
    // are the 0-based x*100+y form so decode is key/100, key%100.
    internal static int GridIdOf(int x, int y) => 10000 + x * 100 + y;
    private static int KeyOf(int x, int y) => x * 100 + y;

    internal static Theatre4ChapterData RequireChapter(Mutation m, int mapId) =>
        RequireAdventure(m).Chapters.FirstOrDefault(chapter => chapter.MapId == mapId)
        ?? throw new ServerCodeException("Theatre4 chapter is not active.", 20218017);

    internal static List<Theatre4GridData> AllGrids(Mutation m, int mapId) => RequireChapter(m, mapId).Grids;

    internal static Theatre4GridData? TryFindGrid(Mutation m, int mapId, int x, int y) =>
        RequireAdventure(m).Chapters.FirstOrDefault(chapter => chapter.MapId == mapId)?.Grids
            .FirstOrDefault(grid => grid.PosX == x && grid.PosY == y);

    internal static Theatre4GridData FindGrid(Mutation m, int mapId, int x, int y) =>
        TryFindGrid(m, mapId, x, y) ?? throw new ServerCodeException("Theatre4 grid does not exist.", 20218021);

    internal static bool TryMapSize(int mapId, out int width, out int height)
    {
        Theatre4MapTable? row = Rows<Theatre4MapTable>().FirstOrDefault(candidate => candidate.Id == mapId);
        width = row?.SizeX ?? 0;
        height = row?.SizeY ?? 0;
        return row is not null && width > 0 && height > 0;
    }

    //region adjacency and ranges (client XTheatre4MapSubControl shapes)

    internal static bool IsInside(int width, int height, int x, int y) =>
        x >= 0 && y >= 0 && x < width && y < height;

    internal static IEnumerable<(int X, int Y)> CrossOffsets(int radius)
    {
        // EN Directions order is right, up, left, down; each direction is walked
        // from near to far. Keep this order for deterministic target/effect selection.
        (int X, int Y)[] directions = [(1, 0), (0, 1), (-1, 0), (0, -1)];
        foreach ((int dx, int dy) in directions)
            for (int distance = 1; distance <= radius; distance++)
                yield return (dx * distance, dy * distance);
    }

    // Client DiamondPosPreset: distance-1 cross + corners, then distance-2 cross.
    private static readonly (int X, int Y)[] DiamondOffsets =
    [
        (1, 0), (0, 1), (-1, 0), (0, -1),
        (1, 1), (1, -1), (-1, 1), (-1, -1),
        (2, 0), (0, 2), (-2, 0), (0, -2)
    ];

    internal static IEnumerable<Theatre4GridData> CrossRange(Mutation m, int mapId, int x, int y, int radius)
    {
        if (!TryMapSize(mapId, out int width, out int height)) yield break;
        foreach ((int dx, int dy) in CrossOffsets(radius))
            if (TryFindGrid(m, mapId, x + dx, y + dy) is { } grid) yield return grid;
    }

    internal static IEnumerable<Theatre4GridData> DiamondRange(Mutation m, int mapId, int x, int y)
    {
        if (!TryMapSize(mapId, out int width, out int height)) yield break;
        foreach ((int dx, int dy) in DiamondOffsets)
            if (TryFindGrid(m, mapId, x + dx, y + dy) is { } grid) yield return grid;
    }

    // Client GetCubeRangeGridIds: the 3x3 ring around the anchor, in its perimeter order.
    internal static IEnumerable<Theatre4GridData> CubeRange(Mutation m, int mapId, int x, int y)
    {
        if (!TryMapSize(mapId, out int width, out int height)) yield break;
        (int dx, int dy)[] offsets =
        [
            (-1, -1), (0, -1), (1, -1), (1, 0),
            (1, 1), (0, 1), (-1, 1), (-1, 0)
        ];
        foreach ((int dx, int dy) in offsets)
            if (TryFindGrid(m, mapId, x + dx, y + dy) is { } grid) yield return grid;
    }

    //endregion

    //region chapter creation

    // Picks the concrete MapId row for a route step: weighted by BaseWeight plus the
    // AddWeight of every satisfied AddWeightCondition (EN Theatre4MapGroup semantics).
    // `excludeMapId` stops a chained step from re-entering the map it just cleared.
    // `allowBaseFallback` keeps an authored route step usable when its optional gates are unmet;
    // chained selection uses false so an ineligible substitution is never appended.
    private static Theatre4MapGroupTable? SelectMapGroupRow(Mutation m, int mapGroupKey, int excludeMapId = 0,
        bool allowBaseFallback = true)
    {
        List<Theatre4MapGroupTable> candidates = Rows<Theatre4MapGroupTable>()
            .Where(row => row.MapGroup == mapGroupKey && row.MapId != excludeMapId).ToList();
        if (candidates.Count == 0) return null;
        List<int> weights = candidates
            .Select(row => Math.Max(0, row.BaseWeight ?? 0)
                + (row.AddWeightCondition is > 0 && IsConditionMet(m, row.AddWeightCondition.Value)
                    ? Math.Max(0, row.AddWeight ?? 0) : 0))
            .ToList();
        int total = weights.Sum();
        if (total <= 0)
            return allowBaseFallback
                ? candidates.FirstOrDefault(row => row.Id == mapGroupKey) ?? candidates[0]
                : null;
        int roll = RandomIndex(m, total);
        for (int i = 0; i < candidates.Count; i++)
        {
            roll -= weights[i];
            if (roll < 0) return candidates[i];
        }
        return candidates[^1];
    }

    // Route step index: MapBlueprint.MapGroup[i] / Index[i]. AscNet Theatre4 local policy:
    // difficulty 1 uses the authored Beginner Route, other difficulties cycle the four
    // coloured routes; the route list is authoritative where authored.
    internal static int BlueprintIdForDifficulty(int difficulty) =>
        difficulty <= 1 ? 1 : 2 + (difficulty - 2) % 4;

    internal static int MapGroupForStep(Mutation m, int blueprintId, int step)
    {
        Theatre4MapBlueprintTable? blueprint = Rows<Theatre4MapBlueprintTable>()
            .FirstOrDefault(row => row.Id == blueprintId);
        if (blueprint is null) return 0;
        return step >= 0 && step < blueprint.MapGroup.Count && blueprint.MapGroup[step] > 0
            ? blueprint.MapGroup[step] : 0;
    }

    // Next chapter after clearing the current one: the authored route step, else the authored
    // MapGroup chain substitution. When neither yields an eligible row the route is finished
    // (EndAdventure) rather than forcing a final map by id.
    internal static Theatre4MapGroupTable? NextChapterRow(Mutation m, int blueprintId, int step, int lastMapId)
    {
        int key = MapGroupForStep(m, blueprintId, step);
        if (key != 0) return SelectMapGroupRow(m, key);
        return SelectMapGroupRow(m, lastMapId, excludeMapId: lastMapId, allowBaseFallback: false);
    }

    internal static Theatre4ChapterData InitializeMap(Mutation m, int mapGroupId)
    {
        Theatre4MapGroupTable row = SelectMapGroupRow(m, mapGroupId)
            ?? throw new ServerCodeException("Theatre4 map group has no authored map.", 20218017);
        return InitializeMapFromRow(m, row);
    }

    // Builds a chapter for an already-selected MapGroup row. Route advancement passes the exact
    // selected row so a chained substitution (e.g. MapGroup 60001 -> MapId 70001) is not re-rolled.
    private static Theatre4ChapterData InitializeMapFromRow(Mutation m, Theatre4MapGroupTable row)
    {
        Theatre4AdventureData adventure = RequireAdventure(m);
        Theatre4MapTable mapRow = Rows<Theatre4MapTable>().FirstOrDefault(candidate => candidate.Id == row.MapId)
            ?? throw new ServerCodeException("Theatre4 map is missing.", 20218017);
        Require(mapRow.SizeX > 0 && mapRow.SizeY > 0, 20218017);
        int width = mapRow.SizeX, height = mapRow.SizeY;
        int chapterIndex = adventure.Chapters.Count;

        // 1. Connected traversable blob grown randomly from a valid start so every floor cell is
        //    reachable; everything else is Nothing (1) and never drawn as walkable.
        // Authored closed regions are excluded before allocation, so content cannot be stranded
        // behind the client's hidden-grid filter.
        List<(HashSet<int> Cells, int ConditionId)> hiddenRegions = HiddenRegions(mapRow);
        HashSet<int> hiddenOpen = [];
        HashSet<int> hiddenClosed = [];
        foreach ((HashSet<int> cells, int conditionId) in hiddenRegions)
        {
            if (HiddenGateOpen(m, null, conditionId))
            {
                hiddenOpen.UnionWith(cells);
            }
            else
            {
                hiddenClosed.UnionWith(cells);
            }
        }
        HashSet<int> floor = GrowFloor(m, width, height, hiddenClosed);
        foreach ((HashSet<int> cells, int conditionId) in hiddenRegions)
            if (HiddenGateOpen(m, null, conditionId))
                CarveIntoFloor(floor, cells, width, height);
        int startKey = floor.Contains(0) ? 0 : floor.Min();
        Dictionary<int, int> distance = BreadthFirst(floor, width, startKey);
        // Boss must sit on the main floor, never inside a hidden bonus region (AscNet local policy).
        List<int> bossCandidates = floor.Where(key => !hiddenOpen.Contains(key)).ToList();
        if (bossCandidates.Count == 0) bossCandidates = floor.ToList();
        int bossKey = bossCandidates.OrderByDescending(key => distance.GetValueOrDefault(key))
            .ThenBy(key => key % 100).ThenBy(key => key / 100).First();

        // 2. Colour every floor cell from the authored per-map colour weights.
        int colorGroup = ColorGroupForChapter(chapterIndex);
        List<Theatre4ColorWeightTable> colorWeights = Rows<Theatre4ColorWeightTable>()
            .Where(color => color.GroupId == colorGroup).ToList();

        List<int> positions = floor.OrderBy(key => distance.GetValueOrDefault(key))
            .ThenBy(key => key).ToList();
        // Hidden cells stay clear of the random content plan; they are bonus space revealed by
        // their authored gate (AscNet local policy).
        List<int> contentSlots = positions.Where(key => key != startKey && key != bossKey
            && !hiddenOpen.Contains(key)).ToList();
        Shuffle(m, contentSlots);

        int eliteLimit = Math.Max(1, mapRow.EliteMonsterLimit ?? 1);
        int monsterSlots = Math.Max(2, contentSlots.Count * 15 / 100);
        int boxSlots = Math.Max(1, contentSlots.Count * 10 / 100);
        int eventSlots = Math.Max(1, contentSlots.Count * 12 / 100);
        int shopSlots = contentSlots.Count >= 12 ? 2 : 1;
        int hurdleSlots = Math.Min(3, contentSlots.Count / 12);
        // AscNet Theatre4 local policy: retain one processed Empty tile on every
        // sufficiently populated map so authored build skills always have a target.
        int emptyReserve = contentSlots.Count > 0 ? 1 : 0;
        int combatSlots = Math.Max(0, contentSlots.Count - emptyReserve
            - boxSlots - eventSlots - shopSlots - hurdleSlots);
        int eliteSlots = Math.Min(eliteLimit, combatSlots);
        int ordinarySlots = Math.Min(monsterSlots, combatSlots - eliteSlots);

        int slot = 0;
        int elites = 0;
        List<Theatre4GridData> grids = [];

        // 3. Start and boss. The boss must carry a playable authored fight so clearing it can pass
        //    the chapter; its ContentId is the Fight.Id used by the punish-effect lookup.
        grids.Add(BuildGrid(m, adventure, startKey, width, colorWeights, colorGroup, chapterIndex,
            T4Grid.Start, 0, 0, null, null, null));
        int bossGroup = BossGroupForChapter(row.MapId, chapterIndex);
        Theatre4FightData? bossFight = BuildMapFight(m, bossGroup, chapterIndex, boss: true,
            out int bossFightId, expectedFightType: 3);
        if (bossFight is null)
        {
            // Fallback (AscNet local policy): choose the first authored, playable boss fight.
            bossGroup = Rows<Theatre4FightGroupTable>()
                .GroupBy(candidate => candidate.GroupId)
                .Where(group => group.All(candidate => FightTypeOf(candidate.FightId) == 3))
                .Select(group => group.Key)
                .OrderBy(group => group)
                .FirstOrDefault();
            bossFight = BuildMapFight(m, bossGroup, chapterIndex, boss: true,
                out bossFightId, expectedFightType: 3);
        }
        Require(bossFight is not null, 20218017);
        Theatre4GridData boss = BuildGrid(m, adventure, bossKey, width, colorWeights, colorGroup, chapterIndex,
            T4Grid.Boss, bossGroup, bossFightId, bossFight, null, null);
        grids.Add(boss);

        void TakeSlot(Action<int> assign)
        {
            if (slot < contentSlots.Count - emptyReserve) assign(contentSlots[slot++]);
        }

        List<int> lowRiskReplacementGroups = ActiveLowRiskReplacementGroups(adventure);
        for (int i = 0; i < eliteSlots && slot < contentSlots.Count; i++, elites++)
            TakeSlot(key => grids.Add(BuildMonster(m, adventure, key, width, colorWeights, colorGroup,
                chapterIndex, chapterIndex, elite: true, replacementGroups: lowRiskReplacementGroups)));
        for (int i = 0; i < ordinarySlots; i++)
            TakeSlot(key => grids.Add(BuildMonster(m, adventure, key, width, colorWeights, colorGroup,
                chapterIndex, chapterIndex, elite: false, replacementGroups: lowRiskReplacementGroups)));
        for (int i = 0; i < boxSlots; i++)
            TakeSlot(key => grids.Add(BuildContent(m, adventure, key, width, colorWeights, colorGroup,
                chapterIndex, T4Grid.Box, PickBoxDrop(m))));
        for (int i = 0; i < eventSlots; i++)
            TakeSlot(key => grids.Add(BuildContent(m, adventure, key, width, colorWeights, colorGroup,
                chapterIndex, T4Grid.Event, PickEventGroup(m))));
        for (int i = 0; i < shopSlots; i++)
            TakeSlot(key => grids.Add(BuildContent(m, adventure, key, width, colorWeights, colorGroup,
                chapterIndex, T4Grid.Shop, PickShop(m))));
        for (int i = 0; i < hurdleSlots; i++)
            TakeSlot(key => grids.Add(BuildGrid(m, adventure, key, width, colorWeights, colorGroup,
                chapterIndex, T4Grid.Hurdle, 0, 0, null, null, null)));
        for (; slot < contentSlots.Count; slot++)
            grids.Add(BuildGrid(m, adventure, contentSlots[slot], width, colorWeights, colorGroup,
                chapterIndex, T4Grid.Empty, 0, 0, null, null, null));

        // Hidden cells keep a grid entry so RevealHiddenRegions can convert them in place: open
        // regions are walkable Empty, closed regions are Nothing and fog-blocked until revealed.
        foreach (int key in hiddenOpen)
            if (!grids.Any(grid => grid.PosX == key / 100 && grid.PosY == key % 100))
                grids.Add(BuildGrid(m, adventure, key, width, colorWeights, colorGroup, chapterIndex,
                    T4Grid.Empty, 0, 0, null, null, null));
        foreach (int key in hiddenClosed)
            if (!grids.Any(grid => grid.PosX == key / 100 && grid.PosY == key % 100))
                grids.Add(BuildGrid(m, adventure, key, width, colorWeights, colorGroup, chapterIndex,
                    T4Grid.Nothing, 0, 0, null, null, null));

        ChapterFog(grids, width, height, startKey, hiddenClosed);

        Theatre4ChapterData chapter = new()
        {
            MapGroup = row.MapGroup,
            MapId = row.MapId,
            Grids = grids,
            EliteCount = elites,
            IsPass = false,
            MaxTracebackDays = Rows<Theatre4TracebackTable>().FirstOrDefault(trace => trace.GroupId == mapRow.TracebackGroupId)
                ?.MaxDays ?? 3
        };
        adventure.Chapters.Add(chapter);
        if (row.Index is > 0) AddMapAtlas(m, row.Index.Value);
        if (row.SpEffectGroup > 0) AddEffectGroup(m, row.SpEffectGroup);
        m.Push(new NotifyTheatre4AddChapter { Chapter = Clone(chapter) });
        // A chapter with no ordinary monster tiles satisfies the absent-row gate vacuously; open
        // those regions now so they are explorable rather than stranded.
        RevealHiddenRegions(m, chapter);
        return chapter;
    }

    private static HashSet<int> GrowFloor(Mutation m, int width, int height, HashSet<int> blocked)
    {
        int startKey = Enumerable.Range(0, width * height).First(key => !blocked.Contains(key));
        HashSet<int> floor = [startKey];
        List<int> frontier = [];
        void Offer(int key)
        {
            if (!blocked.Contains(key) && !floor.Contains(key) && !frontier.Contains(key)) frontier.Add(key);
        }
        void OfferNeighbours(int key)
        {
            int x = key / 100, y = key % 100;
            if (x > 0) Offer(KeyOf(x - 1, y));
            if (x < width - 1) Offer(KeyOf(x + 1, y));
            if (y > 0) Offer(KeyOf(x, y - 1));
            if (y < height - 1) Offer(KeyOf(x, y + 1));
        }
        OfferNeighbours(startKey);
        int target = Math.Max(4, width * height * MapFloorPercent / 100);
        while (floor.Count < target && frontier.Count > 0)
        {
            int index = RandomIndex(m, frontier.Count);
            int key = frontier[index];
            frontier.RemoveAt(index);
            if (!floor.Add(key)) continue;
            OfferNeighbours(key);
        }
        return floor;
    }

    private static Dictionary<int, int> BreadthFirst(HashSet<int> floor, int width, int startKey)
    {
        Dictionary<int, int> distance = new() { [startKey] = 0 };
        Queue<int> queue = new();
        queue.Enqueue(startKey);
        while (queue.Count > 0)
        {
            int key = queue.Dequeue();
            int x = key / 100, y = key % 100;
            foreach ((int nx, int ny) in new[] { (x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1) })
            {
                if (nx < 0 || ny < 0 || nx >= width) continue;
                int next = KeyOf(nx, ny);
                if (!floor.Contains(next) || distance.ContainsKey(next)) continue;
                distance[next] = distance[key] + 1;
                queue.Enqueue(next);
            }
        }
        return distance;
    }

    private static void Shuffle(Mutation m, List<int> values)
    {
        for (int i = values.Count - 1; i > 0; i--)
        {
            int j = RandomIndex(m, i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }
    }

    // AscNet Theatre4 local policy: chapter 1..6 use ColourWeight groups 10002..10007 and
    // later/extra chapters keep the last authored group. Colours themselves stay authored.
    private static int ColorGroupForChapter(int chapterIndex)
    {
        int group = 10001 + Math.Clamp(chapterIndex + 1, 1, 6);
        return Rows<Theatre4ColorWeightTable>().Any(row => row.GroupId == group) ? group : 10001;
    }

    private static int ResourceGroupForChapter(int chapterIndex)
    {
        int group = 10001 + Math.Clamp(chapterIndex + 1, 1, 6);
        return Rows<Theatre4ColorResourceTable>().Any(row => row.GroupId == group) ? group : 10001;
    }

    private static Theatre4GridData BuildGrid(Mutation m, Theatre4AdventureData adventure, int key, int width,
        List<Theatre4ColorWeightTable> colorWeights, int colorGroup, int chapterIndex, int type,
        int contentGroup, int contentId, Theatre4FightData? fight, Theatre4ShopData? shop, Theatre4EventData? evt)
    {
        int x = key / 100, y = key % 100;
        int color = type == T4Grid.Start ? 0 : RollColor(m, colorWeights);
        return new Theatre4GridData
        {
            GridId = GridIdOf(x, y),
            Color = color,
            ColorResource = color == 0 ? 0 : ColorResourceAmount(colorGroup, chapterIndex, color),
            Type = type,
            PosX = x,
            PosY = y,
            State = T4State.Unknown,
            ContentGroup = contentGroup,
            ContentId = contentId,
            DisabledDay = 0,
            Fight = fight,
            Shop = shop,
            Event = evt
        };
    }

    private static int RollColor(Mutation m, List<Theatre4ColorWeightTable> colorWeights)
    {
        if (colorWeights.Count == 0) return T4Color.Red;
        List<int> weights = colorWeights.Select(row => Math.Max(0, row.Weight
            + (row.AddWeightCondition is > 0 && IsConditionMet(m, row.AddWeightCondition.Value)
                ? row.AddWeight ?? 0 : 0))).ToList();
        int total = weights.Sum();
        if (total <= 0) return colorWeights[0].Color;
        int roll = RandomIndex(m, total);
        for (int i = 0; i < colorWeights.Count; i++)
        {
            roll -= weights[i];
            if (roll < 0) return colorWeights[i].Color;
        }
        return colorWeights[^1].Color;
    }

    private static int ColorResourceAmount(int colorGroup, int chapterIndex, int color)
    {
        int group = ResourceGroupForChapter(chapterIndex);
        Theatre4ColorResourceTable? row = Rows<Theatre4ColorResourceTable>()
            .FirstOrDefault(candidate => candidate.GroupId == group && candidate.Color == color)
            ?? Rows<Theatre4ColorResourceTable>().FirstOrDefault(candidate => candidate.GroupId == 10001 && candidate.Color == color);
        return row is null ? 0 : row.Resource.FirstOrDefault();
    }

    private static Theatre4GridData BuildMonster(Mutation m, Theatre4AdventureData adventure, int key, int width,
        List<Theatre4ColorWeightTable> colorWeights, int colorGroup, int chapterIndex, int chapterStep,
        bool elite, IReadOnlyList<int> replacementGroups)
    {
        int group = AuthoredMonsterGroup(chapterStep, elite);
        int fightId = 0;
        Theatre4FightData? fight = group > 0
            ? BuildMapFight(m, group, chapterIndex, boss: false, out fightId,
                expectedFightType: elite ? 2 : 1)
            : null;
        if (!elite)
        {
            foreach (int replacementGroup in ReplacementGroupsFor(group, replacementGroups))
            {
                int replacementFightId = 0;
                Theatre4FightData? replacementFight = BuildMapFight(m, replacementGroup, chapterIndex,
                    boss: false, out replacementFightId, expectedFightType: 1);
                if (replacementFight is null) continue;
                group = replacementGroup;
                fightId = replacementFightId;
                fight = replacementFight;
                break;
            }
        }
        // Authored fight data absent: leave a plain empty tile rather than an unplayable battle.
        if (fight is null)
            return BuildGrid(m, adventure, key, width, colorWeights, colorGroup, chapterIndex,
                T4Grid.Empty, 0, 0, null, null, null);
        return BuildGrid(m, adventure, key, width, colorWeights, colorGroup, chapterIndex,
            T4Grid.Monster, group, fightId, fight, null, null);
    }

    // AscNet policy: EN fight groups expose no chapter key; suffix 101/1 is the
    // local normal/elite partition used to keep generated content playable.
    private static int AuthoredMonsterGroup(int chapterStep, bool elite)
    {
        int fightType = elite ? 2 : 1;
        int suffix = elite ? 1 : 101;
        List<int> groups = Rows<Theatre4FightGroupTable>()
            .GroupBy(row => row.GroupId)
            .Where(group => group.Key % 1000 == suffix
                && group.All(row => FightTypeOf(row.FightId) == fightType))
            .Select(group => group.Key)
            .OrderBy(group => group)
            .ToList();
        return groups.Count == 0 ? 0 : groups[Math.Clamp(chapterStep, 0, groups.Count - 1)];
    }


    private static int FightTypeOf(int fightId) =>
        Rows<Theatre4FightTable>().FirstOrDefault(row => row.Id == fightId)?.FightType ?? 0;


    private static List<int> ActiveLowRiskReplacementGroups(Theatre4AdventureData adventure)
    {
        foreach (Theatre4EffectData effect in EnumerateEffects(adventure))
        {
            Theatre4EffectTable? row = Rows<Theatre4EffectTable>()
                .FirstOrDefault(candidate => candidate.Id == effect.EffectId);
            if (row?.Type == 216) return row.Params?.ToList() ?? [];
        }
        return [];
    }

    // Type216 (talent 216) replaces ordinary FightType 1 encounters on the active map.
    // A grid is frozen only once FightLocate has created the matching active encounter.
    internal static bool ApplyLowRiskEnemyReplacements(Mutation m, Theatre4AdventureData adventure,
        IReadOnlyList<int> replacementGroups)
    {
        if (replacementGroups.Count == 0) return false;
        Theatre4ChapterData? chapter = adventure.Chapters.LastOrDefault(candidate => !candidate.IsPass);
        if (chapter is null) return false;

        List<Theatre4GridData> changed = [];
        foreach (Theatre4GridData grid in chapter.Grids
            .Where(candidate => IsUnresolvedLowRiskMonster(m, chapter, candidate, replacementGroups)))
        {
            foreach (int replacementGroup in ReplacementGroupsFor(grid.ContentGroup, replacementGroups))
            {
                Theatre4FightData? replacement = BuildMapFight(m, replacementGroup,
                    adventure.Chapters.IndexOf(chapter), boss: false, out int fightId,
                    expectedFightType: 1);
                if (replacement is null) continue;
                grid.ContentGroup = replacementGroup;
                grid.ContentId = fightId;
                grid.Fight = replacement;
                changed.Add(grid);
                break;
            }
        }
        if (changed.Count == 0) return false;
        m.Push(new NotifyTheatre4ChangeGrids
        {
            MapId = chapter.MapId, Grids = changed.Select(Clone).ToList()
        });
        return true;
    }

    // AscNet policy: EN has no rule mapping Type216 params to chapter groups;
    // numeric family proximity is a deterministic fallback, never an authored ID contract.
    private static IEnumerable<int> ReplacementGroupsFor(int sourceGroup,
        IReadOnlyList<int> replacementGroups)
    {
        int family = sourceGroup > 0 ? sourceGroup / 1000 : 0;
        return replacementGroups.Where(group => group > 0).Distinct()
            .OrderBy(group => sourceGroup > 0 ? Math.Abs(group / 1000 - family) : 0)
            .ThenBy(group => group);
    }

    private static bool IsUnresolvedLowRiskMonster(Mutation m, Theatre4ChapterData chapter,
        Theatre4GridData grid, IReadOnlyList<int> replacementGroups)
    {
        if (grid.Type != T4Grid.Monster || grid.Fight is null || grid.State == T4State.Processed
            || replacementGroups.Contains(grid.ContentGroup)
            || FightTypeOf(grid.ContentId) != 1)
            return false;
        if (m.State.ActiveEncounter is { MapId: > 0 } encounter
            && encounter.MapId == chapter.MapId && encounter.PosX == grid.PosX
            && encounter.PosY == grid.PosY)
            return false;
        return true;
    }

    private static Theatre4GridData BuildContent(Mutation m, Theatre4AdventureData adventure, int key, int width,
        List<Theatre4ColorWeightTable> colorWeights, int colorGroup, int chapterIndex, int type, int roll)
    {
        Theatre4GridData grid = BuildGrid(m, adventure, key, width, colorWeights, colorGroup, chapterIndex,
            type, 0, 0, null, null, null);
        switch (type)
        {
            case T4Grid.Box:
            {
                int group = roll;
                List<Theatre4BoxGroupTable> rows = Rows<Theatre4BoxGroupTable>()
                    .Where(row => row.GroupId == group).ToList();
                Theatre4BoxGroupTable? chosen = PickWeighted(m, rows, row => row.Weight);
                if (chosen is null) { grid.Type = T4Grid.Empty; break; }
                grid.ContentGroup = chosen.GroupId;
                grid.ContentId = chosen.Id;
                if (!adventure.CreatedBoxGroupIds.Contains(chosen.GroupId)) adventure.CreatedBoxGroupIds.Add(chosen.GroupId);
                break;
            }
            case T4Grid.Event:
            {
                List<Theatre4EventGroupTable> rows = Rows<Theatre4EventGroupTable>()
                    .Where(row => row.GroupId == roll
                        && (row.ConditionId is not > 0 || IsConditionMet(m, row.ConditionId.Value)))
                    .ToList();
                Theatre4EventGroupTable? chosen = PickWeighted(m, rows, row => (row.Weight ?? 0)
                    + row.AddWeight.Select((weight, index) => row.AddWeightCondition.Count > index
                        && row.AddWeightCondition[index] is > 0 && IsConditionMet(m, row.AddWeightCondition[index]) ? weight : 0).Sum());
                if (chosen is null) { grid.Type = T4Grid.Empty; break; }
                grid.ContentGroup = chosen.GroupId;
                grid.ContentId = chosen.EventId;
                grid.Event = BuildMapEvent(m, chosen.EventId);
                if (grid.Event is null) { grid.Type = T4Grid.Empty; grid.ContentGroup = 0; grid.ContentId = 0; break; }
                if (!adventure.CreatedEventGroupIds.Contains(chosen.GroupId)) adventure.CreatedEventGroupIds.Add(chosen.GroupId);
                break;
            }
            case T4Grid.Shop:
            {
                List<Theatre4ShopGroupTable> rows = Rows<Theatre4ShopGroupTable>()
                    .Where(row => row.ShopGroupId == roll).ToList();
                Theatre4ShopGroupTable? chosen = PickWeighted(m, rows, row => row.Weight);
                if (chosen is null) { grid.Type = T4Grid.Empty; break; }
                grid.ContentGroup = chosen.ShopGroupId;
                grid.ContentId = chosen.ShopId;
                // EN XTheatre4Grid materialises Shop only after the tile is flipped.
                // ExploreGrid hydrates it immediately before returning the processed tile.
                if (!Rows<Theatre4ShopTable>().Any(row => row.Id == chosen.ShopId))
                {
                    grid.Type = T4Grid.Empty;
                    grid.ContentGroup = 0;
                    grid.ContentId = 0;
                    break;
                }
                if (!adventure.CreatedShopGroupIds.Contains(chosen.ShopGroupId)) adventure.CreatedShopGroupIds.Add(chosen.ShopGroupId);
                break;
            }
        }
        return grid;
    }

    private static int PickBoxDrop(Mutation m)
    {
        List<int> groups = Rows<Theatre4BoxGroupTable>().Select(row => row.GroupId).Distinct().Where(id => id > 0).ToList();
        return groups.Count == 0 ? 1 : groups[RandomIndex(m, groups.Count)];
    }

    private static int PickEventGroup(Mutation m)
    {
        List<int> groups = Rows<Theatre4EventGroupTable>()
            .Where(row => row.ConditionId is not > 0)
            .Select(row => row.GroupId).Distinct().Where(id => id > 0).ToList();
        return groups.Count == 0 ? 1 : groups[RandomIndex(m, groups.Count)];
    }

    private static int PickShop(Mutation m)
    {
        List<int> groups = Rows<Theatre4ShopGroupTable>().Select(row => row.ShopGroupId).Distinct()
            .Where(id => id > 0).ToList();
        return groups.Count == 0 ? 1 : groups[RandomIndex(m, groups.Count)];
    }

    private static T? PickWeighted<T>(Mutation m, List<T> rows, Func<T, int> weight) where T : class
    {
        List<int> weights = rows.Select(row => Math.Max(0, weight(row))).ToList();
        int total = weights.Sum();
        if (rows.Count == 0) return null;
        if (total <= 0) return rows[RandomIndex(m, rows.Count)];
        int roll = RandomIndex(m, total);
        for (int i = 0; i < rows.Count; i++)
        {
            roll -= weights[i];
            if (roll < 0) return rows[i];
        }
        return rows[^1];
    }

    // AscNet Theatre4 local policy: boss fight group per route step; the authored final maps use
    // their own 7001/7002 boss groups, otherwise the chapter-indexed group list.
    private static int BossGroupForChapter(int mapId, int chapterIndex)
    {
        int[] groups = [301, 302, 401, 402, 403, 404];
        if (mapId == 70001) return 7001;
        if (mapId == 70002) return 7002;
        int candidate = groups[Math.Clamp(chapterIndex, 0, groups.Length - 1)];
        return Rows<Theatre4FightGroupTable>().Any(row => row.GroupId == candidate) ? candidate : 301;
    }

    internal static Theatre4FightData? BuildMapFight(Mutation m, int groupId, int chapterIndex, bool boss,
        out int fightId, int expectedFightType = 0)
    {
        fightId = 0;
        int difficulty = RequireAdventure(m).Difficulty;
        List<Theatre4FightGroupTable> rows = Rows<Theatre4FightGroupTable>()
            .Where(row => row.GroupId == groupId
                && row.Difficult.Count >= difficulty && row.Difficult[difficulty - 1] > 0
                && (row.Condition is not > 0 || IsConditionMet(m, row.Condition.Value))
                && (expectedFightType <= 0 || FightTypeOf(row.FightId) == expectedFightType))
            .ToList();
        Theatre4FightGroupTable? chosen = PickWeighted(m, rows, row => Math.Max(0, row.Weight ?? 0));
        if (chosen is null) return null;
        Theatre4FightTable? fight = Rows<Theatre4FightTable>().FirstOrDefault(row => row.Id == chosen.FightId);
        if (fight is null) return null;
        Theatre4FightMoldTable? mold = Rows<Theatre4FightMoldTable>().FirstOrDefault(row => row.Id == fight.MoldId);
        int stageId = mold?.StageId.FirstOrDefault(id => id > 0) ?? 0;
        // Combat receives the authored FightGroup row Id; grid.ContentGroup retains the selection bucket.
        if (stageId <= 0) return null;
        fightId = fight.Id;
        return new Theatre4FightData
        {
            FightGroupId = chosen.Id,
            StageId = stageId,
            HpPercent = 10000,
            PunishCountdown = boss ? BossPunishCountdownDays : -1,
            FightEvents = mold!.FightEvents.Where(id => id > 0).ToList(),
            Rewards = []
        };
    }

    internal static Theatre4EventData? BuildMapEvent(Mutation m, int eventId)
    {
        Theatre4EventTable? row = Rows<Theatre4EventTable>().FirstOrDefault(candidate => candidate.Id == eventId);
        if (row is null) return null;
        int stageId = 0;
        if (row.FightId is > 0 && Rows<Theatre4FightTable>().FirstOrDefault(fight => fight.Id == row.FightId) is { } fight
            && Rows<Theatre4FightMoldTable>().FirstOrDefault(mold => mold.Id == fight.MoldId) is { } mold)
            stageId = mold.StageId.FirstOrDefault(id => id > 0);
        // A fight event without a resolvable stage can never be entered: treat it as unauthored.
        if (row.Type == 4 && stageId <= 0) return null;
        return new Theatre4EventData { EventId = row.Id, StageId = stageId, StageScore = 0 };
    }

    internal static Theatre4ShopData? BuildMapShop(Mutation m, int shopId)
    {

        Theatre4ShopTable? row = Rows<Theatre4ShopTable>().FirstOrDefault(candidate => candidate.Id == shopId);
        if (row is null) return null;
        Theatre4ShopData shop = new() { ShopId = shopId, RefreshTimes = 0, FreeBuyTimes = 0, Discount = 0 };
        // Economy owns the shelf: authored goods, stock, effect discount offset and free column.
        InitializeShop(m, shop);
        return shop;
    }

    // AscNet policy: directly created, processed shops receive their shelf and entry effects together.
    internal static bool InitializeShopGrid(Mutation m, Theatre4GridData grid, Theatre4ShopGroupTable shopGroup)
    {
        if (shopGroup.ShopGroupId <= 0 || shopGroup.ShopId <= 0) return false;
        Theatre4AdventureData adventure = RequireAdventure(m);
        Theatre4ShopData? shop = BuildMapShop(m, shopGroup.ShopId);
        if (shop is null) return false;
        grid.Type = T4Grid.Shop;
        grid.State = T4State.Processed;
        grid.ContentGroup = shopGroup.ShopGroupId;
        grid.ContentId = shopGroup.ShopId;
        grid.DisabledDay = 0;
        grid.Fight = null;
        grid.Event = null;
        grid.Building = null;
        grid.Shop = shop;
        if (!adventure.CreatedShopGroupIds.Contains(shopGroup.ShopGroupId))
            adventure.CreatedShopGroupIds.Add(shopGroup.ShopGroupId);
        TriggerEffects(m, "shop", grid, 0);
        return true;
    }
    // Type415 (talent 232) immediately turns an authored Blank/completed tile into the
    // yellow-talent shop; a safe unused cell is allocated only when no such tile exists.
    internal static bool TryUnlockTalentShop(Mutation m, Theatre4AdventureData adventure, int shopId)
    {
        if (shopId <= 0) return false;
        Theatre4ChapterData? chapter = adventure.Chapters.LastOrDefault(candidate => !candidate.IsPass);
        if (chapter is null) return false;

        Theatre4ShopGroupTable? shopGroup = Rows<Theatre4ShopGroupTable>()
            .Where(row => row.ShopId == shopId)
            .OrderBy(row => row.Id)
            .FirstOrDefault();
        if (shopGroup is null || shopGroup.ShopGroupId <= 0) return false;


        // Native Blank tiles are either Unknown or Processed; Empty means a completed
        // tile. Never consume a merely discoverable Empty tile for this passive unlock.
        Theatre4GridData? grid = chapter.Grids
            .Where(candidate => (candidate.Type == T4Grid.Blank
                    && candidate.State is T4State.Unknown or T4State.Processed)
                || (candidate.Type == T4Grid.Empty && candidate.State == T4State.Processed))
            .OrderBy(candidate => candidate.Type == T4Grid.Blank ? 0 : 1)
            .ThenBy(candidate => candidate.GridId)
            .FirstOrDefault();
        if (grid is null && TryMapSize(chapter.MapId, out int width, out int height))
        {
            HashSet<int> blocked = ClosedHiddenCells(m, chapter);
            grid = chapter.Grids
                .Where(candidate => candidate.Type == T4Grid.Nothing
                    && IsInside(width, height, candidate.PosX, candidate.PosY)
                    && !blocked.Contains(KeyOf(candidate.PosX, candidate.PosY)))
                .OrderBy(candidate => candidate.GridId)
                .FirstOrDefault(candidate => chapter.Grids.Any(neighbour =>
                    neighbour.Type != T4Grid.Nothing
                    && (neighbour.State is T4State.Explored or T4State.Processed or T4State.Discover)
                    && Math.Abs(neighbour.PosX - candidate.PosX)
                        + Math.Abs(neighbour.PosY - candidate.PosY) == 1));
            if (grid is not null)
            {
                Theatre4GridData neighbour = chapter.Grids
                    .Where(candidate => candidate.Type != T4Grid.Nothing
                        && (candidate.State is T4State.Explored or T4State.Processed or T4State.Discover)
                        && Math.Abs(candidate.PosX - grid.PosX)
                            + Math.Abs(candidate.PosY - grid.PosY) == 1)
                    .OrderBy(candidate => candidate.GridId)
                    .First();
                int chapterIndex = Math.Max(0, adventure.Chapters.IndexOf(chapter));
                grid.Color = neighbour.Color is >= T4Color.Red and <= T4Color.Blue
                    ? neighbour.Color : T4Color.Red;
                grid.ColorResource = ColorResourceAmount(ColorGroupForChapter(chapterIndex),
                    chapterIndex, grid.Color);
            }
        }
        if (grid is null) return false;

        if (!InitializeShopGrid(m, grid, shopGroup)) return false;
        m.Push(new NotifyTheatre4ChangeGrids { MapId = chapter.MapId, Grids = [Clone(grid)] });
        return true;
    }

    // Authored ShopGoods rows per authored goods group; AscNet sorts nothing, the client
    // keeps the server order. Economy may reuse this for refresh.
    internal static List<Theatre4ShopGoodsData> BuildShopGoods(Mutation m, List<int> goodsGroupIds)
    {
        List<Theatre4ShopGoodsData> goods = [];
        HashSet<int> groups = goodsGroupIds.Where(id => id > 0).ToHashSet();
        foreach (Theatre4ShopGoodsTable row in Rows<Theatre4ShopGoodsTable>()
            .Where(row => groups.Contains(row.GroupId)
                && !row.ConditionId.Where(id => id > 0).Any(id => !IsConditionMet(m, id))))
            goods.Add(new Theatre4ShopGoodsData { GoodsId = row.Id, Stock = row.GoodsNum, IsFree = false });
        // AscNet Theatre4 local policy: keep authored rows in table order, cap the shelf at 6.
        return goods.Take(6).ToList();
    }

    private static void ChapterFog(List<Theatre4GridData> grids, int width, int height, int startKey,
        HashSet<int>? blocked)
    {
        Theatre4GridData start = grids.First(grid => grid.PosX == startKey / 100 && grid.PosY == startKey % 100);
        start.State = T4State.Explored;
        RevealNeighbours(grids, width, height, start.PosX, start.PosY, null, blocked);
    }

    // Orthogonal neighbours are explorable (Discover); diagonals and 2-out are only Visible.
    // A Visible tile is promoted to Discover once it gains an orthogonal explored neighbour, so
    // the frontier keeps advancing and the whole connected floor is eventually reachable.
    // Changed grids are appended to `changed` when supplied so callers can push NotifyChangeGrids.
    // Cells in `blocked` are closed hidden regions and are never revealed.
    private static void RevealNeighbours(List<Theatre4GridData> grids, int width, int height, int x, int y,
        List<Theatre4GridData>? changed, HashSet<int>? blocked)
    {
        foreach ((int dx, int dy) in DiamondOffsets)
        {
            int nx = x + dx, ny = y + dy;
            if (!IsInside(width, height, nx, ny)) continue;
            if (blocked is not null && blocked.Contains(KeyOf(nx, ny))) continue;
            Theatre4GridData? grid = grids.FirstOrDefault(candidate => candidate.PosX == nx && candidate.PosY == ny);
            if (grid is null || grid.State is T4State.Explored or T4State.Processed or T4State.Discover) continue;
            bool orthogonal = Math.Abs(dx) + Math.Abs(dy) == 1;
            if (!orthogonal && grid.State == T4State.Visible) continue;
            grid.State = orthogonal ? T4State.Discover : T4State.Visible;
            changed?.Add(grid);
        }
    }

    //endregion

    //region exploration

    [RequestPacketHandler("Theatre4ExploreGridRequest")]
    public static void Theatre4ExploreGrid(Session session, Packet.Request packet) =>
        Handle<Theatre4ExploreGridRequest, Theatre4ExploreGridResponse>(session, packet, (m, request, response) =>
        {
            EnsureAvailable(session);
            Theatre4ExploreGridResponse result = ExploreGrid(m, request);
            response.Grid = result.Grid;
            response.OtherGrids = result.OtherGrids;
        });

    internal static Theatre4ExploreGridResponse ExploreGrid(Mutation m, Theatre4ExploreGridRequest request)
    {
        Theatre4AdventureData adventure = RequireAdventure(m);
        Theatre4ChapterData chapter = RequireChapter(m, request.MapId);
        Theatre4GridData grid = FindGrid(m, request.MapId, request.PosX, request.PosY);
        return ExploreGridCore(m, adventure, chapter, grid, request.MapId, chargeActionPoint: true,
            pushGridAndAdventure: false);
    }

    // Building effects auto-explore a selected target without charging AP or requiring Discover.
    // Selection remains in Effects so Type103 can use nearest-area and Type104 can use Bonfire range.
    internal static bool ExploreGridFromBuilding(Mutation m, Theatre4GridData target)
    {
        Theatre4AdventureData adventure = RequireAdventure(m);
        Theatre4ChapterData? chapter = adventure.Chapters
            .LastOrDefault(candidate => candidate.Grids.Any(grid => ReferenceEquals(grid, target)));
        if (chapter is null || chapter.IsPass || !TryMapSize(chapter.MapId, out int width, out int height))
            return false;
        HashSet<int> blocked = ClosedHiddenCells(m, chapter);
        if (target.Type == T4Grid.Nothing || target.State >= T4State.Explored
            || !IsInside(width, height, target.PosX, target.PosY)
            || blocked.Contains(KeyOf(target.PosX, target.PosY)))
            return false;
        ExploreGridCore(m, adventure, chapter, target, chapter.MapId, chargeActionPoint: false,
            pushGridAndAdventure: true);
        return true;
    }

    private static Theatre4ExploreGridResponse ExploreGridCore(Mutation m, Theatre4AdventureData adventure,
        Theatre4ChapterData chapter, Theatre4GridData grid, int mapId, bool chargeActionPoint,
        bool pushGridAndAdventure)
    {
        Require(!chapter.IsPass, 20218018);
        if (chargeActionPoint)
        {
            Require(grid.Type != T4Grid.Nothing && grid.State == T4State.Discover, 20218018);
            Require(AssetCount(m, T4Asset.ActionPoint, 0) >= MapExploredCost, 20218020);
            SpendAsset(m, T4Asset.ActionPoint, 0, MapExploredCost);
        }
        else
        {
            Require(TryMapSize(mapId, out int autoWidth, out int autoHeight)
                && IsInside(autoWidth, autoHeight, grid.PosX, grid.PosY), 20218018);
            Require(!ClosedHiddenCells(m, chapter).Contains(KeyOf(grid.PosX, grid.PosY)), 20218018);
        }

        grid.State = T4State.Explored;
        adventure.ExploreCount = checked(adventure.ExploreCount + 1);
        UpsertDailyExplorePos(adventure, mapId, grid.PosX, grid.PosY);
        if (grid.Color > 0)
            adventure.DailyExploreColors.Add(grid.Color);
        List<Theatre4GridData> changed = [];
        if (TryMapSize(mapId, out int width, out int height))
            RevealNeighbours(chapter.Grids, width, height, grid.PosX, grid.PosY, changed,
                ClosedHiddenCells(m, chapter));

        // EN XTheatre4BaseGrid opens a shop only from the processed state. Its
        // shelf is generated at discovery, not in the hidden chapter snapshot.
        if (grid.Type == T4Grid.Shop)
        {
            grid.Shop ??= BuildMapShop(m, grid.ContentId)
                ?? throw new ServerCodeException("Theatre4 shop is not authored.", 20218017);
            if (grid.Shop.Goods.Count == 0) InitializeShop(m, grid.Shop);
            grid.State = T4State.Processed;
        }
        else if (grid.Type == T4Grid.Empty)
        {
            grid.State = T4State.Processed;
        }
        // Effects may alter the just-opened tile and its neighbours.
        TriggerEffects(m, "explore", grid);
        if (grid.ColorResource > 0 && grid.Color > 0)
            AddAsset(m, T4Asset.ColorResource, grid.Color, grid.ColorResource);
        // First discovery of a shop is the authored "enter shop" moment (Type217 entry bonus).
        // Effects keeps a per-shop guard so a later refresh (also value 0) cannot mint it twice.
        if (grid.Type == T4Grid.Shop) TriggerEffects(m, "shop", grid, 0);
        // A chest resolves on open: Economy grants the drop transaction and marks the tile processed.
        if (grid.Type == T4Grid.Box) OpenGridBox(m, grid);
        adventure.PreExplorePos = new Theatre4PosData { MapId = mapId, PosX = grid.PosX, PosY = grid.PosY };
        AddDelta(chapter, grid);

        List<Theatre4GridData> pushes = changed;
        if (pushGridAndAdventure)
        {
            pushes = [grid];
            pushes.AddRange(changed.Where(candidate => !ReferenceEquals(candidate, grid)));
        }
        m.Push(new NotifyTheatre4ChangeGrids { MapId = mapId, Grids = pushes.Select(Clone).ToList() });
        if (pushGridAndAdventure)
            m.Push(new NotifyTheatre4AdventureData { AdventureData = Clone(adventure) });
        return new Theatre4ExploreGridResponse { Grid = grid, OtherGrids = changed };
    }

    private static void UpsertDailyExplorePos(Theatre4AdventureData adventure, int mapId, int x, int y)
    {
        if (!adventure.DailyExplorePosSet.Any(candidate =>
            candidate.MapId == mapId && candidate.PosX == x && candidate.PosY == y))
            adventure.DailyExplorePosSet.Add(new Theatre4PosData { MapId = mapId, PosX = x, PosY = y });
    }

    private static void AddDelta(Theatre4ChapterData chapter, Theatre4GridData grid)
    {
        // Chapter grids are the durable store; replaced entries flow back through ChangeGrids.
        int index = chapter.Grids.FindIndex(candidate => candidate.PosX == grid.PosX && candidate.PosY == grid.PosY);
        if (index >= 0) chapter.Grids[index] = grid;
    }

    //endregion

    //region hidden regions (Theatre4Map.HiddenId -> Theatre4MapHiddenGrid)
    // Additive recovery for persisted maps generated before the build-plot reservation. It only
    // repurposes an existing unused Nothing cell; authored/content grids are never replaced.
    internal static bool RepairLegacyBuildPlot(Mutation m, Theatre4AdventureData adventure,
        Theatre4ChapterData chapter)
    {
        if (chapter.IsPass
            || chapter.Grids.Any(grid => grid.Type == T4Grid.Empty)
            || chapter.Grids.Any(grid => grid.Type == T4Grid.Building))
            return false;
        if (!TryMapSize(chapter.MapId, out int width, out int height)) return false;
        HashSet<int> blocked = ClosedHiddenCells(m, chapter);
        Theatre4GridData? candidate = chapter.Grids
            .Where(grid => grid.Type == T4Grid.Nothing
                && IsInside(width, height, grid.PosX, grid.PosY)
                && !blocked.Contains(KeyOf(grid.PosX, grid.PosY)))
            .OrderBy(grid => grid.GridId)
            .FirstOrDefault(grid => chapter.Grids.Any(neighbour =>
                neighbour.Type != T4Grid.Nothing
                && (neighbour.State is T4State.Explored or T4State.Processed or T4State.Discover)
                && Math.Abs(neighbour.PosX - grid.PosX) + Math.Abs(neighbour.PosY - grid.PosY) == 1));
        if (candidate is null) return false;

        Theatre4GridData neighbour = chapter.Grids
            .Where(grid => grid.Type != T4Grid.Nothing
                && (grid.State is T4State.Explored or T4State.Processed or T4State.Discover)
                && Math.Abs(grid.PosX - candidate.PosX) + Math.Abs(grid.PosY - candidate.PosY) == 1)
            .OrderBy(grid => grid.GridId)
            .First();
        int chapterIndex = Math.Max(0, adventure.Chapters.IndexOf(chapter));
        int color = neighbour.Color is >= T4Color.Red and <= T4Color.Blue ? neighbour.Color : T4Color.Red;
        candidate.Type = T4Grid.Empty;
        candidate.State = T4State.Discover;
        candidate.Color = color;
        candidate.ColorResource = ColorResourceAmount(ColorGroupForChapter(chapterIndex), chapterIndex, color);
        candidate.ContentGroup = 0;
        candidate.ContentId = 0;
        candidate.DisabledDay = 0;
        candidate.Fight = null;
        candidate.Shop = null;
        candidate.Event = null;
        return true;
    }


    // Parses an authored "x,y" / "x,y|..." position string from Theatre4MapHiddenGrid.Point/GridPos.
    private static IEnumerable<(int X, int Y)> ParseGridPositions(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) yield break;
        foreach (string part in value.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] xy = part.Split(',');
            if (xy.Length == 2 && int.TryParse(xy[0], out int x) && int.TryParse(xy[1], out int y))
                yield return (x, y);
        }
    }

    private static List<(HashSet<int> Cells, int ConditionId)> HiddenRegions(Theatre4MapTable mapRow)
    {
        List<(HashSet<int>, int)> regions = [];
        foreach (int id in mapRow.HiddenId.Where(id => id > 0))
        {
            Theatre4MapHiddenGridTable? row = Rows<Theatre4MapHiddenGridTable>()
                .FirstOrDefault(candidate => candidate.Id == id);
            if (row is null) continue;
            HashSet<int> cells = [];
            foreach ((int x, int y) in ParseGridPositions(row.GridPos))
                if (IsInside(mapRow.SizeX, mapRow.SizeY, x, y)) cells.Add(KeyOf(x, y));
            foreach ((int x, int y) in ParseGridPositions(row.Point))
                if (IsInside(mapRow.SizeX, mapRow.SizeY, x, y)) cells.Add(KeyOf(x, y));
            if (cells.Count > 0) regions.Add((cells, row.ConditionId));
        }
        return regions;
    }

    // Authored gates stay authoritative. AscNet Theatre4 local policy for a gate whose condition
    // row is absent from the installed condition data (no evaluator ships for it): the region is
    // revealed once every ordinary monster tile in the owning chapter is cleared. That moment
    // precedes the boss clear (which immediately passes/advances the chapter and makes it
    // unexplorable), so the region is playable. No condition-id or map-id special cases; a gate
    // with no condition is never hidden, and unauthored gates stay closed until the chapter exists.
    private static bool HiddenGateOpen(Mutation m, Theatre4ChapterData? chapter, int conditionId)
    {
        if (conditionId <= 0) return true;
        bool authored = Rows<GateConditionTable>().Any(row => row.Id == conditionId);
        if (authored) return IsConditionMet(m, conditionId);
        return chapter is not null && AllOrdinaryMonstersCleared(chapter);
    }

    private static bool AllOrdinaryMonstersCleared(Theatre4ChapterData chapter)
    {
        List<Theatre4GridData> monsters = chapter.Grids.Where(grid => grid.Type == T4Grid.Monster).ToList();
        return monsters.Count == 0 || monsters.All(grid => grid.State == T4State.Processed);
    }

    private static HashSet<int> ClosedHiddenCells(Mutation m, Theatre4ChapterData chapter)
    {
        HashSet<int> closed = [];
        Theatre4MapTable? mapRow = Rows<Theatre4MapTable>().FirstOrDefault(row => row.Id == chapter.MapId);
        if (mapRow is null) return closed;
        foreach ((HashSet<int> cells, int conditionId) in HiddenRegions(mapRow))
            if (!HiddenGateOpen(m, chapter, conditionId)) closed.UnionWith(cells);
        return closed;
    }

    // Adds an open region to the traversable floor, carving a Manhattan corridor when detached.
    private static void CarveIntoFloor(HashSet<int> floor, HashSet<int> cells, int width, int height)
    {
        List<int> region = cells.Where(key => IsInside(width, height, key / 100, key % 100)).ToList();
        if (region.Count == 0) return;
        foreach (int key in region) floor.Add(key);
        bool Adjacent(int key)
        {
            int x = key / 100, y = key % 100;
            return new[] { (x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1) }
                .Any(cell => IsInside(width, height, cell.Item1, cell.Item2)
                    && !region.Contains(KeyOf(cell.Item1, cell.Item2))
                    && floor.Contains(KeyOf(cell.Item1, cell.Item2)));
        }
        if (region.Any(Adjacent)) return;
        int anchor = region[0];
        List<int> targets = floor.Where(key => !region.Contains(key)).ToList();
        if (targets.Count == 0) return;
        int target = targets.OrderBy(key => Math.Abs(key / 100 - anchor / 100) + Math.Abs(key % 100 - anchor % 100)).First();
        CarveLine(floor, anchor, target);
    }

    private static void CarveLine(HashSet<int> floor, int from, int to)
    {
        int ax = from / 100, ay = from % 100, bx = to / 100, by = to % 100;
        for (int x = ax; x != bx; x += Math.Sign(bx - ax)) floor.Add(KeyOf(x, ay));
        for (int y = ay; y != by; y += Math.Sign(by - ay)) floor.Add(KeyOf(bx, y));
        floor.Add(to);
    }

    // Converts every region cell that is still Nothing into walkable Empty and pushes the delta.
    // Called whenever an absent-row hidden gate's condition may have become true (ordinary
    // monsters all cleared, an encounter/event completion, a daily condition change, or map
    // creation for a monster-less chapter); re-running is idempotent.
    internal static void RevealHiddenRegions(Mutation m, Theatre4ChapterData chapter)
    {
        if (!TryMapSize(chapter.MapId, out int width, out int height)) return;
        Theatre4MapTable? mapRow = Rows<Theatre4MapTable>().FirstOrDefault(row => row.Id == chapter.MapId);
        if (mapRow is null) return;

        HashSet<int> closedNow = [];
        List<Theatre4GridData> delta = [];
        foreach ((HashSet<int> cells, int conditionId) in HiddenRegions(mapRow))
        {
            if (!HiddenGateOpen(m, chapter, conditionId))
            {
                closedNow.UnionWith(cells);
                continue;
            }
            bool materialised = false;
            foreach (int key in cells)
            {
                Theatre4GridData? existing = chapter.Grids.FirstOrDefault(candidate =>
                    candidate.PosX == key / 100 && candidate.PosY == key % 100);
                if (existing is null)
                {
                    existing = new Theatre4GridData
                    {
                        GridId = GridIdOf(key / 100, key % 100), PosX = key / 100, PosY = key % 100,
                        Type = T4Grid.Empty, State = T4State.Discover
                    };
                    chapter.Grids.Add(existing);
                    materialised = true;
                    delta.Add(existing);
                }
                else if (existing.Type == T4Grid.Nothing)
                {
                    existing.Type = T4Grid.Empty;
                    existing.State = T4State.Discover;
                    materialised = true;
                    delta.Add(existing);
                }
            }
            if (!materialised) continue;
            CarveRevealedConnection(chapter, width, height, cells, closedNow, delta);
            // Reveal fog from every newly opened cell (region + carved corridor) so the area is
            // reachable from the already-explored floor.
            foreach (Theatre4GridData opened in delta.ToList())
                RevealNeighbours(chapter.Grids, width, height, opened.PosX, opened.PosY, delta, closedNow);
        }
        if (delta.Count == 0) return;
        m.Push(new NotifyTheatre4ChangeGrids { MapId = chapter.MapId, Grids = delta.Select(Clone).ToList() });
        m.Push(new NotifyTheatre4AdventureData { AdventureData = Clone(RequireAdventure(m)) });
    }

    // BFS through in-bounds non-blocked cells until the region touches an existing walkable tile,
    // converting the corridor's Nothing cells to Empty so the revealed area is reachable.
    private static void CarveRevealedConnection(Theatre4ChapterData chapter, int width, int height,
        HashSet<int> region, HashSet<int> blocked, List<Theatre4GridData> delta)
    {
        bool AdjacentToFloor(int key)
        {
            int x = key / 100, y = key % 100;
            foreach ((int nx, int ny) in new[] { (x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1) })
            {
                if (!IsInside(width, height, nx, ny) || region.Contains(KeyOf(nx, ny))) continue;
                Theatre4GridData? neighbour = chapter.Grids.FirstOrDefault(candidate =>
                    candidate.PosX == nx && candidate.PosY == ny);
                if (neighbour is not null && neighbour.Type != T4Grid.Nothing) return true;
            }
            return false;
        }
        if (region.Any(AdjacentToFloor)) return;

        Dictionary<int, int> previous = [];
        Queue<int> queue = new();
        foreach (int key in region) { previous[key] = -1; queue.Enqueue(key); }
        while (queue.Count > 0)
        {
            int current = queue.Dequeue();
            if (!region.Contains(current) && AdjacentToFloor(current))
            {
                for (int step = current; step != -1 && !region.Contains(step); step = previous[step])
                {
                    int pathX = step / 100, pathY = step % 100;
                    Theatre4GridData? grid = chapter.Grids.FirstOrDefault(candidate =>
                        candidate.PosX == pathX && candidate.PosY == pathY);
                    if (grid is null)
                    {
                        grid = new Theatre4GridData
                        {
                            GridId = GridIdOf(pathX, pathY), PosX = pathX, PosY = pathY,
                            Type = T4Grid.Empty, State = T4State.Unknown
                        };
                        chapter.Grids.Add(grid);
                    }
                    else if (grid.Type == T4Grid.Nothing)
                    {
                        grid.Type = T4Grid.Empty;
                        grid.State = T4State.Unknown;
                    }
                    else
                    {
                        continue;
                    }
                    if (!delta.Contains(grid)) delta.Add(grid);
                }
                return;
            }
            int x = current / 100, y = current % 100;
            foreach ((int nx, int ny) in new[] { (x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1) })
            {
                if (!IsInside(width, height, nx, ny)) continue;
                int next = KeyOf(nx, ny);
                if (previous.ContainsKey(next) || blocked.Contains(next) || region.Contains(next)) continue;
                previous[next] = current;
                queue.Enqueue(next);
            }
        }
    }

    //endregion

    //region encounter / chapter progression

    // Called by T4Combat when a fight settles. State.ActiveEncounter carries the frozen target;
    // grid/fate bookkeeping lives in Theatre4Module.Events (CompleteEncounterGrid).
    internal static void CompleteEncounter(Mutation m, bool won, int score = 0)
    {
        RequireAdventure(m);
        CompleteEncounterGrid(m, RequireAdventure(m), won, score);
    }

    internal static void AdvanceChapter(Mutation m, Theatre4ChapterData cleared)
    {
        Theatre4AdventureData adventure = RequireAdventure(m);
        // Authored route progression: the NextChapterRow lookup keys on the cleared map id's
        // MapGroup, so chapters may substitute maps without resetting the route step.
        Theatre4MapGroupTable? next = NextChapterRow(m, adventure.MapBlueprintId, adventure.Chapters.Count, cleared.MapId);
        if (next is null)
        {
            EndAdventure(m, settleType: 2, won: true);
            return;
        }
        InitializeMapFromRow(m, next);
    }

    //endregion
}
