using AscNet.Common;
using AscNet.Common.MsgPack;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.functional;
using AscNet.Table.V2.share.reward;
using AscNet.Table.V2.share.robot;
using AscNet.Table.V2.share.theatre;
using MessagePack;

namespace AscNet.GameServer.Handlers;

internal static partial class TheatreModule
{
    private static int LifecycleConfig(string key) => Rows<TheatreConfigTable>().Single(row => row.Key == key).Value;

    internal static void EnsureAvailable(Mutation mutation, DateTimeOffset now)
    {
        // Original Theatre has no activity TimeId. Its authored function conditions are the gate.
        Require(Rows<FunctionalOpenTable>().Single(row => row.Id == 10420).Condition
            .All(id => id <= 0 || IsConditionSatisfied(mutation, id)), 20155001);
        InitializeMeta(mutation);
    }

    internal static void PrepareLogin(Session session)
    {
        ResumePending(session, forLogin: true);
        Mutation mutation = new(session);
        mutation.State.RequestReceipts.Clear();
        mutation.State.Fight?.RestartReceipts.Clear();
        InitializeMeta(mutation);
        // Offers, selected node, skill choices, teams and attempts are already durable: never redraw here.
        Persist(mutation, string.Empty, string.Empty, null);
    }

    internal static NotifyTheatreData BuildLoginData(Session session)
    {
        var state = session.player.Theatre;
        TheatreData data = state.Data.CurChapterDb is null && state.SettlementRecoveryPending && state.LastSettle is not null && state.LastRunData is not null
            ? Clone(state.LastRunData) : Clone(state.Data);
        // The final presentation needs the old adventure, but permanent meta may have changed since settlement.
        data.Decorations = state.Data.Decorations;
        data.UnlockPowerIds = state.Data.UnlockPowerIds;
        data.UnlockPowerFavorIds = state.Data.UnlockPowerFavorIds;
        data.EffectPowerFavorIds = state.Data.EffectPowerFavorIds;
        data.SkillIllustratedBook = state.Data.SkillIllustratedBook;
        data.Keepsakes = state.Data.Keepsakes;
        data.PassChapterId = state.Data.PassChapterId;
        data.EndingRecord = state.Data.EndingRecord;
        data.PassEventRecord = state.Data.PassEventRecord;
        // Login's top-level counter initializes the current chapter; retain cumulative count only server-side.
        data.PassNodeCount = data.CurChapterDb?.PassNodeCount ?? 0;
        return MessagePackSerializer.Deserialize<NotifyTheatreData>(MessagePackSerializer.Serialize(data));
    }

