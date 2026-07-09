using AscNet.Common.Util;
using System.Buffers;
using MessagePack;
using AscNet.Common.MsgPack;
using AscNet.Table.V2.share.reward;
using AscNet.Common.Database;
using Newtonsoft.Json.Linq;

namespace AscNet.GameServer.Handlers
{

    #region MsgPackScheme
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    [MessagePackObject(true)]
    public class DlcQuestUpdateResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class DlcWorldSaveDataRequest
    {
        public int WorldId;
    }

    [MessagePackObject(true)]
    public class DlcWorldSaveDataResponse
    {
        public int Code;
        public object? WorldSaveData;
    }

    [MessagePackObject(true)]
    public class DlcWorldSceneObjectDataResponse
    {
        public int Code;
        public Dictionary<int, object> SceneObjectStates = new();
    }

    [MessagePackObject(true)]
    public class DlcSceneObjectStateSetResponse
    {
        public int Code;
        public List<object> RewardGoods = new();
    }

    [MessagePackObject(true)]
    public class BigWorldEnterWorldRequest
    {
        public int WorldId;
        public int LevelId;
    }

    [MessagePackObject(true)]
    public class BigWorldEnterWorldResponse
    {
        public int Code;
        public object? EnterResultData;
        public object? PlayerData;
        public object? DlcQuestBag;
    }

    [MessagePackObject(true)]
    public class EnterInstLevelRequest
    {
        public int WorldId;
        public int InstLevelId;
        public object? Team;
        public object? TargetPos;
        public object? TargetRot;
    }

    [MessagePackObject(true)]
    public class EnterInstLevelResponse
    {
        public int Code;
        public object? EnterResultData;
    }

    [MessagePackObject(true)]
    public class NotifyNewEnteredBigWorldLevelId
    {
        public int LevelId;
    }

    [MessagePackObject(true)]
    public class BigWorldOnModuleLoadCompleteRequest
    {
    }

    [MessagePackObject(true)]
    public class BigWorldOnModuleLoadCompleteResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class BigWorldSaveFovDataRequest
    {
        public int FovGroupId;
        public int FovType;
    }

    [MessagePackObject(true)]
    public class BigWorldSaveFovDataResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class BigWorldTeamChangeRequest
    {
        public object? ChangeTeam;
    }

    [MessagePackObject(true)]
    public class BigWorldTeamChangeResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class BigWorldCheckIsShowMainRedPointRequest
    {
        public int SysModuleId;
    }

    [MessagePackObject(true)]
    public class BigWorldCheckIsShowMainRedPointResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class BigWorldCourseCoreSetReadRequest
    {
        public int VersionId;
        public List<int>? ElementIds;
    }

    [MessagePackObject(true)]
    public class BigWorldCourseCoreSetReadResponse
    {
        public int Code;
        public List<int> SuccessIds = new();
    }

    [MessagePackObject(true)]
    public class NotifyBigWorldAlbumUpdate
    {
        public object? AlbumData;
    }

    [MessagePackObject(true)]
    public class NotifyBigWorldMapData
    {
        public Dictionary<int, int> BoxRewardedCntData = new();
    }

    [MessagePackObject(true)]
    public class NotifyBigWorldMainRedPoint
    {
        public List<object> RedPoints = new();
    }

    [MessagePackObject(true)]
    public class StartFightNotify
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class XRpcCommonResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class XRpcComponentActionResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class BigWorldCurNpcPosUpdateResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class BigWorldCafeNewRoundResponse
    {
        public int Code;
        public object? CafeGambling;
    }

    [MessagePackObject(true)]
    public class BigWorldCafeNextRoundResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class BigWorldCafeCardGroupListSaveResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class BigWorldCafeGiveUpResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class BigWorldCafeGuideKickOutSceneResponse
    {
        public int Code;
    }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    #endregion

    internal class DlcModule
    {
        private const string BigWorldEnterWorldPayloadPath = "Configs/big_world_enter_world_response.msgpack.b64";
        private const string BigWorldSaveDataPayloadPath = "Configs/big_world_save_data_response.msgpack.b64";
        private const string BigWorldAlbumUpdatePayloadPath = "Configs/big_world_album_update.msgpack.b64";
        private const string BigWorldMapDataPayloadPath = "Configs/big_world_map_data.msgpack.b64";
        private const string BigWorldSgDormDataPayloadPath = "Configs/big_world_sg_dorm_data.msgpack.b64";
        private const string BigWorldLoadCompleteXRpcPushesSnapshotPath = "Configs/big_world_load_complete_xrpc_pushes.json";
        // XRpcCommon push envelope tail verified against retail interaction/load-complete captures.
        private const byte BigWorldXRpcServerControllerId = 1;
        private const byte BigWorldXRpcCommonOpcode = 15;
        private const byte BigWorldXRpcNoLevelId = 0;
        private const int BigWorldXRpcSceneObjectTargetType = 2;
        private const int BigWorldCourseExploreVersionId = 1;
        private const int BigWorldCourseExploreId = 1;
        private const int BigWorldCourseExplorePoiId = 101;
        private static readonly int[] BigWorldCourseProgressTeleporterPlaceIds = [100030, 100027];
        private static readonly Lazy<byte[]> BigWorldEnterWorldPayload = new(() => LoadBase64Payload(BigWorldEnterWorldPayloadPath));
        private static readonly Lazy<byte[]> BigWorldSaveDataPayload = new(() => LoadBase64Payload(BigWorldSaveDataPayloadPath));
        private static readonly Lazy<byte[]> BigWorldAlbumUpdatePayload = new(() => LoadBase64Payload(BigWorldAlbumUpdatePayloadPath));
        private static readonly Lazy<byte[]> BigWorldMapDataPayload = new(() => LoadBase64Payload(BigWorldMapDataPayloadPath));
        private static readonly Lazy<byte[]> BigWorldSgDormDataPayload = new(() => LoadBase64Payload(BigWorldSgDormDataPayloadPath));
        private static readonly Lazy<IReadOnlyDictionary<int, int>> BigWorldInitialBoxRewardedCounts = new(() => ReadBigWorldInitialBoxRewardedCounts(BigWorldMapDataPayload.Value));
        private static readonly Lazy<JObject> BigWorldEnterWorldFixture = new(() => JObject.Parse(MessagePackSerializer.ConvertToJson(BigWorldEnterWorldPayload.Value)));
        private static readonly Lazy<(long PlayerId, string PlayerName)> BigWorldFixturePlayer = new(() => ReadBigWorldFixturePlayer(BigWorldEnterWorldFixture.Value));
        private static readonly Lazy<(int WorldId, int LevelId)> BigWorldFixtureWorld = new(() => ReadBigWorldFixtureWorld(BigWorldEnterWorldFixture.Value));
        private static readonly Lazy<IReadOnlyList<(int PartId, int ColourId)>> BigWorldFixtureCommanderParts = new(() => ReadBigWorldFixtureCommanderParts(BigWorldEnterWorldFixture.Value));
        private static readonly Lazy<(IReadOnlyList<int> EnteredBigWorldIds, int Gender, IReadOnlyList<int> CommanderFashionBags)> BigWorldExternalRequiredPlayerData = new(() => ReadBigWorldExternalRequiredPlayerData(BigWorldEnterWorldFixture.Value));
        private static readonly Lazy<JObject> BigWorldLoadCompleteXRpcPushesSnapshot = new(() => JsonSnapshot.LoadObject(BigWorldLoadCompleteXRpcPushesSnapshotPath));

        [RequestPacketHandler("BigWorldEnterWorldRequest")]
        public static void BigWorldEnterWorldRequestHandler(Session session, Packet.Request packet)
        {
            _ = MessagePackSerializer.Deserialize<BigWorldEnterWorldRequest>(packet.Content);
            ResetBigWorldSelfRuntimeId(session);
            session.SendResponse(nameof(BigWorldEnterWorldResponse), BuildBigWorldEnterWorldPayload(session), packet.Id);
            session.SendPush(nameof(NotifyBigWorldAlbumUpdate), BigWorldAlbumUpdatePayload.Value);
            session.SendPush(nameof(NotifyBigWorldMapData), BuildBigWorldMapDataPayload(session));
            session.SendPush("NotifySgDormData", BigWorldSgDormDataPayload.Value);
            if (ShouldSendBigWorldCourseData(session))
                session.SendPush("NotifyBigWorldCourseData", BuildBigWorldCourseDataPayload(session));
        }

        [RequestPacketHandler("BigWorldOnModuleLoadCompleteRequest")]
        public static void BigWorldOnModuleLoadCompleteRequestHandler(Session session, Packet.Request packet)
        {
            session.SendResponse(new BigWorldOnModuleLoadCompleteResponse(), packet.Id);
        }

        [RequestPacketHandler("BigWorldSaveFovDataRequest")]
        public static void BigWorldSaveFovDataRequestHandler(Session session, Packet.Request packet)
        {
            session.SendResponse(new BigWorldSaveFovDataResponse(), packet.Id);
        }

        [RequestPacketHandler("BigWorldTeamChangeRequest")]
        public static void BigWorldTeamChangeRequestHandler(Session session, Packet.Request packet)
        {
            session.SendResponse(new BigWorldTeamChangeResponse(), packet.Id);
        }

        [RequestPacketHandler("BigWorldCheckIsShowMainRedPointRequest")]
        public static void BigWorldCheckIsShowMainRedPointRequestHandler(Session session, Packet.Request packet)
        {
            session.SendPush(new NotifyBigWorldMainRedPoint());
            session.SendResponse(new BigWorldCheckIsShowMainRedPointResponse(), packet.Id);
        }

