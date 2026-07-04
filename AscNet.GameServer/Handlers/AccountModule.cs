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
            session.SendResponse(new ReconnectResponse()
            {
                ReconnectToken = request.Token
            }, packet.Id);
        }

        [RequestPacketHandler("SetServerBeanRequest")]
        public static void SetServerBeanRequestHandler(Session session, Packet.Request packet)
        {
            _ = MessagePackSerializer.Deserialize<SetServerBeanRequest>(packet.Content);
            session.SendResponse(new SetServerBeanResponse(), packet.Id);
        }

        [RequestPacketHandler("ReconnectAck")]
        public static void ReconnectAckHandler(Session session, Packet.Request packet)
        {
            // The client uses this as an acknowledgement after ReconnectResponse.
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
                ItemList = session.inventory.Items,
                CharacterList = session.character.Characters.Select(ToLoginCharacter).ToList(),
                EquipList = session.character.Equips,
                HeadPortraitList = session.player.HeadPortraits,
                TeamGroupData = session.player.TeamGroups,
                BaseEquipLoginData = new(),
                FubenData = new()
                {
                    FubenBaseData = new()
                },
                FubenMainLineData = session.player.FubenMainLineData,
                FubenMainLine2Data = new(),
                FashionColorData = new(),
                FubenChapterExtraLoginData = new(),
                FubenUrgentEventData = new(),
                FubenShortStoryLoginData = new(),
                UseBackgroundId = session.player.UseBackgroundId
            };
            if (notifyLogin.PlayerData.DisplayCharIdList.Count < 1)
                notifyLogin.PlayerData.DisplayCharIdList.Add(notifyLogin.PlayerData.DisplayCharId);
            notifyLogin.FashionList.AddRange(session.character.Fashions);

#if DEBUG
            notifyLogin.PlayerData.GuideData = TableReaderV2.Parse<GuideGroupTable>().Select(x => (long)x.Id).ToList();
#endif

            return notifyLogin;
        }

        // TODO: Move somewhere else, also split.
        static void DoLogin(Session session)
        {
            long currentTime = DateTimeOffset.Now.ToUnixTimeSeconds();
            bool isNewDay = currentTime / 86_400 > session.player.PlayerData.LastLoginTime / 86_400;
            if (isNewDay)
            {
                session.player.PlayerData.NewPlayerTaskActiveDay += 1;
            }

            session.player.PlayerData.LastLoginTime = currentTime;
            session.player.AddGatherReward(5);
            NotifyLogin notifyLogin = BuildNotifyLogin(session);

            NotifyStageData notifyStageData = new()
            {
                StageList = session.stage.Stages.Values.ToList()
            };

            StageDatum stageForChat = new()
            {
                StageId = 10030201,
                StarsMark = 7,
                Passed = true,
                PassTimesToday = 0,
                PassTimesTotal = 1,
                BuyCount = 0,
                Score = 0,
                LastPassTime = DateTimeOffset.Now.ToUnixTimeSeconds(),
                RefreshTime = DateTimeOffset.Now.ToUnixTimeSeconds(),
                CreateTime = DateTimeOffset.Now.ToUnixTimeSeconds(),
                BestRecordTime = 0,
                LastRecordTime = 0,
                BestCardIds = new List<long> { 1021001 },
                LastCardIds = new List<long> { 1021001 }
            };

            if (!notifyStageData.StageList.Any(x => x.StageId == stageForChat.StageId))
                notifyStageData.StageList = notifyStageData.StageList.Append(stageForChat).ToList();

            NotifyCharacterDataList notifyCharacterData = new();
            notifyCharacterData.CharacterDataList.AddRange(session.character.Characters);
            
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

            NotifyItemDataList notifyItemDataList = new()
            {
                /*ItemDataList = TableReaderV2.Parse<Table.V2.share.item.ItemTable>().Select(x => new Item()
                {
                    Id = x.Id,
                    Count = x.MaxCount ?? 999_999_999,
                    RefreshTime = DateTimeOffset.Now.ToUnixTimeSeconds(),
                    CreateTime = DateTimeOffset.Now.ToUnixTimeSeconds()
                }).ToList(),*/
                ItemDataList = session.inventory.Items
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
            NotifyFubenBossSingleData notifyFubenBossSingleData = new()
            {
                FubenBossSingleData = new()
                {
                    ActivityNo = 1,
                    TotalScore = 0,
                    MaxScore = 0,
                    OldLevelType = 1,
                    LevelType = 1,
                    ChallengeCount = 0,
                    RemainTime = (uint)(3600 * 24),
                    AutoFightCount = 0,
                    RankPlatform = 0,
                    AfreshId = 0,
                    ChallengeLevelType = 0,
                    IsResetOpen = false
                }
            };
            session.SendPush(notifyLogin);
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
            session.SendPush(notifyStageData);
            session.SendPush(notifyCharacterData);
            session.SendPush(notifyAssistData);
            session.SendPush(notifyChatLoginData);
            session.SendPush(notifyItemDataList);
            session.SendPush(new NotifyTRPGData()
            {
                CurTargetLink = 10001,
                BaseInfo = new()
                {
                    Level = 1
                },
                BossInfo = new()
            });
            session.SendPush(notifyTaskData);
            session.SendPush(notifyGatherRewardList);
            session.SendPush(notifyBirthdayPlot);
            session.SendPush(notifyNewPlayerTaskStatus);
            session.SendPush(notifyFubenBossSingleData);

            #region DisclamerMail
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

            NotifyWorldChat notifyWorldChat = new();
            notifyWorldChat.ChatMessages.Add(ChatModule.MakeLuciaMessage($"Hello {session.player.PlayerData.Name}! Welcome to AscNet, please read mails if you haven't already.\n如果您还没有阅读邮件，请阅读邮件\n\nTry '/help' to get started"));

            session.SendPush(notifyMails);
            session.SendPush(notifyWorldChat);
            #endregion

            // NEEDED to not softlock!
            session.SendPush(new NotifyFubenPrequelData() { FubenPrequelData = new() });
            session.SendPush(new NotifyPrequelChallengeRefreshTime() { NextRefreshTime = (uint)DateTimeOffset.Now.ToUnixTimeSeconds() + 3600 * 24 });
            session.SendPush(new NotifyMainLineActivity() { EndTime = 0 });
            session.SendPush(new NotifyDailyFubenLoginData() { RefreshTime = (uint)DateTimeOffset.Now.ToUnixTimeSeconds() + 3600 * 24 });
            session.SendPush(new NotifyBriefStoryData());
            session.SendPush(new NotifyBfrtData() { BfrtData = new() });
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
