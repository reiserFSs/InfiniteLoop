using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.theatre6;
using MessagePack;

namespace AscNet.GameServer.Handlers;

// Shrouded Requiem PvE: story/gameplay starts, difficulty and unlock validation, paid story stages, the authored
// floor/room chain, choose and task rooms, fight rooms, AVG rooms, extra floors and the terminal boundary.
//
// Client contract (EN XTheatre6ControlNetwork / XTheatre6Control / XTheatre6Model + XUiTheatre6*):
//   * a start response carries the complete mode snapshot; the client builds its chain from it and opens the first
//     room, so the run start must not push a room notification (any push advances the client chain one node).
//   * every later room is announced with NotifyTheatre6NewRoomData/NotifyTheatre6NewFloorData after the response,
//     because ENTER_NEW_ROOM drives XTheatre6SubStageChain:MoveNext.
//   * ChooseEvent resolves the *current* offer and returns the *next* one (NextChooseId/LeftRewards/RightRewards);
//     the client increments CurChoosePoolIdx itself.
//   * floor-introduction AVG (StageFloor.StartAVG) and chapter previews are client-local inserts, never server rooms;
//     room AVG (StageRoom.Type 6) is a real room ended by Theatre6EndAvgRoomRequest.
//   * the client locks settlement presentation before ChooseEvent because NotifyTheatre6SettleData may precede its
//     response; terminal pushes therefore always trail the response that caused them.
//
// Local policy (authorized, no captured constants): retail's server-side offer weights, task reward composition, pool
// candidate selection and extra-floor timing are absent from client sources. Every such decision below is derived from
// the current shared tables (authored weights, quality lists, appointed ids, pool types, ExFloor flags).
internal static partial class Theatre6Module
{
    // Retail CodeText ids (EN bytes/share/text/CodeText.json, 20423001..20423107).
    internal const int ErrAlreadySettle = 20423098;
    internal const int ErrChooseRoomStatus = 20423034;
    internal const int ErrChooseTaskOutOfLimit = 20423025;
    internal const int ErrExFloorState = 20423105;
    internal const int ErrExFloorWaiting = 20423106;
    internal const int ErrFightNotOffered = 20423036;
    internal const int ErrGoldNotEnough = 20423018;
    internal const int ErrMonsterAlreadySet = 20423037;
    internal const int ErrMonsterNotSet = 20423038;
    internal const int ErrNoCurrentMode = 20423013;
    internal const int ErrNoCurrentRoom = 20423014;
    internal const int ErrPlayModeNotUnlocked = 20423050;
    internal const int ErrRoomType = 20423015;
    internal const int ErrSlotId = 20423044;
    internal const int ErrStoryId = 20423087;
    internal const int ErrStoryNotUnlocked = 20423086;
    internal const int ErrTaskNotOffered = 20423016;
    internal const int ErrTaskRefreshLimit = 20423017;

    private const int ModeGamePlay = 1;
    private const int ModeStory = 2;

    private const int RoomChooseTask = 1;
    private const int RoomChooseOption = 2;
    private const int RoomBattleShop = 3;
    private const int RoomMonster = 4;
    private const int RoomBoss = 5;
    private const int RoomAvg = 6;

    private const int RoomStatusTaskRecv = 1;
    private const int RoomStatusChooseEvent = 2;
    private const int RoomStatusTaskFinish = 3;
    private const int RoomStatusChooseRoomFinish = 4;
    private const int RoomStatusFinished = 5;

    private const int TaskStateInit = 0;
    private const int TaskStateActivated = 1;
    private const int TaskStateAchieved = 2;

    private const int RewardGoods = 1;
    private const int RewardSan = 2;
    private const int RewardCoin = 3;
    private const int RewardBuffPool = 4;
    private const int RewardSkillPool = 5;
    private const int RewardHealth = 6;
    private const int RewardFight = 7;
    private const int RewardAvg = 100;

    private const int EffectSkillLevelUp = 12;
    private const int SanTypeDeath = 4;
    private const int TriggerChoice = 1;
    private const int TriggerTaskFinish = 11;

    private static class ProgressionTables
    {
        internal static readonly Lazy<Dictionary<int, Theatre6StageTable>> Stage = new(() => TableReaderV2.Parse<Theatre6StageTable>().ToDictionary(row => row.Id));
        internal static readonly Lazy<Dictionary<int, Theatre6StageFloorTable>> Floor = new(() => TableReaderV2.Parse<Theatre6StageFloorTable>().ToDictionary(row => row.Id));
        internal static readonly Lazy<Dictionary<int, Theatre6StageRoomTable>> Room = new(() => TableReaderV2.Parse<Theatre6StageRoomTable>().ToDictionary(row => row.Id));
        internal static readonly Lazy<Dictionary<int, Theatre6StageChooseTable>> Choose = new(() => TableReaderV2.Parse<Theatre6StageChooseTable>().ToDictionary(row => row.Id));
        internal static readonly Lazy<Dictionary<int, Theatre6StageChoosePoolTable>> ChoosePool = new(() => TableReaderV2.Parse<Theatre6StageChoosePoolTable>().ToDictionary(row => row.Id));
        internal static readonly Lazy<Dictionary<int, Theatre6StageChooseGroupTable>> ChooseGroup = new(() => TableReaderV2.Parse<Theatre6StageChooseGroupTable>().ToDictionary(row => row.Id));
        internal static readonly Lazy<Dictionary<int, Theatre6StageTaskTable>> Task = new(() => TableReaderV2.Parse<Theatre6StageTaskTable>().ToDictionary(row => row.Id));
        internal static readonly Lazy<Dictionary<int, Theatre6StageTaskGroupTable>> TaskGroup = new(() => TableReaderV2.Parse<Theatre6StageTaskGroupTable>().ToDictionary(row => row.Id));
        internal static readonly Lazy<Dictionary<int, Theatre6StageTaskQualityTable>> TaskQuality = new(() => TableReaderV2.Parse<Theatre6StageTaskQualityTable>().ToDictionary(row => row.Id));
        internal static readonly Lazy<Dictionary<int, Theatre6StageFightTable>> Fight = new(() => TableReaderV2.Parse<Theatre6StageFightTable>().ToDictionary(row => row.Id));
        internal static readonly Lazy<Dictionary<int, Theatre6StageFightGroupTable>> FightGroup = new(() => TableReaderV2.Parse<Theatre6StageFightGroupTable>().ToDictionary(row => row.Id));
        internal static readonly Lazy<Dictionary<int, Theatre6StageDifficultyTable>> Difficulty = new(() => TableReaderV2.Parse<Theatre6StageDifficultyTable>().ToDictionary(row => row.Id));
        internal static readonly Lazy<Dictionary<int, Theatre6StageDifficultyGroupTable>> DifficultyGroup = new(() => TableReaderV2.Parse<Theatre6StageDifficultyGroupTable>().ToDictionary(row => row.Id));
        internal static readonly Lazy<Dictionary<int, Theatre6StoryLineTable>> StoryLine = new(() => TableReaderV2.Parse<Theatre6StoryLineTable>().ToDictionary(row => row.Id));
        internal static readonly Lazy<Dictionary<int, Theatre6StageBuffTable>> Buff = new(() => TableReaderV2.Parse<Theatre6StageBuffTable>().ToDictionary(row => row.Id));
        internal static readonly Lazy<Dictionary<int, Theatre6StageSanTable>> San = new(() => TableReaderV2.Parse<Theatre6StageSanTable>().ToDictionary(row => row.Id));
        internal static readonly Lazy<Dictionary<int, Theatre6CharacterTable>> Character = new(() => TableReaderV2.Parse<Theatre6CharacterTable>().ToDictionary(row => row.Id));
    }

    // Columns whose entire source column is blank are omitted, and every partially-blank carrier is generated nullable.
    private static int N(int? value) => value ?? 0;
    private static List<int> AsList(List<int>? values) => values ?? new List<int>();

    // ---------------------------------------------------------------- request handlers

    [RequestPacketHandler("Theatre6PlayModeStartFightRequest")]
    public static void PlayModeStartFight(Session session, Packet.Request packet) =>
        Handle<Theatre6PlayModeStartFightRequest, Theatre6PlayModeStartFightResponse>(session, packet, static (mutation, request, response) =>
        {
            Player player = mutation.Player;
            // Core's authored availability gate (throws CodePlayModeNotUnlocked); no requiredMode so a start may switch
            // between the story and gameplay runs.
            EnsureAvailable(mutation.Session);
            Require(HasUnlock(player, Cfg("PlayModeConditionId")), ErrPlayModeNotUnlocked);

            Require(ProgressionTables.Character.Value.TryGetValue(request.CharacterId, out Theatre6CharacterTable? character), ErrPlayModeNotUnlocked);
            Require(HasUnlock(player, character!.ConditionId), ErrPlayModeNotUnlocked);
            // Theatre6Character authors a single FashionIds column, so the generated member is a scalar. The client
            // sends its saved preference which, with only one authored fashion, can legitimately be absent (nil -> 0);
            // an absent value resolves to the authored default that Core's archive validation and Combat's actor
            // projection both expect.
            Require(request.FashionId == 0 || character.FashionIds == request.FashionId, ErrPlayModeNotUnlocked);
            int fashionId = request.FashionId != 0 ? request.FashionId : character.FashionIds;
            List<int> tagBuffs = AsList(character.TagBuffIds);
            int initBuffIndex = tagBuffs.IndexOf(request.InitBuffId);
            Require(request.InitBuffId == 0 || initBuffIndex >= 0, ErrPlayModeNotUnlocked);
            // BuffConditionIds is index-aligned with TagBuffIds; EN XUiGridTheatre6Buff gates the initial-buff choice
            // on that condition, so the server rejects a locked buff instead of trusting the client's selection.
            List<int> buffConditions = AsList(character.BuffConditionIds);
            if (initBuffIndex >= 0 && initBuffIndex < buffConditions.Count)
                Require(HasUnlock(player, buffConditions[initBuffIndex]), ErrPlayModeNotUnlocked);

            (int difficultyId, int stageId) = ResolveDifficulty(player, character, request.DifficultyId);
            Theatre6RunState run = StartRun(mutation, ModeGamePlay, request.CharacterId, fashionId, request.InitBuffId, stageId, difficultyId);
            response.PlayModeDataDb = BuildModeData(run);
        }, static (response, code) => response.Code = code);

