using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.condition;
using AscNet.Table.V2.share.exhibition;
using AscNet.Table.V2.share.fuben;
using AscNet.Table.V2.share.fuben.charactertower;
using AscNet.Table.V2.share.reward;
using MessagePack;

namespace AscNet.GameServer.Handlers
{
    #region MsgPackScheme
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    [MessagePackObject(true)]
    public class CharacterTowerGetChapterRewardRequest
    {
        public int ChapterId;
    }

    [MessagePackObject(true)]
    public class CharacterTowerGetChapterRewardResponse
    {
        public int Code;
        public List<RewardGoods> Rewards { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class CharacterTowerGetStageRewardRequest
    {
        public int StageId;
    }

    [MessagePackObject(true)]
    public class CharacterTowerGetStageRewardResponse
    {
        public int Code;
        public List<RewardGoods> Rewards { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class CharacterTowerGetStarRewardRequest
    {
        public int TreasureId;
    }

    [MessagePackObject(true)]
    public class CharacterTowerGetStarRewardResponse
    {
        public int Code;
        public List<RewardGoods> Rewards { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class CharacterTowerActivateFightEventIdRequest
    {
        public int RelationId;
        public int FightEventId;
    }

    [MessagePackObject(true)]
    public class CharacterTowerActivateFightEventIdResponse
    {
        public int Code;
        public List<int> FinishConditions { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class CharacterTowerSaveVideoStageIdRequest
    {
        public int StageId;
    }

    [MessagePackObject(true)]
    public class CharacterTowerSaveVideoStageIdResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class CharacterTowerSaveStoryIdRequest
    {
        public int RelationId;
        public string StoryId;
    }

    [MessagePackObject(true)]
    public class CharacterTowerSaveStoryIdResponse
    {
        public int Code;
        public List<int> FinishConditions { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class CharacterTowerSaveTriggerConditionIdRequest
    {
        public int ConditionId;
        public int ChapterId;
    }

    [MessagePackObject(true)]
    public class CharacterTowerSaveTriggerConditionIdResponse
    {
        public int Code;
    }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    #endregion

    /// <summary>
    /// Arcade Anima (CharacterTower, FunctionalOpen 10436) mode state and its seven requests
    /// (lua/matrix/xmanager/XFubenCharacterTowerManager.lua). Ownership, chapter gates, stage rewards, star
    /// treasures and relation tiers are all table-owned by share/fuben/charactertower/*. Stage.Stages stays the
    /// only Passed/StarsMark authority and every payout is a manual claim; combat integration lives in
    /// TowerModule.Combat.cs.
    /// </summary>
    /// <remarks>
    /// Claim durability: a reward is granted through RewardHandler.ApplyRewardsOnceAndPersist under a stable
    /// per-player key, so the receipt in Inventory/Character is written before the mode marker is saved. If the
    /// marker save fails the in-memory edit is rolled back (see SaveClaim) and the retried request re-runs the
    /// same already-receipted claim, which pays once and finally commits the marker.
    /// </remarks>
    internal static partial class TowerModule
    {
        // Error codes: EN client bytes/share/text/CodeText.json keys 20178001..20178010 (CharacterTower*).
        internal const int ConfigNotFound = 20178001;
        internal const int RelationConditionNotEnough = 20178002;
        internal const int StageInfoError = 20178003;
        internal const int StageRewardHadGot = 20178004;
        internal const int StageNotPass = 20178005;
        internal const int StarRewardHadGot = 20178006;
        internal const int StarNotEnough = 20178007;
        internal const int ChapterRewardHadGot = 20178008;
        internal const int FinishStageNotEnough = 20178009;
        internal const int TriggerConditionExist = 20178010;

        private const int StoryChapterType = 1;
        private const int ChallengeChapterType = 2;

        /// <summary>StoryIds sentinel for "this tier has no story" (client normalizes the same string).</summary>
        private const string NoStoryId = "-1";

        private static readonly Dictionary<int, CharacterTowerTable> TowersById =
            TableReaderV2.Parse<CharacterTowerTable>().ToDictionary(row => row.Id);
        private static readonly Dictionary<int, CharacterTowerChapterTable> ChaptersById =
            TableReaderV2.Parse<CharacterTowerChapterTable>().ToDictionary(row => row.Id);
        private static readonly Dictionary<int, CharacterTowerRelationTable> RelationsById =
            TableReaderV2.Parse<CharacterTowerRelationTable>().ToDictionary(row => row.Id);
        private static readonly Dictionary<int, CharacterTowerTreasureTable> TreasuresById =
            TableReaderV2.Parse<CharacterTowerTreasureTable>().ToDictionary(row => row.TreasureId);
        private static readonly Dictionary<int, StageTable> StagesById =
            TableReaderV2.Parse<StageTable>().ToDictionary(row => row.StageId);
        private static readonly Dictionary<int, ConditionTable> ConditionsById =
            TableReaderV2.Parse<ConditionTable>().ToDictionary(row => row.Id);
        private static readonly Dictionary<int, int> ChapterIdByStageId = BuildChapterIdByStageId();
        private static readonly Dictionary<int, int> ChapterIdByRelationGroupId = BuildChapterIdByRelationGroupId();
        private static readonly Dictionary<int, int> ChapterIdByTreasureId = BuildChapterIdByTreasureId();
        private static readonly Dictionary<int, int> TowerIdByChapterId = BuildTowerIdByChapterId();

        private static Dictionary<int, int> BuildChapterIdByStageId()
        {
            Dictionary<int, int> map = new();
            foreach (CharacterTowerChapterTable chapter in ChaptersById.Values)
                foreach (int stageId in chapter.StageIds)
                    if (stageId > 0)
                        map.TryAdd(stageId, chapter.Id);
            return map;
        }

        private static Dictionary<int, int> BuildChapterIdByRelationGroupId()
        {
            Dictionary<int, int> map = new();
            foreach (CharacterTowerChapterTable chapter in ChaptersById.Values)
                if (chapter.RelationGroupId is > 0)
                    map.TryAdd(chapter.RelationGroupId.Value, chapter.Id);
            return map;
        }

        private static Dictionary<int, int> BuildChapterIdByTreasureId()
        {
            Dictionary<int, int> map = new();
            foreach (CharacterTowerChapterTable chapter in ChaptersById.Values)
                foreach (int treasureId in chapter.TreasureId)
                    if (treasureId > 0)
                        map.TryAdd(treasureId, chapter.Id);
            return map;
        }

        private static Dictionary<int, int> BuildTowerIdByChapterId()
        {
            Dictionary<int, int> map = new();
            foreach (CharacterTowerTable tower in TowersById.Values)
                foreach (int chapterId in tower.ChapterIds)
                    if (chapterId > 0)
                        map.TryAdd(chapterId, tower.Id);
            return map;
        }

        #region Ownership and access gates

        internal static bool TryGetChapterId(uint stageId, out int chapterId) =>
            ChapterIdByStageId.TryGetValue((int)stageId, out chapterId);

        internal static bool OwnsStage(uint stageId) => ChapterIdByStageId.ContainsKey((int)stageId);

        /// <summary>
        /// Challenge chapters always fight, including 21000264 (StageType 2 in the EN table, still a fight stage);
        /// a story chapter fights only on StageType 1, its other rows being story/movie rows.
        /// </summary>
        internal static bool IsCombatStage(uint stageId) =>
            TryGetChapterId(stageId, out int chapterId)
            && ChaptersById.TryGetValue(chapterId, out CharacterTowerChapterTable? chapter)
            && (chapter.Type == ChallengeChapterType
                || (StagesById.TryGetValue((int)stageId, out StageTable? stage) && stage.StageType is 1));

        internal static bool IsStoryStage(uint stageId) =>
            TryGetChapterId(stageId, out int chapterId)
            && ChaptersById.TryGetValue(chapterId, out CharacterTowerChapterTable? chapter)
            && chapter.Type == StoryChapterType
            && StagesById.TryGetValue((int)stageId, out StageTable? stage)
            && stage.StageType is 2 or 3;

        /// <summary>0 when the stage is reachable, otherwise the mode error code to answer with.</summary>
        internal static int RequireStageAccess(Session session, uint stageId)
        {
            if (!TryGetChapterId(stageId, out int chapterId)
                || !ChaptersById.TryGetValue(chapterId, out CharacterTowerChapterTable? chapter)
                || !IsChapterOpen(session, chapter))
                return StageInfoError;

            if (!StagesById.TryGetValue((int)stageId, out StageTable? stage))
                return StageInfoError;

            if (stage.RequireLevel is int requireLevel
                && requireLevel > 0
                && session.player.PlayerData.Level < requireLevel)
                return StageInfoError;

            return stage.PreStageId.Where(preStageId => preStageId > 0)
                      .All(preStageId => IsStagePassed(session, preStageId))
                ? 0
                : StageNotPass;
        }

        /// <summary>
        /// Tower window (unbounded permanent rows derived from Tower.OpenTimeId), then the chapter condition:
        /// activity conditions while the chapter is inside its authored activity window, open conditions
        /// (authored level/story gates) otherwise.
        /// </summary>
        private static bool IsChapterOpen(Session session, CharacterTowerChapterTable chapter)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (!IsTowerUnlocked(session, chapter, now))
                return false;

            List<int> conditionIds = IsChapterInActivity(chapter, now)
                ? chapter.ActivityCondition
                : chapter.OpenCondition;
            return conditionIds.Where(conditionId => conditionId > 0)
                .All(conditionId => IsConditionSatisfied(session, conditionId, chapter.CharacterId));
        }

        private static bool IsTowerUnlocked(Session session, CharacterTowerChapterTable chapter, DateTimeOffset now)
        {
            if (!TowerIdByChapterId.TryGetValue(chapter.Id, out int towerId)
                || !TowersById.TryGetValue(towerId, out CharacterTowerTable? tower))
                return false;

            if (!ActivityScheduleService.IsOpen(tower.OpenTimeId, now))
                return false;

            // Any chapter of the tower inside its activity window bypasses the tower condition (client IsUnlock).
            if (tower.ChapterIds.Where(chapterId => chapterId > 0).Any(chapterId =>
                    ChaptersById.TryGetValue(chapterId, out CharacterTowerChapterTable? sibling)
                    && IsChapterInActivity(sibling, now)))
                return true;

            return tower.ConditionId <= 0
                || IsConditionSatisfied(session, tower.ConditionId, chapter.CharacterId);
        }

        /// <summary>
        /// Chapter activity needs a dated, authored promotion window: unbounded (0/0) rows are the local
        /// permanent-availability policy, including every row derived from Tower.OpenTimeId, and must never
        /// project as a promotion. Vera chapters 100611/100621 author TimeId 34301, the same id as tower 1006's
        /// opening window, so a permanent row there would otherwise make them permanently "in activity".
        /// </summary>
        private static bool IsChapterInActivity(CharacterTowerChapterTable chapter, DateTimeOffset now) =>
            chapter.TimeId is > 0
            && ActivityScheduleService.TryGet(chapter.TimeId.Value, out ActivityScheduleEntry schedule)
            && (schedule.StartTime != 0 || schedule.EndTime != 0)
            && schedule.IsOpen(now);

        /// <summary>
        /// Chapters inside an authored promotion window. Empty until a chapter window has its own dated schedule
        /// row, which is why the chapter gates currently resolve through the authored open conditions.
        /// </summary>
        private static List<int> ActiveChapterIds(DateTimeOffset now) =>
            ChaptersById.Values
                .Where(chapter => IsChapterInActivity(chapter, now))
                .Select(chapter => chapter.Id)
                .OrderBy(chapterId => chapterId)
                .ToList();

        private static bool IsStagePassed(Session session, int stageId) =>
            session.stage.Stages.TryGetValue(stageId, out StageDatum? stage) && stage.Passed;

        #endregion

        #region Conditions

        /// <summary>
        /// Table-driven evaluation of the condition families the mode uses. <paramref name="contextualCharacterId"/>
        /// is the chapter's character, which the client passes to XConditionManager for character-scoped leaves.
        /// </summary>
        internal static bool IsConditionSatisfied(Session session, int conditionId, int contextualCharacterId)
        {
            if (conditionId <= 0 || !ConditionsById.TryGetValue(conditionId, out ConditionTable? condition))
                return false;

            List<int> parameters = condition.Params;
            switch (condition.Type)
            {
                case 10101:
                    return parameters.Count > 0
                        && parameters.All(level => session.player.PlayerData.Level >= level);

                case 10102:
                    return parameters.Any(characterId => characterId > 0)
                        && parameters.Where(characterId => characterId > 0).All(characterId =>
                            session.character.Characters.Any(character => character.Id == (uint)characterId));

                case 10105:
                    return parameters.Any(stageId => stageId > 0)
                        && parameters.Where(stageId => stageId > 0).All(stageId => IsStagePassed(session, stageId));

                case 13103:
                    return ContextCharacter(session, contextualCharacterId) is CharacterData levelCharacter
                        && parameters.Count > 0
                        && levelCharacter.Level >= parameters[0];

                case 13117:
                    return ContextCharacter(session, contextualCharacterId) is CharacterData trustCharacter
                        && parameters.Count > 0
                        && trustCharacter.TrustLv >= parameters[0];

                // Exhibition grow-up level: claimed exhibition reward levels for that character, not LiberateLv.
                case 13114:
                    return ContextCharacter(session, contextualCharacterId) is CharacterData exhibitionCharacter
                        && parameters.Count > 0
                        && TableReaderV2.Parse<ExhibitionRewardTable>().Any(reward =>
                            reward.CharacterId == exhibitionCharacter.Id
                            && reward.LevelId >= parameters[0]
                            && session.player.GatherRewards.Contains(reward.Id));

                // Battle power from current state, never the uploaded CharacterData.Ability cache.
                case 13108:
                    return ContextCharacter(session, contextualCharacterId) is CharacterData powerCharacter
                        && parameters.Count > 0
                        && CharacterPower.Calculate(session, powerCharacter) >= parameters[0];

                case 17117:
                    return parameters.Count >= 2
                        && parameters[0] > 0
                        && parameters.Skip(1).Where(stageId => stageId > 0).Sum(stageId => StageStars(session, stageId))
                        >= parameters[0];

                default:
                    return false;
            }
        }

        private static CharacterData? ContextCharacter(Session session, int characterId) =>
            characterId > 0
                ? session.character.Characters.FirstOrDefault(character => character.Id == (uint)characterId)
                : null;

        /// <summary>Earned stars of one stage: the low three StarsMark bits XStageInfo decodes as 1..3 stars.</summary>
        private static int StageStars(Session session, int stageId)
        {
            long starsMark = session.stage.Stages.TryGetValue(stageId, out StageDatum? stage) ? stage.StarsMark : 0;
            return (int)((starsMark & 1) + ((starsMark >> 1) & 1) + ((starsMark >> 2) & 1));
        }

        private static int ChapterStars(Session session, CharacterTowerChapterTable chapter) =>
            chapter.StageIds.Where(stageId => stageId > 0).Sum(stageId => StageStars(session, stageId));

        private static bool IsConditionFrozen(CharacterTowerRelationState? relation, int conditionId) =>
            relation is not null && relation.FinishConditions.Contains(conditionId);

        private static int SatisfiedConditionCount(
            Session session,
            CharacterTowerRelationTable relation,
            int characterId,
            CharacterTowerRelationState? state) =>
            relation.Conditions.Count(conditionId => conditionId > 0
                && (IsConditionFrozen(state, conditionId)
                    || IsConditionSatisfied(session, conditionId, characterId)));

        /// <summary>Conditions that are true right now and not frozen yet; activating a tier freezes them.</summary>
        private static List<int> FreezableConditions(
            Session session,
            CharacterTowerRelationTable relation,
            int characterId,
            CharacterTowerRelationState? state) =>
            relation.Conditions.Where(conditionId => conditionId > 0
                    && !IsConditionFrozen(state, conditionId)
                    && IsConditionSatisfied(session, conditionId, characterId))
                .ToList();

        #endregion

        #region Durable state

        private static CharacterTowerChapterState? FindChapterState(Player player, int chapterId) =>
            player.CharacterTower.Chapters.FirstOrDefault(chapter => chapter.ChapterId == chapterId);

        private static CharacterTowerRelationState? FindRelationState(Player player, int chapterId, int relationId) =>
            FindChapterState(player, chapterId)?.Relations.FirstOrDefault(relation => relation.RelationId == relationId);

        private static CharacterTowerChapterState GetOrCreateChapterState(Player player, int chapterId, out bool created)
        {
            CharacterTowerChapterState? chapter = FindChapterState(player, chapterId);
            if (chapter is not null)
            {
                created = false;
                return chapter;
            }

            chapter = new CharacterTowerChapterState { ChapterId = chapterId };
            player.CharacterTower.Chapters.Add(chapter);
            created = true;
            return chapter;
        }

        private static CharacterTowerRelationState GetOrCreateRelationState(
            CharacterTowerChapterState chapter,
            int relationId,
            out bool created)
        {
            CharacterTowerRelationState? relation = chapter.Relations.FirstOrDefault(entry => entry.RelationId == relationId);
            if (relation is not null)
            {
                created = false;
                return relation;
            }

            relation = new CharacterTowerRelationState { RelationId = relationId };
            chapter.Relations.Add(relation);
            created = true;
            return relation;
        }

        private static void DropChapterStateIfCreated(Player player, CharacterTowerChapterState chapter, bool created)
        {
            if (created)
                player.CharacterTower.Chapters.Remove(chapter);
        }

        /// <summary>
        /// Applies a mode claim marker and persists it. The reward receipt is already durable at this point, so a
        /// failed save rolls the in-memory edit back and lets the retried request re-run the same claim instead of
        /// answering as if it had been committed.
        /// </summary>
        private static void SaveClaim(Session session, Action mutate, Action rollback)
        {
            mutate();
            try
            {
                session.player.SaveChecked();
            }
            catch
            {
                rollback();
                throw;
            }
        }

        private static string ClaimKey(Session session, string scope, int id) =>
            $"character-tower-{scope}:{session.player.PlayerData.Id}:{id}";

        #endregion

        #region Stage and treasure tables

        /// <summary>Manual claim award of a stage: FirstRewardId with the generic FirstRewardShow fallback.</summary>
        private static int StageRewardId(int stageId)
        {
            if (!StagesById.TryGetValue(stageId, out StageTable? stage) || stage.FirstRewardShow is not > 0)
                return 0;

            return stage.FirstRewardId is > 0 ? stage.FirstRewardId.Value : stage.FirstRewardShow.Value;
        }

        /// <summary>Stages the client lists as claimable: exactly those with a FirstRewardShow entry.</summary>
        private static List<int> RewardedStageIds(CharacterTowerChapterTable chapter) =>
            chapter.StageIds.Where(stageId => stageId > 0 && StageRewardId(stageId) > 0).ToList();

        private static bool IsStageRewardClaimed(Session session, int chapterId, int stageId) =>
            FindChapterState(session.player, chapterId)?.StageRewardData.Contains(stageId) == true;

        #endregion

        #region Login projection

        /// <summary>Pure projection of durable mode state; no IO and no mutation.</summary>
        internal static NotifyLoginCharacterTowerData BuildLoginData(Session session)
        {
            CharacterTowerDataDb data = new();
            foreach (CharacterTowerChapterState chapter in session.player.CharacterTower.Chapters
                         .OrderBy(chapter => chapter.ChapterId))
            {
                CharacterTowerChapterInfo info = new()
                {
                    ChapterId = chapter.ChapterId,
                    ChapterRewardData = chapter.ChapterRewardData.ToList(),
                    TreasureData = chapter.TreasureData.ToList(),
                    StageRewardData = chapter.StageRewardData.ToList(),
                    VideoedIds = chapter.VideoedIds.ToList(),
                    TriggerConditions = chapter.TriggerConditions.ToList()
                };
                foreach (CharacterTowerRelationState relation in chapter.Relations.OrderBy(relation => relation.RelationId))
                {
                    info.RelationInfos.Add(new CharacterTowerRelationInfo
                    {
                        RelationId = relation.RelationId,
                        FightEventIds = relation.FightEventIds.ToList(),
                        StoryIds = relation.StoryIds.ToList(),
                        FinishConditions = relation.FinishConditions.ToList()
                    });
                }

                data.ChapterInfos.Add(info);
            }

            return new NotifyLoginCharacterTowerData
            {
                CharacterTowerDataDb = data,
                ActivityChapters = ActiveChapterIds(DateTimeOffset.UtcNow)
            };
        }

        /// <summary>Emits the mode login push; called from the login sequence.</summary>
        internal static void PrepareLogin(Session session) => session.SendPush(BuildLoginData(session));

        #endregion

        #region Relation activation (ActivateFightEventId / SaveStoryId)

        private static bool TryResolveRelation(
            int relationId,
            out CharacterTowerRelationTable relation,
            out CharacterTowerChapterTable chapter)
        {
            relation = null!;
            chapter = null!;
            if (!RelationsById.TryGetValue(relationId, out CharacterTowerRelationTable? resolvedRelation)
                || !ChapterIdByRelationGroupId.TryGetValue(relationId, out int chapterId)
                || !ChaptersById.TryGetValue(chapterId, out CharacterTowerChapterTable? resolvedChapter))
                return false;

            relation = resolvedRelation;
            chapter = resolvedChapter;
            return true;
        }

        [RequestPacketHandler("CharacterTowerActivateFightEventIdRequest")]
        public static void ActivateFightEventId(Session session, Packet.Request packet)
        {
            CharacterTowerActivateFightEventIdRequest request =
                packet.Deserialize<CharacterTowerActivateFightEventIdRequest>();
            if (!TryResolveRelation(request.RelationId, out CharacterTowerRelationTable? relation, out CharacterTowerChapterTable? chapter))
            {
                session.SendResponse(new CharacterTowerActivateFightEventIdResponse { Code = ConfigNotFound }, packet.Id);
                return;
            }

            if (!IsChapterOpen(session, chapter))
            {
                session.SendResponse(new CharacterTowerActivateFightEventIdResponse { Code = StageInfoError }, packet.Id);
                return;
            }

            int tierIndex = relation.FightEventIds.IndexOf(request.FightEventId);
            if (request.FightEventId <= 0 || tierIndex < 0)
            {
                session.SendResponse(new CharacterTowerActivateFightEventIdResponse { Code = ConfigNotFound }, packet.Id);
                return;
            }

            CharacterTowerRelationState? existing = FindRelationState(session.player, chapter.Id, relation.Id);
            if (existing is not null && existing.FightEventIds.Contains(request.FightEventId))
            {
                // Already durable: re-acknowledge the recorded state instead of failing a retried request.
                session.SendResponse(
                    new CharacterTowerActivateFightEventIdResponse { FinishConditions = existing.FinishConditions.ToList() },
                    packet.Id);
                return;
            }

            if (tierIndex >= relation.FinishNums.Count
                || relation.FinishNums[tierIndex] <= 0
                || SatisfiedConditionCount(session, relation, chapter.CharacterId, existing) < relation.FinishNums[tierIndex])
            {
                session.SendResponse(new CharacterTowerActivateFightEventIdResponse { Code = RelationConditionNotEnough }, packet.Id);
                return;
            }

            List<int> frozen = FreezableConditions(session, relation, chapter.CharacterId, existing);
            CharacterTowerChapterState chapterState = GetOrCreateChapterState(session.player, chapter.Id, out bool chapterCreated);
            CharacterTowerRelationState state = GetOrCreateRelationState(chapterState, relation.Id, out bool relationCreated);
            SaveClaim(
                session,
                () =>
                {
                    state.FightEventIds.Add(request.FightEventId);
                    state.FinishConditions.AddRange(frozen);
                },
                () =>
                {
                    state.FightEventIds.Remove(request.FightEventId);
                    foreach (int conditionId in frozen)
                        state.FinishConditions.Remove(conditionId);
                    if (relationCreated)
                        chapterState.Relations.Remove(state);
                    DropChapterStateIfCreated(session.player, chapterState, chapterCreated);
                });

            session.SendResponse(
                new CharacterTowerActivateFightEventIdResponse { FinishConditions = state.FinishConditions.ToList() },
                packet.Id);
        }

        [RequestPacketHandler("CharacterTowerSaveStoryIdRequest")]
        public static void SaveStoryId(Session session, Packet.Request packet)
        {
            CharacterTowerSaveStoryIdRequest request = packet.Deserialize<CharacterTowerSaveStoryIdRequest>();
            if (!TryResolveRelation(request.RelationId, out CharacterTowerRelationTable? relation, out CharacterTowerChapterTable? chapter))
            {
                session.SendResponse(new CharacterTowerSaveStoryIdResponse { Code = ConfigNotFound }, packet.Id);
                return;
            }

            if (!IsChapterOpen(session, chapter))
            {
                session.SendResponse(new CharacterTowerSaveStoryIdResponse { Code = StageInfoError }, packet.Id);
                return;
            }

            string storyId = request.StoryId ?? string.Empty;
            int tierIndex = relation.StoryIds.FindIndex(configured =>
                !string.IsNullOrEmpty(configured)
                && configured != NoStoryId
                && string.Equals(configured, storyId, StringComparison.Ordinal));
            if (string.IsNullOrEmpty(storyId) || storyId == NoStoryId || tierIndex < 0)
            {
                session.SendResponse(new CharacterTowerSaveStoryIdResponse { Code = ConfigNotFound }, packet.Id);
                return;
            }

            CharacterTowerRelationState? existing = FindRelationState(session.player, chapter.Id, relation.Id);
            if (existing is not null && existing.StoryIds.Contains(storyId))
            {
                // Already durable: re-acknowledge the recorded state instead of failing a retried request.
                session.SendResponse(
                    new CharacterTowerSaveStoryIdResponse { FinishConditions = existing.FinishConditions.ToList() },
                    packet.Id);
                return;
            }

            if (tierIndex >= relation.FinishNums.Count
                || relation.FinishNums[tierIndex] <= 0
                || SatisfiedConditionCount(session, relation, chapter.CharacterId, existing) < relation.FinishNums[tierIndex])
            {
                session.SendResponse(new CharacterTowerSaveStoryIdResponse { Code = RelationConditionNotEnough }, packet.Id);
                return;
            }

            List<int> frozen = FreezableConditions(session, relation, chapter.CharacterId, existing);
            CharacterTowerChapterState chapterState = GetOrCreateChapterState(session.player, chapter.Id, out bool chapterCreated);
            CharacterTowerRelationState state = GetOrCreateRelationState(chapterState, relation.Id, out bool relationCreated);
            SaveClaim(
                session,
                () =>
                {
                    state.StoryIds.Add(storyId);
                    state.FinishConditions.AddRange(frozen);
                },
                () =>
                {
                    state.StoryIds.Remove(storyId);
                    foreach (int conditionId in frozen)
                        state.FinishConditions.Remove(conditionId);
                    if (relationCreated)
                        chapterState.Relations.Remove(state);
                    DropChapterStateIfCreated(session.player, chapterState, chapterCreated);
                });

            session.SendResponse(
                new CharacterTowerSaveStoryIdResponse { FinishConditions = state.FinishConditions.ToList() },
                packet.Id);
        }

        #endregion

        #region Claims

        [RequestPacketHandler("CharacterTowerGetStageRewardRequest")]
        public static void GetStageReward(Session session, Packet.Request packet)
        {
            CharacterTowerGetStageRewardRequest request = packet.Deserialize<CharacterTowerGetStageRewardRequest>();
            if (!TryGetChapterId((uint)request.StageId, out int chapterId)
                || !ChaptersById.TryGetValue(chapterId, out CharacterTowerChapterTable? chapter)
                || !IsChapterOpen(session, chapter))
            {
                session.SendResponse(new CharacterTowerGetStageRewardResponse { Code = StageInfoError }, packet.Id);
                return;
            }

            if (IsStageRewardClaimed(session, chapterId, request.StageId))
            {
                session.SendResponse(new CharacterTowerGetStageRewardResponse { Code = StageRewardHadGot }, packet.Id);
                return;
            }

            int rewardId = StageRewardId(request.StageId);
            if (rewardId <= 0)
            {
                session.SendResponse(new CharacterTowerGetStageRewardResponse { Code = ConfigNotFound }, packet.Id);
                return;
            }

            if (!IsStagePassed(session, request.StageId))
            {
                session.SendResponse(new CharacterTowerGetStageRewardResponse { Code = StageNotPass }, packet.Id);
                return;
            }

            List<RewardGoodsTable> goods = RewardHandler.GetRewardGoods(rewardId);
            if (goods.Count == 0)
            {
                session.SendResponse(new CharacterTowerGetStageRewardResponse { Code = ConfigNotFound }, packet.Id);
                return;
            }

            RewardApplicationResult rewards = RewardHandler.ApplyRewardsOnceAndPersist(
                [new RewardGrant(ClaimKey(session, "stage", request.StageId), goods)], session);

            CharacterTowerChapterState state = GetOrCreateChapterState(session.player, chapterId, out bool created);
            SaveClaim(
                session,
                () => state.StageRewardData.Add(request.StageId),
                () =>
                {
                    state.StageRewardData.Remove(request.StageId);
                    DropChapterStateIfCreated(session.player, state, created);
                });

            rewards.SendPushes(session);
            session.SendResponse(
                new CharacterTowerGetStageRewardResponse { Rewards = rewards.RewardGoods },
                packet.Id);
        }

        [RequestPacketHandler("CharacterTowerGetChapterRewardRequest")]
        public static void GetChapterReward(Session session, Packet.Request packet)
        {
            CharacterTowerGetChapterRewardRequest request = packet.Deserialize<CharacterTowerGetChapterRewardRequest>();
            if (!ChaptersById.TryGetValue(request.ChapterId, out CharacterTowerChapterTable? chapter)
                || chapter.ChapterRewardId <= 0)
            {
                session.SendResponse(new CharacterTowerGetChapterRewardResponse { Code = ConfigNotFound }, packet.Id);
                return;
            }

            if (!IsChapterOpen(session, chapter))
            {
                session.SendResponse(new CharacterTowerGetChapterRewardResponse { Code = StageInfoError }, packet.Id);
                return;
            }

            if (FindChapterState(session.player, chapter.Id)?.ChapterRewardData.Contains(chapter.Id) == true)
            {
                session.SendResponse(new CharacterTowerGetChapterRewardResponse { Code = ChapterRewardHadGot }, packet.Id);
                return;
            }

            // The final gift is offered only once every rewarded stage reward of the chapter has been claimed.
            if (RewardedStageIds(chapter).Any(stageId => !IsStageRewardClaimed(session, chapter.Id, stageId)))
            {
                session.SendResponse(new CharacterTowerGetChapterRewardResponse { Code = FinishStageNotEnough }, packet.Id);
                return;
            }

            List<RewardGoodsTable> goods = RewardHandler.GetRewardGoods(chapter.ChapterRewardId);
            if (goods.Count == 0)
            {
                session.SendResponse(new CharacterTowerGetChapterRewardResponse { Code = ConfigNotFound }, packet.Id);
                return;
            }

            RewardApplicationResult rewards = RewardHandler.ApplyRewardsOnceAndPersist(
                [new RewardGrant(ClaimKey(session, "chapter", chapter.Id), goods)], session);

            CharacterTowerChapterState state = GetOrCreateChapterState(session.player, chapter.Id, out bool created);
            SaveClaim(
                session,
                () => state.ChapterRewardData.Add(chapter.Id),
                () =>
                {
                    state.ChapterRewardData.Remove(chapter.Id);
                    DropChapterStateIfCreated(session.player, state, created);
                });

            rewards.SendPushes(session);
            session.SendResponse(
                new CharacterTowerGetChapterRewardResponse { Rewards = rewards.RewardGoods },
                packet.Id);
        }

        [RequestPacketHandler("CharacterTowerGetStarRewardRequest")]
        public static void GetStarReward(Session session, Packet.Request packet)
        {
            CharacterTowerGetStarRewardRequest request = packet.Deserialize<CharacterTowerGetStarRewardRequest>();
            if (!TreasuresById.TryGetValue(request.TreasureId, out CharacterTowerTreasureTable? treasure)
                || treasure.RewardId <= 0
                || !ChapterIdByTreasureId.TryGetValue(request.TreasureId, out int chapterId)
                || !ChaptersById.TryGetValue(chapterId, out CharacterTowerChapterTable? chapter))
            {
                session.SendResponse(new CharacterTowerGetStarRewardResponse { Code = ConfigNotFound }, packet.Id);
                return;
            }

            if (!IsChapterOpen(session, chapter))
            {
                session.SendResponse(new CharacterTowerGetStarRewardResponse { Code = StageInfoError }, packet.Id);
                return;
            }

            if (FindChapterState(session.player, chapter.Id)?.TreasureData.Contains(treasure.TreasureId) == true)
            {
                session.SendResponse(new CharacterTowerGetStarRewardResponse { Code = StarRewardHadGot }, packet.Id);
                return;
            }

            if (ChapterStars(session, chapter) < treasure.RequireStar)
            {
                session.SendResponse(new CharacterTowerGetStarRewardResponse { Code = StarNotEnough }, packet.Id);
                return;
            }

            List<RewardGoodsTable> goods = RewardHandler.GetRewardGoods(treasure.RewardId);
            if (goods.Count == 0)
            {
                session.SendResponse(new CharacterTowerGetStarRewardResponse { Code = ConfigNotFound }, packet.Id);
                return;
            }

            RewardApplicationResult rewards = RewardHandler.ApplyRewardsOnceAndPersist(
                [new RewardGrant(ClaimKey(session, "treasure", treasure.TreasureId), goods)], session);

            CharacterTowerChapterState state = GetOrCreateChapterState(session.player, chapter.Id, out bool created);
            SaveClaim(
                session,
                () => state.TreasureData.Add(treasure.TreasureId),
                () =>
                {
                    state.TreasureData.Remove(treasure.TreasureId);
                    DropChapterStateIfCreated(session.player, state, created);
                });

            rewards.SendPushes(session);
            session.SendResponse(
                new CharacterTowerGetStarRewardResponse { Rewards = rewards.RewardGoods },
                packet.Id);
        }

        #endregion

        #region Client-only markers

        [RequestPacketHandler("CharacterTowerSaveVideoStageIdRequest")]
        public static void SaveVideoStageId(Session session, Packet.Request packet)
        {
            CharacterTowerSaveVideoStageIdRequest request = packet.Deserialize<CharacterTowerSaveVideoStageIdRequest>();
            if (!TryGetChapterId((uint)request.StageId, out int chapterId)
                || !ChaptersById.TryGetValue(chapterId, out CharacterTowerChapterTable? chapter)
                || chapter.Type != StoryChapterType
                || !IsChapterOpen(session, chapter))
            {
                session.SendResponse(new CharacterTowerSaveVideoStageIdResponse { Code = StageInfoError }, packet.Id);
                return;
            }

            if (!IsStagePassed(session, request.StageId))
            {
                session.SendResponse(new CharacterTowerSaveVideoStageIdResponse { Code = StageNotPass }, packet.Id);
                return;
            }

            CharacterTowerChapterState? existingChapter = FindChapterState(session.player, chapterId);
            if (existingChapter is not null && existingChapter.VideoedIds.Contains(request.StageId))
            {
                session.SendResponse(new CharacterTowerSaveVideoStageIdResponse(), packet.Id);
                return;
            }

            CharacterTowerChapterState state = GetOrCreateChapterState(session.player, chapterId, out bool created);
            SaveClaim(
                session,
                () => state.VideoedIds.Add(request.StageId),
                () =>
                {
                    state.VideoedIds.Remove(request.StageId);
                    DropChapterStateIfCreated(session.player, state, created);
                });

            session.SendResponse(new CharacterTowerSaveVideoStageIdResponse(), packet.Id);
        }

        [RequestPacketHandler("CharacterTowerSaveTriggerConditionIdRequest")]
        public static void SaveTriggerConditionId(Session session, Packet.Request packet)
        {
            CharacterTowerSaveTriggerConditionIdRequest request =
                packet.Deserialize<CharacterTowerSaveTriggerConditionIdRequest>();
            if (!ChaptersById.TryGetValue(request.ChapterId, out CharacterTowerChapterTable? chapter)
                || chapter.RelationGroupId is not > 0
                || !RelationsById.TryGetValue(chapter.RelationGroupId.Value, out CharacterTowerRelationTable? relation)
                || request.ConditionId <= 0
                || !relation.Conditions.Contains(request.ConditionId))
            {
                session.SendResponse(new CharacterTowerSaveTriggerConditionIdResponse { Code = ConfigNotFound }, packet.Id);
                return;
            }

            if (!IsChapterOpen(session, chapter))
            {
                session.SendResponse(new CharacterTowerSaveTriggerConditionIdResponse { Code = StageInfoError }, packet.Id);
                return;
            }

            CharacterTowerChapterState? existingChapter = FindChapterState(session.player, chapter.Id);
            if (existingChapter is not null && existingChapter.TriggerConditions.Contains(request.ConditionId))
            {
                session.SendResponse(new CharacterTowerSaveTriggerConditionIdResponse { Code = TriggerConditionExist }, packet.Id);
                return;
            }

            // A trigger is only the one-time left-tip acknowledgement of a condition that is already true; it
            // never completes a condition or advances relation progress.
            CharacterTowerRelationState? relationState = existingChapter?.Relations
                .FirstOrDefault(entry => entry.RelationId == chapter.RelationGroupId.Value);
            if (!IsConditionFrozen(relationState, request.ConditionId)
                && !IsConditionSatisfied(session, request.ConditionId, chapter.CharacterId))
            {
                session.SendResponse(new CharacterTowerSaveTriggerConditionIdResponse { Code = RelationConditionNotEnough }, packet.Id);
                return;
            }

            CharacterTowerChapterState state = GetOrCreateChapterState(session.player, chapter.Id, out bool created);
            SaveClaim(
                session,
                () => state.TriggerConditions.Add(request.ConditionId),
                () =>
                {
                    state.TriggerConditions.Remove(request.ConditionId);
                    DropChapterStateIfCreated(session.player, state, created);
                });

            session.SendResponse(new CharacterTowerSaveTriggerConditionIdResponse(), packet.Id);
        }

        #endregion

        /// <summary>
        /// Relation fight events that may be injected into the fight of <paramref name="stageId"/>: the activated
        /// tiers of that stage's own chapter relation (chapters without one contribute nothing).
        /// </summary>
        internal static List<int> GetActivatedFightEvents(Session session, uint stageId)
        {
            List<int> events = new();
            if (!TryGetChapterId(stageId, out int chapterId)
                || !ChaptersById.TryGetValue(chapterId, out CharacterTowerChapterTable? chapter)
                || chapter.RelationGroupId is not > 0)
                return events;

            CharacterTowerRelationState? relation = FindRelationState(session.player, chapterId, chapter.RelationGroupId.Value);
            if (relation is null)
                return events;

            events.AddRange(relation.FightEventIds.Where(eventId => eventId > 0));
            return events;
        }
    }
}
