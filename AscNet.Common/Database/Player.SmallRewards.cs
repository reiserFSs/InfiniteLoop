using MongoDB.Bson.Serialization.Attributes;

namespace AscNet.Common.Database;

public partial class Player
{
    [BsonElement("version_gift")]
    public VersionGiftState VersionGift { get; set; } = new();

    [BsonElement("accumulate_expend_shop")]
    public AccumulateExpendShopState AccumulateExpendShop { get; set; } = new();
}

public sealed class VersionGiftState
{
    [BsonElement("activity_id")]
    public int ActivityId { get; set; }

    // The daily gift counts as claimed until this unix time (next daily reset after the claim).
    [BsonElement("daily_claimed_until")]
    public long DailyClaimedUntil { get; set; }

    [BsonElement("version_claimed")]
    public bool VersionClaimed { get; set; }

    // 1-based milestone indexes into VersionGiftProcess, as the client's progress grid reads them.
    [BsonElement("progress_indexes")]
    public List<int> ProgressIndexes { get; set; } = new();
}

public sealed class AccumulateExpendShopState
{
    [BsonElement("activity_id")]
    public int ActivityId { get; set; }

    // Inventory.SpentTotals of the shop's item when the state was created; only later spend counts.
    [BsonElement("spent_baseline")]
    public long SpentBaseline { get; set; }

    // Spend already converted into shop tokens (in spent-item units, not tokens).
    [BsonElement("converted_count")]
    public int ConvertedCount { get; set; }

    [BsonElement("sign_count")]
    public int SignCount { get; set; }

    [BsonElement("signed_until")]
    public long SignedUntil { get; set; }
}
