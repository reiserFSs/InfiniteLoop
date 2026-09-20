using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.fuben;
using AscNet.Table.V2.share.theatre4;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Newtonsoft.Json.Linq;
using System.Reflection;

namespace AscNet.Test;

internal partial class Program
{
    // EN XEnumConst.Theatre4.FightLocateType / GridType / state values.
    private const int Theatre4AuditLocateGrid = 1;
    private const int Theatre4AuditLocateFate = 2;
    private const int Theatre4AuditSweepYellow = 2;
    private const int Theatre4AuditSweepRed = 1;
    private const int Theatre4AuditAssetColorCostPoint = 16;
    private const int Theatre4AuditYellowColor = 2;
    private const int Theatre4AuditKindGridFight = 1;
    private const int Theatre4AuditKindGridEvent = 2;
    private const int Theatre4AuditKindFate = 3;

    // This entry is intentionally independent from the older combat scenarios. It is called by
    // Program.Theatre4's reflection runner on both the preserved and corrected runtimes.
    private static void ValidateTheatre4CombatAuditCompatibility()
    {
        PacketFactory.LoadPacketHandlers();
        _ = GetRegisteredRequestHandler(nameof(Theatre4FightLocateRequest));
        Theatre4AuditValidateStartupBoundaries();
        Theatre4AuditValidatePowerBoostBoundaries();
        Theatre4AuditValidateLocateBoundaries();
        Theatre4AuditValidateSweepBoundaries();
        Theatre4AuditValidateSweepType421();
        Theatre4AuditValidateFightOutcomes();
        Theatre4AuditValidatePauseFrames();
        Theatre4AuditValidateRebootBoundaries();
        Theatre4AuditValidateRestartBoundaries();
        Theatre4AuditValidateRestartTransportRelog();
        Theatre4AuditValidateTerminalBoundaries();
        ValidateTheatre4NativeParametersCompatibility();
    }

    private static void ValidateTheatre4NativeParametersCompatibility()
    {
        int firstAction = 0, secondAction = 0, firstHp = 0, secondHp = 0;
        int firstDifficulty = 0, secondDifficulty = 0, firstExplore = 0, secondExplore = 0;
        foreach ((int Mode, int Difficulty, int ExploreCount) in new[] { (1, 1, 1), (4, 2, 2) })
        {
            using Theatre4Case test = new($"native-params-{Mode}");
            Theatre4AuditSeedDifficultyPrerequisite(test, Difficulty);
            test.StartRun(Difficulty);
            int mapId = test.Adventure.Chapters[0].MapId;
            while (test.Adventure.ExploreCount < ExploreCount)
            {
                Theatre4GridData step = test.Adventure.Chapters[0].Grids
                    .FirstOrDefault(grid => grid.State == T4StateDiscover)
                    ?? throw new InvalidDataException($"Native Mode{Mode} fixture has no authored explore step.");
                test.ExploreStep(mapId, step.PosX, step.PosY);
            }

            Theatre4AuditFightFixture fixture = Theatre4AuditFindFightFixture(
                reward: false, restartable: true, mode: Mode);
            Theatre4GridData grid = Theatre4AuditInstallGridFight(test, fixture, boss: false);
            test.RecruitAndSetTeam();
            test.Call(nameof(Theatre4FightLocateRequest), new Theatre4FightLocateRequest
            {
                Type = Theatre4AuditLocateGrid, MapId = mapId, PosX = grid.PosX, PosY = grid.PosY
            });
            JObject prepared = test.AuthorizeFrozenFight();
            JObject fightData = RequiredObject(prepared, "FightData", $"Native Mode{Mode} FightData");
            JObject parameters = RequiredObject(fightData, "StageParams", $"Native Mode{Mode} StageParams");
            Theatre4DifficultyTable difficulty = Theatre4DifficultyRows()
                .Single(row => row.Id == test.Adventure.Difficulty);
            int chapter = Math.Clamp(Math.Max(1, test.Adventure.Chapters.Count), 1, 5) - 1;
            int actionCount = Math.Max(1, test.Adventure.ExploreCount);
            Theatre4ActionFactorTable actionFactor = TableReaderV2.Parse<Theatre4ActionFactorTable>()
                .Where(row => row.Group == difficulty.ActionGroup && row.ActionTimes > 0
                    && row.ActionTimes <= actionCount)
                .OrderByDescending(row => row.ActionTimes).FirstOrDefault()
                ?? throw new InvalidDataException($"Native Mode{Mode} fixture has no authored action factor.");
            AssertEqual(Mode, int.Parse(RequiredValue<string>(parameters, "Mode", JTokenType.String,
                $"Native Mode{Mode} mode"), System.Globalization.CultureInfo.InvariantCulture),
                $"Native Mode{Mode} selects the authored mode");
            JArray modeParams = JArray.Parse(RequiredValue<string>(parameters, "ModeParams", JTokenType.String,
                $"Native Mode{Mode} mode params"));
            AssertEqual(string.Join(",", fixture.Mold.ModeParams),
                string.Join(",", modeParams.Values<int>()),
                $"Native Mode{Mode} keeps its authored JSON mode operands");
            AssertEqual(true, Mode != 4 || modeParams.Count > 0 && modeParams[0]!.Value<int>() > 0,
                "Native Mode4 exposes its authored target threshold");
            AssertEqual(test.Adventure.Difficulty,
                int.Parse(RequiredValue<string>(parameters, "Theater4DiffcultId", JTokenType.String,
                    $"Native Mode{Mode} difficulty"), System.Globalization.CultureInfo.InvariantCulture),
                $"Native Mode{Mode} exposes the selected difficulty");
            int hp = int.Parse(RequiredValue<string>(parameters, "Theater4HpFactor", JTokenType.String,
                $"Native Mode{Mode} HP factor"), System.Globalization.CultureInfo.InvariantCulture);
            int action = int.Parse(RequiredValue<string>(parameters, "Theater4ActionNum", JTokenType.String,
                $"Native Mode{Mode} action factor"), System.Globalization.CultureInfo.InvariantCulture);
            AssertEqual(difficulty.HpFactor[chapter], hp, $"Native Mode{Mode} consumes the authored HP factor");
            AssertEqual(actionFactor.Factor, action, $"Native Mode{Mode} consumes the authored action factor");
            AssertEqual(true, hp > 0 && action > 0, $"Native Mode{Mode} has consumable nonzero factors");

            byte[] frozen = test.State.ActiveEncounter!.PreFightPayload!.ToArray();
            JObject retry = test.AuthorizeFrozenFight();
            AssertEqual(true, JToken.DeepEquals(prepared["FightData"], retry["FightData"]),
                $"Native Mode{Mode} retry replays the frozen StageParams");
            AssertEqual(true, frozen.SequenceEqual(test.State.ActiveEncounter!.PreFightPayload!),
                $"Native Mode{Mode} retry preserves the frozen PreFight bytes");

            if (Mode == 1)
            {
                firstAction = action; firstHp = hp; firstDifficulty = test.Adventure.Difficulty;
                firstExplore = actionCount;
            }
            else
            {
                secondAction = action; secondHp = hp; secondDifficulty = test.Adventure.Difficulty;
                secondExplore = actionCount;
            }
        }
        AssertEqual(true, firstDifficulty != secondDifficulty, "Native parameter cases cover two difficulties");
        AssertEqual(true, firstExplore != secondExplore, "Native parameter cases cover two exploration counts");
        AssertEqual(true, firstAction != secondAction || firstHp != secondHp,
            "Native parameter cases produce different authored scaling outputs");
    }

    // EN XTheatre4SetControl.lua:155-168 gates inherit before affix, requires the server-offered
    // item, and permits the ordinary no-inherit path. Atlas ownership alone is not an offer.
    private static void Theatre4AuditValidateStartupBoundaries()
    {
        Theatre4ItemTable[] items = TableReaderV2.Parse<Theatre4ItemTable>()
            .Where(row => row.IsProp is not > 0 && (row.EffectGroupId is > 0 || row.BackPrice is > 0))
            .Take(2).ToArray();
        if (items.Length < 2) throw new InvalidDataException("Theatre4 startup fixture needs two authored items.");
        int offered = items[0].Id;
        int atlasOnly = items[1].Id;
        int affix = TableReaderV2.Parse<Theatre4AffixTable>()
            .First(row => row.ConditionId is not > 0).Id;
        int difficulty = Theatre4DifficultyRows().OrderBy(row => row.Id).First().Id;

        using (Theatre4Case offeredCase = new("audit-inherit-offered"))
        {
            offeredCase.Data.PreAdventureSettleData = new Theatre4AdventureSettleData { InheritItems = [offered] };
            offeredCase.Data.ItemsAtlas.Add(atlasOnly);
            offeredCase.SaveFixture();
            offeredCase.Call(nameof(Theatre4SelectDifficultRequest), new Theatre4SelectDifficultRequest
            {
                Difficult = difficulty
            });
            offeredCase.Reject(nameof(Theatre4SelectInheritRequest),
                new Theatre4SelectInheritRequest { ItemId = atlasOnly },
                "Atlas-only inheritance item");
            offeredCase.Reject(nameof(Theatre4SelectAffixRequest),
                new Theatre4SelectAffixRequest { Affix = affix },
                "Affix before offered inheritance");
            offeredCase.Call(nameof(Theatre4SelectInheritRequest),
                new Theatre4SelectInheritRequest { ItemId = offered });
            AssertEqual(offered, offeredCase.Adventure.InheritItemId,
                "Inheritance accepts the offered item");
            offeredCase.Call(nameof(Theatre4SelectAffixRequest), new Theatre4SelectAffixRequest { Affix = affix });
            AssertEqual(affix, offeredCase.Adventure.Affix,
                "Affix selection follows accepted inheritance");
        }

        using (Theatre4Case noOffer = new("audit-no-inherit"))
        {
            noOffer.Data.ItemsAtlas.Add(atlasOnly);
            noOffer.SaveFixture();
            noOffer.Call(nameof(Theatre4SelectDifficultRequest), new Theatre4SelectDifficultRequest
            {
                Difficult = difficulty
            });
            noOffer.Call(nameof(Theatre4SelectAffixRequest), new Theatre4SelectAffixRequest { Affix = affix });
            AssertEqual(0, noOffer.Adventure.InheritItemId,
                "No-offer startup leaves inheritance unselected");
            AssertEqual(affix, noOffer.Adventure.Affix,
                "No-offer startup permits affix selection");
        }
    }

