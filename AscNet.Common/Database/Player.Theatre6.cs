using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;

namespace AscNet.Common.Database;

public partial class Player
{
    [BsonElement("theatre6")]
    public Theatre6State Theatre6 { get; set; } = new();
}

public sealed class Theatre6State
{
    [BsonElement("files")]
    public List<Theatre6FileState> Files { get; set; } = new();

    [BsonElement("pvp")]
    public Theatre6PvpState Pvp { get; set; } = new();

    [BsonElement("activity_id")] public int ActivityId { get; set; }
    [BsonElement("active_runs")][BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)] public Dictionary<int, Theatre6RunState> ActiveRuns { get; set; } = new();
    [BsonElement("settlements")][BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)] public Dictionary<int, Theatre6SettlementState> Settlements { get; set; } = new();
    [BsonElement("story_save")] public Theatre6StorySaveState StorySave { get; set; } = new();
    [BsonElement("pass_stages")][BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)] public Dictionary<int, int> PassStageRecords { get; set; } = new();
    [BsonElement("pass_difficulties")][BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)] public Dictionary<int, int> PassDiffRecords { get; set; } = new();
}

public sealed class Theatre6FileState
{
    [BsonElement("slot_id")] public int SlotId { get; set; }
    [BsonElement("character_id")] public int CharacterId { get; set; }
    [BsonElement("score")] public int Score { get; set; }
    [BsonElement("build_tags")] public List<int> BuildTags { get; set; } = new();
    [BsonElement("attrs")] public List<Theatre6AttrState> Attrs { get; set; } = new();
    [BsonElement("skills")] public List<Theatre6SkillState> Skills { get; set; } = new();
    [BsonElement("attr_packs")] public List<Theatre6AttrPackState> AttrPacks { get; set; } = new();
    [BsonElement("buffs")] public List<Theatre6BuffState> Buffs { get; set; } = new();
    [BsonElement("fashion_id")] public int FashionId { get; set; }
}

public sealed class Theatre6AttrState { [BsonElement("attr_id")] public int AttrId { get; set; } [BsonElement("value")] public int Value { get; set; } }
public sealed class Theatre6SkillState { [BsonElement("slot_type")] public int SlotType { get; set; } [BsonElement("position")] public int Position { get; set; } [BsonElement("skill_id")] public int SkillId { get; set; } }
public sealed class Theatre6AttrPackState { [BsonElement("pack_id")] public int PackId { get; set; } [BsonElement("num")] public int Num { get; set; } }
public sealed class Theatre6BuffState { [BsonElement("buff_id")] public int BuffId { get; set; } [BsonElement("trigger_count")] public int TriggerCount { get; set; } [BsonElement("add_magic")] public int AddMagic { get; set; } }

