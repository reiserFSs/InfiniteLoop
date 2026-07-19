using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Handlers;
using AscNet.GameServer;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.fuben.bossinshot;
using AscNet.Table.V2.share.fuben;
using AscNet.Table.V2.share.robot;
using AscNet.Table.V2.share.theatre6;
using AscNet.Table.V2.share.theatre6pvp;
using MongoDB.Bson;
using System.Reflection;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidateVersion46ProtocolCompatibility()
    {
        ValidateBossInshot46Contracts();
        ValidateBossInshotClosure();
        ValidateTheatre646Contracts();
        ValidateFightSettleAuthorization();
    }

    private static void ValidateBossInshot46Contracts()
    {
        using MongoCollectionOverride mongoOverride =
            MongoCollectionOverride.InstallForStoryDeployVersionGapCompatibility(
                out RecordingMongoCollectionProxy<Player> playerCollection);
        List<BossInshotActivityTable> activities = TableReaderV2.Parse<BossInshotActivityTable>()
            .Where(x => x.TimeId is > 0 && x.CharacterIds.Count >= 2).ToList();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        BossInshotActivityTable active = activities
            .Where(activity => ActivityScheduleService.IsOpen(activity.TimeId!.Value, now))
            .OrderByDescending(activity => activity.Id)
            .FirstOrDefault()
            ?? throw new InvalidDataException("BossInshot requires a currently scheduled activity.");
        Dictionary<int, BossInshotCharacterTable> characters = TableReaderV2.Parse<BossInshotCharacterTable>().ToDictionary(x => x.Id);
        Dictionary<int, RobotTable> robots = TableReaderV2.Parse<RobotTable>().ToDictionary(x => x.Id);
        Type bossModule = typeof(BossInshotData).Assembly.GetType("AscNet.GameServer.Handlers.BossInshotModule", throwOnError: true)!;
        ValidateBossInshotLoginReconciliation(active, bossModule, playerCollection);

        foreach (BossInshotActivityTable activity in new[] { active })
        {
            Player player = CreateDrawCompatibilityPlayer(46_100 + activity.Id);
            InvokePrivateStaticWithArgs<bool>(bossModule, "PrepareLogin",
                [player, DateTimeOffset.UtcNow]);
            BossInshotData teaching = ((NotifyBossInshotData)InvokePrivateStatic<object>(bossModule, "BuildNotifyBossInshotData", player)).BossInshotData;
            AssertEqual(activity.Id, teaching.ActivityId, $"BossInshot activity {activity.Id} is table-authorized");
            AssertEqual(0, teaching.CharacterDatas.Count, $"BossInshot activity {activity.Id} hides ordinary character state before teaching");

            player.BossInshot.IsPassTeach = true;
            BossInshotData ordinary = ((NotifyBossInshotData)InvokePrivateStatic<object>(bossModule, "BuildNotifyBossInshotData", player)).BossInshotData;
            int[] expectedCharacters = activity.CharacterIds
                .Where(characters.ContainsKey)
                .Select(id => characters[id].RobotId)
                .Where(robots.ContainsKey)
                .Select(id => robots[id].CharacterId).Where(id => id > 0).ToArray();
            AssertIntegerList(expectedCharacters.Select(x => (long)x).ToArray(), ordinary.CharacterDatas.Select(x => (long)x.CharacterId).ToArray(), $"BossInshot activity {activity.Id} ordinary roster derives from activity/character/robot tables");
        }

        Player settlePlayer = CreateDrawCompatibilityPlayer(46_200);
        InvokePrivateStaticWithArgs<bool>(bossModule, "PrepareLogin",
            [settlePlayer, DateTimeOffset.UtcNow]);
        using LoopbackSessionHarness settleHarness = new(CreateDrawCompatibilityCharacter(46_200), settlePlayer, sessionId: "v46-boss-terminal-settle");
        BossInshotStageTable ordinaryStage = TableReaderV2.Parse<BossInshotStageTable>()
            .First(x => x.StageId != active.TeachStageId && active.BossIds.Contains(x.BossId) && x.UnlockConditionId is null);
        object?[] teachingGateArgs =
        [
            settleHarness.Session,
            new PreFightRequest.PreFightRequestPreFightData { StageId = checked((uint)ordinaryStage.StageId) },
            new PreFightResponse(),
            0
        ];
        AssertEqual(true, InvokePrivateStaticWithArgs<bool>(bossModule, "ApplyPreFight", teachingGateArgs), "BossInshot ordinary stage is claimed before teaching");
        AssertEqual(20215009, (int)teachingGateArgs[3]!, "BossInshot ordinary stage rejects before teaching");

        BossInshotStageTable lockedStage = TableReaderV2.Parse<BossInshotStageTable>()
            .First(x => x.StageId != active.TeachStageId && active.BossIds.Contains(x.BossId) && x.UnlockConditionId is > 0);
        settlePlayer.BossInshot.IsPassTeach = true;
        object?[] unlockGateArgs =
        [
            settleHarness.Session,
            new PreFightRequest.PreFightRequestPreFightData { StageId = checked((uint)lockedStage.StageId) },
            new PreFightResponse(),
            0
        ];
        AssertEqual(true, InvokePrivateStaticWithArgs<bool>(bossModule, "ApplyPreFight", unlockGateArgs), "BossInshot locked ordinary stage is claimed");
        AssertEqual(20215015, (int)unlockGateArgs[3]!, "BossInshot ordinary stage rejects below table unlock threshold");

        BossInshotTowerTable firstTower = TableReaderV2.Parse<BossInshotTowerTable>().OrderBy(x => x.Id).First();
        settlePlayer.BossInshot.PassStages.Add(new BossInshotPassStage { StageId = 30163264, MaxScore = 87001 });
        settlePlayer.BossInshot.AuthorizedTimeIds.Add(firstTower.TimeId);
        settlePlayer.BossInshot.CurrentTowerId = firstTower.Id - 1;
        settlePlayer.BossInshot.Towers.Add(new BossInshotTowerState { TowerId = firstTower.Id, SelectStageId = firstTower.Stages.First() });
        object?[] towerGateArgs =
        [
            settleHarness.Session,
            new PreFightRequest.PreFightRequestPreFightData { StageId = checked((uint)firstTower.Stages.First()), BossInshotTowerId = firstTower.Id },
            new PreFightResponse(),
            0
        ];
        AssertEqual(true, InvokePrivateStaticWithArgs<bool>(bossModule, "ApplyPreFight", towerGateArgs), "BossInshot future tower fight is claimed");
        AssertEqual(20215032, (int)towerGateArgs[3]!, "BossInshot tower above CurrentTowerId rejects");
        PropertyInfo pendingProperty = typeof(Session).GetProperty("PendingBossInshotFight") ?? throw new MissingMemberException(nameof(Session), "PendingBossInshotFight");
        object pending = Activator.CreateInstance(pendingProperty.PropertyType)!;
        pending.GetType().GetProperty("ActivityId")!.SetValue(pending, active.Id);
        pending.GetType().GetProperty("StageId")!.SetValue(pending, (uint)900001);
        pending.GetType().GetProperty("FightId")!.SetValue(pending, (uint)71);
        pending.GetType().GetProperty("CharacterConfigId")!.SetValue(pending, active.CharacterIds[0]);
        pending.GetType().GetProperty("CharacterId")!.SetValue(pending, 1);
        pendingProperty.SetValue(settleHarness.Session, pending);
        settlePlayer.TeamGroups[0] = new TeamGroupDatum { TeamType = 1, TeamId = 0, CaptainPos = 1, FirstFightPos = 1, TeamData = [] };
        settleHarness.Session.stage = CreateLoginAccountCompatibilityStage(settlePlayer.PlayerData.Id);
        FightSettleResponse genericSettle = InvokeFightSettleAchievementClear(
            settleHarness,
            10_030_201,
            settlePlayer.PlayerData.Id,
            46_221,
            46_222,
            addStars: 0,
            achievement: 2,
            expectedStageAchievement: 0,
            "generic pre-fight clears stale BossInshot pending");
        AssertEqual(0, genericSettle.Code, "generic fight settles normally after stale BossInshot pending");
        AssertEqual(true, pendingProperty.GetValue(settleHarness.Session) is null, "accepted generic pre-fight clears stale BossInshot pending");
        pendingProperty.SetValue(settleHarness.Session, pending);
        byte[] before = settlePlayer.BossInshot.ToBson();
        object?[] settleArgs = [settleHarness.Session, new FightSettleResult { StageId = 900002, FightId = 72, IsWin = true }, null];
        bool claimed = InvokePrivateStaticWithArgs<bool>(bossModule, "TrySettle", settleArgs);
        FightSettleResponse badSettle = (FightSettleResponse)settleArgs[2]!;
        AssertEqual(true, claimed, "BossInshot mismatched pending settle is terminally claimed");
        AssertEqual(20215023, badSettle.Code, "BossInshot mismatched pending settle rejects");
        AssertEqual(true, pendingProperty.GetValue(settleHarness.Session) is null, "BossInshot rejected settle consumes pending fight");
        AssertEqual(Convert.ToHexString(before), Convert.ToHexString(settlePlayer.BossInshot.ToBson()), "BossInshot rejected settle is mutation-free");

        NotifyBossInshotPlayback enabledPlayback =
            InvokePrivateStatic<NotifyBossInshotPlayback>(bossModule, "BuildNotifyBossInshotPlayback", settlePlayer);
        AssertEqual(true, enabledPlayback.IsPlayback, "BossInshot playback derives from an authorized replayable activity");
        Player inactivePlaybackPlayer = CreateDrawCompatibilityPlayer(46_201);

        NotifyBossInshotPlayback disabledPlayback =
            InvokePrivateStatic<NotifyBossInshotPlayback>(bossModule, "BuildNotifyBossInshotPlayback", inactivePlaybackPlayer);
        AssertEqual(false, disabledPlayback.IsPlayback, "BossInshot playback stays disabled without activity authority");

        object?[] oversizedStageArgs =
        [
            settleHarness.Session,
            new PreFightRequest.PreFightRequestPreFightData
            {
                StageId = uint.MaxValue,
                BossInshotTowerId = firstTower.Id
            },
            new PreFightResponse(),
            0
        ];
        AssertEqual(true, InvokePrivateStaticWithArgs<bool>(bossModule, "ApplyPreFight", oversizedStageArgs),
            "BossInshot oversized tower stage is claimed without checked-cast failure");
        AssertEqual(20215026, (int)oversizedStageArgs[3]!, "BossInshot oversized tower stage rejects");

        List<(int ConfigId, int CharacterId)> scoringCharacters = active.CharacterIds
            .Where(characters.ContainsKey)
            .Select(configId => (ConfigId: configId, CharacterId: robots.GetValueOrDefault(characters[configId].RobotId)?.CharacterId ?? 0))
            .Where(pair => pair.CharacterId > 0)
            .GroupBy(pair => pair.CharacterId)
            .Select(group => group.First())
            .Take(2)
            .ToList();
        if (scoringCharacters.Count != 2)
            throw new InvalidDataException("BossInshot score regression requires two distinct table-derived characters.");
        BossInshotScoreTable scoreRow = TableReaderV2.Parse<BossInshotScoreTable>()
            .First(row => row.Type == 1 && row.Score is > 0);
        Dictionary<int, int> scoreRecords = new() { [scoreRow.Id] = 1 };
        int scoreCap = checked((int)(active.TowerMaxScoreLimit ?? int.MaxValue));
        int expectedScore = InvokePrivateStatic<int>(bossModule, "CalculateScore", scoreRecords, scoreCap);
        if (expectedScore <= 0)
            throw new InvalidDataException($"BossInshot score row {scoreRow.Id} did not produce a positive score.");

        settlePlayer.BossInshot = new BossInshotState
        {
            ActivityId = active.Id,
            AuthorizedActivityId = active.Id,
            AuthorizedTimeIds = [active.TimeId!.Value],
            IsPassTeach = true
        };
        for (int index = 0; index < scoringCharacters.Count; index++)
        {
            (int configId, int characterId) = scoringCharacters[index];
            uint fightId = checked((uint)(800 + index));
            settleHarness.Session.PendingBossInshotFight = new PendingBossInshotFight
            {
                ActivityId = active.Id,
                StageId = checked((uint)ordinaryStage.StageId),
                FightId = fightId,
                CharacterConfigId = configId,
                CharacterId = characterId,
                IsTower = false
            };
            object?[] scoreSettleArgs =
            [
                settleHarness.Session,
                new FightSettleResult
                {
                    StageId = checked((uint)ordinaryStage.StageId),
                    FightId = fightId,
                    IsWin = true,
                    IntToIntRecord = scoreRecords,
                    LeftTime = 0
                },
                null
            ];
            AssertEqual(true, InvokePrivateStaticWithArgs<bool>(bossModule, "TrySettle", scoreSettleArgs),
                $"BossInshot character {characterId} settle is claimed");
            FightSettleResponse scoreResponse = (FightSettleResponse)scoreSettleArgs[2]!;
            AssertEqual(0, scoreResponse.Code, $"BossInshot character {characterId} settle Code");
            AssertEqual(expectedScore,
                settlePlayer.BossInshot.Characters.Single(state => state.CharacterId == characterId).TotalScore,
                $"BossInshot character {characterId} independent TotalScore");
        }
        AssertEqual(expectedScore,
            settlePlayer.BossInshot.PassStages.Single(stage => stage.StageId == ordinaryStage.StageId).MaxScore,
            "BossInshot global stage maximum remains shared");
        foreach ((int _, int characterId) in scoringCharacters)
        {
            BossInshotCharacterState characterScore =
                settlePlayer.BossInshot.Characters.Single(state => state.CharacterId == characterId);
            AssertEqual(expectedScore, characterScore.StageMaxScores[ordinaryStage.StageId],
                $"BossInshot character {characterId} per-stage maximum");
        }

        (int malformedConfigId, int malformedCharacterId) = scoringCharacters[0];
        const uint malformedFightId = 900;
        settleHarness.Session.PendingBossInshotFight = new PendingBossInshotFight
        {
            ActivityId = active.Id,
            StageId = checked((uint)ordinaryStage.StageId),
            FightId = malformedFightId,
            CharacterConfigId = malformedConfigId,
            CharacterId = malformedCharacterId,
            IsTower = false
        };
        byte[] beforeMalformed = settlePlayer.BossInshot.ToBson();
        object?[] malformedArgs =
        [
            settleHarness.Session,
            new FightSettleResult
            {
                StageId = checked((uint)ordinaryStage.StageId),
                FightId = malformedFightId,
                IsWin = true,
                IntToIntRecord = new Dictionary<object, object> { ["invalid"] = "value" },
                LeftTime = 0
            },
            null
        ];
        AssertEqual(true, InvokePrivateStaticWithArgs<bool>(bossModule, "TrySettle", malformedArgs),
            "BossInshot malformed record settle is claimed");
        AssertEqual(20215023, ((FightSettleResponse)malformedArgs[2]!).Code,
            "BossInshot malformed record settle rejects");
        AssertEqual(true, settleHarness.Session.PendingBossInshotFight is not null,
            "BossInshot malformed record keeps the authorized pending fight retryable");
        AssertEqual(Convert.ToHexString(beforeMalformed), Convert.ToHexString(settlePlayer.BossInshot.ToBson()),
            "BossInshot malformed record rejection is player-state atomic");

        object?[] oversizedTimeArgs =
        [
            settleHarness.Session,
            new FightSettleResult
            {
                StageId = checked((uint)ordinaryStage.StageId),
                FightId = malformedFightId,
                IsWin = true,
                IntToIntRecord = scoreRecords,
                LeftTime = long.MaxValue
            },
            null
        ];
        AssertEqual(true, InvokePrivateStaticWithArgs<bool>(bossModule, "TrySettle", oversizedTimeArgs),
            "BossInshot oversized LeftTime settle is claimed");
        AssertEqual(20215023, ((FightSettleResponse)oversizedTimeArgs[2]!).Code,
            "BossInshot oversized LeftTime settle rejects");
        AssertEqual(true, settleHarness.Session.PendingBossInshotFight is not null,
            "BossInshot oversized LeftTime keeps the authorized pending fight retryable");
        AssertEqual(Convert.ToHexString(beforeMalformed), Convert.ToHexString(settlePlayer.BossInshot.ToBson()),
            "BossInshot oversized LeftTime rejection is player-state atomic");

        object?[] retryArgs =
        [
            settleHarness.Session,
            new FightSettleResult
            {
                StageId = checked((uint)ordinaryStage.StageId),
                FightId = malformedFightId,
                IsWin = true,
                IntToIntRecord = scoreRecords,
                LeftTime = 0
            },
            null
        ];
        AssertEqual(true, InvokePrivateStaticWithArgs<bool>(bossModule, "TrySettle", retryArgs),
            "BossInshot corrected settle retry is claimed");
        AssertEqual(0, ((FightSettleResponse)retryArgs[2]!).Code, "BossInshot corrected settle retry succeeds");
        AssertEqual(true, settleHarness.Session.PendingBossInshotFight is null,
            "BossInshot successful corrected settle consumes pending fight");

        const uint persistenceFailureFightId = 901;
        settleHarness.Session.PendingBossInshotFight = new PendingBossInshotFight
        {
            ActivityId = active.Id,
            StageId = checked((uint)ordinaryStage.StageId),
            FightId = persistenceFailureFightId,
            CharacterConfigId = malformedConfigId,
            CharacterId = malformedCharacterId,
            IsTower = false
        };
        Dictionary<int, int> improvedScoreRecords = new() { [scoreRow.Id] = 2 };
        byte[] beforePersistenceFailure = settlePlayer.BossInshot!.ToBson();
        playerCollection.ThrowOnReplaceOne = true;
        object?[] persistenceFailureArgs =
        [
            settleHarness.Session,
            new FightSettleResult
            {
                StageId = checked((uint)ordinaryStage.StageId),
                FightId = persistenceFailureFightId,
                IsWin = true,
                IntToIntRecord = improvedScoreRecords,
                LeftTime = 0
            },
            null
        ];
        AssertEqual(true, InvokePrivateStaticWithArgs<bool>(bossModule, "TrySettle", persistenceFailureArgs),
            "BossInshot persistence-failure settle is claimed");
        AssertEqual(20215023, ((FightSettleResponse)persistenceFailureArgs[2]!).Code,
            "BossInshot persistence-failure settle rejects");
        AssertEqual(Convert.ToHexString(beforePersistenceFailure),
            Convert.ToHexString(settlePlayer.BossInshot!.ToBson()),
            "BossInshot persistence failure restores the complete player state");
        AssertEqual(true, settleHarness.Session.PendingBossInshotFight is not null,
            "BossInshot persistence failure keeps the pending fight retryable");

        playerCollection.ThrowOnReplaceOne = false;
        object?[] persistenceRetryArgs =
        [
            settleHarness.Session,
            new FightSettleResult
            {
                StageId = checked((uint)ordinaryStage.StageId),
                FightId = persistenceFailureFightId,
                IsWin = true,
                IntToIntRecord = improvedScoreRecords,
                LeftTime = 0
            },
            null
        ];
        AssertEqual(true, InvokePrivateStaticWithArgs<bool>(bossModule, "TrySettle", persistenceRetryArgs),
            "BossInshot persistence retry is claimed");
        AssertEqual(0, ((FightSettleResponse)persistenceRetryArgs[2]!).Code,
            "BossInshot persistence retry succeeds");
        AssertEqual(true, settleHarness.Session.PendingBossInshotFight is null,
            "BossInshot persistence retry consumes the pending fight");

        // A stale authorization must reject before any tower selection can leak across activities.
        settlePlayer.BossInshot.AuthorizedActivityId = int.MaxValue;
        int stale = InvokePrivateStatic<int>(bossModule, "SelectCore", settleHarness.Session, 1, 1, false);
        AssertEqual(20215001, stale, "BossInshot stale activity selection rejects");

        BossInshotTowerTable? selectable = TableReaderV2.Parse<BossInshotTowerTable>().FirstOrDefault(x => x.Type == 2 && x.Stages.Count >= 2 && x.TimeId > 0);
        if (selectable is not null)
        {
            int maximumTowerId = TableReaderV2.Parse<BossInshotTowerTable>().Max(x => x.Id);
            List<BossInshotTowerState> duplicateTowers =
            [
                new BossInshotTowerState { TowerId = selectable.Id, DrawStageIds = selectable.Stages.Take(2).ToList(), SelectStageId = selectable.Stages[0], SelectStageIdAfterAllPass = selectable.Stages[1], IsPass = selectable.Id == maximumTowerId }
            ];
            if (selectable.Id != maximumTowerId) duplicateTowers.Add(new BossInshotTowerState { TowerId = maximumTowerId, IsPass = true });
            settlePlayer.BossInshot = new BossInshotState { ActivityId = active.Id, AuthorizedActivityId = active.Id, AuthorizedTimeIds = [active.TimeId!.Value, selectable.TimeId], CurrentTowerId = selectable.Id,
                PassStages = [new BossInshotPassStage { StageId = 30163264, MaxScore = 87001 }], Towers = duplicateTowers };
            int duplicate = InvokePrivateStatic<int>(bossModule, "SelectCore", settleHarness.Session, selectable.Id, selectable.Stages[1], true);
            AssertEqual(20215029, duplicate, "BossInshot effective duplicate after-pass selection rejects");
        }
    }
    private static void ValidateBossInshotLoginReconciliation(
        BossInshotActivityTable activity,
        Type bossModule,
        RecordingMongoCollectionProxy<Player> playerCollection)
    {
        if (!ActivityScheduleService.TryGet(activity.TimeId!.Value, out ActivityScheduleEntry schedule))
            throw new InvalidDataException("BossInshot activity TimeId lacks an authoritative schedule.");

        Player player = CreateDrawCompatibilityPlayer(46_099);
        player.BossInshot = new BossInshotState
        {
            ActivityId = activity.Id,
            IsPassTeach = true,
            PassStages = [new BossInshotPassStage { StageId = activity.TeachStageId, MaxScore = 1 }]
        };
        int savesBefore = playerCollection.ReplaceOneCalls;
        DateTimeOffset openBoundary = DateTimeOffset.FromUnixTimeSeconds(schedule.StartTime);
        InvokePrivateStaticWithArgs<bool>(bossModule, "PrepareLogin", [player, openBoundary]);
        BossInshotState state = player.BossInshot
            ?? throw new InvalidDataException("BossInshot login did not initialize state.");
        AssertEqual(activity.Id, state.ActivityId, "BossInshot login selects the current table activity");
        AssertEqual(activity.Id, state.AuthorizedActivityId, "BossInshot login authorizes current table activity");
        AssertEqual(true, state.AuthorizedTimeIds.Contains(activity.TimeId.Value),
            "BossInshot login authorizes the current table TimeId");
        AssertEqual(true, state.IsPassTeach, "BossInshot relogin preserves same-activity progress");
        AssertEqual(savesBefore + 1, playerCollection.ReplaceOneCalls,
            "BossInshot login persists authorization migration");

        InvokePrivateStaticWithArgs<bool>(bossModule, "PrepareLogin", [player, openBoundary]);
        AssertEqual(savesBefore + 1, playerCollection.ReplaceOneCalls,
            "BossInshot relogin is idempotent");
        BossInshotData notify = InvokePrivateStatic<NotifyBossInshotData>(
            bossModule, "BuildNotifyBossInshotData", player).BossInshotData;
        AssertEqual(activity.Id, notify.ActivityId,
            "BossInshot notify emits current table activity for an eligible player");

        InvokePrivateStaticWithArgs<bool>(
            bossModule,
            "PrepareLogin",
            [player, DateTimeOffset.FromUnixTimeSeconds(schedule.EndTime)]);
        AssertEqual(0, player.BossInshot!.AuthorizedActivityId,
            "BossInshot closes at its authoritative end boundary");

        player.PlayerData.Level = 1;
        InvokePrivateStaticWithArgs<bool>(bossModule, "PrepareLogin", [player, openBoundary]);
        AssertEqual(0, player.BossInshot!.AuthorizedActivityId,
            "BossInshot rejects players below the table-backed function condition");
        notify = InvokePrivateStatic<NotifyBossInshotData>(
            bossModule, "BuildNotifyBossInshotData", player).BossInshotData;
        AssertEqual(0, notify.ActivityId,
            "BossInshot notify omits inactive or level-gated activity");
    }


    private static void ValidateTheatre646Contracts()
    {
        Type theatreModule = typeof(Theatre6EndGameResponse).Assembly.GetType("AscNet.GameServer.Handlers.Theatre6Module", throwOnError: true)!;
        Type pvpModule = typeof(Theatre6PvpStartResponse).Assembly.GetType("AscNet.GameServer.Handlers.Theatre6PvpModule", throwOnError: true)!;
        using MongoCollectionOverride mongoOverride = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out RecordingMongoCollectionProxy<Player> pvpPlayerCollection,
            out _,
            out _);
        MethodInfo finalize = theatreModule.GetMethod("TryFinalize", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException("Theatre6Module.TryFinalize");
        Theatre6CharacterTable character = TableReaderV2.Parse<Theatre6CharacterTable>().First(x => x.FashionIds > 0 && x.BaseSkill.Count > 0);
        Theatre6RunState captured = new() { ModeId = 1, IsWin = true, File = new Theatre6FileState { CharacterId = character.Id, FashionId = character.FashionIds,
            Attrs = [new Theatre6AttrState { AttrId = 1, Value = 700 }, new Theatre6AttrState { AttrId = 2, Value = 900 }, new Theatre6AttrState { AttrId = 3, Value = 25 }] } };
        object?[] capturedArgs = [captured, null];
        AssertEqual(true, (bool)finalize.Invoke(null, capturedArgs)!, "Theatre6 captured-compatible archive finalizes");
        Theatre6SettlementState capturedResult = (Theatre6SettlementState)capturedArgs[1]!;
        AssertEqual(14_500, capturedResult.File.Score, "Theatre6 captured 14,500 score remains table-derived");

        Theatre6SkillTable activeSkill = TableReaderV2.Parse<Theatre6SkillTable>().First(x => (x.Character == 0 || x.Character == character.Id) && x.SaveScore > 0);
        Theatre6RunState distinct = new() { ModeId = 2, IsWin = true, File = new Theatre6FileState { CharacterId = character.Id, FashionId = character.FashionIds,
            Skills = [new Theatre6SkillState { SlotType = 2, Position = 1, SkillId = activeSkill.Id }] } };
        object?[] distinctArgs = [distinct, null];
        AssertEqual(true, (bool)finalize.Invoke(null, distinctArgs)!, "Theatre6 distinct active-slot archive finalizes");
        Theatre6SettlementState distinctResult = (Theatre6SettlementState)distinctArgs[1]!;
        AssertEqual(activeSkill.SaveScore + character.BaseSkill.Skip(1).Sum(id => TableReaderV2.Parse<Theatre6SkillTable>().Single(x => x.Id == id).SaveScore), distinctResult.File.Score, "Theatre6 active-slot score derives from selected and remaining base skills");
        Theatre6RunState duplicateSlot = new() { ModeId = 3, File = new Theatre6FileState { CharacterId = character.Id, FashionId = character.FashionIds, Skills = [new Theatre6SkillState { SlotType = 2, Position = 1, SkillId = activeSkill.Id }, new Theatre6SkillState { SlotType = 2, Position = 1, SkillId = activeSkill.Id }] } };
        object?[] duplicateSlotArgs = [duplicateSlot, null];
        AssertEqual(false, (bool)finalize.Invoke(null, duplicateSlotArgs)!, "Theatre6 duplicate skill slots are rejected");
        Theatre6RunState duplicateAttr = new() { ModeId = 4, File = new Theatre6FileState { CharacterId = character.Id, FashionId = character.FashionIds, Attrs = [new Theatre6AttrState { AttrId = 1, Value = 700 }, new Theatre6AttrState { AttrId = 1, Value = 700 }] } };
        object?[] duplicateAttrArgs = [duplicateAttr, null];
        AssertEqual(false, (bool)finalize.Invoke(null, duplicateAttrArgs)!, "Theatre6 duplicate attributes are rejected");

        Theatre6PvpActivityTable season = TableReaderV2.Parse<Theatre6PvpActivityTable>().First();
        Player player = CreateDrawCompatibilityPlayer(46_300);
        Theatre6PvpState pvp = player.Theatre6.Pvp;
        pvp.AuthorizedSeasonId = season.Id;
        using LoopbackSessionHarness reconcileHarness = new(CreateDrawCompatibilityCharacter(46_300), player, sessionId: "v46-pvp-reconcile");
        int reconcile = InvokePrivateStatic<int>(pvpModule, "ReconcileSeason", reconcileHarness.Session, pvp, 123456L);
        List<Theatre6PvpRankTable> rankRows = TableReaderV2.Parse<Theatre6PvpRankTable>();
        Theatre6PvpRankTable expectedRank = InvokePrivateStatic<Theatre6PvpRankTable>(pvpModule, "RankForScore", season.InitPoint);
        AssertEqual(rankRows.First().Id, InvokePrivateStatic<Theatre6PvpRankTable>(pvpModule, "RankForScore", int.MinValue).Id, "Theatre6 first rank has open lower bound");
        AssertEqual(rankRows.Last().Id, InvokePrivateStatic<Theatre6PvpRankTable>(pvpModule, "RankForScore", int.MaxValue).Id, "Theatre6 last rank has open upper bound");
        int initialAp = TableReaderV2.Parse<Theatre6PvpConfigTable>().Single(x => x.Key == "ActionPointInit").Values;
        AssertEqual(0, reconcile, "Theatre6 fresh authorized PVP season initializes");
        AssertEqual(season.InitPoint, pvp.Score, "Theatre6 PVP initial score derives from activity table");
        AssertEqual(expectedRank.Id, pvp.RankId, "Theatre6 PVP initial rank derives from rank table");
        AssertEqual(initialAp, pvp.ActionPoint, "Theatre6 PVP initial AP derives from config table");
        Player apFirstPlayer = CreateDrawCompatibilityPlayer(46_301);
        apFirstPlayer.Theatre6.Pvp.AuthorizedSeasonId = season.Id;
        using (LoopbackSessionHarness apFirstHarness = new(CreateDrawCompatibilityCharacter(46_301), apFirstPlayer, sessionId: "v46-pvp-ap-first"))
        {
            InvokeRegisteredRequestHandler("Theatre6PvpGetActionPointRequest", apFirstHarness.Session, 46_311, null);
            Theatre6PvpGetActionPointResponse response = ReadResponsePayload<Theatre6PvpGetActionPointResponse>(apFirstHarness, 46_311, nameof(Theatre6PvpGetActionPointResponse), "Theatre6 AP-first response");
            AssertEqual(0, response.Code, "Theatre6 GetActionPoint-first initializes season");
            AssertEqual(initialAp, response.ActionPoint, "Theatre6 GetActionPoint-first returns table AP");
            AssertEqual(season.InitPoint, apFirstPlayer.Theatre6.Pvp.Score, "Theatre6 GetActionPoint-first initializes table score");
            AssertEqual(expectedRank.Id, apFirstPlayer.Theatre6.Pvp.RankId, "Theatre6 GetActionPoint-first initializes table rank");
        }

        Player refreshFirstPlayer = CreateDrawCompatibilityPlayer(46_302);
        refreshFirstPlayer.Theatre6.Pvp.AuthorizedSeasonId = season.Id;
        refreshFirstPlayer.Theatre6.Pvp.InitializedSeasonId = int.MaxValue;
        refreshFirstPlayer.Theatre6.Pvp.Score = int.MaxValue;
        refreshFirstPlayer.Theatre6.Pvp.BattleRecords.Add(new Theatre6BattleRecordState { BattleId = 99, RobotId = TableReaderV2.Parse<Theatre6PvpRobotTable>().First().Id });
        using (LoopbackSessionHarness refreshFirstHarness = new(CreateDrawCompatibilityCharacter(46_302), refreshFirstPlayer, sessionId: "v46-pvp-refresh-first"))
        {
            InvokeRegisteredRequestHandler("Theatre6PvpRefreshMatchRequest", refreshFirstHarness.Session, 46_312, null);
            Theatre6PvpRefreshMatchResponse response = ReadResponsePayload<Theatre6PvpRefreshMatchResponse>(refreshFirstHarness, 46_312, nameof(Theatre6PvpRefreshMatchResponse), "Theatre6 refresh-first response");
            AssertEqual(0, response.Code, "Theatre6 Refresh-first initializes season");
            AssertEqual(season.InitPoint, refreshFirstPlayer.Theatre6.Pvp.Score, "Theatre6 Refresh-first replaces stale-season score");
            AssertEqual(expectedRank.Id, refreshFirstPlayer.Theatre6.Pvp.RankId, "Theatre6 Refresh-first derives current rank");
            AssertEqual(initialAp, refreshFirstPlayer.Theatre6.Pvp.ActionPoint, "Theatre6 Refresh-first derives current AP");
            AssertEqual(0, refreshFirstPlayer.Theatre6.Pvp.BattleRecords.Count, "Theatre6 Refresh-first excludes stale-season records");
        }

        player.Theatre6.Files = [new Theatre6FileState { CharacterId = 101, SlotId = 1, Score = 11 }, new Theatre6FileState { CharacterId = 101, SlotId = 2, Score = 22 }];
        List<Theatre6FileState>? resolved = InvokePrivateStatic<List<Theatre6FileState>?>(pvpModule, "ResolveFiles", player,
            new List<Theatre6FileSlot> { new() { CharacterId = 101, SlotId = 2 } });
        AssertEqual(22, resolved!.Single().Score, "Theatre6 archive identity includes CharacterId and SlotId");

        Theatre6PvpRankTable rank = TableReaderV2.Parse<Theatre6PvpRankTable>().First(x => x.PvpBuffGroupId > 0);
        pvp.RankId = rank.Id;
        Theatre6PvpBuffGroupTable group = TableReaderV2.Parse<Theatre6PvpBuffGroupTable>().Single(x => x.Id == rank.PvpBuffGroupId);
        AssertEqual(20427027, InvokePrivateStatic<int>(pvpModule, "BuffCode", pvp, 0, true), "Theatre6 required attack buff is enforced");
        AssertEqual(0, InvokePrivateStatic<int>(pvpModule, "BuffCode", pvp, group.AttBuffs.First(), true), "Theatre6 attack buff maps through rank buff group");
        AssertEqual(20427028, InvokePrivateStatic<int>(pvpModule, "BuffCode", pvp, int.MaxValue, true), "Theatre6 out-of-group attack buff rejects");
        Theatre6PvpRobotTable robot = TableReaderV2.Parse<Theatre6PvpRobotTable>().First();
        Theatre6PlayerBattleDb robotWire = InvokePrivateStatic<Theatre6PlayerBattleDb>(pvpModule, "RobotDb", robot.Id);
        Theatre6PvpRankTable robotRank = InvokePrivateStatic<Theatre6PvpRankTable>(pvpModule, "RankForScore", robot.Score)
            ?? throw new InvalidDataException($"Theatre6 robot {robot.Id} score has no table rank.");
        AssertEqual(robotRank.Id, robotWire.RankId, "Theatre6 robot rank derives from score ranges");
        AssertEqual(robot.BuffId ?? 0, robotWire.DefenseBuffId, "Theatre6 robot defense buff derives from robot table");
        pvp.Battle = new Theatre6BattleState { EnemyUid = 7, EnemyRobotId = TableReaderV2.Parse<Theatre6PvpRobotTable>().First().Id, BuffId = group.AttBuffs.First() };
        Theatre6PvpActivityData activity = InvokePrivateStatic<Theatre6PvpActivityData>(pvpModule, "Activity", pvp);
        AssertEqual(pvp.Battle.BuffId, activity.AttackBuffId, "Theatre6 attack buff reconstructs from persisted battle");

        pvpPlayerCollection.CountDocumentsResults.Enqueue(100_000_000L);
        pvpPlayerCollection.CountDocumentsResults.Enqueue(1L);
        pvpPlayerCollection.CountDocumentsResults.Enqueue(74_999_999L);
        pvpPlayerCollection.FindResults = Enumerable.Range(0, 125)
            .Select(index =>
            {
                Player leader = CreateDrawCompatibilityPlayer(100_000 + index);
                leader.Theatre6.Pvp.AuthorizedSeasonId = season.Id;
                leader.Theatre6.Pvp.InitializedSeasonId = season.Id;
                leader.Theatre6.Pvp.Score = 1_000_000 - index;
                return leader;
            })
            .ToList();
        using (LoopbackSessionHarness rankHarness = new(
            CreateDrawCompatibilityCharacter(player.PlayerData.Id),
            player,
            sessionId: "v46-pvp-rank-scale"))
        {
            const int rankPacketId = 46_306;
            InvokeRegisteredRequestHandler(
                "Theatre6PvpQueryRankRequest",
                rankHarness.Session,
                rankPacketId,
                null);
            Theatre6PvpQueryRankResponse rankResponse =
                ReadResponsePayload<Theatre6PvpQueryRankResponse>(
                    rankHarness,
                    rankPacketId,
                    nameof(Theatre6PvpQueryRankResponse),
                    "Theatre6 bounded rank response");
            AssertEqual(100_000_000, rankResponse.TotalCount,
                "Theatre6 rank preserves 100M participant count");
            AssertEqual(75_000_000, rankResponse.SelfRank,
                "Theatre6 rank derives self position without materializing preceding players");
            AssertEqual(100, rankResponse.RankPlayerInfos.Count,
                "Theatre6 rank response is hard-capped");
            AssertEqual(100, pvpPlayerCollection.LastFindLimit,
                "Theatre6 rank pushes hard cap into Mongo query");
            AssertEqual(true, pvpPlayerCollection.LastFindHadSort,
                "Theatre6 rank pushes ordering into Mongo query");
            AssertEqual(2, pvpPlayerCollection.LastFindSort?.ElementCount ?? 0,
                "Theatre6 rank Mongo sort field count");
            AssertEqual(-1, pvpPlayerCollection.LastFindSort?["theatre6.pvp.score"].AsInt32 ?? 0,
                "Theatre6 rank Mongo score sort direction");
            AssertEqual(1, pvpPlayerCollection.LastFindSort?["player_data._id"].AsInt32 ?? 0,
                "Theatre6 rank Mongo player-id tiebreak direction");
            AssertEqual(3, pvpPlayerCollection.CountDocumentsCalls,
                "Theatre6 rank uses bounded database counts");
        }

        pvp.Battle = null;
        pvp.AuthorizedSeasonId = season.Id;
        pvp.InitializedSeasonId = season.Id;
        byte[] malformedDefenseBefore = pvp.ToBson();
        using (LoopbackSessionHarness malformedDefenseHarness = new(
            CreateDrawCompatibilityCharacter(player.PlayerData.Id),
            player,
            sessionId: "v46-pvp-defense-nil-slot"))
        {
            const int malformedDefensePacketId = 46_307;
            InvokeRegisteredRequestHandler(
                "Theatre6PvpUpdateDefenseRequest",
                malformedDefenseHarness.Session,
                malformedDefensePacketId,
                new Theatre6PvpUpdateDefenseRequest
                {
                    Slots = [null, null, null]
                });
            Theatre6PvpUpdateDefenseResponse malformedDefenseResponse =
                ReadResponsePayload<Theatre6PvpUpdateDefenseResponse>(
                    malformedDefenseHarness,
                    malformedDefensePacketId,
                    nameof(Theatre6PvpUpdateDefenseResponse),
                    "Theatre6 nil defense-slot response");
            AssertEqual(20427017, malformedDefenseResponse.Code,
                "Theatre6 nil defense slot returns FileInvalid");
            AssertEqual(
                Convert.ToHexString(malformedDefenseBefore),
                Convert.ToHexString(player.Theatre6.Pvp.ToBson()),
                "Theatre6 nil defense slot preserves PVP state");
        }

        pvp.AuthorizedSeasonId = 0;
        byte[] unavailableBefore = pvp.ToBson();
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(46_300), player, sessionId: "v46-pvp-unavailable");
        AssertUnavailableMutationFree("Theatre6GetPvpPreviewInfoRequest", null, nameof(Theatre6GetPvpPreviewInfoResponse), 46_291, harness, unavailableBefore);
        AssertUnavailableMutationFree("Theatre6PvpStartRequest", null, nameof(Theatre6PvpStartResponse), 46_292, harness, unavailableBefore);
        AssertUnavailableMutationFree("Theatre6PvpUpdateDefenseRequest", new Theatre6PvpUpdateDefenseRequest(), nameof(Theatre6PvpUpdateDefenseResponse), 46_293, harness, unavailableBefore);
        AssertUnavailableMutationFree("Theatre6PvpRefreshMatchRequest", null, nameof(Theatre6PvpRefreshMatchResponse), 46_294, harness, unavailableBefore);
        AssertUnavailableMutationFree("Theatre6PvpGetActionPointRequest", null, nameof(Theatre6PvpGetActionPointResponse), 46_295, harness, unavailableBefore);
        AssertUnavailableMutationFree("Theatre6PvpStartFightRequest", new Theatre6PvpStartFightRequest(), nameof(Theatre6PvpStartFightResponse), 46_301, harness, unavailableBefore);
        AssertUnavailableMutationFree("Theatre6PvpRestartFightRequest", null, nameof(Theatre6PvpRestartFightResponse), 46_302, harness, unavailableBefore);
        AssertUnavailableMutationFree("Theatre6PvpGiveUpFightRequest", null, nameof(Theatre6PvpGiveUpFightResponse), 46_303, harness, unavailableBefore);
        AssertUnavailableMutationFree("Theatre6PvpQueryRankRequest", null, nameof(Theatre6PvpQueryRankResponse), 46_304, harness, unavailableBefore);
        AssertUnavailableMutationFree("Theatre6PvpGetBattleRecordsRequest", null, nameof(Theatre6PvpGetBattleRecordsResponse), 46_305, harness, unavailableBefore);

        static void AssertUnavailableMutationFree(string requestName, object? request, string responseName, int packetId, LoopbackSessionHarness harness, byte[] expected)
        {
            InvokeRegisteredRequestHandler(requestName, harness.Session, packetId, request);
            Type responseType = typeof(Theatre6PvpStartResponse).Assembly.GetType($"AscNet.GameServer.Handlers.{responseName}", throwOnError: true)!;
            object response = ReadResponsePayload(harness, packetId, responseName, $"{requestName} unavailable response", responseType);
            int code = Convert.ToInt32(response.GetType().GetProperty("Code")!.GetValue(response));
            if (code == 0) throw new InvalidDataException($"{requestName} unexpectedly succeeded while unavailable.");
            AssertEqual(Convert.ToHexString(expected), Convert.ToHexString(harness.Session.player.Theatre6.Pvp.ToBson()), $"{requestName} unavailable path preserves AP/score/battle/stats");
        }
    }

    private static void ValidateFightSettleAuthorization()
    {
        using MongoCollectionOverride mongoOverride =
            MongoCollectionOverride.InstallForStoryDeployVersionGapCompatibility();
        StageTable stage = TableReaderV2.Parse<StageTable>()
            .First(row => row.StageId > 0 && row.StageId < 20_000_000);
        const long playerId = 46_400;
        Player player = CreateDrawCompatibilityPlayer(playerId);
        Inventory inventory = CreateDrawCompatibilityInventory(playerId, []);
        using LoopbackSessionHarness harness = new(
            CreateDrawCompatibilityCharacter(playerId),
            player,
            inventory,
            "fight-settle-authorization");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(playerId);
        byte[] playerBefore = player.ToBson();

        const int packetId = 46_401;
        InvokeRegisteredRequestHandler(
            nameof(FightSettleRequest),
            harness.Session,
            packetId,
            new FightSettleRequest
            {
                Result = new FightSettleResult
                {
                    StageId = checked((uint)stage.StageId),
                    FightId = 1,
                    IsWin = true
                }
            });
        FightSettleResponse response = ReadResponsePayload<FightSettleResponse>(
            harness,
            packetId,
            nameof(FightSettleResponse),
            "unauthorized generic fight settlement");
        AssertEqual(1033, response.Code, "generic fight settlement requires matching pre-fight context");
        AssertEqual(0, harness.Session.stage.Stages.Count, "unauthorized settlement does not write stage progress");
        AssertEqual(0, inventory.Items.Count, "unauthorized settlement does not grant inventory rewards");
        AssertEqual(Convert.ToHexString(playerBefore), Convert.ToHexString(player.ToBson()),
            "unauthorized settlement does not mutate player state");
    }

    private static T InvokePrivateStaticWithArgs<T>(Type type, string name, object?[] args)
    {
        MethodInfo method = type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(type.FullName, name);
        return (T)method.Invoke(null, args)!;
    }

    private static T InvokePrivateStatic<T>(Type type, string name, params object?[] args)
    {
        MethodInfo method = type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(type.FullName, name);
        return (T)method.Invoke(null, args)!;
    }
}
