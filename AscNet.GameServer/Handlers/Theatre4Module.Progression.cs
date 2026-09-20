using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.functional;
using AscNet.Table.V2.share.theatre4;
using System.Globalization;
using LoginTask = AscNet.Common.MsgPack.NotifyTaskData.NotifyTaskDataTaskData.NotifyTaskDataTaskDataTask;
using SyncTask = AscNet.Common.MsgPack.NotifyTask.NotifyTaskTasks.NotifyTaskTasksTask;
using GateConditionTable = AscNet.Table.V2.share.condition.ConditionTable;
using TaskConditionTable = AscNet.Table.V2.share.task.ConditionTable;
using TaskTable = AscNet.Table.V2.share.task.TaskTable;

namespace AscNet.GameServer.Handlers;

// Awakening Tundra account progression: activity availability, login payload, permanent
// atlas/task/achievement predicates, battle-pass claims and tech unlock.
internal static partial class Theatre4Module
{
    // EN XItemManager:116 Theatre4TechTreeCoin — the account item spent by Theatre4Tech.Cost.
    private const int TechTreeCoinItemId = 96200;

    internal static List<int> Config(string key) => Rows<Theatre4ConfigTable>().Single(row => row.Key == key).Values;

    internal static int ConfigInt(string key, int index = 0) => Config(key)[index];

    // EN FunctionalOpen row 10470 "Awakening Tundra" (Condition 1020681, Commandant level 80).
    private static bool FeatureAvailable(Session session) => Rows<FunctionalOpenTable>()
        .Single(row => row.Id == 10470).Condition.Where(id => id > 0).All(id => IsConditionMet(session, id));

    private static bool ScheduleOpen(int activityId) => Rows<Theatre4ActivityTable>()
        .Any(row => row.Id == activityId && row.TimeId > 0 && ActivityScheduleService.IsOpen(row.TimeId, DateTimeOffset.UtcNow));

    internal static void EnsureAvailable(Session session)
    {
        Require(FeatureAvailable(session), 20218001);
        Theatre4ActivityData data = session.player.Theatre4.Data;
        Require(data.ActivityId > 0, 20218001);
        // 20218085 Theatre4ActivityConfigError: ActivityId has no authored row.
        Require(Rows<Theatre4ActivityTable>().Any(row => row.Id == data.ActivityId), 20218085);
        Require(ScheduleOpen(data.ActivityId), 20218001);
    }

    internal static void PrepareLogin(Session session)
    {
        ResumePending(session, forLogin: true);
        NormalizeEndingHistory(mutationState: session.player.Theatre4);
        Mutation m = new(session);
        m.State.RequestReceipts.Clear();
        // Restart receipts are transport-scoped too; preserve the frozen encounter itself but
        // discard packet-id/seed bindings from the previous connection.
        m.State.ActiveEncounter?.RestartReceipts.Clear();
        if (FeatureAvailable(session))
        {
            if (m.State.Epoch == 0) m.State.Epoch = 1;
            // Only an open authored activity is adopted; a closed one leaves the mode closed.
            if (m.Data.ActivityId == 0 && Rows<Theatre4ActivityTable>()
                    .FirstOrDefault(row => row.TimeId > 0 && ActivityScheduleService.IsOpen(row.TimeId, DateTimeOffset.UtcNow)) is { } open)
                m.Data.ActivityId = open.Id;
        }
        RecoverLegacyBoxContentIds(m.State);
        RecoverLegacyShopStates(m.State);
        RecoverLegacyFightGroupIds(m.State);
        // Repair legacy build plots only in the live run and rewindable day snapshots.
        Theatre4AdventureData? activeAdventure = m.Data.AdventureData;
        if (activeAdventure is not null)
            foreach (Theatre4ChapterData chapter in activeAdventure.Chapters.Where(chapter => !chapter.IsPass))
                RepairLegacyBuildPlot(m, activeAdventure, chapter);
        foreach (Theatre4AdventureData snapshot in m.State.TracebackSnapshots.Values)
        {
            m.Data.AdventureData = snapshot;
            foreach (Theatre4ChapterData chapter in snapshot.Chapters.Where(chapter => !chapter.IsPass))
                RepairLegacyBuildPlot(m, snapshot, chapter);
        }
        m.Data.AdventureData = activeAdventure;
        // Run-scoped runtime state exists only alongside an adventure; drop leftovers so a
        // stale in-battle encounter or timeback snapshot can never outlive its run.
        if (m.Data.AdventureData is null)
        {
            m.State.ActiveEncounter = null;
            m.State.TracebackSnapshots.Clear();
        }
        Persist(m, string.Empty, string.Empty, null);
        // Login is deliberately not the explicit continuation boundary: RecoverCombatOnLogin
        // preserves an active run's frozen encounter for the native combat reconnect path.
        RecoverCombatOnLogin(session);
    }
    private static void NormalizeEndingHistory(PlayerTheatre4State mutationState)
    {
        HashSet<int> failedIds = Rows<Theatre4EndingTable>()
            .Where(row => row.PassType == 1).Select(row => row.Id).ToHashSet();
        foreach (int id in mutationState.Data.Endings.Keys.Where(failedIds.Contains).ToList())
            mutationState.Data.Endings.Remove(id);
    }