    // Theatre4Effect.tsv 1217 is the authored Type216 low-risk replacement. Its five entries
    // address ordinary chapter groups 1101..5101 only; elite groups and chapter-six group 6101 stay
    // on their authored fights. The route IDs below are the first two authored chapters for
    // difficulty 1 (Beginner) and 2 (Blue), respectively.
    private static void Theatre4AuditValidatePowerBoostBoundaries()
    {
        Theatre4EffectTable power = TableReaderV2.Parse<Theatre4EffectTable>()
            .Single(row => row.Id == 1217 && row.Type == 216);
        int[] replacements = power.Params.Where(id => id > 0).ToArray();
        AssertEqual(true, replacements.SequenceEqual(new[] { 1201, 2201, 3201, 5201, 6201 }),
            "Type216 uses the authored low-risk replacement groups");

        foreach (int difficulty in new[] { 1, 2 })
        {
            int secondMapGroup = difficulty switch
            {
                1 => 11002, // theatre4map/Theatre4MapBlueprint.tsv Beginner Route chapter 2
                2 => 42001, // theatre4map/Theatre4MapBlueprint.tsv Blue Route chapter 2
                _ => throw new InvalidDataException($"No Type216 route fixture for difficulty {difficulty}.")
            };

            using (Theatre4Case absent = new($"audit-power-absent-{difficulty}"))
            {
                Theatre4AuditSeedDifficultyPrerequisite(absent, difficulty);
                absent.StartRun(difficulty);
                AssertEqual(false, absent.Adventure.CustomEffects.Any(effect => effect.EffectId == power.Id),
                    $"Absent Type216 difficulty {difficulty} has no owned 1217 effect");
                Theatre4ChapterData second = Theatre4InvokeInitializeMap(absent, secondMapGroup);
                Theatre4AuditAssertPowerChapter(absent.Adventure.Chapters[0], 1101, 11001,
                    $"Absent Type216 difficulty {difficulty} chapter 1");
                Theatre4AuditAssertPowerChapter(second, 2101, 12001,
                    $"Absent Type216 difficulty {difficulty} chapter 2");

            }
            using (Theatre4Case owned = new($"audit-power-owned-{difficulty}"))
            {
                Theatre4AuditSeedDifficultyPrerequisite(owned, difficulty);
                Theatre4AuditStartPowerRun(owned, difficulty, power.Id);
                Theatre4ChapterData second = Theatre4InvokeInitializeMap(owned, secondMapGroup);
                Theatre4GridData firstOrdinary = Theatre4AuditAssertPowerChapter(
                    owned.Adventure.Chapters[0], 1201, 11001,
                    $"Owned Type216 difficulty {difficulty} chapter 1");
                Theatre4AuditAssertPowerChapter(second, 2201, 12001,
                    $"Owned Type216 difficulty {difficulty} chapter 2");
                Theatre4AuditValidatePowerPreFight(owned, firstOrdinary);
            }
        }

        using (Theatre4Case activation = new("audit-power-activation"))
        {
            activation.StartRun(1);
            Theatre4AuditValidatePowerActivation(activation, replacements);
        }

    }

    // Condition 1020677 is authored as 17410 [1, 1]. A positive difficulty-2
    // fixture therefore carries the same prior clear state as the canonical run setup.
    private static void Theatre4AuditSeedDifficultyPrerequisite(Theatre4Case test, int difficulty)
    {
        if (difficulty <= 1) return;
        Theatre4EndingTable successfulEnding = Theatre4EndingRows()
            .FirstOrDefault(row => row.PassType != 1)
            ?? throw new InvalidDataException("Theatre4 has no authored successful ending row.");
        test.Data.Difficultys[1] = 1;
        test.Data.Endings[successfulEnding.Id] = 1;
        test.SaveFixture();
    }

    private static void Theatre4AuditStartPowerRun(Theatre4Case test, int difficulty, int effectId)
    {
        test.Call(nameof(Theatre4SelectDifficultRequest),
            new Theatre4SelectDifficultRequest { Difficult = difficulty });
        Theatre4SeedEffect(test, effectId);
        AssertEqual(true, test.Adventure.CustomEffects.Any(effect => effect.EffectId == effectId),
            $"Owned Type216 difficulty {difficulty} stores effect {effectId}");
        int affix = TableReaderV2.Parse<Theatre4AffixTable>()
            .First(row => row.ConditionId is not > 0).Id;
        test.Call(nameof(Theatre4SelectAffixRequest), new Theatre4SelectAffixRequest { Affix = affix });
    }

    private static void Theatre4AuditValidatePowerActivation(Theatre4Case test,
        IReadOnlyList<int> replacements)
    {
        Theatre4ChapterData chapter = test.Adventure.Chapters[0];
        List<Theatre4GridData> ordinary = chapter.Grids.Where(grid =>
            grid.Type == T4GridMonster && grid.Fight is not null && grid.ContentGroup == 1101).ToList();
        if (ordinary.Count < 2)
            throw new InvalidDataException("Type216 activation fixture needs two unresolved ordinary fights.");

        Theatre4GridData located = ordinary[0];
        Theatre4GridData explored = ordinary[1];
        int mapId = chapter.MapId;
        located.State = T4StateExplored;
        explored.State = T4StateExplored;
        test.SaveFixture();
        test.RecruitAndSetTeam();
        test.Call(nameof(Theatre4FightLocateRequest), new Theatre4FightLocateRequest
        {
            Type = Theatre4AuditLocateGrid, MapId = mapId, PosX = located.PosX, PosY = located.PosY
        });
        Theatre4ActiveEncounter frozen = test.State.ActiveEncounter
            ?? throw new InvalidDataException("Type216 activation did not freeze the located fight.");
        var frozenIdentity = (
            Kind: frozen.Kind, MapId: frozen.MapId, PosX: frozen.PosX, PosY: frozen.PosY,
            GridId: frozen.GridId, FightGroupId: frozen.FightGroupId, FightId: frozen.FightId,
            StageId: frozen.StageId, FightUuid: frozen.FightUuid, Seed: frozen.Seed);

        Theatre4EffectTable power = TableReaderV2.Parse<Theatre4EffectTable>()
            .Single(row => row.Id == 1217 && row.Type == 216);
        List<Theatre4EffectGroupTable> effectGroups = TableReaderV2.Parse<Theatre4EffectGroupTable>();
        List<Theatre4ColorTalentTable> talents = TableReaderV2.Parse<Theatre4ColorTalentTable>();
        List<Theatre4ColorTalentPoolTable> pools = TableReaderV2.Parse<Theatre4ColorTalentPoolTable>();
        List<Theatre4ColorTalentSlotTable> slots = TableReaderV2.Parse<Theatre4ColorTalentSlotTable>();
        HashSet<int> powerEffectGroups = effectGroups
            .Where(group => group.Effects is { } ids && ids.Contains(power.Id))
            .Select(group => group.Id).ToHashSet();
        Theatre4ColorTalentTable powerTalent = talents
            .Single(talent => powerEffectGroups.Contains(talent.EffectGroupId));
        List<Theatre4ColorTalentSlotTable> yellowSlots = slots
            .Where(row => row.Color == Theatre4AuditYellowColor && row.Level > 0 && row.UnlockPoint is > 0)
            .OrderBy(row => row.Level).ToList();
        Theatre4ColorTalentSlotTable[] powerOfferSlots = yellowSlots
            .Where(slot => slot.GenerateType == 2
                && pools.Any(pool => pool.Group == slot.GeneratePoolGroup && pool.TalentId == powerTalent.Id))
            .ToArray();
        if (powerOfferSlots.Length == 0)
            throw new InvalidDataException(
                $"Type216 talent {powerTalent.Id} has no authored yellow Big-talent offer slot.");
        Theatre4ColorTalentSlotTable powerActivationSlot = powerOfferSlots[0];
        List<int> powerActivationPool = pools
            .Where(pool => pool.Group == powerActivationSlot.GeneratePoolGroup)
            .Select(pool => pool.TalentId).ToList();
        AssertEqual(true, powerActivationPool.Contains(powerTalent.Id),
            "Type216 activation slot owns the authored target in its pool");

        // Match the canonical talent fixtures: points open real WaitSlot offers, and every
        // selection is sent through Theatre4SelectTalentRequest. Type216 activation is staged
        // only in the earliest authored matching pool/slot, so this proof is not RNG-dependent.
        Theatre4AddAsset(test, T4AssetColorPoint, Theatre4AuditYellowColor,
            yellowSlots.Max(slot => slot.UnlockPoint ?? 0));
        int selectedSlotId = 0;
        Theatre4ColorTalentData yellow = test.Adventure.Colors
            .Single(color => color.Color == Theatre4AuditYellowColor);
        foreach (Theatre4ColorTalentSlotTable slot in yellowSlots)
        {
            Theatre4ColorTalentWaitSlotData wait = yellow.WaitSlot
                ?? throw new InvalidDataException("Type216 fixture lost the next authored talent offer.");
            AssertEqual(slot.Id, wait.SlotId, "Type216 follows authored yellow slot order");
            int offered = wait.TalentIds.FirstOrDefault();
            if (offered <= 0)
                throw new InvalidDataException("Type216 fixture has an empty talent offer.");
            // This proof exercises activation/replacement, not weighted draw luck: stage the
            // authored target into its first matching pool while retaining the real offer shape.
            if (slot.Id == powerActivationSlot.Id && !wait.TalentIds.Contains(powerTalent.Id))
            {
                wait.TalentIds[0] = powerTalent.Id;
                test.SaveFixture();
            }
            if (wait.TalentIds.Contains(powerTalent.Id))
            {
                AssertEqual(true, powerOfferSlots.Any(candidate => candidate.Id == wait.SlotId),
                    "Type216 offer uses an authored matching pool and Big slot");
                selectedSlotId = wait.SlotId;
                test.Call(nameof(Theatre4SelectTalentRequest), new Theatre4SelectTalentRequest
                {
                    Color = yellow.Color, TalentId = powerTalent.Id
                });
                break;
            }

            test.Call(nameof(Theatre4SelectTalentRequest),
                new Theatre4SelectTalentRequest { Color = yellow.Color, TalentId = offered });
            yellow = test.Adventure.Colors.Single(color => color.Color == Theatre4AuditYellowColor);
        }
        if (selectedSlotId == 0)
            throw new InvalidDataException(
                $"Type216 talent {powerTalent.Id} was not present in any authored yellow offer.");

        yellow = test.Adventure.Colors.Single(color => color.Color == Theatre4AuditYellowColor);
        AssertEqual(true, yellow.Slots.Any(slot => slot.SlotId == selectedSlotId
            && slot.Talents.Any(talent => talent.TalentId == powerTalent.Id)),
            "Type216 is acquired through the authored talent offer");
        Theatre4ActiveEncounter stillFrozen = test.State.ActiveEncounter
            ?? throw new InvalidDataException("Type216 activation discarded the located fight.");
        AssertEqual(frozenIdentity.Kind, stillFrozen.Kind, "Type216 preserves located encounter kind");
        AssertEqual(frozenIdentity.MapId, stillFrozen.MapId, "Type216 preserves located encounter map");
        AssertEqual(frozenIdentity.PosX, stillFrozen.PosX, "Type216 preserves located encounter X");
        AssertEqual(frozenIdentity.PosY, stillFrozen.PosY, "Type216 preserves located encounter Y");
        AssertEqual(frozenIdentity.GridId, stillFrozen.GridId, "Type216 preserves located encounter grid");
        AssertEqual(frozenIdentity.FightGroupId, stillFrozen.FightGroupId,
            "Type216 preserves located encounter group");
        AssertEqual(frozenIdentity.FightId, stillFrozen.FightId, "Type216 preserves located encounter fight");
        AssertEqual(frozenIdentity.StageId, stillFrozen.StageId, "Type216 preserves located encounter stage");
        AssertEqual(frozenIdentity.FightUuid, stillFrozen.FightUuid,
            "Type216 preserves located encounter UUID");
        AssertEqual(frozenIdentity.Seed, stillFrozen.Seed, "Type216 preserves located encounter seed");

        Theatre4GridData liveLocated = test.Grid(mapId, located.PosX, located.PosY);
        AssertEqual(1101, liveLocated.ContentGroup,
            "Type216 leaves the actually located ordinary fight unchanged");
        Theatre4GridData liveExplored = test.Grid(mapId, explored.PosX, explored.PosY);
        AssertEqual(replacements[0], liveExplored.ContentGroup,
            "Type216 updates an unlocated explored ordinary fight to the authored replacement");
        Theatre4GridData elite = test.Adventure.Chapters[0].Grids.First(grid =>
            grid.Type == T4GridMonster && grid.Fight is not null && grid.ContentGroup == 11001);
        AssertEqual(11001, elite.ContentGroup,
            "Type216 activation leaves an elite fight unchanged");
    }

