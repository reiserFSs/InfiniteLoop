using AscNet.Common.Util;
using AscNet.Table.V2.share.item;
using AscNet.Table.V2.share.reward;

namespace AscNet.GameServer.Handlers.Drops
{
    // Item 94033's description is the drop table. Reward 1011 is a different grant
    // (Cogs and Black Cards) and is not paid. The chances sum to 100.
    internal static class ConstructResearchFortuneBagPolicy
    {
        internal const int DropGroupId = 1011;
        internal const int TicketItemId = 50005;

        internal static readonly (int Count, int Weight)[] Tiers =
        [
            (250, 10),
            (225, 15),
            (200, 20),
            (175, 25),
            (150, 30),
        ];

        private static readonly Lazy<bool> TicketExists = new(() =>
            TableReaderV2.Parse<ItemTable>().Any(row => row.Id == TicketItemId));

        internal static bool Applies(int sourceId) => sourceId == DropGroupId;

        internal static bool TryGrant(int boxCount, out RewardGoodsTable good)
        {
            good = null!;
            int weight = Tiers.Sum(tier => tier.Weight);
            if (boxCount <= 0 || weight <= 0 || !TicketExists.Value)
                return false;

            long total = 0;
            for (int index = 0; index < boxCount; index++)
            {
                int roll = Random.Shared.Next(weight);
                int cursor = 0;
                int picked = 0;
                foreach ((int count, int tierWeight) in Tiers)
                {
                    cursor += tierWeight;
                    if (roll < cursor)
                    {
                        picked = count;
                        break;
                    }
                }
                if (picked <= 0)
                    return false;
                try { total = checked(total + picked); }
                catch (OverflowException) { return false; }
            }

            if (total <= 0 || total > int.MaxValue)
                return false;
            good = new RewardGoodsTable { TemplateId = TicketItemId, Count = (int)total, Params = [] };
            return true;
        }
    }
}
