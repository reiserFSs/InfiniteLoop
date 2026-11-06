using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.miniactivity.pacman2;
using MessagePack;
using static AscNet.GameServer.Handlers.RewardHandler;

namespace AscNet.GameServer.Handlers;

[MessagePackObject(true)] public sealed class PacMan2StageData { public int StageId { get; set; } public int HighestScore { get; set; } }
[MessagePackObject(true)] public sealed class NotifyPacMan2Activity { public int ActivityId { get; set; } public List<PacMan2StageData> StageRecords { get; set; } = new(); }
[MessagePackObject(true)] public sealed class PacMan2StageStartRequest { public int StageId { get; set; } }
[MessagePackObject(true)] public sealed class PacMan2StageStartResponse { public int Code { get; set; } }
[MessagePackObject(true)] public sealed class PacMan2ShoeData { public int Id { get; set; } public int Times { get; set; } }
[MessagePackObject(true)] public sealed class PacMan2SettleRequest
{
    public int StageId { get; set; }
    public int Score { get; set; }
    public int SettleType { get; set; }
    public int OperateType { get; set; }
    public int ReStartTimes { get; set; }
    public int Time { get; set; }
    public int Orbs { get; set; }
    public int Kills { get; set; }
    public int Hp { get; set; }
    public bool IsLastHp { get; set; }
    public List<PacMan2ShoeData>? Shoe { get; set; }
    public int TakeBackPoint { get; set; }
    public int SubHpTimes { get; set; }
    public bool IsTriggerChase { get; set; }
}
[MessagePackObject(true)] public sealed class PacMan2SettleResponse { public int Code { get; set; } public PacMan2StageData? StageData { get; set; } public List<RewardGoods>? RewardGoodsList { get; set; } }

/// <summary>
/// Munchy Pixels (PacMan2). Client contract: XPacMan2Agency / XPacMan2Model / XUiPacMan2Game. The match runs on the
/// client; the server cannot replay it. AscNet policy (missing-retail policy, not recovered retail rules): the
/// reported score is accepted when the run was opened by a validated start request, the reported play time fits the
/// server-measured time since that start, and hp/counters are in range; stars derive from the authored thresholds.
/// Rewards: StarReward[i] when the best score first reaches star i, KeyReward the first time the stage is passed
/// (star >= 1, the client's own IsStagePassed), each claimed once per player.
/// </summary>
internal static class PacMan2Module
{
    internal const int ActivityNotOpen = 20251001, StageNotOpen = 20251002, StageConfigNotFound = 20251003,
        PreStageNotComplete = 20251004, StageGroupError = 20251005, SettleStageIdError = 20251006,
        SettleScoreError = 20251007, SettleTypeError = 20251008;
    internal const int SettleAbort = 0, SettleNormal = 1;
    // Client time is whole seconds of play; allow rounding and clock-read slack against the server clock.
    internal const int TimeToleranceSeconds = 5;

    private static readonly Lazy<List<PacMan2ActivityTable>> Activities = new(() => TableReaderV2.Parse<PacMan2ActivityTable>());
    private static readonly Lazy<Dictionary<int, PacMan2StageTable>> Stages = new(() => TableReaderV2.Parse<PacMan2StageTable>().ToDictionary(row => row.Id));

    /// <summary>Newest activity whose calendar is open (TimeId 0 = untimed).</summary>
    internal static PacMan2ActivityTable? ActiveActivity(DateTimeOffset now) => Activities.Value
        .Where(row => row.TimeId == 0 || ActivityScheduleService.IsOpen(row.TimeId, now))
        .OrderByDescending(row => row.Id).FirstOrDefault();

    internal static int Star(PacMan2StageTable stage, int score) => stage.Star.Count(threshold => score >= threshold);

    private static PacMan2State State(Player player) => player.PacMan2 ??= new();

    private static int BestScore(PacMan2State state, int stageId) => state.Records.FirstOrDefault(row => row.StageId == stageId)?.HighestScore ?? 0;

    private static bool Passed(PacMan2State state, int stageId) =>
        Stages.Value.TryGetValue(stageId, out PacMan2StageTable? stage) && Star(stage, BestScore(state, stageId)) > 0;

    internal static NotifyPacMan2Activity BuildLoginData(Player player, DateTimeOffset now)
    {
        PacMan2ActivityTable? activity = ActiveActivity(now);
        if (activity is null)
            return new NotifyPacMan2Activity();
        return new NotifyPacMan2Activity
        {
            ActivityId = activity.Id,
            StageRecords = State(player).Records.OrderBy(row => row.StageId)
                .Select(row => new PacMan2StageData { StageId = row.StageId, HighestScore = row.HighestScore }).ToList()
        };
    }

