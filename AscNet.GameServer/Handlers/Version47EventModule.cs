using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.miniactivity.envelope;
using AscNet.Table.V2.share.miniactivity.musicgame.concertpreheating;
using AscNet.Table.V2.share.pbr;
using AscNet.Table.V2.share.reward;
using AscNet.Table.V2.share.task;
namespace AscNet.GameServer.Handlers
{
    /// <summary>
    /// 4.7 table/schedule-backed event families: Envelope (capture-proven end-to-end), PBR
    /// activity root, and Concert Pre-Heating login state. Every activation is derived from the
    /// current ActivitySchedule + version tables; no captured ID or URL is ever hardcoded.
    /// </summary>
    internal static class Version47EventModule
    {
        // Envelope errors are sourced from the installed CodeText table; packet-level retail
        // failure ordering is not captured.
        private const int EnvelopeActivityNotOpen = 20428001;

        // 4.7 event daily grants roll over at 05:00 UTC.
        private static readonly DateTime BusinessDayEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // Test/consumer seam (same pattern as DrawManager.UtcNow / TransfiniteTowerModule.Clock): registered
        // Envelope/Concert Pre-Heating handlers and Envelope task evaluation must be exercisable while the
        // authored window is closed. Production default stays the real clock.
        internal static Func<DateTimeOffset> Clock = () => DateTimeOffset.UtcNow;

        private static readonly Lazy<IReadOnlyList<EnvelopeActivityTable>> EnvelopeActivities = new(() =>
            TableReaderV2.Parse<EnvelopeActivityTable>());
        private static readonly Lazy<IReadOnlyList<EnvelopeListTable>> EnvelopeLists = new(() =>
            TableReaderV2.Parse<EnvelopeListTable>());
        private static readonly Lazy<IReadOnlyList<EnvelopeInstrumentTable>> EnvelopeInstruments = new(() =>
            TableReaderV2.Parse<EnvelopeInstrumentTable>());
        private static readonly Lazy<HashSet<int>> EnvelopeCharacters = new(() =>
            TableReaderV2.Parse<EnvelopeCharacterTable>().Select(row => row.Id).ToHashSet());
        private static readonly Lazy<IReadOnlyList<PBRActivityTable>> PbrActivities = new(() =>
            TableReaderV2.Parse<PBRActivityTable>());
        private static readonly Lazy<IReadOnlyList<ConcertPreHeatingActivityTable>> ConcertActivities = new(() =>
            TableReaderV2.Parse<ConcertPreHeatingActivityTable>());
        private static readonly Lazy<IReadOnlyList<ConcertPreHeatingStageTable>> ConcertStages = new(() =>
            TableReaderV2.Parse<ConcertPreHeatingStageTable>());
        private static readonly Lazy<IReadOnlyList<ConcertVideoConfigTable>> VideoConfigs = new(() =>
            TableReaderV2.Parse<ConcertVideoConfigTable>());

        /// <summary>
        /// Login startup push stream for the 4.7 event families, in the retail-observed order:
        /// Concert (PreHeating + VideoConfig), then PBR, then Envelope. Each family is emitted only
        /// when its activity is currently open (schedule + table) and is independent of the others.
        /// </summary>
        public static void SendLoginPushes(Session session, DateTimeOffset now)
        {
            SendConcertLoginPushes(session, now);
            SendPbrLoginPush(session, now);
            SendEnvelopeLoginPush(session, now);
        }

        // ---- Concert Pre-Heating ----

        private static void SendConcertLoginPushes(Session session, DateTimeOffset now)
        {
            NotifyConcertPreHeating? preHeating = BuildConcertNotify(session.player, now);
            if (preHeating is not null)
                session.SendPush(preHeating);

            NotifyConcertVideoConfig? videoConfig = BuildConcertVideoConfigNotify(now);
            if (videoConfig is not null)
                session.SendPush(videoConfig);
        }

        internal static NotifyConcertPreHeating? BuildConcertNotify(Player player, DateTimeOffset now)
        {
            ConcertPreHeatingActivityTable? activity = ActiveConcert(now);
            if (activity is null)
                return null;

            ConcertPreHeatingState state = ReconcileConcert(player, activity.Id);
            return new NotifyConcertPreHeating
            {
                ConcertPreHeatingDataDb = new ConcertPreHeatingDataDb
                {
                    ActivityId = activity.Id,
                    StageFinish = state.CompletedStageIds
                        .Distinct()
                        .Order()
                        .Select(stageId => new ConcertPreHeatingStageFinish { StageId = stageId })
                        .ToList()
                }
            };
        }

