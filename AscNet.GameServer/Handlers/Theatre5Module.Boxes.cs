using AscNet.Common.MsgPack;
using AscNet.Table.V2.share.theatre5;

namespace AscNet.GameServer.Handlers;

internal static partial class Theatre5Module
{
    [RequestPacketHandler("XTheatre5ItemBoxOpenRequest")]
    public static void ItemBoxOpen(Session session, Packet.Request packet) =>
        Handle<XTheatre5ItemBoxOpenRequest, XTheatre5ItemBoxOpenResponse>(session, packet, (mutation, request, response) =>
        {
            EnsureAvailable(session, 2);
            Require(mutation.Data.PvpType == 2, 20282001);
            Theatre5AdventureData adventure = Adventure(mutation);
            Require(adventure is Theatre5PveAdventureData, 20282022);
            Require(adventure.Status is not (5 or 6), 20280006);
            Theatre5PveAdventureData pve = (Theatre5PveAdventureData)adventure;

            // A retried open uses the durable choices, never rerolls or consumes twice.
            Theatre5ItemBoxSelection? pending = pve.ItemBoxSelectData.FirstOrDefault(box => box.BoxInstanceId == request.BoxInstanceId);
            if (pending != null)
            {
                response.OpenType = 1;
                response.UsedInstanceId = pending.BoxInstanceId;
                response.ItemBoxSelectData = Clone(pending.ItemList);
                return;
            }

            Theatre5Item? boxItem = adventure.BagData.BagItemDict.Values.Concat(adventure.BagData.TempItemDict.Values)
                .FirstOrDefault(item => item.InstanceId == request.BoxInstanceId);
            Require(boxItem != null && boxItem.ItemType == 3 && ItemConfig(boxItem.ItemId).Type == 3, 20281003);
            Theatre5ItemBoxTable? config = Rows<Theatre5ItemBoxTable>().FirstOrDefault(row => row.Id == boxItem.ItemId);
            Require(config != null && config.BoxOpenType is 1 or 2 && config.ItemGroupIds.Count > 0, 20282039);

            List<Theatre5Item> items = OpenItemBox(mutation, pve, boxItem, config);

            response.OpenType = config.BoxOpenType;
            response.UsedInstanceId = boxItem.InstanceId;
            // Open consumes a raw array; login retains the wrapper above.
            response.ItemBoxSelectData = Clone(items);
            PushBag(mutation);
        });

    private static List<Theatre5Item> OpenItemBox(Mutation mutation, Theatre5PveAdventureData adventure,
        Theatre5Item boxItem, Theatre5ItemBoxTable config)
    {
        Require(boxItem.ItemType == 3 && ItemConfig(boxItem.ItemId).Type == 3, 20281003);
        Require(config.BoxOpenType is 1 or 2 && config.ItemGroupIds.Count > 0, 20282039);
        List<Theatre5Item> items = [];
        HashSet<int>? excluded = config.BoxOpenType == 1 ? [] : null;
        RemoveItem(adventure, boxItem.InstanceId);
        foreach (int groupId in config.ItemGroupIds)
        {
            Require(groupId > 0, 20282039);
            // LOCAL authored-input policy: choices are distinct; All draws see
            // previously awarded copies through the shared group's owned-copy cap.
            Theatre5Item item = NewItem(adventure, DrawItemGroup(mutation, groupId, excluded));
            items.Add(item);
            excluded?.Add(item.ItemId);
            if (config.BoxOpenType == 2) AddExistingItem(mutation, item);
        }
        if (config.BoxOpenType == 1)
            adventure.ItemBoxSelectData.Add(new() { BoxInstanceId = boxItem.InstanceId, ItemList = items });
        return items;
    }

