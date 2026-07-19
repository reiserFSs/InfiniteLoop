using AscNet.Common.MsgPack;
using AscNet.Common.Database;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.character.quality;
using AscNet.Table.V2.share.equip;
using AscNet.Table.V2.share.item;
using AscNet.Table.V2.share.draw;
using MessagePack;

namespace AscNet.GameServer.Handlers
{
    #region MsgPackScheme
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    [MessagePackObject(true)]
    public sealed class NotifyDrawCanLiverData
    {
        public DrawCanLiverData DrawCanLiverData { get; set; } = new();
    }

    [MessagePackObject(true)]
    public sealed class DrawCanLiverData
    {
        public int ActivityId { get; set; }
        public int DrawCount { get; set; }
        public List<int> RewardIndex { get; set; } = new();
    }

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
    public class DrawGetDrawGroupListRequest
    {
    }

    [MessagePackObject(true)]
    public class DrawGetDrawGroupListResponse
    {
        public int Code { get; set; }
        public List<DrawGroupInfo> DrawGroupInfoList { get; set; } = new();
        public List<DrawAdjustActivityInfo> DrawAdjustActivityInfoList { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class DrawGetHistoryGroupListRequest
    {
    }

    [MessagePackObject(true)]
    public class DrawGetHistoryGroupListResponse
    {
        public int Code { get; set; }
        public List<DrawHistoryGroup> HistoryGroups { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class DrawHistoryGroup
    {
        public int DrawGroupId { get; set; }
        public int Priority { get; set; }
    }

    [MessagePackObject(true)]
    public class DrawGroupGetHistoryRequest
    {
        public int GroupId { get; set; }
        public int GroupSubType { get; set; }
    }

    [MessagePackObject(true)]
    public class DrawGroupGetHistoryResponse
    {
        public int Code { get; set; }
        public List<DrawHistoryReward> HistoryRewardList { get; set; } = new();
        public int BottomTimes { get; set; }
        public int MaxBottomTimes { get; set; }
    }

    [MessagePackObject(true)]
    public class DrawHistoryReward
    {
        public RewardGoods RewardGoods { get; set; } = new();
        public long DrawTime { get; set; }
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
    public class GetGachaInfoRequest
    {
        public int Id { get; set; }
    }

    [MessagePackObject(true)]
    public class GetGachaInfoResponse
    {
        public int Code { get; set; }
        public List<dynamic>? GridInfoList { get; set; }
        public List<dynamic>? GachaRecordList { get; set; }
        public int CurExchangeItemCount { get; set; }
        public List<dynamic>? GetRewardList { get; set; }
        public int TotalTimes { get; set; }
        public int MissTimes { get; set; }
    }

    [MessagePackObject(true)]
    public class GachaItemExchangeRequest
    {
        public int Id { get; set; }
        public int ItemId { get; set; }
        public int Count { get; set; }
    }

    [MessagePackObject(true)]
    public class GachaItemExchangeResponse
    {
        public int Code { get; set; }
        public List<RewardGoods> RewardGoodsList { get; set; } = new();
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
        private static readonly Lazy<Dictionary<int, int>> CharacterMinQualityById = new(() => TableReaderV2.Parse<CharacterQualityTable>()
            .GroupBy(x => x.CharacterId)
            .ToDictionary(group => group.Key, group => group.Min(x => x.Quality)));
        private static readonly Lazy<Dictionary<int, int>> EquipQualityById = new(() => TableReaderV2.Parse<EquipTable>()
            .ToDictionary(x => x.Id, x => x.Quality));
        private static readonly Lazy<Dictionary<int, int>> ItemQualityById = new(() => TableReaderV2.Parse<ItemTable>()
            .ToDictionary(x => x.Id, x => x.Quality));
        internal static NotifyDrawCanLiverData BuildNotifyDrawCanLiverData(Player player) =>
            BuildNotifyDrawCanLiverData(player, DateTimeOffset.UtcNow);

        internal static NotifyDrawCanLiverData BuildNotifyDrawCanLiverData(Player player, DateTimeOffset now)
        {
            DrawCanLiverActivityTable? activity = TableReaderV2.Parse<DrawCanLiverActivityTable>()
                .Where(row => ActivityScheduleService.IsOpen(row.TimeId, now))
                .OrderByDescending(row => row.Id)
                .FirstOrDefault();

            // The Lua consumer dereferences DrawCanLiverData, so inactive windows retain
            // the nested zero/empty state rather than emitting a null payload.
            return new()
            {
                DrawCanLiverData = new()
                {
                    ActivityId = activity?.Id ?? 0,
                    DrawCount = activity is null ? 0 : DrawManager.GetProgressForDrawIds(player, activity.DrawIds),
                    // Claimed milestone state has no durable model yet.
                    RewardIndex = []
                }
            };
        }



        [RequestPacketHandler("DrawGetDrawGroupListRequest")]
        public static void DrawGetDrawGroupListRequestHandler(Session session, Packet.Request packet)
        {
            DrawGetDrawGroupListResponse rsp = new()
            {
                DrawGroupInfoList = DrawManager.GetDrawGroupInfos(session.player),
                DrawAdjustActivityInfoList = DrawManager.GetDrawAdjustActivityInfos()
            };

            session.SendResponse(rsp, packet.Id);
        }

        [RequestPacketHandler("DrawGetHistoryGroupListRequest")]
        public static void DrawGetHistoryGroupListRequestHandler(Session session, Packet.Request packet)
        {
            DrawGetHistoryGroupListResponse rsp = new()
            {
                HistoryGroups = DrawManager.GetDrawHistoryGroups()
                    .Select(group => new DrawHistoryGroup
                    {
                        DrawGroupId = group.DrawGroupId,
                        Priority = group.Priority
                    })
                    .ToList()
            };

            session.SendResponse(rsp, packet.Id);
        }

        [RequestPacketHandler("DrawGroupGetHistoryRequest")]
        public static void DrawGroupGetHistoryRequestHandler(Session session, Packet.Request packet)
        {
            DrawGroupGetHistoryRequest request = packet.Deserialize<DrawGroupGetHistoryRequest>();
            (int bottomTimes, int maxBottomTimes) = DrawManager.GetDrawHistoryStatus(
                session.player,
                request.GroupId,
                request.GroupSubType
            );

            session.SendResponse(new DrawGroupGetHistoryResponse
            {
                HistoryRewardList = DrawManager.GetDrawHistory(session.player, request.GroupId, request.GroupSubType)
                    .Select(entry => new DrawHistoryReward
                    {
                        RewardGoods = entry.RewardGoods,
                        DrawTime = entry.DrawTime
                    })
                    .ToList(),
                BottomTimes = bottomTimes,
                MaxBottomTimes = maxBottomTimes
            }, packet.Id);
        }

        [RequestPacketHandler("DrawGetDrawInfoListRequest")]
        public static void DrawGetDrawInfoListRequestHandler(Session session, Packet.Request packet)
        {
            DrawGetDrawInfoListRequest request = packet.Deserialize<DrawGetDrawInfoListRequest>();

            DrawGetDrawInfoListResponse rsp = new();
            rsp.DrawInfoList.AddRange(DrawManager.GetDrawInfosByGroup(request.GroupId, session.player));

            session.SendResponse(rsp, packet.Id);
        }

        [RequestPacketHandler("DrawSetUseDrawIdRequest")]
        public static void DrawSetUseDrawIdRequestHandler(Session session, Packet.Request packet)
        {
            DrawSetUseDrawIdRequest request = packet.Deserialize<DrawSetUseDrawIdRequest>();
            int switchCount = DrawManager.SetUseDrawId(session.player, request.DrawId);
            if (switchCount > 0)
                session.player.Save();

            session.SendResponse(new DrawSetUseDrawIdResponse
            {
                Code = switchCount > 0 ? 0 : 1,
                SwitchDrawIdCount = switchCount
            }, packet.Id);
        }

        [RequestPacketHandler("DrawDrawCardRequest")]
        public static void DrawDrawCardRequestHandler(Session session, Packet.Request packet)
        {
            DrawDrawCardRequest request = packet.Deserialize<DrawDrawCardRequest>();
            long playerId = session.player.PlayerData.Id;
            int drawCount = request.Count <= 0 ? 1 : Math.Min(request.Count, 10);
            DrawInfo? initialDrawInfo = DrawManager.GetDrawInfoById(request.DrawId, session.player);
            if (initialDrawInfo is null)
            {
                session.SendResponse(new DrawDrawCardResponse { Code = 1 }, packet.Id);
                return;
            }

            int costItemId = request.UseDrawTicketId > 0 ? request.UseDrawTicketId : initialDrawInfo.UseItemId;
            long requiredCost = (long)initialDrawInfo.UseItemCount * drawCount;
            long availableCost = requiredCost > 0
                ? (session.inventory.Items.FirstOrDefault(item => item.Id == costItemId)?.Count ?? 0)
                : 0;
            if ((request.UseDrawTicketId != 0 && request.UseDrawTicketId != initialDrawInfo.UseItemId)
                || requiredCost < 0
                || requiredCost > int.MaxValue
                || (requiredCost > 0 && (!Inventory.IsValidClientItemId(costItemId) || availableCost < requiredCost)))
            {
                session.SendResponse(new DrawDrawCardResponse { Code = 1 }, packet.Id);
                return;
            }

            DrawDrawCardResponse rsp = new() { Code = 0 };
            for (int i = 0; i < drawCount; i++)
            {
                rsp.RewardGoodsList.AddRange(DrawManager.DrawDraw(session.player, request.DrawId, i));
            }
            if (rsp.RewardGoodsList.Count < drawCount)
            {
                session.SendResponse(new DrawDrawCardResponse { Code = 1 }, packet.Id);
                return;
            }

            List<Reward> drawRewards = rsp.RewardGoodsList.Select(x => new Reward
            {
                Id = x.TemplateId,
                Count = x.Count,
                Level = Math.Max(1, x.Level),
                Type = (RewardType)x.RewardType,
                NotifyAsRecycle = (RewardType)x.RewardType == RewardType.Equip,
                ConvertFrom = x.ConvertFrom,
            }).ToList();
            List<Reward> rewards = RewardHandler.ResolveRewards(drawRewards, session);
            rsp.RewardGoodsList = rewards.Select(ToDrawRewardGoods).ToList();

            DrawInfo? drawInfo = DrawManager.ApplyDrawProgress(session.player, request.DrawId, drawCount);
            if (drawInfo is not null)
            {
                rsp.ClientDrawInfo = drawInfo;
                if (requiredCost > 0)
                {
                    rewards.Add(new Reward
                    {
                        Id = costItemId,
                        Count = -(int)requiredCost,
                        Type = RewardType.Item,
                    });
                }
            }

            for (int rewardIndex = 0; rewardIndex < rsp.RewardGoodsList.Count; rewardIndex++)
            {
                RewardGoods reward = rsp.RewardGoodsList[rewardIndex];
                int primaryDisplayId = reward.Id > 0 ? reward.Id : reward.TemplateId;
                int convertedDisplayId = reward.ConvertFrom > 0 ? reward.ConvertFrom : 0;

                session.log.Info(
                    $"DrawRewardFinal uid={session.player.PlayerData.Id} " +
                    $"drawId={request.DrawId} " +
                    $"groupId={drawInfo?.GroupId.ToString() ?? "null"} " +
                    $"groupSubType={drawInfo?.GroupSubType.ToString() ?? "null"} " +
                    $"drawCount={drawCount} " +
                    $"index={rewardIndex} " +
                    $"rewardType={reward.RewardType} " +
                    $"templateId={reward.TemplateId} " +
                    $"id={reward.Id} " +
                    $"primaryDisplayId={primaryDisplayId} " +
                    $"convertFrom={reward.ConvertFrom} " +
                    $"convertedDisplayId={convertedDisplayId} " +
                    $"count={reward.Count} " +
                    $"level={reward.Level} " +
                    $"quality={reward.Quality} " +
                    $"showQuality={reward.ShowQuality} " +
                    $"grade={reward.Grade} " +
                    $"breakthrough={reward.Breakthrough}");
            }

            DrawManager.RecordDrawHistory(session.player, request.DrawId, rsp.RewardGoodsList);

            RewardHandler.GiveRewards(rewards, session);
            session.inventory.Save();
            session.character.Save();
            session.player.Save();
            session.SendResponse(rsp, packet.Id);
        }

        [RequestPacketHandler("LottoInfoRequest")]
        public static void LottoInfoRequestHandler(Session session, Packet.Request packet)
        {
            LottoInfoResponse response = new();
            if (LottoManager.TryBuildInfo(session.player, out LottoInfoResponse.LottoInfo info))
            {
                response.Code = 0;
                response.LottoInfos.Add(info);
            }
            else
            {
                response.Code = DrawManager.CatalogUnavailableCode;
            }

            session.SendResponse(response, packet.Id);
        }

        [RequestPacketHandler("GetGachaInfoRequest")]
        public static void GetGachaInfoRequestHandler(Session session, Packet.Request packet)
        {
            packet.Deserialize<GetGachaInfoRequest>();
            session.SendResponse(new GetGachaInfoResponse(), packet.Id);
        }

        [RequestPacketHandler("GachaItemExchangeRequest")]
        public static void GachaItemExchangeRequestHandler(Session session, Packet.Request packet)
        {
            packet.Deserialize<GachaItemExchangeRequest>();
            session.SendResponse(new GachaItemExchangeResponse(), packet.Id);
        }

        private static RewardGoods ToDrawRewardGoods(Reward reward)
        {
            int level = Math.Max(1, reward.Level);
            int convertFrom = GetDrawConvertFrom(reward);
            int quality = GetDrawRewardQuality(reward, convertFrom);
            int showQuality = GetDrawShowQuality(reward, quality, convertFrom);
            int grade = reward.Type == RewardType.Character && quality > 0 ? 1 : 0;

            return new RewardGoods
            {
                Id = 0,
                TemplateId = reward.Id,
                Count = reward.Count,
                Level = level,
                Quality = quality,
                Grade = grade,
                RewardType = (int)reward.Type,
                ConvertFrom = convertFrom,
                ShowQuality = showQuality,
                IsGift = false,
                RewardMulti = 0
            };
        }


        private static int GetDrawRewardQuality(Reward reward, int convertFrom)
        {
            if (convertFrom > 0)
                return 0;

            return reward.Type switch
            {
                RewardType.Character => CharacterMinQualityById.Value.GetValueOrDefault(reward.Id),
                RewardType.Equip or RewardType.BaseEquip => EquipQualityById.Value.GetValueOrDefault(reward.Id),
                RewardType.Item or RewardType.DrawTicket => ItemQualityById.Value.GetValueOrDefault(reward.Id),
                _ => 0
            };
        }

        private static int GetDrawShowQuality(Reward reward, int quality, int convertFrom)
        {
            return convertFrom > 0 || reward.Type == RewardType.Character ? 0 : quality;
        }

        private static int GetDrawConvertFrom(Reward reward)
        {
            if (reward.ConvertFrom <= 0)
                return 0;

            return IsSupportedDrawCharacterDisplay(reward.ConvertFrom) ? reward.ConvertFrom : 0;
        }

        private static bool IsSupportedDrawCharacterDisplay(int characterId)
        {
            return CharacterMinQualityById.Value.ContainsKey(characterId);
        }


    }
}