        internal static NotifyConcertVideoConfig? BuildConcertVideoConfigNotify(DateTimeOffset now)
        {
            // Video map is built strictly from the current ConcertVideoConfig table; the captured
            // player URL is oracle-only and is never used at runtime.
            if (ActiveConcert(now) is null)
                return null;

            Dictionary<int, ConcertVideoConfigEntry> configs = new();
            foreach (ConcertVideoConfigTable row in VideoConfigs.Value)
            {
                configs[row.Id] = new ConcertVideoConfigEntry
                {
                    Id = row.Id,
                    LiveUrl = row.LiveUrl,
                    LiveTimeId = row.LiveTimeId,
                    RecordUrl = row.RecordUrl,
                    RecordTimeId = row.RecordTimeId
                };
            }
            if (configs.Count == 0)
                return null;

            return new NotifyConcertVideoConfig { ConcertVideoConfigs = configs };
        }

        private static ConcertPreHeatingActivityTable? ActiveConcert(DateTimeOffset now) =>
            ConcertActivities.Value
                .Where(candidate => candidate.TimeId > 0
                    && ActivityScheduleService.IsOpen(candidate.TimeId, now))
                .OrderByDescending(candidate => candidate.Id)
                .FirstOrDefault();

        private static ConcertPreHeatingState ReconcileConcert(Player player, int activityId)
        {
            ConcertPreHeatingState state = player.ConcertPreHeating;
            if (state.ActivityId == activityId)
                return state;

            state.ActivityId = activityId;
            state.CompletedStageIds = new List<int>();
            return state;
        }

        /// <summary>
        /// Stage-start gate for the 4.7 Concert Pre-Heating activity. No retail capture exercises
        /// the rejection path, so Code = 1 is the project's established generic non-zero rejection
        /// (not a captured retail error); stage validity comes from the ConcertPreHeatingActivity
        /// StageIds list and the window from its TimeId schedule. Pure validation: no state
        /// mutation, no push, no settle data.
        /// </summary>
        [RequestPacketHandler("ConcertPreHeatingStartRequest")]
        public static void ConcertPreHeatingStart(Session session, Packet.Request packet)
        {
            ConcertPreHeatingStartRequest request = packet.Deserialize<ConcertPreHeatingStartRequest>();
            session.SendResponse(StartConcertPreHeating(request.StageId, Clock()), packet.Id);
        }

        internal static ConcertPreHeatingStartResponse StartConcertPreHeating(int stageId, DateTimeOffset now)
        {
            ConcertPreHeatingStartResponse response = new();
            ConcertPreHeatingActivityTable? activity = ActiveConcert(now);
            if (activity is null || !activity.StageIds.Contains(stageId))
            {
                response.Code = 1;
                return response;
            }

            response.Code = 0;
            return response;
        }

        // Server-side task condition type used by the ConcertPreHeating task group (Condition.tsv type 140001;
        // the client has no evaluator, progress is pushed via NotifyTask). A condition with a single parameter
        // counts settled stages; one with a second parameter counts that stage id only.
        private const int ConcertStageSettledConditionType = 140001;

        [RequestPacketHandler("ConcertPreHeatingSettleRequest")]
        public static void ConcertPreHeatingSettle(Session session, Packet.Request packet)
        {
            ConcertPreHeatingSettleRequest request = packet.Deserialize<ConcertPreHeatingSettleRequest>();
            session.SendResponse(SettleConcertPreHeating(session, request.StageId, Clock()), packet.Id);
        }

