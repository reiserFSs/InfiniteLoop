using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.condition;

namespace AscNet.GameServer.Handlers;

public static partial class ArchiveCgModule
{
    private static readonly Func<Session, DateTimeOffset, bool> UnmetCondition = static (_, _) => false;
    private static readonly Lazy<Dictionary<int, Func<Session, DateTimeOffset, bool>>> CgConditions = new(BuildCgConditions);

    internal static bool ConditionSatisfied(Session session, int conditionId, DateTimeOffset now) =>
        CgConditions.Value.TryGetValue(conditionId, out var predicate) && predicate(session, now);

    private static Dictionary<int, Func<Session, DateTimeOffset, bool>> BuildCgConditions()
    {
        var rows = TableReaderV2.Parse<ConditionTable>().ToDictionary(row => row.Id);
        Dictionary<int, Func<Session, DateTimeOffset, bool>> predicates = new();
        HashSet<int> visiting = new();
        foreach (Cg cg in Catalog.Value.Values)
            if (cg.Condition > 0) Compile(cg.Condition);
        return predicates;

        Func<Session, DateTimeOffset, bool> Compile(int id)
        {
            if (predicates.TryGetValue(id, out var cached)) return cached;
            if (!rows.TryGetValue(id, out var row) || !visiting.Add(id)) return UnmetCondition;
            Func<Session, DateTimeOffset, bool> predicate = string.IsNullOrWhiteSpace(row.Formula)
                ? CompileCgLeaf(row) : CompileCgFormula(row.Formula, Compile);
            visiting.Remove(id);
            predicates.Add(id, predicate);
            return predicate;
        }
    }

    private static Func<Session, DateTimeOffset, bool> CompileCgLeaf(ConditionTable row)
    {
        if (row.Params.Count == 0) return UnmetCondition;
        int value = row.Params[0];
        switch (row.Type)
        {
            case 10105:
                // The CG closure contains ordinary stages, not Bfrt/Assign mode stages.
                return (session, _) => session.stage.Stages.TryGetValue((uint)value, out var stage) && stage.Passed;
            case 11104:
                // XFashionManager.CheckHasFashion tests ownership, not the separate IsLock flag.
                return (session, _) =>
                {
                    foreach (var fashion in session.character.Fashions)
                        if (fashion.Id == value) return true;
                    return false;
                };
            case 11114:
                // XConditionManager uses WEAPON fashions here and excludes time-limited ownership.
                int[] fashionIds = row.Params.ToArray();
                return (session, _) =>
                {
                    foreach (int id in fashionIds)
                    {
                        if (id == 0) return true; // XWeaponFashionConfigs.DefaultWeaponFashionId.
                        foreach (var fashion in session.character.WeaponFashions)
                            if (fashion.Id == id && fashion.ExpireTime <= 0) return true;
                    }
                    return false;
                };
            case 17000:
            case 17001:
            case 17002:
            case 17003:
                return (session, _) => TheatreModule.IsConditionSatisfied(session, row.Id);
            case 17107:
                return (session, _) =>
                {
                    foreach (var record in session.player.BiancaTheatre.Data.TeamRecords)
                        if (record.EndRecords.Contains(value)) return true;
                    return false;
                };
            case 17208:
                return (session, _) => Theatre3Module.HasEndingRecord(session, value);
            case 23201 when row.Params.Count >= 2:
                int count = row.Params[1];
                return (session, _) => session.player.Theatre6.PassStageRecords.TryGetValue(value, out int passed)
                    && passed >= count;
            case 23001:
                return ActivityScheduleService.TryGet(value, out var schedule)
                    ? (_, now) => schedule.IsOpen(now) : UnmetCondition;
            // No durable TRPG targets/cards, birthday story unlocks, or Theatre 4/5 progress exists.
            // Never substitute unrelated Explore, birthday date, or generic stage records for these modes.
            case 10119:
            case 10126:
            case 10150:
            case 17411:
            case 17826:
            default:
                return UnmetCondition;
        }
    }

    private static Func<Session, DateTimeOffset, bool> CompileCgFormula(string formula, Func<int, Func<Session, DateTimeOffset, bool>> compile)
    {
        int position = 0;
        bool valid = true;
        var result = Expression(0);
        SkipWhitespace();
        return valid && position == formula.Length ? result : UnmetCondition;

        void SkipWhitespace()
        {
            while (position < formula.Length && char.IsWhiteSpace(formula[position])) position++;
        }
        Func<Session, DateTimeOffset, bool> Expression(int depth)
        {
            var value = Atom(depth);
            SkipWhitespace();
            // EN XConditionFormula gives '&' and '|' equal precedence, evaluated left to right.
            while (position < formula.Length && formula[position] is '&' or '|')
            {
                char op = formula[position++];
                var left = value;
                var right = Atom(depth);
                value = op == '&' ? (session, now) => left(session, now) && right(session, now)
                    : (session, now) => left(session, now) || right(session, now);
                SkipWhitespace();
            }
            return value;
        }
        Func<Session, DateTimeOffset, bool> Atom(int depth)
        {
            SkipWhitespace();
            if (position == formula.Length || depth > 32) { valid = false; return UnmetCondition; }
            if (formula[position] == '!')
            {
                position++;
                var operand = Atom(depth + 1);
                return (session, now) => !operand(session, now);
            }
            if (formula[position] == '(')
            {
                position++;
                var nested = Expression(depth + 1);
                SkipWhitespace();
                if (position == formula.Length || formula[position++] != ')') valid = false;
                return nested;
            }
            int start = position;
            while (position < formula.Length && char.IsAsciiDigit(formula[position])) position++;
            if (start == position || !int.TryParse(formula.AsSpan(start, position - start), out int id) || id <= 0)
            {
                valid = false;
                return UnmetCondition;
            }
            return compile(id);
        }
    }
}
