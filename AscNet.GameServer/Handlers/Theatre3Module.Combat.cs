using System.Globalization;
using System.Security.Cryptography;
using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Table.V2.share.character;
using AscNet.Table.V2.share.fuben;
using AscNet.Table.V2.share.robot;
using AscNet.Table.V2.share.theatre3;
using MessagePack;
using Newtonsoft.Json;

namespace AscNet.GameServer.Handlers;

internal static partial class Theatre3Module
{
    internal static bool IsCombatStage(uint stageId) => Rows<Theatre3FightStageTemplateTable>().Any(row => row.StageId == stageId);

    private static Theatre3FightState RequireCombat(Mutation mutation, long fightId)
    {
        Theatre3FightState fight = mutation.State.Fight
            ?? throw new ServerCodeException("No Matrix battle is pending.", 20203030);
        Require(fight.FightId > 0 && fight.FightId == fightId, 20003040);
        Theatre3NodeSlot slot = CurrentSlot(mutation);
        Require(slot.Selected == 1 && slot.SlotId == fight.SlotId && slot.FightTemplateId == fight.FightTemplateId, 20203032);
        return fight;
    }

    internal static bool TryPreFight(Session session, PreFightRequest request, out PreFightResponse response)
    {
        response = new();
        if (!IsCombatStage(request.PreFightData.StageId)) return false;
        PreFightResponse prepared = new();
        try
        {
            Commit(session, mutation =>
            {
                Theatre3NodeSlot slot = CurrentSlot(mutation);
                Require(slot.Selected == 1 && slot.FightTemplateId > 0, 20203030);
                Theatre3FightStageTemplateTable template = Rows<Theatre3FightStageTemplateTable>().SingleOrDefault(row => row.Id == slot.FightTemplateId)
                    ?? throw new ServerCodeException("Matrix encounter template is missing.", 20203031);
                Require(template.StageId == request.PreFightData.StageId, 20203032);
                Require(request.PreFightData.ChallengeCount == 1 && !request.PreFightData.IsHasAssist
                    && request.PreFightData.SpeedrunStageId == 0 && request.PreFightData.SelectAreaId == 0, 20203034);
                Theatre3TeamData team = mutation.Data.CurTeamData
                    ?? throw new ServerCodeException("Matrix team is missing.", 20203033);
                ValidateCombatTeam(mutation, request.PreFightData, team);
                if (mutation.State.Fight is { } pending)
                {
                    RequireCombat(mutation, pending.FightId);
                    Require(pending.StageId == request.PreFightData.StageId && pending.PreFightPayload is not null, 20203032);
                    prepared.FightData = MessagePackSerializer.Deserialize<PreFightResponse.PreFightResponseFightData>(pending.PreFightPayload!);
                    return;
                }
                Theatre3Step step = CurrentStep(mutation, 2);
                Theatre3NodeData node = step.NodeData?.ChapterId == mutation.Data.CurChapterId ? step.NodeData : step.ConnectNodeData!;
                mutation.State.Fight = new()
                {
                    FightId = RandomNumberGenerator.GetInt32(1, int.MaxValue),
                    Seed = unchecked((uint)RandomNumberGenerator.GetInt32(int.MinValue, int.MaxValue)),
                    StageId = request.PreFightData.StageId, FightTemplateId = template.Id,
                    NodeId = node.NodeId, SlotId = slot.SlotId, EventId = slot.EventId,
                    StartedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };
                prepared.FightData = BuildCombatData(mutation, team, template, request.PreFightData.GeneralSkill);
                mutation.State.Fight.PreFightPayload = MessagePackSerializer.Serialize(prepared.FightData);
            });
            session.fight = new(request, prepared.FightData.FightId);
        }
        catch (ServerCodeException exception) { prepared = new() { Code = exception.Code }; }
        response = prepared;
        return true;
    }

    private static void ValidateCombatTeam(Mutation mutation, PreFightRequest.PreFightRequestPreFightData request, Theatre3TeamData team)
    {
        ValidateTheatre3Team(mutation, team);
        Require(team.CardIds.Count == 3 && team.RobotIds.Count == 3 && request.CardIds is not null && request.RobotIds is not null
            && request.CardIds.SequenceEqual(team.CardIds.Select(id => checked((uint)id))) && request.RobotIds.SequenceEqual(team.RobotIds)
            && request.CaptainPos == team.CaptainPos && request.FirstFightPos == team.FirstFightPos
            && request.EnterCgIndex == team.EnterCgIndex && request.SettleCgIndex == team.SettleCgIndex, 20203034);
    }