        /// <summary>
        /// Marks a stage finished (once) and credits the activity task group; rewards are claimed through the
        /// task system, so a repeated settle is a no-op that re-sends the finished list.
        /// </summary>
        internal static ConcertPreHeatingSettleResponse SettleConcertPreHeating(Session session, int stageId, DateTimeOffset now)
        {
            ConcertPreHeatingActivityTable? activity = ActiveConcert(now);
            if (activity is null)
                return new() { Code = 20426001 };
            ConcertPreHeatingStageTable? stage = ConcertStages.Value.FirstOrDefault(row => row.Id == stageId);
            if (stage is null)
                return new() { Code = 20426002 };
            if (!activity.StageIds.Contains(stageId))
                return new() { Code = 20426003 };
            // AscNet policy (AGENTS.md missing-calendar availability): the per-stage TimeIds have no ActivitySchedule
            // row, so such stages are open for the activity window; a scheduled stage window is enforced.
            if ((int?)stage.TimeId is > 0 && ActivityScheduleService.TryGet((int)stage.TimeId!, out _)
                && !ActivityScheduleService.IsOpen((int)stage.TimeId!, now))
                return new() { Code = 20426004 };

            ConcertPreHeatingState state = ReconcileConcert(session.player, activity.Id);
            if (!state.CompletedStageIds.Contains(stageId))
            {
                state.CompletedStageIds.Add(stageId);
                session.player.Save();
                TaskModule.RecordTableDrivenProgress(session,
                [
                    (ConcertStageSettledConditionType, null, 1),
                    (ConcertStageSettledConditionType, stageId, 1)
                ]);
            }
            return new()
            {
                ActivityId = activity.Id,
                StageFinish = state.CompletedStageIds.Distinct().Order()
                    .Select(id => new ConcertPreHeatingStageFinish { StageId = id }).ToList()
            };
        }

        // ---- PBR ----

        private static void SendPbrLoginPush(Session session, DateTimeOffset now)
        {
            PbrActivityDataNotify? notify = BuildPbrNotify(session.player, now);
            if (notify is not null)
                session.SendPush(notify);
        }

        internal static PbrActivityDataNotify? BuildPbrNotify(Player player, DateTimeOffset now)
        {
            PBRActivityTable? activity = ActivePbr(now);
            if (activity is null)
                return null;

            PbrState state = ReconcilePbr(player, activity.Id);
            return new PbrActivityDataNotify
            {
                PbrDataDb = new PbrDataDb
                {
                    ActivityId = activity.Id,
                    SegmentSettleData = ToWireSegmentSettle(state.SegmentSettle),
                    MetaProgression = new PbrMetaProgression
                    {
                        UnlockNodes = state.MetaProgressionUnlockNodes.Distinct().Order().ToList()
                    },
                    StageRecords = state.StageRecords.Values
                        .OrderBy(record => record.StageId)
                        .ToDictionary(record => record.StageId, ToWireStageRecord),
                    Compendiums = new PbrCompendiums
                    {
                        CompendiumItems = state.CompendiumItems.Values
                            .OrderBy(item => item.ItemId)
                            .ToDictionary(item => item.ItemId, ToWireItem),
                        CompendiumMonsters = state.CompendiumMonsters.Values
                            .OrderBy(monster => monster.MonsterId)
                            .ToDictionary(monster => monster.MonsterId, ToWireMonster)
                    }
                }
            };
        }

        private static PBRActivityTable? ActivePbr(DateTimeOffset now) =>
            PbrActivities.Value
                .Where(candidate => candidate.TimeId is int timeId && timeId > 0
                    && ActivityScheduleService.IsOpen(timeId, now))
                .OrderByDescending(candidate => candidate.Id)
                .FirstOrDefault();

        private static PbrState ReconcilePbr(Player player, int activityId)
        {
            PbrState state = player.Pbr;
            if (state.ActivityId == activityId)
                return state;

            state.ActivityId = activityId;
            state.MetaProgressionUnlockNodes = new List<int>();
            state.StageRecords = new Dictionary<int, PbrStageRecordState>();
            state.CompendiumItems = new Dictionary<int, PbrItemState>();
            state.CompendiumMonsters = new Dictionary<int, PbrMonsterState>();
            state.SegmentSettle = null;
            return state;
        }

