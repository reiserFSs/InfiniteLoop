using AscNet.Common.MsgPack;

namespace AscNet.GameServer.Handlers
{
    internal class VoteModule
    {
        [RequestPacketHandler("GetVoteGroupListRequest")]
        public static void GetVoteGroupListRequestHandler(Session session, Packet.Request packet)
        {
            GetVoteGroupListResponse response = new();
            response.VoteGroupList.Add(new()
            {
                Id = 101,
                TimeToClose = 0,
                VoteDic = new Dictionary<dynamic, dynamic>
                {
                    { 1010, 350 },
                    { 1011, 267 },
                    { 1012, 1235 },
                    { 1013, 3 }
                }
            });

            session.SendResponse(response, packet.Id);
        }
    }
}