    private static Theatre4GridData Theatre4AuditAssertPowerChapter(Theatre4ChapterData chapter,
        int ordinaryGroup, int eliteGroup, string name)
    {
        Theatre4GridData ordinary = chapter.Grids.FirstOrDefault(grid =>
            grid.Type == T4GridMonster && grid.Fight is not null && grid.ContentGroup == ordinaryGroup)
            ?? throw new InvalidDataException($"{name}: authored ordinary fight group {ordinaryGroup} is absent.");
        Theatre4GridData elite = chapter.Grids.FirstOrDefault(grid =>
            grid.Type == T4GridMonster && grid.Fight is not null && grid.ContentGroup == eliteGroup)
            ?? throw new InvalidDataException($"{name}: authored elite fight group {eliteGroup} is absent.");
        Theatre4FightTable fight = TableReaderV2.Parse<Theatre4FightTable>()
            .Single(row => row.Id == ordinary.ContentId);
        Theatre4FightMoldTable mold = TableReaderV2.Parse<Theatre4FightMoldTable>()
            .Single(row => row.Id == fight.MoldId);
        AssertEqual(true, ordinary.Fight is { } ordinaryFight && mold.StageId.Contains(ordinaryFight.StageId),
            $"{name}: replacement keeps an authored stage");
        Theatre4FightTable eliteFight = TableReaderV2.Parse<Theatre4FightTable>()
            .Single(row => row.Id == elite.ContentId);
        AssertEqual(true, eliteFight.MoldId > 0, $"{name}: elite fight remains authored");
        return ordinary;
    }


    private static void Theatre4AuditValidatePowerPreFight(Theatre4Case test,
        Theatre4GridData ordinary)
    {
        int mapId = test.Adventure.Chapters[0].MapId;
        Theatre4GridData live = test.Grid(mapId, ordinary.PosX, ordinary.PosY);
        live.State = T4StateExplored;
        test.SaveFixture();
        test.RecruitAndSetTeam();
        test.Call(nameof(Theatre4FightLocateRequest), new Theatre4FightLocateRequest
        {
            Type = Theatre4AuditLocateGrid, MapId = mapId, PosX = live.PosX, PosY = live.PosY
        });
        Theatre4ActiveEncounter encounter = test.State.ActiveEncounter
            ?? throw new InvalidDataException("Type216 locate did not freeze an encounter.");
        Theatre4GridData currentLive = test.Grid(mapId, live.PosX, live.PosY);
        AssertEqual(currentLive.ContentGroup, encounter.FightGroupId,
            "Type216 locate freezes the replacement group");
        int frozenStage = encounter.StageId;
        int frozenFight = encounter.FightId;
        JObject prepared = test.AuthorizeFrozenFight();
        Theatre4ActiveEncounter authorized = test.State.ActiveEncounter
            ?? throw new InvalidDataException("Type216 PreFight did not commit an encounter.");
        Theatre4AuditAssertNpcGroups(prepared, frozenFight, "Type216 PreFight");
        byte[] frozenPayload = authorized.PreFightPayload
            ?? throw new InvalidDataException("Type216 PreFight did not freeze its payload.");
        test.Relog("Type216 frozen room");
        AssertEqual(frozenFight, test.State.ActiveEncounter!.FightId,
            "Type216 relog preserves the frozen fight");
        AssertEqual(frozenStage, test.State.ActiveEncounter.StageId,
            "Type216 relog preserves the frozen stage");
        AssertEqual(true, frozenPayload.SequenceEqual(test.State.ActiveEncounter.PreFightPayload
            ?? throw new InvalidDataException("Type216 relog lost its frozen payload.")),
            "Type216 relog preserves the frozen PreFight bytes");
        JObject replay = test.AuthorizeFrozenFight();
        AssertEqual(true, JToken.DeepEquals(prepared["FightData"], replay["FightData"]),
            "Type216 PreFight retry replays the frozen replacement plan");
    }

    private static void Theatre4AuditAssertNpcGroups(JObject response, int fightId, string name)
    {
        if (response["FightData"] is not JObject fightData
            || fightData["NpcGroupList"] is not JArray groups)
            throw new InvalidDataException($"{name}: response omitted NpcGroupList.");
        Theatre4FightTable fight = TableReaderV2.Parse<Theatre4FightTable>()
            .Single(row => row.Id == fightId);
        Theatre4FightMoldTable mold = TableReaderV2.Parse<Theatre4FightMoldTable>()
            .Single(row => row.Id == fight.MoldId);
        int expected = mold.MonsterGroupIds.Count(id => id > 0);
        AssertEqual(expected, groups.Count, $"{name}: no extra Npc groups");
        AssertEqual(true, groups.All(group => group["NpcList"] is JArray list && list.Count > 0),
            $"{name}: every authored Npc group remains populated");
    }

