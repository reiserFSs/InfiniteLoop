using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.fuben.mainline;
using AscNet.Table.V2.share.reward;
using MessagePack;

namespace AscNet.GameServer.Handlers
{
    #region MsgPackScheme
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    [MessagePackObject(true)]
    public class TreasureRewardRequest
    {
        public int TreasureId;
    }

    [MessagePackObject(true)]
    public class TreasureRewardResponse
    {
        public int Code;
        public List<RewardGoods> RewardGoodsList { get; set; } = new();
    }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    #endregion

    internal class TreasureModule
    {
        [RequestPacketHandler("ReceiveTreasureRewardRequest")]
        public static void HandleReceiveTreasureRewardRequestHandler(Session session, Packet.Request packet)
        {
            TreasureRewardRequest request = packet.Deserialize<TreasureRewardRequest>();
            TreasureRewardResponse response = ClaimMainLineTreasureReward(session, request.TreasureId);
            session.SendResponse(response, packet.Id);
        }

        private static TreasureRewardResponse ClaimMainLineTreasureReward(Session session, int treasureId)
        {
            TreasureTable? treasure = TableReaderV2.Parse<TreasureTable>().FirstOrDefault(x => x.TreasureId == treasureId);
            if (treasure is null)
            {
                return new TreasureRewardResponse { Code = 20003008 };
            }

            if (session.player.FubenMainLineData.TreasureData.Contains(treasureId))
            {
                return new TreasureRewardResponse { Code = 20003010 };
            }

            if (!HasEnoughChapterStars(session, treasureId, treasure.RequireStar))
            {
                return new TreasureRewardResponse { Code = 20003009 };
            }

            List<RewardGoodsTable> rewardGoods = RewardHandler.GetRewardGoods(treasure.RewardId);
            if (rewardGoods.Count == 0)
            {
                return new TreasureRewardResponse { Code = 20003008 };
            }

            if (!session.player.AddTreasure(treasureId))
            {
                return new TreasureRewardResponse { Code = 20003010 };
            }

            RewardApplicationResult result = RewardHandler.ApplyRewards(rewardGoods, session);
            session.player.Save();
            session.inventory.Save();
            session.character.Save();
            result.SendPushes(session);

            return new TreasureRewardResponse
            {
                Code = 0,
                RewardGoodsList = result.RewardGoods
            };
        }

        private static bool HasEnoughChapterStars(Session session, int treasureId, int requiredStars)
        {
            return TableReaderV2.Parse<ChapterTable>()
                .Where(x => x.TreasureId.Contains(treasureId))
                .Any(chapter => CountChapterStars(session, chapter.StageId) >= requiredStars);
        }

        private static int CountChapterStars(Session session, IEnumerable<int> stageIds)
        {
            int total = 0;
            foreach (int stageId in stageIds)
            {
                if (session.stage.Stages.TryGetValue(stageId, out StageDatum? stageData) && stageData.Passed)
                {
                    total += CountStarBits(stageData.StarsMark);
                }
            }

            return total;
        }

        private static int CountStarBits(long starsMark)
        {
            int count = 0;
            while (starsMark > 0)
            {
                count += (int)(starsMark & 1);
                starsMark >>= 1;
            }

            return count;
        }
    }
}
