using AscNet.Common;
using AscNet.Common.Database;
using AscNet.GameServer.Game;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.chat;
using AscNet.Table.V2.share.exhibition;
using AscNet.Table.V2.share.fashion;
using AscNet.Table.V2.share.headportrait;
using AscNet.Table.V2.client.functional;
using AscNet.Table.V2.client.purchase;
using AscNet.Table.V2.client.signin;
using AscNet.Table.V2.share.bigworld.common.course;
using AscNet.Table.V2.share.signin;
using AscNet.Table.V2.share.fuben.bossinshot;
using AscNet.Table.V2.share.fuben.fashionstory;
using AscNet.Table.V2.share.fuben.transfinite;
using AscNet.Table.V2.share.miniactivity.dyemerge;
using AscNet.Table.V2.share.miniactivity.hitmouse;
using AscNet.Table.V2.share.condition;
using AscNet.Table.V2.share.fuben.mainline2;
using AscNet.Table.V2.share.theatre6;
using AscNet.Table.V2.client.activitybrief;
using AscNet.Table.V2.client.uimain;
using AscNet.Table.V2.share.fuben.teaching;
using AscNet.Table.V2.share.newactivitycalendar;
using MessagePack;
using System.Diagnostics;

namespace AscNet.GameServer.Handlers
{
    #region MsgPackScheme
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    [MessagePackObject(true)]
    public class ForceLogoutNotify
    {
        public int Code;
    }
    
    [MessagePackObject(true)]
    public class ShutdownNotify
    {
    }

    [MessagePackObject(true)]
    public class SetServerBeanRequest
    {
        public string ServerBean;
    }

    [MessagePackObject(true)]
    public class SetServerBeanResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class ClientVersionRequest
    {
        public string Version { get; set; } = string.Empty;
    }

    [MessagePackObject(true)]
    public class ClientVersionResponse
    {
        public int Code { get; set; }
        public string Version { get; set; } = AccountModule.CurrentApplicationVersion;
        public bool KickOut { get; set; }
    }

    [MessagePackObject(true)]
    public class NotifyChatBoardLoginData
    {
        public long CurrentChatBoardId { get; set; }
        public List<NotifyChatBoardLoginDataChatBoard> ChatBoards { get; set; } = new();

        [MessagePackObject(true)]
        public class NotifyChatBoardLoginDataChatBoard
        {
            public long Id { get; set; }
            public long GetTime { get; set; }
            public long EndTime { get; set; }
        }
    }

    [MessagePackObject(true)]
    public class NotifyExternalRequiredBigWorldPlayerData
    {
        public List<int> EnteredBigWorldIds = new();
        public int Gender;
        public List<int> CommanderFashionBags = new();
    }

    [MessagePackObject(true)]
    public class UseCdKeyRequest
    {
        public string Id;
    }
    
    [MessagePackObject(true)]
    public class UseCdKeyResponse
    {
        [MessagePackObject(true)]
        public class CdKeyRewardGoods
        {
            public RewardType RewardType;
            public int TemplateId;
        }
        
