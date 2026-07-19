using AscNet.Common.Database;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.activity;
using AscNet.Table.V2.share.theatre6pvp;
using System.Reflection;

namespace AscNet.Test;

internal static partial class Program
{
    private static void ValidateTheatre6Visibility()
    {
        Theatre6PvpActivityTable season = TableReaderV2.Parse<Theatre6PvpActivityTable>()
            .Single(row => row.TimeId > 0);
        EventCatalogTable eventEntry = TableReaderV2.Parse<EventCatalogTable>()
            .Single(row => row.TimeId == season.TimeId);
        AssertEqual(404, eventEntry.Id, "Theatre6 TimeId is sourced from Shrouded Requiem Activity 404");
        if (!ActivityScheduleService.TryGet(season.TimeId, out ActivityScheduleEntry schedule))
            throw new InvalidDataException("Theatre6 PVP time has no authoritative schedule entry.");
        if (schedule.StartTime <= 0 || schedule.EndTime <= schedule.StartTime)
            throw new InvalidDataException("Theatre6 PVP schedule has invalid public-notice bounds.");

        Type theatre = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.Theatre6Module");
        Type pvp = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.Theatre6PvpModule");
        MethodInfo reconcile = RequiredMethod(theatre, "ReconcileAvailability", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Player), typeof(DateTimeOffset)]);
        MethodInfo buildNotify = RequiredMethod(theatre, "BuildNotify", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Player), typeof(DateTimeOffset)]);
        MethodInfo gate = RequiredMethod(pvp, "Gate", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Player), typeof(DateTimeOffset)]);

        Player player = CreateDrawCompatibilityPlayer(46_601);
        player.PlayerData.Level = 80;
        DateTimeOffset active = new(2026, 7, 17, 20, 0, 0, TimeSpan.Zero);
        AssertEqual(true, ActivityScheduleService.IsOpen(season.TimeId, active), "Theatre6 TimeId is open at the 4.6 current date");
        AssertEqual(true, (bool)reconcile.Invoke(null, [player, active])!, "Level-80 Theatre6 login authorizes the active base event and PVP season");
        object? notify = buildNotify.Invoke(null, [player, active]);
        if (notify is null)
            throw new InvalidDataException("Active Theatre6 login omitted NotifyTheatre6ActivityData.");
        AssertEqual(0, (int)gate.Invoke(null, [player, active])!, "Active Theatre6 PVP gate accepts the authorized season");

        DateTimeOffset inactive = DateTimeOffset.FromUnixTimeSeconds(schedule.EndTime);
        AssertEqual(false, (bool)reconcile.Invoke(null, [player, inactive])!, "Theatre6 schedule boundary does not re-authorize stale state");
        if (buildNotify.Invoke(null, [player, inactive]) is not null)
            throw new InvalidDataException("Inactive Theatre6 boundary emitted login state.");
        AssertEqual(20427001, (int)gate.Invoke(null, [player, inactive])!, "Inactive Theatre6 PVP gate remains closed");
    }
}
