using System.Security.Cryptography;
using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Table.V2.share.character;
using AscNet.Table.V2.share.fuben;
using AscNet.Table.V2.share.robot;
using AscNet.Table.V2.share.theatre;
using MessagePack;
using MongoDB.Driver;

namespace AscNet.GameServer.Handlers;

internal static partial class TheatreModule
{
    internal static bool IsCombatStage(uint stageId) => Rows<StageTable>().Any(row => row.StageId == stageId && row.Type == 56);

    private static bool OwnsCombat(Session session) => session.player.Theatre.Fight != null
        || session.fight is { } active && IsCombatStage(active.PreFight.PreFightData.StageId);

    private static TheatreFightState RequireCombat(Mutation mutation, long fightId)
    {
        TheatreFightState fight = mutation.State.Fight
            ?? throw new ServerCodeException("No Theatre battle is pending.", 20155021);
        Require(fight.FightId == fightId && fight.RunId == mutation.State.RunId, 20003040);
        TheatreSlot slot = CurrentSlot(mutation);
        Require(slot.Selected == 1 && slot.SlotId == fight.SlotId
            && mutation.Data.CurChapterDb!.CurNodeDb.NodeId == fight.NodeId
            && fight.StageIndex > 0 && fight.StageIndex <= slot.StageIds.Count
            && slot.StageIds[fight.StageIndex - 1] == fight.StageId
            && !slot.PassedStageIndexs.Contains(fight.StageIndex), 20155021);
        return fight;
    }

    internal static bool TryPreFight(Session session, PreFightRequest request, out PreFightResponse response)
    {
        response = new();
        if (!IsCombatStage(request.PreFightData.StageId)) return false;
        try
        {
            Mutation committed = Commit(session, mutation =>
            {
                Require(!HasPendingSkillChoice(mutation) && !mutation.State.NodeCompletionPending, 20155023);
                TheatreSlot slot = CurrentSlot(mutation);
                bool multi = slot.StageIds.Count > 1;
                int index = multi ? request.PreFightData.TeamIndex : 1;
                Require(multi || request.PreFightData.TeamIndex == 0, 20155037);
                Require(index > 0 && index <= slot.StageIds.Count
                    && slot.StageIds[index - 1] == request.PreFightData.StageId
                    && !slot.PassedStageIndexs.Contains(index), 20155044);
                Require(request.PreFightData.ChallengeCount == 1 && !request.PreFightData.IsHasAssist
                    && request.PreFightData.SpeedrunStageId == 0 && request.PreFightData.SelectAreaId == 0, 20155040);
                TheatreTeamData team = slot.StageIds.Count > 1
                    ? mutation.Data.MultiTeamDatas.SingleOrDefault(team => team.TeamIndex == index)
                        ?? throw new ServerCodeException("Theatre multi-team is not saved.", 20155038)
                    : mutation.Data.SingleTeamData ?? throw new ServerCodeException("Theatre team is not saved.", 20155040);
                ValidateTeam(mutation, team, multi ? index : 0);
                Require(request.PreFightData.CardIds != null && request.PreFightData.RobotIds != null
                    && request.PreFightData.CardIds.SequenceEqual(team.CardIds.Select(id => checked((uint)id)))
                    && request.PreFightData.RobotIds.SequenceEqual(team.RobotIds)
                    && request.PreFightData.CaptainPos == team.CaptainPos
                    && request.PreFightData.FirstFightPos == team.FirstFightPos
                    && (slot.StageIds.Count > 1 || request.PreFightData.EnterCgIndex == team.EnterCgIndex
                        && request.PreFightData.SettleCgIndex == team.SettleCgIndex), 20155040);
                if (mutation.State.Fight is { } pending)
                {
                    RequireCombat(mutation, pending.FightId);
                    Require(pending.StageIndex == index && pending.PreFightPayload != null, 20155040);
                    return;
                }
                mutation.State.Fight = new()
                {
                    RunId = mutation.State.RunId, FightId = RandomNumberGenerator.GetInt32(1, int.MaxValue),
                    Seed = unchecked((uint)RandomNumberGenerator.GetInt32(int.MinValue, int.MaxValue)),
                    StageId = request.PreFightData.StageId, StageIndex = index,
                    NodeId = mutation.Data.CurChapterDb!.CurNodeDb.NodeId, SlotId = slot.SlotId,
                    StartedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Team = Clone(team)
                };
                mutation.State.LastFightPacketId = 0;
                mutation.State.LastFightRequestKey = string.Empty;
                mutation.State.Fight.PreFightPayload = MessagePackSerializer.Serialize(BuildCombatData(mutation, request.PreFightData.GeneralSkill));
            }, nameof(PreFightRequest), request);
            response.FightData = MessagePackSerializer.Deserialize<PreFightResponse.PreFightResponseFightData>(committed.State.Fight!.PreFightPayload!);
            session.fight = new(request, response.FightData.FightId);
        }
        catch (ServerCodeException exception) { response = new() { Code = exception.Code }; }
        catch (MongoException exception) { session.log.Error($"Theatre persistence failed: {exception.Message}"); response = new() { Code = 1 }; }
        return true;
    }

