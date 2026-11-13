using System.Net.Http;
using System.Text.Json;
using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.bigworld.common.item;
using AscNet.Table.V2.share.item;
using MongoDB.Driver;

namespace AscNet.Test;

internal static partial class Program
{
    private static async Task ValidateLiveGmItems(GmTestHost server, GmTestGame account1, GmTestGame account2,
        int gamePort, Uri origin, HttpClient http, string uid2Token, IMongoCollection<Inventory> inventories)
    {
        const int tokenId = 47;
        const string tokenName = "Special Support Token";
        JsonElement catalog = Data(await server.Call("itemCatalog"), "catalog");
        AssertEqual(false, catalog.GetProperty("stale").GetBoolean(), "catalog revision is current");
        AssertEqual(64, catalog.GetProperty("revision").GetString()!.Length, "catalog has a SHA-256 revision");
        var main = TableReaderV2.Parse<ItemTable>();
        var bigWorld = TableReaderV2.Parse<BigWorldItemTable>();
        AssertEqual(main.Select(row => row.Id).Concat(bigWorld.Select(row => row.Id)).Distinct().Count(),
            catalog.GetProperty("total").GetInt32(), "every supported item is indexed");
        var tokenRow = main.Single(row => row.Id == tokenId);
        AssertEqual(tokenName, tokenRow.Name, "verified EN display name");
        long initial1 = Count(1, tokenId);
        long initial2 = Count(2, tokenId);
        long max = Inventory.GetMaxCount(tokenRow);

        foreach (string query in new[] { tokenName, "47", "support token", "speical suport token" })
        {
            var rows = Data(await server.Call("itemSearch", new { uid = 2, query }), "search").GetProperty("rows");
            var item = rows[0].GetProperty("item");
            AssertEqual(tokenId, item.GetProperty("id").GetInt32(), $"first result for '{query}'");
            AssertEqual(tokenName, item.GetProperty("name").GetString(), $"EN name for '{query}'");
            AssertEqual(max, item.GetProperty("inventoryMax").GetInt64(), "real inventory max on search");
            AssertEqual(Inventory.GlobalItemMaxCount, item.GetProperty("bulkMax").GetInt64(), "distinct bulk cap");
            AssertBalance(rows[0].GetProperty("balance"), initial2, max - initial2, "search balance");
        }
        AssertEqual(0, Data(await server.Call("itemSearch", new { uid = 2, query = tokenName, category = "material" }),
            "search").GetProperty("total").GetInt32(), "category filter excludes currency");
        var currency = await Inspect(tokenId);
        AssertEqual("Can be exchanged for items in Resource Shop", currency.GetProperty("details").GetProperty("description").GetString(),
            "authored token description");
        AssertEqual("currency", currency.GetProperty("details").GetProperty("item").GetProperty("category").GetString(), "money category");
        AssertEqual(99_999L, max, "PGR 4.9.x Special Support Token limit");

        ItemTable money = main.Single(row => row.Id == Inventory.Coin);
        AssertEqual(Inventory.MoneyItemMaxCount, ReadItem(await Inspect(money.Id)).GetProperty("inventoryMax").GetInt64(),
            "money without an override uses the money cap");
        ItemTable material = main.First(row => row.ItemType == (int)ItemType.Material && Inventory.CombinedItemIds(row.Id).Count == 1);
        ItemTable eventCurrency = main.First(row => row.ItemType == (int)ItemType.ActiveMoney);
        ItemTable gift = main.First(row => row.ItemType == (int)ItemType.Gift && row.SubTypeParams.FirstOrDefault() == 1);
        ItemTable selection = main.First(row => row.ItemType == (int)ItemType.Gift && row.SubTypeParams.FirstOrDefault() == 3);
        foreach (var row in new[] { material, eventCurrency, gift, selection })
        {
            var item = ReadItem(await Inspect(row.Id));
            AssertEqual(Inventory.GetMaxCount(row), item.GetProperty("inventoryMax").GetInt64(), $"authored cap for {row.Id}");
            AssertEqual(row.ItemType == (int)ItemType.Gift ? 1L : Math.Min(Inventory.GetMaxCount(row), Inventory.GlobalItemMaxCount),
                item.GetProperty("bulkMax").GetInt64(), $"bulk limit for {row.Id}");
        }
        AssertEqual(true, (await Inspect(selection.Id)).GetProperty("details").GetProperty("behavior").GetString()!
            .Contains("selection", StringComparison.OrdinalIgnoreCase), "selection packs stay unopened");
        var physical = await Inspect(Inventory.FreeGem);
        AssertEqual(0L, physical.GetProperty("balance").GetProperty("canAdd").GetInt64(), "combined currencies disable generic grants");
        AssertEqual(2, physical.GetProperty("details").GetProperty("combinedIds").GetArrayLength(), "physical currency IDs");
        AssertEqual(Count(2, Inventory.PaidGem) + Count(2, Inventory.FreeGem),
            physical.GetProperty("balance").GetProperty("combinedOwned").GetInt64(), "combined currency balance");
        AssertEqual("unavailable", ReadItem(await Inspect(bigWorld[0].Id)).GetProperty("grantKind").GetString(), "BigWorld is discoverable");
        AssertEqual(initial2, Count(2, tokenId), "search and inspect do not mutate inventory");

        string key = Guid.NewGuid().ToString("N");
        AssertEqual("grant_not_found", Error(await server.Call("itemResolve", new { uid = 2, itemId = tokenId, quantity = 2, grantId = key })),
            "receipt check is read-only");
        var first = Data(await Add(tokenId, 2, key), "grant");
        AssertEqual(true, first.GetProperty("updateQueued").GetBoolean(), "server queued item update");
        AssertEqual(false, first.GetProperty("replay").GetBoolean(), "first grant is new");
        AssertBalance(first.GetProperty("balance"), initial2 + 2, max - initial2 - 2, "first grant balance");
        var pushed = await account2.AwaitPush<NotifyItemDataList>();
        AssertEqual(initial2 + 2, pushed.ItemDataList.Single(item => item.Id == tokenId).Count, "existing game TCP connection receives absolute balance");
        AssertEqual(initial1, Count(1, tokenId), "simultaneous UID 1 inventory isolation");
        AssertEqual(false, (await account1.Pulse()).Any(push => push.Name == nameof(NotifyItemDataList)), "UID 1 receives no item update");
        AssertEqual(initial2 + 2, Count(2, tokenId), "journaled save before response");
        AssertEqual("grant_id_conflict", Error(await Add(tokenId, 3, key)), "same grant ID cannot change quantity");
        AssertEqual("grant_id_conflict", Error(await Add(material.Id, 2, key)), "same grant ID cannot change item");
        AssertEqual("invalid_item", Error(await Add(int.MaxValue, 1)), "unknown item");
        AssertEqual("invalid_quantity", Error(await Add(tokenId, -1)), "negative item quantity");
        AssertEqual("invalid_quantity", Error(await Add(tokenId, 0)), "zero item quantity");
        AssertEqual("invalid_request", Error(await server.Call("itemAdd",
            new { uid = 2, itemId = tokenId, quantity = long.MaxValue, grantId = key })), "quantity overflow");
        AssertEqual("invalid_grant_id", Error(await Add(tokenId, 1, "bad")), "grant ID validation");
        AssertEqual("invalid_uid", Error(await server.Call("itemSearch", new { uid = 0 })), "invalid UID");
        AssertEqual("unsupported_item", Error(await Add(Inventory.FreeGem, 1)), "combined physical stack cannot be granted");
        AssertEqual("inventory_limit", Error(await Add(tokenId, int.MaxValue)), "oversized request is rejected");

        string duplicateKey = Guid.NewGuid().ToString("N");
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Add(tokenId, 3, duplicateKey)));
        AssertEqual(1, concurrent.Select(reply => Data(reply, "grant")).Count(grant => !grant.GetProperty("replay").GetBoolean()),
            "concurrent duplicate clicks apply once");
        AssertEqual(initial2 + 5, Count(2, tokenId), "duplicate requests leave one grant");
        foreach (var row in new[] { material, eventCurrency, gift, selection })
        {
            long before = Count(2, row.Id);
            AssertEqual(before + 1, Data(await Add(row.Id, 1), "grant").GetProperty("balance").GetProperty("owned").GetInt64(),
                $"grant {row.Name} as an unopened/physical item");
        }
        ItemTable coating = main.First(row => row.ItemType == (int)ItemType.WeaponFashion
            && row.SubTypeParams.Count > 0 && row.SubTypeParams[0] > 0);
        string coatingKey = Guid.NewGuid().ToString("N");
        AssertEqual("inventory_limit", Error(await Add(coating.Id, 2)), "coating grants at most one");
        AssertBalance(Data(await Add(coating.Id, 1, coatingKey), "grant").GetProperty("balance"), 1, 0, "permanent coating ownership");
        var coatingPush = await account2.AwaitPush<NotifyWeaponFashionInfo>();
        AssertEqual(coating.SubTypeParams[0], coatingPush.WeaponFashionDataList.Single().Id, "mapped coating update");
        AssertEqual(0L, Count(2, coating.Id), "coating does not add a ticket stack");
        AssertEqual("inventory_limit", Error(await Add(coating.Id, 1)), "permanently owned coating is already at cap");

        account2.Dispose();
        for (int attempt = 0; attempt < 30; attempt++)
        {
            if (!(await Inspect(tokenId)).GetProperty("online").GetBoolean()) break;
            await Task.Delay(40);
        }
        AssertEqual("player_offline", Error(await Add(tokenId, 1)), "new offline grant is rejected");
        var resolved = Data(await server.Call("itemResolve", new { uid = 2, itemId = tokenId, quantity = 2, grantId = key }), "grant");
        AssertEqual(false, resolved.GetProperty("online").GetBoolean(), "offline resolution reads only a committed receipt");
        AssertEqual(initial2 + 2, resolved.GetProperty("balanceAfterGrant").GetInt64(), "original receipt records outcome");

        using var relog = new GmTestGame(gamePort);
        await relog.Login(uid2Token);
        AssertEqual(initial2 + 5, relog.LoginItems.Single(item => item.Id == tokenId).Count, "relogin contains granted items");
        int capacity = checked((int)(max - initial2 - 5));
        string cappedKey = Guid.NewGuid().ToString("N");
        AssertBalance(Data(await Add(tokenId, capacity, cappedKey), "grant").GetProperty("balance"), max, 0, "MAX reaches exact cap");
        AssertEqual("inventory_limit", Error(await Add(tokenId, 1)), "cap refuses additional grant");

        // Simulate losing the original reply across a process crash and retrying the same key.
        await server.Crash();
        using var restarted = new GmTestHost(origin, managedGm: true);
        await restarted.WaitReady(http, gamePort);
        using var afterRestart = new GmTestGame(gamePort);
        await afterRestart.Login(uid2Token);
        AssertEqual(max, afterRestart.LoginItems.Single(item => item.Id == tokenId).Count, "real host restart retains inventory");
        var replay = Data(await restarted.Call("itemAdd", new { uid = 2, itemId = tokenId, quantity = capacity, grantId = cappedKey }), "grant");
        AssertEqual(true, replay.GetProperty("replay").GetBoolean(), "retry after process restart uses durable receipt");
        AssertBalance(replay.GetProperty("balance"), max, 0, "post-crash cap");
        var coatingReplay = Data(await restarted.Call("itemAdd", new { uid = 2, itemId = coating.Id, quantity = 1, grantId = coatingKey }), "grant");
        AssertEqual(true, coatingReplay.GetProperty("replay").GetBoolean(), "coating receipt survives restart");
        AssertEqual(initial1, Count(1, tokenId), "UID 1 durable inventory unchanged");
        await restarted.Shutdown();
        Console.WriteLine($"Live GM items: {tokenName} {tokenId}, cap {max}. Two accounts, live NotifyItemDataList, mapped coatings, duplicate grants, offline checks, relog and host restart passed.");

        long Count(int uid, int itemId) => inventories.Find(row => row.Uid == uid).Single().Items.Where(item => item.Id == itemId).Sum(item => item.Count);
        async Task<JsonElement> Inspect(int itemId) => Data(await server.Call("itemInspect", new { uid = 2, itemId }), "item");
        Task<JsonElement> Add(int itemId, int quantity, string? grantId = null) => server.Call("itemAdd", new
        {
            uid = 2, itemId, quantity, grantId = grantId ?? Guid.NewGuid().ToString("N")
        });
        static string? Error(JsonElement reply) => reply.GetProperty("code").GetString();
        static JsonElement Data(JsonElement reply, string property)
        {
            if (!reply.GetProperty("ok").GetBoolean())
                throw new InvalidDataException($"Expected a successful {property} reply: {reply}");
            return reply.GetProperty(property);
        }
        static JsonElement ReadItem(JsonElement inspection) => inspection.GetProperty("details").GetProperty("item");
        static void AssertBalance(JsonElement balance, long owned, long capacity, string message)
        {
            AssertEqual(owned, balance.GetProperty("owned").GetInt64(), $"{message} owned");
            AssertEqual(capacity, balance.GetProperty("canAdd").GetInt64(), $"{message} available");
        }
    }
}
