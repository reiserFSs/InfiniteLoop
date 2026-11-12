using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.fuben.arena;
using AscNet.Table.V2.share.fuben.bosssingle;
using AscNet.Table.V2.share.task;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace AscNet.Test;

internal static partial class Program
{
    private static void ValidateModeScoreTaskProgress()
    {
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForShopCompatibility();
        const long uid = 99_882;
        Player player = CreateDrawCompatibilityPlayer(uid);
        player.PlayerData.Level = 43;
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(uid), player,
            CreateDrawCompatibilityInventory(uid, []), "mode-score-tasks");
        Session session = harness.Session;
        session.stage = CreateLoginAccountCompatibilityStage(uid);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        ArenaModule.ReconcileLogin(session, now);
        TaskModule.EnsureMissionResets(session, now);

        CurrentTaskTable[] tasks = TableReaderV2.Parse<CurrentTaskTable>().ToArray();
        var tasksById = tasks.ToDictionary(row => row.Id);
        var conditionTypes = TableReaderV2.Parse<CurrentConditionTable>().ToDictionary(row => row.Id, row => row.Type);
        ChallengeAreaTable[] challenges = TableReaderV2.Parse<ChallengeAreaTable>().ToArray();
        var rewardRows = TableReaderV2.Parse<CurrentRewardTable>().ToDictionary(row => row.Id);
        var rewardGoodsById = TableReaderV2.Parse<CurrentRewardGoodsTable>().ToDictionary(row => row.Id);
        foreach (int id in challenges.SelectMany(row => row.TaskId).Where(id => id > 0).Distinct())
        {
            if (!tasksById.TryGetValue(id, out CurrentTaskTable? task))
                throw new InvalidDataException($"War Zone challenge references missing task {id}.");
            AssertEqual(10, task.Type, $"War Zone task {id} type");
            AssertEqual(true, conditionTypes.GetValueOrDefault(task.Condition) is 28001 or 28003,
                $"War Zone task {id} uses a supported condition");
            AssertEqual(true, rewardRows.TryGetValue(task.RewardId, out var reward)
                && reward.SubIds.Count > 0 && reward.SubIds.All(rewardGoodsById.ContainsKey),
                $"War Zone task {id} has complete rewards");
        }
        HashSet<int> activeArenaIds = ArenaModule.CurrentTaskIds(player).ToHashSet();
        CurrentTaskTable[] arenaScoreTasks = tasks.Where(row => activeArenaIds.Contains(row.Id)
            && conditionTypes.GetValueOrDefault(row.Condition) == 28003).ToArray();
        CurrentTaskTable arenaTask = arenaScoreTasks.Single(row => row.Result == 200_000);
        CurrentTaskTable otherArenaTask = tasks.First(row => row.Type == 10 && !activeArenaIds.Contains(row.Id)
            && conditionTypes.GetValueOrDefault(row.Condition) == 28003);
        CurrentTaskTable[] weeklyBossTasks = tasks.Where(row => row.Type == 3
            && string.IsNullOrWhiteSpace(row.EndTime) && conditionTypes.GetValueOrDefault(row.Condition) == 25006).ToArray();
        AssertEqual(2, weeklyBossTasks.Length, "current weekly Pain Cage score milestones");

        SimulatedBattlefieldState score = player.SimulatedBattlefield;
        score.ArenaJoined = true;
        score.ArenaJoinActivity = score.ArenaActivityNo;
        score.ArenaAreaMaxPoints = new() { [15] = 246_644, [27] = 50_000 };
        score.ArenaPoint = 296_644;
        player.MissionProgress.ConditionCounters[arenaTask.Condition] = 246_644;
        score.BossActivityNo = (int)TaskModule.CurrentWeeklyResetPeriod(now);
        score.BossStageRecords = [new() { Score = 119_999, MaxScore = 250_000 }];
        score.BossCurrentTotalScore = 119_999;
        score.BossTotalScore = 250_000;
        session.player = BsonSerializer.Deserialize<Player>(player.ToBson());
        var login = TaskModule.BuildLoginTasks(session).ToDictionary(row => (int)row.Id);
        foreach (CurrentTaskTable task in arenaScoreTasks)
        {
            if (!login.TryGetValue(task.Id, out var arenaProgress))
                throw new InvalidDataException($"Login omitted War Zone score task {task.Id} with saved progress.");
            AssertEqual(296_644, arenaProgress.Schedule.Single().Value, $"login uses the current War Zone aggregate for task {task.Id}");
            AssertEqual(task.Result <= 296_644 ? 3 : 1, arenaProgress.State, $"War Zone task {task.Id} achievement state");
        }
        AssertEqual(false, login.ContainsKey(otherArenaTask.Id), "login omits another tier's War Zone score task");
        foreach (CurrentTaskTable task in weeklyBossTasks)
        {
            var progress = login[task.Id];
            AssertEqual(Math.Min(250_000, task.Result), progress.Schedule.Single().Value, "weekly task uses the saved Pain Cage best total");
            AssertEqual(task.Result <= 250_000 ? 3 : 1, progress.State, "weekly Pain Cage task achievement state");
        }

