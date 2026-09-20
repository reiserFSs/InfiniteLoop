using MessagePack;

namespace AscNet.Common.MsgPack;

// Phantom Clash (Theatre6 PvP) wire contracts. Field names are the keys the shipped client
// deserializes in xmodule/xtheatre6/controlpartial/XTheatre6ControlPvpNetwork.lua and the
// XTheatre6SubPvpModel consumers; declaration order is the MessagePack map write order and is
// kept identical to the decoded retail version46 capture for the packets it contains.
// Argument-less requests are declared so the dispatch contract deserializes exactly once.

#region Requests

[MessagePackObject(true)]
public sealed class Theatre6PvpStartRequest
{
}

[MessagePackObject(true)]
public sealed class Theatre6PvpUpdateDefenseRequest
{
    public int? BuffId { get; set; }
    public List<Theatre6FileSlot?>? Slots { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre6PvpRefreshMatchRequest
{
}

[MessagePackObject(true)]
public sealed class Theatre6PvpGetActionPointRequest
{
}

[MessagePackObject(true)]
public sealed class Theatre6PvpStartFightRequest
{
    public int EnemyId { get; set; }
    public List<Theatre6FileSlot> MyFileSlots { get; set; } = new();
    public int? BuffId { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre6PvpRestartFightRequest
{
}

[MessagePackObject(true)]
public sealed class Theatre6PvpQueryRankRequest
{
}

[MessagePackObject(true)]
public sealed class Theatre6PvpGetBattleRecordsRequest
{
}

[MessagePackObject(true)]
public sealed class Theatre6GetPvpPreviewInfoRequest
{
}

[MessagePackObject(true)]
public sealed class Theatre6PvpGiveUpFightRequest
{
}

#endregion

#region Responses

[MessagePackObject(true)]
public sealed class Theatre6PvpStartResponse
{
    public Theatre6PvpActivityData? ActivityData { get; set; }
    public int Code { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre6PvpUpdateDefenseResponse
{
    public int Code { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre6PvpRefreshMatchResponse
{
    public Theatre6MatchResult? MatchResult { get; set; }
    public long LastRefreshMatchTime { get; set; }
    public int RefreshRemainSeconds { get; set; }
    public int Code { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre6PvpGetActionPointResponse
{
    public int Code { get; set; }
    public int ActionPoint { get; set; }
    public long LastActionPointRecoverTime { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre6PvpStartFightResponse
{
    public int Code { get; set; }
    public Theatre6TinyBattleState? BattleState { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre6PvpRestartFightResponse
{
    public int Code { get; set; }
    public Theatre6TinyBattleState? BattleState { get; set; }
    public Theatre6PvpFightResult? FightResult { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre6PvpQueryRankResponse
{
    public int Code { get; set; }
    public List<Theatre6RankPlayer> RankPlayerInfos { get; set; } = new();
    public int TotalCount { get; set; }
    public int SelfRank { get; set; } = -1;
}

[MessagePackObject(true)]
public sealed class Theatre6PvpGetBattleRecordsResponse
{
    public int Code { get; set; }
    public List<Theatre6BattleRecord> BattleRecords { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class Theatre6GetPvpPreviewInfoResponse
{
    public int Code { get; set; }
    public Dictionary<int, Theatre6RankRecord> PvpRankRecords { get; set; } = new();
    public int ActionPoint { get; set; }
    public long LastActionPointRecoverTime { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre6PvpGiveUpFightResponse
{
    public int Code { get; set; }
    public Theatre6PvpFightResult? FightResult { get; set; }
}

#endregion

#region Notifications

[MessagePackObject(true)]
public sealed class NotifyTheatre6PvpGetActionPoint
{
    public int ActionPoint { get; set; }
    public long LastActionPointRecoverTime { get; set; }
}

// The client assigns the root object straight into ActivityData.TinyBattleState.
[MessagePackObject(true)]
public sealed class NotifyTheatre6PvpTinyBattleState : Theatre6TinyBattleState
{
}

[MessagePackObject(true)]
public sealed class NotifyTheatre6BattleRecordsUpdate
{
    public List<Theatre6BattleRecord> BattleRecords { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class NotifyTheatre6DefenseUpdate
{
    public int DefenseBuffId { get; set; }
    public List<Theatre6FileData> Lineups { get; set; } = new();
}

// Only the non-null maps change; the client wholly replaces each rank-keyed map it receives.
[MessagePackObject(true)]
public sealed class NotifyTheatre6PvpBattleStatsUpdate
{
    public Theatre6BattleStats BattleStats { get; set; } = new();
}

// The client consumes the root as a match result, not a MatchResult wrapper.
[MessagePackObject(true)]
public sealed class NotifyMatchPlayersUpdate
{
    public int Code { get; set; }
    public List<Theatre6MatchEnemy> Enemies { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class NotifyTheatre6PvpScoreUpdate
{
    public int RankId { get; set; }
    public int Score { get; set; }
}

#endregion

#region Nested data

[MessagePackObject(true)]
public sealed class Theatre6RankRecord
{
    public int RankId { get; set; }
    public int Score { get; set; }
}

// Opponent battle snapshot. A robot opponent carries RobotId and no SaveFiles: the client
// rebuilds its archives itself from Theatre6PvpRobot.UseMonsterIds + Theatre6Monster, which is
// why the retail capture has RobotId set with an empty SaveFiles list.
[MessagePackObject(true)]
public sealed class Theatre6PlayerBattleDb
{
    public long PlayerId { get; set; }
    public int UpdateTime { get; set; }
    public string Name { get; set; } = "";
    public long HeadPortraitId { get; set; }
    public long HeadFrameId { get; set; }
    public int RankId { get; set; }
    public int Score { get; set; }
    public List<Theatre6FileData> SaveFiles { get; set; } = new();
    public int RobotId { get; set; }
    public int DefenseBuffId { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre6MatchEnemy
{
    public int Uid { get; set; }
    public Theatre6PlayerBattleDb BattleData { get; set; } = new();
    public int MistNum { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre6MatchResult
{
    public int Code { get; set; }
    public List<Theatre6MatchEnemy> Enemies { get; set; } = new();
}

[MessagePackObject(true)]
public class Theatre6TinyBattleState
{
    public int EnemyId { get; set; }
    public Theatre6PlayerBattleDb EnemyData { get; set; } = new();
    public List<Theatre6FileData> MyLineups { get; set; } = new();
    public List<bool> RoundResults { get; set; } = new();
    public int CurrentRound { get; set; }
    public bool IsFinished { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre6BattleStats
{
    public Dictionary<int, int>? NormalBattleCounts { get; set; }
    public Dictionary<int, int>? NormalBattleWinCounts { get; set; }
    public Dictionary<int, int>? AdvanceBattleCounts { get; set; }
    public Dictionary<int, int>? AdvanceBattleWinCounts { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre6BattleRecord
{
    public int BattleId { get; set; }
    public long BattleTime { get; set; }
    public bool IsAttacker { get; set; }
    public bool IsWin { get; set; }
    public bool IsAllWin { get; set; }
    public int ScoreChange { get; set; }
    public Theatre6PlayerBattleDb EnemyInfo { get; set; } = new();
    public int MyRankId { get; set; }
    public int RecordStatus { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre6RankPlayer
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public long HeadPortraitId { get; set; }
    public long HeadFrameId { get; set; }
    public int Score { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre6ScoreDetail
{
    public int BaseWinScore { get; set; }
    public int EloScore { get; set; }
    public int AllWinScore { get; set; }
    public int DefenseScore { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre6PvpActivityData
{
    public int ActivityId { get; set; }
    public int RankId { get; set; }
    public int Score { get; set; }
    public int PlayerState { get; set; }
    public Theatre6TinyBattleState? TinyBattleState { get; set; }
    public List<Theatre6MatchEnemy> Enemies { get; set; } = new();
    public List<Theatre6FileData> Lineups { get; set; } = new();
    public List<int> RewardedRanks { get; set; } = new();
    public long LastRefreshMatchTime { get; set; }
    public int RefreshRemainSeconds { get; set; }
    public int AttackBuffId { get; set; }
    public int DefenseBuffId { get; set; }
    public Dictionary<int, Theatre6RankRecord> PvpRankRecords { get; set; } = new();
    public Theatre6BattleStats BattleStats { get; set; } = new();
    public int ActionPoint { get; set; }
    public long LastActionPointRecoverTime { get; set; }
}

// Final or per-round result of a Phantom Clash battle. Sent inside the DLC settlement response.
// Operation operands are the authored Theatre6PvpRank columns so the client's rank-progress
// presentation (BaseWinScore+EloScore, AllWinScore bonus, locked-score phases) is exact.
[MessagePackObject(true)]
public sealed class Theatre6PvpFightResult
{
    public List<bool> RoundResults { get; set; } = new();
    public bool IsFinalWin { get; set; }
    public int RankId { get; set; }
    public int OldScore { get; set; }
    public int NewScore { get; set; }
    public bool IsNewHistory { get; set; }
    public int BattlePhase { get; set; }
    public bool IsAdvanceBattleWin { get; set; }
    public Theatre6ScoreDetail ScoreDetail { get; set; } = new();
    public List<int> RewardedRanks { get; set; } = new();
    public List<RewardGoods> RankRewardGoods { get; set; } = new();
}

// PvE battle result. RewardGoodsList carries the mode's own reward records (RewardType /
// TemplateId / Amount / SkillId / AttrPack / BuffList), not generic account RewardGoods:
// XUiTheatre6FightReward classifies them with XEnumConst.Theatre6.EventRewardType.
[MessagePackObject(true)]
public sealed class Theatre6FightResult
{
    public List<Theatre6RewardData> RewardGoodsList { get; set; } = new();
}

#endregion
