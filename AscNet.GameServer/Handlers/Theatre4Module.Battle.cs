using System.Globalization;
using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Table.V2.share.character;
using AscNet.Table.V2.share.fuben;
using AscNet.Table.V2.share.robot;
using AscNet.Table.V2.share.theatre4;
using MessagePack;
using Newtonsoft.Json;
using MongoDB.Driver;

namespace AscNet.GameServer.Handlers;

// Awakening Tundra (Theatre4) battle integration: grid/fate encounter location and freeze,
// native PreFight roster projection from FightGroup->Fight->FightMold->MonsterGroup->Monster,
// report-identity settlement, sweep (pacify / red buy-death) and resurrection/restart.
//
// The client only ever authorises this mode's combat by stage id, and every Awakening Tundra
// stage id belongs to a FightMold row, so IsCombatStage claims exactly that set. A PreFight for
// one of those stages without a frozen Theatre4ActiveEncounter is an error, never a fall-through
// to the generic spawner: that is what stops a generic-stage bypass.
internal static partial class Theatre4Module
{
    private const int CombatAuthorizationError = 1033;
    private const int EncounterMissingError = 20218120;
    private const int BattleActiveError = 20218121;
    private const int EncounterConflictError = 20218122;
    private const int SweepUnavailableError = 20218123;
    private const int TeamError = 20218124;
    private const int RecruitmentError = 20009021;
    // EN XEnumConst.CHARACTER.GENERALSKILLID_NONESELECT (xmodule/XEnumConst.lua:445);
    // XFubenAgency.NetWorkPreFightRequest (xfuben/XFubenAgency.lua:4208-4210) maps it to battle wire 0.
    private const int GeneralSkillNoneSelected = 9999;
    private const int RebootCountError = 20003041;

    // Theatre4ActiveEncounter.Kind — how the frozen encounter was located.
    private const int EncounterKindGridFight = 1;
    private const int EncounterKindGridEvent = 2;
    private const int EncounterKindFate = 3;

    // EN XEnumConst.Theatre4.AssetType / ColorType.
    private const int AssetGold = 4;
    private const int AssetProsperity = 6;
    private const int AssetColorCostPoint = 16;
    private const int ColorRed = 1;

    // EN XEnumConst.Theatre4.FightLocateType / SweepType.
    private const int FightLocateGrid = 1;
    private const int FightLocateFate = 2;
    private const int SweepRed = 1;
    private const int SweepYellow = 2;

    private static readonly Lazy<HashSet<uint>> CombatStages = new(() =>
        Rows<Theatre4FightMoldTable>().SelectMany(row => row.StageId)
            .Where(id => id > 0).Select(id => checked((uint)id)).ToHashSet());

    internal static bool IsCombatStage(uint stageId) => CombatStages.Value.Contains(stageId);

    private static Theatre4ActiveEncounter RequireEncounter(Mutation mutation) =>
        mutation.State.ActiveEncounter
        ?? throw new ServerCodeException("No Theatre4 battle is pending.", EncounterMissingError);

    // "In battle" is ActiveEncounter != null && SettleReceipt == null; a frozen receipt is settled.
    private static bool InBattle(Mutation mutation) => mutation.State.ActiveEncounter is { SettleReceipt: null };

    //region encounter resolution


    private static (Theatre4FightGroupTable Group, Theatre4FightTable Fight, Theatre4FightMoldTable Mold)
        ValidateGroupEncounter(Theatre4FightGroupTable group, int stageId, int fightId)
    {
        Theatre4FightTable? fight = Rows<Theatre4FightTable>().FirstOrDefault(row => row.Id == fightId);
        if (fight is null)
            throw new ServerCodeException("Theatre4 fight is missing.", EncounterConflictError);
        Theatre4FightMoldTable? mold = Rows<Theatre4FightMoldTable>().FirstOrDefault(row => row.Id == fight.MoldId);
        if (mold is null || !mold.StageId.Contains(stageId))
            throw new ServerCodeException("Theatre4 fight mold does not own the encounter stage.", EncounterConflictError);
        return (group, fight, mold);
    }

    // Grid FightData stores the authored FightGroup row Id.
    // PrepareLogin upgrades legacy bucket-valued records before they reach this path.
    private static (Theatre4FightGroupTable Group, Theatre4FightTable Fight, Theatre4FightMoldTable Mold)
        ResolveGridEncounter(Theatre4GridData grid, int stageId)
    {
        Theatre4FightData fightData = grid.Fight
            ?? throw new ServerCodeException("Theatre4 grid has no fight.", EncounterConflictError);
        int fightId = grid.ContentId;
        Theatre4FightGroupTable? canonical = Rows<Theatre4FightGroupTable>()
            .FirstOrDefault(row => row.Id == fightData.FightGroupId && row.FightId == fightId)
            ?? throw new ServerCodeException("Theatre4 grid fight group row is missing.", EncounterConflictError);
        if (grid.ContentGroup > 0 && canonical.GroupId != grid.ContentGroup)
            throw new ServerCodeException("Theatre4 grid fight bucket disagrees with its authored row.",
                EncounterConflictError);
        return ValidateGroupEncounter(canonical, stageId, fightId);

    }
    private static (Theatre4FightTable Fight, Theatre4FightMoldTable Mold) ResolveFightEncounter(int fightId, int stageId)
    {
        Theatre4FightTable? fight = Rows<Theatre4FightTable>().FirstOrDefault(row => row.Id == fightId);
        if (fight is null)
            throw new ServerCodeException("Theatre4 event fight is missing.", EncounterConflictError);
        Theatre4FightMoldTable? mold = Rows<Theatre4FightMoldTable>().FirstOrDefault(row => row.Id == fight.MoldId);
        if (mold is null || !mold.StageId.Contains(stageId))
            throw new ServerCodeException("Theatre4 event fight does not own the encounter stage.", EncounterConflictError);
        return (fight, mold);
    }

    private static (Theatre4FightTable Fight, Theatre4FightMoldTable Mold)
        ResolveFrozenFight(Theatre4ActiveEncounter encounter)
    {
        if (encounter.Kind == EncounterKindGridFight)
        {
            Require(Rows<Theatre4FightGroupTable>().Any(row =>
                row.GroupId == encounter.FightGroupId && row.FightId == encounter.FightId),
                EncounterConflictError);
            return ResolveFightEncounter(encounter.FightId, encounter.StageId);
        }
        if (encounter.Kind is EncounterKindGridEvent or EncounterKindFate)
            return ResolveFightEncounter(encounter.FightId, encounter.StageId);
        throw new ServerCodeException("Theatre4 encounter kind is invalid.", EncounterConflictError);
    }