    private static PreFightResponse.PreFightResponseFightData BuildCombatData(Mutation mutation, Theatre3TeamData team, Theatre3FightStageTemplateTable template, int generalSkill)
    {
        Theatre3FightState fight = mutation.State.Fight!;
        StageTable stage = Rows<StageTable>().Single(row => row.StageId == template.StageId);
        Theatre3DifficultyTable difficulty = Rows<Theatre3DifficultyTable>().Single(row => row.Id == mutation.Data.DifficultyId);
        // Installed BehaviorNode + native DoGetFix/DoGetListFromParamsDict consume these exact keys.
        // Neutral 10000 factors are the documented local policy; difficulty still applies its authored events.
        Dictionary<string, string> stageParams = new()
        {
            ["Mode"] = template.Mode.ToString(CultureInfo.InvariantCulture),
            ["ModeParams"] = JsonConvert.SerializeObject(template.ModeParams),
            ["Theater3BaseHp"] = template.BaseHp.ToString(CultureInfo.InvariantCulture),
            ["Theater3AtkFactor"] = "10000",
            ["Theater3HpFactor"] = "10000",
            ["IsQubitStage"] = (template.IsQubitStage ?? 0).ToString(CultureInfo.InvariantCulture),
            ["Theatre3QubitValueA"] = mutation.Data.QubitValueA.ToString(CultureInfo.InvariantCulture),
            ["Theatre3QubitValueB"] = mutation.Data.QubitValueB.ToString(CultureInfo.InvariantCulture)
        };
        // Theatre3 TimeLimit is absent in source; the native base stage owns its time limit.
        PreFightResponse.PreFightResponseFightData data = new()
        {
            FightId = checked((uint)fight.FightId), Seed = fight.Seed, StageId = fight.StageId,
            RebootType = 1, RebootId = difficulty.RebootId,
            PassTimeLimit = Convert.ToInt32(stage.PassTimeLimit),
            Restartable = Convert.ToInt32(stage.Restartable) != 0,
            NormalEventIds = stage.NormalEventId.Select(id => (dynamic)id).ToList(),
            EventIds = template.FightEventIds.Concat(difficulty.FightEvents).Select(id => (dynamic)id).ToList(),
            StageParams = stageParams
        };
        List<int> groups = template.MonsterGroupId.ToList();
        // Documented local rule: highest satisfied channel|threshold replaces GroupOrder's base group.
        // IsQubitStage only selects the active A/B route; it does not disable opposite-route quantum tiers.
        int highestThreshold = -1;
        for (int index = 0; index < template.QubitValue.Count; index++)
        {
            string[] operands = template.QubitValue[index].Split('|');
            if (operands.Length != 2 || !int.TryParse(operands[0], out int channel)
                || !int.TryParse(operands[1], out int threshold) || channel is not (1 or 2) || threshold < 0
                || index >= template.QubitMonsterGroup.Count)
                throw new InvalidDataException("Matrix quantum encounter threshold is invalid.");
            int value = channel == 1 ? mutation.Data.QubitValueA : mutation.Data.QubitValueB;
            if (value >= threshold && threshold > highestThreshold)
            {
                highestThreshold = threshold;
                fight.QuantumTier = index;
            }
        }
        if (fight.QuantumTier >= 0)
        {
            int groupIndex = (template.GroupOrder ?? 0) - 1;
            if (groupIndex < 0 || groupIndex >= groups.Count) throw new InvalidDataException("Matrix quantum replacement group is invalid.");
            groups[groupIndex] = template.QubitMonsterGroup[fight.QuantumTier];
        }
        // Local selection rule: authored rates are basis points (the source includes rates above 100).
        // The native script spawns this separate indexed group at its authored time, not as an ordinary wave.
        if (template.ExtraWaveGroupId is > 0 && template.ExtraWaveRate is > 0
            && RandomNumberGenerator.GetInt32(10000) < template.ExtraWaveRate.Value)
        {
            int limit = Rows<Theatre3FightExtraWaveLimitTable>().SingleOrDefault(row => row.Id == template.ExtraWaveGroupId)?.UpperLimit
                ?? throw new ServerCodeException("Matrix extra-wave group is missing.", 20203055);
            List<Theatre3FightExtraWaveTable> candidates = Rows<Theatre3FightExtraWaveTable>()
                .Where(row => row.GroupId == template.ExtraWaveGroupId && row.Weight > 0).ToList();
            for (int index = 0; index < limit; index++)
            {
                List<Theatre3FightExtraWaveTable> available = candidates.Where(row => fight.ExtraWaveIds.Count(id => id == row.Id) < row.UpperLimit).ToList();
                if (available.Count == 0) break;
                int draw = RandomNumberGenerator.GetInt32(available.Sum(row => row.Weight));
                Theatre3FightExtraWaveTable chosen = available.First(row => (draw -= row.Weight) < 0);
                fight.ExtraWaveIds.Add(chosen.Id);
                groups.Add(chosen.MonsterGroupId);
                stageParams["Theater3ExtraWaveIndex"] = groups.Count.ToString(CultureInfo.InvariantCulture);
                stageParams["Theater3ExtraWaveTime"] = (template.ExtraWaveTime
                    ?? throw new InvalidDataException("Matrix extra-wave spawn time is missing.")).ToString(CultureInfo.InvariantCulture);
            }
        }
        data.NpcGroupList = groups.Select(groupId => new
        {
            NpcList = (Rows<Theatre3MonsterGroupTable>().SingleOrDefault(row => row.Id == groupId)
                ?? throw new ServerCodeException("Matrix monster group is missing.", 20203035)).MonsterIds.Select(monsterId =>
            {
                Theatre3MonsterTable monster = Rows<Theatre3MonsterTable>().SingleOrDefault(row => row.Id == monsterId)
                    ?? throw new ServerCodeException("Matrix monster is missing.", 20203036);
                if (monster.BuffIds.Count != monster.BuffLevel.Count) throw new InvalidDataException("Matrix monster buff levels do not match.");
                return new
                {
                    monster.NpcId, monster.Level, BufferIds = Array.Empty<int>(),
                    MagicInfos = monster.BuffIds.Select((id, index) => new { MagicId = id, Level = monster.BuffLevel[index] }).ToList(),
                    AttrTable = new Dictionary<int, object>()
                };
            }).ToList()
        }).ToList();
        PreFightResponse.PreFightResponseFightData.PreFightResponseFightDataRoleData role = new()
        {
            Id = checked((uint)mutation.Session.player.PlayerData.Id), Camp = 1, Name = mutation.Session.player.PlayerData.Name,
            CaptainIndex = team.CaptainPos - 1, FirstFightPos = team.FirstFightPos - 1,
            EnterCgIndex = team.EnterCgIndex - 1, SettleCgIndex = team.SettleCgIndex - 1, NpcData = []
        };
        List<CharacterData> deployed = [];
        for (int position = 0; position < 3; position++)
        {
            int cardId = team.CardIds[position], robotId = team.RobotIds[position];
            if (cardId == 0 && robotId == 0) continue;
            CharacterData character;
            IReadOnlyList<EquipData> equips;
            int weaponFashionId;
            if (robotId != 0)
            {
                RobotTable robot = Rows<RobotTable>().Single(row => row.Id == robotId);
                var deployment = FightModule.BuildRobotDeployment(robot);
                character = deployment.Character; equips = deployment.Equips; weaponFashionId = robot.WeaponFashion ?? 0;
            }
            else
            {
                character = mutation.Session.character.Characters.Single(row => row.Id == cardId);
                equips = FightModule.BuildTeamPrefabFightEquips(mutation.Session, checked((uint)cardId));
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                weaponFashionId = mutation.Session.character.WeaponFashions.FirstOrDefault(row =>
                    (row.ExpireTime == 0 || row.ExpireTime > now) && row.UseCharacterList.Contains(cardId))?.Id ?? 0;
            }
            deployed.Add(character);
            Theatre3BattleEffects effects = GetBattleEffects(mutation, checked((int)character.Id), position + 1);
            role.NpcData[position] = new
            {
                Character = character, Equips = equips, WeaponFashionId = weaponFashionId,
                EventIds = GetFightEvents(mutation, checked((int)character.Id), position + 1),
                FightEventsWithLevel = effects.LeveledEvents, BaseAttrs = effects.AttributeDelta,
                Partner = robotId == 0 ? mutation.Session.character.Partners.FirstOrDefault(partner => partner.CharacterId == character.Id) : null,
                IsRobot = robotId != 0, RobotId = robotId, IsNpc = false,
                CharacterCareer = Rows<CharacterTable>().Single(row => row.Id == character.Id).Career,
                MagicIds = new Dictionary<int, int>()
            };
        }
        foreach (dynamic npc in role.NpcData.Values)
            foreach (var magic in FightModule.BuildObservationMagicIds(deployed, (CharacterData)npc.Character))
                npc.MagicIds.Add(magic.Key, magic.Value);
        Require(FightModule.IsValidGeneralSkill(generalSkill, deployed), 20203034);
        if (generalSkill > 0) data.EventIds.Add(FightModule.GeneralSkillFightEventId(generalSkill));
        data.RoleData.Add(role);
        return data;
    }

