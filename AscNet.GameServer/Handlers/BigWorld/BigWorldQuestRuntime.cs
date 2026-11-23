using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Table.V2.share.statussyncfight.quest;
using MessagePack;
using Newtonsoft.Json.Linq;

namespace AscNet.GameServer.Handlers.BigWorld
{
    // Native quest runtime (retail: XQuest/XQuestStep/XQuestObjective, dump.cs D:765830ff): every objective walks
    // 0 InActive, 1 Enter, 2 ScriptEnter, 3 InProgress, 4 Exit, 5 ScriptExit, 6 Finished; serial steps (ExecMode 1) open
    // their objectives one at a time in DlcQuestObjective.Order, parallel steps (ExecMode 2) all at once.
    // Level-action lists (EnterActions/ExitActions/FixProcessors) run through BigWorldLevelActions. Retail starts a
    // non-empty list one tick after the response of the triggering request, so non-empty lists queue in Deferred and
    // Tick(session) drains them after the response is sent; empty lists (and all state pushes) are synchronous.
    internal static partial class BigWorldQuestRuntime
    {
        // Native enums (dump.cs D:66640-66690).
        internal const int QuestReady = 1, QuestInProgress = 2, QuestFinished = 3;
        internal const int StepInProgress = 1, StepFinished = 2;
        internal const int StateEnter = 1, StateScriptEnter = 2, StateInProgress = 3, StateExit = 4, StateScriptExit = 5, StateFinished = 6;
        internal const int TypeCheckRim = 1, TypeDramaPlayFinish = 2, TypeEnterLevel = 3, TypeInstanceComplete = 4, TypeInteractComplete = 5,
            TypeReadShortMessage = 6, TypeReachTarget = 7, TypeUiClosed = 8, TypeTakePhoto = 9, TypeOnLevelTimeOut = 10, TypeCheckIntVar = 11,
            TypeNarrativeComplete = 12, TypeCollectSceneObject = 13, TypeDeliverItems = 14, TypeKillEnemyGroup = 15, TypeKillEnemy = 16, TypeScanPlusSearch = 18;
        private const int QuestExecSerial = 1;
        private const byte NavHideIntentional = 2; // ENavPointHideFlags.Intentional
        private const int TargetTypeNpc = 1, TargetTypeSceneObject = 2; // EActorType

        // Objectives whose completion the client detects and reports with RpcQuestObjectiveCompleteRequest. ScanPlusSearchComplete (18) is
        // additionally checked against the objective's level (see OnClientComplete): it completes on the client-only XScanPlusAbility search
        // state for SearchInfoId, whose actor mapping (XTableBeScannedComp.ScanPlusBeScannedInfoId) sits in client component blobs absent
        // from every decoded table. KillEnemyGroup (15) is server-detected (see its region in BigWorldQuestRuntime.Objectives.cs): the
        // 4.8 client class has no spawn or completion code, the server spawns the group and counts its deaths.
        private static readonly int[] ClientDetected = [TypeReachTarget, TypeUiClosed, TypeTakePhoto, TypeOnLevelTimeOut, TypeNarrativeComplete, TypeScanPlusSearch];

        private static readonly ConcurrentDictionary<int, JObject> ConfigCache = new();

        internal sealed class Changes
        {
            public readonly List<int> Activated = [];
            public readonly List<int> Finished = [];
            public readonly HashSet<int> Touched = [];
            public readonly List<RewardGoods> Rewards = [];
            public readonly List<NotifyDlcInviteQuestResultReward> InviteResults = [];
            public bool Any => Touched.Count > 0 || Activated.Count > 0;
        }

        private sealed class Rt
        {
            public int Depth, Epoch, NavSeq;
            // OnLevelTimeOut: the level's single XLevelPlayTimer, whether the client finished loading the current level
            // and the client's RpcPauseFight count > 0.
            public PlayTimer? Timer;
            public bool LevelLoaded, FightPaused;
            // RpcRLObjectLoadCompleted received for the current level: the client refuses to open quest photo mode before it
            // ("cannot activate in current state"), so photograph requests wait for it.
            public bool ObjectsLoaded;
            public bool Silent;
            public Changes Changes = new();
            public readonly Queue<Action> Later = new(), Deferred = new();
            // objective id -> (config nav id, runtime nav id); runtime ids are per-session like retail's ControllerIncId.
            public readonly Dictionary<int, List<(int ConfigId, int NavId)>> Navs = [];
            public readonly HashSet<int> DramaSent = [];
            // (objective id, list state 2|5) lists queued or running in this session: the RpcPlayerEnterLevelComplete resume
            // (for lists interrupted by a previous session) must not start them a second time.
            public readonly HashSet<(int ObjectiveId, int State)> Running = [];
            // ESystemFunctionType -> net disables the server applied to the client's XSystemFuncManager this fight (disable and
            // recover +1, enable -1); the client's counters live and die with its fight (see OnLevelSwitching).
            public readonly Dictionary<int, int> FuncHeld = [];
        }

        private static readonly ConditionalWeakTable<Session, Rt> States = new();
        private static Rt Of(Session session) => States.GetOrCreateValue(session);

        private static Theatre5DlcQuestInfo Data(Session session) => session.player.BigWorldState.QuestData;
        private static int I(object? value) => value is null ? 0 : Convert.ToInt32(value);
        private static int TypeOf(DlcQuestObjectiveTable cfg) => I(cfg.ObjectiveType);

        internal static JObject Config(DlcQuestObjectiveTable cfg) => ConfigCache.GetOrAdd(cfg.Id, _ =>
            string.IsNullOrWhiteSpace(cfg.Config) ? [] : JObject.Parse(cfg.Config));

        private static JArray? Actions(DlcQuestObjectiveTable cfg, string key) => Config(cfg)[key] as JArray;

        #region Scope / tick

        // Every public entry runs inside a scope; the outermost one drains the Later queue, persists and sends the commit
        // pushes. silent = no XRpc channel (world enter, client-hosted DlcQuestUpdate): states are recorded, quest
        // pushes are the legacy Notify* packets and lists stay pending until the level is loaded.
        internal static void Scoped(Session session, bool silent, Action body)
        {
            Rt rt = Of(session);
            if (rt.Depth > 0)
            {
                body();
                return;
            }
            rt.Depth = 1;
            rt.Silent = silent;
            rt.Changes = new();
            try
            {
                body();
                while (rt.Later.TryDequeue(out Action? next))
                    next();
            }
            finally { rt.Depth = 0; }
            Commit(session, rt);
        }

        // Drains queued action lists (retail: the tick after the response). Call right after the response of the request
        // that may have queued lists; a no-op when nothing is queued.
        internal static void Tick(Session session)
        {
            Rt rt = Of(session);
            if (rt.Deferred.Count == 0)
                return;
            Scoped(session, false, () =>
            {
                while (rt.Deferred.TryDequeue(out Action? run))
                    run();
            });
        }

        private static void Commit(Session session, Rt rt)
        {
            Changes changes = rt.Changes;
            if (!changes.Any && changes.Rewards.Count == 0 && changes.InviteResults.Count == 0)
                return;
            session.player.Save();
            Theatre5DlcQuestInfo data = Data(session);
            if (rt.Silent)
            {
                foreach (int id in changes.Touched.Where(id => !changes.Activated.Contains(id) && data.ActiveQuests.ContainsKey(id)).OrderBy(id => id))
                    session.SendPush(new NotifyDlcQuestUpdate { QuestData = data.ActiveQuests[id] });
                if (changes.Activated.Count > 0)
                    session.SendPush(new NotifyDlcQuestActivate { NewActivatedQuestIds = changes.Activated.ToList() });
                if (changes.Finished.Count > 0)
                    session.SendPush(new NotifyDlcQuestFinish { NewFinishedQuestIds = changes.Finished.ToList(), ActiveQuests = data.ActiveQuests.Keys.OrderBy(id => id).ToList(), FinishedQuests = data.FinishedQuests.ToList() });
            }
            BigWorldTaskModule.OnProgressChanged(session);
            if (changes.Rewards.Count > 0)
                session.SendPush(new BigWorldNotifyReward { RewardGoodsList = changes.Rewards });
            foreach (NotifyDlcInviteQuestResultReward push in changes.InviteResults)
            {
                push.InviteQuestInfo = BigWorldQuestModule.InviteInfo(session.player);
                session.SendPush(push);
            }
        }

        // Quest/nav/drama/level-action pushes carry envelope level 0 in retail.
        private static void Push(Session session, string rpc, params object?[] args)
        {
            if (!Of(session).Silent)
                session.SendPush("XRpcCommon", BigWorldXRpc.Common(rpc, BigWorldXRpc.Args(args), 0));
        }

        #endregion

        #region State machine

        private static Theatre5DlcQuestStepObjective ObjectiveOf(Theatre5DlcQuest quest, DlcQuestObjectiveTable cfg) =>
            quest.DynamicData!.Steps[cfg.StepId].Objectives[cfg.Id];

        private static void SetState(Session session, Theatre5DlcQuest quest, DlcQuestObjectiveTable cfg, int state)
        {
            ObjectiveOf(quest, cfg).ObjectiveState = state;
            Of(session).Changes.Touched.Add(quest.QuestId);
            Push(session, "RpcQuestObjectiveUpdate", quest.QuestId, cfg.Id, state);
            BigWorldQuestHotfix.OnObjectiveState(session, cfg.Id, state);
        }

        private static void MarkPending(Theatre5DlcQuestStepObjective objective, bool enter, bool pending)
        {
            Theatre5DlcLevelActionListData data = (enter ? objective.EnterActionListData : objective.ExitActionListData) ?? new();
            data.CurActionState = pending ? 1 : 0;
            if (enter) objective.EnterActionListData = data; else objective.ExitActionListData = data;
        }

        private static bool IsPending(Theatre5DlcQuestStepObjective objective, bool enter) =>
            (enter ? objective.EnterActionListData : objective.ExitActionListData)?.CurActionState == 1;

        // Runs a level-action list; done fires exactly once when the list completed (immediately for an empty list). A silent
        // scope leaves non-empty lists pending: they resume when the client reports the level loaded.
        private static void RunList(Session session, JArray? list, QuestActionContext context, Theatre5DlcQuestStepObjective objective, Action done)
        {
            if (list is null || list.Count == 0)
            {
                done();
                return;
            }
            Rt rt = Of(session);
            if (rt.Silent || AwaitsLevelLoad(session, context.ObjectiveId))
                return;
            int epoch = rt.Epoch, expect = context.ObjectiveState;
            if (!rt.Running.Add((context.ObjectiveId, expect)))
                return; // already queued/running in this session
            // Enter lists run under state 2 and exit lists under state 5; the guard drops completions of a superseded run.
            rt.Deferred.Enqueue(() => BigWorldLevelActions.Run(session, list, context, () =>
            {
                if (epoch != rt.Epoch)
                    return;
                rt.Running.Remove((context.ObjectiveId, expect));
                if (objective.ObjectiveState == expect)
                    Scoped(session, false, done);
            }));
        }

