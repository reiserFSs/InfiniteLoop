using AscNet.Common.MsgPack;
using AscNet.Common.Database;
using AscNet.Table.V2.share.condition;
using AscNet.Table.V2.share.reward;
using AscNet.Table.V2.share.wheelchairmanual;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using MessagePack;

namespace AscNet.GameServer.Handlers
{
    #region MsgPackScheme
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    [MessagePackObject(true)]
    public class PurchaseRequest
    {
        public int Count { get; set; }
        public dynamic? Param { get; set; }
        public uint Id { get; set; }
        public int DiscountId { get; set; }
        public List<int> UiTypeList { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class PurchaseResponse
    {
        public int Code { get; set; }
        public int Id { get; set; }
        public List<RewardGoods> RewardList { get; set; } = new();
        public dynamic? PurchaseInfo { get; set; }
        public List<dynamic> NewPurchaseInfoList { get; set; } = new();
        public List<dynamic> RewardGoodsListByType { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class PayInitiatedRequest
    {
        public string Key;
        public string? TargetParam;
    }

    [MessagePackObject(true)]
    public class PayInitiatedResponse
    {
        public int Code;
        public string GameOrder { get; set; } = "";
        public bool LocalCompleted { get; set; }
        public List<RewardGoods> RewardList { get; set; } = new();
    }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    #endregion

    internal partial class PayModule
    {
        private static readonly Lazy<PurchaseCatalog> Catalog = new(() =>
            PurchaseCatalog.Load(JsonSnapshot.ResolvePath("Configs/client_purchases.json")));

        [RequestPacketHandler("GetPurchaseListRequest")]
        public static void GetPurchaseListRequestHandler(Session session, Packet.Request packet)
        {
            GetPurchaseListRequest request = packet.Deserialize<GetPurchaseListRequest>();
            try
            {
                GrantMailDailyRewards(session);
                session.SendResponse(BuildPurchaseListResponse(request.UiTypeList, session.player), packet.Id);
            }
            catch (Exception error)
            {
                session.log.Error($"Cannot load purchase catalog: {error}");
                session.SendResponse(new GetPurchaseListResponse { Code = 2 }, packet.Id);
            }
        }

        private static GetPurchaseListResponse BuildPurchaseListResponse(IEnumerable<int>? uiTypes, Player? player = null)
        {
            GetPurchaseListResponse response = new()
            {
                PurchaseInfoList = Catalog.Value.List(uiTypes, DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            };
            ApplyPurchaseState(response.PurchaseInfoList, player);
            ApplyPurchaseState(response.PurchaseComboInfoList, player);
            return response;
        }

        private static void ApplyPurchaseState(List<dynamic> purchaseInfoList, Player? player)
        {
            foreach (dynamic purchaseInfo in purchaseInfoList)
            {
                if (purchaseInfo is not Dictionary<dynamic, dynamic> data)
                    continue;
                uint id = ReadDynamicUInt(data, "Id");
                data["BuyTimes"] = player is null ? 0 : CurrentBuyTimes(player, data);
                data["LastBuyTime"] = player?.PurchaseLastBuyTimes.GetValueOrDefault(id) ?? 0L;
                int days = RemainingDays(player, id);
                if (days > 0 && player?.PurchaseBuyTimes.GetValueOrDefault(id) == 0) data["BuyTimes"] = 1;
                data["DailyRewardRemainDay"] = days;
                data["BuyLimitRemainDay"] = days;
                data["IsDailyRewardGet"] = player?.PurchaseDailyPasses.GetValueOrDefault(id)?.LastClaimDay == PurchaseDay();
                if (data.TryGetValue("PurchaseSignInInfo", out dynamic? signRaw) && signRaw is Dictionary<dynamic, dynamic> sign)
                {
                    var pass = player?.PurchaseDailyPasses.GetValueOrDefault(id);
                    sign["PurchaseSignInData"] = new Dictionary<dynamic, dynamic>
                    {
                        ["SignRound"] = 1, ["RewardIndexList"] = pass?.RewardIndexList ?? new List<int>()
                    };
                }
                data["DailyRewardSupplementGetData"] = null!;
            }
        }

        [RequestPacketHandler("PurchaseRequest")]
        public static void PurchaseRequestHandler(Session session, Packet.Request packet)
        {
            PurchaseRequest request = packet.Deserialize<PurchaseRequest>();
            PurchaseResponse response = new() { Id = request.Id <= int.MaxValue ? (int)request.Id : 0 };
            lock (session.player)
            {
                try
                {
                    response.Code = ValidatePurchase(session, request, out Dictionary<dynamic, dynamic>? info, out List<RewardGoods> goods, out int cost);
                    if (response.Code == 0)
                    {
                        PlayerPendingPurchase? pending = session.player.PendingPurchase;
                        if (pending is null)
                        {
                            pending = new PlayerPendingPurchase
                            {
                                Id = request.Id, Count = request.Count,
                                PreviousBuyTimes = session.player.PurchaseBuyTimes.GetValueOrDefault(request.Id),
                                BuyTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                                ConsumeId = ReadDynamicInt(info!, "ConsumeId"), ConsumeCount = cost, Goods = goods
                            };
                            PreparePassUpdates(session.player, info!, request.Count, pending);
                            pending.PeriodBuyTimes = checked(CurrentBuyTimes(session.player, info!) + request.Count);
                            session.player.PendingPurchase = pending;
                            try { session.player.SaveChecked(); }
                            catch { session.player.PendingPurchase = null; throw; }
                        }
                        RewardApplicationResult result = ResumePendingPurchase(session)!;
                        // A later mail save must not turn an already committed purchase into a failed purchase.
                        try { GrantMailDailyRewards(session); }
                        catch (Exception error) { session.log.Error($"Daily purchase mail will retry on refresh: {error}"); }
                        response.NewPurchaseInfoList = BuildPurchaseListResponse(request.UiTypeList, session.player).PurchaseInfoList;
                        ApplyPurchaseState([info!], session.player);
                        ReplacePurchaseInfo(response.NewPurchaseInfoList, info!);
                        response.PurchaseInfo = info;
                        response.RewardList = result.RewardGoods;
                        result.SendPushes(session);
                        if (TableReaderV2.Parse<WheelchairManualActivityTable>()
                            .Any(activity => activity.ShowPackageIds.Contains((int)request.Id)))
                            session.SendPush(WheelchairManualModule.BuildPayload(session, DateTimeOffset.UtcNow));
                    }
                }
                catch (Exception exception)
                {
                    session.log.Error($"Purchase {request.Id} failed: {exception}");
                    response.Code = 2; // ServerInternalError (CodeText)
                }
                if (response.Code != 0) session.log.Warn($"Purchase rejected: package={request.Id}, count={request.Count}, code={response.Code}");
                session.SendResponse(response, packet.Id);
            }
        }

        public static RewardApplicationResult? ResumePendingPurchase(Session session)
        {
            PlayerPendingPurchase? pending = session.player.PendingPurchase;
            if (pending is null) return null;
            string key = $"purchase:{session.player.PlayerData.Id}:{pending.Id}:{pending.PreviousBuyTimes}";
            List<RewardGoodsTable> rows = pending.Goods.Select(goods => new RewardGoodsTable
            {
                Id = goods.Id, TemplateId = goods.TemplateId, Count = goods.Count,
                Params = goods.Level > 0 ? [goods.Level] : []
            }).ToList();
            RewardApplicationResult result = RewardHandler.ApplyRewardsOnceAndPersist(
                [new RewardGrant(key, rows, pending.ConsumeCount > 0
                    ? new Dictionary<int, int> { [pending.ConsumeId] = pending.ConsumeCount } : null)], session);
            bool hadCount = session.player.PurchaseBuyTimes.TryGetValue(pending.Id, out int oldCount);
            bool hadTime = session.player.PurchaseLastBuyTimes.TryGetValue(pending.Id, out long oldTime);
            session.player.PurchaseBuyTimes[pending.Id] = checked(pending.PreviousBuyTimes + pending.Count);
            session.player.PurchaseLastBuyTimes[pending.Id] = pending.BuyTime;
            bool hadPeriod = session.player.PurchasePeriodBuyTimes.TryGetValue(pending.Id, out int oldPeriod);
            session.player.PurchasePeriodBuyTimes[pending.Id] = pending.PeriodBuyTimes > 0
                ? pending.PeriodBuyTimes : session.player.PurchaseBuyTimes[pending.Id];
            Dictionary<uint, PlayerPurchaseDailyPass> oldPasses = session.player.PurchaseDailyPasses;
            session.player.PurchaseDailyPasses = new(oldPasses);
            foreach (var pass in pending.DailyPasses)
                session.player.PurchaseDailyPasses[pass.Key] = pass.Value;
            session.player.PendingPurchase = null;
            try { session.player.SaveChecked(); }
            catch
            {
                if (hadCount) session.player.PurchaseBuyTimes[pending.Id] = oldCount;
                else session.player.PurchaseBuyTimes.Remove(pending.Id);
                if (hadTime) session.player.PurchaseLastBuyTimes[pending.Id] = oldTime;
                else session.player.PurchaseLastBuyTimes.Remove(pending.Id);
                session.player.PendingPurchase = pending;
                session.player.PurchaseDailyPasses = oldPasses;
                if (hadPeriod) session.player.PurchasePeriodBuyTimes[pending.Id] = oldPeriod;
                else session.player.PurchasePeriodBuyTimes.Remove(pending.Id);
                throw;
            }
            return result;
        }

        private static int ValidatePurchase(Session session, PurchaseRequest request,
            out Dictionary<dynamic, dynamic>? info, out List<RewardGoods> goods, out int cost)
        {
            goods = []; cost = 0; info = null;
            if (request.Count <= 0 || request.Id > int.MaxValue
                || !ValidPurchaseParam(request.Param))
                return 20053031;
            if (request.DiscountId > 0) return 20053014;
            if (!TryFindPurchaseInfo(request.Id, request.UiTypeList, out info)) return 20053001;
            PlayerPendingPurchase? pending = session.player.PendingPurchase;
            if (pending is not null)
                return pending.Id == request.Id && pending.Count == request.Count ? 0 : 20053031;
            int previous = CurrentBuyTimes(session.player, info!);
            int limit = ReadDynamicInt(info!, "BuyLimitTimes");
            if (previous < 0 || (long)previous + request.Count > int.MaxValue
                || (limit > 0 && (long)previous + request.Count > limit)) return 20053005;
            if (request.Count > 1 && !ReadDynamicBool(info!, "CanMultiply")) return 20053031;
            int availability = PurchaseCatalog.AvailabilityCode(info!, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            if (availability != 0) return availability;
            if (!AreConditionsSatisfied(session, ReadIds(info!, "Conditions"))) return 20053030;
            int predecessor = ReadDynamicInt(info!, "PrePurchaseId");
            if (predecessor > 0 && session.player.PurchaseBuyTimes.GetValueOrDefault((uint)predecessor) == 0) return 20053008;
            if (ReadIds(info!, "MutexPurchaseIds").Any(id => session.player.PurchaseDailyPasses.ContainsKey((uint)id)
                    ? RemainingDays(session.player, (uint)id) > 0
                    : session.player.PurchaseBuyTimes.GetValueOrDefault((uint)id) > 0))
                return 20053101;
            if (!info!.TryGetValue("ConsumeId", out dynamic? rawConsumeId) || rawConsumeId is null
                || !info.TryGetValue("ConsumeCount", out dynamic? rawConsumeCount) || rawConsumeCount is null
                || HasValue(info, "PayKey") || HasValue(info, "PayKeySuffix")
                || HasValue(info, "SelectDataForClient")
                || ReadDynamicInt(info, "SignInId") > 0)
                return 20053031;
            // The client caps a multi-buy at the next discount tier. Reject forged requests
            // crossing that boundary instead of charging one tier for the entire batch.
            if (info.TryGetValue("NormalDiscounts", out dynamic? discountRaw)
                && discountRaw is Dictionary<dynamic, dynamic> discounts
                && discounts.Keys.Any(tier => Convert.ToInt64((object)tier) > (long)previous + 1
                    && Convert.ToInt64((object)tier) <= (long)previous + request.Count)) return 20053031;
            int consumeId = ReadDynamicInt(info, "ConsumeId"), unitCost = PurchaseCatalog.UnitPrice(info, previous);
            if (consumeId < 0 || unitCost < 0 || (unitCost > 0 && !Inventory.IsValidClientItemId(consumeId))
                || (long)unitCost * request.Count > int.MaxValue) return 20053031;
            cost = checked(unitCost * request.Count);
            if (cost > (session.inventory.Items.FirstOrDefault(item => item.Id == consumeId)?.Count ?? 0)) return 20012004;
            try
            {
                goods = ReadPurchaseRewards(info, request.Count);
                AddBonusRewards(info, session.player.PurchaseBuyTimes.GetValueOrDefault(request.Id) == 0, request.Count, goods);
                int company = ReadDynamicInt(info, "CompanyPackage");
                if (company > 0)
                {
                    if (!TryFindPurchaseInfo((uint)company, null, out var companion)) return 20053001;
                    goods.AddRange(ReadPurchaseRewards(companion!, checked(request.Count * ReadDynamicInt(info, "CompanyPackageCount"))));
                }
                PlayerPendingPurchase preview = new();
                PreparePassUpdates(session.player, info, request.Count, preview);
            }
            catch (OverflowException) { return 20053031; }
            catch (PurchaseLimitException error)
            {
                session.log.Warn($"Purchase limit: package={request.Id}, {error.Message}");
                return 20053005;
            }
            catch (InvalidOperationException) { return 20053031; }
            if ((goods.Count == 0 && !HasValue(info, "PurchaseSignInInfo") && !HasValue(info, "DailyRewardGoodsList"))
                || goods.Any(reward => reward.Count <= 0 || !Enum.IsDefined(typeof(RewardType), reward.RewardType)))
                return 20053031;
            return 0;
        }
        public static bool IsPurchaseUnlocked(Session session, int purchaseId)
        {
            if (purchaseId <= 0 || !TryFindPurchaseInfo((uint)purchaseId, null, out Dictionary<dynamic, dynamic>? info))
                return false;
            int predecessor = ReadDynamicInt(info!, "PrePurchaseId");
            return (predecessor <= 0 || session.player.PurchaseBuyTimes.GetValueOrDefault((uint)predecessor) > 0)
                && AreConditionsSatisfied(session, ReadIds(info!, "Conditions"));
        }

        internal static bool AreConditionsSatisfied(Session session, IEnumerable<int> conditionIds)
        {
            foreach (int id in conditionIds)
            {
                ConditionTable? condition = TableReaderV2.Parse<ConditionTable>().Find(row => row.Id == id);
                if (condition is null || !LifeTreeModule.ConditionSatisfied(session, condition)) return false;
            }
            return true;
        }


        private static IEnumerable<int> ReadIds(Dictionary<dynamic, dynamic> data, string key) =>
            data.TryGetValue(key, out dynamic? value) && value is IEnumerable<dynamic> values
                ? values.Select(value => Convert.ToInt32((object)value)) : [];

        private static bool HasValue(Dictionary<dynamic, dynamic> data, string key) =>
            data.TryGetValue(key, out dynamic? value) && value is not null
            && (value is not IEnumerable<dynamic> values || values.Any());

        private static bool TryFindPurchaseInfo(uint purchaseId, IEnumerable<int>? uiTypes, out Dictionary<dynamic, dynamic>? purchaseInfo)
        {
            // UiTypeList selects the list to refresh; companion/pass lookups can cross tabs.
            purchaseInfo = Catalog.Value.Find(purchaseId);
            return purchaseInfo is not null;
        }

        private static void ReplacePurchaseInfo(List<dynamic> purchaseInfoList, Dictionary<dynamic, dynamic> updatedPurchaseInfo)
        {
            uint purchaseId = ReadDynamicUInt(updatedPurchaseInfo, "Id");
            for (int i = 0; i < purchaseInfoList.Count; i++)
            {
                if (purchaseInfoList[i] is Dictionary<dynamic, dynamic> data && ReadDynamicUInt(data, "Id") == purchaseId)
                {
                    purchaseInfoList[i] = updatedPurchaseInfo;
                    return;
                }
            }

            purchaseInfoList.Add(updatedPurchaseInfo);
        }

        private static List<RewardGoods> ReadPurchaseRewards(Dictionary<dynamic, dynamic> purchaseInfo, int countMultiplier)
        {
            if (!purchaseInfo.TryGetValue("RewardGoodsList", out dynamic? rawRewards) || rawRewards is not IEnumerable<dynamic> rewards)
                return [];

            List<RewardGoods> rewardGoodsList = [];
            foreach (dynamic rawReward in rewards)
            {
                if (rawReward is not Dictionary<dynamic, dynamic> reward)
                    continue;

                rewardGoodsList.Add(new RewardGoods
                {
                    RewardType = ReadDynamicInt(reward, "RewardType"),
                    TemplateId = ReadDynamicInt(reward, "TemplateId"),
                    Count = checked(ReadDynamicInt(reward, "Count") * countMultiplier),
                    Level = ReadDynamicInt(reward, "Level"),
                    Quality = ReadDynamicInt(reward, "Quality"),
                    Grade = ReadDynamicInt(reward, "Grade"),
                    Breakthrough = ReadDynamicInt(reward, "Breakthrough"),
                    ConvertFrom = ReadDynamicInt(reward, "ConvertFrom"),
                    ShowQuality = ReadDynamicInt(reward, "ShowQuality"),
                    Id = ReadDynamicInt(reward, "Id"),
                    IsGift = ReadDynamicBool(reward, "IsGift"),
                    RewardMulti = ReadDynamicInt(reward, "RewardMulti")
                });
            }

            return rewardGoodsList;
        }


        private static int ReadDynamicInt(Dictionary<dynamic, dynamic> data, string name)
        {
            return data.TryGetValue(name, out dynamic? value) && value is not null
                ? Convert.ToInt32(value)
                : 0;
        }

        private static uint ReadDynamicUInt(Dictionary<dynamic, dynamic> data, string name)
        {
            return data.TryGetValue(name, out dynamic? value) && value is not null
                ? Convert.ToUInt32(value)
                : 0;
        }

        private static bool ReadDynamicBool(Dictionary<dynamic, dynamic> data, string name)
        {
            return data.TryGetValue(name, out dynamic? value) && value is not null && Convert.ToBoolean(value);
        }

        [RequestPacketHandler("PayInitiatedRequest")]
        public static void PayInitiatedRequestHandler(Session session, Packet.Request packet)
        {
            CompleteLocalRecharge(session, packet);
        }
    }
}