    private static Theatre3RebootTable CombatRebootProfile(Mutation mutation) =>
        Rows<Theatre3RebootTable>().SingleOrDefault(row => row.Id == Rows<Theatre3DifficultyTable>()
            .Single(difficulty => difficulty.Id == mutation.Data.DifficultyId).RebootId)
        ?? throw new ServerCodeException("Matrix reboot profile is missing.", 20203059);

    internal static bool TryReboot(Session session, FightRebootRequest request, out FightRebootResponse response)
    {
        response = new();
        if (session.player.Theatre3.Fight is null) return false;
        if (session.fight is { } active && !IsCombatStage(active.PreFight.PreFightData.StageId)) return false;
        try
        {
            Commit(session, mutation =>
            {
                Theatre3FightState fight = RequireCombat(mutation, request.FightId);
                if (request.RebootCount > 0 && request.RebootCount == fight.RebootCount) return;
                Require(request.RebootCount == fight.RebootCount + 1, 20003041);
                Theatre3RebootTable profile = CombatRebootProfile(mutation);
                Require(fight.ReviveCostCount < profile.MaxRebootCount, 20203060);
                mutation.Cost(96189, GetEffects(mutation).RebootCostOverride ?? profile.RebootCost);
                fight.RebootCount = request.RebootCount;
                fight.ReviveCostCount++;
                ApplyEffectTrigger(mutation, Theatre3EffectTrigger.Reboot, checked((int)fight.FightId), fight.ReviveCostCount);
            });
        }
        catch (ServerCodeException exception) { response.Code = exception.Code; }
        return true;
    }

