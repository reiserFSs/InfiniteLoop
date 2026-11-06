using System.Globalization;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.fuben.reform;

namespace AscNet.GameServer.Handlers;

/// <summary>
/// Dreamscape Disequilibrium (Reform Balance, 4.9; ReformCfg.GameType 2). Client contract: XReformAgency (login push
/// NotifyReformFubenActivity, FubenReformEnterRequest), XReform2ndManager.PreFight (PreFightData.ReformBalanceData),
/// XReformBalance*Control (the weight rules mirrored here), XReform2ndManager.ShowReward / XUiReformCombatSettleWin
/// (ReformFightResult). The client sends no balance-specific request: it submits the chosen weight (mass) ids with the
/// ordinary PreFightRequest, so the server re-validates the whole selection there (EN CodeText 20123050-20123079 are the
/// retail reasons) and derives the fight from it: FightData.EventIds (affix events, difficulty buff, unbalanced/perfect
/// events) and FightData.NpcGroupList (waves from the monsters' SpawnWave/SpawnCount).
///
/// AscNet policy (missing-retail policy, not recovered retail behavior): which ReformCfg row is open (the newest with a
/// positive OpenTimeId whose window is open), stage availability (previous chapter passed; hard needs every normal stage
/// passed, the client's own IsUnlockStageHard; IsSuperior is never granted), the monster Hp column applied as the Life
/// attribute of its StageNpc, empty waves skipped, and Score = FinalWishWeight in the settle result.
/// </summary>
internal static class ReformModule
{
    internal const int NotInTime = 20123001, StageNotActive = 20123002, StageConfigMissing = 20123003, StageNotInTime = 20123004,
        DifficultyNotActive = 20123008, TeamCountError = 20123048, StageDbNotFound = 20123050, StageCfgNotFound = 20123051,
        DataNotFound = 20123052, PreFightDataNotFound = 20123053, MassIdError = 20123054, MassCfgNotFound = 20123055,
        MassSideError = 20123056, MassRepeat = 20123057, SlotOverMax = 20123058, MassNotEnough = 20123059,
        MassCalcError = 20123060, MinQualityNotEnough = 20123061, MassTargetError = 20123062, AffixCfgNotFound = 20123064,
        EnemyTargetCfgNotFound = 20123065, EnemySpawnCfgError = 20123066, DifficultyBuffCfgNotFound = 20123067,
        DifficultyCfgCountNotMatch = 20123068, CharaGroupCfgNotFound = 20123069, EnemyGroupCfgNotFound = 20123070,
        AffixGroupCfgNotFound = 20123071, CharacterOutOfRange = 20123072, EnemyOutOfRange = 20123073,
        AffixOutOfRange = 20123074, FixedEnemyMissing = 20123075, RobotNotInRange = 20123076, CharacterRepeat = 20123077,
        TeamSelectNull = 20123078, TeamOutOfRange = 20123079, GetCharacterError = 20123046;

    // ReformBalanceMass.WeightType and ReformBalanceAffix.AffixType (XReformBalanceSelectControl / ConfigControl).
    private const int Character = 1, Monster = 2, MyAffix = 3, EnemyAffix = 4, AffixTypeMy = 1, AffixTypeEnemy = 2;
    private const int HardDifficulty = 2, DefaultTeamSize = 3, LifeAttribute = 1;
    // ReformBalanceBond.TriggerMode (XReformBalanceLogic.BondTriggerMode).
    private const int BondAll = 1, BondAny = 2;

