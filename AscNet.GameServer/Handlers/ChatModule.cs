using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Commands;
using AscNet.Table.V2.share.chat;
using MessagePack;

namespace AscNet.GameServer.Handlers
{
    #region MsgPackScheme
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    [MessagePackObject(true)]
    public class EnterWorldChatRequest
    {
    }

    [MessagePackObject(true)]
    public class GetWorldChannelInfoRequest
    {
    }

    [MessagePackObject(true)]
    public class SelectChatChannelRequest
    {
        public int ChannelId { get; set; }
    }
    
    [MessagePackObject(true)]
    public class SelectChatChannelResponse
    {
        public int Code { get; set; }
    }
    
    [MessagePackObject(true)]
    public class GetEmojiPackageIdResponse
    {
        public int Code { get; set; }
        public List<int> OrderEmojiPackageIds { get; set; } = new();
    }
    
    [MessagePackObject(true)]
    public class SendChatRequest
    {
        public ChatData ChatData { get; set; }
        public List<long> TargetIdList { get; set; } = new();
    }
    
    [MessagePackObject(true)]
    public class SendChatResponse
    {
        public int Code { get; set; }
        public long RefreshTime { get; set; }
    }

    [MessagePackObject(true)]
    public class NotifyChatMessage
    {
        public int MessageId { get; set; }
        public ChatChannelType ChannelType { get; set; }
        public ChatMsgType MsgType { get; set; }
        public long SenderId { get; set; }
        public long TargetId { get; set; }
        public int Icon { get; set; }
        public int HeadFrameId { get; set; }
        public long CreateTime { get; set; }
        public string? NickName { get; set; }
        public int NameplateId { get; set; }
        public string? Content { get; set; }
        public string? CustomContent { get; set; }
        public int GiftId { get; set; }
        public int GiftCount { get; set; }
        public ChatGiftState GiftStatus { get; set; }
        public int CurrMedalId { get; set; }
        public object? BabelTowerTitleInfo { get; set; }
        public int GuildRankLevel { get; set; }
        public string? GuildName { get; set; }
        public int MentorType { get; set; }
        public int CollectWordId { get; set; }
        public int ChatBoardId { get; set; }
    }
    
    [MessagePackObject(true)]
    public class NotifyWorldChat
    {
        public List<ChatData> ChatMessages { get; set; } = new();
    }
    
    [MessagePackObject(true)]
    public class ChatData
    {
        public int MessageId { get; set; }
        public ChatChannelType ChannelType { get; set; }
        public ChatMsgType MsgType { get; set; }
        public long SenderId { get; set; }
        public int Icon { get; set; }
        public string NickName { get; set; }
        public long TargetId { get; set; }
        public long CreateTime { get; set; }
        public string? Content { get; set; }
        public int GiftId { get; set; }
        public int GiftCount { get; set; }
        public ChatGiftState GiftStatus { get; set; }
        public bool IsRead { get; set; }
        public int CurrMedalId { get; set; }
        public int BabelTowerLevel { get; set; }
        public int BabelTowerTitleId { get; set; }
        public int GuildRankLevel { get; set; }
        public string? GuildName { get; set; }
        public int CollectWordId { get; set; }
        public int NameplateId { get; set; }
    }

    public enum ChatChannelType
    {
        System = 1,
        World = 2,
        Private = 3,
        Room = 4,
        Battle = 5,
        Guild = 6,
        Mentor = 7,
    }

    public enum ChatMsgType
    {
        Normal = 1,
        Emoji = 2,
        Gift = 3,
        Tips = 4,
        RoomMsg = 5,
        System = 6,
        SpringFestival = 7,
    }

    public enum ChatGiftState {
        None = 0,
        WaitReceive = 1, 
        Received = 2, 
        Fetched = 3, 
    }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
#endregion

    internal class ChatModule
    {
        [RequestPacketHandler("EnterWorldChatRequest")]
        public static void EnterWorldChatRequestHandler(Session session, Packet.Request packet)
        {
            if (session.player is null)
            {
                session.PendingEnterWorldChatRequestId = packet.Id;
                return;
            }

            SendEnterWorldChatResponse(session, packet.Id);
        }

        [RequestPacketHandler("GetWorldChannelInfoRequest")]
        public static void GetWorldChannelInfoRequestHandler(Session session, Packet.Request packet)
        {
            if (session.player is null)
            {
                session.PendingGetWorldChannelInfoRequestId = packet.Id;
                return;
            }

            SendWorldChannelInfoResponse(session, packet.Id);
        }

        public static void FlushPendingLoginChat(Session session)
        {
            if (session.PendingEnterWorldChatRequestId is int enterWorldChatRequestId)
            {
                SendEnterWorldChatResponse(session, enterWorldChatRequestId);
                session.PendingEnterWorldChatRequestId = null;
            }

            if (session.PendingGetWorldChannelInfoRequestId is int getWorldChannelInfoRequestId)
            {
                SendWorldChannelInfoResponse(session, getWorldChannelInfoRequestId);
                session.PendingGetWorldChannelInfoRequestId = null;
            }
        }

        private static void SendEnterWorldChatResponse(Session session, int packetId)
        {
            session.SendResponse(new EnterWorldChatResponse
            {
                Code = 0,
                ChannelId = 1 // retail joins logical world-chat channel 1 here; this is not ChatChannelType.World
            }, packetId);
        }

