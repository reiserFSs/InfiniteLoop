using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;

namespace AscNet.Common.Database;

[BsonIgnoreExtraElements]
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
    [BsonElement("best_spend_time"), BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)] public Dictionary<int, int> BestSpendTime { get; set; } = new();
    [BsonElement("got_score_reward_index")] public List<int> GotScoreRewardIndex { get; set; } = new();
    [BsonElement("send_activity_start_mail")] public bool SendActivityStartMail { get; set; }
    [BsonElement("max_rotate_stage_progress_index")] public int MaxRotateStageProgressIndex { get; set; }
    [BsonElement("last_modify_time")] public long LastModifyTime { get; set; }
    [BsonElement("score_reward_group_id")] public int ScoreRewardGroupId { get; set; }
    [BsonElement("rotate_settle_info")] public TransfiniteRotateSettleState? RotateSettleInfo { get; set; }
    [BsonElement("last_rotate_receipt")] public TransfiniteRotateSettleReceipt? LastRotateReceipt { get; set; }
}

[BsonIgnoreExtraElements]
public sealed class TransfiniteBattleState
{
    [BsonElement("stage_group_id")] public int StageGroupId { get; set; }
    [BsonElement("stage_progress_index")] public int StageProgressIndex { get; set; }
    [BsonElement("start_stage_progress")] public int StartStageProgress { get; set; }
    [BsonElement("team_info")] public TransfiniteTeamState? TeamInfo { get; set; }
    [BsonElement("stage_info")] public List<TransfiniteStageState> StageInfo { get; set; } = new();
    [BsonElement("result")] public TransfiniteBattleResultState? Result { get; set; }
    [BsonElement("last_result")] public TransfiniteBattleResultState? LastResult { get; set; }
    [BsonElement("history_results")] public List<TransfiniteBattleResultState> HistoryResults { get; set; } = new();
}

[BsonIgnoreExtraElements]
public sealed class TransfiniteTeamState
{
    [BsonElement("character_id_list")] public List<long> CharacterIdList { get; set; } = new();
    [BsonElement("captain_pos")] public int CaptainPos { get; set; }
    [BsonElement("first_fight_pos")] public int FirstFightPos { get; set; }
    [BsonElement("selected_general_skill")] public int SelectedGeneralSkill { get; set; }
    [BsonElement("enter_cg_index")] public int EnterCgIndex { get; set; }
    [BsonElement("settle_cg_index")] public int SettleCgIndex { get; set; }
}

[BsonIgnoreExtraElements]
public sealed class TransfiniteStageState
{
    [BsonElement("stage_id")] public int StageId { get; set; }
    [BsonElement("is_win")] public bool IsWin { get; set; }
    [BsonElement("spend_time")] public int SpendTime { get; set; }
    [BsonElement("score")] public int Score { get; set; }
}

[BsonIgnoreExtraElements]
public sealed class TransfiniteBattleResultState
{
    [BsonElement("last_win_stage_id")] public int LastWinStageId { get; set; }
    [BsonElement("character_result_list")] public List<TransfiniteCharacterResultState> CharacterResultList { get; set; } = new();
    [BsonElement("stage_spend_time")] public int StageSpendTime { get; set; }
}

[BsonIgnoreExtraElements]
public sealed class TransfiniteCharacterResultState
{
    [BsonElement("character_id")] public long CharacterId { get; set; }
    [BsonElement("hp_percent")] public int HpPercent { get; set; }
    [BsonElement("energy")] public int Energy { get; set; }
}

[BsonIgnoreExtraElements]
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

[BsonIgnoreExtraElements]
public sealed class TransfiniteRotateSettleReceipt
{
    [BsonElement("rotation_id")] public long RotationId { get; set; }
    [BsonElement("max_stage_progress_index")] public int MaxStageProgressIndex { get; set; }
    [BsonElement("settle_score")] public int SettleTransfiniteScore { get; set; }
    [BsonElement("unsettle_score")] public int UnSettleTransfiniteScore { get; set; }
    [BsonElement("reward_goods")] public List<TransfiniteRewardReceipt> RewardGoods { get; set; } = new();
}

[BsonIgnoreExtraElements]
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

[BsonIgnoreExtraElements]
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
    [BsonElement("transfinite_receipts")] public List<TransfiniteInventoryReceipt> TransfiniteReceipts { get; set; } = new();
}

public partial class Player
{
    [BsonElement("transfinite")] public TransfiniteState? Transfinite { get; set; }
}