        /// <summary>
        /// Builds a PbrCompendiumPush for a real compendium mutation. No PBR action handler is
        /// registered (retail mutation ordering/validation is not captured), so this helper exists
        /// solely to emit server-authorized compendium updates once a mutation path lands.
        /// </summary>
        internal static PbrCompendiumPush BuildCompendiumPush(
            IEnumerable<PbrItemState>? addedItems,
            IEnumerable<PbrItemState>? updatedItems,
            IEnumerable<PbrMonsterState>? addedMonsters,
            IEnumerable<PbrMonsterState>? updatedMonsters) => new()
            {
                AddCompendiumItems = (addedItems ?? []).Select(ToWireItem).ToList(),
                UpdateCompendiumItems = (updatedItems ?? []).Select(ToWireItem).ToList(),
                AddCompendiumMonsters = (addedMonsters ?? []).Select(ToWireMonster).ToList(),
                UpdateCompendiumMonsters = (updatedMonsters ?? []).Select(ToWireMonster).ToList()
            };

        private static PbrStageRecord ToWireStageRecord(PbrStageRecordState state) => new()
        {
            StageId = state.StageId,
            HistoryMaxWave = state.HistoryMaxWave,
            IsPass = state.IsPass,
            IsPassWave = state.IsPassWave
        };

        private static PbrItem ToWireItem(PbrItemState state) => new()
        {
            ItemId = state.ItemId,
            UnlockTime = state.UnlockTime,
            GainNum = state.GainNum,
            TriggerNum = state.TriggerNum
        };

        private static PbrMonster ToWireMonster(PbrMonsterState state) => new()
        {
            MonsterId = state.MonsterId,
            DamageTotal = state.DamageTotal,
            BeKillNum = state.BeKillNum
        };

        private static PbrSegmentSettleData? ToWireSegmentSettle(PbrSegmentSettleState? state)
        {
            if (state is null)
                return null;

            return new PbrSegmentSettleData
            {
                State = state.State,
                StageId = state.StageId,
                ShopData = state.ShopData is null ? null : new PbrAdventureShopData
                {
                    ShopId = state.ShopData.ShopId,
                    MaxChooseCount = state.ShopData.MaxChooseCount,
                    MaxFreshCount = state.ShopData.MaxFreshCount,
                    UseChooseCount = state.ShopData.UseChooseCount,
                    UseFreshCount = state.ShopData.UseFreshCount,
                    SellItems = state.ShopData.SellItems.ToList()
                },
                Wave = state.Wave,
                CharacterId = state.CharacterId,
                CharacterLevel = state.CharacterLevel,
                CharacterExp = state.CharacterExp,
                BaseAttrs = new Dictionary<int, int>(state.BaseAttrs),
                CurAttrs = new Dictionary<int, int>(state.CurAttrs),
                MaxAttrs = new Dictionary<int, int>(state.MaxAttrs),
                Items = state.Items.ToDictionary(entry => entry.Key, entry => ToWireItem(entry.Value)),
                WaveMonsters = state.WaveMonsters.ToDictionary(entry => entry.Key, entry => ToWireMonster(entry.Value)),
                WaveObrs = state.WaveObrs.ToDictionary(entry => entry.Key, entry => ToWireItem(entry.Value))
            };
        }

        // ---- Envelope ----

        private static void SendEnvelopeLoginPush(Session session, DateTimeOffset now)
        {
            NotifyEnvelope? notify = BuildEnvelopeNotify(session.player, now);
            if (notify is not null)
                session.SendPush(notify);
        }

        internal static NotifyEnvelope? BuildEnvelopeNotify(Player player, DateTimeOffset now)
        {
            EnvelopeActivityTable? activity = ActiveEnvelope(now);
            if (activity is null)
                return null;

            EnvelopeState state = ReconcileEnvelope(player, activity.Id);
            return new NotifyEnvelope
            {
                ActivityId = activity.Id,
                HasReward = state.LastDailyGrantBusinessDay != BusinessDay(now) || state.PendingTaskReissues.Count > 0
            };
        }

        [RequestPacketHandler("EnvelopeEnterRequest")]
        public static void EnvelopeEnter(Session session, Packet.Request packet)
        {
            HandleEnvelopeEnter(session, packet.Id, Clock());
        }

        internal static void HandleEnvelopeEnter(Session session, int requestId, DateTimeOffset now)
        {
            session.SendResponse(EnterEnvelope(session, now), requestId);
        }

