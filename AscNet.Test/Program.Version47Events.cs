using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.miniactivity.envelope;
using AscNet.Table.V2.share.miniactivity.musicgame.concertpreheating;
using AscNet.Table.V2.share.pbr;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using System.Reflection;

namespace AscNet.Test;

internal static partial class Program
{
    private static void ValidateVersion47EventCompatibility()
    {
        using MongoCollectionOverride mongoOverride = MongoCollectionOverride.InstallForShopCompatibility();

        static Type ModuleType() => RequiredAscNetGameServerType("AscNet.GameServer.Handlers.Version47EventModule");
        static MethodInfo ModuleMethod(string name, params Type[] signature) =>
            RequiredMethod(ModuleType(), name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, signature);
        static object? InvokeMaybe(string method, Type[] signature, params object?[] arguments) =>
            ModuleMethod(method, signature).Invoke(null, arguments);
        static object Invoke(string method, Type[] signature, params object?[] arguments) =>
            InvokeMaybe(method, signature, arguments)
            ?? throw new InvalidDataException($"Version47EventModule.{method} returned null.");

        static ActivityScheduleEntry RequireSchedule(long timeId, string family)
        {
            if (!ActivityScheduleService.TryGet(timeId, out ActivityScheduleEntry entry))
                throw new InvalidDataException($"4.7 {family} TimeId {timeId} is not staged in ActivitySchedule.tsv.");
            if (entry.StartTime <= 0)
                throw new InvalidDataException($"4.7 {family} TimeId {timeId} has no concrete StartTime.");
            return entry;
        }

        ValidateVersion47EventWireShape();

        // ---- authoritative schedule anchors + deterministic open/closed clocks ----
        EnvelopeActivityTable envelope = TableReaderV2.Parse<EnvelopeActivityTable>().Single();
        PBRActivityTable pbr = TableReaderV2.Parse<PBRActivityTable>().Single(row => row.TimeId is int t && t > 0);
        ConcertPreHeatingActivityTable concert = TableReaderV2.Parse<ConcertPreHeatingActivityTable>().Single(row => row.TimeId > 0);
        ActivityScheduleEntry envelopeSchedule = RequireSchedule(envelope.TimeId, "Envelope");
        ActivityScheduleEntry pbrSchedule = RequireSchedule(pbr.TimeId!.Value, "PBR");
        ActivityScheduleEntry concertSchedule = RequireSchedule(concert.TimeId, "Concert");
        DateTimeOffset envelopeOpen = DateTimeOffset.FromUnixTimeSeconds(envelopeSchedule.StartTime);
        DateTimeOffset pbrOpen = DateTimeOffset.FromUnixTimeSeconds(pbrSchedule.StartTime);
        DateTimeOffset concertOpen = DateTimeOffset.FromUnixTimeSeconds(concertSchedule.StartTime);
        DateTimeOffset[] allOpen = { envelopeOpen, pbrOpen, concertOpen };
        DateTimeOffset commonOpen = allOpen.Max();
        DateTimeOffset commonClosed = allOpen.Min().AddSeconds(-1);
        foreach ((ActivityScheduleEntry schedule, long timeId, string family) in
            new[] { (envelopeSchedule, (long)envelope.TimeId, "Envelope"), (pbrSchedule, pbr.TimeId!.Value, "PBR"), (concertSchedule, (long)concert.TimeId, "Concert") })
        {
            if (schedule.EndTime > 0 && schedule.EndTime <= schedule.StartTime + 86400)
                throw new InvalidDataException($"4.7 {family} schedule window is shorter than one day; cannot test business-day rollover.");
        }

        ValidateVersion47EnvelopeCompatibility(envelope, envelopeOpen, InvokeMaybe, Invoke);
        ValidateVersion47PbrCompatibility(pbr, pbrOpen, InvokeMaybe, Invoke);
        ValidateVersion47ConcertCompatibility(concert, concertOpen, InvokeMaybe);
        ValidateVersion47SendLoginPushesOrder(commonOpen, commonClosed, InvokeMaybe);
    }

