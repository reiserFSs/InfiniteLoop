using System.Globalization;
using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Table.V2.share.biancatheatre;
using AscNet.Table.V2.share.character;
using AscNet.Table.V2.share.fuben;
using AscNet.Table.V2.share.robot;
using MessagePack;
using Newtonsoft.Json;

namespace AscNet.GameServer.Handlers;

internal static partial class BiancaTheatreModule
{
    private const int CombatAuthorizationError = 1033;

    internal static bool IsCombatStage(uint stageId) =>
        Rows<BiancaTheatreFightStageTemplateTable>().Any(row => row.StageId == stageId);

    private static BiancaTheatreFightState RequireCombat(Mutation mutation, long fightId = 0)
    {
        BiancaTheatreFightState fight = mutation.State.Fight
            ?? throw new ServerCodeException("No pending theatre battle.", CombatAuthorizationError);
        if (mutation.Data.CurChapterDb is null || mutation.Data.CurChapterId <= 0
            || (fightId != 0 && fight.FightId != unchecked((uint)fightId)))
            throw new ServerCodeException("Theatre battle does not match the active run.", CombatAuthorizationError);
        BiancaTheatreNodeSlot slot = CombatSlot(mutation, fight);
        if (slot.Selected != 1 || slot.FightTemplateId != fight.FightTemplateId)
            throw new ServerCodeException("Theatre battle is not the selected encounter.", CombatAuthorizationError);
        return fight;
    }

    private static BiancaTheatreNodeSlot CombatSlot(Mutation mutation, BiancaTheatreFightState fight) =>
        mutation.Data.CurChapterDb?.Steps.Select(step => step.NodeData)
            .Where(node => node?.NodeId == fight.NodeId)
            .SelectMany(node => node!.Slots)
            .SingleOrDefault(slot => slot.SlotId == fight.SlotId)
        ?? throw new ServerCodeException("Theatre encounter is missing.", CombatAuthorizationError);

    internal static bool TryPreFight(Session session, PreFightRequest request, out PreFightResponse response)
    {
        response = new();
        if (!IsCombatStage(request.PreFightData.StageId))
            return false;
        PreFightResponse result = new();
        try
        {
            ReplayPending(session);
            Commit(session, mutation =>
            {
                BiancaTheatreFightState fight = RequireCombat(mutation);
                BiancaTheatreFightStageTemplateTable template = Rows<BiancaTheatreFightStageTemplateTable>()
                    .Single(row => row.Id == fight.FightTemplateId);
                if (template.StageId != request.PreFightData.StageId
                    || request.PreFightData.ChallengeCount != 1 || request.PreFightData.IsHasAssist
                    || request.PreFightData.SpeedrunStageId != 0 || request.PreFightData.SelectAreaId != 0)
                    throw new ServerCodeException("Invalid theatre battle request.", CombatAuthorizationError);
                if (fight.StageId != 0 && fight.StageId != request.PreFightData.StageId)
                    throw new ServerCodeException("Theatre stage does not match the pending encounter.", CombatAuthorizationError);
                StageTable stage = Rows<StageTable>().Single(row => row.StageId == template.StageId);
                BiancaTheatreTeamData team = mutation.Data.SingleTeamData
                    ?? throw new ServerCodeException("Theatre deployment is missing.", 20004003);
                ValidateCombatTeam(mutation, request.PreFightData, team);
                if (fight.FightId == 0)
                {
                    fight.FightId = Random.Shared.NextInt64(1, int.MaxValue);
                    fight.Seed = (uint)Random.Shared.NextInt64(0, (long)uint.MaxValue + 1);
                    fight.StageId = request.PreFightData.StageId;
                }
                result.FightData = BuildCombatData(mutation, team, template, stage);
            });
            session.fight = new(request, result.FightData.FightId);
        }
        catch (ServerCodeException exception)
        {
            result = new() { Code = exception.Code };
        }
        response = result;
        return true;
    }

