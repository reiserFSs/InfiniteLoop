using System.Reflection;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Commands;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.fuben.arena;
using Newtonsoft.Json.Linq;

namespace AscNet.Test
{
    internal partial class Program
    {
        // --warzone-command-only: /warzone tier|set|zones|zone|fluctuation|dmg, the command-only Desperate Fight boss profile
        // and the per-wave best-run records. Uses no-op Mongo collections; nothing is written to a database.
        private static void ValidateWarzoneZoneCommands()
        {
            using MongoCollectionOverride mongoOverride = MongoCollectionOverride.InstallForShopCompatibility();
            CommandFactory.commands.Clear();
            CommandFactory.LoadCommands();
            // Never read or write the developer's real Resources/Configs/simulated_battlefield.local.json.
            ArenaLocalScoreSettings.PathOverride = Path.Combine(Path.GetTempPath(), $"ascnet-warzone-dmg-{Environment.ProcessId}.local.json");
            File.Delete(ArenaLocalScoreSettings.PathOverride);

            const long playerId = 99_402;
            Dictionary<int, AreaStageTable> areaStages = TableReaderV2.Parse<AreaStageTable>().ToDictionary(row => row.Id);
            List<ChallengeAreaTable> challenges = TableReaderV2.Parse<ChallengeAreaTable>();
            List<ArenaLevelTable> arenaLevels = TableReaderV2.Parse<ArenaLevelTable>();
            Type arenaModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.ArenaModule");
            MethodInfo reconcileArenaLogin = RequiredMethod(arenaModule, "ReconcileLogin", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Session), typeof(long?)]);
            MethodInfo selectArenaAreas = RequiredMethod(arenaModule, "SelectedAreas", BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(int), typeof(long), typeof(ChallengeAreaTable), typeof(int)]);

            // AscNet policy score override: Mark 5 expressions come from simulated_battlefield.json, others from Mark.tsv.
            MethodInfo markExpression = RequiredMethod(arenaModule, "MarkExpression", BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(int), typeof(string), typeof(string)]);
            AssertEqual("EnemyHp/45*2", (string?)markExpression.Invoke(null, [5, "EnemyHpPoint", "EnemyHp/25*2"]), "Mark 5 damage override");
            AssertEqual("NpcGroup*50000", (string?)markExpression.Invoke(null, [5, "NpcGroupPoint", "NpcGroup*50000*2"]), "Mark 5 wave override");
            AssertEqual("Hp*200", (string?)markExpression.Invoke(null, [5, "MyHpPoint", "Hp*200*2"]), "Mark 5 HP override");
            AssertEqual("EnemyHp/100", (string?)markExpression.Invoke(null, [3, "EnemyHpPoint", "EnemyHp/100"]), "Mark 3 keeps table expression");

            // Automatic weekly selection must never pick a command-only (MarkId 5 boss generator) zone.
            foreach (ChallengeAreaTable challenge in challenges)
            {
                foreach (int activityNo in Enumerable.Range(470, 40))
                {
                    List<int> auto = (List<int>)selectArenaAreas.Invoke(null, [activityNo, playerId, challenge, 0])!;
                    if (auto.Any(areaId => areaStages[areaId].Desc.StartsWith("Desperate Fight", StringComparison.Ordinal)))
                        throw new InvalidDataException($"War Zone auto selection for challenge {challenge.ChallengeId} activity {activityNo} picked a command-only Desperate Fight zone: {string.Join(",", auto)}.");
                }
            }

            AscNet.Common.Database.Player player = CreateDrawCompatibilityPlayer(playerId);
            player.PlayerData.Name = "Warzone Command Tester";
            player.PlayerData.Level = 100;
            player.SimulatedBattlefield = new();
            player.PlayerData.CurrTeamId = 1;
            player.TeamGroups[1] = new TeamGroupDatum
            {
                TeamType = 1, TeamId = 1, CaptainPos = 1, FirstFightPos = 1,
                TeamData = new() { { 1, 1_021_001 }, { 2, 0 }, { 3, 0 } }, TeamName = "Warzone"
            };
            AscNet.Common.Database.Character character = CreateDrawCompatibilityCharacter(playerId);
            character.Characters.Add(CreateLoginAccountCompatibilityCharacter(1_021_001, 3_021_001));
            using LoopbackSessionHarness harness = new(character, player, CreateDrawCompatibilityInventory(playerId, []), "warzone-command-test");
            harness.Session.stage = CreateLoginAccountCompatibilityStage(playerId);
            _ = reconcileArenaLogin.Invoke(null, [harness.Session, (long?)null]);
            AssertEqual(arenaLevels.Single(level => level.Name == "Elite").Id, player.SimulatedBattlefield.ArenaLevel, "War Zone command test starts at Elite");

            int packetId = 92_000;
            string Run(string command, params string[] args)
            {
                Command instance = CommandFactory.CreateCommand(command, harness.Session, args)
                    ?? throw new InvalidDataException($"/{command} is not registered.");
                try
                {
                    instance.Execute();
                    return string.Empty;
                }
                catch (CommandMessageCallbackException message)
                {
                    return message.Message;
                }
            }
            void AssertContains(string expected, string actual, string name)
            {
                if (!actual.Contains(expected, StringComparison.Ordinal))
                    throw new InvalidDataException($"{name}: expected text '{expected}' in:\n{actual}");
            }
            int[] JoinAndReadAreas(string name)
            {
                int joinId = ++packetId;
                InvokeRegisteredRequestHandler("JoinActivityRequest", harness.Session, joinId, new Dictionary<string, object>());
                _ = ReadResponsePayload<JoinActivityResponse>(harness, joinId, nameof(JoinActivityResponse), $"{name} join", maxPacketsToRead: 128);
                int areaId = ++packetId;
                InvokeRegisteredRequestHandler("AreaDataRequest", harness.Session, areaId, new Dictionary<string, object>());
                AreaDataResponse areas = ReadResponsePayload<AreaDataResponse>(harness, areaId, nameof(AreaDataResponse), $"{name} areas", maxPacketsToRead: 128);
                AssertEqual(0, areas.Code, $"{name} AreaDataResponse code");
                return JArray.FromObject(areas.AreaList!).Select(area => area.Value<int>("AreaId")).ToArray();
            }
            PreFightResponse PreFight(int areaId, string name, int selectIndex = 0)
            {
                int id = ++packetId;
                InvokeRegisteredRequestHandler(nameof(PreFightRequest), harness.Session, id, new PreFightRequest
                {
                    PreFightData = new PreFightRequest.PreFightRequestPreFightData
                    {
                        ChallengeCount = 1, StageId = checked((uint)areaStages[areaId].StageId[0]), SelectAreaId = areaId,
                        ArenaSelectIndex = selectIndex, CardIds = [], RobotIds = []
                    }
                });
                PreFightResponse response = ReadResponsePayload<PreFightResponse>(harness, id, nameof(PreFightResponse), name, maxPacketsToRead: 128);
                AssertEqual(0, response.Code, $"{name} code");
                return response;
            }
            void AssertBossPreFight(int areaId, string name)
            {
                PreFightResponse response = PreFight(areaId, name);
                JArray waves = JArray.FromObject(response.FightData.NpcGroupList!);
                AssertEqual(99, waves.Count, $"{name} wave count");
                AssertEqual(true, waves.All(wave => wave["NpcList"]!.Count() == 1), $"{name} one enemy per wave");
                HashSet<int> npcIds = waves.Select(wave => wave["NpcList"]![0]!.Value<int>("NpcId")).ToHashSet();
                AssertEqual(true, new[] { 81_300, 81_600, 81_800 }.All(npcIds.Contains), $"{name} uses the MarkId 5 boss generator bosses");
                // AscNet policy: every 4th wave is a boss, cycling 81600 -> 81800 -> 81300; the other waves are regular enemies.
                int[] waveNpcIds = waves.Select(wave => wave["NpcList"]![0]!.Value<int>("NpcId")).ToArray();
                int[] bossCycle = [81_600, 81_800, 81_300];
                for (int wave = 1; wave <= waveNpcIds.Length; wave++)
                {
                    int npcId = waveNpcIds[wave - 1];
                    if (wave % 4 == 0)
                        AssertEqual(bossCycle[(wave / 4 - 1) % 3], npcId, $"{name} wave {wave} boss");
                    else
                        AssertEqual(false, bossCycle.Contains(npcId), $"{name} wave {wave} is a regular enemy");
                }
                // AscNet policy HpRamp: table HP rises with the wave number, separately for regular enemies and bosses.
                Dictionary<int, AscNet.Table.V2.share.fight.npc.NpcTable> npcRows = TableReaderV2.Parse<AscNet.Table.V2.share.fight.npc.NpcTable>().ToDictionary(row => row.Id);
                Dictionary<int, string> lifeRows = TableReaderV2.Parse<AscNet.Table.V2.share.attrib.AttribTable>().ToDictionary(row => row.Id, row => row.Life);
                Dictionary<int, string> growRows = TableReaderV2.Parse<AscNet.Table.V2.share.attrib.AttribPromotedTable>().ToDictionary(row => row.Id, row => row.Life);
                double Fixed(string text) => long.Parse(text[2..], System.Globalization.NumberStyles.HexNumber) / 4294967296d;
                double WaveHp(JToken wave)
                {
                    JToken npc = wave["NpcList"]![0]!;
                    var row = npcRows[npc.Value<int>("NpcId")];
                    return Fixed(lifeRows[row.AttribId]) + Fixed(growRows[row.PromotedId]) * (npc.Value<int>("Level") - 1);
                }
                double previousRegular = 0, previousBoss = 0;
                for (int wave = 1; wave <= waves.Count; wave++)
                {
                    double hp = WaveHp(waves[wave - 1]);
                    bool isBoss = wave % 4 == 0;
                    AssertEqual(true, hp >= (isBoss ? previousBoss : previousRegular), $"{name} wave {wave} HP does not drop");
                    if (isBoss) previousBoss = hp; else previousRegular = hp;
                }
                // AscNet policy BossHpRatio 2 (checked on the unscaled generator levels):
                // a boss has about twice the table HP of the regular enemy in the wave before it.
                var baseline = new PreFightResponse.PreFightResponseFightData();
                AssertEqual(true, ArenaStageDataset.TryHydrate(areaId, checked((uint)areaStages[areaId].StageId[0]), areaStages[areaId].MarkId, areaStages[areaId].Desc, baseline),
                    $"{name} baseline hydrate");
                JArray baselineWaves = JArray.FromObject(baseline.NpcGroupList!);
                for (int wave = 4; wave <= baselineWaves.Count; wave += 4)
                {
                    double bossHp = WaveHp(baselineWaves[wave - 1]), regularHp = WaveHp(baselineWaves[wave - 2]);
                    AssertEqual(true, bossHp >= regularHp * 1.9 && bossHp <= regularHp * 2.2, $"{name} wave {wave} boss HP {bossHp:0} is ~2x regular {regularHp:0}");
                }
                AssertEqual((long)areaStages[areaId].BuffIds.First(buffId => buffId > 0), Convert.ToInt64(response.FightData.EventIds[0]),
                    $"{name} selected zone buff (ArenaSelectIndex 0) from AreaStage");
                AssertEqual("5", JObject.FromObject(response.FightData.StageParams).Value<string>("MarkId"), $"{name} MarkId");
            }

            // Elite tier: regular zones only.
            string message = Run("warzone", "set", "ice");
            AssertEqual(7, player.SimulatedBattlefield.ArenaZoneOverrideAreaId, "/warzone set ice picks the Ice Desperate Fight zone");
            AssertContains("7 Crimson Frostland [Ice] Boss", message, "/warzone set ice message");
            AssertContains("bosses [81600, 81800, 81300] every 4 waves", message, "/warzone set ice enemy profile");
            int[] eliteAreas = JoinAndReadAreas("War Zone Elite ice");
            AssertEqual(7, eliteAreas[0], "War Zone forced Ice zone offered first");
            AssertBossPreFight(7, "War Zone Elite ice boss pre-fight");
            // A forced zone keeps the regular per-tab events: the zone buff plus at most one support event.
            AssertEqual(true, PreFight(7, "War Zone Elite ice events").FightData.EventIds.Count is 1 or 2, "Ice zone sends the zone buff and its tab support event");

            // /warzone fluctuation: 0 = off; otherwise levels scale by level/385 on top of the tier scaling (two distinct inputs).
            long FirstLevel(PreFightResponse response) => JArray.FromObject(response.FightData.NpcGroupList!)[0]!["NpcList"]![0]!.Value<long>("Level");
            long offLevel = FirstLevel(PreFight(7, "War Zone fluctuation off"));
            AssertContains("x2", Run("warzone", "fluctuation", "770"), "/warzone fluctuation 770 message");
            AssertEqual(770, player.SimulatedBattlefield.ArenaFluctuationLevel, "/warzone fluctuation stores the level");
            long doubleLevel = FirstLevel(PreFight(7, "War Zone fluctuation 770"));
            AssertEqual(true, Math.Abs(doubleLevel - offLevel * 2) <= 1, $"fluctuation 770 doubles NPC levels ({offLevel} -> {doubleLevel})");
            _ = Run("warzone", "fluctuation", "385");
            AssertEqual(offLevel, FirstLevel(PreFight(7, "War Zone fluctuation 385")), "fluctuation 385 keeps the tier-scaled level");
            AssertContains("turned off", Run("warzone", "fluctuation", "0"), "/warzone fluctuation 0 turns it off");
            AssertEqual(offLevel, FirstLevel(PreFight(7, "War Zone fluctuation 0")), "fluctuation 0 keeps the tier-scaled level");

            // Best-run per-wave records (WaveCostTimes/TimePointScore) are echoed back through StageParams.
            uint iceStage = checked((uint)areaStages[7].StageId[0]);
            MethodInfo recordBest = RequiredMethod(arenaModule, "RecordStageBestRecords", BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(AscNet.Common.Database.SimulatedBattlefieldState), typeof(FightSettleResult)]);
            recordBest.Invoke(null, [player.SimulatedBattlefield, new FightSettleResult
            {
                StageId = iceStage,
                StringToListIntRecord = new() { ["WaveCostTimes"] = [12, 30, 47], ["TimePointScore"] = [900, 800], ["Other"] = [1] }
            }]);
            JObject echoed = JObject.FromObject(PreFight(7, "War Zone best-run echo").FightData.StageParams);
            AssertEqual("[12,30,47]", echoed.Value<string>("WaveCostTimes"), "best-run WaveCostTimes echoed");
            AssertEqual("[900,800]", echoed.Value<string>("TimePointScore"), "best-run TimePointScore echoed");
            AssertEqual(null, echoed.Value<string>("Other"), "only the per-wave record keys are echoed");

            _ = Run("warzone", "set", "fire");
            AssertEqual(8, player.SimulatedBattlefield.ArenaZoneOverrideAreaId, "/warzone set fire picks a distinct zone");
            _ = Run("warzone", "set", "ice", "duo");
            AssertEqual(12, player.SimulatedBattlefield.ArenaZoneOverrideAreaId, "/warzone set ice duo");
            AssertContains("No enemy profile", Run("warzone", "set", "ice", "mob"), "/warzone set ice mob explains missing Mob data");
            AssertEqual(12, player.SimulatedBattlefield.ArenaZoneOverrideAreaId, "failed /warzone set keeps the zone");
            AssertContains("Use /warzone tier Hero", Run("warzone", "set", "plasma"), "/warzone set plasma below Hero");
            AssertContains("Use /warzone tier Hero", Run("warzone", "set", "vortex", "barrier"), "/warzone set by buff name below Hero");
            AssertContains("Unknown Warzone element", Run("warzone", "set", "lava"), "/warzone set unknown element");
            _ = Run("warzone", "set", "crimson", "frostland");
            AssertEqual(7, player.SimulatedBattlefield.ArenaZoneOverrideAreaId, "/warzone set by zone name");
            string zones = Run("warzone", "zones");
            AssertContains("7 Crimson Frostland [Ice] Boss (selected)", zones, "/warzone zones element/type/selection");
            AssertContains("12 Crimson Frostland [Ice] Duo", zones, "/warzone zones Duo zone");
            string iceZones = Run("warzone", "zones", "ice");
            if (iceZones.Contains("\n8 ", StringComparison.Ordinal))
                throw new InvalidDataException($"/warzone zones ice listed a Fire zone:\n{iceZones}");

            // Hero tier: Entropic Distortion zones become selectable.
            _ = Run("warzone", "tier", "Hero");
            message = Run("warzone", "set", "plasma");
            AssertEqual(40, player.SimulatedBattlefield.ArenaZoneOverrideAreaId, "/warzone set plasma picks a Plasma-led boss zone");
            AssertContains("40 Entropic Distortion [Plasma+True Slash] Boss", message, "/warzone set plasma message");
            int[] heroAreas = JoinAndReadAreas("War Zone Hero plasma");
            AssertEqual(40, heroAreas[^1], "War Zone forced Entropic Distortion zone in the special slot");
            AssertBossPreFight(40, "War Zone Hero plasma boss pre-fight");

            _ = Run("warzone", "set", "glacio");
            int glacioArea = player.SimulatedBattlefield.ArenaZoneOverrideAreaId;
            AssertEqual(4143, areaStages[glacioArea].BuffIds[0], "/warzone set glacio Glacio-led zone");
            AssertEqual(true, areaStages[glacioArea].Desc.StartsWith("Desperate Fight", StringComparison.Ordinal), "/warzone set glacio boss zone");
            _ = JoinAndReadAreas("War Zone Hero glacio");
            AssertBossPreFight(glacioArea, "War Zone Hero glacio boss pre-fight");
            // Two-buff zone: ArenaSelectIndex selects the zone buff (tab).
            int[] glacioBuffs = areaStages[glacioArea].BuffIds.Where(buffId => buffId > 0).ToArray();
            if (glacioBuffs.Length > 1)
            {
                int[] glacioFirst = PreFight(glacioArea, "War Zone Hero glacio buff 1", 0).FightData.EventIds.Select(Convert.ToInt32).ToArray();
                int[] glacioSecond = PreFight(glacioArea, "War Zone Hero glacio buff 2", 1).FightData.EventIds.Select(Convert.ToInt32).ToArray();
                AssertEqual(glacioBuffs[0], glacioFirst[0], "glacio zone ArenaSelectIndex 0 sends only the first zone buff");
                AssertEqual(false, glacioFirst.Contains(glacioBuffs[1]), "glacio zone ArenaSelectIndex 0 omits the second zone buff");
                AssertEqual(glacioBuffs[1], glacioSecond[0], "glacio zone ArenaSelectIndex 1 sends the second zone buff");
            }

            _ = Run("warzone", "set", "ice");
            AssertEqual(7, player.SimulatedBattlefield.ArenaZoneOverrideAreaId, "Hero /warzone set ice prefers the regular Ice zone");
            _ = Run("wz", "set", "dark");
            AssertEqual(10, player.SimulatedBattlefield.ArenaZoneOverrideAreaId, "/wz alias set dark");
            string plasmaBossZones = Run("warzone", "zones", "plasma", "boss");
            AssertContains("40 Entropic Distortion [Plasma+True Slash] Boss", plasmaBossZones, "/warzone zones plasma boss");
            if (plasmaBossZones.Contains(" Duo", StringComparison.Ordinal))
                throw new InvalidDataException($"/warzone zones plasma boss listed a Duo zone:\n{plasmaBossZones}");
            _ = Run("warzone", "zone", "auto");
            AssertEqual(0, player.SimulatedBattlefield.ArenaZoneOverrideAreaId, "/warzone zone auto still clears the selection");
            _ = Run("warzone", "fluctuation", "500");
            AssertContains("fluctuation turned off", Run("warzone", "reset"), "/warzone reset message");
            AssertEqual(0, player.SimulatedBattlefield.ArenaFluctuationLevel, "/warzone reset turns fluctuation off");
            AssertEqual(arenaLevels.Single(level => level.Name == "Elite").Id, player.SimulatedBattlefield.ArenaLevel, "/warzone reset restores the initial tier");

            // /warzone dmg: local, per-install percent on the damage expression, stored in a temp file (never the real one).
            string localSettings = ArenaLocalScoreSettings.FilePath;
            MethodInfo evaluate = RequiredMethod(arenaModule, "Evaluate", BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(string), typeof(Dictionary<string, double>), typeof(int)]);
            int Points(string? expression, double damage) =>
                (int)evaluate.Invoke(null, [expression, new Dictionary<string, double> { ["EnemyHp"] = damage }, 0])!;
            string? Expression(int markId, string tableExpression) => (string?)markExpression.Invoke(null, [markId, "EnemyHpPoint", tableExpression]);
            AssertContains("Boss (Mark 5): 100% (default), EnemyHp/45*2, 44,444 pts", Run("warzone", "dmg"), "/warzone dmg shows defaults");
            AssertContains("Damage score set to 120% for Boss (Mark 5)", Run("wz", "dmg", "120", "boss"), "/wz dmg 120 boss");
            AssertEqual(true, File.Exists(localSettings), "/warzone dmg writes the local settings file");
            AssertEqual("EnemyHp/45*2*120/100", Expression(5, "EnemyHp/25*2"), "Mark 5 damage scaled to 120%");
            AssertEqual(53_333, Points(Expression(5, "EnemyHp/25*2"), 1_000_000), "120% of 1M damage on Mark 5");
            AssertEqual(46_666, Points(Expression(5, "EnemyHp/25*2"), 875_000), "120% of 875k damage on Mark 5");
            AssertEqual("EnemyHp/100", Expression(3, "EnemyHp/100"), "Mark 3 untouched by a boss-only percent");
            _ = Run("warzone", "dmg", "50%");
            AssertEqual("EnemyHp/100*50/100", Expression(3, "EnemyHp/100"), "percent without a mode applies to Mark 3");
            AssertEqual("EnemyHp/50*50/100", Expression(4, "EnemyHp/50"), "percent without a mode applies to Mark 4");
            AssertEqual("EnemyHp/45*2*50/100", Expression(5, "EnemyHp/25*2"), "percent without a mode replaces the Mark 5 value");
            AssertContains("not scored by damage", Run("warzone", "dmg", "120", "1"), "kill-ratio Mark 1 is rejected");
            AssertContains("Percent must be", Run("warzone", "dmg", "0"), "0% is rejected");
            _ = Run("warzone", "dmg", "reset", "duo");
            AssertEqual("EnemyHp/50", Expression(4, "EnemyHp/50"), "/warzone dmg reset duo restores Mark 4");
            AssertEqual("EnemyHp/100*50/100", Expression(3, "EnemyHp/100"), "/warzone dmg reset duo keeps Mark 3");
            _ = Run("warzone", "dmg", "reset");
            AssertEqual("EnemyHp/45*2", Expression(5, "EnemyHp/25*2"), "/warzone dmg reset restores Mark 5");
            AssertEqual(false, File.Exists(localSettings), "/warzone dmg reset removes the empty local settings file");
            File.WriteAllText(localSettings, "{ broken");
            AssertEqual("EnemyHp/45*2", Expression(5, "EnemyHp/25*2"), "an unreadable local file is ignored");
            AssertContains("could not be read", Run("warzone", "dmg"), "/warzone dmg reports an unreadable local file");
            _ = Run("warzone", "dmg", "reset");
            AssertEqual(false, File.Exists(localSettings), "/warzone dmg reset removes an unreadable local file");
            Console.WriteLine("Warzone zone command compatibility passed.");
        }
    }
}
