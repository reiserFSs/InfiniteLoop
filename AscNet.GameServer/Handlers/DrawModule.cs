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
    public sealed class LottoRequest
    {
        public int Id { get; set; }
        public int LottoId { get; set; }
    }

    [MessagePackObject(true)]
    public sealed class LottoResponse
    {
        public int Code { get; set; }
        public int LottoRewardId { get; set; }
        public int ExtraRewardState { get; set; }
        public List<RewardGoods> RewardList { get; set; } = [];
        public List<RewardGoods> ExtraRewardList { get; set; } = [];
        public List<LottoInfoResponse.LottoRecord> LottoRecords { get; set; } = [];
    }

    [MessagePackObject(true)]
    public sealed class LottoBuyTicketRequest
    {
        public int LottoPrimaryId { get; set; }
        public int LottoId { get; set; }
        public int TicketId { get; set; }
        public int TicketKey { get; set; }
    }

    [MessagePackObject(true)]
    public sealed class LottoBuyTicketResponse
    {
        public int Code { get; set; }
        public int ItemId { get; set; }
        public int ItemCount { get; set; }
    }
    [MessagePackObject(true)]
    public sealed class LottoSelfChoiceSelectRequest
    {
        public int LottoPrimaryId { get; set; }
        public int SelectedLottoId { get; set; }
    }

    [MessagePackObject(true)]
    public sealed class LottoSelfChoiceSelectResponse
    {
        public int Code { get; set; }
    }


    [MessagePackObject(true)]
    public sealed class NotifyDrawCanLiverData
    {
        public DrawCanLiverData DrawCanLiverData { get; set; } = new();
    }

    [MessagePackObject(true)]
    public sealed class NotifyDateALiveDraw
    {
        public Dictionary<int, List<int>> OpenDraws { get; set; } = new();
    }

    [MessagePackObject(true)]
    public sealed class DrawCanLiverData
    {
        // Lua treats only nil as "no activity" (0 is truthy and crashes XUiDrawPanelCanLiverJourneyReward).
        public int? ActivityId { get; set; }
        public int DrawCount { get; set; }
        public List<int> RewardIndex { get; set; } = new();
    }
    [MessagePackObject(true)]
    public sealed class DrawCanLiverRewardResponse
    {
        public int Code { get; set; }
        public List<int> RewardIndexSet { get; set; } = [];
        public List<RewardGoods> RewardGoodsList { get; set; } = [];
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
        public DrawAdjustData? DrawAdjustData { get; set; }
    }

    [MessagePackObject(true)]
    public class DrawAdjustData
    {
        public int ActivityId { get; set; }
        public int TargetTimes { get; set; }
    }

    [MessagePackObject(true)]
    public class DrawAdjustTargetRequest
    {
        public int ActivityId { get; set; }
        public int TargetId { get; set; }
    }

    [MessagePackObject(true)]
    public class DrawAdjustTargetResponse
    {
        public int Code { get; set; }
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

    internal partial class DrawModule
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

            // The Lua consumer dereferences DrawCanLiverData, so inactive windows keep the nested
            // object with a nil ActivityId and zero/empty progress.
            return new()
            {
                DrawCanLiverData = new()
                {
                    ActivityId = activity?.Id,
                    DrawCount = activity is null ? 0 : DrawManager.GetProgressForDrawIds(player, activity.DrawIds),
                    RewardIndex = activity is null ? [] : player.DrawState.ClaimedDrawMilestones.GetValueOrDefault(activity.Id, []).ToList()
                }
            };
        }
        // Lua flags the Date A Live pool open when OpenDraws is non-empty; only currently drawable ids are listed.
        internal static NotifyDateALiveDraw BuildNotifyDateALiveDraw() => new()
        {
            OpenDraws = TableReaderV2.Parse<DateALiveActivityTable>()
                .Select(row => (row.Id, Draws: row.DrawIds.Where(id => DrawManager.GetGroupByDrawId(id) != 0).ToList()))
                .Where(row => row.Draws.Count > 0)
                .ToDictionary(row => row.Id, row => row.Draws)
        };

        [RequestPacketHandler("DrawCanLiverRewardRequest")]
        public static void DrawCanLiverRewardRequestHandler(Session session, Packet.Request packet)
        {
            DrawCanLiverActivityTable? activity = TableReaderV2.Parse<DrawCanLiverActivityTable>()
                .Where(row => ActivityScheduleService.IsOpen(row.TimeId, DateTimeOffset.UtcNow))
                .OrderByDescending(row => row.Id)
                .FirstOrDefault();
            if (activity is null)
            {
                session.SendResponse(new DrawCanLiverRewardResponse { Code = 1 }, packet.Id);
                return;
            }

            int count = DrawManager.GetProgressForDrawIds(session.player, activity.DrawIds);
            List<int> claimed = session.player.DrawState.ClaimedDrawMilestones.GetValueOrDefault(activity.Id, []);
            List<int> available = Enumerable.Range(0, Math.Min(activity.Schedules.Count, activity.RewardIds.Count))
                .Where(index => activity.Schedules[index] <= count && !claimed.Contains(index))
                .ToList();
            List<RewardGrant> grants = available
                .Select(index => new RewardGrant(
                    $"draw-milestone:{activity.Id}:{index}",
                    RewardHandler.GetRewardGoods(activity.RewardIds[index])))
                .ToList();
            if (grants.Count == 0 || grants.Any(grant => grant.Goods.Count == 0))
            {
                session.SendResponse(new DrawCanLiverRewardResponse { Code = 1 }, packet.Id);
                return;
            }

            RewardApplicationResult result = RewardHandler.ApplyRewardsOnceAndPersist(grants, session);
            List<int> updated = [.. claimed, .. available];
            session.player.DrawState.ClaimedDrawMilestones[activity.Id] = updated;
            try { session.player.SaveChecked(); }
            catch
            {
                session.player.DrawState.ClaimedDrawMilestones[activity.Id] = claimed;
                throw;
            }
            result.SendPushes(session);
            session.SendResponse(new DrawCanLiverRewardResponse
            {
                RewardIndexSet = available,
                RewardGoodsList = result.RewardGoods
            }, packet.Id);
        }




        [RequestPacketHandler("DrawGetDrawGroupListRequest")]
        public static void DrawGetDrawGroupListRequestHandler(Session session, Packet.Request packet)
        {
            bool initializedPity = DrawManager.InitializePityState(session.player);
            DrawGetDrawGroupListResponse rsp = new()
            {
                DrawGroupInfoList = DrawManager.GetDrawGroupInfos(session.player),
                DrawAdjustActivityInfoList = DrawManager.GetDrawAdjustActivityInfos(session.player)
            };

            if (initializedPity) session.player.SaveChecked();
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
            bool initializedPity = DrawManager.InitializePityState(session.player, request.GroupId);
            (int bottomTimes, int maxBottomTimes) = DrawManager.GetDrawHistoryStatus(
                session.player,
                request.GroupId,
                request.GroupSubType
            );

            DrawGroupGetHistoryResponse response = new()
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
            };
            if (initializedPity) session.player.SaveChecked();
            session.SendResponse(response, packet.Id);
        }

        [RequestPacketHandler("DrawGetDrawInfoListRequest")]
        public static void DrawGetDrawInfoListRequestHandler(Session session, Packet.Request packet)
        {
            DrawGetDrawInfoListRequest request = packet.Deserialize<DrawGetDrawInfoListRequest>();
            bool initializedPity = DrawManager.InitializePityState(session.player, request.GroupId);

            DrawGetDrawInfoListResponse rsp = new();
            rsp.DrawInfoList.AddRange(DrawManager.GetDrawInfosByGroup(request.GroupId, session.player));

            if (initializedPity) session.player.SaveChecked();
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

        [RequestPacketHandler("DrawAdjustTargetRequest")]
        public static void DrawAdjustTargetRequestHandler(Session session, Packet.Request packet)
        {
            DrawAdjustTargetRequest request = packet.Deserialize<DrawAdjustTargetRequest>();
            session.SendResponse(new DrawAdjustTargetResponse
            {
                Code = DrawManager.SetMemberTargetCalibration(session.player, request.ActivityId, request.TargetId)
            }, packet.Id);
        }

        [RequestPacketHandler("DrawDrawCardRequest")]
        public static void DrawDrawCardRequestHandler(Session session, Packet.Request packet)
        {
            DrawDrawCardRequest request = packet.Deserialize<DrawDrawCardRequest>();
            if (session.player.DrawState?.PendingDraw is PlayerPendingDraw pending)
            {
                bool sameRequest = pending.DrawId == request.DrawId
                    && pending.TicketId == request.UseDrawTicketId
                    && pending.Count == (request.Count <= 0 ? 1 : Math.Min(request.Count, 10));
                CompletePendingDraw(session, packet.Id, pending, sameRequest);
                return;
            }
            int drawCount = request.Count <= 0 ? 1 : Math.Min(request.Count, 10);
            int groupId = DrawManager.GetGroupByDrawId(request.DrawId);
            if (groupId > 0 && DrawManager.InitializePityState(session.player, groupId))
                session.player.SaveChecked();
            PlayerDrawTicket? freeTicket = DrawTicketManager.Available(
                session.player, request.UseDrawTicketId, groupId, drawCount, DrawManager.UtcNow());
            DrawInfo? initialDrawInfo = DrawManager.GetDrawInfoById(request.DrawId, session.player);
            if (initialDrawInfo is null || !(DrawManager.HasRewardConfiguration(request.DrawId) || freeTicket is not null))
            {
                session.log.Warn($"Draw rejected: reason=catalog, drawId={request.DrawId}, count={request.Count}, ticketId={request.UseDrawTicketId}, active={initialDrawInfo is not null}.");
                session.SendResponse(new DrawDrawCardResponse { Code = 1 }, packet.Id);
                return;
            }

            int costItemId = freeTicket is not null ? 0
                : request.UseDrawTicketId > 0 ? request.UseDrawTicketId : initialDrawInfo.UseItemId;
            long requiredCost = freeTicket is not null ? 0 : (long)initialDrawInfo.UseItemCount * drawCount;
            // Source ItemCombine: the paid id funds from its whole family (e.g. 50017+50021).
            List<(int ItemId, int Count)>? costPlan = requiredCost > 0
                ? session.inventory.PlanCombinedCost(costItemId, requiredCost, DrawManager.UtcNow()) : [];
            if ((freeTicket is null && request.UseDrawTicketId != 0 && request.UseDrawTicketId != initialDrawInfo.UseItemId)
                || (freeTicket is null && initialDrawInfo.UseItemId == 0)
                || requiredCost < 0
                || costPlan is null)
            {
                long availableCost = costItemId > 0 ? session.inventory.CombinedCount(costItemId, DrawManager.UtcNow()) : 0;
                session.log.Warn($"Draw rejected: reason=payment, drawId={request.DrawId}, count={request.Count}, effectiveCount={drawCount}, ticketId={request.UseDrawTicketId}, expectedTicketId={initialDrawInfo.UseItemId}, costItemId={costItemId}, unitCost={initialDrawInfo.UseItemCount}, required={requiredCost}, available={availableCost}.");
                session.SendResponse(new DrawDrawCardResponse { Code = 1 }, packet.Id);
                return;
            }
            StartDraw(session, packet.Id, request.DrawId, drawCount, request.UseDrawTicketId, freeTicket, costPlan);
        }

        [RequestPacketHandler("LottoRequest")]
        public static void LottoRequestHandler(Session session, Packet.Request packet)
        {
            LottoRequest request = packet.Deserialize<LottoRequest>();
            session.SendResponse(LottoManager.Draw(session, request.Id, request.LottoId), packet.Id);
        }

        [RequestPacketHandler("LottoBuyTicketRequest")]
        public static void LottoBuyTicketRequestHandler(Session session, Packet.Request packet)
        {
            LottoBuyTicketRequest request = packet.Deserialize<LottoBuyTicketRequest>();
            session.SendResponse(LottoManager.BuyTicket(session, request), packet.Id);
        }
        [RequestPacketHandler("LottoSelfChoiceSelectRequest")]
        public static void LottoSelfChoiceSelectRequestHandler(Session session, Packet.Request packet)
        {
            LottoSelfChoiceSelectRequest request = packet.Deserialize<LottoSelfChoiceSelectRequest>();
            session.SendResponse(new LottoSelfChoiceSelectResponse
            {
                Code = LottoManager.Select(session.player, request.LottoPrimaryId, request.SelectedLottoId)
            }, packet.Id);
        }


        [RequestPacketHandler("LottoInfoRequest")]
        public static void LottoInfoRequestHandler(Session session, Packet.Request packet)
        {
            LottoManager.RecoverPending(session);
            LottoInfoResponse response = new()
            {
                LottoInfos = LottoManager.BuildInfos(session.player)
            };
            if (response.LottoInfos.Count == 0)
                response.Code = DrawManager.CatalogUnavailableCode;
            session.SendResponse(response, packet.Id);
        }

        [RequestPacketHandler("GetGachaInfoRequest")]
        public static void GetGachaInfoRequestHandler(Session session, Packet.Request packet)
        {
            GetGachaInfoRequest request = packet.Deserialize<GetGachaInfoRequest>();
            session.SendResponse(GachaManager.GetInfo(session, request.Id), packet.Id);
        }

        [RequestPacketHandler("GachaRequest")]
        public static void GachaRequestHandler(Session session, Packet.Request packet)
        {
            GachaRequest request = packet.Deserialize<GachaRequest>();
            session.SendResponse(GachaManager.Draw(session, request.Id, request.Times), packet.Id);
        }

        [RequestPacketHandler("ChoiceGachaRequest")]
        public static void ChoiceGachaRequestHandler(Session session, Packet.Request packet)
        {
            ChoiceGachaRequest request = packet.Deserialize<ChoiceGachaRequest>();
            session.SendResponse(new ChoiceGachaResponse { Code = GachaManager.Choose(session.player, request.Id) }, packet.Id);
        }

        // No authored Gacha organize row has a TimeId (all 0), so every organize box is closed.
        [RequestPacketHandler("GetGachaOrganizeInfoRequest")]
        public static void GetGachaOrganizeInfoRequestHandler(Session session, Packet.Request packet)
        {
            packet.Deserialize<GetGachaOrganizeInfoRequest>();
            session.SendResponse(new GetGachaOrganizeInfoResponse { Code = GachaManager.NotOpen }, packet.Id);
        }

        [RequestPacketHandler("GachaItemExchangeRequest")]
        public static void GachaItemExchangeRequestHandler(Session session, Packet.Request packet)
        {
            GachaItemExchangeRequest request = packet.Deserialize<GachaItemExchangeRequest>();
            session.SendResponse(GachaManager.Exchange(session, request), packet.Id);
        }

        internal static RewardGoods ToDrawRewardGoods(Reward reward)
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
