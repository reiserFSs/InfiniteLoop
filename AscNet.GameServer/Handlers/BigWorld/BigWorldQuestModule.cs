using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.dlcworld.questsystem;
using AscNet.Table.V2.share.dlcworld.questsystem.environmentquest;
using AscNet.Table.V2.share.dlcworld.questsystem.invitequest;
using AscNet.Table.V2.share.statussyncfight.quest;
using AscNet.Table.V2.share.statussyncfight.level.sceneconfig;
using MessagePack;
using Newtonsoft.Json.Linq;

namespace AscNet.GameServer.Handlers.BigWorld
{
    // Server-side DLC quest state machine over the native quest config (statussyncfight/quest/DlcQuest*),
    // trace/ready tracking, invite and environment quests, and the quest item bag.
    internal static class BigWorldQuestModule
    {
        // CodeText ids (share/text/CodeText.tsv).
        private const int DlcQuestIdNotFound = 25100001;               // quest id not configured
        private const int DlcQuestStepIdNotActivate = 25100003;        // step not activated
        private const int DlcQuestProcessIdNotFound = 25100004;        // objective (process) id not configured
        private const int DlcQuestAlreadyActivate = 25100006;
        private const int DlcQuestNotActivate = 25100007;
        private const int DlcQuestNotAccept = 25100008;                // quest Ready, not undertaken
        private const int DlcQuestUpdateValueError = 25100013;         // client parameter error
        private const int DlcQuestNotFound = 25100023;                 // quest not in progress
        private const int DlcQuestCannotAcceptNow = 25100040;
        private const int DlcQuestCategoryNotMatch = 25100041;
        private const int DlcQuestInviteQuestNotUnlockAllResult = 25100042;
        private const int DlcQuestInviteQuestResultRewardAlreadyGot = 25100043;
        private const int DlcQuestObjectiveStateIsNotCorrect = 25100045;
        private const int DlcEnvironmentQuestConfigNotExist = 25100046;
        private const int DlcQuestStateIsNotReady = 25100047;

        // Native enums (dump.cs D:66640-66690, D:766296).
        private const int QuestReady = 1, QuestInProgress = 2, QuestFinished = 3;
        private const int StepInProgress = 1, StepFinished = 2;
        private const int ObjectiveInProgress = 3, ObjectiveFinished = 6;
        private const int CategoryInstLevelStory = 1, CategoryInstLevelPlay = 2, CategoryInvite = 4;
        private const int TypeDramaPlayFinish = 2, TypeEnterLevel = 3, TypeInstanceComplete = 4, TypeInteractComplete = 5,
            TypeReadShortMessage = 6, TypeNarrativeComplete = 12, TypeCollectSceneObject = 13;
        internal const int PopupUndertake = 1;

        internal static readonly Lazy<Dictionary<int, DlcQuestTable>> Quests = new(() => TableReaderV2.Parse<DlcQuestTable>().ToDictionary(r => r.Id));
        internal static readonly Lazy<Dictionary<int, DlcQuestStepTable>> Steps = new(() => TableReaderV2.Parse<DlcQuestStepTable>().ToDictionary(r => r.Id));
        internal static readonly Lazy<ILookup<int, DlcQuestStepTable>> StepsByQuest = new(() => Steps.Value.Values.OrderBy(r => r.Id).ToLookup(r => r.QuestId));
        internal static readonly Lazy<Dictionary<int, DlcQuestObjectiveTable>> Objectives = new(() => TableReaderV2.Parse<DlcQuestObjectiveTable>().ToDictionary(r => r.Id));
        internal static readonly Lazy<ILookup<int, DlcQuestObjectiveTable>> ObjectivesByStep = new(() => Objectives.Value.Values.OrderBy(r => I(r.Order)).ThenBy(r => r.Id).ToLookup(r => r.StepId));
        private static readonly Lazy<Dictionary<int, DlcQuestItemTable>> Items = new(() => TableReaderV2.Parse<DlcQuestItemTable>().ToDictionary(r => r.Id));
        private static readonly Lazy<List<DlcEnvironmentQuestGroupTable>> EnvGroups = new(() => TableReaderV2.Parse<DlcEnvironmentQuestGroupTable>());
        internal static readonly Lazy<Dictionary<int, DlcInviteQuestTable>> Invites = new(() => TableReaderV2.Parse<DlcInviteQuestTable>().ToDictionary(r => r.Id));
        private static readonly Lazy<Dictionary<int, DlcInviteQuestResultTable>> InviteResults = new(() => TableReaderV2.Parse<DlcInviteQuestResultTable>().ToDictionary(r => r.Id));

