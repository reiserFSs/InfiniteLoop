using MessagePack;

namespace AscNet.GameServer.Handlers
{
    #region MsgPackScheme
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    [MessagePackObject(true)]
    public class PlayerCostTimeUploadRequest
    {
        public int FunctionId { get; set; }
        public int CostTime { get; set; }
    }

    [MessagePackObject(true)]
    public class PlayerCostTimeUploadResponse
    {
        public int Code { get; set; }
    }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    #endregion

    internal class FunctionModule
    {
        [RequestPacketHandler("PlayerCostTimeUploadRequest")]
        public static void PlayerCostTimeUploadRequestHandler(Session session, Packet.Request packet)
        {
            _ = packet.Deserialize<PlayerCostTimeUploadRequest>();

            session.SendResponse(new PlayerCostTimeUploadResponse
            {
                Code = 0
            }, packet.Id);
        }
    }
}
