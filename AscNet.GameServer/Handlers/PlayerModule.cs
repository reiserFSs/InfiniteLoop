using AscNet.Common.MsgPack;
using AscNet.Common.Database;
using MessagePack;

namespace AscNet.GameServer.Handlers
{

    #region MsgPackScheme
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    [MessagePackObject(true)]
    public class ChangePlayerMarkRequest
    {
        public long MaskId;
    }

    [MessagePackObject(true)]
    public class ChangeCommunicationResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class TouchBoardMutualRequest
    {
        public int CharacterId;
    }

    [MessagePackObject(true)]
    public class TouchBoardMutualResponse
    {
    }

    [MessagePackObject(true)]
    public class ChangeCommunicationRequest
    {
        public long Id;
    }

    [MessagePackObject(true)]
    public class ChangePlayerBirthdayRequest : Birthday
    {
    }

    [MessagePackObject(true)]
    public class ChangePlayerBirthdayResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class ChangePlayerGenderRequest
    {
        public int Gender;
    }

    [MessagePackObject(true)]
    public class NotifyPlayerGender
    {
        public long Gender;
        public long ChangeGenderTime;
    }

    [MessagePackObject(true)]
    public class ChangePlayerGenderResponse
    {
        public int Code;
        public long Gender;
        public long ChangeGenderTime;
        public long NextCanChangeTime;
        public PlayerData PlayerData;
        public List<RewardGoods> RewardGoodsList = new();
    }

    [MessagePackObject(true)]
    public class ChangePlayerSignRequest
    {
        public string Msg;
    }

    [MessagePackObject(true)]
    public class ChangePlayerSignResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class SetHeadPortraitRequest
    {
        public long Id { get; set; }
    }

    [MessagePackObject(true)]
    public class SetHeadPortraitResponse
    {
        public int Code { get; set; }
    }

    [MessagePackObject(true)]
    public class SetHeadFrameRequest
    {
        public long Id { get; set; }
    }

    [MessagePackObject(true)]
    public class SetHeadFrameResponse
    {
        public int Code { get; set; }
    }

    [MessagePackObject(true)]
    public class SetCurrentMedalRequest
    {
        public long Id { get; set; }
    }

    [MessagePackObject(true)]
    public class SetCurrentMedalResponse
    {
        public int Code { get; set; }
    }

    [MessagePackObject(true)]
    public class NotifyPlayerCurrMedalId
    {
        public long CurrMedalId { get; set; }
    }

    [MessagePackObject(true)]
    public class SetCurChatBoardRequest
    {
        public long ChatBoardId { get; set; }
    }

    [MessagePackObject(true)]
    public class SetCurChatBoardResponse
    {
        public int Code { get; set; }
    }

    [MessagePackObject(true)]
    public class NotifyCurChatBoardId
    {
        public long CurrentChatBoardId { get; set; }
    }

    [MessagePackObject(true)]
    public class NotifyPlayerName
    {
        public string Name;
    }

    [MessagePackObject(true)]
    public class ChangePlayerNameRequest
    {
        public string Name;
    }

    [MessagePackObject(true)]
    public class ChangePlayerNameResponse
    {
        public int Code;
        public long NextCanChangeTime;
    }

    [MessagePackObject(true)]
    public class RemovePlayerDisplayCharIdRequest
    {
        public long CharId;
    }

    [MessagePackObject(true)]
    public class RemovePlayerDisplayCharIdResponse
    {
        public int Code;
        public List<long> DisplayCharIdList;
    }

    [MessagePackObject(true)]
    public class AddPlayerDisplayCharIdRequest
    {
        public long CharId;
    }

    [MessagePackObject(true)]
    public class AddPlayerDisplayCharIdResponse
    {
        public int Code;
        public List<long> DisplayCharIdList;
    }

    [MessagePackObject(true)]
    public class UpdatePlayerDisplayCharIdRequest
    {
        public long NewCharId;
        public long OldCharId;
    }

    [MessagePackObject(true)]
    public class UpdatePlayerDisplayCharIdResponse
    {
        public int Code;
        public List<long> DisplayCharIdList;
    }

