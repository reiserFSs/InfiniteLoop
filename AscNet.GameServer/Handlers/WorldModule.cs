using MessagePack;

namespace AscNet.GameServer.Handlers
{

    #region MsgPackScheme
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    [MessagePackObject(true)]
    public class JoinWorldRequest
    {
        public long WorldNo;
        public int PlayerId;
        public string Token;
        public bool IsRejoin;
    }

    [MessagePackObject(true)]
    public class JoinWorldResponse
    {
        public int Code;
        public int Port;
        public uint Conv;
        public object? WorldData;
        public object? JoinWorldData;
    }

    [MessagePackObject(true)]
    public class CancelJoinWorldRequest
    {
        public long WorldNo;
        public int PlayerId;
        public string Token;
    }

    [MessagePackObject(true)]
    public class CancelJoinWorldResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class LeaveWorldRequest
    {
    }

    [MessagePackObject(true)]
    public class LeaveWorldResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class QuickUseItemRequest
    {
        public int Index;
    }

    [MessagePackObject(true)]
    public class QuickUseItemResponse
    {
        public int Code;
        public int Index;
    }

    [MessagePackObject(true)]
    public class CollectRequest
    {
        public int Id;
        public int Count;
    }

    [MessagePackObject(true)]
    public class CollectResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class EnterFightTimeRequest
    {
        public int EnterFightTime;
    }

    [MessagePackObject(true)]
    public class EnterFightTimeResponse
    {
        public int Code;
        public int FightStartTime;
    }

    [MessagePackObject(true)]
    public class WorldRebootRequest
    {
        public int RebootCount;
    }

    [MessagePackObject(true)]
    public class WorldRebootResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class DlcCheatRequest
    {
        public string Content;
    }

    [MessagePackObject(true)]
    public class DlcCheatResponse
    {
        public int Code;
    }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    #endregion

    internal class WorldModule
    {
        [RequestPacketHandler("JoinWorldRequest")]
        public static void JoinWorldRequestHandler(Session session, Packet.Request packet)
        {
            _ = packet.Deserialize<JoinWorldRequest>();
            session.SendResponse(new JoinWorldResponse() { Code = 1023 }, packet.Id);
        }

        [RequestPacketHandler("CancelJoinWorldRequest")]
        public static void CancelJoinWorldRequestHandler(Session session, Packet.Request packet)
        {
            _ = packet.Deserialize<CancelJoinWorldRequest>();
            session.SendResponse(new CancelJoinWorldResponse(), packet.Id);
        }

        [RequestPacketHandler("LeaveWorldRequest")]
        public static void LeaveWorldRequestHandler(Session session, Packet.Request packet)
        {
            session.SendResponse(new LeaveWorldResponse(), packet.Id);
        }

        [RequestPacketHandler("QuickUseItemRequest")]
        public static void QuickUseItemRequestHandler(Session session, Packet.Request packet)
        {
            QuickUseItemRequest request = packet.Deserialize<QuickUseItemRequest>();
            session.SendResponse(new QuickUseItemResponse() { Index = request.Index }, packet.Id);
        }

        [RequestPacketHandler("CollectRequest")]
        public static void CollectRequestHandler(Session session, Packet.Request packet)
        {
            _ = packet.Deserialize<CollectRequest>();
            session.SendResponse(new CollectResponse(), packet.Id);
        }

        [RequestPacketHandler("EnterFightTimeRequest")]
        public static void EnterFightTimeRequestHandler(Session session, Packet.Request packet)
        {
            EnterFightTimeRequest request = packet.Deserialize<EnterFightTimeRequest>();
            session.SendResponse(new EnterFightTimeResponse() { FightStartTime = request.EnterFightTime }, packet.Id);
        }

        [RequestPacketHandler("WorldRebootRequest")]
        public static void WorldRebootRequestHandler(Session session, Packet.Request packet)
        {
            _ = packet.Deserialize<WorldRebootRequest>();
            session.SendResponse(new WorldRebootResponse(), packet.Id);
        }

        [RequestPacketHandler("DlcCheatRequest")]
        public static void DlcCheatRequestHandler(Session session, Packet.Request packet)
        {
            _ = packet.Deserialize<DlcCheatRequest>();
            session.SendResponse(new DlcCheatResponse(), packet.Id);
        }
    }
}