        internal static EnvelopeEnterResponse EnterEnvelope(Session session, DateTimeOffset now)
        {
            EnvelopeEnterResponse response = new();
            EnvelopeActivityTable? activity = ActiveEnvelope(now);
            if (activity is null)
            {
                response.Code = EnvelopeActivityNotOpen;
                return response;
            }

            TaskModule.EnsureMissionResets(session);
            EnvelopeState state = ReconcileEnvelope(session.player, activity.Id);
            response.Code = 0;
            int businessDay = BusinessDay(now);
            if (state.LastDailyGrantBusinessDay != businessDay || state.PendingTaskReissues.Count > 0)
            {
                // AscNet policy (no retail capture): every authored event business day from the
                // schedule start through today accrues one DailyTicketReward under its own receipt.
                // The player's first-ever entry day receives the authored FirstDayReward instead;
                // any existing receipt for this activity means that entry already happened. Receipts
                // make reconnects/retries non-multiplying; ActiveEnvelope already rejected closed windows.
                List<RewardGoodsTable> dailyGoods = RewardHandler.GetRewardGoods(activity.DailyTicketRewardId);
                List<RewardGoodsTable> firstGoods = RewardHandler.GetRewardGoods(activity.FirstDayRewardId);
                if (dailyGoods.Count == 0 || firstGoods.Count == 0)
                {
                    response.Code = 20428005;
                    return response;
                }
                ActivityScheduleService.TryGet(activity.TimeId, out ActivityScheduleEntry window);
                int startDay = window.StartTime > 0
                    ? Math.Min(businessDay, BusinessDay(DateTimeOffset.FromUnixTimeSeconds(window.StartTime)))
                    : businessDay;
                startDay = Math.Max(startDay, state.LastDailyGrantBusinessDay + 1);
                string dailyPrefix = $"envelope-daily:{activity.Id}:";
                string firstPrefix = $"envelope-first:{activity.Id}:";
                // AscNet policy: the business day that owns FirstDayRewardId is frozen by its own receipt key, so a
                // retry whose marker write was lost rebuilds the grant it already paid (same key, same goods) rather
                // than re-planning that day as a plain daily ticket and reporting the wrong composition.
                int? firstDay = null;
                foreach (string claim in session.inventory.AppliedRewardClaims)
                {
                    if (claim.StartsWith(firstPrefix, StringComparison.Ordinal)
                        && int.TryParse(claim[firstPrefix.Length..], out int claimedDay))
                    {
                        firstDay = claimedDay;
                        break;
                    }
                }
                bool firstEntry = firstDay is null && state.LastDailyGrantBusinessDay <= 0
                    && !session.inventory.AppliedRewardClaims.Any(claim =>
                        claim.StartsWith(dailyPrefix, StringComparison.Ordinal));
                if (firstEntry)
                    firstDay = businessDay;
                List<RewardGrant> daily = Enumerable.Range(startDay, Math.Max(0, businessDay - startDay + 1))
                    .Select(day => day == firstDay
                        ? new RewardGrant(firstPrefix + day, firstGoods)
                        : new RewardGrant(dailyPrefix + day, dailyGoods))
                    .ToList();
                // Earned-but-unclaimed daily tasks captured at rollover reuse their own period claim key.
                Dictionary<int, TaskTable> tasks = TableReaderV2.Parse<TaskTable>()
                    .Where(TaskModule.IsEnvelopeTask).ToDictionary(task => task.Id);
                List<RewardGrant> reissued = state.PendingTaskReissues
                    .Where(pending => tasks.ContainsKey(pending.Value))
                    .Select(pending => new RewardGrant(pending.Key, RewardHandler.GetRewardGoods(tasks[pending.Value].RewardId ?? 0)))
                    .Where(grant => grant.Goods.Count > 0)
                    .ToList();
                RewardApplicationResult? dailyResult = daily.Count == 0 ? null : RewardHandler.ApplyRewardsOnceAndPersist(daily, session);
                RewardApplicationResult? taskResult = reissued.Count == 0 ? null : RewardHandler.ApplyRewardsOnceAndPersist(reissued, session);
                int previousDay = state.LastDailyGrantBusinessDay;
                Dictionary<string, int> previousPending = state.PendingTaskReissues;
                state.LastDailyGrantBusinessDay = businessDay;
                state.PendingTaskReissues = new Dictionary<string, int>();
                try { session.player.SaveChecked(); }
                catch
                {
                    state.LastDailyGrantBusinessDay = previousDay;
                    state.PendingTaskReissues = previousPending;
                    throw;
                }
                if (dailyResult is not null)
                {
                    response.RewardGoodsList.AddRange(dailyResult.RewardGoods);
                    dailyResult.SendPushes(session);
                }
                if (taskResult is not null)
                {
                    response.TaskRewardGoodsList.AddRange(taskResult.RewardGoods);
                    taskResult.SendPushes(session);
                }
            }

            response.OpenedCharacterIds = state.OpenedCharacterIds.Distinct().Order().ToList();
            TaskModule.SendEnvelopeTaskSync(session, now);
            response.InstrumentBindings = new Dictionary<int, int>(state.InstrumentBindings);
            response.AvgWatchedCharacterIds = state.AvgWatchedCharacterIds.Distinct().Order().ToList();
            return response;
        }