    [RequestPacketHandler("Theatre6EnterStoryLineRequest")]
    public static void EnterStoryLine(Session session, Packet.Request packet) =>
        Handle<Theatre6EnterStoryLineRequest, Theatre6EnterStoryLineResponse>(session, packet, static (mutation, request, response) =>
        {
            Player player = mutation.Player;
            EnsureAvailable(mutation.Session);
            Theatre6State state = mutation.State;
            Require(ProgressionTables.StoryLine.Value.TryGetValue(request.StoryLineId, out Theatre6StoryLineTable? line), ErrStoryId);

            List<int> stageIds = AsList(line!.StageIds);
            List<int> consumeCounts = AsList(line.ConsumeCounts);
            Require(stageIds.Count > 0, ErrStoryId);
            Require(HasUnlock(player, line.ConditionId), ErrStoryNotUnlocked);

            Theatre6StoryLineState lineState = state.StorySave.StoryLineDatas.FirstOrDefault(x => x.StoryLineId == line.Id)
                ?? new Theatre6StoryLineState { StoryLineId = line.Id };
            int passed = Math.Clamp(lineState.StageIndex, 0, stageIds.Count);
            int stageIndex;
            if (passed >= stageIds.Count)
            {
                // The client only sends ReplayStageId once every stage of the line has been passed.
                stageIndex = stageIds.IndexOf(request.ReplayStageId);
                Require(stageIndex >= 0, ErrStoryId);
            }
            else
            {
                stageIndex = passed;
            }

            // The common storyline deliberately leaves UseCharacter blank and authors its actor in StageCharacter.
            int characterId = N(line.UseCharacter) > 0 ? N(line.UseCharacter) : N(line.StageCharacter);
            Require(ProgressionTables.Character.Value.TryGetValue(characterId, out Theatre6CharacterTable? character), ErrStoryNotUnlocked);
            // Single authored FashionIds column -> scalar; the story path uses the authored default.
            int fashionId = character!.FashionIds;
            Require(fashionId > 0, ErrPlayModeNotUnlocked);

            int stageId = stageIds[stageIndex];
            Require(ProgressionTables.Stage.Value.ContainsKey(stageId), ErrStoryId);

            // Paid story stages are charged on entry: EN XUiTheatre6ChooseCharacter spends config.ConsumeCounts[stageIndex].
            int price = stageIndex < consumeCounts.Count ? consumeCounts[stageIndex] : 0;
            bool bought = lineState.BuyIndex.Contains(stageIndex);
            if (price > 0 && !bought)
                mutation.Cost(Cfg("Theatre6Coin"), price);

            if (!state.StorySave.StoryLineDatas.Contains(lineState))
                state.StorySave.StoryLineDatas.Add(lineState);
            if (price > 0 && !bought)
            {
                lineState.BuyIndex.Add(stageIndex);
                lineState.IsBuy = true;
            }
            lineState.IsCompletedBefore = passed >= stageIds.Count;
            lineState.StageIndex = Math.Max(lineState.StageIndex, passed);

            Theatre6RunState run = StartRun(mutation, ModeStory, character.Id, fashionId, 0, stageId, 0, line.Id);
            response.StoryModeDataDb = BuildModeData(run);
            response.StoryLineData = ToWireStoryLine(lineState);
        }, static (response, code) => response.Code = code);

    [RequestPacketHandler("Theatre6RefreshTaskRequest")]
    public static void RefreshTask(Session session, Packet.Request packet) =>
        Handle<Theatre6RefreshTaskRequest, Theatre6RefreshTaskResponse>(session, packet, static (mutation, request, response) =>
        {
            Theatre6RunState run = CurrentRun(mutation);
            Theatre6RoomDataDb room = RequireRoom(run);
            Require(room.RoomType is RoomChooseTask or RoomChooseOption, ErrRoomType);
            Require(room.ChooseRoomStatus == RoomStatusTaskRecv, ErrChooseRoomStatus);
            Require(request.Index >= 1 && request.Index <= run.TaskSlotData.Count, ErrSlotId);

            Theatre6TaskSlotData slot = run.TaskSlotData[request.Index - 1];
            Require(slot.Index == request.Index, ErrSlotId);
            Require(ProgressionTables.TaskGroup.Value.TryGetValue(run.TaskGroupId, out Theatre6StageTaskGroupTable? group), ErrRoomType);
            Require(slot.RefreshCount < N(group!.MaxRefresh), ErrTaskRefreshLimit);

            // EN XUiGridTheatre6TaskDetail: cost = RefreshStartGold + (RefreshCount - freeRefresh) * RefreshAddGold.
            int freeRefresh = N(group.FreeRefresh) + FreeRefreshFromBuffs(run);
            int paid = slot.RefreshCount - freeRefresh;
            int cost = paid < 0 ? 0 : N(group.RefreshStartGold) + paid * N(group.RefreshAddGold);
            Require(run.GoldAmount >= cost, ErrGoldNotEnough);

            int oldTaskId = slot.TaskId;
            HashSet<int> offered = run.StageTasks.Keys.Where(taskId => taskId != oldTaskId).ToHashSet();
            Theatre6StageTaskTable? newTask = PickSingleTask(run, group, offered);
            Require(newTask is not null, ErrTaskNotOffered);

            if (oldTaskId > 0)
                run.StageTasks.Remove(oldTaskId);
            if (cost > 0)
                AddGold(mutation, run, -cost);
            AddTask(mutation, run, newTask!, slot, slot.RefreshCount + 1);

            TickEffects(mutation, run, "task");
            response.NewTask = run.StageTasks[newTask!.Id];
            response.RefreshCount = slot.RefreshCount;
        }, static (response, code) => response.Code = code);

    [RequestPacketHandler("Theatre6ConfirmTaskRequest")]
    public static void ConfirmTask(Session session, Packet.Request packet) =>
        Handle<Theatre6ConfirmTaskRequest, Theatre6ConfirmTaskResponse>(session, packet, static (mutation, request, response) =>
        {
            Theatre6RunState run = CurrentRun(mutation);
            Theatre6RoomDataDb room = RequireRoom(run);
            Require(room.RoomType is RoomChooseTask or RoomChooseOption, ErrRoomType);
            Require(room.ChooseRoomStatus == RoomStatusTaskRecv, ErrChooseRoomStatus);
            RequireOverflowResolved(run);
            // EN XUiTheatre6RoomChooseTask reads the active task group's ChooseNum (the choose group only names the
            // first/loop task groups), so selection cardinality comes from TaskGroupId, not from the room's group.
            Require(ProgressionTables.TaskGroup.Value.TryGetValue(run.TaskGroupId, out Theatre6StageTaskGroupTable? taskGroup), ErrRoomType);
            int chooseNum = Math.Max(1, N(taskGroup!.ChooseNum));

            List<int> chosen = request.TaskIds ?? new List<int>();
            Require(chosen.Count == Math.Max(1, chooseNum), ErrChooseTaskOutOfLimit);
            Require(chosen.Distinct().Count() == chosen.Count, ErrChooseTaskOutOfLimit);
            foreach (int taskId in chosen)
            {
                Require(run.StageTasks.TryGetValue(taskId, out Theatre6TaskData? task) && task!.TaskState == TaskStateInit, ErrTaskNotOffered);
                Require(task!.SlotIndex >= 1 && task.SlotIndex <= run.TaskSlotData.Count, ErrTaskNotOffered);
            }

            foreach (int taskId in chosen)
                run.StageTasks[taskId].TaskState = TaskStateActivated;

            if (room.RoomType == RoomChooseTask)
            {
                // Task rooms have no choose phase: confirmation completes the room.
                room.ChooseRoomStatus = RoomStatusFinished;
                TickEffects(mutation, run, "task");
                response.NextRoomStatus = room.ChooseRoomStatus;
                AdvanceRoom(mutation, run, afterResponse: true);
                return;
            }

            room.ChooseRoomStatus = RoomStatusChooseEvent;
            if (room.CurChooseId <= 0)
                SetChooseStep(mutation, run, room, room.CurChoosePoolIdx);

            TickEffects(mutation, run, "task");
            response.NextRoomStatus = room.ChooseRoomStatus;
        }, static (response, code) => response.Code = code);

    [RequestPacketHandler("Theatre6RecvTaskRoomRewardRequest")]
    public static void RecvTaskRoomReward(Session session, Packet.Request packet) =>
        Handle<Theatre6RecvTaskRoomRewardRequest, Theatre6RecvTaskRoomRewardResponse>(session, packet, static (mutation, _, response) =>
        {
            Theatre6RunState run = CurrentRun(mutation);
            Theatre6RoomDataDb room = RequireRoom(run);
            Require(room.RoomType is RoomChooseTask or RoomChooseOption, ErrRoomType);
            Require(room.ChooseRoomStatus is RoomStatusTaskFinish or RoomStatusChooseRoomFinish, ErrChooseRoomStatus);
            // EN XUiTheatre6RoomTaskSettlement blocks leaving while a force-sell overflow queue is unresolved.
            RequireOverflowResolved(run);
            // A room with an unresolved authored battle cannot advance its task round either: XUiTheatre6RoomTaskSettlement
            // also runs CheckFightReconnect on start, so a client in this state is driven into the stored attempt rather
            // than to the claim screen; a request here with a binding is a stale or hostile duplicate.
            Require(room.FightId <= 0 || room.SelectedMonsterId <= 0, ErrChooseRoomStatus);
            Theatre6StageChooseGroupTable? group = null;
            if (room.RoomType == RoomChooseOption)
                Require(ProgressionTables.ChooseGroup.Value.TryGetValue(room.ChooseGroupId, out group), ErrRoomType);

            List<Theatre6TaskData> claimed = run.StageTasks.Values
                .Where(task => task.TaskState is TaskStateActivated or TaskStateAchieved)
                .OrderBy(task => task.SlotIndex).ThenBy(task => task.TaskId).ToList();
            Require(claimed.Count > 0, ErrChooseRoomStatus);
            SettleActivatedTasks(mutation, run);

            // A finished chain ends the room; otherwise the loop round's fresh offers must be selectable again, which
            // is exactly the TaskRecv status the client dispatches to the task-selection UI.
            room.ChooseRoomStatus = room.RoomType == RoomChooseTask || IsChooseChainDone(room)
                ? RoomStatusFinished
                : RoomStatusTaskRecv;

            GenerateTaskRound(mutation, run, group is not null ? TaskGroupId(group, first: false) : run.TaskGroupId);

            TickEffects(mutation, run, "task");
            // Mission-completion effects can spend sanity/vitality, so the claim can end the run too.
            bool terminal = CheckTerminal(mutation, run);
            response.NextRoomStatus = terminal ? RoomStatusTaskFinish : room.ChooseRoomStatus;
            response.TaskSlotData = CopySlots(run.TaskSlotData);
            response.NewStageTasks = CopyTasks(run.StageTasks);
            response.TaskGroupId = run.TaskGroupId;

            if (!terminal && room.ChooseRoomStatus == RoomStatusFinished && room.FightId <= 0)
                AdvanceRoom(mutation, run, afterResponse: true);
        }, static (response, code) => response.Code = code);

