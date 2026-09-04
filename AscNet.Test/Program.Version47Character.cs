using System.Reflection;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Common.Util;
using AscNet.Table.V2.share.character;
using AscNet.Table.V2.share.character.enhanceskill;
using AscNet.Table.V2.share.character.quality;
using AscNet.Table.V2.share.character.skill;
using AscNet.Table.V2.share.condition;
using AscNet.Table.V2.share.headportrait;
using AscNet.Table.V2.share.fashion;
using AscNet.Table.V2.share.robot;
using MessagePack;
using AscNet.Table.V2.share.exhibition;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace AscNet.Test;

internal partial class Program
{
    /// <summary>
    /// 4.7 character-owned slices: portrait/frame equip validation and login timeout
    /// reconciliation, coating (FashionUse) and avatar-head (CharacterSetHeadInfo) ownership
    /// semantics, Karenina/Pyroath table-backed skill and leap normalization plus the
    /// enhance unlock/upgrade/switch trust boundaries. Self-contained; wired by the
    /// integration owner (not called from Program.Main here).
    /// </summary>
    private static void ValidateVersion47CharacterCompatibility()
    {
        PacketFactory.LoadPacketHandlers();

        ValidateVersion47HeadEquipValidation();
        ValidateVersion47HeadTimeoutReconciliation();

        ValidateVersion47CharacterHeadSelectionCompatibility();
        ValidateVersion47GeneralSkillPreFightCompatibility();
        ValidateVersion47ObserverPreFightCompatibility();
        ValidateVersion47QualityGatedSkillReconciliation();
    }

    private static void ValidateVersion47QualityGatedSkillReconciliation()
    {
        List<(CharacterSkillTable Character, uint SkillId, int FirstQuality, int SecondQuality)> candidates =
            (from character in TableReaderV2.Parse<CharacterSkillTable>()
             from groupId in character.SkillGroupId.Where(id => id > 0).Distinct()
             let groupRow = TableReaderV2.Parse<CharacterSkillGroupTable>().FirstOrDefault(row => row.Id == groupId)
             let candidateSkillId = (uint)(groupRow?.SkillId.FirstOrDefault() ?? 0)
             let upgrades = TableReaderV2.Parse<CharacterSkillUpgradeTable>().Where(row => row.SkillId == (int)candidateSkillId).ToList()
             let gated = upgrades.Where(row => row.Level is 0 or 1 && row.ConditionId is { Count: > 0 }).OrderBy(row => row.Level).ToList()
             let qualities = gated.Select(row => row.ConditionId
                 .Select(id => TableReaderV2.Parse<ConditionTable>().FirstOrDefault(condition => condition.Id == id))
                 .Where(condition => condition is not null && condition.Type == 13105 && condition.Params.Count > 0)
                 .Select(condition => condition!.Params[0])
                 .DefaultIfEmpty(-1)
                 .Max()).ToList()
             where candidateSkillId > 0
                 && Character.CharacterSkillMaxLevel((int)candidateSkillId) == 2
                 && gated.Count >= 2
                 && qualities.Count >= 2
                 && qualities[0] >= 0
                 && qualities[1] > qualities[0]
             select (character, candidateSkillId, qualities[0], qualities[1])).ToList();
        (CharacterSkillTable characterRow, uint skillId, int firstQuality, int secondQuality) = candidates.First();

        AscNet.Common.Database.Character lockedRoster = CreateTestCharacterRoster(characterRow.CharacterId, 80);
        CharacterData locked = RequiredCharacterData(lockedRoster, characterRow.CharacterId);
        locked.Quality = Math.Max(0, firstQuality - 1);
        locked.SkillList.RemoveAll(skill => skill.Id == skillId);
        lockedRoster.NormalizeCharactersForCurrentTables([]);
        AssertEqual(false, locked.SkillList.Any(skill => skill.Id == skillId), "quality-gated skill stays absent below first gate");

        AscNet.Common.Database.Character intermediateRoster = CreateTestCharacterRoster(characterRow.CharacterId, 80);
        CharacterData intermediate = RequiredCharacterData(intermediateRoster, characterRow.CharacterId);
        intermediate.Quality = firstQuality;
        intermediate.Star = int.MaxValue;
        intermediate.SkillList.RemoveAll(skill => skill.Id == skillId);
        intermediateRoster.NormalizeCharactersForCurrentTables([]);
        AssertEqual(1, intermediate.SkillList.Single(skill => skill.Id == skillId).Level, "quality-gated skill reaches intermediate rank");

        AscNet.Common.Database.Character maxRoster = CreateTestCharacterRoster(characterRow.CharacterId, 80);
        CharacterData max = RequiredCharacterData(maxRoster, characterRow.CharacterId);
        max.Quality = secondQuality;
        max.Star = int.MaxValue;
        max.SkillList.RemoveAll(skill => skill.Id == skillId);
        max.SkillList.Add(new CharacterSkill { Id = skillId, Level = 1 });
        maxRoster.NormalizeCharactersForCurrentTables([]);
        AssertEqual(2, max.SkillList.Single(skill => skill.Id == skillId).Level, "quality-gated skill reaches max rank");

        CharacterSkill maxSkill = max.SkillList.Single(skill => skill.Id == skillId);
        bool changed = maxRoster.NormalizeCharactersForCurrentTables([]);
        AssertEqual(false, changed, "quality-gated normalization is idempotent");
        AssertEqual(2, maxSkill.Level, "quality-gated max level remains stable");

        var sharedQualityGate = (from character in TableReaderV2.Parse<CharacterSkillTable>()
                                 where Character.IsOwnableCharacter((uint)character.CharacterId)
                                 from groupId in character.SkillGroupId.Where(id => id > 0).Distinct()
                                 let groupRow = TableReaderV2.Parse<CharacterSkillGroupTable>()
                                     .FirstOrDefault(row => row.Id == groupId)
                                 let defaultSkillId = groupRow?.SkillId.FirstOrDefault() ?? 0
                                 let initial = TableReaderV2.Parse<CharacterSkillUpgradeTable>()
                                     .FirstOrDefault(row => row.SkillId == defaultSkillId && row.Level == 0)
                                 where defaultSkillId > 0
                                     && Character.CharacterSkillMaxLevel(defaultSkillId) == 1
                                     && initial?.ConditionId.Count(id => id > 0) == 1
                                 let condition = TableReaderV2.Parse<ConditionTable>()
                                     .First(row => row.Id == initial!.ConditionId.First(id => id > 0))
                                 where condition.Type == 13105 && condition.Params.Count > 0
                                     && string.IsNullOrWhiteSpace(condition.Formula)
                                     && (condition.Params.Count <= 2 || condition.Params[2] == 0)
                                 let qualityRows = TableReaderV2.Parse<CharacterQualityTable>()
                                     .Where(row => row.CharacterId == character.CharacterId).ToList()
                                 let lowerQuality = qualityRows
                                     .Where(row => row.Quality > 0 && row.Quality < condition.Params[0])
                                     .OrderBy(row => row.Quality).FirstOrDefault()
                                 where lowerQuality is not null
                                     && qualityRows.Any(row => row.Quality == condition.Params[0])
                                 select new
                                 {
                                     character.CharacterId,
                                     SkillId = (uint)defaultSkillId,
                                     ConditionId = condition.Id,
                                     EligibleQuality = condition.Params[0],
                                     LowerQuality = lowerQuality.Quality
                                 })
            .GroupBy(candidate => candidate.ConditionId)
            .Select(group => group.DistinctBy(candidate => candidate.CharacterId).Take(2).ToArray())
            .First(pair => pair.Length == 2);
        AscNet.Common.Database.Character mixedRoster =
            CreateTestCharacterRoster(sharedQualityGate[0].CharacterId, 80);
        mixedRoster.AddCharacter((uint)sharedQualityGate[1].CharacterId, 80);
        for (int eligibleIndex = 0; eligibleIndex < 2; eligibleIndex++)
        {
            for (int index = 0; index < 2; index++)
            {
                var fixture = sharedQualityGate[index];
                CharacterData character = RequiredCharacterData(mixedRoster, fixture.CharacterId);
                character.Quality = index == eligibleIndex ? fixture.EligibleQuality : fixture.LowerQuality;
                character.Star = 0;
                character.SkillList.RemoveAll(skill => skill.Id == fixture.SkillId);
            }
            mixedRoster.NormalizeCharactersForCurrentTables([]);
            var eligibleFixture = sharedQualityGate[eligibleIndex];
            var lockedFixture = sharedQualityGate[1 - eligibleIndex];
            AssertEqual(1, RequiredCharacterData(mixedRoster, eligibleFixture.CharacterId)
                .SkillList.Single(skill => skill.Id == eligibleFixture.SkillId).Level,
                "shared quality condition unlocks the eligible construct's default skill");
            AssertEqual(false, RequiredCharacterData(mixedRoster, lockedFixture.CharacterId)
                .SkillList.Any(skill => skill.Id == lockedFixture.SkillId),
                "shared quality condition leaves the other construct's default skill locked");
        }

        var liberationCandidate = (from character in TableReaderV2.Parse<CharacterSkillTable>()
                                   from groupId in character.SkillGroupId.Where(id => id > 0)
                                   let groupRow = TableReaderV2.Parse<CharacterSkillGroupTable>()
                                       .FirstOrDefault(row => row.Id == groupId)
                                   from liberationSkillId in groupRow?.SkillId.Take(1) ?? []
                                   let initial = TableReaderV2.Parse<CharacterSkillUpgradeTable>()
                                       .FirstOrDefault(row => row.SkillId == liberationSkillId && row.Level == 0)
                                   from conditionId in initial?.ConditionId ?? []
                                   let condition = TableReaderV2.Parse<ConditionTable>()
                                       .FirstOrDefault(row => row.Id == conditionId)
                                   where condition?.Type == 11102
                                       && condition.Params.Count >= 2
                                       && condition.Params[0] == character.CharacterId
                                   select (character.CharacterId, SkillId: (uint)liberationSkillId,
                                       RequiredLiberation: condition.Params[1])).First();
        AscNet.Common.Database.Character staleLiberationRoster =
            CreateTestCharacterRoster(liberationCandidate.CharacterId, 80);
        CharacterData staleLiberation =
            RequiredCharacterData(staleLiberationRoster, liberationCandidate.CharacterId);
        staleLiberation.LiberateLv = liberationCandidate.RequiredLiberation - 1;
        staleLiberation.SkillList.RemoveAll(skill => skill.Id == liberationCandidate.SkillId);
        staleLiberation.SkillList.Add(new CharacterSkill { Id = liberationCandidate.SkillId, Level = 1 });
        staleLiberationRoster.NormalizeCharactersForCurrentTables([]);
        AssertEqual(false, staleLiberation.SkillList.Any(skill => skill.Id == liberationCandidate.SkillId),
            "locked Ultima Awaken skill is removed from stale accounts");

        AscNet.Common.Database.Character awakenedRoster =
            CreateTestCharacterRoster(liberationCandidate.CharacterId, 80);
        CharacterData awakened = RequiredCharacterData(awakenedRoster, liberationCandidate.CharacterId);
        awakened.LiberateLv = liberationCandidate.RequiredLiberation;
        awakened.SkillList.RemoveAll(skill => skill.Id == liberationCandidate.SkillId);
        int liberationReward = TableReaderV2.Parse<ExhibitionRewardTable>()
            .First(row => row.CharacterId == liberationCandidate.CharacterId
                && row.LevelId >= liberationCandidate.RequiredLiberation).Id;
        awakenedRoster.NormalizeCharactersForCurrentTables([liberationReward]);
        AssertEqual(false, awakened.SkillList.Any(skill => skill.Id == liberationCandidate.SkillId),
            "Ultima eligibility does not perform a manual unlock during normalization");
        awakened.SkillList.Add(new CharacterSkill { Id = liberationCandidate.SkillId, Level = 1 });
        awakenedRoster.NormalizeCharactersForCurrentTables([liberationReward]);
        AssertEqual(1, awakened.SkillList.Single(skill => skill.Id == liberationCandidate.SkillId).Level,
            "claimed liberation milestone retains a learned Ultima skill");

        var ordinaryCandidate = (from character in TableReaderV2.Parse<CharacterSkillTable>()
                                 from groupId in character.SkillGroupId.Where(id => id > 0)
                                 let groupRow = TableReaderV2.Parse<CharacterSkillGroupTable>()
                                     .FirstOrDefault(candidateGroup => candidateGroup.Id == groupId)
                                 from ordinarySkillId in groupRow?.SkillId.Take(1) ?? []
                                 where !TableReaderV2.Parse<CharacterSkillUpgradeTable>()
                                     .Any(row => row.SkillId == ordinarySkillId && row.Level == 0 && row.ConditionId is { Count: > 0 })
                                 select (character.CharacterId, SkillId: (uint)ordinarySkillId)).First();
        AscNet.Common.Database.Character ordinaryRoster =
            CreateTestCharacterRoster(ordinaryCandidate.CharacterId, 80);
        CharacterData ordinaryCharacter =
            RequiredCharacterData(ordinaryRoster, ordinaryCandidate.CharacterId);
        CharacterSkill ordinary = ordinaryCharacter.SkillList.Single(skill => skill.Id == ordinaryCandidate.SkillId);
        int ordinaryLevel = ordinary.Level;
        ordinaryRoster.NormalizeCharactersForCurrentTables([]);
        AssertEqual(ordinaryLevel, ordinaryCharacter.SkillList.Single(skill => skill.Id == ordinary.Id).Level,
            "conditionless ordinary skill is preserved");
    }

