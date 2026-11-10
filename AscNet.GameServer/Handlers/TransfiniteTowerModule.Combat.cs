using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Table.V2.share.fuben.transfinitetower;
using AscNet.Table.V2.share.robot;

namespace AscNet.GameServer.Handlers;

internal static partial class TransfiniteTowerModule
{
    private const int TeamSlotCount = 3, TowerMagicLevel = 1;

    private sealed record Member(int Slot, int FightId, int CharacterId, bool IsRobot, TransfiniteTowerCharacterTable? Cfg, bool IsNavigator, List<int> FatigueEventIds);
    private sealed record TowerFight(TransfiniteTowerStageTable Stage, TransfiniteTowerChapterTable? Chapter, List<Member> Members,
        TransfiniteTowerTeamSelection Selection, List<TransfiniteTowerCharacterCount> Charged);

    // FightModule PreFight gate. True when the request concerns a tower stage; code != 0 rejects.
    internal static bool ApplyPreFight(Session session, PreFightRequest.PreFightRequestPreFightData data, out int code)
    {
        code = 0;
        bool tower = IsBattleStage(data.StageId);
        if (data.SpeedrunStageId != 0 && (tower || IsBattleStage(data.SpeedrunStageId)))
        {
            code = FightAuthorizationError;
            return true;
        }
        if (!tower)
            return false;
        code = Validate(session, data, out _);
        return true;
    }

    // Called right before FightModule creates session.fight: decorates the generic FightData and persists the pending fight.
    internal static bool TryCommitPreFight(Session session, PreFightRequest.PreFightRequestPreFightData data,
        PreFightResponse.PreFightResponseFightData fightData, out int code)
    {
        if (!IsBattleStage(data.StageId)) { code = 0; return true; }
        code = Validate(session, data, out TowerFight? fight);
        if (code != 0) return false;
        if (!Decorate(session, fight!, fightData)) { code = FightAuthorizationError; return false; }

        TransfiniteTowerState state = session.player.TransfiniteTower!;
        state.PendingFight = new()
        {
            ChapterId = fight!.Chapter?.Id ?? 0,
            StageCfgId = fight.Stage.Id,
            StageId = data.StageId,
            FightId = fightData.FightId,
            StartedAt = Clock().ToUnixTimeSeconds(),
            Selection = fight.Selection,
            Team = fight.Members.Select(x => new TransfiniteTowerTeamMember { FightId = x.FightId, IsNavigator = x.IsNavigator }).ToList(),
            Charged = fight.Charged
        };
        // A new PreFight invalidates the unconfirmed floor (control.lua:1083 "server invalidates it").
        if (fight.Chapter is { } chapter && Chapter(state, chapter.Id).CurBattleInfo is { } battle)
            battle.PendingStageRecord = null;
        if (TrySave(session)) return true;
        code = FightAuthorizationError;
        return false;
    }

