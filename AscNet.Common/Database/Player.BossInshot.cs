using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;

namespace AscNet.Common.Database;

public sealed class BossInshotState
{
    [BsonElement("activity_id")] public int ActivityId { get; set; }
    [BsonElement("authorized_activity_id")] public int AuthorizedActivityId { get; set; }
    [BsonElement("authorized_time_ids")] public List<int> AuthorizedTimeIds { get; set; } = new();
    [BsonElement("is_pass_teach")] public bool IsPassTeach { get; set; }
    [BsonElement("pass_stages")] public List<BossInshotPassStage> PassStages { get; set; } = new();
    [BsonElement("boss_unlocks")] public List<BossInshotBossUnlock> BossUnlocks { get; set; } = new();
    [BsonElement("characters")] public List<BossInshotCharacterState> Characters { get; set; } = new();
    [BsonElement("current_tower_id")] public int CurrentTowerId { get; set; }
    [BsonElement("towers")] public List<BossInshotTowerState> Towers { get; set; } = new();
    [BsonElement("rank_projection_version")] public int RankProjectionVersion { get; set; }
    [BsonElement("pending_rank_updates")] public List<BossInshotRankUpdate> PendingRankUpdates { get; set; } = new();
}

public sealed class BossInshotRankUpdate
{
    [BsonElement("activity_id")] public int ActivityId { get; set; }
    [BsonElement("boss_id")] public int BossId { get; set; }
    [BsonElement("character_id")] public int CharacterId { get; set; }
    [BsonElement("tower_id")] public int TowerId { get; set; }
    [BsonElement("score")] public int Score { get; set; }
    [BsonElement("achieved_at")] public long AchievedAt { get; set; }
}

public sealed class BossInshotPassStage
{
    [BsonElement("stage_id")] public int StageId { get; set; }
    [BsonElement("max_score")] public int MaxScore { get; set; }
}

public sealed class BossInshotBossUnlock
{
    [BsonElement("boss_id")] public int BossId { get; set; }
    [BsonElement("difficulties")] public List<int> DifficultySet { get; set; } = new();
}

public sealed class BossInshotCharacterState
{
    [BsonElement("character_id")] public int CharacterId { get; set; }
    [BsonElement("selected_talents")] public List<int> SelectTalentIds { get; set; } = new();
    [BsonElement("total_score")] public int TotalScore { get; set; }
    [BsonElement("pass_stages")] public List<int> PassStageSet { get; set; } = new();
    [BsonElement("stage_max_scores")]
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> StageMaxScores { get; set; } = new();
}

public sealed class BossInshotTowerState
{
    [BsonElement("tower_id")] public int TowerId { get; set; }
    [BsonElement("draw_stage_ids")] public List<int> DrawStageIds { get; set; } = new();
    [BsonElement("select_stage_id")] public int SelectStageId { get; set; }
    [BsonElement("select_stage_after_pass")] public int SelectStageIdAfterAllPass { get; set; }
    [BsonElement("is_pass")] public bool IsPass { get; set; }
    [BsonElement("protect_count")] public int TriggerProtectCount { get; set; }
    [BsonElement("scores")] public List<BossInshotTowerScoreState> Scores { get; set; } = new();
}

public sealed class BossInshotTowerScoreState
{
    [BsonElement("boss_id")] public int BossId { get; set; }
    [BsonElement("character_id")] public int CharacterId { get; set; }
    [BsonElement("max_score")] public int MaxScore { get; set; }
    [BsonElement("achieved_at")] public long AchievedAt { get; set; }
}

public partial class Player
{
    [BsonElement("boss_inshot")]
    public BossInshotState? BossInshot { get; set; }
}
