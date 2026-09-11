using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.fuben;
using AscNet.Table.V2.share.robot;
using AscNet.Table.V2.share.theatre;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Newtonsoft.Json.Linq;
using System.Reflection;

namespace AscNet.Test;

internal partial class Program
{
    private static PreFightRequest TheatrePreFight(TheatreCase test, TheatreSlot slot, int teamIndex = 0)
    {
        var team = slot.StageIds.Count > 1 ? test.Data.MultiTeamDatas.Single(team => team.TeamIndex == teamIndex) : test.Data.SingleTeamData!;
        return new()
        {
            PreFightData = new()
            {
                StageId = checked((uint)slot.StageIds[slot.StageIds.Count > 1 ? teamIndex - 1 : 0]),
                TeamIndex = teamIndex,
                ChallengeCount = 1,
                CardIds = team.CardIds.Select(id => checked((uint)id)).ToList(),
                RobotIds = team.RobotIds.ToList(),
                CaptainPos = team.CaptainPos,
                FirstFightPos = team.FirstFightPos,
                EnterCgIndex = team.EnterCgIndex,
                SettleCgIndex = team.SettleCgIndex
            }
        };
    }

    private static PreFightResponse TheatreFight(TheatreCase test, TheatreSlot slot, int teamIndex = 0, bool win = true, bool forceExit = false)
    {
        if (slot.StageIds.Count == 1 && test.Data.SingleTeamData is null)
            test.Call(nameof(TheatreSetSingleTeamRequest), new TheatreSetSingleTeamRequest { TeamData = TheatreTeam(test) });
        if (slot.StageIds.Count > 1 && test.Data.MultiTeamDatas.Count == 0)
            test.Call(nameof(TheatreSetMultiTeamRequest), new TheatreSetMultiTeamRequest
            { TeamDatas = Enumerable.Range(1, slot.StageIds.Count).Select(index => TheatreTeam(test, index, index - 1)).ToList() });
        var request = TheatrePreFight(test, slot, teamIndex);
        test.Call(nameof(PreFightRequest), request);
        var response = MessagePackSerializer.Deserialize<PreFightResponse>(test.LastResponseContent);
        AssertTheatreFightPlan(test, response);
        var settle = CreateMissingStageSettleRequest(request.PreFightData.StageId, response.FightData.FightId, test.Session.player.PlayerData.Id);
        settle.Result.IsWin = win;
        settle.Result.IsForceExit = forceExit;
        settle.Result.RebootCount = test.State.Fight!.RebootCount;
        int writes = test.Stages.ReplaceOneCalls;
        test.Call(nameof(FightSettleRequest), settle);
        var result = MessagePackSerializer.Deserialize<FightSettleResponse>(test.LastResponseContent);
        AssertEqual(win && !forceExit, result.Settle.IsWin, "Force exit cannot manufacture a victory");
        AssertEqual(0, result.Settle.StarsMark, "Original Theatre does not auto-grant three generic stars");
        AssertEqual(0, result.Settle.RewardGoodsList.Count, "Native result cannot auto-claim a generic first-clear reward");
        AssertEqual(writes, test.Stages.ReplaceOneCalls, "Original Theatre settlement cannot advance generic stages");
        AssertEqual(false, test.Pushes.Any(push => push.Name == nameof(NotifyStageData)), "Original Theatre cannot emit generic stage rewards");
        return response;
    }

