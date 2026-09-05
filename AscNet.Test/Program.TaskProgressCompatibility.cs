using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.task;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using System.Reflection;
using LoginTask = AscNet.Common.MsgPack.NotifyTaskData.NotifyTaskDataTaskData.NotifyTaskDataTaskDataTask;
using SyncTask = AscNet.Common.MsgPack.NotifyTask.NotifyTaskTasks.NotifyTaskTasksTask;

namespace AscNet.Test;

internal static partial class Program
{
    private static void ValidateTaskProgressCompatibility()
    {
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForShopCompatibility();
        Type taskModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.TaskModule");
        MethodInfo dispatch = typeof(Session).GetMethod("InvokeRequestHandler", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(Session).FullName, "InvokeRequestHandler");
        MethodInfo record = RequiredMethod(taskModule, "RecordTableDrivenProgress", BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(Session), typeof(IEnumerable<(int ConditionType, int? Parameter, int Amount)>)]);
        int packetId = 47_600;
        ValidateLoginDays();

        for (int rosterIndex = 0; rosterIndex < 2; rosterIndex++)
        {
            long uid = 99_760 + rosterIndex;
            Player player = CreateDrawCompatibilityPlayer(uid);
            player.PlayerData.Level = 9;
            Character roster = CreateDrawCompatibilityCharacter(uid);
            roster.Characters = Enumerable.Range(0, rosterIndex + 1).Select(index => new CharacterData
            {
                Id = (uint)(1_021_001 + index), Level = 50, Quality = 2, Grade = 1, Ability = 2499,
                SkillList = [new CharacterSkill { Id = 1, Level = 10 }, new CharacterSkill { Id = 2, Level = 11 }],
                EnhanceSkillList = [new CharacterSkill { Id = 3, Level = 20 }],
                MagicList = [new CharacterSkill { Id = 4, Level = 20 }]
            }).ToList();
            Inventory inventory = CreateDrawCompatibilityInventory(uid, [new Item { Id = Inventory.DailyActiveness, Count = 0 }]);
            using LoopbackSessionHarness harness = new(roster, player, inventory, $"task-progress-{rosterIndex}");
            harness.Session.stage = CreateLoginAccountCompatibilityStage(uid);
            harness.Session.stage.Stages.Clear();
            List<LoginTask> initial = BuildTaskData(harness.Session);
            Check(initial, 50020, 9, 1);
            Check(initial, 50043, 0, 1);
            Check(initial, 50044, 0, 1);
            Check(initial, 50045, 0, 1);
            Check(initial, 7866, rosterIndex + 1, 1);
            Check(initial, 7403, 0, 1);
            Check(initial, 7603, 0, 1);

            // Snapshot facts change without an event counter; the real dispatcher must publish them.
            player.PlayerData.Level = 10;
            roster.Characters[0].Quality = 3;
            roster.Characters[0].Grade = 6;
            roster.Characters[0].Ability = 2500;
            roster.Characters[0].SkillList[0].Level = 11;
            foreach (int stageId in new[] { 30070105, 30070110 })
                harness.Session.stage.AddStage(new StageDatum { StageId = stageId, Passed = true, PassTimesTotal = 1 });
            player.Save();
            roster.Save();
            harness.Session.stage.Save();
            (FightHeartbeatResponse heartbeat, List<SyncTask> delta) = Dispatch<FightHeartbeatRequest, FightHeartbeatResponse>(
                harness, nameof(FightHeartbeatRequest), new());
            AssertEqual(0, heartbeat.Code, "Snapshot heartbeat response");
            CheckDelta(delta, 50020, 10, 3);
            CheckDelta(delta, 50043, 1, 3);
            CheckDelta(delta, 50044, 1, 3);
            CheckDelta(delta, 50045, 1, 3);
            CheckDelta(delta, 7866, rosterIndex + 2, 1);
            CheckDelta(delta, 7403, 1, 1);
            CheckDelta(delta, 7603, 1, 3);
            AssertEqual(0, Dispatch<FightHeartbeatRequest, FightHeartbeatResponse>(harness, nameof(FightHeartbeatRequest), new()).Tasks.Count,
                "Unchanged dispatch does not duplicate snapshot deltas");
            foreach (int conditionId in new[] { 50020, 50043, 50044, 50045, 7866, 7403, 7603 })
                AssertEqual(false, player.MissionProgress.ConditionCounters.ContainsKey(conditionId), $"Snapshot {conditionId} has no cumulative counter");

            // Grade and BP are independent filters, not merely quality/level checks.
            roster.Characters[0].Grade = 5;
            roster.Characters[0].Ability = 2499;
            List<LoginTask> belowFilters = BuildTaskData(harness.Session);
            Check(belowFilters, 7403, 0, 1);
            Check(belowFilters, 7603, 0, 1);
            player.MissionProgress.ConditionCounters[7866] = 999;
            Check(BuildTaskData(harness.Session), 7866, rosterIndex + 2, 1);
            player.MissionProgress.ConditionCounters.Remove(7866);

            record.Invoke(null, [harness.Session, new (int, int?, int)[] { (11202, 4, 17) }]);
            CheckDelta(Drain(harness), 50046, 17, 1);
            AssertEqual(17, player.MissionProgress.ConditionCounters[50046], "Serum spending remains cumulative");

            LoginTask dispatchTask = BuildTaskData(harness.Session)
                .Single(task => task.Schedule.Any(schedule => schedule.Id == 2022));
            record.Invoke(null, [harness.Session, new (int, int?, int)[] { (29018, null, 1), (29004, null, 1) }]);
            List<SyncTask> dormDelta = Drain(harness);
            CheckDelta(dormDelta, (int)dispatchTask.Id, 1, 3);
            CheckDelta(dormDelta, 8019, 1, 3);
            AssertEqual(1, player.MissionProgress.ConditionCounters[2022], "Dorm dispatch counted once across both table catalogs");
            AssertEqual(1, player.MissionProgress.ConditionCounters[8019], "Dorm chores counted once across both table catalogs");
            (DormEnterResponse enter, List<SyncTask> enterDelta) = Dispatch<DormEnterRequest, DormEnterResponse>(harness, nameof(DormEnterRequest), new());
            AssertEqual(0, enter.Code, "Normal Dorm entry response");
            int enterConditionId = TableReaderV2.Parse<CurrentConditionTable>().Single(condition => condition.Type == 29014).Id;
            AssertEqual(true, enterDelta.Any(task => task.Schedule.Any(schedule => schedule.Id == enterConditionId && schedule.Value == 1)),
                "Normal Dorm entry publishes generic mission progress");

            // Claim the same derived Passport progress displayed by login, then retry after BSON reload.
            RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.PassportModule"),
                "PrepareLogin", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Session)])
                .Invoke(null, [harness.Session]);
            inventory.Items.Single(item => item.Id == Inventory.DailyActiveness).Count = 100;
            Check(BuildTaskData(harness.Session), 80038, 100, 3);
            long expBefore = inventory.Items.FirstOrDefault(item => item.Id == Inventory.PassportExp)?.Count ?? 0;
            FinishTaskResponse claim = Dispatch<FinishTaskRequest, FinishTaskResponse>(harness, nameof(FinishTaskRequest), new() { TaskId = 80038 }).Response;
            AssertEqual(0, claim.Code, "Passport activity claim uses inventory-derived progress");
            long awardedExp = inventory.Items.Single(item => item.Id == Inventory.PassportExp).Count;
            AssertEqual(true, awardedExp > expBefore, "Passport claim grants actual EXP");
            AssertEqual(false, player.MissionProgress.ConditionCounters.ContainsKey(80038), "Passport claim does not fabricate a cumulative counter");
            player.MissionProgress.ClaimedTaskIds.Add(50020);
            player.Save();
            harness.Session.player = player = BsonSerializer.Deserialize<Player>(player.ToBson());
            harness.Session.character = roster = BsonSerializer.Deserialize<Character>(roster.ToBson());
            harness.Session.inventory = inventory = BsonSerializer.Deserialize<Inventory>(inventory.ToBson());
            harness.Session.stage = BsonSerializer.Deserialize<Stage>(harness.Session.stage.ToBson());
            List<LoginTask> relog = BuildTaskData(harness.Session);
            Check(relog, 50020, 10, 4);
            Check(relog, 50043, 1, 3);
            Check(relog, 50044, 1, 3);
            Check(relog, 50045, 1, 3);
            Check(relog, 7866, rosterIndex + 2, 1);
            Check(relog, 8019, 1, 3);
            Check(relog, (int)dispatchTask.Id, 1, 3);
            Check(relog, 50046, 17, 1);
            Check(relog, 80038, 100, 4);
            FinishTaskResponse retry = Dispatch<FinishTaskRequest, FinishTaskResponse>(harness, nameof(FinishTaskRequest), new() { TaskId = 80038 }).Response;
            AssertEqual(20026006, retry.Code, "Claimed Passport retry rejected after reload");
            AssertEqual(awardedExp, inventory.Items.Single(item => item.Id == Inventory.PassportExp).Count, "Passport retry does not duplicate EXP");

            player.MissionProgress.DailyResetDay = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 86_400 - 1;
            GetActivenessRewardResponse staleActivity = Dispatch<GetActivenessRewardRequest, GetActivenessRewardResponse>(
                harness, nameof(GetActivenessRewardRequest), new() { RewardType = 1 }).Response;
            AssertEqual(20026010, staleActivity.Code, "Daily reset precedes activeness reward eligibility");
            AssertEqual(0L, inventory.Items.Single(item => item.Id == Inventory.DailyActiveness).Count, "Activeness claim clears stale daily inventory");
            AssertEqual(0L, player.PlayerData.DailyActivenessRewardStatus, "Stale activeness grants no reward milestone");
            inventory.Items.Single(item => item.Id == Inventory.DailyActiveness).Count = 100;
            player.MissionProgress.DailyResetDay--;
            FinishTaskResponse stalePassport = Dispatch<FinishTaskRequest, FinishTaskResponse>(harness, nameof(FinishTaskRequest), new() { TaskId = 80038 }).Response;
            AssertEqual(20026007, stalePassport.Code, "Daily reset precedes Passport task eligibility");
            AssertEqual(awardedExp, inventory.Items.Single(item => item.Id == Inventory.PassportExp).Count, "Stale Passport claim awards no EXP");
            Check(BuildTaskData(harness.Session), 80038, 0, 1);
            Check(BuildTaskData(harness.Session), 50020, 10, 4);

            EquipData weapon = new() { Id = 1, TemplateId = 2346001, Breakthrough = 0, Level = 29 };
            uint otherWeapon = (uint)TableReaderV2.Parse<AscNet.Table.V2.share.equip.EquipTable>()
                .First(row => row.Site == 0 && row.Id != weapon.TemplateId).Id;
            uint memory = (uint)TableReaderV2.Parse<AscNet.Table.V2.share.equip.EquipTable>()
                .First(row => row.Site is >= 1 and <= 6 && row.Quality == 6).Id;
            EquipData awareness = new() { Id = 3, TemplateId = memory, Breakthrough = 1, Level = 99 };
            roster.Equips = [weapon, new EquipData { Id = 2, TemplateId = otherWeapon, Breakthrough = 4, Level = 45 }, awareness];
            List<LoginTask> equipmentBefore = BuildTaskData(harness.Session);
            Check(equipmentBefore, 8018, 0, 1);
            Check(equipmentBefore, 7865, 0, 1);
            weapon.Level = 30;
            awareness.Breakthrough = 2;
            awareness.Level = 35;
            Check(BuildTaskData(harness.Session), 8018, 1, 3);
            Check(BuildTaskData(harness.Session), 7865, 1, 1);
            weapon.Breakthrough = 1;
            weapon.Level = 1;
            awareness.Breakthrough = 3;
            awareness.Level = 1;
            Check(BuildTaskData(harness.Session), 8018, 1, 3);
            Check(BuildTaskData(harness.Session), 7865, 1, 1);

            PartnerData cub = new()
            {
                Id = 1, TemplateId = 16010000, BreakThrough = 1, Level = 14, Quality = 3,
                SkillList =
                [
                    new PartnerSkillData { Id = 1, Type = 1, Level = 2 + rosterIndex },
                    new PartnerSkillData { Id = 2, Type = 1, Level = 2 + rosterIndex },
                    new PartnerSkillData { Id = 3, Type = 2, Level = 3 },
                    new PartnerSkillData { Id = 4, Type = 2, Level = 4 },
                    new PartnerSkillData { Id = 5, Type = 3, Level = 99 }
                ]
            };
            roster.Partners = [cub, new PartnerData { Id = 2, TemplateId = 16020000, BreakThrough = 3, Level = 30, Quality = 4 }];
            List<LoginTask> cubBefore = BuildTaskData(harness.Session);
            Check(cubBefore, 23010102, 0, 1);
            Check(cubBefore, 23020002, 1, 1);
            Check(cubBefore, 23040101, 0, 1);
            Check(cubBefore, 23050002, 9 + rosterIndex, 1);
            cub.Level = 15;
            cub.Quality = 4;
            List<LoginTask> cubAtBoundary = BuildTaskData(harness.Session);
            Check(cubAtBoundary, 23010102, 1, 3);
            Check(cubAtBoundary, 23020002, 2, 1);
            Check(cubAtBoundary, 23040101, 1, 3);
            cub.BreakThrough = 2;
            cub.Level = 1;
            Check(BuildTaskData(harness.Session), 23010102, 1, 3);

            roster.Characters[0].EnhanceSkillList =
            [
                new CharacterSkill { Id = 102531, Level = 17 },
                new CharacterSkill { Id = 999999, Level = 99 }
            ];
            Check(BuildTaskData(harness.Session), 8023, 0, 1);
            roster.Characters[0].EnhanceSkillList.Add(new CharacterSkill { Id = 102528, Level = 18 });
            Check(BuildTaskData(harness.Session), 8023, 1, 3);
            harness.Session.stage.AddStage(new StageDatum { StageId = 10010101, Passed = true, StarsMark = rosterIndex == 0 ? 5 : 7 });
            harness.Session.stage.AddStage(new StageDatum { StageId = 10010102, Passed = true, StarsMark = 2 });
            Check(BuildTaskData(harness.Session), 50069, 3 + rosterIndex, 1);

            harness.Session.stage.AddStage(new StageDatum { StageId = 30090802, Passed = true, PassTimesTotal = 3 });
            MethodInfo stageClear = RequiredMethod(taskModule, "RecordStageClear", BindingFlags.Static | BindingFlags.Public,
                [typeof(Session), typeof(int), typeof(int), typeof(int), typeof(bool)]);
            stageClear.Invoke(null, [harness.Session, 30090802, 3, 0, true]);
            _ = Drain(harness);
            Check(BuildTaskData(harness.Session), 7305, 3, 3);
            stageClear.Invoke(null, [harness.Session, 30090802, 2, 0, false]);
            _ = Drain(harness);
            AssertEqual(5, player.MissionProgress.ConditionCounters[7305], "Repeated stage condition preserves clear multiplicity");
            Check(BuildTaskData(harness.Session), 7305, 3, 3);

            var sourceStages = TableReaderV2.Parse<AscNet.Table.V2.share.fuben.StageTable>();
            Dictionary<int, int> typedProgress = new() { [3040] = 0, [7301] = 0, [3050] = 0, [7104] = 0, [7803] = 0 };
            foreach (int taskId in typedProgress.Keys)
                player.MissionProgress.ConditionCounters.Remove(taskId);
            foreach ((int stageType, int matchingTask) in new[] { (5, 3040), (11, 7301), (12, 3050), (20, 7104), (17, 7803), (36, 7803), (94, 7803) })
            {
                int typedStage = sourceStages.First(stage => stage.Type == stageType).StageId;
                stageClear.Invoke(null, [harness.Session, typedStage, 1, 0, true]);
                _ = Drain(harness);
                typedProgress[matchingTask]++;
                List<LoginTask> typedTasks = BuildTaskData(harness.Session);
                foreach ((int taskId, int count) in typedProgress)
                    Check(typedTasks, taskId, count, taskId == 7803 && count == 3 ? 3 : 1);
            }
            int unclassifiedStage = sourceStages.First(stage => stage.Type is null or 0).StageId;
            stageClear.Invoke(null, [harness.Session, unclassifiedStage, 1, 0, true]);
            _ = Drain(harness);
            foreach ((int taskId, int count) in typedProgress)
                Check(BuildTaskData(harness.Session), taskId, count, taskId == 7803 ? 3 : 1);

            int prequelStory = sourceStages.First(stage => stage.Type == 11 && stage.StageType == 2).StageId;
            harness.Session.stage.Stages.Remove(prequelStory);
            EnterStoryResponse entered = Dispatch<EnterStoryRequest, EnterStoryResponse>(harness, nameof(EnterStoryRequest), new() { StageId = prequelStory }).Response;
            AssertEqual(0, entered.Code, "Prequel story accepted through registered dispatcher");
            Check(BuildTaskData(harness.Session), 7301, 2, 1);
            EnterStoryResponse replayed = Dispatch<EnterStoryRequest, EnterStoryResponse>(harness, nameof(EnterStoryRequest), new() { StageId = prequelStory }).Response;
            AssertEqual(0, replayed.Code, "Prequel story duplicate remains accepted");
            Check(BuildTaskData(harness.Session), 7301, 2, 1);
            int prequelCombat = sourceStages.First(stage => stage.Type == 11 && stage.StageType is not (2 or 3)).StageId;
            harness.Session.stage.Stages.Remove(prequelCombat);
            EnterStoryResponse forgedCombat = Dispatch<EnterStoryRequest, EnterStoryResponse>(harness, nameof(EnterStoryRequest), new() { StageId = prequelCombat }).Response;
            AssertEqual(20003002, forgedCombat.Code, "Combat stage cannot be cleared through EnterStory");
            AssertEqual(false, harness.Session.stage.Stages.ContainsKey(prequelCombat), "Forged combat story does not persist a clear");
            Check(BuildTaskData(harness.Session), 7301, 2, 1);
            int nonexistentStage = checked(sourceStages.Max(stage => stage.StageId) + 1);
            EnterStoryResponse invalidStory = Dispatch<EnterStoryRequest, EnterStoryResponse>(harness, nameof(EnterStoryRequest), new() { StageId = nonexistentStage }).Response;
            AssertEqual(true, invalidStory.Code != 0, "Unknown story stage is rejected");
            Check(BuildTaskData(harness.Session), 7301, 2, 1);
            int lockedStory = TableReaderV2.Parse<AscNet.Table.V2.share.fuben.fashionstory.FashionStoryTable>()
                .SelectMany(activity => activity.TrialStages).First(id => sourceStages.Any(stage => stage.StageId == id));
            harness.Session.stage.Stages.Remove(lockedStory);
            string beforeLocked = string.Join("|", player.MissionProgress.ConditionCounters.OrderBy(entry => entry.Key));
            EnterStoryResponse locked = Dispatch<EnterStoryRequest, EnterStoryResponse>(harness, nameof(EnterStoryRequest), new() { StageId = lockedStory }).Response;
            AssertEqual(20003024, locked.Code, "Unauthorized fashion story is rejected");
            AssertEqual(false, harness.Session.stage.Stages.ContainsKey(lockedStory), "Locked story is not persisted as passed");
            AssertEqual(beforeLocked, string.Join("|", player.MissionProgress.ConditionCounters.OrderBy(entry => entry.Key)), "Locked story gives no mission credit");

            var mainChapters = TableReaderV2.Parse<AscNet.Table.V2.share.fuben.mainline.ChapterTable>();
            var extraChapters = TableReaderV2.Parse<AscNet.Table.V2.share.fuben.extrachapter.ChapterExtraDetailsTable>();
            var shortChapters = TableReaderV2.Parse<AscNet.Table.V2.share.fuben.shortstory.ShortStoryDetailsTable>();
            List<int>[] chapterStages =
            [
                TableReaderV2.Parse<AscNet.Table.V2.share.fuben.mainline.ChapterMainTable>()
                    .Select(row => mainChapters.FirstOrDefault(chapter => chapter.ChapterId == row.ChapterId.FirstOrDefault()))
                    .First(chapter => chapter?.StageId.Count > 1)!.StageId,
                TableReaderV2.Parse<AscNet.Table.V2.share.fuben.extrachapter.ChapterExtraTable>()
                    .Select(row => extraChapters.FirstOrDefault(chapter => chapter.ChapterId == row.ChapterId.FirstOrDefault()))
                    .First(chapter => chapter?.StageId.Count > 1)!.StageId,
                TableReaderV2.Parse<AscNet.Table.V2.share.fuben.shortstory.ShortStoryChapterTable>()
                    .Select(row => shortChapters.FirstOrDefault(chapter => chapter.ChapterId == row.ChapterId))
                    .First(chapter => chapter?.StageId.Count > 1)!.StageId
            ];
            foreach (List<int> stages in chapterStages)
            {
                harness.Session.stage.Stages.Clear();
                harness.Session.stage.AddStage(new StageDatum { StageId = stages[0], Passed = true });
                List<LoginTask> partialChapter = BuildTaskData(harness.Session);
                Check(partialChapter, 8022, 0, 1);
                Check(partialChapter, 7813, 1, 1);
                foreach (int stageId in stages.Skip(1))
                    harness.Session.stage.AddStage(new StageDatum { StageId = stageId, Passed = true });
                Check(BuildTaskData(harness.Session), 8022, 1, 1);
            }

            roster.Characters.Add(new CharacterData { Id = 1021005, Quality = 6, Level = 80, Grade = 10, Ability = 6000 });
            roster.Fashions = TableReaderV2.Parse<AscNet.Table.V2.share.character.CharacterTable>()
                .Select(character => character.DefaultNpcFashtionId).Where(id => id > 0).Distinct().Take(21)
                .Select(id => new FashionList { Id = id, IsLock = false }).ToList();
            int[] unsupportedTasks = [8031, 3150, 3151, 3152, 3153];
            foreach (int id in unsupportedTasks)
                player.MissionProgress.ConditionCounters[id] = id == 8031 ? 1 : 20;
            List<LoginTask> unsupported = BuildTaskData(harness.Session);
            foreach (int id in unsupportedTasks)
            {
                Check(unsupported, id, 0, 1);
                player.MissionProgress.ClaimedTaskIds.Add(id);
            }
            List<LoginTask> previouslyClaimed = BuildTaskData(harness.Session);
            foreach (int id in unsupportedTasks)
                Check(previouslyClaimed, id, 0, 4);

            Check(BuildTaskData(harness.Session), 2120001, 0, 1);
            string itemsBeforeEvent = Convert.ToBase64String(inventory.ToBson());
            (DoClientTaskEventResponse clientEvent, List<SyncTask> eventDelta) =
                Dispatch<DoClientTaskEventRequest, DoClientTaskEventResponse>(harness, nameof(DoClientTaskEventRequest), new() { ClientTaskType = 10 });
            AssertEqual(0, clientEvent.Code, "Supported client event accepted");
            CheckDelta(eventDelta, 2120001, 1, 3);
            Check(BuildTaskData(harness.Session), 2120001, 1, 3);
            AssertEqual(1, BsonSerializer.Deserialize<Player>(player.ToBson()).MissionProgress.ConditionCounters[2120001],
                "Client event progress survives BSON reload");
            _ = Dispatch<DoClientTaskEventRequest, DoClientTaskEventResponse>(harness, nameof(DoClientTaskEventRequest), new() { ClientTaskType = 10 });
            AssertEqual(1, player.MissionProgress.ConditionCounters[2120001], "Repeated client event is not cumulative");
            AssertEqual(itemsBeforeEvent, Convert.ToBase64String(inventory.ToBson()), "Client events do not grant unclaimed rewards");
            player.MissionProgress.ClaimedTaskIds.Add(2120001);
            _ = Dispatch<DoClientTaskEventRequest, DoClientTaskEventResponse>(harness, nameof(DoClientTaskEventRequest), new() { ClientTaskType = 10 });
            Check(BuildTaskData(harness.Session), 2120001, 1, 4);
            string countersBeforeInvalid = string.Join("|", player.MissionProgress.ConditionCounters.OrderBy(entry => entry.Key));
            var levelBeforeInvalid = player.PlayerData.Level;
            _ = Dispatch<DoClientTaskEventRequest, DoClientTaskEventResponse>(harness, nameof(DoClientTaskEventRequest), new() { ClientTaskType = 10101 });
            AssertEqual(levelBeforeInvalid, player.PlayerData.Level, "Client event cannot spoof level progress");
            AssertEqual(countersBeforeInvalid, string.Join("|", player.MissionProgress.ConditionCounters.OrderBy(entry => entry.Key)),
                "Unsupported client discriminator changes no mission counters");
            AssertEqual(itemsBeforeEvent, Convert.ToBase64String(inventory.ToBson()), "Unsupported client discriminator grants nothing");
        }

        using (MongoCollectionOverride retryMongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out RecordingMongoCollectionProxy<Player> playerSaves,
            out RecordingMongoCollectionProxy<Character> characterSaves,
            out RecordingMongoCollectionProxy<Inventory> inventorySaves))
        {
            const long uid = 99_762;
            using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(uid),
                CreateDrawCompatibilityPlayer(uid), CreateDrawCompatibilityInventory(uid, []), "current-task-failed-save");
            harness.Session.stage = CreateLoginAccountCompatibilityStage(uid);
            _ = BuildTaskData(harness.Session);
            byte[] playerBefore = harness.Session.player.ToBson();
            FinishTaskResponse failed;
            playerSaves.ThrowOnReplaceOne = true;
            try
            {
                failed = Dispatch<FinishTaskRequest, FinishTaskResponse>(harness, nameof(FinishTaskRequest), new() { TaskId = 50020 }).Response;
            }
            finally
            {
                playerSaves.ThrowOnReplaceOne = false;
            }
            AssertEqual(20026003, failed.Code, "Current task reports player persistence failure");
            AssertEqual(false, harness.Session.player.MissionProgress.ClaimedTaskIds.Contains(50020), "Failed current task remains retryable");
            harness.Session.player = BsonSerializer.Deserialize<Player>(playerBefore);
            harness.Session.inventory = BsonSerializer.Deserialize<Inventory>((inventorySaves.LastReplacement
                ?? throw new InvalidDataException("Current task reward inventory was not persisted.")).ToBson());
            harness.Session.character = BsonSerializer.Deserialize<Character>((characterSaves.LastReplacement
                ?? throw new InvalidDataException("Current task reward character state was not persisted.")).ToBson());
            AssertEqual(2500L, harness.Session.inventory.Items.Single(item => item.Id == 50000).Count,
                "Current task inventory grant survives failed player save");
            Check(BuildTaskData(harness.Session), 50020, 10, 3);
            FinishTaskResponse recovered = Dispatch<FinishTaskRequest, FinishTaskResponse>(harness, nameof(FinishTaskRequest), new() { TaskId = 50020 }).Response;
            AssertEqual(0, recovered.Code, "Current task retry completes after reload");
            AssertEqual(2500L, harness.Session.inventory.Items.Single(item => item.Id == 50000).Count, "Current task retry does not duplicate persisted reward");
            Check(BuildTaskData(harness.Session), 50020, 10, 4);
            FinishTaskResponse duplicate = Dispatch<FinishTaskRequest, FinishTaskResponse>(harness, nameof(FinishTaskRequest), new() { TaskId = 50020 }).Response;
            AssertEqual(20026006, duplicate.Code, "Recovered current task rejects duplicate claim");
            AssertEqual(2500L, harness.Session.inventory.Items.Single(item => item.Id == 50000).Count, "Recovered current task duplicate grants nothing");
        }

        void ValidateLoginDays()
        {
            const long uid = 99_763;
            const long firstLogin = 20_000 * 86_400L + 43_200;
            const long laterLogin = firstLogin + 5 * 86_400L;
            const long retryLogin = laterLogin + 3 * 86_400L;
            MethodInfo recordLogin = RequiredMethod(taskModule, "RecordLoginDay", BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(Session), typeof(long?)]);
            using MongoCollectionOverride loginMongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
                out RecordingMongoCollectionProxy<Player> playerSaves, out _, out _);
            Player player = CreateDrawCompatibilityPlayer(uid);
            player.PlayerData.CreateTime = firstLogin - 100 * 86_400L;
            player.PlayerData.LastLoginTime = player.PlayerData.CreateTime;
            player.PlayerData.NewPlayerTaskActiveDay = 0;
            using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(uid), player,
                CreateDrawCompatibilityInventory(uid, []), "task-distinct-login-days");
            harness.Session.stage = CreateLoginAccountCompatibilityStage(uid);
            Check(BuildTaskData(harness.Session), 7862, 0, 1);

            RecordAndCheck(firstLogin, 1);
            RecordAndCheck(firstLogin + 1, 1);
            RecordAndCheck(laterLogin, 2);
            harness.Session.player = BsonSerializer.Deserialize<Player>(PersistedPlayer().ToBson());
            RecordAndCheck(laterLogin + 1, 2);

            long markerBefore = harness.Session.player.PlayerData.LastLoginTime;
            bool failed = false;
            playerSaves.ThrowOnReplaceOne = true;
            try
            {
                recordLogin.Invoke(null, [harness.Session, retryLogin]);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is MongoDB.Driver.MongoException)
            {
                failed = true;
            }
            finally
            {
                playerSaves.ThrowOnReplaceOne = false;
            }
            AssertEqual(true, failed, "Login day persistence failure propagates");
            AssertEqual(2, harness.Session.player.PlayerData.NewPlayerTaskActiveDay, "Failed login day rolls back count");
            AssertEqual(markerBefore, harness.Session.player.PlayerData.LastLoginTime, "Failed login day rolls back marker");
            Check(BuildTaskData(harness.Session), 7862, 2, 1);
            RecordAndCheck(retryLogin, 3);
            harness.Session.player = BsonSerializer.Deserialize<Player>(PersistedPlayer().ToBson());
            RecordAndCheck(retryLogin + 1, 3);

            // Legacy positive counts without a login marker are preserved, not age-backfilled.
            harness.Session.player.PlayerData.NewPlayerTaskActiveDay = 4;
            harness.Session.player.PlayerData.LastLoginTime = 0;
            RecordAndCheck(retryLogin + 2, 4);
            RecordAndCheck(retryLogin + 3, 4);

            Player PersistedPlayer() => BsonSerializer.Deserialize<Player>((playerSaves.LastReplacement
                ?? throw new InvalidDataException("Login day player was not persisted.")).ToBson());

            void RecordAndCheck(long timestamp, int expectedDays)
            {
                recordLogin.Invoke(null, [harness.Session, timestamp]);
                // Inspect the recorded save before BuildTaskData can persist any task initialization.
                Player persisted = PersistedPlayer();
                AssertEqual(expectedDays, persisted.PlayerData.NewPlayerTaskActiveDay, "Persisted distinct login days");
                AssertEqual(timestamp, persisted.PlayerData.LastLoginTime, "Persisted successful login marker");
                AssertEqual(expectedDays, harness.Session.player.PlayerData.NewPlayerTaskActiveDay, "Live distinct login days");
                Check(BuildTaskData(harness.Session), 7862, expectedDays, 1);
            }
        }

        static void Check(IEnumerable<LoginTask> tasks, int id, int value, int state)
        {
            LoginTask task = RequiredStoryLoginTask(tasks, id);
            AssertEqual(value, task.Schedule.Single().Value, $"Task {id} login progress");
            AssertEqual(state, task.State, $"Task {id} login state");
        }

        static void CheckDelta(IEnumerable<SyncTask> tasks, int id, int value, int state)
        {
            SyncTask task = tasks.Single(task => task.Id == id);
            AssertEqual(value, task.Schedule.Single().Value, $"Task {id} live progress");
            AssertEqual(state, task.State, $"Task {id} live state");
        }

        static List<SyncTask> Drain(LoopbackSessionHarness harness)
        {
            List<SyncTask> tasks = [];
            while (harness.TryReadAvailablePacket("Task progress trailing push", out Packet? packet))
            {
                AssertEqual(Packet.ContentType.Push, packet!.Type, "Task progress trailing packet type");
                Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                if (push.Name == nameof(NotifyTask))
                    tasks.AddRange(MessagePackSerializer.Deserialize<NotifyTask>(push.Content).Tasks.Tasks);
            }
            return tasks;
        }

        (TResponse Response, List<SyncTask> Tasks) Dispatch<TRequest, TResponse>(LoopbackSessionHarness harness, string requestName, TRequest request)
        {
            int id = packetId++;
            dispatch.Invoke(harness.Session, [GetRegisteredRequestHandler(requestName), new Packet.Request
            {
                Id = id, Name = requestName, Content = MessagePackSerializer.Serialize(request)
            }]);
            List<SyncTask> tasks = [];
            while (true)
            {
                Packet packet = harness.ReadPacket($"Task progress {requestName}");
                if (packet.Type == Packet.ContentType.Response)
                {
                    Packet.Response response = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                    AssertEqual(id, response.Id, "Task progress response request id");
                    AssertEqual(requestName.Replace("Request", "Response"), response.Name, "Task progress response name");
                    tasks.AddRange(Drain(harness));
                    return (MessagePackSerializer.Deserialize<TResponse>(response.Content), tasks);
                }
                Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                if (push.Name == nameof(NotifyTask))
                    tasks.AddRange(MessagePackSerializer.Deserialize<NotifyTask>(push.Content).Tasks.Tasks);
            }
        }
    }
}