    private static void ValidateCombatTeam(Mutation mutation, PreFightRequest.PreFightRequestPreFightData request,
        BiancaTheatreTeamData team)
    {
        if (team.CardIds.Count != 3 || team.RobotIds.Count != 3
            || request.CardIds is null || request.RobotIds is null
            || !request.CardIds.SequenceEqual(team.CardIds.Select(id => checked((uint)id)))
            || !request.RobotIds.SequenceEqual(team.RobotIds)
            || request.CaptainPos != team.CaptainPos || request.FirstFightPos != team.FirstFightPos
            || request.EnterCgIndex != team.EnterCgIndex || request.SettleCgIndex != team.SettleCgIndex
            || team.CaptainPos is < 1 or > 3 || team.FirstFightPos is < 1 or > 3)
            throw new ServerCodeException("Theatre deployment does not match the saved team.", 20004003);
        HashSet<int> deployed = [];
        for (int position = 0; position < 3; position++)
        {
            int cardId = team.CardIds[position];
            int robotId = team.RobotIds[position];
            if (cardId < 0 || robotId < 0 || (cardId != 0 && robotId != 0))
                throw new ServerCodeException("Invalid theatre deployment slot.", 20004003);
            if (cardId == 0 && robotId == 0)
                continue;
            int characterId = cardId;
            if (robotId != 0)
                characterId = Rows<RobotTable>().SingleOrDefault(row => row.Id == robotId)?.CharacterId
                    ?? throw new ServerCodeException("Invalid theatre robot.", 20009021);
            BiancaTheatreCharacter recruited = mutation.Data.Characters.SingleOrDefault(row => row.CharacterId == characterId)
                ?? throw new ServerCodeException("Character was not recruited in this run.", 20009021);
            if (!deployed.Add(characterId))
                throw new ServerCodeException("Duplicate theatre character.", 20004003);
            if (robotId != 0 && !Rows<BiancaTheatreCharacterLevelTable>().Any(row =>
                row.CharacterId == characterId && row.Level == recruited.Level
                && row.Type == (recruited.IsDecay != 0 ? 2 : 1) && row.RobotId == robotId))
                throw new ServerCodeException("Robot does not match recruited character level.", 20009021);
            if (cardId != 0 && !mutation.Session.character.Characters.Any(row => row.Id == cardId))
                throw new ServerCodeException("Local theatre character is not owned.", 20009021);
        }
        if (deployed.Count == 0 || (mutation.Data.TeamCountEffect > 0 && deployed.Count > mutation.Data.TeamCountEffect)
            || (team.CardIds[team.CaptainPos - 1] == 0 && team.RobotIds[team.CaptainPos - 1] == 0)
            || (team.CardIds[team.FirstFightPos - 1] == 0 && team.RobotIds[team.FirstFightPos - 1] == 0))
            throw new ServerCodeException("Theatre team has no captain or first fighter.", 20004003);
    }

