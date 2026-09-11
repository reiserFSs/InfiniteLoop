using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.theatre3;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Newtonsoft.Json.Linq;
using System.Reflection;

namespace AscNet.Test;

internal partial class Program
{
    private static Theatre3NodeSlot Theatre3BattleSlot(Theatre3Case test)
    {
        var step = test.Data.CurChapterDb!.Steps.Last(value => value.Overdue == 0 && value.StepType == 2);
        var node = step.NodeData?.ChapterId == test.Data.CurChapterId ? step.NodeData : step.ConnectNodeData;
        return node!.Slots.Single(value => value.Selected == 1);
    }

    private static PreFightRequest Theatre3PreFightRequest(Theatre3Case test)
    {
        var slot = Theatre3BattleSlot(test);
        var template = TableReaderV2.Parse<Theatre3FightStageTemplateTable>().Single(row => row.Id == slot.FightTemplateId);
        var team = test.Data.CurTeamData ?? throw new InvalidDataException("Theatre3 battle has no persisted team.");
        return new()
        {
            PreFightData = new()
            {
                StageId = checked((uint)template.StageId),
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

    private static PreFightResponse FightTheatre3(Theatre3Case test, bool win = true, bool forceExit = false)
    {
        test.Call(nameof(PreFightRequest), Theatre3PreFightRequest(test));
        var response = MessagePackSerializer.Deserialize<PreFightResponse>(test.LastResponseContent);
        AssertTheatre3BattlePlan(test, response);
        var request = CreateMissingStageSettleRequest(response.FightData.StageId, response.FightData.FightId, test.Session.player.PlayerData.Id);
        request.Result.IsWin = win;
        request.Result.IsForceExit = forceExit;
        request.Result.RebootCount = test.State.Fight!.RebootCount;
        int stages = test.Stages.ReplaceOneCalls;
        test.Call(nameof(FightSettleRequest), request);
        AssertEqual(stages, test.Stages.ReplaceOneCalls, "Matrix combat does not advance generic stage progression");
        AssertEqual(false, test.Pushes.Any(push => push.Name == nameof(NotifyStageData)), "Matrix result cannot emit generic stage unlock rewards");
        return response;
    }

    private static void AssertTheatre3BattlePlan(Theatre3Case test, PreFightResponse response)
    {
        var pending = test.State.Fight ?? throw new InvalidDataException("PreFight did not durably authorize a Matrix battle.");
        var template = TableReaderV2.Parse<Theatre3FightStageTemplateTable>().Single(row => row.Id == pending.FightTemplateId);
        var body = JObject.Parse(MessagePackSerializer.ConvertToJson(MessagePackSerializer.Serialize(response)))["FightData"]!;
        var team = test.Data.CurTeamData!;
        AssertEqual(pending.Seed, response.FightData.Seed, "Wire seed is the authorized durable seed");
        AssertEqual(pending.StageId, response.FightData.StageId, "Selected source template authorizes the deployed stage");
        var role = body["RoleData"]!.Single(value => value!.Value<long>("Id") == test.Session.player.PlayerData.Id)!;
        AssertEqual(team.CaptainPos - 1, role.Value<int>("CaptainIndex"), "Native captain index is zero-based");
        AssertEqual(team.FirstFightPos - 1, role.Value<int>("FirstFightPos"), "Native first fighter index is zero-based");
        var recruits = TableReaderV2.Parse<Theatre3CharacterRecruitTable>();
        var expectedCharacters = team.CardIds.Select((id, index) => id != 0 ? id :
            team.RobotIds[index] != 0 ? recruits.First(row => row.RobotId == team.RobotIds[index]).CharacterId : 0).Where(id => id > 0);
        AssertPreFightDeployedCharacterIds(response, test.Session.player.PlayerData.Id, expectedCharacters.Select(id => (long)id).ToList(),
            "Matrix deploys exactly its persisted recruited team");
        var groupIds = template.MonsterGroupId.Where(id => id > 0).ToList();
        int tier = template.QubitValue.Select((value, index) => (Parts: value.Split('|'), Index: index))
            .Where(value => (value.Parts[0] == "1" ? test.Data.QubitValueA : test.Data.QubitValueB) >= int.Parse(value.Parts[1]))
            .OrderByDescending(value => int.Parse(value.Parts[1])).Select(value => value.Index).DefaultIfEmpty(-1).First();
        AssertEqual(tier, pending.QuantumTier, "Authorized quantum tier is the highest satisfied source threshold");
        if (tier >= 0) groupIds[Convert.ToInt32(template.GroupOrder) - 1] = template.QubitMonsterGroup[tier];
        var waves = TableReaderV2.Parse<Theatre3FightExtraWaveTable>();
        foreach (var wave in pending.ExtraWaveIds.GroupBy(id => id))
        {
            var source = waves.Single(row => row.Id == wave.Key);
            AssertEqual(template.ExtraWaveGroupId, (int?)source.GroupId, "Extra wave belongs to the selected stage's source group");
            AssertEqual(true, wave.Count() <= source.UpperLimit, "Extra-wave occurrence ceiling is enforced");
        }
        groupIds.AddRange(pending.ExtraWaveIds.Select(id => waves.Single(row => row.Id == id).MonsterGroupId));
        var sourceGroups = TableReaderV2.Parse<Theatre3MonsterGroupTable>();
        var monsters = TableReaderV2.Parse<Theatre3MonsterTable>();
        JArray actualGroups = (JArray)body["NpcGroupList"]!;
        AssertEqual(groupIds.Count, actualGroups.Count, "Native NPC waves include exactly the persisted source plan");
        for (int group = 0; group < groupIds.Count; group++)
        {
            var expected = sourceGroups.Single(row => row.Id == groupIds[group]).MonsterIds.Select(id => monsters.Single(row => row.Id == id)).ToList();
            JArray actual = (JArray)actualGroups[group]["NpcList"]!;
            AssertEqual(expected.Count, actual.Count, "Each native wave retains source monster multiplicity");
            for (int npc = 0; npc < expected.Count; npc++)
            {
                AssertEqual(expected[npc].NpcId, actual[npc].Value<int>("NpcId"), "Wave deploys source NPC identity");
                AssertEqual(expected[npc].Level, actual[npc].Value<int>("Level"), "Wave deploys source NPC level");
                string magic = string.Join(",", expected[npc].BuffIds.Select((id, index) => $"{id}:{expected[npc].BuffLevel[index]}"));
                AssertEqual(magic, string.Join(",", actual[npc]["MagicInfos"]!.Select(value => $"{value!.Value<int>("MagicId")}:{value.Value<int>("Level")}")),
                    "NPC source buffs retain paired levels instead of dropping or shifting operands");
            }
        }
        var difficulty = TableReaderV2.Parse<Theatre3DifficultyTable>().Single(row => row.Id == test.Data.DifficultyId);
        var expectedEvents = template.FightEventIds.Concat(difficulty.FightEvents).Where(id => id > 0).ToHashSet();
        AssertEqual(true, expectedEvents.SetEquals(body["EventIds"]!.Values<int>().Where(id => id > 0)), "Fight difficulty and selected template compose global battle events");
        var parameters = (JObject)body["StageParams"]!;
        AssertEqual(template.Mode, int.Parse(parameters.Value<string>("Mode")!), "Native mode selects the source battle behavior");
        AssertEqual(string.Join(",", template.ModeParams), string.Join(",", JArray.Parse(parameters.Value<string>("ModeParams")!).Values<int>()),
            "Native mode operand list is a JSON array, not a scalar or comma-separated fallback");
        AssertEqual(template.BaseHp, int.Parse(parameters.Value<string>("Theater3BaseHp")!), "Monster scaling reads the native Theater3BaseHp key");
        AssertEqual(Convert.ToInt32(template.IsQubitStage), int.Parse(parameters.Value<string>("IsQubitStage")!), "Native quantum branch selects its authored route");
        AssertEqual(test.Data.QubitValueA, int.Parse(parameters.Value<string>("Theatre3QubitValueA")!), "Native quantum A branch sees durable channel A");
        AssertEqual(test.Data.QubitValueB, int.Parse(parameters.Value<string>("Theatre3QubitValueB")!), "Native quantum B branch sees durable channel B");
        if (pending.ExtraWaveIds.Count > 0)
        {
            AssertEqual(actualGroups.Count, int.Parse(parameters.Value<string>("Theater3ExtraWaveIndex")!), "Timed extra-wave index addresses the final native NPC group using one-based indexing");
            AssertEqual(Convert.ToInt32(template.ExtraWaveTime), int.Parse(parameters.Value<string>("Theater3ExtraWaveTime")!), "Timed wave uses the authored spawn time");
        }
    }

    private static void PrepareTheatre3BattleFixture(Theatre3Case test, Theatre3FightStageTemplateTable template, bool survive)
    {
        test.Start();
        // Explicit source-controlled branch fixture, not a claimed traversal of the random map.
        var node = TableReaderV2.Parse<Theatre3NodeTable>().First(row => (row.UnDefeatNode == 1) == survive);
        var chapter = TableReaderV2.Parse<Theatre3ChapterTable>().Single(row => row.Id == node.ChapterId);
        var fight = TableReaderV2.Parse<Theatre3FightNodeTable>().First(row => row.TemplateId == template.TemplateId);
        test.Data.CurChapterId = node.ChapterId;
        test.Data.CurChapterDb = new() { ChapterId = node.ChapterId, ConnectChapterId = Convert.ToInt32(chapter.ConnectChapter) };
        test.Data.Items.Clear();
        test.Data.Equips.Clear();
        test.State.EffectCounters.Clear();
        test.State.AppliedEffectTriggers.Clear();
        var slot = new Theatre3NodeSlot { SlotId = 1, SlotType = 1 };
        object mutation = Theatre3Mutation(test);
        Theatre3EffectCall("InitializeFightSlot", mutation, slot, fight.Id);
        slot.FightTemplateId = template.Id;
        var state = Theatre3EffectState(mutation);
        state.Data.CurChapterDb!.Steps.Add(new()
        {
            Uid = ++state.NextUid,
            StepType = 2,
            NodeData = new() { ChapterId = node.ChapterId, NodeId = node.Id, Slots = [slot] }
        });
        test.Session.player.Theatre3 = state;
        test.SaveFixture();
        test.Call(nameof(Theatre3SelectNodeRequest), new Theatre3SelectNodeRequest { NodeId = node.Id, SlotId = slot.SlotId });
    }

    private static void ValidateTheatre3CombatCompatibility()
    {
        ValidateTheatre3QuantumBattleCompatibility();
        var templates = TableReaderV2.Parse<Theatre3FightStageTemplateTable>();
        var stageRows = TableReaderV2.Parse<AscNet.Table.V2.share.fuben.StageTable>();
        var choices = templates.Where(row => stageRows.Any(stage => stage.StageId == row.StageId && Convert.ToInt32(stage.Restartable) != 0)
            && TableReaderV2.Parse<Theatre3FightNodeTable>().Any(node => node.TemplateId == row.TemplateId))
            .GroupBy(row => row.StageId).Take(2).Select(group => group.First()).ToList();
        AssertEqual(2, choices.Count, "Combat lifecycle exercises two distinct source battle stages");
        foreach (var template in choices)
        {
            using Theatre3Case test = new($"combat-{template.Id}");
            PrepareTheatre3BattleFixture(test, template, false);
            PreFightRequest request = Theatre3PreFightRequest(test);
            var wrong = MessagePackSerializer.Deserialize<PreFightRequest>(MessagePackSerializer.Serialize(request));
            wrong.PreFightData.StageId = checked((uint)templates.First(row => row.StageId != template.StageId).StageId);
            test.Reject(nameof(PreFightRequest), wrong, "A different Matrix stage is not selected");
            wrong = MessagePackSerializer.Deserialize<PreFightRequest>(MessagePackSerializer.Serialize(request));
            wrong.PreFightData.ChallengeCount = 2;
            test.Reject(nameof(PreFightRequest), wrong, "Matrix cannot multiply a battle challenge");
            wrong.PreFightData.ChallengeCount = 1;
            wrong.PreFightData.RobotIds = [int.MaxValue, 0, 0];
            test.Reject(nameof(PreFightRequest), wrong, "Unrecruited robot cannot replace persisted deployment");
            test.Call(nameof(PreFightRequest), request);
            var entered = MessagePackSerializer.Deserialize<PreFightResponse>(test.LastResponseContent);
            AssertTheatre3BattlePlan(test, entered);
            long fightId = entered.FightData.FightId;
            byte[] plan = test.State.Fight!.PreFightPayload!.ToArray();
            test.Relog("active Matrix fight");
            test.Call(nameof(PreFightRequest), request);
            AssertEqual(true, plan.SequenceEqual(test.State.Fight!.PreFightPayload!), "Relog reuses authorized waves, equipment and unsigned seed");
            AssertEqual(fightId, test.State.Fight.FightId, "Reconnect does not authorize a new fight identity");
            var foreign = CreateMissingStageSettleRequest(request.PreFightData.StageId, fightId + 1, test.Session.player.PlayerData.Id);
            test.Reject(nameof(FightSettleRequest), foreign, "Foreign fight identity cannot settle the selected node");
            foreign.Result.FightId = fightId;
            foreign.Result.StageId = checked((uint)templates.First(row => row.StageId != template.StageId).StageId);
            test.Reject(nameof(FightSettleRequest), foreign, "Foreign stage cannot consume the live fight");
            foreign.Result.StageId = request.PreFightData.StageId;
            foreign.Result.NpcDpsTable[1] = new() { CharacterId = test.Data.Characters.First().CharacterId, RoleId = checked((int)test.Session.player.PlayerData.Id + 1) };
            test.Reject(nameof(FightSettleRequest), foreign, "DPS cannot attribute deployed character damage to another owner");
            foreign.Result.NpcDpsTable.Clear();
            foreign.Result.RebootCount = 1;
            test.Reject(nameof(FightSettleRequest), foreign, "Result cannot manufacture a paid resurrection");
            foreign.Result.RebootCount = 0;
            foreign.Result.SettleFrame = -1;
            test.Reject(nameof(FightSettleRequest), foreign, "Negative frame range cannot settle");
            test.Reject(nameof(FightRebootRequest), new FightRebootRequest { FightId = checked((int)fightId + 1), RebootCount = 1 }, "Foreign reboot cannot spend");
            test.Reject(nameof(FightRestartRequest), new FightRestartRequest { FightId = checked((int)fightId + 1) }, "Foreign restart cannot spend");
            var profile = TableReaderV2.Parse<Theatre3RebootTable>().Single(row => row.Id ==
                TableReaderV2.Parse<Theatre3DifficultyTable>().Single(row => row.Id == test.Data.DifficultyId).RebootId);
            test.Fund(96189, 0);
            test.SaveFixture();
            test.Reject(nameof(FightRebootRequest), new FightRebootRequest { FightId = (int)fightId, RebootCount = 1 }, "Unfunded revival cannot advance authorization");
            test.Fund(96189, profile.RebootCost);
            test.SaveFixture();
            test.Call(nameof(FightRebootRequest), new FightRebootRequest { FightId = (int)fightId, RebootCount = 1 });
            AssertEqual(0L, test.Balance(96189), "Revival debits the difficulty profile's numeric cost");
            test.Repeat(nameof(FightRebootRequest), new FightRebootRequest { FightId = (int)fightId, RebootCount = 1 }, "Reboot count replay");
            test.Reject(nameof(FightRebootRequest), new FightRebootRequest { FightId = (int)fightId, RebootCount = 3 }, "Revival count cannot skip");
            // Type23's numeric override is tested at the real spending boundary.
            var overrideEffect = TableReaderV2.Parse<Theatre3SystemEffectTable>().First(row => row.Type == 23);
            var overrideGroup = TableReaderV2.Parse<Theatre3EffectGroupTable>().First(row => row.SystemEvents.Contains(overrideEffect.Id));
            test.State.EffectCounters[$"eventGroup:999:{overrideGroup.Id}"] = overrideGroup.Id;
            test.Fund(96189, (int)overrideEffect.Params[0]);
            test.SaveFixture();
            test.Call(nameof(FightRebootRequest), new FightRebootRequest { FightId = (int)fightId, RebootCount = 2 });
            AssertEqual(0L, test.Balance(96189), "Reboot effect spends its numeric override, not prose price");
            test.Fund(96189, profile.FubenRestartCost);
            test.SaveFixture();
            int attempts = test.State.Fight!.ReviveCostCount;
            var restart = new FightRestartRequest { FightId = (int)fightId };
            test.Call(nameof(FightRestartRequest), restart);
            int restartPacket = test.LastPacketId;
            AssertEqual(0L, test.Balance(96189), "Restart debits its own source cost");
            AssertEqual(attempts + 1, test.State.Fight!.ReviveCostCount, "Restart consumes the shared attempt ceiling");
            AssertEqual(0, test.State.Fight.RebootCount, "Restart resets only the current attempt's reboot counter");
            byte[] restarted = test.State.Fight.ToBson();
            test.Call(nameof(FightRestartRequest), restart, reusePacketId: restartPacket);
            AssertEqual(true, restarted.SequenceEqual(test.State.Fight!.ToBson()), "Same restart packet preserves seed and attempt count");
            test.State.Fight.ReviveCostCount = profile.MaxRebootCount;
            test.Fund(96189, 1000);
            test.SaveFixture();
            test.Reject(nameof(FightRebootRequest), new FightRebootRequest { FightId = (int)fightId, RebootCount = 1 }, "Source revival ceiling rejects even funded attempts");
            test.Reject(nameof(FightRestartRequest), restart, "Source restart ceiling rejects even funded attempts");
            var settle = CreateMissingStageSettleRequest(request.PreFightData.StageId, fightId, test.Session.player.PlayerData.Id);
            test.Call(nameof(FightSettleRequest), settle);
            AssertEqual(3, test.Step.StepType, "Victory opens claimable fight rewards");
            test.Repeat(nameof(FightSettleRequest), settle, "Completed fight settlement replay");
            test.Reject(nameof(Theatre3RecvFightRewardRequest), new Theatre3RecvFightRewardRequest { Uid = int.MaxValue }, "Unoffered reward UID cannot claim");
            var goldReward = test.Step.FightRewards.First(reward => reward.RewardType == 2);
            long goldBefore = test.Balance(96189);
            test.Call(nameof(Theatre3RecvFightRewardRequest), new Theatre3RecvFightRewardRequest { Uid = goldReward.Uid });
            int expectedGold = TableReaderV2.Parse<Theatre3GoldTable>().Single(row => row.Id == goldReward.ConfigId).Count * goldReward.Count;
            AssertEqual(goldBefore + expectedGold, test.Balance(96189), "Claim delivers the source gold reward exactly once");
            test.Repeat(nameof(Theatre3RecvFightRewardRequest), new Theatre3RecvFightRewardRequest { Uid = goldReward.Uid }, "Reward claim replay");
            test.Relog("claimed fight reward");
        }
        foreach ((bool survive, bool leave) in new[] { (false, false), (true, false), (false, true), (true, true) })
        {
            using Theatre3Case test = new($"loss-{survive}-{leave}");
            PrepareTheatre3BattleFixture(test, choices[0], survive);
            int cleared = test.State.RunNodeCount;
            test.Call(nameof(PreFightRequest), Theatre3PreFightRequest(test));
            var fight = test.State.Fight!;
            if (leave) test.Call(nameof(LeaveFightRequest), new LeaveFightRequest());
            else
            {
                var loss = CreateMissingStageSettleRequest(fight.StageId, fight.FightId, test.Session.player.PlayerData.Id);
                loss.Result.IsWin = false;
                test.Call(nameof(FightSettleRequest), loss);
                test.Repeat(nameof(FightSettleRequest), loss, "Loss receipt cannot settle twice");
            }
            AssertEqual(true, test.State.Fight is null, "Loss or leave revokes the old attempt");
            AssertEqual(survive, test.Data.CurChapterId > 0, "Only source UnDefeatNode keeps the adventure active after loss");
            if (survive) AssertEqual(cleared + 1, test.State.RunNodeCount, "Protected loss advances the source node rather than trapping the map");
            else AssertEqual(true, test.State.LastSettle is not null, "Terminal loss persists adventure settlement");
            if (leave) test.Repeat(nameof(LeaveFightRequest), new LeaveFightRequest(), "Leave replay cannot settle or grant twice");
            test.Relog("loss outcome");
        }
        foreach ((bool selected, bool completed) in new[] { (false, true), (true, false), (true, true) })
        {
            using Theatre3Case test = new($"extra-wave-{selected}-{completed}");
            var template = templates.First(row => row.ExtraWaveGroupId > 0 && row.ExtraWaveTime > 0 &&
                TableReaderV2.Parse<Theatre3FightNodeTable>().Any(node => node.TemplateId == row.TemplateId));
            PrepareTheatre3BattleFixture(test, template, false);
            var request = Theatre3PreFightRequest(test);
            test.Call(nameof(PreFightRequest), request);
            // A source-controlled durable branch fixture avoids asserting random selection frequency.
            var pending = test.State.Fight!;
            var wave = TableReaderV2.Parse<Theatre3FightExtraWaveTable>().First(row => row.GroupId == template.ExtraWaveGroupId && row.Weight > 0 && row.ItemBoxId > 0);
            var plan = MessagePackSerializer.Deserialize<PreFightResponse.PreFightResponseFightData>(pending.PreFightPayload!);
            var npcGroups = ((System.Collections.IEnumerable)(plan.NpcGroupList
                ?? throw new InvalidDataException("Source-controlled battle omitted NPC groups."))).Cast<object>().ToList();
            int baseCount = template.MonsterGroupId.Count(id => id > 0);
            while (npcGroups.Count > baseCount) npcGroups.RemoveAt(npcGroups.Count - 1);
            var rawParameters = (System.Collections.IDictionary)(plan.StageParams
                ?? throw new InvalidDataException("Source-controlled battle omitted native parameters."));
            var parameters = rawParameters.Keys.Cast<object>().ToDictionary(key => (string)key,
                key => Convert.ToString(rawParameters[key]) ?? throw new InvalidDataException("Native stage parameter is null."));
            parameters.Remove("Theater3ExtraWaveIndex");
            parameters.Remove("Theater3ExtraWaveTime");
            pending.ExtraWaveIds.Clear();
            if (selected)
            {
                pending.ExtraWaveIds.Add(wave.Id);
                var monsterIds = TableReaderV2.Parse<Theatre3MonsterGroupTable>().Single(row => row.Id == wave.MonsterGroupId).MonsterIds;
                npcGroups.Add(new
                {
                    NpcList = monsterIds.Select(id =>
                {
                    var monster = TableReaderV2.Parse<Theatre3MonsterTable>().Single(row => row.Id == id);
                    return new
                    {
                        monster.NpcId,
                        monster.Level,
                        BufferIds = Array.Empty<int>(),
                        AttrTable = new Dictionary<int, object>(),
                        MagicInfos = monster.BuffIds.Select((buff, index) => new { MagicId = buff, Level = monster.BuffLevel[index] }).ToList()
                    };
                }).ToList()
                });
                parameters["Theater3ExtraWaveIndex"] = npcGroups.Count.ToString();
                parameters["Theater3ExtraWaveTime"] = Convert.ToInt32(template.ExtraWaveTime).ToString();
            }
            plan.NpcGroupList = npcGroups;
            plan.StageParams = parameters;
            pending.PreFightPayload = MessagePackSerializer.Serialize(plan);
            test.SaveFixture();
            test.Relog("source-controlled extra wave");
            test.Call(nameof(PreFightRequest), request);
            AssertTheatre3BattlePlan(test, MessagePackSerializer.Deserialize<PreFightResponse>(test.LastResponseContent));
            int oldRewards = Theatre3BattleSlot(test).NodeRewards.Count;
            var settle = CreateMissingStageSettleRequest(pending.StageId, pending.FightId, test.Session.player.PlayerData.Id);
            settle.Result.StringToIntRecord = new Dictionary<string, int> { ["Theater3ExtraWaveWin"] = completed ? 1 : 0 };
            test.Call(nameof(FightSettleRequest), settle);
            AssertEqual(oldRewards + (selected && completed ? 1 : 0), test.Step.FightRewards.Count,
                "Extra-wave reward requires both durable selection and the native completion record");
            if (selected && completed)
                AssertEqual(wave.ItemBoxId, test.Step.FightRewards.Last().ConfigId, "Extra completion offers the selected wave's source item box");
            test.Repeat(nameof(FightSettleRequest), settle, "Extra-wave result replay cannot append another reward");
        }
        using (Theatre3Case test = new("fight-reward-journal-recovery"))
        {
            PrepareTheatre3BattleFixture(test, choices[0], false);
            var growth = TableReaderV2.Parse<Theatre3SystemEffectTable>().First(row => row.Type == 20);
            var group = TableReaderV2.Parse<Theatre3EffectGroupTable>().First(row => row.SystemEvents.Contains(growth.Id));
            test.State.EffectCounters[$"eventGroup:991:{group.Id}"] = group.Id;
            test.SaveFixture();
            test.Call(nameof(PreFightRequest), Theatre3PreFightRequest(test));
            var fight = test.State.Fight!;
            var settle = CreateMissingStageSettleRequest(fight.StageId, fight.FightId, test.Session.player.PlayerData.Id);
            long before = test.Balance(96189);
            test.Inventories.ThrowOnReplaceOne = true;
            bool persistenceFailed = false;
            try { test.Call(nameof(FightSettleRequest), settle); }
            catch (InvalidDataException exception) when (exception.InnerException is MongoDB.Driver.MongoException)
            {
                persistenceFailed = true;
            }
            finally { test.Inventories.ThrowOnReplaceOne = false; }
            AssertEqual(true, persistenceFailed, "Injected inventory persistence failure propagates rather than acknowledging an uncommitted battle");
            AssertEqual(true, test.State.PendingMutation is not null, "Failed battle award retains its durable intended result");
            AssertEqual(0, test.Pushes.Count, "Failed battle award emits no success outcome");
            AssertEqual(false, test.Harness.TryReadAvailablePacket("failed battle award", out _), "Persistence failure cannot send a successful wire response or reward push");
            test.Call(nameof(FightSettleRequest), settle);
            int amount = TableReaderV2.Parse<Theatre3GoldTable>().Single(row => row.Id == (int)growth.Params[1]).Count;
            AssertEqual(before + amount, test.Balance(96189), "Actual settlement retry completes its pending award");
            AssertEqual(1, test.State.RunFightCount, "Recovered request does not discard or replay the battle transition");
            test.Repeat(nameof(FightSettleRequest), settle, "Recovered settlement delivers no second award");
            test.Relog("recovered battle award");
        }
    }

    private static void ValidateTheatre3QuantumBattleCompatibility()
    {
        var templates = TableReaderV2.Parse<Theatre3FightStageTemplateTable>()
            .Where(row => row.QubitValue.Count == 2 && TableReaderV2.Parse<Theatre3FightNodeTable>().Any(node => node.TemplateId == row.TemplateId))
            .GroupBy(row => (Route: Convert.ToInt32(row.IsQubitStage), Channel: row.QubitValue[0].Split('|')[0]))
            .Select(group => group.First()).ToList();
        AssertEqual(true, templates.Select(row => Convert.ToInt32(row.IsQubitStage)).ToHashSet().SetEquals([0, 1]),
            "Quantum fixtures cover both authored routes");
        AssertEqual(true, templates.Select(row => row.QubitValue[0].Split('|')[0]).ToHashSet().SetEquals(["1", "2"]),
            "Quantum fixtures exercise both currency channels");
        foreach (var template in templates)
        {
            int channel = int.Parse(template.QubitValue[0].Split('|')[0]);
            int first = int.Parse(template.QubitValue[0].Split('|')[1]);
            int second = int.Parse(template.QubitValue[1].Split('|')[1]);
            foreach ((int value, int tier) in new[] { (first - 1, -1), (first, 0), (second - 1, 0), (second, 1) })
            {
                using Theatre3Case test = new($"quantum-plan-{template.Id}-{value}");
                PrepareTheatre3BattleFixture(test, template, false);
                test.Data.QubitValueA = channel == 1 ? value : 0;
                test.Data.QubitValueB = channel == 2 ? value : 0;
                test.SaveFixture();
                var request = Theatre3PreFightRequest(test);
                test.Call(nameof(PreFightRequest), request);
                var response = MessagePackSerializer.Deserialize<PreFightResponse>(test.LastResponseContent);
                AssertTheatre3BattlePlan(test, response);
                AssertEqual(tier, test.State.Fight!.QuantumTier, "Threshold boundary chooses base, first replacement, or higher replacement");
                byte[] authorizedPlan = test.State.Fight.PreFightPayload!.ToArray();
                test.Relog("quantum threshold authorization");
                test.Call(nameof(PreFightRequest), request);
                AssertEqual(true, authorizedPlan.SequenceEqual(test.State.Fight!.PreFightPayload!), "Quantum replacement cannot reroll on reconnect");
                var slot = Theatre3BattleSlot(test);
                int originalRewards = slot.NodeRewards.Count;
                var settle = CreateMissingStageSettleRequest(response.FightData.StageId, response.FightData.FightId, test.Session.player.PlayerData.Id);
                test.Call(nameof(FightSettleRequest), settle);
                var rewards = test.Data.CurChapterDb!.Steps.Last(step => step.StepType == 3).FightRewards.Skip(originalRewards).ToList();
                int itemGroup = tier < 0 ? 0 : template.QubitItemBoxGroup.ElementAtOrDefault(tier);
                int equipGroup = tier < 0 ? 0 : template.QubitEquipBoxGroup.ElementAtOrDefault(tier);
                int goldGroup = tier < 0 ? 0 : template.QubitGoldGroup.ElementAtOrDefault(tier);
                AssertEqual((itemGroup > 0 ? 1 : 0) + (equipGroup > 0 ? 1 : 0) + (goldGroup > 0 ? 1 : 0), rewards.Count,
                    "Quantum win awards only the selected tier, never all satisfied tiers");
                foreach (var reward in rewards)
                {
                    bool allowed = reward.RewardType switch
                    {
                        1 => TableReaderV2.Parse<Theatre3ItemBoxTable>().Any(row => row.Id == reward.ConfigId && row.BoxGroupId == itemGroup),
                        2 => TableReaderV2.Parse<Theatre3GoldGroupTable>().Any(row => row.GoldId == reward.ConfigId && row.GroupId == goldGroup),
                        3 => TableReaderV2.Parse<Theatre3EquipBoxGroupTable>().Any(row => row.EquipBoxId == reward.ConfigId && row.GroupId == equipGroup),
                        _ => false
                    };
                    AssertEqual(true, allowed, "Quantum reward identity belongs to the selected tier's source pool");
                }
                test.Repeat(nameof(FightSettleRequest), settle, "Quantum settlement cannot append another tier award");
            }
        }
    }

    private static object Theatre3Mutation(Theatre3Case test)
    {
        Type type = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.Theatre3Module")
            .GetNestedType("Mutation", BindingFlags.NonPublic)!;
        return Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic,
            null, [test.Session], null)!;
    }

    private static object? Theatre3EffectCall(string name, object mutation, params object[] arguments)
    {
        object[] values = [mutation, .. arguments];
        MethodInfo method = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.Theatre3Module")
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Single(method => method.Name == name && method.GetParameters().Length == values.Length);
        return method.Invoke(null, values);
    }

    private static PlayerTheatre3State Theatre3EffectState(object mutation) =>
        (PlayerTheatre3State)mutation.GetType().GetProperty("State")!.GetValue(mutation)!;

    private static long Theatre3EffectBalance(object mutation, int id = 96189) =>
        (long)mutation.GetType().GetMethod("Balance")!.Invoke(mutation, [id])!;

    private static void Theatre3Trigger(object mutation, string name, int identity)
    {
        Type type = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.Theatre3EffectTrigger");
        Theatre3EffectCall("ApplyEffectTrigger", mutation, Enum.Parse(type, name), identity, 1);
    }

    private static void ValidateTheatre3EffectCompatibility()
    {
        ValidateTheatre3QuantumGrowthContinuity();
        // These are executable invariants of the documented local policy, not retail equivalence.
        int[] types = [3, 4, 5, 8, 12, 13, 16, 17, 18, 19, 20, 21, 22, 23, 25, 26, 27, 28, 29, 30, 32, 33, 34, 35];
        var systems = TableReaderV2.Parse<Theatre3SystemEffectTable>();
        var groups = TableReaderV2.Parse<Theatre3EffectGroupTable>();
        AssertEqual(string.Join(",", types), string.Join(",", systems.Select(row => row.Type).Distinct().Order()),
            "Every imported system-effect type has an observable policy check");
        foreach (int type in types)
        {
            using Theatre3Case test = new($"effect-{type}");
            test.Start();
            test.Data.Items.Clear();
            test.Data.Equips.Clear();
            test.Data.UnlockStrengthTree.Clear();
            test.Data.QubitValueA = test.Data.QubitValueB = 0;
            test.State.EffectCounters.Clear();
            test.State.AppliedEffectTriggers.Clear();
            test.Fund(96189, 10000);
            var pair = (from effect in systems
                        where effect.Type == type
                        from effectGroup in groups
                        where effectGroup.SystemEvents.Contains(effect.Id)
                        orderby effect.Id, effectGroup.Id
                        select (effect, effectGroup)).First();
            var row = pair.effect;
            var p = row.Params;
            if (type == 8) test.Data.CurChapterId = test.Data.CurChapterDb!.ChapterId = (int)p[0];
            if (type is 27 or 35)
            {
                var chapter = TableReaderV2.Parse<Theatre3ChapterTable>().First(value => Convert.ToInt32(value.ConnectChapter) > 0);
                test.Data.CurChapterId = chapter.Id;
                test.Data.CurChapterDb = new()
                {
                    ChapterId = chapter.Id,
                    ConnectChapterId = Convert.ToInt32(chapter.ConnectChapter),
                    Steps = [new() { Uid = ++test.State.NextUid, StepType = 2, NodeData = new(), ConnectNodeData = new() }]
                };
            }
            if (type == 17)
            {
                var equip = TableReaderV2.Parse<Theatre3EquipTable>().Single(value => value.Id == (int)p[0]);
                test.Data.Equips.Add(new() { EquipId = equip.Id, SuitId = equip.SuitId, Pos = 1 });
            }
            object mutation = Theatre3Mutation(test);
            var state = Theatre3EffectState(mutation);
            int oldChapter = state.Data.CurChapterId;
            int oldSteps = state.Data.CurChapterDb!.Steps.Count;
            long before = Theatre3EffectBalance(mutation);
            if (type == 17) Theatre3Trigger(mutation, "EquipmentChanged", 70000 + type);
            else Theatre3EffectCall("ApplyEffectGroup", mutation, pair.effectGroup.Id, 70000 + type);
            switch (type)
            {
                case 3:
                    {
                        // Two distinct source instances add their percentages, not multiply them.
                        Theatre3EffectCall("ApplyEffectGroup", mutation, pair.effectGroup.Id, 80000 + type);
                        int fightGoldId = TableReaderV2.Parse<Theatre3FightNodeTable>().SelectMany(value => value.GoldIds).First(id => id > 0);
                        var amount = TableReaderV2.Parse<Theatre3GoldTable>().Single(value => value.Id == fightGoldId).Count;
                        Theatre3EffectCall("AwardNodeReward", mutation, new Theatre3NodeReward
                        {
                            Uid = ++state.NextUid,
                            RewardType = 2,
                            ConfigId = fightGoldId,
                            Count = 1
                        });
                        AssertEqual(before + (long)Math.Floor(amount * (1 + 2 * p[0] / 100)), Theatre3EffectBalance(mutation), "Stacked gold effects increase an authored fight-node award additively");
                        long afterFight = Theatre3EffectBalance(mutation);
                        var fixedGold = TableReaderV2.Parse<Theatre3GoldGroupTable>().GroupBy(value => value.GroupId).First(values => values.Count() == 1).Single();
                        int fixedAmount = TableReaderV2.Parse<Theatre3GoldTable>().Single(value => value.Id == fixedGold.GoldId).Count;
                        Theatre3EffectCall("AwardGoldGroup", mutation, fixedGold.GroupId, 1);
                        AssertEqual(afterFight + fixedAmount, Theatre3EffectBalance(mutation), "Fight-drop bonuses cannot multiply a fixed GoldGroup box");
                        break;
                    }
                case 4:
                    Theatre3Trigger(mutation, "NodeCompleted", 101);
                    Theatre3Trigger(mutation, "NodeCompleted", 102);
                    AssertEqual(before + 2 * (long)p[1], Theatre3EffectBalance(mutation, (int)p[0]), "Distinct nodes each award source currency once");
                    break;
                case 5:
                case 13:
                    {
                        var shop = TableReaderV2.Parse<Theatre3NodeShopTable>().First(value => value.ShopType == 1 && value.DiscountNum > 0);
                        var slot = new Theatre3NodeSlot { ShopId = shop.Id };
                        Theatre3EffectCall("GenerateShopItems", mutation, slot);
                        int bonus = pair.effectGroup.SystemEvents.Select(id => systems.Single(value => value.Id == id)).Where(value => value.Type == 5).Sum(value => (int)value.Params[0]);
                        double factor = pair.effectGroup.SystemEvents.Select(id => systems.Single(value => value.Id == id)).Where(value => value.Type == 13).Aggregate(1.0, (value, effect) => value * effect.Params[0]);
                        var discounted = slot.ShopItems.Where(value => value.DiscountPercent > 0).ToList();
                        AssertEqual(Math.Min(slot.ShopItems.Count(value => value.IsLock != 2), shop.DiscountNum + bonus), discounted.Count, "Source slot bonuses constrain actual discounted offers");
                        foreach (var offer in discounted)
                            AssertEqual((int)Math.Ceiling(offer.Price * Math.Clamp(shop.DiscountRate / 100.0 * factor, 0, 1)), offer.DiscountPrice, "Shop cost uses composed source discount rather than prose percent");
                        byte[] offers = slot.ToBson();
                        Theatre3EffectCall("GenerateShopItems", mutation, slot);
                        AssertEqual(true, offers.SequenceEqual(slot.ToBson()), "Discounted offers and identities never reroll on inspection");
                        break;
                    }
                case 8:
                case 21:
                    {
                        if (type == 21) Theatre3Trigger(mutation, "FightWon", 101);
                        var choices = state.Data.CurChapterDb.Steps.Skip(oldSteps).Where(step => step.StepType == 4).ToList();
                        AssertEqual(1, choices.Count, "Item-box effect creates one actionable choice");
                        int boxId = (int)p[type == 8 ? 1 : 0];
                        var box = TableReaderV2.Parse<Theatre3ItemBoxTable>().Single(value => value.Id == boxId);
                        var allowed = TableReaderV2.Parse<Theatre3ItemGroupTable>().Where(value => box.ItemGroupId.Contains(value.GroupId) || value.GroupId == box.SafeGroupId).Select(value => value.ItemId).ToHashSet();
                        AssertEqual(true, choices[0].ItemIds.Count > 0 && choices[0].ItemIds.All(allowed.Contains), "Choice belongs to referenced item box or its source fallback");
                        AssertEqual(choices[0].ItemIds.Count, choices[0].ItemIds.Distinct().Count(), "One box cannot offer the same item twice");
                        break;
                    }
                case 12:
                    AssertEqual(before + (long)p[1], Theatre3EffectBalance(mutation, (int)p[0]), "Acquisition currency is awarded once");
                    break;
                case 16:
                case 22:
                    {
                        Theatre3EffectCall("ApplyEffectGroup", mutation, pair.effectGroup.Id, 80000 + type);
                        string key = type == 16 ? "InitEquipPosCapacity" : "InitCharacterEnergy";
                        int initial = checked((int)TableReaderV2.Parse<Theatre3ConfigTable>().Single(value => value.Key == key).Value);
                        if (type == 16)
                            AssertEqual(true, state.Data.EquipPos.All(value => value.Capacity == initial + 2 * (int)p[0]), "Capacity is recomputed with both owned source instances");
                        else AssertEqual(initial + 2 * (int)p[0], state.Data.MaxEnergy, "Recruitment budget includes both owned source instances");
                        break;
                    }
                case 17:
                    {
                        Theatre3Trigger(mutation, "FightWon", 101);
                        Theatre3Trigger(mutation, "FightWon", 102);
                        object effects = Theatre3EffectCall("GetBattleEffects", mutation, test.Data.Characters.First().CharacterId, 1)!;
                        var levels = (System.Collections.IEnumerable)effects.GetType().GetProperty("LeveledEvents")!.GetValue(effects)!;
                        var entries = levels.Cast<object>().ToList();
                        var equip = TableReaderV2.Parse<Theatre3EquipTable>().Single(value => value.Id == (int)p[0]);
                        var expected = groups.Single(value => value.Id == equip.EffectGroupId).FightEvents.Where(id => id > 0).Order();
                        AssertEqual(string.Join(",", expected), string.Join(",", entries.Select(value => (int)value.GetType().GetProperty("FightEventId")!.GetValue(value)!).Order()), "Growth levels only the referenced equipment's battle events");
                        AssertEqual(true, entries.Count > 0 && entries.All(value => (int)value.GetType().GetProperty("FightEventLevel")!.GetValue(value)! == 2), "Two victories produce level-two native growth events");
                        object other = Theatre3EffectCall("GetBattleEffects", mutation, test.Data.Characters.First().CharacterId, 2)!;
                        AssertEqual(0, ((System.Collections.IEnumerable)other.GetType().GetProperty("LeveledEvents")!.GetValue(other)!).Cast<object>().Count(), "Position-specific growth does not leak to other fighters");
                        break;
                    }
                case 18:
                case 19:
                    Theatre3Trigger(mutation, "FightWon", 101);
                    Theatre3Trigger(mutation, "BossWon", 101);
                    Theatre3Trigger(mutation, "FightWon", 102);
                    AssertEqual(2, state.EffectCounters.GetValueOrDefault($"record:{(int)p[0]}:18"), "Fight counter counts clears rather than treating suit identity as cap");
                    AssertEqual(1, state.EffectCounters.GetValueOrDefault($"record:{(int)p[0]}:19"), "Boss counter excludes ordinary victories");
                    break;
                case 20:
                    Theatre3Trigger(mutation, "FightWon", 101);
                    Theatre3Trigger(mutation, "FightWon", 102);
                    int goldCount = TableReaderV2.Parse<Theatre3GoldTable>().Single(value => value.Id == (int)p[1]).Count;
                    AssertEqual(before + 3L * goldCount, Theatre3EffectBalance(mutation), "Growth rewards follow first-plus-second clear amounts");
                    break;
                case 23:
                    // Real reboot charging with this source is also checked by the combat lifecycle below.
                    Theatre3EffectCall("ApplyEffectGroup", mutation, pair.effectGroup.Id, 80000 + type);
                    object modifiers = Theatre3EffectCall("GetEffects", mutation)!;
                    AssertEqual((int)p[0], (int)modifiers.GetType().GetProperty("RebootCostOverride")!.GetValue(modifiers)!, "Repeated override sources choose minimum cost, not additive cost");
                    break;
                case 25:
                    var boxes = state.Data.CurChapterDb.Steps.Skip(oldSteps).Where(step => step.StepType == 6).ToList();
                    AssertEqual((int)p[1], boxes.Count, "Immediate equipment effect materializes all referenced boxes");
                    AssertEqual(boxes.Count, boxes.Select(step => step.Uid).Distinct().Count(), "Each box has an independent claim identity");
                    AssertEqual(true, boxes.All(step => step.EquipBoxId == (int)p[0] && step.EquipIds.Count > 0), "Each claim contains source-box equipment offers");
                    break;
                case 26:
                    AssertEqual(p[0] != 0, state.Data.ChapterSwitch, "Quantum route gate follows numeric operand");
                    break;
                case 27:
                case 35:
                    var linkedChapter = test.Data.CurChapterDb ?? throw new InvalidDataException("Route-switch fixture lost its paired chapter.");
                    AssertEqual(linkedChapter.ConnectChapterId, state.Data.CurChapterId, "Immediate route switch changes the active connected chapter");
                    AssertEqual(false, oldChapter == state.Data.CurChapterId, "Switch consumes a real route transition");
                    break;
                case 28:
                    int quantum = (int)p[0] == 1 ? state.Data.QubitValueA : state.Data.QubitValueB;
                    AssertEqual(true, quantum >= (int)p[1] && quantum <= (int)p[2], "Quantum acquisition lies within inclusive source bounds without probabilistic expectations");
                    AssertEqual(0, (int)p[0] == 1 ? state.Data.QubitValueB : state.Data.QubitValueA, "Quantum acquisition cannot credit the other channel");
                    break;
                case 29:
                    var workshop = TableReaderV2.Parse<Theatre3WorkShopTable>().First(value => value.Type == (int)p[0]);
                    Theatre3EffectCall("OpenWorkshop", mutation, workshop.Id, 0);
                    AssertEqual(workshop.Count + (int)p[1], state.Data.CurChapterDb.Steps.Last().WorkShopTotalCount, "Workshop effect expands the source operation budget");
                    break;
                case 30:
                    Theatre3Trigger(mutation, "ShopPurchase", 101);
                    AssertEqual(1, state.EffectCounters.GetValueOrDefault("CurBuyCount"), "Lottery hook counts one purchase regardless of outcome");
                    if (state.LastLotteryRewards.TryGetValue(1, out int item))
                    {
                        AssertEqual(true, TableReaderV2.Parse<Theatre3ItemGroupTable>().Any(value => value.GroupId == (int)p[2] && value.ItemId == item), "Lottery item belongs to the referenced weighted group");
                        AssertEqual(true, state.Data.Items.Any(value => value.ItemId == item), "Lottery report corresponds to an actual owned item");
                    }
                    if (state.LastLotteryRewards.TryGetValue(2, out int loss))
                    {
                        AssertEqual(true, -loss >= (int)p[3] && -loss <= (int)p[4], "Lottery debit obeys source absolute bounds, not description percentage");
                        AssertEqual(before + loss, Theatre3EffectBalance(mutation), "Lottery debit matches the reported signed amount");
                    }
                    AssertEqual(true, Theatre3EffectBalance(mutation) >= 0, "Lottery cannot overdraw currency");
                    break;
                case 32:
                    Theatre3Trigger(mutation, "FightWon", 101);
                    Theatre3Trigger(mutation, "FightWon", 102);
                    AssertEqual(2 * (int)p[1], (int)p[0] == 1 ? state.Data.QubitValueA : state.Data.QubitValueB, "Quantum gain counts distinct victories using numeric source amount");
                    break;
                case 33:
                case 34:
                    var equipBox = TableReaderV2.Parse<Theatre3EquipBoxTable>().First(value => value.RefreshCostNum > 0);
                    Theatre3EffectCall("OpenEquipBox", mutation, equipBox.Id, 1, 0);
                    var offerBox = state.Data.CurChapterDb.Steps.Last();
                    if (type == 33) AssertEqual((int)p[0], offerBox.FreeRefreshLimit, "Opened box preserves the additional free refresh allowance");
                    else AssertEqual((int)Math.Floor(equipBox.RefreshCost[0] * (1 - p[0])), (int)Math.Floor(equipBox.RefreshCost[0] * (1 - offerBox.RefreshDiscount)), "Opened box computes reduced paid refresh cost");
                    break;
            }
            byte[] data = state.Data.ToBson();
            long balance = Theatre3EffectBalance(mutation);
            int uid = state.NextUid;
            // Same semantic source and same completion/purchase must not duplicate grants or reroll choices.
            if (type == 17) Theatre3Trigger(mutation, "EquipmentChanged", 70000 + type);
            else Theatre3EffectCall("ApplyEffectGroup", mutation, pair.effectGroup.Id, 70000 + type);
            foreach (string trigger in new[] { "FightWon", "BossWon", "NodeCompleted", "ShopPurchase" })
                if (state.AppliedEffectTriggers.Contains($"{state.RunId}:{trigger}:101:0")) Theatre3Trigger(mutation, trigger, 101);
            AssertEqual(true, data.SequenceEqual(state.Data.ToBson()), $"Effect {type} retry preserves observable state");
            AssertEqual(balance, Theatre3EffectBalance(mutation), $"Effect {type} retry preserves spend and award balance");
            AssertEqual(uid, state.NextUid, $"Effect {type} retry preserves claim identities");
            var restored = BsonSerializer.Deserialize<PlayerTheatre3State>(state.ToBson());
            AssertEqual(true, state.Data.ToBson().SequenceEqual(restored.Data.ToBson()), $"Effect {type} outcomes survive BSON");
        }
    }
    private static void ValidateTheatre3QuantumGrowthContinuity()
    {
        using Theatre3Case test = new("gold-growth-quantum-transition");
        test.Start();
        test.Data.Items.Clear();
        test.Data.Equips.Clear();
        test.Data.UnlockStrengthTree.Clear();
        test.Data.QubitValueA = test.Data.QubitValueB = 0;
        test.State.EffectCounters.Clear();
        test.State.AppliedEffectTriggers.Clear();
        var systems = TableReaderV2.Parse<Theatre3SystemEffectTable>();
        var groups = TableReaderV2.Parse<Theatre3EffectGroupTable>();
        var equip = TableReaderV2.Parse<Theatre3EquipTable>().First(row =>
            groups.Single(group => group.Id == row.EffectGroupId).SystemEvents.Any(id => systems.Any(effect => effect.Id == id && effect.Type == 20))
            && groups.Single(group => group.Id == row.QubitEffectGroupId).SystemEvents.Any(id => systems.Any(effect => effect.Id == id && effect.Type == 20)));
        var ordinary = systems.Single(row => row.Type == 20 && groups.Single(group => group.Id == equip.EffectGroupId).SystemEvents.Contains(row.Id));
        var quantum = systems.Single(row => row.Type == 20 && groups.Single(group => group.Id == equip.QubitEffectGroupId).SystemEvents.Contains(row.Id));
        int ordinaryGold = TableReaderV2.Parse<Theatre3GoldTable>().Single(row => row.Id == (int)ordinary.Params[1]).Count;
        int quantumGold = TableReaderV2.Parse<Theatre3GoldTable>().Single(row => row.Id == (int)quantum.Params[1]).Count;
        test.Data.Equips.Add(new() { EquipId = equip.Id, SuitId = equip.SuitId, Pos = 1 });
        object mutation = Theatre3Mutation(test);
        long initial = Theatre3EffectBalance(mutation);
        for (int clear = 1; clear <= 3; clear++) Theatre3Trigger(mutation, "FightWon", clear);
        AssertEqual(initial + 6L * ordinaryGold, Theatre3EffectBalance(mutation), "Three ordinary victories award first, second and third growth increments");
        long beforeQuantum = Theatre3EffectBalance(mutation);
        Theatre3EffectCall("ActivateQuantumSuit", mutation, equip.SuitId);
        AssertEqual(beforeQuantum, Theatre3EffectBalance(mutation), "Quantization alone cannot mint a victory reward");
        Theatre3Trigger(mutation, "FightWon", 4);
        AssertEqual(beforeQuantum + 4L * quantumGold, Theatre3EffectBalance(mutation), "Fourth victory retains prior clears and uses the new quantum gold amount");
        long after = Theatre3EffectBalance(mutation);
        Theatre3Trigger(mutation, "FightWon", 4);
        AssertEqual(after, Theatre3EffectBalance(mutation), "Quantum victory retry cannot pay the fourth growth reward twice");
    }
}
