using MongoDB.Bson.Serialization.Attributes;

namespace AscNet.Common.Database;

// The receipt belongs to the same document as the mutation it confirms.
public sealed record GmItemGrantReceipt(int ItemId, int Quantity, long BalanceAfterGrant);

public partial class Inventory
{
    [BsonElement("gm_item_grants")]
    [BsonIgnoreIfNull]
    public Dictionary<string, GmItemGrantReceipt>? GmItemGrants { get; set; }
}

public partial class Character
{
    [BsonElement("gm_item_grants")]
    [BsonIgnoreIfNull]
    public Dictionary<string, GmItemGrantReceipt>? GmItemGrants { get; set; }
}
