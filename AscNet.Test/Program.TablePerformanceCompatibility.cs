using System.Collections.Concurrent;
using System.Reflection;
using System.Globalization;
using AscNet.Table.V2.share.biancatheatre;
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
        ValidateTableNumericAndArraySchema();
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

    private static void ValidateTableNumericAndArraySchema()
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        var cache = (ConcurrentDictionary<Type, object>)typeof(TableReaderV2)
            .GetField("cache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        TableSchemaProbe.File = Path.GetTempFileName();
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            // These operations also compile against generated scalar/list contracts, not handwritten schemas.
            BiancaTheatreDifficultyTable difficulty = TableReaderV2.Parse<BiancaTheatreDifficultyTable>().Single(row => row.Id == 3);
            AssertEqual(25d, difficulty.ExpFactor * 10, "Generated decimal factor retains its fraction");
            AssertEqual(true, difficulty.ShowItemIds.SequenceEqual([96117]), "Blank singleton padding remains a one-item list");
            BiancaTheatreSystemEffectTable effect = TableReaderV2.Parse<BiancaTheatreSystemEffectTable>().Single(row => row.Id == 101);
            AssertEqual(96129d, effect.Params.Sum(), "Generated decimal array is numerically consumable");

            System.IO.File.WriteAllText(TableSchemaProbe.File,
                "Id\tFactor\tOptional\tHex\tDigitHex\tExponentHex\tNumbers[1]\tOptions[1]\tLabels[1]\tNumbers[2]\tOptions[2]\tLabels[2]\tNumbers[3]\tOptions[3]\tLabels[3]\tNumbers[4]\tOptions[4]\tLabels[4]\n"
                + "1\t1.25\t\t0xFF\t0000000100000000\t00000001000000E0\t1\t\tfirst\t\t7\t\t2.5\t0\tthird\t\t\t\n"
                + "2\t\t9\t0x10\t0000000200000000\t00000002000000E0\t\t\t\t\t\t\t\t\t\t\t\t\n");
            List<TableSchemaProbe> rows = TableReaderV2.Parse<TableSchemaProbe>();
            AssertEqual(2, rows.Count, "Numeric schema parses every row under non-dot culture");
            AssertEqual(1.25d, rows[0].Factor!.Value, "Invariant nullable decimal scalar");
            AssertEqual<int?>(null, rows[0].Optional, "Blank nullable integer stays null");
            AssertEqual("0xFF", rows[0].Hex, "Hex text is not numerically coerced");
            AssertEqual(0x0000000100000000UL, ulong.Parse(rows[0].DigitHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                "Pure-digit fixed-point hex retains exact raw bits");
            AssertEqual(0x00000001000000E0UL, ulong.Parse(rows[0].ExponentHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                "E-only fixed-point hex is not coerced to exponent notation");
            AssertEqual(true, rows[0].Numbers.SequenceEqual([1d, 0d, 2.5d]), "Interleaved decimal positions survive interior empty cells");
            AssertEqual(true, rows[0].Options.SequenceEqual([0, 7, 0]), "Interleaved numeric holes and explicit trailing zero survive");
            AssertEqual(true, rows[0].Labels.SequenceEqual(["first", "", "third"]), "Interleaved string holes retain option indexes");
            AssertEqual<double?>(null, rows[1].Factor, "Blank nullable decimal stays null");
            AssertEqual(9, rows[1].Optional!.Value, "Nullable integer retains nonblank value");
            AssertEqual(0, rows[1].Numbers.Count + rows[1].Options.Count + rows[1].Labels.Count, "Entirely blank arrays have no padding entries");
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            cache.TryRemove(typeof(TableSchemaProbe), out _);
            System.IO.File.Delete(TableSchemaProbe.File);
        }
    }

    public sealed class TableSchemaProbe : ITable
    {
        public static string File { get; set; } = "";
        public int Id { get; set; }
        public double? Factor { get; set; }
        public int? Optional { get; set; }
        public string Hex { get; set; } = "";
        public string DigitHex { get; set; } = "";
        public string ExponentHex { get; set; } = "";
        public List<double> Numbers { get; set; } = [];
        public List<int> Options { get; set; } = [];
        public List<string> Labels { get; set; } = [];
    }
}