    [MessagePackObject(true)]
    public class SetDisplayCharIdFirstRequest
    {
        public long CharId;
    }

    [MessagePackObject(true)]
    public class SetDisplayCharIdFirstResponse
    {
        public int Code;
        public List<long> DisplayCharIdList;
    }

    [MessagePackObject(true)]
    public class QueryPlayerDetailRequest
    {
        public int PlayerId;
    }

    [MessagePackObject(true)]
    public class QueryPlayerDetailResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class SetAppearanceRequest
    {
        public int CharacterAppearanceType;
        public dynamic? Characters;
        public AppearanceSettingInfo AppearanceSettingInfo;
    }

    [MessagePackObject(true)]
    public class SetAppearanceResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class XCustomComponentData
    {
        public float PositionX;
        public float PositionY;
        public float Scale;
        public float Alpha;
        public bool IsActive;
        public bool IsShowPcTips;
    }

    [MessagePackObject(true)]
    public class XKeyPadPanelCustomData
    {
        public int SchemeId;
        public uint Version;
        public int BallDirection;
        public bool IsShowFps;
        public bool IsShowSignal;
        public bool IsShowQteIcon;
        public int JoystickType;
        public float SafeScreenAreaWidth;
        public float SafeScreenAreaHeight;
        public Dictionary<int, XCustomComponentData> UiData;
    }

    [MessagePackObject(true)]
    public class SyncPlayerKeyPadSettingRequest
    {
        public int CurSchemeId;
        public List<XKeyPadPanelCustomData> PlayerKeyPadSettingList;
    }

    [MessagePackObject(true)]
    public class SyncPlayerKeyPadSettingResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class RecordPlayerKeyPadSettingRequest
    {
        public int CurSchemeId;
        public XKeyPadPanelCustomData KeyPadCustomData;
    }

    [MessagePackObject(true)]
    public class RecordPlayerKeyPadSettingResponse
    {
        public int Code;
    }
    [MessagePackObject(true)]
    public class RecordPlayerPointRequest
    {
        public int PointId;
        public int PointType;
    }

    [MessagePackObject(true)]
    public class RecordPlayerPointResponse
    {
        public int Code;
    }

#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    #endregion

    internal class PlayerModule
    {
        [RequestPacketHandler("ChangePlayerMarkRequest")]
        public static void ChangePlayerMarkRequestHandler(Session session, Packet.Request packet)
        {
            ChangePlayerMarkRequest request = MessagePackSerializer.Deserialize<ChangePlayerMarkRequest>(packet.Content);

            if (session.player.PlayerData.Marks is null)
            {
                session.log.Debug("Marks is somehow null");
                session.player.PlayerData.Marks = new();
            }

            session.player.PlayerData.Marks.Add(request.MaskId);
            session.SendResponse(new ChangePlayerMarkResponse(), packet.Id);
        }

        [RequestPacketHandler("ChangeCommunicationRequest")]
        public static void ChangeCommunicationRequestHandler(Session session, Packet.Request packet)
        {
            ChangeCommunicationRequest request = MessagePackSerializer.Deserialize<ChangeCommunicationRequest>(packet.Content);
            session.player.PlayerData.Communications.Add(request.Id);

            session.SendResponse(new ChangeCommunicationResponse(), packet.Id);
        }

        [RequestPacketHandler("TouchBoardMutualRequest")]
        public static void TouchBoardMutualRequestHandler(Session session, Packet.Request packet)
        {
            TouchBoardMutualRequest request = MessagePackSerializer.Deserialize<TouchBoardMutualRequest>(packet.Content);

            session.SendResponse(new TouchBoardMutualResponse(), packet.Id);
            TaskModule.RecordConditionType(session, 13212);
        }

        [RequestPacketHandler("ChangePlayerNameRequest")]
        public static void ChangePlayerNameRequestHandler(Session session, Packet.Request packet)
        {
            ChangePlayerNameRequest request = MessagePackSerializer.Deserialize<ChangePlayerNameRequest>(packet.Content);
            session.player.PlayerData.Name = request.Name;
            session.player.PlayerData.ChangeNameTime = DateTimeOffset.Now.ToUnixTimeSeconds();

            NotifyPlayerName notifyPlayerName = new() { Name = session.player.PlayerData.Name };
            session.SendPush(notifyPlayerName);
            session.SendResponse(new ChangePlayerNameResponse() { NextCanChangeTime = session.player.PlayerData.ChangeNameTime }, packet.Id);
        }