    [RequestPacketHandler("Theatre6ChooseEventRequest")]
    public static void ChooseEvent(Session session, Packet.Request packet) =>
        Handle<Theatre6ChooseEventRequest, Theatre6ChooseEventResponse>(session, packet, static (mutation, request, response) =>
        {
            Theatre6RunState run = CurrentRun(mutation);
            Theatre6RoomDataDb room = RequireRoom(run);
            Require(room.RoomType == RoomChooseOption, ErrRoomType);
            Require(room.ChooseRoomStatus == RoomStatusChooseEvent, ErrChooseRoomStatus);
            Require(request.SelectType is 1 or 2, ErrChooseRoomStatus);
            // EN XUiTheatre6RoomEitheror:OnEndChoose blocks the choice while a force-sell overflow queue is unresolved.
            RequireOverflowResolved(run);
            Require(ProgressionTables.Choose.Value.ContainsKey(room.CurChooseId), ErrChooseRoomStatus);
            // An authored battle bound to this room must be settled before the chain may continue: with a binding
            // present no offer is resolved, nothing is applied and no cursor advances. A connected client is inside
            // Loading/native combat and never sends this, and a reconnected client cannot either, because
            // XUiTheatre6RoomEitheror:OnStart calls CheckFightReconnect() and opens UiTheatre6Loading whenever the room
            // carries FightId + FightSeed + SelectedMonsterId + non-empty FightRewards — i.e. the reconnect is driven
            // back into the stored attempt instead of choosing. So this is only reachable by a duplicate or hostile
            // request, and it is rejected without mutation (no fake success that would desync the client's own
            // CurChoosePoolIdx counter).
            Require(room.FightId <= 0 || room.SelectedMonsterId <= 0, ErrChooseRoomStatus);

            HashSet<int> knownBuffs = run.Buffs.Keys.ToHashSet();
            bool left = request.SelectType == 1;
            // Apply exactly the offer the player was shown: amounts, resolved skill/relic/buff identities and nested
            // fight loot come from the frozen stored preview, never from a fresh draw. Authored narration steps may
            // legitimately carry no rewards on both sides and still advance the chain.
            List<Theatre6RewardData> offered = CopyRewards(left ? room.LeftRewards : room.RightRewards);

            // Choice-counted buffs decrement on this boundary and must do so before the branch grants new ones
            // (a DurationType 3 buff granted below would otherwise be expired by the tick in the same transition).
            TickEffects(mutation, run, "choice");

            List<Theatre6RewardData> applied = new();
            foreach (Theatre6RewardData reward in offered)
            {
                if (reward.RewardType == RewardFight)
                {
                    // EN consumer reads FightId off this entry and loads native combat; its rewards settle separately.
                    // A binding can only be created here when the room carries no unresolved fight (fenced above).
                    room.FightId = reward.FightId;
                    room.LastFightId = reward.FightId;
                    room.SelectedMonsterId = reward.MonsterId;
                    room.FightSeed = RollFightSeed(run);
                    room.FightRewards = CopyRewards(reward.FightRewards);
                    applied.Add(CopyReward(reward));
                    continue;
                }

                applied.AddRange(ApplyRewardsAndProgress(mutation, run, new[] { reward }));
            }

            foreach (Theatre6TaskData task in run.StageTasks.Values)
            {
                if (task.TaskState != TaskStateActivated)
                    continue;
                RefreshTaskProgress(task);
                if (IsTaskComplete(task))
                    MarkTaskAchieved(mutation, run, task);
            }

            bool taskAchieved = run.StageTasks.Values.Any(task => task.TaskState == TaskStateAchieved);
            room.CurChoosePoolIdx++;
            bool isEnd = IsChooseChainDone(room);
            if (!isEnd)
                SetChooseStep(mutation, run, room, room.CurChoosePoolIdx);

            room.ChooseRoomStatus = isEnd
                ? RoomStatusChooseRoomFinish
                : taskAchieved ? RoomStatusTaskFinish : RoomStatusChooseEvent;

            TriggerEffects(mutation, run, TriggerChoice, amount: 1, parameter: request.SelectType);
            // Permanent missions: one settled choice resolution (condition 136004 counters 140125-127/140215-217).
            RecordMetaProgress(mutation, "Choice");

            // A choice can end the run: the branch rewards and their buff effects spend sanity/vitality. The terminal
            // boundary runs here, before this response advertises another offer, and Core's settled-run gate then
            // rejects any further request for the run.
            bool terminal = CheckTerminal(mutation, run);

            int sanDeathBuffId = SanDeathBuffId(run);
            List<Theatre6LiveBuffState> addedBuffs = NewBuffs(run, knownBuffs);
            response.NextChooseId = terminal || isEnd ? 0 : room.CurChooseId;
            response.StageTasks = CopyTasks(run.StageTasks);
            response.LeftRewards = CopyRewards(room.LeftRewards);
            response.RightRewards = CopyRewards(room.RightRewards);
            response.NextRoomStatus = terminal ? RoomStatusChooseEvent : room.ChooseRoomStatus;
            response.IsEnd = !terminal && isEnd;
            response.RewardGoodsList = applied;
            response.SkillUpBuffDatas = addedBuffs.Where(buff => IsSkillLevelUpBuff(buff.BuffId)).Select(ToWireLiveBuff).ToList();
            response.SanBuffDatas = sanDeathBuffId > 0
                ? addedBuffs.Where(buff => buff.BuffId == sanDeathBuffId).Select(ToWireLiveBuff).ToList()
                : new List<Theatre6LiveBuffData>();
        }, static (response, code) => response.Code = code);

    [RequestPacketHandler("Theatre6FightRoomSlideRequest")]
    public static void FightRoomSlide(Session session, Packet.Request packet) =>
        Handle<Theatre6FightRoomSlideRequest, Theatre6FightRoomSlideResponse>(session, packet, static (mutation, request, response) =>
        {
            Theatre6RunState run = CurrentRun(mutation);
            Theatre6RoomDataDb room = RequireRoom(run);
            Require(room.RoomType is RoomMonster or RoomBoss, ErrRoomType);
            Require(room.SelectedMonsterId == 0, ErrMonsterAlreadySet);
            Require(request.SelectType is 1 or 2, ErrRoomType);
            Require(ProgressionTables.Fight.Value.TryGetValue(room.FightId, out Theatre6StageFightTable? fight), ErrFightNotOffered);

            // EN XUiTheatre6RoomBoss: left binds EasyMonsterId; right binds HardMonsterId for boss rooms only
            // (monster fight rows have no hard variant, and the client then shows the easy monster for both sides).
            int hardMonsterId = N(fight!.HardMonsterId);
            bool hard = request.SelectType == 2 && room.RoomType == RoomBoss && hardMonsterId > 0;
            int monsterId = hard ? hardMonsterId : N(fight.EasyMonsterId);
            Require(monsterId > 0, ErrMonsterNotSet);

            room.SelectedMonsterId = monsterId;
            room.FightSeed = RollFightSeed(run);
            room.FightRewards = BuildFightRewards(mutation, run, fight, hard);
            room.LastFightId = room.FightId;
            // No TickEffects("fight") here: the slide is battle entry, and battle-counted buffs (DurationType 2)
            // decrement exactly once per battle at the settlement boundary in OnFightSettled.
        }, static (response, code) => response.Code = code);

    [RequestPacketHandler("Theatre6EndAvgRoomRequest")]
    public static void EndAvgRoom(Session session, Packet.Request packet) =>
        Handle<Theatre6EndAvgRoomRequest, Theatre6EndAvgRoomResponse>(session, packet, static (mutation, _, _) =>
        {
            Theatre6RunState run = CurrentRun(mutation);
            Require(RequireRoom(run).RoomType == RoomAvg, ErrRoomType);
            AdvanceRoom(mutation, run, afterResponse: true);
        }, static (response, code) => response.Code = code);

    [RequestPacketHandler("Theatre6ExFloorConfirmRequest")]
    public static void ExFloorConfirm(Session session, Packet.Request packet) =>
        Handle<Theatre6ExFloorConfirmRequest, Theatre6ExFloorConfirmResponse>(session, packet, static (mutation, request, response) =>
        {
            Theatre6RunState run = CurrentRun(mutation);
            Require(!run.Settled, ErrAlreadySettle);
            Require(run.WaitingExFloorConfirm, ErrExFloorWaiting);
            Require(TryCurrentRoom(run, out Theatre6StageTable? stage, out _, out _, out _), ErrRoomType);
            List<int> floors = AsList(stage!.FloorIds);
            Require(run.CurFloorIdx + 1 < floors.Count
                && ProgressionTables.Floor.Value.TryGetValue(floors[run.CurFloorIdx + 1], out Theatre6StageFloorTable? pendingFloor)
                && N(pendingFloor!.ExFloor) == 1, ErrExFloorState);

            run.WaitingExFloorConfirm = false;
            if (request.IsEnter)
            {
                // The delayed transition deliberately left the run on the previous floor so the client's prompt gate
                // sees the persisted waiting flag; accepting is where the extra floor and its boundary are entered.
                EnterPendingExtraFloor(mutation, run);
                response.ModeDataDb = BuildModeData(run);
                // Documented ordering: the response carries the extra floor snapshot, a NewFloor push follows it.
                PushRoom(mutation, run, newFloor: true, afterResponse: true);
                return;
            }

            // Decline ends the run on the cleared stage: EN XTheatre6ControlNetwork reads SettleData + StoryModeSaveDb here.
            TickEffects(mutation, run, "end");
            RecalculateScore(mutation, run);
            response.SettleData = SettleRun(mutation, run, isWin: true, pushSettle: false);
            response.StoryModeSaveDb = ToWire(mutation.State.StorySave);
        }, static (response, code) => response.Code = code);

