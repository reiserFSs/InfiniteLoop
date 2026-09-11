using MongoDB.Bson.Serialization.Attributes;

namespace AscNet.Common.Database;

public sealed partial class Guild
{
    [BsonElement("war")]
    public GuildWarGuildState War { get; set; } = new();
}

public sealed partial class GuildPlayerState
{
    public GuildWarParticipation War { get; set; } = new();
}

public sealed partial class GuildWarGuildState
{
    public int Season { get; set; }
    public int RoundId { get; set; }
    public int NextDifficultyId { get; set; }
    public int LastMaxDifficultyId { get; set; }
    public List<GuildWarRoundState> Rounds { get; set; } = [];
}

public sealed partial class GuildWarRoundState
{
    public int RoundId { get; set; }
    public int DifficultyId { get; set; }
    public long StartedAt { get; set; }
    public long EndsAt { get; set; }
    public bool Settled { get; set; }
    public List<GuildWarNodeState> Nodes { get; set; } = [];
    public List<GuildWarMonsterState> Monsters { get; set; } = [];
    public List<int> AttackPlan { get; set; } = [];
    [BsonDictionaryOptions(MongoDB.Bson.Serialization.Options.DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<long, int> PlayerNodes { get; set; } = [];
    public int TotalActivation { get; set; }
    public long TotalPoint { get; set; }
}

public sealed partial class GuildWarNodeState
{
    public int Uid { get; set; }
    public int NodeId { get; set; }
    public long CurHp { get; set; }
    public long HpMax { get; set; }
    public long DeadTime { get; set; }
    public int FightCount { get; set; }
    public int IsDead { get; set; }
    public int CurMember { get; set; }
}

public sealed partial class GuildWarMonsterState
{
    public int Uid { get; set; }
    public int MonsterId { get; set; }
    public int CurNodeIdx { get; set; }
    public long CurHp { get; set; }
    public long HpMax { get; set; }
    public long DeadTime { get; set; }
    public int FightCount { get; set; }
    public long LastMoveDayNo { get; set; }
}

public sealed partial class GuildWarParticipation
{
    public int Season { get; set; }
    public uint GuildId { get; set; }
    public long EnergyDay { get; set; }
    public List<GuildWarPlayerRound> Rounds { get; set; } = [];
    public List<int> PlayedActionIds { get; set; } = [];
}

public sealed partial class GuildWarPlayerRound
{
    public uint GuildId { get; set; }
    public int RoundId { get; set; }
    public int DifficultyId { get; set; }
    public int CurNodeId { get; set; }
    public int Activation { get; set; }
    public long Point { get; set; }
    public List<int> RewardNodeIds { get; set; } = [];
    public int RoundReward { get; set; }
}
