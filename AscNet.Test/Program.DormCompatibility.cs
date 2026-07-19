using System.Reflection;
using System.Globalization;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table;
using AscNet.Table.V2.share.dormitory.character;
using AscNet.Table.V2.share.config;
using AscNet.Table.V2.share.dormitory;
using MessagePack;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidateDormCompatibility()
    {
        const long playerId = 46_995;
        DormitoryTable[] freeRooms = TableReaderV2.Parse<DormitoryTable>()
            .Where(row => row.IsFree == 1)
            .OrderBy(row => row.Id)
            .ToArray();
        if (freeRooms.Length == 0)
            throw new InvalidDataException("Dorm compatibility: no free dormitory table row.");
        DormCharacterWorkTable work = TableReaderV2.Parse<DormCharacterWorkTable>()
            .Where(row => row.DormitoryNum <= freeRooms.Length)
            .OrderByDescending(row => row.DormitoryNum)
            .FirstOrDefault()
            ?? throw new InvalidDataException("Dorm compatibility: no unlocked dorm work table row.");
        int timePerVitality = DormConfig("DormWorkTimePerVitality");
        int rewardPerVitality = DormConfig("DormWorkRewardPerVitality");

        Character roster = CreateDrawCompatibilityCharacter(playerId);
        uint firstCharacterId = 1_021_001;
        uint secondCharacterId = 1_021_002;
        roster.Characters = [new CharacterData { Id = firstCharacterId }, new CharacterData { Id = secondCharacterId }];
        Player player = CreateDrawCompatibilityPlayer(playerId);
        Inventory inventory = CreateDrawCompatibilityInventory(playerId, []);
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out RecordingMongoCollectionProxy<Player> playerSaves, out _, out RecordingMongoCollectionProxy<Inventory> inventorySaves);
        using LoopbackSessionHarness harness = new(roster, player, inventory, "dorm-compat");

        Type dormModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.DormModule");
        MethodInfo buildLoginData = RequiredMethod(dormModule, "BuildLoginData", BindingFlags.Static | BindingFlags.Public, [typeof(Session)]);
        NotifyDormitoryData login = (NotifyDormitoryData)buildLoginData.Invoke(null, [harness.Session])!;
        AssertIntegerList(freeRooms.Select(row => (long)row.Id).ToArray(), login.DormitoryList.Select(row => (long)row.DormitoryId).Order().ToArray(),
            "Dorm login free table rooms");
        AssertIntegerList([firstCharacterId, secondCharacterId], login.CharacterList.Select(row => (long)row.CharacterId).Order().ToArray(),
            "Dorm login owned characters");
        AssertEqual(2, player.Dorm.Characters.Count, "Dorm login persists distinct owned characters");
        AssertEqual(true, player.Dorm.Characters.All(row => row.DormitoryId == -1), "Dorm login starts characters unassigned");
        AssertEqual(true, playerSaves.ReplaceOneCalls > 0, "Dorm login initialization persists player");

        InvokeRegisteredRequestHandler(nameof(DormEnterRequest), harness.Session, 46_995_001, new DormEnterRequest());
        DormEnterResponse enter = ReadResponsePayload<DormEnterResponse>(harness, 46_995_001, nameof(DormEnterResponse), "Dorm enter response");
        AssertEqual(0, enter.Code, "Dorm enter code");
        AssertEqual(2, enter.CharacterEvents.Count, "Dorm enter character events");
        _ = ReadPushPayload<NotifyTask>(harness, nameof(NotifyTask), "Dorm enter task progress push");

        PlayerDormCharacter worker = player.Dorm.Characters.Single(row => row.CharacterId == firstCharacterId);
        worker.Vitality = work.Vitality * 2;
        uint beforeWork = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        InvokeRegisteredRequestHandler(nameof(DormWorkRequest), harness.Session, 46_995_002,
            new DormWorkRequest { Works = [new DormWork { CharacterId = firstCharacterId, WorkPos = work.Seat }] });
        NotifyCharacterVitality vitality = ReadPushPayload<NotifyCharacterVitality>(harness,
            nameof(NotifyCharacterVitality), "Dorm work vitality push");
        AssertEqual(firstCharacterId, vitality.CharacterId, "Dorm work vitality character");
        AssertEqual(0, vitality.Vitality, "Dorm work vitality depletion");
        DormWorkResponse workResponse = ReadResponsePayload<DormWorkResponse>(harness, 46_995_002,
            nameof(DormWorkResponse), "Dorm work response");
        AssertEqual(0, workResponse.Code, "Dorm work code");
        PlayerDormWork persistedWork = player.Dorm.WorkList.Single();
        AssertEqual(firstCharacterId, persistedWork.CharacterId, "Dorm work persisted character");
        AssertEqual(work.Seat, persistedWork.WorkPos, "Dorm work persisted position");
        AssertEqual(2, persistedWork.RewardNum, "Dorm work persisted reward units");
        uint expectedEndTime = (uint)(2L * work.Time * timePerVitality);
        uint observedDuration = persistedWork.WorkEndTime - beforeWork;
        AssertEqual(true, observedDuration >= expectedEndTime && observedDuration <= expectedEndTime + 1,
            "Dorm work table/config-derived duration");
        AssertEqual(true, playerSaves.ReplaceOneCalls >= 2, "Dorm work persists player before packets");
        AssertEqual(1, workResponse.WorkList.Count, "Dorm work response work count");
        AssertEqual(persistedWork.WorkEndTime, workResponse.WorkList.Single().WorkEndTime, "Dorm work response state");

        persistedWork.WorkEndTime = (uint)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1);
        InvokeRegisteredRequestHandler(nameof(DormWorkRewardRequest), harness.Session, 46_995_003,
            new DormWorkRewardRequest { PosList = [work.Seat] });
        NotifyItemDataList itemPush = ReadPushPayload<NotifyItemDataList>(harness,
            nameof(NotifyItemDataList), "Dorm work claim item push");
        Item claimedItem = itemPush.ItemDataList.Single();
        AssertEqual(work.ItemId, claimedItem.Id, "Dorm work claim item id");
        AssertEqual(2L * rewardPerVitality, claimedItem.Count, "Dorm work claim item count");
        DormWorkRewardResponse claim = ReadResponsePayload<DormWorkRewardResponse>(harness, 46_995_003,
            nameof(DormWorkRewardResponse), "Dorm work claim response");
        AssertEqual(0, claim.Code, "Dorm work claim code");
        AssertEqual(1, claim.WorkRewards.Count, "Dorm work claim response reward count");
        AssertEqual(work.Seat, claim.WorkRewards.Single().WorkPos, "Dorm work claim response position");
        AssertEqual(work.ItemId, claim.WorkRewards.Single().ItemId, "Dorm work claim response item");
        AssertEqual(2 * rewardPerVitality, claim.WorkRewards.Single().ItemNum, "Dorm work claim response count");
        AssertEqual(1, player.Dorm.WorkList.Count, "Dorm work claim retains persisted work");
        AssertEqual(0U, player.Dorm.WorkList.Single().WorkEndTime, "Dorm work claim marks persisted work claimed");
        AssertEqual(2L * rewardPerVitality, inventory.Items.Single(item => item.Id == work.ItemId).Count,
            "Dorm work claim persists inventory");
        AssertEqual(true, inventorySaves.ReplaceOneCalls > 0, "Dorm work claim persists inventory before packets");

        InvokeRegisteredRequestHandler(nameof(DormWorkRewardRequest), harness.Session, 46_995_004,
            new DormWorkRewardRequest { PosList = [work.Seat] });
        DormWorkRewardResponse replay = ReadResponsePayload<DormWorkRewardResponse>(harness, 46_995_004,
            nameof(DormWorkRewardResponse), "Dorm work claim replay response");
        AssertEqual(20060040, replay.Code, "Dorm work claim replay code");
        AssertEqual(0, replay.WorkRewards.Count, "Dorm work claim replay no rewards");
        AssertEqual(2L * rewardPerVitality, inventory.Items.Single(item => item.Id == work.ItemId).Count,
            "Dorm work claim replay does not increment inventory");

        worker.Vitality = work.Vitality;
        InvokeRegisteredRequestHandler(nameof(DormWorkRequest), harness.Session, 46_995_041,
            new DormWorkRequest { Works = [new DormWork { CharacterId = firstCharacterId, WorkPos = work.Seat }] });
        _ = ReadPushPayload<NotifyCharacterVitality>(harness, nameof(NotifyCharacterVitality), "Dorm work reused seat vitality push");
        DormWorkResponse reusedSeat = ReadResponsePayload<DormWorkResponse>(harness, 46_995_041,
            nameof(DormWorkResponse), "Dorm work reused seat response");
        AssertEqual(0, reusedSeat.Code, "Dorm work claimed seat reusable");
        AssertEqual(1, player.Dorm.WorkList.Count, "Dorm work replaces claimed slot state");
        AssertNoAvailablePacket(harness, "Dorm work reused seat");

        InvokeRegisteredRequestHandler(nameof(DormCharacterFinishAllEventRequest), harness.Session, 46_995_005,
            new DormCharacterFinishAllEventRequest());
        DormCharacterFinishAllEventResponse finish = ReadResponsePayload<DormCharacterFinishAllEventResponse>(harness,
            46_995_005, nameof(DormCharacterFinishAllEventResponse), "Dorm finish-all response");
        AssertEqual(0, finish.Code, "Dorm finish-all empty code");
        AssertEqual(0, finish.MoodChange.Count, "Dorm finish-all empty mood changes");
        AssertEqual(0, finish.RewardGoods.Count, "Dorm finish-all empty rewards");

        PlayerDormCharacter eventCharacter = player.Dorm.Characters.Single(row => row.CharacterId == secondCharacterId);
        eventCharacter.EventList.Add(new PlayerDormEvent { EventId = 1, EndTime = 1 });
        InvokeRegisteredRequestHandler(nameof(DormCharacterFinishAllEventRequest), harness.Session, 46_995_006,
            new DormCharacterFinishAllEventRequest());
        DormCharacterFinishAllEventResponse unavailableFinish = ReadResponsePayload<DormCharacterFinishAllEventResponse>(harness,
            46_995_006, nameof(DormCharacterFinishAllEventResponse), "Dorm finish-all unavailable response");
        AssertEqual(20060040, unavailableFinish.Code, "Dorm finish-all unsupported event code");
        AssertEqual(1, eventCharacter.EventList.Count, "Dorm finish-all unsupported event state unchanged");
    }

    private static int DormConfig(string key)
    {
        ConfigTable config = TableReaderV2.Parse<ConfigTable>().SingleOrDefault(row => row.Key == key)
            ?? throw new InvalidDataException($"Dorm compatibility: missing config {key}.");
        return int.Parse(config.Value, CultureInfo.InvariantCulture);
    }
}
