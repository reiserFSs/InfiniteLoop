using AscNet.Common;
using AscNet.Common.MsgPack;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.theatre4;

namespace AscNet.GameServer.Handlers;

// Awakening Tundra (Theatre4) adventure lifecycle: difficulty/inherit/affix startup, the daily
// settlement, endings and final reward settlement, plus sand-table timeback snapshots.
//
// Client authority: XTheatre4SetControl step machine, XTheatre4Agency settlement flow,
// XTheatre4Control request payloads, XEnumConst.Theatre4. Authored tables drive every value that
// exists; where no retail authority ships (block layout, per-day algorithms), the rule is marked
// "AscNet Theatre4 local policy" and is deterministic from the persisted per-run RNG.
internal static partial class Theatre4Module
{
    //region startup

    // AscNet Theatre4 local policy: initial Silver Coins fall with difficulty; difficulties 6-7
    // are authored as "Begin with no Initial Resources".
    private static int StartingGold(int difficulty) => difficulty >= 6 ? 0 : difficulty >= 4 ? 50 : 100;
    private static void AddUnlockedTechEffects(Mutation m)
    {
        List<Theatre4TechTable> techRows = Rows<Theatre4TechTable>();
        foreach (int techId in m.Data.Techs)
        {
            Theatre4TechTable? tech = techRows.FirstOrDefault(row => row.Id == techId)
                ?? throw new InvalidDataException($"Theatre4 tech {techId} is missing.");
            if (tech.EffectGroupId > 0) AddEffectGroup(m, tech.EffectGroupId);
        }
    }