    // EN XTheatre4Agency.lua:998-1021 only enters a battle for an explored Monster/Boss, authored
    // Type-4 event/fate, and a matching server-side fight. All rejected coordinates remain bytewise
    // unchanged, which catches visible/discover/processed and wrong-type fall-throughs.
    private static void Theatre4AuditValidateLocateBoundaries()
    {
        foreach (int invalidState in new[] { T4StateUnknown, T4StateVisible, T4StateDiscover, T4StateProcessed })
        {
            using Theatre4Case test = new($"audit-locate-state-{invalidState}");
            test.StartRun(1);
            Theatre4AuditFightFixture fixture = Theatre4AuditFindFightFixture(reward: false, restartable: true);
            int mapId = test.Adventure.Chapters[0].MapId;
            Theatre4GridData grid = Theatre4AuditInstallGridFight(test, fixture, boss: false);
            grid.State = invalidState;
            test.SaveFixture();
            Theatre4AuditRejectLocate(test, new Theatre4FightLocateRequest
            {
                Type = Theatre4AuditLocateGrid, MapId = mapId, PosX = grid.PosX, PosY = grid.PosY
            }, $"Locate rejects grid state {invalidState}");
        }

        using (Theatre4Case wrongType = new("audit-locate-wrong-type"))
        {
            wrongType.StartRun(1);
            Theatre4AuditFightFixture fixture = Theatre4AuditFindFightFixture(reward: false, restartable: true);
            int mapId = wrongType.Adventure.Chapters[0].MapId;
            Theatre4GridData grid = Theatre4AuditInstallGridFight(wrongType, fixture, boss: false);
            Theatre4AuditRejectLocate(wrongType, new Theatre4FightLocateRequest
            {
                Type = 99, MapId = mapId, PosX = grid.PosX, PosY = grid.PosY
            }, "Locate rejects an unknown type");
            grid.Type = T4GridEmpty;
            wrongType.SaveFixture();
            Theatre4AuditRejectLocate(wrongType, new Theatre4FightLocateRequest
            {
                Type = Theatre4AuditLocateGrid, MapId = mapId, PosX = grid.PosX, PosY = grid.PosY
            }, "Locate rejects a non-fight grid type");
        }

        foreach (bool boss in new[] { false, true })
        {
            using Theatre4Case test = new($"audit-locate-legal-{(boss ? "boss" : "monster")}");
            test.StartRun(1);
            Theatre4AuditFightFixture fixture = Theatre4AuditFindFightFixture(
                reward: false, restartable: true, nonFirstStage: true);
            int mapId = test.Adventure.Chapters[0].MapId;
            Theatre4GridData grid = Theatre4AuditInstallGridFight(test, fixture, boss);
            test.Call(nameof(Theatre4FightLocateRequest), new Theatre4FightLocateRequest
            {
                Type = Theatre4AuditLocateGrid, MapId = mapId, PosX = grid.PosX, PosY = grid.PosY
            });
            AssertEqual(fixture.Fight.Id, test.State.ActiveEncounter!.FightId,
                $"Legal {(boss ? "Boss" : "Monster")} locate freezes its authored fight");
            AssertEqual(Theatre4AuditKindGridFight, test.State.ActiveEncounter.Kind,
                "Legal grid locate records grid encounter kind");
        }


        // A locate-only room is idempotent for the same target, may relocate before PreFight, and
        // becomes target-bound once native authorization has been committed.
        using (Theatre4Case relocation = new("audit-locate-retry-relocation"))
        {
            relocation.StartRun(1);
            Theatre4AuditFightFixture fixture = Theatre4AuditFindFightFixture(
                reward: false, restartable: true, nonFirstStage: true);
            int mapId = relocation.Adventure.Chapters[0].MapId;
            Theatre4GridData first = Theatre4AuditInstallGridFight(relocation, fixture, boss: false);
            Theatre4GridData second = Theatre4AuditInstallGridFight(relocation, fixture, boss: false,
                skipGridId: first.GridId);

            relocation.Call(nameof(Theatre4FightLocateRequest), new Theatre4FightLocateRequest
            {
                Type = Theatre4AuditLocateGrid, MapId = mapId, PosX = first.PosX, PosY = first.PosY
            });
            Theatre4ActiveEncounter firstFreeze = relocation.State.ActiveEncounter!;
            long counter = relocation.State.RandomCounter;
            relocation.Call(nameof(Theatre4FightLocateRequest), new Theatre4FightLocateRequest
            {
                Type = Theatre4AuditLocateGrid, MapId = mapId, PosX = first.PosX, PosY = first.PosY
            });
            Theatre4ActiveEncounter sameFreeze = relocation.State.ActiveEncounter!;
            AssertEqual(firstFreeze.FightUuid, sameFreeze.FightUuid,
                "Same-target locate before PreFight preserves FightUuid");
            AssertEqual(firstFreeze.Seed, sameFreeze.Seed,
                "Same-target locate before PreFight preserves Seed");
            AssertEqual(counter, relocation.State.RandomCounter,
                "Same-target locate before PreFight does not advance RNG");

            relocation.Call(nameof(Theatre4FightLocateRequest), new Theatre4FightLocateRequest
            {
                Type = Theatre4AuditLocateGrid, MapId = mapId, PosX = second.PosX, PosY = second.PosY
            });
            Theatre4ActiveEncounter relocated = relocation.State.ActiveEncounter!;
            AssertEqual(second.GridId, relocated.GridId,
                "Different valid target relocates a pre-authorization room");
            AssertEqual(true, relocation.State.RandomCounter > counter,
                "Different target receives a new frozen RNG draw");

            relocation.RecruitAndSetTeam();
            relocation.AuthorizeFrozenFight();
            Theatre4AuditRejectLocate(relocation, new Theatre4FightLocateRequest
            {
                Type = Theatre4AuditLocateGrid, MapId = mapId, PosX = first.PosX, PosY = first.PosY
            }, "Authorized room rejects a different target");

            FightSettleResult report = Theatre4Case.SettleReport(relocation.State.ActiveEncounter!);
            JObject settled = relocation.Call(nameof(FightSettleRequest),
                new FightSettleRequest { Result = report }, verifyPersistence: false);
            AssertEqual((long)fixture.StageId, settled["Settle"]!.Value<long>("StageId"),
                "Non-first authored StageId survives settlement identity");
        }
        (Theatre4EventTable Event, Theatre4FightTable Fight, int Stage) eventFixture =
            Theatre4AuditFindEventFight(nonFirstStage: true);
        using (Theatre4Case eventCase = new("audit-locate-event"))
        {
            eventCase.StartRun(1);
            int mapId = eventCase.Adventure.Chapters[0].MapId;
            Theatre4GridData grid = Theatre4AuditInstallEvent(eventCase, eventFixture.Event.Id,
                eventFixture.Stage);
            eventCase.Call(nameof(Theatre4FightLocateRequest), new Theatre4FightLocateRequest
            {
                Type = Theatre4AuditLocateGrid, MapId = mapId, PosX = grid.PosX, PosY = grid.PosY
            });
            AssertEqual(eventFixture.Fight.Id, eventCase.State.ActiveEncounter!.FightId,
                "Legal Type-4 event locate resolves its authored fight");
            AssertEqual(Theatre4AuditKindGridEvent, eventCase.State.ActiveEncounter.Kind,
                "Type-4 event locate records event encounter kind");
        }

        (Theatre4FateTable Fate, Theatre4FateEventTable FateEvent, Theatre4EventTable Event,
            Theatre4FightTable Fight, int Stage) fateFixture = Theatre4AuditFindFateFight();
        using (Theatre4Case fateCase = new("audit-locate-fate"))
        {
            fateCase.StartRun(1);
            fateCase.Adventure.Fate = new Theatre4FateData
            {
                Id = fateFixture.Fate.Id,
                FateEvents =
                [
                    new Theatre4FateEventData
                    {
                        TableRowId = fateFixture.FateEvent.Id,
                        UniqueId = 880001,
                        Event = new Theatre4EventData
                        {
                            EventId = fateFixture.Event.Id, StageId = fateFixture.Stage
                        },
                        EventTimeLeft = Math.Max(1, fateFixture.FateEvent.Duration)
                    }
                ]
            };
            fateCase.SaveFixture();
            fateCase.Call(nameof(Theatre4FightLocateRequest), new Theatre4FightLocateRequest
            {
                Type = Theatre4AuditLocateFate
            });
            AssertEqual(fateFixture.Fight.Id, fateCase.State.ActiveEncounter!.FightId,
                "Legal Type-4 fate locate resolves its authored fight");
            AssertEqual(Theatre4AuditKindFate, fateCase.State.ActiveEncounter.Kind,
                "Fate locate records fate encounter kind");
        }

        using (Theatre4Case wrongEventCase = new("audit-locate-wrong-event"))
        {
            wrongEventCase.StartRun(1);
            Theatre4EventTable wrongEvent = TableReaderV2.Parse<Theatre4EventTable>()
                .First(row => row.Id > 0 && row.Type != 4);
            int mapId = wrongEventCase.Adventure.Chapters[0].MapId;
            Theatre4GridData grid = Theatre4AuditInstallEvent(wrongEventCase, wrongEvent.Id,
                Theatre4AuditFindFightFixture(reward: false, restartable: true).StageId);
            Theatre4AuditRejectLocate(wrongEventCase, new Theatre4FightLocateRequest
            {
                Type = Theatre4AuditLocateGrid, MapId = mapId, PosX = grid.PosX, PosY = grid.PosY
            }, "Locate rejects an authored non-Type-4 event");
        }
    }

    private static void Theatre4AuditRejectLocate(Theatre4Case test, Theatre4FightLocateRequest request,
        string name)
    {
        byte[] before = test.State.ToBson();
        test.Reject(nameof(Theatre4FightLocateRequest), request, name);
        AssertEqual(true, before.SequenceEqual(test.State.ToBson()), $"{name}: no mutation at all");
    }

    // The sweep enum follows the EN XTheatre4Agency SweepType values. A processed tile cannot be
    // farmed again; invalid values cannot accidentally enter the red buy-death branch.
    private static void Theatre4AuditValidateSweepBoundaries()
    {
        Theatre4EffectTable sweepEffect = TableReaderV2.Parse<Theatre4EffectTable>()
            .First(row => row.Type == 205);
        Theatre4AuditFightFixture fixture = Theatre4AuditFindFightFixture(reward: true, restartable: true);
        using Theatre4Case test = new("audit-sweep-boundaries");
        test.StartRun(1);
        Theatre4SeedEffect(test, sweepEffect.Id);
        int mapId = test.Adventure.Chapters[0].MapId;
        Theatre4GridData grid = Theatre4AuditInstallGridFight(test, fixture, boss: false);
        test.Adventure.Prosperity = Math.Max(test.Adventure.Prosperity,
            Theatre4RowIndex(fixture.Group.ProsperityLimit, test.Adventure.Difficulty));
        test.SetGold(1_000_000);
        test.SaveFixture();

        test.Reject(nameof(Theatre4SweepMosnterRequest), new Theatre4SweepMosnterRequest
        {
            MapId = mapId, PosX = grid.PosX, PosY = grid.PosY, SweepType = 0
        }, "Sweep rejects unsupported type");
        AssertEqual(T4StateExplored, test.Grid(mapId, grid.PosX, grid.PosY).State,
            "Unsupported sweep leaves tile explored");

        int rewardBefore = test.Adventure.Transactions.Count(transaction => transaction.Type == 4);
        test.Call(nameof(Theatre4SweepMosnterRequest), new Theatre4SweepMosnterRequest
        {
            MapId = mapId, PosX = grid.PosX, PosY = grid.PosY, SweepType = Theatre4AuditSweepYellow
        });
        AssertEqual(T4StateProcessed, test.Grid(mapId, grid.PosX, grid.PosY).State,
            "Legal yellow sweep processes its tile");
        AssertEqual(rewardBefore + 1,
            test.Adventure.Transactions.Count(transaction => transaction.Type == 4),
            "Legal sweep creates one claimable fight reward");
        AssertEqual(1, test.Adventure.EffectSweepTimes, "Legal sweep increments its authored counter");
        test.RepeatRejected(nameof(Theatre4SweepMosnterRequest), new Theatre4SweepMosnterRequest
        {
            MapId = mapId, PosX = grid.PosX, PosY = grid.PosY, SweepType = Theatre4AuditSweepYellow
        }, "Processed sweep");
        AssertEqual(1, test.Adventure.EffectSweepTimes, "Processed sweep cannot farm the counter");
        AssertEqual(rewardBefore + 1,
            test.Adventure.Transactions.Count(transaction => transaction.Type == 4),
            "Processed sweep cannot farm reward offers");
    }
    // Type421 is keyed by SweepType: only Params[0] == Yellow changes the yellow proxy to a
    // half-health transition. A partial transition never creates a reward or completion record;
    // red then pays the remaining-health fraction of the authored full red cost.
    private static void Theatre4AuditValidateSweepType421()
    {
        List<Theatre4EffectTable> effects = TableReaderV2.Parse<Theatre4EffectTable>();
        Theatre4EffectTable yellow = effects.Single(row => row.Type == 421
            && row.Params.Count >= 2 && row.Params[0] == Theatre4AuditSweepYellow);
        Theatre4EffectTable blue = effects.Single(row => row.Type == 421
            && row.Params.Count >= 2 && row.Params[0] == 3);
        Theatre4EffectTable red = effects.Single(row => row.Type == 422);
        AssertEqual(true, yellow.Params.SequenceEqual(new[] { Theatre4AuditSweepYellow, 5000 }),
            "Type421 yellow is the authored 5000-basis-point effect");
        AssertEqual(true, blue.Params.SequenceEqual(new[] { 3, 5000 }),
            "Type421 blue keeps its distinct authored colour parameter");
        Theatre4AuditValidateSweepType421Case("audit-sweep-type421-yellow",
            proxyAffix: true, sweepType: Theatre4AuditSweepYellow, expectHalf: true, red.Id);
        Theatre4AuditValidateSweepType421Case("audit-sweep-yellow-no421",
            proxyAffix: false, sweepType: Theatre4AuditSweepYellow, expectHalf: false);
        Theatre4AuditValidateSweepType421Case("audit-sweep-red-no421",
            proxyAffix: false, sweepType: Theatre4AuditSweepRed, expectHalf: false, red.Id);
        Theatre4AuditValidateSweepType421Case("audit-sweep-blue421-yellow",
            proxyAffix: false, sweepType: Theatre4AuditSweepYellow, expectHalf: false, blue.Id);
    }