    // ---------------------------------------------------------------- run engine

    internal static Theatre6RunState StartRun(Mutation m, int modeId, int characterId, int fashionId, int initBuffId, int stageId, int difficultyId = 0, int storyLineId = 0)
    {
        Theatre6State state = m.State;
        Require(ProgressionTables.Stage.Value.TryGetValue(stageId, out Theatre6StageTable? stage), ErrRoomType);

        // The extra-floor gate is authorised by "this difficulty was cleared before". A cleared difficulty replays
        // through StageDifficulty.NewStageIds — the variant that carries the ExFloor floor — so the durable record to
        // test is the difficulty's base stage, i.e. exactly the record that selected the alternate stage above; reading
        // the resolved alternate stage would never be set and would silently suppress the authored prompt.
        int clearedStageId = stageId;
        if (difficultyId > 0
            && ProgressionTables.Difficulty.Value.TryGetValue(difficultyId, out Theatre6StageDifficultyTable? difficultyRow)
            && N(difficultyRow.StageId) > 0)
            clearedStageId = N(difficultyRow.StageId);

        Theatre6RunState run = new()
        {
            RunId = state.NextRunId++,
            ModeId = modeId,
            StageId = stageId,
            DifficultyId = difficultyId,
            StoryLineId = storyLineId,
            CurFloorIdx = 0,
            File = new Theatre6FileState { CharacterId = characterId, FashionId = fashionId },
            MaxHealth = Math.Max(1, N(stage!.HpNum)),
            MaxSan = Math.Max(1, N(stage.BaseSan)),
            StorySave = CloneStorySave(state.StorySave),
            PassStageRecords = new Dictionary<int, int>(state.PassStageRecords),
            PassDiffRecords = new Dictionary<int, int>(state.PassDiffRecords),
            HasClearedBeforeExFloor = state.PassStageRecords.TryGetValue(clearedStageId, out int cleared) && cleared > 0
        };
        run.CurHealth = run.MaxHealth;
        run.InitHealth = run.MaxHealth;
        run.CurSan = run.MaxSan;
        run.MinSan = run.MaxSan;

        InitializeCharacter(m, run, initBuffId);
        TickEffects(m, run, "start");

        state.CurrentMode = modeId;
        state.ActiveRuns[modeId] = run;
        // The start response carries the complete snapshot; the client opens the first room from it.
        MaterializeRoom(m, run);
        GrantFloorBuffPools(m, run, FloorStartBuffPoolIds(run));
        PrimeBossRoom(m, run);
        return run;
    }

    private static List<int> FloorStartBuffPoolIds(Theatre6RunState run)
    {
        if (!TryCurrentRoom(run, out _, out Theatre6StageFloorTable? floor, out _, out _))
            return new List<int>();
        return AsList(floor!.StartBuffPoolIds);
    }

    /// <summary>Entering a new floor: expire the outgoing floor's FloorScoped (DurationType 6) buffs before granting the
    /// new floor's pools, otherwise the boundary tick deletes what was just granted. Only used on real floor changes
    /// (run start has no outgoing floor, and the lagged extra-floor transition calls it when the player accepts).</summary>
    private static void EnterFloorBoundary(Mutation m, Theatre6RunState run)
    {
        TickEffects(m, run, "floor");
        GrantFloorBuffPools(m, run, FloorStartBuffPoolIds(run));
        PrimeBossRoom(m, run);
    }

    /// <summary>Freezes the current floor's final boss room onto the run before any of its rooms are entered: the
    /// client's chapter preview and top-stage boss button read it as soon as the floor starts. Floors without a boss
    /// room keep the previous value so the client never dereferences a null snapshot.</summary>
    private static void PrimeBossRoom(Mutation m, Theatre6RunState run)
    {
        if (!TryCurrentRoom(run, out _, out Theatre6StageFloorTable? floor, out _, out _))
            return;
        List<int> rooms = AsList(floor!.RoomIds);
        for (int i = rooms.Count - 1; i >= 0; i--)
        {
            if (!ProgressionTables.Room.Value.TryGetValue(rooms[i], out Theatre6StageRoomTable? roomCfg) || N(roomCfg.Type) != RoomBoss)
                continue;
            Theatre6StageFightTable? fight = ResolveFight(run, N(roomCfg.Values), null);
            if (fight is null)
                return;
            run.BossRoomDataDb = new Theatre6RoomDataDb
            {
                RoomIdx = i,
                RoomType = RoomBoss,
                FightId = fight.Id,
                LastFightId = fight.Id,
                FightSeed = RollFightSeed(run),
                SelectedMonsterId = 0,
                FightRewards = BuildFightRewards(m, run, fight, hard: false)
            };
            return;
        }
    }

    /// <summary>Announces the room at the current pointer. Run start never calls this.</summary>
    internal static void EnterRoom(Mutation m, Theatre6RunState run, bool afterResponse = false)
    {
        MaterializeRoom(m, run);
        PushRoom(m, run, newFloor: false, afterResponse);
    }

    internal static void AdvanceRoom(Mutation m, Theatre6RunState run, bool afterResponse = false)
    {
        if (run.Settled)
            return;
        Require(TryCurrentRoom(run, out Theatre6StageTable? stage, out Theatre6StageFloorTable? floor, out _, out _), ErrRoomType);

        List<int> floors = AsList(stage!.FloorIds);
        List<int> rooms = AsList(floor!.RoomIds);
        int nextRoomIdx = (run.CurrentRoomDataDb?.RoomIdx ?? -1) + 1;
        bool newFloor = false;
        if (nextRoomIdx >= rooms.Count)
        {
            if (run.CurFloorIdx + 1 >= floors.Count)
            {
                // Every authored floor and room are done: the run is a win.
                TickEffects(m, run, "end");
                RecalculateScore(m, run);
                SettleRun(m, run, isWin: true, pushSettle: true);
                return;
            }

            int pendingFloorIdx = run.CurFloorIdx + 1;
            bool extraFloorPending = ProgressionTables.Floor.Value.TryGetValue(floors[pendingFloorIdx], out Theatre6StageFloorTable? pendingFloor)
                && N(pendingFloor.ExFloor) == 1
                && !run.WaitingExFloorConfirm
                && run.HasClearedBeforeExFloor;

            if (extraFloorPending)
            {
                // XTheatre6Control:CheckEnterExFloorConfirm reads the persisted waiting flag and compares
                // FloorIds[CurFloorIdx + 2], so the run deliberately stays on the current floor (whose last room is
                // already complete) and only publishes the pending flag with a full snapshot. Entering the extra floor
                // — its floor-boundary buff expiry, its StartBuffPoolIds and its boss snapshot — happens on accept, and
                // a reconnect in this state reproduces the same prompt instead of silently entering the optional floor.
                run.WaitingExFloorConfirm = true;
                PushRoom(m, run, newFloor: true, afterResponse);
                return;
            }

            run.CurFloorIdx = pendingFloorIdx;
            nextRoomIdx = 0;
            newFloor = true;
        }

        run.CurrentRoomDataDb = new Theatre6RoomDataDb { RoomIdx = nextRoomIdx, LastFightId = run.CurrentRoomDataDb?.LastFightId ?? 0 };
        MaterializeRoom(m, run);
        if (newFloor)
            EnterFloorBoundary(m, run);
        TickEffects(m, run, "room");
        PushRoom(m, run, newFloor, afterResponse);
    }

    /// <summary>Accepts the pending extra floor: advances the pointer, materialises its first room and runs the floor
    /// boundary the delayed transition skipped.</summary>
    private static void EnterPendingExtraFloor(Mutation m, Theatre6RunState run)
    {
        Require(TryCurrentRoom(run, out Theatre6StageTable? stage, out _, out _, out _), ErrRoomType);
        List<int> floors = AsList(stage!.FloorIds);
        int pendingFloorIdx = run.CurFloorIdx + 1;
        Require(pendingFloorIdx < floors.Count
            && ProgressionTables.Floor.Value.TryGetValue(floors[pendingFloorIdx], out Theatre6StageFloorTable? pendingFloor)
            && N(pendingFloor.ExFloor) == 1, ErrExFloorState);

        int lastFightId = run.CurrentRoomDataDb?.LastFightId ?? 0;
        run.CurFloorIdx = pendingFloorIdx;
        run.CurrentRoomDataDb = new Theatre6RoomDataDb { RoomIdx = 0, LastFightId = lastFightId };
        MaterializeRoom(m, run);
        EnterFloorBoundary(m, run);
        TickEffects(m, run, "room");
    }

    /// <summary>Native combat settlement boundary. Returns the rewards applied for the report the client displayed.</summary>
    internal static List<Theatre6RewardData> OnFightSettled(Mutation m, Theatre6RunState run, bool isWin)
    {
        Theatre6RoomDataDb? room = run.CurrentRoomDataDb;
        Require(room is not null && room.FightId > 0, ErrNoCurrentRoom);
        Require(room!.SelectedMonsterId > 0, ErrMonsterNotSet);
        Require(ProgressionTables.Fight.Value.TryGetValue(room.FightId, out Theatre6StageFightTable? fight), ErrFightNotOffered);

        run.Fights.Add(new Theatre6FightRecordState
        {
            DifficultyType = run.DifficultyId,
            FightResultType = isWin ? 1 : 2,
            FightId = room.FightId,
            MonsterId = room.SelectedMonsterId
        });

        // Permanent missions count completed battles regardless of outcome (condition 136005, parameter 3).
        RecordMetaProgress(m, "Battle", 1, parameter: 3);

        // Battle-counted buffs decrement at the fight boundary, before this fight's loot is granted.
        TickEffects(m, run, "fight");

        List<Theatre6RewardData> applied = isWin
            ? ApplyRewardsAndProgress(m, run, room.FightRewards)
            : ApplyRewardsAndProgress(m, run, BuildDefeatRewards(m, run, fight!));

        room.LastFightId = room.FightId;

        // EN XUiTheatre6RoomEitheror keeps the choice UI open when isEnd is false, so a fight offered mid-chain only
        // settles itself: the room keeps its status, the remaining authored steps continue, and the frozen fight
        // identity is cleared so a later reconnect check cannot replay the battle that just ended.
        bool choiceContinues = room.RoomType == RoomChooseOption && room.ChooseRoomStatus == RoomStatusChooseEvent;
        room.FightId = 0;
        room.FightSeed = 0;
        room.SelectedMonsterId = 0;
        room.FightRewards.Clear();

        // A choose room whose chain is over ends through this battle (the client never opens the claim screen in that
        // path), so its achieved missions are settled here rather than dropped.
        if (!choiceContinues && room.RoomType == RoomChooseOption && room.ChooseRoomStatus >= RoomStatusTaskFinish)
        {
            SettleActivatedTasks(m, run);
            room.ChooseRoomStatus = RoomStatusFinished;
        }

        // The terminal boundary precedes both the mid-chain continuation return and the advance, so a defeat that
        // empties vitality/sanity can never leave the run progressing.
        if (CheckTerminal(m, run))
            return applied;
        if (choiceContinues)
            return applied;

        AdvanceRoom(m, run, afterResponse: true);
        return applied;
    }

