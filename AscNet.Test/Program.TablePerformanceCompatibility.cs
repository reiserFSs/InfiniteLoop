using System.Collections.Concurrent;
using System.Reflection;
using AscNet.Common.Database;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.Table.V2.share.condition;
using AscNet.Table.V2.share.dormitory.quest;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidateTablePerformanceCompatibility()
    {
        Type dorm = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.DormModule");
        var satisfied = dorm.GetMethod("ConditionsSatisfied", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate<Func<Session, IEnumerable<int>, QuestTable?, IEnumerable<uint>, bool>>();
        var cache = (ConcurrentDictionary<Type, object>)typeof(TableReaderV2)
            .GetField("cache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        string originalDirectory = Directory.GetCurrentDirectory();
        string temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ascnet-dorm-index-{Guid.NewGuid():N}");
        string conditionPath = Path.Combine(temporaryDirectory, ConditionTable.File);
        cache.TryRemove(typeof(ConditionTable), out object? originalConditions);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(conditionPath)!);
            Directory.SetCurrentDirectory(temporaryDirectory);
            // An empty shadow file fails parsing without altering authoritative resources.
            System.IO.File.WriteAllText(conditionPath, "");
            AssertEqual(true, satisfied(null!, [], null, []), "Dorm empty conditions after failed load");
            AssertEqual(true, satisfied(null!, [0, -1], null, []), "Dorm nonpositive conditions ignored");
            AssertEqual(false, satisfied(null!, [1], null, []), "Dorm missing condition after failed load");

            Session first = CreateStoryTaskProgressSession(7);
            first.player = new Player();
            first.player.Dorm.Quest.TerminalLv = 2;
            Session second = CreateStoryTaskProgressSession(7);
            second.player = new Player();
            second.player.Dorm.Quest.TerminalLv = 1;
            second.stage.Stages[7].Passed = false;

            // A partial failed parse must also be retried, not retained as a complete index.
            System.IO.File.WriteAllText(conditionPath, "Id\tType\tParams[0]\n1\t20104\t2\ninvalid\t20104\t2\n");
            AssertEqual(true, satisfied(first, [1], null, []), "Dorm retry after empty failed load");
            AssertEqual(false, satisfied(first, [2], null, []), "Dorm absent row in partial failed load");
            System.IO.File.WriteAllText(conditionPath,
                "Id\tType\tParams[0]\n1\t20104\t2\n2\t10105\t7\n3\t-1\t\n4\t20104\t\n");
            AssertEqual(true, satisfied(first, [1, 2], null, []), "Dorm retry after partial failed load");
            AssertEqual(false, satisfied(first, [3], null, []), "Dorm unknown condition type rejected");
            AssertEqual(false, satisfied(first, [4], null, []), "Dorm empty required parameters rejected");
            AssertEqual(false, satisfied(first, [5], null, []), "Dorm absent condition rejected");
            AssertEqual(false, satisfied(second, [1], null, []), "Dorm terminal condition reads distinct player");
            AssertEqual(false, satisfied(second, [2], null, []), "Dorm stage condition reads distinct player");
            second.player.Dorm.Quest.TerminalLv = 2;
            second.stage.Stages[7].Passed = true;
            AssertEqual(true, satisfied(second, [1, 2], null, []), "Dorm cached rows reevaluate changed player state");
            AssertEqual(false, satisfied(null!, [5, 1], null, []), "Dorm missing condition preserves short circuit order");
        }
        finally
        {
            cache.TryRemove(typeof(ConditionTable), out _);
            if (originalConditions is not null)
                cache[typeof(ConditionTable)] = originalConditions;
            Directory.SetCurrentDirectory(originalDirectory);
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }
}