    private static void Theatre4AuditValidateSweepType421Case(string name, bool proxyAffix,
        int sweepType, bool expectHalf, params int[] extraEffectIds)
    {
        using Theatre4Case test = new(name);
        Theatre4AuditStartSweepRun(test, proxyAffix, extraEffectIds);
        Theatre4AuditFightFixture fixture = Theatre4AuditFindFightFixture(reward: true, restartable: true);
        int mapId = test.Adventure.Chapters[0].MapId;
        Theatre4GridData grid = Theatre4AuditInstallGridFight(test, fixture, boss: false);
        test.Adventure.Prosperity = Math.Max(test.Adventure.Prosperity,
            Theatre4RowIndex(fixture.Group.ProsperityLimit, test.Adventure.Difficulty));
        Theatre4EffectTable discount = TableReaderV2.Parse<Theatre4EffectTable>()
            .Single(row => row.Type == 206);
        int clearCost = Theatre4RowIndex(fixture.Group.ClearCost, test.Adventure.Difficulty);
        int fullRedCost = Theatre4RowIndex(fixture.Group.ClearRedColorCost, test.Adventure.Difficulty);
        if (clearCost <= 0 || fullRedCost <= 0)
            throw new InvalidDataException($"{name}: authored sweep costs are incomplete.");
        int discountBp = 10000 + discount.Params.FirstOrDefault();
        int expectedYellowCost = (int)((long)clearCost * discountBp / 10000);
        int goldBefore = 1_000_000;
        test.SetGold(goldBefore);
        test.SaveFixture();

        int rewardsBefore = test.Adventure.Transactions.Count(transaction => transaction.Type == 4);
        int finishBefore = test.Adventure.FinishFightIds.Count;
        int eliteBefore = test.Adventure.Chapters[0].EliteCount;
        Theatre4SweepMosnterRequest request = new()
        {
            MapId = mapId, PosX = grid.PosX, PosY = grid.PosY, SweepType = sweepType
        };
        int expectedGold = sweepType == Theatre4AuditSweepYellow
            ? goldBefore - expectedYellowCost : goldBefore;
        int expectedRed = test.Adventure.Colors.Single(color => color.Color == 1).PointCanCost;
        if (sweepType == Theatre4AuditSweepRed)
        {
            Theatre4AddAsset(test, Theatre4AuditAssetColorCostPoint, 1, fullRedCost);
            expectedRed += fullRedCost - fullRedCost;
        }

        test.Call(nameof(Theatre4SweepMosnterRequest), request, transportId: 421_001);
        Theatre4GridData afterFirst = test.Grid(mapId, grid.PosX, grid.PosY);
        if (expectHalf)
        {
            AssertEqual(5000, afterFirst.Fight!.HpPercent, "Type421 yellow changes 10000 HP to 5000 HP");
            AssertEqual(T4StateExplored, afterFirst.State,
                "Type421 half sweep leaves the fight explored");
            AssertEqual(expectedGold, test.Adventure.Gold,
                "Type421 half sweep charges the discounted authored gold cost");
            AssertEqual(rewardsBefore,
                test.Adventure.Transactions.Count(transaction => transaction.Type == 4),
                "Type421 half sweep creates no fight reward");
            AssertEqual(finishBefore, test.Adventure.FinishFightIds.Count,
                "Type421 half sweep does not finish the fight");
            AssertEqual(eliteBefore, test.Adventure.Chapters[0].EliteCount,
                "Type421 half sweep does not advance chapter progression");

            byte[] firstResponse = test.LastResponseContent.ToArray();
            byte[] firstState = test.State.ToBson();
            byte[] firstGrid = afterFirst.ToBson();
            int goldAfterFirst = test.Adventure.Gold;
            test.Call(nameof(Theatre4SweepMosnterRequest), request, transportId: 421_001);
            AssertEqual(true, firstResponse.SequenceEqual(test.LastResponseContent),
                "Type421 exact retry replays the response bytes");
            AssertEqual(true, firstState.SequenceEqual(test.State.ToBson()),
                "Type421 exact retry leaves durable state bytewise unchanged");
            AssertEqual(true, firstGrid.SequenceEqual(test.Grid(mapId, grid.PosX, grid.PosY).ToBson()),
                "Type421 exact retry leaves the partial grid bytewise unchanged");
            AssertEqual(goldAfterFirst, test.Adventure.Gold,
                "Type421 exact retry does not charge gold again");

            int redBefore = test.Adventure.Colors.Single(color => color.Color == 1).PointCanCost;
            Theatre4AddAsset(test, Theatre4AuditAssetColorCostPoint, 1, fullRedCost);
            int expectedRedCost = (int)((long)(5000 / 100) * fullRedCost / 100);
            expectedRed = redBefore + fullRedCost - expectedRedCost;
            test.Call(nameof(Theatre4SweepMosnterRequest), new Theatre4SweepMosnterRequest
            {
                MapId = mapId, PosX = grid.PosX, PosY = grid.PosY, SweepType = Theatre4AuditSweepRed
            }, transportId: 421_002);
        }
        else if (sweepType == Theatre4AuditSweepYellow)
            AssertEqual(0, afterFirst.Fight!.HpPercent,
                $"{name}: non-Type421 yellow applies the full authored HP clear");

        Theatre4GridData cleared = test.Grid(mapId, grid.PosX, grid.PosY);
        AssertEqual(T4StateProcessed, cleared.State, $"{name}: clear processes the tile");
        AssertEqual(expectedGold, test.Adventure.Gold, $"{name}: gold balance");
        AssertEqual(rewardsBefore + 1,
            test.Adventure.Transactions.Count(transaction => transaction.Type == 4),
            $"{name}: clear creates one pending fight reward");
        AssertEqual(finishBefore + 1, test.Adventure.FinishFightIds.Count,
            $"{name}: clear records one completed fight");
        AssertEqual(true, test.Adventure.FinishFightIds.Contains(fixture.Fight.Id),
            $"{name}: clear records the authored fight identity");
        AssertEqual(Math.Max(0, eliteBefore - 1), test.Adventure.Chapters[0].EliteCount,
            $"{name}: clear advances chapter progression once");
        AssertEqual(1, test.Adventure.EffectSweepTimes, $"{name}: clear increments sweep count once");
        if (sweepType == Theatre4AuditSweepRed)
            AssertEqual(expectedRed, test.Adventure.Colors.Single(color => color.Color == 1).PointCanCost,
                $"{name}: red development cost");

        Theatre4TransactionData reward = test.Adventure.Transactions
            .SingleOrDefault(transaction => transaction.Type == 4
                && transaction.ConfigId == fixture.Fight.Id)
            ?? throw new InvalidDataException($"{name}: clear omitted its authored reward transaction.");
        AssertEqual(true, reward.Rewards.Count > 0, $"{name}: pending reward is populated");
        test.Call(nameof(Theatre4ConfirmFightRewardRequest),
            new Theatre4ConfirmFightRewardRequest { TransactionId = reward.Id, Index = 1 });
        AssertEqual(true, test.Adventure.Transactions.All(transaction => transaction.Id != reward.Id
                || transaction.SelectIds.Contains(1)),
            $"{name}: reward claim is recorded once");
        test.RepeatRejected(nameof(Theatre4ConfirmFightRewardRequest),
            new Theatre4ConfirmFightRewardRequest { TransactionId = reward.Id, Index = 1 },
            $"{name}: reward claim cannot grant twice");
    }

    private static void Theatre4AuditStartSweepRun(Theatre4Case test, bool proxyAffix,
        IReadOnlyList<int> extraEffectIds)
    {
        if (!proxyAffix)
            test.StartRun(1);
        else
        {
            test.Call(nameof(Theatre4SelectDifficultRequest),
                new Theatre4SelectDifficultRequest { Difficult = 1 });
            test.Data.Difficultys[5] = 1;
            test.SaveFixture();
            Theatre4EffectTable yellow = TableReaderV2.Parse<Theatre4EffectTable>()
                .Single(row => row.Type == 421 && row.Params.Count >= 2 && row.Params[0] == 2);
            HashSet<int> proxyGroups = TableReaderV2.Parse<Theatre4EffectGroupTable>()
                .Where(group => group.Effects.Contains(yellow.Id)).Select(group => group.Id).ToHashSet();
            Theatre4AffixTable proxy = TableReaderV2.Parse<Theatre4AffixTable>()
                .Single(row => proxyGroups.Contains(row.EffectGroupId));
            test.Call(nameof(Theatre4SelectAffixRequest),
                new Theatre4SelectAffixRequest { Affix = proxy.Id });
            AssertEqual(true, test.Adventure.CustomEffects.Any(effect => effect.EffectId == yellow.Id),
                "Proxy Campaign owns the authored yellow Type421 effect");
        }

        Theatre4EffectTable sweep = TableReaderV2.Parse<Theatre4EffectTable>()
            .Single(row => row.Type == 205);
        Theatre4EffectTable discount = TableReaderV2.Parse<Theatre4EffectTable>()
            .Single(row => row.Type == 206);
        Theatre4SeedEffect(test, sweep.Id);
        Theatre4SeedEffect(test, discount.Id);
        foreach (int effectId in extraEffectIds)
            Theatre4SeedEffect(test, effectId);
    }

    // Loss and force-exit are terminal battle outcomes, but only a real win creates a Type-4
    // reward offer. The offer is intentionally left unclaimed until ConfirmFightRewardRequest.
    private static void Theatre4AuditValidateFightOutcomes()
    {
        Theatre4AuditFightFixture fixture = Theatre4AuditFindFightFixture(reward: true, restartable: true);
        using Theatre4Case test = new("audit-fight-outcomes");
        test.StartRun(1);
        int mapId = test.Adventure.Chapters[0].MapId;
        Theatre4GridData loss = Theatre4AuditInstallGridFight(test, fixture, boss: false);
        Theatre4GridData forceExit = Theatre4AuditInstallGridFight(test, fixture, boss: false,
            skipGridId: loss.GridId);
        Theatre4GridData win = Theatre4AuditInstallGridFight(test, fixture, boss: false,
            skipGridId: forceExit.GridId);
        test.RecruitAndSetTeam();

        Theatre4AuditSettleGrid(test, mapId, loss, isWin: false, forceExit: false);
        AssertEqual(0, test.Adventure.Transactions.Count(transaction => transaction.Type == 4),
            "Battle loss creates no fight reward offer");
        AssertEqual(T4StateExplored, test.Grid(mapId, loss.PosX, loss.PosY).State,
            "Battle loss leaves its tile retryable");

        Theatre4AuditSettleGrid(test, mapId, forceExit, isWin: true, forceExit: true);
        AssertEqual(0, test.Adventure.Transactions.Count(transaction => transaction.Type == 4),
            "Force-exit creates no fight reward offer");
        AssertEqual(T4StateExplored, test.Grid(mapId, forceExit.PosX, forceExit.PosY).State,
            "Force-exit leaves its tile retryable");

        int transactionsBeforeWin = test.Adventure.Transactions.Count(transaction => transaction.Type == 4);
        Theatre4AuditSettleGrid(test, mapId, win, isWin: true, forceExit: false);
        Theatre4TransactionData reward = test.Adventure.Transactions
            .FirstOrDefault(transaction => transaction.Type == 4)
            ?? throw new InvalidDataException("A winning fight did not create a reward offer.");
        AssertEqual(transactionsBeforeWin + 1,
            test.Adventure.Transactions.Count(transaction => transaction.Type == 4),
            "Winning fight creates exactly one fight reward offer");
        AssertEqual(true, reward.Rewards.Count > 0, "Winning fight reward offer is populated");
        AssertEqual(0, reward.SelectIds.Count, "Winning fight reward remains unclaimed");
        test.Call(nameof(Theatre4ConfirmFightRewardRequest), new Theatre4ConfirmFightRewardRequest
        {
            TransactionId = reward.Id, Index = 1
        });
        AssertEqual(true, test.Adventure.Transactions.All(transaction => transaction.Id != reward.Id
                || transaction.SelectIds.Contains(1)),
            "Fight reward is claimed only after confirmation");
    }