    private static void ValidateVersion47ObserverPreFightCompatibility()
    {
        CharacterCareerTable observerCareer = TableReaderV2.Parse<CharacterCareerTable>()
            .First(row => row.Name == "Observer");
        CharacterTable observerRow = TableReaderV2.Parse<CharacterTable>()
            .First(row => row.Career == observerCareer.Type);
        CharacterObsTriggerMagicTable observationRow = TableReaderV2.Parse<CharacterObsTriggerMagicTable>().First();
        int maxLevel = TableReaderV2.Parse<CharacterSkillLevelEffectTable>()
            .Where(row => row.SkillId == observationRow.SkillId)
            .Max(row => row.Level);
        MethodInfo buildMagicIds = RequiredMethod(
            RequiredAscNetGameServerType("AscNet.GameServer.Handlers.FightModule"),
            "BuildObservationMagicIds", BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(IEnumerable<CharacterData>), typeof(CharacterData)]);

        CharacterData locked = new() { Id = (uint)observerRow.Id, SkillList = [] };
        Dictionary<int, int> lockedMagic = (Dictionary<int, int>)buildMagicIds.Invoke(
            null, [new[] { locked }, locked])!;
        AssertEqual(0, lockedMagic.Count, "locked Observer skill omits observation activation");

        CharacterData unlocked = new()
        {
            Id = (uint)observerRow.Id,
            SkillList = [new CharacterSkill { Id = (uint)observationRow.SkillId, Level = maxLevel }]
        };
        Dictionary<string, int> careerTypes = TableReaderV2.Parse<CharacterCareerTable>()
            .ToDictionary(row => row.Name, row => row.Type);
        Dictionary<int, string> careerNames = careerTypes.ToDictionary(pair => pair.Value, pair => pair.Key);
        int tankType = careerTypes["Tank"];
        int amplifierType = careerTypes["Amplifier"];
        int[] supportElements = observationRow.ObservationCareer
            .Select((career, index) => (career, element: observationRow.ObservationElement[index]))
            .Where(value => value.career == tankType)
            .Select(value => value.element)
            .Distinct()
            .ToArray();
        int[] tankElements = observationRow.ObservationCareer
            .Select((career, index) => (career, element: observationRow.ObservationElement[index]))
            .Where(value => value.career == amplifierType)
            .Select(value => value.element)
            .Distinct()
            .ToArray();
        CharacterTable support = TableReaderV2.Parse<CharacterTable>().First(row =>
            row.Id != observerRow.Id
            && (careerNames[row.Career] is "Support" or "Amplifier")
            && supportElements.Contains(row.Element));
        CharacterTable tank = TableReaderV2.Parse<CharacterTable>().First(row =>
            row.Id != observerRow.Id
            && (careerNames[row.Career] is "Tank" or "Breaker")
            && tankElements.Contains(row.Element));