    private static void ValidateVersion47EventWireShape()
    {
        // Envelope
        AssertMailNamedMapKeys(new NotifyEnvelope { ActivityId = 1, HasReward = true }, ["ActivityId", "HasReward"], "NotifyEnvelope");
        AssertMailNamedMapKeys(new EnvelopeEnterRequest(), [], "EnvelopeEnterRequest");
        AssertMailNamedMapKeys(new EnvelopeEnterResponse(), ["Code", "RewardGoodsList", "TaskRewardGoodsList", "OpenedCharacterIds", "InstrumentBindings", "AvgWatchedCharacterIds"], "EnvelopeEnterResponse");

        // PBR root
        AssertMailNamedMapKeys(new PbrActivityDataNotify(), ["PbrDataDb"], "PbrActivityDataNotify");
        AssertMailNamedMapKeys(new PbrDataDb(), ["ActivityId", "SegmentSettleData", "MetaProgression", "StageRecords", "Compendiums"], "PbrDataDb");
        AssertMailNamedMapKeys(new PbrMetaProgression(), ["UnlockNodes"], "PbrMetaProgression");
        AssertMailNamedMapKeys(new PbrCompendiums(), ["CompendiumItems", "CompendiumMonsters"], "PbrCompendiums");
        AssertMailNamedMapKeys(new PbrCompendiumPush(), ["AddCompendiumItems", "UpdateCompendiumItems", "AddCompendiumMonsters", "UpdateCompendiumMonsters"], "PbrCompendiumPush");

        // Concert
        AssertMailNamedMapKeys(new NotifyConcertPreHeating(), ["ConcertPreHeatingDataDb"], "NotifyConcertPreHeating");
        AssertMailNamedMapKeys(new ConcertPreHeatingDataDb(), ["ActivityId", "StageFinish"], "ConcertPreHeatingDataDb");
        AssertMailNamedMapKeys(new NotifyConcertVideoConfig(), ["ConcertVideoConfigs"], "NotifyConcertVideoConfig");
    }