    private static void Theatre4AuditSettleGrid(Theatre4Case test, int mapId, Theatre4GridData grid,
        bool isWin, bool forceExit)
    {
        test.Call(nameof(Theatre4FightLocateRequest), new Theatre4FightLocateRequest
        {
            Type = Theatre4AuditLocateGrid, MapId = mapId, PosX = grid.PosX, PosY = grid.PosY
        });
        test.AuthorizeFrozenFight();
        FightSettleResult report = Theatre4Case.SettleReport(test.State.ActiveEncounter!
            ?? throw new InvalidDataException("Settle fixture did not freeze an encounter."));
        report.IsWin = isWin;
        report.IsForceExit = forceExit;
        JObject response = test.Call(nameof(FightSettleRequest), new FightSettleRequest { Result = report },
            verifyPersistence: false);
        AssertEqual(0, response.Value<int>("Code"),
            $"{(forceExit ? "force-exit" : isWin ? "win" : "loss")} settles");
        AssertEqual(isWin && !forceExit, response["Settle"]!.Value<bool>("IsWin"),
            "Settle response reports the effective win outcome");
    }

    // EN XFightConfig/FightSettleResult accepts zero and positive pause frames subject to the
    // total-frame bound; negative ExSkillPauseFrame is malformed and must preserve the room.
    private static void Theatre4AuditValidatePauseFrames()
    {
        Theatre4AuditFightFixture fixture = Theatre4AuditFindFightFixture(reward: false, restartable: true);
        using (Theatre4Case zero = new("audit-exskill-pause-zero"))
        {
            zero.StartRun(1);
            Theatre4GridData grid = Theatre4AuditInstallGridFight(zero, fixture, boss: false);
            zero.RecruitAndSetTeam();
            zero.Call(nameof(Theatre4FightLocateRequest), new Theatre4FightLocateRequest
            {
                Type = Theatre4AuditLocateGrid, MapId = zero.Adventure.Chapters[0].MapId,
                PosX = grid.PosX, PosY = grid.PosY
            });
            zero.AuthorizeFrozenFight();
            FightSettleResult report = Theatre4Case.SettleReport(zero.State.ActiveEncounter!);
            report.ExSkillPauseFrame = 0;
            zero.Call(nameof(FightSettleRequest), new FightSettleRequest { Result = report },
                verifyPersistence: false);
            AssertEqual(true, zero.State.ActiveEncounter is { SettleReceipt: not null },
                "Zero ExSkillPauseFrame is legal");
        }

        using (Theatre4Case positive = new("audit-exskill-pause-positive"))
        {
            positive.StartRun(1);
            Theatre4GridData grid = Theatre4AuditInstallGridFight(positive, fixture, boss: false);
            positive.RecruitAndSetTeam();
            positive.Call(nameof(Theatre4FightLocateRequest), new Theatre4FightLocateRequest
            {
                Type = Theatre4AuditLocateGrid, MapId = positive.Adventure.Chapters[0].MapId,
                PosX = grid.PosX, PosY = grid.PosY
            });
            positive.AuthorizeFrozenFight();
            FightSettleResult report = Theatre4Case.SettleReport(positive.State.ActiveEncounter!);
            report.ExSkillPauseFrame = 1;
            positive.Call(nameof(FightSettleRequest), new FightSettleRequest { Result = report },
                verifyPersistence: false);
            AssertEqual(true, positive.State.ActiveEncounter is { SettleReceipt: not null },
                "Positive ExSkillPauseFrame is legal");
        }

        using (Theatre4Case negative = new("audit-exskill-pause-negative"))
        {
            negative.StartRun(1);
            Theatre4GridData grid = Theatre4AuditInstallGridFight(negative, fixture, boss: false);
            negative.RecruitAndSetTeam();
            negative.Call(nameof(Theatre4FightLocateRequest), new Theatre4FightLocateRequest
            {
                Type = Theatre4AuditLocateGrid, MapId = negative.Adventure.Chapters[0].MapId,
                PosX = grid.PosX, PosY = grid.PosY
            });
            negative.AuthorizeFrozenFight();
            FightSettleResult report = Theatre4Case.SettleReport(negative.State.ActiveEncounter!);
            report.ExSkillPauseFrame = -1;
            negative.Reject(nameof(FightSettleRequest), new FightSettleRequest { Result = report },
                "Negative ExSkillPauseFrame");
            AssertEqual(true, negative.State.ActiveEncounter is { SettleReceipt: null, PreFightPayload: not null },
                "Negative ExSkillPauseFrame leaves the authorized room retryable");
        }
    }

    // EN Theatre4Reboot.tsv defines MaxRebootCount and costs. AttemptCount includes the initial
    // PreFight, so exactly N reboot/restart actions are legal and N+1 is not; duplicate transport
    // ids are receipt replays, not extra charges.
    private static void Theatre4AuditValidateRebootBoundaries()
    {
        Theatre4AuditFightFixture fixture = Theatre4AuditFindFightFixture(reward: false, restartable: true);
        using Theatre4Case test = new("audit-reboot-ceiling");
        test.StartRun(1);
        Theatre4GridData grid = Theatre4AuditInstallGridFight(test, fixture, boss: false);
        test.RecruitAndSetTeam();
        test.Call(nameof(Theatre4FightLocateRequest), new Theatre4FightLocateRequest
        {
            Type = Theatre4AuditLocateGrid, MapId = test.Adventure.Chapters[0].MapId,
            PosX = grid.PosX, PosY = grid.PosY
        });
        test.AuthorizeFrozenFight();
        Theatre4ActiveEncounter encounter = test.State.ActiveEncounter!;
        Theatre4DifficultyTable difficulty = Theatre4DifficultyRows().Single(row => row.Id == 1);
        Theatre4RebootTable profile = TableReaderV2.Parse<Theatre4RebootTable>()
            .Single(row => row.Id == difficulty.RebootId);
        int max = profile.MaxRebootCount;
        AssertEqual(true, max > 0, "Reboot fixture has an authored positive ceiling");
        test.SetGold(1_000_000);

        test.Reject(nameof(FightRebootRequest), new FightRebootRequest
        {
            FightId = checked((int)encounter.FightUuid), RebootCount = 0
        }, "Zero reboot count");
        test.Reject(nameof(FightRebootRequest), new FightRebootRequest
        {
            FightId = checked((int)encounter.FightUuid), RebootCount = 2
        }, "Skipped reboot count");
        int duplicateId = 70_001;
        JObject first = test.Call(nameof(FightRebootRequest), new FightRebootRequest
        {
            FightId = checked((int)encounter.FightUuid), RebootCount = 1
        }, transportId: duplicateId);
        AssertEqual(0, first.Value<int>("Code"), "First reboot is accepted");
        int attemptsAfterFirst = test.State.ActiveEncounter!.AttemptCount;
        int goldAfterFirst = test.Adventure.Gold;
        byte[] firstBytes = test.LastResponseContent.ToArray();
        test.Call(nameof(FightRebootRequest), new FightRebootRequest
        {
            FightId = checked((int)encounter.FightUuid), RebootCount = 1
        }, transportId: duplicateId);
        AssertEqual(attemptsAfterFirst, test.State.ActiveEncounter!.AttemptCount,
            "Same-ID reboot retry does not add an attempt");
        AssertEqual(goldAfterFirst, test.Adventure.Gold, "Same-ID reboot retry does not charge gold");
        AssertEqual(true, firstBytes.SequenceEqual(test.LastResponseContent),
            "Same-ID reboot retry replays exact response bytes");

        for (int count = 2; count <= max; count++)
        {
            test.Call(nameof(FightRebootRequest), new FightRebootRequest
            {
                FightId = checked((int)encounter.FightUuid), RebootCount = count
            }, transportId: duplicateId + count);
            AssertEqual(1 + count, test.State.ActiveEncounter!.AttemptCount,
                $"Reboot count {count} consumes one authored attempt");
        }
        int attemptsBeforeRejected = test.State.ActiveEncounter!.AttemptCount;
        int goldBeforeRejected = test.Adventure.Gold;
        test.Reject(nameof(FightRebootRequest), new FightRebootRequest
        {
            FightId = checked((int)encounter.FightUuid), RebootCount = max + 1
        }, "Reboot beyond authored ceiling");
        AssertEqual(attemptsBeforeRejected, test.State.ActiveEncounter!.AttemptCount,
            "Reboot beyond ceiling does not add an attempt");
        AssertEqual(goldBeforeRejected, test.Adventure.Gold,
            "Reboot beyond ceiling does not charge gold");
    }

