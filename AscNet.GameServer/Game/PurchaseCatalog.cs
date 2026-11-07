using AscNet.Common.Util;
using Newtonsoft.Json.Linq;

namespace AscNet.GameServer.Game;

/// <summary>Server-owned package definitions, independent of player purchase state.</summary>
public sealed class PurchaseCatalog
{
    private readonly Dictionary<uint, JObject> entries = new();
    private readonly Dictionary<uint, JObject> combos = new();
    private static readonly string[] PlayerFields =
    [
        "BuyTimes", "LastBuyTime", "DailyRewardRemainDay", "BuyLimitRemainDay",
        "IsDailyRewardGet", "DailyRewardSupplementGetData", "DiscountCouponInfos", "ConvertSwitch"
    ];

    public static PurchaseCatalog Load(string path) => Parse(File.ReadAllText(path));

    public static PurchaseCatalog Parse(string json)
    {
        JObject root = JObject.Parse(json);
        if (root.Value<int>("SchemaVersion") != 1 || root["Purchases"] is not JArray purchases)
            throw new InvalidDataException("Purchase catalog requires SchemaVersion 1 and Purchases; response snapshots are not supported.");
        PurchaseCatalog catalog = new();
        foreach (JToken token in purchases)
        {
            if (token is not JObject entry)
                throw new InvalidDataException("Purchase catalog entries must be objects.");
            uint id = entry.Value<uint>("Id");
            if (id == 0 || id > int.MaxValue || !catalog.entries.TryAdd(id, entry))
                throw new InvalidDataException($"Invalid or duplicate purchase Id {id}.");
            if (entry.Value<int>("UiType") <= 0 || entry["ConsumeCount"] is null
                || entry.Value<int>("ConsumeCount") < 0 || entry["ConsumeId"] is null
                || entry.Value<int>("ConsumeId") < 0)
                throw new InvalidDataException($"Purchase {id} requires a UI type and nonnegative price/currency.");
            if (PlayerFields.Any(field => entry.Property(field) is not null)
                || (entry["PurchaseSignInInfo"] as JObject)?.Property("PurchaseSignInData") is not null)
                throw new InvalidDataException($"Purchase {id} contains player state instead of catalog data.");
            foreach (string field in new[] { "TimeToShelve", "TimeToUnShelve", "TimeToInvalid" })
                if (entry[field] is null || entry.Value<long>(field) < 0)
                    throw new InvalidDataException($"Purchase {id} requires nonnegative {field} (0 means unrestricted).");
            if (entry["NormalDiscounts"] is JObject discounts)
                foreach (JProperty discount in discounts.Properties())
                    if (!int.TryParse(discount.Name, out int purchaseNumber) || purchaseNumber <= 0
                        || discount.Value.Value<int>() is < 0 or > 10000)
                        throw new InvalidDataException($"Purchase {id} has an invalid discount tier.");
            if (entry["DailyRewardDays"] is not null && entry.Value<int>("DailyRewardDays") <= 0)
                throw new InvalidDataException($"Purchase {id} has an invalid daily reward duration.");
            // The client resolves monthly passes by its own region's package ids (PurchasePackageYKUiConfig), so a pass
            // is listed only to the regions named here; no field = every region.
            if (entry["Regions"] is not null
                && (entry["Regions"] is not JArray regions || regions.Count == 0 || regions.Any(region => region.Type != JTokenType.String)))
                throw new InvalidDataException($"Purchase {id} has an invalid Regions list.");
        }
        // Optional server-owned bundles (client XPurchaseComboDataForClient). No combo definitions
        // ship with the client, so none are authored here; the list may stay empty.
        if (root["Combos"] is JArray comboRows)
            foreach (JToken token in comboRows)
            {
                if (token is not JObject combo)
                    throw new InvalidDataException("Purchase combos must be objects.");
                uint id = combo.Value<uint>("Id");
                if (id == 0 || id > int.MaxValue || catalog.entries.ContainsKey(id) || !catalog.combos.TryAdd(id, combo))
                    throw new InvalidDataException($"Invalid or duplicate purchase combo Id {id}.");
                if (combo.Property("BuyTimes") is not null || combo.Property("ComboInfo") is not null)
                    throw new InvalidDataException($"Combo {id} contains player state instead of catalog data.");
                if (combo["SubPackageDiscounts"] is not JObject subs || !subs.HasValues
                    || combo.Value<int>("UiType") <= 0 || combo["ConsumeId"] is null || combo.Value<int>("ConsumeId") < 0
                    || combo["BuyLimitParam"] is null || combo.Value<int>("BuyLimitParam") < 0)
                    throw new InvalidDataException($"Combo {id} requires a UI type, currency, buy limit and sub-packages.");
                foreach (string field in new[] { "TimeToShelve", "TimeToUnShelve", "TimeToInvalid" })
                    if (combo[field] is null || combo.Value<long>(field) < 0)
                        throw new InvalidDataException($"Combo {id} requires nonnegative {field} (0 means unrestricted).");
                foreach (JProperty sub in subs.Properties())
                {
                    if (!uint.TryParse(sub.Name, out uint subId) || !catalog.entries.TryGetValue(subId, out JObject? package)
                        || package.Value<int>("UiType") != combo.Value<int>("UiType")
                        || package.Value<int>("ConsumeId") != combo.Value<int>("ConsumeId")
                        || sub.Value.Type != JTokenType.Integer
                        || sub.Value.Value<int>() < 0 || sub.Value.Value<int>() > package.Value<int>("ConsumeCount")
                        || package.Value<int>("ConsumeCount") <= 0)
                        throw new InvalidDataException($"Combo {id} has an invalid sub-package {sub.Name}.");
                }
            }
        return catalog;
    }