    //endregion

    //region locate + freeze

    [RequestPacketHandler("Theatre4FightLocateRequest")]
    public static void Theatre4FightLocate(Session session, Packet.Request packet) =>
        Handle<Theatre4FightLocateRequest, Theatre4FightLocateResponse>(session, packet, (m, request, response) =>
        {
            EnsureAvailable(session);
            Theatre4AdventureData adventure = RequireAdventure(m);
            if (m.State.ActiveEncounter is { SettleReceipt: null } existing)
            {
                bool sameTarget = IsSameLocateTarget(m, existing, request);
                if (existing.PreFightPayload is not null)
                {
                    // An authorized battle is immutable; only its exact, still-authored target may retry.
                    Require(sameTarget, BattleActiveError);
                    return;
                }
                // A room may be cancelled before PreFight and retargeted, but a retry of the same
                // target must retain the frozen UUID/seed rather than consume RNG again.
                if (sameTarget) return;
            }
            Require(request.Type is FightLocateGrid or FightLocateFate, EncounterConflictError);
            m.State.ActiveEncounter = request.Type == FightLocateGrid
                ? LocateGrid(m, request)
                : LocateFate(m, adventure, request);
        });

    private static bool IsSameLocateTarget(Mutation mutation, Theatre4ActiveEncounter encounter,
        Theatre4FightLocateRequest request)
    {
        if (!IsSameLocate(encounter, request)) return false;
        try
        {
            if (request.Type == FightLocateGrid)
            {
                Theatre4GridData? grid = TryFindGrid(mutation, request.MapId, request.PosX, request.PosY);
                if (grid is null || grid.State != T4State.Explored) return false;
                if (grid.Type is T4Grid.Monster or T4Grid.Boss && grid.Fight is { } fight)
                {
                    (Theatre4FightGroupTable group, Theatre4FightTable row, _) =
                        ResolveGridEncounter(grid, fight.StageId);
                    return encounter.Kind == EncounterKindGridFight && encounter.GridId == grid.GridId
                        && encounter.FightGroupId == group.GroupId && encounter.FightId == row.Id
                        && encounter.StageId == fight.StageId;
                }
                if (grid.Type == T4Grid.Event && grid.Event is { StageId: > 0, EventId: > 0 } gridEvent)
                {
                    Theatre4EventTable? authored = Rows<Theatre4EventTable>()
                        .FirstOrDefault(row => row.Id == gridEvent.EventId);
                    if (authored?.Type != 4) return false;
                    (Theatre4FightTable row, _) = ResolveFightEncounter(authored.FightId ?? 0, gridEvent.StageId);
                    return encounter.Kind == EncounterKindGridEvent && encounter.GridId == grid.GridId
                        && encounter.FightId == row.Id && encounter.StageId == gridEvent.StageId;
                }
                return false;
            }
            Theatre4FateEventData? fate = RequireAdventure(mutation).Fate?.FateEvents
                .FirstOrDefault(row => row.UniqueId == encounter.FateUniqueId);
            if (fate?.Event is not { StageId: > 0, EventId: > 0 } evt) return false;
            Theatre4EventTable? authoredFate = Rows<Theatre4EventTable>()
                .FirstOrDefault(row => row.Id == evt.EventId);
            if (authoredFate?.Type != 4) return false;
            (Theatre4FightTable fateFight, _) = ResolveFightEncounter(authoredFate.FightId ?? 0, evt.StageId);
            return encounter.Kind == EncounterKindFate && encounter.FightId == fateFight.Id
                && encounter.StageId == evt.StageId;
        }
        catch (ServerCodeException)
        {
            return false;
        }
    }

    private static bool IsSameLocate(Theatre4ActiveEncounter encounter, Theatre4FightLocateRequest request) =>
        request.Type switch
        {
            FightLocateGrid => encounter.Kind is EncounterKindGridFight or EncounterKindGridEvent
                && encounter.MapId == request.MapId && encounter.PosX == request.PosX && encounter.PosY == request.PosY,
            FightLocateFate => encounter.Kind == EncounterKindFate,
            _ => false
        };

    // ActiveEncounter.HpPercent is the existing server-side whole-percent field (0..100).
    // Grid FightData is native basis points (0..10000); convert only at the grid boundary.
    private static Theatre4ActiveEncounter LocateGrid(Mutation mutation, Theatre4FightLocateRequest request)
    {
        Require(request.MapId > 0, EncounterConflictError);
        Theatre4GridData grid = FindGrid(mutation, request.MapId, request.PosX, request.PosY);
        Require(grid.State == T4State.Explored, EncounterConflictError);
        if (grid.Type is T4Grid.Monster or T4Grid.Boss && grid.Fight is { FightGroupId: > 0, StageId: > 0 } fight)
        {
            (Theatre4FightGroupTable group, Theatre4FightTable row, Theatre4FightMoldTable mold) =
                ResolveGridEncounter(grid, fight.StageId);
            return Freeze(mutation, EncounterKindGridFight, request.MapId, request.PosX, request.PosY, grid.GridId, 0,
                group.GroupId, fight.StageId, row, mold,
                fight.HpPercent <= 0 ? 100 : Math.Clamp(fight.HpPercent / 100, 1, 100));
        }
        if (grid.Type == T4Grid.Event && grid.Event is { StageId: > 0, EventId: > 0 } gridEvent)
        {
            Theatre4EventTable? authored = Rows<Theatre4EventTable>().FirstOrDefault(row => row.Id == gridEvent.EventId);
            if (authored is null)
                throw new ServerCodeException("Theatre4 grid event is missing.", EncounterConflictError);
            Require(authored.Type == 4, EncounterConflictError);
            (Theatre4FightTable row, Theatre4FightMoldTable mold) =
                ResolveFightEncounter(authored.FightId ?? 0, gridEvent.StageId);
            return Freeze(mutation, EncounterKindGridEvent, request.MapId, request.PosX, request.PosY, grid.GridId, 0, 0,
                gridEvent.StageId, row, mold, 100);
        }
        throw new ServerCodeException("Theatre4 grid has no battle.", EncounterConflictError);
    }