        // RpcPlayerSwitchLevelNotify makes the client load the target level; level actions pushed before its RpcPlayerEnterLevelComplete
        // reach no level (the invite instances 4038/4039 open their quest during the switch: trial NPC, first-person view and quest pop
        // never showed). A list of an objective that plays in the instance being entered therefore stays pending, like in a silent
        // scope, and resumes in OnPlayerEnterLevelComplete. Open-world objectives are not held: the client rebuilds those from the
        // world-entry data and a late replay would run them twice.
        private static bool AwaitsLevelLoad(Session session, int objectiveId)
        {
            int instLevelId = session.player.BigWorldState.InstLevelId;
            if (instLevelId == 0 || Of(session).LevelLoaded || !BigWorldQuestModule.Objectives.Value.TryGetValue(objectiveId, out DlcQuestObjectiveTable? cfg))
                return false;
            return (I(cfg.LevelId) > 0 ? I(cfg.LevelId) : BigWorldQuestModule.Quests.Value[cfg.QuestId].LevelId) == instLevelId;
        }

        // The client keeps a quest object after the quest finished and refuses a second one with the same id; RpcRemoveQuest
        // [QuestId] (XQuestManager.HandleRemoveQuest) drops it before the quest is activated again. [LIVE, issue #73]
        internal static void DropClientQuest(Session session, int questId) => Push(session, "RpcRemoveQuest", questId);

        private static void OpenObjective(Session session, Theatre5DlcQuest quest, DlcQuestObjectiveTable cfg)
        {
            Theatre5DlcQuestStepObjective objective = ObjectiveOf(quest, cfg);
            SetState(session, quest, cfg, StateEnter);
            SetState(session, quest, cfg, StateScriptEnter);
            // ObjectiveEnter fixes (Space Leap settle objectives: FullyClear + CompleteLevelPlay) run as the objective opens, not only
            // on a level reload that finds it still in state 1/2. AscNet policy [INF]: no retail capture shows the engine's timing.
            RunFixes(session, quest, cfg, objective, 1);
            JArray? enter = Actions(cfg, "EnterActions");
            MarkPending(objective, true, enter is { Count: > 0 });
            RunList(session, enter, new QuestActionContext(quest.QuestId, cfg.Id, StateScriptEnter), objective, () => EnterDone(session, quest, cfg));
        }

        // The FixProcessors of one timing whose conditions hold, as one list (ordered: a later fix reads the vars an earlier one sets).
        // The context state is the timing (1 Enter, 3 InProgress, 4 Exit), so its run key never collides with the Enter/Exit lists (2/5).
        private static void RunFixes(Session session, Theatre5DlcQuest quest, DlcQuestObjectiveTable cfg, Theatre5DlcQuestStepObjective objective, int timing)
        {
            if (Config(cfg)["FixProcessors"]?[timing.ToString()] is not JArray fixes)
                return;
            JArray actions = new(fixes.Where(fix => CheckConditionGroup(session, fix["ConditionGroup"], quest.QuestId)).SelectMany(fix => fix["Actions"]!));
            RunList(session, actions, new QuestActionContext(quest.QuestId, cfg.Id, timing), objective, () => { });
        }

        private static void EnterDone(Session session, Theatre5DlcQuest quest, DlcQuestObjectiveTable cfg)
        {
            MarkPending(ObjectiveOf(quest, cfg), true, false);
            SetState(session, quest, cfg, StateInProgress);
            AddNavs(session, quest, cfg);
            SyncTimer(session);
            SyncEnemyGroups(session);
            SendDrama(session, quest, cfg);
            // Retail delivers AutoSend messages when the objective opens. AscNet policy: AutoSend=0 rows (invite quests, whose
            // messages have no other delivery path) deliver at the same moment.
            if (TypeOf(cfg) == TypeReadShortMessage && I(cfg.ShortMessageId) > 0)
                BigWorldArchiveModule.ReplayMessage(session, I(cfg.ShortMessageId));
            // InstanceComplete with AppendHistoryCount counts the visits finished before the objective opened.
            if (TypeOf(cfg) == TypeInstanceComplete && I(cfg.AppendHistoryCount) == 1)
                ObjectiveOf(quest, cfg).InstLevelCompleteCount = session.player.BigWorldState.InstLevelFinishedCounts.GetValueOrDefault(I(cfg.InstLevelId));
            if (IsSatisfied(session, quest, cfg))
                Close(session, quest, cfg);
        }

        private static void Close(Session session, Theatre5DlcQuest quest, DlcQuestObjectiveTable cfg)
        {
            Theatre5DlcQuestStepObjective objective = ObjectiveOf(quest, cfg);
            if (objective.ObjectiveState != StateInProgress)
                return;
            SetState(session, quest, cfg, StateExit);
            BigWorldQuestModule.ApplySliceRewards(session, ObjectiveItems(cfg));
            BigWorldQuestModule.RecycleQuestItems(session, I(cfg.ItemAction) == 1 ? (cfg.RecycleItemIds ?? []).Select(id => I(id)).ToList() : []);
            OnObjectiveStatistics(session, quest, cfg);
            RemoveNavs(session, cfg.Id);
            StopTimer(session, cfg);
            if (TypeOf(cfg) == TypeKillEnemyGroup)
                BigWorldActors.DespawnGroup(session, cfg.Id);
            SetState(session, quest, cfg, StateScriptExit);
            JArray? exit = Actions(cfg, "ExitActions");
            MarkPending(objective, false, exit is { Count: > 0 });
            RunList(session, exit, new QuestActionContext(quest.QuestId, cfg.Id, StateScriptExit), objective, () => ExitDone(session, quest, cfg));
        }

        // DlcQuestObjective.ItemAction 1 hands ItemIds x ItemCounts to the quest bag as the objective completes: no level action, interaction
        // option or reward row grants DlcQuestItems, yet deliver objectives (14) consume them (Frostheart Shadow 40320305 drama -> 28000035
        // -> 40320315). AscNet policy [INF]: grant at the InProgress -> Exit edge (the 4.8 client has only the XTableDlcQuestObjective.ItemAction
        // field [DUMP48 +0x40], no enum or consumer; retail is uncaptured for 4032); other values (4044's 137) carry no ItemIds. The same
        // objectives' RecycleItemIds ([DUMP48 +0x58]) take the stacks back (4026010 returns 28000022-24 that 40260104-06 granted).
        private static List<RewardGoods> ObjectiveItems(DlcQuestObjectiveTable cfg) =>
            I(cfg.ItemAction) != 1 ? [] : (cfg.ItemIds ?? []).Zip(cfg.ItemCounts ?? [], (id, count) => new RewardGoods { TemplateId = I(id), Count = I(count) })
                .Where(good => good.TemplateId > 0 && good.Count > 0).ToList();

        // Saves written before ItemAction was honoured hold a finished granting objective and an open deliver objective with an empty
        // bag. ponytail: heals by comparing the bag with the quest's finished granting objectives; a stack delivered by an earlier
        // deliver objective of the same quest would be re-granted (no such quest in the tables).
        private static void HealDeliverItems(Session session, Theatre5DlcQuest quest, DlcQuestObjectiveTable cfg)
        {
            Dictionary<int, int> bag = session.player.BigWorldState.DlcQuestBag;
            Dictionary<int, int> granted = [];
            foreach (DlcQuestObjectiveTable done in BigWorldQuestModule.Objectives.Value.Values.Where(o => o.QuestId == quest.QuestId && quest.DynamicData!.FinishedObjectiveIds.Contains(o.Id)))
                foreach (RewardGoods good in ObjectiveItems(done))
                    granted[good.TemplateId] = granted.GetValueOrDefault(good.TemplateId) + good.Count;
            BigWorldQuestModule.ApplySliceRewards(session, RequiredItems(cfg)
                .Select(kv => new RewardGoods { TemplateId = kv.Key, Count = Math.Min(kv.Value - bag.GetValueOrDefault(kv.Key), granted.GetValueOrDefault(kv.Key)) })
                .Where(good => good.Count > 0).ToList());
        }

        private static Dictionary<int, int> RequiredItems(DlcQuestObjectiveTable cfg) =>
            (Config(cfg)["RequiredItemInfoDict"] as JObject ?? []).Properties().ToDictionary(p => int.Parse(p.Name), p => p.Value.Value<int>());

        private static void ExitDone(Session session, Theatre5DlcQuest quest, DlcQuestObjectiveTable cfg)
        {
            MarkPending(ObjectiveOf(quest, cfg), false, false);
            Finish(session, quest, cfg);
        }

        // State 6, then the quest-activation check (PreFinishObjectives) before the next objective opens (retail sequence I).
        private static void Finish(Session session, Theatre5DlcQuest quest, DlcQuestObjectiveTable cfg)
        {
            SetState(session, quest, cfg, StateFinished);
            quest.DynamicData!.FinishedObjectiveIds.Add(cfg.Id);
            BigWorldQuestModule.AutoActivate(session);
            Advance(session, quest, cfg);
        }

        private static void Advance(Session session, Theatre5DlcQuest quest, DlcQuestObjectiveTable cfg)
        {
            Theatre5DlcQuestStep step = quest.DynamicData!.Steps[cfg.StepId];
            DlcQuestStepTable stepCfg = BigWorldQuestModule.Steps.Value[cfg.StepId];
            List<DlcQuestObjectiveTable> all = BigWorldQuestModule.ObjectivesByStep.Value[cfg.StepId].ToList();
            // NeedCompleteCount is the N-of-M rule of parallel steps (ExecMode 2). Serial steps run their whole chain: six serial
            // steps set it to 1 over 2..18 objectives (402801: drama, then the exit interaction that leaves instance 4028).
            // Optional objectives never count: every parallel step has at least NeedCompleteCount required objectives, and 3027's
            // 302702 (optional NPC chat + required UiClosed that grants the System Log item) would otherwise end on the chat.
            bool done = I(stepCfg.NeedCompleteCount) > 0 && I(stepCfg.ExecMode) != QuestExecSerial
                ? all.Count(o => I(o.Optional) == 0 && step.Objectives.GetValueOrDefault(o.Id)?.ObjectiveState == StateFinished) >= I(stepCfg.NeedCompleteCount)
                : all.Where(o => I(o.Optional) == 0).All(o => step.Objectives.GetValueOrDefault(o.Id)?.ObjectiveState == StateFinished);
            if (done)
            {
                FinishStep(session, quest, stepCfg, all);
                return;
            }
            if (I(stepCfg.ExecMode) == QuestExecSerial && all.FirstOrDefault(o => step.Objectives[o.Id].ObjectiveState == 0) is { } next)
                OpenObjective(session, quest, next);
        }