    // FightModule settle gate. True when the fought or credited stage is a tower stage.
    internal static bool TrySettle(Session session, FightSettleResult result, uint responseStageId, out FightSettleResponse response)
    {
        response = null!;
        if (!IsBattleStage(result.StageId) && !IsBattleStage(responseStageId)) return false;
        TransfiniteTowerState? state = session.player.TransfiniteTower;
        TransfiniteTowerPendingFight? pending = state?.PendingFight;
        if (responseStageId != result.StageId || pending is null || pending.FightId != result.FightId || pending.StageId != result.StageId
            || !Stages.Value.TryGetValue(pending.StageCfgId, out TransfiniteTowerStageTable? stage))
        {
            response = new() { Code = FightAuthorizationError };
            return true;
        }

        state!.PendingFight = null;
        FightSettleResponse.FightSettleResponseSettle settle = new()
        {
            IsWin = false,
            StageId = result.StageId,
            LeftTime = (int)Math.Clamp(result.LeftTime, int.MinValue, int.MaxValue),
            NpcHpInfo = result.NpcHpInfo
        };
        TransfiniteTowerActivityTable? activity = OpenActivity(Clock());
        TransfiniteTowerChapterInfo? info = null;
        int code = 0;
        if (result.IsWin && !result.IsForceExit)
        {
            if (activity is null || activity.Id != state.ActivityId)
                code = ActivityNotOpen;
            else if (pending.ChapterId == 0)
            {
                // Teaching stages carry no tower result (agency.lua:111).
                if (!state.PassedTeachStageIds.Contains(stage.Id)) state.PassedTeachStageIds.Add(stage.Id);
                settle.IsWin = true;
            }
            else if (!Chapters.Value.TryGetValue(pending.ChapterId, out TransfiniteTowerChapterTable? chapter))
                code = ChapterCfgNotFound;
            else
            {
                info = Chapter(state, pending.ChapterId);
                code = ProgressCode(chapter, info, stage.Order, out _);
                if (code == 0)
                {
                    // AscNet policy: floor time is trusted server wall-clock since the committed PreFight.
                    int spend = (int)Math.Clamp(Clock().ToUnixTimeSeconds() - pending.StartedAt, 0, int.MaxValue);
                    (info.CurBattleInfo ??= new()).PendingStageRecord = new()
                    {
                        Order = stage.Order, SpendTime = spend, Team = pending.Team, StageCfgId = stage.Id,
                        Selection = pending.Selection, Charged = pending.Charged
                    };
                    settle.IsWin = true;
                    settle.TransfiniteTowerFightResult = new() { Order = stage.Order, SpendTime = spend };
                }
            }
        }
        if (!TrySave(session))
        {
            response = new() { Code = FightAuthorizationError };
            return true;
        }
        response = code != 0 ? new() { Code = code } : new() { Code = 0, Settle = settle };
        if (info is not null && code == 0) SendChapterInfo(session, info);
        return true;
    }

    // Floor admission (control.lua:437 prepare-state machine): next floor, or re-challenge of the last cleared floor.
    private static int ProgressCode(TransfiniteTowerChapterTable chapter, TransfiniteTowerChapterInfo info, int order, out bool rechallenge)
    {
        int progress = info.CurBattleInfo?.StageProgressIndex ?? 0;
        rechallenge = progress > 0 && order == progress;
        if (info.LastStageRecordList.Count > 0 && !(chapter.Type == ChapterTypeTeach && order == 1 && progress == 0))
            return LastRecordNotReset;
        return order == progress + 1 || rechallenge ? 0 : StageProgressError;
    }

