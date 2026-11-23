using System.Security.Cryptography;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.bigworld.common;
using AscNet.Table.V2.share.bigworld.common.course;
using AscNet.Table.V2.share.bigworld.common.helpcourse;
using AscNet.Table.V2.share.bigworld.common.message;
using AscNet.Table.V2.share.bigworld.common.news;
using AscNet.Table.V2.share.bigworld.common.photograph;

namespace AscNet.GameServer.Handlers.BigWorld
{
    // Album/photograph, course, short messages, news popups and help courses (teach).
    internal static class BigWorldArchiveModule
    {
        // CodeText ids (share/text/CodeText.tsv).
        private const int BigWorldAlbumFull = 25200094;                   // album is full
        private const int BigWorldAlbumInvalidDeleteIds = 25200095;       // invalid delete ids
        private const int BigWorldAlbumPhotoRemarkIsNullOrEmpty = 25200096;
        private const int BigWorldAlbumPhotoRemarkInvalid = 25200097;
        private const int BigWorldAlbumPhotoRemarkTooLong = 25200098;
        private const int BigWorldAlbumPhotoNotFound = 25200099;
        private const int BigWorldCourseNotOpen = 20290001;
        private const int BigWorldCourseInvalidContentId = 20290002;
        private const int BigWorldCourseInvalidExploreId = 20290003;
        private const int BigWorldCourseTaskCntDataIsNull = 20290004;     // version has no task progress rewards
        private const int BigWorldCourseTaskNoAvailableReward = 20290005;
        private const int BigWorldCourseExploreIdConfigEmpty = 20290006;
        private const int BigWorldCourseExploreCompleteRewardAlreadyGet = 20290008;
        private const int BigWorldCourseExploreCompleteRewardNotReady = 20290009;
        private const int BigWorldCourseExploreRewardAlreadyGet = 20290011;
        private const int BigWorldCourseExploreUnsupportType = 20290012;
        private const int BigWorldCourseInvalidPoiId = 20290013;
        private const int BigWorldCourseExplorePoiProgressNotEnough = 20290014;
        private const int BigWorldCourseInvalidCoreElementId = 20290016;
        private const int BigWorldCourseCoreElementAlreadyRead = 20290017;
        private const int BigWorldCourseExploreNoAvailableReward = 20290020;
        private const int BigWorldMessageStepCfgNull = 25200009;
        private const int BigWorldMessageStepRepeat = 25200011;
        private const int BigWorldMessageIsFinish = 25200012;
        internal const int BigWorldMessageIsActive = 25200018;
        private const int BigWorldMessageNotActive = 25200019;
        private const int BigWorldHelpCourseCfgNotFound = 25200013;
        private const int BigWorldHelpCourseDataIsUnlocked = 25200014;
        private const int BigWorldHelpCourseDataNotUnlock = 25200015;
        private const int BigWorldHelpCourseDataIsRead = 25200016;
        private const int BigWorldNewsIdNotFound = 25200116;
        private const int BigWorldNewsIdInvalid = 25200117;              // already popped / out of show time
        private const int BigWorldNewsIdNotUnlocked = 25200118;

        // Client XEnumConst.BWMessage.MessageState.
        private const int MessageNotFinish = 0, MessageFinish = 1, MessageNotRead = 2;