        private static void FinishStep(Session session, Theatre5DlcQuest quest, DlcQuestStepTable stepCfg, List<DlcQuestObjectiveTable> all)
        {
            Theatre5DlcQuestStep step = quest.DynamicData!.Steps[stepCfg.Id];
            step.StepState = StepFinished;
            Of(session).Changes.Touched.Add(quest.QuestId);
            Push(session, "RpcQuestStepUpdate", quest.QuestId, stepCfg.Id, StepFinished);
            // Optional/surplus objectives still open are abandoned with the step.
            foreach (DlcQuestObjectiveTable open in all.Where(o => step.Objectives[o.Id].ObjectiveState is > 0 and < StateFinished))
            {
                RemoveNavs(session, open.Id);
                StopTimer(session, open);
            }
            if (I(stepCfg.IsEndStep) == 1 || IsExhausted(quest))
            {
                EndQuest(session, quest, stepCfg.Id);
                return;
            }
            OpenBranches(session, quest);
        }

        private static void EndQuest(Session session, Theatre5DlcQuest quest, int endStepId)
        {
            BigWorldQuestModule.FinishQuest(session, quest, endStepId, Of(session).Changes);
            Push(session, "RpcQuestUpdate", quest.QuestId, QuestFinished);
            // A finished quest changes conditions 10101001/10101005 that CheckRim objectives of other quests wait on (7003 on 7001).
            Reevaluate(session);
        }

        // Quests 4012/4034-4036 have no IsEndStep row at all (the 4.8 client never evaluates IsEndStep: it only receives
        // RpcQuestUpdate). AscNet policy [INF]: such a quest ends when every one of its steps has finished. Reevaluate applies the
        // same rule to saves written before it existed (4012 sat with every step finished).
        private static bool IsExhausted(Theatre5DlcQuest quest)
        {
            List<DlcQuestStepTable> steps = BigWorldQuestModule.StepsByQuest.Value[quest.QuestId].ToList();
            return steps.Count > 0 && steps.All(s => I(s.IsEndStep) == 0)
                && steps.All(s => quest.DynamicData!.Steps.GetValueOrDefault(s.Id)?.StepState == StepFinished);
        }

        // Every unopened step whose PreStep are all finished and whose PreLevelConditionGroup holds opens.
        // AscNet policy: a non-first step with no PreStep (8 rows, e.g. invite 2009 endings) follows FirstStepId.
        private static void OpenBranches(Session session, Theatre5DlcQuest quest)
        {
            int firstStepId = BigWorldQuestModule.Quests.Value[quest.QuestId].FirstStepId;
            foreach (DlcQuestStepTable next in BigWorldQuestModule.StepsByQuest.Value[quest.QuestId])
            {
                if (next.Id == firstStepId || quest.DynamicData!.Steps.ContainsKey(next.Id))
                    continue;
                IReadOnlyList<int> pre = next.PreStep.Count > 0 ? next.PreStep : [firstStepId];
                if (pre.All(id => quest.DynamicData.Steps.GetValueOrDefault(id)?.StepState == StepFinished)
                    && CheckConditionGroup(session, StepConfig(next)["PreLevelConditionGroup"], quest.QuestId))
                    OpenStep(session, quest, next.Id, false);
            }
        }

        private static JObject StepConfig(DlcQuestStepTable step) =>
            string.IsNullOrWhiteSpace(step.Config) ? [] : JObject.Parse(step.Config);

        // Step opens with every objective listed at state 0 (retail RepQuest); serial steps then open the first one only.
        // deferOpen: quest activation opens its first objective after the current chain settles (retail sequence I).
        internal static void OpenStep(Session session, Theatre5DlcQuest quest, int stepId, bool deferOpen)
        {
            Theatre5DlcQuestStep step = new() { StepId = stepId, StepState = StepInProgress };
            quest.DynamicData!.Steps[stepId] = step;
            List<DlcQuestObjectiveTable> all = BigWorldQuestModule.ObjectivesByStep.Value[stepId].ToList();
            foreach (DlcQuestObjectiveTable cfg in all)
                step.Objectives[cfg.Id] = new Theatre5DlcQuestStepObjective { Id = cfg.Id };
            Of(session).Changes.Touched.Add(quest.QuestId);
            Push(session, "RpcQuestStepUpdate", quest.QuestId, stepId, StepInProgress);
            void Open()
            {
                if (quest.DynamicData.Steps.GetValueOrDefault(stepId) != step)
                    return;
                if (I(BigWorldQuestModule.Steps.Value[stepId].ExecMode) == QuestExecSerial)
                {
                    if (all.Count > 0)
                        OpenObjective(session, quest, all[0]);
                }
                else
                    foreach (DlcQuestObjectiveTable cfg in all)
                        if (step.Objectives[cfg.Id].ObjectiveState == 0)
                            OpenObjective(session, quest, cfg);
            }
            if (deferOpen)
                Of(session).Later.Enqueue(Open);
            else
                Open();
        }

        internal static void Activate(Session session, DlcQuestTable cfg, bool undertake)
        {
            Theatre5DlcQuestInfo data = Data(session);
            Theatre5DlcQuest quest = new() { QuestId = cfg.Id, DynamicData = new() { QuestState = QuestReady }, StaticData = new() };
            data.ActiveQuests[cfg.Id] = quest;
            Changes changes = Of(session).Changes;
            changes.Activated.Add(cfg.Id);
            changes.Touched.Add(cfg.Id);
            Push(session, "RpcActivateQuests", new List<object?[]> { BigWorldQuestModule.RepQuest(quest) });
            if (undertake)
            {
                Undertake(session, quest);
                return;
            }
            if (!data.ReadyQuestIds.Contains(cfg.Id))
                data.ReadyQuestIds.Add(cfg.Id);
            // AscNet policy [INF]: a quest whose first step undertakes itself (UnderTakeSelfQuest in an objective's Enter- or
            // ExitActions) opens that step while still Ready, as retail does for 2003 (step opened before RpcQuestUpdate 2).
            // 2002 starts this way: Drama_200203 (2002014) plays while Ready and its ExitActions undertake the quest.
            // Other Ready quests wait for the player's undertake.
            if (BigWorldQuestModule.Objectives.Value.Values.Any(o => o.StepId == cfg.FirstStepId
                    && new[] { "EnterActions", "ExitActions" }.Any(list => Actions(o, list)?.Any(a => I(a["ActionType"]) == UnderTakeSelfQuestAction) == true)))
                OpenStep(session, quest, cfg.FirstStepId, true);
        }

        private const int UnderTakeSelfQuestAction = 21000;

        internal static void Undertake(Session session, Theatre5DlcQuest quest)
        {
            BigWorldPlayerState state = session.player.BigWorldState;
            Data(session).ReadyQuestIds.Remove(quest.QuestId);
            if (state.TraceReadyQuestId == quest.QuestId)
                state.TraceReadyQuestId = 0;
            quest.DynamicData!.QuestState = QuestInProgress;
            Of(session).Changes.Touched.Add(quest.QuestId);
            Push(session, "RpcQuestUpdate", quest.QuestId, QuestInProgress);
            int firstStepId = BigWorldQuestModule.Quests.Value[quest.QuestId].FirstStepId;
            if (!quest.DynamicData.Steps.ContainsKey(firstStepId))
                OpenStep(session, quest, firstStepId, false);
        }

        #endregion

        #region Objective type behaviour

        private static void SendDrama(Session session, Theatre5DlcQuest quest, DlcQuestObjectiveTable cfg)
        {
            if (TypeOf(cfg) == TypeTakePhoto)
            {
                SendPhotograph(session, cfg);
                return;
            }
            if (TypeOf(cfg) != TypeDramaPlayFinish || string.IsNullOrEmpty(cfg.DramaName) || Of(session).Silent)
                return;
            // The drama plays in its objective's level; entering that level replays it (OnPlayerEnterLevelComplete).
            if (I(cfg.LevelId) > 0 && I(cfg.LevelId) != BigWorldModule.CurrentLevelId(session.player.BigWorldState))
                return;
            Of(session).DramaSent.Add(cfg.Id);
            List<float> pos = (cfg.ReferencePos ?? []).Select(v => (float)Convert.ToDouble(v)).ToList();
            List<float> rot = (cfg.ReferenceRot ?? []).Select(v => (float)Convert.ToDouble(v)).ToList();
            Push(session, "RpcPlayQuestDramaRequest", quest.QuestId, cfg.DramaName, pos, rot, I(cfg.CombineKey));
        }

        // TakePhotoComplete with a CamParamId: the quest photo mode is opened by the server with RpcOpenGameplayPhotographRequest
        // [CamParamId, NpcPlaceIdList, SceneObjectPlaceIdList, IgnoreShelterSceneObjectList, PlayerNpcAnimationDict,
        // LevelNpcAnimationDict, PhotoFilterId] (dump.cs D:691075), filled from the objective's own config (XTableQuestObjective
        // TakePhotoComplete). AscNet policy [INF]: the 4.8 config carries no OpenGameplayPhotograph action for these objectives
        // (the legacy Lua EnterFunc called it), so it is sent when the objective reaches InProgress in its level and replayed on
        // level load like quest dramas. CamParamId 0 objectives use the player's normal camera (function Photo) and send nothing.
        private static void SendPhotograph(Session session, DlcQuestObjectiveTable cfg)
        {
            if (I(cfg.CamParamId) <= 0 || Of(session).Silent || !Of(session).ObjectsLoaded)
                return; // replayed by OnObjectsLoaded
            if (I(cfg.LevelId) > 0 && I(cfg.LevelId) != BigWorldModule.CurrentLevelId(session.player.BigWorldState))
                return;
            Of(session).DramaSent.Add(cfg.Id);
            static List<int> Ids(List<int>? values) => values?.Where(id => id > 0).ToList() ?? [];
            static Dictionary<int, string> Anims(JToken? token) => token is JObject o
                ? o.Properties().ToDictionary(p => int.Parse(p.Name), p => (string?)p.Value ?? "") : [];
            JObject config = Config(cfg);
            Push(session, "RpcOpenGameplayPhotographRequest", I(cfg.CamParamId), Ids(cfg.DetectionNpcPlaceIdList), Ids(cfg.DetectionSceneObjectPlaceIdList),
                Ids(cfg.IgnoreShelterSceneObjectList), Anims(config["PlayerNpcAnimationDict"]), Anims(config["LevelNpcAnimationDict"]), I(cfg.PhotoFilterId));
        }