    private static PreFightResponse.PreFightResponseFightData BuildCombatData(Mutation mutation,
        BiancaTheatreTeamData team, BiancaTheatreFightStageTemplateTable template, StageTable stage)
    {
        BiancaTheatreFightState fight = mutation.State.Fight!;
        long vision = mutation.Balance(VisionItem);
        List<BiancaTheatreVisionFactorLevelTable> visionBands = Rows<BiancaTheatreVisionFactorLevelTable>().OrderBy(row => row.Id).ToList();
        int band = visionBands.FindIndex(row => vision >= (row.Min ?? 0) && vision <= row.Max);
        BiancaTheatreNodeTable node = Rows<BiancaTheatreNodeTable>().Single(row => row.Id == fight.NodeId);
        if (band < 0 || band >= node.FightFactor.Count)
            throw new InvalidOperationException("Theatre node has no factor for the current vision band.");
        int visionStage = mutation.Data.IsOpenVision != 0
            ? Rows<BiancaTheatreVisionTable>().Single(row => vision >= (row.Min ?? 0) && vision <= row.Max).Id : 0;
        // Private-server policy: ordered vision bands select the node's seven combat factors.
        List<int> comboEvents = CombatComboEvents(mutation, team);
        PreFightResponse.PreFightResponseFightData data = new()
        {
            FightId = checked((uint)fight.FightId), Seed = fight.Seed, StageId = checked((uint)template.StageId),
            RebootId = stage.RebootId ?? 0, PassTimeLimit = template.TimeLimit,
            NormalEventIds = stage.NormalEventId.Select(id => (dynamic)id).ToList(),
            EventIds = template.FightEventIds.Select(id => (dynamic)id).ToList(),
            Restartable = Convert.ToInt32(stage.Restartable) != 0,
            StageParams = new Dictionary<string, string>
            {
                ["Mode"] = template.Mode.ToString(CultureInfo.InvariantCulture),
                ["ModeParams"] = JsonConvert.SerializeObject(template.ModeParams),
                ["GoldCount"] = mutation.Balance(InnerCoin).ToString(CultureInfo.InvariantCulture),
                ["VisionCount"] = vision.ToString(CultureInfo.InvariantCulture),
                ["VisionStageId"] = visionStage.ToString(CultureInfo.InvariantCulture),
                ["FightFactor"] = node.FightFactor[band].ToString(CultureInfo.InvariantCulture),
                ["PassFightCount"] = mutation.Data.CurChapterDb!.PassFightCount.ToString(CultureInfo.InvariantCulture)
            }
        };
        List<BiancaTheatreFightEventLevel> leveledEvents = [];
        foreach (BiancaTheatreEffectGroupTable effect in EffectGroups(mutation, includeCombos: false))
        {
            if (effect.SystemEvents.Any(id => Rows<BiancaTheatreSystemEffectTable>().Single(row => row.Id == id).Type == 9))
            {
                // Private-server pass-value policy: the source damage event receives owned artifact count as its level.
                leveledEvents.AddRange(effect.FightEvents.Select(id => new BiancaTheatreFightEventLevel
                {
                    FightEventId = id, FightEventLevel = mutation.Data.Items.Count
                }));
            }
            else
                data.EventIds.AddRange(effect.FightEvents.Select(id => (dynamic)id));
        }
        data.FightEventsWithLevel = leveledEvents;
        data.NpcGroupList = template.MonsterGroupId.Select(groupId => new
        {
            NpcList = Rows<BiancaTheatreMonsterGroupTable>().Single(group => group.Id == groupId).MonsterIds
                .Select(monsterId =>
                {
                    BiancaTheatreMonsterTable monster = Rows<BiancaTheatreMonsterTable>().Single(row => row.Id == monsterId);
                    if (monster.BuffIds.Count != monster.BuffLevel.Count)
                        throw new InvalidOperationException($"Bianca monster {monster.Id} has unpaired buff levels.");
                    return new
                    {
                        monster.NpcId, monster.Level, BufferIds = Array.Empty<int>(),
                        MagicInfos = monster.BuffIds.Select((id, index) => new { MagicId = id, Level = monster.BuffLevel[index] }).ToList(),
                        AttrTable = new Dictionary<int, int>()
                    };
                }).ToList()
        }).ToList();
        PreFightResponse.PreFightResponseFightData.PreFightResponseFightDataRoleData role = new()
        {
            Id = checked((uint)mutation.Session.player.PlayerData.Id), Camp = 1,
            Name = mutation.Session.player.PlayerData.Name, CaptainIndex = team.CaptainPos - 1,
            FirstFightPos = team.FirstFightPos - 1, EnterCgIndex = team.EnterCgIndex - 1,
            SettleCgIndex = team.SettleCgIndex - 1, NpcData = []
        };
        for (int position = 0; position < 3; position++)
        {
            int cardId = team.CardIds[position];
            int robotId = team.RobotIds[position];
            if (cardId == 0 && robotId == 0)
                continue;
            CharacterData character;
            IReadOnlyList<EquipData> equips;
            int weaponFashionId;
            if (robotId != 0)
            {
                RobotTable robot = Rows<RobotTable>().Single(row => row.Id == robotId);
                var deployment = FightModule.BuildRobotDeployment(robot);
                character = deployment.Character;
                equips = deployment.Equips;
                weaponFashionId = robot.WeaponFashion ?? 0;
            }
            else
            {
                character = mutation.Session.character.Characters.Single(row => row.Id == cardId);
                equips = FightModule.BuildTeamPrefabFightEquips(mutation.Session, checked((uint)cardId));
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                weaponFashionId = mutation.Session.character.WeaponFashions.FirstOrDefault(row =>
                    (row.ExpireTime == 0 || row.ExpireTime > now) && row.UseCharacterList.Contains(cardId))?.Id ?? 0;
            }
            BiancaTheatreCharacter recruited = mutation.Data.Characters.Single(row => row.CharacterId == character.Id);
            BiancaTheatreCharacterLevelTable level = Rows<BiancaTheatreCharacterLevelTable>()
                .Single(row => row.CharacterId == character.Id && row.Level == recruited.Level
                    && row.Type == (recruited.IsDecay != 0 ? 2 : 1));
            role.NpcData[position] = new
            {
                Character = character, Equips = equips, WeaponFashionId = weaponFashionId,
                EventIds = level.FightEventIds.Concat(comboEvents).ToList(),
                Partner = robotId == 0 ? mutation.Session.character.Partners.FirstOrDefault(partner => partner.CharacterId == character.Id) : null,
                IsRobot = robotId != 0, RobotId = robotId, IsNpc = false,
                CharacterCareer = Rows<CharacterTable>().Single(row => row.Id == character.Id).Career,
                MagicIds = new Dictionary<int, int>()
            };
        }
        var deployed = role.NpcData.Values.Select(npc => (CharacterData)npc.Character).ToArray();
        foreach (dynamic npc in role.NpcData.Values)
            foreach (var magic in FightModule.BuildObservationMagicIds(deployed, (CharacterData)npc.Character))
                npc.MagicIds.Add(magic.Key, magic.Value);
        data.RoleData.Add(role);
        return data;
    }