        private static int I(object? value) => value is null ? 0 : Convert.ToInt32(value);

        private static Theatre5DlcQuestInfo Data(Player player) => player.BigWorldState.QuestData;

        #region PlayerData / Core APIs

        internal static void FillPlayerData(Player player, BigWorldPlayerData data)
        {
            BigWorldPlayerState state = player.BigWorldState;
            if (state.TraceQuestId > 0 && Quests.Value.TryGetValue(state.TraceQuestId, out DlcQuestTable? traced))
                data.TraceQuestIds[traced.Category] = traced.Id;
            data.TraceQuestData = new BigWorldTraceQuestData
            {
                IsEnabled = true,
                CurrentTraceQuestId = state.TraceQuestId,
                LastTraceQuestId = state.LastTraceQuestId,
                CurrentTraceReadyQuestId = state.TraceReadyQuestId
            };
            data.InviteQuestInfo = InviteInfo(player);
            data.EnvironmentQuestData = EnvironmentData(player);
        }

        internal static BigWorldInviteQuestInfo InviteInfo(Player player) => new()
        {
            UnlockedInviteQuestResultIds = player.BigWorldState.UnlockedInviteQuestResultIds.ToList(),
            ReceivedRewardInviteQuestIds = new(player.BigWorldState.ReceivedRewardInviteQuestIds)
        };

        private static BigWorldEnvironmentQuestData EnvironmentData(Player player)
        {
            BigWorldEnvironmentQuestData data = new();
            foreach (DlcEnvironmentQuestGroupTable group in EnvGroups.Value.Where(g => g.IsDefaultGroup == 1))
                data.ActivatedQuestGroupIds.TryAdd(group.LevelId, group.Id);
            foreach ((int levelId, int groupId) in player.BigWorldState.EnvironmentQuestGroups)
                data.ActivatedQuestGroupIds[levelId] = groupId;
            return data;
        }

        internal static Dictionary<int, DlcQuestItem> BuildQuestBag(Player player) =>
            player.BigWorldState.DlcQuestBag.Where(kv => kv.Value > 0)
                .ToDictionary(kv => kv.Key, kv => new DlcQuestItem { ItemId = kv.Key, Count = kv.Value });

        // DlcQuestItem goods returned unapplied by BigWorldRewardService.Grant.
        internal static void ApplySliceRewards(Session session, IReadOnlyList<RewardGoods> goods)
        {
            Dictionary<int, int> bag = session.player.BigWorldState.DlcQuestBag;
            NotifyDlcQuestItemUpdate update = new();
            NotifyDlcQuestItemObtainDisplay display = new();
            foreach (RewardGoods good in goods)
            {
                if (!Items.Value.TryGetValue(good.TemplateId, out DlcQuestItemTable? item) || good.Count <= 0)
                    continue;
                int current = bag.GetValueOrDefault(item.Id);
                // AscNet policy: counts above DlcQuestItem.MaxCount are dropped (MaxCount 0 = uncapped).
                int next = item.MaxCount > 0 ? Math.Min(item.MaxCount, current + good.Count) : current + good.Count;
                if (next == current)
                    continue;
                bag[item.Id] = next;
                update.DlcQuestItemChangeDict[item.Id] = new DlcQuestItem { ItemId = item.Id, Count = next };
                int shown = display.DlcQuestItemChangeDict.TryGetValue(item.Id, out DlcQuestItem? prior) ? prior.Count : 0;
                display.DlcQuestItemChangeDict[item.Id] = new DlcQuestItem { ItemId = item.Id, Count = shown + next - current };
            }
            if (update.DlcQuestItemChangeDict.Count == 0)
                return;
            session.player.Save();
            session.SendPush(update);
            session.SendPush(display);
        }