        [RequestPacketHandler("ChangePlayerSignRequest")]
        public static void ChangePlayerSignRequestHandler(Session session, Packet.Request packet)
        {
            ChangePlayerSignRequest request = MessagePackSerializer.Deserialize<ChangePlayerSignRequest>(packet.Content);
            session.player.PlayerData.Sign = request.Msg;

            session.SendResponse(new ChangePlayerSignResponse(), packet.Id);
        }

        [RequestPacketHandler("SetHeadPortraitRequest")]
        public static void SetHeadPortraitRequestHandler(Session session, Packet.Request packet)
        {
            SetHeadPortraitRequest request = MessagePackSerializer.Deserialize<SetHeadPortraitRequest>(packet.Content);
            session.player.PlayerData.CurrHeadPortraitId = request.Id;
            session.player.Save();
            session.SendResponse(new SetHeadPortraitResponse(), packet.Id);
        }

        [RequestPacketHandler("SetHeadFrameRequest")]
        public static void SetHeadFrameRequestHandler(Session session, Packet.Request packet)
        {
            SetHeadFrameRequest request = MessagePackSerializer.Deserialize<SetHeadFrameRequest>(packet.Content);
            session.player.PlayerData.CurrHeadFrameId = request.Id;
            session.player.Save();
            session.SendResponse(new SetHeadFrameResponse(), packet.Id);
        }

        [RequestPacketHandler("SetCurrentMedalRequest")]
        public static void SetCurrentMedalRequestHandler(Session session, Packet.Request packet)
        {
            SetCurrentMedalRequest request = MessagePackSerializer.Deserialize<SetCurrentMedalRequest>(packet.Content);
            session.player.PlayerData.CurrMedalId = request.Id;
            session.player.Save();
            session.SendPush(new NotifyPlayerCurrMedalId
            {
                CurrMedalId = session.player.PlayerData.CurrMedalId
            });
            session.SendResponse(new SetCurrentMedalResponse(), packet.Id);
        }

        [RequestPacketHandler("SetCurChatBoardRequest")]
        public static void SetCurChatBoardRequestHandler(Session session, Packet.Request packet)
        {
            SetCurChatBoardRequest request = MessagePackSerializer.Deserialize<SetCurChatBoardRequest>(packet.Content);
            session.player.PlayerData.CurrentChatBoardId = request.ChatBoardId;
            session.player.Save();
            session.SendPush(new NotifyCurChatBoardId
            {
                CurrentChatBoardId = session.player.PlayerData.CurrentChatBoardId
            });
            session.SendResponse(new SetCurChatBoardResponse(), packet.Id);
        }

        [RequestPacketHandler("GetPlayerInfoListRequest")]
        public static void GetPlayerInfoListRequestHandler(Session session, Packet.Request packet)
        {
            GetPlayerInfoListRequest request = MessagePackSerializer.Deserialize<GetPlayerInfoListRequest>(packet.Content);
            GetPlayerInfoListResponse response = new();
            HashSet<uint> emittedPlayerIds = new();

            foreach (uint id in request.Ids)
            {
                if (!emittedPlayerIds.Add(id))
                    continue;

                Player? player = (long)id == session.player.PlayerData.Id
                    ? session.player
                    : Player.TryFromPlayerId(id);

                response.PlayerInfoList.Add(player is null ? ToFallbackPlayerInfo(id) : ToPlayerInfo(player));
            }

            session.SendResponse(response, packet.Id);
        }

