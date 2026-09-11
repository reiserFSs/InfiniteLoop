using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.config;
using AscNet.Table.V2.share.guild.boss;
using AscNet.Table.V2.share.reward;
using AscNet.Table.V2.share.chat;
using AscNet.Table.V2.share.robot;
using MongoDB.Bson;
using MongoDB.Driver;
using Newtonsoft.Json.Linq;
using System.Reflection;
using System.Globalization;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidateGuildBossCompatibility()
    {
        ValidateVersion47ObserverPreFightCompatibility(includeGuild: true);
        using GuildTestScope scope = new();
        LoopbackSessionHarness leader = scope.CreatePlayer();
        LoopbackSessionHarness member = scope.CreatePlayer();
        LoopbackSessionHarness outsider = scope.CreatePlayer();
        Guild seeded = scope.SeedGuild(leader, member);
        long leaderId = leader.Session.player.PlayerData.Id;
        long memberId = member.Session.player.PlayerData.Id;
        Guild LoadGuild() => Guild.FindById(seeded.Id) ?? throw new InvalidDataException("Boss fixture guild disappeared");
        Player LoadPlayer(long uid) => Player.collection.Find(p => p.PlayerData.Id == uid).Single();
        Inventory LoadInventory(long uid) => Inventory.collection.Find(p => p.Uid == uid).Single();
        JObject Call(LoopbackSessionHarness actor, string name, object request)
        {
            JObject response = GuildRpc(actor, name, request);
            GuildAssert(response.Value<int?>("Code") == 0, name + " failed: " + response);
            return response;
        }
        void Reject(LoopbackSessionHarness actor, string name, object request)
        {
            JObject response = GuildRpc(actor, name, request);
            GuildAssert(response.Value<int?>("Code") is int code && code != 0, name + " accepted invalid state/input");
        }
        JObject Activity(LoopbackSessionHarness actor) => Call(actor, "GuildBossActivityRequest", new GuildEmptyRequest());
        var levels = TableReaderV2.Parse<GuildBossLevelTable>().OrderBy(r => r.Level).ToArray();
        var scoreBoxes = TableReaderV2.Parse<GuildBossScoreRewardTable>().OrderBy(r => r.Score).ToArray();
        var hpBoxes = TableReaderV2.Parse<GuildBossHpRewardTable>().OrderByDescending(r => r.HpPercent).ToArray();
        var styles = TableReaderV2.Parse<GuildBossFightStyleTable>().OrderBy(r => r.Id).ToArray();
        var skills = TableReaderV2.Parse<GuildBossFightStyleSkillTable>().ToArray();
        var config = TableReaderV2.Parse<ConfigTable>().ToDictionary(r => r.Key, r => r.Value);
        decimal scoreRatio = decimal.Parse(config["GuildBossScoreCollectionRatio"], CultureInfo.InvariantCulture);
        int uploadCap = int.Parse(config["GuildBossStageUploadCount"], CultureInfo.InvariantCulture);
        GuildAssert(scoreRatio > 0 && uploadCap > 1 && levels.Length > 1, "Boss authority cannot exercise score/upload/difficulty boundaries");

        JObject initial = Activity(leader);
        long activityId = initial.Value<long>("ActivityId");
        int level = initial.Value<int>("BossLevel");
        long hpMax = levels.Single(r => r.Level == level).BossHp;
        GuildAssert(initial.Value<long>("HpMax") == hpMax && initial.Value<long>("HpLeft") == hpMax,
            "Boss initial HP must derive from selected authoritative difficulty");
        var stageList = initial["BossList"]!.ToObject<List<GuildBossStageInfo>>()!;
        var catalog = TableReaderV2.Parse<GuildBossStageCatalogTable>().ToDictionary(r => r.StageId);
        GuildAssert(stageList.All(s => catalog.ContainsKey(s.StageId)), "Boss offers a stage absent from authority");
        int bossStage = stageList.Single(s => s.Type == 3).StageId;
        var rosterRules = TableReaderV2.Parse<GuildBossDataTable>().ToArray();
        var robotGroups = TableReaderV2.Parse<GuildBossStageRobotTable>().ToDictionary(row => row.Id);
        var robotCharacters = TableReaderV2.Parse<RobotTable>().ToDictionary(row => row.Id, row => row.CharacterId);
        var robotGroupIds = rosterRules.SelectMany(rule => rule.Robot.Concat(rule.FixedRobot)).Distinct()
            .SelectMany(group => robotGroups[group].RobotId.Select(id => (Id: id, Group: group)))
            .ToDictionary(pair => pair.Id, pair => pair.Group);
        List<GuildBossRobot> Roster(JObject activity) => activity["RobotList"]!.ToObject<List<GuildBossRobot>>()!;
        void AssertRoster(List<GuildBossRobot> roster)
        {
            GuildAssert(roster.Select(row => row.Type).ToHashSet().SetEquals(rosterRules.Select(row => row.StageType))
                && roster.Count == rosterRules.Length, "Boss roster omitted or duplicated a stage type");
            foreach (var rule in rosterRules)
            {
                var ids = roster.Single(row => row.Type == rule.StageType).RobotIds;
                var groups = ids.Select(id => robotGroupIds[id]).ToArray();
                GuildAssert(ids.Count == rule.RobotCount + rule.FixedRobot.Count
                    && groups.Distinct().Count() == ids.Count
                    && ids.Select(id => robotCharacters[id]).Distinct().Count() == ids.Count,
                    "Boss roster must offer one unique character variant per selected authored group");
                GuildAssert(rule.FixedRobot.All(groups.Contains)
                    && groups.All(group => rule.FixedRobot.Contains(group) || rule.Robot.Contains(group)),
                    "Boss roster omitted a fixed group or invented an unauthored group");
            }
        }
        void FlattenRoster(Guild guild)
        {
            foreach (var roster in guild.Boss.Robots)
                roster.RobotIds = roster.RobotIds.Select(id => robotGroupIds[id]).Distinct()
                    .SelectMany(group => robotGroups[group].RobotId).ToList();
            guild.SaveChecked();
        }
        var initialRoster = Roster(initial);
        AssertRoster(initialRoster);
        FlattenRoster(LoadGuild());
        var repairedRoster = Roster(Activity(member));
        AssertRoster(repairedRoster);
        foreach (var roster in initialRoster)
            GuildAssert(repairedRoster.Single(row => row.Type == roster.Type).RobotIds.SequenceEqual(roster.RobotIds),
                "Repairing the flattened catalog changed this period's selected characters or variants");
        GuildAssert(JToken.DeepEquals(JToken.FromObject(repairedRoster), JToken.FromObject(LoadGuild().Boss.Robots)),
            "Activity roster repair did not persist");

        var reconcileRobots = RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.GuildBossModule"),
            "ReconcileRobots", BindingFlags.Static | BindingFlags.NonPublic, [typeof(GuildBossState)]);
        GuildBossState firstPeriodRoster = new() { Period = activityId };
        GuildBossState secondPeriodRoster = new() { Period = activityId + 1 };
        reconcileRobots.Invoke(null, [firstPeriodRoster]);
        reconcileRobots.Invoke(null, [secondPeriodRoster]);
        AssertRoster(firstPeriodRoster.Robots);
        AssertRoster(secondPeriodRoster.Robots);
        foreach (var rule in rosterRules.Where(row => row.FixedRobot.Count > 0))
        {
            foreach (int group in rule.FixedRobot.Where(group => robotGroups[group].RobotId.Count > 1))
            {
                var variants = robotGroups[group].RobotId;
                int firstVariant = firstPeriodRoster.Robots.Single(row => row.Type == rule.StageType).RobotIds.Single(variants.Contains);
                int secondVariant = secondPeriodRoster.Robots.Single(row => row.Type == rule.StageType).RobotIds.Single(variants.Contains);
                GuildAssert(firstVariant != secondVariant && robotCharacters[firstVariant] == robotCharacters[secondVariant],
                    "Distinct periods must rotate a fixed group's variant without changing its character");
            }
        }
        JObject info = Call(leader, "GuildBossInfoRequest", new GuildBossInfoRequest());
        GuildAssert(info.Value<long>("ActivityId") == activityId && info.Value<long>("HpLeft") == hpMax
            && info.Value<long>("EndTime") > DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "Boss info/activity clock and HP disagree");
        Call(leader, "GuildBossStageRequest", new GuildBossStageRequest { StageId = bossStage });
        Reject(leader, "GuildBossStageRequest", new GuildBossStageRequest { StageId = int.MaxValue });

        (string Name, object Request)[] membershipRequests =
        [
            ("GuildBossInfoRequest", new GuildBossInfoRequest()),
            ("GuildBossActivityRequest", new GuildEmptyRequest()),
            ("GuildBossStageRequest", new GuildBossStageRequest { StageId = bossStage }),
            ("GuildBossPlayerRankRequest", new GuildEmptyRequest()),
            ("GuildBossGuildRankRequest", new GuildEmptyRequest()),
            ("GuildBossPlayerStageRankRequest", new GuildBossPlayerStageRankRequest { StageId = bossStage }),
            ("GuildBossScoreBoxRequest", new GuildBossScoreBoxRequest { BoxId = scoreBoxes[0].Id }),
            ("GuildBossHpBoxRequest", new GuildBossHpBoxRequest { BoxId = hpBoxes[0].Id }),
            ("GuildBossLevelRequest", new GuildBossLevelRequest { ActivityId = activityId, BossLevelNext = level }),
            ("GuildBossSetOrderRequest", new GuildBossSetOrderRequest { ActivityId = activityId, OrderList = initial["OrderList"]!.ToObject<List<int>>()! }),
            ("GuildBossUploadRequest", new GuildBossUploadRequest { StageId = bossStage }),
            ("GuildBossGetAllBossRewardRequest", new GuildEmptyRequest()),
            ("GuildFightStyleRequest", new GuildEmptyRequest()),
            ("GuildSelectFightStyleRequest", new GuildSelectFightStyleRequest { StyleId = styles[0].Id }),
            ("GuildSelectFightStyleSkillRequest", new GuildSelectFightStyleSkillRequest { OperType = 1, SkillId = skills[0].Id })
        ];
        foreach (var request in membershipRequests) Reject(outsider, request.Name, request.Request);
        GuildAssert(Guild.FindByMember(outsider.Session.player.PlayerData.Id) is null, "Boss requests enrolled an outsider");

        var nextLevel = levels.First(r => r.Level > level);
        Reject(member, "GuildBossLevelRequest", new GuildBossLevelRequest { ActivityId = activityId, BossLevelNext = nextLevel.Level });
        Reject(leader, "GuildBossLevelRequest", new GuildBossLevelRequest { ActivityId = activityId, BossLevelNext = nextLevel.Level });
        Reject(leader, "GuildBossLevelRequest", new GuildBossLevelRequest { ActivityId = activityId - 1, BossLevelNext = level });
        Guild unlocked = LoadGuild();
        unlocked.Boss.GuildScoreSumBest = Convert.ToInt64(nextLevel.UnlockScore);
        unlocked.SaveChecked();
        Call(leader, "GuildBossLevelRequest", new GuildBossLevelRequest { ActivityId = activityId, BossLevelNext = nextLevel.Level });
        JObject selectedDifficulty = Activity(member);
        GuildAssert(selectedDifficulty.Value<int>("BossLevel") == level && selectedDifficulty.Value<int>("BossLevelNext") == nextLevel.Level
            && selectedDifficulty.Value<long>("HpLeft") == hpMax, "Next difficulty changed current encounter or was not shared");

        List<int> orders = stageList.Where(s => s.Type != 3).GroupBy(s => s.Type)
            .SelectMany(group => Enumerable.Range(1, group.Count()).Reverse()).ToList();
        Reject(member, "GuildBossSetOrderRequest", new GuildBossSetOrderRequest { ActivityId = activityId, OrderList = orders });
        Reject(leader, "GuildBossSetOrderRequest", new GuildBossSetOrderRequest { ActivityId = activityId, OrderList = [-1] });
        Call(leader, "GuildBossSetOrderRequest", new GuildBossSetOrderRequest { ActivityId = activityId, OrderList = orders });
        GuildAssert(Activity(member)["OrderList"]!.Values<int>().SequenceEqual(orders), "Tactical order not visible to another member");

        Call(leader, "GuildSelectFightStyleRequest", new GuildSelectFightStyleRequest { StyleId = styles[0].Id });
        Reject(leader, "GuildSelectFightStyleRequest", new GuildSelectFightStyleRequest { StyleId = int.MaxValue });
        Reject(leader, "GuildSelectFightStyleRequest", new GuildSelectFightStyleRequest { StyleId = styles[1].Id });
        var chosenSkill = skills.First(s => s.Style == styles[0].Id && s.IsPermanent != 1 && s.IsCore != 1);
        Call(leader, "GuildSelectFightStyleSkillRequest", new GuildSelectFightStyleSkillRequest { OperType = 1, SkillId = chosenSkill.Id });
        JObject selectedStyle = Call(leader, "GuildFightStyleRequest", new GuildEmptyRequest());
        GuildAssert(selectedStyle["FightStyle"]!.Value<int>("StyleId") == styles[0].Id
            && selectedStyle["FightStyle"]!["EffectedSkillId"]!.Values<int>().Contains(chosenSkill.Id), "Selected boss skill not visible");
        Reject(leader, "GuildSelectFightStyleSkillRequest", new GuildSelectFightStyleSkillRequest
        { OperType = 1, SkillId = skills.First(s => s.Style != styles[0].Id && s.IsPermanent != 1).Id });
        Call(leader, "GuildSelectFightStyleSkillRequest", new GuildSelectFightStyleSkillRequest { OperType = 2, SkillId = chosenSkill.Id });
        GuildAssert(!Call(leader, "GuildFightStyleRequest", new GuildEmptyRequest())["FightStyle"]!["EffectedSkillId"]!.Values<int>().Contains(chosenSkill.Id),
            "Uninstalled boss skill remains active");

        int[] supportRobots = initialRoster.Single(row => row.Type == 3).RobotIds
            .Where(id => robotCharacters[id] != leader.Session.character.Characters.First().Id
                && robotCharacters[id] != member.Session.character.Characters.First().Id).Take(2).ToArray();
        GuildAssert(supportRobots.Length == 2, "Boss fixture needs two distinct support robots");
        // XGuildBossManager.PreFight sends only these five fields, without ChallengeCount.
        Dictionary<string, object> clientPreFightData = new()
        {
            ["CardIds"] = new uint[] { checked((uint)leader.Session.character.Characters.First().Id), 0, 0 },
            ["StageId"] = checked((uint)bossStage),
            ["CaptainPos"] = 1,
            ["FirstFightPos"] = 1,
            ["RobotIds"] = new int[] { 0, supportRobots[0], supportRobots[1] }
        };
        Dictionary<string, object> clientPreFight = new() { ["PreFightData"] = clientPreFightData };
        JObject clientFight = Call(leader, nameof(PreFightRequest), clientPreFight);
        long clientFightId = clientFight["FightData"]!.Value<long>("FightId");
        GuildAssert(clientFightId > 0 && clientFight["FightData"]!.Value<int>("StageId") == bossStage
            && leader.Session.fight?.PreFight.PreFightData.ChallengeCount == 1,
            "Client-shaped guild pre-fight without ChallengeCount must start one attempt");
        JObject clientRetry = Call(leader, nameof(PreFightRequest), clientPreFight);
        GuildAssert(clientRetry["FightData"]!.Value<long>("FightId") == clientFightId
            && leader.Session.fight?.PreFight.PreFightData.ChallengeCount == 1,
            "Omitted-count guild pre-fight retry must reuse the single attempt");
        clientPreFightData["ChallengeCount"] = 1;
        JObject explicitRetry = Call(leader, nameof(PreFightRequest), clientPreFight);
        GuildAssert(explicitRetry["FightData"]!.Value<long>("FightId") == clientFightId
            && leader.Session.fight?.PreFight.PreFightData.ChallengeCount == 1,
            "Explicit single-count retry must reuse the client-shaped attempt");
        byte[] preFightBoss = LoadGuild().Boss.ToBson();
        byte[] preFightPlayer = LoadPlayer(leaderId).GuildState.ToBson();
        byte[] preFightInventory = LoadInventory(leaderId).ToBson();
        int wrongStage = catalog.Keys.First(id => stageList.All(stage => stage.StageId != id));
        foreach (var invalid in new[] { (Count: -1, Stage: bossStage), (Count: 2, Stage: bossStage), (Count: 0, Stage: wrongStage) })
        {
            clientPreFightData["ChallengeCount"] = invalid.Count;
            clientPreFightData["StageId"] = checked((uint)invalid.Stage);
            Reject(leader, nameof(PreFightRequest), clientPreFight);
            GuildAssert(preFightBoss.SequenceEqual(LoadGuild().Boss.ToBson())
                && preFightPlayer.SequenceEqual(LoadPlayer(leaderId).GuildState.ToBson())
                && preFightInventory.SequenceEqual(LoadInventory(leaderId).ToBson())
                && leader.Session.fight?.FightId == clientFightId
                && leader.Session.fight.PreFight.PreFightData.ChallengeCount == 1,
                "Invalid guild pre-fight mutated the attempt, participation or rewards");
        }

        FightSettleRequest StartFight(LoopbackSessionHarness actor, long score, int fightStage = 0, int contributors = 0)
        {
            if (fightStage == 0) fightStage = bossStage;
            Activity(actor);
            byte[] inventoryBefore = LoadInventory(actor.Session.player.PlayerData.Id).ToBson();
            byte[] stageBefore = Stage.collection.Find(s => s.Uid == actor.Session.player.PlayerData.Id).Single().ToBson();
            int characterId = checked((int)actor.Session.character.Characters.First().Id);
            int[] team = contributors == 0 ? [characterId]
                : [characterId, robotCharacters[supportRobots[0]], robotCharacters[supportRobots[1]]];
            JObject pre = Call(actor, nameof(PreFightRequest), new PreFightRequest
            {
                PreFightData = new()
                {
                    StageId = checked((uint)fightStage),
                    ChallengeCount = 1,
                    CardIds = [checked((uint)characterId), 0, 0],
                    RobotIds = contributors == 0 ? [0, 0, 0] : [0, supportRobots[0], supportRobots[1]],
                    CaptainPos = 1,
                    FirstFightPos = 1
                }
            });
            GuildAssert(inventoryBefore.SequenceEqual(LoadInventory(actor.Session.player.PlayerData.Id).ToBson())
                && stageBefore.SequenceEqual(Stage.collection.Find(s => s.Uid == actor.Session.player.PlayerData.Id).Single().ToBson()),
                "Guild pre-fight adapter charged ordinary stamina or changed ordinary stages");
            var settle = CreateMissingStageSettleRequest(checked((uint)fightStage), pre["FightData"]!.Value<long>("FightId"), actor.Session.player.PlayerData.Id);
            settle.Result.StartFrame = 0;
            settle.Result.SettleFrame = 20;
            settle.Result.TotalDamage = checked((long)decimal.Ceiling(score / scoreRatio));
            settle.Result.NpcHpInfo = team.Select((id, index) => (id, index)).ToDictionary(row => row.index + 1,
                row => new NpcHp
                {
                    Type = 1,
                    CharacterId = row.id,
                    BuffIds = [],
                    AttrTable = new()
                    { [1] = new Dictionary<string, object> { ["Value"] = 100, ["MaxValue"] = 100 } }
                });
            settle.Result.NpcDpsTable = new()
            {
                [1] = new NpcDpsTable
                {
                    CharacterId = characterId,
                    RoleId = checked((int)actor.Session.player.PlayerData.Id),
                    DamageTotal = settle.Result.TotalDamage
                }
            };
            if (contributors == 2)
            {
                long secondDamage = settle.Result.TotalDamage / 2;
                settle.Result.NpcDpsTable[1].DamageTotal -= secondDamage;
                settle.Result.NpcDpsTable[2] = new NpcDpsTable
                {
                    CharacterId = team[1],
                    RoleId = checked((int)actor.Session.player.PlayerData.Id),
                    DamageTotal = secondDamage
                };
            }
            return settle;
        }
        // Native GetFightsResultsBytes DPS entries omit Type (unlike NpcHpInfo).
        Dictionary<string, object> ClientSettle(FightSettleRequest settle)
        {
            var result = typeof(FightSettleResult).GetProperties().ToDictionary(property => property.Name,
                property => property.GetValue(settle.Result)!);
            result["NpcDpsTable"] = settle.Result.NpcDpsTable.ToDictionary(pair => pair.Key, pair => new
            {
                pair.Value.RoleId,
                pair.Value.NpcId,
                pair.Value.CharacterId,
                pair.Value.DamageTotal,
                pair.Value.DamageNormal,
                pair.Value.DamageMagic,
                pair.Value.BreakEndure,
                pair.Value.Cure,
                pair.Value.Hurt
            });
            return new() { ["Result"] = result };
        }
        JObject Candidate(LoopbackSessionHarness actor, FightSettleRequest settle)
        {
            byte[] inventoryBefore = LoadInventory(actor.Session.player.PlayerData.Id).ToBson();
            byte[] stageBefore = Stage.collection.Find(s => s.Uid == actor.Session.player.PlayerData.Id).Single().ToBson();
            JObject before = Activity(actor);
            JObject result = Call(actor, nameof(FightSettleRequest), ClientSettle(settle));
            JToken candidate = result["Settle"]!["GuildBossFightResult"]!;
            long expectedScore = checked((long)decimal.Floor(settle.Result.TotalDamage * scoreRatio));
            GuildAssert(candidate.Value<long>("Damage") == settle.Result.TotalDamage && candidate.Value<long>("TotalScore") == expectedScore,
                "Guild candidate score not derived from validated damage and authority ratio");
            GuildAssert(result["Settle"]!.Value<int>("StageId") == settle.Result.StageId
                && result["Settle"]!.Value<bool>("IsWin") == (settle.Result.IsWin && !settle.Result.IsForceExit)
                && result["Settle"]!.Value<int>("ChallengeCount") == 1,
                "Client-shaped settlement lost its stage, outcome or normalized single attempt");
            JObject after = Activity(actor);
            GuildAssert(before.Value<long>("HpLeft") == after.Value<long>("HpLeft") && before.Value<long>("GuildScoreSum") == after.Value<long>("GuildScoreSum")
                && JToken.DeepEquals(before["BossList"], after["BossList"]), "Settle committed score/HP before upload");
            GuildAssert(inventoryBefore.SequenceEqual(LoadInventory(actor.Session.player.PlayerData.Id).ToBson())
                && stageBefore.SequenceEqual(Stage.collection.Find(s => s.Uid == actor.Session.player.PlayerData.Id).Single().ToBson()),
                "Guild generic fight adapter awarded ordinary rewards or ordinary stage progression");
            GuildAssert(LoadPlayer(actor.Session.player.PlayerData.Id).GuildState.Boss.Attempt is { Settled: true, Uploaded: false }, "Candidate did not persist before response");
            return result;
        }

        Reject(leader, "GuildBossUploadRequest", new GuildBossUploadRequest { StageId = bossStage });
        long firstScore = Math.Max(1, hpMax / 10);
        FightSettleRequest firstFight = StartFight(leader, firstScore, contributors: 1);
        GuildAssert(firstFight.Result.FightId == clientFightId, "Settlement must exercise the original omitted-count attempt");
        void RejectFirstSettlement()
        {
            byte[] inventory = LoadInventory(leaderId).ToBson();
            byte[] player = LoadPlayer(leaderId).GuildState.Boss.ToBson();
            byte[] guild = LoadGuild().Boss.ToBson();
            Reject(leader, nameof(FightSettleRequest), ClientSettle(firstFight));
            GuildAssert(inventory.SequenceEqual(LoadInventory(leaderId).ToBson())
                && player.SequenceEqual(LoadPlayer(leaderId).GuildState.Boss.ToBson())
                && guild.SequenceEqual(LoadGuild().Boss.ToBson()),
                "Invalid sparse settlement changed persisted participation, score or rewards");
        }
        firstFight.Result.FightId++;
        Reject(leader, nameof(FightSettleRequest), ClientSettle(firstFight));
        firstFight.Result.FightId--;
        firstFight.Result.StageId = checked((uint)stageList.First(stage => stage.StageId != bossStage).StageId);
        Reject(leader, nameof(FightSettleRequest), ClientSettle(firstFight));
        firstFight.Result.StageId = checked((uint)bossStage);
        firstFight.Result.TotalDamage = -1;
        Reject(leader, nameof(FightSettleRequest), firstFight);
        firstFight.Result.TotalDamage = checked((long)decimal.Ceiling(firstScore / scoreRatio));
        firstFight.Result.TotalDamage++;
        RejectFirstSettlement();
        firstFight.Result.TotalDamage--;
        firstFight.Result.NpcHpInfo[1].AttrTable[1] = new Dictionary<string, object> { ["Value"] = 101, ["MaxValue"] = 100 };
        Reject(leader, nameof(FightSettleRequest), firstFight);
        firstFight.Result.NpcHpInfo[1].AttrTable[1] = new Dictionary<string, object> { ["Value"] = 100, ["MaxValue"] = 100 };
        firstFight.Result.PlayerIds = [memberId];
        Reject(leader, nameof(FightSettleRequest), firstFight);
        firstFight.Result.PlayerIds = [leaderId];
        firstFight.Result.TotalDamage = checked((long)decimal.Floor(hpMax / scoreRatio) + 1);
        firstFight.Result.NpcDpsTable[1].DamageTotal = firstFight.Result.TotalDamage;
        Reject(leader, nameof(FightSettleRequest), firstFight);
        firstFight.Result.TotalDamage = checked((long)decimal.Ceiling(firstScore / scoreRatio));
        firstFight.Result.NpcDpsTable[1].DamageTotal = firstFight.Result.TotalDamage;
        firstFight.Result.NpcDpsTable[1].RoleId = checked((int)memberId);
        RejectFirstSettlement();
        firstFight.Result.NpcDpsTable[1].RoleId = checked((int)leaderId);
        int firstCharacter = firstFight.Result.NpcDpsTable[1].CharacterId;
        firstFight.Result.NpcDpsTable[1].CharacterId = int.MaxValue;
        RejectFirstSettlement();
        firstFight.Result.NpcDpsTable[1].CharacterId = firstCharacter;
        firstFight.Result.NpcDpsTable[2] = new NpcDpsTable { RoleId = checked((int)leaderId), CharacterId = firstCharacter };
        RejectFirstSettlement();
        firstFight.Result.NpcDpsTable.Remove(2);
        firstFight.Result.NpcDpsTable[2] = new NpcDpsTable { RoleId = 0, CharacterId = 0, Cure = 1, DamageTotal = 0 };
        JObject firstCandidate = Candidate(leader, firstFight);
        byte[] settledInventory = LoadInventory(leaderId).ToBson();
        byte[] settledPlayer = LoadPlayer(leaderId).GuildState.Boss.ToBson();
        byte[] settledGuild = LoadGuild().Boss.ToBson();
        JObject repeatedCandidate = Call(leader, nameof(FightSettleRequest), ClientSettle(firstFight));
        GuildAssert(JToken.DeepEquals(firstCandidate["Settle"], repeatedCandidate["Settle"]), "Identical settle retry changed frozen candidate");
        GuildAssert(settledInventory.SequenceEqual(LoadInventory(leaderId).ToBson())
            && settledPlayer.SequenceEqual(LoadPlayer(leaderId).GuildState.Boss.ToBson())
            && settledGuild.SequenceEqual(LoadGuild().Boss.ToBson()),
            "Identical native settlement retry changed participation or awarded rewards");
        firstFight.Result.TotalDamage++;
        Reject(leader, nameof(FightSettleRequest), ClientSettle(firstFight));
        firstFight.Result.TotalDamage--;
        Reject(member, "GuildBossUploadRequest", new GuildBossUploadRequest { StageId = bossStage });
        Reject(leader, "GuildBossUploadRequest", new GuildBossUploadRequest { StageId = stageList.First(s => s.Type != 3).StageId });
        LoopbackSessionHarness resumed = scope.OpenPlayer(leaderId);
        JObject uploaded = Call(resumed, "GuildBossUploadRequest", new GuildBossUploadRequest { StageId = bossStage });
        GuildAssert(uploaded.Value<long>("SubHp") == firstScore && Activity(member).Value<long>("HpLeft") == hpMax - firstScore,
            "Resumed upload did not apply candidate exactly once to shared HP");
        GuildAssert(LoadGuild().Boss.Participants.Single(p => p.PlayerId == leaderId).Stages.Single(s => s.StageId == bossStage).UploadCount == 1,
            "Omitted-count attempt must consume exactly one upload");
        byte[] committed = LoadGuild().Boss.ToBson();
        Reject(resumed, "GuildBossUploadRequest", new GuildBossUploadRequest { StageId = bossStage });
        GuildAssert(committed.SequenceEqual(LoadGuild().Boss.ToBson()), "Duplicate upload changed shared boss state");

        long memberScore = firstScore * 2;
        FightSettleRequest memberFight = StartFight(member, memberScore, contributors: 2);
        JObject memberCandidate = Candidate(member, memberFight);
        byte[] memberSettled = LoadPlayer(memberId).GuildState.Boss.ToBson();
        byte[] memberInventory = LoadInventory(memberId).ToBson();
        byte[] memberGuild = LoadGuild().Boss.ToBson();
        JObject memberRetry = Call(member, nameof(FightSettleRequest), ClientSettle(memberFight));
        GuildAssert(JToken.DeepEquals(memberCandidate["Settle"], memberRetry["Settle"])
            && memberSettled.SequenceEqual(LoadPlayer(memberId).GuildState.Boss.ToBson())
            && memberInventory.SequenceEqual(LoadInventory(memberId).ToBson())
            && memberGuild.SequenceEqual(LoadGuild().Boss.ToBson()),
            "Two-contributor native settlement retry changed candidate, participation or rewards");
        Call(member, "GuildBossUploadRequest", new GuildBossUploadRequest { StageId = bossStage });
        GuildAssert(Activity(resumed).Value<long>("HpLeft") == hpMax - firstScore - memberScore, "Two-player uploads lost or double-counted damage");
        Candidate(member, StartFight(member, firstScore));
        committed = LoadGuild().Boss.ToBson();
        Reject(member, "GuildBossUploadRequest", new GuildBossUploadRequest { StageId = bossStage });
        GuildAssert(committed.SequenceEqual(LoadGuild().Boss.ToBson()), "Lower score upload changed best score, HP or upload count");
        for (int count = 2; count <= uploadCap; count++)
        {
            long improved = firstScore + count;
            Candidate(resumed, StartFight(resumed, improved));
            long before = Activity(member).Value<long>("HpLeft");
            JObject improvement = Call(resumed, "GuildBossUploadRequest", new GuildBossUploadRequest { StageId = bossStage });
            long delta = count == 2 ? 2 : 1;
            GuildAssert(improvement.Value<long>("SubHp") == delta && Activity(member).Value<long>("HpLeft") == before - delta,
                "Improved upload applied full score instead of score delta");
        }
        Reject(resumed, nameof(PreFightRequest), new PreFightRequest
        {
            PreFightData = new() { StageId = checked((uint)bossStage), ChallengeCount = 1, CardIds = [checked((uint)resumed.Session.character.Characters.First().Id)], RobotIds = [0], CaptainPos = 1, FirstFightPos = 1 }
        });
        JObject ranks = Call(resumed, "GuildBossPlayerRankRequest", new GuildEmptyRequest());
        GuildAssert(ranks["RankList"]!.Select(r => r.Value<long>("Id")).ToHashSet().SetEquals(new[] { leaderId, memberId }), "Boss member rank manufactured or omitted participants");
        JObject stageRanks = Call(resumed, "GuildBossPlayerStageRankRequest", new GuildBossPlayerStageRankRequest { StageId = bossStage });
        GuildAssert(stageRanks["RankList"]!.First()!.Value<long>("Id") == memberId, "Stage rank is not ordered by committed score");
        JObject guildRanks = Call(resumed, "GuildBossGuildRankRequest", new GuildEmptyRequest());
        GuildAssert(guildRanks["MyRank"]!.Value<uint>("Id") == seeded.Id
            && guildRanks["MyRank"]!.Value<long>("Score") == Activity(resumed).Value<long>("GuildScoreSum"), "Guild rank not derived from local committed participants");

        // Earn the death transition through combat before seeding the separate score-box boundary.
        Reject(resumed, "GuildBossScoreBoxRequest", new GuildBossScoreBoxRequest { BoxId = scoreBoxes.Last().Id });
        Reject(resumed, "GuildBossHpBoxRequest", new GuildBossHpBoxRequest { BoxId = hpBoxes.Last().Id });
        Candidate(member, StartFight(member, hpMax));
        long beforeKill = LoadGuild().Boss.HpLeft;
        JObject killingUpload = Call(member, "GuildBossUploadRequest", new GuildBossUploadRequest { StageId = bossStage });
        int deathBonus = int.Parse(config["GuildBossDeathAddScore"], CultureInfo.InvariantCulture);
        GuildAssert(killingUpload.Value<long>("SubHp") == beforeKill && LoadGuild().Boss.HpLeft == 0
            && LoadGuild().Boss.Participants.All(p => p.DeathBonus && p.BonusScore == deathBonus),
            "Killing upload did not clamp shared HP and grant the authored death bonus once to both participants");
        committed = LoadGuild().Boss.ToBson();
        Reject(member, "GuildBossUploadRequest", new GuildBossUploadRequest { StageId = bossStage });
        GuildAssert(committed.SequenceEqual(LoadGuild().Boss.ToBson()), "Repeated killing upload duplicated the death bonus");
        // Explicit isolated score threshold fixture, never a captured successful reward/upload.
        Guild rewards = LoadGuild();
        rewards.Boss.Participants.Single(p => p.PlayerId == leaderId).Stages.Single(s => s.StageId == bossStage).Score = scoreBoxes.Max(r => r.Score);
        rewards.SaveChecked();
        long rewardEpoch = long.Parse(config["GuildBossNewRewardDate"], CultureInfo.InvariantCulture);
        var buildTimeControls = RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule"),
            "BuildTimeLimitControlConfigList", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
            [typeof(DateTimeOffset), typeof(bool)]);
        var rewardTimeIds = scoreBoxes.Select(r => r.TimeId).Concat(hpBoxes.Select(r => r.TimeId)).Distinct().ToArray();
        int styleTimeId = int.Parse(config["GuildBossThirdVersionTimeId"], CultureInfo.InvariantCulture);
        foreach (long timestamp in new[] { rewardEpoch - 1, rewardEpoch })
        {
            var controls = (List<TimeLimitCtrlConfigList>)(buildTimeControls.Invoke(null,
                [DateTimeOffset.FromUnixTimeSeconds(timestamp), false])
                ?? throw new InvalidDataException("Account omitted its client time controls"));
            foreach (int timeId in rewardTimeIds)
            {
                TimeLimitCtrlConfigList marker = controls.Single(row => row.Id == timeId);
                GuildAssert(marker.StartTime == rewardEpoch && marker.EndTime == 0,
                    "Boss reward clock control did not advertise its authoritative epoch without an invented expiry");
                bool clientUsesNewReward = timestamp >= marker.StartTime && (marker.EndTime == 0 || timestamp < marker.EndTime);
                GuildAssert(clientUsesNewReward == (timestamp == rewardEpoch),
                    "Boss client reward marker switches at the wrong side of the source epoch");
            }
            TimeLimitCtrlConfigList styleMarker = controls.Single(row => row.Id == styleTimeId);
            GuildAssert(styleMarker.StartTime == 0 && styleMarker.EndTime == 0,
                "Current boss style version was hidden behind an invented calendar");
        }
        bool newRewards = DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= rewardEpoch;
        var boxRewardIds = scoreBoxes.Select(r => newRewards ? r.NewRewardId : r.RewardId)
            .Concat(hpBoxes.Select(r => newRewards ? r.NewRewardId : r.RewardId)).ToHashSet();
        var allowedGoodsIds = TableReaderV2.Parse<RewardTable>().Where(r => boxRewardIds.Contains(r.Id)).SelectMany(r => r.SubIds).ToHashSet();
        var allowedGoods = TableReaderV2.Parse<RewardGoodsTable>().Where(r => allowedGoodsIds.Contains(r.Id)).ToArray();
        long[] RewardEconomySnapshot()
        {
            Guild guild = LoadGuild();
            return [guild.ContributeLeft,
                guild.GiftContribute,
                guild.Build,
                guild.TalentPoint,
                guild.TalentPointFromBuild,
                guild.ShopCoin,
                guild.Members.Values.Sum(m => (long)m.WeekContribute),
                guild.Members.Values.Sum(m => (long)m.TotalContribute),
                guild.Members.Values.Sum(m => (long)m.ActiveContribute)];
        }
        void Claim(string name, object request)
        {
            var before = LoadInventory(leaderId).Items.ToDictionary(i => i.Id, i => i.Count);
            Guild guildBefore = LoadGuild();
            JObject reward = Call(resumed, name, request);
            var goods = reward["RewardGoods"]!.ToObject<List<RewardGoods>>()!;
            GuildAssert(goods.Any(g => g.Count > 0), name + " granted no earned reward");
            GuildAssert(goods.All(g => allowedGoods.Any(row => row.Id == g.Id && row.TemplateId == g.TemplateId && row.Count == g.Count)),
                name + " returned rewards absent from the authored boss reward tables");
            Inventory after = LoadInventory(leaderId);
            Guild guildAfter = LoadGuild();
            foreach (var item in goods.Where(g => g.RewardType == (int)RewardType.Item).GroupBy(g => g.TemplateId))
            {
                long count = item.Sum(g => (long)g.Count);
                switch (item.Key)
                {
                    case 38:
                        GuildMemberState memberBefore = guildBefore.Members[leaderId];
                        GuildMemberState memberAfter = guildAfter.Members[leaderId];
                        GuildAssert(guildAfter.ContributeLeft == guildBefore.ContributeLeft + count
                            && guildAfter.GiftContribute == guildBefore.GiftContribute + count
                            && memberAfter.WeekContribute == memberBefore.WeekContribute + count
                            && memberAfter.TotalContribute == memberBefore.TotalContribute + count
                            && memberAfter.ActiveContribute == memberBefore.ActiveContribute + count,
                            name + " did not credit authored contribution to the guild and claiming member");
                        break;
                    case 40:
                        GuildAssert(guildBefore.Level < TableReaderV2.Parse<AscNet.Table.V2.share.guild.GuildLevelTable>().Max(row => row.Level),
                            "Boss reward fixture unexpectedly reached maximum guild level");
                        GuildAssert(guildAfter.Build == guildBefore.Build + count, name + " did not credit authored guild build");
                        break;
                    case 62723:
                        long shopCap = long.Parse(config["GuildShopCoinLimit"], CultureInfo.InvariantCulture);
                        GuildAssert(guildAfter.ShopCoin == Math.Min(shopCap, guildBefore.ShopCoin + count),
                            name + " did not credit authored guild shop coins up to their configured cap");
                        break;
                    default:
                        GuildAssert(after.Items.Single(i => i.Id == item.Key).Count == before.GetValueOrDefault(item.Key) + count,
                            name + " did not credit authored player inventory goods");
                        break;
                }
            }
        }
        Claim("GuildBossScoreBoxRequest", new GuildBossScoreBoxRequest { BoxId = scoreBoxes[0].Id });
        Claim("GuildBossHpBoxRequest", new GuildBossHpBoxRequest { BoxId = hpBoxes[0].Id });
        byte[] claimed = LoadInventory(leaderId).ToBson();
        long[] claimedEconomy = RewardEconomySnapshot();
        Reject(resumed, "GuildBossScoreBoxRequest", new GuildBossScoreBoxRequest { BoxId = scoreBoxes[0].Id });
        Reject(resumed, "GuildBossHpBoxRequest", new GuildBossHpBoxRequest { BoxId = hpBoxes[0].Id });
        GuildAssert(claimed.SequenceEqual(LoadInventory(leaderId).ToBson()), "Repeated box claim duplicated rewards");
        GuildAssert(claimedEconomy.SequenceEqual(RewardEconomySnapshot()), "Repeated box claim duplicated guild/member economic rewards");
        Claim("GuildBossGetAllBossRewardRequest", new GuildEmptyRequest());
        JObject allClaimed = Activity(scope.OpenPlayer(leaderId));
        GuildAssert(allClaimed["ScoreBoxGot"]!.Values<int>().ToHashSet().SetEquals(scoreBoxes.Select(r => r.Id))
            && allClaimed["HpBoxGotNew"]!.Values<int>().ToHashSet().SetEquals(hpBoxes.Select(r => r.Id)), "Receive-all receipts did not survive reload");
        claimed = LoadInventory(leaderId).ToBson();
        claimedEconomy = RewardEconomySnapshot();
        GuildRpc(resumed, "GuildBossGetAllBossRewardRequest", new GuildEmptyRequest());
        GuildAssert(claimed.SequenceEqual(LoadInventory(leaderId).ToBson()), "Receive-all retry duplicated rewards");
        GuildAssert(claimedEconomy.SequenceEqual(RewardEconomySnapshot()), "Receive-all retry duplicated guild/member economic rewards");

        Guild prior = LoadGuild();
        long archivedScore = prior.Boss.Participants.Sum(p => p.BonusScore + p.Stages.Sum(s => s.Score));
        long previousPeriod = activityId - 1; // WeeklyPeriod is an integer week index, not a Unix timestamp.
        prior.Boss.Period = previousPeriod;
        prior.SaveChecked();
        byte[] beforeRankMail = LoadInventory(leaderId).ToBson();
        JObject rolled = Activity(resumed);
        Guild afterRollover = LoadGuild();
        GuildBossArchiveState archive = afterRollover.Boss.Archives.Single(a => a.Period == previousPeriod);
        GuildAssert(archive.HpLeft == 0 && archive.Score == archivedScore && archive.Participants.Any(p => p.PlayerId == leaderId && p.ScoreBoxGot.Count == scoreBoxes.Length),
            "Weekly rollover lost previous HP, scores or reward receipts");
        GuildAssert(rolled.Value<int>("BossLevel") == nextLevel.Level && rolled.Value<long>("HpMax") == nextLevel.BossHp
            && rolled.Value<long>("HpLeft") == nextLevel.BossHp && rolled.Value<long>("PlayerFinalScore") == 0
            && !rolled["ScoreBoxGot"]!.Any() && !rolled["HpBoxGotNew"]!.Any(), "Weekly rollover did not apply selected difficulty and reset participation");
        GuildAssert(afterRollover.MemberIds.ToHashSet().SetEquals(new[] { leaderId, memberId }), "Mode rollover altered real membership");
        Reject(resumed, "GuildBossUploadRequest", new GuildBossUploadRequest { StageId = bossStage });
        int archiveCount = afterRollover.Boss.Archives.Count;
        byte[] rolloverInventory = LoadInventory(leaderId).ToBson();
        Activity(scope.OpenPlayer(leaderId));
        GuildAssert(LoadGuild().Boss.Archives.Count == archiveCount && rolloverInventory.SequenceEqual(LoadInventory(leaderId).ToBson()),
            "Weekly archive/reward settlement repeated on reload");
        GuildAssert(LoadGuild().Boss.Archives.Single(a => a.Period == previousPeriod).RewardedPlayerIds.Contains(leaderId),
            "Weekly rank/box settlement did not record its durable completion");
        string rankClaim = $"guild-boss:{seeded.Id}:{leaderId}:rank:{previousPeriod}";
        PlayerMail rankMail = LoadPlayer(leaderId).Mails.Single(mail => mail.RewardClaimKey == rankClaim);
        GuildAssert(rankMail.Id == rankClaim && rankMail.Status != 3
            && beforeRankMail.SequenceEqual(LoadInventory(leaderId).ToBson()),
            "Weekly rank reward must arrive as one unclaimed mail, not immediate inventory credit");
        var rankRule = TableReaderV2.Parse<GuildBossRankRewardTable>().Single(row => row.RewardId == archive.RankRewardId);
        var rankGoodsIds = TableReaderV2.Parse<RewardTable>().Single(row => row.Id == rankRule.RewardId).SubIds.ToHashSet();
        var rankGoods = TableReaderV2.Parse<RewardGoodsTable>().Where(row => rankGoodsIds.Contains(row.Id)).ToArray();
        GuildAssert(rankMail.RewardGoodsList is not null && rankMail.RewardGoodsList.Count == rankGoods.Length
            && rankGoods.All(row => rankMail.RewardGoodsList.Any(g => g.Id == row.Id
                && g.TemplateId == row.TemplateId && g.Count == row.Count)), "Rank mail attachments differ from frozen authoritative rank rewards");
        LoopbackSessionHarness mailSession = scope.OpenPlayer(leaderId);
        var beforeMailClaim = LoadInventory(leaderId).Items.ToDictionary(item => item.Id, item => item.Count);
        var stampIds = rankMail.RewardGoodsList!.Where(g => g.RewardType == (int)RewardType.ChatEmoji)
            .Select(g => checked((int)g.TemplateId)).ToHashSet();
        GuildAssert(stampIds.Count > 0, "Authored rank reward must exercise its timed stamp entitlement");
        var stampRules = TableReaderV2.Parse<EmojiTable>().Where(row => stampIds.Contains(row.Id)).ToDictionary(row => row.Id);
        long claimStarted = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var beforeStampClaim = Character.collection.Find(row => row.Uid == leaderId).Single();
        GuildAssert(stampIds.All(id => !beforeStampClaim.CanUseChatEmoji(id, claimStarted)),
            "Unclaimed rank mail already unlocked its earned stamp");
        Call(mailSession, nameof(MailGetSingleRewardRequest), new MailGetSingleRewardRequest { Id = rankMail.Id });
        Inventory afterMailClaim = LoadInventory(leaderId);
        long claimFinished = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var earnedStamps = Character.collection.Find(row => row.Uid == leaderId).Single();
        var stampExpiries = earnedStamps.ChatEmojis.Where(stamp => stampIds.Contains(stamp.Id)).ToDictionary(stamp => stamp.Id, stamp => stamp.EndTime);
        GuildAssert(stampExpiries.Count == stampIds.Count, "Claimed rank mail did not persist every stamp entitlement");
        foreach (int stampId in stampIds)
        {
            long duration = Convert.ToInt64(stampRules[stampId].Duration);
            long expiry = stampExpiries[stampId];
            GuildAssert(duration > 0 && expiry >= claimStarted + duration && expiry <= claimFinished + duration,
                "Rank stamp expiry must derive from its authored duration at claim time");
            GuildAssert(earnedStamps.CanUseChatEmoji(stampId, expiry - 1) && !earnedStamps.CanUseChatEmoji(stampId, expiry),
                "Rank stamp usage does not enforce its precise expiry boundary");
            GuildAssert(earnedStamps.GetUnlockedEmojis(expiry - 1).Any(stamp => stamp.Id == stampId && stamp.EndTime == expiry)
                && !earnedStamps.GetUnlockedEmojis(expiry).Any(stamp => stamp.Id == stampId),
                "Reloaded chat login projection exposes an expired stamp or hides an earned active stamp");
        }
        foreach (var item in rankMail.RewardGoodsList!.Where(g => g.RewardType == (int)RewardType.Item).GroupBy(g => g.TemplateId))
            GuildAssert(afterMailClaim.Items.Single(i => i.Id == item.Key).Count
                == beforeMailClaim.GetValueOrDefault(checked((int)item.Key)) + item.Sum(g => (long)g.Count),
                "Claimed rank mail did not credit its authored item attachments exactly once");
        GuildAssert(LoadPlayer(leaderId).Mails.Single(mail => mail.Id == rankMail.Id).Status == 3
            && afterMailClaim.AppliedRewardClaims.Contains(rankClaim), "Rank mail claim did not persist status and reward receipt");
        byte[] rankClaimedInventory = afterMailClaim.ToBson();
        LoopbackSessionHarness claimedMailSession = scope.OpenPlayer(leaderId);
        Reject(claimedMailSession, nameof(MailGetSingleRewardRequest), new MailGetSingleRewardRequest { Id = rankMail.Id });
        Activity(claimedMailSession);
        GuildAssert(rankClaimedInventory.SequenceEqual(LoadInventory(leaderId).ToBson())
            && LoadPlayer(leaderId).Mails.Count(mail => mail.RewardClaimKey == rankClaim) == 1,
            "Rank mail claim/rollover retry duplicated inventory or delivery after reload");
        var replayedStamps = Character.collection.Find(row => row.Uid == leaderId).Single();
        GuildAssert(stampExpiries.All(pair => replayedStamps.ChatEmojis.Count(stamp => stamp.Id == pair.Key) == 1
            && replayedStamps.ChatEmojis.Single(stamp => stamp.Id == pair.Key).EndTime == pair.Value),
            "Rank mail replay duplicated a stamp or extended its frozen expiry");
        resumed = claimedMailSession;
        var rolledStages = rolled["BossList"]!.ToObject<List<GuildBossStageInfo>>()!;
        foreach (int type in new[] { 1, 2 })
        {
            int stageId = rolledStages.First(s => s.Type == type).StageId;
            FightSettleRequest lost = StartFight(resumed, firstScore, stageId);
            lost.Result.IsWin = false;
            lost.Result.IsForceExit = true;
            Candidate(resumed, lost);
            Reject(resumed, "GuildBossUploadRequest", new GuildBossUploadRequest { StageId = stageId });
            int beforeEffects = LoadGuild().Boss.Stages.Single(s => s.StageId == stageId).CurEffectCount;
            Candidate(resumed, StartFight(resumed, firstScore, stageId));
            long beforeHp = LoadGuild().Boss.HpLeft;
            JObject lowHighUpload = Call(resumed, "GuildBossUploadRequest", new GuildBossUploadRequest { StageId = stageId });
            GuildBossStageState progressed = LoadGuild().Boss.Stages.Single(s => s.StageId == stageId);
            GuildAssert(progressed.CurEffectCount == Math.Min(beforeEffects + 1, progressed.EffectCount)
                && beforeHp - LoadGuild().Boss.HpLeft == lowHighUpload.Value<long>("SubHp"), "Low/high first upload lost shared effect/HP progress");
            Candidate(resumed, StartFight(resumed, firstScore + 1, stageId));
            Call(resumed, "GuildBossUploadRequest", new GuildBossUploadRequest { StageId = stageId });
            GuildAssert(LoadGuild().Boss.Stages.Single(s => s.StageId == stageId).CurEffectCount == progressed.CurEffectCount,
                "Improving one stage retriggered its first-upload effect");
            Reject(resumed, nameof(PreFightRequest), new PreFightRequest
            {
                PreFightData = new()
                {
                    StageId = checked((uint)rolledStages.First(s => s.Type == type && s.StageId != stageId).StageId),
                    ChallengeCount = 1,
                    CardIds = [checked((uint)resumed.Session.character.Characters.First().Id)],
                    RobotIds = [0],
                    CaptainPos = 1,
                    FirstFightPos = 1
                }
            });
        }

        // Keep the deployed robot frozen even if a legacy catalog is repaired mid-fight.
        Guild robotGuild = LoadGuild();
        var bossRobots = robotGuild.Boss.Robots.Single(row => row.Type == 3).RobotIds;
        int selectedRobot = bossRobots.First(id => robotGroups[robotGroupIds[id]].RobotId.Count > 1);
        var selectedVariants = robotGroups[robotGroupIds[selectedRobot]].RobotId;
        int preservedRobot = selectedVariants.First(id => id != selectedRobot);
        bossRobots[bossRobots.IndexOf(selectedRobot)] = preservedRobot;
        robotGuild.SaveChecked();
        GuildAssert(Roster(Activity(resumed)).Single(row => row.Type == 3).RobotIds.Contains(preservedRobot)
            && LoadGuild().Boss.Robots.Single(row => row.Type == 3).RobotIds.Contains(preservedRobot),
            "Activity replaced an already-valid singleton robot variant");
        int robotStage = rolledStages.Single(row => row.Type == 3).StageId;
        JObject robotPreFight = Call(resumed, nameof(PreFightRequest), new PreFightRequest
        {
            PreFightData = new()
            {
                StageId = checked((uint)robotStage),
                ChallengeCount = 1,
                CardIds = [0, 0, 0],
                RobotIds = [preservedRobot, 0, 0],
                CaptainPos = 1,
                FirstFightPos = 1
            }
        });
        byte[] frozenRobotAttempt = LoadPlayer(leaderId).GuildState.Boss.Attempt!.ToBson();
        JToken uploadedTeams = JToken.FromObject(LoadGuild().Boss.Participants);
        FlattenRoster(LoadGuild());
        var midFightRoster = Roster(Activity(resumed));
        AssertRoster(midFightRoster);
        GuildAssert(midFightRoster.Single(row => row.Type == 3).RobotIds.Contains(selectedRobot)
            && !midFightRoster.Single(row => row.Type == 3).RobotIds.Contains(preservedRobot),
            "Flattened roster fixture did not change the offered robot variant");
        GuildAssert(frozenRobotAttempt.SequenceEqual(LoadPlayer(leaderId).GuildState.Boss.Attempt!.ToBson())
            && JToken.DeepEquals(uploadedTeams, JToken.FromObject(LoadGuild().Boss.Participants)),
            "Roster repair changed a frozen in-flight attempt or uploaded team");
        var robotSettle = CreateMissingStageSettleRequest(checked((uint)robotStage),
            robotPreFight["FightData"]!.Value<long>("FightId"), leaderId);
        robotSettle.Result.StartFrame = 0;
        robotSettle.Result.SettleFrame = 20;
        robotSettle.Result.TotalDamage = 1;
        robotSettle.Result.NpcHpInfo = new()
        {
            [1] = new NpcHp
            {
                Type = 1,
                CharacterId = robotCharacters[preservedRobot],
                BuffIds = [],
                AttrTable = new()
                { [1] = new Dictionary<string, object> { ["Value"] = 100, ["MaxValue"] = 100 } }
            }
        };
        robotSettle.Result.NpcDpsTable = new()
        {
            [1] = new NpcDpsTable { RoleId = checked((int)leaderId), CharacterId = robotCharacters[preservedRobot], DamageTotal = 1 }
        };
        Candidate(resumed, robotSettle);
    }
}