        // DlcQuestObjective.RecycleItemIds: the listed quest items leave the bag entirely. AscNet policy [INF]: the objective's recycle
        // takes the whole stack (the configs pair each recycle with the objectives that granted the item); the client's quest bag
        // takes the absolute count of NotifyDlcQuestItemUpdate (XBigWorldServiceModel:UpdateQuestItemMap sets _QuestItemMap[id] = Count),
        // so a 0 count removes it. No obtain display for a removal.
        internal static void RecycleQuestItems(Session session, IReadOnlyList<int> itemIds)
        {
            Dictionary<int, int> bag = session.player.BigWorldState.DlcQuestBag;
            NotifyDlcQuestItemUpdate update = new();
            foreach (int itemId in itemIds.Where(id => bag.GetValueOrDefault(id) > 0))
            {
                bag.Remove(itemId);
                update.DlcQuestItemChangeDict[itemId] = new DlcQuestItem { ItemId = itemId, Count = 0 };
            }
            if (update.DlcQuestItemChangeDict.Count == 0)
                return;
            session.player.Save();
            session.SendPush(update);
        }

        // WorldData.QuestData: applies pending auto-activation first (the enter response carries the result).
        internal static Theatre5DlcQuestInfo BuildWorldQuestData(Session session)
        {
            BigWorldQuestRuntime.Scoped(session, true, () => BigWorldQuestRuntime.Reevaluate(session));
            return Data(session.player);
        }

        // RepInitialQuests [Quests, FinishedQuestIds] (dump.cs D:767163-767349).
        internal static object[] BuildRepInitialQuests(Player player)
        {
            Theatre5DlcQuestInfo data = Data(player);
            return [data.ActiveQuests.Values.OrderBy(q => q.QuestId).Select(RepQuest).ToList(), data.FinishedQuests.ToList()];
        }

        // Retail RepQuest lists every configured step (unstarted ones at state 0) with all objectives not yet in
        // FinishedObjectiveIds; objective slot [1] is FinishType (0 in every retail sample), and counted objective
        // types carry their Rep*Progress blob regardless of state.
        internal static object?[] RepQuest(Theatre5DlcQuest quest)
        {
            Theatre5DlcQuestDynamicData dyn = quest.DynamicData ??= new();
            HashSet<int> finished = dyn.FinishedObjectiveIds.ToHashSet();
            List<int> stepIds = StepsByQuest.Value[quest.QuestId].Select(step => step.Id).Union(dyn.Steps.Keys).OrderBy(id => id).ToList();
            List<object?[]> steps = stepIds.Select(stepId =>
            {
                dyn.Steps.TryGetValue(stepId, out Theatre5DlcQuestStep? step);
                IEnumerable<int> objectiveIds = ObjectivesByStep.Value[stepId].Select(cfg => cfg.Id)
                    .Union(step?.Objectives.Keys ?? Enumerable.Empty<int>())
                    .Where(id => !finished.Contains(id));
                return new object?[]
                {
                    stepId, step?.StepState ?? 0,
                    objectiveIds.Select(id =>
                    {
                        Theatre5DlcQuestStepObjective? o = null;
                        step?.Objectives.TryGetValue(id, out o);
                        Objectives.Value.TryGetValue(id, out DlcQuestObjectiveTable? cfg);
                        return new object?[] { id, o?.FinishType ?? 0, o?.ObjectiveState ?? 0, Progress(cfg, o ?? new Theatre5DlcQuestStepObjective { Id = id }) };
                    }).ToList()
                };
            }).ToList();
            return [quest.QuestId, dyn.QuestState, steps, dyn.FinishedObjectiveIds.OrderBy(id => id).ToList()];
        }

