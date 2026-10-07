using MongoDB.Bson.Serialization.Attributes;

namespace AscNet.Common.Database;

public sealed class ItemUsePendingOperation
{
    public string ClaimKey { get; set; } = string.Empty;
    public int ItemId { get; set; }
    public int Count { get; set; }
    public int RecycleTime { get; set; }
    public List<int> SelectRewardIds { get; set; } = new();
    public List<ItemUsePendingReward> Goods { get; set; } = new();
}

public sealed class ItemUsePendingReward
{
    public int Id { get; set; }
    public int TemplateId { get; set; }
    public int Count { get; set; }
    public List<int> Params { get; set; } = new();
}

public partial class Player
{
    [BsonElement("pending_item_use")]
    [BsonIgnoreIfNull]
    public ItemUsePendingOperation? PendingItemUse { get; set; }
}
