using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;

namespace AscNet.Common.Database;

public partial class Player
{
    [BsonElement("purchase_last_buy_times")]
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<uint, long> PurchaseLastBuyTimes { get; set; } = new();

    [BsonElement("pending_purchase")]
    public PlayerPendingPurchase? PendingPurchase { get; set; }

    [BsonElement("purchase_daily_passes")]
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<uint, PlayerPurchaseDailyPass> PurchaseDailyPasses { get; set; } = new();

    [BsonElement("pending_recharge")]
    public PlayerPendingRecharge? PendingRecharge { get; set; }

    [BsonElement("recharge_sequence")]
    public long RechargeSequence { get; set; }

    [BsonElement("purchase_period_buy_times")]
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<uint, int> PurchasePeriodBuyTimes { get; set; } = new();
}

public sealed class PlayerPurchaseDailyPass
{
    public long EndDay { get; set; }
    public long StartDay { get; set; }
    public List<int> RewardIndexList { get; set; } = new();
    public long LastClaimDay { get; set; } = -1;
}

public sealed class PlayerPendingRecharge
{
    public string Key { get; set; } = "";
    public string Order { get; set; } = "";
    public int Count { get; set; }
}

public sealed class PlayerPendingPurchase
{
    public uint Id { get; set; }
    public int Count { get; set; }
    public int PreviousBuyTimes { get; set; }
    public int PeriodBuyTimes { get; set; }
    public long BuyTime { get; set; }
    public int ConsumeId { get; set; }
    public int ConsumeCount { get; set; }
    public List<AscNet.Common.MsgPack.RewardGoods> Goods { get; set; } = new();
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<uint, PlayerPurchaseDailyPass> DailyPasses { get; set; } = new();
}