    /// <summary>Settles every activated mission of the current round: achieved missions grant their authored reward and
    /// convert surplus materials through Config TaskOverflowGoodFactor, unachieved ones the StageTaskQuality consolation.
    /// The achievement transition itself (and its triggers/counters) is handled by MarkTaskAchieved.</summary>
    private static void SettleActivatedTasks(Mutation m, Theatre6RunState run)
    {
        List<Theatre6TaskData> activated = run.StageTasks.Values
            .Where(task => task.TaskState is TaskStateActivated or TaskStateAchieved)
            .OrderBy(task => task.SlotIndex).ThenBy(task => task.TaskId).ToList();
        if (activated.Count == 0)
            return;

        int overflowFactor = Cfg("TaskOverflowGoodFactor");
        foreach (Theatre6TaskData task in activated)
        {
            RefreshTaskProgress(task);
            if (!IsTaskComplete(task))
            {
                // StageTaskQuality.FailCoin is the authored consolation for an unachieved mission.
                int failCoin = ProgressionTables.TaskQuality.Value.TryGetValue(N(TaskLookup(task.TaskId)?.Quality), out Theatre6StageTaskQualityTable? quality)
                    ? N(quality.FailCoin)
                    : 0;
                if (failCoin > 0)
                    AddGold(m, run, failCoin);
                continue;
            }

            ApplyRewards(m, run, task.RewardGoods);
            int overflow = task.GoodsSlots.Sum(slot => Math.Max(0, slot.Amount - slot.NeedNum));
            if (overflow > 0 && overflowFactor > 0)
                AddGold(m, run, overflow * overflowFactor);
            MarkTaskAchieved(m, run, task);
        }
    }

    /// <summary>Credits materials already added to run.Goods to the offered task slots.</summary>
    internal static void ProgressTasks(Mutation m, Theatre6RunState run, int goodsId, int amount)
    {
        if (goodsId <= 0 || amount == 0 || run.StageTasks.Count == 0)
            return;

        foreach (Theatre6TaskData task in run.StageTasks.Values)
        {
            // Only missions the player actually selected (Activated) track materials; untouched offers stay Init so an
            // unselected task can never be completed, rewarded or counted by the authored selection limit.
            if (task.TaskState != TaskStateActivated)
                continue;

            bool changed = false;
            foreach (Theatre6TaskGoodsData slot in task.GoodsSlots)
            {
                if (slot.GoodsId != goodsId)
                    continue;
                slot.Amount = Math.Max(0, slot.Amount + amount);
                changed = true;
            }

            if (!changed)
                continue;
            RefreshTaskProgress(task);
            if (IsTaskComplete(task))
                MarkTaskAchieved(m, run, task);
        }
    }

    /// <summary>Single place where a stage mission transitions into the achieved state: the client-side display
    /// transition, the authored mission-completion effect trigger (11) and Core's permanent mission counter
    /// (RecordMetaProgress "Task") all fire exactly once per mission here.</summary>
    private static void MarkTaskAchieved(Mutation m, Theatre6RunState run, Theatre6TaskData task)
    {
        if (task.TaskState == TaskStateAchieved)
            return;
        task.TaskState = TaskStateAchieved;
        // EN XTheatre6Control:IsStageTaskFinish reads GoodsState (and ConditionState for conditioned rows), not
        // TaskState, so the completed-key must travel with the same transition.
        task.GoodsState = TaskStateAchieved;
        task.ConditionState = TaskStateAchieved;
        TriggerEffects(m, run, TriggerTaskFinish, amount: 1);
        RecordMetaProgress(m, "Task");
    }

    /// <summary>Terminal boundary: freezes the settlement and pushes it. Returns the frozen settlement.</summary>
    internal static Theatre6SettleData SettleRun(Mutation m, Theatre6RunState run, bool isWin, bool pushSettle = true)
    {
        if (run.Settled)
            return run.SettleData ?? FinalizeRun(m, run, run.IsWin);

        run.WaitingExFloorConfirm = false;
        if (isWin)
            ApplyClearProgress(run);

        Theatre6SettleData settle = FinalizeRun(m, run, isWin);
        if (pushSettle)
            m.Push(new NotifyTheatre6SettleData { SettleData = settle, StoryModeSaveDb = ToWire(m.State.StorySave) }, true);
        return settle;
    }

    private static bool CheckTerminal(Mutation m, Theatre6RunState run)
    {
        // Vitality (Stage.HpNum, spent by DefeatGet type 6) and Sanity (Stage.BaseSan) are both run-ending.
        if (run.CurHealth > 0 && run.CurSan > 0)
            return false;
        TickEffects(m, run, "end");
        RecalculateScore(m, run);
        SettleRun(m, run, isWin: false, pushSettle: true);
        return true;
    }

    private static void ApplyClearProgress(Theatre6RunState run)
    {
        run.PassStageRecords[run.StageId] = run.PassStageRecords.TryGetValue(run.StageId, out int count) ? count + 1 : 1;
        if (run.DifficultyId > 0)
            run.PassDiffRecords[run.DifficultyId] = run.PassDiffRecords.TryGetValue(run.DifficultyId, out int diffCount) ? diffCount + 1 : 1;
        if (ProgressionTables.Difficulty.Value.TryGetValue(run.DifficultyId, out Theatre6StageDifficultyTable? difficulty))
        {
            // A replay clear through the alternate stage also clears the base stage the difficulty declares.
            int baseStageId = N(difficulty.StageId);
            if (baseStageId > 0 && baseStageId != run.StageId)
                run.PassStageRecords[baseStageId] = run.PassStageRecords.TryGetValue(baseStageId, out int baseCount) ? baseCount + 1 : 1;
        }

        if (run.ModeId != ModeStory || !ProgressionTables.StoryLine.Value.TryGetValue(run.StoryLineId, out Theatre6StoryLineTable? line))
            return;

        int stageIndex = AsList(line!.StageIds).IndexOf(run.StageId);
        if (stageIndex < 0)
            return;
        Theatre6StoryLineState state = run.StorySave.StoryLineDatas.FirstOrDefault(x => x.StoryLineId == line.Id)
            ?? new Theatre6StoryLineState { StoryLineId = line.Id };
        if (!run.StorySave.StoryLineDatas.Contains(state))
            run.StorySave.StoryLineDatas.Add(state);
        state.StageIndex = Math.Max(state.StageIndex, stageIndex + 1);
        state.IsCompletedBefore = state.StageIndex >= AsList(line.StageIds).Count;
    }

    // ---------------------------------------------------------------- room materialisation

    private static void MaterializeRoom(Mutation m, Theatre6RunState run)
    {
        Require(TryCurrentRoom(run, out _, out _, out Theatre6StageRoomTable? roomCfg, out int roomIdx), ErrRoomType);
        Theatre6RoomDataDb previous = run.CurrentRoomDataDb ?? new Theatre6RoomDataDb();
        Theatre6RoomDataDb room = new()
        {
            RoomIdx = roomIdx,
            RoomType = N(roomCfg!.Type),
            LastFightId = previous.LastFightId
        };
        // StageRoom authors a single Values column -> scalar.
        int value = N(roomCfg.Values);
        // Install the room before populating it: room-scoped helpers (BuildShop, the boss mirror) write through the run,
        // and installing it afterwards silently sent the shop's rolled shelf into the discarded interim room.
        run.CurrentRoomDataDb = room;

        switch (room.RoomType)
        {
            case RoomChooseTask:
                // No authored stage currently uses a standalone task room; StageRoom.Values names its task group.
                Require(value > 0 && ProgressionTables.TaskGroup.Value.ContainsKey(value), ErrRoomType);
                room.ChooseGroupId = 0;
                room.ChooseRoomStatus = RoomStatusTaskRecv;
                GenerateTaskRound(m, run, value);
                break;

            case RoomChooseOption:
                Require(ProgressionTables.ChooseGroup.Value.TryGetValue(value, out Theatre6StageChooseGroupTable? group), ErrRoomType);
                room.ChooseGroupId = value;
                room.ChooseRoomStatus = RoomStatusTaskRecv;
                GenerateTaskRound(m, run, TaskGroupId(group!, first: true));
                SetChooseStep(m, run, room, 0);
                break;

            case RoomBattleShop:
                Require(value > 0, ErrRoomType);
                room.ShopId = value;
                // The fresh room is already installed as run.CurrentRoomDataDb, so the 3-arg contract overload targets
                // exactly the room being published.
                if (room.ShopGoods.Count == 0 && room.ShopFreshCount == 0)
                    BuildShop(m, run, value);
                break;

            case RoomMonster:
            case RoomBoss:
                Theatre6RoomDataDb? primed = run.BossRoomDataDb;
                if (room.RoomType == RoomBoss && primed is not null && primed.RoomType == RoomBoss && primed.RoomIdx == roomIdx)
                {
                    // Reuse the floor preview's frozen boss identity so the previewed fight is the one played.
                    room.FightId = primed.FightId;
                    room.FightSeed = primed.FightSeed;
                    room.SelectedMonsterId = 0;
                    room.FightRewards = CopyRewards(primed.FightRewards);
                    room.LastFightId = primed.LastFightId;
                    break;
                }

                Theatre6StageFightTable? fight = ResolveFight(run, value, null);
                Require(fight is not null, ErrFightNotOffered);
                room.FightId = fight!.Id;
                room.LastFightId = fight.Id;
                room.FightSeed = RollFightSeed(run);
                room.SelectedMonsterId = 0;
                room.FightRewards = BuildFightRewards(m, run, fight, hard: false);
                // The client reads modelData.BossRoomDataDb.{RoomIdx,FightId,LastFightId} for the chapter preview and
                // the top-stage boss button; only a real boss room may replace the floor's primed final boss.
                if (room.RoomType == RoomBoss)
                    run.BossRoomDataDb = room;
                break;

            case RoomAvg:
                break;

            default:
                Require(false, ErrRoomType);
                break;
        }
    }