        private static GetPlayerInfoListResponse.GetPlayerInfoListResponsePlayerInfo ToPlayerInfo(Player player)
        {
            return new()
            {
                Id = (uint)player.PlayerData.Id,
                Name = player.PlayerData.Name,
                Level = (int)player.PlayerData.Level,
                FriendExp = 0,
                Sign = player.PlayerData.Sign,
                CurrHeadPortraitId = (uint)player.PlayerData.CurrHeadPortraitId,
                CurrHeadFrameId = (int)player.PlayerData.CurrHeadFrameId,
                LastLoginTime = (uint)player.PlayerData.LastLoginTime,
                IsOnline = false,
                CurrMedalId = (int)player.PlayerData.CurrMedalId,
                IsCancel = false,
                DlcMultiplayerTitle = 0
            };
        }

        private static GetPlayerInfoListResponse.GetPlayerInfoListResponsePlayerInfo ToFallbackPlayerInfo(uint id)
        {
            return new()
            {
                Id = id,
                Name = $"Commandant {id}",
                Level = 1,
                FriendExp = 0,
                Sign = string.Empty,
                CurrHeadPortraitId = 9000003,
                CurrHeadFrameId = 0,
                LastLoginTime = (uint)DateTimeOffset.Now.ToUnixTimeSeconds(),
                IsOnline = false,
                CurrMedalId = 0,
                IsCancel = false,
                DlcMultiplayerTitle = 0
            };
        }

        [RequestPacketHandler("ChangePlayerBirthdayRequest")]
        public static void ChangePlayerBirthdayRequestHandler(Session session, Packet.Request packet)
        {
            ChangePlayerBirthdayRequest request = MessagePackSerializer.Deserialize<ChangePlayerBirthdayRequest>(packet.Content);
            session.player.PlayerData.Birthday = request;
            session.SendPush(new NotifyBirthdayPlot() { IsChange = 1 });

            session.SendResponse(new ChangePlayerBirthdayResponse(), packet.Id);
        }

        [RequestPacketHandler("ChangePlayerGenderRequest")]
        public static void ChangePlayerGenderRequestHandler(Session session, Packet.Request packet)
        {
            ChangePlayerGenderRequest request = MessagePackSerializer.Deserialize<ChangePlayerGenderRequest>(packet.Content);
            if (request.Gender is < 1 or > 3)
            {
                // PlayerGenderCfgNotExist
                session.SendResponse(new ChangePlayerGenderResponse() { Code = 20002020 }, packet.Id);
                return;
            }

            bool isFirstGenderSetup = session.player.PlayerData.Gender <= 0 || session.player.PlayerData.ChangeGenderTime <= 0;
            if (!isFirstGenderSetup && session.player.PlayerData.Gender == request.Gender)
            {
                // PlayerGenderIsSame
                session.SendResponse(new ChangePlayerGenderResponse() { Code = 20002021 }, packet.Id);
                return;
            }

            ChangePlayerGenderResponse response = new();
            session.player.PlayerData.Gender = request.Gender;
            session.player.PlayerData.ChangeGenderTime = DateTimeOffset.Now.ToUnixTimeSeconds();
            response.Gender = session.player.PlayerData.Gender;
            response.ChangeGenderTime = session.player.PlayerData.ChangeGenderTime;
            response.NextCanChangeTime = session.player.PlayerData.ChangeGenderTime;
            response.PlayerData = session.player.PlayerData;

            if (isFirstGenderSetup)
            {
                Item rewardItem = session.inventory.Do(Inventory.FreeGem, 50);
                session.SendPush(new NotifyItemDataList()
                {
                    ItemDataList = { rewardItem }
                });
                response.RewardGoodsList.Add(new RewardGoods()
                {
                    RewardType = (int)RewardType.Item,
                    TemplateId = Inventory.FreeGem,
                    Count = 50
                });
                session.inventory.Save();
            }

            session.SendPush(new NotifyPlayerGender()
            {
                Gender = session.player.PlayerData.Gender,
                ChangeGenderTime = session.player.PlayerData.ChangeGenderTime
            });

            session.player.Save();
            session.SendResponse(response, packet.Id);
        }

