using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.miniactivity.envelope;
using AscNet.Table.V2.share.miniactivity.musicgame.concertpreheating;
using AscNet.Table.V2.share.pbr;
namespace AscNet.GameServer.Handlers
{
    /// <summary>
    /// 4.7 table/schedule-backed event families: Envelope (capture-proven end-to-end), PBR
    /// activity root, and Concert Pre-Heating login state. Every activation is derived from the
    /// current ActivitySchedule + version tables; no captured ID or URL is ever hardcoded.
    /// </summary>
    internal static class Version47EventModule
    {
        // No capture exercises the Envelope failure path, so this code is unverified retail value.
        // ponytail: unverified Envelope not-open code (20428001); any non-zero signals failure and
        // the acceptance only requires "not falsely returns success". Replace once a retail
        // Envelope error capture is available.
        private const int EnvelopeActivityNotOpen = 20428001;

        // 4.7 event daily grants roll over at 05:00 UTC.
        private static readonly DateTime BusinessDayEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static readonly Lazy<IReadOnlyList<EnvelopeActivityTable>> EnvelopeActivities = new(() =>
            TableReaderV2.Parse<EnvelopeActivityTable>());
        private static readonly Lazy<IReadOnlyList<PBRActivityTable>> PbrActivities = new(() =>
            TableReaderV2.Parse<PBRActivityTable>());
        private static readonly Lazy<IReadOnlyList<ConcertPreHeatingActivityTable>> ConcertActivities = new(() =>
            TableReaderV2.Parse<ConcertPreHeatingActivityTable>());
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
            session.SendResponse(StartConcertPreHeating(request.StageId, DateTimeOffset.UtcNow), packet.Id);
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
                HasReward = state.LastDailyGrantBusinessDay != BusinessDay(now)
            };
        }

        [RequestPacketHandler("EnvelopeEnterRequest")]
        public static void EnvelopeEnter(Session session, Packet.Request packet)
        {
            HandleEnvelopeEnter(session, packet.Id, DateTimeOffset.UtcNow);
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

            EnvelopeState state = ReconcileEnvelope(session.player, activity.Id);
            response.Code = 0;
            int businessDay = BusinessDay(now);
            if (state.LastDailyGrantBusinessDay != businessDay)
            {
                RewardApplicationResult result = RewardHandler.ApplyRewards(
                    RewardHandler.GetRewardGoods(activity.DailyTicketRewardId), session);
                if (result.RewardGoods.Count > 0)
                {
                    response.RewardGoodsList.AddRange(result.RewardGoods);
                    state.LastDailyGrantBusinessDay = businessDay;
                    session.inventory.Save();
                    session.character.Save();
                    session.player.Save();
                }
                else
                {
                    if (result.DormFurnitureChanged || result.GatherRewardIds.Count > 0 || result.HeadPortraitData.Heads.Count > 0)
                        session.player.Save();
                    session.log.Error(
                        $"No reward is configured for Envelope daily ticket reward {activity.DailyTicketRewardId}.");
                }
                result.SendPushes(session);
            }

            response.OpenedCharacterIds = state.OpenedCharacterIds.Distinct().Order().ToList();
            response.InstrumentBindings = new Dictionary<int, int>(state.InstrumentBindings);
            response.AvgWatchedCharacterIds = state.AvgWatchedCharacterIds.Distinct().Order().ToList();
            return response;
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
            return state;
        }

        /// <summary>Ordinal of the UTC business day that rolls over at 05:00 UTC.</summary>
        internal static int BusinessDay(DateTimeOffset now) =>
            checked((int)(now.UtcDateTime.AddHours(-5).Date - BusinessDayEpoch).TotalDays);
    }
}
