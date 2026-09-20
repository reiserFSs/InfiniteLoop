using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.miniactivity.musicgame.concertpreheating;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using System.Reflection;

namespace AscNet.Test;

internal static partial class Program
{
    /// <summary>
    /// Isolated entry for the 4.7 ConcertPreHeatingStartRequest handler. Callable by the parent
    /// integration driver; derives valid StageIds and the activity window from the current
    /// ConcertPreHeatingActivity table + ActivitySchedule. Exercises admission with explicit
    /// timestamps and verifies registered-handler responses against the actual authored window.
    /// </summary>
    public static void ValidateVersion47ConcertStartCompatibility()
    {
        // Wire shape: named-key DTOs.
        AssertMailNamedMapKeys(new ConcertPreHeatingStartRequest { StageId = 101 }, ["StageId"], "ConcertPreHeatingStartRequest");
        AssertMailNamedMapKeys(new ConcertPreHeatingStartResponse { Code = 0 }, ["Code"], "ConcertPreHeatingStartResponse");

        // Registered in Version47EventModule.
        MethodInfo handler = GetRegisteredRequestHandlerMethod("ConcertPreHeatingStartRequest");
        AssertEqual("AscNet.GameServer.Handlers.Version47EventModule", handler.DeclaringType?.FullName, "ConcertPreHeatingStartRequest handler module");

        // Authoritative stage set + open window from the current tables/schedule.
        ConcertPreHeatingActivityTable concert = TableReaderV2.Parse<ConcertPreHeatingActivityTable>().Single(row => row.TimeId > 0);
        if (!ActivityScheduleService.TryGet(concert.TimeId, out ActivityScheduleEntry schedule))
            throw new InvalidDataException($"ConcertPreHeating TimeId {concert.TimeId} is not staged in ActivitySchedule.tsv.");
        if (schedule.StartTime <= 0 || schedule.EndTime <= schedule.StartTime)
            throw new InvalidDataException("ConcertPreHeating schedule must have a concrete, nonempty window.");
        DateTimeOffset concertOpen = DateTimeOffset.FromUnixTimeSeconds(schedule.StartTime);
        DateTimeOffset concertClose = DateTimeOffset.FromUnixTimeSeconds(schedule.EndTime);
        if (concert.StageIds.Count < 2)
            throw new InvalidDataException("ConcertPreHeatingActivity has fewer than two authoritative StageIds; cannot test two open stages.");

        int stageA = concert.StageIds[0];
        int stageB = concert.StageIds[1];
        int unknownStage = concert.StageIds.Max() + 1;

        static MethodInfo ModuleMethod(string name, params Type[] signature) =>
            RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.Version47EventModule"),
                name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, signature);
        static ConcertPreHeatingStartResponse Start(int stageId, DateTimeOffset now) =>
            (ConcertPreHeatingStartResponse)ModuleMethod("StartConcertPreHeating", [typeof(int), typeof(DateTimeOffset)])
                .Invoke(null, [stageId, now])!;

        // Two authoritative open stages/current states are accepted.
        AssertEqual(0, Start(stageA, concertOpen).Code, "Concert start authoritative stage A code");
        AssertEqual(0, Start(stageB, concertOpen).Code, "Concert start authoritative stage B code");

        // Nonexistent stage is rejected deterministically while the activity is open.
        AssertEqual(1, Start(unknownStage, concertOpen).Code, "Concert start unknown stage rejected");

        // The authored interval is start-inclusive and end-exclusive.
        AssertEqual(1, Start(stageA, concertOpen.AddSeconds(-1)).Code, "Concert start before-window rejected");
        AssertEqual(0, Start(stageA, concertClose.AddSeconds(-1)).Code, "Concert start last open second accepted");
        AssertEqual(1, Start(stageA, concertClose).Code, "Concert start at end rejected");

        // The registered handler uses UtcNow directly, with no scoped clock override. Verify
        // its valid-stage response against the authored bounds, allowing either adjacent state
        // only if dispatch crosses a boundary. Explicit-time checks above cover both outcomes.
        long uid = 47_501;
        using (LoopbackSessionHarness harness = new(
            CreateDrawCompatibilityCharacter(uid),
            CreateDrawCompatibilityPlayer(uid),
            CreateDrawCompatibilityInventory(uid, []),
            "version47-concert-start-test"))
        {
            byte[] before = harness.Session.player.ConcertPreHeating.ToBson();
            static int ExpectedCode(ActivityScheduleEntry window, DateTimeOffset now) =>
                now.ToUnixTimeSeconds() >= window.StartTime && now.ToUnixTimeSeconds() < window.EndTime ? 0 : 1;
            DateTimeOffset beforeDispatch = DateTimeOffset.UtcNow;
            InvokeRequestHandler(harness, "ConcertPreHeatingStartRequest", 2001, new ConcertPreHeatingStartRequest { StageId = stageA });
            DateTimeOffset afterDispatch = DateTimeOffset.UtcNow;
            ConcertPreHeatingStartResponse valid = ReadResponsePayload<ConcertPreHeatingStartResponse>(
                harness, 2001, nameof(ConcertPreHeatingStartResponse), "Concert start end-to-end authored stage");
            AssertEqual(true, valid.Code == ExpectedCode(schedule, beforeDispatch)
                || valid.Code == ExpectedCode(schedule, afterDispatch),
                "Concert start authored stage follows current schedule");
            AssertEqual(true, before.SequenceEqual(harness.Session.player.ConcertPreHeating.ToBson()),
                "Concert start authored stage does not mutate persisted concert state");
            if (harness.TryReadAvailablePacket("Concert start authored stage unexpected push", out _))
                throw new InvalidDataException("ConcertPreHeatingStart emitted a push.");

            InvokeRequestHandler(harness, "ConcertPreHeatingStartRequest", 2002, new ConcertPreHeatingStartRequest { StageId = unknownStage });
            ConcertPreHeatingStartResponse bad = ReadResponsePayload<ConcertPreHeatingStartResponse>(
                harness, 2002, nameof(ConcertPreHeatingStartResponse), "Concert start end-to-end unknown stage");
            AssertEqual(1, bad.Code, "Concert start end-to-end unknown stage rejected");
            if (harness.TryReadAvailablePacket("Concert start unexpected push", out _))
                throw new InvalidDataException("ConcertPreHeatingStart emitted a push.");

            // Snapshot bytes rather than aliasing the mutable player under test.
            AssertEqual(true, before.SequenceEqual(harness.Session.player.ConcertPreHeating.ToBson()),
                "Concert start does not mutate persisted concert state");
        }

        // BSON stability of the (untouched) persisted concert state.
        ConcertPreHeatingState reloaded = BsonSerializer.Deserialize<ConcertPreHeatingState>(new ConcertPreHeatingState().ToBson());
        AssertEqual(0, reloaded.CompletedStageIds.Count, "Concert state default BSON round-trip");
    }
}
