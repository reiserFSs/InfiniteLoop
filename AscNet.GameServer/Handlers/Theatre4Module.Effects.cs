using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.theatre4;

namespace AscNet.GameServer.Handlers;

// Awakening Tundra effect evaluation, owned by TundraEffects.
//
// Authority:
//   * Table columns are authoritative (AscNet/Resources/table/share/theatre4/Theatre4Effect.tsv,
//     generated as Theatre4EffectTable with nullable indexed columns -> always null-safe here).
//   * Client formulas are transcribed exactly where the EN client actually executes them:
//     XTheatre4EffectSubControl (409/410/411/111/113/209/218/interest/301/416/421-424),
//     XTheatre4MapSubControl (active-skill targeting, ranges), XTheatre4Control (UseSkillEffect cost).
//   * Effect types with no client evaluator are implemented as plainly labelled
//     "AscNet Theatre4 policy" rules derived from the authored Desc/Desc1 text. They are NOT
//     retail facts; every such rule is marked LOCAL POLICY in this file.
//
// Durability: effect instances are persistent Theatre4EffectData objects (CustomEffects,
// Items[].Effects, Props[].Effects, Talents[].Effects). Accumulators live in the instance's
// Count/CustomData/ColorResource/MarkupRate/Accumulate/UseTimes fields so relog and retries
// keep them. One-shot effects (grants, map marks) carry a persisted applied marker so
// RebuildEffects can never double-apply.
internal static partial class Theatre4Module
{
    //region effect configuration

    // CustomData key used to remember that a one-shot effect already ran. Colour keys are 1..3
    // and the client reads key 0 for type 33, so a negative key is never a real accumulator.
    private const int AppliedMarkerKey = -1;

    private static readonly Lazy<Dictionary<int, Theatre4EffectTable>> EffectConfigs =
        new(() => Rows<Theatre4EffectTable>().ToDictionary(row => row.Id));

    private static readonly Lazy<Dictionary<int, List<int>>> EffectGroups =
        new(() => Rows<Theatre4EffectGroupTable>().ToDictionary(row => row.Id, row => row.Effects ?? []));

    private static readonly Lazy<Dictionary<int, Theatre4BuildingTable>> BuildingConfigs =
        new(() => Rows<Theatre4BuildingTable>().ToDictionary(row => row.Id));

    private static readonly Lazy<Dictionary<int, Theatre4ItemTable>> ItemConfigs =
        new(() => Rows<Theatre4ItemTable>().ToDictionary(row => row.Id));

    private static Theatre4EffectTable? EffectConfig(int effectId) => EffectConfigs.Value.GetValueOrDefault(effectId);

    // Indexed columns are only materialised when a row's line reaches them, so every read is guarded.
    private static int EffParam(List<int>? parameters, int index) =>
        parameters is { } list && index >= 0 && index < list.Count ? list[index] : 0;

    private static int EffectTypeOf(Theatre4EffectData effect) => EffectConfig(effect.EffectId)?.Type ?? 0;

    internal static List<int> EffectGroupEffects(int groupId) => EffectGroups.Value.GetValueOrDefault(groupId) ?? [];

    //endregion

    //region effect sources

    // Persisted sources only, in the client's own precedence order.
    internal static IEnumerable<Theatre4EffectData> EnumerateEffects(Theatre4AdventureData adventure)
    {
        foreach (Theatre4EffectData effect in adventure.CustomEffects) yield return effect;
        foreach (Theatre4ItemData item in adventure.Items)
            foreach (Theatre4EffectData effect in item.Effects) yield return effect;
        foreach (Theatre4ItemData prop in adventure.Props)
            foreach (Theatre4EffectData effect in prop.Effects) yield return effect;
        foreach (Theatre4ColorTalentData color in adventure.Colors)
            foreach (Theatre4ColorTalentSlotData slot in color.Slots)
                foreach (Theatre4TalentData talent in slot.Talents)
                    foreach (Theatre4EffectData effect in talent.Effects) yield return effect;
    }
    // Client GetAllEffects contains only persisted talent, item, prop, and custom effects.
    // Theatre4CharacterStar.ColorLevel is a separate character preview field (consumed by the
    // recruitment/formation UI), not an effect-group id; never synthesize effects from it.
    private static IEnumerable<Theatre4EffectData> AllEffects(Theatre4AdventureData adventure) =>
        EnumerateEffects(adventure);


    // Persisted effect instances are the only trigger source; character-star bonuses are DTO
    // fields populated by recruitment and never enter the effect lifecycle.
    private static List<Theatre4EffectData> PersistedEffects(Mutation mutation) =>
        EnumerateEffects(RequireAdventure(mutation)).ToList();

    private static List<Theatre4EffectData> SnapshotEffects(Mutation mutation) =>
        AllEffects(RequireAdventure(mutation)).ToList();

    //endregion

    //region instance lifecycle

    private static Theatre4EffectData NewEffect(Mutation mutation, int effectId, int itemUid)
    {
        if (EffectConfig(effectId) is null) throw new InvalidDataException($"Unknown Theatre4 effect {effectId}.");
        return new Theatre4EffectData { Id = NextId(mutation), EffectId = effectId, ItemUid = itemUid };
    }

    // Fills an item's effect list from its authored EffectGroupId. Called by the economy when an
    // item enters the run; the returned instances are the durable ones the client will render.
    internal static void AddItemEffects(Mutation mutation, Theatre4ItemData item)
    {
        item.Effects = [];
        if (!ItemConfigs.Value.TryGetValue(item.ItemId, out Theatre4ItemTable? config)) return;
        if (config.EffectGroupId is not > 0) return;
        foreach (int effectId in EffectGroupEffects(config.EffectGroupId!.Value))
            item.Effects.Add(NewEffect(mutation, effectId, item.Uid));
    }

    internal static void AddEffects(Mutation mutation, IEnumerable<int> effectIds, int itemUid = 0)
    {
        Theatre4AdventureData adventure = RequireAdventure(mutation);
        Theatre4ItemData? owner = itemUid > 0
            ? adventure.Items.Concat(adventure.Props).FirstOrDefault(candidate => candidate.Uid == itemUid)
            : null;
        foreach (int effectId in effectIds)
        {
            Theatre4EffectData effect = NewEffect(mutation, effectId, itemUid);
            if (owner is not null) owner.Effects.Add(effect);
            else adventure.CustomEffects.Add(effect);
        }
    }

    internal static void AddEffectGroup(Mutation mutation, int groupId, int itemUid = 0) =>
        AddEffects(mutation, EffectGroupEffects(groupId), itemUid);

    internal static void RemoveEffect(Mutation mutation, Theatre4EffectData effect)
    {
        Theatre4AdventureData adventure = RequireAdventure(mutation);
        adventure.CustomEffects.Remove(effect);
        foreach (Theatre4ItemData item in adventure.Items.Concat(adventure.Props))
            if (item.Effects.Remove(effect)) break;
        foreach (Theatre4ColorTalentData color in adventure.Colors)
            foreach (Theatre4ColorTalentSlotData slot in color.Slots)
                foreach (Theatre4TalentData talent in slot.Talents)
                    talent.Effects.Remove(effect);
    }

    internal static void RemoveEffectsOfItem(Mutation mutation, int itemUid)
    {
        Theatre4AdventureData adventure = RequireAdventure(mutation);
        adventure.CustomEffects.RemoveAll(effect => effect.ItemUid == itemUid);
    }

    // Rebuilds derived ownership without duplicating passive stacks and without re-running any
    // one-shot effect twice: newly observed one-shot effects (marker absent) are applied exactly
    // once and marked; orphaned custom effects whose item left the run are dropped. Accumulators
    // (Count/CustomData/ColorResource/MarkupRate/Accumulate/UseTimes) are never reset here.
    internal static void RebuildEffects(Mutation mutation)
    {
        Theatre4AdventureData adventure = RequireAdventure(mutation);
        HashSet<int> liveUids = adventure.Items.Concat(adventure.Props).Where(item => item.Uid > 0)
            .Select(item => item.Uid).ToHashSet();
        adventure.CustomEffects.RemoveAll(effect => effect.ItemUid > 0 && !liveUids.Contains(effect.ItemUid));
        List<Theatre4EffectData> changed = [];
        foreach (Theatre4EffectData effect in EnumerateEffects(adventure))
        {
            if (effect.CustomData.ContainsKey(AppliedMarkerKey)) continue;
            if (ApplyOneShot(mutation, effect)) changed.Add(effect);
        }
        PushEffects(mutation, changed);
    }

    //endregion

    //region triggers

    internal static void TriggerEffects(Mutation mutation, string trigger, Theatre4GridData? grid = null, int value = 0,
        int affectedCount = 1)
    {
        if (trigger is not ("start" or "explore" or "daily" or "shop" or "itemremoved"
            or "build" or "build-point-spent" or "fight" or "sweep" or "tower-clear"))
            throw new InvalidDataException($"Unsupported Theatre4 effect trigger {trigger}.");
        Require(affectedCount > 0, 1);

        if (trigger == "daily")
        {
            ApplyDailySettle(mutation);
            return;
        }
        List<Theatre4EffectData> changed = [];
        if (trigger == "start")
        {
            foreach (Theatre4EffectData effect in PersistedEffects(mutation))
                if (ApplyOneShot(mutation, effect)) changed.Add(effect);
        }
        else
        {
            foreach (Theatre4EffectData effect in PersistedEffects(mutation))
                if (ApplyTrigger(mutation, effect, trigger, grid, value, affectedCount)) changed.Add(effect);
        }
        // Building range resource is a property of the actual exploration transition, not an
        // effect instance. ExploreGridCore processes each tile once, so this cannot be farmed by
        // rebuilding or relogging.
        if (trigger == "explore" && grid is not null)
            ApplyBuildingExploreResource(mutation, grid);
        PushEffects(mutation, changed);
    }

    // Applies the one-shot ("acquired") semantics of an effect at most once, persisting the marker.
    private static bool ApplyOneShot(Mutation mutation, Theatre4EffectData effect)
    {
        Theatre4EffectTable? row = EffectConfig(effect.EffectId);
        if (row is null) return false;
        Theatre4AdventureData adventure = RequireAdventure(mutation);
        bool applied = (row.Type ?? 0) switch
        {
            219 => GrantAsset(mutation, row),
            412 => SetAsset(mutation, row),
            // 14 Inflation: authored start multiplier (x3 -> x1 over the run).
            14 => ApplyInitialMarkup(effect, row),
            // 409 change-asset: event-option groups are added on option choice and applied here
            // once (signed delta path), so RebuildEffects cannot double-run it.
            409 => ApplyChangeAsset(mutation, effect.EffectId),
            405 => MarkTiles(mutation, adventure, row, open: true),
            406 => MarkTiles(mutation, adventure, row, open: false),
            408 => GenerateMarkTiles(mutation, adventure, row),
            215 => SpawnTreasureThief(mutation, adventure, row),
            // 216 replaces the current low-risk ordinary groups on activation. Map also applies
            // the same authored replacement when a later chapter is created.
            216 => ApplyLowRiskEnemyReplacements(mutation, adventure, row.Params ?? []),
            413 or 415 => ApplySpecialTalentEffect(mutation, row),
            _ => false
        };
        if (applied) effect.CustomData[AppliedMarkerKey] = 1;
        return applied;
    }

