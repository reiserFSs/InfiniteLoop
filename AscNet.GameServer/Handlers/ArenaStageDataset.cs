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

    public static int? PassTimeLimit(int areaId, uint stageId, int markId, string archetype) =>
        Resolve(areaId, stageId, markId, archetype)?.PassTimeLimit;

    public static bool TryHydrate(int areaId, uint stageId, int markId, string archetype, PreFightResponse.PreFightResponseFightData fightData)
    {
        Stage? stage = Resolve(areaId, stageId, markId, archetype);
        if (stage is null)
            return false;

        // Materialize the graph every time. Response consumers may mutate dynamic dictionaries/lists.
        fightData.NpcGroupList = stage.NpcGroupRefs.Select(groupRef => new Dictionary<string, object>
        {
            ["NpcList"] = Data.Value.GroupDefinitions[groupRef].NpcRefs
                .Select(npcRef => CloneObject(Data.Value.NpcDefinitions[npcRef]))
                .ToList()
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

    private static Stage? Resolve(int areaId, uint stageId, int markId, string archetype)
    {
        if (!ConfiguredStages.Value.Contains((areaId, stageId, markId, archetype)))
            return null;
        Stage? exact = Data.Value.Stages.SingleOrDefault(candidate =>
            candidate.StageId == stageId && candidate.AreaId == areaId);
        if (exact is not null)
            return exact.MarkId == markId && exact.Archetype == archetype ? exact : null;
        return Data.Value.Stages.SingleOrDefault(candidate =>
            candidate.MarkId == markId && candidate.Archetype == archetype && candidate.ReusableArchetype);
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
        if (data.Stages.Where(stage => stage.ReusableArchetype).GroupBy(stage => (stage.MarkId, stage.Archetype)).Any(group => group.Count() > 1))
            throw new InvalidDataException("Arena reusable MarkId/archetype profiles must be unique");
        if (data.Stages.Any(stage => string.IsNullOrWhiteSpace(stage.Archetype)))
            throw new InvalidDataException("Arena stages must identify a source archetype");
        if (data.Stages.Any(stage => !ConfiguredStages.Value.Contains(
                (stage.AreaId, stage.StageId, stage.MarkId, stage.Archetype))))
            throw new InvalidDataException("Arena dataset contains a stage profile absent from nonabandoned AreaStage configuration");
        foreach (Group group in data.GroupDefinitions)
            if (group.NpcRefs.Count == 0 || group.NpcRefs.Any(index => index < 0 || index >= data.NpcDefinitions.Count))
                throw new InvalidDataException("Arena group has an empty or invalid NPC reference list");
        foreach (Stage stage in data.Stages)
            if (stage.NpcGroupRefs.Count == 0 || stage.NpcGroupRefs.Any(index => index < 0 || index >= data.GroupDefinitions.Count))
                throw new InvalidDataException($"Arena stage {stage.StageId} has an empty or invalid group reference list");
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

    private sealed class Stage
    {
        public uint StageId { get; set; }
        public int AreaId { get; set; }
        public int MarkId { get; set; }
        public bool ReusableArchetype { get; set; }
        public List<int> NpcGroupRefs { get; set; } = new();
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