    private static readonly Lazy<List<ReformCfgTable>> Activities = new(() => TableReaderV2.Parse<ReformCfgTable>());
    private static readonly Lazy<List<ReformChapterTable>> Chapters = new(() => TableReaderV2.Parse<ReformChapterTable>());
    private static readonly Lazy<Dictionary<int, ReformStageTable>> Stages = new(() => TableReaderV2.Parse<ReformStageTable>().ToDictionary(row => row.Id));
    private static readonly Lazy<Dictionary<int, ReformBalanceMassTable>> Masses = new(() => TableReaderV2.Parse<ReformBalanceMassTable>().ToDictionary(row => row.Id));
    private static readonly Lazy<Dictionary<int, ReformBalanceAffixTable>> Affixes = new(() => TableReaderV2.Parse<ReformBalanceAffixTable>().ToDictionary(row => row.Id));
    private static readonly Lazy<Dictionary<int, ReformBalanceAffixGroupTable>> AffixGroups = new(() => TableReaderV2.Parse<ReformBalanceAffixGroupTable>().ToDictionary(row => row.Id));
    private static readonly Lazy<Dictionary<int, ReformBalanceBondTable>> Bonds = new(() => TableReaderV2.Parse<ReformBalanceBondTable>().ToDictionary(row => row.Id));
    private static readonly Lazy<Dictionary<int, ReformBalanceCharacterTable>> Characters = new(() => TableReaderV2.Parse<ReformBalanceCharacterTable>().ToDictionary(row => row.CharacterId));
    private static readonly Lazy<Dictionary<int, ReformBalanceTargetGroupTable>> TargetGroups = new(() => TableReaderV2.Parse<ReformBalanceTargetGroupTable>().ToDictionary(row => row.Id));
    private static readonly Lazy<Dictionary<int, ReformUseCharaGroupTable>> CharaGroups = new(() => TableReaderV2.Parse<ReformUseCharaGroupTable>().ToDictionary(row => row.Id));
    private static readonly Lazy<Dictionary<int, ReformEnemyTargetTable>> Enemies = new(() => TableReaderV2.Parse<ReformEnemyTargetTable>().ToDictionary(row => row.Id));
    private static readonly Lazy<Dictionary<string, double>> Config = new(() => TableReaderV2.Parse<ReformBalanceConfigTable>()
        .ToDictionary(row => row.Key, row => Convert.ToDouble(row.Value, CultureInfo.InvariantCulture)));
    // The mode's stages: every chapter's [normal, hard] stage list.
    private static readonly Lazy<HashSet<int>> ChapterStageIds = new(() => Chapters.Value.SelectMany(chapter => chapter.ChapterStageId).ToHashSet());

    internal static bool IsBalanceStage(uint stageId) => stageId <= int.MaxValue && ChapterStageIds.Value.Contains((int)stageId)
        && Stages.Value.ContainsKey((int)stageId);

    /// <summary>Newest ReformCfg of the balance mode whose calendar is open (rows with OpenTimeId 0 are ended seasons).</summary>
    internal static ReformCfgTable? ActiveActivity(DateTimeOffset now) => Activities.Value
        .Where(row => row.OpenTimeId > 0 && ActivityScheduleService.IsOpen(row.OpenTimeId, now))
        .OrderByDescending(row => row.Id).FirstOrDefault();

    private static ReformState State(Player player) => player.Reform ??= new();

    private static ReformStageRecord? Record(Player player, int stageId) => State(player).Stages.FirstOrDefault(row => row.StageId == stageId);

    private static bool Passed(Player player, int stageId) => Record(player, stageId)?.Passed == true;

    /// <summary>XReformModel.IsChapterPassed: the hard stage or the first (normal) stage cleared.</summary>
    private static bool ChapterPassed(Player player, ReformChapterTable chapter) =>
        chapter.ChapterStageId.Any(id => Stages.Value.TryGetValue(id, out ReformStageTable? stage) && stage.Difficulty == HardDifficulty && Passed(player, id))
        || chapter.ChapterStageId.Count > 0 && Passed(player, chapter.ChapterStageId[0]);

    /// <summary>XReformModel.IsUnlockAllStageHard: every chapter's normal stage cleared.</summary>
    private static bool AllNormalPassed(Player player) => Chapters.Value.All(chapter => chapter.ChapterStageId.Count > 0 && Passed(player, chapter.ChapterStageId[0]));