    internal static Theatre4AdventureData InitializeAdventure(Mutation m, int difficultyId)
    {
        Theatre4DifficultyTable row = Rows<Theatre4DifficultyTable>().FirstOrDefault(candidate => candidate.Id == difficultyId)
            ?? throw new ServerCodeException("Theatre4 difficulty is not authored.", 20218033);
        Require(row.ConditionId is not > 0 || IsConditionMet(m, row.ConditionId.Value), 20218033);
        // RunId/RNG/receipts/timeback are run-scoped; Core reseeds and clears them here.
        StartRun(m);
        Theatre4AdventureData adventure = new()
        {
            Difficulty = difficultyId,
            MapBlueprintId = BlueprintIdForDifficulty(difficultyId),
            Hp = row.Hp,
            MaxAp = row.ActionPoint,
            Ap = row.ActionPoint,
            Bp = row.Energy ?? 0,
            Gold = StartingGold(difficultyId),
            ItemLimit = ConfigInt("ItemCountLimit"),
            StartTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        InitializeColors(adventure);
        m.State.ActiveEncounter = null;
        m.Data.AdventureData = adventure;
        InitializeEconomy(m);
        // Difficulty and already-unlocked tech effects are owned by the new run. Preserve the
        // persisted tech order and multiplicity; prerequisites were enforced at unlock time.
        AddEffects(m, row.EffectGroup.Where(id => id > 0));
        AddUnlockedTechEffects(m);
        TriggerEffects(m, "start");
        // Clear the original client's stale FinishFight cache before publishing the new run.
        if (m.Data.PreAdventureSettleData is { } previousSettle)
            m.Push(new NotifyTheatre4AdventureSettle
            {
                SettleData = Clone(previousSettle),
                AdventureData = null
            });
        m.Push(new NotifyTheatre4AdventureData { AdventureData = Clone(adventure) });
        return adventure;
    }

    internal static void InitializeColors(Theatre4AdventureData adventure)
    {
        adventure.Colors.Clear();
        for (int color = T4Color.Red; color <= T4Color.Blue; color++)
            adventure.Colors.Add(new Theatre4ColorTalentData
            {
                Color = color,
                Level = 1,
                Resource = 0,
                Point = 0,
                DailyResource = 0,
                PointCanCost = 0
            });
    }

    internal static Theatre4ColorTalentData RequireColor(Mutation m, int color)
    {
        Theatre4AdventureData adventure = RequireAdventure(m);
        return adventure.Colors.FirstOrDefault(candidate => candidate.Color == color)
            ?? throw new ServerCodeException("Theatre4 colour is not initialised.", 20218030);
    }


    //endregion

    //region selection handlers

    [RequestPacketHandler("Theatre4SelectDifficultRequest")]
    public static void Theatre4SelectDifficult(Session session, Packet.Request packet) =>
        Handle<Theatre4SelectDifficultRequest, Theatre4SelectDifficultResponse>(session, packet, (m, request, response) =>
        {
            EnsureAvailable(session);
            Require(m.Data.AdventureData is null, 20218036);
            InitializeAdventure(m, request.Difficult);
            response.AdventureData = Clone(m.Data.AdventureData!);
        });

    [RequestPacketHandler("Theatre4SelectInheritRequest")]
    public static void Theatre4SelectInherit(Session session, Packet.Request packet) =>
        Handle<Theatre4SelectInheritRequest, Theatre4SelectInheritResponse>(session, packet, (m, request, response) =>
        {
            EnsureAvailable(session);
            Theatre4AdventureData adventure = RequireAdventure(m);
            Require(request.ItemId > 0, 20218034);
            Require(adventure.InheritItemId == 0, 20218034);
            int allowed = Math.Max(1, ConfigInt("InheritItemNum"));
            List<int> offered = m.Data.PreAdventureSettleData?.InheritItems ?? [];
            Require(adventure.Items.Count(item => offered.Contains(item.ItemId)) < allowed, 20218034);
            Require(offered.Contains(request.ItemId), 20218034);
            AddAsset(m, T4Asset.Item, request.ItemId, 1);
            adventure.InheritItemId = request.ItemId;
            RebuildEffects(m);
            response.AdventureData = Clone(adventure);
        });

    [RequestPacketHandler("Theatre4SelectAffixRequest")]
    public static void Theatre4SelectAffix(Session session, Packet.Request packet) =>
        Handle<Theatre4SelectAffixRequest, Theatre4SelectAffixResponse>(session, packet, (m, request, response) =>
        {
            EnsureAvailable(session);
            Theatre4AdventureData adventure = RequireAdventure(m);
            Require(adventure.Affix == 0, 1);
            List<int> offered = m.Data.PreAdventureSettleData?.InheritItems ?? [];
            Require(offered.Count == 0 || adventure.InheritItemId > 0, 20218034);
            Theatre4AffixTable affix = Rows<Theatre4AffixTable>().FirstOrDefault(candidate => candidate.Id == request.Affix)
                ?? throw new ServerCodeException("Theatre4 affix is not authored.", 20218035);
            Require(affix.ConditionId is not > 0 || IsConditionMet(m, affix.ConditionId.Value), 20218035);
            adventure.Affix = affix.Id;
            if (affix.EffectGroupId > 0) AddEffectGroup(m, affix.EffectGroupId);
            foreach (int ticket in affix.RecruitTicketId.Where(id => id > 0))
                if (!adventure.RecruitTickets.Contains(ticket)) adventure.RecruitTickets.Add(ticket);

            int firstStep = MapGroupForStep(m, adventure.MapBlueprintId, 0);
            Require(firstStep > 0, 20218017);
            InitializeMap(m, firstStep);
            BeginAdventureRecruit(m);
            RebuildEffects(m);
            response.AdventureData = Clone(adventure);
        });

    //endregion

    //region daily settlement

    [RequestPacketHandler("Theatre4DailySettleRequest")]
    public static void Theatre4DailySettle(Session session, Packet.Request packet) =>
        Handle<Theatre4DailySettleRequest, Theatre4DailySettleResponse>(session, packet, (m, request, response) =>
        {
            EnsureAvailable(session);
            Theatre4AdventureData adventure = RequireAdventure(m);
            int day = adventure.Days;

            // Full-day snapshot for sand-table timeback plus the wire summary the backtrack UI reads.
            Theatre4AdventureData frozen = Clone(adventure);
            m.State.TracebackSnapshots[day] = frozen;
            adventure.Days = checked(day + 1);

            (int interest, int limit) = Interest(m);
            if (interest > 0) AddAsset(m, T4Asset.Gold, 0, interest);
            // Effects own the per-effect daily grants (BuildPoint, gold, colour daily, 14 decay...).
            TriggerEffects(m, "daily");
            Theatre4SettlementBonuses bonuses = ComputeSettlementBonuses(m);
            (Dictionary<int, int> levels, Dictionary<int, int> resources) =
                ApplyDailySettlementBonuses(m, bonuses);
            ResetDailySettlementAccumulators(m);
            adventure.DailyExploreColors.Clear();
            adventure.DailyExplorePosSet.Clear();
            adventure.Ap = adventure.MaxAp + adventure.ExtraMaxAp + DailyActionPointBonus(m);
            TickItemDurations(m, adventure);

            adventure.TracebackDatas[day] = BuildTracebackData(frozen, adventure, day);
            response.SettleResult = BuildDailySettleResult(adventure, interest, limit, levels, resources, bonuses.ColorExtra);

            // Boss punish may end the run: EndAdventure already pushes the settle payload, so no
            // live adventure snapshot must follow it.
            if (TickChapters(m, adventure) || adventure.Hp <= 0)
            {
                if (m.Data.AdventureData is not null) EndAdventure(m, settleType: 1, won: false);
                return;
            }
            TriggerFate(m, adventure);
            // Authored hidden gates may become satisfiable from day/state changes.
            foreach (Theatre4ChapterData chapter in adventure.Chapters) RevealHiddenRegions(m, chapter);
            PushResource(m);
            m.Push(new NotifyTheatre4AdventureData { AdventureData = Clone(adventure) });
            m.Push(new NotifyTheatre4TracebackInfo { TracebackDatas = adventure.TracebackDatas.ToDictionary(
                pair => pair.Key, pair => Clone(pair.Value)) });
        });

    private static (Dictionary<int, int> Levels, Dictionary<int, int> Resources) ApplyDailySettlementBonuses(
        Mutation m, Theatre4SettlementBonuses bonuses)
    {
        Theatre4AdventureData adventure = RequireAdventure(m);
        Dictionary<int, int> levels = [];
        Dictionary<int, int> resources = [];
        int prosperity = 0;
        foreach (Theatre4ColorTalentData color in adventure.Colors)
        {
            int level = checked(color.Level + bonuses.ColorLevel.GetValueOrDefault(color.Color));
            int resource = checked(color.Resource + color.DailyResource);
            resource = checked(resource + bonuses.ColorResource.GetValueOrDefault(color.Color));
            double product = level * (double)resource * bonuses.ColorExtra;
            if (!double.IsFinite(product) || product < 0)
                throw new InvalidDataException("Theatre4 daily settlement operands are invalid.");
            int amount = NativeSettlementRound(product);
            levels[color.Color] = level;
            resources[color.Color] = resource;
            if (amount == 0) continue;
            AddAsset(m, T4Asset.ColorPoint, color.Color, amount);
            if (color.Color == T4Color.Red) AddAsset(m, T4Asset.ColorCostPoint, color.Color, amount);
            prosperity = checked(prosperity + amount);
        }

        foreach ((int color, int amount) in bonuses.PermanentColorResource)
            AddAsset(m, T4Asset.ColorResource, color, amount);
        foreach (Theatre4AssetData asset in bonuses.Assets)
            AddAsset(m, asset.Type, asset.Id, asset.Num);
        if (prosperity > 0) AddAsset(m, T4Asset.Prosperity, 0, prosperity);
        ConsumePermanentSettlementBonuses(m, bonuses);
        return (levels, resources);
    }

    private static int NativeSettlementRound(double value) =>
        checked((int)Math.Floor(value + 0.5d));

    private static Theatre4DailySettleResultData BuildDailySettleResult(Theatre4AdventureData adventure,
        int interest, int limit, Dictionary<int, int> levels, Dictionary<int, int> resources, double extra) => new()
    {
        Interest = interest,
        InterestLimit = limit,
        BuildPoint = adventure.Bp,
        ColorLevel = levels,
        ColorResource = resources,
        ColorExtra = extra
    };

    // Wire summary the backtrack bubble reads. CostAp/CostGold/CostBp are the day's net spend
    // measured against the frozen start-of-day snapshot (AscNet Theatre4 local policy); CostAp is
    // what ApplyTracebackRollback refunds.
    private static Theatre4TracebackData BuildTracebackData(Theatre4AdventureData frozen,
        Theatre4AdventureData current, int day) => new()
    {
        Days = day,
        CostAp = Math.Max(0, frozen.Ap - current.Ap),
        CostGold = Math.Max(0, frozen.Gold - current.Gold),
        CostBp = Math.Max(0, frozen.Bp - current.Bp),
        Colors = current.Colors.Select(color => new Theatre4TracebackColorData
        {
            Color = color.Color, Level = color.Level, Resource = color.Resource
        }).ToList()
    };

    private static void TickItemDurations(Mutation m, Theatre4AdventureData adventure)
    {
        List<Theatre4ItemData> expired = [];
        foreach (Theatre4ItemData item in adventure.Items.Concat(adventure.Props))
        {
            if (item.LeftDays < 0) continue;
            item.LeftDays -= 1;
            if (item.LeftDays <= 0) expired.Add(item);
        }
        foreach (Theatre4ItemData item in expired)
        {
            adventure.Items.RemoveAll(candidate => candidate.Uid == item.Uid);
            adventure.Props.RemoveAll(candidate => candidate.Uid == item.Uid);
            RemoveEffectsOfItem(m, item.Uid);
            m.Push(new NotifyTheatre4RemoveItem { Item = Clone(item) });
            TriggerEffects(m, "itemremoved", null, item.ItemId);
        }
    }

    // Boss countdown and punish strikes. Returns true when the run was ended.
    private static bool TickChapters(Mutation m, Theatre4AdventureData adventure)
    {
        foreach (Theatre4ChapterData chapter in adventure.Chapters.Where(chapter => !chapter.IsPass))
            foreach (Theatre4GridData grid in chapter.Grids.Where(grid => grid.Type == T4Grid.Boss
                && grid.State != T4State.Processed && grid.Fight is not null))
            {
                if (grid.Fight!.PunishCountdown < 0) continue;
                if (grid.Fight.PunishCountdown > 0)
                {
                    grid.Fight.PunishCountdown -= 1;
                    continue;
                }
                // Countdown already 0: the authored punish term lands each further day.
                int punish = PunishHp(m, grid.ContentId);
                adventure.Hp = Math.Max(0, adventure.Hp - Math.Max(1, punish));
                if (adventure.Hp <= 0)
                {
                    EndAdventure(m, settleType: 1, won: false);
                    return true;
                }
            }
        return false;
    }

    //endregion

    //region fate timeline

    // Spawns the day's authored fate event and ages the pending ones. Returns true if ended.
    private static bool TriggerFate(Mutation m, Theatre4AdventureData adventure)
    {
        Theatre4FateTable? fateRow = Rows<Theatre4FateTable>()
            .FirstOrDefault(row => row.Difficulty == adventure.Difficulty);
        if (fateRow is null) return false;
        bool spawned = false;
        for (int i = 0; i < fateRow.TriggerDay.Count; i++)
        {
            if (fateRow.TriggerDay[i] != adventure.Days) continue;
            int group = i < fateRow.EventGroup.Count ? fateRow.EventGroup[i] : 0;
            if (group <= 0) continue;
            List<Theatre4FateEventTable> rows = Rows<Theatre4FateEventTable>()
                .Where(row => row.GroupId == group
                    && !row.Condition.Where(id => id > 0).Any(id => !IsConditionMet(m, id)))
                .ToList();
            Theatre4FateEventTable? chosen = PickWeighted(m, rows, row => Math.Max(0, row.Weight ?? 0)
                + row.ConditionWeight.Select((weight, index) => row.Condition.Count > index
                    && row.Condition[index] is > 0 && IsConditionMet(m, row.Condition[index]) ? weight : 0).Sum());
            if (chosen is null || BuildMapEvent(m, chosen.EventId) is not { } evt) continue;
            adventure.Fate = new Theatre4FateData
            {
                Id = fateRow.Id,
                FateEvents =
                [
                    new Theatre4FateEventData
                    {
                        TableRowId = chosen.Id,
                        UniqueId = NextId(m),
                        Event = evt,
                        EventTimeLeft = Math.Max(1, chosen.Duration)
                    }
                ]
            };
            spawned = true;
            m.Push(new NotifyTheatre4FateData { FateData = Clone(adventure.Fate) });
            break;
        }
        if (spawned || adventure.Fate is null) return false;
        foreach (Theatre4FateEventData fateEvent in adventure.Fate.FateEvents)
            if (fateEvent.EventTimeLeft > 0) fateEvent.EventTimeLeft -= 1;
        // No authored terminal condition: an unresolved timeline event simply keeps its place.
        return false;
    }

    //endregion

    //region settlement

    [RequestPacketHandler("Theatre4SettleAdventureRequest")]
    public static void Theatre4SettleAdventure(Session session, Packet.Request packet) =>
        Handle<Theatre4SettleAdventureRequest, Theatre4SettleAdventureResponse>(session, packet, (m, request, response) =>
        {
            EnsureAvailable(session);
            RequireAdventure(m);
            EndAdventure(m, settleType: 3, won: false);
        });

    internal static void EndAdventure(Mutation m, int settleType, bool won)
    {
        if (m.Data.AdventureData is not { } adventure) return;
        Theatre4DifficultyTable? difficulty = Rows<Theatre4DifficultyTable>()
            .FirstOrDefault(row => row.Id == adventure.Difficulty);


        int bpExp = won && difficulty is not null
            ? (int)Math.Round(difficulty.BPExpMax * difficulty.BPExpRate) : 0;
        adventure.SettleBpExp = bpExp;
        if (bpExp > 0) AddBattlePassExp(m, bpExp);
        if (difficulty is not null)
            foreach (int drop in difficulty.RewardDrop.Where(id => id > 0)) GrantModeDrop(m, drop);

        int ending = ResolveEnding(m, won);
        int inheritNum = Math.Max(0, ConfigInt("InheritItemNum"));
        Theatre4AdventureSettleData settle = new()
        {
            SettleType = settleType,
            EndingId = ending,
            GridPassCount = adventure.Chapters.Count(chapter => chapter.IsPass),
            InheritItems = adventure.Items.Where(item => m.Data.ItemsAtlas.Contains(item.ItemId))
                .Select(item => item.ItemId).Distinct().Take(inheritNum).ToList(),
            Prosperity = adventure.Prosperity,
            StarBpExp = bpExp,
            RewardGoods = [],
            SettleTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        m.Data.PreAdventureSettleData = settle;
        if (won)
        {
            m.Data.Endings[ending] = m.Data.Endings.GetValueOrDefault(ending) + 1;
            m.Data.Difficultys[adventure.Difficulty] =
                m.Data.Difficultys.GetValueOrDefault(adventure.Difficulty) + 1;
        }
        m.Data.AdventureData = null;
        m.State.ActiveEncounter = null;
        m.State.TracebackSnapshots.Clear();
        // The ending presentation and relog recovery need the terminal run snapshot; the client
        // reads it as XTheatre4Adventure:NotifyAdventureData, the same reader as the live push.
        m.State.PendingSettleAdventure = Clone(adventure);
        m.Push(new NotifyTheatre4AdventureSettle { SettleData = settle, AdventureData = Clone(adventure) });
        m.Push(new NotifyTheatre4FinishEndings { Endings = new(m.Data.Endings) });
        m.Push(new NotifyTheatre4FinishDifficultys { Difficultys = new(m.Data.Difficultys) });
    }

    // Ending.PassType 1 = failure, 2 = success; Priority orders the pick; Condition gates it.
    // The authored table guarantees an ungated row per pass type (success 3, failure 10), so a
    // missing candidate is a data error rather than a reason to invent an ending.
    internal static int ResolveEnding(Mutation m, bool won)
    {
        List<Theatre4EndingTable> candidates = Rows<Theatre4EndingTable>()
            .Where(row => row.PassType == (won ? 2 : 1)
                && (row.Condition is not > 0 || IsConditionMet(m, row.Condition.Value)))
            .OrderBy(row => row.Priority)
            .ToList();
        if (candidates.Count == 0)
            throw new InvalidDataException($"Theatre4 has no eligible {(won ? "success" : "failure")} ending.");
        return candidates[0].Id;
    }

    //endregion

    //region timeback

    // Called from XTheatre4UseSkillEffect (effect type 423) after the skill cost was charged.
    // Restores the full frozen AdventureData for the target day and refunds the Action Points
    // banked in the summary rows of every rewound day. Snapshots are run-scoped and cleared at
    // settlement, so no economic reward can be duplicated by rewinding into a settled run.
    internal static void ApplyTracebackRollback(Mutation m, int effectId, List<int> parameters)
    {
        Theatre4AdventureData adventure = RequireAdventure(m);
        // Client target day is Days - MaxTracebackDays, so day 0 is valid once three days elapsed.
        Require(parameters.Count >= 1 && parameters[0] >= 0, 20218140);
        int targetDay = parameters[0];
        Require(m.State.TracebackSnapshots.ContainsKey(targetDay), 20218140);
        Require(adventure.TracebackPoint > 0, 20218141);
        adventure.TracebackPoint -= 1;

        int refundAp = adventure.TracebackDatas.Where(pair => pair.Key > targetDay).Sum(pair => pair.Value.CostAp);
        Theatre4AdventureData restored = Clone(m.State.TracebackSnapshots[targetDay]);
        restored.TracebackPoint = adventure.TracebackPoint;

        foreach (int key in adventure.TracebackDatas.Keys.Where(key => key > targetDay).ToList())
        {
            adventure.TracebackDatas.Remove(key);
            m.State.TracebackSnapshots.Remove(key);
        }
        m.Data.AdventureData = restored;
        if (refundAp > 0) AddAsset(m, T4Asset.ActionPoint, 0, refundAp);

        m.Push(new NotifyTheatre4AdventureData { AdventureData = Clone(restored) });
        m.Push(new NotifyTheatre4TracebackInfo { TracebackDatas = restored.TracebackDatas.ToDictionary(
            pair => pair.Key, pair => Clone(pair.Value)) });
        foreach (Theatre4ChapterData chapter in restored.Chapters)
            m.Push(new NotifyTheatre4ChangeGrids { MapId = chapter.MapId, Grids = chapter.Grids.Select(Clone).ToList() });
    }

    //endregion
}
