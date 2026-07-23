using MessagePack;
using AscNet.Common.MsgPack;

namespace AscNet.GameServer.Handlers
{

    #region MsgPackScheme
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    [MessagePackObject(true)]
    public class SweepRequest
    {
        public int StageId { get; set; }
        public int Count { get; set; }
    }

    [MessagePackObject(true)]
    public class SweepResponse
    {
        public int Code { get; set; }
        public List<List<RewardGoods>> SweepRewards { get; set; } = [];
    }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    #endregion

    internal class AutoFightModule
    {
        [RequestPacketHandler("SweepRequest")]
        public static void SweepRequestHandler(Session session, Packet.Request packet)
        {
            SweepRequest request = packet.Deserialize<SweepRequest>();
            if (!RepeatChallengeModule.IsStage((uint)request.StageId)
                || !RepeatChallengeModule.TrySweep(session, request.Count, out List<List<RewardGoods>> rewards, out RewardApplicationResult? application))
            {
                session.SendResponse(new SweepResponse { Code = 1 }, packet.Id);
                return;
            }

            application!.SendPushes(session);
            session.SendPush(RepeatChallengeModule.BuildExpChange(session.player));
            session.SendResponse(new SweepResponse { Code = 0, SweepRewards = rewards }, packet.Id);
        }
    }
}
