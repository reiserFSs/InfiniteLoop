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
        // --warzone-command-only: /warzone tier|set|zones|zone|fluctuation|dmg and the per-area best-run wave records.
        // Uses no-op Mongo collections; nothing is written to a database.
        private static void ValidateWarzoneZoneCommands()
        {
            using MongoCollectionOverride mongoOverride = MongoCollectionOverride.InstallForShopCompatibility();
            CommandFactory.commands.Clear();
            CommandFactory.LoadCommands();
            // Never read or write the server's real install-wide /warzone dmg file.
            ArenaLocalScoreSettings.PathOverride = Path.Combine(Path.GetTempPath(), $"ascnet-warzone-dmg-{Environment.ProcessId}.local.json");
            File.Delete(ArenaLocalScoreSettings.PathOverride);

            const long playerId = 99_402;
            Dictionary<int, AreaStageTable> areaStages = TableReaderV2.Parse<AreaStageTable>().ToDictionary(row => row.Id);
            Dictionary<int, MarkTable> marks = TableReaderV2.Parse<MarkTable>().ToDictionary(row => row.MarkId);
            List<ArenaLevelTable> arenaLevels = TableReaderV2.Parse<ArenaLevelTable>();
            Type arenaModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.ArenaModule");
            MethodInfo reconcileArenaLogin = RequiredMethod(arenaModule, "ReconcileLogin", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Session), typeof(long?)]);
            MethodInfo markExpression = RequiredMethod(arenaModule, "MarkExpression", BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(int), typeof(string), typeof(string)]);

            // Without a /warzone dmg percent every score expression is the Mark.tsv one, unchanged.
            foreach (MarkTable mark in marks.Values)
            {
                AssertEqual(mark.EnemyHpPoint, (string?)markExpression.Invoke(null, [mark.MarkId, "EnemyHpPoint", mark.EnemyHpPoint]), $"Mark {mark.MarkId} damage expression");
                AssertEqual(mark.MyHpPoint, (string?)markExpression.Invoke(null, [mark.MarkId, "MyHpPoint", mark.MyHpPoint]), $"Mark {mark.MarkId} HP expression");
                AssertEqual(mark.NpcGroupPoint, (string?)markExpression.Invoke(null, [mark.MarkId, "NpcGroupPoint", mark.NpcGroupPoint]), $"Mark {mark.MarkId} wave expression");
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

            // Elite tier: Ice has no hydratable Boss zone, so /warzone set ice falls back to the Ice Duo zone.
            string message = Run("warzone", "set", "ice");
            AssertEqual(12, player.SimulatedBattlefield.ArenaZoneOverrideAreaId, "/warzone set ice picks the Ice Duo zone");
            AssertContains("12 Crimson Frostland [Ice] Duo", message, "/warzone set ice message");
            int[] eliteAreas = JoinAndReadAreas("War Zone Elite ice");
            AssertEqual(12, eliteAreas[0], "War Zone forced Ice zone offered first");
            PreFightResponse icePreFight = PreFight(12, "War Zone Elite ice pre-fight");
            AssertEqual("4", JObject.FromObject(icePreFight.FightData.StageParams).Value<string>("MarkId"), "Ice Duo zone MarkId");
            // A forced zone keeps the regular per-tab events: the zone buff plus at most one support event.
            AssertEqual(true, icePreFight.FightData.EventIds.Count is 1 or 2, "Ice zone sends the zone buff and its tab support event");
            AssertEqual((long)areaStages[12].BuffIds.First(buffId => buffId > 0), Convert.ToInt64(icePreFight.FightData.EventIds[0]),
                "Ice zone buff (ArenaSelectIndex 0) from AreaStage");

            // /warzone fluctuation: 0 = off; 1..3850 scales levels by level/385 on top of the tier scaling (distinct inputs).
            long FirstLevel(PreFightResponse response) => JArray.FromObject(response.FightData.NpcGroupList!)[0]!["NpcList"]![0]!.Value<long>("Level");
            long offLevel = FirstLevel(PreFight(12, "War Zone fluctuation off"));
            AssertContains("x2", Run("warzone", "fluctuation", "770"), "/warzone fluctuation 770 message");
            AssertEqual(770, player.SimulatedBattlefield.ArenaFluctuationLevel, "/warzone fluctuation stores the level");
            long doubleLevel = FirstLevel(PreFight(12, "War Zone fluctuation 770"));
            AssertEqual(true, Math.Abs(doubleLevel - offLevel * 2) <= 1, $"fluctuation 770 doubles NPC levels ({offLevel} -> {doubleLevel})");
            _ = Run("warzone", "fluctuation", "385");
            AssertEqual(offLevel, FirstLevel(PreFight(12, "War Zone fluctuation 385")), "fluctuation 385 keeps the tier-scaled level");
            AssertContains("must be 0 (off) or 1-3850", Run("warzone", "fluctuation", "-5"), "/warzone fluctuation rejects a negative level");
            AssertContains("must be 0 (off) or 1-3850", Run("warzone", "fluctuation", "3851"), "/warzone fluctuation rejects a level above the range");
            AssertEqual(385, player.SimulatedBattlefield.ArenaFluctuationLevel, "a rejected fluctuation level keeps the stored one");
            AssertContains("turned off", Run("warzone", "fluctuation", "0"), "/warzone fluctuation 0 turns it off");
            AssertEqual(offLevel, FirstLevel(PreFight(12, "War Zone fluctuation 0")), "fluctuation 0 keeps the tier-scaled level");

            // Best-run per-wave records (WaveCostTimes/TimePointScore) are echoed back through StageParams, per area:
            // areas 12 (Crimson Frostland) and 34 (an Entropic Distortion tab) share one StageId.
            uint iceStage = checked((uint)areaStages[12].StageId[0]);
            AssertEqual(iceStage, checked((uint)areaStages[34].StageId[0]), "areas 12 and 34 share a StageId");
            MethodInfo recordBest = RequiredMethod(arenaModule, "RecordStageBestRecords", BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(AscNet.Common.Database.SimulatedBattlefieldState), typeof(int), typeof(FightSettleResult)]);
            recordBest.Invoke(null, [player.SimulatedBattlefield, 12, new FightSettleResult
            {
                StageId = iceStage,
                StringToListIntRecord = new() { ["WaveCostTimes"] = [12, 30, 47], ["TimePointScore"] = [900, 800], ["Other"] = [1] }
            }]);
            recordBest.Invoke(null, [player.SimulatedBattlefield, 34, new FightSettleResult
            {
                StageId = iceStage,
                StringToListIntRecord = new() { ["WaveCostTimes"] = [5], ["TimePointScore"] = [100] }
            }]);
            JObject echoed = JObject.FromObject(PreFight(12, "War Zone best-run echo").FightData.StageParams);
            AssertEqual("[12,30,47]", echoed.Value<string>("WaveCostTimes"), "area 12 echoes its own WaveCostTimes, not area 34's");
            AssertEqual("[900,800]", echoed.Value<string>("TimePointScore"), "area 12 echoes its own TimePointScore");
            AssertEqual(null, echoed.Value<string>("Other"), "only the per-wave record keys are echoed");
            AssertEqual(2, player.SimulatedBattlefield.ArenaAreaStageBestRecords.Count, "best records are stored per area and stage");

            _ = Run("warzone", "set", "fire");
            AssertEqual(13, player.SimulatedBattlefield.ArenaZoneOverrideAreaId, "/warzone set fire picks a distinct zone");
            _ = Run("warzone", "set", "ice", "duo");
            AssertEqual(12, player.SimulatedBattlefield.ArenaZoneOverrideAreaId, "/warzone set ice duo");
            AssertEqual(0, player.SimulatedBattlefield.ArenaAreaStageBestRecords.Count, "a zone change clears the best records with the scores");
            AssertContains("No enemy profile", Run("warzone", "set", "ice", "mob"), "/warzone set ice mob explains missing Mob data");
            AssertEqual(12, player.SimulatedBattlefield.ArenaZoneOverrideAreaId, "failed /warzone set keeps the zone");
            AssertContains("Use /warzone tier Hero", Run("warzone", "set", "plasma"), "/warzone set plasma below Hero");
            AssertContains("Use /warzone tier Hero", Run("warzone", "set", "vortex", "barrier"), "/warzone set by buff name below Hero");
            AssertContains("Unknown Warzone element", Run("warzone", "set", "lava"), "/warzone set unknown element");
            _ = Run("warzone", "set", "cycle", "of", "fire");
            AssertEqual(13, player.SimulatedBattlefield.ArenaZoneOverrideAreaId, "/warzone set by zone name");
            string zones = Run("warzone", "zones");
            AssertContains("13 Cycle of Fire [Fire] Duo (selected)", zones, "/warzone zones element/type/selection");
            AssertContains("12 Crimson Frostland [Ice] Duo", zones, "/warzone zones Duo zone");
            string iceZones = Run("warzone", "zones", "ice");
            if (iceZones.Contains("\n13 ", StringComparison.Ordinal))
                throw new InvalidDataException($"/warzone zones ice listed a Fire zone:\n{iceZones}");

            // Hero tier: Entropic Distortion zones become selectable for the special slot.
            _ = Run("warzone", "tier", "Hero");
            message = Run("warzone", "set", "plasma");
            int plasmaArea = player.SimulatedBattlefield.ArenaZoneOverrideAreaId;
            AssertEqual(true, areaStages[plasmaArea].BuffIds.Contains(4114), $"/warzone set plasma picks a Plasma zone ({plasmaArea})");
            AssertEqual("Entropic Distortion", areaStages[plasmaArea].Name, "/warzone set plasma picks an Entropic Distortion zone");
            AssertContains($"{plasmaArea} Entropic Distortion", message, "/warzone set plasma message");
            int[] heroAreas = JoinAndReadAreas("War Zone Hero plasma");
            AssertEqual(plasmaArea, heroAreas[^1], "War Zone forced Entropic Distortion zone in the special slot");
            // Two-buff zone: ArenaSelectIndex selects the zone buff (tab).
            int[] plasmaBuffs = areaStages[plasmaArea].BuffIds.Where(buffId => buffId > 0).ToArray();
            if (plasmaBuffs.Length > 1)
            {
                int[] first = PreFight(plasmaArea, "War Zone Hero plasma buff 1", 0).FightData.EventIds.Select(Convert.ToInt32).ToArray();
                int[] second = PreFight(plasmaArea, "War Zone Hero plasma buff 2", 1).FightData.EventIds.Select(Convert.ToInt32).ToArray();
                AssertEqual(plasmaBuffs[0], first[0], "plasma zone ArenaSelectIndex 0 sends only the first zone buff");
                AssertEqual(false, first.Contains(plasmaBuffs[1]), "plasma zone ArenaSelectIndex 0 omits the second zone buff");
                AssertEqual(plasmaBuffs[1], second[0], "plasma zone ArenaSelectIndex 1 sends the second zone buff");
            }

            _ = Run("warzone", "set", "ice");
            AssertEqual(12, player.SimulatedBattlefield.ArenaZoneOverrideAreaId, "Hero /warzone set ice prefers the regular Ice zone");
            _ = Run("wz", "set", "dark");
            AssertEqual(15, player.SimulatedBattlefield.ArenaZoneOverrideAreaId, "/wz alias set dark");
            _ = Run("warzone", "zone", "auto");
            AssertEqual(0, player.SimulatedBattlefield.ArenaZoneOverrideAreaId, "/warzone zone auto still clears the selection");
            _ = Run("warzone", "fluctuation", "500");
            AssertContains("fluctuation turned off", Run("warzone", "reset"), "/warzone reset message");
            AssertEqual(0, player.SimulatedBattlefield.ArenaFluctuationLevel, "/warzone reset turns fluctuation off");
            AssertEqual(arenaLevels.Single(level => level.Name == "Elite").Id, player.SimulatedBattlefield.ArenaLevel, "/warzone reset restores the initial tier");

            // /warzone dmg: install-wide percent on the Mark.tsv damage expression, stored in a temp file (never the real one).
            string localSettings = ArenaLocalScoreSettings.FilePath;
            MethodInfo evaluate = RequiredMethod(arenaModule, "Evaluate", BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(string), typeof(Dictionary<string, double>), typeof(int)]);
            int Points(string? expression, double damage) =>
                (int)evaluate.Invoke(null, [expression, new Dictionary<string, double> { ["EnemyHp"] = damage }, 0])!;
            string? Expression(int markId) => (string?)markExpression.Invoke(null, [markId, "EnemyHpPoint", marks[markId].EnemyHpPoint]);
            string table5 = marks[5].EnemyHpPoint!;
            int default5 = Points(table5, 1_000_000);
            AssertContains($"Boss (Mark 5): 100% (default), {table5}, {default5.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} pts",
                Run("warzone", "dmg"), "/warzone dmg shows the table defaults");
            AssertContains("install-wide", Run("warzone", "dmg"), "/warzone dmg says it is install-wide");
            AssertContains("for every player on this server", Run("wz", "dmg", "120", "boss"), "/wz dmg 120 boss");
            AssertEqual(true, File.Exists(localSettings), "/warzone dmg writes the settings file");
            AssertEqual($"{table5}*120/100", Expression(5), "Mark 5 damage scaled to 120%");
            AssertEqual(default5 * 120 / 100, Points(Expression(5), 1_000_000), "120% of 1M damage on Mark 5");
            AssertEqual(Points(table5, 875_000) * 120 / 100, Points(Expression(5), 875_000), "120% of 875k damage on Mark 5");
            AssertEqual(marks[3].EnemyHpPoint, Expression(3), "Mark 3 untouched by a boss-only percent");
            _ = Run("warzone", "dmg", "50%");
            AssertEqual($"{marks[3].EnemyHpPoint}*50/100", Expression(3), "percent without a mode applies to Mark 3");
            AssertEqual($"{marks[4].EnemyHpPoint}*50/100", Expression(4), "percent without a mode applies to Mark 4");
            AssertEqual($"{table5}*50/100", Expression(5), "percent without a mode replaces the Mark 5 value");
            AssertContains("not scored by damage", Run("warzone", "dmg", "120", "1"), "kill-ratio Mark 1 is rejected");
            AssertContains("Percent must be", Run("warzone", "dmg", "0"), "0% is rejected");
            _ = Run("warzone", "dmg", "reset", "duo");
            AssertEqual(marks[4].EnemyHpPoint, Expression(4), "/warzone dmg reset duo restores Mark 4");
            AssertEqual($"{marks[3].EnemyHpPoint}*50/100", Expression(3), "/warzone dmg reset duo keeps Mark 3");
            _ = Run("warzone", "dmg", "reset");
            AssertEqual(table5, Expression(5), "/warzone dmg reset restores Mark 5");
            AssertEqual(false, File.Exists(localSettings), "/warzone dmg reset removes the empty settings file");
            File.WriteAllText(localSettings, "{ broken");
            AssertEqual(table5, Expression(5), "an unreadable settings file is ignored");
            AssertContains("could not be read", Run("warzone", "dmg"), "/warzone dmg reports an unreadable settings file");
            _ = Run("warzone", "dmg", "reset");
            AssertEqual(false, File.Exists(localSettings), "/warzone dmg reset removes an unreadable settings file");
            Console.WriteLine("Warzone zone command compatibility passed.");
        }
    }
}
