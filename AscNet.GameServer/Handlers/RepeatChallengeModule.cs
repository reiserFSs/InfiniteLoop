using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.condition;
using AscNet.Table.V2.share.fuben;
using AscNet.Table.V2.share.fuben.repeatchallenge;
using MessagePack;

namespace AscNet.GameServer.Handlers
{
    [MessagePackObject(true)]
    public class RepeatChallengeRewardRequest
    {
        public int Id { get; set; }
    }

    [MessagePackObject(true)]
    public class RepeatChallengeRewardResponse
    {
        public int Code { get; set; }
        public List<RewardGoods> RewardGoodsList { get; set; } = [];
    }

    [MessagePackObject(true)]
    public class NotifyRcExpChange
    {
        [MessagePackObject(true)]
        public class RcExpInfo
        {
            public int Level { get; set; }
            public int Exp { get; set; }
        }

        public RcExpInfo ExpInfo { get; set; } = new();
    }

    internal static class RepeatChallengeModule
    {
        private sealed record Data(
            RepeatChallengeActivityTable Activity,
            RepeatChallengeChapterTable Chapter,
            RepeatChallengeStageTable Stage,
            StageTable StageData,
            IReadOnlyList<RepeatChallengeLevelTable> Levels,
            IReadOnlyDictionary<int, RepeatChallengeRewardTable> Rewards,
            IReadOnlyDictionary<int, ConditionTable> Conditions);

        private static readonly Lazy<Data> Runtime = new(Load);

        public static NotifyRepeatChallengeData BuildLoginData(Player player)
        {
            SimulatedBattlefieldState state = GetState(player, out bool changed);
            if (changed) player.Save();
            Data data = Runtime.Value;
            return new NotifyRepeatChallengeData
            {
                Id = data.Activity.Id,
                ExpInfo = ToExpInfo(state),
                RcChapters =
                [
                    new Dictionary<string, object>
                    {
                        ["Id"] = data.Chapter.Id,
                        ["FinishStages"] = state.RepeatChallengeCleared ? new[] { data.Stage.Id } : Array.Empty<int>()
                    }
                ],
                RewardIds = state.RepeatChallengeClaimedRewardIds.Cast<dynamic>().ToList()
            };
        }

        public static bool IsStage(uint stageId) => Runtime.Value.Stage.Id == stageId;

        public static void ApplyPreFight(Player player, PreFightResponse.PreFightResponseFightData fightData)
        {
            if (!IsStage(fightData.StageId)) return;
            SimulatedBattlefieldState state = GetState(player, out bool changed);
            if (changed) player.Save();
            Data data = Runtime.Value;
            fightData.RebootId = data.StageData.RebootId ?? 0;
            fightData.MonsterLevel = ResolveMonsterLevels((int)player.PlayerData.Level);
            fightData.EventIds = data.Levels.Take(state.RepeatChallengeLevel)
                .Select(level => level.BuffId)
                .Where(id => id > 0)
                .Cast<dynamic>()
                .ToList();
        }

        public static bool RecordStageClear(Player player, uint stageId, int challengeCount)
        {
            if (!IsStage(stageId) || challengeCount <= 0) return false;
            SimulatedBattlefieldState state = GetState(player, out bool normalized);
            int oldLevel = state.RepeatChallengeLevel, oldExp = state.RepeatChallengeExp;
            bool oldCleared = state.RepeatChallengeCleared;
            state.RepeatChallengeExp = Math.Min(MaxExp(), checked(state.RepeatChallengeExp + checked(Runtime.Value.Stage.ExpAdd * challengeCount)));
            state.RepeatChallengeLevel = ResolveLevel(state.RepeatChallengeExp);
            state.RepeatChallengeCleared = true;
            return normalized || oldLevel != state.RepeatChallengeLevel || oldExp != state.RepeatChallengeExp || oldCleared != state.RepeatChallengeCleared;
        }

        public static NotifyRcExpChange BuildExpChange(Player player)
        {
            SimulatedBattlefieldState state = GetState(player, out _);
            return new NotifyRcExpChange { ExpInfo = new() { Level = state.RepeatChallengeLevel, Exp = state.RepeatChallengeExp } };
        }