        // RpcRLObjectLoadCompleted: the level is fully loaded; open the quest photo mode of in-progress photo objectives here.
        internal static void OnObjectsLoaded(Session session) =>
            Scoped(session, false, () =>
            {
                Rt rt = Of(session);
                rt.ObjectsLoaded = true;
                foreach (Theatre5DlcQuest quest in Data(session).ActiveQuests.Values.OrderBy(q => q.QuestId).Where(q => q.DynamicData is not null).ToList())
                    foreach (Theatre5DlcQuestStep step in quest.DynamicData!.Steps.Values.Where(st => st.StepState == StepInProgress))
                        foreach (DlcQuestObjectiveTable cfg in BigWorldQuestModule.ObjectivesByStep.Value[step.StepId])
                            if (TypeOf(cfg) == TypeTakePhoto && step.Objectives.GetValueOrDefault(cfg.Id)?.ObjectiveState == StateInProgress && !rt.DramaSent.Contains(cfg.Id))
                                SendPhotograph(session, cfg);
            });

        private static List<float> Vec(JToken? token) => token is JArray array ? array.Select(v => (float)v).ToList() : [0F, 0F, 0F];

        // Nav point fields (retail 14 incl. the 4.8 extra bool): Id, LevelId, QuestId, UiStyleId (DlcQuest.Type), DisplayOffset,
        // ShowEffect, ForceMapPinActive, ObjectiveId, InitHide, Radius, FloorNum, HideFlags, extra, TargetPos|PlaceId.
        private static void AddNavs(Session session, Theatre5DlcQuest quest, DlcQuestObjectiveTable cfg)
        {
            Rt rt = Of(session);
            if (rt.Silent || rt.Navs.ContainsKey(cfg.Id))
                return;
            JObject config = Config(cfg);
            Theatre5DlcQuestStepObjective objective = ObjectiveOf(quest, cfg);
            List<(int, int)> added = [];
            void Add(string rpc, JToken arg, object target)
            {
                int configId = arg.Value<int?>("Id") ?? 0;
                int navId = ((++rt.NavSeq) << 4) | BigWorldXRpc.ServerControllerId;
                bool hidden = objective.NavPointData.TryGetValue(configId, out Theatre5DlcQuestNavPointSaveData? saved)
                    ? saved.HideFlags is > 0 : arg.Value<bool?>("InitHide") ?? false;
                object? hideFlags = saved?.HideFlags is > 0 ? saved.HideFlags : null;
                Push(session, rpc, navId, I(cfg.LevelId), quest.QuestId, BigWorldQuestModule.Quests.Value[quest.QuestId].Type,
                    Vec(arg["DisplayOffset"]), arg.Value<bool?>("ShowEffect") ?? false, arg.Value<bool?>("ForceMapPinActive") ?? false,
                    cfg.Id, hidden, (float)(arg.Value<double?>("Radius") ?? 0), arg.Value<int?>("FloorNum") ?? 0, hideFlags, false, target);
                added.Add((configId, navId));
            }
            foreach (JToken arg in config["TracePosArgs"] as JArray ?? [])
                Add("RpcAddQuestNavPoint", arg, Vec(arg["Position"]));
            foreach (JToken arg in config["TraceActorArgs"] as JArray ?? [])
            {
                string? rpc = arg.Value<int?>("TargetType") switch
                {
                    TargetTypeNpc => "RpcAddQuestNavPointForLevelNpc",
                    TargetTypeSceneObject => "RpcAddQuestNavPointForLevelSceneObject",
                    _ => null
                };
                if (rpc is not null)
                    Add(rpc, arg, arg.Value<int?>("PlaceId") ?? 0);
            }
            if (added.Count > 0)
                rt.Navs[cfg.Id] = added;
        }

        private static void RemoveNavs(Session session, int objectiveId)
        {
            Rt rt = Of(session);
            if (!rt.Navs.Remove(objectiveId, out List<(int ConfigId, int NavId)>? navs))
                return;
            foreach ((_, int navId) in navs)
                Push(session, "RpcRemoveNavPoint", navId);
        }

        // Level action SetNavPointActive: QuestObjectiveId + NavPointId (TraceArgs.Id) -> RpcSetNavPointActive.
        internal static void SetNavPointActive(Session session, int objectiveId, int navPointId, bool active) =>
            Scoped(session, false, () =>
            {
                if (!BigWorldQuestModule.Objectives.Value.TryGetValue(objectiveId, out DlcQuestObjectiveTable? cfg)
                    || !Data(session).ActiveQuests.TryGetValue(cfg.QuestId, out Theatre5DlcQuest? quest)
                    || quest.DynamicData?.Steps.GetValueOrDefault(cfg.StepId)?.Objectives.GetValueOrDefault(objectiveId) is not { } objective)
                    return;
                objective.NavPointData[navPointId] = new Theatre5DlcQuestNavPointSaveData { NavPointConfigId = navPointId, HideFlags = active ? null : NavHideIntentional };
                Of(session).Changes.Touched.Add(cfg.QuestId);
                if (Of(session).Navs.TryGetValue(objectiveId, out List<(int ConfigId, int NavId)>? navs))
                    foreach ((_, int navId) in navs.Where(n => n.ConfigId == navPointId))
                        Push(session, "RpcSetNavPointActive", navId, active, (int)NavHideIntentional, true);
            });

        // CheckRimSystemCondition: every ConditionIds entry holds. CheckIntVar: quest var Key compared with Value by
        // EIntCheckType (dump48 / XDlcFightEnum.lua: 1 >, 2 >=, 3 ==, 4 <=, 5 <), not ECompareType.
        private static bool IsSatisfied(Session session, Theatre5DlcQuest quest, DlcQuestObjectiveTable cfg)
        {
            switch (TypeOf(cfg))
            {
                case TypeCheckRim:
                    return (cfg.ConditionIds ?? []).All(id => BigWorldConditionService.Check(session.player, id));
                case TypeCheckIntVar:
                    if (string.IsNullOrEmpty(cfg.Key))
                        return false;
                    int left = GetVar(session.player, quest.QuestId, 1, cfg.Key) is { } v ? Convert.ToInt32(v) : 0, right = I(cfg.Value);
                    return I(cfg.CheckType) switch { 1 => left > right, 2 => left >= right, 3 => left == right, 4 => left <= right, 5 => left < right, _ => false };
                case TypeKillEnemy:
                {
                    List<int> exists = (cfg.ExistsEnemyList ?? []).Select(v => I(v)).ToList();
                    return exists.Count > 0 && exists.All(ObjectiveOf(quest, cfg).KilledEnemies.Contains);
                }
                case TypeKillEnemyGroup:
                    return ObjectiveOf(quest, cfg).KilledEnemies.Count >= Math.Max(1, I(cfg.Count));
                case TypeInstanceComplete:
                    return ObjectiveOf(quest, cfg).InstLevelCompleteCount >= Math.Max(1, I(cfg.Count));
                default:
                    return false;
            }
        }

        #endregion

        #region Level conditions (XLevelConditionManager)

        // XConfigLevelConditionGroup { ConditionLists:[{ Conditions:[{CondType, Params, InvertResult}], LogicOp }], LogicOp }.
        // Empty group/list = true. LogicOp 0 And, 1 Or.
        internal static bool CheckConditionGroup(Session session, JToken? group, int questId)
        {
            if (group?["ConditionLists"] is not JArray { Count: > 0 } lists)
                return true;
            bool CheckList(JToken list)
            {
                if (list["Conditions"] is not JArray { Count: > 0 } conditions)
                    return true;
                bool Check(JToken condition) => CheckCondition(session, condition["Params"], questId) != (condition.Value<bool?>("InvertResult") ?? false);
                return list.Value<int?>("LogicOp") == 1 ? conditions.Any(Check) : conditions.All(Check);
            }
            return group.Value<int?>("LogicOp") == 1 ? lists.Any(CheckList) : lists.All(CheckList);
        }

        private static bool CheckCondition(Session session, JToken? p, int questId)
        {
            BigWorldPlayerState state = session.player.BigWorldState;
            switch (p?.Value<string>("$type"))
            {
                case "XConfigLevelConditionParamsCompareVar":
                    return CompareVar(Token(session, p["LeftVarToken"], questId), Token(session, p["RightVarToken"], questId), p.Value<int?>("CompareType") ?? 0);
                case "XConfigLevelConditionParamsCheckQuestObjectiveComplete":
                {
                    int objectiveId = p.Value<int?>("QuestObjectiveId") ?? 0;
                    if (!BigWorldQuestModule.Objectives.Value.TryGetValue(objectiveId, out DlcQuestObjectiveTable? cfg))
                        return false;
                    Theatre5DlcQuestInfo data = Data(session);
                    if (data.FinishedQuests.Contains(cfg.QuestId))
                        return true;
                    Theatre5DlcQuestDynamicData? dyn = data.ActiveQuests.GetValueOrDefault(cfg.QuestId)?.DynamicData;
                    if (dyn is null)
                        return false;
                    if (dyn.FinishedObjectiveIds.Contains(objectiveId))
                        return true;
                    // CheckExitAsComplete: an objective already in its Exit phase counts as complete.
                    return (p.Value<bool?>("CheckExitAsComplete") ?? false)
                        && dyn.Steps.GetValueOrDefault(cfg.StepId)?.Objectives.GetValueOrDefault(objectiveId)?.ObjectiveState >= StateExit;
                }
                case "XConfigLevelConditionParamsCheckQuestComplete":
                    return data_FinishedQuest(session, p.Value<int?>("QuestId") ?? 0);
                case "XConfigLevelConditionParamsIsInLevel":
                    return BigWorldModule.CurrentLevelId(state) == (p.Value<int?>("LevelId") ?? 0);
                case "XConfigLevelConditionParamsCheckPlayerInRange":
                {
                    if (state.LastPosition is not { } pos)
                        return false;
                    List<double> point = p["RangePoint"]!.Select(v => (double)v).ToList(), size = p["RangeSize"]!.Select(v => (double)v).ToList();
                    double dx = pos.X - point[0], dy = pos.Y - point[1], dz = pos.Z - point[2];
                    // RangeType 1 = sphere (RangeSize.x radius); other types = axis-aligned box of half extents RangeSize.
                    return (p.Value<int?>("RangeType") ?? 1) == 1
                        ? dx * dx + dy * dy + dz * dz <= size[0] * size[0]
                        : Math.Abs(dx) <= size[0] && Math.Abs(dy) <= size[1] && Math.Abs(dz) <= size[2];
                }
                case "XConfigLevelConditionParamsCheckNpcInRange":
                    // AscNet policy: NPC positions are client-simulated and not tracked, so an NPC is never known to be in range
                    // (the FixProcessors using it then re-apply their intended position, which is what a fix is for).
                    return false;
                default:
                    Console.Error.WriteLine($"BigWorld level condition unsupported: {p?.Value<string>("$type")}; evaluating false.");
                    return false;
            }
        }

