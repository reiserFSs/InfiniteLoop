using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.condition;
using AscNet.Table.V2.share.fuben;

namespace AscNet.GameServer.Handlers;

// Arcade Anima (CharacterTower) fight and story-progression integration.
//
// The mode owns four narrow decisions inside the shared pipeline and nothing else, so ordinary
// stage/task/guide/settlement behaviour stays shared:
//   * pre-flight gates          -> ValidatePreFight (called by FightModule before deployment)
//   * accepted fight data       -> ApplyPreFight (activated relation fight events, authored restartability)
//   * settlement star payload   -> TryReadSettleStars, plus the reward suppression in FightModule
//   * story-card completion     -> TryEnterStory
internal static partial class TowerModule
{
    // Same value as FightModule.FightAuthorizationError and BiancaTheatreModule.CombatAuthorizationError:
    // a request that contradicts the authored stage contract must not rewrite mode state.
    internal const int CombatAuthorizationError = 1033;

    // Native XFightResult.AddStars is a bit set indexed from star 1. GameAssembly.dll.orig
    // XFightResult.AddStar (RVA 0x1C1F710) and RemoveStar (RVA 0x1C21470) both do bts/btr on
    // XFightResultData.AddStars (+0x34) with bit index (index-1)&31, so bit 0 is star 1. Every Arcade
    // stage row authors at most three star descriptions, so only the low three bits are a real payload.
    private const int StageStarMask = 0b111;

    // Arcade stages never author a team requirement beyond the forced-character condition, so the
    // accepted lineup gates are evaluated here, before the generic deployment machinery drops unowned ids.
    internal static int ValidatePreFight(Session session, PreFightRequest.PreFightRequestPreFightData data)
    {
        // Settlement applies the quick-clear target in place of the fought stage, so an Arcade target has to be
        // refused whichever stage the request fights. No Arcade row is a speedrun target (the client's
        // StageSpeedrun table lists none) and every other mode guards the field the same way.
        if (data.SpeedrunStageId != 0 && OwnsStage(data.SpeedrunStageId))
            return CombatAuthorizationError;

        if (!OwnsStage(data.StageId))
            return 0; // Not an Arcade stage: the generic pipeline keeps full authority.

        if (!IsCombatStage(data.StageId))
            return StageInfoError; // Story-only card: combat must not start for it.

        int access = RequireStageAccess(session, data.StageId);
        if (access != 0)
            return access;

        // No Arcade stage row authors MaxChallengeNums/BuyChallengeCount/AutoFightId or a speedrun target,
        // so a request carrying a multi-challenge count or a stage redirect is not this mode's contract.
        if (data.ChallengeCount != 1 || data.SpeedrunStageId != 0)
            return CombatAuthorizationError;

        if (StageRow(data.StageId) is not { } stage)
            return StageInfoError;

        // Fixed-robot story stages are fought with the authored Stage.RobotId rows. The client already
        // uploads no team for them; dropping a forged lineup keeps the authored frames authoritative.
        List<int> robotIds = stage.RobotId.Where(robotId => robotId > 0).ToList();
        if (robotIds.Count > 0)
        {
            data.CardIds = [];
            data.RobotIds = robotIds;
            return 0;
        }

        return MeetsForceCondition(session, data.CardIds, stage.ForceConditionId)
            ? 0
            : CombatAuthorizationError;
    }

    internal static void ApplyPreFight(
        Session session,
        PreFightRequest.PreFightRequestPreFightData data,
        PreFightResponse.PreFightResponseFightData fightData)
    {
        if (!OwnsStage(data.StageId))
            return;

        // An activated relation tier unlocks its authored FightEvent (BornMagic/EnemyBornMagic resolved
        // natively from the fight data). The mode only decides which tiers are durably activated; it never
        // authors effect levels or replacement proc logic. Story-chapter stages have no relation group.
        foreach (int fightEventId in GetActivatedFightEvents(session, data.StageId))
        {
            if (fightEventId <= 0)
                continue;
            bool alreadyPresent = false;
            foreach (dynamic existing in fightData.EventIds)
            {
                if ((int)existing != fightEventId)
                    continue;
                alreadyPresent = true;
                break;
            }
            if (!alreadyPresent)
                fightData.EventIds.Add(fightEventId);
        }

        // MonsterLevel is deliberately not touched. The generic resolver already projects only the authored
        // StageLevelControl rows, so the six combat stages without a row (Vera 21000260-264/21000271) send an
        // empty list instead of a designed level. Native DoGetFightMonsterLevel bounds-checks its 1-based
        // index (Count < index -> skip) and then reports 0, so those stages take the authored graph fallback;
        // verified in the matched GameAssembly.dll.orig at RVA 0x168A210. No server-side level is invented.
        //
        // Restartable is authored per stage (all thirty challenge stages, no story-chapter row). The
        // in-fight restart button reads this flag from the fight data; FightRestartRequest keeps its
        // generic fight-identity check, so the mode adds no separate restart state.
        fightData.Restartable = Convert.ToInt32(StageRow(data.StageId)?.Restartable) != 0;
    }

