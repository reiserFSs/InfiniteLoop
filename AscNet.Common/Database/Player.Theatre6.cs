using AscNet.Common.MsgPack;
using MessagePack;
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
    // PlayMode.Story/GamePlay of the run that unscoped Theatre6 notifications address.
    [BsonElement("current_mode")] public int CurrentMode { get; set; }
    [BsonElement("next_run_id")] public int NextRunId { get; set; } = 1;
    // Identity counter for frozen native attempts; every attempt is unique per epoch.
    [BsonElement("next_attempt_id")] public int NextAttemptId { get; set; } = 1;
    [BsonElement("next_mutation_id")] public long NextMutationId { get; set; }
    // Bumped whenever cross-document authority (season, availability, meta progression) resets;
    // receipts and frozen attempts from an earlier epoch can never act on the new one.
    [BsonElement("epoch")] public long Epoch { get; set; }
    [BsonElement("active_runs")][BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)] public Dictionary<int, Theatre6RunState> ActiveRuns { get; set; } = new();
    [BsonElement("settlements")][BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)] public Dictionary<int, Theatre6SettlementState> Settlements { get; set; } = new();
    [BsonElement("story_save")] public Theatre6StorySaveState StorySave { get; set; } = new();
    // Permanent gameplay progression: talent level/exp plus the story-detail ids already played.
    [BsonElement("play_save")] public Theatre6PlaySaveState PlaySave { get; set; } = new();
    [BsonElement("pass_stages")][BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)] public Dictionary<int, int> PassStageRecords { get; set; } = new();
    [BsonElement("pass_difficulties")][BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)] public Dictionary<int, int> PassDiffRecords { get; set; } = new();
    // Shared task ledger for the authored permanent Theatre6 mission group.
    [BsonElement("task_progress")][BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)] public Dictionary<int, int> TaskProgress { get; set; } = new();
    [BsonElement("claimed_task_ids")] public HashSet<int> ClaimedTaskIds { get; set; } = new();
    [BsonElement("pending_mutation")] public Theatre6PendingMutation? PendingMutation { get; set; }
    // Receipts are transport-retry suppression for one live connection. Packet ids restart
    // on a new connection, so a receipt is only valid for the session that stored it.
    [BsonElement("receipt_session")] public string ReceiptSessionId { get; set; } = string.Empty;
    // Highest transport id this session stored a frozen response for (-1 = none yet). An id at or
    // below it that is no longer cached was retired from the bounded replay window and must be
    // rejected rather than re-executed.
    [BsonElement("receipt_high_water")] public int ReceiptHighWaterId { get; set; } = -1;
    [BsonElement("request_receipts")][BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)] public Dictionary<int, Theatre6RequestReceipt> RequestReceipts { get; set; } = new();
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
    // Archive-saved stage buffs (Theatre6BuffData wire shape), distinct from live Buffs.
    [BsonElement("buffs")] public List<Theatre6BuffState> Buffs { get; set; } = new();
    [BsonElement("fashion_id")] public int FashionId { get; set; }
}

public sealed class Theatre6AttrState { [BsonElement("attr_id")] public int AttrId { get; set; } [BsonElement("value")] public int Value { get; set; } }
public sealed class Theatre6SkillState { [BsonElement("slot_type")] public int SlotType { get; set; } [BsonElement("position")] public int Position { get; set; } [BsonElement("skill_id")] public int SkillId { get; set; } }
public sealed class Theatre6AttrPackState { [BsonElement("pack_id")] public int PackId { get; set; } [BsonElement("num")] public int Num { get; set; } }
// Archive-saved stage buff. Persisted in Theatre6FileState.Buffs and also carried on the wire as
// Theatre6RewardData.BuffList, which is why the type needs a named-key MessagePack contract.
[MessagePackObject(true)]
public sealed class Theatre6BuffState { [BsonElement("buff_id")] public int BuffId { get; set; } [BsonElement("trigger_count")] public int TriggerCount { get; set; } [BsonElement("add_magic")] public int AddMagic { get; set; } }