    private static void ValidateVersion47EnvelopeCompatibility(
        EnvelopeActivityTable envelope,
        DateTimeOffset envelopeOpen,
        Func<string, Type[], object?[], object?> invokeMaybe,
        Func<string, Type[], object?[], object> invoke)
    {
        // NotifyEnvelope reflects daily-grant availability (fresh => HasReward true).
        Player envelopeFresh = CreateDrawCompatibilityPlayer(47_101);
        NotifyEnvelope? freshNotify = (NotifyEnvelope?)invokeMaybe("BuildEnvelopeNotify", [typeof(Player), typeof(DateTimeOffset)], [envelopeFresh, envelopeOpen]);
        AssertEqual(envelope.Id, freshNotify!.ActivityId, "Envelope login push activity id");
        AssertEqual(true, freshNotify.HasReward, "Envelope fresh login has reward");

        // Claimed today => no reward; next business day => reward again; closed => no push.
        envelopeFresh.Envelope.ActivityId = envelope.Id;
        envelopeFresh.Envelope.LastDailyGrantBusinessDay =
            (int)invoke("BusinessDay", [typeof(DateTimeOffset)], [envelopeOpen]);
        NotifyEnvelope? claimedNotify = (NotifyEnvelope?)invokeMaybe("BuildEnvelopeNotify", [typeof(Player), typeof(DateTimeOffset)], [envelopeFresh, envelopeOpen]);
        AssertEqual(false, claimedNotify!.HasReward, "Envelope claimed today has no reward");
        NotifyEnvelope? nextDayNotify = (NotifyEnvelope?)invokeMaybe("BuildEnvelopeNotify", [typeof(Player), typeof(DateTimeOffset)], [envelopeFresh, envelopeOpen.AddDays(1)]);
        AssertEqual(true, nextDayNotify!.HasReward, "Envelope next business day has reward");
        AssertEqual(null, (NotifyEnvelope?)invokeMaybe("BuildEnvelopeNotify", [typeof(Player), typeof(DateTimeOffset)], [envelopeFresh, envelopeOpen.AddSeconds(-1)]),
            "Envelope closed clock yields no push");

        // First enter grants the daily ticket, pushes the item before the exact response, and
        // echoes persisted open/bind/AVG fields; replay on the same business day is non-duplicating.
        long envelopeUid = 47_102;
        using (LoopbackSessionHarness envelopeHarness = new(
            CreateDrawCompatibilityCharacter(envelopeUid),
            CreateDrawCompatibilityPlayer(envelopeUid),
            CreateDrawCompatibilityInventory(envelopeUid, []),
            "version47-envelope-enter-test"))
        {
            invokeMaybe("HandleEnvelopeEnter", [typeof(Session), typeof(int), typeof(DateTimeOffset)], [envelopeHarness.Session, 1001, envelopeOpen]);
            NotifyItemDataList itemPush = ReadPushPayload<NotifyItemDataList>(envelopeHarness, nameof(NotifyItemDataList), "Envelope first-enter item push");
            EnvelopeEnterResponse first = ReadResponsePayload<EnvelopeEnterResponse>(
                envelopeHarness, 1001, nameof(EnvelopeEnterResponse), "Envelope first-enter response");
            AssertEqual(0, first.Code, "Envelope first-enter code");
            if (first.RewardGoodsList.Count == 0)
                throw new InvalidDataException("Envelope first-enter granted no daily ticket reward.");
            if (itemPush.ItemDataList.Count == 0)
                throw new InvalidDataException("Envelope first-enter emitted no item push.");
            // Reward ordering: the item push precedes the response, and the response lists the
            // granted daily ticket (from the Envelope + Reward tables) first.
            AssertEqual(envelope.TicketItemId, first.RewardGoodsList[0].TemplateId,
                "Envelope response reward is the daily ticket");
            AssertEqual(1, first.RewardGoodsList[0].RewardType, "Envelope reward type is Item");
            AssertEqual(0, first.TaskRewardGoodsList.Count, "Envelope first-enter task rewards empty");
            AssertEqual(0, first.OpenedCharacterIds.Count, "Envelope first-enter opened characters empty");
            AssertEqual(0, first.InstrumentBindings.Count, "Envelope first-enter instrument bindings empty");
            AssertEqual(0, first.AvgWatchedCharacterIds.Count, "Envelope first-enter avg characters empty");

            // Same business day replay: no item push, no re-grant, still succeeds.
            invokeMaybe("HandleEnvelopeEnter", [typeof(Session), typeof(int), typeof(DateTimeOffset)], [envelopeHarness.Session, 1002, envelopeOpen]);
            EnvelopeEnterResponse replay = ReadResponsePayload<EnvelopeEnterResponse>(
                envelopeHarness, 1002, nameof(EnvelopeEnterResponse), "Envelope same-day replay response");
            AssertEqual(0, replay.Code, "Envelope replay code");
            AssertEqual(0, replay.RewardGoodsList.Count, "Envelope replay does not re-grant");

            // Next business day: a fresh grant is issued again (item push + response).
            invokeMaybe("HandleEnvelopeEnter", [typeof(Session), typeof(int), typeof(DateTimeOffset)], [envelopeHarness.Session, 1003, envelopeOpen.AddDays(1)]);
            ReadPushPayload<NotifyItemDataList>(envelopeHarness, nameof(NotifyItemDataList), "Envelope next-day item push");
            EnvelopeEnterResponse secondDay = ReadResponsePayload<EnvelopeEnterResponse>(
                envelopeHarness, 1003, nameof(EnvelopeEnterResponse), "Envelope next-day response");
            AssertEqual(0, secondDay.Code, "Envelope next-day code");
            if (secondDay.RewardGoodsList.Count == 0)
                throw new InvalidDataException("Envelope next-day enter granted no daily ticket reward.");
        }

        // BSON round-trip preserves the durable daily-grant day and echo fields.
        envelopeFresh.Envelope.OpenedCharacterIds = [3, 1, 1];
        envelopeFresh.Envelope.InstrumentBindings = new Dictionary<int, int> { [4] = 2 };
        envelopeFresh.Envelope.AvgWatchedCharacterIds = [7];
        EnvelopeState reloadedEnvelope = BsonSerializer.Deserialize<EnvelopeState>(envelopeFresh.Envelope.ToBson());
        AssertEqual(envelopeFresh.Envelope.LastDailyGrantBusinessDay, reloadedEnvelope.LastDailyGrantBusinessDay,
            "Envelope BSON reload preserves daily-grant business day");
        AssertEqual("1,3", string.Join(",", reloadedEnvelope.OpenedCharacterIds.Distinct().Order()), "Envelope BSON reload opened characters");
        AssertEqual(2, reloadedEnvelope.InstrumentBindings[4], "Envelope BSON reload instrument binding");
        AssertEqual("7", string.Join(",", reloadedEnvelope.AvgWatchedCharacterIds), "Envelope BSON reload avg characters");
    }

