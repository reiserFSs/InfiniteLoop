using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.equip.equipguide;
namespace AscNet.GameServer.Handlers
{
    internal class VoteModule
    {
        [RequestPacketHandler("GetVoteGroupListRequest")]
        public static void GetVoteGroupListRequestHandler(Session session, Packet.Request packet)
        {
            GetVoteGroupListResponse response = new();
            int[] voteIds = TableReaderV2.Parse<EquipRecommendTable>()
                .Select(row => row.Id)
                .Where(id => id > 0)
                .Distinct()
                .Order()
                .ToArray();

            if (voteIds.Length > 0)
            {
                response.VoteGroupList.Add(new()
                {
                    Id = voteIds[0],
                    TimeToClose = 0,
                    VoteDic = voteIds.ToDictionary(id => (dynamic)id, _ => (dynamic)0)
                });
            }

            session.SendResponse(response, packet.Id);
        }
    }
}
