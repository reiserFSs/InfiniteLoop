using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Game;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.exhibition;
using AscNet.Table.V2.share.fuben;
using AscNet.Table.V2.share.fuben.charactertower;
using AscNet.Table.V2.share.item;
using AscNet.Table.V2.share.reward;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Newtonsoft.Json.Linq;
using System.Reflection;
using Character = AscNet.Common.Database.Character;
using Inventory = AscNet.Common.Database.Inventory;

namespace AscNet.Test;

internal partial class Program
{
    // EN client bytes/share/text/CodeText.json keys 20178001..20178010 (CharacterTower*), restated here so this
    // suite is an independent oracle instead of a mirror of the module's own internal constants.
    private const int CharacterTowerConfigNotFound = 20178001;
    private const int CharacterTowerRelationConditionNotEnough = 20178002;
    private const int CharacterTowerStageInfoError = 20178003;
    private const int CharacterTowerStageRewardHadGot = 20178004;
    private const int CharacterTowerStageNotPass = 20178005;
    private const int CharacterTowerStarRewardHadGot = 20178006;
    private const int CharacterTowerStarNotEnough = 20178007;
    private const int CharacterTowerChapterRewardHadGot = 20178008;
    private const int CharacterTowerFinishStageNotEnough = 20178009;
    private const int CharacterTowerTriggerConditionExist = 20178010;
    // FightModule.FightAuthorizationError and TowerModule.CombatAuthorizationError share this value.
    private const int CharacterTowerCombatAuthorizationError = 1033;

    // Authored Arcade Anima membership (EN share/fuben/charactertower/*). ValidateCharacterTowerAuthoredFixture
    // re-derives every one of these from the imported tables before the behavioural checks use them.
    private const int CharacterTowerWatanabeCharacterId = 1081002;
    private const int CharacterTowerVeraCharacterId = 1131003;
    private const int CharacterTowerArcaCharacterId = 1571003;
    private const int CharacterTowerWatanabeStoryChapterId = 100111;
    private const int CharacterTowerWatanabeChallengeChapterId = 100121;
    private const int CharacterTowerVeraStoryChapterId = 100611;
    private const int CharacterTowerVeraChallengeChapterId = 100621;
    private const int CharacterTowerArcaStoryChapterId = 100511;
    private const int CharacterTowerArcaChallengeChapterId = 100521;
    private const int CharacterTowerWatanabeRelationId = 100121;
    private const int CharacterTowerVeraRelationId = 100621;
    private const int CharacterTowerWatanabeFirstStageId = 21000100;
    private const int CharacterTowerWatanabeStoryFirstStageId = 21000110;
    private const int CharacterTowerWatanabeRobotStageId = 21000111;
    private const int CharacterTowerArcaChallengeFirstStageId = 21000115;
    private const int CharacterTowerVeraChallengeFirstStageId = 21000260;
    private const int CharacterTowerVeraTypedStoryChallengeStageId = 21000264;
    private const int CharacterTowerWatanabeTreasure5Stars = 1000002;
    private const int CharacterTowerWatanabeTreasure10Stars = 1000003;
    private const int CharacterTowerWatanabeTreasure15Stars = 1000004;
    private const int CharacterTowerArcaTreasure5Stars = 1004002;

    /// <summary>
    /// Arcade Anima (CharacterTower) compatibility: the seven registered requests over the real encrypted
    /// loopback session, the native login/claim/reward field shapes, chapter and relation gates, the shared
    /// fight pipeline boundaries and durable claim/star recovery.
    /// </summary>
    private static void ValidateCharacterTowerCompatibility()
    {
        ValidateCharacterTowerAuthoredFixture();
        ValidateCharacterTowerProtocolSurface();
        ValidateCharacterTowerLoginProjection();
        ValidateCharacterTowerChapterGates();
        ValidateCharacterTowerConditionSources();
        ValidateCharacterTowerRelationProgress();
        ValidateCharacterTowerCombatTrust();
        ValidateCharacterTowerClaimTrust();
        ValidateCharacterTowerProtectionBoundaries();
        Console.WriteLine("CharacterTower compatibility: seven mode RPCs, login projection, chapter/relation gates, Arcade combat trust, manual claims and recovery passed.");
    }

    /// <summary>Guards the authored expectations this suite is built on; a table edit fails loudly here first.</summary>
    private static void ValidateCharacterTowerAuthoredFixture()
    {
        List<CharacterTowerTable> towers = TableReaderV2.Parse<CharacterTowerTable>();
        List<CharacterTowerChapterTable> chapters = TableReaderV2.Parse<CharacterTowerChapterTable>();
        List<CharacterTowerTreasureTable> treasures = TableReaderV2.Parse<CharacterTowerTreasureTable>();
        Dictionary<int, StageTable> stages = TableReaderV2.Parse<StageTable>().ToDictionary(row => row.StageId);
        HashSet<int> conditionIds = TableReaderV2.Parse<AscNet.Table.V2.share.condition.ConditionTable>()
            .Select(row => row.Id).ToHashSet();

        AssertEqual(6, towers.Count, "Arcade towers");
        AssertEqual(6, chapters.Count(chapter => chapter.Type == 1), "Arcade story chapters");
        AssertEqual(6, chapters.Count(chapter => chapter.Type == 2), "Arcade challenge chapters");
        foreach (CharacterTowerTable tower in towers)
        {
            AssertEqual(true, tower.OpenTimeId > 0 && ActivityScheduleService.IsOpen(tower.OpenTimeId, DateTimeOffset.UtcNow),
                $"Arcade tower {tower.Id} unbounded window {tower.OpenTimeId}");
            AssertEqual(2, tower.ChapterIds.Count(chapterId => chapterId > 0), $"Arcade tower {tower.Id} chapter pair");
        }

        CharacterTowerChapterTable watanabeStory = TowerChapter(CharacterTowerWatanabeStoryChapterId);
        CharacterTowerChapterTable watanabeChallenge = TowerChapter(CharacterTowerWatanabeChallengeChapterId);
        CharacterTowerChapterTable veraChallenge = TowerChapter(CharacterTowerVeraChallengeChapterId);
        CharacterTowerChapterTable arcaChallenge = TowerChapter(CharacterTowerArcaChallengeChapterId);
        AssertEqual(CharacterTowerWatanabeCharacterId, watanabeStory.CharacterId, "Watanabe story actor");
        AssertEqual(CharacterTowerWatanabeCharacterId, watanabeChallenge.CharacterId, "Watanabe challenge actor");
        AssertEqual(CharacterTowerVeraCharacterId, veraChallenge.CharacterId, "Vera challenge actor");
        AssertEqual(CharacterTowerArcaCharacterId, arcaChallenge.CharacterId, "Arca challenge actor");
        AssertEqual(1, watanabeStory.Type, "story chapter type");
        AssertEqual(2, watanabeChallenge.Type, "challenge chapter type");
        AssertEqual(0, watanabeStory.RelationGroupId.GetValueOrDefault(), "story chapters own no relation group");
        AssertEqual(CharacterTowerWatanabeChallengeChapterId, watanabeChallenge.RelationGroupId.GetValueOrDefault(), "challenge relation group");
        AssertEqual(CharacterTowerVeraChallengeChapterId, veraChallenge.RelationGroupId.GetValueOrDefault(), "Vera relation group");
        AssertIntegerList([21000110, 21000111, 21000112, 21000113, 21000114],
            watanabeStory.StageIds.Where(id => id > 0).Select(id => (long)id).ToArray(), "authored Watanabe story stage order");
        AssertIntegerList([21000100, 21000101, 21000102, 21000103, 21000104],
            watanabeChallenge.StageIds.Where(id => id > 0).Select(id => (long)id).ToArray(), "authored Watanabe challenge stage order");
        AssertEqual(true, watanabeStory.ChapterRewardId > 0 && watanabeChallenge.ChapterRewardId <= 0,
            "only story chapters author a final gift");
        AssertIntegerList([CharacterTowerWatanabeTreasure5Stars, CharacterTowerWatanabeTreasure10Stars, CharacterTowerWatanabeTreasure15Stars],
            watanabeChallenge.TreasureId.Where(id => id > 0).Select(id => (long)id).ToArray(), "authored Watanabe treasure order");
        AssertIntegerList([1005002, 1005003, 1005004],
            veraChallenge.TreasureId.Where(id => id > 0).Select(id => (long)id).ToArray(), "authored Vera treasure order");
        AssertEqual(30, TowerAuthoredLevel(CharacterTowerWatanabeChallengeChapterId), "Watanabe chapter Commandant gate");
        AssertEqual(30, TowerAuthoredLevel(CharacterTowerVeraChallengeChapterId), "Vera chapter Commandant gate");
        AssertEqual(52, TowerAuthoredLevel(CharacterTowerArcaChallengeChapterId), "Noan/Arca challenge Commandant gate");
        AssertEqual(52, TowerAuthoredLevel(CharacterTowerArcaStoryChapterId), "Noan/Arca story Commandant gate");

        CharacterTowerRelationTable watanabeRelation = TowerRelation(CharacterTowerWatanabeRelationId);
        AssertIntegerList([1, 2, 3, 4, 5, 6], watanabeRelation.FinishNums.Select(value => (long)value).ToArray(),
            "authored relation tier thresholds");
        AssertIntegerList([2260101, -1, 2260102, -1, 2260103, -1],
            watanabeRelation.FightEventIds.Select(value => (long)value).ToArray(), "authored relation fight event tiers");
        AssertEqual(true, watanabeRelation.StoryIds[0] == "-1" && watanabeRelation.StoryIds[1].Length > 0,
            "authored relation story tiers use the typed sentinel");
        AssertEqual(6, watanabeRelation.Conditions.Count(conditionId => conditionId > 0), "authored relation condition count");
        foreach (int relationId in new[] { CharacterTowerWatanabeRelationId, CharacterTowerVeraRelationId })
        {
            CharacterTowerRelationTable relation = TowerRelation(relationId);
            foreach (int conditionId in relation.Conditions.Where(id => id > 0))
                AssertEqual(true, conditionIds.Contains(conditionId), $"relation {relationId} condition {conditionId} is authored");
            AssertEqual(6, relation.FightEventIds.Count, $"relation {relationId} tier count");
            AssertEqual(relation.FightEventIds.Count, relation.FinishNums.Count, $"relation {relationId} threshold count");
            AssertEqual(relation.FightEventIds.Count, relation.StoryIds.Count, $"relation {relationId} story tier count");
        }

        foreach (CharacterTowerChapterTable chapter in chapters.Where(chapter => chapter.RelationGroupId is > 0))
        {
            AssertEqual(chapter.Id, chapter.RelationGroupId!.Value, $"challenge chapter {chapter.Id} owns its relation group");
            foreach (int stageId in chapter.StageIds.Where(id => id > 0))
            {
                AssertEqual(74, Convert.ToInt32(stages[stageId].Type), $"Arcade stage {stageId} runtime Type");
                AssertEqual(true, stages[stageId].FirstRewardShow is > 0, $"Arcade stage {stageId} manual first reward");
            }
        }

        AssertEqual(1, stages[CharacterTowerWatanabeRobotStageId].StageType, "authored story-chapter combat row keeps combat visual type");
        AssertEqual(true, stages[CharacterTowerWatanabeRobotStageId].RobotId.Any(robotId => robotId > 0), "authored fixed-robot story stage");
        AssertEqual(2, stages[CharacterTowerVeraTypedStoryChallengeStageId].StageType,
            "authored 21000264 anomaly: challenge row carries the story visual type");
        AssertEqual(true, Convert.ToInt32(stages[CharacterTowerVeraTypedStoryChallengeStageId].Restartable) != 0,
            "authored challenge restartability");
        AssertEqual(true, stages[CharacterTowerWatanabeFirstStageId].ForceConditionId.Any(id => id > 0), "authored challenge force condition");
        AssertIntegerList([5, 10, 15], treasures
            .Where(treasure => treasure.TreasureId is CharacterTowerWatanabeTreasure5Stars or CharacterTowerWatanabeTreasure10Stars or CharacterTowerWatanabeTreasure15Stars)
            .OrderBy(treasure => treasure.TreasureId).Select(treasure => (long)treasure.RequireStar).ToArray(),
            "authored treasure thresholds");
    }