    /// <summary>Task 61004: accumulated wish mass in hard mode = sum of each hard stage's best final wish mass (AscNet policy, matches the chapter quality display).</summary>
    internal static int HardWishTotal(Player player) => State(player).Stages
        .Where(row => Stages.Value.TryGetValue(row.StageId, out ReformStageTable? stage) && stage.Difficulty == HardDifficulty && ChapterStageIds.Value.Contains(row.StageId))
        .Sum(row => row.MaxWishQuality);

    internal static bool IsStagePassed(Player player, int stageId) => Passed(player, stageId);

    internal static ReformFubenDb? BuildDb(Player player, DateTimeOffset now)
    {
        if (ActiveActivity(now) is not { } activity)
            return null;
        return new ReformFubenDb
        {
            ActivityId = activity.Id,
            StageDbs = Chapters.Value.OrderBy(chapter => chapter.Order).SelectMany(chapter => chapter.ChapterStageId).Distinct().Select(stageId =>
            {
                ReformStageRecord? record = Record(player, stageId);
                return new ReformStageDb
                {
                    Id = stageId,
                    Pass = record?.Passed == true,
                    // SetData indexes DifficultyDbs[CurDiffIndex + 1] and reads Score/ExtraStar/EnemyReplaceIds.
                    DifficultyDbs = [new ReformDifficultyDb { Score = record?.MaxWishQuality ?? 0 }],
                    BalanceMaxWishQuality = record?.MaxWishQuality ?? 0
                };
            }).ToList()
        };
    }

    internal static NotifyReformFubenActivity? BuildLoginData(Player player, DateTimeOffset now) =>
        BuildDb(player, now) is { } db ? new NotifyReformFubenActivity { ReformFubenDb = db } : null;

    [RequestPacketHandler("FubenReformEnterRequest")]
    public static void Enter(Session session, Packet.Request packet)
    {
        ReformFubenDb? db = BuildDb(session.player, DateTimeOffset.UtcNow);
        session.SendResponse(new FubenReformEnterResponse { Code = db is null ? NotInTime : 0, ReformFubenDb = db }, packet.Id);
    }

    /// <summary>Availability of one stage for this player (activity open, stage window, chapter order, hard unlock).</summary>
    internal static int ValidateStage(Player player, int stageId, DateTimeOffset now)
    {
        if (ActiveActivity(now) is null)
            return NotInTime;
        if (!Stages.Value.TryGetValue(stageId, out ReformStageTable? stage))
            return StageCfgNotFound;
        ReformChapterTable? chapter = Chapters.Value.FirstOrDefault(row => row.ChapterStageId.Contains(stageId));
        if (chapter is null)
            return StageConfigMissing;
        if (stage.OpenTimeId > 0 && !ActivityScheduleService.IsOpen(stage.OpenTimeId, now))
            return StageNotInTime;
        if (chapter.Order > 0 && Chapters.Value.FirstOrDefault(row => row.Id == chapter.Order) is { } previous && !ChapterPassed(player, previous))
            return StageNotActive;
        if (stage.UnlockStageId > 0 && !Passed(player, stage.UnlockStageId))
            return StageNotActive;
        if (stage.Difficulty == HardDifficulty && !AllNormalPassed(player))
            return DifficultyNotActive;
        return 0;
    }

    internal sealed class Evaluation
    {
        public int Code { get; init; }
        public int WishTotal { get; set; }
        public int CostTotal { get; set; }
        public int Bonus { get; set; }
        public int FinalWish => WishTotal + Bonus;
        public HashSet<int> CharacterIds { get; } = [];
        public List<int> EventIds { get; } = [];
        public List<object> NpcGroups { get; } = [];
    }

    private static Evaluation Fail(int code) => new() { Code = code };

    private static int ConfigInt(string key) => (int)Config.Value.GetValueOrDefault(key);