    private static void AssertTheatreFightPlan(TheatreCase test, PreFightResponse response)
    {
        AssertEqual(0, response.Code, "Original Theatre prefight succeeds");
        var fight = test.State.Fight!;
        var data = response.FightData;
        var stage = TableReaderV2.Parse<StageTable>().Single(row => row.StageId == fight.StageId);
        AssertEqual(fight.FightId, (long)data.FightId, "Frozen battle identity");
        AssertEqual(fight.Seed, (long)data.Seed, "Unsigned seed survives persisted plan");
        AssertEqual(Convert.ToInt32(stage.PassTimeLimit), data.PassTimeLimit, "Source time limit");
        AssertEqual(Convert.ToInt32(stage.Restartable) != 0, data.Restartable, "Source restart permission");
        AssertEqual(true, stage.NormalEventId.SequenceEqual(data.NormalEventIds.Select(value => Convert.ToInt32((object)value))), "Native normal events retain source IDs");
        var expected = (List<int>)TheatreCall("GetFightEvents", TheatreMutation(test), 0)!;
        AssertEqual(true, expected.SequenceEqual(data.EventIds.Select(value => Convert.ToInt32((object)value))), "Native effects registered globally once");
        var resolve = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.FightModule").GetMethod("ResolveStageLevelControl", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        var levels = resolve.Invoke(null, [fight.StageId, checked((int)test.Session.player.PlayerData.Level)]);
        var expectedLevels = levels is null ? new List<int>() : (List<int>)(levels.GetType().GetProperty("MonsterLevel")!.GetValue(levels)
            ?? throw new InvalidDataException("Authored level control has no monster levels."));
        AssertEqual(true, expectedLevels.SequenceEqual(data.MonsterLevel ?? throw new InvalidDataException("Native fight omitted monster levels.")), "Native stage level control, including graph-authored fallback");
        var role = data.RoleData.Single();
        AssertEqual(fight.Team.CaptainPos - 1, role.CaptainIndex, "Native captain is zero based");
        AssertEqual(fight.Team.FirstFightPos - 1, role.FirstFightPos, "Native entry position is zero based");
        for (int position = 0; position < 3; position++)
        {
            bool deployed = fight.Team.CardIds[position] > 0 || fight.Team.RobotIds[position] > 0;
            AssertEqual(deployed, role.NpcData.ContainsKey(position), "Sparse native NPC slots preserve team positions");
            if (!deployed) continue;
            // Inspect only the NPC value: serializing the whole FightData through JSON loses integer map keys.
            var npc = JObject.FromObject((object)role.NpcData[position]);
            int robotId = fight.Team.RobotIds[position];
            int characterId = robotId == 0 ? fight.Team.CardIds[position] : TableReaderV2.Parse<RobotTable>().Single(row => row.Id == robotId).CharacterId;
            AssertEqual(characterId, npc["Character"]!["Id"]!.Value<int>(), "Owned/trial native character identity");
            AssertEqual(robotId != 0, npc["IsRobot"]!.Value<bool>(), "Owned/trial native deployment mode");
            AssertEqual(robotId, npc["RobotId"]!.Value<int>(), "Trial source robot identity");
            AssertEqual(false, npc.ContainsKey("EventIds"), "Global decoration/skill effects are not duplicated per NPC");
            if (robotId > 0)
            {
                var robot = TableReaderV2.Parse<RobotTable>().Single(row => row.Id == robotId);
                var equips = (JArray)npc["Equips"]!;
                for (int wafer = 0; wafer < robot.WaferId.Count; wafer++)
                {
                    var equip = equips.Single(value => value["TemplateId"]!.Value<int>() == robot.WaferId[wafer]);
                    AssertEqual(robot.WaferLevel[wafer], equip["Level"]!.Value<int>(), "Authored trial wafer level reaches native NPC");
                    AssertEqual(robot.WaferBreakThrough[wafer], equip["Breakthrough"]!.Value<int>(), "Authored trial wafer breakthrough reaches native NPC");
                    int awake = Convert.ToInt32(robot.WaferAwakeCount.ElementAtOrDefault(wafer));
                    AssertEqual(true, Enumerable.Range(1, awake).SequenceEqual(equip["AwakeSlotList"]!.Values<int>()), "Authored trial wafer awakening slots reach native NPC");
                    string[] templates = Convert.ToString(robot.WaferResonance.ElementAtOrDefault(wafer))?.Split('|') ?? [];
                    string[] types = Convert.ToString(robot.WaferResonanceType.ElementAtOrDefault(wafer))?.Split('|') ?? [];
                    var resonances = (JArray)equip["ResonanceInfo"]!;
                    var authored = templates.Zip(types).Select(pair =>
                        (Template: int.TryParse(pair.First, out int template) ? template : 0,
                         Type: int.TryParse(pair.Second, out int type) ? type : 0)).TakeWhile(pair => pair.Template > 0 && pair.Type > 0).ToList();
                    AssertEqual(authored.Count, resonances.Count, "Authored wafer resonance count reaches native NPC");
                    for (int index = 0; index < authored.Count; index++)
                    {
                        AssertEqual(authored[index].Template, resonances[index]["TemplateId"]!.Value<int>(), "Authored wafer resonance effect reaches native NPC");
                        AssertEqual(authored[index].Type, resonances[index]["Type"]!.Value<int>(), "Authored wafer resonance type reaches native NPC");
                        AssertEqual(characterId, resonances[index]["CharacterId"]!.Value<int>(), "Trial resonance remains bound to authored character");
                    }
                }
            }
        }
    }

