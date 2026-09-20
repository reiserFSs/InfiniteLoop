using AscNet.Common.Util;
using AscNet.Common.MsgPack;
using MessagePack;
using AscNet.Table.V2.share.theatre4;

namespace AscNet.Test;

internal partial class Program
{
    // Source-backed Type413/415 proof. The only fixture edits are colour points, gold, and one
    // processed Empty tile so Type415 has an authored current-map placement target. Authored
    // talent 232 unlocks a shop tile and Type217 pays for entering a new shop; processed-Shop
    // materialization and its shared creation-entry trigger are AscNet policy, so only Type217
    // effects owned before selection count.
    private static void ValidateTheatre4TalentAuditCompatibility()
    {
        ValidateTheatre4BattlePowerLeapCompatibility();
        ValidateTheatre4DivinePowerCompatibility();
        ValidateTheatre4ColorTalentSnapshotCompatibility();
    }
    // Consumer-shaped regression: NotifyTheatre4ColorTalentData replaces, rather than merges, the
    // native colour cache. Authored unlock rows keep this probe independent of captured values.
    private static void ValidateTheatre4ColorTalentSnapshotCompatibility()
    {
        using Theatre4Case test = new("talent-color-snapshot");
        test.StartRun(1);

        int[] colorIds = test.Adventure.Colors.Select(color => color.Color).ToArray();
        AssertEqual(3, colorIds.Length, "Colour snapshot starts with three colours");
        AssertEqual(3, colorIds.Distinct().Count(), "Colour snapshot starts with distinct colours");
        List<Theatre4ColorTalentSlotTable> authoredSlots = TableReaderV2.Parse<Theatre4ColorTalentSlotTable>()
            .Where(row => colorIds.Contains(row.Color) && row.Level > 0 && row.UnlockPoint is > 0)
            .ToList();
        List<IGrouping<int, Theatre4ColorTalentSlotTable>> groups = authoredSlots
            .GroupBy(row => row.Color).OrderBy(group => group.Min(row => row.Level)).ToList();
        IGrouping<int, Theatre4ColorTalentSlotTable> firstGroup = groups.FirstOrDefault()
            ?? throw new InvalidDataException("Colour snapshot has no authored offer row.");
        IGrouping<int, Theatre4ColorTalentSlotTable> secondGroup = groups
            .FirstOrDefault(group => group.Key != firstGroup.Key)
            ?? throw new InvalidDataException("Colour snapshot has no independent authored offer row.");
        int untouchedColor = colorIds.First(color => color != firstGroup.Key && color != secondGroup.Key);
        Theatre4ColorTalentSlotTable firstSlot = firstGroup.OrderBy(row => row.Level).First();
        Theatre4ColorTalentSlotTable secondSlot = secondGroup.OrderBy(row => row.Level).First();

        void PrimeOffer(int colorId, Theatre4ColorTalentSlotTable slot)
        {
            Theatre4ColorTalentData state = test.Adventure.Colors.Single(color => color.Color == colorId);
            state.Point = (slot.UnlockPoint ?? 0) - 1;
            state.Resource = 0;
            state.DailyResource = 1;
        }

        PrimeOffer(firstGroup.Key, firstSlot);
        test.SaveFixture();
        test.Call(nameof(Theatre4DailySettleRequest));
        Dictionary<int, Theatre4ColorTalentData> cache = ReadColorTalentCache(test,
            "Colour offer notification");
        Theatre4ColorTalentData first = test.Adventure.Colors.Single(color => color.Color == firstGroup.Key);
        AssertEqual(firstSlot.Id, first.WaitSlot!.SlotId, "Colour offer uses the authored first slot");
        AssertEqual(true, cache[firstGroup.Key].WaitSlot is not null,
            "Colour replacement cache retains the offered colour");
        AssertEqual(true, cache.ContainsKey(secondGroup.Key) && cache.ContainsKey(untouchedColor),
            "Colour replacement cache retains untouched colours");

        test.SetGold(1_000_000);
        test.Call(nameof(Theatre4RefreshTalentRequest),
            new Theatre4RefreshTalentRequest { Color = firstGroup.Key });
        cache = ReadColorTalentCache(test, "Colour refresh notification");
        first = test.Adventure.Colors.Single(color => color.Color == firstGroup.Key);
        Theatre4ColorTalentWaitSlotData refreshed = first.WaitSlot
            ?? throw new InvalidDataException("Colour refresh removed the pending offer.");
        AssertEqual(firstSlot.Id, refreshed.SlotId, "Colour refresh retains the authored slot");
        AssertEqual(true, cache[firstGroup.Key].WaitSlot!.TalentIds.SequenceEqual(refreshed.TalentIds),
            "Colour replacement cache receives the refreshed offer");

        int selectedTalent = refreshed.TalentIds.FirstOrDefault();
        if (selectedTalent <= 0) throw new InvalidDataException("Colour refresh returned an empty offer.");
        test.Call(nameof(Theatre4SelectTalentRequest),
            new Theatre4SelectTalentRequest { Color = firstGroup.Key, TalentId = selectedTalent });
        cache = ReadColorTalentCache(test, "Colour selection notification");
        AssertEqual(true, cache[firstGroup.Key].Slots.SelectMany(slot => slot.Talents)
                .Any(talent => talent.TalentId == selectedTalent),
            "Colour replacement cache retains the selected talent");

        Theatre4ColorTalentData firstAfterSelection = test.Adventure.Colors
            .Single(color => color.Color == firstGroup.Key);
        Theatre4ColorTalentData independent = test.Adventure.Colors
            .Single(color => color.Color == secondGroup.Key);
        firstAfterSelection.DailyResource = 0;
        independent.Point = (secondSlot.UnlockPoint ?? 0) - 1;
        independent.Resource = 0;
        independent.DailyResource = 1;
        test.SaveFixture();
        test.Call(nameof(Theatre4DailySettleRequest));
        cache = ReadColorTalentCache(test, "Independent colour delta notification");
        AssertEqual((secondSlot.UnlockPoint ?? 0), cache[secondGroup.Key].Point,
            "Independent colour delta advances only its authored point");
        AssertEqual(true, cache[firstGroup.Key].Slots.SelectMany(slot => slot.Talents)
                .Any(talent => talent.TalentId == selectedTalent),
            "Independent colour delta preserves the selected colour");
        AssertEqual(true, cache[secondGroup.Key].WaitSlot is not null,
            "Independent colour delta opens its authored offer");
        AssertEqual(true, cache.ContainsKey(untouchedColor),
            "Independent colour delta preserves the third colour");
    }
    private static Dictionary<int, Theatre4ColorTalentData> ReadColorTalentCache(Theatre4Case test, string name)
    {
        var push = test.Pushes.LastOrDefault(candidate =>
            candidate.Name == nameof(NotifyTheatre4ColorTalentData))
            ?? throw new InvalidDataException($"{name}: missing colour talent notification.");
        NotifyTheatre4ColorTalentData update =
            MessagePackSerializer.Deserialize<NotifyTheatre4ColorTalentData>(push.Content);
        AssertEqual(3, update.ColorTalents.Count, $"{name}: full colour snapshot count");
        AssertEqual(3, update.ColorTalents.Select(color => color.Color).Distinct().Count(),
            $"{name}: full colour snapshot identity");
        return update.ColorTalents.ToDictionary(color => color.Color);
    }