    private static bool ApplyInitialMarkup(Theatre4EffectData effect, Theatre4EffectTable row)
    {
        effect.MarkupRate = EffParam(row.Params, 0);
        return true;
    }

    private static bool ApplyTrigger(Mutation mutation, Theatre4EffectData effect, string trigger,
        Theatre4GridData? grid, int value, int affectedCount)
    {
        Theatre4EffectTable? row = EffectConfig(effect.EffectId);
        if (row is null) return false;
        return (row.Type ?? 0) switch
        {
            1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9 or 10 or 11 or 12 or 13 or 14 or 15 or 16 or 17
                or 18 or 19 or 20 or 21 or 22 or 23 or 24 or 26 or 27 or 28 or 29 or 31 or 32 or 33
                or 34 or 35 or 36 or 37 or 38 => ApplyItemEffect(mutation, effect, row, trigger, grid, value,
                    affectedCount),
            101 or 102 or 103 or 104 or 105 or 106 or 107 or 108 or 109 or 110 or 111 or 112 or 113 or 116
                or 118 or 119 or 120 or 122 or 123 or 125 => ApplyBuildingPassive(mutation, effect, row, trigger,
                    grid, value, affectedCount),
            211 or 212 or 213 or 216 or 217 => ApplyItemEffect(mutation, effect, row, trigger, grid, value, affectedCount),
            _ => false
        };
    }

    //endregion

    //region item / economy effect semantics

    private static bool ApplyItemEffect(Mutation mutation, Theatre4EffectData effect, Theatre4EffectTable row,
        string trigger, Theatre4GridData? grid, int value, int affectedCount)
    {
        Theatre4AdventureData adventure = RequireAdventure(mutation);
        int type = row.Type ?? 0;
        switch (trigger)
        {
            case "explore":
                return ApplyExploreEffect(mutation, adventure, effect, row, grid);
            case "shop":
                return ApplyShopEffect(mutation, adventure, effect, row, grid, value);
            case "build":
                return ApplyBuildEffect(mutation, adventure, effect, row, grid, value, affectedCount);
            case "build-point-spent":
                return ApplyBuildPointSpentEffect(mutation, adventure, effect, row, value);
            case "fight":
            case "sweep":
            case "tower-clear":
                return ApplyFightEffect(mutation, adventure, effect, row, trigger, grid, value);
            case "itemremoved":
                return ApplyItemRemovedEffect(mutation, adventure, effect, row);
            case "daily":
                // Daily accumulation is materialised by ApplyDailySettle as one pass over all effects.
                return false;
            default:
                return false;
        }
    }

    private static bool ApplyExploreEffect(Mutation mutation, Theatre4AdventureData adventure,
        Theatre4EffectData effect, Theatre4EffectTable row, Theatre4GridData? grid)
    {
        if (grid is null) return false;
        int type = row.Type ?? 0;
        switch (type)
        {
            // 3 Adventurer: resource +P for an area not bordering the previous one.
            case 3:
                if (grid.Color <= 0 || IsAdjacentToPrevious(adventure, grid)) return false;
                AddAsset(mutation, EffAssetColorResource, grid.Color, EffParam(row.Params, 0));
                return true;
            // 4 Lucky Star: bank while on Empty, cash on the next Non-Empty.
            case 4:
                if (grid.Type == EffGridEmpty)
                {
                    effect.Count = checked(effect.Count + Math.Max(1, EffParam(row.Params, 0)));
                    return true;
                }
                if (effect.Count <= 0) return false;
                int lumps = effect.Count / Math.Max(1, EffParam(row.Params, 0));
                AddAsset(mutation, EffAssetColorResource, EffectColor(adventure, grid), lumps * EffParam(row.Params, 1));
                effect.Count = 0;
                return true;
            // 5 / 6: final / first action of the day.
            case 5:
                if (adventure.Ap > 0) return false;
                AddAsset(mutation, EffAssetColorResource, EffectColor(adventure, grid), EffParam(row.Params, 0));
                return true;
            case 6:
                int dayKey = adventure.Days;
                if (effect.CustomData.ContainsKey(dayKey)) return false;
                effect.CustomData[dayKey] = 1;
                AddAsset(mutation, EffAssetColorResource, EffectColor(adventure, grid), EffParam(row.Params, 0));
                return true;
            // 10 Bounty Notice: colour resource in an enemy tile's area doubles.
            case 10:
                if (grid.Type is not (EffGridMonster or EffGridBoss)) return false;
                int doubled = grid.ColorResource;
                if (doubled <= 0) return false;
                grid.ColorResource = checked(doubled * 2);
                mutation.Push(new NotifyTheatre4ChangeGrids { MapId = grid.GridId > 0 ? RequireChapterMapId(adventure) : 0, Grids = [grid] });
                return true;
            // 11 Resource Conversion: LOCAL POLICY — spend Params[0] Gold for the same amount of
            // the explored tile's colour resource when affordable.
            case 11:
                if (EffParam(row.Params, 0) <= 0 || AssetCount(mutation, EffAssetGold, 0) < EffParam(row.Params, 0)) return false;
                SpendAsset(mutation, EffAssetGold, 0, EffParam(row.Params, 0));
                AddAsset(mutation, EffAssetColorResource, EffectColor(adventure, grid), EffParam(row.Params, 0));
                return true;
            // 32: reward every N matching tiles. [gridType, interval, colorFilter, assetType, assetId, count]
            case 32:
                int gridFilter = EffParam(row.Params, 0);
                int colorFilter = EffParam(row.Params, 2);
                if (gridFilter != 0 && grid.Type != gridFilter) return false;
                if (colorFilter != 0 && grid.Color != colorFilter) return false;
                effect.Accumulate = checked(effect.Accumulate + 1);
                if (effect.Accumulate % Math.Max(1, EffParam(row.Params, 1)) != 0) return true;
                AddAsset(mutation, EffParam(row.Params, 3), EffParam(row.Params, 4), EffParam(row.Params, 5));
                return true;
            // 33: exploring a matching tile grants a named combat effect. [gridType, interval, colorFilter, magicId]
            case 33:
                if (EffParam(row.Params, 0) != 0 && grid.Type != EffParam(row.Params, 0)) return false;
                if (EffParam(row.Params, 2) != 0 && grid.Color != EffParam(row.Params, 2)) return false;
                effect.CustomData[effect.CustomData.Count] = EffParam(row.Params, 3);
                return true;
            // 213 Jenga: consecutive same-colour tiles escalate the gold reward.
            case 213:
                if (grid.Color != EffParam(row.Params, 0))
                {
                    effect.Count = 0;
                    return false;
                }
                AddAsset(mutation, EffAssetGold, 0, EffParam(row.Params, 3) + effect.Count * EffParam(row.Params, 4));
                effect.Count = checked(effect.Count + 1);
                return true;
            default:
                return false;
        }
    }

    // Type7/9 target a recruited character's colour multipliers. Choices resolves the authored
    // CharacterStar rows and passes the before/after maps here; this also credits the corresponding
    // persistent colour levels once, so the DTO and global asset stay in sync.
    internal static void ApplyRecruitCharacterEffects(Mutation mutation, Theatre4CharacterData character,
        IReadOnlyDictionary<int, int> beforeLevels, IReadOnlyDictionary<int, int> afterLevels,
        int starUps, bool isNew)
    {
        character.ColorLevelAdds ??= [];
        int bonus = isNew
            ? EffectSimpleValue(mutation, 9)
            : checked(Math.Max(0, starUps) * EffectSimpleValue(mutation, 7));
        for (int color = 1; color <= 3; color++)
        {
            int before = beforeLevels.TryGetValue(color, out int oldLevel) ? oldLevel : 0;
            int after = afterLevels.TryGetValue(color, out int newLevel) ? newLevel : 0;
            if (after <= 0) continue;
            int delta = checked(after - before + bonus);
            if (delta == 0) continue;
            AddAsset(mutation, EffAssetColorLevel, color, delta);
            character.ColorLevelAdds[color] = checked(character.ColorLevelAdds.GetValueOrDefault(color) + delta);
        }
    }

    private static bool ApplyShopEffect(Mutation mutation, Theatre4AdventureData adventure,
        Theatre4EffectData effect, Theatre4EffectTable row, Theatre4GridData? grid, int value)
    {
        int type = row.Type ?? 0;
        switch (type)
        {
            // 2 Hoarder: random colour resource +P per purchase (value > 0).
            case 2:
                if (value <= 0) return false;
                int color = 1 + RandomIndex(mutation, 3);
                int amount = checked(value * EffParam(row.Params, 0));
                effect.CustomData[color] = checked(effect.CustomData.GetValueOrDefault(color) + amount);
                AddAsset(mutation, EffAssetColorResource, color, amount);
                return true;
            // 22 Big Buyer: tally markup +P per purchase.
            case 22:
                if (value <= 0) return false;
                effect.MarkupRate = checked(effect.MarkupRate + value * EffParam(row.Params, 0));
                return true;
            // 26 Random Sale: while owned, a random product column is free in each new shop;
            // any purchase consumes the item (EN Theatre4Item row 26 description).
            case 26:
                if (value <= 0 || effect.CustomData.ContainsKey(AppliedMarkerKey)) return false;
                effect.CustomData[AppliedMarkerKey] = 1;
                return true;
            // 217 Shop & Earn: entry gold once per shop grid. Economy only fires the shop trigger
            // on first entry with value 0 (refresh uses its own marker, purchase uses value > 0),
            // so hidden/unexplored shop initialisation never reaches this.
            case 217:
                if (value != 0 || grid is null) return false;
                int entryKey = ShopEntryKey(adventure, grid);
                if (effect.CustomData.ContainsKey(entryKey)) return false;
                effect.CustomData[entryKey] = 1;
                ApplyAssetDelta(mutation, EffParam(row.Params, 0), EffParam(row.Params, 1), EffParam(row.Params, 2));
                return true;
            default:
                return false;
        }
    }