    private static List<int> CombatComboEvents(Mutation mutation, BiancaTheatreTeamData team)
    {
        List<CharacterTable> recruited = mutation.Data.Characters
            .Select(role => Rows<CharacterTable>().Single(row => row.Id == role.CharacterId)).ToList();
        HashSet<int> deployed = team.CardIds.Where(id => id > 0).ToHashSet();
        foreach (int robotId in team.RobotIds.Where(id => id > 0))
            deployed.Add(Rows<RobotTable>().Single(row => row.Id == robotId).CharacterId);
        List<int> events = [];
        foreach (BiancaTheatreComboTable combo in ActiveCombos(mutation))
        {
            if (combo.EffectId.Count != combo.EffectValid.Count || combo.EffectId.Count != combo.EffectTarget.Count)
                throw new InvalidOperationException($"Bianca combo {combo.Id} has unpaired effect qualifiers.");
            for (int index = 0; index < combo.EffectId.Count; index++)
            {
                BiancaTheatreComboValidTable valid = Rows<BiancaTheatreComboValidTable>().Single(row => row.Id == combo.EffectValid[index]);
                if (valid.TriggerType != 1)
                    throw new InvalidOperationException($"Bianca combo validator {valid.Id} is not a validity predicate.");
                // Private-server predicates retain source category values and evaluate the selected roster.
                bool applies = valid.ComboType switch
                {
                    1 => true,
                    4 when valid.Params.Count == 2 => deployed.Count(id => Rows<BiancaTheatreBaseCharacterTable>()
                        .Single(row => row.CharacterId == id).ReferenceComboId.Contains(valid.Params[1])) >= valid.Params[0],
                    6 when valid.Params.Count == 1 => recruited.Any(row => row.Element == valid.Params[0]),
                    7 when valid.Params.Count == 1 => recruited.Any(row => row.Career == valid.Params[0]),
                    _ => throw new InvalidOperationException($"Unsupported Bianca combo validator {valid.Id}.")
                };
                if (!applies)
                    continue;
                BiancaTheatreComboValidTable target = Rows<BiancaTheatreComboValidTable>().Single(row => row.Id == combo.EffectTarget[index]);
                if (target.TriggerType != 2)
                    throw new InvalidOperationException($"Bianca combo target {target.Id} is not a target predicate.");
                int copies = target.ComboType switch
                {
                    1 => 1,
                    4 when target.Params.Count == 0 => recruited.Select(row => row.EquipType).Distinct().Count(),
                    _ => throw new InvalidOperationException($"Unsupported Bianca combo target {target.Id}.")
                };
                BiancaTheatreEffectGroupTable effect = Rows<BiancaTheatreEffectGroupTable>().Single(row => row.Id == combo.EffectId[index]);
                // Weapon-diversity policy repeats the effect per distinct category; never deduplicate events.
                for (int copy = 0; copy < copies; copy++)
                    events.AddRange(effect.FightEvents);
            }
        }
        return events;
    }

