using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.fuben.mainline2;
using AscNet.Table.V2.share.reward;
using MessagePack;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AscNet.GameServer.Handlers
{
    #region MsgPackScheme
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    [MessagePackObject(true)]
    public class MainLine2UpdateExhibitionChapterRequest
    {
        public int ChapterId { get; set; }
    }

    [MessagePackObject(true)]
    public class MainLine2UpdateExhibitionChapterResponse
    {
        public int Code { get; set; }
    }

    [MessagePackObject(true)]
    public class MainLine2ReceiveMainTreasureRequest
    {
        public int? MainId { get; set; }
        public int? MainLineId { get; set; }
        public int? Id { get; set; }
        public int? TreasureId { get; set; }
        public int? TreasureIdx { get; set; }
        public int? TreasureIndex { get; set; }
        public int? Index { get; set; }
    }

    [MessagePackObject(true)]
    public class MainLine2ReceiveMainTreasureResponse
    {
        public int Code { get; set; }
        public List<int> RewardIdxs { get; set; } = new();
        public List<RewardGoods> RewardGoodsList { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class MainLine2MessageStateUpdateRequest
    {
        public int? Id { get; set; }
        public int? MessageId { get; set; }
        public int? MessageStateId { get; set; }
        public int? State { get; set; }
        public int? Status { get; set; }
        public int? Value { get; set; }
        public int? MessageState { get; set; }
    }

    [MessagePackObject(true)]
    public class MainLine2MessageStateUpdateResponse
    {
        public int Code { get; set; }
    }

    [MessagePackObject(true)]
    public class MainLineLuosaitaEnterRequest
    {
        public int SectionId { get; set; }
    }

    [MessagePackObject(true)]
    public class MainLineLuosaitaEnterResponse
    {
        public int Code { get; set; }
        public MainLineLuosaitaSectionInfo SectionInfo { get; set; }
    }

    [MessagePackObject(true)]
    public class MainLineLuosaitaMoveRequest
    {
        public int SectionId { get; set; }
        public int PosId { get; set; }
        public int TargetPosId { get; set; }
    }

    [MessagePackObject(true)]
    public class MainLineLuosaitaMoveResponse
    {
        public int Code { get; set; }
        public MainLineLuosaitaSectionInfo SectionInfo { get; set; }
    }

    [MessagePackObject(true)]
    public class MainLineLuosaitaUseDocRequest
    {
        public int SectionId { get; set; }
        public int DocId { get; set; }
    }

    [MessagePackObject(true)]
    public class MainLineLuosaitaUseDocResponse
    {
        public int Code { get; set; }
        public MainLineLuosaitaSectionInfo SectionInfo { get; set; }
    }

    [MessagePackObject(true)]
    public class NotifyMainLineLuosaitaSectionInfo
    {
        public MainLineLuosaitaSectionInfo SectionInfo { get; set; }
    }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    #endregion

    internal class MainLine2Module
    {
        [RequestPacketHandler("MainLine2UpdateExhibitionChapterRequest")]
        public static void MainLine2UpdateExhibitionChapterRequestHandler(Session session, Packet.Request packet)
        {
            MainLine2UpdateExhibitionChapterRequest request =
                MessagePackSerializer.Deserialize<MainLine2UpdateExhibitionChapterRequest>(packet.Content);
            FubenMainLine2Data data = session.player.FubenMainLine2Data ??= new();
            NormalizeMainLine2Data(data);
            if (IsExhibitionChapter(request.ChapterId) && data.LastExhibitionChapterId != request.ChapterId)
            {
                data.LastExhibitionChapterId = request.ChapterId;
                session.player.SaveChecked();
            }

            session.SendResponse(new MainLine2UpdateExhibitionChapterResponse
            {
                Code = 0
            }, packet.Id);
        }

        [RequestPacketHandler("MainLine2ReceiveMainTreasureRequest")]
        public static void MainLine2ReceiveMainTreasureRequestHandler(Session session, Packet.Request packet)
        {
            MainLine2ReceiveMainTreasureRequest request = MessagePackSerializer.Deserialize<MainLine2ReceiveMainTreasureRequest>(packet.Content);
            MainLine2ReceiveMainTreasureResponse response = ClaimMainLine2MainTreasure(session, request, packet.Content);
            session.SendResponse(response, packet.Id);
        }

        [RequestPacketHandler("MainLine2MessageStateUpdateRequest")]
        public static void MainLine2MessageStateUpdateRequestHandler(Session session, Packet.Request packet)
        {
            FubenMainLine2Data data = session.player.FubenMainLine2Data ??= new();
            NormalizeMainLine2Data(data);
            string payloadJson = MessagePackSerializer.ConvertToJson(packet.Content);
            bool updated = false;
            try
            {
                updated = TryApplyMainLine2MessageStateUpdate(data, packet.Content, payloadJson);
            }
            catch (Exception ex)
            {
                session.log.Warn($"MainLine2MessageStateUpdateRequest invalid payload={payloadJson}: {ex.Message}");
            }

            if (!updated)
            {
                session.log.Warn($"MainLine2MessageStateUpdateRequest unresolved payload={payloadJson}");
            }

            session.player.Save();
            session.SendResponse(new MainLine2MessageStateUpdateResponse { Code = 0 }, packet.Id);
        }

        [RequestPacketHandler("MainLineLuosaitaEnterRequest")]
        public static void MainLineLuosaitaEnterRequestHandler(Session session, Packet.Request packet)
        {
            MainLineLuosaitaEnterRequest request = MessagePackSerializer.Deserialize<MainLineLuosaitaEnterRequest>(packet.Content);

            session.SendResponse(new MainLineLuosaitaEnterResponse
            {
                Code = 0,
                SectionInfo = MainLineLuosaitaPayloadFactory.BuildEnterSectionInfo(request.SectionId, session.stage)
            }, packet.Id);
        }

        [RequestPacketHandler("MainLineLuosaitaMoveRequest")]
        public static void MainLineLuosaitaMoveRequestHandler(Session session, Packet.Request packet)
        {
            MainLineLuosaitaMoveRequest request = MessagePackSerializer.Deserialize<MainLineLuosaitaMoveRequest>(packet.Content);

            session.SendResponse(new MainLineLuosaitaMoveResponse
            {
                Code = 0,
                SectionInfo = MainLineLuosaitaPayloadFactory.BuildMoveSectionInfo(request.SectionId, request.PosId, request.TargetPosId, session.stage)
            }, packet.Id);
        }

        [RequestPacketHandler("MainLineLuosaitaUseDocRequest")]
        public static void MainLineLuosaitaUseDocRequestHandler(Session session, Packet.Request packet)
        {
            MainLineLuosaitaUseDocRequest request = MessagePackSerializer.Deserialize<MainLineLuosaitaUseDocRequest>(packet.Content);

            session.SendResponse(new MainLineLuosaitaUseDocResponse
            {
                Code = 0,
                SectionInfo = MainLineLuosaitaPayloadFactory.BuildUseDocSectionInfo(request.SectionId, request.DocId, session.stage)
            }, packet.Id);
        }

        internal static FubenMainLine2Data BuildLoginData(Session session)
        {
            FubenMainLine2Data persistedData = session.player.FubenMainLine2Data ??= new();
            NormalizeMainLine2Data(persistedData);
            EnsureNavigableExhibitionChapter(persistedData);
            LifeTreeModule.NormalizePersistedChapterAcknowledgements(session.player);

            FubenMainLine2Data data = CloneMainLine2Data(persistedData);
            if (session.stage is not null)
            {
                MergeMainLine2FirstPassTimes(data, session.stage);
            }
            return data;
        }

        private static void NormalizeMainLine2Data(FubenMainLine2Data data)
        {
            data.LastPassStage ??= new();
            data.MainDatas ??= new();
            data.ChapterDatas ??= new();
            data.EggsTreasureDistributeData ??= new();
            data.FirstPassTime ??= new();
            data.MessageState ??= new();
        }
        private static bool EnsureNavigableExhibitionChapter(FubenMainLine2Data data)
        {
            if (IsExhibitionChapter(data.LastExhibitionChapterId))
                return false;

            int chapterId = LifeTreeModule.GetCurrentPopupChapterId()
                ?? LifeTreeModule.GetNavigableChapterIds().Order().FirstOrDefault();
            if (chapterId == 0)
                return false;

            data.LastExhibitionChapterId = chapterId;
            return true;
        }

        private static bool IsExhibitionChapter(int chapterId)
            => chapterId > 0 && TableReaderV2.Parse<MainLine2ExhibitionChapterTable>()
                .Any(chapter => chapter.Id == chapterId);

        private static bool TryApplyMainLine2MessageStateUpdate(FubenMainLine2Data data, byte[] rawContent, string payloadJson)
        {
            bool updated = false;
            try
            {
                MainLine2MessageStateUpdateRequest request = MessagePackSerializer.Deserialize<MainLine2MessageStateUpdateRequest>(rawContent);
                updated = TryApplyMainLine2MessageStateUpdate(data, request);
            }
            catch (Exception)
            {
            }

            JToken payload = JToken.Parse(payloadJson);
            updated |= TryApplyMainLine2MessageStateMap(data, payload);
            if (payload is not JObject payloadObject)
            {
                return updated;
            }

            updated |= TryApplyMainLine2MessageStateMap(data, payloadObject["MessageState"])
                | TryApplyMainLine2MessageStateMap(data, payloadObject["MessageStates"]);
            int? messageId = ReadFirstMainLine2MessageStateInt(payloadObject, "Id", "MessageId", "MessageStateId");
            if (messageId.HasValue && messageId.Value > 0)
            {
                int state = ReadFirstMainLine2MessageStateInt(payloadObject, "State", "Status", "Value", "MessageState") ?? 1;
                data.MessageState[messageId.Value] = state;
                updated = true;
            }

            return updated;
        }

        private static bool TryApplyMainLine2MessageStateUpdate(FubenMainLine2Data data, MainLine2MessageStateUpdateRequest request)
        {
            int? messageId = request.Id ?? request.MessageId ?? request.MessageStateId;
            if (!messageId.HasValue || messageId.Value <= 0)
            {
                return false;
            }

            int state = request.State ?? request.Status ?? request.Value ?? request.MessageState ?? 1;
            data.MessageState[messageId.Value] = state;
            return true;
        }

        private static bool TryApplyMainLine2MessageStateMap(FubenMainLine2Data data, JToken? token)
        {
            bool updated = false;
            if (token is JObject map)
            {
                foreach (JProperty property in map.Properties())
                {
                    int? state = ReadMainLine2MessageStateInt(property.Value);
                    if (int.TryParse(property.Name, out int messageId) && messageId > 0 && state.HasValue)
                    {
                        data.MessageState[messageId] = state.Value;
                        updated = true;
                    }
                }
            }
            else if (token is JArray entries)
            {
                foreach (JToken entry in entries)
                {
                    if (entry is not JObject entryObject)
                    {
                        continue;
                    }

                    int? messageId = ReadFirstMainLine2MessageStateInt(entryObject, "Key", "Id", "MessageId", "MessageStateId");
                    int? state = ReadFirstMainLine2MessageStateInt(entryObject, "Value", "State", "Status", "MessageState");
                    if (messageId.HasValue && messageId.Value > 0 && state.HasValue)
                    {
                        data.MessageState[messageId.Value] = state.Value;
                        updated = true;
                    }
                }
            }

            return updated;
        }

        private static int? ReadFirstMainLine2MessageStateInt(JObject payload, params string[] names)
        {
            foreach (string name in names)
            {
                int? value = ReadMainLine2MessageStateInt(payload[name]);
                if (value.HasValue)
                {
                    return value.Value;
                }
            }

            return null;
        }

        private static int? ReadMainLine2MessageStateInt(JToken? token)
        {
            if (token is null || token.Type == JTokenType.Null)
            {
                return null;
            }

            return token.Type switch
            {
                JTokenType.Integer => token.Value<int>(),
                JTokenType.Boolean => token.Value<bool>() ? 1 : 0,
                JTokenType.String => int.TryParse(token.Value<string>(), out int value) ? value : null,
                _ => null
            };
        }

        private static FubenMainLine2Data CloneMainLine2Data(FubenMainLine2Data data)
        {
            return new FubenMainLine2Data
            {
                LastPassStage = new Dictionary<int, long>(data.LastPassStage),
                MainDatas = data.MainDatas.Select(mainData => new MainLine2MainDatum
                {
                    Id = mainData.Id,
                    IsAchievementGet = mainData.IsAchievementGet,
                    MainTreasureIdxs = new List<int>(mainData.MainTreasureIdxs)
                }).ToList(),
                ChapterDatas = data.ChapterDatas.Select(chapterData => new MainLine2ChapterDatum
                {
                    Id = chapterData.Id,
                    TreasureIdxs = new List<int>(chapterData.TreasureIdxs)
                }).ToList(),
                EggsTreasureDistributeData = new List<object>(data.EggsTreasureDistributeData),
                FirstPassTime = new Dictionary<int, long>(data.FirstPassTime),
                MessageState = new Dictionary<int, int>(data.MessageState),
                LastExhibitionChapterId = data.LastExhibitionChapterId
            };
        }

        private static MainLine2ReceiveMainTreasureResponse ClaimMainLine2MainTreasure(Session session, MainLine2ReceiveMainTreasureRequest request, byte[] rawRequestContent)
        {
            MainLine2MainTable? main = ResolveMainLine2MainTable(request);
            if (main is null || main.TreasureId is null or <= 0)
            {
                session.log.Warn($"MainLine2ReceiveMainTreasureRequest unresolved payload={MessagePackSerializer.ConvertToJson(rawRequestContent)}");
                return new MainLine2ReceiveMainTreasureResponse { Code = 20003008 };
            }

            MainLine2TreasureTable? treasure = TableReaderV2.Parse<MainLine2TreasureTable>()
                .FirstOrDefault(x => x.Id == main.TreasureId.Value);
            if (treasure is null)
            {
                return new MainLine2ReceiveMainTreasureResponse { Code = 20003008 };
            }

            FubenMainLine2Data data = session.player.FubenMainLine2Data ??= new();
            NormalizeMainLine2Data(data);
            int passedStageCount = CountPassedMainLine2Stages(session.stage, main);
            List<int> rewardIdxs = ResolveClaimableMainLine2RewardIdxs(request, treasure, data, main.Id, passedStageCount);
            if (rewardIdxs.Count == 0)
            {
                bool hasProgressForAnyReward = treasure.StageCounts.Any(requiredStageCount => passedStageCount >= requiredStageCount);
                return new MainLine2ReceiveMainTreasureResponse { Code = hasProgressForAnyReward ? 20003010 : 20003009 };
            }

            var rewardGoods = ResolveMainLine2MainTreasureRewardGoods(treasure, rewardIdxs);
            if (rewardGoods.Count == 0)
            {
                return new MainLine2ReceiveMainTreasureResponse { Code = 20003008 };
            }

            foreach (int rewardIdx in rewardIdxs)
            {
                if (!session.player.AddMainLine2MainTreasure(main.Id, rewardIdx))
                {
                    return new MainLine2ReceiveMainTreasureResponse { Code = 20003010 };
                }
            }

            List<RewardGoods> rewardGoodsList = RewardHandler.GiveRewards(rewardGoods, session);
            session.player.Save();
            session.inventory.Save();
            session.character.Save();

            return new MainLine2ReceiveMainTreasureResponse
            {
                Code = 0,
                RewardIdxs = rewardIdxs,
                RewardGoodsList = rewardGoodsList
            };
        }

        private static MainLine2MainTable? ResolveMainLine2MainTable(MainLine2ReceiveMainTreasureRequest request)
        {
            List<MainLine2MainTable> mains = TableReaderV2.Parse<MainLine2MainTable>();
            int? mainId = request.MainId ?? request.MainLineId ?? request.Id;
            if (mainId is not null)
            {
                return mains.FirstOrDefault(x => x.Id == mainId.Value);
            }

            if (request.TreasureId is not null)
            {
                return mains.FirstOrDefault(x => x.TreasureId == request.TreasureId.Value);
            }

            return null;
        }

        private static int? ResolveMainLine2TreasureIdx(MainLine2ReceiveMainTreasureRequest request)
        {
            return request.TreasureIdx ?? request.TreasureIndex ?? request.Index;
        }

        private static List<int> ResolveClaimableMainLine2RewardIdxs(
            MainLine2ReceiveMainTreasureRequest request,
            MainLine2TreasureTable treasure,
            FubenMainLine2Data data,
            int mainId,
            int passedStageCount)
        {
            int rewardCount = Math.Min(treasure.StageCounts.Count, treasure.RewardIds.Count);
            int? requestedRewardIdx = ResolveMainLine2TreasureIdx(request);
            if (requestedRewardIdx is not null)
            {
                int rewardIdx = requestedRewardIdx.Value;
                if (rewardIdx < 0
                    || rewardIdx >= rewardCount
                    || passedStageCount < treasure.StageCounts[rewardIdx]
                    || IsMainLine2MainTreasureClaimed(data, mainId, rewardIdx))
                {
                    return [];
                }

                return [rewardIdx];
            }

            List<int> rewardIdxs = new();
            for (int rewardIdx = 0; rewardIdx < rewardCount; rewardIdx++)
            {
                if (passedStageCount >= treasure.StageCounts[rewardIdx]
                    && !IsMainLine2MainTreasureClaimed(data, mainId, rewardIdx))
                {
                    rewardIdxs.Add(rewardIdx);
                }
            }

            return rewardIdxs;
        }

        private static List<RewardGoodsTable> ResolveMainLine2MainTreasureRewardGoods(MainLine2TreasureTable treasure, IEnumerable<int> rewardIdxs)
        {
            List<RewardGoodsTable> rewardGoods = new();
            foreach (int rewardIdx in rewardIdxs)
            {
                if (rewardIdx < treasure.HighlightRewardIds.Count && treasure.HighlightRewardIds[rewardIdx] > 0)
                {
                    rewardGoods.AddRange(RewardHandler.GetRewardGoods(treasure.HighlightRewardIds[rewardIdx]));
                }

                if (rewardIdx < treasure.RewardIds.Count && treasure.RewardIds[rewardIdx] > 0)
                {
                    rewardGoods.AddRange(RewardHandler.GetRewardGoods(treasure.RewardIds[rewardIdx]));
                }
            }

            return rewardGoods;
        }

        private static bool IsMainLine2MainTreasureClaimed(FubenMainLine2Data data, int mainId, int treasureIdx)
        {
            return data.MainDatas.FirstOrDefault(x => x.Id == mainId)?.MainTreasureIdxs.Contains(treasureIdx) == true;
        }

        private static int CountPassedMainLine2Stages(Stage stage, MainLine2MainTable main)
        {
            HashSet<int> stageIds = ResolveMainLine2StageIds(main);
            return stageIds.Count(stageId => stage.Stages.TryGetValue(stageId, out StageDatum? stageData) && stageData.Passed);
        }

        private static HashSet<int> ResolveMainLine2StageIds(MainLine2MainTable main)
        {
            HashSet<int> chapterIds = main.ChapterIds.Where(chapterId => chapterId > 0).ToHashSet();
            HashSet<int> stageGroupIds = TableReaderV2.Parse<MainLine2ChapterTable>()
                .Where(chapter => chapterIds.Contains(chapter.ChapterId))
                .SelectMany(chapter => chapter.StageGroupIds)
                .Where(stageGroupId => stageGroupId > 0)
                .ToHashSet();

            return TableReaderV2.Parse<MainLine2StageGroupTable>()
                .Where(stageGroup => stageGroupIds.Contains(stageGroup.Id))
                .SelectMany(stageGroup => stageGroup.StageIds)
                .Where(stageId => stageId > 0)
                .ToHashSet();
        }

        private static void MergeMainLine2FirstPassTimes(FubenMainLine2Data data, Stage stage)
        {
            HashSet<int> mainLine2StageIds = TableReaderV2.Parse<MainLine2StageTable>()
                .Select(x => x.Id)
                .ToHashSet();
            foreach (StageDatum stageData in stage.Stages.Values)
            {
                if (!stageData.Passed || !mainLine2StageIds.Contains((int)stageData.StageId))
                {
                    continue;
                }

                data.FirstPassTime[(int)stageData.StageId] = stageData.CreateTime > 0
                    ? stageData.CreateTime
                    : stageData.LastPassTime;
            }
        }
    }

    public static class MainLineLuosaitaPayloadFactory
    {
        private const string CapturedLoginDataJson = "{\"IncId\":29,\"SectionInfos\":[{\"SectionId\":1,\"BlockInfos\":[{\"Id\":101,\"BlockStatus\":1},{\"Id\":102,\"BlockStatus\":1},{\"Id\":103,\"BlockStatus\":1},{\"Id\":104,\"BlockStatus\":1},{\"Id\":105,\"BlockStatus\":0},{\"Id\":106,\"BlockStatus\":0},{\"Id\":107,\"BlockStatus\":0},{\"Id\":108,\"BlockStatus\":0},{\"Id\":109,\"BlockStatus\":0},{\"Id\":110,\"BlockStatus\":0}],\"SectionMembers\":[{\"Guid\":1,\"Type\":1,\"BlockId\":104,\"PosId\":4,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":{\"Id\":101,\"CurHp\":8,\"ExtraAttack\":0}},{\"Guid\":9,\"Type\":4,\"BlockId\":104,\"PosId\":9,\"CharacterId\":0,\"StageId\":10380101,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":13,\"Type\":2,\"BlockId\":105,\"PosId\":13,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":204,\"CurHp\":9,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":14,\"Type\":2,\"BlockId\":106,\"PosId\":14,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":205,\"CurHp\":3,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":15,\"Type\":2,\"BlockId\":107,\"PosId\":15,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":206,\"CurHp\":7,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":16,\"Type\":2,\"BlockId\":108,\"PosId\":16,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":207,\"CurHp\":16,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":17,\"Type\":2,\"BlockId\":109,\"PosId\":17,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":208,\"CurHp\":17,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":26,\"Type\":2,\"BlockId\":107,\"PosId\":26,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":239,\"CurHp\":99,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":27,\"Type\":2,\"BlockId\":110,\"PosId\":27,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":240,\"CurHp\":99,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":28,\"Type\":3,\"BlockId\":104,\"PosId\":5,\"CharacterId\":3107,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":29,\"Type\":3,\"BlockId\":104,\"PosId\":6,\"CharacterId\":3101,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null}],\"DocList\":[],\"CharacterMoveIds\":[],\"SectionStatus\":0}],\"KillEnemySet\":[201,202,203]}";

        private static readonly IReadOnlyDictionary<string, string> CapturedSectionInfoJsonByRequest = new Dictionary<string, string>
        {
            ["Enter:2"] = "{\"SectionId\":2,\"BlockInfos\":[{\"Id\":201,\"BlockStatus\":1},{\"Id\":202,\"BlockStatus\":1},{\"Id\":205,\"BlockStatus\":1},{\"Id\":203,\"BlockStatus\":1},{\"Id\":204,\"BlockStatus\":1},{\"Id\":206,\"BlockStatus\":1},{\"Id\":207,\"BlockStatus\":0},{\"Id\":208,\"BlockStatus\":0},{\"Id\":209,\"BlockStatus\":0},{\"Id\":210,\"BlockStatus\":0},{\"Id\":211,\"BlockStatus\":0}],\"SectionMembers\":[{\"Guid\":66,\"Type\":1,\"BlockId\":203,\"PosId\":31,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":{\"Id\":102,\"CurHp\":7,\"ExtraAttack\":0}},{\"Guid\":68,\"Type\":3,\"BlockId\":204,\"PosId\":33,\"CharacterId\":3203,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":69,\"Type\":3,\"BlockId\":206,\"PosId\":34,\"CharacterId\":3202,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":70,\"Type\":2,\"BlockId\":207,\"PosId\":35,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":212,\"CurHp\":16,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":71,\"Type\":2,\"BlockId\":208,\"PosId\":36,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":211,\"CurHp\":7,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":72,\"Type\":2,\"BlockId\":209,\"PosId\":37,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":209,\"CurHp\":5,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":73,\"Type\":4,\"BlockId\":209,\"PosId\":38,\"CharacterId\":0,\"StageId\":10380108,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":76,\"Type\":2,\"BlockId\":210,\"PosId\":41,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":210,\"CurHp\":10,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":77,\"Type\":2,\"BlockId\":211,\"PosId\":42,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":213,\"CurHp\":19,\"ExtraAttack\":0},\"ArmyInfo\":null}],\"DocList\":[],\"CharacterMoveIds\":[],\"SectionStatus\":0}",
            ["Move:1:13:14"] = "{\"SectionId\":1,\"BlockInfos\":[{\"Id\":101,\"BlockStatus\":1},{\"Id\":102,\"BlockStatus\":1},{\"Id\":103,\"BlockStatus\":1},{\"Id\":104,\"BlockStatus\":1},{\"Id\":105,\"BlockStatus\":1},{\"Id\":106,\"BlockStatus\":1},{\"Id\":107,\"BlockStatus\":0},{\"Id\":108,\"BlockStatus\":0},{\"Id\":109,\"BlockStatus\":0},{\"Id\":110,\"BlockStatus\":0}],\"SectionMembers\":[{\"Guid\":1,\"Type\":1,\"BlockId\":106,\"PosId\":14,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":{\"Id\":101,\"CurHp\":8,\"ExtraAttack\":0}},{\"Guid\":9,\"Type\":4,\"BlockId\":104,\"PosId\":9,\"CharacterId\":0,\"StageId\":10380101,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":15,\"Type\":2,\"BlockId\":107,\"PosId\":15,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":206,\"CurHp\":7,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":16,\"Type\":2,\"BlockId\":108,\"PosId\":16,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":207,\"CurHp\":16,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":17,\"Type\":2,\"BlockId\":109,\"PosId\":17,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":208,\"CurHp\":17,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":26,\"Type\":2,\"BlockId\":107,\"PosId\":26,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":239,\"CurHp\":99,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":27,\"Type\":2,\"BlockId\":110,\"PosId\":27,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":240,\"CurHp\":99,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":30,\"Type\":4,\"BlockId\":104,\"PosId\":10,\"CharacterId\":0,\"StageId\":10380102,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":31,\"Type\":4,\"BlockId\":104,\"PosId\":11,\"CharacterId\":0,\"StageId\":10380103,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":34,\"Type\":3,\"BlockId\":104,\"PosId\":7,\"CharacterId\":3110,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":35,\"Type\":3,\"BlockId\":104,\"PosId\":8,\"CharacterId\":3108,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":36,\"Type\":4,\"BlockId\":104,\"PosId\":12,\"CharacterId\":0,\"StageId\":10380104,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":39,\"Type\":4,\"BlockId\":107,\"PosId\":20,\"CharacterId\":0,\"StageId\":10380105,\"EnemyInfo\":null,\"ArmyInfo\":null}],\"DocList\":[{\"Id\":1,\"Used\":true},{\"Id\":2,\"Used\":true},{\"Id\":18,\"Used\":true},{\"Id\":3,\"Used\":true},{\"Id\":4,\"Used\":true},{\"Id\":20,\"Used\":true},{\"Id\":5,\"Used\":true}],\"CharacterMoveIds\":[],\"SectionStatus\":0}",
            ["Move:1:14:15"] = "{\"SectionId\":1,\"BlockInfos\":[{\"Id\":101,\"BlockStatus\":1},{\"Id\":102,\"BlockStatus\":1},{\"Id\":103,\"BlockStatus\":1},{\"Id\":104,\"BlockStatus\":1},{\"Id\":105,\"BlockStatus\":1},{\"Id\":106,\"BlockStatus\":1},{\"Id\":107,\"BlockStatus\":0},{\"Id\":108,\"BlockStatus\":0},{\"Id\":109,\"BlockStatus\":0},{\"Id\":110,\"BlockStatus\":0}],\"SectionMembers\":[{\"Guid\":1,\"Type\":1,\"BlockId\":107,\"PosId\":15,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":{\"Id\":101,\"CurHp\":8,\"ExtraAttack\":0}},{\"Guid\":9,\"Type\":4,\"BlockId\":104,\"PosId\":9,\"CharacterId\":0,\"StageId\":10380101,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":16,\"Type\":2,\"BlockId\":108,\"PosId\":16,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":207,\"CurHp\":16,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":17,\"Type\":2,\"BlockId\":109,\"PosId\":17,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":208,\"CurHp\":17,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":26,\"Type\":2,\"BlockId\":107,\"PosId\":26,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":239,\"CurHp\":99,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":27,\"Type\":2,\"BlockId\":110,\"PosId\":27,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":240,\"CurHp\":99,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":30,\"Type\":4,\"BlockId\":104,\"PosId\":10,\"CharacterId\":0,\"StageId\":10380102,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":31,\"Type\":4,\"BlockId\":104,\"PosId\":11,\"CharacterId\":0,\"StageId\":10380103,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":34,\"Type\":3,\"BlockId\":104,\"PosId\":7,\"CharacterId\":3110,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":35,\"Type\":3,\"BlockId\":104,\"PosId\":8,\"CharacterId\":3108,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":36,\"Type\":4,\"BlockId\":104,\"PosId\":12,\"CharacterId\":0,\"StageId\":10380104,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":39,\"Type\":4,\"BlockId\":107,\"PosId\":20,\"CharacterId\":0,\"StageId\":10380105,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":48,\"Type\":3,\"BlockId\":107,\"PosId\":18,\"CharacterId\":3111,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":49,\"Type\":3,\"BlockId\":107,\"PosId\":19,\"CharacterId\":3104,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null}],\"DocList\":[{\"Id\":1,\"Used\":true},{\"Id\":2,\"Used\":true},{\"Id\":18,\"Used\":true},{\"Id\":3,\"Used\":true},{\"Id\":4,\"Used\":true},{\"Id\":20,\"Used\":true},{\"Id\":5,\"Used\":true}],\"CharacterMoveIds\":[],\"SectionStatus\":0}",
            ["Move:1:15:16"] = "{\"SectionId\":1,\"BlockInfos\":[{\"Id\":101,\"BlockStatus\":1},{\"Id\":102,\"BlockStatus\":1},{\"Id\":103,\"BlockStatus\":1},{\"Id\":104,\"BlockStatus\":1},{\"Id\":105,\"BlockStatus\":1},{\"Id\":106,\"BlockStatus\":1},{\"Id\":107,\"BlockStatus\":1},{\"Id\":108,\"BlockStatus\":1},{\"Id\":109,\"BlockStatus\":0},{\"Id\":110,\"BlockStatus\":0}],\"SectionMembers\":[{\"Guid\":1,\"Type\":1,\"BlockId\":108,\"PosId\":16,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":{\"Id\":101,\"CurHp\":7,\"ExtraAttack\":10}},{\"Guid\":9,\"Type\":4,\"BlockId\":104,\"PosId\":9,\"CharacterId\":0,\"StageId\":10380101,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":17,\"Type\":2,\"BlockId\":109,\"PosId\":17,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":208,\"CurHp\":17,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":27,\"Type\":2,\"BlockId\":110,\"PosId\":27,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":240,\"CurHp\":99,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":30,\"Type\":4,\"BlockId\":104,\"PosId\":10,\"CharacterId\":0,\"StageId\":10380102,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":31,\"Type\":4,\"BlockId\":104,\"PosId\":11,\"CharacterId\":0,\"StageId\":10380103,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":34,\"Type\":3,\"BlockId\":104,\"PosId\":7,\"CharacterId\":3110,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":35,\"Type\":3,\"BlockId\":104,\"PosId\":8,\"CharacterId\":3108,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":36,\"Type\":4,\"BlockId\":104,\"PosId\":12,\"CharacterId\":0,\"StageId\":10380104,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":39,\"Type\":4,\"BlockId\":107,\"PosId\":20,\"CharacterId\":0,\"StageId\":10380105,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":48,\"Type\":3,\"BlockId\":107,\"PosId\":18,\"CharacterId\":3111,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":49,\"Type\":3,\"BlockId\":107,\"PosId\":19,\"CharacterId\":3104,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":50,\"Type\":4,\"BlockId\":110,\"PosId\":24,\"CharacterId\":0,\"StageId\":10380106,\"EnemyInfo\":null,\"ArmyInfo\":null}],\"DocList\":[{\"Id\":1,\"Used\":true},{\"Id\":2,\"Used\":true},{\"Id\":18,\"Used\":true},{\"Id\":3,\"Used\":true},{\"Id\":4,\"Used\":true},{\"Id\":20,\"Used\":true},{\"Id\":5,\"Used\":true},{\"Id\":42,\"Used\":true}],\"CharacterMoveIds\":[],\"SectionStatus\":0}",
            ["Move:1:16:17"] = "{\"SectionId\":1,\"BlockInfos\":[{\"Id\":101,\"BlockStatus\":1},{\"Id\":102,\"BlockStatus\":1},{\"Id\":103,\"BlockStatus\":1},{\"Id\":104,\"BlockStatus\":1},{\"Id\":105,\"BlockStatus\":1},{\"Id\":106,\"BlockStatus\":1},{\"Id\":107,\"BlockStatus\":1},{\"Id\":108,\"BlockStatus\":1},{\"Id\":109,\"BlockStatus\":1},{\"Id\":110,\"BlockStatus\":0}],\"SectionMembers\":[{\"Guid\":1,\"Type\":1,\"BlockId\":109,\"PosId\":17,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":{\"Id\":101,\"CurHp\":7,\"ExtraAttack\":10}},{\"Guid\":9,\"Type\":4,\"BlockId\":104,\"PosId\":9,\"CharacterId\":0,\"StageId\":10380101,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":27,\"Type\":2,\"BlockId\":110,\"PosId\":27,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":240,\"CurHp\":99,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":30,\"Type\":4,\"BlockId\":104,\"PosId\":10,\"CharacterId\":0,\"StageId\":10380102,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":31,\"Type\":4,\"BlockId\":104,\"PosId\":11,\"CharacterId\":0,\"StageId\":10380103,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":34,\"Type\":3,\"BlockId\":104,\"PosId\":7,\"CharacterId\":3110,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":35,\"Type\":3,\"BlockId\":104,\"PosId\":8,\"CharacterId\":3108,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":36,\"Type\":4,\"BlockId\":104,\"PosId\":12,\"CharacterId\":0,\"StageId\":10380104,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":39,\"Type\":4,\"BlockId\":107,\"PosId\":20,\"CharacterId\":0,\"StageId\":10380105,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":48,\"Type\":3,\"BlockId\":107,\"PosId\":18,\"CharacterId\":3111,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":49,\"Type\":3,\"BlockId\":107,\"PosId\":19,\"CharacterId\":3104,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":50,\"Type\":4,\"BlockId\":110,\"PosId\":24,\"CharacterId\":0,\"StageId\":10380106,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":57,\"Type\":3,\"BlockId\":110,\"PosId\":21,\"CharacterId\":3105,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":58,\"Type\":3,\"BlockId\":110,\"PosId\":22,\"CharacterId\":3106,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":59,\"Type\":3,\"BlockId\":110,\"PosId\":23,\"CharacterId\":3103,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null}],\"DocList\":[{\"Id\":1,\"Used\":true},{\"Id\":2,\"Used\":true},{\"Id\":18,\"Used\":true},{\"Id\":3,\"Used\":true},{\"Id\":4,\"Used\":true},{\"Id\":20,\"Used\":true},{\"Id\":5,\"Used\":true},{\"Id\":42,\"Used\":true}],\"CharacterMoveIds\":[],\"SectionStatus\":0}",
            ["Move:1:4:13"] = "{\"SectionId\":1,\"BlockInfos\":[{\"Id\":101,\"BlockStatus\":1},{\"Id\":102,\"BlockStatus\":1},{\"Id\":103,\"BlockStatus\":1},{\"Id\":104,\"BlockStatus\":1},{\"Id\":105,\"BlockStatus\":1},{\"Id\":106,\"BlockStatus\":0},{\"Id\":107,\"BlockStatus\":0},{\"Id\":108,\"BlockStatus\":0},{\"Id\":109,\"BlockStatus\":0},{\"Id\":110,\"BlockStatus\":0}],\"SectionMembers\":[{\"Guid\":1,\"Type\":1,\"BlockId\":105,\"PosId\":13,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":{\"Id\":101,\"CurHp\":8,\"ExtraAttack\":0}},{\"Guid\":9,\"Type\":4,\"BlockId\":104,\"PosId\":9,\"CharacterId\":0,\"StageId\":10380101,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":14,\"Type\":2,\"BlockId\":106,\"PosId\":14,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":205,\"CurHp\":3,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":15,\"Type\":2,\"BlockId\":107,\"PosId\":15,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":206,\"CurHp\":7,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":16,\"Type\":2,\"BlockId\":108,\"PosId\":16,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":207,\"CurHp\":16,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":17,\"Type\":2,\"BlockId\":109,\"PosId\":17,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":208,\"CurHp\":17,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":26,\"Type\":2,\"BlockId\":107,\"PosId\":26,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":239,\"CurHp\":99,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":27,\"Type\":2,\"BlockId\":110,\"PosId\":27,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":240,\"CurHp\":99,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":30,\"Type\":4,\"BlockId\":104,\"PosId\":10,\"CharacterId\":0,\"StageId\":10380102,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":31,\"Type\":4,\"BlockId\":104,\"PosId\":11,\"CharacterId\":0,\"StageId\":10380103,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":34,\"Type\":3,\"BlockId\":104,\"PosId\":7,\"CharacterId\":3110,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":35,\"Type\":3,\"BlockId\":104,\"PosId\":8,\"CharacterId\":3108,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":36,\"Type\":4,\"BlockId\":104,\"PosId\":12,\"CharacterId\":0,\"StageId\":10380104,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":39,\"Type\":4,\"BlockId\":107,\"PosId\":20,\"CharacterId\":0,\"StageId\":10380105,\"EnemyInfo\":null,\"ArmyInfo\":null}],\"DocList\":[{\"Id\":1,\"Used\":true},{\"Id\":2,\"Used\":true},{\"Id\":18,\"Used\":true},{\"Id\":3,\"Used\":true},{\"Id\":4,\"Used\":true},{\"Id\":20,\"Used\":true},{\"Id\":5,\"Used\":true}],\"CharacterMoveIds\":[],\"SectionStatus\":0}",
            ["Move:2:31:37"] = "{\"SectionId\":2,\"BlockInfos\":[{\"Id\":201,\"BlockStatus\":1},{\"Id\":202,\"BlockStatus\":1},{\"Id\":205,\"BlockStatus\":1},{\"Id\":203,\"BlockStatus\":1},{\"Id\":204,\"BlockStatus\":1},{\"Id\":206,\"BlockStatus\":1},{\"Id\":207,\"BlockStatus\":0},{\"Id\":208,\"BlockStatus\":0},{\"Id\":209,\"BlockStatus\":1},{\"Id\":210,\"BlockStatus\":0},{\"Id\":211,\"BlockStatus\":0}],\"SectionMembers\":[{\"Guid\":66,\"Type\":1,\"BlockId\":209,\"PosId\":37,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":{\"Id\":102,\"CurHp\":7,\"ExtraAttack\":0}},{\"Guid\":68,\"Type\":3,\"BlockId\":204,\"PosId\":33,\"CharacterId\":3203,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":69,\"Type\":3,\"BlockId\":206,\"PosId\":34,\"CharacterId\":3202,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":70,\"Type\":2,\"BlockId\":207,\"PosId\":35,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":212,\"CurHp\":16,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":71,\"Type\":2,\"BlockId\":208,\"PosId\":36,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":211,\"CurHp\":7,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":73,\"Type\":4,\"BlockId\":209,\"PosId\":38,\"CharacterId\":0,\"StageId\":10380108,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":76,\"Type\":2,\"BlockId\":210,\"PosId\":41,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":210,\"CurHp\":10,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":77,\"Type\":2,\"BlockId\":211,\"PosId\":42,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":213,\"CurHp\":19,\"ExtraAttack\":0},\"ArmyInfo\":null}],\"DocList\":[],\"CharacterMoveIds\":[],\"SectionStatus\":0}",
            ["Move:2:31:41"] = "{\"SectionId\":2,\"BlockInfos\":[{\"Id\":201,\"BlockStatus\":1},{\"Id\":202,\"BlockStatus\":1},{\"Id\":205,\"BlockStatus\":1},{\"Id\":203,\"BlockStatus\":1},{\"Id\":204,\"BlockStatus\":1},{\"Id\":206,\"BlockStatus\":1},{\"Id\":207,\"BlockStatus\":0},{\"Id\":208,\"BlockStatus\":0},{\"Id\":209,\"BlockStatus\":1},{\"Id\":210,\"BlockStatus\":1},{\"Id\":211,\"BlockStatus\":0}],\"SectionMembers\":[{\"Guid\":66,\"Type\":1,\"BlockId\":209,\"PosId\":37,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":{\"Id\":102,\"CurHp\":7,\"ExtraAttack\":0}},{\"Guid\":68,\"Type\":3,\"BlockId\":204,\"PosId\":33,\"CharacterId\":3203,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":69,\"Type\":3,\"BlockId\":206,\"PosId\":34,\"CharacterId\":3202,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":70,\"Type\":2,\"BlockId\":207,\"PosId\":35,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":212,\"CurHp\":16,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":71,\"Type\":2,\"BlockId\":208,\"PosId\":36,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":211,\"CurHp\":7,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":73,\"Type\":4,\"BlockId\":209,\"PosId\":38,\"CharacterId\":0,\"StageId\":10380108,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":77,\"Type\":2,\"BlockId\":211,\"PosId\":42,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":213,\"CurHp\":19,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":84,\"Type\":4,\"BlockId\":209,\"PosId\":39,\"CharacterId\":0,\"StageId\":10380109,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":85,\"Type\":4,\"BlockId\":209,\"PosId\":40,\"CharacterId\":0,\"StageId\":10380110,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":86,\"Type\":4,\"BlockId\":211,\"PosId\":47,\"CharacterId\":0,\"StageId\":10380111,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":87,\"Type\":3,\"BlockId\":211,\"PosId\":44,\"CharacterId\":3201,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":88,\"Type\":1,\"BlockId\":210,\"PosId\":41,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":{\"Id\":103,\"CurHp\":10,\"ExtraAttack\":0}}],\"DocList\":[{\"Id\":10,\"Used\":true},{\"Id\":11,\"Used\":true},{\"Id\":33,\"Used\":true}],\"CharacterMoveIds\":[],\"SectionStatus\":0}",
            ["Move:2:35:42"] = "{\"SectionId\":2,\"BlockInfos\":[{\"Id\":201,\"BlockStatus\":1},{\"Id\":202,\"BlockStatus\":1},{\"Id\":205,\"BlockStatus\":1},{\"Id\":203,\"BlockStatus\":1},{\"Id\":204,\"BlockStatus\":1},{\"Id\":206,\"BlockStatus\":1},{\"Id\":207,\"BlockStatus\":1},{\"Id\":208,\"BlockStatus\":1},{\"Id\":209,\"BlockStatus\":1},{\"Id\":210,\"BlockStatus\":1},{\"Id\":211,\"BlockStatus\":1}],\"SectionMembers\":[{\"Guid\":66,\"Type\":1,\"BlockId\":211,\"PosId\":42,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":{\"Id\":102,\"CurHp\":7,\"ExtraAttack\":5}},{\"Guid\":68,\"Type\":3,\"BlockId\":204,\"PosId\":33,\"CharacterId\":3203,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":69,\"Type\":3,\"BlockId\":206,\"PosId\":34,\"CharacterId\":3202,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":73,\"Type\":4,\"BlockId\":209,\"PosId\":38,\"CharacterId\":0,\"StageId\":10380108,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":84,\"Type\":4,\"BlockId\":209,\"PosId\":39,\"CharacterId\":0,\"StageId\":10380109,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":85,\"Type\":4,\"BlockId\":209,\"PosId\":40,\"CharacterId\":0,\"StageId\":10380110,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":86,\"Type\":4,\"BlockId\":211,\"PosId\":47,\"CharacterId\":0,\"StageId\":10380111,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":87,\"Type\":3,\"BlockId\":211,\"PosId\":44,\"CharacterId\":3201,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":88,\"Type\":1,\"BlockId\":210,\"PosId\":41,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":{\"Id\":103,\"CurHp\":10,\"ExtraAttack\":0}},{\"Guid\":89,\"Type\":3,\"BlockId\":211,\"PosId\":43,\"CharacterId\":3205,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null}],\"DocList\":[{\"Id\":10,\"Used\":true},{\"Id\":11,\"Used\":true},{\"Id\":33,\"Used\":true},{\"Id\":30,\"Used\":true}],\"CharacterMoveIds\":[],\"SectionStatus\":0}",
            ["Move:2:36:35"] = "{\"SectionId\":2,\"BlockInfos\":[{\"Id\":201,\"BlockStatus\":1},{\"Id\":202,\"BlockStatus\":1},{\"Id\":205,\"BlockStatus\":1},{\"Id\":203,\"BlockStatus\":1},{\"Id\":204,\"BlockStatus\":1},{\"Id\":206,\"BlockStatus\":1},{\"Id\":207,\"BlockStatus\":1},{\"Id\":208,\"BlockStatus\":1},{\"Id\":209,\"BlockStatus\":1},{\"Id\":210,\"BlockStatus\":1},{\"Id\":211,\"BlockStatus\":0}],\"SectionMembers\":[{\"Guid\":66,\"Type\":1,\"BlockId\":207,\"PosId\":35,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":{\"Id\":102,\"CurHp\":7,\"ExtraAttack\":0}},{\"Guid\":68,\"Type\":3,\"BlockId\":204,\"PosId\":33,\"CharacterId\":3203,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":69,\"Type\":3,\"BlockId\":206,\"PosId\":34,\"CharacterId\":3202,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":73,\"Type\":4,\"BlockId\":209,\"PosId\":38,\"CharacterId\":0,\"StageId\":10380108,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":77,\"Type\":2,\"BlockId\":211,\"PosId\":42,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":213,\"CurHp\":19,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":84,\"Type\":4,\"BlockId\":209,\"PosId\":39,\"CharacterId\":0,\"StageId\":10380109,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":85,\"Type\":4,\"BlockId\":209,\"PosId\":40,\"CharacterId\":0,\"StageId\":10380110,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":86,\"Type\":4,\"BlockId\":211,\"PosId\":47,\"CharacterId\":0,\"StageId\":10380111,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":87,\"Type\":3,\"BlockId\":211,\"PosId\":44,\"CharacterId\":3201,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":88,\"Type\":1,\"BlockId\":210,\"PosId\":41,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":{\"Id\":103,\"CurHp\":10,\"ExtraAttack\":0}}],\"DocList\":[{\"Id\":10,\"Used\":true},{\"Id\":11,\"Used\":true},{\"Id\":33,\"Used\":true},{\"Id\":30,\"Used\":false}],\"CharacterMoveIds\":[],\"SectionStatus\":0}",
            ["Move:2:37:36"] = "{\"SectionId\":2,\"BlockInfos\":[{\"Id\":201,\"BlockStatus\":1},{\"Id\":202,\"BlockStatus\":1},{\"Id\":205,\"BlockStatus\":1},{\"Id\":203,\"BlockStatus\":1},{\"Id\":204,\"BlockStatus\":1},{\"Id\":206,\"BlockStatus\":1},{\"Id\":207,\"BlockStatus\":0},{\"Id\":208,\"BlockStatus\":1},{\"Id\":209,\"BlockStatus\":1},{\"Id\":210,\"BlockStatus\":1},{\"Id\":211,\"BlockStatus\":0}],\"SectionMembers\":[{\"Guid\":66,\"Type\":1,\"BlockId\":208,\"PosId\":36,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":{\"Id\":102,\"CurHp\":7,\"ExtraAttack\":0}},{\"Guid\":68,\"Type\":3,\"BlockId\":204,\"PosId\":33,\"CharacterId\":3203,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":69,\"Type\":3,\"BlockId\":206,\"PosId\":34,\"CharacterId\":3202,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":70,\"Type\":2,\"BlockId\":207,\"PosId\":35,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":212,\"CurHp\":16,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":73,\"Type\":4,\"BlockId\":209,\"PosId\":38,\"CharacterId\":0,\"StageId\":10380108,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":77,\"Type\":2,\"BlockId\":211,\"PosId\":42,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":213,\"CurHp\":19,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":84,\"Type\":4,\"BlockId\":209,\"PosId\":39,\"CharacterId\":0,\"StageId\":10380109,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":85,\"Type\":4,\"BlockId\":209,\"PosId\":40,\"CharacterId\":0,\"StageId\":10380110,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":86,\"Type\":4,\"BlockId\":211,\"PosId\":47,\"CharacterId\":0,\"StageId\":10380111,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":87,\"Type\":3,\"BlockId\":211,\"PosId\":44,\"CharacterId\":3201,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":88,\"Type\":1,\"BlockId\":210,\"PosId\":41,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":{\"Id\":103,\"CurHp\":10,\"ExtraAttack\":0}}],\"DocList\":[{\"Id\":10,\"Used\":true},{\"Id\":11,\"Used\":true},{\"Id\":33,\"Used\":true}],\"CharacterMoveIds\":[],\"SectionStatus\":0}",
            ["UseDoc:1:1"] = "{\"SectionId\":1,\"BlockInfos\":[{\"Id\":101,\"BlockStatus\":1},{\"Id\":102,\"BlockStatus\":1},{\"Id\":103,\"BlockStatus\":1},{\"Id\":104,\"BlockStatus\":1},{\"Id\":105,\"BlockStatus\":0},{\"Id\":106,\"BlockStatus\":0},{\"Id\":107,\"BlockStatus\":0},{\"Id\":108,\"BlockStatus\":0},{\"Id\":109,\"BlockStatus\":0},{\"Id\":110,\"BlockStatus\":0}],\"SectionMembers\":[{\"Guid\":1,\"Type\":1,\"BlockId\":104,\"PosId\":4,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":{\"Id\":101,\"CurHp\":8,\"ExtraAttack\":0}},{\"Guid\":9,\"Type\":4,\"BlockId\":104,\"PosId\":9,\"CharacterId\":0,\"StageId\":10380101,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":13,\"Type\":2,\"BlockId\":105,\"PosId\":13,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":204,\"CurHp\":9,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":14,\"Type\":2,\"BlockId\":106,\"PosId\":14,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":205,\"CurHp\":3,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":15,\"Type\":2,\"BlockId\":107,\"PosId\":15,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":206,\"CurHp\":7,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":16,\"Type\":2,\"BlockId\":108,\"PosId\":16,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":207,\"CurHp\":16,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":17,\"Type\":2,\"BlockId\":109,\"PosId\":17,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":208,\"CurHp\":17,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":26,\"Type\":2,\"BlockId\":107,\"PosId\":26,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":239,\"CurHp\":99,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":27,\"Type\":2,\"BlockId\":110,\"PosId\":27,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":240,\"CurHp\":99,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":30,\"Type\":4,\"BlockId\":104,\"PosId\":10,\"CharacterId\":0,\"StageId\":10380102,\"EnemyInfo\":null,\"ArmyInfo\":null}],\"DocList\":[{\"Id\":1,\"Used\":true}],\"CharacterMoveIds\":[],\"SectionStatus\":0}",
            ["UseDoc:1:18"] = "{\"SectionId\":1,\"BlockInfos\":[{\"Id\":101,\"BlockStatus\":1},{\"Id\":102,\"BlockStatus\":1},{\"Id\":103,\"BlockStatus\":1},{\"Id\":104,\"BlockStatus\":1},{\"Id\":105,\"BlockStatus\":0},{\"Id\":106,\"BlockStatus\":0},{\"Id\":107,\"BlockStatus\":0},{\"Id\":108,\"BlockStatus\":0},{\"Id\":109,\"BlockStatus\":0},{\"Id\":110,\"BlockStatus\":0}],\"SectionMembers\":[{\"Guid\":1,\"Type\":1,\"BlockId\":104,\"PosId\":4,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":{\"Id\":101,\"CurHp\":8,\"ExtraAttack\":0}},{\"Guid\":9,\"Type\":4,\"BlockId\":104,\"PosId\":9,\"CharacterId\":0,\"StageId\":10380101,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":13,\"Type\":2,\"BlockId\":105,\"PosId\":13,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":204,\"CurHp\":9,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":14,\"Type\":2,\"BlockId\":106,\"PosId\":14,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":205,\"CurHp\":3,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":15,\"Type\":2,\"BlockId\":107,\"PosId\":15,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":206,\"CurHp\":7,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":16,\"Type\":2,\"BlockId\":108,\"PosId\":16,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":207,\"CurHp\":16,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":17,\"Type\":2,\"BlockId\":109,\"PosId\":17,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":208,\"CurHp\":17,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":26,\"Type\":2,\"BlockId\":107,\"PosId\":26,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":239,\"CurHp\":99,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":27,\"Type\":2,\"BlockId\":110,\"PosId\":27,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":240,\"CurHp\":99,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":30,\"Type\":4,\"BlockId\":104,\"PosId\":10,\"CharacterId\":0,\"StageId\":10380102,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":31,\"Type\":4,\"BlockId\":104,\"PosId\":11,\"CharacterId\":0,\"StageId\":10380103,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":34,\"Type\":3,\"BlockId\":104,\"PosId\":7,\"CharacterId\":3110,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":35,\"Type\":3,\"BlockId\":104,\"PosId\":8,\"CharacterId\":3108,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null}],\"DocList\":[{\"Id\":1,\"Used\":true},{\"Id\":2,\"Used\":true},{\"Id\":18,\"Used\":true}],\"CharacterMoveIds\":[],\"SectionStatus\":0}",
            ["UseDoc:1:2"] = "{\"SectionId\":1,\"BlockInfos\":[{\"Id\":101,\"BlockStatus\":1},{\"Id\":102,\"BlockStatus\":1},{\"Id\":103,\"BlockStatus\":1},{\"Id\":104,\"BlockStatus\":1},{\"Id\":105,\"BlockStatus\":0},{\"Id\":106,\"BlockStatus\":0},{\"Id\":107,\"BlockStatus\":0},{\"Id\":108,\"BlockStatus\":0},{\"Id\":109,\"BlockStatus\":0},{\"Id\":110,\"BlockStatus\":0}],\"SectionMembers\":[{\"Guid\":1,\"Type\":1,\"BlockId\":104,\"PosId\":4,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":{\"Id\":101,\"CurHp\":8,\"ExtraAttack\":0}},{\"Guid\":9,\"Type\":4,\"BlockId\":104,\"PosId\":9,\"CharacterId\":0,\"StageId\":10380101,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":13,\"Type\":2,\"BlockId\":105,\"PosId\":13,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":204,\"CurHp\":9,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":14,\"Type\":2,\"BlockId\":106,\"PosId\":14,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":205,\"CurHp\":3,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":15,\"Type\":2,\"BlockId\":107,\"PosId\":15,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":206,\"CurHp\":7,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":16,\"Type\":2,\"BlockId\":108,\"PosId\":16,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":207,\"CurHp\":16,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":17,\"Type\":2,\"BlockId\":109,\"PosId\":17,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":208,\"CurHp\":17,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":26,\"Type\":2,\"BlockId\":107,\"PosId\":26,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":239,\"CurHp\":99,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":27,\"Type\":2,\"BlockId\":110,\"PosId\":27,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":240,\"CurHp\":99,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":30,\"Type\":4,\"BlockId\":104,\"PosId\":10,\"CharacterId\":0,\"StageId\":10380102,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":31,\"Type\":4,\"BlockId\":104,\"PosId\":11,\"CharacterId\":0,\"StageId\":10380103,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":34,\"Type\":3,\"BlockId\":104,\"PosId\":7,\"CharacterId\":3110,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":35,\"Type\":3,\"BlockId\":104,\"PosId\":8,\"CharacterId\":3108,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null}],\"DocList\":[{\"Id\":1,\"Used\":true},{\"Id\":2,\"Used\":true},{\"Id\":18,\"Used\":false}],\"CharacterMoveIds\":[],\"SectionStatus\":0}",
            ["UseDoc:1:20"] = "{\"SectionId\":1,\"BlockInfos\":[{\"Id\":101,\"BlockStatus\":1},{\"Id\":102,\"BlockStatus\":1},{\"Id\":103,\"BlockStatus\":1},{\"Id\":104,\"BlockStatus\":1},{\"Id\":105,\"BlockStatus\":0},{\"Id\":106,\"BlockStatus\":0},{\"Id\":107,\"BlockStatus\":0},{\"Id\":108,\"BlockStatus\":0},{\"Id\":109,\"BlockStatus\":0},{\"Id\":110,\"BlockStatus\":0}],\"SectionMembers\":[{\"Guid\":1,\"Type\":1,\"BlockId\":104,\"PosId\":4,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":{\"Id\":101,\"CurHp\":8,\"ExtraAttack\":0}},{\"Guid\":9,\"Type\":4,\"BlockId\":104,\"PosId\":9,\"CharacterId\":0,\"StageId\":10380101,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":13,\"Type\":2,\"BlockId\":105,\"PosId\":13,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":204,\"CurHp\":9,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":14,\"Type\":2,\"BlockId\":106,\"PosId\":14,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":205,\"CurHp\":3,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":15,\"Type\":2,\"BlockId\":107,\"PosId\":15,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":206,\"CurHp\":7,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":16,\"Type\":2,\"BlockId\":108,\"PosId\":16,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":207,\"CurHp\":16,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":17,\"Type\":2,\"BlockId\":109,\"PosId\":17,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":208,\"CurHp\":17,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":26,\"Type\":2,\"BlockId\":107,\"PosId\":26,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":239,\"CurHp\":99,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":27,\"Type\":2,\"BlockId\":110,\"PosId\":27,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":240,\"CurHp\":99,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":30,\"Type\":4,\"BlockId\":104,\"PosId\":10,\"CharacterId\":0,\"StageId\":10380102,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":31,\"Type\":4,\"BlockId\":104,\"PosId\":11,\"CharacterId\":0,\"StageId\":10380103,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":34,\"Type\":3,\"BlockId\":104,\"PosId\":7,\"CharacterId\":3110,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":35,\"Type\":3,\"BlockId\":104,\"PosId\":8,\"CharacterId\":3108,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":36,\"Type\":4,\"BlockId\":104,\"PosId\":12,\"CharacterId\":0,\"StageId\":10380104,\"EnemyInfo\":null,\"ArmyInfo\":null}],\"DocList\":[{\"Id\":1,\"Used\":true},{\"Id\":2,\"Used\":true},{\"Id\":18,\"Used\":true},{\"Id\":3,\"Used\":true},{\"Id\":4,\"Used\":true},{\"Id\":20,\"Used\":true}],\"CharacterMoveIds\":[],\"SectionStatus\":0}",
            ["UseDoc:1:3"] = "{\"SectionId\":1,\"BlockInfos\":[{\"Id\":101,\"BlockStatus\":1},{\"Id\":102,\"BlockStatus\":1},{\"Id\":103,\"BlockStatus\":1},{\"Id\":104,\"BlockStatus\":1},{\"Id\":105,\"BlockStatus\":0},{\"Id\":106,\"BlockStatus\":0},{\"Id\":107,\"BlockStatus\":0},{\"Id\":108,\"BlockStatus\":0},{\"Id\":109,\"BlockStatus\":0},{\"Id\":110,\"BlockStatus\":0}],\"SectionMembers\":[{\"Guid\":1,\"Type\":1,\"BlockId\":104,\"PosId\":4,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":{\"Id\":101,\"CurHp\":8,\"ExtraAttack\":0}},{\"Guid\":9,\"Type\":4,\"BlockId\":104,\"PosId\":9,\"CharacterId\":0,\"StageId\":10380101,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":13,\"Type\":2,\"BlockId\":105,\"PosId\":13,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":204,\"CurHp\":9,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":14,\"Type\":2,\"BlockId\":106,\"PosId\":14,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":205,\"CurHp\":3,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":15,\"Type\":2,\"BlockId\":107,\"PosId\":15,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":206,\"CurHp\":7,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":16,\"Type\":2,\"BlockId\":108,\"PosId\":16,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":207,\"CurHp\":16,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":17,\"Type\":2,\"BlockId\":109,\"PosId\":17,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":208,\"CurHp\":17,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":26,\"Type\":2,\"BlockId\":107,\"PosId\":26,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":239,\"CurHp\":99,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":27,\"Type\":2,\"BlockId\":110,\"PosId\":27,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":240,\"CurHp\":99,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":30,\"Type\":4,\"BlockId\":104,\"PosId\":10,\"CharacterId\":0,\"StageId\":10380102,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":31,\"Type\":4,\"BlockId\":104,\"PosId\":11,\"CharacterId\":0,\"StageId\":10380103,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":34,\"Type\":3,\"BlockId\":104,\"PosId\":7,\"CharacterId\":3110,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":35,\"Type\":3,\"BlockId\":104,\"PosId\":8,\"CharacterId\":3108,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":36,\"Type\":4,\"BlockId\":104,\"PosId\":12,\"CharacterId\":0,\"StageId\":10380104,\"EnemyInfo\":null,\"ArmyInfo\":null}],\"DocList\":[{\"Id\":1,\"Used\":true},{\"Id\":2,\"Used\":true},{\"Id\":18,\"Used\":true},{\"Id\":3,\"Used\":true},{\"Id\":4,\"Used\":false},{\"Id\":20,\"Used\":false}],\"CharacterMoveIds\":[],\"SectionStatus\":0}",
            ["UseDoc:1:4"] = "{\"SectionId\":1,\"BlockInfos\":[{\"Id\":101,\"BlockStatus\":1},{\"Id\":102,\"BlockStatus\":1},{\"Id\":103,\"BlockStatus\":1},{\"Id\":104,\"BlockStatus\":1},{\"Id\":105,\"BlockStatus\":0},{\"Id\":106,\"BlockStatus\":0},{\"Id\":107,\"BlockStatus\":0},{\"Id\":108,\"BlockStatus\":0},{\"Id\":109,\"BlockStatus\":0},{\"Id\":110,\"BlockStatus\":0}],\"SectionMembers\":[{\"Guid\":1,\"Type\":1,\"BlockId\":104,\"PosId\":4,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":{\"Id\":101,\"CurHp\":8,\"ExtraAttack\":0}},{\"Guid\":9,\"Type\":4,\"BlockId\":104,\"PosId\":9,\"CharacterId\":0,\"StageId\":10380101,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":13,\"Type\":2,\"BlockId\":105,\"PosId\":13,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":204,\"CurHp\":9,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":14,\"Type\":2,\"BlockId\":106,\"PosId\":14,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":205,\"CurHp\":3,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":15,\"Type\":2,\"BlockId\":107,\"PosId\":15,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":206,\"CurHp\":7,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":16,\"Type\":2,\"BlockId\":108,\"PosId\":16,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":207,\"CurHp\":16,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":17,\"Type\":2,\"BlockId\":109,\"PosId\":17,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":208,\"CurHp\":17,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":26,\"Type\":2,\"BlockId\":107,\"PosId\":26,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":239,\"CurHp\":99,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":27,\"Type\":2,\"BlockId\":110,\"PosId\":27,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":240,\"CurHp\":99,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":30,\"Type\":4,\"BlockId\":104,\"PosId\":10,\"CharacterId\":0,\"StageId\":10380102,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":31,\"Type\":4,\"BlockId\":104,\"PosId\":11,\"CharacterId\":0,\"StageId\":10380103,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":34,\"Type\":3,\"BlockId\":104,\"PosId\":7,\"CharacterId\":3110,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":35,\"Type\":3,\"BlockId\":104,\"PosId\":8,\"CharacterId\":3108,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":36,\"Type\":4,\"BlockId\":104,\"PosId\":12,\"CharacterId\":0,\"StageId\":10380104,\"EnemyInfo\":null,\"ArmyInfo\":null}],\"DocList\":[{\"Id\":1,\"Used\":true},{\"Id\":2,\"Used\":true},{\"Id\":18,\"Used\":true},{\"Id\":3,\"Used\":true},{\"Id\":4,\"Used\":true},{\"Id\":20,\"Used\":false}],\"CharacterMoveIds\":[],\"SectionStatus\":0}",
            ["UseDoc:1:42"] = "{\"SectionId\":1,\"BlockInfos\":[{\"Id\":101,\"BlockStatus\":1},{\"Id\":102,\"BlockStatus\":1},{\"Id\":103,\"BlockStatus\":1},{\"Id\":104,\"BlockStatus\":1},{\"Id\":105,\"BlockStatus\":1},{\"Id\":106,\"BlockStatus\":1},{\"Id\":107,\"BlockStatus\":1},{\"Id\":108,\"BlockStatus\":0},{\"Id\":109,\"BlockStatus\":0},{\"Id\":110,\"BlockStatus\":0}],\"SectionMembers\":[{\"Guid\":1,\"Type\":1,\"BlockId\":107,\"PosId\":15,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":{\"Id\":101,\"CurHp\":10,\"ExtraAttack\":10}},{\"Guid\":9,\"Type\":4,\"BlockId\":104,\"PosId\":9,\"CharacterId\":0,\"StageId\":10380101,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":16,\"Type\":2,\"BlockId\":108,\"PosId\":16,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":207,\"CurHp\":16,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":17,\"Type\":2,\"BlockId\":109,\"PosId\":17,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":208,\"CurHp\":17,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":27,\"Type\":2,\"BlockId\":110,\"PosId\":27,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":240,\"CurHp\":99,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":30,\"Type\":4,\"BlockId\":104,\"PosId\":10,\"CharacterId\":0,\"StageId\":10380102,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":31,\"Type\":4,\"BlockId\":104,\"PosId\":11,\"CharacterId\":0,\"StageId\":10380103,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":34,\"Type\":3,\"BlockId\":104,\"PosId\":7,\"CharacterId\":3110,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":35,\"Type\":3,\"BlockId\":104,\"PosId\":8,\"CharacterId\":3108,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":36,\"Type\":4,\"BlockId\":104,\"PosId\":12,\"CharacterId\":0,\"StageId\":10380104,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":39,\"Type\":4,\"BlockId\":107,\"PosId\":20,\"CharacterId\":0,\"StageId\":10380105,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":48,\"Type\":3,\"BlockId\":107,\"PosId\":18,\"CharacterId\":3111,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":49,\"Type\":3,\"BlockId\":107,\"PosId\":19,\"CharacterId\":3104,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":50,\"Type\":4,\"BlockId\":110,\"PosId\":24,\"CharacterId\":0,\"StageId\":10380106,\"EnemyInfo\":null,\"ArmyInfo\":null}],\"DocList\":[{\"Id\":1,\"Used\":true},{\"Id\":2,\"Used\":true},{\"Id\":18,\"Used\":true},{\"Id\":3,\"Used\":true},{\"Id\":4,\"Used\":true},{\"Id\":20,\"Used\":true},{\"Id\":5,\"Used\":true},{\"Id\":42,\"Used\":true}],\"CharacterMoveIds\":[],\"SectionStatus\":0}",
            ["UseDoc:1:5"] = "{\"SectionId\":1,\"BlockInfos\":[{\"Id\":101,\"BlockStatus\":1},{\"Id\":102,\"BlockStatus\":1},{\"Id\":103,\"BlockStatus\":1},{\"Id\":104,\"BlockStatus\":1},{\"Id\":105,\"BlockStatus\":0},{\"Id\":106,\"BlockStatus\":0},{\"Id\":107,\"BlockStatus\":0},{\"Id\":108,\"BlockStatus\":0},{\"Id\":109,\"BlockStatus\":0},{\"Id\":110,\"BlockStatus\":0}],\"SectionMembers\":[{\"Guid\":1,\"Type\":1,\"BlockId\":104,\"PosId\":4,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":{\"Id\":101,\"CurHp\":8,\"ExtraAttack\":0}},{\"Guid\":9,\"Type\":4,\"BlockId\":104,\"PosId\":9,\"CharacterId\":0,\"StageId\":10380101,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":13,\"Type\":2,\"BlockId\":105,\"PosId\":13,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":204,\"CurHp\":9,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":14,\"Type\":2,\"BlockId\":106,\"PosId\":14,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":205,\"CurHp\":3,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":15,\"Type\":2,\"BlockId\":107,\"PosId\":15,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":206,\"CurHp\":7,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":16,\"Type\":2,\"BlockId\":108,\"PosId\":16,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":207,\"CurHp\":16,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":17,\"Type\":2,\"BlockId\":109,\"PosId\":17,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":208,\"CurHp\":17,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":26,\"Type\":2,\"BlockId\":107,\"PosId\":26,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":239,\"CurHp\":99,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":27,\"Type\":2,\"BlockId\":110,\"PosId\":27,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":240,\"CurHp\":99,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":30,\"Type\":4,\"BlockId\":104,\"PosId\":10,\"CharacterId\":0,\"StageId\":10380102,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":31,\"Type\":4,\"BlockId\":104,\"PosId\":11,\"CharacterId\":0,\"StageId\":10380103,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":34,\"Type\":3,\"BlockId\":104,\"PosId\":7,\"CharacterId\":3110,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":35,\"Type\":3,\"BlockId\":104,\"PosId\":8,\"CharacterId\":3108,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":36,\"Type\":4,\"BlockId\":104,\"PosId\":12,\"CharacterId\":0,\"StageId\":10380104,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":39,\"Type\":4,\"BlockId\":107,\"PosId\":20,\"CharacterId\":0,\"StageId\":10380105,\"EnemyInfo\":null,\"ArmyInfo\":null}],\"DocList\":[{\"Id\":1,\"Used\":true},{\"Id\":2,\"Used\":true},{\"Id\":18,\"Used\":true},{\"Id\":3,\"Used\":true},{\"Id\":4,\"Used\":true},{\"Id\":20,\"Used\":true},{\"Id\":5,\"Used\":true}],\"CharacterMoveIds\":[],\"SectionStatus\":0}",
            ["UseDoc:1:6"] = "{\"SectionId\":1,\"BlockInfos\":[{\"Id\":101,\"BlockStatus\":1},{\"Id\":102,\"BlockStatus\":1},{\"Id\":103,\"BlockStatus\":1},{\"Id\":104,\"BlockStatus\":1},{\"Id\":105,\"BlockStatus\":1},{\"Id\":106,\"BlockStatus\":1},{\"Id\":107,\"BlockStatus\":1},{\"Id\":108,\"BlockStatus\":1},{\"Id\":109,\"BlockStatus\":1},{\"Id\":110,\"BlockStatus\":1}],\"SectionMembers\":[{\"Guid\":1,\"Type\":1,\"BlockId\":109,\"PosId\":17,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":{\"Id\":101,\"CurHp\":7,\"ExtraAttack\":10}},{\"Guid\":9,\"Type\":4,\"BlockId\":104,\"PosId\":9,\"CharacterId\":0,\"StageId\":10380101,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":30,\"Type\":4,\"BlockId\":104,\"PosId\":10,\"CharacterId\":0,\"StageId\":10380102,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":31,\"Type\":4,\"BlockId\":104,\"PosId\":11,\"CharacterId\":0,\"StageId\":10380103,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":34,\"Type\":3,\"BlockId\":104,\"PosId\":7,\"CharacterId\":3110,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":35,\"Type\":3,\"BlockId\":104,\"PosId\":8,\"CharacterId\":3108,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":36,\"Type\":4,\"BlockId\":104,\"PosId\":12,\"CharacterId\":0,\"StageId\":10380104,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":39,\"Type\":4,\"BlockId\":107,\"PosId\":20,\"CharacterId\":0,\"StageId\":10380105,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":48,\"Type\":3,\"BlockId\":107,\"PosId\":18,\"CharacterId\":3111,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":49,\"Type\":3,\"BlockId\":107,\"PosId\":19,\"CharacterId\":3104,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":50,\"Type\":4,\"BlockId\":110,\"PosId\":24,\"CharacterId\":0,\"StageId\":10380106,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":57,\"Type\":3,\"BlockId\":110,\"PosId\":21,\"CharacterId\":3105,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":58,\"Type\":3,\"BlockId\":110,\"PosId\":22,\"CharacterId\":3106,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":59,\"Type\":3,\"BlockId\":110,\"PosId\":23,\"CharacterId\":3103,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":60,\"Type\":4,\"BlockId\":110,\"PosId\":25,\"CharacterId\":0,\"StageId\":10380107,\"EnemyInfo\":null,\"ArmyInfo\":null}],\"DocList\":[{\"Id\":1,\"Used\":true},{\"Id\":2,\"Used\":true},{\"Id\":18,\"Used\":true},{\"Id\":3,\"Used\":true},{\"Id\":4,\"Used\":true},{\"Id\":20,\"Used\":true},{\"Id\":5,\"Used\":true},{\"Id\":42,\"Used\":true},{\"Id\":6,\"Used\":true},{\"Id\":7,\"Used\":false},{\"Id\":8,\"Used\":false}],\"CharacterMoveIds\":[],\"SectionStatus\":0}",
            ["UseDoc:1:7"] = "{\"SectionId\":1,\"BlockInfos\":[{\"Id\":101,\"BlockStatus\":1},{\"Id\":102,\"BlockStatus\":1},{\"Id\":103,\"BlockStatus\":1},{\"Id\":104,\"BlockStatus\":1},{\"Id\":105,\"BlockStatus\":1},{\"Id\":106,\"BlockStatus\":1},{\"Id\":107,\"BlockStatus\":1},{\"Id\":108,\"BlockStatus\":1},{\"Id\":109,\"BlockStatus\":1},{\"Id\":110,\"BlockStatus\":1}],\"SectionMembers\":[{\"Guid\":1,\"Type\":1,\"BlockId\":109,\"PosId\":17,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":{\"Id\":101,\"CurHp\":7,\"ExtraAttack\":10}},{\"Guid\":9,\"Type\":4,\"BlockId\":104,\"PosId\":9,\"CharacterId\":0,\"StageId\":10380101,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":30,\"Type\":4,\"BlockId\":104,\"PosId\":10,\"CharacterId\":0,\"StageId\":10380102,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":31,\"Type\":4,\"BlockId\":104,\"PosId\":11,\"CharacterId\":0,\"StageId\":10380103,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":34,\"Type\":3,\"BlockId\":104,\"PosId\":7,\"CharacterId\":3110,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":35,\"Type\":3,\"BlockId\":104,\"PosId\":8,\"CharacterId\":3108,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":36,\"Type\":4,\"BlockId\":104,\"PosId\":12,\"CharacterId\":0,\"StageId\":10380104,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":39,\"Type\":4,\"BlockId\":107,\"PosId\":20,\"CharacterId\":0,\"StageId\":10380105,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":48,\"Type\":3,\"BlockId\":107,\"PosId\":18,\"CharacterId\":3111,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":49,\"Type\":3,\"BlockId\":107,\"PosId\":19,\"CharacterId\":3104,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":50,\"Type\":4,\"BlockId\":110,\"PosId\":24,\"CharacterId\":0,\"StageId\":10380106,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":57,\"Type\":3,\"BlockId\":110,\"PosId\":21,\"CharacterId\":3105,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":58,\"Type\":3,\"BlockId\":110,\"PosId\":22,\"CharacterId\":3106,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":59,\"Type\":3,\"BlockId\":110,\"PosId\":23,\"CharacterId\":3103,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":60,\"Type\":4,\"BlockId\":110,\"PosId\":25,\"CharacterId\":0,\"StageId\":10380107,\"EnemyInfo\":null,\"ArmyInfo\":null}],\"DocList\":[{\"Id\":1,\"Used\":true},{\"Id\":2,\"Used\":true},{\"Id\":18,\"Used\":true},{\"Id\":3,\"Used\":true},{\"Id\":4,\"Used\":true},{\"Id\":20,\"Used\":true},{\"Id\":5,\"Used\":true},{\"Id\":42,\"Used\":true},{\"Id\":6,\"Used\":true},{\"Id\":7,\"Used\":true},{\"Id\":8,\"Used\":false}],\"CharacterMoveIds\":[],\"SectionStatus\":0}",
            ["UseDoc:1:8"] = "{\"SectionId\":1,\"BlockInfos\":[{\"Id\":101,\"BlockStatus\":1},{\"Id\":102,\"BlockStatus\":1},{\"Id\":103,\"BlockStatus\":1},{\"Id\":104,\"BlockStatus\":1},{\"Id\":105,\"BlockStatus\":1},{\"Id\":106,\"BlockStatus\":1},{\"Id\":107,\"BlockStatus\":1},{\"Id\":108,\"BlockStatus\":1},{\"Id\":109,\"BlockStatus\":1},{\"Id\":110,\"BlockStatus\":1}],\"SectionMembers\":[{\"Guid\":1,\"Type\":1,\"BlockId\":109,\"PosId\":17,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":{\"Id\":101,\"CurHp\":7,\"ExtraAttack\":10}},{\"Guid\":9,\"Type\":4,\"BlockId\":104,\"PosId\":9,\"CharacterId\":0,\"StageId\":10380101,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":30,\"Type\":4,\"BlockId\":104,\"PosId\":10,\"CharacterId\":0,\"StageId\":10380102,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":31,\"Type\":4,\"BlockId\":104,\"PosId\":11,\"CharacterId\":0,\"StageId\":10380103,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":34,\"Type\":3,\"BlockId\":104,\"PosId\":7,\"CharacterId\":3110,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":35,\"Type\":3,\"BlockId\":104,\"PosId\":8,\"CharacterId\":3108,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":36,\"Type\":4,\"BlockId\":104,\"PosId\":12,\"CharacterId\":0,\"StageId\":10380104,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":39,\"Type\":4,\"BlockId\":107,\"PosId\":20,\"CharacterId\":0,\"StageId\":10380105,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":48,\"Type\":3,\"BlockId\":107,\"PosId\":18,\"CharacterId\":3111,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":49,\"Type\":3,\"BlockId\":107,\"PosId\":19,\"CharacterId\":3104,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":50,\"Type\":4,\"BlockId\":110,\"PosId\":24,\"CharacterId\":0,\"StageId\":10380106,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":57,\"Type\":3,\"BlockId\":110,\"PosId\":21,\"CharacterId\":3105,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":58,\"Type\":3,\"BlockId\":110,\"PosId\":22,\"CharacterId\":3106,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":59,\"Type\":3,\"BlockId\":110,\"PosId\":23,\"CharacterId\":3103,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":60,\"Type\":4,\"BlockId\":110,\"PosId\":25,\"CharacterId\":0,\"StageId\":10380107,\"EnemyInfo\":null,\"ArmyInfo\":null}],\"DocList\":[{\"Id\":1,\"Used\":true},{\"Id\":2,\"Used\":true},{\"Id\":18,\"Used\":true},{\"Id\":3,\"Used\":true},{\"Id\":4,\"Used\":true},{\"Id\":20,\"Used\":true},{\"Id\":5,\"Used\":true},{\"Id\":42,\"Used\":true},{\"Id\":6,\"Used\":true},{\"Id\":7,\"Used\":true},{\"Id\":8,\"Used\":true}],\"CharacterMoveIds\":[],\"SectionStatus\":0}",
            ["UseDoc:1:9"] = "{\"SectionId\":1,\"BlockInfos\":[{\"Id\":101,\"BlockStatus\":1},{\"Id\":102,\"BlockStatus\":1},{\"Id\":103,\"BlockStatus\":1},{\"Id\":104,\"BlockStatus\":1},{\"Id\":105,\"BlockStatus\":1},{\"Id\":106,\"BlockStatus\":1},{\"Id\":107,\"BlockStatus\":1},{\"Id\":108,\"BlockStatus\":1},{\"Id\":109,\"BlockStatus\":1},{\"Id\":110,\"BlockStatus\":1}],\"SectionMembers\":[{\"Guid\":1,\"Type\":1,\"BlockId\":109,\"PosId\":17,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":{\"Id\":101,\"CurHp\":7,\"ExtraAttack\":10}},{\"Guid\":9,\"Type\":4,\"BlockId\":104,\"PosId\":9,\"CharacterId\":0,\"StageId\":10380101,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":30,\"Type\":4,\"BlockId\":104,\"PosId\":10,\"CharacterId\":0,\"StageId\":10380102,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":31,\"Type\":4,\"BlockId\":104,\"PosId\":11,\"CharacterId\":0,\"StageId\":10380103,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":34,\"Type\":3,\"BlockId\":104,\"PosId\":7,\"CharacterId\":3110,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":35,\"Type\":3,\"BlockId\":104,\"PosId\":8,\"CharacterId\":3108,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":36,\"Type\":4,\"BlockId\":104,\"PosId\":12,\"CharacterId\":0,\"StageId\":10380104,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":39,\"Type\":4,\"BlockId\":107,\"PosId\":20,\"CharacterId\":0,\"StageId\":10380105,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":48,\"Type\":3,\"BlockId\":107,\"PosId\":18,\"CharacterId\":3111,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":49,\"Type\":3,\"BlockId\":107,\"PosId\":19,\"CharacterId\":3104,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":50,\"Type\":4,\"BlockId\":110,\"PosId\":24,\"CharacterId\":0,\"StageId\":10380106,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":57,\"Type\":3,\"BlockId\":110,\"PosId\":21,\"CharacterId\":3105,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":58,\"Type\":3,\"BlockId\":110,\"PosId\":22,\"CharacterId\":3106,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":59,\"Type\":3,\"BlockId\":110,\"PosId\":23,\"CharacterId\":3103,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":60,\"Type\":4,\"BlockId\":110,\"PosId\":25,\"CharacterId\":0,\"StageId\":10380107,\"EnemyInfo\":null,\"ArmyInfo\":null}],\"DocList\":[{\"Id\":1,\"Used\":true},{\"Id\":2,\"Used\":true},{\"Id\":18,\"Used\":true},{\"Id\":3,\"Used\":true},{\"Id\":4,\"Used\":true},{\"Id\":20,\"Used\":true},{\"Id\":5,\"Used\":true},{\"Id\":42,\"Used\":true},{\"Id\":6,\"Used\":true},{\"Id\":7,\"Used\":true},{\"Id\":8,\"Used\":true},{\"Id\":9,\"Used\":true}],\"CharacterMoveIds\":[],\"SectionStatus\":1}",
            ["UseDoc:2:10"] = "{\"SectionId\":2,\"BlockInfos\":[{\"Id\":201,\"BlockStatus\":1},{\"Id\":202,\"BlockStatus\":1},{\"Id\":205,\"BlockStatus\":1},{\"Id\":203,\"BlockStatus\":1},{\"Id\":204,\"BlockStatus\":1},{\"Id\":206,\"BlockStatus\":1},{\"Id\":207,\"BlockStatus\":0},{\"Id\":208,\"BlockStatus\":0},{\"Id\":209,\"BlockStatus\":1},{\"Id\":210,\"BlockStatus\":0},{\"Id\":211,\"BlockStatus\":0}],\"SectionMembers\":[{\"Guid\":66,\"Type\":1,\"BlockId\":209,\"PosId\":37,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":{\"Id\":102,\"CurHp\":7,\"ExtraAttack\":0}},{\"Guid\":68,\"Type\":3,\"BlockId\":204,\"PosId\":33,\"CharacterId\":3203,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":69,\"Type\":3,\"BlockId\":206,\"PosId\":34,\"CharacterId\":3202,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":70,\"Type\":2,\"BlockId\":207,\"PosId\":35,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":212,\"CurHp\":16,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":71,\"Type\":2,\"BlockId\":208,\"PosId\":36,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":211,\"CurHp\":7,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":73,\"Type\":4,\"BlockId\":209,\"PosId\":38,\"CharacterId\":0,\"StageId\":10380108,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":76,\"Type\":2,\"BlockId\":210,\"PosId\":41,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":210,\"CurHp\":10,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":77,\"Type\":2,\"BlockId\":211,\"PosId\":42,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":213,\"CurHp\":19,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":84,\"Type\":4,\"BlockId\":209,\"PosId\":39,\"CharacterId\":0,\"StageId\":10380109,\"EnemyInfo\":null,\"ArmyInfo\":null}],\"DocList\":[{\"Id\":10,\"Used\":true}],\"CharacterMoveIds\":[],\"SectionStatus\":0}",
            ["UseDoc:2:11"] = "{\"SectionId\":2,\"BlockInfos\":[{\"Id\":201,\"BlockStatus\":1},{\"Id\":202,\"BlockStatus\":1},{\"Id\":205,\"BlockStatus\":1},{\"Id\":203,\"BlockStatus\":1},{\"Id\":204,\"BlockStatus\":1},{\"Id\":206,\"BlockStatus\":1},{\"Id\":207,\"BlockStatus\":0},{\"Id\":208,\"BlockStatus\":0},{\"Id\":209,\"BlockStatus\":1},{\"Id\":210,\"BlockStatus\":0},{\"Id\":211,\"BlockStatus\":0}],\"SectionMembers\":[{\"Guid\":66,\"Type\":1,\"BlockId\":209,\"PosId\":37,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":{\"Id\":102,\"CurHp\":7,\"ExtraAttack\":0}},{\"Guid\":68,\"Type\":3,\"BlockId\":204,\"PosId\":33,\"CharacterId\":3203,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":69,\"Type\":3,\"BlockId\":206,\"PosId\":34,\"CharacterId\":3202,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":70,\"Type\":2,\"BlockId\":207,\"PosId\":35,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":212,\"CurHp\":16,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":71,\"Type\":2,\"BlockId\":208,\"PosId\":36,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":211,\"CurHp\":7,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":73,\"Type\":4,\"BlockId\":209,\"PosId\":38,\"CharacterId\":0,\"StageId\":10380108,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":76,\"Type\":2,\"BlockId\":210,\"PosId\":41,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":210,\"CurHp\":10,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":77,\"Type\":2,\"BlockId\":211,\"PosId\":42,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":213,\"CurHp\":19,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":84,\"Type\":4,\"BlockId\":209,\"PosId\":39,\"CharacterId\":0,\"StageId\":10380109,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":85,\"Type\":4,\"BlockId\":209,\"PosId\":40,\"CharacterId\":0,\"StageId\":10380110,\"EnemyInfo\":null,\"ArmyInfo\":null}],\"DocList\":[{\"Id\":10,\"Used\":true},{\"Id\":11,\"Used\":true}],\"CharacterMoveIds\":[],\"SectionStatus\":0}",
            ["UseDoc:2:30"] = "{\"SectionId\":2,\"BlockInfos\":[{\"Id\":201,\"BlockStatus\":1},{\"Id\":202,\"BlockStatus\":1},{\"Id\":205,\"BlockStatus\":1},{\"Id\":203,\"BlockStatus\":1},{\"Id\":204,\"BlockStatus\":1},{\"Id\":206,\"BlockStatus\":1},{\"Id\":207,\"BlockStatus\":1},{\"Id\":208,\"BlockStatus\":1},{\"Id\":209,\"BlockStatus\":1},{\"Id\":210,\"BlockStatus\":1},{\"Id\":211,\"BlockStatus\":0}],\"SectionMembers\":[{\"Guid\":66,\"Type\":1,\"BlockId\":207,\"PosId\":35,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":{\"Id\":102,\"CurHp\":7,\"ExtraAttack\":5}},{\"Guid\":68,\"Type\":3,\"BlockId\":204,\"PosId\":33,\"CharacterId\":3203,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":69,\"Type\":3,\"BlockId\":206,\"PosId\":34,\"CharacterId\":3202,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":73,\"Type\":4,\"BlockId\":209,\"PosId\":38,\"CharacterId\":0,\"StageId\":10380108,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":77,\"Type\":2,\"BlockId\":211,\"PosId\":42,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":213,\"CurHp\":19,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":84,\"Type\":4,\"BlockId\":209,\"PosId\":39,\"CharacterId\":0,\"StageId\":10380109,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":85,\"Type\":4,\"BlockId\":209,\"PosId\":40,\"CharacterId\":0,\"StageId\":10380110,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":86,\"Type\":4,\"BlockId\":211,\"PosId\":47,\"CharacterId\":0,\"StageId\":10380111,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":87,\"Type\":3,\"BlockId\":211,\"PosId\":44,\"CharacterId\":3201,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":88,\"Type\":1,\"BlockId\":210,\"PosId\":41,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":{\"Id\":103,\"CurHp\":10,\"ExtraAttack\":0}}],\"DocList\":[{\"Id\":10,\"Used\":true},{\"Id\":11,\"Used\":true},{\"Id\":33,\"Used\":true},{\"Id\":30,\"Used\":true}],\"CharacterMoveIds\":[],\"SectionStatus\":0}",
            ["UseDoc:2:33"] = "{\"SectionId\":2,\"BlockInfos\":[{\"Id\":201,\"BlockStatus\":1},{\"Id\":202,\"BlockStatus\":1},{\"Id\":205,\"BlockStatus\":1},{\"Id\":203,\"BlockStatus\":1},{\"Id\":204,\"BlockStatus\":1},{\"Id\":206,\"BlockStatus\":1},{\"Id\":207,\"BlockStatus\":0},{\"Id\":208,\"BlockStatus\":0},{\"Id\":209,\"BlockStatus\":1},{\"Id\":210,\"BlockStatus\":0},{\"Id\":211,\"BlockStatus\":0}],\"SectionMembers\":[{\"Guid\":66,\"Type\":1,\"BlockId\":209,\"PosId\":37,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":{\"Id\":102,\"CurHp\":7,\"ExtraAttack\":0}},{\"Guid\":68,\"Type\":3,\"BlockId\":204,\"PosId\":33,\"CharacterId\":3203,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":69,\"Type\":3,\"BlockId\":206,\"PosId\":34,\"CharacterId\":3202,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":70,\"Type\":2,\"BlockId\":207,\"PosId\":35,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":212,\"CurHp\":16,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":71,\"Type\":2,\"BlockId\":208,\"PosId\":36,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":211,\"CurHp\":7,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":73,\"Type\":4,\"BlockId\":209,\"PosId\":38,\"CharacterId\":0,\"StageId\":10380108,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":76,\"Type\":2,\"BlockId\":210,\"PosId\":41,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":210,\"CurHp\":10,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":77,\"Type\":2,\"BlockId\":211,\"PosId\":42,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":213,\"CurHp\":19,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":84,\"Type\":4,\"BlockId\":209,\"PosId\":39,\"CharacterId\":0,\"StageId\":10380109,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":85,\"Type\":4,\"BlockId\":209,\"PosId\":40,\"CharacterId\":0,\"StageId\":10380110,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":86,\"Type\":4,\"BlockId\":211,\"PosId\":47,\"CharacterId\":0,\"StageId\":10380111,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":87,\"Type\":3,\"BlockId\":211,\"PosId\":44,\"CharacterId\":3201,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":88,\"Type\":1,\"BlockId\":203,\"PosId\":31,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":{\"Id\":103,\"CurHp\":20,\"ExtraAttack\":0}}],\"DocList\":[{\"Id\":10,\"Used\":true},{\"Id\":11,\"Used\":true},{\"Id\":33,\"Used\":true}],\"CharacterMoveIds\":[],\"SectionStatus\":0}"
        };

        private static readonly int[] CapturedStageProgressOrder =
        [
            17013901,
            10380102,
            10380103,
            10380104,
            17013902,
            10380106,
            10380107,
            10380108,
            10380109,
            10380110
        ];

        private static readonly IReadOnlyDictionary<int, string> CapturedSectionInfoJsonByStage = new Dictionary<int, string>
        {
            [10380102] = "{\"SectionId\":1,\"BlockInfos\":[{\"Id\":101,\"BlockStatus\":1},{\"Id\":102,\"BlockStatus\":1},{\"Id\":103,\"BlockStatus\":1},{\"Id\":104,\"BlockStatus\":1},{\"Id\":105,\"BlockStatus\":0},{\"Id\":106,\"BlockStatus\":0},{\"Id\":107,\"BlockStatus\":0},{\"Id\":108,\"BlockStatus\":0},{\"Id\":109,\"BlockStatus\":0},{\"Id\":110,\"BlockStatus\":0}],\"SectionMembers\":[{\"Guid\":1,\"Type\":1,\"BlockId\":104,\"PosId\":4,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":{\"Id\":101,\"CurHp\":8,\"ExtraAttack\":0}},{\"Guid\":9,\"Type\":4,\"BlockId\":104,\"PosId\":9,\"CharacterId\":0,\"StageId\":10380101,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":13,\"Type\":2,\"BlockId\":105,\"PosId\":13,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":204,\"CurHp\":9,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":14,\"Type\":2,\"BlockId\":106,\"PosId\":14,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":205,\"CurHp\":3,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":15,\"Type\":2,\"BlockId\":107,\"PosId\":15,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":206,\"CurHp\":7,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":16,\"Type\":2,\"BlockId\":108,\"PosId\":16,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":207,\"CurHp\":16,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":17,\"Type\":2,\"BlockId\":109,\"PosId\":17,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":208,\"CurHp\":17,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":26,\"Type\":2,\"BlockId\":107,\"PosId\":26,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":239,\"CurHp\":99,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":27,\"Type\":2,\"BlockId\":110,\"PosId\":27,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":240,\"CurHp\":99,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":30,\"Type\":4,\"BlockId\":104,\"PosId\":10,\"CharacterId\":0,\"StageId\":10380102,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":31,\"Type\":4,\"BlockId\":104,\"PosId\":11,\"CharacterId\":0,\"StageId\":10380103,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":34,\"Type\":3,\"BlockId\":104,\"PosId\":7,\"CharacterId\":3110,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":35,\"Type\":3,\"BlockId\":104,\"PosId\":8,\"CharacterId\":3108,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null}],\"DocList\":[{\"Id\":1,\"Used\":true},{\"Id\":2,\"Used\":false},{\"Id\":18,\"Used\":false}],\"CharacterMoveIds\":[],\"SectionStatus\":0}",
            [10380103] = "{\"SectionId\":1,\"BlockInfos\":[{\"Id\":101,\"BlockStatus\":1},{\"Id\":102,\"BlockStatus\":1},{\"Id\":103,\"BlockStatus\":1},{\"Id\":104,\"BlockStatus\":1},{\"Id\":105,\"BlockStatus\":0},{\"Id\":106,\"BlockStatus\":0},{\"Id\":107,\"BlockStatus\":0},{\"Id\":108,\"BlockStatus\":0},{\"Id\":109,\"BlockStatus\":0},{\"Id\":110,\"BlockStatus\":0}],\"SectionMembers\":[{\"Guid\":1,\"Type\":1,\"BlockId\":104,\"PosId\":4,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":{\"Id\":101,\"CurHp\":8,\"ExtraAttack\":0}},{\"Guid\":9,\"Type\":4,\"BlockId\":104,\"PosId\":9,\"CharacterId\":0,\"StageId\":10380101,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":13,\"Type\":2,\"BlockId\":105,\"PosId\":13,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":204,\"CurHp\":9,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":14,\"Type\":2,\"BlockId\":106,\"PosId\":14,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":205,\"CurHp\":3,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":15,\"Type\":2,\"BlockId\":107,\"PosId\":15,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":206,\"CurHp\":7,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":16,\"Type\":2,\"BlockId\":108,\"PosId\":16,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":207,\"CurHp\":16,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":17,\"Type\":2,\"BlockId\":109,\"PosId\":17,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":208,\"CurHp\":17,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":26,\"Type\":2,\"BlockId\":107,\"PosId\":26,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":239,\"CurHp\":99,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":27,\"Type\":2,\"BlockId\":110,\"PosId\":27,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":240,\"CurHp\":99,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":30,\"Type\":4,\"BlockId\":104,\"PosId\":10,\"CharacterId\":0,\"StageId\":10380102,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":31,\"Type\":4,\"BlockId\":104,\"PosId\":11,\"CharacterId\":0,\"StageId\":10380103,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":34,\"Type\":3,\"BlockId\":104,\"PosId\":7,\"CharacterId\":3110,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":35,\"Type\":3,\"BlockId\":104,\"PosId\":8,\"CharacterId\":3108,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":36,\"Type\":4,\"BlockId\":104,\"PosId\":12,\"CharacterId\":0,\"StageId\":10380104,\"EnemyInfo\":null,\"ArmyInfo\":null}],\"DocList\":[{\"Id\":1,\"Used\":true},{\"Id\":2,\"Used\":true},{\"Id\":18,\"Used\":true},{\"Id\":3,\"Used\":false},{\"Id\":4,\"Used\":false},{\"Id\":20,\"Used\":false}],\"CharacterMoveIds\":[],\"SectionStatus\":0}",
            [10380104] = "{\"SectionId\":1,\"BlockInfos\":[{\"Id\":101,\"BlockStatus\":1},{\"Id\":102,\"BlockStatus\":1},{\"Id\":103,\"BlockStatus\":1},{\"Id\":104,\"BlockStatus\":1},{\"Id\":105,\"BlockStatus\":0},{\"Id\":106,\"BlockStatus\":0},{\"Id\":107,\"BlockStatus\":0},{\"Id\":108,\"BlockStatus\":0},{\"Id\":109,\"BlockStatus\":0},{\"Id\":110,\"BlockStatus\":0}],\"SectionMembers\":[{\"Guid\":1,\"Type\":1,\"BlockId\":104,\"PosId\":4,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":{\"Id\":101,\"CurHp\":8,\"ExtraAttack\":0}},{\"Guid\":9,\"Type\":4,\"BlockId\":104,\"PosId\":9,\"CharacterId\":0,\"StageId\":10380101,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":13,\"Type\":2,\"BlockId\":105,\"PosId\":13,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":204,\"CurHp\":9,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":14,\"Type\":2,\"BlockId\":106,\"PosId\":14,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":205,\"CurHp\":3,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":15,\"Type\":2,\"BlockId\":107,\"PosId\":15,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":206,\"CurHp\":7,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":16,\"Type\":2,\"BlockId\":108,\"PosId\":16,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":207,\"CurHp\":16,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":17,\"Type\":2,\"BlockId\":109,\"PosId\":17,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":208,\"CurHp\":17,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":26,\"Type\":2,\"BlockId\":107,\"PosId\":26,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":239,\"CurHp\":99,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":27,\"Type\":2,\"BlockId\":110,\"PosId\":27,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":240,\"CurHp\":99,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":30,\"Type\":4,\"BlockId\":104,\"PosId\":10,\"CharacterId\":0,\"StageId\":10380102,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":31,\"Type\":4,\"BlockId\":104,\"PosId\":11,\"CharacterId\":0,\"StageId\":10380103,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":34,\"Type\":3,\"BlockId\":104,\"PosId\":7,\"CharacterId\":3110,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":35,\"Type\":3,\"BlockId\":104,\"PosId\":8,\"CharacterId\":3108,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":36,\"Type\":4,\"BlockId\":104,\"PosId\":12,\"CharacterId\":0,\"StageId\":10380104,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":39,\"Type\":4,\"BlockId\":107,\"PosId\":20,\"CharacterId\":0,\"StageId\":10380105,\"EnemyInfo\":null,\"ArmyInfo\":null}],\"DocList\":[{\"Id\":1,\"Used\":true},{\"Id\":2,\"Used\":true},{\"Id\":18,\"Used\":true},{\"Id\":3,\"Used\":true},{\"Id\":4,\"Used\":true},{\"Id\":20,\"Used\":true},{\"Id\":5,\"Used\":false}],\"CharacterMoveIds\":[],\"SectionStatus\":0}",
            [10380106] = "{\"SectionId\":1,\"BlockInfos\":[{\"Id\":101,\"BlockStatus\":1},{\"Id\":102,\"BlockStatus\":1},{\"Id\":103,\"BlockStatus\":1},{\"Id\":104,\"BlockStatus\":1},{\"Id\":105,\"BlockStatus\":1},{\"Id\":106,\"BlockStatus\":1},{\"Id\":107,\"BlockStatus\":1},{\"Id\":108,\"BlockStatus\":1},{\"Id\":109,\"BlockStatus\":1},{\"Id\":110,\"BlockStatus\":1}],\"SectionMembers\":[{\"Guid\":1,\"Type\":1,\"BlockId\":109,\"PosId\":17,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":{\"Id\":101,\"CurHp\":7,\"ExtraAttack\":10}},{\"Guid\":9,\"Type\":4,\"BlockId\":104,\"PosId\":9,\"CharacterId\":0,\"StageId\":10380101,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":30,\"Type\":4,\"BlockId\":104,\"PosId\":10,\"CharacterId\":0,\"StageId\":10380102,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":31,\"Type\":4,\"BlockId\":104,\"PosId\":11,\"CharacterId\":0,\"StageId\":10380103,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":34,\"Type\":3,\"BlockId\":104,\"PosId\":7,\"CharacterId\":3110,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":35,\"Type\":3,\"BlockId\":104,\"PosId\":8,\"CharacterId\":3108,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":36,\"Type\":4,\"BlockId\":104,\"PosId\":12,\"CharacterId\":0,\"StageId\":10380104,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":39,\"Type\":4,\"BlockId\":107,\"PosId\":20,\"CharacterId\":0,\"StageId\":10380105,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":48,\"Type\":3,\"BlockId\":107,\"PosId\":18,\"CharacterId\":3111,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":49,\"Type\":3,\"BlockId\":107,\"PosId\":19,\"CharacterId\":3104,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":50,\"Type\":4,\"BlockId\":110,\"PosId\":24,\"CharacterId\":0,\"StageId\":10380106,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":57,\"Type\":3,\"BlockId\":110,\"PosId\":21,\"CharacterId\":3105,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":58,\"Type\":3,\"BlockId\":110,\"PosId\":22,\"CharacterId\":3106,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":59,\"Type\":3,\"BlockId\":110,\"PosId\":23,\"CharacterId\":3103,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":60,\"Type\":4,\"BlockId\":110,\"PosId\":25,\"CharacterId\":0,\"StageId\":10380107,\"EnemyInfo\":null,\"ArmyInfo\":null}],\"DocList\":[{\"Id\":1,\"Used\":true},{\"Id\":2,\"Used\":true},{\"Id\":18,\"Used\":true},{\"Id\":3,\"Used\":true},{\"Id\":4,\"Used\":true},{\"Id\":20,\"Used\":true},{\"Id\":5,\"Used\":true},{\"Id\":42,\"Used\":true},{\"Id\":6,\"Used\":false},{\"Id\":7,\"Used\":false},{\"Id\":8,\"Used\":false}],\"CharacterMoveIds\":[],\"SectionStatus\":0}",
            [10380107] = "{\"SectionId\":1,\"BlockInfos\":[{\"Id\":101,\"BlockStatus\":1},{\"Id\":102,\"BlockStatus\":1},{\"Id\":103,\"BlockStatus\":1},{\"Id\":104,\"BlockStatus\":1},{\"Id\":105,\"BlockStatus\":1},{\"Id\":106,\"BlockStatus\":1},{\"Id\":107,\"BlockStatus\":1},{\"Id\":108,\"BlockStatus\":1},{\"Id\":109,\"BlockStatus\":1},{\"Id\":110,\"BlockStatus\":1}],\"SectionMembers\":[{\"Guid\":1,\"Type\":1,\"BlockId\":109,\"PosId\":17,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":{\"Id\":101,\"CurHp\":7,\"ExtraAttack\":10}},{\"Guid\":9,\"Type\":4,\"BlockId\":104,\"PosId\":9,\"CharacterId\":0,\"StageId\":10380101,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":30,\"Type\":4,\"BlockId\":104,\"PosId\":10,\"CharacterId\":0,\"StageId\":10380102,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":31,\"Type\":4,\"BlockId\":104,\"PosId\":11,\"CharacterId\":0,\"StageId\":10380103,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":34,\"Type\":3,\"BlockId\":104,\"PosId\":7,\"CharacterId\":3110,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":35,\"Type\":3,\"BlockId\":104,\"PosId\":8,\"CharacterId\":3108,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":36,\"Type\":4,\"BlockId\":104,\"PosId\":12,\"CharacterId\":0,\"StageId\":10380104,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":39,\"Type\":4,\"BlockId\":107,\"PosId\":20,\"CharacterId\":0,\"StageId\":10380105,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":48,\"Type\":3,\"BlockId\":107,\"PosId\":18,\"CharacterId\":3111,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":49,\"Type\":3,\"BlockId\":107,\"PosId\":19,\"CharacterId\":3104,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":50,\"Type\":4,\"BlockId\":110,\"PosId\":24,\"CharacterId\":0,\"StageId\":10380106,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":57,\"Type\":3,\"BlockId\":110,\"PosId\":21,\"CharacterId\":3105,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":58,\"Type\":3,\"BlockId\":110,\"PosId\":22,\"CharacterId\":3106,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":59,\"Type\":3,\"BlockId\":110,\"PosId\":23,\"CharacterId\":3103,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":60,\"Type\":4,\"BlockId\":110,\"PosId\":25,\"CharacterId\":0,\"StageId\":10380107,\"EnemyInfo\":null,\"ArmyInfo\":null}],\"DocList\":[{\"Id\":1,\"Used\":true},{\"Id\":2,\"Used\":true},{\"Id\":18,\"Used\":true},{\"Id\":3,\"Used\":true},{\"Id\":4,\"Used\":true},{\"Id\":20,\"Used\":true},{\"Id\":5,\"Used\":true},{\"Id\":42,\"Used\":true},{\"Id\":6,\"Used\":true},{\"Id\":7,\"Used\":true},{\"Id\":8,\"Used\":true},{\"Id\":9,\"Used\":false}],\"CharacterMoveIds\":[],\"SectionStatus\":1}",
            [10380108] = "{\"SectionId\":2,\"BlockInfos\":[{\"Id\":201,\"BlockStatus\":1},{\"Id\":202,\"BlockStatus\":1},{\"Id\":205,\"BlockStatus\":1},{\"Id\":203,\"BlockStatus\":1},{\"Id\":204,\"BlockStatus\":1},{\"Id\":206,\"BlockStatus\":1},{\"Id\":207,\"BlockStatus\":0},{\"Id\":208,\"BlockStatus\":0},{\"Id\":209,\"BlockStatus\":1},{\"Id\":210,\"BlockStatus\":0},{\"Id\":211,\"BlockStatus\":0}],\"SectionMembers\":[{\"Guid\":66,\"Type\":1,\"BlockId\":209,\"PosId\":37,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":{\"Id\":102,\"CurHp\":7,\"ExtraAttack\":0}},{\"Guid\":68,\"Type\":3,\"BlockId\":204,\"PosId\":33,\"CharacterId\":3203,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":69,\"Type\":3,\"BlockId\":206,\"PosId\":34,\"CharacterId\":3202,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":70,\"Type\":2,\"BlockId\":207,\"PosId\":35,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":212,\"CurHp\":16,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":71,\"Type\":2,\"BlockId\":208,\"PosId\":36,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":211,\"CurHp\":7,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":73,\"Type\":4,\"BlockId\":209,\"PosId\":38,\"CharacterId\":0,\"StageId\":10380108,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":76,\"Type\":2,\"BlockId\":210,\"PosId\":41,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":210,\"CurHp\":10,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":77,\"Type\":2,\"BlockId\":211,\"PosId\":42,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":213,\"CurHp\":19,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":84,\"Type\":4,\"BlockId\":209,\"PosId\":39,\"CharacterId\":0,\"StageId\":10380109,\"EnemyInfo\":null,\"ArmyInfo\":null}],\"DocList\":[{\"Id\":10,\"Used\":false}],\"CharacterMoveIds\":[],\"SectionStatus\":0}",
            [10380109] = "{\"SectionId\":2,\"BlockInfos\":[{\"Id\":201,\"BlockStatus\":1},{\"Id\":202,\"BlockStatus\":1},{\"Id\":205,\"BlockStatus\":1},{\"Id\":203,\"BlockStatus\":1},{\"Id\":204,\"BlockStatus\":1},{\"Id\":206,\"BlockStatus\":1},{\"Id\":207,\"BlockStatus\":0},{\"Id\":208,\"BlockStatus\":0},{\"Id\":209,\"BlockStatus\":1},{\"Id\":210,\"BlockStatus\":0},{\"Id\":211,\"BlockStatus\":0}],\"SectionMembers\":[{\"Guid\":66,\"Type\":1,\"BlockId\":209,\"PosId\":37,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":{\"Id\":102,\"CurHp\":7,\"ExtraAttack\":0}},{\"Guid\":68,\"Type\":3,\"BlockId\":204,\"PosId\":33,\"CharacterId\":3203,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":69,\"Type\":3,\"BlockId\":206,\"PosId\":34,\"CharacterId\":3202,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":70,\"Type\":2,\"BlockId\":207,\"PosId\":35,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":212,\"CurHp\":16,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":71,\"Type\":2,\"BlockId\":208,\"PosId\":36,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":211,\"CurHp\":7,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":73,\"Type\":4,\"BlockId\":209,\"PosId\":38,\"CharacterId\":0,\"StageId\":10380108,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":76,\"Type\":2,\"BlockId\":210,\"PosId\":41,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":210,\"CurHp\":10,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":77,\"Type\":2,\"BlockId\":211,\"PosId\":42,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":213,\"CurHp\":19,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":84,\"Type\":4,\"BlockId\":209,\"PosId\":39,\"CharacterId\":0,\"StageId\":10380109,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":85,\"Type\":4,\"BlockId\":209,\"PosId\":40,\"CharacterId\":0,\"StageId\":10380110,\"EnemyInfo\":null,\"ArmyInfo\":null}],\"DocList\":[{\"Id\":10,\"Used\":true},{\"Id\":11,\"Used\":false}],\"CharacterMoveIds\":[],\"SectionStatus\":0}",
            [10380110] = "{\"SectionId\":2,\"BlockInfos\":[{\"Id\":201,\"BlockStatus\":1},{\"Id\":202,\"BlockStatus\":1},{\"Id\":205,\"BlockStatus\":1},{\"Id\":203,\"BlockStatus\":1},{\"Id\":204,\"BlockStatus\":1},{\"Id\":206,\"BlockStatus\":1},{\"Id\":207,\"BlockStatus\":0},{\"Id\":208,\"BlockStatus\":0},{\"Id\":209,\"BlockStatus\":1},{\"Id\":210,\"BlockStatus\":0},{\"Id\":211,\"BlockStatus\":0}],\"SectionMembers\":[{\"Guid\":66,\"Type\":1,\"BlockId\":209,\"PosId\":37,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":{\"Id\":102,\"CurHp\":7,\"ExtraAttack\":0}},{\"Guid\":68,\"Type\":3,\"BlockId\":204,\"PosId\":33,\"CharacterId\":3203,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":69,\"Type\":3,\"BlockId\":206,\"PosId\":34,\"CharacterId\":3202,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":70,\"Type\":2,\"BlockId\":207,\"PosId\":35,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":212,\"CurHp\":16,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":71,\"Type\":2,\"BlockId\":208,\"PosId\":36,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":211,\"CurHp\":7,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":73,\"Type\":4,\"BlockId\":209,\"PosId\":38,\"CharacterId\":0,\"StageId\":10380108,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":76,\"Type\":2,\"BlockId\":210,\"PosId\":41,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":210,\"CurHp\":10,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":77,\"Type\":2,\"BlockId\":211,\"PosId\":42,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":213,\"CurHp\":19,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":84,\"Type\":4,\"BlockId\":209,\"PosId\":39,\"CharacterId\":0,\"StageId\":10380109,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":85,\"Type\":4,\"BlockId\":209,\"PosId\":40,\"CharacterId\":0,\"StageId\":10380110,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":86,\"Type\":4,\"BlockId\":211,\"PosId\":47,\"CharacterId\":0,\"StageId\":10380111,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":87,\"Type\":3,\"BlockId\":211,\"PosId\":44,\"CharacterId\":3201,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null}],\"DocList\":[{\"Id\":10,\"Used\":true},{\"Id\":11,\"Used\":true},{\"Id\":33,\"Used\":false}],\"CharacterMoveIds\":[],\"SectionStatus\":0}",
            [17013901] = "{\"SectionId\":1,\"BlockInfos\":[{\"Id\":101,\"BlockStatus\":1},{\"Id\":102,\"BlockStatus\":1},{\"Id\":103,\"BlockStatus\":1},{\"Id\":104,\"BlockStatus\":1},{\"Id\":105,\"BlockStatus\":0},{\"Id\":106,\"BlockStatus\":0},{\"Id\":107,\"BlockStatus\":0},{\"Id\":108,\"BlockStatus\":0},{\"Id\":109,\"BlockStatus\":0},{\"Id\":110,\"BlockStatus\":0}],\"SectionMembers\":[{\"Guid\":1,\"Type\":1,\"BlockId\":104,\"PosId\":4,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":{\"Id\":101,\"CurHp\":8,\"ExtraAttack\":0}},{\"Guid\":9,\"Type\":4,\"BlockId\":104,\"PosId\":9,\"CharacterId\":0,\"StageId\":10380101,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":13,\"Type\":2,\"BlockId\":105,\"PosId\":13,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":204,\"CurHp\":9,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":14,\"Type\":2,\"BlockId\":106,\"PosId\":14,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":205,\"CurHp\":3,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":15,\"Type\":2,\"BlockId\":107,\"PosId\":15,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":206,\"CurHp\":7,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":16,\"Type\":2,\"BlockId\":108,\"PosId\":16,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":207,\"CurHp\":16,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":17,\"Type\":2,\"BlockId\":109,\"PosId\":17,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":208,\"CurHp\":17,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":26,\"Type\":2,\"BlockId\":107,\"PosId\":26,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":239,\"CurHp\":99,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":27,\"Type\":2,\"BlockId\":110,\"PosId\":27,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":240,\"CurHp\":99,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":30,\"Type\":4,\"BlockId\":104,\"PosId\":10,\"CharacterId\":0,\"StageId\":10380102,\"EnemyInfo\":null,\"ArmyInfo\":null}],\"DocList\":[{\"Id\":1,\"Used\":false}],\"CharacterMoveIds\":[],\"SectionStatus\":0}",
            [17013902] = "{\"SectionId\":1,\"BlockInfos\":[{\"Id\":101,\"BlockStatus\":1},{\"Id\":102,\"BlockStatus\":1},{\"Id\":103,\"BlockStatus\":1},{\"Id\":104,\"BlockStatus\":1},{\"Id\":105,\"BlockStatus\":1},{\"Id\":106,\"BlockStatus\":1},{\"Id\":107,\"BlockStatus\":1},{\"Id\":108,\"BlockStatus\":0},{\"Id\":109,\"BlockStatus\":0},{\"Id\":110,\"BlockStatus\":0}],\"SectionMembers\":[{\"Guid\":1,\"Type\":1,\"BlockId\":107,\"PosId\":15,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":{\"Id\":101,\"CurHp\":8,\"ExtraAttack\":0}},{\"Guid\":9,\"Type\":4,\"BlockId\":104,\"PosId\":9,\"CharacterId\":0,\"StageId\":10380101,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":16,\"Type\":2,\"BlockId\":108,\"PosId\":16,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":207,\"CurHp\":16,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":17,\"Type\":2,\"BlockId\":109,\"PosId\":17,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":208,\"CurHp\":17,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":27,\"Type\":2,\"BlockId\":110,\"PosId\":27,\"CharacterId\":0,\"StageId\":0,\"EnemyInfo\":{\"Id\":240,\"CurHp\":99,\"ExtraAttack\":0},\"ArmyInfo\":null},{\"Guid\":30,\"Type\":4,\"BlockId\":104,\"PosId\":10,\"CharacterId\":0,\"StageId\":10380102,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":31,\"Type\":4,\"BlockId\":104,\"PosId\":11,\"CharacterId\":0,\"StageId\":10380103,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":34,\"Type\":3,\"BlockId\":104,\"PosId\":7,\"CharacterId\":3110,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":35,\"Type\":3,\"BlockId\":104,\"PosId\":8,\"CharacterId\":3108,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":36,\"Type\":4,\"BlockId\":104,\"PosId\":12,\"CharacterId\":0,\"StageId\":10380104,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":39,\"Type\":4,\"BlockId\":107,\"PosId\":20,\"CharacterId\":0,\"StageId\":10380105,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":48,\"Type\":3,\"BlockId\":107,\"PosId\":18,\"CharacterId\":3111,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":49,\"Type\":3,\"BlockId\":107,\"PosId\":19,\"CharacterId\":3104,\"StageId\":0,\"EnemyInfo\":null,\"ArmyInfo\":null},{\"Guid\":50,\"Type\":4,\"BlockId\":110,\"PosId\":24,\"CharacterId\":0,\"StageId\":10380106,\"EnemyInfo\":null,\"ArmyInfo\":null}],\"DocList\":[{\"Id\":1,\"Used\":true},{\"Id\":2,\"Used\":true},{\"Id\":18,\"Used\":true},{\"Id\":3,\"Used\":true},{\"Id\":4,\"Used\":true},{\"Id\":20,\"Used\":true},{\"Id\":5,\"Used\":true},{\"Id\":42,\"Used\":false}],\"CharacterMoveIds\":[],\"SectionStatus\":0}"
        };

        public static FubenMainLineLuosaitaData BuildLoginData(Stage? stage = null)
        {
            FubenMainLineLuosaitaData loginData = JsonConvert.DeserializeObject<FubenMainLineLuosaitaData>(CapturedLoginDataJson)
                ?? new FubenMainLineLuosaitaData();
            loginData.SectionInfos = BuildSectionInfosForStageProgress(stage, loginData.SectionInfos);
            return loginData;
        }

        public static MainLineLuosaitaSectionInfo BuildCapturedSectionInfo(Stage? stage = null)
        {
            FubenMainLineLuosaitaData loginData = BuildLoginData(stage);
            return loginData.SectionInfos.FirstOrDefault(sectionInfo => sectionInfo.SectionId == 1)
                ?? new MainLineLuosaitaSectionInfo { SectionId = 1 };
        }

        public static MainLineLuosaitaSectionInfo BuildEnterSectionInfo(int sectionId, Stage? stage = null)
        {
            if (TryBuildStageProgressSectionInfo(stage, sectionId, out MainLineLuosaitaSectionInfo sectionInfo))
                return sectionInfo;

            return BuildCapturedSectionInfo($"Enter:{sectionId}", sectionId, stage);
        }

        public static MainLineLuosaitaSectionInfo BuildMoveSectionInfo(int sectionId, int posId, int targetPosId, Stage? stage = null)
        {
            return BuildCapturedSectionInfo($"Move:{sectionId}:{posId}:{targetPosId}", sectionId, stage);
        }

        public static MainLineLuosaitaSectionInfo BuildUseDocSectionInfo(int sectionId, int docId, Stage? stage = null)
        {
            return BuildCapturedSectionInfo($"UseDoc:{sectionId}:{docId}", sectionId, stage);
        }

        public static bool HasCapturedStageProgress(int stageId)
        {
            return CapturedSectionInfoJsonByStage.ContainsKey(stageId);
        }

        public static bool TryBuildStageProgressSectionInfo(int stageId, out MainLineLuosaitaSectionInfo sectionInfo)
        {
            if (CapturedSectionInfoJsonByStage.TryGetValue(stageId, out string? sectionJson))
            {
                sectionInfo = DeserializeSectionInfo(sectionJson);
                return true;
            }

            sectionInfo = new MainLineLuosaitaSectionInfo();
            return false;
        }

        private static MainLineLuosaitaSectionInfo BuildCapturedSectionInfo(string key, int sectionId, Stage? stage)
        {
            if (CapturedSectionInfoJsonByRequest.TryGetValue(key, out string? sectionJson))
                return DeserializeSectionInfo(sectionJson);

            if (TryBuildStageProgressSectionInfo(stage, sectionId, out MainLineLuosaitaSectionInfo sectionInfo))
                return sectionInfo;

            if (sectionId == 1)
                return BuildCapturedSectionInfo();

            if (CapturedSectionInfoJsonByRequest.TryGetValue($"Enter:{sectionId}", out string? enterSectionJson))
                return DeserializeSectionInfo(enterSectionJson);

            return BuildCapturedSectionInfo();
        }

        private static List<MainLineLuosaitaSectionInfo> BuildSectionInfosForStageProgress(
            Stage? stage,
            IEnumerable<MainLineLuosaitaSectionInfo> baseSectionInfos)
        {
            Dictionary<int, MainLineLuosaitaSectionInfo> sectionInfosById = baseSectionInfos
                .ToDictionary(sectionInfo => sectionInfo.SectionId);

            foreach (int stageId in CapturedStageProgressOrder)
            {
                if (!IsStagePassed(stage, stageId) || !CapturedSectionInfoJsonByStage.TryGetValue(stageId, out string? sectionJson))
                    continue;

                MainLineLuosaitaSectionInfo progressedSectionInfo = DeserializeSectionInfo(sectionJson);
                sectionInfosById[progressedSectionInfo.SectionId] = progressedSectionInfo;
            }

            return sectionInfosById.Values
                .OrderBy(sectionInfo => sectionInfo.SectionId)
                .ToList();
        }

        private static bool TryBuildStageProgressSectionInfo(Stage? stage, int sectionId, out MainLineLuosaitaSectionInfo sectionInfo)
        {
            sectionInfo = new MainLineLuosaitaSectionInfo();
            bool found = false;

            foreach (int stageId in CapturedStageProgressOrder)
            {
                if (!IsStagePassed(stage, stageId) || !CapturedSectionInfoJsonByStage.TryGetValue(stageId, out string? sectionJson))
                    continue;

                MainLineLuosaitaSectionInfo progressedSectionInfo = DeserializeSectionInfo(sectionJson);
                if (progressedSectionInfo.SectionId != sectionId)
                    continue;

                sectionInfo = progressedSectionInfo;
                found = true;
            }

            return found;
        }

        private static bool IsStagePassed(Stage? stage, int stageId)
        {
            return stage?.Stages is not null
                && stage.Stages.TryGetValue(stageId, out StageDatum? stageDatum)
                && stageDatum.Passed;
        }

        private static MainLineLuosaitaSectionInfo DeserializeSectionInfo(string json)
        {
            return JsonConvert.DeserializeObject<MainLineLuosaitaSectionInfo>(json)
                ?? new MainLineLuosaitaSectionInfo();
        }
    }
}