    private static void ValidateTheatre4BattlePowerLeapCompatibility()
    {
        using Theatre4Case test = new("talent-413");
        test.StartRun(1);

        List<Theatre4EffectTable> effects = TableReaderV2.Parse<Theatre4EffectTable>();
        List<Theatre4EffectGroupTable> effectGroups = TableReaderV2.Parse<Theatre4EffectGroupTable>();
        List<Theatre4ColorTalentTable> talents = TableReaderV2.Parse<Theatre4ColorTalentTable>();
        List<Theatre4ColorTalentPoolTable> pools = TableReaderV2.Parse<Theatre4ColorTalentPoolTable>();
        List<Theatre4ColorTalentSlotTable> slots = TableReaderV2.Parse<Theatre4ColorTalentSlotTable>();
        Theatre4EffectTable leap = effects.Single(row => row.Type == 413);
        if (leap.Params is not { Count: >= 2 } leapParams)
            throw new InvalidDataException("Type413 effect is missing its authored parameters.");
        int leapColor = leapParams[0];
        HashSet<int> leapEffectGroups = effectGroups
            .Where(group => group.Effects is { } ids && ids.Contains(leap.Id))
            .Select(group => group.Id).ToHashSet();
        Theatre4ColorTalentTable ownerTalent = talents
            .Single(talent => leapEffectGroups.Contains(talent.EffectGroupId));
        HashSet<int> ownerPoolGroups = pools.Where(pool => pool.TalentId == ownerTalent.Id)
            .Select(pool => pool.Group).ToHashSet();
        List<Theatre4ColorTalentSlotTable> redSlots = slots
            .Where(slot => slot.Color == leapColor && slot.Level > 0 && slot.UnlockPoint is > 0)
            .OrderBy(slot => slot.Level).ToList();
        Theatre4ColorTalentSlotTable ownerSlot = redSlots
            .Single(slot => ownerPoolGroups.Contains(slot.GeneratePoolGroup));
        List<Theatre4ColorTalentSlotTable> bonusSlots = redSlots
            .Where(slot => slot.GenerateType == 2
                && !ownerPoolGroups.Contains(slot.GeneratePoolGroup)).ToList();
        int totalPoints = redSlots.Max(slot => slot.UnlockPoint ?? 0);
        Theatre4AddAsset(test, T4AssetColorPoint, leapColor, totalPoints);
        test.SetGold(1_000_000);

        Theatre4ColorTalentData red = test.Adventure.Colors.Single(color => color.Color == leapColor);
        HashSet<int> preLeapTalents = [];
        foreach (Theatre4ColorTalentSlotTable slot in redSlots)
        {
            Theatre4ColorTalentWaitSlotData wait = red.WaitSlot
                ?? throw new InvalidDataException("Type413 fixture lost the next authored talent offer.");
            AssertEqual(slot.Id, wait.SlotId, "Type413 follows authored red slot order");
            int offered = wait.TalentIds.FirstOrDefault();
            if (offered <= 0) throw new InvalidDataException("Type413 fixture has an empty talent offer.");
            test.Call(nameof(Theatre4SelectTalentRequest),
                new Theatre4SelectTalentRequest { Color = red.Color, TalentId = offered });
            red = test.Adventure.Colors.Single(color => color.Color == leap.Params[0]);
            if (slot.Id != ownerSlot.Id)
                foreach (Theatre4TalentData talent in red.Slots.Single(data => data.SlotId == slot.Id).Talents)
                    preLeapTalents.Add(talent.TalentId);
        }

        red = test.Adventure.Colors.Single(color => color.Color == leap.Params[0]);
        Theatre4ColorTalentWaitSlotData firstBonus = red.WaitSlot
            ?? throw new InvalidDataException("Type413 did not open a retroactive bonus offer.");
        AssertEqual(bonusSlots[0].Id, firstBonus.SlotId,
            "Type413 starts retroactive offers at the first authored eligible major slot");
        AssertEqual(3, firstBonus.TalentIds.Count,
            "Type413 keeps the authored three-card exclusive offer shape");
        AssertEqual(true, preLeapTalents.IsSubsetOf(
            red.Slots.SelectMany(slot => slot.Talents).Select(talent => talent.TalentId)),
            "Type413 preserves every previously selected talent");

        // Refresh changes only the pending offer budget; a same-transport retry cannot reroll it.
        int pendingBudget = firstBonus.RefreshFreeTimes + firstBonus.RefreshTimes;
        test.Call(nameof(Theatre4RefreshTalentRequest),
            new Theatre4RefreshTalentRequest { Color = red.Color }, transportId: 41_301);
        red = test.Adventure.Colors.Single(color => color.Color == leap.Params[0]);
        Theatre4ColorTalentWaitSlotData refreshed = red.WaitSlot
            ?? throw new InvalidDataException("Type413 refresh removed the pending offer.");
        AssertEqual(firstBonus.SlotId, refreshed.SlotId, "Type413 refresh retains the authored slot identity");
        AssertEqual(pendingBudget - 1, refreshed.RefreshFreeTimes + refreshed.RefreshTimes,
            "Type413 refresh consumes exactly one refresh budget");
        List<int> frozenRefreshOffer = refreshed.TalentIds.ToList();
        test.Call(nameof(Theatre4RefreshTalentRequest),
            new Theatre4RefreshTalentRequest { Color = red.Color }, transportId: 41_301);
        red = test.Adventure.Colors.Single(color => color.Color == leap.Params[0]);
        AssertEqual(true, frozenRefreshOffer.SequenceEqual(red.WaitSlot!.TalentIds),
            "Type413 refresh retry preserves the frozen offer identity");

        int firstSlotId = refreshed.SlotId;
        test.Call(nameof(Theatre4SelectTalentRequest),
            new Theatre4SelectTalentRequest { Color = red.Color, TalentId = refreshed.TalentIds[0] });
        red = test.Adventure.Colors.Single(color => color.Color == leap.Params[0]);
        AssertEqual(2, red.Slots.Single(slot => slot.SlotId == firstSlotId).Talents.Count,
            "Type413 appends the first bonus selection to the existing slot");

        // The second bonus offer is a normal persisted WaitSlot and must survive login unchanged.
        red = test.Adventure.Colors.Single(color => color.Color == leap.Params[0]);
        Theatre4ColorTalentWaitSlotData beforeRelog = red.WaitSlot
            ?? throw new InvalidDataException("Type413 did not retain the second bonus offer.");
        List<int> relogOffer = beforeRelog.TalentIds.ToList();
        int relogSlotId = beforeRelog.SlotId;
        test.Relog("type413 pending bonus");
        red = test.Adventure.Colors.Single(color => color.Color == leap.Params[0]);
        AssertEqual(relogSlotId, red.WaitSlot!.SlotId, "Type413 relog retains the pending slot identity");
        AssertEqual(true, relogOffer.SequenceEqual(red.WaitSlot.TalentIds),
            "Type413 relog retains the pending offer cards");

        int retryTalent = red.WaitSlot.TalentIds[0];
        test.Call(nameof(Theatre4SelectTalentRequest),
            new Theatre4SelectTalentRequest { Color = red.Color, TalentId = retryTalent }, transportId: 41_302);
        red = test.Adventure.Colors.Single(color => color.Color == leap.Params[0]);
        int selectedAfterRetry = red.Slots.Single(slot => slot.SlotId == relogSlotId).Talents.Count;
        test.Call(nameof(Theatre4SelectTalentRequest),
            new Theatre4SelectTalentRequest { Color = red.Color, TalentId = retryTalent }, transportId: 41_302);
        red = test.Adventure.Colors.Single(color => color.Color == leap.Params[0]);
        AssertEqual(selectedAfterRetry, red.Slots.Single(slot => slot.SlotId == relogSlotId).Talents.Count,
            "Type413 selection retry cannot append a duplicate talent");

        int remainingBonusSelections = bonusSlots.Count * 2 - 2;
        for (int index = 0; index < remainingBonusSelections; index++)
        {
            red = test.Adventure.Colors.Single(color => color.Color == leap.Params[0]);
            Theatre4ColorTalentWaitSlotData wait = red.WaitSlot
                ?? throw new InvalidDataException("Type413 stopped its serial bonus offers early.");
            test.Call(nameof(Theatre4SelectTalentRequest),
                new Theatre4SelectTalentRequest { Color = red.Color, TalentId = wait.TalentIds[0] });
        }

        red = test.Adventure.Colors.Single(color => color.Color == leap.Params[0]);
        AssertEqual(null, red.WaitSlot, "Type413 retires the final bonus offer without reopening it");
        foreach (Theatre4ColorTalentSlotTable slot in bonusSlots)
            AssertEqual(3, red.Slots.Single(data => data.SlotId == slot.Id).Talents.Count,
                "Type413 reaches three durable selections per eligible major slot");
        AssertEqual(1, red.Slots.Single(data => data.SlotId == ownerSlot.Id).Talents.Count,
            "Type413 excludes its own authored owner slot");
        HashSet<int> bonusSlotIds = bonusSlots.Select(slot => slot.Id).ToHashSet();
        List<int> selectedIds = red.Slots.Where(slot => bonusSlotIds.Contains(slot.SlotId))
            .SelectMany(slot => slot.Talents).Select(talent => talent.TalentId).ToList();
        AssertEqual(selectedIds.Count, selectedIds.Distinct().Count(),
            "Type413 never duplicates an exclusive talent across bonus offers");
        AssertEqual(bonusSlots.Count * 3,
            red.Slots.Where(slot => bonusSlotIds.Contains(slot.SlotId))
                .Sum(slot => slot.Talents.Count),
            "Type413 persists all 21 authored exclusive selections");

        test.Relog("type413 complete");
        red = test.Adventure.Colors.Single(color => color.Color == leap.Params[0]);
        AssertEqual(selectedIds.Count,
            red.Slots.Where(slot => bonusSlotIds.Contains(slot.SlotId))
                .SelectMany(slot => slot.Talents).Count(),
            "Type413 completed slot lists survive relog");
    }

