using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace AscNet.GameServer.Commands;

public sealed record GmItemBalance(long Owned, long CanAdd, long? CombinedOwned);
public sealed record GmItemSearchRow(GmItemMetadata Item, GmItemBalance? Balance);
public sealed record GmItemSearch(long Uid, bool Online, int Total, int Offset, IReadOnlyList<GmItemSearchRow> Rows);
public sealed record GmItemInspection(long Uid, bool Online, string? ConnectedClientVersion, GmItemDetails Details, GmItemBalance? Balance);
public sealed record GmItemGrant(long Uid, GmItemMetadata Item, GmItemBalance Balance, int Added, long BalanceAfterGrant,
    bool Replay, bool Online, bool UpdateQueued);

public static class GmItemService
{
    public static GmItemSearch Search(long uid, string? query, string? category, int offset)
    {
        var (total, page) = GmItemCatalog.Current.Search(query, category, offset);
        return GmPlayerService.WithOnlinePlayer(uid, session =>
        {
            bool online = IsReady(session);
            return new GmItemSearch(uid, online, total, offset, page.Select(entry =>
                new GmItemSearchRow(entry.Item, online ? Balance(entry, session!.inventory, session.character) : null)).ToArray());
        });
    }

    public static GmItemInspection Inspect(long uid, int itemId)
    {
        var entry = GmItemCatalog.Current.Find(itemId);
        return GmPlayerService.WithOnlinePlayer(uid, session =>
        {
            bool online = IsReady(session);
            return new GmItemInspection(uid, online, session?.ClientApplicationVersion, entry.Details,
                online ? Balance(entry, session!.inventory, session.character) : null);
        });
    }

    public static GmItemGrant Add(long uid, int itemId, int quantity, string? grantId, bool resolveOnly = false)
    {
        if (quantity <= 0)
            throw new GmOperationException("invalid_quantity", "Enter a positive quantity within the inventory limit.");
        if (!Guid.TryParseExact(grantId, "N", out Guid key) || key == Guid.Empty)
            throw new GmOperationException("invalid_grant_id", "The grant requires a valid retry ID.");
        string receiptKey = key.ToString("N");
        var catalog = GmItemCatalog.Current;
        var entry = catalog.Find(itemId);
        return GmPlayerService.WithOnlinePlayer(uid, session =>
        {
            bool online = IsReady(session);
            Inventory? inventory = online ? session!.inventory : Inventory.collection.Find(row => row.Uid == uid).SingleOrDefault();
            Character? character = online ? session!.character : Character.collection.Find(row => row.Uid == uid).SingleOrDefault();
            var receipt = inventory?.GmItemGrants?.GetValueOrDefault(receiptKey) ?? character?.GmItemGrants?.GetValueOrDefault(receiptKey);
            if (receipt is not null)
            {
                if (receipt.ItemId != itemId || receipt.Quantity != quantity)
                    throw new GmOperationException("grant_id_conflict", "This grant ID already belongs to another item or quantity for this UID.");
                bool pushed = online && PushCurrent(session!, entry);
                return Confirm(uid, entry, inventory, character, receipt, replay: true, online, pushed);
            }

            if (resolveOnly)
                throw new GmOperationException("grant_not_found", "No committed grant was found for this ID. Retry the original grant to apply it once.");
            if (!online)
                throw new GmOperationException("player_offline", "Player offline — log into PGR to add items.");
            catalog.RequireCurrent();
            if (entry.Item.GrantKind == "unavailable")
                throw new GmOperationException("unsupported_item", entry.Details.Behavior);

            GmItemBalance before = Balance(entry, inventory, character);
            if (before.CanAdd == 0)
                throw new GmOperationException("inventory_limit", entry.Details.WeaponFashionId.HasValue
                    ? "This weapon coating is already permanently unlocked." : "Inventory limit reached.");
            if (quantity > before.CanAdd)
                throw new GmOperationException("inventory_limit", $"Only {before.CanAdd:N0} can be added to this item.");

            receipt = new GmItemGrantReceipt(itemId, quantity, checked(before.Owned + quantity));
            if (entry.Details.WeaponFashionId is int fashionId)
                SaveCoating(session!, character!, fashionId, receiptKey, receipt);
            else
                SaveItem(session!, inventory!, receiptKey, receipt);
            bool updateQueued = PushCurrent(session!, entry);
            return Confirm(uid, entry, session!.inventory, session.character, receipt, replay: false, true, updateQueued);
        });
    }