        private static bool data_FinishedQuest(Session session, int questId) => Data(session).FinishedQuests.Contains(questId);

        // XConfigQuestVarRefToken (VarType 1 int, 2 float, 3 bool; QuestId 0/absent = the context quest) or literal.
        private static object Token(Session session, JToken? token, int questId)
        {
            if (token?.Value<string>("$type") == "XConfigQuestVarLiteralToken")
            {
                JToken? literal = token["Literal"];
                return literal?.Value<string>("$type") switch
                {
                    "XQuestVarValueBool" => literal.Value<bool>("Value"),
                    "XQuestVarValueFloat" => literal.Value<double>("Value"),
                    _ => literal?.Value<int>("Value") ?? 0
                };
            }
            int owner = token?.Value<int?>("QuestId") is > 0 ? token.Value<int>("QuestId") : questId;
            object? value = GetVar(session.player, owner, token?.Value<int?>("VarType") ?? 1, token?.Value<string>("StrKey") ?? "");
            return value ?? (token?.Value<int?>("VarType") switch { 2 => 0D, 3 => false, _ => 0 });
        }

        // ELevelVarCompareType: 0 Equal, 1 NotEqual, 2 Greater, 3 Less, 4 GreaterOrEqual, 5 LessOrEqual.
        private static bool CompareVar(object left, object right, int compare)
        {
            if (left is bool || right is bool)
            {
                bool equal = Convert.ToBoolean(left) == Convert.ToBoolean(right);
                return compare == 1 ? !equal : equal && compare == 0;
            }
            double l = Convert.ToDouble(left), r = Convert.ToDouble(right);
            return compare switch { 0 => l == r, 1 => l != r, 2 => l > r, 3 => l < r, 4 => l >= r, 5 => l <= r, _ => false };
        }

        #endregion

        #region Completion triggers

        // Active (InProgress) objectives across quests whose step is running.
        private static List<(Theatre5DlcQuest Quest, DlcQuestObjectiveTable Cfg, Theatre5DlcQuestStepObjective Objective)> InProgress(Session session) =>
            Data(session).ActiveQuests.Values.OrderBy(q => q.QuestId)
                .Where(q => q.DynamicData is not null)
                .SelectMany(q => q.DynamicData!.Steps.Values.Where(s => s.StepState == StepInProgress).OrderBy(s => s.StepId)
                    .SelectMany(s => BigWorldQuestModule.ObjectivesByStep.Value[s.StepId]
                        .Where(cfg => s.Objectives.GetValueOrDefault(cfg.Id)?.ObjectiveState == StateInProgress)
                        .Select(cfg => (q, cfg, s.Objectives[cfg.Id]))))
                .ToList();

        private static List<int> IntList(object? value) => value is object?[] items ? items.Select(BigWorldXRpc.ToInt).ToList() : [];

        private static Dictionary<int, string> StringMap(object? value) => value is IDictionary<object, object> map
            ? map.ToDictionary(kv => BigWorldXRpc.ToInt(kv.Key), kv => $"{kv.Value}") : [];

        // RpcTakePhotoCompleteNotify [DetectedNpcPlaceIdList, DetectedSoPlaceIdList, PhotoFilterId, PlayerNpcAnimationDict,
        // LevelNpcAnimationDict, PhotoId] (dump.cs D:767784): the server decides which TakePhotoComplete objectives the shot
        // satisfies (native GetPhotographCompletedObjectives; body not in the dump). AscNet policy [INF]: an in-progress photo
        // objective of the current level completes when every configured detection NPC/scene object is in the shot and the
        // configured filter / pose animations (when set) match; objectives without requirements complete on any shot.
        internal static void OnTakePhoto(Session session, List<int> npcs, List<int> sceneObjects, int filterId,
            Dictionary<int, string> playerAnims, Dictionary<int, string> levelAnims)
        {
            int levelId = BigWorldModule.CurrentLevelId(session.player.BigWorldState);
            static bool Covers(Dictionary<int, string> shot, JToken? wanted) => wanted is not JObject w
                || w.Properties().All(p => shot.TryGetValue(int.Parse(p.Name), out string? anim) && anim == (string?)p.Value);
            CloseMatching(session, (cfg, _) => TypeOf(cfg) == TypeTakePhoto
                && (I(cfg.LevelId) is 0 || I(cfg.LevelId) == levelId)
                && (cfg.DetectionNpcPlaceIdList ?? []).Where(id => id > 0).All(npcs.Contains)
                && (cfg.DetectionSceneObjectPlaceIdList ?? []).Where(id => id > 0).All(sceneObjects.Contains)
                && (I(cfg.PhotoFilterId) <= 0 || I(cfg.PhotoFilterId) == filterId)
                && Covers(playerAnims, Config(cfg)["PlayerNpcAnimationDict"]) && Covers(levelAnims, Config(cfg)["LevelNpcAnimationDict"]));
        }

        private static void CloseMatching(Session session, Func<DlcQuestObjectiveTable, Theatre5DlcQuestStepObjective, bool> match) =>
            Scoped(session, false, () =>
            {
                foreach ((Theatre5DlcQuest quest, DlcQuestObjectiveTable cfg, Theatre5DlcQuestStepObjective objective) in InProgress(session))
                    if (match(cfg, objective))
                        Close(session, quest, cfg);
            });

        private static void PushProgress(Session session, Theatre5DlcQuest quest, DlcQuestObjectiveTable cfg, Theatre5DlcQuestStepObjective objective) =>
            // Retail RpcQuestObjectiveProgressNotify targets controller 0 (all controllers).
            session.SendPush("XRpcCommon", BigWorldXRpc.Common("RpcQuestObjectiveProgressNotify",
                BigWorldXRpc.Args(quest.QuestId, cfg.Id, BigWorldQuestModule.Progress(cfg, objective)), 0, target: 0));

        // Interaction slice: after the interaction's own pushes (and action list) completed.
        // TargetArgs {actorType: {placeId: optionId}} (0 = any option): the objective takes the interaction that used the listed
        // option. Every non-zero value of the shipped rows names a LevelInteractOption of its place (Liv 4038: 40380601/02 = the two
        // answers at 2600002, options 1/2; 30140101-04 = options 6/2/3/4 of 3500002); none is a repeat count.
        internal static void OnInteract(Session session, int actorType, int placeId, int optionId) =>
            Scoped(session, false, () =>
            {
                foreach ((Theatre5DlcQuest quest, DlcQuestObjectiveTable cfg, Theatre5DlcQuestStepObjective objective) in InProgress(session))
                {
                    if (TypeOf(cfg) != TypeInteractComplete || Config(cfg)["TargetArgs"]?[actorType.ToString()]?[placeId.ToString()] is not { } target)
                        continue;
                    int wanted = target.Value<int>();
                    if (wanted != 0 && wanted != optionId)
                        continue;
                    List<Theatre5DlcQuestInteractRecord> records = objective.InteractProgressRecords.TryGetValue(actorType, out var list)
                        ? list : objective.InteractProgressRecords[actorType] = [];
                    if (records.Any(r => r.TargetPlaceId == placeId && r.WasCompleted))
                        continue;
                    records.Add(new Theatre5DlcQuestInteractRecord { TargetPlaceId = placeId, WasCompleted = true });
                    Of(session).Changes.Touched.Add(quest.QuestId);
                    PushProgress(session, quest, cfg, objective);
                    if (TargetsMet(cfg, objective))
                        Close(session, quest, cfg);
                }
            });

        // Every listed place was interacted with (once, with its option).
        private static bool TargetsMet(DlcQuestObjectiveTable cfg, Theatre5DlcQuestStepObjective objective)
        {
            if (Config(cfg)["TargetArgs"] is not JObject targets)
                return false;
            bool any = false;
            foreach (JProperty type in targets.Properties())
                foreach (JProperty place in ((JObject)type.Value).Properties())
                {
                    any = true;
                    if (objective.InteractProgressRecords.GetValueOrDefault(int.Parse(type.Name))?.Any(r => r.WasCompleted && r.TargetPlaceId == int.Parse(place.Name)) != true)
                        return false;
                }
            return any;
        }

        // CollectSceneObject with CollectObjectiveType 1 (fixed PlaceIdList); by-base-id collection is not configured in any 4.8 row.
        internal static void OnSceneObjectCollected(Session session, int levelId, int placeId) =>
            Scoped(session, false, () =>
            {
                foreach ((Theatre5DlcQuest quest, DlcQuestObjectiveTable cfg, Theatre5DlcQuestStepObjective objective) in InProgress(session))
                {
                    if (TypeOf(cfg) != TypeCollectSceneObject || I(cfg.CollectObjectiveType) != 1 || I(cfg.LevelId) != levelId || !(cfg.PlaceIdList ?? []).Contains(placeId)
                        || objective.SceneObjectCollectedRecords.Contains(placeId))
                        continue;
                    objective.SceneObjectCollectedRecords.Add(placeId);
                    objective.SceneObjectCollectedCountRecord++;
                    Of(session).Changes.Touched.Add(quest.QuestId);
                    PushProgress(session, quest, cfg, objective);
                    if (objective.SceneObjectCollectedCountRecord >= Math.Max(1, I(cfg.MaxProgress)))
                        Close(session, quest, cfg);
                }
            });

        // FinishInstLevel/SettleInstLevel: InstanceComplete objectives of that instance level count one completion.
        internal static void OnInstanceComplete(Session session, int instLevelId) =>
            Scoped(session, false, () =>
            {
                foreach ((Theatre5DlcQuest quest, DlcQuestObjectiveTable cfg, Theatre5DlcQuestStepObjective objective) in InProgress(session))
                {
                    if (TypeOf(cfg) != TypeInstanceComplete || I(cfg.InstLevelId) != instLevelId)
                        continue;
                    objective.InstLevelCompleteCount++;
                    Of(session).Changes.Touched.Add(quest.QuestId);
                    PushProgress(session, quest, cfg, objective);
                    if (objective.InstLevelCompleteCount >= Math.Max(1, I(cfg.Count)))
                        Close(session, quest, cfg);
                }
            });