        private static readonly Lazy<Dictionary<string, string>> Config = new(() =>
            TableReaderV2.Parse<BigWorldConfigTable>().ToDictionary(row => row.Key, row => row.Value));
        private static readonly Lazy<HashSet<int>> Filters = new(() => TableReaderV2.Parse<BigWorldPhotographFiltersTable>().Select(row => row.Id).ToHashSet());
        private static readonly Lazy<HashSet<int>> Actions = new(() => TableReaderV2.Parse<BigWorldPhotographActionsTable>().Select(row => row.Id).ToHashSet());
        private static readonly Lazy<List<BigWorldCourseVersionTable>> Versions = new(() =>
            TableReaderV2.Parse<BigWorldCourseVersionTable>().OrderBy(row => row.VersionId).ToList());
        private static readonly Lazy<Dictionary<int, BigWorldCourseContentTable>> Contents = new(() =>
            TableReaderV2.Parse<BigWorldCourseContentTable>().ToDictionary(row => row.ContentId));
        private static readonly Lazy<Dictionary<int, BigWorldCourseExploreTable>> Explores = new(() =>
            TableReaderV2.Parse<BigWorldCourseExploreTable>().ToDictionary(row => row.ExploreId));
        private static readonly Lazy<Dictionary<int, BigWorldCourseExplorePoiTable>> Pois = new(() =>
            TableReaderV2.Parse<BigWorldCourseExplorePoiTable>().ToDictionary(row => row.Id));
        private static readonly Lazy<List<BigWorldCourseCoreTable>> Cores = new(() => TableReaderV2.Parse<BigWorldCourseCoreTable>());
        private static readonly Lazy<List<BigWorldCourseTaskProgressRewardTable>> TaskRewards = new(() =>
            TableReaderV2.Parse<BigWorldCourseTaskProgressRewardTable>());
        private static readonly Lazy<Dictionary<int, BigWorldMessageTable>> Messages = new(() =>
            TableReaderV2.Parse<BigWorldMessageTable>().ToDictionary(row => row.Id));
        private static readonly Lazy<Dictionary<int, BigWorldMessageStepTable>> Steps = new(() =>
            TableReaderV2.Parse<BigWorldMessageStepTable>().ToDictionary(row => row.Id));
        private static readonly Lazy<Dictionary<int, BigWorldNewsTable>> News = new(() =>
            TableReaderV2.Parse<BigWorldNewsTable>().ToDictionary(row => row.Id));
        private static readonly Lazy<HashSet<int>> HelpCourseIds = new(() =>
            TableReaderV2.Parse<BigWorldHelpCourseTable>().Select(row => row.Id).ToHashSet());

        private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        private static BigWorldPlayerState State(Session session) => session.player.BigWorldState;

        internal static void FillPlayerData(Player player, BigWorldPlayerData data)
        {
            BigWorldPlayerState state = player.BigWorldState;
            data.BigWorldPhotographData = PhotographData(state);
            data.BigWorldMessageDict = MessageDict(state);
            data.BigWorldHelpCourseList = state.HelpCourses
                .Select(course => new BigWorldHelpCourseData { Id = course.Id, IsRead = course.IsRead, CreateTime = course.CreateTime })
                .ToList();
            data.NewsPopupData = state.NewsPopupIds.ToList();
        }

        // Sent right after the EnterWorld response.
        internal static void SendEnterPushes(Session session)
        {
            BigWorldPlayerState state = State(session);
            session.SendPush(new NotifyBigWorldAlbumUpdate
            {
                AlbumData = new BigWorldAlbumData
                {
                    PhotoIdSequence = state.AlbumPhotoIdSequence,
                    PhotoDatas = VisiblePhotos(state),
                    TaskHidePhotoData = TaskPhotos(state)
                }
            });
            session.SendPush(new NotifyBigWorldCourseData { Data = CourseData(session) });
        }

        #region Album / photograph

        [RequestPacketHandler("BigWorldAlbumDataRequest")]
        public static void BigWorldAlbumDataRequestHandler(Session session, Packet.Request packet)
        {
            BigWorldPlayerState state = State(session);
            session.SendResponse(new BigWorldAlbumDataResponse { PhotoDatas = VisiblePhotos(state), TaskPhotoData = TaskPhotos(state) }, packet.Id);
        }

        [RequestPacketHandler("BigWorldAlbumAddPhotoRequest")]
        public static void BigWorldAlbumAddPhotoRequestHandler(Session session, Packet.Request packet)
        {
            BigWorldAlbumAddPhotoRequest request = packet.Deserialize<BigWorldAlbumAddPhotoRequest>();
            BigWorldPlayerState state = State(session);
            // AscNet policy: hidden task photos are not counted against AlbumCapacity (the client never lists them).
            if (!request.IsTaskHidePhoto && state.AlbumPhotos.Count(photo => !photo.IsTaskHidePhoto) >= ConfigInt("AlbumCapacity"))
            {
                session.SendResponse(new BigWorldAlbumAddPhotoResponse { Code = BigWorldAlbumFull }, packet.Id);
                return;
            }

            BigWorldAlbumPhoto photo = new()
            {
                Id = ++state.AlbumPhotoIdSequence,
                // AscNet policy: CheckSalt is a random positive int; with Id it keys the client-side image file.
                CheckSalt = RandomNumberGenerator.GetInt32(1, int.MaxValue),
                IsTaskHidePhoto = request.IsTaskHidePhoto,
                TaskHideUniqueId = Math.Max(0, request.TaskHideUniqueId),
                // AscNet policy [INFERENCE]: message steps look photos up by PhotoRefId; the task shot id is the ref.
                MessageRefId = Math.Max(0, request.TaskHideUniqueId),
                CreateTime = Now
            };
            // AscNet policy: a re-shot of the same hidden task target replaces the older record.
            if (photo.IsTaskHidePhoto && photo.TaskHideUniqueId > 0)
                state.AlbumPhotos.RemoveAll(old => old.IsTaskHidePhoto && old.TaskHideUniqueId == photo.TaskHideUniqueId);
            state.AlbumPhotos.Add(photo);
            session.player.Save();
            session.SendResponse(new BigWorldAlbumAddPhotoResponse { PhotoData = ToPhotoData(photo) }, packet.Id);
        }