    private static Theatre4ActiveEncounter LocateFate(Mutation mutation, Theatre4AdventureData adventure,
        Theatre4FightLocateRequest request)
    {
        // Local policy: at most one pending timeline fight may exist, because the client's
        // FightLocate carries no fate uid to disambiguate several simultaneously active fights.
        List<Theatre4FateEventData> pending = adventure.Fate?.FateEvents
            .Where(row => row.UniqueId > 0 && row.Event is { StageId: > 0, EventId: > 0 })
            .ToList() ?? [];
        Require(pending.Count == 1, EncounterConflictError);
        Theatre4FateEventData fate = pending[0];
        Theatre4EventTable? authored = Rows<Theatre4EventTable>().FirstOrDefault(row => row.Id == fate.Event!.EventId);
        if (authored is null)
            throw new ServerCodeException("Theatre4 fate event is missing.", EncounterConflictError);
        Require(authored.Type == 4, EncounterConflictError);
        (Theatre4FightTable row, Theatre4FightMoldTable mold) =
            ResolveFightEncounter(authored.FightId ?? 0, fate.Event!.StageId);
        return Freeze(mutation, EncounterKindFate, 0, 0, 0, 0, fate.UniqueId, 0, fate.Event!.StageId, row, mold, 100);
    }

    // Freeze receives the already-converted server-side whole-percent value. Fate/event encounters
    // have no grid FightData and therefore continue to enter here at 100.
    private static Theatre4ActiveEncounter Freeze(Mutation mutation, int kind, int mapId, int posX, int posY,
        int gridId, int fateUniqueId, int fightGroupId, int stageId, Theatre4FightTable fight,
        Theatre4FightMoldTable mold, int hpPercent)
    {
        if (hpPercent <= 0) hpPercent = 100;
        if (stageId <= 0 || !mold.StageId.Contains(stageId))
            throw new ServerCodeException("Theatre4 fight mold has no requested stage.", EncounterConflictError);
        return new Theatre4ActiveEncounter
        {
            Kind = kind,
            MapId = mapId,
            PosX = posX,
            PosY = posY,
            GridId = gridId,
            FateUniqueId = fateUniqueId,
            FightGroupId = fightGroupId,
            FightId = fight.Id,
            StageId = stageId,
            HpPercent = hpPercent,
            FightEvents = mold.FightEvents.Where(id => id > 0).ToList(),
            // The persisted per-run RNG is used once, and the result is frozen durably, so a
            // retry or relog can never reroll the encounter.
            FightUuid = RandomIndex(mutation, int.MaxValue - 1) + 1,
            Seed = unchecked((uint)RandomIndex(mutation, int.MaxValue)),
            StartedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
    }

    //region team data

    [RequestPacketHandler("Theatre4SetTeamDataRequest")]
    public static void Theatre4SetTeamData(Session session, Packet.Request packet) =>
        Handle<Theatre4SetTeamDataRequest, Theatre4SetTeamDataResponse>(session, packet, (m, request, response) =>
        {
            EnsureAvailable(session);
            Theatre4AdventureData adventure = RequireAdventure(m);
            Require(m.State.ActiveEncounter is not { SettleReceipt: null, PreFightPayload: not null }, BattleActiveError);
            Require(request.TeamData is not null, TeamError);
            Theatre4TeamData team = request.TeamData;
            ValidateTeamShape(team);
            ValidateRecruitedRoster(m, session, adventure, team);
            adventure.TeamData = Clone(team);
        });

    private static void ValidateTeamShape(Theatre4TeamData team)
    {
        Require(team.CardIds.Count == 3 && team.RobotIds.Count == 3, TeamError);
        Require(team.CaptainPos is >= 1 and <= 3 && team.FirstFightPos is >= 1 and <= 3, TeamError);
        Require(team.EnterCgIndex >= 0 && team.SettleCgIndex >= 0 && team.GeneralSkill >= 0, TeamError);
        for (int slot = 0; slot < 3; slot++)
            Require(team.CardIds[slot] >= 0 && team.RobotIds[slot] >= 0
                && (team.CardIds[slot] == 0 || team.RobotIds[slot] == 0), TeamError);
    }

    // Recruited-set authority: AdventureData.Characters is keyed by the Theatre4Character config
    // Id (XTheatre4Model.GetRecruitedCharacterConfigIds), while the team carries entity ids. A card
    // maps to the config row with CharacterId==card and a robot to the row with RobotId==robot;
    // both must be a recruited config Id.
    private static void ValidateRecruitedRoster(Mutation m, Session session, Theatre4AdventureData adventure,
        Theatre4TeamData team)
    {
        HashSet<int> recruited = adventure.Characters.Select(row => row.CharacterId).Where(id => id > 0).ToHashSet();
        HashSet<int> deployed = [];
        for (int slot = 0; slot < 3; slot++)
        {
            int card = team.CardIds[slot];
            int robot = team.RobotIds[slot];
            if (card == 0 && robot == 0) continue;
            int characterId;
            if (robot != 0)
            {
                Theatre4CharacterTable? recruitment = Rows<Theatre4CharacterTable>()
                    .FirstOrDefault(row => row.RobotId == robot);
                Require(recruitment is not null && recruited.Contains(recruitment.Id), RecruitmentError);
                Require(Rows<RobotTable>().Any(row => row.Id == robot), RecruitmentError);
                characterId = recruitment!.CharacterId;
            }
            else
            {
                Theatre4CharacterTable? recruitment = Rows<Theatre4CharacterTable>()
                    .FirstOrDefault(row => row.CharacterId == card);
                Require(recruitment is not null && recruited.Contains(recruitment.Id), RecruitmentError);
                Require(session.character.Characters.Any(row => row.Id == card), RecruitmentError);
                characterId = card;
            }
            Require(deployed.Add(characterId), TeamError);
        }
        Require(deployed.Count > 0, TeamError);
        Require(team.CardIds[team.CaptainPos - 1] != 0 || team.RobotIds[team.CaptainPos - 1] != 0, TeamError);
        Require(team.CardIds[team.FirstFightPos - 1] != 0 || team.RobotIds[team.FirstFightPos - 1] != 0, TeamError);
    }

    //endregion

    //region fight hooks

    internal static bool TryPreFight(Session session, PreFightRequest request, out PreFightResponse response, int packetId)
    {
        response = new();
        if (request.PreFightData is null || !IsCombatStage(request.PreFightData.StageId)) return false;
        try
        {
            PreFightResponse committed = Commit<PreFightRequest, PreFightResponse>(
                session, packetId, nameof(PreFightRequest), request, (mutation, prepared) =>
                {
                    Theatre4ActiveEncounter encounter = RequireEncounter(mutation);
                    Theatre4AdventureData adventure = RequireAdventure(mutation);
                    Require(InBattle(mutation), BattleActiveError);
                    Require(encounter.StageId == request.PreFightData.StageId, EncounterConflictError);
                    (Theatre4FightTable fight, Theatre4FightMoldTable mold) = ResolveFrozenFight(encounter);
                    ValidatePreFightRequest(mutation, session, adventure, request.PreFightData, encounter);
                    if (encounter.PreFightPayload is not null)
                    {
                        // Retry of an already-authorized battle replays the frozen authorization.
                        prepared.FightData = MessagePackSerializer.Deserialize<PreFightResponse.PreFightResponseFightData>(
                            encounter.PreFightPayload);
                        return;
                    }
                    prepared.FightData = BuildFightData(mutation, session, adventure, encounter, fight, mold,
                        request.PreFightData);
                    encounter.PreFightPayload = MessagePackSerializer.Serialize(prepared.FightData);
                    encounter.AttemptCount = checked(encounter.AttemptCount + 1);
                });
            response = committed;
            if (committed.FightData is not null)
                session.fight = new(request, committed.FightData.FightId);
        }
        catch (ServerCodeException exception) { response = new() { Code = exception.Code }; }
        catch (OverflowException) { response = new() { Code = CombatAuthorizationError }; }
        catch (MongoException exception)
        {
            session.log.Error($"Theatre4 persistence failed: {exception.Message}");
            response = new() { Code = 1 };
        }
        catch (MessagePackSerializationException) { response = new() { Code = 1 }; }
        return true;
    }

    private static void ValidatePreFightRequest(Mutation mutation, Session session, Theatre4AdventureData adventure,
        PreFightRequest.PreFightRequestPreFightData request, Theatre4ActiveEncounter encounter)
    {
        Require(request.ChallengeCount == 1 && !request.IsHasAssist && request.SpeedrunStageId == 0
            && request.SelectAreaId == 0, CombatAuthorizationError);
        Require(request.CardIds is not null && request.RobotIds is not null, TeamError);
        Require(request.CardIds.Count == 3 && request.RobotIds.Count == 3, TeamError);
        // The battle room may open and be entered before an explicit SetTeamData save; the
        // PreFight request is then the authoritative deployment and is validated on its own.
        Theatre4TeamData? stored = adventure.TeamData;
        if (stored is null)
        {
            stored = new Theatre4TeamData
            {
                CardIds = request.CardIds.Select(id => checked((int)id)).ToList(),
                RobotIds = request.RobotIds.ToList(),
                CaptainPos = request.CaptainPos,
                FirstFightPos = request.FirstFightPos,
                EnterCgIndex = request.EnterCgIndex,
                SettleCgIndex = request.SettleCgIndex,
                GeneralSkill = request.GeneralSkill
            };
            ValidateTeamShape(stored);
            ValidateRecruitedRoster(mutation, session, adventure, stored);
            adventure.TeamData = stored;
        }
        else
        {
            ValidateTeamShape(stored);
            Require(request.CardIds.SequenceEqual(stored.CardIds.Select(id => checked((uint)id)))
                && request.RobotIds.SequenceEqual(stored.RobotIds)
                && request.CaptainPos == stored.CaptainPos && request.FirstFightPos == stored.FirstFightPos
                && request.EnterCgIndex == stored.EnterCgIndex && request.SettleCgIndex == stored.SettleCgIndex
                && request.GeneralSkill == (stored.GeneralSkill == GeneralSkillNoneSelected ? 0 : stored.GeneralSkill), TeamError);
            ValidateRecruitedRoster(mutation, session, adventure, stored);
        }
        if (encounter.CardIds.Count == 0)
        {
            encounter.CardIds = stored.CardIds.ToList();
            encounter.RobotIds = stored.RobotIds.ToList();
        }
        else
            Require(encounter.CardIds.SequenceEqual(stored.CardIds) && encounter.RobotIds.SequenceEqual(stored.RobotIds),
                EncounterConflictError);
    }

    private static PreFightResponse.PreFightResponseFightData BuildFightData(Mutation mutation, Session session,
        Theatre4AdventureData adventure, Theatre4ActiveEncounter encounter, Theatre4FightTable fight,
        Theatre4FightMoldTable mold, PreFightRequest.PreFightRequestPreFightData request)
    {
        Theatre4DifficultyTable difficulty = Rows<Theatre4DifficultyTable>()
            .FirstOrDefault(row => row.Id == adventure.Difficulty)
            ?? throw new ServerCodeException("Theatre4 difficulty is missing.", EncounterConflictError);
        StageTable stage = Rows<StageTable>().FirstOrDefault(row => row.StageId == encounter.StageId)
            ?? throw new ServerCodeException("Theatre4 fight stage is missing.", EncounterConflictError);
        Theatre4CombatProjection projection = ProjectCombat(mutation, encounter.FightId);
        int[] factors = EnemyFactors(adventure, difficulty);
        int actionCount = Math.Max(1, adventure.ExploreCount);
        Theatre4ActionFactorTable authoredTheatre4ActionFactor = Rows<Theatre4ActionFactorTable>()
            .Where(row => row.Group == difficulty.ActionGroup && row.ActionTimes > 0
                && row.ActionTimes <= actionCount)
            .OrderByDescending(row => row.ActionTimes).FirstOrDefault()
            ?? throw new ServerCodeException("Theatre4 action factor is missing.", EncounterConflictError);
        if (authoredTheatre4ActionFactor.Factor <= 0)
            throw new ServerCodeException("Theatre4 action factor is invalid.", EncounterConflictError);
        PreFightResponse.PreFightResponseFightData data = new()
        {
            FightId = checked((uint)encounter.FightUuid),
            Seed = encounter.Seed,
            StageId = checked((uint)encounter.StageId),
            RebootId = difficulty.RebootId,
            PassTimeLimit = stage.PassTimeLimit ?? 0,
            Restartable = Convert.ToInt32(stage.Restartable) != 0,
            NormalEventIds = stage.NormalEventId.Where(id => id > 0).Select(id => (dynamic)id).ToList(),
            EventIds = encounter.FightEvents.Concat(projection.GlobalEventIds).Distinct()
                .Select(id => (dynamic)id).ToList(),
            FightEventsWithLevel = projection.LeveledEvents
                .Select(entry => (dynamic)new { FightEventId = entry.FightEventId, FightEventLevel = entry.Level }).ToList(),
            // Native stage initialization gates Mode4 on these keys. AscNet policy counts one
            // successful grid exploration as one action and uses the highest authored row <= max(1, ExploreCount).
            StageParams = new Dictionary<string, string>
            {
                ["Mode"] = mold.Mode.ToString(CultureInfo.InvariantCulture),
                ["ModeParams"] = JsonConvert.SerializeObject(mold.ModeParams),
                ["Theater4DiffcultId"] = difficulty.Id.ToString(CultureInfo.InvariantCulture),
                ["Theater4HpFactor"] = factors[1].ToString(CultureInfo.InvariantCulture),
                ["Theater4ActionNum"] = authoredTheatre4ActionFactor.Factor.ToString(CultureInfo.InvariantCulture)
            }
        };

        List<int> groups = mold.MonsterGroupIds.Where(id => id > 0).ToList();
        data.NpcGroupList = groups.Select(groupId => new
        {
            NpcList = (Rows<Theatre4MonsterGroupTable>().FirstOrDefault(row => row.Id == groupId)
                ?? throw new ServerCodeException("Theatre4 monster group is missing.", EncounterConflictError))
                .MonsterIds.Where(id => id > 0).Select(monsterId =>
            {
                Theatre4MonsterTable monster = Rows<Theatre4MonsterTable>().FirstOrDefault(row => row.Id == monsterId)
                    ?? throw new ServerCodeException("Theatre4 monster is missing.", EncounterConflictError);
                if (monster.BuffIds.Count != monster.BuffLevel.Count)
                    throw new InvalidDataException($"Theatre4 monster {monster.Id} has unpaired buff levels.");
                Dictionary<int, int> magic = [];
                for (int index = 0; index < monster.BuffIds.Count; index++)
                    if (monster.BuffIds[index] > 0 && monster.BuffLevel[index] > 0)
                        magic[monster.BuffIds[index]] = monster.BuffLevel[index];
                foreach (var extra in projection.MonsterMagicIds)
                    if (extra.Key > 0 && extra.Value > 0)
                        magic[extra.Key] = magic.TryGetValue(extra.Key, out int existing)
                            ? Math.Max(existing, extra.Value) : extra.Value;
                return new
                {
                    monster.NpcId,
                    Level = ScaleLevel(monster.Level, factors),
                    BufferIds = Array.Empty<int>(),
                    MagicInfos = magic.Select(pair => new { MagicId = pair.Key, Level = pair.Value }).ToList(),
                    AttrTable = new Dictionary<int, object>()
                };
            }).ToList()
        }).ToList();

        Theatre4TeamData team = adventure.TeamData
            ?? throw new ServerCodeException("Theatre4 team is missing.", TeamError);
        PreFightResponse.PreFightResponseFightData.PreFightResponseFightDataRoleData role = new()
        {
            Id = checked((uint)session.player.PlayerData.Id), Camp = 1, Name = session.player.PlayerData.Name,
            CaptainIndex = team.CaptainPos - 1, FirstFightPos = team.FirstFightPos - 1,
            EnterCgIndex = team.EnterCgIndex - 1, SettleCgIndex = team.SettleCgIndex - 1,
            NpcData = []
        };
        List<CharacterData> deployed = [];
        for (int position = 0; position < 3; position++)
        {
            int cardId = encounter.CardIds[position];
            int robotId = encounter.RobotIds[position];
            if (cardId == 0 && robotId == 0) continue;
            CharacterData character;
            IReadOnlyList<EquipData> equips;
            int weaponFashionId;
            if (robotId != 0)
            {
                RobotTable robot = Rows<RobotTable>().FirstOrDefault(row => row.Id == robotId)
                    ?? throw new ServerCodeException("Theatre4 robot is missing.", TeamError);
                var deployment = FightModule.BuildRobotDeployment(robot);
                character = deployment.Character;
                equips = deployment.Equips;
                weaponFashionId = robot.WeaponFashion ?? 0;
            }
            else
            {
                character = session.character.Characters.FirstOrDefault(row => row.Id == cardId)
                    ?? throw new ServerCodeException("Theatre4 character is not owned.", RecruitmentError);
                equips = FightModule.BuildTeamPrefabFightEquips(session, checked((uint)cardId));
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                weaponFashionId = session.character.WeaponFashions.FirstOrDefault(row =>
                    (row.ExpireTime == 0 || row.ExpireTime > now) && row.UseCharacterList.Contains(cardId))?.Id ?? 0;
            }
            deployed.Add(character);
            role.NpcData[position] = new
            {
                Character = character, Equips = equips, WeaponFashionId = weaponFashionId,
                EventIds = projection.PlayerEventIds.ToList(),
                BaseAttrs = projection.PlayerBaseAttrs,
                Partner = robotId == 0
                    ? session.character.Partners.FirstOrDefault(partner => partner.CharacterId == character.Id)
                    : null,
                IsRobot = robotId != 0, RobotId = robotId, IsNpc = false,
                CharacterCareer = (Rows<CharacterTable>().FirstOrDefault(row => row.Id == character.Id)
                    ?? throw new ServerCodeException("Theatre4 character career is missing.", TeamError)).Career,
                MagicIds = projection.PlayerMagicIds.ToDictionary(pair => pair.Key, pair => pair.Value)
            };
        }
        foreach (dynamic npc in role.NpcData.Values)
            foreach (var magic in FightModule.BuildObservationMagicIds(deployed, (CharacterData)npc.Character))
                npc.MagicIds.Add(magic.Key, magic.Value);
        Require(FightModule.IsValidGeneralSkill(request.GeneralSkill, deployed), TeamError);
        if (request.GeneralSkill > 0) data.EventIds.Add(FightModule.GeneralSkillFightEventId(request.GeneralSkill));
        data.RoleData.Add(role);
        return data;
    }

    // AscNet Theatre4 policy: the shipped FightMold roster names only NpcId + Level, so the
    // authored per-chapter difficulty factors are applied to the authored monster level; both
    // native attack and health scale with level. The factor arrays are indexed by the run's
    // current chapter, exactly as the Difficulty row is authored.
    private static int[] EnemyFactors(Theatre4AdventureData adventure, Theatre4DifficultyTable difficulty)
    {
        int chapter = Math.Clamp(Math.Max(1, adventure.Chapters.Count), 1, 5) - 1;
        return [Factor(difficulty.AtkFactor, chapter), Factor(difficulty.HpFactor, chapter)];
    }

    private static int Factor(List<int> values, int index)
    {
        if (index < 0 || index >= values.Count || values[index] <= 0)
            throw new ServerCodeException("Theatre4 difficulty factor is missing or invalid.", EncounterConflictError);
        return values[index];
    }

    private static int ScaleLevel(int level, int[] factors)
    {
        // The authored monster level is the difficulty-1 baseline; greater authored
        // difficulty factors only raise it.
        long basis = Math.Max(10000, ((long)factors[0] + factors[1]) / 2);
        return Math.Max(1, checked((int)Math.Round(level * basis / 10000d)));
    }

    //endregion

    //region settle

    internal static bool TrySettleFight(Session session, FightSettleResult result, out FightSettleResponse response,
        int packetId)
    {
        response = new();
        if (!IsCombatStage(result.StageId)) return false;
        try
        {
            FightSettleRequest request = new() { Result = result };
            response = Commit<FightSettleRequest, FightSettleResponse>(
                session, packetId, nameof(FightSettleRequest), request, (mutation, settled) =>
                {
                    Theatre4ActiveEncounter encounter = RequireEncounter(mutation);
                    if (encounter.SettleReceipt is { } frozen)
                    {
                        // Idempotent duplicate: replay the exact frozen settlement, never re-grant.
                        Require(result.FightId == encounter.FightUuid && result.StageId == encounter.StageId,
                            EncounterConflictError);
                        settled.Settle = MessagePackSerializer.Deserialize<FightSettleResponse>(frozen).Settle;
                        return;
                    }
                    RequireAdventure(mutation);
                    Require(result.FightId == encounter.FightUuid, EncounterConflictError);
                    Require(result.StageId == encounter.StageId, EncounterConflictError);
                    Require(result.RebootCount == encounter.RebootCount, RebootCountError);
                    ValidateCombatReport(mutation, session, encounter, result);
                    bool won = result.IsWin && !result.IsForceExit;
                    int score = won ? Math.Max(0, result.Achievement) : 0;
                    if (won) CreateFightRewardTransaction(mutation, encounter.FightId);
                    Theatre4GridData? triggerGrid = encounter.MapId > 0
                        ? TryFindGrid(mutation, encounter.MapId, encounter.PosX, encounter.PosY)
                        : null;
                    Require(encounter.MapId <= 0 || triggerGrid is not null, EncounterConflictError);
                    TriggerEffects(mutation, "fight", triggerGrid, won ? 1 : 0);
                    // Score is the authored stage score the event options compare against.
                    RecordEncounterScore(mutation, encounter, score);
                    // Map/Events owns FinishFightIds, grid bookkeeping and the change-grid pushes; it
                    // can end the adventure (Data.AdventureData becomes null), so it runs last.
                    CompleteEncounter(mutation, won, score);
                    settled.Settle = new()
                    {
                        IsWin = won, StageId = encounter.StageId, ChallengeCount = 1,
                        LeftTime = checked((int)result.LeftTime), NpcHpInfo = result.NpcHpInfo
                    };
                    encounter.SettleKey = mutation.NextClaimKey();
                    encounter.SettleReceipt = MessagePackSerializer.Serialize(settled);
                });
            if (session.fight?.FightId == unchecked((uint)result.FightId)) session.fight = null;
        }
        catch (ServerCodeException exception) { response = new() { Code = exception.Code }; }
        catch (MongoException exception)
        {
            session.log.Error($"Theatre4 persistence failed: {exception.Message}");
            response = new() { Code = 1 };
        }
        catch (MessagePackSerializationException) { response = new() { Code = 1 }; }
        return true;
    }

    // The native event entity reports StageScore; OptionType CheckStageScore compares it.
    // Local policy: the settled achievement value is the score for a grid or fate event fight.
    private static void RecordEncounterScore(Mutation mutation, Theatre4ActiveEncounter encounter, int score)
    {
        if (encounter.MapId > 0)
        {
            Theatre4GridData? grid = TryFindGrid(mutation, encounter.MapId, encounter.PosX, encounter.PosY);
            if (grid?.Event is { } gridEvent) gridEvent.StageScore = score;
            return;
        }
        Theatre4FateEventData? fate = RequireAdventure(mutation).Fate?.FateEvents
            .FirstOrDefault(row => row.UniqueId == encounter.FateUniqueId);
        if (fate?.Event is { } fateEvent) fateEvent.StageScore = score;
    }

    // Structural report identity only: invariants the native producer cannot violate. No damage
    // equation is invented from the source format.
    private static void ValidateCombatReport(Mutation mutation, Session session, Theatre4ActiveEncounter encounter,
        FightSettleResult result)
    {
        Require(result.StartFrame >= 0 && result.SettleFrame >= result.StartFrame && result.PauseFrame >= 0
            && result.ExSkillPauseFrame >= 0
// Native untimed stages may produce negative countdown values
            && result.LeftTime is >= int.MinValue and <= int.MaxValue
            && result.TotalDamage >= 0 && result.TotalDamaged >= 0 && result.TotalCure >= 0, CombatAuthorizationError);
        long frames = result.SettleFrame - result.StartFrame;
        Require(result.PauseFrame <= frames && result.ExSkillPauseFrame <= frames - result.PauseFrame,
            CombatAuthorizationError);
        // Native XFightConfig.FPS is 20; one second covers the persisted whole-second clock boundary.
        long activeFrames = frames - result.PauseFrame - result.ExSkillPauseFrame;
        Require(activeFrames / 20 <= Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - encounter.StartedAt) + 1,
            CombatAuthorizationError);
        StageTable stage = Rows<StageTable>().FirstOrDefault(row => row.StageId == result.StageId)
            ?? throw new ServerCodeException("Theatre4 fight stage is missing.", CombatAuthorizationError);
        Require(stage.PassTimeLimit is not > 0 || result.LeftTime <= stage.PassTimeLimit, CombatAuthorizationError);
        Require(result.PlayerIds is null || result.PlayerIds.All(id => id == session.player.PlayerData.Id),
            CombatAuthorizationError);
        HashSet<int> characters = encounter.CardIds.Where(id => id > 0).ToHashSet();
        foreach (int robotId in encounter.RobotIds.Where(id => id > 0))
            characters.Add((Rows<RobotTable>().FirstOrDefault(row => row.Id == robotId)
                ?? throw new ServerCodeException("Theatre4 settlement robot is missing.", CombatAuthorizationError))
                .CharacterId);
        foreach (NpcDpsTable npc in result.NpcDpsTable?.Values ?? Enumerable.Empty<NpcDpsTable>())
        {
            Require(npc.CharacterId == 0 || characters.Contains(npc.CharacterId), CombatAuthorizationError);
            Require(npc.CharacterId == 0 || npc.RoleId == 0 || npc.RoleId == session.player.PlayerData.Id,
                CombatAuthorizationError);
            Require(npc.DamageTotal >= 0 && npc.DamageNormal >= 0 && npc.Cure >= 0 && npc.Hurt >= 0,
                CombatAuthorizationError);
        }
        if (result.DamageSourceDic is null || result.StringToListIntRecord is null)
            throw new ServerCodeException("Theatre4 damage history is malformed.", CombatAuthorizationError);
        foreach (var damage in result.DamageSourceDic)
            Require(damage.Value is not null && damage.Value.Values.All(value => value >= 0), CombatAuthorizationError);
        foreach (var record in result.StringToListIntRecord.Values)
            Require(record is not null && (record.Count == 0 || characters.Contains(record[0] / 10)),
                CombatAuthorizationError);
    }

