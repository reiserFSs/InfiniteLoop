using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.theatre5;
using AscNet.Table.V2.share.theatre5.theatre5pve;

namespace AscNet.GameServer.Handlers;

internal static partial class Theatre5Module
{
    internal static void InitializeProgression(Mutation m)
    {
        var adventure = Adventure(m);
        var levels = ProgressionLevels(m);
        adventure.CharacterLv = levels.Min(row => row.Level ?? 0);
        adventure.CharacterExp = ConfigInt("CharacterInitExp");
        Require(adventure.CharacterExp >= 0, 20283002);
        adventure.RandomRelics.Clear();
        adventure.UseRelicRefreshCount = 0;
    }

    // EXP producers are silent: event/battle/BuyExp responses add deltas, whereas
    // relic effects publish NewExp absolutely. Never publish both for one grant.
    internal static void AddExperience(Mutation m, int amount)
    {
        var adventure = Adventure(m);
        Require(amount >= 0 && (long)adventure.CharacterExp + amount <= int.MaxValue, 20283019);
        adventure.CharacterExp += amount; // Keep overflow EXP, including at the authored maximum level.
    }

    internal static bool CanLevelUp(Mutation m)
    {
        var adventure = Adventure(m);
        var levels = ProgressionLevels(m);
        var current = levels.FirstOrDefault(row => (row.Level ?? 0) == adventure.CharacterLv);
        Require(current != null, 20283002);
        return current.Exp > 0 && adventure.CharacterExp >= current.Exp
            && levels.Any(row => (row.Level ?? 0) == adventure.CharacterLv + 1);
    }

    private static List<Theatre5CharacterLevelTable> ProgressionLevels(Mutation m)
    {
        int group;
        if (m.Data.PvpType == 1)
            group = ConfigInt("PvpLevelGroup");
        else
        {
            Require(m.Data.PvpType == 2, 20282001);
            var chapterData = m.Data.PveAdventureData?.PveChapterData;
            Require(chapterData != null, 20282019);
            var chapter = Rows<Theatre5PveChapterTable>().FirstOrDefault(row => row.Id == chapterData.ChapterId);
            Require(chapter != null, 20282004);
            group = chapter.LevelGroup;
        }
        var levels = Rows<Theatre5CharacterLevelTable>().Where(row => row.GroupId == group).ToList();
        Require(levels.Count > 0, 20283002);
        return levels;
    }

    private static Theatre5AdventureData ProgressionAdventure(Mutation m)
    {
        EnsureAvailable(m.Session);
        var adventure = Adventure(m);
        // Both skill choice and shopping host the client's interrupt queue.
        Require(adventure.Status is 3 or 4, 20281001);
        return adventure;
    }

    private static List<Theatre5Item> DrawLevelRelics(Mutation m, Theatre5CharacterLevelTable level)
    {
        var adventure = Adventure(m);
        List<Theatre5Item> offers = [];
        HashSet<int> excluded = [];
        // LOCAL selection: ordered authored slots, weighted eligible draws without
        // duplicate config IDs. Refresh replaces the whole offer, not owned relics.
        // Shared sampling enforces authored conditions and ownership limits.
        foreach (int group in level.RelicGroup.Where(group => group > 0))
        {
            int itemId = DrawItemGroup(m, group, excluded);
            Require(ItemConfig(itemId).Type == 7, 20283005);
            offers.Add(NewItem(adventure, itemId));
            excluded.Add(itemId);
        }
        return offers;
    }

    [RequestPacketHandler("XTheatre5CharacterLevelUpRequest")]
    public static void XTheatre5CharacterLevelUpRequestHandler(Session session, Packet.Request packet) =>
        Handle<XTheatre5CharacterLevelUpRequest, XTheatre5CharacterLevelUpResponse>(session, packet, (m, request, response) =>
        {
            var adventure = ProgressionAdventure(m);
            Require(adventure.RandomRelics.Count == 0, 20283006);
            var levels = ProgressionLevels(m);
            var current = levels.FirstOrDefault(row => (row.Level ?? 0) == adventure.CharacterLv);
            Require(current != null, 20283002);
            var next = levels.FirstOrDefault(row => (row.Level ?? 0) == adventure.CharacterLv + 1);
            Require(next != null, 20283003);
            Require(current.Exp > 0 && adventure.CharacterExp >= current.Exp, 20283001);
            adventure.CharacterExp -= current.Exp.Value;
            adventure.CharacterLv = next.Level ?? 0;
            adventure.UseRelicRefreshCount = 0;
            // The row describes the transition out of its level: the client labels
            // its relic unlock as row.Level + 1, including the initial level-zero row.
            adventure.RandomRelics = DrawLevelRelics(m, current);
            TriggerEffects(m, "LevelUp");
            response.CharacterLv = adventure.CharacterLv;
            response.CharacterExp = adventure.CharacterExp;
            response.Status = adventure.Status;
            response.UseRefreshCount = adventure.UseRelicRefreshCount;
            response.RandomRelics = Clone(adventure.RandomRelics);
        });

