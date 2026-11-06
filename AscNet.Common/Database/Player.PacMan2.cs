using MongoDB.Bson.Serialization.Attributes;

namespace AscNet.Common.Database;

public sealed class PacMan2StageRecord
{
    [BsonElement("stage_id")] public int StageId { get; set; }
    [BsonElement("highest_score")] public int HighestScore { get; set; }
}

/// <summary>Munchy Pixels progress. Stars are derived from HighestScore and the stage's authored thresholds.</summary>
public sealed class PacMan2State
{
    [BsonElement("records")] public List<PacMan2StageRecord> Records { get; set; } = new();
    // The run opened by PacMan2StageStartRequest and not yet settled (0 = none).
    [BsonElement("playing_stage_id")] public int PlayingStageId { get; set; }
    [BsonElement("playing_start_unix")] public long PlayingStartUnix { get; set; }
}

public partial class Player
{
    [BsonElement("pacman2")] public PacMan2State? PacMan2 { get; set; }
}
