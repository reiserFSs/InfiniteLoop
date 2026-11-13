using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.Util;
using AscNet.GameServer.Handlers;
using AscNet.GameServer.Handlers.Drops;
using AscNet.Table.V2.share.bigworld.common.item;
using AscNet.Table.V2.share.item;

namespace AscNet.GameServer.Commands;

public sealed record GmItemCategory(string Key, string Name);
public sealed record GmItemMetadata(int Id, string? Name, string Category, string CategoryName, int ItemType,
    long? InventoryMax, long? BulkMax, string GrantKind);
public sealed record GmItemDetails(GmItemMetadata Item, string? Description, string? Icon, string Source,
    string ClientVersion, int[] SubtypeParameters, int? WeaponFashionId, IReadOnlyList<int> CombinedIds,
    int? GiftRewardId, string Behavior);
public sealed record GmItemCatalogInfo(string Revision, string ClientVersion, bool Stale, int Total,
    IReadOnlyList<GmItemCategory> Categories, IReadOnlyList<string> Languages);

/// <summary>One immutable, versioned index of the item tables the server loaded.</summary>
public sealed class GmItemCatalog
{
    private const string VersionManifest = "table/manifest.json";
    private static readonly Lazy<GmItemCatalog> Catalog = new(() => new GmItemCatalog());
    private readonly Dictionary<int, Entry> byId;
    private readonly Entry[] entries;
    private readonly Dictionary<string, string> sourceHashes;
    private readonly string revision;
    private readonly string clientVersion;
    private readonly GmItemCategory[] categories;
    private readonly string[] languages;

    public static GmItemCatalog Current => Catalog.Value;