        // RequestLeaveInstLevel: PassOnLeaveInstLevel objectives complete (one completion) when the player leaves the instance.
        internal static void OnInstanceLeft(Session session, int instLevelId) =>
            Scoped(session, false, () =>
            {
                Of(session).LevelLoaded = false;
                Of(session).ObjectsLoaded = false;
                SuspendTimer(session);
                foreach ((Theatre5DlcQuest quest, DlcQuestObjectiveTable cfg, Theatre5DlcQuestStepObjective objective) in InProgress(session))
                {
                    if (TypeOf(cfg) != TypeInstanceComplete || I(cfg.InstLevelId) != instLevelId || I(cfg.PassOnLeaveInstLevel) != 1)
                        continue;
                    objective.InstLevelCompleteCount++;
                    Of(session).Changes.Touched.Add(quest.QuestId);
                    PushProgress(session, quest, cfg, objective);
                    if (objective.InstLevelCompleteCount >= Math.Max(1, I(cfg.Count)))
                        Close(session, quest, cfg);
                }
            });

        // LeaveInstLevelRequest NoSaveExit ("Exit Without Saving", BigWorldText StoryLevelTipExit*): AscNet policy [INF] - the
        // progress of the instance's unfinished quests is discarded; they return to Ready and restart on the next entry
        // (OnLevelEntered undertakes them). RpcRemoveQuest + RpcActivateQuests replace the client's quest object. A relog out of
        // an unsaved instance does the same silently (the client has no fight yet; the world-entry response carries the quests).
        internal static void OnInstanceRolledBack(Session session, int instLevelId, bool silent = false) =>
            Scoped(session, silent, () =>
            {
                Rt rt = Of(session);
                rt.Epoch++;
                rt.Deferred.Clear();
                rt.Running.Clear();
                rt.LevelLoaded = false;
                rt.ObjectsLoaded = false;
                SuspendTimer(session);
                foreach (Theatre5DlcQuest quest in Data(session).ActiveQuests.Values
                    .Where(q => q.DynamicData?.QuestState == QuestInProgress
                        && BigWorldQuestModule.Quests.Value.TryGetValue(q.QuestId, out var c) && c.Category is 1 or 2 && c.LevelId == instLevelId)
                    .OrderBy(q => q.QuestId).ToList())
                {
                    foreach (int objectiveId in BigWorldQuestModule.Objectives.Value.Values.Where(o => o.QuestId == quest.QuestId).Select(o => o.Id))
                    {
                        RemoveNavs(session, objectiveId);
                        rt.DramaSent.Remove(objectiveId);
                    }
                    DropClientQuest(session, quest.QuestId);
                    Activate(session, BigWorldQuestModule.Quests.Value[quest.QuestId], false);
                }
            });

        // Jumper (BigWorldModule.JumpPlayQuestOf) and invite-instance (BigWorldQuestModule.InviteLevelQuests) level quests never resume:
        // a visit or re-challenge discards the previous run in any state (mid-run, settle pending, finished) and the quest restarts
        // Ready - OnLevelEntered undertakes it. The old objectives are zeroed so a list still in flight cannot finish the new quest;
        // FinishedQuests keeps the history (the client replaces its quest object with RpcRemoveQuest + RpcActivateQuests, as for a
        // rollback). AscNet policy [INF].
        internal static void RestartPlayQuest(Session session, int questId, bool silent) =>
            Scoped(session, silent, () =>
            {
                Rt rt = Of(session);
                Theatre5DlcQuestInfo data = Data(session);
                List<DlcQuestObjectiveTable> objectives = BigWorldQuestModule.Objectives.Value.Values.Where(o => o.QuestId == questId).ToList();
                if (data.ActiveQuests.GetValueOrDefault(questId) is { DynamicData: { } old })
                {
                    foreach (DlcQuestObjectiveTable cfg in objectives)
                        StopTimer(session, cfg);
                    foreach (Theatre5DlcQuestStepObjective objective in old.Steps.Values.SelectMany(step => step.Objectives.Values))
                        objective.ObjectiveState = 0;
                    DropClientQuest(session, questId);
                    data.ActiveQuests.Remove(questId);
                }
                else if (data.FinishedQuests.Contains(questId))
                    DropClientQuest(session, questId);
                data.ReadyQuestIds.Remove(questId);
                foreach (DlcQuestObjectiveTable cfg in objectives)
                {
                    RemoveNavs(session, cfg.Id);
                    rt.DramaSent.Remove(cfg.Id);
                    rt.Running.Remove((cfg.Id, StateScriptEnter));
                    rt.Running.Remove((cfg.Id, StateScriptExit));
                }
                Activate(session, BigWorldQuestModule.Quests.Value[questId], false);
            });

        internal static void OnLevelEntered(Session session, int levelId, bool silent) =>
            Scoped(session, silent, () =>
            {
                // The new level's replicate (and its XLevelPlayTimer) reaches the client with RpcPlayerEnterLevelComplete.
                Of(session).LevelLoaded = false;
                Of(session).ObjectsLoaded = false;
                SuspendTimer(session);
                CloseMatching(session, (cfg, _) => TypeOf(cfg) == TypeEnterLevel && I(cfg.TargetLevelId) == levelId);
                // Instance quests already in progress when their level is re-entered (after a relog the player is put back in
                // the open world): RpcReLaunchQuests [[RepQuest]] (dump.cs XQuestManager.HandleReLaunchQuests) makes the client
                // re-track them (XBigWorldQuestAgency:OnQuestRelaunch -> OnQuestUndertaken -> TrackQuest for inst quests);
                // without it the tracker stays on the outer quest. Quests undertaken below get RpcQuestUpdate instead.
                // The client already holds the quest (world-entry InitialQuests or an earlier visit) and rejects a relaunch of an
                // existing quest ("已存在任务对象"), so RpcRemoveQuest [QuestId] (XQuestManager.HandleRemoveQuest) drops it first.
                List<Theatre5DlcQuest> relaunch = Data(session).ActiveQuests.Values
                    .Where(q => q.DynamicData?.QuestState == QuestInProgress
                        && BigWorldQuestModule.Quests.Value.TryGetValue(q.QuestId, out var c) && c.Category is 1 or 2 && c.LevelId == levelId)
                    .OrderBy(q => q.QuestId).ToList();
                foreach (Theatre5DlcQuest quest in relaunch)
                    DropClientQuest(session, quest.QuestId);
                if (relaunch.Count > 0)
                    Push(session, "RpcReLaunchQuests", relaunch.Select(BigWorldQuestModule.RepQuest).ToList());
                BigWorldQuestModule.AutoActivate(session);
                // AscNet policy [INF]: instance quests (EDlcQuestCategory InstLevelStoryQuest 1 / InstLevelPlayQuest 2) bound to
                // this level start when the player enters it. No config action or client request undertakes them (4026 "Froggie's
                // Day" has AutoUndertake 0 and no UnderTakeSelfQuest), and the level is only reachable to play that quest.
                foreach (Theatre5DlcQuest ready in Data(session).ActiveQuests.Values.Where(q => q.DynamicData?.QuestState == QuestReady).ToList())
                    if (BigWorldQuestModule.Quests.Value.TryGetValue(ready.QuestId, out var cfg) && cfg.Category is 1 or 2 && cfg.LevelId == levelId)
                    {
                        Undertake(session, ready);
                        // The entry that undertook the instance quest is the entry its EnterLevel objective waits for (4024); the
                        // first CloseMatching above ran before the quest existed. AscNet policy [INF].
                        CloseMatching(session, (o, _) => TypeOf(o) == TypeEnterLevel && o.QuestId == ready.QuestId && I(o.TargetLevelId) == levelId);
                    }
            });

        // ReadShortMessage completes when the game-server message record reaches its terminal step (retail sequence J).
        internal static void OnMessageFinished(Session session, int messageId) =>
            CloseMatching(session, (cfg, _) => TypeOf(cfg) == TypeReadShortMessage && I(cfg.ShortMessageId) == messageId);

        // RpcDramaFinishNotify.HistoryDecisionDict (key clip id, value selected option ids [DUMP48 XDramaFinishEventArgs]): an in-progress
        // DramaPlayFinish objective's RecordDecisionClipSelect {clip: var} stores the first selected option in the quest's int var
        // before the objective closes, so the PreLevelConditionGroup of its branch steps (decision1 == 1/2) sees it. A clip absent from
        // the dict was skipped: AscNet policy [INF] option 1, as the shipped Level_600101 script defaults a skipped decision to branch 1.
        internal static void RecordDecisions(Session session, string dramaName, IReadOnlyDictionary<int, List<int>> history)
        {
            foreach ((Theatre5DlcQuest quest, DlcQuestObjectiveTable cfg, _) in InProgress(session).ToList())
                if (TypeOf(cfg) == TypeDramaPlayFinish && cfg.DramaName == dramaName && Config(cfg)["RecordDecisionClipSelect"] is JObject clips)
                    foreach (JProperty clip in clips.Properties())
                        SetVar(session, quest.QuestId, 1, clip.Value.Value<string>()!,
                            history.TryGetValue(int.Parse(clip.Name), out List<int>? picked) && picked.Count > 0 ? picked[0] : 1);
        }

        internal static void OnDramaFinish(Session session, string dramaName) =>
            CloseMatching(session, (cfg, _) => TypeOf(cfg) == TypeDramaPlayFinish && cfg.DramaName == dramaName);

        internal static void OnUiClosed(Session session, string uiName, IReadOnlyList<int> intParams) =>
            CloseMatching(session, (cfg, _) => TypeOf(cfg) == TypeUiClosed && cfg.UiName == uiName
                && (cfg.IntParams ?? []).All(intParams.Contains));

        internal static void OnNarrativeComplete(Session session, int narrativeId) =>
            Scoped(session, false, () =>
            {
                foreach ((Theatre5DlcQuest quest, DlcQuestObjectiveTable cfg, Theatre5DlcQuestStepObjective objective) in InProgress(session))
                {
                    if (TypeOf(cfg) != TypeNarrativeComplete || !(cfg.NarrativeIds ?? []).Contains(narrativeId))
                        continue;
                    if (!objective.NarrativeCompletedRecords.Contains(narrativeId))
                        objective.NarrativeCompletedRecords.Add(narrativeId);
                    Of(session).Changes.Touched.Add(quest.QuestId);
                    PushProgress(session, quest, cfg, objective);
                    if ((cfg.NarrativeIds ?? []).All(objective.NarrativeCompletedRecords.Contains))
                        Close(session, quest, cfg);
                }
            });

