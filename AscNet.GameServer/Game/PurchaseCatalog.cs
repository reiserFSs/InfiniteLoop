using AscNet.Common.Util;
using Newtonsoft.Json.Linq;

namespace AscNet.GameServer.Game;

/// <summary>Server-owned package definitions, independent of player purchase state.</summary>
public sealed class PurchaseCatalog
{
    private readonly Dictionary<uint, JObject> entries = new();
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
        }
        return catalog;
    }

    public List<dynamic> List(IEnumerable<int>? uiTypes, long now)
    {
        HashSet<int>? requested = uiTypes?.ToHashSet();
        return entries.Values
            .Where(entry => entry.Value<bool?>("Enabled") != false
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

    private static bool HasEnded(long end, long now) => end > 0 && end <= now;

    private static Dictionary<dynamic, dynamic> ToPurchaseInfo(JObject entry)
    {
        // A fresh recursive copy prevents a player's state from leaking into the catalog.
        Dictionary<dynamic, dynamic> info = JsonSnapshot.ReadDynamic(entry)!;
        info.Remove("Enabled");
        info.Remove("DailyRewardDays");
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