// Live run instance of an authored stage buff. Uid is the identity the client, the
// skill-upgrade requests and the add/update/delete pushes all key on.
public sealed class Theatre6LiveBuffState
{
    [BsonElement("uid")] public int Uid { get; set; }
    [BsonElement("buff_id")] public int BuffId { get; set; }
    [BsonElement("remain_count")] public int RemainCount { get; set; }
    [BsonElement("trigger_count")] public int TriggerCount { get; set; }
    [BsonElement("task_free_refresh_count")] public int TaskFreeRefreshCount { get; set; }
    [BsonElement("add_magic")] public int AddMagic { get; set; }
    // DurationType 6 effects expire at the floor boundary; the flag is persisted so a
    // relog cannot leave a floor-scoped buff alive on the next floor.
    [BsonElement("floor_scoped")] public bool FloorScoped { get; set; }
}

public sealed class Theatre6BgmState { [BsonElement("cue_id")] public int CueId { get; set; } [BsonElement("priority")] public int Priority { get; set; } }

// Frozen pending native fight of the current room. SelectedMonsterId/FightId/seed and the
// offered easy/hard monsters must survive reconnection so a resumed client can load it.
public sealed class Theatre6PendingFightState
{
    [BsonElement("fight_id")] public int FightId { get; set; }
    [BsonElement("monster_id")] public int MonsterId { get; set; }
    [BsonElement("fight_seed")] public int FightSeed { get; set; }
    [BsonElement("difficulty_type")] public int DifficultyType { get; set; }
    [BsonElement("easy_monster_id")] public int EasyMonsterId { get; set; }
    [BsonElement("hard_monster_id")] public int HardMonsterId { get; set; }
    [BsonElement("fight_type")] public int FightType { get; set; }
}

public sealed class Theatre6RunState
{
    [BsonElement("mode_id")] public int ModeId { get; set; }
    [BsonElement("run_id")] public int RunId { get; set; }
    [BsonElement("settled")] public bool Settled { get; set; }
    [BsonElement("is_win")] public bool IsWin { get; set; }
    [BsonElement("file")] public Theatre6FileState File { get; set; } = new();
    [BsonElement("stage_id")] public int StageId { get; set; }
    [BsonElement("difficulty_id")] public int DifficultyId { get; set; }
    [BsonElement("story_line_id")] public int StoryLineId { get; set; }
    [BsonElement("cur_floor_idx")] public int CurFloorIdx { get; set; }
    [BsonElement("current_room")] public Theatre6RoomDataDb? CurrentRoomDataDb { get; set; }
    [BsonElement("boss_room")] public Theatre6RoomDataDb? BossRoomDataDb { get; set; }
    [BsonElement("goods")][BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)] public Dictionary<int, Theatre6GoodsData> Goods { get; set; } = new();
    [BsonElement("stage_tasks")][BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)] public Dictionary<int, Theatre6TaskData> StageTasks { get; set; } = new();
    [BsonElement("task_slot_data")] public List<Theatre6TaskSlotData> TaskSlotData { get; set; } = new();
    [BsonElement("task_group_id")] public int TaskGroupId { get; set; }
    [BsonElement("skill_over_queue")] public List<int> SkillOverQueue { get; set; } = new();
    [BsonElement("buffs")][BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)] public Dictionary<int, Theatre6LiveBuffState> Buffs { get; set; } = new();
    [BsonElement("destroyed_buffs")][BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)] public Dictionary<int, Theatre6LiveBuffState> DestroyedBuffs { get; set; } = new();
    [BsonElement("next_buff_uid")] public int NextBuffUid { get; set; } = 1;
    [BsonElement("bgms")][BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)] public Dictionary<int, Theatre6BgmState> Bgms { get; set; } = new();
    [BsonElement("messy_codes")][BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)] public Dictionary<int, int> MessyCodes { get; set; } = new();
    [BsonElement("cur_code_id")] public int CurCodeId { get; set; }
    [BsonElement("floor_buff_uid")] public List<int> FloorBuffUid { get; set; } = new();
    [BsonElement("cur_health")] public int CurHealth { get; set; }
    [BsonElement("max_health")] public int MaxHealth { get; set; }
    [BsonElement("init_health")] public int InitHealth { get; set; }
    [BsonElement("cur_san")] public int CurSan { get; set; }
    [BsonElement("max_san")] public int MaxSan { get; set; }
    [BsonElement("min_san")] public int MinSan { get; set; }
    [BsonElement("gold_amount")] public int GoldAmount { get; set; }
    [BsonElement("score_total")] public int ScoreTotal { get; set; }
    [BsonElement("random_state")] public long RandomState { get; set; }
    [BsonElement("waiting_ex_floor_confirm")] public bool WaitingExFloorConfirm { get; set; }
    [BsonElement("has_cleared_before_ex_floor")] public bool HasClearedBeforeExFloor { get; set; }
    [BsonElement("pending_fight")] public Theatre6PendingFightState? PendingFight { get; set; }
    // Frozen native (DLC world201) authorization of this run. Epoch/RunId make a stale
    // attempt unable to settle a different run, exactly like the Theatre5 attempt.
    [BsonElement("native_attempt")] public Theatre6NativeAttemptState? NativeAttempt { get; set; }
    [BsonElement("fights")] public List<Theatre6FightRecordState> Fights { get; set; } = new();
    [BsonElement("rewards")] public List<Theatre6SettleRewardState>? Rewards { get; set; }
    [BsonElement("settle_data")] public Theatre6SettleData? SettleData { get; set; }
    [BsonElement("story_save")] public Theatre6StorySaveState StorySave { get; set; } = new();
    [BsonElement("pass_stages")][BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)] public Dictionary<int, int> PassStageRecords { get; set; } = new();
    [BsonElement("pass_difficulties")][BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)] public Dictionary<int, int> PassDiffRecords { get; set; } = new();
}