        private static void SendWorldChannelInfoResponse(Session session, int packetId)
        {
            GetWorldChannelInfoResponse getWorldChannelInfoResponse = new();
            for (int channelId = 0; channelId <= 7; channelId++)
            {
                getWorldChannelInfoResponse.ChannelInfos.Add(new()
                {
                    ChannelId = channelId,
                    PlayerNum = 0
                });
            }

            session.SendResponse(getWorldChannelInfoResponse, packetId);
        }

        [RequestPacketHandler("OfflineMessageRequest")]
        public static void OfflineMessageRequestHandler(Session session, Packet.Request packet)
        {
            OfflineMessageResponse offlineMessageResponse = new()
            {
                Code = 0,
                Messages = Array.Empty<dynamic>()
            };
            session.SendResponse(offlineMessageResponse, packet.Id);
        }
        
        [RequestPacketHandler("SendChatRequest")]
        public static void SendChatRequestHandler(Session session, Packet.Request packet)
        {
            SendChatRequest request = MessagePackSerializer.Deserialize<SendChatRequest>(packet.Content);
            NotifyChatMessage notifyChatMessage = BuildNotifyChatMessage(session, request.ChatData);
            NotifyWorldChat notifyWorldChat = new();

            if (request.ChatData.Content is not null && request.ChatData.Content.StartsWith('/'))
            {
                var cmdStrings = request.ChatData.Content.Split(" ");

                try
                {
                    Command? cmd = CommandFactory.CreateCommand(cmdStrings.First().Split('/').Last(), session, cmdStrings[1..]);
                    if (cmd is null)
                    {
                        notifyWorldChat.ChatMessages.Add(MakeLuciaMessage($"Invalid command {cmdStrings.First().Split('/').Last()}, try /help"));
                    }

                    cmd?.Execute();
                    notifyWorldChat.ChatMessages.Add(MakeLuciaMessage("Command executed!"));
                }
                catch (CommandMessageCallbackException ex)
                {
                    notifyWorldChat.ChatMessages.Add(MakeLuciaMessage(ex.Message));
                }
                catch (Exception ex)
                {
#if DEBUG
                    notifyWorldChat.ChatMessages.Add(MakeLuciaMessage($"Command {cmdStrings.First().Split('/').Last()} failed to execute!, " + ex.ToString()));
#else
                    notifyWorldChat.ChatMessages.Add(MakeLuciaMessage($"Command {cmdStrings.First().Split('/').Last()} failed to execute!, " + ex.Message));
#endif
                }
            }

            session.SendPush(notifyChatMessage);
            session.SendResponse(new SendChatResponse() { Code = 0, RefreshTime = 0 }, packet.Id);
            session.SendPush(notifyWorldChat);
        }

        private static NotifyChatMessage BuildNotifyChatMessage(Session session, ChatData chatData)
        {
            return new()
            {
                MessageId = 0,
                ChannelType = chatData.ChannelType,
                MsgType = chatData.MsgType,
                SenderId = session.player.PlayerData.Id,
                TargetId = chatData.TargetId,
                Icon = (int)session.player.PlayerData.CurrHeadPortraitId,
                HeadFrameId = (int)session.player.PlayerData.CurrHeadFrameId,
                CreateTime = DateTimeOffset.Now.ToUnixTimeSeconds(),
                NickName = session.player.PlayerData.Name,
                NameplateId = 0,
                Content = chatData.Content?.TrimStart('\r', '\n'),
                CustomContent = null,
                GiftId = chatData.GiftId,
                GiftCount = chatData.GiftCount,
                GiftStatus = chatData.GiftStatus,
                CurrMedalId = (int)session.player.PlayerData.CurrMedalId,
                BabelTowerTitleInfo = null,
                GuildRankLevel = 1,
                GuildName = "AscNet",
                MentorType = 1,
                CollectWordId = chatData.CollectWordId,
                ChatBoardId = (int)session.player.PlayerData.CurrentChatBoardId
            };
        }

        
        [RequestPacketHandler("SelectChatChannelRequest")]
        public static void SelectChatChannelRequestHandler(Session session, Packet.Request packet)
        {
            // SelectChatChannelRequest request = MessagePackSerializer.Deserialize<SelectChatChannelRequest>(packet.Content);

            // disabling channel switching because the game is cringe and we don't need it anyway.
            session.SendResponse(new SelectChatChannelResponse()
            {
                Code = 20033013 // ChatChannelNotExist
            }, packet.Id);
        }

        public static ChatData MakeLuciaMessage(string content)
        {
            return new ChatData()
            {
                MessageId = 1,
                Content = content,
                Icon = 9010102,
                ChannelType = ChatChannelType.World,
                MsgType = ChatMsgType.Normal,
                SenderId = 0,
                NickName = "System - Lucia",
                CreateTime = DateTimeOffset.Now.ToUnixTimeSeconds()
            };
        }

        public static ChatData MakeLuciaWorldMessage(string content)
        {
            ChatData message = MakeLuciaMessage(content);
            message.SenderId = 88001;
            return message;
        }

        #region EmojiPackModule
        [RequestPacketHandler("GetEmojiPackageIdRequest")]
        public static void GetEmojiPackageIdRequestHandler(Session session, Packet.Request packet)
        {
            session.SendResponse(new GetEmojiPackageIdResponse()
            {
                Code = 0,
                OrderEmojiPackageIds = TableReaderV2.Parse<EmojiPackTable>().Select(x => x.Id).ToList()
            }, packet.Id);
        }
        #endregion
    }
}