    private static void ValidateVersion47PbrCompatibility(
        PBRActivityTable pbr,
        DateTimeOffset pbrOpen,
        Func<string, Type[], object?[], object?> invokeMaybe,
        Func<string, Type[], object?[], object> invoke)
    {
        // Inactive clock: no login root.
        Player pbrEmpty = CreateDrawCompatibilityPlayer(47_201);
        AssertEqual(null, invokeMaybe("BuildPbrNotify", [typeof(Player), typeof(DateTimeOffset)], [pbrEmpty, pbrOpen.AddSeconds(-1)]),
            "PBR inactive clock yields no push");

        // Active empty state: durable default/empty meta progression, stage records, compendiums,
        // and null segment settle exactly matching the retail root.
        PbrActivityDataNotify activeRoot = (PbrActivityDataNotify)invoke("BuildPbrNotify", [typeof(Player), typeof(DateTimeOffset)], [pbrEmpty, pbrOpen]);
        AssertEqual(pbr.Id, activeRoot.PbrDataDb.ActivityId, "PBR active login activity id");
        AssertEqual(null, activeRoot.PbrDataDb.SegmentSettleData, "PBR active login segment settle null");
        AssertEqual(0, activeRoot.PbrDataDb.MetaProgression.UnlockNodes.Count, "PBR active login unlock nodes empty");
        AssertEqual(0, activeRoot.PbrDataDb.StageRecords.Count, "PBR active login stage records empty");
        AssertEqual(0, activeRoot.PbrDataDb.Compendiums.CompendiumItems.Count, "PBR active login compendium items empty");
        AssertEqual(0, activeRoot.PbrDataDb.Compendiums.CompendiumMonsters.Count, "PBR active login compendium monsters empty");

        // Distinct persisted state is reflected through the root.
        Player pbrPopulated = CreateDrawCompatibilityPlayer(47_202);
        pbrPopulated.Pbr.ActivityId = pbr.Id;
        pbrPopulated.Pbr.MetaProgressionUnlockNodes = [5, 3, 3];
        pbrPopulated.Pbr.StageRecords[30063133] = new PbrStageRecordState { StageId = 30063133, HistoryMaxWave = 4, IsPass = true, IsPassWave = true };
        pbrPopulated.Pbr.CompendiumItems[1] = new PbrItemState { ItemId = 1, UnlockTime = 123, GainNum = 2, TriggerNum = 3 };
        pbrPopulated.Pbr.CompendiumMonsters[7] = new PbrMonsterState { MonsterId = 7, DamageTotal = 99, BeKillNum = 1 };
        PbrActivityDataNotify populated = (PbrActivityDataNotify)invoke("BuildPbrNotify", [typeof(Player), typeof(DateTimeOffset)], [pbrPopulated, pbrOpen]);
        AssertEqual("3,5", string.Join(",", populated.PbrDataDb.MetaProgression.UnlockNodes), "PBR populated unlock nodes sorted distinct");
        AssertEqual(true, populated.PbrDataDb.StageRecords[30063133].IsPass, "PBR populated stage record pass");
        AssertEqual(4, populated.PbrDataDb.StageRecords[30063133].HistoryMaxWave, "PBR populated stage record max wave");
        AssertEqual(2, populated.PbrDataDb.Compendiums.CompendiumItems[1].GainNum, "PBR populated compendium item gain");
        AssertEqual(1, populated.PbrDataDb.Compendiums.CompendiumMonsters[7].BeKillNum, "PBR populated compendium monster kills");

        // Compendium push helper maps real mutations to the wire contract.
        PbrCompendiumPush compendiumPush = (PbrCompendiumPush)invoke("BuildCompendiumPush",
            [typeof(IEnumerable<PbrItemState>), typeof(IEnumerable<PbrItemState>), typeof(IEnumerable<PbrMonsterState>), typeof(IEnumerable<PbrMonsterState>)],
            [new[] { new PbrItemState { ItemId = 9, UnlockTime = 1, GainNum = 1, TriggerNum = 0 } }, null, null,
             new[] { new PbrMonsterState { MonsterId = 77, DamageTotal = 5, BeKillNum = 3 } }]);
        AssertEqual(1, compendiumPush.AddCompendiumItems.Count, "PBR compendium push added item count");
        AssertEqual(9, compendiumPush.AddCompendiumItems[0].ItemId, "PBR compendium push added item id");
        AssertEqual(1, compendiumPush.UpdateCompendiumMonsters.Count, "PBR compendium push updated monster count");
        AssertEqual(3, compendiumPush.UpdateCompendiumMonsters[0].BeKillNum, "PBR compendium push updated monster kills");

        // BSON round-trip preserves durable PBR state.
        PbrState reloadedPbr = BsonSerializer.Deserialize<PbrState>(pbrPopulated.Pbr.ToBson());
        AssertEqual("5,3,3", string.Join(",", reloadedPbr.MetaProgressionUnlockNodes), "PBR BSON reload unlock nodes");
        AssertEqual(true, reloadedPbr.StageRecords[30063133].IsPass, "PBR BSON reload stage record");
        AssertEqual(2, reloadedPbr.CompendiumItems[1].GainNum, "PBR BSON reload compendium item");
        AssertEqual(1, reloadedPbr.CompendiumMonsters[7].BeKillNum, "PBR BSON reload compendium monster");
    }