    //endregion

    //region sweep

    [RequestPacketHandler("Theatre4SweepMosnterRequest")]
    public static void Theatre4SweepMosnter(Session session, Packet.Request packet) =>
        Handle<Theatre4SweepMosnterRequest, Theatre4SweepMosnterResponse>(session, packet, (m, request, response) =>
        {
            EnsureAvailable(session);
            Theatre4AdventureData adventure = RequireAdventure(m);
            Theatre4GridData grid = FindGrid(m, request.MapId, request.PosX, request.PosY);
            Require(grid.State == T4State.Explored && grid.Type is T4Grid.Monster or T4Grid.Boss,
                SweepUnavailableError);
            Theatre4FightData fight = grid.Fight
                ?? throw new ServerCodeException("Theatre4 grid has no fight to sweep.", EncounterConflictError);
            (Theatre4FightGroupTable group, Theatre4FightTable row, _) =
                ResolveGridEncounter(grid, fight.StageId);
            Theatre4CombatProjection projection = ProjectCombat(m, row.Id);
            int difficulty = adventure.Difficulty;
            int prosperityLimit = RowIndex(group.ProsperityLimit, difficulty);
            Require(AssetCount(m, AssetProsperity, 0) >= prosperityLimit, SweepUnavailableError);

            Require(request.SweepType is SweepRed or SweepYellow, SweepUnavailableError);
            if (request.SweepType == SweepYellow)
            {
                Require(projection.SweepEnabled, SweepUnavailableError);
                int cost = (int)((long)RowIndex(group.ClearCost, difficulty) * projection.SweepDiscountBp / 10000);
                SpendAsset(m, AssetGold, 0, Math.Max(0, cost));
                // Type421 is authored in basis points of the native 0..10000 HP field.
                int hp = fight.HpPercent is > 0 and <= 10000 ? fight.HpPercent : 10000;
                fight.HpPercent = Math.Max(0, hp - projection.SweepYellowHpBasisPoints);
                if (fight.HpPercent > 0)
                {
                    m.Push(new NotifyTheatre4ChangeGrids { MapId = request.MapId, Grids = [grid] });
                    return;
                }
            }
            else
            {
                Require(projection.RedBuyDeathEnabled, SweepUnavailableError);
                int hp = fight.HpPercent is > 0 and <= 10000 ? fight.HpPercent : 10000;
                // Native red buy-death floors the remaining basis-point HP to whole percent
                // before applying the authored full red clear cost.
                int cost = (int)((long)(hp / 100) * RowIndex(group.ClearRedColorCost, difficulty) / 100);
                SpendAsset(m, AssetColorCostPoint, ColorRed, Math.Max(0, cost));
            }

            adventure.EffectSweepTimes = checked(adventure.EffectSweepTimes + 1);
            CreateFightRewardTransaction(m, row.Id);
            TriggerEffects(m, "sweep", grid, 1);
            RecordMetaProgress(m, "Sweep");
            m.Push(new NotifyTheatre4CustomCounter
            {
                EffectShopBuyTimes = adventure.EffectShopBuyTimes,
                EffectSweepTimes = adventure.EffectSweepTimes
            });
            // Completion can advance the route or end the adventure; Map owns the resulting pushes.
            CompleteSweep(m, grid, request.MapId, row.Id);
        });

