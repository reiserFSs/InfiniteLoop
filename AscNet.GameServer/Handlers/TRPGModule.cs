using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.condition;
using AscNet.Table.V2.share.trpg;
using AscNet.Table.V2.share.trpg.secondmain;
using MessagePack;

namespace AscNet.GameServer.Handlers
{
    #region MsgPackScheme
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    [MessagePackObject(true)]
    public class TRPGFunctionFinishRequest
    {
        public int FunctionId { get; set; }
    }

    [MessagePackObject(true)]
    public class TRPGFunctionFinishResponse
    {
        public int Code { get; set; }
        public List<RewardGoods> RewardList { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class TRPGChangePageStatusRequest
    {
        public bool Status { get; set; }
    }

    [MessagePackObject(true)]
    public class TRPGChangePageStatusResponse
    {
        public int Code { get; set; }
    }

    [MessagePackObject(true)]
    public class TRPGSelectTargetLinkRequest
    {
        public int Id { get; set; }
    }

    [MessagePackObject(true)]
    public class TRPGSelectTargetLinkResponse
    {
        public int Code { get; set; }
    }

    [MessagePackObject(true)]
    public class TRPGRewardRequest
    {
        public int Id { get; set; }
    }

    [MessagePackObject(true)]
    public class TRPGRewardResponse
    {
        public int Code { get; set; }
        public List<RewardGoods> RewardList { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class TRPGOpenMemoirRequest
    {
        public int Id { get; set; }
    }

    [MessagePackObject(true)]
    public class TRPGOpenMemoirResponse
    {
        public int Code { get; set; }
    }

    [MessagePackObject(true)]
    public class NotifyTRPGTargetFinish
    {
        public List<int> TargetList { get; set; } = new();
    }

    /// <summary>XTRPGManager.NotifyTRPGSyncInfo: finished targets plus normal-mode stages that now count as passed.</summary>
    [MessagePackObject(true)]
    public class NotifyTRPGSyncInfo
    {
        public List<int> TargetList { get; set; } = new();
        public List<int> StageList { get; set; } = new();
    }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    #endregion

    internal partial class TRPGModule
    {
        // CodeText 20104xxx (Trpg*): retail server error codes.
        private const int TargetLinkNotExist = 20104001, FuncCfgNotExist = 20104007, FuncNotNeedSave = 20104008,
            FuncIsFinished = 20104011, FuncStageNotFinish = 20104012, MemoirIdError = 20104057,
            MemoirAlreadyUnlock = 20104058, MemoirItemNotEnough = 20104059, RewardIdError = 20104060,
            RewardAlreadyGet = 20104061, PageStatusNotChange = 20104068, ConditionNotPass = 20104069;
        private const int FunctionTypeCommitItem = 3, FunctionTypeFinishStage = 4, FunctionTypeExamine = 5,
            TargetTypeGainRole = 2, TargetTypeFinishStage = 3, TargetTypeGainItem = 4, TargetTypeCommitItem = 5, TargetTypeFunction = 7;
        private const int ConditionTrpgTarget = 10119, ConditionStagePassed = 10105;

        private static List<TRPGTargetLinkTable> TargetLinks => TableReaderV2.Parse<TRPGTargetLinkTable>();
        private static List<TRPGTargetTable> Targets => TableReaderV2.Parse<TRPGTargetTable>();
        private static List<TRPGSecondMainStageTable> SecondMainStages => TableReaderV2.Parse<TRPGSecondMainStageTable>();

        internal static NotifyTRPGData BuildLoginData(Session session)
        {
            TrpgState state = session.player.Trpg;
            // Chapter stages cleared before the TRPG layer persisted targets still owe their synced targets.
            List<int> owed = MarkTargets(state, TableReaderV2.Parse<TRPGSyncStageTable>().Where(x => IsStagePassed(session, x.Id)).SelectMany(x => x.SyncTargetIds.Where(id => id > 0)));
            owed.AddRange(MarkDerivedTargets(session));
            if (owed.Count > 0) session.player.SaveChecked();
            return new()
            {
                // Lowest Id is the opening chain (Fake Ascension: 10001), the same chain a fresh player starts on.
                CurTargetLink = (uint)(state.CurTargetLink > 0 ? state.CurTargetLink : TargetLinks.Min(x => x.Id)),
                BaseInfo = TrpgExplore.BaseInfo(session),
                BossInfo = TrpgBossLogin(session),
                TargetList = state.Targets.Cast<dynamic>().ToList(),
                RewardList = state.Rewards.Cast<dynamic>().ToList(),
                FuncList = state.Functions.ToList(),
                Characters = TrpgTalents.Characters(session).Cast<dynamic>().ToList(),
                ShopInfos = TrpgShopLogin(session),
                MazeInfos = TrpgMazeLogin(session),
                MemoirList = state.Memoirs.Cast<dynamic>().ToList(),
                ItemCapacityAdd = TrpgTalents.ItemCapacityAdd(session),
                IsNormalPage = state.IsNormalPage,
                StageList = SecondMainStages.Select(x => x.StageId).Where(x => IsStagePassed(session, x)).Cast<dynamic>().ToList()
            };
        }

        internal static bool TargetFinished(Player player, int targetId) => player.Trpg.Targets.Contains(targetId);

        private static bool IsStagePassed(Session session, int stageId) =>
            session.stage.Stages.TryGetValue((uint)stageId, out StageDatum? stage) && stage.Passed;

        /// <summary>Client Condition rows the TRPG tables use: finished target (10119) or passed stage (10105).</summary>
        private static bool ConditionSatisfied(Session session, int conditionId)
        {
            ConditionTable? row = TableReaderV2.Parse<ConditionTable>().Find(x => x.Id == conditionId);
            if (row is null || row.Params.Count == 0) return false;
            return row.Type switch
            {
                ConditionTrpgTarget => row.Params.All(x => TargetFinished(session.player, x)),
                ConditionStagePassed => row.Params.All(x => IsStagePassed(session, x)),
                _ => false
            };
        }

        /// <summary>Marks the known targets finished; returns the newly finished ids. No save, no push: see <see cref="Settle"/>.</summary>
        private static List<int> MarkTargets(TrpgState state, IEnumerable<int> targetIds)
        {
            List<int> added = new();
            foreach (int id in targetIds.Distinct())
            {
                if (state.Targets.Contains(id) || Targets.All(x => x.Id != id)) continue;
                state.Targets.Add(id);
                added.Add(id);
            }
            return added;
        }

        /// <summary>AscNet rule (tables do not say how these finish): gain-investigator (2) and gain-item (4) targets finish once the investigator is owned / the TRPG item was obtained.</summary>
        private static List<int> MarkDerivedTargets(Session session)
        {
            List<int> all = new();
            for (List<int> added; (added = MarkTargets(session.player.Trpg, Targets.Where(x =>
                (x.Type == TargetTypeGainRole && TrpgTalents.Roles.Any(r => r.Id == x.Params && TargetFinished(session.player, r.UnlockTarget)))
                || (x.Type == TargetTypeGainItem && TrpgItems.Config(x.Params) is not null && TrpgItems.Count(session, x.Params) > 0)).Select(x => x.Id))).Count > 0;)
                all.AddRange(added);
            return all;
        }

        /// <summary>Retail's exploration-to-regular sync (TRPGSyncTarget): a finished target passes the regular-mode stages it stands for.</summary>
        private static List<int> SyncStages(Session session, IEnumerable<int> finishedTargets)
        {
            List<int> stages = TableReaderV2.Parse<TRPGSyncTargetTable>().Where(x => finishedTargets.Contains(x.Id))
                .SelectMany(x => x.SyncStageIds.Where(id => id > 0)).Distinct().Where(id => !IsStagePassed(session, id)).ToList();
            if (stages.Count == 0) return stages;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            List<StageDatum> data = stages.Select(id => new StageDatum
            {
                StageId = (uint)id,
                Passed = true,
                PassTimesTotal = 1,
                CreateTime = now,
                LastPassTime = now,
                BestCardIds = [],
                LastCardIds = []
            }).ToList();
            data.ForEach(session.stage.AddStage);
            session.stage.Save();
            session.SendPush(new NotifyStageData { StageList = data });
            stages.ForEach(id => TaskModule.RecordStageClear(session, id, 1, 0));
            return stages;
        }

        /// <summary>Everything that follows newly finished targets: derived targets, regular-stage sync, new investigators; then persists and pushes.
        /// Returns every newly finished target (derived ones included).</summary>
        private static List<int> Settle(Session session, List<int> added, IEnumerable<int> extraStages)
        {
            added.AddRange(MarkDerivedTargets(session));
            List<int> stages = SyncStages(session, added).Concat(extraStages).ToList();
            if (added.Count > 0) session.player.SaveChecked();
            List<TRPGRoleTable> unlocked = TrpgTalents.Roles.Where(x => added.Contains(x.UnlockTarget)).ToList();
            if (unlocked.Count > 0) TrpgTalents.PushCharacters(session, unlocked);
            if (stages.Count > 0) session.SendPush(new NotifyTRPGSyncInfo { TargetList = added, StageList = stages });
            else if (added.Count > 0) session.SendPush(new NotifyTRPGTargetFinish { TargetList = added });
            return added;
        }

        /// <summary>Finishes the targets (maze cards, functions, ...) with all consequences; returns the newly finished ids.</summary>
        internal static List<int> FinishTargets(Session session, IEnumerable<int> targetIds) =>
            Settle(session, MarkTargets(session.player.Trpg, targetIds), []);

        /// <summary>Re-evaluates gain-item / gain-investigator targets after something was granted outside the TRPG handlers.</summary>
        internal static void RefreshDerivedTargets(Session session) => Settle(session, [], []);

        /// <summary>Normal-mode (TRPGSyncStage) and exploration-mode (finish-stage target) targets a cleared stage completes.</summary>
        internal static void OnStageCleared(Session session, uint stageId)
        {
            int stage = checked((int)stageId);
            IEnumerable<int> synced = TableReaderV2.Parse<TRPGSyncStageTable>().Where(x => x.Id == stage)
                .SelectMany(x => x.SyncTargetIds.Where(id => id > 0));
            IEnumerable<int> staged = Targets.Where(x => x.Type == TargetTypeFinishStage && x.Params == stage).Select(x => x.Id);
            bool normalStage = SecondMainStages.Any(x => x.StageId == stage);
            Settle(session, MarkTargets(session.player.Trpg, synced.Concat(staged)), normalStage ? [stage] : []);
        }

        [RequestPacketHandler("TRPGFunctionFinishRequest")]
        public static void TRPGFunctionFinishRequestHandler(Session session, Packet.Request packet)
        {
            TRPGFunctionFinishRequest request = packet.Deserialize<TRPGFunctionFinishRequest>();
            TrpgState state = session.player.Trpg;
            TRPGFunctionTable? function = TableReaderV2.Parse<TRPGFunctionTable>().Find(x => x.Id == request.FunctionId);
            TRPGFunctionFinishResponse response = new();
            // Commit-item function (XTRPGManager: Params = [itemId, count]) hands the items in.
            int commitItem = function?.Type == FunctionTypeCommitItem && int.TryParse(function.Params.ElementAtOrDefault(0), out int item) ? item : 0;
            int commitCount = commitItem > 0 && int.TryParse(function!.Params.ElementAtOrDefault(1), out int count) ? count : 1;

            if (function is null) response.Code = FuncCfgNotExist;
            else if (function.NeedSave != 1) response.Code = FuncNotNeedSave;
            else if (state.Functions.Contains(function.Id)) response.Code = FuncIsFinished;
            else if (CheckFunctionOrder(session, function) is int order and not 0) response.Code = order;
            else if (function.Type == FunctionTypeFinishStage && !(int.TryParse(function.Params.FirstOrDefault(), out int stageId) && IsStagePassed(session, stageId)))
                response.Code = FuncStageNotFinish;
            else if (function.Type == FunctionTypeExamine && !(int.TryParse(function.Params.FirstOrDefault(), out int examineId) && ExaminePassed(session.player, examineId)))
                response.Code = FuncExamineNotFinish;
            else if (function.Type == FunctionTypeCommitItem && TrpgItems.Count(session, commitItem) < commitCount)
                response.Code = UseItemCountNotEnough;
            else
            {
                List<AscNet.Table.V2.share.reward.RewardGoodsTable> goods = function.RewardId is > 0 ? RewardHandler.GetRewardGoods(function.RewardId.Value) : [];
                Dictionary<int, int>? costs = commitItem > 0 ? new() { [commitItem] = commitCount } : null;
                if (goods.Count > 0 || costs is not null)
                {
                    RewardApplicationResult granted = RewardHandler.ApplyRewardsOnceAndPersist([new RewardGrant($"trpg:function:{function.Id}", goods, costs)], session);
                    response.RewardList = granted.RewardGoods;
                    granted.SendPushes(session);
                }
                state.Functions.Add(function.Id);
                // Handing in an item finishes the commit-item targets for it (TRPGTarget Type 5, Params = item id).
                IEnumerable<int> targets = Targets.Where(x => (x.Type == TargetTypeFunction && x.Params == function.Id)
                    || (commitItem > 0 && x.Type == TargetTypeCommitItem && x.Params == commitItem)).Select(x => x.Id);
                session.player.SaveChecked();
                FinishTargets(session, targets);
            }

            session.SendResponse(response, packet.Id);
        }

        [RequestPacketHandler("TRPGChangePageStatusRequest")]
        public static void TRPGChangePageStatusRequestHandler(Session session, Packet.Request packet)
        {
            TRPGChangePageStatusRequest request = packet.Deserialize<TRPGChangePageStatusRequest>();
            TrpgState state = session.player.Trpg;
            TRPGChangePageStatusResponse response = new();

            if (state.IsNormalPage == request.Status) response.Code = PageStatusNotChange;
            else
            {
                state.IsNormalPage = request.Status;
                session.player.SaveChecked();
            }

            session.SendResponse(response, packet.Id);
        }

        [RequestPacketHandler("TRPGSelectTargetLinkRequest")]
        public static void TRPGSelectTargetLinkRequestHandler(Session session, Packet.Request packet)
        {
            TRPGSelectTargetLinkRequest request = packet.Deserialize<TRPGSelectTargetLinkRequest>();
            TRPGSelectTargetLinkResponse response = new();

            if (TargetLinks.All(x => x.Id != request.Id)) response.Code = TargetLinkNotExist;
            else
            {
                session.player.Trpg.CurTargetLink = request.Id;
                session.player.SaveChecked();
            }

            session.SendResponse(response, packet.Id);
        }

        [RequestPacketHandler("TRPGRewardRequest")]
        public static void TRPGRewardRequestHandler(Session session, Packet.Request packet)
        {
            TRPGRewardRequest request = packet.Deserialize<TRPGRewardRequest>();
            TrpgState state = session.player.Trpg;
            TRPGRewardTable? reward = TableReaderV2.Parse<TRPGRewardTable>().Find(x => x.Id == request.Id);
            TRPGRewardResponse response = new();

            if (reward is null) response.Code = RewardIdError;
            else if (state.Rewards.Contains(reward.Id)) response.Code = RewardAlreadyGet;
            // Condition[1] gates exploration mode, Condition[2] regular mode (XTRPGManager.CheckRewardCondition); either unlocks the claim.
            else if (!reward.Condition.Any(x => x == 0 || ConditionSatisfied(session, x))) response.Code = ConditionNotPass;
            else
            {
                List<AscNet.Table.V2.share.reward.RewardGoodsTable> goods = RewardHandler.GetRewardGoods(reward.RewardId);
                if (goods.Count > 0)
                {
                    RewardApplicationResult granted = RewardHandler.ApplyRewardsOnceAndPersist([new RewardGrant($"trpg:reward:{reward.Id}", goods)], session);
                    response.RewardList = granted.RewardGoods;
                    granted.SendPushes(session);
                }
                state.Rewards.Add(reward.Id);
                session.player.SaveChecked();
            }

            session.SendResponse(response, packet.Id);
        }

        [RequestPacketHandler("TRPGOpenMemoirRequest")]
        public static void TRPGOpenMemoirRequestHandler(Session session, Packet.Request packet)
        {
            TRPGOpenMemoirRequest request = packet.Deserialize<TRPGOpenMemoirRequest>();
            TrpgState state = session.player.Trpg;
            TRPGMemoirStoryTable? memoir = TableReaderV2.Parse<TRPGMemoirStoryTable>().Find(x => x.Id == request.Id);
            TRPGOpenMemoirResponse response = new();

            if (memoir is null) response.Code = MemoirIdError;
            else if (state.Memoirs.Contains(memoir.Id)) response.Code = MemoirAlreadyUnlock;
            else if (session.inventory.SpendableCount(memoir.UnlockItemId) < memoir.UnlockItemCount) response.Code = MemoirItemNotEnough;
            else
            {
                RewardApplicationResult spent = RewardHandler.ApplyRewardsOnceAndPersist(
                    [new RewardGrant($"trpg:memoir:{memoir.Id}", [], new Dictionary<int, int> { [memoir.UnlockItemId] = memoir.UnlockItemCount })], session);
                state.Memoirs.Add(memoir.Id);
                session.player.SaveChecked();
                spent.SendPushes(session);
            }

            session.SendResponse(response, packet.Id);
        }
    }
}