    [RequestPacketHandler("TheatreStartAdventureRequest")]
    public static void TheatreStartAdventureRequestHandler(Session session, Packet.Request packet) =>
        Handle<TheatreStartAdventureRequest, TheatreStartAdventureResponse>(session, packet, (mutation, request, response) =>
        {
            Require(mutation.Data.CurChapterDb is null, 20155004);
            var difficulty = Rows<TheatreDifficultyTable>().SingleOrDefault(row => row.Id == request.Difficulty);
            Require(difficulty is not null && IsConditionSatisfied(mutation, difficulty.ConditionId ?? 0), 20155001);
            Require(request.Mode is 0 or 1, 20155001);
            Require(request.Mode == 0 || IsConditionSatisfied(mutation, LifecycleConfig("SPModeConditionId")), 20155001);
            int keepsake = request.KeepsakeId ?? 0;
            Require(keepsake == 0 || mutation.Data.Keepsakes.Any(row => row.KeepsakeId == keepsake), 20155003);
            // Highest unlocked authored branch wins; group 1's level-zero condition is intentionally also true.
            var group = Rows<TheatreChapterGroupTable>().Where(row => (row.Mode ?? 0) == request.Mode
                && IsConditionSatisfied(mutation, row.ConditionId ?? 0)).MaxBy(row => row.Id);
            Require(group is not null, 20155022);
            ResetSettledRun(mutation);
            mutation.State.RunId = checked(mutation.State.RunId + 1);
            mutation.State.LastFightId = 0;
            mutation.State.LastFightStageId = 0;
            mutation.State.LastFightRunId = 0;
            mutation.State.LastFightSettle = null;
            mutation.State.LastFightPacketId = 0;
            mutation.State.LastFightRequestKey = string.Empty;
            mutation.State.LastRunData = null;
            mutation.State.LastSettle = null;
            mutation.State.SettlementRecoveryPending = false;
            mutation.State.Mode = request.Mode;
            mutation.Data.DifficultyId = request.Difficulty;
            mutation.Data.KeepsakeId = keepsake;
            mutation.Data.CurRoleLv = Rows<TheatreLvTable>().Min(row => row.Lv);
            mutation.Data.ReopenCount = 0; // Wire counts uses; difficulty + decoration define the limit.
            FreezeRolePool(mutation);
            mutation.Data.CurChapterDb = new() { ChapterId = group!.ChapterStartId };
            // LOCAL run-currency boundary: an accepted new run discards leftover Marg before its initial bonus.
            // Journal costs are not node-shop purchases and must not credit spend tasks.
            long remainingMarg = mutation.Balance(96101);
            while (remainingMarg > 0)
            {
                int discard = (int)Math.Min(remainingMarg, int.MaxValue);
                mutation.Cost(96101, discard);
                remainingMarg -= discard;
            }
            int initialCoin = GetModifiers(mutation).InitialCoinBonus;
            if (initialCoin > 0)
                mutation.Grant(new RewardGrant(
                    $"theatre:start:{mutation.Session.player.PlayerData.Id}:{mutation.State.RunId}",
                    [new RewardGoodsTable { Id = 96101, TemplateId = 96101, Count = initialCoin }]));
            BeginAdventure(mutation);
            response.ChapterId = group.ChapterStartId;
        }, onFailure: failed => failed.SendPush(BuildLoginData(failed)));

    private static void FreezeRolePool(Mutation mutation)
    {
        var levels = Rows<TheatreLvTable>();
        var attrs = Rows<TheatreRoleAttrTable>();
        var robots = Rows<RobotTable>();
        mutation.State.FrozenRoleIds = Rows<TheatreRoleTable>().Where(role => levels.All(level =>
            attrs.Any(attr => attr.RoleId == role.Id && attr.Lv == level.Lv
                && robots.Any(robot => robot.Id == attr.RobotId)))).Select(role => role.Id).ToList();
        mutation.State.FrozenOwnCharacterIds = mutation.Session.character.Characters.Select(character => checked((int)character.Id)).ToList();
        mutation.State.FrozenRolePoolInitialized = true;
    }

    internal static void InitializeChapterRecruit(Mutation mutation)
    {
        Require(mutation.Data.CurChapterDb is not null, 20155005);
        mutation.Data.CurChapterDb!.RefreshRoleCount = 0;
        mutation.Data.CurChapterDb.RefreshRole = DrawRecruitRoles(mutation);
    }

    private static TheatreChapterTable RecruitChapter(Mutation mutation)
    {
        Require(mutation.Data.CurChapterDb is not null, 20155005);
        var chapter = Rows<TheatreChapterTable>().SingleOrDefault(row => row.Id == mutation.Data.CurChapterDb!.ChapterId);
        Require(chapter is not null, 20155022);
        Require(chapter!.IsRecruit != 0 && mutation.State.Fight is null, 20155010);
        return chapter;
    }

    internal static int RemainingRecruitCount(Mutation mutation)
    {
        var chapter = RecruitChapter(mutation);
        var previous = Rows<TheatreChapterTable>().Where(row => row.GroupId == chapter.GroupId && row.Id <= chapter.Id).ToList();
        return Math.Max(0, checked(previous.Sum(row => row.RecruitCount)
            + previous.Count * GetModifiers(mutation).RecruitBonus - mutation.Data.RecruitRole.Count));
    }