        [RequestPacketHandler("UpdatePlayerDisplayCharIdRequest")]
        public static void UpdatePlayerDisplayCharIdRequestHandler(Session session, Packet.Request packet)
        {
            UpdatePlayerDisplayCharIdRequest request = MessagePackSerializer.Deserialize<UpdatePlayerDisplayCharIdRequest>(packet.Content);
            if (session.player.PlayerData.DisplayCharIdList.Contains(request.OldCharId))
            {
                session.player.PlayerData.DisplayCharIdList[session.player.PlayerData.DisplayCharIdList.IndexOf(request.OldCharId)] = request.NewCharId;
            }

            session.SendResponse(new UpdatePlayerDisplayCharIdResponse() { DisplayCharIdList = session.player.PlayerData.DisplayCharIdList }, packet.Id);
        }

        [RequestPacketHandler("AddPlayerDisplayCharIdRequest")]
        public static void AddPlayerDisplayCharIdRequestHandler(Session session, Packet.Request packet)
        {
            AddPlayerDisplayCharIdRequest request = MessagePackSerializer.Deserialize<AddPlayerDisplayCharIdRequest>(packet.Content);
            session.player.PlayerData.DisplayCharIdList.Add(request.CharId);

            session.SendResponse(new AddPlayerDisplayCharIdResponse() { DisplayCharIdList = session.player.PlayerData.DisplayCharIdList }, packet.Id);
        }

        [RequestPacketHandler("RemovePlayerDisplayCharIdRequest")]
        public static void RemovePlayerDisplayCharIdRequestHandler(Session session, Packet.Request packet)
        {
            RemovePlayerDisplayCharIdRequest request = MessagePackSerializer.Deserialize<RemovePlayerDisplayCharIdRequest>(packet.Content);
            session.player.PlayerData.DisplayCharIdList.Remove(request.CharId);

            session.SendResponse(new RemovePlayerDisplayCharIdResponse() { DisplayCharIdList = session.player.PlayerData.DisplayCharIdList }, packet.Id);
        }

        [RequestPacketHandler("SetDisplayCharIdFirstRequest")]
        public static void SetDisplayCharIdFirstRequestHandler(Session session, Packet.Request packet)
        {
            SetDisplayCharIdFirstRequest request = MessagePackSerializer.Deserialize<SetDisplayCharIdFirstRequest>(packet.Content);
            session.player.PlayerData.DisplayCharIdList.Remove(request.CharId);
            session.player.PlayerData.DisplayCharIdList.Insert(0, request.CharId);

            session.SendResponse(new SetDisplayCharIdFirstResponse() { DisplayCharIdList = session.player.PlayerData.DisplayCharIdList }, packet.Id);
        }

        // TODO: "Display Preview" button in Details section of account info menu
        [RequestPacketHandler("QueryPlayerDetailRequest")]
        public static void QueryPlayerDetailRequestHandler(Session session, Packet.Request packet)
        {
            session.SendResponse(new QueryPlayerDetailResponse() { Code = 1 }, packet.Id);
        }

        // TODO: "Save" button in Details section of account info menu
        [RequestPacketHandler("SetAppearanceRequest")]
        public static void SetAppearanceRequestHandler(Session session, Packet.Request packet)
        {
            session.SendResponse(new SetAppearanceResponse() { Code = 1 }, packet.Id);
        }

        [RequestPacketHandler("RecordPlayerPointRequest")]
        public static void RecordPlayerPointRequestHandler(Session session, Packet.Request packet)
        {
            _ = MessagePackSerializer.Deserialize<RecordPlayerPointRequest>(packet.Content);
            session.SendResponse(new RecordPlayerPointResponse(), packet.Id);
        }

        [RequestPacketHandler("SyncPlayerKeyPadSettingRequest")]
        public static void SyncPlayerKeyPadSettingRequestHandler(Session session, Packet.Request packet)
        {
            _ = MessagePackSerializer.Deserialize<SyncPlayerKeyPadSettingRequest>(packet.Content);
            session.SendResponse(new SyncPlayerKeyPadSettingResponse(), packet.Id);
        }

        [RequestPacketHandler("RecordPlayerKeyPadSettingRequest")]
        public static void RecordPlayerKeyPadSettingRequestHandler(Session session, Packet.Request packet)
        {
            _ = MessagePackSerializer.Deserialize<RecordPlayerKeyPadSettingRequest>(packet.Content);
            session.SendResponse(new RecordPlayerKeyPadSettingResponse(), packet.Id);
        }
    }
}
