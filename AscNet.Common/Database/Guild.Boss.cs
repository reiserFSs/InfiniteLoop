using AscNet.Common.MsgPack;

namespace AscNet.Common.Database;

public sealed partial class Guild
{
    public GuildBossState Boss { get; set; } = new();
}

public sealed class GuildBossState
{
    public long Period { get; set; }
    public int BossLevel { get; set; }
    public int BossLevelNext { get; set; }
    public long HpMax { get; set; }
    public long HpLeft { get; set; }
    public long GuildScoreSumBest { get; set; }
    public long BonusScore { get; set; }
    public long RankEnteredAt { get; set; }
    public bool Charged { get; set; }
    public List<int> OrderList { get; set; } = [];
    public List<GuildBossStageState> Stages { get; set; } = [];
    public List<GuildBossRobot> Robots { get; set; } = [];
    public List<GuildBossParticipantState> Participants { get; set; } = [];
    public List<GuildBossLog> Logs { get; set; } = [];
    public List<GuildBossArchiveState> Archives { get; set; } = [];
}

public sealed class GuildBossStageState
{
    public int StageId { get; set; }
    public int Type { get; set; }
    public int EffectId { get; set; }
    public int EffectCount { get; set; }
    public int CurEffectCount { get; set; }
}

public sealed class GuildBossParticipantState
{
    public long PlayerId { get; set; }
    public long RankEnteredAt { get; set; }
    public long BonusScore { get; set; }
    public bool DeathBonus { get; set; }
    public List<GuildBossPlayerStageState> Stages { get; set; } = [];
    public List<int> ScoreBoxGot { get; set; } = [];
    public List<int> HpBoxGot { get; set; } = [];
}

public sealed class GuildBossPlayerStageState
{
    public int StageId { get; set; }
    public long Score { get; set; }
    public int UploadCount { get; set; }
    public long RankEnteredAt { get; set; }
    public List<int> CardIds { get; set; } = [];
    public List<CharacterHeadInfo> CharacterHeadInfoList { get; set; } = [];
}

public sealed class GuildBossArchiveState
{
    public long Period { get; set; }
    public int BossLevel { get; set; }
    public long HpMax { get; set; }
    public long HpLeft { get; set; }
    public long Score { get; set; }
    public long RankEnteredAt { get; set; }
    public long EndTime { get; set; }
    public int RankRewardId { get; set; }
    public int RankMailId { get; set; }
    public List<GuildBossParticipantState> Participants { get; set; } = [];
    public List<long> RewardedPlayerIds { get; set; } = [];
}