    private static TheatreSlot PrepareTheatreCombat(TheatreCase test, TheatreStageTable stage)
    {
        test.Start();
        var level = TableReaderV2.Parse<TheatreLvTable>().Single(row => row.Lv == test.Data.CurRoleLv);
        test.Data.RecruitRole = TableReaderV2.Parse<TheatreRoleAttrTable>().Where(row => row.Lv == level.Lv)
            .GroupBy(row => TableReaderV2.Parse<RobotTable>().Single(robot => robot.Id == row.RobotId).CharacterId)
            .Select(group => group.First().RoleId).ToList();
        test.Data.CurChapterDb!.SkillToSelect.Clear();
        test.State.PendingSkillPowers.Clear();
        test.State.NodeCompletionPending = false;
        var slot = new TheatreSlot
        {
            SlotId = ++test.State.NextUid,
            SlotType = 3,
            Selected = 1,
            TheatreStageId = stage.Id,
            StageIds = stage.StageId.Where(id => id > 0).ToList()
        };
        TheatreFixtureSlot(test, slot);
        if (slot.StageIds.Count == 1)
            test.Call(nameof(TheatreSetSingleTeamRequest), new TheatreSetSingleTeamRequest { TeamData = TheatreTeam(test) });
        else
            test.Call(nameof(TheatreSetMultiTeamRequest), new TheatreSetMultiTeamRequest
            { TeamDatas = Enumerable.Range(1, slot.StageIds.Count).Select(index => TheatreTeam(test, index, index - 1)).ToList() });
        return CurrentTheatreSlot(test);
    }

