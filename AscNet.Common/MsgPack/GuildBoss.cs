using System.Collections.Generic;
using MessagePack;

namespace AscNet.Common.MsgPack;

[MessagePackObject(true)]
public sealed class GuildBossInfoRequest
{
    public long LogId { get; set; }
}

[MessagePackObject(true)]
public sealed class GuildBossInfoResponse : GuildResponse
{
    public long ActivityId { get; set; }
    public long HpMax { get; set; }
    public long HpLeft { get; set; }
    public long EndTime { get; set; }
    public long TotalScore { get; set; }
    public GuildBossPlayerRank MyRank { get; set; } = new();
    public int MyRankNum { get; set; }
    public List<GuildBossLog> Logs { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class GuildBossPlayerRankResponse : GuildResponse
{
    public List<GuildBossPlayerRank> RankList { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class GuildBossGuildRankResponse : GuildResponse
{
    public List<GuildBossGuildRank> RankList { get; set; } = new();
    public GuildBossGuildRank MyRank { get; set; } = new();
    public double MyRankNum { get; set; }
}

[MessagePackObject(true)]
public sealed class GuildBossActivityResponse : GuildResponse
{
    public long ActivityId { get; set; }
    public long GuildScoreSumBest { get; set; }
    public long HpMax { get; set; }
    public long HpLeft { get; set; }
    public long GuildScoreSum { get; set; }
    public int BossLevel { get; set; }
    public int BossLevelNext { get; set; }
    public List<int> ScoreBoxGot { get; set; } = new();
    public List<int> HpBoxGotNew { get; set; } = new();
    public List<GuildBossRobot> RobotList { get; set; } = new();
    public long PlayerFinalScore { get; set; }
    public List<GuildBossStageInfo> BossList { get; set; } = new();
    public List<int> OrderList { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class GuildBossStageRequest
{
    public int StageId { get; set; }
    public long LogId { get; set; }
}

[MessagePackObject(true)]
public sealed class GuildBossStageResponse : GuildResponse
{
    public int BuffLeft { get; set; }
    public int CurEffectCount { get; set; }
    public int TotalEffectCount { get; set; }
    public List<GuildBossStageTopPlayer> TopPlayers { get; set; } = new();
    public List<GuildBossLog> Logs { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class GuildBossPlayerStageRankRequest
{
    public int StageId { get; set; }
}

[MessagePackObject(true)]
public sealed class GuildBossPlayerStageRankResponse : GuildResponse
{
    public List<GuildBossPlayerRank> RankList { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class GuildBossScoreBoxRequest
{
    public int BoxId { get; set; }
}

[MessagePackObject(true)]
public sealed class GuildBossScoreBoxResponse : GuildResponse
{
    public List<RewardGoods> RewardGoods { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class GuildBossHpBoxRequest
{
    public int BoxId { get; set; }
}

[MessagePackObject(true)]
public sealed class GuildBossHpBoxResponse : GuildResponse
{
    public List<RewardGoods> RewardGoods { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class GuildBossLevelRequest
{
    public int BossLevelNext { get; set; }
    public long ActivityId { get; set; }
}

[MessagePackObject(true)]
public sealed class GuildBossLevelResponse : GuildResponse { }

[MessagePackObject(true)]
public sealed class GuildBossSetOrderRequest
{
    public List<int> OrderList { get; set; } = new();
    public long ActivityId { get; set; }
}

[MessagePackObject(true)]
public sealed class GuildBossSetOrderResponse : GuildResponse { }

[MessagePackObject(true)]
public sealed class GuildBossUploadRequest
{
    public int StageId { get; set; }
}

[MessagePackObject(true)]
public sealed class GuildBossUploadResponse : GuildResponse
{
    public long SubHp { get; set; }
    public int Contribute { get; set; }
}

[MessagePackObject(true)]
public sealed class GuildBossGetAllBossRewardResponse : GuildResponse
{
    public List<RewardGoods> RewardGoods { get; set; } = new();
    public List<int> BossHpId { get; set; } = new();
    public List<int> BossScoreId { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class GuildFightStyleResponse : GuildResponse
{
    public GuildBossFightStyle FightStyle { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class GuildSelectFightStyleRequest
{
    public int StyleId { get; set; }
}

[MessagePackObject(true)]
public sealed class GuildSelectFightStyleResponse : GuildResponse { }

[MessagePackObject(true)]
public sealed class GuildSelectFightStyleSkillRequest
{
    public int OperType { get; set; }
    public int? SkillId { get; set; }
}

[MessagePackObject(true)]
public sealed class GuildSelectFightStyleSkillResponse : GuildResponse { }

[MessagePackObject(true)]
public sealed class GuildBossStageInfo
{
    public int StageId { get; set; }
    public int Type { get; set; }
    public long Score { get; set; }
    public int UploadCount { get; set; }
    public int EffectId { get; set; }
    public int CurEffectCount { get; set; }
    public int TotalEffectCount { get; set; }
    public List<int> CardIds { get; set; } = new();
    public List<CharacterHeadInfo> CharacterHeadInfoList { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class GuildBossRobot
{
    public int Type { get; set; }
    public List<int> RobotIds { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class GuildBossPlayerRank
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public long Score { get; set; }
    public int RankLevel { get; set; }
    public int HeadPortraitId { get; set; }
    public int HeadFrameId { get; set; }
    public List<int> CardIds { get; set; } = new();
    public List<CharacterHeadInfo> CharacterHeadInfoList { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class GuildBossGuildRank
{
    public uint Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public long Score { get; set; }
    public int IconId { get; set; }
}

[MessagePackObject(true)]
public sealed class GuildBossStageTopPlayer
{
    public long Id { get; set; }
    public string PlayerName { get; set; } = string.Empty;
    public int HeadPortraitId { get; set; }
    public int HeadFrameId { get; set; }
    public long Score { get; set; }
}

[MessagePackObject(true)]
public sealed class GuildBossLog
{
    public long LogId { get; set; }
    public long PlayerId { get; set; }
    public string PlayerName { get; set; } = string.Empty;
    public long Time { get; set; }
    public int StageId { get; set; }
    public long SubHp { get; set; }
    public int CurEffectCount { get; set; }
    public int TotalEffectCount { get; set; }
    public int EffectValue { get; set; }
    public long EffectHp { get; set; }
}

[MessagePackObject(true)]
public sealed class GuildBossFightStyle
{
    public int StyleId { get; set; }
    public List<int> EffectedSkillId { get; set; } = new();
    public long LastEffectTime { get; set; }
}

[MessagePackObject(true)]
public sealed class GuildBossFightResult
{
    public long HpMaxScore { get; set; }
    public long TotalScore { get; set; }
    public long TotalHighScore { get; set; }
    public int UseTime { get; set; }
    public long Damage { get; set; }
    public long DamageScore { get; set; }
    public double HpLeftPer { get; set; }
    public long HpScore { get; set; }
    public long Base { get; set; }
}
