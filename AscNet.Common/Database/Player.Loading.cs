using AscNet.Common.MsgPack;
using MongoDB.Bson.Serialization.Attributes;

namespace AscNet.Common.Database;

public partial class Player
{
    [BsonElement("loading_option")]
    public LoadingOptionData LoadingOption { get; set; } = new();
}
