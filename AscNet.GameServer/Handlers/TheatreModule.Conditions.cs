using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.condition;
using AscNet.Table.V2.share.theatre;

namespace AscNet.GameServer.Handlers;

internal static partial class TheatreModule
{
    private static readonly Lazy<Dictionary<int, Func<Session, TheatreData, Mutation?, bool>?>> GeneralConditions = new(BuildGeneralConditions);
    private static readonly Lazy<Dictionary<int, TheatreDecorationTable>> ConditionDecorations = new(() =>
        Rows<TheatreDecorationTable>().ToDictionary(row => row.Id));

    internal static bool IsConditionSatisfied(Mutation mutation, int conditionId) =>
        EvaluateCondition(mutation.Session, mutation.Data, mutation, conditionId);

    internal static bool IsConditionSatisfied(Session session, int conditionId) =>
        EvaluateCondition(session, session.player.Theatre.Data, null, conditionId);

    internal static bool IsCommonShop(uint shopId) => GetCommonShopEventCause(shopId) != 0;

    internal static int GetCommonShopEventCause(uint shopId)
    {
        foreach (var row in Rows<TheatreConfigTable>())
        {
            if (row.Value != shopId) continue;
            // LOCAL: journal purchases by the authored normal/special shop configuration.
            if (row.Key == "ShopIdByNormal") return 92004;
            if (row.Key == "ShopIdBySpecial") return 92008;
        }
        return 0;
    }

    private static bool EvaluateCondition(Session session, TheatreData data, Mutation? mutation, int conditionId) =>
        conditionId == 0 || conditionId > 0 && GeneralConditions.Value.TryGetValue(conditionId, out var predicate)
            && predicate is not null && predicate(session, data, mutation);

    private static Dictionary<int, Func<Session, TheatreData, Mutation?, bool>?> BuildGeneralConditions()
    {
        var rows = Rows<ConditionTable>().ToDictionary(row => row.Id);
        Dictionary<int, Func<Session, TheatreData, Mutation?, bool>?> predicates = [];
        HashSet<int> visiting = [];
        foreach (int id in rows.Keys) Compile(id);
        return predicates;

        Func<Session, TheatreData, Mutation?, bool>? Compile(int id)
        {
            if (predicates.TryGetValue(id, out var cached)) return cached;
            if (visiting.Count >= 32 || !rows.TryGetValue(id, out var row) || !visiting.Add(id)) return null;
            var predicate = string.IsNullOrWhiteSpace(row.Formula)
                ? CompileGeneralConditionLeaf(row) : CompileGeneralConditionFormula(row.Formula, Compile);
            visiting.Remove(id);
            predicates[id] = predicate;
            return predicate;
        }
    }

    private static Func<Session, TheatreData, Mutation?, bool>? CompileGeneralConditionLeaf(ConditionTable row)
    {
        if (row.Params.Count == 0) return null;
        int value = row.Params[0];
        int second = row.Params.Count > 1 ? row.Params[1] : 0;
        switch (row.Type)
        {
            // XConditionManager 17000/17003 use distinct exact-ID records, not ordinals or balances.
            case 17000: return (_, data, _) => data.PassChapterId.Contains(value);
            case 17001 when row.Params.Count >= 2:
                return (_, data, _) => data.PassEventRecord.TryGetValue(value, out var steps)
                    && steps.GetValueOrDefault(second);
            case 17002 when row.Params.Count >= 2:
                return (_, data, _) =>
                {
                    int level = 0;
                    foreach (int id in data.Decorations)
                        if (ConditionDecorations.Value.TryGetValue(id, out var decoration) && decoration.DecorationId == value)
                            level = Math.Max(level, decoration.Lv ?? 0);
                    // Lua explicitly uses >=, even for zero; descriptions suggesting absence are stale.
                    return level >= second;
                };
            case 17003: return (_, data, _) => data.EndingRecord.Contains(value);
            case 23001:
                // The scheduler owns the approved 803/804/805 decoration-release exception.
                // All other time IDs retain their authored schedule; unknown clocks fail closed.
                return (session, _, _) => ActivityScheduleService.IsOpen(value, DateTimeOffset.UtcNow);
            case 10151 when row.Params.Count >= 2:
                return (session, _, _) => session.player.Dorm.Characters.Any(character => character.CharacterId == value)
                    == (second > 0);
            case 11101 when row.Params.Count >= 2:
                return (session, _, mutation) => (mutation?.Balance(value)
                    ?? session.inventory.Items.Where(item => item.Id == value).Sum(item => (long)item.Count)) >= second;
            case 10111:
                return (session, _, mutation) => mutation?.IsTaskClaimed(value)
                    ?? session.player.MissionProgress.ClaimedTaskIds.Contains(value);
            case 10101:
            case 10102:
            case 10105:
                return (session, _, _) => LifeTreeModule.ConditionSatisfied(session, row);
            default: return null;
        }
    }

    private static Func<Session, TheatreData, Mutation?, bool>? CompileGeneralConditionFormula(string formula,
        Func<int, Func<Session, TheatreData, Mutation?, bool>?> compile)
    {
        // XConditionFormula evaluates & and | left-to-right with equal precedence.
        int position = 0;
        bool valid = true;
        var result = Expression(0);
        SkipWhitespace();
        return valid && position == formula.Length ? result : null;

        void SkipWhitespace()
        {
            while (position < formula.Length && char.IsWhiteSpace(formula[position])) position++;
        }
        Func<Session, TheatreData, Mutation?, bool>? Expression(int depth)
        {
            var left = Atom(depth);
            SkipWhitespace();
            while (position < formula.Length && formula[position] is '&' or '|')
            {
                char op = formula[position++];
                var right = Atom(depth);
                var previous = left;
                left = previous is null || right is null ? null : op == '&'
                    ? (session, data, mutation) => previous(session, data, mutation) && right(session, data, mutation)
                    : (session, data, mutation) => previous(session, data, mutation) || right(session, data, mutation);
                SkipWhitespace();
            }
            return left;
        }
        Func<Session, TheatreData, Mutation?, bool>? Atom(int depth)
        {
            SkipWhitespace();
            if (position == formula.Length || depth > 32) { valid = false; return null; }
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
                return null;
            }
            return compile(id);
        }
    }
}