        // Rep*Progress = [Count] for the counted types in dump.cs D:767267-767330.
        internal static byte[]? Progress(DlcQuestObjectiveTable? cfg, Theatre5DlcQuestStepObjective o) => cfg?.ObjectiveType switch
        {
            TypeInstanceComplete => BigWorldXRpc.Args(o.InstLevelCompleteCount),
            TypeInteractComplete => BigWorldXRpc.Args(o.InteractProgressRecords.Values.Sum(r => r.Count(x => x.WasCompleted))),
            TypeNarrativeComplete => BigWorldXRpc.Args(o.NarrativeCompletedRecords.Count),
            TypeCollectSceneObject => BigWorldXRpc.Args(o.SceneObjectCollectedCountRecord),
            // DeliverItems (14) / KillEnemyGroup (15) have no DeserializeProgress override (dump.cs D:766407, D:766936).
            TypeDeliverItems or TypeKillEnemyGroup => null,
            // KillEnemy (16) / ScanPlusSearchComplete (18) postdate dump.cs. The installed client reads KillEnemy progress as a
            // named-key map and passes a HashSet field to AddRangeWithoutGC, so the set must be present. [INF] key `KilledEnemies` =
            // the server-counted dead level NPC place ids (no retail KillEnemy progress was captured). 18 is assumed to use the same
            // layout with nothing counted (the scan is client-detected).
            BigWorldQuestRuntime.TypeKillEnemy => MessagePack.MessagePackSerializer.Serialize(new Dictionary<string, int[]> { ["KilledEnemies"] = o.KilledEnemies.ToArray() }),
            > TypeCollectSceneObject => EmptyProgress,
            _ => null
        };
        private const int TypeDeliverItems = 14, TypeKillEnemyGroup = 15;
        private static readonly byte[] EmptyProgress =
            MessagePack.MessagePackSerializer.Serialize(new Dictionary<string, int[]> { ["KilledEnemies"] = [] });

        internal static bool TryHandleXRpc(Session session, string rpcName, byte[] args, int levelId) =>
            BigWorldQuestRuntime.HandleXRpc(session, rpcName, args);

        internal static object? Decode(byte[] args)
        {
            try { return MessagePackSerializer.Deserialize<object?>(args, Packet.InboundOptions); }
            catch (MessagePackSerializationException) { return null; }
        }

        internal static void OnLevelEntered(Session session, int levelId) =>
            BigWorldQuestRuntime.OnLevelEntered(session, levelId, !BigWorldModule.IsOnline);

        #endregion

        #region Handlers

        [RequestPacketHandler("DlcQuestUpdateRequest")]
        public static void DlcQuestUpdateRequestHandler(Session session, Packet.Request packet)
        {
            DlcQuestUpdateRequest request = packet.Deserialize<DlcQuestUpdateRequest>();
            Theatre5DlcQuestInfo data = Data(session.player);
            List<Theatre5DlcQuestStepObjective> list = request.QuestStepObjectiveList ?? [];
            int code = list.Count == 0 ? DlcQuestUpdateValueError : 0;
            foreach (Theatre5DlcQuestStepObjective entry in list)
            {
                if (code != 0)
                    break;
                if (!Objectives.Value.TryGetValue(entry.Id, out DlcQuestObjectiveTable? cfg))
                    code = DlcQuestProcessIdNotFound;
                else if (!data.ActiveQuests.TryGetValue(cfg.QuestId, out Theatre5DlcQuest? quest) || quest.DynamicData is null)
                    code = data.FinishedQuests.Contains(cfg.QuestId) ? DlcQuestObjectiveStateIsNotCorrect : DlcQuestNotActivate;
                else if (entry.ObjectiveState is < 0 or > ObjectiveFinished)
                    code = DlcQuestObjectiveStateIsNotCorrect;
                else if (quest.DynamicData.QuestState == QuestReady)
                {
                    // AscNet policy: progress reported on a first-step objective of a Ready quest undertakes it.
                    if (cfg.StepId != Quests.Value[cfg.QuestId].FirstStepId)
                        code = DlcQuestNotAccept;
                }
                else if (!quest.DynamicData.Steps.TryGetValue(cfg.StepId, out Theatre5DlcQuestStep? step) || step.StepState != StepInProgress
                         || !step.Objectives.TryGetValue(cfg.Id, out Theatre5DlcQuestStepObjective? current))
                    code = DlcQuestStepIdNotActivate;
                else if (current.ObjectiveState == ObjectiveFinished || entry.ObjectiveState < current.ObjectiveState)
                    code = DlcQuestObjectiveStateIsNotCorrect;
            }

            session.SendResponse(new DlcQuestUpdateResponse { Code = code }, packet.Id);
            if (code == 0)
                BigWorldQuestRuntime.ApplyClientUpdate(session, list);
        }

