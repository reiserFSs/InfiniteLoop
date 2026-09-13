using System.Reflection;
using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Game;
using AscNet.GameServer.Handlers;
using AscNet.SDKServer.Models;
using AscNet.Table;
using AscNet.Table.V2.client.functional;
using AscNet.Table.V2.client.purchase;
using AscNet.Table.V2.share.bigworld.common.course;
using AscNet.Table.V2.share.functional;
using AscNet.Table.V2.share.condition;
using AscNet.Table.V2.share.guide;
using AscNet.Table.V2.share.player;
using AscNet.Table.V2.share.fuben.bossinshot;
using AscNet.Table.V2.share.fuben.fashionstory;
using AscNet.Table.V2.share.fuben.transfinite;
using AscNet.Table.V2.share.miniactivity.dyemerge;
using AscNet.Table.V2.share.theatre6;
using AscNet.Table.V2.share.equip;
using AscNet.Table.V2.share.draw;
using AscNet.Table.V2.client.draw;
using AscNet.Table.V2.share.character;
using MessagePack;
using Microsoft.AspNetCore.Http;
using MongoDB.Bson;
using Newtonsoft.Json.Linq;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidateVersion46BootstrapCompatibility()
    {
        ValidateVersion46ConfigurationMetadata();
        ValidateVersion46LoginShape();
        ValidateVersion46TableDrivenDrawCatalog();
        ValidateVersion46PlayerMarks();
        ValidateVersion46ActivitySchedule();
        ValidateVersion46GuideCompletion();
    }
    private static void ValidateVersion46ActivitySchedule()
    {
        DateTimeOffset current = new(2026, 7, 17, 20, 0, 0, TimeSpan.Zero);
        const long battlePanelTimeId = 48113;
        const long trialTimeId = 48501;
        const long knowerTimeId = 48705;
        const int trialCalendarId = 46005;
        const int knowerCalendarId = 46001;
        ActivityScheduleEntry trial = ActivityScheduleService.All.Single(schedule => schedule.Id == trialTimeId);
        ActivityScheduleEntry knower = ActivityScheduleService.All.Single(schedule => schedule.Id == knowerTimeId);
        ActivityScheduleEntry battlePanel = ActivityScheduleService.All.Single(schedule => schedule.Id == battlePanelTimeId);
        ActivityScheduleEntry eventsPage = ActivityScheduleService.All.Single(schedule => schedule.Id == 48126);
        AssertEqual(1784264400L, eventsPage.StartTime, "4.6 Events page patch start");
        AssertEqual(1787115600L, eventsPage.EndTime, "4.6 Events page patch end");
        AssertEqual(
            "client/activitybrief/ActivityBrief+LoginNotice:EndTime+GameNotice:update-note-EndTime+maintenance-duration",
            eventsPage.Source,
            "4.6 Events page authoritative schedule source");
        MethodInfo timeControlBuilder = RequiredMethod(
            RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule"),
            "BuildTimeLimitControlConfigList",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
            [typeof(DateTimeOffset), typeof(bool)]);
        List<TimeLimitCtrlConfigList> timeControls =
            (List<TimeLimitCtrlConfigList>)timeControlBuilder.Invoke(null, [current, false])!;
        List<ActivityScheduleEntry> battleTipSchedules = ActivityScheduleService.All
            .Where(schedule => schedule.Source.Contains("FubenActivityTimeTips", StringComparison.Ordinal))
            .OrderBy(schedule => schedule.StartTime)
            .ThenBy(schedule => schedule.Id)
            .ToList();
        ActivityScheduleEntry activeBattleTip = battleTipSchedules.Single(schedule => ActivityScheduleService.IsOpen(schedule.Id, current));
        TimeLimitCtrlConfigList activeBattleTipControl = timeControls.Single(control => control.Id == activeBattleTip.Id);
        AssertEqual(activeBattleTip.StartTime, activeBattleTipControl.StartTime, "4.6 active Battle Screen tip start");
        AssertEqual(activeBattleTip.EndTime, activeBattleTipControl.EndTime, "4.6 active Battle Screen tip end");
        if (battleTipSchedules.Count == 0
            || battleTipSchedules.Any(schedule => schedule.EndTime <= schedule.StartTime)
            || battleTipSchedules.Zip(battleTipSchedules.Skip(1)).Any(pair => pair.First.EndTime > pair.Second.StartTime))
            throw new InvalidDataException("4.6 Battle Screen tip controls were not derived as non-overlapping component transitions.");
        TimeLimitCtrlConfigList eventsPageControl = timeControls.Single(control => control.Id == eventsPage.Id);
        AssertEqual(eventsPage.StartTime, eventsPageControl.StartTime, "4.6 NotifyLogin Events page start");
        AssertEqual(eventsPage.EndTime, eventsPageControl.EndTime, "4.6 NotifyLogin Events page end");
        foreach ((long timeId, long startTime, long endTime) in new[]
        {
            (48101L, 1784368800L, 1786338000L),
            (48401L, 1784264400L, 1787115600L),
            (48602L, 1784264400L, 1790139600L)
        })
        {
            ActivityScheduleEntry schedule = ActivityScheduleService.All.Single(entry => entry.Id == timeId);
            TimeLimitCtrlConfigList control = timeControls.Single(entry => entry.Id == timeId);
            AssertEqual(startTime, schedule.StartTime, $"4.6 Events group {timeId} schedule start");
            AssertEqual(endTime, schedule.EndTime, $"4.6 Events group {timeId} schedule end");
            AssertEqual(startTime, control.StartTime, $"4.6 NotifyLogin Events group {timeId} start");
            AssertEqual(endTime, control.EndTime, $"4.6 NotifyLogin Events group {timeId} end");
        }
        if (!ActivityScheduleService.IsOpen(battlePanelTimeId, current))
            throw new InvalidDataException("4.6 Battle Screen activity card did not follow the current regional table-derived promo window.");
        if (ActivityScheduleService.IsOpen(battlePanelTimeId, DateTimeOffset.FromUnixTimeSeconds(battlePanel.StartTime - 1))
            || !ActivityScheduleService.IsOpen(battlePanelTimeId, DateTimeOffset.FromUnixTimeSeconds(battlePanel.StartTime))
            || ActivityScheduleService.IsOpen(battlePanelTimeId, DateTimeOffset.FromUnixTimeSeconds(battlePanel.EndTime)))
            throw new InvalidDataException("4.6 Battle Screen activity card did not observe its table-derived boundaries.");
        if (!ActivityScheduleService.IsOpen(trialTimeId, current) || !ActivityScheduleService.IsOpen(knowerTimeId, current))
            throw new InvalidDataException("4.6 current battle events were not opened from their authoritative schedule windows.");
        if (ActivityScheduleService.IsOpen(trialTimeId, DateTimeOffset.FromUnixTimeSeconds(trial.StartTime - 1))
            || !ActivityScheduleService.IsOpen(trialTimeId, DateTimeOffset.FromUnixTimeSeconds(trial.StartTime))
            || ActivityScheduleService.IsOpen(trialTimeId, DateTimeOffset.FromUnixTimeSeconds(trial.EndTime)))
            throw new InvalidDataException("Trial of Simulacrums did not observe its authoritative schedule boundaries.");
        if (ActivityScheduleService.IsOpen(knowerTimeId, DateTimeOffset.FromUnixTimeSeconds(knower.StartTime - 1))
            || !ActivityScheduleService.IsOpen(knowerTimeId, DateTimeOffset.FromUnixTimeSeconds(knower.StartTime))
            || ActivityScheduleService.IsOpen(knowerTimeId, DateTimeOffset.FromUnixTimeSeconds(knower.EndTime)))
            throw new InvalidDataException("The Knower's Dilemma did not observe its authoritative schedule boundaries.");
        DateTimeOffset future = DateTimeOffset.FromUnixTimeSeconds(1784887200);
        foreach (long timeId in new long[] { 48111, battlePanelTimeId, 48136, 48139, 48140, 48820 })
            if (!ActivityScheduleService.IsOpen(timeId, current))
                throw new InvalidDataException($"4.6 current event TimeId {timeId} was not opened from public schedule data.");
        foreach (long timeId in new long[] { 48128, 48137, 48141, 48201 })
            if (ActivityScheduleService.IsOpen(timeId, current))
                throw new InvalidDataException($"4.6 future event TimeId {timeId} opened before its public notice window.");
        foreach (long timeId in new long[] { 48128, 48137, 48141, 48201 })
            if (!ActivityScheduleService.IsOpen(timeId, future))
                throw new InvalidDataException($"4.6 future event TimeId {timeId} did not open at its public notice window.");
        if (ActivityScheduleService.IsOpen(47609, current))
            throw new InvalidDataException("Stale 4.5 Battle Screen TimeId appeared in the 4.6 schedule.");
        if (ActivityScheduleService.IsOpen(45001, current))
            throw new InvalidDataException("Historical 4.5 TimeId appeared in the 4.6 schedule.");
        List<(TransfiniteActivityTable Activity, ActivityScheduleEntry Schedule)> transfiniteRotations =
            TableReaderV2.Parse<TransfiniteActivityTable>()
                .Select(activity => ActivityScheduleService.TryGet(activity.TimeId, out ActivityScheduleEntry schedule)
                    ? (Activity: activity, Schedule: schedule)
                    : ((TransfiniteActivityTable Activity, ActivityScheduleEntry Schedule)?)null)
                .Where(pair => pair is not null && pair.Value.Schedule.Source.StartsWith("version-history:", StringComparison.Ordinal))
                .Select(pair => pair!.Value)
                .OrderBy(pair => pair.Schedule.StartTime)
                .ThenBy(pair => pair.Activity.Id)
                .ToList();
        if (transfiniteRotations.Count < 2 || transfiniteRotations.Any(pair => pair.Activity.CycleSeconds <= 0))
            throw new InvalidDataException("4.6 Transfinite needs authoritative version-history anchors with positive cycles.");
        for (int index = 0; index < transfiniteRotations.Count; index++)
        {
            (TransfiniteActivityTable Activity, ActivityScheduleEntry Schedule) rotation = transfiniteRotations[index];
            TimeLimitCtrlConfigList control = timeControls.Single(entry => entry.Id == rotation.Activity.TimeId);
            AssertEqual(rotation.Schedule.StartTime, control.StartTime, $"4.6 Transfinite {rotation.Activity.TimeId} control start");
            AssertEqual(
                index + 1 < transfiniteRotations.Count ? transfiniteRotations[index + 1].Schedule.StartTime : 0L,
                control.EndTime,
                $"4.6 Transfinite {rotation.Activity.TimeId} control end");
        }
        MethodInfo calendarBuilder = RequiredMethod(
            RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule"),
            "BuildNewActivityCalendarPayload",
            BindingFlags.Static | BindingFlags.NonPublic,
            Type.EmptyTypes);
        Dictionary<string, object?> calendar = (Dictionary<string, object?>)calendarBuilder.Invoke(null, null)!;
        int[] openCalendarIds = (int[])calendar["OpenActivityIds"]!;
        if (openCalendarIds.Length == 0 || openCalendarIds.Any(activityId => activityId / 1000 != 46))
            throw new InvalidDataException("4.6 calendar must derive current 46xxx activities from the schedule.");
        MethodInfo timedCalendarBuilder = RequiredMethod(
            RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule"),
            "BuildNewActivityCalendarPayload",
            BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(DateTimeOffset)]);
        Dictionary<string, object?> currentCalendar = (Dictionary<string, object?>)timedCalendarBuilder.Invoke(null, [current])!;
        int[] currentOpenCalendarIds = (int[])currentCalendar["OpenActivityIds"]!;
        if (!currentOpenCalendarIds.SequenceEqual([46001, 46002, 46003, 46004, 46005, 46009]))
            throw new InvalidDataException("4.6 fixed-clock calendar did not emit exactly the current table-derived event entries.");
        AssertEqual(
            "CurrentGuildBossEndTime,NewActivityCalendarData,OpenActivityIds",
            string.Join(',', currentCalendar.Keys.Order(StringComparer.Ordinal)),
            "4.6 calendar exact top-level MessagePack keys");
        JObject currentCalendarWire = JObject.Parse(MessagePackSerializer.ConvertToJson(MessagePackSerializer.Serialize(currentCalendar)));
        AssertEqual(
            "CurrentGuildBossEndTime,NewActivityCalendarData,OpenActivityIds",
            string.Join(',', currentCalendarWire.Properties().Select(property => property.Name).Order(StringComparer.Ordinal)),
            "4.6 calendar serialized top-level MessagePack keys");
        AssertEqual(
            "TimeLimitActivityInfos,WeekActivityInfos",
            string.Join(',', currentCalendarWire.Value<JObject>("NewActivityCalendarData")!.Properties().Select(property => property.Name).Order(StringComparer.Ordinal)),
            "4.6 calendar serialized nested MessagePack keys");
        Dictionary<string, object?> currentCalendarData = (Dictionary<string, object?>)currentCalendar["NewActivityCalendarData"]!;
        AssertEqual(
            "TimeLimitActivityInfos,WeekActivityInfos",
            string.Join(',', currentCalendarData.Keys.Order(StringComparer.Ordinal)),
            "4.6 calendar exact nested MessagePack keys");
        object[] currentActivities = (object[])currentCalendarData["TimeLimitActivityInfos"]!;
        AssertEqual(0, currentActivities.Length, "4.6 calendar fresh time-limit progress");

        MethodInfo guildBossEndTime = RequiredMethod(
            RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule"),
            "GetCurrentGuildBossEndTime",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
            [typeof(DateTimeOffset)]);
        long calendarBossEnd = (long)guildBossEndTime.Invoke(null, [current])!;
        AssertEqual(
            new DateTimeOffset(2026, 7, 20, 5, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
            calendarBossEnd,
            "4.6 calendar next guild boss Monday boundary");
        AssertEqual(calendarBossEnd, (long)currentCalendar["CurrentGuildBossEndTime"]!, "4.6 calendar guild boss boundary payload");

        MethodInfo wheelchairBuilder = RequiredMethod(
            RequiredAscNetGameServerType("AscNet.GameServer.Handlers.WheelchairManualModule"),
            "BuildPayload",
            BindingFlags.Static | BindingFlags.Public,
            [typeof(Session), typeof(DateTimeOffset)]);
        using LoopbackSessionHarness wheelchairHarness = new(
            CreateDrawCompatibilityCharacter(46_099), CreateDrawCompatibilityPlayer(46_099),
            CreateDrawCompatibilityInventory(46_099, []), "manual-calendar");
        wheelchairHarness.Session.stage = CreateLoginAccountCompatibilityStage(46_099);
        NotifyWheelchairManualActivity wheelchair = (NotifyWheelchairManualActivity)wheelchairBuilder.Invoke(null,
            [wheelchairHarness.Session, current])!;
        AssertEqual(calendarBossEnd, wheelchair.CurrentGuildBossEndTime, "4.6 calendar and wheelchair guild boss boundary");

        int[] beforeTrial = (int[])((Dictionary<string, object?>)timedCalendarBuilder.Invoke(null, [DateTimeOffset.FromUnixTimeSeconds(trial.StartTime - 1)])!)["OpenActivityIds"]!;
        int[] beforeKnower = (int[])((Dictionary<string, object?>)timedCalendarBuilder.Invoke(null, [DateTimeOffset.FromUnixTimeSeconds(knower.StartTime - 1)])!)["OpenActivityIds"]!;
        int[] afterTrial = (int[])((Dictionary<string, object?>)timedCalendarBuilder.Invoke(null, [DateTimeOffset.FromUnixTimeSeconds(trial.EndTime)])!)["OpenActivityIds"]!;
        int[] afterKnower = (int[])((Dictionary<string, object?>)timedCalendarBuilder.Invoke(null, [DateTimeOffset.FromUnixTimeSeconds(knower.EndTime)])!)["OpenActivityIds"]!;
        if (beforeTrial.Contains(trialCalendarId) || beforeKnower.Contains(knowerCalendarId)
            || afterTrial.Contains(trialCalendarId) || afterKnower.Contains(knowerCalendarId))
            throw new InvalidDataException("4.6 calendar did not remove battle events outside their schedule windows.");
        int[] beforeFutureStart = (int[])((Dictionary<string, object?>)timedCalendarBuilder.Invoke(null, [current])!)["OpenActivityIds"]!;
        int[] afterFutureStart = (int[])((Dictionary<string, object?>)timedCalendarBuilder.Invoke(null, [future])!)["OpenActivityIds"]!;
        if (beforeFutureStart.Contains(46008) || !afterFutureStart.Contains(46008))
            throw new InvalidDataException("4.6 calendar did not refresh schedule-backed activities across a future start.");
    }


    private static void ValidateVersion46ConfigurationMetadata()
    {
        Type controller = Type.GetType("AscNet.SDKServer.Controllers.ConfigController, AscNet.SDKServer", throwOnError: true)!;
        MethodInfo getVersion = RequiredMethod(controller, "GetVersionConfig", BindingFlags.Static | BindingFlags.NonPublic, [typeof(string)]);
        ServerVersionConfig live = (ServerVersionConfig)getVersion.Invoke(null, ["4.6.0"])!;
        AssertEqual("4.6.7", live.DocumentVersion, "4.6 live DocumentVersion");
        AssertEqual("4.6.7", live.LaunchModuleVersion, "4.6 live LaunchModuleVersion");
        AssertEqual("c5d4baac85a6e37b8109ea43dc045d31", live.IndexMd5, "4.6 live IndexMd5");
        AssertEqual("f84967640f63e7d1010e459e70e44d5b0610602e", live.IndexSha1, "4.6 live IndexSha1");
        AssertEqual("e7d4d4d44b26dbadd4d752111686216451706717", live.LaunchIndexSha1, "4.6 live LaunchIndexSha1");
        MethodInfo addCurrent = RequiredMethod(
            controller,
            "AddCurrentClientConfig",
            BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(List<RemoteConfig>), typeof(string), typeof(string), typeof(ServerVersionConfig), typeof(string)]);
        List<RemoteConfig> served = [];
        addCurrent.Invoke(null, [served, "com.kurogame.pc.punishing.grayraven.en", "4.6.0", live, "http://127.0.0.1:8080"]);
        AssertEqual(live.DocumentVersion, ConfigValue(served, "DocumentVersion"), "4.6 served DocumentVersion");
        AssertEqual(live.LaunchModuleVersion, ConfigValue(served, "LaunchModuleVersion"), "4.6 served LaunchModuleVersion");
        AssertEqual(live.IndexMd5, ConfigValue(served, "IndexMd5"), "4.6 served IndexMd5");
        AssertEqual(live.IndexSha1, ConfigValue(served, "IndexSha1"), "4.6 served IndexSha1");
        AssertEqual(live.LaunchIndexSha1, ConfigValue(served, "LaunchIndexSha1"), "4.6 served LaunchIndexSha1");

        ServerVersionConfig current = (ServerVersionConfig)getVersion.Invoke(null, ["4.7.0"])!;
        AssertEqual("4.7.11", current.DocumentVersion, "4.7 live DocumentVersion");
        AssertEqual("4.7.11", current.LaunchModuleVersion, "4.7 live LaunchModuleVersion");
        AssertEqual("c5d4baac85a6e37b8109ea43dc045d31", current.IndexMd5, "4.7 live IndexMd5");
        AssertEqual("5f41e51783a5183a619d14d586638dbdb557a996", current.IndexSha1, "4.7 live IndexSha1");
        AssertEqual("2ea1ce3cc9271c9e75df7a16b5465cfb8922abc7", current.LaunchIndexSha1, "4.7 live LaunchIndexSha1");
        ServerVersionConfig fallback = (ServerVersionConfig)getVersion.Invoke(null, ["99.0.0"])!;
        AssertEqual(current.IndexSha1, fallback.IndexSha1, "unknown future version uses latest live metadata");
        ServerVersionConfig previous = (ServerVersionConfig)getVersion.Invoke(null, ["4.5.0"])!;
        if (previous.IndexSha1 == live.IndexSha1)
            throw new InvalidDataException("4.6 configuration metadata did not remain distinct from 4.5.");

        DefaultHttpContext context = new();
        context.Request.RouteValues["version"] = "4.6.0";
        AssertNoticeObject("HandleLoginNoticeRequest", notice =>
        {
            if (string.IsNullOrWhiteSpace(notice.Value<string>("Title")))
                throw new InvalidDataException("4.6 LoginNotice omitted Title.");
        });
        AssertNoticeObject("HandleScrollTextNoticeRequest", notice =>
        {
            if (string.IsNullOrWhiteSpace(notice.Value<string>("Content")))
                throw new InvalidDataException("4.6 ScrollTextNotice omitted Content.");
        });
        AssertNoticeObject("HandleScrollPicNoticeRequest", notice =>
        {
            if (notice.Value<JArray>("Content") is not { Count: > 0 })
                throw new InvalidDataException("4.6 ScrollPicNotice omitted banner entries.");
        });
        AssertNoticeObject("HandleSecondMenuNoticeRequest", notice =>
        {
            if (notice.Value<JArray>("Content") is not { Count: > 0 })
                throw new InvalidDataException("4.6 SecondMenuNotice omitted submenu entries.");
            JToken officialSite = notice.Value<JArray>("Content")!
                .Single(entry => entry.Value<string>("Title") == "Official Site");
            AssertEqual("Logo", officialSite.Value<string>("StyleType"),
                "4.6 Official Site submenu style");
        });

        context.Request.RouteValues["version"] = "4.7.0";
        AssertNoticeObject("HandleSecondMenuNoticeRequest", notice =>
        {
            JToken swapTeam = notice.Value<JArray>("Content")!
                .Single(entry => entry.Value<string>("Title") == "Swap Team");
            AssertEqual("90049", swapTeam.Value<string>("JumpAddr"), "4.7 Swap Team submenu jump");
        });
        context.Request.RouteValues["version"] = "4.6.0";

        MethodInfo popupHandler = RequiredMethod(
            controller,
            "HandlePopUpPicNoticeRequest",
            BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(HttpContext)]);
        AssertEqual("null", (string)popupHandler.Invoke(null, [context])!, "4.6 unavailable PopUpPicNotice");

        void AssertNoticeObject(string handlerName, Action<JObject> assert)
        {
            MethodInfo noticeHandler = RequiredMethod(
                controller,
                handlerName,
                BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(HttpContext)]);
            string json = (string)noticeHandler.Invoke(null, [context])!;
            assert(JObject.Parse(json));
        }
    }

    private static void ValidateVersion46LoginShape()
    {
        const long uid = 46_001;
        AscNet.Common.Database.Player player = CreateDrawCompatibilityPlayer(uid);
        player.PlayerData.Level = 80;
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(uid), player, CreateDrawCompatibilityInventory(uid, []), "v46-login-shape");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(uid);
        MethodInfo build = RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule"), "BuildNotifyLogin", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Session)]);
        NotifyLogin login = (NotifyLogin)build.Invoke(null, [harness.Session])!;
        NotifyLogin decoded = MessagePackSerializer.Deserialize<NotifyLogin>(MessagePackSerializer.Serialize(login));
        if (decoded.TimeLimitCtrlConfigList.Count < 2 || decoded.TimeLimitCtrlConfigList.Select(control => control.Id).Distinct().Count() < 2)
            throw new InvalidDataException("4.6 NotifyLogin must derive at least two distinct time controls from current authoritative tables.");
        HashSet<long> emittedTimeIds = decoded.TimeLimitCtrlConfigList.Select(control => control.Id).ToHashSet();
        if (!emittedTimeIds.Contains(48113) || emittedTimeIds.Contains(47609))
            throw new InvalidDataException("4.6 NotifyLogin did not cut over to the current Battle Screen TimeId.");
        if (!ActivityScheduleService.All.Select(schedule => schedule.Id).All(emittedTimeIds.Contains))
            throw new InvalidDataException("4.6 NotifyLogin omitted an authoritative activity schedule control.");
        foreach (FunctionOpenTimeConfig mapping in decoded.FunctionOpenTimeConfigList)
        {
            if (!emittedTimeIds.Contains(mapping.TimeId))
                throw new InvalidDataException($"4.6 NotifyLogin mapped FunctionId {mapping.FunctionId} to an un-emitted TimeId {mapping.TimeId}.");
        }
        if (decoded.FunctionOpenTimeConfigList.Any(mapping => mapping.TimeId == 20000))
            throw new InvalidDataException("4.6 NotifyLogin must leave Babylonia open by omitting its unsupported time mapping.");
        foreach (long derivedTimeId in new long[] { 48126, 905, 906 })
        {
            if (!emittedTimeIds.Contains(derivedTimeId))
                throw new InvalidDataException($"4.6 NotifyLogin omitted active table-derived TimeId {derivedTimeId}.");
        }

        MethodInfo buildControls = RequiredMethod(
            RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule"),
            "BuildTimeLimitControlConfigList",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
            [typeof(DateTimeOffset), typeof(bool)]);
        List<TimeLimitCtrlConfigList> inactiveControls = (List<TimeLimitCtrlConfigList>)buildControls.Invoke(
            null,
            [DateTimeOffset.FromUnixTimeSeconds(0), false])!;
        HashSet<long> inactiveTimeIds = inactiveControls.Select(control => control.Id).ToHashSet();
        if (!inactiveTimeIds.Contains(48126) || inactiveTimeIds.Contains(905) || inactiveTimeIds.Contains(906))
            throw new InvalidDataException("4.6 authoritative Events page control must remain emitted while inactive derived controls stay absent.");
        foreach (long eventTimeId in new long[] { 48113, 48136, 48139, 48140, 48501, 48705 })
        {
            ActivityScheduleEntry scheduled = ActivityScheduleService.All.Single(schedule => schedule.Id == eventTimeId);
            TimeLimitCtrlConfigList emitted = decoded.TimeLimitCtrlConfigList.Single(control => control.Id == eventTimeId);
            if (emitted.StartTime != scheduled.StartTime || emitted.EndTime != scheduled.EndTime)
                throw new InvalidDataException($"4.6 NotifyLogin emitted an invalid time control for current event TimeId {eventTimeId}.");
        }
        if (ActivityScheduleService.All.Any(schedule => schedule.Source != "policy:latest-TransfiniteActivity-always-open"
            && (schedule.StartTime == 0 || schedule.EndTime == 0)))
            throw new InvalidDataException("4.6 generated schedule contains an event without notice-backed bounds.");
        string json = MessagePackSerializer.ConvertToJson(MessagePackSerializer.Serialize(login));
        JObject loginJson = JObject.Parse(json);
        JArray purchaseInfos = loginJson.Value<JArray>("PurchaseClientInfoLoginData")
            ?? throw new InvalidDataException("4.6 NotifyLogin omitted PurchaseClientInfoLoginData.");
        int[] expectedPurchaseIds = TableReaderV2.Parse<PurchasePackageYKUiConfigTable>()
            .Select(package => package.Id)
            .OrderBy(id => id)
            .ToArray();
        int[] actualPurchaseIds = purchaseInfos
            .Select(info => info.Value<int>("Id"))
            .OrderBy(id => id)
            .ToArray();
        AssertEqual(
            string.Join(",", expectedPurchaseIds),
            string.Join(",", actualPurchaseIds),
            "4.6 NotifyLogin monthly purchase catalog");
        if (purchaseInfos.Count == 0
            || purchaseInfos.Any(info => info.Value<int>("UiType") <= 0
                || info.Value<int>("DailyRewardRemainDay") != 0
                || info.Value<bool>("IsDailyRewardGet")
                || info.Value<int>("BuyTimes") != 0))
            throw new InvalidDataException("4.6 NotifyLogin did not initialize neutral monthly purchase state.");

        player.PurchaseBuyTimes[(uint)expectedPurchaseIds[0]] = 2;
        JObject progressedLoginJson = JObject.Parse(MessagePackSerializer.ConvertToJson(
            MessagePackSerializer.Serialize((NotifyLogin)build.Invoke(null, [harness.Session])!)));
        JArray progressedPurchaseInfos = progressedLoginJson.Value<JArray>("PurchaseClientInfoLoginData")
            ?? throw new InvalidDataException("4.6 progressed NotifyLogin omitted PurchaseClientInfoLoginData.");
        foreach (JToken purchaseInfo in progressedPurchaseInfos)
        {
            int expectedBuyTimes = purchaseInfo.Value<int>("Id") == expectedPurchaseIds[0] ? 2 : 0;
            AssertEqual(expectedBuyTimes, purchaseInfo.Value<int>("BuyTimes"), "4.6 NotifyLogin persisted purchase state");
        }
        if (json.Contains("BaseEquipLoginData", StringComparison.Ordinal))
            throw new InvalidDataException("4.6 NotifyLogin unexpectedly emitted legacy BaseEquipLoginData.");
        if (!json.Contains("TimeLimitCtrlConfigList", StringComparison.Ordinal) || !json.Contains("FunctionOpenTimeConfigList", StringComparison.Ordinal))
            throw new InvalidDataException("4.6 NotifyLogin omitted required schedule keys.");
    }

    private static void ValidateVersion46TableDrivenDrawCatalog()
    {
        const long uid = 46_002;
        AscNet.Common.Database.Player player = CreateDrawCompatibilityPlayer(uid);
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out _, out _, out _);
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(uid), player, CreateDrawCompatibilityInventory(uid, []), "v46-table-draw");
        InvokeRegisteredRequestHandler(nameof(DrawGetDrawGroupListRequest), harness.Session, 46_020, new DrawGetDrawGroupListRequest());
        DrawGetDrawGroupListResponse catalog = ReadResponsePayload<DrawGetDrawGroupListResponse>(harness, 46_020, nameof(DrawGetDrawGroupListResponse), "4.6 table draw catalog");
        int[] expectedGroups = [1, 2, 4, 12, 16, 22, 11];
        AssertEqual(string.Join(',', expectedGroups), string.Join(',', catalog.DrawGroupInfoList.Select(group => group.Id)), "4.6 server-pushed draw groups");
        if (catalog.DrawGroupInfoList.Any(group => group.Id is 13 or 15 or 35 or 36))
            throw new InvalidDataException("4.6 catalog must not advertise groups whose pity law or window is unavailable.");

        DrawGroupInfo group11 = catalog.DrawGroupInfoList.Single(group => group.Id == 11);
        AssertEqual(1509, group11.UseDrawIdDict[0], "4.6 group 11 selected draw");
        AssertEqual("1509", string.Join(',', group11.OptionalDrawIdList), "4.6 group 11 optional draw");
        AssertEqual(1787115600L, group11.StartTime, "4.6 group 11 start");
        AssertEqual(1790204400L, group11.EndTime, "4.6 group 11 end");
        MethodInfo activityDrawListBuilder = RequiredMethod(
            RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule"),
            "BuildActivityDrawListPayload",
            BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(AscNet.Common.Database.Player)]);
        MethodInfo activityDrawCountBuilder = RequiredMethod(
            RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule"),
            "BuildActivityDrawGroupCountPayload",
            BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(AscNet.Common.Database.Player)]);
        NotifyActivityDrawList activityDrawList = (NotifyActivityDrawList)activityDrawListBuilder.Invoke(null, [player])!;
        NotifyActivityDrawGroupCount activityDrawCount = (NotifyActivityDrawGroupCount)activityDrawCountBuilder.Invoke(null, [player])!;
        MethodInfo getDrawGroupInfos = RequiredMethod(
            RequiredAscNetGameServerType("AscNet.GameServer.Game.DrawManager"),
            "GetDrawGroupInfos",
            BindingFlags.Static | BindingFlags.Public,
            [typeof(AscNet.Common.Database.Player)]);
        List<DrawGroupInfo> activeActivityGroups = ((List<DrawGroupInfo>)getDrawGroupInfos.Invoke(null, [player])!)
            .Where(group => group.Type == 2)
            .ToList();
        uint[] expectedActivityDrawIds = activeActivityGroups
            .SelectMany(group => group.OptionalDrawIdList)
            .Select(id => checked((uint)id))
            .ToArray();
        if (!activityDrawList.DrawIdList.SequenceEqual(expectedActivityDrawIds))
            throw new InvalidDataException("4.6 activity draw list did not flatten active Type-2 group optional draws in catalog order.");
        AssertEqual(activeActivityGroups.Count, activityDrawCount.Count, "4.6 activity draw group count derives from active Type-2 groups");
        JObject activityDrawListWire = JObject.Parse(MessagePackSerializer.ConvertToJson(MessagePackSerializer.Serialize(activityDrawList)));
        JObject activityDrawCountWire = JObject.Parse(MessagePackSerializer.ConvertToJson(MessagePackSerializer.Serialize(activityDrawCount)));
        AssertEqual("DrawIdList", string.Join(',', activityDrawListWire.Properties().Select(property => property.Name)), "4.6 activity draw list exact MessagePack key");
        AssertEqual("Count", string.Join(',', activityDrawCountWire.Properties().Select(property => property.Name)), "4.6 activity draw count exact MessagePack key");
        ActivityScheduleEntry canLiverSchedule = ActivityScheduleService.All.Single(schedule => schedule.Id == 48032);
        DrawCanLiverActivityTable canLiverActivity = TableReaderV2.Parse<DrawCanLiverActivityTable>()
            .Single(activity => activity.TimeId == canLiverSchedule.Id);
        MethodInfo canLiverBuilder = RequiredMethod(
            RequiredAscNetGameServerType("AscNet.GameServer.Handlers.DrawModule"),
            "BuildNotifyDrawCanLiverData",
            BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(AscNet.Common.Database.Player), typeof(DateTimeOffset)]);
        NotifyDrawCanLiverData inactiveCanLiver = (NotifyDrawCanLiverData)canLiverBuilder.Invoke(null, [player, DateTimeOffset.FromUnixTimeSeconds(canLiverSchedule.StartTime - 1)])!;
        AssertEqual(0, inactiveCanLiver.DrawCanLiverData.ActivityId, "4.6 inactive DrawCanLiver uses the Lua-safe zero activity");
        AssertEqual(0, inactiveCanLiver.DrawCanLiverData.DrawCount, "4.6 inactive DrawCanLiver has no progress");
        if (inactiveCanLiver.DrawCanLiverData.RewardIndex.Count != 0)
            throw new InvalidDataException("4.6 inactive DrawCanLiver must retain the nested empty reward-index contract.");

        NotifyDrawCanLiverData freshCanLiver = (NotifyDrawCanLiverData)canLiverBuilder.Invoke(null, [player, DateTimeOffset.FromUnixTimeSeconds(canLiverSchedule.StartTime)])!;
        AssertEqual(canLiverActivity.Id, freshCanLiver.DrawCanLiverData.ActivityId, "4.6 DrawCanLiver activity comes from the active table row");
        AssertEqual(0, freshCanLiver.DrawCanLiverData.DrawCount, "4.6 fresh DrawCanLiver progress");

        FieldInfo drawsByIdField = RequiredAscNetGameServerType("AscNet.GameServer.Game.DrawManager")
            .GetField("DrawsById", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidDataException("DrawManager.DrawsById is missing.");
        Dictionary<int, DrawInfo> drawsById = (Dictionary<int, DrawInfo>)drawsByIdField.GetValue(null)!;
        MethodInfo getProgressForDrawIds = RequiredMethod(
            RequiredAscNetGameServerType("AscNet.GameServer.Game.DrawManager"),
            "GetProgressForDrawIds",
            BindingFlags.Static | BindingFlags.Public,
            [typeof(AscNet.Common.Database.Player), typeof(IEnumerable<int>)]);
        // Can-liver progress attribution is historical: the groups are resolved from the
        // template table because the expired draws are no longer active.
        int firstCanLiverGroup = drawsById[canLiverActivity.DrawIds[0]].GroupId;
        int secondCanLiverGroup = drawsById[canLiverActivity.DrawIds[1]].GroupId;
        player.DrawState.PityCountByGroup[firstCanLiverGroup] = 4;
        player.DrawState.PityCountByGroup[secondCanLiverGroup] = 9;
        NotifyDrawCanLiverData progressedCanLiver = (NotifyDrawCanLiverData)canLiverBuilder.Invoke(null, [player, DateTimeOffset.FromUnixTimeSeconds(canLiverSchedule.StartTime)])!;
        AssertEqual(13, progressedCanLiver.DrawCanLiverData.DrawCount, "4.6 DrawCanLiver sums durable draw-group progress");
        AssertEqual(4, (int)getProgressForDrawIds.Invoke(null, [player, new[] { canLiverActivity.DrawIds[0], canLiverActivity.DrawIds[0] }])!, "4.6 DrawCanLiver counts a shared group once");
        JObject canLiverWire = JObject.Parse(MessagePackSerializer.ConvertToJson(MessagePackSerializer.Serialize(progressedCanLiver)));
        AssertEqual("DrawCanLiverData", string.Join(',', canLiverWire.Properties().Select(property => property.Name)), "4.6 DrawCanLiver outer MessagePack key");
        AssertEqual("ActivityId,DrawCount,RewardIndex", string.Join(',', ((JObject)canLiverWire["DrawCanLiverData"]!).Properties().Select(property => property.Name)), "4.6 DrawCanLiver nested MessagePack keys");
        if (((JArray)canLiverWire["DrawCanLiverData"]!["RewardIndex"]!).Count != 0)
            throw new InvalidDataException("4.6 DrawCanLiver reward claims must not be fabricated without durable state.");


        DrawInfo GetInfo(int groupId, int packetId)
        {
            InvokeRegisteredRequestHandler(nameof(DrawGetDrawInfoListRequest), harness.Session, packetId, new DrawGetDrawInfoListRequest { GroupId = groupId });
            return ReadResponsePayload<DrawGetDrawInfoListResponse>(harness, packetId, nameof(DrawGetDrawInfoListResponse), $"4.6 group {groupId} draw info").DrawInfoList.Single();
        }

        DrawInfo draw11 = GetInfo(11, 46_021);
        AssertEqual(50005, draw11.UseItemId, "4.6 draw 1509 use item");
        AssertEqual(250, draw11.UseItemCount, "4.6 draw 1509 use item count");
        AssertEqual(group11.StartTime, draw11.StartTime, "4.6 draw 1509 active start");
        AssertEqual(group11.EndTime, draw11.EndTime, "4.6 draw 1509 active end");
        AssertEqual(60, draw11.MaxBottomTimes, "4.6 draw 1509 pity");

        InvokeRegisteredRequestHandler(nameof(DrawGetDrawInfoListRequest), harness.Session, 46_022, new DrawGetDrawInfoListRequest { GroupId = 15 });
        if (ReadResponsePayload<DrawGetDrawInfoListResponse>(harness, 46_022, nameof(DrawGetDrawInfoListResponse), "4.6 Fate group draw info").DrawInfoList.Count != 0)
            throw new InvalidDataException("4.6 Fate group 15 draw info must stay empty without an authoritative pity law.");

        MethodInfo drawMethod = RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Game.DrawManager"), "DrawDraw", BindingFlags.Static | BindingFlags.Public, [typeof(AscNet.Common.Database.Player), typeof(int), typeof(int)]);
        List<RewardGoods> sampledRewards = Enumerable.Range(0, 16).SelectMany(offset => (List<RewardGoods>)drawMethod.Invoke(null, [player, 1509, offset])!).ToList();
        if (sampledRewards.Count != 16 || sampledRewards.All(reward => reward.TemplateId == draw11.ResourceIds[1]))
            throw new InvalidDataException("4.6 draw 1509: expected the restored table-pool reward algorithm, not a deterministic target reward.");

        AssertEqual(true, AscNet.Common.Database.Character.IsOwnableCharacter(1071005), "4.6 draw 1509 target ownability");
        player.DrawState.PityRounds[11].Misses = 59;
        harness.Session.inventory.Items.Add(new Item { Id = 50003, Count = 250 });
        byte[] beforeWrongTicket = player.ToBson();
        InvokeRegisteredRequestHandler(nameof(DrawDrawCardRequest), harness.Session, 46_029, new DrawDrawCardRequest { DrawId = 1509, Count = 1, UseDrawTicketId = 50003 });
        DrawDrawCardResponse wrongTicketDraw = ReadResponsePayload<DrawDrawCardResponse>(harness, 46_029, nameof(DrawDrawCardResponse), "4.6 group 11 wrong ticket draw");
        AssertEqual(1, wrongTicketDraw.Code, "4.6 group 11 wrong ticket rejection");
        AssertEqual(250L, harness.Session.inventory.Items.Single(item => item.Id == 50003).Count, "4.6 group 11 wrong ticket remains");
        AssertEqual(Convert.ToHexString(beforeWrongTicket), Convert.ToHexString(player.ToBson()), "4.6 group 11 wrong ticket preserves pity/history");

        harness.Session.inventory.Items.Add(new Item { Id = 50005, Count = 250 });
        InvokeRegisteredRequestHandler(nameof(DrawDrawCardRequest), harness.Session, 46_030, new DrawDrawCardRequest { DrawId = 1509, Count = 1 });
        DrawDrawCardResponse forcedGroup11Draw = (DrawDrawCardResponse)ReadResponsePayload(harness, 46_030, nameof(DrawDrawCardResponse), "4.6 group 11 forced pity draw", typeof(DrawDrawCardResponse), maxPacketsToRead: 8);
        AssertEqual(0, forcedGroup11Draw.Code, "4.6 group 11 forced pity draw code");
        RewardGoods forcedGroup11Reward = forcedGroup11Draw.RewardGoodsList.Single();
        if (forcedGroup11Reward.TemplateId != 1071005 && forcedGroup11Reward.ConvertFrom != 1071005)
            throw new InvalidDataException($"4.6 group 11 forced pity target: expected 1071005, got template {forcedGroup11Reward.TemplateId}, conversion {forcedGroup11Reward.ConvertFrom}.");
        AssertEqual(0L, harness.Session.inventory.Items.Single(item => item.Id == 50005).Count, "4.6 group 11 retail ticket deduction");
        if (!harness.Session.character.Characters.Any(character => character.Id == 1071005))
            throw new InvalidDataException("4.6 group 11 forced pity draw did not create character 1071005.");

        // Pity belongs to the group, not to an individual optional banner.
        player.DrawState.PityRounds[12].Misses = 59;
        player.DrawState.PityRounds[12].GuaranteedTarget = true;
        InvokeRegisteredRequestHandler(nameof(DrawGetDrawInfoListRequest), harness.Session, 46_026, new DrawGetDrawInfoListRequest { GroupId = 12 });
        DrawGetDrawInfoListResponse nearPity = ReadResponsePayload<DrawGetDrawInfoListResponse>(harness, 46_026, nameof(DrawGetDrawInfoListResponse), "4.6 group 12 shared pity");
        if (nearPity.DrawInfoList.Count == 0 || nearPity.DrawInfoList.Any(info => info.BottomTimes != 1))
            throw new InvalidDataException("4.6 group 12: every optional draw must expose the shared one-pull pity state.");
        int group12DrawId = nearPity.DrawInfoList.Min(info => info.Id);
        InvokeRegisteredRequestHandler(nameof(DrawSetUseDrawIdRequest), harness.Session, 46_025, new DrawSetUseDrawIdRequest { DrawId = group12DrawId });
        DrawSetUseDrawIdResponse switched = ReadResponsePayload<DrawSetUseDrawIdResponse>(harness, 46_025, nameof(DrawSetUseDrawIdResponse), "4.6 group 12 pity selection");
        AssertEqual(0, switched.Code, "4.6 group 12 pity selection code");
        AscNet.Common.Database.Player pityReload = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<AscNet.Common.Database.Player>(player.ToBson());
        AssertEqual(59, pityReload.DrawState.PityRounds[12].Misses, "4.6 group 12 pity BSON roundtrip");
        AssertEqual(true, pityReload.DrawState.SelectedDrawByGroup[12].Slots.Values.Contains(group12DrawId), "4.6 group 12 selection BSON roundtrip");

        harness.Session.inventory.Items.Single(item => item.Id == 50005).Count += 250;
        InvokeRegisteredRequestHandler(nameof(DrawDrawCardRequest), harness.Session, 46_027, new DrawDrawCardRequest { DrawId = group12DrawId, Count = 1 });
        DrawDrawCardResponse forcedDraw = (DrawDrawCardResponse)ReadResponsePayload(harness, 46_027, nameof(DrawDrawCardResponse), "4.6 group 12 forced pity draw", typeof(DrawDrawCardResponse), maxPacketsToRead: 8);
        AssertEqual(0, forcedDraw.Code, "4.6 group 12 forced pity draw code");
        RewardGoods forcedReward = forcedDraw.RewardGoodsList.Single();
        int forcedTarget = nearPity.DrawInfoList.Single(info => info.Id == group12DrawId).ResourceIds[1];
        if (forcedReward.TemplateId != forcedTarget && forcedReward.ConvertFrom != forcedTarget)
            throw new InvalidDataException($"4.6 group 12 forced pity target: expected {forcedTarget}, got template {forcedReward.TemplateId}, conversion {forcedReward.ConvertFrom}.");
        InvokeRegisteredRequestHandler(nameof(DrawGetDrawInfoListRequest), harness.Session, 46_028, new DrawGetDrawInfoListRequest { GroupId = 12 });
        DrawGetDrawInfoListResponse resetPity = ReadResponsePayload<DrawGetDrawInfoListResponse>(harness, 46_028, nameof(DrawGetDrawInfoListResponse), "4.6 group 12 reset pity");
        if (resetPity.DrawInfoList.Any(info => info.BottomTimes != 60))
            throw new InvalidDataException("4.6 group 12: shared pity must reset every optional draw after the forced pull.");
        pityReload = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<AscNet.Common.Database.Player>(player.ToBson());
        AssertEqual(0, pityReload.DrawState.PityRounds[12].Misses, "4.6 group 12 reset pity BSON roundtrip");

        InvokeRegisteredRequestHandler(nameof(DrawSetUseDrawIdRequest), harness.Session, 46_023, new DrawSetUseDrawIdRequest { DrawId = 1509 });
        DrawSetUseDrawIdResponse selected = ReadResponsePayload<DrawSetUseDrawIdResponse>(harness, 46_023, nameof(DrawSetUseDrawIdResponse), "4.6 group 11 selection");
        AssertEqual(0, selected.Code, "4.6 group 11 selection code");
        AssertEqual(1, selected.SwitchDrawIdCount, "4.6 group 11 selection count");

        byte[] beforeUnknown = player.ToBson();
        InvokeRegisteredRequestHandler(nameof(DrawSetUseDrawIdRequest), harness.Session, 46_024, new DrawSetUseDrawIdRequest { DrawId = int.MaxValue });
        DrawSetUseDrawIdResponse unknown = ReadResponsePayload<DrawSetUseDrawIdResponse>(harness, 46_024, nameof(DrawSetUseDrawIdResponse), "unknown draw selection");
        AssertEqual(1, unknown.Code, "unknown draw rejection");
        AssertEqual(0, unknown.SwitchDrawIdCount, "unknown draw selection unchanged count");
        AssertEqual(Convert.ToHexString(beforeUnknown), Convert.ToHexString(player.ToBson()), "unknown draw rejection state");
    }

    private static void ValidateMemberTargetLocalPolicy()
    {
        ValidateMemberCalibrationLocalPolicy();
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out var playerSaves, out _, out _);
        Type manager = RequiredAscNetGameServerType("AscNet.GameServer.Game.DrawManager");
        MethodInfo infos = RequiredMethod(manager, "GetDrawInfosByGroup", BindingFlags.Static | BindingFlags.Public,
            [typeof(int), typeof(AscNet.Common.Database.Player)]);
        MethodInfo drawPublic = RequiredMethod(manager, "DrawDraw", BindingFlags.Static | BindingFlags.Public,
            [typeof(AscNet.Common.Database.Player), typeof(int), typeof(int)]);
        AscNet.Common.Database.Player player = CreateDrawCompatibilityPlayer(46_090);
        player.DrawState.PityCountByGroup[1] = 59;
        Dictionary<int, CharacterTable> characters = TableReaderV2.Parse<CharacterTable>().ToDictionary(row => row.Id);
        Dictionary<int, int> ranks = TableReaderV2.Parse<AscNet.Table.V2.share.character.quality.CharacterQualityTable>()
            .GroupBy(row => row.CharacterId).ToDictionary(group => group.Key, group => group.Min(row => row.Quality));
        DrawInfo[] targets = ((List<DrawInfo>)infos.Invoke(null, [1, player])!)
            .Where(draw => ranks.GetValueOrDefault(draw.ResourceIds.GetValueOrDefault(1)) == 2)
            .DistinctBy(draw => draw.ResourceIds[1]).Take(2).ToArray();
        AssertEqual(2, targets.Length, "MemberTarget distinct authoritative A targets");
        int Rank(RewardGoods reward) => ranks.GetValueOrDefault(reward.ConvertFrom > 0
            ? reward.ConvertFrom : reward.RewardType == (int)RewardType.Character ? reward.TemplateId : 0);
        RewardGoods legacyBoundary = ((List<RewardGoods>)drawPublic.Invoke(null, [player, targets[0].Id, 0])!).Single();
        AssertEqual(3, Rank(legacyBoundary), "MemberTarget legacy 60th pull awards S, never selected A");

        MethodInfo rollDraw = RequiredMethod(manager, "RollDraw", BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(AscNet.Common.Database.Player), typeof(DrawInfo), typeof(Random)]);
        MethodInfo getRound = RequiredMethod(manager, "GetPityRound", BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(AscNet.Common.Database.Player), typeof(DrawInfo), typeof(Random)]);
        MethodInfo applyProgress = RequiredMethod(manager, "ApplyDrawProgress", BindingFlags.Static | BindingFlags.Public,
            [typeof(AscNet.Common.Database.Player), typeof(int), typeof(int)]);
        MethodInfo select = RequiredMethod(manager, "SetUseDrawId", BindingFlags.Static | BindingFlags.Public,
            [typeof(AscNet.Common.Database.Player), typeof(int)]);
        PlayerDrawPityRound Round() => player.DrawState.PityRounds.TryGetValue(1, out var round) ? round
            : (PlayerDrawPityRound)getRound.Invoke(null, [player, targets[0], new DrawFixedRandom(0)])!;
        void Progress(int a, int s) => player.DrawState.PityRounds[1] =
            new PlayerDrawPityRound { LowerMisses = a, Misses = s, Limit = 60, HasObtainedRare = true };
        int Since(string name) => name == "SinceAOrS" ? Round().LowerMisses : Round().Misses;
        // Live-path pulls: the shared engine consumes the rare roll first, then the
        // member reward composition consumes category, rank, target and item rolls.
        RewardGoods Pull(DrawInfo draw, double rare, double category, double rank = 0, double target = 0, double item = 0) =>
            (RewardGoods)rollDraw.Invoke(null, [player, draw, new DrawSequenceRandom([rare, category, rank, target, item])])!;
        DrawProbShowTable profile = TableReaderV2.Parse<DrawProbShowTable>().Single(row => row.DrawId == targets[0].Id);
        double[] weights = profile.ProbShow.Where(value => !string.IsNullOrWhiteSpace(value))
            .Skip(1).Select(value => double.Parse(value.TrimEnd('%'), System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        double sChance = weights[0] / 100;
        double nonS = weights.Skip(1).Sum();
        // The rare check is a separate engine roll; this maps category index to a
        // roll inside the conditional (non-S) share.
        double Category(int index) => (weights.Skip(1).Take(index - 1).Sum() + weights[index] / 2) / nonS;
        foreach (DrawInfo draw in targets)
        {
            DrawPreviewTable preview = TableReaderV2.Parse<DrawPreviewTable>().Single(row => row.Id == draw.Id);
            Dictionary<int, int> previewIds = TableReaderV2.Parse<DrawPreviewGoodsTable>().ToDictionary(row => row.Id, row => row.TemplateId);
            int[] pool = preview.GoodsId.Concat(preview.UpGoodsId).Select(id => previewIds.GetValueOrDefault(id)).Distinct()
                .Where(id => characters.ContainsKey(id) && AscNet.Common.Database.Character.IsOwnableCharacter((uint)id)).ToArray();
            int aCount = pool.Count(id => ranks.GetValueOrDefault(id) == 2);
            int bCount = pool.Count(id => ranks.GetValueOrDefault(id) == 1);
            double rankBoundary = (double)aCount / (aCount + bCount);
            string targetPercent = TableReaderV2.Parse<DrawAimProbabilityTable>().Single(row => row.Id == draw.Id)
                .UpProbabilityPercent ?? throw new InvalidDataException($"MemberTarget {draw.Id} has no authoritative target percentage.");
            double targetChance = double.Parse(targetPercent.TrimEnd('%'), System.Globalization.CultureInfo.InvariantCulture) / 100;
            Progress(5, 17);
            RewardGoods fullA = Pull(draw, .999, Category(1), Math.BitDecrement(rankBoundary), Math.BitDecrement(targetChance));
            AssertEqual((int)RewardType.Character, fullA.RewardType, $"MemberTarget {draw.Id} raw full A type");
            AssertEqual(draw.ResourceIds[1], fullA.TemplateId, $"MemberTarget {draw.Id} conditional selected A");
            AssertEqual(0, fullA.ConvertFrom, "raw A is not preconverted");
            AssertEqual(0, Since("SinceAOrS"), "ordinary A resets ten guarantee");
            AssertEqual(18, Since("SinceS"), "ordinary A retains S progress");
            Progress(0, 0);
            AssertEqual(1, Rank(Pull(draw, .999, Category(1), rankBoundary)), "A/B count-derived rank boundary selects B");
            if (targetChance < 1)
            {
                Progress(0, 0);
                RewardGoods otherA = Pull(draw, .999, Category(1), 0, targetChance);
                AssertEqual(2, Rank(otherA), "conditional target miss remains A");
                AssertEqual(false, otherA.TemplateId == draw.ResourceIds[1], "conditional target miss excludes selected A");
            }
            Progress(9, 20);
            AssertEqual(2, Rank(Pull(draw, .999, Category(6))), "tenth non-S pull forces A over materials");
            AssertEqual(0, Since("SinceAOrS"), "guaranteed A resets ten");
            AssertEqual(21, Since("SinceS"), "guaranteed A does not reset sixty");
            Progress(9, 20);
            AssertEqual(3, Rank(Pull(draw, Math.BitDecrement(sChance), 0)), "base S precedes tenth A guarantee");
            AssertEqual(0, Since("SinceS"), "early S resets sixty");
            AssertEqual(0, Since("SinceAOrS"), "early S resets ten");
            Progress(9, 59);
            AssertEqual(3, Rank(Pull(draw, .999, Category(6))), "sixtieth S precedes tenth A guarantee");
            Progress(0, 0);
            AssertEqual(2, Rank(Pull(draw, sChance, Category(1))), "exact base S boundary leaves S category");
            for (int category = 2; category < weights.Length; category++)
            {
                Progress(0, 0);
                RewardGoods material = Pull(draw, .999, Category(category));
                AssertEqual(category == 3 ? (int)RewardType.Equip : (int)RewardType.Item, material.RewardType,
                    $"MemberTarget category {category} reward family");
                if (category == 2)
                    AssertEqual(true, pool.Any(id => characters[id].ItemId == material.TemplateId), "shard category yields visible character shard");
                if (category == 3)
                    AssertEqual(4, TableReaderV2.Parse<EquipTable>().Single(row => row.Id == material.TemplateId).Quality, "memory category quality");
            }
            using LoopbackSessionHarness grantHarness = new(CreateDrawCompatibilityCharacter(46_091), player,
                CreateDrawCompatibilityInventory(46_091, []), $"member-target-grant-{draw.Id}");
            Type rewardHandler = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.RewardHandler");
            MethodInfo resolve = RequiredMethod(rewardHandler, "ResolveRewards", BindingFlags.Static | BindingFlags.Public,
                [typeof(IEnumerable<Reward>), typeof(Session)]);
            MethodInfo apply = RequiredMethod(rewardHandler, "ApplyRewards", BindingFlags.Static | BindingFlags.Public,
                [typeof(IEnumerable<Reward>), typeof(Session)]);
            Reward[] raw = [new() { Id = fullA.TemplateId, Type = RewardType.Character, Count = 1, Level = 1 }];
            List<Reward> first = (List<Reward>)resolve.Invoke(null, [raw, grantHarness.Session])!;
            apply.Invoke(null, [first, grantHarness.Session]);
            AssertEqual(true, grantHarness.Session.character.Characters.Any(row => row.Id == fullA.TemplateId), "new A grants owned character");
            List<Reward> duplicate = (List<Reward>)resolve.Invoke(null, [raw, grantHarness.Session])!;
            Reward conversion = duplicate.Single(reward => reward.ConvertFrom == fullA.TemplateId);
            AssertEqual(characters[fullA.TemplateId].ItemId, conversion.Id, "duplicate A converts to its own shard");
            apply.Invoke(null, [duplicate, grantHarness.Session]);
            AssertEqual((long)conversion.Count, grantHarness.Session.inventory.Items.Single(item => item.Id == conversion.Id).Count, "duplicate A credits shard inventory");
            AssertEqual(1, grantHarness.Session.character.Characters.Count(row => row.Id == fullA.TemplateId), "duplicate does not grant second character");
        }
        Progress(8, 58);
        select.Invoke(null, [player, targets[1].Id]);
        player = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<AscNet.Common.Database.Player>(player.ToBson());
        AssertEqual(targets[1].Id, player.DrawState.SelectedDrawByGroup[1].Slots[0], "target switch BSON selection");
        AssertEqual(8, Since("SinceAOrS"), "target switch BSON ten progress");
        AssertEqual(58, Since("SinceS"), "target switch BSON sixty progress");
        int oldTotal = player.DrawState.PityCountByGroup[1];
        RewardGoods[] batch = Enumerable.Range(0, 3).Select(_ => Pull(targets[1], .999, Category(6))).ToArray();
        AssertEqual("0,3,0", string.Join(',', batch.Select(Rank)), "batch sequential S boundary");
        AssertEqual(1, Since("SinceAOrS"), "post-S batch ten progress");
        AssertEqual(1, Since("SinceS"), "post-S batch sixty progress");
        applyProgress.Invoke(null, [player, targets[1].Id, 3]);
        AssertEqual(oldTotal + 3, player.DrawState.PityCountByGroup[1], "draw total remains cumulative");
        Progress(8, 28);
        RewardGoods[] aBatch = Enumerable.Range(0, 3).Select(_ => Pull(targets[0], .999, Category(6))).ToArray();
        AssertEqual("0,2,0", string.Join(',', aBatch.Select(Rank)), "batch sequential A boundary");
        AssertEqual(1, Since("SinceAOrS"), "post-A batch ten progress");
        AssertEqual(31, Since("SinceS"), "post-A batch preserves sixty progress");

        player = CreateDrawCompatibilityPlayer(46_092);
        player.DrawState.PityCountByGroup[1] = 119;
        AssertEqual(59, Since("SinceS"), "history-free migration preserves old visible S remaining");
        AssertEqual(0, Since("SinceAOrS"), "history-free migration starts explicit local A progress");
        player = CreateDrawCompatibilityPlayer(46_093);
        player.DrawState.PityCountByGroup[1] = 200;
        int sId = legacyBoundary.TemplateId;
        int aId = targets[0].ResourceIds[1];
        player.DrawState.HistoryByGroup[1] = new PlayerDrawHistoryGroupState
        {
            HistoryBySubType = new()
            {
                [0] =
                [
                    new() { DrawTime = 4, RewardGoods = new() { RewardType = (int)RewardType.Item, TemplateId = characters[aId].ItemId, ConvertFrom = aId, Count = 18 } },
                    new() { DrawTime = 2, RewardGoods = new() { RewardType = (int)RewardType.Character, TemplateId = sId, Count = 1 } },
                    new() { DrawTime = 5, RewardGoods = new() { RewardType = (int)RewardType.Item, TemplateId = characters[aId].ItemId, Count = 2 } },
                    new() { DrawTime = 3, RewardGoods = new() { RewardType = (int)RewardType.Item, TemplateId = characters[aId].ItemId, Count = 2 } }
                ]
            }
        };
        AssertEqual(1, Since("SinceAOrS"), "chronological history recognizes converted A but not raw shard");
        AssertEqual(3, Since("SinceS"), "chronological history anchors at full S");
        player = CreateDrawCompatibilityPlayer(46_094);
        DrawInfo handlerDraw = targets[0];
        using LoopbackSessionHarness handler = new(CreateDrawCompatibilityCharacter(46_094), player,
            CreateDrawCompatibilityInventory(46_094, [new Item { Id = handlerDraw.UseItemId, Count = handlerDraw.UseItemCount * 2 }]),
            "member-target-handler-guarantee");
        Progress(9, 59);
        InvokeRegisteredRequestHandler(nameof(DrawDrawCardRequest), handler.Session, 46_094,
            new DrawDrawCardRequest { DrawId = handlerDraw.Id, Count = 1 });
        DrawDrawCardResponse response = (DrawDrawCardResponse)ReadResponsePayload(handler, 46_094, nameof(DrawDrawCardResponse),
            "MemberTarget handler S guarantee", typeof(DrawDrawCardResponse), maxPacketsToRead: 20);
        AssertEqual(0, response.Code, "MemberTarget handler success");
        RewardGoods granted = response.RewardGoodsList.Single();
        AssertEqual(3, Rank(granted), "MemberTarget handler grants rank-correct S");
        AssertEqual(true, handler.Session.character.Characters.Any(row => row.Id == granted.TemplateId), "MemberTarget handler applies new full character");
        AssertEqual(60, response.ClientDrawInfo!.BottomTimes, "MemberTarget wire S guarantee resets");
        Dictionary<int, int> goods = TableReaderV2.Parse<DrawPreviewGoodsTable>().ToDictionary(row => row.Id, row => row.TemplateId);
        DrawPreviewTable handlerPreview = TableReaderV2.Parse<DrawPreviewTable>().Single(row => row.Id == handlerDraw.Id);
        foreach (int id in handlerPreview.GoodsId.Concat(handlerPreview.UpGoodsId).Select(id => goods.GetValueOrDefault(id))
            .Distinct().Where(id => ranks.GetValueOrDefault(id) == 3 && !handler.Session.character.Characters.Any(row => row.Id == id)))
            handler.Session.character.Characters.Add(new CharacterData { Id = (uint)id });
        Progress(9, 59);
        InvokeRegisteredRequestHandler(nameof(DrawDrawCardRequest), handler.Session, 46_095,
            new DrawDrawCardRequest { DrawId = handlerDraw.Id, Count = 1 });
        DrawDrawCardResponse duplicateResponse = (DrawDrawCardResponse)ReadResponsePayload(handler, 46_095, nameof(DrawDrawCardResponse),
            "MemberTarget handler duplicate guarantee", typeof(DrawDrawCardResponse), maxPacketsToRead: 20);
        AssertEqual(0, duplicateResponse.Code, "MemberTarget duplicate handler success");
        RewardGoods converted = duplicateResponse.RewardGoodsList.Single();
        AssertEqual(3, Rank(converted), "MemberTarget duplicate retains S identity");
        AssertEqual(characters[converted.ConvertFrom].ItemId, converted.TemplateId, "MemberTarget duplicate response uses matching shard");
        AssertEqual((long)converted.Count, handler.Session.inventory.Items.Single(item => item.Id == converted.TemplateId).Count, "MemberTarget handler credits duplicate shards");
        AssertEqual(0L, handler.Session.inventory.Items.Single(item => item.Id == handlerDraw.UseItemId).Count, "MemberTarget handler charges both draws");
        player = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<AscNet.Common.Database.Player>(
            playerSaves.LastSuccessfulReplacementBson ?? throw new InvalidDataException("MemberTarget handler did not persist its draw state."));
        AssertEqual(0, Since("SinceS"), "MemberTarget handler persisted duplicate S reset");
        AssertEqual(0, Since("SinceAOrS"), "MemberTarget handler persisted duplicate ten reset");
    }

    private static void ValidateMemberCalibrationLocalPolicy()
    {
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out var saves, out _, out _);
        AscNet.Common.Database.Player player = CreateDrawCompatibilityPlayer(46_096);
        using LoopbackSessionHarness first = new(CreateDrawCompatibilityCharacter(46_096), player,
            CreateDrawCompatibilityInventory(46_096, []), "member-calibration-first");
        using LoopbackSessionHarness second = new(CreateDrawCompatibilityCharacter(46_097), CreateDrawCompatibilityPlayer(46_097),
            CreateDrawCompatibilityInventory(46_097, []), "member-calibration-second");
        int packetId = 46_100;
        DrawAdjustActivityInfo Activity(LoopbackSessionHarness harness)
        {
            int id = packetId++;
            InvokeRegisteredRequestHandler(nameof(DrawGetDrawGroupListRequest), harness.Session, id, new DrawGetDrawGroupListRequest());
            return ReadResponsePayload<DrawGetDrawGroupListResponse>(harness, id, nameof(DrawGetDrawGroupListResponse),
                "member calibration group-list").DrawAdjustActivityInfoList.Single(info => info.DrawGroupId == 1);
        }
        DrawAdjustActivityInfo campaign = Activity(first);
        // This is the EN GetTargetCount gate: zero remaining selects the old banner without BtnAdd.
        // Keep this wire-only assertion before resolving any newly introduced runtime member.
        AssertEqual(1, campaign.AdjustTimes - campaign.TargetTimes, "fresh member calibration opens BannerNew/BtnAdd instead of old banner");
        AssertEqual(0, campaign.TargetId, "fresh member calibration lets player choose later");
        AssertEqual(1, Activity(second).AdjustTimes - Activity(second).TargetTimes, "second account has independent unused calibration");
        AssertEqual(0L, campaign.StartTime, "local permanent calibration has no start bound");
        AssertEqual(0L, campaign.EndTime, "local permanent calibration has no expiry");

        Type manager = RequiredAscNetGameServerType("AscNet.GameServer.Game.DrawManager");
        MethodInfo infos = RequiredMethod(manager, "GetDrawInfosByGroup", BindingFlags.Static | BindingFlags.Public,
            [typeof(int), typeof(AscNet.Common.Database.Player)]);
        MethodInfo rollDraw = RequiredMethod(manager, "RollDraw", BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(AscNet.Common.Database.Player), typeof(DrawInfo), typeof(Random)]);
        MethodInfo getRound = RequiredMethod(manager, "GetPityRound", BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(AscNet.Common.Database.Player), typeof(DrawInfo), typeof(Random)]);
        Dictionary<int, int> ranks = TableReaderV2.Parse<AscNet.Table.V2.share.character.quality.CharacterQualityTable>()
            .GroupBy(row => row.CharacterId).ToDictionary(group => group.Key, group => group.Min(row => row.Quality));
        DrawInfo draw = ((List<DrawInfo>)infos.Invoke(null, [1, player])!)
            .First(info => ranks.GetValueOrDefault(info.ResourceIds.GetValueOrDefault(1)) == 2);
        Dictionary<int, int> goods = TableReaderV2.Parse<DrawPreviewGoodsTable>().ToDictionary(row => row.Id, row => row.TemplateId);
        DrawPreviewTable preview = TableReaderV2.Parse<DrawPreviewTable>().Single(row => row.Id == draw.Id);
        int[] sPool = preview.GoodsId.Concat(preview.UpGoodsId).Select(id => goods.GetValueOrDefault(id)).Distinct()
            .Where(id => ranks.GetValueOrDefault(id) == 3 && AscNet.Common.Database.Character.IsOwnableCharacter((uint)id)).ToArray();
        AssertEqual(true, sPool.Length >= 2, "authoritative member pool offers distinct S choices");
        int target = sPool[^1];
        int alternative = sPool.First(id => id != target);
        Type responseType = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.DrawAdjustTargetResponse");
        AssertEqual(true, sPool.All(campaign.EffectTargetTemplateIds.Contains), "client selector exposes authoritative eligible S choices");
        int Choose(LoopbackSessionHarness harness, int activityId, int targetId)
        {
            int id = packetId++;
            InvokeRegisteredRequestHandler("DrawAdjustTargetRequest", harness.Session, id, new { ActivityId = activityId, TargetId = targetId });
            object response = ReadResponsePayload(harness, id, "DrawAdjustTargetResponse", "member calibration selection", responseType, maxPacketsToRead: 20);
            return (int)responseType.GetProperty("Code")!.GetValue(response)!;
        }
        Type guideMappingType = typeof(DrawProbShowTable).Assembly.GetType("AscNet.Table.V2.client.draw.DrawCalibrationGuideTable")
            ?? throw new InvalidDataException("Missing authoritative calibration guide mapping.");
        var guideMappings = (System.Collections.IEnumerable)RequiredGenericMethodDefinition(typeof(TableReaderV2), nameof(TableReaderV2.Parse),
            BindingFlags.Public | BindingFlags.Static, parameterCount: 0).MakeGenericMethod(guideMappingType).Invoke(null, null)!;
        int guideId = (int)guideMappingType.GetProperty("GuideGroupId")!.GetValue(guideMappings.Cast<object>()
            .Single(row => (int)guideMappingType.GetProperty("DrawGroupId")!.GetValue(row)! == 1))!;
        void CompleteGuide(LoopbackSessionHarness harness)
        {
            int id = packetId++;
            InvokeRegisteredRequestHandler(nameof(GuideCompleteRequest), harness.Session, id, new GuideCompleteRequest { GuideGroupId = guideId });
            AssertEqual(guideId, ReadPushPayload<NotifyGuide>(harness, nameof(NotifyGuide), "calibration guide completion notification").GuideGroupId,
                "guide completion notifies mapped calibration guide");
            AssertEqual(0, ReadResponsePayload<GuideCompleteResponse>(harness, id, nameof(GuideCompleteResponse), "calibration guide completion").Code,
                "actual GuideComplete accepts calibration guide");
        }
        AssertEqual(true, Choose(first, campaign.ActivityId, target) != 0, "calibration target choice waits for guide completion");
        AssertEqual(0, Activity(first).TargetId, "pre-guide rejection preserves unselected calibration");
        AssertEqual(1, Activity(first).AdjustTimes - Activity(first).TargetTimes, "pre-guide rejection leaves guide banner available");
        AssertEqual(0, Choose(first, campaign.ActivityId, 0), "choose-later remains idempotent before guide");
        CompleteGuide(first);
        CompleteGuide(second);
        PlayerDrawPityRound Round() => first.Session.player.DrawState.PityRounds.TryGetValue(1, out var round) ? round
            : (PlayerDrawPityRound)getRound.Invoke(null, [first.Session.player, draw, new DrawFixedRandom(0)])!;
        void Progress(int a, int s) => first.Session.player.DrawState.PityRounds[1] =
            new PlayerDrawPityRound { LowerMisses = a, Misses = s, Limit = 60, HasObtainedRare = true };
        int Since(string name) => name == "SinceAOrS" ? Round().LowerMisses : Round().Misses;
        RewardGoods Pull(double rare, double category, double rank = 0) =>
            (RewardGoods)rollDraw.Invoke(null, [first.Session.player, draw, new DrawSequenceRandom([rare, category, rank, 0d, 0d])])!;
        Progress(8, 58);
        AssertEqual(0, Choose(first, campaign.ActivityId, target), "select S calibration target");
        AssertEqual(target, Activity(first).TargetId, "group-list renders chosen S");
        AssertEqual(0, Choose(first, campaign.ActivityId, target), "same-choice retry succeeds");
        AssertEqual(8, Since("SinceAOrS"), "calibration selection preserves ten pity");
        AssertEqual(58, Since("SinceS"), "calibration selection preserves sixty pity");
        AssertEqual(0, Activity(second).TargetId, "selection does not leak to second account");
        AssertEqual(true, Choose(first, -1, alternative) != 0, "unknown activity rejected");
        AssertEqual(true, Choose(first, campaign.ActivityId, draw.ResourceIds[1]) != 0, "A target rejected");
        AssertEqual(true, Choose(first, campaign.ActivityId, -1) != 0, "unknown target rejected");
        AssertEqual(target, Activity(first).TargetId, "invalid requests preserve selected S");
        string persistedBeforeFailure = Convert.ToHexString(saves.LastSuccessfulReplacementBson
            ?? throw new InvalidDataException("Calibration selection was not persisted."));
        saves.ThrowOnReplaceOne = true;
        try
        {
            Choose(first, campaign.ActivityId, alternative);
            throw new InvalidDataException("Failed calibration persistence did not surface its Mongo error.");
        }
        catch (InvalidDataException exception) when (exception.GetBaseException() is MongoDB.Driver.MongoException)
        {
        }
        finally
        {
            saves.ThrowOnReplaceOne = false;
        }
        AssertEqual(target, Activity(first).TargetId, "failed persistence rolls selected S back");
        AssertEqual(persistedBeforeFailure, Convert.ToHexString(saves.LastSuccessfulReplacementBson!),
            "failed persistence leaves stored calibration unchanged");
        AssertEqual(0, Choose(first, campaign.ActivityId, 0), "clear means choose later");
        Progress(0, 0);
        RewardGoods unselectedS = Pull(0, 0);
        AssertEqual(3, ranks[unselectedS.TemplateId], "unselected natural S still rewards S");
        AssertEqual(1, Activity(first).AdjustTimes - Activity(first).TargetTimes, "unselected S does not spend entitlement");
        AssertEqual(0, Choose(first, campaign.ActivityId, target), "reselect after clearing");
        first.Session.player = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<AscNet.Common.Database.Player>(
            saves.LastSuccessfulReplacementBson ?? throw new InvalidDataException("Calibration selection was not persisted."));
        AssertEqual(target, Activity(first).TargetId, "relog restores usable selection");
        Progress(0, 0);
        Pull(Math.BitDecrement(1d), Math.BitDecrement(1d));
        AssertEqual(1, Activity(first).AdjustTimes - Activity(first).TargetTimes, "material pull does not consume calibration");
        double sChance = double.Parse(TableReaderV2.Parse<DrawProbShowTable>().Single(row => row.DrawId == draw.Id)
            .ProbShow.Where(value => !string.IsNullOrWhiteSpace(value)).Skip(1).First().TrimEnd('%'),
            System.Globalization.CultureInfo.InvariantCulture) / 100;
        Progress(0, 0);
        AssertEqual(2, ranks[Pull(sChance, 0).TemplateId], "calibration does not replace ordinary A");
        Progress(0, 0);
        AssertEqual(1, ranks[Pull(sChance, 0, Math.BitDecrement(1d)).TemplateId], "calibration does not replace ordinary B");
        AssertEqual(1, Activity(first).AdjustTimes - Activity(first).TargetTimes, "A and B leave calibration unused");
        Progress(8, 58);
        RewardGoods[] batch = Enumerable.Range(0, 3).Select(_ => Pull(Math.BitDecrement(1d), Math.BitDecrement(1d))).ToArray();
        AssertEqual(target, batch[1].TemplateId, "sequential batch substitutes selected S on sixty guarantee");
        AssertEqual(1, Since("SinceS"), "batch continues pity after calibrated S");
        AssertEqual(0, Activity(first).AdjustTimes - Activity(first).TargetTimes, "calibrated S exhausts banner entitlement");
        AssertEqual(0, Choose(first, campaign.ActivityId, target), "exhausted same-choice retry is idempotent");
        AssertEqual(true, Choose(first, campaign.ActivityId, alternative) != 0, "exhausted target cannot rearm");
        AssertEqual(true, Choose(first, campaign.ActivityId, 0) != 0, "exhausted clear cannot rearm");
        first.Session.player = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<AscNet.Common.Database.Player>(first.Session.player.ToBson());
        AssertEqual(0, Activity(first).AdjustTimes - Activity(first).TargetTimes, "BSON relog retains consumed entitlement without reset");
        Progress(0, 0);
        AssertEqual(unselectedS.TemplateId, Pull(0, 0).TemplateId, "later S returns to ordinary deterministic pool after single consumption");
        AssertEqual(1, Activity(second).AdjustTimes - Activity(second).TargetTimes, "first account consumption leaves second unused");

        // The second account owns every possible S: fulfillment must precede duplicate conversion.
        AssertEqual(0, Choose(second, campaign.ActivityId, alternative), "second account selects independently");
        foreach (int id in sPool)
            second.Session.character.Characters.Add(new CharacterData { Id = (uint)id });
        second.Session.player.DrawState.PityRounds[1] =
            new PlayerDrawPityRound { Misses = 59, Limit = 60, HasObtainedRare = true };
        second.Session.inventory.Items.Add(new Item { Id = draw.UseItemId, Count = draw.UseItemCount });
        int drawPacket = packetId++;
        InvokeRegisteredRequestHandler(nameof(DrawDrawCardRequest), second.Session, drawPacket, new DrawDrawCardRequest { DrawId = draw.Id, Count = 1 });
        DrawDrawCardResponse awarded = (DrawDrawCardResponse)ReadResponsePayload(second, drawPacket, nameof(DrawDrawCardResponse),
            "calibrated duplicate draw", typeof(DrawDrawCardResponse), maxPacketsToRead: 20);
        AssertEqual(0, awarded.Code, "calibrated duplicate draw succeeds");
        RewardGoods duplicate = awarded.RewardGoodsList.Single();
        AssertEqual(alternative, duplicate.ConvertFrom, "duplicate conversion retains calibrated S identity");
        int shardId = TableReaderV2.Parse<CharacterTable>().Single(row => row.Id == alternative).ItemId;
        AssertEqual(shardId, duplicate.TemplateId, "selected duplicate becomes its own shard");
        AssertEqual((long)duplicate.Count, second.Session.inventory.Items.Single(item => item.Id == shardId).Count, "calibrated duplicate credits actual inventory");
        object update = typeof(DrawDrawCardResponse).GetProperty("DrawAdjustData")!.GetValue(awarded)
            ?? throw new InvalidDataException("Calibrated draw omitted client consumption update.");
        AssertEqual(campaign.ActivityId, (int)update.GetType().GetProperty("ActivityId")!.GetValue(update)!, "draw update addresses displayed campaign");
        AssertEqual(1, (int)update.GetType().GetProperty("TargetTimes")!.GetValue(update)!, "draw response tells client calibration is consumed");
        AssertEqual(0, Activity(second).AdjustTimes - Activity(second).TargetTimes, "duplicate consumes exactly once");
        second.Session.player = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<AscNet.Common.Database.Player>(
            saves.LastSuccessfulReplacementBson ?? throw new InvalidDataException("Calibrated duplicate draw was not persisted."));
        AssertEqual(0, Activity(second).AdjustTimes - Activity(second).TargetTimes, "actual rewarded draw persists consumed entitlement across relog");
        first.Session.player = CreateDrawCompatibilityPlayer(46_098);
        first.Session.player.DrawState.PityCountByGroup[1] = 200;
        AssertEqual(1, Activity(first).AdjustTimes - Activity(first).TargetTimes, "untracked legacy player retains unused permanent entitlement");
        int naturalTarget = sPool.First(id => id != unselectedS.TemplateId);
        CompleteGuide(first);
        AssertEqual(0, Choose(first, campaign.ActivityId, naturalTarget), "legacy account selects natural-S target");
        Progress(0, 0);
        AssertEqual(naturalTarget, Pull(0, 0).TemplateId, "base-probability S fulfills selected target without waiting for pity");
        AssertEqual(1, Activity(first).TargetTimes, "natural S consumes calibration once");
    }

    private static void ValidateVersion46PlayerMarks()
    {
        long functional = TableReaderV2.Parse<FunctionalOpenTable>().Select(row => (long)row.Id).First(id => id > 0);
        long skipped = TableReaderV2.Parse<SkipFunctionalTable>()
            .Select(row => (long)(row.FunctionalId ?? throw new InvalidDataException("SkipFunctional row has no FunctionalId.")))
            .First(id => id > 0 && id != functional);
        const long uid = 46_003;
        AscNet.Common.Database.Player player = CreateDrawCompatibilityPlayer(uid);
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out RecordingMongoCollectionProxy<AscNet.Common.Database.Player> saves, out _, out _);
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(uid), player, CreateDrawCompatibilityInventory(uid, []), "v46-player-marks");

        Dispatch(functional, 46_030, 0, 1, "FunctionalOpen mark");
        Dispatch(functional, 46_031, 0, 1, "duplicate FunctionalOpen mark");
        Dispatch(skipped, 46_032, 0, 2, "SkipFunctional mark");
        Dispatch(999_999, 46_033, 1, 2, "arbitrary mark");
        if (!player.PlayerData.Marks.SequenceEqual([functional, skipped]))
            throw new InvalidDataException($"4.6 player marks: expected [{functional},{skipped}], got [{string.Join(',', player.PlayerData.Marks)}].");

        void Dispatch(long id, int packetId, int code, int saveCount, string name)
        {
            InvokeRegisteredRequestHandler(nameof(ChangePlayerMarkRequest), harness.Session, packetId, new ChangePlayerMarkRequest { MaskId = id });
            ChangePlayerMarkResponse response = ReadResponsePayload<ChangePlayerMarkResponse>(harness, packetId, nameof(ChangePlayerMarkResponse), name);
            AssertEqual(code, response.Code, $"{name} code");
            AssertEqual(saveCount, saves.ReplaceOneCalls, $"{name} persistence count");
        }
    }

    private static void ValidateVersion46GuideCompletion()
    {
        Dictionary<int, ConditionTable> conditions = TableReaderV2.Parse<ConditionTable>()
            .ToDictionary(row => row.Id);
        List<GuideGroupTable> guides = TableReaderV2.Parse<GuideGroupTable>().ToList();
        GuideGroupTable completedTriggerGuide = guides.First(row =>
            row.CompleteId == row.Id
            && row.RewardId == 0
            && row.ConditionId.Any(id =>
                conditions.TryGetValue(id, out ConditionTable? condition)
                && condition.Type == 10108
                && condition.Params.Count > 0));
        int[] stagesThatDisableTrigger = completedTriggerGuide.ConditionId
            .Where(conditions.ContainsKey)
            .SelectMany(id => conditions[id].Type == 10108 ? conditions[id].Params : [])
            .Distinct()
            .ToArray();
        GuideGroupTable sharedCompletionGuide = guides.First(row =>
            row.CompleteId != row.Id && row.RewardId == 0);
        List<GuideGroupTable> skippedGroup = guides
            .Where(row => row.GroupId != 0)
            .GroupBy(row => row.GroupId)
            .Where(group => group.Count() > 1 && group.All(row => row.RewardId == 0))
            .Select(group => group.ToList())
            .First();
        GuideGroupTable rewardedGroupGuide = guides.First(row => row.RewardId > 0);

        const long uid = 46_004;
        AscNet.Common.Database.Player player = CreateDrawCompatibilityPlayer(uid);
        using MongoCollectionOverride mongo =
            MongoCollectionOverride.InstallForDailySignInCompatibility(
                out RecordingMongoCollectionProxy<AscNet.Common.Database.Player> playerSaves,
                out _,
                out _);
        using LoopbackSessionHarness harness = new(
            CreateDrawCompatibilityCharacter(uid),
            player,
            CreateDrawCompatibilityInventory(uid, []),
            "v46-guide-complete");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(uid);

        foreach (int stageId in stagesThatDisableTrigger)
            harness.Session.stage.AddStage(new StageDatum { StageId = (uint)stageId, Passed = true });
        InvokeRegisteredRequestHandler(
            nameof(GuideCompleteRequest),
            harness.Session,
            46_040,
            new GuideCompleteRequest { GuideGroupId = completedTriggerGuide.Id });
        NotifyGuide completedAfterTriggerNotify = ReadPushPayload<NotifyGuide>(
            harness,
            nameof(NotifyGuide),
            "4.6 GuideComplete after trigger condition changes notify");
        AssertEqual(completedTriggerGuide.Id,
            completedAfterTriggerNotify.GuideGroupId,
            "4.6 GuideComplete after trigger condition changes notified guide");
        GuideCompleteResponse completedAfterTrigger = ReadResponsePayload<GuideCompleteResponse>(
            harness,
            46_040,
            nameof(GuideCompleteResponse),
            "4.6 GuideComplete after trigger condition changes");
        AssertEqual(0, completedAfterTrigger.Code,
            "4.6 GuideComplete accepts completion after its display trigger changes");
        AssertEqual(true, player.PlayerData.GuideData.Contains(completedTriggerGuide.Id),
            "4.6 GuideComplete records completion after trigger changes");
        AssertEqual(1, playerSaves.ReplaceOneCalls,
            "4.6 GuideComplete persists completion after trigger changes");

        InvokeRegisteredRequestHandler(
            nameof(GuideCompleteRequest),
            harness.Session,
            46_041,
            new GuideCompleteRequest { GuideGroupId = sharedCompletionGuide.Id });
        NotifyGuide sharedCompletionNotify = ReadPushPayload<NotifyGuide>(
            harness,
            nameof(NotifyGuide),
            "4.6 GuideComplete shared completion config notify");
        AssertEqual(sharedCompletionGuide.Id,
            sharedCompletionNotify.GuideGroupId,
            "4.6 GuideComplete shared completion config notified guide");
        GuideCompleteResponse sharedCompletion = ReadResponsePayload<GuideCompleteResponse>(
            harness,
            46_041,
            nameof(GuideCompleteResponse),
            "4.6 GuideComplete shared completion config");
        AssertEqual(0, sharedCompletion.Code,
            "4.6 GuideComplete accepts table-backed shared completion config");
        AssertEqual(true, player.PlayerData.GuideData.Contains(sharedCompletionGuide.Id),
            "4.6 GuideComplete records shared completion config");
        AssertEqual(2, playerSaves.ReplaceOneCalls,
            "4.6 GuideComplete persists shared completion config");

        InvokeRegisteredRequestHandler(
            nameof(GuideGroupFinishRequest),
            harness.Session,
            46_042,
            new GuideGroupFinishRequest { GroupId = skippedGroup[0].GroupId });
        GuideGroupFinishResponse skipped = ReadResponsePayload<GuideGroupFinishResponse>(
            harness,
            46_042,
            nameof(GuideGroupFinishResponse),
            "4.6 GuideGroupFinish");
        AssertEqual(0, skipped.Code, "4.6 GuideGroupFinish code");
        AssertEqual(true,
            skippedGroup.All(row => player.PlayerData.GuideData.Contains(row.Id)),
            "4.6 GuideGroupFinish records every guide in the group");
        AssertEqual(3, playerSaves.ReplaceOneCalls,
            "4.6 GuideGroupFinish persists skipped group");

        InvokeRegisteredRequestHandler(
            nameof(GuideGroupFinishRequest),
            harness.Session,
            46_043,
            new GuideGroupFinishRequest { GroupId = rewardedGroupGuide.GroupId });
        Packet rewardPushPacket = harness.ReadPacket("4.6 rewarded GuideGroupFinish push");
        AssertEqual(Packet.ContentType.Push, rewardPushPacket.Type,
            "4.6 rewarded GuideGroupFinish packet type");
        GuideGroupFinishResponse rewarded = ReadResponsePayload<GuideGroupFinishResponse>(
            harness,
            46_043,
            nameof(GuideGroupFinishResponse),
            "4.6 rewarded GuideGroupFinish");
        AssertEqual(0, rewarded.Code, "4.6 rewarded GuideGroupFinish code");
        AssertEqual(true, rewarded.RewardGoodsList is { Count: > 0 },
            "4.6 rewarded GuideGroupFinish returns configured rewards");
        AssertEqual(true, player.PlayerData.GuideData.Contains(rewardedGroupGuide.Id),
            "4.6 rewarded GuideGroupFinish records completion");
        AssertEqual(4, playerSaves.ReplaceOneCalls,
            "4.6 rewarded GuideGroupFinish persists completion");

        AscNet.Common.Database.Player guideReload =
            MongoDB.Bson.Serialization.BsonSerializer.Deserialize<AscNet.Common.Database.Player>(
                player.ToBson());
        using (LoopbackSessionHarness reloginHarness = new(
            harness.Session.character,
            guideReload,
            harness.Session.inventory,
            sessionId: "version-46-guide-relogin"))
        {
            reloginHarness.Session.stage = CreateLoginAccountCompatibilityStage(uid);
            MethodInfo buildNotifyLogin = RequiredMethod(
                RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule"),
                "BuildNotifyLogin",
                BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(Session)]);
            NotifyLogin relogin = (NotifyLogin)buildNotifyLogin.Invoke(
                null,
                [reloginHarness.Session])!;
            NotifyLogin reloginWire = MessagePackSerializer.Deserialize<NotifyLogin>(
                MessagePackSerializer.Serialize(relogin));
            AssertEqual(true,
                skippedGroup.All(row => reloginWire.PlayerData.GuideData.Contains(row.Id)),
                "4.6 skipped guide group returned after relogin");
        }
        int savesAfterRelogin = playerSaves.ReplaceOneCalls;

        InvokeRegisteredRequestHandler(
            nameof(GuideGroupFinishRequest),
            harness.Session,
            46_044,
            new GuideGroupFinishRequest { GroupId = skippedGroup[0].GroupId });
        GuideGroupFinishResponse repeated = ReadResponsePayload<GuideGroupFinishResponse>(
            harness,
            46_044,
            nameof(GuideGroupFinishResponse),
            "4.6 repeated GuideGroupFinish");
        AssertEqual(0, repeated.Code, "4.6 repeated GuideGroupFinish code");
        AssertEqual(savesAfterRelogin, playerSaves.ReplaceOneCalls,
            "4.6 repeated GuideGroupFinish does not save");

        InvokeRegisteredRequestHandler(
            nameof(GuideGroupFinishRequest),
            harness.Session,
            46_045,
            new GuideGroupFinishRequest { GroupId = int.MinValue });
        GuideGroupFinishResponse invalid = ReadResponsePayload<GuideGroupFinishResponse>(
            harness,
            46_045,
            nameof(GuideGroupFinishResponse),
            "4.6 invalid GuideGroupFinish");
        AssertEqual(1, invalid.Code, "4.6 invalid GuideGroupFinish code");
        AssertEqual(savesAfterRelogin, playerSaves.ReplaceOneCalls,
            "4.6 invalid GuideGroupFinish does not save");
    }
}
