using MongoDB.Bson.Serialization.Attributes;

namespace AscNet.Common.Database;

/// <summary>Main-line chapter 13 (Fake Ascension) TRPG progress. Normal-mode stage passes live in the stage
/// document and are derived from it; this holds what only the TRPG layer knows.</summary>
[BsonIgnoreExtraElements]
public sealed class TrpgState
{
    /// <summary>0 = never chosen; login then sends the first main-line target chain.</summary>
    [BsonElement("cur_target_link")] public int CurTargetLink { get; set; }
    [BsonElement("targets")] public List<int> Targets { get; set; } = new();
    [BsonElement("rewards")] public List<int> Rewards { get; set; } = new();
    [BsonElement("functions")] public List<int> Functions { get; set; } = new();
    [BsonElement("memoirs")] public List<int> Memoirs { get; set; } = new();
    /// <summary>False = exploration page, true = regular (normal) main line page.</summary>
    [BsonElement("is_normal_page")] public bool IsNormalPage { get; set; }
}

public partial class Player
{
    [BsonElement("trpg")] public TrpgState Trpg { get; set; } = new();
}