    internal static void RecoverPendingBoxes(Mutation mutation)
    {
        if (mutation.Data.PveAdventureData is not { } adventure) return;
        int selectedMode = mutation.Data.PvpType;
        try
        {
            // Fresh login has no event-reward callback to issue auto-open RPCs.
            // Hydrate the real opened state in its existing atomic login transaction.
            mutation.Data.PvpType = 2;
            foreach (Theatre5Item box in adventure.BagData.BagItemDict.Values
                .Concat(adventure.BagData.TempItemDict.Values).Where(item => item.ItemType == 3).ToArray())
            {
                if (adventure.ItemBoxSelectData.Any(pending => pending.BoxInstanceId == box.InstanceId)) continue;
                Theatre5ItemBoxTable? config = Rows<Theatre5ItemBoxTable>().FirstOrDefault(row => row.Id == box.ItemId);
                Require(config != null, 20282039);
                if (config.IsAutoOpen == 1) OpenItemBox(mutation, adventure, box, config);
            }
        }
        finally
        {
            mutation.Data.PvpType = selectedMode;
        }
    }

    [RequestPacketHandler("XTheatre5ItemBoxSelectRequest")]
    public static void ItemBoxSelect(Session session, Packet.Request packet) =>
        Handle<XTheatre5ItemBoxSelectRequest, XTheatre5ItemBoxSelectResponse>(session, packet, (mutation, request, response) =>
        {
            EnsureAvailable(session, 2);
            Require(mutation.Data.PvpType == 2, 20282001);
            Theatre5AdventureData adventure = Adventure(mutation);
            Require(adventure is Theatre5PveAdventureData, 20282022);
            Require(adventure.Status is not (5 or 6), 20280006);
            Theatre5PveAdventureData pve = (Theatre5PveAdventureData)adventure;
            Theatre5ItemBoxSelection? pending = pve.ItemBoxSelectData.FirstOrDefault(box => box.BoxInstanceId == request.BoxInstanceId);
            Require(pending != null, 20281003);
            Theatre5Item? selected = pending.ItemList.FirstOrDefault(item => item.InstanceId == request.ItemInstanceId);
            Require(selected != null, 20281003);

            // Mutation is a cloned transaction: placement failure preserves the pending
            // selection, and transport receipt replay cannot grant the selected item twice.
            AddExistingItem(mutation, selected);
            pve.ItemBoxSelectData.Remove(pending);
            PushBag(mutation);
        });

    [RequestPacketHandler("XTheatre5HammerStrengthenRequest")]
    public static void HammerStrengthen(Session session, Packet.Request packet) =>
        Handle<XTheatre5HammerStrengthenRequest, XTheatre5HammerStrengthenResponse>(session, packet, (mutation, request, response) =>
        {
            EnsureAvailable(session);
            Theatre5AdventureData adventure = Adventure(mutation);
            Require(adventure.Status == 4, 20281001);
            Theatre5Item? hammer = adventure.BagData.BagItemDict.Values.Concat(adventure.BagData.TempItemDict.Values)
                .FirstOrDefault(item => item.InstanceId == request.HammerId);
            Theatre5Item? rune = adventure.BagData.BagItemDict.Values.Concat(adventure.BagData.RuneDict.Values)
                .FirstOrDefault(item => item.InstanceId == request.RuneId);
            Require(hammer != null && hammer.ItemType == 6, 20281003);
            Require(rune != null && rune.ItemType == 2, 20281003);
            Theatre5ItemTable hammerConfig = ItemConfig(hammer.ItemId);
            Theatre5ItemTable runeConfig = ItemConfig(rune.ItemId);
            Require(hammerConfig.Type == 6 && runeConfig.Type == 2, 20281003);
            Require(!rune.IsStrengthen, 20283010);
            Require(hammerConfig.Quality == runeConfig.Quality, 20283012);
            Require(hammerConfig.Tags.Count == 0 || hammerConfig.Tags.Any(tag => runeConfig.Tags.Contains(tag)), 20283011);

            RemoveItem(adventure, hammer.InstanceId);
            rune.IsStrengthen = true;
            UpdateMissionProgress(mutation, "StrengthenRune", 1, rune.ItemId);
            TriggerEffects(mutation, "StrengthenRune", rune.ItemId, 1);
            response.HammerId = hammer.InstanceId;
            response.Rune = Clone(rune);
            // The callback removes the hammer and updates this rune itself. A full bag
            // snapshot here would apply that same inventory change before the callback.
        });
}
