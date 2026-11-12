using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;

namespace AscNet.Common.Database;

public partial class Player
{
    // Currency paid through completed recharge products, in hundredths of Pay.ShowAmount.
    // FirstPayReward.NeedPayMoney uses the same unit in server tables.
    [BsonElement("total_pay_money")]
    public long TotalPayMoney { get; set; }

    [BsonElement("first_pay_reward_ids")]
    public List<int> FirstPayRewardIds { get; set; } = new();

    [BsonElement("cn_first_pay_client_version")]
    [BsonIgnoreIfNull]
    public string? CnFirstPayClientVersion { get; set; }

    [BsonElement("purchase_combo_buy_times")]
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<uint, int> PurchaseComboBuyTimes { get; set; } = new();

    [BsonElement("pending_purchase_combo")]
    public PlayerPendingCombo? PendingCombo { get; set; }
}

/// <summary>A validated bundle purchase awaiting its single atomic reward/cost application.</summary>
public sealed class PlayerPendingCombo
{
    public uint Id { get; set; }
    public int PreviousBuyTimes { get; set; }
    public long BuyTime { get; set; }
    public int ConsumeId { get; set; }
    public int ConsumeCount { get; set; }
    public List<PlayerPendingPurchase> Subs { get; set; } = new();
}