    private static bool ApplyBuildEffect(Mutation mutation, Theatre4AdventureData adventure,
        Theatre4EffectData effect, Theatre4EffectTable row, Theatre4GridData? grid, int value, int affectedCount)
    {
        int type = row.Type ?? 0;
        switch (type)
        {
            // 13 City Architect: tally resource +P per build/alter performed today.
            case 13:
                effect.Count = checked(effect.Count + 1);
                return true;
            // 212 Build Diversity: gold +P per build/alter.
            case 212:
                AddAsset(mutation, EffParam(row.Params, 0), EffParam(row.Params, 1), EffParam(row.Params, 2));
                return true;
            // 112 Modification Collab: one refund for an actual modification in any building range.
            case 112:
                if (value is not (EffAlterAlterColor or EffAlterRemoveHurdle)
                    || grid is null || !InAnyBuildingRange(mutation, grid)) return false;
                AddAsset(mutation, EffParam(row.Params, 0), EffParam(row.Params, 1), EffParam(row.Params, 2));
                return true;
            // 122 Collab: a modification arms one refund for the next building.
            case 122:
                if (value is EffAlterAlterColor or EffAlterRemoveHurdle)
                {
                    effect.Count = 1;
                    return true;
                }
                if (value == EffAlterCreateBuilding && effect.Count > 0)
                {
                    effect.Count = 0;
                    AddAsset(mutation, EffParam(row.Params, 0), EffParam(row.Params, 1), EffParam(row.Params, 2));
                    return true;
                }
                return false;
            // 125 Free-Loading: a modification receives the affected tile's authored resource once,
            // scaled by Params[0] / 10000 (the authored row uses 10000 = 100%).
            case 125:
                if (value is not (EffAlterAlterColor or EffAlterRemoveHurdle) || grid is null
                    || grid.Color <= 0 || grid.ColorResource <= 0) return false;
                int resource = checked((int)((long)grid.ColorResource * EffParam(row.Params, 0) / 10000));
                if (resource <= 0) return false;
                AddAsset(mutation, EffAssetColorResource, grid.Color, resource);
                return true;
            // 118 Mining: gold per actually removed obstacle (including Type119 extras).
            case 118:
                if (value != EffAlterRemoveHurdle || affectedCount <= 0) return false;
                AddAsset(mutation, EffParam(row.Params, 0), EffParam(row.Params, 1),
                    checked(affectedCount * EffParam(row.Params, 2)));
                return true;
            default:
                return false;
        }
    }

    private static bool ApplyBuildPointSpentEffect(Mutation mutation, Theatre4AdventureData adventure,
        Theatre4EffectData effect, Theatre4EffectTable row, int amount)
    {
        if ((row.Type ?? 0) != 211 || amount <= 0) return false;
        AddAsset(mutation, EffParam(row.Params, 0), EffParam(row.Params, 1),
            checked(amount * EffParam(row.Params, 2)));
        return true;
    }

    private static bool ApplyFightEffect(Mutation mutation, Theatre4AdventureData adventure,
        Theatre4EffectData effect, Theatre4EffectTable row, string trigger, Theatre4GridData? grid, int value)
    {
        int type = row.Type ?? 0;
        switch (type)
        {
            // 17 Watchtower Specialty: only an actual arrow-tower clear counts.
            case 17:
                if (trigger != "tower-clear" || grid is null || value <= 0) return false;
                effect.MarkupRate = checked(effect.MarkupRate + value * EffParam(row.Params, 1));
                return true;
            // 34 Pacify Expert: the authored Red and Yellow proxy sweeps both count; normal fights
            // do not, and tower clears are a separate trigger.
            case 34:
                if (trigger != "sweep" || grid is null || value <= 0) return false;
                effect.MarkupRate = checked(effect.MarkupRate + value * EffParam(row.Params, 1));
                return true;
            // 109 Precision / Eradicate / Slaughter: reward per destroyed node.
            case 109:
                if (trigger is not ("fight" or "sweep" or "tower-clear") || value <= 0) return false;
                AddAsset(mutation, EffParam(row.Params, 0), EffParam(row.Params, 1),
                    checked(value * EffParam(row.Params, 2)));
                return true;
            // 108 Color Level: the authored Params[0] is the tri-colour reward-drop id. It applies
            // to every genuine fight/sweep/tower clear only when the target is in a Supply Depot's
            // cube range.
            case 108:
                if (trigger is not ("fight" or "sweep" or "tower-clear") || value <= 0) return false;
                Theatre4GridData? target = grid ?? CombatGrid(mutation);
                if (target is null || !InBuildingRange(mutation, target, 3)) return false;
                GrantModeDrop(mutation, EffParam(row.Params, 0));
                return true;
            default:
                return false;
        }
    }


    private static bool ApplyItemRemovedEffect(Mutation mutation, Theatre4AdventureData adventure,
        Theatre4EffectData effect, Theatre4EffectTable row)
    {
        // 16 Destroyer: markup +P whenever a blueprint leaves the run.
        if ((row.Type ?? 0) == 16)
        {
            effect.MarkupRate = checked(effect.MarkupRate + EffParam(row.Params, 0));
            return true;
        }
        return false;
    }

    //endregion

    //region building / talent passives

    // Building passives are attached to the colour talent that owns the building skill. They are
    // evaluated at the map boundary where they act; range-geometry passives use the map helpers.
    private static bool ApplyBuildingPassive(Mutation mutation, Theatre4EffectData effect, Theatre4EffectTable row,
        string trigger, Theatre4GridData? grid, int value, int affectedCount)
    {
        Theatre4AdventureData adventure = RequireAdventure(mutation);
        int type = row.Type ?? 0;
        switch (type)
        {
            // Type101 owns the concrete construction outcome for its authored building id.
            case 101:
                if (trigger != "build" || grid?.Building is null
                    || grid.Building.BuildingId != EffParam(row.Params, 0)) return false;
                return ApplyBuildingConstruction(mutation, adventure, effect, grid);
            // 102 cost reductions are consumed by GetEffectCost, never as a build-time grant.
            case 102:
                return false;
            // 103 Flip Open Manually: creating a Bonfire opens the nearest still-unknown tile.
            case 103:
                if (trigger != "build" || grid?.Building?.BuildingType != 1) return false;
                return OpenNearestUnknown(mutation, adventure, grid);
            // 105 is consumed only after a tower actually takes effect.
            case 105:
            case 106:
            case 107:
                return false;
            // 108/109 are combat-clear effects despite being authored with building groups.
            case 108:
            case 109:
                if (trigger is not ("fight" or "sweep" or "tower-clear")) return false;
                return ApplyFightEffect(mutation, adventure, effect, row, trigger, grid, value);
            // 110 Save Money: opening enough tiles inside a Supply Depot's range pays a drop.
            case 110:
                if (effect.CustomData.ContainsKey(AppliedMarkerKey)) return false;
                if (trigger != "explore" || grid is null || !InBuildingRange(mutation, grid, 3)) return false;
                effect.Count = checked(effect.Count + 1);
                if (effect.Count < Math.Max(1, EffParam(row.Params, 0))) return true;
                GrantModeDrop(mutation, EffParam(row.Params, 1));
                effect.CustomData[AppliedMarkerKey] = 1;
                return true;
            // 111/113/209/218 daily adds are materialised by ApplyDailySettle.
            case 111 or 113 or 209 or 218:
                return false;
            // 112/118/122/125 share the real modification context.
            case 112 or 118 or 122 or 125:
                if (trigger != "build") return false;
                return ApplyBuildEffect(mutation, adventure, effect, row, grid, value, affectedCount);
            default:
                return false;
        }
    }

    private static bool ApplyBuildingConstruction(Mutation mutation, Theatre4AdventureData adventure,
        Theatre4EffectData effect, Theatre4GridData origin)
    {
        Theatre4ChapterData? chapter = ChapterForGrid(adventure, origin);
        if (chapter is null || origin.Building is null) return false;
        int buildingType = origin.Building.BuildingType;
        if (buildingType is not (1 or 2 or 5)) return false;
        int receipt = BuildingReceiptKey(adventure, origin);
        if (effect.CustomData.ContainsKey(receipt)) return false;
        // The building operation is one-shot per placed grid; future exploration remains a
        // separate state transition and therefore cannot duplicate its range reward.
        effect.CustomData[receipt] = 1;
        if (buildingType is 1 or 5) ExploreBuildingCross(mutation, chapter.MapId, origin,
            buildingType == 1 ? 2 : 1);
        if (buildingType is 2 or 5) ApplyArrowTower(mutation, adventure, chapter, origin);
        return true;
    }

    private static void ExploreBuildingCross(Mutation mutation, int mapId, Theatre4GridData origin, int radius)
    {
        foreach (Theatre4GridData target in CrossRange(mutation, mapId, origin.PosX, origin.PosY, radius))
            RevealBuildingTarget(mutation, target);
    }

    private static bool ApplyArrowTower(Mutation mutation, Theatre4AdventureData adventure,
        Theatre4ChapterData chapter, Theatre4GridData origin)
    {
        List<Theatre4GridData> range = BuildingRange(mutation, chapter, origin)
            .Where(IsActiveCombatTarget).ToList();
        List<Theatre4GridData> ordinary = range.Where(grid => grid.Type == EffGridMonster).ToList();
        List<Theatre4GridData> selected = [];
        if (HasEffect(mutation, 106))
            selected.AddRange(ordinary);
        else if (ordinary.Count > 0)
            selected.Add(ordinary[RandomIndex(mutation, ordinary.Count)]);

        bool acted = false;
        foreach (Theatre4GridData target in selected)
        {
            CompleteTheatre4BuildingClear(mutation, target);
            acted = true;
        }

        List<Theatre4GridData> damagedBosses = [];
        if (HasEffect(mutation, 107))
            foreach (Theatre4GridData boss in range.Where(grid => grid.Type == EffGridBoss))
            {
                int current = Math.Clamp(boss.Fight?.HpPercent ?? 10000, 1, 10000);
                int ratio = Math.Clamp(EffectSimpleValue(mutation, 107), 1, 9999);
                int updated = Math.Max(1, (int)((long)current * (10000 - ratio) / 10000));
                if (boss.Fight is null) continue;
                boss.Fight.HpPercent = updated;
                damagedBosses.Add(boss);
                acted = true;
            }
        if (damagedBosses.Count > 0)
            mutation.Push(new NotifyTheatre4ChangeGrids
            {
                MapId = chapter.MapId, Grids = damagedBosses.Select(Clone).ToList()
            });
        if (acted) ConsumeTowerSelfDestruction(mutation);
        return acted;
    }

    private static bool IsActiveCombatTarget(Theatre4GridData grid) =>
        grid.State is >= EffStateVisible and < EffStateProcessed
        && grid.Fight is { FightGroupId: > 0, StageId: > 0 };

    private static void ConsumeTowerSelfDestruction(Mutation mutation)
    {
        foreach (Theatre4EffectData effect in SnapshotEffects(mutation)
            .Where(candidate => EffectTypeOf(candidate) == 105).ToList())
        {
            Theatre4EffectTable? row = EffectConfig(effect.EffectId);
            RemoveEffect(mutation, effect);
            int refund = EffParam(row?.Params, 0);
            if (refund > 0) AddAsset(mutation, EffAssetBuildPoint, 0, refund);
        }
    }

