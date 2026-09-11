using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;

namespace AscNet.Common.Database;

public sealed partial class GuildWarRoundState
{
    public List<long> RewardParticipants { get; set; } = [];
    public List<int> ClearedNodeTypes { get; set; } = [];
    public long FirstPointAt { get; set; }
    public GuildWarSettlement? Settlement { get; set; }
    public long FirstPassAt { get; set; }
}

public sealed partial class GuildWarGuildState
{
    public bool SeasonSettled { get; set; }
    public int FinalRankPercent { get; set; }
    public List<GuildWarSeasonStanding> RankSeasons { get; set; } = [];
}

public sealed class GuildWarSeasonStanding
{
    public int Season { get; set; }
    public long Point { get; set; }
    public int Activation { get; set; }
    public long FirstPointAt { get; set; }
}

public sealed partial class GuildWarParticipation
{
    public List<GuildWarSettlement> Settlements { get; set; } = [];
    public List<GuildWarTaskReceipt> TaskReceipts { get; set; } = [];
    public int FinalRankPercent { get; set; }
}

public sealed partial class GuildWarPlayerRound
{
    public int FightCount { get; set; }
    public long FirstPointAt { get; set; }
    public List<int> BossRewardIds { get; set; } = [];
    public List<GuildWarRankContribution> Contributions { get; set; } = [];
}

public sealed class GuildWarRankContribution
{
    public int Uid { get; set; }
    public bool Monster { get; set; }
    public long Damage { get; set; }
    public long Point { get; set; }
    public int Activation { get; set; }
    public long FirstPointAt { get; set; }
}

public sealed class GuildWarTaskReceipt
{
    public int Season { get; set; }
    public int TaskId { get; set; }
}

public sealed class GuildWarSettlement
{
    public int Season { get; set; }
    public int RoundId { get; set; }
    public int DifficultyId { get; set; }
    public int IsPass { get; set; }
    public long PassUseSecond { get; set; }
    public int TotalActivation { get; set; }
    public int PlayerActivation { get; set; }
    public long PlayerPoints { get; set; }
    public int BaseHpPercent { get; set; }
    public long BasePoint { get; set; }
    public int FinalRankPercent { get; set; }
    public List<uint> CurMember { get; set; } = [];
}
