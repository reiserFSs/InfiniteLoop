using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Game;
using AscNet.GameServer.Handlers;
using MongoDB.Bson;
using AscNet.Table.V2.share.fuben.bosssingle;
using Newtonsoft.Json.Linq;
using System.Reflection;

namespace AscNet.Test;

internal partial class Program
{
    // Pain Cage Intensive Battle: selected "Forged Stratagem" (Type 2) features add their ScoreRate (1/100 %)
    // to the total Progress; the persisted best score is the boosted one.
    private static void ValidateBossSingleChallengeScoreRate()
    {
        using MongoCollectionOverride mongoOverride = MongoCollectionOverride.InstallForBossCompatibility(
            out RecordingMongoCollectionProxy<AscNet.Common.Database.Player> _,
            out RecordingMongoCollectionProxy<AscNet.Common.Database.Stage> _);
        const long playerId = 99_702;
        const uint characterId = 1_021_001;
        AscNet.Common.Database.Player player = CreateDrawCompatibilityPlayer(playerId);
        player.SimulatedBattlefield = new() { BossRankPlatform = 2 };
        AscNet.Common.Database.Character character = CreateDrawCompatibilityCharacter(playerId);
        character.Characters.Add(CreateLoginAccountCompatibilityCharacter(characterId, 3_021_001));
        using LoopbackSessionHarness harness = new(character, player, CreateDrawCompatibilityInventory(playerId, []), "boss-score-rate-test");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(playerId);

        List<BossSingleGradeTable> grades = TableReaderV2.Parse<BossSingleGradeTable>();
        List<BossSingleGroupTable> groups = TableReaderV2.Parse<BossSingleGroupTable>();
        List<BossSingleSectionTable> sections = TableReaderV2.Parse<BossSingleSectionTable>();
        List<BossSingleStageTable> stages = TableReaderV2.Parse<BossSingleStageTable>();
        BossSingleChallengeGradeTable challengeGrade = TableReaderV2.Parse<BossSingleChallengeGradeTable>().Single();
        List<BossSingleChallengeFeatureTable> features = TableReaderV2.Parse<BossSingleChallengeFeatureTable>();
        BossSingleChallengeFeatureGroupTable group = TableReaderV2.Parse<BossSingleChallengeFeatureGroupTable>().Single(row => row.Id == 8);

        MethodInfo buildLoginData = RequiredMethod(
            RequiredAscNetGameServerType("AscNet.GameServer.Handlers.BossModule"), "BuildLoginData",
            BindingFlags.Static | BindingFlags.NonPublic, [typeof(AscNet.Common.Database.Player), typeof(long?)]);
        var state = player.SimulatedBattlefield;
        _ = buildLoginData.Invoke(null, [player, null]); // reconciles BossActivityNo
        state.BossLevelType = grades.Where(row => row.GradeType >= challengeGrade.NeedGradeType).OrderBy(row => row.GradeType).First().LevelType;
        state.BossList = groups.Single(row => row.Id == grades.Single(value => value.LevelType == state.BossLevelType).GroupId.First()).SectionId.ToList();
        state.BossStageRecords = [new() { StageId = stages.First().StageId, Score = challengeGrade.NeedScore, MaxScore = challengeGrade.NeedScore }];
        state.BossChallengeSelectedFeatureGroup = group.Id;
        state.BossTotalScore = state.BossMaxScore = state.BossCurrentTotalScore = challengeGrade.NeedScore;
        NotifyFubenBossSingleData login = (NotifyFubenBossSingleData)buildLoginData.Invoke(null, [player, null])!;
        BossSingleSectionTable section = sections.Single(row => row.Id == login.FubenBossSingleData.ChallengeSectionId
            && row.AfreshId == sections.Max(value => value.AfreshId));
        int stageId = section.StageId.First();
        BossSingleStageTable stage = stages.Single(row => row.StageId == stageId);

        const int buffGroup = 207; // group 8 pairs buff group 207 with feature 70
        List<BossSingleChallengeBuffGroupTable> rows = TableReaderV2.Parse<BossSingleChallengeBuffGroupTable>()
            .Where(row => row.BuffGroupId == buffGroup && row.Index > 0).OrderBy(row => row.Index).ToList();
        AssertEqual(true, rows.Count >= 2, "score rate fixture has two selectable rows");
        int Rate(BossSingleChallengeBuffGroupTable row) => features.Single(f => f.Id == row.Buff[0] && f.Type == 2).ScoreRate ?? 0;
        int rateA = Rate(rows[0]), rateB = Rate(rows[1]);
        AssertEqual(true, rateA > 0 && rateB > 0 && rateA != rateB, "score rate fixture rates are positive and distinct");

        int packet = 83_000;
        int Run(Dictionary<int, int>? choices)
        {
            PreFightRequest.PreFightRequestPreFightData data = new()
            {
                StageId = checked((uint)stageId), ChallengeCount = 1, CardIds = [characterId], RobotIds = [],
                BossSingleStageType = 3,
                BossSingleChallengeBuffGroup = choices is null ? buffGroup : new Dictionary<string, object>
                    { ["BuffGroupId"] = buffGroup, ["BuffChoices"] = choices }
            };
            InvokeRegisteredRequestHandler(nameof(PreFightRequest), harness.Session, ++packet, new PreFightRequest { PreFightData = data });
            PreFightResponse pre = ReadResponsePayload<PreFightResponse>(harness, packet, nameof(PreFightResponse), "score rate pre-fight");
            AssertEqual(0, pre.Code, "score rate pre-fight code");
            InvokeRegisteredRequestHandler(nameof(FightSettleRequest), harness.Session, ++packet, new FightSettleRequest
            {
                Result = new FightSettleResult
                {
                    IsWin = true, StageId = checked((uint)stageId), FightId = pre.FightData.FightId,
                    StartFrame = 1, SettleFrame = 1 + 8 * 20, LeftTime = Math.Max(0, stage.PassTimeLimit - 8),
                    NpcHpInfo = new()
                    {
                        [1] = new NpcHp { CharacterId = checked((int)characterId), Type = 1, BuffIds = [],
                            AttrTable = new() { [1] = new Dictionary<object, object> { ["Value"] = 100, ["MaxValue"] = 100 } } },
                        [2] = new NpcHp { Type = 2, BuffIds = [],
                            AttrTable = new() { [1] = new Dictionary<object, object> { ["Value"] = 0, ["MaxValue"] = 1_000_000 } } }
                    }
                }
            });
            FightSettleResponse settle = ReadResponsePayload<FightSettleResponse>(harness, packet, nameof(FightSettleResponse), "score rate settle");
            BossSingleFightResult result = settle.Settle?.BossSingleFightResult as BossSingleFightResult
                ?? JObject.FromObject(settle.Settle!.BossSingleFightResult!).ToObject<BossSingleFightResult>()!;
            state.BossChallengeHistory.RemoveAll(row => row.StageId == stageId);
            InvokeRegisteredRequestHandler(nameof(BossSingleSaveScoreRequest), harness.Session, ++packet, new BossSingleSaveScoreRequest { StageId = stageId });
            AssertEqual(0, ReadResponsePayload<BossSingleSaveScoreResponse>(harness, packet, nameof(BossSingleSaveScoreResponse), "score rate save", 64).Code, "score rate save code");
            AssertEqual(result.TotalScore, state.BossChallengeHistory.Single(row => row.StageId == stageId).Score, "score rate persists the settled total");
            AssertEqual(result.TotalScore, MongoDB.Bson.Serialization.BsonSerializer.Deserialize<AscNet.Common.Database.Player>(player.ToBsonDocument())
                .SimulatedBattlefield.BossChallengeHistory.Single(row => row.StageId == stageId).Score, "score rate best score survives BSON reload");
            return result.TotalScore;
        }

        int baseScore = Run(null);
        AssertEqual(baseScore, Run([]), "empty selection scores the plain total");
        int Expected(int rate) => checked((int)(baseScore + (long)baseScore * rate / 10000));
        AssertEqual(Expected(rateA), Run(new() { [rows[0].Index] = 1 }), "one selected feature adds its ScoreRate");
        AssertEqual(Expected(rateB), Run(new() { [rows[1].Index] = 1 }), "other selected feature adds its own ScoreRate");
        AssertEqual(Expected(rateA + rateB), Run(new() { [rows[0].Index] = 1, [rows[1].Index] = 1 }), "two selected features add their ScoreRates");
        ValidateBossSingleWeaknessModulePairing(player, buildLoginData);
    }