    // Arrow Tower (effect type 106) clears an authored ordinary/elite grid without fabricating
    // a client battle report. Its dedicated trigger preserves tower-only authored effects; it
    // deliberately does not count as Pacify/sweep or emit those counters.
    internal static void CompleteTheatre4BuildingClear(Mutation mutation, Theatre4GridData grid)
    {
        Theatre4AdventureData adventure = RequireAdventure(mutation);
        Require(grid.Type == T4Grid.Monster && grid.State is T4State.Visible or T4State.Discover or T4State.Explored,
            EncounterConflictError);
        Theatre4ChapterData chapter = adventure.Chapters.FirstOrDefault(candidate =>
            candidate.Grids.Any(candidateGrid => ReferenceEquals(candidateGrid, grid)))
            ?? throw new ServerCodeException("Theatre4 building target is outside the run.", EncounterConflictError);
        Theatre4FightData fight = grid.Fight
            ?? throw new ServerCodeException("Theatre4 building target has no fight.", EncounterConflictError);
        (_, Theatre4FightTable row, _) = ResolveGridEncounter(grid, fight.StageId);
        CreateFightRewardTransaction(mutation, row.Id);
        TriggerEffects(mutation, "tower-clear", grid, 1);
        CompleteSweep(mutation, grid, chapter.MapId, row.Id);
    }