    private static void ValidateTheatreCombatCompatibility()
    {
        var stages = TableReaderV2.Parse<TheatreStageTable>();
        var native = TableReaderV2.Parse<StageTable>();
        var single = stages.First(row => row.StageCount == 1 && native.Any(stage => stage.StageId == row.StageId[0] && Convert.ToInt32(stage.Restartable) != 0));
        using (TheatreCase test = new("combat-boundaries"))
        {
            var slot = PrepareTheatreCombat(test, single);
            var request = TheatrePreFight(test, slot);
            foreach (Action<PreFightRequest> corrupt in new Action<PreFightRequest>[]
            {
                value => value.PreFightData.TeamIndex = 1,
                value => value.PreFightData.ChallengeCount = 2,
                value => value.PreFightData.CardIds = [0, 0],
                value => value.PreFightData.RobotIds = [int.MaxValue, 0, 0],
                value => value.PreFightData.CaptainPos = 0,
                value => value.PreFightData.IsHasAssist = true,
                value => value.PreFightData.GeneralSkill = int.MaxValue
            })
            {
                var wrong = MessagePackSerializer.Deserialize<PreFightRequest>(MessagePackSerializer.Serialize(request));
                corrupt(wrong);
                test.Reject(nameof(PreFightRequest), wrong, "Malformed deployment cannot authorize native battle");
            }
            test.Call(nameof(PreFightRequest), request);
            AssertTheatreFightPlan(test, MessagePackSerializer.Deserialize<PreFightResponse>(test.LastResponseContent));
            long fightId = test.State.Fight!.FightId;
            var packet = MessagePackSerializer.Deserialize<PreFightResponse.PreFightResponseFightData>(test.State.Fight.PreFightPayload!);
            packet.Seed = uint.MaxValue;
            test.State.Fight.Seed = uint.MaxValue;
            test.State.Fight.PreFightPayload = MessagePackSerializer.Serialize(packet);
            test.SaveFixture();
            byte[] frozen = test.State.Fight.PreFightPayload.ToArray();
            test.Relog("full-width frozen native battle");
            test.Call(nameof(PreFightRequest), request);
            AssertEqual(true, frozen.SequenceEqual(test.State.Fight!.PreFightPayload!), "Reconnect returns exactly the frozen plan");
            AssertEqual((long)uint.MaxValue, test.State.Fight.Seed, "BSON does not sign-truncate the seed");
            test.Reject(nameof(TheatreSetSingleTeamRequest), new TheatreSetSingleTeamRequest { TeamData = TheatreTeam(test) }, "Live fight locks team changes");
            var invalid = CreateMissingStageSettleRequest(request.PreFightData.StageId, (uint)fightId, test.Session.player.PlayerData.Id);
            foreach (Action<FightSettleResult> corrupt in new Action<FightSettleResult>[]
            {
                value => value.FightId++, value => value.StageId++, value => value.RebootCount++,
                value => value.SettleFrame = -1, value => value.PauseFrame = 1,
                value => value.TotalDamage = -1, value => value.LeftTime = -1,
                value => value.PlayerIds = [test.Session.player.PlayerData.Id + 1]
            })
            {
                var wrong = MessagePackSerializer.Deserialize<FightSettleRequest>(MessagePackSerializer.Serialize(invalid));
                corrupt(wrong.Result);
                test.Reject(nameof(FightSettleRequest), wrong, "Untrusted native result cannot consume the authorized fight");
            }
            long runId = test.State.RunId;
            test.State.RunId++;
            test.SaveFixture();
            test.Reject(nameof(FightSettleRequest), invalid, "Stale run cannot settle a retained fight");
            test.State.RunId = runId;
            test.SaveFixture();
            int lives = test.Data.ReopenCount;
            test.Reject(nameof(FightRebootRequest), new FightRebootRequest { FightId = (int)fightId, RebootCount = 1 }, "Original native reboot profile zero is unavailable");
            AssertEqual(lives, test.Data.ReopenCount, "Unavailable native revive cannot charge a life");
            test.Call(nameof(FightRestartRequest), new FightRestartRequest { FightId = (int)fightId });
            AssertEqual(lives + 1, test.Data.ReopenCount, "Local restart spends exactly one used reopen");
            var restarted = MessagePackSerializer.Deserialize<PreFightResponse.PreFightResponseFightData>(test.State.Fight!.PreFightPayload!);
            packet.Seed = restarted.Seed;
            AssertEqual(true, MessagePackSerializer.Serialize(packet).SequenceEqual(test.State.Fight.PreFightPayload!), "Restart changes seed, not native battle plan");
            test.Call(nameof(LeaveFightRequest), new LeaveFightRequest());
            AssertEqual(lives + 2, test.Data.ReopenCount, "Retreat spends one used reopen");
            AssertEqual(true, test.State.Fight is null && test.Session.fight is null, "Retreat clears persisted and connection battle");
            AssertEqual(0, CurrentTheatreSlot(test).PassedStageIndexs.Count, "Retreat does not grant stage victory");
        }
        foreach (bool forceExit in new[] { false, true })
            using (TheatreCase test = new("combat-defeat-or-forced-exit"))
            {
                var slot = PrepareTheatreCombat(test, single);
                int lives = test.Data.ReopenCount;
                TheatreFight(test, slot, win: forceExit, forceExit: forceExit);
                AssertEqual(lives + 1, test.Data.ReopenCount, "Defeat and forced exit consume one used reopen");
                AssertEqual(0, CurrentTheatreSlot(test).PassedStageIndexs.Count, "Defeat and forced exit do not complete a stage");
                test.Relog("defeated battle");
                AssertEqual(true, test.State.Fight is null, "Reconnect cannot resurrect a completed defeat");
            }
        using (TheatreCase test = new("combat-life-limit"))
        {
            var slot = PrepareTheatreCombat(test, single);
            test.Data.ReopenCount = TableReaderV2.Parse<TheatreDifficultyTable>().Single(row => row.Id == test.Data.DifficultyId).ReopenCount;
            test.Data.Decorations.Clear();
            test.SaveFixture();
            test.Call(nameof(PreFightRequest), TheatrePreFight(test, slot));
            test.Reject(nameof(FightRestartRequest), new FightRestartRequest { FightId = (int)test.State.Fight!.FightId }, "Exhausted reopen budget rejects restart");
        }
        var repeated = stages.First(row => row.StageCount == 2);
        using (TheatreCase test = new("combat-repeated-stage-index"))
        {
            var slot = PrepareTheatreCombat(test, repeated);
            // Synthetic persisted boundary: authored two-stage rows currently have distinct IDs.
            // Keep their real stage/count authority but prove completion keys are indexes, not IDs.
            slot.StageIds[1] = slot.StageIds[0];
            test.SaveFixture();
            var duplicate = test.Data.MultiTeamDatas.Select(team => MessagePackSerializer.Deserialize<TheatreTeamData>(MessagePackSerializer.Serialize(team))).ToList();
            duplicate[1].RobotIds = duplicate[0].RobotIds.ToList();
            test.Reject(nameof(TheatreSetMultiTeamRequest), new TheatreSetMultiTeamRequest { TeamDatas = duplicate }, "A character cannot deploy in two multi teams");
            TheatreFight(test, slot, 1);
            AssertEqual(true, CurrentTheatreSlot(test).PassedStageIndexs.SequenceEqual(new[] { 1 }), "Repeated stage IDs complete by index only");
            test.Reject(nameof(PreFightRequest), TheatrePreFight(test, CurrentTheatreSlot(test), 1), "Completed index cannot fight again before reset");
            var changed = test.Data.MultiTeamDatas.Select(team => MessagePackSerializer.Deserialize<TheatreTeamData>(MessagePackSerializer.Serialize(team))).ToList();
            changed[0] = TheatreTeam(test, 1, changed.Count);
            test.Reject(nameof(TheatreSetMultiTeamRequest), new TheatreSetMultiTeamRequest { TeamDatas = changed }, "Completed multi team is locked");
            int reopen = test.Data.ReopenCount;
            test.Call(nameof(TheatreMultiTeamResetRequest), new TheatreMultiTeamResetRequest { TeamIndex = 1 });
            AssertEqual(reopen, test.Data.ReopenCount, "Team reset is not a combat reopen");
            AssertEqual(0, CurrentTheatreSlot(test).PassedStageIds.Count, "Reset clears only indexed completion projections");
            TheatreFight(test, CurrentTheatreSlot(test), 1);
            TheatreFight(test, CurrentTheatreSlot(test), 2);
            AssertEqual(3, test.State.RunFightCount, "Repeated stage IDs each grant exactly one indexed victory after reset");
        }
        foreach (var ownStage in new[] { single, repeated })
            using (TheatreCase test = new("combat-owned-deployment"))
            {
                var slot = PrepareTheatreCombat(test, ownStage);
                var level = TableReaderV2.Parse<TheatreLvTable>().MaxBy(row => row.Lv)!;
                test.Data.CurRoleLv = level.Lv;
                var robot = TableReaderV2.Parse<RobotTable>().Single(row => row.Id ==
                    TableReaderV2.Parse<TheatreRoleAttrTable>().Single(row => row.RoleId == test.Data.RecruitRole[0] && row.Lv == level.Lv).RobotId);
                var build = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.FightModule").GetMethod("BuildRobotDeployment", BindingFlags.Static | BindingFlags.NonPublic)!;
                var deployment = ((CharacterData Character, List<EquipData> Equips))build.Invoke(null, [robot])!;
                test.Session.character.Characters.RemoveAll(row => row.Id == deployment.Character.Id);
                test.Session.character.Characters.Add(deployment.Character);
                test.Session.character.Equips.RemoveAll(equip => equip.CharacterId == robot.CharacterId);
                uint nextEquip = test.Session.character.Equips.Select(equip => equip.Id).DefaultIfEmpty().Max();
                foreach (var equip in deployment.Equips)
                {
                    equip.Id = checked(++nextEquip);
                    equip.CharacterId = robot.CharacterId;
                    test.Session.character.Equips.Add(equip);
                }
                var permission = TheatreMutation(test);
                TheatreCall("UpdateOwnCharacterPermission", permission);
                test.Session.player.Theatre = (PlayerTheatreState)permission.GetType().GetProperty("State")!.GetValue(permission)!;
                AssertEqual(1, test.Data.UseOwnCharacter, "Authored roster power earns owned-character permission");
                test.State.FrozenOwnCharacterIds = [checked((int)deployment.Character.Id)];
                var team = new TheatreTeamData
                {
                    CaptainPos = 1,
                    FirstFightPos = 1,
                    CardIds = [checked((int)deployment.Character.Id), 0, 0],
                    RobotIds = [0, 0, 0]
                };
                test.SaveFixture();
                if (slot.StageIds.Count == 1)
                    test.Call(nameof(TheatreSetSingleTeamRequest), new TheatreSetSingleTeamRequest { TeamData = team });
                else
                {
                    team.TeamIndex = 1;
                    var teams = Enumerable.Range(1, slot.StageIds.Count).Select(index => TheatreTeam(test, index, index - 1)).ToList();
                    teams[0] = team;
                    test.Call(nameof(TheatreSetMultiTeamRequest), new TheatreSetMultiTeamRequest { TeamDatas = teams });
                }
                TheatreFight(test, slot, slot.StageIds.Count > 1 ? 1 : 0);
            }
        using (TheatreCase test = new("combat-persistence-boundary"))
        {
            var slot = PrepareTheatreCombat(test, single);
            var request = TheatrePreFight(test, slot);
            test.Players.ThrowOnReplaceOne = true;
            try
            {
                var failed = test.Call(nameof(PreFightRequest), request, success: false);
                AssertEqual(1, failed["Code"]!.Value<int>(), "Actual Mongo failure is a retryable runtime response");
                AssertEqual(true, test.State.Fight is null && test.Session.fight is null, "Failed authorization cannot create connection-only fight");
            }
            finally { test.Players.ThrowOnReplaceOne = false; }
            test.Call(nameof(PreFightRequest), request);
            int lives = test.Data.ReopenCount;
            test.Players.ThrowOnReplaceOne = true;
            try
            {
                test.Call(nameof(LeaveFightRequest), new LeaveFightRequest(), success: false);
                AssertEqual(true, test.State.Fight is not null && test.Session.fight is not null, "Failed leave retains battle for retry");
                AssertEqual(lives, test.Data.ReopenCount, "Failed leave does not spend a life before commit");
            }
            finally { test.Players.ThrowOnReplaceOne = false; }
            test.Call(nameof(LeaveFightRequest), new LeaveFightRequest());
            AssertEqual(lives + 1, test.Data.ReopenCount, "Retried leave commits one life");
        }
        using (TheatreCase test = new("combat-authored-wafer-effects"))
        {
            var slot = PrepareTheatreCombat(test, single);
            int level = TableReaderV2.Parse<TheatreLvTable>().Max(row => row.Lv);
            var robot = TableReaderV2.Parse<RobotTable>().First(row =>
                row.WaferAwakeCount.Any(count => Convert.ToInt32(count) > 0)
                && TableReaderV2.Parse<TheatreRoleAttrTable>().Any(attr => attr.RobotId == row.Id && attr.Lv == level && test.Data.RecruitRole.Contains(attr.RoleId)));
            int role = TableReaderV2.Parse<TheatreRoleAttrTable>().Single(row => row.RobotId == robot.Id).RoleId;
            test.Data.CurRoleLv = level;
            test.Data.RecruitRole.Remove(role);
            test.Data.RecruitRole.Insert(0, role);
            test.SaveFixture();
            test.Call(nameof(TheatreSetSingleTeamRequest), new TheatreSetSingleTeamRequest { TeamData = TheatreTeam(test) });
            TheatreFight(test, slot);
        }
        using (TheatreCase test = new("combat-settle-response-receipt"))
        {
            var slot = PrepareTheatreCombat(test, repeated);
            TheatreFight(test, slot, 1);
            int settlePacket = test.LastPacketId;
            object settleRequest = test.LastRequest!;
            byte[] response = test.LastResponseContent.ToArray();
            test.Replay("Completed native settlement");
            test.Relog("completed native settlement receipt");
            byte[] state = test.State.ToBson(), inventory = test.Session.inventory.ToBson();
            test.Call(nameof(FightSettleRequest), settleRequest, reusePacketId: settlePacket);
            AssertEqual(true, response.SequenceEqual(test.LastResponseContent), "Reconnect returns frozen same-ID settlement response");
            AssertEqual(0, test.Pushes.Count, "Reconnect settlement replay is response-only");
            AssertEqual(true, state.SequenceEqual(test.State.ToBson()) && inventory.SequenceEqual(test.Session.inventory.ToBson()), "Reconnect settlement replay grants nothing");
            test.Reject(nameof(FightSettleRequest), settleRequest, "New packet ID cannot settle a completed stale fight");
        }
        using (TheatreCase test = new("combat-pending-restart-alias"))
        {
            var slot = PrepareTheatreCombat(test, single);
            test.Call(nameof(PreFightRequest), TheatrePreFight(test, slot));
            var restart = new FightRestartRequest { FightId = checked((int)test.State.Fight!.FightId) };
            int lives = test.Data.ReopenCount;
            test.Players.BeforeReplaceOne = replacement =>
            {
                if (replacement.Theatre.PendingMutation is null)
                    throw new MongoDB.Driver.MongoException("Injected restart final player commit failure.");
            };
            try
            {
                var failed = test.Call(nameof(FightRestartRequest), restart, success: false);
                AssertEqual(1, failed["Code"]!.Value<int>(), "Pending restart reports real persistence failure");
            }
            finally { test.Players.BeforeReplaceOne = null; }
            AssertEqual(true, test.State.PendingMutation is not null, "Restart retains durable owed outcome");
            long owedSeed = test.State.PendingMutation!.Outcome.Fight!.Seed;
            int failedPacket = test.LastPacketId;
            test.Call(nameof(FightRestartRequest), restart);
            AssertEqual(true, test.LastPacketId != failedPacket, "Semantic retry uses a new transport packet");
            AssertEqual(owedSeed, test.State.Fight!.Seed, "Semantic pending retry returns the owed frozen seed");
            AssertEqual(lives + 1, test.Data.ReopenCount, "Semantic pending retry spends only the owed life");
            AssertEqual(unchecked((int)owedSeed), MessagePackSerializer.Deserialize<FightRestartResponse>(test.LastResponseContent).Seed, "Restart reply preserves all unsigned seed bits");
            int recoveredPacket = test.LastPacketId;
            byte[] recoveredResponse = test.LastResponseContent.ToArray(), recoveredInventory = test.Session.inventory.ToBson();
            test.Call(nameof(FightRestartRequest), restart, reusePacketId: recoveredPacket);
            AssertEqual(true, recoveredResponse.SequenceEqual(test.LastResponseContent), "Recovered restart replay returns the same correlated response");
            AssertEqual(owedSeed, test.State.Fight!.Seed, "Recovered restart replay preserves the frozen seed");
            AssertEqual(lives + 1, test.Data.ReopenCount, "Recovered restart replay does not spend another life");
            AssertEqual(true, recoveredInventory.SequenceEqual(test.Session.inventory.ToBson()), "Recovered restart replay leaves inventory unchanged");
            AssertEqual(0, test.Pushes.Count, "Recovered restart replay emits no mode pushes");
            test.Call(nameof(FightRestartRequest), restart);
            AssertEqual(lives + 2, test.Data.ReopenCount, "Later new-ID restart is a new operation");
        }
        using (TheatreCase test = new("combat-leave-old-packet"))
        {
            var slot = PrepareTheatreCombat(test, single);
            var request = TheatrePreFight(test, slot);
            test.Call(nameof(PreFightRequest), request);
            test.Call(nameof(LeaveFightRequest), new LeaveFightRequest());
            int oldLeave = test.LastPacketId;
            test.Call(nameof(PreFightRequest), request);
            long fightId = test.State.Fight!.FightId;
            byte[] state = test.State.ToBson(), inventory = test.Session.inventory.ToBson();
            test.Call(nameof(LeaveFightRequest), new LeaveFightRequest(), success: null, reusePacketId: oldLeave);
            AssertEqual(fightId, test.State.Fight!.FightId, "Old leave packet cannot clear a newly authorized fight");
            AssertEqual(fightId, test.Session.fight!.FightId, "Old leave packet cannot clear new connection battle");
            AssertEqual(0, test.Pushes.Count, "Old leave packet has no life or reward pushes");
            AssertEqual(true, state.SequenceEqual(test.State.ToBson()) && inventory.SequenceEqual(test.Session.inventory.ToBson()), "Old leave packet is mutation-free");
        }
    }