    private static void RecoverLegacyBoxContentIds(PlayerTheatre4State state)
    {
        List<Theatre4BoxGroupTable> rows = Rows<Theatre4BoxGroupTable>();
        IEnumerable<Theatre4AdventureData?> snapshots = [state.Data.AdventureData, state.PendingSettleAdventure,
            .. state.TracebackSnapshots.Values];
        foreach (Theatre4GridData grid in snapshots.Where(snapshot => snapshot is not null)
                     .SelectMany(snapshot => snapshot!.Chapters).SelectMany(chapter => chapter.Grids)
                     .Where(grid => grid.Type == T4Grid.Box))
        {
            if (rows.Any(row => row.Id == grid.ContentId && row.GroupId == grid.ContentGroup)) continue;
            Theatre4BoxGroupTable? recovered = rows.Where(row =>
                    row.GroupId == grid.ContentGroup && row.DropId == grid.ContentId)
                .MinBy(row => row.Id);
            // AscNet Theatre4 legacy recovery policy: old saves retained reward/group but lost the
            // cosmetic row choice, so choose the lowest authored identity without rerolling reward.
            if (recovered is not null) grid.ContentId = recovered.Id;
        }
    }
    private static void RecoverLegacyShopStates(PlayerTheatre4State state)
    {
        IEnumerable<Theatre4AdventureData?> snapshots = [state.Data.AdventureData, state.PendingSettleAdventure,
            .. state.TracebackSnapshots.Values];
        foreach (Theatre4GridData grid in snapshots.Where(snapshot => snapshot is not null)
                     .SelectMany(snapshot => snapshot!.Chapters).SelectMany(chapter => chapter.Grids)
                     .Where(grid => grid.Type == T4Grid.Shop && grid.State == T4State.Explored && grid.Shop is not null))
            grid.State = T4State.Processed;
    }

    // FightData.FightGroupId is the authored Theatre4FightGroup unique Id on the wire. Older
    // snapshots stored the selection bucket (GroupId) there; migrate only when the existing
    // grid's bucket and Fight.Id identify one unambiguous authored row. ActiveEncounter keeps its
    // bucket because it is server-side lookup state, not the client FightData field. Legacy HP
    // migration is committed with the identity repair so an ambiguous alias cannot be scaled
    // repeatedly on later logins.
    private static void RecoverLegacyFightGroupIds(PlayerTheatre4State state)
    {
        List<Theatre4FightGroupTable> rows = Rows<Theatre4FightGroupTable>();
        IEnumerable<Theatre4AdventureData?> snapshots = [state.Data.AdventureData, state.PendingSettleAdventure,
            .. state.TracebackSnapshots.Values];
        foreach (Theatre4GridData grid in snapshots.Where(snapshot => snapshot is not null)
                     .SelectMany(snapshot => snapshot!.Chapters).SelectMany(chapter => chapter.Grids)
                     .Where(grid => grid.Fight is not null && grid.ContentId > 0))
        {
            Theatre4FightData fight = grid.Fight!;
            int bucket = grid.ContentGroup;
            if (bucket <= 0 || fight.FightGroupId <= 0) continue;

            bool canonical = rows.Any(row => row.Id == fight.FightGroupId
                && row.GroupId == bucket && row.FightId == grid.ContentId);
            if (canonical || fight.FightGroupId != bucket) continue;

            List<Theatre4FightGroupTable> legacy = rows.Where(row =>
                row.GroupId == bucket && row.FightId == grid.ContentId).ToList();
            // Multiple aliases leave both fields untouched: their HP unit provenance is ambiguous.
            if (legacy.Count != 1) continue;

            // This exact bucket+fight row is the only provenance that proves the old 0..100
            // field. Scale it once, then repair the row identity in the same mutation.
            Theatre4FightGroupTable authored = legacy[0];
            if (fight.HpPercent is >= 0 and <= 100)
                fight.HpPercent = checked(fight.HpPercent * 100);
            fight.FightGroupId = authored.Id;
        }
    }


