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
using AscNet.Table.V2.share.miniactivity.dyemerge;
using AscNet.Table.V2.share.theatre6;
using AscNet.Table.V2.share.equip;
using AscNet.Table.V2.share.draw;
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
        List<ActivityScheduleEntry> transfiniteRotations = ActivityScheduleService.All
            .Where(schedule => schedule.Source.Contains("/TransfiniteActivity", StringComparison.Ordinal))
            .OrderBy(schedule => schedule.StartTime)
            .ToList();
        if (transfiniteRotations.Count != 2
            || transfiniteRotations.Any(schedule => schedule.StartTime == 0 || schedule.EndTime <= schedule.StartTime)
            || ActivityScheduleService.All.Any(schedule => schedule.Source == "policy:latest-TransfiniteActivity-always-open"))
            throw new InvalidDataException("4.6 Transfinite schedule was not derived from bounded EN version history.");
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
            RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule"),
            "BuildWheelchairManualActivityPayload",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
            [typeof(DateTimeOffset)]);
        NotifyWheelchairManualActivity wheelchair = (NotifyWheelchairManualActivity)wheelchairBuilder.Invoke(null, [current])!;
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

        ServerVersionConfig fallback = (ServerVersionConfig)getVersion.Invoke(null, ["99.0.0"])!;
        AssertEqual(live.IndexSha1, fallback.IndexSha1, "unknown future version uses latest live metadata");
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
        });

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
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(uid), player, CreateDrawCompatibilityInventory(uid, []), "v46-table-draw");
        InvokeRegisteredRequestHandler(nameof(DrawGetDrawGroupListRequest), harness.Session, 46_020, new DrawGetDrawGroupListRequest());
        DrawGetDrawGroupListResponse catalog = ReadResponsePayload<DrawGetDrawGroupListResponse>(harness, 46_020, nameof(DrawGetDrawGroupListResponse), "4.6 table draw catalog");
        int[] expectedGroups = [1, 2, 4, 12, 13, 16, 22, 35, 36];
        AssertEqual(string.Join(',', expectedGroups), string.Join(',', catalog.DrawGroupInfoList.Select(group => group.Id)), "4.6 server-pushed draw groups");

        DrawGroupInfo group35 = catalog.DrawGroupInfoList.Single(group => group.Id == 35);
        DrawGroupInfo group36 = catalog.DrawGroupInfoList.Single(group => group.Id == 36);
        AssertEqual(1499, group35.UseDrawIdDict[0], "4.6 group 35 selected draw");
        AssertEqual("2493", string.Join(',', group36.OptionalDrawIdList), "4.6 group 36 optional draw");
        AssertEqual(1784246400L, group35.StartTime, "4.6 group 35 start");
        AssertEqual(1787094000L, group35.EndTime, "4.6 group 35 end");
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

        MethodInfo getGroupByDrawId = RequiredMethod(
            RequiredAscNetGameServerType("AscNet.GameServer.Game.DrawManager"),
            "GetGroupByDrawId",
            BindingFlags.Static | BindingFlags.Public,
            [typeof(int)]);
        MethodInfo getProgressForDrawIds = RequiredMethod(
            RequiredAscNetGameServerType("AscNet.GameServer.Game.DrawManager"),
            "GetProgressForDrawIds",
            BindingFlags.Static | BindingFlags.Public,
            [typeof(AscNet.Common.Database.Player), typeof(IEnumerable<int>)]);
        int firstCanLiverGroup = (int)getGroupByDrawId.Invoke(null, [canLiverActivity.DrawIds[0]])!;
        int secondCanLiverGroup = (int)getGroupByDrawId.Invoke(null, [canLiverActivity.DrawIds[1]])!;
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

        DrawInfo draw35 = GetInfo(35, 46_021);
        DrawInfo draw36 = GetInfo(36, 46_022);
        foreach (DrawInfo draw in new[] { draw35, draw36 })
        {
            AssertEqual(50005, draw.UseItemId, $"4.6 draw {draw.Id} use item");
            AssertEqual(250, draw.UseItemCount, $"4.6 draw {draw.Id} use item count");
            AssertEqual(draw.StartTime, group35.StartTime, $"4.6 draw {draw.Id} active start");
            AssertEqual(draw.EndTime, group35.EndTime, $"4.6 draw {draw.Id} active end");
        }
        AssertEqual(60, draw35.MaxBottomTimes, "4.6 draw 1499 pity");
        AssertEqual(100, draw36.MaxBottomTimes, "4.6 draw 2493 pity");

        MethodInfo drawMethod = RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Game.DrawManager"), "DrawDraw", BindingFlags.Static | BindingFlags.Public, [typeof(AscNet.Common.Database.Player), typeof(int), typeof(int)]);
        List<RewardGoods> sampledRewards = Enumerable.Range(0, 16).SelectMany(offset => (List<RewardGoods>)drawMethod.Invoke(null, [player, 1499, offset])!).ToList();
        if (sampledRewards.Count != 16 || sampledRewards.All(reward => reward.TemplateId == draw35.ResourceIds[1]))
            throw new InvalidDataException("4.6 draw 1499: expected the restored table-pool reward algorithm, not a deterministic target reward.");

        AssertEqual(true, AscNet.Common.Database.Character.IsOwnableCharacter(1401003), "4.6 draw 1499 target ownability");
        player.DrawState.PityCountByGroup[35] = 59;
        harness.Session.inventory.Items.Add(new Item { Id = 50003, Count = 250 });
        byte[] beforeWrongTicket = player.ToBson();
        InvokeRegisteredRequestHandler(nameof(DrawDrawCardRequest), harness.Session, 46_029, new DrawDrawCardRequest { DrawId = 1499, Count = 1, UseDrawTicketId = 50003 });
        DrawDrawCardResponse wrongTicketDraw = ReadResponsePayload<DrawDrawCardResponse>(harness, 46_029, nameof(DrawDrawCardResponse), "4.6 group 35 wrong ticket draw");
        AssertEqual(1, wrongTicketDraw.Code, "4.6 group 35 wrong ticket rejection");
        AssertEqual(250L, harness.Session.inventory.Items.Single(item => item.Id == 50003).Count, "4.6 group 35 wrong ticket remains");
        AssertEqual(Convert.ToHexString(beforeWrongTicket), Convert.ToHexString(player.ToBson()), "4.6 group 35 wrong ticket preserves pity/history");

        harness.Session.inventory.Items.Add(new Item { Id = 50005, Count = 250 });
        InvokeRegisteredRequestHandler(nameof(DrawDrawCardRequest), harness.Session, 46_030, new DrawDrawCardRequest { DrawId = 1499, Count = 1 });
        DrawDrawCardResponse forcedGroup35Draw = (DrawDrawCardResponse)ReadResponsePayload(harness, 46_030, nameof(DrawDrawCardResponse), "4.6 group 35 forced pity draw", typeof(DrawDrawCardResponse), maxPacketsToRead: 8);
        AssertEqual(0, forcedGroup35Draw.Code, "4.6 group 35 forced pity draw code");
        RewardGoods forcedGroup35Reward = forcedGroup35Draw.RewardGoodsList.Single();
        if (forcedGroup35Reward.TemplateId != 1401003 && forcedGroup35Reward.ConvertFrom != 1401003)
            throw new InvalidDataException($"4.6 group 35 forced pity target: expected 1401003, got template {forcedGroup35Reward.TemplateId}, conversion {forcedGroup35Reward.ConvertFrom}.");
        AssertEqual(0L, harness.Session.inventory.Items.Single(item => item.Id == 50005).Count, "4.6 group 35 retail ticket deduction");
        if (!harness.Session.character.Characters.Any(character => character.Id == 1401003))
            throw new InvalidDataException("4.6 group 35 forced pity draw did not create character 1401003.");

        // Pity belongs to the group, not to an individual optional banner.
        player.DrawState.PityCountByGroup[12] = 59;
        player.DrawState.ProgressByDrawId[1495] = new PlayerDrawProgress { TotalCount = 59, TodayCount = 59 };
        InvokeRegisteredRequestHandler(nameof(DrawSetUseDrawIdRequest), harness.Session, 46_025, new DrawSetUseDrawIdRequest { DrawId = 1496 });
        DrawSetUseDrawIdResponse switched = ReadResponsePayload<DrawSetUseDrawIdResponse>(harness, 46_025, nameof(DrawSetUseDrawIdResponse), "4.6 group 12 pity selection");
        AssertEqual(0, switched.Code, "4.6 group 12 pity selection code");
        InvokeRegisteredRequestHandler(nameof(DrawGetDrawInfoListRequest), harness.Session, 46_026, new DrawGetDrawInfoListRequest { GroupId = 12 });
        DrawGetDrawInfoListResponse nearPity = ReadResponsePayload<DrawGetDrawInfoListResponse>(harness, 46_026, nameof(DrawGetDrawInfoListResponse), "4.6 group 12 shared pity");
        if (nearPity.DrawInfoList.Any(info => info.BottomTimes != 1))
            throw new InvalidDataException("4.6 group 12: every optional draw must expose the shared one-pull pity state.");
        AscNet.Common.Database.Player pityReload = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<AscNet.Common.Database.Player>(player.ToBson());
        AssertEqual(59, pityReload.DrawState.PityCountByGroup[12], "4.6 group 12 pity BSON roundtrip");
        AssertEqual(1496, pityReload.DrawState.SelectedDrawByGroup[12].Slots[0], "4.6 group 12 selection BSON roundtrip");

        harness.Session.inventory.Items.Single(item => item.Id == 50005).Count += 250;
        InvokeRegisteredRequestHandler(nameof(DrawDrawCardRequest), harness.Session, 46_027, new DrawDrawCardRequest { DrawId = 1496, Count = 1 });
        DrawDrawCardResponse forcedDraw = (DrawDrawCardResponse)ReadResponsePayload(harness, 46_027, nameof(DrawDrawCardResponse), "4.6 group 12 forced pity draw", typeof(DrawDrawCardResponse), maxPacketsToRead: 8);
        AssertEqual(0, forcedDraw.Code, "4.6 group 12 forced pity draw code");
        RewardGoods forcedReward = forcedDraw.RewardGoodsList.Single();
        int forcedTarget = nearPity.DrawInfoList.Single(info => info.Id == 1496).ResourceIds[1];
        if (forcedReward.TemplateId != forcedTarget && forcedReward.ConvertFrom != forcedTarget)
            throw new InvalidDataException($"4.6 group 12 forced pity target: expected {forcedTarget}, got template {forcedReward.TemplateId}, conversion {forcedReward.ConvertFrom}.");
        InvokeRegisteredRequestHandler(nameof(DrawGetDrawInfoListRequest), harness.Session, 46_028, new DrawGetDrawInfoListRequest { GroupId = 12 });
        DrawGetDrawInfoListResponse resetPity = ReadResponsePayload<DrawGetDrawInfoListResponse>(harness, 46_028, nameof(DrawGetDrawInfoListResponse), "4.6 group 12 reset pity");
        if (resetPity.DrawInfoList.Any(info => info.BottomTimes != 60))
            throw new InvalidDataException("4.6 group 12: shared pity must reset every optional draw after the forced pull.");
        pityReload = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<AscNet.Common.Database.Player>(player.ToBson());
        AssertEqual(60, pityReload.DrawState.PityCountByGroup[12], "4.6 group 12 reset pity BSON roundtrip");

        InvokeRegisteredRequestHandler(nameof(DrawSetUseDrawIdRequest), harness.Session, 46_023, new DrawSetUseDrawIdRequest { DrawId = 1499 });
        DrawSetUseDrawIdResponse selected = ReadResponsePayload<DrawSetUseDrawIdResponse>(harness, 46_023, nameof(DrawSetUseDrawIdResponse), "4.6 group 35 selection");
        AssertEqual(0, selected.Code, "4.6 group 35 selection code");
        AssertEqual(1, selected.SwitchDrawIdCount, "4.6 group 35 selection count");

        byte[] beforeUnknown = player.ToBson();
        InvokeRegisteredRequestHandler(nameof(DrawSetUseDrawIdRequest), harness.Session, 46_024, new DrawSetUseDrawIdRequest { DrawId = int.MaxValue });
        DrawSetUseDrawIdResponse unknown = ReadResponsePayload<DrawSetUseDrawIdResponse>(harness, 46_024, nameof(DrawSetUseDrawIdResponse), "unknown draw selection");
        AssertEqual(1, unknown.Code, "unknown draw rejection");
        AssertEqual(0, unknown.SwitchDrawIdCount, "unknown draw selection unchanged count");
        AssertEqual(Convert.ToHexString(beforeUnknown), Convert.ToHexString(player.ToBson()), "unknown draw rejection state");
    }

    private static void ValidateVersion46PlayerMarks()
    {
        long functional = TableReaderV2.Parse<FunctionalOpenTable>().Select(row => (long)row.Id).First(id => id > 0);
        long skipped = TableReaderV2.Parse<SkipFunctionalTable>().Select(row => (long)row.FunctionalId).First(id => id > 0 && id != functional);
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