    private static bool OpenNearestUnknown(Mutation mutation, Theatre4AdventureData adventure, Theatre4GridData origin)
    {
        Theatre4ChapterData? chapter = ChapterForGrid(adventure, origin);
        if (chapter is null) return false;
        foreach (Theatre4GridData target in chapter.Grids
            .Where(grid => grid.Type != EffGridNothing && grid.State < EffStateExplored)
            .OrderBy(grid => Math.Abs(grid.PosX - origin.PosX) + Math.Abs(grid.PosY - origin.PosY))
            .ThenBy(grid => grid.GridId))
            if (ExploreGridFromBuilding(mutation, target)) return true;
        return false;
    }

    // 104: one deterministic tile per effect instance, chosen from every Bonfire's diamond range.
    private static bool OpenBonfireTile(Mutation mutation, Theatre4AdventureData adventure)
    {
        Theatre4ChapterData? chapter = adventure.Chapters.LastOrDefault();
        if (chapter is null) return false;
        HashSet<int> blocked = ClosedHiddenCells(mutation, chapter);
        List<Theatre4GridData> candidates = [];
        foreach (Theatre4GridData building in chapter.Grids.Where(grid =>
            grid.Type == EffGridBuilding && grid.Building?.BuildingType == 1))
            foreach (Theatre4GridData target in BuildingRange(mutation, chapter, building))
                if (target.State < EffStateExplored && target.Type != EffGridNothing
                    && !blocked.Contains(KeyOf(target.PosX, target.PosY))
                    && !candidates.Contains(target)) candidates.Add(target);
        if (candidates.Count == 0) return false;
        return ExploreGridFromBuilding(mutation, candidates[RandomIndex(mutation, candidates.Count)]);
    }


    //endregion

    //region daily settlement

    // Called by Map on "daily". Applies the daily grants/growth; tally-only accumulators reset
    // only after ComputeSettlementBonuses has consumed them.
    private static void ApplyDailySettle(Mutation mutation)
    {
        Theatre4AdventureData adventure = RequireAdventure(mutation);
        List<Theatre4EffectData> changed = [];
        int buildPoint = 0;
        int gold = 0;
        foreach (Theatre4EffectData effect in SnapshotEffects(mutation))
        {
            Theatre4EffectTable? row = EffectConfig(effect.EffectId);
            if (row is null) continue;
            int type = row.Type ?? 0;
            switch (type)
            {
                case 1:
                    foreach (int color in DistinctDailyColors(adventure))
                        if (DailyColorCount(adventure, color) >= Math.Max(1, EffParam(row.Params, 0)))
                        {
                            effect.CustomData[color] = checked(effect.CustomData.GetValueOrDefault(color) + EffParam(row.Params, 1));
                            changed.Add(effect);
                        }
                    break;
                case 15:
                    effect.MarkupRate = (EffParam(row.Params, 0) + RandomIndex(mutation, Math.Max(1, EffParam(row.Params, 1) - EffParam(row.Params, 0) + 1))) * 10000;
                    changed.Add(effect);
                    break;
                // 14 Inflation: decay the authored per-mille bonus by Params[2] to a floor of x1.
                case 14:
                    effect.MarkupRate = Math.Max(10000, effect.MarkupRate - EffParam(row.Params, 2));
                    changed.Add(effect);
                    break;
                case 18:
                    effect.MarkupRate = adventure.Items.Count * EffParam(row.Params, 0);
                    changed.Add(effect);
                    break;
                case 19:
                    effect.MarkupRate = DistinctDailyColors(adventure).Count() >= Math.Max(1, EffParam(row.Params, 0))
                        ? EffParam(row.Params, 1) : 0;
                    changed.Add(effect);
                    break;
                case 27:
                    // 27 Golden/Silver/Copper Egg: pay the daily upkeep, then self-destruct with a
                    // reward once the authored accumulation threshold is reached.
                    if (EffParam(row.Params, 0) > 0 && AssetCount(mutation, EffAssetGold, 0) >= EffParam(row.Params, 0))
                        SpendAsset(mutation, EffAssetGold, 0, EffParam(row.Params, 0));
                    effect.Count = checked(effect.Count + EffParam(row.Params, 0));
                    if (effect.Count >= Math.Max(1, EffParam(row.Params, 1)))
                    {
                        GrantModeReward(mutation, EffParam(row.Params, 2));
                        AddAsset(mutation, EffAssetItemLimit, 0, 1);
                        RemoveEffect(mutation, effect);
                    }
                    changed.Add(effect);
                    break;
                case 35:
                    if (adventure.Days > 0 && adventure.Days % Math.Max(1, EffParam(row.Params, 0)) == 0)
                        AddAsset(mutation, EffParam(row.Params, 1), EffParam(row.Params, 2), EffParam(row.Params, 3));
                    break;
                case 38:
                    if (DistinctDailyColors(adventure).Count() >= Math.Max(1, EffParam(row.Params, 0)))
                        for (int color = 1; color <= 3; color++)
                        {
                            if (EffParam(row.Params, 1) != 0) AddAsset(mutation, EffAssetColorResource, color, EffParam(row.Params, 1));
                            if (EffParam(row.Params, 2) != 0) AddAsset(mutation, EffAssetColorLevel, color, EffParam(row.Params, 2));
                        }
                    break;
                case 31:
                    // LOCAL POLICY: "area Coloured Resource -P" is realised as a per-colour daily delta.
                    for (int color = 1; color <= 3; color++)
                        ApplyAssetDelta(mutation, EffAssetColorDailyResource, color, EffParam(row.Params, 0));
                    break;
                case 104:
                    // 104 Flip Open Automatically: open one still-unknown tile inside a Bonfire's range.
                    if (OpenBonfireTile(mutation, adventure)) changed.Add(effect);
                    break;
                // 123 Enhancement: every colour gains the authored per-tile talent amount daily.
                case 123:
                    for (int color = 1; color <= 3; color++) AddAsset(mutation, EffAssetColorPoint, color, EffParam(row.Params, 0));
                    break;
            }
        }
        // 111/113 BuildPoint and 113/209/218 gold adds (exact client accumulation rules).
        buildPoint += DailyBuildPointAdds(mutation);
        gold += DailyGoldAdds(mutation);
        if (buildPoint != 0) AddAsset(mutation, EffAssetBuildPoint, 0, buildPoint);
        if (gold != 0) AddAsset(mutation, EffAssetGold, 0, gold);
        PushEffects(mutation, changed);
    }

    internal static void ResetDailySettlementAccumulators(Mutation mutation)
    {
        Theatre4AdventureData adventure = RequireAdventure(mutation);
        List<Theatre4EffectData> changed = [];
        foreach (Theatre4EffectData effect in EnumerateEffects(adventure))
            if (EffectTypeOf(effect) is 13 or 22 && effect.Count != 0)
            {
                effect.Count = 0;
                changed.Add(effect);
            }
        PushEffects(mutation, changed);
    }

    // Base building recoveries use all chapters, matching MapSubControl's permanent totals.
    // Type113's "current map" multiplier deliberately uses only the active chapter.
    internal static int DailyBuildPointAdds(Mutation mutation)
    {
        Theatre4AdventureData adventure = RequireAdventure(mutation);
        int buildingCount = CurrentChapterBuildingCount(adventure);
        int value = 0;
        foreach (Theatre4ChapterData chapter in adventure.Chapters)
            foreach (Theatre4GridData grid in chapter.Grids)
            {
                if (grid.Type != EffGridBuilding || grid.Building is null
                    || !BuildingConfigs.Value.TryGetValue(grid.Building.BuildingId, out Theatre4BuildingTable? config))
                    continue;
                if (grid.Building.BuildingType == 1) value += EffParam(config.Params, 0);
                else if (grid.Building.BuildingType == 5) value += EffParam(config.Params, 1);
            }
        foreach (Theatre4EffectData effect in SnapshotEffects(mutation))
        {
            Theatre4EffectTable? row = EffectConfig(effect.EffectId);
            if (row is null) continue;
            int type = row.Type ?? 0;
            if (type == 111 && EffParam(row.Params, 0) == EffAssetBuildPoint) value += EffParam(row.Params, 2);
            else if (type == 113 && buildingCount > 0 && EffParam(row.Params, 0) == EffAssetBuildPoint)
                value += EffParam(row.Params, 2) * buildingCount;
        }
        return value;
    }

    // 113 (per current-map building) + 209 (interest below cap) + 218 (per shop purchase) gold adds.
    internal static int DailyGoldAdds(Mutation mutation)
    {
        Theatre4AdventureData adventure = RequireAdventure(mutation);
        int buildingCount = CurrentChapterBuildingCount(adventure);
        (int interest, int limit) = Interest(mutation);
        int value = 0;
        foreach (Theatre4EffectData effect in SnapshotEffects(mutation))
        {
            Theatre4EffectTable? row = EffectConfig(effect.EffectId);
            if (row is null) continue;
            int type = row.Type ?? 0;
            if (type == 113 && buildingCount > 0 && EffParam(row.Params, 0) == EffAssetGold)
                value += EffParam(row.Params, 2) * buildingCount;
            else if (type == 209 && interest < limit && EffParam(row.Params, 0) == EffAssetGold)
                value += EffParam(row.Params, 2);
            else if (type == 218 && adventure.EffectShopBuyTimes > 0 && EffParam(row.Params, 0) == EffAssetGold)
                value += EffParam(row.Params, 2) * adventure.EffectShopBuyTimes;
        }
        return value;
    }

    // Exact client interest rule: floor(Gold/InterestNeedCount)*InterestAwardCount + pacify bonus,
    // capped by InterestAwardLimit + 208/210 extras.
    internal static (int Interest, int Limit) Interest(Mutation mutation)
    {
        Theatre4AdventureData adventure = RequireAdventure(mutation);
        int need = Math.Max(1, ConfigInt("InterestNeedCount"));
        int award = ConfigInt("InterestAwardCount");
        int limit = ConfigInt("InterestAwardLimit");
        foreach (Theatre4EffectData effect in SnapshotEffects(mutation))
        {
            Theatre4EffectTable? row = EffectConfig(effect.EffectId);
            if (row is null) continue;
            switch (row.Type ?? 0)
            {
                case 207: limit += EffParam(row.Params, 0) * adventure.EffectSweepTimes; break;
                case 208: limit += EffParam(row.Params, 0); break;
                case 210: limit += effect.CustomData.GetValueOrDefault(0); break;
            }
        }
        int interest = ((adventure.Gold / need) * award) + EffectInterestFinalBonus(mutation);
        if (interest >= limit)
        {
            // 210 grows its extra cap when interest is already full (client EffectWhenInterestFullExtra).
            foreach (Theatre4EffectData effect in SnapshotEffects(mutation))
            {
                Theatre4EffectTable? row = EffectConfig(effect.EffectId);
                if (row?.Type != 210) continue;
                int current = effect.CustomData.GetValueOrDefault(0);
                int maximum = EffParam(row.Params, 1);
                if (current < maximum) limit += Math.Min(current + EffParam(row.Params, 0), maximum) - current;
            }
            interest = Math.Min(interest, limit);
        }
        return (Math.Max(0, interest), Math.Max(0, limit));
    }

