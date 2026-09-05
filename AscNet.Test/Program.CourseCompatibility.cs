using System.Reflection;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.fuben.course;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidateCourseCompatibility()
    {
        PacketFactory.LoadPacketHandlers();
        const long uid = 49_301;
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForStoryDeployVersionGapCompatibility();
        Player player = CreateDrawCompatibilityPlayer(uid);
        Character character = CreateDrawCompatibilityCharacter(uid);
        Inventory inventory = CreateDrawCompatibilityInventory(uid, []);
        using LoopbackSessionHarness harness = new(character, player, inventory, "course-loopback");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(uid);
        List<CourseChapterTable> chapters = TableReaderV2.Parse<CourseChapterTable>();
        Dictionary<int, CourseStageTable> stages = TableReaderV2.Parse<CourseStageTable>().ToDictionary(row => row.StageId);
        CourseChapterTable lesson = chapters.First(row => row.StageType == 1 && row.StageIds.Count > 1);
        CourseChapterTable exam = chapters.First(row => row.StageType == 2 && row.PrevChapterIds is not > 0);
        CourseRewardTable reward = TableReaderV2.Parse<CourseRewardTable>()
            .Where(row => row.ChapterId == lesson.ChapterId && row.Point > 0).OrderBy(row => row.Point).First();
        player.PlayerData.Level = chapters.Max(row => row.UnlockLv ?? 0);
        Type module = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.CourseModule");
        MethodInfo login = RequiredMethod(module, "BuildLoginData", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Player)]);
        MethodInfo complete = RequiredMethod(module, "IsChapterComplete", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Player), typeof(int)]);
        int packetId = 49_310;
        int Mask(int stageId) => (1 << stages[stageId].StarPoint.Count) - 1;
        bool IsComplete(int chapterId) => (bool)complete.Invoke(null, [player, chapterId])!;
        NotifyCourseData Login() => (NotifyCourseData)login.Invoke(null, [player])!;

        T Response<T>(int id, string name, bool allowPushes = false, bool rewardPushes = false)
        {
            string[] rewardOrder = [nameof(NotifyItemDataList), nameof(NotifyEquipDataList), nameof(FashionSyncNotify),
                nameof(NotifyWeaponFashionInfo), nameof(NotifyCharacterDataList), nameof(NotifyPartnerDataList),
                nameof(NotifyGatherReward), nameof(NotifyHeadPortraitInfos)];
            int previous = -1;
            for (int index = 0; index < 32; index++)
            {
                Packet packet = harness.ReadPacket(name);
                if (packet.Type == Packet.ContentType.Push)
                {
                    AssertEqual(true, allowPushes, $"{name} unexpected push");
                    Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                    AssertEqual(false, push.Name == nameof(NotifyCourseData), "Course result must not update saved client cache before save response");
                    if (rewardPushes)
                    {
                        int position = Array.IndexOf(rewardOrder, push.Name);
                        AssertEqual(true, position >= 0 && position >= previous, "Course rewards publish ordered inventory pushes before response");
                        previous = position;
                    }
                    continue;
                }
                Packet.Response response = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                AssertEqual(Packet.ContentType.Response, packet.Type, name);
                AssertEqual(id, response.Id, $"{name} packet id");
                AssertEqual(name, response.Name, $"{name} packet name");
                T result = MessagePackSerializer.Deserialize<T>(response.Content);
                AssertNoAvailablePacket(harness, name);
                return result;
            }
            throw new InvalidDataException($"{name}: response missing after pushes.");
        }

        CourseSaveResultResponse Save(bool nil = false)
        {
            int id = packetId++;
            if (nil)
                GetRegisteredRequestHandler(nameof(CourseSaveResultRequest)).Invoke(harness.Session,
                    new Packet.Request { Id = id, Name = nameof(CourseSaveResultRequest), Content = MessagePackSerializer.Serialize<object?>(null) });
            else
                InvokeRegisteredRequestHandler(nameof(CourseSaveResultRequest), harness.Session, id, new CourseSaveResultRequest());
            return Response<CourseSaveResultResponse>(id, nameof(CourseSaveResultResponse));
        }

        CourseGetRewardResponse Claim(List<int>? ids, int code, bool nil = false)
        {
            byte[] before = player.Course.ToBson();
            byte[] inventoryBefore = inventory.ToBson();
            byte[] characterBefore = character.ToBson();
            int id = packetId++;
            if (nil)
                GetRegisteredRequestHandler(nameof(CourseGetRewardRequest)).Invoke(harness.Session,
                    new Packet.Request { Id = id, Name = nameof(CourseGetRewardRequest), Content = MessagePackSerializer.Serialize<object?>(null) });
            else
                InvokeRegisteredRequestHandler(nameof(CourseGetRewardRequest), harness.Session, id, new CourseGetRewardRequest { RewardIds = ids });
            CourseGetRewardResponse response = Response<CourseGetRewardResponse>(id, nameof(CourseGetRewardResponse), code == 0, true);
            AssertEqual(code, response.Code, "Course reward code");
            if (code != 0)
            {
                AssertEqual(0, response.SuccessRewardIds.Count, "Rejected reward has no successful ids");
                AssertEqual(Convert.ToHexString(before), Convert.ToHexString(player.Course.ToBson()), "Rejected reward preserves course state");
                AssertEqual(Convert.ToHexString(inventoryBefore), Convert.ToHexString(inventory.ToBson()), "Rejected reward preserves inventory");
                AssertEqual(Convert.ToHexString(characterBefore), Convert.ToHexString(character.ToBson()), "Rejected reward preserves roster");
            }
            return response;
        }

        void Fight(int stageId, int stars)
        {
            byte[] saved = MessagePackSerializer.Serialize(Login());
            int id = packetId++;
            InvokeRegisteredRequestHandler(nameof(PreFightRequest), harness.Session, id, new PreFightRequest
            {
                PreFightData = new() { StageId = (uint)stageId, ChallengeCount = 1, CardIds = null, RobotIds = null, CaptainPos = 1, FirstFightPos = 1 }
            });
            PreFightResponse preFight = Response<PreFightResponse>(id, nameof(PreFightResponse));
            AssertEqual(0, preFight.Code, "Course real pre-fight accepts unlocked stage");
            FightSettleRequest request = CreateMissingStageSettleRequest((uint)stageId, preFight.FightData.FightId, uid);
            request.Result.AddStars = stars;
            request.Result.LeftTime = 0;
            id = packetId++;
            InvokeRegisteredRequestHandler(nameof(FightSettleRequest), harness.Session, id, request);
            AssertEqual(0, Response<FightSettleResponse>(id, nameof(FightSettleResponse), true).Code, "Course authenticated settle succeeds");
            AssertEqual(stageId, player.Course.PendingResult?.Id ?? 0, "Accepted fight persists pending stage");
            AssertEqual(stars, player.Course.PendingResult?.StarsFlag ?? -1, "Pending result preserves current attempt mask");
            AssertEqual(Convert.ToHexString(saved), Convert.ToHexString(MessagePackSerializer.Serialize(Login())), "Pending fight is not saved course progression");
        }

        void RejectPreFight(int stageId, int code)
        {
            byte[] before = player.Course.ToBson();
            int id = packetId++;
            InvokeRegisteredRequestHandler(nameof(PreFightRequest), harness.Session, id, new PreFightRequest
            {
                PreFightData = new() { StageId = (uint)stageId, ChallengeCount = 1, CardIds = null, RobotIds = null, CaptainPos = 1, FirstFightPos = 1 }
            });
            AssertEqual(code, Response<PreFightResponse>(id, nameof(PreFightResponse)).Code, "Course pre-fight gate rejects");
            AssertEqual(Convert.ToHexString(before), Convert.ToHexString(player.Course.ToBson()), "Rejected pre-fight preserves saved and pending course state");
        }

        void RejectFightResult(int stageId, bool wrongFightId)
        {
            int id = packetId++;
            InvokeRegisteredRequestHandler(nameof(PreFightRequest), harness.Session, id, new PreFightRequest
            {
                PreFightData = new() { StageId = (uint)stageId, ChallengeCount = 1, CardIds = null, RobotIds = null, CaptainPos = 1, FirstFightPos = 1 }
            });
            PreFightResponse started = Response<PreFightResponse>(id, nameof(PreFightResponse));
            AssertEqual(0, started.Code, "Invalid-result probe starts an authorized course fight");
            FightSettleRequest request = CreateMissingStageSettleRequest((uint)stageId, started.FightData.FightId, uid);
            request.Result.AddStars = wrongFightId ? Mask(stageId) : 1 << stages[stageId].StarPoint.Count;
            if (wrongFightId) request.Result.FightId++;
            byte[] before = player.Course.ToBson();
            id = packetId++;
            InvokeRegisteredRequestHandler(nameof(FightSettleRequest), harness.Session, id, request);
            AssertEqual(true, Response<FightSettleResponse>(id, nameof(FightSettleResponse)).Code != 0,
                wrongFightId ? "Mismatched fight id rejects" : "Unconfigured course star bit rejects");
            AssertEqual(Convert.ToHexString(before), Convert.ToHexString(player.Course.ToBson()), "Rejected settle cannot authorize pending result");
            AssertEqual(20175007, Save().Code, "Rejected settle cannot be saved");
        }

        AssertEqual(20175007, Save().Code, "Missing result cannot save");
        AssertEqual(20175007, Save(true).Code, "Nil request cannot invent a result");
        Claim(null, 20175008, true);
        Claim(null, 20175008);
        Claim([], 20175008);
        Claim([int.MaxValue], 20175008);
        Claim([reward.Id, reward.Id], 20175008);
        Claim([reward.Id], 20175010);

        long unlockedLevel = player.PlayerData.Level;
        player.PlayerData.Level = 0;
        RejectPreFight(lesson.StageIds[0], 20175002);
        player.PlayerData.Level = unlockedLevel;
        RejectPreFight(exam.StageIds[0], 20175003);
        RejectPreFight(lesson.StageIds.First(id => stages[id].PrevStageId is > 0), 20175006);
        RejectFightResult(lesson.StageIds[0], true);
        RejectFightResult(lesson.StageIds[0], false);

        int firstStage = lesson.StageIds[0];
        Fight(firstStage, Mask(firstStage));
        Claim([reward.Id], 20175010);
        player = BsonSerializer.Deserialize<Player>(player.ToBson());
        harness.Session.player = player;
        AssertEqual(firstStage, player.Course.PendingResult?.Id ?? 0, "Pending lesson result survives BSON relog");
        byte[] blockedLesson = player.Course.ToBson();
        AssertEqual(1, Save(true).Code, "Lesson save blocks while retail completion evidence is unavailable");
        AssertEqual(Convert.ToHexString(blockedLesson), Convert.ToHexString(player.Course.ToBson()),
            "Blocked lesson save retains pending result without mutating saved progression");
        AssertEqual(0, Login().Data.ChapterDataList.Count, "Login does not invent lesson chapter completion");

        // Explicit persisted earned-state fixture: lesson saving is blocked, not simulated or authorized here.
        player.Course.PendingResult = null;
        player.Course.Stages = [new CourseStageState { Id = firstStage, StarsFlag = 0 }];
        Claim([reward.Id], 20175011);
        player.Course.Stages = chapters.Where(row => row.StageType == 1).SelectMany(row => row.StageIds)
            .Select(id => new CourseStageState { Id = id, StarsFlag = Mask(id) }).ToList();
        int expectedLesson = player.Course.Stages.Sum(stage => stages[stage.Id].StarPoint.Sum());
        player.Course.MaxTotalLessonPoint = expectedLesson;
        player = BsonSerializer.Deserialize<Player>(player.ToBson());
        harness.Session.player = player;
        AssertEqual(expectedLesson, Login().Data.TotalLessonPoint, "Persisted lesson stars derive current points independently of completion evidence");
        AssertEqual(player.Course.Stages.Count, Login().Data.StageDataDict.Count, "Login preserves earned lesson stages");
        AssertEqual(0, Login().Data.ChapterDataList.Count, "Login omits unsupported lesson chapter clear flags");
        Claim([reward.Id, int.MaxValue], 20175008);
        CourseGetRewardResponse awarded = Claim([reward.Id], 0);
        AssertEqual(true, awarded.SuccessRewardIds.SequenceEqual([reward.Id]), "Reward response identifies exact successful claim");
        AssertEqual(true, awarded.RewardGoodsList.Any(goods => goods.Count > 0), "Valid course claim returns actual goods");
        AssertEqual(true, player.Course.RewardIds.Contains(reward.Id), "Award claim persists");
        player = BsonSerializer.Deserialize<Player>(player.ToBson());
        harness.Session.player = player;
        AssertEqual(true, Login().Data.RewardIds.Contains(reward.Id), "Award restored in login data after BSON relog");
        AssertEqual(expectedLesson, Login().Data.MaxTotalLessonPoint, "Historical total restored after BSON relog");
        Claim([reward.Id], 20175009);

        int examClearPoint = exam.ClearPoint ?? throw new InvalidDataException("Exam requires authoritative ClearPoint.");
        CourseChapterTable dependentExam = chapters.First(row => row.StageType == 2 && row.PrevChapterIds == exam.ChapterId);
        RejectPreFight(dependentExam.StageIds[0], 20175004);
        int examPoints = 0;
        for (int index = 0; index < exam.StageIds.Count; index++)
        {
            int stageId = exam.StageIds[index];
            Fight(stageId, index == exam.StageIds.Count - 1 ? 0 : Mask(stageId));
            CourseSaveResultResponse saved = Save();
            if (index != exam.StageIds.Count - 1) examPoints += stages[stageId].StarPoint.Sum();
            AssertEqual(examPoints, saved.ChapterData!.TotalPoint, "Exam distinct stage point accumulation");
            AssertEqual(false, saved.ChapterData.IsClear, "Exam needs every stage and ClearPoint");
            AssertEqual(expectedLesson, saved.TotalLessonPoint, "Exam points do not inflate lesson total");
            AssertEqual(false, IsComplete(exam.ChapterId), "Incomplete exam cannot satisfy mission chapter");
            if (index == 0)
            {
                Fight(stageId, 0);
                CourseSaveResultResponse downgrade = Save();
                AssertEqual(0, downgrade.StageData!.StarsFlag, "Unmastered exam retry replaces saved stars rather than ORing");
                AssertEqual(0, downgrade.ChapterData!.TotalPoint, "Unmastered exam downgrade lowers current chapter points");
                AssertEqual(expectedLesson, downgrade.MaxTotalLessonPoint, "Exam downgrade preserves historical lesson maximum");
                AssertEqual(1, player.Course.Stages.Count(stage => exam.StageIds.Contains(stage.Id)),
                    "Repeated exam stage cannot substitute for distinct chapter stages");
                Fight(stageId, Mask(stageId));
                AssertEqual(examPoints, Save().ChapterData!.TotalPoint, "Exam retry restores only its own configured points");
            }
        }
        AssertEqual(true, examPoints < examClearPoint, "All saved exam stages remain below mandatory ClearPoint");
        int finalStage = exam.StageIds[^1];
        Fight(finalStage, Mask(finalStage));
        CourseSaveResultResponse cleared = Save();
        AssertEqual(examPoints + stages[finalStage].StarPoint.Sum(), cleared.ChapterData!.TotalPoint, "Final exam retry replaces zero-star result");
        AssertEqual(true, cleared.ChapterData.TotalPoint >= examClearPoint && cleared.ChapterData.IsClear, "Distinct saved exam stages reach ClearPoint");
        AssertEqual(true, IsComplete(exam.ChapterId), "Complete exam satisfies mission chapter predicate");
        Fight(finalStage, 0);
        CourseSaveResultResponse masteredRetry = Save();
        AssertEqual(Mask(finalStage), masteredRetry.StageData!.StarsFlag, "Fully mastered chapter preserves saved stars on a worse retry");
        AssertEqual(cleared.ChapterData.TotalPoint, masteredRetry.ChapterData!.TotalPoint, "Fully mastered chapter preserves points");
        AssertEqual(true, masteredRetry.ChapterData.IsClear, "Fully mastered exam preserves clear flag");
        AssertEqual(20175007, Save().Code, "Mastered retry consumes pending result");
        CourseRewardTable examReward = TableReaderV2.Parse<CourseRewardTable>()
            .Where(row => row.ChapterId == exam.ChapterId).OrderBy(row => row.Point).First();
        AssertEqual(true, Claim([examReward.Id], 0).SuccessRewardIds.SequenceEqual([examReward.Id]),
            "Saved real exam progression authorizes exact reward");
        Claim([examReward.Id], 20175009);
        player = BsonSerializer.Deserialize<Player>(player.ToBson());
        harness.Session.player = player;
        AssertEqual(true, Login().Data.ChapterDataList.Single(row => row.Id == exam.ChapterId).IsClear, "Exam completion survives BSON relog");
        int dependentPoints = 0;
        for (int index = 0; index < dependentExam.StageIds.Count; index++)
        {
            int stageId = dependentExam.StageIds[index];
            Fight(stageId, Mask(stageId));
            AssertEqual(false, IsComplete(dependentExam.ChapterId), "Pending dependent exam stage cannot complete chapter");
            CourseSaveResultResponse saved = Save();
            dependentPoints += stages[stageId].StarPoint.Sum();
            AssertEqual(dependentPoints, saved.ChapterData!.TotalPoint, "Dependent exam accumulates distinct saved stage points");
            AssertEqual(index == dependentExam.StageIds.Count - 1 && dependentPoints >= dependentExam.ClearPoint,
                saved.ChapterData.IsClear, "Dependent exam requires all saved stages and configured threshold");
            AssertEqual(index == dependentExam.StageIds.Count - 1, IsComplete(dependentExam.ChapterId),
                "Dependent exam mission predicate rejects one stage and completes full chapter");
        }
        player = BsonSerializer.Deserialize<Player>(player.ToBson());
        harness.Session.player = player;
        AssertEqual(true, Login().Data.ChapterDataList.Single(row => row.Id == dependentExam.ChapterId).IsClear,
            "Dependent exam completion survives BSON relog");
        Console.WriteLine("Course compatibility: blocked unsupported lesson save, persisted lesson fixtures, real exams, pending/save, downgrade, rewards and BSON relog passed.");
    }
}
