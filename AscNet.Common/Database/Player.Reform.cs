using MongoDB.Bson.Serialization.Attributes;

namespace AscNet.Common.Database;

public sealed class ReformStageRecord
{
    [BsonElement("stage_id")] public int StageId { get; set; }
    [BsonElement("passed")] public bool Passed { get; set; }
    /// <summary>Best final wish mass of any win (the client's BalanceMaxWishQuality).</summary>
    [BsonElement("max_wish_quality")] public int MaxWishQuality { get; set; }
}

/// <summary>Dreamscape Disequilibrium (Reform Balance) progress; everything else is derived from tables.</summary>
public sealed class ReformState
{
    [BsonElement("stages")] public List<ReformStageRecord> Stages { get; set; } = new();
}

public partial class Player
{
    [BsonElement("reform")] public ReformState? Reform { get; set; }
}