    private static int Theatre4OwnedShopEntryGold(Theatre4Case test,
        IReadOnlyList<Theatre4EffectTable> effects)
    {
        IEnumerable<Theatre4EffectData> owned = test.Adventure.CustomEffects
            .Concat(test.Adventure.Items.SelectMany(item => item.Effects))
            .Concat(test.Adventure.Props.SelectMany(item => item.Effects))
            .Concat(test.Adventure.Colors.SelectMany(color => color.Slots)
                .SelectMany(slot => slot.Talents).SelectMany(talent => talent.Effects));
        return owned.Select(effect => effects.FirstOrDefault(row => row.Id == effect.EffectId))
            .OfType<Theatre4EffectTable>()
            .Where(row => row.Type == 217 && row.Params is { Count: >= 3 }
                && row.Params[0] == T4AssetGold)
            .Sum(row => row.Params![2]);
    }

    private static void ValidateTheatre4DivinePowerCompatibility()
    {
        using Theatre4Case test = new("talent-415");
        test.StartRun(1);

        // Keep difficulty/affix CustomEffects and their applied receipts: RebuildEffects must not
        // re-grant startup assets while this fixture selects the authored yellow talents.
        List<Theatre4EffectTable> effects = TableReaderV2.Parse<Theatre4EffectTable>();

        List<Theatre4EffectGroupTable> effectGroups = TableReaderV2.Parse<Theatre4EffectGroupTable>();
        List<Theatre4ColorTalentTable> talents = TableReaderV2.Parse<Theatre4ColorTalentTable>();
        List<Theatre4ColorTalentPoolTable> pools = TableReaderV2.Parse<Theatre4ColorTalentPoolTable>();
        List<Theatre4ColorTalentSlotTable> slots = TableReaderV2.Parse<Theatre4ColorTalentSlotTable>();
        Theatre4EffectTable divine = effects.Single(row => row.Type == 415);
        int shopId = divine.Params[0];
        HashSet<int> divineEffectGroups = effectGroups
            .Where(group => group.Effects is { } ids && ids.Contains(divine.Id))
            .Select(group => group.Id).ToHashSet();
        Theatre4ColorTalentTable ownerTalent = talents
            .Single(talent => divineEffectGroups.Contains(talent.EffectGroupId));
        Theatre4ColorTalentSlotTable ownerSlot = slots
            .Where(slot => slot.Level > 0 && slot.UnlockPoint is > 0)
            .Single(slot => pools.Any(pool => pool.Group == slot.GeneratePoolGroup
                && pool.TalentId == ownerTalent.Id));
        int yellowColor = ownerSlot.Color;
        List<Theatre4ColorTalentSlotTable> yellowSlots = slots
            .Where(slot => slot.Color == yellowColor && slot.Level > 0 && slot.UnlockPoint is > 0)
            .OrderBy(slot => slot.Level).ToList();

        int mapId = test.Adventure.Chapters[0].MapId;
        _ = Theatre4EmptyTile(test, mapId);
        Dictionary<int, int> beforeTypes = test.Chapter(mapId).Grids
            .ToDictionary(grid => grid.GridId, grid => grid.Type);
        int type415BuildPointBefore = 0;
        Theatre4AddAsset(test, T4AssetColorPoint, yellowColor, yellowSlots.Max(slot => slot.UnlockPoint ?? 0));

        Theatre4ColorTalentData yellow = test.Adventure.Colors.Single(color => color.Color == yellowColor);
        foreach (Theatre4ColorTalentSlotTable slot in yellowSlots)
        {
            Theatre4ColorTalentWaitSlotData wait = yellow.WaitSlot
                ?? throw new InvalidDataException("Type415 fixture lost the next authored talent offer.");
            AssertEqual(slot.Id, wait.SlotId, "Type415 follows authored yellow slot order");
            if (slot.Id == ownerSlot.Id)
                AssertEqual(true, wait.TalentIds.Contains(ownerTalent.Id),
                    "Type415 owner slot exposes its authored talent");
            int offered = wait.TalentIds[0];
            Theatre4SelectTalentRequest request = new() { Color = yellow.Color, TalentId = offered };
            if (slot.Id == ownerSlot.Id)
            {
                int ownerGoldBefore = test.Adventure.Gold;
                type415BuildPointBefore = test.Adventure.Bp;
                Dictionary<int, long> ownerItemsBefore = test.ItemCounts();
                // AscNet policy materializes Type415 as a processed shop and routes it through
                // the same entry trigger as direct Type201 creation; compute only pre-existing
                // Type217 consumers here.
                int ownedShopEntryGold = Theatre4OwnedShopEntryGold(test, effects);
                test.Call(nameof(Theatre4SelectTalentRequest), request, transportId: 41_501);
                AssertEqual(type415BuildPointBefore, test.Adventure.Bp,
                    "Type415 never debits BuildPoint");
                AssertEqual(ownerGoldBefore + ownedShopEntryGold, test.Adventure.Gold,
                    "Type415 applies only owned authored shop-entry gold");
                AssertEqual(true, ownerItemsBefore.OrderBy(pair => pair.Key)
                        .SequenceEqual(test.ItemCounts().OrderBy(pair => pair.Key)),
                    "Type415 does not purchase shop goods");
                int shopsAfterFirst = test.Chapter(mapId).Grids.Count(grid => grid.Type == T4GridShop);
                test.Call(nameof(Theatre4SelectTalentRequest), request, transportId: 41_501);
                AssertEqual(shopsAfterFirst,
                    test.Chapter(mapId).Grids.Count(grid => grid.Type == T4GridShop),
                    "Type415 selection retry does not spawn a second shop");
                AssertEqual(type415BuildPointBefore, test.Adventure.Bp,
                    "Type415 retry never debits BuildPoint");
                AssertEqual(ownerGoldBefore + ownedShopEntryGold, test.Adventure.Gold,
                    "Type415 retry does not auto-purchase from the shop");
                AssertEqual(true, ownerItemsBefore.OrderBy(pair => pair.Key)
                        .SequenceEqual(test.ItemCounts().OrderBy(pair => pair.Key)),
                    "Type415 retry does not purchase shop goods");
            }
            else
            {
                test.Call(nameof(Theatre4SelectTalentRequest), request);
            }
            yellow = test.Adventure.Colors.Single(color => color.Color == yellowColor);
        }

        Theatre4GridData unlocked = test.Chapter(mapId).Grids
            .SingleOrDefault(grid => beforeTypes.TryGetValue(grid.GridId, out int oldType)
                && grid.Type == T4GridShop && oldType is T4GridEmpty or T4GridBlank)
            ?? throw new InvalidDataException("Type415 did not convert an authored blank/empty tile");
        Theatre4ShopGroupTable shopGroup = TableReaderV2.Parse<Theatre4ShopGroupTable>()
            .Where(row => row.ShopId == shopId).OrderBy(row => row.Id).First();
        AssertEqual(shopId, unlocked.ContentId, "Type415 uses Params[0] as the authored shop id");
        AssertEqual(shopGroup.ShopGroupId, unlocked.ContentGroup,
            "Type415 uses the authored shop-group identity");
        AssertEqual(shopId, unlocked.Shop!.ShopId, "Type415 initializes the durable shop shelf");
        int unlockedGridId = unlocked.GridId;
        int shopCount = test.Chapter(mapId).Grids.Count(grid => grid.Type == T4GridShop);
        test.Relog("type415 shop");
        AssertEqual(shopCount, test.Chapter(mapId).Grids.Count(grid => grid.Type == T4GridShop),
            "Type415 shop placement survives relog without repeating");
        AssertEqual(type415BuildPointBefore, test.Adventure.Bp, "Type415 relog keeps BuildPoint unchanged");
        Theatre4GridData relogShop = test.Chapter(mapId).Grids
            .Single(grid => grid.GridId == unlockedGridId);
        AssertEqual(shopId, relogShop.ContentId, "Type415 relog keeps shop content identity");
        AssertEqual(shopGroup.ShopGroupId, relogShop.ContentGroup,
            "Type415 relog keeps shop-group identity");
    }
}