        [RequestPacketHandler("BigWorldAlbumDeletePhotoRequest")]
        public static void BigWorldAlbumDeletePhotoRequestHandler(Session session, Packet.Request packet)
        {
            List<int> ids = packet.Deserialize<BigWorldAlbumDeletePhotoRequest>().PhotoIds ?? [];
            BigWorldPlayerState state = State(session);
            if (ids.Count == 0 || ids.Any(id => !state.AlbumPhotos.Any(photo => photo.Id == id)))
            {
                session.SendResponse(new BigWorldAlbumDeletePhotoResponse { Code = BigWorldAlbumInvalidDeleteIds }, packet.Id);
                return;
            }
            state.AlbumPhotos.RemoveAll(photo => ids.Contains(photo.Id));
            session.player.Save();
            session.SendResponse(new BigWorldAlbumDeletePhotoResponse(), packet.Id);
        }

        [RequestPacketHandler("BigWorldAlbumUpdatePhotoRequest")]
        public static void BigWorldAlbumUpdatePhotoRequestHandler(Session session, Packet.Request packet)
        {
            BigWorldAlbumUpdatePhotoRequest request = packet.Deserialize<BigWorldAlbumUpdatePhotoRequest>();
            BigWorldAlbumPhoto? photo = State(session).AlbumPhotos.FirstOrDefault(row => row.Id == request.PhotoId);
            int code = photo is null ? BigWorldAlbumPhotoNotFound
                : string.IsNullOrWhiteSpace(request.Remark) ? BigWorldAlbumPhotoRemarkIsNullOrEmpty
                : request.Remark.Length > ConfigInt("AlbumMaxRemarkLength") ? BigWorldAlbumPhotoRemarkTooLong
                // ponytail: AscNet has no sensitive-word table; only control characters are rejected. Add a word filter when one is imported.
                : request.Remark.Any(char.IsControl) ? BigWorldAlbumPhotoRemarkInvalid
                : 0;
            if (code == 0)
            {
                photo!.Remark = request.Remark;
                session.player.Save();
            }
            session.SendResponse(new BigWorldAlbumUpdatePhotoResponse { Code = code }, packet.Id);
        }

        // Every BigWorldRewardService.Grant result goes through here: photo filter/action goods are unlocked
        // (slice-owned, not applied by Grant); course task progress items trigger NotifyBigWorldCourseTaskCntProgress.
        internal static void ApplySliceRewards(Session session, IReadOnlyList<RewardGoods> goods)
        {
            BigWorldPlayerState state = State(session);
            bool photographChanged = false;
            foreach (RewardGoods row in goods)
            {
                bool isFilter = Filters.Value.Contains(row.TemplateId);
                // Retail rewards also grant condition-unlocked (ObtainType 1) poses, e.g. goods 70002100 -> 31000102;
                // [INFERENCE] the server list is authoritative for ObtainType 0 and harmless for 1, so every granted template is recorded.
                if (!isFilter && !Actions.Value.Contains(row.TemplateId))
                    continue;
                List<int> list = isFilter ? state.UnlockedCameraFilters : state.UnlockedCharacterActions;
                if (!list.Contains(row.TemplateId))
                {
                    list.Add(row.TemplateId);
                    photographChanged = true;
                }
            }
            if (photographChanged)
            {
                session.player.Save();
                session.SendPush(new NotifyBigWorldPhotographDataUpdate { BigWorldPhotographData = PhotographData(state) });
            }

            HashSet<int> itemIds = goods.Select(row => row.TemplateId).ToHashSet();
            foreach (BigWorldCourseVersionTable version in Versions.Value)
            {
                BigWorldCourseContentTable? task = Content(version, 1);
                if (task is not null && task.TaskProgressItemId > 0 && itemIds.Contains(task.TaskProgressItemId))
                    session.SendPush(new NotifyBigWorldCourseTaskCntProgress { VersionId = version.VersionId, TotalProgress = TaskProgress(session, task) });
            }
        }

        private static BigWorldPhotographData PhotographData(BigWorldPlayerState state) => new()
        {
            UnlockedCameraFilters = state.UnlockedCameraFilters.ToList(),
            UnlockedCharacterActions = state.UnlockedCharacterActions.ToList()
        };