    // Player report (CN 4.9): Flare Burst I (Raydiance weakness) was offered with the "except Fire"
    // module and Brutal Crash III (Physical) with "except Ignition"; both come from FeatureGroup 4,
    // the only group whose pairings differ from every other group's. It must never be offered.
    private static void ValidateBossSingleWeaknessModulePairing(AscNet.Common.Database.Player player, MethodInfo buildLoginData)
    {
        Type module = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.BossModule");
        Dictionary<int, int> moduleOne = TableReaderV2.Parse<BossSingleChallengeBuffGroupTable>()
            .Where(row => row.Index == 1 && row.Buff.Count > 0)
            .GroupBy(row => row.BuffGroupId).ToDictionary(group => group.Key, group => group.First().Buff[0]);
        Dictionary<int, BossSingleChallengeFeatureGroupTable> groups = TableReaderV2.Parse<BossSingleChallengeFeatureGroupTable>()
            .ToDictionary(row => row.Id);
        // Flare Burst I (57) and Brutal Crash III (67): the module each has everywhere except group 4.
        int UsualModule(int feature) => groups.Values.Where(row => row.Id != 4)
            .SelectMany(row => row.FeatureIds.Zip(row.BuffGroupIds))
            .Where(pair => pair.First == feature && moduleOne.ContainsKey(pair.Second))
            .Select(pair => moduleOne[pair.Second]).Distinct().Single();
        foreach ((int feature, int buffGroup) in groups[4].FeatureIds.Zip(groups[4].BuffGroupIds).Where(pair => pair.First is 57 or 67))
            AssertEqual(false, moduleOne[buffGroup] == UsualModule(feature), $"fixture: group 4 gives weakness {feature} a foreign module");

        bool Consistent(int groupId) => groups[groupId].FeatureIds.Zip(groups[groupId].BuffGroupIds)
            .All(pair => pair.First is not (57 or 67) || moduleOne[pair.Second] == UsualModule(pair.First));

        // A player whose saved weakness set is group 4 is moved to a consistent group at login.
        var state = player.SimulatedBattlefield;
        state.BossChallengeSelectedFeatureGroup = 4;
        var login = (NotifyFubenBossSingleData)buildLoginData.Invoke(null, [player, null])!;
        int sent = login.FubenBossSingleData.ChallengeFeatureGroupId;
        AssertEqual(true, sent > 0 && sent != 4 && Consistent(sent), $"saved group 4 is replaced at login (sent {sent})");
        AssertEqual(sent, state.BossChallengeSelectedFeatureGroup, "the replacement is stored");

        // The weekly default never lands on group 4, and weeks that did not land on it keep their pick.
        MethodInfo weekly = RequiredMethod(module, "WeeklyIntensiveFeatureGroup", BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(AscNet.Common.Database.SimulatedBattlefieldState), typeof(BossSingleChallengeGradeTable)]);
        MethodInfo hash = RequiredMethod(module, "StableHash", BindingFlags.Static | BindingFlags.NonPublic, [typeof(string)]);
        BossSingleChallengeGradeTable grade = TableReaderV2.Parse<BossSingleChallengeGradeTable>().Single();
        List<int> usable = groups.Values.Where(row => row.BuffGroupIds.Count > 0 && row.BuffGroupIds.All(moduleOne.ContainsKey))
            .Select(row => row.Id).OrderBy(id => id).ToList();
        int moved = 0, kept = 0;
        for (int activity = 1; activity <= 400; activity++)
        {
            var probe = new AscNet.Common.Database.SimulatedBattlefieldState { BossActivityNo = activity };
            int picked = (int)weekly.Invoke(null, [probe, grade])!;
            int old = usable[(int)((uint)hash.Invoke(null, [$"{activity}:{grade.LevelType}:feature"])! % (uint)usable.Count)];
            AssertEqual(true, picked != 4 && Consistent(picked), $"week {activity} offers a consistent group (got {picked})");
            if (old == 4) moved++;
            else { kept++; AssertEqual(old, picked, $"week {activity} keeps its pick"); }
        }
        AssertEqual(true, moved > 0 && kept > 0, $"both week kinds exercised (moved {moved}, kept {kept})");
    }
}
