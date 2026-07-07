using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.equip;
using AscNet.Table.V2.share.fuben;
using AscNet.Table.V2.share.reward;
using MessagePack;

namespace AscNet.GameServer.Handlers
{
    #region MsgPackScheme
#pragma warning disable CS8618
    [MessagePackObject(true)]
    public class ReceivePrequelRewardRequest
    {
        public int StageId { get; set; }
    }

    [MessagePackObject(true)]
    public class ReceivePrequelRewardResponse
    {
        public int Code { get; set; }
        public int StageId { get; set; }
        public List<PrequelRewardGoods> RewardGoodsList { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class PrequelRewardGoods
    {
        public int RewardType { get; set; }
        public int TemplateId { get; set; }
        public int Count { get; set; }
        public int Level { get; set; }
        public int Quality { get; set; }
        public int Grade { get; set; }
        public int Breakthrough { get; set; }
        public int ConvertFrom { get; set; }
        public bool IsGift { get; set; }
        public int RewardMulti { get; set; }
        public int Id { get; set; }
    }
#pragma warning restore CS8618
    #endregion

    internal class PrequelModule
    {
        [RequestPacketHandler("ReceivePrequelRewardRequest")]
        public static void ReceivePrequelRewardRequestHandler(Session session, Packet.Request packet)
        {
            ReceivePrequelRewardRequest request = MessagePackSerializer.Deserialize<ReceivePrequelRewardRequest>(packet.Content);
            ReceivePrequelRewardResponse response = ClaimPrequelReward(session, request.StageId);
            session.SendResponse(response, packet.Id);
        }

        private static ReceivePrequelRewardResponse ClaimPrequelReward(Session session, int stageId)
        {
            StageTable? stage = TableReaderV2.Parse<StageTable>().FirstOrDefault(x => x.StageId == stageId);
            if (stage is null)
            {
                return new ReceivePrequelRewardResponse { Code = 1, StageId = stageId };
            }

            if (!session.stage.Stages.TryGetValue(stageId, out StageDatum? stageData) || !stageData.Passed)
            {
                return new ReceivePrequelRewardResponse { Code = 1, StageId = stageId };
            }

            session.stage.PrequelRewardedStages ??= new();
            if (session.stage.PrequelRewardedStages.Contains(stageId))
            {
                return new ReceivePrequelRewardResponse { Code = 0, StageId = stageId };
            }

            List<RewardGoodsTable> rewardGoodsTables = ResolvePrequelRewardGoods(stage);
            if (rewardGoodsTables.Count == 0)
            {
                return new ReceivePrequelRewardResponse { Code = 1, StageId = stageId };
            }

            List<PrequelRewardGoods> rewardGoodsList = new();
            List<Reward> rewards = new();
            foreach (RewardGoodsTable rewardGoodsTable in rewardGoodsTables)
            {
                RewardType? rewardType = RewardHandler.GetRewardType(rewardGoodsTable);
                if (rewardType is null)
                    continue;

                int level = ResolveRewardLevel(rewardGoodsTable);
                rewardGoodsList.Add(ToPrequelRewardGoods(rewardGoodsTable, rewardType.Value, level));
                rewards.Add(new Reward
                {
                    Type = rewardType.Value,
                    Id = rewardGoodsTable.TemplateId,
                    Count = rewardGoodsTable.Count,
                    Level = level > 0 ? level : 1,
                    IsRecycle = ShouldRecyclePrequelEquip(rewardType.Value, rewardGoodsTable.TemplateId)
                });
            }

            if (rewardGoodsList.Count == 0)
            {
                return new ReceivePrequelRewardResponse { Code = 1, StageId = stageId };
            }

            if (!session.stage.AddPrequelRewardedStage(stageId))
            {
                return new ReceivePrequelRewardResponse { Code = 0, StageId = stageId };
            }

            RewardHandler.GiveRewards(rewards, session);
            session.inventory.Save();
            session.character.Save();
            session.stage.Save();

            return new ReceivePrequelRewardResponse
            {
                Code = 0,
                StageId = stageId,
                RewardGoodsList = rewardGoodsList
            };
        }

        private static List<RewardGoodsTable> ResolvePrequelRewardGoods(StageTable stage)
        {
            int? rewardId = stage.FirstRewardId > 0 ? stage.FirstRewardId : stage.FirstRewardShow;
            if (rewardId is not > 0)
                return [];

            List<RewardGoodsTable> rewardGoodsTables = RewardHandler.GetRewardGoods(rewardId.Value);
            if (rewardGoodsTables.Count > 0)
                return rewardGoodsTables;

            if (stage.FirstRewardShow is > 0 && stage.FirstRewardShow != rewardId)
                return RewardHandler.GetRewardGoods(stage.FirstRewardShow.Value);

            return [];
        }

        private static PrequelRewardGoods ToPrequelRewardGoods(RewardGoodsTable rewardGoodsTable, RewardType rewardType, int level)
        {
            return new PrequelRewardGoods
            {
                RewardType = (int)rewardType,
                TemplateId = rewardGoodsTable.TemplateId,
                Count = rewardGoodsTable.Count,
                Level = level,
                Quality = 0,
                Grade = 0,
                Breakthrough = 0,
                ConvertFrom = 0,
                IsGift = false,
                RewardMulti = 0,
                Id = rewardGoodsTable.Id
            };
        }

        private static int ResolveRewardLevel(RewardGoodsTable rewardGoodsTable)
        {
            return rewardGoodsTable.Params.Count > 0 && rewardGoodsTable.Params[0] > 0
                ? rewardGoodsTable.Params[0]
                : 0;
        }

        private static bool ShouldRecyclePrequelEquip(RewardType rewardType, int templateId)
        {
            if (rewardType != RewardType.Equip)
                return false;

            EquipTable? equip = TableReaderV2.Parse<EquipTable>().FirstOrDefault(x => x.Id == templateId);
            return equip is { Type: 99, Site: > 0 };
        }
    }
}
