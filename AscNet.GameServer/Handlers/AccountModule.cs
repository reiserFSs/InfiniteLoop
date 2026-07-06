using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.chat;
using AscNet.Table.V2.share.guide;
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
        public string Version { get; set; } = "4.5.0";
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

#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    #endregion

    internal class AccountModule
    {
        private static readonly (long Id, long StartTime, long EndTime)[] CurrentDrawTimeLimitControls =
        [
            (36, 1780376400, 0),
            (50, 1777532400, 1778741940),
            (51, 1778742000, 1779951540),
            (52, 1779951600, 1781161140),
            (53, 1781161200, 1782370740),
            (54, 1782370800, 1783580340),
            (55, 1783580400, 1784789940),
            (47001, 1780376400, 1784178000),
            (47002, 1780376400, 1784178000),
            (47101, 1780358400, 1784242800),
            (47110, 1780376400, 1784178000),
            (47121, 1780376400, 1784178000),
            (47201, 1780358400, 1784242800),
            (47205, 1780376400, 1784264400),
            (47301, 1780376400, 1784242800),
            (47351, 1780376400, 1784178000),
            (47406, 1780358400, 1784242800),
            (47407, 1780358400, 1784242800),
            (47408, 1780376400, 1784242800),
            (47409, 1780376400, 1784264400),
            (47410, 1780376400, 1784264400),
            (47601, 1780376400, 1784242740),
            (47609, 1780376400, 1784264400),
            (47701, 1780376400, 1784264400),
            (47703, 1780358400, 1784242800),
            (47704, 1780358400, 1784242800),
            (47705, 1780376400, 1784264400),
            (47706, 1780653600, 1784264400),
            (47801, 1780376400, 1784264400),
            (47911, 1780653600, 1784242800),
            (47912, 1780358400, 1784242800),
            (47913, 1780653600, 1784242800),
            (47920, 1780358400, 1784242800),
            (47921, 1780358400, 1784242800),
            (47922, 1780358400, 1784242800),
            (47923, 1780358400, 1784242800),
            (47930, 1780358400, 1784242800),
            (47931, 1780376400, 1784178000),
            (47943, 1780376400, 1780653600),
            (47944, 1780376400, 1783573200),
            (47945, 1780376400, 1780740000),
            (47947, 1780376400, 1781604000),
            (2160712, 1780376400, 1784178000),
            (2160713, 1780376400, 1784178000)
        ];

        private const long HomeChatUnlockStageId = 10030201;
        private const long DefaultChatBoardId = 25000001;

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

        private static readonly long[] DefaultLoginGateMarks =
        [
            801,
            802,
            803,
            20000
        ];

        private static List<TimeLimitCtrlConfigList> BuildCurrentDrawTimeLimitControls()
        {
            return CurrentDrawTimeLimitControls
                .Select(timeLimit => new TimeLimitCtrlConfigList
                {
                    Id = timeLimit.Id,
                    StartTime = timeLimit.StartTime,
                    EndTime = timeLimit.EndTime
                })
                .ToList();
        }

        [RequestPacketHandler("HandshakeRequest")]
        public static void HandshakeRequestHandler(Session session, Packet.Request packet)
        {
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
            start:
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

            Session? previousSession = Server.Instance.Sessions
                .Select(x => x.Value)
                .Where(x => x.GetHashCode() != session.GetHashCode())
                .FirstOrDefault(x => x.player is not null && x.player.PlayerData.Id == player.PlayerData.Id);
            if (previousSession is not null)
            {
                // GateServerForceLogoutByAnotherLogin
                previousSession.SendPush(new ForceLogoutNotify() { Code = 1018 });
                previousSession.DisconnectProtocol();

                // Player data will be outdated without refetching it after disconnecting the previous session.
                goto start;
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

        [RequestPacketHandler("ReconnectRequest")]
        public static void ReconnectRequestHandler(Session session, Packet.Request packet)
        {
            ReconnectRequest request = MessagePackSerializer.Deserialize<ReconnectRequest>(packet.Content);
            Player? player;
            if (session.player is not null)
                player = session.player;
            else
            {
                player = Player.FromToken(request.Token);
                session.log.Debug("Player is reconnecting into new session...");
                if (player is not null && (session.character is null || session.stage is null || session.inventory is null))
                {
                    session.log.Debug("Reassigning player props...");
                    session.character = Character.FromUid(player.PlayerData.Id);
                    session.stage = Stage.FromUid(player.PlayerData.Id);
                    session.inventory = Inventory.FromUid(player.PlayerData.Id);
                }
            }

            if (player?.PlayerData.Id != request.PlayerId)
            {
                session.SendResponse(new ReconnectResponse()
                {
                    Code = 1029 // ReconnectInvalidToken
                }, packet.Id);
                return;
            }

            session.player = player;
            session.ContinuePushSequenceFrom(request.LastMsgSeqNo);
            session.SendResponse(new ReconnectResponse()
            {
                ReconnectToken = request.Token,
                RequestNo = request.LastMsgSeqNo
            }, packet.Id);
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

        private static NotifyLogin BuildNotifyLogin(Session session)
        {
            NotifyLogin notifyLogin = new()
            {
                PlayerData = session.player.PlayerData,
                TimeLimitCtrlConfigList = BuildCurrentDrawTimeLimitControls(),
                ItemList = Inventory.FilterClientItems(session.inventory.Items),
                CharacterList = session.character.Characters.Select(ToLoginCharacter).ToList(),
                EquipList = session.character.Equips,
                FashionList = session.character.Fashions,
                FashionSuitList = [],
                FashionColors = [],
                HeadPortraitList = session.player.HeadPortraits,
                TeamGroupData = session.player.TeamGroups,
                TeamPrefabData = new Dictionary<int, dynamic>(),
                BaseEquipLoginData = new(),
                FubenData = new()
                {
                    StageData = BuildLoginStageData(session),
                    FubenBaseData = new()
                },
                IsSetFightCgEnable = true,
                FubenMainLineData = session.player.FubenMainLineData,
                FubenEventData = new(),
                FubenMainLine2Data = new(),
                FubenMainLineLuosaitaData = new(),
                FashionColorData = new(),
                FubenChapterExtraLoginData = new(),
                FubenUrgentEventData = new(),
                FubenShortStoryLoginData = new(),
                SignInfos = SignInModule.BuildLoginSignInfos(session.player),
                UseBackgroundId = session.player.UseBackgroundId,
                HaveBackgroundIds = BuildHaveBackgroundIds(session.player),
                RandomBackgroundLoginData = new(),
                FunctionOpenTimeConfigList = BuildFunctionOpenTimeConfigList(),
                DlcPlayerData = new(),
                DlcCharacterList = session.character.Characters.Select(ToDlcCharacter).ToList(),
                RedPointRecords = session.player.RedPointRecords ?? new()
            };
            if (notifyLogin.PlayerData.DisplayCharIdList.Count < 1)
                notifyLogin.PlayerData.DisplayCharIdList.Add(notifyLogin.PlayerData.DisplayCharId);

            notifyLogin.PlayerData.GuideData = TableReaderV2.Parse<GuideGroupTable>().Select(x => (long)x.Id).ToList();
            notifyLogin.PlayerData.Marks ??= new List<long>();
            foreach (long mark in DefaultLoginGateMarks)
            {
                if (!notifyLogin.PlayerData.Marks.Contains(mark))
                    notifyLogin.PlayerData.Marks.Add(mark);
            }

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

            if (!stageData.ContainsKey(HomeChatUnlockStageId))
                stageData[HomeChatUnlockStageId] = BuildPassedStage(HomeChatUnlockStageId);

            return stageData;
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

        private static List<FunctionOpenTimeConfig> BuildFunctionOpenTimeConfigList()
        {
            return
            [
                new()
                {
                    FunctionId = 20000,
                    TimeId = 100
                }
            ];
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

        private static NotifyChatBoardLoginData BuildChatBoardLoginData(Player player)
        {
            long currentChatBoardId = player.PlayerData.CurrentChatBoardId;
            if (currentChatBoardId <= 0)
            {
                currentChatBoardId = DefaultChatBoardId;
                player.PlayerData.CurrentChatBoardId = currentChatBoardId;
            }

            long getTime = player.PlayerData.CreateTime > 0
                ? player.PlayerData.CreateTime
                : DateTimeOffset.Now.ToUnixTimeSeconds();
            HashSet<long> chatBoardIds = DefaultChatBoardIds.ToHashSet();
            chatBoardIds.Add(currentChatBoardId);

            return new()
            {
                CurrentChatBoardId = currentChatBoardId,
                ChatBoards = chatBoardIds
                    .Order()
                    .Select(chatBoardId => new NotifyChatBoardLoginData.NotifyChatBoardLoginDataChatBoard
                    {
                        Id = chatBoardId,
                        GetTime = getTime,
                        EndTime = 0
                    })
                    .ToList()
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

        private static NotifyFubenBossSingleData BuildBossSingleLoginData()
        {
            return new()
            {
                FubenBossSingleData = new()
                {
                    ActivityNo = 260,
                    OldLevelType = 8,
                    LevelType = 8,
                    RemainTime = 3600 * 24,
                    RankPlatform = 1,
                    AfreshId = 1,
                    IsResetOpen = true,
                    TrialStageInfoList =
                    [
                        BuildBossSingleStageInfo(30302803),
                        BuildBossSingleStageInfo(30302804),
                        BuildBossSingleStageInfo(30302805)
                    ],
                    NormalStageTeamInfos =
                    [
                        BuildBossSingleTeamInfo(2030),
                        BuildBossSingleTeamInfo(2034),
                        BuildBossSingleTeamInfo(2038)
                    ]
                },
                BossListDict = new()
                {
                    [7] = new() { 102, 104, 109 },
                    [8] = new() { 2030, 2034, 2038 }
                }
            };
        }

        private static Dictionary<string, object> BuildBossSingleStageInfo(int stageId)
        {
            return new()
            {
                ["StageId"] = stageId,
                ["Score"] = 0
            };
        }

        private static Dictionary<string, object> BuildBossSingleTeamInfo(int sectionId)
        {
            return new()
            {
                ["SectionId"] = sectionId,
                ["CharacterIds"] = Array.Empty<int>()
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
                ["Version"] = "4.5.0",
                ["KickOut"] = false
            }),
            ["NotifyActivityDrawList"] = SerializeStartupPayload(new Dictionary<string, object?>
            {
                ["DrawIdList"] = Array.Empty<object>()
            }),
            ["NotifyActivityDrawGroupCount"] = SerializeStartupPayload(new Dictionary<string, object?>
            {
                ["Count"] = 0
            }),
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
            ["NotifyPassportBaseInfo"] = SerializeStartupPayload(new Dictionary<string, object?>
            {
                ["BaseInfo"] = new Dictionary<string, object?>
                {
                    ["Level"] = 1,
                    ["Exp"] = 0
                }
            }),
            ["NotifyPassportAutoGetTaskReward"] = SerializeStartupPayload(new Dictionary<string, object?>
            {
                ["ActivityId"] = 44,
                ["RewardList"] = Array.Empty<object>()
            }),
            ["NotifyPassportData"] = SerializeStartupPayload(new Dictionary<string, object?>
            {
                ["ActivityId"] = 44,
                ["Level"] = 1,
                ["BaseInfo"] = new Dictionary<string, object?>
                {
                    ["Level"] = 1,
                    ["Exp"] = 0
                },
                ["PassportInfos"] = Array.Empty<object>(),
                ["LastTimeBaseInfo"] = new Dictionary<string, object?>
                {
                    ["Level"] = 0,
                    ["Exp"] = 0
                },
                ["IsGetSupplyReward"] = false,
                ["IsActivateRegressionTask"] = false,
                ["IsActivateNewbieTask"] = false
            }),
            ["NotifyMentorChat"] = SerializeStartupPayload(new Dictionary<string, object?>
            {
                ["ChatMessages"] = Array.Empty<object>()
            }),
            ["NotifyWheelchairManualActivityUpdate"] = SerializeStartupPayload(new Dictionary<string, object?>
            {
                ["UpdateTimeLimitActivityInfos"] = Array.Empty<object>(),
                ["UpdateWeekActivityInfos"] = Array.Empty<object>(),
                ["CurrentGuildBossEndTime"] = 0
            })
        };

        private static byte[] SerializeStartupPayload(Dictionary<string, object?> payload)
        {
            return MessagePackSerializer.Serialize(payload);
        }

        private static void SendEmptyStartupPush(Session session, string name)
        {
            if (SupportedStartupPushPayloads.TryGetValue(name, out byte[]? supportedPayload))
                session.SendPush(name, supportedPayload);
        }


        private static NotifyMails BuildDisclaimerMail()
        {
            NotifyMails notifyMails = new();
            notifyMails.NewMailList.Add(new NotifyMails.NotifyMailsNewMailList()
            {
                Id = "0",
                Status = 0, // MAIL_STATUS_UNREAD
                SendName = "<color=#8b0000><b>AscNet</b></color> Developers",
                Title = "<b>[IMPORTANT]</b> Information Regarding This Server Software [有关本服务器软件的信息］",
                Content = @"Hello Commandant!
Welcome to <color=#8b0000><b>AscNet</b></color>, we are happy that you are using this <b>Server Software</b>.
This <b>Server Software</b> is always free and if you are paying to gain access to this you are being SCAMMED, we encourage you to help prevent another buyer like you by making a PSA or telling others whom you may see as potential users.
Sorry for the inconvenience.

欢迎来到 <color=#8b0000><b>AscNet</b></color>，我们很高兴您使用本服务器软件。
本服务器软件始终是免费的，如果您是通过付费来使用本软件，那您就被骗了，我们鼓励您告诉其他潜在用户，以防止再有像您这样的买家。
不便之处，敬请原谅。
[中文版为机器翻译，准确内容请参考英文信息］",
                CreateTime = ((DateTimeOffset)Process.GetCurrentProcess().StartTime).ToUnixTimeSeconds(),
                SendTime = ((DateTimeOffset)Process.GetCurrentProcess().StartTime).ToUnixTimeSeconds(),
                ExpireTime = DateTimeOffset.Now.ToUnixTimeSeconds() * 2,
                IsForbidDelete = true
            });

            return notifyMails;
        }

        // TODO: Move somewhere else, also split.
        static void DoLogin(Session session)
        {
            DoLogin(session, updateLoginAccounting: true);
        }

        static void DoLogin(Session session, bool updateLoginAccounting)
        {
            long currentTime = DateTimeOffset.Now.ToUnixTimeSeconds();
            if (updateLoginAccounting)
            {
                bool isNewDay = currentTime / 86_400 > session.player.PlayerData.LastLoginTime / 86_400;
                if (isNewDay)
                {
                    session.player.PlayerData.NewPlayerTaskActiveDay += 1;
                }

                session.player.PlayerData.LastLoginTime = currentTime;
                session.player.AddGatherReward(5);
            }
            NotifyLogin notifyLogin = BuildNotifyLogin(session);

            NotifyAssistData notifyAssistData = new()
            {
                AssistData = new()
                {
                    AssistCharacterId = session.character.Characters.First().Id
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
                    NewbieHonorReward = false,
                    NewbieUnlockPeriod = 7,
                    Course = session.stage.Course,
                    FinishedTasks = session.stage.FinishedTasks,
                    Tasks = TaskModule.BuildStoryTaskData(session),
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
            NotifyMails notifyMails = BuildDisclaimerMail();
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
            session.SendPush(new NotifyArchiveLoginData());
            SendEmptyStartupPush(session, "NotifyLoginAwarenessInfo");
            session.SendPush(notifyChatLoginData);
            session.SendPush(new NotifySocialData());
            session.SendPush(notifyTaskData);
            session.SendPush(new NotifyActivenessStatus());
            session.SendPush(notifyNewPlayerTaskStatus);
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
            session.SendPush(new NotifyDormitoryData());
            session.SendPush(BuildTrpgLoginData());
            session.SendPush(new NotifyMedalData());
            session.SendPush(new NotifyExploreData());
            session.SendPush(notifyGatherRewardList);
            session.SendPush(new NotifyGuildEvent());
            SendEmptyStartupPush(session, "NotifyNewActivityCalendarData");
            session.SendPush(notifyAssistData);
            SendEmptyStartupPush(session, "NotifyDlcFightCharacterId");
            SendEmptyStartupPush(session, "NotifyDlcChipFormDataList");
            SendEmptyStartupPush(session, "NotifyDlcChipAssistChipId");
            SendEmptyStartupPush(session, "NotifyTheatre5ActivityData");
            session.SendPush(BuildEmptyNotifyTask());
            SendEmptyStartupPush(session, "NotifyTheatre6ActivityData");
            SendEmptyStartupPush(session, "NotifyNameplateLoginData");
            SendEmptyStartupPush(session, "NotifyGuildDormPlayerData");
            session.SendPush(BuildChatBoardLoginData(session.player));
            SendEmptyStartupPush(session, "NotifyHoldRegressionData");
            SendEmptyStartupPush(session, "NotifyHoldRegressionIgnoreChannel");
            SendEmptyStartupPush(session, "NotifyBountyTaskInfo");
            session.SendPush(BuildEmptyNotifyTask());
            session.SendPush(new NotifyFiveTwentyRecord());
            session.SendPush(purchaseDailyNotify);
            session.SendPush(purchaseRecommendConfig);
            session.SendPush(new NotifyDrawTicketData());
            SendEmptyStartupPush(session, "NotifyLoginItemCollectionData");
            SendEmptyStartupPush(session, "NotifyBigWorldMainRedPoint");
            SendEmptyStartupPush(session, "NotifyExternalRequiredBigWorldPlayerData");
            session.SendPush(new NotifyAccumulatedPayData());
            SendEmptyStartupPush(session, "NotifyAccumulateExpendData");
            session.SendPush(new NotifyArenaActivity());
            session.SendPush(BuildEmptyNotifyTask());
            session.SendPush(new NotifyFubenPrequelData() { FubenPrequelData = new() });
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
            SendEmptyStartupPush(session, "NotifyActivityDrawList");
            SendEmptyStartupPush(session, "NotifyActivityDrawGroupCount");
            SendEmptyStartupPush(session, "NotifyEscapeData");
            SendEmptyStartupPush(session, "NotifyExperimentData");
            session.SendPush(BuildBossSingleLoginData());
            SendEmptyStartupPush(session, "NotifyFestivalData");
            SendEmptyStartupPush(session, "NotifyKotodamaData");
            SendEmptyStartupPush(session, "NotifyMazeData");
            SendEmptyStartupPush(session, "NotifyMechanismDataDb");
            SendEmptyStartupPush(session, "NotifyPracticeData");
            SendEmptyStartupPush(session, "NotifyTeachingActivityInfo");
            SendEmptyStartupPush(session, "NotifyTrialData");
            session.SendPush(notifyFunctionalEntranceData);
            SendEmptyStartupPush(session, "NotifyGachaCanLiverData");
            SendEmptyStartupPush(session, "NotifyGame2048DataDb");
            SendEmptyStartupPush(session, "NotifyGameCollectionData");
            session.SendPush(BuildEmptyNotifyTask());
            SendEmptyStartupPush(session, "NotifyGoldenMinerGameInfo");
            SendEmptyStartupPush(session, "NotifyGuildSignPlayerData");
            SendEmptyStartupPush(session, "NotifyItemRestrictLoginData");
            session.SendPush(LifeTreeModule.BuildNotifyLifeTreeData(session.player));
            SendEmptyStartupPush(session, "NotifySelfChoiceLottoData");
            SendEmptyStartupPush(session, "NotifyLoginMailCollectionBoxData");
            SendEmptyStartupPush(session, "NotifyNonogramData");
            SendEmptyStartupPush(session, "NotifyPivotCombatData");
            SendEmptyStartupPush(session, "NotifySettingLoadingOption");
            SendEmptyStartupPush(session, "NotifyRepeatChallengeData");
            SendEmptyStartupPush(session, "NotifyPlayerReportData");
            SendEmptyStartupPush(session, "NotifySameColorGameData");
            SendEmptyStartupPush(session, "NotifyStrongholdLoginData");
            SendEmptyStartupPush(session, "NotifySucceedBossData");
            SendEmptyStartupPush(session, "NotifyTaikoMasterData");
            SendEmptyStartupPush(session, "NotifyTheatreData");
            session.SendPush(BuildEmptyNotifyTask());
            session.SendPush(BuildEmptyNotifyTask());
            SendEmptyStartupPush(session, "NotifyTransfiniteData");
            SendEmptyStartupPush(session, "NotifyTurntableData");
            SendEmptyStartupPush(session, "NotifyVoteData");
            SendEmptyStartupPush(session, "NotifyWheelchairManualActivity");
            SendEmptyStartupPush(session, "NotifyTheatre4ActivityData");
            SendEmptyStartupPush(session, "NotifyRestaurantData");
            SendEmptyStartupPush(session, "NotifyBlackRockChessData");
            session.SendPush(BuildEmptyNotifyTask());
            session.SendPush(BuildEmptyNotifyTask());
            session.SendPush(BuildEmptyNotifyTask());
            SendEmptyStartupPush(session, "NotifyReviewConfig");
            SendEmptyStartupPush(session, "NotifyPassportData");
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
                FashionId = character.FashionId,
                CreateTime = character.CreateTime,
                TrustLv = character.TrustLv,
                TrustExp = character.TrustExp,
                Ability = character.Ability,
                LiberateLv = character.LiberateLv,
                CharacterHeadInfo = new()
                {
                    HeadFashionId = character.CharacterHeadInfo?.HeadFashionId ?? 0,
                    HeadFashionType = character.CharacterHeadInfo?.HeadFashionType ?? 0
                }
            };
        }
    }
}