    [RequestPacketHandler("XTheatre5RelicRefreshRequest")]
    public static void XTheatre5RelicRefreshRequestHandler(Session session, Packet.Request packet) =>
        Handle<XTheatre5RelicRefreshRequest, XTheatre5RelicRefreshResponse>(session, packet, (m, request, response) =>
        {
            var adventure = ProgressionAdventure(m);
            Require(adventure.RandomRelics.Count > 0, 20283007);
            Require(adventure.UseRelicRefreshCount < ConfigInt("RelicRefresh"), 20283004);
            var level = ProgressionLevels(m).FirstOrDefault(row => (row.Level ?? 0) == adventure.CharacterLv - 1);
            Require(level != null, 20283002);
            var offers = DrawLevelRelics(m, level);
            Require(offers.Count > 0, 20283007);
            adventure.RandomRelics = offers;
            adventure.UseRelicRefreshCount++;
            response.UseRefreshCount = adventure.UseRelicRefreshCount;
            response.RandomRelics = Clone(adventure.RandomRelics);
        });

    [RequestPacketHandler("XTheatre5RelicChooseRequest")]
    public static void XTheatre5RelicChooseRequestHandler(Session session, Packet.Request packet) =>
        Handle<XTheatre5RelicChooseRequest, XTheatre5RelicChooseResponse>(session, packet, (m, request, response) =>
        {
            var adventure = ProgressionAdventure(m);
            Require(adventure.RandomRelics.Count > 0, 20283007);
            var relic = adventure.RandomRelics.FirstOrDefault(item => item.InstanceId == request.InstanceId);
            Require(relic != null && relic.ItemType == 7, 20283005);
            Require(CanOwnItem(m, relic.ItemId), 20283005);
            AddExistingItem(m, relic);
            adventure.RandomRelics.Clear();
            adventure.UseRelicRefreshCount = 0;
            TriggerEffects(m, "ChooseRelic", relic.ItemId, relic.InstanceId);
            response.Status = adventure.Status;
            response.UseRefreshCount = adventure.UseRelicRefreshCount;
            response.RandomRelics = Clone(adventure.RandomRelics);
            response.ChooseRelic = Clone(relic);
        });

    [RequestPacketHandler("XTheatre5BuyExpRequest")]
    public static void XTheatre5BuyExpRequestHandler(Session session, Packet.Request packet) =>
        Handle<XTheatre5BuyExpRequest, XTheatre5BuyExpResponse>(session, packet, (m, request, response) =>
        {
            var adventure = ProgressionAdventure(m);
            Require(adventure.Status == 4 && adventure.RandomRelics.Count == 0, 20283008);
            Require(request.Exp > 0, 20283019);
            var levels = ProgressionLevels(m);
            var current = levels.FirstOrDefault(row => (row.Level ?? 0) == adventure.CharacterLv);
            Require(current != null, 20283002);
            Require(levels.Any(row => (row.Level ?? 0) == adventure.CharacterLv + 1), 20283003);
            Require(current.Exp > adventure.CharacterExp, 20283008);
            Require(request.Exp <= current.Exp - adventure.CharacterExp, 20283020);
            Require(adventure.ShopData != null, 20283018);
            var shop = Rows<Theatre5ShopTable>().FirstOrDefault(row => row.Id == adventure.ShopData.ShopId);
            Require(shop != null, 20281002);
            Require(shop.ExpPrice > 0, 20283008);
            long cost = (long)request.Exp * shop.ExpPrice;
            Require(cost <= adventure.GoldNum, 20281005);
            adventure.GoldNum -= (int)cost;
            AddExperience(m, request.Exp);
            UpdateMissionProgress(m, "SpendGold", (int)cost);
            // No absolute balance/EXP push here: the callback applies these deltas.
            response.BuyExp = request.Exp;
            response.CostGold = (int)cost;
        });
}
