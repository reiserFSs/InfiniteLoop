using AscNet.Common.Util;
using MessagePack;
using Newtonsoft.Json.Linq;

namespace AscNet.GameServer.Handlers
{
    #region MsgPackScheme
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    [MessagePackObject(true)]
    public class PassportRecvAllRewardRequest
    {
    }

    [MessagePackObject(true)]
    public class PassportRecvAllRewardResponse
    {
        public int Code { get; set; }
        public List<dynamic> RewardList { get; set; } = new();
        public List<dynamic> PassportInfos { get; set; } = new();
    }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    #endregion

    internal class PassportModule
    {
        private const string PassportSnapshotPath = "Configs/passport_recv_all_reward.json";
        private static readonly Lazy<PassportRecvAllRewardResponse> RetailRecvAllRewardResponse = new(LoadRecvAllRewardResponse);

        [RequestPacketHandler("PassportRecvAllRewardRequest")]
        public static void PassportRecvAllRewardRequestHandler(Session session, Packet.Request packet)
        {
            session.SendResponse(RetailRecvAllRewardResponse.Value, packet.Id);
        }

        private static PassportRecvAllRewardResponse LoadRecvAllRewardResponse()
        {
            JObject snapshot = JsonSnapshot.LoadObject(PassportSnapshotPath);
            return new PassportRecvAllRewardResponse
            {
                Code = JsonSnapshot.ReadInt(snapshot, "Code"),
                RewardList = JsonSnapshot.ReadDynamicList(snapshot["RewardList"]),
                PassportInfos = JsonSnapshot.ReadDynamicList(snapshot["PassportInfos"])
            };
        }
    }
}