    private static void Theatre4AuditValidateRestartBoundaries()
    {
        Theatre4AuditFightFixture fixture = Theatre4AuditFindFightFixture(reward: false, restartable: true);
        using Theatre4Case test = new("audit-restart-ceiling");
        test.StartRun(1);
        Theatre4GridData grid = Theatre4AuditInstallGridFight(test, fixture, boss: false);
        test.RecruitAndSetTeam();
        test.Call(nameof(Theatre4FightLocateRequest), new Theatre4FightLocateRequest
        {
            Type = Theatre4AuditLocateGrid, MapId = test.Adventure.Chapters[0].MapId,
            PosX = grid.PosX, PosY = grid.PosY
        });
        JObject prepared = test.AuthorizeFrozenFight();
        Theatre4ActiveEncounter encounter = test.State.ActiveEncounter!;
        Theatre4DifficultyTable difficulty = Theatre4DifficultyRows().Single(row => row.Id == 1);
        Theatre4RebootTable profile = TableReaderV2.Parse<Theatre4RebootTable>()
            .Single(row => row.Id == difficulty.RebootId);
        int max = profile.MaxRebootCount;
        AssertEqual(true, max > 0, "Restart fixture has an authored positive ceiling");
        test.SetGold(1_000_000);
        int fightId = checked((int)encounter.FightUuid);
        int initialAttempts = encounter.AttemptCount;
        int initialGold = test.Adventure.Gold;
        int pendingTransport = 71_001;
        test.Players.BeforeReplaceOne = replacement =>
        {
            if (replacement.Theatre4.PendingMutation is null)
                throw new MongoDB.Driver.MongoException("Injected Theatre4 restart save failure.");
        };
        try
        {
            test.Call(nameof(FightRestartRequest), new FightRestartRequest { FightId = fightId },
                success: false, verifyPersistence: false, transportId: pendingTransport);
        }
        finally
        {
            test.Players.BeforeReplaceOne = null;
        }
        AssertEqual(true, test.State.PendingMutation is not null,
            "Restart save failure keeps its pending mutation");
        Player durablePending = BsonSerializer.Deserialize<Player>(test.Players.LastSuccessfulReplacementBson
            ?? throw new InvalidDataException("Restart pending mutation was not persisted."));
        AssertEqual(true, durablePending.Theatre4.PendingMutation is not null,
            "Restart save failure leaves a durable pending journal");
        int pendingSeed = unchecked((int)test.State.PendingMutation!.Outcome.ActiveEncounter!.Seed);
        AssertEqual(initialAttempts + 1,
            test.State.PendingMutation.Outcome.ActiveEncounter.AttemptCount,
            "Pending restart outcome consumes one attempt");
        AssertEqual(initialGold - profile.FubenRestartCost,
            test.State.PendingMutation.Outcome.Data.AdventureData!.Gold,
            "Pending restart outcome charges its authored cost exactly once");

        JObject recovered = test.Call(nameof(FightRestartRequest), new FightRestartRequest { FightId = fightId },
            verifyPersistence: false, transportId: pendingTransport + 1);
        AssertEqual(0, recovered.Value<int>("Code"), "Pending restart retry succeeds");
        int recoveredSeed = RequiredValue<int>(recovered, "Seed", JTokenType.Integer,
            "Pending restart response");
        AssertEqual(pendingSeed, recoveredSeed, "Pending restart retry returns its owed seed");
        AssertEqual(initialAttempts + 1, test.State.ActiveEncounter!.AttemptCount,
            "Pending restart retry does not consume another attempt");
        AssertEqual(initialGold - profile.FubenRestartCost, test.Adventure.Gold,
            "Pending restart retry does not charge another cost");
        AssertEqual(null, test.State.PendingMutation, "Committed restart clears its pending mutation");

        JObject replay = test.Call(nameof(FightRestartRequest), new FightRestartRequest { FightId = fightId },
            verifyPersistence: false, transportId: pendingTransport);
        AssertEqual(recoveredSeed, replay.Value<int>("Seed"),
            "Same-ID restart replay returns the committed seed");
        AssertEqual(initialAttempts + 1, test.State.ActiveEncounter!.AttemptCount,
            "Same-ID restart replay does not consume another attempt");
        test.AuthorizeFrozenFight();
        PreFightResponse.PreFightResponseFightData retryFight = MessagePackSerializer.Deserialize<PreFightResponse.PreFightResponseFightData>(
            test.State.ActiveEncounter!.PreFightPayload
                ?? throw new InvalidDataException("Restart retry lost the frozen PreFight payload."));
        AssertEqual((long)unchecked((uint)recoveredSeed), (long)retryFight.Seed,
            "Restart retry updates the frozen PreFight seed");

        for (int action = 2; action <= max; action++)
        {
            test.Call(nameof(FightRestartRequest), new FightRestartRequest { FightId = fightId },
                transportId: pendingTransport + 100 + action);
            AssertEqual(initialAttempts + action, test.State.ActiveEncounter!.AttemptCount,
                $"Restart action {action} consumes one authored attempt");
        }
        int attemptsBeforeRejected = test.State.ActiveEncounter!.AttemptCount;
        int goldBeforeRejected = test.Adventure.Gold;
        test.Reject(nameof(FightRestartRequest), new FightRestartRequest { FightId = fightId },
            "Restart beyond authored ceiling");
        AssertEqual(attemptsBeforeRejected, test.State.ActiveEncounter!.AttemptCount,
            "Restart beyond ceiling does not add an attempt");
        AssertEqual(goldBeforeRejected, test.Adventure.Gold,
            "Restart beyond ceiling does not charge gold");
    }

    // A transport id is a same-connection receipt key, not a durable operation id. Reusing it
    // after a fresh login must execute a new restart; only the same live connection replays it.
    private static void Theatre4AuditValidateRestartTransportRelog()
    {
        Theatre4AuditFightFixture fixture = Theatre4AuditFindFightFixture(reward: false, restartable: true);
        using Theatre4Case test = new("audit-restart-transport-relog");
        test.StartRun(1);
        Theatre4GridData grid = Theatre4AuditInstallGridFight(test, fixture, boss: false);
        test.RecruitAndSetTeam();
        int mapId = test.Adventure.Chapters[0].MapId;
        test.Call(nameof(Theatre4FightLocateRequest), new Theatre4FightLocateRequest
        {
            Type = Theatre4AuditLocateGrid, MapId = mapId, PosX = grid.PosX, PosY = grid.PosY
        });
        test.AuthorizeFrozenFight();
        Theatre4ActiveEncounter encounter = test.State.ActiveEncounter
            ?? throw new InvalidDataException("Restart transport fixture did not freeze an encounter.");
        int fightId = checked((int)encounter.FightUuid);
        Theatre4RebootTable profile = TableReaderV2.Parse<Theatre4RebootTable>()
            .Single(row => row.Id == Theatre4DifficultyRows().Single(row => row.Id == 1).RebootId);
        test.SetGold(1_000_000);
        int transportId = 72_001;
        JObject first = test.Call(nameof(FightRestartRequest), new FightRestartRequest { FightId = fightId },
            verifyPersistence: false, transportId: transportId);
        int firstSeed = RequiredValue<int>(first, "Seed", JTokenType.Integer, "First restart response");
        int firstAttempts = test.State.ActiveEncounter!.AttemptCount;
        int firstGold = test.Adventure.Gold;
        byte[] firstResponse = test.LastResponseContent.ToArray();
        JObject sameConnection = test.Call(nameof(FightRestartRequest),
            new FightRestartRequest { FightId = fightId }, verifyPersistence: false,
            transportId: transportId);
        AssertEqual(true, firstResponse.SequenceEqual(test.LastResponseContent),
            "Same-session restart id replays the exact response");
        AssertEqual(firstAttempts, test.State.ActiveEncounter!.AttemptCount,
            "Same-session restart id does not consume another attempt");
        AssertEqual(firstGold, test.Adventure.Gold,
            "Same-session restart id does not charge another cost");
        AssertEqual(firstSeed, RequiredValue<int>(sameConnection, "Seed", JTokenType.Integer,
            "Same-session restart response"), "Same-session restart id replays its seed");

        test.Relog("restart transport id");
        JObject reused = test.Call(nameof(FightRestartRequest),
            new FightRestartRequest { FightId = fightId }, verifyPersistence: false,
            transportId: transportId);
        int reusedSeed = RequiredValue<int>(reused, "Seed", JTokenType.Integer,
            "Fresh-session restart response");
        AssertEqual(firstAttempts + 1, test.State.ActiveEncounter!.AttemptCount,
            "Fresh-session reuse of a restart id consumes a new attempt");
        AssertEqual(firstGold - profile.FubenRestartCost, test.Adventure.Gold,
            "Fresh-session reuse of a restart id charges a fresh cost");
        AssertEqual(true, reusedSeed != firstSeed,
            "Fresh-session reuse of a restart id returns a new seed");
    }

    // Terminal source authority is XTheatre4Agency settlement presentation plus EndAdventure's
    // PassType counters: HP loss/abandonment remain presentable failures but only a legitimate win
    // increments permanent Endings/Difficultys. Difficulty reward drops remain consolation rewards.
    private static void Theatre4AuditValidateTerminalBoundaries()
    {
        using (Theatre4Case hpLoss = new("audit-terminal-hp-loss"))
        {
            FightSettleRequest request = Theatre4LastHpSettleRequest(hpLoss, out int endingsBefore);
            int difficultyClears = hpLoss.Data.Difficultys.GetValueOrDefault(1);
            hpLoss.Call(nameof(FightSettleRequest), request, verifyPersistence: false);
            AssertEqual(null, hpLoss.Data.AdventureData, "HP-loss settlement closes the adventure");
            AssertEqual(endingsBefore, hpLoss.Data.Endings.Values.Sum(),
                "HP-loss settlement does not count as a permanent ending");
            AssertEqual(difficultyClears, hpLoss.Data.Difficultys.GetValueOrDefault(1),
                "HP-loss settlement does not count a difficulty clear");
            AssertEqual(null, hpLoss.State.ActiveEncounter,
                "HP-loss settlement leaves no active encounter");
            AssertEqual(true, hpLoss.State.PendingSettleAdventure is not null,
                "HP-loss settlement retains a terminal presentation snapshot");
            NotifyTheatre4AdventureSettle ending = hpLoss.EndingPush("HP-loss terminal");
            AssertEndingShape(ending, 1, 1, "HP-loss terminal");
            Theatre4AuditAssertFailureConsolation(hpLoss, 1);
            NotifyTheatre4AdventureSettle recovered = hpLoss.RelogEnding("HP-loss terminal");
            AssertEndingShape(recovered, 1, 1, "HP-loss relog terminal");
            AssertEqual(endingsBefore, hpLoss.Data.Endings.Values.Sum(),
                "HP-loss relog remains non-permanent");
        }

        using (Theatre4Case abandon = new("audit-terminal-abandon"))
        {
            abandon.StartRun(1);
            int endingsBefore = abandon.Data.Endings.Values.Sum();
            abandon.Call(nameof(Theatre4SettleAdventureRequest));
            AssertEqual(null, abandon.Data.AdventureData, "Abandonment closes the adventure");
            AssertEqual(endingsBefore, abandon.Data.Endings.Values.Sum(),
                "Abandonment does not count a permanent ending");
            AssertEqual(null, abandon.State.ActiveEncounter, "Abandonment leaves no active encounter");
            NotifyTheatre4AdventureSettle ending = abandon.EndingPush("abandon terminal");
            AssertEndingShape(ending, 3, 1, "Abandon terminal");
            Theatre4AuditAssertFailureConsolation(abandon, 1);
            NotifyTheatre4AdventureSettle recovered = abandon.RelogEnding("abandon terminal");
            AssertEndingShape(recovered, 3, 1, "Abandon relog terminal");
            AssertEqual(endingsBefore, abandon.Data.Endings.Values.Sum(),
                "Abandon relog remains non-permanent");
        }

        using (Theatre4Case win = new("audit-terminal-win"))
        {
            win.StartRun(1);
            int endingsBefore = win.Data.Endings.Values.Sum();
            AssertEqual(true, win.CompleteRun(), "Authored route reaches a legitimate win");
            AssertEqual(endingsBefore + 1, win.Data.Endings.Values.Sum(),
                "Legitimate win records one permanent ending");
            AssertEqual(1, win.Data.Difficultys.GetValueOrDefault(1),
                "Legitimate win records one difficulty clear");
            AssertEqual(null, win.Data.AdventureData, "Legitimate win closes the adventure");
            NotifyTheatre4AdventureSettle ending = win.EndingPush("win terminal");
            AssertEndingShape(ending, 2, 1, "Win terminal");
            NotifyTheatre4AdventureSettle recovered = win.RelogEnding("win terminal");
            AssertEndingShape(recovered, 2, 1, "Win relog terminal");
            AssertEqual(endingsBefore + 1, win.Data.Endings.Values.Sum(),
                "Win relog does not duplicate the permanent ending");
        }
    }