        [RequestPacketHandler("DlcQuestTraceIdChangeRequest")]
        public static void DlcQuestTraceIdChangeRequestHandler(Session session, Packet.Request packet)
        {
            int questId = packet.Deserialize<DlcQuestTraceIdChangeRequest>().ChangeTraceQuestId;
            BigWorldPlayerState state = session.player.BigWorldState;
            int code = 0;
            if (questId != 0)
            {
                if (!Quests.Value.TryGetValue(questId, out DlcQuestTable? cfg))
                    code = DlcQuestIdNotFound;
                // The client never syncs instance-level quests (XBigWorldTrackQuestAgency.lua:30-45).
                else if (cfg.Category is CategoryInstLevelStory or CategoryInstLevelPlay)
                    code = DlcQuestCategoryNotMatch;
                else if (!Data(session.player).ActiveQuests.TryGetValue(questId, out Theatre5DlcQuest? quest) || quest.DynamicData?.QuestState != QuestInProgress)
                    code = DlcQuestNotFound;
            }
            if (code == 0 && state.TraceQuestId != questId)
            {
                if (state.TraceQuestId != 0)
                    state.LastTraceQuestId = state.TraceQuestId;
                state.TraceQuestId = questId;
                session.player.Save();
            }
            session.SendResponse(new DlcQuestTraceIdChangeResponse { Code = code }, packet.Id);
        }

        [RequestPacketHandler("BigWorldSetTrackReadyQuestIdRequest")]
        public static void BigWorldSetTrackReadyQuestIdRequestHandler(Session session, Packet.Request packet)
        {
            int questId = packet.Deserialize<BigWorldSetTrackReadyQuestIdRequest>().QuestId;
            int code = questId == 0 ? 0
                : !Quests.Value.ContainsKey(questId) ? DlcQuestIdNotFound
                : !Data(session.player).ReadyQuestIds.Contains(questId) ? DlcQuestStateIsNotReady
                : 0;
            if (code == 0)
            {
                session.player.BigWorldState.TraceReadyQuestId = questId;
                session.player.Save();
            }
            session.SendResponse(new BigWorldSetTrackReadyQuestIdResponse { Code = code }, packet.Id);
        }

        [RequestPacketHandler("DlcEnvironmentQuestGroupChangeRequest")]
        public static void DlcEnvironmentQuestGroupChangeRequestHandler(Session session, Packet.Request packet)
        {
            DlcEnvironmentQuestGroupChangeRequest request = packet.Deserialize<DlcEnvironmentQuestGroupChangeRequest>();
            DlcEnvironmentQuestGroupChangeResponse response = new();
            if (!EnvGroups.Value.Any(g => g.Id == request.QuestGroupId && g.LevelId == request.LevelId))
                response.Code = DlcEnvironmentQuestConfigNotExist;
            else
            {
                session.player.BigWorldState.EnvironmentQuestGroups[request.LevelId] = request.QuestGroupId;
                session.player.Save();
                response.EnvironmentQuestData = EnvironmentData(session.player);
            }
            session.SendResponse(response, packet.Id);
            if (response.Code == 0)
                BigWorldQuestRuntime.OnConditionsChanged(session);
        }

