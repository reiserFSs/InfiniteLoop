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
using AscNet.Table.V2.share.dormitory.furniture;
using AscNet.Table.V2.share.dormitory.quest;
using AscNet.Table.V2.share.task;
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
        DormitoryTable room = freeRooms.FirstOrDefault(row => row.CharCapacity >= 2)
            ?? throw new InvalidDataException("Dorm compatibility: no free room with capacity for two characters.");
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
        Dictionary<string, int> loadedConfig = (Dictionary<string, int>)(dormModule.GetMethod("Config",
            BindingFlags.Static | BindingFlags.NonPublic)?.Invoke(null, null)
            ?? throw new InvalidDataException("Dorm compatibility: Config unavailable."));
        AssertEqual(DormConfig("DormMaxCreateCount"), loadedConfig["DormMaxCreateCount"], "Dorm create cap config");
        AssertEqual(DormConfig("DormMaxRecycleCount"), loadedConfig["DormMaxRecycleCount"], "Dorm recycle cap config");
        AssertEqual(DormConfig("DormMaxTotalFurnitureCount"), loadedConfig["DormMaxTotalFurnitureCount"], "Dorm total furniture cap config");
        HashSet<uint> knownFurniture = TableReaderV2.Parse<FurnitureTable>().Select(row => (uint)row.Id).ToHashSet();
        var freeRoomInitialFurniture = freeRooms.SelectMany(table => table.InitFurniture.Zip(table.InitFurniturePos, (configId, position) => new
        {
            DormitoryId = (uint)table.Id,
            ConfigId = (uint)configId,
            Position = position.Split('|').Select(int.Parse).ToArray()
        }).Where(entry => entry.ConfigId > 0 && knownFurniture.Contains(entry.ConfigId))).ToArray();
        DormitoryTable repairRoom = freeRooms.FirstOrDefault(table => freeRoomInitialFurniture.Any(entry => entry.DormitoryId == (uint)table.Id))
            ?? throw new InvalidDataException("Dorm compatibility: no furnished free dormitory table row.");
        var initialFurniture = freeRoomInitialFurniture.Where(entry => entry.DormitoryId == (uint)repairRoom.Id).ToArray();
        FurnitureTable unrelatedFurniture = TableReaderV2.Parse<FurnitureTable>()
            .FirstOrDefault(row => !initialFurniture.Any(entry => entry.ConfigId == (uint)row.Id))
            ?? throw new InvalidDataException("Dorm compatibility: no unrelated furniture table row.");
        const int legacyFurnitureId = 10_000;
        const int unrelatedFurnitureId = 20_000;
        Player repairPlayer = CreateDrawCompatibilityPlayer(playerId + 1);
        repairPlayer.Dorm.Rooms = [new PlayerDormRoom { Id = (uint)repairRoom.Id, Name = repairRoom.InitName ?? string.Empty }];
        repairPlayer.Dorm.Furniture = initialFurniture.Select((entry, index) => new PlayerDormFurniture
        {
            Id = legacyFurnitureId + index, ConfigId = entry.ConfigId, DormitoryId = 0
        }).Append(new PlayerDormFurniture
        {
            Id = unrelatedFurnitureId, ConfigId = (uint)unrelatedFurniture.Id, DormitoryId = 0
        }).ToList();
        Character repairRoster = CreateDrawCompatibilityCharacter(playerId + 1);
        Inventory repairInventory = CreateDrawCompatibilityInventory(playerId + 1, []);
        using (LoopbackSessionHarness repairHarness = new(repairRoster, repairPlayer, repairInventory, "dorm-repair"))
        {
            _ = buildLoginData.Invoke(null, [repairHarness.Session]);
            AssertIntegerList(freeRooms.Select(table => (long)table.Id).ToArray(),
                repairPlayer.Dorm.Rooms.Select(entry => (long)entry.Id).Order().ToArray(), "Dorm repair grants every free room");
            AssertEqual(freeRoomInitialFurniture.Length + 1, repairPlayer.Dorm.Furniture.Count,
                "Dorm repair preserves legacy furniture while granting missing rooms");
            foreach (var (entry, index) in initialFurniture.Select((entry, index) => (entry, index)))
            {
                PlayerDormFurniture furniture = repairPlayer.Dorm.Furniture.Single(furniture => furniture.Id == legacyFurnitureId + index);
                AssertEqual(entry.ConfigId, furniture.ConfigId, "Dorm repair preserves legacy furniture config");
                AssertEqual((int)entry.DormitoryId, furniture.DormitoryId, "Dorm repair assigns legacy furniture to its room");
                AssertEqual(entry.Position[0], furniture.X, "Dorm repair uses table furniture X");
                AssertEqual(entry.Position[1], furniture.Y, "Dorm repair uses table furniture Y");
                AssertEqual(entry.Position[2], furniture.Angle, "Dorm repair uses table furniture angle");
            }
            AssertEqual(-1, repairPlayer.Dorm.Furniture.Single(entry => entry.Id == unrelatedFurnitureId).DormitoryId,
                "Dorm repair normalizes unrelated legacy furniture");
            var repairedRooms = repairPlayer.Dorm.Rooms.OrderBy(entry => entry.Id).Select(entry => (entry.Id, entry.Name)).ToArray();
            var repairedState = repairPlayer.Dorm.Furniture.OrderBy(entry => entry.Id)
                .Select(entry => (entry.Id, entry.ConfigId, entry.DormitoryId, entry.X, entry.Y, entry.Angle)).ToArray();
            _ = buildLoginData.Invoke(null, [repairHarness.Session]);
            AssertEqual(true, repairedRooms.SequenceEqual(repairPlayer.Dorm.Rooms.OrderBy(entry => entry.Id)
                .Select(entry => (entry.Id, entry.Name))), "Dorm repaired rooms reconnect is idempotent");
            AssertEqual(true, repairedState.SequenceEqual(repairPlayer.Dorm.Furniture.OrderBy(entry => entry.Id)
                .Select(entry => (entry.Id, entry.ConfigId, entry.DormitoryId, entry.X, entry.Y, entry.Angle))),
                "Dorm repaired furniture reconnect is idempotent");
        }
        Player emptyRoomPlayer = CreateDrawCompatibilityPlayer(playerId + 2);
        emptyRoomPlayer.Dorm.Rooms = [new PlayerDormRoom { Id = (uint)repairRoom.Id, Name = repairRoom.InitName ?? string.Empty }];
        emptyRoomPlayer.Dorm.Furniture = [new PlayerDormFurniture { Id = 1, ConfigId = (uint)unrelatedFurniture.Id, DormitoryId = -1 }];
        using (LoopbackSessionHarness emptyRoomHarness = new(CreateDrawCompatibilityCharacter(playerId + 2), emptyRoomPlayer,
            CreateDrawCompatibilityInventory(playerId + 2, []), "dorm-empty"))
        {
            _ = buildLoginData.Invoke(null, [emptyRoomHarness.Session]);
            AssertIntegerList(freeRooms.Select(table => (long)table.Id).ToArray(),
                emptyRoomPlayer.Dorm.Rooms.Select(entry => (long)entry.Id).Order().ToArray(), "Dorm repair grants missing free rooms");
            AssertEqual(0, emptyRoomPlayer.Dorm.Furniture.Count(entry => entry.DormitoryId == repairRoom.Id),
                "Dorm repair preserves intentionally empty room");
            AssertEqual(-1, emptyRoomPlayer.Dorm.Furniture.Single(entry => entry.Id == 1).DormitoryId,
                "Dorm repair preserves warehouse furniture");
        }
        NotifyDormitoryData login = (NotifyDormitoryData)buildLoginData.Invoke(null, [harness.Session])!;
        AssertIntegerList(freeRooms.Select(table => (long)table.Id).ToArray(),
            login.DormitoryList.Select(entry => (long)entry.DormitoryId).Order().ToArray(), "Dorm login grants every free room");
        AssertIntegerList(freeRooms.Select(table => (long)table.Id).ToArray(),
            player.Dorm.Rooms.Select(entry => (long)entry.Id).Order().ToArray(), "Dorm login persists every free room");
        foreach (DormitoryTable freeRoom in freeRooms)
            AssertEqual(freeRoom.InitName ?? string.Empty, player.Dorm.Rooms.Single(entry => entry.Id == (uint)freeRoom.Id).Name,
                "Dorm login uses table room name");
        AssertEqual(freeRoomInitialFurniture.Length, player.Dorm.Furniture.Count, "Dorm login grants all table initial furniture");
        foreach (var entry in freeRoomInitialFurniture)
            AssertEqual(freeRoomInitialFurniture.Count(candidate => candidate.DormitoryId == entry.DormitoryId
                    && candidate.ConfigId == entry.ConfigId && candidate.Position.SequenceEqual(entry.Position)),
                player.Dorm.Furniture.Count(furniture => furniture.DormitoryId == (int)entry.DormitoryId
                    && furniture.ConfigId == entry.ConfigId && furniture.X == entry.Position[0]
                    && furniture.Y == entry.Position[1] && furniture.Angle == entry.Position[2]),
                "Dorm login uses authoritative table furniture");
        InvokeRegisteredRequestHandler(nameof(ActiveDormItemRequest), harness.Session, 46_995_027,
            new ActiveDormItemRequest { DormitoryId = room.Id });
        ActiveDormItemResponse activeDorm = ReadResponsePayload<ActiveDormItemResponse>(
            harness, 46_995_027, nameof(ActiveDormItemResponse), "Dorm duplicate activation response");
        AssertEqual(true, activeDorm.Code != 0, "Dorm duplicate activation rejects owned free room");
        long currentDay = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 86_400;
        DateTimeOffset dailyTaskNow = DateTimeOffset.UtcNow;
        HashSet<int> dailyConditionIds = TableReaderV2.Parse<ConditionTable>()
            .Where(condition => condition.Type is >= 29000 and < 29100)
            .Select(condition => condition.Id)
            .ToHashSet();
        TaskTable dailyTask = TableReaderV2.Parse<TaskTable>()
            .Where(task => task.Type == 13 && task.ShowAfterTaskId is not > 0
                && dailyConditionIds.Contains(task.Condition) && task.RewardId is > 0
                && (string.IsNullOrWhiteSpace(task.StartTime) || DateTimeOffset.TryParseExact(task.StartTime, "yyyy/M/d H:mm",
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset start) && dailyTaskNow >= start)
                && (string.IsNullOrWhiteSpace(task.EndTime) || DateTimeOffset.TryParseExact(task.EndTime, "yyyy/M/d H:mm",
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset end) && dailyTaskNow < end))
            .OrderByDescending(task => task.Id)
            .First();
        string legacyDailyClaimKey = $"dorm-task:{dailyTask.Id}";
        player.MissionProgress.DailyResetDay = currentDay - 1;
        player.MissionProgress.ClaimedTaskIds.Add(dailyTask.Id);
        player.MissionProgress.ConditionCounters[dailyTask.Condition] = dailyTask.Result ?? 1;
        inventory.AppliedRewardClaims.Add(legacyDailyClaimKey);
        roster.AppliedRewardClaims.Add(legacyDailyClaimKey);
        RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.TaskModule"),
            "EnsureMissionResets", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Session)])
            .Invoke(null, [harness.Session]);
        AssertEqual(false, player.MissionProgress.ClaimedTaskIds.Contains(dailyTask.Id), "Dorm daily reset clears claimed task");
        AssertEqual(false, player.MissionProgress.ConditionCounters.ContainsKey(dailyTask.Condition), "Dorm daily reset clears task progress");
        AssertEqual(false, inventory.AppliedRewardClaims.Contains(legacyDailyClaimKey, StringComparer.Ordinal)
            || roster.AppliedRewardClaims.Contains(legacyDailyClaimKey, StringComparer.Ordinal),
            "Dorm daily reset prunes legacy reward receipt");
        player.MissionProgress.ConditionCounters[dailyTask.Condition] = dailyTask.Result ?? 1;
        InvokeRegisteredRequestHandler(nameof(FinishMultiTaskRequest), harness.Session, 46_995_029,
            new FinishMultiTaskRequest { TaskIds = [dailyTask.Id] });
        FinishMultiTaskResponse dailyClaimAll = (FinishMultiTaskResponse)ReadResponsePayload(harness, 46_995_029,
            nameof(FinishMultiTaskResponse), "Dorm daily claim-all response", typeof(FinishMultiTaskResponse), maxPacketsToRead: 8);
        AssertEqual(0, dailyClaimAll.Code, "Dorm daily claim-all code");
        AssertIntegerList([dailyTask.Id], dailyClaimAll.SuccessTaskIds.Select(id => (long)id).ToArray(),
            "Dorm daily claim-all success ids");
        AssertEqual(0, dailyClaimAll.NotDealTaskIds.Count, "Dorm daily claim-all rejected ids");
        AssertEqual(true, dailyClaimAll.RewardGoodsList.Count > 0, "Dorm daily claim-all rewards");
        AssertEqual(true, inventory.AppliedRewardClaims.Contains($"dorm-task:{dailyTask.Id}:{currentDay}", StringComparer.Ordinal),
            "Dorm daily claim uses reset-scoped receipt");
        AssertIntegerList([firstCharacterId, secondCharacterId], login.CharacterList.Select(row => (long)row.CharacterId).Order().ToArray(),
            "Dorm login owned characters");
        AssertEqual(2, player.Dorm.Characters.Count, "Dorm login persists distinct owned characters");
        AssertEqual(true, player.Dorm.Characters.All(row => row.DormitoryId == -1), "Dorm login starts characters unassigned");
        AssertEqual(true, playerSaves.ReplaceOneCalls > 0, "Dorm login initialization persists player");
        AssertEqual(true, login.DormQuestData.TotalQuest.Count == player.Dorm.Quest.TotalQuest.Count && player.Dorm.Quest.TotalQuest.Count > 0,
            "Dorm login includes normalized quest board");
        PlayerDormQuest questBoard = player.Dorm.Quest.TotalQuest.First(board =>
            TableReaderV2.Parse<QuestTable>().Single(row => row.Id == board.QuestId).MemberCount <= roster.Characters.Count);
        QuestTable questTable = TableReaderV2.Parse<QuestTable>().Single(row => row.Id == questBoard.QuestId);
        List<uint> questTeam = roster.Characters.Take(questTable.MemberCount).Select(row => row.Id).ToList();
        ConditionTable questCondition = TableReaderV2.Parse<ConditionTable>().Single(row => row.Type == 29018);
        DateTimeOffset questNow = DateTimeOffset.UtcNow;
        TaskTable[] activeQuestTasks = TableReaderV2.Parse<TaskTable>()
            .Where(task => task.Condition == questCondition.Id
                && (string.IsNullOrWhiteSpace(task.StartTime) || DateTimeOffset.TryParseExact(task.StartTime, "yyyy/M/d H:mm",
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset start) && questNow >= start)
                && (string.IsNullOrWhiteSpace(task.EndTime) || DateTimeOffset.TryParseExact(task.EndTime, "yyyy/M/d H:mm",
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset end) && questNow < end))
            .ToArray();
        AssertEqual(1, activeQuestTasks.Length, "Dorm quest active task table row");
        InvokeRegisteredRequestHandler(nameof(QuestAcceptRequest), harness.Session, 46_995_028,
            new QuestAcceptRequest { QuestAcceptParams = [new QuestAcceptParam { Index = questBoard.Index, TeamCharacter = questTeam }] });
        NotifyTask questTask = ReadPushPayload<NotifyTask>(harness, nameof(NotifyTask), "Dorm quest accept task push");
        AssertEqual(1, questTask.Tasks.Tasks.Count, "Dorm quest accept task count");
        AssertEqual((uint)activeQuestTasks.Single().Id, questTask.Tasks.Tasks.Single().Id, "Dorm quest task row is active");
        AssertEqual(true, questTask.Tasks.Tasks.Single().Schedule.Any(schedule => schedule.Id == questCondition.Id && schedule.Value == 1),
            "Dorm quest accept task schedule");
        QuestAcceptResponse questAccept = ReadResponsePayload<QuestAcceptResponse>(harness, 46_995_028, nameof(QuestAcceptResponse), "Dorm quest accept response");
        AssertEqual(0, questAccept.Code, "Dorm quest accept code");
        AssertEqual(questBoard.Index, player.Dorm.Quest.QuestAccept.Single().Index, "Dorm quest accept persists board index");
        InvokeRegisteredRequestHandler(nameof(QuestAcceptRequest), harness.Session, 46_995_029,
            new QuestAcceptRequest { QuestAcceptParams = [new QuestAcceptParam { Index = questBoard.Index, TeamCharacter = questTeam }] });
        AssertEqual(20060069, ReadResponsePayload<QuestAcceptResponse>(harness, 46_995_029, nameof(QuestAcceptResponse), "Dorm quest replay response").Code,
            "Dorm quest accept replay code");
        AssertNoAvailablePacket(harness, "Dorm quest replay");
        InvokeRegisteredRequestHandler(nameof(QuestRecallTeamRequest), harness.Session, 46_995_030,
            new QuestRecallTeamRequest { Index = questBoard.Index, ResetCount = player.Dorm.Quest.ResetCount });
        _ = ReadPushPayload<NotifyDormQuestData>(harness, nameof(NotifyDormQuestData), "Dorm quest recall push");
        AssertEqual(0, ReadResponsePayload<QuestRecallTeamResponse>(harness, 46_995_030, nameof(QuestRecallTeamResponse), "Dorm quest recall response").Code,
            "Dorm quest recall code");
        InvokeRegisteredRequestHandler(nameof(QuestGetAllRewardRequest), harness.Session, 46_995_031, new QuestGetAllRewardRequest());
        AssertEqual(20060077, ReadResponsePayload<QuestGetAllRewardResponse>(harness, 46_995_031, nameof(QuestGetAllRewardResponse), "Dorm empty quest reward response").Code,
            "Dorm quest empty reward replay code");
        InvokeRegisteredRequestHandler(nameof(QuestAcceptRequest), harness.Session, 46_995_033,
            new QuestAcceptRequest { QuestAcceptParams = [new QuestAcceptParam { Index = questBoard.Index, TeamCharacter = questTeam }] });
        _ = ReadPushPayload<NotifyTask>(harness, nameof(NotifyTask), "Dorm quest retry accept task push");
        AssertEqual(0, ReadResponsePayload<QuestAcceptResponse>(harness, 46_995_033, nameof(QuestAcceptResponse), "Dorm quest retry accept response").Code,
            "Dorm quest retry accept code");
        player.Dorm.Quest.QuestAccept.Single(accept => accept.Index == questBoard.Index && accept.ResetCount == questBoard.ResetCount).AcceptTime = 0;
        int inventorySavesBeforeQuestRetry = inventorySaves.ReplaceOneCalls;
        playerSaves.ThrowOnReplaceOne = true;
        InvokeRegisteredRequestHandler(nameof(QuestGetAllRewardRequest), harness.Session, 46_995_034, new QuestGetAllRewardRequest());
        playerSaves.ThrowOnReplaceOne = false;
        AssertEqual(true, ReadResponsePayload<QuestGetAllRewardResponse>(harness, 46_995_034, nameof(QuestGetAllRewardResponse), "Dorm quest failed reward response").Code != 0,
            "Dorm quest player-save failure rejects reward");
        AssertEqual(inventorySavesBeforeQuestRetry, inventorySaves.ReplaceOneCalls, "Dorm quest failed player save grants nothing");
        PlayerDormQuestAccept failedQuestReward = player.Dorm.Quest.QuestAccept.Single(accept => accept.Index == questBoard.Index && accept.ResetCount == questBoard.ResetCount);
        AssertEqual(false, failedQuestReward.IsAward, "Dorm quest failed reward leaves accept pending");
        AssertEqual(0, player.Dorm.PendingRewards.Count(pending => pending.Key.StartsWith($"dorm-quest:{playerId}:", StringComparison.Ordinal)),
            "Dorm quest failed reward leaves no durable pending grant");
        InvokeRegisteredRequestHandler(nameof(QuestGetAllRewardRequest), harness.Session, 46_995_035, new QuestGetAllRewardRequest());
        _ = ReadPushPayload<NotifyItemDataList>(harness, nameof(NotifyItemDataList), "Dorm quest retry reward push");
        AssertEqual(0, ReadResponsePayload<QuestGetAllRewardResponse>(harness, 46_995_035, nameof(QuestGetAllRewardResponse), "Dorm quest retry reward response").Code,
            "Dorm quest reward retry code");
        AssertEqual(inventorySavesBeforeQuestRetry + 1, inventorySaves.ReplaceOneCalls, "Dorm quest reward retry grants once");
        PlayerDormQuestAccept awardedQuest = player.Dorm.Quest.QuestAccept.Single(accept => accept.Index == questBoard.Index && accept.ResetCount == questBoard.ResetCount);
        AssertEqual(true, awardedQuest.IsAward, "Dorm quest successful reward marks accept awarded");
        AssertEqual(0, player.Dorm.PendingRewards.Count(pending => pending.Key.StartsWith($"dorm-quest:{playerId}:", StringComparison.Ordinal)),
            "Dorm quest successful reward clears pending grant");
        int completedReset = player.Dorm.Quest.ResetCount;
        foreach (PlayerDormQuest board in player.Dorm.Quest.TotalQuest.Where(board =>
            !player.Dorm.Quest.QuestAccept.Any(accept => accept.Index == board.Index && accept.ResetCount == board.ResetCount)))
            player.Dorm.Quest.QuestAccept.Add(new PlayerDormQuestAccept
            {
                QuestId = board.QuestId, Index = board.Index, ResetCount = board.ResetCount, IsSpecialQuest = board.IsSpecialQuest, IsAward = true
            });
        InvokeRegisteredRequestHandler(nameof(QuestAcceptRequest), harness.Session, 46_995_036, new QuestAcceptRequest());
        AssertEqual(20060072, ReadResponsePayload<QuestAcceptResponse>(harness, 46_995_036, nameof(QuestAcceptResponse), "Dorm quest board reset trigger response").Code,
            "Dorm quest exhausted board trigger code");
        AssertEqual(completedReset + 1, player.Dorm.Quest.ResetCount, "Dorm quest exhausted board advances reset");
        AssertEqual(true, player.Dorm.Quest.QuestAccept.Any(accept => accept.ResetCount == completedReset && accept.IsAward),
            "Dorm quest reset preserves awarded acceptance history");
        PlayerDormQuest nextQuestBoard = player.Dorm.Quest.TotalQuest.First(board =>
            TableReaderV2.Parse<QuestTable>().Single(row => row.Id == board.QuestId).MemberCount <= roster.Characters.Count);
        QuestTable nextQuestTable = TableReaderV2.Parse<QuestTable>().Single(row => row.Id == nextQuestBoard.QuestId);
        InvokeRegisteredRequestHandler(nameof(QuestAcceptRequest), harness.Session, 46_995_037,
            new QuestAcceptRequest
            {
                QuestAcceptParams = [new QuestAcceptParam
                {
                    Index = nextQuestBoard.Index,
                    TeamCharacter = roster.Characters.Take(nextQuestTable.MemberCount).Select(character => character.Id).ToList()
                }]
            });
        _ = ReadPushPayload<NotifyTask>(harness, nameof(NotifyTask), "Dorm reset quest accept task push");
        AssertEqual(0, ReadResponsePayload<QuestAcceptResponse>(harness, 46_995_037, nameof(QuestAcceptResponse), "Dorm reset quest accept response").Code,
            "Dorm quest accepts from replacement board");
        InvokeRegisteredRequestHandler(nameof(QuestReadFileRequest), harness.Session, 46_995_032, new QuestReadFileRequest { FileId = -1 });
        AssertEqual(20060079, ReadResponsePayload<QuestReadFileResponse>(harness, 46_995_032, nameof(QuestReadFileResponse), "Dorm invalid quest file response").Code,
            "Dorm quest invalid file code");
        InvokeRegisteredRequestHandler(nameof(DormitoryListRequest), harness.Session, 46_995_010, new DormitoryListRequest());
        DormitoryListResponse roomList = ReadResponsePayload<DormitoryListResponse>(harness, 46_995_010,
            nameof(DormitoryListResponse), "Dorm room list response");
        AssertIntegerList(freeRooms.Select(row => (long)row.Id).ToArray(),
            roomList.DormitoryList.Select(row => (long)row.DormitoryId).Order().ToArray(), "Dorm room list state");
        FurnitureTable layoutFurniture = TableReaderV2.Parse<FurnitureTable>().FirstOrDefault(row => row.Id > 0)
            ?? throw new InvalidDataException("Dorm compatibility: no furniture table row.");
        DormLayoutFurnitureData layoutFurnitureData = new() { ConfigId = (uint)layoutFurniture.Id, X = 0, Y = 0, Angle = 0 };
        InvokeRegisteredRequestHandler(nameof(DormSnapshotLayoutRequest), harness.Session, 46_995_015,
            new DormSnapshotLayoutRequest { FurnitureList = [layoutFurnitureData] });
        DormSnapshotLayoutResponse snapshot = ReadResponsePayload<DormSnapshotLayoutResponse>(harness, 46_995_015,
            nameof(DormSnapshotLayoutResponse), "Dorm snapshot layout response");
        AssertEqual(0, snapshot.Code, "Dorm snapshot layout code");
        AssertEqual(true, snapshot.ShareId.Length > 0 && player.Dorm.Shares.Single().ShareId == snapshot.ShareId,
            "Dorm snapshot layout persists share");
        InvokeRegisteredRequestHandler(nameof(DormCollectLayoutRequest), harness.Session, 46_995_016,
            new DormCollectLayoutRequest { LayoutName = "compat", FurnitureList = [layoutFurnitureData] });
        DormCollectLayoutResponse collect = ReadResponsePayload<DormCollectLayoutResponse>(harness, 46_995_016,
            nameof(DormCollectLayoutResponse), "Dorm collect layout response");
        AssertEqual(0, collect.Code, "Dorm collect layout code");
        AssertEqual(true, collect.NewLayoutId > 0 && player.Dorm.Layouts.Single().LayoutId == collect.NewLayoutId,
            "Dorm collect layout persists table-derived furniture");
        InvokeRegisteredRequestHandler(nameof(DormBindLayoutRequest), harness.Session, 46_995_017,
            new DormBindLayoutRequest { DormitoryId = room.Id, LayoutId = collect.NewLayoutId });
        AssertEqual(0, ReadResponsePayload<DormBindLayoutResponse>(harness, 46_995_017, nameof(DormBindLayoutResponse), "Dorm bind layout response").Code,
            "Dorm bind layout code");
        AssertEqual(collect.NewLayoutId, player.Dorm.BindRelations.Single().LayoutId, "Dorm bind layout persists relation");
        InvokeRegisteredRequestHandler(nameof(DormUnBindLayoutRequest), harness.Session, 46_995_018,
            new DormUnBindLayoutRequest { DormitoryId = room.Id });
        AssertEqual(0, ReadResponsePayload<DormUnBindLayoutResponse>(harness, 46_995_018, nameof(DormUnBindLayoutResponse), "Dorm unbind layout response").Code,
            "Dorm unbind layout code");
        AssertEqual(0, player.Dorm.BindRelations.Count, "Dorm unbind layout removes relation");
        InvokeRegisteredRequestHandler(nameof(DormVisitRequest), harness.Session, 46_995_019,
            new DormVisitRequest { TargetId = playerId, DormitoryId = room.Id, CharacterId = firstCharacterId });
        playerSaves.FindResults = [player];
        DormVisitResponse visit = ReadResponsePayload<DormVisitResponse>(harness, 46_995_019, nameof(DormVisitResponse), "Dorm self visit response");
        AssertEqual(0, visit.Code, "Dorm self visit code");
        AssertEqual(playerId, player.Dorm.Visits.Single().PlayerId, "Dorm visit persists record");
        InvokeRegisteredRequestHandler(nameof(DormRecommendRequest), harness.Session, 46_995_020, new DormRecommendRequest());
        DormRecommendResponse recommend = ReadResponsePayload<DormRecommendResponse>(harness, 46_995_020, nameof(DormRecommendResponse), "Dorm recommend response");
        AssertEqual(0, recommend.Code, "Dorm recommend code");
        AssertEqual(true, player.Dorm.LastRecommendTime > 0 && !recommend.PlayerIds.Contains(playerId), "Dorm recommend persists and excludes self");
        List<(FurnitureCreateAttrTable Recipe, FurnitureTypeTable Type)> recipes = TableReaderV2.Parse<FurnitureCreateAttrTable>()
            .Where(row => row.MinConsume > 0)
            .Join(TableReaderV2.Parse<FurnitureTypeTable>(), recipe => recipe.FurnitureType, type => type.Id, (recipe, type) => (recipe, type))
            .Where(row => TableReaderV2.Parse<FurnitureTable>().Any(furniture => furniture.TypeId == row.recipe.FurnitureType && furniture.GainType == 1))
            .GroupBy(row => row.type.MinorType)
            .Select(group => (group.First().recipe, group.First().type))
            .Take(2)
            .ToList();
        if (recipes.Count != 2) throw new InvalidDataException("Dorm compatibility: no two creatable furniture minor types.");
        (FurnitureCreateAttrTable recipe, FurnitureTypeTable recipeType) = recipes[0];
        (FurnitureCreateAttrTable secondRecipe, FurnitureTypeTable secondRecipeType) = recipes[1];
        inventory.Do(Inventory.FurnitureCoin, 3 * recipe.MinConsume + secondRecipe.MinConsume);
        ConditionTable[] firstCreateConditions = TableReaderV2.Parse<ConditionTable>()
            .Where(condition => condition.Type == 29017 || condition.Type == 29008
                && (condition.Params.Count < 2 || condition.Params[1] == recipeType.MinorType))
            .ToArray();
        InvokeRegisteredRequestHandler(nameof(CreateFurnitureRequest), harness.Session, 46_995_024,
            new CreateFurnitureRequest { TypeIds = [recipe.FurnitureType], Count = 3, CostA = recipe.MinConsume });
        NotifyTask firstCreateTask = ReadPushPayload<NotifyTask>(harness, nameof(NotifyTask), "Dorm furniture create task push");
        AssertEqual(true, firstCreateConditions.All(condition => firstCreateTask.Tasks.Tasks.Any(task =>
            task.Schedule.Any(schedule => schedule.Id == condition.Id && schedule.Value >= 1))), "Dorm first furniture task schedules");
        _ = ReadPushPayload<NotifyItemDataList>(harness, nameof(NotifyItemDataList), "Dorm furniture create item push");
        CreateFurnitureResponse create = ReadResponsePayload<CreateFurnitureResponse>(harness, 46_995_024, nameof(CreateFurnitureResponse), "Dorm furniture create response");
        AssertEqual(0, create.Code, "Dorm furniture create code");
        AssertEqual(3 * recipe.MinConsume, create.Count, "Dorm furniture create table-derived cost");
        DormFurnitureData createdFurniture = create.FurnitureList.First();
        AssertEqual(true, player.Dorm.Furniture.Any(row => row.Id == createdFurniture.Id && row.ConfigId == createdFurniture.ConfigId),
            "Dorm furniture create persists generated furniture");
        TaskTable createClaimTask = TableReaderV2.Parse<TaskTable>().Single(task =>
            task.Type != 12
            && task.Suffix == "Dormitory"
            && task.ShowAfterTaskId is not > 0
            && firstCreateTask.Tasks.Tasks.Any(progress => progress.Id == task.Id)
            && player.MissionProgress.ConditionCounters.GetValueOrDefault(task.Condition) >= task.Result);
        InvokeRegisteredRequestHandler(nameof(FinishTaskRequest), harness.Session, 46_995_035,
            new FinishTaskRequest { TaskId = createClaimTask.Id });
        FinishTaskResponse createClaim = (FinishTaskResponse)ReadResponsePayload(harness, 46_995_035,
            nameof(FinishTaskResponse), "Dorm created furniture task claim response", typeof(FinishTaskResponse), maxPacketsToRead: 8);
        AssertEqual(0, createClaim.Code, "Dorm created furniture task claim code");
        AssertEqual(true, createClaim.RewardGoodsList.Count > 0 && player.MissionProgress.ClaimedTaskIds.Contains(createClaimTask.Id),
            "Dorm created furniture task claim persists reward");
        InvokeRegisteredRequestHandler(nameof(FinishTaskRequest), harness.Session, 46_995_036,
            new FinishTaskRequest { TaskId = createClaimTask.Id });
        FinishTaskResponse createClaimReplay = (FinishTaskResponse)ReadResponsePayload(harness, 46_995_036,
            nameof(FinishTaskResponse), "Dorm created furniture task replay response", typeof(FinishTaskResponse), maxPacketsToRead: 8);
        AssertEqual(20026006, createClaimReplay.Code, "Dorm created furniture task replay code");
        AssertEqual(0, createClaimReplay.RewardGoodsList.Count, "Dorm created furniture task replay has no reward");
        ConditionTable[] secondCreateConditions = TableReaderV2.Parse<ConditionTable>()
            .Where(condition => condition.Type == 29008 && condition.Params.Count > 1 && condition.Params[1] == secondRecipeType.MinorType)
            .ToArray();
        InvokeRegisteredRequestHandler(nameof(CreateFurnitureRequest), harness.Session, 46_995_033,
            new CreateFurnitureRequest { TypeIds = [secondRecipe.FurnitureType], Count = 1, CostA = secondRecipe.MinConsume });
        NotifyTask secondCreateTask = ReadPushPayload<NotifyTask>(harness, nameof(NotifyTask), "Dorm second furniture create task push");
        AssertEqual(true, secondCreateConditions.All(condition => secondCreateTask.Tasks.Tasks.Any(task =>
            task.Schedule.Any(schedule => schedule.Id == condition.Id && schedule.Value == 1))), "Dorm second furniture minor task schedules");
        _ = ReadPushPayload<NotifyItemDataList>(harness, nameof(NotifyItemDataList), "Dorm second furniture create item push");
        _ = ReadResponsePayload<CreateFurnitureResponse>(harness, 46_995_033, nameof(CreateFurnitureResponse), "Dorm second furniture create response");
        AssertEqual(true, recipeType.MinorType != secondRecipeType.MinorType
            && firstCreateConditions.Where(condition => condition.Type == 29008 && condition.Params.Count > 1)
                .All(condition => player.MissionProgress.ConditionCounters.GetValueOrDefault(condition.Id) == 3)
            && secondCreateConditions.All(condition => player.MissionProgress.ConditionCounters.GetValueOrDefault(condition.Id) == 1),
            "Dorm distinct furniture minor progress");
        InvokeRegisteredRequestHandler(nameof(SetFurnitureOptLockRequest), harness.Session, 46_995_025,
            new SetFurnitureOptLockRequest { FurnitureId = createdFurniture.Id, IsLocked = true });
        AssertEqual(0, ReadResponsePayload<SetFurnitureOptLockResponse>(harness, 46_995_025, nameof(SetFurnitureOptLockResponse), "Dorm furniture lock response").Code,
            "Dorm furniture lock code");
        AssertEqual(true, player.Dorm.Furniture.Single(row => row.Id == createdFurniture.Id).IsLocked, "Dorm furniture lock persists");
        InvokeRegisteredRequestHandler(nameof(DecomposeFurnitureRequest), harness.Session, 46_995_026,
            new DecomposeFurnitureRequest { FurnitureIds = [createdFurniture.Id] });
        AssertEqual(20060040, ReadResponsePayload<DecomposeFurnitureResponse>(harness, 46_995_026, nameof(DecomposeFurnitureResponse), "Dorm locked furniture decompose response").Code,
            "Dorm locked furniture cannot decompose");
        AssertNoAvailablePacket(harness, "Dorm locked furniture decompose");
        InvokeRegisteredRequestHandler(nameof(SetFurnitureOptLockRequest), harness.Session, 46_995_027,
            new SetFurnitureOptLockRequest { FurnitureId = createdFurniture.Id, IsLocked = false });
        _ = ReadResponsePayload<SetFurnitureOptLockResponse>(harness, 46_995_027, nameof(SetFurnitureOptLockResponse), "Dorm furniture unlock response");
        ConditionTable[] decomposeConditions = TableReaderV2.Parse<ConditionTable>().Where(condition => condition.Type == 29015).ToArray();
        string decomposeInventoryBeforeFailure = Convert.ToHexString(MessagePackSerializer.Serialize(inventory.Items));
        string decomposeClaimKey = $"dorm-decompose:{playerId}:{createdFurniture.Id}";
        playerSaves.ThrowOnReplaceOne = true;
        InvokeRegisteredRequestHandler(nameof(DecomposeFurnitureRequest), harness.Session, 46_995_037,
            new DecomposeFurnitureRequest { FurnitureIds = [createdFurniture.Id] });
        playerSaves.ThrowOnReplaceOne = false;
        AssertEqual(20060040, ReadResponsePayload<DecomposeFurnitureResponse>(harness, 46_995_037,
            nameof(DecomposeFurnitureResponse), "Dorm failed furniture decompose response").Code,
            "Dorm player-save failure rejects decompose");
        AssertEqual(false, player.Dorm.Furniture.Any(row => row.Id == createdFurniture.Id),
            "Dorm player-save failure leaves source furniture unusable");
        AssertEqual(true, player.Dorm.PendingRewards.Any(entry => entry.Key == decomposeClaimKey),
            "Dorm player-save failure retains pending decomposition");
        AssertEqual(decomposeInventoryBeforeFailure, Convert.ToHexString(MessagePackSerializer.Serialize(inventory.Items)),
            "Dorm player-save failure rolls back durable proceeds");
        AssertEqual(false, inventory.AppliedRewardClaims.Contains(decomposeClaimKey, StringComparer.Ordinal),
            "Dorm player-save failure rolls back reward receipt");
        AssertNoAvailablePacket(harness, "Dorm failed furniture decompose");
        InvokeRegisteredRequestHandler(nameof(DecomposeFurnitureRequest), harness.Session, 46_995_034,
            new DecomposeFurnitureRequest { FurnitureIds = [createdFurniture.Id] });
        NotifyTask decomposeTask = ReadPushPayload<NotifyTask>(harness, nameof(NotifyTask), "Dorm furniture decompose task push");
        AssertEqual(true, decomposeConditions.All(condition => decomposeTask.Tasks.Tasks.Any(task =>
            task.Schedule.Any(schedule => schedule.Id == condition.Id && schedule.Value == 1))), "Dorm decompose task schedules");
        _ = ReadPushPayload<NotifyItemDataList>(harness, nameof(NotifyItemDataList), "Dorm furniture decompose item push");
        AssertEqual(0, ReadResponsePayload<DecomposeFurnitureResponse>(harness, 46_995_034, nameof(DecomposeFurnitureResponse), "Dorm furniture decompose response").Code,
            "Dorm furniture decompose code");
        AssertEqual(false, player.Dorm.PendingRewards.Any(entry => entry.Key == decomposeClaimKey),
            "Dorm decomposition retry clears pending operation");
        AssertEqual(true, inventory.AppliedRewardClaims.Contains(decomposeClaimKey, StringComparer.Ordinal),
            "Dorm decomposition retry grants once");

        InvokeRegisteredRequestHandler(nameof(DormPutCharacterRequest), harness.Session, 46_995_011,
            new DormPutCharacterRequest { DormitoryId = room.Id, CharacterIds = [firstCharacterId, secondCharacterId] });
        NotifyDormCharacterRecovery putRecovery = ReadPushPayload<NotifyDormCharacterRecovery>(harness,
            nameof(NotifyDormCharacterRecovery), "Dorm put character recovery push");
        AssertEqual(2, putRecovery.ChangeType, "Dorm put character recovery type");
        AssertIntegerList([firstCharacterId, secondCharacterId],
            putRecovery.Recoveries.Select(row => (long)row.CharacterId).Order().ToArray(), "Dorm put character recovery roster");
        DormPutCharacterResponse put = ReadResponsePayload<DormPutCharacterResponse>(harness, 46_995_011,
            nameof(DormPutCharacterResponse), "Dorm put character response");
        AssertEqual(0, put.Code, "Dorm put character code");
        AssertIntegerList([firstCharacterId, secondCharacterId],
            put.SuccessIds.Select(id => (long)id).ToArray(), "Dorm put character success ids");
        AssertEqual(true, player.Dorm.Characters.All(character => character.DormitoryId == room.Id),
            "Dorm put character persisted assignments");
        PlayerDormFurniture lockedFurniture = player.Dorm.Furniture.FirstOrDefault()
            ?? throw new InvalidDataException("Dorm compatibility: no owned furniture to place.");
        InvokeRegisteredRequestHandler(nameof(SetFurnitureOptLockRequest), harness.Session, 46_995_035,
            new SetFurnitureOptLockRequest { FurnitureId = lockedFurniture.Id, IsLocked = true });
        AssertEqual(0, ReadResponsePayload<SetFurnitureOptLockResponse>(harness, 46_995_035,
            nameof(SetFurnitureOptLockResponse), "Dorm placement furniture lock response").Code, "Dorm placement furniture lock code");
        PlayerDormCharacter recoveryCharacter = player.Dorm.Characters.Single(character => character.CharacterId == firstCharacterId);
        int originalMood = recoveryCharacter.Mood, originalVitality = recoveryCharacter.Vitality;
        recoveryCharacter.Mood = 5_000; recoveryCharacter.Vitality = 5_000;
        uint recoveryAnchor = checked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        recoveryCharacter.LastRecoveryTime = recoveryAnchor - 59 * 60;
        int savesBeforePlacement = playerSaves.ReplaceOneCalls;
        InvokeRegisteredRequestHandler(nameof(PutFurnitureRequest), harness.Session, 46_995_036,
            new PutFurnitureRequest { DormitoryId = room.Id, FurnitureList = [new PutFurnitureData { Id = lockedFurniture.Id, X = 0, Y = 0, Angle = 0 }] });
        _ = ReadPushPayload<NotifyDormCharacterRecovery>(harness, nameof(NotifyDormCharacterRecovery), "Dorm locked furniture placement recovery push");
        AssertEqual(0, ReadResponsePayload<PutFurnitureResponse>(harness, 46_995_036,
            nameof(PutFurnitureResponse), "Dorm locked furniture placement response").Code, "Dorm locked furniture placement code");
        AssertEqual(true, lockedFurniture.IsLocked && lockedFurniture.DormitoryId == room.Id && playerSaves.ReplaceOneCalls > savesBeforePlacement,
            "Dorm locked furniture placement persists");
        AssertEqual(true, recoveryCharacter.Mood == 5_000 && recoveryCharacter.Vitality == 5_000
            && recoveryCharacter.LastRecoveryTime >= recoveryAnchor, "Dorm partial recovery anchors before new furniture rate");
        recoveryCharacter.Mood = originalMood; recoveryCharacter.Vitality = originalVitality;

        InvokeRegisteredRequestHandler(nameof(DormPutCharacterRequest), harness.Session, 46_995_012,
            new DormPutCharacterRequest { DormitoryId = room.Id, CharacterIds = [9_999_999] });
        DormPutCharacterResponse invalidPut = ReadResponsePayload<DormPutCharacterResponse>(harness, 46_995_012,
            nameof(DormPutCharacterResponse), "Dorm invalid put character response");
        AssertEqual(20060040, invalidPut.Code, "Dorm invalid put character code");
        AssertEqual(0, invalidPut.SuccessIds.Count, "Dorm invalid put character successes");
        AssertNoAvailablePacket(harness, "Dorm invalid put character");

        InvokeRegisteredRequestHandler(nameof(DormRemoveCharacterRequest), harness.Session, 46_995_013,
            new DormRemoveCharacterRequest { CharacterIds = [secondCharacterId] });
        _ = ReadPushPayload<NotifyDormCharacterRecovery>(harness,
            nameof(NotifyDormCharacterRecovery), "Dorm remove character recovery push");
        DormRemoveCharacterResponse remove = ReadResponsePayload<DormRemoveCharacterResponse>(harness, 46_995_013,
            nameof(DormRemoveCharacterResponse), "Dorm remove character response");
        AssertEqual(0, remove.Code, "Dorm remove character code");
        AssertEqual(secondCharacterId, remove.SuccessList.Single().CharacterId, "Dorm removed character id");
        AssertEqual(room.Id, remove.SuccessList.Single().DormitoryId, "Dorm removed character room");
        AssertEqual(-1, player.Dorm.Characters.Single(character => character.CharacterId == secondCharacterId).DormitoryId,
            "Dorm remove character persisted assignment");
        AssertEqual(room.Id, player.Dorm.Characters.Single(character => character.CharacterId == firstCharacterId).DormitoryId,
            "Dorm remove character preserves other assignments");

        DormCharacterFondleTable fondle = TableReaderV2.Parse<DormCharacterFondleTable>().FirstOrDefault(row => row.CharacterId == firstCharacterId)
            ?? throw new InvalidDataException("Dorm compatibility: missing fondle row for owned character.");
        InvokeRegisteredRequestHandler(nameof(GetFondleDataRequest), harness.Session, 46_995_021, new GetFondleDataRequest { CharacterId = firstCharacterId });
        GetFondleDataResponse fondleData = ReadResponsePayload<GetFondleDataResponse>(harness, 46_995_021, nameof(GetFondleDataResponse), "Dorm fondle data response");
        AssertEqual(true, fondleData.FondleCount >= 0 && fondleData.FondleCount <= fondle.MaxCount, "Dorm fondle table count invariant");
        int fondlesBefore = player.Dorm.Characters.Single(row => row.CharacterId == firstCharacterId).LeftFondleCount;
        HashSet<int> fondleConditionIds = TableReaderV2.Parse<ConditionTable>().Where(condition => condition.Type == 29006).Select(condition => condition.Id).ToHashSet();
        if (fondlesBefore > 0)
        {
            InvokeRegisteredRequestHandler(nameof(DormDoFondleRequest), harness.Session, 46_995_022,
                new DormDoFondleRequest { CharacterId = firstCharacterId, FondleType = 1 });
            NotifyTask fondleTask = ReadPushPayload<NotifyTask>(harness, nameof(NotifyTask), "Dorm fondle task push");
            AssertEqual(true, fondleTask.Tasks.Tasks.Any(task => task.Schedule.Any(schedule => fondleConditionIds.Contains((int)schedule.Id) && schedule.Value == 1)),
                "Dorm fondle task schedule");
            NotifyCharacterMood fondleMood = ReadPushPayload<NotifyCharacterMood>(harness, nameof(NotifyCharacterMood), "Dorm fondle mood push");
            AssertEqual(firstCharacterId, fondleMood.CharacterId, "Dorm fondle mood character");
            AssertEqual(0, ReadResponsePayload<DormDoFondleResponse>(harness, 46_995_022, nameof(DormDoFondleResponse), "Dorm fondle response").Code,
                "Dorm fondle code");
            AssertEqual(fondlesBefore - 1, player.Dorm.Characters.Single(row => row.CharacterId == firstCharacterId).LeftFondleCount,
                "Dorm fondle persists count");
        }
        InvokeRegisteredRequestHandler(nameof(DormDoFondleRequest), harness.Session, 46_995_023,
            new DormDoFondleRequest { CharacterId = firstCharacterId, FondleType = 0 });
        AssertEqual(20060059, ReadResponsePayload<DormDoFondleResponse>(harness, 46_995_023, nameof(DormDoFondleResponse), "Dorm invalid fondle response").Code,
            "Dorm invalid fondle type code");
        AssertNoAvailablePacket(harness, "Dorm invalid fondle");
        InvokeRegisteredRequestHandler(nameof(DormOutRequest), harness.Session, 46_995_014, new DormOutRequest());
        AssertNoAvailablePacket(harness, "Dorm out one-way request");


        CurrentConditionTable enterCondition = TableReaderV2.Parse<CurrentConditionTable>().Single(condition => condition.Type == 29014);
        DateTimeOffset enterNow = DateTimeOffset.UtcNow;
        CurrentTaskTable[] activeEnterTasks = TableReaderV2.Parse<CurrentTaskTable>()
            .Where(task => task.Condition == enterCondition.Id
                && (string.IsNullOrWhiteSpace(task.StartTime) || DateTimeOffset.TryParseExact(task.StartTime, "yyyy/M/d H:mm",
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset start) && enterNow >= start)
                && (string.IsNullOrWhiteSpace(task.EndTime) || DateTimeOffset.TryParseExact(task.EndTime, "yyyy/M/d H:mm",
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset end) && enterNow < end))
            .ToArray();
        AssertEqual(1, activeEnterTasks.Length, "Dorm enter active task table row");
        PlayerDormCharacter expiredEventCharacter = player.Dorm.Characters.First();
        expiredEventCharacter.EventList.Add(new PlayerDormEvent { EventId = int.MaxValue, EndTime = (uint)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1) });
        long resumeItemBefore = inventory.Items.FirstOrDefault(item => item.Id == work.ItemId)?.Count ?? 0;
        player.Dorm.PendingRewards.Add(new PlayerDormPendingReward
        {
            Key = $"dorm-resume:{playerId}:compat",
            Goods = [new PlayerDormPendingRewardItem { TemplateId = work.ItemId, Count = 1, Params = [] }]
        });
        InvokeRegisteredRequestHandler(nameof(DormEnterRequest), harness.Session, 46_995_001, new DormEnterRequest());
        NotifyItemDataList resumedDormReward = ReadPushPayload<NotifyItemDataList>(harness, nameof(NotifyItemDataList), "Dorm enter pending reward push");
        AssertEqual(work.ItemId, resumedDormReward.ItemDataList.Single().Id, "Dorm enter pending reward item");
        AssertEqual(0, player.Dorm.PendingRewards.Count, "Dorm enter clears restored pending reward");
        NotifyTask enterTask = ReadPushPayload<NotifyTask>(harness, nameof(NotifyTask), "Dorm enter task progress push");
        AssertEqual(1, enterTask.Tasks.Tasks.Count, "Dorm enter task count");
        AssertEqual((uint)activeEnterTasks.Single().Id, enterTask.Tasks.Tasks.Single().Id, "Dorm enter task row is active");
        AssertEqual(true, enterTask.Tasks.Tasks.Single().Schedule.Any(schedule => schedule.Id == enterCondition.Id && schedule.Value == 1),
            "Dorm enter task schedule");
        DormEnterResponse enter = ReadResponsePayload<DormEnterResponse>(harness, 46_995_001, nameof(DormEnterResponse), "Dorm enter response");
        NotifyCharacterAttr enterAttrs = ReadPushPayload<NotifyCharacterAttr>(harness, nameof(NotifyCharacterAttr), "Dorm enter character attr push");
        AssertEqual(2, enterAttrs.AttrList.Count, "Dorm enter character attr count");
        AssertEqual(true, expiredEventCharacter.EventList.Any(evt => evt.EventId == int.MaxValue), "Dorm enter preserves expired event");
        AssertEqual(0, enter.Code, "Dorm enter code");
        AssertEqual(2, enter.CharacterEvents.Count, "Dorm enter character events");

        InvokeRegisteredRequestHandler(nameof(DormEnterRequest), harness.Session, 46_995_038, new DormEnterRequest());
        _ = ReadPushPayload<NotifyTask>(harness, nameof(NotifyTask), "Dorm reenter task progress push");
        AssertEqual(0, ReadResponsePayload<DormEnterResponse>(harness, 46_995_038, nameof(DormEnterResponse), "Dorm reenter response").Code,
            "Dorm reenter code");
        _ = ReadPushPayload<NotifyCharacterAttr>(harness, nameof(NotifyCharacterAttr), "Dorm reenter character attr push");
        AssertEqual(resumeItemBefore + 1, inventory.Items.Single(item => item.Id == work.ItemId).Count,
            "Dorm reenter does not duplicate restored reward");
        PlayerDormCharacter worker = player.Dorm.Characters.Single(row => row.CharacterId == firstCharacterId);
        worker.Vitality = work.Vitality * 2;
        uint beforeWork = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        HashSet<int> workConditionIds = TableReaderV2.Parse<ConditionTable>().Where(condition => condition.Type == 29004).Select(condition => condition.Id).ToHashSet();
        InvokeRegisteredRequestHandler(nameof(DormWorkRequest), harness.Session, 46_995_002,
            new DormWorkRequest { Works = [new DormWork { CharacterId = firstCharacterId, WorkPos = work.Seat }] });
        NotifyCharacterVitality vitality = ReadPushPayload<NotifyCharacterVitality>(harness,
            nameof(NotifyCharacterVitality), "Dorm work vitality push");
        AssertEqual(firstCharacterId, vitality.CharacterId, "Dorm work vitality character");
        AssertEqual(0, vitality.Vitality, "Dorm work vitality depletion");
        NotifyTask workTask = ReadPushPayload<NotifyTask>(harness, nameof(NotifyTask), "Dorm work task push");
        AssertEqual(true, workTask.Tasks.Tasks.Any(task => task.Schedule.Any(schedule => workConditionIds.Contains((int)schedule.Id) && schedule.Value == 1)),
            "Dorm work task schedule");
        DormWorkResponse workResponse = ReadResponsePayload<DormWorkResponse>(harness, 46_995_002,
            nameof(DormWorkResponse), "Dorm work response");
        AssertEqual(0, workResponse.Code, "Dorm work code");
        PlayerDormWork persistedWork = player.Dorm.WorkList.Single();
        AssertEqual(firstCharacterId, persistedWork.CharacterId, "Dorm work persisted character");
        AssertEqual(work.Seat, persistedWork.WorkPos, "Dorm work persisted position");
        AssertEqual(room.Id, worker.DormitoryId, "Dorm work preserves character room assignment");
        NotifyDormitoryData reconnect = (NotifyDormitoryData)buildLoginData.Invoke(null, [harness.Session])!;
        AssertEqual(room.Id, reconnect.CharacterList.Single(character => character.CharacterId == firstCharacterId).DormitoryId,
            "Dorm work assignment survives reconnect");
        uint expectedEndTime = (uint)(2L * work.Time * timePerVitality);
        uint observedDuration = persistedWork.WorkEndTime - beforeWork;
        AssertEqual(true, observedDuration >= expectedEndTime && observedDuration <= expectedEndTime + 1,
            "Dorm work table/config-derived duration");
        AssertEqual(true, playerSaves.ReplaceOneCalls >= 2, "Dorm work persists player before packets");
        AssertEqual(1, workResponse.WorkList.Count, "Dorm work response work count");
        AssertEqual(persistedWork.WorkEndTime, workResponse.WorkList.Single().WorkEndTime, "Dorm work response state");

        InvokeRegisteredRequestHandler(nameof(DormRemoveCharacterRequest), harness.Session, 46_995_002_1,
            new DormRemoveCharacterRequest { CharacterIds = [firstCharacterId] });
        AssertEqual(20060040, ReadResponsePayload<DormRemoveCharacterResponse>(harness, 46_995_002_1,
            nameof(DormRemoveCharacterResponse), "Dorm active worker removal response").Code, "Dorm active worker removal rejected");

        persistedWork.WorkEndTime = (uint)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1);
        long workItemBefore = inventory.Items.FirstOrDefault(item => item.Id == work.ItemId)?.Count ?? 0;
        inventorySaves.ThrowOnReplaceOne = true;
        InvokeRegisteredRequestHandler(nameof(DormWorkRewardRequest), harness.Session, 46_995_003,
            new DormWorkRewardRequest { PosList = [work.Seat] });
        inventorySaves.ThrowOnReplaceOne = false;
        AssertEqual(20060040, ReadResponsePayload<DormWorkRewardResponse>(harness, 46_995_003,
            nameof(DormWorkRewardResponse), "Dorm work failed claim response").Code, "Dorm work failed reward persistence");
        AssertEqual(true, player.Dorm.PendingRewards.Count == 1 && player.Dorm.WorkList.Single().WorkEndTime == 0,
            "Dorm work persists pending completion before inventory reward");

        InvokeRegisteredRequestHandler(nameof(DormWorkRewardRequest), harness.Session, 46_995_004,
            new DormWorkRewardRequest { PosList = [work.Seat] });
        NotifyItemDataList itemPush = ReadPushPayload<NotifyItemDataList>(harness,
            nameof(NotifyItemDataList), "Dorm work claim item push");
        Item claimedItem = itemPush.ItemDataList.Single();
        AssertEqual(work.ItemId, claimedItem.Id, "Dorm work claim item id");
        AssertEqual(workItemBefore + 2L * rewardPerVitality, claimedItem.Count, "Dorm work claim item count");
        DormWorkRewardResponse claim = ReadResponsePayload<DormWorkRewardResponse>(harness, 46_995_004,
            nameof(DormWorkRewardResponse), "Dorm work claim response");
        AssertEqual(0, claim.Code, "Dorm work claim code");
        AssertEqual(1, claim.WorkRewards.Count, "Dorm work claim response reward count");
        AssertEqual(work.Seat, claim.WorkRewards.Single().WorkPos, "Dorm work claim response position");
        AssertEqual(work.ItemId, claim.WorkRewards.Single().ItemId, "Dorm work claim response item");
        AssertEqual(2 * rewardPerVitality, claim.WorkRewards.Single().ItemNum, "Dorm work claim response count");
        AssertEqual(0, player.Dorm.PendingRewards.Count, "Dorm work claim clears pending reward");
        AssertEqual(workItemBefore + 2L * rewardPerVitality, inventory.Items.Single(item => item.Id == work.ItemId).Count,
            "Dorm work retry grants inventory once");

        InvokeRegisteredRequestHandler(nameof(DormWorkRewardRequest), harness.Session, 46_995_005,
            new DormWorkRewardRequest { PosList = [work.Seat] });
        DormWorkRewardResponse replay = ReadResponsePayload<DormWorkRewardResponse>(harness, 46_995_005,
            nameof(DormWorkRewardResponse), "Dorm work claim replay response");
        AssertEqual(20060040, replay.Code, "Dorm work claim replay code");
        AssertEqual(workItemBefore + 2L * rewardPerVitality, inventory.Items.Single(item => item.Id == work.ItemId).Count,
            "Dorm work claim replay does not increment inventory");

        worker.Vitality = work.Vitality;
        InvokeRegisteredRequestHandler(nameof(DormWorkRequest), harness.Session, 46_995_041,
            new DormWorkRequest { Works = [new DormWork { CharacterId = firstCharacterId, WorkPos = work.Seat }] });
        _ = ReadPushPayload<NotifyCharacterVitality>(harness, nameof(NotifyCharacterVitality), "Dorm work reused seat vitality push");
        _ = ReadPushPayload<NotifyTask>(harness, nameof(NotifyTask), "Dorm work reused seat task push");
        DormWorkResponse reusedSeat = ReadResponsePayload<DormWorkResponse>(harness, 46_995_041,
            nameof(DormWorkResponse), "Dorm work reused seat response");
        AssertEqual(0, reusedSeat.Code, "Dorm work claimed seat reusable");
        AssertEqual(1, player.Dorm.WorkList.Count, "Dorm work replaces claimed slot state");
        AssertEqual(true, player.Dorm.WorkList.Single().ClaimKey != persistedWork.ClaimKey,
            "Dorm reused seat receives distinct reward claim identity");
        AssertNoAvailablePacket(harness, "Dorm work reused seat");
        worker.Mood = work.Mood;
        inventorySaves.ThrowOnReplaceOne = true;
        InvokeRegisteredRequestHandler(nameof(DormWordDoneRequest), harness.Session, 46_995_042,
            new DormWordDoneRequest { WorkPos = [work.Seat] });
        inventorySaves.ThrowOnReplaceOne = false;
        AssertEqual(20060027, ReadResponsePayload<DormWordDoneResponse>(harness, 46_995_042,
            nameof(DormWordDoneResponse), "Dorm quick work failed claim response").Code, "Dorm quick work reward persistence failure");
        AssertEqual(true, player.Dorm.PendingRewards.Count == 1, "Dorm quick work persists pending reward");
        InvokeRegisteredRequestHandler(nameof(DormWordDoneRequest), harness.Session, 46_995_043,
            new DormWordDoneRequest { WorkPos = [work.Seat] });
        _ = ReadPushPayload<NotifyItemDataList>(harness, nameof(NotifyItemDataList), "Dorm quick work retry item push");
        AssertEqual(0, ReadResponsePayload<DormWordDoneResponse>(harness, 46_995_043,
            nameof(DormWordDoneResponse), "Dorm quick work retry response").Code, "Dorm quick work retry succeeds once");
        AssertEqual(0, player.Dorm.PendingRewards.Count, "Dorm quick work retry clears pending reward");
        _ = ReadPushPayload<NotifyCharacterAttr>(harness, nameof(NotifyCharacterAttr), "Dorm quick work retry character push");

        InvokeRegisteredRequestHandler(nameof(DormCharacterFinishAllEventRequest), harness.Session, 46_995_005,
            new DormCharacterFinishAllEventRequest());
        DormCharacterFinishAllEventResponse finish = ReadResponsePayload<DormCharacterFinishAllEventResponse>(harness,
            46_995_005, nameof(DormCharacterFinishAllEventResponse), "Dorm finish-all response");
        AssertEqual(20060040, finish.Code, "Dorm finish-all empty code");
        DormCharacterEventTable eventTable = TableReaderV2.Parse<DormCharacterEventTable>().FirstOrDefault()
            ?? throw new InvalidDataException("Dorm compatibility: no character event row.");
        PlayerDormCharacter eventCharacter = player.Dorm.Characters.Single(row => row.CharacterId == secondCharacterId);
        eventCharacter.EventList.Add(new PlayerDormEvent { EventId = eventTable.EventId, EndTime = 0 });
        InvokeRegisteredRequestHandler(nameof(DormCharacterFinishAllEventRequest), harness.Session, 46_995_006,
            new DormCharacterFinishAllEventRequest());
        DormCharacterFinishAllEventResponse completedFinish = ReadResponsePayload<DormCharacterFinishAllEventResponse>(harness,
            46_995_006, nameof(DormCharacterFinishAllEventResponse), "Dorm finish-all completion response");
        AssertEqual(20060040, completedFinish.Code, "Dorm finish-all unavailable reward mapping code");
        AssertEqual(1, eventCharacter.EventList.Count, "Dorm finish-all preserves event without authoritative reward mapping");
        AssertNoAvailablePacket(harness, "Dorm finish-all unavailable reward mapping");
    }

    private static int DormConfig(string key)
    {
        ConfigTable config = TableReaderV2.Parse<ConfigTable>().SingleOrDefault(row => row.Key == key)
            ?? throw new InvalidDataException($"Dorm compatibility: missing config {key}.");
        return int.Parse(config.Value, CultureInfo.InvariantCulture);
    }
}