    internal static void RequireCanEnterChapter(Mutation mutation)
    {
        var chapter = RecruitChapter(mutation);
        var data = mutation.Data.CurChapterDb!;
        Require(RemainingRecruitCount(mutation) == 0
            || (data.RefreshRoleCount >= checked(chapter.RecruitRefreshCount + GetModifiers(mutation).RefreshBonus)
                && !data.RefreshRole.Any(role => !mutation.Data.RecruitRole.Contains(role))), 20155007);
    }

    private static List<int> DrawRecruitRoles(Mutation mutation)
    {
        Require(mutation.State.FrozenRolePoolInitialized, 20155008);
        // LOCAL allocation: one equally weighted role from each authored PoolId, without replacement.
        // Retail pool probabilities/offer size are absent. Pool membership is source-defined; not ownership.
        return Rows<TheatreRoleTable>().Where(row => mutation.State.FrozenRoleIds.Contains(row.Id)
                && !mutation.Data.RecruitRole.Contains(row.Id)).GroupBy(row => row.PoolId).OrderBy(group => group.Key)
            .Select(group => { var candidates = group.ToArray(); return candidates[Random.Shared.Next(candidates.Length)].Id; }).ToList();
    }

    [RequestPacketHandler("TheatreRefreshCharacterRequest")]
    public static void TheatreRefreshCharacterRequestHandler(Session session, Packet.Request packet) =>
        Handle<TheatreRefreshCharacterRequest, TheatreRefreshCharacterResponse>(session, packet, (mutation, request, response) =>
        {
            var chapter = RecruitChapter(mutation);
            Require(RemainingRecruitCount(mutation) > 0, 20155007);
            var data = mutation.Data.CurChapterDb!;
            Require(data.RefreshRoleCount < checked(chapter.RecruitRefreshCount + GetModifiers(mutation).RefreshBonus), 20155006);
            data.RefreshRole = DrawRecruitRoles(mutation);
            data.RefreshRoleCount = checked(data.RefreshRoleCount + 1);
            response.RefreshRoleCount = data.RefreshRoleCount;
            response.RoleList = data.RefreshRole.ToList();
        });

    [RequestPacketHandler("TheatreRecruitCharacterRequest")]
    public static void TheatreRecruitCharacterRequestHandler(Session session, Packet.Request packet) =>
        Handle<TheatreRecruitCharacterRequest, TheatreRecruitCharacterResponse>(session, packet, (mutation, request, response) =>
        {
            RecruitChapter(mutation);
            Require(RemainingRecruitCount(mutation) > 0, 20155007);
            Require(mutation.State.FrozenRoleIds.Contains(request.RoleId)
                && mutation.Data.CurChapterDb!.RefreshRole.Contains(request.RoleId), 20155008);
            Require(!mutation.Data.RecruitRole.Contains(request.RoleId), 20155009);
            mutation.Data.RecruitRole.Add(request.RoleId);
            UpdateOwnCharacterPermission(mutation);
        });

    internal static void ValidateTeam(Mutation mutation, TheatreTeamData team, int index)
    {
        Require(mutation.Data.CurChapterDb is not null, 20155005);
        Require(team is not null && team.TeamIndex == index, 20155037);
        Require(team!.CardIds is { Count: 3 } && team.RobotIds is { Count: 3 }, 20155033);
        Require(team.CaptainPos is >= 1 and <= 3 && team.FirstFightPos is >= 1 and <= 3
            && team.EnterCgIndex is >= 0 and <= 3 && team.SettleCgIndex is >= 0 and <= 3, 20155035);
        HashSet<int> characters = [];
        for (int position = 0; position < 3; position++)
        {
            int card = team.CardIds[position], robot = team.RobotIds[position];
            Require(card >= 0 && robot >= 0 && (card == 0 || robot == 0), 20155033);
            if (card == 0 && robot == 0) continue;
            int character = TeamCharacter(mutation, card, robot);
            Require(characters.Add(character), 20155039);
        }
        Require(team.CardIds[team.CaptainPos - 1] > 0 || team.RobotIds[team.CaptainPos - 1] > 0, 20155035);
        Require(team.CardIds[team.FirstFightPos - 1] > 0 || team.RobotIds[team.FirstFightPos - 1] > 0, 20155035);
    }