    internal static NotifyTheatre4ActivityData BuildLoginData(Session session) =>
        new() { Data = Clone(session.player.Theatre4.Data) };

    // Re-delivers the ending to a fresh client that relogs after settlement but before starting
    // the next adventure. Same packet and shape the settlement pushed; permanent progress and
    // reward grants are never re-applied.
    internal static void SendRecoveredSettlement(Session session)
    {
        PlayerTheatre4State state = session.player.Theatre4;
        if (state.PendingSettleAdventure is null || state.Data.PreAdventureSettleData is null) return;
        session.SendPush(new NotifyTheatre4AdventureSettle
        {
            SettleData = Clone(state.Data.PreAdventureSettleData),
            AdventureData = Clone(state.PendingSettleAdventure)
        });
    }

    //region conditions

    private static readonly Lazy<Dictionary<int, Func<Player?, PlayerTheatre4State, bool>?>> GateConditions =
        new(BuildGateConditions);

    internal static bool IsConditionMet(Session session, int conditionId) =>
        IsConditionMet(session.player, conditionId, session.player.Theatre4);

    internal static bool IsConditionMet(Mutation mutation, int conditionId) =>
        IsConditionMet(mutation.Session.player, conditionId, mutation.State);

    // Zero denotes an absent gate. Missing rows, unsupported leaves, invalid operands,
    // cycles and malformed formulas fail closed, including under OR and negation.
    // Authored robots have run state but no account identity. Account leaves reject null.
    internal static bool IsConditionMet(Player? player, int conditionId, PlayerTheatre4State? state) => conditionId == 0
        || conditionId > 0 && (state ?? player?.Theatre4) is { } sourceState
            && GateConditions.Value.TryGetValue(conditionId, out var predicate)
            && predicate is not null && predicate(player, sourceState);

    private static Dictionary<int, Func<Player?, PlayerTheatre4State, bool>?> BuildGateConditions()
    {
        var rows = Rows<GateConditionTable>().ToDictionary(row => row.Id);
        Dictionary<int, Func<Player?, PlayerTheatre4State, bool>?> predicates = [];
        HashSet<int> visiting = [];
        foreach (int id in rows.Keys) Compile(id);
        return predicates;

        Func<Player?, PlayerTheatre4State, bool>? Compile(int id)
        {
            if (predicates.TryGetValue(id, out var cached)) return cached;
            if (visiting.Count >= 32 || !rows.TryGetValue(id, out var row) || !visiting.Add(id)) return null;
            // Authored formula rows omit Type; the generated nullable field is null, not zero.
            var predicate = !string.IsNullOrWhiteSpace(row.Formula)
                ? CompileGateFormula(row.Formula, Compile) : CompileGateLeaf(row);
            visiting.Remove(id);
            predicates[id] = predicate;
            return predicate;
        }
    }