    /// <summary>XReformBalanceLogic.ResolveMass: the highest satisfied UnlockStageIds index selects its MassValues entry.</summary>
    private static int ResolveMass(ReformBalanceMassTable mass, Player player)
    {
        int hit = -1;
        for (int index = 0; index < mass.UnlockStageIds.Count && index < mass.MassValues.Count; index++)
            if (Passed(player, mass.UnlockStageIds[index]))
                hit = index;
        return hit >= 0 ? mass.MassValues[hit] : mass.DefaultMass;
    }

    /// <summary>XReformBalanceLogic.JudgeBond over the stage's bonds; returns the affix ids the triggered bonds unlock.</summary>
    private static HashSet<int> UnlockedAffixes(ReformStageTable stage, HashSet<int> characterIds, HashSet<int> affixIds)
    {
        HashSet<int> unlocked = [];
        foreach (int bondId in stage.BalanceBondIds.Where(id => id > 0))
        {
            if (!Bonds.Value.TryGetValue(bondId, out ReformBalanceBondTable? bond))
                continue;
            bool hasCharacters = bond.RequiredCharacterIds.Count > 0, hasAffixes = bond.RequiredAffixIds.Count > 0;
            if (!hasCharacters && !hasAffixes)
                continue;
            bool triggered = bond.TriggerMode switch
            {
                BondAll => bond.RequiredCharacterIds.All(characterIds.Contains) && bond.RequiredAffixIds.All(affixIds.Contains),
                BondAny => bond.RequiredCharacterIds.Any(characterIds.Contains) || bond.RequiredAffixIds.Any(affixIds.Contains),
                _ => false
            };
            if (triggered && bond.UnlockedAffixIds is int unlock && unlock > 0)
                unlocked.Add(unlock);
        }
        return unlocked;
    }