        private static List<BigWorldPhotoData> VisiblePhotos(BigWorldPlayerState state) =>
            state.AlbumPhotos.Where(photo => !photo.IsTaskHidePhoto).Select(ToPhotoData).ToList();

        private static Dictionary<int, BigWorldPhotoData> TaskPhotos(BigWorldPlayerState state) =>
            state.AlbumPhotos.Where(photo => photo.IsTaskHidePhoto).ToDictionary(photo => photo.Id, ToPhotoData);

        private static BigWorldPhotoData ToPhotoData(BigWorldAlbumPhoto photo) => new()
        {
            Id = photo.Id,
            CheckSalt = photo.CheckSalt,
            IsTaskHidePhoto = photo.IsTaskHidePhoto,
            MessageRefId = photo.MessageRefId,
            Remark = photo.Remark,
            CreateTime = photo.CreateTime
        };

        private static int ConfigInt(string key) => int.Parse(Config.Value[key]);

        #endregion

        #region Course

        [RequestPacketHandler("BigWorldCourseCoreSetReadRequest")]
        public static void BigWorldCourseCoreSetReadRequestHandler(Session session, Packet.Request packet)
        {
            BigWorldCourseCoreSetReadRequest request = packet.Deserialize<BigWorldCourseCoreSetReadRequest>();
            BigWorldCourseCoreSetReadResponse response = new();
            BigWorldCourseVersionTable? version = OpenVersion(session, request.VersionId);
            BigWorldCourseContentTable? core = version is null ? null : Content(version, 3);
            if (version is null || core is null)
            {
                response.Code = version is null ? BigWorldCourseNotOpen : BigWorldCourseInvalidContentId;
                session.SendResponse(response, packet.Id);
                return;
            }

            HashSet<int> valid = Cores.Value.Where(row => row.ContentId == core.ContentId).SelectMany(row => row.ElementIds).ToHashSet();
            List<int> requested = (request.ElementIds ?? []).Where(valid.Contains).Distinct().ToList();
            BigWorldPlayerState state = State(session);
            List<int> read = state.CourseReadElementIds.TryGetValue(version.VersionId, out List<int>? existing) ? existing : [];
            response.SuccessIds = requested.Where(id => !read.Contains(id)).ToList();
            if (requested.Count == 0)
                response.Code = BigWorldCourseInvalidCoreElementId;
            else if (response.SuccessIds.Count == 0)
                response.Code = BigWorldCourseCoreElementAlreadyRead;
            else
            {
                read.AddRange(response.SuccessIds);
                state.CourseReadElementIds[version.VersionId] = read;
                session.player.Save();
            }
            session.SendResponse(response, packet.Id);
        }

        [RequestPacketHandler("BigWorldCourseTaskCntGetRewardRequest")]
        public static void BigWorldCourseTaskCntGetRewardRequestHandler(Session session, Packet.Request packet)
        {
            int versionId = packet.Deserialize<BigWorldCourseTaskCntGetRewardRequest>().VersionId;
            BigWorldCourseTaskCntGetRewardResponse response = new();
            BigWorldCourseVersionTable? version = OpenVersion(session, versionId);
            BigWorldCourseContentTable? task = version is null ? null : Content(version, 1);
            List<BigWorldCourseTaskProgressRewardTable> rows = task is null ? [] : TaskRewards.Value.Where(row => row.ContentId == task.ContentId).ToList();
            BigWorldPlayerState state = State(session);
            List<BigWorldCourseTaskProgressRewardTable> claimable = task is null ? [] : rows
                .Where(row => row.Progress <= TaskProgress(session, task) && !state.CourseTaskRewardIds.Contains(row.Id)).ToList();
            response.Code = version is null ? BigWorldCourseNotOpen
                : task is null ? BigWorldCourseInvalidContentId
                : rows.Count == 0 ? BigWorldCourseTaskCntDataIsNull
                : claimable.Count == 0 ? BigWorldCourseTaskNoAvailableReward
                : 0;
            if (response.Code == 0)
            {
                // Mark first, then grant: a failed grant must not become repeatable.
                state.CourseTaskRewardIds.AddRange(claimable.Select(row => row.Id));
                session.player.Save();
                foreach (BigWorldCourseTaskProgressRewardTable row in claimable)
                    response.RewardGoodsList.AddRange(GrantReward(session, row.RewardId));
                response.GotRewardIds = rows.Select(row => row.Id).Where(state.CourseTaskRewardIds.Contains).ToList();
            }
            session.SendResponse(response, packet.Id);
        }