    private static FightRebootTable CombatRebootProfile(BiancaTheatreFightState fight)
    {
        StageTable stage = Rows<StageTable>().SingleOrDefault(row => row.StageId == fight.StageId)
            ?? throw new ServerCodeException("Reboot stage not found.", 20003042);
        return Rows<FightRebootTable>().SingleOrDefault(row => row.Id == stage.RebootId)
            ?? throw new ServerCodeException("Reboot configuration not found.", 20003043);
    }

    internal static bool TryReboot(Session session, FightRebootRequest request, out FightRebootResponse response)
    {
        response = new();
        if (session.fight is { } active && active.FightId == unchecked((uint)request.FightId)
            && !IsCombatStage(active.PreFight.PreFightData.StageId))
            return false;
        if (session.player.BiancaTheatre.Fight is not { } pending || !IsCombatStage(pending.StageId))
            return false;
        try
        {
            ReplayPending(session);
            Commit(session, mutation =>
            {
                if (request.FightId == 0 || mutation.State.Fight?.FightId != unchecked((uint)request.FightId))
                    throw new ServerCodeException("Reboot fight does not match.", 20003040);
                BiancaTheatreFightState fight = RequireCombat(mutation, request.FightId);
                if (request.RebootCount == fight.RebootCount && request.RebootCount > 0)
                    return;
                if (request.RebootCount != fight.RebootCount + 1)
                    throw new ServerCodeException("Reboot count does not match.", 20003041);
                FightRebootTable profile = CombatRebootProfile(fight);
                if (fight.ReviveCostCount >= profile.MaxRebootCount || request.RebootCount <= 0)
                    throw new ServerCodeException("Invalid reboot count.", 20003044);
                if (profile.ConsumeCount.Count == 0)
                    throw new ServerCodeException("Reboot cost is missing.", 20003043);
                int cost = profile.ConsumeCount[Math.Min(fight.ReviveCostCount, profile.ConsumeCount.Count - 1)];
                mutation.AddCost(profile.RebootItemId, cost);
                fight.RebootCount = request.RebootCount;
                fight.ReviveCostCount = checked(fight.ReviveCostCount + 1);
                mutation.State.RunReviveCount = checked(mutation.State.RunReviveCount + 1);
            });
        }
        catch (ServerCodeException exception) { response.Code = exception.Code; }
        return true;
    }

