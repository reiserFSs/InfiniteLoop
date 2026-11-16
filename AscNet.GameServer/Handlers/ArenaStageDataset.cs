using AscNet.Common.MsgPack;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using AscNet.Common.Util;
using AscNet.Table.V2.share.fuben.arena;

namespace AscNet.GameServer.Handlers;

internal static class ArenaStageDataset
{
    private const string ConfigPath = "./Configs/arena_stage_data.json";
    private static readonly Lazy<Dataset> Data = new(Load);
    private static readonly Lazy<HashSet<(int AreaId, uint StageId, int MarkId, string Archetype)>> ConfiguredStages = new(() =>
        TableReaderV2.Parse<AreaStageTable>()
            .Where(area => area.IsAbandoned == 0)
            .SelectMany(area => area.StageId
                .Where(stageId => stageId > 0)
                .Select(stageId => (area.Id, checked((uint)stageId), area.MarkId, area.Desc)))
            .ToHashSet());

    public static bool Supports(int areaId, uint stageId, int markId, string archetype) =>
        Resolve(areaId, stageId, markId, archetype) is not null;

    // AscNet policy: profiles flagged CommandSelectOnly hydrate an area only after a player explicitly picks it
    // (/warzone zone|set); automatic weekly zone selection keeps using the other profiles.
    public static bool IsAutoSelectable(int areaId, uint stageId, int markId, string archetype) =>
        Resolve(areaId, stageId, markId, archetype) is { CommandSelectOnly: false };

    public static string? DescribeProfile(int areaId, uint stageId, int markId, string archetype)
    {
        Stage? stage = Resolve(areaId, stageId, markId, archetype);
        if (stage is null)
            return null;
        if (stage.Generator is { } generator)
            return $"MarkId {stage.MarkId} generator from area {stage.AreaId}: {generator.WaveCount} single-enemy waves, "
                + $"bosses [{string.Join(", ", generator.BossNpcIds)}]"
                + (generator.BossWaveInterval > 0 ? $" every {generator.BossWaveInterval} waves" : "")
                + $", repeatable [{string.Join(", ", generator.RepeatableNpcIds)}]";
        return $"MarkId {stage.MarkId} waves from area {stage.AreaId}: {stage.NpcGroupRefs.Count} waves, NPCs ["
            + string.Join(", ", stage.NpcGroupRefs
                .SelectMany(groupRef => Data.Value.GroupDefinitions[groupRef].NpcRefs)
                .Select(npcRef => Data.Value.NpcDefinitions[npcRef].Value<int>("NpcId"))
                .Distinct()) + "]";
    }

    public static int? PassTimeLimit(int areaId, uint stageId, int markId, string archetype) =>
        Resolve(areaId, stageId, markId, archetype)?.PassTimeLimit;

    public static int? LevelReferenceChallengeId(int areaId, uint stageId, int markId, string archetype) =>
        Resolve(areaId, stageId, markId, archetype)?.LevelReferenceChallengeId;

    // Scales every hydrated NPC level; levels are stored for the captured challenge, so callers pass target/reference.
    public static void ScaleLevels(PreFightResponse.PreFightResponseFightData fightData, double factor)
    {
        List<Dictionary<string, object>> groups = (List<Dictionary<string, object>>?)fightData.NpcGroupList
            ?? throw new InvalidOperationException("Arena NpcGroupList is not hydrated");
        foreach (Dictionary<string, object> group in groups)
            foreach (Dictionary<string, object?> npc in ((List<object?>)group["NpcList"]).Cast<Dictionary<string, object?>>())
                npc["Level"] = Math.Max(1L, (long)Math.Round(Convert.ToInt64(npc["Level"]) * factor));
    }

    public static bool TryHydrate(int areaId, uint stageId, int markId, string archetype, PreFightResponse.PreFightResponseFightData fightData)
        => TryHydrate(areaId, stageId, markId, archetype, 0, 0, fightData);

