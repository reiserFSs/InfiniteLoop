using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.trpg.boss;
using MessagePack;

namespace AscNet.GameServer.Handlers
{
    #region MsgPackScheme
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    [MessagePackObject(true)]
    public class TRPGBossDetailResponse
    {
        public int Code { get; set; }
        public long LoseHp { get; set; }
        public long TotalHp { get; set; }
    }

    [MessagePackObject(true)]
    public class TRPGBossPhasesRewardRequest
    {
        public int Id { get; set; }
    }

    [MessagePackObject(true)]
    public class TRPGBossPhasesRewardResponse
    {
        public int Code { get; set; }
        public List<RewardGoods> RewardList { get; set; } = new();
    }

    /// <summary>XTRPGManager.NotifyTRPGClientBossData: BaseInfo has the NotifyTRPGData.BossInfo shape.</summary>
    [MessagePackObject(true)]
    public class NotifyTRPGClientBossData
    {
        public NotifyTRPGData.NotifyTRPGDataBossInfo BaseInfo { get; set; }
    }

    /// <summary>FightSettleResponse.Settle.TRPGBossFightResult, read by UiTRPGWinWorldBoss.</summary>
    [MessagePackObject(true)]
    public class TRPGBossFightResult
    {
        public int FightTime { get; set; }
        public long BossDamage { get; set; }
    }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    #endregion

    internal partial class TRPGModule
    {
        // CodeText 20104062-67 (Trpg*).
        private const int BossNotInOpenTime = 20104062, BossPhasesRewardIdIsError = 20104063,
            BossPhasesRewardAlreadyGet = 20104064, BossPercentNotEnough = 20104065, BossChallengeCountNotEnough = 20104067;
        private const int FightAuthorizationError = 1033, FightFramesPerSecond = 20;

        /// <summary>AscNet policy (missing-retail, not retail parity): TRPGBossHpConfig/HpControl are not shipped in the client tables,
        /// so the boss pool is per player and this size is ours. 80/60/40/20/0 % phases (TRPGBossPhasesReward) are measured against it.</summary>
        internal const long BossTotalHp = 10_000_000;

        private static TRPGBossTable Boss => TableReaderV2.Parse<TRPGBossTable>().Single(x => x.Id == 1);

        private static bool BossOpen => ActivityScheduleService.IsOpen(Boss.TimeId, DateTimeOffset.UtcNow);

        /// <summary>Attempts used in the current daily period (XTRPGBossInfo.ChallengeCount counts used attempts).</summary>
        private static int BossChallengeCount(TrpgShopBossState state) =>
            state.BossChallengeDay == TaskModule.CurrentDailyResetPeriod(DateTimeOffset.UtcNow.ToUnixTimeSeconds()) ? state.BossChallengeCount : 0;

        internal static NotifyTRPGData.NotifyTRPGDataBossInfo TrpgBossLogin(Session session)
        {
            TrpgShopBossState state = session.player.TrpgShopBoss;
            return new()
            {
                Id = Boss.Id,
                ChallengeCount = BossChallengeCount(state),
                PhasesRewardList = state.BossPhaseRewards.Cast<dynamic>().ToList()
            };
        }

        [RequestPacketHandler("TRPGBossDetailRequest")]
        public static void TRPGBossDetailRequestHandler(Session session, Packet.Request packet)
        {
            TRPGBossDetailResponse response = new();
            if (!BossOpen) response.Code = BossNotInOpenTime;
            else
            {
                response.LoseHp = Math.Min(session.player.TrpgShopBoss.BossLoseHp, BossTotalHp);
                response.TotalHp = BossTotalHp;
            }
            session.SendResponse(response, packet.Id);
        }

