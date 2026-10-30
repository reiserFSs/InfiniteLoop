using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Game;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.fuben.arena;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Newtonsoft.Json.Linq;
using System.Reflection;

namespace AscNet.Test;

internal partial class Program
{
    // Player report: Entropic Distortion (an effect zone) shares its StageId with a normal zone (Nihil), so its better-than-nothing
    // score was dropped (stage best compared) and the map showed the other zone's stage score. Each area keeps its own best.
    private static void ValidateArenaEffectZoneScore()
    {
        using MongoCollectionOverride mongoOverride = MongoCollectionOverride.InstallForShopCompatibility();
        Dictionary<int, AreaStageTable> areaStages = TableReaderV2.Parse<AreaStageTable>().ToDictionary(row => row.Id);
        List<ChallengeAreaTable> challenges = TableReaderV2.Parse<ChallengeAreaTable>();
        int heroLevel = JObject.Parse(File.ReadAllText(ResourcePath("Configs", "simulated_battlefield.json")))["ArenaActivity"]!.Value<int>("HeroArenaLevel");
        ChallengeAreaTable hero = challenges.Where(row => row.ArenaLv == heroLevel && 100 >= row.MinLv && 100 <= row.MaxLv).OrderBy(row => row.ChallengeId).First();

        Type arenaModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.ArenaModule");
        MethodInfo reconcileLogin = RequiredMethod(arenaModule, "ReconcileLogin", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Session), typeof(long?)]);
        MethodInfo currentActivity = RequiredMethod(arenaModule, "CurrentActivity", BindingFlags.Static | BindingFlags.NonPublic, [typeof(long?)]);
        MethodInfo selectAreas = RequiredMethod(arenaModule, "SelectedAreas", BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(int), typeof(long), typeof(ChallengeAreaTable)]);
        MethodInfo record = RequiredMethod(arenaModule, "RecordFightResult", BindingFlags.Static | BindingFlags.Public, [typeof(Session), typeof(FightSettleResult)]);

        int activity = (int)currentActivity.Invoke(null, [(long?)null])!;
        // Find a player whose selection holds two zones with one StageId, at least one of them a two-tab effect zone.
        long playerId = 0; int normalArea = 0, effectArea = 0;
        for (long candidate = 99_500; candidate < 101_500 && effectArea == 0; candidate++)
        {
            List<int> areas = (List<int>)selectAreas.Invoke(null, [activity, candidate, hero])!;
            foreach (int effect in areas.Where(id => areaStages[id].DistributeTypes.Count(type => type > 0) == 2))
                foreach (int normal in areas.Where(id => id != effect && areaStages[id].StageId[0] == areaStages[effect].StageId[0]))
                    (playerId, normalArea, effectArea) = (candidate, normal, effect);
        }
        if (effectArea == 0)
            throw new InvalidDataException("No simulated player selects an effect zone sharing its StageId with another zone.");

        AscNet.Common.Database.Player player = CreateDrawCompatibilityPlayer(playerId);
        player.PlayerData.Level = 100;
        player.SimulatedBattlefield = new();
        AscNet.Common.Database.Character character = CreateDrawCompatibilityCharacter(playerId);
        using LoopbackSessionHarness harness = new(character, player, CreateDrawCompatibilityInventory(playerId, []), "arena-effect-zone-score-test");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(playerId);
        _ = reconcileLogin.Invoke(null, [harness.Session, (long?)null]);
        player.SimulatedBattlefield.ArenaLevel = hero.ArenaLv;
        player.SimulatedBattlefield.ArenaChallengeId = hero.ChallengeId;
        player.SimulatedBattlefield.ArenaActivityNo = activity;
        player.SimulatedBattlefield.ArenaJoined = true;
        player.SimulatedBattlefield.ArenaJoinActivity = activity;

        ArenaResult Settle(int areaId, int npcGroup)
        {
            uint stageId = checked((uint)areaStages[areaId].StageId[0]);
            harness.Session.fight = new Fight(new PreFightRequest
            {
                PreFightData = new PreFightRequest.PreFightRequestPreFightData
                { ChallengeCount = 1, StageId = stageId, SelectAreaId = areaId, ArenaSelectIndex = 0, CardIds = [], RobotIds = [] }
            }, 1);
            return (ArenaResult)record.Invoke(null, [harness.Session, new FightSettleResult
            {
                IsWin = true, StageId = stageId, StartFrame = 1, SettleFrame = 3825, PauseFrame = 1423,
                LeftTime = 179, DeathTotalEnemy = npcGroup, TotalDamage = npcGroup * 1_000_000L,
                StringToIntRecord = new Dictionary<string, int> { ["NpcGroup"] = npcGroup },
                NpcHpInfo = new() { [1] = new NpcHp { Type = 1, AttrTable = new() { [1] = new Dictionary<object, object> { ["Value"] = 100, ["MaxValue"] = 100 } }, BuffIds = [] } }
            }])!
                ?? throw new InvalidDataException($"Arena area {areaId} settle was not scored.");
        }

        ArenaResult normalScore = Settle(normalArea, 60);
        ArenaResult effectScore = Settle(effectArea, 5);
        AssertEqual(true, effectScore.Point < normalScore.Point && effectScore.Point > 0, "effect zone fight scores lower than the shared-stage zone");
        AssertEqual(0, effectScore.OldArenaMaxPoint, "effect zone old best is its own, not the shared stage's");
        AssertEqual(effectScore.Point, effectScore.ArenaMaxPoint, "effect zone settle reports its own best");

        void CheckProjection(AscNet.Common.Database.Player owner, string label)
        {
            Session session = harness.Session;
            AscNet.Common.Database.Player previous = session.player;
            session.player = owner;
            try
            {
                AreaDataResponse response = (AreaDataResponse)RequiredMethod(arenaModule, "BuildAreaData", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Session)])
                    .Invoke(null, [session])!;
                List<JObject> areas = JArray.FromObject(response.AreaList!).OfType<JObject>().ToList();
                AssertEqual(normalScore.Point, areas.Single(area => area.Value<int>("AreaId") == normalArea).Value<int>("Point"), $"{label} normal area Point");
                AssertEqual(effectScore.Point, areas.Single(area => area.Value<int>("AreaId") == effectArea).Value<int>("Point"), $"{label} effect area Point");
                AssertEqual(normalScore.Point, areas.Single(area => area.Value<int>("AreaId") == normalArea)["StageInfos"]![0]!.Value<int>("Point"), $"{label} normal StageInfos");
                AssertEqual(effectScore.Point, areas.Single(area => area.Value<int>("AreaId") == effectArea)["StageInfos"]![0]!.Value<int>("Point"), $"{label} effect StageInfos is not the shared stage's score");
                AssertEqual(normalScore.Point + effectScore.Point, response.TotalPoint, $"{label} total is the sum of all areas");
            }
            finally { session.player = previous; }
        }
        CheckProjection(player, "live");
        AssertEqual(normalScore.Point + effectScore.Point, player.SimulatedBattlefield.ArenaPoint, "stored total");
        AscNet.Common.Database.Player reloaded = BsonSerializer.Deserialize<AscNet.Common.Database.Player>(player.ToBson());
        CheckProjection(reloaded, "reloaded");
    }
}