    private static void PushRoom(Mutation m, Theatre6RunState run, bool newFloor, bool afterResponse)
    {
        if (newFloor)
        {
            m.Push(new NotifyTheatre6NewFloorData { ModeDataDb = BuildModeData(run) }, afterResponse);
            return;
        }

        m.Push(new NotifyTheatre6NewRoomData
        {
            RoomDataDb = run.CurrentRoomDataDb,
            StageTasks = CopyTasks(run.StageTasks),
            TaskSlotData = CopySlots(run.TaskSlotData),
            TaskGroupId = run.TaskGroupId
        }, afterResponse);
    }

    private static bool TryCurrentRoom(Theatre6RunState run, out Theatre6StageTable? stage, out Theatre6StageFloorTable? floor, out Theatre6StageRoomTable? room, out int roomIdx)
    {
        stage = null;
        floor = null;
        room = null;
        roomIdx = 0;
        if (!ProgressionTables.Stage.Value.TryGetValue(run.StageId, out Theatre6StageTable? stageRow))
            return false;
        List<int> floors = AsList(stageRow.FloorIds);
        if (run.CurFloorIdx < 0 || run.CurFloorIdx >= floors.Count || !ProgressionTables.Floor.Value.TryGetValue(floors[run.CurFloorIdx], out Theatre6StageFloorTable? floorRow))
            return false;
        List<int> rooms = AsList(floorRow.RoomIds);
        roomIdx = run.CurrentRoomDataDb?.RoomIdx ?? 0;
        if (roomIdx < 0 || roomIdx >= rooms.Count || !ProgressionTables.Room.Value.TryGetValue(rooms[roomIdx], out Theatre6StageRoomTable? roomRow))
            return false;

        stage = stageRow;
        floor = floorRow;
        room = roomRow;
        return true;
    }

    private static Theatre6RoomDataDb RequireRoom(Theatre6RunState run) =>
        run.CurrentRoomDataDb ?? throw new InvalidDataException("Theatre6 run has no current room.");

    // ---------------------------------------------------------------- choose offers

    private static void SetChooseStep(Mutation m, Theatre6RunState run, Theatre6RoomDataDb room, int poolIdx)
    {
        Require(ProgressionTables.ChooseGroup.Value.TryGetValue(room.ChooseGroupId, out Theatre6StageChooseGroupTable? group), ErrChooseRoomStatus);
        List<int> poolIds = AsList(group!.ChoosePoolIds);
        Require(poolIdx >= 0 && poolIdx < poolIds.Count, ErrChooseRoomStatus);
        Require(ProgressionTables.ChoosePool.Value.TryGetValue(poolIds[poolIdx], out Theatre6StageChoosePoolTable? pool), ErrChooseRoomStatus);

        // An authored choose line continues across the pool steps that appoint it (XTheatre6StageChooseGroup lists a
        // pool once per line member, and the members carry increasing LineNum inside one ChooseLine).
        if (ProgressionTables.Choose.Value.TryGetValue(room.CurChooseId, out Theatre6StageChooseTable? current) && N(current.ChooseLine) > 0)
        {
            int next = N(current.LineNum) + 1;
            Theatre6StageChooseTable? member = ProgressionTables.Choose.Value.Values
                .Where(row => N(row.ChooseLine) == N(current.ChooseLine) && N(row.LineNum) == next)
                .OrderBy(row => row.Id).FirstOrDefault();
            // The line was entered through its head's condition, so its remaining members continue unconditionally.
            if (member is not null)
            {
                ApplyChoose(m, run, room, member);
                return;
            }
        }

        Theatre6StageChooseTable? picked = PickChoose(run, pool!);
        Require(picked is not null, ErrChooseRoomStatus);
        ApplyChoose(m, run, room, picked!);
    }

    private static void ApplyChoose(Mutation m, Theatre6RunState run, Theatre6RoomDataDb room, Theatre6StageChooseTable choose)
    {
        ProgressionTables.ChooseGroup.Value.TryGetValue(room.ChooseGroupId, out Theatre6StageChooseGroupTable? group);
        room.CurChooseId = choose.Id;
        room.LeftRewards = BuildRewardList(m, run, group,
            AsList(choose.LeftRewardTypes), AsList(choose.LeftRewardTypeIds),
            AsList(choose.LeftRewardMinNums), AsList(choose.LeftRewardMaxNums));
        room.RightRewards = BuildRewardList(m, run, group,
            AsList(choose.RightRewardTypes), AsList(choose.RightRewardTypeIds),
            AsList(choose.RightRewardMinNums), AsList(choose.RightRewardMaxNums));
    }

    /// <summary>Picks the event for one authored pool step. Appointed pools name their candidates; other pools draw from
    /// the normal (non-line) pool by authored quality weights, then bias towards events carrying the appointed reward
    /// types. Weights compose as authored (QualityWeights * NeedRewardWeight * RewardWeights).</summary>
    private static Theatre6StageChooseTable? PickChoose(Theatre6RunState run, Theatre6StageChoosePoolTable pool)
    {
        List<int> appoints = AsList(pool.AppointChooses);
        List<int> appointWeights = AsList(pool.ChooseWeights);
        List<Theatre6StageChooseTable> candidates = new();
        List<int> weights = new();

        if (appoints.Count > 0)
        {
            Dictionary<int, (Theatre6StageChooseTable Row, int Weight)> byLine = new();
            for (int i = 0; i < appoints.Count; i++)
            {
                if (!ProgressionTables.Choose.Value.TryGetValue(appoints[i], out Theatre6StageChooseTable? row))
                    continue;
                int weight = i < appointWeights.Count && appointWeights[i] > 0 ? appointWeights[i] : 1;
                int key = N(row.ChooseLine) > 0 ? N(row.ChooseLine) : -row.Id;
                int lineNum = Math.Max(1, N(row.LineNum));
                if (!byLine.TryGetValue(key, out var existing) || lineNum < Math.Max(1, N(existing.Row.LineNum)))
                    byLine[key] = (row, weight);
                else if (weight > existing.Weight)
                    byLine[key] = (existing.Row, weight);
            }

            foreach (var entry in byLine.Values.OrderBy(entry => entry.Row.Id))
            {
                // The head (lowest LineNum) gates the whole authored chain: a later member's own condition cannot
                // re-open a branch whose head was rejected for this run.
                if (!IsChooseEligible(run, entry.Row))
                    continue;
                candidates.Add(entry.Row);
                weights.Add(Math.Max(1, entry.Weight));
            }
        }

        if (candidates.Count == 0)
        {
            List<int> needQuality = AsList(pool.NeedQualitys);
            List<int> qualityWeights = AsList(pool.QualityWeights);
            List<Theatre6StageChooseTable> normal = ProgressionTables.Choose.Value.Values
                .Where(row => N(row.IsOutNormalPool) == 0 && N(row.ChooseLine) == 0 && IsChooseEligible(run, row))
                .OrderBy(row => row.Id).ToList();
            List<Theatre6StageChooseTable> filtered = needQuality.Count == 0
                ? normal
                : normal.Where(row => needQuality.Contains(N(row.Quality))).ToList();
            if (filtered.Count == 0)
                filtered = normal;

            foreach (Theatre6StageChooseTable row in filtered)
            {
                int index = needQuality.IndexOf(N(row.Quality));
                int weight = index >= 0 && index < qualityWeights.Count && qualityWeights[index] > 0 ? qualityWeights[index] : 1;
                candidates.Add(row);
                weights.Add(weight);
            }
        }

        if (candidates.Count == 0)
            return null;

        List<int> needRewards = AsList(pool.NeedRewards);
        List<int> rewardWeights = AsList(pool.RewardWeights);
        if (needRewards.Count > 0)
        {
            int bias = Math.Max(1, N(pool.NeedRewardWeight));
            for (int i = 0; i < candidates.Count; i++)
            {
                int best = 0;
                for (int type = 0; type < needRewards.Count; type++)
                {
                    if (needRewards[type] <= 0 || !HasRewardType(candidates[i], needRewards[type]))
                        continue;
                    int typeWeight = type < rewardWeights.Count && rewardWeights[type] > 0 ? rewardWeights[type] : 1;
                    best = Math.Max(best, bias * typeWeight);
                }
                if (best > 0)
                    weights[i] *= best;
            }
        }

        int total = weights.Sum();
        if (total <= 0)
            return candidates[Roll(run, candidates.Count)];
        int roll = Roll(run, total);
        for (int i = 0; i < candidates.Count; i++)
        {
            if (roll < weights[i])
                return candidates[i];
            roll -= weights[i];
        }
        return candidates[^1];
    }

    private static bool HasRewardType(Theatre6StageChooseTable choose, int rewardType) =>
        AsList(choose.LeftRewardTypes).Contains(rewardType) || AsList(choose.RightRewardTypes).Contains(rewardType);

    private static bool IsChooseEligible(Theatre6RunState run, Theatre6StageChooseTable choose)
    {
        // StageChoose.NeedBuffs names a buff configuration the run must already carry (live or destroyed instance).
        int needBuff = N(choose.NeedBuffs);
        return needBuff <= 0
            || run.Buffs.Values.Any(buff => buff.BuffId == needBuff)
            || run.DestroyedBuffs.Values.Any(buff => buff.BuffId == needBuff);
    }

    private static bool IsChooseChainDone(Theatre6RoomDataDb room)
    {
        if (!ProgressionTables.ChooseGroup.Value.TryGetValue(room.ChooseGroupId, out Theatre6StageChooseGroupTable? group))
            return true;
        int poolCount = AsList(group.ChoosePoolIds).Count;
        return poolCount == 0 || room.CurChoosePoolIdx >= poolCount;
    }

    // ---------------------------------------------------------------- rewards

    private static List<Theatre6RewardData> ApplyRewardsAndProgress(Mutation m, Theatre6RunState run, IEnumerable<Theatre6RewardData> rewards)
    {
        List<Theatre6RewardData> list = rewards.Where(reward => reward.RewardType != RewardFight).ToList();
        if (list.Count == 0)
            return new List<Theatre6RewardData>();

        List<Theatre6RewardData> applied = ApplyRewards(m, run, list);
        foreach (Theatre6RewardData reward in applied)
        {
            if (reward.RewardType == RewardGoods)
                ProgressTasks(m, run, reward.TemplateId, reward.Amount);
        }
        return applied;
    }