    private static int EffectInterestFinalBonus(Mutation mutation)
    {
        Theatre4AdventureData adventure = RequireAdventure(mutation);
        int bonus = 0;
        foreach (Theatre4EffectData effect in SnapshotEffects(mutation))
        {
            Theatre4EffectTable? row = EffectConfig(effect.EffectId);
            if (row?.Type == 207) bonus += EffParam(row.Params, 0) * adventure.EffectSweepTimes;
        }
        return bonus;
    }

    //endregion

    //region settlement

    // Everything the client computes "结算时" (at tally). Map/EndAdventure consumes this to build
    // the settlement payload; values are derived on demand so repeated calls cannot stack.
    internal static Theatre4SettlementBonuses ComputeSettlementBonuses(Mutation mutation)
    {
        Theatre4AdventureData adventure = RequireAdventure(mutation);
        Theatre4SettlementBonuses result = new();
        int maxStar = adventure.Characters.Count == 0 ? 0 : adventure.Characters.Max(character => character.Star);
        bool straight = DailyShapeSatisfied(adventure, straight: true);
        bool corner = DailyShapeSatisfied(adventure, straight: false);
        foreach (Theatre4EffectData effect in SnapshotEffects(mutation))
        {
            Theatre4EffectTable? row = EffectConfig(effect.EffectId);
            if (row is null) continue;
            int type = row.Type ?? 0;
            // Type 14/15 values are complete xN factors; all other rates are additive basis points.
            if (type is 14 or 15) result.AddAbsoluteMarkup(effect.MarkupRate);
            else result.AddMarkup(effect.MarkupRate);
            switch (type)
            {
                // 1/2 accumulate a permanent per-colour resource bank; realised and consumed at tally.
                case 1 or 2:
                    for (int color = 1; color <= 3; color++)
                    {
                        int amount = effect.CustomData.GetValueOrDefault(color);
                        result.AddResource(color, amount);
                        result.AddPermanentResource(color, amount);
                    }
                    break;
                case 4:
                    if (effect.Count > 0)
                    {
                        int lumps = effect.Count / Math.Max(1, EffParam(row.Params, 0));
                        for (int color = 1; color <= 3; color++) result.AddResource(color, lumps * EffParam(row.Params, 1));
                    }
                    break;
                // 8 Superstar: all colours level up by the highest character star.
                case 8:
                    for (int color = 1; color <= 3; color++) result.AddLevel(color, maxStar * EffParam(row.Params, 0));
                    break;
                // 12/36/37 shape tallies.
                case 12 when straight:
                    for (int color = 1; color <= 3; color++) result.AddResource(color, EffParam(row.Params, 0));
                    break;
                case 13:
                    for (int color = 1; color <= 3; color++) result.AddResource(color, effect.Count * EffParam(row.Params, 0));
                    break;
                case 20:
                    result.AddAsset(EffParam(row.Params, 0), EffParam(row.Params, 1), adventure.Ap * EffParam(row.Params, 2));
                    break;
                case 21:
                    result.AddMarkup(adventure.Chapters.SelectMany(chapter => chapter.Grids)
                        .Where(grid => grid.State >= EffStateExplored).Select(grid => grid.Color).Distinct().Count()
                        * EffParam(row.Params, 0));
                    break;
                // 28 Foreign Exchange: gold equal to owned Construction Permits x P.
                case 28:
                    result.AddAsset(EffAssetGold, 0, AssetCount(mutation, EffAssetBuildPoint, 0) * EffParam(row.Params, 0));
                    break;
                case 29 when corner:
                    result.AddAsset(EffAssetGold, 0, EffParam(row.Params, 0));
                    break;
                case 36 when straight:
                    result.AddAsset(EffParam(row.Params, 0), EffParam(row.Params, 1), EffParam(row.Params, 2));
                    break;
                case 37 when corner:
                    result.AddMarkup(EffParam(row.Params, 0));
                    for (int color = 1; color <= 3; color++) result.AddResource(color, EffParam(row.Params, 2));
                    break;
                // 410 Action Points Converted to Stamina: floor(AP/P1)*P4.
                case 410:
                    int ap = AssetCount(mutation, EffAssetActionPoint, 0);
                    result.AddAsset(EffParam(row.Params, 1), EffParam(row.Params, 2),
                        Math.Max(ap, 0) / Math.Max(1, EffParam(row.Params, 0)) * EffParam(row.Params, 3));
                    break;
                // 425 post-timeback grants only apply when the rollback actually happens.
                default:
                    break;
            }
        }
        return result;
    }

    internal static void ConsumePermanentSettlementBonuses(Mutation mutation, Theatre4SettlementBonuses bonuses)
    {
        if (bonuses.PermanentColorResource.Count == 0) return;
        Theatre4AdventureData adventure = RequireAdventure(mutation);
        List<Theatre4EffectData> changed = [];
        foreach (Theatre4EffectData effect in EnumerateEffects(adventure))
        {
            if (EffectTypeOf(effect) is not (1 or 2)) continue;
            bool effectChanged = false;
            for (int color = 1; color <= 3; color++)
                if (effect.CustomData.GetValueOrDefault(color) != 0)
                {
                    effect.CustomData[color] = 0;
                    effectChanged = true;
                }
            if (effectChanged) changed.Add(effect);
        }
        PushEffects(mutation, changed);
    }

    // 425 (after time rewind) is applied by Map immediately after ApplyTracebackRollback.
    internal static void TriggerTimeback(Mutation mutation)
    {
        Theatre4AdventureData adventure = RequireAdventure(mutation);
        foreach (Theatre4EffectData effect in SnapshotEffects(mutation))
        {
            Theatre4EffectTable? row = EffectConfig(effect.EffectId);
            if (row?.Type != 425) continue;
            AddAsset(mutation, EffParam(row.Params, 0), EffParam(row.Params, 1), EffParam(row.Params, 2));
        }
    }

    //endregion

    //region active skills

    internal readonly record struct EffectCost(int Count, int AssetType, int AssetId);

    // Exact client cost rule (XTheatre4EffectSubControl:GetEffectCostInfo) plus type 102 reductions.
    internal static EffectCost GetEffectCost(Mutation mutation, int effectId)
    {
        Theatre4EffectTable? row = EffectConfig(effectId);
        if (row?.SkillCostType is not int assetType) return new EffectCost(0, 0, 0);
        int useTimes = AllEffects(RequireAdventure(mutation)).Where(effect => effect.EffectId == effectId)
            .Sum(effect => effect.UseTimes);
        List<int> costUp = row.SkillCostUp ?? [];
        int price;
        if (useTimes == 0 || costUp.Count == 0) price = row.SkillCostCount ?? 0;
        else price = costUp[Math.Min(useTimes, costUp.Count) - 1];
        foreach (Theatre4EffectData effect in AllEffects(RequireAdventure(mutation)))
        {
            Theatre4EffectTable? candidate = EffectConfig(effect.EffectId);
            if ((candidate?.Type ?? 0) == 102 && EffParam(candidate!.Params, 0) == effectId)
                price += EffParam(candidate.Params, 1);
        }
        return new EffectCost(Math.Max(0, price), assetType, 0);
    }

    [RequestPacketHandler("Theatre4UseSkillEffectRequest")]
    public static void Theatre4UseSkillEffect(Session session, Packet.Request packet) =>
        Handle<Theatre4UseSkillEffectRequest, Theatre4UseSkillEffectResponse>(session, packet, (m, request, response) =>
        {
            EnsureAvailable(session);
            Theatre4AdventureData adventure = RequireAdventure(m);
            Theatre4EffectTable row = EffectConfig(request.EffectId)
                ?? throw new InvalidDataException($"Unknown Theatre4 effect {request.EffectId}.");
            List<Theatre4EffectData> owned = AllEffects(adventure).Where(effect => effect.EffectId == request.EffectId).ToList();
            Require(owned.Count > 0, 20218120);
            int type = row.Type ?? 0;

            EffectCost cost = GetEffectCost(m, request.EffectId);
            // Type423 charges its single TimeBack through ApplyTracebackRollback (asset 17 maps to
            // adventure.TracebackPoint, which that helper decrements), so it must not pay twice.
            if (type != 423 && cost.Count > 0)
                SpendAsset(m, cost.AssetType, cost.AssetId, cost.Count, 20218152);

            Theatre4GridData? target = null;
            int affectedCount = 1;
            switch (type)
            {
                case 101:
                    target = ApplyCreateBuilding(m, adventure, row, request.Params);
                    break;
                case 414:
                    throw new InvalidDataException("Theatre4 Type414 is a native building selector, not a build skill.");
                case 115:
                    target = ApplyAlterColor(m, adventure, row, request.Params);
                    break;
                case 117:
                    target = ApplyRemoveHurdle(m, adventure, row, request.Params, out affectedCount);
                    break;
                case 201:
                    target = ApplyCreateShop(m, adventure, row, request.Params);
                    break;
                case 423:
                    // Client sends empty Params (XTheatre4Control:2574-2584); the server selects the
                    // rollback day exactly like XTheatre4EffectSubControl:HasEnoughTimeBackData:
                    // Days - current chapter MaxTracebackDays.
                    ApplyTracebackRollback(m, request.EffectId, [TracebackTargetDay(adventure)]);
                    TriggerTimeback(m);
                    return;
                default:
                    throw new InvalidDataException($"Theatre4 effect {request.EffectId} (type {type}) is not an active skill.");
            }
            foreach (Theatre4EffectData effect in owned) effect.UseTimes = checked(effect.UseTimes + 1);
            RebuildEffects(m);
            // 111008 challenge counter: build/alter grid via skill effect (Core RecordMetaProgress).
            RecordMetaProgress(m, "BuildAlter");
            TriggerEffects(m, "build", target, type switch
            {
                101 => EffAlterCreateBuilding,
                115 => EffAlterAlterColor,
                117 => EffAlterRemoveHurdle,
                _ => EffAlterCreateShop
            }, affectedCount: affectedCount);
        });

    // BUILD / Type101: place a building on the selected empty processed grid.
    private static Theatre4GridData ApplyCreateBuilding(Mutation mutation, Theatre4AdventureData adventure,
        Theatre4EffectTable row, List<int> parameters)
    {
        int buildingId = EffParam(row.Params, 0);
        Require(buildingId > 0 && parameters.Count >= 3, 20218121);
        Theatre4GridData grid = ResolveSkillGrid(mutation, parameters);
        Require(grid.Type == EffGridEmpty && grid.State == EffStateProcessed, 20218122);
        Require(BuildingConfigs.Value.ContainsKey(buildingId), 20218123);
        if (BuildingConfigs.Value[buildingId].MaxCountInChapter is int max and > 0)
        {
            int existing = (RequireChapter(mutation, GetMapId(parameters)).Grids)
                .Count(candidate => candidate.Type == EffGridBuilding && candidate.Building?.BuildingId == buildingId);
            Require(existing < max, 20218124);
        }
        grid.Type = EffGridBuilding;
        grid.Building = new Theatre4BuildingData
        {
            BuildingId = buildingId,
            BuildingType = BuildingConfigs.Value[buildingId].Type
        };
        mutation.Push(new NotifyTheatre4ChangeGrids { MapId = GetMapId(parameters), Grids = [grid] });
        return grid;
    }