    // EN XConditionManager:2355-2490 Theatre4 leaves. Operand meanings come from the
    // evaluator comments; state sources come from the XTheatre4Agency checkers.
    private static Func<Player?, PlayerTheatre4State, bool>? CompileGateLeaf(GateConditionTable row)
    {
        List<int> p = row.Params;
        if (p.Count == 0) return null;
        int first = p[0];
        int second = p.Count > 1 ? p[1] : 0;
        int third = p.Count > 2 ? p[2] : 0;
        int fourth = p.Count > 3 ? p[3] : 0;
        switch (row.Type)
        {
            case 10101: return (player, _) => player is not null && player.PlayerData.Level >= first;
            // 17401 active talent ownership; the client checks every selected slot.
            case 17401 when p.Count >= 2 && first > 0 && second is 0 or 1:
                return (_, state) => state.Data.AdventureData is { } adventure
                    && (second == 0 ? !HasActiveTalent(adventure, first) : HasActiveTalent(adventure, first));
            // 17402 in-run event finish count; 0 means never finished.
            case 17402 when p.Count >= 2 && first > 0 && second >= 0:
                return (_, state) => state.Data.AdventureData is { } adventure
                    && CountReached(adventure.FinishEventIds.GetValueOrDefault(first), second);
            // 17403 in-run collection ownership; 0 checks absence.
            case 17403 when p.Count >= 2 && first > 0 && second is 0 or 1:
                return (_, state) => state.Data.AdventureData is { } adventure
                    && (second == 0 ? CountRunItems(adventure, first) == 0 : CountRunItems(adventure, first) > 0);
            // 17404 accumulated talent points of a color at or above the required total.
            case 17404 when p.Count >= 2 && first is >= 1 and <= 3 && second >= 0:
                return (_, state) => ColorValue(state, first, talent => talent.Point) >= second;
            // 17405 in-run color talent level.
            case 17405 when p.Count >= 2 && first is >= 1 and <= 3 && second >= 0:
                return (_, state) => ColorValue(state, first, talent => talent.Level) >= second;
            // 17406/17407 compare in-run gold and HP (1 >=, 2 <=).
            case 17406 when p.Count >= 2 && first is 1 or 2 && second >= 0:
                return (_, state) => state.Data.AdventureData is { } adventure
                    && (first == 1 ? adventure.Gold >= second : adventure.Gold <= second);
            case 17407 when p.Count >= 2 && first is 1 or 2 && second >= 0:
                return (_, state) => state.Data.AdventureData is { } adventure
                    && (first == 1 ? adventure.Hp >= second : adventure.Hp <= second);
            // 17408 a color has at least as many talent points as every other color.
            case 17408 when p.Count >= 1 && first is >= 1 and <= 3:
                return (_, state) => state.Data.AdventureData is { } adventure
                    && Enumerable.Range(T4Color.Red, T4Color.Blue - T4Color.Red + 1)
                        .Where(color => color != first)
                        .All(color => ColorValue(state, color, talent => talent.Point)
                            <= ColorValue(state, first, talent => talent.Point));
            // 17409 in-run prosperity at or above the required value.
            case 17409 when p.Count >= 1 && first >= 0:
                return (_, state) => state.Data.AdventureData is { } adventure && adventure.Prosperity >= first;
            // 17410/17411 permanent difficulty and ending completion counts (0 = never).
            case 17410 when p.Count >= 2 && first >= 0 && second >= 0:
                return (_, state) => (first == 0
                    ? state.Data.Difficultys.Values.Sum(value => (long)value)
                    : state.Data.Difficultys.GetValueOrDefault(first)) >= second;
            case 17411 when p.Count >= 2 && first >= 0 && second >= 0:
                return (_, state) => (first == 0
                    ? state.Data.Endings.Values.Sum(value => (long)value)
                    : state.Data.Endings.GetValueOrDefault(first)) >= second;
            // 17412 the current run finished the given fight (sweep/recruit/kill all count).
            case 17412 when p.Count >= 1 && first > 0:
                return (_, state) => state.Data.AdventureData is { } adventure
                    && adventure.FinishFightIds.Contains(first);
            case 17413 when p.Count >= 2 && first is >= 1 and <= 3:
                return (_, state) => state.Data.AdventureData is { } adventure
                    && (first switch
                    {
                        1 => adventure.Chapters.Count > second,
                        2 => adventure.Chapters.Count < second,
                        _ => adventure.Chapters.Count == second
                    });
            // 17414 a color is at the highest talent level among the three colors.
            case 17414 when p.Count >= 1 && first is >= 1 and <= 3:
                return (_, state) => state.Data.AdventureData is { } adventure
                    && adventure.Colors.All(other => other.Color == first
                        || other.Level <= ColorValue(state, first, talent => talent.Level));
            // 17415 external-loop event finish count; 0 means never finished.
            case 17415 when p.Count >= 2 && first > 0 && second >= 0:
                return (_, state) => CountReached(state.Data.GlobalFinishEventIds.GetValueOrDefault(first), second);
            // 17416 a processed grid at the authored map and coordinates.
            case 17416 when p.Count >= 3 && first > 0 && second >= 0 && third >= 0:
                return (_, state) => state.Data.AdventureData?.Chapters
                    .FirstOrDefault(chapter => chapter.MapId == first)?.Grids
                    .Any(grid => grid.PosX == second && grid.PosY == third && grid.State == T4State.Processed) == true;
            // 17430 current run difficulty; compare type 4 accepts any listed value.
            case 17430 when p.Count >= 2 && first is >= 1 and <= 4:
                return (_, state) => state.Data.AdventureData is { Difficulty: > 0 } adventure
                    && (first switch
                    {
                        1 => adventure.Difficulty == second,
                        2 => adventure.Difficulty >= second,
                        3 => adventure.Difficulty <= second,
                        _ => p.Skip(1).Contains(adventure.Difficulty)
                    });
            // 17434 current map (DLC).
            case 17434 when p.Count >= 1 && first > 0:
                return (_, state) => state.Data.AdventureData?.Chapters.LastOrDefault()?.MapId == first;
            // 17431/17433 have no evaluator in the shipped EN/CN Lua. AscNet local policy:
            // 17431 rows are [15, 0, compareType, value] and all measure the run's AwakeningPoint
            // (compareType follows 17430: 2 >=, 3 <=); params 1/2 select the retail resource and
            // are not modelled. 17433 rows are [inheritItemId, isOwn] mirroring 17403 ownership,
            // matching its only reachable row 1020775 "Red Purchased Kill not selected".
            case 17431 when p.Count >= 4 && third is 2 or 3 && fourth >= 0:
                return (_, state) => state.Data.AdventureData is { } adventure && (third == 2
                    ? adventure.AwakeningPoint >= fourth
                    : adventure.AwakeningPoint <= fourth);
            case 17433 when p.Count >= 1 && first > 0:
                return (_, state) => state.Data.AdventureData is { } adventure
                    && (second == 0 ? adventure.InheritItemId != first : adventure.InheritItemId == first);
            default: return null;
        }
    }