        [RequestPacketHandler("DlcInviteQuestAcceptRequest")]
        public static void DlcInviteQuestAcceptRequestHandler(Session session, Packet.Request packet)
        {
            int questId = packet.Deserialize<DlcInviteQuestAcceptRequest>().QuestId;
            Theatre5DlcQuestInfo data = Data(session.player);
            int code = !Quests.Value.TryGetValue(questId, out DlcQuestTable? cfg) || !Invites.Value.TryGetValue(questId, out DlcInviteQuestTable? invite) ? DlcQuestIdNotFound
                : cfg.Category != CategoryInvite ? DlcQuestCategoryNotMatch
                : data.ActiveQuests.ContainsKey(questId) ? DlcQuestAlreadyActivate
                // One invite at a time (XBigWorldQuestModel:IsUnderTakenInviteQuest).
                : data.ActiveQuests.Keys.Any(id => Quests.Value.TryGetValue(id, out DlcQuestTable? other) && other.Category == CategoryInvite) ? DlcQuestCannotAcceptNow
                : !BigWorldConditionService.Check(session.player, invite.Condition) ? DlcQuestCannotAcceptNow
                : 0;
            session.SendResponse(new DlcInviteQuestAcceptResponse { Code = code }, packet.Id);
            if (code != 0)
                return;
            // AscNet policy: invites are replayable to reach every ResultId; accepting restarts a finished invite (the client keeps
            // its finished quest object, which BigWorldQuestRuntime.DropClientQuest removes first).
            bool replay = data.FinishedQuests.Remove(questId);
            BigWorldQuestRuntime.Scoped(session, !BigWorldModule.IsOnline, () =>
            {
                if (replay)
                    BigWorldQuestRuntime.DropClientQuest(session, questId);
                BigWorldQuestRuntime.Activate(session, cfg!, true);
                AutoActivate(session);
            });
            BigWorldQuestRuntime.Tick(session);
        }

        [RequestPacketHandler("DlcInviteQuestResultNumRewardRequest")]
        public static void DlcInviteQuestResultNumRewardRequestHandler(Session session, Packet.Request packet)
        {
            int questId = packet.Deserialize<DlcInviteQuestResultNumRewardRequest>().QuestId;
            BigWorldPlayerState state = session.player.BigWorldState;
            int code = !Invites.Value.TryGetValue(questId, out DlcInviteQuestTable? invite) ? DlcQuestIdNotFound
                : state.ReceivedRewardInviteQuestIds.ContainsKey(questId) ? DlcQuestInviteQuestResultRewardAlreadyGot
                // XBigWorldSpecialQuestAgency:CheckInviteFinish: every ResultId unlocked.
                : !invite.ResultIds.All(state.UnlockedInviteQuestResultIds.Contains) ? DlcQuestInviteQuestNotUnlockAllResult
                : 0;
            List<RewardGoods> goods = [];
            if (code == 0)
            {
                state.ReceivedRewardInviteQuestIds[questId] = invite!.ResultIds.Count;
                if (invite.TotalRewardId > 0)
                    goods = BigWorldModule.GrantReward(session, invite.TotalRewardId);
                session.player.Save();
                session.SendPush(new NotifyDlcInviteQuestResultNumReward { RewardItems = goods, InviteQuestInfo = InviteInfo(session.player) });
            }
            session.SendResponse(new DlcInviteQuestResultNumRewardResponse { Code = code }, packet.Id);
        }

        #endregion

        #region Activation / completion

        private static bool ObjectiveDone(Theatre5DlcQuestInfo data, int objectiveId) =>
            Objectives.Value.TryGetValue(objectiveId, out DlcQuestObjectiveTable? cfg)
            && (data.FinishedQuests.Contains(cfg.QuestId)
                || data.ActiveQuests.GetValueOrDefault(cfg.QuestId)?.DynamicData?.FinishedObjectiveIds.Contains(objectiveId) == true);

        // Environment quest -> its DlcEnvironmentQuest schedule group (quest of the listed objectives) and that group's level.
        // The quest's own LevelId is 0 for most of them (3008-3013, 3017-3019), so the group's DlcEnvironmentQuestGroup.LevelId rules.
        private static readonly Lazy<Dictionary<int, (int GroupId, int LevelId)>> EnvQuestGroups = new(() => TableReaderV2.Parse<DlcEnvironmentQuestTable>()
            .SelectMany(row => row.ObjectiveIds.SelectMany(ids => $"{ids}".Split('|'))
                .Select(id => Objectives.Value.TryGetValue(int.Parse(id), out DlcQuestObjectiveTable? o) ? o.QuestId : 0)
                .Where(questId => questId > 0).Select(questId => (questId, GroupId: row.GroupId, LevelId: EnvGroups.Value.First(g => g.Id == row.GroupId).LevelId)))
            .DistinctBy(pair => pair.questId).ToDictionary(pair => pair.questId, pair => (pair.GroupId, pair.LevelId)));

