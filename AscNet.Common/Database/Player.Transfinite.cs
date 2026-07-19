using MongoDB.Bson.Serialization.Options;
using MongoDB.Bson.Serialization.Attributes;

namespace AscNet.Common.Database;

public sealed class TransfiniteState
{
    [BsonElement("activity_id")] public int ActivityId { get; set; }
    [BsonElement("activity_authorized_until")] public long ActivityAuthorizedUntil { get; set; }
    [BsonElement("circle_id")] public int CircleId { get; set; }
    [BsonElement("begin_time")] public long BeginTime { get; set; }
    [BsonElement("region_id")] public int RegionId { get; set; }
    [BsonElement("stage_group_index")] public int StageGroupIndex { get; set; }
    [BsonElement("stage_group_id")] public int StageGroupId { get; set; }
    [BsonElement("battle_info")] public TransfiniteBattleState? BattleInfo { get; set; }
    [BsonElement("best_spend_time")]
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> BestSpendTime { get; set; } = new();
    [BsonElement("got_score_reward_index")] public List<int> GotScoreRewardIndex { get; set; } = new();
    [BsonElement("send_activity_start_mail")] public bool SendActivityStartMail { get; set; }
    [BsonElement("max_rotate_stage_progress_index")] public int MaxRotateStageProgressIndex { get; set; }
    [BsonElement("last_modify_time")] public long LastModifyTime { get; set; }
    [BsonElement("score_reward_group_id")] public int ScoreRewardGroupId { get; set; }
    [BsonElement("rotate_settle_info")] public TransfiniteRotateSettleState? RotateSettleInfo { get; set; }
    [BsonElement("last_rotate_receipt")] public TransfiniteRotateSettleReceipt? LastRotateReceipt { get; set; }
}

public sealed class TransfiniteBattleState
{
    [BsonElement("stage_id")] public int StageId { get; set; }
    [BsonElement("stage_progress_index")] public int StageProgressIndex { get; set; }
    [BsonElement("score")] public int Score { get; set; }
    [BsonElement("passed_stage_ids")] public List<int> PassedStageIds { get; set; } = new();
}

public sealed class TransfiniteRotateSettleState
{
    [BsonElement("rotation_id")] public long RotationId { get; set; }
    [BsonElement("region_id")] public int RegionId { get; set; }
    [BsonElement("max_stage_progress_index")] public int MaxStageProgressIndex { get; set; }
    [BsonElement("score_reward_group_id")] public int ScoreRewardGroupId { get; set; }
    [BsonElement("settle_score")] public int SettleTransfiniteScore { get; set; }
    [BsonElement("unsettle_score")] public int UnSettleTransfiniteScore { get; set; }
    [BsonElement("reward_indices")] public List<int> GotScoreRewardIndex { get; set; } = new();
}

public sealed class TransfiniteRotateSettleReceipt
{
    [BsonElement("rotation_id")] public long RotationId { get; set; }
    [BsonElement("max_stage_progress_index")] public int MaxStageProgressIndex { get; set; }
    [BsonElement("settle_score")] public int SettleTransfiniteScore { get; set; }
    [BsonElement("unsettle_score")] public int UnSettleTransfiniteScore { get; set; }
    [BsonElement("reward_goods")] public List<TransfiniteRewardReceipt> RewardGoods { get; set; } = new();
}

public sealed class TransfiniteRewardReceipt
{
    [BsonElement("reward_type")] public int RewardType { get; set; }
    [BsonElement("template_id")] public int TemplateId { get; set; }
    [BsonElement("count")] public int Count { get; set; }
    [BsonElement("level")] public int Level { get; set; }
    [BsonElement("quality")] public int Quality { get; set; }
    [BsonElement("grade")] public int Grade { get; set; }
    [BsonElement("breakthrough")] public int Breakthrough { get; set; }
    [BsonElement("convert_from")] public int ConvertFrom { get; set; }
    [BsonElement("show_quality")] public int ShowQuality { get; set; }
    [BsonElement("is_gift")] public bool IsGift { get; set; }
    [BsonElement("reward_multi")] public int RewardMulti { get; set; }
    [BsonElement("id")] public int Id { get; set; }
}

public sealed class TransfiniteInventoryReceipt
{
    [BsonElement("activity_id")] public int ActivityId { get; set; }
    [BsonElement("rotation_id")] public long RotationId { get; set; }
    [BsonElement("region_id")] public int RegionId { get; set; }
    [BsonElement("score_reward_group_id")] public int ScoreRewardGroupId { get; set; }
    [BsonElement("max_stage_progress_index")] public int MaxStageProgressIndex { get; set; }
    [BsonElement("settle_score")] public int SettleTransfiniteScore { get; set; }
    [BsonElement("unsettle_score")] public int UnSettleTransfiniteScore { get; set; }
    [BsonElement("reward_goods")] public List<TransfiniteRewardReceipt> RewardGoods { get; set; } = new();
}

public partial class Inventory
{
    [BsonElement("transfinite_receipts")]
    public List<TransfiniteInventoryReceipt> TransfiniteReceipts { get; set; } = new();
}

public partial class Player
{
    [BsonElement("transfinite")] public TransfiniteState? Transfinite { get; set; }
}
