using AscNet.Common;
using AscNet.Common.MsgPack;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.theatre4;

namespace AscNet.GameServer.Handlers;

// Awakening Tundra (Theatre4) event resolution: grid events (dialogue / options / reward / fight)
// and the fate timeline events.
//
// Client authority: XTheatre4Event + XTheatre4EventOption (Option is the EventOption row Id,
// OptionType/NextEvent/NextEventGroupId/EffectGroupId), XTheatre4Control.DoGridEventRequest /
// DoFateEventRequest, XTheatre4Agency fate notify. Authored tables drive outcomes; the small
// amount of missing routing (which option ends a chain) follows the authored IsEnd/NextEvent data.
internal static partial class Theatre4Module
{
    //region grid events

    [RequestPacketHandler("Theatre4DoGridEventRequest")]
    public static void Theatre4DoGridEvent(Session session, Packet.Request packet) =>
        Handle<Theatre4DoGridEventRequest, Theatre4DoGridEventResponse>(session, packet, (m, request, response) =>
        {
            EnsureAvailable(session);
            response.Grids = ResolveGridEvent(m, request);
        });

    private static List<Theatre4GridData> ResolveGridEvent(Mutation m, Theatre4DoGridEventRequest request)
    {
        Theatre4AdventureData adventure = RequireAdventure(m);
        Theatre4ChapterData chapter = RequireChapter(m, request.MapId);
        Theatre4GridData grid = FindGrid(m, request.MapId, request.PosX, request.PosY);
        Require(grid.Type == T4Grid.Event && grid.Event is not null, 20218030);
        // Only an opened (Explored) event may be resolved; a processed one is done.
        Require(grid.State == T4State.Explored, 20218030);

        Theatre4EventTable row = Rows<Theatre4EventTable>()
            .FirstOrDefault(candidate => candidate.Id == grid.Event!.EventId)
            ?? throw new ServerCodeException("Theatre4 grid event is not authored.", 20218030);
        bool recorded = false;

        if (row.OptionGroupId is > 0)
        {
            Require(request.Option > 0, 20218030);
            Theatre4EventOptionTable option = Rows<Theatre4EventOptionTable>()
                .FirstOrDefault(candidate => candidate.GroupId == row.OptionGroupId && candidate.Id == request.Option)
                ?? throw new ServerCodeException("Theatre4 event option is not authored.", 20218030);
            Require(option.OptionShowCondition is not > 0 || IsConditionMet(m, option.OptionShowCondition.Value), 20218030);
            Require(option.OptionCondition is not > 0 || IsConditionMet(m, option.OptionCondition.Value), 20218030);
            ApplyOption(m, option);
            adventure.OptionIds.Add(option.Id);
            AdvanceOrComplete(m, adventure, grid, row,
                ResolveNextEventId(m, option.NextEvent, option.NextEventGroupId));
            recorded = true;
        }
        else if (row.Type == 4 && grid.Event!.StageId > 0)
        {
            // A fight event with no options is entered through Combat, never resolved by this RPC.
            Require(request.Option == 0, 20218030);
        }
        else
        {
            // Dialogue / Reward rows carry no options: Option must be 0 and the authored
            // NextEvent/NextEventGroup chain is followed before the row completes.
            Require(request.Option == 0, 20218030);
            AdvanceOrComplete(m, adventure, grid, row,
                ResolveNextEventId(m, row.NextEvent, row.NextEventGroup));
            recorded = true;
        }

        RebuildEffects(m);
        // Event resolution can be the completion that satisfies an absent-row hidden gate.
        RevealHiddenRegions(m, chapter);
        m.Push(new NotifyTheatre4ChangeGrids { MapId = request.MapId, Grids = [Clone(grid)] });
        m.Push(new NotifyTheatre4AdventureData { AdventureData = Clone(adventure) });
        if (recorded) PushFinishEventRecord(m, adventure);
        return [grid];
    }