        // AscNet policy [INF]: the instance levels a character invitation leads into. An invite quest (Category 4) reaches its level
        // through RequestEnterInstLevel (18001) in an objective list (2007 -> 4039, 2009 -> 4038) or through a LevelInteractOption
        // gated on the invite quest's state (door 3500002: 2015 -> 4040, 2016 -> 4041, 2006 -> 4044). Invitations are replayable and
        // the level is only reachable to play one, so a visit restarts the level's own quest (BigWorldModule.StartPlayRun); retail's
        // replay request is not captured.
        private static readonly Lazy<HashSet<int>> InviteLevels = new(() =>
        {
            static IEnumerable<int> Entered(JToken? list) => (list as JArray ?? []).Where(a => (int?)a["ActionType"] == 18001).Select(a => a["Params"]!.Value<int>("InstLevelId"));
            IEnumerable<int> fromObjectives = Objectives.Value.Values.Where(o => Quests.Value.TryGetValue(o.QuestId, out DlcQuestTable? q) && q.Category == CategoryInvite)
                .SelectMany(o => { JObject cfg = BigWorldQuestRuntime.Config(o); return Entered(cfg["EnterActions"]).Concat(Entered(cfg["ExitActions"])); });
            IEnumerable<int> fromOptions = TableReaderV2.Parse<LevelInteractOptionTable>().Where(row => row.Config?.Contains("\"ActionType\":18001") == true)
                .Select(row => JObject.Parse(row.Config))
                .Where(cfg => cfg["LevelConditionGroup"]?.SelectTokens("$..QuestId").Any(id => Quests.Value.TryGetValue((int)id, out DlcQuestTable? q) && q.Category == CategoryInvite) == true)
                .SelectMany(cfg => Entered(cfg["CompleteActionList"]));
            return fromObjectives.Concat(fromOptions).ToHashSet();
        });

        internal static List<int> InviteLevelQuests(int levelId) => !InviteLevels.Value.Contains(levelId) ? []
            : Quests.Value.Values.Where(q => q.Category is CategoryInstLevelStory or CategoryInstLevelPlay && q.LevelId == levelId).Select(q => q.Id).Order().ToList();

        // AscNet policy [INF]: an environment quest (DlcEnvironmentQuest objectives; ShieldPopViewType 3 = no popup on receive or
        // finish [LUA XBigWorldQuestModel QuestViewShieldTypeList]) has no accept UI and AutoUndertake 0, so it starts as soon as its
        // schedule group is the one selected for its level; its InteractComplete objectives are the Encounter Journal entries
        // (BigworldAIMemory.Condition -> BigWorldCondition 10101003 objective finished).
        internal static bool EnvironmentQuestOnDuty(Player player, DlcQuestTable cfg) =>
            EnvQuestGroups.Value.TryGetValue(cfg.Id, out var env)
            && EnvironmentData(player).ActivatedQuestGroupIds.GetValueOrDefault(env.LevelId) == env.GroupId;

        // AscNet policy (retail rule is server-only): the config gates chained quests with PreFinishQuests/PreFinishObjectives/
        // Condition; an ungated quest is bound to its LevelId (instances, jump challenges, F.O.S., the district) and activates
        // only while the player is in that level; LevelId 0 without gates never auto-activates. Environment quests also need
        // their schedule group selected for the level. Retail oracle: after the tutorial only 2002 (LevelId 4001) is active.
        private static bool LevelGateOpen(Session session, DlcQuestTable cfg)
        {
            bool gated = cfg.PreFinishQuests.Count > 0 || cfg.PreFinishObjectives.Count > 0 || cfg.Condition > 0;
            if (gated)
                return true;
            int levelId = BigWorldModule.CurrentLevelId(session.player.BigWorldState);
            if (EnvQuestGroups.Value.TryGetValue(cfg.Id, out var env))
                return env.LevelId == levelId && EnvironmentQuestOnDuty(session.player, cfg);
            return cfg.LevelId > 0 && cfg.LevelId == levelId;
        }