public sealed class Theatre6RunState
{
    [BsonElement("mode_id")] public int ModeId { get; set; }
    [BsonElement("settled")] public bool Settled { get; set; }
    [BsonElement("is_win")] public bool IsWin { get; set; }
    [BsonElement("file")] public Theatre6FileState File { get; set; } = new();
    [BsonElement("cur_health")] public int CurHealth { get; set; }
    [BsonElement("max_health")] public int MaxHealth { get; set; }
    [BsonElement("cur_san")] public int CurSan { get; set; }
    [BsonElement("max_san")] public int MaxSan { get; set; }
    [BsonElement("fights")] public List<Theatre6FightRecordState> Fights { get; set; } = new();
    [BsonElement("rewards")] public List<Theatre6SettleRewardState>? Rewards { get; set; }
    [BsonElement("story_save")] public Theatre6StorySaveState StorySave { get; set; } = new();
    [BsonElement("pass_stages")][BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)] public Dictionary<int, int> PassStageRecords { get; set; } = new();
    [BsonElement("pass_difficulties")][BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)] public Dictionary<int, int> PassDiffRecords { get; set; } = new();
}
public sealed class Theatre6SettlementState
{
    [BsonElement("mode_id")] public int ModeId { get; set; }
    [BsonElement("is_win")] public bool IsWin { get; set; }
    [BsonElement("file")] public Theatre6FileState File { get; set; } = new();
    [BsonElement("cur_health")] public int CurHealth { get; set; }
    [BsonElement("max_health")] public int MaxHealth { get; set; }
    [BsonElement("cur_san")] public int CurSan { get; set; }
    [BsonElement("max_san")] public int MaxSan { get; set; }
    [BsonElement("fights")] public List<Theatre6FightRecordState> Fights { get; set; } = new();
    [BsonElement("rewards")] public List<Theatre6SettleRewardState>? Rewards { get; set; }
    [BsonElement("story_save")] public Theatre6StorySaveState StorySave { get; set; } = new();
    [BsonElement("pass_stages")][BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)] public Dictionary<int, int> PassStageRecords { get; set; } = new();
    [BsonElement("pass_difficulties")][BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)] public Dictionary<int, int> PassDiffRecords { get; set; } = new();
}
public sealed class Theatre6FightRecordState { [BsonElement("difficulty_type")] public int DifficultyType { get; set; } [BsonElement("fight_result_type")] public int FightResultType { get; set; } [BsonElement("fight_id")] public int FightId { get; set; } [BsonElement("monster_id")] public int MonsterId { get; set; } }
public sealed class Theatre6SettleRewardState { [BsonElement("id")] public int Id { get; set; } [BsonElement("type")] public int Type { get; set; } [BsonElement("count")] public int Count { get; set; } [BsonElement("is_first")] public bool IsFirst { get; set; } }
public sealed class Theatre6StorySaveState { [BsonElement("lines")] public List<Theatre6StoryLineState> StoryLineDatas { get; set; } = new(); [BsonElement("story_ids")] public List<int> StoryIds { get; set; } = new(); }
public sealed class Theatre6StoryLineState { [BsonElement("story_line_id")] public int StoryLineId { get; set; } [BsonElement("stage_index")] public int StageIndex { get; set; } [BsonElement("completed_before")] public bool IsCompletedBefore { get; set; } [BsonElement("is_buy")] public bool IsBuy { get; set; } [BsonElement("buy_index")] public List<int> BuyIndex { get; set; } = new(); }

public sealed class Theatre6PvpState
{
    // A season is deliberately never auto-activated: schedule/condition authority must set this.
    [BsonElement("authorized_season_id")] public int AuthorizedSeasonId { get; set; }
    [BsonElement("initialized_season_id")] public int InitializedSeasonId { get; set; }
    // Schedule authority writes these explicitly; table TimeIds alone never open a promotion.
    [BsonElement("authorized_time_ids")] public List<int> AuthorizedTimeIds { get; set; } = new();
    [BsonElement("rank_id")] public int RankId { get; set; }
    [BsonElement("score")] public int Score { get; set; }
    [BsonElement("player_state")] public int PlayerState { get; set; }
    [BsonElement("action_point")] public int ActionPoint { get; set; }
    [BsonElement("action_point_time")] public long ActionPointTime { get; set; }
    [BsonElement("defense_files")] public List<Theatre6FileState> DefenseFiles { get; set; } = new();
    [BsonElement("defense_buff_id")] public int DefenseBuffId { get; set; }
    [BsonElement("defense_update_time")] public long DefenseUpdateTime { get; set; }
    [BsonElement("matches")] public List<Theatre6MatchState> Matches { get; set; } = new();
    [BsonElement("next_match_uid")] public int NextMatchUid { get; set; } = 1;
    [BsonElement("last_refresh_time")] public long LastRefreshTime { get; set; }
    [BsonElement("refresh_period_start")] public long RefreshPeriodStart { get; set; }
    [BsonElement("refresh_period_count")] public int RefreshPeriodCount { get; set; }
    [BsonElement("battle")] public Theatre6BattleState? Battle { get; set; }
    [BsonElement("rewarded_ranks")] public List<int> RewardedRanks { get; set; } = new();
    [BsonElement("rank_records")][BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)] public Dictionary<int, Theatre6RankRecordState> RankRecords { get; set; } = new();
    [BsonElement("battle_records")] public List<Theatre6BattleRecordState> BattleRecords { get; set; } = new();
    [BsonElement("stats")] public Theatre6BattleStatsState Stats { get; set; } = new();
    public void InitializeSeason(int seasonId, int rankId, int score, int actionPoint, long now)
    {
        InitializedSeasonId = seasonId;
        RankId = rankId;
        Score = score;
        PlayerState = 0;
        ActionPoint = actionPoint;
        ActionPointTime = now;
        DefenseFiles.Clear();
        DefenseBuffId = 0;
        DefenseUpdateTime = 0;
        Matches.Clear();
        NextMatchUid = 1;
        LastRefreshTime = 0;
        RefreshPeriodStart = 0;
        RefreshPeriodCount = 0;
        Battle = null;
        RewardedRanks.Clear();
        BattleRecords.Clear();
        Stats = new();
        RankRecords[seasonId] = new() { RankId = rankId, Score = score };
    }
}