public sealed class Theatre6NativeAttemptState
{
    [BsonElement("epoch")] public long Epoch { get; set; }
    [BsonElement("run_id")] public int RunId { get; set; }
    [BsonElement("attempt_id")] public int AttemptId { get; set; }
    [BsonElement("world_id")] public int WorldId { get; set; }
    [BsonElement("level_id")] public int LevelId { get; set; }
    [BsonElement("seed")] public int Seed { get; set; }
    [BsonElement("room_idx")] public int RoomIdx { get; set; }
    [BsonElement("fight_id")] public int FightId { get; set; }
    [BsonElement("monster_id")] public int MonsterId { get; set; }
    [BsonElement("fight_seed")] public int FightSeed { get; set; }
    [BsonElement("difficulty_type")] public int DifficultyType { get; set; }
    [BsonElement("room_id")] public string RoomId { get; set; } = string.Empty;
    [BsonElement("started_at")] public long StartedAt { get; set; }
    [BsonElement("settled")] public bool Settled { get; set; }
    [BsonElement("interrupted")] public bool Interrupted { get; set; }
    [BsonElement("check_failed")] public bool CheckFailed { get; set; }
    [BsonElement("settle_request_key")] public string SettleRequestKey { get; set; } = string.Empty;
    [BsonElement("entry_response")] public byte[] EntryResponse { get; set; } = Array.Empty<byte>();
    [BsonElement("settle_response")] public byte[]? SettleResponse { get; set; }
}