    private static int TeamCharacter(Mutation mutation, int card, int robot)
    {
        int level = Rows<TheatreLvTable>().Single(row => row.Lv == mutation.Data.CurRoleLv).Lv;
        var attrs = Rows<TheatreRoleAttrTable>().Where(row => row.Lv == level && mutation.Data.RecruitRole.Contains(row.RoleId));
        if (robot > 0)
        {
            Require(attrs.Any(row => row.RobotId == robot), 20155032);
            return Rows<RobotTable>().Single(row => row.Id == robot).CharacterId;
        }
        Require(mutation.Data.UseOwnCharacter != 0 && mutation.State.FrozenOwnCharacterIds.Contains(card)
            && attrs.Any(row => Rows<RobotTable>().Any(robotRow => robotRow.Id == row.RobotId && robotRow.CharacterId == card)), 20155033);
        var owned = mutation.Session.character.Characters.SingleOrDefault(character => character.Id == card);
        Require(owned is not null, 20155033);
        Require(CharacterPower.Calculate(mutation.Session, owned!) >= LifecycleConfig("UseOwnCharacterFa"), 20155034);
        return card;
    }

    internal static void RemapRoleLevel(Mutation mutation, int newLevel)
    {
        var level = Rows<TheatreLvTable>().Single(row => row.Lv == newLevel);
        var attrs = Rows<TheatreRoleAttrTable>();
        foreach (var team in mutation.Data.MultiTeamDatas.Concat(mutation.Data.SingleTeamData is { } single ? [single] : []))
            for (int i = 0; i < team.RobotIds.Count; i++)
                if (team.RobotIds[i] > 0)
                {
                    int role = attrs.Single(row => row.RobotId == team.RobotIds[i]).RoleId;
                    team.RobotIds[i] = attrs.Single(row => row.RoleId == role && row.Lv == level.Lv).RobotId;
                }
        mutation.Data.CurRoleLv = newLevel;
        UpdateOwnCharacterPermission(mutation);
    }

    internal static void UpdateOwnCharacterPermission(Mutation mutation)
    {
        if (mutation.Data.UseOwnCharacter != 0 || mutation.Data.RecruitRole.Count == 0) return;
        // LOCAL unlock rule: compare the source-displayed trial roster mean BP against the authored threshold.
        // Role BP = current RoleAttr.FightAbility + sum of current TheatreSkill.FightAbility (client source).
        long skillPower = Rows<TheatreSkillTable>().Where(row => mutation.Data.Skills.Contains(row.Id)).Sum(row => (long)row.FightAbility);
        long rosterPower = Rows<TheatreRoleAttrTable>().Where(row => row.Lv == mutation.Data.CurRoleLv
            && mutation.Data.RecruitRole.Contains(row.RoleId)).Sum(row => (long)row.FightAbility + skillPower);
        if (rosterPower / mutation.Data.RecruitRole.Count < LifecycleConfig("UseOwnCharacterFa")) return;
        mutation.Data.UseOwnCharacter = 1;
        mutation.Push(new NotifyTheatreUseOwnCharacter { UseOwnCharacter = 1 });
    }

    [RequestPacketHandler("TheatreSetSingleTeamRequest")]
    public static void TheatreSetSingleTeamRequestHandler(Session session, Packet.Request packet) =>
        Handle<TheatreSetSingleTeamRequest, TheatreSetSingleTeamResponse>(session, packet, (mutation, request, response) =>
        {
            Require(mutation.State.Fight is null, 20155040);
            ValidateTeam(mutation, request.TeamData, 0);
            mutation.Data.SingleTeamData = Clone(request.TeamData);
        });

    private static TheatreSlot MultiTeamSlot(Mutation mutation)
    {
        var slot = CurrentSlot(mutation);
        var stage = Rows<TheatreStageTable>().SingleOrDefault(row => row.Id == slot.TheatreStageId);
        Require(stage is not null && stage.StageCount > 1 && slot.StageIds.Count == stage.StageCount, 20155036);
        return slot;
    }