        [RequestPacketHandler("BigWorldCourseExploreCntGetRewardRequest")]
        public static void BigWorldCourseExploreCntGetRewardRequestHandler(Session session, Packet.Request packet)
        {
            int exploreId = packet.Deserialize<BigWorldCourseExploreCntGetRewardRequest>().ExploreId;
            BigWorldCourseExploreCntGetRewardResponse response = new();
            BigWorldPlayerState state = State(session);
            if (!Explores.Value.TryGetValue(exploreId, out BigWorldCourseExploreTable? explore))
                response.Code = BigWorldCourseInvalidExploreId;
            else if (VersionOfContent(explore.ContentId) is not { } version || explore.PoiIds.Count == 0)
                response.Code = BigWorldCourseExploreIdConfigEmpty;
            else if (OpenVersion(session, version.VersionId) is null)
                response.Code = BigWorldCourseNotOpen;
            else if (explore.Type is not (1 or 2))
                response.Code = BigWorldCourseExploreUnsupportType;
            else if (explore.PoiIds.Any(id => !Pois.Value.ContainsKey(id)))
                response.Code = BigWorldCourseInvalidPoiId;
            else if (state.CourseExploreRewardIds.Contains(exploreId))
                response.Code = BigWorldCourseExploreRewardAlreadyGet;
            else if (!ExploreComplete(state, explore))
                response.Code = BigWorldCourseExplorePoiProgressNotEnough;
            else if (explore.RewardId <= 0)
                response.Code = BigWorldCourseExploreNoAvailableReward;
            else
            {
                state.CourseExploreRewardIds.Add(exploreId);
                session.player.Save();
                response.RewardGoodsList = GrantReward(session, explore.RewardId);
            }
            session.SendResponse(response, packet.Id);
        }

        [RequestPacketHandler("BigWorldCourseExploreCntGetCompleteRewardRequest")]
        public static void BigWorldCourseExploreCntGetCompleteRewardRequestHandler(Session session, Packet.Request packet)
        {
            int versionId = packet.Deserialize<BigWorldCourseExploreCntGetCompleteRewardRequest>().VersionId;
            BigWorldCourseExploreCntGetCompleteRewardResponse response = new();
            BigWorldPlayerState state = State(session);
            BigWorldCourseVersionTable? version = OpenVersion(session, versionId);
            BigWorldCourseContentTable? content = version is null ? null : Content(version, 2);
            if (version is null)
                response.Code = BigWorldCourseNotOpen;
            else if (content is null)
                response.Code = BigWorldCourseInvalidContentId;
            else if (content.ExploreRewardId <= 0)
                response.Code = BigWorldCourseExploreNoAvailableReward;
            else if (state.CourseExploreCompleteVersionIds.Contains(versionId))
                response.Code = BigWorldCourseExploreCompleteRewardAlreadyGet;
            // AscNet policy: "complete" = every explore point of the content at its TotalProgress; claiming the
            // per-explore rewards first is not required (client red dot is ambiguous).
            else if (!Explores.Value.Values.Where(row => row.ContentId == content.ContentId).All(row => ExploreComplete(state, row)))
                response.Code = BigWorldCourseExploreCompleteRewardNotReady;
            else
            {
                state.CourseExploreCompleteVersionIds.Add(versionId);
                session.player.Save();
                response.RewardGoodsList = GrantReward(session, content.ExploreRewardId);
            }
            session.SendResponse(response, packet.Id);
        }

        // Core reports an explore point's absolute progress (it owns the level/place -> poi mapping).
        // Returns false when the stored count is already >= count (nothing persisted or pushed).
        internal static bool ReportExplorePoi(Session session, int poiId, int count)
        {
            if (!Pois.Value.TryGetValue(poiId, out BigWorldCourseExplorePoiTable? poi))
                throw new InvalidDataException($"BigWorldCourseExplorePoi {poiId} does not exist.");
            BigWorldCourseExploreTable explore = Explores.Value.Values.FirstOrDefault(row => row.PoiIds.Contains(poiId))
                ?? throw new InvalidDataException($"BigWorldCourseExplorePoi {poiId} belongs to no explore.");
            BigWorldCourseVersionTable version = VersionOfContent(explore.ContentId)
                ?? throw new InvalidDataException($"BigWorldCourseExplore {explore.ExploreId} belongs to no version.");

            BigWorldPlayerState state = State(session);
            int clamped = Math.Clamp(count, 0, poi.TotalProgress);
            if (state.CoursePoiCounts.GetValueOrDefault(poiId) >= clamped)
                return false;
            state.CoursePoiCounts[poiId] = clamped;
            session.player.Save();
            session.SendPush(new NotifyBigWorldCourseExploreProgress
            {
                VersionId = version.VersionId,
                ExploreId = explore.ExploreId,
                PoiId = poiId,
                Count = clamped
            });
            return true;
        }

