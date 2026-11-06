using MessagePack;

namespace AscNet.Common.MsgPack;

/// <summary>PreFightData.ReformBalanceData (XReform2ndManager.PreFight): the submitted weight (mass) ids of both scale sides.</summary>
[MessagePackObject(true)]
public sealed class ReformBalanceData
{
    public List<int>? WishMassIds { get; set; }
    public List<int>? CostMassIds { get; set; }
}

public partial class PreFightRequest
{
    public partial class PreFightRequestPreFightData
    {
        public ReformBalanceData? ReformBalanceData { get; set; }
    }
}

[MessagePackObject(true)]
public sealed class ReformEnemyReplaceData
{
    public int EnemyGroupId { get; set; }
    public int SourceId { get; set; }
    public int TargetId { get; set; }
    public List<int> AffixSourceId { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class ReformDifficultyDb
{
    public int Score { get; set; }
    public int ExtraStar { get; set; }
    public List<ReformEnemyReplaceData> EnemyReplaceIds { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class ReformStageDb
{
    public int Id { get; set; }
    public bool Pass { get; set; }
    public int? EnvId { get; set; }
    public int CurDiffIndex { get; set; }
    public List<ReformDifficultyDb> DifficultyDbs { get; set; } = [];
    public int BalanceMaxWishQuality { get; set; }
}

[MessagePackObject(true)]
public sealed class ReformFubenDb
{
    public int ActivityId { get; set; }
    public bool IsPlaying { get; set; }
    public bool IsSuperior { get; set; }
    public List<ReformStageDb> StageDbs { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class NotifyReformFubenActivity
{
    public ReformFubenDb ReformFubenDb { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class FubenReformEnterResponse
{
    public int Code { get; set; }
    public ReformFubenDb? ReformFubenDb { get; set; }
}

/// <summary>FightSettleResponse.Settle.ReformFightResult as read by XReform2ndManager.ShowReward / XUiReformCombatSettleWin.</summary>
[MessagePackObject(true)]
public sealed class ReformFightResult
{
    public int Score { get; set; }
    public int ExtraStar { get; set; }
    public int FinalWishWeight { get; set; }
    public int? BonusWishWeight { get; set; }
}