        [RequestPacketHandler("BigWorldCourseCoreSetReadRequest")]
        public static void BigWorldCourseCoreSetReadRequestHandler(Session session, Packet.Request packet)
        {
            BigWorldCourseCoreSetReadRequest request = MessagePackSerializer.Deserialize<BigWorldCourseCoreSetReadRequest>(packet.Content);
            bool sendTaskProgress = PersistBigWorldCourseCoreReadElements(session, request);
            session.SendResponse(new BigWorldCourseCoreSetReadResponse
            {
                Code = 0,
                SuccessIds = request.ElementIds ?? []
            }, packet.Id);

            if (sendTaskProgress)
            {
                session.SendPush("NotifyBigWorldCourseTaskCntProgress", MessagePackSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["VersionId"] = request.VersionId,
                    ["TotalProgress"] = GetBigWorldState(session).BigWorldCourseTaskProgress
                }));
            }
        }

        [RequestPacketHandler("DlcQuestUpdateRequest")]
        public static void DlcQuestUpdateRequestHandler(Session session, Packet.Request packet)
        {
            session.SendResponse(new DlcQuestUpdateResponse(), packet.Id);
        }

        [RequestPacketHandler("DlcWorldSaveDataRequest")]
        public static void DlcWorldSaveDataRequestHandler(Session session, Packet.Request packet)
        {
            _ = MessagePackSerializer.Deserialize<DlcWorldSaveDataRequest>(packet.Content);
            session.SendResponse(nameof(DlcWorldSaveDataResponse), BuildBigWorldSaveDataPayload(session), packet.Id);
            session.PendingBigWorldLoadCompleteXRpc = true;
            session.PendingBigWorldStartFightNotify = true;
        }

        [RequestPacketHandler("DlcWorldSceneObjectDataRequest")]
        public static void DlcWorldSceneObjectDataRequestHandler(Session session, Packet.Request packet)
        {
            session.SendResponse(BuildBigWorldSceneObjectDataResponse(session, packet.Content), packet.Id);
        }

        [RequestPacketHandler("DlcSceneObjectStateSetRequest")]
        public static void DlcSceneObjectStateSetRequestHandler(Session session, Packet.Request packet)
        {
            session.SendResponse(new DlcSceneObjectStateSetResponse(), packet.Id);
        }

        [RequestPacketHandler("DlcWorldEnterSucceedRequest")]
        public static void DlcWorldEnterSucceedRequestHandler(Session session, Packet.Request packet)
        {
        }

        [RequestPacketHandler("EnterInstLevelRequest")]
        public static void EnterInstLevelRequestHandler(Session session, Packet.Request packet)
        {
            EnterInstLevelRequest request = MessagePackSerializer.Deserialize<EnterInstLevelRequest>(packet.Content);
            PersistBigWorldEnterInstLevelRequest(session, request);
            session.log.Info($"BigWorld EnterInstLevelRequest WorldId={request.WorldId} InstLevelId={request.InstLevelId}");
            session.SendResponse(nameof(EnterInstLevelResponse), BuildEnterInstLevelResponsePayload(session), packet.Id);

            if (request.InstLevelId > 0)
            {
                session.SendPush(new NotifyNewEnteredBigWorldLevelId
                {
                    LevelId = request.InstLevelId
                });
            }
        }

        [RequestPacketHandler("DlcSingleFightSettleRequest")]
        public static void DlcSingleFightSettleRequestHandler(Session session, Packet.Request packet)
        {
        }

        [RequestPacketHandler("BigWorldCurNpcPosUpdateRequest")]
        public static void BigWorldCurNpcPosUpdateRequestHandler(Session session, Packet.Request packet)
        {
            session.SendResponse(new BigWorldCurNpcPosUpdateResponse(), packet.Id);
        }

        [RequestPacketHandler("BigWorldCafeNewRoundRequest")]
        public static void BigWorldCafeNewRoundRequestHandler(Session session, Packet.Request packet)
        {
            session.SendResponse(new BigWorldCafeNewRoundResponse(), packet.Id);
        }

        [RequestPacketHandler("BigWorldCafeNextRoundRequest")]
        public static void BigWorldCafeNextRoundRequestHandler(Session session, Packet.Request packet)
        {
            session.SendResponse(new BigWorldCafeNextRoundResponse(), packet.Id);
        }

        [RequestPacketHandler("BigWorldCafeCardGroupListSaveRequest")]
        public static void BigWorldCafeCardGroupListSaveRequestHandler(Session session, Packet.Request packet)
        {
            session.SendResponse(new BigWorldCafeCardGroupListSaveResponse(), packet.Id);
        }

        [RequestPacketHandler("BigWorldCafeGiveUpRequest")]
        public static void BigWorldCafeGiveUpRequestHandler(Session session, Packet.Request packet)
        {
            session.SendResponse(new BigWorldCafeGiveUpResponse(), packet.Id);
        }

        [RequestPacketHandler("BigWorldCafeGuideKickOutSceneRequest")]
        public static void BigWorldCafeGuideKickOutSceneRequestHandler(Session session, Packet.Request packet)
        {
            session.SendResponse(new BigWorldCafeGuideKickOutSceneResponse(), packet.Id);
        }

        [RequestPacketHandler("XRpcCommon")]
        public static void XRpcCommonRequestHandler(Session session, Packet.Request packet)
        {
            TryTrackBigWorldCommonRequestState(session, packet.Content);
            TrySendBigWorldInteractNotifies(session, packet.Content);
            session.SendResponse(new XRpcCommonResponse(), packet.Id);
        }

        private static void TrySendBigWorldInteractNotifies(Session session, byte[] payload)
        {
            int sourceUuid;
            int targetUuid;
            int targetPlaceId;
            int targetType;
            int targetLevelId;
            int optionId;
            try
            {
                if (!TryReadBigWorldPlayerInteractRequest(payload, out sourceUuid, out targetUuid, out targetPlaceId, out targetType, out targetLevelId, out optionId))
                    return;
            }
            catch (Exception ex)
            {
                session.log.Error($"Failed to read BigWorld interact request: {ex}");
                return;
            }

            session.log.Info($"BigWorld RpcPlayerInteractRequest SourceUuid={sourceUuid} TargetUuid={targetUuid} PlaceId={targetPlaceId} TargetType={targetType} LevelId={targetLevelId} OptionId={optionId}");
            PersistBigWorldSelfRuntimeId(session, sourceUuid);

            List<RewardGoodsTable> rewardGoodsTables = [];
            List<RewardGoods> rewardGoods = [];
            bool shouldGrantSceneObjectReward = targetType == BigWorldXRpcSceneObjectTargetType
                && !HasClaimedBigWorldSceneObject(session, targetLevelId, targetPlaceId);
            if (shouldGrantSceneObjectReward)
            {
                try
                {
                    rewardGoodsTables = RewardHandler.GetRewardGoods(targetPlaceId);
                    rewardGoods = BuildBigWorldRewardGoods(rewardGoodsTables);
                    if (rewardGoodsTables.Count > 0)
                        PersistClaimedBigWorldSceneObject(session, targetLevelId, targetPlaceId, targetUuid);
                }
                catch (Exception ex)
                {
                    session.log.Error($"Failed to resolve BigWorld interact rewards for placeId {targetPlaceId}: {ex}");
                    rewardGoodsTables = [];
                    rewardGoods = [];
                }
            }

            if (rewardGoodsTables.Count > 0)
            {
                try
                {
                    int boxRewardedCount = GetBigWorldRewardedBoxCount(session, targetLevelId);
                    session.SendPush("NotifyBigWorldBoxData", MessagePackSerializer.Serialize(new Dictionary<string, object?>
                    {
                        ["LevelId"] = targetLevelId,
                        ["BoxRewardedCnt"] = boxRewardedCount
                    }));
                    session.SendPush("NotifyBigWorldCourseExploreProgress", MessagePackSerializer.Serialize(new Dictionary<string, object?>
                    {
                        ["VersionId"] = BigWorldCourseExploreVersionId,
                        ["ExploreId"] = BigWorldCourseExploreId,
                        ["PoiId"] = BigWorldCourseExplorePoiId,
                        ["Count"] = boxRewardedCount
                    }));

                    if (rewardGoods.Count > 0)
                        session.SendPush("XRpcComponentAction", BuildBigWorldXRpcComponentActionPayload(
                            "RpcSceneObjectCollectNotify",
                            SerializeBigWorldSceneObjectCollectArgs(rewardGoods),
                            levelId: targetLevelId,
                            targetUuid: targetUuid));
                }
                catch (Exception ex)
                {
                    session.log.Error($"Failed to send BigWorld reward sidecars for placeId {targetPlaceId}: {ex}");
                }
            }

            try
            {
                session.SendPush("XRpcCommon", BuildBigWorldXRpcPayload(
                    "RpcNpcInteractStartNotify",
                    SerializeBigWorldXRpcIntegerArgs(targetUuid, targetPlaceId, targetType, optionId),
                    levelId: BigWorldXRpcNoLevelId));
                session.SendPush("XRpcCommon", BuildBigWorldXRpcPayload(
                    "RpcNpcInteractFinishNotify",
                    SerializeBigWorldXRpcIntegerArgs(),
                    levelId: BigWorldXRpcNoLevelId));
            }
            catch (Exception ex)
            {
                session.log.Error($"Failed to send BigWorld interact completion notifies for placeId {targetPlaceId}: {ex}");
            }

            if (rewardGoodsTables.Count > 0)
            {
                try
                {
                    RewardHandler.GiveRewards(rewardGoodsTables, session);
                    session.inventory.Save();
                    session.character.Save();
                }
                catch (Exception ex)
                {
                    session.log.Error($"Failed to grant BigWorld interact rewards for placeId {targetPlaceId}: {ex}");
                }
            }
        }

        private static bool TryReadBigWorldPlayerInteractRequest(byte[] payload, out int sourceUuid, out int targetUuid, out int targetPlaceId, out int targetType, out int targetLevelId, out int optionId)
        {
            sourceUuid = 0;
            targetUuid = 0;
            targetPlaceId = 0;
            targetType = 0;
            targetLevelId = 0;
            optionId = 0;

            object?[] rpc = MessagePackSerializer.Deserialize<object?[]>(payload);
            if (rpc.Length < 2 || rpc[0] is not string rpcName || rpcName != "RpcPlayerInteractRequest" || rpc[1] is not byte[] argsPayload)
                return false;

            object?[] args = MessagePackSerializer.Deserialize<object?[]>(argsPayload);
            if (args.Length < 8)
                return false;

            sourceUuid = ReadBigWorldXRpcInt32(args[2]);
            targetUuid = ReadBigWorldXRpcInt32(args[3]);
            targetPlaceId = ReadBigWorldXRpcInt32(args[4]);
            targetType = ReadBigWorldXRpcInt32(args[5]);
            targetLevelId = ReadBigWorldXRpcInt32(args[6]);
            optionId = ReadBigWorldXRpcInt32(args[7]);
            return true;
        }

        private static int ReadBigWorldXRpcInt32(object? value)
        {
            return value switch
            {
                byte typed => typed,
                sbyte typed => typed,
                short typed => typed,
                ushort typed => typed,
                int typed => typed,
                uint typed when typed <= int.MaxValue => (int)typed,
                long typed when typed >= int.MinValue && typed <= int.MaxValue => (int)typed,
                ulong typed when typed <= int.MaxValue => (int)typed,
                _ => throw new MessagePackSerializationException($"Expected BigWorld XRpc int32-compatible value, got {value?.GetType().FullName ?? "null"}.")
            };
        }

        private static byte[] BuildBigWorldXRpcPayload(string rpcName, byte[] argsPayload, byte levelId)
        {
            ArrayBufferWriter<byte> buffer = new();
            MessagePackWriter writer = new(buffer);
            writer.WriteArrayHeader(5);
            writer.Write(rpcName);
            writer.Write(argsPayload);
            writer.Write(BigWorldXRpcServerControllerId);
            writer.Write(BigWorldXRpcCommonOpcode);
            writer.Write(levelId);
            writer.Flush();
            return buffer.WrittenMemory.ToArray();
        }

        private static byte[] SerializeBigWorldXRpcIntegerArgs(params int[] args)
        {
            ArrayBufferWriter<byte> buffer = new();
            MessagePackWriter writer = new(buffer);
            writer.WriteArrayHeader(args.Length);
            foreach (int arg in args)
                WriteBigWorldXRpcInteger(ref writer, arg);

            writer.Flush();
            return buffer.WrittenMemory.ToArray();
        }

        private static void WriteBigWorldXRpcInteger(ref MessagePackWriter writer, int value)
        {
            if (value < 0)
            {
                writer.Write(value);
                return;
            }

            if (value <= byte.MaxValue)
            {
                writer.Write((byte)value);
                return;
            }

            if (value <= ushort.MaxValue)
            {
                writer.Write((ushort)value);
                return;
            }

            writer.Write((uint)value);
        }

        private static byte[] BuildBigWorldXRpcComponentActionPayload(string rpcName, byte[] argsPayload, int levelId, int targetUuid)
        {
            ArrayBufferWriter<byte> buffer = new();
            MessagePackWriter writer = new(buffer);
            writer.WriteArrayHeader(6);
            writer.Write(rpcName);
            writer.Write(argsPayload);
            writer.Write((byte)0);
            writer.Write(BigWorldXRpcCommonOpcode);
            WriteBigWorldXRpcInteger(ref writer, levelId);
            WriteBigWorldXRpcInteger(ref writer, targetUuid);
            writer.Flush();
            return buffer.WrittenMemory.ToArray();
        }

        private static byte[] SerializeBigWorldSceneObjectCollectArgs(IReadOnlyList<RewardGoods> rewardGoods)
        {
            return MessagePackSerializer.Serialize(new object?[] { rewardGoods.Select(ToBigWorldRewardGoodsPayload).ToArray() });
        }

        private static Dictionary<string, object?> ToBigWorldRewardGoodsPayload(RewardGoods reward)
        {
            return new Dictionary<string, object?>
            {
                ["RewardType"] = reward.RewardType,
                ["TemplateId"] = reward.TemplateId,
                ["Count"] = reward.Count,
                ["Level"] = reward.Level,
                ["Quality"] = reward.Quality,
                ["Grade"] = reward.Grade,
                ["Breakthrough"] = reward.Breakthrough,
                ["ConvertFrom"] = reward.ConvertFrom,
                ["IsGift"] = reward.IsGift,
                ["RewardMulti"] = reward.RewardMulti,
                ["Id"] = reward.Id
            };
        }

        private static List<RewardGoods> BuildBigWorldRewardGoods(IReadOnlyList<RewardGoodsTable> rewardGoodsTables)
        {
            List<RewardGoods> rewardGoods = [];
            for (int index = 0; index < rewardGoodsTables.Count; index++)
            {
                RewardGoodsTable rewardGoodsTable = rewardGoodsTables[index];
                RewardType? rewardType = RewardHandler.GetRewardType(rewardGoodsTable);
                if (rewardType is null)
                    continue;

                rewardGoods.Add(new RewardGoods
                {
                    RewardType = (int)rewardType.Value,
                    TemplateId = rewardGoodsTable.TemplateId,
                    Count = rewardGoodsTable.Count,
                    Id = 70_000_000 + index
                });
            }

            return rewardGoods;
        }

        private static int GetBigWorldRewardedBoxCount(Session session, int levelId)
        {
            try
            {
                BigWorldInitialBoxRewardedCounts.Value.TryGetValue(levelId, out int initialCount);
                int collectedInLevel = GetClaimedBigWorldSceneObjects(session)
                    .Where(sceneObject => sceneObject.LevelId == levelId)
                    .Select(sceneObject => sceneObject.PlaceId)
                    .Distinct()
                    .Count();
                return initialCount + collectedInLevel;
            }
            catch (Exception ex)
            {
                session.log.Error($"Failed to read BigWorld rewarded box count for levelId {levelId}: {ex}");
                return GetClaimedBigWorldSceneObjects(session)
                    .Where(sceneObject => sceneObject.LevelId == levelId)
                    .Select(sceneObject => sceneObject.PlaceId)
                    .Distinct()
                    .Count();
            }
        }

        [RequestPacketHandler("XRpcComponentAction")]
        public static void XRpcComponentActionRequestHandler(Session session, Packet.Request packet)
        {
            TryPersistBigWorldNpcPosition(session, packet.Content);
            session.SendResponse(new XRpcComponentActionResponse(), packet.Id);
        }

        private static byte[] LoadBase64Payload(string relativePath)
        {
            string payload = JsonSnapshot.LoadText(relativePath).Trim();
            if (string.IsNullOrWhiteSpace(payload))
                throw new FileNotFoundException($"Required BigWorld fixture is missing or empty: {relativePath}");

            return Convert.FromBase64String(payload);
        }

        private static (long PlayerId, string PlayerName) ReadBigWorldFixturePlayer(JObject response)
        {
            JArray players = RequiredBigWorldFixtureArray(response["EnterResultData"]?["WorldData"]?["Players"], "EnterResultData.WorldData.Players");
            JObject firstPlayer = players.First as JObject
                ?? throw new InvalidDataException("BigWorld enter-world fixture is missing EnterResultData.WorldData.Players[0].");
            long playerId = RequiredBigWorldFixtureLong(firstPlayer, "Id", "EnterResultData.WorldData.Players[0]");
            string playerName = RequiredBigWorldFixtureString(firstPlayer, "Name", "EnterResultData.WorldData.Players[0]");
            return (playerId, playerName);
        }

        private static (int WorldId, int LevelId) ReadBigWorldFixtureWorld(JObject response)
        {
            JObject worldData = RequiredBigWorldFixtureObject(response["EnterResultData"]?["WorldData"], "EnterResultData.WorldData");
            return (
                RequiredBigWorldFixtureInt(worldData, "WorldId", "EnterResultData.WorldData"),
                RequiredBigWorldFixtureInt(worldData, "LevelId", "EnterResultData.WorldData"));
        }

        private static IReadOnlyDictionary<int, int> ReadBigWorldInitialBoxRewardedCounts(byte[] mapDataPayload)
        {
            JObject mapData = JObject.Parse(MessagePackSerializer.ConvertToJson(mapDataPayload));
            if (mapData["BoxRewardedCntData"] is not JObject boxRewardedCntData)
                return new Dictionary<int, int>();

            Dictionary<int, int> counts = new();
            foreach (JProperty property in boxRewardedCntData.Properties())
            {
                if (int.TryParse(property.Name, out int levelId) && property.Value.Type == JTokenType.Integer)
                    counts[levelId] = property.Value.Value<int>();
            }

            return counts;
        }

        private static IReadOnlyList<(int PartId, int ColourId)> ReadBigWorldFixtureCommanderParts(JObject response)
        {
            JObject playerData = RequiredBigWorldFixtureObject(response["PlayerData"], "PlayerData");
            int outfitType = RequiredBigWorldFixtureInt(playerData, "CurCommanderOutfitType", "PlayerData");
            JObject outfits = RequiredBigWorldFixtureObject(playerData["CommanderFashionOutfits"], "PlayerData.CommanderFashionOutfits");
            JObject outfit = RequiredBigWorldFixtureObject(outfits[outfitType.ToString()], $"PlayerData.CommanderFashionOutfits[{outfitType}]");
            JObject wearFashionDict = RequiredBigWorldFixtureObject(outfit["WearFashionDict"], $"PlayerData.CommanderFashionOutfits[{outfitType}].WearFashionDict");

            List<(int Order, int PartId, int ColourId)> parts = new(wearFashionDict.Count);
            foreach (JProperty property in wearFashionDict.Properties())
            {
                int order = int.TryParse(property.Name, out int parsedOrder) ? parsedOrder : int.MaxValue;
                JObject part = RequiredBigWorldFixtureObject(property.Value, $"PlayerData.CommanderFashionOutfits[{outfitType}].WearFashionDict[{property.Name}]");
                parts.Add((
                    order,
                    RequiredBigWorldFixtureInt(part, "PartId", $"PlayerData.CommanderFashionOutfits[{outfitType}].WearFashionDict[{property.Name}]"),
                    RequiredBigWorldFixtureInt(part, "ColourId", $"PlayerData.CommanderFashionOutfits[{outfitType}].WearFashionDict[{property.Name}]")));
            }

            if (parts.Count == 0)
                throw new InvalidDataException($"BigWorld enter-world fixture is missing PlayerData.CommanderFashionOutfits[{outfitType}].WearFashionDict parts.");

            return parts
                .OrderBy(part => part.Order)
                .Select(part => (part.PartId, part.ColourId))
                .ToArray();
        }

        private static (IReadOnlyList<int> EnteredBigWorldIds, int Gender, IReadOnlyList<int> CommanderFashionBags) ReadBigWorldExternalRequiredPlayerData(JObject response)
        {
            JObject playerData = RequiredBigWorldFixtureObject(response["PlayerData"], "PlayerData");
            JArray commanderFashionBags = RequiredBigWorldFixtureArray(playerData["CommanderFashionBags"], "PlayerData.CommanderFashionBags");
            return (
                Array.Empty<int>(),
                RequiredBigWorldFixtureInt(playerData, "Gender", "PlayerData"),
                ReadBigWorldFixtureIntArray(commanderFashionBags, "PlayerData.CommanderFashionBags"));
        }

        internal static NotifyExternalRequiredBigWorldPlayerData BuildExternalRequiredBigWorldPlayerData()
        {
            (IReadOnlyList<int> enteredBigWorldIds, int gender, IReadOnlyList<int> commanderFashionBags) = BigWorldExternalRequiredPlayerData.Value;
            return new NotifyExternalRequiredBigWorldPlayerData
            {
                EnteredBigWorldIds = enteredBigWorldIds.ToList(),
                Gender = gender,
                CommanderFashionBags = commanderFashionBags.ToList()
            };
        }

        private static JObject RequiredBigWorldFixtureObject(JToken? token, string path)
        {
            return token as JObject
                ?? throw new InvalidDataException($"BigWorld enter-world fixture is missing object {path}.");
        }

        private static JArray RequiredBigWorldFixtureArray(JToken? token, string path)
        {
            return token as JArray
                ?? throw new InvalidDataException($"BigWorld enter-world fixture is missing array {path}.");
        }

        private static int RequiredBigWorldFixtureInt(JObject source, string name, string path)
        {
            return source.Value<int?>(name)
                ?? throw new InvalidDataException($"BigWorld enter-world fixture is missing integer {path}.{name}.");
        }

        private static long RequiredBigWorldFixtureLong(JObject source, string name, string path)
        {
            return source.Value<long?>(name)
                ?? throw new InvalidDataException($"BigWorld enter-world fixture is missing integer {path}.{name}.");
        }

        private static string RequiredBigWorldFixtureString(JObject source, string name, string path)
        {
            return source.Value<string>(name)
                ?? throw new InvalidDataException($"BigWorld enter-world fixture is missing string {path}.{name}.");
        }

        private static IReadOnlyList<int> ReadBigWorldFixtureIntArray(JArray array, string path)
        {
            int[] values = new int[array.Count];
            for (int index = 0; index < array.Count; index++)
            {
                values[index] = array[index]?.Value<int?>()
                    ?? throw new InvalidDataException($"BigWorld enter-world fixture is missing integer {path}[{index}].");
            }

            return values;
        }

        private static byte[] BuildBigWorldEnterWorldPayload(Session session)
        {
            byte[] payload = BigWorldEnterWorldPayload.Value;
            try
            {
                return PatchBigWorldEnterWorldPayload(session, payload);
            }
            catch (Exception ex)
            {
                session.log.Warn($"Falling back to unpatched BigWorldEnterWorldResponse payload: {ex.Message}");
                return payload;
            }
        }

        private static byte[] PatchBigWorldEnterWorldPayload(Session session, byte[] payload)
        {
            MessagePackReader reader = new(new ReadOnlySequence<byte>(payload));
            ArrayBufferWriter<byte> buffer = new(payload.Length + 64);
            MessagePackWriter writer = new(buffer);
            List<string> path = new(8);
            RewriteBigWorldEnterWorldValue(session, payload, ref reader, ref writer, path, false);
            if (reader.Consumed != payload.Length)
                throw new MessagePackSerializationException("BigWorld enter-world fixture contains trailing MessagePack data.");
            writer.Flush();
            return buffer.WrittenMemory.ToArray();
        }

        private static byte[] PatchBigWorldNestedNativePayload(Session session, byte[] payload)
        {
            MessagePackReader reader = new(new ReadOnlySequence<byte>(payload));
            ArrayBufferWriter<byte> buffer = new(payload.Length + 64);
            MessagePackWriter writer = new(buffer);
            List<string> path = new(8);
            RewriteBigWorldEnterWorldValue(session, payload, ref reader, ref writer, path, true);
            writer.Flush();
            int consumed = checked((int)reader.Consumed);
            if (consumed < payload.Length)
            {
                ReadOnlySpan<byte> trailingPayload = payload.AsSpan(consumed);
                trailingPayload.CopyTo(buffer.GetSpan(trailingPayload.Length));
                buffer.Advance(trailingPayload.Length);
            }

            return buffer.WrittenMemory.ToArray();
        }

        private static bool TryPatchBigWorldNestedNativePayload(Session session, byte[] payload, out byte[] patchedPayload)
        {
            try
            {
                patchedPayload = PatchBigWorldNestedNativePayload(session, payload);
                return true;
            }
            catch
            {
                patchedPayload = payload;
                return false;
            }
        }

        private static void RewriteBigWorldEnterWorldValue(Session session, byte[] payload, ref MessagePackReader reader, ref MessagePackWriter writer, List<string> path, bool patchFixtureIdentityLiterals)
        {
            if (patchFixtureIdentityLiterals
                && reader.NextMessagePackType == MessagePackType.Array
                && TryWriteBigWorldNativeSceneObjectRow(session, payload, ref reader, ref writer))
            {
                return;
            }

            if (IsBigWorldCommanderPartListPath(path)
                && reader.NextMessagePackType == MessagePackType.Array
                && TryWriteBigWorldCommanderPartList(payload, ref reader, ref writer))
            {
                return;
            }

            if (TryWriteBigWorldNativeLevelIdReplacement(session, ref reader, ref writer, path))
                return;

            if (TryWriteBigWorldEnterWorldReplacement(session, ref writer, path))
            {
                reader.Skip();
                return;
            }

            if (IsBigWorldNestedNativePayloadPath(path) && reader.NextMessagePackType == MessagePackType.Binary)
            {
                ReadOnlySequence<byte>? nestedPayload = reader.ReadBytes();
                writer.Write(PatchBigWorldNestedNativePayload(session, nestedPayload?.ToArray() ?? []));
                return;
            }

            if (patchFixtureIdentityLiterals && reader.NextMessagePackType == MessagePackType.Binary)
            {
                ReadOnlySequence<byte>? nestedPayload = reader.ReadBytes();
                byte[] nestedPayloadBytes = nestedPayload?.ToArray() ?? [];
                writer.Write(TryPatchBigWorldNestedNativePayload(session, nestedPayloadBytes, out byte[] patchedPayload)
                    ? patchedPayload
                    : nestedPayloadBytes);
                return;
            }

            if (patchFixtureIdentityLiterals && TryWriteBigWorldFixtureIdentityReplacement(session, ref reader, ref writer))
                return;

            switch (reader.NextMessagePackType)
            {
                case MessagePackType.Map:
                    int mapCount = reader.ReadMapHeader();
                    writer.WriteMapHeader(mapCount);
                    for (int index = 0; index < mapCount; index++)
                    {
                        string? key = TryReadStringKey(payload, ref reader, ref writer);
                        path.Add(key ?? string.Empty);
                        RewriteBigWorldEnterWorldValue(session, payload, ref reader, ref writer, path, patchFixtureIdentityLiterals);
                        path.RemoveAt(path.Count - 1);
                    }
                    break;
                case MessagePackType.Array:
                    int arrayCount = reader.ReadArrayHeader();
                    writer.WriteArrayHeader(arrayCount);
                    for (int index = 0; index < arrayCount; index++)
                    {
                        path.Add(index.ToString());
                        RewriteBigWorldEnterWorldValue(session, payload, ref reader, ref writer, path, patchFixtureIdentityLiterals);
                        path.RemoveAt(path.Count - 1);
                    }
                    break;
                default:
                    CopyRawMessagePackValue(payload, ref reader, ref writer);
                    break;
            }
        }

        private static string? TryReadStringKey(byte[] payload, ref MessagePackReader reader, ref MessagePackWriter writer)
        {
            if (reader.NextMessagePackType != MessagePackType.String)
            {
                CopyRawMessagePackValue(payload, ref reader, ref writer);
                return null;
            }

            string? key = reader.ReadString();
            writer.Write(key);
            return key;
        }

        private static void CopyRawMessagePackValue(byte[] payload, ref MessagePackReader reader, ref MessagePackWriter writer)
        {
            int start = checked((int)reader.Consumed);
            reader.Skip();
            int length = checked((int)reader.Consumed) - start;
            writer.WriteRaw(payload.AsSpan(start, length));
        }

        private static bool IsBigWorldNestedNativePayloadPath(List<string> path)
        {
            return path.Count == 2
                && path[0] == "EnterResultData"
                && (path[1] == "FightData" || path[1] == "LevelData");
        }

        private static bool TryWriteBigWorldFixtureIdentityReplacement(Session session, ref MessagePackReader reader, ref MessagePackWriter writer)
        {
            try
            {
                (long fixturePlayerId, string fixturePlayerName) = BigWorldFixturePlayer.Value;
                if (reader.NextMessagePackType == MessagePackType.Integer)
                {
                    MessagePackReader probe = reader;
                    long value = probe.ReadInt64();
                    if (value == fixturePlayerId)
                    {
                        reader = probe;
                        WriteBigWorldInteger(ref writer, session.player.PlayerData.Id);
                        return true;
                    }
                }

                if (reader.NextMessagePackType == MessagePackType.String)
                {
                    MessagePackReader probe = reader;
                    string? value = probe.ReadString();
                    if (value == fixturePlayerName)
                    {
                        reader = probe;
                        writer.Write(session.player.PlayerData.Name ?? string.Empty);
                        return true;
                    }
                }
            }
            catch (OverflowException)
            {
            }
            catch (MessagePackSerializationException)
            {
            }
            catch (InvalidOperationException)
            {
            }

            return false;
        }

        private static bool TryWriteBigWorldEnterWorldReplacement(Session session, ref MessagePackWriter writer, List<string> path)
        {
            (int worldId, int levelId) = GetEffectiveBigWorldLocation(session);
            BigWorldPlayerState state = GetBigWorldState(session);

            if (IsBigWorldPlayerPath(path, "Id"))
            {
                WriteBigWorldInteger(ref writer, session.player.PlayerData.Id);
                return true;
            }

            if (IsBigWorldPlayerPath(path, "Name"))
            {
                writer.Write(session.player.PlayerData.Name ?? string.Empty);
                return true;
            }

            if (IsBigWorldPlayerPath(path, "HeadPortraitId"))
            {
                writer.Write((int)session.player.PlayerData.CurrHeadPortraitId);
                return true;
            }

            if (IsBigWorldPlayerPath(path, "HeadFrameId"))
            {
                writer.Write((int)session.player.PlayerData.CurrHeadFrameId);
                return true;
            }

            if (IsBigWorldWorldDataPath(path, "WorldId")
                || IsBigWorldBornDataPath(path, "LastWorldId")
                || IsBigWorldPlayerDataPath(path, "LastWorldId")
                || PathEndsWith(path, "WorldData", "WorldId"))
            {
                writer.Write(worldId);
                return true;
            }

            if (IsBigWorldWorldDataPath(path, "LevelId")
                || IsBigWorldBornDataPath(path, "LastLevelId")
                || IsBigWorldPlayerDataPath(path, "LastLevelId")
                || PathEndsWith(path, "WorldData", "LevelId"))
            {
                writer.Write(levelId);
                return true;
            }

            if (IsBigWorldPlayerDataPath(path, "TeleporterData")
                && state.BigWorldCourseTaskProgress >= 2)
            {
                WriteBigWorldTeleporterData(ref writer, levelId);
                return true;
            }

            if (IsBigWorldBornDataPath(path, "Position")
                && TryGetSupportedBigWorldLastPosition(state, out BigWorldVector3? position))
            {
                WriteBigWorldVector3(ref writer, position!);
                return true;
            }

            if (IsBigWorldBornDataPath(path, "EulerAngles")
                && TryGetSupportedBigWorldRotationY(state, out double rotationY))
            {
                WriteBigWorldEulerAngles(ref writer, rotationY);
                return true;
            }

            return false;
        }

        private static bool TryWriteBigWorldNativeLevelIdReplacement(Session session, ref MessagePackReader reader, ref MessagePackWriter writer, List<string> path)
        {
            if (!IsBigWorldNativeLevelIdPath(path))
                return false;

            (_, int fixtureLevelId) = BigWorldFixtureWorld.Value;
            (_, int levelId) = GetEffectiveBigWorldLocation(session);
            if (levelId == fixtureLevelId || reader.NextMessagePackType != MessagePackType.Integer)
                return false;

            MessagePackReader probe = reader;
            long value = probe.ReadInt64();
            if (value != fixtureLevelId)
                return false;

            reader = probe;
            writer.Write(levelId);
            return true;

        }

        private static bool IsBigWorldPlayerPath(List<string> path, string leaf)
        {
            return path.Count == 5
                && path[0] == "EnterResultData"
                && path[1] == "WorldData"
                && path[2] == "Players"
                && path[3] == "0"
                && path[4] == leaf;
        }

        private static bool IsBigWorldBornDataPath(List<string> path, string leaf)
        {
            return path.Count == 6
                && path[0] == "EnterResultData"
                && path[1] == "WorldData"
                && path[2] == "Players"
                && path[3] == "0"
                && path[4] == "BornData"
                && path[5] == leaf;
        }

        private static bool IsBigWorldPlayerDataPath(List<string> path, string leaf)
        {
            return path.Count == 2
                && path[0] == "PlayerData"
                && path[1] == leaf;
        }

        private static bool IsBigWorldWorldDataPath(List<string> path, string leaf)
        {
            return path.Count == 3
                && path[0] == "EnterResultData"
                && path[1] == "WorldData"
                && path[2] == leaf;
        }

        private static bool PathEndsWith(List<string> path, params string[] suffix)
        {
            if (path.Count < suffix.Length)
                return false;

            for (int index = 0; index < suffix.Length; index++)
            {
                if (path[path.Count - suffix.Length + index] != suffix[index])
                    return false;
            }

            return true;
        }

        private static bool IsBigWorldNativeLevelIdPath(List<string> path)
        {
            return (path.Count == 1 && path[0] == "0")
                || (path.Count == 3 && path[0] == "1" && path[2] == "0")
                || (path.Count == 4 && path[0] == "2" && path[1] == "0" && path[3] == "4")
                || (path.Count >= 2 && path[path.Count - 2] == "WorldData" && path[path.Count - 1] == "LevelId");
        }

        private static void WriteBigWorldVector3(ref MessagePackWriter writer, BigWorldVector3 value)
        {
            writer.WriteMapHeader(3);
            writer.Write("X");
            WriteBigWorldFloat(ref writer, value.X);
            writer.Write("Y");
            WriteBigWorldFloat(ref writer, value.Y);
            writer.Write("Z");
            WriteBigWorldFloat(ref writer, value.Z);
        }

        private static void WriteBigWorldFloat(ref MessagePackWriter writer, double value)
        {
            writer.Write((float)value);
        }

        private static void WriteBigWorldEulerAngles(ref MessagePackWriter writer, double rotationY)
        {
            writer.WriteMapHeader(3);
            writer.Write("X");
            WriteBigWorldFloat(ref writer, 0D);
            writer.Write("Y");
            WriteBigWorldFloat(ref writer, rotationY);
            writer.Write("Z");
            WriteBigWorldFloat(ref writer, 0D);
        }

        private static void WriteBigWorldTeleporterData(ref MessagePackWriter writer, int levelId)
        {
            writer.WriteMapHeader(1);
            writer.Write(levelId);
            writer.WriteArrayHeader(2 + BigWorldCourseProgressTeleporterPlaceIds.Length);
            writer.Write(100122);
            writer.Write(100121);
            foreach (int placeId in BigWorldCourseProgressTeleporterPlaceIds)
                writer.Write(placeId);
        }

        private static void WriteBigWorldInteger(ref MessagePackWriter writer, long value)
        {
            if (value >= int.MinValue && value <= int.MaxValue)
                writer.Write((int)value);
            else
                writer.Write(value);
        }

        private static BigWorldPlayerState GetBigWorldState(Session session)
        {
            if (session.player.BigWorldState is null)
                session.player.BigWorldState = new BigWorldPlayerState();

            session.player.BigWorldState.ClaimedSceneObjects ??= [];
            session.player.BigWorldState.BigWorldCourseReadElementIds ??= [];
            return session.player.BigWorldState;
        }

        private static (int WorldId, int LevelId) GetEffectiveBigWorldLocation(Session session)
        {
            return BigWorldFixtureWorld.Value;
        }

        private static bool IsSupportedBigWorldLocation(BigWorldPlayerState state)
        {
            (int fixtureWorldId, int fixtureLevelId) = BigWorldFixtureWorld.Value;
            return (state.LastWorldId <= 0 || state.LastWorldId == fixtureWorldId)
                && state.LastLevelId == fixtureLevelId;
        }

        private static bool TryGetSupportedBigWorldLastPosition(BigWorldPlayerState state, out BigWorldVector3? position)
        {
            if (IsSupportedBigWorldLocation(state) && state.LastPosition is { } lastPosition)
            {
                position = lastPosition;
                return true;
            }

            position = null;
            return false;
        }

        private static bool TryGetSupportedBigWorldRotationY(BigWorldPlayerState state, out double rotationY)
        {
            if (IsSupportedBigWorldLocation(state) && GetBigWorldRotationY(state) is double lastRotationY)
            {
                rotationY = lastRotationY;
                return true;
            }

            rotationY = 0D;
            return false;
        }

        private static bool HasBigWorldStatePatches(Session session)
        {
            BigWorldPlayerState state = GetBigWorldState(session);
            return (IsSupportedBigWorldLocation(state)
                    && (state.LastPosition is not null || state.LastRotationY is not null))
                || state.ClaimedSceneObjects.Count > 0;
        }

        private static List<BigWorldClaimedSceneObject> GetClaimedBigWorldSceneObjects(Session session)
        {
            return GetBigWorldState(session).ClaimedSceneObjects;
        }

        private static bool HasClaimedBigWorldSceneObject(Session session, int levelId, int placeId)
        {
            return GetClaimedBigWorldSceneObjects(session)
                .Any(sceneObject => sceneObject.LevelId == levelId && sceneObject.PlaceId == placeId);
        }

        private static void PersistBigWorldSelfRuntimeId(Session session, int sourceUuid)
        {
            if (sourceUuid <= 0)
                return;

            BigWorldPlayerState state = GetBigWorldState(session);
            if (state.LastSelfRuntimeId == sourceUuid)
                return;

            state.LastSelfRuntimeId = sourceUuid;
            session.player.Save();
        }

        private static void ResetBigWorldSelfRuntimeId(Session session)
        {
            BigWorldPlayerState state = GetBigWorldState(session);
            if (state.LastSelfRuntimeId == 0)
                return;

            state.LastSelfRuntimeId = 0;
            session.player.Save();
        }

        private static void PersistClaimedBigWorldSceneObject(Session session, int levelId, int placeId, int uuid)
        {
            BigWorldPlayerState state = GetBigWorldState(session);
            if (state.ClaimedSceneObjects.Any(sceneObject => sceneObject.LevelId == levelId && sceneObject.PlaceId == placeId))
                return;

            state.ClaimedSceneObjects.Add(new BigWorldClaimedSceneObject
            {
                LevelId = levelId,
                PlaceId = placeId,
                Uuid = uuid,
                ClaimedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });
            if (state.LastLevelId <= 0)
                state.LastLevelId = levelId;
            if (state.LastWorldId <= 0)
                state.LastWorldId = BigWorldFixtureWorld.Value.WorldId;

            session.player.Save();
        }

        private static bool PersistBigWorldCourseCoreReadElements(Session session, BigWorldCourseCoreSetReadRequest request)
        {
            BigWorldPlayerState state = GetBigWorldState(session);
            bool changed = false;
            foreach (int elementId in request.ElementIds ?? [])
            {
                if (elementId <= 0 || state.BigWorldCourseReadElementIds.Contains(elementId))
                    continue;

                state.BigWorldCourseReadElementIds.Add(elementId);
                changed = true;
            }

            bool sendTaskProgress = false;
            if (request.VersionId == BigWorldCourseExploreVersionId
                && state.BigWorldCourseReadElementIds.Count >= 2
                && state.BigWorldCourseTaskProgress < 2)
            {
                state.BigWorldCourseTaskProgress = 2;
                changed = true;
                sendTaskProgress = true;
            }

            if (changed)
                session.player.Save();

            return sendTaskProgress;
        }

        private static byte[] BuildBigWorldMapDataPayload(Session session)
        {
            if (GetClaimedBigWorldSceneObjects(session).Count == 0)
                return BigWorldMapDataPayload.Value;

            Dictionary<int, int> counts = BigWorldInitialBoxRewardedCounts.Value.ToDictionary(pair => pair.Key, pair => pair.Value);
            foreach (IGrouping<int, BigWorldClaimedSceneObject> levelGroup in GetClaimedBigWorldSceneObjects(session).GroupBy(sceneObject => sceneObject.LevelId))
            {
                BigWorldInitialBoxRewardedCounts.Value.TryGetValue(levelGroup.Key, out int initialCount);
                counts[levelGroup.Key] = initialCount + levelGroup.Select(sceneObject => sceneObject.PlaceId).Distinct().Count();
            }

            return MessagePackSerializer.Serialize(new NotifyBigWorldMapData
            {
                BoxRewardedCntData = counts
            });
        }

        private static bool ShouldSendBigWorldCourseData(Session session)
        {
            BigWorldPlayerState state = GetBigWorldState(session);
            return state.BigWorldCourseReadElementIds.Count > 0
                || state.BigWorldCourseTaskProgress > 0;
        }

        private static byte[] BuildBigWorldCourseDataPayload(Session session)
        {
            (_, int levelId) = BigWorldFixtureWorld.Value;
            int boxRewardedCount = GetBigWorldRewardedBoxCount(session, levelId);
            BigWorldPlayerState state = GetBigWorldState(session);

            return MessagePackSerializer.Serialize(new Dictionary<string, object?>
            {
                ["Data"] = new Dictionary<string, object?>
                {
                    ["Datas"] = new Dictionary<int, object?>
                    {
                        [1] = BuildBigWorldCourseVersionData(1, boxRewardedCount, state),
                        [2] = BuildEmptyBigWorldCourseVersionData(2),
                        [3] = BuildEmptyBigWorldCourseVersionData(3)
                    }
                }
            });
        }

        private static Dictionary<string, object?> BuildBigWorldCourseVersionData(int versionId, int boxRewardedCount, BigWorldPlayerState state)
        {
            return new Dictionary<string, object?>
            {
                ["VersionId"] = versionId,
                ["TaskCntData"] = new Dictionary<string, object?>
                {
                    ["ContentId"] = versionId * 100 + 1,
                    ["TotalProgress"] = state.BigWorldCourseTaskProgress,
                    ["GotRewardIds"] = Array.Empty<int>()
                },
                ["ExploreCntData"] = new Dictionary<string, object?>
                {
                    ["ContentId"] = versionId * 100 + 2,
                    ["IsGotCompleteReward"] = false,
                    ["ExploreDatas"] = new Dictionary<int, object?>
                    {
                        [BigWorldCourseExploreId] = new Dictionary<string, object?>
                        {
                            ["ExploreId"] = BigWorldCourseExploreId,
                            ["IsGotReward"] = false,
                            ["PoiCounts"] = new Dictionary<int, int>
                            {
                                [BigWorldCourseExplorePoiId] = boxRewardedCount,
                                [102] = 1
                            }
                        }
                    }
                },
                ["CoreCntData"] = new Dictionary<string, object?>
                {
                    ["ContentId"] = versionId * 100 + 3,
                    ["ReadElementIds"] = state.BigWorldCourseReadElementIds.Distinct().OrderBy(elementId => elementId).ToArray()
                }
            };
        }

        private static Dictionary<string, object?> BuildEmptyBigWorldCourseVersionData(int versionId)
        {
            return new Dictionary<string, object?>
            {
                ["VersionId"] = versionId,
                ["TaskCntData"] = new Dictionary<string, object?>
                {
                    ["ContentId"] = versionId * 100 + 1,
                    ["TotalProgress"] = 0,
                    ["GotRewardIds"] = Array.Empty<int>()
                },
                ["ExploreCntData"] = new Dictionary<string, object?>
                {
                    ["ContentId"] = versionId * 100 + 2,
                    ["IsGotCompleteReward"] = false,
                    ["ExploreDatas"] = new Dictionary<int, object?>()
                },
                ["CoreCntData"] = new Dictionary<string, object?>
                {
                    ["ContentId"] = versionId * 100 + 3,
                    ["ReadElementIds"] = Array.Empty<int>()
                }
            };
        }

        private static DlcWorldSceneObjectDataResponse BuildBigWorldSceneObjectDataResponse(Session session, byte[] requestContent)
        {
            int requestedLevelId = TryReadBigWorldRequestLevelId(requestContent);
            DlcWorldSceneObjectDataResponse response = new();
            foreach (BigWorldClaimedSceneObject sceneObject in GetClaimedBigWorldSceneObjects(session))
            {
                if (requestedLevelId > 0 && sceneObject.LevelId != requestedLevelId)
                    continue;

                response.SceneObjectStates[sceneObject.PlaceId] = BuildClaimedBigWorldSceneObjectState(sceneObject.PlaceId);
            }

            return response;
        }

        private static int TryReadBigWorldRequestLevelId(byte[] requestContent)
        {
            try
            {
                Dictionary<string, object?> request = MessagePackSerializer.Deserialize<Dictionary<string, object?>>(requestContent);
                if (request.TryGetValue("LevelId", out object? levelId))
                    return ReadBigWorldXRpcInt32(levelId);
                if (request.TryGetValue("InstLevelId", out object? instLevelId))
                    return ReadBigWorldXRpcInt32(instLevelId);
            }
            catch
            {
            }

            return 0;
        }

        private static Dictionary<string, object?> BuildClaimedBigWorldSceneObjectState(int placeId)
        {
            return new Dictionary<string, object?>
            {
                ["Active"] = false,
                ["Flags"] = Array.Empty<int>(),
                ["MovementToNodeIndex"] = 0,
                ["MovementNodeIndexIncrement"] = 0,
                ["IsInteractable"] = false,
                ["CurrentAction"] = 0,
                ["PlaceId"] = placeId,
                ["Position"] = null,
                ["Rotation"] = null,
                ["BeScanned"] = true,
                ["VarCompData"] = null
            };
        }

        private static byte[] BuildBigWorldSaveDataPayload(Session session)
        {
            byte[] payload = BigWorldSaveDataPayload.Value;
            if (!HasBigWorldStatePatches(session))
                return payload;

            try
            {
                return PatchBigWorldSaveDataPayload(session, payload);
            }
            catch (Exception ex)
            {
                session.log.Warn($"Falling back to unpatched DlcWorldSaveDataResponse payload: {ex.Message}");
                return payload;
            }
        }

        private static byte[] PatchBigWorldSaveDataPayload(Session session, byte[] payload)
        {
            MessagePackReader reader = new(new ReadOnlySequence<byte>(payload));
            ArrayBufferWriter<byte> buffer = new(payload.Length + 64);
            MessagePackWriter writer = new(buffer);
            List<string> path = new(8);
            RewriteBigWorldSaveDataValue(session, payload, ref reader, ref writer, path);
            if (reader.Consumed != payload.Length)
                throw new MessagePackSerializationException("BigWorld save-data fixture contains trailing MessagePack data.");
            writer.Flush();
            return buffer.WrittenMemory.ToArray();
        }

        private static void RewriteBigWorldSaveDataValue(Session session, byte[] payload, ref MessagePackReader reader, ref MessagePackWriter writer, List<string> path)
        {
            if (TryWriteBigWorldSaveSoSaveDatasMap(session, payload, ref reader, ref writer, path))
                return;

            if (TryWriteBigWorldSaveDataReplacement(session, ref writer, path))
            {
                reader.Skip();
                return;
            }

            switch (reader.NextMessagePackType)
            {
                case MessagePackType.Map:
                    int mapCount = reader.ReadMapHeader();
                    writer.WriteMapHeader(mapCount);
                    for (int index = 0; index < mapCount; index++)
                    {
                        string? key = TryReadBigWorldSaveDataKey(session, ref reader, ref writer, path);
                        path.Add(key ?? string.Empty);
                        RewriteBigWorldSaveDataValue(session, payload, ref reader, ref writer, path);
                        path.RemoveAt(path.Count - 1);
                    }
                    break;
                case MessagePackType.Array:
                    int arrayCount = reader.ReadArrayHeader();
                    writer.WriteArrayHeader(arrayCount);
                    for (int index = 0; index < arrayCount; index++)
                    {
                        path.Add(index.ToString());
                        RewriteBigWorldSaveDataValue(session, payload, ref reader, ref writer, path);
                        path.RemoveAt(path.Count - 1);
                    }
                    break;
                default:
                    CopyRawMessagePackValue(payload, ref reader, ref writer);
                    break;
            }
        }

        private static bool TryWriteBigWorldSaveSoSaveDatasMap(Session session, byte[] payload, ref MessagePackReader reader, ref MessagePackWriter writer, List<string> path)
        {
            if (!IsBigWorldSaveSoSaveDatasPath(path)
                || reader.NextMessagePackType != MessagePackType.Map
                || !int.TryParse(path[2], out int levelId))
            {
                return false;
            }

            HashSet<string> claimedPlaceIds = GetClaimedBigWorldSceneObjects(session)
                .Where(sceneObject => sceneObject.LevelId == levelId)
                .Select(sceneObject => sceneObject.PlaceId.ToString())
                .ToHashSet(StringComparer.Ordinal);
            if (claimedPlaceIds.Count == 0)
                return false;

            MessagePackReader probe = reader;
            int mapCount = probe.ReadMapHeader();
            int writtenCount = 0;
            for (int index = 0; index < mapCount; index++)
            {
                string? key = ReadBigWorldMessagePackKey(ref probe);
                bool skip = key is not null && claimedPlaceIds.Contains(key);
                probe.Skip();
                if (!skip)
                    writtenCount++;
            }

            _ = reader.ReadMapHeader();
            writer.WriteMapHeader(writtenCount);
            for (int index = 0; index < mapCount; index++)
            {
                int keyStart = checked((int)reader.Consumed);
                string? key = ReadBigWorldMessagePackKey(ref reader);
                int keyLength = checked((int)reader.Consumed) - keyStart;
                if (key is not null && claimedPlaceIds.Contains(key))
                {
                    reader.Skip();
                    continue;
                }

                writer.WriteRaw(payload.AsSpan(keyStart, keyLength));
                path.Add(key ?? string.Empty);
                RewriteBigWorldSaveDataValue(session, payload, ref reader, ref writer, path);
                path.RemoveAt(path.Count - 1);
            }

            return true;
        }

        private static string? ReadBigWorldMessagePackKey(ref MessagePackReader reader)
        {
            if (reader.NextMessagePackType == MessagePackType.String)
                return reader.ReadString();

            if (reader.NextMessagePackType == MessagePackType.Integer)
                return reader.ReadInt64().ToString();

            reader.Skip();
            return null;
        }

        private static string? TryReadBigWorldSaveDataKey(Session session, ref MessagePackReader reader, ref MessagePackWriter writer, List<string> path)
        {
            (int fixtureWorldId, int fixtureLevelId) = BigWorldFixtureWorld.Value;
            (int worldId, int levelId) = GetEffectiveBigWorldLocation(session);
            bool canReplaceLevelDataKey = path.Count == 2
                && path[0] == "WorldSaveData"
                && path[1] == "LevelDataDict"
                && levelId != fixtureLevelId;

            if (reader.NextMessagePackType == MessagePackType.String)
            {
                string key = reader.ReadString() ?? string.Empty;
                if (canReplaceLevelDataKey && key == fixtureLevelId.ToString())
                {
                    writer.Write(levelId.ToString());
                    return levelId.ToString();
                }

                writer.Write(key);
                return key;
            }

            if (reader.NextMessagePackType == MessagePackType.Integer)
            {
                long key = reader.ReadInt64();
                if (canReplaceLevelDataKey && key == fixtureLevelId)
                {
                    writer.Write(levelId);
                    return levelId.ToString();
                }

                WriteBigWorldInteger(ref writer, key);
                return key.ToString();
            }

            CopyRawMessagePackValue(Array.Empty<byte>(), ref reader, ref writer);
            return null;
        }

        private static bool TryWriteBigWorldSaveDataReplacement(Session session, ref MessagePackWriter writer, List<string> path)
        {
            (int worldId, int levelId) = GetEffectiveBigWorldLocation(session);
            BigWorldPlayerState state = GetBigWorldState(session);

            if (IsBigWorldSaveLevelDataPath(path, "WorldId"))
            {
                writer.Write(worldId);
                return true;
            }

            if (IsBigWorldSaveLevelDataPath(path, "LevelId"))
            {
                writer.Write(levelId);
                return true;
            }

            if (IsBigWorldSaveLevelDataPath(path, "ReliablePos")
                && TryGetSupportedBigWorldLastPosition(state, out BigWorldVector3? position))
            {
                WriteBigWorldVector3(ref writer, position!);
                return true;
            }

            if (IsBigWorldSaveLevelDataPath(path, "ReliableRotationY")
                && TryGetSupportedBigWorldRotationY(state, out double rotationY))
            {
                WriteBigWorldFloat(ref writer, rotationY);
                return true;
            }


            return false;
        }

        private static bool IsBigWorldSaveLevelDataPath(List<string> path, string leaf)
        {
            return path.Count == 4
                && path[0] == "WorldSaveData"
                && path[1] == "LevelDataDict"
                && path[3] == leaf;
        }

        private static bool IsBigWorldSaveSoSaveDatasPath(List<string> path)
        {
            return path.Count == 5
                && path[0] == "WorldSaveData"
                && path[1] == "LevelDataDict"
                && path[3] == "ActorSaveData"
                && path[4] == "SoSaveDatas";
        }


        private static byte[] BuildEnterInstLevelResponsePayload(Session session)
        {
            try
            {
                Dictionary<string, object?> enterWorldResponse = MessagePackSerializer.Deserialize<Dictionary<string, object?>>(BuildBigWorldEnterWorldPayload(session));
                enterWorldResponse.TryGetValue("EnterResultData", out object? enterResultData);
                return MessagePackSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["Code"] = 0,
                    ["EnterResultData"] = enterResultData
                });
            }
            catch (Exception ex)
            {
                session.log.Warn($"Falling back to empty EnterInstLevelResponse payload: {ex.Message}");
                return MessagePackSerializer.Serialize(new EnterInstLevelResponse());
            }
        }

        private static void PersistBigWorldEnterInstLevelRequest(Session session, EnterInstLevelRequest request)
        {
            BigWorldPlayerState state = GetBigWorldState(session);
            bool changed = false;
            (int fixtureWorldId, int fixtureLevelId) = BigWorldFixtureWorld.Value;
            bool canPersistLocation = (request.WorldId <= 0 || request.WorldId == fixtureWorldId)
                && request.InstLevelId == fixtureLevelId;

            if (canPersistLocation)
            {
                if (state.LastWorldId != fixtureWorldId)
                {
                    state.LastWorldId = fixtureWorldId;
                    changed = true;
                }

                if (state.LastLevelId != fixtureLevelId)
                {
                    state.LastLevelId = fixtureLevelId;
                    changed = true;
                }

                if (TryReadBigWorldVector3(request.TargetPos, out BigWorldVector3? targetPos))
                {
                    state.LastPosition = targetPos;
                    changed = true;
                }

                if (TryReadBigWorldRotationY(request.TargetRot, out double targetRotationY))
                {
                    state.LastRotationY = targetRotationY;
                    changed = true;
                }
            }

            if (state.LastSelfRuntimeId != 0)
            {
                state.LastSelfRuntimeId = 0;
                changed = true;
            }

            if (changed)
                session.player.Save();
        }

        private static void TryTrackBigWorldCommonRequestState(Session session, byte[] payload)
        {
            try
            {
                object?[] rpc = MessagePackSerializer.Deserialize<object?[]>(payload);
                if (rpc.Length == 0 || rpc[0] is not string rpcName)
                    return;

                if (rpcName == "RpcSwitchPlayerNpcRequest")
                {
                    BigWorldPlayerState state = GetBigWorldState(session);
                    if (state.LastSelfRuntimeId != 0)
                    {
                        state.LastSelfRuntimeId = 0;
                        session.player.Save();
                    }
                }
            }
            catch
            {
            }
        }

        private static void TryPersistBigWorldNpcPosition(Session session, byte[] payload)
        {
            try
            {
                object?[] rpc = MessagePackSerializer.Deserialize<object?[]>(payload);
                if (rpc.Length < 6
                    || rpc[0] is not string rpcName
                    || rpcName != "XRpcNpcPositionAndRotation"
                    || rpc[1] is not byte[] argsPayload)
                {
                    return;
                }

                int levelId = ReadBigWorldXRpcInt32(rpc[4]);
                int targetUuid = ReadBigWorldXRpcInt32(rpc[5]);
                if (!TryReadBigWorldNpcPositionArgs(argsPayload, out BigWorldVector3? position, out BigWorldVector4? rotation, out double rotationY))
                    return;

                BigWorldPlayerState state = GetBigWorldState(session);
                if (state.LastSelfRuntimeId != 0 && state.LastSelfRuntimeId != targetUuid)
                    return;

                state.LastSelfRuntimeId = targetUuid;
                if (state.LastWorldId <= 0)
                    state.LastWorldId = BigWorldFixtureWorld.Value.WorldId;
                state.LastLevelId = levelId;
                state.LastPosition = position;
                state.LastRotation = rotation;
                state.LastRotationY = rotationY;
                session.player.Save();
            }
            catch (Exception ex)
            {
                session.log.Error($"Failed to persist BigWorld position update: {ex}");
            }
        }

        private static bool TryReadBigWorldNpcPositionArgs(byte[] argsPayload, out BigWorldVector3? position, out BigWorldVector4? rotation, out double rotationY)
        {
            position = null;
            rotation = null;
            rotationY = 0D;
            try
            {
                MessagePackReader reader = new(new ReadOnlySequence<byte>(argsPayload));
                int count = reader.ReadArrayHeader();
                if (count < 7)
                    return false;

                double x = ReadBigWorldXRpcDouble(ref reader);
                double y = ReadBigWorldXRpcDouble(ref reader);
                double z = ReadBigWorldXRpcDouble(ref reader);
                double qx = ReadBigWorldXRpcDouble(ref reader);
                double qy = ReadBigWorldXRpcDouble(ref reader);
                double qz = ReadBigWorldXRpcDouble(ref reader);
                double qw = ReadBigWorldXRpcDouble(ref reader);
                position = new BigWorldVector3 { X = x, Y = y, Z = z };
                rotation = new BigWorldVector4 { X = qx, Y = qy, Z = qz, W = qw };
                rotationY = NormalizeBigWorldYawDegrees(Math.Atan2(2D * (qw * qy + qx * qz), 1D - 2D * (qy * qy + qz * qz)) * 180D / Math.PI);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static double ReadBigWorldXRpcDouble(ref MessagePackReader reader)
        {
            return reader.NextMessagePackType switch
            {
                MessagePackType.Float => reader.ReadDouble(),
                MessagePackType.Integer => reader.ReadInt64(),
                _ => throw new MessagePackSerializationException($"Expected BigWorld XRpc numeric value, got {reader.NextMessagePackType}.")
            };
        }

        private static bool TryReadBigWorldVector3(object? value, out BigWorldVector3? vector)
        {
            vector = null;
            if (value is null)
                return false;
            if (value is BigWorldVector3 typed)
            {
                vector = typed;
                return true;
            }
            if (value is object?[] array && array.Length >= 3
                && TryReadBigWorldDouble(array[0], out double arrayX)
                && TryReadBigWorldDouble(array[1], out double arrayY)
                && TryReadBigWorldDouble(array[2], out double arrayZ))
            {
                vector = new BigWorldVector3 { X = arrayX, Y = arrayY, Z = arrayZ };
                return true;
            }
            if (value is System.Collections.IDictionary dictionary
                && TryReadBigWorldDictionaryDouble(dictionary, "X", out double x)
                && TryReadBigWorldDictionaryDouble(dictionary, "Y", out double y)
                && TryReadBigWorldDictionaryDouble(dictionary, "Z", out double z))
            {
                vector = new BigWorldVector3 { X = x, Y = y, Z = z };
                return true;
            }

            return false;
        }

        private static bool TryReadBigWorldRotationY(object? value, out double rotationY)
        {
            rotationY = 0D;
            if (value is null)
                return false;
            if (TryReadBigWorldVector3(value, out BigWorldVector3? eulerAngles) && eulerAngles is not null)
            {
                rotationY = eulerAngles.Y;
                return true;
            }
            if (value is object?[] array && array.Length >= 4
                && TryReadBigWorldDouble(array[0], out double qx)
                && TryReadBigWorldDouble(array[1], out double qy)
                && TryReadBigWorldDouble(array[2], out double qz)
                && TryReadBigWorldDouble(array[3], out double qw))
            {
                rotationY = NormalizeBigWorldYawDegrees(Math.Atan2(2D * (qw * qy + qx * qz), 1D - 2D * (qy * qy + qz * qz)) * 180D / Math.PI);
                return true;
            }
            if (value is System.Collections.IDictionary dictionary)
            {
                if (TryReadBigWorldDictionaryDouble(dictionary, "X", out double dictionaryQx)
                    && TryReadBigWorldDictionaryDouble(dictionary, "Y", out double dictionaryQy)
                    && TryReadBigWorldDictionaryDouble(dictionary, "Z", out double dictionaryQz)
                    && TryReadBigWorldDictionaryDouble(dictionary, "W", out double dictionaryQw))
                {
                    rotationY = NormalizeBigWorldYawDegrees(Math.Atan2(2D * (dictionaryQw * dictionaryQy + dictionaryQx * dictionaryQz), 1D - 2D * (dictionaryQy * dictionaryQy + dictionaryQz * dictionaryQz)) * 180D / Math.PI);
                    return true;
                }
                if (TryReadBigWorldDictionaryDouble(dictionary, "Y", out double eulerY))
                {
                    rotationY = eulerY;
                    return true;
                }
            }

            return false;
        }

        private static double? GetBigWorldRotationY(BigWorldPlayerState state)
        {
            if (state.LastRotationY is double rotationY)
                return rotationY;
            if (state.LastRotation is { } rotation)
                return NormalizeBigWorldYawDegrees(Math.Atan2(2D * (rotation.W * rotation.Y + rotation.X * rotation.Z), 1D - 2D * (rotation.Y * rotation.Y + rotation.Z * rotation.Z)) * 180D / Math.PI);
            return null;
        }

        private static double NormalizeBigWorldYawDegrees(double yaw)
        {
            yaw %= 360D;
            return yaw < 0D ? yaw + 360D : yaw;
        }

        private static bool TryReadBigWorldDictionaryDouble(System.Collections.IDictionary dictionary, string key, out double value)
        {
            foreach (object? candidateKey in dictionary.Keys)
            {
                if (candidateKey is not null
                    && string.Equals(candidateKey.ToString(), key, StringComparison.OrdinalIgnoreCase)
                    && TryReadBigWorldDouble(dictionary[candidateKey], out value))
                {
                    return true;
                }
            }

            value = 0D;
            return false;
        }

        private static bool TryReadBigWorldDouble(object? value, out double result)
        {
            switch (value)
            {
                case byte typed:
                    result = typed;
                    return true;
                case sbyte typed:
                    result = typed;
                    return true;
                case short typed:
                    result = typed;
                    return true;
                case ushort typed:
                    result = typed;
                    return true;
                case int typed:
                    result = typed;
                    return true;
                case uint typed:
                    result = typed;
                    return true;
                case long typed:
                    result = typed;
                    return true;
                case ulong typed:
                    result = typed;
                    return true;
                case float typed:
                    result = typed;
                    return true;
                case double typed:
                    result = typed;
                    return true;
                default:
                    result = 0D;
                    return false;
            }
        }

        private static bool TryWriteBigWorldNativeSceneObjectRow(Session session, byte[] payload, ref MessagePackReader reader, ref MessagePackWriter writer)
        {
            try
            {
                int start = checked((int)reader.Consumed);
                MessagePackReader probe = reader;
                probe.Skip();
                int length = checked((int)probe.Consumed) - start;
                byte[] rawRow = payload.AsSpan(start, length).ToArray();
                object?[] row = MessagePackSerializer.Deserialize<object?[]>(rawRow);
                if (row.Length != 10 || row[0] is not string rowType || rowType != "XSceneObject")
                    return false;
                if (row[1] is not byte[] configPayload || !TryReadBigWorldSceneObjectPlaceId(configPayload, out int placeId))
                    return false;

                (int fixtureWorldId, int fixtureLevelId) = BigWorldFixtureWorld.Value;
                (int worldId, int levelId) = GetEffectiveBigWorldLocation(session);
                int rowLevelId = ReadBigWorldXRpcInt32(row[4]);
                bool changed = false;
                if (rowLevelId == fixtureLevelId && levelId != fixtureLevelId)
                {
                    row[4] = levelId;
                    rowLevelId = levelId;
                    changed = true;
                }

                if (HasClaimedBigWorldSceneObject(session, rowLevelId, placeId))
                {
                    row[3] = false;
                    if (row[7] is byte[] componentsPayload)
                        row[7] = PatchClaimedBigWorldSceneObjectComponents(componentsPayload);
                    changed = true;
                }

                if (!changed)
                    return false;

                reader = probe;
                writer.WriteRaw(MessagePackSerializer.Serialize(row));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadBigWorldSceneObjectPlaceId(byte[] configPayload, out int placeId)
        {
            placeId = 0;
            try
            {
                object?[] config = MessagePackSerializer.Deserialize<object?[]>(configPayload);
                if (config.Length < 2)
                    return false;

                placeId = ReadBigWorldXRpcInt32(config[1]);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static byte[] PatchClaimedBigWorldSceneObjectComponents(byte[] componentsPayload)
        {
            try
            {
                object?[] components = MessagePackSerializer.Deserialize<object?[]>(componentsPayload);
                for (int index = 0; index < components.Length; index++)
                {
                    if (components[index] is not object?[] component || component.Length < 2 || component[1] is not string componentName)
                        continue;

                    component[0] = componentName switch
                    {
                        "XSceneObjectStateComponent" => MessagePackSerializer.Serialize(new object?[] { 0 }),
                        "XSceneObjectInteractableComponent" => MessagePackSerializer.Serialize(new object?[] { false }),
                        "XSceneTreasureBoxBeScannedComponent" => MessagePackSerializer.Serialize(new object?[] { true }),
                        _ => component[0]
                    };
                }

                return MessagePackSerializer.Serialize(components);
            }
            catch
            {
                return componentsPayload;
            }
        }

        private static bool IsBigWorldCommanderPartListPath(List<string> path)
        {
            return path.Count >= 2
                && path[path.Count - 2] == "PartData"
                && path[path.Count - 1] == "PartList";
        }

        private static bool TryWriteBigWorldCommanderPartList(byte[] payload, ref MessagePackReader reader, ref MessagePackWriter writer)
        {
            try
            {
                IReadOnlyList<(int PartId, int ColourId)> fixtureParts = BigWorldFixtureCommanderParts.Value;
                HashSet<int> fixturePartIds = new(fixtureParts.Select(part => part.PartId));
                MessagePackReader probe = reader;
                int count = probe.ReadArrayHeader();
                Dictionary<int, byte[]> rawPartsById = new(count);

                for (int index = 0; index < count; index++)
                {
                    int start = checked((int)probe.Consumed);
                    probe.Skip();
                    int length = checked((int)probe.Consumed) - start;
                    byte[] rawPart = payload.AsSpan(start, length).ToArray();

                    if (TryReadBigWorldPartId(rawPart, out int partId))
                        rawPartsById.TryAdd(partId, rawPart);
                }

                if (rawPartsById.Count == 0
                    || rawPartsById.Count != count
                    || rawPartsById.Keys.Any(partId => !fixturePartIds.Contains(partId))
                    || rawPartsById.Count * 2 < fixtureParts.Count
                    || fixtureParts.All(part => rawPartsById.ContainsKey(part.PartId)))
                {
                    return false;
                }

                reader = probe;
                writer.WriteArrayHeader(fixtureParts.Count);
                foreach ((int partId, int colourId) in fixtureParts)
                {
                    if (rawPartsById.TryGetValue(partId, out byte[]? rawPart))
                        writer.WriteRaw(rawPart);
                    else
                        WriteBigWorldCommanderPart(ref writer, partId, colourId);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadBigWorldPartId(byte[] partPayload, out int partId)
        {
            partId = 0;
            try
            {
                Dictionary<string, object?> part = MessagePackSerializer.Deserialize<Dictionary<string, object?>>(partPayload);
                if (!part.TryGetValue("PartId", out object? value))
                    return false;

                partId = ReadBigWorldXRpcInt32(value);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void WriteBigWorldCommanderPart(ref MessagePackWriter writer, int partId, int colourId)
        {
            writer.WriteMapHeader(2);
            writer.Write("PartId");
            writer.Write(partId);
            writer.Write("ColourId");
            writer.Write(colourId);
        }




        internal static void SendPendingBigWorldStartFightNotify(Session session)
        {
            if (!session.PendingBigWorldStartFightNotify)
                return;

            session.PendingBigWorldStartFightNotify = false;
            session.SendPush(new StartFightNotify());
        }

        internal static void SendPendingBigWorldLoadCompleteXRpc(Session session)
        {
            if (!session.PendingBigWorldLoadCompleteXRpc)
                return;

            session.PendingBigWorldLoadCompleteXRpc = false;
            if (BigWorldLoadCompleteXRpcPushesSnapshot.Value["Pushes"] is not JArray pushes)
                return;

            foreach (JObject push in pushes.OfType<JObject>())
            {
                string? name = push.Value<string>("Name");
                string? payload = push.Value<string>("Payload");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(payload))
                    continue;

                try
                {
                    byte[] payloadBytes = Convert.FromBase64String(payload);
                    session.SendPush(name, PatchBigWorldXRpcPayload(session, payloadBytes));
                }
                catch
                {
                    // A malformed optional bootstrap fixture entry must not abort LoadComplete handling.
                }
            }
        }

        private static byte[] PatchBigWorldXRpcPayload(Session session, byte[] payload)
        {
            try
            {
                object?[] rpc = MessagePackSerializer.Deserialize<object?[]>(payload);
                if (rpc.Length >= 2
                    && rpc[0] is string { } rpcName
                    && rpcName == "RpcSetCombatState"
                    && rpc[1] is byte[] argsPayload)
                {
                    Dictionary<string, object?> args = MessagePackSerializer.Deserialize<Dictionary<string, object?>>(argsPayload);
                    args["PlayerId"] = checked((int)session.player.PlayerData.Id);
                    rpc[1] = MessagePackSerializer.Serialize(args);
                    return MessagePackSerializer.Serialize(rpc);
                }
            }
            catch
            {
                // Best-effort player-id patching must not block the remaining load-complete RPC bootstrap.
            }

            return payload;
        }

    }
}
