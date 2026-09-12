using System.Globalization;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Table.V2.share.condition;
using AscNet.Table.V2.share.theatre5;
using AscNet.Table.V2.share.functional;

namespace AscNet.GameServer.Handlers;

internal static partial class Theatre5Module
{
    private static readonly Lazy<Dictionary<int, Func<Player?, PlayerTheatre5State, bool>?>> CommonConditions = new(BuildCommonConditions);
    private static readonly Lazy<Dictionary<int, Theatre5ItemTable>> ConditionItems = new(() => Rows<Theatre5ItemTable>().ToDictionary(row => row.Id));

    internal static bool IsConditionMet(Session session, int conditionId, PlayerTheatre5State? state = null) =>
        IsConditionMet(session.player, conditionId, state);

    // Zero denotes an absent gate. Missing rows, unsupported leaves, invalid operands,
    // cycles and malformed formulas fail closed, including under OR and negation.
    // Authored robots have run state but no account identity. Account leaves reject null.
    internal static bool IsConditionMet(Player? player, int conditionId, PlayerTheatre5State? state = null) => conditionId == 0
        || conditionId > 0 && (state ?? player?.Theatre5) is { } sourceState
            && CommonConditions.Value.TryGetValue(conditionId, out var predicate)
            && predicate is not null && predicate(player, sourceState);

    private static Dictionary<int, Func<Player?, PlayerTheatre5State, bool>?> BuildCommonConditions()
    {
        var rows = Rows<ConditionTable>().ToDictionary(row => row.Id);
        Dictionary<int, Func<Player?, PlayerTheatre5State, bool>?> predicates = [];
        HashSet<int> visiting = [];
        foreach (int id in rows.Keys) Compile(id);
        return predicates;

        Func<Player?, PlayerTheatre5State, bool>? Compile(int id)
        {
            if (predicates.TryGetValue(id, out var cached)) return cached;
            if (visiting.Count >= 32 || !rows.TryGetValue(id, out var row) || !visiting.Add(id)) return null;
            // Authored formula rows omit Type; the generated nullable field is null, not zero.
            var predicate = !string.IsNullOrWhiteSpace(row.Formula)
                ? CompileCommonFormula(row.Formula, Compile) : CompileCommonLeaf(row);
            visiting.Remove(id);
            predicates[id] = predicate;
            return predicate;
        }
    }

    private static Theatre5AdventureData? ConditionAdventure(PlayerTheatre5State state) => state.Data.PvpType switch
    {
        1 => state.Data.PvpAdventureData,
        2 => state.Data.PveAdventureData,
        _ => null
    };

    private static bool CompareCondition(long actual, int comparison, int expected) => comparison switch
    {
        1 => actual == expected,
        2 => actual >= expected,
        3 => actual <= expected,
        _ => false
    };

