using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;

namespace AscNet.Common.Database;

public partial class Player
{
    [BsonElement("sky_tower")]
    public SkyTowerState SkyTower { get; set; } = new();
}

/// <summary>
/// Dreamhouse Tycoon (client XSkyTower) per-player state. Everything here is derived from the client contract
/// and the authoritative share/skytower tables; the production rules (AscNet policy where no retail server
/// exists) live in SkyTowerModule. Keys follow the wire: work position = FloorId * 10 + (index - 1), storage =
/// GroupId * 10000 + ProductType * 100 + ProductId.
/// </summary>
public sealed class SkyTowerState
{
    /// <summary>SkyTowerActivity.Id the state was created for; 0 = never initialised.</summary>
    [BsonElement("activity_id")] public int ActivityId { get; set; }
    [BsonElement("level")] public int Level { get; set; }
    [BsonElement("gold")] public long Gold { get; set; }
    [BsonElement("character_lv_up_item")] public long CharacterLvUpItem { get; set; }
    [BsonElement("speed_up_item")] public long SpeedUpItem { get; set; }
    [BsonElement("speed_up_daily_used")] public int SpeedUpDailyUsedCount { get; set; }
    [BsonElement("speed_up_granted_days")] public int SpeedUpGrantedDays { get; set; }
    [BsonElement("total_gold_produced")] public long TotalGoldProduced { get; set; }
    [BsonElement("total_trans_character_lv_up_item")] public long TotalTransCharacterLvUpItem { get; set; }
    /// <summary>Gold credited by any source today / ever; the GainGold (143001) task progress.</summary>
    [BsonElement("daily_gold_gained")] public long DailyGoldGained { get; set; }
    [BsonElement("total_gold_gained")] public long TotalGoldGained { get; set; }
    [BsonElement("last_settle_time")] public long LastSettleTime { get; set; }
    [BsonElement("daily_period")] public long DailyPeriod { get; set; }
    [BsonElement("last_unlocked_floor_id")] public int LastUnlockedFloorId { get; set; }
    [BsonElement("last_unlocked_work_pos_index")] public int LastUnlockedWorkPosIndex { get; set; }

    [BsonElement("work_pos")]
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, SkyTowerWorkPosState> WorkPos { get; set; } = new();

    [BsonElement("storage")]
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, long> Storage { get; set; } = new();

    [BsonElement("characters")]
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> Characters { get; set; } = new();

    [BsonElement("performs")]
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, SkyTowerPerformState> Performs { get; set; } = new();

    /// <summary>Accumulated progress of the perform tasks that count production while their perform is running, and the best gold speed.</summary>
    [BsonElement("task_progress")]
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, long> TaskProgress { get; set; } = new();

    /// <summary>Claim receipts of SkyTower item grants (the reward pipeline keeps player-owned receipts on the player).</summary>
    [BsonElement("applied_reward_claims")] public List<string> AppliedRewardClaims { get; set; } = [];
}

public sealed class SkyTowerWorkPosState
{
    [BsonElement("work_type")] public int WorkType { get; set; }
    [BsonElement("product_id")] public int ProductId { get; set; }
    [BsonElement("character_id")] public int CharacterId { get; set; }
    [BsonElement("state")] public int State { get; set; }
    [BsonElement("worked_seconds")] public long WorkedSeconds { get; set; }
    [BsonElement("produce_remainder")] public long ProduceRemainder { get; set; }
    [BsonElement("reserved_inputs")] public List<SkyTowerProductAmountState> ReservedInputs { get; set; } = [];
    [BsonElement("reserved_extra_produce_ratio")] public long ReservedExtraProduceRatio { get; set; }
    [BsonElement("has_started_round")] public bool HasStartedRound { get; set; }
}

public sealed class SkyTowerProductAmountState
{
    [BsonElement("product_type")] public int ProductType { get; set; }
    [BsonElement("product_id")] public int ProductId { get; set; }
    [BsonElement("count")] public long Count { get; set; }
}

public sealed class SkyTowerPerformState
{
    /// <summary>0 not started, 1 running, 2 finished (client XMVCA.XSkyTower.PerformState).</summary>
    [BsonElement("state")] public int State { get; set; }
    [BsonElement("update_time")] public long UpdateTime { get; set; }

    [BsonElement("story")]
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> Story { get; set; } = new();
}