        // RpcQuestObjectiveCompleteRequest: the client detected a client-side objective (reach position, ui closed, photo, timer, narrative).
        // ScanPlusSearch objectives at other levels than the player's are rejected (AscNet policy: their scan targets exist only in the
        // objective's own level); the timer report is accepted at any point and cannot double-complete because Close only acts on an
        // InProgress objective.
        internal static void OnClientComplete(Session session, int questId, int objectiveId) =>
            CloseMatching(session, (cfg, _) => cfg.Id == objectiveId && cfg.QuestId == questId && ClientDetected.Contains(TypeOf(cfg))
                && (TypeOf(cfg) != TypeScanPlusSearch || InObjectiveLevel(session, cfg)));

        // Game-server state feeding CheckRimSystemCondition changed (guide complete, message record, course/task): re-evaluate.
        internal static void OnConditionsChanged(Session session)
        {
            // AscNet policy: a player who never entered Babylonia has no quest runtime yet; entering evaluates everything.
            // Main-game call sites (guides) must not activate Babylonia quests for players who never opened it.
            if (session.player.BigWorldState.EnteredWorldIds.Count == 0)
                return;
            // Outside the world there is no XRpc channel: states are recorded and pending lists resume on level load.
            if (session.BigWorldWorldId == 0)
            {
                Scoped(session, true, () => Reevaluate(session));
                return;
            }
            Scoped(session, false, () => Reevaluate(session));
        }

        internal static void Reevaluate(Session session)
        {
            bool again = true;
            while (again)
            {
                again = false;
                foreach ((Theatre5DlcQuest quest, DlcQuestObjectiveTable cfg, _) in InProgress(session))
                    if (TypeOf(cfg) is TypeCheckRim or TypeCheckIntVar && IsSatisfied(session, quest, cfg))
                    {
                        Close(session, quest, cfg);
                        again = true;
                        break;
                    }
            }
            foreach (Theatre5DlcQuest quest in Data(session).ActiveQuests.Values.OrderBy(q => q.QuestId).ToList())
                if (quest.DynamicData?.QuestState == QuestInProgress)
                {
                    if (IsExhausted(quest))
                        EndQuest(session, quest, quest.DynamicData.Steps.Keys.Max());
                    else
                        OpenBranches(session, quest);
                }
            BigWorldQuestModule.AutoActivate(session);
        }

        // Quest VarBlock (native XDlcVarBlockData: typed string-keyed dictionaries; EVarType 1 int, 2 float, 3 bool) of an active quest.
        // Returns null when the quest is not active or the key is unset.
        internal static object? GetVar(Player player, int questId, int varType, string key)
        {
            Theatre5DlcVarBlockData? block = player.BigWorldState.QuestData.ActiveQuests.GetValueOrDefault(questId)?.DynamicData?.VarBlockData;
            return varType switch
            {
                1 => block is not null && block.IntDict.TryGetValue(key, out int i) ? i : null,
                2 => block is not null && block.FloatDict.TryGetValue(key, out float f) ? f : null,
                3 => block is not null && block.BoolDict.TryGetValue(key, out bool b) ? b : null,
                _ => null
            };
        }

        // Stores a var, tells the client (RpcQuestSyncVars = [QuestId, RepVarModify[ {EVarType: XVarModifyDictionary bytes} ]];
        // client XQuest.HandleSyncVars -> VarBlock.ApplyModify -> OnSetVar -> QuestVarChange / Lua var listeners [DUMP48 0x1DE91C0,
        // 0x1DE2E90, 0x3C26B80]) and re-evaluates CheckIntVar objectives / branch gates. Enter/relog needs no replay: the quest's
        // DynamicData.VarBlockData (XDlcVarBlockData) travels with the quest data.
        internal static void SetVar(Session session, int questId, int varType, string key, object value) =>
            Scoped(session, false, () =>
            {
                if (Data(session).ActiveQuests.GetValueOrDefault(questId)?.DynamicData is not { } dyn)
                    return;
                Theatre5DlcVarBlockData block = dyn.VarBlockData ??= new();
                switch (varType)
                {
                    case 1: block.IntDict[key] = Convert.ToInt32(value); break;
                    case 2: block.FloatDict[key] = Convert.ToSingle(value); break;
                    case 3: block.BoolDict[key] = Convert.ToBoolean(value); break;
                    default: return;
                }
                Of(session).Changes.Touched.Add(questId);
                object stored = varType switch { 1 => block.IntDict[key], 2 => block.FloatDict[key], _ => block.BoolDict[key] };
                Push(session, "RpcQuestSyncVars", questId, new object?[] { new Dictionary<int, byte[]> { [varType] = BigWorldXRpc.VarModifySet(key, stored) } });
                OnConditionsChanged(session);
            });

        // Level action UnderTakeSelfQuest.
        internal static void UnderTakeSelfQuest(Session session, int questId) =>
            Scoped(session, false, () =>
            {
                if (Data(session).ActiveQuests.GetValueOrDefault(questId) is { DynamicData.QuestState: QuestReady } quest)
                    Undertake(session, quest);
            });

        // Level action UnlockBranchQuestResult: unlocks the invite result (once rewarded) for the quest (null = context quest).
        internal static void UnlockBranchQuestResult(Session session, int resultId, int? questId) =>
            Scoped(session, false, () =>
            {
                int owner = questId ?? Data(session).ActiveQuests.Keys.OrderBy(id => id)
                    .FirstOrDefault(id => BigWorldQuestModule.Invites.Value.TryGetValue(id, out var invite) && invite.ResultIds.Contains(resultId));
                BigWorldQuestModule.UnlockResult(session, owner, resultId, Of(session).Changes);
            });

        #endregion

        #region Level enter / leave

        // RpcPlayerEnterLevelComplete: replay the active nav points (and undelivered drama requests), run the native FixProcessors
        // for every running objective and resume lists that were pending when the session ended.
        internal static void OnPlayerEnterLevelComplete(Session session) =>
            Scoped(session, false, () =>
            {
                Rt rt = Of(session);
                List<(Theatre5DlcQuest Quest, DlcQuestObjectiveTable Cfg, Theatre5DlcQuestStepObjective Objective)> running = Data(session).ActiveQuests.Values
                    .OrderBy(q => q.QuestId).Where(q => q.DynamicData is not null)
                    .SelectMany(q => q.DynamicData!.Steps.Values.Where(s => s.StepState == StepInProgress).OrderBy(s => s.StepId)
                        .SelectMany(s => BigWorldQuestModule.ObjectivesByStep.Value[s.StepId]
                            .Where(cfg => s.Objectives.GetValueOrDefault(cfg.Id)?.ObjectiveState is > 0 and < StateFinished)
                            .Select(cfg => (q, cfg, s.Objectives[cfg.Id]))))
                    .ToList();
                foreach ((Theatre5DlcQuest quest, DlcQuestObjectiveTable cfg, Theatre5DlcQuestStepObjective objective) in running)
                    if (objective.ObjectiveState == StateInProgress)
                    {
                        AddNavs(session, quest, cfg);
                        if (TypeOf(cfg) == TypeDeliverItems)
                            HealDeliverItems(session, quest, cfg);
                        if (!rt.DramaSent.Contains(cfg.Id))
                            SendDrama(session, quest, cfg);
                    }
                // Quest system-function state lives only in the client's XSystemFuncManager (per fight, in memory; neither the quest
                // dicts nor the save data are read back [DUMP48 XQuest +0x68/+0x70 touched only by .ctor/CleanUp]), so a relog needs
                // it re-applied. The client's restore path is RpcRecoverSystemFuncEntryEnableBatchRequest {DisableDict}, which adds
                // the counts per type [DUMP48 XSystemFuncManager.HandleRpcRecoverSystemFuncEntryEnableBatchRequest 0x1F1DEE0: TryAdd
                // else +=]; the quest's enables (e.g. 40270106 ScanAbility) and controls are replayed as its actions sent them. Only
                // quests bound to the current level apply: the client keeps these counters across level switches and a replay in
                // another level underflows that level's own counter (live "TaskEntry < 0" after 4012 was left unfinished). Which
                // quests/order is AscNet policy [INF]; retail's restore trigger is not captured.
                int levelId = BigWorldModule.CurrentLevelId(session.player.BigWorldState);
                Dictionary<int, Theatre5DlcSystemFunctionControlSave> controls = [];
                Dictionary<int, int> recover = [];
                List<int> enable = [];
                foreach (Theatre5DlcQuest quest in Data(session).ActiveQuests.Values.OrderBy(q => q.QuestId))
                    if (quest.DynamicData is { QuestState: QuestInProgress } dynamic
                        && BigWorldQuestModule.Quests.Value.GetValueOrDefault(quest.QuestId) is { } questCfg && (questCfg.LevelId <= 0 || questCfg.LevelId == levelId))
                    {
                        foreach ((int type, int count) in dynamic.FuncEntryDisableDict)
                            recover[type] = recover.GetValueOrDefault(type) + count;
                        enable.AddRange(dynamic.FuncEntryEnabled.Where(type => !enable.Contains(type)));
                        foreach (Theatre5DlcSystemFunctionControlSave save in dynamic.SystemFuncControlSaveData)
                            controls[save.SystemFunctionType] = save; // set semantics: the highest quest id wins per function
                    }
                if (!rt.Silent)
                {
                    if (recover.Count > 0)
                    {
                        foreach ((int type, int count) in recover)
                            rt.FuncHeld[type] = rt.FuncHeld.GetValueOrDefault(type) + count;
                        session.SendPush("XRpcCommon", BigWorldXRpc.Common("RpcRecoverSystemFuncEntryEnableBatchRequest", BigWorldXRpc.Args(recover), 0));
                    }
                    if (enable.Count > 0)
                        SendFuncEntry(session, enable, []);
                }
                foreach (Theatre5DlcSystemFunctionControlSave save in controls.Values)
                    if (BigWorldActionHandlers.ControlParams(save) is { } control && !rt.Silent)
                        session.SendPush("XRpcCommon", BigWorldXRpc.Common("RpcControlSystemFunctionRequest", BigWorldActionHandlers.ControlSystemFunctionArgs(control), 0));
                // The client finished loading the current level: the level's XLevelPlayTimer exists on the client from here on.
                rt.LevelLoaded = true;
                SyncTimer(session);
                SyncEnemyGroups(session);
                // Fixes and pending lists belong to the level the objective plays in; other levels' objectives wait for that level.
                running = running.Where(r => I(r.Cfg.LevelId) is 0 || I(r.Cfg.LevelId) == levelId).ToList();
                // Repair of saves written by the old invite replay: a ReadShortMessage objective that is open while its message record is
                // already finished can never close (it advances only from the message-finish notification, which the replay never
                // re-delivered, and the one-invite-at-a-time rule then blocks every other invitation). The lost notification is replayed
                // from the record here, where the objective's exit actions can still transfer the player. Idempotent: the objective
                // leaves InProgress, so a second pass finds nothing. Persists with the quest save of this scope.
                foreach ((Theatre5DlcQuest quest, DlcQuestObjectiveTable cfg, Theatre5DlcQuestStepObjective objective) in running.ToList())
                    if (TypeOf(cfg) == TypeReadShortMessage && objective.ObjectiveState == StateInProgress && BigWorldArchiveModule.MessageFinished(session, I(cfg.ShortMessageId)))
                    {
                        session.log.Info($"BigWorld quest {quest.QuestId}: read objective {cfg.Id} was open on finished message {I(cfg.ShortMessageId)}; completing it.");
                        Close(session, quest, cfg);
                    }
                foreach ((Theatre5DlcQuest quest, DlcQuestObjectiveTable cfg, Theatre5DlcQuestStepObjective objective) in running)
                {
                    RunFixes(session, quest, cfg, objective, objective.ObjectiveState switch { StateInProgress => 3, >= StateExit => 4, _ => 1 });
                }
                foreach ((Theatre5DlcQuest quest, DlcQuestObjectiveTable cfg, Theatre5DlcQuestStepObjective objective) in running)
                {
                    if (objective.ObjectiveState is StateEnter or StateScriptEnter)
                        RunList(session, Actions(cfg, "EnterActions"), new QuestActionContext(quest.QuestId, cfg.Id, StateScriptEnter), objective, () => EnterDone(session, quest, cfg));
                    else if (objective.ObjectiveState is StateExit or StateScriptExit)
                        RunList(session, Actions(cfg, "ExitActions"), new QuestActionContext(quest.QuestId, cfg.Id, StateScriptExit), objective, () => ExitDone(session, quest, cfg));
                }
            });

