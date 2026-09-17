using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.fuben.simulatetrain;
using AscNet.GameServer.Handlers;
using AscNet.GameServer.Game;
using MessagePack;
using Newtonsoft.Json.Linq;
using System.Reflection;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidateSimulateTrainCompatibility()
    {
        List<SimulateTrainMonsterTable> bosses = TableReaderV2.Parse<SimulateTrainMonsterTable>();
        AssertEqual(true, bosses.Count > 0, "SimulateTrain monster table is not empty");
        AssertEqual(bosses.Count, bosses.Select(boss => boss.Id).Distinct().Count(),
            "SimulateTrain boss IDs are unique");
        AssertEqual(bosses.Count, bosses.Select(boss => boss.StageId).Distinct().Count(),
            "SimulateTrain stage IDs are unique");
        SimulateTrainMonsterTable ronin = bosses.Single(boss => boss.Id == 2_001);
        AssertEqual(true, ronin.NpcLevel.SequenceEqual([170, 205, 250]),
            "SimulateTrain Ronin difficulty levels");
        AssertEqual(true, ronin.StageBuffId.SequenceEqual([750_976, 750_977, 750_978]),
            "SimulateTrain Ronin difficulty buffs");
        AssertEqual(201, ronin.TimeId, "SimulateTrain Ronin permanent TimeId");
        AssertEqual(0, ronin.ImpasseTimeId, "SimulateTrain Ronin ImpasseTimeId");
        DateTimeOffset permanentPracticeAt = DateTimeOffset.UnixEpoch;
        AssertEqual(true, ActivityScheduleService.IsOpen(ronin.TimeId, permanentPracticeAt),
            "SimulateTrain permanent practice schedule is open");
        List<SimulateTrainPeriodBuffTable> periodBuffs =
            TableReaderV2.Parse<SimulateTrainPeriodBuffTable>();
        AssertEqual(22, periodBuffs.Count, "SimulateTrain period buff row count");

        PreFightRequest request = new()
        {
            PreFightData = new()
            {
                StageId = 30_161_351,
                SimulateTrainInfo = new()
                {
                    BossId = 2_001,
                    Period = 1,
                    AtkLevel = 2,
                    HpLevel = 1,
                    Difficulty = 3,
                    DangerCoefficient = 150,
                }
            }
        };
        request = MessagePackSerializer.Deserialize<PreFightRequest>(
            MessagePackSerializer.Serialize(request));
        AssertEqual(2_001, request.PreFightData.SimulateTrainInfo?.BossId ?? 0,
            "PreFightRequest preserves SimulateTrainInfo");

        Type simulateTrainModule = RequiredAscNetGameServerType(
            "AscNet.GameServer.Handlers.SimulateTrainModule");
        MethodInfo applyPreFight = RequiredMethod(
            simulateTrainModule,
            "TryApplyPreFight",
            BindingFlags.Static | BindingFlags.Public,
            [
                typeof(PreFightRequest.PreFightRequestPreFightData),
                typeof(PreFightResponse.PreFightResponseFightData),
                typeof(DateTimeOffset),
                typeof(int).MakeByRefType()
            ]);
        PreFightResponse.PreFightResponseFightData fightData = new() { StageId = request.PreFightData.StageId };
        object?[] preFightArguments = [request.PreFightData, fightData, permanentPracticeAt, 0];
        AssertEqual(true, (bool)(applyPreFight.Invoke(null, preFightArguments) ?? false),
            "SimulateTrain pre-fight is recognized");
        AssertEqual(0, (int)(preFightArguments[3] ?? -1),
            "SimulateTrain pre-fight is accepted");
        AssertEqual<List<int>?>(null, fightData.MonsterLevel,
            "SimulateTrain uses explicit NPC group instead of top-level monster levels");
        AssertEqual(0, fightData.EventIds.Count,
            "SimulateTrain uses NPC buffers instead of top-level fight events");
        AssertEqual(true, fightData.NormalEventIds.Cast<int>().SequenceEqual([2]),
            "SimulateTrain applies the normal stage event");
        if (fightData.NpcGroupList is not List<SimulateTrainNpcGroupData> roninGroups)
            throw new InvalidDataException("SimulateTrain pre-fight NpcGroupList had the wrong runtime type.");
        SimulateTrainNpcData roninNpc = roninGroups.Single().NpcList.Single();
        AssertEqual(90_250, roninNpc.NpcId,
            "SimulateTrain hard Ronin NPC");
        AssertEqual(250, roninNpc.Level,
            "SimulateTrain hard Ronin level");
        AssertEqual(true, roninNpc.BufferIds.SequenceEqual(
                [750_978, 750_962, 750_955]),
            "SimulateTrain hard Ronin NPC buffers");

        PreFightRequest officialCaptureRequest = new()
        {
            PreFightData = new()
            {
                StageId = 30_161_303,
                SimulateTrainInfo = new()
                {
                    BossId = 3_021,
                    Period = 2,
                    AtkLevel = 2,
                    HpLevel = 1,
                    Difficulty = 1,
                    DangerCoefficient = 3_000,
                }
            }
        };
        PreFightResponse.PreFightResponseFightData officialCaptureFightData =
            new() { StageId = officialCaptureRequest.PreFightData.StageId };
        object?[] officialCaptureArguments =
            [officialCaptureRequest.PreFightData, officialCaptureFightData, permanentPracticeAt, 0];
        AssertEqual(true, (bool)(applyPreFight.Invoke(null, officialCaptureArguments) ?? false),
            "Official captured SimulateTrain pre-fight is recognized");
        AssertEqual(0, (int)(officialCaptureArguments[3] ?? -1),
            "Official captured SimulateTrain pre-fight is accepted");
        JObject officialWire = JObject.Parse(MessagePackSerializer.ConvertToJson(
            MessagePackSerializer.Serialize(new PreFightResponse
            {
                Code = 0,
                FightData = officialCaptureFightData,
            })));
        JObject officialWireFightData = (JObject)officialWire["FightData"]!;
        AssertEqual(JTokenType.Null, officialWireFightData["MonsterLevel"]!.Type,
            "Official captured SimulateTrain wire MonsterLevel");
        AssertEqual(true, ((JArray)officialWireFightData["EventIds"]!).Count == 0,
            "Official captured SimulateTrain wire EventIds");
        AssertEqual(true, ((JArray)officialWireFightData["NormalEventIds"]!)
                .Values<int>().SequenceEqual([2]),
            "Official captured SimulateTrain wire NormalEventIds");
        JObject officialWireNpc = (JObject)officialWireFightData["NpcGroupList"]![0]!["NpcList"]![0]!;
        AssertEqual(837_000, officialWireNpc["NpcId"]!.Value<int>(),
            "Official captured SimulateTrain wire NPC");
        AssertEqual(400, officialWireNpc["Level"]!.Value<int>(),
            "Official captured SimulateTrain wire NPC level");
        AssertEqual(true, ((JArray)officialWireNpc["BufferIds"]!).Values<int>().SequenceEqual(
                [750_976, 750_953, 750_962, 750_955]),
            "Official captured SimulateTrain wire NPC buffers");

        request.PreFightData.SimulateTrainInfo!.Period = 2;
        object?[] unsupportedPeriodArguments =
        [
            request.PreFightData,
            new PreFightResponse.PreFightResponseFightData { StageId = request.PreFightData.StageId },
            permanentPracticeAt,
            0
        ];
        AssertEqual(true, (bool)(applyPreFight.Invoke(null, unsupportedPeriodArguments) ?? false),
            "SimulateTrain unsupported period is recognized");
        AssertEqual(true, (int)(unsupportedPeriodArguments[3] ?? 0) != 0,
            "SimulateTrain unsupported period is rejected");
        request.PreFightData.SimulateTrainInfo.Period = 1;

        int PreFightCodeAt(SimulateTrainMonsterTable boss, DateTimeOffset now, int difficulty = 1)
        {
            PreFightRequest preFight = new()
            {
                PreFightData = new()
                {
                    StageId = checked((uint)boss.StageId),
                    SimulateTrainInfo = new()
                    {
                        BossId = boss.Id,
                        Period = 1,
                        AtkLevel = 2,
                        HpLevel = 1,
                        Difficulty = difficulty,
                    }
                }
            };
            object?[] arguments =
            [
                preFight.PreFightData,
                new PreFightResponse.PreFightResponseFightData { StageId = preFight.PreFightData.StageId },
                now,
                0
            ];
            AssertEqual(true, (bool)(applyPreFight.Invoke(null, arguments) ?? false),
                $"SimulateTrain boss {boss.Id} schedule check is recognized");
            return (int)(arguments[3] ?? -1);
        }

        // Rotation TimeIds are permanent, so both difficulties must be enterable at every probe.
        DateTimeOffset[] probes = [DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow];
        foreach (SimulateTrainMonsterTable boss in bosses)
        {
            foreach (DateTimeOffset probe in probes)
            {
                AssertEqual(true, ActivityScheduleService.IsOpen(boss.TimeId, probe),
                    $"SimulateTrain boss {boss.Id} base schedule is open at {probe:O}");
                AssertEqual(0, PreFightCodeAt(boss, probe),
                    $"SimulateTrain boss {boss.Id} base pre-fight at {probe:O}");
                if (boss.ImpasseTimeId <= 0)
                    continue;
                AssertEqual(true, ActivityScheduleService.IsOpen(boss.ImpasseTimeId, probe),
                    $"SimulateTrain boss {boss.Id} Impasse schedule is open at {probe:O}");
                AssertEqual(0, PreFightCodeAt(boss, probe, boss.NpcId.Count),
                    $"SimulateTrain boss {boss.Id} Impasse pre-fight at {probe:O}");
            }
        }

        SimulateTrainMonsterTable cthylla = bosses.Single(boss => boss.Id == 3_052);
        AssertEqual(30_308, cthylla.TimeId, "SimulateTrain Cthylla rotation TimeId");

        int[] rotationTimeIds = bosses
            .SelectMany(boss => new[] { boss.TimeId, boss.ImpasseTimeId })
            .Where(timeId => timeId > 0 && timeId != 201)
            .Distinct()
            .Order()
            .ToArray();
        AssertEqual(true, rotationTimeIds.Length > 0, "SimulateTrain publishes rotation TimeIds");
        foreach (int timeId in rotationTimeIds)
        {
            ActivityScheduleEntry rotation = ActivityScheduleService.All.Single(row => row.Id == timeId);
            AssertEqual<(long, long)>((0, 0), (rotation.StartTime, rotation.EndTime),
                $"SimulateTrain TimeId {timeId} is a permanent window");
            AssertEqual(true, rotation.Source.Contains("SimulateTrainMonster"),
                $"SimulateTrain TimeId {timeId} is derived from the monster table");
        }

        AssertEqual(1, ActivityScheduleService.All.Count(row => row.Id == 201),
            "SimulateTrain permanent practice TimeId resolves exactly once");
        AssertEqual(true, ActivityScheduleService.All.Single(row => row.Id == 201).Source
                .Contains("SimulateTrain:permanent-practice"),
            "SimulateTrain permanent practice keeps its authored schedule provenance");
        AssertEqual(1, ActivityScheduleService.All.Count(row => row.Id == 47_121),
            "Authored activity TimeId 47121 resolves exactly once");

        request.PreFightData.SimulateTrainInfo!.BossId = 9_999;
        object?[] invalidPreFightArguments =
        [
            request.PreFightData,
            new PreFightResponse.PreFightResponseFightData { StageId = request.PreFightData.StageId },
            permanentPracticeAt,
            0
        ];
        AssertEqual(true, (bool)(applyPreFight.Invoke(null, invalidPreFightArguments) ?? false),
            "SimulateTrain invalid pre-fight is recognized");
        AssertEqual(true, (int)(invalidPreFightArguments[3] ?? 0) != 0,
            "SimulateTrain mismatched boss is rejected");
        request.PreFightData.SimulateTrainInfo.BossId = 2_001;

        MethodInfo buildFightResult = RequiredMethod(
            simulateTrainModule,
            "BuildFightResult",
            BindingFlags.Static | BindingFlags.Public,
            [typeof(PreFightRequest.PreFightRequestPreFightData), typeof(FightSettleResult)]);
        SimulateTrainFightResultData result = (SimulateTrainFightResultData)(buildFightResult.Invoke(
            null,
            [
                request.PreFightData,
                new FightSettleResult
                {
                    StageId = request.PreFightData.StageId,
                    StartFrame = 100,
                    SettleFrame = 500,
                    PauseFrame = 40,
                }
            ]) ?? throw new InvalidDataException("SimulateTrainModule.BuildFightResult returned nil."));
        AssertEqual(3, result.Difficulty, "SimulateTrain settle difficulty");
        AssertEqual(2, result.AtkLevel, "SimulateTrain settle attack level");
        AssertEqual(1, result.HpLevel, "SimulateTrain settle health level");
        AssertEqual(18L, result.FightTime, "SimulateTrain settle fight time");


        Type accountModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule");
        MethodInfo buildArchive = RequiredMethod(
            accountModule,
            "BuildNotifyArchiveLoginData",
            BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(Player)]);
        HashSet<uint> visibilityNpcIds = bosses
            .Select(boss => checked((uint)boss.NpcId.First()))
            .ToHashSet();
        Player freshPlayer = CreateDrawCompatibilityPlayer(88_301);
        NotifyArchiveLoginData freshPayload = (NotifyArchiveLoginData)(buildArchive.Invoke(
            null,
            [freshPlayer])
            ?? throw new InvalidDataException("AccountModule.BuildNotifyArchiveLoginData returned nil."));
        AssertEqual(true, freshPayload.MonsterUnlockIds.Order().SequenceEqual(
                bosses.Select(boss => checked((uint)boss.Id)).Order()),
            "SimulateTrain archive unlock IDs match the configured catalog");
        AssertEqual(true, freshPayload.Monsters.Select(monster => monster.Id).ToHashSet()
                .SetEquals(visibilityNpcIds),
            "Fresh SimulateTrain archive receives one visibility record per boss");
        AssertEqual(true, freshPayload.Monsters.All(monster => monster.Killed == 0),
            "Fresh SimulateTrain visibility records do not fabricate kills");

        Player archivePlayer = CreateDrawCompatibilityPlayer(88_302);
        archivePlayer.ArchiveMonsterKills = new() { [90_250] = 3, [99_999] = 0 };
        NotifyArchiveLoginData payload = (NotifyArchiveLoginData)(buildArchive.Invoke(
            null,
            [archivePlayer])
            ?? throw new InvalidDataException("AccountModule.BuildNotifyArchiveLoginData returned nil."));
        AssertEqual(1, payload.Monsters.Count(monster => monster.Killed > 0),
            "SimulateTrain archive exposes only persisted positive kill counts");
        AssertEqual(3, payload.Monsters.Single(monster => monster.Id == 90_250).Killed,
            "SimulateTrain archive login preserves the persisted kill count");

        MethodInfo recordArchiveKill = RequiredMethod(
            simulateTrainModule,
            "RecordArchiveKill",
            BindingFlags.Static | BindingFlags.Public,
            [typeof(Player), typeof(PreFightRequest.PreFightRequestPreFightData), typeof(FightSettleResult)]);
        NotifyArchiveMonsterRecord archiveRecord = (NotifyArchiveMonsterRecord)(recordArchiveKill.Invoke(
            null,
            [
                archivePlayer,
                request.PreFightData,
                new FightSettleResult { IsWin = true, StageId = request.PreFightData.StageId }
            ]) ?? throw new InvalidDataException("SimulateTrainModule.RecordArchiveKill returned nil."));
        AssertEqual(4, archivePlayer.ArchiveMonsterKills[90_250],
            "SimulateTrain clear persists the real boss kill");
        AssertEqual(4U, archiveRecord.Monsters.Single().Killed,
            "SimulateTrain clear pushes the persisted boss kill count");
    }
}