    [RequestPacketHandler("TheatreSetMultiTeamRequest")]
    public static void TheatreSetMultiTeamRequestHandler(Session session, Packet.Request packet) =>
        Handle<TheatreSetMultiTeamRequest, TheatreSetMultiTeamResponse>(session, packet, (mutation, request, response) =>
        {
            Require(mutation.State.Fight is null, 20155040);
            var slot = MultiTeamSlot(mutation);
            Require(request.TeamDatas is not null && request.TeamDatas.Count == slot.StageIds.Count
                && request.TeamDatas.All(team => team is not null), 20155038);
            Require(request.TeamDatas!.Select(team => team.TeamIndex).Distinct().Count() == slot.StageIds.Count, 20155037);
            HashSet<int> characters = [];
            foreach (var team in request.TeamDatas)
            {
                Require(team.TeamIndex >= 1 && team.TeamIndex <= slot.StageIds.Count, 20155037);
                ValidateTeam(mutation, team, team.TeamIndex);
                if (slot.PassedStageIndexs.Contains(team.TeamIndex))
                {
                    var saved = mutation.Data.MultiTeamDatas.SingleOrDefault(row => row.TeamIndex == team.TeamIndex);
                    Require(saved is not null && saved.CaptainPos == team.CaptainPos && saved.FirstFightPos == team.FirstFightPos
                        && saved.CardIds.SequenceEqual(team.CardIds) && saved.RobotIds.SequenceEqual(team.RobotIds), 20155040);
                }
                for (int i = 0; i < 3; i++)
                    if (team.CardIds[i] > 0 || team.RobotIds[i] > 0)
                        Require(characters.Add(TeamCharacter(mutation, team.CardIds[i], team.RobotIds[i])), 20155039);
            }
            mutation.Data.MultiTeamDatas = Clone(request.TeamDatas.OrderBy(team => team.TeamIndex).ToList());
        });

    [RequestPacketHandler("TheatreMultiTeamResetRequest")]
    public static void TheatreMultiTeamResetRequestHandler(Session session, Packet.Request packet) =>
        Handle<TheatreMultiTeamResetRequest, TheatreMultiTeamResetResponse>(session, packet, (mutation, request, response) =>
        {
            Require(mutation.State.Fight is null, 20155040);
            var slot = MultiTeamSlot(mutation);
            Require(request.TeamIndex >= 1 && request.TeamIndex <= slot.StageIds.Count, 20155044);
            Require(slot.PassedStageIndexs.Remove(request.TeamIndex), 20155044);
            slot.PassedStageIds = slot.PassedStageIndexs.Select(index => slot.StageIds[index - 1]).ToList();
        });

    internal static void ResetSettledRun(Mutation mutation)
    {
        var data = mutation.Data;
        mutation.State.Data = new()
        {
            Decorations = data.Decorations, UnlockPowerIds = data.UnlockPowerIds,
            UnlockPowerFavorIds = data.UnlockPowerFavorIds, EffectPowerFavorIds = data.EffectPowerFavorIds,
            SkillIllustratedBook = data.SkillIllustratedBook, Keepsakes = data.Keepsakes,
            PassChapterId = data.PassChapterId, EndingRecord = data.EndingRecord, PassEventRecord = data.PassEventRecord
        };
        mutation.State.Mode = 0;
        mutation.State.FrozenRoleIds.Clear();
        mutation.State.FrozenOwnCharacterIds.Clear();
        mutation.State.FrozenRolePoolInitialized = false;
        mutation.State.Fight = null;
        mutation.State.NodeCursor = 0;
        mutation.State.NodeCompletionPending = false;
        mutation.State.ShopSkillOpened = false;
        mutation.State.PendingSkillShopType = 0;
        mutation.State.PendingSkillPowers.Clear();
        mutation.State.RunNodeCount = mutation.State.RunFightCount = mutation.State.RunEventCount = mutation.State.RunBossCount = 0;
        mutation.State.CompletedChapterIds.Clear();
        mutation.State.RunEventIds.Clear();
        mutation.State.EventVisitedSteps.Clear();
    }
}