    /// <summary>
    /// Validates a submitted selection against the stage and derives the fight. Order of the checks follows the retail
    /// reasons: config, data, ids, sides, repeats, slots, ranges, fixed monsters, counts, quality.
    /// </summary>
    internal static Evaluation Evaluate(Player player, int stageId, ReformBalanceData? data)
    {
        if (!Stages.Value.TryGetValue(stageId, out ReformStageTable? stage))
            return Fail(StageCfgNotFound);
        if (data?.WishMassIds is null && data?.CostMassIds is null)
            return Fail(DataNotFound);
        List<int> wish = data!.WishMassIds ?? [], cost = data.CostMassIds ?? [];
        if (wish.Count > stage.WishWeightSlotNum || cost.Count > stage.CostWeightSlotNum)
            return Fail(SlotOverMax);
        if (wish.Concat(cost).GroupBy(id => id).Any(group => group.Count() > 1))
            return Fail(MassRepeat);

        List<ReformBalanceMassTable> wishMasses = [], costMasses = [];
        foreach ((List<int> ids, List<ReformBalanceMassTable> rows, bool wishSide) in new[] { (wish, wishMasses, true), (cost, costMasses, false) })
            foreach (int id in ids)
            {
                if (!Masses.Value.TryGetValue(id, out ReformBalanceMassTable? mass))
                    return Fail(MassCfgNotFound);
                if (mass.WeightType is < Character or > EnemyAffix)
                    return Fail(MassTargetError);
                if ((mass.WeightType is Character or MyAffix) != wishSide)
                    return Fail(MassSideError);
                if (mass.Difficulty != stage.Difficulty)
                    return Fail(MassIdError);
                if (mass.DefaultMass > Config.Value.GetValueOrDefault("MaxSingleWeightMass", int.MaxValue)
                    || mass.MassValues.Any(value => value > Config.Value.GetValueOrDefault("MaxSingleWeightMass", int.MaxValue)))
                    return Fail(MassCalcError);
                rows.Add(mass);
            }

        Evaluation result = new() { Code = 0 };
        // Characters: row in ReformBalanceCharacter and in the stage's UseCharaGroup.
        if (wishMasses.Any(mass => mass.WeightType == Character))
        {
            if (!CharaGroups.Value.TryGetValue(stage.UseCharaGroup, out ReformUseCharaGroupTable? charaGroup))
                return Fail(CharaGroupCfgNotFound);
            foreach (ReformBalanceMassTable mass in wishMasses.Where(row => row.WeightType == Character))
            {
                if (!Characters.Value.ContainsKey(mass.TargetId))
                    return Fail(MassTargetError);
                if (!charaGroup.CharacterIds.Contains(mass.TargetId))
                    return Fail(CharacterOutOfRange);
                result.CharacterIds.Add(mass.TargetId);
            }
        }
        // Monsters: the stage's ReformBalanceTargetGroup plus its fixed monster.
        int? fixedEnemy = stage.FixedEnemyIds is > 0 ? stage.FixedEnemyIds : null;
        List<ReformBalanceMassTable> monsters = costMasses.Where(row => row.WeightType == Monster).ToList();
        if (monsters.Count > 0)
        {
            ReformBalanceTargetGroupTable? targetGroup = null;
            if (stage.EnemyGroupId > 0 && !TargetGroups.Value.TryGetValue(stage.EnemyGroupId, out targetGroup))
                return Fail(EnemyGroupCfgNotFound);
            foreach (ReformBalanceMassTable mass in monsters)
            {
                if (!Enemies.Value.ContainsKey(mass.TargetId))
                    return Fail(EnemyTargetCfgNotFound);
                if (!(targetGroup?.TargetIds.Contains(mass.TargetId) == true || mass.TargetId == fixedEnemy))
                    return Fail(EnemyOutOfRange);
            }
        }
        if (fixedEnemy is int fixedId && !monsters.Any(row => row.TargetId == fixedId))
            return Fail(FixedEnemyMissing);
        // Affixes: the stage's group, or an affix a triggered bond unlocks (bonds can chain through affixes).
        List<ReformBalanceMassTable> affixMasses = wishMasses.Concat(costMasses).Where(row => row.WeightType is MyAffix or EnemyAffix).ToList();
        if (affixMasses.Count > 0)
        {
            foreach (ReformBalanceMassTable mass in affixMasses)
            {
                if (!Affixes.Value.TryGetValue(mass.TargetId, out ReformBalanceAffixTable? affix))
                    return Fail(AffixCfgNotFound);
                if (affix.AffixType != (mass.WeightType == MyAffix ? AffixTypeMy : AffixTypeEnemy))
                    return Fail(AffixOutOfRange);
            }
            HashSet<int> valid = [];
            if (stage.AffixGroupId > 0)
            {
                if (!AffixGroups.Value.TryGetValue(stage.AffixGroupId, out ReformBalanceAffixGroupTable? affixGroup))
                    return Fail(AffixGroupCfgNotFound);
                valid.UnionWith(affixGroup.AffixIds);
            }
            HashSet<int> chosenMine = affixMasses.Where(row => row.WeightType == MyAffix).Select(row => row.TargetId).ToHashSet();
            for (int round = 0; round <= stage.BalanceBondIds.Count + 1; round++)
            {
                int before = valid.Count;
                valid.UnionWith(UnlockedAffixes(stage, result.CharacterIds, chosenMine.Where(valid.Contains).ToHashSet()));
                if (valid.Count == before)
                    break;
            }
            if (affixMasses.Any(row => !valid.Contains(row.TargetId)))
                return Fail(AffixOutOfRange);
        }

        int maxCharacters = stage.PlayerAmountLimitDefault > 0 ? stage.PlayerAmountLimitDefault : DefaultTeamSize;
        if (result.CharacterIds.Count > maxCharacters)
            return Fail(TeamCountError);
        if (result.CharacterIds.Count < ConfigInt("MinSelectCharacterCount") || monsters.Count < ConfigInt("MinSelectEnemyCount"))
            return Fail(MassNotEnough);

        result.WishTotal = wishMasses.Sum(mass => ResolveMass(mass, player));
        result.CostTotal = costMasses.Sum(mass => ResolveMass(mass, player));
        if (result.WishTotal < stage.MinQuality)
            return Fail(MinQualityNotEnough);

        // XReformBalanceLogic.JudgeBalance: |wish - cost| / wish against NormalBalanceMassDifferenceRate percent.
        bool perfect = result.WishTotal > 0 && result.WishTotal == result.CostTotal;
        bool balanced = result.WishTotal > 0
            && (double)Math.Abs(result.WishTotal - result.CostTotal) / result.WishTotal <= Config.Value.GetValueOrDefault("NormalBalanceMassDifferenceRate") / 100;
        if (!balanced && stage.UnbalancedFightEventId > 0)
            result.EventIds.Add(stage.UnbalancedFightEventId);
        // XReformBalanceControl.GetPerfectBonusMass: hard stages turn a perfect balance into extra wish mass, normal stages into a fight event.
        if (perfect && stage.Difficulty == HardDifficulty)
        {
            double rate = Config.Value.GetValueOrDefault("PerfectBalanceMassBonusRate") / 100;
            result.Bonus = (int)Math.Floor(result.WishTotal * (1 + rate) - result.WishTotal);
        }
        else if (perfect && stage.PerfectBalanceFightEventId > 0)
            result.EventIds.Add(stage.PerfectBalanceFightEventId);

        // XReformBalanceLogic.JudgeDifficulty: the highest satisfied threshold (1-based, capped at 3) picks the buff.
        if (stage.DifficultyMassThresholds.Count > 0)
        {
            if (stage.DifficultyMassThresholds.Count != stage.DifficultyBuffIds.Count)
                return Fail(DifficultyCfgCountNotMatch);
            int tier = stage.DifficultyMassThresholds.FindLastIndex(threshold => result.CostTotal >= threshold);
            int buffId = stage.DifficultyBuffIds[Math.Max(0, Math.Min(tier, 2))];
            if (buffId <= 0)
                return Fail(DifficultyBuffCfgNotFound);
            result.EventIds.Add(buffId);
        }
        foreach (ReformBalanceMassTable mass in affixMasses)
            if (Affixes.Value[mass.TargetId].FightEventId is > 0 and int eventId)
                result.EventIds.Add(eventId);

        // Waves: ReformEnemyTarget.SpawnWave/SpawnCount of every selected monster (the fixed one included).
        int maxWaves = ConfigInt("MaxWaveCount");
        List<IGrouping<int, ReformEnemyTargetTable>> waves = monsters.Select(row => Enemies.Value[row.TargetId])
            .GroupBy(enemy => enemy.SpawnWave).OrderBy(group => group.Key).ToList();
        if (waves.Any(group => group.Key <= 0 || group.Any(enemy => enemy.SpawnCount <= 0)) || waves.Count > maxWaves)
            return Fail(EnemySpawnCfgError);
        foreach (IGrouping<int, ReformEnemyTargetTable> wave in waves)
            result.NpcGroups.Add(new
            {
                NpcList = wave.SelectMany(enemy => Enumerable.Range(0, enemy.SpawnCount).Select(_ => (object)new
                {
                    NpcId = enemy.NpcId,
                    Level = enemy.Level,
                    BufferIds = enemy.BuffIds.Where(id => id > 0).ToList(),
                    MagicInfos = new List<object>(),
                    AttrTable = enemy.Hp > 0
                        ? new Dictionary<int, object> { [LifeAttribute] = new { Value = enemy.Hp, MaxValue = enemy.Hp } }
                        : new Dictionary<int, object>()
                })).ToList()
            });
        result.EventIds.RemoveAll(id => id <= 0);
        return result;
    }