    // activityNo/playerId seed generator profiles, so a player's waves are stable within one activity.
    public static bool TryHydrate(
        int areaId,
        uint stageId,
        int markId,
        string archetype,
        int activityNo,
        long playerId,
        PreFightResponse.PreFightResponseFightData fightData)
    {
        Stage? stage = Resolve(areaId, stageId, markId, archetype);
        if (stage is null)
            return false;

        // Materialize the graph every time. Response consumers may mutate dynamic dictionaries/lists.
        List<List<JObject>> waves = stage.Generator is { } generator
            ? GenerateWaves(generator, activityNo, playerId, areaId, stageId, ResolveNpcIds(generator))
            : stage.NpcGroupRefs.Select(groupRef => Data.Value.GroupDefinitions[groupRef].NpcRefs
                .Select(npcRef => Data.Value.NpcDefinitions[npcRef]).ToList()).ToList();
        fightData.NpcGroupList = waves.Select(npcs => new Dictionary<string, object>
        {
            ["NpcList"] = npcs.Select(CloneObject).ToList()
        }).ToList();
        fightData.PassTimeLimit = stage.PassTimeLimit;
        fightData.ReviseId = stage.ReviseId;
        fightData.Restartable = stage.Restartable;
        fightData.FightCheckType = stage.FightCheckType;
        fightData.SegmentFightCheckSecond = stage.SegmentFightCheckSecond;
        fightData.Records = CloneObject(stage.Records);
        fightData.StageParams = CloneObject(stage.StageParams);
        return true;
    }

    // AscNet policy (retail Desperate Fight wave order is unavailable): a generator profile builds single-enemy waves
    // from its NPC templates, seeded by activity/player/area/stage so a player's run is stable within one activity.
    private static List<List<JObject>> GenerateWaves(
        Generator generator, int activityNo, long playerId, int areaId, uint stageId,
        (List<JObject> Repeatable, List<JObject> UniqueOnce) npcTemplates)
    {
        List<JObject> bossTemplates = generator.BossNpcIds
            .Select(id => npcTemplates.UniqueOnce.Single(npc => npc.Value<int>("NpcId") == id)).ToList();
        List<JObject> waveNpcs = npcTemplates.UniqueOnce
            .Where(npc => !generator.BossNpcIds.Contains(npc.Value<int>("NpcId"))).ToList();
        if (generator.IncludeEveryNpcOnce)
            waveNpcs.AddRange(npcTemplates.Repeatable);

        uint state = Seed(activityNo, playerId, areaId, stageId);
        if (generator.BossWaveInterval > 0)
        {
            List<List<JObject>> intervalWaves = GenerateIntervalBossWaves(generator, bossTemplates, waveNpcs, npcTemplates.Repeatable, ref state);
            return generator.HpRamp ? ApplyHpRamp(generator, intervalWaves) : intervalWaves;
        }
        while (waveNpcs.Count + bossTemplates.Count < generator.WaveCount)
            waveNpcs.Add(npcTemplates.Repeatable[(int)(Next(ref state) % (uint)npcTemplates.Repeatable.Count)]);

        Shuffle(waveNpcs, ref state);
        Shuffle(bossTemplates, ref state);
        JObject?[] generatedWaves = new JObject?[generator.WaveCount];
        List<int> bossPositions = PlaceBosses(generator.WaveCount, bossTemplates.Count, generator.MinimumBossWaveGap, ref state);
        for (int index = 0; index < bossTemplates.Count; index++)
            generatedWaves[bossPositions[index]] = bossTemplates[index];

        int normalIndex = 0;
        for (int index = 0; index < generatedWaves.Length; index++)
        {
            if (generatedWaves[index] is null)
                generatedWaves[index] = waveNpcs[normalIndex++];
        }

        List<List<JObject>> randomWaves = generatedWaves.Select(npc => new List<JObject> { npc! }).ToList();
        return generator.HpRamp ? ApplyHpRamp(generator, randomWaves) : randomWaves;
    }