    private static bool HasActiveTalent(Theatre4AdventureData adventure, int talentId) =>
        adventure.Colors.Any(color => color.Slots.Any(slot => slot.Talents.Any(talent => talent.TalentId == talentId)));

    private static int CountRunItems(Theatre4AdventureData adventure, int itemId) =>
        adventure.Items.Count(item => item.ItemId == itemId)
        + adventure.Props.Count(item => item.ItemId == itemId);

    // Missing color entries read as zero, matching the client's GetColor*ById accessors.
    private static int ColorValue(PlayerTheatre4State state, int color, Func<Theatre4ColorTalentData, int> selector) =>
        state.Data.AdventureData?.Colors.FirstOrDefault(entry => entry.Color == color) is { } talent ? selector(talent) : 0;

    private static bool CountReached(int times, int count) => count == 0 ? times == 0 : times >= count;

    private static Func<Player?, PlayerTheatre4State, bool>? CompileGateFormula(string formula,
        Func<int, Func<Player?, PlayerTheatre4State, bool>?> compile)
    {
        // EN XConditionFormula:8-14 gives & and | equal, left-to-right precedence; unary !
        // binds more tightly.
        int position = 0;
        bool valid = true;
        var result = Expression(0);
        SkipWhitespace();
        return valid && position == formula.Length ? result : null;

        void SkipWhitespace()
        {
            while (position < formula.Length && char.IsWhiteSpace(formula[position])) position++;
        }
        Func<Player?, PlayerTheatre4State, bool>? Expression(int depth)
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
        Func<Player?, PlayerTheatre4State, bool>? Atom(int depth)
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

    //endregion

    //region permanent atlas

    // The handbooks are the permanent record: XTheatre4Model:816/835/854 key each list by id.
    internal static void AddItemAtlas(Mutation m, int itemId)
    {
        Require(itemId > 0, 1);
        if (m.Data.ItemsAtlas.Contains(itemId)) return;
        m.Data.ItemsAtlas.Add(itemId);
        PushAtlas(m);
    }

    internal static void AddTalentAtlas(Mutation m, int talentId)
    {
        Require(talentId > 0, 1);
        if (m.Data.TalentAtlas.Contains(talentId)) return;
        m.Data.TalentAtlas.Add(talentId);
        PushAtlas(m);
    }

    internal static void AddMapAtlas(Mutation m, int mapIndex)
    {
        Require(mapIndex > 0, 1);
        if (m.Data.MapAtlas.Contains(mapIndex)) return;
        m.Data.MapAtlas.Add(mapIndex);
        PushAtlas(m);
    }

    // NotifyTheatre4Atlas replaces all three lists, so one deduped snapshot per operation.
    private static void PushAtlas(Mutation m)
    {
        m.Pushes.RemoveAll(packet => packet.Name == nameof(NotifyTheatre4Atlas));
        m.Push(new NotifyTheatre4Atlas
        {
            ItemsAtlas = m.Data.ItemsAtlas,
            TalentAtlas = m.Data.TalentAtlas,
            MapAtlas = m.Data.MapAtlas
        });
    }

    //endregion

    //region battle pass

    // Cumulative threshold from EN XTheatre4SystemSubControl:72-87 (each row's NeedExp is a delta).
    private static long BattlePassThreshold(List<Theatre4BattlePassTable> levels, int level)
    {
        long total = 0;
        foreach (var row in levels)
        {
            total += row.NeedExp;
            if (row.Level == level) return total;
        }
        return long.MaxValue;
    }

    internal static void AddBattlePassExp(Mutation m, int amount)
    {
        Require(amount >= 0, 1);
        if (amount == 0) return;
        m.Data.TotalBattlePassExp = checked(m.Data.TotalBattlePassExp + amount);
        // Authority: NotifyTheatre4BattlePassExp carries the absolute total.
        m.Push(new NotifyTheatre4BattlePassExp { TotalBattlePassExp = m.Data.TotalBattlePassExp });
    }

    [RequestPacketHandler("Theatre4BattlePassGetRewardRequest")]
    public static void Theatre4BattlePassGetReward(Session session, Packet.Request packet) =>
        Handle<Theatre4BattlePassGetRewardRequest, Theatre4BattlePassGetRewardResponse>(session, packet, (m, request, response) =>
        {
            EnsureAvailable(session);
            // EN XEnumConst:2722 BattlePassGetRewardType: 1 GetOnce, 2 GetAll.
            Require(request.GetRewardType is 1 or 2, 20218107);
            List<Theatre4BattlePassTable> levels = Rows<Theatre4BattlePassTable>().OrderBy(row => row.Level).ToList();
            Require(levels.Count > 0, 20218103);

            List<Theatre4BattlePassTable> claimable;
            if (request.GetRewardType == 2)
            {
                long total = 0;
                claimable = [];
                foreach (var row in levels)
                {
                    total += row.NeedExp;
                    if (m.Data.TotalBattlePassExp >= total && !m.Data.BattlePassGotRewardIds.Contains(row.Level))
                        claimable.Add(row);
                }
                Require(claimable.Count > 0, 20218106);
            }
            else
            {
                var row = levels.FirstOrDefault(candidate => candidate.Level == request.Id);
                Require(row is not null, 20218103);
                Require(!m.Data.BattlePassGotRewardIds.Contains(row!.Level), 20218105);
                Require(m.Data.TotalBattlePassExp >= BattlePassThreshold(levels, row.Level), 20218104);
                claimable = [row];
            }

            foreach (var row in claimable)
            {
                List<AscNet.Table.V2.share.reward.RewardGoodsTable> goods = RewardHandler.GetRewardGoods(row.RewardId);
                Require(goods.Count > 0, 20218046);
                m.Grant(new RewardGrant(m.NextClaimKey(), goods));
                foreach (var good in goods)
                {
                    var type = RewardHandler.GetRewardType(good);
                    Require(type is not null, 20218045);
                    response.RewardGoodsList.Add(new RewardGoods
                    {
                        Id = good.Id, TemplateId = good.TemplateId, Count = good.Count, RewardType = (int)type!.Value
                    });
                }
                m.Data.BattlePassGotRewardIds.Add(row.Level);
                response.GotRewardIds.Add(row.Level);
            }
        });

    //endregion

    //region tech

    [RequestPacketHandler("Theatre4TechUnlockRequest")]
    public static void Theatre4TechUnlock(Session session, Packet.Request packet) =>
        Handle<Theatre4TechUnlockRequest, Theatre4TechUnlockResponse>(session, packet, (m, request, response) =>
        {
            EnsureAvailable(session);
            var tech = Rows<Theatre4TechTable>().FirstOrDefault(row => row.Id == request.TechId);
            Require(tech is not null, 20218099);
            Require(!m.Data.Techs.Contains(request.TechId), 20218100);
            // EN XTheatre4Agency:1202 requires every prerequisite to be unlocked and active;
            // Techs membership is exactly that, because a row can only be added after its owns gate.
            Require(tech!.PreIds.Where(id => id > 0).All(m.Data.Techs.Contains), 20218101);
            Require(tech.Condition is not > 0 || IsConditionMet(m, tech.Condition.Value), 20218102);
            m.Cost(TechTreeCoinItemId, tech.Cost, 20012004);
            m.Data.Techs.Add(request.TechId);
        });

    //endregion

    //region enter

    [RequestPacketHandler("Theatre4EnterRequest")]
    public static void Theatre4Enter(Session session, Packet.Request packet)
    {
        long? abandonedFightId = null;
        int abandonedStageId = 0;
        if (session.player.Theatre4.ActiveEncounter is { PreFightPayload: not null } existing
            && session.fight is { } active
            && active.FightId == unchecked((uint)existing.FightUuid)
            && active.PreFight.PreFightData.StageId == existing.StageId)
        {
            abandonedFightId = existing.FightUuid;
            abandonedStageId = existing.StageId;
        }
        Handle<Theatre4EnterRequest, Theatre4EnterResponse>(session, packet, (m, _, _) =>
        {
            EnsureAvailable(session);
            // Local policy: explicit Enter abandons only an authorized unresolved battle. Network
            // login is intentionally separate and preserves its frozen bytes for native reconnect.
            RequireAdventure(m);
            if (m.State.ActiveEncounter is { SettleReceipt: null, PreFightPayload: not null })
            {
                AbandonEncounter(m);
                // EndAdventure clears the active run on the server, but its settlement push carries
                // the terminal clone; refresh the activity model before Enter's success callback
                // advances the native UI to the difficulty step.
                if (m.Data.AdventureData is null)
                    m.Push(new NotifyTheatre4ActivityData { Data = Clone(m.Data) });
            }
        }, static (response, code) => response.Code = code,
            afterCommit: committed =>
            {
                if (abandonedFightId is long fightId
                    && committed.fight is { } active
                    && active.FightId == unchecked((uint)fightId)
                    && active.PreFight.PreFightData.StageId == abandonedStageId)
                    committed.fight = null;
            });
    }

    //endregion

    //region tasks

    private static readonly Lazy<Dictionary<int, TaskTable>> MetaTasks = new(() =>
    {
        HashSet<int> ids = Rows<Theatre4TaskTable>().SelectMany(row => row.TaskId ?? [])
            .Where(id => id > 0).ToHashSet();
        return Rows<TaskTable>().Where(row => ids.Contains(row.Id)).ToDictionary(row => row.Id);
    });

    private static readonly Lazy<Dictionary<int, TaskConditionTable>> MetaTaskConditions = new(() =>
    {
        HashSet<int> ids = MetaTasks.Value.Values.Select(row => row.Condition).ToHashSet();
        return Rows<TaskConditionTable>().Where(row => ids.Contains(row.Id)).ToDictionary(row => row.Id);
    });

    internal static bool IsMetaTask(int id) => MetaTasks.Value.ContainsKey(id);

    private static bool IsMetaTaskOpen(Mutation m, DateTimeOffset now) =>
        // No Theatre4 TaskTimeLimit rows are authored; the activity window is the only gate.
        m.Data.ActivityId > 0 && Rows<Theatre4ActivityTable>().Any(row => row.Id == m.Data.ActivityId
            && row.TimeId > 0 && ActivityScheduleService.IsOpen(row.TimeId, now));

    // EN share/task/Condition rows 240429-240441. Types 111003/111004/111005/111008/111009
    // have no Lua evaluator; the permanent fields and the shared counter ledger are the
    // server-side sources, and unknown types fail loudly rather than reporting success.
    internal static int EvaluateMetaCondition(Mutation m, TaskConditionTable condition)
    {
        List<int> p = condition.Params;
        switch (condition.Type)
        {
            // 111003 [endingId]: ending first reached, recorded in the permanent Endings map.
            case 111003 when p.Count == 1 && p[0] > 0:
                return m.Data.Endings.GetValueOrDefault(p[0]) > 0 ? 1 : 0;
            // 111005 [count]: blueprints collected, recorded in the permanent item handbook.
            case 111005 when p.Count == 1 && p[0] > 0:
                return m.Data.ItemsAtlas.Distinct().Count();
            // 111004 [count, scalar] reinforcement selections, 111008 build/alter, 111009 sweeps:
            // once-only counters produced inside the run and accumulated by the shared ledger.
            case 111004 or 111008 or 111009:
                return m.GetTaskConditionProgress(condition.Id);
            default:
                throw new InvalidDataException($"Unsupported Theatre4 task condition {condition.Id}/{condition.Type}.");
        }
    }

    private static int MetaTaskValue(Mutation mutation, TaskTable task) =>
        Math.Min(Math.Max(0, EvaluateMetaCondition(mutation, MetaTaskConditions.Value[task.Condition])),
            Math.Max(1, task.Result ?? 1));

    private static int MetaTaskState(Mutation mutation, TaskTable task, int value) =>
        mutation.IsTaskClaimed(task.Id) ? 4 : value >= Math.Max(1, task.Result ?? 1) ? 3 : 1;

    internal static List<LoginTask> BuildTasks(Session session) =>
        BuildTaskUpdates(new Mutation(session, newOperation: false)).Select(task => new LoginTask
        {
            Id = task.Id, State = task.State, RecordTime = task.RecordTime,
            Schedule = task.Schedule.Select(value => new LoginTask.NotifyTaskDataTaskDataTaskSchedule
            {
                Id = value.Id, Value = value.Value
            }).ToList()
        }).ToList();

    internal static List<SyncTask> BuildTaskUpdates(Mutation mutation)
    {
        uint now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return MetaTasks.Value.Values.Select(task => new SyncTask
        {
            Id = (uint)task.Id,
            State = MetaTaskState(mutation, task, MetaTaskValue(mutation, task)),
            RecordTime = now,
            Schedule = [new() { Id = (uint)task.Condition, Value = MetaTaskValue(mutation, task) }]
        }).ToList();
    }

    internal static bool CanClaimMetaTask(Mutation mutation, int id) =>
        MetaTasks.Value.TryGetValue(id, out var task) && !mutation.IsTaskClaimed(id)
        && IsMetaTaskOpen(mutation, DateTimeOffset.UtcNow)
        && MetaTaskValue(mutation, task) >= Math.Max(1, task.Result ?? 1);

    internal static List<RewardGoods> ClaimMetaTask(Mutation mutation, int id)
    {
        Require(MetaTasks.Value.TryGetValue(id, out var task), 20026005);
        Require(!mutation.IsTaskClaimed(id), 20026006);
        Require(CanClaimMetaTask(mutation, id), 20026007);
        List<RewardGoods> result = [];
        if (task!.RewardId is > 0)
        {
            var goods = RewardHandler.GetRewardGoods(task.RewardId.Value);
            Require(goods.Count > 0, 20026003);
            foreach (var good in goods)
            {
                var type = RewardHandler.GetRewardType(good);
                Require(type is not null, 20026003);
                result.Add(new RewardGoods
                {
                    Id = good.Id, TemplateId = good.TemplateId, Count = good.Count, RewardType = (int)type!.Value
                });
            }
            mutation.Grant(new RewardGrant(
                $"theatre4-task:{mutation.Session.player.PlayerData.Id}:{id}", goods));
        }
        mutation.MarkTaskClaimed(id);
        mutation.Push(new NotifyTask { Tasks = new() { Tasks = BuildTaskUpdates(mutation) } });
        return result;
    }

    // Producers report the authored challenge counters here. Condition types and targets are
    // share/task/Condition rows 240434-240436 (111004), 240440 (111009) and 240441 (111008).
    internal static void RecordMetaProgress(Mutation mutation, string trigger, int value = 1)
    {
        Require(value >= 0, 1);
        int conditionType = trigger switch
        {
            "ReinforcementSelected" => 111004,
            "Sweep" => 111009,
            "BuildAlter" => 111008,
            _ => throw new InvalidDataException($"Unsupported Theatre4 meta trigger {trigger}.")
        };
        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool changed = false;
        foreach (var condition in MetaTaskConditions.Value.Values.Where(row => row.Type == conditionType))
        {
            if (!MetaTasks.Value.Values.Any(task => task.Condition == condition.Id && IsMetaTaskOpen(mutation, now)))
                continue;
            mutation.AddTaskConditionProgress(condition.Id, value);
            changed = true;
        }
        if (changed) mutation.Push(new NotifyTask { Tasks = new() { Tasks = BuildTaskUpdates(mutation) } });
    }

    //endregion
}
