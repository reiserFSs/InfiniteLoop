using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using MessagePack;
using Newtonsoft.Json.Linq;

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
    }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    #endregion

    internal class PayModule
    {
        private const string PurchaseSnapshotPath = "Configs/client_purchases.json";
        private static readonly Lazy<JObject> RetailPurchaseSnapshot = new(() => JsonSnapshot.LoadObject(PurchaseSnapshotPath));

        [RequestPacketHandler("GetPurchaseListRequest")]
        public static void GetPurchaseListRequestHandler(Session session, Packet.Request packet)
        {
            GetPurchaseListRequest request = MessagePackSerializer.Deserialize<GetPurchaseListRequest>(packet.Content);
            session.SendResponse(BuildPurchaseListResponse(request.UiTypeList, session.player.PurchaseBuyTimes), packet.Id);
        }

        private static GetPurchaseListResponse BuildPurchaseListResponse(IEnumerable<int>? uiTypes, IReadOnlyDictionary<uint, int>? purchaseBuyTimes = null)
        {
            JObject root = RetailPurchaseSnapshot.Value;
            JObject? responses = root["Responses"] as JObject;
            string key = BuildPurchaseSnapshotKey(uiTypes);
            JObject? data = responses?[key] as JObject;

            if (data is null)
            {
                string? defaultKey = root.Value<string>("DefaultKey");
                data = defaultKey is null ? null : responses?[defaultKey] as JObject;
            }

            GetPurchaseListResponse response = ReadPurchaseResponse(data);
            ApplyPurchaseBuyTimes(response.PurchaseInfoList, purchaseBuyTimes);
            return response;
        }

        private static string BuildPurchaseSnapshotKey(IEnumerable<int>? uiTypes)
        {
            return uiTypes is null
                ? string.Empty
                : string.Join(",", uiTypes.OrderBy(static uiType => uiType));
        }

        private static GetPurchaseListResponse ReadPurchaseResponse(JObject? data)
        {
            if (data is null)
                return new GetPurchaseListResponse { Code = 0 };

            return new GetPurchaseListResponse
            {
                Code = JsonSnapshot.ReadInt(data, "Code"),
                PurchaseInfoList = JsonSnapshot.ReadDynamicList(data["PurchaseInfoList"]),
                PurchaseComboInfoList = JsonSnapshot.ReadDynamicList(data["PurchaseComboInfoList"])
            };
        }

        private static void ApplyPurchaseBuyTimes(List<dynamic> purchaseInfoList, IReadOnlyDictionary<uint, int>? purchaseBuyTimes)
        {
            if (purchaseBuyTimes is null || purchaseBuyTimes.Count == 0)
                return;

            foreach (dynamic purchaseInfo in purchaseInfoList)
            {
                if (purchaseInfo is not Dictionary<dynamic, dynamic> data)
                    continue;

                uint purchaseId = ReadDynamicUInt(data, "Id");
                if (purchaseBuyTimes.TryGetValue(purchaseId, out int buyTimes))
                    data["BuyTimes"] = buyTimes;
            }
        }

        [RequestPacketHandler("PurchaseRequest")]
        public static void PurchaseRequestHandler(Session session, Packet.Request packet)
        {
            PurchaseRequest request = MessagePackSerializer.Deserialize<PurchaseRequest>(packet.Content);
            int count = Math.Max(1, request.Count);
            session.log.Debug($"PurchaseRequest Id={request.Id} Count={count} DiscountId={request.DiscountId} UiTypes={string.Join(',', request.UiTypeList)}");

            PurchaseResponse response = new()
            {
                Code = 0,
                NewPurchaseInfoList = BuildPurchaseListResponse(request.UiTypeList, session.player.PurchaseBuyTimes).PurchaseInfoList
            };

            Dictionary<dynamic, dynamic>? purchaseInfo = FindPurchaseInfo(response.NewPurchaseInfoList, request.Id);
            if (purchaseInfo is null)
                TryFindPurchaseInfo(request.Id, request.UiTypeList, out purchaseInfo);

            if (purchaseInfo is not null)
            {
                int previousBuyTimes = session.player.PurchaseBuyTimes.TryGetValue(request.Id, out int savedBuyTimes)
                    ? savedBuyTimes
                    : ReadDynamicInt(purchaseInfo, "BuyTimes");
                int nextBuyTimes = previousBuyTimes + count;
                purchaseInfo["BuyTimes"] = nextBuyTimes;
                session.player.PurchaseBuyTimes[request.Id] = nextBuyTimes;
                purchaseInfo["LastBuyTime"] = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                ReplacePurchaseInfo(response.NewPurchaseInfoList, purchaseInfo);
                response.PurchaseInfo = purchaseInfo;
                response.RewardList = ReadPurchaseRewards(purchaseInfo, count);

                ApplyPurchaseCost(session, purchaseInfo, count);
                ApplyPurchaseRewards(session, response.RewardList);
                session.inventory.Save();
                session.character.Save();
                session.player.Save();
            }
            else
            {
                session.log.Warn($"PurchaseRequest unknown Id={request.Id} raw={Convert.ToHexString(packet.Content)}");
            }

            session.SendResponse(response, packet.Id);
        }

        private static bool TryFindPurchaseInfo(uint purchaseId, IEnumerable<int>? uiTypes, out Dictionary<dynamic, dynamic>? purchaseInfo)
        {
            purchaseInfo = FindPurchaseInfo(BuildPurchaseListResponse(uiTypes).PurchaseInfoList, purchaseId);
            if (purchaseInfo is not null)
                return true;

            JObject? responses = RetailPurchaseSnapshot.Value["Responses"] as JObject;
            if (responses is null)
                return false;

            foreach (JProperty property in responses.Properties())
            {
                purchaseInfo = FindPurchaseInfo(ReadPurchaseResponse(property.Value as JObject).PurchaseInfoList, purchaseId);
                if (purchaseInfo is not null)
                    return true;
            }

            return false;
        }

        private static Dictionary<dynamic, dynamic>? FindPurchaseInfo(IEnumerable<dynamic> purchaseInfoList, uint purchaseId)
        {
            foreach (dynamic purchaseInfo in purchaseInfoList)
            {
                if (purchaseInfo is Dictionary<dynamic, dynamic> data && ReadDynamicUInt(data, "Id") == purchaseId)
                    return data;
            }

            return null;
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
                    Count = ReadDynamicInt(reward, "Count") * countMultiplier,
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

        private static void ApplyPurchaseCost(Session session, Dictionary<dynamic, dynamic> purchaseInfo, int count)
        {
            int consumeId = ReadDynamicInt(purchaseInfo, "ConsumeId");
            int consumeCount = ReadDynamicInt(purchaseInfo, "ConsumeCount");
            if (consumeId <= 0 || consumeCount <= 0)
                return;

            long totalCost = (long)consumeCount * count;
            NotifyItemDataList notifyItemDataList = new();
            notifyItemDataList.ItemDataList.Add(session.inventory.Do(consumeId, -(int)Math.Min(totalCost, int.MaxValue)));
            session.SendPush(notifyItemDataList);
        }

        private static void ApplyPurchaseRewards(Session session, IEnumerable<RewardGoods> rewardGoodsList)
        {
            List<Reward> rewards = [];
            foreach (RewardGoods rewardGoods in rewardGoodsList)
            {
                if (!Enum.IsDefined(typeof(RewardType), rewardGoods.RewardType))
                    continue;

                rewards.Add(new Reward
                {
                    Type = (RewardType)rewardGoods.RewardType,
                    Id = rewardGoods.TemplateId,
                    Count = rewardGoods.Count,
                    Level = rewardGoods.Level
                });
            }

            RewardHandler.GiveRewards(rewards, session);
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
            session.SendResponse(new PayInitiatedResponse() { Code = 1 }, packet.Id);
        }
    }
}