public sealed class Theatre6RankRecordState { [BsonElement("rank_id")] public int RankId { get; set; } [BsonElement("score")] public int Score { get; set; } }
public sealed class Theatre6MatchState { [BsonElement("uid")] public int Uid { get; set; } [BsonElement("robot_id")] public int RobotId { get; set; } [BsonElement("mist_num")] public int MistNum { get; set; } }
public sealed class Theatre6BattleState
{
    [BsonElement("enemy_uid")] public int EnemyUid { get; set; }
    [BsonElement("enemy_robot_id")] public int EnemyRobotId { get; set; }
    [BsonElement("my_files")] public List<Theatre6FileState> MyFiles { get; set; } = new();
    [BsonElement("round_results")] public List<bool> RoundResults { get; set; } = new();
    [BsonElement("current_round")] public int CurrentRound { get; set; } = 1;
    [BsonElement("finished")] public bool Finished { get; set; }
    [BsonElement("started_at")] public long StartedAt { get; set; }
    [BsonElement("restart_count")] public int RestartCount { get; set; }
    [BsonElement("phase")] public int Phase { get; set; }
    [BsonElement("buff_id")] public int BuffId { get; set; }
    [BsonElement("result")] public Theatre6FightResultState? Result { get; set; }
}
public sealed class Theatre6FightResultState { [BsonElement("round_results")] public List<bool> RoundResults { get; set; } = new(); [BsonElement("old_score")] public int OldScore { get; set; } [BsonElement("new_score")] public int NewScore { get; set; } [BsonElement("rank_id")] public int RankId { get; set; } [BsonElement("phase")] public int Phase { get; set; } }
public sealed class Theatre6BattleRecordState { [BsonElement("battle_id")] public int BattleId { get; set; } [BsonElement("battle_time")] public long BattleTime { get; set; } [BsonElement("is_win")] public bool IsWin { get; set; } [BsonElement("is_all_win")] public bool IsAllWin { get; set; } [BsonElement("score_change")] public int ScoreChange { get; set; } [BsonElement("robot_id")] public int RobotId { get; set; } [BsonElement("my_rank_id")] public int MyRankId { get; set; } [BsonElement("status")] public int Status { get; set; } }
public sealed class Theatre6BattleStatsState
{
    [BsonElement("normal")][BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)] public Dictionary<int,int> Normal { get; set; } = new();
    [BsonElement("normal_wins")][BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)] public Dictionary<int,int> NormalWins { get; set; } = new();
    [BsonElement("advance")][BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)] public Dictionary<int,int> Advance { get; set; } = new();
    [BsonElement("advance_wins")][BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)] public Dictionary<int,int> AdvanceWins { get; set; } = new();
}
