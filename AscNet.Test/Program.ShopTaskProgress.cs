using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.task;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidateShopTaskProgressCompatibility()
    {
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForShopCompatibility();
        const long playerId = 99_163;
        Player player = CreateDrawCompatibilityPlayer(playerId);
        Inventory inventory = CreateDrawCompatibilityInventory(playerId, [new Item { Id = Inventory.Coin, Count = 1_000_000 }]);
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(playerId), player, inventory, "shop-task-progress");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(playerId);
        int packetId = 49_100;
        InvokeRegisteredRequestHandler(nameof(GetShopInfoRequest), harness.Session, ++packetId, new GetShopInfoRequest { Id = 1 });
        GetShopInfoResponse shop = ReadResponsePayload<GetShopInfoResponse>(harness, packetId, nameof(GetShopInfoResponse), "shop task catalog");
        var goods = shop.ClientShop.GoodsList.First(row => row.ConsumeList.Count == 1
            && row.ConsumeList[0].Id == Inventory.Coin && row.ConsumeList[0].Count > 0
            && (row.BuyTimesLimit == 0 || row.BuyTimesLimit >= 3)
            && row.RewardGoods.RewardType == (int)RewardType.Item && row.RewardGoods.TemplateId != Inventory.Coin);
        CurrentConditionTable purchase = TableReaderV2.Parse<CurrentConditionTable>().First(row => row.Type == 20201 && row.Params.Count == 1);
        CurrentConditionTable coin = TableReaderV2.Parse<CurrentConditionTable>().First(row => row.Type == 11202 && row.Params[1] == Inventory.Coin);
        CurrentConditionTable serum = TableReaderV2.Parse<CurrentConditionTable>().First(row => row.Type == 11202 && row.Params[1] == Inventory.ActionPoint);
        int paid = checked((int)goods.ConsumeList[0].Count * 3);
        inventory.Items.Single(item => item.Id == Inventory.Coin).Count = paid;

        BuyResponse Buy(uint shopId, int count)
        {
            InvokeRegisteredRequestHandler(nameof(BuyRequest), harness.Session, ++packetId,
                new BuyRequest { ShopId = shopId, GoodsId = goods.Id, Count = count });
            return (BuyResponse)ReadResponsePayload(harness, packetId, nameof(BuyResponse), "shop task purchase", typeof(BuyResponse), maxPacketsToRead: 64);
        }

        AssertEqual(0, Buy(shop.ClientShop.Id, 3).Code, "bulk purchase succeeds");
        AssertEqual(3, player.ShopBuyTimes[goods.Id], "bulk quantity committed");
        AssertEqual(3, player.MissionProgress.ConditionCounters.GetValueOrDefault(purchase.Id), "bulk purchase task quantity");
        AssertEqual(paid, player.MissionProgress.ConditionCounters.GetValueOrDefault(coin.Id), "actual paid currency task amount");
        AssertEqual(0, player.MissionProgress.ConditionCounters.GetValueOrDefault(serum.Id), "shop coins never count as serum");
        AssertEqual(0L, inventory.Items.Single(item => item.Id == Inventory.Coin).Count, "purchase deducts exact price");
        AssertEqual(1, Buy(uint.MaxValue, 1).Code, "goods from unrelated shop rejected");
        AssertEqual(1, Buy(shop.ClientShop.Id, 0).Code, "zero quantity rejected");
        AssertEqual(1, Buy(shop.ClientShop.Id, 1).Code, "unaffordable purchase rejected");
        AssertEqual(3, player.ShopBuyTimes[goods.Id], "rejected purchases preserve committed count");
        AssertEqual(3, player.MissionProgress.ConditionCounters.GetValueOrDefault(purchase.Id), "rejected purchases preserve task quantity");
        AssertEqual(paid, player.MissionProgress.ConditionCounters.GetValueOrDefault(coin.Id), "rejected purchases preserve currency task amount");
    }
}