    private static void SaveItem(Session session, Inventory original, string key, GmItemGrantReceipt receipt)
    {
        if (original.Items.Count(item => item.Id == receipt.ItemId) > 1)
            throw new GmOperationException("ambiguous_inventory", "The player has duplicate physical stacks for this ID.");
        Inventory staged = BsonSerializer.Deserialize<Inventory>(original.ToBson());
        Item item = staged.Do(receipt.ItemId, receipt.Quantity);
        if (item.Count != receipt.BalanceAfterGrant)
            throw new GmOperationException("inventory_limit", "The requested amount does not fit the physical inventory stack.");
        (staged.GmItemGrants ??= []).Add(key, receipt);
        try
        {
            staged.SaveChecked(journal: true);
            session.inventory = staged;
        }
        catch (Exception error)
        {
            session.inventory = RecoverSave(session, () => Inventory.collection.Find(row => row.Id == original.Id).SingleOrDefault(),
                saved => saved.GmItemGrants?.GetValueOrDefault(key), receipt, error);
        }
    }

    private static void SaveCoating(Session session, Character original, int fashionId, string key, GmItemGrantReceipt receipt)
    {
        Character staged = BsonSerializer.Deserialize<Character>(original.ToBson());
        session.character = staged;
        try
        {
            if (ItemGrantRules.UnlockCoating(session, fashionId) is null)
                throw new GmOperationException("unsupported_item", "This weapon coating could not be unlocked.");
            (staged.GmItemGrants ??= []).Add(key, receipt);
        }
        finally
        {
            session.character = original;
        }
        try
        {
            staged.SaveChecked(journal: true);
            session.character = staged;
        }
        catch (Exception error)
        {
            session.character = RecoverSave(session, () => Character.collection.Find(row => row.Id == original.Id).SingleOrDefault(),
                saved => saved.GmItemGrants?.GetValueOrDefault(key), receipt, error);
        }
    }

    private static T RecoverSave<T>(Session session, Func<T?> load, Func<T, GmItemGrantReceipt?> getReceipt,
        GmItemGrantReceipt expected, Exception writeError) where T : class
    {
        try
        {
            if (load() is { } saved && getReceipt(saved) == expected)
                return saved;
        }
        catch (Exception readError)
        {
            writeError = new AggregateException(writeError, readError);
        }
        session.DisconnectProtocol(persistState: false);
        throw new GmOperationException("save_unconfirmed", "The save outcome is unknown. Sign in again and retry the original grant ID.", writeError);
    }

    private static bool IsReady(Session? session) =>
        session is { IsDisconnected: false, inventory: not null, character: not null };

    private static GmItemGrant Confirm(long uid, GmItemCatalog.Entry entry, Inventory? inventory, Character? character,
        GmItemGrantReceipt receipt, bool replay, bool online, bool pushed) =>
        new(uid, entry.Item, Balance(entry, inventory, character), receipt.Quantity, receipt.BalanceAfterGrant, replay, online, pushed);

    private static GmItemBalance Balance(GmItemCatalog.Entry entry, Inventory? inventory, Character? character)
    {
        if (entry.Details.WeaponFashionId is int fashion)
        {
            long owned = character?.WeaponFashions.Any(row => row.Id == fashion && row.ExpireTime == 0) == true ? 1 : 0;
            return new(owned, 1 - owned, null);
        }
        int id = entry.Item.Id;
        long count = inventory?.Items.Where(row => row.Id == id).Sum(row => row.Count) ?? 0;
        long capacity = entry.Item.GrantKind == "stack"
            ? Math.Clamp(entry.Item.InventoryMax!.Value - Math.Max(0, count), 0, int.MaxValue) : 0;
        long? combined = entry.Details.CombinedIds.Count <= 1 || inventory is null ? null :
            Inventory.IsBlackCard(id) ? inventory.SpendableCount(id) : inventory.CombinedCount(id, DateTimeOffset.UtcNow);
        return new(count, capacity, combined);
    }

    private static bool PushCurrent(Session session, GmItemCatalog.Entry entry)
    {
        if (entry.Details.WeaponFashionId is int fashion)
        {
            if (session.character.WeaponFashions.Find(row => row.Id == fashion) is not { } coating)
                return false;
            ItemGrantRules.PushCoating(session, coating);
        }
        else
        {
            Item item = session.inventory.Items.FirstOrDefault(row => row.Id == entry.Item.Id)
                ?? new Item { Id = entry.Item.Id, Count = 0 };
            session.SendPush(new NotifyItemDataList { ItemDataList = [item] });
        }
        return true;
    }
}
