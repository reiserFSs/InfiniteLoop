using AscNet.Common.MsgPack;
using MessagePack;
using Newtonsoft.Json;

namespace AscNet.GameServer.Handlers
{

    #region MsgPackScheme
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    [MessagePackObject(true)]
    public class GuildListRecommendRequest
    {
        public int PageNo;
    }

    [MessagePackObject(true)]
    public class GuildListRecommendResponse
    {
        public int Code;
    }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    #endregion

    internal class GuildModule
    {
        private const uint DefaultGuildId = 365;

        // TODO: Guild listing
        [RequestPacketHandler("GuildListRecommendRequest")]
        public static void GuildListRecommendRequestHandler(Session session, Packet.Request packet)
        {
            session.SendResponse(new GuildListRecommendResponse(), packet.Id);
        }

        [RequestPacketHandler("GuildListDetailRequest")]
        public static void GuildListDetailRequestHandler(Session session, Packet.Request packet)
        {
            GuildListDetailRequest request = MessagePackSerializer.Deserialize<GuildListDetailRequest>(packet.Content);
            uint guildId = ResolveGuildId(request.GuildId);

            session.SendResponse(new GuildListDetailResponse
            {
                Code = 0,
                GuildId = guildId,
                GuildName = "AscNet",
                GuildIconId = 1,
                GuildLevel = 1,
                GuildMemberCount = 1,
                GuildMemberMaxCount = 80,
                GuildTouristCount = 0,
                GuildTouristMaxCount = 10,
                GuildContributeLeft = 0,
                GuildContributeIn7Days = 0,
                GuildLeaderName = session.player.PlayerData.Name,
                GuildDeclaration = "AscNet private guild",
                RankNames = string.Empty,
                GiftContribute = 0,
                GiftGuildLevel = 1,
                GiftLevel = 0,
                GiftLevelGot = [],
                GiftGuildGot = (int)guildId,
                Build = 0,
                Option = 0,
                MinLevel = 1,
                MaintainState = 0,
                EmergenceTime = 0,
                TalentPointFromBuild = 0,
                Notice = string.Empty,
                TalentSumLevel = 0
            }, packet.Id);
        }

        [RequestPacketHandler("GuildMemberDetailRequest")]
        public static void GuildMemberDetailRequestHandler(Session session, Packet.Request packet)
        {
            GuildMemberDetailRequest request = MessagePackSerializer.Deserialize<GuildMemberDetailRequest>(packet.Content);
            GuildMemberDetailResponse response = new()
            {
                Code = 0,
                GuildId = ResolveGuildId(request.GuildId),
                CanImpeach = false,
                HasImpeach = false
            };
            response.MembersData.Add(BuildCurrentPlayerGuildMember(session));

            session.SendResponse(response, packet.Id);
        }

        [RequestPacketHandler("GuildListChatRequest")]
        public static void GuildListChatRequestHandler(Session session, Packet.Request packet)
        {
            session.SendResponse(new GuildListChatResponse
            {
                Code = 0,
                ChatList = [BuildGuildChatCacheMessage(session)]
            }, packet.Id);
        }

        [RequestPacketHandler("GuildWarOpenSupportPanelRequest")]
        public static void GuildWarOpenSupportPanelRequestHandler(Session session, Packet.Request packet)
        {
            session.SendResponse(new GuildWarOpenSupportPanelResponse
            {
                Code = 0,
                SupportDetail = new()
                {
                    CharacterId = (int)(session.character.Characters.FirstOrDefault()?.Id ?? 0),
                    SupportSupply = 0,
                    ToAssistRecords = [],
                    MyLogs = [],
                    GetAssistRecords = [],
                    MyAssistRecords = [],
                    LastRecvTime = (int)DateTimeOffset.Now.ToUnixTimeSeconds()
                }
            }, packet.Id);
        }

        private static uint ResolveGuildId(int guildId)
        {
            return guildId > 0 ? (uint)guildId : DefaultGuildId;
        }

        private static GuildMemberDetailResponse.GuildMemberDetailResponseMembersData BuildCurrentPlayerGuildMember(Session session)
        {
            return new()
            {
                Id = (uint)session.player.PlayerData.Id,
                Name = session.player.PlayerData.Name,
                HeadPortraitId = (uint)session.player.PlayerData.CurrHeadPortraitId,
                HeadFrameId = (int)session.player.PlayerData.CurrHeadFrameId,
                Level = (int)session.player.PlayerData.Level,
                RankLevel = 1,
                ContributeIn7Days = 0,
                ContributeAct = 0,
                ContributeHistory = 0,
                Popularity = 0,
                LastLoginTime = (uint)session.player.PlayerData.LastLoginTime,
                OnlineFlag = 1
            };
        }

        private static string BuildGuildChatCacheMessage(Session session)
        {
            return JsonConvert.SerializeObject(new
            {
                MessageId = 0,
                ChannelType = 6,
                MsgType = 1,
                SenderId = session.player.PlayerData.Id,
                TargetId = 0,
                Icon = (int)session.player.PlayerData.CurrHeadPortraitId,
                HeadFrameId = (int)session.player.PlayerData.CurrHeadFrameId,
                CreateTime = DateTimeOffset.Now.ToUnixTimeSeconds(),
                NickName = session.player.PlayerData.Name,
                NameplateId = 0,
                Content = "Welcome to AscNet.",
                CustomContent = (string?)null,
                GiftId = 0,
                GiftCount = 0,
                GiftStatus = 0,
                CurrMedalId = (int)session.player.PlayerData.CurrMedalId,
                BabelTowerTitleInfo = (object?)null,
                GuildRankLevel = 1,
                GuildName = "AscNet",
                MentorType = 1,
                CollectWordId = 0,
                ChatBoardId = (int)session.player.PlayerData.CurrentChatBoardId
            });
        }

    }
}
