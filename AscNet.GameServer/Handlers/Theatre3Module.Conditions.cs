using AscNet.Common.Util;
using AscNet.Table.V2.share.condition;
using AscNet.Table.V2.share.theatre3;

namespace AscNet.GameServer.Handlers;

internal static partial class Theatre3Module
{
    private static readonly Lazy<Dictionary<int, Func<Mutation, bool>?>> GeneralConditions = new(BuildGeneralConditions);

    internal static bool IsConditionSatisfied(Mutation mutation, int conditionId) => conditionId == 0
        || conditionId > 0 && GeneralConditions.Value.TryGetValue(conditionId, out var predicate)
            && predicate is not null && predicate(mutation);

    private static Dictionary<int, Func<Mutation, bool>?> BuildGeneralConditions()
    {
        var rows = TableReaderV2.Parse<ConditionTable>().ToDictionary(row => row.Id);
        Dictionary<int, Func<Mutation, bool>?> predicates = [];
        HashSet<int> visiting = [];
        foreach (int id in rows.Keys) Compile(id);
        return predicates;

        Func<Mutation, bool>? Compile(int id)
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

    private static Func<Mutation, bool>? CompileGeneralConditionLeaf(ConditionTable row)
    {
        // XConditionManager.lua 1540–1668 omits server-only discriminators. The approved
        // local rules use accepted mutation counters, never the client's placeholder false.
        if (row.Type == 17202) return m => m.Data.EndingRecord.Count > 0;
        if (row.Params.Count == 0) return null;
        int value = row.Params[0];
        int second = row.Params.Count > 1 ? row.Params[1] : 0;
        switch (row.Type)
        {
            // The client takes a chapter ordinal, not Theatre3Chapter.Id.
            case 17200: return m => value <= 1 || m.Data.PassChapterIds.Contains(value - 1);
            case 17201: return m => m.State.TotalFightCount >= value;
            case 17205: return m => m.State.TotalCoinSpent >= value;
            case 17207: return m => m.Data.TotalAllPassCount >= value;
            case 17208: return m => value == 0 || m.Data.EndingRecord.Contains(value);
            // Numeric Params override the conflicting description on row 1020208.
            // Acquisitions count even if the item is subsequently consumed or expires.
            case 17210 when row.Params.Count >= 2:
                return m => m.Data.DifficultyId > 0 && m.State.RunItemsObtainedByQuality.GetValueOrDefault(second) >= value;
            case 17211: return m => m.Data.UnlockStrengthTree.Contains(value);
            // Lua drops the second parameter; authoritative rows explicitly use both 0 and 1.
            // Honor absence as well as presence, but never satisfy a run gate outside a run.
            case 17212 when row.Params.Count >= 2 && second is 0 or 1:
                return m => m.Data.DifficultyId > 0 && m.State.RunPassedEventStepIds.Contains(value) == (second == 1);
            case 17213 when row.Params.Count >= 2:
                return m => m.Data.PassDifficultyRecords.TryGetValue(value, out var endings)
                    && endings.Count > 0 && (second == 0 || endings.Contains(second));
            case 17214 when row.Params.Count >= 2:
                return m => m.State.ItemsObtainedByQuality.GetValueOrDefault(second) >= value;
            // Local server rule: cumulative transitions into an active suit, not equipped pieces.
            case 17215: return m => m.State.TotalActivatedSuitCount >= value;
            case 17216: return m => m.Data.DifficultyId > 0 && m.State.CompletedChapterIds.Contains(value);
            case 17217: return m => m.Data.DifficultyId > 0 && m.State.RunPassedNodeIds.Contains(value);
            case 17218: return m => !m.Data.UnlockStrengthTree.Contains(value);
            case 17219:
                var level = TableReaderV2.Parse<Theatre3QubitLevelTable>().Find(level => level.Level == value);
                return level is null ? null : m => m.Data.DifficultyId > 0
                    && (long)m.Data.QubitValueA + m.Data.QubitValueB >= level.Exp;
            case 17220 when row.Params.Count >= 2 && second is 0 or 1:
                return m => m.State.PassedEventStepIds.Contains(value) == (second == 1);
            case 17221 when row.Params.Count >= 2 && value is 1 or 2:
                return m =>
                {
                    if (m.Data.DifficultyId <= 0) return false;
                    int count = 0;
                    // The client counts suit containers, including incomplete sets, per position.
                    // ponytail: bounded equipment inventory uses an allocation-free quadratic scan;
                    // index suit containers if the mode ever permits unbounded equipment.
                    for (int i = 0; i < m.Data.Equips.Count; i++)
                    {
                        var equip = m.Data.Equips[i];
                        if (equip.SuitId <= 0 || equip.Pos <= 0) continue;
                        bool counted = false, quantum = false;
                        for (int j = 0; j < m.Data.Equips.Count; j++)
                        {
                            var other = m.Data.Equips[j];
                            if (other.Pos != equip.Pos || other.SuitId != equip.SuitId) continue;
                            counted |= j < i;
                            quantum |= other.QubitActive;
                        }
                        if (!counted && !quantum) count++;
                    }
                    return value == 1 ? count >= second : count <= second;
                };
            case 17222 when row.Params.Count >= 2 && second is 0 or 1:
                var item = TableReaderV2.Parse<Theatre3ItemTable>().Find(item => item.Id == value);
                // All authoritative item rows omit ExpireTime; the generated schema therefore
                // has no expiry property. These items remain owned until explicitly consumed.
                return item is null ? null : m => m.Data.DifficultyId > 0
                    && m.Data.Items.Any(owned => owned.ItemId == value) == (second == 1);
            case 17223 when row.Params.Count >= 2 && value is 1 or 2:
                return m => m.Data.DifficultyId > 0 && (second == 0
                    ? (value == 1 ? m.Data.QubitValueA : m.Data.QubitValueB) == 0
                    : (value == 1 ? m.Data.QubitValueA : m.Data.QubitValueB) >= second);
            case 17224 when value is 0 or 1:
                // No animation acknowledgement RPC exists. Approved local substitute: persist
                // eligibility once quantum reaches the first configured level, across all runs.
                return m => m.State.QuantumTipUnlocked == (value == 1);
            case 17225: return m => m.Data.FireItemCount >= value;
            case 17226 when row.Params.Count >= 2 && second is 0 or 1:
                return m => m.Data.DifficultyId > 0 && m.Data.PassEventFightNodes.Contains(value) == (second == 1);
            default:
                // Delegate generic leaves to the existing engine. Unsupported types fail closed;
                // no semantics/rows exist for Theatre3 gaps 17203/4/6/9 in this client content.
                return row.Type is 10101 or 10102 or 10105
                    ? m => LifeTreeModule.ConditionSatisfied(m.Session, row) : null;
        }
    }

    private static Func<Mutation, bool>? CompileGeneralConditionFormula(string formula, Func<int, Func<Mutation, bool>?> compile)
    {
        // Same left-to-right equal precedence as XConditionFormula and ArchiveCgModule.
        int position = 0;
        bool valid = true;
        var result = Expression(0);
        SkipWhitespace();
        return valid && position == formula.Length ? result : null;

        void SkipWhitespace()
        {
            while (position < formula.Length && char.IsWhiteSpace(formula[position])) position++;
        }
        Func<Mutation, bool>? Expression(int depth)
        {
            var left = Atom(depth);
            SkipWhitespace();
            while (position < formula.Length && formula[position] is '&' or '|')
            {
                char op = formula[position++];
                var right = Atom(depth);
                var previous = left;
                left = previous is null || right is null ? null : op == '&'
                    ? m => previous(m) && right(m) : m => previous(m) || right(m);
                SkipWhitespace();
            }
            return left;
        }
        Func<Mutation, bool>? Atom(int depth)
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