    internal static bool TryRestart(Session session, FightRestartRequest request, out FightRestartResponse response)
    {
        response = new();
        if (session.fight is { } active && active.FightId == unchecked((uint)request.FightId)
            && !IsCombatStage(active.PreFight.PreFightData.StageId))
            return false;
        if (session.player.BiancaTheatre.Fight is not { } pending || !IsCombatStage(pending.StageId))
            return false;
        uint seed = 0;
        try
        {
            BiancaTheatreFightState? recovering = session.player.BiancaTheatre.PendingMutation?.Outcome.Fight;
            bool replayRestart = recovering?.FightId == unchecked((uint)request.FightId)
                && recovering.RestartCount > pending.RestartCount;
            ReplayPending(session);
            if (replayRestart)
            {
                response.Seed = unchecked((int)session.player.BiancaTheatre.Fight!.Seed);
                return true;
            }
            Commit(session, mutation =>
            {
                if (request.FightId == 0 || mutation.State.Fight?.FightId != unchecked((uint)request.FightId))
                    throw new ServerCodeException("Restart fight does not match.", 20003040);
                BiancaTheatreFightState fight = RequireCombat(mutation, request.FightId);
                if (Convert.ToInt32(Rows<StageTable>().Single(row => row.StageId == fight.StageId).Restartable) == 0)
                    throw new ServerCodeException("This theatre stage cannot restart.", CombatAuthorizationError);
                FightRebootTable profile = CombatRebootProfile(fight);
                // Private-server policy: restart and resurrection consume the same attempt allowance.
                if (fight.ReviveCostCount >= profile.MaxRebootCount || profile.ConsumeCount.Count == 0)
                    throw new ServerCodeException("Invalid restart count.", 20003044);
                int cost = profile.ConsumeCount[Math.Min(fight.ReviveCostCount, profile.ConsumeCount.Count - 1)];
                mutation.AddCost(profile.RebootItemId, cost);
                fight.RestartCount = checked(fight.RestartCount + 1);
                fight.ReviveCostCount = checked(fight.ReviveCostCount + 1);
                fight.RebootCount = 0;
                mutation.State.RunReviveCount = checked(mutation.State.RunReviveCount + 1);
                fight.Seed = seed = (uint)Random.Shared.NextInt64(0, (long)uint.MaxValue + 1);
            });
            response.Seed = unchecked((int)seed);
        }
        catch (ServerCodeException exception) { response.Code = exception.Code; }
        return true;
    }

    internal static void RecoverCombatOnLogin(Session session)
    {
        if (session.player.BiancaTheatre.Fight is not { FightId: > 0 })
            return;
        Mutation mutation = new(session);
        RecoverCombatAttempt(mutation, forLogin: true);
        RecordCoreProgress(mutation);
        Persist(mutation, string.Empty, string.Empty, null);
    }

    internal static bool TryLeaveFight(Session session, out LeaveFightResponse response)
    {
        response = new();
        if (session.fight is { } active && !IsCombatStage(active.PreFight.PreFightData.StageId))
            return false;
        if (session.player.BiancaTheatre.Fight is not { } pending || !IsCombatStage(pending.StageId))
            return false;
        try
        {
            ReplayPending(session);
            if (session.player.BiancaTheatre.Fight is { FightId: > 0 })
                Commit(session, mutation => RecoverCombatAttempt(mutation, forLogin: false));
            session.fight = null;
        }
        catch (ServerCodeException exception) { response.Code = exception.Code; }
        return true;
    }

    private static void RecoverCombatAttempt(Mutation mutation, bool forLogin)
    {
        BiancaTheatreFightState fight = RequireCombat(mutation);
        int Config(string key) => Rows<BiancaTheatreConfigTable>().Single(row => row.Key == key).Value;
        // Private-server policy: login and explicit leave consume the same per-run close tolerance.
        if (Config("OpenCloseCheck") != 0)
        {
            mutation.State.AbnormalExitCount = checked(mutation.State.AbnormalExitCount + 1);
            if (forLogin)
                mutation.State.PendingAbnormalExitNotification = true;
            else
                mutation.Push(new NotifyBiancaTheatreAbnormalExit());
            if (mutation.State.AbnormalExitCount > Config("CloseCheckCount"))
            {
                int cost = Config("CloseCheckCost");
                if (mutation.Balance(ActionPoint) < cost)
                {
                    BiancaTheatreSettleData settled = SettleRun(mutation, true);
                    if (forLogin)
                        mutation.State.PendingSettleNotification = true;
                    else
                        mutation.Push(new NotifyBiancaTheatreAdventureSettle { SettleData = settled });
                }
                else
                    mutation.AddCost(ActionPoint, cost);
            }
        }
        if (mutation.State.Fight is not null)
        {
            fight.FightId = 0;
            fight.Seed = 0;
            fight.StageIndex = 0;
            fight.RebootCount = 0;
            fight.RestartCount = 0;
            fight.ReviveCostCount = 0;
        }
    }