        private static BigWorldCourseData CourseData(Session session)
        {
            BigWorldPlayerState state = State(session);
            BigWorldCourseData data = new();
            foreach (BigWorldCourseVersionTable version in Versions.Value)
            {
                BigWorldCourseVersionData entry = new() { VersionId = version.VersionId };
                if (Content(version, 1) is { } task)
                    entry.TaskCntData = new BigWorldCourseTaskCntData
                    {
                        ContentId = task.ContentId,
                        TotalProgress = TaskProgress(session, task),
                        GotRewardIds = TaskRewards.Value.Where(row => row.ContentId == task.ContentId && state.CourseTaskRewardIds.Contains(row.Id))
                            .Select(row => row.Id).ToList()
                    };
                if (Content(version, 2) is { } explore)
                    entry.ExploreCntData = new BigWorldCourseExploreCntData
                    {
                        ContentId = explore.ContentId,
                        IsGotCompleteReward = state.CourseExploreCompleteVersionIds.Contains(version.VersionId),
                        ExploreDatas = Explores.Value.Values.Where(row => row.ContentId == explore.ContentId).ToDictionary(row => row.ExploreId, row => new BigWorldCourseExploreData
                        {
                            ExploreId = row.ExploreId,
                            IsGotReward = state.CourseExploreRewardIds.Contains(row.ExploreId),
                            PoiCounts = row.PoiIds.ToDictionary(id => id, id => state.CoursePoiCounts.GetValueOrDefault(id))
                        })
                    };
                if (Content(version, 3) is { } core)
                    entry.CoreCntData = new BigWorldCourseCoreCntData
                    {
                        ContentId = core.ContentId,
                        ReadElementIds = state.CourseReadElementIds.GetValueOrDefault(version.VersionId)?.ToList() ?? []
                    };
                data.Datas[version.VersionId] = entry;
            }
            return data;
        }

        // ContentType 1 Task, 2 Explore, 3 Core.
        private static BigWorldCourseContentTable? Content(BigWorldCourseVersionTable version, int contentType) =>
            version.ContentIds.Select(id => Contents.Value.GetValueOrDefault(id)).FirstOrDefault(row => row?.ContentType == contentType);

        private static BigWorldCourseVersionTable? VersionOfContent(int contentId) =>
            Versions.Value.FirstOrDefault(row => row.ContentIds.Contains(contentId));

        // Client Agency:63-79: ConditionId passes and TimeId is in time.
        private static BigWorldCourseVersionTable? OpenVersion(Session session, int versionId) =>
            Versions.Value.FirstOrDefault(row => row.VersionId == versionId
                && BigWorldConditionService.Check(session.player, row.ConditionId)
                && InTime(row.TimeId));

        // Client XFunctionManager.CheckInTimeByTimeId(timeId, defaultOpen: true): no TimeId or no time data = open.
        private static bool InTime(int timeId) =>
            !ActivityScheduleService.TryGet(timeId, out ActivityScheduleEntry entry) || entry.IsOpen(DateTimeOffset.UtcNow);

        // TotalProgress = held count of the content's TaskProgressItemId (Star Medal).
        private static int TaskProgress(Session session, BigWorldCourseContentTable task) =>
            task.TaskProgressItemId <= 0 ? 0
                : (int)session.inventory.Items.Where(item => item.Id == task.TaskProgressItemId).Sum(item => (long)item.Count);

        private static bool ExploreComplete(BigWorldPlayerState state, BigWorldCourseExploreTable explore) =>
            explore.PoiIds.All(id => Pois.Value.TryGetValue(id, out BigWorldCourseExplorePoiTable? poi)
                && state.CoursePoiCounts.GetValueOrDefault(id) >= poi.TotalProgress);

        private static List<RewardGoods> GrantReward(Session session, int rewardId)
        {
            List<RewardGoods> goods = BigWorldRewardService.Grant(session, rewardId);
            ApplySliceRewards(session, goods);
            return goods;
        }

        #endregion

        #region Message