        int[] selected = [..arenaScoreTasks.Select(row => row.Id), otherArenaTask.Id, ..weeklyBossTasks.Select(row => row.Id)];
        const int claimPacketId = 81_901;
        InvokeRegisteredRequestHandler(nameof(FinishTaskRequest), session, claimPacketId,
            new FinishTaskRequest { TaskId = arenaTask.Id });
        FinishTaskResponse claim = ReadResponsePayload<FinishTaskResponse>(harness, claimPacketId,
            nameof(FinishTaskResponse), "War Zone 200,000 score reward", maxPacketsToRead: 32);
        AssertEqual(0, claim.Code, "War Zone 200,000 score milestone is claimable");
        var expectedGoods = rewardRows[arenaTask.RewardId].SubIds.Select(id => rewardGoodsById[id]).ToList();
        AssertEqual(expectedGoods.Count, claim.RewardGoodsList.Count, "War Zone milestone reward goods count");
        foreach (var expected in expectedGoods)
        {
            RewardGoods actual = claim.RewardGoodsList.Single(good => good.TemplateId == expected.TemplateId);
            AssertEqual(expected.Count, actual.Count, "War Zone milestone grants the authored reward quantity");
        }

        score = session.player.SimulatedBattlefield;
        score.ArenaAreaMaxPoints[27] = 75_000;
        score.ArenaPoint = 321_644;
        score.BossStageRecords[0].MaxScore = 450_000;
        score.BossTotalScore = 450_000;
        var updated = SyncScores(selected);
        AssertEqual(321_644, updated[arenaTask.Id], "live sync uses the updated War Zone aggregate");
        AssertEqual(0, updated[otherArenaTask.Id], "live sync excludes another tier's score");
        foreach (CurrentTaskTable task in weeklyBossTasks)
            AssertEqual(task.Result, updated[task.Id], "live sync advances the weekly Pain Cage score");

        score.ArenaActivityNo--;
        score.ArenaJoinActivity--;
        score.BossActivityNo--;
        AssertEqual(true, SyncScores(selected).Values.All(value => value == 0), "expired rotation scores do not advance current tasks");

        score.ArenaActivityNo++;
        score.ArenaJoinActivity++;
        foreach (ChallengeAreaTable challenge in challenges)
        {
            session.player.PlayerData.Level = challenge.MinLv;
            score.ArenaLevel = challenge.ArenaLv;
            score.ArenaChallengeId = challenge.ChallengeId;
            int[] ids = challenge.TaskId.Where(id => id > 0
                && conditionTypes[tasksById[id].Condition] == 28003).ToArray();
            var progress = SyncScores(ids);
            foreach (int id in ids)
                AssertEqual(321_644, progress[id], $"War Zone challenge {challenge.ChallengeId} score task {id}");
        }

        score.BossActivityNo++;
        int[] bossIds = weeklyBossTasks.Select(row => row.Id).ToArray();
        foreach (BossSingleGradeTable grade in TableReaderV2.Parse<BossSingleGradeTable>())
        {
            session.player.PlayerData.Level = grade.MinPlayerLevel;
            score.BossLevelType = grade.LevelType;
            var progress = SyncScores(bossIds);
            foreach (CurrentTaskTable task in weeklyBossTasks)
                AssertEqual(task.Result, progress[task.Id], $"Pain Cage grade {grade.LevelType} advances weekly task {task.Id}");
        }

        Dictionary<int, int> SyncScores(int[] taskIds)
        {
            TaskModule.SendCurrentTaskBatch(session, taskIds);
            Packet packet = harness.ReadPacket("mode score task sync");
            Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
            AssertEqual(nameof(NotifyTask), push.Name, "mode score task push");
            return MessagePackSerializer.Deserialize<NotifyTask>(push.Content).Tasks.Tasks
                .ToDictionary(row => (int)row.Id, row => row.Schedule.Single().Value);
        }
    }
}
