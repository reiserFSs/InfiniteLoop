using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.equip;
using Newtonsoft.Json.Linq;

namespace AscNet.Test;

internal static partial class Program
{
    /// <summary>
    /// Player report "signature memory from Leia is missing": suit 1666 (Mousme) and the 4.8 suit 1665 (Pesanteur) had
    /// no good in the 6★ Memory Shop (406), the only place the client's Set screen (ScreenGroup 2) offers them, so no
    /// owned-or-purchasable path existed. Every piece must be buyable for the shared 150 6★ Memory Shard price.
    /// </summary>
    private static void ValidateVersion49MemoryShop()
    {
        int[] suitIds = [1665, 1666];
        JObject shops = JObject.Parse(File.ReadAllText(ResourcePath("Configs", "client_shops.json")));
        JObject[] goods = ((JArray)shops["406"]!["GoodsList"]!).OfType<JObject>().ToArray();
        EquipTable[] pieces = TableReaderV2.Parse<EquipTable>()
            .Where(row => suitIds.Contains(row.SuitId) && row.Site is >= 1 and <= 6).ToArray();
        AssertEqual(12, pieces.Length, "two 6★ memory suits of six pieces");

        const long playerId = 99_849;
        Character character = CreateDrawCompatibilityCharacter(playerId);
        Inventory inventory = CreateDrawCompatibilityInventory(playerId, [new Item { Id = 29, Count = 150 * 12 }]);
        using MongoCollectionOverride noOpStages = MongoCollectionOverride.InstallNoOpStageCollection();
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out _, out _, out _);
        using LoopbackSessionHarness harness = new(character, CreateDrawCompatibilityPlayer(playerId), inventory,
            "memory-shop-compat-test");

        int packetId = 14_849;
        foreach (EquipTable piece in pieces)
        {
            JObject good = goods.Single(row => (int)row["RewardGoods"]!["TemplateId"]! == piece.Id);
            AssertEqual((int)RewardType.Equip, (int)good["RewardGoods"]!["RewardType"]!, $"{piece.Name} {piece.Id} is an equip good");
            JObject cost = ((JArray)good["ConsumeList"]!).OfType<JObject>().Single();
            AssertEqual(29, (int)cost["Id"]!, $"{piece.Id} costs 6★ Memory Shards");
            AssertEqual(150, (int)cost["Count"]!, $"{piece.Id} price matches every other 6★ memory");
            InvokeRegisteredRequestHandler(nameof(BuyRequest), harness.Session, ++packetId, new BuyRequest
            {
                ShopId = 406, GoodsId = (uint)(int)good["Id"]!, Count = 1
            });
            BuyResponse response = ReadResponsePayload<BuyResponse>(
                harness, packetId, nameof(BuyResponse), $"memory {piece.Id} purchase", maxPacketsToRead: 12);
            AssertEqual(0, response.Code, $"memory {piece.Id} purchase Code");
            AssertEqual(true, character.Equips.Any(row => row.TemplateId == (uint)piece.Id), $"memory {piece.Id} is owned");
        }
        AssertEqual(0L, inventory.Items.Single(item => item.Id == 29).Count, "all twelve pieces were paid in shards");
    }
}