        [RequestPacketHandler("EnvelopeRecordAvgRequest")]
        public static void EnvelopeRecordAvg(Session session, Packet.Request packet) =>
            HandleEnvelopeRecordAvg(session, packet.Deserialize<EnvelopeRecordAvgRequest>(), packet.Id, Clock());

        internal static void HandleEnvelopeRecordAvg(Session session, EnvelopeRecordAvgRequest request, int requestId, DateTimeOffset now)
        {
            EnvelopeActivityTable? activity = ActiveEnvelope(now);
            int code = activity is null ? EnvelopeActivityNotOpen : 0;
            if (activity is not null)
            {
                EnvelopeState state = ReconcileEnvelope(session.player, activity.Id);
                if (!EnvelopeCharacters.Value.Contains(request.CharacterId))
                    code = 20428002;
                else if (!state.OpenedCharacterIds.Contains(request.CharacterId))
                    code = 20428008;
                else if (state.AvgWatchedCharacterIds.Contains(request.CharacterId))
                    code = 20428012;
                else
                {
                    state.AvgWatchedCharacterIds.Add(request.CharacterId);
                    session.player.Save();
                    TaskModule.SendEnvelopeTaskSync(session, now);
                }
            }
            session.SendResponse(new EnvelopeRecordAvgResponse { Code = code }, requestId);
        }

        [RequestPacketHandler("EnvelopeOpenRequest")]
        public static void EnvelopeOpen(Session session, Packet.Request packet) =>
            HandleEnvelopeOpen(session, packet.Deserialize<EnvelopeOpenRequest>(), packet.Id, Clock());

        internal static void HandleEnvelopeOpen(Session session, EnvelopeOpenRequest request, int requestId, DateTimeOffset now)
        {
            EnvelopeActivityTable? activity = ActiveEnvelope(now);
            int code = activity is null ? EnvelopeActivityNotOpen : 0;
            if (activity is not null)
            {
                EnvelopeState state = ReconcileEnvelope(session.player, activity.Id);
                EnvelopeListTable? envelope = EnvelopeLists.Value.FirstOrDefault(row => row.Id == request.Id);
                if (envelope is null)
                    code = 20428003;
                else if (state.OpenedCharacterIds.Contains(envelope.CharacterId))
                    code = 20428004;
                else
                    code = OpenEnvelopeCharacter(session, activity, state, envelope.CharacterId, false);
            }
            if (code == 0)
                TaskModule.SendEnvelopeTaskSync(session, now);
            session.SendResponse(new EnvelopeOpenResponse { Code = code }, requestId);
        }

        [RequestPacketHandler("EnvelopeSelectOpenRequest")]
        public static void EnvelopeSelectOpen(Session session, Packet.Request packet) =>
            HandleEnvelopeSelectOpen(session, packet.Deserialize<EnvelopeSelectOpenRequest>(), packet.Id, Clock());

        internal static void HandleEnvelopeSelectOpen(Session session, EnvelopeSelectOpenRequest request, int requestId, DateTimeOffset now)
        {
            EnvelopeActivityTable? activity = ActiveEnvelope(now);
            int code = activity is null ? EnvelopeActivityNotOpen : 0;
            if (activity is not null)
            {
                EnvelopeState state = ReconcileEnvelope(session.player, activity.Id);
                if (!EnvelopeCharacters.Value.Contains(request.CharacterId))
                    code = 20428002;
                else if (state.OpenedCharacterIds.Contains(request.CharacterId))
                    code = 20428004;
                else
                    code = OpenEnvelopeCharacter(session, activity, state, request.CharacterId, true);
            }
            if (code == 0)
                TaskModule.SendEnvelopeTaskSync(session, now);
            session.SendResponse(new EnvelopeSelectOpenResponse { Code = code }, requestId);
        }