    private static PreFightResponse.PreFightResponseFightData BuildCombatData(Mutation mutation, int generalSkill)
    {
        TheatreFightState fight = mutation.State.Fight!;
        TheatreTeamData team = fight.Team;
        StageTable stage = Rows<StageTable>().Single(row => row.StageId == fight.StageId);
        PreFightResponse.PreFightResponseFightData data = new()
        {
            FightId = checked((uint)fight.FightId), Seed = checked((uint)fight.Seed), StageId = fight.StageId,
            PassTimeLimit = Convert.ToInt32(stage.PassTimeLimit), Restartable = Convert.ToInt32(stage.Restartable) != 0,
            NormalEventIds = stage.NormalEventId.Select(id => (dynamic)id).ToList(),
            EventIds = GetFightEvents(mutation).Select(id => (dynamic)id).ToList(),
            // All 88 native graphs own their spawns. Eight have no LevelControl: empty invokes their authored fallback.
            MonsterLevel = FightModule.ResolveStageLevelControl(fight.StageId, checked((int)mutation.Session.player.PlayerData.Level))?.MonsterLevel ?? []
        };
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
            role.NpcData[position] = new
            {
                Character = character, Equips = equips, WeaponFashionId = weaponFashionId,
                Partner = robotId == 0 ? mutation.Session.character.Partners.FirstOrDefault(partner => partner.CharacterId == character.Id) : null,
                IsRobot = robotId != 0, RobotId = robotId, IsNpc = false,
                CharacterCareer = Rows<CharacterTable>().Single(row => row.Id == character.Id).Career,
                MagicIds = new Dictionary<int, int>()
            };
        }
        foreach (dynamic npc in role.NpcData.Values)
            foreach (var magic in FightModule.BuildObservationMagicIds(deployed, (CharacterData)npc.Character))
                npc.MagicIds.Add(magic.Key, magic.Value);
        Require(FightModule.IsValidGeneralSkill(generalSkill, deployed), 20155040);
        if (generalSkill > 0) data.EventIds.Add(FightModule.GeneralSkillFightEventId(generalSkill));
        data.RoleData.Add(role);
        return data;
    }

    private static void SpendReopen(Mutation mutation)
    {
        int maximum = Rows<TheatreDifficultyTable>().Single(row => row.Id == mutation.Data.DifficultyId).ReopenCount
            + GetModifiers(mutation).ReopenBonus;
        Require(mutation.Data.ReopenCount < maximum, 20155041);
        mutation.Data.ReopenCount++;
        mutation.Push(new NotifyTheatreReopenCount { ReopenCount = mutation.Data.ReopenCount });
    }

    internal static bool TryReboot(Session session, FightRebootRequest request, out FightRebootResponse response)
    {
        response = new();
        if (!OwnsCombat(session)) return false;
        try
        {
            Commit(session, mutation =>
            {
                TheatreFightState fight = RequireCombat(mutation, request.FightId);
                // Native XSingleReboot.Available is false without a configured RebootId.
                // Original tables authorize no profile; life-based restart is not native revival.
                var packet = MessagePackSerializer.Deserialize<PreFightResponse.PreFightResponseFightData>(fight.PreFightPayload!);
                Require(packet.RebootId > 0, 20155041);
                if (request.RebootCount > 0 && request.RebootCount == fight.RebootCount) return;
                Require(request.RebootCount == fight.RebootCount + 1, 20003041);
                // LOCAL: revival spends one life, never a Matrix reboot profile or currency.
                SpendReopen(mutation);
                fight.RebootCount = request.RebootCount;
            }, nameof(FightRebootRequest), request);
        }
        catch (ServerCodeException exception) { response.Code = exception.Code; }
        catch (MongoException exception) { session.log.Error($"Theatre persistence failed: {exception.Message}"); response.Code = 1; }
        return true;
    }

    internal static bool TryRestart(Session session, FightRestartRequest request, out FightRestartResponse response, int requestId = 0)
    {
        response = new();
        if (!OwnsCombat(session)) return false;
        try
        {
            Mutation committed = Commit(session, mutation =>
            {
                TheatreFightState fight = RequireCombat(mutation, request.FightId);
                if (fight.RestartReceipts.ContainsKey(requestId)) return;
                Require(Convert.ToInt32(Rows<StageTable>().Single(row => row.StageId == fight.StageId).Restartable) != 0, 20155021);
                // LOCAL: restarting spends one life; team reset never uses this counter.
                SpendReopen(mutation);
                fight.RestartCount++;
                fight.RebootCount = 0;
                fight.Seed = unchecked((uint)RandomNumberGenerator.GetInt32(int.MinValue, int.MaxValue));
                fight.StartedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var packet = MessagePackSerializer.Deserialize<PreFightResponse.PreFightResponseFightData>(fight.PreFightPayload!);
                packet.Seed = checked((uint)fight.Seed);
                fight.PreFightPayload = MessagePackSerializer.Serialize(packet);
                // Per-fight, per-connection retry window; login clears it before packet IDs can be reused.
                while (fight.RestartReceipts.Count >= 256) fight.RestartReceipts.Remove(fight.RestartReceipts.Keys.First());
                fight.RestartReceipts.Add(requestId, fight.Seed);
            }, nameof(FightRestartRequest), request, recovered =>
            {
                TheatreFightState fight = recovered.Fight!;
                while (!fight.RestartReceipts.ContainsKey(requestId) && fight.RestartReceipts.Count >= 256)
                    fight.RestartReceipts.Remove(fight.RestartReceipts.Keys.First());
                fight.RestartReceipts[requestId] = fight.Seed;
            });
            response.Seed = unchecked((int)committed.State.Fight!.RestartReceipts[requestId]);
        }
        catch (ServerCodeException exception) { response.Code = exception.Code; }
        catch (MongoException exception) { session.log.Error($"Theatre persistence failed: {exception.Message}"); response.Code = 1; }
        return true;
    }

    internal static bool TryLeaveFight(Session session, out LeaveFightResponse response, int requestId = 0)
    {
        response = new();
        PlayerTheatreState state = session.player.Theatre;
        bool hasReceipt = state.RequestReceipts.TryGetValue(requestId, out TheatreRequestReceipt? receipt)
            && receipt.ResponseName == nameof(LeaveFightResponse);
        if (!OwnsCombat(session) && !hasReceipt) return false;
        try
        {
            if (hasReceipt)
            {
                Require(state.PendingMutation == null && state.Fight == null && session.fight == null
                    && receipt!.RunId == state.RunId && state.LastFightRunId == state.RunId
                    && receipt.RequestKey == state.LastFightId.ToString(System.Globalization.CultureInfo.InvariantCulture), 20155021);
                response = MessagePackSerializer.Deserialize<LeaveFightResponse>(receipt!.Response);
                return true;
            }
            long fightId = session.player.Theatre.Fight?.FightId ?? 0;
            Commit(session, mutation =>
            {
                TheatreFightState fight = RequireCombat(mutation, fightId);
                CompleteCombat(mutation, fight, new() { Settle = new() { StageId = fight.StageId, IsWin = false, ChallengeCount = 1 } });
                RememberLeaveReceipt(mutation.State, requestId);
            }, nameof(LeaveFightRequest), fightId, recovered => RememberLeaveReceipt(recovered, requestId));
            session.fight = null;
        }
        catch (ServerCodeException exception) { response.Code = exception.Code; }
        catch (MongoException exception) { session.log.Error($"Theatre persistence failed: {exception.Message}"); response.Code = 1; }
        return true;
    }

    private static void RememberLeaveReceipt(PlayerTheatreState state, int requestId) => StoreReceipt(state, requestId, new()
    {
        RunId = state.RunId, MutationId = state.NextMutationId,
        RequestKey = state.LastFightId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ResponseName = nameof(LeaveFightResponse), Response = MessagePackSerializer.Serialize(new LeaveFightResponse())
    });

    internal static bool TrySettleFight(Session session, FightSettleResult result, out FightSettleResponse response, int requestId = 0)
    {
        response = new();
        if (!IsCombatStage(result.StageId) && !OwnsCombat(session)) return false;
        try
        {
            string requestKey = Convert.ToHexString(SHA256.HashData(MessagePackSerializer.Serialize(result)));
            PlayerTheatreState state = session.player.Theatre;
            if (state.PendingMutation == null && state.Fight == null && state.LastFightPacketId == requestId
                && state.LastFightRequestKey == requestKey && state.LastFightRunId == state.RunId
                && state.LastFightId == result.FightId && state.LastFightStageId == result.StageId
                && state.LastFightSettle is { } receipt)
            {
                response = MessagePackSerializer.Deserialize<FightSettleResponse>(receipt);
                return true;
            }
            Mutation committed = Commit(session, mutation =>
            {
                TheatreFightState fight = RequireCombat(mutation, result.FightId);
                Require(fight.StageId == result.StageId, 20155021);
                Require(fight.RebootCount == result.RebootCount, 20003041);
                ValidateCombatResult(mutation, result, fight);
                CompleteCombat(mutation, fight, new()
                {
                    Settle = new()
                    {
                        StageId = fight.StageId, IsWin = result.IsWin && !result.IsForceExit, ChallengeCount = 1,
                        LeftTime = checked((int)result.LeftTime), NpcHpInfo = result.NpcHpInfo
                    }
                });
                mutation.State.LastFightPacketId = requestId;
                mutation.State.LastFightRequestKey = requestKey;
            }, nameof(FightSettleRequest), result, recovered => recovered.LastFightPacketId = requestId);
            response = MessagePackSerializer.Deserialize<FightSettleResponse>(committed.State.LastFightSettle!);
            if (session.fight?.FightId == result.FightId) session.fight = null;
        }
        catch (ServerCodeException exception) { response = new() { Code = exception.Code }; }
        catch (MongoException exception) { session.log.Error($"Theatre persistence failed: {exception.Message}"); response = new() { Code = 1 }; }
        return true;
    }

    private static void CompleteCombat(Mutation mutation, TheatreFightState fight, FightSettleResponse response)
    {
        // LOCAL: defeat/retreat uses one life. ReopenCount is USED attempts, not remaining lives.
        if (!response.Settle.IsWin)
        {
            int maximum = Rows<TheatreDifficultyTable>().Single(row => row.Id == mutation.Data.DifficultyId).ReopenCount
                + GetModifiers(mutation).ReopenBonus;
            if (mutation.Data.ReopenCount < maximum) SpendReopen(mutation);
        }
        mutation.State.Fight = null;
        OnFightResult(mutation, response.Settle.IsWin, fight.StageIndex);
        mutation.State.LastFightId = fight.FightId;
        mutation.State.LastFightStageId = fight.StageId;
        mutation.State.LastFightRunId = fight.RunId;
        mutation.State.LastFightSettle = MessagePackSerializer.Serialize(response);
        mutation.State.LastFightPacketId = 0;
        mutation.State.LastFightRequestKey = string.Empty;
    }

    private static void ValidateCombatResult(Mutation mutation, FightSettleResult result, TheatreFightState fight)
    {
        Require(result.StartFrame >= 0 && result.SettleFrame >= result.StartFrame && result.PauseFrame >= 0
            && result.ExSkillPauseFrame >= 0 && result.LeftTime is >= 0 and <= int.MaxValue
            && result.TotalDamage >= 0 && result.TotalDamaged >= 0 && result.TotalCure >= 0, 1033);
        long frames = result.SettleFrame - result.StartFrame;
        Require(result.PauseFrame <= frames && result.ExSkillPauseFrame <= frames - result.PauseFrame, 1033);
        // Native simulation runs at 20 FPS; one second covers the persisted timestamp boundary.
        Require((frames - result.PauseFrame - result.ExSkillPauseFrame) / 20
            <= Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - fight.StartedAt) + 1, 1033);
        StageTable stage = Rows<StageTable>().Single(row => row.StageId == fight.StageId);
        Require(stage.PassTimeLimit is not > 0 || result.LeftTime <= stage.PassTimeLimit, 1033);
        Require(result.PlayerIds == null || result.PlayerIds.All(id => id == mutation.Session.player.PlayerData.Id), 1033);
        HashSet<int> characters = fight.Team.CardIds.Where(id => id > 0).ToHashSet();
        foreach (int id in fight.Team.RobotIds.Where(id => id > 0)) characters.Add(Rows<RobotTable>().Single(row => row.Id == id).CharacterId);
        foreach (NpcDpsTable npc in result.NpcDpsTable?.Values ?? Enumerable.Empty<NpcDpsTable>())
        {
            Require(npc.CharacterId == 0 || characters.Contains(npc.CharacterId), 1033);
            Require(npc.CharacterId == 0 || npc.RoleId == 0 || npc.RoleId == mutation.Session.player.PlayerData.Id, 1033);
            Require(npc.DamageTotal >= 0 && npc.DamageNormal >= 0 && npc.Cure >= 0 && npc.Hurt >= 0, 1033);
        }
        Require(result.DamageSourceDic != null && result.StringToListIntRecord != null, 1033);
        foreach (var damage in result.DamageSourceDic!)
            Require(damage.Value != null && damage.Value.Values.All(value => value >= 0), 1033);
        foreach (var record in result.StringToListIntRecord!.Values)
            Require(record != null, 1033);
    }
}