    /// <summary>
    /// All seven registered requests over the real socket: correlated packet id/name, the native response fields
    /// the client actually consumes, integer-only list payloads and a per-session channel that never answers
    /// another session.
    /// </summary>
    private static void ValidateCharacterTowerProtocolSurface()
    {
        using CharacterTowerCase test = new("protocol-surface", 30, [CharacterTowerWatanabeCharacterId]);
        CharacterData watanabe = test.Owned(CharacterTowerWatanabeCharacterId);
        watanabe.Level = 80; // live 10436013 only: TrustLv stays 1 until the tip probe
        foreach (int stageId in new[] { 21000110, 21000111, 21000112, 21000113, 21000114 })
            test.SeedStage(stageId, passed: true, starsMark: 0);
        foreach (int stageId in new[] { 21000100, 21000101, 21000102, 21000103, 21000104 })
            test.SeedStage(stageId, passed: true, starsMark: 1);

        // CharacterTowerGetStageRewardRequest: manual claim of the authored stage first reward.
        Dictionary<int, long> balancesBefore = TowerBalances(test.Session);
        JObject stageReward = test.Call(nameof(CharacterTowerGetStageRewardRequest),
            new CharacterTowerGetStageRewardRequest { StageId = CharacterTowerWatanabeFirstStageId }, 0, packetId: 5_130_001);
        TowerAssertRequiredFields(stageReward, "CharacterTowerGetStageRewardResponse", "Code", "Rewards");
        TowerAssertRewards(stageReward, 16316, "stage reward");
        TowerAssertItemGoods(test.Session, balancesBefore, 16316, "stage reward");
        AssertEqual(true, test.Pushes.Any(push => push.Name == nameof(NotifyItemDataList)), "stage reward pushes the ownership delta");
        byte[] afterStageClaim = test.Session.inventory.ToBson();
        test.Reject(nameof(CharacterTowerGetStageRewardRequest), new CharacterTowerGetStageRewardRequest { StageId = CharacterTowerWatanabeFirstStageId },
            CharacterTowerStageRewardHadGot, "duplicate stage reward");
        AssertEqual(true, afterStageClaim.SequenceEqual(test.Session.inventory.ToBson()), "a duplicate claim cannot pay twice");

        // CharacterTowerGetChapterRewardRequest: the final gift needs every rewarded stage claim of the chapter.
        foreach (int stageId in new[] { 21000110, 21000111, 21000112, 21000113 })
            test.Call(nameof(CharacterTowerGetStageRewardRequest), new CharacterTowerGetStageRewardRequest { StageId = stageId }, 0);
        test.Reject(nameof(CharacterTowerGetChapterRewardRequest), new CharacterTowerGetChapterRewardRequest { ChapterId = CharacterTowerWatanabeStoryChapterId },
            CharacterTowerFinishStageNotEnough, "chapter gift before the last stage claim");
        test.Call(nameof(CharacterTowerGetStageRewardRequest), new CharacterTowerGetStageRewardRequest { StageId = 21000114 }, 0);
        JObject chapterReward = test.Call(nameof(CharacterTowerGetChapterRewardRequest),
            new CharacterTowerGetChapterRewardRequest { ChapterId = CharacterTowerWatanabeStoryChapterId }, 0);
        TowerAssertRequiredFields(chapterReward, "CharacterTowerGetChapterRewardResponse", "Code", "Rewards");
        TowerAssertRewards(chapterReward, TowerChapter(CharacterTowerWatanabeStoryChapterId).ChapterRewardId, "chapter reward");
        AssertEqual(true, test.Session.character.ScoreTitles.Any(title => title.Id == 13019204), "the chapter keepsake grants its authored collection");
        test.Reject(nameof(CharacterTowerGetChapterRewardRequest), new CharacterTowerGetChapterRewardRequest { ChapterId = CharacterTowerWatanabeStoryChapterId },
            CharacterTowerChapterRewardHadGot, "duplicate chapter gift");
        test.Reject(nameof(CharacterTowerGetChapterRewardRequest), new CharacterTowerGetChapterRewardRequest { ChapterId = CharacterTowerWatanabeChallengeChapterId },
            CharacterTowerConfigNotFound, "challenge chapters author no final gift");

        // CharacterTowerGetStarRewardRequest: the authored 5-star boundary of the chapter's first treasure.
        CharacterTowerTreasureTable treasure = TowerTreasure(CharacterTowerWatanabeTreasure5Stars);
        AssertEqual(5, treasure.RequireStar, "authored first treasure threshold");
        JObject starReward = test.Call(nameof(CharacterTowerGetStarRewardRequest),
            new CharacterTowerGetStarRewardRequest { TreasureId = CharacterTowerWatanabeTreasure5Stars }, 0);
        TowerAssertRequiredFields(starReward, "CharacterTowerGetStarRewardResponse", "Code", "Rewards");
        TowerAssertRewards(starReward, treasure.RewardId, "star treasure reward");
        AssertEqual(true, test.Session.player.HeadPortraits.Any(head => head.Id == 9012001), "the treasure grants its authored portrait");
        AssertEqual(true, test.Pushes.Any(push => push.Name == nameof(NotifyHeadPortraitInfos)), "the treasure pushes portrait ownership");
        test.Reject(nameof(CharacterTowerGetStarRewardRequest), new CharacterTowerGetStarRewardRequest { TreasureId = CharacterTowerWatanabeTreasure5Stars },
            CharacterTowerStarRewardHadGot, "duplicate star treasure");

        // CharacterTowerActivateFightEventIdRequest: integer FinishConditions payload, never a story echo.
        JObject activation = test.Call(nameof(CharacterTowerActivateFightEventIdRequest),
            new CharacterTowerActivateFightEventIdRequest { RelationId = CharacterTowerWatanabeRelationId, FightEventId = 2260101 }, 0);
        TowerAssertRequiredFields(activation, "CharacterTowerActivateFightEventIdResponse", "Code", "FinishConditions");
        TowerAssertArray(activation["FinishConditions"], JTokenType.Integer, "activation FinishConditions");
        AssertIntegerList([10436013], activation["FinishConditions"]!.Values<int>().Select(value => (long)value).ToArray(),
            "activation freezes exactly the live character-level condition");

        // CharacterTowerSaveStoryIdRequest: string StoryId on the wire, integer FinishConditions back.
        test.SeedStage(10040203, passed: true, starsMark: 0);
        JObject storySave = test.Call(nameof(CharacterTowerSaveStoryIdRequest),
            new CharacterTowerSaveStoryIdRequest { RelationId = CharacterTowerWatanabeRelationId, StoryId = "JS00106BA" }, 0);
        TowerAssertRequiredFields(storySave, "CharacterTowerSaveStoryIdResponse", "Code", "FinishConditions");
        TowerAssertArray(storySave["FinishConditions"], JTokenType.Integer, "story save FinishConditions");
        AssertIntegerList([10436013, 10436014], storySave["FinishConditions"]!.Values<int>().Select(value => (long)value).ToArray(),
            "story save freezes the frozen-or-live conditions at their authored order");

        // CharacterTowerSaveVideoStageIdRequest / CharacterTowerSaveTriggerConditionIdRequest answer Code only.
        JObject videoSave = test.Call(nameof(CharacterTowerSaveVideoStageIdRequest),
            new CharacterTowerSaveVideoStageIdRequest { StageId = CharacterTowerWatanabeStoryFirstStageId }, 0);
        TowerAssertRequiredFields(videoSave, "CharacterTowerSaveVideoStageIdResponse", "Code");
        AssertEqual(true, test.ChapterState(CharacterTowerWatanabeStoryChapterId)!.VideoedIds.Contains(CharacterTowerWatanabeStoryFirstStageId),
            "the video marker records under the owning story chapter");
        test.Owned(CharacterTowerWatanabeCharacterId).TrustLv = 5; // rewards replace roster entries; mutate the current actor
        JObject triggerSave = test.Call(nameof(CharacterTowerSaveTriggerConditionIdRequest),
            new CharacterTowerSaveTriggerConditionIdRequest { ChapterId = CharacterTowerWatanabeChallengeChapterId, ConditionId = 10436015 }, 0);
        TowerAssertRequiredFields(triggerSave, "CharacterTowerSaveTriggerConditionIdResponse", "Code");
        CharacterTowerRelationState relation = test.RelationState(CharacterTowerWatanabeChallengeChapterId, CharacterTowerWatanabeRelationId)!;
        AssertIntegerList([10436013, 10436014], relation.FinishConditions.Select(value => (long)value).ToArray(),
            "a tip acknowledgement never freezes a condition");
        AssertIntegerList([2260101], relation.FightEventIds.Select(value => (long)value).ToArray(), "activation is recorded once");
        TowerAssertStrings(["JS00106BA"], relation.StoryIds, "the story id keeps its string identity");
        AssertIntegerList([CharacterTowerWatanabeStoryFirstStageId],
            test.ChapterState(CharacterTowerWatanabeStoryChapterId)!.VideoedIds.Select(value => (long)value).ToArray(), "video marker");

        // Packet id and channel correlation: the response echoes the client id and only its own socket sees it.
        using CharacterTowerCase bystander = new("protocol-bystander", 30, [CharacterTowerWatanabeCharacterId]);
        _ = test.Call(nameof(CharacterTowerGetStageRewardRequest),
            new CharacterTowerGetStageRewardRequest { StageId = CharacterTowerWatanabeFirstStageId }, CharacterTowerStageRewardHadGot, packetId: 7);
        AssertEqual(false, bystander.Harness.TryReadAvailablePacket("CharacterTower cross-session packet", out _),
            "an Arcade request answers only its own session channel");
    }

    /// <summary>
    /// Login projection: the real AccountModule login emits the push, a progressed account round-trips through
    /// fresh BSON, and every nested list keeps the native client's typed non-null shape.
    /// </summary>
    private static void ValidateCharacterTowerLoginProjection()
    {
        // Fresh account: the real login sequence must publish the mode push with typed empty containers.
        using (MongoCollectionOverride mongo = MongoCollectionOverride.InstallForShopCompatibility())
        {
            const long playerId = 88_940;
            Character character = CreateDrawCompatibilityCharacter(playerId);
            character.Characters.Add(CreateLoginAccountCompatibilityCharacter(1021001, fashionId: 3021001));
            Player player = CreateDrawCompatibilityPlayer(playerId);
            player.PlayerData.LastLoginTime = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds();
            Inventory inventory = CreateDrawCompatibilityInventory(playerId, [new Item { Id = Inventory.Coin, Count = 12_345 }]);
            using LoopbackSessionHarness harness = new(character, player, inventory, "character-tower-fresh-login");
            harness.Session.stage = CreateLoginAccountCompatibilityStage(playerId);

            RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule"), "DoLogin",
                BindingFlags.Static | BindingFlags.NonPublic, [typeof(Session)]).Invoke(null, [harness.Session]);

            JObject? loginPush = null;
            for (int index = 0; index < 256 && loginPush is null; index++)
            {
                Packet.Push push = TowerReadPush(harness, $"CharacterTower fresh login push {index + 1}");
                if (push.Name == "NotifyLoginCharacterTowerData")
                    loginPush = JObject.Parse(MessagePackSerializer.ConvertToJson(push.Content));
            }
            if (loginPush is null)
                throw new InvalidDataException("AccountModule.DoLogin must emit NotifyLoginCharacterTowerData during login.");
            TowerAssertRequiredFields(loginPush, "NotifyLoginCharacterTowerData", "CharacterTowerDataDb", "ActivityChapters");
            JObject data = loginPush["CharacterTowerDataDb"] as JObject
                ?? throw new InvalidDataException("NotifyLoginCharacterTowerData.CharacterTowerDataDb must be a map.");
            TowerAssertRequiredFields(data, "CharacterTowerDataDb", "ChapterInfos");
            TowerAssertArray(data["ChapterInfos"], JTokenType.Object, "fresh ChapterInfos");
            AssertEqual(0, ((JArray)data["ChapterInfos"]!).Count,
                "a fresh account publishes typed empty ChapterInfos instead of null");
            TowerAssertArray(loginPush["ActivityChapters"], JTokenType.Integer, "ActivityChapters");
            AssertEqual(0, ((JArray)loginPush["ActivityChapters"]!).Count,
                "a permanent tower opening window is not a promotional chapter activity, so a fresh account reports no active chapter");
        }

        // Progressed account: the same push keeps its native types and survives a fresh-session BSON reload.
        using CharacterTowerCase test = new("login-projection", 30, [CharacterTowerWatanabeCharacterId]);
        CharacterData watanabe = test.Owned(CharacterTowerWatanabeCharacterId);
        watanabe.Level = 80;
        test.SeedStage(CharacterTowerWatanabeFirstStageId, passed: true, starsMark: 2);
        test.SeedStage(CharacterTowerWatanabeStoryFirstStageId, passed: true, starsMark: 0);
        test.Call(nameof(CharacterTowerGetStageRewardRequest), new CharacterTowerGetStageRewardRequest { StageId = CharacterTowerWatanabeFirstStageId }, 0);
        test.Call(nameof(CharacterTowerActivateFightEventIdRequest),
            new CharacterTowerActivateFightEventIdRequest { RelationId = CharacterTowerWatanabeRelationId, FightEventId = 2260101 }, 0);
        test.SeedStage(10040203, passed: true, starsMark: 0);
        test.Call(nameof(CharacterTowerSaveStoryIdRequest),
            new CharacterTowerSaveStoryIdRequest { RelationId = CharacterTowerWatanabeRelationId, StoryId = "JS00106BA" }, 0);
        test.Call(nameof(CharacterTowerSaveVideoStageIdRequest), new CharacterTowerSaveVideoStageIdRequest { StageId = CharacterTowerWatanabeStoryFirstStageId }, 0);
        test.Owned(CharacterTowerWatanabeCharacterId).TrustLv = 5;
        test.Call(nameof(CharacterTowerSaveTriggerConditionIdRequest),
            new CharacterTowerSaveTriggerConditionIdRequest { ChapterId = CharacterTowerWatanabeChallengeChapterId, ConditionId = 10436015 }, 0);