        private static int OpenEnvelopeCharacter(Session session, EnvelopeActivityTable activity, EnvelopeState state, int characterId, bool selected)
        {
            if (activity.TicketItemId <= 0 || (selected && activity.SelectChoiceItemId <= 0))
                return 20428005;
            if ((session.inventory.Items.FirstOrDefault(item => item.Id == activity.TicketItemId)?.Count ?? 0) < 1
                || (selected && (session.inventory.Items.FirstOrDefault(item => item.Id == activity.SelectChoiceItemId)?.Count ?? 0) < 1))
                return 20012004;

            NotifyItemDataList changed = new();
            changed.ItemDataList.Add(session.inventory.Do(activity.TicketItemId, -1));
            if (selected)
                changed.ItemDataList.Add(session.inventory.Do(activity.SelectChoiceItemId, -1));
            state.OpenedCharacterIds.Add(characterId);
            session.inventory.Save();
            session.player.Save();
            session.SendPush(changed);
            return 0;
        }

        [RequestPacketHandler("EnvelopeBindRequest")]
        public static void EnvelopeBind(Session session, Packet.Request packet) =>
            HandleEnvelopeBind(session, packet.Deserialize<EnvelopeBindRequest>(), packet.Id, Clock());

        internal static void HandleEnvelopeBind(Session session, EnvelopeBindRequest request, int requestId, DateTimeOffset now)
        {
            EnvelopeActivityTable? activity = ActiveEnvelope(now);
            int code = activity is null ? EnvelopeActivityNotOpen : 0;
            if (activity is not null)
            {
                EnvelopeState state = ReconcileEnvelope(session.player, activity.Id);
                Dictionary<int, int>? bindings = request.Bindings;
                if (bindings is null || bindings.Count > EnvelopeInstruments.Value.Count)
                    code = 20428010;
                else
                {
                    foreach ((int instrumentId, int characterId) in bindings)
                    {
                        EnvelopeInstrumentTable? instrument = EnvelopeInstruments.Value.FirstOrDefault(row => row.Id == instrumentId);
                        if (instrument is null) { code = 20428006; break; }
                        if (state.OpenedCharacterIds.Count < instrument.OpenTarget) { code = 20428007; break; }
                        if (!state.OpenedCharacterIds.Contains(characterId)) { code = 20428008; break; }
                    }
                    if (code == 0 && bindings.Values.Distinct().Count() != bindings.Count)
                        code = 20428009;
                    if (code == 0)
                    {
                        state.InstrumentBindings = new Dictionary<int, int>(bindings);
                        session.player.Save();
                        TaskModule.SendEnvelopeTaskSync(session, now);
                    }
                }
            }
            session.SendResponse(new EnvelopeBindResponse { Code = code }, requestId);
        }

        private static EnvelopeActivityTable? ActiveEnvelope(DateTimeOffset now) =>
            EnvelopeActivities.Value
                .Where(candidate => candidate.TimeId > 0
                    && ActivityScheduleService.IsOpen(candidate.TimeId, now))
                .OrderByDescending(candidate => candidate.Id)
                .FirstOrDefault();

        private static EnvelopeState ReconcileEnvelope(Player player, int activityId)
        {
            EnvelopeState state = player.Envelope;
            if (state.ActivityId == activityId)
                return state;

            state.ActivityId = activityId;
            state.LastDailyGrantBusinessDay = 0;
            state.OpenedCharacterIds = new List<int>();
            state.InstrumentBindings = new Dictionary<int, int>();
            state.AvgWatchedCharacterIds = new List<int>();
            state.PendingTaskReissues = new Dictionary<string, int>();
            return state;
        }

        /// <summary>Ordinal of the UTC business day that rolls over at 05:00 UTC.</summary>
        internal static int BusinessDay(DateTimeOffset now) =>
            checked((int)(now.UtcDateTime.AddHours(-5).Date - BusinessDayEpoch).TotalDays);
    }
}