        // A quest action activates a short message. Returns 0 or BigWorldMessageIsActive.
        internal static int ActivateMessage(Session session, int messageId)
        {
            if (!Messages.Value.TryGetValue(messageId, out BigWorldMessageTable? message))
                throw new InvalidDataException($"BigWorldMessage {messageId} does not exist.");
            BigWorldPlayerState state = State(session);
            if (state.Messages.ContainsKey(messageId))
                return BigWorldMessageIsActive;
            state.Messages[messageId] = new BigWorldMessageRecord { State = MessageNotRead, CreateTime = Now };
            session.player.Save();
            session.SendPush(new NotifyBigWorldNotReadMessage { MessageId = messageId, StepId = message.FirstStepId, State = MessageNotRead });
            return 0;
        }

        internal static bool MessageFinished(Session session, int messageId) =>
            State(session).Messages.TryGetValue(messageId, out BigWorldMessageRecord? record) && record.State == MessageFinish;

        // A ReadShortMessage objective opens on a finished thread only when its quest is played again (every message belongs to
        // exactly one objective). The objective closes only from the message-finish notification, so the thread restarts unread: the
        // client ignores a repeated NotifyBigWorldNotReadMessage for a known id (XBigWorldMessageModel:AddUnReadMessage), so the
        // full record dict is resynced first and the notify then raises the unread marker/popup. AscNet policy [INF].
        internal static void ReplayMessage(Session session, int messageId)
        {
            if (!MessageFinished(session, messageId))
            {
                ActivateMessage(session, messageId);
                return;
            }
            BigWorldMessageRecord record = State(session).Messages[messageId];
            record.StepIds.Clear();
            record.State = MessageNotRead;
            record.CreateTime = Now;
            session.player.Save();
            SendMessageRecordUpdate(session);
            session.SendPush(new NotifyBigWorldNotReadMessage { MessageId = messageId, StepId = Messages.Value[messageId].FirstStepId, State = MessageNotRead });
        }

        [RequestPacketHandler("BigWorldMessageReadRecordRequest")]
        public static void BigWorldMessageReadRecordRequestHandler(Session session, Packet.Request packet)
        {
            BigWorldMessageReadRecordRequest request = packet.Deserialize<BigWorldMessageReadRecordRequest>();
            BigWorldPlayerState state = State(session);
            BigWorldMessageReadRecordResponse response = new();
            if (!state.Messages.TryGetValue(request.MessageId, out BigWorldMessageRecord? record) || !Messages.Value.TryGetValue(request.MessageId, out BigWorldMessageTable? message))
                response.Code = BigWorldMessageNotActive;
            else if (record.State == MessageFinish)
                response.Code = BigWorldMessageIsFinish;
            else if (!Steps.Value.ContainsKey(request.StepId))
                response.Code = BigWorldMessageStepCfgNull;
            else if (request.StepId == message.FirstStepId || record.StepIds.Contains(request.StepId))
                response.Code = record.StepIds.Contains(request.StepId) ? BigWorldMessageStepRepeat : 0;
            else if (!Reachable(record.StepIds.Count > 0 ? record.StepIds[^1] : message.FirstStepId, request.StepId))
                response.Code = BigWorldMessageStepCfgNull;

            if (response.Code == 0)
            {
                // AscNet policy: every reported step is recorded; the client DFS rebuilds the path from FirstStepId
                // through each LatestStepId in order. Terminal step (no NextStep) finishes the message.
                record!.StepIds.Add(request.StepId);
                record.State = NextSteps(request.StepId).Count == 0 ? MessageFinish : MessageNotFinish;
                session.player.Save();
                // BigWorldMessage.RewardId/AwardId are 0 for every row: RewardGoodsList stays empty.
                // Retail (sequence J): ReadShortMessage objectives advance before the record response.
                if (record.State == MessageFinish)
                    BigWorldQuestRuntime.OnMessageFinished(session, request.MessageId);
            }
            session.SendResponse(response, packet.Id);
            BigWorldQuestRuntime.Tick(session);
        }

        private static List<int> NextSteps(int stepId) =>
            Steps.Value.TryGetValue(stepId, out BigWorldMessageStepTable? step) ? step.NextStep.Where(id => id > 0).ToList() : [];

        private static bool Reachable(int from, int target)
        {
            HashSet<int> seen = [from];
            Queue<int> pending = new([from]);
            while (pending.TryDequeue(out int current))
                foreach (int next in NextSteps(current))
                {
                    if (next == target)
                        return true;
                    if (seen.Add(next))
                        pending.Enqueue(next);
                }
            return false;
        }

