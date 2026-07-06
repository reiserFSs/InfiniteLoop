using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using MessagePack;
using Newtonsoft.Json.Linq;

namespace AscNet.GameServer.Handlers
{
    #region MsgPackScheme
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
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
            session.SendResponse(BuildPurchaseListResponse(request.UiTypeList), packet.Id);
        }

        private static GetPurchaseListResponse BuildPurchaseListResponse(IEnumerable<int>? uiTypes)
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

            return ReadPurchaseResponse(data);
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

        [RequestPacketHandler("PayInitiatedRequest")]
        public static void PayInitiatedRequestHandler(Session session, Packet.Request packet)
        {
            session.SendResponse(new PayInitiatedResponse() { Code = 1 }, packet.Id);
        }
    }
}