    /// <summary>The lineup must be built from the selected character weights (retail 20123076-20123079).</summary>
    private static int ValidateLineup(Session session, PreFightRequest.PreFightRequestPreFightData data, HashSet<int> selectedCharacters)
    {
        List<int> deployed = [];
        foreach (uint cardId in (data.CardIds ?? []).Where(id => id > 0))
        {
            if (session.character.Characters.All(row => row.Id != cardId))
                return GetCharacterError;
            deployed.Add((int)cardId);
        }
        foreach (int robotId in (data.RobotIds ?? []).Where(id => id > 0))
        {
            ReformBalanceCharacterTable? row = Characters.Value.Values.FirstOrDefault(character => character.RobotId == robotId);
            if (row is null)
                return RobotNotInRange;
            deployed.Add(row.CharacterId);
        }
        if (deployed.Count == 0)
            return TeamSelectNull;
        if (deployed.Distinct().Count() != deployed.Count)
            return CharacterRepeat;
        return deployed.All(selectedCharacters.Contains) ? 0 : TeamOutOfRange;
    }

    /// <summary>PreFight hook: false when the stage is not a balance stage; otherwise validates and fills the fight data.</summary>
    internal static bool TryApplyPreFight(Session session, PreFightRequest.PreFightRequestPreFightData data, PreFightResponse.PreFightResponseFightData fightData, out int code)
    {
        code = 0;
        if (!IsBalanceStage(data.StageId))
            return false;
        int stageId = (int)data.StageId;
        code = ValidateStage(session.player, stageId, DateTimeOffset.UtcNow);
        if (code != 0)
            return true;
        if (data.ChallengeCount != 1 || data.SpeedrunStageId != 0)
        {
            code = PreFightDataNotFound;
            return true;
        }
        Evaluation evaluation = Evaluate(session.player, stageId, data.ReformBalanceData);
        code = evaluation.Code != 0 ? evaluation.Code : ValidateLineup(session, data, evaluation.CharacterIds);
        if (code != 0)
            return true;
        fightData.EventIds.AddRange(evaluation.EventIds.Where(id => !fightData.EventIds.Contains(id)).Select(id => (dynamic)id));
        fightData.NpcGroupList = evaluation.NpcGroups;
        return true;
    }