    private GmItemCatalog()
    {
        var manifest = JsonSnapshot.LoadObject(VersionManifest);
        clientVersion = (string?)manifest["client49Import"]?["applicationVersion"] ?? "Unknown";
        var overlays = ClientRegions.GateRegions
            .Select(region => (Region: region, Items: LocalizedNames<ItemTable>(region, item => item.Id, item => item.Name),
                BigWorld: LocalizedNames<BigWorldItemTable>(region, item => item.Id, item => item.Name)))
            .Where(overlay => overlay.Items.Count > 0 || overlay.BigWorld.Count > 0)
            .ToArray();
        languages = [ClientRegions.Default, .. overlays.Select(overlay => overlay.Region)];

        string[] sources = [ItemTable.File, BigWorldItemTable.File, ItemCombineTable.File, VersionManifest,
            .. overlays.SelectMany(overlay => new[] { RegionPath(overlay.Region, ItemTable.File), RegionPath(overlay.Region, BigWorldItemTable.File) })
                .Where(path => File.Exists(JsonSnapshot.ResolvePath(path)))];
        sourceHashes = sources.ToDictionary(path => path, ReadSourceHash);
        revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('\n', sourceHashes.OrderBy(source => source.Key, StringComparer.Ordinal).Select(source => $"{source.Key} {source.Value}")))))
            .ToLowerInvariant();

        List<ItemTable> items = TableReaderV2.Parse<ItemTable>();
        if (items.Count == 0)
            throw new InvalidDataException("The authoritative Item table could not be loaded.");
        byId = items.ToDictionary(item => item.Id, item => MainItem(item, overlays
            .Select(overlay => overlay.Items.GetValueOrDefault(item.Id)).OfType<string>()));
        foreach (BigWorldItemTable item in TableReaderV2.Parse<BigWorldItemTable>())
        {
            // The item API resolves a normal Item row first when an ID is shared.
            if (!byId.ContainsKey(item.Id))
                byId.Add(item.Id, BigWorldItem(item, overlays.Select(overlay => overlay.BigWorld.GetValueOrDefault(item.Id)).OfType<string>()));
        }
        entries = byId.Values.OrderBy(entry => entry.Item.Name ?? "\uffff", StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Item.Id).ToArray();
        categories = entries.Select(entry => new GmItemCategory(entry.Item.Category, entry.Item.CategoryName))
            .Distinct().OrderBy(category => category.Name, StringComparer.Ordinal).ToArray();
    }

    public GmItemCatalogInfo Info() => new(revision, clientVersion, IsStale(), entries.Length, categories, languages);
    public IEnumerable<GmItemDetails> Export() => entries.Select(entry => entry.Details);

    internal Entry Find(int itemId) => byId.GetValueOrDefault(itemId)
        ?? throw new GmOperationException("invalid_item", $"Item ID {itemId} is not in the supported catalog.");

    internal void RequireCurrent()
    {
        if (IsStale())
            throw new GmOperationException("catalog_stale", "The item tables changed on disk. Restart the launcher-managed server to reload the catalog.");
    }

    internal (int Total, Entry[] Page) Search(string? query, string? category, int offset)
    {
        if (query is { Length: > 120 } || offset < 0
            || (category is not null && !categories.Any(value => value.Key == category)))
            throw new GmOperationException("invalid_search", "Enter up to 120 characters and select an available category.");

        string text = query?.Trim() ?? "";
        IEnumerable<Entry> filtered = entries;
        if (category is not null)
            filtered = filtered.Where(entry => entry.Item.Category == category);
        if (text.Length > 0 && text.All(char.IsAsciiDigit))
        {
            Entry[] numeric = int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int id)
                ? filtered.Where(entry => entry.Item.Id == id).ToArray() : [];
            return (numeric.Length, numeric.Skip(offset).ToArray());
        }

        string normalized = Normalize(text);
        if (normalized.Length > 0)
        {
            string[] tokens = normalized.Split(' ');
            filtered = filtered.Select(entry => (Entry: entry, Rank: entry.Names.Select(name => Rank(name, normalized, tokens)).DefaultIfEmpty(-1).Max()))
                .Where(match => match.Rank >= 0).OrderByDescending(match => match.Rank).Select(match => match.Entry);
        }
        Entry[] all = filtered.ToArray();
        return (all.Length, all.Skip(offset).Take(50).ToArray());
    }

    private Entry MainItem(ItemTable row, IEnumerable<string> aliases)
    {
        int type = row.ItemType;
        var category = Category(type);
        var combined = ItemGrantRules.CombinedIds(row.Id);
        long? max = Inventory.GetMaxCount(row);
        long? bulk = ItemGrantRules.BatchLimit(row);
        int? fashion = null;
        int? gift = null;
        string kind = "stack";
        string behavior = "Adds to this item's inventory stack.";
        if (type == (int)ItemType.WeaponFashion)
        {
            if (RewardHandler.TryResolveWeaponFashionReward(row, out int fashionId))
            {
                kind = "weaponCoating";
                fashion = fashionId;
                max = bulk = 1;
                behavior = "Unlocks a permanent weapon coating. Owned shows permanent ownership.";
            }
            else
            {
                kind = "unavailable";
                max = bulk = null;
                behavior = "The coating ticket has no verified weapon fashion mapping.";
            }
        }
        else if (combined.Count > 1)
        {
            kind = "unavailable";
            behavior = "This balance combines multiple physical IDs. Owned and Max refer to this ID. Use the existing currency-specific grant when available.";
        }
        else if (row.IsHidden() || !category.Known)
        {
            kind = "unavailable";
            behavior = DropsHandlerFactory.GetDropHandler(row.Id) is not null
                ? "Hidden reward token with a dedicated settlement handler."
                : "The item type has no verified direct grant behavior in the launcher.";
        }
        else if (type == (int)ItemType.Gift)
        {
            gift = row.SubTypeParams.Count >= 2 && row.SubTypeParams[1] > 0 ? row.SubTypeParams[1] : null;
            behavior = row.SubTypeParams.FirstOrDefault() == 3
                ? "Adds unopened selection packs. Choose their contents in PGR."
                : "Adds unopened gifts. Open them in PGR where supported.";
        }
        return Build(row.Id, row.Name, row.Description, row.BigIcon, row.Icon, ItemTable.File, type,
            category.Key, category.Label, max, bulk, kind, row.SubTypeParams.ToArray(), fashion, combined, gift, behavior, aliases);
    }

    private Entry BigWorldItem(BigWorldItemTable row, IEnumerable<string> aliases) =>
        Build(row.Id, row.Name, row.Description, row.BigIcon, row.Icon, BigWorldItemTable.File, row.ItemType,
            "bigWorld", "BigWorld", Inventory.GetMaxCount(null), null, "unavailable", row.SubTypeParams.ToArray(), null,
            [row.Id], null, "Mode item. Granting is handled by BigWorld progression.", aliases);

    private Entry Build(int id, string? name, string? description, string? bigIcon, string? icon, string source, int type,
        string category, string label, long? max, long? bulk, string kind, int[] subtype, int? fashion, IReadOnlyList<int> combined,
        int? gift, string behavior, IEnumerable<string> aliases)
    {
        var metadata = new GmItemMetadata(id, AuthoredText(name), category, label, type, max, bulk, kind);
        var details = new GmItemDetails(metadata, AuthoredText(description), AuthoredText(bigIcon) ?? AuthoredText(icon),
            source.Replace('\\', '/'), clientVersion, subtype, fashion, combined, gift, behavior);
        return new Entry(metadata, details, new[] { metadata.Name }.OfType<string>().Concat(aliases).Select(Normalize)
            .Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).ToArray());
    }

    private static (string Key, string Label, bool Known) Category(int type) => (ItemType)type switch
    {
        ItemType.Money => ("currency", "Currency", true),
        ItemType.ExchangeMoney or ItemType.SpExchangeMoney => ("exchange", "Exchange currency", true),
        ItemType.ActiveMoney or ItemType.PlayingMoney => ("event", "Event currency", true),
        ItemType.Gift => ("gift", "Gift / selection pack", true),
        ItemType.WeaponFashion => ("coating", "Weapon coating", true),
        ItemType.FurnitureItem => ("furniture", "Furniture item", true),
        ItemType.Fragment or ItemType.EquipExpNotInBag => ("fragment", "Fragment", true),
        ItemType.FavorGift => ("affection", "Affection gift", true),
        ItemType.Assert or ItemType.NormalConsumableItem => ("consumable", "Consumable / resource", true),
        ItemType.Material or ItemType.CardExp or ItemType.EquipExp or ItemType.EquipResonanace or ItemType.PartnerExp
            => ("material", "Material", true),
        ItemType.PlayingItem or ItemType.TRPGItem => ("mode", "Mode item", true),
        ItemType.UnShow => ("hidden", "Hidden reward", false),
        _ => ("other", "Other", false)
    };

    private static Dictionary<int, string> LocalizedNames<T>(string region, Func<T, int> id, Func<T, string?> name)
        where T : ITable => File.Exists(JsonSnapshot.ResolvePath(RegionPath(region, T.File)))
            ? TableReaderV2.Parse<T>(region).Where(row => AuthoredText(name(row)) is not null).ToDictionary(id, row => name(row)!)
            : [];

    private static string RegionPath(string region, string file) => Path.Combine("regions", region, file);
    private static string? AuthoredText(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static string Normalize(string value) => string.Join(' ', new string(value.Normalize(NormalizationForm.FormKC)
        .Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ').ToArray()).Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static int Rank(string name, string query, string[] tokens)
    {
        if (name == query) return 4;
        if (name.StartsWith(query, StringComparison.Ordinal)) return 3;
        if (name.Contains(query, StringComparison.Ordinal)) return 2;
        string[] words = name.Split(' ');
        return tokens.All(token => name.Contains(token, StringComparison.Ordinal)
            || token.Length >= 4 && words.Any(word => OneEditApart(word, token))) ? 1 : -1;
    }

    private static bool OneEditApart(string left, string right)
    {
        if (Math.Abs(left.Length - right.Length) > 1) return false;
        if (left.Length > right.Length) (left, right) = (right, left);
        int index = 0;
        while (index < left.Length && left[index] == right[index]) index++;
        if (index == left.Length) return true;
        if (left.Length < right.Length) return left.AsSpan(index).SequenceEqual(right.AsSpan(index + 1));
        return left.AsSpan(index + 1).SequenceEqual(right.AsSpan(index + 1))
            || index + 1 < left.Length && left[index] == right[index + 1] && left[index + 1] == right[index]
            && left.AsSpan(index + 2).SequenceEqual(right.AsSpan(index + 2));
    }

    private static string ReadSourceHash(string file)
    {
        string path = JsonSnapshot.ResolvePath(file);
        if (!File.Exists(path)) return "";
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private bool IsStale() => sourceHashes.Any(source => ReadSourceHash(source.Key) != source.Value)
        || ClientRegions.GateRegions.Any(region => new[] { ItemTable.File, BigWorldItemTable.File }
            .Select(file => RegionPath(region, file))
            .Any(path => !sourceHashes.ContainsKey(path) && File.Exists(JsonSnapshot.ResolvePath(path))));

    internal sealed record Entry(GmItemMetadata Item, GmItemDetails Details, string[] Names);
}