    private static void Theatre4AuditAssertFailureConsolation(Theatre4Case test, int difficulty)
    {
        Theatre4DifficultyTable row = Theatre4DifficultyRows().Single(candidate => candidate.Id == difficulty);
        int dropId = row.RewardDrop.FirstOrDefault(id => id > 0);
        if (dropId <= 0) throw new InvalidDataException("Failure fixture has no authored consolation drop.");
        Theatre4RewardDropTable drop = TableReaderV2.Parse<Theatre4RewardDropTable>()
            .Single(candidate => candidate.Id == dropId);
        HashSet<(int Type, int Id, int Num)> authored = TableReaderV2.Parse<Theatre4RewardTable>()
            .Where(candidate => drop.GroupIds.Contains(candidate.GroupId) && candidate.Condition is not > 0)
            .Select(candidate => (candidate.ElementType, candidate.ElementId ?? 0, candidate.ElementCount))
            .ToHashSet();
        NotifyTheatre4Reward reward = test.Pushes
            .Where(push => push.Name == nameof(NotifyTheatre4Reward))
            .Select(push => MessagePackSerializer.Deserialize<NotifyTheatre4Reward>(push.Content))
            .FirstOrDefault() ?? throw new InvalidDataException("Failure settlement omitted consolation reward push.");
        AssertEqual(true, reward.Rewards.Count > 0, "Failure settlement consolation reward is nonempty");
        AssertEqual(true, reward.Rewards.All(asset => authored.Contains((asset.Type, asset.Id, asset.Num))),
            "Failure settlement consolation reward comes from the authored drop");
    }

    private static Theatre4GridData Theatre4AuditInstallGridFight(Theatre4Case test,
        Theatre4AuditFightFixture fixture, bool boss, int skipGridId = 0)
    {
        Theatre4GridData host = test.Adventure.Chapters[0].Grids
            .Where(grid => grid.GridId != skipGridId && grid.Type is not (T4GridBoss or T4GridStart or T4GridEvent)
                && grid.Fight is null && grid.Shop is null && grid.Event is null && grid.Building is null)
            .OrderByDescending(grid => grid.PosX + grid.PosY).FirstOrDefault()
            ?? throw new InvalidDataException("Theatre4 map has no spare fight tile.");
        Theatre4GridData grid = test.Grid(test.Adventure.Chapters[0].MapId, host.PosX, host.PosY);
        grid.Type = boss ? T4GridBoss : T4GridMonster;
        grid.State = T4StateExplored;
        grid.ContentGroup = fixture.Group.GroupId;
        grid.ContentId = fixture.Fight.Id;
        grid.Fight = new Theatre4FightData
        {
            FightGroupId = fixture.Group.Id,
            StageId = fixture.StageId,
            HpPercent = 10000,
            PunishCountdown = -1,
            FightEvents = fixture.Mold.FightEvents.Where(id => id > 0).ToList()
        };
        grid.Shop = null;
        grid.Event = null;
        grid.Building = null;
        test.SaveFixture();
        return grid;
    }

    private static Theatre4GridData Theatre4AuditInstallEvent(Theatre4Case test, int eventId, int stageId)
    {
        int mapId = test.Adventure.Chapters[0].MapId;
        Theatre4GridData host = test.Adventure.Chapters[0].Grids
            .Where(grid => grid.Type is not (T4GridBoss or T4GridStart)
                && grid.Fight is null && grid.Shop is null && grid.Event is null && grid.Building is null)
            .OrderByDescending(grid => grid.PosX + grid.PosY).FirstOrDefault()
            ?? throw new InvalidDataException("Theatre4 map has no spare event tile.");
        Theatre4GridData grid = test.Grid(mapId, host.PosX, host.PosY);
        grid.Type = T4GridEvent;
        grid.State = T4StateExplored;
        grid.ContentId = eventId;
        grid.ContentGroup = TableReaderV2.Parse<Theatre4EventGroupTable>()
            .FirstOrDefault(row => row.EventId == eventId)?.GroupId ?? 0;
        grid.Fight = null;
        grid.Shop = null;
        grid.Building = null;
        grid.Event = new Theatre4EventData { EventId = eventId, StageId = stageId };
        test.SaveFixture();
        return grid;
    }

    private static Theatre4AuditFightFixture Theatre4AuditFindFightFixture(bool reward, bool restartable,
        bool nonFirstStage = false, int? mode = null)
    {
        List<Theatre4FightTable> fights = TableReaderV2.Parse<Theatre4FightTable>();
        List<Theatre4FightMoldTable> molds = TableReaderV2.Parse<Theatre4FightMoldTable>();
        List<StageTable> stages = TableReaderV2.Parse<StageTable>();
        HashSet<int> deterministicDrops = Theatre4DeterministicDrops();
        foreach (Theatre4FightGroupTable group in TableReaderV2.Parse<Theatre4FightGroupTable>())
        {
            Theatre4FightTable? fight = fights.FirstOrDefault(row => row.Id == group.FightId);
            if (fight is null || reward != (fight.RewardDropId is > 0)) continue;
            if (reward && !deterministicDrops.Contains(fight.RewardDropId!.Value)) continue;
            Theatre4FightMoldTable? mold = molds.FirstOrDefault(row => row.Id == fight.MoldId);
            if (mold is null || (mode is not null && mold.Mode != mode)) continue;
            IEnumerable<int> authoredStages = nonFirstStage ? mold.StageId.Skip(1) : mold.StageId;
            foreach (int stageId in authoredStages.Where(id => id > 0))
            {
                StageTable? stage = stages.FirstOrDefault(row => row.StageId == stageId);
                if (stage is null || restartable != (Convert.ToInt32(stage.Restartable) != 0)) continue;
                if (group.Difficult.Count < 1 || group.Difficult[0] <= 0) continue;
                return new Theatre4AuditFightFixture(group, fight, mold, stageId);
            }
        }
        throw new InvalidDataException(
            $"No authored Theatre4 fixture (reward={reward}, restartable={restartable}, nonFirst={nonFirstStage}).");
    }

    private static (Theatre4EventTable Event, Theatre4FightTable Fight, int Stage)
        Theatre4AuditFindEventFight(bool nonFirstStage)
    {
        List<Theatre4FightTable> fights = TableReaderV2.Parse<Theatre4FightTable>();
        List<Theatre4FightMoldTable> molds = TableReaderV2.Parse<Theatre4FightMoldTable>();
        List<StageTable> stages = TableReaderV2.Parse<StageTable>();
        foreach (Theatre4EventTable evt in TableReaderV2.Parse<Theatre4EventTable>())
        {
            if (evt.Type != 4 || evt.FightId is not > 0) continue;
            Theatre4FightTable? fight = fights.FirstOrDefault(row => row.Id == evt.FightId.Value);
            if (fight is null || fight.RewardDropId is > 0) continue;
            Theatre4FightMoldTable? mold = molds.FirstOrDefault(row => row.Id == fight.MoldId);
            if (mold is null) continue;
            IEnumerable<int> authoredStages = nonFirstStage ? mold.StageId.Skip(1) : mold.StageId;
            int stage = authoredStages.FirstOrDefault(id => id > 0 && stages.Any(row => row.StageId == id));
            if (stage > 0) return (evt, fight, stage);
        }
        throw new InvalidDataException("No authored Type-4 event fight has a non-first stage.");
    }

    private static (Theatre4FateTable Fate, Theatre4FateEventTable FateEvent, Theatre4EventTable Event,
        Theatre4FightTable Fight, int Stage) Theatre4AuditFindFateFight()
    {
        Theatre4FateTable fate = TableReaderV2.Parse<Theatre4FateTable>()
            .First(row => row.Difficulty == 1);
        Dictionary<int, Theatre4EventTable> events = TableReaderV2.Parse<Theatre4EventTable>()
            .ToDictionary(row => row.Id);
        Dictionary<int, Theatre4FightTable> fights = TableReaderV2.Parse<Theatre4FightTable>()
            .ToDictionary(row => row.Id);
        Dictionary<int, Theatre4FightMoldTable> molds = TableReaderV2.Parse<Theatre4FightMoldTable>()
            .ToDictionary(row => row.Id);
        List<StageTable> stages = TableReaderV2.Parse<StageTable>();
        foreach (Theatre4FateEventTable fateEvent in TableReaderV2.Parse<Theatre4FateEventTable>()
            .Where(row => row.GroupId > 0))
        {
            int eventId = fateEvent.EventId;
            for (int depth = 0; depth < 64 && events.TryGetValue(eventId, out Theatre4EventTable? evt); depth++)
            {
                if (evt.Type == 4 && evt.FightId is > 0
                    && fights.TryGetValue(evt.FightId.Value, out Theatre4FightTable? fight)
                    && molds.TryGetValue(fight.MoldId, out Theatre4FightMoldTable? mold))
                {
                    int stage = mold.StageId.Skip(1)
                        .FirstOrDefault(id => id > 0 && stages.Any(row => row.StageId == id));
                    if (stage > 0) return (fate, fateEvent, evt, fight, stage);
                }
                if (evt.NextEvent is not > 0) break;
                eventId = evt.NextEvent.Value;
            }
        }
        throw new InvalidDataException("No authored Type-4 fate fight has a non-first stage.");
    }
    private sealed record Theatre4AuditFightFixture(
        Theatre4FightGroupTable Group,
        Theatre4FightTable Fight,
        Theatre4FightMoldTable Mold,
        int StageId);
}
