using AscNet.Common.MsgPack;
using MessagePack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.guide;

namespace AscNet.GameServer.Handlers
{
    #region MsgPackScheme
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    [MessagePackObject(true)]
    public class GuideGroupFinishRequest
    {
        public int GroupId;
    }

    [MessagePackObject(true)]
    public class GuideGroupFinishResponse
    {
        public int Code;
        public List<RewardGoods>? RewardGoodsList;
    }

    [MessagePackObject(true)]
    public class GuideCompleteRequest
    {
        public int GuideGroupId;
    }

    [MessagePackObject(true)]
    public class NotifyGuide
    {
        public int GuideGroupId;
    }

    [MessagePackObject(true)]
    public class GuideCompleteResponse
    {
        public int Code;
        public List<RewardGoods>? RewardGoodsList;
    }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    #endregion

    internal class GuideModule
    {
        private static readonly Lazy<Dictionary<int, GuideGroupTable>> GuideGroups = new(() =>
            TableReaderV2.Parse<GuideGroupTable>().ToDictionary(guide => guide.Id));
        private static readonly Lazy<HashSet<int>> GuideCompletions = new(() =>
            TableReaderV2.Parse<GuideCompleteTable>().Select(completion => completion.Id).ToHashSet());
        [RequestPacketHandler("GuideOpenRequest")]
        public static void GuideOpenRequestHandler(Session session, Packet.Request packet)
        {
            GuideCompleteRequest request = packet.Deserialize<GuideCompleteRequest>();
            session.SendResponse(new GuideOpenResponse
            {
                Code = IsValidGuide(request.GuideGroupId) ? 0 : 1
            }, packet.Id);
        }

        [RequestPacketHandler("GuideGroupFinishRequest")]
        public static void GuideGroupFinishRequestHandler(Session session, Packet.Request packet)
        {
            GuideGroupFinishRequest request = packet.Deserialize<GuideGroupFinishRequest>();
            List<GuideGroupTable> groupGuides = GuideGroups.Value.Values
                .Where(guide => guide.GroupId == request.GroupId)
                .ToList();
            if (groupGuides.Count == 0)
            {
                session.SendResponse(new GuideGroupFinishResponse { Code = 1 }, packet.Id);
                return;
            }

            session.player.PlayerData.GuideData ??= new();
            List<GuideGroupTable> addedGuides = groupGuides
                .Where(guide => !session.player.PlayerData.GuideData.Contains(guide.Id))
                .ToList();
            if (addedGuides.Count == 0)
            {
                session.SendResponse(new GuideGroupFinishResponse(), packet.Id);
                return;
            }

            List<RewardGrant> rewardGrants = new();
            foreach (GuideGroupTable guide in addedGuides.Where(guide => guide.RewardId > 0))
            {
                var configuredRewards = RewardHandler.GetRewardGoods(guide.RewardId);
                if (configuredRewards.Count == 0)
                {
                    session.SendResponse(new GuideGroupFinishResponse { Code = 1 }, packet.Id);
                    return;
                }
                rewardGrants.Add(new RewardGrant($"guide:{guide.Id}", configuredRewards));
            }

            RewardApplicationResult? rewardApplication = null;
            try
            {
                if (rewardGrants.Count > 0)
                    rewardApplication = RewardHandler.ApplyRewardsOnceAndPersist(rewardGrants, session);
            }
            catch (Exception exception)
            {
                session.log.Error(
                    $"Failed to persist guide group rewards {request.GroupId}: {exception}");
                session.SendResponse(new GuideGroupFinishResponse { Code = 1 }, packet.Id);
                return;
            }

            List<long> addedGuideIds = addedGuides.Select(guide => (long)guide.Id).ToList();
            session.player.PlayerData.GuideData.AddRange(addedGuideIds);
            try
            {
                session.player.SaveChecked();
            }
            catch (Exception exception)
            {
                session.player.PlayerData.GuideData.RemoveAll(addedGuideIds.Contains);
                session.log.Error(
                    $"Failed to persist guide group completion {request.GroupId}: {exception}");
                session.SendResponse(new GuideGroupFinishResponse { Code = 1 }, packet.Id);
                return;
            }

            rewardApplication?.SendPushes(session);
            session.SendResponse(new GuideGroupFinishResponse
            {
                RewardGoodsList = rewardApplication?.RewardGoods
            }, packet.Id);
        }

        [RequestPacketHandler("GuideCompleteRequest")]
        public static void GuideCompleteRequestHandler(Session session, Packet.Request packet)
        {
            GuideCompleteRequest request = MessagePackSerializer.Deserialize<GuideCompleteRequest>(packet.Content);
            if (!GuideGroups.Value.TryGetValue(request.GuideGroupId, out GuideGroupTable? guide)
                || !GuideCompletions.Value.Contains(guide.CompleteId))
            {
                session.SendResponse(new GuideCompleteResponse { Code = 1 }, packet.Id);
                return;
            }

            session.player.PlayerData.GuideData ??= new();
            if (session.player.PlayerData.GuideData.Contains(request.GuideGroupId))
            {
                session.SendResponse(new GuideCompleteResponse(), packet.Id);
                return;
            }

            string claimKey = $"guide:{request.GuideGroupId}";

            RewardApplicationResult? rewardApplication = null;
            if (guide.RewardId is > 0)
            {
                var configuredRewards = RewardHandler.GetRewardGoods(guide.RewardId);
                if (configuredRewards.Count == 0)
                {
                    session.SendResponse(new GuideCompleteResponse { Code = 1 }, packet.Id);
                    return;
                }
                try
                {
                    rewardApplication = RewardHandler.ApplyRewardsOnceAndPersist(
                        [new RewardGrant(claimKey, configuredRewards)],
                        session);
                }
                catch (Exception exception)
                {
                    session.log.Error(
                        $"Failed to persist guide reward {request.GuideGroupId}: {exception}");
                    session.SendResponse(new GuideCompleteResponse { Code = 1 }, packet.Id);
                    return;
                }
            }

            session.player.PlayerData.GuideData.Add(request.GuideGroupId);
            try
            {
                session.player.SaveChecked();
            }
            catch (Exception exception)
            {
                session.player.PlayerData.GuideData.Remove(request.GuideGroupId);
                session.log.Error(
                    $"Failed to persist guide completion {request.GuideGroupId}: {exception}");
                session.SendResponse(new GuideCompleteResponse { Code = 1 }, packet.Id);
                return;
            }
            rewardApplication?.SendPushes(session);
            session.SendResponse(new GuideCompleteResponse
            {
                RewardGoodsList = rewardApplication?.RewardGoods
            }, packet.Id);
        }

        private static bool IsValidGuide(int guideGroupId)
            => GuideGroups.Value.TryGetValue(guideGroupId, out GuideGroupTable? guide)
                && GuideCompletions.Value.Contains(guide.CompleteId);
    }
}