        [RequestPacketHandler("TRPGBossPhasesRewardRequest")]
        public static void TRPGBossPhasesRewardRequestHandler(Session session, Packet.Request packet)
        {
            TRPGBossPhasesRewardRequest request = packet.Deserialize<TRPGBossPhasesRewardRequest>();
            TrpgShopBossState state = session.player.TrpgShopBoss;
            TRPGBossPhasesRewardTable? phase = TableReaderV2.Parse<TRPGBossPhasesRewardTable>().Find(x => x.Id == request.Id);
            TRPGBossPhasesRewardResponse response = new();

            if (!BossOpen) response.Code = BossNotInOpenTime;
            else if (phase is null) response.Code = BossPhasesRewardIdIsError;
            else if (state.BossPhaseRewards.Contains(phase.Id)) response.Code = BossPhasesRewardAlreadyGet;
            // XTRPGManager.IsWorldBossCanReceiveReward: remaining HP fraction <= Percent / 100.
            else if ((double)(BossTotalHp - Math.Min(state.BossLoseHp, BossTotalHp)) / BossTotalHp > phase.Percent * 0.01) response.Code = BossPercentNotEnough;
            else
            {
                List<AscNet.Table.V2.share.reward.RewardGoodsTable> goods = RewardHandler.GetRewardGoods(phase.RewardId);
                RewardApplicationResult? result = goods.Count == 0 ? null
                    : RewardHandler.ApplyRewardsOnceAndPersist([new RewardGrant($"trpg:boss:phase:{phase.Id}", goods)], session);
                state.BossPhaseRewards.Add(phase.Id);
                session.player.SaveChecked();
                result?.SendPushes(session);
                response.RewardList = result?.RewardGoods ?? new();
            }

            session.SendResponse(response, packet.Id);
        }

        private static bool IsBossStage(uint stageId) => Boss.StageId == stageId;

        /// <summary>PreFight gate: 0 for any other stage, else boss window and daily attempts.</summary>
        internal static int ValidateBossPreFight(Session session, uint stageId)
        {
            if (!IsBossStage(stageId)) return 0;
            if (!BossOpen) return BossNotInOpenTime;
            return BossChallengeCount(session.player.TrpgShopBoss) >= Boss.ChallengeCount ? BossChallengeCountNotEnough : 0;
        }

        /// <summary>Settle gate, run before any reward is applied: window, attempts and the damage the result claims.</summary>
        internal static int ValidateBossSettle(Session session, FightSettleResult result)
        {
            if (!IsBossStage(result.StageId) || !result.IsWin || result.IsForceExit) return 0;
            int code = ValidateBossPreFight(session, result.StageId);
            if (code != 0) return code;
            // Native damage rows: TotalDamage never exceeds the sum of the team's DamageTotal (see GuildBossModule.ValidateBossResult).
            if (result.TotalDamage < 0 || result.StartFrame < 0 || result.SettleFrame < result.StartFrame || result.PauseFrame < 0
                || result.NpcDpsTable is null || result.NpcDpsTable.Values.Sum(x => Math.Max(0, x.DamageTotal)) < result.TotalDamage)
                return FightAuthorizationError;
            return 0;
        }

        /// <summary>Books a validated win: boss HP, the attempt, and the client's <c>NotifyTRPGClientBossData</c>.
        /// Mutates the player; the settle path persists it. Null for any other stage or a non-win.</summary>
        internal static TRPGBossFightResult? RecordBossSettle(Session session, FightSettleResult result)
        {
            if (!IsBossStage(result.StageId) || !result.IsWin || result.IsForceExit) return null;
            TrpgShopBossState state = session.player.TrpgShopBoss;
            state.BossChallengeCount = BossChallengeCount(state) + 1;
            state.BossChallengeDay = TaskModule.CurrentDailyResetPeriod(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            state.BossLoseHp = Math.Min(BossTotalHp, state.BossLoseHp + result.TotalDamage);
            session.SendPush(new NotifyTRPGClientBossData { BaseInfo = TrpgBossLogin(session) });
            return new TRPGBossFightResult
            {
                FightTime = checked((int)Math.Max(0, result.SettleFrame - result.StartFrame - result.PauseFrame) / FightFramesPerSecond),
                BossDamage = result.TotalDamage
            };
        }
    }
}