    private static int Validate(Session session, PreFightRequest.PreFightRequestPreFightData data, out TowerFight? fight)
    {
        fight = null;
        DateTimeOffset now = Clock();
        TransfiniteTowerStageTable stage = StagesByBattleId.Value[(int)data.StageId];
        if (OpenActivity(now) is not { } activity) return ActivityNotOpen;
        TransfiniteTowerState state = State(session, activity);
        TransfiniteTowerChapterTable? chapter = ChapterOf(activity, stage);
        bool teach = chapter is null;
        if (teach && !activity.TeachStageIds.Contains(stage.Id)) return StageCfgNotFound;
        TransfiniteTowerChapterInfo? info = null;
        bool rechallenge = false;
        if (chapter is not null)
        {
            if (!IsChapterOpen(session.player, chapter, now)) return ChapterNotOpen;
            info = Chapter(state, chapter.Id);
            int progressCode = ProgressCode(chapter, info, stage.Order, out rechallenge);
            if (progressCode != 0) return progressCode;
        }
        if (!Groups.Value.TryGetValue(stage.CharacterGroupId, out TransfiniteTowerCharacterGroupTable? group)) return CharacterGroupCfgNotFound;

        List<uint> cards = data.CardIds ?? [];
        List<int> robots = data.RobotIds ?? [];
        if (cards.Skip(TeamSlotCount).Any(x => x != 0) || robots.Skip(TeamSlotCount).Any(x => x != 0)) return TeamSlotOverLimit;
        List<Member> members = [];
        for (int slot = 0; slot < TeamSlotCount; slot++)
        {
            int card = slot < cards.Count ? (int)Math.Min(cards[slot], int.MaxValue) : 0;
            int robot = slot < robots.Count ? robots[slot] : 0;
            if (card < 0 || robot < 0) return TeamMemberNotInGroup;
            if (card > 0 && robot > 0) return TeamSlotConflict;
            if (card == 0 && robot == 0) continue;
            TransfiniteTowerCharacterTable? cfg;
            int characterId;
            if (robot > 0)
            {
                if (!Robots.Value.TryGetValue(robot, out RobotTable? robotRow) || robotRow.CharacterId <= 0) return TeamMemberNotInGroup;
                cfg = GroupCharacter(group, x => x.RobotId == robot);
                if (cfg is null) return TeamMemberNotInGroup;
                characterId = robotRow.CharacterId;
            }
            else
            {
                if (!session.character.Characters.Any(x => x.Id == (uint)card)) return TeamCharacterMismatch;
                cfg = GroupCharacter(group, x => x.CharacterId == card);
                if (cfg is null && group.IsAllCharacter != 1) return TeamMemberNotInGroup;
                characterId = card;
            }
            bool navigator = Characters.Value.Values.Any(x => x.CharacterId == characterId && x.Type == NavigatorType);
            if (cfg is not null && !IsTimeOpen(cfg.UnLockTimeId, now)) return navigator ? NavigatorLocked : CharacterLocked;
            members.Add(new(slot, robot > 0 ? robot : card, characterId, robot > 0, cfg, navigator, []));
        }
        if (members.Count == 0) return TeamSlotOverLimit;
        if (members.Select(x => x.CharacterId).Distinct().Count() != members.Count) return TeamMemberDuplicate;
        if (data.CaptainPos is < 1 or > TeamSlotCount || data.FirstFightPos is < 1 or > TeamSlotCount
            || members.All(x => x.Slot != data.CaptainPos - 1) || members.All(x => x.Slot != data.FirstFightPos - 1))
            return TeamSlotOverLimit;

        int navigators = members.Count(x => x.IsNavigator);
        int navigatorCode = stage.NavigatorMode switch
        {
            1 when navigators < stage.NavigatorCount => NavigatorRequired,
            1 or 2 when navigators > stage.NavigatorCount => NavigatorCountOverLimit,
            3 when navigators > 0 => NavigatorForbidden,
            _ => 0
        };
        if (navigatorCode != 0) return navigatorCode;

        TransfiniteTowerStageRecord? record = rechallenge ? info!.CurBattleInfo!.StageRecordList.FirstOrDefault(x => x.Order == stage.Order) : null;
        if (rechallenge && (record is null || !record.Team.Select(x => x.FightId).OrderBy(x => x).SequenceEqual(members.Select(x => x.FightId).OrderBy(x => x))))
            return RefightTeamChanged;

        bool teachTower = teach || chapter!.Type == ChapterTypeTeach;
        List<TransfiniteTowerCharacterCount> charged = [];
        foreach (Member member in members.Where(x => !x.IsNavigator))
        {
            int cfgId = member.Cfg?.Id ?? 0;
            int init = InitCount(group, member.Cfg);
            // Teach towers read the configured energy without deducting use (agency.lua:680).
            int used = teachTower ? 0 : UsedBefore(info!, stage.Order, cfgId, member.CharacterId);
            int remain = Math.Max(0, init - used);
            if (!rechallenge && remain <= 0) return ChallengeCountNotEnough;
            // Row index is the client's own debuff index: XTransfiniteTowerAgency:GetRoleDebuff evaluates
            // GetEntityEnergyMax() - remain against DEBUFF_HP_REDUCE, and Config FightCount is the Lua
            // ENERGY_MAX default, so row 1/2 are the authored 33%/66% penalties for 2/1 remaining charges
            // of a frame (InitFightCounts is 1..3).
            if (FightCounts.Value.TryGetValue(DefaultFightCount.Value - remain, out TransfiniteTowerFightCountTable? debuff))
                member.FatigueEventIds.AddRange(debuff.DebuffFightEventIds.Where(x => x > 0));
            if (!teach) charged.Add(new() { CharacterCfgId = cfgId, CharacterId = member.CharacterId, UsedCount = 1 });
        }
        if (rechallenge) charged = record!.Charged;

        fight = new(stage, chapter, members, new()
        {
            CardIds = Enumerable.Range(0, TeamSlotCount).Select(i => members.FirstOrDefault(x => x.Slot == i && !x.IsRobot)?.FightId ?? 0).ToList(),
            RobotIds = Enumerable.Range(0, TeamSlotCount).Select(i => members.FirstOrDefault(x => x.Slot == i && x.IsRobot)?.FightId ?? 0).ToList(),
            CaptainPos = data.CaptainPos,
            FirstFightPos = data.FirstFightPos
        }, charged);
        return 0;
    }