    // CompleteEncounter locates the encounter through the frozen ActiveEncounter context, so a
    // sweep (which has no battle) publishes the same grid context for the duration of the call.
    private static void CompleteSweep(Mutation mutation, Theatre4GridData grid, int mapId, int fightId)
    {
        Theatre4ActiveEncounter? previous = mutation.State.ActiveEncounter;
        mutation.State.ActiveEncounter = new Theatre4ActiveEncounter
        {
            Kind = EncounterKindGridFight,
            MapId = mapId,
            PosX = grid.PosX,
            PosY = grid.PosY,
            GridId = grid.GridId,
            FightGroupId = grid.ContentGroup,
            FightId = fightId,
            StageId = grid.Fight?.StageId ?? 0,
            HpPercent = (grid.Fight?.HpPercent ?? 0) <= 0
                ? 100 : Math.Clamp((grid.Fight?.HpPercent ?? 0) / 100, 1, 100)
        };
        try { CompleteEncounter(mutation, true, 0); }
        finally { mutation.State.ActiveEncounter = previous; }
    }

    private static int RowIndex(List<int> values, int oneBasedId) => values.Count == 0
        ? 0 : values[Math.Clamp(oneBasedId - 1, 0, values.Count - 1)];

    //endregion

    //region reboot / restart / leave / relog