    private static Func<Player?, PlayerTheatre5State, bool>? CompileCommonLeaf(ConditionTable row)
    {
        if (row.Params.Count == 0) return null;
        int first = Convert.ToInt32(row.Params[0], CultureInfo.InvariantCulture);
        int second = row.Params.Count > 1 ? Convert.ToInt32(row.Params[1], CultureInfo.InvariantCulture) : 0;
        int third = row.Params.Count > 2 ? Convert.ToInt32(row.Params[2], CultureInfo.InvariantCulture) : 0;
        switch (row.Type)
        {
            case 10101: return (player, _) => player is not null && player.PlayerData.Level >= first;
            case 17826 when row.Params.Count >= 2 && second is 0 or 1:
                // XConditionManager:2673 and XTheatre5PVERougeData:224: any storyline,
                // completed contents only, never the currently selected content.
                return (_, state) => state.Data.PveStoryLines.Values.Any(story => story.FinishContents.Contains(first)) == (second == 1);
            case 17833 when first is 1 or 2:
                // Condition operands reverse the wire's PVP=1/PVE=2 enum.
                return (_, state) => state.Data.PvpType == (first == 1 ? 2 : 1);
            case 17831 when row.Params.Count >= 2:
                var rank = Rows<Theatre5RankTable>().Find(rank => rank.Id == second);
                return rank is null ? null : (player, state) => ConditionFeatureAvailable(player, state)
                    && Rows<Theatre5ActivityTable>().Any(activity => activity.Id == state.Data.ActivityId
                        && Convert.ToInt32(activity.TimeId, CultureInfo.InvariantCulture) == 46401)
                    && (first > 0 ? state.Data.Characters.TryGetValue(first, out var character) && character.Rating >= rank.Rating
                        : state.Data.Characters.Values.Any(character => character.Rating >= rank.Rating));
            case 17832 when row.Params.Count >= 2:
                // Agency:755 and Model's GetCharacterWinGameCount use CommonFightCnt.
                return (player, state) => ConditionFeatureAvailable(player, state) && (first > 0
                    ? state.Data.CommonFightCnt.GetValueOrDefault(first) >= second
                    : state.Data.CommonFightCnt.Count > 0 && state.Data.CommonFightCnt.Values.Sum(count => (long)count) >= second);
            case 17821 when row.Params.Count >= 2 && second is 0 or 1:
                return (_, state) => state.Data.PveClues.ContainsKey(first) == (second == 1);
            case 17820 when row.Params.Count >= 3 && second is >= 1 and <= 3:
                return (_, state) => CompareCondition(state.ChapterPassCounts.GetValueOrDefault(first), second, third);
            case 17830:
                return (_, state) => state.Data.PveAdventureData is { PveChapterData: not null } adventure && adventure.CharacterId == first;
            case 17822 or 17827 or 17828 or 17829 when row.Params.Count >= 2 && first is >= 1 and <= 3:
                return (_, state) => state.Data.PveAdventureData is { PveChapterData: { } chapter } adventure
                    && CompareCondition(row.Type switch
                    {
                        17822 => chapter.CurPveChapterLevel?.Level ?? 0,
                        17827 => chapter.BattleStatus.Count,
                        17828 => adventure.Health,
                        _ => adventure.GoldNum
                    }, first, second);
            case 17801:
                // Authored ownership gate includes stored skills, not only deployed skills.
                return (_, state) => ConditionAdventure(state) is { } adventure
                    && (adventure.BagData.SkillDict.Values.Any(item => item.ItemId == first)
                        || adventure.BagData.BagItemDict.Values.Any(item => item.ItemType == 1 && item.ItemId == first)
                        || adventure.BagData.TempItemDict.Values.Any(item => item.ItemType == 1 && item.ItemId == first));
            case 17803 or 17804 or 17840 or 17844 or 17846 or 17849 when row.Params.Count >= 2 && first is >= 1 and <= 3:
                // Server-only leaves: authored Condition operands define 1=equal,2=>=,3=<=.
                // Local policy maps these to accepted run outcomes, never account totals.
                // Numeric operands win over inconsistent translated descriptions (1050255).
                return (_, state) => ConditionAdventure(state) is { } adventure && CompareCondition(row.Type switch
                {
                    17803 => adventure.RoundNum,
                    17804 => adventure is Theatre5PvpAdventureData ? state.PvpWinCount
                        : ((Theatre5PveAdventureData)adventure).PveChapterData?.BattleStatus.Count(win => win) ?? 0,
                    17840 => adventure.EnterShopCnt,
                    17844 => adventure is Theatre5PvpAdventureData ? state.PvpLoseCount
                        : ((Theatre5PveAdventureData)adventure).PveChapterData?.BattleStatus.Count(win => !win) ?? 0,
                    17846 => adventure.CharacterLv,
                    _ => adventure is Theatre5PvpAdventureData ? state.PvpContinueWin
                        : ((Theatre5PveAdventureData)adventure).PveChapterData?.ContinueWin ?? 0
                }, first, second);
            case 17850 when row.Params.Count >= 3 && first >= 0 && second > 0 && third >= 0:
                // Authored tag/quality/count operands, corroborated by BattleAgencyCom:561–585.
                // Only equipped runes count; the debug routine's unknown-pass is NOT copied.
                return (_, state) => ConditionAdventure(state) is { } adventure
                    && adventure.BagData.RuneDict.Values.Count(item => ConditionItems.Value.TryGetValue(item.ItemId, out var config)
                        && config.Quality == second && (first == 0 || config.Tags.Contains(first))) >= third;
            default: return null;
        }
    }

    private static bool ConditionFeatureAvailable(Player? player, PlayerTheatre5State state) =>
        player is not null && Rows<FunctionalOpenTable>().Single(row => row.Id == 10491).Condition
            .Where(id => id > 0).All(id => IsConditionMet(player, id, state));

    private static Func<Player?, PlayerTheatre5State, bool>? CompileCommonFormula(string formula,
        Func<int, Func<Player?, PlayerTheatre5State, bool>?> compile)
    {
        // Existing Theatre3/ArchiveCg parser pattern. EN XConditionFormula:8–14 gives
        // & and | equal, left-to-right precedence; unary ! binds more tightly.
        int position = 0;
        bool valid = true;
        var result = Expression(0);
        SkipWhitespace();
        return valid && position == formula.Length ? result : null;

        void SkipWhitespace()
        {
            while (position < formula.Length && char.IsWhiteSpace(formula[position])) position++;
        }
        Func<Player?, PlayerTheatre5State, bool>? Expression(int depth)
        {
            var left = Atom(depth);
            SkipWhitespace();
            while (position < formula.Length && formula[position] is '&' or '|')
            {
                char op = formula[position++];
                var right = Atom(depth);
                var previous = left;
                left = previous is null || right is null ? null : op == '&'
                    ? (player, state) => previous(player, state) && right(player, state)
                    : (player, state) => previous(player, state) || right(player, state);
                SkipWhitespace();
            }
            return left;
        }
        Func<Player?, PlayerTheatre5State, bool>? Atom(int depth)
        {
            SkipWhitespace();
            if (position == formula.Length || depth > 32) { valid = false; return null; }
            if (formula[position] == '!')
            {
                position++;
                var operand = Atom(depth + 1);
                return operand is null ? null : (player, state) => !operand(player, state);
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
            if (start == position || !int.TryParse(formula.AsSpan(start, position - start), NumberStyles.None,
                    CultureInfo.InvariantCulture, out int id) || id <= 0)
            {
                valid = false;
                return null;
            }
            return compile(id);
        }
    }
}
