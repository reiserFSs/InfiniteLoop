using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;
using AscNet.Common.MsgPack;

namespace AscNet.Common.Database;

/// <summary>Durable Stronghold activity state. Missing retail server tables are intentionally not represented.</summary>
[BsonIgnoreExtraElements]
public sealed class StrongholdState
{
    [BsonElement("activity_id")] public int ActivityId { get; set; }
    [BsonElement("begin_time")] public uint BeginTime { get; set; }
    [BsonElement("fight_begin_time")] public int FightBeginTime { get; set; }
    [BsonElement("cur_day")] public int CurDay { get; set; }
    /// <summary>Authored daily-reset period (GuildModule.DailyPeriod) up to which day boundaries are settled; 0 = pre-reconciliation document.</summary>
    [BsonElement("settled_period")] public long SettledPeriod { get; set; }
    [BsonElement("level_id")] public int LevelId { get; set; }
    [BsonElement("assist_character_id")] public int AssistCharacterId { get; set; }
    [BsonElement("set_assist_character_time")] public int SetAssistCharacterTime { get; set; }
    [BsonElement("borrow_count")] public int BorrowCount { get; set; }
    [BsonElement("electric_energy")] public int ElectricEnergy { get; set; }
    [BsonElement("endurance")] public int Endurance { get; set; }
    [BsonElement("mineral_left")] public int MineralLeft { get; set; }
    [BsonElement("total_mineral")] public int TotalMineral { get; set; }
    [BsonElement("electric_character_ids")] public List<int> ElectricCharacterIds { get; set; } = new();
    [BsonElement("finish_group_ids")] public List<int> FinishGroupIds { get; set; } = new();
    [BsonElement("finish_group_infos")] public List<StrongholdFinishGroupInfo> FinishGroupInfos { get; set; } = new();
    [BsonElement("history_finish_group_infos")] public List<StrongholdFinishGroupInfo> HistoryFinishGroupInfos { get; set; } = new();
    [BsonElement("group_infos")] public List<StrongholdGroupInfo> GroupInfos { get; set; } = new();
    [BsonElement("group_stage_datas")] public List<StrongholdGroupStageData> GroupStageDatas { get; set; } = new();
    [BsonElement("team_infos")][BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)] public Dictionary<int, StrongholdTeamInfo> TeamInfos { get; set; } = new();
    [BsonElement("fight_team_infos")][BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)] public Dictionary<int, List<StrongholdTeamInfo>> FightTeamInfos { get; set; } = new();
    [BsonElement("rune_list")] public List<int> RuneList { get; set; } = new();
    [BsonElement("reward_ids")] public List<int> RewardIds { get; set; } = new();
    [BsonElement("stay_days")] public List<int> StayDays { get; set; } = new();
    [BsonElement("mine_records")] public List<StrongholdMineRecord> MineRecords { get; set; } = new();
    [BsonElement("claimed_reward_ids")] public List<int> ClaimedRewardIds { get; set; } = new();
    [BsonElement("last_result_record")] public StrongholdResultRecord LastResultRecord { get; set; } = new();
    [BsonElement("current_result_record")] public StrongholdResultRecord? CurrentResultRecord { get; set; }
    [BsonElement("pending_group_id")] public int PendingGroupId { get; set; }
    [BsonElement("pending_stage_id")] public int PendingStageId { get; set; }
}

public partial class Player
{
    [BsonElement("stronghold")] public StrongholdState Stronghold { get; set; } = new();
}