    internal static bool TryRestart(Session session, FightRestartRequest request, out FightRestartResponse response, int requestId = 0)
    {
        response = new();
        if (session.player.Theatre3.Fight is null) return false;
        if (session.fight is { } active && !IsCombatStage(active.PreFight.PreFightData.StageId)) return false;
        uint seed = 0;
        try
        {
            Commit(session, mutation =>
            {
                Theatre3FightState fight = RequireCombat(mutation, request.FightId);
                if (fight.RestartReceipts.TryGetValue(requestId, out long storedSeed))
                {
                    seed = checked((uint)storedSeed);
                    return;
                }
                Require(Convert.ToInt32(Rows<StageTable>().Single(row => row.StageId == fight.StageId).Restartable) != 0, 20203030);
                Theatre3RebootTable profile = CombatRebootProfile(mutation);
                // Documented local rule: restart and revive share the profile's attempt ceiling;
                // restart uses its own authoritative cost, never the descriptive revival price.
                Require(fight.ReviveCostCount < profile.MaxRebootCount, 20203060);
                mutation.Cost(96189, profile.FubenRestartCost);
                fight.ReviveCostCount++;
                fight.RestartCount++;
                fight.RebootCount = 0;
                fight.Seed = seed = unchecked((uint)RandomNumberGenerator.GetInt32(int.MinValue, int.MaxValue));
                fight.StartedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                fight.RestartReceipts[requestId] = seed;
                PreFightResponse.PreFightResponseFightData data = MessagePackSerializer.Deserialize<PreFightResponse.PreFightResponseFightData>(fight.PreFightPayload!);
                data.Seed = seed;
                fight.PreFightPayload = MessagePackSerializer.Serialize(data);
                ApplyEffectTrigger(mutation, Theatre3EffectTrigger.Reboot, checked((int)fight.FightId), fight.ReviveCostCount);
            });
            response.Seed = unchecked((int)seed);
        }
        catch (ServerCodeException exception) { response.Code = exception.Code; }
        return true;
    }