    // Moves the grid to the authored next event, or completes the chain when there is none
    // (or the next row is unauthored/unplayable).
    private static void AdvanceOrComplete(Mutation m, Theatre4AdventureData adventure, Theatre4GridData grid,
        Theatre4EventTable row, int nextEventId)
    {
        // Every accepted event step owns its authored effects/rewards, including steps that
        // continue a chain. The client sends one request per displayed event row.
        RecordEvent(m, adventure, row.Id);
        ApplyEventOutcome(m, row);
        Theatre4EventData? next = nextEventId > 0 ? BuildMapEvent(m, nextEventId) : null;
        if (next is null)
        {
            CompleteGridEvent(grid);
            return;
        }
        grid.Event = next;
    }

    private static void ApplyEventOutcome(Mutation m, Theatre4EventTable row)
    {
        if (row.EffectGroupId is > 0) AddEffectGroup(m, row.EffectGroupId.Value);
        GrantRewards(m, row.RewardId);
    }

    private static void RecordEvent(Mutation m, Theatre4AdventureData adventure, int eventId)
    {
        if (eventId <= 0) return;
        adventure.FinishEventIds[eventId] = adventure.FinishEventIds.GetValueOrDefault(eventId) + 1;
        m.Data.GlobalFinishEventIds[eventId] = m.Data.GlobalFinishEventIds.GetValueOrDefault(eventId) + 1;
    }

    private static void PushFinishEventRecord(Mutation m, Theatre4AdventureData adventure) =>
        m.Push(new NotifyTheatre4FinishEventRecord
        {
            FinishEventIds = new(adventure.FinishEventIds),
            GlobalFinishEventIds = new(m.Data.GlobalFinishEventIds)
        });

    private static void CompleteGridEvent(Theatre4GridData grid)
    {
        if (grid.Event is null) return;
        grid.State = T4State.Processed;
        // Clearing Event makes the client's post-request grid EventId 0, which closes the chain UI.
        grid.Event = null;
    }

    // Called by T4Combat through CompleteEncounter when an event fight is won/lost.
    private static bool CompleteEventFight(Mutation m, Theatre4AdventureData adventure, Theatre4GridData grid, bool won)
    {
        if (!won)
        {
            adventure.Hp = Math.Max(0, adventure.Hp - 1);
            if (adventure.Hp <= 0) EndAdventure(m, settleType: 1, won: false);
            return false;
        }
        if (grid.Event is not { } current) return false;
        Theatre4EventTable? row = Rows<Theatre4EventTable>()
            .FirstOrDefault(candidate => candidate.Id == current.EventId);
        if (row is null) return false;
        AdvanceOrComplete(m, adventure, grid, row,
            ResolveNextEventId(m, row.NextEvent, row.NextEventGroup));
        return true;
    }


    //endregion

    //region fate events

    [RequestPacketHandler("Theatre4DoFateEventRequest")]
    public static void Theatre4DoFateEvent(Session session, Packet.Request packet) =>
        Handle<Theatre4DoFateEventRequest, Theatre4DoFateEventResponse>(session, packet, (m, request, response) =>
        {
            EnsureAvailable(session);
            Theatre4AdventureData adventure = RequireAdventure(m);
            Require(adventure.Fate is not null, 20218031);
            Theatre4FateEventData fateEvent = adventure.Fate!.FateEvents
                .FirstOrDefault(candidate => candidate.UniqueId == request.FateEventUniqueId)
                ?? throw new ServerCodeException("Theatre4 fate event is not active.", 20218031);
            Theatre4EventData? evt = fateEvent.Event;
            Require(evt is not null, 20218031);
            Theatre4EventTable row = Rows<Theatre4EventTable>()
                .FirstOrDefault(candidate => candidate.Id == evt!.EventId)
                ?? throw new ServerCodeException("Theatre4 fate event is not authored.", 20218031);
            bool recorded = false;

            // Type 4 fate events are fought through Combat; the option request only previews them.
            if (row.Type == 4)
            {
                Require(request.Option == 0, 20218031);
                return;
            }

            if (row.OptionGroupId is > 0)
            {
                Require(request.Option > 0, 20218031);
                Theatre4EventOptionTable option = Rows<Theatre4EventOptionTable>()
                    .FirstOrDefault(candidate => candidate.GroupId == row.OptionGroupId && candidate.Id == request.Option)
                    ?? throw new ServerCodeException("Theatre4 fate option is not authored.", 20218031);
                Require(option.OptionShowCondition is not > 0 || IsConditionMet(m, option.OptionShowCondition.Value), 20218031);
                Require(option.OptionCondition is not > 0 || IsConditionMet(m, option.OptionCondition.Value), 20218031);
                ApplyOption(m, option);
                AdvanceFateOrComplete(m, adventure, fateEvent, row, option.NextEvent, option.NextEventGroupId);
                recorded = true;
            }
            else
            {
                // Option-less rows must receive Option 0 and follow the authored chain first.
                Require(request.Option == 0, 20218031);
                AdvanceFateOrComplete(m, adventure, fateEvent, row, row.NextEvent, row.NextEventGroup);
                recorded = true;
            }

            RebuildEffects(m);
            m.Push(new NotifyTheatre4AdventureData { AdventureData = Clone(adventure) });
            PushFate(m, adventure);
            if (recorded) PushFinishEventRecord(m, adventure);
        });

