using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.GameServer.Handlers;
using AscNet.GameServer;
using AscNet.Common.Util;
using AscNet.Table.V2.share.character;
using AscNet.Table.V2.share.character.grade;
using AscNet.Table.V2.share.character.skill;
using AscNet.Table.V2.share.item;
using AscNet.Table.V2.share.condition;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidateVersion48Frames()
    {
        PacketFactory.LoadPacketHandlers();
        foreach (int id in new[] { 1411003, 1421003 })
        {
            AssertEqual(true, Character.IsOwnableCharacter((uint)id), $"4.8 frame {id} has complete acquisition tables");
            Character roster = CreateTestCharacterRoster(id, 1);
            AssertEqual(true, roster.Characters.Any(character => character.Id == id), $"4.8 frame {id} can be acquired");
        }

        const int frameId = 1421003;
        Character trainedRoster = CreateTestCharacterRoster(frameId, 1);
        trainedRoster.Uid = 48_142;
        CharacterData trained = RequiredCharacterData(trainedRoster, frameId);
        int initialQuality = trained.Quality;
        const int standardSkillId = 142301;
        int initialSkillLevel = trained.SkillList.Single(skill => skill.Id == standardSkillId).Level;
        int standardSkillMaxLevel = TableReaderV2.Parse<CharacterSkillUpgradeTable>()
            .Where(row => row.SkillId == standardSkillId).Max(row => row.Level);
        AssertEqual(true, initialSkillLevel < standardSkillMaxLevel,
            "Anabasis has standard skill progression to train");
        int initialStar = trained.Star;
        int trainingItemId = TableReaderV2.Parse<ItemTable>()
            .Single(row => row.ItemType == (int)ItemType.NormalConsumableItem).Id;
        var player = CreateDrawCompatibilityPlayer(trainedRoster.Uid);
        player.PlayerData.Level = 52;
        using (MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out _, out RecordingMongoCollectionProxy<Character> characterSaves,
            out RecordingMongoCollectionProxy<Inventory> inventorySaves))
        using (LoopbackSessionHarness harness = new(trainedRoster, player,
            CreateDrawCompatibilityInventory(trainedRoster.Uid, [new Item { Id = trainingItemId, Count = 1 }]), "v48-frame-training"))
        {
            const int packetId = 48_142;
            InvokeRegisteredRequestHandler(nameof(CharacterUseOneClickItemRequest), harness.Session, packetId,
                new CharacterUseOneClickItemRequest { CharacterId = frameId });
            AssertEqual(0L, harness.Session.inventory.Items.Single(item => item.Id == trainingItemId).Count,
                "Transition Device consumes one item");
            _ = ReadPushPayload<NotifyItemDataList>(harness, nameof(NotifyItemDataList), "training inventory push");
            CharacterData pushed = ReadPushPayload<NotifyCharacterDataList>(harness,
                nameof(NotifyCharacterDataList), "training character push").CharacterDataList.Single();
            AssertEqual(Character.characterLevelUpTemplates
                .Where(row => row.Type == TableReaderV2.Parse<CharacterTable>()
                    .First(frame => frame.Id == frameId).LevelUpTemplateId)
                .Max(row => row.Level), pushed.Level,
                "Transition Device reaches authored level cap independent of Commandant level");
            AssertEqual(TableReaderV2.Parse<CharacterGradeTable>().Where(row => row.CharacterId == frameId)
                .Max(row => row.Grade), pushed.Grade, "training reaches authored maximum grade");
            AssertEqual(initialQuality, pushed.Quality, "training does not evolve quality");
            AssertEqual(initialStar, pushed.Star, "training does not evolve stars");
            AssertEqual(0, pushed.EnhanceSkillList.Count, "training does not unlock Leap skills");
            AssertEqual(standardSkillMaxLevel,
                pushed.SkillList.Single(skill => skill.Id == standardSkillId).Level,
                "training upgrades eligible standard skills to their authored cap");
            _ = ReadPushPayload<NotifyTask>(harness, nameof(NotifyTask), "training task progress push");
            AssertEqual(0, ReadResponsePayload<CharacterUseOneClickItemResponse>(harness, packetId,
                nameof(CharacterUseOneClickItemResponse), "training response").Code, "training succeeds");
            AssertEqual(1, characterSaves.ReplaceOneCalls, "training saves frame");
            AssertEqual(1, inventorySaves.ReplaceOneCalls, "training saves spent item");
        }

        // 4.9 client XCharacterControl.RequestRoleCultureSpecialTraining has no character Type filter.
        // 1421003 is Type 1; the other seven Type 2 (Transcendant) characters are every row with Type == 2.
        int[] type2 = TableReaderV2.Parse<CharacterTable>().Where(row => row.Type == 2).Select(row => row.Id).ToArray();
        AssertEqual(7, type2.Length, "Type 2 character count");
        AssertEqual(true, type2.Contains(1531003), "Selena: Tempest is Type 2");
        foreach (int id in type2.Prepend(frameId))
            ValidateTransitionDevice(id, 48_200 + id % 1000);
        ValidateTransitionDeviceRejections();
    }

    private static void ValidateTransitionDevice(int characterId, int uid)
    {
        const int itemId = 30000;
        Character roster = CreateTestCharacterRoster(characterId, 1);
        roster.Uid = uid;
        CharacterData initial = RequiredCharacterData(roster, characterId);
        int initialQuality = initial.Quality, initialStar = initial.Star;
        var initialEnhance = initial.EnhanceSkillList.Select(skill => (skill.Id, skill.Level)).ToArray();
        int levelUpTemplate = TableReaderV2.Parse<CharacterTable>().Single(row => row.Id == characterId).LevelUpTemplateId;
        int expectedLevel = Character.characterLevelUpTemplates.Where(row => row.Type == levelUpTemplate).Max(row => row.Level);
        int expectedGrade = TableReaderV2.Parse<CharacterGradeTable>().Where(row => row.CharacterId == characterId).Max(row => row.Grade);
        HashSet<int> liberationConditions = TableReaderV2.Parse<ConditionTable>().Where(row => row.Type == 11102).Select(row => row.Id).ToHashSet();
        var upgrades = TableReaderV2.Parse<CharacterSkillUpgradeTable>().ToLookup(row => row.SkillId);
        // Standard skills reach their CharacterSkillUpgrade cap; the final liberation skill is a toggle, not an upgrade.
        var expectedSkills = initial.SkillList.ToDictionary(skill => skill.Id, skill =>
            upgrades[(int)skill.Id].Any(row => row.Level == 0 && row.ConditionId.Any(liberationConditions.Contains))
                ? skill.Level : upgrades[(int)skill.Id].Max(row => row.Level));
        AssertEqual(true, expectedSkills.Any(skill => skill.Value > initial.SkillList.Single(s => s.Id == skill.Key).Level),
            $"{characterId} has standard skills to train");

        Player player = CreateDrawCompatibilityPlayer(uid);
        player.PlayerData.Level = 52;
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out _, out RecordingMongoCollectionProxy<Character> characterSaves,
            out RecordingMongoCollectionProxy<Inventory> inventorySaves);
        using LoopbackSessionHarness harness = new(roster, player,
            CreateDrawCompatibilityInventory(uid, [new Item { Id = itemId, Count = 3 }]), $"transition-{characterId}");
        int packetId = uid;
        InvokeRegisteredRequestHandler(nameof(CharacterUseOneClickItemRequest), harness.Session, packetId,
            new CharacterUseOneClickItemRequest { CharacterId = characterId });
        NotifyItemDataList itemPush = ReadPushPayload<NotifyItemDataList>(harness, nameof(NotifyItemDataList), $"{characterId} item push");
        AssertEqual(2L, itemPush.ItemDataList.Single(item => item.Id == itemId).Count, $"{characterId} item push carries the one spent device");
        CharacterData pushed = ReadPushPayload<NotifyCharacterDataList>(harness, nameof(NotifyCharacterDataList),
            $"{characterId} character push").CharacterDataList.Single();
        _ = ReadPushPayload<NotifyTask>(harness, nameof(NotifyTask), $"{characterId} task push");
        AssertEqual(0, ReadResponsePayload<CharacterUseOneClickItemResponse>(harness, packetId,
            nameof(CharacterUseOneClickItemResponse), $"{characterId} response").Code, $"{characterId} quick upgrade succeeds");
        AssertEqual(2L, harness.Session.inventory.Items.Single(item => item.Id == itemId).Count, $"{characterId} consumes exactly one device");

        void AssertTrained(CharacterData actual, string source)
        {
            AssertEqual(expectedLevel, actual.Level, $"{characterId} {source} level");
            AssertEqual(expectedGrade, actual.Grade, $"{characterId} {source} grade");
            AssertEqual(initialQuality, actual.Quality, $"{characterId} {source} quality unchanged");
            AssertEqual(initialStar, actual.Star, $"{characterId} {source} star unchanged");
            AssertEqual(true, initialEnhance.SequenceEqual(actual.EnhanceSkillList.Select(skill => (skill.Id, skill.Level))),
                $"{characterId} {source} leap skills unchanged");
            foreach ((uint skillId, int level) in expectedSkills)
                AssertEqual(level, actual.SkillList.Single(skill => skill.Id == skillId).Level, $"{characterId} {source} skill {skillId}");
        }
        AssertTrained(pushed, "push");
        AssertEqual(1, characterSaves.ReplaceOneCalls, $"{characterId} saves character once");
        AssertEqual(1, inventorySaves.ReplaceOneCalls, $"{characterId} saves inventory once");
        AssertTrained(RequiredCharacterData(BsonSerializer.Deserialize<Character>(characterSaves.LastSuccessfulReplacementBson
            ?? throw new InvalidDataException("Transition Device character was not saved.")), characterId), "reload");
        AssertEqual(2L, BsonSerializer.Deserialize<Inventory>(inventorySaves.LastSuccessfulReplacementBson
            ?? throw new InvalidDataException("Transition Device inventory was not saved."))
            .Items.Single(item => item.Id == itemId).Count, $"{characterId} spent device survives reload");

        // Nothing left to train (the client greys the toggle): CodeText 20009064, no item spent, no save.
        InvokeRegisteredRequestHandler(nameof(CharacterUseOneClickItemRequest), harness.Session, packetId + 5000,
            new CharacterUseOneClickItemRequest { CharacterId = characterId });
        AssertEqual(20009064, ReadResponsePayload<CharacterUseOneClickItemResponse>(harness, packetId + 5000,
            nameof(CharacterUseOneClickItemResponse), $"{characterId} maxed response").Code, $"{characterId} maxed rejects");
        AssertEqual(2L, harness.Session.inventory.Items.Single(item => item.Id == itemId).Count, $"{characterId} maxed keeps device");
        AssertEqual(1, characterSaves.ReplaceOneCalls, $"{characterId} maxed does not save");
    }

    private static void ValidateTransitionDeviceRejections()
    {
        const int itemId = 30000, selena = 1531003, uid = 48_190;
        Character roster = CreateTestCharacterRoster(selena, 1);
        roster.Uid = uid;
        void Reject(int characterId, long deviceCount, int expectedCode, string name)
        {
            Player player = CreateDrawCompatibilityPlayer(uid);
            player.PlayerData.Level = 52;
            byte[] before = roster.ToBson();
            using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
                out _, out RecordingMongoCollectionProxy<Character> characterSaves,
                out RecordingMongoCollectionProxy<Inventory> inventorySaves);
            using LoopbackSessionHarness harness = new(BsonSerializer.Deserialize<Character>(before), player,
                CreateDrawCompatibilityInventory(uid, deviceCount > 0 ? [new Item { Id = itemId, Count = deviceCount }] : []),
                $"transition-reject-{name}");
            InvokeRegisteredRequestHandler(nameof(CharacterUseOneClickItemRequest), harness.Session, uid,
                new CharacterUseOneClickItemRequest { CharacterId = characterId });
            AssertEqual(expectedCode, ReadResponsePayload<CharacterUseOneClickItemResponse>(harness, uid,
                nameof(CharacterUseOneClickItemResponse), name).Code, $"{name} code");
            AssertEqual(true, before.SequenceEqual(harness.Session.character.ToBson()), $"{name} leaves character unchanged");
            AssertEqual(deviceCount, harness.Session.inventory.Items.SingleOrDefault(item => item.Id == itemId)?.Count ?? 0L, $"{name} keeps device");
            AssertEqual(0, characterSaves.ReplaceOneCalls, $"{name} saves no character");
            AssertEqual(0, inventorySaves.ReplaceOneCalls, $"{name} saves no inventory");
        }
        Reject(1521003, 1, 20009021, "not owned");
        Reject(int.MaxValue, 1, 20009021, "unknown character");
        Reject(selena, 0, 20012004, "no device");
    }
}