public sealed class Theatre6SettlementState
{
    [BsonElement("mode_id")] public int ModeId { get; set; }
    [BsonElement("run_id")] public int RunId { get; set; }
    [BsonElement("epoch")] public long Epoch { get; set; }
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
// Persisted settlement reward. The legacy elements id/type/count/is_first keep old documents
// readable; template_id/reward_type carry the granted item identity the settlement UI needs.
public sealed class Theatre6SettleRewardState
{
    [BsonElement("id")] public int Id { get; set; }
    [BsonElement("type")] public int Type { get; set; }
    [BsonElement("count")] public int Count { get; set; }
    [BsonElement("is_first")] public bool IsFirst { get; set; }
    [BsonElement("template_id")] public int TemplateId { get; set; }
    [BsonElement("reward_type")] public int RewardType { get; set; }
}
public sealed class Theatre6StorySaveState { [BsonElement("lines")] public List<Theatre6StoryLineState> StoryLineDatas { get; set; } = new(); [BsonElement("story_ids")] public List<int> StoryIds { get; set; } = new(); }
public sealed class Theatre6StoryLineState { [BsonElement("story_line_id")] public int StoryLineId { get; set; } [BsonElement("stage_index")] public int StageIndex { get; set; } [BsonElement("completed_before")] public bool IsCompletedBefore { get; set; } [BsonElement("is_buy")] public bool IsBuy { get; set; } [BsonElement("buy_index")] public List<int> BuyIndex { get; set; } = new(); }
public sealed class Theatre6PlaySaveState { [BsonElement("talent_level")] public int TalentLevel { get; set; } [BsonElement("talent_exp")] public int TalentExp { get; set; } [BsonElement("story_ids")] public List<int> StoryIds { get; set; } = new(); }

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
    [BsonElement("next_battle_id")] public int NextBattleId { get; set; } = 1;
    // Ranks whose authored RewardIds goods were earned but not yet delivered to the client.
    [BsonElement("pending_rank_reward_ids")] public List<int> PendingRankRewardIds { get; set; } = new();
    // Optimistic concurrency for cross-player defense writes.
    [BsonElement("defense_version")] public long DefenseVersion { get; set; }
    // Cross-player defense hand-off, kept as an ordered outbox on the ORIGIN document: an
    // outcome whose hand-off was skipped (recipient offline, recipient holding a pending
    // mutation, failed hand-off save) stays discoverable by the recipient's next session
    // instead of living only on the attacker's return. Entries are written in ascending
    // BattleId order, matching NextBattleId allocation, and removed by an identity-guarded
    // pull once the defender's side is durable.
    [BsonElement("pending_defense_outs")] public List<Theatre6DefenseOutcome> PendingDefenseOutcomes { get; set; } = new();
    // One watermark per (season, attacker): an intent only applies when its battle id is greater
    // than the recorded high-water mark, so a late retry can never double-credit a defense.
    [BsonElement("applied_defense_watermarks")] public List<Theatre6DefenseWatermark> AppliedDefenseWatermarks { get; set; } = new();
    // Frozen authored MinScore of the rank held when the season activated; score clamping
    // must not follow re-authored rank floors mid-season.
    [BsonElement("current_rank_min_score")] public int CurrentRankMinScore { get; set; }
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
        NextBattleId = 1;
        PendingRankRewardIds.Clear();
        DefenseVersion = 0;
        // PendingDefenseOutcomes and AppliedDefenseWatermarks deliberately survive a rollover:
        // an earned defence whose hand-off is still pending must not be destroyed by the reset,
        // and the watermark is the durable acknowledgement that lets the origin retire an entry
        // without it being re-delivered. Both are self-scoping (outcome.season_id, watermark
        // keyed by season+attacker), so retaining them cannot leak an old season into the new one.
        RewardedRanks.Clear();
        BattleRecords.Clear();
        Stats = new();
        RankRecords[seasonId] = new() { RankId = rankId, Score = score };
    }
}

public sealed class Theatre6RankRecordState { [BsonElement("rank_id")] public int RankId { get; set; } [BsonElement("score")] public int Score { get; set; } }

// Self-sufficient defence result travelling from the attacker's committed mutation to the
// defender's later session; nothing here is read back from the attacker's document.
public sealed class Theatre6DefenseOutcome
{
    [BsonElement("season_id")] public int SeasonId { get; set; }
    [BsonElement("attacker_id")] public long AttackerId { get; set; }
    [BsonElement("battle_id")] public int BattleId { get; set; }
    [BsonElement("defender_id")] public long DefenderId { get; set; }
    [BsonElement("defender_rank_id")] public int DefenderRankId { get; set; }
    [BsonElement("defender_score")] public int DefenderScore { get; set; }
    [BsonElement("attacker_pre_score")] public int AttackerPreScore { get; set; }
    [BsonElement("attacker_win")] public bool AttackerWin { get; set; }
    [BsonElement("attacker_all_win")] public bool AttackerAllWin { get; set; }
    [BsonElement("defense_buff_id")] public int DefenseBuffId { get; set; }
    [BsonElement("attacker_name")] public string AttackerName { get; set; } = string.Empty;
    [BsonElement("attacker_head_portrait_id")] public long AttackerHeadPortraitId { get; set; }
    [BsonElement("attacker_head_frame_id")] public long AttackerHeadFrameId { get; set; }
    [BsonElement("attacker_rank_id")] public int AttackerRankId { get; set; }
    [BsonElement("created_at")] public long CreatedAt { get; set; }
}

public sealed class Theatre6DefenseWatermark
{
    [BsonElement("season_id")] public int SeasonId { get; set; }
    [BsonElement("attacker_id")] public long AttackerId { get; set; }
    [BsonElement("highest_applied_battle_id")] public int HighestAppliedBattleId { get; set; }
}
// Authored robot identity or a frozen human defender snapshot. Robots keep RobotId and an
// empty Files list (the client rebuilds them from Theatre6PvpRobot/Monster rows).
public sealed class Theatre6EnemySnapshotState
{
    [BsonElement("player_id")] public long PlayerId { get; set; }
    [BsonElement("robot_id")] public int RobotId { get; set; }
    [BsonElement("mist_num")] public int MistNum { get; set; }
    [BsonElement("rank_id")] public int RankId { get; set; }
    [BsonElement("score")] public int Score { get; set; }
    [BsonElement("defense_buff_id")] public int DefenseBuffId { get; set; }
    [BsonElement("name")] public string Name { get; set; } = string.Empty;
    [BsonElement("head_portrait_id")] public long HeadPortraitId { get; set; }
    [BsonElement("head_frame_id")] public long HeadFrameId { get; set; }
    [BsonElement("is_human")] public bool IsHuman { get; set; }
    [BsonElement("files")] public List<Theatre6FileState> Files { get; set; } = new();
}