    private static List<Theatre6RewardData> BuildRewardList(Mutation m, Theatre6RunState run, Theatre6StageChooseGroupTable? group, List<int> types, List<int> ids, List<int> mins, List<int> maxs)
    {
        List<Theatre6RewardData> rewards = new();
        for (int i = 0; i < types.Count; i++)
        {
            int type = types[i];
            if (type <= 0)
                continue;
            int id = i < ids.Count ? ids[i] : 0;
            int min = i < mins.Count ? mins[i] : 0;
            int max = i < maxs.Count ? maxs[i] : 0;
            Theatre6RewardData? reward = BuildReward(m, run, group, type, id, min, max);
            if (reward is not null)
                rewards.Add(reward);
        }
        return rewards;
    }

    private static Theatre6RewardData? BuildReward(Mutation m, Theatre6RunState run, Theatre6StageChooseGroupTable? group, int type, int id, int min, int max)
    {
        switch (type)
        {
            case RewardGoods:
            case RewardSan:
            case RewardHealth:
                // Material/Sanity/Vitality entries carry their authored range in the min/max columns.
                return new Theatre6RewardData { RewardType = type, TemplateId = type == RewardGoods ? id : 0, Amount = SampleAmount(run, min, max) };
            case RewardCoin:
                return new Theatre6RewardData { RewardType = type, Amount = min != 0 || max != 0 ? SampleAmount(run, min, max) : id };
            case RewardBuffPool:
                // Economy resolves the authored candidate set (StageBuffPool + weights) and grants the concrete buffs.
                return id > 0 ? RollBuffPoolReward(m, run, id) : null;
            case RewardSkillPool:
                // Economy resolves RandomPool.Type 1/2 to a concrete skill or relic identity, frozen here for the offer.
                return id > 0 ? RollPoolReward(m, run, id) : null;
            case RewardFight:
                return BuildFightReward(m, run, group, id);
            case RewardAvg:
                return new Theatre6RewardData { RewardType = type, TemplateId = id };
            default:
                // Authored type without a known interpretation: keep the authored identity, invent nothing.
                return new Theatre6RewardData { RewardType = type, TemplateId = id, Amount = min };
        }
    }

    private static int SampleAmount(Theatre6RunState run, int min, int max)
    {
        if (max < min)
            (min, max) = (max, min);
        return max == min ? min : min + Roll(run, max - min + 1);
    }

    private static Theatre6RewardData? BuildFightReward(Mutation m, Theatre6RunState run, Theatre6StageChooseGroupTable? group, int id)
    {
        Theatre6StageFightTable? fight = ResolveFight(run, id, group);
        if (fight is null)
            return null;
        return new Theatre6RewardData
        {
            RewardType = RewardFight,
            TemplateId = id,
            FightId = fight.Id,
            MonsterId = N(fight.EasyMonsterId),
            FightRewards = BuildFightRewards(m, run, fight, hard: false)
        };
    }

    private static List<Theatre6RewardData> BuildFightRewards(Mutation m, Theatre6RunState run, Theatre6StageFightTable fight, bool hard) =>
        hard
            ? BuildRewardList(m, run, null, AsList(fight.HardRewardTypes), AsList(fight.HardRewardIds), new List<int>(), new List<int>())
            : BuildRewardList(m, run, null, AsList(fight.EasyRewardTypes), AsList(fight.EasyRewardIds), new List<int>(), new List<int>());

    private static List<Theatre6RewardData> BuildDefeatRewards(Mutation m, Theatre6RunState run, Theatre6StageFightTable fight)
    {
        List<int> types = AsList(fight.DefeatGetTypes);
        List<int> values = AsList(fight.DefeatGetValues);
        List<Theatre6RewardData> result = new();
        for (int i = 0; i < types.Count; i++)
        {
            int value = i < values.Count ? values[i] : 0;
            if (types[i] <= 0 || value == 0)
                continue;
            Theatre6RewardData? reward = BuildReward(m, run, null, types[i], value, value, value);
            if (reward is not null)
                result.Add(reward);
        }
        return result;
    }

    /// <summary>Resolves an authored fight reference: a StageFight id directly, a StageFightGroup id, or the choose
    /// group's AppointFightGroupIds when the reward entry names no group. Group candidates are drawn by authored weight.</summary>
    private static Theatre6StageFightTable? ResolveFight(Theatre6RunState run, int id, Theatre6StageChooseGroupTable? group)
    {
        if (ProgressionTables.Fight.Value.TryGetValue(id, out Theatre6StageFightTable? direct))
            return direct;

        List<int> groupIds = new();
        if (id > 0 && ProgressionTables.FightGroup.Value.ContainsKey(id))
            groupIds.Add(id);
        else if (group is not null && N(group.AppointFightGroupIds) > 0)
            groupIds.Add(N(group.AppointFightGroupIds));
        if (groupIds.Count == 0)
            return null;

        foreach (int groupId in groupIds)
        {
            if (!ProgressionTables.FightGroup.Value.TryGetValue(groupId, out Theatre6StageFightGroupTable? fightGroup))
                continue;
            List<int> fightIds = AsList(fightGroup.FightIds);
            List<int> weights = AsList(fightGroup.Weights);
            if (fightIds.Count == 0)
                continue;
            int total = 0;
            for (int i = 0; i < fightIds.Count; i++)
                total += i < weights.Count && weights[i] > 0 ? weights[i] : 1;
            int roll = Roll(run, Math.Max(1, total));
            for (int i = 0; i < fightIds.Count; i++)
            {
                int weight = i < weights.Count && weights[i] > 0 ? weights[i] : 1;
                if (roll < weight)
                {
                    if (ProgressionTables.Fight.Value.TryGetValue(fightIds[i], out Theatre6StageFightTable? picked))
                        return picked;
                    break;
                }
                roll -= weight;
            }
        }
        return null;
    }

    /// <summary>Per-binding nonce for a fight (room entry, easy/hard slide or event-spawned fight). Always non-zero so
    /// native attempt identity can never confuse "unset" with a live binding; cleared to 0 when the fight settles.</summary>
    private static int RollFightSeed(Theatre6RunState run) => Roll(run, int.MaxValue - 1) + 1;

    // ---------------------------------------------------------------- tasks

    private static int TaskGroupId(Theatre6StageChooseGroupTable group, bool first)
    {
        int groupId = first ? N(group.FirstTaskGroupId) : N(group.LoopTaskGroupId);
        if (groupId <= 0)
            groupId = N(group.FirstTaskGroupId);
        Require(groupId > 0 && ProgressionTables.TaskGroup.Value.ContainsKey(groupId), ErrRoomType);
        return groupId;
    }

    private static void GenerateTaskRound(Mutation m, Theatre6RunState run, int taskGroupId)
    {
        Require(ProgressionTables.TaskGroup.Value.TryGetValue(taskGroupId, out Theatre6StageTaskGroupTable? group), ErrRoomType);
        int taskNum = Math.Max(1, N(group!.TaskNum));
        List<int> appoints = AsList(group.AppointTasks).Where(id => ProgressionTables.Task.Value.ContainsKey(id)).Distinct().ToList();
        List<int> taskIds = appoints.Count > 0
            ? appoints.Take(taskNum).ToList()
            : PickTaskIds(run, group, taskNum);
        Require(taskIds.Count > 0, ErrTaskNotOffered);

        Dictionary<int, Theatre6TaskData> tasks = new();
        List<Theatre6TaskSlotData> slots = new();
        for (int i = 0; i < taskIds.Count; i++)
        {
            Theatre6TaskSlotData slot = new() { Index = i + 1, TaskId = taskIds[i], RefreshCount = 0 };
            slots.Add(slot);
            tasks[taskIds[i]] = BuildTaskData(m, run, ProgressionTables.Task.Value[taskIds[i]], slot.Index);
        }

        run.TaskGroupId = taskGroupId;
        run.StageTasks = tasks;
        run.TaskSlotData = slots;
    }

    /// <summary>Task offer composition: authored AppointTasks first, otherwise a weighted draw from the in-pool tasks
    /// whose quality the group authors (QualityTypes/QualityBaseWeights), reduced per duplicate material demand by
    /// SameGoodsReduceWeight. All weights are authored; only their composition order is local.</summary>
    private static List<int> PickTaskIds(Theatre6RunState run, Theatre6StageTaskGroupTable group, int count)
    {
        List<int> qualityTypes = AsList(group.QualityTypes);
        List<int> qualityWeights = AsList(group.QualityBaseWeights);
        int baseWeight = Math.Max(1, N(group.BaseWeight));
        int sameGoodsReduce = Math.Max(0, N(group.SameGoodsReduceWeight));
        int characterId = run.File.CharacterId;

        List<Theatre6StageTaskTable> pool = ProgressionTables.Task.Value.Values
            .Where(task => N(task.IsOutPool) == 0 && (N(task.CharacterId) == 0 || N(task.CharacterId) == characterId))
            .OrderBy(task => task.Id).ToList();
        if (qualityTypes.Count > 0)
        {
            List<Theatre6StageTaskTable> graded = pool.Where(task => qualityTypes.Contains(N(task.Quality))).ToList();
            if (graded.Count > 0)
                pool = graded;
        }
        if (pool.Count == 0)
            return new List<int>();

        List<Theatre6StageTaskTable> picked = new();
        for (int round = 0; round < count && picked.Count < pool.Count; round++)
        {
            List<int> weights = new(pool.Count);
            int total = 0;
            foreach (Theatre6StageTaskTable task in pool)
            {
                if (picked.Any(existing => existing.Id == task.Id))
                {
                    weights.Add(0);
                    continue;
                }

                int qualityIndex = qualityTypes.IndexOf(N(task.Quality));
                int weight = qualityIndex >= 0 && qualityIndex < qualityWeights.Count && qualityWeights[qualityIndex] > 0
                    ? qualityWeights[qualityIndex]
                    : baseWeight;
                int duplicates = picked.Count(existing => SharesGoods(existing, task));
                if (duplicates > 0 && sameGoodsReduce > 0)
                    weight = Math.Max(1, weight - duplicates * sameGoodsReduce);
                weights.Add(weight);
                total += weight;
            }

            if (total <= 0)
                break;
            int roll = Roll(run, total);
            int index = 0;
            for (; index < pool.Count; index++)
            {
                if (roll < weights[index])
                    break;
                roll -= weights[index];
            }
            picked.Add(pool[Math.Min(index, pool.Count - 1)]);
        }
        return picked.Select(task => task.Id).ToList();
    }