    [RequestPacketHandler("PacMan2StageStartRequest")]
    public static void StageStart(Session session, Packet.Request packet)
    {
        PacMan2StageStartRequest request = packet.Deserialize<PacMan2StageStartRequest>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        int code = ValidateStart(State(session.player), request.StageId, now);
        if (code == 0)
        {
            PacMan2State state = State(session.player);
            state.PlayingStageId = request.StageId;
            state.PlayingStartUnix = now.ToUnixTimeSeconds();
            session.player.Save();
        }
        session.SendResponse(new PacMan2StageStartResponse { Code = code }, packet.Id);
    }

    internal static int ValidateStart(PacMan2State state, int stageId, DateTimeOffset now)
    {
        if (ActiveActivity(now) is not { } activity)
            return ActivityNotOpen;
        if (!Stages.Value.TryGetValue(stageId, out PacMan2StageTable? stage))
            return StageConfigNotFound;
        if (stage.GroupId != activity.GroupId)
            return StageGroupError;
        if (stage.TimeId > 0 && !ActivityScheduleService.IsOpen(stage.TimeId, now))
            return StageNotOpen;
        if (stage.PreStageId > 0 && !Passed(state, stage.PreStageId))
            return PreStageNotComplete;
        return 0;
    }

    [RequestPacketHandler("PacMan2SettleRequest")]
    public static void Settle(Session session, Packet.Request packet)
    {
        PacMan2SettleRequest request = packet.Deserialize<PacMan2SettleRequest>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        PacMan2State state = State(session.player);
        int code = ValidateSettle(state, request, now);
        if (code != 0)
        {
            session.SendResponse(new PacMan2SettleResponse { Code = code }, packet.Id);
            return;
        }

        PacMan2StageTable stage = Stages.Value[request.StageId];
        PacMan2StageRecord? record = state.Records.FirstOrDefault(row => row.StageId == request.StageId);
        int oldStar = Star(stage, record?.HighestScore ?? 0);
        int bestScore = Math.Max(record?.HighestScore ?? 0, request.Score);
        int newStar = request.SettleType == SettleNormal ? Star(stage, bestScore) : oldStar;

        RewardApplicationResult? rewards = null;
        List<RewardGrant> grants = [];
        string prefix = $"pacman2:{session.player.PlayerData.Id}:{request.StageId}";
        if (oldStar == 0 && newStar > 0)
            grants.Add(new RewardGrant($"{prefix}:key", GetRewardGoods(stage.KeyReward)));
        for (int star = oldStar + 1; star <= newStar; star++)
            grants.Add(new RewardGrant($"{prefix}:star{star}", GetRewardGoods(stage.StarReward[star - 1])));
        grants = grants.Where(grant => grant.Goods.Count > 0).ToList();
        if (grants.Count > 0)
            rewards = ApplyRewardsOnceAndPersist(grants, session);

        if (request.SettleType == SettleNormal)
        {
            if (record is null)
                state.Records.Add(record = new PacMan2StageRecord { StageId = request.StageId });
            record.HighestScore = bestScore;
        }
        state.PlayingStageId = 0;
        state.PlayingStartUnix = 0;
        session.player.Save();

        rewards?.SendPushes(session);
        session.SendResponse(new PacMan2SettleResponse
        {
            Code = 0,
            StageData = new PacMan2StageData { StageId = request.StageId, HighestScore = bestScore },
            RewardGoodsList = rewards?.RewardGoods ?? []
        }, packet.Id);
    }

    internal static int ValidateSettle(PacMan2State state, PacMan2SettleRequest request, DateTimeOffset now)
    {
        if (ActiveActivity(now) is null)
            return ActivityNotOpen;
        if (!Stages.Value.TryGetValue(request.StageId, out PacMan2StageTable? stage))
            return StageConfigNotFound;
        if (state.PlayingStageId != request.StageId)
            return SettleStageIdError;
        if (request.SettleType is not (SettleAbort or SettleNormal))
            return SettleTypeError;
        long elapsed = now.ToUnixTimeSeconds() - state.PlayingStartUnix;
        bool plausible = request.Score >= 0 && request.Orbs >= 0 && request.Kills >= 0 && request.TakeBackPoint >= 0
            && request.SubHpTimes >= 0 && request.Time >= 0 && request.Time <= elapsed + TimeToleranceSeconds
            && request.Hp >= 0 && request.Hp <= stage.InitialHp
            // A cleared run is alive at the end.
            && (request.SettleType == SettleAbort || request.Hp > 0);
        return plausible ? 0 : SettleScoreError;
    }
}