public sealed class Theatre6MatchState
{
    [BsonElement("uid")] public int Uid { get; set; }
    [BsonElement("robot_id")] public int RobotId { get; set; }
    [BsonElement("mist_num")] public int MistNum { get; set; }
    [BsonElement("enemy")] public Theatre6EnemySnapshotState Enemy { get; set; } = new();
}

public sealed class Theatre6BattleState
{
    // Identity every battle record (attacker's and a human defender's) is keyed by.
    [BsonElement("battle_id")] public int BattleId { get; set; }
    [BsonElement("enemy_uid")] public int EnemyUid { get; set; }
    [BsonElement("enemy_robot_id")] public int EnemyRobotId { get; set; }
    [BsonElement("enemy")] public Theatre6EnemySnapshotState Enemy { get; set; } = new();
    [BsonElement("my_files")] public List<Theatre6FileState> MyFiles { get; set; } = new();
    [BsonElement("round_results")] public List<bool> RoundResults { get; set; } = new();
    [BsonElement("current_round")] public int CurrentRound { get; set; } = 1;
    [BsonElement("finished")] public bool Finished { get; set; }
    [BsonElement("started_at")] public long StartedAt { get; set; }
    [BsonElement("expire_at")] public long ExpireAt { get; set; }
    [BsonElement("last_settle_time")] public long LastSettleTime { get; set; }
    [BsonElement("restart_count")] public int RestartCount { get; set; }
    [BsonElement("phase")] public int Phase { get; set; }
    [BsonElement("buff_id")] public int BuffId { get; set; }
    [BsonElement("attempt_id")] public int AttemptId { get; set; }
    [BsonElement("world_id")] public int WorldId { get; set; }
    [BsonElement("level_id")] public int LevelId { get; set; }
    [BsonElement("seed")] public int Seed { get; set; }
    [BsonElement("room_id")] public string RoomId { get; set; } = string.Empty;
    // Native environment magic carry across the three PvP rounds: the client copies
    // PvpBuffActionRecord into every round's actor and native echoes it back, so round N+1
    // must be seeded from round N's report.
    [BsonElement("self_buff_action")][BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)] public Dictionary<int, int> SelfBuffActionRecord { get; set; } = new();
    [BsonElement("enemy_buff_action")][BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)] public Dictionary<int, int> EnemyBuffActionRecord { get; set; } = new();
    [BsonElement("settle_request_key")] public string SettleRequestKey { get; set; } = string.Empty;
    [BsonElement("entry_response")] public byte[] EntryResponse { get; set; } = Array.Empty<byte>();
    [BsonElement("settle_response")] public byte[]? SettleResponse { get; set; }
    [BsonElement("result")] public Theatre6FightResultState? Result { get; set; }
}

public sealed class Theatre6FightResultState
{
    [BsonElement("round_results")] public List<bool> RoundResults { get; set; } = new();
    [BsonElement("old_score")] public int OldScore { get; set; }
    [BsonElement("new_score")] public int NewScore { get; set; }
    [BsonElement("rank_id")] public int RankId { get; set; }
    [BsonElement("phase")] public int Phase { get; set; }
    [BsonElement("is_final_win")] public bool IsFinalWin { get; set; }
    [BsonElement("is_advance_battle_win")] public bool IsAdvanceBattleWin { get; set; }
    [BsonElement("base_win_score")] public int BaseWinScore { get; set; }
    [BsonElement("elo_score")] public int EloScore { get; set; }
    [BsonElement("all_win_score")] public int AllWinScore { get; set; }
    [BsonElement("defense_score")] public int DefenseScore { get; set; }
    [BsonElement("is_new_history")] public bool IsNewHistory { get; set; }
}