    // Type115: recolour a grid (plus Type116 extra cross tiles).
    private static Theatre4GridData ApplyAlterColor(Mutation mutation, Theatre4AdventureData adventure,
        Theatre4EffectTable row, List<int> parameters)
    {
        int color = EffParam(row.Params, 0);
        Require(color is >= 1 and <= 3 && parameters.Count >= 3, 20218125);
        Theatre4GridData grid = ResolveSkillGrid(mutation, parameters);
        Require(grid.Type is not (EffGridBoss or EffGridStart or EffGridNothing or EffGridBlank or EffGridBuilding)
            && grid.State != EffStateExplored && grid.State != EffStateProcessed, 20218126);
        Require(grid.Color != color, 20218127);
        int mapId = GetMapId(parameters);
        List<Theatre4GridData> changed = [grid];
        grid.Color = color;
        int extra = AlterColorExtraGrids(mutation);
        if (extra > 0)
            foreach (Theatre4GridData neighbour in CrossRange(mutation, mapId, grid.PosX, grid.PosY, extra))
                if (!ReferenceEquals(neighbour, grid))
                {
                    neighbour.Color = color;
                    changed.Add(neighbour);
                }
        mutation.Push(new NotifyTheatre4ChangeGrids { MapId = mapId, Grids = changed });
        return grid;
    }

    // Type117: clear a hurdle (or a monster when Type120 is owned), plus Type119 extra tiles.
    private static Theatre4GridData ApplyRemoveHurdle(Mutation mutation, Theatre4AdventureData adventure,
        Theatre4EffectTable row, List<int> parameters, out int affectedCount)
    {
        Require(parameters.Count >= 3, 20218128);
        Theatre4GridData grid = ResolveSkillGrid(mutation, parameters);
        bool monsters = RemoveHurdleTargetsMonster(mutation);
        Require(!(grid.State is EffStateUnknown or EffStateVisible) && grid.State != EffStateProcessed, 20218129);
        Require(grid.Type == EffGridHurdle || (monsters && grid.Type == EffGridMonster), 20218130);
        int mapId = GetMapId(parameters);
        List<Theatre4GridData> changed = [grid];
        grid.Type = EffGridEmpty;
        int extra = RemoveHurdleExtraGrids(mutation);
        if (extra > 0)
            foreach (Theatre4GridData neighbour in CrossRange(mutation, mapId, grid.PosX, grid.PosY, 1))
                if (neighbour.Type == EffGridHurdle)
                {
                    neighbour.Type = EffGridEmpty;
                    changed.Add(neighbour);
                }
        affectedCount = changed.Count;
        mutation.Push(new NotifyTheatre4ChangeGrids { MapId = mapId, Grids = changed });
        return grid;
    }

    // Type201: turn a processed empty grid into a shop of the group selected by the skill row.
    private static Theatre4GridData ApplyCreateShop(Mutation mutation, Theatre4AdventureData adventure,
        Theatre4EffectTable row, List<int> parameters)
    {
        int shopGroupId = EffParam(row.Params, 0);
        Require(shopGroupId > 0 && parameters.Count >= 3, 20218131);
        Theatre4GridData grid = ResolveSkillGrid(mutation, parameters);
        Require(grid.Type == EffGridEmpty && grid.State == EffStateProcessed, 20218132);
        Theatre4ShopGroupTable? shopGroup = PickWeighted(mutation,
            Rows<Theatre4ShopGroupTable>().Where(candidate => candidate.ShopGroupId == shopGroupId).ToList(),
            candidate => candidate.Weight);
        Require(shopGroup is not null, 20218131);
        Require(InitializeShopGrid(mutation, grid, shopGroup!), 20218131);
        mutation.Push(new NotifyTheatre4ChangeGrids { MapId = GetMapId(parameters), Grids = [grid] });
        return grid;
    }

    private static Theatre4GridData ResolveSkillGrid(Mutation mutation, List<int> parameters)
    {
        int mapId = GetMapId(parameters);
        // Wire Params is [mapId, posY, posX] (XTheatre4MapSubControl:GetMapBuildParams).
        // Same active-chapter gate as Explore (Map.cs): a passed chapter can never be mutated.
        Theatre4ChapterData chapter = RequireChapter(mutation, mapId);
        Require(!chapter.IsPass, 20218018);
        return FindGrid(mutation, mapId, EffParam(parameters, 2), EffParam(parameters, 1));
    }

    private static int GetMapId(List<int> parameters) => EffParam(parameters, 0);

    // Grid ids are coordinate-derived without a map component (Map GridIdOf), so the per-shop
    // one-shot receipt packs the authored coordinate tuple with the map id. Theatre4Map.tsv bounds
    // are Id <= 70002, SizeX <= 8, SizeY <= 7, so mapId*10000 + PosY*100 + PosX stays in int range
    // (70002*10000 = 700,020,000) and cannot alias across maps.
    private static int ShopEntryKey(Theatre4AdventureData adventure, Theatre4GridData grid)
    {
        if (grid.PosX is < 0 or >= 100 || grid.PosY is < 0 or >= 100)
            throw new InvalidDataException($"Theatre4 grid coordinate {grid.PosX},{grid.PosY} exceeds the authored packing bound.");
        foreach (Theatre4ChapterData chapter in adventure.Chapters)
            if (chapter.Grids.Contains(grid))
                return checked(chapter.MapId * 10000 + grid.PosY * 100 + grid.PosX);
        throw new InvalidDataException($"Theatre4 shop grid {grid.GridId} is not owned by any active chapter.");
    }

    // Client HasEnoughTimeBackData: rollback day = Days - current chapter MaxTracebackDays.
    private static int TracebackTargetDay(Theatre4AdventureData adventure)
    {
        if (adventure.Chapters.Count == 0) throw new ServerCodeException("Theatre4 has no active chapter.", 20218140);
        return adventure.Days - adventure.Chapters[^1].MaxTracebackDays;
    }

    private static int RequireChapterMapId(Theatre4AdventureData adventure) =>
        adventure.Chapters.Count > 0 ? adventure.Chapters[^1].MapId : 0;

    //endregion

    //region reads consumed by Map / Economy / Combat

    internal static int EffectSimpleValue(Mutation mutation, int effectType) =>
        AllEffects(RequireAdventure(mutation)).Where(effect => EffectTypeOf(effect) == effectType)
            .Sum(effect => EffParam(EffectConfig(effect.EffectId)?.Params, 0));

    internal static bool HasEffect(Mutation mutation, int effectType) =>
        AllEffects(RequireAdventure(mutation)).Any(effect => EffectTypeOf(effect) == effectType);

    internal static int EffectSimpleListCount(Mutation mutation, int effectType) =>
        AllEffects(RequireAdventure(mutation)).Count(effect => EffectTypeOf(effect) == effectType);

    internal static int AlterColorExtraGrids(Mutation mutation) => EffectSimpleValue(mutation, 116);

    internal static int RemoveHurdleExtraGrids(Mutation mutation) => EffectSimpleValue(mutation, 119);

    internal static bool RemoveHurdleTargetsMonster(Mutation mutation) => HasEffect(mutation, 120);

    internal static bool ShopRefreshAvailable(Mutation mutation) => HasEffect(mutation, 204);

    internal static bool PacifyAvailable(Mutation mutation) => HasEffect(mutation, 205);

    internal static bool FirstPurchaseFree(Mutation mutation) => HasEffect(mutation, 202);

    internal static bool RandomFreeShopSlot(Mutation mutation) =>
        AllEffects(RequireAdventure(mutation)).Any(effect =>
            EffectTypeOf(effect) == 26 && !effect.CustomData.ContainsKey(AppliedMarkerKey));

    internal static int RecruitAttemptBonus(Mutation mutation) => EffectSimpleValue(mutation, 23);

    internal static int DailyActionPointBonus(Mutation mutation)
    {
        Theatre4AdventureData adventure = RequireAdventure(mutation);
        int bonus = EffectSimpleValue(mutation, 24);
        foreach (Theatre4GridData grid in adventure.Chapters.LastOrDefault()?.Grids ?? [])
            if (grid.Type == EffGridBuilding && grid.Building?.BuildingType == 4
                && BuildingConfigs.Value.TryGetValue(grid.Building.BuildingId, out Theatre4BuildingTable? row))
                bonus += EffParam(row.Params, 0);
        return bonus;
    }

    internal static int TalentRefreshExtraCost(Mutation mutation) => EffectSimpleValue(mutation, 301);

    internal static int FreeTalentRefreshCount(Mutation mutation) => EffectSimpleValue(mutation, 302);

    // 206: per-mille discount; client returns (discount+10000)/10000, with 0 meaning no discount.
    internal static int SweepDiscountRatio(Mutation mutation) => EffectSimpleValue(mutation, 206);

    internal static bool RedBuyDeadAvailable(Mutation mutation) => HasEffect(mutation, 422);


    internal static bool TimebackAvailable(Mutation mutation) => HasEffect(mutation, 423);

    internal static bool AwakeningAvailable(Mutation mutation) => HasEffect(mutation, 424);

    // 203: shop discount ratio in per-mille (client applies it to goods prices).
    internal static int ShopDiscountRatio(Mutation mutation) => EffectSimpleValue(mutation, 203);

    // 409: event-option asset change (Add/Sub/Multiply/Division, ratio 10000), mirroring
    // XTheatre4EffectSubControl:GetEffect409ChangeAssetCount exactly.
    internal static bool ApplyChangeAsset(Mutation mutation, int effectId)
    {
        Theatre4EffectTable? row = EffectConfig(effectId);
        if (row?.Type != 409) throw new InvalidDataException($"Theatre4 effect {effectId} is not a change-asset effect.");
        int option = EffParam(row.Params, 0);
        int assetType = EffParam(row.Params, 3);
        int assetId = EffParam(row.Params, 4);
        int current = AssetCount(mutation, assetType, assetId);
        int updated = option switch
        {
            1 => current + EffParam(row.Params, 1),
            2 => current - EffParam(row.Params, 1),
            3 => (int)Math.Floor(current * (long)EffParam(row.Params, 2) / 10000.0),
            4 when EffParam(row.Params, 2) > 0 => (int)Math.Floor(current / (EffParam(row.Params, 2) / 10000.0)),
            _ => current
        };
        updated = Math.Max(updated, 0);
        ApplyAssetDelta(mutation, assetType, assetId, updated - current);
        return true;
    }

