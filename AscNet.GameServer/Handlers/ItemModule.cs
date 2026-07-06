using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.item;
using AscNet.Table.V2.share.reward;
using MessagePack;

namespace AscNet.GameServer.Handlers
{
    #region MsgPackScheme
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    [MessagePackObject(true)]
    public class GetAndroidOrIosMoneyCardResponse
    {
        public int Code;
        public int MoneyCard;
        public int Count;
    }

    [MessagePackObject(true)]
    public class ItemUseRequest
    {
        public int Id;
        public int RecycleTime;
        public int Count;
    }
    
    [MessagePackObject(true)]
    public class ItemUseResponse
    {
        public int Code;
        public List<RewardGoods> RewardGoodsList { get; set; } = new();
    }
    [MessagePackObject(true)]
    public class ItemBuyAssetRequest
    {
        public int Times { get; set; }
        public int ItemId { get; set; }
        public int ConsumeId { get; set; }
    }

    [MessagePackObject(true)]
    public class ItemBuyAssetResponse
    {
        public int Code { get; set; }
        public int Count { get; set; }
        public bool IsCrit { get; set; }
    }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    #endregion

    internal class ItemModule
    {
        [RequestPacketHandler("GetAndroidOrIosMoneyCardRequest")]
        public static void GetAndroidOrIosMoneyCardRequestHandler(Session session, Packet.Request packet)
        {
            session.SendResponse(new GetAndroidOrIosMoneyCardResponse()
            {
                Code = 0,
                Count = 0,
                MoneyCard = 0
            }, packet.Id);
        }
        
        // Fixed-reward gift packs, such as Cog Packs.
        [RequestPacketHandler("ItemUseRequest")]
        public static void ItemUseRequestHandler(Session session, Packet.Request packet)
        {
            ItemUseRequest request = packet.Deserialize<ItemUseRequest>();
            int itemId = request.Id;
            int count = request.Count;
            if (itemId <= 0 || count <= 0)
            {
                session.SendResponse(new ItemUseResponse() { Code = 1 }, packet.Id);
                return;
            }

            ItemTable? itemTable = TableReaderV2.Parse<ItemTable>().FirstOrDefault(item => item.Id == itemId);
            Item? inventoryItem = session.inventory.Items.FirstOrDefault(item => item.Id == itemId);
            if (itemTable is null || inventoryItem is null || inventoryItem.Count < count)
            {
                session.SendResponse(new ItemUseResponse() { Code = 1 }, packet.Id);
                return;
            }

            List<Reward> rewards = [];
            ItemUseResponse response = new() { Code = 0 };
            if (!TryBuildItemUseRewards(itemTable, count, response.RewardGoodsList, rewards))
            {
                session.SendResponse(new ItemUseResponse() { Code = 1 }, packet.Id);
                return;
            }

            if (rewards.Count == 0)
            {
                session.SendResponse(new ItemUseResponse() { Code = 1 }, packet.Id);
                return;
            }

            session.SendPush(new NotifyItemDataList
            {
                ItemDataList = { session.inventory.Do(itemId, -count) }
            });
            RewardHandler.GiveRewards(rewards, session);
            session.inventory.Save();
            session.character.Save();
            session.SendResponse(response, packet.Id);
        }

        private static int ResolveFixedGiftRewardId(ItemTable itemTable)
        {
            if (itemTable.ItemType != (int)AscNet.Common.ItemType.Gift)
                return 0;

            return itemTable.SubTypeParams.Count >= 2 && itemTable.SubTypeParams[0] == 1
                ? itemTable.SubTypeParams[1]
                : 0;
        }

        private static bool TryBuildItemUseRewards(ItemTable itemTable, int count, List<RewardGoods> rewardGoodsList, List<Reward> rewards)
        {
            if (TryBuildRandomGiftRewards(itemTable, count, rewardGoodsList, rewards))
                return true;

            int fixedRewardId = ResolveFixedGiftRewardId(itemTable);
            if (fixedRewardId > 0)
                return TryAddRewardGoods(RewardHandler.GetRewardGoods(fixedRewardId), count, rewardGoodsList, rewards);

            return false;
        }

        private static bool TryBuildRandomGiftRewards(ItemTable itemTable, int count, List<RewardGoods> rewardGoodsList, List<Reward> rewards)
        {
            if (itemTable.ItemType != (int)AscNet.Common.ItemType.Gift
                || itemTable.SubTypeParams.Count < 2
                || itemTable.SubTypeParams[0] != 2)
            {
                return false;
            }

            List<RewardGoodsTable> configuredRewards = RewardHandler.GetRewardGoods(itemTable.SubTypeParams[1])
                .Where(rewardGoods => RewardHandler.GetRewardType(rewardGoods) is not null)
                .ToList();
            if (configuredRewards.Count == 0)
                return false;

            for (int i = 0; i < count; i++)
            {
                RewardGoodsTable selectedReward = configuredRewards[Random.Shared.Next(configuredRewards.Count)];
                if (!TryAddSingleRewardGoods(selectedReward, 1, rewardGoodsList, rewards))
                    return false;
            }

            return true;
        }


        private static bool TryAddRewardGoods(IEnumerable<RewardGoodsTable> configuredRewards, int count, List<RewardGoods> rewardGoodsList, List<Reward> rewards)
        {
            foreach (var rewardGoods in configuredRewards)
            {
                if (!TryAddSingleRewardGoods(rewardGoods, count, rewardGoodsList, rewards))
                    return false;
            }

            return rewards.Count > 0;
        }

        private static bool TryAddSingleRewardGoods(RewardGoodsTable rewardGoods, int count, List<RewardGoods> rewardGoodsList, List<Reward> rewards)
        {
            RewardType? rewardType = RewardHandler.GetRewardType(rewardGoods);
            if (rewardType is null)
                return true;

            long rewardCountLong = (long)rewardGoods.Count * count;
            if (rewardCountLong > int.MaxValue)
                return false;

            int rewardCount = (int)rewardCountLong;
            rewardGoodsList.Add(new RewardGoods
            {
                Id = rewardGoods.Id,
                TemplateId = rewardGoods.TemplateId,
                Count = rewardCount,
                RewardType = (int)rewardType.Value
            });
            rewards.Add(new Reward
            {
                Id = rewardGoods.TemplateId,
                Count = rewardCount,
                Type = rewardType.Value
            });

            return true;
        }

        [RequestPacketHandler("ItemBuyAssetRequest")]
        public static void ItemBuyAssetRequestHandler(Session session, Packet.Request packet)
        {
            ItemBuyAssetRequest request = packet.Deserialize<ItemBuyAssetRequest>();
            int count = Math.Max(0, request.Times);

            Item consumedItem = session.inventory.Do(request.ConsumeId, -count);
            Item boughtItem = session.inventory.Do(request.ItemId, count);

            session.SendPush(new NotifyItemDataList
            {
                ItemDataList = { consumedItem, boughtItem }
            });
            session.inventory.Save();
            session.SendResponse(new ItemBuyAssetResponse
            {
                Count = count,
                IsCrit = false
            }, packet.Id);
        }
     }
}