    private static void ValidateTheatreEffectsCompatibility()
    {
        using TheatreCase test = new("native-effects-and-local-operands");
        test.Start();
        var rows = TableReaderV2.Parse<TheatreDecorationTable>();
        var known = new HashSet<int> { 1, 3, 4, 5, 9, 10, 11, 12, 13, 15 };
        AssertEqual(true, rows.SelectMany(row => row.Type).All(known.Contains), "Every authored decoration operand has an explicit policy check");
        foreach (var row in rows)
        {
            test.Data.Decorations = [row.Id];
            object modifiers = TheatreCall("GetModifiers", TheatreMutation(test))!;
            var additive = new Dictionary<int, string> { [1] = "InitialCoinBonus", [3] = "NodeCoinBonus", [5] = "ShopItemCountBonus", [9] = "ReopenBonus", [10] = "RecruitBonus", [11] = "RefreshBonus" };
            foreach (var (type, property) in additive)
                AssertEqual(row.Type.Select((value, index) => value == type ? checked((int)row.Param[index]) : 0).Sum(),
                    (int)modifiers.GetType().GetProperty(property)!.GetValue(modifiers)!, $"Authored additive operand {row.Id}/{type}");
            foreach (var (type, method) in new[] { (4, "ShopPrice"), (12, "InspirationGain"), (13, "CadenzaGain") })
            {
                decimal factor = row.Type.Select((value, index) => value == type ? checked((decimal)row.Param[index]) : 1m).Aggregate(1m, (a, b) => a * b);
                foreach (int amount in new[] { 0, 1, 9, 10, 11, 99, 101 })
                    AssertEqual(checked((int)decimal.Floor(amount * factor)), (int)modifiers.GetType().GetMethod(method)!.Invoke(modifiers, [amount])!, $"Round once at money boundary {row.Id}/{method}/{amount}");
                try { modifiers.GetType().GetMethod(method)!.Invoke(modifiers, [-1]); throw new InvalidDataException("Negative money amount was accepted."); }
                catch (TargetInvocationException exception) when (exception.InnerException is ArgumentOutOfRangeException) { }
            }
            AssertEqual(true, row.Type.Select((value, index) => value == 15 ? checked((int)row.Param[index]) : 0).Where(id => id > 0)
                .SequenceEqual((List<int>)modifiers.GetType().GetProperty("FightEventIds")!.GetValue(modifiers)!), "Native decoration IDs are numeric operands, not display percentages");
        }
        test.Data.Decorations = rows.Select(row => row.Id).Concat(rows.Select(row => row.Id)).ToList();
        test.Data.Skills = TableReaderV2.Parse<TheatreSkillTable>().Select(row => row.Id).ToList();
        var difficulty = TableReaderV2.Parse<TheatreDifficultyTable>().Single(row => row.Id == test.Data.DifficultyId);
        var current = rows.GroupBy(row => row.DecorationId).Select(group => group.MaxBy(row => row.Lv ?? 0)!);
        var expected = TableReaderV2.Parse<TheatreSkillTable>().SelectMany(row => row.FightEventId)
            .Concat(current.SelectMany(row => row.Type.Select((type, index) => type == 15 ? checked((int)row.Param[index]) : 0)))
            .Concat(difficulty.FightEventId).Concat(difficulty.EnemyBuff).Where(id => id > 0).Distinct().ToList();
        var mutation = TheatreMutation(test);
        AssertEqual(true, expected.SequenceEqual((List<int>)TheatreCall("GetFightEvents", mutation, 0)!), "Current decoration rows plus selected skill rows and difficulty native effects form an ordered union");
        foreach (var (type, method) in new[] { (4, "ShopPrice"), (12, "InspirationGain"), (13, "CadenzaGain") })
        {
            decimal factor = current.SelectMany(row => row.Type.Select((value, index) => value == type ? checked((decimal)row.Param[index]) : 1m))
                .Aggregate(1m, (a, b) => a * b);
            var modifiers = TheatreCall("GetModifiers", mutation)!;
            AssertEqual(checked((int)decimal.Floor(101 * factor)), (int)modifiers.GetType().GetMethod(method)!.Invoke(modifiers, [101])!,
                "Independent current-row factors multiply without summing obsolete levels");
        }
        try { TheatreCall("GetFightEvents", mutation, 1); throw new InvalidDataException("Role-scoped native effect duplication accepted."); }
        catch (TargetInvocationException exception) when (exception.InnerException is ArgumentException) { }
        var fullWidth = new TheatreFightState { Seed = uint.MaxValue, StageId = uint.MaxValue, RestartReceipts = new() { [1] = uint.MaxValue } };
        var restored = BsonSerializer.Deserialize<TheatreFightState>(fullWidth.ToBson());
        AssertEqual((long)uint.MaxValue, restored.Seed, "Persist full-width original battle seed");
        AssertEqual((long)uint.MaxValue, restored.RestartReceipts[1], "Persist full-width restart receipt seed");
    }
}