        public int Code;
        public List<CdKeyRewardGoods>? RewardGoods;
    }
    [MessagePackObject(true)]
    public class SyncReadGameNoticeRequest
    {
        public List<RedPointGameNoticeInfo> GameNoticeInfos { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class SyncReadGameNoticeResponse
    {
        public int Code { get; set; }
    }

    [MessagePackObject(true)]
    public class ChangeAssistCharIdRequest
    {
        public int AssistCharId { get; set; }
    }

    [MessagePackObject(true)]
    public class ChangeAssistCharIdResponse
    {
        public int Code { get; set; }
    }

    [MessagePackObject(true)]
    public class UnlockArchiveComicsRequest
    {
        public List<int> Ids { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class UnlockArchiveComicsResponse
    {
        public int Code { get; set; }
        public List<int> SuccessIds { get; set; } = new();
    }

#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    #endregion

    internal partial class AccountModule
    {
        internal const string CurrentApplicationVersion = "4.6.0";
        internal const string CurrentDocumentVersion = "4.6.7";
        private static readonly long[] DefaultPassedMainStoryStageIds =
        [
            10010101,
            10010102,
            10010103,
            10010104,
            10010201,
            10010202,
            10010203,
            10010204
        ];
        private const long DefaultChatBoardId = 25000001;
        private const int ChangeAssistCharIdRejectedCode = 20002006;


        private static readonly long[] DefaultChatBoardIds =
        [
            25000001,
            25000002,
            25000003,
            25000004,
            25000008,
            25000010
        ];

        private static readonly long[] DefaultCommunicationIds =
        [
            101, 102, 103, 104, 1, 105, 2, 3, 111, 106, 4, 5, 107, 108, 6, 7, 8, 9, 109,
            10, 11, 112, 12, 110, 14, 19, 25, 18, 20, 22, 24, 555, 21, 23, 599, 600,
            3108, 4108, 557, 558, 601, 602, 603, 604, 605, 556, 606, 607, 608, 609
        ];


        private static NotifyExternalRequiredBigWorldPlayerData BuildExternalRequiredBigWorldPlayerData()
        {
            return DlcModule.BuildExternalRequiredBigWorldPlayerData();
        }


        [RequestPacketHandler("HandshakeRequest")]
        public static void HandshakeRequestHandler(Session session, Packet.Request packet)
        {
            _ = MessagePackSerializer.Deserialize<HandshakeRequest>(packet.Content);
            // TODO: make this somehow universal, look into better architecture to handle packets
            // and automatically log their deserialized form

            HandshakeResponse response = new()
            {
                Code = 0,
                UtcOpenTime = 0,
                Sha1Table = null
            };

            session.SendResponse(response, packet.Id);
        }

        [RequestPacketHandler("LoginRequest")]
        public static void LoginRequestHandler(Session session, Packet.Request packet)
        {
            LoginRequest request = MessagePackSerializer.Deserialize<LoginRequest>(packet.Content);
            Player? player = Player.FromToken(request.Token);

            if (player is null)
            {
                session.SendResponse(new LoginResponse
                {
                    Code = 1007 // LoginInvalidLoginToken
                }, packet.Id);
                return;
            }

            lock (Session.GetPlayerOperationLock(player.PlayerData.Id))
            {
                player = Player.FromToken(request.Token);
                if (player is null)
                {
                    session.SendResponse(new LoginResponse
                    {
                        Code = 1007 // LoginInvalidLoginToken
                    }, packet.Id);
                    return;
                }

                Session? previousSession = FindOtherPlayerSession(session, player.PlayerData.Id);
                if (previousSession is not null)
                {
                    // GateServerForceLogoutByAnotherLogin
                    previousSession.SendPush(new ForceLogoutNotify { Code = 1018 });
                    previousSession.DisconnectProtocol();

                    // The previous session persisted its latest state while disconnecting.
                    player = Player.FromToken(request.Token);
                    if (player is null)
                    {
                        session.SendResponse(new LoginResponse
                        {
                            Code = 1007 // LoginInvalidLoginToken
                        }, packet.Id);
                        return;
                    }
                }

                player.SimulatedBattlefield ??= new();
                if (player.SimulatedBattlefield.BossRankPlatform != request.LoginPlatform)
                {
                    player.SimulatedBattlefield.BossRankPlatform = request.LoginPlatform;
                    player.Save();
                }

                session.player = player;
                session.character = Character.FromUid(player.PlayerData.Id);
                session.stage = Stage.FromUid(player.PlayerData.Id);
                session.inventory = Inventory.FromUid(player.PlayerData.Id);

                session.SendResponse(new LoginResponse
                {
                    Code = 0,
                    ReconnectToken = player.Token,
                    UtcOffset = 0,
                    UtcServerTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                }, packet.Id);

                DoLogin(session);
            }
        }

        [RequestPacketHandler("ReconnectRequest")]
        public static void ReconnectRequestHandler(Session session, Packet.Request packet)
        {
            ReconnectRequest request = MessagePackSerializer.Deserialize<ReconnectRequest>(packet.Content);
            Player? candidate = session.player ?? Player.FromToken(request.Token);
            if (candidate?.PlayerData.Id != request.PlayerId)
            {
                session.SendResponse(new ReconnectResponse
                {
                    Code = 1029 // ReconnectInvalidToken
                }, packet.Id);
                return;
            }

            lock (Session.GetPlayerOperationLock(candidate.PlayerData.Id))
            {
                Player? player = session.player;
                if (player is null)
                {
                    player = Player.FromToken(request.Token);
                    if (player?.PlayerData.Id != request.PlayerId)
                    {
                        session.SendResponse(new ReconnectResponse
                        {
                            Code = 1029 // ReconnectInvalidToken
                        }, packet.Id);
                        return;
                    }
                }

                Session? previousSession = FindOtherPlayerSession(session, player.PlayerData.Id);
                if (previousSession is not null)
                {
                    previousSession.SendPush(new ForceLogoutNotify { Code = 1018 });
                    previousSession.DisconnectProtocol();
                    player = Player.FromToken(request.Token);
                    if (player?.PlayerData.Id != request.PlayerId)
                    {
                        session.SendResponse(new ReconnectResponse
                        {
                            Code = 1029 // ReconnectInvalidToken
                        }, packet.Id);
                        return;
                    }
                }

                session.log.Debug("Player is reconnecting...");
                if (session.player is null
                    || session.character is null
                    || session.stage is null
                    || session.inventory is null
                    || previousSession is not null)
                {
                    session.log.Debug("Reassigning player props...");
                    session.character = Character.FromUid(player.PlayerData.Id);
                    session.stage = Stage.FromUid(player.PlayerData.Id);
                    session.inventory = Inventory.FromUid(player.PlayerData.Id);
                }

                session.player = player;
                session.ContinuePushSequenceFrom(request.LastMsgSeqNo);
                session.SendResponse(new ReconnectResponse
                {
                    ReconnectToken = request.Token,
                    RequestNo = request.LastMsgSeqNo
                }, packet.Id);
            }
        }

        private static Session? FindOtherPlayerSession(Session session, long playerId)
        {
            return Server.Instance.Sessions.Values.FirstOrDefault(candidate =>
                !ReferenceEquals(candidate, session)
                && candidate.player is not null
                && candidate.player.PlayerData.Id == playerId);
        }

        [RequestPacketHandler("ClientVersionRequest")]
        public static void ClientVersionRequestHandler(Session session, Packet.Request packet)
        {
            _ = MessagePackSerializer.Deserialize<ClientVersionRequest>(packet.Content);
            session.SendResponse(new ClientVersionResponse(), packet.Id);
        }

        [RequestPacketHandler("SetServerBeanRequest")]
        public static void SetServerBeanRequestHandler(Session session, Packet.Request packet)
        {
            _ = MessagePackSerializer.Deserialize<SetServerBeanRequest>(packet.Content);
            session.SendResponse(new SetServerBeanResponse(), packet.Id);
        }

        [RequestPacketHandler("SyncReadGameNoticeRequest")]
        public static void SyncReadGameNoticeRequestHandler(Session session, Packet.Request packet)
        {
            SyncReadGameNoticeRequest request = packet.Deserialize<SyncReadGameNoticeRequest>();
            SyncGameNoticeInfos(session.player, request.GameNoticeInfos);
            session.SendResponse(new SyncReadGameNoticeResponse(), packet.Id);
        }

        [RequestPacketHandler("ChangeAssistCharIdRequest")]
        public static void ChangeAssistCharIdRequestHandler(Session session, Packet.Request packet)
        {
            ChangeAssistCharIdRequest request = packet.Deserialize<ChangeAssistCharIdRequest>();
            int currentAssistCharacterId = ResolveAssistCharacterId(session);

            ChangeAssistCharIdResponse response = new();
            if (request.AssistCharId == currentAssistCharacterId
                || !session.character.Characters.Any(character => (int)character.Id == request.AssistCharId))
            {
                response.Code = ChangeAssistCharIdRejectedCode;
                session.SendResponse(response, packet.Id);
                return;
            }

            session.player.AssistCharacterId = request.AssistCharId;
            session.player.Save();
            session.SendResponse(response, packet.Id);
        }

        [RequestPacketHandler("UnlockArchiveComicsRequest")]
        public static void UnlockArchiveComicsRequestHandler(Session session, Packet.Request packet)
        {
            UnlockArchiveComicsRequest request = packet.Deserialize<UnlockArchiveComicsRequest>();
            List<int> successIds = UnlockArchiveComics(session.player, request.Ids);
            session.SendResponse(new UnlockArchiveComicsResponse
            {
                Code = 0,
                SuccessIds = successIds
            }, packet.Id);
        }

        private static List<int> UnlockArchiveComics(Player player, List<int>? requestedIds)
        {
            List<int> successIds = new();
            if (requestedIds is null || requestedIds.Count == 0)
                return successIds;

            player.UnlockComics ??= new();
            foreach (int requestedId in requestedIds)
            {
                if (requestedId <= 0 || player.UnlockComics.Contains(requestedId) || successIds.Contains(requestedId))
                    continue;

                player.UnlockComics.Add(requestedId);
                successIds.Add(requestedId);
            }

            if (successIds.Count == 0)
                return successIds;

            player.UnlockComics.Sort();
            player.Save();
            return successIds;
        }

        private static void SyncGameNoticeInfos(Player player, List<RedPointGameNoticeInfo>? gameNoticeInfos)
        {
            if (gameNoticeInfos is null || gameNoticeInfos.Count == 0)
                return;

            player.RedPointRecords ??= new();
            Dictionary<string, RedPointGameNoticeInfo> savedById = player.RedPointRecords.GameNoticeInfos
                .Where(IsValidGameNoticeInfo)
                .GroupBy(info => info.NoticeId)
                .ToDictionary(group => group.Key, group => group.OrderByDescending(info => info.ModifyTime).First());

            bool changed = false;
            foreach (RedPointGameNoticeInfo noticeInfo in gameNoticeInfos.Where(IsValidGameNoticeInfo))
            {
                if (savedById.TryGetValue(noticeInfo.NoticeId, out RedPointGameNoticeInfo? saved)
                    && saved.ModifyTime == noticeInfo.ModifyTime
                    && saved.EndTime == noticeInfo.EndTime)
                {
                    continue;
                }

                savedById[noticeInfo.NoticeId] = noticeInfo;
                changed = true;
            }

            if (!changed && savedById.Count == player.RedPointRecords.GameNoticeInfos.Count)
                return;

            player.RedPointRecords.GameNoticeInfos = savedById.Values
                .OrderBy(info => info.NoticeId, StringComparer.Ordinal)
                .ToList();
            player.Save();
        }

        private static bool IsValidGameNoticeInfo(RedPointGameNoticeInfo noticeInfo)
        {
            return !string.IsNullOrWhiteSpace(noticeInfo.NoticeId);
        }

        [RequestPacketHandler("ReconnectAck")]
        public static void ReconnectAckHandler(Session session, Packet.Request packet)
        {
            // Retail clients send ReconnectAck as a client push; the push path logs it.
        }

        // TODO: Promo code
        [RequestPacketHandler("UseCdKeyRequest")]
        public static void UseCdKeyRequestHandler(Session session, Packet.Request packet)
        {
            session.SendResponse(new UseCdKeyResponse() { Code = 20054001 }, packet.Id);
        }
        
        internal static void SendLoginState(Session session)
        {
            session.SendPush(BuildNotifyLogin(session));
        }

        private static List<TimeLimitCtrlConfigList> BuildTimeLimitControlConfigList() =>
            BuildTimeLimitControlConfigList(DateTimeOffset.UtcNow, BuildWheelchairManualActivityPayload().ActivityId > 0);

        internal static List<TimeLimitCtrlConfigList> BuildTimeLimitControlConfigList(
            DateTimeOffset now,
            bool wheelchairActivityActive)
        {
            Dictionary<long, TimeLimitCtrlConfigList> controls = ActivityScheduleService.All.ToDictionary(
                row => row.Id,
                row => new TimeLimitCtrlConfigList
                {
                    Id = row.Id,
                    StartTime = row.StartTime,
                    EndTime = row.EndTime
                });

            void AddDerived(long id, long startTime, long endTime)
            {
                if (id > 0)
                    controls.TryAdd(id, new() { Id = id, StartTime = startTime, EndTime = endTime });
            }

            Dictionary<int, ActivityBriefGroupTable> groups = TableReaderV2.Parse<ActivityBriefGroupTable>()
                .ToDictionary(group => group.Id);
            foreach (ActivityBriefTable brief in TableReaderV2.Parse<ActivityBriefTable>().Where(brief => brief.TimeId > 0))
            {
                ActivityScheduleEntry[] openChildSchedules = brief.GroupIdList
                    .Where(groupId => groupId > 0 && groups.TryGetValue(groupId, out _))
                    .Select(groupId => groups[groupId].TimeId)
                    .Where(timeId => timeId is > 0 && ActivityScheduleService.TryGet(timeId.Value, out _))
                    .Select(timeId => timeId!.Value)
                    .Select(timeId =>
                    {
                        ActivityScheduleService.TryGet(timeId, out ActivityScheduleEntry schedule);
                        return schedule;
                    })
                    .Where(schedule => schedule.IsOpen(now))
                    .OrderBy(schedule => schedule.Id)
                    .ToArray();
                if (openChildSchedules.Length != 0)
                {
                    AddDerived(
                        brief.TimeId,
                        openChildSchedules.Min(schedule => schedule.StartTime),
                        openChildSchedules.Any(schedule => schedule.EndTime == 0)
                            ? 0
                            : openChildSchedules.Max(schedule => schedule.EndTime));
                }
            }

            Dictionary<int, TeachingActivityTable> teachingActivities = TableReaderV2.Parse<TeachingActivityTable>()
                .ToDictionary(activity => activity.Id);
            Dictionary<int, SkipFunctionalTable> skipFunctions = TableReaderV2.Parse<SkipFunctionalTable>()
                .GroupBy(skip => skip.SkipId)
                .ToDictionary(group => group.Key, group => group.First());
            foreach (NewActivityCalendarActivityTable calendar in TableReaderV2.Parse<NewActivityCalendarActivityTable>()
                .Where(calendar => ActivityScheduleService.TryGet(calendar.MainTimeId, out ActivityScheduleEntry schedule)
                    && schedule.IsOpen(now))
                .OrderBy(calendar => calendar.ActivityId))
            {
                ActivityScheduleService.TryGet(calendar.MainTimeId, out ActivityScheduleEntry outerSchedule);
                if (!skipFunctions.TryGetValue(calendar.SkipId, out SkipFunctionalTable? skip))
                    continue;

                foreach (int teachingActivityId in skip.CustomParams
                    .Append(skip.ParamId ?? 0)
                    .Where(id => id > 0)
                    .Distinct()
                    .OrderBy(id => id))
                {
                    if (teachingActivities.TryGetValue(teachingActivityId, out TeachingActivityTable? teaching)
                        && teaching.TimeId is > 0)
                    {
                        AddDerived(teaching.TimeId.Value, outerSchedule.StartTime, outerSchedule.EndTime);
                    }
                }
            }

            if (wheelchairActivityActive)
            {
                foreach (ActivityBtnTable button in TableReaderV2.Parse<ActivityBtnTable>()
                    .Where(button => button.TimeId is > 0
                        && button.ManagerName?.Contains("Wheelchair", StringComparison.OrdinalIgnoreCase) == true)
                    .OrderBy(button => button.Id))
                {
                    if (button.TimeId is not int timeId)
                        continue;
                    if (skipFunctions.TryGetValue(button.SkipId, out SkipFunctionalTable? skip)
                        && skip.UiName?.Contains("Wheelchair", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        AddDerived(timeId, 0, 0);
                    }
                }
            }

            return controls.Values.OrderBy(control => control.Id).ToList();
        }

        private static List<FunctionOpenTimeConfig> BuildFunctionOpenTimeConfigList(
            IEnumerable<TimeLimitCtrlConfigList> controls)
        {
            HashSet<long> emittedTimeIds = controls.Select(control => control.Id).ToHashSet();
            int[] bigWorldFunctionIds = TableReaderV2.Parse<SkipFunctionalTable>()
                .Where(row => row.UiName == "SkipToBigWorld" && row.FunctionalId is > 0)
                .Select(row => row.FunctionalId!.Value)
                .Distinct()
                .OrderBy(id => id)
                .ToArray();
            int[] bigWorldTimeIds = TableReaderV2.Parse<BigWorldCourseVersionTable>()
                .Where(row => row.TimeId is > 0 && emittedTimeIds.Contains(row.TimeId))
                .Select(row => row.TimeId)
                .Distinct()
                .OrderBy(id => id)
                .ToArray();

            return bigWorldFunctionIds
                .SelectMany(functionId => bigWorldTimeIds.Select(timeId => new FunctionOpenTimeConfig
                {
                    FunctionId = functionId,
                    TimeId = timeId
                }))
                .ToList();
        }

        private static List<dynamic> BuildPurchaseClientInfoLoginData(Player player)
        {
            List<PurchasePackageYKUiConfigTable> packages =
                TableReaderV2.Parse<PurchasePackageYKUiConfigTable>();
            HashSet<int> packageIds = packages.Select(package => package.Id).ToHashSet();
            int monthlyUiType = TableReaderV2.Parse<SignCardTable>()
                .Where(card => card.Param.Count >= 2
                    && card.Param[0] > 0
                    && packageIds.Contains(card.Param[1]))
                .Select(card => card.Param[0])
                .Distinct()
                .Single();
            return packages
                .OrderBy(package => package.Id)
                .Select(package => (dynamic)new GetPurchaseListResponse.GetPurchaseListResponsePurchaseInfo
                {
                    Id = (uint)package.Id,
                    UiType = monthlyUiType,
                    BuyTimes = player.PurchaseBuyTimes.GetValueOrDefault((uint)package.Id),
                    DailyRewardRemainDay = 0,
                    IsDailyRewardGet = false
                })
                .ToList();
        }

        private static NotifyLogin BuildNotifyLogin(Session session)
        {
            BossModule.PrepareLogin(session);
            BossInshotModule.PrepareLogin(session.player, DateTimeOffset.UtcNow);
            FashionStoryModule.PrepareLogin(session.player, DateTimeOffset.UtcNow);
            TransfiniteModule.PrepareLogin(session.player, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            List<TimeLimitCtrlConfigList> timeLimitControls = BuildTimeLimitControlConfigList();
            NotifyLogin notifyLogin = new()
            {
                PlayerData = session.player.PlayerData,
                TimeLimitCtrlConfigList = timeLimitControls,
                ItemList = Inventory.FilterClientItems(session.inventory.Items),
                CharacterList = session.character.Characters.Select(ToLoginCharacter).ToList(),
                EquipList = session.character.Equips,
                FashionList = session.character.Fashions,
                WeaponFashionList = session.character.WeaponFashions
                    .Select(fashion => new WeaponFashionData
                    {
                        Id = fashion.Id,
                        ExpireTime = fashion.ExpireTime,
                        UseCharacterList = fashion.UseCharacterList.ToList()
                    })
                    .ToList(),
                PartnerList = session.character.Partners,
                FashionSuitList = [],
                FashionColors = BuildOwnedFashionColors(session.character),
                HeadPortraitList = session.player.HeadPortraits,
                TeamGroupData = session.player.TeamGroups,
                TeamPrefabData = (session.player.TeamPrefabs ?? [])
                    .OfType<TeamPrefabData>()
                    .Select((teamPrefab, index) => new { Key = index + 1, Value = teamPrefab })
                    .ToDictionary(entry => entry.Key, entry => entry.Value),
                FubenData = new()
                {
                    StageData = BuildLoginStageData(session),
                    FubenBaseData = new()
                },
                IsSetFightCgEnable = true,
                FubenMainLineData = session.player.FubenMainLineData,
                FubenEventData = new(),
                FubenMainLine2Data = MainLine2Module.BuildLoginData(session),
                FubenMainLineLuosaitaData = MainLineLuosaitaPayloadFactory.BuildLoginData(session.stage),
                FubenChapterExtraLoginData = new(),
                FubenUrgentEventData = new(),
                FubenShortStoryLoginData = new(),
                SignInfos = SignInModule.BuildLoginSignInfos(session.player),
                UseBackgroundId = session.player.UseBackgroundId,
                HaveBackgroundIds = BuildHaveBackgroundIds(session.player),
                RandomBackgroundLoginData = new(),
                PurchaseClientInfoLoginData = BuildPurchaseClientInfoLoginData(session.player),
                FunctionOpenTimeConfigList = BuildFunctionOpenTimeConfigList(timeLimitControls),
                DlcPlayerData = new(),
                DlcCharacterList = session.character.Characters.Select(ToDlcCharacter).ToList(),
                RedPointRecords = session.player.RedPointRecords ?? new()
            };
            if (notifyLogin.PlayerData.DisplayCharIdList.Count < 1)
                notifyLogin.PlayerData.DisplayCharIdList.Add(notifyLogin.PlayerData.DisplayCharId);


            notifyLogin.PlayerData.ShieldFuncList ??= new List<dynamic>();
            notifyLogin.PlayerData.ShieldFuncList.RemoveAll(IsHomeChatShieldFunction);
            if (notifyLogin.PlayerData.ShieldFuncList.Count == 0)
                notifyLogin.PlayerData.ShieldFuncList.Add(8001);

            notifyLogin.PlayerData.Communications ??= new List<long>();
            HashSet<long> communicationIds = notifyLogin.PlayerData.Communications.ToHashSet();
            foreach (long communicationId in DefaultCommunicationIds)
            {
                if (communicationIds.Add(communicationId))
                    notifyLogin.PlayerData.Communications.Add(communicationId);
            }


            return notifyLogin;
        }

        private static Dictionary<int, List<int>> BuildOwnedFashionColors(Character character)
        {
            character.FashionColors ??= [];
            return TableReaderV2.Parse<FashionColorTable>()
                .Where(color =>
                    color.Id > 0
                    && character.FashionColors.TryGetValue(
                        color.OriginalFashionId,
                        out List<int>? ownedColors)
                    && ownedColors.Contains(color.Id))
                .GroupBy(color => color.OriginalFashionId)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(color => color.Id).Distinct().Order().ToList());
        }

        private static bool IsHomeChatShieldFunction(dynamic value)
        {
            try
            {
                long functionId = Convert.ToInt64(value);
                return functionId is 802 or 803;
            }
            catch
            {
                return false;
            }
        }

        private static List<int> BuildHaveBackgroundIds(Player player)
        {
            HashSet<int> backgroundIds = new()
            {
                14000001,
                14000004,
                14000008
            };

            if (player.UseBackgroundId > 0)
                backgroundIds.Add(player.UseBackgroundId);

            return backgroundIds.Order().ToList();
        }

        private static Dictionary<long, StageDatum> BuildLoginStageData(Session session)
        {
            Dictionary<long, StageDatum> stageData = session.stage?.Stages is null
                ? new Dictionary<long, StageDatum>()
                : new Dictionary<long, StageDatum>(session.stage.Stages);
            bool changed = false;

            foreach (long stageId in GetCurrentMainLine2PrerequisiteStageIds())
                EnsureLoginPassedStage(session, stageData, stageId, ref changed);
            foreach (long stageId in DefaultPassedMainStoryStageIds)
                EnsureLoginPassedStage(session, stageData, stageId, ref changed);


            if (changed)
                session.stage?.Save();

            return stageData;
        }
        private static IEnumerable<long> GetCurrentMainLine2PrerequisiteStageIds()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            Dictionary<int, ConditionTable> conditions = TableReaderV2.Parse<ConditionTable>()
                .ToDictionary(condition => condition.Id);
            return TableReaderV2.Parse<MainLine2ChapterTable>()
                .Where(chapter => chapter.ActivityTimeId.GetValueOrDefault() > 0
                    && ActivityScheduleService.IsOpen(chapter.ActivityTimeId.Value, now))
                .Select(chapter => conditions.GetValueOrDefault(chapter.OpenCondition))
                .Where(condition => condition is not null
                    && condition.Type == 10105
                    && condition.Params.Count > 0
                    && condition.Params[0] > 0)
                .Select(condition => (long)condition!.Params[0])
                .Distinct()
                .Order();
        }


        private static void EnsureLoginPassedStage(Session session, Dictionary<long, StageDatum> stageData, long stageId, ref bool changed)
        {
            if (stageData.ContainsKey(stageId))
                return;

            StageDatum passedStage = BuildPassedStage(stageId);
            stageData[stageId] = passedStage;
            session.stage?.AddStage(passedStage);
            changed = true;
        }

        private static StageDatum BuildPassedStage(long stageId)
        {
            long now = DateTimeOffset.Now.ToUnixTimeSeconds();
            return new()
            {
                StageId = stageId,
                StarsMark = 7,
                Passed = true,
                PassTimesToday = 0,
                PassTimesTotal = 1,
                BuyCount = 0,
                Score = 0,
                LastPassTime = now,
                RefreshTime = now,
                CreateTime = now,
                BestRecordTime = 0,
                LastRecordTime = 0,
                BestCardIds = [1021001],
                LastCardIds = [1021001]
            };
        }

        private static DlcCharacter ToDlcCharacter(CharacterData character)
        {
            return new()
            {
                Id = character.Id,
                FashionId = character.FashionId,
                FashionColorId = 0,
                ChipFormId = 0,
                CreateTime = character.CreateTime,
                StyleType = 0
            };
        }

        private static NotifyMedalData BuildMedalLoginData(Player player)
        {
            return new NotifyMedalData
            {
                MedalInfos = (player.UnlockedMedals ?? [])
                    .Select(medal => new NotifyMedalData.NotifyMedalDataMedalInfo
                    {
                        Id = medal.Id,
                        Time = medal.Time,
                        KeepTime = medal.KeepTime
                    })
                    .ToList()
            };
        }

        private static NotifyChatBoardLoginData BuildChatBoardLoginData(Player player)
        {
            long now = DateTimeOffset.Now.ToUnixTimeSeconds();
            long defaultGetTime = player.PlayerData.CreateTime > 0 ? player.PlayerData.CreateTime : now;
            Dictionary<long, NotifyChatBoardLoginData.NotifyChatBoardLoginDataChatBoard> chatBoards =
                DefaultChatBoardIds.ToDictionary(
                    id => id,
                    id => new NotifyChatBoardLoginData.NotifyChatBoardLoginDataChatBoard
                    {
                        Id = id,
                        GetTime = defaultGetTime,
                        EndTime = 0
                    });
            foreach (ChatBoardUnlockState unlock in player.UnlockedChatBoards ?? [])
            {
                if (unlock.EndTime == 0 || unlock.EndTime > now)
                {
                    chatBoards[unlock.Id] = new NotifyChatBoardLoginData.NotifyChatBoardLoginDataChatBoard
                    {
                        Id = unlock.Id,
                        GetTime = unlock.GetTime,
                        EndTime = unlock.EndTime
                    };
                }
            }

            long currentChatBoardId = player.PlayerData.CurrentChatBoardId;
            if (currentChatBoardId > 0
                && !chatBoards.ContainsKey(currentChatBoardId)
                && !(player.UnlockedChatBoards ?? []).Any(unlock => unlock.Id == currentChatBoardId))
            {
                chatBoards[currentChatBoardId] = new NotifyChatBoardLoginData.NotifyChatBoardLoginDataChatBoard
                {
                    Id = currentChatBoardId,
                    GetTime = defaultGetTime,
                    EndTime = 0
                };
            }
            if (!chatBoards.ContainsKey(currentChatBoardId))
            {
                currentChatBoardId = DefaultChatBoardId;
                player.PlayerData.CurrentChatBoardId = currentChatBoardId;
            }

            return new()
            {
                CurrentChatBoardId = currentChatBoardId,
                ChatBoards = chatBoards.Values.OrderBy(chatBoard => chatBoard.Id).ToList()
            };
        }

        private static NotifyPayInfo BuildNotifyPayInfo()
        {
            return new()
            {
                TotalPayMoney = 0,
                FirstRewardReceivedList = []
            };
        }

        private static NotifyFunctionalEntranceData BuildFunctionalEntranceData()
        {
            return new()
            {
                RedPointDatas = new()
                {
                    { 20000, 45 }
                }
            };
        }

        private static PurchaseDailyNotify BuildPurchaseDailyNotify()
        {
            PurchaseDailyNotify notify = new();
            notify.FreeRewardInfoList.Add(new()
            {
                Id = 90943,
                Name = "Serum Daily Supply",
                UiType = 6
            });
            notify.FreeRewardInfoList.Add(new()
            {
                Id = 90944,
                Name = "Weekly Limited Pack",
                UiType = 6
            });

            return notify;
        }

        private static NotifyPurchaseRecommendConfig BuildPurchaseRecommendConfig()
        {
            return new()
            {
                Data = new()
                {
                    AddOrModifyConfigs = new(),
                    RemoveIds = []
                }
            };
        }

        private static NotifyRegression2Data BuildRegressionLoginData()
        {
            return new()
            {
                Data = new()
                {
                    ActivityData = new()
                    {
                        Id = 1,
                        State = 1
                    },
                    SignInData = null!
                }
            };
        }

        private static NotifyTask BuildEmptyNotifyTask()
        {
            return new()
            {
                Tasks = new()
            };
        }

        private static NotifyBfrtData BuildBfrtLoginData()
        {
            return new()
            {
                BfrtData = new()
            };
        }

        private static NotifyTRPGData BuildTrpgLoginData()
        {
            return new()
            {
                CurTargetLink = 10001,
                BaseInfo = new()
                {
                    Level = 1
                },
                BossInfo = new()
            };
        }


        private static uint NextDailyRefreshTime()
        {
            return (uint)DateTimeOffset.Now.ToUnixTimeSeconds() + 3600 * 24;
        }

        private static readonly IReadOnlyDictionary<string, byte[]> SupportedStartupPushPayloads = new Dictionary<string, byte[]>
        {
            ["NotifyLoginAwarenessInfo"] = SerializeStartupPayload(new Dictionary<string, object?>
            {
                ["AwarenessInfo"] = new Dictionary<string, object?>
                {
                    ["ChapterRecords"] = Array.Empty<object>(),
                    ["ChallengeRecords"] = Array.Empty<object>(),
                    ["TeamRecords"] = Array.Empty<object>()
                }
            }),
            ["NotifyDlcFightCharacterId"] = SerializeStartupPayload(new Dictionary<string, object?>
            {
                ["FightCharacterId"] = 0
            }),
            ["NotifyDlcChipFormDataList"] = SerializeStartupPayload(new Dictionary<string, object?>
            {
                ["ChipFormDataList"] = Array.Empty<object>()
            }),
            ["NotifyDlcChipAssistChipId"] = SerializeStartupPayload(new Dictionary<string, object?>
            {
                ["AssistChipId"] = 0
            }),
            ["NotifyNameplateLoginData"] = SerializeStartupPayload(new Dictionary<string, object?>
            {
                ["CurrentWearNameplate"] = 0,
                ["UnlockNameplates"] = Array.Empty<object>()
            }),
            ["NotifyGuildDormPlayerData"] = SerializeStartupPayload(new Dictionary<string, object?>
            {
                ["GuildDormData"] = new Dictionary<string, object?>
                {
                    ["CurrentCharacterId"] = 1021001,
                    ["DailyInteractRewardTotalTimes"] = 0,
                    ["DailyInteractRewardCurTimes"] = 0,
                    ["OneTimeInteractReplyIds"] = Array.Empty<object>(),
                    ["InteractedFurnitureIds"] = Array.Empty<object>(),
                    ["RandomBoxes"] = Array.Empty<object>()
                }
            }),
            ["NotifyHoldRegressionIgnoreChannel"] = SerializeStartupPayload(new Dictionary<string, object?>
            {
                ["IgnoreChannelIds"] = Array.Empty<object>()
            }),
            ["NotifyClientVersion"] = SerializeStartupPayload(new Dictionary<string, object?>
            {
                ["Version"] = CurrentDocumentVersion,
                ["KickOut"] = false
            }),
            ["NotifyNewActivityCalendarData"] = SerializeStartupPayload(BuildNewActivityCalendarPayload()),
            ["NotifyAccumulateExpendData"] = SerializeStartupPayload(BuildAccumulateExpendPayload()),
            ["NotifyExperimentData"] = SerializeStartupPayload(new Dictionary<string, object?>
            {
                ["FinishIds"] = Array.Empty<object>(),
                ["ExperimentInfos"] = Array.Empty<object>()
            }),
            ["NotifySameColorGameData"] = SerializeStartupPayload(new Dictionary<string, object?>
            {
                ["ActivityId"] = 0,
                ["BossRecords"] = Array.Empty<object>()
            }),
            ["NotifyReviewConfig"] = SerializeStartupPayload(new Dictionary<string, object?>
            {
                ["ReviewActivityConfigList"] = Array.Empty<object>()
            }),
            ["NotifyMentorData"] = SerializeStartupPayload(new Dictionary<string, object?>
            {
                ["PlayerType"] = 0,
                ["Teacher"] = new Dictionary<string, object?>(),
                ["Students"] = Array.Empty<object>(),
                ["ApplyList"] = Array.Empty<object>(),
                ["GraduateStudentCount"] = 0,
                ["StageReward"] = Array.Empty<object>(),
                ["WeeklyTaskReward"] = Array.Empty<object>(),
                ["WeeklyTaskCompleteCount"] = 0,
                ["Tag"] = Array.Empty<object>(),
                ["OnlineTag"] = Array.Empty<object>(),
                ["Announcement"] = string.Empty,
                ["DailyChangeTaskCount"] = 0,
                ["WeeklyLevel"] = 0,
                ["MonthlyStudentCount"] = 0,
                ["Message"] = null
            }),
            ["NotifyGuildData"] = SerializeStartupPayload(new Dictionary<string, object?>
            {
                ["GuildId"] = 0,
                ["GuildName"] = string.Empty,
                ["GuildLevel"] = 0,
                ["IconId"] = 0,
                ["GuildRankLevel"] = 0,
                ["HasContributeReward"] = 0,
                ["HasRecruit"] = false,
                ["BossEndTime"] = 0,
                ["FreeChangeGuildNameCount"] = 0,
                ["ShopCoin"] = 0,
                ["HeadPortraits"] = Array.Empty<object>(),
                ["DormThemes"] = Array.Empty<object>(),
                ["DormBgms"] = Array.Empty<object>()
            }),
            ["NotifyMentorChat"] = SerializeStartupPayload(new Dictionary<string, object?>
            {
                ["ChatMessages"] = Array.Empty<object>()
            }),
            ["NotifyFestivalData"] = SerializeStartupPayload(BuildFestivalPayload()),
            ["NotifyGame2048DataDb"] = SerializeStartupPayload(BuildGame2048Payload()),
            ["NotifyGameCollectionData"] = SerializeStartupPayload(BuildGameCollectionPayload()),
            ["NotifyGoldenMinerGameInfo"] = SerializeStartupPayload(BuildGoldenMinerPayload()),
            ["NotifyTaikoMasterData"] = SerializeStartupPayload(BuildTaikoMasterPayload()),
            ["NotifyTurntableData"] = SerializeStartupPayload(BuildTurntablePayload())
        };

        private static byte[] SerializeStartupPayload(Dictionary<string, object?> payload)
        {
            return MessagePackPayloads.Serialize(payload);
        }

        private static void SendEmptyStartupPush(Session session, string name)
        {
            if (name == "NotifyNewActivityCalendarData")
            {
                session.SendPush(name, SerializeStartupPayload(BuildNewActivityCalendarPayload()));
                return;
            }
            if (name == nameof(NotifyWheelchairManualActivity))
            {
                session.SendPush(BuildWheelchairManualActivityPayload());
                return;
            }
            if (name == nameof(NotifyWheelchairManualActivityUpdate))
            {
                session.SendPush(BuildWheelchairManualActivityUpdatePayload());
                return;
            }
            if (name == "NotifySelfChoiceLottoData")
            {
                session.SendPush(name, SerializeStartupPayload(BuildSelfChoiceLottoPayload(session.player)));
                return;
            }
            if (SupportedStartupPushPayloads.TryGetValue(name, out byte[]? supportedPayload))
                session.SendPush(name, supportedPayload);
        }




        private static int ResolveAssistCharacterId(Session session)
        {
            int savedAssistCharacterId = session.player.AssistCharacterId;
            if (savedAssistCharacterId != 0
                && session.character.Characters.Any(character => (int)character.Id == savedAssistCharacterId))
            {
                return savedAssistCharacterId;
            }

            int fallbackAssistCharacterId = (int)(session.character.Characters.FirstOrDefault()?.Id ?? 0);
            if (session.player.AssistCharacterId != fallbackAssistCharacterId)
                session.player.AssistCharacterId = fallbackAssistCharacterId;

            return fallbackAssistCharacterId;
        }

        private static NotifyArchiveLoginData BuildNotifyArchiveLoginData(Player player)
        {
            List<int> unlockComics = player.UnlockComics is { Count: > 0 }
                ? player.UnlockComics.Distinct().Order().ToList()
                : ArchiveDefaults.CreateDefaultUnlockedArchiveComics();

            return new NotifyArchiveLoginData
            {
                UnlockComics = unlockComics
            };
        }

        // TODO: Move somewhere else, also split.
        static void DoLogin(Session session)
        {
            DoLogin(session, updateLoginAccounting: true);
        }
        internal static void ReconcileGatherRewardBaselines(Session session)
        {
            HashSet<int> ownedCharacterIds = session.character.Characters.Select(character => (int)character.Id).ToHashSet();
            foreach (ExhibitionRewardTable reward in TableReaderV2.Parse<ExhibitionRewardTable>().Where(reward =>
                         reward.LevelId == 1
                         && reward.ConditionIds.Count == 0
                         && ownedCharacterIds.Contains(reward.CharacterId)))
            {
                session.player.AddGatherReward(reward.Id);
            }
        }


        static void DoLogin(Session session, bool updateLoginAccounting)
        {
            long currentTime = DateTimeOffset.Now.ToUnixTimeSeconds();
            if (updateLoginAccounting)
            {
                bool isNewDay = currentTime / 86_400 > session.player.PlayerData.LastLoginTime / 86_400;
                if (session.player.PlayerData.NewPlayerTaskActiveDay <= 0)
                {
                    session.player.PlayerData.NewPlayerTaskActiveDay = 1;
                }
                else if (isNewDay)
                {
                    session.player.PlayerData.NewPlayerTaskActiveDay += 1;
                }

                session.player.PlayerData.LastLoginTime = currentTime;
            }
            ReconcileGatherRewardBaselines(session);
            RepairProfileCosmeticRewards(session);
            session.player.NormalizeTeamPrefabs();
            session.ClampPlayerLevelToConfiguredMaximum();
            Theatre6Module.ReconcileAvailability(session.player, DateTimeOffset.UtcNow);

            (ActivityResultNotify? arenaResult, NotifyArenaActivity arenaActivity) = ArenaModule.ReconcileLogin(session);

            if (Common.Common.config.SkipCommonGuides)
                GuideModule.SkipCommonGuides(session.player);

            NotifyLogin notifyLogin = BuildNotifyLogin(session);

            NotifyAssistData notifyAssistData = new()
            {
                AssistData = new()
                {
                    AssistCharacterId = (uint)ResolveAssistCharacterId(session)
                }
            };

            NotifyChatLoginData notifyChatLoginData = new()
            {
                RefreshTime = ((DateTimeOffset)Process.GetCurrentProcess().StartTime).ToUnixTimeSeconds(),
                UnlockEmojis = TableReaderV2.Parse<EmojiTable>().Select(x => new NotifyChatLoginData.NotifyChatLoginDataUnlockEmoji() { Id = (uint)x.Id }).ToList()
            };

            NotifyTaskData notifyTaskData = new()
            {
                TaskData = new()
                {
                    NewbieHonorReward = session.player.MissionProgress.NewbieHonorReward,
                    NewbieUnlockPeriod = 7,
                    Course = session.stage.Course,
                    FinishedTasks = session.stage.FinishedTasks,
                    NewPlayerRewardRecord = session.player.MissionProgress.NewPlayerRewardRecords,
                    NewbieRecvProgress = session.player.MissionProgress.NewbieRewardRecords,
                    Tasks = TaskModule.BuildTaskData(session),
                }
            };
            NotifyGatherRewardList notifyGatherRewardList = new()
            {
                GatherRewards = session.player.GatherRewards
            };

            NotifyBirthdayPlot notifyBirthdayPlot = new()
            {
                IsChange = session.player.PlayerData.Birthday is null ? 0 : 1
            };

            NotifyNewPlayerTaskStatus notifyNewPlayerTaskStatus = new()
            {
                NewPlayerTaskActiveDay = session.player.PlayerData.NewPlayerTaskActiveDay
            };
            NotifyPayInfo notifyPayInfo = BuildNotifyPayInfo();
            long mailNow = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            bool mailStateChanged = MailModule.EnsureSystemMails(session.player, mailNow)
                | MailModule.ReconcileExpiry(session.player, mailNow);
            NotifyMails notifyMails = MailModule.BuildNotifyMails(session.player, mailNow);
            if (mailStateChanged)
                session.player.SaveChecked();
            NotifyFunctionalEntranceData notifyFunctionalEntranceData = BuildFunctionalEntranceData();
            PurchaseDailyNotify purchaseDailyNotify = BuildPurchaseDailyNotify();
            NotifyPurchaseRecommendConfig purchaseRecommendConfig = BuildPurchaseRecommendConfig();

            session.SendPush(notifyLogin);
            session.SendPush(notifyPayInfo);
            session.SendPush(notifyMails);
            session.SendPush(new NotifyEquipChipGroupList());
            session.SendPush(new NotifyEquipChipAutoRecycleSite()
            {
                ChipRecycleSite = new()
                {
                    RecycleStar = [1, 2, 3, 4]
                }
            });
            session.SendPush(new NotifyEquipGuideData()
            {
                EquipGuideData = new()
            });
            session.SendPush(BuildNotifyArchiveLoginData(session.player));
            SendEmptyStartupPush(session, "NotifyLoginAwarenessInfo");
            session.SendPush(notifyChatLoginData);
            session.SendPush(new NotifySocialData());
            session.SendPush(notifyTaskData);
            session.SendPush(TaskModule.BuildActivenessStatus(session));
            session.SendPush(notifyNewPlayerTaskStatus);
            TaskModule.SendTaskSync(session);
            session.SendPush(BuildRegressionLoginData());
            session.SendPush(new NotifyMaintainerActionData());
            session.SendPush(new NotifyAllRedEnvelope());
            session.SendPush(new NotifyScoreTitleData());
            session.SendPush(BuildBfrtLoginData());
            session.SendPush(new NotifyBiancaTheatreActivityData());
            SendEmptyStartupPush(session, "NotifyTheatre3ActivityData");
            SendEmptyStartupPush(session, "NotifyFangKuaiData");
            session.SendPush(new NotifyWorkNextRefreshTime()
            {
                NextRefreshTime = NextDailyRefreshTime()
            });
            session.SendPush(DormModule.BuildLoginData(session));
            session.SendPush(BuildTrpgLoginData());
            session.SendPush(BuildMedalLoginData(session.player));
            session.SendPush(new NotifyExploreData());
            session.SendPush(notifyGatherRewardList);
            session.SendPush(new NotifyGuildEvent());
            SendEmptyStartupPush(session, "NotifyNewActivityCalendarData");
            session.SendPush(notifyAssistData);
            SendEmptyStartupPush(session, "NotifyDlcFightCharacterId");
            SendEmptyStartupPush(session, "NotifyDlcChipFormDataList");
            SendEmptyStartupPush(session, "NotifyDlcChipAssistChipId");
            SendEmptyStartupPush(session, "NotifyTheatre5ActivityData");
            SendCurrentEventTaskBatch(session, CurrentEventTaskBatchTheatre5);
            NotifyTheatre6ActivityData? theatre6Data = Theatre6Module.BuildNotify(session.player);
            if (theatre6Data is not null)
                session.SendPush(theatre6Data);
            SendEmptyStartupPush(session, "NotifyNameplateLoginData");
            SendEmptyStartupPush(session, "NotifyGuildDormPlayerData");
            session.SendPush(BuildChatBoardLoginData(session.player));
            SendEmptyStartupPush(session, "NotifyHoldRegressionData");
            SendEmptyStartupPush(session, "NotifyHoldRegressionIgnoreChannel");
            SendEmptyStartupPush(session, "NotifyBountyTaskInfo");
            SendCurrentEventTaskBatch(session, CurrentEventTaskBatchBounty);
            session.SendPush(new NotifyFiveTwentyRecord());
            session.SendPush(purchaseDailyNotify);
            session.SendPush(purchaseRecommendConfig);
            session.SendPush(new NotifyDrawTicketData());
            SendEmptyStartupPush(session, "NotifyLoginItemCollectionData");
            session.SendPush(new NotifyBigWorldMainRedPoint());
            session.SendPush(BuildExternalRequiredBigWorldPlayerData());
            session.SendPush(BuildCurrentAccumulatedPayData());
            SendEmptyStartupPush(session, "NotifyAccumulateExpendData");
            if (arenaResult is not null)
                session.SendPush(arenaResult);
            session.SendPush(arenaActivity);
            SendCurrentEventTaskBatch(session, ArenaModule.CurrentTaskIds(session.player));
            session.SendPush(new NotifyFubenPrequelData() { FubenPrequelData = new() { RewardedStages = session.stage.PrequelRewardedStages } });
            session.SendPush(new NotifyPrequelChallengeRefreshTime() { NextRefreshTime = NextDailyRefreshTime() });
            session.SendPush(new NotifyDailyFubenLoginData() { RefreshTime = NextDailyRefreshTime() });
            session.SendPush(notifyBirthdayPlot);
            SendEmptyStartupPush(session, "NotifyBoardEffectData");
            SendEmptyStartupPush(session, "NotifyBountyChallengeData");
            SendEmptyStartupPush(session, "NotifyBountyChallengeMonsterDifficultyState");
            session.SendPush(new NotifyBriefStoryData());
            SendEmptyStartupPush(session, "NotifyCerberusGameData");
            SendEmptyStartupPush(session, "NotifyLoginCharacterTowerData");
            SendEmptyStartupPush(session, "NotifyClickClearData");
            SendEmptyStartupPush(session, "NotifyClientVersion");
            SendEmptyStartupPush(session, "NotifyColorTableActivityData");
            SendEmptyStartupPush(session, "NotifyCommunityData");
            SendEmptyStartupPush(session, "NotifyCoupletData");
            SendEmptyStartupPush(session, "NotifyCourseData");
            SendEmptyStartupPush(session, "NotifyDoomsdayDbChange");
            session.SendPush(BuildActivityDrawListPayload(session.player));
            session.SendPush(BuildActivityDrawGroupCountPayload(session.player));
            SendEmptyStartupPush(session, "NotifyEscapeData");
            SendEmptyStartupPush(session, "NotifyExperimentData");
            NotifyFashionStoryData fashionStoryData = FashionStoryModule.BuildNotify(session);
            if (fashionStoryData.ActivityId > 0)
                session.SendPush(fashionStoryData);
            session.SendPush(HitMouseModule.BuildNotifyHitMouseData(session.player));
            DyeMergeStagesRecordNotify dyeMergeData = DyeMergeModule.BuildNotify(session.player);
            if (dyeMergeData.ActivityId > 0)
                session.SendPush(dyeMergeData);
            session.SendPush(BossInshotModule.BuildNotifyBossInshotData(session.player));
            session.SendPush(BossInshotModule.BuildNotifyBossInshotPlayback(session.player));
            session.SendPush(BossModule.BuildLoginData(session.player));
            NotifyBossActivityData? bossActivityData = BossModule.BuildActivityLoginData(session);
            if (bossActivityData is not null)
                session.SendPush(bossActivityData);
            SendEmptyStartupPush(session, "NotifyFestivalData");
            SendEmptyStartupPush(session, "NotifyKotodamaData");
            SendEmptyStartupPush(session, "NotifyMazeData");
            SendEmptyStartupPush(session, "NotifyMechanismDataDb");
            StudyProgressModule.SendLoginState(session);
            SendEmptyStartupPush(session, "NotifyTrialData");
            session.SendPush(notifyFunctionalEntranceData);
            session.SendPush(DrawModule.BuildNotifyDrawCanLiverData(session.player));
            SendEmptyStartupPush(session, "NotifyGame2048DataDb");
            SendEmptyStartupPush(session, "NotifyGameCollectionData");
            SendCurrentEventTaskBatch(session, RetroArcadeTaskBatchEntry);
            SendEmptyStartupPush(session, "NotifyGoldenMinerGameInfo");
            SendEmptyStartupPush(session, "NotifyGuildSignPlayerData");
            SendEmptyStartupPush(session, "NotifyItemRestrictLoginData");
            session.SendPush(LifeTreeModule.BuildNotifyLifeTreeData(session.player));
            SendEmptyStartupPush(session, "NotifySelfChoiceLottoData");
            session.SendPush(new NotifyLoginMailCollectionBoxData());
            SendEmptyStartupPush(session, "NotifyNonogramData");
            SendEmptyStartupPush(session, "NotifyPivotCombatData");
            SendEmptyStartupPush(session, "NotifySettingLoadingOption");
            session.SendPush(RepeatChallengeModule.BuildLoginData(session.player));
            SendEmptyStartupPush(session, "NotifyPlayerReportData");
            SendEmptyStartupPush(session, "NotifySameColorGameData");
            SendEmptyStartupPush(session, "NotifyStrongholdLoginData");
            SendEmptyStartupPush(session, "NotifySucceedBossData");
            SendEmptyStartupPush(session, "NotifyTaikoMasterData");
            SendEmptyStartupPush(session, "NotifyTheatreData");
            SendCurrentEventTaskBatch(session, RetroArcadeTaskBatchPostTaikoA);
            SendCurrentEventTaskBatch(session, RetroArcadeTaskBatchPostTaikoB);
            NotifyTransfiniteData transfiniteData = TransfiniteModule.BuildNotify(session.player);
            if (transfiniteData.TransfiniteData is not null)
                session.SendPush(transfiniteData);
            SendEmptyStartupPush(session, "NotifyTurntableData");
            SendEmptyStartupPush(session, "NotifyVoteData");
            SendEmptyStartupPush(session, "NotifyWheelchairManualActivity");
            SendEmptyStartupPush(session, "NotifyTheatre4ActivityData");
            SendEmptyStartupPush(session, "NotifyRestaurantData");
            SendEmptyStartupPush(session, "NotifyBlackRockChessData");
            SendCurrentEventTaskBatch(session, RetroArcadeTaskBatchPostSubModesA);
            SendCurrentEventTaskBatch(session, RetroArcadeTaskBatchPostSubModesB);
            SendCurrentEventTaskBatch(session, RetroArcadeTaskBatchPostSubModesC);
            SendEmptyStartupPush(session, "NotifyReviewConfig");
            NotifyPassportData passportData = PassportModule.BuildNotifyPassportData(session.player, session.inventory);
            if (passportData.ActivityId > 0)
                session.SendPush(passportData);
            SendEmptyStartupPush(session, "NotifyMentorData");
            SendEmptyStartupPush(session, "NotifyMentorChat");
            SendEmptyStartupPush(session, "NotifyGuildData");
            session.SendPush(notifyMails);
            // Seed the home world-chat marquee during the login push stream (before the home
            // UI builds). Retail relies on live player traffic to populate it, which a
            // single-player private server never has; without a message present at home-build
            // time the client never creates the marquee component. (Regressed when the payload
            // integration moved world-chat delivery to the EnterWorldChatRequest response.)
            NotifyWorldChat loginWorldChat = new();
            loginWorldChat.ChatMessages.Add(ChatModule.MakeLuciaWorldMessage($"Hello {session.player.PlayerData.Name}! Welcome to AscNet, please read your mails if you haven't already.\n如果您还没有阅读邮件，请阅读邮件\n\nTry '/help' to get started"));
            session.SendPush(loginWorldChat);
            SendEmptyStartupPush(session, "NotifyGuildWarActivityData");
            SendEmptyStartupPush(session, "NotifyWheelchairManualActivityUpdate");

            ChatModule.FlushPendingLoginChat(session);
            session.player.Save();
        }

        private static void RepairProfileCosmeticRewards(Session session)
        {
            HashSet<int> claimedGatherRewardIds = session.player.GatherRewards.ToHashSet();
            bool fashionChanged = false;
            List<HeadPortraitList> repairedHeads = [];
            foreach (ExhibitionRewardTable exhibitionReward in TableReaderV2.Parse<ExhibitionRewardTable>().Where(reward =>
                claimedGatherRewardIds.Contains(reward.Id)
                && reward.RewardId is > 0))
            {
                foreach (var rewardGoods in RewardHandler.GetRewardGoods(exhibitionReward.RewardId!.Value))
                {
                    switch (RewardHandler.GetRewardType(rewardGoods))
                    {
                        case RewardType.Fashion:
                            fashionChanged |= RewardHandler.UnlockFashionReward(
                                rewardGoods.TemplateId,
                                session,
                                headPortraits: repairedHeads);
                            break;
                        case RewardType.HeadPortrait:
                            RewardHandler.UnlockHeadPortraitReward(
                                rewardGoods.TemplateId,
                                session,
                                repairedHeads);
                            break;
                    }
                }
            }

            foreach (HeadPortraitTable head in TableReaderV2.Parse<HeadPortraitTable>().Where(head => head.IsInit == 1))
                RewardHandler.UnlockHeadPortraitReward(head.Id, session, repairedHeads);

            Dictionary<int, FashionTable> fashions = TableReaderV2.Parse<FashionTable>()
                .ToDictionary(fashion => fashion.Id);
            foreach (FashionList ownedFashion in session.character.Fashions.Where(fashion => !fashion.IsLock))
            {
                if (ownedFashion.Id <= int.MaxValue
                    && fashions.TryGetValue((int)ownedFashion.Id, out FashionTable? fashion))
                {
                    RewardHandler.UnlockHeadPortraitReward(fashion.GiftId ?? 0, session, repairedHeads);
                }
            }

            if (fashionChanged)
                session.character.Save();
            if (repairedHeads.Count > 0)
                session.player.Save();
        }


        private static LoginCharacterList ToLoginCharacter(CharacterData character)
        {
            return new LoginCharacterList
            {
                Id = character.Id,
                Level = character.Level,
                Exp = character.Exp,
                Quality = character.Quality,
                InitQuality = character.InitQuality,
                Star = character.Star,
                Grade = character.Grade,
                SkillList = character.SkillList.Select(skill => new SkillList
                {
                    Id = skill.Id,
                    Level = skill.Level
                }).ToList(),
                EnhanceSkillList = character.EnhanceSkillList.Cast<dynamic>().ToList(),
                MagicList = character.MagicList,
                FashionId = character.FashionId,
                RandomFashion = character.RandomFashion,
                CreateTime = character.CreateTime,
                TrustLv = character.TrustLv,
                TrustExp = character.TrustExp,
                Ability = character.Ability,
                LiberateLv = character.LiberateLv,
                NewFlag = character.NewFlag,
                CollectState = character.CollectState,
                IsEnhanceSkillNotice = character.IsEnhanceSkillNotice,
                CharacterType = character.CharacterType,
                CharacterHeadInfo = new()
                {
                    HeadFashionId = character.CharacterHeadInfo?.HeadFashionId ?? 0,
                    HeadFashionType = character.CharacterHeadInfo?.HeadFashionType ?? 0
                }
            };
        }
    }
}
