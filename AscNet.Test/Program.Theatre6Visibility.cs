using AscNet.Common.Database;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.activity;
using AscNet.Table.V2.share.condition;
using AscNet.Table.V2.client.functional;
using AscNet.Table.V2.share.functional;
using AscNet.Table.V2.share.theatre6pvp;
using System.Reflection;

namespace AscNet.Test;

internal static partial class Program
{
    // Promotion visibility is derived from the authored chain rather than a pinned activity id:
    // Theatre6PvpActivity.TimeId -> EventCatalog.SkipId -> SkipFunctional.FunctionalId
    // -> FunctionalOpen.Condition -> Condition type10101 level.
    private static void ValidateTheatre6Visibility()
    {
        (Theatre6PvpActivityTable season, int timeId, int requiredLevel) = RequiemGate();
        if (!ActivityScheduleService.TryGet(timeId, out ActivityScheduleEntry schedule))
            throw new InvalidDataException("Theatre6 PVP time has no authoritative schedule entry.");
        if (schedule.StartTime <= 0 || schedule.EndTime <= schedule.StartTime)
            throw new InvalidDataException("Theatre6 PVP schedule has invalid public-notice bounds.");

        Type theatre = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.Theatre6Module");
        MethodInfo reconcile = RequiredMethod(theatre, "ReconcileAvailability", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Player), typeof(DateTimeOffset)]);
        MethodInfo buildNotify = RequiredMethod(theatre, "BuildNotify", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Player), typeof(DateTimeOffset)]);

        DateTimeOffset active = RequiemOpenMoment(timeId);
        Player player = CreateDrawCompatibilityPlayer(46_601);
        player.PlayerData.Level = requiredLevel;
        AssertEqual(true, ActivityScheduleService.IsOpen(timeId, active), "Theatre6 TimeId is open inside its authored schedule window");
        AssertEqual(true, (bool)reconcile.Invoke(null, [player, active])!, "Theatre6 login authorizes the active base event and PVP season");
        object? notify = buildNotify.Invoke(null, [player, active]);
        if (notify is null)
            throw new InvalidDataException("Active Theatre6 login omitted NotifyTheatre6ActivityData.");
        AssertEqual(Convert.ToInt32(season.Id), player.Theatre6.Pvp.AuthorizedSeasonId,
            "Theatre6 login records the authored PVP season");

        Player below = CreateDrawCompatibilityPlayer(46_602);
        below.PlayerData.Level = requiredLevel - 1;
        AssertEqual(false, (bool)reconcile.Invoke(null, [below, active])!, "Theatre6 login refuses a player below the authored requirement");
        if (buildNotify.Invoke(null, [below, active]) is not null)
            throw new InvalidDataException("Under-levelled Theatre6 login emitted login state.");
        AssertEqual(0, below.Theatre6.Pvp.AuthorizedSeasonId, "Under-levelled Theatre6 login never activates a season");

        DateTimeOffset inactive = DateTimeOffset.FromUnixTimeSeconds(schedule.EndTime);
        AssertEqual(false, ActivityScheduleService.IsOpen(timeId, inactive), "Theatre6 schedule is closed at its authored end");
        // The base PvE promotion is permanent: the closed boundary clears only the PVP season.
        AssertEqual(true, (bool)reconcile.Invoke(null, [player, inactive])!, "Theatre6 expiry clears stale durable authorization");
        AssertEqual(0, player.Theatre6.Pvp.AuthorizedSeasonId, "Expired Theatre6 boundary drops the stale PVP season");
        if (buildNotify.Invoke(null, [player, inactive]) is null)
            throw new InvalidDataException("Permanent Theatre6 base visibility stopped publishing login state after the PVP window closed.");

    }

}