    internal static bool TryLeaveFight(Session session, out LeaveFightResponse response)
    {
        response = new();
        if (session.player.Theatre3.Fight is null) return false;
        if (session.fight is { } active && !IsCombatStage(active.PreFight.PreFightData.StageId)) return false;
        try
        {
            Commit(session, mutation =>
            {
                Theatre3FightState fight = RequireCombat(mutation, mutation.State.Fight!.FightId);
                FightSettleResponse result = new() { Settle = new() { StageId = fight.StageId, IsWin = false, ChallengeCount = 1 } };
                mutation.State.Fight = null;
                OnFightResult(mutation, false);
                RememberCombatResult(mutation, fight, result);
            });
            session.fight = null;
        }
        catch (ServerCodeException exception) { response.Code = exception.Code; }
        return true;
    }

    internal static bool TrySettleFight(Session session, FightSettleResult result, out FightSettleResponse response)
    {
        response = new();
        if (!IsCombatStage(result.StageId)) return false;
        FightSettleResponse settled = new();
        try
        {
            ReplayPending(session);
            if (session.player.Theatre3.LastFightId == result.FightId && session.player.Theatre3.LastFightStageId == result.StageId
                && session.player.Theatre3.LastFightSettle is { } receipt)
            {
                response = MessagePackSerializer.Deserialize<FightSettleResponse>(receipt);
                return true;
            }
            Commit(session, mutation =>
            {
                Theatre3FightState fight = RequireCombat(mutation, result.FightId);
                Require(fight.StageId == result.StageId, 20203032);
                Require(fight.RebootCount == result.RebootCount, 20003041);
                ValidateCombatResult(mutation, result);
                bool won = result.IsWin && !result.IsForceExit;
                settled.Settle = new()
                {
                    StageId = result.StageId, IsWin = won, ChallengeCount = 1,
                    LeftTime = checked((int)result.LeftTime), NpcHpInfo = result.NpcHpInfo
                };
                if (won)
                {
                    CurrentSlot(mutation).PassedStageIds.Add(checked((int)result.StageId));
                    AddQuantumFightRewards(mutation, fight);
                    mutation.Data.FightRecords.Add(new()
                    {
                        StageId = checked((int)result.StageId),
                        DamageSourceDic = result.DamageSourceDic.ToDictionary(pair => pair.Key,
                            pair => new Theatre3DamageSource { DamageSource = new(pair.Value) }),
                        StringToListIntRecord = result.StringToListIntRecord.ToDictionary(pair => pair.Key, pair => pair.Value.ToList())
                    });
                }
                mutation.State.Fight = null;
                if (won && result.StringToIntRecord?.GetValueOrDefault("Theater3ExtraWaveWin") == 1)
                    foreach (int waveId in fight.ExtraWaveIds)
                    {
                        int boxId = Rows<Theatre3FightExtraWaveTable>().Single(row => row.Id == waveId).ItemBoxId;
                        if (boxId > 0) CurrentSlot(mutation).NodeRewards.Add(new()
                        {
                            Uid = AllocateUid(mutation), RewardType = 1, ConfigId = boxId, Count = 1, IsShow = 1
                        });
                    }
                OnFightResult(mutation, won);
                RememberCombatResult(mutation, fight, settled);
            });
            if (session.fight?.FightId == result.FightId) session.fight = null;
        }
        catch (ServerCodeException exception) { settled = new() { Code = exception.Code }; }
        response = settled;
        return true;
    }