    /// <summary>Whether a package is listed/purchasable for a client region (null = EN, the shared tables).</summary>
    public bool IsVisible(uint id, string? region) => entries.TryGetValue(id, out JObject? entry) && IsVisible(entry, region);

    private static bool IsVisible(JObject entry, string? region) =>
        entry["Regions"] is not JArray regions || regions.Any(name => (string?)name == (string.IsNullOrEmpty(region) ? "en" : region));

    public List<dynamic> List(IEnumerable<int>? uiTypes, long now, string? region = null)
    {
        HashSet<int>? requested = uiTypes?.ToHashSet();
        return entries.Values
            .Where(entry => entry.Value<bool?>("Enabled") != false
                && IsVisible(entry, region)
                && (requested is null || requested.Contains(entry.Value<int>("UiType")))
                && !HasEnded(entry.Value<long>("TimeToInvalid"), now)
                && !HasEnded(entry.Value<long>("TimeToUnShelve"), now))
            .Select(entry => (dynamic)ToPurchaseInfo(entry)).ToList();
    }

    public Dictionary<dynamic, dynamic>? Find(uint id) =>
        entries.TryGetValue(id, out JObject? entry) && entry.Value<bool?>("Enabled") != false
            ? ToPurchaseInfo(entry) : null;

    public int DailyRewardDays(uint id) => entries.TryGetValue(id, out JObject? entry)
        ? entry.Value<int?>("DailyRewardDays") ?? 0 : 0;

    public List<dynamic> ListCombos(IEnumerable<int>? uiTypes, long now)
    {
        HashSet<int>? requested = uiTypes?.ToHashSet();
        return combos.Values
            .Where(combo => combo.Value<bool?>("Enabled") != false
                && (requested is null || requested.Contains(combo.Value<int>("UiType")))
                && !HasEnded(combo.Value<long>("TimeToInvalid"), now)
                && !HasEnded(combo.Value<long>("TimeToUnShelve"), now))
            .Select(combo => (dynamic)ToComboInfo(combo)).ToList();
    }

    public Dictionary<dynamic, dynamic>? FindCombo(uint id) =>
        combos.TryGetValue(id, out JObject? combo) && combo.Value<bool?>("Enabled") != false
            ? ToComboInfo(combo) : null;

    private static Dictionary<dynamic, dynamic> ToComboInfo(JObject combo)
    {
        Dictionary<dynamic, dynamic> info = JsonSnapshot.ReadDynamic(combo)!;
        info.Remove("Enabled");
        return info;
    }

    private static bool HasEnded(long end, long now) => end > 0 && end <= now;

    private static Dictionary<dynamic, dynamic> ToPurchaseInfo(JObject entry)
    {
        // A fresh recursive copy prevents a player's state from leaking into the catalog.
        Dictionary<dynamic, dynamic> info = JsonSnapshot.ReadDynamic(entry)!;
        info.Remove("Enabled");
        info.Remove("DailyRewardDays");
        info.Remove("Regions");
        // No ownership-based price reduction is configured; client discount tiers apply separately.
        info["ConvertSwitch"] = entry.Value<int>("ConsumeCount");
        info["DiscountCouponInfos"] = null!;
        return info;
    }

    public static int AvailabilityCode(Dictionary<dynamic, dynamic> info, long now)
    {
        long start = Convert.ToInt64((object)info["TimeToShelve"]);
        long end = Convert.ToInt64((object)info["TimeToUnShelve"]);
        long invalid = Convert.ToInt64((object)info["TimeToInvalid"]);
        if (start > now) return 20053002;
        if (HasEnded(invalid, now)) return 20053003;
        return HasEnded(end, now) ? 20053004 : 0;
    }

    public static int UnitPrice(Dictionary<dynamic, dynamic> info, int bought)
    {
        int price = Convert.ToInt32((object)info["ConsumeCount"]);
        int basisPoints = 10000;
        int selectedTier = 0;
        if (info.TryGetValue("NormalDiscounts", out dynamic? raw) && raw is Dictionary<dynamic, dynamic> discounts)
            foreach (var tier in discounts)
            {
                int number = Convert.ToInt32((object)tier.Key);
                if (number <= (long)bought + 1 && number > selectedTier)
                {
                    selectedTier = number;
                    basisPoints = Convert.ToInt32((object)tier.Value);
                }
            }
        return checked((int)((long)price * basisPoints / 10000));
    }
}