        private static Dictionary<int, BigWorldMessageData> MessageDict(BigWorldPlayerState state) =>
            state.Messages.ToDictionary(pair => pair.Key, pair => new BigWorldMessageData
            {
                MessageId = pair.Key,
                State = pair.Value.State,
                IsGetReward = pair.Value.IsGetReward,
                CreateTime = pair.Value.CreateTime,
                StepRecordList = pair.Value.StepIds.Select(id => new BigWorldMessageStepRecord { LatestStepId = id }).ToList()
            });

        // Full-dict resync (client replaces everything); used after bulk message state changes.
        internal static void SendMessageRecordUpdate(Session session) =>
            session.SendPush(new NotifyBigWorldMessageRecordUpdate { BigWorldMessageDict = MessageDict(State(session)) });

        #endregion

        #region News

        [RequestPacketHandler("BigWorldNewsMarkPopupRequest")]
        public static void BigWorldNewsMarkPopupRequestHandler(Session session, Packet.Request packet)
        {
            List<int> ids = packet.Deserialize<BigWorldNewsMarkPopupRequest>().NewsIds ?? [];
            BigWorldPlayerState state = State(session);
            BigWorldNewsMarkPopupResponse response = new();
            int firstError = ids.Count == 0 ? BigWorldNewsIdInvalid : 0;
            foreach (int id in ids.Distinct())
            {
                int code = !News.Value.TryGetValue(id, out BigWorldNewsTable? news) ? BigWorldNewsIdNotFound
                    : state.NewsPopupIds.Contains(id) ? BigWorldNewsIdInvalid
                    : !InTime(news.ShowTimeId) ? BigWorldNewsIdInvalid
                    : !BigWorldConditionService.Check(session.player, news.UnlockCondition) ? BigWorldNewsIdNotUnlocked
                    // No PopupCondition gate: the client's CheckAutoPopup marks the CustomParams target news without
                    // CheckPopupCondition (XBigWorldNewsAgency.lua:55-80), and every retail row has PopupCondition 0.
                    : 0;
                if (code == 0)
                    response.MarkedNewsIds.Add(id);
                else if (firstError == 0)
                    firstError = code;
            }
            // AscNet policy: mark the eligible subset; fail only when nothing was marked.
            if (response.MarkedNewsIds.Count == 0)
                response.Code = firstError;
            else
            {
                state.NewsPopupIds.AddRange(response.MarkedNewsIds);
                session.player.Save();
            }
            session.SendResponse(response, packet.Id);
        }

        #endregion

        #region Help course

        [RequestPacketHandler("BigWorldHelpCourseUnlockRequest")]
        public static void BigWorldHelpCourseUnlockRequestHandler(Session session, Packet.Request packet)
        {
            BigWorldHelpCourseUnlockRequest request = packet.Deserialize<BigWorldHelpCourseUnlockRequest>();
            BigWorldPlayerState state = State(session);
            // AscNet policy: the client-side trigger (BigWorldHelpCourseUnlockTrigger / X3C OnShowTeach) is trusted.
            int code = !HelpCourseIds.Value.Contains(request.CourseId) ? BigWorldHelpCourseCfgNotFound
                : state.HelpCourses.Any(course => course.Id == request.CourseId) ? BigWorldHelpCourseDataIsUnlocked
                : 0;
            if (code != 0)
            {
                session.SendResponse(new BigWorldHelpCourseUnlockResponse { Code = code }, packet.Id);
                return;
            }
            BigWorldHelpCourseRecord record = new() { Id = request.CourseId, IsRead = request.IsRead, CreateTime = Now };
            state.HelpCourses.Add(record);
            session.player.Save();
            session.SendResponse(new BigWorldHelpCourseUnlockResponse(), packet.Id);
            session.SendPush(new NotifyBigWorldHelpCourseUnlock { Data = new BigWorldHelpCourseData { Id = record.Id, IsRead = record.IsRead, CreateTime = record.CreateTime } });
        }

        [RequestPacketHandler("BigWorldHelpCourseReadRequest")]
        public static void BigWorldHelpCourseReadRequestHandler(Session session, Packet.Request packet)
        {
            int courseId = packet.Deserialize<BigWorldHelpCourseReadRequest>().CourseId;
            BigWorldHelpCourseRecord? record = State(session).HelpCourses.FirstOrDefault(course => course.Id == courseId);
            int code = record is null ? BigWorldHelpCourseDataNotUnlock : record.IsRead ? BigWorldHelpCourseDataIsRead : 0;
            if (code == 0)
            {
                record!.IsRead = true;
                session.player.Save();
            }
            session.SendResponse(new BigWorldHelpCourseReadResponse { Code = code }, packet.Id);
        }

        #endregion
    }
}