        Dictionary<int, int> supportMagic = (Dictionary<int, int>)buildMagicIds.Invoke(
            null, [new[]
            {
                unlocked,
                new CharacterData { Id = (uint)support.Id }
            }, unlocked])!;
        Dictionary<int, int> tankMagic = (Dictionary<int, int>)buildMagicIds.Invoke(
            null, [new[]
            {
                unlocked,
                new CharacterData { Id = (uint)tank.Id }
            }, unlocked])!;
        AssertEqual(true, supportMagic.Count > 0 && tankMagic.Count > 0,
            "unlocked Observer emits table-selected observation MagicIds");
        AssertEqual(true, !supportMagic.Keys.SequenceEqual(tankMagic.Keys),
            "Observer team compositions select different MagicIds");
        AssertEqual(true, supportMagic.Values.All(value => value == maxLevel), "Observer MagicIds preserve skill level");
        AssertEqual(true, tankMagic.Values.All(value => value == maxLevel), "Observer tank-form MagicIds preserve skill level");
        AssertEqual(observerRow.Career, (int)RequiredMethod(
            RequiredAscNetGameServerType("AscNet.GameServer.Handlers.FightModule"),
            "ResolveCharacterCareer", BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(int)]).Invoke(null, [observerRow.Id])!,
            "ordinary career resolver is table-backed");
        int physicalElement = TableReaderV2.Parse<CharacterElementTable>()
            .First(row => row.ElementName == "Physical").Id;
        CharacterTable[] physicalCharacters = TableReaderV2.Parse<CharacterTable>()
            .Where(row => row.Id != observerRow.Id && row.Element == physicalElement)
            .Take(2)
            .ToArray();
        AssertEqual(2, physicalCharacters.Length, "authoritative table provides multiple Physical characters");
        Dictionary<int, int> duplicatePhysicalMagic = (Dictionary<int, int>)buildMagicIds.Invoke(
            null, [new[]
            {
                unlocked,
                new CharacterData { Id = (uint)physicalCharacters[0].Id },
                new CharacterData { Id = (uint)physicalCharacters[1].Id }
            }, unlocked])!;
        AssertEqual(0, duplicatePhysicalMagic.Count,
            "Observer rejects teams containing multiple table-selected Physical characters");
    }

    private static void ValidateVersion47GeneralSkillPreFightCompatibility()
    {
        const long playerId = 48_107;
        CharacterGeneralSkillTable validGeneralSkill = TableReaderV2.Parse<CharacterGeneralSkillTable>()
            .First(row => row.FightEventId > 0 && row.FightEventId != 5000 + row.Id);
        int requiredSkillIndex = validGeneralSkill.SkillId.FindIndex(id => id > 0);
        int requiredSkillId = validGeneralSkill.SkillId[requiredSkillIndex];
        int requiredSkillLevel = validGeneralSkill.SkillLevel[requiredSkillIndex];
        CharacterSkillTable skillRow = TableReaderV2.Parse<CharacterSkillTable>()
            .First(row => row.SkillGroupId.Any(groupId =>
                TableReaderV2.Parse<CharacterSkillGroupTable>().Find(group => group.Id == groupId)?.SkillId.Contains(requiredSkillId) == true));
        CharacterGeneralSkillTable unownedGeneralSkillRow = TableReaderV2.Parse<CharacterGeneralSkillTable>()
            .First(row => row.Id != validGeneralSkill.Id
                && row.FightEventId > 0
                && row.IsSkipSkillCheck == 0
                && row.SkillId.Any(id => id > 0)
                && !row.SkillId.Contains(requiredSkillId));
        int unownedGeneralSkill = unownedGeneralSkillRow.Id;
        int validEventId = validGeneralSkill.FightEventId
            ?? throw new InvalidDataException("Selected general skill has no fight event.");
        int invalidEventId = unownedGeneralSkillRow.FightEventId
            ?? throw new InvalidDataException("Unowned general skill has no fight event.");
        MethodInfo mapGeneralSkillEvent = RequiredMethod(
            RequiredAscNetGameServerType("AscNet.GameServer.Handlers.FightModule"),
            "GeneralSkillFightEventId", BindingFlags.Static | BindingFlags.NonPublic, [typeof(int)]);
        foreach (CharacterGeneralSkillTable generalSkill in TableReaderV2.Parse<CharacterGeneralSkillTable>()
                     .Where(row => row.FightEventId > 0 && row.FightEventId != 5000 + row.Id))
        {
            AssertEqual(generalSkill.FightEventId!.Value,
                (int)mapGeneralSkillEvent.Invoke(null, [generalSkill.Id])!,
                $"GeneralSkill {generalSkill.Id} uses its authoritative fight event");
        }
        RobotTable robot = TableReaderV2.Parse<RobotTable>().First();
        Character roster = CreateDrawCompatibilityCharacter(playerId);
        roster.Characters =
        [
            new CharacterData
            {
                Id = (uint)skillRow.CharacterId,
                Level = 80,
                SkillList = [new CharacterSkill { Id = (uint)requiredSkillId, Level = requiredSkillLevel }]
            }
        ];
        using LoopbackSessionHarness harness = new(
            roster, CreateDrawCompatibilityPlayer(playerId),
            CreateDrawCompatibilityInventory(playerId, []), "v47-general-skill-prefight");

        PreFightRequest request = new()
        {
            PreFightData = new()
            {
                StageId = 0,
                CardIds = [(uint)skillRow.CharacterId],
                RobotIds = [robot.Id],
                GeneralSkill = validGeneralSkill.Id
            }
        };
        InvokeRegisteredRequestHandler(nameof(PreFightRequest), harness.Session, 12_701, request);
        PreFightResponse validResponse = ReadResponsePayload<PreFightResponse>(
            harness, 12_701, nameof(PreFightResponse), "GeneralSkill valid PreFight");
        AssertEqual(0, validResponse.Code, "GeneralSkill valid PreFight code");
        AssertEqual(true, validResponse.FightData.EventIds.Select(Convert.ToInt32).Contains(validEventId),
            "GeneralSkill valid PreFight appends event id");
        PreFightResponse.PreFightResponseFightData.PreFightResponseFightDataRoleData role =
            validResponse.FightData.RoleData.Single(row => row.Id == (uint)playerId);
        AssertEqual(2, role.NpcData.Count, "GeneralSkill robot PreFight constructs both deployments");
        System.Collections.IDictionary robotNpc = role.NpcData.Values
            .Select(value => RequiredDynamicMap((object?)value, "GeneralSkill robot NPC"))
            .Single(npc => RequiredDynamicInteger(npc, "RobotId", "GeneralSkill robot NPC") != 0);
        AssertEqual(robot.Id, RequiredDynamicInteger(robotNpc, "RobotId", "GeneralSkill robot NPC"),
            "GeneralSkill robot PreFight preserves RobotId");

        request.PreFightData.GeneralSkill = unownedGeneralSkill;
        InvokeRegisteredRequestHandler(nameof(PreFightRequest), harness.Session, 12_702, request);
        PreFightResponse invalidResponse = ReadResponsePayload<PreFightResponse>(
            harness, 12_702, nameof(PreFightResponse), "GeneralSkill invalid PreFight");
        AssertEqual(0, invalidResponse.Code, "GeneralSkill invalid selection still authorizes PreFight");
        AssertEqual(false, invalidResponse.FightData.EventIds.Select(Convert.ToInt32).Contains(invalidEventId),
            "GeneralSkill unowned selection does not append event id");
    }

    private static void ValidateVersion47HeadEquipValidation()
    {
        const long playerId = 48_101;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Player player = CreateDrawCompatibilityPlayer(playerId);
        player.HeadPortraits.AddRange(
        [
            new HeadPortraitList { Id = 9090002, LeftCount = 1, BeginTime = now },    // owned portrait (forever)
            new HeadPortraitList { Id = 9090020, LeftCount = 1, BeginTime = now },    // owned frame (forever)
            new HeadPortraitList { Id = 9100002, LeftCount = 1, BeginTime = now }     // owned valid timed frame
        ]);
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out RecordingMongoCollectionProxy<Player> playerSaves, out _, out _);
        using LoopbackSessionHarness harness = new(
            CreateDrawCompatibilityCharacter(playerId), player,
            CreateDrawCompatibilityInventory(playerId, []), "v47-head-equip");
        long nowUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Valid owned portrait equip persists once.
        const int portraitPacketId = 12_101;
        InvokeRegisteredRequestHandler(nameof(SetHeadPortraitRequest), harness.Session, portraitPacketId,
            new SetHeadPortraitRequest { Id = 9090002 });
        ReadResponsePayload<SetHeadPortraitResponse>(harness, portraitPacketId, nameof(SetHeadPortraitResponse),
            "SetHeadPortrait valid owned");
        AssertEqual(9090002L, player.PlayerData.CurrHeadPortraitId, "SetHeadPortrait persisted CurrHeadPortraitId");
        AssertEqual(1, playerSaves.ReplaceOneCalls, "SetHeadPortrait valid saves player once");

        // Idempotent re-equip of the same portrait does not save.
        InvokeRegisteredRequestHandler(nameof(SetHeadPortraitRequest), harness.Session, portraitPacketId + 1,
            new SetHeadPortraitRequest { Id = 9090002 });
        ReadResponsePayload<SetHeadPortraitResponse>(harness, portraitPacketId + 1, nameof(SetHeadPortraitResponse),
            "SetHeadPortrait idempotent");
        AssertEqual(1, playerSaves.ReplaceOneCalls, "SetHeadPortrait idempotent does not save");

        // Unowned table portrait is rejected.
        int rejectedPortraitPacketId = 12_111;
        InvokeRegisteredRequestHandler(nameof(SetHeadPortraitRequest), harness.Session, rejectedPortraitPacketId,
            new SetHeadPortraitRequest { Id = 9000001 });
        SetHeadPortraitResponse unownedPortrait = ReadResponsePayload<SetHeadPortraitResponse>(harness,
            rejectedPortraitPacketId, nameof(SetHeadPortraitResponse), "SetHeadPortrait unowned");
        AssertEqual(20012001, unownedPortrait.Code, "SetHeadPortrait unowned rejection code");
        AssertEqual(9090002L, player.PlayerData.CurrHeadPortraitId, "SetHeadPortrait unowned does not change equip");

        // Nonexistent ID is rejected.
        InvokeRegisteredRequestHandler(nameof(SetHeadPortraitRequest), harness.Session, rejectedPortraitPacketId + 1,
            new SetHeadPortraitRequest { Id = 9_999_999 });
        SetHeadPortraitResponse nonexistentPortrait = ReadResponsePayload<SetHeadPortraitResponse>(harness,
            rejectedPortraitPacketId + 1, nameof(SetHeadPortraitResponse), "SetHeadPortrait nonexistent");
        AssertEqual(20012001, nonexistentPortrait.Code, "SetHeadPortrait nonexistent rejection code");

        // Wrong head type (a frame id into the portrait slot) is rejected.
        InvokeRegisteredRequestHandler(nameof(SetHeadPortraitRequest), harness.Session, rejectedPortraitPacketId + 2,
            new SetHeadPortraitRequest { Id = 9090020 });
        SetHeadPortraitResponse wrongTypePortrait = ReadResponsePayload<SetHeadPortraitResponse>(harness,
            rejectedPortraitPacketId + 2, nameof(SetHeadPortraitResponse), "SetHeadPortrait wrong type");
        AssertEqual(20012001, wrongTypePortrait.Code, "SetHeadPortrait wrong type rejection code");

        // Valid timed frame equip succeeds.
        const int framePacketId = 12_121;
        InvokeRegisteredRequestHandler(nameof(SetHeadFrameRequest), harness.Session, framePacketId,
            new SetHeadFrameRequest { Id = 9100002 });
        ReadResponsePayload<SetHeadFrameResponse>(harness, framePacketId, nameof(SetHeadFrameResponse),
            "SetHeadFrame valid timed frame");
        AssertEqual(9100002L, player.PlayerData.CurrHeadFrameId, "SetHeadFrame persisted CurrHeadFrameId");
        AssertEqual(2, playerSaves.ReplaceOneCalls, "SetHeadFrame valid saves player once");

        // An expired owned timed frame cannot be equipped.
        player.HeadPortraits.Add(new HeadPortraitList { Id = 9100001, LeftCount = 1, BeginTime = nowUtc - 1_728_001 });
        InvokeRegisteredRequestHandler(nameof(SetHeadFrameRequest), harness.Session, framePacketId + 1,
            new SetHeadFrameRequest { Id = 9100001 });
        SetHeadFrameResponse expiredFrame = ReadResponsePayload<SetHeadFrameResponse>(harness, framePacketId + 1,
            nameof(SetHeadFrameResponse), "SetHeadFrame expired");
        AssertEqual(20012001, expiredFrame.Code, "SetHeadFrame expired rejection code");
        AssertEqual(9100002L, player.PlayerData.CurrHeadFrameId, "SetHeadFrame expired keeps prior equip");
        AssertEqual(2, playerSaves.ReplaceOneCalls, "SetHeadFrame expired does not save");
        AssertNoAvailablePacket(harness, "SetHeadFrame expired");
    }

    private static void ValidateVersion47HeadTimeoutReconciliation()
    {
        MethodInfo reconcile = RequiredMethod(
            RequiredAscNetGameServerType("AscNet.GameServer.Handlers.PlayerModule"),
            "ReconcileHeadTimeouts",
            BindingFlags.Static | BindingFlags.Public,
            [typeof(Session), typeof(DateTimeOffset)]);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // State 1 (valid timing): a still-valid timed frame yields no timeout and no repair.
        {
            const long validPlayerId = 48_102;
            Player player = CreateDrawCompatibilityPlayer(validPlayerId);
            player.HeadPortraits.AddRange(
            [
                new HeadPortraitList { Id = 9100002, LeftCount = 1, BeginTime = now },   // valid: 0 < 2592000
                new HeadPortraitList { Id = 9090020, LeftCount = 1, BeginTime = now }
            ]);
            player.PlayerData.CurrHeadFrameId = 9090020;
            using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
                out RecordingMongoCollectionProxy<Player> validSaves, out _, out _);
            using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(validPlayerId), player,
                CreateDrawCompatibilityInventory(validPlayerId, []), "v47-head-reconcile-valid");
            object? result = reconcile.Invoke(null, [harness.Session, DateTimeOffset.FromUnixTimeSeconds(now)]);
            AssertEqual(true, result is null, "ReconcileHeadTimeouts valid timing returns null");
            AssertEqual(0, validSaves.ReplaceOneCalls, "ReconcileHeadTimeouts valid timing does not save");
        }

        // State 2 (expired timing): the expired owned frame is reported in TimeoutIds, the equipped
        // frame is repaired to a valid owned default, and the player is persisted.
        {
            const long expiredPlayerId = 48_103;
            Player player = CreateDrawCompatibilityPlayer(expiredPlayerId);
            player.HeadPortraits.AddRange(
            [
                new HeadPortraitList { Id = 9100001, LeftCount = 1, BeginTime = now - 1_728_001 }, // expired: 1728001 !< 1728000
                new HeadPortraitList { Id = 9090020, LeftCount = 1, BeginTime = now }
            ]);
            player.PlayerData.CurrHeadFrameId = 9100001;
            using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
                out RecordingMongoCollectionProxy<Player> expiredSaves, out _, out _);
            using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(expiredPlayerId), player,
                CreateDrawCompatibilityInventory(expiredPlayerId, []), "v47-head-reconcile-expired");
            NotifyHeadTimeout? timeout = (NotifyHeadTimeout?)reconcile.Invoke(null,
                [harness.Session, DateTimeOffset.FromUnixTimeSeconds(now)]);
            AssertEqual(true, timeout is not null, "ReconcileHeadTimeouts expired timing returns NotifyHeadTimeout");
            AssertEqual(true, player.HeadPortraits.Any(head => head.Id == 9100001),
                "ReconcileHeadTimeouts keeps expired owned entry");
            AssertIntegerList([9100001], timeout!.TimeoutIds.ToArray(), "ReconcileHeadTimeouts exact TimeoutIds");
            AssertEqual(9090020L, timeout.CurrHeadFrameId, "ReconcileHeadTimeouts repaired CurrHeadFrameId");
            AssertEqual(9090020L, player.PlayerData.CurrHeadFrameId, "ReconcileHeadTimeouts repaired persisted CurrHeadFrameId");
            AssertEqual(1, expiredSaves.ReplaceOneCalls, "ReconcileHeadTimeouts expired persists player once");
        }
    }

    private static void ValidateVersion47FashionUseCompatibility()
    {
        const long playerId = 48_104;
        const uint defaultFashionId = 2022001;   // Lucia default
        const uint selectableFashionId = 6210101; // Lucia coating

        AscNet.Common.Database.Character character = CreateDrawCompatibilityCharacter(playerId);
        CharacterData lucia = new()
        {
            Id = 1021001,
            Level = 80,
            FashionId = defaultFashionId,
            CharacterHeadInfo = new CharacterData.CharacterHead { HeadFashionId = defaultFashionId, HeadFashionType = 0 }
        };
        character.Characters = [lucia];
        character.Fashions =
        [
            new FashionList { Id = defaultFashionId, IsLock = false },
            new FashionList { Id = selectableFashionId, IsLock = false }
        ];
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out _, out RecordingMongoCollectionProxy<AscNet.Common.Database.Character> characterSaves, out _);
        using LoopbackSessionHarness harness = new(character, CreateDrawCompatibilityPlayer(playerId),
            CreateDrawCompatibilityInventory(playerId, []), "v47-fashion-use");

        // Valid owned coating equip: push NotifyCharacterDataList, then response; persists.
        const int packetId = 12_201;
        InvokeRegisteredRequestHandler(nameof(FashionUseRequest), harness.Session, packetId,
            new FashionUseRequest { FashionId = selectableFashionId });
        NotifyCharacterDataList push = ReadPushPayload<NotifyCharacterDataList>(harness, nameof(NotifyCharacterDataList),
            "FashionUse NotifyCharacterDataList");
        CharacterData pushed = push.CharacterDataList.Single();
        AssertEqual(1021001U, pushed.Id, "FashionUse pushed character id");
        AssertEqual(selectableFashionId, pushed.FashionId, "FashionUse pushed FashionId");
        FashionUseResponse response = ReadResponsePayload<FashionUseResponse>(harness, packetId, nameof(FashionUseResponse),
            "FashionUse response");
        AssertEqual(0, response.Code, "FashionUse response Code");
        AssertEqual(selectableFashionId, lucia.FashionId, "FashionUse persisted FashionId");
        AssertEqual(1, characterSaves.ReplaceOneCalls, "FashionUse saves character once");

        // Idempotent re-equip of the same coating: still push+response, but no save.
        InvokeRegisteredRequestHandler(nameof(FashionUseRequest), harness.Session, packetId + 1,
            new FashionUseRequest { FashionId = selectableFashionId });
        ReadPushPayload<NotifyCharacterDataList>(harness, nameof(NotifyCharacterDataList), "FashionUse idempotent push");
        ReadResponsePayload<FashionUseResponse>(harness, packetId + 1, nameof(FashionUseResponse), "FashionUse idempotent response");
        AssertEqual(1, characterSaves.ReplaceOneCalls, "FashionUse idempotent does not save");

        // Unknown coating is rejected.
        InvokeRegisteredRequestHandler(nameof(FashionUseRequest), harness.Session, packetId + 2,
            new FashionUseRequest { FashionId = 9_999_999 });
        FashionUseResponse unknown = ReadResponsePayload<FashionUseResponse>(harness, packetId + 2, nameof(FashionUseResponse),
            "FashionUse unknown");
        AssertEqual(20012001, unknown.Code, "FashionUse unknown rejection code");
        AssertEqual(selectableFashionId, lucia.FashionId, "FashionUse unknown keeps prior equip");
        AssertNoAvailablePacket(harness, "FashionUse unknown");

        // A table-valid but unowned coating for an owned character is rejected.
        const uint unownedLuciaFashion = 6210102;
        InvokeRegisteredRequestHandler(nameof(FashionUseRequest), harness.Session, packetId + 3,
            new FashionUseRequest { FashionId = unownedLuciaFashion });
        FashionUseResponse unowned = ReadResponsePayload<FashionUseResponse>(harness, packetId + 3, nameof(FashionUseResponse),
            "FashionUse unowned");
        AssertEqual(20012001, unowned.Code, "FashionUse unowned rejection code");
        AssertNoAvailablePacket(harness, "FashionUse unowned");

        // Relogin reflects the persisted equipped coating.
        AssertLoginFashion(harness, selectableFashionId);
    }

    private static void ValidateVersion47CharacterHeadSelectionCompatibility()
    {
        const long playerId = 48_105;
        CharacterTable luciaRow = TableReaderV2.Parse<CharacterTable>()
            .Single(row => row.Id == 1021001);
        uint defaultFashionId = (uint)luciaRow.DefaultNpcFashtionId;
        uint selectableFashionId = (uint)TableReaderV2.Parse<FashionTable>()
            .First(row => row.CharacterId == luciaRow.Id
                && row.Id != luciaRow.DefaultNpcFashtionId).Id;
        uint foreignFashionId = (uint)TableReaderV2.Parse<FashionTable>()
            .First(row => row.CharacterId != 1021001 && row.Id > 0).Id;

        AscNet.Common.Database.Character character = CreateDrawCompatibilityCharacter(playerId);
        CharacterData lucia = new()
        {
            Id = 1021001,
            Level = 80,
            LiberateLv = 4,                        // GrowUpLevel.Higher
            FashionId = defaultFashionId,
            CharacterHeadInfo = new CharacterData.CharacterHead
            {
                HeadFashionId = selectableFashionId,
                HeadFashionType = 2
            }
        };
        character.Characters = [lucia];
        character.Fashions =
        [
            new FashionList { Id = defaultFashionId, IsLock = false },
            new FashionList { Id = selectableFashionId, IsLock = false }
        ];
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out _, out RecordingMongoCollectionProxy<AscNet.Common.Database.Character> characterSaves, out _);
        using LoopbackSessionHarness harness = new(character, CreateDrawCompatibilityPlayer(playerId),
            CreateDrawCompatibilityInventory(playerId, []), "v47-character-head");

        int packetId = 12_301;
        // Type 0 (default) succeeds.
        AssertHeadSelectionSucceeds(harness, characterSaves, packetId++, defaultFashionId, 0, "default");
        // Type 1 (liberation) with Higher liberation succeeds.
        AssertHeadSelectionSucceeds(harness, characterSaves, packetId++, defaultFashionId, 1, "liberation");
        // Type 2 (owned same-character coating) succeeds.
        AssertHeadSelectionSucceeds(harness, characterSaves, packetId++, selectableFashionId, 2, "fashion");

        // Liberation head without Higher liberation is rejected.
        lucia.LiberateLv = 3;
        AssertHeadSelectionRejected(harness, packetId++, defaultFashionId, 1, "liberation without Higher");
        lucia.LiberateLv = 4;

        // Fashion head that is unowned is rejected.
        uint unownedFashionId = (uint)TableReaderV2.Parse<FashionTable>()
            .First(row => row.CharacterId == luciaRow.Id
                && harness.Session.character.Fashions.All(owned => owned.Id != row.Id)).Id;
        AssertHeadSelectionRejected(harness, packetId++, unownedFashionId, 2,
            "unowned fashion head");

        // Fashion head for a different character is rejected (same-character table relation).
        AssertHeadSelectionRejected(harness, packetId++, foreignFashionId, 2, "foreign-character fashion head");

        // Unknown head type is rejected.
        AssertHeadSelectionRejected(harness, packetId++, selectableFashionId, 7, "unknown head type");

        // Relogin reflects the persisted head selection (type 2 fashion).
        AssertLoginHead(harness, selectableFashionId, 2);
        AssertEqual(3, characterSaves.ReplaceOneCalls, "CharacterSetHeadInfo successful selections save once each");
    }

    private static void AssertHeadSelectionSucceeds(
        LoopbackSessionHarness harness,
        RecordingMongoCollectionProxy<AscNet.Common.Database.Character> saves,
        int packetId,
        uint headFashionId,
        int headFashionType,
        string name)
    {
        int savesBefore = saves.ReplaceOneCalls;
        InvokeRegisteredRequestHandler(nameof(CharacterSetHeadInfoRequest), harness.Session, packetId,
            new CharacterSetHeadInfoRequest
            {
                TemplateId = 1021001,
                CharacterHeadInfo = new CharacterData.CharacterHead
                {
                    HeadFashionId = headFashionId,
                    HeadFashionType = headFashionType
                }
            });
        Packet first = harness.ReadPacket($"CharacterSetHeadInfo {name} first packet");
        if (first.Type != Packet.ContentType.Push)
        {
            Packet.Response rejected = MessagePackSerializer.Deserialize<Packet.Response>(first.Content);
            CharacterSetHeadInfoResponse payload =
                MessagePackSerializer.Deserialize<CharacterSetHeadInfoResponse>(rejected.Content);
            throw new InvalidDataException(
                $"CharacterSetHeadInfo {name} returned Code={payload.Code} before its push.");
        }
        Packet.Push pushPacket = MessagePackSerializer.Deserialize<Packet.Push>(first.Content);
        AssertEqual(nameof(NotifyCharacterDataList), pushPacket.Name,
            $"CharacterSetHeadInfo {name} push name");
        NotifyCharacterDataList push =
            MessagePackSerializer.Deserialize<NotifyCharacterDataList>(pushPacket.Content);
        CharacterData pushed = push.CharacterDataList.Single();
        AssertEqual((uint)headFashionId, pushed.CharacterHeadInfo.HeadFashionId, $"CharacterSetHeadInfo {name} pushed head id");
        AssertEqual(headFashionType, pushed.CharacterHeadInfo.HeadFashionType, $"CharacterSetHeadInfo {name} pushed head type");
        CharacterSetHeadInfoResponse response = ReadResponsePayload<CharacterSetHeadInfoResponse>(harness, packetId,
            nameof(CharacterSetHeadInfoResponse), $"CharacterSetHeadInfo {name} response");
        AssertEqual(0, response.Code, $"CharacterSetHeadInfo {name} response code");
        AssertEqual(savesBefore + 1, saves.ReplaceOneCalls, $"CharacterSetHeadInfo {name} saves once");
    }

    private static void AssertHeadSelectionRejected(LoopbackSessionHarness harness, int packetId, uint headFashionId, int headFashionType, string name)
    {
        InvokeRegisteredRequestHandler(nameof(CharacterSetHeadInfoRequest), harness.Session, packetId,
            new CharacterSetHeadInfoRequest
            {
                TemplateId = 1021001,
                CharacterHeadInfo = new CharacterData.CharacterHead
                {
                    HeadFashionId = headFashionId,
                    HeadFashionType = headFashionType
                }
            });
        CharacterSetHeadInfoResponse response = ReadResponsePayload<CharacterSetHeadInfoResponse>(harness, packetId,
            nameof(CharacterSetHeadInfoResponse), $"CharacterSetHeadInfo {name} rejection response");
        AssertEqual(20012001, response.Code, $"CharacterSetHeadInfo {name} rejection code");
        AssertNoAvailablePacket(harness, $"CharacterSetHeadInfo {name} rejection");
    }

    private static void ValidateVersion47EnhanceSkillNormalization()
    {
        // One active skill per enhance group; foreign skills and duplicate alternates are pruned,
        // out-of-range levels are clamped. Uses Lucia: Crimson Weave (existing groups).
        const int characterId = 1021005;
        AscNet.Common.Database.Character roster = CreateTestCharacterRoster(characterId, level: 80);
        CharacterData character = RequiredCharacterData(roster, characterId);
        character.EnhanceSkillList =
        [
            new CharacterSkill { Id = 102531, Level = 5 },  // group 1025280 active
            new CharacterSkill { Id = 102528, Level = 3 },  // duplicate alternate in group 1025280
            new CharacterSkill { Id = 102529, Level = 99 }, // group 1025290, level out of range
            new CharacterSkill { Id = 999_999, Level = 2 }  // foreign
        ];

        bool changed = roster.NormalizeCharactersForCurrentTables([]);
        AssertEqual(true, changed, "EnhanceSkill normalization reports change");

        // Exactly one active skill per owned group; no foreign skills; level 99 clamped to the group max (18).
        List<CharacterSkill> enhanceSkills = character.EnhanceSkillList;
        AssertEqual(2, enhanceSkills.Count, "EnhanceSkill normalization keeps one active per owned group");
        AssertEqual(true, enhanceSkills.All(skill => skill.Id is 102531 or 102528 or 102529 or 102530),
            "EnhanceSkill normalization retains only owned group skills");
        AssertEqual(false, enhanceSkills.Any(skill => skill.Id == 999_999), "EnhanceSkill normalization drops foreign skill");
        AssertEqual(true, enhanceSkills.Any(skill => skill.Id == 102531), "EnhanceSkill normalization keeps group 1025280 active");
        AssertEqual(false, enhanceSkills.Any(skill => skill.Id == 102528), "EnhanceSkill normalization dedupes group 1025280 alternate");
        CharacterSkill normalizedGroup1025290 = enhanceSkills.Single(skill => skill.Id == 102529);
        AssertEqual(18, normalizedGroup1025290.Level, "EnhanceSkill normalization clamps out-of-range level to max");
    }

    private static void ValidateVersion47KareninaTables()
    {
        // Data gate: 4.7 Karenina: Effulgence rows and Pyroath leap groups must be staged by the
        // data cutover before Karenina runtime compatibility can resolve from tables.
        AssertEqual(true, TableReaderV2.Parse<CharacterTable>().Any(row => row.Id == 1071005),
            "4.7 Karenina: Effulgence present in Character.tsv");
        AssertEqual(true, TableReaderV2.Parse<CharacterSkillTable>().Any(row => row.CharacterId == 1071005),
            "4.7 Karenina: Effulgence present in CharacterSkill.tsv");
        EnhanceSkillTable pyroathEnhance = TableReaderV2.Parse<EnhanceSkillTable>()
            .SingleOrDefault(row => row.CharacterId == 1021006)
            ?? throw new InvalidDataException("4.7 Pyroath EnhanceSkill.tsv missing character 1021006.");
        AssertIntegerList([1026280, 1026290, 1026300],
            pyroathEnhance.SkillGroupId.Where(id => id > 0).Select(id => (long)id).ToArray(),
            "4.7 Pyroath EnhanceSkill.tsv SkillGroupId");
        foreach (int groupId in new[] { 1026280, 1026290, 1026300 })
        {
            AssertEqual(true, TableReaderV2.Parse<EnhanceSkillGroupTable>().Any(row => row.Id == groupId),
                $"4.7 Pyroath EnhanceSkillGroup.tsv group {groupId}");
        }
    }

    private static void ValidateVersion47PyroathEnhanceSkillCompatibility()
    {
        const int characterId = 1021006;
        const int groupId = 1026280;
        const int defaultSkillId = 102628;

        AscNet.Common.Database.Character highRoster = CreateTestCharacterRoster(characterId, level: 80);
        highRoster.Uid = 48_106;
        Dictionary<int, int> unlockCosts = CostOfLevel(defaultSkillId, 0, "Pyroath unlock level 0");
        Dictionary<int, int> upgradeCosts = CostOfLevel(defaultSkillId, 1, "Pyroath upgrade level 1");
        Dictionary<int, long> unlockInventory = InitialCounts(unlockCosts, surplus: 10);
        Dictionary<int, long> upgradeInventory = InitialCounts(upgradeCosts, surplus: 10);

        // Unlock succeeds: default active skill at level 1, costs consumed, Item->Character->response.
        using (MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out _, out RecordingMongoCollectionProxy<AscNet.Common.Database.Character> unlockSaves,
            out RecordingMongoCollectionProxy<Inventory> unlockInventorySaves))
        using (LoopbackSessionHarness harness = new(highRoster, CreateDrawCompatibilityPlayer(highRoster.Uid),
            CreateInventory(highRoster.Uid, unlockInventory), "v47-pyroath-unlock"))
        {
            const int unlockPacketId = 12_401;
            InvokeRegisteredRequestHandler(nameof(CharacterUnlockEnhanceSkillRequest), harness.Session, unlockPacketId,
                new CharacterUnlockEnhanceSkillRequest { SkillGroupId = groupId });
            NotifyItemDataList itemPush = ReadPushPayload<NotifyItemDataList>(harness, nameof(NotifyItemDataList),
                "Pyroath unlock NotifyItemDataList");
            AssertCosts(itemPush, harness.Session.inventory, unlockInventory, unlockCosts, "Pyroath unlock");
            NotifyCharacterDataList characterPush = ReadPushPayload<NotifyCharacterDataList>(harness,
                nameof(NotifyCharacterDataList), "Pyroath unlock NotifyCharacterDataList");
            CharacterData pushed = characterPush.CharacterDataList.Single();
            CharacterSkill unlocked = pushed.EnhanceSkillList.Single(skill => skill.Id == defaultSkillId);
            AssertEqual(1, unlocked.Level, "Pyroath unlock default skill level");
            CharacterUnlockEnhanceSkillResponse unlockResponse = ReadResponsePayload<CharacterUnlockEnhanceSkillResponse>(harness,
                unlockPacketId, nameof(CharacterUnlockEnhanceSkillResponse), "Pyroath unlock response");
            AssertEqual(0, unlockResponse.Code, "Pyroath unlock response Code");
            AssertEqual(1, unlockSaves.ReplaceOneCalls, "Pyroath unlock saves character once");
            AssertEqual(1, unlockInventorySaves.ReplaceOneCalls, "Pyroath unlock saves inventory once");
        }

        // Uniframe unlock gates use Commandant level (Condition 10101), not Construct level.
        const int uniframeCharacterId = 1511003;
        const int uniframeGroupId = 1513280;
        const int uniframeSkillId = 151328;
        Dictionary<int, int> uniframeCosts = CostOfLevel(uniframeSkillId, 0, "Uniframe unlock level 0");
        AscNet.Common.Database.Character uniframeRoster = CreateTestCharacterRoster(uniframeCharacterId, level: 80);
        uniframeRoster.Uid = 48_109;
        Player uniframePlayer = CreateDrawCompatibilityPlayer(uniframeRoster.Uid);
        uniframePlayer.PlayerData.Level = 52;
        using (MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out _, out RecordingMongoCollectionProxy<AscNet.Common.Database.Character> uniframeSaves, out _))
        using (LoopbackSessionHarness harness = new(uniframeRoster, uniframePlayer,
            CreateInventory(uniframeRoster.Uid, InitialCounts(uniframeCosts, surplus: 10)), "v47-uniframe-unlock"))
        {
            InvokeRegisteredRequestHandler(nameof(CharacterUnlockEnhanceSkillRequest), harness.Session, 12_406,
                new CharacterUnlockEnhanceSkillRequest { SkillGroupId = uniframeGroupId });
            _ = ReadPushPayload<NotifyItemDataList>(harness, nameof(NotifyItemDataList), "Uniframe unlock items");
            CharacterData pushed = ReadPushPayload<NotifyCharacterDataList>(harness,
                nameof(NotifyCharacterDataList), "Uniframe unlock character").CharacterDataList.Single();
            AssertEqual((uint)uniframeSkillId, pushed.EnhanceSkillList.Single().Id, "Uniframe unlock skill");
            AssertEqual(0, ReadResponsePayload<CharacterUnlockEnhanceSkillResponse>(harness, 12_406,
                nameof(CharacterUnlockEnhanceSkillResponse), "Uniframe unlock response").Code, "Uniframe unlock response Code");
            AssertEqual(1, uniframeSaves.ReplaceOneCalls, "Uniframe unlock saves character once");
        }

        AscNet.Common.Database.Character lowUniframeRoster = CreateTestCharacterRoster(uniframeCharacterId, level: 80);
        lowUniframeRoster.Uid = 48_110;
        Player lowUniframePlayer = CreateDrawCompatibilityPlayer(lowUniframeRoster.Uid);
        lowUniframePlayer.PlayerData.Level = 51;
        using (MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out _, out RecordingMongoCollectionProxy<AscNet.Common.Database.Character> lowUniframeSaves, out _))
        using (LoopbackSessionHarness harness = new(lowUniframeRoster, lowUniframePlayer,
            CreateInventory(lowUniframeRoster.Uid, InitialCounts(uniframeCosts, surplus: 10)), "v47-uniframe-unlock-gate"))
        {
            InvokeRegisteredRequestHandler(nameof(CharacterUnlockEnhanceSkillRequest), harness.Session, 12_407,
                new CharacterUnlockEnhanceSkillRequest { SkillGroupId = uniframeGroupId });
            AssertEqual(20009021, ReadResponsePayload<CharacterUnlockEnhanceSkillResponse>(harness, 12_407,
                nameof(CharacterUnlockEnhanceSkillResponse), "Uniframe level gate response").Code, "Uniframe level gate Code");
            AssertEqual(0, lowUniframeSaves.ReplaceOneCalls, "Uniframe level gate does not save");
            AssertNoAvailablePacket(harness, "Uniframe level gate");
        }

        const int selenaCharacterId = 1531003;
        const int selenaSpeedAttackGroupId = 1533300;
        const int selenaSpeedAttackSkillId = 153330;
        AscNet.Common.Database.Character selenaRoster = CreateTestCharacterRoster(selenaCharacterId, level: 80);
        selenaRoster.Uid = 48_111;
        CharacterData selenaCharacter = RequiredCharacterData(selenaRoster, selenaCharacterId);
        selenaCharacter.EnhanceSkillList.Add(new CharacterSkill { Id = 153328, Level = 18 });
        Dictionary<int, int> selenaCosts = CostOfLevel(selenaSpeedAttackSkillId, 0, "Selena Enhanced Speed Attack");
        Dictionary<int, int> selenaFinalCosts = CostOfLevel(153332, 0, "Selena Enhanced Finishing Move");
        Dictionary<int, long> selenaInventory = InitialCounts(selenaCosts, surplus: 10);
        foreach ((int itemId, int count) in selenaFinalCosts)
            selenaInventory[itemId] = selenaInventory.GetValueOrDefault(itemId) + count;
        Player selenaPlayer = CreateDrawCompatibilityPlayer(selenaRoster.Uid);
        selenaPlayer.PlayerData.Level = 52;
        using (MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out _, out RecordingMongoCollectionProxy<AscNet.Common.Database.Character> selenaSaves, out _))
        using (LoopbackSessionHarness harness = new(selenaRoster, selenaPlayer,
            CreateInventory(selenaRoster.Uid, selenaInventory), "v47-selena-speed-attack-unlock"))
        {
            InvokeRegisteredRequestHandler(nameof(CharacterUnlockEnhanceSkillRequest), harness.Session, 12_408,
                new CharacterUnlockEnhanceSkillRequest { SkillGroupId = selenaSpeedAttackGroupId });
            _ = ReadPushPayload<NotifyItemDataList>(harness, nameof(NotifyItemDataList), "Selena skill unlock items");
            CharacterData pushed = ReadPushPayload<NotifyCharacterDataList>(harness,
                nameof(NotifyCharacterDataList), "Selena skill unlock character").CharacterDataList.Single();
            AssertEqual((uint)selenaSpeedAttackSkillId,
                pushed.EnhanceSkillList.Single(skill => skill.Id == selenaSpeedAttackSkillId).Id,
                "Selena Enhanced Speed Attack unlocked");
            AssertEqual(0, ReadResponsePayload<CharacterUnlockEnhanceSkillResponse>(harness, 12_408,
                nameof(CharacterUnlockEnhanceSkillResponse), "Selena skill unlock response").Code,
                "Selena skill unlock response Code");
            AssertEqual(1, selenaSaves.ReplaceOneCalls, "Selena skill unlock saves character once");

            AssertEqual(false, Character.MeetsCharacterSkillCondition(selenaCharacter, [7326], [], 52),
                "Selena final skill rejects one missing prerequisite");
            selenaCharacter.EnhanceSkillList.Add(new CharacterSkill { Id = 153329, Level = 18 });
            AssertEqual(true, Character.MeetsCharacterSkillCondition(selenaCharacter, [7326], [], 52),
                "Selena final skill accepts both prerequisites");
            InvokeRegisteredRequestHandler(nameof(CharacterUnlockEnhanceSkillRequest), harness.Session, 12_409,
                new CharacterUnlockEnhanceSkillRequest { SkillGroupId = 1533320 });
            _ = ReadPushPayload<NotifyItemDataList>(harness, nameof(NotifyItemDataList), "Selena final skill items");
            CharacterData finalPush = ReadPushPayload<NotifyCharacterDataList>(harness,
                nameof(NotifyCharacterDataList), "Selena final skill character").CharacterDataList.Single();
            AssertEqual((uint)153332, finalPush.EnhanceSkillList.Single(skill => skill.Id == 153332).Id,
                "Selena Enhanced Finishing Move unlocked");
            AssertEqual(0, ReadResponsePayload<CharacterUnlockEnhanceSkillResponse>(harness, 12_409,
                nameof(CharacterUnlockEnhanceSkillResponse), "Selena final skill response").Code,
                "Selena final skill response Code");
            AssertEqual(2, selenaSaves.ReplaceOneCalls, "Selena skill unlocks persist");
        }

        // Upgrade the just-unlocked skill.
        AscNet.Common.Database.Character upgradeRoster = CreateTestCharacterRoster(characterId, level: 80);
        upgradeRoster.Uid = 48_107;
        CharacterData upgradeCharacter = RequiredCharacterData(upgradeRoster, characterId);
        upgradeCharacter.EnhanceSkillList.Add(new CharacterSkill { Id = (uint)defaultSkillId, Level = 1 });
        using (MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out _, out RecordingMongoCollectionProxy<AscNet.Common.Database.Character> upgradeSaves, out _))
        using (LoopbackSessionHarness harness = new(upgradeRoster, CreateDrawCompatibilityPlayer(upgradeRoster.Uid),
            CreateInventory(upgradeRoster.Uid, upgradeInventory), "v47-pyroath-upgrade"))
        {
            const int upgradePacketId = 12_402;
            InvokeRegisteredRequestHandler(nameof(CharacterUpgradeEnhanceSkillRequest), harness.Session, upgradePacketId,
                new CharacterUpgradeEnhanceSkillRequest { SkillGroupId = groupId, Count = 1 });
            NotifyItemDataList itemPush = ReadPushPayload<NotifyItemDataList>(harness, nameof(NotifyItemDataList),
                "Pyroath upgrade NotifyItemDataList");
            AssertCosts(itemPush, harness.Session.inventory, upgradeInventory, upgradeCosts, "Pyroath upgrade");
            NotifyCharacterDataList characterPush = ReadPushPayload<NotifyCharacterDataList>(harness,
                nameof(NotifyCharacterDataList), "Pyroath upgrade NotifyCharacterDataList");
            CharacterSkill upgraded = characterPush.CharacterDataList.Single()
                .EnhanceSkillList.Single(skill => skill.Id == defaultSkillId);
            AssertEqual(2, upgraded.Level, "Pyroath upgrade level");
            CharacterUpgradeEnhanceSkillResponse upgradeResponse = ReadResponsePayload<CharacterUpgradeEnhanceSkillResponse>(harness,
                upgradePacketId, nameof(CharacterUpgradeEnhanceSkillResponse), "Pyroath upgrade response");
            AssertEqual(0, upgradeResponse.Code, "Pyroath upgrade response Code");
            AssertEqual(1, upgradeSaves.ReplaceOneCalls, "Pyroath upgrade saves character once");
        }

        // Condition not met (character below Lv.80 for condition 7341): no mutation, no save.
        AscNet.Common.Database.Character lowRoster = CreateTestCharacterRoster(characterId, level: 79);
        lowRoster.Uid = 48_108;
        using (MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out _, out RecordingMongoCollectionProxy<AscNet.Common.Database.Character> lowSaves, out _))
        using (LoopbackSessionHarness harness = new(lowRoster, CreateDrawCompatibilityPlayer(lowRoster.Uid),
            CreateInventory(lowRoster.Uid, unlockInventory), "v47-pyroath-condition"))
        {
            const int conditionPacketId = 12_403;
            InvokeRegisteredRequestHandler(nameof(CharacterUnlockEnhanceSkillRequest), harness.Session, conditionPacketId,
                new CharacterUnlockEnhanceSkillRequest { SkillGroupId = groupId });
            CharacterUnlockEnhanceSkillResponse conditionResponse = ReadResponsePayload<CharacterUnlockEnhanceSkillResponse>(harness,
                conditionPacketId, nameof(CharacterUnlockEnhanceSkillResponse), "Pyroath condition response");
            AssertEqual(20009021, conditionResponse.Code, "Pyroath condition rejection code");
            AssertEqual(0, lowSaves.ReplaceOneCalls, "Pyroath condition rejection does not save");
            AssertNoAvailablePacket(harness, "Pyroath condition rejection");
        }

        // Insufficient inventory: no mutation, no save.
        AscNet.Common.Database.Character poorRoster = CreateTestCharacterRoster(characterId, level: 80);
        poorRoster.Uid = 48_109;
        using (MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out _, out RecordingMongoCollectionProxy<AscNet.Common.Database.Character> poorSaves, out _))
        using (LoopbackSessionHarness harness = new(poorRoster, CreateDrawCompatibilityPlayer(poorRoster.Uid),
            CreateInventory(poorRoster.Uid, new Dictionary<int, long>()), "v47-pyroath-insufficient"))
        {
            const int poorPacketId = 12_404;
            InvokeRegisteredRequestHandler(nameof(CharacterUnlockEnhanceSkillRequest), harness.Session, poorPacketId,
                new CharacterUnlockEnhanceSkillRequest { SkillGroupId = groupId });
            CharacterUnlockEnhanceSkillResponse poorResponse = ReadResponsePayload<CharacterUnlockEnhanceSkillResponse>(harness,
                poorPacketId, nameof(CharacterUnlockEnhanceSkillResponse), "Pyroath insufficient response");
            AssertEqual(20012004, poorResponse.Code, "Pyroath insufficient rejection code");
            AssertEqual(0, poorSaves.ReplaceOneCalls, "Pyroath insufficient rejection does not save");
            AssertNoAvailablePacket(harness, "Pyroath insufficient rejection");
        }

        // Max level: an already-terminal skill cannot upgrade.
        AscNet.Common.Database.Character maxRoster = CreateTestCharacterRoster(characterId, level: 80);
        maxRoster.Uid = 48_110;
        CharacterData maxCharacter = RequiredCharacterData(maxRoster, characterId);
        int maxLevel = Character.EnhanceSkillMaxLevel(defaultSkillId);
        maxCharacter.EnhanceSkillList.Add(new CharacterSkill { Id = (uint)defaultSkillId, Level = maxLevel });
        using (MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out _, out RecordingMongoCollectionProxy<AscNet.Common.Database.Character> maxSaves, out _))
        using (LoopbackSessionHarness harness = new(maxRoster, CreateDrawCompatibilityPlayer(maxRoster.Uid),
            CreateInventory(maxRoster.Uid, upgradeInventory), "v47-pyroath-max"))
        {
            const int maxPacketId = 12_405;
            InvokeRegisteredRequestHandler(nameof(CharacterUpgradeEnhanceSkillRequest), harness.Session, maxPacketId,
                new CharacterUpgradeEnhanceSkillRequest { SkillGroupId = groupId, Count = 1 });
            CharacterUpgradeEnhanceSkillResponse maxResponse = ReadResponsePayload<CharacterUpgradeEnhanceSkillResponse>(harness,
                maxPacketId, nameof(CharacterUpgradeEnhanceSkillResponse), "Pyroath max response");
            AssertEqual(20009014, maxResponse.Code, "Pyroath max-level rejection code");
            AssertEqual(0, maxSaves.ReplaceOneCalls, "Pyroath max-level rejection does not save");
            AssertNoAvailablePacket(harness, "Pyroath max-level rejection");
        }
    }

    private static void ValidateVersion47EnhanceSkillSwitchCompatibility()
    {
        const int characterId = 1021005;   // Lucia: Crimson Weave, group 1025280 has alternates [102531, 102528]
        const uint activeId = 102531;
        const uint alternateId = 102528;

        AscNet.Common.Database.Character roster = CreateTestCharacterRoster(characterId, level: 80);
        roster.Uid = 48_111;
        CharacterData character = RequiredCharacterData(roster, characterId);
        character.EnhanceSkillList.Add(new CharacterSkill { Id = activeId, Level = 5 });
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out _, out RecordingMongoCollectionProxy<AscNet.Common.Database.Character> characterSaves, out _);
        using LoopbackSessionHarness harness = new(roster, CreateDrawCompatibilityPlayer(roster.Uid),
            CreateDrawCompatibilityInventory(roster.Uid, []), "v47-enhance-switch");

        // Switch to the alternate, preserving level.
        const int switchPacketId = 12_501;
        InvokeRegisteredRequestHandler(nameof(CharacterSwitchEnhanceSkillRequest), harness.Session, switchPacketId,
            new CharacterSwitchEnhanceSkillRequest { SkillId = (int)alternateId });
        NotifyCharacterDataList switchPush = ReadPushPayload<NotifyCharacterDataList>(harness, nameof(NotifyCharacterDataList),
            "CharacterSwitchEnhanceSkill NotifyCharacterDataList");
        CharacterSkill switched = switchPush.CharacterDataList.Single()
            .EnhanceSkillList.Single(skill => skill.Id == alternateId);
        AssertEqual(5, switched.Level, "CharacterSwitchEnhanceSkill preserves level");
        CharacterSwitchEnhanceSkillResponse switchResponse = ReadResponsePayload<CharacterSwitchEnhanceSkillResponse>(harness,
            switchPacketId, nameof(CharacterSwitchEnhanceSkillResponse), "CharacterSwitchEnhanceSkill response");
        AssertEqual(0, switchResponse.Code, "CharacterSwitchEnhanceSkill response Code");
        AssertEqual(1, characterSaves.ReplaceOneCalls, "CharacterSwitchEnhanceSkill saves once");
        AssertEqual(alternateId, character.EnhanceSkillList.Single().Id, "CharacterSwitchEnhanceSkill persisted active id");
        AssertEqual(1, character.EnhanceSkillList.Count, "CharacterSwitchEnhanceSkill keeps one active per group");

        // Idempotent switch to the same active skill: no save.
        InvokeRegisteredRequestHandler(nameof(CharacterSwitchEnhanceSkillRequest), harness.Session, switchPacketId + 1,
            new CharacterSwitchEnhanceSkillRequest { SkillId = (int)alternateId });
        CharacterSwitchEnhanceSkillResponse idempotentResponse = ReadResponsePayload<CharacterSwitchEnhanceSkillResponse>(harness,
            switchPacketId + 1, nameof(CharacterSwitchEnhanceSkillResponse), "CharacterSwitchEnhanceSkill idempotent response");
        AssertEqual(0, idempotentResponse.Code, "CharacterSwitchEnhanceSkill idempotent response Code");
        AssertEqual(1, characterSaves.ReplaceOneCalls, "CharacterSwitchEnhanceSkill idempotent does not save");

        // Foreign skill (not a member of an owned enhance group) is rejected.
        const uint foreignSkill = 102629;   // belongs to a different group (1026290), not owned/unlocked here
        InvokeRegisteredRequestHandler(nameof(CharacterSwitchEnhanceSkillRequest), harness.Session, switchPacketId + 2,
            new CharacterSwitchEnhanceSkillRequest { SkillId = (int)foreignSkill });
        CharacterSwitchEnhanceSkillResponse foreignResponse = ReadResponsePayload<CharacterSwitchEnhanceSkillResponse>(harness,
            switchPacketId + 2, nameof(CharacterSwitchEnhanceSkillResponse), "CharacterSwitchEnhanceSkill foreign rejection");
        AssertEqual(20009048, foreignResponse.Code, "CharacterSwitchEnhanceSkill foreign rejection code");
        AssertEqual(alternateId, character.EnhanceSkillList.Single().Id, "CharacterSwitchEnhanceSkill foreign keeps active");
        AssertNoAvailablePacket(harness, "CharacterSwitchEnhanceSkill foreign rejection");
    }

    private static void AssertLoginFashion(LoopbackSessionHarness harness, uint expectedFashionId)
    {
        NotifyLogin login = BuildNotifyLogin(harness);
        LoginCharacterList loginCharacter = login.CharacterList.Single();
        AssertEqual(expectedFashionId, (uint)loginCharacter.FashionId, "NotifyLogin persisted FashionId");
    }

    private static void AssertLoginHead(LoopbackSessionHarness harness, uint expectedHeadFashionId, int expectedType)
    {
        NotifyLogin login = BuildNotifyLogin(harness);
        LoginCharacterList loginCharacter = login.CharacterList.Single();
        AssertEqual(expectedHeadFashionId, (uint)loginCharacter.CharacterHeadInfo.HeadFashionId, "NotifyLogin persisted head fashion id");
        AssertEqual(expectedType, (int)loginCharacter.CharacterHeadInfo.HeadFashionType, "NotifyLogin persisted head fashion type");
    }

    private static NotifyLogin BuildNotifyLogin(LoopbackSessionHarness harness)
    {
        MethodInfo buildNotifyLogin = RequiredMethod(
            RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule"),
            "BuildNotifyLogin",
            BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(Session)]);
        return (NotifyLogin)(buildNotifyLogin.Invoke(null, [harness.Session])
            ?? throw new InvalidDataException("BuildNotifyLogin returned null."));
    }

    private static Dictionary<int, int> CostOfLevel(int skillId, int level, string name)
    {
        List<EnhanceSkillUpgradeTable> rows = Character.OrderedEnhanceSkillUpgrades(skillId);
        if (level >= rows.Count)
            throw new InvalidDataException($"{name}: {skillId} missing level {level}.");
        return AggregateCosts(rows[level], name);
    }

    private static Dictionary<int, int> AggregateCosts(EnhanceSkillUpgradeTable row, string name)
    {
        Dictionary<int, int> costs = [];
        if (row.CostItem is null || row.CostItemCount is null)
            throw new InvalidDataException($"{name}: row has no cost items.");
        int pairCount = Math.Min(row.CostItem.Count, row.CostItemCount.Count);
        for (int index = 0; index < pairCount; index++)
        {
            int itemId = row.CostItem[index];
            int count = row.CostItemCount[index];
            if (itemId <= 0 || count <= 0)
                continue;
            costs[itemId] = costs.GetValueOrDefault(itemId) + count;
        }
        if (costs.Values.Sum() <= 0)
            throw new InvalidDataException($"{name}: expected positive item costs.");
        return costs;
    }

    private static Dictionary<int, long> InitialCounts(IReadOnlyDictionary<int, int> costs, long surplus)
    {
        return costs.ToDictionary(cost => cost.Key, cost => (long)cost.Value + surplus);
    }
    private static void AssertCosts(
        NotifyItemDataList push,
        AscNet.Common.Database.Inventory inventory,
        IReadOnlyDictionary<int, long> initial,
        IReadOnlyDictionary<int, int> costs,
        string name)
    {
        foreach ((int itemId, int cost) in costs)
        {
            long expected = initial[itemId] - cost;
            AssertEqual(expected, push.ItemDataList.Single(item => item.Id == itemId).Count,
                $"{name} pushed item {itemId}");
            AssertEqual(expected, inventory.Items.Single(item => item.Id == itemId).Count,
                $"{name} persisted item {itemId}");
        }
    }

    private static AscNet.Common.Database.Inventory CreateInventory(long playerId, IReadOnlyDictionary<int, long> counts)
    {
        return CreateDrawCompatibilityInventory(playerId,
            counts.OrderBy(count => count.Key).Select(count => new Item { Id = count.Key, Count = count.Value }));
    }

    /// <summary>
    /// Karenina: Effulgence base-skill obtain/level regression. The CharacterSkillUpgrade terminal row
    /// for short-level (QTE/signature/ultimate) skills carries real costs, so the old
    /// max-from-upgrade-rows logic granted a level with no CharacterSkillLevelEffect row and the client
    /// reported "Construct data not found" (20009052). The server must cap at the constructible ceiling
    /// derived from CharacterSkillLevelEffect and reject over-level upgrades atomically.
    /// </summary>
    private static void ValidateVersion47EffulgenceSkillProgressionCompatibility()
    {
        const int characterId = 1071005; // Karenina: Effulgence
        const long playerId = 48_112;

        // Derive the previously-failing group through table relationships: an Effulgence skill whose
        // costed CharacterSkillUpgrade rows reach a higher ceiling than the constructible
        // CharacterSkillLevelEffect data. No captured skill id is used.
        CharacterSkillTable characterSkill = TableReaderV2.Parse<CharacterSkillTable>()
            .Single(row => row.CharacterId == characterId);
        uint failingSkillId = 0;
        int failingGroupId = 0;
        int failingConstructibleMax = 0;
        foreach (int groupId in characterSkill.SkillGroupId.Where(id => id > 0).Distinct())
        {
            uint skillId = TableReaderV2.Parse<CharacterSkillGroupTable>()
                .Single(group => group.Id == groupId)
                .SkillId.Select(Convert.ToUInt32).FirstOrDefault();
            if (skillId <= 0)
                continue;

            int constructibleMax = TableReaderV2.Parse<CharacterSkillLevelEffectTable>()
                .Where(effect => effect.SkillId == (int)skillId)
                .Select(effect => effect.Level)
                .DefaultIfEmpty()
                .Max();
            List<CharacterSkillUpgradeTable> upgradeRows = TableReaderV2.Parse<CharacterSkillUpgradeTable>()
                .Where(upgrade => upgrade.SkillId == (int)skillId)
                .ToList();
            int reachable = 1;
            while (true)
            {
                CharacterSkillUpgradeTable? row = upgradeRows.FirstOrDefault(upgrade => upgrade.Level == reachable);
                if (row is null || (row.UseCoin.GetValueOrDefault() == 0 && row.UseSkillPoint.GetValueOrDefault() == 0))
                    break;
                reachable++;
            }

            if (reachable > constructibleMax && constructibleMax >= 2)
            {
                failingGroupId = groupId;
                failingSkillId = skillId;
                failingConstructibleMax = constructibleMax;
                break;
            }
        }
        AssertEqual(true, failingGroupId > 0,
            "Effulgence has an over-ceiling skill with a natural in-ceiling upgrade (previously-failing group)");

        // A fresh authoritative character obtains only groups whose initial table condition is met.
        AscNet.Common.Database.Character freshRoster = CreateTestCharacterRoster(characterId, level: 80);
        CharacterData freshCharacter = RequiredCharacterData(freshRoster, characterId);
        int expectedInitialSkillCount = 0;
        foreach (int groupId in characterSkill.SkillGroupId.Where(id => id > 0).Distinct())
        {
            uint skillId = TableReaderV2.Parse<CharacterSkillGroupTable>()
                .Single(group => group.Id == groupId)
                .SkillId.Select(Convert.ToUInt32).FirstOrDefault();
            if (skillId <= 0)
                continue;
            CharacterSkillUpgradeTable? initialUpgrade = TableReaderV2.Parse<CharacterSkillUpgradeTable>()
                .FirstOrDefault(upgrade => upgrade.SkillId == (int)skillId && upgrade.Level == 0);
            bool unlocked = initialUpgrade is null
                || AscNet.Common.Database.Character.MeetsCharacterSkillCondition(freshCharacter, initialUpgrade.ConditionId, []);
            AssertEqual(unlocked, freshCharacter.SkillList.Any(skill => skill.Id == skillId),
                $"Effulgence fresh-obtain {groupId} initial condition");
            if (unlocked)
                expectedInitialSkillCount++;
        }
        AssertEqual(expectedInitialSkillCount, freshCharacter.SkillList.Count,
            "Effulgence fresh-obtain grants condition-eligible skill groups");

        // Level the affected skill within its constructible ceiling with table-derived costs.
        CharacterSkillUpgradeTable validTransition = TableReaderV2.Parse<CharacterSkillUpgradeTable>()
            .Single(upgrade => upgrade.SkillId == (int)failingSkillId && upgrade.Level == failingConstructibleMax - 1);
        long initialCoin = 1_000_000;
        long initialSkillPoint = 100_000;

        AscNet.Common.Database.Character upgradeRoster = CreateTestCharacterRoster(characterId, level: 80);
        upgradeRoster.Uid = playerId;
        CharacterData upgradeCharacter = RequiredCharacterData(upgradeRoster, characterId);
        upgradeCharacter.Quality = 4; // meets the short-skill quality gate (Condition 13105)
        if (upgradeCharacter.SkillList.All(skill => skill.Id != failingSkillId))
            upgradeCharacter.SkillList.Add(new CharacterSkill { Id = failingSkillId, Level = 1 });
        CharacterSkill upgradeSkill = RequiredCharacterSkill(upgradeCharacter, failingSkillId,
            "Effulgence valid upgrade setup skill");
        upgradeSkill.Level = failingConstructibleMax - 1;
        using (MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out _, out RecordingMongoCollectionProxy<AscNet.Common.Database.Character> upgradeSaves, out _))
        using (LoopbackSessionHarness harness = new(upgradeRoster, CreateDrawCompatibilityPlayer(playerId),
            CreateDrawCompatibilityInventory(playerId,
            [
                new Item { Id = Inventory.Coin, Count = initialCoin },
                new Item { Id = Inventory.SkillPoint, Count = initialSkillPoint }
            ]), "v47-effulgence-skill-valid-upgrade"))
        {
            const int upgradePacketId = 12_601;
            InvokeRegisteredRequestHandler(nameof(CharacterUpgradeSkillGroupRequest), harness.Session, upgradePacketId,
                new CharacterUpgradeSkillGroupRequest { SkillGroupId = failingGroupId, Count = 1 });
            NotifyCharacterDataList upgradeNotify = ReadPushPayload<NotifyCharacterDataList>(harness,
                nameof(NotifyCharacterDataList), "Effulgence valid upgrade NotifyCharacterDataList");
            CharacterSkill leveled = RequiredCharacterSkill(RequiredNotifyCharacterData(upgradeNotify, characterId,
                "Effulgence valid upgrade notify character"), failingSkillId, "Effulgence valid upgrade pushed skill");
            AssertEqual(failingConstructibleMax, leveled.Level, "Effulgence valid upgrade reaches constructible ceiling");
            NotifyItemDataList upgradeItems = ReadPushPayload<NotifyItemDataList>(harness, nameof(NotifyItemDataList),
                "Effulgence valid upgrade NotifyItemDataList");
            AssertEqual(initialCoin - validTransition.UseCoin.GetValueOrDefault(),
                upgradeItems.ItemDataList.Single(item => item.Id == Inventory.Coin).Count,
                "Effulgence valid upgrade deducted coin");
            AssertEqual(initialSkillPoint - validTransition.UseSkillPoint.GetValueOrDefault(),
                upgradeItems.ItemDataList.Single(item => item.Id == Inventory.SkillPoint).Count,
                "Effulgence valid upgrade deducted skill point");
            CharacterUpgradeSkillGroupResponse upgradeResponse = ReadResponsePayload<CharacterUpgradeSkillGroupResponse>(harness,
                upgradePacketId, nameof(CharacterUpgradeSkillGroupResponse), "Effulgence valid upgrade response");
            AssertEqual(0, upgradeResponse.Code, "Effulgence valid upgrade response Code");
            AssertEqual(1, upgradeSaves.ReplaceOneCalls, "Effulgence valid upgrade saves character once");
        }

        // Over-level upgrade (beyond the constructible ceiling) rejects atomically: no mutation, no save.
        AscNet.Common.Database.Character invalidRoster = CreateTestCharacterRoster(characterId, level: 80);
        invalidRoster.Uid = playerId + 1;
        CharacterData invalidCharacter = RequiredCharacterData(invalidRoster, characterId);
        invalidCharacter.Quality = 4; // passes the quality gate so the rejection is the max-level cap
        if (invalidCharacter.SkillList.All(skill => skill.Id != failingSkillId))
            invalidCharacter.SkillList.Add(new CharacterSkill { Id = failingSkillId, Level = 1 });
        CharacterSkill invalidSkill = RequiredCharacterSkill(invalidCharacter, failingSkillId,
            "Effulgence over-level upgrade setup skill");
        invalidSkill.Level = failingConstructibleMax;
        using (MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out _, out RecordingMongoCollectionProxy<AscNet.Common.Database.Character> invalidSaves, out _))
        using (LoopbackSessionHarness harness = new(invalidRoster, CreateDrawCompatibilityPlayer(playerId + 1),
            CreateDrawCompatibilityInventory(playerId + 1,
            [
                new Item { Id = Inventory.Coin, Count = initialCoin },
                new Item { Id = Inventory.SkillPoint, Count = initialSkillPoint }
            ]), "v47-effulgence-skill-over-level"))
        {
            const int invalidPacketId = 12_602;
            InvokeRegisteredRequestHandler(nameof(CharacterUpgradeSkillGroupRequest), harness.Session, invalidPacketId,
                new CharacterUpgradeSkillGroupRequest { SkillGroupId = failingGroupId, Count = 1 });
            CharacterUpgradeSkillGroupResponse invalidResponse = ReadResponsePayload<CharacterUpgradeSkillGroupResponse>(harness,
                invalidPacketId, nameof(CharacterUpgradeSkillGroupResponse), "Effulgence over-level response");
            AssertEqual(20009014, invalidResponse.Code, "Effulgence over-level upgrade rejection code (max level)");
            AssertEqual(failingConstructibleMax, invalidSkill.Level, "Effulgence over-level upgrade does not mutate skill");
            AssertEqual(0, invalidSaves.ReplaceOneCalls, "Effulgence over-level upgrade rejection does not save");
            AssertNoAvailablePacket(harness, "Effulgence over-level upgrade rejection");
        }

        static CharacterSkill RequiredCharacterSkill(CharacterData character, uint skillId, string name)
        {
            List<CharacterSkill> matches = character.SkillList.Where(skill => skill.Id == skillId).ToList();
            AssertEqual(1, matches.Count, $"{name} matching skill count");
            return matches[0];
        }

        static CharacterData RequiredNotifyCharacterData(NotifyCharacterDataList notify, int characterId, string name)
        {
            List<CharacterData> matches = notify.CharacterDataList.Where(character => character.Id == characterId).ToList();
            AssertEqual(1, matches.Count, $"{name} affected character count");
            return matches[0];
        }
    }
}