    private static TransfiniteTowerCharacterTable? GroupCharacter(TransfiniteTowerCharacterGroupTable group, Func<TransfiniteTowerCharacterTable, bool> match) =>
        group.TowerCharacterIds.Select(id => Characters.Value.GetValueOrDefault(id)).FirstOrDefault(x => x is not null && match(x));

    // XTransfiniteTowerAgency.GetEntityInitCount: per-group InitFightCounts, Config FightCount otherwise.
    private static int InitCount(TransfiniteTowerCharacterGroupTable group, TransfiniteTowerCharacterTable? cfg)
    {
        int index = cfg is null ? -1 : group.TowerCharacterIds.IndexOf(cfg.Id);
        return index >= 0 && index < group.InitFightCounts.Count ? group.InitFightCounts[index] : DefaultFightCount.Value;
    }

    // Uses charged by committed floors below this one (agency.lua:703 GetEntityEnergyBeforeStage).
    private static int UsedBefore(TransfiniteTowerChapterInfo info, int order, int cfgId, int characterId) =>
        info.CurBattleInfo?.StageRecordList.Where(x => x.Order < order).SelectMany(x => x.Charged)
            .Count(x => cfgId > 0 ? x.CharacterCfgId == cfgId : x.CharacterCfgId <= 0 && x.CharacterId == characterId) ?? 0;

    // Adds every authored effect: stage FightEventIds, character MagicIds, fatigue debuff events; NPCs keyed by team slot.
    private static bool Decorate(Session session, TowerFight fight, PreFightResponse.PreFightResponseFightData fightData)
    {
        foreach (int eventId in fight.Stage.FightEventIds.Where(x => x > 0))
            if (!fightData.EventIds.Any(x => Convert.ToInt32((object)x) == eventId))
                fightData.EventIds.Add(eventId);
        var role = fightData.RoleData.FirstOrDefault(x => x.Id == (uint)session.player.PlayerData.Id);
        if (role?.NpcData is null || role.NpcData.Count != fight.Members.Count) return false;
        Dictionary<int, dynamic> npcs = new();
        foreach (dynamic npc in role.NpcData.Values)
        {
            CharacterData character = npc.Character;
            int characterId = (int)character.Id;
            bool isRobot = npc.IsRobot;
            Member? member = fight.Members.FirstOrDefault(x => x.CharacterId == characterId && x.IsRobot == isRobot);
            if (member is null || npcs.ContainsKey(member.Slot)) return false;
            Dictionary<int, int> magicIds = npc.MagicIds;
            foreach (int magicId in member.Cfg?.MagicIds.Where(x => x > 0) ?? [])
                magicIds.TryAdd(magicId, TowerMagicLevel);
            npcs[member.Slot] = new
            {
                npc.Character, npc.Equips, npc.WeaponFashionId, npc.Partner, npc.IsRobot, npc.RobotId, npc.IsNpc,
                npc.CharacterCareer, npc.CharacterSkillPlus, npc.FightEventsWithLevel, npc.AwarenessSetPositions, MagicIds = magicIds, EventIds = member.FatigueEventIds.ToList()
            };
        }
        role.NpcData = npcs;
        return true;
    }
}