    private static Theatre6StageTaskTable? PickSingleTask(Theatre6RunState run, Theatre6StageTaskGroupTable group, HashSet<int> offered)
    {
        List<int> pool = ProgressionTables.Task.Value.Values
            .Where(task => N(task.IsOutPool) == 0
                && !offered.Contains(task.Id)
                && (N(task.CharacterId) == 0 || N(task.CharacterId) == run.File.CharacterId))
            .OrderBy(task => task.Id).Select(task => task.Id).ToList();
        List<int> qualityTypes = AsList(group.QualityTypes);
        if (qualityTypes.Count > 0)
        {
            List<int> graded = pool.Where(id => qualityTypes.Contains(N(ProgressionTables.Task.Value[id].Quality))).ToList();
            if (graded.Count > 0)
                pool = graded;
        }
        if (pool.Count == 0)
            pool = AsList(group.AppointTasks).Where(id => ProgressionTables.Task.Value.ContainsKey(id) && !offered.Contains(id)).ToList();
        if (pool.Count == 0)
            return null;
        return ProgressionTables.Task.Value[pool[Roll(run, pool.Count)]];
    }

    private static void AddTask(Mutation m, Theatre6RunState run, Theatre6StageTaskTable task, Theatre6TaskSlotData slot, int refreshCount)
    {
        slot.TaskId = task.Id;
        slot.RefreshCount = refreshCount;
        run.StageTasks[task.Id] = BuildTaskData(m, run, task, slot.Index);
    }

    private static Theatre6TaskData BuildTaskData(Mutation m, Theatre6RunState run, Theatre6StageTaskTable task, int slotIndex)
    {
        Theatre6TaskData data = new() { TaskId = task.Id, SlotIndex = slotIndex, TaskState = TaskStateInit };
        List<int> goodsTypes = AsList(task.AppointGoodsTypes);
        List<int> goodsNums = AsList(task.GoodsNums);
        for (int i = 0; i < goodsTypes.Count; i++)
        {
            if (goodsTypes[i] <= 0)
                continue;
            data.GoodsSlots.Add(new Theatre6TaskGoodsData
            {
                GoodsId = goodsTypes[i],
                Amount = 0,
                NeedNum = i < goodsNums.Count ? goodsNums[i] : 0
            });
        }

        List<int> rewardTypes = AsList(task.RewardTypes);
        List<int> rewardIds = AsList(task.RewardIds);
        for (int i = 0; i < rewardTypes.Count; i++)
        {
            if (rewardTypes[i] <= 0)
                continue;
            int id = i < rewardIds.Count ? rewardIds[i] : 0;
            Theatre6RewardData? reward = BuildReward(m, run, null, rewardTypes[i], id, id, id);
            if (reward is not null)
                data.RewardGoods.Add(reward);
        }
        return data;
    }

    private static Theatre6StageTaskTable? TaskLookup(int taskId) =>
        ProgressionTables.Task.Value.TryGetValue(taskId, out Theatre6StageTaskTable? task) ? task : null;

    private static bool SharesGoods(Theatre6StageTaskTable a, Theatre6StageTaskTable b) =>
        AsList(a.AppointGoodsTypes).Intersect(AsList(b.AppointGoodsTypes)).Any();

    private static void RefreshTaskProgress(Theatre6TaskData task)
    {
        int done = 0;
        int need = 0;
        foreach (Theatre6TaskGoodsData slot in task.GoodsSlots)
        {
            done += Math.Min(slot.Amount, slot.NeedNum);
            need += slot.NeedNum;
        }
        // EN XUiGridTheatre6TaskDetail: Progress / 10 is the displayed percentage, and FailAddNum is rendered as the
        // gold paid for an unfinished mission, so it carries the same StageTaskQuality.FailCoin this server grants.
        task.Progress = need <= 0 ? 100 : Math.Min(100, done * 100 / need) * 10;
        task.FailAddNum = ProgressionTables.TaskQuality.Value.TryGetValue(N(TaskLookup(task.TaskId)?.Quality), out Theatre6StageTaskQualityTable? quality)
            ? N(quality.FailCoin)
            : 0;
    }

    private static bool IsTaskComplete(Theatre6TaskData task) =>
        task.GoodsSlots.Count > 0 && task.GoodsSlots.All(slot => slot.NeedNum <= 0 || slot.Amount >= slot.NeedNum);

    private static int FreeRefreshFromBuffs(Theatre6RunState run) =>
        run.Buffs.Values.Sum(buff => Math.Max(0, buff.TaskFreeRefreshCount));

    // ---------------------------------------------------------------- unlock / difficulty / misc helpers

    private static (int DifficultyId, int StageId) ResolveDifficulty(Player player, Theatre6CharacterTable character, int difficultyId)
    {
        int difficultyGroupId = N(character.PlayDiffGroupIds);
        Theatre6StageDifficultyGroupTable? group = null;
        Require(difficultyGroupId > 0 && ProgressionTables.DifficultyGroup.Value.TryGetValue(difficultyGroupId, out group), ErrPlayModeNotUnlocked);
        Require(AsList(group!.DifficultyIds).Contains(difficultyId), ErrPlayModeNotUnlocked);
        Require(ProgressionTables.Difficulty.Value.TryGetValue(difficultyId, out Theatre6StageDifficultyTable? difficulty), ErrPlayModeNotUnlocked);
        Require(HasUnlock(player, difficulty!.ConditionId), ErrPlayModeNotUnlocked);

        int stageId = N(difficulty.StageId);
        int alternateStageId = N(difficulty.NewStageIds);
        if (stageId > 0 && alternateStageId > 0 && player.Theatre6.PassStageRecords.TryGetValue(stageId, out int cleared) && cleared > 0)
            stageId = alternateStageId;
        else if (stageId <= 0 && alternateStageId > 0)
            stageId = alternateStageId;

        Require(stageId > 0 && ProgressionTables.Stage.Value.ContainsKey(stageId), ErrRoomType);
        return (difficultyId, stageId);
    }

    private static bool HasUnlock(Player player, int? conditionId)
    {
        int id = N(conditionId);
        return id <= 0 || HasCondition(player, id);
    }

    private static int SanDeathBuffId(Theatre6RunState run)
    {
        if (!ProgressionTables.Stage.Value.TryGetValue(run.StageId, out Theatre6StageTable? stage))
            return 0;
        int sanGroupId = N(stage.SanGroupId);
        List<Theatre6StageSanTable> rows = ProgressionTables.San.Value.Values
            .Where(row => N(row.SanGroupId) == sanGroupId && N(row.SanType) == SanTypeDeath)
            .OrderBy(row => row.Id).ToList();
        List<int> buffIds = rows.Count > 0 ? AsList(rows[0].BuffIds) : new List<int>();
        return buffIds.Count > 0 ? buffIds[0] : 0;
    }

    private static bool IsSkillLevelUpBuff(int buffId) =>
        ProgressionTables.Buff.Value.TryGetValue(buffId, out Theatre6StageBuffTable? buff) && N(buff.BuffEffectType) == EffectSkillLevelUp;

    private static List<Theatre6LiveBuffState> NewBuffs(Theatre6RunState run, HashSet<int> known) =>
        run.Buffs.Values.Where(buff => !known.Contains(buff.Uid)).OrderBy(buff => buff.Uid).ToList();

    // ---------------------------------------------------------------- copies

    private static Theatre6StorySaveState CloneStorySave(Theatre6StorySaveState source) => new()
    {
        StoryIds = source.StoryIds.ToList(),
        StoryLineDatas = source.StoryLineDatas.Select(CloneStoryLine).ToList()
    };

    private static Theatre6StoryLineState CloneStoryLine(Theatre6StoryLineState source) => new()
    {
        StoryLineId = source.StoryLineId,
        StageIndex = source.StageIndex,
        IsCompletedBefore = source.IsCompletedBefore,
        IsBuy = source.IsBuy,
        BuyIndex = source.BuyIndex.ToList()
    };

    private static Theatre6StoryLineData ToWireStoryLine(Theatre6StoryLineState source) => new()
    {
        StoryLineId = source.StoryLineId,
        StageIndex = source.StageIndex,
        IsCompletedBefore = source.IsCompletedBefore,
        IsBuy = source.IsBuy,
        BuyIndex = source.BuyIndex.ToList()
    };

    private static Theatre6RewardData CopyReward(Theatre6RewardData source) => new()
    {
        RewardType = source.RewardType,
        TemplateId = source.TemplateId,
        Amount = source.Amount,
        AmountChange = source.AmountChange,
        SkillId = source.SkillId,
        AttrPack = source.AttrPack,
        BuffList = source.BuffList.Select(buff => new Theatre6BuffState { BuffId = buff.BuffId, TriggerCount = buff.TriggerCount, AddMagic = buff.AddMagic }).ToList(),
        FightId = source.FightId,
        MonsterId = source.MonsterId,
        FightRewards = source.FightRewards.Select(CopyReward).ToList()
    };

    private static List<Theatre6RewardData> CopyRewards(IEnumerable<Theatre6RewardData> source) => source.Select(CopyReward).ToList();

    private static Dictionary<int, Theatre6TaskData> CopyTasks(Dictionary<int, Theatre6TaskData> source) =>
        source.ToDictionary(entry => entry.Key, entry => new Theatre6TaskData
        {
            TaskId = entry.Value.TaskId,
            SlotIndex = entry.Value.SlotIndex,
            TaskState = entry.Value.TaskState,
            Schedule = entry.Value.Schedule,
            FailAddNum = entry.Value.FailAddNum,
            Progress = entry.Value.Progress,
            GoodsSlots = entry.Value.GoodsSlots
                .Select(slot => new Theatre6TaskGoodsData { GoodsId = slot.GoodsId, Amount = slot.Amount, NeedNum = slot.NeedNum }).ToList(),
            RewardGoods = CopyRewards(entry.Value.RewardGoods)
        });

    private static List<Theatre6TaskSlotData> CopySlots(List<Theatre6TaskSlotData> source) =>
        source.Select(slot => new Theatre6TaskSlotData { Index = slot.Index, TaskId = slot.TaskId, RefreshCount = slot.RefreshCount }).ToList();
}