        [RequestPacketHandler("RepeatChallengeRewardRequest")]
        public static void RepeatChallengeRewardRequestHandler(Session session, Packet.Request packet)
        {
            RepeatChallengeRewardRequest request = packet.Deserialize<RepeatChallengeRewardRequest>();
            if (!Runtime.Value.Rewards.TryGetValue(request.Id, out RepeatChallengeRewardTable? reward)
                || !TryGetRewardLevel(reward, out int requiredLevel))
            {
                session.SendResponse(new RepeatChallengeRewardResponse { Code = 1 }, packet.Id);
                return;
            }

            SimulatedBattlefieldState state = GetState(session.player, out bool normalized);
            if (state.RepeatChallengeClaimedRewardIds.Contains(reward.Id) || state.RepeatChallengeLevel < requiredLevel)
            {
                if (normalized) session.player.Save();
                session.SendResponse(new RepeatChallengeRewardResponse { Code = 1 }, packet.Id);
                return;
            }

            List<AscNet.Table.V2.share.reward.RewardGoodsTable> goods = RewardHandler.GetRewardGoods(reward.RewardId);
            if (goods.Count == 0)
            {
                session.SendResponse(new RepeatChallengeRewardResponse { Code = 1 }, packet.Id);
                return;
            }

            RewardApplicationResult application;
            try
            {
                application = RewardHandler.ApplyRewardsOnceAndPersist(
                    [new RewardGrant($"repeat-challenge:{Runtime.Value.Activity.Id}:{session.player.PlayerData.Id}:{reward.Id}", goods)], session);
                state.RepeatChallengeClaimedRewardIds.Add(reward.Id);
                state.RepeatChallengeClaimedRewardIds.Sort();
                session.player.SaveChecked();
            }
            catch
            {
                state.RepeatChallengeClaimedRewardIds.Remove(reward.Id);
                throw;
            }

            application.SendPushes(session);
            session.SendResponse(new RepeatChallengeRewardResponse { Code = 0, RewardGoodsList = application.RewardGoods }, packet.Id);
        }

        public static bool TrySweep(Session session, int count, out List<List<RewardGoods>> rewards, out RewardApplicationResult? application)
        {
            rewards = [];
            application = null;
            if (count <= 0 || !GetState(session.player, out _).RepeatChallengeCleared
                || !TryGetSweepLevel(out int sweepLevel)
                || GetState(session.player, out _).RepeatChallengeLevel < sweepLevel)
                return false;

            Data data = Runtime.Value;
            int actionPointCost = data.Stage.ActionPoint;
            if (actionPointCost <= 0 || count > int.MaxValue / actionPointCost)
                return false;
            int totalCost = actionPointCost * count;
            long actionPoints = session.inventory.Items.FirstOrDefault(item => item.Id == Inventory.ActionPoint)?.Count ?? 0;
            if (actionPoints < totalCost)
                return false;

            List<AscNet.Table.V2.share.reward.RewardGoodsTable> configured = RewardHandler.GetRewardGoods(data.StageData.FinishDropId ?? 0);
            configured.AddRange(RewardHandler.GetRewardGoods(ResolveLevelControl((int)session.player.PlayerData.Level).FinishDropId ?? 0));
            if (configured.Count == 0)
                return false;
            List<AscNet.Table.V2.share.reward.RewardGoodsTable> all = [];
            for (int i = 0; i < count; i++) all.AddRange(configured);
            try
            {
                application = RewardHandler.ApplyRewards(all, session);
                application.ItemData.ItemDataList.Add(session.inventory.Do(Inventory.ActionPoint, -totalCost));
                if (RecordStageClear(session.player, (uint)data.Stage.Id, count))
                    session.player.SaveChecked();
                session.inventory.Save();
                session.character.Save();
            }
            catch
            {
                throw;
            }
            for (int i = 0; i < count; i++)
                rewards.Add(application.RewardGoods.Skip(i * configured.Count).Take(configured.Count).ToList());
            return true;
        }

        private static Data Load()
        {
            RepeatChallengeActivityTable activity = TableReaderV2.Parse<RepeatChallengeActivityTable>()
                .OrderBy(row => row.Id).FirstOrDefault() ?? throw new InvalidDataException("RepeatChallengeActivity has no activity.");
            int chapterId = activity.NormalChapter;
            RepeatChallengeChapterTable chapter = TableReaderV2.Parse<RepeatChallengeChapterTable>()
                .SingleOrDefault(row => row.Id == chapterId) ?? throw new InvalidDataException($"RepeatChallengeActivity {activity.Id} has no chapter.");
            int stageId = chapter.StageId;
            RepeatChallengeStageTable stage = TableReaderV2.Parse<RepeatChallengeStageTable>()
                .SingleOrDefault(row => row.Id == stageId) ?? throw new InvalidDataException($"RepeatChallengeChapter {chapter.Id} has no stage progression.");
            StageTable stageData = TableReaderV2.Parse<StageTable>()
                .SingleOrDefault(row => row.StageId == stageId) ?? throw new InvalidDataException($"RepeatChallenge stage {stageId} is missing Stage data.");
            RepeatChallengeLevelTable[] levels = TableReaderV2.Parse<RepeatChallengeLevelTable>().OrderBy(row => row.Id).ToArray();
            if (levels.Length == 0 || levels.Select(row => row.Id).SequenceEqual(Enumerable.Range(1, levels.Length)) is false)
                throw new InvalidDataException("RepeatChallengeLevel IDs must be contiguous from 1.");
            return new(activity, chapter, stage, stageData, levels,
                TableReaderV2.Parse<RepeatChallengeRewardTable>().ToDictionary(row => row.Id),
                TableReaderV2.Parse<ConditionTable>().ToDictionary(row => row.Id));
        }