    // AscNet policy (retail wave levels are unavailable): keep each wave's enemy type but re-level it so table HP
    // (Attrib.Life + AttribPromoted.Life x (Level - 1)) rises with the wave number. Regular enemies ramp linearly from
    // the lowest to the highest regular template HP, bosses from the lowest to the highest boss template HP, over the
    // whole run. With BossHpRatio > 0 a boss instead targets BossHpRatio x the regular ramp HP of its wave, and
    // HpRampScale multiplies every target. The War Zone tier/fluctuation level scaling applies to the result.
    private static List<List<JObject>> ApplyHpRamp(Generator generator, List<List<JObject>> waves)
    {
        HashSet<int> bossIds = generator.BossNpcIds.ToHashSet();
        List<(int NpcId, double Hp)> templates = generator.NpcDefinitions
            .Select(npc => (NpcId: npc.Value<int>("NpcId"), Hp: TableHp(npc.Value<int>("NpcId"), npc.Value<int?>("Level") ?? 0)))
            .Where(template => template.Hp > 0)
            .ToList();
        (double Min, double Max)? Range(bool boss)
        {
            List<double> values = templates.Where(template => bossIds.Contains(template.NpcId) == boss).Select(template => template.Hp).ToList();
            return values.Count == 0 ? null : (values.Min(), values.Max());
        }
        (double Min, double Max)? regular = Range(false), boss = Range(true);

        List<List<JObject>> result = new(waves.Count);
        for (int index = 0; index < waves.Count; index++)
        {
            double fraction = waves.Count <= 1 ? 1 : index / (double)(waves.Count - 1);
            result.Add(waves[index].Select(template =>
            {
                int npcId = template.Value<int>("NpcId");
                (double Min, double Max)? range = bossIds.Contains(npcId) ? boss : regular;
                if (range is not { } bounds || !HpPerLevel.Value.TryGetValue(npcId, out (double Base, double PerLevel) life) || life.PerLevel <= 0)
                    return template;
                double target = bounds.Min + (bounds.Max - bounds.Min) * fraction;
                if (generator.BossHpRatio > 0 && bossIds.Contains(npcId) && regular is { } regularBounds)
                    target = (regularBounds.Min + (regularBounds.Max - regularBounds.Min) * fraction) * generator.BossHpRatio;
                target *= generator.HpRampScale;
                JObject npc = (JObject)template.DeepClone();
                npc["Level"] = Math.Max(1, (int)Math.Ceiling((target - life.Base) / life.PerLevel) + 1);
                return npc;
            }).ToList());
        }
        return result;
    }

    private static double TableHp(int npcId, int level) =>
        level > 0 && HpPerLevel.Value.TryGetValue(npcId, out (double Base, double PerLevel) life) ? life.Base + life.PerLevel * (level - 1) : 0;

