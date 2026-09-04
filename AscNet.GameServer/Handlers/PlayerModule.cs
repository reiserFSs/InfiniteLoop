using AscNet.Common.MsgPack;
using AscNet.Common.Database;
using AscNet.Common.Util;
using AscNet.Table.V2.client.functional;
using AscNet.Table.V2.share.functional;
using AscNet.Table.V2.share.headportrait;
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
    public class NotifyHeadTimeout
    {
        public long CurrHeadPortraitId { get; set; }
        public long CurrHeadFrameId { get; set; }
        public List<long> TimeoutIds { get; set; } = new();
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
        private static readonly Lazy<HashSet<long>> ValidPlayerMarkIds = new(() =>
            TableReaderV2.Parse<FunctionalOpenTable>()
                .Select(row => (long)row.Id)
                .Concat(TableReaderV2.Parse<SkipFunctionalTable>()
                    .Select(row => (long)row.FunctionalId.GetValueOrDefault()))
                .Where(id => id > 0)
                .ToHashSet());

        [RequestPacketHandler("ChangePlayerMarkRequest")]
        public static void ChangePlayerMarkRequestHandler(Session session, Packet.Request packet)
        {
            ChangePlayerMarkRequest request = packet.Deserialize<ChangePlayerMarkRequest>();

            if (!ValidPlayerMarkIds.Value.Contains(request.MaskId))
            {
                session.SendResponse(new ChangePlayerMarkResponse { Code = 1 }, packet.Id);
                return;
            }

            session.player.PlayerData.Marks ??= new();

            if (!session.player.PlayerData.Marks.Contains(request.MaskId))
            {
                session.player.PlayerData.Marks.Add(request.MaskId);
                session.player.Save();
            }
            session.SendResponse(new ChangePlayerMarkResponse(), packet.Id);
        }

        [RequestPacketHandler("ChangeCommunicationRequest")]
        public static void ChangeCommunicationRequestHandler(Session session, Packet.Request packet)
        {
            ChangeCommunicationRequest request = packet.Deserialize<ChangeCommunicationRequest>();
            session.player.PlayerData.Communications.Add(request.Id);

            session.SendResponse(new ChangeCommunicationResponse(), packet.Id);
        }

        [RequestPacketHandler("TouchBoardMutualRequest")]
        public static void TouchBoardMutualRequestHandler(Session session, Packet.Request packet)
        {
            TouchBoardMutualRequest request = packet.Deserialize<TouchBoardMutualRequest>();

            session.SendResponse(new TouchBoardMutualResponse(), packet.Id);
            TaskModule.RecordConditionType(session, 13212);
        }

        [RequestPacketHandler("ChangePlayerNameRequest")]
        public static void ChangePlayerNameRequestHandler(Session session, Packet.Request packet)
        {
            ChangePlayerNameRequest request = packet.Deserialize<ChangePlayerNameRequest>();
            session.player.PlayerData.Name = request.Name;
            session.player.PlayerData.ChangeNameTime = DateTimeOffset.Now.ToUnixTimeSeconds();

            NotifyPlayerName notifyPlayerName = new() { Name = session.player.PlayerData.Name };
            session.SendPush(notifyPlayerName);
            session.SendResponse(new ChangePlayerNameResponse() { NextCanChangeTime = session.player.PlayerData.ChangeNameTime }, packet.Id);
        }

        [RequestPacketHandler("ChangePlayerSignRequest")]
        public static void ChangePlayerSignRequestHandler(Session session, Packet.Request packet)
        {
            ChangePlayerSignRequest request = packet.Deserialize<ChangePlayerSignRequest>();
            session.player.PlayerData.Sign = request.Msg;

            session.SendResponse(new ChangePlayerSignResponse(), packet.Id);
        }

        [RequestPacketHandler("SetHeadPortraitRequest")]
        public static void SetHeadPortraitRequestHandler(Session session, Packet.Request packet)
        {
            SetHeadPortraitRequest request = packet.Deserialize<SetHeadPortraitRequest>();
            const int invalidRequestCode = 20012001;
            if (!CanEquipHead(session, request.Id, type: 1, DateTimeOffset.Now.ToUnixTimeSeconds()))
            {
                session.SendResponse(new SetHeadPortraitResponse { Code = invalidRequestCode }, packet.Id);
                return;
            }

            if (session.player.PlayerData.CurrHeadPortraitId != request.Id)
            {
                session.player.PlayerData.CurrHeadPortraitId = request.Id;
                session.player.Save();
            }
            session.SendResponse(new SetHeadPortraitResponse(), packet.Id);
        }

        [RequestPacketHandler("SetHeadFrameRequest")]
        public static void SetHeadFrameRequestHandler(Session session, Packet.Request packet)
        {
            SetHeadFrameRequest request = packet.Deserialize<SetHeadFrameRequest>();
            const int invalidRequestCode = 20012001;
            if (!CanEquipHead(session, request.Id, type: 2, DateTimeOffset.Now.ToUnixTimeSeconds()))
            {
                session.SendResponse(new SetHeadFrameResponse { Code = invalidRequestCode }, packet.Id);
                return;
            }

            if (session.player.PlayerData.CurrHeadFrameId != request.Id)
            {
                session.player.PlayerData.CurrHeadFrameId = request.Id;
                session.player.Save();
            }
            session.SendResponse(new SetHeadFrameResponse(), packet.Id);
        }

        /// <summary>
        /// A client may equip an owned, table-typed, currently-valid portrait/frame only.
        /// </summary>
        private static bool CanEquipHead(Session session, long id, int type, long nowUnixSeconds)
        {
            if (id <= 0)
                return false;

            HeadPortraitTable? row = TableReaderV2.Parse<HeadPortraitTable>().Find(candidate => candidate.Id == id);
            if (row is null || row.Type != type)
                return false;

            HeadPortraitList? owned = session.player.HeadPortraits.Find(candidate => candidate.Id == id);
            return owned is not null && IsHeadEntryValid(row, owned, nowUnixSeconds);
        }

        /// <summary>
        /// Mirror of XHeadPortraitManager.IsHeadPortraitValid (Forever=0, Duration=1, FixedTime=2).
        /// AscNet's HeadPortrait table has no FixedTime TimeId window, so FixedTime entries resolve as not-valid.
        /// </summary>
        public static bool IsHeadEntryValid(HeadPortraitTable row, HeadPortraitList owned, long nowUnixSeconds)
        {
            if (row is null || owned is null)
                return false;

            return (row.LimitType ?? 0) switch
            {
                0 => true, // Forever
                1 => nowUnixSeconds - owned.BeginTime < (long)owned.LeftCount * row.Duration.GetValueOrDefault(), // Duration, repeatable via LeftCount
                _ => false
            };
        }

        /// <summary>
        /// Login reconciliation hook for AccountModule. Mutates and persists: keeps expired owned
        /// entries in HeadPortraits (matching the retail NotifyLogin oracle) and repairs any expired
        /// equipped portrait/frame to a valid owned non-expired same-type default (or 0 if none).
        /// Returns the exact NotifyHeadTimeout state to push AFTER NotifyLogin; null when nothing is expired.
        /// </summary>
        public static NotifyHeadTimeout? ReconcileHeadTimeouts(Session session, DateTimeOffset now)
        {
            long nowUnixSeconds = now.ToUnixTimeSeconds();
            List<HeadPortraitTable> rows = TableReaderV2.Parse<HeadPortraitTable>();
            List<long> timeoutIds = session.player.HeadPortraits
                .Where(owned => rows.FirstOrDefault(row => row.Id == owned.Id) is { } row
                    && !IsHeadEntryValid(row, owned, nowUnixSeconds))
                .Select(owned => owned.Id)
                .ToList();

            bool changed = false;
            long portraitId = RepairEquippedHeadId(session, rows, type: 1, session.player.PlayerData.CurrHeadPortraitId, nowUnixSeconds, ref changed);
            long frameId = RepairEquippedHeadId(session, rows, type: 2, session.player.PlayerData.CurrHeadFrameId, nowUnixSeconds, ref changed);

            if (changed)
                session.player.Save();

            if (timeoutIds.Count == 0)
                return null;

            return new NotifyHeadTimeout
            {
                CurrHeadPortraitId = portraitId,
                CurrHeadFrameId = frameId,
                TimeoutIds = timeoutIds
            };
        }

        private static long RepairEquippedHeadId(Session session, List<HeadPortraitTable> rows, int type, long currentId, long nowUnixSeconds, ref bool changed)
        {
            if (currentId > 0
                && rows.FirstOrDefault(row => row.Id == currentId && row.Type == type) is { } currentRow
                && session.player.HeadPortraits.FirstOrDefault(owned => owned.Id == currentId) is { } currentOwned
                && IsHeadEntryValid(currentRow, currentOwned, nowUnixSeconds))
            {
                return currentId;
            }

            long repairedId = session.player.HeadPortraits
                .Where(owned => rows.FirstOrDefault(row => row.Id == owned.Id && row.Type == type) is { } row
                    && IsHeadEntryValid(row, owned, nowUnixSeconds))
                .OrderByDescending(owned => rows.First(row => row.Id == owned.Id).Priority)
                .Select(owned => owned.Id)
                .FirstOrDefault();
            if (repairedId != currentId)
            {
                if (type == 1)
                    session.player.PlayerData.CurrHeadPortraitId = repairedId;
                else
                    session.player.PlayerData.CurrHeadFrameId = repairedId;
                changed = true;
            }
            return repairedId;
        }

        [RequestPacketHandler("SetCurrentMedalRequest")]
        public static void SetCurrentMedalRequestHandler(Session session, Packet.Request packet)
        {
            SetCurrentMedalRequest request = packet.Deserialize<SetCurrentMedalRequest>();
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
            SetCurChatBoardRequest request = packet.Deserialize<SetCurChatBoardRequest>();
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
            GetPlayerInfoListRequest request = packet.Deserialize<GetPlayerInfoListRequest>();
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
            ChangePlayerBirthdayRequest request = packet.Deserialize<ChangePlayerBirthdayRequest>();
            session.player.PlayerData.Birthday = request;
            session.SendPush(new NotifyBirthdayPlot() { IsChange = 1 });

            session.SendResponse(new ChangePlayerBirthdayResponse(), packet.Id);
        }

        [RequestPacketHandler("ChangePlayerGenderRequest")]
        public static void ChangePlayerGenderRequestHandler(Session session, Packet.Request packet)
        {
            ChangePlayerGenderRequest request = packet.Deserialize<ChangePlayerGenderRequest>();
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
            UpdatePlayerDisplayCharIdRequest request = packet.Deserialize<UpdatePlayerDisplayCharIdRequest>();
            if (session.player.PlayerData.DisplayCharIdList.Contains(request.OldCharId))
            {
                session.player.PlayerData.DisplayCharIdList[session.player.PlayerData.DisplayCharIdList.IndexOf(request.OldCharId)] = request.NewCharId;
            }

            session.SendResponse(new UpdatePlayerDisplayCharIdResponse() { DisplayCharIdList = session.player.PlayerData.DisplayCharIdList }, packet.Id);
        }

        [RequestPacketHandler("AddPlayerDisplayCharIdRequest")]
        public static void AddPlayerDisplayCharIdRequestHandler(Session session, Packet.Request packet)
        {
            AddPlayerDisplayCharIdRequest request = packet.Deserialize<AddPlayerDisplayCharIdRequest>();
            session.player.PlayerData.DisplayCharIdList.Add(request.CharId);

            session.SendResponse(new AddPlayerDisplayCharIdResponse() { DisplayCharIdList = session.player.PlayerData.DisplayCharIdList }, packet.Id);
        }

        [RequestPacketHandler("RemovePlayerDisplayCharIdRequest")]
        public static void RemovePlayerDisplayCharIdRequestHandler(Session session, Packet.Request packet)
        {
            RemovePlayerDisplayCharIdRequest request = packet.Deserialize<RemovePlayerDisplayCharIdRequest>();
            session.player.PlayerData.DisplayCharIdList.Remove(request.CharId);

            session.SendResponse(new RemovePlayerDisplayCharIdResponse() { DisplayCharIdList = session.player.PlayerData.DisplayCharIdList }, packet.Id);
        }

        [RequestPacketHandler("SetDisplayCharIdFirstRequest")]
        public static void SetDisplayCharIdFirstRequestHandler(Session session, Packet.Request packet)
        {
            SetDisplayCharIdFirstRequest request = packet.Deserialize<SetDisplayCharIdFirstRequest>();
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
            _ = packet.Deserialize<RecordPlayerPointRequest>();
            session.SendResponse(new RecordPlayerPointResponse(), packet.Id);
        }

        [RequestPacketHandler("SyncPlayerKeyPadSettingRequest")]
        public static void SyncPlayerKeyPadSettingRequestHandler(Session session, Packet.Request packet)
        {
            _ = packet.Deserialize<SyncPlayerKeyPadSettingRequest>();
            session.SendResponse(new SyncPlayerKeyPadSettingResponse(), packet.Id);
        }

        [RequestPacketHandler("RecordPlayerKeyPadSettingRequest")]
        public static void RecordPlayerKeyPadSettingRequestHandler(Session session, Packet.Request packet)
        {
            _ = packet.Deserialize<RecordPlayerKeyPadSettingRequest>();
            session.SendResponse(new RecordPlayerKeyPadSettingResponse(), packet.Id);
        }
    }
}