    private static Theatre4RebootTable RebootProfile(int rebootId) =>
        Rows<Theatre4RebootTable>().FirstOrDefault(row => row.Id == rebootId)
        ?? throw new ServerCodeException("Theatre4 reboot configuration is missing.", EncounterConflictError);

    private static int RebootId(Mutation mutation)
    {
        Theatre4AdventureData adventure = RequireAdventure(mutation);
        return (Rows<Theatre4DifficultyTable>().FirstOrDefault(row => row.Id == adventure.Difficulty)
            ?? throw new ServerCodeException("Theatre4 difficulty is missing.", EncounterConflictError)).RebootId;
    }

    internal static bool TryReboot(Session session, FightRebootRequest request, out FightRebootResponse response, int packetId)
    {
        response = new();
        if (session.player.Theatre4.ActiveEncounter is null) return false;
        if (session.fight is { } active && !IsCombatStage(active.PreFight.PreFightData.StageId)) return false;
        try
        {
            response = Commit<FightRebootRequest, FightRebootResponse>(
                session, packetId, nameof(FightRebootRequest), request, (mutation, _) =>
                {
                    Theatre4ActiveEncounter encounter = RequireEncounter(mutation);
                    Require(InBattle(mutation), BattleActiveError);
                    Require(request.FightId > 0 && unchecked((uint)request.FightId) == encounter.FightUuid,
                        EncounterConflictError);
                    if (request.RebootCount > 0 && request.RebootCount == encounter.RebootCount) return;
                    Require(request.RebootCount == encounter.RebootCount + 1, RebootCountError);
                    Theatre4RebootTable profile = RebootProfile(RebootId(mutation));
                    Require(encounter.AttemptCount <= profile.MaxRebootCount, RebootCountError);
                    SpendAsset(mutation, AssetGold, 0, profile.RebootCost);
                    encounter.RebootCount = request.RebootCount;
                    encounter.AttemptCount = checked(encounter.AttemptCount + 1);
                });
        }
        catch (ServerCodeException exception) { response.Code = exception.Code; }
        catch (MongoException exception)
        {
            session.log.Error($"Theatre4 persistence failed: {exception.Message}");
            response.Code = 1;
        }
        catch (MessagePackSerializationException) { response.Code = 1; }
        return true;
    }