    private static void AdvanceFateOrComplete(Mutation m, Theatre4AdventureData adventure,
        Theatre4FateEventData fateEvent, Theatre4EventTable row, int? nextEventId, int? nextEventGroup)
    {
        RecordEvent(m, adventure, row.Id);
        ApplyEventOutcome(m, row);
        int next = ResolveNextEventId(m, nextEventId, nextEventGroup);
        Theatre4EventData? advance = next > 0 ? BuildMapEvent(m, next) : null;
        if (advance is null) CompleteFateEvent(adventure, fateEvent);
        else fateEvent.Event = advance;
    }

    private static void CompleteFateEvent(Theatre4AdventureData adventure, Theatre4FateEventData fateEvent)
    {
        adventure.Fate?.FateEvents.Remove(fateEvent);
        if (adventure.Fate is { FateEvents.Count: 0 }) adventure.Fate = null;
    }

    // Called through CompleteEncounter for a fight frozen with FightLocateType.Fate.
    private static bool CompleteFateFight(Mutation m, Theatre4AdventureData adventure, bool won)
    {
        Theatre4FateEventData? fateEvent = adventure.Fate?.FateEvents
            .FirstOrDefault(candidate => candidate.UniqueId == m.State.ActiveEncounter?.FateUniqueId);
        if (fateEvent is null) return false;
        if (!won)
        {
            adventure.Hp = Math.Max(0, adventure.Hp - 1);
            if (adventure.Hp <= 0) EndAdventure(m, settleType: 1, won: false);
            return false;
        }
        if (fateEvent.Event is not { } current) return false;
        Theatre4EventTable? row = Rows<Theatre4EventTable>()
            .FirstOrDefault(candidate => candidate.Id == current.EventId);
        if (row is null) return false;
        AdvanceFateOrComplete(m, adventure, fateEvent, row, row.NextEvent, row.NextEventGroup);
        return true;
    }

    private static void PushFate(Mutation m, Theatre4AdventureData adventure) =>
        m.Push(new NotifyTheatre4FateData { FateData = adventure.Fate is null ? null : Clone(adventure.Fate) });

    //endregion

    //region shared option resolution

    private static void ApplyOption(Mutation m, Theatre4EventOptionTable option)
    {
        int count = Math.Max(0, option.OptionItemCount ?? 0);
        // EN XEnumConst.Theatre4.EventOptionType: 1 CostItem, 2 CheckItem, 3/5 Dialogue, 4 StageScore.
        switch (option.OptionType)
        {
            case 1:
                Require(option.OptionItemType is > 0 && count >= 0
                    && AssetCount(m, option.OptionItemType.Value, option.OptionItemId ?? 0) >= count, 20218030);
                if (count > 0) SpendAsset(m, option.OptionItemType!.Value, option.OptionItemId ?? 0, count);
                break;
            case 2:
                Require(option.OptionItemType is > 0
                    && AssetCount(m, option.OptionItemType.Value, option.OptionItemId ?? 0) >= Math.Max(1, count), 20218030);
                break;
            case 3 or 4 or 5:
                break;
            default:
                throw new ServerCodeException("Theatre4 event option type is not authored.", 20218030);
        }
        if (option.EffectGroupId is > 0) AddEffectGroup(m, option.EffectGroupId.Value);
    }

