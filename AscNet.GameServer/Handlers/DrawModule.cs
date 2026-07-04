using AscNet.Common.MsgPack;
using AscNet.GameServer.Game;
using MessagePack;

namespace AscNet.GameServer.Handlers
{
    #region MsgPackScheme
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    [MessagePackObject(true)]
    public class DrawDrawCardRequest
    {
        public int DrawId { get; set; }
        public int Count { get; set; }
        public int UseDrawTicketId { get; set; }
    }

    [MessagePackObject(true)]
    public class DrawDrawCardResponse
    {
        public int Code { get; set; }
        public List<RewardGoods> RewardGoodsList { get; set; } = new();
        public DrawInfo? ClientDrawInfo { get; set; }
        public List<dynamic>? ExtraRewardList { get; set; }
        public dynamic? DrawAdjustData { get; set; }
    }

    [MessagePackObject(true)]
    public class DrawSetUseDrawIdRequest
    {
        public int DrawId { get; set; }
    }

    [MessagePackObject(true)]
    public class DrawSetUseDrawIdResponse
    {
        public int Code { get; set; }
        public int SwitchDrawIdCount { get; set; }
    }

    [MessagePackObject(true)]
    public class DrawGetDrawInfoListResponse
    {
        public int Code { get; set; }
        public List<DrawInfo> DrawInfoList { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class DrawGetDrawGroupListResponse
    {
        public int Code { get; set; }
        public List<DrawGroupInfo> DrawGroupInfoList { get; set; } = new();
        public List<DrawAdjustActivityInfo> DrawAdjustActivityInfoList { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class DrawGroupInfo
    {
        public long BannerBeginTime { get; set; }
        public long BannerEndTime { get; set; }
        public int BottomTimes { get; set; }
        public int MaxBottomTimes { get; set; }
        public int UseItemId { get; set; }
        public int SwitchDrawIdCount { get; set; }
        public int UseTenDrawOnSaleTimes { get; set; }
        public Dictionary<int, int> UseDrawIdDict { get; set; } = new();
        public int Id { get; set; }
        public int Priority { get; set; }
        public double ResetTime { get; set; }
        public long StartTime { get; set; }
        public long EndTime { get; set; }
        public int Order { get; set; }
        public int SwitchDrawIdActivityId { get; set; }
        public int MaxSwitchDrawIdCount { get; set; }
        public string Banner { get; set; } = string.Empty;
        public string UiPrefab { get; set; } = "UiDraw";
        public string UiBackGround { get; set; } = "Assets/Product/Ui/ComponentPrefab/DrawBackGround/DrawBackGround01.prefab";
        public int Tag { get; set; }
        public List<int> OptionalDrawIdList { get; set; } = new();
        public List<int> TagBlackListDrawIds { get; set; } = new();
        public Dictionary<int, int> TenDrawOnSales { get; set; } = new();
        public List<int> TransformSuitList { get; set; } = new();
        public int ConditionId { get; set; }
        public int Type { get; set; }
        public int ExtraRewardId { get; set; }
        public int ExtraRewardCycleTimes { get; set; }
        public int ShowPredictType { get; set; }
    }

    [MessagePackObject(true)]
    public class DrawInfo
    {
        public int TodayCount { get; set; }
        public int TotalCount { get; set; }
        public int BottomTimes { get; set; }
        public int MaxBottomTimes { get; set; }
        public bool IsTriggerSpecified { get; set; }
        public bool IsShowShop { get; set; }
        public bool IsShowBubble { get; set; }
        public int UseTenDrawOnSaleTimes { get; set; }
        public int Id { get; set; }
        public int GroupId { get; set; }
        public int DrawType { get; set; }
        public int UseItemId { get; set; }
        public int UseItemCount { get; set; }
        public int DailyLimitTimes { get; set; }
        public int ActivityLimitTimes { get; set; }
        public long StartTime { get; set; }
        public long EndTime { get; set; }
        public string Banner { get; set; } = string.Empty;
        public Dictionary<int, string> Resources { get; set; } = new();
        public Dictionary<int, int> ResourceIds { get; set; } = new();
        public List<int> BtnDrawCount { get; set; } = new();
        public int ShowPriority { get; set; }
        public List<int> PurchaseUiType { get; set; } = new();
        public List<int> PurchaseId { get; set; } = new();
        public List<int> ExPurchaseIds { get; set; } = new();
        public int CapacityCheckType { get; set; }
        public int UpGoodsId { get; set; }
        public int GroupSubType { get; set; }
    }

    [MessagePackObject(true)]
    public class DrawGetDrawInfoListRequest
    {
        public int GroupId { get; set; }
    }

    [MessagePackObject(true)]
    public class DrawAdjustActivityInfo
    {
        public int TargetTimes { get; set; }
        public int TargetId { get; set; }
        public int ActivityStatus { get; set; }
        public int ActivityId { get; set; }
        public long StartTime { get; set; }
        public long EndTime { get; set; }
        public int AdjustTimes { get; set; }
        public int DrawGroupId { get; set; }
        public List<int> TargetTemplateIds { get; set; } = new();
        public List<int> SourceTemplateIds { get; set; } = new();
        public List<int> EffectTargetTemplateIds { get; set; } = new();
    }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    #endregion

    internal class DrawModule
    {
        [RequestPacketHandler("DrawGetDrawGroupListRequest")]
        public static void DrawGetDrawGroupListRequestHandler(Session session, Packet.Request packet)
        {
            DrawGetDrawGroupListResponse rsp = new()
            {
                DrawGroupInfoList = DrawManager.GetDrawGroupInfos(session.player.PlayerData.Id),
                DrawAdjustActivityInfoList = DrawManager.GetDrawAdjustActivityInfos()
            };

            session.SendResponse(rsp, packet.Id);
        }

        [RequestPacketHandler("DrawGetDrawInfoListRequest")]
        public static void DrawGetDrawInfoListRequestHandler(Session session, Packet.Request packet)
        {
            DrawGetDrawInfoListRequest request = packet.Deserialize<DrawGetDrawInfoListRequest>();

            DrawGetDrawInfoListResponse rsp = new();
            rsp.DrawInfoList.AddRange(DrawManager.GetDrawInfosByGroup(request.GroupId, session.player.PlayerData.Id));

            session.SendResponse(rsp, packet.Id);
        }

        [RequestPacketHandler("DrawSetUseDrawIdRequest")]
        public static void DrawSetUseDrawIdRequestHandler(Session session, Packet.Request packet)
        {
            DrawSetUseDrawIdRequest request = packet.Deserialize<DrawSetUseDrawIdRequest>();

            session.SendResponse(new DrawSetUseDrawIdResponse
            {
                SwitchDrawIdCount = DrawManager.SetUseDrawId(session.player.PlayerData.Id, request.DrawId)
            }, packet.Id);
        }

        [RequestPacketHandler("DrawDrawCardRequest")]
        public static void DrawDrawCardRequestHandler(Session session, Packet.Request packet)
        {
            DrawDrawCardRequest request = packet.Deserialize<DrawDrawCardRequest>();
            int drawCount = request.Count <= 0 ? 1 : Math.Min(request.Count, 10);

            DrawDrawCardResponse rsp = new();
            for (int i = 0; i < drawCount; i++)
            {
                rsp.RewardGoodsList.AddRange(DrawManager.DrawDraw(session.player.PlayerData.Id, request.DrawId, i));
            }

            List<Reward> rewards = rsp.RewardGoodsList.Select(x => new Reward
            {
                Id = x.TemplateId,
                Count = x.Count,
                Level = Math.Max(1, x.Level),
                Type = (RewardType)x.RewardType,
            }).ToList();

            DrawInfo? drawInfo = DrawManager.ApplyDrawProgress(session.player.PlayerData.Id, request.DrawId, drawCount);
            if (drawInfo is not null)
            {
                rsp.ClientDrawInfo = drawInfo;
                int costItemId = request.UseDrawTicketId > 0 ? request.UseDrawTicketId : drawInfo.UseItemId;
                rewards.Add(new Reward
                {
                    Id = costItemId,
                    Count = drawInfo.UseItemCount * drawCount * -1,
                    Type = RewardType.Item,
                });
            }

            RewardHandler.GiveRewards(rewards, session);
            session.inventory.Save();
            session.character.Save();
            session.SendResponse(rsp, packet.Id);
        }
    }
}