    // Called only for an authenticated, accepted win of an Arcade stage. An impossible payload is refused
    // so a malformed client result cannot rewrite durable star progress.
    internal static bool TryReadSettleStars(FightSettleResult result, out long starsMark)
    {
        starsMark = 0;
        if (result.AddStars < 0 || (result.AddStars & ~StageStarMask) != 0)
            return false;
        starsMark = result.AddStars;
        return true;
    }

    internal static bool TryEnterStory(Session session, EnterStoryRequest request, out EnterStoryResponse response)
    {
        response = new EnterStoryResponse();
        if (request.StageId <= 0 || !OwnsStage((uint)request.StageId))
            return false; // Not an Arcade stage: the generic story handler keeps full authority.

        // Challenge chapters stay combat even when the authored row carries the story visual type
        // (21000264), so only movie-only story-chapter rows may complete through EnterStoryRequest.
        if (!IsStoryStage((uint)request.StageId))
        {
            response.Code = StageInfoError;
            return true;
        }

        int access = RequireStageAccess(session, (uint)request.StageId);
        if (access != 0)
        {
            response.Code = access;
            return true;
        }

        session.stage.Stages.TryGetValue(request.StageId, out StageDatum? existing);
        if (existing is { Passed: true })
        {
            session.SendPush(new NotifyStageData { StageList = [existing] });
            return true;
        }

        StageDatum stageData = new()
        {
            StageId = request.StageId,
            Passed = true,
            // Story cards carry no stars, but XStageInfo.GetStars reads the numeric wire value whenever a
            // stage datum exists, so the field stays an explicit number.
            StarsMark = 0,
            CreateTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            BestCardIds = [],
            LastCardIds = []
        };
        session.stage.AddStage(stageData);
        try
        {
            session.stage.SaveChecked();
        }
        catch
        {
            // Same invariant as the fight path: a story card that was not stored must not read as passed, so a
            // failed write restores the previous datum (or removes the new entry) before the request aborts.
            if (existing is null)
                session.stage.Stages.Remove(request.StageId);
            else
                session.stage.AddStage(existing);
            throw;
        }
        TaskModule.RecordStageClear(session, request.StageId, 1, 0, true);
        session.SendPush(new NotifyStageData { StageList = [stageData] });
        return true;
    }

    // Stage ForceConditionId rows are Type 18103: Params[0] is the required match count and the remaining
    // parameters are character ids, checked against the lineup, matching XConditionManager[18103]. The id
    // must also be owned, because an unowned id is dropped during deployment and would otherwise satisfy
    // the gate here while the fielded team does not.
    private static bool MeetsForceCondition(Session session, List<uint>? cardIds, List<int> forceConditionIds)
    {
        foreach (int conditionId in forceConditionIds.Where(id => id > 0))
        {
            ConditionTable? condition = TableReaderV2.Parse<ConditionTable>()
                .FirstOrDefault(row => row.Id == conditionId);
            if (condition is null || condition.Type != 18103 || condition.Params.Count < 2)
                continue;

            int required = Math.Max(1, condition.Params[0]);
            int matched = 0;
            foreach (int characterId in condition.Params.Skip(1).Where(id => id > 0))
            {
                if (cardIds?.Contains((uint)characterId) != true || !OwnsCharacter(session, characterId))
                    continue;
                if (++matched >= required)
                    return true;
            }

            return false; // An authored requirement exists and the accepted lineup does not meet it.
        }

        return true;
    }

    private static bool OwnsCharacter(Session session, int characterId) =>
        session.character.Characters.Any(character => character.Id == (uint)characterId);

    private static StageTable? StageRow(uint stageId) =>
        TableReaderV2.Parse<StageTable>().FirstOrDefault(row => row.StageId == stageId);
}