    internal static bool TrySettleFight(Session session, FightSettleResult result, out FightSettleResponse response)
    {
        response = new();
        if (!IsCombatStage(result.StageId))
            return false;
        if (result.FightId < int.MinValue || result.FightId > uint.MaxValue)
        {
            response.Code = CombatAuthorizationError;
            return true;
        }
        try { ReplayPending(session); }
        catch (ServerCodeException exception)
        {
            response.Code = exception.Code;
            return true;
        }
        BiancaTheatreState state = session.player.BiancaTheatre;
        if (state.LastFightId == unchecked((uint)result.FightId) && state.LastFightStageId == result.StageId
            && state.LastFightSettle is not null)
        {
            response = MessagePackSerializer.Deserialize<FightSettleResponse>(state.LastFightSettle);
            return true;
        }
        FightSettleResponse settled = new();
        try
        {
            Commit(session, mutation =>
            {
                BiancaTheatreFightState fight = RequireCombat(mutation, result.FightId);
                if (fight.StageId != result.StageId || fight.FightId == 0)
                    throw new ServerCodeException("Theatre settlement does not match battle.", CombatAuthorizationError);
                if (result.RebootCount != fight.RebootCount)
                    throw new ServerCodeException("Settlement resurrection count does not match.", 20003041);
                bool won = result.IsWin && !result.IsForceExit;
                settled.Settle = new()
                {
                    IsWin = won, StageId = result.StageId, LeftTime = checked((int)result.LeftTime),
                    NpcHpInfo = result.NpcHpInfo, ChallengeCount = 1
                };
                BiancaTheatreNodeSlot slot = CombatSlot(mutation, fight);
                if (won)
                {
                    int goodsStart = mutation.Goods.Count;
                    int guaranteedGold = SystemEffects(mutation).Where(effect => effect.Type == 3)
                        .Sum(effect => checked((int)effect.Params[0]));
                    NodeGoods(mutation, NodeCoin, guaranteedGold);
                    slot.PassedStageIds.Add(checked((int)result.StageId));
                    mutation.State.PassedStageIds.Add(checked((int)result.StageId));
                    if (fight.EventId != 0)
                        CompleteEventFight(mutation);
                    else
                    {
                        mutation.State.Fight = null;
                        AppendFightRewards(mutation, slot);
                    }
                    settled.Settle.RewardGoodsList = NodeGoodsResponse(mutation, goodsStart);
                }
                else
                {
                    mutation.Push(new NotifyBiancaTheatreAdventureSettle { SettleData = SettleRun(mutation, true) });
                }
                mutation.State.LastFightId = unchecked((uint)result.FightId);
                mutation.State.LastFightStageId = result.StageId;
                mutation.State.LastFightSettle = MessagePackSerializer.Serialize(settled);
            });
        }
        catch (ServerCodeException exception) { settled = new() { Code = exception.Code }; }
        response = settled;
        return true;
    }
}


[MessagePackObject(true)]
public sealed class NotifyBiancaTheatreAbnormalExit { }

[MessagePackObject(true)]
public sealed class BiancaTheatreFightEventLevel
{
    public int FightEventId { get; set; }
    public int FightEventLevel { get; set; }
}