        JObject progressed = test.LoginPush();
        NotifyLoginCharacterTowerData typed = test.LoginData();
        AssertEqual(true, JToken.DeepEquals(progressed, JObject.Parse(MessagePackSerializer.ConvertToJson(MessagePackSerializer.Serialize(typed)))),
            "the emitted login push equals the module projection");
        TowerAssertChapterInfo(progressed, CharacterTowerWatanabeChallengeChapterId, "progressed login push");
        TowerAssertChapterInfo(progressed, CharacterTowerWatanabeStoryChapterId, "progressed login push");
        test.Relog("login-projection");
        AssertEqual(true, JToken.DeepEquals(progressed, test.LoginPush()),
            "a fresh session rebuilt from saved BSON projects the identical mode state");

        CharacterTowerChapterInfo challenge = typed.CharacterTowerDataDb.ChapterInfos
            .SingleOrDefault(chapter => chapter.ChapterId == CharacterTowerWatanabeChallengeChapterId)
            ?? throw new InvalidDataException("Progressed login push must carry the claimed challenge chapter.");
        AssertIntegerList([CharacterTowerWatanabeFirstStageId], challenge.StageRewardData.Select(value => (long)value).ToArray(), "durable stage claim");
        CharacterTowerRelationInfo relation = challenge.RelationInfos.Single(info => info.RelationId == CharacterTowerWatanabeRelationId);
        AssertIntegerList([2260101], relation.FightEventIds.Select(value => (long)value).ToArray(), "durable relation activation");
        TowerAssertStrings(["JS00106BA"], relation.StoryIds, "durable relation story id");
        AssertEqual(true, relation.FinishConditions.Count > 0, "durable frozen conditions");
        CharacterTowerChapterInfo storyChapter = typed.CharacterTowerDataDb.ChapterInfos
            .Single(chapter => chapter.ChapterId == CharacterTowerWatanabeStoryChapterId);
        AssertIntegerList([CharacterTowerWatanabeStoryFirstStageId], storyChapter.VideoedIds.Select(value => (long)value).ToArray(), "durable video marker");
        AssertIntegerList([10436015], challenge.TriggerConditions.Select(value => (long)value).ToArray(), "durable tip markers live on their own chapter");
    }

    /// <summary>
    /// Chapter ownership, level gates (Commandant 30 versus Noan's authored 52), predecessor stages, unknown ids
    /// and cross-chapter state isolation for every entry point that resolves a chapter from a wire id.
    /// </summary>
    private static void ValidateCharacterTowerChapterGates()
    {
        int arcaRequiredLevel = TowerAuthoredLevel(CharacterTowerArcaChallengeChapterId);
        using (CharacterTowerCase locked = new("gates-locked-arca", arcaRequiredLevel - 1,
            [CharacterTowerWatanabeCharacterId, CharacterTowerVeraCharacterId, CharacterTowerArcaCharacterId]))
        {
            locked.SeedStage(CharacterTowerArcaChallengeFirstStageId, passed: true, starsMark: 7);
            locked.SeedStage(21000120, passed: true, starsMark: 0);
            locked.Owned(CharacterTowerArcaCharacterId).Level = 80;
            locked.Reject(nameof(CharacterTowerGetStageRewardRequest), new CharacterTowerGetStageRewardRequest { StageId = CharacterTowerArcaChallengeFirstStageId },
                CharacterTowerStageInfoError, "locked Noan stage claim");
            locked.Reject(nameof(CharacterTowerGetChapterRewardRequest), new CharacterTowerGetChapterRewardRequest { ChapterId = CharacterTowerArcaChallengeChapterId },
                CharacterTowerConfigNotFound, "the Noan challenge chapter authors no final gift at any level");
            locked.Reject(nameof(CharacterTowerGetChapterRewardRequest), new CharacterTowerGetChapterRewardRequest { ChapterId = CharacterTowerArcaStoryChapterId },
                CharacterTowerStageInfoError, "locked Noan story chapter gift");
            locked.Reject(nameof(CharacterTowerGetStarRewardRequest), new CharacterTowerGetStarRewardRequest { TreasureId = CharacterTowerArcaTreasure5Stars },
                CharacterTowerStageInfoError, "locked Noan star treasure");
            locked.Reject(nameof(CharacterTowerActivateFightEventIdRequest),
                new CharacterTowerActivateFightEventIdRequest { RelationId = 100521, FightEventId = 2260501 },
                CharacterTowerStageInfoError, "locked Noan relation activation");
            locked.Reject(nameof(CharacterTowerSaveStoryIdRequest),
                new CharacterTowerSaveStoryIdRequest { RelationId = 100521, StoryId = "JS00506BA" },
                CharacterTowerStageInfoError, "locked Noan relation story");
            locked.Reject(nameof(CharacterTowerSaveTriggerConditionIdRequest),
                new CharacterTowerSaveTriggerConditionIdRequest { ChapterId = CharacterTowerArcaChallengeChapterId, ConditionId = 10436053 },
                CharacterTowerStageInfoError, "locked Noan condition tip");
            locked.Reject(nameof(CharacterTowerSaveVideoStageIdRequest), new CharacterTowerSaveVideoStageIdRequest { StageId = 21000120 },
                CharacterTowerStageInfoError, "locked Noan story card marker");
            // The open Watanabe chapter still gates on the stage's own pass state, not on the chapter.
            locked.Reject(nameof(CharacterTowerGetStageRewardRequest), new CharacterTowerGetStageRewardRequest { StageId = CharacterTowerWatanabeFirstStageId },
                CharacterTowerStageNotPass, "open chapter without a passed stage");
            // Video markers belong to story chapters only.
            locked.Reject(nameof(CharacterTowerSaveVideoStageIdRequest), new CharacterTowerSaveVideoStageIdRequest { StageId = CharacterTowerWatanabeFirstStageId },
                CharacterTowerStageInfoError, "challenge rows have no passed-card marker");
        }

        using (CharacterTowerCase open = new("gates-open-arca", arcaRequiredLevel,
            [CharacterTowerWatanabeCharacterId, CharacterTowerVeraCharacterId, CharacterTowerArcaCharacterId]))
        {
            open.SeedStage(21000120, passed: true, starsMark: 0);
            open.Reject(nameof(CharacterTowerGetStageRewardRequest), new CharacterTowerGetStageRewardRequest { StageId = CharacterTowerArcaChallengeFirstStageId },
                CharacterTowerStageNotPass, "Noan opens at the authored level but still needs the passed stage");
            open.Call(nameof(CharacterTowerSaveVideoStageIdRequest), new CharacterTowerSaveVideoStageIdRequest { StageId = 21000120 }, 0);
            AssertEqual(true, open.ChapterState(CharacterTowerArcaStoryChapterId)!.VideoedIds.Contains(21000120),
                "Noan story marker records once the authored level gate is met");
        }

        using (CharacterTowerCase unknown = new("gates-unknown-ids", 30, [CharacterTowerWatanabeCharacterId, CharacterTowerVeraCharacterId]))
        {
            unknown.Reject(nameof(CharacterTowerGetStageRewardRequest), new CharacterTowerGetStageRewardRequest { StageId = 999_999 },
                CharacterTowerStageInfoError, "unknown stage id");
            unknown.Reject(nameof(CharacterTowerSaveVideoStageIdRequest), new CharacterTowerSaveVideoStageIdRequest { StageId = 0 },
                CharacterTowerStageInfoError, "zero stage id");
            unknown.Reject(nameof(CharacterTowerGetChapterRewardRequest), new CharacterTowerGetChapterRewardRequest { ChapterId = 999_999 },
                CharacterTowerConfigNotFound, "unknown chapter id");
            unknown.Reject(nameof(CharacterTowerGetStarRewardRequest), new CharacterTowerGetStarRewardRequest { TreasureId = 999_999 },
                CharacterTowerConfigNotFound, "unknown treasure id");
            unknown.Reject(nameof(CharacterTowerActivateFightEventIdRequest),
                new CharacterTowerActivateFightEventIdRequest { RelationId = 999_999, FightEventId = 2260101 },
                CharacterTowerConfigNotFound, "unknown relation id");
            unknown.Reject(nameof(CharacterTowerSaveStoryIdRequest),
                new CharacterTowerSaveStoryIdRequest { RelationId = 999_999, StoryId = "JS00106BA" },
                CharacterTowerConfigNotFound, "unknown relation story id");
            unknown.Reject(nameof(CharacterTowerSaveTriggerConditionIdRequest),
                new CharacterTowerSaveTriggerConditionIdRequest { ChapterId = CharacterTowerWatanabeChallengeChapterId, ConditionId = 999_999 },
                CharacterTowerConfigNotFound, "unknown condition tip");
            unknown.Reject(nameof(CharacterTowerSaveTriggerConditionIdRequest),
                new CharacterTowerSaveTriggerConditionIdRequest { ChapterId = CharacterTowerWatanabeStoryChapterId, ConditionId = 10436013 },
                CharacterTowerConfigNotFound, "story chapters own no relation tips");
            unknown.Reject(nameof(CharacterTowerSaveTriggerConditionIdRequest),
                new CharacterTowerSaveTriggerConditionIdRequest { ChapterId = CharacterTowerWatanabeChallengeChapterId, ConditionId = 10436063 },
                CharacterTowerConfigNotFound, "another chapter's condition is not this relation's tip");

            // The stage's own pass state gates the claim; the predecessor gate is exercised by the fight and story entry points.
            unknown.Reject(nameof(CharacterTowerGetStageRewardRequest), new CharacterTowerGetStageRewardRequest { StageId = 21000101 },
                CharacterTowerStageNotPass, "claim before the stage itself is passed");
            unknown.SeedStage(21000101, passed: true, starsMark: 1);
            unknown.Call(nameof(CharacterTowerGetStageRewardRequest), new CharacterTowerGetStageRewardRequest { StageId = 21000101 }, 0);

            // Ownership resolution: each claim records under its own chapter and never touches the other's state.
            unknown.SeedStage(CharacterTowerVeraChallengeFirstStageId, passed: true, starsMark: 1);
            unknown.Call(nameof(CharacterTowerGetStageRewardRequest), new CharacterTowerGetStageRewardRequest { StageId = CharacterTowerVeraChallengeFirstStageId }, 0);
            CharacterTowerChapterState veraChapter = unknown.ChapterState(CharacterTowerVeraChallengeChapterId)
                ?? throw new InvalidDataException("The Vera claim must record its own chapter state.");
            AssertIntegerList([CharacterTowerVeraChallengeFirstStageId], veraChapter.StageRewardData.Select(value => (long)value).ToArray(), "Vera stage claim ownership");
            CharacterTowerChapterState watanabeChapter = unknown.ChapterState(CharacterTowerWatanabeChallengeChapterId)!;
            AssertIntegerList([21000101], watanabeChapter.StageRewardData.Select(value => (long)value).ToArray(),
                "the Watanabe claim stays inside its own chapter");
            AssertEqual(false, watanabeChapter.StageRewardData.Contains(CharacterTowerVeraChallengeFirstStageId), "a cross-chapter claim cannot leak");
            AssertEqual(2, unknown.Tower.Chapters.Count, "only claimed chapters create durable state");
        }
    }

    /// <summary>
    /// The authored condition families behind relation progress, probed through the tip request's live-or-frozen
    /// gate: commandant level, main-story pass, character level, affection, exhibition grow-up, battle power and
    /// the low-three-bit star sum.
    /// </summary>
    private static void ValidateCharacterTowerConditionSources()
    {
        using (CharacterTowerCase test = new("condition-level", 30, [CharacterTowerWatanabeCharacterId]))
        {
            CharacterData watanabe = test.Owned(CharacterTowerWatanabeCharacterId);
            watanabe.Level = 79;
            test.Reject(nameof(CharacterTowerSaveTriggerConditionIdRequest),
                new CharacterTowerSaveTriggerConditionIdRequest { ChapterId = CharacterTowerWatanabeChallengeChapterId, ConditionId = 10436013 },
                CharacterTowerRelationConditionNotEnough, "character Lv79 cannot satisfy the authored Lv80 condition");
            watanabe.Level = 80;
            test.Call(nameof(CharacterTowerSaveTriggerConditionIdRequest),
                new CharacterTowerSaveTriggerConditionIdRequest { ChapterId = CharacterTowerWatanabeChallengeChapterId, ConditionId = 10436013 }, 0);
        }

        using (CharacterTowerCase test = new("condition-main-story", 30, [CharacterTowerWatanabeCharacterId]))
        {
            test.Owned(CharacterTowerWatanabeCharacterId).Level = 80;
            test.Reject(nameof(CharacterTowerSaveTriggerConditionIdRequest),
                new CharacterTowerSaveTriggerConditionIdRequest { ChapterId = CharacterTowerWatanabeChallengeChapterId, ConditionId = 10436014 },
                CharacterTowerRelationConditionNotEnough, "an unpassed main story stage cannot satisfy the pass condition");
            test.SeedStage(10040203, passed: true, starsMark: 7);
            test.Call(nameof(CharacterTowerSaveTriggerConditionIdRequest),
                new CharacterTowerSaveTriggerConditionIdRequest { ChapterId = CharacterTowerWatanabeChallengeChapterId, ConditionId = 10436014 }, 0);
        }

        using (CharacterTowerCase test = new("condition-affection", 30, [CharacterTowerWatanabeCharacterId]))
        {
            CharacterData watanabe = test.Owned(CharacterTowerWatanabeCharacterId);
            watanabe.TrustLv = 4;
            test.Reject(nameof(CharacterTowerSaveTriggerConditionIdRequest),
                new CharacterTowerSaveTriggerConditionIdRequest { ChapterId = CharacterTowerWatanabeChallengeChapterId, ConditionId = 10436015 },
                CharacterTowerRelationConditionNotEnough, "affection 4 cannot satisfy the authored affection 5 condition");
            watanabe.TrustLv = 5;
            test.Call(nameof(CharacterTowerSaveTriggerConditionIdRequest),
                new CharacterTowerSaveTriggerConditionIdRequest { ChapterId = CharacterTowerWatanabeChallengeChapterId, ConditionId = 10436015 }, 0);
        }

        using (CharacterTowerCase test = new("condition-exhibition", 30, [CharacterTowerWatanabeCharacterId]))
        {
            CharacterData watanabe = test.Owned(CharacterTowerWatanabeCharacterId);
            // The client's exhibition grow-up level is claimed exhibition rewards, never the liberate level.
            watanabe.LiberateLv = 6;
            test.Reject(nameof(CharacterTowerSaveTriggerConditionIdRequest),
                new CharacterTowerSaveTriggerConditionIdRequest { ChapterId = CharacterTowerWatanabeChallengeChapterId, ConditionId = 10436016 },
                CharacterTowerRelationConditionNotEnough, "LiberateLv alone is not the exhibition grow-up source");
            AssertEqual(true, test.SeedExhibitionReward(CharacterTowerWatanabeCharacterId, levelId: 4) > 0, "authored exhibition reward row");
            test.Call(nameof(CharacterTowerSaveTriggerConditionIdRequest),
                new CharacterTowerSaveTriggerConditionIdRequest { ChapterId = CharacterTowerWatanabeChallengeChapterId, ConditionId = 10436016 }, 0);
        }

        using (CharacterTowerCase test = new("condition-power", 30, [CharacterTowerWatanabeCharacterId, CharacterTowerVeraCharacterId]))
        {
            CharacterData watanabe = test.Owned(CharacterTowerWatanabeCharacterId);
            watanabe.Level = 80;
            watanabe.Ability = 0;
            // Battle power comes from current state, so the gate must agree with CharacterPower itself...
            long watanabePower = TowerCalculatedPower(test.Session, watanabe);
            AssertEqual(watanabePower >= 6200 ? 0 : CharacterTowerRelationConditionNotEnough,
                test.TriggerCode(CharacterTowerWatanabeChallengeChapterId, 10436017),
                "the battle power condition tracks the calculated power source");
            // ...and a forged upload cache must never be able to open it.
            CharacterData vera = test.Owned(CharacterTowerVeraCharacterId);
            vera.Level = 80;
            vera.Ability = int.MaxValue;
            long veraPower = TowerCalculatedPower(test.Session, vera);
            AssertEqual(veraPower >= 6200 ? 0 : CharacterTowerRelationConditionNotEnough,
                test.TriggerCode(CharacterTowerVeraChallengeChapterId, 10436067),
                "a forged CharacterData.Ability cache cannot open the battle power condition");
        }

        using (CharacterTowerCase test = new("condition-stars", 30, [CharacterTowerWatanabeCharacterId]))
        {
            foreach (int stageId in new[] { CharacterTowerWatanabeFirstStageId, 21000101, 21000102, 21000103, 21000104 })
                test.SeedStage(stageId, passed: true, starsMark: 0b1000); // bit 3 is not a star: the client decodes bits 0..2
            test.Reject(nameof(CharacterTowerSaveTriggerConditionIdRequest),
                new CharacterTowerSaveTriggerConditionIdRequest { ChapterId = CharacterTowerWatanabeChallengeChapterId, ConditionId = 10436018 },
                CharacterTowerRelationConditionNotEnough, "star bits above the low three are not counted");
            foreach (int stageId in new[] { CharacterTowerWatanabeFirstStageId, 21000101, 21000102, 21000103 })
                test.SeedStage(stageId, passed: true, starsMark: 1);
            test.Reject(nameof(CharacterTowerSaveTriggerConditionIdRequest),
                new CharacterTowerSaveTriggerConditionIdRequest { ChapterId = CharacterTowerWatanabeChallengeChapterId, ConditionId = 10436018 },
                CharacterTowerRelationConditionNotEnough, "four chapter stars are below the authored fifteen");
            foreach (int stageId in new[] { CharacterTowerWatanabeFirstStageId, 21000101, 21000102, 21000103, 21000104 })
                test.SeedStage(stageId, passed: true, starsMark: 7);
            test.Call(nameof(CharacterTowerSaveTriggerConditionIdRequest),
                new CharacterTowerSaveTriggerConditionIdRequest { ChapterId = CharacterTowerWatanabeChallengeChapterId, ConditionId = 10436018 }, 0);
        }

        using (CharacterTowerCase test = new("condition-tips", 30, [CharacterTowerWatanabeCharacterId]))
        {
            CharacterData watanabe = test.Owned(CharacterTowerWatanabeCharacterId);
            test.Reject(nameof(CharacterTowerSaveTriggerConditionIdRequest),
                new CharacterTowerSaveTriggerConditionIdRequest { ChapterId = CharacterTowerWatanabeChallengeChapterId, ConditionId = 10436015 },
                CharacterTowerRelationConditionNotEnough, "a false condition cannot be acknowledged");
            watanabe.Level = 80;
            test.Call(nameof(CharacterTowerSaveTriggerConditionIdRequest),
                new CharacterTowerSaveTriggerConditionIdRequest { ChapterId = CharacterTowerWatanabeChallengeChapterId, ConditionId = 10436013 }, 0);
            test.Reject(nameof(CharacterTowerSaveTriggerConditionIdRequest),
                new CharacterTowerSaveTriggerConditionIdRequest { ChapterId = CharacterTowerWatanabeChallengeChapterId, ConditionId = 10436013 },
                CharacterTowerTriggerConditionExist, "a one-time tip cannot be acknowledged twice");
            CharacterTowerChapterState chapterState = test.ChapterState(CharacterTowerWatanabeChallengeChapterId)!;
            AssertIntegerList([10436013], chapterState.TriggerConditions.Select(value => (long)value).ToArray(), "tip markers are chapter-scoped");
            AssertEqual(0, chapterState.Relations.Count, "a tip acknowledgement creates no relation progress");
        }
    }

    /// <summary>
    /// Relation tiers: authored FinishNums thresholds, frozen-versus-live condition counting, story tier strings,
    /// idempotent re-acks and chapter scoping.
    /// </summary>
    private static void ValidateCharacterTowerRelationProgress()
    {
        using CharacterTowerCase test = new("relation-progress", 30, [CharacterTowerWatanabeCharacterId]);
        CharacterData watanabe = test.Owned(CharacterTowerWatanabeCharacterId);
        watanabe.Level = 80; // live 10436013 only

        test.Reject(nameof(CharacterTowerActivateFightEventIdRequest),
            new CharacterTowerActivateFightEventIdRequest { RelationId = CharacterTowerWatanabeRelationId, FightEventId = 2260102 },
            CharacterTowerRelationConditionNotEnough, "tier three requires three satisfied conditions");
        test.Reject(nameof(CharacterTowerActivateFightEventIdRequest),
            new CharacterTowerActivateFightEventIdRequest { RelationId = CharacterTowerWatanabeRelationId, FightEventId = -1 },
            CharacterTowerConfigNotFound, "the story-only sentinel is never an activation payload");
        test.Reject(nameof(CharacterTowerActivateFightEventIdRequest),
            new CharacterTowerActivateFightEventIdRequest { RelationId = CharacterTowerWatanabeRelationId, FightEventId = 2_260_999 },
            CharacterTowerConfigNotFound, "unconfigured fight event");
        test.Call(nameof(CharacterTowerActivateFightEventIdRequest),
            new CharacterTowerActivateFightEventIdRequest { RelationId = CharacterTowerWatanabeRelationId, FightEventId = 2260101 }, 0);

        // Freezing keeps a satisfied condition counted after its live source disappears.
        watanabe.Level = 1;
        CharacterTowerRelationState relation = test.RelationState(CharacterTowerWatanabeChallengeChapterId, CharacterTowerWatanabeRelationId)!;
        AssertIntegerList([10436013], relation.FinishConditions.Select(value => (long)value).ToArray(), "tier one freezes exactly the live conditions");
        test.Reject(nameof(CharacterTowerSaveStoryIdRequest),
            new CharacterTowerSaveStoryIdRequest { RelationId = CharacterTowerWatanabeRelationId, StoryId = "JS00106BA" },
            CharacterTowerRelationConditionNotEnough, "one frozen condition is below the story tier threshold");
        test.Reject(nameof(CharacterTowerSaveStoryIdRequest),
            new CharacterTowerSaveStoryIdRequest { RelationId = CharacterTowerWatanabeRelationId, StoryId = "-1" },
            CharacterTowerConfigNotFound, "the negative story sentinel is never a movie");
        test.Reject(nameof(CharacterTowerSaveStoryIdRequest),
            new CharacterTowerSaveStoryIdRequest { RelationId = CharacterTowerWatanabeRelationId, StoryId = "JS00999BA" },
            CharacterTowerConfigNotFound, "unconfigured relation story");

        test.SeedStage(10040203, passed: true, starsMark: 0); // live 10436014
        JObject storySave = test.Call(nameof(CharacterTowerSaveStoryIdRequest),
            new CharacterTowerSaveStoryIdRequest { RelationId = CharacterTowerWatanabeRelationId, StoryId = "JS00106BA" }, 0);
        AssertIntegerList([10436013, 10436014], storySave["FinishConditions"]!.Values<int>().Select(value => (long)value).ToArray(),
            "the story tier freezes the frozen-or-live conditions");

        watanabe.TrustLv = 5; // live 10436015
        test.Call(nameof(CharacterTowerActivateFightEventIdRequest),
            new CharacterTowerActivateFightEventIdRequest { RelationId = CharacterTowerWatanabeRelationId, FightEventId = 2260102 }, 0);
        test.Reject(nameof(CharacterTowerSaveStoryIdRequest),
            new CharacterTowerSaveStoryIdRequest { RelationId = CharacterTowerWatanabeRelationId, StoryId = "JS00107BA" },
            CharacterTowerRelationConditionNotEnough, "tier four requires four satisfied conditions");
        test.SeedExhibitionReward(CharacterTowerWatanabeCharacterId, levelId: 4); // live 10436016
        test.Call(nameof(CharacterTowerSaveStoryIdRequest),
            new CharacterTowerSaveStoryIdRequest { RelationId = CharacterTowerWatanabeRelationId, StoryId = "JS00107BA" }, 0);
        test.Reject(nameof(CharacterTowerActivateFightEventIdRequest),
            new CharacterTowerActivateFightEventIdRequest { RelationId = CharacterTowerWatanabeRelationId, FightEventId = 2260103 },
            CharacterTowerRelationConditionNotEnough, "tier five requires five satisfied conditions");
        foreach (int stageId in new[] { CharacterTowerWatanabeFirstStageId, 21000101, 21000102, 21000103, 21000104 })
            test.SeedStage(stageId, passed: true, starsMark: 7); // live 10436018
        test.Call(nameof(CharacterTowerActivateFightEventIdRequest),
            new CharacterTowerActivateFightEventIdRequest { RelationId = CharacterTowerWatanabeRelationId, FightEventId = 2260103 }, 0);
        test.Reject(nameof(CharacterTowerSaveStoryIdRequest),
            new CharacterTowerSaveStoryIdRequest { RelationId = CharacterTowerWatanabeRelationId, StoryId = "JS00108BA" },
            CharacterTowerRelationConditionNotEnough, "the sixth story tier still requires all six conditions");

        relation = test.RelationState(CharacterTowerWatanabeChallengeChapterId, CharacterTowerWatanabeRelationId)!;
        AssertIntegerList([2260101, 2260102, 2260103], relation.FightEventIds.Select(value => (long)value).ToArray(),
            "activated fight events are recorded in tier order, once each");
        TowerAssertStrings(["JS00106BA", "JS00107BA"], relation.StoryIds, "saved relation stories keep their string identity");
        AssertIntegerList([10436013, 10436014, 10436015, 10436016, 10436018], relation.FinishConditions.Select(value => (long)value).ToArray(),
            "every activation freezes exactly the conditions true at that moment");
        AssertEqual(false, relation.StoryIds.Contains("JS00108BA"), "an unmet tier can never be saved");

        // Re-acknowledgement is idempotent for both activation kinds, and each chapter keeps its own relation.
        AssertIntegerList([10436013, 10436014, 10436015, 10436016, 10436018],
            test.Call(nameof(CharacterTowerActivateFightEventIdRequest),
                new CharacterTowerActivateFightEventIdRequest { RelationId = CharacterTowerWatanabeRelationId, FightEventId = 2260101 }, 0)["FinishConditions"]!
                .Values<int>().Select(value => (long)value).ToArray(), "a repeated activation re-acknowledges the durable tier");
        test.Call(nameof(CharacterTowerSaveStoryIdRequest),
            new CharacterTowerSaveStoryIdRequest { RelationId = CharacterTowerWatanabeRelationId, StoryId = "JS00106BA" }, 0);
        relation = test.RelationState(CharacterTowerWatanabeChallengeChapterId, CharacterTowerWatanabeRelationId)!;
        AssertEqual(3, relation.FightEventIds.Count, "a repeated activation cannot duplicate a fight event");
        AssertEqual(2, relation.StoryIds.Count, "a repeated story save cannot duplicate a story id");
        AssertEqual(1, test.Tower.Chapters.Count, "relation progress is scoped to the owning chapter");
        AssertEqual(true, test.RelationState(CharacterTowerVeraChallengeChapterId, CharacterTowerVeraRelationId) is null,
            "another chapter's relation stays untouched");

        test.Relog("relation-progress");
        relation = test.RelationState(CharacterTowerWatanabeChallengeChapterId, CharacterTowerWatanabeRelationId)!;
        AssertIntegerList([10436013, 10436014, 10436015, 10436016, 10436018], relation.FinishConditions.Select(value => (long)value).ToArray(),
            "frozen conditions survive a fresh-session BSON reload");
        test.Call(nameof(CharacterTowerActivateFightEventIdRequest),
            new CharacterTowerActivateFightEventIdRequest { RelationId = CharacterTowerWatanabeRelationId, FightEventId = 2260103 }, 0);
        AssertEqual(3, test.RelationState(CharacterTowerWatanabeChallengeChapterId, CharacterTowerWatanabeRelationId)!.FightEventIds.Count,
            "a re-acknowledged tier stays accepted and unique after reload");
    }

    /// <summary>
    /// The shared fight pipeline around an Arcade stage: pre-flight gates and lineup authority, robot/story rows,
    /// relation event injection, restartability, the generic monster-level source, star masks on win/loss/replay,
    /// settlement reward suppression and the durable stage write.
    /// </summary>
    private static void ValidateCharacterTowerCombatTrust()
    {
        using (CharacterTowerCase test = new("combat-gates", 30, [CharacterTowerWatanabeCharacterId, CharacterTowerVeraCharacterId]))
        {
            // A story-only card can never start combat, and closed/redirected/multi-challenge requests are refused.
            test.RejectPreFight(TowerPreFight(CharacterTowerWatanabeStoryFirstStageId, [CharacterTowerWatanabeCharacterId]),
                CharacterTowerStageInfoError, "story card cannot start combat");
            test.RejectPreFight(TowerPreFight(21000101, [CharacterTowerWatanabeCharacterId]),
                CharacterTowerStageNotPass, "predecessor stage gate");
            test.RejectPreFight(TowerPreFight(CharacterTowerWatanabeFirstStageId, [CharacterTowerWatanabeCharacterId], challengeCount: 2),
                CharacterTowerCombatAuthorizationError, "multi-challenge is not this mode's contract");
            test.RejectPreFight(TowerPreFight(CharacterTowerWatanabeFirstStageId, [CharacterTowerWatanabeCharacterId], speedrunStageId: 21000101),
                CharacterTowerCombatAuthorizationError, "stage redirection is not this mode's contract");
            test.RejectPreFight(TowerPreFight(CharacterTowerWatanabeFirstStageId, [CharacterTowerVeraCharacterId]),
                CharacterTowerCombatAuthorizationError, "the challenge lineup must field the authored forced character");
            using (CharacterTowerCase unowned = new("combat-unowned-lineup", 30, [CharacterTowerVeraCharacterId]))
                unowned.RejectPreFight(TowerPreFight(CharacterTowerWatanabeFirstStageId, [CharacterTowerWatanabeCharacterId]),
                    CharacterTowerCombatAuthorizationError, "an unowned forced character cannot satisfy the lineup gate");

            // Story-chapter combat rows deploy their authored fixed robots and drop a forged normal lineup.
            test.SeedStage(CharacterTowerWatanabeStoryFirstStageId, passed: true, starsMark: 0);
            PreFightResponse robotFight = test.EnterFight("story robot row", CharacterTowerWatanabeRobotStageId, [CharacterTowerVeraCharacterId]);
            AssertPreFightDeployedCharacterIds(robotFight, test.Session.player.PlayerData.Id,
                ExpectedStageDeployCharacterIds(CharacterTowerWatanabeRobotStageId, CharacterTowerWatanabeCharacterId),
                "the story-chapter combat row deploys its authored robots");
            AssertEqual(false, robotFight.FightData!.Restartable, "story rows author no restartability");

            // Stage 21000264 is a challenge row authored with the story visual type: the challenge UI still fights it.
            foreach (int stageId in new[] { CharacterTowerVeraChallengeFirstStageId, 21000261, 21000262, 21000263 })
                test.SeedStage(stageId, passed: true, starsMark: 0);
            PreFightResponse typedStoryChallenge = test.EnterFight("21000264 challenge combat",
                CharacterTowerVeraTypedStoryChallengeStageId, [CharacterTowerVeraCharacterId]);
            AssertEqual(true, typedStoryChallenge.FightData!.Restartable, "challenge restartability is authored per stage");
            AssertEqual((uint)CharacterTowerVeraTypedStoryChallengeStageId, typedStoryChallenge.FightData.StageId, "21000264 stays a fight stage");
            test.LeaveFight();

            // Reward suppression: a challenge win grants nothing by itself.
            PreFightResponse challengeFight = test.EnterFight("challenge lineup", CharacterTowerWatanabeFirstStageId, [CharacterTowerWatanabeCharacterId]);
            AssertPreFightDeployedCharacterIds(challengeFight, test.Session.player.PlayerData.Id, [CharacterTowerWatanabeCharacterId],
                "challenge deployment uses the fielded authored lineup");
            AssertEqual(true, challengeFight.FightData!.Restartable, "challenge restartability");
            TowerAssertMonsterLevel(test, challengeFight, CharacterTowerWatanabeFirstStageId);
            AssertEqual(false, TowerContainsEvent(challengeFight.FightData.EventIds, 2260101),
                "no relation event is injected before a tier is activated");

            Dictionary<int, long> balancesBeforeFight = TowerBalances(test.Session);
            FightSettleResponse partial = test.Settle("partial stars", challengeFight, addStars: 1);
            AssertEqual(true, partial.Settle!.IsWin, "a win settles");
            AssertEqual(1, partial.Settle.StarsMark, "a one-star payload must never read as a full clear");
            AssertEqual(1, partial.Settle.ChallengeCount, "the single challenge count is echoed");
            AssertEqual(0, partial.Settle.RewardGoodsList.Count, "tower settlement grants no first-clear rewards");
            AssertEqual(false, partial.Settle.MultiRewardGoodsList.SelectMany(goods => goods).Any(), "tower settlement grants no multi rewards");
            TowerAssertNoItemChange(test.Session, balancesBeforeFight, "a tower settlement");
            AssertEqual(false, test.Pushes.Any(push => push.Name == nameof(NotifyItemDataList)),
                "a tower settlement emits no item ownership push");
            StageDatum stored = test.StoredStage(CharacterTowerWatanabeFirstStageId)
                ?? throw new InvalidDataException("A committed tower stage write is missing.");
            AssertEqual(true, stored.Passed, "the win records the pass");
            AssertEqual(1L, stored.StarsMark, "persisted partial stars");
            AssertEqual(1L, stored.PassTimesTotal, "a committed clear is counted once");
            test.AssertStagePush(CharacterTowerWatanabeFirstStageId, 1, "partial star settle push");

            // The alternate prequel protocol can never pay this first clear...
            test.Reject(nameof(ReceivePrequelRewardRequest), new ReceivePrequelRewardRequest { StageId = CharacterTowerWatanabeFirstStageId },
                1, "the alternate prequel claim cannot pay an Arcade first clear");
            AssertEqual(false, test.Session.stage.PrequelRewardedStages!.Contains(CharacterTowerWatanabeFirstStageId),
                "the prequel ledger stays untouched by Arcade progression");
            // ...the mode's own manual claim is the only payout...
            test.Call(nameof(CharacterTowerGetStageRewardRequest), new CharacterTowerGetStageRewardRequest { StageId = CharacterTowerWatanabeFirstStageId }, 0);
            TowerAssertItemGoods(test.Session, balancesBeforeFight, 16316, "the manual stage claim is the real first-clear payout");

            // Replay: the star mask is a union, never a downgrade.
            PreFightResponse replay = test.EnterFight("replay", CharacterTowerWatanabeFirstStageId, [CharacterTowerWatanabeCharacterId]);
            AssertEqual(3, test.Settle("partial replay", replay, addStars: 2).Settle!.StarsMark, "a replay unions the earned star bits onto the persisted mask");
            PreFightResponse full = test.EnterFight("full stars", CharacterTowerWatanabeFirstStageId, [CharacterTowerWatanabeCharacterId]);
            AssertEqual(7, test.Settle("full stars", full, addStars: 7).Settle!.StarsMark, "only the authored payload reaches three stars");
            PreFightResponse downgrade = test.EnterFight("star downgrade", CharacterTowerWatanabeFirstStageId, [CharacterTowerWatanabeCharacterId]);
            AssertEqual(7, test.Settle("star downgrade", downgrade, addStars: 3).Settle!.StarsMark, "a weaker replay cannot erase earned stars");
            AssertEqual(4L, test.StoredStage(CharacterTowerWatanabeFirstStageId)!.PassTimesTotal, "replays accumulate clear counts");

            // Impossible star payloads are refused without touching progression, and the fight stays retryable.
            byte[] starsBefore = test.StoredStage(CharacterTowerWatanabeFirstStageId)!.ToBson();
            PreFightResponse badMask = test.EnterFight("bad mask", CharacterTowerWatanabeFirstStageId, [CharacterTowerWatanabeCharacterId]);
            test.RejectSettle("bad star mask", TowerSettle(CharacterTowerWatanabeFirstStageId, badMask.FightData!.FightId,
                test.Session.player.PlayerData.Id, addStars: 8), CharacterTowerCombatAuthorizationError);
            test.RejectSettle("negative star mask", TowerSettle(CharacterTowerWatanabeFirstStageId, badMask.FightData.FightId,
                test.Session.player.PlayerData.Id, addStars: -1), CharacterTowerCombatAuthorizationError);
            AssertEqual(true, starsBefore.SequenceEqual(test.StoredStage(CharacterTowerWatanabeFirstStageId)!.ToBson()),
                "an impossible star payload cannot rewrite durable stars");
            AssertEqual(true, test.Session.fight is not null, "a refused star payload keeps the accepted fight open");
            AssertEqual(7, test.Settle("retry after refusal", badMask, addStars: 3).Settle!.StarsMark, "the same fight still settles correctly");

            // Foreign identity and stage redirection never settle.
            PreFightResponse foreign = test.EnterFight("foreign identity", CharacterTowerWatanabeFirstStageId, [CharacterTowerWatanabeCharacterId]);
            test.RejectSettle("foreign fight id", TowerSettle(CharacterTowerWatanabeFirstStageId, foreign.FightData!.FightId + 1,
                test.Session.player.PlayerData.Id, addStars: 7), CharacterTowerCombatAuthorizationError);
            test.RejectSettle("redirected stage", TowerSettle(21000101, foreign.FightData.FightId,
                test.Session.player.PlayerData.Id, addStars: 7), CharacterTowerCombatAuthorizationError);
            test.LeaveFight();

            // A loss and a force exit change nothing and credit no first clear.
            test.SeedStage(CharacterTowerWatanabeFirstStageId, passed: true, starsMark: 7);
            PreFightResponse loss = test.EnterFight("loss", 21000101, [CharacterTowerWatanabeCharacterId]);
            FightSettleResponse lost = test.Settle("loss", loss, addStars: 7, isWin: false);
            AssertEqual(false, lost.Settle!.IsWin, "a loss settles as a failure");
            AssertEqual(0, lost.Settle.StarsMark, "a loss earns no stars");
            AssertEqual(false, test.StoredStage(21000101)?.Passed ?? false, "a loss cannot complete the stage");
            test.Reject(nameof(CharacterTowerGetStageRewardRequest), new CharacterTowerGetStageRewardRequest { StageId = 21000101 },
                CharacterTowerStageNotPass, "a lost stage is not claimable");
            PreFightResponse exit = test.EnterFight("force exit", 21000101, [CharacterTowerWatanabeCharacterId]);
            FightSettleResponse exited = test.Settle("force exit", exit, addStars: 7, isWin: true, isForceExit: true);
            AssertEqual(false, exited.Settle!.IsWin, "a force exit settles as a failure");
            AssertEqual(false, test.StoredStage(21000101)?.Passed ?? false, "a force exit cannot complete the stage");
        }

        using (CharacterTowerCase test = new("combat-events", 30, [CharacterTowerWatanabeCharacterId, CharacterTowerVeraCharacterId]))
        {
            test.Owned(CharacterTowerWatanabeCharacterId).Level = 80;
            test.Owned(CharacterTowerVeraCharacterId).Level = 80;
            test.Call(nameof(CharacterTowerActivateFightEventIdRequest),
                new CharacterTowerActivateFightEventIdRequest { RelationId = CharacterTowerWatanabeRelationId, FightEventId = 2260101 }, 0);
            test.Call(nameof(CharacterTowerActivateFightEventIdRequest),
                new CharacterTowerActivateFightEventIdRequest { RelationId = CharacterTowerVeraRelationId, FightEventId = 2260601 }, 0);
            PreFightResponse watanabe = test.EnterFight("relation events", CharacterTowerWatanabeFirstStageId, [CharacterTowerWatanabeCharacterId]);
            AssertEqual(true, TowerContainsEvent(watanabe.FightData!.EventIds, 2260101),
                "an activated relation tier reaches its own chapter's fight");
            AssertEqual(false, TowerContainsEvent(watanabe.FightData.EventIds, 2260601),
                "another chapter's relation event is never injected");
            test.LeaveFight();
            PreFightResponse vera = test.EnterFight("relation isolation", CharacterTowerVeraChallengeFirstStageId, [CharacterTowerVeraCharacterId]);
            AssertEqual(true, TowerContainsEvent(vera.FightData!.EventIds, 2260601), "each chapter owns its relation events");
            AssertEqual(false, TowerContainsEvent(vera.FightData.EventIds, 2260101), "relation events never leak across chapters");
            AssertEqual(true, vera.FightData.Restartable, "the Vera challenge row is restartable");
            TowerAssertMonsterLevel(test, vera, CharacterTowerVeraChallengeFirstStageId);
            test.LeaveFight();
        }
    }

    /// <summary>
    /// Manual claims: authored gates, exactly-once payouts, durable markers, fresh-session persistence and the
    /// pending partial-save recovery where the reward receipt commits before the mode marker.
    /// </summary>
    private static void ValidateCharacterTowerClaimTrust()
    {
        using (CharacterTowerCase test = new("claim-trust", 30, [CharacterTowerWatanabeCharacterId]))
        {
            test.Reject(nameof(CharacterTowerGetStageRewardRequest), new CharacterTowerGetStageRewardRequest { StageId = CharacterTowerWatanabeFirstStageId },
                CharacterTowerStageNotPass, "an unpassed stage is not claimable");
            test.SeedStage(CharacterTowerWatanabeFirstStageId, passed: true, starsMark: 7);
            test.SeedStage(21000101, passed: true, starsMark: 1);
            Dictionary<int, long> balancesBeforeClaim = TowerBalances(test.Session);
            test.Call(nameof(CharacterTowerGetStageRewardRequest), new CharacterTowerGetStageRewardRequest { StageId = CharacterTowerWatanabeFirstStageId }, 0);
            TowerAssertItemGoods(test.Session, balancesBeforeClaim, 16316, "a stage claim");
            Dictionary<int, long> balancesAfterClaim = TowerBalances(test.Session);
            AssertIntegerList([CharacterTowerWatanabeFirstStageId],
                test.ChapterState(CharacterTowerWatanabeChallengeChapterId)!.StageRewardData.Select(value => (long)value).ToArray(), "stage claim marker");
            test.Reject(nameof(CharacterTowerGetStageRewardRequest), new CharacterTowerGetStageRewardRequest { StageId = CharacterTowerWatanabeFirstStageId },
                CharacterTowerStageRewardHadGot, "a duplicate stage claim is refused");
            TowerAssertNoItemChange(test.Session, balancesAfterClaim, "a duplicate stage claim");

            // Treasure thresholds use the low-three-bit star sum of the owning chapter only.
            test.Reject(nameof(CharacterTowerGetStarRewardRequest), new CharacterTowerGetStarRewardRequest { TreasureId = CharacterTowerWatanabeTreasure5Stars },
                CharacterTowerStarNotEnough, "four stars are below the authored five");
            test.SeedStage(21000101, passed: true, starsMark: 3);
            test.Call(nameof(CharacterTowerGetStarRewardRequest), new CharacterTowerGetStarRewardRequest { TreasureId = CharacterTowerWatanabeTreasure5Stars }, 0);
            AssertIntegerList([CharacterTowerWatanabeTreasure5Stars],
                test.ChapterState(CharacterTowerWatanabeChallengeChapterId)!.TreasureData.Select(value => (long)value).ToArray(), "treasure marker");
            test.Reject(nameof(CharacterTowerGetStarRewardRequest), new CharacterTowerGetStarRewardRequest { TreasureId = CharacterTowerWatanabeTreasure10Stars },
                CharacterTowerStarNotEnough, "the next authored threshold is still unreached");
            test.Relog("claim-trust");
            test.Reject(nameof(CharacterTowerGetStarRewardRequest), new CharacterTowerGetStarRewardRequest { TreasureId = CharacterTowerWatanabeTreasure5Stars },
                CharacterTowerStarRewardHadGot, "the claimed treasure stays claimed after relog");
            test.Reject(nameof(CharacterTowerGetStageRewardRequest), new CharacterTowerGetStageRewardRequest { StageId = CharacterTowerWatanabeFirstStageId },
                CharacterTowerStageRewardHadGot, "the stage marker stays durable after relog");
            TowerAssertNoItemChange(test.Session, balancesAfterClaim, "a relog cannot duplicate a durable payout");
        }

        // The reward receipt is already durable when the mode marker save fails: the module must roll back its
        // in-memory edit and let the retried request finish the same claim exactly once.
        using (CharacterTowerCase test = new("claim-pending-save", 30, [CharacterTowerWatanabeCharacterId]))
        {
            test.SeedStage(CharacterTowerWatanabeFirstStageId, passed: true, starsMark: 1);
            Dictionary<int, long> balancesBeforeClaim = TowerBalances(test.Session);
            test.Players.ThrowOnReplaceOne = true;
            try
            {
                test.CallThrows(nameof(CharacterTowerGetStageRewardRequest),
                    new CharacterTowerGetStageRewardRequest { StageId = CharacterTowerWatanabeFirstStageId }, "stage claim marker save failure");
            }
            finally
            {
                test.Players.ThrowOnReplaceOne = false;
            }
            AssertEqual(true, test.ChapterState(CharacterTowerWatanabeChallengeChapterId) is null,
                "a failed marker save leaves no committed in-memory claim");
            TowerAssertItemGoods(test.Session, balancesBeforeClaim, 16316,
                "the receipted reward is durable before the mode marker (pending partial save)");
            Dictionary<int, long> balancesAfterReceipt = TowerBalances(test.Session);
            test.Relog("claim-pending-save");
            TowerAssertNoItemChange(test.Session, balancesAfterReceipt, "a relog does not replay the receipted payout");
            JObject recovered = test.Call(nameof(CharacterTowerGetStageRewardRequest),
                new CharacterTowerGetStageRewardRequest { StageId = CharacterTowerWatanabeFirstStageId }, 0);
            AssertIntegerList([CharacterTowerWatanabeFirstStageId],
                test.ChapterState(CharacterTowerWatanabeChallengeChapterId)!.StageRewardData.Select(value => (long)value).ToArray(),
                "the retried request commits the same claim marker");
            TowerAssertNoItemChange(test.Session, balancesAfterReceipt, "the recovered claim cannot pay a second time");
            TowerAssertRewards(recovered, 16316, "an already receipted retry replays the authored acknowledgement without repaying");
            AssertEqual(true, test.Pushes.Any(push => push.Name == nameof(NotifyItemDataList)),
                "an already receipted retry still republishes ownership state");
            test.Reject(nameof(CharacterTowerGetStageRewardRequest), new CharacterTowerGetStageRewardRequest { StageId = CharacterTowerWatanabeFirstStageId },
                CharacterTowerStageRewardHadGot, "the recovered claim is durable");
        }

        // The shared settlement write is checked for tower stages: a failed write restores the pre-settle datum,
        // credits no task, and a retry of the same fight still commits exactly one clean clear.
        using (CharacterTowerCase test = new("settle-durability", 30, [CharacterTowerWatanabeCharacterId]))
        {
            PreFightResponse fight = test.EnterFight("settle durability", CharacterTowerWatanabeFirstStageId, [CharacterTowerWatanabeCharacterId]);
            foreach (string failingSave in new[] { "player", "inventory", "character", "stage" })
            {
                test.Players.ThrowOnReplaceOne = failingSave == "player";
                test.Inventories.ThrowOnReplaceOne = failingSave == "inventory";
                test.Characters.ThrowOnReplaceOne = failingSave == "character";
                test.Stages.ThrowOnReplaceOne = failingSave == "stage";
                try
                {
                    test.CallThrows(nameof(FightSettleRequest),
                        TowerSettle(CharacterTowerWatanabeFirstStageId, fight.FightData!.FightId, test.Session.player.PlayerData.Id, addStars: 1),
                        $"tower settlement {failingSave} write failure");
                }
                finally
                {
                    test.Players.ThrowOnReplaceOne = false;
                    test.Inventories.ThrowOnReplaceOne = false;
                    test.Characters.ThrowOnReplaceOne = false;
                    test.Stages.ThrowOnReplaceOne = false;
                }
                AssertEqual(true, test.StoredStage(CharacterTowerWatanabeFirstStageId) is null,
                    $"a failed {failingSave} write removes an uncommitted first clear");
                AssertEqual(true, test.Session.fight is not null, $"a failed {failingSave} write keeps the accepted fight authorized");
                test.Reject(nameof(CharacterTowerGetStageRewardRequest), new CharacterTowerGetStageRewardRequest { StageId = CharacterTowerWatanabeFirstStageId },
                    CharacterTowerStageNotPass, $"a clear with a failed {failingSave} write is not claimable");
            }
            FightSettleResponse retry = test.Settle("settle retry", fight, addStars: 1);
            AssertEqual(1, retry.Settle!.StarsMark, "the retried clear keeps the exact partial stars");
            AssertEqual(1L, test.StoredStage(CharacterTowerWatanabeFirstStageId)!.PassTimesTotal, "a failed write cannot pre-count a clear");
            AssertEqual(true, test.Pushes.Any(push => push.Name == nameof(NotifyTask)), "a committed clear publishes the generic task sync");
            test.AssertStagePush(CharacterTowerWatanabeFirstStageId, 1, "retried settle push");
            test.Call(nameof(CharacterTowerGetStageRewardRequest), new CharacterTowerGetStageRewardRequest { StageId = CharacterTowerWatanabeFirstStageId }, 0);
        }
    }

    /// <summary>
    /// The neighbouring protocols an Arcade stage must not leak into: generic story entry, challenge/story visual
    /// types and the alternate prequel reward claim.
    /// </summary>
    private static void ValidateCharacterTowerProtectionBoundaries()
    {
        using (CharacterTowerCase test = new("boundaries", 30, [CharacterTowerWatanabeCharacterId]))
        {
            test.Call(nameof(EnterStoryRequest), new EnterStoryRequest { StageId = CharacterTowerWatanabeStoryFirstStageId }, 0);
            StageDatum storyDatum = test.StoredStage(CharacterTowerWatanabeStoryFirstStageId)
                ?? throw new InvalidDataException("EnterStory must persist the story stage.");
            AssertEqual(true, storyDatum.Passed, "EnterStory completes the story card");
            AssertEqual(0L, storyDatum.StarsMark, "story cards carry an explicit numeric zero, never free stars");
            test.AssertStagePush(CharacterTowerWatanabeStoryFirstStageId, 0, "story entry push");

            test.Call(nameof(EnterStoryRequest), new EnterStoryRequest { StageId = CharacterTowerWatanabeStoryFirstStageId }, 0);
            AssertEqual(1, test.Session.stage.Stages.Count(stage => stage.Key == CharacterTowerWatanabeStoryFirstStageId),
                "a story replay cannot duplicate the stage datum");
            AssertEqual(0L, test.StoredStage(CharacterTowerWatanabeStoryFirstStageId)!.StarsMark, "a story replay cannot invent stars");

            // A combat row inside a story chapter must be fought, and the authored 21000264 anomaly stays combat.
            test.Reject(nameof(EnterStoryRequest), new EnterStoryRequest { StageId = CharacterTowerWatanabeRobotStageId },
                CharacterTowerStageInfoError, "a story-chapter combat row cannot complete as a story");
            test.Reject(nameof(EnterStoryRequest), new EnterStoryRequest { StageId = 21000113 },
                CharacterTowerStageNotPass, "a story card still needs its predecessor");
            test.Reject(nameof(EnterStoryRequest), new EnterStoryRequest { StageId = CharacterTowerVeraTypedStoryChallengeStageId },
                CharacterTowerStageInfoError, "the challenge row authored with StageType 2 stays combat");
            test.Reject(nameof(EnterStoryRequest), new EnterStoryRequest { StageId = CharacterTowerArcaChallengeFirstStageId },
                CharacterTowerStageInfoError, "a locked Noan challenge row is not a story card");

            // A non-Arcade stage keeps the generic story handler's authority (an authored Prequel story stage,
            // the same family the generic story tests already drive).
            StageTable ordinary = TableReaderV2.Parse<StageTable>()
                .First(stage => Convert.ToInt32(stage.Type) == 11 && stage.StageType is 2 or 3);
            test.Call(nameof(EnterStoryRequest), new EnterStoryRequest { StageId = ordinary.StageId }, 0);
            AssertEqual(true, test.StoredStage(ordinary.StageId)?.Passed ?? false, "the generic story handler still completes ordinary cards");
            test.AssertStagePush(ordinary.StageId, 0, "generic story entry push");
        }

        using (CharacterTowerCase test = new("boundaries-prequel", 30, [CharacterTowerWatanabeCharacterId]))
        {
            PreFightResponse fight = test.EnterFight("prequel boundary", CharacterTowerWatanabeFirstStageId, [CharacterTowerWatanabeCharacterId]);
            test.Settle("prequel boundary", fight, addStars: 7);
            Dictionary<int, long> balancesBeforeClaim = TowerBalances(test.Session);
            test.Reject(nameof(ReceivePrequelRewardRequest), new ReceivePrequelRewardRequest { StageId = CharacterTowerWatanabeFirstStageId },
                1, "the alternate prequel protocol cannot pay an Arcade first clear");
            AssertEqual(0, test.Session.stage.PrequelRewardedStages!.Count, "the prequel ledger never records an Arcade stage");
            TowerAssertNoItemChange(test.Session, balancesBeforeClaim, "a refused alternate claim");
            test.Call(nameof(CharacterTowerGetStageRewardRequest), new CharacterTowerGetStageRewardRequest { StageId = CharacterTowerWatanabeFirstStageId }, 0);
            TowerAssertItemGoods(test.Session, balancesBeforeClaim, 16316, "the mode's own manual first-clear claim");
            Dictionary<int, long> balancesAfterClaim = TowerBalances(test.Session);
            test.Reject(nameof(ReceivePrequelRewardRequest), new ReceivePrequelRewardRequest { StageId = CharacterTowerWatanabeFirstStageId },
                1, "the alternate claim stays closed after the manual claim");
            TowerAssertNoItemChange(test.Session, balancesAfterClaim, "the closed alternate claim");
        }
    }

    #region Fixture

    private sealed class CharacterTowerCase : IDisposable
    {
        private static long nextPlayerId = 88_900;

        private readonly MongoCollectionOverride collections;
        private int packetId;

        public readonly RecordingMongoCollectionProxy<Player> Players;
        public readonly RecordingMongoCollectionProxy<Character> Characters;
        public readonly RecordingMongoCollectionProxy<Inventory> Inventories;
        public readonly RecordingMongoCollectionProxy<Stage> Stages;
        public LoopbackSessionHarness Harness { get; private set; }
        public Session Session => Harness.Session;
        public CharacterTowerState Tower => Session.player.CharacterTower;
        public List<Packet.Push> Pushes { get; } = [];
        public byte[] LastResponseContent { get; private set; } = [];

        public CharacterTowerCase(string name, int level, IReadOnlyList<int> ownedCharacters)
        {
            collections = MongoCollectionOverride.InstallForBiancaCompatibility(out Players, out Characters, out Inventories, out Stages);
            long playerId = ++nextPlayerId;
            Player player = CreateDrawCompatibilityPlayer(playerId);
            player.PlayerData.Level = level;
            Character character = CreateDrawCompatibilityCharacter(playerId);
            foreach (int characterId in ownedCharacters)
                character.Characters.Add(TowerCharacterData(characterId));
            Harness = new(character, player, CreateDrawCompatibilityInventory(playerId, []), $"character-tower-{name}");
            Session.stage = CreateLoginAccountCompatibilityStage(playerId);
        }

        public CharacterData Owned(int characterId) => Session.character.Characters.Single(character => character.Id == (uint)characterId);

        public StageDatum SeedStage(int stageId, bool passed, long starsMark)
        {
            StageDatum datum = new()
            {
                StageId = stageId,
                Passed = passed,
                StarsMark = starsMark,
                CreateTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                BestCardIds = [],
                LastCardIds = []
            };
            Session.stage.AddStage(datum);
            return datum;
        }

        public StageDatum? StoredStage(int stageId) =>
            Session.stage.Stages.TryGetValue(stageId, out StageDatum? datum) ? datum : null;

        public CharacterTowerChapterState? ChapterState(int chapterId) =>
            Tower.Chapters.FirstOrDefault(chapter => chapter.ChapterId == chapterId);

        public CharacterTowerRelationState? RelationState(int chapterId, int relationId) =>
            ChapterState(chapterId)?.Relations.FirstOrDefault(relation => relation.RelationId == relationId);

        public int SeedExhibitionReward(int characterId, int levelId)
        {
            ExhibitionRewardTable row = TableReaderV2.Parse<ExhibitionRewardTable>()
                .Where(candidate => candidate.CharacterId == (uint)characterId && candidate.LevelId == levelId)
                .OrderBy(candidate => candidate.Id)
                .First();
            Session.player.AddGatherReward(row.Id);
            return row.Id;
        }

        /// <summary>Raw live-or-frozen condition probe through the tip request, the mode's only condition oracle.</summary>
        public int TriggerCode(int chapterId, int conditionId) => RequiredValue<int>(
            Call(nameof(CharacterTowerSaveTriggerConditionIdRequest),
                new CharacterTowerSaveTriggerConditionIdRequest { ChapterId = chapterId, ConditionId = conditionId }, expectedCode: null),
            "Code", JTokenType.Integer, "condition probe");

        public PreFightResponse EnterFight(string name, int stageId, IReadOnlyList<int> cardIds)
        {
            Call(nameof(PreFightRequest), TowerPreFight(stageId, cardIds), 0);
            PreFightResponse response = MessagePackSerializer.Deserialize<PreFightResponse>(LastResponseContent);
            if (response.FightData is null)
                throw new InvalidDataException($"{name}: an accepted pre-fight has no deployment.");
            return response;
        }

        public void RejectPreFight(PreFightRequest request, int code, string name)
        {
            byte[] stages = Session.stage.ToBson();
            bool hadFight = Session.fight is not null;
            Call(nameof(PreFightRequest), request, code);
            AssertEqual(0, Pushes.Count, $"{name}: refusal emits no pushes");
            AssertEqual(true, stages.SequenceEqual(Session.stage.ToBson()), $"{name}: refusal preserves stage state");
            AssertEqual(hadFight, Session.fight is not null, $"{name}: refusal never authorizes a fight");
        }

        public FightSettleResponse Settle(string name, PreFightResponse fight, int addStars, bool isWin = true, bool isForceExit = false)
        {
            if (fight.FightData is null)
                throw new InvalidDataException($"{name}: no accepted fight to settle.");
            Call(nameof(FightSettleRequest), TowerSettle(fight.FightData.StageId, fight.FightData.FightId,
                Session.player.PlayerData.Id, addStars, isWin, isForceExit), 0);
            FightSettleResponse response = MessagePackSerializer.Deserialize<FightSettleResponse>(LastResponseContent);
            if (response.Settle is null)
                throw new InvalidDataException($"{name}: an accepted settle has no payload.");
            return response;
        }

        public void RejectSettle(string name, FightSettleRequest request, int code)
        {
            byte[] stages = Session.stage.ToBson();
            Call(nameof(FightSettleRequest), request, code);
            AssertEqual(0, Pushes.Count, $"{name}: refusal emits no pushes");
            AssertEqual(true, stages.SequenceEqual(Session.stage.ToBson()), $"{name}: refusal preserves stage state");
        }

        public void LeaveFight() => Call(nameof(LeaveFightRequest), new LeaveFightRequest(), 0);

        public void AssertStagePush(int stageId, long starsMark, string name)
        {
            Packet.Push push = Pushes.LastOrDefault(candidate => candidate.Name == nameof(NotifyStageData))
                ?? throw new InvalidDataException($"{name}: no NotifyStageData push was emitted.");
            NotifyStageData data = (NotifyStageData)MessagePackDeserialize(typeof(NotifyStageData), push.Content)!;
            StageDatum datum = data.StageList.SingleOrDefault(stage => stage.StageId == stageId)
                ?? throw new InvalidDataException($"{name}: NotifyStageData is missing stage {stageId}.");
            AssertEqual(true, datum.Passed, $"{name} Passed");
            AssertEqual(starsMark, datum.StarsMark, $"{name} StarsMark");
        }

        public JObject Call(string requestName, object? request, int? expectedCode, int? packetId = null)
        {
            Pushes.Clear();
            int id = packetId ?? ++this.packetId;
            InvokeRegisteredRequestHandler(requestName, Session, id, request);
            for (int index = 0; index < 256; index++)
            {
                Packet packet = Harness.ReadPacket($"{requestName} result {index}");
                if (packet.Type == Packet.ContentType.Push)
                {
                    Pushes.Add(MessagePackSerializer.Deserialize<Packet.Push>(packet.Content));
                    continue;
                }

                AssertEqual(Packet.ContentType.Response, packet.Type, $"{requestName} response packet type");
                Packet.Response response = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                AssertEqual(id, response.Id, $"{requestName} packet id correlation");
                AssertEqual(requestName.Replace("Request", "Response", StringComparison.Ordinal), response.Name,
                    $"{requestName} response packet name");
                LastResponseContent = response.Content;
                JObject body = JObject.Parse(MessagePackSerializer.ConvertToJson(response.Content));
                int code = RequiredValue<int>(body, "Code", JTokenType.Integer, requestName);
                if (expectedCode.HasValue)
                    AssertEqual(expectedCode.Value, code, $"{requestName} code");
                if (Harness.TryReadAvailablePacket($"{requestName} trailing packet", out Packet extra))
                    throw new InvalidDataException($"{requestName}: unexpected trailing packet after the response ({extra.Type}).");
                return body;
            }

            throw new InvalidDataException($"{requestName}: no correlated response within 256 packets.");
        }

        /// <summary>Invokes a request whose durable write must fail: no response and no push may escape.</summary>
        public void CallThrows(string requestName, object? request, string name)
        {
            Pushes.Clear();
            int id = ++packetId;
            bool threw = false;
            try
            {
                InvokeRegisteredRequestHandler(requestName, Session, id, request);
            }
            catch (InvalidDataException)
            {
                threw = true;
            }
            AssertEqual(true, threw, $"{name}: a failed durable write must abort the request");
            AssertEqual(false, Harness.TryReadAvailablePacket($"{name}: failed durable write output", out _),
                $"{name}: a failed durable write emits no response or push");
        }

        public void Reject(string requestName, object? request, int code, string name)
        {
            byte[] player = Session.player.ToBson();
            byte[] inventory = Session.inventory.ToBson();
            byte[] roster = Session.character.ToBson();
            byte[] stages = Session.stage.ToBson();
            Call(requestName, request, code);
            AssertEqual(0, Pushes.Count, $"{name}: rejection emits no push");
            AssertEqual(true, player.SequenceEqual(Session.player.ToBson()), $"{name}: rejection preserves player state");
            AssertEqual(true, inventory.SequenceEqual(Session.inventory.ToBson()), $"{name}: rejection preserves inventory");
            AssertEqual(true, roster.SequenceEqual(Session.character.ToBson()), $"{name}: rejection preserves roster");
            AssertEqual(true, stages.SequenceEqual(Session.stage.ToBson()), $"{name}: rejection preserves stage state");
        }

        public NotifyLoginCharacterTowerData LoginData() => (NotifyLoginCharacterTowerData)RequiredMethod(
            RequiredAscNetGameServerType("AscNet.GameServer.Handlers.TowerModule"),
            "BuildLoginData",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
            [typeof(Session)]).Invoke(null, [Session])!;

        public JObject LoginPush()
        {
            RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.TowerModule"),
                "PrepareLogin",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                [typeof(Session)]).Invoke(null, [Session]);
            return ReadPushMapPayload(Harness, "NotifyLoginCharacterTowerData", "CharacterTower login push");
        }

        public void Relog(string name)
        {
            Player player = BsonSerializer.Deserialize<Player>(Players.LastSuccessfulReplacementBson ?? Session.player.ToBson());
            Character character = BsonSerializer.Deserialize<Character>(Characters.LastSuccessfulReplacementBson ?? Session.character.ToBson());
            Inventory inventory = BsonSerializer.Deserialize<Inventory>(Inventories.LastSuccessfulReplacementBson ?? Session.inventory.ToBson());
            Stage stage = BsonSerializer.Deserialize<Stage>(Session.stage.ToBson());
            Harness.Dispose();
            Harness = new(character, player, inventory, $"character-tower-relog-{name}");
            Session.stage = stage;
        }

        public void Dispose()
        {
            Harness.Dispose();
            collections.Dispose();
        }
    }

    private static CharacterData TowerCharacterData(int characterId) => new()
    {
        Id = (uint)characterId,
        Level = 80,
        Quality = 5,
        InitQuality = 5,
        Star = 5,
        Grade = 1,
        TrustLv = 1,
        CreateTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        CharacterHeadInfo = new() { HeadFashionId = 0, HeadFashionType = 1 }
    };

    #endregion

    #region Oracles and assertions

    private static CharacterTowerChapterTable TowerChapter(int chapterId) =>
        TableReaderV2.Parse<CharacterTowerChapterTable>().Single(row => row.Id == chapterId);

    private static CharacterTowerRelationTable TowerRelation(int relationId) =>
        TableReaderV2.Parse<CharacterTowerRelationTable>().Single(row => row.Id == relationId);

    private static CharacterTowerTreasureTable TowerTreasure(int treasureId) =>
        TableReaderV2.Parse<CharacterTowerTreasureTable>().Single(row => row.TreasureId == treasureId);

    /// <summary>Authored Commandant requirement of a chapter (ConditionType 10101) or of a condition id.</summary>
    private static int TowerAuthoredLevel(int chapterOrConditionId)
    {
        CharacterTowerChapterTable? chapter = TableReaderV2.Parse<CharacterTowerChapterTable>()
            .FirstOrDefault(row => row.Id == chapterOrConditionId);
        List<int> conditionIds = chapter is not null
            ? (chapter.ActivityCondition.Count > 0 ? chapter.ActivityCondition : chapter.OpenCondition)
            : [chapterOrConditionId];
        return conditionIds
            .Select(conditionId => TableReaderV2.Parse<AscNet.Table.V2.share.condition.ConditionTable>()
                .FirstOrDefault(condition => condition.Id == conditionId && condition.Type == 10101))
            .Where(condition => condition is not null)
            .Select(condition => condition!.Params[0])
            .DefaultIfEmpty(0)
            .First();
    }

    private static long TowerCalculatedPower(Session session, CharacterData character) => (long)RequiredMethod(
        RequiredAscNetGameServerType("AscNet.GameServer.Game.CharacterPower"),
        "Calculate",
        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
        [typeof(Session), typeof(CharacterData)]).Invoke(null, [session, character])!;

    private static bool TowerContainsEvent(List<dynamic> eventIds, int expected)
    {
        foreach (dynamic value in eventIds)
        {
            if (Convert.ToInt32(value) == expected)
                return true;
        }

        return false;
    }

    private static PreFightRequest TowerPreFight(int stageId, IReadOnlyList<int> cardIds, int challengeCount = 1, int speedrunStageId = 0) => new()
    {
        PreFightData = new()
        {
            StageId = (uint)stageId,
            ChallengeCount = challengeCount,
            SpeedrunStageId = (uint)speedrunStageId,
            CaptainPos = 1,
            FirstFightPos = 1,
            CardIds = cardIds.Select(cardId => (uint)cardId).ToList(),
            RobotIds = []
        }
    };

    private static FightSettleRequest TowerSettle(uint stageId, long fightId, long playerId, int addStars, bool isWin = true, bool isForceExit = false)
    {
        FightSettleRequest request = CreateMissingStageSettleRequest(stageId, fightId, playerId);
        request.Result.AddStars = addStars;
        request.Result.IsWin = isWin;
        request.Result.IsForceExit = isForceExit;
        return request;
    }

    private static Packet.Push TowerReadPush(LoopbackSessionHarness harness, string name)
    {
        Packet packet = harness.ReadPacket(name);
        AssertEqual(Packet.ContentType.Push, packet.Type, $"{name} packet type");
        return MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
    }

    /// <summary>The generic authored monster-level source the fight payload must mirror, resolved exactly like FightModule.</summary>
    private static void TowerAssertMonsterLevel(CharacterTowerCase test, PreFightResponse fight, int stageId)
    {
        object? control = RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.FightModule"),
                "ResolveStageLevelControl",
                BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(uint), typeof(int)])
            .Invoke(null, [(uint)stageId, (int)test.Session.player.PlayerData.Level]);
        List<int> expected = [];
        if (control is not null)
        {
            MemberInfo member = RequiredDataMember(control.GetType(), "MonsterLevel");
            expected = member switch
            {
                PropertyInfo property => ((System.Collections.IEnumerable)property.GetValue(control)!).Cast<int>().ToList(),
                FieldInfo field => ((System.Collections.IEnumerable)field.GetValue(control)!).Cast<int>().ToList(),
                _ => throw new InvalidDataException("The stage level control monster-level member is unsupported.")
            };
        }

        AssertEqual(expected.Count, fight.FightData!.MonsterLevel?.Count ?? 0,
            $"stage {stageId} monster level comes from the authored level control");
        for (int index = 0; index < expected.Count; index++)
            AssertEqual(expected[index], fight.FightData.MonsterLevel![index], $"stage {stageId} monster level {index}");
    }

    /// <summary>
    /// Asserts the client-consumed fields exist. The native client only proves that these fields are consumed,
    /// not that a server may not send additional ones, so extra protocol fields are deliberately permitted.
    /// </summary>
    private static void TowerAssertRequiredFields(JObject payload, string name, params string[] requiredFields)
    {
        foreach (string field in requiredFields)
        {
            JToken? token = payload.TryGetValue(field, out JToken? found) ? found : null;
            if (token is null || token.Type == JTokenType.Null)
                throw new InvalidDataException($"{name}: missing non-nil native field '{field}'.");
        }
    }

    private static void TowerAssertArray(JToken? token, JTokenType elementType, string name)
    {
        if (token is not JArray array)
            throw new InvalidDataException($"{name}: expected an array, got {token?.Type.ToString() ?? "nil"}.");
        foreach (JToken element in array)
        {
            if (element.Type != elementType)
                throw new InvalidDataException($"{name}: expected {elementType} elements, got {element.Type}.");
        }
    }

    private static void TowerAssertStrings(IReadOnlyList<string> expected, IReadOnlyList<string> actual, string name)
    {
        AssertEqual(expected.Count, actual.Count, $"{name} count");
        for (int index = 0; index < expected.Count; index++)
            AssertEqual(expected[index], actual[index], $"{name}[{index}]");
    }

    /// <summary>
    /// Plain-item share of one authored reward id: goods whose TemplateId sits in the item band (below the
    /// 1e6 reward-type bands) are the ones the inventory can actually deliver, summed per item id.
    /// </summary>
    private static Dictionary<int, long> TowerItemGoods(int rewardId, string name) =>
        ResolveRewardGoods(rewardId, TableReaderV2.Parse<RewardGoodsTable>(), name)
            .Where(row => row.TemplateId is > 0 and < 1_000_000 && row.Count > 0)
            .GroupBy(row => row.TemplateId)
            .ToDictionary(group => group.Key, group => group.Sum(row => (long)row.Count));

    private static Dictionary<int, long> TowerBalances(Session session) =>
        session.inventory.Items
            .GroupBy(item => item.Id)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Count));

    /// <summary>
    /// Asserts the claim delivered exactly the authored plain-item goods, applying the same max-count rule the
    /// inventory itself enforces, so an unrelated or missing balance move fails too.
    /// </summary>
    private static void TowerAssertItemGoods(Session session, IReadOnlyDictionary<int, long> before, int rewardId, string name)
    {
        Dictionary<int, long> after = TowerBalances(session);
        Dictionary<int, long> goods = TowerItemGoods(rewardId, name);
        foreach (int itemId in before.Keys.Union(after.Keys).Union(goods.Keys).OrderBy(id => id))
        {
            long cap = Inventory.GetMaxCount(TableReaderV2.Parse<ItemTable>().Find(row => row.Id == itemId));
            long expected = Math.Min(before.GetValueOrDefault(itemId) + goods.GetValueOrDefault(itemId), cap);
            AssertEqual(expected, after.GetValueOrDefault(itemId), $"{name} balance for item {itemId}");
        }
    }

    /// <summary>Asserts nothing moved in the inventory: the claim paid nothing new or was refused.</summary>
    private static void TowerAssertNoItemChange(Session session, IReadOnlyDictionary<int, long> before, string name)
    {
        Dictionary<int, long> after = TowerBalances(session);
        foreach (int itemId in before.Keys.Union(after.Keys).OrderBy(id => id))
            AssertEqual(before.GetValueOrDefault(itemId), after.GetValueOrDefault(itemId), $"{name} balance for item {itemId}");
    }

    /// <summary>Asserts the claim response reports exactly the authored reward goods of one reward id.</summary>
    private static void TowerAssertRewards(JObject body, int rewardId, string name)
    {
        List<RewardGoodsTable> goods = ResolveRewardGoods(rewardId, TableReaderV2.Parse<RewardGoodsTable>(), name);
        JArray rewards = RequiredValue<JArray>(body, "Rewards", JTokenType.Array, name);
        AssertEqual(goods.Count, rewards.Count, $"{name} reward count");
        foreach (RewardGoodsTable row in goods)
        {
            JObject? entry = rewards.OfType<JObject>().FirstOrDefault(candidate =>
                candidate.Value<int>("TemplateId") == row.TemplateId && candidate.Value<int>("Count") == row.Count);
            if (entry is null)
                throw new InvalidDataException($"{name}: missing the authored goods row {row.Id} ({row.TemplateId} x{row.Count}).");
            _ = RequiredValue<int>(entry, "RewardType", JTokenType.Integer, name);
        }
    }

    /// <summary>Asserts one chapter entry of the native login push keeps its typed fields (extra fields tolerated).</summary>
    private static void TowerAssertChapterInfo(JObject loginPush, int chapterId, string name)
    {
        JObject data = loginPush["CharacterTowerDataDb"] as JObject
            ?? throw new InvalidDataException($"{name}: CharacterTowerDataDb is not a map.");
        JObject? chapter = ((JArray?)data["ChapterInfos"])?.OfType<JObject>()
            .FirstOrDefault(entry => entry.Value<int?>("ChapterId") == chapterId);
        if (chapter is null)
            throw new InvalidDataException($"{name}: the login push is missing chapter {chapterId}.");
        TowerAssertRequiredFields(chapter, $"{name} chapter {chapterId}", "ChapterId", "ChapterRewardData", "TreasureData",
            "StageRewardData", "VideoedIds", "TriggerConditions", "RelationInfos");
        AssertEqual(JTokenType.Integer, chapter["ChapterId"]!.Type, $"{name} chapter ChapterId type");
        foreach (string field in new[] { "ChapterRewardData", "TreasureData", "StageRewardData", "VideoedIds", "TriggerConditions" })
            TowerAssertArray(chapter[field], JTokenType.Integer, $"{name} chapter {chapterId}.{field}");
        TowerAssertArray(chapter["RelationInfos"], JTokenType.Object, $"{name} chapter {chapterId}.RelationInfos");
        foreach (JObject relation in ((JArray)chapter["RelationInfos"]!).OfType<JObject>())
        {
            TowerAssertRequiredFields(relation, $"{name} relation", "RelationId", "FightEventIds", "StoryIds", "FinishConditions");
            AssertEqual(JTokenType.Integer, relation["RelationId"]!.Type, $"{name} relation RelationId type");
            TowerAssertArray(relation["FightEventIds"], JTokenType.Integer, $"{name} relation FightEventIds");
            TowerAssertArray(relation["StoryIds"], JTokenType.String, $"{name} relation StoryIds");
            TowerAssertArray(relation["FinishConditions"], JTokenType.Integer, $"{name} relation FinishConditions");
        }
    }

    #endregion
}
