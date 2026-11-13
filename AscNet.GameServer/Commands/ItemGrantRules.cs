using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.item;

namespace AscNet.GameServer.Commands;

internal static class ItemGrantRules
{
    internal static long BatchLimit(ItemTable item) =>
        item.ItemType == (int)ItemType.Gift ? 1 : Math.Min(Inventory.GetMaxCount(item), Inventory.GlobalItemMaxCount);

    internal static IReadOnlyList<int> CombinedIds(int itemId) =>
        Inventory.IsBlackCard(itemId) ? [Inventory.PaidGem, Inventory.FreeGem] : Inventory.CombinedItemIds(itemId);

    internal static WeaponFashionData? UnlockCoating(Session session, int fashionId)
    {
        List<WeaponFashionData> changed = [];
        RewardHandler.UnlockWeaponFashionReward(fashionId, session, changed);
        return changed.SingleOrDefault();
    }

    internal static void PushCoating(Session session, WeaponFashionData coating) =>
        session.SendPush(new NotifyWeaponFashionInfo { WeaponFashionDataList = [coating] });
}