    // NpcId -> (Attrib.Life, AttribPromoted.Life) decoded from the tables' Q32.32 fixed-point hex.
    private static readonly Lazy<Dictionary<int, (double Base, double PerLevel)>> HpPerLevel = new(() =>
    {
        static double Fixed(string? text) => string.IsNullOrEmpty(text) || text.Length < 3 ? 0
            : long.Parse(text.AsSpan(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture) / 4294967296d;
        Dictionary<int, string?> life = TableReaderV2.Parse<AscNet.Table.V2.share.attrib.AttribTable>().ToDictionary(row => row.Id, row => (string?)row.Life);
        Dictionary<int, string?> promoted = TableReaderV2.Parse<AscNet.Table.V2.share.attrib.AttribPromotedTable>().ToDictionary(row => row.Id, row => (string?)row.Life);
        return TableReaderV2.Parse<AscNet.Table.V2.share.fight.npc.NpcTable>()
            .Where(npc => life.ContainsKey(npc.AttribId) && promoted.ContainsKey(npc.PromotedId))
            .ToDictionary(npc => npc.Id, npc => (Fixed(life[npc.AttribId]), Fixed(promoted[npc.PromotedId])));
    });

    // AscNet policy (retail wave order is unavailable): every BossWaveInterval-th wave (4, 8, 12, ...) is a boss,
    // cycling BossNpcIds in configured order; the other waves are the shuffled regular pool.
    private static List<List<JObject>> GenerateIntervalBossWaves(
        Generator generator, List<JObject> bossTemplates, List<JObject> waveNpcs, List<JObject> repeatable, ref uint state)
    {
        List<JObject> bosses = generator.BossNpcIds
            .Select(id => bossTemplates.Single(npc => npc.Value<int>("NpcId") == id)).ToList();
        int bossWaves = generator.WaveCount / generator.BossWaveInterval;
        while (waveNpcs.Count + bossWaves < generator.WaveCount)
            waveNpcs.Add(repeatable[(int)(Next(ref state) % (uint)repeatable.Count)]);
        Shuffle(waveNpcs, ref state);

        List<List<JObject>> waves = new(generator.WaveCount);
        int normalIndex = 0, bossIndex = 0;
        for (int wave = 1; wave <= generator.WaveCount; wave++)
        {
            JObject npc = wave % generator.BossWaveInterval == 0
                ? bosses[bossIndex++ % bosses.Count]
                : waveNpcs[normalIndex++];
            waves.Add([npc]);
        }
        return waves;
    }

    private static void Shuffle<T>(List<T> values, ref uint state)
    {
        for (int index = values.Count - 1; index > 0; index--)
        {
            int other = (int)(Next(ref state) % (uint)(index + 1));
            (values[index], values[other]) = (values[other], values[index]);
        }
    }

    private static List<int> PlaceBosses(int waveCount, int bossCount, int minimumGap, ref uint state)
    {
        if (bossCount == 0) return [];
        if (waveCount < (bossCount - 1) * minimumGap + 1)
            throw new InvalidDataException("Could not place Warzone boss appearances with the configured wave gap");

        int[] slack = new int[bossCount + 1];
        int remaining = waveCount - ((bossCount - 1) * minimumGap + 1);
        for (int index = 0; index < remaining; index++)
            slack[(int)(Next(ref state) % (uint)slack.Length)]++;

        List<int> selected = [];
        int wave = slack[0];
        for (int index = 0; index < bossCount; index++)
        {
            selected.Add(wave);
            wave += minimumGap + slack[index + 1];
        }
        Shuffle(selected, ref state);
        return selected;
    }

    private static (List<JObject> Repeatable, List<JObject> UniqueOnce) ResolveNpcIds(Generator generator)
    {
        Dictionary<int, JObject> npcById = generator.NpcDefinitions.ToDictionary(npc => npc.Value<int>("NpcId"));
        return (
            generator.RepeatableNpcIds.Select(id => npcById[id]).ToList(),
            generator.UniqueOnceNpcIds.Select(id => npcById[id]).ToList());
    }

    private static uint Seed(int activityNo, long playerId, int areaId, uint stageId)
    {
        uint hash = 2166136261;
        foreach (uint value in new[] { unchecked((uint)activityNo), unchecked((uint)playerId), (uint)((ulong)playerId >> 32), unchecked((uint)areaId), stageId })
        {
            hash = (hash ^ value) * 16777619;
        }
        return hash == 0 ? 0x6D2B79F5 : hash;
    }

    private static uint Next(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }

    private static Stage? Resolve(int areaId, uint stageId, int markId, string archetype)
    {
        if (!ConfiguredStages.Value.Contains((areaId, stageId, markId, archetype)))
            return null;
        Stage? exact = Data.Value.Stages.SingleOrDefault(candidate =>
            candidate.StageId == stageId && candidate.AreaId == areaId);
        if (exact is not null)
            return exact.MarkId == markId && exact.Archetype == archetype ? exact : null;
        return Data.Value.Stages.SingleOrDefault(candidate =>
            candidate.ReusableArchetype && candidate.MarkId == markId && candidate.Archetypes.Contains(archetype));
    }

    private static object? CloneObject(JToken token) => token switch
    {
        JObject value => value.Properties().ToDictionary(property => property.Name, property => CloneObject(property.Value)),
        JArray value => value.Select(CloneObject).ToList(),
        JValue value => value.Value,
        _ => throw new InvalidDataException($"Unsupported Arena dynamic value {token.Type}")
    };

    private static Dataset Load()
    {
        Dataset data = JsonConvert.DeserializeObject<Dataset>(File.ReadAllText(ConfigPath))
            ?? throw new InvalidDataException($"Could not deserialize {ConfigPath}");
        if (data.SchemaVersion != 2)
            throw new InvalidDataException($"Unsupported Arena stage schema {data.SchemaVersion}");
        if (data.Stages.Select(stage => (stage.AreaId, stage.StageId)).Distinct().Count() != data.Stages.Count)
            throw new InvalidDataException("Arena stage keys must be unique");
        if (data.Stages.Where(stage => stage.ReusableArchetype)
                .SelectMany(stage => stage.Archetypes.Select(archetype => (stage.MarkId, archetype)))
                .GroupBy(key => key).Any(group => group.Count() > 1))
            throw new InvalidDataException("Arena reusable MarkId/archetype profiles must be unique");
        if (data.Stages.Any(stage => string.IsNullOrWhiteSpace(stage.Archetype)
                || stage.ArchetypeAliases.Any(alias => string.IsNullOrWhiteSpace(alias) || alias == stage.Archetype)))
            throw new InvalidDataException("Arena stages must identify a source archetype and distinct non-empty aliases");
        if (data.Stages.Any(stage => stage.ArchetypeAliases.Count > 0 && !stage.ReusableArchetype))
            throw new InvalidDataException("Arena archetype aliases only apply to reusable profiles");
        if (data.Stages.Any(stage => stage.LevelReferenceChallengeId <= 0))
            throw new InvalidDataException("Arena stages must name the captured ChallengeId their NPC levels belong to");
        if (data.Stages.Any(stage => !ConfiguredStages.Value.Contains(
                (stage.AreaId, stage.StageId, stage.MarkId, stage.Archetype))))
            throw new InvalidDataException("Arena dataset contains a stage profile absent from nonabandoned AreaStage configuration");
        foreach (Group group in data.GroupDefinitions)
            if (group.NpcRefs.Count == 0 || group.NpcRefs.Any(index => index < 0 || index >= data.NpcDefinitions.Count))
                throw new InvalidDataException("Arena group has an empty or invalid NPC reference list");
        foreach (Stage stage in data.Stages)
        {
            if (stage.Generator is null && (stage.NpcGroupRefs.Count == 0 || stage.NpcGroupRefs.Any(index => index < 0 || index >= data.GroupDefinitions.Count)))
                throw new InvalidDataException($"Arena stage {stage.StageId} has an empty or invalid group reference list");
            if (stage.Generator is not { } generator)
                continue;
            int[] configuredIds = generator.RepeatableNpcIds.Concat(generator.UniqueOnceNpcIds).ToArray();
            int matchingNpcDefinitions = generator.NpcDefinitions.Count(npc => configuredIds.Contains(npc.Value<int?>("NpcId") ?? 0));
            if (generator.Mode != "SingleEnemyPerWave" || generator.WaveCount <= 0
                || generator.RepeatableNpcIds.Count == 0
                || generator.IncludeEveryNpcOnce && generator.WaveCount < generator.RepeatableNpcIds.Count + generator.UniqueOnceNpcIds.Count
                || generator.WaveCount < generator.BossNpcIds.Count
                || generator.MinimumBossWaveGap < 1
                || generator.BossWaveInterval < 0
                || generator.BossHpRatio < 0 || generator.BossHpRatio > 0 && !generator.HpRamp
                || generator.HpRampScale <= 0 || generator.HpRampScale != 1 && !generator.HpRamp
                || generator.BossWaveInterval > 0 && (generator.BossNpcIds.Count == 0 || generator.BossWaveInterval > generator.WaveCount)
                || generator.BossNpcIds.Except(generator.UniqueOnceNpcIds).Any()
                || generator.RepeatableNpcIds.Intersect(generator.UniqueOnceNpcIds).Any()
                || configuredIds.Distinct().Count() != configuredIds.Length
                || matchingNpcDefinitions != configuredIds.Length
                || generator.NpcDefinitions.Count != configuredIds.Length)
                throw new InvalidDataException($"Arena stage {stage.StageId} has an invalid single-enemy generator profile");
            _ = ResolveNpcIds(generator);
        }
        return data;
    }

    private sealed class Dataset
    {
        public int SchemaVersion { get; set; }
        public List<JObject> NpcDefinitions { get; set; } = new();
        public List<Group> GroupDefinitions { get; set; } = new();
        public List<Stage> Stages { get; set; } = new();
    }

    private sealed class Group
    {
        public List<int> NpcRefs { get; set; } = new();
    }

    private sealed class Generator
    {
        public string Mode { get; set; } = string.Empty;
        public int WaveCount { get; set; }
        public bool IncludeEveryNpcOnce { get; set; }
        public List<JObject> NpcDefinitions { get; set; } = new();
        public List<int> RepeatableNpcIds { get; set; } = new();
        public List<int> UniqueOnceNpcIds { get; set; } = new();
        public List<int> BossNpcIds { get; set; } = new();
        public int MinimumBossWaveGap { get; set; } = 3;
        // > 0: every Nth wave is a boss, cycling BossNpcIds in order (bosses then repeat). 0: each boss once, random slots.
        public int BossWaveInterval { get; set; }
        // true: re-level each wave's enemy so table HP rises with the wave number (see ApplyHpRamp).
        public bool HpRamp { get; set; }
        // > 0 with HpRamp: boss table HP = BossHpRatio x the regular ramp HP of the same wave (AscNet policy).
        public double BossHpRatio { get; set; }
        // With HpRamp: multiplies every ramp target (regular and boss) table HP (AscNet policy).
        public double HpRampScale { get; set; } = 1;
    }

    private sealed class Stage
    {
        public uint StageId { get; set; }
        public int AreaId { get; set; }
        public int MarkId { get; set; }
        public bool ReusableArchetype { get; set; }
        public List<string> ArchetypeAliases { get; set; } = new();
        public bool CommandSelectOnly { get; set; }
        public IEnumerable<string> Archetypes => ArchetypeAliases.Prepend(Archetype);
        public int LevelReferenceChallengeId { get; set; }
        public List<int> NpcGroupRefs { get; set; } = new();
        public Generator? Generator { get; set; }
        public string Archetype { get; set; } = string.Empty;
        public int PassTimeLimit { get; set; }
        public int ReviseId { get; set; }
        public bool Restartable { get; set; }
        public int FightCheckType { get; set; }
        public int SegmentFightCheckSecond { get; set; }
        public JObject Records { get; set; } = new();
        public JObject StageParams { get; set; } = new();
    }
}
