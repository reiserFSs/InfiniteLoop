using AscNet.Common.Util;
using AscNet.Table.V2.share.fuben;
using AscNet.Table.V2.share.robot;
using Newtonsoft.Json.Linq;

namespace AscNet.GameServer.Handlers;

internal static class CurrentClientStudyTables
{
    internal const string ClientVersion = "4.6.0";
    internal const int PracticeChapterCount = 8;
    internal const int PracticeGroupCount = 88;
    internal const int PracticeActivityCount = 250;
    internal const int TeachingActivityCount = 49;
    internal const int TeachingRobotCount = 142;
    internal const int StudyStageCount = 467;
    internal const int StageLevelControlCount = 141;
    internal const int RobotCount = 170;
    private const int ProgressionEdgeCount = 255;
    private const int ProgressionChainCount = 212;

    private const string SourceRevision = "bb3c34765c9d9c1c542079d536a17e82b27f3245";
    private const string ResourcePath = "Configs/study_compatibility_4.6.0.json";
    private static readonly Lazy<Catalog> Data = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);
    internal static bool TryGetStage(long stageId, out StageTable stage)
    {
        if (!TryGetStageKey(stageId, out int key))
        {
            stage = null!;
            return false;
        }

        return Data.Value.Stages.TryGetValue(key, out stage!);
    }
    internal static bool TryGetConfiguredRobotIds(long stageId, out IReadOnlyList<int> robotIds)
    {
        if (TryGetStageKey(stageId, out int key) && Data.Value.ConfiguredRobotIds.TryGetValue(key, out int[]? configured))
        {
            robotIds = configured;
            return true;
        }

        robotIds = Array.Empty<int>();
        return false;
    }
    internal static bool TryGetStageLevelControls(long stageId, out IReadOnlyList<StageLevelControlTable> rows)
    {
        if (!TryGetStageKey(stageId, out int key) || !Data.Value.Stages.ContainsKey(key))
        {
            rows = Array.Empty<StageLevelControlTable>();
            return false;
        }

        rows = Data.Value.StageLevelControls.TryGetValue(key, out StageLevelControlTable[]? controls)
            ? controls
            : Array.Empty<StageLevelControlTable>();
        return true;
    }

    internal static bool TryGetRobot(int robotId, out RobotTable robot)
    {
        return Data.Value.Robots.TryGetValue(robotId, out robot!);
    }

    internal static bool TryGetPracticeChapterId(long stageId, out int chapterId)
    {
        if (!TryGetStageKey(stageId, out int key))
        {
            chapterId = default;
            return false;
        }

        return Data.Value.PracticeChapterIds.TryGetValue(key, out chapterId);
    }

    internal static bool TryGetTeachingActivityIds(long stageId, out IReadOnlyList<int> activityIds)
    {
        if (TryGetStageKey(stageId, out int key) && Data.Value.TeachingActivityIds.TryGetValue(key, out int[]? activities))
        {
            activityIds = activities;
            return true;
        }

        activityIds = Array.Empty<int>();
        return false;
    }

    private static bool TryGetStageKey(long stageId, out int key)
    {
        if (stageId is > 0 and <= int.MaxValue)
        {
            key = (int)stageId;
            return true;
        }

        key = default;
        return false;
    }

    private static Catalog Load()
    {
        JObject root = JsonSnapshot.LoadObject(ResourcePath);
        string clientVersion = root.Value<string>("ClientVersion")
            ?? throw new InvalidDataException($"{ResourcePath}: ClientVersion is required.");
        if (!string.Equals(clientVersion, ClientVersion, StringComparison.Ordinal))
            throw new InvalidDataException($"{ResourcePath}: expected client version {ClientVersion}, got {clientVersion}.");

        string sourceRevision = root.Value<string>("SourceRevision")
            ?? throw new InvalidDataException($"{ResourcePath}: SourceRevision is required.");
        if (!string.Equals(sourceRevision, SourceRevision, StringComparison.Ordinal))
            throw new InvalidDataException($"{ResourcePath}: expected source revision {SourceRevision}, got {sourceRevision}.");

        JObject sourcePaths = RequireObject(root, "SourcePaths");
        JObject sourceHashes = RequireObject(root, "SourceHashes");
        foreach (string source in new[] { "Stage", "StageLevelControl", "Robot", "PracticeChapter", "PracticeGroup", "PracticeActivity", "TeachingActivity", "TeachingRobot" })
        {
            string? path = sourcePaths.Value<string>(source);
            if (string.IsNullOrWhiteSpace(path) || !path.StartsWith("en/bytes/", StringComparison.Ordinal) || !path.EndsWith(".json", StringComparison.Ordinal))
                throw new InvalidDataException($"{ResourcePath}: SourcePaths.{source} must identify an EN JSON source.");
            string? hash = sourceHashes.Value<string>(source);
            if (hash is null || hash.Length != 40 || hash.Any(character => !Uri.IsHexDigit(character)))
                throw new InvalidDataException($"{ResourcePath}: SourceHashes.{source} must be a SHA-1 hash.");
        }

        JObject expectedCounts = RequireObject(root, "ExpectedCounts");
        ValidateDeclaredCount(expectedCounts, "PracticeChapters", PracticeChapterCount);
        ValidateDeclaredCount(expectedCounts, "PracticeGroups", PracticeGroupCount);
        ValidateDeclaredCount(expectedCounts, "PracticeActivities", PracticeActivityCount);
        ValidateDeclaredCount(expectedCounts, "TeachingActivities", TeachingActivityCount);
        ValidateDeclaredCount(expectedCounts, "TeachingRobots", TeachingRobotCount);
        ValidateDeclaredCount(expectedCounts, "StudyStages", StudyStageCount);
        ValidateDeclaredCount(expectedCounts, "StageLevelControls", StageLevelControlCount);
        ValidateDeclaredCount(expectedCounts, "Robots", RobotCount);

        JArray practiceChapters = RequireArray(root, "PracticeChapters", PracticeChapterCount);
        JArray practiceGroups = RequireArray(root, "PracticeGroups", PracticeGroupCount);
        JArray practiceActivities = RequireArray(root, "PracticeActivities", PracticeActivityCount);
        JArray teachingActivities = RequireArray(root, "TeachingActivities", TeachingActivityCount);
        JArray teachingRobots = RequireArray(root, "TeachingRobots", TeachingRobotCount);
        JArray stageRows = RequireArray(root, "Stages", StudyStageCount);
        JArray stageLevelControlRows = RequireArray(root, "StageLevelControls", StageLevelControlCount);
        JArray robotRows = RequireArray(root, "Robots", RobotCount);

        HashSet<int> studyStageIds = new();
        foreach (JObject row in practiceGroups.OfType<JObject>())
        {
            AddPositiveIds(studyStageIds, row["StageIds"]);
            AddPositiveIds(studyStageIds, row["LinkStageIds"]);
        }
        foreach (JObject row in practiceActivities.OfType<JObject>())
            AddPositiveId(studyStageIds, row.Value<int>("StageId"));
        foreach (JObject row in teachingActivities.OfType<JObject>())
        {
            AddPositiveIds(studyStageIds, row["StageId"]);
            AddPositiveIds(studyStageIds, row["ChallengeStage"]);
            AddPositiveIds(studyStageIds, row["LinkStageId"]);
        }
        if (studyStageIds.Count != StudyStageCount)
            throw new InvalidDataException($"{ResourcePath}: Practice/Teaching sources resolve {studyStageIds.Count} Study stages; expected {StudyStageCount}.");

        Dictionary<int, int[]> practiceGroupStageIds = new();
        foreach (JObject row in practiceGroups.OfType<JObject>())
        {
            int groupId = row.Value<int>("GroupId");
            if (groupId <= 0 || !practiceGroupStageIds.TryAdd(groupId, ReadPositiveIds(row["StageIds"])))
                throw new InvalidDataException($"{ResourcePath}: invalid or duplicate PracticeGroup GroupId {groupId}.");
        }

        Dictionary<int, int> practiceChapterIds = new();
        foreach (JObject row in practiceChapters.OfType<JObject>())
        {
            int chapterId = row.Value<int>("Id");
            if (chapterId <= 0)
                throw new InvalidDataException($"{ResourcePath}: invalid PracticeChapter Id {chapterId}.");

            IEnumerable<int> chapterStageIds = ReadPositiveIds(row["StageId"])
                .Concat(ReadPositiveIds(row["Groups"]).SelectMany(groupId =>
                {
                    if (!practiceGroupStageIds.TryGetValue(groupId, out int[]? groupStages))
                        throw new InvalidDataException($"{ResourcePath}: PracticeChapter {chapterId} references missing PracticeGroup {groupId}.");
                    return groupStages;
                }));
            foreach (int stageId in chapterStageIds)
            {
                if (!studyStageIds.Contains(stageId) || !practiceChapterIds.TryAdd(stageId, chapterId))
                    throw new InvalidDataException($"{ResourcePath}: Practice stage {stageId} has invalid or ambiguous chapter ownership.");
            }
        }

        Dictionary<int, List<int>> teachingActivityIds = new();
        foreach (JObject row in teachingActivities.OfType<JObject>())
        {
            int activityId = row.Value<int>("Id");
            if (activityId <= 0)
                throw new InvalidDataException($"{ResourcePath}: invalid TeachingActivity Id {activityId}.");
            foreach (int stageId in ReadPositiveIds(row["StageId"]).Concat(ReadPositiveIds(row["ChallengeStage"])))
            {
                if (!studyStageIds.Contains(stageId))
                    throw new InvalidDataException($"{ResourcePath}: TeachingActivity {activityId} references non-Study stage {stageId}.");
                if (!teachingActivityIds.TryGetValue(stageId, out List<int>? activityIds))
                    teachingActivityIds.Add(stageId, activityIds = new());
                activityIds.Add(activityId);
            }
        }
        HashSet<int> ownedStudyStageIds = practiceChapterIds.Keys.Concat(teachingActivityIds.Keys).ToHashSet();
        if (!studyStageIds.SetEquals(ownedStudyStageIds)
            || practiceChapterIds.Keys.Intersect(teachingActivityIds.Keys).Any())
        {
            throw new InvalidDataException(
                $"{ResourcePath}: every Study stage must belong to exactly one client progression namespace.");
        }

        ValidateProgressionGraph(stageRows, studyStageIds);
        foreach (JObject stageRow in stageRows.OfType<JObject>())
            NormalizeBooleanScalars(stageRow);

        Dictionary<int, StageTable> stages = ToUniqueDictionary<StageTable>(stageRows, row => row.StageId, "Stages");
        if (!studyStageIds.SetEquals(stages.Keys))
            throw new InvalidDataException($"{ResourcePath}: Stages must exactly match the Practice/Teaching stage set.");

        Dictionary<int, int[]> teachingRobotIds = new();
        foreach (JObject row in teachingRobots.OfType<JObject>())
        {
            int stageId = row.Value<int>("StageId");
            if (!teachingRobotIds.TryAdd(stageId, ReadPositiveIds(row["RobotId"])))
                throw new InvalidDataException($"{ResourcePath}: duplicate TeachingRobot StageId {stageId}.");
        }

        Dictionary<int, int[]> configuredRobotIds = new(stages.Count);
        foreach ((int stageId, StageTable stage) in stages)
        {
            int[] stageRobotIds = stage.RobotId?.Where(id => id > 0).Distinct().ToArray() ?? Array.Empty<int>();
            configuredRobotIds.Add(stageId, teachingRobotIds.TryGetValue(stageId, out int[]? teachingIds) && teachingIds.Length > 0
                ? teachingIds
                : stageRobotIds);
        }

        Dictionary<int, RobotTable> robots = ToUniqueDictionary<RobotTable>(robotRows, row => row.Id, "Robots");
        HashSet<int> referencedRobotIds = configuredRobotIds.Values.SelectMany(ids => ids).ToHashSet();
        if (referencedRobotIds.Count != RobotCount || !referencedRobotIds.SetEquals(robots.Keys))
            throw new InvalidDataException($"{ResourcePath}: configured Study robots must exactly match the {RobotCount} imported Robot rows.");
        if (robots.Values.Any(robot => robot.CharacterId <= 0))
            throw new InvalidDataException($"{ResourcePath}: every imported Robot must define a CharacterId.");

        Dictionary<int, List<StageLevelControlTable>> controls = new();
        HashSet<int> controlIds = new();
        foreach (JObject token in stageLevelControlRows.OfType<JObject>())
        {
            StageLevelControlTable row = token.ToObject<StageLevelControlTable>()
                ?? throw new InvalidDataException($"{ResourcePath}: invalid StageLevelControl row.");
            if (!controlIds.Add(row.Id))
                throw new InvalidDataException($"{ResourcePath}: duplicate StageLevelControl Id {row.Id}.");
            if (!stages.ContainsKey(row.StageId))
                throw new InvalidDataException($"{ResourcePath}: StageLevelControl {row.Id} references non-Study stage {row.StageId}.");
            if (!controls.TryGetValue(row.StageId, out List<StageLevelControlTable>? stageControls))
                controls.Add(row.StageId, stageControls = new());
            stageControls.Add(row);
        }

        return new Catalog(
            stages,
            configuredRobotIds,
            controls.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray()),
            robots,
            practiceChapterIds,
            teachingActivityIds.ToDictionary(pair => pair.Key, pair => pair.Value.Distinct().Order().ToArray()));
    }

    private static void ValidateProgressionGraph(JArray stageRows, IReadOnlySet<int> studyStageIds)
    {
        HashSet<(int From, int To)> preEdges = new();
        HashSet<(int From, int To)> nextEdges = new();
        foreach (JObject row in stageRows.OfType<JObject>())
        {
            int stageId = row.Value<int>("StageId");
            foreach (int predecessor in ReadPositiveIds(row["PreStageId"]))
            {
                if (!studyStageIds.Contains(predecessor))
                    throw new InvalidDataException($"{ResourcePath}: Study stage {stageId} references non-Study predecessor {predecessor}.");
                preEdges.Add((predecessor, stageId));
            }
            foreach (int successor in ReadPositiveIds(row["NextStageId"]))
            {
                if (!studyStageIds.Contains(successor))
                    throw new InvalidDataException($"{ResourcePath}: Study stage {stageId} references non-Study successor {successor}.");
                nextEdges.Add((stageId, successor));
            }
        }

        if (preEdges.Count != ProgressionEdgeCount || !preEdges.SetEquals(nextEdges))
            throw new InvalidDataException($"{ResourcePath}: Study PreStageId/NextStageId graph must contain exactly {ProgressionEdgeCount} reciprocal edges.");
        Dictionary<int, int> inDegree = studyStageIds.ToDictionary(stageId => stageId, _ => 0);
        Dictionary<int, int> outDegree = studyStageIds.ToDictionary(stageId => stageId, _ => 0);
        foreach ((int from, int to) in preEdges)
        {
            if (++outDegree[from] > 1 || ++inDegree[to] > 1)
                throw new InvalidDataException($"{ResourcePath}: Study progression graph must be a collection of linear chains.");
        }
        if (inDegree.Values.Count(degree => degree == 0) != ProgressionChainCount || outDegree.Values.Count(degree => degree == 0) != ProgressionChainCount)
            throw new InvalidDataException($"{ResourcePath}: Study progression graph must contain {ProgressionChainCount} roots and terminals.");
        Dictionary<int, int> successorByStageId = preEdges.ToDictionary(edge => edge.From, edge => edge.To);
        HashSet<int> traversedStageIds = new();
        foreach (int rootStageId in inDegree.Where(pair => pair.Value == 0).Select(pair => pair.Key))
        {
            int stageId = rootStageId;
            while (traversedStageIds.Add(stageId))
            {
                if (!successorByStageId.TryGetValue(stageId, out int successorStageId))
                    break;
                stageId = successorStageId;
            }
        }
        if (!traversedStageIds.SetEquals(studyStageIds))
            throw new InvalidDataException($"{ResourcePath}: Study progression graph must not contain cycles.");

    }

    private static Dictionary<int, T> ToUniqueDictionary<T>(JArray rows, Func<T, int> keySelector, string section)
    {
        Dictionary<int, T> result = new(rows.Count);
        foreach (JObject token in rows.OfType<JObject>())
        {
            T row = token.ToObject<T>()
                ?? throw new InvalidDataException($"{ResourcePath}: invalid {section} row.");
            int key = keySelector(row);
            if (key <= 0 || !result.TryAdd(key, row))
                throw new InvalidDataException($"{ResourcePath}: invalid or duplicate {section} key {key}.");
        }
        return result;
    }

    private static void NormalizeBooleanScalars(JObject row)
    {
        foreach (JProperty property in row.Properties().Where(property => property.Value.Type == JTokenType.Boolean).ToArray())
            property.Value = property.Value.Value<bool>() ? 1 : 0;
    }

    private static void ValidateDeclaredCount(JObject counts, string name, int expected)
    {
        int actual = counts.Value<int?>(name)
            ?? throw new InvalidDataException($"{ResourcePath}: ExpectedCounts.{name} is required.");
        if (actual != expected)
            throw new InvalidDataException($"{ResourcePath}: ExpectedCounts.{name} is {actual}; expected {expected}.");
    }

    private static JObject RequireObject(JObject root, string name)
    {
        return root[name] as JObject
            ?? throw new InvalidDataException($"{ResourcePath}: {name} must be an object.");
    }

    private static JArray RequireArray(JObject root, string name, int expectedCount)
    {
        JArray rows = root[name] as JArray
            ?? throw new InvalidDataException($"{ResourcePath}: {name} must be an array.");
        if (rows.Count != expectedCount)
            throw new InvalidDataException($"{ResourcePath}: {name} contains {rows.Count} rows; expected {expectedCount}.");
        return rows;
    }

    private static void AddPositiveIds(HashSet<int> destination, JToken? token)
    {
        foreach (int id in ReadPositiveIds(token))
            destination.Add(id);
    }

    private static void AddPositiveId(HashSet<int> destination, int id)
    {
        if (id > 0)
            destination.Add(id);
    }

    private static int[] ReadPositiveIds(JToken? token)
    {
        return token is JArray values
            ? values.Values<int>().Where(id => id > 0).Distinct().ToArray()
            : Array.Empty<int>();
    }

    private sealed record Catalog(
        IReadOnlyDictionary<int, StageTable> Stages,
        IReadOnlyDictionary<int, int[]> ConfiguredRobotIds,
        IReadOnlyDictionary<int, StageLevelControlTable[]> StageLevelControls,
        IReadOnlyDictionary<int, RobotTable> Robots,
        IReadOnlyDictionary<int, int> PracticeChapterIds,
        IReadOnlyDictionary<int, int[]> TeachingActivityIds);
}