    private static int ResolveNextEventId(Mutation m, int? nextEvent, int? nextGroup)
    {
        if (nextEvent is > 0) return nextEvent.Value;
        if (nextGroup is not > 0) return 0;
        List<Theatre4EventGroupTable> rows = Rows<Theatre4EventGroupTable>()
            .Where(row => row.GroupId == nextGroup
                && (row.ConditionId is not > 0 || IsConditionMet(m, row.ConditionId.Value)))
            .ToList();
        Theatre4EventGroupTable? chosen = PickWeighted(m, rows, row => (row.Weight ?? 0)
            + row.AddWeight.Select((weight, index) => row.AddWeightCondition.Count > index
                && row.AddWeightCondition[index] is > 0 && IsConditionMet(m, row.AddWeightCondition[index]) ? weight : 0).Sum());
        return chosen?.EventId ?? 0;
    }

    private static void GrantRewards(Mutation m, List<int>? rewardIds)
    {
        if (rewardIds is null) return;
        foreach (int rewardId in rewardIds.Where(id => id > 0)) GrantModeReward(m, rewardId);
    }

    //endregion

    //region encounter completion (called from Map.CompleteEncounter)

    internal static void CompleteEncounterGrid(Mutation m, Theatre4AdventureData adventure, bool won, int score)
    {
        if (m.State.ActiveEncounter is not { } encounter) return;
        // Theatre4ActiveEncounter.Kind: 1 grid fight, 2 grid event, 3 fate. Fate freezes MapId 0.
        if (encounter.MapId <= 0)
        {
            bool fateRecorded = CompleteFateFight(m, adventure, won);
            if (m.Data.AdventureData is null) return;
            PushFate(m, adventure);
            m.Push(new NotifyTheatre4AdventureData { AdventureData = Clone(adventure) });
            if (fateRecorded) PushFinishEventRecord(m, adventure);
            return;
        }
        Theatre4ChapterData? chapter = adventure.Chapters.FirstOrDefault(candidate => candidate.MapId == encounter.MapId);
        Theatre4GridData? grid = chapter?.Grids.FirstOrDefault(candidate => candidate.PosX == encounter.PosX
            && candidate.PosY == encounter.PosY);
        if (chapter is null || grid is null) return;

        if (!won)
        {
            adventure.Hp = Math.Max(0, adventure.Hp - 1);
            if (adventure.Hp <= 0) EndAdventure(m, settleType: 1, won: false);
            if (m.Data.AdventureData is null) return;
            m.Push(new NotifyTheatre4AdventureData { AdventureData = Clone(adventure) });
            return;
        }
        bool recorded = false;
        if (grid.Type == T4Grid.Event)
        {
            recorded = CompleteEventFight(m, adventure, grid, won);
            RevealHiddenRegions(m, chapter);
        }
        else
        {
            grid.State = T4State.Processed;
            if (grid.Type == T4Grid.Monster) chapter.EliteCount = Math.Max(0, chapter.EliteCount - 1);
            if (!adventure.FinishFightIds.Contains(encounter.FightId)) adventure.FinishFightIds.Add(encounter.FightId);
            // Absent-row hidden gates key on all ordinary monsters cleared; that can happen here,
            // immediately before the boss passes/advances the chapter and closes exploration.
            RevealHiddenRegions(m, chapter);
            if (grid.Type == T4Grid.Boss)
            {
                chapter.IsPass = true;
                AdvanceChapter(m, chapter);
                if (m.Data.AdventureData is null) return;
            }
        }
        m.Push(new NotifyTheatre4ChangeGrids { MapId = chapter.MapId, Grids = [Clone(grid)] });
        m.Push(new NotifyTheatre4AdventureData { AdventureData = Clone(adventure) });
        if (recorded) PushFinishEventRecord(m, adventure);
        m.Push(new NotifyTheatre4FinishFightRecord { FinishFightIds = new(adventure.FinishFightIds) });
    }

    //endregion
}
