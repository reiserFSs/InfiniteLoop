using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Game;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.condition;
using AscNet.Table.V2.share.fuben.bossinshot;
using AscNet.Table.V2.share.robot;
using MessagePack;
using MongoDB.Bson;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidateBossInshotClosure()
    {
        using MongoCollectionOverride mongoOverride = MongoCollectionOverride.InstallForBossInshotCompatibility(
            out RecordingMongoCollectionProxy<Player> playerCollection,
            out RecordingMongoCollectionProxy<BossInshotRankEntry> rankCollection);

        BossInshotActivityTable activity = TableReaderV2.Parse<BossInshotActivityTable>()
            .Single(row => row.TimeId is > 0 && row.TowerConditions is > 0 && row.TowerBossIds.Count > 0);
        Dictionary<int, BossInshotCharacterTable> characterRows = TableReaderV2.Parse<BossInshotCharacterTable>()
            .ToDictionary(row => row.Id);
        Dictionary<int, RobotTable> robotRows = TableReaderV2.Parse<RobotTable>()
            .ToDictionary(row => row.Id);
        int characterConfigId = activity.CharacterIds.First(configId =>
            characterRows.TryGetValue(configId, out BossInshotCharacterTable? character)
            && robotRows.TryGetValue(character.RobotId, out RobotTable? robot)
            && robot.CharacterId > 0);
        int characterId = robotRows[characterRows[characterConfigId].RobotId].CharacterId;
        Type bossModule = typeof(BossInshotData).Assembly.GetType(
            "AscNet.GameServer.Handlers.BossInshotModule",
            throwOnError: true)!;

        ValidateBossInshotEarlyLineupRejection(
            activity,
            characterRows[characterConfigId].RobotId);
        ValidateBossInshotBoundedRankQuery(
            activity,
            characterConfigId,
            characterId,
            rankCollection);
        ValidateBossInshotDuplicateNormalizationAndStableDraw(
            activity,
            characterId,
            bossModule);
        ValidateBossInshotAtomicTowerMutations(
            activity,
            playerCollection,
            bossModule);
        ValidateBossInshotPassedTowerReplay(
            activity,
            characterConfigId,
            characterId,
            bossModule);
        ValidateBossInshotHandlerRetry(
            activity,
            characterConfigId,
            characterId,
            playerCollection);
    }

    private static void ValidateBossInshotEarlyLineupRejection(
        BossInshotActivityTable activity,
        int robotId)
    {
        const long playerId = 46_690;
        Player player = CreateDrawCompatibilityPlayer(playerId);
        player.BossInshot = CreateAuthorizedBossInshotState(activity);
        using LoopbackSessionHarness harness = new(
            CreateDrawCompatibilityCharacter(playerId),
            player,
            sessionId: "v46-boss-prefight-lineup");

        PreFightRequest invalidRequest = new()
        {
            PreFightData = new PreFightRequest.PreFightRequestPreFightData
            {
                StageId = checked((uint)activity.TeachStageId),
                ChallengeCount = 1,
                CardIds = [0, 0, 0],
                RobotIds = Enumerable.Repeat(robotId, 32).ToList()
            }
        };
        const int invalidPacketId = 46_691;
        InvokeRegisteredRequestHandler(
            nameof(PreFightRequest),
            harness.Session,
            invalidPacketId,
            invalidRequest);
        PreFightResponse invalidResponse = ReadResponsePayload<PreFightResponse>(
            harness,
            invalidPacketId,
            nameof(PreFightResponse),
            "BossInshot oversized lineup response");
        AssertEqual(20215035, invalidResponse.Code,
            "BossInshot oversized robot lineup rejects");
        AssertEqual(0, invalidResponse.FightData.RoleData.Count,
            "BossInshot invalid lineup rejects before NPC expansion");
        AssertEqual(true, harness.Session.fight is null,
            "BossInshot invalid lineup does not authorize a fight");

        PreFightRequest paddedRequest = new()
        {
            PreFightData = new PreFightRequest.PreFightRequestPreFightData
            {
                StageId = checked((uint)activity.TeachStageId),
                ChallengeCount = 1,
                CardIds = [0, 0, 0],
                RobotIds = [0, robotId, 0]
            }
        };
        const int paddedPacketId = 46_692;
        InvokeRegisteredRequestHandler(
            nameof(PreFightRequest),
            harness.Session,
            paddedPacketId,
            paddedRequest);
        PreFightResponse paddedResponse = ReadResponsePayload<PreFightResponse>(
            harness,
            paddedPacketId,
            nameof(PreFightResponse),
            "BossInshot padded retail lineup response");
        AssertEqual(0, paddedResponse.Code,
            "BossInshot zero-padded retail robot lineup succeeds");
        AssertEqual(1, paddedResponse.FightData.RoleData.Single().NpcData.Count,
            "BossInshot padded retail lineup expands one robot");
    }

    private static void ValidateBossInshotBoundedRankQuery(
        BossInshotActivityTable activity,
        int characterConfigId,
        int characterId,
        RecordingMongoCollectionProxy<BossInshotRankEntry> rankCollection)
    {
        const long playerId = 46_700;
        Player player = CreateDrawCompatibilityPlayer(playerId);
        player.BossInshot = CreateAuthorizedBossInshotState(activity);
        using LoopbackSessionHarness harness = new(
            CreateDrawCompatibilityCharacter(playerId),
            player,
            sessionId: "v46-boss-rank-bounded");

        int bossId = activity.TowerBossIds[0];
        string ownId = BossInshotRankEntry.BuildId(activity.Id, bossId, characterId, playerId);
        List<BossInshotRankEntry> leaders = Enumerable.Range(0, 150)
            .Select(index => new BossInshotRankEntry
            {
                Id = index == 0
                    ? ownId
                    : BossInshotRankEntry.BuildId(activity.Id, bossId, characterId, playerId + index),
                ActivityId = activity.Id,
                BossId = bossId,
                CharacterId = characterId,
                PlayerId = playerId + index,
                Name = $"rank-{index}",
                TowerId = 9 - index / 20,
                Score = 1_000_000 - index,
                AchievedAt = 1_000 + index
            })
            .ToList();
        rankCollection.FindResults = leaders;
        rankCollection.CountDocumentsResults.Enqueue(100_000_000L);
        rankCollection.CountDocumentsResults.Enqueue(42L);

        const int packetId = 46_701;
        InvokeRegisteredRequestHandler(
            nameof(BossInshotTowerQueryRankRequest),
            harness.Session,
            packetId,
            new BossInshotTowerQueryRankRequest
            {
                CharacterCfgId = characterConfigId,
                BossId = bossId
            });
        BossInshotTowerQueryRankResponse response = ReadResponsePayload<BossInshotTowerQueryRankResponse>(
            harness,
            packetId,
            nameof(BossInshotTowerQueryRankResponse),
            "BossInshot bounded materialized rank response");
        AssertEqual(0, response.Code, "BossInshot bounded rank Code");
        AssertEqual(100_000_000, response.TotalCount, "BossInshot rank preserves 100M participant count");
        AssertEqual(43, response.Rank, "BossInshot self rank derives from indexed better-row count");
        AssertEqual(100, response.RankPlayerInfos.Count, "BossInshot leaderboard response is capped at 100 rows");
        AssertEqual(100, rankCollection.LastFindLimit ?? -1, "BossInshot leaderboard query applies server-side limit");
        AssertEqual(true, rankCollection.LastFindHadSort, "BossInshot leaderboard query applies server-side sort");
        string renderedSort = rankCollection.LastFindSort?.ToJson() ?? string.Empty;
        if (!renderedSort.Contains("tower_id", StringComparison.Ordinal)
            || !renderedSort.Contains("score", StringComparison.Ordinal)
            || !renderedSort.Contains("achieved_at", StringComparison.Ordinal)
            || !renderedSort.Contains("player_id", StringComparison.Ordinal))
            throw new InvalidDataException($"BossInshot rank sort is incomplete: {renderedSort}");
        AssertEqual(2, rankCollection.CountDocumentsCalls, "BossInshot rank uses bounded total/self count queries");
    }

    private static void ValidateBossInshotDuplicateNormalizationAndStableDraw(
        BossInshotActivityTable activity,
        int characterId,
        Type bossModule)
    {
        BossInshotTowerTable towerConfig = TableReaderV2.Parse<BossInshotTowerTable>()
            .First(row => row.Stages.Count > 0);
        int bossId = TableReaderV2.Parse<BossInshotTowerStageTable>()
            .Single(row => row.StageId == towerConfig.Stages[0]).BossId;
        Player player = CreateDrawCompatibilityPlayer(46_710);
        player.BossInshot = CreateAuthorizedBossInshotState(activity);
        player.BossInshot.Towers =
        [
            new BossInshotTowerState
            {
                TowerId = towerConfig.Id,
                DrawStageIds = [towerConfig.Stages[0]],
                Scores =
                [
                    new BossInshotTowerScoreState
                    {
                        BossId = bossId,
                        CharacterId = characterId,
                        MaxScore = 200,
                        AchievedAt = 30
                    }
                ]
            },
            new BossInshotTowerState
            {
                TowerId = towerConfig.Id,
                DrawStageIds = [towerConfig.Stages[0]],
                Scores =
                [
                    new BossInshotTowerScoreState
                    {
                        BossId = bossId,
                        CharacterId = characterId,
                        MaxScore = 100,
                        AchievedAt = 10
                    },
                    new BossInshotTowerScoreState
                    {
                        BossId = bossId,
                        CharacterId = characterId,
                        MaxScore = 200,
                        AchievedAt = 25
                    }
                ]
            }
        ];

        NotifyBossInshotData notify = InvokePrivateStatic<NotifyBossInshotData>(
            bossModule,
            "BuildNotifyBossInshotData",
            player);
        AssertEqual(1, player.BossInshot.Towers.Count, "BossInshot duplicate tower BSON rows normalize");
        BossInshotTowerScoreState normalized = player.BossInshot.Towers.Single().Scores.Single();
        AssertEqual(200, normalized.MaxScore, "BossInshot duplicate score rows retain maximum");
        AssertEqual(25L, normalized.AchievedAt, "BossInshot duplicate score ties retain earliest timestamp");
        AssertEqual(200,
            notify.BossInshotData.TowerDataDict[towerConfig.Id]
                .BossMaxScoreDict[bossId]
                .CharacterScoreDict[characterId],
            "BossInshot normalized score projects without duplicate-key failure");

        BossInshotTowerTable randomTower = TableReaderV2.Parse<BossInshotTowerTable>()
            .First(row => row.SelectNum > 0 && row.Stages.Distinct().Count() > row.SelectNum);
        BossInshotTowerState first = InvokePrivateStatic<BossInshotTowerState>(
            bossModule,
            "InitializeTower",
            activity.Id,
            46_711L,
            randomTower);
        BossInshotTowerState repeat = InvokePrivateStatic<BossInshotTowerState>(
            bossModule,
            "InitializeTower",
            activity.Id,
            46_711L,
            randomTower);
        AssertIntegerList(
            first.DrawStageIds.Select(id => (long)id).ToArray(),
            repeat.DrawStageIds.Select(id => (long)id).ToArray(),
            "BossInshot tower draw is stable for identical persisted identity");
        AssertEqual(randomTower.SelectNum, first.DrawStageIds.Count, "BossInshot tower draw count derives from table");
        AssertEqual(true,
            first.DrawStageIds.All(randomTower.Stages.Contains),
            "BossInshot tower draw only selects table stages");
    }

    private static void ValidateBossInshotAtomicTowerMutations(
        BossInshotActivityTable activity,
        RecordingMongoCollectionProxy<Player> playerCollection,
        Type bossModule)
    {
        const long playerId = 46_720;
        Player player = CreateDrawCompatibilityPlayer(playerId);
        player.BossInshot = CreateAuthorizedBossInshotState(activity);
        using LoopbackSessionHarness harness = new(
            CreateDrawCompatibilityCharacter(playerId),
            player,
            sessionId: "v46-boss-tower-atomic");

        byte[] beforeEnter = player.BossInshot.ToBson();
        playerCollection.ThrowOnReplaceOne = true;
        const int failedEnterPacketId = 46_721;
        InvokeRegisteredRequestHandler(
            nameof(BossInshotEnterNextTowerRequest),
            harness.Session,
            failedEnterPacketId,
            new BossInshotEnterNextTowerRequest());
        BossInshotEnterNextTowerResponse failedEnter = ReadResponsePayload<BossInshotEnterNextTowerResponse>(
            harness,
            failedEnterPacketId,
            nameof(BossInshotEnterNextTowerResponse),
            "BossInshot failed tower entry response");
        AssertEqual(20215023, failedEnter.Code, "BossInshot failed tower entry returns protocol error");
        AssertEqual(Convert.ToHexString(beforeEnter), Convert.ToHexString(player.BossInshot.ToBson()),
            "BossInshot failed tower entry restores live state");

        playerCollection.ThrowOnReplaceOne = false;
        const int retryEnterPacketId = 46_722;
        InvokeRegisteredRequestHandler(
            nameof(BossInshotEnterNextTowerRequest),
            harness.Session,
            retryEnterPacketId,
            new BossInshotEnterNextTowerRequest());
        BossInshotEnterNextTowerResponse retryEnter = ReadResponsePayload<BossInshotEnterNextTowerResponse>(
            harness,
            retryEnterPacketId,
            nameof(BossInshotEnterNextTowerResponse),
            "BossInshot tower entry retry response");
        AssertEqual(0, retryEnter.Code, "BossInshot tower entry retry succeeds after failed save");

        BossInshotTowerTable selectable = TableReaderV2.Parse<BossInshotTowerTable>()
            .First(row => row.Type == 2 && row.Stages.Count > 0);
        player.BossInshot = CreateAuthorizedBossInshotState(activity);
        player.BossInshot.CurrentTowerId = selectable.Id;
        player.BossInshot.Towers =
        [
            new BossInshotTowerState
            {
                TowerId = selectable.Id,
                DrawStageIds = selectable.Stages.Take(selectable.SelectNum).ToList()
            }
        ];
        int selectedStageId = player.BossInshot.Towers[0].DrawStageIds[0];
        byte[] beforeSelect = player.BossInshot.ToBson();
        playerCollection.ThrowOnReplaceOne = true;
        int failedSelect = InvokePrivateStatic<int>(
            bossModule,
            "SelectCore",
            harness.Session,
            selectable.Id,
            selectedStageId,
            false);
        AssertEqual(20215023, failedSelect, "BossInshot failed tower selection returns protocol error");
        AssertEqual(Convert.ToHexString(beforeSelect), Convert.ToHexString(player.BossInshot.ToBson()),
            "BossInshot failed tower selection restores live state");

        playerCollection.ThrowOnReplaceOne = false;
        int retrySelect = InvokePrivateStatic<int>(
            bossModule,
            "SelectCore",
            harness.Session,
            selectable.Id,
            selectedStageId,
            false);
        AssertEqual(0, retrySelect, "BossInshot tower selection retry succeeds after failed save");
        AssertEqual(selectedStageId,
            player.BossInshot.Towers.Single(row => row.TowerId == selectable.Id).SelectStageId,
            "BossInshot successful selection publishes persisted candidate");
    }

    private static void ValidateBossInshotPassedTowerReplay(
        BossInshotActivityTable activity,
        int characterConfigId,
        int characterId,
        Type bossModule)
    {
        BossInshotTowerTable towerConfig = TableReaderV2.Parse<BossInshotTowerTable>()
            .First(row => row.FailReBackToId >= 0 && row.ProtectCount > 0 && row.PassScore > 0);
        int stageId = towerConfig.Stages[0];
        Player player = CreateDrawCompatibilityPlayer(46_730);
        player.BossInshot = CreateAuthorizedBossInshotState(activity);
        player.BossInshot.CurrentTowerId = towerConfig.Id;
        player.BossInshot.Towers =
        [
            new BossInshotTowerState
            {
                TowerId = towerConfig.Id,
                DrawStageIds = [stageId],
                SelectStageId = stageId,
                IsPass = true
            }
        ];
        using LoopbackSessionHarness harness = new(
            CreateDrawCompatibilityCharacter(46_730),
            player,
            sessionId: "v46-boss-passed-replay");

        for (uint attempt = 1; attempt <= 2; attempt++)
        {
            uint fightId = 46_730 + attempt;
            harness.Session.PendingBossInshotFight = new PendingBossInshotFight
            {
                ActivityId = activity.Id,
                TowerId = towerConfig.Id,
                StageId = checked((uint)stageId),
                FightId = fightId,
                CharacterConfigId = characterConfigId,
                CharacterId = characterId,
                IsTower = true
            };
            object?[] settleArgs =
            [
                harness.Session,
                new FightSettleResult
                {
                    StageId = checked((uint)stageId),
                    FightId = fightId,
                    IsWin = true,
                    IntToIntRecord = new Dictionary<int, int>(),
                    LeftTime = 0
                },
                null
            ];
            AssertEqual(true,
                InvokePrivateStaticWithArgs<bool>(bossModule, "TrySettle", settleArgs),
                $"BossInshot passed tower replay {attempt} is claimed");
            AssertEqual(0,
                ((FightSettleResponse)settleArgs[2]!).Code,
                $"BossInshot passed tower replay {attempt} succeeds");
            AssertEqual(towerConfig.Id, player.BossInshot.CurrentTowerId,
                $"BossInshot passed tower replay {attempt} does not fall back current tower");
            AssertEqual(0,
                player.BossInshot.Towers.Single().TriggerProtectCount,
                $"BossInshot passed tower replay {attempt} does not consume protection");
        }
    }

    private static void ValidateBossInshotHandlerRetry(
        BossInshotActivityTable activity,
        int characterConfigId,
        int characterId,
        RecordingMongoCollectionProxy<Player> playerCollection)
    {
        BossInshotStageTable stage = TableReaderV2.Parse<BossInshotStageTable>()
            .First(row => row.StageId != activity.TeachStageId
                && activity.BossIds.Contains(row.BossId)
                && row.UnlockConditionId is null);
        BossInshotScoreTable scoreRow = TableReaderV2.Parse<BossInshotScoreTable>()
            .First(row => row.Type == 1 && row.Score is > 0);
        const long playerId = 46_740;
        const uint fightId = 46_741;
        Player player = CreateDrawCompatibilityPlayer(playerId);
        player.BossInshot = CreateAuthorizedBossInshotState(activity);
        using LoopbackSessionHarness harness = new(
            CreateDrawCompatibilityCharacter(playerId),
            player,
            sessionId: "v46-boss-handler-retry");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(playerId);
        PreFightRequest preFight = new()
        {
            PreFightData = new PreFightRequest.PreFightRequestPreFightData
            {
                StageId = checked((uint)stage.StageId),
                ChallengeCount = 1
            }
        };
        harness.Session.fight = new Fight(preFight, fightId);
        harness.Session.PendingBossInshotFight = new PendingBossInshotFight
        {
            ActivityId = activity.Id,
            StageId = checked((uint)stage.StageId),
            FightId = fightId,
            CharacterConfigId = characterConfigId,
            CharacterId = characterId,
            IsTower = false
        };
        FightSettleRequest request = new()
        {
            Result = new FightSettleResult
            {
                StageId = checked((uint)stage.StageId),
                FightId = fightId,
                IsWin = true,
                IntToIntRecord = new Dictionary<int, int> { [scoreRow.Id] = 1 },
                LeftTime = 0
            }
        };

        playerCollection.ThrowOnReplaceOne = true;
        const int failedPacketId = 46_742;
        InvokeRegisteredRequestHandler(nameof(FightSettleRequest), harness.Session, failedPacketId, request);
        FightSettleResponse failed = ReadResponsePayload<FightSettleResponse>(
            harness,
            failedPacketId,
            nameof(FightSettleResponse),
            "BossInshot handler persistence-failure response");
        AssertEqual(20215023, failed.Code, "BossInshot handler reports retryable persistence failure");
        AssertEqual(true, harness.Session.PendingBossInshotFight is not null,
            "BossInshot handler keeps pending context after persistence failure");
        AssertEqual(true, harness.Session.fight is not null,
            "BossInshot handler keeps authorization context after persistence failure");

        playerCollection.ThrowOnReplaceOne = false;
        const int retryPacketId = 46_743;
        InvokeRegisteredRequestHandler(nameof(FightSettleRequest), harness.Session, retryPacketId, request);
        string[] retryPushNames =
        [
            nameof(NotifyBossInshotData),
            nameof(NotifyArchiveMonsterRecord),
            nameof(NotifyTask)
        ];
        foreach (string expectedPushName in retryPushNames)
        {
            Packet pushPacket = harness.ReadPacket(
                $"BossInshot handler persistence retry {expectedPushName}");
            AssertEqual(Packet.ContentType.Push, pushPacket.Type,
                $"BossInshot handler persistence retry {expectedPushName} packet type");
            Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(pushPacket.Content);
            AssertEqual(expectedPushName, push.Name,
                $"BossInshot handler persistence retry {expectedPushName} packet name");
        }
        FightSettleResponse retry = ReadResponsePayload<FightSettleResponse>(
            harness,
            retryPacketId,
            nameof(FightSettleResponse),
            "BossInshot handler persistence retry response");
        AssertEqual(0, retry.Code, "BossInshot handler retry reaches settlement and succeeds");
        AssertEqual(true, harness.Session.PendingBossInshotFight is null,
            "BossInshot successful handler retry consumes pending context");
        AssertEqual(true, harness.Session.fight is null,
            "BossInshot successful handler retry consumes authorization context");
    }

    private static BossInshotState CreateAuthorizedBossInshotState(BossInshotActivityTable activity)
    {
        BossInshotState state = new()
        {
            ActivityId = activity.Id,
            AuthorizedActivityId = activity.Id,
            AuthorizedTimeIds = TableReaderV2.Parse<BossInshotTowerTable>()
                .Select(row => row.TimeId)
                .Where(id => id > 0)
                .Append(activity.TimeId!.Value)
                .Distinct()
                .ToList(),
            IsPassTeach = true,
            RankProjectionVersion = 2
        };
        ConditionTable condition = TableReaderV2.Parse<ConditionTable>()
            .Single(row => row.Id == activity.TowerConditions!.Value);
        if (condition.Type != 15306 || condition.Params.Count < 2)
            throw new InvalidDataException($"BossInshot tower condition {condition.Id} is not a supported score threshold.");
        state.PassStages.Add(new BossInshotPassStage
        {
            StageId = condition.Params[0],
            MaxScore = condition.Params[1]
        });
        return state;
    }
}