public sealed class Theatre6BattleRecordState
{
    [BsonElement("battle_id")] public int BattleId { get; set; }
    [BsonElement("battle_time")] public long BattleTime { get; set; }
    [BsonElement("is_attacker")] public bool IsAttacker { get; set; } = true;
    [BsonElement("is_win")] public bool IsWin { get; set; }
    [BsonElement("is_all_win")] public bool IsAllWin { get; set; }
    [BsonElement("score_change")] public int ScoreChange { get; set; }
    [BsonElement("robot_id")] public int RobotId { get; set; }
    [BsonElement("player_id")] public long PlayerId { get; set; }
    [BsonElement("enemy_rank_id")] public int EnemyRankId { get; set; }
    [BsonElement("enemy_score")] public int EnemyScore { get; set; }
    [BsonElement("defense_buff_id")] public int DefenseBuffId { get; set; }
    [BsonElement("enemy_name")] public string EnemyName { get; set; } = string.Empty;
    [BsonElement("enemy_head_portrait_id")] public long EnemyHeadPortraitId { get; set; }
    [BsonElement("enemy_head_frame_id")] public long EnemyHeadFrameId { get; set; }
    [BsonElement("my_rank_id")] public int MyRankId { get; set; }
    [BsonElement("status")] public int Status { get; set; }
}
public sealed class Theatre6BattleStatsState
{
    [BsonElement("normal")][BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)] public Dictionary<int,int> Normal { get; set; } = new();
    [BsonElement("normal_wins")][BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)] public Dictionary<int,int> NormalWins { get; set; } = new();
    [BsonElement("advance")][BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)] public Dictionary<int,int> Advance { get; set; } = new();
    [BsonElement("advance_wins")][BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)] public Dictionary<int,int> AdvanceWins { get; set; } = new();
}

// Staged request outcome. The identical Godfall transaction shape is reused: the intent is
// written with SaveChecked before any cross-document reward is applied, so a crash between
// intent and completion replays instead of duplicating.
public sealed class Theatre6PendingMutation
{
    [BsonElement("request_key")] public string RequestKey { get; set; } = string.Empty;
    [BsonElement("outcome")] public Theatre6State Outcome { get; set; } = new();
    [BsonElement("grants")] public List<Theatre6PendingRewardGrant> Grants { get; set; } = new();
    [BsonElement("pushes")] public List<Theatre6PendingPacket> Pushes { get; set; } = new();
    // Pushes the client must receive after the frozen response (documented orderings).
    [BsonElement("after_response_pushes")] public List<Theatre6PendingPacket> AfterResponsePushes { get; set; } = new();
    [BsonElement("claimed_task_ids")] public List<int> ClaimedTaskIds { get; set; } = new();
    [BsonElement("task_progress")][BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)] public Dictionary<int, int> TaskProgress { get; set; } = new();
    [BsonElement("shop_buy_times")][BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)] public Dictionary<long, int> ShopBuyTimes { get; set; } = new();
    [BsonElement("response")] public byte[]? Response { get; set; }
    [BsonElement("response_name")] public string ResponseName { get; set; } = string.Empty;
}

public sealed class Theatre6PendingRewardGrant
{
    [BsonElement("claim_key")] public string ClaimKey { get; set; } = string.Empty;
    [BsonElement("goods")] public List<Theatre6PendingGoods> Goods { get; set; } = new();
    [BsonElement("costs")][BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)] public Dictionary<int, int> Costs { get; set; } = new();
    [BsonElement("event_cause")] public int? EventCause { get; set; }
}

public sealed class Theatre6PendingGoods
{
    [BsonElement("id")] public int Id { get; set; }
    [BsonElement("template_id")] public int TemplateId { get; set; }
    [BsonElement("count")] public int Count { get; set; }
    [BsonElement("params")] public List<int> Params { get; set; } = new();
}

public sealed class Theatre6PendingPacket
{
    [BsonElement("name")] public string Name { get; set; } = string.Empty;
    [BsonElement("payload")] public byte[] Payload { get; set; } = Array.Empty<byte>();
}

public sealed class Theatre6RequestReceipt
{
    [BsonElement("epoch")] public long Epoch { get; set; }
    [BsonElement("run_id")] public int RunId { get; set; }
    [BsonElement("mode")] public int Mode { get; set; }
    [BsonElement("mode_run_id")] public int ModeRunId { get; set; }
    [BsonElement("mutation_id")] public long MutationId { get; set; }
    [BsonElement("request_key")] public string RequestKey { get; set; } = string.Empty;
    [BsonElement("response_name")] public string ResponseName { get; set; } = string.Empty;
    [BsonElement("response")] public byte[] Response { get; set; } = Array.Empty<byte>();
}