    // 411: authored fight restriction. Returns forbidType 1 (sweep allowed) or 2 (sweep forbidden).
    internal static bool FightForbidden(Mutation mutation, int fightId, int mapId, out int forbidType)
    {
        forbidType = 0;
        int fightType = Rows<Theatre4FightTable>().FirstOrDefault(row => row.Id == fightId)?.FightType ?? 0;
        if (fightType <= 0 || mapId <= 0) return false;
        foreach (Theatre4EffectData effect in AllEffects(RequireAdventure(mutation)))
        {
            Theatre4EffectTable? row = EffectConfig(effect.EffectId);
            if (row?.Type != 411) continue;
            List<int> parameters = row.Params ?? [];
            if (parameters.Count < 3 || parameters[0] != fightType) continue;
            if (!parameters.Skip(2).Contains(mapId)) continue;
            forbidType = EffParam(parameters, 1);
            return true;
        }
        return false;
    }

    // 1302 punish group: sum of Type407 Params[1] for the fight's PunishEffectGroup.
    internal static int PunishHp(Mutation mutation, int fightId)
    {
        int groupId = Rows<Theatre4FightTable>().FirstOrDefault(row => row.Id == fightId)?.PunishEffectGroup ?? 0;
        if (groupId <= 0) return 0;
        int hp = 0;
        foreach (int effectId in EffectGroupEffects(groupId))
            if ((EffectConfig(effectId)?.Type ?? 0) == 407) hp += EffParam(EffectConfig(effectId)?.Params, 0);
        return hp;
    }

    // 416: awakening gain bonus in per-mille (Params[2] is the ratio denominator base).
    internal static int AwakeningBonusRatio(Mutation mutation) => EffectSimpleValue(mutation, 416);

    //endregion

    //region combat projection

    // One explicit projection for TundraCombat, with the exact shape Combat requested.
    // Effects never mutate battle state; the run's combat-affecting effects are exposed as ids.
    internal sealed class Theatre4CombatProjection
    {
        public List<int> PlayerEventIds { get; init; } = [];
        public List<int> GlobalEventIds { get; init; } = [];
        public List<(int FightEventId, int Level)> LeveledEvents { get; init; } = [];
        public Dictionary<int, int> PlayerMagicIds { get; init; } = [];
        public Dictionary<int, int> PlayerBaseAttrs { get; init; } = [];
        public Dictionary<int, int> MonsterMagicIds { get; init; } = [];
        public bool SweepEnabled { get; init; }
        public bool RedBuyDeathEnabled { get; init; }
        public int SweepDiscountBp { get; init; } = 10000;
        public int SweepYellowHpBasisPoints { get; init; } = 10000;
    }

    // fightId, when supplied, folds that fight's authored PunishEffectGroup into the projection.
    internal static Theatre4CombatProjection ProjectCombat(Mutation mutation, int fightId = 0)
    {
        Theatre4AdventureData adventure = RequireAdventure(mutation);
        List<int> effectIds = EnumerateEffects(adventure).Select(effect => effect.EffectId).ToList();
        // Difficulty effect group (authored as a direct effect-id list) and the selected affix.
        if (Rows<Theatre4DifficultyTable>().FirstOrDefault(row => row.Id == adventure.Difficulty)?.EffectGroup is { } difficulty)
            effectIds.AddRange(difficulty);
        if (Rows<Theatre4AffixTable>().FirstOrDefault(row => row.Id == adventure.Affix)?.EffectGroupId is int affixGroup and > 0)
            effectIds.AddRange(EffectGroupEffects(affixGroup));
        if (fightId > 0
            && Rows<Theatre4FightTable>().FirstOrDefault(row => row.Id == fightId)?.PunishEffectGroup is int punishGroup and > 0)
            effectIds.AddRange(EffectGroupEffects(punishGroup));

        List<int> playerEvents = [];
        List<int> globalEvents = [];
        Dictionary<int, int> playerMagic = [];
        int sweepDiscount = 0;
        int? sweepYellowHp = null;
        bool redBuyDeath = false;
        bool sweepEnabled = false;
        foreach (int effectId in effectIds.Distinct())
        {
            Theatre4EffectTable? row = EffectConfig(effectId);
            if (row is null) continue;
            int type = row.Type ?? 0;
            if (type == 0)
            {
                // Zero-Type rows author a FightEvent.Id in Params[0] (share/fight/FightEvent.json,
                // not imported). Relay the client-native event id; never reinterpret it as magic.
                int fightEvent = EffParam(row.Params, 0);
                if (fightEvent > 0) playerEvents.Add(fightEvent);
                continue;
            }
            switch (type)
            {
                case 33:
                    // Type33 accumulates per-tile FightEvent ids while exploring; project the
                    // accumulated values, not the static config params.
                    foreach (Theatre4EffectData instance in EnumerateEffects(adventure).Where(candidate => candidate.EffectId == effectId))
                        foreach (int accumulated in instance.CustomData.Values)
                            if (accumulated > 0) playerEvents.Add(accumulated);
                    break;
                case 205: sweepEnabled = true; break;
                case 206: sweepDiscount += EffParam(row.Params, 0); break;
                case 421 when sweepYellowHp is null && EffParam(row.Params, 0) == SweepYellow:
                    sweepYellowHp = Math.Clamp(EffParam(row.Params, 1), 0, 10000);
                    break;
                case 422: redBuyDeath = true; break;
            }
        }
        return new Theatre4CombatProjection
        {
            PlayerEventIds = playerEvents.Distinct().ToList(),
            GlobalEventIds = globalEvents.Distinct().ToList(),
            LeveledEvents = [],
            PlayerMagicIds = playerMagic,
            PlayerBaseAttrs = [],
            MonsterMagicIds = [],
            SweepEnabled = sweepEnabled,
            RedBuyDeathEnabled = redBuyDeath,
            SweepDiscountBp = 10000 + sweepDiscount,
            SweepYellowHpBasisPoints = sweepYellowHp ?? 10000,
        };
    }

    //endregion

    //region map marks / spawns

    private static bool MarkTiles(Mutation mutation, Theatre4AdventureData adventure, Theatre4EffectTable row, bool open)
    {
        int mark = EffParam(row.Params, 0);
        if (mark <= 0) return false;
        List<Theatre4GridData> changed = [];
        foreach (Theatre4ChapterData chapter in adventure.Chapters)
            foreach (Theatre4GridData grid in chapter.Grids)
            {
                if (grid.ContentGroup != mark) continue;
                if (open && grid.State is EffStateUnknown or EffStateVisible)
                {
                    grid.State = EffStateDiscover;
                    changed.Add(grid);
                }
                else if (!open && grid.State != EffStateProcessed && grid.State != EffStateUnknown)
                {
                    grid.State = EffStateUnknown;
                    changed.Add(grid);
                }
            }
        if (changed.Count > 0 && adventure.Chapters.Count > 0)
            mutation.Push(new NotifyTheatre4ChangeGrids { MapId = adventure.Chapters[^1].MapId, Grids = changed });
        return changed.Count > 0;
    }

    private static bool GenerateMarkTiles(Mutation mutation, Theatre4AdventureData adventure, Theatre4EffectTable row)
    {
        int mark = EffParam(row.Params, 0);
        int gridType = EffParam(row.Params, 1);
        int contentId = EffParam(row.Params, 2);
        if (mark <= 0 || gridType <= 0) return false;
        List<Theatre4GridData> changed = [];
        foreach (Theatre4ChapterData chapter in adventure.Chapters)
            foreach (Theatre4GridData grid in chapter.Grids)
            {
                if (grid.ContentGroup != mark || grid.Type != EffGridNothing) continue;
                grid.Type = gridType;
                grid.ContentId = contentId;
                changed.Add(grid);
            }
        if (changed.Count > 0 && adventure.Chapters.Count > 0)
            mutation.Push(new NotifyTheatre4ChangeGrids { MapId = adventure.Chapters[^1].MapId, Grids = changed });
        return changed.Count > 0;
    }

    // LOCAL POLICY: 215 spawns one Treasure Thief on an unused unknown floor tile of
    // the newest map, resolving its authored fight group through the normal map pipeline.
    private static bool SpawnTreasureThief(Mutation mutation, Theatre4AdventureData adventure, Theatre4EffectTable row)
    {
        List<int> fightGroups = row.Params ?? [];
        if (fightGroups.Count == 0 || adventure.Chapters.Count == 0) return false;
        Theatre4ChapterData chapter = adventure.Chapters[^1];
        HashSet<int> blocked = ClosedHiddenCells(mutation, chapter);
        List<Theatre4GridData> candidates = chapter.Grids
            .Where(grid => grid.Type == EffGridEmpty && grid.State == EffStateUnknown
                && !blocked.Contains(KeyOf(grid.PosX, grid.PosY))
                && grid.ContentGroup == 0 && grid.ContentId == 0
                && grid.Fight is null && grid.Shop is null && grid.Event is null
                && grid.Building is null).ToList();
        if (candidates.Count == 0) return false;
        Theatre4GridData target = candidates[RandomIndex(mutation, candidates.Count)];
        int groupId = fightGroups[RandomIndex(mutation, fightGroups.Count)];
        Theatre4FightData fight = BuildMapFight(mutation, groupId, adventure.Chapters.Count - 1,
            boss: false, out int fightId, expectedFightType: 4)
            ?? throw new ServerCodeException("Theatre4 treasure encounter is not configured.", EncounterConflictError);
        target.Type = EffGridMonster;
        target.ContentGroup = groupId;
        target.ContentId = fightId;
        target.Fight = fight;
        target.State = EffStateUnknown;
        mutation.Push(new NotifyTheatre4ChangeGrids { MapId = chapter.MapId, Grids = [target] });
        return true;
    }

    //endregion

    //region shared helpers

    private static bool GrantAsset(Mutation mutation, Theatre4EffectTable row) =>
        AddAssetIfCount(mutation, EffParam(row.Params, 0), EffParam(row.Params, 1), EffParam(row.Params, 2));

    private static bool AddAssetIfCount(Mutation mutation, int assetType, int assetId, int count)
    {
        if (assetType <= 0 || count == 0) return false;
        AddAsset(mutation, assetType, assetId, count);
        return true;
    }

    // 412 "adjust current asset to a value": expressed as the delta from the current count so it
    // uses the same economy path as every other asset change.
    private static bool SetAsset(Mutation mutation, Theatre4EffectTable row)
    {
        int assetType = EffParam(row.Params, 0);
        int assetId = EffParam(row.Params, 1);
        if (assetType <= 0) return false;
        int target = EffParam(row.Params, 2);
        ApplyAssetDelta(mutation, assetType, assetId, target - AssetCount(mutation, assetType, assetId));
        return true;
    }

    // Applies a signed asset delta. Economy.AddAsset rejects negatives, so a debit is clamped to
    // the available balance and charged through SpendAsset (SetAsset to a lower value, 409 Sub/
    // Division, the negative 31/415 payouts). Returns false only when nothing could be applied.
    private static bool ApplyAssetDelta(Mutation mutation, int assetType, int assetId, int delta)
    {
        if (assetType <= 0) return false;
        if (delta == 0) return true;
        if (delta > 0)
        {
            AddAsset(mutation, assetType, assetId, delta);
            return true;
        }
        int debit = Math.Min(AssetCount(mutation, assetType, assetId), -delta);
        if (debit > 0) SpendAsset(mutation, assetType, assetId, debit, 20218152);
        return true;
    }