        private static SimulatedBattlefieldState GetState(Player player, out bool changed)
        {
            player.SimulatedBattlefield ??= new();
            SimulatedBattlefieldState state = player.SimulatedBattlefield;
            bool claimsMissing = state.RepeatChallengeClaimedRewardIds is null;
            state.RepeatChallengeClaimedRewardIds ??= [];
            Data data = Runtime.Value;
            changed = claimsMissing;
            if (state.RepeatChallengeActivityId != data.Activity.Id)
            {
                state.RepeatChallengeActivityId = data.Activity.Id;
                state.RepeatChallengeLevel = 1;
                state.RepeatChallengeExp = 0;
                state.RepeatChallengeCleared = false;
                state.RepeatChallengeClaimedRewardIds = [];
                changed = true;
            }
            int level = Math.Clamp(state.RepeatChallengeLevel, 1, data.Levels.Count);
            int exp = Math.Clamp(state.RepeatChallengeExp, 0, MaxExp());
            List<int> claims = state.RepeatChallengeClaimedRewardIds.Where(data.Rewards.ContainsKey).Distinct().OrderBy(id => id).ToList();
            changed |= level != state.RepeatChallengeLevel || exp != state.RepeatChallengeExp || !claims.SequenceEqual(state.RepeatChallengeClaimedRewardIds);
            state.RepeatChallengeLevel = level;
            state.RepeatChallengeExp = exp;
            state.RepeatChallengeClaimedRewardIds = claims;
            return state;
        }

        private static List<int> ResolveMonsterLevels(int playerLevel)
        {
            StageLevelControlTable control = ResolveLevelControl(playerLevel);
            if (control.MonsterLevel.Count == 0 || control.MonsterLevel.Any(level => level <= 0))
                throw new InvalidDataException($"RepeatChallenge stage {Runtime.Value.Stage.Id} has no valid monster-level control.");
            return control.MonsterLevel;
        }

        private static StageLevelControlTable ResolveLevelControl(int playerLevel)
        {
            StageLevelControlTable? control = TableReaderV2.Parse<StageLevelControlTable>()
                .Where(row => row.StageId == Runtime.Value.Stage.Id)
                .OrderBy(row => row.MaxLevel)
                .FirstOrDefault(row => playerLevel <= row.MaxLevel)
                ?? TableReaderV2.Parse<StageLevelControlTable>().Where(row => row.StageId == Runtime.Value.Stage.Id).OrderBy(row => row.MaxLevel).LastOrDefault();
            return control ?? throw new InvalidDataException($"RepeatChallenge stage {Runtime.Value.Stage.Id} has no level control.");
        }

        private static int ResolveLevel(int exp)
        {
            int cumulative = 0, level = 1;
            foreach (RepeatChallengeLevelTable row in Runtime.Value.Levels.Take(Runtime.Value.Levels.Count - 1))
            {
                cumulative = checked(cumulative + Required(row.UpExp, $"RepeatChallengeLevel {row.Id}.UpExp"));
                if (exp < cumulative) break;
                level = row.Id + 1;
            }
            return level;
        }

        private static int MaxExp() => Runtime.Value.Levels.Sum(level => Required(level.UpExp, $"RepeatChallengeLevel {level.Id}.UpExp"));
        private static NotifyRepeatChallengeData.NotifyRepeatChallengeDataExpInfo ToExpInfo(SimulatedBattlefieldState state) => new() { Level = state.RepeatChallengeLevel, Exp = state.RepeatChallengeExp };
        private static int Required(int? value, string field) => value is > 0 ? value.Value : throw new InvalidDataException($"{field} must be positive.");
        private static bool TryGetSweepLevel(out int level)
        {
            level = 0;
            return Runtime.Value.Conditions.TryGetValue(Runtime.Value.Activity.SweepCondition, out ConditionTable? condition)
                && condition.Type == 11108
                && condition.Params.FirstOrDefault() is > 0
                && (level = condition.Params[0]) > 0;
        }

        private static bool TryGetRewardLevel(RepeatChallengeRewardTable reward, out int level)
        {
            level = 0;
            return Runtime.Value.Conditions.TryGetValue(reward.Condition, out ConditionTable? condition)
                && condition.Type == 11108
                && condition.Params.FirstOrDefault() is > 0
                && (level = condition.Params[0]) > 0;
        }
    }
}
