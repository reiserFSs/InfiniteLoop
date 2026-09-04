using MessagePack;

namespace AscNet.GameServer.Handlers
{

    #region MsgPackScheme
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    [MessagePackObject(true)]
    public class RaceRegisterRaceLiveRequest
    {
    }

    [MessagePackObject(true)]
    public class RaceRegisterRaceLiveResponse
    {
        public int Code;
        public long ServerTimeMs;
        public List<object> SectorInfos = new();
        public List<object> RankResults = new();
    }

    [MessagePackObject(true)]
    public class RaceUnregisterRaceLiveRequest
    {
    }

    [MessagePackObject(true)]
    public class RaceUnregisterRaceLiveResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class RaceGetRacePlaybackReportHashRequest
    {
        public int RoundId;
    }

    [MessagePackObject(true)]
    public class RaceGetRacePlaybackReportHashResponse
    {
        public int Code;
        public string ReportHash = "";
    }

    [MessagePackObject(true)]
    public class RaceGetRacePlaybackRequest
    {
        public int RoundId;
        public int FrameIdx;
        public int FrameCount;
    }

    [MessagePackObject(true)]
    public class RaceGetRacePlaybackResponse
    {
        public int Code;
        public object? SectorInfo;
    }

    [MessagePackObject(true)]
    public class RaceGetPlaybackRankResultRequest
    {
        public int RoundId;
    }

    [MessagePackObject(true)]
    public class RaceGetRacePlaybackRankResultResponse
    {
        public int Code;
        public List<object> RankResults = new();
    }

    [MessagePackObject(true)]
    public class RaceTestGetRaceReportRequest
    {
        public string ReportName;
    }

    [MessagePackObject(true)]
    public class RaceTestGetRaceReportResponse
    {
        public int Code;
        public object? Report;
    }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    #endregion

    internal class RaceModule
    {
        private const int NotAvailableCode = 1023;

        [RequestPacketHandler("RaceRegisterRaceLiveRequest")]
        public static void RaceRegisterRaceLiveRequestHandler(Session session, Packet.Request packet)
        {
            session.SendResponse(new RaceRegisterRaceLiveResponse()
            {
                ServerTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }, packet.Id);
        }

        [RequestPacketHandler("RaceUnregisterRaceLiveRequest")]
        public static void RaceUnregisterRaceLiveRequestHandler(Session session, Packet.Request packet)
        {
            session.SendResponse(new RaceUnregisterRaceLiveResponse(), packet.Id);
        }

        [RequestPacketHandler("RaceGetRacePlaybackReportHashRequest")]
        public static void RaceGetRacePlaybackReportHashRequestHandler(Session session, Packet.Request packet)
        {
            _ = packet.Deserialize<RaceGetRacePlaybackReportHashRequest>();
            session.SendResponse(new RaceGetRacePlaybackReportHashResponse() { Code = NotAvailableCode }, packet.Id);
        }

        [RequestPacketHandler("RaceGetRacePlaybackRequest")]
        public static void RaceGetRacePlaybackRequestHandler(Session session, Packet.Request packet)
        {
            _ = packet.Deserialize<RaceGetRacePlaybackRequest>();
            session.SendResponse(new RaceGetRacePlaybackResponse() { Code = NotAvailableCode }, packet.Id);
        }

        [RequestPacketHandler("RaceGetPlaybackRankResultRequest")]
        public static void RaceGetPlaybackRankResultRequestHandler(Session session, Packet.Request packet)
        {
            _ = packet.Deserialize<RaceGetPlaybackRankResultRequest>();
            session.SendResponse(new RaceGetRacePlaybackRankResultResponse() { Code = NotAvailableCode }, packet.Id);
        }

        [RequestPacketHandler("RaceTestGetRaceReportRequest")]
        public static void RaceTestGetRaceReportRequestHandler(Session session, Packet.Request packet)
        {
            _ = packet.Deserialize<RaceTestGetRaceReportRequest>();
            session.SendResponse(new RaceTestGetRaceReportResponse() { Code = NotAvailableCode }, packet.Id);
        }
    }
}