    /// <summary>
    /// Settle gate, before any mutation: the fight must be this stage's accepted pre-fight and its stored selection must
    /// still evaluate (retail 20123053 when the pre-battle data is gone).
    /// </summary>
    internal static int ValidateSettle(Session session, FightSettleResult result)
    {
        if (!IsBalanceStage(result.StageId) || !result.IsWin || result.IsForceExit)
            return 0;
        PreFightRequest.PreFightRequestPreFightData? data = session.fight?.PreFight.PreFightData;
        if (data is null || data.StageId != result.StageId || data.ReformBalanceData is null)
            return PreFightDataNotFound;
        return Evaluate(session.player, (int)result.StageId, data.ReformBalanceData).Code;
    }

    /// <summary>Persists a won balance fight (pass + best wish mass) and builds the settle's ReformFightResult.</summary>
    internal static ReformFightResult? RecordWin(Session session, FightSettleResult result)
    {
        if (!IsBalanceStage(result.StageId) || !result.IsWin || result.IsForceExit)
            return null;
        Evaluation evaluation = Evaluate(session.player, (int)result.StageId, session.fight!.PreFight.PreFightData.ReformBalanceData);
        ReformState state = State(session.player);
        ReformStageRecord record = Record(session.player, (int)result.StageId) ?? new ReformStageRecord { StageId = (int)result.StageId };
        if (!state.Stages.Contains(record))
            state.Stages.Add(record);
        record.Passed = true;
        record.MaxWishQuality = Math.Max(record.MaxWishQuality, evaluation.FinalWish);
        session.player.Save();
        TaskModule.SendReformTaskSync(session);
        return new ReformFightResult
        {
            Score = evaluation.FinalWish,
            FinalWishWeight = evaluation.FinalWish,
            BonusWishWeight = evaluation.Bonus > 0 ? evaluation.Bonus : null
        };
    }
}