    private static void ValidateVersion47ConcertCompatibility(
        ConcertPreHeatingActivityTable concert,
        DateTimeOffset concertOpen,
        Func<string, Type[], object?[], object?> invokeMaybe)
    {
        Player concertEmpty = CreateDrawCompatibilityPlayer(47_301);
        AssertEqual(null, invokeMaybe("BuildConcertNotify", [typeof(Player), typeof(DateTimeOffset)], [concertEmpty, concertOpen.AddSeconds(-1)]),
            "Concert inactive clock yields no push");
        AssertEqual(null, invokeMaybe("BuildConcertVideoConfigNotify", [typeof(DateTimeOffset)], [concertOpen.AddSeconds(-1)]),
            "Concert video config inactive clock yields no push");

        NotifyConcertPreHeating activeConcert = (NotifyConcertPreHeating)invokeMaybe("BuildConcertNotify", [typeof(Player), typeof(DateTimeOffset)], [concertEmpty, concertOpen])!;
        AssertEqual(concert.Id, activeConcert.ConcertPreHeatingDataDb.ActivityId, "Concert active login activity id");
        AssertEqual(0, activeConcert.ConcertPreHeatingDataDb.StageFinish.Count, "Concert active empty stage finish");

        // Video map is built strictly from the current ConcertVideoConfig table (the captured
        // player URL is never a runtime source).
        NotifyConcertVideoConfig video = (NotifyConcertVideoConfig)invokeMaybe("BuildConcertVideoConfigNotify", [typeof(DateTimeOffset)], [concertOpen])!;
        List<ConcertVideoConfigTable> videoRows = TableReaderV2.Parse<ConcertVideoConfigTable>().ToList();
        AssertEqual(videoRows.Count, video.ConcertVideoConfigs.Count, "Concert video map row count matches table");
        foreach (ConcertVideoConfigTable row in videoRows)
        {
            if (!video.ConcertVideoConfigs.TryGetValue(row.Id, out ConcertVideoConfigEntry? entry) || entry is null)
                throw new InvalidDataException($"Concert video config row {row.Id} missing.");
            AssertEqual(row.LiveUrl, entry.LiveUrl, $"Concert video config row {row.Id} live url from table");
            AssertEqual(row.RecordUrl, entry.RecordUrl, $"Concert video config row {row.Id} record url from table");
            AssertEqual(row.LiveTimeId, entry.LiveTimeId, $"Concert video config row {row.Id} live time id");
            AssertEqual(row.RecordTimeId, entry.RecordTimeId, $"Concert video config row {row.Id} record time id");
        }

        // Distinct persisted completed stages are deduplicated and sorted.
        Player concertDone = CreateDrawCompatibilityPlayer(47_302);
        concertDone.ConcertPreHeating.ActivityId = concert.Id;
        concertDone.ConcertPreHeating.CompletedStageIds = [102, 101, 101];
        NotifyConcertPreHeating done = (NotifyConcertPreHeating)invokeMaybe("BuildConcertNotify", [typeof(Player), typeof(DateTimeOffset)], [concertDone, concertOpen])!;
        AssertEqual("101,102", string.Join(",", done.ConcertPreHeatingDataDb.StageFinish.Select(stage => stage.StageId)),
            "Concert completed stages deduplicated and sorted");

        // BSON round-trip preserves durable completed stages.
        ConcertPreHeatingState reloadedConcert = BsonSerializer.Deserialize<ConcertPreHeatingState>(concertDone.ConcertPreHeating.ToBson());
        AssertEqual("102,101,101", string.Join(",", reloadedConcert.CompletedStageIds), "Concert BSON reload completed stages");
    }