    private static int EffectColor(Theatre4AdventureData adventure, Theatre4GridData? grid)
    {
        if (grid is { Color: > 0 }) return grid.Color;
        return 1 + (adventure.Days + adventure.ExploreCount) % 3;
    }

    private static bool IsAdjacentToPrevious(Theatre4AdventureData adventure, Theatre4GridData grid)
    {
        if (adventure.PreExplorePos is not { } previous) return false;
        return Math.Abs(previous.PosX - grid.PosX) <= 1 && Math.Abs(previous.PosY - grid.PosY) <= 1
            && previous.MapId == RequireChapterMapId(adventure);
    }

    private static IEnumerable<int> DistinctDailyColors(Theatre4AdventureData adventure) =>
        adventure.DailyExploreColors.Where(color => color is >= 1 and <= 3).Distinct();

    private static int DailyColorCount(Theatre4AdventureData adventure, int color) =>
        adventure.DailyExploreColors.Count(candidate => candidate == color);

    // Straight / corner detection over the day's explored positions (DailyExplorePosSet).
    // LOCAL POLICY: "includes the shape" means a 3-cell straight line or a 3-cell right angle.
    private static bool DailyShapeSatisfied(Theatre4AdventureData adventure, bool straight)
    {
        HashSet<(int MapId, int X, int Y)> cells = adventure.DailyExplorePosSet
            .Select(pos => (pos.MapId, pos.PosX, pos.PosY)).ToHashSet();
        if (cells.Count < 3) return false;
        if (straight)
        {
            foreach ((int mapId, int x, int y) in cells)
            {
                if (cells.Contains((mapId, x + 1, y)) && cells.Contains((mapId, x + 2, y))) return true;
                if (cells.Contains((mapId, x, y + 1)) && cells.Contains((mapId, x, y + 2))) return true;
                if (cells.Contains((mapId, x + 1, y + 1)) && cells.Contains((mapId, x + 2, y + 2))) return true;
                if (cells.Contains((mapId, x - 1, y + 1)) && cells.Contains((mapId, x - 2, y + 2))) return true;
            }
            return false;
        }
        foreach ((int mapId, int x, int y) in cells)
        {
            bool up = cells.Contains((mapId, x, y - 1)), down = cells.Contains((mapId, x, y + 1));
            bool left = cells.Contains((mapId, x - 1, y)), right = cells.Contains((mapId, x + 1, y));
            if ((up || down) && (left || right)) return true;
            if (cells.Contains((mapId, x + 1, y + 1))
                && (cells.Contains((mapId, x + 1, y)) || cells.Contains((mapId, x, y + 1)))) return true;
            if (cells.Contains((mapId, x - 1, y + 1))
                && (cells.Contains((mapId, x - 1, y)) || cells.Contains((mapId, x, y + 1)))) return true;
        }
        return false;
    }

    // Building range geometry, transcribed from XTheatre4MapSubControl.
    private static IEnumerable<Theatre4GridData> BuildingRange(Mutation mutation,
        Theatre4ChapterData chapter, Theatre4GridData building) =>
        building.Building?.BuildingType switch
        {
            1 => DiamondRange(mutation, chapter.MapId, building.PosX, building.PosY),
            2 or 5 => CrossRange(mutation, chapter.MapId, building.PosX, building.PosY, 1),
            3 => CubeRange(mutation, chapter.MapId, building.PosX, building.PosY),
            _ => []
        };

    private static bool InBuildingRange(Mutation mutation, Theatre4GridData target, int buildingType)
    {
        Theatre4AdventureData adventure = RequireAdventure(mutation);
        Theatre4ChapterData? chapter = ChapterForGrid(adventure, target);
        if (chapter is null) return false;
        return chapter.Grids.Where(grid => grid.Type == EffGridBuilding
                && grid.Building?.BuildingType == buildingType)
            .Any(building => BuildingRange(mutation, chapter, building)
                .Any(candidate => candidate.GridId == target.GridId));
    }

    private static bool InAnyBuildingRange(Mutation mutation, Theatre4GridData grid)
    {
        Theatre4AdventureData adventure = RequireAdventure(mutation);
        Theatre4ChapterData? chapter = ChapterForGrid(adventure, grid);
        if (chapter is null) return false;
        return chapter.Grids.Where(candidate => candidate.Type == EffGridBuilding)
            .Any(building => BuildingRange(mutation, chapter, building)
                .Any(candidate => candidate.GridId == grid.GridId));
    }

    private static Theatre4ChapterData? ChapterForGrid(Theatre4AdventureData adventure, Theatre4GridData grid) =>
        adventure.Chapters.FirstOrDefault(chapter => chapter.Grids.Any(candidate =>
            ReferenceEquals(candidate, grid)
            || (candidate.GridId == grid.GridId && candidate.PosX == grid.PosX && candidate.PosY == grid.PosY)));

    private static int CurrentChapterBuildingCount(Theatre4AdventureData adventure) =>
        adventure.Chapters.LastOrDefault()?.Grids.Count(grid => grid.Type == EffGridBuilding) ?? 0;

    private static int BuildingReceiptKey(Theatre4AdventureData adventure, Theatre4GridData grid)
    {
        Theatre4ChapterData chapter = ChapterForGrid(adventure, grid)
            ?? throw new InvalidDataException($"Theatre4 building grid {grid.GridId} is outside the run.");
        int index = adventure.Chapters.IndexOf(chapter);
        return checked(index * 10000 + grid.GridId);
    }


    private static void ApplyBuildingExploreResource(Mutation mutation, Theatre4GridData target)
    {
        Theatre4AdventureData adventure = RequireAdventure(mutation);
        Theatre4ChapterData? chapter = ChapterForGrid(adventure, target);
        if (chapter is null || target.State < EffStateExplored) return;
        foreach (Theatre4GridData building in chapter.Grids.Where(grid =>
            grid.Type == EffGridBuilding && grid.Building is not null))
        {
            int buildingType = building.Building!.BuildingType;
            if (buildingType is not (3 or 5)
                || !BuildingRange(mutation, chapter, building).Any(grid => grid.GridId == target.GridId))
                continue;
            Theatre4BuildingTable? config = BuildingConfigs.Value.GetValueOrDefault(building.Building.BuildingId);
            int amount = config is null ? 0 : EffParam(config.Params, 0);
            if (amount <= 0 || target.Color <= 0) continue;
            AddAsset(mutation, EffAssetColorResource, target.Color, amount);
        }
    }

    private static Theatre4GridData? CombatGrid(Mutation mutation)
    {
        Theatre4ActiveEncounter? encounter = mutation.State.ActiveEncounter;
        return encounter is { MapId: > 0 }
            ? TryFindGrid(mutation, encounter.MapId, encounter.PosX, encounter.PosY)
            : null;
    }

    private static bool RevealBuildingTarget(Mutation mutation, Theatre4GridData target)
    {
        if (target.Type == EffGridNothing || target.State != EffStateUnknown) return false;
        Theatre4ChapterData? chapter = RequireAdventure(mutation).Chapters
            .FirstOrDefault(candidate => candidate.Grids.Any(grid => ReferenceEquals(grid, target)));
        if (chapter is null || ClosedHiddenCells(mutation, chapter).Contains(KeyOf(target.PosX, target.PosY)))
            return false;
        target.State = EffStateDiscover;
        mutation.Push(new NotifyTheatre4ChangeGrids { MapId = chapter.MapId, Grids = [Clone(target)] });
        return true;
    }

    private static void PushEffects(Mutation mutation, IEnumerable<Theatre4EffectData> effects)
    {
        List<Theatre4EffectData> list = effects.Distinct().ToList();
        if (list.Count > 0) mutation.Push(new NotifyTheatre4EffectsChange { Effects = list });
    }

    //region constants

    private const int EffGridNothing = 1;
    private const int EffGridEmpty = 2;
    private const int EffGridHurdle = 3;
    private const int EffGridShop = 4;
    private const int EffGridBox = 5;
    private const int EffGridMonster = 6;
    private const int EffGridBoss = 7;
    private const int EffGridStart = 9;
    private const int EffGridBlank = 10;
    private const int EffGridBuilding = 11;

    private const int EffStateUnknown = 0;
    private const int EffStateVisible = 1;
    private const int EffStateDiscover = 2;
    private const int EffStateExplored = 3;
    private const int EffStateProcessed = 4;

    private const int EffAlterCreateBuilding = 1;
    private const int EffAlterAlterColor = 2;
    private const int EffAlterRemoveHurdle = 3;
    private const int EffAlterCreateShop = 4;

    private const int EffAssetItemBox = 1;
    private const int EffAssetColorLevel = 7;
    private const int EffAssetColorResource = 8;
    private const int EffAssetBuildPoint = 10;
    private const int EffAssetActionPoint = 11;
    private const int EffAssetColorDailyResource = 12;
    private const int EffAssetItemLimit = 13;
    private const int EffAssetColorPoint = 9;
    private const int EffAssetGold = 4;

    //endregion

    //endregion
}

// Settlement tally produced by ComputeSettlementBonuses and consumed by Map/EndAdventure.
internal sealed class Theatre4SettlementBonuses
{
    public Dictionary<int, int> ColorResource { get; } = [];
    public Dictionary<int, int> ColorLevel { get; } = [];
    public Dictionary<int, int> PermanentColorResource { get; } = [];
    public List<Theatre4AssetData> Assets { get; } = [];
    private int? AbsoluteMarkupRate { get; set; }
    private int AdditiveMarkupRate { get; set; }
    public double ColorExtra => ((AbsoluteMarkupRate ?? 10000) + AdditiveMarkupRate) / 10000d;

    public void AddResource(int color, int amount)
    {
        if (amount != 0) ColorResource[color] = checked(ColorResource.GetValueOrDefault(color) + amount);
    }

    public void AddPermanentResource(int color, int amount)
    {
        if (amount != 0)
            PermanentColorResource[color] = checked(PermanentColorResource.GetValueOrDefault(color) + amount);
    }

    public void AddLevel(int color, int amount)
    {
        if (amount != 0) ColorLevel[color] = checked(ColorLevel.GetValueOrDefault(color) + amount);
    }

    public void AddMarkup(int basisPoints) =>
        AdditiveMarkupRate = checked(AdditiveMarkupRate + basisPoints);

    public void AddAbsoluteMarkup(int basisPoints) =>
        AbsoluteMarkupRate = checked((AbsoluteMarkupRate ?? 0) + basisPoints);

    public void AddAsset(int assetType, int assetId, int count)
    {
        if (assetType == Theatre4Module.T4Asset.ColorLevel)
            AddLevel(assetId, count);
        else if (assetType == Theatre4Module.T4Asset.ColorResource)
            AddResource(assetId, count);
        else if (assetType > 0 && count != 0)
            Assets.Add(new Theatre4AssetData { Type = assetType, Id = assetId, Num = count });
    }
}
