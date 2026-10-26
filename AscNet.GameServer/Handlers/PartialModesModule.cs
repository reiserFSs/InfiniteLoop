using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.fuben.mainlineluosaita;
using MessagePack;

namespace AscNet.GameServer.Handlers;

[MessagePackObject(true)] public sealed class MainLineLuosaitaCharacterMoveRequest { public int SectionId { get; set; } public int MoveId { get; set; } }
[MessagePackObject(true)] public sealed class MainLineLuosaitaCharacterMoveResponse { public int Code { get; set; } public MainLineLuosaitaSectionInfo? SectionInfo { get; set; } }
[MessagePackObject(true)] public sealed class ConcertPreHeatingSettleRequest { public int StageId { get; set; } }
[MessagePackObject(true)] public sealed class ConcertPreHeatingSettleResponse { public int Code { get; set; } public int ActivityId { get; set; } public List<ConcertPreHeatingStageFinish> StageFinish { get; set; } = new(); }
[MessagePackObject(true)] public sealed class Maverick3ExitStageRequest { public int StageId { get; set; } }
[MessagePackObject(true)] public sealed class Maverick3ExitStageResponse { public int Code { get; set; } }

/// <summary>Small requests of modes whose full state is not (or only partly) served; see Docs/closed.md.</summary>
internal static class PartialModesModule
{
    private const int MoveCfgNotExist = 20315029, CharacterInfoNotExist = 20315030;
    private const int Maverick3ActivityNotOpen = 20240001;

    [RequestPacketHandler("MainLineLuosaitaCharacterMoveRequest")]
    public static void LuosaitaCharacterMove(Session session, Packet.Request packet)
    {
        MainLineLuosaitaCharacterMoveRequest request = packet.Deserialize<MainLineLuosaitaCharacterMoveRequest>();
        session.SendResponse(CharacterMove(request.MoveId), packet.Id);
    }

    internal static MainLineLuosaitaCharacterMoveResponse CharacterMove(int moveId)
    {
        MainLineLuosaitaCharacterMoveTable? move = TableReaderV2.Parse<MainLineLuosaitaCharacterMoveTable>()
            .FirstOrDefault(row => row.Id == moveId);
        if (move is null)
            return new() { Code = MoveCfgNotExist };

        // The server holds no mutable Luosaita section state: sections are projected from stage progress
        // (MainLineLuosaitaPayloadFactory), and the only authored move (character 3401, Position row on a
        // section-4 block) belongs to a section the server never serves, so no served section carries the
        // character. Fail closed with the client's own code (Docs/closed.md).
        return new() { Code = CharacterInfoNotExist };
    }

    [RequestPacketHandler("Maverick3ExitStageRequest")]
    public static void Maverick3ExitStage(Session session, Packet.Request packet)
    {
        packet.Deserialize<Maverick3ExitStageRequest>();
        // Maverick3 has no server tables, login data or stage saves, so there is never a save to discard.
        session.SendResponse(new Maverick3ExitStageResponse { Code = Maverick3ActivityNotOpen }, packet.Id);
    }
}