    private static void ValidateVersion47SendLoginPushesOrder(
        DateTimeOffset commonOpen,
        DateTimeOffset commonClosed,
        Func<string, Type[], object?[], object?> invokeMaybe)
    {
        long uid = 47_401;
        using (LoopbackSessionHarness loginHarness = new(
            CreateDrawCompatibilityCharacter(uid),
            CreateDrawCompatibilityPlayer(uid),
            CreateDrawCompatibilityInventory(uid, []),
            "version47-login-push-order-test"))
        {
            // Observed startup order with all three families active: Concert, ConcertVideoConfig,
            // PBR, Envelope.
            invokeMaybe("SendLoginPushes", [typeof(Session), typeof(DateTimeOffset)], [loginHarness.Session, commonOpen]);
            ReadPushPayload<NotifyConcertPreHeating>(loginHarness, nameof(NotifyConcertPreHeating), "login-push concert");
            ReadPushPayload<NotifyConcertVideoConfig>(loginHarness, nameof(NotifyConcertVideoConfig), "login-push concert video");
            ReadPushPayload<PbrActivityDataNotify>(loginHarness, nameof(PbrActivityDataNotify), "login-push pbr");
            ReadPushPayload<NotifyEnvelope>(loginHarness, nameof(NotifyEnvelope), "login-push envelope");

            // Independent clock: when no 4.7 activity is open, no family emits anything.
            invokeMaybe("SendLoginPushes", [typeof(Session), typeof(DateTimeOffset)], [loginHarness.Session, commonClosed]);
            if (loginHarness.TryReadAvailablePacket("login-push closed-clock unexpected push", out _))
                throw new InvalidDataException("SendLoginPushes emitted a push when no 4.7 activity is open.");
        }
    }
}