    internal static bool TryRestart(Session session, FightRestartRequest request, out FightRestartResponse response,
        int packetId)
    {
        response = new();
        if (session.player.Theatre4.ActiveEncounter is null) return false;
        if (session.fight is { } active && !IsCombatStage(active.PreFight.PreFightData.StageId)) return false;
        try
        {
            response = Commit<FightRestartRequest, FightRestartResponse>(
                session, packetId, nameof(FightRestartRequest), request, (mutation, restarted) =>
                {
                    Theatre4ActiveEncounter encounter = RequireEncounter(mutation);
                    Require(InBattle(mutation), BattleActiveError);
                    Require(request.FightId > 0 && unchecked((uint)request.FightId) == encounter.FightUuid,
                        EncounterConflictError);
                    if (encounter.RestartReceipts.TryGetValue(packetId, out long stored))
                    {
                        restarted.Seed = checked((int)stored);
                        return;
                    }
                    StageTable restartStage = Rows<StageTable>().FirstOrDefault(row => row.StageId == encounter.StageId)
                        ?? throw new ServerCodeException("Theatre4 fight stage is missing.", EncounterConflictError);
                    Require(Convert.ToInt32(restartStage.Restartable) != 0, CombatAuthorizationError);
                    Theatre4RebootTable profile = RebootProfile(RebootId(mutation));
                    Require(encounter.AttemptCount <= profile.MaxRebootCount, RebootCountError);
                    SpendAsset(mutation, AssetGold, 0, profile.FubenRestartCost);
                    encounter.AttemptCount = checked(encounter.AttemptCount + 1);
                    encounter.RebootCount = 0;
                    encounter.Seed = unchecked((uint)RandomIndex(mutation, int.MaxValue));
                    encounter.StartedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    encounter.RestartReceipts[packetId] = encounter.Seed;
                    if (encounter.PreFightPayload is { } payload)
                    {
                        PreFightResponse.PreFightResponseFightData data =
                            MessagePackSerializer.Deserialize<PreFightResponse.PreFightResponseFightData>(payload);
                        data.Seed = encounter.Seed;
                        encounter.PreFightPayload = MessagePackSerializer.Serialize(data);
                    }
                    restarted.Seed = unchecked((int)encounter.Seed);
                });
        }
        catch (ServerCodeException exception) { response = new() { Code = exception.Code }; }
        catch (MongoException exception)
        {
            session.log.Error($"Theatre4 persistence failed: {exception.Message}");
            response = new() { Code = 1 };
        }
        catch (MessagePackSerializationException) { response = new() { Code = 1 }; }
        return true;
    }

    // Explicit local Leave/Enter abandonment shares the loss receipt; login recovery deliberately
    // preserves an authorized frozen encounter for the native combat reconnect path.
    private static void AbandonEncounter(Mutation mutation)
    {
        Theatre4ActiveEncounter encounter = RequireEncounter(mutation);
        if (encounter.SettleReceipt is not null) return;
        int stageId = encounter.StageId;
        CompleteEncounter(mutation, false, 0);
        if (mutation.State.ActiveEncounter is not { } settled) return;
        settled.SettleKey = mutation.NextClaimKey();
        settled.SettleReceipt = MessagePackSerializer.Serialize(new FightSettleResponse
        {
            Settle = new()
            {
                IsWin = false, StageId = stageId, ChallengeCount = 1, LeftTime = 0
            }
        });
    }

    internal static bool TryLeaveFight(Session session, out LeaveFightResponse response, int packetId)
    {
        response = new();
        if (session.player.Theatre4.ActiveEncounter is null
            && session.player.Theatre4.PendingMutation is null
            && !session.player.Theatre4.RequestReceipts.ContainsKey(packetId)) return false;
        if (session.fight is { } active && !IsCombatStage(active.PreFight.PreFightData.StageId)) return false;
        try
        {
            response = Commit<LeaveFightRequest, LeaveFightResponse>(
                session, packetId, nameof(LeaveFightRequest), new LeaveFightRequest(), (mutation, _) =>
                {
                    AbandonEncounter(mutation);
                });
            session.fight = null;
        }
        catch (ServerCodeException exception) { response.Code = exception.Code; }
        catch (MongoException exception)
        {
            session.log.Error($"Theatre4 persistence failed: {exception.Message}");
            response.Code = 1;
        }
        catch (MessagePackSerializationException) { response.Code = 1; }
        return true;
    }

    internal static void RecoverCombatOnLogin(Session session)
    {
        // The frozen encounter, its PreFightPayload and any settle receipt are durable and are
        // replayed by PreFight/settle, so a relog must NOT abandon a live battle: the client
        // reconnects to the same frozen fight. Only a run-less leftover is dropped (PrepareLogin
        // also does this; kept here as the combat-owned fallback).
        if (session.player.Theatre4.ActiveEncounter is null
            || session.player.Theatre4.Data.AdventureData is not null) return;
        Mutation mutation = new(session);
        mutation.State.ActiveEncounter = null;
        Persist(mutation, string.Empty, string.Empty, null);
    }

    //endregion
}