    private static void AddQuantumFightRewards(Mutation mutation, Theatre3FightState fight)
    {
        if (fight.QuantumTier < 0) return;
        Theatre3FightStageTemplateTable template = Rows<Theatre3FightStageTemplateTable>().Single(row => row.Id == fight.FightTemplateId);
        Theatre3NodeSlot slot = CurrentSlot(mutation);
        // Only the selected tier's authored loot applies; lower tiers are not cumulative and thresholds are not currency grants.
        int equipGroup = template.QubitEquipBoxGroup.ElementAtOrDefault(fight.QuantumTier);
        if (equipGroup > 0)
            slot.NodeRewards.Add(NewReward(mutation, 3, Weighted(Rows<Theatre3EquipBoxGroupTable>()
                .Where(row => row.GroupId == equipGroup), row => row.Weight ?? 0).EquipBoxId));
        int itemGroup = template.QubitItemBoxGroup.ElementAtOrDefault(fight.QuantumTier);
        if (itemGroup > 0)
            slot.NodeRewards.Add(NewReward(mutation, 1, Weighted(Rows<Theatre3ItemBoxTable>()
                .Where(row => row.BoxGroupId == itemGroup), row => row.Weight).Id));
        int goldGroup = template.QubitGoldGroup.ElementAtOrDefault(fight.QuantumTier);
        if (goldGroup > 0) slot.NodeRewards.Add(NewReward(mutation, 2, SelectGold(mutation, goldGroup)));
    }

    private static void RememberCombatResult(Mutation mutation, Theatre3FightState fight, FightSettleResponse response)
    {
        mutation.State.LastFightId = fight.FightId;
        mutation.State.LastFightStageId = fight.StageId;
        mutation.State.LastFightSettle = MessagePackSerializer.Serialize(response);
    }

    private static void ValidateCombatResult(Mutation mutation, FightSettleResult result)
    {
        Require(result.StartFrame >= 0 && result.SettleFrame >= result.StartFrame && result.PauseFrame >= 0
            && result.ExSkillPauseFrame >= 0 && result.LeftTime is >= 0 and <= int.MaxValue
            && result.TotalDamage >= 0 && result.TotalDamaged >= 0 && result.TotalCure >= 0, 1033);
        long frames = result.SettleFrame - result.StartFrame;
        Require(result.PauseFrame <= frames && result.ExSkillPauseFrame <= frames - result.PauseFrame, 1033);
        // Native XFightConfig.FPS is 20; one second covers the persisted whole-second clock boundary.
        long activeFrames = frames - result.PauseFrame - result.ExSkillPauseFrame;
        Require(activeFrames / 20 <= Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - mutation.State.Fight!.StartedAt) + 1, 1033);
        StageTable stage = Rows<StageTable>().Single(row => row.StageId == result.StageId);
        Require(stage.PassTimeLimit is not > 0 || result.LeftTime <= stage.PassTimeLimit, 1033);
        Require(result.PlayerIds is null || result.PlayerIds.All(id => id == mutation.Session.player.PlayerData.Id), 1033);
        Theatre3TeamData team = mutation.Data.CurTeamData!;
        HashSet<int> characters = team.CardIds.Where(id => id > 0).ToHashSet();
        foreach (int robotId in team.RobotIds.Where(id => id > 0)) characters.Add(Rows<RobotTable>().Single(row => row.Id == robotId).CharacterId);
        // Sparse native DPS reports need not list every deployed role; only reported player ownership is checked.
        foreach (NpcDpsTable npc in result.NpcDpsTable?.Values ?? Enumerable.Empty<NpcDpsTable>())
        {
            Require(npc.CharacterId == 0 || characters.Contains(npc.CharacterId), 1033);
            Require(npc.CharacterId == 0 || npc.RoleId == 0 || npc.RoleId == mutation.Session.player.PlayerData.Id, 1033);
            Require(npc.DamageTotal >= 0 && npc.DamageNormal >= 0 && npc.Cure >= 0 && npc.Hurt >= 0, 1033);
        }
        if (result.DamageSourceDic is null || result.StringToListIntRecord is null)
            throw new ServerCodeException("Matrix damage history is malformed.", 1033);
        foreach (var damage in result.DamageSourceDic)
            Require(damage.Value is not null && damage.Value.Values.All(value => value >= 0), 1033);
        foreach (var record in result.StringToListIntRecord.Values)
            Require(record is not null && (record.Count == 0 || characters.Contains(record[0] / 10)), 1033);
    }
}
