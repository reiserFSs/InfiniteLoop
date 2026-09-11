using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;

namespace AscNet.Common.Database;

public sealed partial class GuildWarRoundState
{
    public int Season { get; set; }
    public int GameThrough { get; set; } = 1;
    public int BossDead { get; set; }
    public int DragonRage { get; set; }
    public int FullDragonRageTime { get; set; }
    public int GameThroughCfgId { get; set; }
    public int DragonRageCfgId { get; set; }
    public int FullDragonRageCfgId { get; set; }
    public long LastDragonRageReduceTime { get; set; }
    public long DynamicsAdvancedAt { get; set; }
    public int AttackTimes { get; set; }
    public long NextAttackTime { get; set; }
    public List<int> LastProtectNodeIds { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<long, int> DefenseDic { get; set; } = [];
    public List<GuildWarStationState> Stations { get; set; } = [];
    public List<GuildWarReinforcementState> Reinforcements { get; set; } = [];
    public List<GuildWarDynamicAction> DynamicsActions { get; set; } = [];
}

public sealed partial class GuildWarNodeState
{
    public long NextReinforcementsBornTime { get; set; }
    public int LastReinforcementId { get; set; }
    public int LastAttackedReinforcementId { get; set; }
    public long LastReinforcementAttackedTime { get; set; }
    public long RebuildAt { get; set; }
    public long NextBossTreatMstTime { get; set; }
}

public sealed partial class GuildWarPlayerRound
{
    public List<int> BeStationedFightRecord { get; set; } = [];
    public int DefenseCount { get; set; }
    public int ReinforcementSupportCount { get; set; }
    public int StationCount { get; set; }
    public List<string> StationReceipts { get; set; } = [];
}

public sealed class GuildWarStationState
{
    public long PlayerId { get; set; }
    public int NodeId { get; set; }
    public int CharacterId { get; set; }
}

public sealed class GuildWarReinforcementState
{
    public int Uid { get; set; }
    public int ReinforcementId { get; set; }
    public long CurHp { get; set; }
    public long HpMax { get; set; }
    public int CurNodeId { get; set; }
    public long DeadTime { get; set; }
    public long NextMoveTime { get; set; }
    public long ReadyDoneTime { get; set; }
    public List<long> SupportedPlayerIds { get; set; } = [];
    public bool Attacked { get; set; }
}

public sealed class GuildWarDynamicAction
{
    public int ActionId { get; set; }
    public long CreateTime { get; set; }
    public int ActionType { get; set; }
    public int NodeUid { get; set; }
    public int MonsterUid { get; set; }
    public int ReinforcementUid { get; set; }
    public int PreNodeIdx { get; set; }
    public int NextNodeIdx { get; set; }
    public int DragonRageValue { get; set; }
    public long Damage { get; set; }
    public int GameThrough { get; set; }
    public int GameThroughId { get; set; }
    public byte[]? NodeSnapshot { get; set; }
    public byte[]? MonsterSnapshot { get; set; }
    public byte[]? ReinforcementSnapshot { get; set; }
}