        // Leaving the world: nav points go (one RpcRemoveNavPoint each), superseded list completions are ignored and
        // pending lists restart at the next RpcPlayerEnterLevelComplete.
        internal static void OnPlayerLeaveLevel(Session session)
        {
            Rt rt = Of(session);
            rt.Epoch++;
            rt.FuncHeld.Clear(); // the client's fight, and its counters, are gone
            rt.Deferred.Clear();
            rt.Running.Clear();
            rt.DramaSent.Clear();
            rt.LevelLoaded = false;
            rt.ObjectsLoaded = false;
            SuspendTimer(session);
            // Retail removes them ordered by quest (2002050 before 20030101 in both captured leaves).
            foreach (int objectiveId in rt.Navs.Keys.OrderBy(id => id).ToList())
                RemoveNavs(session, objectiveId);
        }

        // RpcSetSystemFuncEntryEnableBatchRequest [EnableList, DisableList], recording the net effect on the client's counters.
        internal static void SendFuncEntry(Session session, List<int> enable, List<int> disable)
        {
            Dictionary<int, int> held = Of(session).FuncHeld;
            foreach (int type in enable)
                held[type] = held.GetValueOrDefault(type) - 1;
            foreach (int type in disable)
                held[type] = held.GetValueOrDefault(type) + 1;
            session.SendPush("XRpcCommon", BigWorldXRpc.Common("RpcSetSystemFuncEntryEnableBatchRequest", BigWorldXRpc.Args(enable, disable), 0));
        }

        // Before RpcPlayerSwitchLevelNotify: the client's XSystemFuncManager keeps its counters across the switch and its
        // OnLeaveLevel re-enables the left level's Level.DisableSystemFuncEnumStr once per type [DUMP48 0x1F1F070 ->
        // SetFuncEntryEnableInternal 0x1F1F9A0: a count below 0 logs "屏蔽计数小于0" and clamps]. So undo what the server applied: give
        // back the disables it added (enable per count) and re-arm the level default a quest enable consumed (one disable).
        internal static void OnLevelSwitching(Session session, int fromLevel)
        {
            Dictionary<int, int> held = Of(session).FuncHeld;
            string? defaultsStr = BigWorldModule.Levels.Value.GetValueOrDefault(fromLevel)?.DisableSystemFuncEnumStr;
            HashSet<int> defaults = defaultsStr?.Split('|', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToHashSet() ?? [];
            List<int> enable = [], disable = [];
            foreach ((int type, int count) in held.OrderBy(p => p.Key))
                if (count > 0)
                    enable.AddRange(Enumerable.Repeat(type, count));
                else if (count < 0 && defaults.Contains(type))
                    disable.Add(type);
            held.Clear();
            if (enable.Count + disable.Count > 0)
                session.SendPush("XRpcCommon", BigWorldXRpc.Common("RpcSetSystemFuncEntryEnableBatchRequest", BigWorldXRpc.Args(enable, disable), 0));
        }

        #endregion

        #region Client requests

        internal static bool HandleXRpc(Session session, string rpcName, byte[] args)
        {
            switch (rpcName)
            {
                case "RpcQuestObjectiveCompleteRequest":
                    object? decoded = BigWorldQuestModule.Decode(args);
                    if (decoded is IDictionary<object, object> map)
                        OnClientComplete(session, map.TryGetValue("QuestId", out object? q) ? BigWorldXRpc.ToInt(q) : 0,
                            map.TryGetValue("ObjectiveId", out object? o) ? BigWorldXRpc.ToInt(o) : 0);
                    else if (decoded is object?[] { Length: >= 2 } arr)
                        OnClientComplete(session, BigWorldXRpc.ToInt(arr[0]), BigWorldXRpc.ToInt(arr[1]));
                    return true;
                case "RpcQuestDeliverItemsRequest":
                    if (BigWorldQuestModule.Decode(args) is object?[] { Length: >= 2 } deliver && deliver[1] is IDictionary<object, object> items)
                        OnDeliverItems(session, BigWorldXRpc.ToInt(deliver[0]), items.ToDictionary(kv => BigWorldXRpc.ToInt(kv.Key), kv => BigWorldXRpc.ToInt(kv.Value)));
                    return true;
                case "RpcDramaFinishNotify":
                    if (BigWorldQuestModule.Decode(args) is object?[] { Length: >= 1 } drama && drama[0] is string dramaName)
                    {
                        Dictionary<int, List<int>> history = drama.Length > 2 && drama[2] is IDictionary<object, object> picks
                            ? picks.ToDictionary(kv => BigWorldXRpc.ToInt(kv.Key), kv => (kv.Value as IEnumerable<object?> ?? []).Select(BigWorldXRpc.ToInt).ToList())
                            : [];
                        RecordDecisions(session, dramaName, history);
                        OnDramaFinish(session, dramaName);
                    }
                    return true;
                case "RpcUiClosedNotify":
                    if (BigWorldQuestModule.Decode(args) is object?[] { Length: >= 1 } ui && ui[0] is string uiName)
                        OnUiClosed(session, uiName, ui.Length > 1 && ui[1] is IEnumerable<object?> ints ? ints.Select(BigWorldXRpc.ToInt).ToList() : []);
                    return true;
                case "RpcNarrativeCompleteNotify":
                    if (BigWorldQuestModule.Decode(args) is object?[] { Length: >= 1 } narrative)
                        OnNarrativeComplete(session, BigWorldXRpc.ToInt(narrative[0]));
                    return true;
                // Retail acks RpcShortMessageReadComplete bare: the objective completes with the game-server message record.
                case "RpcShortMessageReadComplete":
                    return true;
                case "RpcQuestPopupClosedNotify":
                    if (BigWorldQuestModule.Decode(args) is object?[] { Length: >= 2 } popup && BigWorldXRpc.ToInt(popup[1]) == BigWorldQuestModule.PopupUndertake)
                        Scoped(session, false, () =>
                        {
                            // AscNet policy: closing the Undertake popup of a Ready quest undertakes it.
                            if (Data(session).ActiveQuests.GetValueOrDefault(BigWorldXRpc.ToInt(popup[0])) is { DynamicData.QuestState: QuestReady } ready)
                            {
                                Undertake(session, ready);
                                BigWorldQuestModule.AutoActivate(session);
                            }
                        });
                    return true;
                case "RpcPlayerEnterLevelComplete":
                    OnPlayerEnterLevelComplete(session);
                    return true;
                case "RpcTakePhotoCompleteNotify":
                    if (BigWorldQuestModule.Decode(args) is object?[] { Length: >= 5 } photo)
                        OnTakePhoto(session, IntList(photo[0]), IntList(photo[1]), BigWorldXRpc.ToInt(photo[2]), StringMap(photo[3]), StringMap(photo[4]));
                    return true;
                case "RpcRLObjectLoadCompleted":
                    OnObjectsLoaded(session);
                    return true;
                default:
                    return false;
            }
        }

        // DlcQuestUpdateRequest (client-hosted engine): the client reports objective states, the server records them and
        // advances the serial state machine without running level actions (the client already did).
        internal static void ApplyClientUpdate(Session session, IEnumerable<Theatre5DlcQuestStepObjective> entries) =>
            Scoped(session, true, () =>
            {
                Theatre5DlcQuestInfo data = Data(session);
                foreach (Theatre5DlcQuestStepObjective entry in entries)
                {
                    DlcQuestObjectiveTable cfg = BigWorldQuestModule.Objectives.Value[entry.Id];
                    if (!data.ActiveQuests.TryGetValue(cfg.QuestId, out Theatre5DlcQuest? quest))
                        continue; // finished earlier in this batch
                    if (quest.DynamicData!.QuestState == QuestReady)
                        Undertake(session, quest);
                    if (!quest.DynamicData.Steps.TryGetValue(cfg.StepId, out Theatre5DlcQuestStep? step) || step.StepState != StepInProgress
                        || !step.Objectives.TryGetValue(cfg.Id, out Theatre5DlcQuestStepObjective? current))
                        continue;
                    if (entry.ObjectiveState == StateFinished && current.ObjectiveState != StateFinished)
                    {
                        MarkPending(current, true, false);
                        MarkPending(current, false, false);
                        RemoveNavs(session, cfg.Id);
                        StopTimer(session, cfg);
                        Finish(session, quest, cfg);
                    }
                    else if (entry.ObjectiveState > current.ObjectiveState)
                    {
                        current.ObjectiveState = entry.ObjectiveState;
                        Of(session).Changes.Touched.Add(cfg.QuestId);
                    }
                }
                BigWorldQuestModule.AutoActivate(session);
            });

        #endregion
    }
}
