using AscNet.Common.Database;
using AscNet.Common.MsgPack;

namespace AscNet.GameServer.Handlers;

internal static class NameplateModule
{
    [RequestPacketHandler("WearNameplateRequest")]
    public static void HandleWearNameplateRequest(Session session, Packet.Request packet)
    {
        WearNameplateRequest request = packet.Deserialize<WearNameplateRequest>();
        int code = 0;
        if (request.NameplateId != 0)
        {
            if (Character.GetNameplateConfig(request.NameplateId) is null)
                code = 20120001;
            else if (!session.character.Nameplates.Any(value => value.Id == request.NameplateId
                && (value.EndTime == 0 || value.EndTime > DateTimeOffset.UtcNow.ToUnixTimeSeconds())))
                code = 20120002;
        }
        if (code != 0)
        {
            session.SendResponse(new WearNameplateResponse { Code = code }, packet.Id);
            return;
        }

        int previous = session.character.CurrentWearNameplate;
        if (previous != request.NameplateId)
        {
            session.character.CurrentWearNameplate = request.NameplateId;
            try
            {
                session.character.SaveChecked();
            }
            catch
            {
                session.character.CurrentWearNameplate = previous;
                throw;
            }
        }
        session.SendResponse(new WearNameplateResponse(), packet.Id);
    }
}