        // Activates every auto-activatable quest whose prerequisites hold (inside a BigWorldQuestRuntime scope).
        internal static void AutoActivate(Session session)
        {
            Theatre5DlcQuestInfo data = Data(session.player);
            // Ready environment quests saved before they auto-started (or whose group was just selected) start in their level.
            int levelId = BigWorldModule.CurrentLevelId(session.player.BigWorldState);
            foreach (Theatre5DlcQuest ready in data.ActiveQuests.Values.Where(q => q.DynamicData?.QuestState == QuestReady).OrderBy(q => q.QuestId).ToList())
                if (Quests.Value.TryGetValue(ready.QuestId, out DlcQuestTable? readyCfg) && EnvQuestGroups.Value.TryGetValue(readyCfg.Id, out var readyEnv) && readyEnv.LevelId == levelId && EnvironmentQuestOnDuty(session.player, readyCfg))
                    BigWorldQuestRuntime.Undertake(session, ready);
            bool again = true;
            while (again)
            {
                again = false;
                foreach (DlcQuestTable cfg in Quests.Value.Values.OrderBy(q => q.Id))
                {
                    if (cfg.IsAllowAutoActivate != 1 || data.ActiveQuests.ContainsKey(cfg.Id) || data.FinishedQuests.Contains(cfg.Id)
                        || !cfg.PreFinishQuests.All(data.FinishedQuests.Contains)
                        || !cfg.PreFinishObjectives.All(id => ObjectiveDone(data, id))
                        || !BigWorldConditionService.Check(session.player, cfg.Condition)
                        || !LevelGateOpen(session, cfg))
                        continue;
                    BigWorldQuestRuntime.Activate(session, cfg, cfg.AutoUndertake == 1 || EnvironmentQuestOnDuty(session.player, cfg));
                    again = true;
                }
            }
        }

        internal static void FinishQuest(Session session, Theatre5DlcQuest quest, int endStepId, BigWorldQuestRuntime.Changes changes)
        {
            Theatre5DlcQuestInfo data = Data(session.player);
            BigWorldPlayerState state = session.player.BigWorldState;
            DlcQuestTable cfg = Quests.Value[quest.QuestId];
            quest.DynamicData!.QuestState = QuestFinished;
            data.ActiveQuests.Remove(quest.QuestId);
            if (!data.FinishedQuests.Contains(quest.QuestId))
                data.FinishedQuests.Add(quest.QuestId);
            changes.Finished.Add(quest.QuestId);
            if (state.TraceQuestId == quest.QuestId)
            {
                state.LastTraceQuestId = quest.QuestId;
                state.TraceQuestId = 0;
            }
            if (cfg.RewardId > 0 && !state.RewardedQuestIds.Contains(cfg.Id))
            {
                state.RewardedQuestIds.Add(cfg.Id);
                changes.Rewards.AddRange(BigWorldModule.GrantReward(session, cfg.RewardId));
            }
        }

        // Level action UnlockBranchQuestResult: the invite quest's ending result is unlocked and rewarded once.
        internal static void UnlockResult(Session session, int questId, int resultId, BigWorldQuestRuntime.Changes changes)
        {
            BigWorldPlayerState state = session.player.BigWorldState;
            if (!Invites.Value.TryGetValue(questId, out DlcInviteQuestTable? invite) || !invite.ResultIds.Contains(resultId))
                return;
            if (!state.UnlockedInviteQuestResultIds.Contains(resultId))
                state.UnlockedInviteQuestResultIds.Add(resultId);
            List<RewardGoods> goods = [];
            if (!state.RewardedInviteResultIds.Contains(resultId) && InviteResults.Value.TryGetValue(resultId, out DlcInviteQuestResultTable? result) && result.RewardId > 0)
            {
                state.RewardedInviteResultIds.Add(resultId);
                goods = BigWorldModule.GrantReward(session, result.RewardId);
            }
            changes.Touched.Add(questId);
            changes.InviteResults.Add(new NotifyDlcInviteQuestResultReward { QuestId = questId, ResultId = resultId, RewardItems = goods });
        }

        #endregion
    }
}
